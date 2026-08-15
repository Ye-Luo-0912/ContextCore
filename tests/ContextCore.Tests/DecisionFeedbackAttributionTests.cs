using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Learning;

namespace ContextCore.Tests;

/// <summary>
/// 决策失败归因与标签质量测试。
/// 覆盖：四类失败模式（未召回/未选中/未使用/工具失败）的确定性区分；
/// 成功工具观察只提供正向线索、失败确认 ID 只提供排除事实；
/// 弱标签带来源与置信度，存在人工金标时不得等权；偏差监控量化三类风险。
/// </summary>
[TestClass]
[TestCategory("LR4B")]
[TestCategory("Learning")]
public sealed class DecisionFeedbackAttributionTests
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 验证：需要但从未进入候选集的目标归为「证据存在但没召回」。
    /// </summary>
    [TestMethod]
    public void Classify_NeededIdNeverInCandidates_IsEvidenceNotRecalled()
    {
        var result = DecisionFailureAttribution.Classify(
            ["entity-missing"],
            recalledIds: Empty,
            selectedIds: Empty,
            excludedIds: Empty,
            toolCallErrored: false);

        Assert.AreEqual(DecisionFailureMode.EvidenceNotRecalled, result.Mode);
        Assert.AreEqual(LearningFeedbackKinds.EvidenceNotRecalled, result.FeedbackKind);
        Assert.AreEqual(0.6, result.Confidence);
        Assert.AreEqual(DecisionFailureAttribution.SourceTool, result.Source);
        Assert.IsTrue(result.HasNegativeLabel);
        CollectionAssert.Contains(result.EvidenceIds.ToArray(), "entity-missing");
    }

    /// <summary>
    /// 验证：进入候选集但被分配器裁掉的目标归为「召回但没选」。
    /// </summary>
    [TestMethod]
    public void Classify_NeededIdRecalledButNotSelected_IsRecalledNotSelected()
    {
        var result = DecisionFailureAttribution.Classify(
            ["entity-dropped"],
            recalledIds: new HashSet<string>(["entity-dropped"], StringComparer.Ordinal),
            selectedIds: Empty,
            excludedIds: Empty,
            toolCallErrored: false);

        Assert.AreEqual(DecisionFailureMode.RecalledNotSelected, result.Mode);
        Assert.AreEqual(LearningFeedbackKinds.RecalledNotSelected, result.FeedbackKind);
        Assert.AreEqual(0.8, result.Confidence);
    }

    /// <summary>
    /// 验证：已选中进入上下文但模型仍去工具里找的目标归为「已选但模型未使用」。
    /// </summary>
    [TestMethod]
    public void Classify_NeededIdSelected_IsSelectedNotUsed()
    {
        var result = DecisionFailureAttribution.Classify(
            ["entity-selected"],
            recalledIds: new HashSet<string>(["entity-selected"], StringComparer.Ordinal),
            selectedIds: new HashSet<string>(["entity-selected"], StringComparer.Ordinal),
            excludedIds: Empty,
            toolCallErrored: false);

        Assert.AreEqual(DecisionFailureMode.SelectedNotUsed, result.Mode);
        Assert.AreEqual(LearningFeedbackKinds.SelectedNotUsed, result.FeedbackKind);
        Assert.AreEqual(0.7, result.Confidence);
    }

    /// <summary>
    /// 验证：工具调用自身报错优先归为「工具自身失败」，不归因到证据链路。
    /// </summary>
    [TestMethod]
    public void Classify_ToolError_TakesPrecedence()
    {
        var result = DecisionFailureAttribution.Classify(
            ["entity-any"],
            recalledIds: Empty,
            selectedIds: Empty,
            excludedIds: Empty,
            toolCallErrored: true);

        Assert.AreEqual(DecisionFailureMode.ToolFailed, result.Mode);
        Assert.AreEqual(LearningFeedbackKinds.ToolFailed, result.FeedbackKind);
        Assert.AreEqual(0.9, result.Confidence);
    }

    /// <summary>
    /// 验证：失败确认不存在的 ID 只提供排除事实，不产生负向归因标签。
    /// </summary>
    [TestMethod]
    public void Classify_ExcludedIds_AreExclusionFactsOnly()
    {
        var result = DecisionFailureAttribution.Classify(
            ["entity-gone"],
            recalledIds: Empty,
            selectedIds: Empty,
            excludedIds: new HashSet<string>(["entity-gone"], StringComparer.Ordinal),
            toolCallErrored: false);

        Assert.AreEqual(DecisionFailureMode.None, result.Mode);
        Assert.IsFalse(result.HasNegativeLabel, "排除事实不应产生负向标签。");
        Assert.AreEqual(string.Empty, result.FeedbackKind);
    }

    /// <summary>
    /// 验证：成功工具观察（无未满足目标）只提供正向线索，不产生负向标签。
    /// </summary>
    [TestMethod]
    public void Classify_SuccessfulTurn_IsNone()
    {
        var result = DecisionFailureAttribution.Classify(
            Array.Empty<string>(),
            recalledIds: Empty,
            selectedIds: Empty,
            excludedIds: Empty,
            toolCallErrored: false);

        Assert.AreEqual(DecisionFailureMode.None, result.Mode);
        Assert.IsFalse(result.HasNegativeLabel);
    }

    /// <summary>
    /// 验证：多个未满足目标命中不同模式时，取最上游失败且置信度取最低值。
    /// </summary>
    [TestMethod]
    public void Classify_MultipleIds_UsesMostUpstreamFailureAndMinConfidence()
    {
        var result = DecisionFailureAttribution.Classify(
            ["entity-selected", "entity-missing"],
            recalledIds: new HashSet<string>(["entity-selected"], StringComparer.Ordinal),
            selectedIds: new HashSet<string>(["entity-selected"], StringComparer.Ordinal),
            excludedIds: Empty,
            toolCallErrored: false);

        Assert.AreEqual(DecisionFailureMode.EvidenceNotRecalled, result.Mode,
            "上游失败（未召回）解释下游表象（未使用）。");
        Assert.AreEqual(0.6, result.Confidence, "置信度取各命中中的最低值。");
        Assert.AreEqual(2, result.EvidenceIds.Count);
    }

    /// <summary>
    /// 验证：同一目标已有来源为 human 的事件时，自动化弱标签不得与其等权。
    /// </summary>
    [TestMethod]
    public void IsOverriddenByHumanGold_RespectsHumanLabels()
    {
        var humanGold = new LearningFeedbackEvent
        {
            FeedbackId = "lfb_human",
            TargetId = "entity-1",
            Source = DecisionFailureAttribution.SourceHuman,
            FeedbackKind = LearningFeedbackKinds.Useful,
            Confidence = 1.0
        };
        var toolLabel = new LearningFeedbackEvent
        {
            FeedbackId = "lfb_tool",
            TargetId = "entity-1",
            Source = DecisionFailureAttribution.SourceTool,
            FeedbackKind = LearningFeedbackKinds.EvidenceNotRecalled,
            Confidence = 0.6
        };

        Assert.IsTrue(
            DecisionFailureAttribution.IsOverriddenByHumanGold([humanGold], "entity-1"),
            "存在人工金标时弱标签应被覆盖。");
        Assert.IsFalse(
            DecisionFailureAttribution.IsOverriddenByHumanGold([toolLabel], "entity-1"),
            "仅有工具弱标签时不构成人工金标覆盖。");
        Assert.IsFalse(
            DecisionFailureAttribution.IsOverriddenByHumanGold([humanGold], "entity-other"),
            "目标不同不应互相覆盖。");
    }

    /// <summary>
    /// 验证：偏差监控量化无观察比例、只观察已展示候选比例与位置偏差风险。
    /// </summary>
    [TestMethod]
    public void BiasReport_QuantifiesUnobservedAndSelectedOnlyAndPosition()
    {
        var report = FeedbackBiasReportBuilder.Build(
        [
            new TurnFeedbackRecord(HasToolEvidence: false, HasFeedback: false, TargetWasSelected: false, SelectedRank: 0),
            new TurnFeedbackRecord(HasToolEvidence: false, HasFeedback: false, TargetWasSelected: false, SelectedRank: 0),
            new TurnFeedbackRecord(HasToolEvidence: false, HasFeedback: false, TargetWasSelected: false, SelectedRank: 0),
            new TurnFeedbackRecord(HasToolEvidence: false, HasFeedback: false, TargetWasSelected: false, SelectedRank: 0),
            new TurnFeedbackRecord(HasToolEvidence: false, HasFeedback: false, TargetWasSelected: false, SelectedRank: 0),
            new TurnFeedbackRecord(HasToolEvidence: true, HasFeedback: true, TargetWasSelected: true, SelectedRank: 1),
            new TurnFeedbackRecord(HasToolEvidence: true, HasFeedback: true, TargetWasSelected: true, SelectedRank: 1),
            new TurnFeedbackRecord(HasToolEvidence: true, HasFeedback: true, TargetWasSelected: true, SelectedRank: 2)
        ]);

        Assert.AreEqual(8, report.TotalTurns);
        Assert.AreEqual(3, report.ObservedTurns);
        Assert.AreEqual(3, report.FeedbackEvents);
        Assert.AreEqual(3, report.FeedbackOnSelected);
        Assert.AreEqual(0.625, report.UnobservedFraction, 1e-9);
        Assert.AreEqual(1.0, report.FeedbackOnSelectedOnlyFraction, 1e-9);
        Assert.AreEqual(4.0 / 3.0, report.MeanFeedbackRank, 1e-9);
        Assert.IsTrue(report.Risks.Any(risk => risk.Contains("选择偏差", StringComparison.Ordinal)),
            "无观察过半应报告选择偏差。");
        Assert.IsTrue(report.Risks.Any(risk => risk.Contains("只观察已展示", StringComparison.Ordinal)),
            "反馈全部落在已展示候选应报告该风险。");
        Assert.IsTrue(report.Risks.Any(risk => risk.Contains("位置偏差", StringComparison.Ordinal)),
            "反馈集中在靠前位置应报告位置偏差。");
    }

    /// <summary>
    /// 验证：观察充分且反馈分散时，偏差监控不报风险。
    /// </summary>
    [TestMethod]
    public void BiasReport_CleanData_HasNoRisks()
    {
        var report = FeedbackBiasReportBuilder.Build(
        [
            new TurnFeedbackRecord(HasToolEvidence: true, HasFeedback: true, TargetWasSelected: false, SelectedRank: 0),
            new TurnFeedbackRecord(HasToolEvidence: true, HasFeedback: true, TargetWasSelected: false, SelectedRank: 0),
            new TurnFeedbackRecord(HasToolEvidence: true, HasFeedback: true, TargetWasSelected: true, SelectedRank: 4),
            new TurnFeedbackRecord(HasToolEvidence: true, HasFeedback: true, TargetWasSelected: true, SelectedRank: 7)
        ]);

        Assert.AreEqual(0.0, report.UnobservedFraction, 1e-9);
        Assert.AreEqual(0.5, report.FeedbackOnSelectedOnlyFraction, 1e-9);
        Assert.AreEqual(0, report.Risks.Count, "观察充分且反馈分散时不应报风险。");
    }
}
