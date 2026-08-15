using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Learning;

/// <summary>
/// 决策失败归因模式：区分失败发生在哪一段（召回/选择/利用/工具）。
/// 归因只产生弱标签（带来源与置信度），不改变任何正式策略。
/// </summary>
public enum DecisionFailureMode
{
    /// <summary>无可归因失败（成功工具观察只提供正向线索，不产生负向标签）。</summary>
    None = 0,

    /// <summary>证据存在但没召回：需要但未进入候选集。</summary>
    EvidenceNotRecalled = 1,

    /// <summary>召回但没选：进入候选集但被分配器裁掉。</summary>
    RecalledNotSelected = 2,

    /// <summary>已选但模型未使用：材料已进上下文但模型仍去工具里找。</summary>
    SelectedNotUsed = 3,

    /// <summary>工具自身失败：调用报错（权限/超时/格式等），与证据无关。</summary>
    ToolFailed = 4
}

/// <summary>一次归因产出的弱标签：模式 + 来源 + 置信度 + 涉及的证据 ID。</summary>
public sealed record FailureAttribution(
    DecisionFailureMode Mode,
    string FeedbackKind,
    string Source,
    double Confidence,
    IReadOnlyList<string> EvidenceIds)
{
    /// <summary>是否产生了可写入反馈事件的负向弱标签。</summary>
    public bool HasNegativeLabel => Mode != DecisionFailureMode.None;
}

/// <summary>
/// 确定性失败归因与弱标签质量守卫。
/// 规则：工具报错优先归为 ToolFailed；失败确认的 ID 只作为排除事实、不产生负向标签；
/// 成功工具观察只提供正向线索；需要而未候选 → EvidenceNotRecalled，
/// 候选未选中 → RecalledNotSelected，选中未使用 → SelectedNotUsed。
/// 自动化弱标签置信度不超过 0.9；存在人工金标时弱标签不得与其等权。
/// </summary>
public static class DecisionFailureAttribution
{
    /// <summary>自动化弱标签来源（工具证据推导）。</summary>
    public const string SourceTool = "tool";

    /// <summary>人工金标来源（人工纠正/审核）。</summary>
    public const string SourceHuman = "human";

    // 多个需要 ID 命中不同模式时，取最上游的失败（上游失败解释了下游表象），
    // 置信度取各命中中的最低值，避免高估。
    private static readonly (DecisionFailureMode Mode, double Confidence)[] Priority =
    [
        (DecisionFailureMode.EvidenceNotRecalled, 0.6),
        (DecisionFailureMode.RecalledNotSelected, 0.8),
        (DecisionFailureMode.SelectedNotUsed, 0.7)
    ];

    /// <summary>
    /// 根据本轮需要但未满足的目标 ID、候选/选中/排除集合与工具报错标志，
    /// 归因出唯一的失败模式与弱标签。neededIds 为空或全部是排除事实时归为 None。
    /// </summary>
    /// <param name="neededIds">本轮需要但在工具结果里未满足的目标实体 ID。</param>
    /// <param name="recalledIds">本轮召回（进入候选集）的候选 ID 全集，包含选中项。</param>
    /// <param name="selectedIds">本轮最终选中的候选 ID 集合。</param>
    /// <param name="excludedIds">失败工具观察确认不存在的 ID（排除事实）。</param>
    /// <param name="toolCallErrored">本轮是否存在工具调用自身报错。</param>
    public static FailureAttribution Classify(
        IReadOnlyList<string> neededIds,
        IReadOnlySet<string> recalledIds,
        IReadOnlySet<string> selectedIds,
        IReadOnlySet<string> excludedIds,
        bool toolCallErrored)
    {
        if (toolCallErrored)
        {
            return new FailureAttribution(
                DecisionFailureMode.ToolFailed,
                LearningFeedbackKinds.ToolFailed,
                SourceTool,
                0.9,
                [.. neededIds]);
        }

        DecisionFailureMode mode = DecisionFailureMode.None;
        var confidence = 0.5;
        var evidenceIds = new List<string>();

        foreach (var id in neededIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (excludedIds.Contains(id))
            {
                // 失败确认不存在的 ID 只提供排除事实，不产生负向归因标签。
                continue;
            }

            (var hitMode, var hitConfidence) = selectedIds.Contains(id)
                ? (DecisionFailureMode.SelectedNotUsed, 0.7)
                : recalledIds.Contains(id)
                    ? (DecisionFailureMode.RecalledNotSelected, 0.8)
                    : (DecisionFailureMode.EvidenceNotRecalled, 0.6);

            if (PriorityOf(hitMode) < PriorityOf(mode) || mode == DecisionFailureMode.None)
            {
                mode = hitMode;
                confidence = hitConfidence;
            }
            else if (PriorityOf(hitMode) == PriorityOf(mode))
            {
                confidence = Math.Min(confidence, hitConfidence);
            }

            evidenceIds.Add(id);
        }

        return new FailureAttribution(
            mode,
            MapToKind(mode),
            SourceTool,
            mode == DecisionFailureMode.None ? 0.5 : confidence,
            evidenceIds);
    }

