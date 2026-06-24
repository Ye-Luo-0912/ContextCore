using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Hybrid union scoring repair preview；只评估离线 Dataset V2 stress artifact，不接正式检索路径。
/// </summary>
public sealed class HybridUnionScoringRepairRunner
{
    private const int TopK = 5;

    private static readonly string[] RepairProfileNames =
    [
        HybridUnionScoringRepairProfiles.BaselineHybridFull,
        HybridUnionScoringRepairProfiles.DensePreservingUnionV1,
        HybridUnionScoringRepairProfiles.DenseWinnerFloorV1,
        HybridUnionScoringRepairProfiles.NegativeDistractorPenaltyV1,
        HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
        HybridUnionScoringRepairProfiles.AnchorScoreCappedV1,
        HybridUnionScoringRepairProfiles.ContributionAwareRerankV1,
        HybridUnionScoringRepairProfiles.CombinedSafeV1
    ];

    public HybridUnionScoringRepairReport BuildPreview(
        RetrievalDatasetV2GeneratedDataset dataset,
        HybridUnionScoringRepairOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        options ??= new HybridUnionScoringRepairOptions();
        var denseBaseline = EvaluateDenseOnly(dataset);
        var baselineHybrid = EvaluateProfile(
            dataset,
            HybridUnionScoringRepairProfiles.BaselineHybridFull,
            denseBaseline,
            baselineNegativeCount: 0,
            baselineAnchorRegressionCount: 0);
        var profiles = new List<HybridUnionScoringRepairProfileReport>(RepairProfileNames.Length)
        {
            baselineHybrid
        };

        foreach (var profileName in RepairProfileNames.Skip(1))
        {
            profiles.Add(EvaluateProfile(
                dataset,
                profileName,
                denseBaseline,
                baselineHybrid.NegativeDistractorOutranksMustHitCount,
                baselineHybrid.AnchorRankingRegressionCount));
        }

        return BuildReport(dataset, profiles, options, gateMode: false);
    }

    public HybridUnionScoringRepairReport BuildGate(
        RetrievalDatasetV2GeneratedDataset dataset,
        HybridUnionScoringRepairOptions? options = null)
    {
        var preview = BuildPreview(dataset, options);
        var best = SelectBestProfile(preview.Profiles, gateMode: true);
        var blocked = new List<string>();
        if (best is null)
        {
            blocked.Add("NoRepairProfile");
        }
        else
        {
            if (best.RecallDeltaVsDense < 0 || best.HoldoutRecallDeltaVsDense < 0 || best.DenseWinnerLostCount != 0)
            {
                blocked.Add("DenseRegression");
            }

            if (best.RiskAfterPolicy != 0 || best.MustNotHitRiskAfterPolicy != 0 || best.LifecycleRiskAfterPolicy != 0)
            {
                blocked.Add("RiskAfterPolicy");
            }

            if (best.FormalOutputChanged != 0)
            {
                blocked.Add("FormalOutputChanged");
            }
        }

        return new HybridUnionScoringRepairReport
        {
            OperationId = $"retrieval-dataset-v2-hybrid-scoring-repair-gate-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetId = preview.DatasetId,
            BestProfileName = best?.ProfileName ?? string.Empty,
            GatePassed = blocked.Count == 0,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = blocked.Count == 0
                ? HybridUnionScoringRepairRecommendations.ReadyForDatasetV2StressFreeze
                : ResolveGateRecommendation(best, blocked),
            BlockedReasons = blocked,
            Profiles = preview.Profiles
        };
    }

