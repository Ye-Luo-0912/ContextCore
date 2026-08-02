using ContextCore.Abstractions;
using ContextCore.Core.Services.ModelExecution;

namespace ContextCore.Tests;

// ===========================================================================
// Model Execution Runtime 验收测试
//
// 覆盖范围（6 个验收用例）：
//   1. FeatureRegistry_RegisterAndGet_ReturnsSchema          —— 注册后按版本号取回
//   2. FeatureRegistry_GetLatest_ReturnsMostRecent           —— GetLatest 返回最新 CreatedAt
//   3. BatchInference_ProducesDeterministicScore             —— 相同输入产出相同分数
//   4. BatchInference_FallbackSucceeds                       —— 真实模型不可用时 fallback 成功
//   5. Calibration_PlattScaling_ProducesValidProbability     —— 校准后分数在 [0,1]
//   6. Calibration_BatchCalibrate_AllInValidRange            —— 批量校准全部在 [0,1]
//
// 设计原则：
//   - 每个 [TestClass] 自包含，无共享 fixture（与现有 R28-B 测试模式一致）
//   - 共享 helper 放在 file-level internal static class
//   - 验收点对应任务描述中给出的 6 个验收用例
// ===========================================================================

[TestClass]
[TestCategory("R28-D")]
public sealed class R28B_ModelExecutionTests
{
    // =========================================================================
    // Feature Registry
    // =========================================================================

    [TestMethod]
    public void FeatureRegistry_RegisterAndGet_ReturnsSchema()
    {
        var registry = new DefaultFeatureRegistry();
        var schema = ModelExecutionTestHelpers.BuildSchema("1.0.0", new[]
        {
            ("age", FeatureType.Numeric, true, (string?)"0"),
            ("gender", FeatureType.Categorical, false, null)
        });

        registry.Register(schema);

        var fetched = registry.Get("1.0.0");
        Assert.IsNotNull(fetched);
        Assert.AreEqual("1.0.0", fetched!.Version);
        Assert.AreEqual(2, fetched.Features.Count);
        Assert.AreEqual("age", fetched.Features[0].Name);
        Assert.AreEqual(FeatureType.Numeric, fetched.Features[0].Type);
        Assert.IsTrue(fetched.Features[0].IsRequired);
        Assert.AreEqual("0", fetched.Features[0].DefaultValue);
    }

    [TestMethod]
    public void FeatureRegistry_GetLatest_ReturnsMostRecent()
    {
        var registry = new DefaultFeatureRegistry();
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var v1 = ModelExecutionTestHelpers.BuildSchema("1.0.0", Array.Empty<(string, FeatureType, bool, string?)>(), baseTime);
        var v2 = ModelExecutionTestHelpers.BuildSchema("2.0.0", Array.Empty<(string, FeatureType, bool, string?)>(), baseTime.AddDays(1));
        var v3 = ModelExecutionTestHelpers.BuildSchema("3.0.0", Array.Empty<(string, FeatureType, bool, string?)>(), baseTime.AddDays(2));

        registry.Register(v1);
        registry.Register(v2);
        registry.Register(v3);

        var latest = registry.GetLatest();
        Assert.IsNotNull(latest);
        Assert.AreEqual("3.0.0", latest!.Version);

        // ListAll 应返回 3 个 schema，按 CreatedAt 升序排列。
        var all = registry.ListAll();
        Assert.AreEqual(3, all.Count);
        Assert.AreEqual("1.0.0", all[0].Version);
        Assert.AreEqual("2.0.0", all[1].Version);
        Assert.AreEqual("3.0.0", all[2].Version);
    }

    [TestMethod]
    public void FeatureRegistry_RegisterDuplicateVersion_Throws()
    {
        var registry = new DefaultFeatureRegistry();
        var schema = ModelExecutionTestHelpers.BuildSchema("1.0.0");

        registry.Register(schema);
        Assert.ThrowsException<InvalidOperationException>(() => registry.Register(schema));
    }

    [TestMethod]
    public void FeatureRegistry_GetUnknownVersion_ReturnsNull()
    {
        var registry = new DefaultFeatureRegistry();
        Assert.IsNull(registry.Get("nonexistent"));
        Assert.IsNull(registry.GetLatest());
        Assert.AreEqual(0, registry.ListAll().Count);
    }

    // =========================================================================
    // Batch Inference Engine
    // =========================================================================

