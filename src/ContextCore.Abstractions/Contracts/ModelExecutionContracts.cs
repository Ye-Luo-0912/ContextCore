namespace ContextCore.Abstractions;

// ===========================================================================
// / R28-F：Model Execution Runtime 契约
//
// 目标：
//   把分散在 ModelGateway 基础设施之上的特征管理、批量推理、模型校准能力
//   显式契约化，补齐以下四个抽象：
//     1. IFeatureRegistry       —— Feature Registry：管理特征 schema 版本
//     2. IBatchInferenceEngine  —— Batch Inference Engine：批量推理
//     3. ICalibrationService    —— Calibration Service：模型分数校准
//     4. Deterministic fallback —— 由 DeterministicBatchInferenceEngine 提供，
//        在真实模型不可用时使用 feature hash 产出确定性分数
//
// 设计原则：
//   1. 契约层不引入存储 I/O：所有抽象为进程内接口，实现层可注入持久化 store。
//   2. FeatureSchema 全局不可变：版本号一旦注册不可修改，新版本通过新 schema 注册实现。
//   3. BatchInference 必须可降级：当真实模型不可用时回退到 Deterministic 实现，
//      保证主链始终能产出非空结果（fail-safe 而非 fail-fast）。
//   4. Calibration 默认 identity：未配置参数时校准等价于恒等变换（返回原始 raw score）。
//
// （本迭代新增）：
//   - ModelExecutionSnapshot：把 ModelArtifactId/ModelVersion/FeatureSchemaVersion/
//     CalibrationVersion/InferenceEngineKind/ContentHash 组成精确模型执行快照，
//     替代之前用 engine.ModelVersion 直接解析 FeatureSchema 的耦合。
//   - CalibrationParameters 扩展：支持 Platt(A,B)/Temperature(T)/Isotonic(points)/Identity，
//     旧的 Parameter 字段保留为 Platt A 的兼容别名。
//   - FeatureBatch：连续数值内存（ReadOnlyMemory<float>），替代 boxing 字典。
//   - IBatchInferenceEngine 增加 ContentHash/CalibrationVersion/InferBatchAsync。
//   - IInferenceResultValidator：推理输出严格验证（NaN/Infinity/Confidence 范围/Count 一致性）。
// ===========================================================================

/// <summary>R28-D：Feature Registry — 管理特征 schema 版本。</summary>
public interface IFeatureRegistry
{
    /// <summary>注册特征 schema。</summary>
    void Register(FeatureSchema schema);

    /// <summary>获取指定版本的特征 schema。</summary>
    FeatureSchema? Get(string schemaVersion);

    /// <summary>获取最新版本。</summary>
    FeatureSchema? GetLatest();

    /// <summary>列出所有已注册版本。</summary>
    IReadOnlyList<FeatureSchema> ListAll();
}

/// <summary>R28-D：特征 schema 定义。</summary>
public sealed record FeatureSchema
{
    /// <summary>Schema 版本号（语义化版本字符串，如 "1.0.0"）。</summary>
    public required string Version { get; init; }

    /// <summary>该 schema 包含的特征定义列表。</summary>
    public required IReadOnlyList<FeatureDefinition> Features { get; init; }

    /// <summary>Schema 创建时间戳。</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>R28-D：特征定义。</summary>
public sealed record FeatureDefinition
{
    /// <summary>特征名（在 schema 内唯一）。</summary>
    public required string Name { get; init; }

    /// <summary>特征类型。</summary>
    public required FeatureType Type { get; init; }

    /// <summary>是否必填（缺失时是否阻止推理）。</summary>
    public required bool IsRequired { get; init; }

    /// <summary>默认值（字符串形式，由消费方按 Type 解析）；可空表示无默认值。</summary>
    public string? DefaultValue { get; init; }
}

/// <summary>R28-D：特征类型枚举。</summary>
public enum FeatureType : byte
{
    /// <summary>数值型特征。</summary>
    Numeric = 0,

    /// <summary>类别型特征。</summary>
    Categorical = 1,

    /// <summary>布尔型特征。</summary>
    Boolean = 2,

    /// <summary>文本型特征。</summary>
    Text = 3
}

/// <summary>
/// 推理引擎类型。让消费方区分真实模型 vs 确定性 fallback，
/// 避免把 DeterministicBatchInferenceEngine 的 feature hash 当成真实模型分数参与排序。
/// </summary>
public enum InferenceEngineKind : byte
{
    /// <summary>
    /// 真实模型（远程推理服务 / ONNX 等）。其分数可参与 FinalScore 加权。
    /// </summary>
    RealModel = 0,

