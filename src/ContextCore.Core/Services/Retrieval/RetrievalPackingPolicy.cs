using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 负责混合检索结果的候选合并、排序和预算打包。
/// 该类只处理结果组装规则，不访问存储，避免 Retriever 主流程继续堆积结果侧分支。
/// </summary>
internal static class RetrievalPackingPolicy
{
    /// <summary>
    /// 合并主召回通道和仅关系扩展通道的候选项，并按统一规则重排。
    /// P0-7.4: 关系独有候选先取一个上限（cap）参与最终竞争。
    /// R12.4A #8: 实际的 TopK 预留配额在 <see cref="Pack"/> 阶段实施；
    /// 此处的 cap 仅作为噪声过滤，避免过多低分 relation-only 候选进入排序。
    /// </summary>
    public static IReadOnlyList<ContextRetrievalCandidate> BuildRankedCandidates(
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> mainCandidates,
        IReadOnlyList<ContextRetrievalCandidate> relationOnlyCandidates)
    {
        var topK = request.TopK > 0 ? request.TopK : 10;
        var orderedMain = OrderCandidates(mainCandidates);
        // R12.4A #7: relation-only 候选也必须使用确定性 tie-break（CandidateId 升序），
        // 否则同 Score 候选的 cap 选择依赖输入枚举顺序，跨 Provider/并发 Channel 不稳定。
        var orderedRelationOnly = OrderCandidates(relationOnlyCandidates);

        // P0-7.4 + R12.4A #8: 这是 cap（上限），用于限制进入统一排序的 relation-only 候选数量。
        // Pack 阶段会显式为 relation-only 候选预留 TopK 名额；此处的 cap 仅做噪声过滤。
        var relationCandidateCap = Math.Min(orderedRelationOnly.Length, Math.Max(2, topK / 3));
        var cappedRelationCandidates = orderedRelationOnly.Take(relationCandidateCap).ToArray();
        var cappedRelationIds = cappedRelationCandidates
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filteredMain = orderedMain
            .Where(candidate => !cappedRelationIds.Contains(candidate.CandidateId))
            .ToArray();

        return OrderCandidates(filteredMain.Concat(cappedRelationCandidates).ToArray());
    }

