using System.Diagnostics;
using ContextCore.Abstractions;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// R29 WP-A-2：OnnxInferenceEngine — IBatchInferenceEngine 的 ONNX 实现
//
// 目标：
//   1. 把 IOnnxInferenceSession 适配为 IBatchInferenceEngine，让 ContextCore
//      既有评分链路（DefaultContextDecisionEngine / Scorer 等）能直接消费 ONNX 真实模型。
//   2. 对外暴露 ModelVersion / Kind / ContentHash / CalibrationVersion 元数据，
//      与 DeterministicBatchInferenceEngine 实现契约一致；Kind = RealModel。
//   3. 同时支持字典路径（InferAsync）与连续内存路径（InferBatchAsync）：
//      - InferBatchAsync：直接转发到 session，零拷贝。
//      - InferAsync：将 FeatureVector.Values 字典转为连续 float 内存后调用 InferBatchAsync。
//   4. 超时控制：构造 CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts)
//      并在请求的 TimeoutMs 或 options.InferenceTimeoutMs 中取较小者作为硬超时。
//
// 设计原则：
//   1. 单例：构造时加载会话，所有推理请求复用同一 session；OnnxRuntime session.Run 线程安全。
//   2. CalibrationVersion 来自外部注入（ICalibrationService 或 ModelArtifactDescriptor）；
//      引擎本身不持久化校准参数。
//   3. 异常隔离：session 抛出的非取消异常被捕获并转为 Succeeded=false 的结果，
//      与 DeterministicBatchInferenceEngine 行为一致；OperationCanceledException 不被捕获
//      （与项目内存约束一致：Authoritative Runtime 不捕获 OperationCanceledException）。
// ===========================================================================

/// <summary>
/// R29 WP-A-2：基于 ONNX Runtime 的 IBatchInferenceEngine 实现。
/// </summary>
/// <remarks>
/// <b>构造</b>：
/// <code>
/// var factory = new OnnxRuntimeInferenceSessionFactory();
/// var session = await factory.CreateAsync(options, descriptor);
/// var engine = new OnnxInferenceEngine(session, options, calibrationVersion: "platt-v1");
/// </code>
/// <para>
/// <b>CalibrationVersion</b>：默认 "default-v1"；真实生产路径应在构造时传入
/// 与 ICalibrationService 中已注册的参数版本号一致的字符串。
/// </para>
/// </remarks>
public sealed class OnnxInferenceEngine : IBatchInferenceEngine
{
    private readonly IOnnxInferenceSession _session;
    private readonly OnnxInferenceEngineOptions _options;
    private readonly string _calibrationVersion;

    // P3 步骤4：warmup 状态标志。
    // 0 = 未 warmup；1 = 已 warmup。
    // 子问题2修复：warmup 失败时重置为 0，允许后续重试（不再永久标记为已 warmup）。
    // 使用 Interlocked 实现无锁 idempotent warmup，避免首次推理时的并发重复 warmup。
    private int _warmedUp;

    // 子问题4：并发推理槽位（SemaphoreSlim），限制同时调用 session.Run 的请求数。
    // 容量 = MaxConcurrentInferences（默认 = Environment.ProcessorCount）。
    // G8：构造时若 CpuOversubscriptionGuard=true，按 IntraOpNumThreads 收缩以避免过度订阅。
    private readonly SemaphoreSlim _inferenceSlots;

    // G8：等待推理槽位的排队计数（在 SemaphoreSlim 上等待但尚未获取的请求数）。
    // 用于实现 BatchQueueCapacity 限制：超过则立即拒绝，避免无限排队。
    // -1 表示 BatchQueueCapacity=0（不限制），跳过计数逻辑。
    private readonly int _batchQueueCapacity;
    private int _waitingCount;

    // 子问题4：熔断器状态。
    // _consecutiveFailures：连续超时/失败次数；达到 CircuitBreakerThreshold 后打开熔断器。
    // _circuitBreakerUntilUtc：熔断器打开状态下的恢复时间戳（半开探测在此时间后允许）。
    private int _consecutiveFailures;
    private long _circuitBreakerUntilUtcTicks;

