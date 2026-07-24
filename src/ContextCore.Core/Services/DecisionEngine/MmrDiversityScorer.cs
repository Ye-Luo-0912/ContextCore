using ContextCore.Abstractions;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R28-B.8.1 / R28-G P1-3：MMR（Maximal Marginal Relevance）diversity scorer
//
// MMR 重排序算法：
//   MMR = argmax_{d ∈ R\D} [λ · sim(d, q) - (1-λ) · max_{d' ∈ D} sim(d, d')]
//   其中 R=候选集，D=已选集，q=query，sim=相似度。
//
// 相似度定义（R28-G P1-3 保持原契约，未来可扩展为 embedding cosine）：
//   sim(d, q) = candidate.Utility.FinalScore（归一化到 [0,1]）
//   sim(d, d') = 基于 Type + Source 的简单相似度
//     相同 Type:  +0.8    不同 Type:  +0.2
//     相同 Source: +0.6
//     最终 cap 到 [0, 1.0]
//
// R28-G P1-3 性能优化：
//   1. 增量更新 maxSimilarityToSelected 数组：每轮选出 best 后，只对 best 做一次
//      max_sim 增量更新（O(n)），避免每轮对每个候选扫描整个 selected 集合。
//      复杂度从 O(n³) 降至 O(n²)。
//   2. pre-rank TopN 预选：候选数 > preRankTopN 时先按 FinalScore 排序保留前 N。
//      控制 MMR 输入规模，避免大候选集下的 O(n²) 平方级膨胀。
//   3. 候选移除用 swap-pop 替代 List.Remove（O(1) 而非 O(n)）。
//   4. 每轮选 best 用单次线性扫描 + 跟踪 max（不再 OrderBy+First）。
//
// 设计原则：
//   1. 纯内存、无副作用：输入候选集不被修改，返回新列表。
//   2. 确定性：相同输入 + 相同 lambda 产生相同输出（tie-break 按 CandidateId 升序）。
//   3. 不处理 mandatory 候选：由调用方在 MMR 前分离 mandatory / non-mandatory。
// ===========================================================================

/// <summary>R28-B.8.1 / R28-G P1-3：MMR（Maximal Marginal Relevance）diversity scorer。</summary>
internal static class MmrDiversityScorer
{
    /// <summary>
    /// R28-G P1-3：默认 pre-rank TopN 上限。
    /// 候选数超过此值时先按 FinalScore 降序保留前 N 个再做 MMR，避免 O(n²) 平方膨胀。
    /// </summary>
    internal const int DefaultPreRankTopN = 256;

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
        => RerankWithMmr(candidates, lambda, topK, DefaultPreRankTopN);

    /// <summary>
    /// R28-G P1-3：MMR 重排序（可配置 pre-rank TopN 上限）。
    /// </summary>
    /// <param name="candidates">候选集合（不会被修改）。</param>
    /// <param name="lambda">MMR lambda（0=纯 diversity，1=纯 relevance）。</param>
    /// <param name="topK">最多返回的候选数。</param>
    /// <param name="preRankTopN">候选数超过此值时先按 FinalScore 降序保留前 N 个。&lt;=0 表示禁用预选。</param>
    /// <returns>重排序后的候选列表。</returns>
    public static IReadOnlyList<ContextCandidateEnvelope> RerankWithMmr(
        IReadOnlyList<ContextCandidateEnvelope> candidates,
        double lambda,
        int topK,
        int preRankTopN)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // 候选数 <= 1 或 topK <= 1，直接返回（无法做 diversity 选择）
        if (candidates.Count <= 1 || topK <= 1)
        {
            return candidates;
        }

        // 钳制 lambda 到 [0, 1]，避免非法值导致 MMR 分数越界
        var clampedLambda = Math.Clamp(lambda, 0.0, 1.0);

        // R28-G P1-3：pre-rank TopN 预选。
        // 候选数 > preRankTopN（且 preRankTopN > 0）时，先按 FinalScore 降序保留前 N，
        // 控制 MMR 输入规模。预选不改变 topK 语义，只是限制 MMR 的搜索空间。
        IReadOnlyList<ContextCandidateEnvelope> working = candidates;
        if (preRankTopN > 0 && candidates.Count > preRankTopN)
        {
            working = PreRankTopN(candidates, preRankTopN);
        }

