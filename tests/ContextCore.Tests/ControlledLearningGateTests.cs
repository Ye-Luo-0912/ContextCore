using ContextCore.Core.Services.Learning;
using ContextCore.Evaluation.Learning;

namespace ContextCore.Tests;

/// <summary>
/// 受控自学习测试。
/// 覆盖：学习边界（可学习表面白名单、禁止表面黑名单）；离线评测（test 隔离数据上
/// 基线 vs 候选对比、切片检查、内容寻址工件）；shadow/canary 门槛（样本门槛与
/// 版本固定/并行基线/kill switch/自动回滚前置条件）；Active 门槛（三项统计可信
/// 证据齐备才 Active，默认关闭并给出退回目标）。
/// </summary>
[TestClass]
[TestCategory("LR5A")]
[TestCategory("LR5B")]
[TestCategory("LR5C")]
[TestCategory("LR5D")]
[TestCategory("Learning")]
public sealed class ControlledLearningGateTests
{
    // ── 学习边界 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 验证：优先学习表面允许，禁止表面（权限/租户/排除/生命周期/安全 gate/迁移/持久化）拒绝。
    /// </summary>
    [TestMethod]
    public void LearningBoundary_AllowsLearnable_RejectsForbidden()
    {
        Assert.IsTrue(LearningBoundary.IsLearnable("QueryExpansionSelection"));
        Assert.IsTrue(LearningBoundary.IsLearnable("ChannelBudget"));
        Assert.IsTrue(LearningBoundary.IsLearnable("CandidateRerank"));
        Assert.IsTrue(LearningBoundary.IsLearnable("MemoryPromotionSuggestion"));

        Assert.IsFalse(LearningBoundary.IsLearnable("Permissions"), "权限不可学习。");
        Assert.IsFalse(LearningBoundary.IsLearnable("Tenant"), "租户不可学习。");
        Assert.IsFalse(LearningBoundary.IsLearnable("Exclusion"), "排除不可学习。");
        Assert.IsFalse(LearningBoundary.IsLearnable("Lifecycle"), "生命周期不可学习。");
        Assert.IsFalse(LearningBoundary.IsLearnable("SafetyGate"), "安全 gate 不可学习。");
        Assert.IsFalse(LearningBoundary.IsLearnable("Migration"), "迁移不可学习。");
        Assert.IsFalse(LearningBoundary.IsLearnable("Persistence"), "持久化不可学习。");
        Assert.IsFalse(LearningBoundary.IsLearnable("UnknownSurface"));
        Assert.IsFalse(LearningBoundary.IsLearnable(string.Empty));
    }

    /// <summary>
    /// 验证：批量校验返回被边界禁止的表面清单。
    /// </summary>
    [TestMethod]
    public void LearningBoundary_Validate_ReturnsForbiddenSurfaces()
    {
        var violations = LearningBoundary.Validate(
        [
            "QueryExpansionSelection",
            "SafetyGate",
            "CandidateRerank",
            "Permissions"
        ]);

        CollectionAssert.AreEqual(new[] { "SafetyGate", "Permissions" }, violations.ToArray(),
            "只应返回被禁止的表面（保持输入顺序）。");
    }

    // ── 离线训练与评测 ────────────────────────────────────────────────────

    /// <summary>
    /// 验证：test 隔离数据上计算基线 vs 候选准确率、切片差异与内容寻址工件。
    /// </summary>
    [TestMethod]
    public void OfflineEvaluation_ComparesBaselineAndCandidate_WithSlices()
    {
        var baseline = new[]
        {
            new OfflineEvaluationPrediction("e1", true),
            new OfflineEvaluationPrediction("e2", true),
            new OfflineEvaluationPrediction("e3", true),
            new OfflineEvaluationPrediction("e4", false),
            new OfflineEvaluationPrediction("e5", false)
        };
        var candidate = new[]
        {
            new OfflineEvaluationPrediction("e1", true),
            new OfflineEvaluationPrediction("e2", true),
            new OfflineEvaluationPrediction("e3", true),
            new OfflineEvaluationPrediction("e4", true),
            new OfflineEvaluationPrediction("e5", false)
        };
        var slices = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["e1"] = "tenant:alpha",
            ["e2"] = "tenant:alpha",
            ["e3"] = "hard-negative",
            ["e4"] = "hard-negative",
            ["e5"] = "language:zh"
        };

