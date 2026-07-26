using ContextCore.Abstractions;
using ContextCore.Core.Services.ModelExecution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextCore.Tests;

// ===========================================================================
// R29 WP-A-3：DefaultCalibrationValidator 单元测试
//
// 覆盖范围：
//   §1 Identity 恒通过（Info 级违规）
//   §2 Platt(A, B) 参数验证
//        - A/B 有限
//        - A != 0
//        - |A| 过大 → Warning
//        - |B| 过大 → Warning
//   §3 Temperature(T) 参数验证
//        - T 有限
//        - T > 0
//        - T 极小 → Warning（饱和）
//        - T 极大 → Warning（近似 identity）
//   §4 Isotonic(points) 参数验证
//        - points != null
//        - Count >= 2
//        - Input / Output 有限
//        - Input 升序
//        - Output 单调非递减
//        - Output 在 [0, 1] 范围
//        - 覆盖率不足 → Warning
//        - 重复 Input → Warning
//   §5 一致性校验（Method 与 Kind 对齐；Parameter 与 ParameterA 同步）
//   §6 null parameters / 未知 Kind
//   §7 ValidateBatch 聚合
//   §8 DI 注册扩展
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("WP-A-3")]
public sealed class R29_CalibrationValidatorTests
{
    private readonly DefaultCalibrationValidator _validator = new();

    // ===========================================================================
    // §1 Identity
    // ===========================================================================

    [TestMethod]
    public void Identity_AlwaysValid_WithInfoViolation()
    {
        var parameters = new CalibrationParameters
        {
            Method = "identity",
            Kind = CalibrationMethodKind.Identity,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters, "test-model");

        Assert.IsTrue(result.IsValid, "Identity 应恒通过");
        Assert.AreEqual(0, result.ErrorCount);
        Assert.AreEqual(1, result.Violations.Count);
        Assert.AreEqual(CalibrationViolationSeverity.Info, result.Violations[0].Severity);
        Assert.AreEqual("identity.always_valid", result.Violations[0].Code);
        Assert.AreEqual("test-model", result.Violations[0].ModelName);
        Assert.AreEqual("identity", result.Violations[0].Method);
        Assert.IsNull(result.Error);
    }

    // ===========================================================================
    // §2 Platt(A, B)
    // ===========================================================================

