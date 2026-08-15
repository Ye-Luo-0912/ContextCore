using ContextCore.Abstractions.Models;
using ContextCore.Evaluation.Learning;

namespace ContextCore.Tests;

/// <summary>
/// 无学习的策略改进测试。
/// 覆盖：带归因的反馈事件翻译为显式规则修补（补查询/找回/已提供），工具自身失败不产生规则；
/// 撤销反馈不驱动规则；同类型同目标去重；训练必要性门——确定性规则能解决全部失败时不需要训练。
/// </summary>
[TestClass]
[TestCategory("LR4D")]
[TestCategory("Learning")]
public sealed class FeedbackDrivenPolicyPatchTests
{
    /// <summary>
    /// 验证：未召回/未选中/未使用分别映射为补查询/找回/上下文已提供规则；
    /// 工具自身失败不产生规则；每条修补带来源 lineage 与稳定 PatchId。
    /// </summary>
    [TestMethod]
    public void Build_FeedbackKinds_MapToRulePatches()
    {
        var events = new[]
        {
            CreateEvent("lfb_recall", LearningFeedbackKinds.EvidenceNotRecalled, targetId: "entity-a"),
            CreateEvent("lfb_select", LearningFeedbackKinds.RecalledNotSelected, targetId: "entity-b"),
            CreateEvent("lfb_use", LearningFeedbackKinds.SelectedNotUsed, targetId: "entity-c"),
            CreateEvent("lfb_tool", LearningFeedbackKinds.ToolFailed, targetId: "entity-d")
        };

        var patches = FeedbackDrivenPolicyPatchBuilder.Build(events);

        Assert.AreEqual(3, patches.Count, "工具自身失败不应产生规则。");
        var queryClaim = patches.Single(patch => patch.Kind == FeedbackRulePatchKind.QueryClaim);
        var recoveryGoal = patches.Single(patch => patch.Kind == FeedbackRulePatchKind.RecoveryGoal);
        var contextUsage = patches.Single(patch => patch.Kind == FeedbackRulePatchKind.ContextUsage);

        Assert.AreEqual("entity-a", queryClaim.TargetId);
        Assert.AreEqual("lfb_recall", queryClaim.SourceFeedbackId, "修补应携带来源反馈 lineage。");
        Assert.AreEqual("entity-b", recoveryGoal.TargetId);
        Assert.AreEqual("entity-c", contextUsage.TargetId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(queryClaim.PatchId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(queryClaim.Reason));
    }

    /// <summary>
    /// 验证：被撤销的反馈不再驱动任何规则修补。
    /// </summary>
    [TestMethod]
    public void Build_RevokedFeedback_DoesNotDriveRules()
    {
        var events = new[]
        {
            CreateEvent("lfb_original", LearningFeedbackKinds.EvidenceNotRecalled, targetId: "entity-a"),
            CreateEvent("lfb_revoke", LearningFeedbackKinds.Revoke, targetId: "entity-a", revokesFeedbackId: "lfb_original")
        };

        var patches = FeedbackDrivenPolicyPatchBuilder.Build(events);

        Assert.AreEqual(0, patches.Count, "撤销反馈不应驱动规则。");
    }

    /// <summary>
    /// 验证：同一类型同一目标的重复反馈去重，置信度取最高值。
    /// </summary>
    [TestMethod]
    public void Build_DeduplicatesSameKindAndTarget()
    {
        var events = new[]
        {
            CreateEvent("lfb_weak", LearningFeedbackKinds.RecalledNotSelected, targetId: "entity-b", source: "tool"),
            CreateEvent("lfb_human", LearningFeedbackKinds.RecalledNotSelected, targetId: "entity-b", source: "human")
        };

        var patches = FeedbackDrivenPolicyPatchBuilder.Build(events);

        Assert.AreEqual(1, patches.Count, "同类型同目标应去重。");
        Assert.AreEqual(1.0, patches[0].Confidence, "人工金标置信度更高，应保留。");
        Assert.AreEqual("lfb_human", patches[0].SourceFeedbackId);
    }

    /// <summary>
    /// 验证：确定性规则能覆盖全部失败时，训练不必要（不为"用了 AI"额外训练模型）。
    /// </summary>
    [TestMethod]
    public void Gate_AllAddressable_TrainingNotNeeded()
    {
        var events = new[]
        {
            CreateEvent("lfb_1", LearningFeedbackKinds.EvidenceNotRecalled, targetId: "entity-a"),
            CreateEvent("lfb_2", LearningFeedbackKinds.RecalledNotSelected, targetId: "entity-b"),
            CreateEvent("lfb_3", LearningFeedbackKinds.SelectedNotUsed, targetId: "entity-c")
        };
        var patches = FeedbackDrivenPolicyPatchBuilder.Build(events);

        var decision = FeedbackDrivenPolicyPatchBuilder.DecideTrainingNecessity(events, patches);

        Assert.IsTrue(decision.TrainingNotNeeded, "全部失败可确定性修复时不应训练。");
        Assert.AreEqual(3, decision.DeterministicallyAddressable);
        Assert.AreEqual(0, decision.NotDeterministicallyAddressable);
    }

    /// <summary>
    /// 验证：存在工具自身失败（确定性规则无法修复）时，才考虑训练（仍需学习门槛）。
    /// </summary>
    [TestMethod]
    public void Gate_ToolFailuresPresent_TrainingMayBeConsidered()
    {
        var events = new[]
        {
            CreateEvent("lfb_1", LearningFeedbackKinds.EvidenceNotRecalled, targetId: "entity-a"),
            CreateEvent("lfb_2", LearningFeedbackKinds.ToolFailed, targetId: "entity-d")
        };
        var patches = FeedbackDrivenPolicyPatchBuilder.Build(events);

        var decision = FeedbackDrivenPolicyPatchBuilder.DecideTrainingNecessity(events, patches);

        Assert.IsFalse(decision.TrainingNotNeeded, "存在工具自身失败时训练才可能被考虑。");
        Assert.AreEqual(1, decision.DeterministicallyAddressable);
        Assert.AreEqual(1, decision.NotDeterministicallyAddressable);
    }

    /// <summary>
    /// 验证：被撤销的失败不计入训练必要性判断。
    /// </summary>
    [TestMethod]
    public void Gate_RevokedFailures_DoNotCount()
    {
        var events = new[]
        {
            CreateEvent("lfb_original", LearningFeedbackKinds.EvidenceNotRecalled, targetId: "entity-a"),
            CreateEvent("lfb_revoke", LearningFeedbackKinds.Revoke, targetId: "entity-a", revokesFeedbackId: "lfb_original")
        };

        var decision = FeedbackDrivenPolicyPatchBuilder.DecideTrainingNecessity(
            events,
            FeedbackDrivenPolicyPatchBuilder.Build(events));

        Assert.IsTrue(decision.TrainingNotNeeded);
        Assert.AreEqual(0, decision.DeterministicallyAddressable + decision.NotDeterministicallyAddressable);
    }

    // ── 构造 ────────────────────────────────────────────────────────────────

    private static LearningFeedbackEvent CreateEvent(
        string feedbackId,
        string kind,
        string targetId,
        string source = "tool",
        string revokesFeedbackId = "")
        => new()
        {
            FeedbackId = feedbackId,
            WorkspaceId = "ws-patch",
            CollectionId = "col-patch",
            Source = source,
            SourceOperationId = $"operation-{feedbackId}",
            CapabilityId = ShadowCapabilityIds.VectorRetrieval,
            TargetId = targetId,
            TargetType = LearningFeedbackTargetType.VectorCandidate.ToString(),
            FeedbackKind = kind,
            PolicyVersion = "policy-v1",
            RevokesFeedbackId = revokesFeedbackId
        };
}
