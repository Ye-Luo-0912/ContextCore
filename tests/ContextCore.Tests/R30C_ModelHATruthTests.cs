using ContextCore.Abstractions;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Inference.Onnx;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.InMemory.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContextCore.Tests;

// ===========================================================================
// R30C Model HA Truth —— 集群期望、节点实际模型与 API 报告一致
//
// 验收：任意节点重启后加载唯一 Champion；不能出现 Slot=A、Engine=B。
// 覆盖：
//   - reconcile retry/backoff：失败按指数退避重试（Base×2^n，上限 MaxDelay/MaxRetryCount）；
//   - drift 自动隔离：同 Revision 下 ContentHash 不一致 → 节点标记 Isolated（持久化）；
//   - rollout readiness：至少一节点 + 全收敛 + 零漂移 → IsRolloutReady=true；
//   - EngineGeneration 记录：已应用状态携带本地引擎代次（与 SlotRevision 分离）；
//   - Staged Handle 身份校验（PromoteStaged fail-closed）由 R29H 覆盖，此处不重复。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
[TestCategory("Model-HA-Truth")]
public sealed class R30C_ModelHATruthTests
{
    private const string SchemaVersion = "s4-schema-v1";
    private const string CalibrationVersion = "s4-cal-v1";

    // ===========================================================================
    // reconcile retry/backoff —— 指数退避纯函数
    // ===========================================================================