    /// <summary>
    /// 确定性回放（feature hash / 规则评分）。仅用于：
    ///   - 模型不可用时的 fail-safe 降级
    ///   - 基础设施测试与本地预览
    ///   - contract test
    /// 默认配置下不得改变 FinalScore（除非显式开启 EnableModelScoring 且接受 hash 评分）。
    /// </summary>
    DeterministicReplay = 1,

    /// <summary>禁用：引擎不可用，所有推理请求立即失败。</summary>
    Disabled = 2
}

/// <summary>R28-D：Batch Inference Engine — 批量推理。</summary>
public interface IBatchInferenceEngine
{
    /// <summary>批量推理。</summary>
    ValueTask<BatchInferenceResult> InferAsync(BatchInferenceRequest request, CancellationToken ct = default);

    /// <summary>模型版本。</summary>
    string ModelVersion { get; }

    /// <summary>
    /// 引擎类型。消费方据此判断是否信任分数参与排序。
    /// DeterministicReplay 类型不得在默认配置下改变 FinalScore。
    /// </summary>
    InferenceEngineKind Kind { get; }

    /// <summary>
    /// 模型工件内容哈希（用于精确 Model Execution Snapshot）。
    /// 真实模型应返回 ONNX/序列化模型的 SHA-256；
    /// Deterministic 引擎返回自身实现的哈希（用于版本一致性检查）。
    /// </summary>
    string ContentHash { get; }

    /// <summary>
    /// 绑定的校准版本号（与 CalibrationParameters 的拟合版本对齐）。
    /// 默认 "default-v1"。
    /// </summary>
    string CalibrationVersion { get; }

    /// <summary>
    /// 基于连续数值内存（FeatureBatch）的批量推理。
    /// 比 InferAsync(BatchInferenceRequest) 减少装箱与字典查找开销，适合高频推理。
    /// 默认实现回退到字典路径（向后兼容）。
    /// </summary>
    ValueTask<BatchInferenceResult> InferBatchAsync(FeatureBatch batch, CancellationToken ct = default);
}

/// <summary>
/// 子问题1：Fallback Inference Engine — 用于 ModelActivationManager 注入的降级引擎标记接口。
/// </summary>
/// <remarks>
/// 仅为 <see cref="IBatchInferenceEngine"/> 的 marker 接口，不新增成员。
/// 引入此接口的目的：在 DI 容器中将 fallback 引擎（通常为 DeterministicBatchInferenceEngine）
/// 与 <see cref="IModelActivationManager"/> 自身（也实现 IBatchInferenceEngine）区分开，
/// 避免 ModelActivationManager 构造时解析 IBatchInferenceEngine 又回到自身的循环依赖。
/// ModelActivationManager 构造函数注入 <see cref="IFallbackInferenceEngine"/>，
/// 而消费方仍通过 <see cref="IBatchInferenceEngine"/> 获取 ModelActivationManager 代理。
/// </remarks>
public interface IFallbackInferenceEngine : IBatchInferenceEngine
{
}

/// <summary>R28-D：批量推理请求。</summary>
public sealed record BatchInferenceRequest
{
    /// <summary>批量输入特征向量列表。</summary>
    public required IReadOnlyList<FeatureVector> Inputs { get; init; }

    /// <summary>目标模型名（可选；为空时使用引擎默认模型）。</summary>
    public string? ModelName { get; init; }

    /// <summary>超时时间（毫秒）。</summary>
    public int TimeoutMs { get; init; } = 5000;
}

/// <summary>R28-D：特征向量。</summary>
public sealed record FeatureVector
{
    /// <summary>关联的 schema 版本。</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>特征值字典（key = 特征名，value = 原始值）。</summary>
    public required IReadOnlyDictionary<string, object> Values { get; init; }
}

/// <summary>R28-D：批量推理结果。</summary>
public sealed record BatchInferenceResult
{
    /// <summary>每条输入对应的推理输出（顺序与输入一致）。</summary>
    public required IReadOnlyList<InferenceOutput> Outputs { get; init; }

    /// <summary>整体是否成功（false 时 Error 字段填充失败原因）。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>失败原因（Succeeded=true 时为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>本次推理耗时。</summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>R28-D：单条推理输出。</summary>
public sealed record InferenceOutput
{
    /// <summary>主分数（语义由模型决定，校准前可能不在 [0,1]）。</summary>
    public required double Score { get; init; }

    /// <summary>置信度（[0,1]）。</summary>
    public required double Confidence { get; init; }

    /// <summary>多分类场景下每个类别的分数（key = 类别标签，value = 分数）；二分类可空。</summary>
    public IReadOnlyDictionary<string, double>? PerClassScores { get; init; }
}

/// <summary>R28-D：Calibration Service — 模型校准。</summary>
public interface ICalibrationService
{
    /// <summary>校准分数。</summary>
    double Calibrate(double rawScore, string? modelName = null);

