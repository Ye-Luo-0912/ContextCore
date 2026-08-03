using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.BoundedContext;

namespace ContextCore.Tests;

/// <summary>
/// DefaultContextRepairDetector 实现测试。
///
/// 覆盖：
/// 1. null 输入处理（decision null 抛异常 / qualityReport null 返回空列表）
/// 2. 7 类异常独立检测（每类触发一个 Diagnosis）
/// 3. 7 类异常同时触发返回 7 个 Diagnosis
/// 4. 所有指标都满足阈值时返回空列表
/// 5. 自定义阈值覆盖默认值
/// 6. 阈值范围 [0,1] 校验（ArgumentOutOfRangeException）
/// 7. WorkspaceId/CollectionId 从 envelope 推导（Selected → Dropped → Empty）
/// 8. Diagnosis 字段映射（Reason/ReasonDetail/TriggerMetricValue/TriggerMetricThreshold/
/// QualityReport/SuggestedRepairStrategy/DiagnosedAt/DiagnosisId 格式）
/// 9. CancellationToken 传递
/// </summary>
[TestClass]
[TestCategory("R22")]
public sealed class DefaultContextRepairDetectorTests
{
    // =========================================================================
    // 1. null 输入处理
    // =========================================================================

    [TestMethod]
    public async Task DetectAsync_NullDecision_Throws()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport();

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => detector.DetectAsync(null!, report));
    }

    [TestMethod]
    public async Task DetectAsync_NullQualityReport_ReturnsEmptyList()
    {
        var detector = new DefaultContextRepairDetector();
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, null);

        Assert.AreEqual(0, diagnoses.Count);
    }

    // =========================================================================
    // 2. 7 类异常独立检测
    // =========================================================================

    [TestMethod]
    public async Task DetectAsync_AnchorCoverageBelowThreshold_DetectsPrimaryAnchorUncovered()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(anchorCoverage: 0.40);
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        Assert.AreEqual(ContextRepairReason.PrimaryAnchorUncovered, diagnoses[0].Reason);
        Assert.AreEqual(0.40, diagnoses[0].TriggerMetricValue);
        Assert.AreEqual(DefaultContextRepairDetector.DefaultAnchorCoverageThreshold, diagnoses[0].TriggerMetricThreshold);
        Assert.AreEqual("re-retrieve-anchor-coverage", diagnoses[0].SuggestedRepairStrategy);
    }

    [TestMethod]
    public async Task DetectAsync_HardConstraintBelowThreshold_DetectsHardConstraintMissing()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(hardConstraint: 0.50);
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        Assert.AreEqual(ContextRepairReason.HardConstraintMissing, diagnoses[0].Reason);
        Assert.AreEqual(0.50, diagnoses[0].TriggerMetricValue);
        Assert.AreEqual("inject-missing-hard-constraint", diagnoses[0].SuggestedRepairStrategy);
    }

    [TestMethod]
    public async Task DetectAsync_RequiredItemCoverageBelowThreshold_DetectsMustHitMissing()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(requiredItem: 0.50);
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        Assert.AreEqual(ContextRepairReason.MustHitMissing, diagnoses[0].Reason);
        Assert.AreEqual("re-retrieve-must-hit", diagnoses[0].SuggestedRepairStrategy);
    }

    [TestMethod]
    public async Task DetectAsync_RedundancyBelowThreshold_DetectsSevereRedundancy()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(redundancy: 0.30);
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        Assert.AreEqual(ContextRepairReason.SevereRedundancy, diagnoses[0].Reason);
        Assert.AreEqual("drop-redundant", diagnoses[0].SuggestedRepairStrategy);
    }

    [TestMethod]
    public async Task DetectAsync_SectionBalanceBelowThreshold_DetectsSectionSqueezeAnomaly()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(sectionBalance: 0.20);
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        Assert.AreEqual(ContextRepairReason.SectionSqueezeAnomaly, diagnoses[0].Reason);
        Assert.AreEqual("rebalance-sections", diagnoses[0].SuggestedRepairStrategy);
    }

    [TestMethod]
    public async Task DetectAsync_TokenEfficiencyBelowThreshold_DetectsTokenUtilizationTooLow()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(tokenEfficiency: 0.10);
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        Assert.AreEqual(ContextRepairReason.TokenUtilizationTooLow, diagnoses[0].Reason);
        Assert.AreEqual("expand-candidate-pool", diagnoses[0].SuggestedRepairStrategy);
    }

    [TestMethod]
    public async Task DetectAsync_LifecycleRiskBelowThreshold_DetectsLifecycleConflictUnresolved()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(lifecycleRisk: 0.50);
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        Assert.AreEqual(ContextRepairReason.LifecycleConflictUnresolved, diagnoses[0].Reason);
        Assert.AreEqual("resolve-lifecycle-conflict", diagnoses[0].SuggestedRepairStrategy);
    }

    // =========================================================================
    // 3. 7 类异常同时触发
    // =========================================================================

    [TestMethod]
    public async Task DetectAsync_AllMetricsBelowThreshold_Returns7Diagnoses()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(
            anchorCoverage: 0.0,
            hardConstraint: 0.0,
            requiredItem: 0.0,
            redundancy: 0.0,
            sectionBalance: 0.0,
            tokenEfficiency: 0.0,
            lifecycleRisk: 0.0);
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(7, diagnoses.Count);
        var reasons = diagnoses.Select(d => d.Reason).ToHashSet();
        Assert.IsTrue(reasons.Contains(ContextRepairReason.PrimaryAnchorUncovered));
        Assert.IsTrue(reasons.Contains(ContextRepairReason.HardConstraintMissing));
        Assert.IsTrue(reasons.Contains(ContextRepairReason.MustHitMissing));
        Assert.IsTrue(reasons.Contains(ContextRepairReason.SevereRedundancy));
        Assert.IsTrue(reasons.Contains(ContextRepairReason.SectionSqueezeAnomaly));
        Assert.IsTrue(reasons.Contains(ContextRepairReason.TokenUtilizationTooLow));
        Assert.IsTrue(reasons.Contains(ContextRepairReason.LifecycleConflictUnresolved));
    }

    // =========================================================================
    // 4. 所有指标都满足阈值时返回空列表
    // =========================================================================

    [TestMethod]
    public async Task DetectAsync_AllMetricsAboveThreshold_ReturnsEmptyList()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(
            anchorCoverage: 1.0,
            hardConstraint: 1.0,
            requiredItem: 1.0,
            redundancy: 1.0,
            sectionBalance: 1.0,
            tokenEfficiency: 1.0,
            lifecycleRisk: 1.0);
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(0, diagnoses.Count);
    }

    // =========================================================================
    // 5. 自定义阈值覆盖默认值
    // =========================================================================

    [TestMethod]
    public async Task DetectAsync_CustomThresholds_OverrideDefaults()
    {
        // 默认 AnchorCoverage 阈值 = 0.80；score = 0.85 默认不触发
        // 自定义阈值 = 0.90 → 触发
        var detector = new DefaultContextRepairDetector(anchorCoverageThreshold: 0.90);
        var report = MakeQualityReport(anchorCoverage: 0.85);
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        Assert.AreEqual(ContextRepairReason.PrimaryAnchorUncovered, diagnoses[0].Reason);
        Assert.AreEqual(0.90, diagnoses[0].TriggerMetricThreshold);
    }

    [TestMethod]
    public async Task DetectAsync_ThresholdExactlyAtBoundary_DoesNotTrigger()
    {
        // 阈值边界 = score == threshold 不触发（严格小于）
        var detector = new DefaultContextRepairDetector(anchorCoverageThreshold: 0.80);
        var report = MakeQualityReport(anchorCoverage: 0.80);
        var decision = MakeDecisionResult();

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(0, diagnoses.Count);
    }

    // =========================================================================
    // 6. 阈值范围 [0,1] 校验
    // =========================================================================

    [TestMethod]
    public void Constructor_AnchorCoverageThresholdOutOfRange_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new DefaultContextRepairDetector(anchorCoverageThreshold: 1.5));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new DefaultContextRepairDetector(anchorCoverageThreshold: -0.1));
    }

    [TestMethod]
    public void Constructor_HardConstraintThresholdOutOfRange_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new DefaultContextRepairDetector(hardConstraintSatisfactionThreshold: 2.0));
    }

    [TestMethod]
    public void Constructor_TokenEfficiencyThresholdOutOfRange_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new DefaultContextRepairDetector(tokenEfficiencyThreshold: -0.5));
    }

    [TestMethod]
    public void Constructor_LifecycleRiskThresholdOutOfRange_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new DefaultContextRepairDetector(lifecycleRiskThreshold: 1.01));
    }

    [TestMethod]
    public void Constructor_AllThresholdsAtZero_Allowed()
    {
        // 阈值 = 0 表示不触发任何异常（除非 score < 0，不可能）
        var detector = new DefaultContextRepairDetector(
            anchorCoverageThreshold: 0.0,
            hardConstraintSatisfactionThreshold: 0.0,
            requiredItemCoverageThreshold: 0.0,
            redundancyThreshold: 0.0,
            sectionBalanceThreshold: 0.0,
            tokenEfficiencyThreshold: 0.0,
            lifecycleRiskThreshold: 0.0);
        var report = MakeQualityReport(anchorCoverage: 0.0);
        var decision = MakeDecisionResult();

        var diagnoses = detector.DetectAsync(decision, report).GetAwaiter().GetResult();

        Assert.AreEqual(0, diagnoses.Count);
    }

    // =========================================================================
    // 7. WorkspaceId/CollectionId 从 envelope 推导
    // =========================================================================

    [TestMethod]
    public async Task DetectAsync_ScopeExtractedFromFirstSelectedEnvelope()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(anchorCoverage: 0.40);
        var decision = MakeDecisionResult(workspaceId: "ws-from-selected", collectionId: "col-from-selected");

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        Assert.AreEqual("ws-from-selected", diagnoses[0].WorkspaceId);
        Assert.AreEqual("col-from-selected", diagnoses[0].CollectionId);
    }

    [TestMethod]
    public async Task DetectAsync_ScopeFallsBackToFirstDroppedEnvelope()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(anchorCoverage: 0.40);
        // 没有 SelectedEnvelopes，但 DroppedEnvelopes 有
        var decision = MakeDecisionResult(
            workspaceId: "ws-from-dropped",
            collectionId: "col-from-dropped",
            selectedCount: 0,
            droppedCount: 1);

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        Assert.AreEqual("ws-from-dropped", diagnoses[0].WorkspaceId);
        Assert.AreEqual("col-from-dropped", diagnoses[0].CollectionId);
    }

    [TestMethod]
    public async Task DetectAsync_ScopeEmpty_WhenNoEnvelopes()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(anchorCoverage: 0.40);
        var decision = MakeDecisionResult(selectedCount: 0, droppedCount: 0);

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        Assert.AreEqual(string.Empty, diagnoses[0].WorkspaceId);
        Assert.AreEqual(string.Empty, diagnoses[0].CollectionId);
    }

    // =========================================================================
    // 8. Diagnosis 字段映射
    // =========================================================================

    [TestMethod]
    public async Task DetectAsync_DiagnosisFields_AreCorrectlyPopulated()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(anchorCoverage: 0.40);
        var decision = MakeDecisionResult(requestId: "req-test-123");

        var diagnoses = await detector.DetectAsync(decision, report);

        Assert.AreEqual(1, diagnoses.Count);
        var d = diagnoses[0];
        Assert.IsTrue(d.DiagnosisId.StartsWith("diag-", StringComparison.Ordinal));
        Assert.AreEqual("req-test-123", d.DecisionRequestId);
        Assert.AreEqual(ContextRepairReason.PrimaryAnchorUncovered, d.Reason);
        Assert.AreEqual(0.40, d.TriggerMetricValue);
        Assert.AreEqual(DefaultContextRepairDetector.DefaultAnchorCoverageThreshold, d.TriggerMetricThreshold);
        Assert.IsNotNull(d.QualityReport);
        Assert.AreSame(report, d.QualityReport);
        Assert.AreEqual("re-retrieve-anchor-coverage", d.SuggestedRepairStrategy);
        Assert.IsTrue(d.DiagnosedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.IsTrue(d.DiagnosedAt <= DateTimeOffset.UtcNow);
        Assert.IsTrue(d.ReasonDetail.Contains("AnchorCoverage", StringComparison.Ordinal));
        Assert.IsTrue(d.ReasonDetail.Contains("0.4000", StringComparison.Ordinal));
        Assert.IsTrue(d.ReasonDetail.Contains("0.8000", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DetectAsync_DiagnosisId_UniquePerCall()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(anchorCoverage: 0.40);
        var decision = MakeDecisionResult();

        var d1 = await detector.DetectAsync(decision, report);
        var d2 = await detector.DetectAsync(decision, report);

        Assert.AreNotEqual(d1[0].DiagnosisId, d2[0].DiagnosisId);
    }

    // =========================================================================
    // 9. CancellationToken 传递
    // =========================================================================

    [TestMethod]
    public async Task DetectAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var detector = new DefaultContextRepairDetector();
        var report = MakeQualityReport(anchorCoverage: 0.40);
        var decision = MakeDecisionResult();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => detector.DetectAsync(decision, report, cts.Token));
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static ContextDecisionResult MakeDecisionResult(
        string requestId = "req-1",
        string workspaceId = "ws-test",
        string collectionId = "col-test",
        int selectedCount = 1,
        int droppedCount = 0)
    {
        var selected = new List<ContextCandidateEnvelope>(selectedCount);
        for (var i = 0; i < selectedCount; i++)
        {
            selected.Add(MakeEnvelope($"sel-{i}", workspaceId, collectionId));
        }
        var dropped = new List<ContextCandidateEnvelope>(droppedCount);
        for (var i = 0; i < droppedCount; i++)
        {
            dropped.Add(MakeEnvelope($"drop-{i}", workspaceId, collectionId));
        }

        return new ContextDecisionResult
        {
            RequestId = requestId,
            DecisionSource = ContextDecisionSource.Package,
            SelectedEnvelopes = selected,
            DroppedEnvelopes = dropped,
            PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            ModelEnabled = false
        };
    }

    private static ContextCandidateEnvelope MakeEnvelope(
        string candidateId,
        string workspaceId,
        string collectionId)
    {
        return new ContextCandidateEnvelope
        {
            CandidateId = candidateId,
            CanonicalKey = CanonicalCandidateKey.Create(
                workspaceId: "test-ws",
                collectionId: "test-col",
                entityKind: "test-entity",
                entityId: candidateId,
                entityVersion: "v1"),
            Source = ContextCandidateSource.Semantic,
            WorkspaceId = workspaceId,
            CollectionId = collectionId
        };
    }

    /// <summary>
    /// 构造 PackageQualityReport。默认所有指标 score = 1.0（不触发任何异常）。
    /// 仅显式指定的字段被覆盖为低值以触发对应异常。
    /// </summary>
    private static PackageQualityReport MakeQualityReport(
        double anchorCoverage = 1.0,
        double hardConstraint = 1.0,
        double requiredItem = 1.0,
        double redundancy = 1.0,
        double sectionBalance = 1.0,
        double tokenEfficiency = 1.0,
        double lifecycleRisk = 1.0)
    {
        return new PackageQualityReport
        {
            AnchorCoverage = MakeMetric("AnchorCoverage", anchorCoverage),
            HardConstraintSatisfaction = MakeMetric("HardConstraintSatisfaction", hardConstraint),
            RequiredItemCoverage = MakeMetric("RequiredItemCoverage", requiredItem),
            Redundancy = MakeMetric("Redundancy", redundancy),
            ProvenanceCompleteness = MakeMetric("ProvenanceCompleteness", 1.0),
            LifecycleRisk = MakeMetric("LifecycleRisk", lifecycleRisk),
            TokenEfficiency = MakeMetric("TokenEfficiency", tokenEfficiency),
            SectionBalance = MakeMetric("SectionBalance", sectionBalance),
            OverallScore = Math.Min(
                Math.Min(anchorCoverage, hardConstraint),
                Math.Min(Math.Min(requiredItem, redundancy), Math.Min(sectionBalance, Math.Min(tokenEfficiency, lifecycleRisk)))),
            ComputedAt = DateTimeOffset.UtcNow
        };
    }

    private static PackageQualityMetric MakeMetric(string name, double score)
    {
        return new PackageQualityMetric
        {
            Name = name,
            Score = score,
            Numerator = 0,
            Denominator = 1,
            Detail = $"{name}={score:F2}"
        };
    }
}
