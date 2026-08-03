using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory.Stores;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Tests;

// ===========================================================================
// 集群级 Canary Kill Switch（P0-9）—— 紧急覆盖存储 + 路由优先级 + 恢复语义
//
// 覆盖范围：
//   InMemoryCanaryEmergencyOverrideStore：set/get/clear + 活跃覆盖唯一性；
//   Postgres 迁移 SQL：canary_emergency_overrides 表 + 活跃覆盖部分唯一索引；
//   AuthoritativeRetrievalRuntime / AuthoritativePackageRuntime：活跃覆盖强制回退 V1
//     （优先级：Emergency Override > canary DB 百分比 > Cutover 配置）；
//   CanaryProgressionService.RecoverFromStoreAsync：覆盖期间强制 0% + 非 Consistent，
//     AdvanceAsync 拒绝推进。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Canary")]
public sealed class R29H_CanaryEmergencyOverrideTests
{
    private const string RunId = "run-emergency-001";

    // ---------------------------------------------------------------------------
    // InMemoryCanaryEmergencyOverrideStore
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task InMemoryStore_SetOverride_GetActive_ReturnsRecord()
    {
        var store = new InMemoryCanaryEmergencyOverrideStore();

        var missing = await store.GetActiveAsync(RunId);
        Assert.IsNull(missing, "无覆盖时应返回 null。");

        var set = await store.TrySetOverrideAsync(RunId, "v2 P95 恶化", "ops-oncall");
        Assert.IsTrue(set, "首次设置覆盖应成功。");

        var active = await GetActiveRequiredAsync(store, RunId);
        Assert.AreEqual("v2 P95 恶化", active.Reason);
        Assert.AreEqual("ops-oncall", active.OperatorName);
        Assert.IsNull(active.ClearedAt, "活跃覆盖 ClearedAt 应为 null。");
    }

    [TestMethod]
    public async Task InMemoryStore_SetOverrideTwice_SecondRejected()
    {
        var store = new InMemoryCanaryEmergencyOverrideStore();
        await store.TrySetOverrideAsync(RunId, "原因 A", "ops-a");

        var second = await store.TrySetOverrideAsync(RunId, "原因 B", "ops-b");

        Assert.IsFalse(second, "已存在活跃覆盖时第二次设置应被拒绝（不覆盖、不报错）。");
        var active = await GetActiveRequiredAsync(store, RunId);
        Assert.AreEqual("原因 A", active.Reason, "原有覆盖应保留。");
        Assert.AreEqual("ops-a", active.OperatorName);
    }

    [TestMethod]
    public async Task InMemoryStore_ClearOverride_ThenSetAgain_ReplacesCleared()
    {
        var store = new InMemoryCanaryEmergencyOverrideStore();
        await store.TrySetOverrideAsync(RunId, "原因 A", "ops-a");

        var cleared = await store.TryClearOverrideAsync(RunId, "ops-a");
        Assert.IsTrue(cleared, "存在活跃覆盖时清除应成功。");
        Assert.IsNull(await store.GetActiveAsync(RunId), "清除后 GetActive 应返回 null。");

        var clearedAgain = await store.TryClearOverrideAsync(RunId, "ops-a");
        Assert.IsFalse(clearedAgain, "无活跃覆盖时再次清除应返回 false。");

        var replaced = await store.TrySetOverrideAsync(RunId, "原因 B", "ops-b");
        Assert.IsTrue(replaced, "已清除的历史覆盖应可被新覆盖替换。");
        var active = await GetActiveRequiredAsync(store, RunId);
        Assert.AreEqual("原因 B", active.Reason);
        Assert.AreEqual("ops-b", active.OperatorName);
    }

    [TestMethod]
    public async Task InMemoryStore_GetActiveOverrides_ReturnsOnlyActive()
    {
        var store = new InMemoryCanaryEmergencyOverrideStore();
        await store.TrySetOverrideAsync("run-1", "原因 A", "ops-a");
        await store.TrySetOverrideAsync("run-2", "原因 B", "ops-b");
        await store.TryClearOverrideAsync("run-2", "ops-b");

        var active = await store.GetActiveOverridesAsync();

        Assert.AreEqual(1, active.Count, "仅 run-1 的覆盖应保持活跃。");
        Assert.AreEqual("run-1", active[0].RunId);
    }

    // ---------------------------------------------------------------------------
    // Postgres 迁移 SQL
    // ---------------------------------------------------------------------------

    [TestMethod]
    public void MigrationSql_IncludesEmergencyOverridesTable()
    {
        var sql = BuildSql();

        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_canary_emergency_overrides");
        StringAssert.Contains(sql, "run_id text NOT NULL");
        StringAssert.Contains(sql, "reason text NOT NULL");
        StringAssert.Contains(sql, "operator_name text NOT NULL");
        StringAssert.Contains(sql, "cleared_at timestamptz");
        StringAssert.Contains(sql, "cleared_by text");
        StringAssert.Contains(sql, "PRIMARY KEY (run_id)");
        // 活跃覆盖部分唯一索引：同一 run 至多一条 cleared_at IS NULL 记录。
        StringAssert.Contains(sql, "CREATE UNIQUE INDEX IF NOT EXISTS ix_cc_canary_emergency_overrides_active");
        StringAssert.Contains(sql, "WHERE cleared_at IS NULL");
    }

