using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Dataset V2 stress recall failure triage；只解释 stress shadow eval 失败，不接正式检索。
/// </summary>
public sealed class RetrievalDatasetV2StressRecallFailureTriageRunner
{
    private const int TopK = 5;

    public RetrievalDatasetV2StressRecallFailureTriageReport BuildReport(
        RetrievalDatasetV2GeneratedDataset dataset,
        bool holdoutOnly = false)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var samples = holdoutOnly
            ? dataset.Samples.Where(static sample => string.Equals(sample.Split, "holdout", StringComparison.OrdinalIgnoreCase)).ToArray()
            : dataset.Samples;
        var corpusById = dataset.CorpusItems
            .Where(static item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(static item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var failures = new List<RetrievalDatasetV2StressRecallFailureDetail>();
        var denseWins = 0;
        var hybridWins = 0;
        var anchorRegressions = 0;

        foreach (var sample in samples)
        {
            var dense = TopKFor(sample, dataset.CorpusItems, ScoreMode.Dense);
            var lexical = TopKFor(sample, dataset.CorpusItems, ScoreMode.Lexical);
            var anchor = TopKFor(sample, dataset.CorpusItems, ScoreMode.Anchor);
            var hybrid = TopKFor(sample, dataset.CorpusItems, ScoreMode.Hybrid);
            var denseHit = Hit(sample, dense.TopK);
            var hybridHit = Hit(sample, hybrid.TopK);
            var anchorHit = Hit(sample, anchor.TopK);
            if (denseHit && !hybridHit)
            {
                denseWins++;
            }

            if (hybridHit && !denseHit)
            {
                hybridWins++;
            }

            if (anchorHit && !hybridHit)
            {
                anchorRegressions++;
            }

            if (hybridHit)
            {
                continue;
            }

            failures.Add(BuildFailure(sample, corpusById, dense, lexical, anchor, hybrid));
        }

        var failureBySplit = CountBy(failures.Select(static failure => failure.Split));
        var failureByDifficulty = CountBy(failures.Select(static failure => failure.Difficulty));
        var failureByReason = CountBy(failures.Select(static failure => failure.FailureReason));
        return new RetrievalDatasetV2StressRecallFailureTriageReport
        {
            OperationId = $"retrieval-dataset-v2-stress-failure-triage-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetId = DatasetId(dataset),
            SampleCount = samples.Count,
            FailureCount = failures.Count,
            FailureCountBySplit = failureBySplit,
            FailureCountByDifficulty = failureByDifficulty,
            FailureCountByReason = failureByReason,
            HoldoutFailureCount = failures.Count(static failure => string.Equals(failure.Split, "holdout", StringComparison.OrdinalIgnoreCase)),
            DenseOnlyWinCount = denseWins,
            HybridWinCount = hybridWins,
            AnchorRegressionCount = anchorRegressions,
            MustHitBelowTopKCount = failures.Count(static failure => string.Equals(failure.FailureReason, RetrievalDatasetV2StressFailureReasons.MustHitBelowTopK, StringComparison.OrdinalIgnoreCase)),
            MustHitMissingFromCandidateSetCount = failures.Count(static failure => string.Equals(failure.FailureReason, RetrievalDatasetV2StressFailureReasons.MustHitMissingFromCandidateSet, StringComparison.OrdinalIgnoreCase)),
            EligibilityBlockedCount = failures.Count(static failure => failure.MustHitBlockedByEligibility),
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = ResolveRecommendation(failureByReason),
            ProfileComparisons = BuildProfileComparisons(samples, dataset.CorpusItems),
            Failures = failures
                .OrderBy(static failure => failure.Split, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static failure => failure.Difficulty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static failure => failure.SampleId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    public RetrievalDatasetV2StressRecallFailureTriageReport BuildClusters(RetrievalDatasetV2GeneratedDataset dataset)
    {
        return BuildReport(dataset, holdoutOnly: false);
    }

    public static string BuildMarkdown(string title, RetrievalDatasetV2StressRecallFailureTriageReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"- DatasetId: `{report.DatasetId}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- FailureCount: `{report.FailureCount}`");
        builder.AppendLine($"- HoldoutFailureCount: `{report.HoldoutFailureCount}`");
        builder.AppendLine($"- DenseOnlyWinCount: `{report.DenseOnlyWinCount}`");
        builder.AppendLine($"- HybridWinCount: `{report.HybridWinCount}`");
        builder.AppendLine($"- AnchorRegressionCount: `{report.AnchorRegressionCount}`");
        builder.AppendLine($"- MustHitBelowTopKCount: `{report.MustHitBelowTopKCount}`");
        builder.AppendLine($"- MustHitMissingFromCandidateSetCount: `{report.MustHitMissingFromCandidateSetCount}`");
        builder.AppendLine($"- EligibilityBlockedCount: `{report.EligibilityBlockedCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendBreakdown(builder, "Failure By Split", report.FailureCountBySplit);
        AppendBreakdown(builder, "Failure By Difficulty", report.FailureCountByDifficulty);
        AppendBreakdown(builder, "Failure By Reason", report.FailureCountByReason);
        builder.AppendLine();
        builder.AppendLine("## Profile Comparisons");
        builder.AppendLine("| Comparison | Left | Right | LeftRecall | RightRecall | LeftOnlyWins | RightOnlyWins | BothMiss |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|");
        foreach (var comparison in report.ProfileComparisons)
        {
            builder.AppendLine($"| {Escape(comparison.ComparisonName)} | {Escape(comparison.LeftProfileName)} | {Escape(comparison.RightProfileName)} | {comparison.LeftRecall:P2} | {comparison.RightRecall:P2} | {comparison.LeftOnlyWinCount} | {comparison.RightOnlyWinCount} | {comparison.BothMissCount} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Failure Details");
        builder.AppendLine("| Sample | Split | Difficulty | Reason | MustHitRank | NearestWrong | Repair |");
        builder.AppendLine("|---|---|---|---|---:|---|---|");
        foreach (var failure in report.Failures.Take(120))
        {
            builder.AppendLine($"| {Escape(failure.SampleId)} | {Escape(failure.Split)} | {Escape(failure.Difficulty)} | {Escape(failure.FailureReason)} | {failure.MustHitRank} | {Escape(failure.NearestWrongCandidateId)} | {Escape(failure.RecommendedRepair)} |");
        }

        return builder.ToString();
    }

    private static IReadOnlyList<RetrievalDatasetV2StressProfileComparison> BuildProfileComparisons(
        IReadOnlyList<RetrievalDatasetV2Sample> samples,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus)
    {
        return
        [
            CompareProfiles(samples, corpus, "dense-only-vs-hybrid-full", ScoreProfile.DenseOnly, ScoreProfile.HybridFull),
            CompareProfiles(samples, corpus, "dense-only-vs-hybrid-with-anchor-shuffle", ScoreProfile.DenseOnly, ScoreProfile.HybridWithAnchorShuffle),
            CompareProfiles(samples, corpus, "dense-only-vs-hybrid-with-metadata-anchor-removed", ScoreProfile.DenseOnly, ScoreProfile.HybridWithMetadataAnchorRemoved),
            CompareProfiles(samples, corpus, "anchor-only-vs-hybrid-full", ScoreProfile.AnchorOnly, ScoreProfile.HybridFull)
        ];
    }

    private static RetrievalDatasetV2StressProfileComparison CompareProfiles(
        IReadOnlyList<RetrievalDatasetV2Sample> samples,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        string comparisonName,
        ScoreProfile leftProfile,
        ScoreProfile rightProfile)
    {
        var leftHits = 0;
        var rightHits = 0;
        var leftOnlyWins = 0;
        var rightOnlyWins = 0;
        var bothHits = 0;
        var bothMiss = 0;
        foreach (var sample in samples)
        {
            var leftHit = Hit(sample, TopKForProfile(sample, corpus, leftProfile).TopK);
            var rightHit = Hit(sample, TopKForProfile(sample, corpus, rightProfile).TopK);
            if (leftHit)
            {
                leftHits++;
            }

            if (rightHit)
            {
                rightHits++;
            }

            if (leftHit && rightHit)
            {
                bothHits++;
                continue;
            }

            if (leftHit)
            {
                leftOnlyWins++;
                continue;
            }

            if (rightHit)
            {
                rightOnlyWins++;
                continue;
            }

            bothMiss++;
        }

        return new RetrievalDatasetV2StressProfileComparison
        {
            ComparisonName = comparisonName,
            LeftProfileName = ProfileName(leftProfile),
            RightProfileName = ProfileName(rightProfile),
            SampleCount = samples.Count,
            LeftHitCount = leftHits,
            RightHitCount = rightHits,
            LeftOnlyWinCount = leftOnlyWins,
            RightOnlyWinCount = rightOnlyWins,
            BothHitCount = bothHits,
            BothMissCount = bothMiss,
            LeftRecall = samples.Count == 0 ? 0 : leftHits / (double)samples.Count,
            RightRecall = samples.Count == 0 ? 0 : rightHits / (double)samples.Count
        };
    }

    private static RetrievalDatasetV2StressRecallFailureDetail BuildFailure(
        RetrievalDatasetV2Sample sample,
        IReadOnlyDictionary<string, RetrievalDatasetV2CorpusItem> corpusById,
        ScoreResult dense,
        ScoreResult lexical,
        ScoreResult anchor,
        ScoreResult hybrid)
    {
        var mustHit = sample.MustHitItemIds.FirstOrDefault(static id => !string.IsNullOrWhiteSpace(id)) ?? string.Empty;
        var mustHitPresent = corpusById.TryGetValue(mustHit, out var mustHitItem);
        var mustHitRank = RankOf(mustHit, hybrid.RankedIds);
        var mustHitCandidateSet = mustHitRank > 0;
        var mustHitBlocked = mustHitPresent && IsBlockedByEligibility(sample, mustHitItem!);
        var targetMismatch = mustHitPresent && !string.Equals(mustHitItem!.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase);
        var nearestWrong = hybrid.TopK.FirstOrDefault(id => !sample.MustHitItemIds.Contains(id, StringComparer.OrdinalIgnoreCase)) ?? string.Empty;
        corpusById.TryGetValue(nearestWrong, out var nearestWrongItem);
        var reason = Classify(sample, mustHitPresent, mustHitCandidateSet, mustHitRank, mustHitBlocked, targetMismatch, dense, lexical, anchor, hybrid);
        return new RetrievalDatasetV2StressRecallFailureDetail
        {
            SampleId = sample.SampleId,
            Split = sample.Split,
            Difficulty = sample.Difficulty,
            QueryText = sample.QueryText,
            ExpectedTargetSection = sample.ExpectedTargetSection,
            MustHitItemIds = sample.MustHitItemIds,
            MustNotHitItemIds = sample.MustNotHitItemIds,
            DenseTopK = dense.TopK,
            LexicalTopK = lexical.TopK,
            AnchorTopK = anchor.TopK,
            HybridTopK = hybrid.TopK,
            MustHitPresentInCorpus = mustHitPresent,
            MustHitPresentInCandidateSet = mustHitCandidateSet,
            MustHitRank = mustHitRank,
            MustHitBlockedByEligibility = mustHitBlocked,
            MustHitTargetSectionMismatch = targetMismatch,
            NearestWrongCandidateId = nearestWrong,
            NearestWrongCandidateKind = nearestWrongItem?.ItemKind ?? string.Empty,
            FailureReason = reason,
            RecommendedRepair = RepairFor(reason)
        };
    }

    private static string Classify(
        RetrievalDatasetV2Sample sample,
        bool mustHitPresent,
        bool mustHitCandidateSet,
        int mustHitRank,
        bool mustHitBlocked,
        bool targetMismatch,
        ScoreResult dense,
        ScoreResult lexical,
        ScoreResult anchor,
        ScoreResult hybrid)
    {
        if (mustHitBlocked)
        {
            return RetrievalDatasetV2StressFailureReasons.MustHitBlockedByEligibility;
        }

        if (targetMismatch)
        {
            return RetrievalDatasetV2StressFailureReasons.TargetSectionMismatch;
        }

        if (!mustHitPresent || !mustHitCandidateSet)
        {
            return RetrievalDatasetV2StressFailureReasons.MustHitMissingFromCandidateSet;
        }

        if (sample.MustNotHitItemIds.Any(id => hybrid.TopK.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressFailureReasons.NegativeDistractorOutranksMustHit;
        }

        if (Hit(sample, anchor.TopK) && !Hit(sample, hybrid.TopK))
        {
            return RetrievalDatasetV2StressFailureReasons.AnchorRankingRegression;
        }

        if (Hit(sample, dense.TopK) && !Hit(sample, hybrid.TopK))
        {
            return RetrievalDatasetV2StressFailureReasons.HybridUnionRankingRegression;
        }

        if (mustHitRank > TopK)
        {
            return RetrievalDatasetV2StressFailureReasons.MustHitBelowTopK;
        }

        if (Tokenize(sample.QueryText).Count <= 3)
        {
            return RetrievalDatasetV2StressFailureReasons.QueryTooSparse;
        }

        if (sample.Difficulty.Contains("relation", StringComparison.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2StressFailureReasons.MultiHopRelationNotRepresented;
        }

        if (sample.Difficulty.Contains("lifecycle", StringComparison.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2StressFailureReasons.LifecycleTrapTooAmbiguous;
        }

        if (!Hit(sample, dense.TopK))
        {
            return RetrievalDatasetV2StressFailureReasons.DenseSemanticMismatch;
        }

        if (!Hit(sample, lexical.TopK))
        {
            return RetrievalDatasetV2StressFailureReasons.LexicalTokenMismatch;
        }

        if (!Hit(sample, anchor.TopK))
        {
            return RetrievalDatasetV2StressFailureReasons.AnchorMetadataInsufficient;
        }

        return RetrievalDatasetV2StressFailureReasons.Unknown;
    }

    private static string RepairFor(string reason)
    {
        return reason switch
        {
            RetrievalDatasetV2StressFailureReasons.AnchorRankingRegression => "Inspect hybrid anchor weighting and avoid anchor score suppressing dense winners.",
            RetrievalDatasetV2StressFailureReasons.HybridUnionRankingRegression => "Preview hybrid union scoring repair so dense wins are preserved in topK.",
            RetrievalDatasetV2StressFailureReasons.QueryTooSparse => "Preview query normalization and sparse-token expansion.",
            RetrievalDatasetV2StressFailureReasons.MultiHopRelationNotRepresented => "Preview relation-aware candidate provider for multi-hop samples.",
            RetrievalDatasetV2StressFailureReasons.NegativeDistractorOutranksMustHit => "Strengthen must-not negative policy in preview scoring.",
            RetrievalDatasetV2StressFailureReasons.AnchorMetadataInsufficient => "Backfill shared anchors or reduce anchor-only dependence.",
            RetrievalDatasetV2StressFailureReasons.LexicalTokenMismatch => "Preview lexical normalization repair.",
            RetrievalDatasetV2StressFailureReasons.DenseSemanticMismatch => "Preview dense query/corpus representation repair.",
            RetrievalDatasetV2StressFailureReasons.MustHitBelowTopK => "Tune ranking so present mustHit candidates are promoted into topK.",
            RetrievalDatasetV2StressFailureReasons.MustHitMissingFromCandidateSet => "Repair candidate generation coverage before ranking changes.",
            RetrievalDatasetV2StressFailureReasons.MustHitBlockedByEligibility => "Review lifecycle/target-section metadata for the sample.",
            RetrievalDatasetV2StressFailureReasons.TargetSectionMismatch => "Repair expected target section or corpus target-section metadata.",
            RetrievalDatasetV2StressFailureReasons.LifecycleTrapTooAmbiguous => "Add clearer non-normal context cues for lifecycle traps.",
            _ => "Inspect sample-level score distribution and corpus metadata."
        };
    }

    private static ScoreResult TopKFor(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        ScoreMode mode)
    {
        var profile = mode switch
        {
            ScoreMode.Dense => ScoreProfile.DenseOnly,
            ScoreMode.Lexical => ScoreProfile.LexicalOnly,
            ScoreMode.Anchor => ScoreProfile.AnchorOnly,
            _ => ScoreProfile.HybridFull
        };
        return TopKForProfile(sample, corpus, profile);
    }

    private static ScoreResult TopKForProfile(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        ScoreProfile profile)
    {
        var queryTokens = Tokenize(sample.QueryText);
        var scored = corpus
            .Select(item =>
            {
                var dense = DenseScore(queryTokens, item, includeTags: profile != ScoreProfile.HybridWithMetadataAnchorRemoved);
                var lexical = LexicalScore(queryTokens, item);
                var anchor = profile is ScoreProfile.HybridWithAnchorShuffle or ScoreProfile.HybridWithMetadataAnchorRemoved
                    ? 0
                    : AnchorScore(queryTokens, item);
                var score = profile switch
                {
                    ScoreProfile.DenseOnly => dense,
                    ScoreProfile.LexicalOnly => lexical,
                    ScoreProfile.AnchorOnly => anchor,
                    ScoreProfile.HybridWithAnchorShuffle => dense + lexical,
                    ScoreProfile.HybridWithMetadataAnchorRemoved => dense + lexical,
                    _ => dense + lexical + anchor * 0.5
                };
                if (sample.MustNotHitItemIds.Contains(item.ItemId, StringComparer.OrdinalIgnoreCase)
                    && HasNegativeConstraintCue(sample.QueryText))
                {
                    score = 0;
                }

                return new ScoredItem(item, score);
            })
            .Where(candidate => candidate.Score > 0)
            .Where(candidate => !IsBlockedByEligibility(sample, candidate.Item))
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ScoreResult(
            scored.Select(static item => item.Item.ItemId).ToArray(),
            scored.Take(TopK).Select(static item => item.Item.ItemId).ToArray());
    }

    private static double DenseScore(
        IReadOnlySet<string> queryTokens,
        RetrievalDatasetV2CorpusItem item,
        bool includeTags = true)
    {
        var itemTokens = Tokenize($"{item.Content} {item.TargetSection} {item.ItemKind} {item.SourceKind}");
        if (includeTags)
        {
            itemTokens.UnionWith(Tokenize(string.Join(' ', item.Tags)));
        }

        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(token => itemTokens.Contains(token));
        return overlap / Math.Sqrt(queryTokens.Count * itemTokens.Count);
    }

    private static string ProfileName(ScoreProfile profile)
    {
        return profile switch
        {
            ScoreProfile.DenseOnly => "dense-only",
            ScoreProfile.LexicalOnly => "lexical-only",
            ScoreProfile.AnchorOnly => "anchor-only",
            ScoreProfile.HybridWithAnchorShuffle => "hybrid-with-anchor-shuffle",
            ScoreProfile.HybridWithMetadataAnchorRemoved => "hybrid-with-metadata-anchor-removed",
            _ => "hybrid-full"
        };
    }

    private static double LexicalScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var itemTokens = Tokenize(item.Content);
        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(token => itemTokens.Contains(token));
        var union = queryTokens.Count + itemTokens.Count - overlap;
        return union == 0 ? 0 : (double)overlap / union;
    }

    private static double AnchorScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var anchors = Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {item.TargetSection}");
        if (queryTokens.Count == 0 || anchors.Count == 0)
        {
            return 0;
        }

        return queryTokens.Count(token => anchors.Contains(token)) / (double)anchors.Count;
    }

    private static bool IsBlockedByEligibility(RetrievalDatasetV2Sample sample, RetrievalDatasetV2CorpusItem item)
    {
        if (!string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase))
        {
            return !(string.Equals(item.Lifecycle, "Active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Current", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Stable", StringComparison.OrdinalIgnoreCase))
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool Hit(RetrievalDatasetV2Sample sample, IReadOnlyList<string> ids)
    {
        return sample.MustHitItemIds.Any(id => ids.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    private static int RankOf(string itemId, IReadOnlyList<string> rankedIds)
    {
        for (var i = 0; i < rankedIds.Count; i++)
        {
            if (string.Equals(rankedIds[i], itemId, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static bool HasNegativeConstraintCue(string queryText)
    {
        return queryText.Contains("excluding", StringComparison.OrdinalIgnoreCase)
            || queryText.Contains("avoid", StringComparison.OrdinalIgnoreCase)
            || queryText.Contains("do not", StringComparison.OrdinalIgnoreCase)
            || queryText.Contains("instead of", StringComparison.OrdinalIgnoreCase)
            || queryText.Contains("without relying", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> Tokenize(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            FlushToken(builder, result);
        }

        FlushToken(builder, result);
        return result;
    }

    private static void FlushToken(StringBuilder builder, ISet<string> result)
    {
        if (builder.Length >= 2)
        {
            result.Add(builder.ToString());
        }

        builder.Clear();
    }

    private static Dictionary<string, int> CountBy(IEnumerable<string> values)
    {
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveRecommendation(IReadOnlyDictionary<string, int> reasonBreakdown)
    {
        if (reasonBreakdown.Count == 0)
        {
            return RetrievalDatasetV2StressFailureTriageRecommendations.ReadyForRankingRepairPreview;
        }

        if (reasonBreakdown.ContainsKey(RetrievalDatasetV2StressFailureReasons.HybridUnionRankingRegression)
            || reasonBreakdown.ContainsKey(RetrievalDatasetV2StressFailureReasons.MustHitBelowTopK))
        {
            return RetrievalDatasetV2StressFailureTriageRecommendations.NeedsHybridUnionScoringRepair;
        }

        if (reasonBreakdown.ContainsKey(RetrievalDatasetV2StressFailureReasons.AnchorRankingRegression)
            || reasonBreakdown.ContainsKey(RetrievalDatasetV2StressFailureReasons.AnchorMetadataInsufficient))
        {
            return RetrievalDatasetV2StressFailureTriageRecommendations.NeedsAnchorRankingRepair;
        }

        if (reasonBreakdown.ContainsKey(RetrievalDatasetV2StressFailureReasons.QueryTooSparse)
            || reasonBreakdown.ContainsKey(RetrievalDatasetV2StressFailureReasons.LexicalTokenMismatch))
        {
            return RetrievalDatasetV2StressFailureTriageRecommendations.NeedsQueryNormalizationRepair;
        }

        if (reasonBreakdown.ContainsKey(RetrievalDatasetV2StressFailureReasons.MultiHopRelationNotRepresented))
        {
            return RetrievalDatasetV2StressFailureTriageRecommendations.NeedsRelationAwareCandidateProvider;
        }

        if (reasonBreakdown.ContainsKey(RetrievalDatasetV2StressFailureReasons.NegativeDistractorOutranksMustHit))
        {
            return RetrievalDatasetV2StressFailureTriageRecommendations.NeedsHarderNegativePolicy;
        }

        return RetrievalDatasetV2StressFailureTriageRecommendations.KeepPreviewOnly;
    }

    private static string DatasetId(RetrievalDatasetV2GeneratedDataset dataset)
    {
        var seed = $"{dataset.CorpusItems.Count}|{dataset.Samples.Count}|{string.Join('|', dataset.CorpusItems.Take(5).Select(static item => item.ItemId))}|{string.Join('|', dataset.Samples.Take(5).Select(static sample => sample.SampleId))}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return $"rdsv2-stress-{Convert.ToHexString(hash).ToLowerInvariant()[..16]}";
    }

    private static void AppendBreakdown(StringBuilder builder, string title, IReadOnlyDictionary<string, int> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine("| Key | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var value in values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {value.Key} | {value.Value} |");
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private enum ScoreMode
    {
        Dense,
        Lexical,
        Anchor,
        Hybrid
    }

    private enum ScoreProfile
    {
        DenseOnly,
        LexicalOnly,
        AnchorOnly,
        HybridFull,
        HybridWithAnchorShuffle,
        HybridWithMetadataAnchorRemoved
    }

    private sealed record ScoredItem(RetrievalDatasetV2CorpusItem Item, double Score);

    private sealed record ScoreResult(IReadOnlyList<string> RankedIds, IReadOnlyList<string> TopK);
}
