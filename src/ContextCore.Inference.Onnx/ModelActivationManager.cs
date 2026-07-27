using System.Diagnostics;
using ContextCore.Abstractions;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// P0-7 / P0-8：Model Activation Manager 实现
//
// 目标：
//   1. 权威编排模型激活流程：IModelArtifactRegistry 读取 descriptor
//      → ICalibrationValidator 验证校准参数（P0-8）
//      → IFeatureSchemaValidator 验证 schema 存在性（P0-8）
//      → IOnnxInferenceSessionFactory 创建 session
//      → OnnxInferenceEngine 激活。
//   2. 作为 IBatchInferenceEngine 代理：激活前委托给 fallback（Deterministic），
//      激活后委托给 OnnxInferenceEngine，让消费方无需感知激活切换。
//   3. 线程安全：ActivateAsync 可在运行时调用，通过 lock 原子切换引擎。
//
// 设计原则：
//   1. fail-safe：激活失败不影响现有推理（继续使用 fallback）。
//   2. 校准验证不通过时拒绝激活（Error 级违规）；Warning 级违规允许激活。
//   3. schema 不存在时拒绝激活（防止推理时 schema drift）。
//   4. 不捕获 OperationCanceledException（与项目约束一致）。
// ===========================================================================

/// <summary>
/// P0-7：权威模型激活管理器实现。
/// 编排模型工件加载 → 验证 → ONNX 引擎激活的完整流程。
/// </summary>
/// <remarks>
/// 同时实现 <see cref="IBatchInferenceEngine"/> 作为代理：
/// 未激活时委托给 fallback（通常为 DeterministicBatchInferenceEngine），
/// 激活后委托给 OnnxInferenceEngine。
/// </remarks>
public sealed class ModelActivationManager : IModelActivationManager
{
    private readonly IModelArtifactRegistry _registry;
    private readonly ICalibrationValidator _calibrationValidator;
    private readonly IFeatureRegistry _featureRegistry;
    private readonly IOnnxInferenceSessionFactory _sessionFactory;
    private readonly IFallbackInferenceEngine _fallbackEngine;
    private readonly ICalibrationService? _calibrationService;

    // 激活状态：通过 lock 保护读写，确保原子切换。
    private readonly object _activationLock = new();
    private volatile IBatchInferenceEngine? _activeEngine;
    private volatile ModelArtifactDescriptor? _activeDescriptor;

    // 子问题3：热切换时旧引擎放入 _previousEngine，后台延迟 Dispose（等待 in-flight 请求完成）。
    // 同一时间至多一个 _previousEngine；新的激活会把当前 _activeEngine 移到 _previousEngine，
    // 并立即 Dispose 更早的 _previousEngine（其 grace period 已超过）。
    private volatile IBatchInferenceEngine? _previousEngine;

    // 子问题6：引用计数 — 每个 engine 关联一个 InflightCounter，跟踪该 engine 上的 in-flight 请求数。
    // 请求在捕获 engine 引用时同时捕获 counter 引用，确保热切换后旧请求递减的是旧 counter（而非新 counter）。
    // Dispose 任务等待旧 counter 归零后才释放旧引擎，避免 in-flight 请求触发 ObjectDisposedException。
    private InflightCounter _activeCounter = new();
    private InflightCounter? _previousCounter;

