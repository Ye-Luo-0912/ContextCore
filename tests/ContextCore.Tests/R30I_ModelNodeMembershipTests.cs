using ContextCore.Abstractions;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.InMemory.Stores;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// P0-15 Model Node Membership —— 节点成员资格租约验收测试
//
// 问题：Applied-State Registry 把数据库中所有节点记录视为当前节点——已下线节点记录
// 永久阻止 Converged；新启动但尚未写 Applied State 的节点不计入 NodeCount；AppliedAt
// 没有 stale cutoff；没有 node membership lease。节点被标记 Isolated 后也没有真正停止
// 接收模型流量。
//
// 修复（WP-E2）：
//   1. model_node_membership 表 + IModelNodeMembershipStore：node_id / instance_id /
//      lease_token / lease_expires_at（stale cutoff）/ last_heartbeat / serving_enabled；
//      领取/续租原子完成，被其他活跃实例持有返回 null，过期接管生成新令牌（fencing）；
//   2. ClusterModelAppliedStateRegistry：Rollout Ready 基于当前活跃成员——租约过期的
//      历史行不再阻止 Converged；新启动未写 Applied State 的成员计入 NodeCount 且视为未就绪；
//   3. Admission：节点必须是活跃成员（租约未过期）且 serving_enabled=true 才可接流量
//      （Isolated 节点由 Reconciler 置 serving_enabled=false，真正停止接流量）。
//
// 覆盖范围：
//   Part 1 — InMemoryModelNodeMembershipStore 租约语义（领取/续租/冲突/过期接管/fencing）；
//   Part 2 — 注册表按活跃成员聚合（离线节点不阻止收敛、未上报成员计入 NodeCount、
//            活跃成员漂移阻止 RolloutReady）；
//   Part 3 — 迁移契约（0007 v59→v60 + 基线 DDL + RequiredOperationalTableSuffixes）；
//   Part 4 — 迁移后 SchemaVersion（R29S 已更新为 v60，此处验证步骤注册表接线）。
//   Part 5 — Postgres 合并写集成（serving + applied state 单往返，fail-closed）。
// ===========================================================================

[TestClass]
[TestCategory("Storage")]
[TestCategory("R30")]
public sealed class R30I_ModelNodeMembershipTests
{
    private const string SlotName = "primary";

    // =========================================================================
    // Part 1: InMemoryModelNodeMembershipStore 租约语义
    // =========================================================================

    [TestMethod]
    public async Task Acquire_FirstTime_CreatesLeaseWithTokenAndExpiry()
    {
        var store = new InMemoryModelNodeMembershipStore();

        var membership = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-1", TimeSpan.FromMinutes(5), servingEnabled: true);

        Assert.IsNotNull(membership);
        Assert.AreEqual("node-a", membership.NodeGroupId);
        Assert.AreEqual("instance-1", membership.InstanceId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(membership.LeaseToken));
        Assert.IsTrue(membership.LeaseExpiresAt > DateTimeOffset.UtcNow.AddMinutes(4), "租约过期时间应约为 now + duration。");
        Assert.IsTrue(membership.ServingEnabled);
    }

    [TestMethod]
    public async Task Renew_SameInstance_KeepsToken()
    {
        var store = new InMemoryModelNodeMembershipStore();
        var first = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-1", TimeSpan.FromMinutes(5), servingEnabled: true);

        var second = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-1", TimeSpan.FromMinutes(5), servingEnabled: false);

        Assert.IsNotNull(second);
        Assert.AreEqual(first!.LeaseToken, second.LeaseToken, "同实例续租应保持令牌（不产生新令牌）。");
        Assert.IsFalse(second.ServingEnabled, "续租应刷新 serving_enabled（本地健康状态）。");
    }

    [TestMethod]
    public async Task Acquire_DifferentInstances_SameGroup_EachOwnLease()
    {
        var store = new InMemoryModelNodeMembershipStore();

        // 同一节点组的两个实例各自领取自己的成员租约（互不排斥）——修复
        // "每节点仅一个活跃实例可入集群"的部署限制。
        var first = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-1", TimeSpan.FromMinutes(5), servingEnabled: true);
        var second = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-2", TimeSpan.FromMinutes(5), servingEnabled: true);

        Assert.IsNotNull(first, "实例 1 领取自身租约成功。");
        Assert.IsNotNull(second, "同节点组另一实例 2 领取自身租约成功（不再互相排斥）。");
        Assert.AreNotEqual(first!.LeaseToken, second!.LeaseToken, "不同实例各自持有独立租约令牌。");
    }