        var result = OfflineLearningEvaluationHarness.Evaluate(
            baseline,
            candidate,
            slices,
            snapshotId: "snap_123",
            featureSchemaVersion: "features/v1",
            codeVersion: "code-1.0");

        Assert.AreEqual(5, result.SampleCount);
        Assert.AreEqual(0.6, result.BaselineAccuracy, 1e-9);
        Assert.AreEqual(0.8, result.CandidateAccuracy, 1e-9);
        Assert.AreEqual(0.2, result.Improvement, 1e-9);
        Assert.IsTrue(result.CandidateBetterOrEqual);

        var hardNegative = result.Slices.Single(slice => slice.Name == "hard-negative");
        Assert.AreEqual(2, hardNegative.SampleCount);
        Assert.AreEqual(0.5, hardNegative.BaselineAccuracy, 1e-9);
        Assert.AreEqual(1.0, hardNegative.CandidateAccuracy, 1e-9);

        var tenant = result.Slices.Single(slice => slice.Name == "tenant:alpha");
        Assert.AreEqual(2, tenant.SampleCount);

        var artifact = result.Slices.Single(slice => slice.Name == "overall");
        Assert.AreEqual(5, artifact.SampleCount);
        Assert.AreEqual("snap_123", result.SnapshotId, "工件必须能追到输入快照。");
        Assert.AreEqual("features/v1", result.FeatureSchemaVersion);
        Assert.AreEqual("code-1.0", result.CodeVersion);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ArtifactId));
    }

    /// <summary>
    /// 验证：相同输入产出相同工件 ID；特征 schema 或数据版本变化产出不同 ID（可重复构建）。
    /// </summary>
    [TestMethod]
    public void OfflineEvaluation_ArtifactId_IsContentAddressed()
    {
        var baseline = new[] { new OfflineEvaluationPrediction("e1", true) };
        var candidate = new[] { new OfflineEvaluationPrediction("e1", true) };
        var slices = new Dictionary<string, string>(StringComparer.Ordinal) { ["e1"] = "overall" };

        var first = OfflineLearningEvaluationHarness.Evaluate(baseline, candidate, slices, "snap_1", "features/v1", "code-1");
        var second = OfflineLearningEvaluationHarness.Evaluate(baseline, candidate, slices, "snap_1", "features/v1", "code-1");
        var changedSchema = OfflineLearningEvaluationHarness.Evaluate(baseline, candidate, slices, "snap_1", "features/v2", "code-1");
        var changedData = OfflineLearningEvaluationHarness.Evaluate(baseline, candidate, slices, "snap_2", "features/v1", "code-1");

        Assert.AreEqual(first.ArtifactId, second.ArtifactId);
        Assert.AreNotEqual(first.ArtifactId, changedSchema.ArtifactId, "特征 schema 变化应改变工件 ID。");
        Assert.AreNotEqual(first.ArtifactId, changedData.ArtifactId, "数据版本变化应改变工件 ID。");
    }

    /// <summary>
    /// 验证：基线候选预测条数不一致时拒绝评估。
    /// </summary>
    [TestMethod]
    public void OfflineEvaluation_MismatchedPredictions_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            OfflineLearningEvaluationHarness.Evaluate(
                [new OfflineEvaluationPrediction("e1", true)],
                [new OfflineEvaluationPrediction("e1", true), new OfflineEvaluationPrediction("e2", false)],
                new Dictionary<string, string>(StringComparer.Ordinal),
                "snap_1",
                "features/v1"));
    }

    // ── Shadow 与 canary 门槛 ─────────────────────────────────────────────

    /// <summary>
    /// 验证：样本未达门槛且前置条件缺失时停留在 shadow；齐备后进入 canary。
    /// </summary>
    [TestMethod]
    public void ShadowCanary_BlocksUntilPrerequisitesMet()
    {
        var config = new RolloutGateConfig(
            ShadowSampleThreshold: 100,
            CanaryTrafficFraction: 0.05,
            VersionPinned: true,
            ParallelBaseline: true,
            KillSwitchEnabled: true,
            AutoRollbackEnabled: true);

        var notStarted = ShadowCanaryGate.Evaluate(config, 0);
        Assert.AreEqual(RolloutStage.NotStarted, notStarted.Stage);
        Assert.IsFalse(notStarted.ReadyForCanary);

        var shadow = ShadowCanaryGate.Evaluate(config, 50);
        Assert.AreEqual(RolloutStage.Shadow, shadow.Stage);
        Assert.IsFalse(shadow.ReadyForCanary);
        Assert.IsTrue(shadow.Blockers.Any(blocker => blocker.Contains("门槛", StringComparison.Ordinal)));

        var canary = ShadowCanaryGate.Evaluate(config, 120);
        Assert.AreEqual(RolloutStage.Canary, canary.Stage);
        Assert.IsTrue(canary.ReadyForCanary);
        Assert.AreEqual(0, canary.Blockers.Count);
    }

    /// <summary>
    /// 验证：kill switch / 自动回滚 / 版本固定缺失时即使样本达标也阻断 canary。
    /// </summary>
    [TestMethod]
    public void ShadowCanary_ReportsMissingSafetyPrerequisites()
    {
        var config = new RolloutGateConfig(
            ShadowSampleThreshold: 10,
            CanaryTrafficFraction: 0.05,
            VersionPinned: false,
            ParallelBaseline: true,
            KillSwitchEnabled: false,
            AutoRollbackEnabled: true);

        var decision = ShadowCanaryGate.Evaluate(config, 500);

        Assert.IsFalse(decision.ReadyForCanary, "缺少 kill switch 与版本固定时不得进入 canary。");
        Assert.AreEqual(RolloutStage.Shadow, decision.Stage);
        Assert.IsTrue(decision.Blockers.Any(blocker => blocker.Contains("kill switch", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(decision.Blockers.Any(blocker => blocker.Contains("版本未固定", StringComparison.Ordinal)));
    }

    // ── Active 门槛 ───────────────────────────────────────────────────────

    /// <summary>
    /// 验证：三项统计可信证据齐备才允许 Active。
    /// </summary>
    [TestMethod]
    public void ActiveGate_RequiresAllThreeEvidence()
    {
        var active = ActiveLearningGate.Evaluate(
            new ActiveLearningEvidence(RecallImproved: true, TaskSuccessImproved: true, SafetyGatesPreserved: true),
            stablePolicyRef: "deterministic/v3");

        Assert.IsTrue(active.Active);
        Assert.AreEqual(0, active.Missing.Count);

        var missingRecall = ActiveLearningGate.Evaluate(
            new ActiveLearningEvidence(RecallImproved: false, TaskSuccessImproved: true, SafetyGatesPreserved: true),
            stablePolicyRef: "deterministic/v3");

        Assert.IsFalse(missingRecall.Active, "Recall 无统计可信提升时不得 Active。");
        CollectionAssert.Contains(missingRecall.Missing.ToArray(), "Required-Evidence Recall@TokenBudget 无统计可信提升。");

        var safetyRegression = ActiveLearningGate.Evaluate(
            new ActiveLearningEvidence(RecallImproved: true, TaskSuccessImproved: true, SafetyGatesPreserved: false),
            stablePolicyRef: "deterministic/v3");

        Assert.IsFalse(safetyRegression.Active, "安全门回退时不得 Active。");
        Assert.AreEqual("deterministic/v3", safetyRegression.RollbackTo, "失败时应退回上一稳定策略。");
    }

    /// <summary>
    /// 验证：默认无证据时保持关闭。
    /// </summary>
    [TestMethod]
    public void ActiveGate_DefaultsToClosed()
    {
        var decision = ActiveLearningGate.Evaluate(
            new ActiveLearningEvidence(RecallImproved: false, TaskSuccessImproved: false, SafetyGatesPreserved: false),
            stablePolicyRef: "deterministic/v3");

        Assert.IsFalse(decision.Active, "门槛默认保持 Active 关闭。");
        Assert.AreEqual(3, decision.Missing.Count);
        Assert.AreEqual("deterministic/v3", decision.RollbackTo);
    }
}
