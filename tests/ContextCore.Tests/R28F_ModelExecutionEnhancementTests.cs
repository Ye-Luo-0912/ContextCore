using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Core.Services.ModelExecution;

namespace ContextCore.Tests;

// ===========================================================================
// 模型执行链增强验收测试
//
// 覆盖范围：
// FeatureBatch —— 连续 float 内存批量推理契约
// DeterministicBatchInferenceEngine.InferBatchAsync —— 优化 hash 路径
// CalibrationStrategies —— Identity / Platt / Temperature / Isotonic
// PlattCalibrationService —— 扩展注册方法（Temperature / Isotonic / Identity）
// ModelExecutionSnapshot —— 精确模型执行快照
// DefaultInferenceResultValidator —— 推理输出严格验证
//
// 验收点（对应 任务描述）：
// FeatureSchemaVersion 与 ModelVersion 解耦 → ModelExecutionSnapshot
// 推理输出严格验证（NaN / Infinity / Confidence 范围 / Count / timeout）
// Calibration 默认 Identity，显式 Platt / Temperature / Isotonic 策略
// FeatureBatch 连续内存推理
// Deterministic Hash 消除 StringBuilder / string[] / 排序分配
// ===========================================================================

// ===========================================================================
// FeatureBatch 连续内存契约
// ===========================================================================

[TestClass]
[TestCategory("R28-F")]
public sealed class R28F_FeatureBatchTests
{
    [TestMethod]
    public void FeatureBatch_Construct_WithValidDimensions_Succeeds()
    {
        var batch = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = new float[] { 1f, 2f, 3f, 4f, 5f, 6f },
            RowCount = 2,
            FeatureCount = 3,
            FeatureNames = new[] { "a", "b", "c" }
        };

        Assert.AreEqual("1.0.0", batch.SchemaVersion);
        Assert.AreEqual(6, batch.Values.Length);
        Assert.AreEqual(2, batch.RowCount);
        Assert.AreEqual(3, batch.FeatureCount);
        Assert.AreEqual(3, batch.FeatureNames.Count);
    }

    [TestMethod]
    public void FeatureBatch_SingleRow_Works()
    {
        var batch = new FeatureBatch
        {
            SchemaVersion = "v1",
            Values = new float[] { 0.5f, 0.3f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "x", "y" }
        };

        Assert.AreEqual(1, batch.RowCount);
        Assert.AreEqual(2, batch.Values.Length);
    }

    [TestMethod]
    public void FeatureBatch_EmptyBatch_HasZeroRowCount()
    {
        var batch = new FeatureBatch
        {
            SchemaVersion = "v1",
            Values = Array.Empty<float>(),
            RowCount = 0,
            FeatureCount = 3,
            FeatureNames = new[] { "a", "b", "c" }
        };

        Assert.AreEqual(0, batch.RowCount);
        Assert.AreEqual(0, batch.Values.Length);
    }
}

// ===========================================================================
// DeterministicBatchInferenceEngine.InferBatchAsync（优化 hash 路径）
// ===========================================================================

[TestClass]
[TestCategory("R28-F")]
public sealed class R28F_DeterministicBatchInferenceEngineTests
{
    [TestMethod]
    public async Task InferBatchAsync_SameInput_ProducesSameScore()
    {
        var engine = new DeterministicBatchInferenceEngine();
        var batch = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = new float[] { 0.1f, 0.2f, 0.3f, 0.4f },
            RowCount = 2,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };

        var r1 = await engine.InferBatchAsync(batch);
        var r2 = await engine.InferBatchAsync(batch);

