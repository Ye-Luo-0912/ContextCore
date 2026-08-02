using System.Diagnostics;
using ContextCore.Abstractions;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// Shadow Model Manager — Champion / Challenger 影子模式支持
//
// 目标（对齐 P0-6 Model Control Plane API §4 Champion/Challenger）：
//   1. 维护一个独立于 ActiveEngine 的影子推理引擎（Challenger），
//      让控制平面能在不替换当前 active 模型（Champion）的前提下加载并验证候选模型。
//   2. Challenger 的推理结果不返回给用户，仅记录用于 Champion vs Challenger 对比。
//   3. 复用 IOnnxInferenceSessionFactory / IFeatureRegistry / ICalibrationValidator，
//      与 ModelActivationManager 共享 schema 验证 / 校准验证 / ONNX session 创建路径。
//
// 与 IModelActivationManager 的边界：
//   - IModelActivationManager.ActivateAsync 把引擎切换为 ActiveEngine（替换 Champion）。
//   - ShadowModelManager.ActivateShadowAsync 加载引擎到 ShadowEngine（不替换 Champion）。
//   - 控制平面通过对比 ShadowEngine 与 ActiveEngine 的推理结果决定是否提升 Challenger。
//
// 设计原则：
//   1. 线程安全：通过 lock 保护 ShadowEngine 切换，与 ModelActivationManager 一致。
//   2. fail-safe：影子激活失败不影响现有推理（ShadowEngine 保持 null，RunShadowAsync 返回失败结果）。
//   3. 不抛异常：RunShadowAsync 失败时返回 Succeeded=false 的结果，由调用方决定降级。
// ===========================================================================

/// <summary>
/// 影子模型管理器。维护独立于 ActiveEngine 的 Challenger 引擎，支持 Champion/Challenger 对比。
/// </summary>
/// <remarks>
/// 在 DI 中注册为 Singleton。当 ModelExecutionMode != RealModel 时不注册（无 ONNX session 工厂可用）。
/// 调用方（如 ModelControlPlaneEndpoints）通过本类型在 shadow 端点中加载与运行 Challenger。
/// </remarks>
public sealed class ShadowModelManager
{
    private readonly IOnnxInferenceSessionFactory _sessionFactory;
    private readonly IFeatureRegistry _featureRegistry;
    private readonly ICalibrationValidator _calibrationValidator;
    private readonly ICalibrationService? _calibrationService;
    private readonly object _shadowLock = new();
    private volatile OnnxInferenceEngine? _shadowEngine;
    private volatile ModelArtifactDescriptor? _shadowDescriptor;

    /// <summary>
    /// 构造 ShadowModelManager。
    /// </summary>
    /// <param name="sessionFactory">ONNX session 工厂（与 ModelActivationManager 共享）。</param>
    /// <param name="featureRegistry">特征注册表（用于 schema 存在性验证与 warmup batch 宽度）。</param>
    /// <param name="calibrationValidator">校准参数验证器（与 ModelActivationManager 共享）。</param>
    /// <param name="calibrationService">可选的校准服务（提供待验证参数；为 null 时跳过校准验证）。</param>
    public ShadowModelManager(
        IOnnxInferenceSessionFactory sessionFactory,
        IFeatureRegistry featureRegistry,
        ICalibrationValidator calibrationValidator,
        ICalibrationService? calibrationService = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(featureRegistry);
        ArgumentNullException.ThrowIfNull(calibrationValidator);

        _sessionFactory = sessionFactory;
        _featureRegistry = featureRegistry;
        _calibrationValidator = calibrationValidator;
        _calibrationService = calibrationService;
    }

    /// <summary>当前影子引擎（null = 未加载 Challenger）。</summary>
    public OnnxInferenceEngine? ShadowEngine => _shadowEngine;

    /// <summary>当前影子模型工件描述符（null = 未加载 Challenger）。</summary>
    public ModelArtifactDescriptor? ShadowDescriptor => _shadowDescriptor;

