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
    private readonly IBatchInferenceEngine _fallbackEngine;
    private readonly ICalibrationService? _calibrationService;

    // 激活状态：通过 lock 保护读写，确保原子切换。
    private readonly object _activationLock = new();
    private volatile IBatchInferenceEngine? _activeEngine;
    private volatile ModelArtifactDescriptor? _activeDescriptor;

    /// <summary>
    /// 构造 ModelActivationManager。
    /// </summary>
    /// <param name="registry">模型工件注册表（从中读取 descriptor）。</param>
    /// <param name="calibrationValidator">校准参数验证器（P0-8：加载时验证校准有效性）。</param>
    /// <param name="featureRegistry">特征注册表（从中查询 schema；P0-8：加载时验证 schema 存在性）。</param>
    /// <param name="sessionFactory">ONNX session 工厂（创建推理会话）。</param>
    /// <param name="fallbackEngine">降级引擎（未激活时使用，通常为 DeterministicBatchInferenceEngine）。</param>
    /// <param name="calibrationService">可选的校准服务（提供待验证的校准参数；为 null 时跳过校准验证）。</param>
    public ModelActivationManager(
        IModelArtifactRegistry registry,
        ICalibrationValidator calibrationValidator,
        IFeatureRegistry featureRegistry,
        IOnnxInferenceSessionFactory sessionFactory,
        IBatchInferenceEngine fallbackEngine,
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
    public ValueTask<BatchInferenceResult> InferAsync(
        BatchInferenceRequest request,
        CancellationToken ct = default)
        => (_activeEngine ?? _fallbackEngine).InferAsync(request, ct);

    /// <inheritdoc />
    public ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken ct = default)
        => (_activeEngine ?? _fallbackEngine).InferBatchAsync(batch, ct);

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

        // P0-7 Step 4：构造 OnnxInferenceEngine 并原子切换
        var engine = new OnnxInferenceEngine(
            session,
            options,
            calibrationVersion: descriptor.CalibrationVersion);

        lock (_activationLock)
        {
            _activeEngine = engine;
            _activeDescriptor = descriptor;
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        return ModelActivationResult.Succeeded(descriptor, engine, calResult ?? new CalibrationValidationResult
        {
            IsValid = true,
            Error = null,
            Violations = Array.Empty<CalibrationViolation>()
        });
    }
}