        Assert.IsTrue(r1.Succeeded);
        Assert.AreEqual(2, r1.Outputs.Count);
        Assert.AreEqual(r1.Outputs[0].Score, r2.Outputs[0].Score, 1e-12);
        Assert.AreEqual(r1.Outputs[1].Score, r2.Outputs[1].Score, 1e-12);
    }

    [TestMethod]
    public async Task InferBatchAsync_DifferentValues_ProducesDifferentScores()
    {
        var engine = new DeterministicBatchInferenceEngine();
        var batch1 = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = new float[] { 0.1f, 0.2f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };
        var batch2 = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = new float[] { 0.9f, 0.8f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };

        var r1 = await engine.InferBatchAsync(batch1);
        var r2 = await engine.InferBatchAsync(batch2);

        Assert.AreNotEqual(r1.Outputs[0].Score, r2.Outputs[0].Score, 1e-12);
    }

    [TestMethod]
    public async Task InferBatchAsync_DifferentSchemaVersion_ProducesDifferentScores()
    {
        // schema version 参与 hash，不同版本应产出不同分数。
        var engine = new DeterministicBatchInferenceEngine();
        var values = new float[] { 0.5f, 0.5f };

        var r1 = await engine.InferBatchAsync(new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = values,
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        });
        var r2 = await engine.InferBatchAsync(new FeatureBatch
        {
            SchemaVersion = "2.0.0",
            Values = values,
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        });

        Assert.AreNotEqual(r1.Outputs[0].Score, r2.Outputs[0].Score, 1e-12);
    }

    [TestMethod]
    public async Task InferBatchAsync_EmptyBatch_ReturnsSuccessWithZeroOutputs()
    {
        var engine = new DeterministicBatchInferenceEngine();
        var batch = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = Array.Empty<float>(),
            RowCount = 0,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };

        var result = await engine.InferBatchAsync(batch);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, result.Outputs.Count);
    }

    [TestMethod]
    public async Task InferBatchAsync_ValuesLengthMismatch_ReturnsFailure()
    {
        var engine = new DeterministicBatchInferenceEngine();
        var batch = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = new float[] { 0.1f, 0.2f, 0.3f }, // 3 个值，但 RowCount=2 * FeatureCount=2 = 4
            RowCount = 2,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };

        var result = await engine.InferBatchAsync(batch);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Error);
        Assert.IsTrue(result.Error!.Contains("Values.Length"));
    }

    [TestMethod]
    public async Task InferBatchAsync_CancellationRequested_ReturnsFailure()
    {
        var engine = new DeterministicBatchInferenceEngine();
        var batch = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = new float[] { 0.1f, 0.2f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await engine.InferBatchAsync(batch, cts.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Error);
    }

    [TestMethod]
    public async Task InferBatchAsync_ScoreInRange()
    {
        var engine = new DeterministicBatchInferenceEngine();
        var batch = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f },
            RowCount = 3,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };

        var result = await engine.InferBatchAsync(batch);

        Assert.IsTrue(result.Succeeded);
        foreach (var output in result.Outputs)
        {
            Assert.IsTrue(output.Score >= -1.0 && output.Score <= 1.0, $"Score={output.Score} 越界 [-1,1]");
            Assert.IsTrue(output.Confidence >= 0.0 && output.Confidence <= 1.0, $"Confidence={output.Confidence} 越界 [0,1]");
        }
    }

    [TestMethod]
    public async Task InferBatchAsync_DurationIsPositive()
    {
        // 推理验证要求 Duration > 0（当 TimeoutMs > 0）。
        // DeterministicBatchInferenceEngine 使用 Stopwatch，Duration 应 > 0。
        var engine = new DeterministicBatchInferenceEngine();
        var batch = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = new float[] { 0.1f, 0.2f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };

        var result = await engine.InferBatchAsync(batch);

        Assert.IsTrue(result.Duration > TimeSpan.Zero, $"Duration={result.Duration} 应 > 0");
    }

    [TestMethod]
    public async Task InferBatchAsync_NullBatch_Throws()
    {
        var engine = new DeterministicBatchInferenceEngine();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            engine.InferBatchAsync(null!).AsTask());
    }
}

// ===========================================================================
// CalibrationStrategies 策略族
// ===========================================================================

[TestClass]
[TestCategory("R28-F")]
public sealed class R28F_CalibrationStrategiesTests
{
    // -------------------------------------------------------------------------
    // IdentityCalibration
    // -------------------------------------------------------------------------

