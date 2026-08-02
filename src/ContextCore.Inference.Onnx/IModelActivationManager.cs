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
/// P0-8：推理引擎租约 —— 捕获当前 Active Engine 并递增引用计数，
/// 防止引擎在执行期间被 Dispose。调用方必须通过 <see cref="Dispose"/> 释放（递减引用计数）。
/// </summary>
/// <remarks>
/// 用于 <see cref="InferenceScheduler"/> 等上层组件在入队时捕获引擎引用，
/// 确保请求在捕获的世代上执行，避免热切换后 cross-generation execution。
/// </remarks>
public interface IInferenceEngineLease : IDisposable
{
    /// <summary>捕获的推理引擎（已递增引用计数，执行期间不会被 Dispose）。</summary>
    IBatchInferenceEngine Engine { get; }

    /// <summary>捕获时引擎的世代号（用于诊断/日志）。</summary>
    long Generation { get; }
}

/// <summary>
/// P0-7：权威模型激活管理器。编排模型工件加载 → 验证 → ONNX 引擎激活的完整流程。
/// </summary>
/// <remarks>
/// 同时实现 <see cref="IBatchInferenceEngine"/> 作为代理：
/// 未激活时委托给 fallback（Deterministic），激活后委托给 OnnxInferenceEngine。
/// 在 DI 中注册为 <see cref="IBatchInferenceEngine"/> 的实现，消费方无需感知激活状态。
/// 继承 <see cref="IAsyncDisposable"/>：Dispose 时等待所有 Retired Handle 引用归零并取消后台 Dispose Task。
/// </remarks>
public interface IModelActivationManager : IBatchInferenceEngine, IAsyncDisposable
{
    /// <summary>当前已激活的推理引擎（null = 未激活，使用 fallback）。</summary>
    IBatchInferenceEngine? ActiveEngine { get; }

    /// <summary>
    /// P0-8：捕获当前 Active Engine 并递增引用计数。调用方必须通过返回的 lease 释放。
    /// 未激活时返回 null（调用方应回退到 <see cref="IBatchInferenceEngine.InferBatchAsync"/> 走 fallback）。
    /// </summary>
    /// <returns>引擎租约（null = 未激活）；调用方必须 Dispose 以递减引用计数。</returns>
    IInferenceEngineLease? AcquireEngineLease();

    /// <summary>
    /// P0-8：捕获 fallback 引擎的永久租约（不过期）。
    /// 用于 <see cref="InferenceScheduler"/> 在入队时无 Active Engine 的情况下，
    /// 固定请求在 fallback 引擎上执行，避免排队期间模型被激活后 cross-generation execution
    /// （即执行阶段通过动态代理跑到新激活的引擎）。
    /// </summary>
    /// <remarks>
    /// 返回的 lease 引用 fallback 引擎（<see cref="IInferenceEngineLease.Generation"/>=0，
    /// 0 不会与任何真实激活世代冲突——真实世代自 1 起自增）。
    /// <see cref="IDisposable.Dispose"/> 为 no-op：fallback 引擎由 DI 容器管理生命周期，
    /// 无需引用计数。lease 本身可被调用方任意次数 Dispose（幂等安全）。
    /// </remarks>
    /// <returns>fallback 引擎的永久租约（Generation=0，Dispose 为 no-op）。</returns>
    IInferenceEngineLease AcquireFallbackEngineLease();

    /// <summary>当前已激活的模型工件描述符（null = 未激活）。</summary>
    ModelArtifactDescriptor? ActiveDescriptor { get; }

    /// <summary>
    /// P0-7：当前 Active Handle 的世代号（每次激活自增；null = 未激活）。
    /// 调用方（如 <see cref="InferenceScheduler"/>）据此感知模型热切换：
    /// 世代号变化意味着底层引擎已替换，已攒批的请求不能与新请求合并到同一 BatchKey。
    /// </summary>
    long? ActiveGeneration { get; }

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

