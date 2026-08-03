namespace ContextCore.Abstractions;

// ===========================================================================
// Feature Schema Validator 契约
//
// 目标（对齐 Production Intelligence 规格）：
// 在推理前严格校验输入特征与 FeatureSchema 一致性，防止 schema drift
// 导致模型读到错误列序 / 缺失必填特征 / 类型不兼容的值。
//
// 与 IInferenceResultValidator 的边界：
// - IFeatureSchemaValidator 验证"输入特征 vs schema 定义"（推理前），
// 关心名称 / 必填 / 默认值 / 类型可转换性。
// - IInferenceResultValidator 验证"推理输出 vs 输入约束"（推理后），
// 关心 NaN / Infinity / Count / Range。
// 两者互补，分别在推理前后把守质量门。
//
// 与 ICalibrationValidator 的边界：
// - ICalibrationValidator 验证"校准参数本身"（加载时，与单次输入无关）。
// - IFeatureSchemaValidator 验证"输入数据 vs schema"（每次推理前）。
//
// 设计原则：
// 1. 不抛异常：返回结构化 FeatureSchemaValidationResult，由调用方决定降级
// （拒绝推理 / 应用默认值后继续 / 标记 warning 继续）。
// 2. 完整违规清单：聚合所有违规（Error + Warning），便于诊断。
// 3. 严重程度分级：Error 阻止推理；Warning 表示统计可疑但可继续。
// 4. 不依赖外部库：所有检验（名称匹配 / 类型可转换性）用纯 .NET 实现。
// ===========================================================================

/// <summary>
/// 特征 schema 验证器。
/// 在推理前对输入特征与 <see cref="FeatureSchema"/> 执行严格匹配验证。
/// </summary>
/// <remarks>
/// <b>使用模式</b>：
/// <code>
/// var validator = host.Services.GetRequiredService&lt;IFeatureSchemaValidator&gt;();
/// var schema = featureRegistry.Get(descriptor.FeatureSchemaVersion);
/// var result = validator.Validate(schema!, inputVector);
/// if (!result.IsValid)
/// {
/// // 拒绝推理或应用默认值后重试
/// }
/// </code>
/// <para>
/// 验证项：
/// <list type="bullet">
/// <item><b>SchemaVersion 匹配</b>：输入 SchemaVersion 必须与 schema.Version 一致。</item>
/// <item><b>必填特征存在</b>：<see cref="FeatureDefinition.IsRequired"/> = true 时输入必须包含该特征。</item>
/// <item><b>无未知特征</b>：输入中的特征名必须全部出现在 schema.Features 中（默认严格模式）。</item>
/// <item><b>类型可转换</b>：输入值必须可转换为 <see cref="FeatureDefinition.Type"/> 指定的类型。</item>
/// <item><b>默认值可解析</b>：当必填特征缺失且 schema 提供 DefaultValue 时，默认值字符串必须可解析为目标类型（Warning）。</item>
/// </list>
/// </para>
/// </remarks>
public interface IFeatureSchemaValidator
{
    /// <summary>
    /// 验证单个特征向量与 schema 的一致性。
    /// </summary>
    /// <param name="schema">目标 schema（来自 IFeatureRegistry）。</param>
    /// <param name="input">待验证的特征向量。</param>
    /// <returns>验证结果（<see cref="FeatureSchemaValidationResult"/>）。</returns>
    FeatureSchemaValidationResult Validate(FeatureSchema schema, FeatureVector input);

    /// <summary>
    /// 验证 <see cref="FeatureBatch"/> 与 schema 的一致性。
    /// 检查 SchemaVersion、FeatureCount、FeatureNames 顺序与 schema.Features 对齐。
    /// </summary>
    /// <param name="schema">目标 schema。</param>
    /// <param name="batch">批量特征数据。</param>
    /// <returns>验证结果。</returns>
    FeatureSchemaValidationResult Validate(FeatureSchema schema, FeatureBatch batch);

    /// <summary>
    /// 批量验证：对一组特征向量执行验证，返回聚合结果。
    /// 任一向量返回 Error 时，聚合结果 IsValid=false。
    /// </summary>
    /// <param name="schema">目标 schema。</param>
    /// <param name="inputs">待验证的特征向量列表。</param>
    /// <returns>聚合验证结果；包含所有违规明细（标注行索引）。</returns>
    FeatureSchemaValidationResult ValidateBatch(FeatureSchema schema, IReadOnlyList<FeatureVector> inputs);
}

/// <summary>
/// 特征 schema 验证结果。
/// </summary>
public sealed record FeatureSchemaValidationResult
{
    /// <summary>是否通过验证（无 Error 级违规时为 true；Warning 不影响 IsValid）。</summary>
    public required bool IsValid { get; init; }

    /// <summary>聚合错误消息（IsValid=true 时为 null）。</summary>
    public required string? Error { get; init; }

    /// <summary>所有违规明细（Error + Warning；IsValid=true 时可能含 Warning）。</summary>
    public required IReadOnlyList<FeatureSchemaViolation> Violations { get; init; }

    /// <summary>Error 级违规数量（等于 0 时 IsValid=true）。</summary>
    public int ErrorCount => Violations.Count(v => v.Severity == FeatureSchemaViolationSeverity.Error);

    /// <summary>Warning 级违规数量（不影响 IsValid）。</summary>
    public int WarningCount => Violations.Count(v => v.Severity == FeatureSchemaViolationSeverity.Warning);
}

/// <summary>
/// 单条特征 schema 违规。
/// </summary>
public sealed record FeatureSchemaViolation
{
    /// <summary>严重程度（Error / Warning / Info）。</summary>
    public required FeatureSchemaViolationSeverity Severity { get; init; }

    /// <summary>违规代码（机器可读，如 "schema.version_mismatch"、"feature.missing_required"）。</summary>
    public required string Code { get; init; }

    /// <summary>人类可读的违规描述。</summary>
    public required string Message { get; init; }

    /// <summary>关联的特征名（可空，用于 schema 级违规如版本不匹配）。</summary>
    public string? FeatureName { get; init; }

    /// <summary>关联的行索引（仅 ValidateBatch 重载填充；单条验证为 null）。</summary>
    public int? RowIndex { get; init; }
}

/// <summary>
/// 特征 schema 违规严重程度。
/// </summary>
public enum FeatureSchemaViolationSeverity : byte
{
    /// <summary>
    /// 信息级（如可选特征应用了默认值）。
    /// 不影响 IsValid，仅记录诊断。
    /// </summary>
    Info = 0,

    /// <summary>
    /// 警告级（统计可疑但可继续使用，如默认值字符串无法解析但特征可选）。
    /// 不影响 IsValid。
    /// </summary>
    Warning = 1,

    /// <summary>
    /// 错误级（输入非法，必须拒绝推理或退化为默认值）。
    /// 使 IsValid=false。
    /// </summary>
    Error = 2
}