    [TestMethod]
    public void IdentityCalibration_RawPassthrough()
    {
        var strategy = new IdentityCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "identity",
            Kind = CalibrationMethodKind.Identity,
            FittedAt = DateTimeOffset.UtcNow
        };

        foreach (var raw in new[] { -100.0, -1.0, 0.0, 0.5, 1.0, 100.0 })
        {
            Assert.AreEqual(raw, strategy.Calibrate(raw, parameters), 1e-12);
        }
    }

    [TestMethod]
    public void IdentityCalibration_Kind_IsIdentity()
    {
        Assert.AreEqual(CalibrationMethodKind.Identity, new IdentityCalibration().Kind);
    }

    // -------------------------------------------------------------------------
    // PlattCalibration
    // -------------------------------------------------------------------------

    [TestMethod]
    public void PlattCalibration_A1B0_EqualsSigmoid()
    {
        var strategy = new PlattCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 1.0,
            ParameterB = 0.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        foreach (var raw in new[] { -10.0, -1.0, 0.0, 1.0, 10.0 })
        {
            var expected = 1.0 / (1.0 + Math.Exp(-raw));
            Assert.AreEqual(expected, strategy.Calibrate(raw, parameters), 1e-12);
        }
    }

    [TestMethod]
    public void PlattCalibration_CustomAB_EqualsSigmoidOfAXPlusB()
    {
        var strategy = new PlattCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 2.0,
            ParameterB = -0.5,
            FittedAt = DateTimeOffset.UtcNow
        };

        var raw = 0.3;
        var expected = 1.0 / (1.0 + Math.Exp(-(2.0 * raw + (-0.5))));
        Assert.AreEqual(expected, strategy.Calibrate(raw, parameters), 1e-12);
    }

    [TestMethod]
    public void PlattCalibration_SaturatesAtExtremeInput()
    {
        var strategy = new PlattCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 1.0,
            ParameterB = 0.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        Assert.AreEqual(1.0, strategy.Calibrate(100.0, parameters), 1e-12);
        Assert.AreEqual(0.0, strategy.Calibrate(-100.0, parameters), 1e-12);
    }

    [TestMethod]
    public void PlattCalibration_NaNInput_ReturnsNaN()
    {
        var strategy = new PlattCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = 1.0,
            ParameterB = 0.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        Assert.IsTrue(double.IsNaN(strategy.Calibrate(double.NaN, parameters)));
    }

    [TestMethod]
    public void PlattCalibration_KindMismatch_Throws()
    {
        var strategy = new PlattCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "identity",
            Kind = CalibrationMethodKind.Identity,
            FittedAt = DateTimeOffset.UtcNow
        };

        Assert.ThrowsException<ArgumentException>(() =>
            strategy.Calibrate(0.5, parameters));
    }

    // -------------------------------------------------------------------------
    // TemperatureCalibration
    // -------------------------------------------------------------------------

    [TestMethod]
    public void TemperatureCalibration_T1_EqualsSigmoid()
    {
        var strategy = new TemperatureCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "temperature",
            Kind = CalibrationMethodKind.Temperature,
            Temperature = 1.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        foreach (var raw in new[] { -5.0, 0.0, 5.0 })
        {
            var expected = 1.0 / (1.0 + Math.Exp(-raw));
            Assert.AreEqual(expected, strategy.Calibrate(raw, parameters), 1e-12);
        }
    }

    [TestMethod]
    public void TemperatureCalibration_T2_SoftensScore()
    {
        // T > 1 软化：raw=2 → sigmoid(2/2)=sigmoid(1) < sigmoid(2)
        var strategy = new TemperatureCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "temperature",
            Kind = CalibrationMethodKind.Temperature,
            Temperature = 2.0,
            FittedAt = DateTimeOffset.UtcNow
        };

        var calibrated = strategy.Calibrate(2.0, parameters);
        var directSigmoid = 1.0 / (1.0 + Math.Exp(-2.0));
        Assert.IsTrue(calibrated < directSigmoid, $"T=2 应软化分数：{calibrated} < {directSigmoid}");
    }

    [TestMethod]
    public void TemperatureCalibration_T05_SharpensScore()
    {
        // T < 1 锐化：raw=2 → sigmoid(2/0.5)=sigmoid(4) > sigmoid(2)
        var strategy = new TemperatureCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "temperature",
            Kind = CalibrationMethodKind.Temperature,
            Temperature = 0.5,
            FittedAt = DateTimeOffset.UtcNow
        };

        var calibrated = strategy.Calibrate(2.0, parameters);
        var directSigmoid = 1.0 / (1.0 + Math.Exp(-2.0));
        Assert.IsTrue(calibrated > directSigmoid, $"T=0.5 应锐化分数：{calibrated} > {directSigmoid}");
    }

    [TestMethod]
    public void TemperatureCalibration_InvalidT_Throws()
    {
        var strategy = new TemperatureCalibration();
        foreach (var invalidT in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            var parameters = new CalibrationParameters
            {
                Method = "temperature",
                Kind = CalibrationMethodKind.Temperature,
                Temperature = invalidT,
                FittedAt = DateTimeOffset.UtcNow
            };
            Assert.ThrowsException<ArgumentException>(() =>
                strategy.Calibrate(0.5, parameters));
        }
    }

    [TestMethod]
    public void TemperatureCalibration_KindMismatch_Throws()
    {
        var strategy = new TemperatureCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            FittedAt = DateTimeOffset.UtcNow
        };

        Assert.ThrowsException<ArgumentException>(() =>
            strategy.Calibrate(0.5, parameters));
    }

    // -------------------------------------------------------------------------
    // IsotonicCalibration
    // -------------------------------------------------------------------------

    [TestMethod]
    public void IsotonicCalibration_InterpolatesBetweenPoints()
    {
        var strategy = new IsotonicCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = 0.0, Output = 0.1 },
                new IsotonicPoint { Input = 1.0, Output = 0.9 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        // 线性插值：raw=0.5 → 0.1 + (0.9-0.1) * 0.5 = 0.5
        Assert.AreEqual(0.5, strategy.Calibrate(0.5, parameters), 1e-9);
    }

    [TestMethod]
    public void IsotonicCalibration_ClampsBelowMinInput()
    {
        var strategy = new IsotonicCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = 0.5, Output = 0.2 },
                new IsotonicPoint { Input = 1.0, Output = 0.8 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        // raw < min input → clamp to first point output
        Assert.AreEqual(0.2, strategy.Calibrate(-1.0, parameters), 1e-12);
        Assert.AreEqual(0.2, strategy.Calibrate(0.0, parameters), 1e-12);
    }

    [TestMethod]
    public void IsotonicCalibration_ClampsAboveMaxInput()
    {
        var strategy = new IsotonicCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = 0.0, Output = 0.2 },
                new IsotonicPoint { Input = 0.5, Output = 0.8 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        // raw > max input → clamp to last point output
        Assert.AreEqual(0.8, strategy.Calibrate(1.0, parameters), 1e-12);
        Assert.AreEqual(0.8, strategy.Calibrate(100.0, parameters), 1e-12);
    }

    [TestMethod]
    public void IsotonicCalibration_LessThan2Points_ReturnsIdentity()
    {
        var strategy = new IsotonicCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[] { new IsotonicPoint { Input = 0.5, Output = 0.5 } },
            FittedAt = DateTimeOffset.UtcNow
        };

        Assert.AreEqual(0.7, strategy.Calibrate(0.7, parameters), 1e-12);
    }

    [TestMethod]
    public void IsotonicCalibration_EmptyPoints_ReturnsIdentity()
    {
        var strategy = new IsotonicCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = Array.Empty<IsotonicPoint>(),
            FittedAt = DateTimeOffset.UtcNow
        };

        Assert.AreEqual(0.42, strategy.Calibrate(0.42, parameters), 1e-12);
    }

    [TestMethod]
    public void IsotonicCalibration_KindMismatch_Throws()
    {
        var strategy = new IsotonicCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "identity",
            Kind = CalibrationMethodKind.Identity,
            FittedAt = DateTimeOffset.UtcNow
        };

        Assert.ThrowsException<ArgumentException>(() =>
            strategy.Calibrate(0.5, parameters));
    }

    [TestMethod]
    public void IsotonicCalibration_MultiSegmentInterpolation()
    {
        var strategy = new IsotonicCalibration();
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = new[]
            {
                new IsotonicPoint { Input = 0.0, Output = 0.0 },
                new IsotonicPoint { Input = 0.5, Output = 0.5 },
                new IsotonicPoint { Input = 1.0, Output = 1.0 }
            },
            FittedAt = DateTimeOffset.UtcNow
        };

        // raw=0.25 在第一段 → 0.0 + (0.5-0.0) * 0.5 = 0.25
        Assert.AreEqual(0.25, strategy.Calibrate(0.25, parameters), 1e-9);
        // raw=0.75 在第二段 → 0.5 + (1.0-0.5) * 0.5 = 0.75
        Assert.AreEqual(0.75, strategy.Calibrate(0.75, parameters), 1e-9);
    }
}