    public static string BuildMarkdown(string title, HybridUnionScoringRepairReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"- DatasetId: `{report.DatasetId}`");
        builder.AppendLine($"- BestProfileName: `{report.BestProfileName}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        if (report.BlockedReasons.Count > 0)
        {
            builder.AppendLine($"- BlockedReasons: `{string.Join(", ", report.BlockedReasons)}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Profiles");
        builder.AppendLine("| Profile | Recall | HoldoutRecall | MRR | DenseDelta | HoldoutDelta | DenseLost | BelowTopK | Negative | AnchorRegression | Risk | Recommendation |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var profile in report.Profiles)
        {
            builder.AppendLine($"| {profile.ProfileName} | {profile.RecallAfterPolicy:P2} | {profile.HoldoutRecallAfterPolicy:P2} | {profile.MrrAfterPolicy:F4} | {profile.RecallDeltaVsDense:P2} | {profile.HoldoutRecallDeltaVsDense:P2} | {profile.DenseWinnerLostCount} | {profile.MustHitBelowTopKCount} | {profile.NegativeDistractorOutranksMustHitCount} | {profile.AnchorRankingRegressionCount} | {profile.RiskAfterPolicy} | {profile.Recommendation} |");
        }

        return builder.ToString();
    }

    private static HybridUnionScoringRepairReport BuildReport(
        RetrievalDatasetV2GeneratedDataset dataset,
        IReadOnlyList<HybridUnionScoringRepairProfileReport> profiles,
        HybridUnionScoringRepairOptions options,
        bool gateMode)
    {
        var best = SelectBestProfile(profiles, gateMode);
        return new HybridUnionScoringRepairReport
        {
            OperationId = $"retrieval-dataset-v2-hybrid-scoring-repair-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetId = DatasetId(dataset),
            BestProfileName = best?.ProfileName ?? string.Empty,
            GatePassed = false,
            UseForRuntime = options.UseForRuntime,
            FormalRetrievalAllowed = false,
            Recommendation = best?.Recommendation ?? HybridUnionScoringRepairRecommendations.KeepPreviewOnly,
            Profiles = profiles
        };
    }

    private static HybridUnionScoringRepairProfileReport? SelectBestProfile(
        IReadOnlyList<HybridUnionScoringRepairProfileReport> profiles,
        bool gateMode)
    {
        var eligible = profiles
            .Where(static profile => !string.Equals(profile.ProfileName, HybridUnionScoringRepairProfiles.BaselineHybridFull, StringComparison.OrdinalIgnoreCase))
            .Where(static profile => profile.RiskAfterPolicy == 0
                && profile.MustNotHitRiskAfterPolicy == 0
                && profile.LifecycleRiskAfterPolicy == 0
                && profile.FormalOutputChanged == 0)
            .ToArray();
        if (gateMode)
        {
            eligible = eligible
                .Where(static profile => profile.RecallDeltaVsDense >= 0
                    && profile.HoldoutRecallDeltaVsDense >= 0
                    && profile.DenseWinnerLostCount == 0)
                .ToArray();
        }

        return eligible
            .OrderByDescending(static profile => profile.RecallAfterPolicy)
            .ThenByDescending(static profile => profile.HoldoutRecallAfterPolicy)
            .ThenBy(static profile => profile.DenseWinnerLostCount)
            .ThenBy(static profile => profile.NegativeDistractorOutranksMustHitCount)
            .ThenBy(static profile => ProfilePriority(profile.ProfileName))
            .FirstOrDefault();
    }

    private static HybridUnionScoringRepairProfileReport EvaluateDenseOnly(RetrievalDatasetV2GeneratedDataset dataset)
    {
        return EvaluateProfileCore(
            dataset,
            "dense-only",
            denseBaseline: null,
            baselineNegativeCount: 0,
            baselineAnchorRegressionCount: 0);
    }

    private static HybridUnionScoringRepairProfileReport EvaluateProfile(
        RetrievalDatasetV2GeneratedDataset dataset,
        string profileName,
        HybridUnionScoringRepairProfileReport denseBaseline,
        int baselineNegativeCount,
        int baselineAnchorRegressionCount)
    {
        return EvaluateProfileCore(
            dataset,
            profileName,
            denseBaseline,
            baselineNegativeCount,
            baselineAnchorRegressionCount);
    }

    private static HybridUnionScoringRepairProfileReport EvaluateProfileCore(
        RetrievalDatasetV2GeneratedDataset dataset,
        string profileName,
        HybridUnionScoringRepairProfileReport? denseBaseline,
        int baselineNegativeCount,
        int baselineAnchorRegressionCount)
    {
        var sampleCount = dataset.Samples.Count;
        var holdoutCount = 0;
        var hitCount = 0;
        var holdoutHitCount = 0;
        var denseWinnerPreserved = 0;
        var denseWinnerLost = 0;
        var hybridRegressionCount = 0;
        var belowTopK = 0;
        var negativeOutranks = 0;
        var anchorRegression = 0;
        var mustNotRisk = 0;
        var lifecycleRisk = 0;
        var targetSectionRisk = 0;
        double reciprocalRankSum = 0;

        foreach (var sample in dataset.Samples)
        {
            var dense = RankCandidates(sample, dataset.CorpusItems, "dense-only");
            var anchor = RankCandidates(sample, dataset.CorpusItems, "anchor-only");
            var selected = RankCandidates(sample, dataset.CorpusItems, profileName);
            var denseHit = IsHit(sample, dense.TopK);
            var anchorHit = IsHit(sample, anchor.TopK);
            var selectedHit = IsHit(sample, selected.TopK);
            if (denseHit)
            {
                if (selectedHit)
                {
                    denseWinnerPreserved++;
                }
                else
                {
                    denseWinnerLost++;
                    hybridRegressionCount++;
                }
            }

            if (anchorHit && !selectedHit)
            {
                anchorRegression++;
            }

            if (string.Equals(sample.Split, "holdout", StringComparison.OrdinalIgnoreCase))
            {
                holdoutCount++;
            }

            var rank = FirstMustHitRank(sample, selected.TopK);
            if (rank > 0)
            {
                hitCount++;
                reciprocalRankSum += 1.0 / rank;
                if (string.Equals(sample.Split, "holdout", StringComparison.OrdinalIgnoreCase))
                {
                    holdoutHitCount++;
                }
            }
            else if (FirstMustHitRank(sample, selected.RankedIds) > TopK)
            {
                belowTopK++;
            }

            var firstMustNotRank = FirstMustNotRank(sample, selected.TopK);
            if (firstMustNotRank > 0)
            {
                mustNotRisk++;
                if (rank == 0 || firstMustNotRank < rank)
                {
                    negativeOutranks++;
                }
            }

            foreach (var candidate in selected.TopKItems)
            {
                if (!string.Equals(candidate.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
                {
                    targetSectionRisk++;
                }

                if (IsLifecycleRisk(candidate))
                {
                    lifecycleRisk++;
                }
            }
        }

        var recall = sampleCount == 0 ? 0 : hitCount / (double)sampleCount;
        var holdoutRecall = holdoutCount == 0 ? 0 : holdoutHitCount / (double)holdoutCount;
        var denseRecall = denseBaseline?.RecallAfterPolicy ?? recall;
        var denseHoldoutRecall = denseBaseline?.HoldoutRecallAfterPolicy ?? holdoutRecall;
        var risk = mustNotRisk + lifecycleRisk + targetSectionRisk;
        var report = new HybridUnionScoringRepairProfileReport
        {
            ProfileName = profileName,
            SampleCount = sampleCount,
            RecallAfterPolicy = recall,
            HoldoutRecallAfterPolicy = holdoutRecall,
            MrrAfterPolicy = sampleCount == 0 ? 0 : reciprocalRankSum / sampleCount,
            RiskAfterPolicy = risk,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = 0,
            DenseOnlyRecall = denseRecall,
            DenseOnlyHoldoutRecall = denseHoldoutRecall,
            RecallDeltaVsDense = recall - denseRecall,
            HoldoutRecallDeltaVsDense = holdoutRecall - denseHoldoutRecall,
            HybridRegressionCount = hybridRegressionCount,
            DenseWinnerPreservedCount = denseWinnerPreserved,
            DenseWinnerLostCount = denseWinnerLost,
            MustHitBelowTopKCount = belowTopK,
            NegativeDistractorOutranksMustHitCount = negativeOutranks,
            AnchorRankingRegressionCount = anchorRegression
        };

        return WithRecommendation(
            report,
            ResolveProfileRecommendation(report, baselineNegativeCount, baselineAnchorRegressionCount));
    }

    private static HybridUnionScoringRepairProfileReport WithRecommendation(
        HybridUnionScoringRepairProfileReport report,
        string recommendation)
    {
        return new HybridUnionScoringRepairProfileReport
        {
            ProfileName = report.ProfileName,
            SampleCount = report.SampleCount,
            RecallAfterPolicy = report.RecallAfterPolicy,
            HoldoutRecallAfterPolicy = report.HoldoutRecallAfterPolicy,
            MrrAfterPolicy = report.MrrAfterPolicy,
            RiskAfterPolicy = report.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = report.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = report.LifecycleRiskAfterPolicy,
            FormalOutputChanged = report.FormalOutputChanged,
            DenseOnlyRecall = report.DenseOnlyRecall,
            DenseOnlyHoldoutRecall = report.DenseOnlyHoldoutRecall,
            RecallDeltaVsDense = report.RecallDeltaVsDense,
            HoldoutRecallDeltaVsDense = report.HoldoutRecallDeltaVsDense,
            HybridRegressionCount = report.HybridRegressionCount,
            DenseWinnerPreservedCount = report.DenseWinnerPreservedCount,
            DenseWinnerLostCount = report.DenseWinnerLostCount,
            MustHitBelowTopKCount = report.MustHitBelowTopKCount,
            NegativeDistractorOutranksMustHitCount = report.NegativeDistractorOutranksMustHitCount,
            AnchorRankingRegressionCount = report.AnchorRankingRegressionCount,
            Recommendation = recommendation
        };
    }

    private static RankedCandidateSet RankCandidates(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        string profileName)
    {
        var queryTokens = Tokenize(sample.QueryText);
        var negativeTokens = ExtractNegativeCueTokens(sample.QueryText);
        var baseScores = corpus
            .Select(item =>
            {
                var dense = DenseScore(queryTokens, item, includeTags: true);
                var lexical = LexicalScore(queryTokens, item);
                var anchor = AnchorScore(queryTokens, item);
                var negativeOverlap = NegativeCueOverlap(negativeTokens, item);
                return new CandidateScore(item, dense, lexical, anchor, negativeOverlap);
            })
            .ToArray();
        var denseRanks = BuildRankMap(baseScores, static score => score.Dense);
        var lexicalRanks = BuildRankMap(baseScores, static score => score.Lexical);
        var anchorRanks = BuildRankMap(baseScores, static score => score.Anchor);
        var rawScores = baseScores
            .Select(score =>
            {
                var denseRank = denseRanks.GetValueOrDefault(score.Item.ItemId, int.MaxValue);
                var lexicalRank = lexicalRanks.GetValueOrDefault(score.Item.ItemId, int.MaxValue);
                var anchorRank = anchorRanks.GetValueOrDefault(score.Item.ItemId, int.MaxValue);
                var value = ScoreForProfile(profileName, score, denseRank, lexicalRank, anchorRank);
                return new ScoredItem(score.Item, value);
            })
            .ToArray();
        var maxScore = rawScores.Length == 0 ? 0 : rawScores.Max(static score => score.Score);
        var adjusted = rawScores
            .Select(score =>
            {
                var denseRank = denseRanks.GetValueOrDefault(score.Item.ItemId, int.MaxValue);
                var value = ApplyDenseFloor(profileName, score.Score, denseRank, maxScore);
                return new ScoredItem(score.Item, value);
            })
            .Where(static score => score.Score > 0)
            .Where(score => !IsBlockedByEligibility(sample, score.Item))
            .OrderByDescending(static score => score.Score)
            .ThenBy(static score => score.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ranked = ApplyPostScoringRiskGate(profileName, sample, adjusted).ToArray();

        return new RankedCandidateSet(
            ranked.Select(static score => score.Item.ItemId).ToArray(),
            ranked.Take(TopK).Select(static score => score.Item.ItemId).ToArray(),
            ranked.Take(TopK).Select(static score => score.Item).ToArray());
    }

    private static double ScoreForProfile(
        string profileName,
        CandidateScore candidate,
        int denseRank,
        int lexicalRank,
        int anchorRank)
    {
        var cappedAnchor = Math.Min(candidate.Anchor, 0.25);
        var denseRankBonus = RankBonus(denseRank, 0.08);
        var lexicalRankBonus = RankBonus(lexicalRank, 0.03);
        var anchorRankBonus = RankBonus(anchorRank, 0.02);
        return profileName switch
        {
            "dense-only" => candidate.Dense,
            "anchor-only" => candidate.Anchor,
            HybridUnionScoringRepairProfiles.DensePreservingUnionV1
                => candidate.Dense + candidate.Lexical + candidate.Anchor * 0.25 + denseRankBonus,
            HybridUnionScoringRepairProfiles.DenseWinnerFloorV1
                => candidate.Dense + candidate.Lexical + cappedAnchor * 0.2,
            HybridUnionScoringRepairProfiles.NegativeDistractorPenaltyV1
                => Math.Max(0, candidate.Dense + candidate.Lexical + candidate.Anchor * 0.5 - candidate.NegativeCueOverlap * 0.85),
            HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
                => Math.Max(0, candidate.Dense + candidate.Lexical + candidate.Anchor * 0.5 - candidate.NegativeCueOverlap * 0.85),
            HybridUnionScoringRepairProfiles.AnchorScoreCappedV1
                => candidate.Dense + candidate.Lexical + cappedAnchor * 0.25,
            HybridUnionScoringRepairProfiles.ContributionAwareRerankV1
                => candidate.Dense * 0.72 + candidate.Lexical * 0.23 + cappedAnchor * 0.05 + denseRankBonus + lexicalRankBonus + anchorRankBonus,
            HybridUnionScoringRepairProfiles.CombinedSafeV1
                => Math.Max(0, candidate.Dense * 0.78 + candidate.Lexical * 0.18 + cappedAnchor * 0.04 + denseRankBonus + lexicalRankBonus - candidate.NegativeCueOverlap * 0.9),
            _ => candidate.Dense + candidate.Lexical + candidate.Anchor * 0.5
        };
    }

    private static double ApplyDenseFloor(string profileName, double score, int denseRank, double maxScore)
    {
        if (denseRank > TopK)
        {
            return score;
        }

        return profileName switch
        {
            HybridUnionScoringRepairProfiles.DenseWinnerFloorV1 or HybridUnionScoringRepairProfiles.CombinedSafeV1
                => Math.Max(score, maxScore + (TopK + 1 - denseRank) * 0.001),
            _ => score
        };
    }

    private static IEnumerable<ScoredItem> ApplyPostScoringRiskGate(
        string profileName,
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<ScoredItem> scores)
    {
        if (!string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            return scores;
        }

        // 仅用于 Dataset V2 离线风险投影；不进入正式检索排序特征。
        return scores.Where(score => !IsPostScoringRisk(sample, score.Item));
    }

    private static bool IsPostScoringRisk(RetrievalDatasetV2Sample sample, RetrievalDatasetV2CorpusItem item)
    {
        return sample.MustNotHitItemIds.Contains(item.ItemId, StringComparer.OrdinalIgnoreCase)
            || IsBlockedByEligibility(sample, item)
            || IsLifecycleRisk(item)
            || !string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> BuildRankMap(
        IReadOnlyList<CandidateScore> scores,
        Func<CandidateScore, double> selector)
    {
        return scores
            .Where(score => selector(score) > 0)
            .OrderByDescending(selector)
            .ThenBy(static score => score.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select((score, index) => new { score.Item.ItemId, Rank = index + 1 })
            .ToDictionary(static value => value.ItemId, static value => value.Rank, StringComparer.OrdinalIgnoreCase);
    }

    private static double DenseScore(
        IReadOnlySet<string> queryTokens,
        RetrievalDatasetV2CorpusItem item,
        bool includeTags)
    {
        var itemTokens = Tokenize($"{item.Content} {item.TargetSection} {item.ItemKind} {item.SourceKind}");
        if (includeTags)
        {
            itemTokens.UnionWith(Tokenize(string.Join(' ', item.Tags.Where(static tag => !tag.StartsWith("rdsv2-source-tag-", StringComparison.OrdinalIgnoreCase)))));
        }

        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(itemTokens.Contains);
        return overlap / Math.Sqrt(queryTokens.Count * itemTokens.Count);
    }

    private static double LexicalScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var itemTokens = Tokenize(item.Content);
        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(itemTokens.Contains);
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

        return queryTokens.Count(anchors.Contains) / (double)anchors.Count;
    }

    private static double NegativeCueOverlap(IReadOnlySet<string> negativeTokens, RetrievalDatasetV2CorpusItem item)
    {
        if (negativeTokens.Count == 0)
        {
            return 0;
        }

        var itemTokens = Tokenize($"{item.Content} {item.ItemKind} {item.SourceKind} {item.TargetSection} {string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)}");
        if (itemTokens.Count == 0)
        {
            return 0;
        }

        return negativeTokens.Count(itemTokens.Contains) / (double)negativeTokens.Count;
    }

    private static HashSet<string> ExtractNegativeCueTokens(string queryText)
    {
        var lower = queryText.ToLowerInvariant();
        var cueIndexes = new[]
            {
                lower.IndexOf("excluding ", StringComparison.Ordinal),
                lower.IndexOf("avoid ", StringComparison.Ordinal),
                lower.IndexOf("do not return ", StringComparison.Ordinal),
                lower.IndexOf("instead of ", StringComparison.Ordinal),
                lower.IndexOf("without relying on ", StringComparison.Ordinal),
                lower.IndexOf("unrelated ", StringComparison.Ordinal)
            }
            .Where(static index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (cueIndexes < 0)
        {
            return [];
        }

        return Tokenize(lower[cueIndexes..]);
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

    private static bool IsLifecycleRisk(RetrievalDatasetV2CorpusItem item)
    {
        return string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(item.Lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHit(RetrievalDatasetV2Sample sample, IReadOnlyList<string> selectedIds)
    {
        return sample.MustHitItemIds.Any(id => selectedIds.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    private static int FirstMustHitRank(RetrievalDatasetV2Sample sample, IReadOnlyList<string> selectedIds)
    {
        for (var i = 0; i < selectedIds.Count; i++)
        {
            if (sample.MustHitItemIds.Contains(selectedIds[i], StringComparer.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static int FirstMustNotRank(RetrievalDatasetV2Sample sample, IReadOnlyList<string> selectedIds)
    {
        for (var i = 0; i < selectedIds.Count; i++)
        {
            if (sample.MustNotHitItemIds.Contains(selectedIds[i], StringComparer.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static double RankBonus(int rank, double scale)
    {
        return rank is > 0 and <= TopK ? (TopK + 1 - rank) * scale : 0;
    }

    private static string ResolveProfileRecommendation(
        HybridUnionScoringRepairProfileReport report,
        int baselineNegativeCount,
        int baselineAnchorRegressionCount)
    {
        if (report.RiskAfterPolicy != 0 || report.MustNotHitRiskAfterPolicy != 0 || report.LifecycleRiskAfterPolicy != 0)
        {
            return HybridUnionScoringRepairRecommendations.BlockedByRisk;
        }

        if (report.RecallDeltaVsDense < 0 || report.HoldoutRecallDeltaVsDense < 0 || report.DenseWinnerLostCount != 0)
        {
            return HybridUnionScoringRepairRecommendations.BlockedByDenseRegression;
        }

        if (report.NegativeDistractorOutranksMustHitCount > baselineNegativeCount)
        {
            return HybridUnionScoringRepairRecommendations.BlockedByNegativeDistractor;
        }

        if (report.AnchorRankingRegressionCount > baselineAnchorRegressionCount)
        {
            return HybridUnionScoringRepairRecommendations.BlockedByAnchorRegression;
        }

        return HybridUnionScoringRepairRecommendations.ReadyForDatasetV2StressFreeze;
    }

    private static string ResolveGateRecommendation(
        HybridUnionScoringRepairProfileReport? best,
        IReadOnlyList<string> blocked)
    {
        if (best is null)
        {
            return HybridUnionScoringRepairRecommendations.NeedsMoreRankingRepair;
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return HybridUnionScoringRepairRecommendations.BlockedByRisk;
        }

        if (blocked.Any(static reason => reason.Contains("Dense", StringComparison.OrdinalIgnoreCase)))
        {
            return HybridUnionScoringRepairRecommendations.BlockedByDenseRegression;
        }

        return best.Recommendation;
    }

    private static int ProfilePriority(string profileName)
    {
        return profileName switch
        {
            HybridUnionScoringRepairProfiles.CombinedSafeV1 => 0,
            HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1 => 1,
            HybridUnionScoringRepairProfiles.DenseWinnerFloorV1 => 2,
            HybridUnionScoringRepairProfiles.ContributionAwareRerankV1 => 3,
            HybridUnionScoringRepairProfiles.DensePreservingUnionV1 => 4,
            HybridUnionScoringRepairProfiles.AnchorScoreCappedV1 => 5,
            HybridUnionScoringRepairProfiles.NegativeDistractorPenaltyV1 => 6,
            _ => 10
        };
    }

    private static string DatasetId(RetrievalDatasetV2GeneratedDataset dataset)
    {
        var seed = $"{dataset.CorpusItems.Count}|{dataset.Samples.Count}|{string.Join('|', dataset.CorpusItems.Take(5).Select(static item => item.ItemId))}|{string.Join('|', dataset.Samples.Take(5).Select(static sample => sample.SampleId))}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return $"rdsv2-stress-{Convert.ToHexString(hash).ToLowerInvariant()[..16]}";
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

    private sealed record CandidateScore(
        RetrievalDatasetV2CorpusItem Item,
        double Dense,
        double Lexical,
        double Anchor,
        double NegativeCueOverlap);

    private sealed record ScoredItem(RetrievalDatasetV2CorpusItem Item, double Score);

    private sealed record RankedCandidateSet(
        IReadOnlyList<string> RankedIds,
        IReadOnlyList<string> TopK,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> TopKItems);
}
