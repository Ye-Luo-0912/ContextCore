using System.Threading.Channels;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Retrieval;

/// <summary>
/// 检索 trace 写队列化装饰器：SaveAsync 入队即返回（不阻塞检索热路径），
/// 后台 drain 任务串行持久化到内层 store。
/// trace 是诊断数据：队列满时直接丢弃（DropWrite，不背压热路径），
/// 后台写失败静默记录并继续（不中断 drain 循环）。
/// </summary>
public sealed class QueuedRetrievalTraceStore : IRetrievalTraceStore, IDisposable
{
    private readonly IRetrievalTraceStore _inner;
    private readonly Channel<ContextRetrievalTrace> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _drainTask;
    private int _disposed;

    /// <summary>
    /// 初始化队列化 trace store。
    /// </summary>
    /// <param name="inner">内层持久化 store（File / InMemory / Postgres）。</param>
    /// <param name="capacity">队列容量；超过后新 trace 被丢弃（诊断数据不阻塞热路径）。</param>
    public QueuedRetrievalTraceStore(IRetrievalTraceStore inner, int capacity = 512)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _channel = Channel.CreateBounded<ContextRetrievalTrace>(
            new BoundedChannelOptions(Math.Max(16, capacity))
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });
        _cts = new CancellationTokenSource();
        _drainTask = Task.Run(() => DrainAsync(_cts.Token));
    }

    /// <inheritdoc />
    public Task SaveAsync(ContextRetrievalTrace trace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        cancellationToken.ThrowIfCancellationRequested();

        // 入队即返回：持久化在后台 drain 进行，检索/构建不被 trace 写入阻塞。
        _channel.Writer.TryWrite(trace);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
        => _inner.QueryRecentAsync(workspaceId, collectionId, take, cancellationToken);

    /// <summary>后台 drain：从队列取 trace 串行写入内层 store。</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var trace))
                {
                    try
                    {
                        await _inner.SaveAsync(trace, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 后台写失败静默（诊断数据可丢弃），不中断 drain 循环。
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 先完成写入（SignalWriter）：让后台 drain 消费完队列中未落库的 trace，
        // 再取消兜底——先取消会导致 WaitToReadAsync 退出而丢弃剩余 trace。
        _channel.Writer.TryComplete();
        try
        {
            _drainTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 排空超时忽略（诊断数据）。
        }
        _cts.Cancel();
        _cts.Dispose();
    }
}
