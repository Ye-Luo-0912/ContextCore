using ContextCore.Abstractions;
using ContextCore.Core.Services.Evolution;

namespace ContextCore.Tests;

/// <summary>
/// R17-2 DefaultPromotionJudge 实现层测试。
/// 覆盖：5 种 PromotionDecision（Advance/Hold/Rollback/Promote/Reject）、终态阶段、RollbackCondition 触发、ExpectedGain 方向对比、置信度阈值、硬边界。
/// </summary>
[TestClass]
[TestCategory("R17")]
[TestCategory("Evolution")]
public sealed class DefaultPromotionJudgeTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static OptimizationProposal BuildProposal(
        OptimizationTargetComponent component = OptimizationTargetComponent.PackagePolicy,
        ExpectedGain[]? gains = null,
        RollbackCondition[]? rollbacks = null) => new()
        {
            ProposalId = "prop-test",
            Version = OptimizationProposalVersion.Initial,
            Title = "T",
            Hypothesis = "H",
            TargetComponent = component,
            Status = OptimizationProposalStatus.ExperimentReady,
            ExpectedGains = gains ?? new[]
            {
                new ExpectedGain("duration_ms", -350.0, 0.85, new[] { "ItemCount >= 50" })
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

    [TestMethod]
    public async Task JudgeAsync_AutomaticRollback_Stage_Returns_Rollback_Terminal()
    {
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal();
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.AutomaticRollback,
            baselineMetrics: new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            experimentMetrics: new Dictionary<string, double> { ["duration_ms"] = 2400.0 });

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Rollback, result.Decision);
        Assert.IsNull(result.NextStage, "AutomaticRollback 是终态，不应有 NextStage");
    }

    [TestMethod]
    public async Task JudgeAsync_Promotion_Stage_Returns_Promote_Terminal()
    {
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal();
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.Promotion,
            baselineMetrics: new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            experimentMetrics: new Dictionary<string, double> { ["duration_ms"] = 2029.0 });

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Promote, result.Decision);
        Assert.IsNull(result.NextStage, "Promotion 是终态，不应有 NextStage");
    }

    [TestMethod]
    public async Task JudgeAsync_RollbackCondition_Triggered_Returns_Rollback()
    {
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal(rollbacks: new[]
        {
            new RollbackCondition("error_rate", ComparisonOperator.GreaterThan, 0.05, "error rate > 5%")
        });
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.ScopedCanary,
            baselineMetrics: new Dictionary<string, double> { ["error_rate"] = 0.01 },
            experimentMetrics: new Dictionary<string, double> { ["error_rate"] = 0.08 }); // > 0.05

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Rollback, result.Decision);
        StringAssert.Contains(result.Rationale, "RollbackCondition triggered");
        Assert.IsTrue(result.Conditions.Count >= 1, "应返回触发条件描述");
    }

    [TestMethod]
    public async Task JudgeAsync_OfflineExperiment_ExpectedGain_Matched_Advance_To_Shadow()
    {
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal();
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.OfflineExperiment,
            baselineMetrics: new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            experimentMetrics: new Dictionary<string, double> { ["duration_ms"] = 2029.0 }); // -300，方向一致

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Advance, result.Decision);
        Assert.AreEqual(OptimizationStage.Shadow, result.NextStage);
    }

    [TestMethod]
    public async Task JudgeAsync_OfflineExperiment_No_Matching_Metric_Hold()
    {
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal(gains: new[]
        {
            new ExpectedGain("duration_ms", -350.0, 0.85, Array.Empty<string>())
        });
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.OfflineExperiment,
            baselineMetrics: new Dictionary<string, double> { ["unrelated_metric"] = 1.0 },
            experimentMetrics: new Dictionary<string, double> { ["unrelated_metric"] = 1.5 });

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Hold, result.Decision);
        Assert.IsNull(result.NextStage);
    }

    [TestMethod]
    public async Task JudgeAsync_OfflineExperiment_Contradicted_ExpectedGain_Reject()
    {
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal();
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.OfflineExperiment,
            baselineMetrics: new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            experimentMetrics: new Dictionary<string, double> { ["duration_ms"] = 2500.0 }); // +171，方向相反（期望是 -350）

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Reject, result.Decision);
        StringAssert.Contains(result.Rationale, "ExpectedGain contradicted");
    }

    [TestMethod]
    public async Task JudgeAsync_Shadow_No_Rollback_Triggered_Advance_To_ScopedCanary()
    {
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal();
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.Shadow,
            baselineMetrics: new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            experimentMetrics: new Dictionary<string, double> { ["duration_ms"] = 2200.0 }); // -129，方向一致

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Advance, result.Decision);
        Assert.AreEqual(OptimizationStage.ScopedCanary, result.NextStage);
    }

    [TestMethod]
    public async Task JudgeAsync_ScopedCanary_All_Gains_Above_Confidence_Threshold_Promote()
    {
        var judge = new DefaultPromotionJudge(promotionConfidenceThreshold: 0.70);
        var proposal = BuildProposal();
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.ScopedCanary,
            baselineMetrics: new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            experimentMetrics: new Dictionary<string, double> { ["duration_ms"] = 2029.0 });

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Promote, result.Decision);
        Assert.IsNull(result.NextStage, "Promote 是终态");
    }

    [TestMethod]
    public async Task JudgeAsync_ScopedCanary_Below_Confidence_Threshold_Hold()
    {
        var judge = new DefaultPromotionJudge(promotionConfidenceThreshold: 0.95); // 阈值高于 0.85
        var proposal = BuildProposal();
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.ScopedCanary,
            baselineMetrics: new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            experimentMetrics: new Dictionary<string, double> { ["duration_ms"] = 2029.0 });

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Hold, result.Decision);
        StringAssert.Contains(result.Rationale, "置信度低于阈值");
    }

    [TestMethod]
    public async Task JudgeAsync_ScopedCanary_No_Matching_Metric_Hold()
    {
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal(gains: new[]
        {
            new ExpectedGain("duration_ms", -350.0, 0.85, Array.Empty<string>())
        });
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.ScopedCanary,
            baselineMetrics: new Dictionary<string, double> { ["other_metric"] = 1.0 },
            experimentMetrics: new Dictionary<string, double> { ["other_metric"] = 0.5 });

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Hold, result.Decision);
        StringAssert.Contains(result.Rationale, "无匹配数据");
    }

    [TestMethod]
    public async Task JudgeAsync_Shadow_Contradicted_ExpectedGain_Reject()
    {
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal();
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.Shadow,
            baselineMetrics: new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            experimentMetrics: new Dictionary<string, double> { ["duration_ms"] = 2500.0 }); // 方向相反

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Reject, result.Decision);
    }

    [TestMethod]
    public async Task JudgeAsync_Multiple_RollbackConditions_Any_Triggered_Rollback()
    {
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal(rollbacks: new[]
        {
            new RollbackCondition("error_rate", ComparisonOperator.GreaterThan, 0.05, "error rate > 5%"),
            new RollbackCondition("cache_hit_rate", ComparisonOperator.LessThan, 0.80, "cache hit < 80%")
        });
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.Shadow,
            baselineMetrics: new Dictionary<string, double>
            {
                ["error_rate"] = 0.01,
                ["cache_hit_rate"] = 0.90
            },
            experimentMetrics: new Dictionary<string, double>
            {
                ["error_rate"] = 0.02, // 未触发 0.02 < 0.05
                ["cache_hit_rate"] = 0.70 // 触发 0.70 < 0.80
            });

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Rollback, result.Decision, "任一 RollbackCondition 触发应 Rollback");
        StringAssert.Contains(result.Rationale, "cache_hit_rate");
    }

    [TestMethod]
    public async Task JudgeAsync_RollbackCondition_No_Experiment_Metric_Not_Triggered()
    {
        // experimentMetrics 不包含 RollbackCondition 的 metric → 不触发，按 ExpectedGain 方向决定
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal(rollbacks: new[]
        {
            new RollbackCondition("missing_metric", ComparisonOperator.GreaterThan, 1.0, "missing")
        });
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.OfflineExperiment,
            baselineMetrics: new Dictionary<string, double> { ["duration_ms"] = 2329.0 },
            experimentMetrics: new Dictionary<string, double> { ["duration_ms"] = 2029.0 }); // 不包含 missing_metric

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Advance, result.Decision, "RollbackCondition 的 metric 不存在不应触发");
    }

    [TestMethod]
    public async Task JudgeAsync_NullRequest_Throws()
    {
        var judge = new DefaultPromotionJudge();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
            await judge.JudgeAsync(null!));
    }

    [TestMethod]
    public void Constructor_Invalid_Confidence_Threshold_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new DefaultPromotionJudge(promotionConfidenceThreshold: 1.5));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new DefaultPromotionJudge(promotionConfidenceThreshold: -0.1));
    }

    [TestMethod]
    public async Task JudgeAsync_OfflineExperiment_All_Multiple_Gains_Matched_Advance()
    {
        var judge = new DefaultPromotionJudge();
        var proposal = BuildProposal(gains: new[]
        {
            new ExpectedGain("duration_ms", -350.0, 0.85, Array.Empty<string>()),
            new ExpectedGain("allocation_kb", -200.0, 0.88, Array.Empty<string>())
        });
        var request = new PromotionJudgeRequest(
            proposal, OptimizationStage.OfflineExperiment,
            baselineMetrics: new Dictionary<string, double>
            {
                ["duration_ms"] = 2329.0,
                ["allocation_kb"] = 819.0
            },
            experimentMetrics: new Dictionary<string, double>
            {
                ["duration_ms"] = 2029.0, // -300 方向一致
                ["allocation_kb"] = 700.0 // -119 方向一致
            });

        var result = await judge.JudgeAsync(request);

        Assert.AreEqual(PromotionDecision.Advance, result.Decision);
        Assert.AreEqual(OptimizationStage.Shadow, result.NextStage);
        StringAssert.Contains(result.Rationale, "2 条 ExpectedGain");
    }
}
