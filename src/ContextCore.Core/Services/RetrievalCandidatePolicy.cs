using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 混合检索候选项的生命周期过滤、文本匹配和基础评分策略。
/// 该类不访问存储，只处理候选级规则，便于后续替换为更细的打分器或评测驱动策略。
/// </summary>
internal static class RetrievalCandidatePolicy
{
    private const double MinimumContextImportance = 0.05;

    /// <summary>普通召回通道使用的 context 过滤：废弃项需计划允许，极低重要性项视为噪音。</summary>
    public static bool CanUseContextItem(ContextItem item, RetrievalPlan plan)
        => CanUseContextItem(item, RetrievalPlanExecutionPolicy.AllowDeprecated(plan));

    /// <summary>普通召回通道使用的 context 过滤：废弃项需显式允许，极低重要性项视为噪音。</summary>
    public static bool CanUseContextItem(ContextItem item, bool allowDeprecated)
        => CanUseRelatedContextItem(item, allowDeprecated)
           && item.Importance >= MinimumContextImportance;

    /// <summary>关系扩展目标使用的 context 过滤：保持旧行为，不按 importance 丢弃关系目标。</summary>
    public static bool CanUseRelatedContextItem(ContextItem item, bool allowDeprecated)
        => allowDeprecated || !HasDeprecatedMetadata(item.Metadata);

    /// <summary>记忆候选过滤：拒绝项永远排除；废弃项仅在审计/冲突计划下允许。</summary>
    public static bool CanUseMemoryItem(ContextMemoryItem item, RetrievalPlan plan)
        => CanUseMemoryItem(item, RetrievalPlanExecutionPolicy.AllowDeprecated(plan));

    /// <summary>记忆候选过滤：拒绝项永远排除；废弃项仅在显式允许时进入候选集。</summary>
    public static bool CanUseMemoryItem(ContextMemoryItem item, bool allowDeprecated)
        => item.Status != ContextMemoryStatus.Rejected
           && (allowDeprecated || item.Status != ContextMemoryStatus.Deprecated);

    /// <summary>关键词召回中 context 条目的基础分，保持原有权重不变。</summary>
    public static double ScoreKeywordContext(string? queryText, ContextItem item)
    {
        return 2.0
            + CalculateTextScore(queryText, item.Title, item.Content, item.Type, item.Tags)
            + Clamp01(item.Importance);
    }

    /// <summary>关键词召回中 memory 条目的基础分，包含计划主锚点奖励。</summary>
    public static double ScoreKeywordMemory(string? queryText, ContextMemoryItem item, RetrievalPlan plan)
    {
        return 1.8
            + CalculateTextScore(queryText, null, item.Content, item.Type, item.Tags)
            + Clamp01(item.Importance)
            + Clamp01(item.Confidence)
            + RetrievalPlanExecutionPolicy.GetPrimaryAnchorBonus(plan, item);
    }

    /// <summary>向量召回分数归一化，负分截断为 0，保持旧倍率。</summary>
    public static double ScoreVectorHit(double vectorScore)
        => Math.Max(0, vectorScore) * 6.0;

    /// <summary>关系扩展目标评分：父节点传导、关系先验、关系质量和目标重要性共同决定。</summary>
    public static double ScoreRelationTarget(
        double parentScore,
        ContextRelation relation,
        double targetImportance,
        int depth)
    {
        var score = ComputeRelationScore(parentScore, relation, targetImportance);
        score = Math.Min(score, parentScore);

        if (depth > 1)
        {
            score *= Math.Pow(0.75, depth - 1);
        }

        return score;
    }

    /// <summary>判断记忆条目是否匹配查询文本，中文场景下使用轻量 bigram 扩展。</summary>
    public static bool MatchesMemoryQuery(ContextMemoryItem item, string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return true;
        }

        var terms = SplitQueryTerms(queryText);
        if (terms.Length > 0)
        {
            return terms.Any(term =>
                Contains(item.Type, term)
                || Contains(item.Content, term)
                || item.Tags.Any(tag => Contains(tag, term))
                || item.SourceRefs.Any(sourceRef => Contains(sourceRef, term)));
        }

