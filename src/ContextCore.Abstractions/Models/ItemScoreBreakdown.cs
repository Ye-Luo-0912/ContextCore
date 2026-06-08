namespace ContextCore.Abstractions.Models;

/// <summary>
/// 上下文候选项的评分明细，包含 13 个子分维度，供 PackageBuildTrace 可观测性输出使用。
/// 所有字段均为加性分数（正数为加分，负数为减分），FinalScore = 所有子分之和。
/// </summary>
public sealed class ItemScoreBreakdown
{
    /// <summary>基础重要性与置信度分：importance * 4 + confidence * 2，上限 8.0</summary>
    public double BaseScore { get; init; }

    /// <summary>层级分：working_memory=4, stable_memory=2, recent_context/related_context=1, other=0</summary>
    public double LayerScore { get; init; }

    /// <summary>
    /// 生命周期状态分：
    /// active(normal)=+5, active(audit)=+0.5,
    /// deprecated(audit)=+20, deprecated(normal)=-12,
    /// superseded=same as deprecated, rejected=-30
    /// </summary>
    public double StatusScore { get; init; }

    /// <summary>语义锚点匹配总加分（Source != "request.query" 的锚点）</summary>
    public double SemanticAnchorScore { get; init; }

    /// <summary>原始词项匹配总加分（Source == "request.query" 的词条）</summary>
    public double RawTokenMatchScore { get; init; }

    /// <summary>双轨命中奖励：同时命中 SemanticAnchor 和 RawToken 时额外奖励</summary>
    public double AnchorMatchBonus { get; init; }

    /// <summary>模式匹配分：item tags/metadata 包含当前请求 mode 时 +3.0</summary>
    public double ModeMatchScore { get; init; }

    /// <summary>任务意图分：item content 包含 query 意图词时每词 +1.5（上限 6.0）</summary>
    public double TaskIntentScore { get; init; }

    /// <summary>近期性分：UpdatedAt 距今 ≤7天 +2.0，≤30天 +1.0</summary>
    public double RecencyScore { get; init; }

    /// <summary>关系图谱分：item 通过关系图扩展被选中时的额外加分（预留）</summary>
    public double RelationScore { get; init; }

    /// <summary>
    /// 生命周期惩罚：deprecated(normal) 强烈排除扣分，
    /// active 但 0 anchor 命中（anchors.Count > 0 时）的有界加性惩罚
    /// </summary>
    public double LifecyclePenalty { get; init; }

    /// <summary>冗余惩罚：stress-test 类型压制，全局去重等场景（预留）</summary>
    public double RedundancyPenalty { get; init; }

    /// <summary>最终分数 = 所有子分之和（已 clamp 到 0+）</summary>
    public double FinalScore { get; init; }

    /// <summary>格式化为单行字符串，用于 PackageBuildTrace 输出</summary>
    public string ToTraceString()
    {
        var parts = new List<string>();
        if (BaseScore != 0) parts.Add($"Base:{BaseScore:F1}");
        if (LayerScore != 0) parts.Add($"Layer:{LayerScore:F1}");
        if (StatusScore != 0) parts.Add($"Status:{StatusScore:F1}");
        if (SemanticAnchorScore != 0 || RawTokenMatchScore != 0)
        {
            parts.Add($"Anchor:{SemanticAnchorScore + RawTokenMatchScore + AnchorMatchBonus:F1}(Sem:{SemanticAnchorScore:F1}/Raw:{RawTokenMatchScore:F1}/Bonus:{AnchorMatchBonus:F1})");
        }
        if (ModeMatchScore != 0) parts.Add($"Mode:{ModeMatchScore:F1}");
        if (TaskIntentScore != 0) parts.Add($"Intent:{TaskIntentScore:F1}");
        if (RecencyScore != 0) parts.Add($"Recency:{RecencyScore:F1}");
        if (RelationScore != 0) parts.Add($"Relation:{RelationScore:F1}");
        if (LifecyclePenalty != 0) parts.Add($"LifePenalty:{LifecyclePenalty:F1}");
        if (RedundancyPenalty != 0) parts.Add($"Redundancy:{RedundancyPenalty:F1}");
        return string.Join(" | ", parts) + $" => Final:{FinalScore:F2}";
    }
}
