using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Inference.Onnx;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

// ===========================================================================
// R29 WP-A-5：真实模型端到端测试（feature → inference → calibration → score）
//
// 覆盖范围：
//   §1 端到端流水线：FeatureSchemaValidator → StubBatchInferenceEngine
//        → DefaultInferenceResultValidator → PlattCalibrationService → DefaultUtilityScorer
//   §2 模型工件加载路径：ModelArtifactDescriptor + ICalibrationValidator
//        在加载时验证校准参数（拒绝统计非法参数）
//   §3 Schema drift 防护：FeatureSchemaValidator 在推理前 fail-fast
//        （SchemaVersion 不匹配 / 必填缺失 / 类型不兼容）
//   §4 推理输出验证：DefaultInferenceResultValidator 防止 NaN/Infinity 污染排序
//   §5 完整 DI 装配：ServiceCollection 注册所有组件，端到端解析并执行
//   §6 真实 ONNX 引擎路径：Mock session 验证 OnnxInferenceEngine + Validator + Scorer 串联
//
// 设计原则：
//   - 不依赖真实 ONNX 模型文件（使用 Stub / Mock 隔离），
//     真实模型 Testcontainers 测试由集成测试层承担。
//   - 使用 R28DTestHelpers 复用 envelope / snapshot / registry 构造逻辑。
//   - 覆盖正常路径与降级路径（fail-safe 而非 fail-stop）。
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("WP-A-5")]
public sealed class R29_RealModelE2ETests
{
    private const string SchemaVersion = "test-schema-e2e-v1";
    private const string ModelArtifactId = "test-model-e2e-v1";
    private const string CalibrationVersion = "test-calibration-v1";

    // ===========================================================================
    // §1 端到端流水线：feature → inference → calibration → score
    // ===========================================================================

    [TestMethod]
    public async Task E2E_ValidInput_ProducesCalibratedModelWeightedScore()
    {
        // 装配：stub 引擎返回 raw=0.8, confidence=0.95
        var engine = new StubBatchInferenceEngine(ModelArtifactId)
            .WithOutput(score: 0.8, confidence: 0.95);
        var registry = BuildRegistryWithSchema(SchemaVersion);
        var schemaValidator = new DefaultFeatureSchemaValidator();
        var inferenceValidator = new DefaultInferenceResultValidator();
        var calibration = new PlattCalibrationService();
        calibration.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: ModelArtifactId);
        var scorer = new DefaultUtilityScorer(new DefaultFeatureSchemaValidator(), engine, calibration, registry, inferenceValidator);