    /// <summary>把失败模式映射为反馈事件类型。</summary>
    public static string MapToKind(DecisionFailureMode mode) => mode switch
    {
        DecisionFailureMode.EvidenceNotRecalled => LearningFeedbackKinds.EvidenceNotRecalled,
        DecisionFailureMode.RecalledNotSelected => LearningFeedbackKinds.RecalledNotSelected,
        DecisionFailureMode.SelectedNotUsed => LearningFeedbackKinds.SelectedNotUsed,
        DecisionFailureMode.ToolFailed => LearningFeedbackKinds.ToolFailed,
        _ => string.Empty
    };

    /// <summary>
    /// 弱标签质量守卫：同一目标已存在人工金标（来源为 human）时，
    /// 自动化弱标签不得与其等权（返回 true 表示应跳过/降权弱标签）。
    /// </summary>
    public static bool IsOverriddenByHumanGold(
        IReadOnlyList<LearningFeedbackEvent> existingEvents,
        string targetId)
    {
        return existingEvents.Any(item =>
            string.Equals(item.TargetId, targetId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Source, SourceHuman, StringComparison.OrdinalIgnoreCase));
    }

    private static int PriorityOf(DecisionFailureMode mode)
    {
        for (var i = 0; i < Priority.Length; i++)
        {
            if (Priority[i].Mode == mode)
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}

/// <summary>反馈偏差监控报告：量化无观察、只观察已展示候选与位置偏差风险。</summary>
public sealed record FeedbackBiasReport(
    int TotalTurns,
    int ObservedTurns,
    int FeedbackEvents,
    int FeedbackOnSelected,
    double UnobservedFraction,
    double FeedbackOnSelectedOnlyFraction,
    double MeanFeedbackRank,
    IReadOnlyList<string> Risks);

/// <summary>
/// 反馈偏差监控：对每轮记录做聚合，量化三类偏差风险——
/// 无观察轮次比例（选择偏差：只对有工具结果的轮次有反馈）、
/// 反馈只落在已展示（选中）候选的比例（只观察已展示候选）、
/// 反馈目标位置均值（位置偏差：反馈集中在靠前位置）。
/// 只做报告，不改变数据。
/// </summary>
public static class FeedbackBiasReportBuilder
{
    /// <summary>
    /// 聚合每轮反馈记录。rank 为 1 起算的选中位置；无选中目标的轮次 rank 传 0。
    /// </summary>
    /// <param name="turns">每轮记录：是否有工具证据、是否有反馈、反馈目标是否选中、选中位置。</param>
    public static FeedbackBiasReport Build(IReadOnlyList<TurnFeedbackRecord> turns)
    {
        var totalTurns = turns.Count;
        var observedTurns = turns.Count(item => item.HasToolEvidence);
        var feedbackEvents = turns.Count(item => item.HasFeedback);
        var feedbackOnSelected = turns.Count(item => item.HasFeedback && item.TargetWasSelected);
        var rankedFeedback = turns
            .Where(item => item.HasFeedback && item.SelectedRank > 0)
            .Select(item => item.SelectedRank)
            .ToArray();

        var meanRank = rankedFeedback.Length == 0 ? 0.0 : rankedFeedback.Average();
        var unobservedFraction = totalTurns == 0 ? 1.0 : 1.0 - observedTurns / (double)totalTurns;
        var selectedOnlyFraction = feedbackEvents == 0 ? 0.0 : feedbackOnSelected / (double)feedbackEvents;

        var risks = new List<string>();
        if (totalTurns > 0 && unobservedFraction > 0.5)
        {
            risks.Add($"无工具证据轮次占比 {unobservedFraction:P0}，反馈只覆盖被观察的子集（选择偏差）。");
        }

        if (feedbackEvents > 0 && selectedOnlyFraction > 0.8)
        {
            risks.Add($"反馈目标落在已展示（选中）候选的占比 {selectedOnlyFraction:P0}，只观察已展示候选。");
        }

        if (rankedFeedback.Length >= 3 && meanRank <= 2.0)
        {
            risks.Add($"反馈目标平均位置 {meanRank:F1}，反馈集中在靠前位置（位置偏差）。");
        }

        return new FeedbackBiasReport(
            totalTurns,
            observedTurns,
            feedbackEvents,
            feedbackOnSelected,
            unobservedFraction,
            selectedOnlyFraction,
            meanRank,
            risks);
    }
}

/// <summary>单轮反馈记录，供偏差监控聚合。</summary>
public sealed record TurnFeedbackRecord(
    bool HasToolEvidence,
    bool HasFeedback,
    bool TargetWasSelected,
    int SelectedRank);