    // 子问题7：孤儿推理任务计数（已超时但 native session.Run 仍在后台运行的任务数）。
    // native session.Run 是同步 native 调用，无法被 CancellationToken 中断；超时后槽位已释放，
    // 但孤儿 native 调用仍占用 ORT 线程池线程。若上游持续涌入请求，孤儿任务会累积并耗尽 ORT 线程池。
    // 通过计数实现 back-pressure：当孤儿数达 _maxOrphanedInferences 时新请求立即被拒绝。
    // -1 表示不限制（MaxOrphanedInferences=0，向后兼容）。
    private readonly int _maxOrphanedInferences;
    private int _orphanedTaskCount;

    /// <summary>
    /// 构造 OnnxInferenceEngine。
    /// </summary>
    /// <param name="session">已加载的 ONNX 推理会话（由 IOnnxInferenceSessionFactory 创建）。</param>
    /// <param name="options">运行时配置（用于超时与 FeatureVector → FeatureBatch 转换）。</param>
    /// <param name="calibrationVersion">校准版本号（默认 "default-v1"）。</param>
    /// <remarks>
    /// P3 步骤4：构造函数不自动执行 warmup（避免 sync-over-async）。
    /// 调用方应在构造后显式调用 <see cref="WarmupAsync(CancellationToken)"/>，
    /// 或依赖 <see cref="InferBatchAsync"/> 首次调用时的 lazy warmup（受 <see cref="OnnxInferenceEngineOptions.EnableWarmup"/> 控制）。
    /// </remarks>
    public OnnxInferenceEngine(
        IOnnxInferenceSession session,
        OnnxInferenceEngineOptions options,
        string? calibrationVersion = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        _session = session;
        _options = options;
        _calibrationVersion = string.IsNullOrWhiteSpace(calibrationVersion)
            ? "default-v1"
            : calibrationVersion;

        // 子问题4：初始化并发槽位。MaxConcurrentInferences <= 0 时使用 ProcessorCount。
        var slotCount = options.MaxConcurrentInferences > 0
            ? options.MaxConcurrentInferences
            : Environment.ProcessorCount;

        // G8：CPU 过度订阅保护。
        // 当 ASP.NET 请求并发 × IntraOpNumThreads 超过 ProcessorCount 时，ORT 线程池与
        // 请求线程争抢核心导致 P99 飙升。启用保护时收缩 slotCount，使乘积不超过核心数。
        if (options.CpuOversubscriptionGuard)
        {
            var intraOp = Math.Max(1, options.IntraOpNumThreads);
            var processorCount = Environment.ProcessorCount;
            // processorCount / intraOp 表示 ORT 在不超卖核心的前提下可同时运行的并发数。
            var safeSlots = Math.Max(1, processorCount / intraOp);
            if (slotCount > safeSlots)
            {
                slotCount = safeSlots;
            }
        }

        _inferenceSlots = new SemaphoreSlim(Math.Max(1, slotCount), Math.Max(1, slotCount));

        // G8：BatchQueueCapacity<=0 表示不限制（向后兼容）。
        _batchQueueCapacity = options.BatchQueueCapacity > 0 ? options.BatchQueueCapacity : -1;
        _waitingCount = 0;

        // 子问题7：孤儿任务上限。MaxOrphanedInferences<=0 表示不限制（向后兼容）。
        _maxOrphanedInferences = options.MaxOrphanedInferences > 0 ? options.MaxOrphanedInferences : -1;
        _orphanedTaskCount = 0;
    }

    /// <inheritdoc />
    public string ModelVersion => _session.ModelVersion;

    /// <inheritdoc />
    public InferenceEngineKind Kind => InferenceEngineKind.RealModel;

    /// <inheritdoc />
    public string ContentHash => _session.ContentHash;

    /// <inheritdoc />
    public string CalibrationVersion => _calibrationVersion;

    /// <summary>
    /// 模型主输入张量是否接受 float 数据类型（委托给 <see cref="IOnnxInferenceSession.SupportsFloatInput"/>）。
    /// Golden Probe 据此决定是否执行 float warmup 验证：非 float 输入模型（如 int64 input_ids 的 embedding 模型）
    /// 跳过 float probe，避免因输入类型不匹配导致激活失败。
    /// </summary>
    internal bool SupportsFloatInput => _session.SupportsFloatInput;

