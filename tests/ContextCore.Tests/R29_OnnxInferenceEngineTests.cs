using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Inference.Onnx;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

// ===========================================================================
// R29 WP-A-2：OnnxInferenceEngine 单元测试
//
// 覆盖范围：
//   §1 OnnxInferenceEngineOptions 默认值与 fallback 字段
//   §2 OnnxInferenceEngine 元数据暴露（ModelVersion / Kind / ContentHash / CalibrationVersion）
//   §3 OnnxInferenceEngine.InferBatchAsync 委托到 IOnnxInferenceSession
//   §4 OnnxInferenceEngine.InferAsync 字典路径 → FeatureBatch 转换
//   §5 空批次与取消处理
//   §6 超时控制（CreateLinkedCancellationTokenSource）
//   §7 DI 注册扩展（直接注册与工厂延迟加载）
//
// 设计：
//   使用 MockOnnxInferenceSession 隔离真实 ONNX 模型加载，
//   让单元测试可在无模型文件、无 GPU 环境下运行。
//   真实模型端到端测试由 WP-A-5 承担（需 Testcontainers + ONNX 模型工件）。
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("WP-A-2")]
public sealed class R29_OnnxInferenceEngineTests
{
    // ===========================================================================
    // §1 OnnxInferenceEngineOptions 默认值与 fallback 字段
    // ===========================================================================

    [TestMethod]
    public void OnnxInferenceEngineOptions_Defaults_AreSensible()
    {
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits"
        };