    [TestMethod]
    public void ComputeBackoffDelay_ZeroFailures_ReturnsPollInterval()
    {
        var options = new ModelStateReconcilerOptions { PollInterval = TimeSpan.FromSeconds(7) };
        Assert.AreEqual(TimeSpan.FromSeconds(7), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 0));
    }

    [TestMethod]
    public void ComputeBackoffDelay_DoublesExponentially()
    {
        var options = new ModelStateReconcilerOptions
        {
            BackoffBaseDelay = TimeSpan.FromSeconds(1),
            BackoffMaxDelay = TimeSpan.FromMinutes(10)
        };
        Assert.AreEqual(TimeSpan.FromSeconds(1), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 1));
        Assert.AreEqual(TimeSpan.FromSeconds(2), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 2));
        Assert.AreEqual(TimeSpan.FromSeconds(4), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 3));
        Assert.AreEqual(TimeSpan.FromSeconds(8), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 4));
    }

    [TestMethod]
    public void ComputeBackoffDelay_CappedAtMaxDelay()
    {
        var options = new ModelStateReconcilerOptions
        {
            BackoffBaseDelay = TimeSpan.FromSeconds(1),
            BackoffMaxDelay = TimeSpan.FromSeconds(5),
            MaxRetryCount = 100
        };
        // 2^(4-1) = 8s > 5s → 封顶 5s
        Assert.AreEqual(TimeSpan.FromSeconds(5), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 4));
    }

    [TestMethod]
    public void ComputeBackoffDelay_ExponentCappedAtMaxRetryCount()
    {
        var options = new ModelStateReconcilerOptions
        {
            BackoffBaseDelay = TimeSpan.FromSeconds(1),
            BackoffMaxDelay = TimeSpan.FromMinutes(10),
            MaxRetryCount = 3
        };
        // 指数在 MaxRetryCount 后封顶：连续失败 9 次仍按 2^(3-1)=4s，不继续指数增长。
        Assert.AreEqual(TimeSpan.FromSeconds(4), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 3));
        Assert.AreEqual(TimeSpan.FromSeconds(4), ModelStateReconcilerWorker.ComputeBackoffDelay(options, 9));
    }

    [TestMethod]
    public async Task Reconciler_ActivationFailure_BackoffRetriesUntilSuccess()
    {
        const string modelId = "s4-champion-v1";
        const string contentHash = "sha256:s4-champion-v1";
        var slotStore = new InMemoryClusterModelSlotStore();
        await slotStore.GetOrCreateAsync("primary");
        await slotStore.TryUpdateAsync("primary", expectedRevision: 0, modelId, contentHash, ClusterModelSlotDesiredStatus.Active, "control-plane");

        // 前 2 次 session 创建失败（模拟瞬时故障），第 3 次成功。
        var (manager, factory) = BuildActivationManager(failFirstCalls: 2, modelId);
        using var worker = CreateWorker(slotStore, manager, options: new ModelStateReconcilerOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(100),
            BackoffBaseDelay = TimeSpan.FromMilliseconds(50),
            BackoffMaxDelay = TimeSpan.FromMilliseconds(200),
            MaxRetryCount = 8
        });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var descriptor = await WaitForActiveAsync(manager, modelId, "前两次激活失败后应通过退避重试最终激活模型。");
            Assert.AreEqual(modelId, descriptor.ModelArtifactId);
            Assert.AreEqual(3, factory.CreateCallCount, "两次失败 + 一次成功 = 恰好三次 session 创建。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ===========================================================================
    // drift 自动隔离 + EngineGeneration 分离
    // ===========================================================================

    /// <summary>
    /// 验证：同 Revision 下本地引擎内容与集群期望 ContentHash 不一致（Slot=A、Engine=B 类错位）
    /// → Reconciler 将本节点标记为 Isolated（持久化），而非伪装收敛。
    /// </summary>
    [TestMethod]
    public async Task Reconciler_ContentHashDrift_IsolatesNode()
    {
        const string modelId = "s4-champion-v1";
        // 槽位期望哈希与引擎实际加载内容不一致（模拟跨节点加载了不同内容的模型）。
        var slotStore = new InMemoryClusterModelSlotStore();
        await slotStore.GetOrCreateAsync("primary");
        await slotStore.TryUpdateAsync("primary", expectedRevision: 0, modelId, "sha256:POISONED", ClusterModelSlotDesiredStatus.Active, "control-plane");

        var appliedStore = new InMemoryModelNodeAppliedStateStore();
        var (manager, _) = BuildActivationManager(modelId);
        using var worker = CreateWorker(slotStore, manager, appliedStore);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var applied = await WaitForIsolatedAsync(appliedStore, Environment.MachineName, "primary", "漂移后节点应被自动隔离。");
            Assert.IsTrue(applied.Isolated, "漂移节点必须被隔离，不得伪装为已收敛。");
            Assert.IsNotNull(applied.DriftReportedAt, "隔离应记录时间。");
            StringAssert.Contains(applied.IsolationReason, "漂移");
            // 引擎确实加载了模型（内容与期望不一致 → 隔离而非伪装收敛）。
            Assert.AreEqual(modelId, manager.ActiveDescriptor?.ModelArtifactId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 验证：已应用状态携带应用时刻本地引擎代次（EngineGeneration），
    /// 与集群槽位 Revision 分离——"Slot=A、Engine=B"错位可被审计检出。
    /// </summary>
    [TestMethod]
    public async Task Reconciler_RecordsEngineGeneration_AfterActivation()
    {
        const string modelId = "s4-champion-v1";
        const string contentHash = "sha256:s4-champion-v1";
        var slotStore = new InMemoryClusterModelSlotStore();
        await slotStore.GetOrCreateAsync("primary");
        await slotStore.TryUpdateAsync("primary", expectedRevision: 0, modelId, contentHash, ClusterModelSlotDesiredStatus.Active, "control-plane");

        var appliedStore = new InMemoryModelNodeAppliedStateStore();
        var (manager, _) = BuildActivationManager(modelId);
        using var worker = CreateWorker(slotStore, manager, appliedStore);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForActiveAsync(manager, modelId, "节点应激活 Champion 模型。");
            var applied = await WaitForAppliedAsync(appliedStore, Environment.MachineName, "primary", "应记录节点已应用状态。");
            Assert.AreEqual(1, applied.AppliedRevision);
            Assert.IsNotNull(applied.EngineGeneration, "已应用状态应记录本地引擎代次（与 SlotRevision 分离）。");
            Assert.AreEqual(manager.ActiveGeneration, applied.EngineGeneration, "记录代次与激活管理器一致。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task AppliedStateStore_FreshApply_ClearsIsolation()
    {
        var store = new InMemoryModelNodeAppliedStateStore();
        var node = Environment.MachineName;

        await store.MarkIsolatedAsync(node, "primary", "测试隔离");
        var isolated = await store.GetAsync(node, "primary");
        Assert.IsTrue(isolated!.Isolated, "隔离标记应生效。");

        await store.UpsertAsync(new ModelNodeAppliedState
        {
            NodeId = node,
            SlotName = "primary",
            AppliedRevision = 2,
            ModelArtifactId = "s4-champion-v1",
            ContentHash = "sha256:s4-champion-v1",
            AppliedAt = DateTimeOffset.UtcNow
        });
        var cleared = await store.GetAsync(node, "primary");
        Assert.IsFalse(cleared!.Isolated, "成功应用（记录反映引擎实际内容）应清除漂移隔离。");
        Assert.IsNull(cleared.IsolationReason, "隔离原因应随隔离清除。");
        Assert.IsNull(cleared.DriftReportedAt, "隔离时间应随隔离清除。");
    }

    [TestMethod]
    public async Task AppliedStateStore_MarkIsolated_NoPriorRecord_CreatesEntry()
    {
        var store = new InMemoryModelNodeAppliedStateStore();
        var result = await store.MarkIsolatedAsync("node-x", "primary", "no-prior-record");
        Assert.IsNotNull(result, "无既有记录时隔离标记也应创建（审计链完整）。");
        Assert.IsTrue(result.Isolated);
        Assert.AreEqual("node-x", result.NodeId);
        Assert.AreEqual("no-prior-record", result.IsolationReason);
    }

    // ===========================================================================
    // rollout readiness：收敛 + 零漂移才允许 Champion 全集群生效
    // ===========================================================================

    [TestMethod]
    public async Task Registry_AllConvergedNoDrift_IsRolloutReadyTrue()
    {
        var summary = await BuildSummaryAsync(
            desiredRevision: 1, "s4-champion-v1", "sha256:s4-champion-v1",
            Node(1, "s4-champion-v1", "sha256:s4-champion-v1"),
            Node(1, "s4-champion-v1", "sha256:s4-champion-v1"));

        Assert.IsTrue(summary.Converged);
        Assert.AreEqual(0, summary.DriftedNodeCount);
        Assert.IsTrue(summary.IsRolloutReady, "全部节点收敛且无漂移 → 上线就绪。");
    }

    [TestMethod]
    public async Task Registry_NodeBehind_NotRolloutReady()
    {
        var summary = await BuildSummaryAsync(
            desiredRevision: 1, "s4-champion-v1", "sha256:s4-champion-v1",
            Node(0, "s4-old", "sha256:s4-old"));

        Assert.IsFalse(summary.Converged);
        Assert.AreEqual(1, summary.NodesBehind);
        Assert.AreEqual(0, summary.DriftedNodeCount, "落后节点由 NodesBehind 统计，不算漂移。");
        Assert.IsFalse(summary.IsRolloutReady, "存在落后节点 → 未就绪。");
    }

    [TestMethod]
    public async Task Registry_IsolatedNode_NotRolloutReady_DriftedCount1()
    {
        var summary = await BuildSummaryAsync(
            desiredRevision: 1, "s4-champion-v1", "sha256:s4-champion-v1",
            Node(1, "s4-champion-v1", "sha256:s4-champion-v1", isolated: true, reason: "ContentHash 漂移"));

        Assert.IsTrue(summary.Converged, "Revision 层面已收敛，但漂移隔离使其不可用。");
        Assert.AreEqual(1, summary.DriftedNodeCount);
        Assert.IsFalse(summary.IsRolloutReady, "存在隔离节点 → 未就绪。");
    }

    [TestMethod]
    public async Task Registry_ContentMismatchSameRevision_NotRolloutReady()
    {
        var summary = await BuildSummaryAsync(
            desiredRevision: 1, "s4-champion-v1", "sha256:s4-champion-v1",
            Node(1, "s4-champion-v1", "sha256:OTHER-CONTENT"));

        Assert.AreEqual(1, summary.DriftedNodeCount, "同 Revision 下内容不一致 = 漂移。");
        Assert.IsFalse(summary.IsRolloutReady, "内容漂移 → 未就绪（Slot=A、Engine=B 不可伪装收敛）。");
    }

    [TestMethod]
    public async Task Registry_NoNodes_NotRolloutReady()
    {
        var summary = await BuildSummaryAsync(desiredRevision: 1, "s4-champion-v1", "sha256:s4-champion-v1");

        Assert.AreEqual(0, summary.NodeCount);
        Assert.IsFalse(summary.Converged);
        Assert.IsFalse(summary.IsRolloutReady, "无节点上报 → 未就绪。");
    }

    // ===========================================================================
    // 辅助方法
    // ===========================================================================

    private static ModelNodeAppliedState Node(
        long revision,
        string modelId,
        string hash,
        bool isolated = false,
        string? reason = null) => new()
    {
        NodeId = "node-" + Guid.NewGuid().ToString("N")[..8],
        SlotName = "primary",
        AppliedRevision = revision,
        ModelArtifactId = modelId,
        ContentHash = hash,
        Isolated = isolated,
        IsolationReason = reason,
        AppliedAt = DateTimeOffset.UtcNow
    };

    private static async Task<ClusterSlotAppliedSummary> BuildSummaryAsync(
        long desiredRevision,
        string? modelId,
        string? hash,
        params ModelNodeAppliedState[] nodes)
    {
        var slotStore = new InMemoryClusterModelSlotStore();
        await slotStore.GetOrCreateAsync("primary");
        if (desiredRevision > 0)
        {
            await slotStore.TryUpdateAsync(
                "primary", expectedRevision: 0, modelId, hash, ClusterModelSlotDesiredStatus.Active, "control-plane");
        }

        var appliedStore = new InMemoryModelNodeAppliedStateStore();
        foreach (var node in nodes)
        {
            await appliedStore.UpsertAsync(node);
        }

        var registry = new ClusterModelAppliedStateRegistry(slotStore, appliedStore);
        return await registry.GetSlotSummaryAsync("primary");
    }

    private static ModelStateReconcilerWorker CreateWorker(
        IClusterModelSlotStore slotStore,
        IModelActivationManager manager,
        IModelNodeAppliedStateStore? appliedStateStore = null,
        ModelStateReconcilerOptions? options = null)
    {
        var opts = options ?? new ModelStateReconcilerOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(100)
        };
        return new ModelStateReconcilerWorker(
            slotStore,
            manager,
            new TestOptionsMonitor<ModelStateReconcilerOptions>(opts),
            new ConfigurationBuilder().Build(),
            NullLogger<ModelStateReconcilerWorker>.Instance,
            appliedStateStore);
    }

    private static async Task<ModelArtifactDescriptor> WaitForActiveAsync(
        IModelActivationManager manager,
        string modelId,
        string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (manager.ActiveDescriptor is { } d && d.ModelArtifactId == modelId)
            {
                return d;
            }
            await Task.Delay(50);
        }
        Assert.Fail($"{message}（等待模型 {modelId} 激活超时）。");
        return null!;
    }

    private static async Task<ModelNodeAppliedState> WaitForAppliedAsync(
        IModelNodeAppliedStateStore store,
        string nodeId,
        string slotName,
        string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var applied = await store.GetAsync(nodeId, slotName);
            if (applied is not null)
            {
                return applied;
            }
            await Task.Delay(50);
        }
        Assert.Fail($"{message}（等待节点已应用状态写入超时）。");
        return null!;
    }

    private static async Task<ModelNodeAppliedState> WaitForIsolatedAsync(
        IModelNodeAppliedStateStore store,
        string nodeId,
        string slotName,
        string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var applied = await store.GetAsync(nodeId, slotName);
            if (applied is { Isolated: true })
            {
                return applied;
            }
            await Task.Delay(50);
        }
        Assert.Fail($"{message}（等待漂移隔离标记超时）。");
        return null!;
    }

    private static (ModelActivationManager Manager, TrackingSessionFactory Factory) BuildActivationManager(
        params string[] modelIds)
        => BuildActivationManager(failFirstCalls: 0, modelIds);

    private static (ModelActivationManager Manager, TrackingSessionFactory Factory) BuildActivationManager(
        int failFirstCalls,
        params string[] modelIds)
    {
        var registry = new InMemoryModelArtifactRegistry();
        var cal = new PlattCalibrationService();
        foreach (var id in modelIds)
        {
            registry.RegisterAsync(MakeDescriptor(id)).GetAwaiter().GetResult();
            cal.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: id, version: CalibrationVersion);
        }

        var session = new MockOnnxInferenceSession(
            modelIds[0], "1.0.0", "sha256:" + modelIds[0],
            new[] { new InferenceOutput { Score = 0.5, Confidence = 0.9 } });
        var factory = new TrackingSessionFactory(session, failFirstCalls);
        var fallback = new DeterministicBatchInferenceEngine();
        var manager = new ModelActivationManager(
            registry,
            new DefaultCalibrationValidator(),
            BuildFeatureRegistry(),
            factory,
            fallback,
            cal);
        return (manager, factory);
    }

    private static DefaultFeatureRegistry BuildFeatureRegistry()
    {
        var registry = new DefaultFeatureRegistry();
        registry.Register(new FeatureSchema
        {
            Version = SchemaVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Features = new[]
            {
                new FeatureDefinition { Name = "lexical_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "semantic_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" }
            }
        });
        return registry;
    }

    private static ModelArtifactDescriptor MakeDescriptor(string artifactId) => new()
    {
        ModelArtifactId = artifactId,
        ModelName = artifactId,
        ModelVersion = "1.0.0",
        FeatureSchemaVersion = SchemaVersion,
        CalibrationVersion = CalibrationVersion,
        EngineKind = InferenceEngineKind.RealModel,
        ContentHash = "sha256:" + artifactId,
        ArtifactPath = "/path/to/" + artifactId + ".onnx",
        RegisteredAt = DateTimeOffset.UtcNow
    };

    private sealed class TrackingSessionFactory : IOnnxInferenceSessionFactory
    {
        private readonly IOnnxInferenceSession _session;
        private readonly int _failFirstCalls;

        public TrackingSessionFactory(IOnnxInferenceSession session, int failFirstCalls = 0)
        {
            _session = session;
            _failFirstCalls = failFirstCalls;
        }

        public int CreateCallCount { get; private set; }

        public ValueTask<IOnnxInferenceSession> CreateAsync(
            OnnxInferenceEngineOptions options,
            ModelArtifactDescriptor? descriptor = null,
            CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            if (CreateCallCount <= _failFirstCalls)
            {
                throw new InvalidOperationException($"激活失败（第 {CreateCallCount} 次，模拟瞬时故障）。");
            }
            return ValueTask.FromResult(_session);
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        public TestOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