    /// <summary>
    /// 在 TopK 和 token budget 约束下选择最终结果，并输出选中/丢弃决策。
    /// 强制项可突破预算限制，但会占用后续候选的剩余预算。
    /// R12.4A #8: 当 <paramref name="relationOnlyCandidateIds"/> 非空时，为 relation-only 候选
    /// 显式预留 TopK 名额（min(relationOnlyInRanked, max(2, topK/3))，不超过 topK/2），
    /// 确保关系扩展结果不会被高分主通道候选全部挤掉。任一侧未填满的槽位按分数顺序滚入另一侧。
    /// </summary>
    public static RetrievalPackingResult Pack(
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> rankedCandidates,
        IReadOnlySet<string>? relationOnlyCandidateIds = null)
    {
        var topK = request.TopK > 0 ? request.TopK : 10;
        var tokenBudget = request.TokenBudget > 0 ? request.TokenBudget : int.MaxValue;

        // R12.4A #8: Relation quota 语义修正——Pack 阶段显式为 relation-only 候选预留 TopK 名额。
        // 之前 BuildRankedCandidates 只做 cap（上限），Pack 阶段无差别裁剪可能让关系独有候选被全部裁掉，
        // 与 HybridContextRetriever 注释 "为关系独有条目预留保证槽位" 不一致。
        // 预留量 = min(实际参与排序的 relation-only 数, max(2, topK/3))，且不超过 topK/2（确保 main 至少保留一半）。
        var relationOnlyInRanked = relationOnlyCandidateIds is null || relationOnlyCandidateIds.Count == 0
            ? 0
            : rankedCandidates.Count(c => relationOnlyCandidateIds.Contains(c.CandidateId));
        var reservedSlots = relationOnlyInRanked == 0
            ? 0
            : Math.Min(
                Math.Min(relationOnlyInRanked, Math.Max(2, topK / 3)),
                topK / 2);
        var mainSlots = topK - reservedSlots;

        var selected = new List<ContextRetrievalCandidate>();
        var selectedDecisions = new List<ContextRetrievalDecision>();
        var dropped = new List<ContextRetrievalDecision>();
        var usedTokens = 0;
        var selectedMainCount = 0;
        var selectedRelationCount = 0;

        // 阶段1：按分数顺序选择，应用分类配额（无 rollover）。
        // mandatory 候选不受配额限制，但仍计入 token 预算与 selected.Count。
        foreach (var candidate in rankedCandidates)
        {
            var mandatory = IsMandatory(candidate);

            if (mandatory)
            {
                selected.Add(candidate);
                selectedDecisions.Add(ToDecision(candidate, "强制选中"));
                usedTokens += candidate.EstimatedTokens;
                continue;
            }

            if (usedTokens + candidate.EstimatedTokens > tokenBudget)
            {
                dropped.Add(ToDecision(candidate, "超过 token 预算"));
                continue;
            }

            if (selected.Count >= topK)
            {
                dropped.Add(ToDecision(candidate, "超过 TopK"));
                continue;
            }

            if (reservedSlots > 0)
            {
                var isRelationOnly = relationOnlyCandidateIds!.Contains(candidate.CandidateId);
                if (isRelationOnly)
                {
                    if (selectedRelationCount >= reservedSlots)
                    {
                        dropped.Add(ToDecision(candidate, "超过 relation-only 预留配额"));
                        continue;
                    }
                    selectedRelationCount++;
                    selected.Add(candidate);
                    selectedDecisions.Add(ToDecision(candidate, "选中（relation-only 预留）"));
                    usedTokens += candidate.EstimatedTokens;
                    continue;
                }

                if (selectedMainCount >= mainSlots)
                {
                    dropped.Add(ToDecision(candidate, "超过 main 配额（为 relation-only 预留）"));
                    continue;
                }
                selectedMainCount++;
            }
            else
            {
                selectedMainCount++;
            }

            selected.Add(candidate);
            selectedDecisions.Add(ToDecision(candidate, "选中"));
            usedTokens += candidate.EstimatedTokens;
        }

        // 阶段2：rollover——若 relation-only 未填满 reservedSlots 或 main 未填满 mainSlots，
        // 剩余槽位允许另一侧候选按分数顺序填充（dropped 中的候选已按 ranked 顺序排列）。
        if (selected.Count < topK && dropped.Count > 0)
        {
            var candidateById = rankedCandidates.ToDictionary(c => c.CandidateId, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < dropped.Count;)
            {
                if (selected.Count >= topK)
                {
                    break;
                }

                var decision = dropped[i];
                if (!candidateById.TryGetValue(decision.CandidateId, out var candidate))
                {
                    i++;
                    continue;
                }

                if (usedTokens + candidate.EstimatedTokens > tokenBudget)
                {
                    i++;
                    continue;
                }

                dropped.RemoveAt(i);
                selected.Add(candidate);
                selectedDecisions.Add(ToDecision(candidate, "选中（rollover）"));
                usedTokens += candidate.EstimatedTokens;
                // 不递增 i，因为已移除当前元素
            }
        }

        return new RetrievalPackingResult(selected, selectedDecisions, dropped);
    }

    private static ContextRetrievalCandidate[] OrderCandidates(IEnumerable<ContextRetrievalCandidate> candidates)
    {
        // P0-7.5: 最终确定性 tie-break 使用 CandidateId 升序。
        // 在 Mandatory/Score/EstimatedTokens 全部相同时保证跨 Provider/并发 Channel 的稳定排序，
        // 避免依赖输入枚举顺序导致结果不稳定。
        return candidates
            .OrderByDescending(IsMandatory)
            .ThenByDescending(item => item.Score)
            .ThenByDescending(item => item.EstimatedTokens)
            .ThenBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsMandatory(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("mandatory", out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static ContextRetrievalDecision ToDecision(
        ContextRetrievalCandidate candidate,
        string reason)
    {
        return new ContextRetrievalDecision
        {
            CandidateId = candidate.CandidateId,
            SourceId = candidate.SourceId,
            Kind = candidate.Kind,
            Type = candidate.Type,
            Reason = reason,
            Score = candidate.Score,
            EstimatedTokens = candidate.EstimatedTokens,
            Metadata = new Dictionary<string, string>(candidate.Metadata)
        };
    }
}

internal sealed record RetrievalPackingResult(
    IReadOnlyList<ContextRetrievalCandidate> SelectedCandidates,
    IReadOnlyList<ContextRetrievalDecision> SelectedDecisions,
    IReadOnlyList<ContextRetrievalDecision> DroppedDecisions);