// ===========================================================================
// PlattCalibrationService 扩展注册方法
// ===========================================================================

[TestClass]
[TestCategory("R28-F")]
public sealed class R28F_PlattCalibrationServiceExtendedTests
{
    [TestMethod]
    public void RegisterTemperatureParameters_AppliesTemperatureScaling()
    {
        var service = new PlattCalibrationService();
        service.RegisterTemperatureParameters(t: 2.0, modelName: "temp-model");

        var raw = 2.0;
        var expected = 1.0 / (1.0 + Math.Exp(-raw / 2.0));
        Assert.AreEqual(expected, service.Calibrate(raw, "temp-model"), 1e-12);

        var parameters = service.GetParameters("temp-model");
        Assert.IsNotNull(parameters);
        Assert.AreEqual(CalibrationMethodKind.Temperature, parameters!.Kind);
        Assert.AreEqual(2.0, parameters.Temperature);
    }

    [TestMethod]
    public void RegisterTemperatureParameters_InvalidT_Throws()
    {
        var service = new PlattCalibrationService();
        foreach (var invalidT in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            Assert.ThrowsException<ArgumentException>(() =>
                service.RegisterTemperatureParameters(invalidT));
        }
    }

    [TestMethod]
    public void RegisterIsotonicParameters_AppliesInterpolation()
    {
        var service = new PlattCalibrationService();
        service.RegisterIsotonicParameters(
            new[]
            {
                new IsotonicPoint { Input = 0.0, Output = 0.1 },
                new IsotonicPoint { Input = 1.0, Output = 0.9 }
            },
            modelName: "iso-model");

        // raw=0.5 → 线性插值 0.1 + (0.9-0.1)*0.5 = 0.5
        Assert.AreEqual(0.5, service.Calibrate(0.5, "iso-model"), 1e-9);

        var parameters = service.GetParameters("iso-model");
        Assert.IsNotNull(parameters);
        Assert.AreEqual(CalibrationMethodKind.Isotonic, parameters!.Kind);
        Assert.AreEqual(2, parameters.IsotonicPoints.Count);
    }