    [TestMethod]
    public async Task BatchInference_ProducesDeterministicScore()
    {
        var engine = new DeterministicBatchInferenceEngine();
        var vector = ModelExecutionTestHelpers.BuildVector("1.0.0", new Dictionary<string, object>
        {
            ["age"] = 30,
            ["gender"] = "male",
            ["active"] = true
        });

        var request = new BatchInferenceRequest
        {
            Inputs = new[] { vector, vector }
        };

        var result1 = await engine.InferAsync(request);
        var result2 = await engine.InferAsync(request);

        Assert.IsTrue(result1.Succeeded);
        Assert.AreEqual(2, result1.Outputs.Count);
        // 相同输入必须产出相同分数（确定性）。
        Assert.AreEqual(result1.Outputs[0].Score, result1.Outputs[1].Score, 1e-12);
        Assert.AreEqual(result1.Outputs[0].Score, result2.Outputs[0].Score, 1e-12);
        Assert.AreEqual(result1.Outputs[0].Confidence, result2.Outputs[0].Confidence, 1e-12);
        // Score 在 [-1, 1]，Confidence 在 [0, 1]。
        Assert.IsTrue(result1.Outputs[0].Score >= -1.0 && result1.Outputs[0].Score <= 1.0);
        Assert.IsTrue(result1.Outputs[0].Confidence >= 0.0 && result1.Outputs[0].Confidence <= 1.0);
        // Duration 非负。
        Assert.IsTrue(result1.Duration >= TimeSpan.Zero);
    }

    [TestMethod]
    public async Task BatchInference_FallbackSucceeds()
    {
        // 模拟"真实模型不可用 → fallback 到 Deterministic"的场景。
        // Deterministic 引擎作为 fallback 实现：即使无任何外部依赖，
        // 也能对非空输入返回 Succeeded=true 的结果。
        var engine = new DeterministicBatchInferenceEngine();

        Assert.AreEqual("deterministic-hash-v1", engine.ModelVersion);

        var request = new BatchInferenceRequest
        {
            Inputs = new[]
            {
                ModelExecutionTestHelpers.BuildVector("1.0.0", new Dictionary<string, object> { ["x"] = 1 }),
                ModelExecutionTestHelpers.BuildVector("1.0.0", new Dictionary<string, object> { ["x"] = 2 }),
                ModelExecutionTestHelpers.BuildVector("1.0.0", new Dictionary<string, object> { ["x"] = 3 })
            },
            ModelName = "unavailable-remote-model",
            TimeoutMs = 1000
        };

        var result = await engine.InferAsync(request);

        Assert.IsTrue(result.Succeeded, $"fallback 应当成功，Error={result.Error}");
        Assert.IsNull(result.Error);
        Assert.AreEqual(3, result.Outputs.Count);
        // 不同输入产出不同分数（验证 hash 真的反映了输入差异）。
        Assert.AreNotEqual(result.Outputs[0].Score, result.Outputs[1].Score, 1e-12);
        Assert.AreNotEqual(result.Outputs[1].Score, result.Outputs[2].Score, 1e-12);
    }

    [TestMethod]
    public async Task BatchInference_DifferentKeyOrder_ProducesSameScore()
    {
        // 字典遍历顺序不应影响确定性 hash —— 必须按 key 排序后哈希。
        var engine = new DeterministicBatchInferenceEngine();
        var v1 = ModelExecutionTestHelpers.BuildVector("1.0.0", new Dictionary<string, object>
        {
            ["a"] = 1,
            ["b"] = 2,
            ["c"] = 3
        });
        var v2 = ModelExecutionTestHelpers.BuildVector("1.0.0", new Dictionary<string, object>
        {
            ["c"] = 3,
            ["a"] = 1,
            ["b"] = 2
        });

        var r1 = await engine.InferAsync(new BatchInferenceRequest { Inputs = new[] { v1 } });
        var r2 = await engine.InferAsync(new BatchInferenceRequest { Inputs = new[] { v2 } });

        Assert.AreEqual(r1.Outputs[0].Score, r2.Outputs[0].Score, 1e-12);
        Assert.AreEqual(r1.Outputs[0].Confidence, r2.Outputs[0].Confidence, 1e-12);
    }

