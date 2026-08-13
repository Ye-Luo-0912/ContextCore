namespace ContextCore.Abstractions;

// 模型执行模式契约：生产 DI 按 Mode 选择 IBatchInferenceEngine。
// 默认 Deterministic；RealModel 才挂 ModelActivationManager（未激活仍走确定性）。

/// <summary>
/// 模型执行模式。控制生产 DI 中 <see cref="IBatchInferenceEngine"/> 的注册选择。
/// </summary>
public enum ModelExecutionMode : byte
{
    /// <summary>
    /// 确定性回放模式（默认）。
    /// 注册 <see cref="IBatchInferenceEngine"/> 为 DeterministicBatchInferenceEngine，
    /// 使用 feature hash 产出确定性分数。适用于基础设施测试、预览与 fail-safe 降级。
    /// </summary>
    Deterministic = 0,

    /// <summary>
    /// 真实模型模式。
    /// 注册 <see cref="IBatchInferenceEngine"/> 为 ModelActivationManager，
    /// 以 DeterministicBatchInferenceEngine 为 fallback。运行时通过
    /// <see cref="IModelActivationManager"/>（若注册）的 ActivateAsync 切换到真实 ONNX 模型。
    /// 未激活时仍走 Deterministic 路径，保证 fail-safe。
    /// </summary>
    RealModel = 1
}

/// <summary>
/// 模型执行配置选项。控制 <see cref="IBatchInferenceEngine"/> 的注册行为。
/// </summary>
/// <remarks>
/// <b>使用模式</b>：
/// <code>
/// var modelExecOptions = new ModelExecutionOptions
/// {
/// Mode = ModelExecutionMode.RealModel,
/// AutoActivateOnStartup = true,
/// ModelArtifactId = "bge-base-v1"
/// };
/// services.AddContextCore(modelExecOptions);
/// // RealModel 模式下，调用方还需注册 IModelArtifactRegistry 与 IOnnxInferenceSessionFactory
/// // （由 PostgresServiceCollectionExtensions / OnnxInferenceServiceCollectionExtensions 提供）。
/// </code>
/// <para>
/// 默认值：<see cref="Mode"/> = <see cref="ModelExecutionMode.Deterministic"/>，
/// 不改变现有生产行为。
/// </para>
/// </remarks>
public sealed class ModelExecutionOptions
{
    /// <summary>
    /// 默认配置：Deterministic 模式（向后兼容现有生产行为）。
    /// </summary>
    public static ModelExecutionOptions Default { get; } = new();

    /// <summary>
    /// 模型执行模式。默认 <see cref="ModelExecutionMode.Deterministic"/>。
    /// </summary>
    public ModelExecutionMode Mode { get; init; } = ModelExecutionMode.Deterministic;

    /// <summary>
    /// 是否在启动时自动激活模型（仅 RealModel 模式生效）。
    /// 默认 false：由调用方显式调用 IModelActivationManager.ActivateAsync。
    /// 设为 true 时，启动 HostedService 会自动解析 IModelActivationManager 并调用 ActivateAsync。
    /// </summary>
    public bool AutoActivateOnStartup { get; init; } = false;

    /// <summary>
    /// 自动激活时使用的模型工件 ID（仅 AutoActivateOnStartup=true 时生效）。
    /// 为 null 时使用 <see cref="ModelName"/> 调用 ActivateLatestAsync。
    /// </summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>
    /// 自动激活时使用的逻辑模型名（仅 AutoActivateOnStartup=true 且 ModelArtifactId 为 null 时生效）。
    /// </summary>
    public string? ModelName { get; init; }
}
