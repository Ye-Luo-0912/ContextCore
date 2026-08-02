using ContextCore.Abstractions;
using ContextCore.Core.Services.Evolution;

namespace ContextCore.Tests;

/// <summary>
/// DefaultContextEvolutionAgent 实现层测试。
/// 覆盖：DiagnoseAsync 生成 Validated proposal、RefineProposalAsync 推进/驳回、硬边界（Status 上限/管道状态拒绝）。
/// </summary>
[TestClass]
[TestCategory("R16")]
[TestCategory("Evolution")]
public sealed class DefaultContextEvolutionAgentTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task DiagnoseAsync_NoObservationMetrics_StillProducesValidatedProposal_WithPlaceholderBaseline()
    {
        var source = new DefaultAgentObservationSource("test:empty");
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        var request = new AgentDiagnosticRequest(
            workspaceId: "ws-empty",
            collectionId: null,
            targetComponent: OptimizationTargetComponent.PackagePolicy);

        var result = await agent.DiagnoseAsync(request);

        Assert.IsNotNull(result.Proposal, "即使 observation 无指标，仍应输出基于模板的 Validated proposal");
        Assert.AreEqual(OptimizationProposalStatus.Validated, result.Proposal.Status);
        Assert.AreEqual(OptimizationTargetComponent.PackagePolicy, result.Proposal.TargetComponent);
        Assert.AreEqual(DefaultContextEvolutionAgent.AgentIdentifier, result.Proposal.AgentIdentifier);
        // 模板默认至少 1 条 ExpectedGain → 至少 1 条 evidence
        Assert.IsTrue(result.Proposal.Evidence.Count >= 1, "应至少有 1 条 evidence（来自模板 ExpectedGains）");
        Assert.IsTrue(result.Proposal.RollbackConditions.Count >= 1, "应至少有 1 条 RollbackCondition");
        Assert.AreEqual(OptimizationProposalVersion.Initial, result.Proposal.Version);
    }

    [TestMethod]
    public async Task DiagnoseAsync_WithObservationMetrics_ProducesEvidence_UsingMetricsAsBaseline()
    {
        var source = new DefaultAgentObservationSource("test:metrics");
        await source.RecordMetricsAsync(
            workspaceId: "ws-1",
            collectionId: "col-1",
            metrics: new Dictionary<string, double>
            {
                ["duration_ms"] = 2329.0,
                ["allocation_kb"] = 819.0
            });
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        var request = new AgentDiagnosticRequest(
            workspaceId: "ws-1",
            collectionId: "col-1",
            targetComponent: OptimizationTargetComponent.PackagePolicy);

        var result = await agent.DiagnoseAsync(request);

        Assert.IsNotNull(result.Proposal);
        var durationEvidence = result.Proposal.Evidence.Single(e => e.MetricName == "duration_ms");
        Assert.AreEqual(2329.0, durationEvidence.BaselineValue, "baseline 应取自 observation");
        Assert.AreEqual(2329.0 - 350.0, durationEvidence.ExperimentValue, "experiment = baseline + ExpectedGain.EstimatedDelta(-350)");
        Assert.AreEqual(-350.0, durationEvidence.Delta);
        Assert.AreEqual("test:metrics", durationEvidence.Source);
    }

    [TestMethod]
    public async Task DiagnoseAsync_ProposalId_EncodesWorkspaceAndCollectionAndComponent()
    {
        var source = new DefaultAgentObservationSource();
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        var request = new AgentDiagnosticRequest(
            workspaceId: "ws-id-42",
            collectionId: "col-99",
            targetComponent: OptimizationTargetComponent.CostAwareRetrievalRouter);

        var result = await agent.DiagnoseAsync(request);

        Assert.IsNotNull(result.Proposal);
        StringAssert.Contains(result.Proposal.ProposalId, "costawareretrievalrouter", "ProposalId 应含小写目标组件名");
        StringAssert.Contains(result.Proposal.ProposalId, "ws-id-42", "ProposalId 应含 workspaceId");
        StringAssert.Contains(result.Proposal.ProposalId, "col-99", "ProposalId 应含 collectionId");
        StringAssert.Contains(result.Proposal.ProposalId, "20260720120000", "ProposalId 应含时间戳（UTC）");
    }

    [TestMethod]
    public async Task DiagnoseAsync_GeneratesExperimentConfigJson_WithHints()
    {
        var source = new DefaultAgentObservationSource();
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        var request = new AgentDiagnosticRequest(
            workspaceId: "ws-hints",
            collectionId: null,
            targetComponent: OptimizationTargetComponent.CachePolicy,
            hints: new Dictionary<string, string> { ["trigger"] = "high-eviction" });

        var result = await agent.DiagnoseAsync(request);

        Assert.IsNotNull(result.Proposal);
        Assert.IsNotNull(result.Proposal.ExperimentConfigJson);
        StringAssert.Contains(result.Proposal.ExperimentConfigJson, "\"workspaceId\":\"ws-hints\"");
        StringAssert.Contains(result.Proposal.ExperimentConfigJson, "\"targetComponent\":\"CachePolicy\"");
        StringAssert.Contains(result.Proposal.ExperimentConfigJson, "\"trigger\":\"high-eviction\"");
    }

    [TestMethod]
    public async Task DiagnoseAsync_EachTargetComponent_HasMatchingTemplate()
    {
        var source = new DefaultAgentObservationSource();
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        foreach (var component in Enum.GetValues<OptimizationTargetComponent>())
        {
            var request = new AgentDiagnosticRequest(
                workspaceId: "ws-all",
                collectionId: null,
                targetComponent: component);

            var result = await agent.DiagnoseAsync(request);

            Assert.IsNotNull(result.Proposal, $"component={component} 应有匹配模板");
            Assert.AreEqual(component, result.Proposal.TargetComponent);
            Assert.IsTrue(result.Proposal.ExpectedGains.Count >= 1, $"{component} 模板至少 1 条 ExpectedGain");
            Assert.IsTrue(result.Proposal.Risks.Count >= 1, $"{component} 模板至少 1 条 Risk");
            Assert.IsTrue(result.Proposal.RollbackConditions.Count >= 1, $"{component} 模板至少 1 条 RollbackCondition");
            Assert.AreEqual(OptimizationProposalStatus.Validated, result.Proposal.Status,
                $"DiagnoseAsync 必须输出 Validated（不允许直接输出 ExperimentReady/pipeline 状态）");
        }
    }

    [TestMethod]
    public async Task RefineProposalAsync_SupportingEvidence_AdvancesToExperimentReady()
    {
        var source = new DefaultAgentObservationSource();
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        var request = new AgentDiagnosticRequest(
            workspaceId: "ws-refine",
            collectionId: null,
            targetComponent: OptimizationTargetComponent.PackagePolicy);
        var initial = (await agent.DiagnoseAsync(request)).Proposal!;
        Assert.AreEqual(OptimizationProposalStatus.Validated, initial.Status, "前置：初始状态为 Validated");

        // PackagePolicy 模板的 ExpectedGain 包含 duration_ms estimatedDelta=-350
        // 提供 experimentDelta=-300（方向一致：metric 减少更好）→ 应推进到 ExperimentReady
        var supportingEvidence = new[]
        {
            new ExperimentEvidence(
                source: "benchmark:offline",
                metricName: "duration_ms",
                baselineValue: 2329.0,
                experimentValue: 2029.0,
                sampleCount: 30,
                capturedAt: FixedTime.AddMinutes(5),
                notes: "支持假设")
        };

        var refined = await agent.RefineProposalAsync(initial, supportingEvidence);

        Assert.AreEqual(OptimizationProposalStatus.ExperimentReady, refined.Status, "方向一致的 evidence 应推进到 ExperimentReady");
        Assert.AreEqual(1, refined.Version.Minor, "Minor 版本号递增");
        Assert.AreEqual(initial.Version.Major, refined.Version.Major, "Major 不变");
        Assert.AreEqual(initial.Evidence.Count + 1, refined.Evidence.Count, "evidence 应被合并追加");
    }

    [TestMethod]
    public async Task RefineProposalAsync_ContradictingEvidence_RejectsProposal()
    {
        var source = new DefaultAgentObservationSource();
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        var request = new AgentDiagnosticRequest(
            workspaceId: "ws-reject",
            collectionId: null,
            targetComponent: OptimizationTargetComponent.PackagePolicy);
        var initial = (await agent.DiagnoseAsync(request)).Proposal!;

        // PackagePolicy 模板的 ExpectedGain duration_ms estimatedDelta=-350（希望减少）
        // 提供 experimentDelta=+100（方向相反：duration 增加）→ 应驳回
        var contradictingEvidence = new[]
        {
            new ExperimentEvidence(
                source: "benchmark:offline",
                metricName: "duration_ms",
                baselineValue: 2329.0,
                experimentValue: 2429.0,
                sampleCount: 30,
                capturedAt: FixedTime.AddMinutes(5),
                notes: "驳斥假设")
        };

        var refined = await agent.RefineProposalAsync(initial, contradictingEvidence);

        Assert.AreEqual(OptimizationProposalStatus.Rejected, refined.Status, "方向相反的 evidence 应驳回");
        Assert.AreEqual(1, refined.Version.Minor, "即使驳回也应递增版本号");
    }

    [TestMethod]
    public async Task RefineProposalAsync_UnmatchedMetric_KeepsStatusUnchanged()
    {
        var source = new DefaultAgentObservationSource();
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        var request = new AgentDiagnosticRequest(
            workspaceId: "ws-unmatched",
            collectionId: null,
            targetComponent: OptimizationTargetComponent.PackagePolicy);
        var initial = (await agent.DiagnoseAsync(request)).Proposal!;

        // metric "unknown_metric" 不在 ExpectedGains 中 → 视为无信号，Status 不变
        var unmatchedEvidence = new[]
        {
            new ExperimentEvidence(
                source: "telemetry:external",
                metricName: "unknown_metric",
                baselineValue: 1.0,
                experimentValue: 2.0,
                sampleCount: 10,
                capturedAt: FixedTime)
        };

        var refined = await agent.RefineProposalAsync(initial, unmatchedEvidence);

        Assert.AreEqual(initial.Status, refined.Status, "未匹配 metric 不影响 Status");
        Assert.AreEqual(initial.Evidence.Count + 1, refined.Evidence.Count, "evidence 仍应被合并");
    }

    [TestMethod]
    public async Task RefineProposalAsync_AlreadyRejected_KeepsStatusRejected()
    {
        var source = new DefaultAgentObservationSource();
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        var request = new AgentDiagnosticRequest(
            workspaceId: "ws-rejected",
            collectionId: null,
            targetComponent: OptimizationTargetComponent.PackagePolicy);
        var initial = (await agent.DiagnoseAsync(request)).Proposal!;

        // 先驳回到 Rejected
        var contradiction = new[]
        {
            new ExperimentEvidence("benchmark:1", "duration_ms", 2329.0, 2500.0, 5, FixedTime)
        };
        var rejected = await agent.RefineProposalAsync(initial, contradiction);
        Assert.AreEqual(OptimizationProposalStatus.Rejected, rejected.Status);

        // 再提供支持证据 → 应保持 Rejected（语义不可逆）
        var support = new[]
        {
            new ExperimentEvidence("benchmark:2", "duration_ms", 2329.0, 2100.0, 5, FixedTime)
        };
        var refined = await agent.RefineProposalAsync(rejected, support);

        Assert.AreEqual(OptimizationProposalStatus.Rejected, refined.Status, "Rejected 不可逆");
        Assert.AreEqual(2, refined.Version.Minor, "版本号仍递增");
    }

    [TestMethod]
    public async Task RefineProposalAsync_PipelineStageStatus_Throws()
    {
        var source = new DefaultAgentObservationSource();
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        var pipelineStageProposal = new OptimizationProposal
        {
            ProposalId = "prop-pipeline",
            Version = OptimizationProposalVersion.Initial,
            Title = "T",
            Hypothesis = "H",
            TargetComponent = OptimizationTargetComponent.PackagePolicy,
            Status = OptimizationProposalStatus.Shadow,
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
                new RollbackCondition("duration_ms", ComparisonOperator.GreaterThan, 0.0, "no improvement")
            }
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await agent.RefineProposalAsync(pipelineStageProposal, Array.Empty<ExperimentEvidence>()));
    }

    [TestMethod]
    public async Task RefineProposalAsync_AdvanceRequiresAtLeastOneRollbackCondition()
    {
        // 构造一个无 RollbackConditions 的 Validated proposal（违反契约硬约束但用于验证 Agent 防御性）
        var source = new DefaultAgentObservationSource();
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        var noRollbackProposal = new OptimizationProposal
        {
            ProposalId = "prop-no-rollback",
            Version = OptimizationProposalVersion.Initial,
            Title = "T",
            Hypothesis = "H",
            TargetComponent = OptimizationTargetComponent.PackagePolicy,
            Status = OptimizationProposalStatus.Validated,
            ExpectedGains = new[]
            {
                new ExpectedGain("duration_ms", -350.0, 0.85, Array.Empty<string>())
            },
            Risks = new[]
            {
                new RiskAssessment("R1", "desc", RiskSeverity.Low, Array.Empty<string>(), Array.Empty<string>())
            },
            RollbackConditions = Array.Empty<RollbackCondition>() // 空
        };

        var supportingEvidence = new[]
        {
            new ExperimentEvidence("benchmark", "duration_ms", 2329.0, 2029.0, 10, FixedTime)
        };

        var refined = await agent.RefineProposalAsync(noRollbackProposal, supportingEvidence);

        Assert.AreEqual(OptimizationProposalStatus.Validated, refined.Status,
            "无 RollbackConditions 时即使方向一致也不推进到 ExperimentReady（契约硬约束）");
    }

    [TestMethod]
    public async Task DiagnoseAsync_NullRequest_Throws()
    {
        var agent = new DefaultContextEvolutionAgent(new DefaultAgentObservationSource());
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
            await agent.DiagnoseAsync(null!));
    }

    [TestMethod]
    public async Task RefineProposalAsync_NullArguments_Throw()
    {
        var agent = new DefaultContextEvolutionAgent(new DefaultAgentObservationSource());
        var proposal = new OptimizationProposal
        {
            ProposalId = "p", Version = OptimizationProposalVersion.Initial,
            Title = "T", Hypothesis = "H",
            TargetComponent = OptimizationTargetComponent.CachePolicy
        };

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
            await agent.RefineProposalAsync(null!, Array.Empty<ExperimentEvidence>()));
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
            await agent.RefineProposalAsync(proposal, null!));
    }

    [TestMethod]
    public async Task Constructor_NullObservationSource_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new DefaultContextEvolutionAgent(null!));
    }

    [TestMethod]
    public async Task DefaultAgentObservationSource_DefaultSourceId_IsTelemetryDefault()
    {
        var source = new DefaultAgentObservationSource();
        Assert.AreEqual("telemetry:default", source.SourceId);

        // 未写入指标时返回空字典
        var metrics = await source.ObserveAsync("ws-never-recorded", null);
        Assert.AreEqual(0, metrics.Count);
    }

    [TestMethod]
    public async Task DefaultAgentObservationSource_RecordAndObserve_Roundtrip()
    {
        var source = new DefaultAgentObservationSource("test:custom");
        Assert.AreEqual("test:custom", source.SourceId);

        await source.RecordMetricsAsync("ws-A", "col-X", new Dictionary<string, double>
        {
            ["metric1"] = 1.5,
            ["metric2"] = 2.5
        });

        var observed = await source.ObserveAsync("ws-A", "col-X");
        Assert.AreEqual(2, observed.Count);
        Assert.AreEqual(1.5, observed["metric1"]);
        Assert.AreEqual(2.5, observed["metric2"]);

        // 覆盖写入：第二次 Observe 返回新值
        await source.RecordMetricsAsync("ws-A", null, new Dictionary<string, double>
        {
            ["metric3"] = 3.0
        });
        var observed2 = await source.ObserveAsync("ws-A", null);
        Assert.AreEqual(1, observed2.Count, "新写入应覆盖旧值");
        Assert.AreEqual(3.0, observed2["metric3"]);
    }

    [TestMethod]
    public async Task DiagnoseAsync_OverMultipleComponents_ProducesDifferentProposalIds()
    {
        var source = new DefaultAgentObservationSource();
        var agent = new DefaultContextEvolutionAgent(source, new FakeTimeProvider(FixedTime));

        var request1 = new AgentDiagnosticRequest("ws", null, OptimizationTargetComponent.PackagePolicy);
        var request2 = new AgentDiagnosticRequest("ws", null, OptimizationTargetComponent.CachePolicy);

        var result1 = await agent.DiagnoseAsync(request1);
        var result2 = await agent.DiagnoseAsync(request2);

        Assert.AreNotEqual(result1.Proposal!.ProposalId, result2.Proposal!.ProposalId,
            "不同 target component 应产生不同 ProposalId");
    }

    /// <summary>简易 TimeProvider：返回固定或可推进的 UTC 时间。</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _current;

        public FakeTimeProvider(DateTimeOffset initial)
        {
            _current = initial;
        }

        public override DateTimeOffset GetUtcNow() => _current;
    }
}
