using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using ContextCore.Abstractions;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Inference.Onnx;

namespace ContextCore.Tests;

// ===========================================================================
// R29-Hard-Gate：Model Activation 硬验收门测试
//
// 验证任务D 修复后的六个核心模型激活保证：
//   1. ActualModelFile_HashMatches_Descriptor
//      模型激活时实际文件 SHA-256 与 descriptor.ContentHash 匹配。
//   2. Warmup_UsesExact_FeatureSchemaWidth
//      Warmup 使用准确的 FeatureSchema 宽度（而非默认值或猜测值）。
//   3. GoldenProbe_PassesBefore_Activation
//      Golden Probe 在模型激活前通过（probe 失败 → 模型不被激活）。
//   4. HotSwap_DoesNotDispose_InFlightEngine
//      热切换时不 Dispose 正在处理请求的旧引擎。
//   5. OldEngine_IsEventuallyDisposed
//      旧引擎最终被清理（不会泄漏）。
//   6. NativeInferenceTimeout_DoesNotExhaust_Workers
//      原生推理超时不会耗尽所有 worker（SemaphoreSlim 被释放）。
//
// 这些测试是"硬验收门"——任一失败意味着 Model Activation 修复回退，不能合并。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Model-Activation")]
public sealed class R29H_ModelActivationAcceptanceTests
{
    private const string SchemaVersion = "r29h-schema-v1";
    private const string CalibrationVersion = "r29h-cal-v1";

    // ===========================================================================
    // 测试 1：实际模型文件的 SHA-256 与 descriptor 中记录的 ContentHash 匹配
    // ===========================================================================