    [TestMethod]
    public void RegisterIsotonicParameters_UnsortedPoints_Throws()
    {
        var service = new PlattCalibrationService();
        Assert.ThrowsException<ArgumentException>(() =>
            service.RegisterIsotonicParameters(
                new[]
                {
                    new IsotonicPoint { Input = 1.0, Output = 0.9 },
                    new IsotonicPoint { Input = 0.0, Output = 0.1 } // 逆序
                }));
    }

    [TestMethod]
    public void RegisterIdentityParameters_ResetsToIdentity()
    {
        var service = new PlattCalibrationService();
        service.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: "platt-model");
        // 先验证 Platt 生效
        Assert.AreNotEqual(0.0, service.Calibrate(0.0, "platt-model"), 1e-12);

        // 重置为 Identity
        service.RegisterIdentityParameters(modelName: "platt-model");
        Assert.AreEqual(0.0, service.Calibrate(0.0, "platt-model"), 1e-12);

        var parameters = service.GetParameters("platt-model");
        Assert.IsNotNull(parameters);
        Assert.AreEqual(CalibrationMethodKind.Identity, parameters!.Kind);
    }

    [TestMethod]
    public void RegisterTemperatureParameters_GlobalDefault_OverridesIdentity()
    {
        var service = new PlattCalibrationService();
        // 默认是 Identity
        Assert.AreEqual(0.5, service.Calibrate(0.5), 1e-12);

        // 注册全局 Temperature
        service.RegisterTemperatureParameters(t: 1.0);
        var expected = 1.0 / (1.0 + Math.Exp(-0.5));
        Assert.AreEqual(expected, service.Calibrate(0.5), 1e-12);
    }
}

