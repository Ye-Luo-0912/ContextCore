using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// hybrid 候选 union 策略；合并 dense / lexical / anchor 三路候选，按 ItemId 去重，
/// 保留每个来源贡献标记与 eligibility / lifecycle / risk 元数据。
/// 纯函数，确定性；不重新计算 eligibility，直接透传输入候选的安全属性。
/// </summary>
public sealed class HybridCandidateUnionPolicy
{
    // dense 优先级最高，lexical 次之，anchor 最低
    private static readonly Dictionary<string, int> SourcePriority = new(StringComparer.OrdinalIgnoreCase)
    {
        [HybridCandidateSource.Dense] = 0,
        [HybridCandidateSource.Lexical] = 1,
        [HybridCandidateSource.Anchor] = 2
    };

    /// <summary>合并三路候选并去重，按 union score 降序取 UnionTopK。</summary>
    public IReadOnlyList<VectorQueryPreviewCandidate> Union(
        IReadOnlyList<VectorQueryPreviewCandidate>? dense,
        IReadOnlyList<VectorQueryPreviewCandidate>? lexical,
        IReadOnlyList<VectorQueryPreviewCandidate>? anchor,
        HybridVectorLexicalPreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var merged = new Dictionary<string, MergedCandidate>(StringComparer.OrdinalIgnoreCase);
        AddSource(merged, dense, HybridCandidateSource.Dense);
        AddSource(merged, lexical, HybridCandidateSource.Lexical);
        AddSource(merged, anchor, HybridCandidateSource.Anchor);

        var ordered = merged.Values
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(options.UnionTopK)
            .ToList();

        var candidates = new List<VectorQueryPreviewCandidate>();
        for (var i = 0; i < ordered.Count; i++)
        {
            candidates.Add(ordered[i].ToCandidate(i + 1));
        }

        return candidates;
    }

    /// <summary>统计 union 结果的来源贡献分布，供报告使用。</summary>
    public static HybridSourceContribution BuildContributionBreakdown(
        IReadOnlyList<VectorQueryPreviewCandidate>? dense,
        IReadOnlyList<VectorQueryPreviewCandidate>? lexical,
        IReadOnlyList<VectorQueryPreviewCandidate>? anchor,
        HybridVectorLexicalPreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var merged = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        AddSourceSet(merged, dense, HybridCandidateSource.Dense);
        AddSourceSet(merged, lexical, HybridCandidateSource.Lexical);
        AddSourceSet(merged, anchor, HybridCandidateSource.Anchor);

        var denseOnly = 0;
        var lexicalOnly = 0;
        var anchorOnly = 0;
        var denseLexical = 0;
        var denseAnchor = 0;
        var lexicalAnchor = 0;
        var allThree = 0;

        foreach (var sources in merged.Values)
        {
            var hasDense = sources.Contains(HybridCandidateSource.Dense);
            var hasLexical = sources.Contains(HybridCandidateSource.Lexical);
            var hasAnchor = sources.Contains(HybridCandidateSource.Anchor);

            if (hasDense && hasLexical && hasAnchor)
            {
                allThree++;
            }
            else if (hasDense && hasLexical)
            {
                denseLexical++;
            }
            else if (hasDense && hasAnchor)
            {
                denseAnchor++;
            }
            else if (hasLexical && hasAnchor)
            {
                lexicalAnchor++;
            }
            else if (hasDense)
            {
                denseOnly++;
            }
            else if (hasLexical)
            {
                lexicalOnly++;
            }
            else if (hasAnchor)
            {
                anchorOnly++;
            }
        }

        return new HybridSourceContribution
        {
            DenseOnlyCount = denseOnly,
            LexicalOnlyCount = lexicalOnly,
            AnchorOnlyCount = anchorOnly,
            DenseAndLexicalCount = denseLexical,
            DenseAndAnchorCount = denseAnchor,
            LexicalAndAnchorCount = lexicalAnchor,
            AllThreeCount = allThree
        };
    }

    private static void AddSource(
        Dictionary<string, MergedCandidate> merged,
        IReadOnlyList<VectorQueryPreviewCandidate>? candidates,
        string source)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            var key = string.IsNullOrWhiteSpace(candidate.ItemId) ? candidate.CandidateId : candidate.ItemId;
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = new MergedCandidate(candidate, source);
                continue;
            }

            existing.Sources.Add(source);
            // 保留优先级最高来源的候选主体；同优先级保留更高分数
            var candidatePriority = SourcePriority.GetValueOrDefault(source, 99);
            if (candidatePriority < existing.Priority
                || (candidatePriority == existing.Priority && candidate.Similarity > existing.Score))
            {
                existing.ReplaceWith(candidate, source);
            }
        }
    }

    private static void AddSourceSet(
        Dictionary<string, HashSet<string>> merged,
        IReadOnlyList<VectorQueryPreviewCandidate>? candidates,
        string source)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            var key = string.IsNullOrWhiteSpace(candidate.ItemId) ? candidate.CandidateId : candidate.ItemId;
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (!merged.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                merged[key] = set;
            }

            set.Add(source);
        }
    }

    private sealed class MergedCandidate
    {
        public VectorQueryPreviewCandidate Candidate { get; private set; }
        public string Source { get; private set; }
        public double Score => Candidate.Similarity;
        public string ItemId => string.IsNullOrWhiteSpace(Candidate.ItemId) ? Candidate.CandidateId : Candidate.ItemId;
        public int Priority { get; private set; }
        public HashSet<string> Sources { get; }

        public MergedCandidate(VectorQueryPreviewCandidate candidate, string source)
        {
            Candidate = candidate;
            Source = source;
            Priority = SourcePriority.GetValueOrDefault(source, 99);
            Sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source };
        }

        public void ReplaceWith(VectorQueryPreviewCandidate candidate, string source)
        {
            Candidate = candidate;
            Source = source;
            Priority = SourcePriority.GetValueOrDefault(source, 99);
        }

        public VectorQueryPreviewCandidate ToCandidate(int rank)
        {
            var metadata = new Dictionary<string, string>(Candidate.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["hybridSources"] = string.Join(",", Sources.Order(StringComparer.Ordinal))
            };
            return new VectorQueryPreviewCandidate
            {
                CandidateId = Candidate.CandidateId,
                EntryId = Candidate.EntryId,
                ItemId = Candidate.ItemId,
                ItemKind = Candidate.ItemKind,
                Layer = Candidate.Layer,
                Rank = rank,
                RawRank = Candidate.RawRank,
                Similarity = Candidate.Similarity,
                ContentHash = Candidate.ContentHash,
                EmbeddingModel = Candidate.EmbeddingModel,
                EmbeddingProvider = Candidate.EmbeddingProvider,
                Dimension = Candidate.Dimension,
                IsDuplicate = Candidate.IsDuplicate,
                IsStale = Candidate.IsStale,
                IsOrphan = Candidate.IsOrphan,
                IsLifecycleRisk = Candidate.IsLifecycleRisk,
                Diagnostics = Candidate.Diagnostics,
                EligibilityStatus = Candidate.EligibilityStatus,
                BlockedReasons = Candidate.BlockedReasons,
                TargetSection = Candidate.TargetSection,
                RiskIfNormalSelected = Candidate.RiskIfNormalSelected,
                RiskAfterPolicy = Candidate.RiskAfterPolicy,
                Metadata = metadata
            };
        }
    }
}