    /// <summary>批量校准。</summary>
    IReadOnlyList<double> CalibrateBatch(IReadOnlyList<double> rawScores, string? modelName = null);

    /// <summary>获取校准参数。</summary>
    CalibrationParameters? GetParameters(string? modelName = null);

    /// <summary>
    /// 按 modelName + version 精确查找校准参数。
    /// 用于 ModelActivationManager 激活时按 descriptor.CalibrationVersion 精确绑定校准参数。
    /// </summary>
    /// <param name="modelName">模型名（或 ModelArtifactId）；null 表示全局默认。</param>
    /// <param name="version">期望的校准版本号（对应 ModelArtifactDescriptor.CalibrationVersion）。</param>
    /// <returns>匹配 modelName 且 Version 与 <paramref name="version"/> 精确一致的参数；未命中时返回 null。</returns>
    /// <remarks>
    /// 实现应保证 Version 精确匹配（区分大小写、不裁剪空白），不进行任何 fallback。
    /// 调用方（ModelActivationManager）依赖此语义实现 fail-closed：未命中即拒绝激活。
    /// </remarks>
    CalibrationParameters? GetParametersForVersion(string? modelName, string version);
}

/// <summary>
/// / R28-F P3-3：校准方法种类。用于精确路由 calibration 策略。
/// </summary>
public enum CalibrationMethodKind : byte
{
    /// <summary>恒等变换：calibrated = rawScore。不改变输入。</summary>
    Identity = 0,

    /// <summary>Platt scaling：calibrated = sigmoid(A * raw + B)。</summary>
    Platt = 1,

    /// <summary>Temperature scaling：calibrated = sigmoid(raw / T)。</summary>
    Temperature = 2,

    /// <summary>Isotonic regression：分段线性映射。</summary>
    Isotonic = 3
}

/// <summary>
/// / R28-F P3-3：校准参数。
/// 原始契约仅暴露单个 Parameter（= Platt A）；R28-F 扩展为完整支持
/// Platt(A,B) / Temperature(T) / Isotonic(points) / Identity。
/// 旧字段 Parameter 保留为 Platt A 的兼容别名（值同步 ParameterA）。
/// </summary>
public sealed record CalibrationParameters
{
    /// <summary>
    /// 校准方法名（"identity" / "platt" / "temperature" / "isotonic"）。
    /// 与 <see cref="Kind"/> 对应；推荐使用 Kind 枚举判断分支。
    /// </summary>
    public required string Method { get; init; }

    /// <summary>R28-F P3-3：方法种类枚举（强类型路由）。</summary>
    public CalibrationMethodKind Kind { get; init; } = CalibrationMethodKind.Platt;

    /// <summary>
    /// 校准参数（Platt: A；Temperature: T；Isotonic: 忽略）。
    /// <b>保留为向后兼容别名</b>，值与 <see cref="ParameterA"/> 同步。
    /// 新代码应使用 <see cref="ParameterA"/> / <see cref="ParameterB"/> / <see cref="Temperature"/>。
    /// </summary>
    public double Parameter { get; init; } = 1.0;

    /// <summary>R28-F P3-3：Platt A 参数（calibrated = sigmoid(A*raw + B)）。</summary>
    public double ParameterA { get; init; } = 1.0;

    /// <summary>R28-F P3-3：Platt B 参数。</summary>
    public double ParameterB { get; init; } = 0.0;

    /// <summary>R28-F P3-3：Temperature T 参数（calibrated = sigmoid(raw / T)）。</summary>
    public double Temperature { get; init; } = 1.0;

    /// <summary>R28-F P3-3：Isotonic 回归的输入→输出映射点（按 Input 升序）。</summary>
    public IReadOnlyList<IsotonicPoint> IsotonicPoints { get; init; } = Array.Empty<IsotonicPoint>();

    /// <summary>参数拟合时间戳。</summary>
    public required DateTimeOffset FittedAt { get; init; }

    /// <summary>
    /// 校准参数版本号。用于 ModelActivationManager 激活时与
    /// <see cref="ModelArtifactDescriptor.CalibrationVersion"/> 精确匹配。
    /// 默认 "default-v1"（与 OnnxInferenceEngine 默认值对齐）。
    /// </summary>
    /// <remarks>
    /// RegisterXxxParameters 未显式传入 version 时使用此默认值；
    /// 生产路径注册真实校准时应传入与 descriptor.CalibrationVersion 一致的版本号。
    /// </remarks>
    public string Version { get; init; } = "default-v1";
}

/// <summary>
/// Isotonic 回归的单个映射点。
/// </summary>
public sealed record IsotonicPoint
{
    /// <summary>原始分数输入。</summary>
    public required double Input { get; init; }

