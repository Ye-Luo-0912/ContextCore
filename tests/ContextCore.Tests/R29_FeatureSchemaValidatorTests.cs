using ContextCore.Abstractions;
using ContextCore.Core.Services.ModelExecution;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

// ===========================================================================
// R29 WP-A-4：DefaultFeatureSchemaValidator 单元测试
//
// 覆盖范围：
//   §1 SchemaVersion 校验
//        - 匹配 / 不匹配 / 空
//   §2 FeatureVector 必填特征
//        - 全部提供 / 缺失必填 / 缺失可选
//   §3 未知特征检查（严格模式）
//        - 开启：拒绝未知特征
//        - 关闭：允许未知特征
//   §4 类型可转换性
//        - Numeric / Categorical / Boolean / Text
//        - 类型不匹配
//   §5 默认值回退
//        - 必填缺失 + 有默认值 → Warning
//        - 必填缺失 + 无默认值 → Error
//        - 默认值无法解析 → Error
//   §6 FeatureBatch 校验
//        - SchemaVersion / FeatureCount / FeatureNames 顺序 / Values 长度 / NaN 检测
//   §7 ValidateBatch 聚合
//   §8 DI 注册扩展
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("WP-A-4")]
public sealed class R29_FeatureSchemaValidatorTests
{
    private readonly DefaultFeatureSchemaValidator _validator = new();
    private readonly FeatureSchema _schema = BuildTestSchema();

    private static FeatureSchema BuildTestSchema()
    {
        return new FeatureSchema
        {
            Version = "test-schema-v1",
            CreatedAt = DateTimeOffset.UtcNow,
            Features = new[]
            {
                new FeatureDefinition { Name = "lexical_score", Type = FeatureType.Numeric, IsRequired = true, DefaultValue = null },
                new FeatureDefinition { Name = "semantic_score", Type = FeatureType.Numeric, IsRequired = true, DefaultValue = "0" },
                new FeatureDefinition { Name = "category", Type = FeatureType.Categorical, IsRequired = true, DefaultValue = "unknown" },
                new FeatureDefinition { Name = "is_fresh", Type = FeatureType.Boolean, IsRequired = false, DefaultValue = "false" },
                new FeatureDefinition { Name = "recency_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = null }
            }
        };
    }

    private static FeatureVector MakeInput(string schemaVersion, params (string name, object value)[] features)
    {
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in features)
        {
            dict[name] = value;
        }
        return new FeatureVector
        {
            SchemaVersion = schemaVersion,
            Values = dict
        };
    }

    // ===========================================================================
    // §1 SchemaVersion 校验
    // ===========================================================================

