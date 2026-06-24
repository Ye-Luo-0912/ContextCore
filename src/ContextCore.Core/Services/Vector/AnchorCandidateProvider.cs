using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// anchor 候选提供者；基于 query tokens 与 indexed entry 的 sourceTags / ItemKind / sourceRefs 匹配。
/// label-free：anchor 信号只用 entry 自身携带的元数据，不依赖 fixture/domain 词表、不特判 sampleId/itemId。
/// </summary>
public sealed class AnchorCandidateProvider
{
    private readonly VectorCandidateEligibilityPolicy _eligibilityPolicy;

    public AnchorCandidateProvider(VectorCandidateEligibilityPolicy? eligibilityPolicy = null)
    {
        _eligibilityPolicy = eligibilityPolicy ?? new VectorCandidateEligibilityPolicy();
    }

    /// <summary>对给定 entries 生成 anchor 匹配候选，按命中分排序取 topK。</summary>
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
            var normalizedScore = NormalizeScore(score, terms.Length);
            candidates.Add(HybridCandidateBuilder.Build(
                entry,
                normalizedScore,
                rank: i + 1,
                rawRank: i + 1,
                profile,
                _eligibilityPolicy,
                diagnosticTypes: Array.Empty<string>(),
                candidateSource: HybridCandidateSource.Anchor));
        }

        return candidates;
    }

    /// <summary>计算 entry 的 anchor 信号（sourceTags / ItemKind / sourceRefs）对 query terms 的命中。</summary>
    private static (double Score, int MatchedCount) ScoreEntry(VectorIndexEntry entry, string[] terms)
    {
        var tags = GetEntryTags(entry);
        var itemKind = entry.ItemKind ?? string.Empty;
        var refs = GetEntryRefs(entry);

        var matched = 0;
        double score = 0;
        foreach (var term in terms)
        {
            var hit = false;
            foreach (var tag in tags)
            {
                if (tag.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit = true;
                    score += 1.0;
                    break;
                }
            }

            if (!hit && itemKind.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hit = true;
                score += 0.6;
            }

            if (!hit)
            {
                foreach (var refItem in refs)
                {
                    if (refItem.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hit = true;
                        score += 0.4;
                        break;
                    }
                }
            }

            if (hit)
            {
                matched++;
            }
        }

        return (score, matched);
    }

    private static string[] GetEntryTags(VectorIndexEntry entry)
    {
        if (entry.Metadata.TryGetValue("sourceTags", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return [];
    }

    private static string[] GetEntryRefs(VectorIndexEntry entry)
    {
        if (entry.Metadata.TryGetValue("sourceRefs", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return [];
    }

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