        return Contains(item.Type, queryText)
            || Contains(item.Content, queryText)
            || item.Tags.Any(tag => Contains(tag, queryText))
            || item.SourceRefs.Any(sourceRef => Contains(sourceRef, queryText));
    }

    /// <summary>提取当前查询实际命中的轻量词元，供候选 trace 使用，不参与额外打分。</summary>
    public static IReadOnlyList<string> ExtractMatchedTokens(
        string? queryText,
        string? title,
        string content,
        string type,
        IReadOnlyList<string> tags,
        IReadOnlyList<string>? sourceRefs = null)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return Array.Empty<string>();
        }

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = SplitQueryTerms(queryText);
        if (terms.Length > 0)
        {
            foreach (var term in terms)
            {
                if (Contains(title, term)
                    || Contains(content, term)
                    || Contains(type, term)
                    || tags.Any(tag => Contains(tag, term))
                    || (sourceRefs?.Any(sourceRef => Contains(sourceRef, term)) ?? false))
                {
                    matches.Add(term);
                }
            }

            return [.. matches];
        }

        if (Contains(title, queryText)
            || Contains(content, queryText)
            || Contains(type, queryText)
            || tags.Any(tag => Contains(tag, queryText))
            || (sourceRefs?.Any(sourceRef => Contains(sourceRef, queryText)) ?? false))
        {
            matches.Add(queryText);
        }

        return [.. matches];
    }

    /// <summary>提取对 Working Memory 主锚点奖励实际生效的锚点名称，供候选 trace 使用。</summary>
    public static IReadOnlyList<string> ExtractMatchedPrimaryAnchors(RetrievalPlan plan, ContextMemoryItem item)
    {
        if (plan.PrimaryAnchors.Count == 0)
        {
            return Array.Empty<string>();
        }

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in plan.PrimaryAnchors)
        {
            if (Contains(item.Content, anchor.Name)
                || Contains(item.Type, anchor.Name)
                || item.Tags.Any(tag => Contains(tag, anchor.Name)))
            {
                matches.Add(anchor.Name);
            }
        }

        return [.. matches];
    }

    private static double CalculateTextScore(
        string? queryText,
        string? title,
        string content,
        string type,
        IReadOnlyList<string> tags)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return 0.4;
        }

        var score = 0.0;
        if (Contains(title, queryText))
        {
            score += 2.0;
        }

        if (Contains(content, queryText))
        {
            score += 1.6;
        }

        if (Contains(type, queryText))
        {
            score += 0.8;
        }

        score += tags.Count(tag => Contains(tag, queryText)) * 0.5;
        return score;
    }

    private static double ComputeRelationScore(
        double parentScore,
        ContextRelation relation,
        double targetImportance)
    {
        var seedCarryOver = Math.Min(parentScore, 10.0) * 0.25;
        var relationTypeWeight = relation.RelationType switch
        {
            "depends_on" => 1.2,
            "implements" => 1.1,
            "uses" => 1.1,
            "related_to" => 1.0,
            "replaces" => 0.6,
            _ => 0.8
        };
        var relationQuality = Clamp01(relation.Weight) + Clamp01(relation.Confidence);
        var targetBonus = Clamp01(targetImportance) * 0.5;

        return 1.5 + seedCarryOver + relationTypeWeight + relationQuality + targetBonus;
    }

    /// <summary>将查询文本拆分为 ASCII 词元和中文双字 bigram，避免中文长句只能整句匹配。</summary>
    private static string[] SplitQueryTerms(string queryText)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new System.Text.StringBuilder();

        foreach (var ch in queryText)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '.')
            {
                current.Append(ch);
            }
            else
            {
                if (current.Length >= 2)
                {
                    terms.Add(current.ToString());
                }

                current.Clear();
            }
        }

        if (current.Length >= 2)
        {
            terms.Add(current.ToString());
        }

        for (var i = 0; i < queryText.Length - 1; i++)
        {
            if (IsCjkChar(queryText[i]) && IsCjkChar(queryText[i + 1]))
            {
                terms.Add(queryText.Substring(i, 2));
            }
        }

        return terms.Count > 0 ? [.. terms] : [];
    }

    private static bool HasDeprecatedMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        return metadata.TryGetValue("status", out var status)
            && status.Equals("deprecated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string? value, string queryText)
    {
        return value?.Contains(queryText, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsCjkChar(char ch) => ch is >= '\u4E00' and <= '\u9FFF';

    private static double Clamp01(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= 1 ? 1 : value;
    }
}
