using System.Threading.Channels;
using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// R13.4 #1：BestEffort 路径的有界 Channel 装饰器。
/// 使用 <see cref="Channel{T}"/> 作为有界缓冲区，将单条 <see cref="EmitAsync"/> 入队，
/// 由后台消费者按批调用 <see cref="IContextEventSink.EmitBatchAsync"/>，利用 File/Postgres 的批量 I/O。
/// </summary>
/// <remarks>
/// 语义：
/// <list type="bullet">
/// <item>inner.Kind == <see cref="ContextEventSinkKind.BestEffort"/>（默认）：通道满时丢弃新事件
/// （使用 <see cref="BoundedChannelFullMode.Wait"/> + <see cref="Channel{T}.Writer.TryWrite"/>，
/// 后者在通道满时返回 false 而非阻塞），不阻塞调用方；后台消费者按批写入 inner。
/// 失败的批量写入被吞掉（BestEffort fail-open），仅累加 <see cref="ErrorCount"/>。
/// 注意：不能使用 <see cref="BoundedChannelFullMode.DropWrite"/>，因为该模式下 <c>TryWrite</c>
/// 即使丢弃也返回 true，无法检测丢弃。</item>
/// <item>inner.Kind == <see cref="ContextEventSinkKind.Required"/>：不使用通道，直接同步调用
/// inner 的 <see cref="IContextEventSink.EmitAsync"/> / <see cref="IContextEventSink.EmitBatchAsync"/>，
/// 保证审计事件必须落盘成功。</item>
/// </list>
/// </remarks>
public sealed class BoundedChannelContextEventSink : IContextEventSink, IAsyncDisposable
{
    private readonly IContextEventSink _inner;
    private readonly Channel<ContextOperationEvent>? _channel;
    private readonly Task _consumerTask;
    private readonly int _batchSize;
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;
    private long _droppedCount;
    private long _errorCount;
    private long _batchEmitCount;

    /// <summary>
    /// 构造有界 Channel 装饰器。
    /// </summary>
    /// <param name="inner">被装饰的内层 sink。</param>
    /// <param name="capacity">通道容量。仅对 BestEffort inner 生效。</param>
    /// <param name="batchSize">每批写入的最大事件数。</param>
    public BoundedChannelContextEventSink(
        IContextEventSink inner,
        int capacity = 1024,
        int batchSize = 64)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

        _inner = inner;
        _batchSize = batchSize;

        if (inner.Kind == ContextEventSinkKind.BestEffort)
        {
            // 使用 Wait 模式而非 DropWrite：DropWrite 下 TryWrite 即使丢弃也返回 true，
            // 无法在 EmitAsync 中检测丢弃。Wait 模式下 TryWrite 满则返回 false（不阻塞），
            // 装饰器据此累加 DroppedCount。WriteAsync 才会阻塞，但本装饰器不使用。
            _channel = Channel.CreateBounded<ContextOperationEvent>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });
            _consumerTask = Task.Run(() => ConsumeAsync(_shutdownCts.Token));
        }
        else
        {
            // Required sink 不使用通道，_consumerTask 直接完成
            _channel = null;
            _consumerTask = Task.CompletedTask;
        }
    }

    /// <summary>装饰器继承 inner 的 Kind，保持复合接收器的 fail-open / fail-closed 判定一致。</summary>
    public ContextEventSinkKind Kind => _inner.Kind;

    /// <summary>因通道满或写入失败而丢弃的事件数（仅 BestEffort 路径）。</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>批量写入失败的次数（仅 BestEffort 路径）。</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>已成功提交的批量写入次数。</summary>
    public long BatchEmitCount => Interlocked.Read(ref _batchEmitCount);

    /// <summary>当前通道中尚未消费的事件数（仅 BestEffort 路径）。</summary>
    public int PendingCount => _channel?.Reader.Count ?? 0;

    public Task EmitAsync(
        ContextOperationEvent operationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationEvent);

        // Required sink：直接同步调用，审计必须落盘
        if (_inner.Kind == ContextEventSinkKind.Required)
        {
            return _inner.EmitAsync(operationEvent, cancellationToken);
        }

        // BestEffort sink：写入有界通道，满则丢弃
        if (_channel!.Writer.TryWrite(operationEvent))
        {
            return Task.CompletedTask;
        }

        Interlocked.Increment(ref _droppedCount);
        CoreMetrics.EventSinkDropped.Add(1);
        return Task.CompletedTask;
    }

    public async Task EmitBatchAsync(
        IReadOnlyList<ContextOperationEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        // Required sink：直接同步调用
        if (_inner.Kind == ContextEventSinkKind.Required)
        {
            await _inner.EmitBatchAsync(events, cancellationToken).ConfigureAwait(false);
            return;
        }

        // BestEffort sink：逐条写入通道（满则丢弃）
        foreach (var evt in events)
        {
            if (!_channel!.Writer.TryWrite(evt))
            {
                Interlocked.Increment(ref _droppedCount);
                CoreMetrics.EventSinkDropped.Add(1);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var batch = new List<ContextOperationEvent>(_batchSize);

        try
        {
            while (true)
            {
                batch.Clear();

                ContextOperationEvent first;
                try
                {
                    first = await _channel!.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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
                while (batch.Count < _batchSize && _channel!.Reader.TryRead(out var evt))
                {
                    batch.Add(evt);
                }

                await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }

            // 通道关闭后，flush 残留事件（不响应 shutdown 取消，保证 drain 完成）
            while (_channel!.Reader.TryRead(out var evt))
            {
                batch.Add(evt);
                if (batch.Count >= _batchSize)
                {
                    await FlushBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // 消费者异常静默：避免 Dispose 路径抛出
        }
    }

    private async Task FlushBatchAsync(List<ContextOperationEvent> batch, CancellationToken cancellationToken)
    {
        try
        {
            await _inner.EmitBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _batchEmitCount);
            CoreMetrics.EventSinkBatchEmits.Add(1);
        }
        catch
        {
            Interlocked.Increment(ref _errorCount);
            CoreMetrics.EventSinkErrors.Add(1);
            // BestEffort 路径：吞掉异常，事件已丢失
        }
    }

    /// <summary>
    /// 优雅关闭：标记通道完成，等待消费者 flush 完所有残留事件后退出。
    /// 重复调用是幂等的。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_inner.Kind == ContextEventSinkKind.BestEffort)
        {
            _channel!.Writer.TryComplete();
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
        }

        _shutdownCts.Dispose();
    }
}