        Assert.AreEqual("onnx-local", options.ModelArtifactId);
        Assert.AreEqual("1.0.0", options.ModelVersion);
        Assert.AreEqual("sha256:unspecified", options.ContentHash);
        Assert.IsNull(options.ModelPath);
        Assert.IsNull(options.ConfidenceOutputName);
        Assert.AreEqual(0, options.ScoreOutputIndex);
        Assert.AreEqual(1, options.ConfidenceOutputIndex);
        Assert.IsTrue(options.ApplySigmoid);
        Assert.IsTrue(options.ApplySigmoidToConfidence);
        Assert.AreEqual(1, options.IntraOpNumThreads);
        Assert.AreEqual(0, options.InterOpNumThreads);
        Assert.AreEqual(5000, options.InferenceTimeoutMs);
        Assert.IsTrue(options.EnableMemoryPattern);
    }

    [TestMethod]
    public void OnnxInferenceEngineOptions_FallbackMetadata_CanBeOverridden()
    {
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits",
            ModelPath = "/path/to/model.onnx",
            ModelArtifactId = "test-model-v1",
            ModelVersion = "2.0.0",
            ContentHash = "sha256:abc123"
        };

        Assert.AreEqual("/path/to/model.onnx", options.ModelPath);
        Assert.AreEqual("test-model-v1", options.ModelArtifactId);
        Assert.AreEqual("2.0.0", options.ModelVersion);
        Assert.AreEqual("sha256:abc123", options.ContentHash);
    }

    // ===========================================================================
    // §2 OnnxInferenceEngine 元数据暴露
    // ===========================================================================

    [TestMethod]
    public void OnnxInferenceEngine_ExposesSessionMetadata()
    {
        var session = new MockOnnxInferenceSession(
            modelArtifactId: "model-id-123",
            modelVersion: "1.5.0",
            contentHash: "sha256:deadbeef",
            outputs: Array.Empty<InferenceOutput>());
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits"
        };
        var engine = new OnnxInferenceEngine(session, options, calibrationVersion: "platt-v2");

        Assert.AreEqual("1.5.0", engine.ModelVersion);
        Assert.AreEqual(InferenceEngineKind.RealModel, engine.Kind);
        Assert.AreEqual("sha256:deadbeef", engine.ContentHash);
        Assert.AreEqual("platt-v2", engine.CalibrationVersion);
    }

    [TestMethod]
    public void OnnxInferenceEngine_DefaultCalibrationVersion_WhenNotProvided()
    {
        var session = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits"
        };
        var engine = new OnnxInferenceEngine(session, options);

        Assert.AreEqual("default-v1", engine.CalibrationVersion);
    }

    // ===========================================================================
    // §3 OnnxInferenceEngine.InferBatchAsync 委托到 session
    // ===========================================================================

    [TestMethod]
    public async Task InferBatchAsync_DelegatesToSession()
    {
        var expectedOutputs = new[]
        {
            new InferenceOutput { Score = 0.8, Confidence = 0.9 },
            new InferenceOutput { Score = 0.3, Confidence = 0.6 }
        };
        var session = new MockOnnxInferenceSession("id", "1.0.0", "hash", expectedOutputs);
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits",
            EnableWarmup = false // 关闭 warmup 以独立验证"单次推理只调用 session 一次"
        };
        var engine = new OnnxInferenceEngine(session, options);

        var batch = new FeatureBatch
        {
            SchemaVersion = "1.0.0",
            Values = new float[] { 1f, 2f, 3f, 4f },
            RowCount = 2,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };

        var result = await engine.InferBatchAsync(batch);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(2, result.Outputs.Count);
        Assert.AreEqual(0.8, result.Outputs[0].Score, 1e-9);
        Assert.AreEqual(0.9, result.Outputs[0].Confidence, 1e-9);
        Assert.AreEqual(0.3, result.Outputs[1].Score, 1e-9);
        Assert.AreEqual(0.6, result.Outputs[1].Confidence, 1e-9);
        Assert.AreEqual(1, session.InferBatchCallCount, "Session.InferBatchAsync 应被调用一次");
    }

    [TestMethod]
    public async Task InferBatchAsync_PassesBatchToSession()
    {
        var session = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits"
        };
        var engine = new OnnxInferenceEngine(session, options);

        var batch = new FeatureBatch
        {
            SchemaVersion = "v1",
            Values = new float[] { 1f, 2f, 3f, 4f, 5f, 6f },
            RowCount = 3,
            FeatureCount = 2,
            FeatureNames = new[] { "x", "y" }
        };

        await engine.InferBatchAsync(batch);

        Assert.IsNotNull(session.LastReceivedBatch);
        Assert.AreEqual(3, session.LastReceivedBatch!.RowCount);
        Assert.AreEqual(2, session.LastReceivedBatch.FeatureCount);
        Assert.AreEqual(6, session.LastReceivedBatch.Values.Length);
    }

    // ===========================================================================
    // §4 OnnxInferenceEngine.InferAsync 字典路径 → FeatureBatch 转换
    // ===========================================================================

    [TestMethod]
    public async Task InferAsync_ConvertsFeatureVectorsToFeatureBatch()
    {
        var session = new MockOnnxInferenceSession(
            "id", "1.0.0", "hash",
            new[]
            {
                new InferenceOutput { Score = 0.5, Confidence = 0.5 }
            });
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits"
        };
        var engine = new OnnxInferenceEngine(session, options);

        var request = new BatchInferenceRequest
        {
            Inputs = new[]
            {
                new FeatureVector
                {
                    SchemaVersion = "v1",
                    Values = new Dictionary<string, object>
                    {
                        ["feature_a"] = 1.5,
                        ["feature_b"] = 2.0
                    }
                }
            }
        };

        var result = await engine.InferAsync(request);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Outputs.Count);
        Assert.IsNotNull(session.LastReceivedBatch);
        Assert.AreEqual(1, session.LastReceivedBatch!.RowCount);
        Assert.AreEqual(2, session.LastReceivedBatch.FeatureCount);
        // 字典 key 按 Ordinal 排序后转连续内存：feature_a, feature_b
        Assert.AreEqual("feature_a", session.LastReceivedBatch.FeatureNames[0]);
        Assert.AreEqual("feature_b", session.LastReceivedBatch.FeatureNames[1]);
        Assert.AreEqual(1.5f, session.LastReceivedBatch.Values.Span[0], 1e-6);
        Assert.AreEqual(2.0f, session.LastReceivedBatch.Values.Span[1], 1e-6);
    }

    [TestMethod]
    public async Task InferAsync_SupportsMultipleNumericTypes()
    {
        var session = new MockOnnxInferenceSession(
            "id", "1.0.0", "hash",
            new[]
            {
                new InferenceOutput { Score = 0.5, Confidence = 0.5 },
                new InferenceOutput { Score = 0.6, Confidence = 0.6 },
                new InferenceOutput { Score = 0.7, Confidence = 0.7 },
                new InferenceOutput { Score = 0.8, Confidence = 0.8 }
            });
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits"
        };
        var engine = new OnnxInferenceEngine(session, options);

        var request = new BatchInferenceRequest
        {
            Inputs = new[]
            {
                new FeatureVector
                {
                    SchemaVersion = "v1",
                    Values = new Dictionary<string, object>
                    {
                        ["a"] = 1,        // int
                        ["b"] = 2L,       // long
                        ["c"] = true,     // bool -> 1f
                        ["d"] = 0.5f      // float
                    }
                },
                new FeatureVector
                {
                    SchemaVersion = "v1",
                    Values = new Dictionary<string, object>
                    {
                        ["a"] = "3.5",    // string -> parsed
                        ["b"] = 0.0,      // double
                        ["c"] = false,    // bool -> 0f
                        ["d"] = 1.25      // double
                    }
                }
            }
        };

        var result = await engine.InferAsync(request);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(2, result.Outputs.Count);
        var received = session.LastReceivedBatch!;
        Assert.AreEqual(1f, received.Values.Span[0], 1e-6);  // a=1 (int)
        Assert.AreEqual(2f, received.Values.Span[1], 1e-6);  // b=2 (long)
        Assert.AreEqual(1f, received.Values.Span[2], 1e-6);  // c=true -> 1
        Assert.AreEqual(0.5f, received.Values.Span[3], 1e-6); // d=0.5 (float)
        Assert.AreEqual(3.5f, received.Values.Span[4], 1e-6); // a="3.5" parsed
        Assert.AreEqual(0f, received.Values.Span[5], 1e-6);  // b=0.0 (double)
        Assert.AreEqual(0f, received.Values.Span[6], 1e-6);  // c=false -> 0
        Assert.AreEqual(1.25f, received.Values.Span[7], 1e-6); // d=1.25 (double)
    }

    // ===========================================================================
    // §5 空批次与取消处理
    // ===========================================================================

    [TestMethod]
    public async Task InferAsync_EmptyInputs_ReturnsEmptyResultWithoutCallingSession()
    {
        var session = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits"
        };
        var engine = new OnnxInferenceEngine(session, options);

        var request = new BatchInferenceRequest
        {
            Inputs = Array.Empty<FeatureVector>()
        };

        var result = await engine.InferAsync(request);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, result.Outputs.Count);
        Assert.AreEqual(0, session.InferBatchCallCount, "空输入不应调用 session");
    }

    [TestMethod]
    public async Task InferBatchAsync_PreCancelledToken_ReturnsFailedResult()
    {
        var session = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits"
        };
        var engine = new OnnxInferenceEngine(session, options);

        var batch = new FeatureBatch
        {
            SchemaVersion = "v1",
            Values = new float[] { 1f, 2f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await engine.InferBatchAsync(batch, cts.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("推理被取消。", result.Error);
        Assert.AreEqual(0, session.InferBatchCallCount, "预取消不应调用 session");
    }

    // ===========================================================================
    // §6 超时控制
    // ===========================================================================

    [TestMethod]
    public async Task InferAsync_RespectsRequestTimeout()
    {
        // 让 mock session 阻塞到取消以模拟超时
        var session = new SlowOnnxInferenceSession(delayMs: 5000);
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits",
            InferenceTimeoutMs = 100 // 引擎级硬超时
        };
        var engine = new OnnxInferenceEngine(session, options);

        var request = new BatchInferenceRequest
        {
            Inputs = new[]
            {
                new FeatureVector
                {
                    SchemaVersion = "v1",
                    Values = new Dictionary<string, object> { ["a"] = 1.0 }
                }
            },
            TimeoutMs = 100
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await engine.InferAsync(request);
        stopwatch.Stop();

        // 应在超时后立即返回（不是 5000ms 后）
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 2000, $"应在超时后立即返回，实际耗时 {stopwatch.ElapsedMilliseconds}ms");
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task InferBatchAsync_OrphanBackPressure_RejectsWhenNativePoolSaturated()
    {
        // 子问题7：native session.Run 无法被中断；超时后孤儿任务占用 ORT 线程池。
        // 当孤儿数达 MaxOrphanedInferences 时，新请求立即返回 NativePoolSaturated（back-pressure）。
        var session = new SlowOnnxInferenceSession(delayMs: 1000); // 孤儿存活 1s
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits",
            InferenceTimeoutMs = 100, // 100ms 超时
            MaxOrphanedInferences = 1, // 仅允许 1 个孤儿
            CircuitBreakerThreshold = 0 // 禁用熔断器，隔离 back-pressure 行为
        };
        var engine = new OnnxInferenceEngine(session, options);

        var batch = new FeatureBatch
        {
            SchemaVersion = "v1",
            Values = new float[] { 1.0f },
            RowCount = 1,
            FeatureCount = 1,
            FeatureNames = new[] { "a" }
        };

        // 请求 1：超时（100ms），产生 1 个孤儿 native 调用（存活 1000ms）。
        var result1 = await engine.InferBatchAsync(batch);
        Assert.IsFalse(result1.Succeeded);
        Assert.IsTrue(result1.Error!.Contains("InferenceTimeout"), $"应为超时：{result1.Error}");

        // 请求 2：孤儿数已达上限（1），立即被 back-pressure 拒绝。
        var stopwatch = Stopwatch.StartNew();
        var result2 = await engine.InferBatchAsync(batch);
        stopwatch.Stop();
        Assert.IsFalse(result2.Succeeded);
        Assert.IsTrue(result2.Error!.Contains("NativePoolSaturated"), $"应为 back-pressure 拒绝：{result2.Error}");
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 100, $"应立即拒绝，实际耗时 {stopwatch.ElapsedMilliseconds}ms");

        // 等待孤儿 native 调用完成（1000ms + 余量），back-pressure 释放。
        await Task.Delay(1300);

        // 请求 3：孤儿已退出，不再被 back-pressure 拒绝（会再次超时，但不应是 NativePoolSaturated）。
        var result3 = await engine.InferBatchAsync(batch);
        Assert.IsFalse(result3.Succeeded);
        Assert.IsFalse(result3.Error!.Contains("NativePoolSaturated"), $"back-pressure 应已释放：{result3.Error}");
    }

    // ===========================================================================
    // §7 DI 注册扩展
    // ===========================================================================

    [TestMethod]
    public void AddOnnxInferenceEngine_DirectRegistration_RegistersEngineAndInterface()
    {
        var services = new ServiceCollection();
        var session = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits"
        };
        var engine = new OnnxInferenceEngine(session, options);

        services.AddOnnxInferenceEngine(engine);

        var provider = services.BuildServiceProvider();
        var resolvedEngine = provider.GetRequiredService<OnnxInferenceEngine>();
        var resolvedInterface = provider.GetRequiredService<IBatchInferenceEngine>();

        Assert.AreSame(engine, resolvedEngine);
        Assert.AreSame(engine, resolvedInterface);
    }

    [TestMethod]
    public void AddOnnxInferenceEngine_FactoryRegistration_RegistersLazySession()
    {
        var services = new ServiceCollection();
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits",
            ModelPath = "/nonexistent/model.onnx"
        };

        services.AddOnnxInferenceEngine(options, calibrationVersion: "test-v1");
        // 替换工厂为 mock，避免真实加载 ONNX 文件
        var mockSession = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());
        var mockFactory = new MockSessionFactory(mockSession);
        services.AddSingleton<IOnnxInferenceSessionFactory>(mockFactory);

        var provider = services.BuildServiceProvider();
        var resolvedEngine = provider.GetRequiredService<OnnxInferenceEngine>();
        var resolvedInterface = provider.GetRequiredService<IBatchInferenceEngine>();

        Assert.IsNotNull(resolvedEngine);
        Assert.AreSame(resolvedEngine, resolvedInterface);
        Assert.AreEqual("test-v1", resolvedEngine.CalibrationVersion);
        Assert.AreEqual("1.0.0", resolvedEngine.ModelVersion);
    }
}

