using System.Threading.Channels;

namespace ContextCore.Core.Infrastructure;

/// <summary>
/// 异步 trace 写入器配置。
/// </summary>
public sealed class TraceWriterOptions
{
    /// <summary>
    /// 是否启用异步写入模式（默认 true）。
    /// false 时为同步 durable 模式（严格审计），trace 写入直接 await 底层 store。
    /// </summary>
    public bool EnableAsyncWrite { get; init; } = true;

    /// <summary>
    /// 异步写入队列容量（默认 1024）。
    /// 队列满时记录 dropped 指标。
    /// </summary>
    public int ChannelCapacity { get; init; } = 1024;
}

/// <summary>
/// P5-0.4: 通用异步 trace 写入器。使用 bounded Channel 将 trace 写入移出请求关键路径。
/// </summary>
/// <typeparam name="T">trace 记录类型。</typeparam>
/// <remarks>
/// - 默认异步批量写入：SaveAsync 只入队，后台 task 负责实际写入。
/// - 队列满时记录 dropped/backpressure 指标，正式结果不受影响。
/// - 严格审计模式（EnableAsyncWrite=false）：同步 durable 写入。
/// - DisposeAsync 时 drain 队列，确保不丢失。
/// </remarks>
public sealed class AsyncTraceWriter<T> : IAsyncDisposable
{
    private readonly Func<T, CancellationToken, Task> _writeAsync;
    private readonly TraceWriterOptions _options;
    private readonly Channel<T> _channel;
    private readonly Task _drainTask;
    private long _droppedCount;
    private long _writtenCount;

    public AsyncTraceWriter(
        Func<T, CancellationToken, Task> writeAsync,
        TraceWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);
        _writeAsync = writeAsync;
        _options = options ?? new TraceWriterOptions();

        if (_options.EnableAsyncWrite)
        {
            // 使用 Wait 模式而非 DropWrite：DropWrite 下 TryWrite 总返回 true（静默丢弃），
            // 无法检测丢弃。Wait 模式下 TryWrite 满时返回 false，由调用方显式计数丢弃。
            _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(_options.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _drainTask = Task.Run(() => DrainLoopAsync());
        }
        else
        {
            // 同步模式：不创建 channel，SaveAsync 直接 await
            _channel = null!;
            _drainTask = null!;
        }
    }

    /// <summary>已丢弃的 trace 数量（队列满时递增）。</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>已写入的 trace 数量。</summary>
    public long WrittenCount => Interlocked.Read(ref _writtenCount);

    /// <summary>
    /// 提交 trace 写入。异步模式下入队即返回；同步模式下直接 await 底层写入。
    /// </summary>
    public ValueTask SaveAsync(T item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_options.EnableAsyncWrite)
        {
            // 同步 durable 模式：直接 await 底层写入
            return WriteSyncAsync(item, cancellationToken);
        }

        // 异步模式：尝试入队
        if (_channel.Writer.TryWrite(item))
        {
            return ValueTask.CompletedTask;
        }

        // 队列满：记录丢弃指标，不阻塞调用方
        Interlocked.Increment(ref _droppedCount);
        return ValueTask.CompletedTask;
    }

    private async Task DrainLoopAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _writeAsync(item, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Increment(ref _writtenCount);
            }
            catch
            {
                // 后台写入失败不得影响主流程；丢弃该 trace
                Interlocked.Increment(ref _droppedCount);
            }
        }
    }

    private async ValueTask WriteSyncAsync(T item, CancellationToken cancellationToken)
    {
        try
        {
            await _writeAsync(item, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _writtenCount);
        }
        catch
        {
            Interlocked.Increment(ref _droppedCount);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_options.EnableAsyncWrite)
        {
            return;
        }

        // 停止接收新项，让 drain 处理完队列中的剩余项
        _channel.Writer.Complete();

        try
        {
            // 等待 drain 完成（不取消，确保 pending 写入完成）
            await _drainTask.ConfigureAwait(false);
        }
        catch
        {
            // drain 过程中的异常已在上层 catch
        }
    }
}