    [TestMethod]
    public async Task Acquire_ExpiredLease_RenewGeneratesNewToken()
    {
        var store = new InMemoryModelNodeMembershipStore();
        var first = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-1", TimeSpan.Zero, servingEnabled: true);
        Assert.IsNotNull(first);

        // 零时长租约立即过期（stale cutoff）→ 同实例续租时生成新令牌（fencing 旧令牌失效）。
        var renewed = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-1", TimeSpan.FromMinutes(5), servingEnabled: true);

        Assert.IsNotNull(renewed, "过期后同实例续租应成功。");
        Assert.AreNotEqual(first!.LeaseToken, renewed.LeaseToken, "租约过期后必须生成新令牌（fencing）。");
        Assert.AreEqual("instance-1", renewed.InstanceId);
    }

    [TestMethod]
    public async Task SetServingEnabled_ValidToken_UpdatesFlag()
    {
        var store = new InMemoryModelNodeMembershipStore();
        var membership = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-1", TimeSpan.FromMinutes(5), servingEnabled: true);

        var updated = await store.SetServingEnabledAsync(
            "node-a", "instance-1", membership!.LeaseToken, servingEnabled: false);

        Assert.IsTrue(updated);
        var current = await store.GetAsync("node-a", "instance-1");
        Assert.IsNotNull(current);
        Assert.IsFalse(current.ServingEnabled, "有效令牌 + 未过期租约应能翻转 serving 开关。");
    }

    [TestMethod]
    public async Task SetServingEnabled_WrongToken_ReturnsFalse()
    {
        var store = new InMemoryModelNodeMembershipStore();
        var membership = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-1", TimeSpan.FromMinutes(5), servingEnabled: true);

        // 旧令牌（被接管后失效）或伪造令牌 → 拒绝，防止过期持有者篡改状态。
        var updated = await store.SetServingEnabledAsync(
            "node-a", "instance-1", "forged-token", servingEnabled: false);

        Assert.IsFalse(updated, "lease_token 不匹配时不得更新 serving 开关。");
        var current = await store.GetAsync("node-a", "instance-1");
        Assert.IsTrue(current!.ServingEnabled, "失败写入不得改变既有状态。");
    }

    [TestMethod]
    public async Task SetServingEnabled_ExpiredLease_ReturnsFalse()
    {
        var store = new InMemoryModelNodeMembershipStore();
        var membership = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-1", TimeSpan.Zero, servingEnabled: true);

        // 令牌正确但租约已过期 → 拒绝（过期持有者无写入权）。
        var updated = await store.SetServingEnabledAsync(
            "node-a", "instance-1", membership!.LeaseToken, servingEnabled: false);

        Assert.IsFalse(updated, "租约过期后即使令牌匹配也不得更新。");
    }

    [TestMethod]
    public async Task SetServingAndAppliedState_ValidLease_WritesBoth()
    {
        var appliedStore = new InMemoryModelNodeAppliedStateStore();
        var store = new InMemoryModelNodeMembershipStore(appliedStore);
        var membership = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-1", TimeSpan.FromMinutes(5), servingEnabled: false);

        var updated = await store.SetServingAndAppliedStateAsync(
            "node-a", "instance-1", membership!.LeaseToken, servingEnabled: true,
            new ModelNodeAppliedState
            {
                NodeGroupId = "node-a",
                InstanceId = "instance-1",
                SlotName = SlotName,
                AppliedRevision = 7,
                ModelArtifactId = "model-a",
                ContentHash = "sha256:a",
                EngineGeneration = 3,
                AppliedAt = DateTimeOffset.UtcNow
            });

        Assert.IsTrue(updated, "有效令牌 + 未过期租约应合并写成功。");
        var current = await store.GetAsync("node-a", "instance-1");
        Assert.IsTrue(current!.ServingEnabled, "serving 开关应被置 true。");
        var applied = await appliedStore.GetAsync("node-a", "instance-1", SlotName);
        Assert.IsNotNull(applied, "已应用状态应一并写入。");
        Assert.AreEqual(7, applied!.AppliedRevision);
        Assert.AreEqual("model-a", applied.ModelArtifactId);
    }