    /// <summary>
    /// 构造 ModelActivationManager。
    /// </summary>
    /// <param name="registry">模型工件注册表（从中读取 descriptor）。</param>
    /// <param name="calibrationValidator">校准参数验证器（P0-8：加载时验证校准有效性）。</param>
    /// <param name="featureRegistry">特征注册表（从中查询 schema；P0-8：加载时验证 schema 存在性）。</param>
    /// <param name="sessionFactory">ONNX session 工厂（创建推理会话）。</param>
    /// <param name="fallbackEngine">降级引擎（未激活时使用，通常为 DeterministicBatchInferenceEngine）。
    /// 子问题1：通过 IFallbackInferenceEngine 标记接口注入，避免 DI 容器解析 IBatchInferenceEngine
    /// 时回到 ModelActivationManager 自身（循环依赖）。</param>
    /// <param name="calibrationService">可选的校准服务（提供待验证的校准参数；为 null 时跳过校准验证）。</param>
    public ModelActivationManager(
        IModelArtifactRegistry registry,
        ICalibrationValidator calibrationValidator,
        IFeatureRegistry featureRegistry,
        IOnnxInferenceSessionFactory sessionFactory,
        IFallbackInferenceEngine fallbackEngine,
        ICalibrationService? calibrationService = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(calibrationValidator);
        ArgumentNullException.ThrowIfNull(featureRegistry);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(fallbackEngine);

        _registry = registry;
        _calibrationValidator = calibrationValidator;
        _featureRegistry = featureRegistry;
        _sessionFactory = sessionFactory;
        _fallbackEngine = fallbackEngine;
        _calibrationService = calibrationService;
    }

    /// <inheritdoc />
    public IBatchInferenceEngine? ActiveEngine => _activeEngine;

    /// <inheritdoc />
    public ModelArtifactDescriptor? ActiveDescriptor => _activeDescriptor;

    /// <inheritdoc />
    public string ModelVersion => (_activeEngine ?? _fallbackEngine).ModelVersion;

    /// <inheritdoc />
    public InferenceEngineKind Kind => (_activeEngine ?? _fallbackEngine).Kind;

    /// <inheritdoc />
    public string ContentHash => (_activeEngine ?? _fallbackEngine).ContentHash;

    /// <inheritdoc />
    public string CalibrationVersion => (_activeEngine ?? _fallbackEngine).CalibrationVersion;

    /// <inheritdoc />
    public async ValueTask<ModelActivationResult> ActivateAsync(
        string modelArtifactId,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelArtifactId);
        ArgumentNullException.ThrowIfNull(options);

        var descriptor = await _registry.GetAsync(modelArtifactId, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return ModelActivationResult.Failed(
                $"未在 IModelArtifactRegistry 中找到 ModelArtifactId='{modelArtifactId}'。");
        }