    [TestMethod]
    public void SchemaVersion_Match_IsValid()
    {
        var input = MakeInput("test-schema-v1",
            ("lexical_score", 0.5),
            ("semantic_score", 0.7),
            ("category", "doc"),
            ("is_fresh", true));

        var result = _validator.Validate(_schema, input);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.ErrorCount);
        // recency_score 可选缺失 → Info
        Assert.IsTrue(result.Violations.All(v => v.Severity == FeatureSchemaViolationSeverity.Info));
    }

    [TestMethod]
    public void SchemaVersion_Mismatch_ReturnsError()
    {
        var input = MakeInput("wrong-schema-v2",
            ("lexical_score", 0.5),
            ("semantic_score", 0.7),
            ("category", "doc"));

        var result = _validator.Validate(_schema, input);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "schema.version_mismatch"));
        Assert.IsTrue(result.Error!.Contains("schema.version_mismatch"));
    }

    [TestMethod]
    public void SchemaVersion_Empty_ReturnsError()
    {
        var input = MakeInput("",
            ("lexical_score", 0.5),
            ("semantic_score", 0.7),
            ("category", "doc"));

        var result = _validator.Validate(_schema, input);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "schema.version_missing"));
    }

    // ===========================================================================
    // §2 FeatureVector 必填特征
    // ===========================================================================

    [TestMethod]
    public void MissingRequiredFeature_WithoutDefault_ReturnsError()
    {
        // lexical_score 必填且无默认值
        var input = MakeInput("test-schema-v1",
            ("semantic_score", 0.7),
            ("category", "doc"));

        var result = _validator.Validate(_schema, input);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.missing_required" && v.FeatureName == "lexical_score"));
    }

    [TestMethod]
    public void MissingRequiredFeature_WithDefault_AppliesWarning()
    {
        // semantic_score 必填且默认值 "0" 可解析
        var input = MakeInput("test-schema-v1",
            ("lexical_score", 0.5),
            ("category", "doc"));

        var result = _validator.Validate(_schema, input);

        Assert.IsTrue(result.IsValid, "有可解析默认值 → Warning，不阻止");
        Assert.AreEqual(1, result.WarningCount);
        Assert.AreEqual("feature.applied_default", result.Violations[0].Code);
        Assert.AreEqual("semantic_score", result.Violations[0].FeatureName);
    }

    [TestMethod]
    public void MissingOptionalFeature_IsValid_WithInfo()
    {
        // recency_score 可选缺失
        var input = MakeInput("test-schema-v1",
            ("lexical_score", 0.5),
            ("semantic_score", 0.7),
            ("category", "doc"),
            ("is_fresh", true));

        var result = _validator.Validate(_schema, input);

        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.optional_missing" && v.FeatureName == "recency_score"));
        Assert.AreEqual(FeatureSchemaViolationSeverity.Info, result.Violations.First(v => v.Code == "feature.optional_missing").Severity);
    }

    [TestMethod]
    public void RequiredFeature_NullValue_WithDefault_AppliesWarning()
    {
        // semantic_score 必填且默认值可解析；这里值显式为 null
        var input = MakeInput("test-schema-v1",
            ("lexical_score", 0.5),
            ("semantic_score", null!),
            ("category", "doc"));

        var result = _validator.Validate(_schema, input);

        Assert.IsTrue(result.IsValid, "必填值为 null + 默认值可解析 → Warning");
        Assert.AreEqual("feature.applied_default", result.Violations[0].Code);
    }

    [TestMethod]
    public void RequiredFeature_NullValue_WithoutDefault_ReturnsError()
    {
        // lexical_score 必填且无默认值；显式 null
        var input = MakeInput("test-schema-v1",
            ("lexical_score", null!),
            ("semantic_score", 0.7),
            ("category", "doc"));

        var result = _validator.Validate(_schema, input);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.missing_required" && v.FeatureName == "lexical_score"));
    }

    [TestMethod]
    public void OptionalFeature_NullValue_IsValid_WithInfo()
    {
        // is_fresh 可选；显式 null
        var input = MakeInput("test-schema-v1",
            ("lexical_score", 0.5),
            ("semantic_score", 0.7),
            ("category", "doc"),
            ("is_fresh", null!));

        var result = _validator.Validate(_schema, input);

        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.optional_null" && v.FeatureName == "is_fresh"));
    }

    // ===========================================================================
    // §3 未知特征检查（严格模式）
    // ===========================================================================

    [TestMethod]
    public void UnknownFeature_StrictMode_ReturnsError()
    {
        var input = MakeInput("test-schema-v1",
            ("lexical_score", 0.5),
            ("semantic_score", 0.7),
            ("category", "doc"),
            ("unknown_feature", "extra"));

        var result = _validator.Validate(_schema, input);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.unknown" && v.FeatureName == "unknown_feature"));
    }

    [TestMethod]
    public void UnknownFeature_LenientMode_IsValid()
    {
        var lenientValidator = new DefaultFeatureSchemaValidator(
            new FeatureSchemaValidatorOptions { StrictUnknownFeatures = false });

        var input = MakeInput("test-schema-v1",
            ("lexical_score", 0.5),
            ("semantic_score", 0.7),
            ("category", "doc"),
            ("unknown_feature", "extra"));

        var result = lenientValidator.Validate(_schema, input);

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.Violations.Any(v => v.Code == "feature.unknown"));
    }

    // ===========================================================================
    // §4 类型可转换性
    // ===========================================================================

    [TestMethod]
    public void TypeMismatch_NumericFeatureWithNonNumeric_ReturnsError()
    {
        // lexical_score 是 Numeric，传入字符串 "abc" 无法解析
        var input = MakeInput("test-schema-v1",
            ("lexical_score", "abc"),
            ("semantic_score", 0.7),
            ("category", "doc"));

        var result = _validator.Validate(_schema, input);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.type_mismatch" && v.FeatureName == "lexical_score"));
    }

    [TestMethod]
    public void TypeMatch_NumericFeatureWithNumericString_IsValid()
    {
        // "0.5" 可解析为 double → Numeric 合法
        var input = MakeInput("test-schema-v1",
            ("lexical_score", "0.5"),
            ("semantic_score", 0.7),
            ("category", "doc"));

        var result = _validator.Validate(_schema, input);

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.Violations.Any(v => v.Code == "feature.type_mismatch"));
    }

    [TestMethod]
    public void TypeMismatch_CategoricalWithNonString_ReturnsError()
    {
        // category 是 Categorical，传入 int 不匹配
        var input = MakeInput("test-schema-v1",
            ("lexical_score", 0.5),
            ("semantic_score", 0.7),
            ("category", 123));

        var result = _validator.Validate(_schema, input);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.type_mismatch" && v.FeatureName == "category"));
    }

    [TestMethod]
    public void TypeMismatch_BooleanWithNonBool_ReturnsError()
    {
        // is_fresh 是 Boolean，传入 string "yes"（非 bool.TryParse 可解析格式）
        var input = MakeInput("test-schema-v1",
            ("lexical_score", 0.5),
            ("semantic_score", 0.7),
            ("category", "doc"),
            ("is_fresh", "yes"));

        var result = _validator.Validate(_schema, input);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.type_mismatch" && v.FeatureName == "is_fresh"));
    }

    [TestMethod]
    public void TypeMatch_BooleanWithTrue_IsValid()
    {
        var input = MakeInput("test-schema-v1",
            ("lexical_score", 0.5),
            ("semantic_score", 0.7),
            ("category", "doc"),
            ("is_fresh", true));

        var result = _validator.Validate(_schema, input);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void NumericType_AcceptsAllNumericKinds()
    {
        // int / long / float / double / decimal 均应被识别为 Numeric
        var testCases = new object[] { 1, 2L, 3.0f, 4.0, 5.0m };
        foreach (var value in testCases)
        {
            var input = MakeInput("test-schema-v1",
                ("lexical_score", value),
                ("semantic_score", 0.7),
                ("category", "doc"));

            var result = _validator.Validate(_schema, input);
            Assert.IsTrue(result.IsValid, $"数值类型 {value.GetType().Name} 应被接受为 Numeric");
        }
    }

    // ===========================================================================
    // §5 默认值回退
    // ===========================================================================

    [TestMethod]
    public void DefaultValue_Unparseable_ReturnsError()
    {
        // 自定义 schema：必填 Numeric 特征默认值为 "abc"（不可解析）
        var schema = new FeatureSchema
        {
            Version = "v-with-bad-default",
            CreatedAt = DateTimeOffset.UtcNow,
            Features = new[]
            {
                new FeatureDefinition { Name = "score", Type = FeatureType.Numeric, IsRequired = true, DefaultValue = "abc" }
            }
        };

        var input = MakeInput("v-with-bad-default");

        var result = _validator.Validate(schema, input);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.default_value_unparseable" && v.FeatureName == "score"));
    }

    [TestMethod]
    public void DefaultValue_Parseable_AppliedWithWarning()
    {
        // 自定义 schema：必填 Numeric 特征默认值为 "0.5"
        var schema = new FeatureSchema
        {
            Version = "v-with-good-default",
            CreatedAt = DateTimeOffset.UtcNow,
            Features = new[]
            {
                new FeatureDefinition { Name = "score", Type = FeatureType.Numeric, IsRequired = true, DefaultValue = "0.5" }
            }
        };

        var input = MakeInput("v-with-good-default");

        var result = _validator.Validate(schema, input);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(1, result.WarningCount);
        Assert.AreEqual("feature.applied_default", result.Violations[0].Code);
    }

    // ===========================================================================
    // §6 FeatureBatch 校验
    // ===========================================================================

    [TestMethod]
    public void FeatureBatch_Valid_IsValid()
    {
        var batch = new FeatureBatch
        {
            SchemaVersion = "test-schema-v1",
            Values = new float[] { 0.5f, 0.7f, 1.0f, 0f, 0.3f },
            RowCount = 1,
            FeatureCount = 5,
            FeatureNames = new[] { "lexical_score", "semantic_score", "category", "is_fresh", "recency_score" }
        };

        var result = _validator.Validate(_schema, batch);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.ErrorCount);
    }

    [TestMethod]
    public void FeatureBatch_SchemaVersionMismatch_ReturnsError()
    {
        var batch = new FeatureBatch
        {
            SchemaVersion = "wrong-version",
            Values = new float[] { 0.5f, 0.7f, 1.0f, 0f, 0.3f },
            RowCount = 1,
            FeatureCount = 5,
            FeatureNames = new[] { "lexical_score", "semantic_score", "category", "is_fresh", "recency_score" }
        };

        var result = _validator.Validate(_schema, batch);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("schema.version_mismatch", result.Violations[0].Code);
    }

    [TestMethod]
    public void FeatureBatch_FeatureCountMismatch_ReturnsError()
    {
        var batch = new FeatureBatch
        {
            SchemaVersion = "test-schema-v1",
            Values = new float[] { 0.5f, 0.7f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "lexical_score", "semantic_score" }
        };

        var result = _validator.Validate(_schema, batch);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "schema.feature_count_mismatch"));
    }

    [TestMethod]
    public void FeatureBatch_FeatureNameMismatch_ReturnsError()
    {
        var batch = new FeatureBatch
        {
            SchemaVersion = "test-schema-v1",
            Values = new float[] { 0.5f, 0.7f, 1.0f, 0f, 0.3f },
            RowCount = 1,
            FeatureCount = 5,
            FeatureNames = new[] { "lexical_score", "semantic_score", "category", "is_fresh", "WRONG_NAME" }
        };

        var result = _validator.Validate(_schema, batch);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "schema.feature_name_mismatch" && v.FeatureName == "recency_score"));
    }

    [TestMethod]
    public void FeatureBatch_ValuesLengthMismatch_ReturnsError()
    {
        var batch = new FeatureBatch
        {
            SchemaVersion = "test-schema-v1",
            Values = new float[] { 0.5f, 0.7f, 1.0f }, // 长度不匹配 RowCount × FeatureCount
            RowCount = 1,
            FeatureCount = 5,
            FeatureNames = new[] { "lexical_score", "semantic_score", "category", "is_fresh", "recency_score" }
        };

        var result = _validator.Validate(_schema, batch);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "batch.values_length_mismatch"));
    }

    [TestMethod]
    public void FeatureBatch_NaNValue_ReturnsError()
    {
        var batch = new FeatureBatch
        {
            SchemaVersion = "test-schema-v1",
            Values = new float[] { 0.5f, float.NaN, 1.0f, 0f, 0.3f },
            RowCount = 1,
            FeatureCount = 5,
            FeatureNames = new[] { "lexical_score", "semantic_score", "category", "is_fresh", "recency_score" }
        };

        var result = _validator.Validate(_schema, batch);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "batch.value_not_finite" && v.FeatureName == "semantic_score"));
        Assert.AreEqual(0, result.Violations.First(v => v.Code == "batch.value_not_finite").RowIndex);
    }

    [TestMethod]
    public void FeatureBatch_InfinityValue_ReturnsError()
    {
        var batch = new FeatureBatch
        {
            SchemaVersion = "test-schema-v1",
            Values = new float[] { 0.5f, 0.7f, 1.0f, 0f, float.PositiveInfinity },
            RowCount = 1,
            FeatureCount = 5,
            FeatureNames = new[] { "lexical_score", "semantic_score", "category", "is_fresh", "recency_score" }
        };

        var result = _validator.Validate(_schema, batch);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "batch.value_not_finite" && v.FeatureName == "recency_score"));
    }

    [TestMethod]
    public void FeatureBatch_MultiRow_AllValid_IsValid()
    {
        var batch = new FeatureBatch
        {
            SchemaVersion = "test-schema-v1",
            Values = new float[]
            {
                0.5f, 0.7f, 1.0f, 0f, 0.3f,
                0.6f, 0.8f, 1.0f, 1f, 0.4f
            },
            RowCount = 2,
            FeatureCount = 5,
            FeatureNames = new[] { "lexical_score", "semantic_score", "category", "is_fresh", "recency_score" }
        };

        var result = _validator.Validate(_schema, batch);

        Assert.IsTrue(result.IsValid);
    }

    // ===========================================================================
    // §7 ValidateBatch 聚合
    // ===========================================================================

    [TestMethod]
    public void ValidateBatch_AggregatesAllViolations_WithRowIndex()
    {
        var inputs = new[]
        {
            MakeInput("test-schema-v1",
                ("lexical_score", 0.5),
                ("semantic_score", 0.7),
                ("category", "doc")), // 合法
            MakeInput("test-schema-v1",
                ("lexical_score", 0.5)), // 缺失 semantic_score（有默认）+ category（有默认）
            MakeInput("wrong-version",
                ("lexical_score", 0.5),
                ("semantic_score", 0.7),
                ("category", "doc")), // schema version 不匹配
            null! // null 向量
        };

        var result = _validator.ValidateBatch(_schema, inputs!);

        Assert.IsFalse(result.IsValid);
        // row 1: 2 个 applied_default Warning
        // row 2: 1 个 version_mismatch Error
        // row 3: 1 个 null_vector Error
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.applied_default" && v.RowIndex == 1));
        Assert.IsTrue(result.Violations.Any(v => v.Code == "schema.version_mismatch" && v.RowIndex == 2));
        Assert.IsTrue(result.Violations.Any(v => v.Code == "input.null_vector" && v.RowIndex == 3));
    }

    [TestMethod]
    public void ValidateBatch_EmptyList_IsValid()
    {
        var result = _validator.ValidateBatch(_schema, Array.Empty<FeatureVector>());

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.Violations.Count);
    }

    [TestMethod]
    public void ValidateBatch_AllValid_IsValid()
    {
        var inputs = new[]
        {
            MakeInput("test-schema-v1",
                ("lexical_score", 0.5),
                ("semantic_score", 0.7),
                ("category", "doc")),
            MakeInput("test-schema-v1",
                ("lexical_score", 0.6),
                ("semantic_score", 0.8),
                ("category", "code"))
        };

        var result = _validator.ValidateBatch(_schema, inputs);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.ErrorCount);
    }

    [TestMethod]
    public void ValidateBatch_NullInputs_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            _validator.ValidateBatch(_schema, null!));
    }

    [TestMethod]
    public void Validate_NullSchema_ThrowsArgumentNullException()
    {
        var input = MakeInput("v1", ("a", 1));
        Assert.ThrowsException<ArgumentNullException>(() =>
            _validator.Validate(null!, input));
    }

    [TestMethod]
    public void Validate_NullInput_ThrowsArgumentNullException()
    {
        FeatureVector nullInput = null!;
        Assert.ThrowsException<ArgumentNullException>(() =>
            _validator.Validate(_schema, nullInput));
    }

    [TestMethod]
    public void Validate_NullBatch_ThrowsArgumentNullException()
    {
        FeatureBatch nullBatch = null!;
        Assert.ThrowsException<ArgumentNullException>(() =>
            _validator.Validate(_schema, nullBatch));
    }

    // ===========================================================================
    // §8 DI 注册扩展
    // ===========================================================================

    [TestMethod]
    public void ServiceCollection_Registers_IFeatureSchemaValidator_AsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFeatureSchemaValidator, DefaultFeatureSchemaValidator>();

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetService<IFeatureSchemaValidator>();

        Assert.IsNotNull(validator);
        Assert.IsInstanceOfType<DefaultFeatureSchemaValidator>(validator);

        // Singleton 生命周期验证
        var validator2 = provider.GetService<IFeatureSchemaValidator>();
        Assert.AreSame(validator, validator2);
    }

    [TestMethod]
    public void ServiceCollection_DefaultConstructor_CanValidate()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFeatureSchemaValidator, DefaultFeatureSchemaValidator>();
        using var provider = services.BuildServiceProvider();

        var validator = provider.GetRequiredService<IFeatureSchemaValidator>();

        var input = MakeInput("test-schema-v1",
            ("lexical_score", 0.5),
            ("semantic_score", 0.7),
            ("category", "doc"));

        var result = validator.Validate(_schema, input);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void FeatureSchemaValidatorOptions_Default_IsStrict()
    {
        var options = FeatureSchemaValidatorOptions.Default;

        Assert.IsTrue(options.StrictUnknownFeatures);
    }
}
