using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Storage.InMemory.Stores;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Tests;

// ===========================================================================
// WP-D2（P0-12）：Canary 单一真相源迁移 — pipeline_runs snapshot 原子写入
//
// 背景：推进仍先写 canary_pipelines，随后 PersistCanaryStateToRunAsync 二次写
// snapshot（独立事务），CAS 失败仅 Warning "不阻断推进" → 双真相源
// （canary_pipelines=50% / snapshot=25% / local=50%）。
//
// 修复：CanaryPercentage/Revision/Epoch 全部由 ICanaryDecisionApplier 在单一事务内
// CAS 写入 pipeline_runs snapshot；transition audit 同事务；删除生产路径对
// canary_pipelines 的写入；legacy 表只保留一次性迁移读取。
//
// 覆盖范围：
// 1. 生产路径（_decisionApplier 非空）：Advance/Rollback 后 snapshot 只被 applier
//    写一次（CanaryRevision 恰好 +1，不再二次写入导致 2 次递增）；
// 2. 连续推进：CAS 链 revision 0→1→2，epoch 同步递增（无双计数器漂移）；
// 3. 迁移 SQL：legacy canary 表（canary_pipelines / canary_transition_audit /
//    canary_run_epochs）保留供一次性迁移读取；pipeline_runs 含 canary 列。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
[TestCategory("WP-D")]
public sealed class R30F_CanarySingleTruthSourceTests
{
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

    // ---------------------------------------------------------------------------
    // 1. 生产路径：snapshot 只由 applier 写一次（旧代码会二次递增 CanaryRevision）
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task Advance_ApplierPath_WritesSnapshotExactlyOnce()
    {
        var store = new InMemoryPipelineRunStore();
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var service = new CanaryProgressionService(
            store, new CutoverController(),
            CanaryAcceptanceHelpers.DefaultOptions, time,
            decisionApplier: new SnapshotWritingDecisionApplier(store));

        var runId = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store);
        service.InitializeCanary(runId);
        time.Advance(TimeSpan.FromSeconds(2));

        var result = await service.AdvanceAsync(
            runId, "t-single-adv-001", null,
            HealthyBaseline, HealthyExperiment);

        Assert.IsTrue(result.Applied, "健康指标 + 观察时长达标时应成功推进 1% → 5%。");
        Assert.AreEqual(5, result.CurrentPercentage);

        var snapshot = await store.GetRunAsync(runId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(5, snapshot!.CanaryPercentage, "Canary 状态必须由 applier 原子写入 snapshot（单一真相源）。");
        Assert.AreEqual(1, snapshot.CanaryRevision,
            "snapshot 必须只被 applier 写入一次（0→1）；旧代码 PersistCanaryStateToRunAsync 会二次递增到 2。");
        Assert.AreEqual(1, snapshot.CanaryEpoch, "推进后 CanaryEpoch 应为 1（applier 单事务写入）。");
    }

    [TestMethod]
    public async Task Advance_Twice_RevisionAndEpochMonotonicNoDrift()
    {
        var store = new InMemoryPipelineRunStore();
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var service = new CanaryProgressionService(
            store, new CutoverController(),
            CanaryAcceptanceHelpers.DefaultOptions, time,
            decisionApplier: new SnapshotWritingDecisionApplier(store));

        var runId = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store);
        service.InitializeCanary(runId);

        time.Advance(TimeSpan.FromSeconds(2));
        var first = await service.AdvanceAsync(
            runId, "t-single-adv-2a", null, HealthyBaseline, HealthyExperiment);
        Assert.IsTrue(first.Applied);
        Assert.AreEqual(5, first.CurrentPercentage);

        time.Advance(TimeSpan.FromSeconds(2));
        var second = await service.AdvanceAsync(
            runId, "t-single-adv-2b", null, HealthyBaseline, HealthyExperiment);
        Assert.IsTrue(second.Applied);
        Assert.AreEqual(10, second.CurrentPercentage);