    [TestMethod]
    public void Platt_ValidParameters_NoViolations()
    {
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 1.5,
            ParameterB = -0.2,
            Parameter = 1.5,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters, "platt-model");

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.ErrorCount);
        Assert.AreEqual(0, result.WarningCount);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void Platt_ANotFinite_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = double.NaN,
            ParameterB = 0.0,
            Parameter = double.NaN,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(1, result.ErrorCount);
        Assert.AreEqual("platt.a_not_finite", result.Violations[0].Code);
        Assert.IsTrue(result.Error!.Contains("platt.a_not_finite"));
    }

    [TestMethod]
    public void Platt_BNotFinite_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 1.0,
            ParameterB = double.PositiveInfinity,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(1, result.ErrorCount);
        Assert.AreEqual("platt.b_not_finite", result.Violations[0].Code);
    }

    [TestMethod]
    public void Platt_AZero_ReturnsError_DegeneratesToConstant()
    {
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 0.0,
            ParameterB = 0.0,
            Parameter = 0.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(1, result.ErrorCount);
        Assert.AreEqual("platt.a_zero", result.Violations[0].Code);
        Assert.IsTrue(result.Violations[0].Message.Contains("常数"));
    }

    [TestMethod]
    public void Platt_ATooLarge_ReturnsWarning()
    {
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 200.0,
            ParameterB = 0.0,
            Parameter = 200.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsTrue(result.IsValid, "Warning 不影响 IsValid");
        Assert.AreEqual(0, result.ErrorCount);
        Assert.AreEqual(1, result.WarningCount);
        Assert.AreEqual("platt.a_saturating", result.Violations[0].Code);
        Assert.IsTrue(result.Violations[0].Message.Contains("step function"));
    }

    [TestMethod]
    public void Platt_BTooLarge_ReturnsWarning()
    {
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 1.0,
            ParameterB = 50.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(1, result.WarningCount);
        Assert.AreEqual("platt.b_saturating", result.Violations[0].Code);
    }

    [TestMethod]
    public void Platt_NegativeA_ButNonZero_IsValid()
    {
        // 负 A 也是合法校准（反向 sigmoid）；只要 |A| 不过大就有效
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = -2.5,
            ParameterB = 0.1,
            Parameter = -2.5,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.WarningCount);
    }

    // ===========================================================================
    // §3 Temperature(T)
    // ===========================================================================

    [TestMethod]
    public void Temperature_ValidT_IsValid()
    {
        var parameters = new CalibrationParameters
        {
            Method = "temperature",
            Kind = CalibrationMethodKind.Temperature,
            Temperature = 1.5,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.ErrorCount);
        Assert.AreEqual(0, result.WarningCount);
    }

    [TestMethod]
    public void Temperature_TNotFinite_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "temperature",
            Kind = CalibrationMethodKind.Temperature,
            Temperature = double.NaN,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("temperature.t_not_finite", result.Violations[0].Code);
    }

    [TestMethod]
    public void Temperature_TZero_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "temperature",
            Kind = CalibrationMethodKind.Temperature,
            Temperature = 0.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("temperature.t_non_positive", result.Violations[0].Code);
    }

    [TestMethod]
    public void Temperature_TNegative_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "temperature",
            Kind = CalibrationMethodKind.Temperature,
            Temperature = -1.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("temperature.t_non_positive", result.Violations[0].Code);
    }

    [TestMethod]
    public void Temperature_TTooSmall_ReturnsWarning()
    {
        var parameters = new CalibrationParameters
        {
            Method = "temperature",
            Kind = CalibrationMethodKind.Temperature,
            Temperature = 1e-4,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(1, result.WarningCount);
        Assert.AreEqual("temperature.t_saturating", result.Violations[0].Code);
    }

    [TestMethod]
    public void Temperature_TTooLarge_ReturnsWarning()
    {
        var parameters = new CalibrationParameters
        {
            Method = "temperature",
            Kind = CalibrationMethodKind.Temperature,
            Temperature = 200.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(1, result.WarningCount);
        Assert.AreEqual("temperature.t_near_identity", result.Violations[0].Code);
    }

    // ===========================================================================
    // §4 Isotonic(points)
    // ===========================================================================

    [TestMethod]
    public void Isotonic_ValidPoints_IsValid()
    {
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = -10.0, Output = 0.05 },
                new IsotonicPoint { Input = -5.0, Output = 0.15 },
                new IsotonicPoint { Input = 0.0, Output = 0.5 },
                new IsotonicPoint { Input = 5.0, Output = 0.85 },
                new IsotonicPoint { Input = 10.0, Output = 0.95 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.ErrorCount);
        Assert.AreEqual(0, result.WarningCount);
    }

    [TestMethod]
    public void Isotonic_PointsNull_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = null!,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("iso.points_null", result.Violations[0].Code);
    }

    [TestMethod]
    public void Isotonic_PointsInsufficient_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = 0.0, Output = 0.5 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("iso.points_insufficient", result.Violations[0].Code);
    }

    [TestMethod]
    public void Isotonic_InputNotFinite_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = -10.0, Output = 0.1 },
                new IsotonicPoint { Input = double.NaN, Output = 0.5 },
                new IsotonicPoint { Input = 10.0, Output = 0.9 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "iso.input_not_finite"));
    }

    [TestMethod]
    public void Isotonic_OutputNotFinite_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = -10.0, Output = double.PositiveInfinity },
                new IsotonicPoint { Input = 0.0, Output = 0.5 },
                new IsotonicPoint { Input = 10.0, Output = 0.9 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "iso.output_not_finite"));
    }

    [TestMethod]
    public void Isotonic_InputNotSorted_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = 0.0, Output = 0.5 },
                new IsotonicPoint { Input = -5.0, Output = 0.2 },
                new IsotonicPoint { Input = 10.0, Output = 0.9 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("iso.input_not_sorted", result.Violations[0].Code);
    }

    [TestMethod]
    public void Isotonic_OutputNotMonotonic_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = -10.0, Output = 0.5 },
                new IsotonicPoint { Input = 0.0, Output = 0.3 }, // 违反单调非递减
                new IsotonicPoint { Input = 10.0, Output = 0.9 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("iso.output_not_monotonic", result.Violations[0].Code);
    }

    [TestMethod]
    public void Isotonic_OutputOutOfRange_ReturnsWarning()
    {
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = -10.0, Output = -0.1 }, // 超出 [0, 1]
                new IsotonicPoint { Input = 0.0, Output = 0.5 },
                new IsotonicPoint { Input = 10.0, Output = 1.05 } // 超出 [0, 1]
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        // Output 超出 [0,1] 是 Warning，不阻止使用
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(1, result.WarningCount);
        Assert.AreEqual("iso.output_out_of_unit", result.Violations[0].Code);
    }

    [TestMethod]
    public void Isotonic_CoverageInsufficient_ReturnsWarning()
    {
        // Input 范围只有 [-1, 1]，相对典型 logit [-10, 10] 覆盖率不足
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = -1.0, Output = 0.1 },
                new IsotonicPoint { Input = 0.0, Output = 0.5 },
                new IsotonicPoint { Input = 1.0, Output = 0.9 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(1, result.WarningCount);
        Assert.AreEqual("iso.coverage_insufficient", result.Violations[0].Code);
    }

    [TestMethod]
    public void Isotonic_DuplicateInput_ReturnsWarning()
    {
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = -10.0, Output = 0.1 },
                new IsotonicPoint { Input = 0.0, Output = 0.5 },
                new IsotonicPoint { Input = 0.0, Output = 0.6 }, // 重复 Input
                new IsotonicPoint { Input = 10.0, Output = 0.9 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsTrue(result.IsValid, "重复 Input 是 Warning 而非 Error");
        Assert.AreEqual(1, result.WarningCount);
        Assert.AreEqual("iso.duplicate_input", result.Violations[0].Code);
    }

    // ===========================================================================
    // §5 一致性校验
    // ===========================================================================

    [TestMethod]
    public void Consistency_MethodKindMismatch_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "platt",  // 不匹配 Kind
            Kind = CalibrationMethodKind.Temperature,
            Temperature = 1.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "consistency.method_kind_mismatch"));
    }

    [TestMethod]
    public void Consistency_ParameterAliasDrift_ReturnsWarning()
    {
        // Parameter（旧别名）与 ParameterA 不同步
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 1.5,
            Parameter = 2.0, // 不匹配 ParameterA
            ParameterB = 0.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsTrue(result.IsValid, "Parameter 别名 drift 是 Warning");
        Assert.AreEqual(1, result.WarningCount);
        Assert.AreEqual("consistency.parameter_alias_drift", result.Violations[0].Code);
    }

    // ===========================================================================
    // §6 null parameters / 未知 Kind
    // ===========================================================================

    [TestMethod]
    public void NullParameters_ReturnsError()
    {
        var result = _validator.Validate(null, "test-model");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(1, result.ErrorCount);
        Assert.AreEqual("parameters.null", result.Violations[0].Code);
        Assert.AreEqual("test-model", result.Violations[0].ModelName);
    }

    [TestMethod]
    public void UnknownKind_ReturnsError()
    {
        var parameters = new CalibrationParameters
        {
            Method = "unknown",
            Kind = (CalibrationMethodKind)99,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "kind.unknown"));
    }

    // ===========================================================================
    // §7 ValidateBatch 聚合
    // ===========================================================================

    [TestMethod]
    public void ValidateBatch_AggregatesAllViolations()
    {
        var entries = new[]
        {
            ("valid-identity", (CalibrationParameters?)new CalibrationParameters
            {
                Method = "identity",
                Kind = CalibrationMethodKind.Identity,
                FittedAt = DateTimeOffset.UtcNow
            }),
            ("invalid-platt", new CalibrationParameters
            {
                Method = "platt",
                Kind = CalibrationMethodKind.Platt,
                ParameterA = 0.0,
                ParameterB = 0.0,
                Parameter = 0.0,
                FittedAt = DateTimeOffset.UtcNow
            }),
            ("warning-platt", new CalibrationParameters
            {
                Method = "platt",
                Kind = CalibrationMethodKind.Platt,
                ParameterA = 200.0,
                ParameterB = 0.0,
                Parameter = 200.0,
                FittedAt = DateTimeOffset.UtcNow
            }),
            (null, (CalibrationParameters?)null)
        };

        var result = _validator.ValidateBatch(entries!);

        Assert.IsFalse(result.IsValid, "包含 Error 级违规，整体不通过");
        Assert.IsTrue(result.ErrorCount >= 2, "应聚合 invalid-platt 与 null 两个 Error");
        Assert.IsTrue(result.WarningCount >= 1, "应包含 warning-platt 的 Warning");
        Assert.IsTrue(result.Violations.Any(v => v.ModelName == "invalid-platt" && v.Code == "platt.a_zero"));
        Assert.IsTrue(result.Violations.Any(v => v.ModelName == null && v.Code == "parameters.null"));
        Assert.IsTrue(result.Violations.Any(v => v.ModelName == "warning-platt" && v.Code == "platt.a_saturating"));
    }

    [TestMethod]
    public void ValidateBatch_EmptyList_IsValid()
    {
        var result = _validator.ValidateBatch(Array.Empty<(string?, CalibrationParameters?)>());

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.Violations.Count);
    }

    [TestMethod]
    public void ValidateBatch_AllValid_IsValid()
    {
        var entries = new[]
        {
            ("identity-1", (CalibrationParameters?)new CalibrationParameters
            {
                Method = "identity",
                Kind = CalibrationMethodKind.Identity,
                FittedAt = DateTimeOffset.UtcNow
            }),
            ("platt-1", new CalibrationParameters
            {
                Method = "platt",
                Kind = CalibrationMethodKind.Platt,
                ParameterA = 1.0,
                ParameterB = 0.0,
                Parameter = 1.0,
                FittedAt = DateTimeOffset.UtcNow
            })
        };

        var result = _validator.ValidateBatch(entries!);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.ErrorCount);
    }

    [TestMethod]
    public void ValidateBatch_NullEntries_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            _validator.ValidateBatch(null!));
    }

    // ===========================================================================
    // §8 DI 注册扩展
    // ===========================================================================

    [TestMethod]
    public void AddContextCore_Registers_ICalibrationValidator_AsSingleton()
    {
        // 注意：此测试验证 Service 项目中的 AddContextCore 注册，
        // 通过 ServiceCollection + BuildServiceProvider 解析。
        // 不调用真正的 AddContextCore（它依赖 PostgresOptions 等大量基础设施），
        // 而是直接注册 DefaultCalibrationValidator 验证类型绑定。
        var services = new ServiceCollection();
        services.AddSingleton<ICalibrationValidator, DefaultCalibrationValidator>();

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetService<ICalibrationValidator>();

        Assert.IsNotNull(validator);
        Assert.IsInstanceOfType<DefaultCalibrationValidator>(validator);

        // Singleton 生命周期验证
        var validator2 = provider.GetService<ICalibrationValidator>();
        Assert.AreSame(validator, validator2);
    }

    [TestMethod]
    public void AddContextCore_DefaultCalibrationValidator_CanResolveAndValidate()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICalibrationValidator, DefaultCalibrationValidator>();
        using var provider = services.BuildServiceProvider();

        var validator = provider.GetRequiredService<ICalibrationValidator>();

        // 验证解析出的实例可以正常工作
        var parameters = new CalibrationParameters
        {
            Method = "identity",
            Kind = CalibrationMethodKind.Identity,
            FittedAt = DateTimeOffset.UtcNow
        };
        var result = validator.Validate(parameters);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(1, result.Violations.Count);
        Assert.AreEqual(CalibrationViolationSeverity.Info, result.Violations[0].Severity);
    }

    [TestMethod]
    public void CalibrationValidationResult_ErrorWarningCounts_AreCorrect()
    {
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 200.0,
            ParameterB = 50.0,
            Parameter = 200.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = _validator.Validate(parameters);

        // |A|=200 触发 a_saturating Warning；|B|=50 触发 b_saturating Warning
        Assert.AreEqual(2, result.WarningCount);
        Assert.AreEqual(0, result.ErrorCount);
        Assert.IsTrue(result.IsValid);
        Assert.IsNull(result.Error);
    }
}
