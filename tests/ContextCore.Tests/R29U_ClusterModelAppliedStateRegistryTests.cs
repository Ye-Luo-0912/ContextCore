using ContextCore.Abstractions;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

// ===========================================================================
// Cluster Model Applied-State Registry —— 集群模型已应用状态注册表验收测试
//
// 覆盖范围：
//   1. IModelNodeAppliedStateStore.ListBySlotAsync（InMemory）：仅返回目标槽位、
//      按 NodeId 字典序排序、无记录/空槽位返回空列表；
//   2. 注册表 GetSlotSummaryAsync 聚合：期望状态读取（含槽位未初始化默认值）、
//      收敛判定（全部节点 AppliedRevision == DesiredRevision）、落后节点计数、
//      内容哈希冲突计数、无节点时的默认摘要；
//   3. 注册表 ListNodeStatesAsync：IsCurrent / IsBehind 标记、NodeId 排序。
//
// 不连接真实数据库：注册表只依赖 IClusterModelSlotStore + IModelNodeAppliedStateStore
// 接口（任何存储实现均可），测试用 InMemory store + 固定槽位 fake 验证聚合语义；
// Postgres 侧 ListBySlotAsync 的 SQL 路径由集成测试（ContextCore.IntegrationTests）覆盖。
// ===========================================================================

[TestClass]
[TestCategory("Storage")]
[TestCategory("R29")]
public sealed class R29U_ClusterModelAppliedStateRegistryTests
{
    private const string SlotName = "primary";

    // =========================================================================
    // Part 1: IModelNodeAppliedStateStore.ListBySlotAsync（InMemory）
    // =========================================================================

    [TestMethod]
    public async Task InMemoryListBySlot_ReturnsOnlyMatchingSlot_OrderedByNodeId()
    {
        var store = new InMemoryModelNodeAppliedStateStore();
        await store.UpsertAsync(Applied("node-c", 1, "model-a", "sha256:c"));
        await store.UpsertAsync(Applied("node-a", 1, "model-a", "sha256:a"));
        await store.UpsertAsync(Applied("node-b", 2, "model-b", "sha256:b"));
        await store.UpsertAsync(Applied("node-d", 1, "model-a", "sha256:d", slotName: "secondary"));

        var entries = await store.ListBySlotAsync(SlotName);

        Assert.AreEqual(3, entries.Count, "应只返回目标槽位 'primary' 的记录。");
        Assert.AreEqual("node-a", entries[0].NodeId);
        Assert.AreEqual("node-b", entries[1].NodeId);
        Assert.AreEqual("node-c", entries[2].NodeId);
    }