    [TestMethod]
    public async Task SetServingAndAppliedState_WrongToken_ReturnsFalse_WritesNothing()
    {
        var appliedStore = new InMemoryModelNodeAppliedStateStore();
        var store = new InMemoryModelNodeMembershipStore(appliedStore);
        var membership = await store.TryAcquireOrRenewLeaseAsync(
            "node-a", "instance-1", TimeSpan.FromMinutes(5), servingEnabled: false);

        var updated = await store.SetServingAndAppliedStateAsync(
            "node-a", "instance-1", "forged-token", servingEnabled: true,
            new ModelNodeAppliedState
            {
                NodeGroupId = "node-a",
                InstanceId = "instance-1",
                SlotName = SlotName,
                AppliedRevision = 7,
                AppliedAt = DateTimeOffset.UtcNow
            });

        Assert.IsFalse(updated, "lease_token 不匹配时合并写必须整体失败（fail-closed）。");
        var current = await store.GetAsync("node-a", "instance-1");
        Assert.IsFalse(current!.ServingEnabled, "失败写入不得改变既有 serving 状态。");
        Assert.IsNull(await appliedStore.GetAsync("node-a", "instance-1", SlotName), "serving 更新失败时已应用状态不得写入。");
    }

    [TestMethod]
    public async Task GetActiveMembers_ExcludesExpired_OrdersByNodeId()
    {
        var store = new InMemoryModelNodeMembershipStore();
        await store.TryAcquireOrRenewLeaseAsync("node-c", "i1", TimeSpan.FromMinutes(5), servingEnabled: true);
        await store.TryAcquireOrRenewLeaseAsync("node-b", "i1", TimeSpan.Zero, servingEnabled: true);
        await store.TryAcquireOrRenewLeaseAsync("node-a", "i1", TimeSpan.FromMinutes(5), servingEnabled: true);

        var active = await store.GetActiveMembersAsync();

        Assert.AreEqual(2, active.Count, "租约已过期的成员不得计入活跃成员。");
        Assert.AreEqual("node-a", active[0].NodeGroupId);
        Assert.AreEqual("node-c", active[1].NodeGroupId);
    }

    // =========================================================================
    // Part 2: 注册表按活跃成员聚合（Rollout Ready 基于当前活跃成员）
    // =========================================================================

    [TestMethod]
    public async Task Registry_OfflineNodeRecord_DoesNotBlockConverged()
    {
        // 三台节点都已上报（rev5），但 C 已下线（无成员租约）——收敛/就绪只算活跃成员 A、B。
        var (registry, appliedStore, _) = CreateRegistryWithMembership(
            new FixedSlotStore(Slot(SlotName, revision: 5, "model-a", "sha256:a", ClusterModelSlotDesiredStatus.Active)),
            Member("node-a"), Member("node-b"));
        await appliedStore.UpsertAsync(Applied("node-a", 5, "model-a", "sha256:a"));
        await appliedStore.UpsertAsync(Applied("node-b", 5, "model-a", "sha256:a"));
        await appliedStore.UpsertAsync(Applied("node-c", 5, "model-a", "sha256:a"));

        var summary = await registry.GetSlotSummaryAsync(SlotName);

        Assert.AreEqual(2, summary.NodeCount, "NodeCount 应基于活跃成员数，而非历史 Applied State 行数。");
        Assert.IsTrue(summary.Converged, "已下线节点的历史记录不应永久阻止 Converged。");
        Assert.AreEqual(0, summary.NodesBehind);
        Assert.IsTrue(summary.IsRolloutReady);
    }

    [TestMethod]
    public async Task Registry_MemberWithoutAppliedState_CountsTowardNodeCount_BlocksReady()
    {
        // 新节点 B 已加入集群（有成员租约）但尚未写 Applied State → 计入 NodeCount，
        // 视为未就绪 → 不收敛、RolloutReady=false（修复"新启动节点不计入 NodeCount"）。
        var (registry, appliedStore, _) = CreateRegistryWithMembership(
            new FixedSlotStore(Slot(SlotName, revision: 5, "model-a", "sha256:a", ClusterModelSlotDesiredStatus.Active)),
            Member("node-a"), Member("node-b"));
        await appliedStore.UpsertAsync(Applied("node-a", 5, "model-a", "sha256:a"));

        var summary = await registry.GetSlotSummaryAsync(SlotName);

        Assert.AreEqual(2, summary.NodeCount, "尚未写 Applied State 的活跃成员也应计入 NodeCount。");
        Assert.IsFalse(summary.Converged, "存在未上报应用的活跃成员时不应收敛。");
        Assert.AreEqual(1, summary.NodesBehind, "无记录的活跃成员视为落后/未就绪。");
        Assert.IsFalse(summary.IsRolloutReady);
    }

