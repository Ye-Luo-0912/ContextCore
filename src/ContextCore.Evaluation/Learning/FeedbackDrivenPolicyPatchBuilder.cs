using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Learning;

/// <summary>
/// 反馈驱动的确定性规则修补类型。
/// 只生成可审计的离线规则建议，不直接改动线上策略；应用需另走离线/灰度门槛。
/// </summary>
public enum FeedbackRulePatchKind
{
    /// <summary>补显式查询：证据存在但没召回的目标应进入显式查询。</summary>
    QueryClaim,

    /// <summary>进入找回问句：被预算裁掉的候选实体应在下一轮显式找回。</summary>
    RecoveryGoal,

    /// <summary>排除：确认不存在的目标不再召回。</summary>
    Exclusion,

    /// <summary>上下文已提供：目标已选中进入上下文，模型无需再查。</summary>
    ContextUsage
}

/// <summary>一条反馈驱动的确定性规则修补；携带来源反馈事件 lineage 与置信度。</summary>
public sealed record FeedbackRulePatch(
    string PatchId,
    FeedbackRulePatchKind Kind,
    string TargetId,
    string Reason,
    string SourceFeedbackId,
    double Confidence);

/// <summary>训练必要性门决策：确定性规则能否解决当前反馈池。</summary>
public sealed record TrainingNecessityDecision(
    bool TrainingNotNeeded,
    int DeterministicallyAddressable,
    int NotDeterministicallyAddressable,
    IReadOnlyList<string> Reasons);