    /// <summary>
    /// 加载并预热 Challenger 模型（不替换 ActiveEngine）。
    /// </summary>
    /// <param name="descriptor">待加载的模型工件描述符。</param>
    /// <param name="options">ONNX 推理配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>加载结果（含验证明细；失败时返回错误消息）。</returns>
    public async ValueTask<ShadowActivationResult> ActivateShadowAsync(
        ModelArtifactDescriptor descriptor,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);

        // 校准验证（与 ModelActivationManager.ActivateCoreAsync 路径一致 — 精确 CalibrationVersion 绑定 + fail-closed）
        var calValidation = ValidateCalibrationForDescriptor(descriptor);
        if (calValidation is { IsFailed: true } failed)
        {
            return ShadowActivationResult.Failed(failed.Error!, descriptor, failed.Result);
        }
        var calResult = calValidation?.Result;

        // schema 存在性验证
        var schema = _featureRegistry.Get(descriptor.FeatureSchemaVersion);
        if (schema is null)
        {
            return ShadowActivationResult.Failed(
                $"特征 schema 版本 '{descriptor.FeatureSchemaVersion}' 未在 IFeatureRegistry 中注册。",
                descriptor,
                calResult);
        }

        // 创建 ONNX session
        IOnnxInferenceSession session;
        try
        {
            session = await _sessionFactory.CreateAsync(options, descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            return ShadowActivationResult.Failed($"ONNX 模型文件未找到：{ex.Message}", descriptor, calResult);
        }
        catch (InvalidOperationException ex)
        {
            return ShadowActivationResult.Failed($"ONNX session 创建失败：{ex.Message}", descriptor, calResult);
        }
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex)
        {
            return ShadowActivationResult.Failed($"ONNX session 创建失败：{ex.Message}", descriptor, calResult);
        }

        var engine = new OnnxInferenceEngine(session, options, calibrationVersion: descriptor.CalibrationVersion);

        // 预热：用 1 行 × FeatureCount 列全 0 batch 触发 ORT graph optimization。
        // 失败时不发布 ShadowEngine，返回 Failed（不影响现有推理）。
        try
        {
            await engine.WarmupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await SafeDisposeEngineAsync(engine).ConfigureAwait(false);
            return ShadowActivationResult.Failed(
                $"Challenger warmup 失败：{ex.GetType().Name}: {ex.Message}",
                descriptor,
                calResult);
        }

        // 原子切换 ShadowEngine；旧 ShadowEngine 立即 Dispose（无 in-flight 请求，无需 grace period）。
        OnnxInferenceEngine? oldShadow;
        lock (_shadowLock)
        {
            oldShadow = _shadowEngine;
            _shadowEngine = engine;
            _shadowDescriptor = descriptor;
        }

        if (oldShadow is not null)
        {
            _ = Task.Run(() => SafeDisposeEngineAsync(oldShadow));
        }