    [TestMethod]
    public async Task ActualModelFile_HashMatches_Descriptor()
    {
        // 创建临时文件（非真实 ONNX，仅用于 SHA-256 校验）
        var tempDir = Path.Combine(Path.GetTempPath(), "r29h-hash-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var modelPath = Path.Combine(tempDir, "fake-model.onnx");
        await File.WriteAllBytesAsync(modelPath, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });
        try
        {
            var actualHash = ComputeSha256(modelPath);

            // 使用真实 OnnxRuntimeInferenceSessionFactory（它会做 SHA-256 校验）
            var factory = new OnnxRuntimeInferenceSessionFactory();
            var options = new OnnxInferenceEngineOptions
            {
                InputTensorName = "input",
                ScoreOutputName = "logits",
                ModelPath = modelPath,
                InferenceTimeoutMs = 5000
            };

            // ── 场景 A：正确的 hash ──
            // hash 校验通过，但文件不是有效 ONNX → 在 InferenceSession 构造时失败
            var descriptorCorrectHash = MakeDescriptor("hash-correct", modelPath, actualHash);
            var resultCorrect = await ActivateViaManagerAsync(descriptorCorrectHash, factory, options);

            Assert.IsFalse(resultCorrect.Success, "非 ONNX 文件应激活失败");
            // 错误不应包含 "ModelFileHashMismatch"（hash 匹配，是后续 session 创建失败）
            Assert.IsFalse(resultCorrect.Error!.Contains("ModelFileHashMismatch"),
                $"hash 正确时不应报 ModelFileHashMismatch：{resultCorrect.Error}");

            // ── 场景 B：错误的 hash ──
            var descriptorWrongHash = MakeDescriptor("hash-wrong", modelPath, "sha256:0000000000000000000000000000000000000000000000000000000000000000");
            var resultWrong = await ActivateViaManagerAsync(descriptorWrongHash, factory, options);

            Assert.IsFalse(resultWrong.Success, "hash 不匹配应激活失败");
            // 错误应包含 "ModelFileHashMismatch"
            Assert.IsTrue(resultWrong.Error!.Contains("ModelFileHashMismatch"),
                $"hash 不匹配时应报 ModelFileHashMismatch：{resultWrong.Error}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // ===========================================================================
    // 测试 2：Warmup 使用准确的 FeatureSchema 宽度
    // ===========================================================================

    [TestMethod]
    public async Task Warmup_UsesExact_FeatureSchemaWidth()
    {
        // 定义 FeatureCount=4 的 schema（非默认值 2，确保 warmup 使用的是 schema 宽度）
        const int expectedFeatureCount = 4;
        var featureRegistry = BuildFeatureRegistry(expectedFeatureCount);

        // 使用自定义 session 捕获 warmup batch
        var trackingSession = new TrackingOnnxInferenceSession(
            "warmup-test-model", "1.0.0", "sha256:warmup",
            new[] { new InferenceOutput { Score = 0.5, Confidence = 0.9 } });
        var factory = new SimpleSessionFactory(trackingSession);

        var descriptor = MakeDescriptor("warmup-test", "/path/to/model.onnx", "sha256:test");
        var registry = new SimpleModelArtifactRegistry();
        await registry.RegisterAsync(descriptor);

        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();
        var manager = new ModelActivationManager(
            registry, calValidator, featureRegistry, factory, fallback, BuildCalibrationService());

        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits",
            InferenceTimeoutMs = 5000
        };

        var result = await manager.ActivateAsync("warmup-test", options);
        Assert.IsTrue(result.Success, $"激活应成功：{result.Error}");

        // 验证 warmup batch 的 FeatureCount 与 schema.Features.Count 一致
        Assert.IsNotNull(trackingSession.LastReceivedBatch, "warmup 应调用 session.InferBatchAsync");
        Assert.AreEqual(expectedFeatureCount, trackingSession.LastReceivedBatch!.FeatureCount,
            $"warmup batch 的 FeatureCount 应等于 schema.Features.Count={expectedFeatureCount}");
        Assert.AreEqual(1, trackingSession.LastReceivedBatch.RowCount,
            "warmup batch 的 RowCount 应为 1");
        Assert.AreEqual(expectedFeatureCount, trackingSession.LastReceivedBatch.Values.Length,
            $"warmup batch 的 Values.Length 应等于 FeatureCount={expectedFeatureCount}");
    }

    // ===========================================================================
    // 测试 3：Golden Probe 在模型激活前通过
    // ===========================================================================

    [TestMethod]
    public async Task GoldenProbe_PassesBefore_Activation()
    {
        // ── 场景 A：Golden Probe 失败（Confidence 越界 [0,1]）──
        var badSession = new TrackingOnnxInferenceSession(
            "probe-fail-model", "1.0.0", "sha256:probe-fail",
            new[] { new InferenceOutput { Score = 0.5, Confidence = 2.0 } }); // Confidence > 1 → 越界
        var badFactory = new SimpleSessionFactory(badSession);
        var featureRegistry = BuildFeatureRegistry(2);
        var badDescriptor = MakeDescriptor("probe-fail", "/path/to/model.onnx", "sha256:probe-fail");
        var badRegistry = new SimpleModelArtifactRegistry();
        await badRegistry.RegisterAsync(badDescriptor);

        var managerBad = new ModelActivationManager(
            badRegistry, new DefaultCalibrationValidator(), featureRegistry,
            badFactory, new DeterministicBatchInferenceEngine(), BuildCalibrationService());

        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits",
            InferenceTimeoutMs = 5000
        };

        var badResult = await managerBad.ActivateAsync("probe-fail", options);

        // Golden Probe 失败 → 激活失败
        Assert.IsFalse(badResult.Success, "Golden Probe 失败时不应激活");
        Assert.IsTrue(badResult.Error!.Contains("Golden Probe"),
            $"错误应提及 Golden Probe：{badResult.Error}");
        Assert.IsNull(managerBad.ActiveEngine, "Golden Probe 失败后不应有活跃引擎");

        // ── 场景 B：Golden Probe 通过（Confidence 在 [0,1]）──
        var goodSession = new TrackingOnnxInferenceSession(
            "probe-pass-model", "1.0.0", "sha256:probe-pass",
            new[] { new InferenceOutput { Score = 0.5, Confidence = 0.9 } }); // 合法
        var goodFactory = new SimpleSessionFactory(goodSession);
        var goodDescriptor = MakeDescriptor("probe-pass", "/path/to/model.onnx", "sha256:probe-pass");
        var goodRegistry = new SimpleModelArtifactRegistry();
        await goodRegistry.RegisterAsync(goodDescriptor);

        var managerGood = new ModelActivationManager(
            goodRegistry, new DefaultCalibrationValidator(), featureRegistry,
            goodFactory, new DeterministicBatchInferenceEngine(), BuildCalibrationService());

        var goodResult = await managerGood.ActivateAsync("probe-pass", options);

        // Golden Probe 通过 → 激活成功
        Assert.IsTrue(goodResult.Success, $"Golden Probe 通过时应激活成功：{goodResult.Error}");
        Assert.IsNotNull(managerGood.ActiveEngine, "Golden Probe 通过后应有活跃引擎");
    }

    // ===========================================================================
    // 测试 4：热切换时不 Dispose 正在处理请求的旧引擎
    // ===========================================================================

    [TestMethod]
    public async Task HotSwap_DoesNotDispose_InFlightEngine()
    {
        // 使用慢速 session 模拟 in-flight 推理（延迟 500ms）
        var slowSession = new SlowOnnxInferenceSession(delayMs: 500);
        var factory = new SimpleSessionFactory(slowSession);
        var featureRegistry = BuildFeatureRegistry(2);
        var fallback = new DeterministicBatchInferenceEngine();

        var descriptorA = MakeDescriptor("hotswap-a", "/path/to/a.onnx", "sha256:a");
        var descriptorB = MakeDescriptor("hotswap-b", "/path/to/b.onnx", "sha256:b");
        var registry = new SimpleModelArtifactRegistry();
        await registry.RegisterAsync(descriptorA);
        await registry.RegisterAsync(descriptorB);

        var manager = new ModelActivationManager(
            registry, new DefaultCalibrationValidator(), featureRegistry,
            factory, fallback, BuildCalibrationService());

        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits",
            InferenceTimeoutMs = 10000, // 远大于 slow session 延迟
            PreviousEngineGracePeriodMs = 10000 // 长宽限期，确保旧引擎在测试期间不被清理
        };

        // 激活模型 A
        var resultA = await manager.ActivateAsync("hotswap-a", options);
        Assert.IsTrue(resultA.Success, $"模型 A 激活应成功：{resultA.Error}");
        var engineA = manager.ActiveEngine;
        Assert.IsNotNull(engineA, "模型 A 激活后应有活跃引擎");

        // 启动一个 in-flight 推理请求（不 await，让它在后台运行）
        var batch = new FeatureBatch
        {
            SchemaVersion = SchemaVersion,
            Values = new float[] { 0.5f, 0.7f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "lexical_score", "semantic_score" }
        };
        var inferenceTask = engineA!.InferBatchAsync(batch).AsTask();

        // 等待推理请求开始（确保它是 in-flight）
        await Task.Delay(50);

        // 热切换到模型 B（旧引擎 A 进入 grace period）
        var resultB = await manager.ActivateAsync("hotswap-b", options);
        Assert.IsTrue(resultB.Success, $"模型 B 激活应成功：{resultB.Error}");
        Assert.IsNotNull(manager.ActiveEngine);
        Assert.AreNotSame(engineA, manager.ActiveEngine, "热切换后活跃引擎应切换到 B");

        // 验证 in-flight 推理请求仍能完成（旧引擎未被 Dispose）
        var inferenceResult = await inferenceTask;
        Assert.IsTrue(inferenceResult.Succeeded,
            $"in-flight 推理应成功完成（旧引擎未被 Dispose）：{inferenceResult.Error}");
    }

