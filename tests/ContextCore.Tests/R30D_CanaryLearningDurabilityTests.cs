using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Tests;

// ===========================================================================
// WP-D：Recovery、Canary 与 Learning Durability — 验收测试
//
// 覆盖范围：
//   1. 迁移 SQL：pipeline_runs 追加 canary_percentage / canary_revision / canary_epoch 列，
//      learning_leases 表创建 + 必需表后缀注册。
//   2. IPipelineRunStore.UpdateCanaryStateAsync：单真相源 CAS 写入
//      （成功推进 / revision 不匹配返回 null / run 不存在返回 null）。
//   3. CanaryProgressionService 单真相源写入：Advance/Rollback 后将 canary 状态并入
//      pipeline run snapshot（CanaryPercentage / CanaryRevision / CanaryEpoch）。
//   4. RecoverFromStoreAsync：snapshot 优先恢复（单一真相源），legacy canary_pipelines
//      仅作为 CanaryRevision == 0 时的回退路径。
//   5. ILearningLeaseStore（InMemory）：获取排他 / token CAS 续约 / 释放 / 过期清理。
//
// 设计原则：
//   - 复用 R28B/R29H 的 InMemoryPipelineRunStore + FakeTimeProvider 模式，不 stub 决策内核。
//   - 迁移断言复用 R29S 的 BuildMigrationSql 文本校验模式。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
[TestCategory("WP-D")]
public sealed class R30D_CanaryLearningDurabilityTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, double> HealthyBaseline =
        new Dictionary<string, double>
        {
            ["error_rate"] = 0.01,
            ["p95_latency_ms"] = 100.0
        };

    private static readonly IReadOnlyDictionary<string, double> HealthyExperiment =
        new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.02
        };

    private static readonly IReadOnlyDictionary<string, double> BadExperiment =
        new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.5
        };

    // ===========================================================================
    // 1. 迁移 SQL 断言（v56 → v57）
    // ===========================================================================

    [TestMethod]
    public void MigrationSql_AddsCanaryColumnsToPipelineRuns()
    {
        var sql = BuildSql();

        Assert.IsTrue(sql.Contains("canary_percentage integer NOT NULL DEFAULT 0", StringComparison.Ordinal),
            "baseline pipeline_runs 必须包含 canary_percentage 列。");
        Assert.IsTrue(sql.Contains("canary_revision bigint NOT NULL DEFAULT 0", StringComparison.Ordinal),
            "baseline pipeline_runs 必须包含 canary_revision 列。");
        Assert.IsTrue(sql.Contains("canary_epoch bigint NOT NULL DEFAULT 0", StringComparison.Ordinal),
            "baseline pipeline_runs 必须包含 canary_epoch 列。");
        Assert.IsTrue(sql.Contains("ADD COLUMN IF NOT EXISTS canary_percentage integer NOT NULL DEFAULT 0", StringComparison.Ordinal),
            "升级路径必须包含 canary_percentage 幂等 ADD COLUMN。");
    }

    [TestMethod]
    public void MigrationSql_CreatesLearningLeasesTable()
    {
        var sql = BuildSql();

        Assert.IsTrue(sql.Contains("CREATE TABLE IF NOT EXISTS cc_learning_leases", StringComparison.Ordinal),
            "baseline 必须创建 learning_leases 表。");
        Assert.IsTrue(sql.Contains("lease_token text NOT NULL", StringComparison.Ordinal),
            "learning_leases 必须包含 lease_token 列（CAS 校验）。");
        Assert.IsTrue(sql.Contains("lease_expires_at timestamptz NOT NULL", StringComparison.Ordinal),
            "learning_leases 必须包含 lease_expires_at 列（过期抢占）。");
        CollectionAssert.Contains(
            PostgresMigrationRunner.RequiredOperationalTableSuffixes.ToList(),
            "learning_leases");
    }

    // ===========================================================================
    // 2. IPipelineRunStore.UpdateCanaryStateAsync（单真相源 CAS 写入）
    // ===========================================================================

    [TestMethod]
    public async Task UpdateCanaryState_Success_AdvancesCanaryFields()
    {
        var store = new InMemoryPipelineRunStore();
        var runId = await CreateScopedCanaryRunAsync(store);

        var updatedAt = BaseTime.AddMinutes(5);
        var updated = await store.UpdateCanaryStateAsync(runId, expectedCanaryRevision: 0, newPercentage: 25, newEpoch: 3, updatedAt);

        Assert.IsNotNull(updated, "CAS 匹配时应成功更新。");
        Assert.AreEqual(25, updated!.CanaryPercentage);
        Assert.AreEqual(1, updated.CanaryRevision, "CanaryRevision 应从 0 推进到 1。");
        Assert.AreEqual(3, updated.CanaryEpoch);
        Assert.AreEqual(updatedAt, updated.UpdatedAt);

        var persisted = await store.GetRunAsync(runId);
        Assert.AreEqual(25, persisted!.CanaryPercentage, "更新必须持久化到 store。");
        Assert.AreEqual(1, persisted.CanaryRevision);
    }

    [TestMethod]
    public async Task UpdateCanaryState_CasMismatch_ReturnsNull()
    {
        var store = new InMemoryPipelineRunStore();
        var runId = await CreateScopedCanaryRunAsync(store);

        await store.UpdateCanaryStateAsync(runId, expectedCanaryRevision: 0, newPercentage: 5, newEpoch: 1, BaseTime);
        var stale = await store.UpdateCanaryStateAsync(runId, expectedCanaryRevision: 0, newPercentage: 10, newEpoch: 2, BaseTime);

        Assert.IsNull(stale, "expectedCanaryRevision 与 store 内值不匹配时必须返回 null（CAS 失败）。");
        var persisted = await store.GetRunAsync(runId);
        Assert.AreEqual(5, persisted!.CanaryPercentage, "CAS 失败不得修改已持久化的 canary 状态。");
    }

    [TestMethod]
    public async Task UpdateCanaryState_RunMissing_ReturnsNull()
    {
        var store = new InMemoryPipelineRunStore();
        var result = await store.UpdateCanaryStateAsync("run-does-not-exist", 0, 5, 1, BaseTime);
        Assert.IsNull(result, "run 不存在时必须返回 null。");
    }

    // ===========================================================================
    // 3. CanaryProgressionService 单真相源写入
    // ===========================================================================

    [TestMethod]
    public async Task Advance_PersistsCanaryStateToRunSnapshot()
    {
        var (service, time, store) = BuildService();
        var runId = await CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);
        Assert.AreEqual(1, service.GetCurrentPercentage(runId), "初始化后应为阶梯首档 1%。");

        time.Advance(TimeSpan.FromSeconds(2));
        var result = await service.AdvanceAsync(runId, "t-wp-d-advance-1", null, HealthyBaseline, HealthyExperiment);

        Assert.IsTrue(result.Applied, "健康指标 + 观察时长达标时应成功推进 1% → 5%。");
        Assert.AreEqual(5, result.CurrentPercentage);

        var snapshot = await store.GetRunAsync(runId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(5, snapshot!.CanaryPercentage, "推进后 canary 状态必须并入 pipeline run snapshot（单一真相源）。");
        Assert.AreEqual(1, snapshot.CanaryRevision, "首次单真相源写入后 CanaryRevision 应为 1。");
        Assert.AreEqual(1, snapshot.CanaryEpoch, "首次推进后 CanaryEpoch 应为 1。");
        Assert.IsTrue(snapshot.UpdatedAt > BaseTime, "UpdatedAt 必须刷新。");
    }

    [TestMethod]
    public async Task Rollback_PersistsZeroToRunSnapshot()
    {
        var (service, time, store) = BuildService();
        var runId = await CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);
        time.Advance(TimeSpan.FromSeconds(2));

        // divergence_rate=0.5 > 阈值 0.05 → 触发回滚
        var result = await service.AdvanceAsync(runId, "t-wp-d-rollback-1", null, HealthyBaseline, BadExperiment);

        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision);

        var snapshot = await store.GetRunAsync(runId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(0, snapshot!.CanaryPercentage, "回滚后 snapshot canary 百分比必须持久化为 0%。");
        Assert.AreEqual(1, snapshot.CanaryRevision, "回滚也应触发单真相源写入（CanaryRevision=1）。");
        Assert.AreEqual(1, snapshot.CanaryEpoch);
    }

    [TestMethod]
    public async Task RecoverFromStore_SnapshotIsPrimaryTruth()
    {
        var store = new InMemoryPipelineRunStore();
        var defaultController = new CutoverController(0);
        var registry = new CutoverControllerRegistry(defaultController);
        var runId = await CreateScopedCanaryRunAsync(store, canaryPercentage: 25, canaryRevision: 2, canaryEpoch: 2);
        // legacy canary_pipelines 声称 60%——snapshot（25%）必须是权威
        var decisionApplier = new StubCanaryDecisionApplier(
            [new CanaryPipelineState { RunId = runId, Revision = 3, Percentage = 60, Status = "Active" }]);

        var service = new CanaryProgressionService(
            store, defaultController, null, null, registry,
            decisionApplier: decisionApplier);

        var recovered = await service.RecoverFromStoreAsync(CancellationToken.None);

        Assert.AreEqual(1, recovered, "应从 snapshot 恢复 1 个活跃 pipeline。");
        Assert.AreEqual(25, service.GetCurrentPercentage(runId),
            "snapshot 是单一真相源——恢复必须采用 snapshot.CanaryPercentage（25）而非 legacy 表（60）。");
        Assert.AreEqual(CanaryLocalState.Consistent, service.GetLocalState(runId));
        Assert.AreEqual(25, registry.GetOrCreate(runId).CutoverPercentage);
    }

    [TestMethod]
    public async Task RecoverFromStore_LegacyFallback_WhenSnapshotLacksCanaryData()
    {
        var store = new InMemoryPipelineRunStore();
        var defaultController = new CutoverController(0);
        var registry = new CutoverControllerRegistry(defaultController);
        // CanaryRevision == 0 = legacy 数据（尚未经单真相源写入）→ 应回退到 canary_pipelines
        var runId = await CreateScopedCanaryRunAsync(store);
        var decisionApplier = new StubCanaryDecisionApplier(
            [new CanaryPipelineState { RunId = runId, Revision = 3, Percentage = 60, Status = "Active" }]);

        var service = new CanaryProgressionService(
            store, defaultController, null, null, registry,
            decisionApplier: decisionApplier);

        var recovered = await service.RecoverFromStoreAsync(CancellationToken.None);

        Assert.AreEqual(1, recovered);
        Assert.AreEqual(60, service.GetCurrentPercentage(runId), "legacy run 应按 canary_pipelines 百分比恢复。");
        Assert.AreEqual(CanaryLocalState.Consistent, service.GetLocalState(runId));
    }

    // ===========================================================================
    // 4. ILearningLeaseStore（InMemory）：worker 池级租约
    // ===========================================================================

    [TestMethod]
    public async Task LearningLease_TryAcquire_ExclusiveUntilExpiry()
    {
        var store = new InMemoryLearningLeaseStore();

        var first = await store.TryAcquireAsync("learning-materialization", TimeSpan.FromMinutes(5), "node-a");
        Assert.IsNotNull(first, "无现有租约时应获取成功。");

        var second = await store.TryAcquireAsync("learning-materialization", TimeSpan.FromMinutes(5), "node-b");
        Assert.IsNull(second, "现有租约未过期时其他实例不得抢占。");

        Assert.IsTrue(await store.HasActiveLeaseAsync("learning-materialization"), "未过期租约应处于活跃状态。");
        Assert.IsTrue(await store.RenewAsync("learning-materialization", first!.LeaseToken, TimeSpan.FromMinutes(5)),
            "持有者可续约。");
    }

    [TestMethod]
    public async Task LearningLease_TokenCas_RejectsNonOwner()
    {
        var store = new InMemoryLearningLeaseStore();
        var lease = await store.TryAcquireAsync("learning-materialization", TimeSpan.FromMinutes(5), "node-a");
        Assert.IsNotNull(lease);

        Assert.IsFalse(await store.RenewAsync("learning-materialization", "wrong-token", TimeSpan.FromMinutes(5)),
            "非持有者不得续约（token CAS 拒绝）。");
        Assert.IsFalse(await store.ReleaseAsync("learning-materialization", "wrong-token"),
            "非持有者不得释放（token CAS 拒绝）。");
        Assert.IsTrue(await store.HasActiveLeaseAsync("learning-materialization"),
            "被拒绝的操作不得破坏既有租约。");
    }

    [TestMethod]
    public async Task LearningLease_Release_InvalidatesLease()
    {
        var store = new InMemoryLearningLeaseStore();
        var lease = await store.TryAcquireAsync("learning-materialization", TimeSpan.FromMinutes(5), "node-a");
        Assert.IsNotNull(lease);

        Assert.IsTrue(await store.ReleaseAsync("learning-materialization", lease!.LeaseToken), "持有者可释放。");
        Assert.IsFalse(await store.HasActiveLeaseAsync("learning-materialization"), "释放后租约不再活跃。");

        var reacquired = await store.TryAcquireAsync("learning-materialization", TimeSpan.FromMinutes(5), "node-b");
        Assert.IsNotNull(reacquired, "释放后其他实例可重新获取。");
    }

    [TestMethod]
    public async Task LearningLease_ReapExpired_ClearsExpiredLeases()
    {
        var store = new InMemoryLearningLeaseStore();
        var lease = await store.TryAcquireAsync("learning-materialization", TimeSpan.FromMilliseconds(1), "node-a");
        Assert.IsNotNull(lease);

        await Task.Delay(50);
        Assert.IsFalse(await store.HasActiveLeaseAsync("learning-materialization"), "租约应已过期。");
        Assert.IsFalse(await store.RenewAsync("learning-materialization", lease!.LeaseToken, TimeSpan.FromMinutes(5)),
            "过期租约不得续约（fencing 安全边界）。");

        var reaped = await store.ReapExpiredAsync();
        Assert.AreEqual(1, reaped, "过期租约应被清理。");

        var reacquired = await store.TryAcquireAsync("learning-materialization", TimeSpan.FromMinutes(5), "node-b");
        Assert.IsNotNull(reacquired, "过期租约清理后其他实例可获取。");
    }

    // ===========================================================================
    // 辅助
    // ===========================================================================

    private static (CanaryProgressionService service, FakeTimeProvider time, InMemoryPipelineRunStore store) BuildService()
    {
        var time = new FakeTimeProvider(BaseTime);
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController();
        var service = new CanaryProgressionService(
            store, cutover,
            new CanaryGateOptions
            {
                PercentageLadder = [1, 5, 10, 25, 50, 100],
                MinObservationPeriod = TimeSpan.FromSeconds(1)
            },
            time);
        return (service, time, store);
    }

    private static async Task<string> CreateScopedCanaryRunAsync(
        IPipelineRunStore store,
        int canaryPercentage = 0,
        long canaryRevision = 0,
        long canaryEpoch = 0)
    {
        var runId = $"run-wp-d-{Guid.NewGuid():N}";
        var snapshot = new PipelineRunSnapshot
        {
            RunId = runId,
            ProposalId = "prop-wp-d-test",
            ProposalVersion = OptimizationProposalVersion.Initial,
            Proposal = BuildProposal(),
            CurrentStage = OptimizationStage.ScopedCanary,
            Status = PipelineRunStatus.Running,
            StartedAt = BaseTime,
            UpdatedAt = BaseTime,
            CompletedAt = null,
            RollbackReason = null,
            StageMetrics = Array.Empty<BaselineComparison>(),
            Revision = 1,
            LeaseOwner = null,
            LeaseExpiresAt = null,
            LastTransitionId = null,
            CanaryPercentage = canaryPercentage,
            CanaryRevision = canaryRevision,
            CanaryEpoch = canaryEpoch
        };
        var created = await store.TryCreateRunAsync(snapshot);
        Assert.IsTrue(created, "测试 run 创建失败：TryCreateRunAsync 应返回 true");
        return runId;
    }

    private static OptimizationProposal BuildProposal() => new()
    {
        ProposalId = "prop-wp-d-test",
        Version = OptimizationProposalVersion.Initial,
        Title = "WP-D Durability Test",
        Hypothesis = "H",
        TargetComponent = OptimizationTargetComponent.PackagePolicy,
        Status = OptimizationProposalStatus.ExperimentReady,
        ExpectedGains = new[]
        {
            new ExpectedGain("duration_ms", -350.0, 0.85, Array.Empty<string>())
        },
        Risks = new[]
        {
            new RiskAssessment("R1", "desc", RiskSeverity.Low, Array.Empty<string>(), Array.Empty<string>())
        },
        RollbackConditions = new[]
        {
            new RollbackCondition("error_rate", ComparisonOperator.GreaterThan, 0.05, "error rate > 5%")
        }
    };

    private static string BuildSql() => PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
    {
        ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
        TablePrefix = "cc_",
        EnablePgVectorExtension = true
    });

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
            => ValueTask.FromResult<IReadOnlyList<CanaryPipelineState>>(_activeStates);
    }

    /// <summary>可推进时间的 TimeProvider：测试用，通过 Advance 推进时间。</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _current;

        public FakeTimeProvider(DateTimeOffset initial) => _current = initial;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan delta) => _current = _current.Add(delta);
    }
}