// ===========================================================================
// 测试辅助：Mock 实现
// ===========================================================================

internal sealed class MockOnnxInferenceSession : IOnnxInferenceSession
{
    private readonly IReadOnlyList<InferenceOutput> _outputs;

    public MockOnnxInferenceSession(
        string modelArtifactId,
        string modelVersion,
        string contentHash,
        IReadOnlyList<InferenceOutput> outputs)
    {
        ModelArtifactId = modelArtifactId;
        ModelVersion = modelVersion;
        ContentHash = contentHash;
        _outputs = outputs;
    }

    public string ModelArtifactId { get; }

    public string ModelVersion { get; }

    public string ContentHash { get; }

    public int InferBatchCallCount { get; private set; }

    public FeatureBatch? LastReceivedBatch { get; private set; }

    public ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken cancellationToken = default)
    {
        // 与真实 OnnxRuntimeInferenceSession 行为一致：先检查取消，再记录调用
        if (cancellationToken.IsCancellationRequested)
        {
            return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = "推理被取消。",
                Duration = TimeSpan.Zero
            });
        }

        InferBatchCallCount++;
        LastReceivedBatch = batch;

        // 按 batch.RowCount 截取 outputs（不足时补默认值）
        var outputs = new InferenceOutput[batch.RowCount];
        for (var i = 0; i < batch.RowCount; i++)
        {
            outputs[i] = i < _outputs.Count
                ? _outputs[i]
                : new InferenceOutput { Score = 0.0, Confidence = 0.0 };
        }

        return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
        {
            Outputs = outputs,
            Succeeded = true,
            Error = null,
            Duration = TimeSpan.FromMilliseconds(1)
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SlowOnnxInferenceSession : IOnnxInferenceSession
{
    private readonly int _delayMs;

    public SlowOnnxInferenceSession(int delayMs)
    {
        _delayMs = delayMs;
        ModelArtifactId = "slow";
        ModelVersion = "1.0.0";
        ContentHash = "slow-hash";
    }

    public string ModelArtifactId { get; }
    public string ModelVersion { get; }
    public string ContentHash { get; }

    public async ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(_delayMs, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = "推理超时。",
                Duration = TimeSpan.Zero
            };
        }

        return new BatchInferenceResult
        {
            Outputs = Array.Empty<InferenceOutput>(),
            Succeeded = true,
            Error = null,
            Duration = TimeSpan.FromMilliseconds(_delayMs)
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class MockSessionFactory : IOnnxInferenceSessionFactory
{
    private readonly IOnnxInferenceSession _session;

    public MockSessionFactory(IOnnxInferenceSession session)
    {
        _session = session;
    }

    public ValueTask<IOnnxInferenceSession> CreateAsync(
        OnnxInferenceEngineOptions options,
        ModelArtifactDescriptor? descriptor = null,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_session);
    }
}
