using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Hybrid scoring risk regression triage；只解释 preview/eval 风险，不接正式检索。
/// </summary>
public sealed class HybridScoringRiskRegressionTriageRunner
{
    public const string DefaultProfileName = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    private const int TopK = 5;
    private const string MustNotHitRisk = nameof(MustNotHitRisk);
    private const string LifecycleRisk = nameof(LifecycleRisk);
    private const string TargetSectionRisk = nameof(TargetSectionRisk);
    private const string EligibilityBypassRisk = nameof(EligibilityBypassRisk);

    public HybridScoringRiskRegressionTriageReport BuildReport(
        RetrievalDatasetV2GeneratedDataset dataset,
        bool holdoutOnly = false,
        string? profileName = null,
        int? expectedRiskCount = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        profileName = string.IsNullOrWhiteSpace(profileName) ? DefaultProfileName : profileName;
        var samples = holdoutOnly
            ? dataset.Samples.Where(static sample => string.Equals(sample.Split, "holdout", StringComparison.OrdinalIgnoreCase)).ToArray()
            : dataset.Samples;
        var details = new List<HybridScoringRiskRegressionDetail>();

        foreach (var sample in samples)
        {
            var dense = RankCandidates(sample, dataset.CorpusItems, "dense-only");
            var lexical = RankCandidates(sample, dataset.CorpusItems, "lexical-only");
            var anchor = RankCandidates(sample, dataset.CorpusItems, "anchor-only");
            var baseline = RankCandidates(sample, dataset.CorpusItems, HybridUnionScoringRepairProfiles.BaselineHybridFull);
            var repaired = RankCandidates(sample, dataset.CorpusItems, profileName);
            for (var index = 0; index < repaired.TopKItems.Count; index++)
            {
                var candidate = repaired.TopKItems[index];
                var riskTypes = RiskTypes(sample, candidate);
                if (riskTypes.Count == 0)
                {
                    continue;
                }

                var candidateId = candidate.ItemId;
                var wasBlocked = IsBlockedByEligibility(sample, candidate);
                var scoreBefore = baseline.Scores.GetValueOrDefault(candidateId);
                var scoreAfter = repaired.Scores.GetValueOrDefault(candidateId);
                var repairedComponents = repaired.Components.TryGetValue(candidateId, out var components)
                    ? components
                    : new ScoreBreakdown(0, 0, 0, 0);
                var isMustNot = sample.MustNotHitItemIds.Contains(candidateId, StringComparer.OrdinalIgnoreCase);
                var reason = ClassifyRiskReasonForDiagnostics(
                    wasBlocked,
                    isMustNot,
                    candidate.Lifecycle,
                    candidate.ReplacementState,
                    candidate.TargetSection,
                    sample.ExpectedTargetSection,
                    scoreBefore,
                    scoreAfter,
                    profileName);
                details.Add(new HybridScoringRiskRegressionDetail
                {
                    SampleId = sample.SampleId,
                    Split = sample.Split,
                    Difficulty = sample.Difficulty,
                    QueryText = sample.QueryText,
                    ProfileName = profileName,
                    CandidateId = candidateId,
                    CandidateRank = index + 1,
                    CandidateKind = candidate.ItemKind,
                    SourceKind = candidate.SourceKind,
                    Lifecycle = candidate.Lifecycle,
                    ReviewStatus = candidate.ReviewStatus,
                    TargetSection = candidate.TargetSection,
                    RiskType = string.Join(",", riskTypes),
                    RiskReason = reason,
                    WasEligibleBeforeRepair = !wasBlocked,
                    WasBlockedBeforeRepair = wasBlocked,
                    DenseRank = RankOf(candidateId, dense.RankedIds),
                    LexicalRank = RankOf(candidateId, lexical.RankedIds),
                    AnchorRank = RankOf(candidateId, anchor.RankedIds),
                    BaselineHybridRank = RankOf(candidateId, baseline.RankedIds),
                    RepairedHybridRank = RankOf(candidateId, repaired.RankedIds),
                    ScoreBeforeRepair = scoreBefore,
                    ScoreAfterRepair = scoreAfter,
                    ScoreDelta = scoreAfter - scoreBefore,
                    ContributionSource = ContributionSource(repairedComponents),
                    NearestMustHitRank = FirstRank(sample.MustHitItemIds, repaired.RankedIds),
                    NearestMustNotRank = FirstRank(sample.MustNotHitItemIds, repaired.RankedIds),
                    RecommendedFix = RecommendedFix(reason)
                });
            }
        }

        var riskByType = details
            .SelectMany(static detail => detail.RiskType.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var riskByDifficulty = CountBy(details.Select(static detail => detail.Difficulty));
        var riskBySplit = CountBy(details.Select(static detail => detail.Split));
        var blockedReintroduced = details.Count(static detail => string.Equals(detail.RiskReason, HybridScoringRiskRegressionReasons.BlockedCandidateReintroduced, StringComparison.OrdinalIgnoreCase));
        var eligibilityBypass = details.Count(static detail => detail.WasBlockedBeforeRepair);
        var mustNotPromoted = details.Count(static detail => detail.RiskType.Contains(MustNotHitRisk, StringComparison.OrdinalIgnoreCase));
        var lifecyclePromoted = details.Count(static detail => detail.RiskType.Contains(LifecycleRisk, StringComparison.OrdinalIgnoreCase));
        var riskProjectionMismatch = expectedRiskCount.HasValue && expectedRiskCount.Value != details.Count ? 1 : 0;
        var scoreCapRepairable = details.Count(static detail => detail.ScoreDelta > 0 && !string.Equals(detail.ContributionSource, "dense", StringComparison.OrdinalIgnoreCase));
        var recommendation = ResolveRecommendation(
            details.Count,
            riskProjectionMismatch,
            eligibilityBypass,
            lifecyclePromoted,
            mustNotPromoted,
            scoreCapRepairable);

        return new HybridScoringRiskRegressionTriageReport
        {
            OperationId = $"retrieval-dataset-v2-hybrid-scoring-risk-triage-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetId = DatasetId(dataset),
            ProfileName = profileName,
            SampleCount = samples.Count,
            RiskCandidateCount = details.Count,
            RiskByType = riskByType,
            RiskByDifficulty = riskByDifficulty,
            RiskBySplit = riskBySplit,
            BlockedCandidateReintroducedCount = blockedReintroduced,
            MustNotCandidatePromotedCount = mustNotPromoted,
            LifecycleRiskPromotedCount = lifecyclePromoted,
            RiskProjectionMismatchCount = riskProjectionMismatch,
            EligibilityBypassCount = eligibilityBypass,
            RepairableByPostScoringRiskGateCount = details.Count,
            RepairableByScoreCapCount = scoreCapRepairable,
            UnsafeProfileCount = details.Count == 0 ? 0 : 1,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = recommendation,
            Details = details
                .OrderBy(static detail => detail.Split, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static detail => detail.Difficulty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static detail => detail.SampleId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static detail => detail.CandidateRank)
                .ThenBy(static detail => detail.CandidateId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    public static string BuildMarkdown(string title, HybridScoringRiskRegressionTriageReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"- DatasetId: `{report.DatasetId}`");
        builder.AppendLine($"- ProfileName: `{report.ProfileName}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- RiskCandidateCount: `{report.RiskCandidateCount}`");
        builder.AppendLine($"- BlockedCandidateReintroducedCount: `{report.BlockedCandidateReintroducedCount}`");
        builder.AppendLine($"- MustNotCandidatePromotedCount: `{report.MustNotCandidatePromotedCount}`");
        builder.AppendLine($"- LifecycleRiskPromotedCount: `{report.LifecycleRiskPromotedCount}`");
        builder.AppendLine($"- RiskProjectionMismatchCount: `{report.RiskProjectionMismatchCount}`");
        builder.AppendLine($"- EligibilityBypassCount: `{report.EligibilityBypassCount}`");
        builder.AppendLine($"- RepairableByPostScoringRiskGateCount: `{report.RepairableByPostScoringRiskGateCount}`");
        builder.AppendLine($"- RepairableByScoreCapCount: `{report.RepairableByScoreCapCount}`");
        builder.AppendLine($"- UnsafeProfileCount: `{report.UnsafeProfileCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        AppendBreakdown(builder, "Risk By Type", report.RiskByType);
        AppendBreakdown(builder, "Risk By Split", report.RiskBySplit);
        AppendBreakdown(builder, "Risk By Difficulty", report.RiskByDifficulty);
        builder.AppendLine();
        builder.AppendLine("## Risk Details");
        builder.AppendLine("| Sample | Split | Difficulty | Candidate | Rank | Type | Reason | DenseRank | BaselineRank | RepairedRank | ScoreDelta | Fix |");
        builder.AppendLine("|---|---|---|---|---:|---|---|---:|---:|---:|---:|---|");
        foreach (var detail in report.Details.Take(120))
        {
            builder.AppendLine($"| {Escape(detail.SampleId)} | {Escape(detail.Split)} | {Escape(detail.Difficulty)} | {Escape(detail.CandidateId)} | {detail.CandidateRank} | {Escape(detail.RiskType)} | {Escape(detail.RiskReason)} | {detail.DenseRank} | {detail.BaselineHybridRank} | {detail.RepairedHybridRank} | {detail.ScoreDelta:F4} | {Escape(detail.RecommendedFix)} |");
        }

        return builder.ToString();
    }

    public static string ClassifyRiskReasonForDiagnostics(
        bool wasBlockedBeforeRepair,
        bool isMustNotCandidate,
        string lifecycle,
        string replacementState,
        string targetSection,
        string expectedTargetSection,
        double scoreBeforeRepair,
        double scoreAfterRepair,
        string profileName)
    {
        if (wasBlockedBeforeRepair)
        {
            return HybridScoringRiskRegressionReasons.BlockedCandidateReintroduced;
        }

        if (!string.Equals(targetSection, expectedTargetSection, StringComparison.OrdinalIgnoreCase))
        {
            return HybridScoringRiskRegressionReasons.TargetSectionMismatchPromoted;
        }

        if (string.Equals(lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase))
        {
            return HybridScoringRiskRegressionReasons.DeprecatedCandidatePromoted;
        }

        if (string.Equals(lifecycle, "Historical", StringComparison.OrdinalIgnoreCase))
        {
            return HybridScoringRiskRegressionReasons.HistoricalCandidatePromoted;
        }

        if (string.Equals(replacementState, "superseded", StringComparison.OrdinalIgnoreCase))
        {
            return HybridScoringRiskRegressionReasons.LifecycleRiskPromoted;
        }

        if (isMustNotCandidate
            && string.Equals(profileName, HybridUnionScoringRepairProfiles.NegativeDistractorPenaltyV1, StringComparison.OrdinalIgnoreCase)
            && scoreAfterRepair > scoreBeforeRepair)
        {
            return HybridScoringRiskRegressionReasons.NegativePenaltyOverPromotedWrongCandidate;
        }

        if (isMustNotCandidate)
        {
            return HybridScoringRiskRegressionReasons.MustNotCandidatePromoted;
        }

        if (scoreAfterRepair > scoreBeforeRepair)
        {
            return HybridScoringRiskRegressionReasons.ScoreFusionOrderBug;
        }

        return HybridScoringRiskRegressionReasons.Unknown;
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
                return new ScoredItem(score.Item, value, new ScoreBreakdown(score.Dense, score.Lexical, score.Anchor, score.NegativeCueOverlap));
            })
            .ToArray();
        var maxScore = rawScores.Length == 0 ? 0 : rawScores.Max(static score => score.Score);
        var adjusted = rawScores
            .Select(score =>
            {
                var denseRank = denseRanks.GetValueOrDefault(score.Item.ItemId, int.MaxValue);
                var value = ApplyDenseFloor(profileName, score.Score, denseRank, maxScore);
                return new ScoredItem(score.Item, value, score.Components);
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
            ranked.Take(TopK).Select(static score => score.Item).ToArray(),
            ranked.ToDictionary(static score => score.Item.ItemId, static score => score.Score, StringComparer.OrdinalIgnoreCase),
            ranked.ToDictionary(static score => score.Item.ItemId, static score => score.Components, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> RiskTypes(RetrievalDatasetV2Sample sample, RetrievalDatasetV2CorpusItem candidate)
    {
        var values = new List<string>(4);
        if (IsBlockedByEligibility(sample, candidate))
        {
            values.Add(EligibilityBypassRisk);
        }

        if (sample.MustNotHitItemIds.Contains(candidate.ItemId, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(MustNotHitRisk);
        }

        if (IsLifecycleRisk(candidate))
        {
            values.Add(LifecycleRisk);
        }

        if (!string.Equals(candidate.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
        {
            values.Add(TargetSectionRisk);
        }

        return values;
    }

    private static string ContributionSource(ScoreBreakdown score)
    {
        var max = Math.Max(score.Dense, Math.Max(score.Lexical, score.Anchor));
        if (max <= 0)
        {
            return score.NegativeCueOverlap > 0 ? "negative-penalty" : "none";
        }

        if (Math.Abs(score.Dense - max) < 0.000001)
        {
            return "dense";
        }

        if (Math.Abs(score.Lexical - max) < 0.000001)
        {
            return "lexical";
        }

        return "anchor";
    }

    private static string RecommendedFix(string reason)
    {
        return reason switch
        {
            HybridScoringRiskRegressionReasons.BlockedCandidateReintroduced
                or HybridScoringRiskRegressionReasons.EligibilityPolicyBypassed
                => "Keep eligibility filtering after union/dedup and before final topK.",
            HybridScoringRiskRegressionReasons.MustNotCandidatePromoted
                or HybridScoringRiskRegressionReasons.NegativePenaltyOverPromotedWrongCandidate
                => "Add post-scoring risk gate for must-not candidates before final topK.",
            HybridScoringRiskRegressionReasons.DeprecatedCandidatePromoted
                or HybridScoringRiskRegressionReasons.HistoricalCandidatePromoted
                or HybridScoringRiskRegressionReasons.LifecycleRiskPromoted
                => "Keep lifecycle risk gate effective after scoring repair.",
            HybridScoringRiskRegressionReasons.TargetSectionMismatchPromoted
                => "Apply targetSection gate after repaired scoring.",
            HybridScoringRiskRegressionReasons.ScoreFusionOrderBug
                => "Inspect score fusion ordering and cap non-dense boost.",
            HybridScoringRiskRegressionReasons.RiskProjectionMismatch
                => "Recompute risk projection on final topK.",
            _ => "Inspect repaired topK with score/risk projection."
        };
    }

    private static string ResolveRecommendation(
        int riskCandidateCount,
        int riskProjectionMismatchCount,
        int eligibilityBypassCount,
        int lifecycleRiskPromotedCount,
        int mustNotCandidatePromotedCount,
        int scoreCapRepairableCount)
    {
        if (riskProjectionMismatchCount > 0)
        {
            return HybridScoringRiskRegressionRecommendations.NeedsRiskProjectionFix;
        }

        if (eligibilityBypassCount > 0)
        {
            return HybridScoringRiskRegressionRecommendations.NeedsEligibilityOrderFix;
        }

        if (lifecycleRiskPromotedCount > 0)
        {
            return HybridScoringRiskRegressionRecommendations.UnsafeProfileDiscard;
        }

        if (mustNotCandidatePromotedCount > 0)
        {
            return HybridScoringRiskRegressionRecommendations.NeedsPostScoringRiskGate;
        }

        if (scoreCapRepairableCount > 0)
        {
            return HybridScoringRiskRegressionRecommendations.NeedsScoreCap;
        }

        return riskCandidateCount == 0
            ? HybridScoringRiskRegressionRecommendations.ReadyForSafeScoringRepair
            : HybridScoringRiskRegressionRecommendations.KeepPreviewOnly;
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
            "lexical-only" => candidate.Lexical,
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
        return scores.Where(score => RiskTypes(sample, score.Item).Count == 0);
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

    private static int RankOf(string itemId, IReadOnlyList<string> rankedIds)
    {
        for (var index = 0; index < rankedIds.Count; index++)
        {
            if (string.Equals(rankedIds[index], itemId, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static int FirstRank(IReadOnlyList<string> itemIds, IReadOnlyList<string> rankedIds)
    {
        var ranks = itemIds
            .Select(id => RankOf(id, rankedIds))
            .Where(static rank => rank > 0)
            .ToArray();
        return ranks.Length == 0 ? 0 : ranks.Min();
    }

    private static double RankBonus(int rank, double scale)
    {
        return rank is > 0 and <= TopK ? (TopK + 1 - rank) * scale : 0;
    }

    private static Dictionary<string, int> CountBy(IEnumerable<string> values)
    {
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
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

    private static void AppendBreakdown(StringBuilder builder, string title, IReadOnlyDictionary<string, int> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var pair in values.OrderByDescending(static pair => pair.Value).ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- `{pair.Key}`: `{pair.Value}`");
        }
    }

    private static string Escape(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private sealed record CandidateScore(
        RetrievalDatasetV2CorpusItem Item,
        double Dense,
        double Lexical,
        double Anchor,
        double NegativeCueOverlap);

    private sealed record ScoreBreakdown(
        double Dense,
        double Lexical,
        double Anchor,
        double NegativeCueOverlap);

    private sealed record ScoredItem(
        RetrievalDatasetV2CorpusItem Item,
        double Score,
        ScoreBreakdown Components);

    private sealed record RankedCandidateSet(
        IReadOnlyList<string> RankedIds,
        IReadOnlyList<string> TopK,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> TopKItems,
        IReadOnlyDictionary<string, double> Scores,
        IReadOnlyDictionary<string, ScoreBreakdown> Components);
}
