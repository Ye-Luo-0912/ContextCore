using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using ContextCore.Abstractions;
using ContextCore.Inference.Onnx;

namespace ContextCore.Tests;

// ===========================================================================
// 推理热路径单元测试（性能优化）
//
// 覆盖范围：
// - OnnxExecutionProvider.DirectML 枚举值
// - 并发槽位默认值按 EP profile 解析（CPU=ProcessorCount，单 GPU=1）
// - 显式 MaxConcurrentInferences 覆盖 profile 默认
// - InferencePhaseTimingCallback 阶段耗时回调（Queue/Copy/Run/Parse）
// - InferenceMetrics 指标记录（session contention / shards / fill ratio /
//   queue wait / cancellation waste，含 model / node / batch 维度）
//
// 设计：
// 复用 R29_OnnxInferenceEngineTests 的 MockOnnxInferenceSession /
// SlowOnnxInferenceSession 隔离真实 ONNX 模型加载。
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("WP-A-2")]
public sealed class R30_InferenceHotPathTests
{
    private static FeatureBatch SingleRowBatch() => new()
    {
        SchemaVersion = "v1",
        Values = new float[] { 0.5f, 0.25f },
        RowCount = 1,
        FeatureCount = 2,
        FeatureNames = new[] { "f0", "f1" }
    };

    // ===========================================================================
    // DirectML EP
    // ===========================================================================

    [TestMethod]
    public void OnnxExecutionProvider_HasDirectMLValue()
    {
        Assert.AreEqual(3, (int)OnnxExecutionProvider.DirectML);
    }

    // ===========================================================================
    // 并发槽位默认值按 EP profile 解析
    // ===========================================================================

    [TestMethod]
    public void OnnxInferenceEngine_DefaultConcurrency_IsProfileAware()
    {
        var session = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());