    [TestMethod]
    public async Task Registry_ActiveMemberDrifted_BlocksRolloutReady()
    {
        // 活跃成员 B 被隔离（漂移）→ DriftedNodeCount=1 → RolloutReady=false。
        var (registry, appliedStore, _) = CreateRegistryWithMembership(
            new FixedSlotStore(Slot(SlotName, revision: 5, "model-a", "sha256:a", ClusterModelSlotDesiredStatus.Active)),
            Member("node-a"), Member("node-b"));
        await appliedStore.UpsertAsync(Applied("node-a", 5, "model-a", "sha256:a"));
        await appliedStore.UpsertAsync(Applied("node-b", 5, "model-a", "sha256:a", isolated: true));

        var summary = await registry.GetSlotSummaryAsync(SlotName);

        Assert.IsTrue(summary.Converged, "Revision 维度仍收敛（漂移由隔离单独统计）。");
        Assert.AreEqual(1, summary.DriftedNodeCount);
        Assert.IsFalse(summary.IsRolloutReady, "存在漂移/隔离的活跃成员时不得判定为上线就绪。");
    }

    [TestMethod]
    public async Task Registry_OfflineDriftedRecord_DoesNotCountAsDrifted()
    {
        // 已下线节点 C 的历史漂移记录不再计入 DriftedNodeCount（只评估当前活跃成员）。
        var (registry, appliedStore, _) = CreateRegistryWithMembership(
            new FixedSlotStore(Slot(SlotName, revision: 5, "model-a", "sha256:a", ClusterModelSlotDesiredStatus.Active)),
            Member("node-a"), Member("node-b"));
        await appliedStore.UpsertAsync(Applied("node-a", 5, "model-a", "sha256:a"));
        await appliedStore.UpsertAsync(Applied("node-b", 5, "model-a", "sha256:a"));
        await appliedStore.UpsertAsync(Applied("node-c", 5, "model-a", "sha256:drifted", isolated: true));

        var summary = await registry.GetSlotSummaryAsync(SlotName);

        Assert.AreEqual(0, summary.DriftedNodeCount, "已下线节点的漂移记录不应阻止当前集群就绪。");
        Assert.IsTrue(summary.IsRolloutReady);
    }

    // =========================================================================
    // Part 3: 迁移契约（v59 → v60 model_node_membership）
    // =========================================================================

    [TestMethod]
    public void Migration_ModelNodeMembership_DeclaresV59ToV60()
    {
        var step = PostgresMigrationStepRegistry.Steps
            .OfType<PostgresMigrationModelNodeMembership>()
            .Single();

        Assert.AreEqual("0007_model_node_membership", step.MigrationId);
        Assert.AreEqual("cc-schema-v59", step.FromSchemaVersion);
        Assert.AreEqual("cc-schema-v60", step.ToSchemaVersion);
        CollectionAssert.AreEqual(
            new[] { PostgresMigrationStage.Online },
            step.Stages.ToArray(),
            "v59→v60 应为单 Online 阶段（CREATE TABLE IF NOT EXISTS，幂等）。");
    }

