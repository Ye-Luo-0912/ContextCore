using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using ContextCore.Abstractions;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// InferenceScheduler：ONNX 推理并发治理 + 动态批处理
//
// 目标：
//   1. bounded inference queue：使用 Channel<InferenceRequest> 作为有界队列，
//      队列满时执行 backpressure（立即拒绝），避免过载场景下请求无限堆积。
//   2. 最大并发数（真正生效）：Batch Coordinator 从 channel 读取并按 BatchKey 攒批，
//      每个 Ready 批次以 fire-and-forget 方式派发到 Execution Worker；
//      由 SemaphoreSlim(MaxConcurrency) 限制同时执行的批次数。
//      Coordinator 派发后不 await 执行，因此 MaxConcurrency>1 时会形成多个并发微批。
//   3. micro-batching：在 BatchWaitWindow 内攒多个单条请求，达到 MaxBatchSize（按 row 数）
//      或窗口到期后合并为一次 session.Run 调用，提升吞吐。
//   4. 按模型分组（BatchKey）：(ModelGeneration, SchemaVersion, FeatureNamesHash, FeatureCount,
//      ExecutionProvider) 不同的请求不能合并到同一批，避免特征列错位。
//      FeatureNamesHash 为顺序敏感哈希，并通过 NamesEqual 二次校验防止哈希碰撞误合并。
//   5. ArrayPool 安全：租借的数组在写入前清零，且只把有效长度（recompute 后的 totalRows）
//      送入模型，避免池化数组尾部脏数据污染推理输入。
//   6. deadline 贯穿：攒批时跳过已过期请求；执行推理前再次检查；推理中以最早 deadline
//      构造 CancellationTokenSource 并传入内部引擎，到期即取消推理。
//   7. 过期请求清理：移除过期请求后重新计算 totalRows（避免脏数据送入模型），
//      并通过 TrySetResult 返回超时错误通知等待方。
//   8. worker 崩溃恢复：Coordinator 循环与 Execution 均用 try-catch 包裹；
//      异常时将批次中所有未完成请求标记为失败；Coordinator 崩溃后自动重启。
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
    /// 微批处理的最大大小（按 row 数，默认 32）。
    /// 一个请求可能包含多行；批次大小 = 所有请求的行数总和。
    /// 攒到该行数后立即触发一次推理；不足则等待 BatchWaitWindow 到期。
    /// 超过此值时会拆分为多个微批。&lt;=0 表示不限制（仅按窗口攒批）。
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
    /// 是否启用 DropWrite 策略（默认 false）。
    /// false 时队列满返回 QueueFull 错误（调用方应处理失败/重试）；
    /// true 时队列满返回 Dropped 结果（语义为"已丢弃"而非"错误"，调用方可据此跳过而非重试），
    /// 并递增 <see cref="InferenceScheduler.DroppedCount"/> 计数器供监控使用。
    /// 适用于实时推理场景中"宁丢不堵"的背压策略。
    /// </summary>
    public bool EnableDropWrite { get; set; } = false;

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
    // Coordinator 崩溃后重启的退避间隔，避免极端情况下死循环打满 CPU。
    private static readonly TimeSpan CoordinatorRestartBackoff = TimeSpan.FromMilliseconds(100);

    private readonly IBatchInferenceEngine _inner;
    private readonly InferenceSchedulerOptions _options;

    // bounded queue：MaxQueueLength=0 时退化为无界（不推荐）。
    private readonly Channel<InferenceRequest> _channel;

    // DropWrite 计数器：EnableDropWrite=true 时队列满丢弃的请求数。
    private long _droppedCount;

    /// <summary>DropWrite 策略下被丢弃的请求总数（供监控/告警使用）。</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    // 并发治理：限制同时执行的微批数（真正生效——Coordinator 派发后不 await 执行）。
    private readonly SemaphoreSlim _concurrencyLimiter;

    // 后台 Coordinator：从 channel 读取请求，按 BatchKey 攒批后 fire-and-forget 派发到 Execution Worker。
    private readonly Task _workerTask;

    // 停止令牌：Dispose 时取消 Coordinator 与所有 pending 请求。
    private readonly CancellationTokenSource _stopCts;
    private int _disposed;

    // ModelGeneration 不再缓存为常量 —— 每次执行时从 IModelActivationManager 获取当前 Active Generation，
    // 以感知模型热切换。世代号变化时新请求会进入新的 BatchKey，自然与旧世代已攒批的请求分离。
    // _executionProvider 仍为常量（IBatchInferenceEngine 接口未暴露 EP，调度器层无法动态获取）。
    private readonly string _executionProvider = string.Empty;

    // 在飞 Execution Worker 任务追踪：Dispose 时 await 全部完成，避免请求泄漏。
    private readonly object _outstandingLock = new();
    private readonly List<Task> _outstanding = new();

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
                FullMode = BoundedChannelFullMode.Wait,
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

        // EnableDynamicBatching=false 时不启动 Coordinator，直接转发到内部引擎。
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
        // deadline 使用 Stopwatch 单调时钟（DeadlineTimestamp）避免 wall clock 漂移导致
        // 请求被错误地判定为未过期/已过期。Deadline (DateTimeOffset) 仅保留用于错误消息显示。
        // 入队时捕获当前 Active Engine 的租约，确保请求在入队时的世代上执行，
        // 避免热切换后请求在新引擎上执行（cross-generation execution）。
        var nowTimestamp = Stopwatch.GetTimestamp();
        var deadline = DateTimeOffset.UtcNow + _options.RequestDeadline;
        var deadlineTimestamp = nowTimestamp + (long)(_options.RequestDeadline.TotalSeconds * Stopwatch.Frequency);

        IInferenceEngineLease? capturedLease = null;
        if (_inner is IModelActivationManager activationManager)
        {
            // 捕获当前 Active Engine 租约；未激活时使用 fallback 永久租约，
            // 固定请求在 fallback 引擎上执行，避免排队期间模型被激活后 cross-generation execution。
            capturedLease = activationManager.AcquireEngineLease()
                ?? activationManager.AcquireFallbackEngineLease();
        }

        var request = new InferenceRequest
        {
            Batch = batch,
            DeadlineTimestamp = deadlineTimestamp,
            Deadline = deadline,
            CancellationToken = ct,
            Completion = new TaskCompletionSource<BatchInferenceResult>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            CapturedLease = capturedLease
        };

        // 注册外部取消 —— 保存 CancellationTokenRegistration 以便请求完成后 Dispose，
        // 避免 ct 长期存活时回调注册泄漏。即使 TryWrite 失败也要正确清理。
        if (ct.CanBeCanceled)
        {
            request.CancellationRegistration = ct.Register(static state =>
            {
                var req = (InferenceRequest)state!;
                // CallerCompletion —— 完成调用方 Task（让调用方收到取消异常），
                // 但不释放 Engine Lease：lease 生命周期由内部执行管理（InternalExecutionCompletion）。
                // 请求继续留在队列中等待内部执行；内部执行检查 CallerCancelled 标志后跳过引擎调用并释放 lease。
                // 这避免请求已进入正在执行的微批时，lease 释放导致旧引擎引用计数归零被 drain/dispose。
                Volatile.Write(ref req.CallerCancelled, 1);
                req.Completion.TrySetCanceled(req.CancellationToken);
            }, request);
        }

        // 写入 bounded channel：满时 TryWrite 返回 false，立即返回 QueueFull 失败（backpressure）。
        // Wait 模式下 TryWrite 满时返回 false（不阻塞），由这里返回 QueueFull，
        // 保证请求不会因 DropWrite 被静默丢弃导致 TaskCompletionSource 永久挂起。
        if (!_channel.Writer.TryWrite(request))
        {
            request.CancellationRegistration.Dispose();
            // 入队失败时释放租约（递减引用计数 / fallback lease no-op）。
            request.CapturedLease?.Dispose();
            // DropWrite 策略：返回 Dropped 结果（语义为"已丢弃"而非"错误"），递增计数器。
            if (_options.EnableDropWrite)
            {
                Interlocked.Increment(ref _droppedCount);
                return new ValueTask<BatchInferenceResult>(BuildDroppedResult());
            }
            return new ValueTask<BatchInferenceResult>(BuildQueueFullResult());
        }

        // Engine Lease 生命周期与调用方 Task 解耦 ——
        // 不再通过 ContinueWith 在 CallerCompletion 时释放 lease。
        // lease 由内部执行出口释放（InternalExecutionCompletion）：
        //   - 正常执行完成（成功/失败/异常）后 FinalizeRequest 释放；
        //   - caller 已取消时，内部执行跳过引擎调用并 FinalizeRequest 释放；
        //   - 过期/shutdown 路径同样 FinalizeRequest 释放。
        // 这避免外部取消立即释放 lease 导致请求已进入微批时旧引擎被 drain/dispose。
        // CancellationRegistration 也由 FinalizeRequest 统一 Dispose（避免回调内 Dispose 自身）。
        return new ValueTask<BatchInferenceResult>(request.Completion.Task);
    }

    // -----------------------------------------------------------------------
    // Coordinator：从 channel 读取请求，按 BatchKey 攒批，fire-and-forget 派发到 Execution Worker。
    // 派发后不 await 执行——这是 MaxConcurrency 真正生效的关键。
    // -----------------------------------------------------------------------

    /// <summary>
    /// Coordinator 主循环（带崩溃自动重启）。
    /// 每轮调用 <see cref="RunCoordinatorLoopAsync"/> 执行实际的读取/攒批/派发；
    /// 抛出非取消异常时失败当前 pending 缓冲并重启下一轮。
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            var pending = new Dictionary<InferenceBatchKey, PendingGroup>();
            try
            {
                await RunCoordinatorLoopAsync(pending, stopToken).ConfigureAwait(false);
                // 正常返回（channel 关闭）：失败剩余 pending 并退出。
                FailPendingBuffer(pending, BuildShutdownResult());
                break;
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                FailPendingBuffer(pending, BuildShutdownResult());
                break;
            }
            catch (Exception ex)
            {
                // Coordinator 崩溃：失败当前 pending 中所有请求，自动重启下一轮循环。
                FailPendingBuffer(pending, BuildWorkerCrashResult(ex));
                try
                {
                    await Task.Delay(CoordinatorRestartBackoff, stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                // continue → 重启 Coordinator
            }
        }

        // Coordinator 退出时失败所有未处理的 pending 请求（防止泄漏）。
        DrainPendingRequestsOnShutdown();
    }

    /// <summary>
    /// 单轮 Coordinator 循环：阻塞等待首个请求 → 排空可读请求并按 BatchKey 攒批
    /// → 派发窗口已到期的分组 → 等待到最早窗口到期或新请求到达。
    /// 正常返回表示 channel 已关闭；stopToken 取消时抛出 OperationCanceledException。
    /// </summary>
    private async Task RunCoordinatorLoopAsync(
        Dictionary<InferenceBatchKey, PendingGroup> pending,
        CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            // 若无 pending，阻塞等待首个请求到达。
            if (pending.Count == 0)
            {
                if (!await _channel.Reader.WaitToReadAsync(stopToken).ConfigureAwait(false))
                {
                    // channel 已关闭：返回，由外层失败剩余 pending（此处 pending 为空）。
                    return;
                }
            }

            // 排空当前可读请求（非阻塞），按 BatchKey 攒批。
            while (_channel.Reader.TryRead(out var req))
            {
                ProcessIncoming(req, pending, stopToken);
            }

            // 派发窗口已到期的分组。
            DispatchExpiredGroups(pending, stopToken);

            if (pending.Count == 0)
            {
                // 全部派发或全部过期：回到外层等待首个请求。
                continue;
            }

            // 计算最早窗口到期时间。
            var now = DateTimeOffset.UtcNow;
            var earliest = DateTimeOffset.MaxValue;
            foreach (var g in pending.Values)
            {
                if (g.WindowDeadline < earliest) earliest = g.WindowDeadline;
            }

            var dueIn = earliest - now;
            if (dueIn <= TimeSpan.Zero)
            {
                // 已有窗口到期：下一轮循环派发。
                continue;
            }

            // 等待到最早窗口到期或有新请求到达。
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            delayCts.CancelAfter(dueIn);
            try
            {
                var moreAvailable = await _channel.Reader.WaitToReadAsync(delayCts.Token).ConfigureAwait(false);
                if (!moreAvailable)
                {
                    // channel 已关闭：立即派发所有 pending（不等窗口）并返回。
                    DispatchAll(pending, stopToken);
                    return;
                }
                // 有新请求可读：下一轮循环排空。
            }
            catch (OperationCanceledException)
            {
                // delay 到期或 stop：下一轮处理。
                if (stopToken.IsCancellationRequested) return;
            }
        }
    }

    /// <summary>
    /// 将单个请求加入对应的 pending 分组（按 BatchKey）。
    /// 处理：已取消/已过期请求立即失败；FeatureNames 哈希碰撞时拆分；超过 MaxBatchSize（按 row 数）时拆分。
    /// </summary>
    private void ProcessIncoming(
        InferenceRequest req,
        Dictionary<InferenceBatchKey, PendingGroup> pending,
        CancellationToken stopToken)
    {
        // 已被外部 ct 取消（CallerCancelled）或已失败：跳过攒批。
        // 外部取消时 lease 尚未释放（由内部执行管理），此处 FinalizeRequest 释放 lease + 取消注册。
        if (req.Completion.Task.IsCompleted)
        {
            FinalizeRequest(req);
            return;
        }

        // 已过 deadline：立即失败通知，不进入微批。
        // 使用 Stopwatch 单调时钟判断过期，避免 wall clock 漂移。
        if (Stopwatch.GetTimestamp() >= req.DeadlineTimestamp)
        {
            req.Completion.TrySetResult(BuildExpiredResult(req));
            FinalizeRequest(req);
            return;
        }

        var key = ComputeBatchKey(req);
        var rows = req.Batch.RowCount;

        if (!pending.TryGetValue(key, out var group))
        {
            group = null;
        }
        else if (!NamesEqual(group.FeatureNames, req.Batch.FeatureNames))
        {
            // 哈希碰撞（同 key 不同 FeatureNames）：派发已有分组，重新开组。
            pending.Remove(key);
            DispatchGroup(group.Requests, stopToken);
            group = null;
        }
        else if (_options.MaxBatchSize > 0
            && group.TotalRows + rows > _options.MaxBatchSize
            && group.TotalRows > 0)
        {
            // 加入后超过 MaxBatchSize（按 row 数）：先派发已有分组，再为新请求开新组。
            pending.Remove(key);
            DispatchGroup(group.Requests, stopToken);
            group = null;
        }

        if (group is null)
        {
            group = new PendingGroup
            {
                Key = key,
                FeatureNames = req.Batch.FeatureNames,
                WindowDeadline = DateTimeOffset.UtcNow + _options.BatchWaitWindow
            };
            pending[key] = group;
        }

        group.Requests.Add(req);
        group.TotalRows += rows;

        // 单组攒满（按 row 数）立即派发。
        if (_options.MaxBatchSize > 0 && group.TotalRows >= _options.MaxBatchSize)
        {
            pending.Remove(key);
            DispatchGroup(group.Requests, stopToken);
        }
    }

    /// <summary>
    /// 派发所有窗口已到期的 pending 分组。
    /// </summary>
    private void DispatchExpiredGroups(
        Dictionary<InferenceBatchKey, PendingGroup> pending,
        CancellationToken stopToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        List<PendingGroup>? toDispatch = null;
        foreach (var g in pending.Values)
        {
            if (now >= g.WindowDeadline)
            {
                (toDispatch ??= new List<PendingGroup>()).Add(g);
            }
        }

        if (toDispatch is null)
        {
            return;
        }

        foreach (var g in toDispatch)
        {
            pending.Remove(g.Key);
            DispatchGroup(g.Requests, stopToken);
        }
    }

    /// <summary>
    /// 立即派发所有 pending 分组（不等窗口），用于 channel 关闭时的收尾。
    /// </summary>
    private void DispatchAll(
        Dictionary<InferenceBatchKey, PendingGroup> pending,
        CancellationToken stopToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var snapshot = new List<PendingGroup>(pending.Values);
        pending.Clear();
        foreach (var g in snapshot)
        {
            DispatchGroup(g.Requests, stopToken);
        }
    }

    /// <summary>
    /// fire-and-forget 派发一个 Ready 批次到 Execution Worker，并追踪任务以便 Dispose 时等待。
    /// 实际并发由 <see cref="_concurrencyLimiter"/> 限制。
    /// </summary>
    private void DispatchGroup(List<InferenceRequest> requests, CancellationToken stopToken)
    {
        if (requests.Count == 0)
        {
            return;
        }

        var task = RunGroupExecutionAsync(requests, stopToken);
        TrackOutstanding(task);
    }

    /// <summary>
    /// 单个 Execution Worker：获取并发槽位后执行批次推理。
    /// try-catch 包裹确保 worker 崩溃时批次中所有未完成请求被标记为失败（不永久等待）。
    /// </summary>
    private async Task RunGroupExecutionAsync(List<InferenceRequest> group, CancellationToken stopToken)
    {
        await _concurrencyLimiter.WaitAsync(stopToken).ConfigureAwait(false);
        try
        {
            await ExecuteGroupAsync(group, stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            FailAll(group, BuildShutdownResult());
        }
        catch (Exception ex)
        {
            // Execution Worker 崩溃：将批次中所有未完成请求标记为失败。
            FailAll(group, BuildWorkerCrashResult(ex));
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    /// <summary>
    /// 执行单个 BatchKey 分组：二次检查 deadline、重新计算 totalRows、合并为连续 FeatureBatch、
    /// 以最早 deadline 构造 CTS 调用内部引擎、按行偏移拆分结果回各请求。
    /// 同组内所有请求 SchemaVersion/FeatureCount/FeatureNames 一致（由 BatchKey + NamesEqual 保证）。
    /// </summary>
    private async Task ExecuteGroupAsync(List<InferenceRequest> group, CancellationToken stopToken)
    {
        var featureCount = group[0].Batch.FeatureCount;

        // 二次检查 deadline：在等待并发槽位期间可能已有请求过期；同时丢弃已被外部 ct 取消的请求。
        // 使用 Stopwatch 单调时钟判断过期。
        var nowTimestamp = Stopwatch.GetTimestamp();
        var active = new List<InferenceRequest>(group.Count);
        for (var i = 0; i < group.Count; i++)
        {
            var req = group[i];
            if (nowTimestamp >= req.DeadlineTimestamp)
            {
                req.Completion.TrySetResult(BuildExpiredResult(req));
                FinalizeRequest(req);
            }
            else if (req.Completion.Task.IsCompleted)
            {
                // 已被外部 ct 取消（CallerCancelled）—— 释放 lease，不参与合并，不调用引擎。
                FinalizeRequest(req);
            }
            else
            {
                active.Add(req);
            }
        }

        if (active.Count == 0)
        {
            return;
        }

        // 重新计算 totalRows（移除过期/取消请求后）——关键：避免脏数据送入模型，
        // 也避免 ArrayPool 尾部未写入区域被当作有效输入。
        var totalRows = 0;
        for (var i = 0; i < active.Count; i++)
        {
            totalRows += active[i].Batch.RowCount;
        }

        var requiredLength = totalRows * featureCount;
        var buffer = ArrayPool<float>.Shared.Rent(requiredLength);
        var rowOffsets = new int[active.Count + 1];
        try
        {
            // ArrayPool 安全 —— 后续 CopyTo 会完整覆盖 [0, requiredLength) 区域，
            // 无需预先 Clear（原先整段 Clear 后又完整覆盖是冗余操作）。
            // 合并为连续 float 内存：[totalRows × featureCount] row-major。
            var offset = 0;
            for (var i = 0; i < active.Count; i++)
            {
                rowOffsets[i] = offset;
                var rows = active[i].Batch.RowCount;
                var src = active[i].Batch.Values.Span;
                var dst = buffer.AsSpan(offset * featureCount, rows * featureCount);
                src.CopyTo(dst);
                offset += rows;
            }
            rowOffsets[active.Count] = offset;

            var combined = new FeatureBatch
            {
                SchemaVersion = active[0].Batch.SchemaVersion,
                Values = buffer.AsMemory(0, requiredLength), // 仅有效长度送入模型
                RowCount = totalRows,
                FeatureCount = featureCount,
                FeatureNames = active[0].Batch.FeatureNames
            };

            // deadline 贯穿推理：以最早 deadline 构造 CTS，与 stopToken 链接后传入内部引擎。
            // 微批内请求均在 BatchWaitWindow 内到达、共享同一 RequestDeadline，最早到期≈全员到期。
            // 使用 Stopwatch 单调时钟计算 dueIn，避免 wall clock 漂移。
            var earliest = active[0].DeadlineTimestamp;
            for (var i = 1; i < active.Count; i++)
            {
                if (active[i].DeadlineTimestamp < earliest) earliest = active[i].DeadlineTimestamp;
            }
            var nowTicks = Stopwatch.GetTimestamp();
            var dueInTicks = earliest - nowTicks;
            if (dueInTicks <= 0)
            {
                // 全部已过期（防御性）：直接失败。
                FailAll(active, BuildExpiredResult(null));
                return;
            }
            var dueIn = TimeSpan.FromSeconds((double)dueInTicks / Stopwatch.Frequency);

            using var deadlineCts = new CancellationTokenSource(dueIn);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopToken, deadlineCts.Token);

            // 使用入队时捕获的引擎执行（避免热切换后 cross-generation execution）。
            // 同组所有请求共享同一 BatchKey（含 ModelGeneration），故捕获的引擎一致；
            // _inner 为 IModelActivationManager 时 CapturedLease 永远非 null（激活或 fallback lease），
            // 故未激活时使用 fallback 引擎执行（稳定引用），不会因排队期间模型激活而跑到新引擎。
            // _inner 非 IModelActivationManager 时 CapturedLease 为 null，回退到 _inner（行为不变）。
            // 若捕获的引擎已被 Dispose（模型已退役且 drain 超时），InferBatchAsync 会抛出异常 ——
            // 这是正确行为：请求应失败而非静默切换到新世代引擎，调用方可重试。
            var executionEngine = active[0].CapturedLease?.Engine ?? _inner;

            BatchInferenceResult result;
            try
            {
                // 单请求/合并后总行数 > MaxBatchSize 时分片调用内部引擎，避免单次推理超过引擎分片上限。
                // 每个 shard 行数 ≤ MaxBatchSize；结果按行顺序合并为一个 BatchInferenceResult。
                if (_options.MaxBatchSize > 0 && totalRows > _options.MaxBatchSize)
                {
                    result = await InferWithShardingAsync(combined, totalRows, featureCount, executionEngine, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    result = await executionEngine.InferBatchAsync(combined, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                if (stopToken.IsCancellationRequested)
                {
                    FailAll(active, BuildShutdownResult());
                }
                else
                {
                    // deadline 到期取消推理：批内所有请求视作超时。
                    FailAll(active, BuildExpiredResult(null));
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
                FailAll(active, failed);
                return;
            }

            // 推理失败：所有请求共享失败结果。
            if (!result.Succeeded)
            {
                FailAll(active, result);
                return;
            }

            // 推理成功：按 rowOffsets 拆分 Outputs 给各请求。
            // 严格相等验证 —— 引擎返回的 Outputs 数量必须 == totalRows，否则视为异常（不只是 < totalRows）。
            if (result.Outputs.Count != totalRows)
            {
                var mismatchError = $"微批推理输出数量({result.Outputs.Count}) != 请求总行数({totalRows})，无法拆分。";
                var mismatchResult = new BatchInferenceResult
                {
                    Outputs = Array.Empty<InferenceOutput>(),
                    Succeeded = false,
                    Error = mismatchError,
                    Duration = result.Duration
                };
                FailAll(active, mismatchResult);
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
                // InternalExecutionCompletion —— 推理成功后释放 lease + 取消注册。
                FinalizeRequest(active[i]);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 分片推理 —— 当合并后总行数超过 MaxBatchSize 时，按 MaxBatchSize 拆分为多个子 batch，
    /// 依次调用内部引擎，按行顺序合并所有 shard 的 Outputs 为单个 BatchInferenceResult。
    /// 任一 shard 失败/抛异常即返回失败（已执行的 shard 结果丢弃）。
    /// </summary>
    /// <param name="combined">合并后的完整 batch（RowCount > MaxBatchSize）。</param>
    /// <param name="totalRows">总行数（= combined.RowCount）。</param>
    /// <param name="featureCount">每行特征数。</param>
    /// <param name="engine">执行引擎（入队时捕获的引擎，确保 shard 也在同一世代执行）。</param>
    /// <param name="ct">取消令牌（已链接 stopToken + deadline）。</param>
    private async ValueTask<BatchInferenceResult> InferWithShardingAsync(
        FeatureBatch combined,
        int totalRows,
        int featureCount,
        IBatchInferenceEngine engine,
        CancellationToken ct)
    {
        var maxBatchSize = _options.MaxBatchSize;
        var allOutputs = new List<InferenceOutput>(totalRows);
        var totalDuration = TimeSpan.Zero;

        for (var rowStart = 0; rowStart < totalRows; rowStart += maxBatchSize)
        {
            var shardRows = Math.Min(maxBatchSize, totalRows - rowStart);
            var shardBuffer = ArrayPool<float>.Shared.Rent(shardRows * featureCount);
            try
            {
                // 复制 shard 行到独立 buffer。
                // srcSpan 在循环内获取，避免 ReadOnlySpan<float> 跨 await 边界（CS4007）。
                var srcSpan = combined.Values.Span;
                var srcOffset = rowStart * featureCount;
                var srcSlice = srcSpan.Slice(srcOffset, shardRows * featureCount);
                srcSlice.CopyTo(shardBuffer);

                var shardBatch = new FeatureBatch
                {
                    SchemaVersion = combined.SchemaVersion,
                    Values = shardBuffer.AsMemory(0, shardRows * featureCount),
                    RowCount = shardRows,
                    FeatureCount = featureCount,
                    FeatureNames = combined.FeatureNames
                };

                var shardResult = await engine.InferBatchAsync(shardBatch, ct).ConfigureAwait(false);
                if (!shardResult.Succeeded)
                {
                    // shard 失败：返回失败（携带 shard 错误信息）。
                    return new BatchInferenceResult
                    {
                        Outputs = Array.Empty<InferenceOutput>(),
                        Succeeded = false,
                        Error = $"分片推理 shard[rowStart={rowStart},rows={shardRows}] 失败：{shardResult.Error}",
                        Duration = totalDuration + shardResult.Duration
                    };
                }

                // 严格相等验证 —— shard 输出行数必须 == shardRows。
                if (shardResult.Outputs.Count != shardRows)
                {
                    return new BatchInferenceResult
                    {
                        Outputs = Array.Empty<InferenceOutput>(),
                        Succeeded = false,
                        Error = $"分片推理 shard[rowStart={rowStart},rows={shardRows}] 输出数量" +
                                $"({shardResult.Outputs.Count}) != shard 行数({shardRows})",
                        Duration = totalDuration + shardResult.Duration
                    };
                }

                allOutputs.AddRange(shardResult.Outputs);
                totalDuration += shardResult.Duration;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(shardBuffer);
            }
        }

        return new BatchInferenceResult
        {
            Outputs = allOutputs,
            Succeeded = true,
            Error = null,
            Duration = totalDuration
        };
    }

    /// <summary>
    /// 计算请求的 BatchKey。
    /// FeatureNamesHash 为顺序敏感哈希；NamesEqual 在加入分组时二次校验防止碰撞误合并。
    /// ModelGeneration 使用入队时捕获的 CapturedLease.Generation（而非当前 ActiveGeneration），
    /// 避免热切换后请求被错误分组：
    ///   - CapturedLease 非 null：使用 lease.Generation（请求实际执行的引擎世代）；
    ///     _inner 为 IModelActivationManager 时 CapturedLease 永远非 null
    ///     （激活=真实世代，未激活=fallback lease Generation=0）；
    ///   - CapturedLease 为 null（_inner 非 IModelActivationManager）：用 0L（无世代概念，行为不变）。
    /// 关键：BatchKey 必须与执行时使用的引擎世代一致（ExecuteGroupAsync 用 active[0].CapturedLease.Engine），
    /// 否则同组请求会执行在首个请求的引擎上而与其他请求的捕获引擎不同，导致跨世代合并。
    /// </summary>
    private InferenceBatchKey ComputeBatchKey(InferenceRequest req)
    {
        long modelGeneration;
        if (req.CapturedLease is { } lease)
        {
            // 使用入队时捕获的 lease.Generation。
            // _inner 为 IModelActivationManager 时 CapturedLease 永远非 null（激活=真实世代，未激活=fallback lease Generation=0）。
            // fallback lease Generation=0 不会与真实激活世代冲突（真实世代自 1 起自增）。
            modelGeneration = lease.Generation;
        }
        else
        {
            // _inner 非 IModelActivationManager（无世代概念，CapturedLease 为 null）：用 0L。
            modelGeneration = 0L;
        }

        var batch = req.Batch;
        return new InferenceBatchKey(
            ModelGeneration: modelGeneration,
            SchemaVersion: batch.SchemaVersion,
            FeatureNamesHash: ComputeFeatureNamesHash(batch.FeatureNames),
            FeatureCount: batch.FeatureCount,
            ExecutionProvider: _executionProvider);
    }

    /// <summary>
    /// 顺序敏感的 FeatureNames 哈希：HashCode 按添加顺序组合，
    /// 故 ["a","b"] 与 ["b","a"] 产生不同哈希，避免列序错位的请求被合并。
    /// </summary>
    private static int ComputeFeatureNamesHash(IReadOnlyList<string> names)
    {
        var hash = new HashCode();
        for (var i = 0; i < names.Count; i++)
        {
            hash.Add(names[i]);
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// 顺序敏感的 FeatureNames 相等性判断（防止哈希碰撞误合并）。
    /// </summary>
    private static bool NamesEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void TrackOutstanding(Task t)
    {
        lock (_outstandingLock)
        {
            // 清理已完成任务，防止列表无界增长。
            for (var i = _outstanding.Count - 1; i >= 0; i--)
            {
                if (_outstanding[i].IsCompleted)
                {
                    _outstanding.RemoveAt(i);
                }
            }
            _outstanding.Add(t);
        }
    }

    private Task[] SnapshotOutstanding()
    {
        lock (_outstandingLock)
        {
            return _outstanding.ToArray();
        }
    }

    /// <summary>
    /// 失败 pending 缓冲中所有请求（Coordinator 崩溃/停止时调用）。
    /// </summary>
    private static void FailPendingBuffer(
        Dictionary<InferenceBatchKey, PendingGroup> pending,
        BatchInferenceResult result)
    {
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var g in pending.Values)
        {
            FailAll(g.Requests, result);
        }
        pending.Clear();
    }

    /// <summary>
    /// 请求终结 —— Dispose CancellationRegistration 与 CapturedLease。
    /// 在请求离开内部执行的每个出口调用（成功/失败/取消/过期/跳过/shutdown）。
    /// 这是 InternalExecutionCompletion：lease 生命周期由内部执行管理，
    /// 与调用方 Task（CallerCompletion）生命周期解耦。
    /// 幂等：CancellationRegistration.Dispose 与 IInferenceEngineLease.Dispose 均幂等，
    /// 重复调用安全（外部取消 + 内部失败可能同时触发，但各自只释放一次自己的引用）。
    /// </summary>
    private static void FinalizeRequest(InferenceRequest req)
    {
        req.CancellationRegistration.Dispose();
        req.CapturedLease?.Dispose();
    }

    private static void FailAll(List<InferenceRequest> reqs, BatchInferenceResult result)
    {
        for (var i = 0; i < reqs.Count; i++)
        {
            reqs[i].Completion.TrySetResult(result);
            // InternalExecutionCompletion —— 失败路径同样释放 lease + 取消注册。
            // 对已被 caller 取消（IsCompleted）的请求，TrySetResult 为 no-op，FinalizeRequest 仍释放 lease。
            FinalizeRequest(reqs[i]);
        }
    }

    /// <summary>
    /// Coordinator 退出时失败所有 channel 中未读请求（防止泄漏）。
    /// </summary>
    private void DrainPendingRequestsOnShutdown()
    {
        while (_channel.Reader.TryRead(out var req))
        {
            req.Completion.TrySetResult(BuildShutdownResult());
            // shutdown 路径释放 lease + 取消注册。
            FinalizeRequest(req);
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

    private static BatchInferenceResult BuildDroppedResult() => new()
    {
        Outputs = Array.Empty<InferenceOutput>(),
        Succeeded = false,
        Error = "InferenceSchedulerDropped：调度器有界队列已满，请求已被 DropWrite 策略丢弃。" +
                "此为预期背控行为，调用方可据此跳过而非重试。",
        Duration = TimeSpan.Zero
    };

    private static BatchInferenceResult BuildExpiredResult(InferenceRequest? req) => new()
    {
        Outputs = Array.Empty<InferenceOutput>(),
        Succeeded = false,
        Error = req is null
            ? "InferenceSchedulerRequestDeadline：请求在调度器内停留超过 deadline，未完成推理即超时。"
            : $"InferenceSchedulerRequestDeadline：请求在调度器内停留超过 deadline" +
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

    private static BatchInferenceResult BuildWorkerCrashResult(Exception ex) => new()
    {
        Outputs = Array.Empty<InferenceOutput>(),
        Succeeded = false,
        Error = $"InferenceSchedulerWorkerCrash：执行 worker 异常：{ex.GetType().Name}: {ex.Message}",
        Duration = TimeSpan.Zero
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 通知 Coordinator 停止。
        _stopCts.Cancel();
        _channel.Writer.TryComplete();

        // 等待 Coordinator 退出（退出时会失败 channel 中剩余请求）。
        if (_workerTask is not null)
        {
            try
            {
                await _workerTask.ConfigureAwait(false);
            }
            catch
            {
                // best-effort：Coordinator 异常已在其内部处理。
            }
        }

        // 等待所有在飞 Execution Worker 完成（stopToken 已取消，它们会失败各自批次）。
        var outstanding = SnapshotOutstanding();
        if (outstanding.Length > 0)
        {
            try
            {
                await Task.WhenAll(outstanding).ConfigureAwait(false);
            }
            catch
            {
                // best-effort：各 worker 异常已在其内部处理。
            }
        }

        // 兜底：再次失败所有未读请求。
        DrainPendingRequestsOnShutdown();

        _concurrencyLimiter.Dispose();
        _stopCts.Dispose();
    }

    /// <summary>
    /// BatchKey：判定请求能否合并到同一微批。不同 BatchKey 的请求不能合并（避免特征列错位）。
    /// </summary>
    /// <remarks>
    /// ModelGeneration / ExecutionProvider 在调度器包裹单一内部引擎时为常量
    /// （接口未暴露 EP，模型代际在调度器生命周期内不变），不影响分组正确性。
    /// SchemaVersion 为 string（与 <see cref="FeatureBatch.SchemaVersion"/> 一致）。
    /// </remarks>
    private sealed record InferenceBatchKey(
        long ModelGeneration,
        string SchemaVersion,
        int FeatureNamesHash,
        int FeatureCount,
        string ExecutionProvider);

    /// <summary>
    /// 攒批缓冲：同一 BatchKey 的请求聚合，按 row 数累计，窗口到期或攒满后派发。
    /// </summary>
    private sealed class PendingGroup
    {
        public required InferenceBatchKey Key { get; init; }
        public IReadOnlyList<string> FeatureNames { get; init; } = Array.Empty<string>();
        public List<InferenceRequest> Requests { get; } = new();
        public int TotalRows;
        public required DateTimeOffset WindowDeadline { get; init; }
    }

    /// <summary>
    /// 内部推理请求载体：携带 FeatureBatch、deadline 与 TaskCompletionSource。
    /// deadline 使用 Stopwatch 单调时钟（DeadlineTimestamp）避免 wall clock 漂移；
    /// 保留 Deadline (DateTimeOffset) 仅用于错误消息显示。
    /// CapturedLease 在入队时捕获当前 Active Engine 引用（含引用计数），
    /// 确保请求在捕获的世代上执行，避免热切换后 cross-generation execution。
    /// </summary>
    private sealed class InferenceRequest
    {
        public required FeatureBatch Batch { get; init; }
        /// <summary>请求截止时间（Stopwatch 时间戳，单调时钟）。Stopwatch.GetTimestamp() ≥ 此值即过期。</summary>
        public required long DeadlineTimestamp { get; init; }
        /// <summary>请求截止时间（wall clock，仅用于错误消息显示，不参与判断）。</summary>
        public required DateTimeOffset Deadline { get; init; }
        public required CancellationToken CancellationToken { get; init; }
        public required TaskCompletionSource<BatchInferenceResult> Completion { get; init; }
        /// <summary>
        /// 外部 ct 取消注册句柄。请求完成（成功/失败/取消）后必须 Dispose，
        /// 否则 ct 永远存活时注册回调也永远存活（内存泄漏）。
        /// </summary>
        public CancellationTokenRegistration CancellationRegistration;
        /// <summary>
        /// 入队时捕获的引擎租约。
        /// _inner 为 IModelActivationManager 时永远非 null：
        ///   - 已激活：Active Engine 租约（引用计数，Dispose 递减）；
        ///   - 未激活：fallback 永久租约（Generation=0，Dispose 为 no-op）。
        /// _inner 非 IModelActivationManager 时为 null（执行时回退到 _inner）。
        /// 执行时使用 lease.Engine 而非 _inner，确保请求在入队时的世代上执行。
        /// lease 由内部执行出口（FinalizeRequest）释放，与调用方 Task 生命周期解耦。
        /// </summary>
        public IInferenceEngineLease? CapturedLease;

        /// <summary>
        /// 调用方取消标志（1 = 已被外部 ct 取消）。
        /// 外部取消回调设置此标志并完成 Caller 的 TaskCompletionSource（TrySetCanceled），
        /// 但不释放 Engine Lease —— lease 由内部执行出口（FinalizeRequest）释放。
        /// 内部执行检查此标志（或 Completion.Task.IsCompleted）：若已取消，跳过引擎调用并释放 lease。
        /// </summary>
        public int CallerCancelled;
    }
}
