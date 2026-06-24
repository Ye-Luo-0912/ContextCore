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
    /// 关系独有候选先保留一部分参与最终竞争，随后仍按统一分数规则排序。
    /// </summary>
    public static IReadOnlyList<ContextRetrievalCandidate> BuildRankedCandidates(
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> mainCandidates,
        IReadOnlyList<ContextRetrievalCandidate> relationOnlyCandidates)
    {
        var topK = request.TopK > 0 ? request.TopK : 10;
        var orderedMain = OrderCandidates(mainCandidates);
        var orderedRelationOnly = relationOnlyCandidates
            .OrderByDescending(item => item.Score)
            .ToArray();

        var reservedRelationSlots = Math.Min(orderedRelationOnly.Length, Math.Max(2, topK / 3));
        var reservedRelationCandidates = orderedRelationOnly.Take(reservedRelationSlots).ToArray();
        var reservedIds = reservedRelationCandidates
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filteredMain = orderedMain
            .Where(candidate => !reservedIds.Contains(candidate.CandidateId))
            .ToArray();

        return OrderCandidates(filteredMain.Concat(reservedRelationCandidates).ToArray());
    }

    /// <summary>
    /// 在 TopK 和 token budget 约束下选择最终结果，并输出选中/丢弃决策。
    /// 强制项可突破预算限制，但会占用后续候选的剩余预算。
    /// </summary>
    public static RetrievalPackingResult Pack(
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> rankedCandidates)
    {
        var topK = request.TopK > 0 ? request.TopK : 10;
        var tokenBudget = request.TokenBudget > 0 ? request.TokenBudget : int.MaxValue;
        var selected = new List<ContextRetrievalCandidate>();
        var selectedDecisions = new List<ContextRetrievalDecision>();
        var dropped = new List<ContextRetrievalDecision>();
        var usedTokens = 0;

        foreach (var candidate in rankedCandidates)
        {
            var mandatory = IsMandatory(candidate);
            if (!mandatory && selected.Count >= topK)
            {
                dropped.Add(ToDecision(candidate, "超过 TopK"));
                continue;
            }

            if (!mandatory && usedTokens + candidate.EstimatedTokens > tokenBudget)
            {
                dropped.Add(ToDecision(candidate, "超过 token 预算"));
                continue;
            }

            selected.Add(candidate);
            selectedDecisions.Add(ToDecision(candidate, mandatory ? "强制选中" : "选中"));
            usedTokens += candidate.EstimatedTokens;
        }

        return new RetrievalPackingResult(selected, selectedDecisions, dropped);
    }

    private static ContextRetrievalCandidate[] OrderCandidates(IEnumerable<ContextRetrievalCandidate> candidates)
    {
        return candidates
            .OrderByDescending(IsMandatory)
            .ThenByDescending(item => item.Score)
            .ThenByDescending(item => item.EstimatedTokens)
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
