namespace ContextCore.Abstractions;

// ===========================================================================
// Calibration Validator 契约
//
// 目标（对齐 Production Intelligence 规格）：
// 在模型加载时验证校准参数（Platt / Temperature / Isotonic / Identity）的
// 统计有效性，让 ModelArtifactRegistry 加载 descriptor 后能拒绝在统计上
// 不合理的校准配置，而不是等到推理时被策略实现抛 ArgumentException。
//
// 与 IInferenceResultValidator 的边界：
// - IInferenceResultValidator 验证单次推理输出（NaN / Infinity / Count / Range），
// 在 Scorer 应用分数到排序键之前调用。
// - ICalibrationValidator 验证校准参数本身（参数范围 / 单调性 / 覆盖率），
// 在模型加载与 ICalibrationService.Register* 之后调用。
// 两者互补，不重复：Validator 关心"参数是否合法且统计合理"，
// Result Validator 关心"本次推理输出是否合法"。
//
// 设计原则：
// 1. 不抛异常：返回结构化 CalibrationValidationResult，由调用方决定降级
// （拒绝加载 / 退化为 Identity / 标记为 warning 继续）。
// 2. 完整违规清单：聚合所有违规（Error + Warning），便于诊断。
// 3. 严重程度分级：Error 阻止使用该参数；Warning 表示统计可疑但可继续。
// 4. 不依赖外部统计库：所有检验（单调性、范围、覆盖率）用纯 .NET 实现。
// ===========================================================================

/// <summary>
/// 校准参数验证器。
/// 在模型加载时对 <see cref="CalibrationParameters"/> 执行统计有效性验证。
/// </summary>
/// <remarks>
/// <b>使用模式</b>：
/// <code>
/// var validator = host.Services.GetRequiredService&lt;ICalibrationValidator&gt;();
/// var parameters = calibrationService.GetParameters(modelName);
/// var result = validator.Validate(parameters, modelName);
/// if (!result.IsValid)
/// {
/// // 拒绝加载或退化为 Identity
/// }
/// </code>
/// <para>
/// 验证项（按 <see cref="CalibrationMethodKind"/> 路由）：
/// <list type="bullet">
/// <item><b>Identity</b>：恒通过。</item>
/// <item><b>Platt(A, B)</b>：A / B 有限；A != 0（A=0 使校准退化为常数 sigmoid(B)）；
/// |A| 过大（> 100）使校准饱和为 step function，发出 Warning。</item>
/// <item><b>Temperature(T)</b>：T > 0 且有限；T &lt; 1e-3 使 sigmoid 饱和（Warning）；
/// T > 100 使校准近似 identity（Warning）。</item>
/// <item><b>Isotonic(points)</b>：points != null 且 Count >= 2；
/// 按 Input 升序；Input / Output 均有限；
/// Output 单调非递减（isotonic 性质）；Output 在 [0,1] 范围（若为概率）；
/// Input 范围覆盖模型典型输出（不足时 Warning）。</item>
/// </list>
/// </para>
/// </remarks>
public interface ICalibrationValidator
{
    /// <summary>
    /// 验证一组校准参数的统计有效性。
    /// </summary>
    /// <param name="parameters">待验证的校准参数；为 null 时返回 Error。</param>
    /// <param name="modelName">关联的模型名（用于诊断消息；可空）。</param>
    /// <returns>验证结果（<see cref="CalibrationValidationResult"/>）。</returns>
    CalibrationValidationResult Validate(
        CalibrationParameters? parameters,
        string? modelName = null);

    /// <summary>
    /// 批量验证：对一组 (modelName, parameters) 对执行验证，返回聚合结果。
    /// 任一对返回 Error 时，聚合结果 IsValid=false。
    /// </summary>
    /// <param name="entries">待验证的 (modelName, parameters) 对列表。</param>
    /// <returns>聚合验证结果；包含所有 entry 的违规明细。</returns>
    CalibrationValidationResult ValidateBatch(
        IReadOnlyList<(string? ModelName, CalibrationParameters? Parameters)> entries);
}

/// <summary>
/// 校准验证结果。
/// </summary>
public sealed record CalibrationValidationResult
{
    /// <summary>是否通过验证（无 Error 级违规时为 true；Warning 不影响 IsValid）。</summary>
    public required bool IsValid { get; init; }

    /// <summary>聚合错误消息（IsValid=true 时为 null）。</summary>
    public required string? Error { get; init; }

    /// <summary>所有违规明细（Error + Warning；IsValid=true 时可能含 Warning）。</summary>
    public required IReadOnlyList<CalibrationViolation> Violations { get; init; }

    /// <summary>Error 级违规数量（等于 0 时 IsValid=true）。</summary>
    public int ErrorCount => Violations.Count(v => v.Severity == CalibrationViolationSeverity.Error);

    /// <summary>Warning 级违规数量（不影响 IsValid）。</summary>
    public int WarningCount => Violations.Count(v => v.Severity == CalibrationViolationSeverity.Warning);
}

/// <summary>
/// 单条校准违规。
/// </summary>
public sealed record CalibrationViolation
{
    /// <summary>严重程度（Error / Warning / Info）。</summary>
    public required CalibrationViolationSeverity Severity { get; init; }

    /// <summary>违规代码（机器可读，如 "platt.a_zero"、"iso.not_monotonic"）。</summary>
    public required string Code { get; init; }

    /// <summary>人类可读的违规描述。</summary>
    public required string Message { get; init; }

    /// <summary>关联的模型名（可空）。</summary>
    public string? ModelName { get; init; }

    /// <summary>关联的校准方法名（"identity" / "platt" / "temperature" / "isotonic"）。</summary>
    public string? Method { get; init; }
}

/// <summary>
/// 校准违规严重程度。
/// </summary>
public enum CalibrationViolationSeverity : byte
{
    /// <summary>
    /// 信息级（如 Identity 恒通过、Isotonic 退化）。
    /// 不影响 IsValid，仅记录诊断。
    /// </summary>
    Info = 0,

    /// <summary>
    /// 警告级（统计可疑但可继续使用，如 Platt |A| 过大、Isotonic 覆盖率不足）。
    /// 不影响 IsValid。
    /// </summary>
    Warning = 1,

    /// <summary>
    /// 错误级（参数非法，必须拒绝加载或退化为 Identity）。
    /// 使 IsValid=false。
    /// </summary>
    Error = 2
}