    /// <summary>
    /// P15：加载并预热模型，但不发布为 ActiveEngine。
    /// 返回一个 Staged Handle，调用方可随后通过 <see cref="PromoteStagedAsync"/> 将其原子发布为 active，
    /// 或直接丢弃（Dispose）。本方法用于 /warmup 端点：预热不应替换当前 active 模型。
    /// </summary>
    /// <param name="modelArtifactId">模型工件 ID（从 IModelArtifactRegistry 查询）。</param>
    /// <param name="options">ONNX 推理配置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Staged Handle（含已 warmup 的引擎、descriptor、handle id）。</returns>
    ValueTask<StagedModelHandle> LoadAndWarmupAsync(
        string modelArtifactId,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// P15：将先前 <see cref="LoadAndWarmupAsync"/> 产生的 Staged Handle 原子发布为 active。
    /// 未找到 handleId 时返回 Failed。成功后 Staged Handle 从内部暂存表中移除。
    /// </summary>
    /// <param name="stagedHandleId">由 <see cref="LoadAndWarmupAsync"/> 返回的 handle id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>激活结果（含已发布的引擎）。</returns>
    ValueTask<ModelActivationResult> PromoteStagedAsync(
        string stagedHandleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// P0-9：停用当前 Active Engine，回退到 fallback 引擎。
    /// 用于 HA Reconciler 收敛 Inactive 期望状态。
    /// 旧 Active Handle 进入 Retired 列表并按 grace period drain（与 ActivateAsync 切换一致），
    /// 确保 in-flight 请求不会在引擎 Dispose 后失败。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>停用结果（Success=true 表示已回退到 fallback；无 Active Engine 时也返回 Success）。</returns>
    ValueTask<ModelActivationResult> DeactivateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// P15：Staged Model Handle — 由 <see cref="IModelActivationManager.LoadAndWarmupAsync"/> 返回。
/// 表示已加载并 warmup 但尚未发布为 active 的引擎。调用方应在使用完毕后 Dispose；
/// 或通过 <see cref="IModelActivationManager.PromoteStagedAsync"/> 提升为 active。
/// Success=false 时 Engine 为 null，调用方应检查 <see cref="Success"/> 后再使用 Engine。
/// </summary>
public sealed record StagedModelHandle
{
    /// <summary>Staged Handle 全局唯一标识（由 LoadAndWarmupAsync 生成）。</summary>
    public required string HandleId { get; init; }

    /// <summary>是否加载并 warmup 成功。false 时 Engine 为 null，参考 <see cref="Error"/>。</summary>
    public required bool Success { get; init; }

    /// <summary>失败时的错误消息（Success=true 时为 null）。</summary>
    public required string? Error { get; init; }

    /// <summary>已加载并 warmup 的引擎（Success=false 时为 null）。</summary>
    public required IBatchInferenceEngine? Engine { get; init; }

    /// <summary>对应的模型工件描述符（descriptor 未找到时可能为 null）。</summary>
    public required ModelArtifactDescriptor? Descriptor { get; init; }

    /// <summary>校准验证结果（未执行时为 null）。</summary>
    public required CalibrationValidationResult? CalibrationValidation { get; init; }

    /// <summary>Staged 时间戳。</summary>
    public required DateTimeOffset StagedAt { get; init; }

    /// <summary>构造成功结果。</summary>
    internal static StagedModelHandle Succeeded(
        string handleId,
        IBatchInferenceEngine engine,
        ModelArtifactDescriptor descriptor,
        CalibrationValidationResult? calResult) => new()
    {
        HandleId = handleId,
        Success = true,
        Error = null,
        Engine = engine,
        Descriptor = descriptor,
        CalibrationValidation = calResult,
        StagedAt = DateTimeOffset.UtcNow
    };

    /// <summary>构造失败结果（Engine 为 null，Success=false）。</summary>
    internal static StagedModelHandle Failed(
        string handleId,
        ModelArtifactDescriptor? descriptor,
        string error,
        CalibrationValidationResult? calResult = null) => new()
    {
        HandleId = handleId,
        Success = false,
        Error = error,
        Engine = null,
        Descriptor = descriptor,
        CalibrationValidation = calResult,
        StagedAt = DateTimeOffset.UtcNow
    };
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

    /// <summary>P0-9：构造停用成功结果（Engine/Descriptor/Calibration 均为 null，表示已回退到 fallback）。</summary>
    internal static ModelActivationResult Deactivated(ModelArtifactDescriptor? previousDescriptor) => new()
    {
        Success = true,
        Error = null,
        Descriptor = previousDescriptor,
        Engine = null,
        CalibrationValidation = null,
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