        // 归一化 FinalScore 到 [0,1]（min-max 归一化）
        var scores = new double[working.Count];
        double minScore = double.PositiveInfinity;
        double maxScore = double.NegativeInfinity;
        for (var i = 0; i < working.Count; i++)
        {
            var s = working[i].Utility.FinalScore;
            scores[i] = s;
            if (s < minScore) minScore = s;
            if (s > maxScore) maxScore = s;
        }
        var scoreRange = maxScore - minScore;

        // 归一化函数：所有分数相同（range ≈ 0）时统一为 1.0，避免 0/0 不确定
        double NormalizeScore(int idx)
        {
            if (scoreRange < 1e-9) return 1.0;
            return (scores[idx] - minScore) / scoreRange;
        }

        var result = new List<ContextCandidateEnvelope>(Math.Min(topK, working.Count));

        // R28-G P1-3：remaining 用数组 + active 标志位，swap-pop 替代 List.Remove。
        // remainingIndices 跟踪仍可选择的候选在 working 中的下标。
        var remainingIndices = new int[working.Count];
        for (var i = 0; i < working.Count; i++) remainingIndices[i] = i;
        var remainingCount = working.Count;

        // R28-G P1-3：maxSimilarityToSelected 增量更新数组。
        // 每轮选出 best 后，只对 best 做一次 max_sim 增量更新（O(n)），
        // 不再每轮对每个候选扫描整个 selected 集合。
        var maxSimToSelected = new double[working.Count];

        var effectiveTopK = Math.Min(topK, working.Count);

        // 第一轮：选 relevance 最高的（此时 D 为空，MMR = λ · sim(d, q)）
        // tie-break 按 CandidateId 升序保证确定性
        var firstIdx = 0;
        for (var i = 1; i < remainingCount; i++)
        {
            var curIdx = remainingIndices[i];
            var firstCandIdx = remainingIndices[firstIdx];
            var cmp = CompareFirstRound(working, curIdx, firstCandIdx, scores, minScore, scoreRange);
            if (cmp < 0)
            {
                firstIdx = i;
            }
        }
        var firstWorkingIdx = remainingIndices[firstIdx];
        result.Add(working[firstWorkingIdx]);
        // swap-pop 移除 firstIdx
        remainingIndices[firstIdx] = remainingIndices[--remainingCount];

        // 后续轮次：选 MMR 分数最高的候选
        while (remainingCount > 0 && result.Count < effectiveTopK)
        {
            // R28-G P1-3：单次线性扫描找 best（不再 OrderBy+First）。
            // bestIdx 指向 remainingIndices 中的位置；bestWorkingIdx 指向 working 中的下标。
            var bestRemIdx = 0;
            var bestWorkingIdx = remainingIndices[0];
            var bestMmrScore = ComputeMmrScore(
                clampedLambda, NormalizeScore(bestWorkingIdx), maxSimToSelected[bestWorkingIdx]);

            for (var i = 1; i < remainingCount; i++)
            {
                var curWorkingIdx = remainingIndices[i];
                var mmrScore = ComputeMmrScore(
                    clampedLambda, NormalizeScore(curWorkingIdx), maxSimToSelected[curWorkingIdx]);
                var cmp = mmrScore.CompareTo(bestMmrScore);
                if (cmp == 0)
                {
                    // tie-break：CandidateId 升序（确定性）
                    cmp = StringComparer.Ordinal.Compare(
                        working[curWorkingIdx].CandidateId,
                        working[bestWorkingIdx].CandidateId);
                }
                if (cmp > 0)
                {
                    bestRemIdx = i;
                    bestWorkingIdx = curWorkingIdx;
                    bestMmrScore = mmrScore;
                }
            }

            result.Add(working[bestWorkingIdx]);
            // swap-pop 移除 bestRemIdx
            remainingIndices[bestRemIdx] = remainingIndices[--remainingCount];

            // R28-G P1-3：增量更新 maxSimilarityToSelected。
            // 只对刚选入的 bestWorkingIdx 做一次 similarity 计算，
            // 与每个 remaining 候选的 max_sim 取 max。
            // 这把每轮 O(n·|selected|) 降到 O(n)，总复杂度从 O(n³) 降到 O(n²)。
            for (var i = 0; i < remainingCount; i++)
            {
                var curWorkingIdx = remainingIndices[i];
                var sim = Similarity(working[curWorkingIdx], working[bestWorkingIdx]);
                if (sim > maxSimToSelected[curWorkingIdx])
                {
                    maxSimToSelected[curWorkingIdx] = sim;
                }
            }
        }