    [TestMethod]
    public async Task BatchInference_EmptyInput_ReturnsEmptySuccess()
    {
        var engine = new DeterministicBatchInferenceEngine();
        var result = await engine.InferAsync(new BatchInferenceRequest { Inputs = Array.Empty<FeatureVector>() });

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, result.Outputs.Count);
    }

    // =========================================================================
    // Calibration Service
    // =========================================================================

    [TestMethod]
    public void Calibration_PlattScaling_ProducesValidProbability()
    {
        var service = new PlattCalibrationService();

        // 默认 Identity（raw 原样返回，不调用 sigmoid）。
        // 要复用旧 sigmoid 语义，需显式注册 Platt(A=1, B=0)。
        service.RegisterPlattParameters(a: 1.0, b: 0.0);
        // 默认参数现在应反映 Platt 注册
        var defaultParams = service.GetParameters();
        Assert.IsNotNull(defaultParams);
        Assert.AreEqual("platt", defaultParams!.Method);
        Assert.AreEqual(CalibrationMethodKind.Platt, defaultParams.Kind);
        Assert.AreEqual(1.0, defaultParams.ParameterA);
        Assert.AreEqual(0.0, defaultParams.ParameterB);

        // sigmoid(raw) 必落在 [0, 1]。
        var rawScores = new[] { -100.0, -10.0, -1.0, 0.0, 1.0, 10.0, 100.0, double.NaN };
        foreach (var raw in rawScores)
        {
            var calibrated = service.Calibrate(raw);
            if (double.IsNaN(raw))
            {
                Assert.IsTrue(double.IsNaN(calibrated), "NaN 输入应返回 NaN");
            }
            else
            {
                Assert.IsTrue(calibrated >= 0.0 && calibrated <= 1.0,
                    $"calibrated={calibrated} 越界 [0,1] (raw={raw})");
            }
        }

        // 校准参数应可查询。
        var parameters = service.GetParameters();
        Assert.IsNotNull(parameters);
        Assert.AreEqual("platt", parameters!.Method);
        Assert.AreEqual(1.0, parameters.Parameter);
        Assert.AreEqual(1.0, parameters.ParameterA);
        Assert.AreEqual(0.0, parameters.ParameterB);
    }

    [TestMethod]
    public void Calibration_BatchCalibrate_AllInValidRange()
    {
        var service = new PlattCalibrationService();
        // 注册自定义参数（A=2, B=-0.5）。
        service.RegisterParameters(a: 2.0, b: -0.5, modelName: "custom");

        var rawScores = new List<double> { -50, -5, -0.5, 0, 0.5, 5, 50 };
        var calibrated = service.CalibrateBatch(rawScores, "custom");

        Assert.AreEqual(rawScores.Count, calibrated.Count);
        foreach (var c in calibrated)
        {
            Assert.IsTrue(c >= 0.0 && c <= 1.0, $"批量校准结果 {c} 越界 [0,1]");
        }

        // 自定义参数应可查询。
        var parameters = service.GetParameters("custom");
        Assert.IsNotNull(parameters);
        Assert.AreEqual("platt", parameters!.Method);
        Assert.AreEqual(2.0, parameters.Parameter);
    }

    [TestMethod]
    public void Calibration_BatchCalibrate_EmptyInput_ReturnsEmpty()
    {
        var service = new PlattCalibrationService();
        var result = service.CalibrateBatch(Array.Empty<double>());
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Calibration_UnknownModel_FallsBackToDefault()
    {
        var service = new PlattCalibrationService();
        // 默认参数现在是 Identity（raw 原样返回），不再执行 sigmoid。
        // 未注册的模型名应回退到默认参数。
        var calibrated = service.Calibrate(0.0, "never-registered");
        Assert.AreEqual(0.0, calibrated, 1e-12);
        Assert.IsNull(service.GetParameters("never-registered"));

        // 验证 Identity 默认行为：raw=0.8 → calibrated=0.8（无变换）
        var calibratedNonZero = service.Calibrate(0.8, "never-registered");
        Assert.AreEqual(0.8, calibratedNonZero, 1e-12);
    }

    [TestMethod]
    public void Calibration_DefaultIdentity_Identity_NoTransformApplied()
    {
        // 新增测试 — 默认 Identity 不调用 Math.Exp，原样返回 raw score。
        var service = new PlattCalibrationService();
        var parameters = service.GetParameters();
        Assert.IsNotNull(parameters);
        Assert.AreEqual(CalibrationMethodKind.Identity, parameters!.Kind);
        Assert.AreEqual("identity", parameters.Method);

        // 各种 raw score 均应原样返回
        foreach (var raw in new[] { -100.0, -1.0, 0.0, 0.5, 1.0, 100.0 })
        {
            var calibrated = service.Calibrate(raw);
            Assert.AreEqual(raw, calibrated, 1e-12,
                $"Identity 校准应原样返回 raw={raw}，实际 {calibrated}");
        }

        // raw=0 → calibrated=0（关键差异：旧版默认会返回 0.5）
        Assert.AreEqual(0.0, service.Calibrate(0.0), 1e-12,
            "Identity 校准对 raw=0 应返回 0，而非旧版 sigmoid(0)=0.5");
    }
}

/// <summary>
/// 测试辅助：构建 FeatureSchema / FeatureVector。
/// </summary>
internal static class ModelExecutionTestHelpers
{
    public static FeatureSchema BuildSchema(
        string version,
        IReadOnlyList<(string Name, FeatureType Type, bool IsRequired, string? DefaultValue)>? features = null,
        DateTimeOffset? createdAt = null)
    {
        var list = new List<FeatureDefinition>();
        if (features is not null)
        {
            foreach (var f in features)
            {
                list.Add(new FeatureDefinition
                {
                    Name = f.Name,
                    Type = f.Type,
                    IsRequired = f.IsRequired,
                    DefaultValue = f.DefaultValue
                });
            }
        }
        return new FeatureSchema
        {
            Version = version,
            Features = list,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };
    }

    public static FeatureVector BuildVector(string schemaVersion, IReadOnlyDictionary<string, object> values)
    {
        return new FeatureVector
        {
            SchemaVersion = schemaVersion,
            Values = values
        };
    }
}
