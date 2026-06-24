using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 词法候选提供者；基于 query tokens 与 indexed entry 文本的命中计数打分。
/// label-free：只读 entry 文本/标签，不读 eval 标签、不特判 sampleId/itemId、不依赖 fixture/domain 词表。
/// </summary>
public sealed class LexicalCandidateProvider
{
    private readonly VectorCandidateEligibilityPolicy _eligibilityPolicy;

    public LexicalCandidateProvider(VectorCandidateEligibilityPolicy? eligibilityPolicy = null)
    {
        _eligibilityPolicy = eligibilityPolicy ?? new VectorCandidateEligibilityPolicy();
    }

    /// <summary>对给定 entries 生成词法匹配候选，按命中分排序取 topK。</summary>
    public IReadOnlyList<VectorQueryPreviewCandidate> GenerateCandidates(
        string queryText,
        IReadOnlyList<VectorIndexEntry> indexedEntries,
        VectorQueryProfile profile,
        int topK)
    {
        ArgumentNullException.ThrowIfNull(indexedEntries);
        ArgumentNullException.ThrowIfNull(profile);
        if (indexedEntries.Count == 0 || topK <= 0)
        {
            return [];
        }

        var terms = HybridCandidateBuilder.TokenizeQuery(queryText);
        if (terms.Length == 0)
        {
            return [];
        }

        var scored = new List<(VectorIndexEntry Entry, double Score, int MatchedCount)>();
        foreach (var entry in indexedEntries)
        {
            var (score, matchedCount) = ScoreEntry(entry, terms);
            if (matchedCount > 0)
            {
                scored.Add((entry, score, matchedCount));
            }
        }

        var ordered = scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToList();

        var candidates = new List<VectorQueryPreviewCandidate>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (entry, score, _) = ordered[i];
            // 词法分数归一化到 [0, 1]，作为 similarity 传入 eligibility policy（用于 MinSimilarity 闸门）
            var normalizedScore = NormalizeScore(score, terms.Length);
            candidates.Add(HybridCandidateBuilder.Build(
                entry,
                normalizedScore,
                rank: i + 1,
                rawRank: i + 1,
                profile,
                _eligibilityPolicy,
                diagnosticTypes: Array.Empty<string>(),
                candidateSource: HybridCandidateSource.Lexical));
        }

        return candidates;
    }

    /// <summary>计算 entry 文本对各 query terms 的命中数与加权分。</summary>
    private static (double Score, int MatchedCount) ScoreEntry(VectorIndexEntry entry, string[] terms)
    {
        var text = GetEntryText(entry);
        if (string.IsNullOrEmpty(text))
        {
            return (0, 0);
        }

        var matched = 0;
        double score = 0;
        foreach (var term in terms)
        {
            var idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                matched++;
                // 命中越靠前权重越高（标题区域优先）
                score += idx == 0 ? 1.0 : 0.6;
            }
        }

        return (score, matched);
    }

    private static string GetEntryText(VectorIndexEntry entry)
    {
        if (entry.Metadata.TryGetValue("indexedText", out var indexed) && !string.IsNullOrWhiteSpace(indexed))
        {
            return indexed;
        }

        if (entry.Metadata.TryGetValue("sourceText", out var source) && !string.IsNullOrWhiteSpace(source))
        {
            return source;
        }

        return string.Empty;
    }

    /// <summary>词法分按 terms 总数归一化到 [0, 1]；归一化后保持与 dense similarity 可比。</summary>
    private static double NormalizeScore(double rawScore, int termCount)
    {
        if (termCount <= 0)
        {
            return 0;
        }

        var maxPossible = termCount * 1.0;
        var normalized = rawScore / maxPossible;
        return normalized > 1 ? 1 : normalized;
    }
}

/// <summary>hybrid retrieval 候选来源标识，写入 candidate.Metadata["candidateSource"]。</summary>
public static class HybridCandidateSource
{
    public const string Dense = "dense";

    public const string Lexical = "lexical";

    public const string Anchor = "anchor";
}