    /// <summary>校准后输出。</summary>
    public required double Output { get; init; }
}

/// <summary>
/// Model Execution Snapshot。
/// 把 ModelArtifactId / ModelVersion / FeatureSchemaVersion / CalibrationVersion /
/// InferenceEngineKind / ContentHash 组成精确的模型执行快照，
/// 用于：(1) Scorer 解耦 schema 解析与模型版本；
///       (2) 审计/复现一次推理所用的精确工件组合；
///       (3) 检测跨节点 HA 不一致（不同节点加载了不同 ContentHash）。
/// </summary>
public sealed record ModelExecutionSnapshot
{
    /// <summary>模型工件 ID（对应 RoutingProfile.ModelArtifactId）。</summary>
    public required string ModelArtifactId { get; init; }

    /// <summary>模型版本号（来自 IBatchInferenceEngine.ModelVersion）。</summary>
    public required string ModelVersion { get; init; }

    /// <summary>特征 schema 版本（来自 EffectivePolicySnapshot.FeatureSchemaVersion）。</summary>
    public required string FeatureSchemaVersion { get; init; }

    /// <summary>校准版本号（来自 IBatchInferenceEngine.CalibrationVersion）。</summary>
    public required string CalibrationVersion { get; init; }

    /// <summary>推理引擎类型（来自 IBatchInferenceEngine.Kind）。</summary>
    public required InferenceEngineKind EngineKind { get; init; }

    /// <summary>模型工件内容哈希（来自 IBatchInferenceEngine.ContentHash）。</summary>
    public required string ContentHash { get; init; }

    /// <summary>本次快照构建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 连续数值内存的批量特征数据。
/// 替代 <see cref="FeatureVector"/>（IReadOnlyDictionary&lt;string,object&gt; 装箱）的高性能等价物。
/// 内存布局：row-major，RowCount × FeatureCount 连续 float。
/// </summary>
/// <remarks>
/// 推荐使用方式：
///   var batch = FeatureBatch.FromRows(schema, rows);
///   var result = await engine.InferBatchAsync(batch, ct);
/// 一次 batch inference / request，避免每候选装箱与字典查找。
/// </remarks>
public sealed record FeatureBatch
{
    /// <summary>关联的 schema 版本（与 FeatureSchema.Version 对齐）。</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// 连续 float 缓冲区（row-major：第 i 行第 j 列位于 Values[i * FeatureCount + j]）。
    /// 长度必须等于 RowCount × FeatureCount。
    /// </summary>
    public required ReadOnlyMemory<float> Values { get; init; }

    /// <summary>行数（候选数量）。</summary>
    public required int RowCount { get; init; }

    /// <summary>每行特征数量（与 FeatureSchema.Features.Count 一致）。</summary>
    public required int FeatureCount { get; init; }

    /// <summary>
    /// 特征名列表（按列顺序；长度必须等于 FeatureCount）。
    /// 与 FeatureSchema.Features 顺序对齐，用于校验 schema 一致性。
    /// </summary>
    public required IReadOnlyList<string> FeatureNames { get; init; }
}

/// <summary>
/// 推理输出验证结果。
/// </summary>
public sealed record InferenceValidationResult
{
    /// <summary>是否通过验证。</summary>
    public required bool IsValid { get; init; }

    /// <summary>聚合错误消息（IsValid=true 时为 null）。</summary>
    public required string? Error { get; init; }

    /// <summary>所有违规明细（IsValid=true 时为空）。</summary>
    public required IReadOnlyList<string> Violations { get; init; }
}

/// <summary>
/// 推理输出验证器。
/// 在 Scorer 应用模型分数到 Allocator 排序前，对 BatchInferenceResult 执行严格验证：
///   - Outputs.Count == Inputs.Count
///   - Score/Confidence 不是 NaN/Infinity
///   - Confidence 在 [0,1]
///   - schema/version 与输入一致
///   - timeout 真实执行（Duration > 0 当 TimeoutMs > 0）
/// </summary>
public interface IInferenceResultValidator
{
    /// <summary>验证一次批量推理结果。</summary>
    /// <param name="request">原始请求（用于检查 Inputs.Count 与 SchemaVersion）。</param>
    /// <param name="result">推理结果。</param>
    /// <returns>验证结果（IsValid=false 时含违规明细）。</returns>
    InferenceValidationResult Validate(BatchInferenceRequest request, BatchInferenceResult result);

    /// <summary>R28-F P4-1：基于 FeatureBatch 的重载（验证 SchemaVersion 一致性）。</summary>
    InferenceValidationResult Validate(FeatureBatch batch, BatchInferenceResult result);
}
