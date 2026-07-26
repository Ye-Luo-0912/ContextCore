using ContextCore.Abstractions;

namespace ContextCore.Core.Services.ModelExecution;

// ===========================================================================
// R29 WP-A-3：Default Calibration Validator
//
// 目标：
//   在模型加载时对 CalibrationParameters 执行统计有效性验证，按 Kind 路由：
//     - Identity        —— 恒通过（Info：始终恒等变换）
//     - Platt(A, B)     —— A/B 有限；A != 0；|A| 过大 → Warning（饱和）
//     - Temperature(T) —— T > 0 且有限；T 极小 → Warning（饱和）；T 极大 → Warning（近似 identity）
//     - Isotonic(points)—— Count >= 2；Input 升序；Input/Output 有限；
//                          Output 单调非递减；Output 在 [0,1]；覆盖率不足 → Warning
//
// 设计原则：
//   1. 不抛异常：所有非法情形转为 Error 级 CalibrationViolation。
//   2. 完整违规清单：聚合所有违规（Error + Warning + Info），便于诊断。
//   3. 不依赖外部统计库：所有检验用纯 .NET 实现。
//   4. 与 ICalibrationStrategy 实现的运行时校验互补：
//      策略在 Calibrate() 中抛 ArgumentException 是 fail-fast；
//      Validator 在加载时返回结构化结果是 fail-safe（让上层决定降级策略）。
// ===========================================================================

/// <summary>
/// R29 WP-A-3：默认校准参数验证器。
/// </summary>
public sealed class DefaultCalibrationValidator : ICalibrationValidator
{
    // 阈值常量（基于统计经验，可通过 options 暴露但当前固定）
    private const double PlattSaturatingAbsA = 100.0;
    private const double TemperatureSaturatingMax = 1e-3;
    private const double TemperatureNearIdentityMin = 100.0;
    private const double ProbabilityEpsilon = 1e-9;
    private const double IsotonicCoverageWarningThreshold = 0.5; // 输入范围相对典型 logit 范围 [-10, 10] 的覆盖率

    /// <inheritdoc />
    public CalibrationValidationResult Validate(
        CalibrationParameters? parameters,
        string? modelName = null)
    {
        if (parameters is null)
        {
            return BuildResult(new[]
            {
                new CalibrationViolation
                {
                    Severity = CalibrationViolationSeverity.Error,
                    Code = "parameters.null",
                    Message = "校准参数为 null；无法验证。",
                    ModelName = modelName,
                    Method = null
                }
            });
        }

        var violations = new List<CalibrationViolation>();
        ValidateConsistency(parameters, modelName, violations);
        ValidateByKind(parameters, modelName, violations);
        return BuildResult(violations);
    }

    /// <inheritdoc />
    public CalibrationValidationResult ValidateBatch(
        IReadOnlyList<(string? ModelName, CalibrationParameters? Parameters)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return BuildResult(Array.Empty<CalibrationViolation>());
        }

        var allViolations = new List<CalibrationViolation>();
        foreach (var (modelName, parameters) in entries)
        {
            var result = Validate(parameters, modelName);
            allViolations.AddRange(result.Violations);
        }

