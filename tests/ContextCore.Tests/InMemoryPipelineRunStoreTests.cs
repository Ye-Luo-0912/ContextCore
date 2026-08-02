using ContextCore.Abstractions;
using ContextCore.Core.Services.Evolution;

namespace ContextCore.Tests;

/// <summary>
/// InMemoryPipelineRunStore 实现测试。
///
/// 覆盖：
///   1. SaveRunAsync null / GetRunAsync null / ListRunsByProposalAsync null 抛异常
///   2. Run + Canary + Rollback + Baseline 4 类记录的 Save/Get/List/Delete 往返
///   3. Count 属性
///   4. 默认构造（无参）创建实例可用
/// </summary>
[TestClass]
[TestCategory("R27")]
[TestCategory("Evolution")]
public sealed class InMemoryPipelineRunStoreTests
{
    // =========================================================================
    // 1. SaveRunAsync + GetRunAsync
    // =========================================================================

    [TestMethod]
    public async Task SaveRunAsync_NullSnapshot_Throws()
    {
        var store = new InMemoryPipelineRunStore();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.SaveRunAsync(null!));
    }

    [TestMethod]
    public async Task SaveRunAsync_NewRun_IncrementsRunCount()
    {
        var store = new InMemoryPipelineRunStore();
        var snapshot = MakeRunSnapshot("run-1", "prop-1");

        await store.SaveRunAsync(snapshot);

        Assert.AreEqual(1, store.RunCount);
    }

    [TestMethod]
    public async Task SaveRunAsync_SameRunId_Overwrites()
    {
        var store = new InMemoryPipelineRunStore();
        var s1 = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.OfflineExperiment);
        var s2 = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow);

        await store.SaveRunAsync(s1);
        await store.SaveRunAsync(s2);

        Assert.AreEqual(1, store.RunCount);
        var fetched = await store.GetRunAsync("run-1");
        Assert.AreEqual(OptimizationStage.Shadow, fetched!.CurrentStage);
    }

    [TestMethod]
    public async Task GetRunAsync_NotFound_ReturnsNull()
    {
        var store = new InMemoryPipelineRunStore();
        var fetched = await store.GetRunAsync("nonexistent");
        Assert.IsNull(fetched);
    }

    // =========================================================================
    // 2. ListRunsByProposalAsync
    // =========================================================================

    [TestMethod]
    public async Task ListRunsByProposalAsync_FiltersByProposalId()
    {
        var store = new InMemoryPipelineRunStore();
        await store.SaveRunAsync(MakeRunSnapshot("run-1", "prop-a"));
        await store.SaveRunAsync(MakeRunSnapshot("run-2", "prop-b"));
        await store.SaveRunAsync(MakeRunSnapshot("run-3", "prop-a"));

        var results = await store.ListRunsByProposalAsync("prop-a");

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.All(r => r.ProposalId == "prop-a"));
    }

    [TestMethod]
    public async Task ListRunsByProposalAsync_TakeLimitsResults()
    {
        var store = new InMemoryPipelineRunStore();
        await store.SaveRunAsync(MakeRunSnapshot("run-1", "prop-a"));
        await store.SaveRunAsync(MakeRunSnapshot("run-2", "prop-a"));
        await store.SaveRunAsync(MakeRunSnapshot("run-3", "prop-a"));

        var results = await store.ListRunsByProposalAsync("prop-a", take: 2);

        Assert.AreEqual(2, results.Count);
    }

    // =========================================================================
    // 3. DeleteRunAsync
    // =========================================================================

    [TestMethod]
    public async Task DeleteRunAsync_ExistingRun_ReturnsTrue()
    {
        var store = new InMemoryPipelineRunStore();
        await store.SaveRunAsync(MakeRunSnapshot("run-1", "prop-1"));

        var result = await store.DeleteRunAsync("run-1");

        Assert.IsTrue(result);
        Assert.AreEqual(0, store.RunCount);
    }

    [TestMethod]
    public async Task DeleteRunAsync_NonExistingRun_ReturnsFalse()
    {
        var store = new InMemoryPipelineRunStore();
        var result = await store.DeleteRunAsync("nonexistent");
        Assert.IsFalse(result);
    }

    // =========================================================================
    // 4. Canary assignments
    // =========================================================================

    [TestMethod]
    public async Task SaveCanaryAssignmentAsync_NewAssignment_IncrementsCount()
    {
        var store = new InMemoryPipelineRunStore();
        var assignment = MakeCanaryAssignment("assign-1", "run-1", "prop-1");

        await store.SaveCanaryAssignmentAsync(assignment);

        Assert.AreEqual(1, store.CanaryAssignmentCount);
    }

    [TestMethod]
    public async Task ListCanaryAssignmentsByRunAsync_FiltersByRunId()
    {
        var store = new InMemoryPipelineRunStore();
        await store.SaveCanaryAssignmentAsync(MakeCanaryAssignment("assign-1", "run-1", "prop-1"));
        await store.SaveCanaryAssignmentAsync(MakeCanaryAssignment("assign-2", "run-2", "prop-2"));
        await store.SaveCanaryAssignmentAsync(MakeCanaryAssignment("assign-3", "run-1", "prop-1"));

        var results = await store.ListCanaryAssignmentsByRunAsync("run-1");

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.All(a => a.RunId == "run-1"));
    }

    // =========================================================================
    // 5. Rollback records
    // =========================================================================

    [TestMethod]
    public async Task SaveRollbackRecordAsync_NewRecord_IncrementsCount()
    {
        var store = new InMemoryPipelineRunStore();
        var record = MakeRollbackRecord("rb-1", "run-1", "prop-1");

        await store.SaveRollbackRecordAsync(record);

        Assert.AreEqual(1, store.RollbackRecordCount);
    }

    [TestMethod]
    public async Task GetRollbackRecordByRunAsync_FindsByRunId()
    {
        var store = new InMemoryPipelineRunStore();
        await store.SaveRollbackRecordAsync(MakeRollbackRecord("rb-1", "run-1", "prop-1"));

        var record = await store.GetRollbackRecordByRunAsync("run-1");

        Assert.IsNotNull(record);
        Assert.AreEqual("rb-1", record!.RecordId);
    }

    [TestMethod]
    public async Task GetRollbackRecordByRunAsync_NotFound_ReturnsNull()
    {
        var store = new InMemoryPipelineRunStore();
        var record = await store.GetRollbackRecordByRunAsync("nonexistent");
        Assert.IsNull(record);
    }

    // =========================================================================
    // 6. Baseline comparisons
    // =========================================================================

    [TestMethod]
    public async Task SaveBaselineComparisonAsync_NewComparison_IncrementsCount()
    {
        var store = new InMemoryPipelineRunStore();
        var comparison = MakeBaselineComparison("cmp-1", "prop-1");

        await store.SaveBaselineComparisonAsync(comparison);

        Assert.AreEqual(1, store.BaselineComparisonCount);
    }

    [TestMethod]
    public async Task ListBaselineComparisonsByProposalAsync_FiltersByProposalId()
    {
        var store = new InMemoryPipelineRunStore();
        await store.SaveBaselineComparisonAsync(MakeBaselineComparison("cmp-1", "prop-a"));
        await store.SaveBaselineComparisonAsync(MakeBaselineComparison("cmp-2", "prop-b"));
        await store.SaveBaselineComparisonAsync(MakeBaselineComparison("cmp-3", "prop-a"));

        var results = await store.ListBaselineComparisonsByProposalAsync("prop-a");

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.All(c => c.ProposalId == "prop-a"));
    }

    // =========================================================================
    // 7. DefaultConstructor
    // =========================================================================

    [TestMethod]
    public void DefaultConstructor_CreatesEmptyStore()
    {
        var store = new InMemoryPipelineRunStore();
        Assert.AreEqual(0, store.RunCount);
        Assert.AreEqual(0, store.CanaryAssignmentCount);
        Assert.AreEqual(0, store.RollbackRecordCount);
        Assert.AreEqual(0, store.BaselineComparisonCount);
    }

    // =========================================================================
    // 8. TryTransitionAsync CAS 推进
    // =========================================================================

    [TestMethod]
    public async Task TryTransitionAsync_NullRunId_Throws()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace(null) 抛 ArgumentNullException（ArgumentNullException 派生自 ArgumentException）
        var store = new InMemoryPipelineRunStore();
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2);
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.TryTransitionAsync(null!, 1, OptimizationStage.OfflineExperiment, next));
    }

    [TestMethod]
    public async Task TryTransitionAsync_EmptyRunId_Throws()
    {
        var store = new InMemoryPipelineRunStore();
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2);
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.TryTransitionAsync("", 1, OptimizationStage.OfflineExperiment, next));
    }

    [TestMethod]
    public async Task TryTransitionAsync_NullNext_Throws()
    {
        var store = new InMemoryPipelineRunStore();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.TryTransitionAsync("run-1", 1, OptimizationStage.OfflineExperiment, null!));
    }

    [TestMethod]
    public async Task TryTransitionAsync_RunIdMismatch_Throws()
    {
        var store = new InMemoryPipelineRunStore();
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2);
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.TryTransitionAsync("different-run", 1, OptimizationStage.OfflineExperiment, next));
    }

    [TestMethod]
    public async Task TryTransitionAsync_RunNotFound_ReturnsNull()
    {
        // CAS 失败语义：runId 与 next.RunId 一致但 store 中不存在该 run → 返回 null
        var store = new InMemoryPipelineRunStore();
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2);

        var result = await store.TryTransitionAsync("run-1", 1, OptimizationStage.OfflineExperiment, next);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryTransitionAsync_RevisionAndStageMatch_ReturnsNext()
    {
        // CAS 成功 — revision=1 + stage=OfflineExperiment 匹配 → 推进到 Shadow
        var store = new InMemoryPipelineRunStore();
        await store.SaveRunAsync(MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.OfflineExperiment, revision: 1));
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2);

        var result = await store.TryTransitionAsync("run-1", 1, OptimizationStage.OfflineExperiment, next);

        Assert.IsNotNull(result);
        Assert.AreEqual(OptimizationStage.Shadow, result!.CurrentStage);
        Assert.AreEqual(2, result.Revision);

        var fetched = await store.GetRunAsync("run-1");
        Assert.AreEqual(OptimizationStage.Shadow, fetched!.CurrentStage);
        Assert.AreEqual(2, fetched.Revision);
    }

    [TestMethod]
    public async Task TryTransitionAsync_RevisionMismatch_ReturnsNull()
    {
        // CAS 失败 — 当前 revision=2，但 expectedRevision=1 → 返回 null
        var store = new InMemoryPipelineRunStore();
        await store.SaveRunAsync(MakeRunSnapshot("run-1", "prop-1", revision: 2));
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 3);

        var result = await store.TryTransitionAsync("run-1", 1, OptimizationStage.OfflineExperiment, next);

        Assert.IsNull(result);
        // 当前快照未被修改
        var fetched = await store.GetRunAsync("run-1");
        Assert.AreEqual(2, fetched!.Revision);
        Assert.AreEqual(OptimizationStage.OfflineExperiment, fetched.CurrentStage);
    }

    [TestMethod]
    public async Task TryTransitionAsync_StageMismatch_ReturnsNull()
    {
        // CAS 失败 — revision 匹配但 stage 不匹配（已被并发推进到 Shadow）
        var store = new InMemoryPipelineRunStore();
        await store.SaveRunAsync(MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2));
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.ScopedCanary, revision: 3);

        var result = await store.TryTransitionAsync("run-1", 2, OptimizationStage.OfflineExperiment, next);

        Assert.IsNull(result);
        var fetched = await store.GetRunAsync("run-1");
        Assert.AreEqual(OptimizationStage.Shadow, fetched!.CurrentStage);
    }

    [TestMethod]
    public async Task TryTransitionAsync_AuditBatchWrittenAtomically()
    {
        // CAS 成功时，BaselineComparison + RollbackRecord 应在同事务内写入
        var store = new InMemoryPipelineRunStore();
        await store.SaveRunAsync(MakeRunSnapshot("run-1", "prop-1", revision: 1));
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2);
        var comparison = MakeBaselineComparison("cmp-1", "prop-1");
        var rollback = MakeRollbackRecord("rb-1", "run-1", "prop-1");
        var audit = new PipelineAuditBatch
        {
            BaselineComparison = comparison,
            RollbackRecord = rollback
        };

        var result = await store.TryTransitionAsync("run-1", 1, OptimizationStage.OfflineExperiment, next, audit);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, store.BaselineComparisonCount);
        Assert.AreEqual(1, store.RollbackRecordCount);
        // 未在 audit 中提供 CanaryAssignment → 数量保持 0
        Assert.AreEqual(0, store.CanaryAssignmentCount);
    }

    [TestMethod]
    public async Task TryTransitionAsync_CasFailure_AuditNotWritten()
    {
        // CAS 失败时，audit 批量不应写入
        var store = new InMemoryPipelineRunStore();
        await store.SaveRunAsync(MakeRunSnapshot("run-1", "prop-1", revision: 5));
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 6);
        var comparison = MakeBaselineComparison("cmp-1", "prop-1");
        var audit = new PipelineAuditBatch { BaselineComparison = comparison };

        var result = await store.TryTransitionAsync("run-1", 1, OptimizationStage.OfflineExperiment, next, audit);

        Assert.IsNull(result);
        Assert.AreEqual(0, store.BaselineComparisonCount);
    }

    [TestMethod]
    public async Task TryTransitionAsync_IdempotentRetry_SameTransitionId_ReturnsCurrent()
    {
        // 幂等重试 — next.LastTransitionId 与 current.LastTransitionId 相同 → 返回当前快照
        var store = new InMemoryPipelineRunStore();
        var current = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2, lastTransitionId: "t-123");
        await store.SaveRunAsync(current);
        // 调用方重试：使用相同的 transitionId
        var next = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2, lastTransitionId: "t-123");

        var result = await store.TryTransitionAsync("run-1", 1, OptimizationStage.OfflineExperiment, next);

        // 幂等：返回当前快照（不 CAS，不递增 revision）
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result!.Revision);
        Assert.AreEqual(OptimizationStage.Shadow, result.CurrentStage);
        Assert.AreEqual("t-123", result.LastTransitionId);
    }

    [TestMethod]
    public async Task TryTransitionAsync_ConcurrentTransition_SecondFails()
    {
        // 模拟并发推进 — 两个调用方同时推进同一 run，只有一个成功
        var store = new InMemoryPipelineRunStore();
        await store.SaveRunAsync(MakeRunSnapshot("run-1", "prop-1", revision: 1));
        var next1 = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2, lastTransitionId: "t-1");
        var next2 = MakeRunSnapshot("run-1", "prop-1", stage: OptimizationStage.Shadow, revision: 2, lastTransitionId: "t-2");

        var result1 = await store.TryTransitionAsync("run-1", 1, OptimizationStage.OfflineExperiment, next1);
        var result2 = await store.TryTransitionAsync("run-1", 1, OptimizationStage.OfflineExperiment, next2);

        Assert.IsNotNull(result1);
        Assert.IsNull(result2);
        // 当前快照是第一个推进的结果
        var fetched = await store.GetRunAsync("run-1");
        Assert.AreEqual(2, fetched!.Revision);
        Assert.AreEqual("t-1", fetched.LastTransitionId);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static PipelineRunSnapshot MakeRunSnapshot(
        string runId,
        string proposalId,
        OptimizationStage stage = OptimizationStage.OfflineExperiment,
        long revision = 1,
        string? lastTransitionId = null) => new()
    {
        RunId = runId,
        ProposalId = proposalId,
        ProposalVersion = OptimizationProposalVersion.Initial,
        Proposal = new OptimizationProposal
        {
            ProposalId = proposalId,
            Version = OptimizationProposalVersion.Initial,
            Title = "T",
            Hypothesis = "H",
            TargetComponent = OptimizationTargetComponent.PackagePolicy,
            Status = OptimizationProposalStatus.ExperimentReady,
            RollbackConditions = new[]
            {
                new RollbackCondition("error_rate", ComparisonOperator.GreaterThan, 0.05, "error rate > 5%")
            }
        },
        CurrentStage = stage,
        Status = PipelineRunStatus.Running,
        StartedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        // HA 字段
        Revision = revision,
        LastTransitionId = lastTransitionId
    };

    private static CanaryAssignment MakeCanaryAssignment(
        string assignmentId,
        string runId,
        string proposalId) => new(
        assignmentId: assignmentId,
        proposalId: proposalId,
        runId: runId,
        strategy: CanaryAssignmentStrategy.Random,
        assignedAt: DateTimeOffset.UtcNow);

    private static RollbackRecord MakeRollbackRecord(
        string recordId,
        string runId,
        string proposalId) => new(
        recordId: recordId,
        runId: runId,
        proposalId: proposalId,
        reason: RollbackReason.RollbackConditionTriggered,
        triggeredAt: DateTimeOffset.UtcNow);

    private static BaselineComparison MakeBaselineComparison(
        string comparisonId,
        string proposalId) => new(
        comparisonId: comparisonId,
        proposalId: proposalId,
        baselineMetrics: new Dictionary<string, double> { ["latency_ms"] = 100.0 },
        experimentMetrics: new Dictionary<string, double> { ["latency_ms"] = 80.0 },
        comparedAt: DateTimeOffset.UtcNow);
}