    // ===========================================================================
    // 测试 5：旧引擎最终被清理（不会泄漏）
    // ===========================================================================

    [TestMethod]
    public async Task OldEngine_IsEventuallyDisposed()
    {
        // 使用短 grace period，让旧引擎在短时间内被清理
        var sessionA = new TrackingOnnxInferenceSession(
            "old-engine-a", "1.0.0", "sha256:old-a",
            new[] { new InferenceOutput { Score = 0.5, Confidence = 0.9 } });
        var sessionB = new TrackingOnnxInferenceSession(
            "old-engine-b", "1.0.0", "sha256:old-b",
            new[] { new InferenceOutput { Score = 0.5, Confidence = 0.9 } });
        var factory = new SimpleSessionFactory(() => sessionA, () => sessionB);
        var featureRegistry = BuildFeatureRegistry(2);
        var fallback = new DeterministicBatchInferenceEngine();

        var descriptorA = MakeDescriptor("old-engine-a", "/path/to/a.onnx", "sha256:old-a");
        var descriptorB = MakeDescriptor("old-engine-b", "/path/to/b.onnx", "sha256:old-b");
        var registry = new SimpleModelArtifactRegistry();
        await registry.RegisterAsync(descriptorA);
        await registry.RegisterAsync(descriptorB);

        var manager = new ModelActivationManager(
            registry, new DefaultCalibrationValidator(), featureRegistry,
            factory, fallback, BuildCalibrationService());

        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits",
            InferenceTimeoutMs = 5000,
            PreviousEngineGracePeriodMs = 200 // 短宽限期：200ms 后清理旧引擎
        };

