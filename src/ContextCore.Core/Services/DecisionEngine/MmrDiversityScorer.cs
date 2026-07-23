using ContextCore.Abstractions;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R28-B.8.1：MMR（Maximal Marginal Relevance）diversity scorer
//
// MMR 重排序算法：
//   MMR = argmax_{d ∈ R\D} [λ · sim(d, q) - (1-λ) · max_{d' ∈ D} sim(d, d')]
//   其中 R=候选集，D=已选集，q=query，sim=相似度。
//
// 相似度定义：
//   sim(d, q) = candidate.Utility.FinalScore（归一化到 [0,1]）
//   sim(d, d') = 基于 Type + Source 的简单相似度
//     相同 Type:  +0.8    不同 Type:  +0.2
//     相同 Source: +0.6
//     最终 cap 到 [0, 1.0]
//
// 设计原则：
//   1. 纯内存、无副作用：输入候选集不被修改，返回新列表。
//   2. 确定性：相同输入 + 相同 lambda 产生相同输出（tie-break 按 CandidateId 升序）。
//   3. 不处理 mandatory 候选：由调用方在 MMR 前分离 mandatory / non-mandatory。
// ===========================================================================

/// <summary>R28-B.8.1：MMR（Maximal Marginal Relevance）diversity scorer。</summary>
internal static class MmrDiversityScorer
{
    /// <summary>
    /// 使用 MMR 重排序候选。
    /// </summary>
    /// <param name="candidates">候选集合（不会被修改）。</param>
    /// <param name="lambda">MMR lambda（0=纯 diversity，1=纯 relevance）。</param>
    /// <param name="topK">最多返回的候选数。</param>
    /// <returns>重排序后的候选列表（长度 <= min(candidates.Count, topK)）。</returns>
    public static IReadOnlyList<ContextCandidateEnvelope> RerankWithMmr(
        IReadOnlyList<ContextCandidateEnvelope> candidates,
        double lambda,
        int topK)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // 候选数 <= 1 或 topK <= 1，直接返回（无法做 diversity 选择）
        if (candidates.Count <= 1 || topK <= 1)
        {
            return candidates;
        }

        // 钳制 lambda 到 [0, 1]，避免非法值导致 MMR 分数越界
        var clampedLambda = Math.Clamp(lambda, 0.0, 1.0);

        // 归一化 FinalScore 到 [0,1]（min-max 归一化）
        var scores = candidates.Select(c => c.Utility.FinalScore).ToList();
        var minScore = scores.Min();
        var maxScore = scores.Max();
        var scoreRange = maxScore - minScore;

        double NormalizeScore(double score)
        {
            // 所有分数相同（range ≈ 0）时统一为 1.0，避免 0/0 不确定
            if (scoreRange < 1e-9) return 1.0;
            return (score - minScore) / scoreRange;
        }

        var result = new List<ContextCandidateEnvelope>(candidates.Count);
        var remaining = new List<ContextCandidateEnvelope>(candidates);
        var selected = new List<ContextCandidateEnvelope>();

        // 第一轮：选 relevance 最高的（此时 D 为空，MMR = λ · sim(d, q)）
        // tie-break 按 CandidateId 升序保证确定性
        var first = remaining
            .OrderByDescending(c => NormalizeScore(c.Utility.FinalScore))
            .ThenBy(c => c.CandidateId, StringComparer.Ordinal)
            .First();
        selected.Add(first);
        result.Add(first);
        remaining.Remove(first);

        // 后续轮次：每轮选 MMR 分数最高的候选
        var effectiveTopK = Math.Min(topK, candidates.Count);
        while (remaining.Count > 0 && result.Count < effectiveTopK)
        {
            var best = remaining
                .Select(c =>
                {
                    var relevance = NormalizeScore(c.Utility.FinalScore);
                    // max_{d' ∈ D} sim(d, d')：与已选集中最相似的候选的相似度
                    var maxSim = selected.Count > 0
                        ? selected.Max(s => Similarity(c, s))
                        : 0.0;
                    var mmrScore = clampedLambda * relevance - (1.0 - clampedLambda) * maxSim;
                    return new { Candidate = c, MmrScore = mmrScore };
                })
                .OrderByDescending(x => x.MmrScore)
                .ThenBy(x => x.Candidate.CandidateId, StringComparer.Ordinal)
                .First();

            selected.Add(best.Candidate);
            result.Add(best.Candidate);
            remaining.Remove(best.Candidate);
        }

        return result;
    }

    /// <summary>
    /// 计算两个候选之间的相似度（基于 Type + Source）。
    /// 相同 Type: +0.8，不同 Type: +0.2；相同 Source: +0.6；最终 cap 到 [0, 1.0]。
    /// </summary>
    private static double Similarity(ContextCandidateEnvelope a, ContextCandidateEnvelope b)
    {
        var sim = 0.0;
        if (string.Equals(a.Type, b.Type, StringComparison.Ordinal))
        {
            sim += 0.8;
        }
        else
        {
            sim += 0.2;
        }
        if (a.Source == b.Source)
        {
            sim += 0.6;
        }
        return Math.Min(1.0, sim);
    }
}