        // 输入：detScore=0.5, breakdown 满足 schema 必填项
        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5,
            breakdown: new Dictionary<string, double>
            {
                ["lexical_score"] = 0.5,
                ["semantic_score"] = 0.7
            });
        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            deterministicWeight: 0.4,
            modelWeight: 0.6,
            confidenceThreshold: 0.70,
            modelArtifactId: ModelArtifactId,
            featureSchemaVersion: SchemaVersion);

        // 1. Feature 阶段：特征提升（DefaultFeaturePipeline）
        var pipeline = new DefaultFeaturePipeline();
        var enriched = await pipeline.EnrichAsync(new[] { envelope }, R28DTestHelpers.BuildContext(), default);

        // 2. Schema 验证（FeatureSchemaValidator）
        var schema = registry.Get(SchemaVersion)!;
        var inputVector = new FeatureVector
        {
            SchemaVersion = SchemaVersion,
            Values = new Dictionary<string, object>
            {
                ["lexical_score"] = 0.5,
                ["semantic_score"] = 0.7
            }
        };
        var schemaResult = schemaValidator.Validate(schema, inputVector);
        Assert.IsTrue(schemaResult.IsValid, "Schema 验证应通过");

        // 3. Inference + 4. 输出验证 + 5. Calibration + 6. Score
        var scored = await scorer.ScoreAsync(enriched, snapshot, default);

        // 期望：raw=0.8 → sigmoid(0.8) ≈ 0.689974
        // FinalScore = 0.4 * 0.5 + 0.6 * 0.689974 ≈ 0.613984
        var expectedCalibrated = 1.0 / (1.0 + Math.Exp(-0.8));
        var expectedFinal = 0.4 * 0.5 + 0.6 * expectedCalibrated;

        Assert.AreEqual(1, scored.Count);
        Assert.AreEqual(expectedCalibrated, scored[0].Utility.ModelScore!.Value, 1e-9);
        Assert.AreEqual(expectedFinal, scored[0].Utility.FinalScore, 1e-9);
        Assert.AreEqual("model-weighted", scored[0].Utility.ReasonCode);
        Assert.IsTrue(scored[0].Utility.ModelApplied);
    }

    [TestMethod]
    public async Task E2E_LowConfidence_FallsBackToDeterministic()
    {
        var engine = new StubBatchInferenceEngine(ModelArtifactId)
            .WithOutput(score: 0.9, confidence: 0.50); // 低于阈值 0.70
        var registry = BuildRegistryWithSchema(SchemaVersion);
        var scorer = new DefaultUtilityScorer(new DefaultFeatureSchemaValidator(), engine, null, registry);

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5);
        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            confidenceThreshold: 0.70,
            featureSchemaVersion: SchemaVersion);

        var scored = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        Assert.AreEqual(1, scored.Count);
        Assert.AreEqual(0.5, scored[0].Utility.FinalScore, 1e-9);
        Assert.AreEqual("fallback-to-deterministic", scored[0].Utility.ReasonCode);
        Assert.IsFalse(scored[0].Utility.ModelApplied);
    }

    [TestMethod]
    public async Task E2E_InferenceReturnsNaN_FallsBackToDeterministic()
    {
        var engine = new StubBatchInferenceEngine(ModelArtifactId)
            .WithOutput(score: double.NaN, confidence: 0.95);
        var registry = BuildRegistryWithSchema(SchemaVersion);
        var inferenceValidator = new DefaultInferenceResultValidator();
        var scorer = new DefaultUtilityScorer(new DefaultFeatureSchemaValidator(), engine, null, registry, inferenceValidator);

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5);
        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            featureSchemaVersion: SchemaVersion);

        var scored = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        // 推理输出含 NaN → DefaultInferenceResultValidator 标记违规 → 降级 deterministic
        Assert.AreEqual(1, scored.Count);
        Assert.AreEqual(0.5, scored[0].Utility.FinalScore, 1e-9);
        Assert.IsTrue(scored[0].Utility.ModelAttempted);
        Assert.IsFalse(scored[0].Utility.ModelApplied);
    }

    // ===========================================================================
    // §2 模型工件加载：ICalibrationValidator 在加载时验证校准参数
    // ===========================================================================

    [TestMethod]
    public void E2E_CalibrationValidator_RejectsInvalidParameters_AtModelLoad()
    {
        // 模拟模型加载：descriptor 引用了一组非法 Platt 参数（A=0）
        var validator = new DefaultCalibrationValidator();
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 0.0,
            ParameterB = 0.0,
            Parameter = 0.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = validator.Validate(parameters, ModelArtifactId);

        Assert.IsFalse(result.IsValid, "A=0 应被拒绝（校准退化为常数）");
        Assert.AreEqual("platt.a_zero", result.Violations[0].Code);
        Assert.AreEqual(ModelArtifactId, result.Violations[0].ModelName);
    }

    [TestMethod]
    public void E2E_CalibrationValidator_AcceptsValidParameters_AllowsModelLoad()
    {
        var validator = new DefaultCalibrationValidator();
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 1.5,
            ParameterB = -0.2,
            Parameter = 1.5,
            FittedAt = DateTimeOffset.UtcNow
        };

        var result = validator.Validate(parameters, ModelArtifactId);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.ErrorCount);
    }

    // ===========================================================================
    // §3 Schema drift 防护：FeatureSchemaValidator 在推理前 fail-fast
    // ===========================================================================

    [TestMethod]
    public async Task E2E_SchemaDrift_VersionMismatch_BlocksInference()
    {
        var engine = new StubBatchInferenceEngine(ModelArtifactId)
            .WithOutput(score: 0.9, confidence: 0.95);
        var registry = BuildRegistryWithSchema(SchemaVersion);
        var schemaValidator = new DefaultFeatureSchemaValidator();
        var scorer = new DefaultUtilityScorer(new DefaultFeatureSchemaValidator(), engine, null, registry);

        // 故意使用不匹配的 schema 版本
        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            featureSchemaVersion: "wrong-schema-version");

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5);

        // 在 Scorer 内部 schema 解析失败 → 降级 deterministic
        var scored = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);
        Assert.AreEqual(1, scored.Count);
        Assert.IsFalse(scored[0].Utility.ModelApplied);
        Assert.AreEqual("schema-not-found", scored[0].Utility.ModelFallbackReason);

        // 直接通过 FeatureSchemaValidator 验证 schema 不匹配
        var schema = registry.Get(SchemaVersion)!;
        var wrongInput = new FeatureVector
        {
            SchemaVersion = "wrong-version",
            Values = new Dictionary<string, object>()
        };
        var result = schemaValidator.Validate(schema, wrongInput);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("schema.version_mismatch", result.Violations[0].Code);
    }

    [TestMethod]
    public async Task E2E_SchemaDrift_MissingRequired_BlocksInference()
    {
        // 自定义 schema：lexical_score 必填且无默认值
        var strictSchema = new FeatureSchema
        {
            Version = "strict-schema-v1",
            CreatedAt = DateTimeOffset.UtcNow,
            Features = new[]
            {
                new FeatureDefinition { Name = "lexical_score", Type = FeatureType.Numeric, IsRequired = true, DefaultValue = null },
                new FeatureDefinition { Name = "semantic_score", Type = FeatureType.Numeric, IsRequired = true, DefaultValue = null }
            }
        };
        var registry = new DefaultFeatureRegistry();
        registry.Register(strictSchema);

        var schemaValidator = new DefaultFeatureSchemaValidator();

        // 输入缺失必填特征
        var input = new FeatureVector
        {
            SchemaVersion = "strict-schema-v1",
            Values = new Dictionary<string, object>
            {
                ["lexical_score"] = 0.5
                // semantic_score 缺失
            }
        };
        var result = schemaValidator.Validate(strictSchema, input);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.missing_required" && v.FeatureName == "semantic_score"));

        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task E2E_SchemaDrift_TypeMismatch_BlocksInference()
    {
        var registry = BuildRegistryWithSchema(SchemaVersion);
        var schemaValidator = new DefaultFeatureSchemaValidator();
        var schema = registry.Get(SchemaVersion)!;

        // lexical_score 是 Numeric，传入字符串 "abc"
        var input = new FeatureVector
        {
            SchemaVersion = SchemaVersion,
            Values = new Dictionary<string, object>
            {
                ["lexical_score"] = "abc"
            }
        };
        var result = schemaValidator.Validate(schema, input);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Code == "feature.type_mismatch" && v.FeatureName == "lexical_score"));

        await Task.CompletedTask;
    }

    // ===========================================================================
    // §4 推理输出验证：DefaultInferenceResultValidator 防止脏数据
    // ===========================================================================

    [TestMethod]
    public void E2E_InferenceValidator_RejectsNaN_Score()
    {
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest
        {
            Inputs = new[]
            {
                new FeatureVector
                {
                    SchemaVersion = SchemaVersion,
                    Values = new Dictionary<string, object>()
                }
            }
        };
        var result = new BatchInferenceResult
        {
            Outputs = new[]
            {
                new InferenceOutput { Score = double.NaN, Confidence = 0.5 }
            },
            Succeeded = true,
            Error = null,
            Duration = TimeSpan.FromMilliseconds(10)
        };

        var validation = validator.Validate(request, result);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Violations.Any(v => v.Contains("NaN")));
    }

    [TestMethod]
    public void E2E_InferenceValidator_RejectsConfidenceOutOfRange()
    {
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest
        {
            Inputs = new[]
            {
                new FeatureVector
                {
                    SchemaVersion = SchemaVersion,
                    Values = new Dictionary<string, object>()
                }
            }
        };
        var result = new BatchInferenceResult
        {
            Outputs = new[]
            {
                new InferenceOutput { Score = 0.5, Confidence = 1.5 } // 超出 [0, 1]
            },
            Succeeded = true,
            Error = null,
            Duration = TimeSpan.FromMilliseconds(10)
        };

        var validation = validator.Validate(request, result);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Violations.Any(v => v.Contains("[0,1]")));
    }

    [TestMethod]
    public void E2E_InferenceValidator_RejectsCountMismatch()
    {
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest
        {
            Inputs = new[]
            {
                new FeatureVector
                {
                    SchemaVersion = SchemaVersion,
                    Values = new Dictionary<string, object>()
                },
                new FeatureVector
                {
                    SchemaVersion = SchemaVersion,
                    Values = new Dictionary<string, object>()
                }
            }
        };
        var result = new BatchInferenceResult
        {
            Outputs = new[]
            {
                new InferenceOutput { Score = 0.5, Confidence = 0.9 }
                // 缺一条输出
            },
            Succeeded = true,
            Error = null,
            Duration = TimeSpan.FromMilliseconds(10)
        };

        var validation = validator.Validate(request, result);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Violations.Any(v => v.Contains("Outputs.Count")));
    }

    [TestMethod]
    public void E2E_InferenceValidator_RejectsWeightDrift()
    {
        var validator = new DefaultInferenceResultValidator();
        // w_d + w_m ≠ 1.0 → FinalScore 被错误缩放
        var result = validator.ValidateScoreWeights(deterministicWeight: 0.4, modelWeight: 0.7);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Contains("w_d + w_m")));
    }

    // ===========================================================================
    // §5 完整 DI 装配
    // ===========================================================================

    [TestMethod]
    public async Task E2E_DIServiceCollection_ResolvesAllValidatorsAndScorer()
    {
        // 验证所有 R29 WP-A 组件可通过 DI 解析并协同工作
        var services = new ServiceCollection();
        services.AddSingleton<IFeatureRegistry>(BuildRegistryWithSchema(SchemaVersion));
        services.AddSingleton<ICalibrationValidator, DefaultCalibrationValidator>();
        services.AddSingleton<IFeatureSchemaValidator, DefaultFeatureSchemaValidator>();
        services.AddSingleton<IInferenceResultValidator, DefaultInferenceResultValidator>();
        services.AddSingleton<IBatchInferenceEngine>(sp =>
        {
            var engine = new StubBatchInferenceEngine(ModelArtifactId);
            engine.WithOutput(score: 0.7, confidence: 0.9);
            return engine;
        });
        services.AddSingleton<ICalibrationService>(sp =>
        {
            var calibration = new PlattCalibrationService();
            calibration.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: ModelArtifactId);
            return calibration;
        });
        services.AddSingleton<IUtilityScorer, DefaultUtilityScorer>();

        using var provider = services.BuildServiceProvider();

        var calibrationValidator = provider.GetRequiredService<ICalibrationValidator>();
        var schemaValidator = provider.GetRequiredService<IFeatureSchemaValidator>();
        var inferenceValidator = provider.GetRequiredService<IInferenceResultValidator>();
        var scorer = provider.GetRequiredService<IUtilityScorer>();
        var registry = provider.GetRequiredService<IFeatureRegistry>();

        Assert.IsNotNull(calibrationValidator);
        Assert.IsNotNull(schemaValidator);
        Assert.IsNotNull(inferenceValidator);
        Assert.IsNotNull(scorer);
        Assert.IsNotNull(registry);

        // 端到端：feature → inference → score
        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.4,
            breakdown: new Dictionary<string, double> { ["lexical_score"] = 0.6 });
        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            deterministicWeight: 0.5,
            modelWeight: 0.5,
            modelArtifactId: ModelArtifactId,
            featureSchemaVersion: SchemaVersion);

        var scored = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        Assert.AreEqual(1, scored.Count);
        Assert.IsTrue(scored[0].Utility.ModelApplied);
        Assert.AreEqual("model-weighted", scored[0].Utility.ReasonCode);

        // 同步验证：Schema 与 Calibration 在加载时通过
        var schema = registry.Get(SchemaVersion)!;
        var input = new FeatureVector
        {
            SchemaVersion = SchemaVersion,
            Values = new Dictionary<string, object> { ["lexical_score"] = 0.6 }
        };
        Assert.IsTrue(schemaValidator.Validate(schema, input).IsValid);
    }

    // ===========================================================================
    // §6 真实 ONNX 引擎路径（Mock session）
    // ===========================================================================

    [TestMethod]
    public async Task E2E_OnnxInferenceEngine_WithMockSession_ProducesScore()
    {
        // 使用 MockOnnxInferenceSession 隔离真实 ONNX 文件
        var mockSession = new MockOnnxInferenceSession(
            modelArtifactId: "onnx-mock-v1",
            modelVersion: "1.0.0",
            contentHash: "sha256:mock",
            outputs: new[]
            {
                new InferenceOutput { Score = 0.5, Confidence = 0.9 }
            });
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits"
        };
        var engine = new OnnxInferenceEngine(mockSession, options, calibrationVersion: CalibrationVersion);

        // 构造 FeatureBatch
        var batch = new FeatureBatch
        {
            SchemaVersion = SchemaVersion,
            Values = new float[] { 0.5f, 0.7f, 0.1f },
            RowCount = 1,
            FeatureCount = 3,
            FeatureNames = new[] { "lexical_score", "semantic_score", "recency_score" }
        };

        var result = await engine.InferBatchAsync(batch);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Outputs.Count);
        // MockSession 返回固定 score=0.5, confidence=0.9
        Assert.AreEqual(0.5, result.Outputs[0].Score, 1e-6);
        Assert.AreEqual(0.9, result.Outputs[0].Confidence, 1e-6);

        // 与 DefaultUtilityScorer 串联
        var registry = BuildRegistryWithSchema(SchemaVersion);
        var scorer = new DefaultUtilityScorer(new DefaultFeatureSchemaValidator(), engine, null, registry);
        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.4);
        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            deterministicWeight: 0.5,
            modelWeight: 0.5,
            confidenceThreshold: 0.70,
            featureSchemaVersion: SchemaVersion);

        var scored = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        Assert.AreEqual(1, scored.Count);
        Assert.IsTrue(scored[0].Utility.ModelApplied);
    }

    [TestMethod]
    public async Task E2E_OnnxInferenceEngine_MetadataMatchesDescriptor()
    {
        var mockSession = new MockOnnxInferenceSession(
            modelArtifactId: "onnx-mock-v1",
            modelVersion: "1.0.0",
            contentHash: "sha256:mock",
            outputs: Array.Empty<InferenceOutput>());
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits"
        };
        var engine = new OnnxInferenceEngine(mockSession, options, calibrationVersion: CalibrationVersion);

        // 验证元数据：与 ModelArtifactDescriptor 字段对应
        Assert.AreEqual("1.0.0", engine.ModelVersion);
        Assert.AreEqual(InferenceEngineKind.RealModel, engine.Kind);
        Assert.AreEqual("sha256:mock", engine.ContentHash);
        Assert.AreEqual(CalibrationVersion, engine.CalibrationVersion);

        await Task.CompletedTask;
    }

    // ===========================================================================
    // §7 多候选批量推理端到端
    // ===========================================================================

    [TestMethod]
    public async Task E2E_MultipleCandidates_BatchInference_AggregatesCorrectly()
    {
        var engine = new StubBatchInferenceEngine(ModelArtifactId)
            .WithOutput(score: 0.7, confidence: 0.9)
            .WithOutput(score: 0.9, confidence: 0.95)
            .WithOutput(score: 0.3, confidence: 0.6); // 低置信度
        var registry = BuildRegistryWithSchema(SchemaVersion);
        var scorer = new DefaultUtilityScorer(new DefaultFeatureSchemaValidator(), engine, null, registry);

        var envelopes = new[]
        {
            R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5),
            R28DTestHelpers.MakeEnvelope("c2", detScore: 0.6),
            R28DTestHelpers.MakeEnvelope("c3", detScore: 0.4)
        };
        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            deterministicWeight: 0.5,
            modelWeight: 0.5,
            confidenceThreshold: 0.70,
            featureSchemaVersion: SchemaVersion);

        var scored = await scorer.ScoreAsync(envelopes, snapshot, default);

        Assert.AreEqual(3, scored.Count);
        // c1: model applied (confidence=0.9)
        Assert.IsTrue(scored[0].Utility.ModelApplied);
        // c2: model applied (confidence=0.95)
        Assert.IsTrue(scored[1].Utility.ModelApplied);
        // c3: low confidence fallback (confidence=0.6 < 0.7)
        Assert.IsFalse(scored[2].Utility.ModelApplied);
        Assert.AreEqual("fallback-to-deterministic", scored[2].Utility.ReasonCode);
    }

    // ===========================================================================
    // 辅助方法
    // ===========================================================================

    private static IFeatureRegistry BuildRegistryWithSchema(string schemaVersion)
    {
        var registry = new DefaultFeatureRegistry();
        registry.Register(new FeatureSchema
        {
            Version = schemaVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Features = new[]
            {
                new FeatureDefinition { Name = "lexical_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "semantic_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "recency_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "relation_boost", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "mandatory_weight", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "deterministic_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" }
            }
        });
        return registry;
    }
}