        // CPU：沿用 ProcessorCount（历史行为）。
        var cpu = new OnnxInferenceEngine(session, new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "score",
            EnableWarmup = false
        });
        Assert.AreEqual(Environment.ProcessorCount, cpu.MaxConcurrency);

        // 单 GPU（CUDA / TensorRT / DirectML）：默认 1，避免按核数配置导致过度订阅。
        foreach (var ep in new[] { OnnxExecutionProvider.CUDA, OnnxExecutionProvider.TensorRT, OnnxExecutionProvider.DirectML })
        {
            var gpu = new OnnxInferenceEngine(session, new OnnxInferenceEngineOptions
            {
                InputTensorName = "input",
                ScoreOutputName = "score",
                EnableWarmup = false,
                ExecutionProvider = ep
            });
            Assert.AreEqual(1, gpu.MaxConcurrency, $"EP={ep} 默认并发应为 1");
        }
    }

    [TestMethod]
    public void OnnxInferenceEngine_ExplicitConcurrency_OverridesProfileDefault()
    {
        var session = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());
        var engine = new OnnxInferenceEngine(session, new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "score",
            EnableWarmup = false,
            ExecutionProvider = OnnxExecutionProvider.CUDA,
            MaxConcurrentInferences = 4
        });

        Assert.AreEqual(4, engine.MaxConcurrency);
    }

    [TestMethod]
    public void InferenceScheduler_DefaultConcurrency_IsProfileAware()
    {
        var session = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());
        var engine = new OnnxInferenceEngine(session, new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "score",
            EnableWarmup = false
        });

        var cpu = new InferenceScheduler(engine, new InferenceSchedulerOptions
        {
            EnableDynamicBatching = true,
            ExecutionProvider = OnnxExecutionProvider.CPU
        });
        Assert.AreEqual(Environment.ProcessorCount, cpu.MaxConcurrency);

        var gpu = new InferenceScheduler(engine, new InferenceSchedulerOptions
        {
            EnableDynamicBatching = true,
            ExecutionProvider = OnnxExecutionProvider.DirectML
        });
        Assert.AreEqual(1, gpu.MaxConcurrency);

        var explicitOverride = new InferenceScheduler(engine, new InferenceSchedulerOptions
        {
            EnableDynamicBatching = true,
            ExecutionProvider = OnnxExecutionProvider.DirectML,
            MaxConcurrency = 3
        });
        Assert.AreEqual(3, explicitOverride.MaxConcurrency);
    }

    // ===========================================================================
    // 阶段耗时回调
    // ===========================================================================

    [TestMethod]
    public async Task OnnxInferenceEngine_PhaseTimingCallback_ReportsAllPhases()
    {
        var outputs = new[] { new InferenceOutput { Score = 0.7, Confidence = 0.8 } };
        var session = new MockOnnxInferenceSession("id", "1.0.0", "hash", outputs);
        var phases = new ConcurrentQueue<InferencePhase>();
        var engine = new OnnxInferenceEngine(session, new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "score",
            EnableWarmup = false,
            InferencePhaseTimingCallback = (phase, elapsed) => phases.Enqueue(phase)
        });

        var result = await engine.InferBatchAsync(SingleRowBatch()).ConfigureAwait(false);

        Assert.IsTrue(result.Succeeded);
        foreach (var phase in Enum.GetValues<InferencePhase>())
        {
            Assert.IsTrue(phases.Contains(phase), $"应上报阶段 {phase} 的耗时");
        }
    }

    // ===========================================================================
    // InferenceMetrics 指标记录
    // ===========================================================================

    [TestMethod]
    public async Task InferenceHotPath_Metrics_AreRecorded()
    {
        using var capture = new InferenceMetricCapture();

        // ── 分片计数：MaxBatchSize=2 + 5 行 → 3 个 shard ──
        var session = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());
        var shardingEngine = new OnnxInferenceEngine(session, new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "score",
            EnableWarmup = false,
            MaxConcurrentInferences = 1,
            CpuOversubscriptionGuard = false,
            MaxBatchSize = 2
        });
        var bigBatch = new FeatureBatch
        {
            SchemaVersion = "v1",
            Values = new float[10],
            RowCount = 5,
            FeatureCount = 2,
            FeatureNames = new[] { "f0", "f1" }
        };
        var shardResult = await shardingEngine.InferBatchAsync(bigBatch).ConfigureAwait(false);
        Assert.IsTrue(shardResult.Succeeded);
        Assert.AreEqual(3, session.InferBatchCallCount, "5 行 / MaxBatchSize=2 应分 3 片执行");

        // ── 会话竞争：单槽位 + 慢会话 + 并发请求 ──
        var slow = new SlowOnnxInferenceSession(120);
        var contentionEngine = new OnnxInferenceEngine(slow, new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "score",
            EnableWarmup = false,
            MaxConcurrentInferences = 1,
            CpuOversubscriptionGuard = false
        });
        var first = contentionEngine.InferBatchAsync(SingleRowBatch()).AsTask();
        await Task.Delay(30).ConfigureAwait(false); // 首个请求已占用唯一槽位
        var second = contentionEngine.InferBatchAsync(SingleRowBatch()).AsTask();
        await Task.WhenAll(first, second).ConfigureAwait(false);

        // ── 调度器动态批处理：填充率 + 排队等待 + 取消浪费 ──
        var batchingEngine = new OnnxInferenceEngine(
            new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>()),
            new OnnxInferenceEngineOptions
            {
                InputTensorName = "input",
                ScoreOutputName = "score",
                EnableWarmup = false,
                MaxConcurrentInferences = 1,
                CpuOversubscriptionGuard = false
            });
        var scheduler = new InferenceScheduler(batchingEngine, new InferenceSchedulerOptions
        {
            EnableDynamicBatching = true,
            MaxConcurrency = 1,
            MaxBatchSize = 4,
            BatchWaitWindow = TimeSpan.FromMilliseconds(20),
            MaxQueueLength = 16,
            RequestDeadline = TimeSpan.FromSeconds(5)
        });

        // 2 行请求 → 填充率 0.5；取消一个请求 → 取消浪费计数。
        var cancelledCts = new CancellationTokenSource();
        var cancelledTask = scheduler.InferBatchAsync(new FeatureBatch
        {
            SchemaVersion = "v1",
            Values = new float[4],
            RowCount = 2,
            FeatureCount = 2,
            FeatureNames = new[] { "f0", "f1" }
        }, cancelledCts.Token).AsTask();
        cancelledCts.Cancel();

        var okResult = await scheduler.InferBatchAsync(new FeatureBatch
        {
            SchemaVersion = "v1",
            Values = new float[2],
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "f0", "f1" }
        }).ConfigureAwait(false);
        Assert.IsTrue(okResult.Succeeded);

        try
        {
            await cancelledTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 已取消的请求按预期抛出取消异常。
        }

        // ── 断言捕获的指标 ──
        var shards = capture.ValuesOf("contextcore.inference.shards_executed");
        Assert.IsTrue(shards.Count > 0, "应记录分片计数");
        Assert.AreEqual(3, shards.Sum(), "5 行分片应累计 3");

        var contention = capture.ValuesOf("contextcore.inference.session_contention");
        Assert.IsTrue(contention.Sum() >= 1, "并发竞争应至少记录 1 次");

        var fillRatios = capture.ValuesOf("contextcore.inference.batch_fill_ratio");
        Assert.IsTrue(fillRatios.Count >= 1, "动态批处理应记录填充率");
        // 2 行请求可能单独成组（填充率 0.5）或与已取消请求同组被剔除后单独成组（0.25），两者均合法。
        Assert.IsTrue(fillRatios.All(r => r > 0 && r <= 1), "填充率应在 (0, 1] 区间");

        var queueWaits = capture.ValuesOf("contextcore.inference.queue_wait.duration");
        Assert.IsTrue(queueWaits.Count >= 1, "动态批处理应记录排队等待时长");
        Assert.IsTrue(queueWaits.All(v => v >= 0), "排队等待时长不应为负");

        var cancellationWaste = capture.ValuesOf("contextcore.inference.cancellation_waste");
        Assert.IsTrue(cancellationWaste.Sum() >= 1, "已取消请求应记录取消浪费");

        // ── 延迟直方图：成功推理应记录单批延迟 ──
        var latencies = capture.ValuesOf("contextcore.inference.latency.duration");
        Assert.IsTrue(latencies.Count >= 1, "成功推理应记录延迟直方图");
        Assert.IsTrue(latencies.All(v => v >= 0), "推理延迟不应为负");

        // ── 维度断言：指标携带 model / node / ep（批次类指标额外携带 batch）──
        var fillWithTags = capture.TaggedValuesOf("contextcore.inference.batch_fill_ratio");
        Assert.IsTrue(fillWithTags.Count >= 1, "填充率指标应携带维度标签");
        var fillTags = fillWithTags[0].Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.AreEqual("1.0.0", fillTags["model"], "model 维度应为引擎模型版本");
        Assert.AreEqual(Environment.MachineName, fillTags["node"], "node 维度应为当前节点标识");
        Assert.AreEqual("CPU", fillTags["ep"], "指标应携带执行提供方（EP）维度");
        Assert.IsTrue(fillTags.TryGetValue("batch", out var fillBatch) && Convert.ToInt32(fillBatch) > 0,
            "填充率指标应携带正数 batch 行数维度");

        var contentionWithTags = capture.TaggedValuesOf("contextcore.inference.session_contention");
        Assert.IsTrue(contentionWithTags.Count >= 1, "会话竞争指标应携带维度标签");
        var contentionTags = contentionWithTags[0].Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.AreEqual("1.0.0", contentionTags["model"], "会话竞争 model 维度应为引擎模型版本");
        Assert.AreEqual(Environment.MachineName, contentionTags["node"], "会话竞争 node 维度应为当前节点标识");
        Assert.AreEqual("CPU", contentionTags["ep"], "会话竞争指标应携带执行提供方（EP）维度");
        Assert.IsFalse(contentionTags.ContainsKey("batch"), "会话竞争指标不应携带 batch 维度");

        var latencyWithTags = capture.TaggedValuesOf("contextcore.inference.latency.duration");
        Assert.IsTrue(latencyWithTags.Count >= 1, "延迟指标应携带维度标签");
        var latencyTags = latencyWithTags[0].Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.AreEqual("1.0.0", latencyTags["model"], "延迟 model 维度应为引擎模型版本");
        Assert.AreEqual("CPU", latencyTags["ep"], "延迟指标应携带执行提供方（EP）维度");
    }

    /// <summary>
    /// 测试辅助：订阅 ContextCore.Inference.Onnx 仪表，捕获测量值。
    /// MeterListener.Start 会对已存在的 instrument 补发 InstrumentPublished（与 OTel 行为一致），
    /// 因此本捕获器对测试执行顺序不敏感。
    /// </summary>
    private sealed class InferenceMetricCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> _values = new();

        public InferenceMetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, "ContextCore.Inference.Onnx", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                _values.Enqueue((instrument.Name, value, tags.ToArray())));
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                _values.Enqueue((instrument.Name, value, tags.ToArray())));
            _listener.Start();
        }

        public IReadOnlyList<double> ValuesOf(string instrumentName)
        {
            var result = new List<double>();
            foreach (var (name, value, _) in _values)
            {
                if (string.Equals(name, instrumentName, StringComparison.Ordinal))
                {
                    result.Add(value);
                }
            }
            return result;
        }

        /// <summary>返回指定仪表的所有测量 (值, 标签) 对，供维度断言。</summary>
        public IReadOnlyList<(double Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)> TaggedValuesOf(string instrumentName)
        {
            var result = new List<(double, IReadOnlyList<KeyValuePair<string, object?>>)>();
            foreach (var (name, value, tags) in _values)
            {
                if (string.Equals(name, instrumentName, StringComparison.Ordinal))
                {
                    result.Add((value, tags));
                }
            }
            return result;
        }

        public void Dispose() => _listener.Dispose();
    }
}
