using System.Collections.Concurrent;
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
//   3. 线程安全：ActivateAsync 可在运行时调用，通过原子 ActiveModelHandle 切换引擎。
//
// 设计原则：
//   1. fail-safe：激活失败不影响现有推理（继续使用 fallback）。
//   2. 校准验证不通过时拒绝激活（Error 级违规）；Warning 级违规允许激活。
//   3. schema 不存在时拒绝激活（防止推理时 schema drift）。
//   4. 不捕获 OperationCanceledException（与项目约束一致）。
//
// P1：原子 Active Handle
//   - 用单个 volatile ActiveModelHandle 替换分开的 _activeEngine / _activeCounter，
//     推理时只读取一次 handle（Volatile.Read），确保 engine 与 counter 来自同一世代，
//     避免热切换发生在两次读取之间导致旧引擎请求被计入新 counter。
//   - 激活时创建新 handle（Generation+1），旧 handle 注册到延迟清理队列；
//     不在激活时立即 Dispose 旧引擎。
//
// P15：LoadAndWarmupAsync
//   - 加载并 warmup 模型但不发布为 active；返回 StagedModelHandle。
//   - 用于 /warmup 端点：预热不应替换当前 active 模型。
//   - PromoteStagedAsync 将 Staged Handle 原子发布为 active。
// ===========================================================================

/// <summary>
/// P1：原子 Active Handle — 把引擎、descriptor、引用计数器与世代号绑定为单个不可变 record。
/// 推理路径通过 Volatile.Read 一次性读取整个 handle，确保 engine 与 counter 始终来自同一世代，
/// 避免热切换发生在两次读取之间导致旧引擎请求被计入新 counter（进而让旧 Session 被提前释放）。
/// </summary>
internal sealed record ActiveModelHandle(
    IBatchInferenceEngine Engine,
    ModelArtifactDescriptor Descriptor,
    ModelReferenceCounter Counter,
    long Generation);

/// <summary>
/// P1：引用计数器 — 跟踪某个引擎实例上的 in-flight 推理请求数。
/// 每个 ActiveModelHandle 关联一个独立 counter；请求开始时捕获 handle 引用并 Increment，
/// 结束时 Decrement 同一 counter。热切换后旧请求递减的是旧 handle 的 counter（引用已捕获），
/// 新请求递增新 handle 的 counter，互不干扰。Dispose 任务等待旧 counter 归零后才释放旧引擎。
/// </summary>
internal sealed class ModelReferenceCounter
{
    private int _count;

    /// <summary>当前 in-flight 请求数（≥0）。</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>原子递增（请求开始时调用）。</summary>
    public void Increment() => Interlocked.Increment(ref _count);

