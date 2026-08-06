using System.Threading.Channels;

namespace ContextCore.Core.Services.Learning.V14_0;

/// <summary>
/// IRuntimeCandidateTraceSink 的有界 Channel 装饰器。
/// 将单条 <see cref="Write"/> 入队到有界通道，由后台消费者按批聚合后逐行写入 inner sink，
/// 使 File 等同步 sink 的写入与主流程解耦（主流程只做 TryWrite，绝不阻塞、绝不抛异常）。
/// </summary>
/// <remarks>
/// 语义（与 BoundedChannelContextEventSink 一致）：
/// <list type="bullet">
/// <item>通道满时 <see cref="Channel{T}.Writer.TryWrite"/> 返回 false（使用
/// <see cref="BoundedChannelFullMode.Wait"/>；不能使用 DropWrite——该模式下 TryWrite
/// 即使丢弃也返回 true，无法检测丢弃），调用方不阻塞，仅累加 <see cref="DroppedCount"/>。</item>
/// <item>后台消费者按 <c>batchSize</c> 聚合读取，随后逐行调用 inner 的
/// <see cref="IRuntimeCandidateTraceSink.Write"/>；inner 写失败不影响主流程
/// （fail-open），仅累加 <see cref="WriteFailures"/>。</item>
/// <item><see cref="PendingCount"/> 暴露通道中待处理行数（饱和检测）。</item>
/// <item><see cref="FlushAsync"/> 等待已入队行全部写完（enqueued 与 flushed 对齐）。</item>
/// <item><see cref="DisposeAsync"/>：TryComplete + Cancel + 残余循环
/// （CancellationToken.None），保证关闭时残留行全部 drain。</item>
/// </list>
/// </remarks>
public sealed class BoundedRuntimeCandidateTraceSink : IRuntimeCandidateTraceSink, IAsyncDisposable
{
    private readonly IRuntimeCandidateTraceSink _inner;
    private readonly Channel<RuntimeCandidateTraceRow> _channel;
    private readonly Task _consumerTask;
    private readonly int _batchSize;
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;
    private long _enqueuedCount;
    private long _flushedCount;
    private long _droppedCount;
    private long _writeFailures;
    private long _batchEmitCount;

    public BoundedRuntimeCandidateTraceSink(
        IRuntimeCandidateTraceSink inner,
        int capacity = 1024,
        int batchSize = 64)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

        _inner = inner;
        _batchSize = batchSize;
        _channel = Channel.CreateBounded<RuntimeCandidateTraceRow>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        _consumerTask = Task.Run(() => ConsumeAsync(_shutdownCts.Token));
    }

    public bool Enabled => _inner.Enabled;

    /// <summary>成功入队的行数（含已消费与待消费）。</summary>
    public int WriteCount => (int)Interlocked.Read(ref _enqueuedCount);

    /// <summary>inner 写入失败次数（后台消费者侧；主流程不受影响）。</summary>
    public int WriteFailures => (int)Interlocked.Read(ref _writeFailures);

    /// <summary>因通道满被丢弃的行数。</summary>
    public int DroppedCount => (int)Interlocked.Read(ref _droppedCount);

    /// <summary>已聚合执行的批量写入次数。</summary>
    public long BatchEmitCount => Interlocked.Read(ref _batchEmitCount);

    /// <summary>通道中尚未被消费者取走的行数（饱和检测）。</summary>
    public int PendingCount => _channel.Reader.Count;

    public void Write(RuntimeCandidateTraceRow row)
    {
        if (_channel.Writer.TryWrite(row))
        {
            Interlocked.Increment(ref _enqueuedCount);
            return;
        }
        Interlocked.Increment(ref _droppedCount);
    }

    /// <summary>
    /// 等待已入队行全部写入 inner（enqueued 与 flushed 对齐）后返回。
    /// 调用方必须停止写入后调用才具有"排空"语义；取消时抛出。
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        while (Interlocked.Read(ref _enqueuedCount) > Interlocked.Read(ref _flushedCount))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var batch = new List<RuntimeCandidateTraceRow>(_batchSize);

        try
        {
            while (true)
            {
                batch.Clear();

                RuntimeCandidateTraceRow first;
                try
                {
                    first = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break; // shutdown 信号：进入残留 flush
                }
                catch (ChannelClosedException)
                {
                    break; // 通道已关闭：进入残留 flush
                }

                batch.Add(first);

                // 非阻塞填充剩余批次
                while (batch.Count < _batchSize && _channel.Reader.TryRead(out var row))
                {
                    batch.Add(row);
                }

                await FlushBatchAsync(batch).ConfigureAwait(false);
            }

            // 通道关闭后，flush 残留行（不响应 shutdown 取消，保证 drain 完成）
            while (_channel.Reader.TryRead(out var row))
            {
                batch.Add(row);
                if (batch.Count >= _batchSize)
                {
                    await FlushBatchAsync(batch).ConfigureAwait(false);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch).ConfigureAwait(false);
            }
        }
        catch
        {
            // 消费者异常静默：避免 DisposeAsync 路径抛出
        }
    }

    private async Task FlushBatchAsync(List<RuntimeCandidateTraceRow> batch)
    {
        foreach (var row in batch)
        {
            try
            {
                _inner.Write(row);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _writeFailures);
                // fail-open：吞掉异常，不阻断消费者
            }
        }
        Interlocked.Add(ref _flushedCount, batch.Count);
        Interlocked.Increment(ref _batchEmitCount);
    }

    /// <summary>
    /// 优雅关闭：标记通道完成，等待消费者 flush 完所有残留行后退出。
    /// 重复调用是幂等的。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        try
        {
            _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS 已被释放（不应发生，但兜底）
        }
        try
        {
            await _consumerTask.ConfigureAwait(false);
        }
        catch
        {
            // 消费者异常静默：Dispose 路径不应抛出
        }

        _shutdownCts.Dispose();
    }
}
