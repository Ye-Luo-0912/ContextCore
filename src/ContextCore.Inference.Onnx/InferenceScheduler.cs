using System.Buffers;
using System.Threading.Channels;
using ContextCore.Abstractions;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// InferenceScheduler：ONNX 推理并发治理 + 动态批处理
//
// 目标：
//   1. bounded inference queue：使用 Channel<InferenceRequest> 作为有界队列，
//      队列满时执行 backpressure（等待或立即拒绝），避免过载场景下请求无限堆积。
//   2. 最大并发数：通过 SemaphoreSlim 限制同时执行的微批数（MaxConcurrency），
//      防止打满 ORT 线程池或 GPU 显存。
//   3. micro-batching：在 BatchWaitWindow 内攒多个单条请求，达到 MaxBatchSize 或
//      窗口到期后合并为一次 session.Run 调用，提升吞吐。
//   4. 按模型分组：当前实现按 (SchemaVersion, FeatureCount) 分组执行；不同 schema
//      的请求会落入不同的微批，避免特征列错位。
//   5. batch wait window：BatchWaitWindow 控制最大攒批等待时间，平衡延迟与吞吐。
//   6. queue timeout：RequestDeadline 限制单个请求在调度器内的总停留时间，
//      超时返回失败，避免慢请求长期占用槽位。
//
// 设计原则：
//   1. 透明代理：InferenceScheduler 实现 IBatchInferenceEngine，可包裹任何
//      IBatchInferenceEngine（如 ModelActivationManager / OnnxInferenceEngine），
//      对消费方完全透明。
//   2. 默认关闭动态批处理：EnableDynamicBatching=false 时直接转发到内部引擎，
//      行为与未引入本类完全一致。仅在显式启用时才走 channel + 微批路径。
//      这是为了响应"先通过真实 profile 决定 dynamic batching 是否值得"的约束：
//      在低 QPS 单条请求场景下，micro-batching 只增加延迟不增加吞吐，
//      默认关闭让运维在通过 profile 验证收益后再显式开启。
//   3. fail-safe：调度器异常不应导致请求永久挂起；所有错误路径都通过
//      TaskCompletionSource.TrySetResult 返回失败结果。
//   4. 不破坏现有 OnnxInferenceEngine 的 InferBatchAsync 接口：本类位于
//      OnnxInferenceEngine 之上，作为可选的中间层。
// ===========================================================================

/// <summary>
/// ONNX 推理调度器配置。
/// 控制 bounded queue 容量、最大并发、微批处理参数与请求超时。
/// </summary>
/// <remarks>
/// 默认值面向"低 QPS 单条请求"场景：EnableDynamicBatching=false，
/// 调度器退化为透明转发，不引入额外延迟。运维在通过真实 profile 验证
/// micro-batching 收益后，可显式设置 EnableDynamicBatching=true 开启。
/// </remarks>
public sealed class InferenceSchedulerOptions
{
    /// <summary>
    /// 是否启用动态批处理（默认 false）。
    /// false 时 InferenceScheduler 直接转发到内部引擎，行为与未引入本类一致；
    /// true 时走 Channel + 微批路径，在 BatchWaitWindow 内攒批执行。
    /// </summary>
    /// <remarks>
    /// 启用前应通过真实 profile 验证收益：当前若为低 QPS 单条请求场景，
    /// micro-batching 会增加 BatchWaitWindow 量级的延迟而不增加吞吐。
    /// 推荐在 QPS ≥ 100 且单次推理耗时 ≥ 1ms 的场景下启用。
    /// </remarks>
    public bool EnableDynamicBatching { get; set; } = false;

    /// <summary>
    /// 最大并发微批数（默认 0 = 使用 Environment.ProcessorCount）。
    /// 通过 SemaphoreSlim 限制同时调用内部引擎 InferBatchAsync 的批次数。
    /// 超过此数的批次在 Semaphore 上等待，不消耗引擎槽位。
    /// </summary>
    public int MaxConcurrency { get; set; } = 0;

    /// <summary>
    /// 微批处理的最大大小（默认 32）。
    /// 攒到该行数后立即触发一次推理；不足则等待 BatchWaitWindow 到期。
    /// 应与 OnnxInferenceEngineOptions.MaxBatchSize 协调（本值不应超过引擎层的分片上限）。
    /// </summary>
    public int MaxBatchSize { get; set; } = 32;

    /// <summary>
    /// 有界队列容量上限（默认 256）。
    /// 队列满时新请求立即返回 QueueFull 失败（backpressure），避免无限堆积。
    /// 0 表示不限制（不推荐：过载时会无界增长导致 OOM）。
    /// </summary>
    public int MaxQueueLength { get; set; } = 256;

