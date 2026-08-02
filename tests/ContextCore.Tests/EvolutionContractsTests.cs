using ContextCore.Abstractions;

namespace ContextCore.Tests;

/// <summary>
/// Evolution Contracts 验证测试。
/// 确保所有公共契约类型可正确构造、字段验证生效、枚举值完整。
/// </summary>
/// <remarks>
/// V1 阶段不验证实现行为（DefaultContextEvolutionAgent/DefaultPromotionJudge 等留待后续阶段），
/// 只验证契约层本身的可实施性：构造函数参数校验、不可变性、枚举完备性。
/// </remarks>
[TestClass]
[TestCategory("Contract")]
[TestCategory("R16")]
[TestCategory("R17")]
public sealed class EvolutionContractsTests
{
    [TestMethod]
    public void OptimizationProposalVersion_Initial_Is_1_0()
    {
        var v = OptimizationProposalVersion.Initial;
        Assert.AreEqual(1, v.Major);
        Assert.AreEqual(0, v.Minor);
        Assert.AreEqual("v1.0", v.ToString());
    }

    [TestMethod]
    public void OptimizationProposalVersion_BumpMinor_Increments_Minor()
    {
        var v = OptimizationProposalVersion.Initial;
        var v2 = v.BumpMinor();
        Assert.AreEqual(1, v2.Major);
        Assert.AreEqual(1, v2.Minor);
    }

    [TestMethod]
    public void OptimizationProposalVersion_BumpMajor_Resets_Minor()
    {
        var v = OptimizationProposalVersion.Initial.BumpMinor().BumpMinor();
        var v2 = v.BumpMajor();
        Assert.AreEqual(2, v2.Major);
        Assert.AreEqual(0, v2.Minor);
    }

    [TestMethod]
    public void ExperimentEvidence_Construct_Records_Baseline_And_Delta()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var evidence = new ExperimentEvidence(
            source: "benchmark:PackageBuildCold",
            metricName: "duration_ms",
            baselineValue: 100.0,
            experimentValue: 80.0,
            sampleCount: 10,
            capturedAt: capturedAt,
            notes: "10% improvement");

