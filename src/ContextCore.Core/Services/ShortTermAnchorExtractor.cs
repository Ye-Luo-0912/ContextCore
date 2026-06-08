using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 短期锚点角色分类器：将 ContextAnchorExtractor 输出的原始锚点
/// 按召回意图分类为 Primary / Support / Negative / Audit / Conflict 五个角色。
/// 第一版使用规则启发式，不调用 LLM。
/// </summary>
public sealed class ShortTermAnchorExtractor
{
    /// <summary>
    /// 将原始锚点列表分类为带角色的召回锚点条目列表。
    /// </summary>
    public IReadOnlyList<RetrievalAnchorEntry> Classify(
        IReadOnlyList<ContextAnchor> anchors,
        IReadOnlyList<RecentContextItem> recentItems)
    {
        var results = new List<RetrievalAnchorEntry>(anchors.Count);
        foreach (var anchor in anchors)
        {
            var role = ClassifyAnchor(anchor);
            results.Add(new RetrievalAnchorEntry(anchor.Name, role, anchor.Weight, anchor.Source, anchor.Type));
        }

        return results;
    }

    private static RetrievalAnchorRole ClassifyAnchor(ContextAnchor anchor)
    {
        var name = anchor.Name;

        // Audit signals (keyword-based check on anchor name)
        if (IsAuditSignal(name))
            return RetrievalAnchorRole.Audit;

        // Conflict signals (keyword-based check on anchor name)
        if (IsConflictSignal(name))
            return RetrievalAnchorRole.Conflict;

        // Negative: very low weight anchors are treated as noise/exclusion signals
        if (anchor.Weight < 0.25)
            return RetrievalAnchorRole.Negative;

        // Primary: high-weight Task or Intent anchors — most direct signal for current work
        if (anchor.Type is AnchorType.Task or AnchorType.Intent && anchor.Weight >= 0.75)
            return RetrievalAnchorRole.Primary;

        // Primary: high-weight Topic anchors
        if (anchor.Type is AnchorType.Topic && anchor.Weight >= 0.75)
            return RetrievalAnchorRole.Primary;

        // Primary: very high-weight Mode anchors (e.g., a dominant operational mode)
        if (anchor.Type is AnchorType.Mode && anchor.Weight >= 0.90)
            return RetrievalAnchorRole.Primary;

        // Support: Constraint, Project, Entity, TimeRange, and lower-weight Task/Topic/Mode
        return RetrievalAnchorRole.Support;
    }

    private static bool IsAuditSignal(string name)
    {
        return name.Contains("废弃", StringComparison.OrdinalIgnoreCase)
            || name.Contains("审计", StringComparison.OrdinalIgnoreCase)
            || name.Contains("历史", StringComparison.OrdinalIgnoreCase)
            || name.Contains("旧版", StringComparison.OrdinalIgnoreCase)
            || name.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            || name.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
            || name.Contains("audit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConflictSignal(string name)
    {
        return name.Contains("冲突", StringComparison.OrdinalIgnoreCase)
            || name.Contains("替换", StringComparison.OrdinalIgnoreCase)
            || name.Contains("竞争", StringComparison.OrdinalIgnoreCase)
            || name.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            || name.Contains("supersede", StringComparison.OrdinalIgnoreCase)
            || name.Contains("replace", StringComparison.OrdinalIgnoreCase);
    }
}