    /// <summary>
    /// 攒批等待窗口（默认 5ms）。
    /// 第一个请求到达后开始计时，到期或攒满 MaxBatchSize 后触发推理。
    /// 较小值降低延迟，较大值提升批合并率。
    /// </summary>
    public TimeSpan BatchWaitWindow { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// 请求在调度器内的总停留超时（默认 5s）。
    /// 包含排队 + 攒批 + 推理 + 结果拆分时间。超时返回失败，避免慢请求长期占用槽位。
    /// 应 ≥ 内部引擎的 InferenceTimeoutMs + BatchWaitWindow，否则会在引擎完成前提前失败。
    /// </summary>
    public TimeSpan RequestDeadline { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 模型版本（用于监控与日志；与内部引擎的 ModelVersion 一致）。
    /// 仅作为元数据暴露，不参与调度决策。
    /// </summary>
    public string? ModelVersion { get; set; }
}

/// <summary>
/// ONNX 推理调度器：在 IBatchInferenceEngine 之上提供有界队列、并发治理与动态批处理。
/// </summary>
/// <remarks>
/// <b>使用方式</b>：
/// <code>
/// var scheduler = new InferenceScheduler(innerEngine, new InferenceSchedulerOptions
/// {
///     EnableDynamicBatching = true,
///     MaxConcurrency = 4,
///     MaxBatchSize = 32,
///     BatchWaitWindow = TimeSpan.FromMilliseconds(5)
/// });
/// var result = await scheduler.InferBatchAsync(batch, ct);
/// </code>
/// <para>
/// <b>线程安全</b>：可被多线程并发调用。内部通过 Channel + SemaphoreSlim 保证线程安全。
/// </para>
/// <para>
/// <b>资源释放</b>：实现 <see cref="IAsyncDisposable"/>，Dispose 时停止 worker 并失败所有
/// pending 请求。Dispose 后再调用 InferBatchAsync 会返回失败结果。
/// </para>
/// </remarks>
public sealed class InferenceScheduler : IBatchInferenceEngine, IAsyncDisposable
{
    private readonly IBatchInferenceEngine _inner;
    private readonly InferenceSchedulerOptions _options;

    // bounded queue：MaxQueueLength=0 时退化为无界（不推荐）。
    private readonly Channel<InferenceRequest> _channel;

    // 并发治理：限制同时执行的微批数。
    private readonly SemaphoreSlim _concurrencyLimiter;

    // 后台 worker：从 channel 读取请求，攒批后调用内部引擎。
    private readonly Task _workerTask;

    // 停止令牌：Dispose 时取消 worker 与所有 pending 请求。
    private readonly CancellationTokenSource _stopCts;
    private int _disposed;

    /// <summary>
    /// 构造 InferenceScheduler。
    /// </summary>
    /// <param name="inner">被包裹的内部引擎（如 ModelActivationManager / OnnxInferenceEngine）。</param>
    /// <param name="options">调度器配置。</param>
    public InferenceScheduler(IBatchInferenceEngine inner, InferenceSchedulerOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);

        _inner = inner;
        _options = options;
        _stopCts = new CancellationTokenSource();

        // 构建 bounded channel。MaxQueueLength=0 时使用 Unbounded（向后兼容）。
        if (options.MaxQueueLength > 0)
        {
            _channel = Channel.CreateBounded<InferenceRequest>(new BoundedChannelOptions(options.MaxQueueLength)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });
        }
        else
        {
            _channel = Channel.CreateUnbounded<InferenceRequest>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

        var concurrency = options.MaxConcurrency > 0
            ? options.MaxConcurrency
            : Environment.ProcessorCount;
        _concurrencyLimiter = new SemaphoreSlim(Math.Max(1, concurrency), Math.Max(1, concurrency));

        // EnableDynamicBatching=false 时不启动 worker，直接转发到内部引擎。
        if (options.EnableDynamicBatching)
        {
            _workerTask = Task.Run(() => ProcessQueueAsync(_stopCts.Token));
        }
        else
        {
            _workerTask = Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public string ModelVersion => _options.ModelVersion ?? _inner.ModelVersion;

    /// <inheritdoc />
    public InferenceEngineKind Kind => _inner.Kind;

    /// <inheritdoc />
    public string ContentHash => _inner.ContentHash;

    /// <inheritdoc />
    public string CalibrationVersion => _inner.CalibrationVersion;

    /// <inheritdoc />
    public ValueTask<BatchInferenceResult> InferAsync(
        BatchInferenceRequest request,
        CancellationToken ct = default)
    {
        // 字典路径不参与微批处理：直接转发到内部引擎。
        // 动态批处理主要面向 FeatureBatch 高频路径；字典路径通常流量较低，且
        // 字典→FeatureBatch 转换在内部引擎层已处理，调度器层不重复实现。
        return _inner.InferAsync(request, ct);
    }

    /// <inheritdoc />
    public ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        // 未启用动态批处理：直接转发，行为与未引入本类一致。
        if (!_options.EnableDynamicBatching)
        {
            return _inner.InferBatchAsync(batch, ct);
        }

        // 已 Dispose：立即返回失败。
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new ValueTask<BatchInferenceResult>(BuildDisposedResult());
        }

        // 空批次直接返回成功（与内部引擎行为一致，避免无意义排队）。
        if (batch.RowCount == 0)
        {
            return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = true,
                Error = null,
                Duration = TimeSpan.Zero
            });
        }