        Assert.AreEqual("benchmark:PackageBuildCold", evidence.Source);
        Assert.AreEqual("duration_ms", evidence.MetricName);
        Assert.AreEqual(100.0, evidence.BaselineValue);
        Assert.AreEqual(80.0, evidence.ExperimentValue);
        Assert.AreEqual(-20.0, evidence.Delta);
        Assert.AreEqual(10, evidence.SampleCount);
        Assert.AreEqual(capturedAt, evidence.CapturedAt);
        Assert.AreEqual("10% improvement", evidence.Notes);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void ExperimentEvidence_SampleCount_Zero_Throws()
    {
        _ = new ExperimentEvidence(
            source: "test",
            metricName: "test",
            baselineValue: 0,
            experimentValue: 0,
            sampleCount: 0,
            capturedAt: DateTimeOffset.UtcNow);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void ExperimentEvidence_NullSource_Throws()
    {
        _ = new ExperimentEvidence(
            source: "",
            metricName: "test",
            baselineValue: 0,
            experimentValue: 0,
            sampleCount: 1,
            capturedAt: DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public void ExpectedGain_Construct_Records_Confidence_And_Preconditions()
    {
        var gain = new ExpectedGain(
            metricName: "duration_ms",
            estimatedDelta: -20.0,
            confidence: 0.85,
            preconditions: new[] { "TokenBudget >= 4000", "ItemCount >= 50" });

        Assert.AreEqual("duration_ms", gain.MetricName);
        Assert.AreEqual(-20.0, gain.EstimatedDelta);
        Assert.AreEqual(0.85, gain.Confidence);
        Assert.AreEqual(2, gain.Preconditions.Count);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentOutOfRangeException))]
    public void ExpectedGain_Confidence_OutOfRange_Throws()
    {
        _ = new ExpectedGain(
            metricName: "test",
            estimatedDelta: 0,
            confidence: 1.5,
            preconditions: Array.Empty<string>());
    }

    [TestMethod]
    public void RiskAssessment_Construct_Records_All_Fields()
    {
        var risk = new RiskAssessment(
            riskId: "R-001",
            description: "Token 预算溢出风险",
            severity: RiskSeverity.High,
            triggerConditions: new[] { "TokenBudget < 2000" },
            mitigations: new[] { "动态降低 section 数量" });

        Assert.AreEqual("R-001", risk.RiskId);
        Assert.AreEqual("Token 预算溢出风险", risk.Description);
        Assert.AreEqual(RiskSeverity.High, risk.Severity);
        Assert.AreEqual(1, risk.TriggerConditions.Count);
        Assert.AreEqual(1, risk.Mitigations.Count);
    }

    [TestMethod]
    public void RollbackCondition_IsTriggered_GreaterThan()
    {
        var cond = new RollbackCondition(
            metricName: "error_rate",
            op: ComparisonOperator.GreaterThan,
            threshold: 0.05,
            description: "错误率 > 5%");

        Assert.IsTrue(cond.IsTriggered(0.06), "0.06 > 0.05 应触发");
        Assert.IsFalse(cond.IsTriggered(0.05), "0.05 > 0.05 不应触发");
        Assert.IsFalse(cond.IsTriggered(0.04), "0.04 > 0.05 不应触发");
    }

    [TestMethod]
    public void RollbackCondition_IsTriggered_LessThan()
    {
        var cond = new RollbackCondition(
            metricName: "accuracy",
            op: ComparisonOperator.LessThan,
            threshold: 0.90,
            description: "准确率 < 90%");

        Assert.IsTrue(cond.IsTriggered(0.89), "0.89 < 0.90 应触发");
        Assert.IsFalse(cond.IsTriggered(0.90), "0.90 < 0.90 不应触发");
        Assert.IsFalse(cond.IsTriggered(0.91), "0.91 < 0.90 不应触发");
    }

    [TestMethod]
    public void RollbackCondition_IsTriggered_Equals()
    {
        var cond = new RollbackCondition(
            metricName: "status",
            op: ComparisonOperator.Equals,
            threshold: 500.0,
            description: "状态码 = 500");

        Assert.IsTrue(cond.IsTriggered(500.0), "500 == 500 应触发");
        Assert.IsFalse(cond.IsTriggered(499.0), "499 != 500 不应触发");
    }

    [TestMethod]
    public void OptimizationProposal_Can_Construct_With_All_Fields()
    {
        var proposal = new OptimizationProposal
        {
            ProposalId = "prop-001",
            Version = OptimizationProposalVersion.Initial,
            Title = "Reduce token budget for cold path",
            Hypothesis = "降低 recent_context section 的 token 预算可在不损失召回率的前提下减少 15% duration",
            TargetComponent = OptimizationTargetComponent.PackagePolicy,
            Status = OptimizationProposalStatus.Validated,
            Evidence = new[]
            {
                new ExperimentEvidence(
                    "benchmark:cold-path",
                    "duration_ms",
                    baselineValue: 2329,
                    experimentValue: 1980,
                    sampleCount: 37,
                    capturedAt: DateTimeOffset.UtcNow)
            },
            ExpectedGains = new[]
            {
                new ExpectedGain("duration_ms", -349, 0.92, new[] { "ItemCount >= 50" })
            },
            Risks = new[]
            {
                new RiskAssessment(
                    "R-001",
                    "section 内容可能不足",
                    RiskSeverity.Medium,
                    new[] { "TokenBudget < 2000" },
                    new[] { "动态调整" })
            },
            RollbackConditions = new[]
            {
                new RollbackCondition(
                    "cache_hit_rate",
                    ComparisonOperator.LessThan,
                    0.80,
                    "缓存命中率 < 80%")
            },
            ExperimentConfigJson = "{\"stage\":\"shadow\",\"duration\":\"24h\"}",
            RollbackPlan = "恢复 PackagePolicy.TokenBudget.RecentContext 到 2000",
            GeneratedAt = DateTimeOffset.UtcNow,
            AgentIdentifier = "agent-v1"
        };

        Assert.AreEqual("prop-001", proposal.ProposalId);
        Assert.AreEqual(OptimizationProposalVersion.Initial, proposal.Version);
        Assert.AreEqual(OptimizationTargetComponent.PackagePolicy, proposal.TargetComponent);
        Assert.AreEqual(OptimizationProposalStatus.Validated, proposal.Status);
        Assert.AreEqual(1, proposal.Evidence.Count);
        Assert.AreEqual(1, proposal.ExpectedGains.Count);
        Assert.AreEqual(1, proposal.Risks.Count);
        Assert.AreEqual(1, proposal.RollbackConditions.Count);
        Assert.AreEqual("agent-v1", proposal.AgentIdentifier);
    }

    [TestMethod]
    public void OptimizationProposal_Is_Immutable_Record()
    {
        var proposal1 = new OptimizationProposal
        {
            ProposalId = "p1",
            Version = OptimizationProposalVersion.Initial,
            Title = "T",
            Hypothesis = "H",
            TargetComponent = OptimizationTargetComponent.CachePolicy
        };
        // record 的 with 表达式生成新实例
        var proposal2 = proposal1 with { Status = OptimizationProposalStatus.ExperimentReady };
        Assert.AreEqual(OptimizationProposalStatus.Draft, proposal1.Status, "原实例不变");
        Assert.AreEqual(OptimizationProposalStatus.ExperimentReady, proposal2.Status, "新实例状态更新");
        Assert.AreNotSame(proposal1, proposal2, "with 生成新实例");
    }

    [TestMethod]
    public void AgentDiagnosticRequest_Construct_Records_Hints()
    {
        var req = new AgentDiagnosticRequest(
            workspaceId: "ws-1",
            collectionId: "col-1",
            targetComponent: OptimizationTargetComponent.CostAwareRetrievalRouter,
            hints: new Dictionary<string, string> { ["issue"] = "cache miss rate high" });

        Assert.AreEqual("ws-1", req.WorkspaceId);
        Assert.AreEqual("col-1", req.CollectionId);
        Assert.AreEqual(OptimizationTargetComponent.CostAwareRetrievalRouter, req.TargetComponent);
        Assert.AreEqual(1, req.Hints.Count);
        Assert.AreEqual("cache miss rate high", req.Hints["issue"]);
    }

    [TestMethod]
    public void AgentDiagnosticRequest_NullHints_Defaults_To_Empty()
    {
        var req = new AgentDiagnosticRequest(
            workspaceId: "ws-1",
            collectionId: null,
            targetComponent: OptimizationTargetComponent.SectionAssembly,
            hints: null);

        Assert.IsNull(req.CollectionId);
        Assert.IsNotNull(req.Hints);
        Assert.AreEqual(0, req.Hints.Count);
    }

    [TestMethod]
    public void AgentDiagnosticResult_With_Null_Proposal_Constructs()
    {
        var result = new AgentDiagnosticResult(
            proposal: null,
            summary: "No actionable hypothesis formed",
            observations: new[] { "obs1", "obs2" },
            hypothesisTrail: Array.Empty<string>());

        Assert.IsNull(result.Proposal);
        Assert.AreEqual("No actionable hypothesis formed", result.Summary);
        Assert.AreEqual(2, result.Observations.Count);
        Assert.AreEqual(0, result.HypothesisTrail.Count);
    }

    [TestMethod]
    public void PipelineRunResult_Construct_Records_All_Fields()
    {
        var completedAt = DateTimeOffset.UtcNow;
        var result = new PipelineRunResult(
            runId: "run-001",
            proposalId: "prop-001",
            proposalVersion: OptimizationProposalVersion.Initial,
            stage: OptimizationStage.ScopedCanary,
            status: PipelineRunStatus.RolledBack,
            stageMetrics: new Dictionary<string, double> { ["error_rate"] = 0.08 },
            rollbackReason: "error rate 8% > threshold 5%",
            completedAt: completedAt);

        Assert.AreEqual("run-001", result.RunId);
        Assert.AreEqual("prop-001", result.ProposalId);
        Assert.AreEqual(OptimizationProposalVersion.Initial, result.ProposalVersion);
        Assert.AreEqual(OptimizationStage.ScopedCanary, result.Stage);
        Assert.AreEqual(PipelineRunStatus.RolledBack, result.Status);
        Assert.AreEqual(1, result.StageMetrics.Count);
        Assert.AreEqual(0.08, result.StageMetrics["error_rate"]);
        Assert.AreEqual("error rate 8% > threshold 5%", result.RollbackReason);
        Assert.AreEqual(completedAt, result.CompletedAt);
    }

    [TestMethod]
    public void PromotionJudgeRequest_Construct_Records_Metrics()
    {
        var proposal = new OptimizationProposal
        {
            ProposalId = "p1",
            Version = OptimizationProposalVersion.Initial,
            Title = "T",
            Hypothesis = "H",
            TargetComponent = OptimizationTargetComponent.CandidateUtilityReranker
        };
        var req = new PromotionJudgeRequest(
            proposal: proposal,
            currentStage: OptimizationStage.ScopedCanary,
            baselineMetrics: new Dictionary<string, double> { ["accuracy"] = 0.85 },
            experimentMetrics: new Dictionary<string, double> { ["accuracy"] = 0.88 },
            stageMetrics: new Dictionary<string, double> { ["p99_latency"] = 12.5 });

        Assert.AreSame(proposal, req.Proposal);
        Assert.AreEqual(OptimizationStage.ScopedCanary, req.CurrentStage);
        Assert.AreEqual(0.85, req.BaselineMetrics["accuracy"]);
        Assert.AreEqual(0.88, req.ExperimentMetrics["accuracy"]);
        Assert.AreEqual(12.5, req.StageMetrics["p99_latency"]);
    }

    [TestMethod]
    public void PromotionJudgeResult_Construct_With_All_Decision_Types()
    {
        // 测试所有 5 种 PromotionDecision 都能构造合法结果
        var decisions = new[]
        {
            PromotionDecision.Advance,
            PromotionDecision.Hold,
            PromotionDecision.Rollback,
            PromotionDecision.Promote,
            PromotionDecision.Reject
        };

        foreach (var decision in decisions)
        {
            var result = new PromotionJudgeResult(
                decision: decision,
                rationale: $"rationale for {decision}",
                nextStage: decision == PromotionDecision.Advance ? OptimizationStage.Shadow : null,
                conditions: new[] { "observe 24h" });

            Assert.AreEqual(decision, result.Decision);
            Assert.AreEqual($"rationale for {decision}", result.Rationale);
            if (decision == PromotionDecision.Advance)
            {
                Assert.AreEqual(OptimizationStage.Shadow, result.NextStage);
            }
            else
            {
                Assert.IsNull(result.NextStage);
            }
            Assert.AreEqual(1, result.Conditions.Count);
        }
    }

    [TestMethod]
    public void Enums_Have_Expected_Value_Counts()
    {
        // 防御性：枚举值数量应与契约设计一致，避免后续 silently 增删导致的行为变化
        Assert.AreEqual(8, Enum.GetValues<OptimizationProposalStatus>().Length, "OptimizationProposalStatus 应有 8 个值");
        Assert.AreEqual(6, Enum.GetValues<OptimizationTargetComponent>().Length, "OptimizationTargetComponent 应有 6 个值");
        Assert.AreEqual(4, Enum.GetValues<RiskSeverity>().Length, "RiskSeverity 应有 4 个值");
        Assert.AreEqual(5, Enum.GetValues<ComparisonOperator>().Length, "ComparisonOperator 应有 5 个值");
        Assert.AreEqual(5, Enum.GetValues<OptimizationStage>().Length, "OptimizationStage 应有 5 个值");
        Assert.AreEqual(7, Enum.GetValues<PipelineRunStatus>().Length, "PipelineRunStatus 应有 7 个值");
        Assert.AreEqual(5, Enum.GetValues<PromotionDecision>().Length, "PromotionDecision 应有 5 个值");
    }

    [TestMethod]
    public void Interfaces_Are_Interfaces()
    {
        // 确保所有 R16/R17 主接口都是 interface 类型（编译期约束在运行时也成立）
        Assert.IsTrue(typeof(IAgentObservationSource).IsInterface);
        Assert.IsTrue(typeof(IContextEvolutionAgent).IsInterface);
        Assert.IsTrue(typeof(IPromotionJudge).IsInterface);
        Assert.IsTrue(typeof(IGuardedOptimizationPipeline).IsInterface);
    }
}