// ===========================================================================
// ModelExecutionSnapshot
// ===========================================================================

[TestClass]
[TestCategory("R28-F")]
public sealed class R28F_ModelExecutionSnapshotTests
{
    [TestMethod]
    public void ModelExecutionSnapshot_Construct_WithAllFields()
    {
        var snapshot = new ModelExecutionSnapshot
        {
            ModelArtifactId = "model-001",
            ModelVersion = "1.2.0",
            FeatureSchemaVersion = "schema-2.0",
            CalibrationVersion = "calib-v3",
            EngineKind = InferenceEngineKind.RealModel,
            ContentHash = "sha256:abcdef",
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.AreEqual("model-001", snapshot.ModelArtifactId);
        Assert.AreEqual("1.2.0", snapshot.ModelVersion);
        Assert.AreEqual("schema-2.0", snapshot.FeatureSchemaVersion);
        Assert.AreEqual("calib-v3", snapshot.CalibrationVersion);
        Assert.AreEqual(InferenceEngineKind.RealModel, snapshot.EngineKind);
        Assert.AreEqual("sha256:abcdef", snapshot.ContentHash);
    }

    [TestMethod]
    public void ModelExecutionSnapshot_FromEngine_BuildsCorrectSnapshot()
    {
        // 模拟从引擎 + policy 构造快照的典型场景
        var engine = new DeterministicBatchInferenceEngine();
        var snapshot = new ModelExecutionSnapshot
        {
            ModelArtifactId = "deterministic-fallback",
            ModelVersion = engine.ModelVersion,
            FeatureSchemaVersion = "schema-1.0",
            CalibrationVersion = engine.CalibrationVersion,
            EngineKind = engine.Kind,
            ContentHash = engine.ContentHash,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.AreEqual("deterministic-hash-v1", snapshot.ModelVersion);
        Assert.AreEqual("deterministic-hash-v1:fnv1a-64", snapshot.ContentHash);
        Assert.AreEqual(InferenceEngineKind.DeterministicReplay, snapshot.EngineKind);
        Assert.AreEqual("default-v1", snapshot.CalibrationVersion);
        // FeatureSchemaVersion 与 ModelVersion 是独立维度
        Assert.AreNotEqual(snapshot.ModelVersion, snapshot.FeatureSchemaVersion);
    }
}

// ===========================================================================
// DefaultInferenceResultValidator
// ===========================================================================

[TestClass]
[TestCategory("R28-F")]
public sealed class R28F_DefaultInferenceResultValidatorTests
{
    private static FeatureVector MakeVector() => new()
    {
        SchemaVersion = "1.0.0",
        Values = new Dictionary<string, object> { ["x"] = 1.0 }
    };

    // -------------------------------------------------------------------------
    // Validate(BatchInferenceRequest, BatchInferenceResult)
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Validate_SuccessWithMatchingCounts_Passes()
    {
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest
        {
            Inputs = new[] { MakeVector(), MakeVector() }
        };
        var result = new BatchInferenceResult
        {
            Outputs = new[]
            {
                new InferenceOutput { Score = 0.5, Confidence = 0.9 },
                new InferenceOutput { Score = 0.3, Confidence = 0.8 }
            },
            Succeeded = true,
            Duration = TimeSpan.FromMilliseconds(1)
        };

        var validation = validator.Validate(request, result);

        Assert.IsTrue(validation.IsValid);
        Assert.IsNull(validation.Error);
    }

    [TestMethod]
    public void Validate_FailedResult_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest { Inputs = new[] { MakeVector() } };
        var result = new BatchInferenceResult
        {
            Succeeded = false,
            Error = "engine timeout",
            Outputs = Array.Empty<InferenceOutput>(),
            Duration = TimeSpan.Zero
        };

        var validation = validator.Validate(request, result);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Error!.Contains("推理未成功"));
    }