/// <summary>
/// 无学习的策略改进：把带归因的反馈事件翻译成显式、可审计的确定性规则修补。
/// 规则映射：未召回 → 补显式查询；未选中 → 进入找回问句；未使用 → 上下文已提供；
/// 工具自身失败 → 不产生规则（不是策略问题）。撤销反馈不驱动规则。
/// 修补按 (类型, 目标) 去重，置信度取来源事件中的最高值。
/// </summary>
public static class FeedbackDrivenPolicyPatchBuilder
{
    /// <summary>
    /// 从反馈事件生成规则修补。
    /// </summary>
    /// <param name="events">带归因的反馈事件（含撤销事件）。</param>
    public static IReadOnlyList<FeedbackRulePatch> Build(IReadOnlyList<LearningFeedbackEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        // 撤销事件使其目标反馈失效：被撤销的反馈不再驱动任何规则。
        var revokedFeedbackIds = events
            .Where(item => string.Equals(item.FeedbackKind, LearningFeedbackKinds.Revoke, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.RevokesFeedbackId.Trim())
            .Where(static id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var patchesByKey = new Dictionary<string, FeedbackRulePatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events.OrderBy(static item => item.CreatedAt))
        {
            if (string.Equals(item.FeedbackKind, LearningFeedbackKinds.Revoke, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (revokedFeedbackIds.Contains(item.FeedbackId))
            {
                continue;
            }

            var patch = ToPatch(item);
            if (patch is null)
            {
                continue;
            }

            var key = $"{patch.Kind}\u001f{patch.TargetId}";
            if (!patchesByKey.TryGetValue(key, out var existing) || patch.Confidence > existing.Confidence)
            {
                patchesByKey[key] = patch;
            }
        }

        return patchesByKey.Values
            .OrderBy(patch => patch.Kind.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(patch => patch.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 训练必要性门：若反馈池中的失败全部能被确定性规则修补覆盖
    /// （未召回/未选中/未使用），则不需要为"用了 AI"额外训练模型；
    /// 只有存在确定性规则无法修复的失败（工具自身失败）时才考虑训练，且仍需学习门槛。
    /// </summary>
    /// <param name="events">带归因的反馈事件。</param>
    /// <param name="patches">已生成的规则修补。</param>
    public static TrainingNecessityDecision DecideTrainingNecessity(
        IReadOnlyList<LearningFeedbackEvent> events,
        IReadOnlyList<FeedbackRulePatch> patches)
    {
        ArgumentNullException.ThrowIfNull(events);

        var revokedFeedbackIds = events
            .Where(item => string.Equals(item.FeedbackKind, LearningFeedbackKinds.Revoke, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.RevokesFeedbackId.Trim())
            .Where(static id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var negativeLabels = events
            .Where(item => !string.Equals(item.FeedbackKind, LearningFeedbackKinds.Revoke, StringComparison.OrdinalIgnoreCase))
            .Where(item => !revokedFeedbackIds.Contains(item.FeedbackId))
            .Where(item => IsNegativeFailureKind(item.FeedbackKind))
            .ToArray();

        var addressable = negativeLabels.Count(item =>
            item.FeedbackKind is LearningFeedbackKinds.EvidenceNotRecalled
                or LearningFeedbackKinds.RecalledNotSelected
                or LearningFeedbackKinds.SelectedNotUsed);
        var notAddressable = negativeLabels.Count(item =>
            string.Equals(item.FeedbackKind, LearningFeedbackKinds.ToolFailed, StringComparison.OrdinalIgnoreCase));

        var reasons = new List<string>();
        if (negativeLabels.Length == 0)
        {
            reasons.Add("反馈池中没有可归因的失败，无需训练。");
        }
        else if (notAddressable == 0)
        {
            reasons.Add($"全部 {addressable} 条失败均可由确定性规则修补覆盖，无需训练。");
        }
        else
        {
            reasons.Add($"{notAddressable} 条工具自身失败无法用确定性规则修复，训练仅在通过学习门槛后才考虑。");
        }

        return new TrainingNecessityDecision(
            TrainingNotNeeded: notAddressable == 0,
            DeterministicallyAddressable: addressable,
            NotDeterministicallyAddressable: notAddressable,
            Reasons: reasons);
    }

    private static FeedbackRulePatch? ToPatch(LearningFeedbackEvent item)
    {
        var kind = item.FeedbackKind switch
        {
            LearningFeedbackKinds.EvidenceNotRecalled => FeedbackRulePatchKind.QueryClaim,
            LearningFeedbackKinds.RecalledNotSelected => FeedbackRulePatchKind.RecoveryGoal,
            LearningFeedbackKinds.SelectedNotUsed => FeedbackRulePatchKind.ContextUsage,
            _ => (FeedbackRulePatchKind?)null
        };
        if (kind is null)
        {
            return null;
        }

        var isHuman = string.Equals(item.Source, "human", StringComparison.OrdinalIgnoreCase);
        var confidence = isHuman ? 1.0 : ResolveConfidence(item.FeedbackKind);
        var targetId = string.IsNullOrWhiteSpace(item.TargetId) ? item.RequestId : item.TargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return null;
        }

        return new FeedbackRulePatch(
            PatchId: BuildPatchId(kind.Value, targetId),
            Kind: kind.Value,
            TargetId: targetId,
            Reason: BuildReason(kind.Value, item),
            SourceFeedbackId: item.FeedbackId,
            Confidence: confidence);
    }

    private static double ResolveConfidence(string feedbackKind) => feedbackKind switch
    {
        LearningFeedbackKinds.EvidenceNotRecalled => 0.6,
        LearningFeedbackKinds.RecalledNotSelected => 0.8,
        LearningFeedbackKinds.SelectedNotUsed => 0.7,
        _ => 0.5
    };

    private static string BuildReason(FeedbackRulePatchKind kind, LearningFeedbackEvent item)
    {
        var detail = string.IsNullOrWhiteSpace(item.PolicyVersion) ? string.Empty : $"（策略 {item.PolicyVersion}）";
        return kind switch
        {
            FeedbackRulePatchKind.QueryClaim => $"证据存在但未召回：{item.TargetId} 应进入显式查询{detail}",
            FeedbackRulePatchKind.RecoveryGoal => $"已召回但未选中：{item.TargetId} 应进入下一轮找回问句{detail}",
            FeedbackRulePatchKind.ContextUsage => $"已选中但未使用：{item.TargetId} 已在上下文中，无需重查{detail}",
            _ => item.TargetId
        };
    }

    private static bool IsNegativeFailureKind(string feedbackKind)
        => feedbackKind is LearningFeedbackKinds.EvidenceNotRecalled
            or LearningFeedbackKinds.RecalledNotSelected
            or LearningFeedbackKinds.SelectedNotUsed
            or LearningFeedbackKinds.ToolFailed;

    private static string BuildPatchId(FeedbackRulePatchKind kind, string targetId)
    {
        var input = $"{kind}\u001f{targetId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "patch_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
