using ContextCore.Abstractions;
using ContextCore.Storage.InMemory.Stores;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Tests;

// ===========================================================================
// Model Node Applied State（P0-8）—— 节点已应用状态存储 + 迁移 SQL
//
// 覆盖范围：
//   InMemoryModelNodeAppliedStateStore：Upsert CAS（仅当新 AppliedRevision ≥ 已存时覆盖）；
//   Postgres 迁移 SQL：model_node_applied_state 表（PK node_id+slot_name）；
//       cluster_model_slots 的 desired_status CHECK 约束（仅 'Inactive'/'Active' 合法）；
//   ClusterModelSlotDesiredStatus 枚举：InMemory store 读写往返。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Model-HA")]
public sealed class R29H_ModelNodeAppliedStateTests
{
    private const string NodeId = "node-test-1";

    [TestMethod]
    public async Task InMemoryStore_UpsertNew_ThenGet_ReturnsRecord()
    {
        var store = new InMemoryModelNodeAppliedStateStore();

        var missing = await store.GetAsync(NodeId, "primary");
        Assert.IsNull(missing, "无记录时应返回 null。");

        var state = MakeState(NodeId, "primary", appliedRevision: 1, modelId: "model-a", contentHash: "sha256:a");
        await store.UpsertAsync(state);

        var stored = await GetRequiredAsync(store, NodeId, "primary");
        Assert.AreEqual(1, stored.AppliedRevision);
        Assert.AreEqual("model-a", stored.ModelArtifactId);
        Assert.AreEqual("sha256:a", stored.ContentHash);
    }

    [TestMethod]
    public async Task InMemoryStore_UpsertHigherRevision_Overwrites()
    {
        var store = new InMemoryModelNodeAppliedStateStore();
        await store.UpsertAsync(MakeState(NodeId, "primary", 1, "model-a", "sha256:a"));

        var updated = await store.UpsertAsync(MakeState(NodeId, "primary", 2, "model-b", "sha256:b"));

        Assert.AreEqual(2, updated.AppliedRevision, "更高 Revision 应覆盖旧记录。");
        var stored = await GetRequiredAsync(store, NodeId, "primary");
        Assert.AreEqual(2, stored.AppliedRevision);
        Assert.AreEqual("model-b", stored.ModelArtifactId);
    }

    [TestMethod]
    public async Task InMemoryStore_UpsertLowerRevision_KeepsExisting()
    {
        var store = new InMemoryModelNodeAppliedStateStore();
        await store.UpsertAsync(MakeState(NodeId, "primary", 2, "model-b", "sha256:b"));

        var rejected = await store.UpsertAsync(MakeState(NodeId, "primary", 1, "model-a", "sha256:a"));

        Assert.AreEqual(2, rejected.AppliedRevision, "陈旧节点回写（更低 Revision）应被 CAS 拒绝并返回已存记录。");
        var stored = await GetRequiredAsync(store, NodeId, "primary");
        Assert.AreEqual(2, stored.AppliedRevision);
        Assert.AreEqual("model-b", stored.ModelArtifactId);
    }

    [TestMethod]
    public async Task InMemoryStore_DifferentSlot_IsIsolated()
    {
        var store = new InMemoryModelNodeAppliedStateStore();
        await store.UpsertAsync(MakeState(NodeId, "primary", 1, "model-a", "sha256:a"));

        var otherSlot = await store.GetAsync(NodeId, "secondary");

        Assert.IsNull(otherSlot, "不同 slot 的记录应相互隔离。");
        var primary = await GetRequiredAsync(store, NodeId, "primary");
        Assert.AreEqual("model-a", primary.ModelArtifactId);
    }

    [TestMethod]
    public void MigrationSql_IncludesModelNodeAppliedStateTable()
    {
        var sql = BuildSql();

        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_model_node_applied_state");
        StringAssert.Contains(sql, "node_id text NOT NULL");
        StringAssert.Contains(sql, "slot_name text NOT NULL DEFAULT 'primary'");
        StringAssert.Contains(sql, "applied_revision bigint NOT NULL");
        StringAssert.Contains(sql, "PRIMARY KEY (node_id, slot_name)");
    }

    [TestMethod]
    public void MigrationSql_ClusterModelSlot_EnforcesDesiredStatusCheck()
    {
        var sql = BuildSql();

        // 期望状态仅允许 'Inactive' / 'Active'，防止脏数据写入非法期望状态。
        StringAssert.Contains(sql, "CHECK (desired_status IN ('Inactive', 'Active'))");
        StringAssert.Contains(sql, "cc_cluster_model_slots_desired_status_check");
        StringAssert.Contains(sql, "desired_status text NOT NULL DEFAULT 'Inactive'");
    }

    [TestMethod]
    public void MigrationSql_WithSchema_UsesSchemaQualifiedConstraint()
    {
        var sql = PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            TablePrefix = "cc_",
            SchemaName = "contextcore_mna",
            EnablePgVectorExtension = false
        });

        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS contextcore_mna.cc_model_node_applied_state");
        StringAssert.Contains(sql, "cc_cluster_model_slots_desired_status_check");
    }

    [TestMethod]
    public async Task InMemoryClusterModelSlotStore_EnumDesiredStatus_Roundtrips()
    {
        var store = new InMemoryClusterModelSlotStore();
        await store.GetOrCreateAsync("primary");

        var active = await store.TryUpdateAsync(
            "primary", 0, "model-a", "sha256:a", ClusterModelSlotDesiredStatus.Active, "control-plane");
        Assert.IsNotNull(active);
        Assert.AreEqual(ClusterModelSlotDesiredStatus.Active, active.DesiredStatus);

        var inactive = await store.TryUpdateAsync(
            "primary", 1, null, null, ClusterModelSlotDesiredStatus.Inactive, "control-plane");
        Assert.IsNotNull(inactive);
        Assert.AreEqual(ClusterModelSlotDesiredStatus.Inactive, inactive.DesiredStatus);
    }

    private static string BuildSql() => PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
    {
        ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
        TablePrefix = "cc_",
        EnablePgVectorExtension = true
    });

    private static ModelNodeAppliedState MakeState(
        string nodeId,
        string slotName,
        long appliedRevision,
        string? modelId,
        string? contentHash) => new()
    {
        NodeId = nodeId,
        SlotName = slotName,
        AppliedRevision = appliedRevision,
        ModelArtifactId = modelId,
        ContentHash = contentHash,
        AppliedAt = DateTimeOffset.UtcNow
    };

    private static async Task<ModelNodeAppliedState> GetRequiredAsync(
        IModelNodeAppliedStateStore store,
        string nodeId,
        string slotName)
    {
        var state = await store.GetAsync(nodeId, slotName);
        Assert.IsNotNull(state, "应存在节点已应用状态记录。");
        return state!;
    }
}