        // 激活模型 A
        var resultA = await manager.ActivateAsync("old-engine-a", options);
        Assert.IsTrue(resultA.Success, $"模型 A 激活应成功：{resultA.Error}");

        // 激活模型 B（热切换），旧引擎 A 进入 _previousEngine
        var resultB = await manager.ActivateAsync("old-engine-b", options);
        Assert.IsTrue(resultB.Success, $"模型 B 激活应成功：{resultB.Error}");

        // P1 重构后用 _previousHandle（ActiveModelHandle）替代 _previousEngine
        var previousHandleField = typeof(ModelActivationManager)
            .GetField("_previousHandle", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(previousHandleField, "_previousHandle 字段应存在");

        var previousRightAfter = previousHandleField!.GetValue(manager);
        Assert.IsNotNull(previousRightAfter, "热切换后 _previousHandle 应引用旧引擎 A 的 handle");

        // 等待 grace period 过期 + 后台清理任务执行
        await Task.Delay(800);

        // 验证 _previousHandle 已被清理（不再引用旧引擎）
        var previousAfterGrace = previousHandleField.GetValue(manager);
        Assert.IsNull(previousAfterGrace,
            "grace period 过期后 _previousHandle 应被清理为 null，避免引擎泄漏");
    }

    // ===========================================================================
    // 测试 6：原生推理超时不会耗尽所有 worker（SemaphoreSlim 被释放）
    // ===========================================================================

    [TestMethod]
    public async Task NativeInferenceTimeout_DoesNotExhaust_Workers()
    {
        // 创建慢速 session（延迟 5000ms，远超引擎超时）
        var slowSession = new SlowOnnxInferenceSession(delayMs: 5000);
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "logits",
            InferenceTimeoutMs = 200, // 短超时：200ms
            MaxConcurrentInferences = 1, // 仅 1 个并发槽位
            CircuitBreakerThreshold = 0, // 禁用熔断器，确保超时后仍可继续
            CpuOversubscriptionGuard = false // 禁用 CPU 保护，确保槽位数 = MaxConcurrentInferences
        };
        var engine = new OnnxInferenceEngine(slowSession, options);

        var batch = new FeatureBatch
        {
            SchemaVersion = SchemaVersion,
            Values = new float[] { 0.5f, 0.7f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };

        // ── 第一个请求：会超时（200ms 后返回失败，但 native 调用仍在后台运行）──
        var firstResult = await engine.InferBatchAsync(batch);
        Assert.IsFalse(firstResult.Succeeded, "第一个请求应因超时失败");
        Assert.IsTrue(firstResult.Error!.Contains("Timeout") || firstResult.Error!.Contains("超时"),
            $"错误应包含超时信息：{firstResult.Error}");

        // ── 第二个请求：应能继续执行（证明 SemaphoreSlim 被释放）──
        // 用快速 session 替换（通过新引擎），或直接发第二个请求到同一引擎
        // 注意：slowSession 仍会延迟 5000ms，但引擎超时 200ms 会先触发
        var secondResult = await engine.InferBatchAsync(batch);

        // 第二个请求不应被永久阻塞（证明槽位被释放）
        // 它也会超时（因为 session 仍然慢），但关键是不卡死
        Assert.IsFalse(secondResult.Succeeded, "第二个请求也会超时（session 仍然慢）");
        Assert.IsTrue(secondResult.Error!.Contains("Timeout") || secondResult.Error!.Contains("超时"),
            $"第二个请求错误应包含超时信息：{secondResult.Error}");

        // 验证：如果 SemaphoreSlim 没有被释放，第二个请求会永久阻塞（测试会超时）
        // 测试能走到这里，说明 SemaphoreSlim 被正确释放了
    }

