using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 短期锚点角色分类器：将 ContextAnchorExtractor 输出的原始锚点
/// 按召回意图分类为 Primary / Support / Negative / Audit / Conflict 五个角色。
/// 第一版使用 profile 化规则启发式，不调用 LLM。
/// </summary>
public sealed class ShortTermAnchorExtractor
{
    private readonly ShortTermAnchorClassificationProfile _profile;

    public ShortTermAnchorExtractor(ShortTermAnchorClassificationProfile? profile = null)
    {
        _profile = profile ?? ShortTermAnchorClassificationProfile.CreateDefault();
    }

    /// <summary>将原始锚点列表分类为带角色的召回锚点条目列表。</summary>
    public IReadOnlyList<RetrievalAnchorEntry> Classify(
        IReadOnlyList<ContextAnchor> anchors,
        IReadOnlyList<RecentContextItem> recentItems)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(recentItems);

        var results = new List<RetrievalAnchorEntry>(anchors.Count);
        foreach (var anchor in anchors)
        {
            var role = ClassifyAnchor(anchor);
            results.Add(new RetrievalAnchorEntry(anchor.Name, role, anchor.Weight, anchor.Source, anchor.Type));
        }

        return results;
    }

    private RetrievalAnchorRole ClassifyAnchor(ContextAnchor anchor)
    {
        foreach (var rule in _profile.RoleRules)
        {
            if (rule.Signals.Any(signal => ContainsSignal(anchor.Name, signal)))
            {
                return rule.Role;
            }
        }

        if (anchor.Weight < _profile.NegativeWeightThreshold)
        {
            return RetrievalAnchorRole.Negative;
        }

        if (anchor.Type is AnchorType.Task or AnchorType.Intent
            && anchor.Weight >= _profile.PrimaryTaskOrIntentWeightThreshold)
        {
            return RetrievalAnchorRole.Primary;
        }

        if (anchor.Type is AnchorType.Topic
            && anchor.Weight >= _profile.PrimaryTopicWeightThreshold)
        {
            return RetrievalAnchorRole.Primary;
        }

        if (anchor.Type is AnchorType.Mode
            && anchor.Weight >= _profile.PrimaryModeWeightThreshold)
        {
            return RetrievalAnchorRole.Primary;
        }

        return RetrievalAnchorRole.Support;
    }

    private static bool ContainsSignal(string name, string signal)
    {
        return !string.IsNullOrWhiteSpace(signal)
            && name.Contains(signal, StringComparison.OrdinalIgnoreCase);
    }
}