    [TestMethod]
    public async Task InMemoryListBySlot_EmptyWhenNoRecords()
    {
        var store = new InMemoryModelNodeAppliedStateStore();

        var entries = await store.ListBySlotAsync(SlotName);

        Assert.IsNotNull(entries);
        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task InMemoryListBySlot_BlankSlot_ReturnsEmpty()
    {
        var store = new InMemoryModelNodeAppliedStateStore();
        await store.UpsertAsync(Applied("node-a", 1, "model-a", "sha256:a"));

        var entries = await store.ListBySlotAsync("  ");

        Assert.AreEqual(0, entries.Count, "空白槽位名应返回空列表而非抛异常。");
    }

    // =========================================================================
    // Part 2: 注册表摘要聚合（GetSlotSummaryAsync）
    // =========================================================================

    [TestMethod]
    public async Task Registry_Summary_NoNodes_ReportsZeroAndNotConverged()
    {
        var (registry, _, _) = CreateRegistry(new FixedSlotStore(
            Slot(SlotName, revision: 5, "model-a", "sha256:a", ClusterModelSlotDesiredStatus.Active)));

        var summary = await registry.GetSlotSummaryAsync(SlotName);

        Assert.AreEqual(5, summary.DesiredRevision);
        Assert.AreEqual("model-a", summary.DesiredModelArtifactId);
        Assert.AreEqual(0, summary.NodeCount);
        Assert.IsFalse(summary.Converged, "无节点上报时不应判定为已收敛。");
        Assert.AreEqual(0, summary.MinAppliedRevision);
        Assert.AreEqual(0, summary.MaxAppliedRevision);
        Assert.AreEqual(0, summary.NodesBehind);
        Assert.AreEqual(0, summary.ContentHashConflictCount);
    }

    [TestMethod]
    public async Task Registry_Summary_AllNodesOnDesiredRevision_Converged()
    {
        var (registry, appliedStore, _) = CreateRegistry(new FixedSlotStore(
            Slot(SlotName, revision: 5, "model-a", "sha256:a", ClusterModelSlotDesiredStatus.Active)));
        await appliedStore.UpsertAsync(Applied("node-a", 5, "model-a", "sha256:a"));
        await appliedStore.UpsertAsync(Applied("node-b", 5, "model-a", "sha256:a"));

        var summary = await registry.GetSlotSummaryAsync(SlotName);

        Assert.AreEqual(2, summary.NodeCount);
        Assert.IsTrue(summary.Converged, "全部节点 AppliedRevision == DesiredRevision 应判定为已收敛。");
        Assert.AreEqual(0, summary.NodesBehind);
        Assert.AreEqual(5, summary.MinAppliedRevision);
        Assert.AreEqual(5, summary.MaxAppliedRevision);
        Assert.AreEqual(0, summary.ContentHashConflictCount);
        Assert.IsNotNull(summary.LatestAppliedAtUtc);
    }

    [TestMethod]
    public async Task Registry_Summary_NodeBehind_ReportsBehindCount()
    {
        var (registry, appliedStore, _) = CreateRegistry(new FixedSlotStore(
            Slot(SlotName, revision: 5, "model-a", "sha256:a", ClusterModelSlotDesiredStatus.Active)));
        await appliedStore.UpsertAsync(Applied("node-a", 5, "model-a", "sha256:a"));
        await appliedStore.UpsertAsync(Applied("node-b", 3, "model-a", "sha256:a"));

        var summary = await registry.GetSlotSummaryAsync(SlotName);

        Assert.IsFalse(summary.Converged, "存在落后节点时不应判定为已收敛。");
        Assert.AreEqual(1, summary.NodesBehind);
        Assert.AreEqual(3, summary.MinAppliedRevision);
        Assert.AreEqual(5, summary.MaxAppliedRevision);
    }

    [TestMethod]
    public async Task Registry_Summary_ContentHashConflict_ReportsConflictCount()
    {
        var (registry, appliedStore, _) = CreateRegistry(new FixedSlotStore(
            Slot(SlotName, revision: 5, "model-a", "sha256:expected", ClusterModelSlotDesiredStatus.Active)));
        await appliedStore.UpsertAsync(Applied("node-a", 5, "model-a", "sha256:expected"));
        await appliedStore.UpsertAsync(Applied("node-b", 5, "model-a", "sha256:drifted"));
        await appliedStore.UpsertAsync(Applied("node-c", 5, "model-a", "sha256:drifted"));

        var summary = await registry.GetSlotSummaryAsync(SlotName);

        Assert.AreEqual(1, summary.ContentHashConflictCount, "两组不同内容哈希（组数 2 - 1）应报告冲突数 1。");
        Assert.IsTrue(summary.Converged, "Revision 一致但内容冲突时，Revision 维度仍视为已收敛（冲突单独告警）。");
    }

    [TestMethod]
    public async Task Registry_Summary_SlotNotInitialized_DesiredDefaultsZero()
    {
        var (registry, _, _) = CreateRegistry(new FixedSlotStore(null));

        var summary = await registry.GetSlotSummaryAsync(SlotName);

        Assert.AreEqual(0, summary.DesiredRevision);
        Assert.AreEqual(ClusterModelSlotDesiredStatus.Inactive, summary.DesiredStatus);
        Assert.IsNull(summary.DesiredModelArtifactId);
        Assert.IsFalse(summary.Converged);
    }

    [TestMethod]
    public async Task Registry_Summary_DesiredInactive_ConvergedOnRevision()
    {
        var (registry, appliedStore, _) = CreateRegistry(new FixedSlotStore(
            Slot(SlotName, revision: 4, null, null, ClusterModelSlotDesiredStatus.Inactive)));
        await appliedStore.UpsertAsync(Applied("node-a", 4, null, null));
        await appliedStore.UpsertAsync(Applied("node-b", 4, null, null));

        var summary = await registry.GetSlotSummaryAsync(SlotName);

        Assert.IsTrue(summary.Converged, "Inactive 期望状态下所有节点应用同一 Revision 也应判定为已收敛。");
        Assert.AreEqual(ClusterModelSlotDesiredStatus.Inactive, summary.DesiredStatus);
        Assert.AreEqual(0, summary.ContentHashConflictCount);
    }

    // =========================================================================
    // Part 3: 节点条目展开（ListNodeStatesAsync）
    // =========================================================================

    [TestMethod]
    public async Task Registry_ListNodeStates_FlagsCurrentAndBehind()
    {
        var (registry, appliedStore, _) = CreateRegistry(new FixedSlotStore(
            Slot(SlotName, revision: 5, "model-a", "sha256:a", ClusterModelSlotDesiredStatus.Active)));
        await appliedStore.UpsertAsync(Applied("node-current", 5, "model-a", "sha256:a"));
        await appliedStore.UpsertAsync(Applied("node-behind", 3, "model-b", "sha256:b"));
        await appliedStore.UpsertAsync(Applied("node-drifted", 5, "model-a", "sha256:other"));

        var entries = await registry.ListNodeStatesAsync(SlotName);

        Assert.AreEqual(3, entries.Count);

        var current = entries.Single(e => e.NodeId == "node-current");
        Assert.IsTrue(current.IsCurrent);
        Assert.IsFalse(current.IsBehind);

        var behind = entries.Single(e => e.NodeId == "node-behind");
        Assert.IsFalse(behind.IsCurrent);
        Assert.IsTrue(behind.IsBehind);

        var drifted = entries.Single(e => e.NodeId == "node-drifted");
        Assert.IsFalse(drifted.IsCurrent, "Revision 一致但内容哈希与期望不符不应标记为 IsCurrent。");
        Assert.IsFalse(drifted.IsBehind);
    }

    [TestMethod]
    public async Task Registry_ListNodeStates_SortedByNodeId()
    {
        var (registry, appliedStore, _) = CreateRegistry(new FixedSlotStore(
            Slot(SlotName, revision: 1, "model-a", "sha256:a", ClusterModelSlotDesiredStatus.Active)));
        await appliedStore.UpsertAsync(Applied("node-z", 1, "model-a", "sha256:a"));
        await appliedStore.UpsertAsync(Applied("node-a", 1, "model-a", "sha256:a"));
        await appliedStore.UpsertAsync(Applied("node-m", 1, "model-a", "sha256:a"));

        var entries = await registry.ListNodeStatesAsync(SlotName);

        Assert.AreEqual("node-a", entries[0].NodeId);
        Assert.AreEqual("node-m", entries[1].NodeId);
        Assert.AreEqual("node-z", entries[2].NodeId);
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static (ClusterModelAppliedStateRegistry Registry, InMemoryModelNodeAppliedStateStore AppliedStore, FixedSlotStore SlotStore) CreateRegistry(
        FixedSlotStore slotStore)
    {
        var appliedStore = new InMemoryModelNodeAppliedStateStore();
        return (new ClusterModelAppliedStateRegistry(slotStore, appliedStore), appliedStore, slotStore);
    }

    private static ClusterModelSlot Slot(
        string slotName,
        long revision,
        string? modelId,
        string? contentHash,
        ClusterModelSlotDesiredStatus desiredStatus) => new()
    {
        SlotName = slotName,
        Revision = revision,
        ActiveModelArtifactId = modelId,
        ContentHash = contentHash,
        DesiredStatus = desiredStatus,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test"
    };

    private static ModelNodeAppliedState Applied(
        string nodeId,
        long revision,
        string? modelId,
        string? contentHash,
        string slotName = "primary",
        DateTimeOffset? appliedAt = null) => new()
    {
        NodeId = nodeId,
        SlotName = slotName,
        AppliedRevision = revision,
        ModelArtifactId = modelId,
        ContentHash = contentHash,
        AppliedAt = appliedAt ?? DateTimeOffset.UtcNow
    };

    /// <summary>固定槽位 fake：返回预置期望状态（registry 聚合逻辑测试用）。</summary>
    private sealed class FixedSlotStore : IClusterModelSlotStore
    {
        private readonly ClusterModelSlot? _slot;

        public FixedSlotStore(ClusterModelSlot? slot) => _slot = slot;

        public ValueTask<ClusterModelSlot?> GetAsync(string slotName, CancellationToken ct = default)
            => new(_slot);

        public ValueTask<ClusterModelSlot?> TryUpdateAsync(
            string slotName,
            long expectedRevision,
            string? activeModelArtifactId,
            string? contentHash,
            ClusterModelSlotDesiredStatus desiredStatus,
            string? updatedBy,
            CancellationToken ct = default)
            => new(_slot);

        public ValueTask<ClusterModelSlot> GetOrCreateAsync(string slotName, CancellationToken ct = default)
            => new(_slot ?? throw new InvalidOperationException("FixedSlotStore 无预置槽位。"));
    }
}