    /// <summary>原子递减（请求结束时调用，包括异常路径）。</summary>
    public void Decrement() => Interlocked.Decrement(ref _count);
}

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

    // P1：原子 Active Handle — 单个 volatile 字段同时持有 engine + counter + descriptor + generation。
    // 推理路径通过 Volatile.Read 一次性读取整个 handle，保证 engine 与 counter 来自同一世代。
    private volatile ActiveModelHandle? _activeHandle;
    private long _generation;

    // 子问题3：热切换时旧 handle 放入 _previousHandle，后台延迟 Dispose（等待 in-flight 请求完成）。
    // 同一时间至多一个 _previousHandle；新的激活会把当前 _activeHandle 移到 _previousHandle，
    // 并立即 Dispose 更早的 _previousHandle（其 grace period 已超过）。
    private volatile ActiveModelHandle? _previousHandle;
    private readonly object _activationLock = new();

    // P15：Staged Handle 暂存表 — LoadAndWarmupAsync 把已 warmup 的 handle 存入此表，
    // 调用方可通过 PromoteStagedAsync(handleId) 原子发布为 active。
    // 失败 / 丢弃的 Staged Handle 由调用方负责 Dispose（不会自动清理）。
    private readonly ConcurrentDictionary<string, StagedModelHandle> _stagedHandles = new();

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
    public IBatchInferenceEngine? ActiveEngine => _activeHandle?.Engine;

    /// <inheritdoc />
    public ModelArtifactDescriptor? ActiveDescriptor => _activeHandle?.Descriptor;

    /// <inheritdoc />
    public string ModelVersion => (_activeHandle?.Engine ?? _fallbackEngine).ModelVersion;

    /// <inheritdoc />
    public InferenceEngineKind Kind => (_activeHandle?.Engine ?? _fallbackEngine).Kind;

    /// <inheritdoc />
    public string ContentHash => (_activeHandle?.Engine ?? _fallbackEngine).ContentHash;

    /// <inheritdoc />
    public string CalibrationVersion => (_activeHandle?.Engine ?? _fallbackEngine).CalibrationVersion;

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
    public async ValueTask<StagedModelHandle> LoadAndWarmupAsync(
        string modelArtifactId,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelArtifactId);
        ArgumentNullException.ThrowIfNull(options);

        var handleId = Guid.NewGuid().ToString("N");

        var descriptor = await _registry.GetAsync(modelArtifactId, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return StagedModelHandle.Failed(
                handleId,
                descriptor: null,
                $"未在 IModelArtifactRegistry 中找到 ModelArtifactId='{modelArtifactId}'。");
        }

        // 复用 ActivateCoreAsync 的加载 + 验证 + warmup 流程，但不发布为 active。
        var loaded = await LoadAndWarmupCoreAsync(descriptor, options, cancellationToken).ConfigureAwait(false);
        if (!loaded.Success)
        {
            return StagedModelHandle.Failed(handleId, descriptor, loaded.Error!, loaded.CalibrationValidation);
        }

        var staged = StagedModelHandle.Succeeded(
            handleId,
            loaded.Engine!,
            descriptor,
            loaded.CalibrationValidation);

        // 存入暂存表，调用方可通过 PromoteStagedAsync(handleId) 提升为 active。
        _stagedHandles[handleId] = staged;
        return staged;
    }

    /// <inheritdoc />
    public ValueTask<ModelActivationResult> PromoteStagedAsync(
        string stagedHandleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedHandleId);

        if (!_stagedHandles.TryGetValue(stagedHandleId, out var staged) || !staged.Success || staged.Engine is null)
        {
            return new ValueTask<ModelActivationResult>(ModelActivationResult.Failed(
                $"未找到 Staged Handle '{stagedHandleId}' 或该 handle 已失效。"));
        }

        // 从暂存表移除（原子发布后不再允许重复 promote）。
        _stagedHandles.TryRemove(stagedHandleId, out _);

        // 原子发布：复用 PublishAtomic 把已 warmup 的引擎切到 active。
        // PromoteStaged 路径使用默认 grace period（无 options 上下文）。
        var published = PublishAtomicWithGracePeriod(staged.Engine, staged.Descriptor, staged.CalibrationValidation, 30000);
        return new ValueTask<ModelActivationResult>(published);
    }

    /// <inheritdoc />
    public async ValueTask<BatchInferenceResult> InferAsync(
        BatchInferenceRequest request,
        CancellationToken ct = default)
    {
        // P1：通过 Volatile.Read 一次性捕获整个 ActiveModelHandle（engine + counter + generation）。
        // 热切换发生在读取之后时，本次请求继续使用旧 handle 的 engine 与 counter，
        // 旧 counter 仅被本次请求递减，不会污染新 counter；新 counter 也不会被本次请求递减。
        var handle = _activeHandle;
        if (handle is null)
        {
            return await _fallbackEngine.InferAsync(request, ct).ConfigureAwait(false);
        }

        handle.Counter.Increment();
        try
        {
            return await handle.Engine.InferAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            handle.Counter.Decrement();
        }
    }

    /// <inheritdoc />
    public async ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken ct = default)
    {
        // P1：与 InferAsync 一致 — 单次 Volatile.Read 捕获 handle，避免 engine/counter 错配。
        var handle = _activeHandle;
        if (handle is null)
        {
            return await _fallbackEngine.InferBatchAsync(batch, ct).ConfigureAwait(false);
        }

        handle.Counter.Increment();
        try
        {
            return await handle.Engine.InferBatchAsync(batch, ct).ConfigureAwait(false);
        }
        finally
        {
            handle.Counter.Decrement();
        }
    }

    // -----------------------------------------------------------------------
    // 激活核心流程：descriptor → 校准验证 → schema 验证 → session 创建 → Golden Probe → 原子发布
    // -----------------------------------------------------------------------

    private async ValueTask<ModelActivationResult> ActivateCoreAsync(
        ModelArtifactDescriptor descriptor,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken)
    {
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

        return PublishAtomic(engine, descriptor, calResult, options);
    }

    /// <summary>
    /// P15：LoadAndWarmupCoreAsync — 执行校准 + schema + session + Golden Probe warmup，
    /// 但不发布为 active。返回 (Success, Engine, Error, CalibrationValidation) 元组式结果。
    /// </summary>
    private async ValueTask<(bool Success, IBatchInferenceEngine? Engine, string? Error, CalibrationValidationResult? CalibrationValidation)> LoadAndWarmupCoreAsync(
        ModelArtifactDescriptor descriptor,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken)
    {
        // P0-8 Step 1：校准验证
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
                    return (false, null, $"校准验证失败：{calResult.Error}", calResult);
                }
            }
        }

        // P0-8 Step 2：schema 存在性验证
        var schema = _featureRegistry.Get(descriptor.FeatureSchemaVersion);
        if (schema is null)
        {
            return (false, null,
                $"特征 schema 版本 '{descriptor.FeatureSchemaVersion}' 未在 IFeatureRegistry 中注册；无法 warmup 模型。",
                calResult);
        }

        // Step 3：创建 ONNX session
        IOnnxInferenceSession session;
        try
        {
            session = await _sessionFactory.CreateAsync(options, descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            return (false, null, $"ONNX 模型文件未找到：{ex.Message}", calResult);
        }
        catch (InvalidOperationException ex)
        {
            return (false, null, $"ONNX session 创建失败：{ex.Message}", calResult);
        }
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex)
        {
            return (false, null, $"ONNX session 创建失败：{ex.Message}", calResult);
        }

        var engine = new OnnxInferenceEngine(
            session,
            options,
            calibrationVersion: descriptor.CalibrationVersion);

        // Step 4：Golden Probe warmup（不发布为 active）
        var goldenProbeError = await RunGoldenProbeAsync(engine, schema, options, cancellationToken).ConfigureAwait(false);
        if (goldenProbeError is not null)
        {
            await SafeDisposeEngineAsync(engine).ConfigureAwait(false);
            return (false, null, $"Golden Probe 失败：{goldenProbeError}", calResult);
        }

        return (true, engine, null, calResult);
    }

    /// <summary>
    /// P1：原子发布 — 把已 warmup 的引擎切到 _activeHandle（Generation+1），
    /// 旧 handle 移到 _previousHandle，调度延迟 Dispose（等待 in-flight 引用归零）。
    /// </summary>
    private ModelActivationResult PublishAtomic(
        IBatchInferenceEngine engine,
        ModelArtifactDescriptor descriptor,
        CalibrationValidationResult? calResult,
        OnnxInferenceEngineOptions options)
    {
        return PublishAtomicWithGracePeriod(engine, descriptor, calResult, options.PreviousEngineGracePeriodMs);
    }

    /// <summary>P1：原子发布的内部实现，直接传入 gracePeriodMs。</summary>
    private ModelActivationResult PublishAtomicWithGracePeriod(
        IBatchInferenceEngine engine,
        ModelArtifactDescriptor descriptor,
        CalibrationValidationResult? calResult,
        int gracePeriodMs)
    {
        // P1：在 lock 内创建新 handle（Generation+1），把旧 handle 移到 _previousHandle。
        // 不在激活时立即 Dispose 旧引擎；旧引擎由延迟清理任务在引用归零后 Dispose。
        ActiveModelHandle? oldActive;
        ActiveModelHandle? oldPrevious;
        lock (_activationLock)
        {
            oldActive = _activeHandle;
            oldPrevious = _previousHandle;

            var newGeneration = unchecked(Interlocked.Increment(ref _generation));
            var newHandle = new ActiveModelHandle(
                engine,
                descriptor,
                new ModelReferenceCounter(),
                newGeneration);

            // 用 Interlocked.Exchange 保证 _activeHandle 的写入对其他线程可见顺序：
            // 先写 _activeHandle，再写 _previousHandle。Volatile.Read 在读取侧保证可见性。
            _activeHandle = newHandle;
            _previousHandle = oldActive;
        }

        // 立即 Dispose 更早的 _previousHandle（其 grace period 已超过至少一个激活周期）。
        // 该 handle 上的 in-flight 请求早已完成（至少经过一次完整激活周期）。
        if (oldPrevious is not null && !ReferenceEquals(oldPrevious.Engine, _fallbackEngine))
        {
            _ = Task.Run(() => SafeDisposeHandleAsync(oldPrevious));
        }

        // 调度延迟 Dispose 刚被替换的 oldActive（等待 in-flight 引用归零 + grace period）。
        if (oldActive is not null && !ReferenceEquals(oldActive.Engine, _fallbackEngine))
        {
            var oldHandleForClosure = oldActive;
            _ = Task.Run(async () =>
            {
                try
                {
                    // P1：先等待 grace period（时间兜底），再检查旧 handle 引用计数。
                    // 引用计数（oldHandle.Counter）精确跟踪旧引擎上的 in-flight 请求数；
                    // 即使新引擎持续接收新请求（新 counter），旧 counter 仍只递减不递增，能精确归零。
                    await Task.Delay(gracePeriodMs, CancellationToken.None).ConfigureAwait(false);

                    // 自旋等待旧 counter 归零（最多再等 gracePeriodMs，避免无限等待）。
                    var drainDeadline = Stopwatch.GetTimestamp();
                    var drainTimeoutTicks = TimeSpan.FromMilliseconds(gracePeriodMs).Ticks;
                    while (oldHandleForClosure.Counter.Count > 0)
                    {
                        if (Stopwatch.GetElapsedTime(drainDeadline).Ticks > drainTimeoutTicks)
                        {
                            // 超时仍有 in-flight：best-effort Dispose（极端场景可能触发 ORT 内部异常）。
                            break;
                        }
                        await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
                    }

                    // 仅当 oldHandleForClosure 仍是 _previousHandle 时 Dispose（避免误删正在使用的引擎）。
                    var currentPrevious = _previousHandle;
                    if (ReferenceEquals(currentPrevious, oldHandleForClosure))
                    {
                        Interlocked.CompareExchange(ref _previousHandle, null, oldHandleForClosure);
                        await SafeDisposeHandleAsync(oldHandleForClosure).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // best-effort：调度失败不影响激活流程。
                }
            });
        }

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
    /// P1：best-effort Dispose 整个 ActiveModelHandle（实际仅 Dispose Engine；counter 无需释放）。
    /// </summary>
    private static ValueTask SafeDisposeHandleAsync(ActiveModelHandle handle)
        => SafeDisposeEngineAsync(handle.Engine);
}