    [TestMethod]
    public void Validate_CountMismatch_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest { Inputs = new[] { MakeVector(), MakeVector() } };
        var result = new BatchInferenceResult
        {
            Succeeded = true,
            Outputs = new[] { new InferenceOutput { Score = 0.5, Confidence = 0.9 } }, // 1 != 2
            Duration = TimeSpan.FromMilliseconds(1)
        };

        var validation = validator.Validate(request, result);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Violations.Any(v => v.Contains("Outputs.Count")));
    }

    [TestMethod]
    public void Validate_NaNScore_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest { Inputs = new[] { MakeVector() } };
        var result = new BatchInferenceResult
        {
            Succeeded = true,
            Outputs = new[] { new InferenceOutput { Score = double.NaN, Confidence = 0.9 } },
            Duration = TimeSpan.FromMilliseconds(1)
        };

        var validation = validator.Validate(request, result);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Violations.Any(v => v.Contains("Score") && v.Contains("NaN")));
    }

    [TestMethod]
    public void Validate_InfinityScore_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest { Inputs = new[] { MakeVector() } };
        var result = new BatchInferenceResult
        {
            Succeeded = true,
            Outputs = new[] { new InferenceOutput { Score = double.PositiveInfinity, Confidence = 0.9 } },
            Duration = TimeSpan.FromMilliseconds(1)
        };

        var validation = validator.Validate(request, result);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Violations.Any(v => v.Contains("Score") && v.Contains("有限")));
    }

    [TestMethod]
    public void Validate_NaNConfidence_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest { Inputs = new[] { MakeVector() } };
        var result = new BatchInferenceResult
        {
            Succeeded = true,
            Outputs = new[] { new InferenceOutput { Score = 0.5, Confidence = double.NaN } },
            Duration = TimeSpan.FromMilliseconds(1)
        };

        var validation = validator.Validate(request, result);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Violations.Any(v => v.Contains("Confidence") && v.Contains("有限")));
    }

    [TestMethod]
    public void Validate_ConfidenceOutOfRange_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest { Inputs = new[] { MakeVector() } };

        // > 1.0
        var resultHi = new BatchInferenceResult
        {
            Succeeded = true,
            Outputs = new[] { new InferenceOutput { Score = 0.5, Confidence = 1.5 } },
            Duration = TimeSpan.FromMilliseconds(1)
        };
        var validationHi = validator.Validate(request, resultHi);
        Assert.IsFalse(validationHi.IsValid);
        Assert.IsTrue(validationHi.Violations.Any(v => v.Contains("[0,1]")));

        // < 0.0
        var resultLo = new BatchInferenceResult
        {
            Succeeded = true,
            Outputs = new[] { new InferenceOutput { Score = 0.5, Confidence = -0.1 } },
            Duration = TimeSpan.FromMilliseconds(1)
        };
        var validationLo = validator.Validate(request, resultLo);
        Assert.IsFalse(validationLo.IsValid);
        Assert.IsTrue(validationLo.Violations.Any(v => v.Contains("[0,1]")));
    }

    [TestMethod]
    public void Validate_TimeoutSetButZeroDuration_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest
        {
            Inputs = new[] { MakeVector() },
            TimeoutMs = 5000
        };
        var result = new BatchInferenceResult
        {
            Succeeded = true,
            Outputs = new[] { new InferenceOutput { Score = 0.5, Confidence = 0.9 } },
            Duration = TimeSpan.Zero // timeout > 0 但 duration = 0
        };

        var validation = validator.Validate(request, result);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Violations.Any(v => v.Contains("TimeoutMs") && v.Contains("Duration")));
    }

    [TestMethod]
    public void Validate_TimeoutZero_NoDurationCheck()
    {
        // TimeoutMs=0 时不检查 Duration（允许即时返回）
        var validator = new DefaultInferenceResultValidator();
        var request = new BatchInferenceRequest
        {
            Inputs = new[] { MakeVector() },
            TimeoutMs = 0
        };
        var result = new BatchInferenceResult
        {
            Succeeded = true,
            Outputs = new[] { new InferenceOutput { Score = 0.5, Confidence = 0.9 } },
            Duration = TimeSpan.Zero
        };

        var validation = validator.Validate(request, result);

        Assert.IsTrue(validation.IsValid);
    }

    // -------------------------------------------------------------------------
    // Validate(FeatureBatch, BatchInferenceResult)
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Validate_FeatureBatch_SuccessWithMatchingCounts()
    {
        var validator = new DefaultInferenceResultValidator();
        var batch = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = new float[] { 0.1f, 0.2f, 0.3f, 0.4f },
            RowCount = 2,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };
        var result = new BatchInferenceResult
        {
            Succeeded = true,
            Outputs = new[]
            {
                new InferenceOutput { Score = 0.5, Confidence = 0.9 },
                new InferenceOutput { Score = 0.3, Confidence = 0.8 }
            },
            Duration = TimeSpan.FromMilliseconds(1)
        };

        var validation = validator.Validate(batch, result);

        Assert.IsTrue(validation.IsValid);
    }

    [TestMethod]
    public void Validate_FeatureBatch_CountMismatch_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var batch = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = new float[] { 0.1f, 0.2f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };
        var result = new BatchInferenceResult
        {
            Succeeded = true,
            Outputs = new[]
            {
                new InferenceOutput { Score = 0.5, Confidence = 0.9 },
                new InferenceOutput { Score = 0.3, Confidence = 0.8 }
            }, // 2 != 1
            Duration = TimeSpan.FromMilliseconds(1)
        };

        var validation = validator.Validate(batch, result);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Violations.Any(v => v.Contains("RowCount")));
    }

    // -------------------------------------------------------------------------
    // ValidateScoreWeights
    // -------------------------------------------------------------------------

    [TestMethod]
    public void ValidateScoreWeights_ValidSum_Passes()
    {
        var validator = new DefaultInferenceResultValidator();
        var result = validator.ValidateScoreWeights(0.6, 0.4);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void ValidateScoreWeights_SumNotOne_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var result = validator.ValidateScoreWeights(0.5, 0.5, expectedSum: 1.0);

        // 0.5 + 0.5 = 1.0 → valid
        Assert.IsTrue(result.IsValid);

        var result2 = validator.ValidateScoreWeights(0.3, 0.3);
        // 0.3 + 0.3 = 0.6 != 1.0 → invalid
        Assert.IsFalse(result2.IsValid);
        Assert.IsTrue(result2.Violations.Any(v => v.Contains("w_d + w_m")));
    }

    [TestMethod]
    public void ValidateScoreWeights_NegativeWeights_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var result = validator.ValidateScoreWeights(-0.1, 1.1);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Contains("DeterministicWeight") && v.Contains("负数")));
    }

    [TestMethod]
    public void ValidateScoreWeights_NaNWeights_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var result = validator.ValidateScoreWeights(double.NaN, 1.0);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Contains("DeterministicWeight") && v.Contains("有限")));
    }

    [TestMethod]
    public void ValidateScoreWeights_InfinityWeights_ReturnsInvalid()
    {
        var validator = new DefaultInferenceResultValidator();
        var result = validator.ValidateScoreWeights(0.5, double.PositiveInfinity);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Violations.Any(v => v.Contains("ModelWeight") && v.Contains("有限")));
    }
}
