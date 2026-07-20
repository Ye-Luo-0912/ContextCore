using ContextCore.Abstractions;
using ContextCore.Core.Services.Evolution;

namespace ContextCore.Tests;

/// <summary>
/// R17-2 DefaultGuardedOptimizationPipeline 实现层测试。
/// 覆盖：StartAsync + AdvanceAsync + GetStatusAsync、5 阶段顺序推进、自动回滚、终态、硬边界。
/// </summary>
[TestClass]
[TestCategory("R17")]
[TestCategory("Evolution")]
public sealed class DefaultGuardedOptimizationPipelineTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static OptimizationProposal BuildExperimentReadyProposal(
        ExpectedGain[]? gains = null,
        RollbackCondition[]? rollbacks = null) => new()
        {
            ProposalId = "prop-test",
            Version = OptimizationProposalVersion.Initial,
            Title = "T",
            Hypothesis = "H",
            TargetComponent = OptimizationTargetComponent.PackagePolicy,
            Status = OptimizationProposalStatus.ExperimentReady,
            ExpectedGains = gains ?? new[]
            {
                new ExpectedGain("duration_ms", -350.0, 0.85, Array.Empty<string>())
            },
            Risks = new[]
            {
                new RiskAssessment("R1", "desc", RiskSeverity.Low, Array.Empty<string>(), Array.Empty<string>())
            },
            RollbackConditions = rollbacks ?? new[]
            {
                new RollbackCondition("error_rate", ComparisonOperator.GreaterThan, 0.05, "error rate > 5%")
            }
        };

    private static DefaultGuardedOptimizationPipeline BuildPipeline(
        double confidenceThreshold = 0.70)
        => new(new DefaultPromotionJudge(promotionConfidenceThreshold: confidenceThreshold));

    [TestMethod]
    public async Task StartAsync_ExperimentReady_Proposal_Creates_Run_In_OfflineExperiment()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();

        var result = await pipeline.StartAsync(proposal);

        Assert.AreEqual(OptimizationStage.OfflineExperiment, result.Stage);
        Assert.AreEqual(PipelineRunStatus.Running, result.Status);
        Assert.IsFalse(string.IsNullOrEmpty(result.RunId));
        Assert.AreEqual(proposal.ProposalId, result.ProposalId);
        Assert.AreEqual(OptimizationProposalVersion.Initial, result.ProposalVersion);
    }

    [TestMethod]
    public async Task StartAsync_NonExperimentReady_Proposal_Throws()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal() with { Status = OptimizationProposalStatus.Validated };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await pipeline.StartAsync(proposal));
    }

    [TestMethod]
    public async Task StartAsync_No_RollbackConditions_Throws()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal(rollbacks: Array.Empty<RollbackCondition>());

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await pipeline.StartAsync(proposal));
    }

    [TestMethod]
    public async Task AdvanceWithMetricsAsync_OfflineExperiment_GainsMatched_Advances_To_Shadow()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();
        var run = await pipeline.StartAsync(proposal);

        var result = await pipeline.AdvanceWithMetricsAsync(run.RunId,
            baselineMetrics: new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            experimentMetrics: new Dictionary<string, double> { ["duration_ms"] = 2029.0 }); // -300 方向一致

        Assert.AreEqual(OptimizationStage.Shadow, result.Stage);
        Assert.AreEqual(PipelineRunStatus.StageCompleted, result.Status);
    }

    [TestMethod]
    public async Task AdvanceWithMetricsAsync_OfflineExperiment_Contradicted_Rejects()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();
        var run = await pipeline.StartAsync(proposal);

        var result = await pipeline.AdvanceWithMetricsAsync(run.RunId,
            baselineMetrics: new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            experimentMetrics: new Dictionary<string, double> { ["duration_ms"] = 2500.0 }); // +171 方向相反

        Assert.AreEqual(PipelineRunStatus.Rejected, result.Status);
        Assert.AreEqual(OptimizationStage.OfflineExperiment, result.Stage, "Reject 保持原 stage");
    }

    [TestMethod]
    public async Task AdvanceWithMetricsAsync_RollbackCondition_Triggered_Rolls_Back()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();
        var run = await pipeline.StartAsync(proposal);

        var result = await pipeline.AdvanceWithMetricsAsync(run.RunId,
            baselineMetrics: new Dictionary<string, double> { ["error_rate"] = 0.01 },
            experimentMetrics: new Dictionary<string, double> { ["error_rate"] = 0.10 }); // > 0.05 触发

        Assert.AreEqual(OptimizationStage.AutomaticRollback, result.Stage);
        Assert.AreEqual(PipelineRunStatus.RolledBack, result.Status);
        Assert.IsFalse(string.IsNullOrEmpty(result.RollbackReason));
        Assert.IsNotNull(result.CompletedAt, "回滚后应填充 CompletedAt");
    }

    [TestMethod]
    public async Task AdvanceWithMetricsAsync_RollbackRecord_Is_Persisted()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();
        var run = await pipeline.StartAsync(proposal);

        await pipeline.AdvanceWithMetricsAsync(run.RunId,
            baselineMetrics: new Dictionary<string, double> { ["error_rate"] = 0.01 },
            experimentMetrics: new Dictionary<string, double> { ["error_rate"] = 0.10 });

        var rollback = await pipeline.GetRollbackRecordAsync(run.RunId);
        Assert.IsNotNull(rollback, "RollbackRecord 应被持久化");
        Assert.AreEqual(RollbackReason.RollbackConditionTriggered, rollback.Reason);
        Assert.AreEqual("error_rate", rollback.TriggeredConditionMetricName);
        Assert.AreEqual(0.05, rollback.TriggeredConditionThreshold);
        Assert.AreEqual(0.10, rollback.TriggeredConditionValue);
    }

    [TestMethod]
    public async Task AdvanceWithMetricsAsync_FullPipeline_Promote()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();
        var run = await pipeline.StartAsync(proposal);

        // Stage 1: OfflineExperiment → Shadow
        var r1 = await pipeline.AdvanceWithMetricsAsync(run.RunId,
            new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            new Dictionary<string, double> { ["duration_ms"] = 2029.0 });
        Assert.AreEqual(OptimizationStage.Shadow, r1.Stage);

        // Stage 2: Shadow → ScopedCanary
        var r2 = await pipeline.AdvanceWithMetricsAsync(run.RunId,
            new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            new Dictionary<string, double> { ["duration_ms"] = 2100.0 });
        Assert.AreEqual(OptimizationStage.ScopedCanary, r2.Stage);

        // Stage 3: ScopedCanary → Promotion
        var r3 = await pipeline.AdvanceWithMetricsAsync(run.RunId,
            new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            new Dictionary<string, double> { ["duration_ms"] = 2029.0 });
        Assert.AreEqual(OptimizationStage.Promotion, r3.Stage);
        Assert.AreEqual(PipelineRunStatus.Promoted, r3.Status);
        Assert.IsNotNull(r3.CompletedAt);
    }

    [TestMethod]
    public async Task AdvanceWithMetricsAsync_Terminal_State_Is_Idempotent()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();
        var run = await pipeline.StartAsync(proposal);

        // 触发 Rollback
        var rolledBack = await pipeline.AdvanceWithMetricsAsync(run.RunId,
            new Dictionary<string, double> { ["error_rate"] = 0.01 },
            new Dictionary<string, double> { ["error_rate"] = 0.10 });
        Assert.AreEqual(PipelineRunStatus.RolledBack, rolledBack.Status);

        // 再次 Advance：应返回相同状态（幂等）
        var result = await pipeline.AdvanceWithMetricsAsync(run.RunId,
            new Dictionary<string, double> { ["error_rate"] = 0.01 },
            new Dictionary<string, double> { ["error_rate"] = 0.01 });

        Assert.AreEqual(PipelineRunStatus.RolledBack, result.Status);
        Assert.AreEqual(OptimizationStage.AutomaticRollback, result.Stage);
    }

    [TestMethod]
    public async Task GetStatusAsync_Returns_Current_State()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();
        var run = await pipeline.StartAsync(proposal);

        await pipeline.AdvanceWithMetricsAsync(run.RunId,
            new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            new Dictionary<string, double> { ["duration_ms"] = 2029.0 });

        var status = await pipeline.GetStatusAsync(run.RunId);

        Assert.IsNotNull(status);
        Assert.AreEqual(OptimizationStage.Shadow, status.Stage);
        Assert.AreEqual(PipelineRunStatus.StageCompleted, status.Status);
    }

    [TestMethod]
    public async Task GetStatusAsync_Unknown_RunId_Returns_Null()
    {
        var pipeline = BuildPipeline();
        var status = await pipeline.GetStatusAsync("nonexistent-run");
        Assert.IsNull(status, "未知 runId 应返回 null 而非抛异常");
    }

    [TestMethod]
    public async Task AdvanceWithMetricsAsync_Unknown_RunId_Throws()
    {
        var pipeline = BuildPipeline();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await pipeline.AdvanceWithMetricsAsync("nonexistent-run",
                new Dictionary<string, double>(),
                new Dictionary<string, double>()));
    }

    [TestMethod]
    public async Task AdvanceWithMetricsAsync_NullArguments_Throw()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();
        var run = await pipeline.StartAsync(proposal);

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
            await pipeline.AdvanceWithMetricsAsync(null!, new Dictionary<string, double>(), new Dictionary<string, double>()));
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
            await pipeline.AdvanceWithMetricsAsync(run.RunId, null!, new Dictionary<string, double>()));
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
            await pipeline.AdvanceWithMetricsAsync(run.RunId, new Dictionary<string, double>(), null!));
    }

    [TestMethod]
    public async Task StartAsync_NullProposal_Throws()
    {
        var pipeline = BuildPipeline();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
            await pipeline.StartAsync(null!));
    }

    [TestMethod]
    public async Task Constructor_NullJudge_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new DefaultGuardedOptimizationPipeline(null!));
    }

    [TestMethod]
    public async Task AdvanceWithMetricsAsync_Hold_Does_Not_Change_Stage()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal(gains: new[]
        {
            new ExpectedGain("duration_ms", -350.0, 0.85, Array.Empty<string>())
        });
        var run = await pipeline.StartAsync(proposal);

        // 仅提供不相关的 metric → Hold
        var result = await pipeline.AdvanceWithMetricsAsync(run.RunId,
            new Dictionary<string, double> { ["unrelated"] = 1.0 },
            new Dictionary<string, double> { ["unrelated"] = 1.5 });

        Assert.AreEqual(OptimizationStage.OfflineExperiment, result.Stage, "Hold 不应改变 stage");
        Assert.AreEqual(PipelineRunStatus.Running, result.Status);
    }

    [TestMethod]
    public async Task RecordCanaryAssignmentAsync_Persists_And_Queryable()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();
        var run = await pipeline.StartAsync(proposal);

        var assignment = new CanaryAssignment(
            assignmentId: "ca-1",
            proposalId: proposal.ProposalId,
            runId: run.RunId,
            strategy: CanaryAssignmentStrategy.PercentageBased,
            assignedAt: FixedTime)
        {
            Percentage = 0.05
        };

        await pipeline.RecordCanaryAssignmentAsync(assignment);
        var list = await pipeline.GetCanaryAssignmentsAsync(run.RunId);

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("ca-1", list[0].AssignmentId);
    }

    [TestMethod]
    public async Task AdvanceWithMetricsAsync_BaselineComparison_Persisted_For_Audit()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();
        var run = await pipeline.StartAsync(proposal);

        var baseline = new Dictionary<string, double> { ["duration_ms"] = 2329.0 };
        var experiment = new Dictionary<string, double> { ["duration_ms"] = 2029.0 };

        var result = await pipeline.AdvanceWithMetricsAsync(run.RunId, baseline, experiment);

        Assert.AreEqual(1, result.StageMetrics.Count, "StageMetrics 应包含最近一次实验指标");
        Assert.AreEqual(2029.0, result.StageMetrics["duration_ms"]);
    }

    [TestMethod]
    public async Task AdvanceAsync_Interface_Method_No_Metrics_Returns_Current_State()
    {
        var pipeline = BuildPipeline();
        var proposal = BuildExperimentReadyProposal();
        var run = await pipeline.StartAsync(proposal);

        // 接口方法不注入指标 → 应返回当前状态（等价于 Hold）
        var result = await pipeline.AdvanceAsync(run.RunId);

        Assert.AreEqual(OptimizationStage.OfflineExperiment, result.Stage, "无指标注入时应保持原 stage");
        Assert.AreEqual(PipelineRunStatus.Running, result.Status);
    }
}