        var snapshot = await store.GetRunAsync(runId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(10, snapshot!.CanaryPercentage);
        Assert.AreEqual(2, snapshot.CanaryRevision,
            "两次推进 = 恰好两次 CAS（0→1→2），CanaryRevision 不得因二次写入漂移。");
        Assert.AreEqual(2, snapshot.CanaryEpoch, "epoch 必须与推进同步递增（无双计数器漂移）。");
    }

    [TestMethod]
    public async Task Rollback_ApplierPath_WritesSnapshotExactlyOnce()
    {
        var store = new InMemoryPipelineRunStore();
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var service = new CanaryProgressionService(
            store, new CutoverController(),
            CanaryAcceptanceHelpers.DefaultOptions, time,
            decisionApplier: new SnapshotWritingDecisionApplier(store));

        var runId = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store);
        service.InitializeCanary(runId);
        time.Advance(TimeSpan.FromSeconds(2));

        // divergence_rate=0.5 > 阈值 → 自动回滚
        var result = await service.AdvanceAsync(
            runId, "t-single-rb-001", null, HealthyBaseline, BadExperiment);

        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision);

        var snapshot = await store.GetRunAsync(runId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(0, snapshot!.CanaryPercentage, "回滚后 snapshot Canary 百分比必须持久化为 0%。");
        Assert.AreEqual(1, snapshot.CanaryRevision,
            "回滚 = 一次 snapshot CAS（0→1）；旧代码会二次递增到 2（双计数器漂移）。");
        Assert.AreEqual(1, snapshot.CanaryEpoch);
    }

    // ---------------------------------------------------------------------------
    // 2. 迁移 SQL：legacy canary 表保留（一次性迁移读取），pipeline_runs 含 canary 列
    // ---------------------------------------------------------------------------

    [TestMethod]
    public void MigrationSql_KeepsLegacyCanaryTablesForOneTimeMigrationRead()
    {
        var sql = BuildSql();

        Assert.IsTrue(sql.Contains("CREATE TABLE IF NOT EXISTS cc_canary_pipelines", StringComparison.Ordinal),
            "legacy canary_pipelines 表必须保留（一次性迁移读取：RecoverFromStoreAsync 对 CanaryRevision==0 的 run）。");
        Assert.IsTrue(sql.Contains("CREATE TABLE IF NOT EXISTS cc_canary_transition_audit", StringComparison.Ordinal),
            "canary_transition_audit 表必须保留（append-only 审计，与 snapshot CAS 同事务写入）。");
        Assert.IsTrue(sql.Contains("CREATE TABLE IF NOT EXISTS cc_canary_run_epochs", StringComparison.Ordinal),
            "canary_run_epochs 表必须保留（epoch 真相源）。");
        Assert.IsTrue(sql.Contains("canary_revision bigint NOT NULL DEFAULT 0", StringComparison.Ordinal),
            "pipeline_runs 必须保留 canary_revision 列（P0-12 单一真相源 CAS 锚点）。");
        Assert.IsTrue(sql.Contains("canary_percentage integer NOT NULL DEFAULT 0", StringComparison.Ordinal),
            "pipeline_runs 必须保留 canary_percentage 列。");
        Assert.IsTrue(sql.Contains("canary_epoch bigint NOT NULL DEFAULT 0", StringComparison.Ordinal),
            "pipeline_runs 必须保留 canary_epoch 列。");
    }

    [TestMethod]
    public void MigrationSql_LegacyCanaryTablesRegisteredAsOperational()
    {
        var suffixes = PostgresMigrationRunner.RequiredOperationalTableSuffixes.ToList();
        CollectionAssert.Contains(suffixes, "canary_pipelines");
        CollectionAssert.Contains(suffixes, "canary_transition_audit");
        CollectionAssert.Contains(suffixes, "canary_run_epochs");
        CollectionAssert.Contains(suffixes, "pipeline_runs");
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

    /// <summary>
    /// 模拟生产 PostgresCanaryLeaderLease 的 snapshot 单事务写入语义：
    /// GetCanaryPipelineStateAsync 读 snapshot revision；ApplyCanaryDecision(Local)Async
    /// 通过 store.UpdateCanaryStateAsync 执行 snapshot CAS（恰好一次递增），
    /// 其余方法返回与 snapshot 一致的当前值。用于验证服务生产路径不再二次写 snapshot。
    /// </summary>
    private sealed class SnapshotWritingDecisionApplier : ICanaryDecisionApplier
    {
        private readonly IPipelineRunStore _store;

        public SnapshotWritingDecisionApplier(IPipelineRunStore store) => _store = store;

        public ValueTask<CanaryDecisionResult> ApplyCanaryDecisionAsync(
            CanaryDecisionRequest request, CancellationToken cancellationToken = default)
            => ApplyCoreAsync(request, cancellationToken);

        public ValueTask<CanaryDecisionResult> ApplyCanaryDecisionLocalAsync(
            CanaryDecisionRequest request, CancellationToken cancellationToken = default)
            => ApplyCoreAsync(request, cancellationToken);

        private async ValueTask<CanaryDecisionResult> ApplyCoreAsync(
            CanaryDecisionRequest request, CancellationToken cancellationToken)
        {
            var snapshot = await _store.GetRunAsync(request.RunId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                return new CanaryDecisionResult
                {
                    Applied = false,
                    PreviousPercentage = 0,
                    CurrentPercentage = 0,
                    NewRevision = 0,
                    NewEpoch = 0,
                    FailureReason = "RevisionMismatch"
                };
            }

            if (snapshot.CanaryRevision != request.ExpectedRevision)
            {
                return new CanaryDecisionResult
                {
                    Applied = false,
                    PreviousPercentage = snapshot.CanaryPercentage,
                    CurrentPercentage = snapshot.CanaryPercentage,
                    NewRevision = (int)snapshot.CanaryRevision,
                    NewEpoch = 0,
                    FailureReason = "RevisionMismatch"
                };
            }

            var previousPercentage = snapshot.CanaryPercentage;
            var updated = await _store.UpdateCanaryStateAsync(
                request.RunId,
                request.ExpectedRevision,
                (int)request.NewPercentage,
                request.NewEpoch,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            if (updated is null)
            {
                return new CanaryDecisionResult
                {
                    Applied = false,
                    PreviousPercentage = previousPercentage,
                    CurrentPercentage = previousPercentage,
                    NewRevision = 0,
                    NewEpoch = 0,
                    FailureReason = "RevisionMismatch"
                };
            }

            return new CanaryDecisionResult
            {
                Applied = true,
                PreviousPercentage = previousPercentage,
                CurrentPercentage = (int)request.NewPercentage,
                NewRevision = (int)updated.CanaryRevision,
                NewEpoch = request.NewEpoch,
                FailureReason = "Success"
            };
        }

        public async ValueTask<CanaryPipelineState> GetCanaryPipelineStateAsync(
            string runId, CancellationToken cancellationToken = default)
        {
            var snapshot = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            return snapshot is null
                ? new CanaryPipelineState { RunId = runId, Revision = 0, Percentage = 0 }
                : new CanaryPipelineState
                {
                    RunId = runId,
                    Revision = (int)snapshot.CanaryRevision,
                    Percentage = snapshot.CanaryPercentage
                };
        }

        public async ValueTask<long> GetCurrentEpochAsync(
            string runId, CancellationToken cancellationToken = default)
        {
            var snapshot = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            return snapshot?.CanaryEpoch ?? 0;
        }

        public ValueTask<IReadOnlyList<CanaryPipelineState>> GetAllActivePipelineStatesAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<CanaryPipelineState>>(Array.Empty<CanaryPipelineState>());
    }
}