        // 构造请求并写入 channel。
        var deadline = DateTimeOffset.UtcNow + _options.RequestDeadline;
        var request = new InferenceRequest
        {
            Batch = batch,
            Deadline = deadline,
            CancellationToken = ct,
            Completion = new TaskCompletionSource<BatchInferenceResult>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };

        // 写入 bounded channel：满时 TryWrite 返回 false，立即返回 QueueFull 失败（backpressure）。
        if (!_channel.Writer.TryWrite(request))
        {
            return new ValueTask<BatchInferenceResult>(BuildQueueFullResult());
        }

        // 注册外部取消：把 ct 取消信号转译为 TrySetCanceled，让 awaiter 收到 OperationCanceledException。
        // 同时也防止 ct 已取消但请求已在 worker 中执行的场景。
        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                request.Completion.TrySetCanceled(ct);
            });
        }

        return new ValueTask<BatchInferenceResult>(request.Completion.Task);
    }

    // -----------------------------------------------------------------------
    // Worker 主循环：从 channel 读取请求，在 BatchWaitWindow 内攒批后调用内部引擎。
    // -----------------------------------------------------------------------

    private async Task ProcessQueueAsync(CancellationToken stopToken)
    {
        var batchBuffer = new List<InferenceRequest>(_options.MaxBatchSize > 0 ? _options.MaxBatchSize : 32);

        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                batchBuffer.Clear();

                // 等待第一个请求到达。channel 关闭时退出循环。
                if (!await _channel.Reader.WaitToReadAsync(stopToken).ConfigureAwait(false))
                {
                    break;
                }

                // 攒批窗口：从首个请求到达开始计时，最多等待 BatchWaitWindow。
                var windowDeadlineUtc = DateTimeOffset.UtcNow + _options.BatchWaitWindow;
                var maxBatchSize = _options.MaxBatchSize > 0 ? _options.MaxBatchSize : 32;

                while (batchBuffer.Count < maxBatchSize)
                {
                    if (_channel.Reader.TryRead(out var req))
                    {
                        // 已过 deadline 的请求立即失败，不进入微批。
                        if (DateTimeOffset.UtcNow > req.Deadline)
                        {
                            req.Completion.TrySetResult(BuildExpiredResult(req));
                            continue;
                        }

                        batchBuffer.Add(req);

                        // 达到 MaxBatchSize：立即触发推理，不再等待。
                        if (batchBuffer.Count >= maxBatchSize)
                        {
                            break;
                        }
                    }
                    else
                    {
                        // 暂无更多请求。若批次为空，回到外层循环等待首个请求；
                        // 若已有请求，等待窗口到期或新请求到达。
                        if (batchBuffer.Count == 0)
                        {
                            break;
                        }

                        var remainingMs = (windowDeadlineUtc - DateTimeOffset.UtcNow).TotalMilliseconds;
                        if (remainingMs <= 0)
                        {
                            break;
                        }

                        using var windowCts = new CancellationTokenSource(
                            (int)Math.Max(1, remainingMs));
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                            stopToken, windowCts.Token);

                        try
                        {
                            var moreAvailable = await _channel.Reader.WaitToReadAsync(linked.Token)
                                .ConfigureAwait(false);
                            if (!moreAvailable)
                            {
                                // channel 已关闭：触发对当前 batch 的处理后退出外层循环。
                                break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // 窗口到期：触发微批推理。
                            break;
                        }
                    }
                }

                if (batchBuffer.Count > 0)
                {
                    await DispatchBatchAsync(batchBuffer, stopToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止路径：Dispose 触发 stopToken 取消。
        }
        catch (Exception)
        {
            // best-effort：worker 异常不应导致进程崩溃。
            // pending 请求由 Dispose 路径统一失败。
        }
        finally
        {
            // worker 退出时失败所有未处理的 pending 请求（防止泄漏）。
            DrainPendingRequestsOnShutdown();
        }
    }

    /// <summary>
    /// 分派一个微批到内部引擎：获取并发槽位、合并 batch、调用引擎、拆分结果。
    /// 不同 (SchemaVersion, FeatureCount) 的请求会被分组独立执行，避免特征列错位。
    /// </summary>
    private async Task DispatchBatchAsync(List<InferenceRequest> batch, CancellationToken stopToken)
    {
        // 获取并发槽位：限制同时执行的微批数。
        await _concurrencyLimiter.WaitAsync(stopToken).ConfigureAwait(false);

        try
        {
            // 按分组执行：(SchemaVersion, FeatureCount) 不同的请求无法合并（特征列对齐会错位）。
            // 多数生产场景下 batch 内所有请求 schema 一致，分组退化为单组。
            var groups = GroupBySchema(batch);

            foreach (var group in groups)
            {
                await ExecuteGroupAsync(group, stopToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    /// <summary>
    /// 按 (SchemaVersion, FeatureCount) 分组请求。
    /// 同组内可安全合并为一个连续 FeatureBatch；不同组需独立调用引擎。
    /// </summary>
    private static List<List<InferenceRequest>> GroupBySchema(List<InferenceRequest> batch)
    {
        if (batch.Count <= 1)
        {
            return new List<List<InferenceRequest>> { batch };
        }

        var groups = new List<List<InferenceRequest>>();
        var first = batch[0];
        var current = new List<InferenceRequest> { first };
        var currentKey = (first.Batch.SchemaVersion, first.Batch.FeatureCount);

        for (var i = 1; i < batch.Count; i++)
        {
            var req = batch[i];
            var key = (req.Batch.SchemaVersion, req.Batch.FeatureCount);
            if (key == currentKey)
            {
                current.Add(req);
            }
            else
            {
                groups.Add(current);
                current = new List<InferenceRequest> { req };
                currentKey = key;
            }
        }

        groups.Add(current);
        return groups;
    }

    /// <summary>
    /// 执行单个 schema 分组：合并所有请求的 FeatureBatch 为一个连续 batch，
    /// 调用内部引擎推理，然后按行偏移拆分结果回各请求。
    /// </summary>
    private async Task ExecuteGroupAsync(List<InferenceRequest> group, CancellationToken stopToken)
    {
        var featureCount = group[0].Batch.FeatureCount;
        var totalRows = 0;
        for (var i = 0; i < group.Count; i++)
        {
            totalRows += group[i].Batch.RowCount;
        }

        // 二次检查 deadline：在等待并发槽位期间可能已有请求过期。
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < group.Count; i++)
        {
            if (now > group[i].Deadline)
            {
                group[i].Completion.TrySetResult(BuildExpiredResult(group[i]));
                group[i] = null!; // 标记跳过
            }
        }

        // 过滤掉已过期/已处理的请求。
        var active = new List<InferenceRequest>(group.Count);
        foreach (var req in group)
        {
            if (req is not null)
            {
                active.Add(req);
            }
        }

        if (active.Count == 0)
        {
            return;
        }

        // 合并为连续 float 内存：[totalRows × featureCount] row-major。
        // 使用 ArrayPool 复用大 buffer，避免每次微批都分配大数组。
        var requiredLength = totalRows * featureCount;
        var buffer = ArrayPool<float>.Shared.Rent(requiredLength);
        var rowOffsets = new int[active.Count + 1];
        try
        {
            var offset = 0;
            for (var i = 0; i < active.Count; i++)
            {
                rowOffsets[i] = offset;
                var src = active[i].Batch.Values.Span;
                var dst = buffer.AsSpan(offset * featureCount, active[i].Batch.RowCount * featureCount);
                src.CopyTo(dst);
                offset += active[i].Batch.RowCount;
            }
            rowOffsets[active.Count] = offset;

            var combined = new FeatureBatch
            {
                SchemaVersion = active[0].Batch.SchemaVersion,
                Values = buffer.AsMemory(0, requiredLength),
                RowCount = totalRows,
                FeatureCount = featureCount,
                FeatureNames = active[0].Batch.FeatureNames
            };

            // 调用内部引擎。stopToken 用于 Dispose 时中断；外部 ct 由各请求独立管理（无法合并）。
            BatchInferenceResult result;
            try
            {
                result = await _inner.InferBatchAsync(combined, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Dispose 触发：失败所有 active 请求。
                foreach (var req in active)
                {
                    req.Completion.TrySetResult(BuildShutdownResult());
                }
                return;
            }
            catch (Exception ex)
            {
                var failed = new BatchInferenceResult
                {
                    Outputs = Array.Empty<InferenceOutput>(),
                    Succeeded = false,
                    Error = $"微批推理抛出异常：{ex.GetType().Name}: {ex.Message}",
                    Duration = TimeSpan.Zero
                };
                foreach (var req in active)
                {
                    req.Completion.TrySetResult(failed);
                }
                return;
            }

            // 推理失败：所有请求共享失败结果。
            if (!result.Succeeded)
            {
                foreach (var req in active)
                {
                    req.Completion.TrySetResult(result);
                }
                return;
            }

            // 推理成功：按 rowOffsets 拆分 Outputs 给各请求。
            // 防御性：若引擎返回的 Outputs 数量与 totalRows 不一致，按比例分发失败。
            if (result.Outputs.Count < totalRows)
            {
                var mismatchError = $"微批推理输出数量({result.Outputs.Count}) < 请求总行数({totalRows})，无法拆分。";
                var mismatchResult = new BatchInferenceResult
                {
                    Outputs = Array.Empty<InferenceOutput>(),
                    Succeeded = false,
                    Error = mismatchError,
                    Duration = result.Duration
                };
                foreach (var req in active)
                {
                    req.Completion.TrySetResult(mismatchResult);
                }
                return;
            }

            for (var i = 0; i < active.Count; i++)
            {
                var startRow = rowOffsets[i];
                var endRow = rowOffsets[i + 1];
                var rowCount = endRow - startRow;
                var outputs = new InferenceOutput[rowCount];
                for (var j = 0; j < rowCount; j++)
                {
                    outputs[j] = result.Outputs[startRow + j];
                }

                active[i].Completion.TrySetResult(new BatchInferenceResult
                {
                    Outputs = outputs,
                    Succeeded = true,
                    Error = null,
                    Duration = result.Duration
                });
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// worker 退出时失败所有 pending 请求（防止泄漏）。
    /// </summary>
    private void DrainPendingRequestsOnShutdown()
    {
        while (_channel.Reader.TryRead(out var req))
        {
            req.Completion.TrySetResult(BuildShutdownResult());
        }
    }

    private static BatchInferenceResult BuildQueueFullResult() => new()
    {
        Outputs = Array.Empty<InferenceOutput>(),
        Succeeded = false,
        Error = "InferenceSchedulerQueueFull：调度器有界队列已满，请求被拒绝（backpressure）。" +
                "请降低上游并发或增大 MaxQueueLength。",
        Duration = TimeSpan.Zero
    };

    private static BatchInferenceResult BuildExpiredResult(InferenceRequest req) => new()
    {
        Outputs = Array.Empty<InferenceOutput>(),
        Succeeded = false,
        Error = $"InferenceSchedulerRequestDeadline：请求在调度器内停留超过 deadline" +
                $"（截止于 {req.Deadline:O}），未执行推理即超时。",
        Duration = TimeSpan.Zero
    };

    private static BatchInferenceResult BuildShutdownResult() => new()
    {
        Outputs = Array.Empty<InferenceOutput>(),
        Succeeded = false,
        Error = "InferenceSchedulerShutdown：调度器已 Dispose，请求未完成。",
        Duration = TimeSpan.Zero
    };

    private static BatchInferenceResult BuildDisposedResult() => new()
    {
        Outputs = Array.Empty<InferenceOutput>(),
        Succeeded = false,
        Error = "InferenceSchedulerDisposed：调度器已 Dispose，拒绝新请求。",
        Duration = TimeSpan.Zero
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 通知 worker 停止。
        _stopCts.Cancel();
        _channel.Writer.TryComplete();

        // 等待 worker 退出（worker 退出时会失败所有 pending 请求）。
        if (_workerTask is not null)
        {
            try
            {
                await _workerTask.ConfigureAwait(false);
            }
            catch
            {
                // best-effort：worker 异常已在其内部处理。
            }
        }

        // 兜底：再次失败所有未读请求。
        DrainPendingRequestsOnShutdown();

        _concurrencyLimiter.Dispose();
        _stopCts.Dispose();
    }

    /// <summary>
    /// 内部推理请求载体：携带 FeatureBatch、deadline 与 TaskCompletionSource。
    /// </summary>
    private sealed class InferenceRequest
    {
        public required FeatureBatch Batch { get; init; }
        public required DateTimeOffset Deadline { get; init; }
        public required CancellationToken CancellationToken { get; init; }
        public required TaskCompletionSource<BatchInferenceResult> Completion { get; init; }
    }
}