        return await ActivateCoreAsync(descriptor, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ModelActivationResult> ActivateLatestAsync(
        string modelName,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(options);

        var descriptor = await _registry.GetLatestAsync(modelName, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return ModelActivationResult.Failed(
                $"未在 IModelArtifactRegistry 中找到 ModelName='{modelName}' 的最新版本。");
        }

        return await ActivateCoreAsync(descriptor, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<BatchInferenceResult> InferAsync(
        BatchInferenceRequest request,
        CancellationToken ct = default)
    {
        // 子问题6：捕获当前引擎及其关联 counter，原子递增引用计数。
        // 热切换后旧请求仍递减旧 counter（counter 引用在请求开始时已捕获），确保 Dispose 任务能精确等待。
        // activeEngine 为 null 时使用 fallback（fallback 永不 Dispose，无需计数）。
        var engine = _activeEngine;
        if (engine is null)
        {
            return await _fallbackEngine.InferAsync(request, ct).ConfigureAwait(false);
        }

        var counter = _activeCounter;
        counter.Increment();
        try
        {
            return await engine.InferAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            counter.Decrement();
        }
    }

    /// <inheritdoc />
    public async ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken ct = default)
    {
        // 子问题6：捕获当前引擎及其关联 counter，原子递增引用计数。
        var engine = _activeEngine;
        if (engine is null)
        {
            return await _fallbackEngine.InferBatchAsync(batch, ct).ConfigureAwait(false);
        }

        var counter = _activeCounter;
        counter.Increment();
        try
        {
            return await engine.InferBatchAsync(batch, ct).ConfigureAwait(false);
        }
        finally
        {
            counter.Decrement();
        }
    }

    // -----------------------------------------------------------------------
    // 激活核心流程：descriptor → 校准验证 → schema 验证 → session 创建 → 引擎切换
    // -----------------------------------------------------------------------

    private async ValueTask<ModelActivationResult> ActivateCoreAsync(
        ModelArtifactDescriptor descriptor,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();

        // P0-8 Step 1：校准验证（仅当 ICalibrationService 可用且 descriptor 引用了校准版本时）
        CalibrationValidationResult? calResult = null;
        if (_calibrationService is not null)
        {
            var parameters = _calibrationService.GetParameters(descriptor.ModelArtifactId)
                ?? _calibrationService.GetParameters(descriptor.ModelName);
            if (parameters is not null)
            {
                calResult = _calibrationValidator.Validate(parameters, descriptor.ModelArtifactId);
                if (!calResult.IsValid)
                {
                    return ModelActivationResult.Failed(
                        $"校准验证失败：{calResult.Error}",
                        descriptor,
                        calResult,
                        schemaError: null);
                }
            }
        }

        // P0-8 Step 2：schema 存在性验证（descriptor.FeatureSchemaVersion 必须在 IFeatureRegistry 中已注册）
        var schema = _featureRegistry.Get(descriptor.FeatureSchemaVersion);
        if (schema is null)
        {
            var schemaError = $"特征 schema 版本 '{descriptor.FeatureSchemaVersion}' 未在 IFeatureRegistry 中注册；" +
                              "无法激活模型（推理时会触发 schema drift）。";
            return ModelActivationResult.Failed(
                schemaError,
                descriptor,
                calResult,
                schemaError);
        }

        // P0-7 Step 3：创建 ONNX session
        IOnnxInferenceSession session;
        try
        {
            session = await _sessionFactory.CreateAsync(options, descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            return ModelActivationResult.Failed(
                $"ONNX 模型文件未找到：{ex.Message}",
                descriptor,
                calResult);
        }
        catch (InvalidOperationException ex)
        {
            return ModelActivationResult.Failed(
                $"ONNX session 创建失败：{ex.Message}",
                descriptor,
                calResult);
        }
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex)
        {
            // hash 校验通过但文件非有效 ONNX（如损坏、非 ONNX 格式）时，InferenceSession 构造抛出
            // OnnxRuntimeException。将其转为 Failed 结果（错误不含 ModelFileHashMismatch），
            // 让激活流程 fail-safe（不影响现有推理），而非让异常向上传播。
            return ModelActivationResult.Failed(
                $"ONNX session 创建失败：{ex.Message}",
                descriptor,
                calResult);
        }

        // P0-7 Step 4：构造 OnnxInferenceEngine
        var engine = new OnnxInferenceEngine(
            session,
            options,
            calibrationVersion: descriptor.CalibrationVersion);

        // 子问题2：Golden Probe — 发布引擎前用 schema.FeatureCount 宽度的全 0 batch 执行一次推理，
        // 验证模型可用性（输出非空、值有限、confidence 在 [0,1]）。失败时不发布引擎，返回 Failed。
        // 同时把引擎标记为已 warmup（_warmedUp=1），后续 InferBatchAsync 跳过 lazy warmup。
        var goldenProbeError = await RunGoldenProbeAsync(engine, schema, options, cancellationToken).ConfigureAwait(false);
        if (goldenProbeError is not null)
        {
            // Golden Probe 失败：Dispose 已创建的 session/engine，避免资源泄漏。
            await SafeDisposeEngineAsync(engine).ConfigureAwait(false);
            return ModelActivationResult.Failed(
                $"Golden Probe 失败：{goldenProbeError}",
                descriptor,
                calResult);
        }

        // 子问题3：原子切换引擎并调度旧引擎延迟 Dispose。
        // 旧 _activeEngine 移到 _previousEngine；更早的 _previousEngine 立即 Dispose（grace period 已超过）。
        // 当前被替换的引擎进入 _previousEngine，等待 grace period 后由后台任务 Dispose。
        IBatchInferenceEngine? oldActive;
        IBatchInferenceEngine? oldPrevious;
        InflightCounter? oldActiveCounter;
        lock (_activationLock)
        {
            oldActive = _activeEngine;
            oldPrevious = _previousEngine;
            oldActiveCounter = _activeCounter;

            _activeEngine = engine;
            _activeDescriptor = descriptor;
            _activeCounter = new InflightCounter(); // 新引擎关联新 counter
            _previousEngine = oldActive;
            _previousCounter = oldActiveCounter; // 旧 counter 跟踪旧引擎 in-flight
        }

        // 立即 Dispose 更早的 _previousEngine（其 grace period 已超过至少一个激活周期）。
        if (oldPrevious is not null && !ReferenceEquals(oldPrevious, _fallbackEngine))
        {
            _ = Task.Run(() => SafeDisposeEngineAsync(oldPrevious));
        }

        // 调度延迟 Dispose 刚被替换的 oldActive（等待 in-flight 请求完成）。
        if (oldActive is not null && !ReferenceEquals(oldActive, _fallbackEngine))
        {
            var gracePeriodMs = options.PreviousEngineGracePeriodMs > 0
                ? options.PreviousEngineGracePeriodMs
                : 30000;
            _ = Task.Run(async () =>
            {
                try
                {
                    // 子问题6：先等待 grace period（时间兜底），再检查旧引擎引用计数。
                    // 引用计数（oldActiveCounter）精确跟踪旧引擎上的 in-flight 请求数；
                    // 即使新引擎持续接收新请求（新 counter），旧 counter 仍只递减不递增，能精确归零。
                    await Task.Delay(gracePeriodMs, CancellationToken.None).ConfigureAwait(false);

                    // 自旋等待旧 counter 归零（最多再等 gracePeriodMs，避免无限等待）。
                    var drainDeadline = Stopwatch.GetTimestamp();
                    var drainTimeoutTicks = TimeSpan.FromMilliseconds(gracePeriodMs).Ticks;
                    while (oldActiveCounter is not null && oldActiveCounter.Count > 0)
                    {
                        if (Stopwatch.GetElapsedTime(drainDeadline).Ticks > drainTimeoutTicks)
                        {
                            // 超时仍有 in-flight：best-effort Dispose（极端场景可能触发 ORT 内部异常）。
                            break;
                        }
                        await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
                    }

                    // 仅当 oldActive 仍是 _previousEngine 时 Dispose（避免误删正在使用的引擎）。
                    var currentPrevious = _previousEngine;
                    if (ReferenceEquals(currentPrevious, oldActive))
                    {
                        Interlocked.CompareExchange(ref _previousEngine, null, oldActive);
                        Interlocked.CompareExchange(ref _previousCounter, null, oldActiveCounter);
                        await SafeDisposeEngineAsync(oldActive).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // best-effort：调度失败不影响激活流程。
                }
            });
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        return ModelActivationResult.Succeeded(descriptor, engine, calResult ?? new CalibrationValidationResult
        {
            IsValid = true,
            Error = null,
            Violations = Array.Empty<CalibrationViolation>()
        });
    }

    /// <summary>
    /// 子问题2：Golden Probe — 用 schema.FeatureCount 宽度的全 0 warmup batch 调用引擎推理，
    /// 验证输出可用性。同时把引擎标记为已 warmup，跳过后续 lazy warmup。
    /// </summary>
    /// <param name="engine">待验证的 ONNX 引擎。</param>
    /// <param name="schema">模型对应的特征 schema（用于确定 warmup batch 宽度）。</param>
    /// <param name="options">引擎配置（用于 warmup 超时）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>验证失败时返回错误消息；成功时返回 null。</returns>
    private static async ValueTask<string?> RunGoldenProbeAsync(
        OnnxInferenceEngine engine,
        FeatureSchema schema,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken)
    {
        var featureCount = schema.Features.Count;
        if (featureCount <= 0)
        {
            // schema 无特征列：跳过 Golden Probe（无法构造 warmup batch），不视为失败。
            // 标记引擎为已 warmup，跳过后续 lazy warmup。
            _ = engine.WarmupAsync(cancellationToken);
            return null;
        }

        // 构造 1 行 × FeatureCount 列全 0 的 warmup batch。
        var featureNames = new string[featureCount];
        for (var j = 0; j < featureCount; j++)
        {
            featureNames[j] = schema.Features[j].Name;
        }

        var warmupBatch = new FeatureBatch
        {
            SchemaVersion = schema.Version,
            Values = new float[featureCount], // 全 0
            RowCount = 1,
            FeatureCount = featureCount,
            FeatureNames = featureNames
        };

        BatchInferenceResult probeResult;
        try
        {
            // 子问题2：调用 WarmupAsync(batch) 重载，原子设置 _warmedUp=1 并执行推理。
            probeResult = await engine.WarmupAsync(warmupBatch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"warmup 推理抛出异常：{ex.GetType().Name}: {ex.Message}";
        }

        if (!probeResult.Succeeded)
        {
            return $"warmup 推理报告失败：{probeResult.Error}";
        }

        // 验证 1：Output count > 0
        if (probeResult.Outputs.Count == 0)
        {
            return "warmup 输出为空（Outputs.Count == 0）";
        }

        // 验证 2 & 3：每个输出值有限（非 NaN/Infinity），confidence 在 [0,1]
        for (var i = 0; i < probeResult.Outputs.Count; i++)
        {
            var output = probeResult.Outputs[i];
            if (double.IsNaN(output.Score) || double.IsInfinity(output.Score))
            {
                return $"warmup 输出[{i}].Score 非有限（Score={output.Score}）";
            }
            if (double.IsNaN(output.Confidence) || double.IsInfinity(output.Confidence))
            {
                return $"warmup 输出[{i}].Confidence 非有限（Confidence={output.Confidence}）";
            }
            if (output.Confidence < 0.0 || output.Confidence > 1.0)
            {
                return $"warmup 输出[{i}].Confidence 越界 [0,1]（Confidence={output.Confidence}）";
            }
        }

        // 可选：latency threshold 检查（warmup 耗时 < 配置阈值的 2 倍，留余量）
        var latencyThresholdMs = options.InferenceTimeoutMs > 0
            ? options.InferenceTimeoutMs * 2
            : 10000;
        if (probeResult.Duration.TotalMilliseconds > latencyThresholdMs)
        {
            return $"warmup 耗时 {probeResult.Duration.TotalMilliseconds:F0}ms 超过阈值 {latencyThresholdMs}ms";
        }

        return null; // 验证通过
    }

    /// <summary>
    /// 子问题3：best-effort Dispose 引擎实例。
    /// 同时支持 IAsyncDisposable 与 IDisposable；失败时吞异常（不影响主流程）。
    /// </summary>
    /// <param name="engine">待 Dispose 的引擎。</param>
    private static async ValueTask SafeDisposeEngineAsync(IBatchInferenceEngine engine)
    {
        try
        {
            if (engine is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (engine is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // best-effort：Dispose 失败不影响激活流程。
        }
    }

    /// <summary>
    /// 子问题6：引用计数器 — 跟踪某个引擎实例上的 in-flight 推理请求数。
    /// 每个 engine 关联一个独立 counter；请求开始时捕获 counter 引用并 Increment，
    /// 结束时 Decrement 同一 counter。热切换后旧请求递减的是旧 counter（counter 引用已捕获），
    /// 新请求递增新 counter，互不干扰。Dispose 任务等待旧 counter 归零后才释放旧引擎。
    /// </summary>
    private sealed class InflightCounter
    {
        private int _count;

        /// <summary>当前 in-flight 请求数（≥0）。</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>原子递增（请求开始时调用）。</summary>
        public void Increment() => Interlocked.Increment(ref _count);

        /// <summary>原子递减（请求结束时调用，包括异常路径）。</summary>
        public void Decrement() => Interlocked.Decrement(ref _count);
    }
}