    // ===========================================================================
    // 辅助方法
    // ===========================================================================

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hashBytes = SHA256.HashData(stream);
        return "sha256:" + Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static ModelArtifactDescriptor MakeDescriptor(
        string artifactId, string artifactPath, string contentHash) => new()
    {
        ModelArtifactId = artifactId,
        ModelName = artifactId,
        ModelVersion = "1.0.0",
        FeatureSchemaVersion = SchemaVersion,
        CalibrationVersion = CalibrationVersion,
        EngineKind = InferenceEngineKind.RealModel,
        ContentHash = contentHash,
        ArtifactPath = artifactPath,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    private static DefaultFeatureRegistry BuildFeatureRegistry(int featureCount)
    {
        var registry = new DefaultFeatureRegistry();
        var features = new FeatureDefinition[featureCount];
        for (var i = 0; i < featureCount; i++)
        {
            features[i] = new FeatureDefinition
            {
                Name = $"feature_{i}",
                Type = FeatureType.Numeric,
                IsRequired = false,
                DefaultValue = "0"
            };
        }
        registry.Register(new FeatureSchema
        {
            Version = SchemaVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Features = features
        });
        return registry;
    }

    private static async ValueTask<ModelActivationResult> ActivateViaManagerAsync(
        ModelArtifactDescriptor descriptor,
        IOnnxInferenceSessionFactory factory,
        OnnxInferenceEngineOptions options)
    {
        var registry = new SimpleModelArtifactRegistry();
        await registry.RegisterAsync(descriptor);
        var manager = new ModelActivationManager(
            registry, new DefaultCalibrationValidator(), BuildFeatureRegistry(2),
            factory, new DeterministicBatchInferenceEngine(), BuildCalibrationService());
        return await manager.ActivateAsync(descriptor.ModelArtifactId, options);
    }

    /// <summary>
    /// 构建测试用 ICalibrationService：对任意 modelName + version 返回有效的 Identity 校准参数。
    /// WP-5 fail-closed 要求非 default-v1 的 CalibrationVersion 必须命中已注册参数，
    /// 本 helper 让校准验证通过，使测试聚焦于 warmup/probe/hotswap 等被测逻辑。
    /// </summary>
    private static ICalibrationService BuildCalibrationService() => new TestCalibrationService();

    // ===========================================================================
    // 私有 Mock：SimpleModelArtifactRegistry
    // ===========================================================================

    private sealed class SimpleModelArtifactRegistry : IModelArtifactRegistry
    {
        private readonly ConcurrentDictionary<string, ModelArtifactDescriptor> _byId = new(StringComparer.Ordinal);
        private readonly List<ModelArtifactDescriptor> _all = new();
        private readonly object _lock = new();

        public ValueTask RegisterAsync(ModelArtifactDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            lock (_lock)
            {
                if (_byId.ContainsKey(descriptor.ModelArtifactId))
                {
                    throw new InvalidOperationException($"ModelArtifactId '{descriptor.ModelArtifactId}' 已注册。");
                }
                _byId[descriptor.ModelArtifactId] = descriptor;
                _all.Add(descriptor);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<ModelArtifactDescriptor?> GetAsync(string modelArtifactId, CancellationToken cancellationToken = default)
        {
            _byId.TryGetValue(modelArtifactId, out var descriptor);
            return ValueTask.FromResult(descriptor);
        }

        public ValueTask<ModelArtifactDescriptor?> GetLatestAsync(string modelName, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var latest = _all
                    .Where(d => string.Equals(d.ModelName, modelName, StringComparison.Ordinal))
                    .OrderByDescending(d => d.RegisteredAt)
                    .FirstOrDefault();
                return ValueTask.FromResult(latest);
            }
        }

        public ValueTask<IReadOnlyList<ModelArtifactDescriptor>> ListByVersionAsync(string modelName, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var versions = _all
                    .Where(d => string.Equals(d.ModelName, modelName, StringComparison.Ordinal))
                    .OrderBy(d => d.RegisteredAt)
                    .ToList();
                return ValueTask.FromResult<IReadOnlyList<ModelArtifactDescriptor>>(versions);
            }
        }

        public ValueTask<IReadOnlyList<ModelArtifactDescriptor>> ListAllAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var all = _all.OrderBy(d => d.RegisteredAt).ToList();
                return ValueTask.FromResult<IReadOnlyList<ModelArtifactDescriptor>>(all);
            }
        }
    }

    // ===========================================================================
    // 私有 Mock：SimpleSessionFactory
    // ===========================================================================

    /// <summary>
    /// 简单工厂：返回预设的 session。
    /// 支持无参构造（每次返回同一 session）或双 session 轮替（用于热切换测试）。
    /// </summary>
    private sealed class SimpleSessionFactory : IOnnxInferenceSessionFactory
    {
        private readonly Func<IOnnxInferenceSession> _sessionProvider;

        public SimpleSessionFactory(IOnnxInferenceSession session)
        {
            _sessionProvider = () => session;
        }

        public SimpleSessionFactory(Func<IOnnxInferenceSession> first, Func<IOnnxInferenceSession> second)
        {
            var callCount = 0;
            _sessionProvider = () =>
            {
                var n = Interlocked.Increment(ref callCount);
                return n == 1 ? first() : second();
            };
        }

        public ValueTask<IOnnxInferenceSession> CreateAsync(
            OnnxInferenceEngineOptions options,
            ModelArtifactDescriptor? descriptor = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_sessionProvider());
        }
    }

    // ===========================================================================
    // 私有 Mock：TrackingOnnxInferenceSession
    // ===========================================================================

    /// <summary>
    /// 跟踪 warmup/probe 调用的 session。
    /// 记录最后一次接收的 batch（用于验证 FeatureSchema 宽度）。
    /// </summary>
    private sealed class TrackingOnnxInferenceSession : IOnnxInferenceSession
    {
        private readonly IReadOnlyList<InferenceOutput> _outputs;

        public TrackingOnnxInferenceSession(
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
        public FeatureBatch? LastReceivedBatch { get; private set; }
        public int DisposeCallCount { get; private set; }

        public ValueTask<BatchInferenceResult> InferBatchAsync(
            FeatureBatch batch,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(new BatchInferenceResult
                {
                    Outputs = Array.Empty<InferenceOutput>(),
                    Succeeded = false,
                    Error = "推理被取消。",
                    Duration = TimeSpan.Zero
                });
            }

            LastReceivedBatch = batch;

            var outputs = new InferenceOutput[batch.RowCount];
            for (var i = 0; i < batch.RowCount; i++)
            {
                outputs[i] = i < _outputs.Count
                    ? _outputs[i]
                    : new InferenceOutput { Score = 0.0, Confidence = 0.0 };
            }

            return ValueTask.FromResult(new BatchInferenceResult
            {
                Outputs = outputs,
                Succeeded = true,
                Error = null,
                Duration = TimeSpan.FromMilliseconds(1)
            });
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    // ===========================================================================
    // 私有 Mock：SlowOnnxInferenceSession
    // ===========================================================================

    /// <summary>
    /// 慢速 session：每次推理延迟指定毫秒，用于测试超时和热切换。
    /// </summary>
    private sealed class SlowOnnxInferenceSession : IOnnxInferenceSession
    {
        private readonly int _delayMs;

        public SlowOnnxInferenceSession(int delayMs)
        {
            _delayMs = delayMs;
            ModelArtifactId = "slow";
            ModelVersion = "1.0.0";
            ContentHash = "sha256:slow";
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
                Outputs = new[]
                {
                    new InferenceOutput { Score = 0.5, Confidence = 0.9 }
                },
                Succeeded = true,
                Error = null,
                Duration = TimeSpan.FromMilliseconds(_delayMs)
            };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ===========================================================================
    // 测试辅助：TestCalibrationService（对任意 modelName + version 返回有效 Identity 参数）
    // ===========================================================================

    /// <summary>
    /// 测试用 ICalibrationService：对任意 modelName + version 返回有效的 Identity 校准参数。
    /// WP-5 fail-closed 要求非 default-v1 的 CalibrationVersion 必须命中已注册参数，
    /// 此实现让校准验证始终通过，使测试聚焦于 warmup/probe/hotswap 等被测逻辑。
    /// </summary>
    private sealed class TestCalibrationService : ICalibrationService
    {
        private static readonly CalibrationParameters IdentityParams = new()
        {
            Method = "identity",
            Kind = CalibrationMethodKind.Identity,
            ParameterA = 1.0,
            ParameterB = 0.0,
            Parameter = 1.0,
            FittedAt = DateTimeOffset.UtcNow,
            Version = "any"
        };

        public double Calibrate(double rawScore, string? modelName = null) => rawScore;

        public IReadOnlyList<double> CalibrateBatch(IReadOnlyList<double> rawScores, string? modelName = null)
            => rawScores as IReadOnlyList<double> ?? rawScores.ToArray();

        public CalibrationParameters? GetParameters(string? modelName = null) => IdentityParams;

        public CalibrationParameters? GetParametersForVersion(string? modelName, string version) => IdentityParams;
    }
}