        return BuildResult(allViolations);
    }

    // -----------------------------------------------------------------------
    // 一致性校验：Method 字符串与 Kind 枚举对齐
    // -----------------------------------------------------------------------

    private static void ValidateConsistency(
        CalibrationParameters parameters,
        string? modelName,
        List<CalibrationViolation> violations)
    {
        var expectedMethod = parameters.Kind switch
        {
            CalibrationMethodKind.Identity => "identity",
            CalibrationMethodKind.Platt => "platt",
            CalibrationMethodKind.Temperature => "temperature",
            CalibrationMethodKind.Isotonic => "isotonic",
            _ => null
        };

        if (expectedMethod is not null
            && !string.Equals(expectedMethod, parameters.Method, StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(new CalibrationViolation
            {
                Severity = CalibrationViolationSeverity.Error,
                Code = "consistency.method_kind_mismatch",
                Message = $"Method='{parameters.Method}' 与 Kind={parameters.Kind} 不一致；期望 Method='{expectedMethod}'。",
                ModelName = modelName,
                Method = parameters.Method
            });
        }

        // R28-D 兼容别名：Parameter 应与 ParameterA 同步
        if (parameters.Kind == CalibrationMethodKind.Platt
            && !Equals(parameters.Parameter, parameters.ParameterA))
        {
            violations.Add(new CalibrationViolation
            {
                Severity = CalibrationViolationSeverity.Warning,
                Code = "consistency.parameter_alias_drift",
                Message = $"Parameter({parameters.Parameter}) 与 ParameterA({parameters.ParameterA}) 不同步；Parameter 是 R28-D 兼容别名，应与 ParameterA 一致。",
                ModelName = modelName,
                Method = parameters.Method
            });
        }
    }

    // -----------------------------------------------------------------------
    // 按 Kind 路由的统计验证
    // -----------------------------------------------------------------------

    private static void ValidateByKind(
        CalibrationParameters parameters,
        string? modelName,
        List<CalibrationViolation> violations)
    {
        switch (parameters.Kind)
        {
            case CalibrationMethodKind.Identity:
                ValidateIdentity(parameters, modelName, violations);
                break;
            case CalibrationMethodKind.Platt:
                ValidatePlatt(parameters, modelName, violations);
                break;
            case CalibrationMethodKind.Temperature:
                ValidateTemperature(parameters, modelName, violations);
                break;
            case CalibrationMethodKind.Isotonic:
                ValidateIsotonic(parameters, modelName, violations);
                break;
            default:
                violations.Add(new CalibrationViolation
                {
                    Severity = CalibrationViolationSeverity.Error,
                    Code = "kind.unknown",
                    Message = $"未知的 CalibrationMethodKind={parameters.Kind}。",
                    ModelName = modelName,
                    Method = parameters.Method
                });
                break;
        }
    }

    // -------------------- Identity --------------------

    private static void ValidateIdentity(
        CalibrationParameters parameters,
        string? modelName,
        List<CalibrationViolation> violations)
    {
        // Identity 恒通过；记录 Info 让审计可追溯
        violations.Add(new CalibrationViolation
        {
            Severity = CalibrationViolationSeverity.Info,
            Code = "identity.always_valid",
            Message = "Identity 校准恒等变换，统计上恒通过；Calibrate(raw) = raw。",
            ModelName = modelName,
            Method = parameters.Method
        });
    }

    // -------------------- Platt(A, B) --------------------

    private static void ValidatePlatt(
        CalibrationParameters parameters,
        string? modelName,
        List<CalibrationViolation> violations)
    {
        var a = parameters.ParameterA;
        var b = parameters.ParameterB;

        // 1. A / B 必须有限
        if (!IsFinite(a))
        {
            violations.Add(Error("platt.a_not_finite",
                $"Platt ParameterA={FormatDouble(a)} 不是有限数值（NaN/Infinity）；无法用于校准。",
                modelName, parameters.Method));
        }
        if (!IsFinite(b))
        {
            violations.Add(Error("platt.b_not_finite",
                $"Platt ParameterB={FormatDouble(b)} 不是有限数值（NaN/Infinity）；无法用于校准。",
                modelName, parameters.Method));
        }

        if (!IsFinite(a) || !IsFinite(b))
        {
            return; // 后续校验依赖有限值
        }

        // 2. A != 0（A=0 使校准退化为常数 sigmoid(B)）
        if (Math.Abs(a) < ProbabilityEpsilon)
        {
            violations.Add(Error("platt.a_zero",
                $"Platt ParameterA={FormatDouble(a)} ≈ 0；校准将退化为常数 sigmoid(B)={FormatDouble(SigmoidSaturated(b))}，无区分度。",
                modelName, parameters.Method));
        }

        // 3. |A| 过大 → Warning（sigmoid 饱和为 step function）
        if (Math.Abs(a) > PlattSaturatingAbsA)
        {
            violations.Add(Warning("platt.a_saturating",
                $"Platt |ParameterA|={FormatDouble(Math.Abs(a))} > {PlattSaturatingAbsA}；sigmoid 将在 |raw| > {35.0 / Math.Abs(a):F4} 处饱和，校准接近 step function。",
                modelName, parameters.Method));
        }

        // 4. B 范围合理性：|B| > 35 会使 sigmoid 饱和
        if (Math.Abs(b) > 35.0)
        {
            violations.Add(Warning("platt.b_saturating",
                $"Platt |ParameterB|={FormatDouble(Math.Abs(b))} > 35；当 A*raw ≈ 0 时 sigmoid(B) 已饱和到 {FormatDouble(SigmoidSaturated(b))}。",
                modelName, parameters.Method));
        }
    }

    // -------------------- Temperature(T) --------------------

    private static void ValidateTemperature(
        CalibrationParameters parameters,
        string? modelName,
        List<CalibrationViolation> violations)
    {
        var t = parameters.Temperature;

        // 1. T 必须有限
        if (!IsFinite(t))
        {
            violations.Add(Error("temperature.t_not_finite",
                $"Temperature T={FormatDouble(t)} 不是有限数值（NaN/Infinity）；无法用于校准。",
                modelName, parameters.Method));
            return;
        }

        // 2. T > 0
        if (t <= 0.0)
        {
            violations.Add(Error("temperature.t_non_positive",
                $"Temperature T={FormatDouble(t)} <= 0；T 必须 > 0（sigmoid(raw/T) 要求除数合法）。",
                modelName, parameters.Method));
            return;
        }

        // 3. T 极小 → Warning（sigmoid 饱和）
        if (t < TemperatureSaturatingMax)
        {
            violations.Add(Warning("temperature.t_saturating",
                $"Temperature T={FormatDouble(t)} < {TemperatureSaturatingMax}；sigmoid 将在 |raw| > {35.0 * t:F4} 处饱和，校准接近 step function。",
                modelName, parameters.Method));
        }

        // 4. T 极大 → Warning（近似 identity）
        if (t > TemperatureNearIdentityMin)
        {
            violations.Add(Warning("temperature.t_near_identity",
                $"Temperature T={FormatDouble(t)} > {TemperatureNearIdentityMin}；sigmoid(raw/T) 近似 sigmoid(0) = 0.5，校准接近常数。",
                modelName, parameters.Method));
        }
    }

    // -------------------- Isotonic(points) --------------------

    private static void ValidateIsotonic(
        CalibrationParameters parameters,
        string? modelName,
        List<CalibrationViolation> violations)
    {
        var points = parameters.IsotonicPoints;

        // 1. points 不能为 null
        if (points is null)
        {
            violations.Add(Error("iso.points_null",
                "IsotonicPoints 为 null；必须提供至少 2 个点才能执行分段线性插值。",
                modelName, parameters.Method));
            return;
        }

        // 2. 至少 2 个点
        if (points.Count < 2)
        {
            violations.Add(Error("iso.points_insufficient",
                $"IsotonicPoints.Count={points.Count} < 2；点数不足无法插值，策略将退化为 identity。",
                modelName, parameters.Method));
            return;
        }

        // 3. 所有 Input / Output 必须有限
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!IsFinite(p.Input))
            {
                violations.Add(Error("iso.input_not_finite",
                    $"IsotonicPoints[{i}].Input={FormatDouble(p.Input)} 不是有限数值。",
                    modelName, parameters.Method));
            }
            if (!IsFinite(p.Output))
            {
                violations.Add(Error("iso.output_not_finite",
                    $"IsotonicPoints[{i}].Output={FormatDouble(p.Output)} 不是有限数值。",
                    modelName, parameters.Method));
            }
        }

        // 4. Input 必须升序
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].Input < points[i - 1].Input)
            {
                violations.Add(Error("iso.input_not_sorted",
                    $"IsotonicPoints 未按 Input 升序：points[{i}].Input={FormatDouble(points[i].Input)} < points[{i - 1}].Input={FormatDouble(points[i - 1].Input)}。",
                    modelName, parameters.Method));
                break; // 仅报告第一个违规，避免刷屏
            }
        }

        // 5. Output 单调非递减（isotonic 性质）
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].Output < points[i - 1].Output - ProbabilityEpsilon)
            {
                violations.Add(Error("iso.output_not_monotonic",
                    $"IsotonicPoints Output 非单调非递减：points[{i}].Output={FormatDouble(points[i].Output)} < points[{i - 1}].Output={FormatDouble(points[i - 1].Output)}；isotonic 回归要求 Output 单调非递减。",
                    modelName, parameters.Method));
                break;
            }
        }

        // 6. Output 应在 [0, 1] 范围（若为概率校准）
        for (var i = 0; i < points.Count; i++)
        {
            var output = points[i].Output;
            if (output < -ProbabilityEpsilon || output > 1.0 + ProbabilityEpsilon)
            {
                violations.Add(Warning("iso.output_out_of_unit",
                    $"IsotonicPoints[{i}].Output={FormatDouble(output)} 不在 [0, 1] 范围；若为概率校准应限制到 [0, 1]。",
                    modelName, parameters.Method));
                break;
            }
        }

        // 7. Input 范围覆盖率（相对典型 logit 范围 [-10, 10]）
        var minInput = points[0].Input;
        var maxInput = points[^1].Input;
        var range = maxInput - minInput;
        if (range < 20.0 * IsotonicCoverageWarningThreshold)
        {
            violations.Add(Warning("iso.coverage_insufficient",
                $"IsotonicPoints Input 范围 [{FormatDouble(minInput)}, {FormatDouble(maxInput)}] 跨度 {FormatDouble(range)}；相对典型 logit 范围 [-10, 10] 覆盖不足，超出范围的输入将被 clamp 到边界。",
                modelName, parameters.Method));
        }

        // 8. 重复 Input → Warning（会导致插值除零，策略内部已 clamp 但仍可疑）
        for (var i = 1; i < points.Count; i++)
        {
            if (Math.Abs(points[i].Input - points[i - 1].Input) < ProbabilityEpsilon)
            {
                violations.Add(Warning("iso.duplicate_input",
                    $"IsotonicPoints[{i}].Input 与 points[{i - 1}].Input 近似相等（{FormatDouble(points[i].Input)}）；插值时可能除零，策略将退化为取前一点 Output。",
                    modelName, parameters.Method));
                break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // 辅助
    // -----------------------------------------------------------------------

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

    private static string FormatDouble(double x)
    {
        if (double.IsNaN(x)) return "NaN";
        if (double.IsPositiveInfinity(x)) return "+Infinity";
        if (double.IsNegativeInfinity(x)) return "-Infinity";
        return x.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double SigmoidSaturated(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x >= 35.0) return 1.0;
        if (x <= -35.0) return 0.0;
        return 1.0 / (1.0 + Math.Exp(-x));
    }

    private static CalibrationViolation Error(string code, string message, string? modelName, string? method)
        => new()
        {
            Severity = CalibrationViolationSeverity.Error,
            Code = code,
            Message = message,
            ModelName = modelName,
            Method = method
        };

    private static CalibrationViolation Warning(string code, string message, string? modelName, string? method)
        => new()
        {
            Severity = CalibrationViolationSeverity.Warning,
            Code = code,
            Message = message,
            ModelName = modelName,
            Method = method
        };

    private static CalibrationValidationResult BuildResult(IReadOnlyList<CalibrationViolation> violations)
    {
        var errorCount = violations.Count(v => v.Severity == CalibrationViolationSeverity.Error);
        var isValid = errorCount == 0;

        string? error = null;
        if (!isValid)
        {
            var errorMessages = violations
                .Where(v => v.Severity == CalibrationViolationSeverity.Error)
                .Select(v => $"[{v.Code}] {v.Message}");
            error = string.Join("; ", errorMessages);
        }

        return new CalibrationValidationResult
        {
            IsValid = isValid,
            Error = error,
            Violations = violations
        };
    }
}