    /// <inheritdoc />
    public async ValueTask<BatchInferenceResult> InferAsync(
        BatchInferenceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Inputs.Count == 0)
        {
            return new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = true,
                Error = null,
                Duration = TimeSpan.Zero
            };
        }

        // 将 FeatureVector 字典路径转为 FeatureBatch 连续内存路径，
        // 复用 InferBatchAsync 的高性能实现，避免在引擎层维护两条推理路径。
        var batch = ConvertToFeatureBatch(request);
        using var linkedCts = CreateLinkedCancellationTokenSource(request.TimeoutMs, ct);
        return await InferBatchAsync(batch, linkedCts.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        // 子问题4：熔断器检查 — 打开状态下立即短路返回失败，不再调用 session.Run。
        if (IsCircuitBreakerOpen())
        {
            return new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = "CircuitBreakerOpen：连续推理超时/失败已达阈值，已短路。",
                Duration = TimeSpan.Zero
            };
        }

        // 取消检查：预取消或 warmup 期间被取消的令牌应优雅返回失败结果，
        // 而非让下游 _inferenceSlots.WaitAsync(ct) 抛 TaskCanceledException。
        // 这覆盖两种场景：(1) 调用方预取消；(2) InferAsync 的 linkedCts 在 warmup 期间超时触发。
        if (ct.IsCancellationRequested)
        {
            return new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = "推理被取消。",
                Duration = TimeSpan.Zero
            };
        }

        // P3 步骤4：lazy warmup（若 EnableWarmup=true 且尚未 warmup）。
        // 子问题2修复：warmup 失败时 EnsureWarmedUpAsync 内部已重置 _warmedUp=0，允许后续重试。
        if (_options.EnableWarmup)
        {
            await EnsureWarmedUpAsync(ct).ConfigureAwait(false);
        }

        // 空批次直接返回成功（与 session 行为一致）。
        if (batch.RowCount == 0)
        {
            return new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = true,
                Error = null,
                Duration = TimeSpan.Zero
            };
        }

        // P3 步骤3：Large batch splitting — 当 RowCount 超过 MaxBatchSize 时分片执行。
        // 这避免 large batch 一次性加载到 GPU 显存导致 OOM。
        // MaxBatchSize=0 表示不限制（默认）。
        if (_options.MaxBatchSize > 0 && batch.RowCount > _options.MaxBatchSize)
        {
            return await InferBatchWithSplittingAsync(batch, ct).ConfigureAwait(false);
        }

        // warmup 可能耗时较长（首次 graph optimization），期间 InferAsync 的 linkedCts 可能因
        // request.TimeoutMs 到期而被触发；进入槽位前再次校验，避免 _inferenceSlots.WaitAsync(ct)
        // 抛 TaskCanceledException（调用方期望优雅的失败结果而非异常）。
        if (ct.IsCancellationRequested)
        {
            return new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = "推理被取消。",
                Duration = TimeSpan.Zero
            };
        }

        // 子问题4：经并发槽位 + 超时 watchdog 路径执行单次推理。
        return await ExecuteWithSlotAndTimeoutAsync(batch, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 子问题4：在并发槽位 + 超时 watchdog 下执行一次 session 推理。
    /// session.Run 是同步 native 调用，无法被 CancellationToken 中断；本方法：
    ///   1. 通过 SemaphoreSlim 限制并发数（防止打满 ORT 线程池）；
    ///   2. 通过 Task.Run 把同步调用 offload 到线程池，避免阻塞主线程；
    ///   3. 通过 Task.WaitAsync(TimeSpan) 实现超时；超时后释放槽位让其他请求继续，
    ///      native 调用仍在后台运行（无法中断）。
    ///   4. 超时累计达 CircuitBreakerThreshold 后打开熔断器。
    /// G8：在 WaitAsync 之前用 _waitingCount 实现 bounded queue；超过 BatchQueueCapacity
    ///   立即返回 QueueFull 失败，避免在过载场景下请求无限期堆积。
    /// 子问题7：超时产生的孤儿 native 调用通过 _orphanedTaskCount 计数；当达
    ///   MaxOrphanedInferences 时新请求立即返回 NativePoolSaturated 失败（back-pressure），
    ///   防止孤儿任务耗尽 ORT 线程池导致全局推理雪崩。
    /// </summary>
    /// <param name="batch">单批次特征数据（已被分片逻辑切分到合理大小）。</param>
    /// <param name="ct">取消令牌（用于 WaitAsync 与 session 入口检查）。</param>
    /// <returns>推理结果；超时/失败时返回 Succeeded=false 的结果（不抛异常，除非 ct 取消）。</returns>
    private async ValueTask<BatchInferenceResult> ExecuteWithSlotAndTimeoutAsync(
        FeatureBatch batch,
        CancellationToken ct)
    {
        // P5：阶段计时 — 方法入口时间戳（Queue 阶段起点）
        var methodEntryAt = Stopwatch.GetTimestamp();

        // 子问题7：back-pressure — 当孤儿任务（已超时但 native session.Run 仍在后台运行）
        // 达到上限时立即拒绝，不再进入 session.Run。
        // native 调用无法被中断，孤儿任务会持续占用 ORT 线程池线程；若不限制，
        // 上游持续涌入的请求会让孤儿任务累积并耗尽 ORT 线程池，导致全局推理雪崩。
        // _maxOrphanedInferences=-1 表示不限制（向后兼容 MaxOrphanedInferences=0）。
        if (_maxOrphanedInferences > 0
            && Volatile.Read(ref _orphanedTaskCount) >= _maxOrphanedInferences)
        {
            return new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = $"NativePoolSaturated：已超时但仍在后台运行的孤儿推理任务达上限" +
                        $"({_maxOrphanedInferences})，native 线程池可能已饱和，请稍后重试。",
                Duration = TimeSpan.Zero
            };
        }

        // G8：bounded queue — 在等待槽位前先校验排队容量。
        // _batchQueueCapacity=-1 表示不限制（向后兼容 BatchQueueCapacity=0 行为）。
        // 否则用 Interlocked.Increment 原子抢占一个等待位；若超过容量立即回退并拒绝。
        if (_batchQueueCapacity > 0)
        {
            var waiting = Interlocked.Increment(ref _waitingCount);
            if (waiting > _batchQueueCapacity)
            {
                Interlocked.Decrement(ref _waitingCount);
                return new BatchInferenceResult
                {
                    Outputs = Array.Empty<InferenceOutput>(),
                    Succeeded = false,
                    Error = $"QueueFull：等待推理槽位的请求已达上限({_batchQueueCapacity})，" +
                            $"请稍后重试或降低上游并发。",
                    Duration = TimeSpan.Zero
                };
            }
        }

        try
        {
            await _inferenceSlots.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // 一旦获取到槽位（或被取消/抛异常），就不再占用等待位。
            // 后续 ExecuteWithSlotAndTimeoutAsync 调用方仍由 _inferenceSlots.Release() 控制并发。
            if (_batchQueueCapacity > 0)
            {
                Interlocked.Decrement(ref _waitingCount);
            }
        }

        var slotReleased = false;
        var startedAt = Stopwatch.GetTimestamp();
        var timeoutMs = _options.InferenceTimeoutMs;

        // P5：阶段计时 — Queue 阶段完成（从方法入口到获取槽位）
        ReportPhaseTiming(InferencePhase.Queue, methodEntryAt, startedAt);

        try
        {
            // P5：阶段计时 — Copy 阶段（槽位获取到 Task.Run 前；FeatureBatch 已是连续内存，通常极短）
            var runStartedAt = Stopwatch.GetTimestamp();
            ReportPhaseTiming(InferencePhase.Copy, startedAt, runStartedAt);

            // Task.Run 把同步 native 调用 offload 到线程池；CancellationToken.None 防止 Task.Run 自身取消
            // （ct 已传入 session.InferBatchAsync，由其入口检查 IsCancellationRequested）。
            var inferenceTask = Task.Run(
                () => _session.InferBatchAsync(batch, ct).AsTask(),
                CancellationToken.None);

            BatchInferenceResult result;
            try
            {
                result = timeoutMs > 0
                    ? await inferenceTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None).ConfigureAwait(false)
                    : await inferenceTask.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // P5：阶段计时 — Run 阶段（超时路径：run 持续到超时）
                var timeoutRunCompletedAt = Stopwatch.GetTimestamp();
                ReportPhaseTiming(InferencePhase.Run, runStartedAt, timeoutRunCompletedAt);
                // 子问题4：超时后释放槽位，让其他请求继续；native 调用仍在后台运行（无法中断）。
                slotReleased = true;
                _inferenceSlots.Release();

                // 累计连续失败，达到阈值时打开熔断器。
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                if (_options.CircuitBreakerThreshold > 0
                    && failures >= _options.CircuitBreakerThreshold)
                {
                    OpenCircuitBreaker();
                }

                // 子问题7：记录孤儿任务并在其最终完成时递减计数，实现 back-pressure。
                // native session.Run 无法被中断，孤儿任务会继续占用 ORT 线程池线程直到完成或抛异常。
                // 通过 _orphanedTaskCount 计数，当达 _maxOrphanedInferences 时新请求被拒绝。
                // 无论孤儿任务最终 RanToCompletion / Faulted / Canceled，都递减计数并观察异常
                // （访问 t.Exception 标记为已观察，避免 UnobservedTaskException）。
                if (_maxOrphanedInferences > 0)
                {
                    Interlocked.Increment(ref _orphanedTaskCount);
                    _ = inferenceTask.ContinueWith(
                        t =>
                        {
                            Interlocked.Decrement(ref _orphanedTaskCount);
                            _ = t.Exception; // 标记异常为已观察
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }
                else
                {
                    // 不限制孤儿数时仍观察异常，避免 UnobservedTaskException。
                    _ = inferenceTask.ContinueWith(
                        t => { _ = t.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted);
                }

                return new BatchInferenceResult
                {
                    Outputs = Array.Empty<InferenceOutput>(),
                    Succeeded = false,
                    Error = $"InferenceTimeout：推理在 {timeoutMs}ms 内未完成；native 调用仍在后台运行，槽位已释放。",
                    Duration = Stopwatch.GetElapsedTime(startedAt)
                };
            }

            // P5：阶段计时 — Run 阶段完成（成功路径：await 返回）
            var runCompletedAt = Stopwatch.GetTimestamp();
            ReportPhaseTiming(InferencePhase.Run, runStartedAt, runCompletedAt);

            // 推理本身成功（result.Succeeded=true）→ 重置连续失败计数。
            // 推理报告失败（result.Succeeded=false，如 ORT 内部异常）→ 不重置，但不立即触发熔断
            // （仅超时累计触发；业务失败由调用方降级处理）。
            if (result.Succeeded)
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }

            // P5：阶段计时 — Parse 阶段（结果处理；通常极短）
            var parseCompletedAt = Stopwatch.GetTimestamp();
            ReportPhaseTiming(InferencePhase.Parse, runCompletedAt, parseCompletedAt);
            return result;
        }
        finally
        {
            if (!slotReleased)
            {
                _inferenceSlots.Release();
            }
        }
    }

    /// <summary>
    /// P5：阶段耗时回调（仅在 InferencePhaseTimingCallback 非空时调用）。
    /// 在推理热路径上同步执行，回调实现应避免锁竞争与 IO（建议 DDSketch.Add 或 channel 写入）。
    /// </summary>
    /// <param name="phase">推理阶段。</param>
    /// <param name="start">起始时间戳（Stopwatch.GetTimestamp()）。</param>
    /// <param name="end">结束时间戳（Stopwatch.GetTimestamp()）。</param>
    private void ReportPhaseTiming(InferencePhase phase, long start, long end)
    {
        if (_options.InferencePhaseTimingCallback is { } callback)
        {
            callback(phase, Stopwatch.GetElapsedTime(start, end));
        }
    }

    /// <summary>
    /// 子问题4：熔断器是否处于打开状态。
    /// 打开后经过 CircuitBreakerResetMs 时间进入"半开"状态（返回 false），允许一次探测请求通过。
    /// </summary>
    private bool IsCircuitBreakerOpen()
    {
        if (_options.CircuitBreakerThreshold <= 0)
        {
            return false;
        }

        var untilTicks = Interlocked.Read(ref _circuitBreakerUntilUtcTicks);
        if (untilTicks == 0)
        {
            return false;
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        if (nowTicks >= untilTicks)
        {
            // 进入半开状态：允许通过；不清零 untilTicks，由成功路径的 ResetCircuitBreaker 清零。
            return false;
        }

        return true;
    }

    /// <summary>子问题4：打开熔断器，记录恢复时间戳。</summary>
    private void OpenCircuitBreaker()
    {
        var resetAt = DateTime.UtcNow.AddMilliseconds(
            _options.CircuitBreakerResetMs > 0 ? _options.CircuitBreakerResetMs : 30000);
        Interlocked.Exchange(ref _circuitBreakerUntilUtcTicks, resetAt.Ticks);
    }

    /// <summary>子问题4：关闭熔断器（探测请求成功后调用）。</summary>
    private void ResetCircuitBreaker()
    {
        Interlocked.Exchange(ref _circuitBreakerUntilUtcTicks, 0);
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    /// <summary>
    /// P3 步骤4：执行 warmup（idempotent，可安全多次调用）。
    /// 用一个 1 行全 0 的 dummy FeatureBatch 调用一次 session.InferBatchAsync，
    /// 让 ORT 完成 graph optimization 与内存分配，避免首次真实推理的冷启动延迟。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>
    /// 子问题2修复：warmup 失败（非取消异常）时重置 _warmedUp=0，允许后续重试，
    /// 不再永久标记为已 warmup。真实推理若仍失败，由 ExecuteWithSlotAndTimeoutAsync 的失败路径处理。
    /// <see cref="OperationCanceledException"/> 会被传播（尊重调用方取消意图）。
    /// </remarks>
    public async ValueTask WarmupAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _warmedUp, 1, 0) != 0)
        {
            return; // 已 warmup 或正在 warmup（由其他线程完成）
        }

        try
        {
            // dummy batch：1 行 × 1 列全 0，最小化 warmup 开销。
            // 目的不是验证模型输出正确性，而是触发 ORT 的 graph optimization 与首次内存分配。
            var dummyBatch = new FeatureBatch
            {
                SchemaVersion = "warmup",
                Values = new float[] { 0f },
                RowCount = 1,
                FeatureCount = 1,
                FeatureNames = new[] { "warmup" }
            };
            await _session.InferBatchAsync(dummyBatch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消时重置标志，允许后续重试 warmup
            Interlocked.Exchange(ref _warmedUp, 0);
            throw;
        }
        catch (Exception)
        {
            // 子问题2修复：warmup 失败时重置标志为 0，允许后续重试。
            // 之前的行为是保持 _warmedUp=1（永不重试），导致模型恢复后仍无法重新 warmup。
            Interlocked.Exchange(ref _warmedUp, 0);
        }
    }

    /// <summary>
    /// 子问题2：用指定 FeatureBatch 执行 warmup（idempotent），返回推理结果供调用方做 Golden Probe 验证。
    /// 用于 ModelActivationManager 在激活前验证模型可用性：传入 schema.FeatureCount 宽度的全 0 batch。
    /// 成功时 _warmedUp=1，后续 InferBatchAsync 跳过 lazy warmup。
    /// 失败时（非取消异常）_warmedUp=0，允许重试。
    /// </summary>
    /// <param name="warmupBatch">warmup 用的特征批次（通常 1 行 × FeatureCount 列全 0）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>推理结果（供 Golden Probe 验证 Output count / 有限性 / confidence 范围）。</returns>
    public async ValueTask<BatchInferenceResult> WarmupAsync(FeatureBatch warmupBatch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(warmupBatch);

        if (Interlocked.CompareExchange(ref _warmedUp, 1, 0) != 0)
        {
            // 已 warmup：直接执行推理返回结果（用于 Golden Probe 重新验证）。
            return await _session.InferBatchAsync(warmupBatch, ct).ConfigureAwait(false);
        }

        try
        {
            return await _session.InferBatchAsync(warmupBatch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _warmedUp, 0);
            throw;
        }
        catch (Exception)
        {
            // warmup 失败：重置标志，允许重试。
            Interlocked.Exchange(ref _warmedUp, 0);
            throw;
        }
    }

    /// <summary>
    /// P3 步骤4：lazy warmup 内部实现。仅在首次调用时执行 warmup，后续调用直接返回。
    /// </summary>
    private async ValueTask EnsureWarmedUpAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _warmedUp) != 0)
        {
            return;
        }
        await WarmupAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// P3 步骤3：按 MaxBatchSize 分片执行推理，合并各片输出。
    /// 子问题4：每片通过 ExecuteWithSlotAndTimeoutAsync 执行，复用并发槽位与超时 watchdog。
    /// </summary>
    /// <param name="batch">原始大批量特征数据。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>合并后的推理结果（输出顺序与输入行一致）。</returns>
    /// <remarks>
    /// 分片策略：按 MaxBatchSize 切分 batch.Values 为多个连续切片，每片独立调用 ExecuteWithSlotAndTimeoutAsync。
    /// 任一片失败或超时则整体返回失败（Error 含失败片索引）。
    /// 合并各片 Outputs 到结果列表，累计 Duration。
    /// </remarks>
    private async ValueTask<BatchInferenceResult> InferBatchWithSplittingAsync(
        FeatureBatch batch,
        CancellationToken ct)
    {
        var maxBatchSize = _options.MaxBatchSize;
        var featureCount = batch.FeatureCount;
        var totalRows = batch.RowCount;
        var allOutputs = new List<InferenceOutput>(totalRows);
        var totalDuration = TimeSpan.Zero;
        var startedAt = Stopwatch.GetTimestamp();

        for (var rowOffset = 0; rowOffset < totalRows; rowOffset += maxBatchSize)
        {
            var chunkRows = Math.Min(maxBatchSize, totalRows - rowOffset);
            var chunkValues = batch.Values.Slice(
                rowOffset * featureCount,
                chunkRows * featureCount);

            var chunkBatch = new FeatureBatch
            {
                SchemaVersion = batch.SchemaVersion,
                Values = chunkValues,
                RowCount = chunkRows,
                FeatureCount = featureCount,
                FeatureNames = batch.FeatureNames
            };

            // 子问题4：每片走 ExecuteWithSlotAndTimeoutAsync，复用并发槽位 + 超时 + 熔断。
            BatchInferenceResult chunkResult;
            try
            {
                chunkResult = await ExecuteWithSlotAndTimeoutAsync(chunkBatch, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            if (!chunkResult.Succeeded)
            {
                return new BatchInferenceResult
                {
                    Outputs = allOutputs,
                    Succeeded = false,
                    Error = $"分片推理报告失败（rowOffset={rowOffset}, chunkRows={chunkRows}）：{chunkResult.Error}",
                    Duration = Stopwatch.GetElapsedTime(startedAt)
                };
            }

            allOutputs.AddRange(chunkResult.Outputs);
            totalDuration += chunkResult.Duration;
        }

        return new BatchInferenceResult
        {
            Outputs = allOutputs,
            Succeeded = true,
            Error = null,
            Duration = totalDuration
        };
    }

    private static FeatureBatch ConvertToFeatureBatch(BatchInferenceRequest request)
    {
        var inputs = request.Inputs;
        var rowCount = inputs.Count;

        // 收集所有特征名（保持稳定顺序：以第一条输入的 key 序列为基准，缺失补默认值）。
        var featureNames = inputs[0].Values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var featureCount = featureNames.Length;

        var values = new float[rowCount * featureCount];
        for (var row = 0; row < rowCount; row++)
        {
            var vector = inputs[row].Values;
            var offset = row * featureCount;
            for (var col = 0; col < featureCount; col++)
            {
                values[offset + col] = ConvertToFloat(vector[featureNames[col]]);
            }
        }

        return new FeatureBatch
        {
            SchemaVersion = inputs[0].SchemaVersion,
            Values = values,
            RowCount = rowCount,
            FeatureCount = featureCount,
            FeatureNames = featureNames
        };
    }

    private static float ConvertToFloat(object? value)
    {
        return value switch
        {
            null => 0f,
            float f => f,
            double d => (float)d,
            int i => i,
            long l => l,
            bool b => b ? 1f : 0f,
            string s when float.TryParse(s, out var parsed) => parsed,
            _ => 0f
        };
    }

    private CancellationTokenSource CreateLinkedCancellationTokenSource(int requestTimeoutMs, CancellationToken ct)
    {
        var effectiveTimeoutMs = Math.Min(
            requestTimeoutMs > 0 ? requestTimeoutMs : _options.InferenceTimeoutMs,
            _options.InferenceTimeoutMs);

        if (effectiveTimeoutMs <= 0)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(effectiveTimeoutMs));
        return cts;
    }
}
