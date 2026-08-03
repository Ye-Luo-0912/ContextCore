using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

/// <summary>
/// 基于 <see cref="System.Threading.Channels"/> 的进程内 Agent Run 事件推送通知器。
/// </summary>
/// <remarks>
/// <b>设计要点</b>：
/// <list type="bullet">
/// <item>Per-run 维护一组 per-subscriber <see cref="Channel{Long}"/>（bounded, capacity 256），
/// 支持多订阅者 fan-out（每个 SSE 连接独立消费自己的 sequence 流）。</item>
/// <item><see cref="Notify"/> 在事件批量提交事务 COMMIT 后由 Event Store 调用，
/// 向该 run 的所有订阅者 channel TryWrite 最新 sequence；channel 已满时丢弃（轮询兜底）。</item>
/// <item><see cref="SubscribeAsync"/> 等待最多 500ms 读取新 sequence；超时则结束迭代，
/// 让调用方回退到 <see cref="IAgentRunEventStore.ReadAsync"/> 轮询。</item>
/// <item><see cref="RegisterSubscription"/> 分离订阅注册与事件等待，
/// 让 SSE 端点先注册订阅再读 DB，消除"DB 读取与订阅注册之间事件丢失"竞态。</item>
/// <item>订阅者断开（cancellationToken 取消或 Dispose）时从 per-run 订阅表移除 channel；
/// 某 run 无订阅者时清理其订阅表条目。</item>
/// </list>
///
/// <b>仅进程内</b>：多实例部署时每个实例的 SSE 连接由本实例的 Event Store 推送；
/// 跨实例的 SSE 客户端依赖 SSE 端点的周期性 ReadAsync 轮询兜底。
/// </remarks>
public sealed class ChannelAgentRunEventNotifier : IAgentRunEventNotifier
{
    private const int ChannelCapacity = 256;

    // per-run → (subscriberId → channel)。subscriberId 用于订阅者注销时移除自身 channel。
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<long>>> _subscribers
        = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Notify(string workspaceId, string runId, long lastSequence)
    {
        var key = Key(workspaceId, runId);
        if (!_subscribers.TryGetValue(key, out var subscribers) || subscribers.IsEmpty)
        {
            return;
        }

        foreach (var pair in subscribers)
        {
            // TryWrite：channel 已满时丢弃此通知（SSE 轮询兜底会补上，不丢失事件本身）。
            pair.Value.Writer.TryWrite(lastSequence);
        }
    }

    /// <inheritdoc />
    public IAgentRunEventSubscription RegisterSubscription(string workspaceId, string runId, long fromSequence)
    {
        var key = Key(workspaceId, runId);
        var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var subscriberId = Guid.NewGuid();
        var subscribers = _subscribers.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, Channel<long>>());
        subscribers[subscriberId] = channel;

        return new ChannelSubscription(
            channel, _subscribers, subscribers, key, subscriberId, fromSequence);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<long> SubscribeAsync(
        string workspaceId,
        string runId,
        long fromSequence,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 委托到 RegisterSubscription，复用分离后的注册逻辑。
        var subscription = RegisterSubscription(workspaceId, runId, fromSequence);
        try
        {
            await foreach (var seq in subscription.WithCancellation(ct).ConfigureAwait(false))
            {
                yield return seq;
            }
        }
        finally
        {
            subscription.Dispose();
        }
    }

    private static string Key(string workspaceId, string runId)
        => $"{workspaceId}:{runId}";

    /// <summary>
    /// Channel-based 订阅句柄。注册时创建，Dispose 时注销。
    /// 枚举 IAsyncEnumerable 等待 notifier 推送的 sequence。
    /// </summary>
    private sealed class ChannelSubscription : IAgentRunEventSubscription
    {
        private readonly Channel<long> _channel;
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<long>>> _outer;
        private readonly ConcurrentDictionary<Guid, Channel<long>> _inner;
        private readonly string _key;
        private readonly Guid _subscriberId;
        private readonly long _fromSequence;
        private int _disposed; // 0 = active, 1 = disposed

        internal ChannelSubscription(
            Channel<long> channel,
            ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<long>>> outer,
            ConcurrentDictionary<Guid, Channel<long>> inner,
            string key,
            Guid subscriberId,
            long fromSequence)
        {
            _channel = channel;
            _outer = outer;
            _inner = inner;
            _key = key;
            _subscriberId = subscriberId;
            _fromSequence = fromSequence;
        }

        public IAsyncEnumerator<long> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        private async IAsyncEnumerable<long> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                // 持久订阅——不超时退出，一直等待直到客户端断开、watchdog 超时或 channel 完成。
                while (!cancellationToken.IsCancellationRequested)
                {
                    bool hasData;
                    try
                    {
                        hasData = await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        yield break;
                    }

                    if (!hasData)
                    {
                        // channel 已完成（不应发生，但防御性处理）。
                        yield break;
                    }

                    while (_channel.Reader.TryRead(out var seq))
                    {
                        if (seq >= _fromSequence)
                        {
                            yield return seq;
                        }
                    }
                }
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _inner.TryRemove(_subscriberId, out _);
            if (_inner.IsEmpty)
            {
                _outer.TryRemove(_key, out _);
            }
            _channel.Writer.TryComplete();
        }
    }
}