        return ShadowActivationResult.Succeeded(descriptor, engine, calResult);
    }

    /// <summary>
    /// 在 Challenger 上执行一次推理（结果不返回给用户，仅用于 Champion vs Challenger 对比）。
    /// </summary>
    /// <param name="batch">批量特征数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>推理结果；ShadowEngine 未加载时返回 Succeeded=false。</returns>
    public async ValueTask<BatchInferenceResult> RunShadowAsync(
        FeatureBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var engine = _shadowEngine;
        if (engine is null)
        {
            return new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = "Challenger 未加载；请先调用 ActivateShadowAsync。",
                Duration = TimeSpan.Zero
            };
        }

        return await engine.InferBatchAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>清除 Challenger（Dispose 引擎并清空描述符）。</summary>
    /// <returns>待 await 的 ValueTask。</returns>
    public async ValueTask ClearShadowAsync()
    {
        OnnxInferenceEngine? oldShadow;
        lock (_shadowLock)
        {
            oldShadow = _shadowEngine;
            _shadowEngine = null;
            _shadowDescriptor = null;
        }

        if (oldShadow is not null)
        {
            await SafeDisposeEngineAsync(oldShadow).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 校准验证辅助方法 — 精确 CalibrationVersion 绑定 + fail-closed。
    /// 与 ModelActivationManager.ValidateCalibrationForDescriptor 行为一致。
    /// </summary>
    private CalibrationValidationOutcome? ValidateCalibrationForDescriptor(ModelArtifactDescriptor descriptor)
    {
        const string defaultVersion = "default-v1";
        var expectedVersion = string.IsNullOrWhiteSpace(descriptor.CalibrationVersion)
            ? defaultVersion
            : descriptor.CalibrationVersion;
        var isDefaultVersion = string.Equals(expectedVersion, defaultVersion, StringComparison.Ordinal);

        if (_calibrationService is null)
        {
            if (isDefaultVersion) return null;
            return CalibrationValidationOutcome.Failed(
                $"校准验证失败：descriptor.CalibrationVersion='{expectedVersion}' 非 default-v1，" +
                "但 ICalibrationService 未注册；fail-closed 拒绝激活 Challenger。",
                calResult: null);
        }

        var parameters = _calibrationService.GetParametersForVersion(descriptor.ModelArtifactId, expectedVersion)
            ?? _calibrationService.GetParametersForVersion(descriptor.ModelName, expectedVersion);

        if (parameters is null)
        {
            if (isDefaultVersion) return null;
            return CalibrationValidationOutcome.Failed(
                $"校准验证失败：未找到 ModelArtifactId='{descriptor.ModelArtifactId}' / ModelName='{descriptor.ModelName}' " +
                $"且 Version='{expectedVersion}' 精确匹配的校准参数；fail-closed 拒绝激活 Challenger。",
                calResult: null);
        }

        var calResult = _calibrationValidator.Validate(parameters, descriptor.ModelArtifactId);
        if (!calResult.IsValid)
        {
            return CalibrationValidationOutcome.Failed($"校准验证失败：{calResult.Error}", calResult);
        }
        return CalibrationValidationOutcome.Succeeded(calResult);
    }

    /// <summary>WP-5：校准验证结果容器。</summary>
    private sealed class CalibrationValidationOutcome
    {
        public bool IsFailed { get; init; }
        public string? Error { get; init; }
        public CalibrationValidationResult? Result { get; init; }

        public static CalibrationValidationOutcome Failed(string error, CalibrationValidationResult? calResult)
            => new() { IsFailed = true, Error = error, Result = calResult };

        public static CalibrationValidationOutcome Succeeded(CalibrationValidationResult calResult)
            => new() { IsFailed = false, Error = null, Result = calResult };
    }

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
            // best-effort：Dispose 失败不影响主流程。
        }
    }
}

/// <summary>
/// Challenger 加载结果。
/// </summary>
public sealed record ShadowActivationResult
{
    /// <summary>是否加载成功。</summary>
    public required bool Success { get; init; }

    /// <summary>失败时的错误消息（Success=true 时为 null）。</summary>
    public required string? Error { get; init; }

    /// <summary>加载的模型工件描述符。</summary>
    public required ModelArtifactDescriptor? Descriptor { get; init; }

    /// <summary>加载的影子引擎（Success=false 时为 null）。</summary>
    public required OnnxInferenceEngine? Engine { get; init; }

    /// <summary>校准验证结果（未执行时为 null）。</summary>
    public required CalibrationValidationResult? CalibrationValidation { get; init; }

    /// <summary>构造成功结果。</summary>
    internal static ShadowActivationResult Succeeded(
        ModelArtifactDescriptor descriptor,
        OnnxInferenceEngine engine,
        CalibrationValidationResult? calResult) => new()
        {
            Success = true,
            Error = null,
            Descriptor = descriptor,
            Engine = engine,
            CalibrationValidation = calResult
        };

    /// <summary>构造失败结果。</summary>
    internal static ShadowActivationResult Failed(
        string error,
        ModelArtifactDescriptor? descriptor = null,
        CalibrationValidationResult? calResult = null) => new()
        {
            Success = false,
            Error = error,
            Descriptor = descriptor,
            Engine = null,
            CalibrationValidation = calResult
        };
}
