using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 解释 RetrievalPlan 在混合检索执行阶段的过滤和加权语义。
/// 该类只处理计划语义，不访问存储，避免 Retriever 主流程继续堆积规则分支。
/// </summary>
internal static class RetrievalPlanExecutionPolicy
{
    /// <summary>审计或冲突计划需要历史证据，因此允许废弃条目进入候选集。</summary>
    public static bool AllowDeprecated(RetrievalPlan plan)
        => plan.AuditAnchors.Count > 0 || plan.ConflictAnchors.Count > 0;

    /// <summary>计划未要求稳定记忆时跳过 Stable Memory 查询，减少低预算召回的无效 I/O。</summary>
    public static bool SuppressStableMemory(RetrievalPlan plan)
        => !plan.NeedsStableMemory;

    /// <summary>
    /// 计算 Working Memory 条目与计划主锚点的匹配奖励。
    /// 每匹配一个 PrimaryAnchor 得 +1.0，最高 +2.0，保持原有排序行为不变。
    /// </summary>
    public static double GetPrimaryAnchorBonus(RetrievalPlan plan, ContextMemoryItem item)
    {
        if (plan.PrimaryAnchors.Count == 0)
        {
            return 0.0;
        }

        var matchCount = 0;
        foreach (var anchor in plan.PrimaryAnchors)
        {
            if (Contains(item.Content, anchor.Name)
                || Contains(item.Type, anchor.Name)
                || item.Tags.Any(tag => Contains(tag, anchor.Name)))
            {
                matchCount++;
            }
        }

        return Math.Min(matchCount * 1.0, 2.0);
    }

    private static bool Contains(string? value, string queryText)
    {
        return value?.Contains(queryText, StringComparison.OrdinalIgnoreCase) == true;
    }
}
