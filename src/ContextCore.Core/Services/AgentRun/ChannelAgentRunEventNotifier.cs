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
///   <item>Per-run 维护一组 per-subscriber <see cref="Channel{Long}"/>（bounded, capacity 256），
///     支持多订阅者 fan-out（每个 SSE 连接独立消费自己的 sequence 流）。</item>
///   <item><see cref="Notify"/> 在事件批量提交事务 COMMIT 后由 Event Store 调用，
///     向该 run 的所有订阅者 channel TryWrite 最新 sequence；channel 已满时丢弃（轮询兜底）。</item>
///   <item><see cref="SubscribeAsync"/> 等待最多 500ms 读取新 sequence；超时则结束迭代，
///     让调用方回退到 <see cref="IAgentRunEventStore.ReadAsync"/> 轮询。</item>
///   <item>订阅者断开（cancellationToken 取消）时从 per-run 订阅表移除 channel；
///     某 run 无订阅者时清理其订阅表条目。</item>
/// </list>
///
/// <b>仅进程内</b>：多实例部署时每个实例的 SSE 连接由本实例的 Event Store 推送；
/// 跨实例的 SSE 客户端依赖 500ms 轮询兜底（与原行为一致）。
/// </remarks>
public sealed class ChannelAgentRunEventNotifier : IAgentRunEventNotifier
{
    private static readonly TimeSpan SubscribeTimeout = TimeSpan.FromMilliseconds(500);
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
    public async IAsyncEnumerable<long> SubscribeAsync(
        string workspaceId,
        string runId,
        long fromSequence,
        [EnumeratorCancellation] CancellationToken ct)
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

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 每轮等待最多 500ms；超时结束迭代让调用方回退轮询。
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(SubscribeTimeout);

                bool hasData;
                try
                {
                    hasData = await channel.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 500ms 超时：无新事件，结束迭代让调用方回退 ReadAsync 轮询。
                    yield break;
                }

                if (!hasData)
                {
                    // channel 已完成（不应发生，但防御性处理）。
                    yield break;
                }

                while (channel.Reader.TryRead(out var seq))
                {
                    if (seq >= fromSequence)
                    {
                        yield return seq;
                    }
                }
            }
        }
        finally
        {
            subscribers.TryRemove(subscriberId, out _);
            // 某 run 无订阅者时清理其订阅表条目，避免内存泄漏。
            if (subscribers.IsEmpty)
            {
                _subscribers.TryRemove(key, out _);
            }
        }
    }

    private static string Key(string workspaceId, string runId)
        => $"{workspaceId}:{runId}";
}