        return result;
    }

    /// <summary>计算 MMR 分数：λ · relevance - (1-λ) · maxSim。</summary>
    private static double ComputeMmrScore(double lambda, double relevance, double maxSim)
        => lambda * relevance - (1.0 - lambda) * maxSim;

    /// <summary>R28-G P1-3：第一轮比较（relevance 降序，CandidateId 升序 tie-break）。</summary>
    private static int CompareFirstRound(
        IReadOnlyList<ContextCandidateEnvelope> working,
        int aIdx,
        int bIdx,
        double[] scores,
        double minScore,
        double scoreRange)
    {
        var normA = scoreRange < 1e-9 ? 1.0 : (scores[aIdx] - minScore) / scoreRange;
        var normB = scoreRange < 1e-9 ? 1.0 : (scores[bIdx] - minScore) / scoreRange;
        var cmp = normB.CompareTo(normA); // 降序
        if (cmp != 0) return cmp;
        return StringComparer.Ordinal.Compare(working[aIdx].CandidateId, working[bIdx].CandidateId);
    }

    /// <summary>
    /// R28-G P1-3：pre-rank TopN 预选。
    /// 按 FinalScore 降序保留前 N 个候选（tie-break CandidateId 升序），限制 MMR 输入规模。
    /// </summary>
    private static IReadOnlyList<ContextCandidateEnvelope> PreRankTopN(
        IReadOnlyList<ContextCandidateEnvelope> candidates,
        int topN)
    {
        // 用 partial TopK 选择（最小堆，大小 topN）避免全量排序
        if (candidates.Count <= topN)
        {
            return candidates;
        }

        // 最小堆：堆顶是当前堆中 FinalScore 最小者（CandidateId 最大者作为 tie-break）
        var heap = new List<ContextCandidateEnvelope>(topN);
        for (var i = 0; i < topN; i++)
        {
            heap.Add(candidates[i]);
            SiftUpByScore(heap, heap.Count - 1);
        }

        for (var i = topN; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            // 堆顶是最小者；若当前候选更大则替换堆顶并下沉
            if (CompareByScore(candidate, heap[0]) > 0)
            {
                heap[0] = candidate;
                SiftDownByScore(heap, 0, heap.Count);
            }
        }

        return heap;
    }

    /// <summary>按 FinalScore 降序 + CandidateId 升序的比较。</summary>
    private static int CompareByScore(ContextCandidateEnvelope a, ContextCandidateEnvelope b)
    {
        var cmp = b.Utility.FinalScore.CompareTo(a.Utility.FinalScore); // 降序
        if (cmp != 0) return cmp;
        return StringComparer.Ordinal.Compare(a.CandidateId, b.CandidateId); // 升序
    }

    private static void SiftUpByScore(List<ContextCandidateEnvelope> heap, int idx)
    {
        while (idx > 0)
        {
            var parent = (idx - 1) >> 1;
            if (CompareByScore(heap[idx], heap[parent]) < 0)
            {
                (heap[idx], heap[parent]) = (heap[parent], heap[idx]);
                idx = parent;
            }
            else
            {
                break;
            }
        }
    }

    private static void SiftDownByScore(List<ContextCandidateEnvelope> heap, int idx, int count)
    {
        while (true)
        {
            var left = 2 * idx + 1;
            var right = 2 * idx + 2;
            var smallest = idx;
            if (left < count && CompareByScore(heap[left], heap[smallest]) < 0)
            {
                smallest = left;
            }
            if (right < count && CompareByScore(heap[right], heap[smallest]) < 0)
            {
                smallest = right;
            }
            if (smallest != idx)
            {
                (heap[idx], heap[smallest]) = (heap[smallest], heap[idx]);
                idx = smallest;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// 计算两个候选之间的相似度（基于 Type + Source）。
    /// 相同 Type: +0.8，不同 Type: +0.2；相同 Source: +0.6；最终 cap 到 [0, 1.0]。
    /// </summary>
    /// <remarks>
    /// R28-G P1-3：保留原 Type+Source 相似度契约（不破坏现有调用方语义）。
    /// 未来可扩展为 normalized embedding cosine，需在 ContextCandidateEnvelope 添加 Embedding 字段。
    /// </remarks>
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