    [TestMethod]
    public void Baseline_ContainsModelNodeMembershipTable()
    {
        var sql = PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false,
            TablePrefix = "cc_"
        });

        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_model_node_membership (",
            "基线 DDL 应含 model_node_membership 建表语句。");
        StringAssert.Contains(sql, "node_group_id text NOT NULL", "基线 DDL 应含 node_group_id 列（节点组稳定标识）。");
        StringAssert.Contains(sql, "instance_id text NOT NULL", "基线 DDL 应含 instance_id 列（进程实例）。");
        StringAssert.Contains(sql, "lease_token text NOT NULL", "基线 DDL 应含 lease_token 列（fencing）。");
        StringAssert.Contains(sql, "lease_expires_at timestamptz NOT NULL", "基线 DDL 应含 lease_expires_at 列（stale cutoff）。");
        StringAssert.Contains(sql, "last_heartbeat timestamptz NOT NULL", "基线 DDL 应含 last_heartbeat 列。");
        StringAssert.Contains(sql, "serving_enabled boolean NOT NULL DEFAULT true", "基线 DDL 应含 serving_enabled 列（Isolated 停止接流量）。");
        StringAssert.Contains(sql, "PRIMARY KEY (node_group_id, instance_id)",
            "基线 DDL 主键应为 (node_group_id, instance_id)——同一节点组可驻留多个实例。");
    }

    [TestMethod]
    public void RequiredOperationalTableSuffixes_ContainsModelNodeMembership()
    {
        CollectionAssert.Contains(
            PostgresMigrationRunner.RequiredOperationalTableSuffixes.ToList(),
            "model_node_membership");
    }

    [TestMethod]
    public void Migration_ModelNodeIdentitySplit_DeclaresV64ToV65()
    {
        var step = PostgresMigrationStepRegistry.Steps
            .OfType<PostgresMigrationModelNodeIdentitySplit>()
            .Single();

        Assert.AreEqual("0012_model_node_identity_split", step.MigrationId);
        Assert.AreEqual("cc-schema-v64", step.FromSchemaVersion);
        Assert.AreEqual("cc-schema-v65", step.ToSchemaVersion);
        CollectionAssert.AreEqual(
            new[]
            {
                PostgresMigrationStage.Online,
                PostgresMigrationStage.Backfill,
                PostgresMigrationStage.ConstraintValidate
            },
            step.Stages.ToArray(),
            "v64→v65 应为 列改名/加列 + 数据回填 + 主键切换 三阶段（幂等重入）。");
    }

    // =========================================================================
    // Part 5: Postgres 合并写集成（SetServingAndAppliedStateAsync 单往返）
    // =========================================================================

    /// <summary>
    /// 验证：Postgres 合并写单次往返完成 serving 开关 + 已应用状态两处写入；
    /// 租约失效（令牌不匹配）时整体失败（fail-closed，两处均不写入）。
    /// </summary>
    [TestMethod]
    public async Task Postgres_SetServingAndAppliedState_ValidLease_WritesBoth()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres 成员合并写测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, membershipStore, appliedStore) = await ResolveStoresAsync(container, "mb_hb_");
            await using (provider)
            {
                var lease = await membershipStore.TryAcquireOrRenewLeaseAsync(
                    "node-pg", "instance-1", TimeSpan.FromMinutes(5), servingEnabled: false);
                Assert.IsNotNull(lease, "领取成员租约成功。");

                var updated = await membershipStore.SetServingAndAppliedStateAsync(
                    "node-pg", "instance-1", lease!.LeaseToken, servingEnabled: true,
                    new ModelNodeAppliedState
                    {
                        NodeGroupId = "node-pg",
                        InstanceId = "instance-1",
                        SlotName = SlotName,
                        AppliedRevision = 9,
                        ModelArtifactId = "model-pg",
                        ContentHash = "sha256:pg",
                        EngineGeneration = 4,
                        AppliedAt = DateTimeOffset.UtcNow
                    });

                Assert.IsTrue(updated, "有效令牌 + 未过期租约应合并写成功。");
                var current = await membershipStore.GetAsync("node-pg", "instance-1");
                Assert.IsTrue(current!.ServingEnabled, "serving 开关应被置 true。");
                var applied = await appliedStore.GetAsync("node-pg", "instance-1", SlotName);
                Assert.IsNotNull(applied, "已应用状态应一并写入。");
                Assert.AreEqual(9, applied!.AppliedRevision);
                Assert.AreEqual("model-pg", applied.ModelArtifactId);
            }
        }
    }

    /// <summary>验证：令牌不匹配时合并写整体失败，已应用状态不得写入（fail-closed）。</summary>
    [TestMethod]
    public async Task Postgres_SetServingAndAppliedState_WrongToken_WritesNothing()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres 成员合并写测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, membershipStore, appliedStore) = await ResolveStoresAsync(container, "mb_hb2_");
            await using (provider)
            {
                var lease = await membershipStore.TryAcquireOrRenewLeaseAsync(
                    "node-pg", "instance-1", TimeSpan.FromMinutes(5), servingEnabled: false);
                Assert.IsNotNull(lease);

                var updated = await membershipStore.SetServingAndAppliedStateAsync(
                    "node-pg", "instance-1", "forged-token", servingEnabled: true,
                    new ModelNodeAppliedState
                    {
                        NodeGroupId = "node-pg",
                        InstanceId = "instance-1",
                        SlotName = SlotName,
                        AppliedRevision = 9,
                        AppliedAt = DateTimeOffset.UtcNow
                    });

                Assert.IsFalse(updated, "令牌不匹配时合并写必须整体失败。");
                var current = await membershipStore.GetAsync("node-pg", "instance-1");
                Assert.IsFalse(current!.ServingEnabled, "失败写入不得改变既有 serving 状态。");
                Assert.IsNull(await appliedStore.GetAsync("node-pg", "instance-1", SlotName),
                    "serving 更新失败时已应用状态不得写入。");
            }
        }
    }

    /// <summary>最小 DI 注册（AddContextCorePostgresStorage），解析成员与已应用状态两个 store。</summary>
    private static async Task<(ServiceProvider Provider, IModelNodeMembershipStore Membership, IModelNodeAppliedStateStore Applied)> ResolveStoresAsync(
        PostgreSqlContainer container, string tablePrefix)
    {
        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = container.GetConnectionString(),
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = tablePrefix
        });
        var provider = services.BuildServiceProvider();
        var membership = provider.GetRequiredService<IModelNodeMembershipStore>();
        var applied = provider.GetRequiredService<IModelNodeAppliedStateStore>();
        Assert.IsInstanceOfType(membership, typeof(PostgresModelNodeMembershipStore),
            "AddContextCorePostgresStorage 必须把 IModelNodeMembershipStore 解析为 Postgres 实现。");
        Assert.IsInstanceOfType(applied, typeof(PostgresModelNodeAppliedStateStore),
            "AddContextCorePostgresStorage 必须把 IModelNodeAppliedStateStore 解析为 Postgres 实现。");
        return (provider, membership, applied);
    }

    private static async Task<PostgreSqlContainer?> TryStartPostgresAsync()
    {
        const string pgVectorImage = "pgvector/pgvector:pg17";
        try
        {
            var container = new PostgreSqlBuilder(pgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await container.StartAsync(cts.Token);
            return container;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[R30I_ModelNodeMembershipTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static (ClusterModelAppliedStateRegistry Registry, InMemoryModelNodeAppliedStateStore AppliedStore, FixedSlotStore SlotStore) CreateRegistryWithMembership(
        FixedSlotStore slotStore,
        params ModelNodeMembership[] members)
    {
        var appliedStore = new InMemoryModelNodeAppliedStateStore();
        var membershipStore = new FixedMembershipStore(members);
        return (new ClusterModelAppliedStateRegistry(slotStore, appliedStore, membershipStore), appliedStore, slotStore);
    }

    private static ModelNodeMembership Member(string nodeId)
        => new()
        {
            NodeGroupId = nodeId,
            InstanceId = "instance-1",
            LeaseToken = "token-" + nodeId,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            LastHeartbeat = DateTimeOffset.UtcNow,
            ServingEnabled = true
        };

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
        bool isolated = false) => new()
    {
        NodeGroupId = nodeId,
        InstanceId = "instance-1",
        SlotName = SlotName,
        AppliedRevision = revision,
        ModelArtifactId = modelId,
        ContentHash = contentHash,
        AppliedAt = DateTimeOffset.UtcNow,
        Isolated = isolated
    };

    /// <summary>固定成员集合 fake：返回预置活跃成员（模拟 store 已按租约过滤）。</summary>
    private sealed class FixedMembershipStore : IModelNodeMembershipStore
    {
        private readonly IReadOnlyList<ModelNodeMembership> _members;

        public FixedMembershipStore(IReadOnlyList<ModelNodeMembership> members) => _members = members;

        public ValueTask<ModelNodeMembership?> TryAcquireOrRenewLeaseAsync(
            string nodeGroupId,
            string instanceId,
            TimeSpan leaseDuration,
            bool servingEnabled,
            CancellationToken ct = default)
            => throw new NotImplementedException("注册表聚合测试不领取租约。");

        public ValueTask<ModelNodeMembership?> GetAsync(
            string nodeGroupId,
            string instanceId,
            CancellationToken ct = default)
            => new(_members.FirstOrDefault(m =>
                string.Equals(m.NodeGroupId, nodeGroupId, StringComparison.Ordinal)
                && string.Equals(m.InstanceId, instanceId, StringComparison.Ordinal)));

        public ValueTask<IReadOnlyList<ModelNodeMembership>> GetActiveMembersAsync(CancellationToken ct = default)
            => new(_members);

        public ValueTask<bool> SetServingEnabledAsync(
            string nodeGroupId,
            string instanceId,
            string leaseToken,
            bool servingEnabled,
            CancellationToken ct = default)
            => throw new NotImplementedException("注册表聚合测试不更新 serving。");

        public ValueTask<bool> SetServingAndAppliedStateAsync(
            string nodeGroupId,
            string instanceId,
            string leaseToken,
            bool servingEnabled,
            ModelNodeAppliedState? appliedState,
            CancellationToken ct = default)
            => throw new NotImplementedException("注册表聚合测试不更新 serving。");
    }

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