    [TestMethod]
    public void MigrationSql_WithSchema_UsesSchemaQualifiedTable()
    {
        var sql = PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            TablePrefix = "cc_",
            SchemaName = "contextcore_ceo",
            EnablePgVectorExtension = false
        });

        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS contextcore_ceo.cc_canary_emergency_overrides");
        StringAssert.Contains(sql, "ix_cc_canary_emergency_overrides_active");
    }

    // ---------------------------------------------------------------------------
    // 路由层：活跃覆盖强制回退 V1
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task RetrievalRuntime_ActiveOverride_ForcesLegacyPath()
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(R28BTestHelpers.MakeResult("op-kill-retrieval"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var overrideStore = new InMemoryCanaryEmergencyOverrideStore();
        await overrideStore.TrySetOverrideAsync(RunId, "v2 异常", "ops-oncall");

        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100),
            canaryMetricsCollector: null,
            emergencyOverrideStore: overrideStore);

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-kill-retrieval",
            WorkspaceId = "ws-kill",
            CollectionId = "col-kill",
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = RunId
            }
        };

        await runtime.RetrieveAsync(request, CancellationToken.None);

        Assert.AreEqual(0, stubV2.ExecuteCallCount,
            "100% cutover + 活跃紧急覆盖时必须强制回退 V1，V2 不应被执行。");
        Assert.AreEqual(1, trackingStore.QueryCallCount, "应走 Legacy 检索路径。");
    }

    [TestMethod]
    public async Task RetrievalRuntime_NoOverride_StillUsesV2()
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(R28BTestHelpers.MakeResult("op-no-kill"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var overrideStore = new InMemoryCanaryEmergencyOverrideStore();

        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100),
            canaryMetricsCollector: null,
            emergencyOverrideStore: overrideStore);

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-no-kill",
            WorkspaceId = "ws-no-kill",
            CollectionId = "col-no-kill",
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = RunId
            }
        };

        await runtime.RetrieveAsync(request, CancellationToken.None);

        Assert.AreEqual(1, stubV2.ExecuteCallCount, "无活跃覆盖时 100% cutover 应正常走 V2。");
        Assert.AreEqual(0, trackingStore.QueryCallCount, "100% V2 路径不应执行 Legacy。");
    }

    [TestMethod]
    public async Task RetrievalRuntime_OverrideOnOtherRun_DoesNotAffect()
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(R28BTestHelpers.MakeResult("op-other-run"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var overrideStore = new InMemoryCanaryEmergencyOverrideStore();
        await overrideStore.TrySetOverrideAsync("run-other", "另一 run 被 Kill", "ops-oncall");

        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100),
            canaryMetricsCollector: null,
            emergencyOverrideStore: overrideStore);

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-other-run",
            WorkspaceId = "ws-other",
            CollectionId = "col-other",
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = RunId
            }
        };

        await runtime.RetrieveAsync(request, CancellationToken.None);

        Assert.AreEqual(1, stubV2.ExecuteCallCount, "覆盖仅作用于对应 run，其他 run 流量不受影响。");
    }

    [TestMethod]
    public async Task PackageRuntime_ActiveOverride_ForcesLegacyPath()
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyBuilder = new BasicContextPackageBuilder(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(R28BTestHelpers.MakeResult("op-kill-package"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new PackageResultProjector();
        var overrideStore = new InMemoryCanaryEmergencyOverrideStore();
        await overrideStore.TrySetOverrideAsync(RunId, "v2 异常", "ops-oncall");

        var runtime = new AuthoritativePackageRuntime(
            legacyBuilder, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100),
            canaryMetricsCollector: null,
            emergencyOverrideStore: overrideStore);

        var request = new ContextPackageRequest
        {
            WorkspaceId = "ws-kill-pkg",
            CollectionId = "col-kill-pkg",
            QueryText = "紧急覆盖测试",
            TokenBudget = 4096,
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = RunId
            }
        };

        await runtime.BuildDetailedAsync(request, CancellationToken.None);

        Assert.AreEqual(0, stubV2.ExecuteCallCount,
            "100% cutover + 活跃紧急覆盖时 Package 路径也必须强制回退 V1。");
    }

    // ---------------------------------------------------------------------------
    // RecoverFromStoreAsync：覆盖期间强制 0% + 非 Consistent
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task RecoverFromStore_ActiveOverride_ForcesZeroAndEmergencyState()
    {
        var store = new InMemoryPipelineRunStore();
        var defaultController = new CutoverController(0);
        var registry = new CutoverControllerRegistry(defaultController);
        var overrideStore = new InMemoryCanaryEmergencyOverrideStore();
        await overrideStore.TrySetOverrideAsync(RunId, "v2 指标恶化", "ops-oncall");
        var decisionApplier = new StubCanaryDecisionApplier(
            [new CanaryPipelineState { RunId = RunId, Revision = 3, Percentage = 60, Status = "Active" }]);

        var service = new CanaryProgressionService(
            store, defaultController, null, null, registry,
            decisionApplier: decisionApplier,
            emergencyOverrideStore: overrideStore);

        var recovered = await service.RecoverFromStoreAsync(CancellationToken.None);

        Assert.AreEqual(1, recovered, "应恢复 1 个活跃 pipeline。");
        Assert.AreEqual(0, service.GetCurrentPercentage(RunId),
            "存在活跃紧急覆盖时恢复必须强制 0%（Kill Switch 优先于 DB 百分比）。");
        var localState = service.GetLocalState(RunId);
        Assert.AreNotEqual(CanaryLocalState.Consistent, localState,
            "覆盖期间恢复不得标记 Consistent。");
        Assert.IsTrue(localState.HasFlag(CanaryLocalState.LocalEmergencyRollback));
        Assert.IsTrue(localState.HasFlag(CanaryLocalState.OperatorAlertRequired));
        Assert.AreEqual(0, registry.GetOrCreate(RunId).CutoverPercentage,
            "CutoverController 也应恢复为 0%。");

        // 覆盖期间 AdvanceAsync 必须拒绝推进（非 Consistent）。
        var advance = await service.AdvanceAsync(
            RunId, "t-emergency-advance-001", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.IsFalse(advance.Applied, "覆盖期间推进必须被拒绝。");
        Assert.AreEqual(CanaryProgressionDecision.Hold, advance.Decision);
    }

    [TestMethod]
    public async Task RecoverFromStore_NoOverride_RestoresPercentageAndConsistent()
    {
        var store = new InMemoryPipelineRunStore();
        var defaultController = new CutoverController(0);
        var registry = new CutoverControllerRegistry(defaultController);
        var overrideStore = new InMemoryCanaryEmergencyOverrideStore();
        var decisionApplier = new StubCanaryDecisionApplier(
            [new CanaryPipelineState { RunId = RunId, Revision = 3, Percentage = 60, Status = "Active" }]);

        var service = new CanaryProgressionService(
            store, defaultController, null, null, registry,
            decisionApplier: decisionApplier,
            emergencyOverrideStore: overrideStore);

        var recovered = await service.RecoverFromStoreAsync(CancellationToken.None);

        Assert.AreEqual(1, recovered);
        Assert.AreEqual(60, service.GetCurrentPercentage(RunId), "无覆盖时应按 DB 百分比恢复。");
        Assert.AreEqual(CanaryLocalState.Consistent, service.GetLocalState(RunId));
        Assert.AreEqual(60, registry.GetOrCreate(RunId).CutoverPercentage);
    }

    // ---------------------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------------------

    private static string BuildSql() => PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
    {
        ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
        TablePrefix = "cc_",
        EnablePgVectorExtension = true
    });

    private static async Task<CanaryEmergencyOverride> GetActiveRequiredAsync(
        ICanaryEmergencyOverrideStore store,
        string runId)
    {
        var active = await store.GetActiveAsync(runId);
        Assert.IsNotNull(active, "应存在活跃紧急覆盖。");
        return active!;
    }

    /// <summary>仅实现 GetAllActivePipelineStatesAsync 的决策应用器 stub（供恢复测试）。</summary>
    private sealed class StubCanaryDecisionApplier : ICanaryDecisionApplier
    {
        private readonly IReadOnlyList<CanaryPipelineState> _activeStates;

        public StubCanaryDecisionApplier(IReadOnlyList<CanaryPipelineState> activeStates) => _activeStates = activeStates;

        public ValueTask<CanaryDecisionResult> ApplyCanaryDecisionAsync(
            CanaryDecisionRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new CanaryDecisionResult
            {
                Applied = false,
                PreviousPercentage = 0,
                CurrentPercentage = 0,
                NewRevision = 0,
                NewEpoch = 0,
                FailureReason = "NotImplemented"
            });

        public ValueTask<CanaryDecisionResult> ApplyCanaryDecisionLocalAsync(
            CanaryDecisionRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new CanaryDecisionResult
            {
                Applied = false,
                PreviousPercentage = 0,
                CurrentPercentage = 0,
                NewRevision = 0,
                NewEpoch = 0,
                FailureReason = "NotImplemented"
            });

        public ValueTask<CanaryPipelineState> GetCanaryPipelineStateAsync(
            string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new CanaryPipelineState { RunId = runId, Revision = 0, Percentage = 0 });

        public ValueTask<long> GetCurrentEpochAsync(
            string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(0L);

        public ValueTask<IReadOnlyList<CanaryPipelineState>> GetAllActivePipelineStatesAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_activeStates);
    }
}
