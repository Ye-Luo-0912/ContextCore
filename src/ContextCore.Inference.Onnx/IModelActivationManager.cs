using ContextCore.Abstractions;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// P0-7：Model Activation Manager 契约
//
// 目标（权威模型激活管理器）：
//   1. 编排从 IModelArtifactRegistry 读取 descriptor → 校准验证（ICalibrationValidator）
//      → schema 存在性验证（IFeatureRegistry.Get）→ ONNX session 创建（IOnnxInferenceSessionFactory）
//      → OnnxInferenceEngine 激活的完整流程。
//   2. 作为 IBatchInferenceEngine 的代理：激活前委托给 Deterministic fallback，
//      激活后委托给 OnnxInferenceEngine，让消费方无需感知激活切换。
//   3. 线程安全：ActivateAsync 可在运行时调用，无缝切换引擎。
//
// P0-8 集成：
//   ICalibrationValidator 在激活流程中被调用，确保校准参数在模型加载时通过统计有效性验证。
//   IFeatureRegistry.Get 验证 descriptor.FeatureSchemaVersion 已注册，防止推理时 schema drift。
//   IFeatureSchemaValidator 由上游消费方在推理前调用（生产推理路径），验证输入特征与 schema 一致性。
// ===========================================================================

/// <summary>
/// P0-7：权威模型激活管理器。编排模型工件加载 → 验证 → ONNX 引擎激活的完整流程。
/// </summary>
/// <remarks>
/// 同时实现 <see cref="IBatchInferenceEngine"/> 作为代理：
/// 未激活时委托给 fallback（Deterministic），激活后委托给 OnnxInferenceEngine。
/// 在 DI 中注册为 <see cref="IBatchInferenceEngine"/> 的实现，消费方无需感知激活状态。
/// </remarks>
public interface IModelActivationManager : IBatchInferenceEngine
{
    /// <summary>当前已激活的推理引擎（null = 未激活，使用 fallback）。</summary>
    IBatchInferenceEngine? ActiveEngine { get; }

    /// <summary>当前已激活的模型工件描述符（null = 未激活）。</summary>
    ModelArtifactDescriptor? ActiveDescriptor { get; }

    /// <summary>
    /// 激活指定模型工件：读取 descriptor → 验证校准（ICalibrationValidator）→ 验证 schema 存在性（IFeatureRegistry）
    /// → 创建 ONNX session（IOnnxInferenceSessionFactory）→ 激活引擎。
    /// </summary>
    /// <param name="modelArtifactId">模型工件 ID（从 IModelArtifactRegistry 查询）。</param>
    /// <param name="options">ONNX 推理配置（张量映射、线程数、超时）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>激活结果（含验证明细）。</returns>
    ValueTask<ModelActivationResult> ActivateAsync(
        string modelArtifactId,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 激活指定模型名的最新版本（通过 IModelArtifactRegistry.GetLatestAsync 解析）。
    /// </summary>
    /// <param name="modelName">逻辑模型名。</param>
    /// <param name="options">ONNX 推理配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>激活结果。</returns>
    ValueTask<ModelActivationResult> ActivateLatestAsync(
        string modelName,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// P0-7：模型激活结果。包含激活是否成功、验证明细和已激活的引擎。
/// </summary>
public sealed record ModelActivationResult
{
    /// <summary>是否激活成功（校准+schema 验证通过且 ONNX session 创建成功）。</summary>
    public required bool Success { get; init; }

    /// <summary>失败时的错误消息（Success=true 时为 null）。</summary>
    public required string? Error { get; init; }

    /// <summary>激活的模型工件描述符（descriptor 未找到时为 null）。</summary>
    public required ModelArtifactDescriptor? Descriptor { get; init; }

    /// <summary>已激活的推理引擎（Success=false 时为 null）。</summary>
    public required IBatchInferenceEngine? Engine { get; init; }

    /// <summary>校准验证结果（P0-8：ICalibrationValidator 输出；未执行时为 null）。</summary>
    public required CalibrationValidationResult? CalibrationValidation { get; init; }

    /// <summary>特征 schema 验证错误消息（P0-8：schema 不存在或不匹配时非 null）。</summary>
    public required string? SchemaValidationError { get; init; }

    /// <summary>构造成功结果。</summary>
    internal static ModelActivationResult Succeeded(
        ModelArtifactDescriptor descriptor,
        IBatchInferenceEngine engine,
        CalibrationValidationResult calResult) => new()
    {
        Success = true,
        Error = null,
        Descriptor = descriptor,
        Engine = engine,
        CalibrationValidation = calResult,
        SchemaValidationError = null
    };

    /// <summary>构造失败结果。</summary>
    internal static ModelActivationResult Failed(
        string error,
        ModelArtifactDescriptor? descriptor = null,
        CalibrationValidationResult? calResult = null,
        string? schemaError = null) => new()
    {
        Success = false,
        Error = error,
        Descriptor = descriptor,
        Engine = null,
        CalibrationValidation = calResult,
        SchemaValidationError = schemaError
    };
}
