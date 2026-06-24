using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Shadow package comparison；只构建离线 package envelope，不写正式 package，不改变 PackingPolicy。
/// </summary>
public sealed class VectorShadowPackageComparisonRunner
{
    private const int TopK = 5;

    public VectorShadowPackageComparisonReport BuildComparison(
        RetrievalDatasetV2GeneratedDataset? dataset,
        GuardedFormalRetrievalPreviewReport? guardedPreviewGate,
        VectorShadowPackageComparisonOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport(dataset, guardedPreviewGate, options, gateMode: false, sourceReports);

    public VectorShadowPackageComparisonReport BuildGate(
        RetrievalDatasetV2GeneratedDataset? dataset,
        GuardedFormalRetrievalPreviewReport? guardedPreviewGate,
        VectorShadowPackageComparisonOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport(dataset, guardedPreviewGate, options, gateMode: true, sourceReports);

    public static string BuildMarkdown(string title, VectorShadowPackageComparisonReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- ComparisonPassed: `{report.ComparisonPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ProfileName: `{report.ProfileName}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- QueryCount: `{report.QueryCount}`");
        builder.AppendLine($"- BaselinePackageCount: `{report.BaselinePackageCount}`");
        builder.AppendLine($"- ShadowPackageCount: `{report.ShadowPackageCount}`");
        builder.AppendLine($"- CandidateAddCount: `{report.CandidateAddCount}`");
        builder.AppendLine($"- CandidateRemoveCount: `{report.CandidateRemoveCount}`");
        builder.AppendLine($"- CandidateUnchangedCount: `{report.CandidateUnchangedCount}`");
        builder.AppendLine($"- SectionChangedCount: `{report.SectionChangedCount}`");
        builder.AppendLine($"- TokenDeltaTotal: `{report.TokenDeltaTotal}`");
        builder.AppendLine($"- TokenDeltaMax: `{report.TokenDeltaMax}`");
        builder.AppendLine($"- ConstraintCoverageDelta: `{report.ConstraintCoverageDelta:F4}`");
        builder.AppendLine($"- RelationCoverageDelta: `{report.RelationCoverageDelta:F4}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- ShadowPackageWritten: `{report.ShadowPackageWritten}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- Shadow package 只写离线报告，不写正式 package artifact。");
        builder.AppendLine("- `PackingPolicyChanged=false` 与 `PackageOutputChanged=false` 是本阶段 gate 条件。");
        builder.AppendLine("- `UseForRuntime`、`FormalRetrievalAllowed`、`ReadyForRuntimeSwitch` 均保持 `false`。");
        builder.AppendLine();
        builder.AppendLine("## Source Reports");
        if (report.SourceReports.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in report.SourceReports.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: `{pair.Value}`");
            }
        }

        return builder.ToString();
    }

    private static VectorShadowPackageComparisonReport BuildReport(
        RetrievalDatasetV2GeneratedDataset? dataset,
        GuardedFormalRetrievalPreviewReport? guardedPreviewGate,
        VectorShadowPackageComparisonOptions? options,
        bool gateMode,
        IReadOnlyDictionary<string, string>? sourceReports)
    {
        options ??= new VectorShadowPackageComparisonOptions();
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName;
        var stats = BuildStats(dataset, profileName);
        var riskAfterPolicy = Math.Max(stats.RiskAfterPolicy, guardedPreviewGate?.RiskAfterPolicy ?? 0);
        var mustNotRisk = Math.Max(stats.MustNotHitRiskAfterPolicy, guardedPreviewGate?.MustNotHitRiskAfterPolicy ?? 0);
        var lifecycleRisk = Math.Max(stats.LifecycleRiskAfterPolicy, guardedPreviewGate?.LifecycleRiskAfterPolicy ?? 0);
        var formalOutputChanged = guardedPreviewGate?.FormalOutputChanged ?? 0;
        var packageOutputChanged = false;
        var packingPolicyChanged = false;
        var runtimeMutated = options.UseForRuntime || options.FormalRetrievalAllowed;
        var blocked = new List<string>();

        if (options.RequireGuardedFormalPreviewPassed
            && (guardedPreviewGate is null
                || !guardedPreviewGate.GatePassed
                || !string.Equals(guardedPreviewGate.Recommendation, GuardedFormalRetrievalPreviewRecommendations.ReadyForShadowPackageComparison, StringComparison.OrdinalIgnoreCase)))
        {
            blocked.Add("GuardedFormalRetrievalPreviewGateNotPassed");
        }

        if (!string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("PreviewProfileNotFrozenBestProfile");
        }

        if (riskAfterPolicy > options.MaxRiskAllowed || mustNotRisk > options.MaxRiskAllowed || lifecycleRisk > options.MaxRiskAllowed)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (formalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (packageOutputChanged)
        {
            blocked.Add("PackageOutputChanged");
        }

        if (packingPolicyChanged)
        {
            blocked.Add("PackingPolicyChanged");
        }

        if (runtimeMutated)
        {
            blocked.Add("RuntimeMutationAttempt");
        }

        if (stats.ConstraintCoverageDelta < 0)
        {
            blocked.Add("ConstraintCoverageRegression");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        return new VectorShadowPackageComparisonReport
        {
            OperationId = gateMode
                ? $"vector-shadow-package-comparison-gate-{Guid.NewGuid():N}"
                : $"vector-shadow-package-comparison-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ComparisonPassed = passed,
            GatePassed = gateMode && passed,
            ProfileName = profileName,
            SampleCount = stats.SampleCount,
            QueryCount = stats.QueryCount,
            BaselinePackageCount = stats.BaselinePackageCount,
            ShadowPackageCount = stats.ShadowPackageCount,
            CandidateAddCount = stats.CandidateAddCount,
            CandidateRemoveCount = stats.CandidateRemoveCount,
            CandidateUnchangedCount = stats.CandidateUnchangedCount,
            SectionChangedCount = stats.SectionChangedCount,
            TokenDeltaTotal = stats.TokenDeltaTotal,
            TokenDeltaMax = stats.TokenDeltaMax,
            ConstraintCoverageDelta = stats.ConstraintCoverageDelta,
            RelationCoverageDelta = stats.RelationCoverageDelta,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            ShadowPackageWritten = false,
            RuntimeMutated = false,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            Recommendation = passed
                ? VectorShadowPackageComparisonRecommendations.ReadyForScopedFormalPreviewOptIn
                : ResolveRecommendation(distinctBlocked, formalOutputChanged, packageOutputChanged, packingPolicyChanged, runtimeMutated),
            BlockedReasons = distinctBlocked,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ComparisonStats BuildStats(RetrievalDatasetV2GeneratedDataset? dataset, string profileName)
    {
        if (dataset is null || dataset.Samples.Count == 0 || dataset.CorpusItems.Count == 0)
        {
            return new ComparisonStats();
        }

        var candidateAdd = 0;
        var candidateRemove = 0;
        var unchanged = 0;
        var sectionChanged = 0;
        var tokenDeltaTotal = 0;
        var tokenDeltaMax = 0;
        var risk = 0;
        var mustNotRisk = 0;
        var lifecycleRisk = 0;
        double constraintDeltaTotal = 0;
        double relationDeltaTotal = 0;
        foreach (var sample in dataset.Samples)
        {
            var baseline = RankCandidates(sample, dataset.CorpusItems, "dense-only");
            var shadow = RankCandidates(sample, dataset.CorpusItems, profileName);
            var baselineIds = baseline.Select(static item => item.ItemId).ToArray();
            var shadowIds = shadow.Select(static item => item.ItemId).ToArray();
            candidateAdd += shadowIds.Count(id => !baselineIds.Contains(id, StringComparer.OrdinalIgnoreCase));
            candidateRemove += baselineIds.Count(id => !shadowIds.Contains(id, StringComparer.OrdinalIgnoreCase));
            unchanged += shadowIds.Count(id => baselineIds.Contains(id, StringComparer.OrdinalIgnoreCase));
            for (var i = 0; i < shadow.Count; i++)
            {
                if (i < baseline.Count
                    && !string.Equals(shadow[i].TargetSection, baseline[i].TargetSection, StringComparison.OrdinalIgnoreCase))
                {
                    sectionChanged++;
                }

                if (IsRisk(sample, shadow[i]))
                {
                    risk++;
                }

                if (sample.MustNotHitItemIds.Contains(shadow[i].ItemId, StringComparer.OrdinalIgnoreCase))
                {
                    mustNotRisk++;
                }

                if (IsLifecycleRisk(shadow[i]))
                {
                    lifecycleRisk++;
                }
            }

            var baselineTokens = baseline.Sum(EstimateTokens);
            var shadowTokens = shadow.Sum(EstimateTokens);
            var delta = shadowTokens - baselineTokens;
            tokenDeltaTotal += delta;
            tokenDeltaMax = Math.Max(tokenDeltaMax, delta);
            constraintDeltaTotal += Coverage(sample, shadow) - Coverage(sample, baseline);
            relationDeltaTotal += RelationCoverage(sample, shadow) - RelationCoverage(sample, baseline);
        }

        return new ComparisonStats(
            dataset.Samples.Count,
            dataset.Samples.Count,
            dataset.Samples.Count,
            dataset.Samples.Count,
            candidateAdd,
            candidateRemove,
            unchanged,
            sectionChanged,
            tokenDeltaTotal,
            tokenDeltaMax,
            constraintDeltaTotal / dataset.Samples.Count,
            relationDeltaTotal / dataset.Samples.Count,
            risk,
            mustNotRisk,
            lifecycleRisk);
    }

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> RankCandidates(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        string profileName)
    {
        var queryTokens = Tokenize(sample.QueryText);
        var negativeTokens = ExtractNegativeCueTokens(sample.QueryText);
        var scored = corpusItems
            .Select(item =>
            {
                var dense = DenseScore(queryTokens, item);
                var lexical = LexicalScore(queryTokens, item);
                var anchor = AnchorScore(queryTokens, item);
                var negative = NegativeCueOverlap(negativeTokens, item);
                return new ScoredItem(item, ScoreForProfile(profileName, dense, lexical, anchor, negative));
            })
            .Where(static item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            scored = scored
                .Where(item => !IsRisk(sample, item.Item))
                .ToArray();
        }

        return scored
            .Take(TopK)
            .Select(static item => item.Item)
            .ToArray();
    }

    private static double ScoreForProfile(string profileName, double dense, double lexical, double anchor, double negativeCueOverlap)
    {
        var cappedAnchor = Math.Min(anchor, 0.25);
        return profileName switch
        {
            "dense-only" => dense,
            HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
                => Math.Max(0, dense + lexical + anchor * 0.5 - negativeCueOverlap * 0.85),
            HybridUnionScoringRepairProfiles.CombinedSafeV1
                => Math.Max(0, dense * 0.78 + lexical * 0.18 + cappedAnchor * 0.04 - negativeCueOverlap * 0.9),
            HybridUnionScoringRepairProfiles.ContributionAwareRerankV1
                => dense * 0.72 + lexical * 0.23 + cappedAnchor * 0.05,
            HybridUnionScoringRepairProfiles.AnchorScoreCappedV1
                => dense + lexical + cappedAnchor * 0.25,
            HybridUnionScoringRepairProfiles.DenseWinnerFloorV1
                => dense + lexical + cappedAnchor * 0.2,
            HybridUnionScoringRepairProfiles.DensePreservingUnionV1
                => dense + lexical + anchor * 0.25,
            HybridUnionScoringRepairProfiles.NegativeDistractorPenaltyV1
                => Math.Max(0, dense + lexical + anchor * 0.5 - negativeCueOverlap * 0.85),
            _ => dense + lexical + anchor * 0.5
        };
    }

    private static double Coverage(RetrievalDatasetV2Sample sample, IReadOnlyList<RetrievalDatasetV2CorpusItem> candidates)
    {
        if (sample.RequiredRelations.Count == 0 && sample.EvidenceRefs.Count == 0 && sample.SourceRefs.Count == 0)
        {
            return candidates.Count == 0 ? 0 : 1;
        }

        var covered = 0;
        if (sample.RequiredRelations.Count == 0
            || candidates.SelectMany(static item => item.Relations).Any(relation => sample.RequiredRelations.Contains(relation.RelationId, StringComparer.OrdinalIgnoreCase)))
        {
            covered++;
        }

        if (sample.EvidenceRefs.Count == 0
            || candidates.SelectMany(static item => item.EvidenceRefs).Any(reference => sample.EvidenceRefs.Contains(reference, StringComparer.OrdinalIgnoreCase)))
        {
            covered++;
        }

        if (sample.SourceRefs.Count == 0
            || candidates.SelectMany(static item => item.SourceRefs).Any(reference => sample.SourceRefs.Contains(reference, StringComparer.OrdinalIgnoreCase)))
        {
            covered++;
        }

        return covered / 3d;
    }

    private static double RelationCoverage(RetrievalDatasetV2Sample sample, IReadOnlyList<RetrievalDatasetV2CorpusItem> candidates)
    {
        if (sample.RequiredRelations.Count == 0)
        {
            return candidates.Any(static item => item.Relations.Count > 0) ? 1 : 0;
        }

        var candidateRelations = candidates
            .SelectMany(static item => item.Relations)
            .Select(static relation => relation.RelationId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sample.RequiredRelations.Count(relation => candidateRelations.Contains(relation)) / (double)sample.RequiredRelations.Count;
    }

    private static bool IsRisk(RetrievalDatasetV2Sample sample, RetrievalDatasetV2CorpusItem item)
        => sample.MustNotHitItemIds.Contains(item.ItemId, StringComparer.OrdinalIgnoreCase)
            || IsBlockedByEligibility(sample, item)
            || IsLifecycleRisk(item)
            || !string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase);

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
        => string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(item.Lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase));

    private static int EstimateTokens(RetrievalDatasetV2CorpusItem item)
        => Math.Max(1, Tokenize($"{item.Content} {string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)}").Count);

    private static double DenseScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var itemTokens = Tokenize($"{item.Content} {item.TargetSection} {item.ItemKind} {item.SourceKind} {string.Join(' ', item.Tags.Where(static tag => !tag.StartsWith("rdsv2-source-tag-", StringComparison.OrdinalIgnoreCase)))}");
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
        return itemTokens.Count == 0 ? 0 : negativeTokens.Count(itemTokens.Contains) / (double)negativeTokens.Count;
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
        return cueIndexes < 0 ? [] : Tokenize(lower[cueIndexes..]);
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
        if (builder.Length == 0)
        {
            return;
        }

        result.Add(builder.ToString());
        builder.Clear();
    }

    private static string ResolveRecommendation(
        IReadOnlyList<string> blocked,
        int formalOutputChanged,
        bool packageOutputChanged,
        bool packingPolicyChanged,
        bool runtimeMutated)
    {
        if (runtimeMutated || blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorShadowPackageComparisonRecommendations.BlockedByRuntimeMutation;
        }

        if (packingPolicyChanged || blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorShadowPackageComparisonRecommendations.BlockedByPackingPolicyChange;
        }

        if (packageOutputChanged || blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorShadowPackageComparisonRecommendations.BlockedByPackageOutputChange;
        }

        if (formalOutputChanged != 0 || blocked.Any(static reason => reason.Contains("FormalOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorShadowPackageComparisonRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorShadowPackageComparisonRecommendations.BlockedByRisk;
        }

        if (blocked.Any(static reason => reason.Contains("ConstraintCoverage", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorShadowPackageComparisonRecommendations.BlockedByConstraintCoverageRegression;
        }

        if (blocked.Any(static reason => reason.Contains("Token", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorShadowPackageComparisonRecommendations.BlockedByTokenBudgetRegression;
        }

        return VectorShadowPackageComparisonRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }

    private readonly record struct ScoredItem(RetrievalDatasetV2CorpusItem Item, double Score);

    private readonly record struct ComparisonStats(
        int SampleCount = 0,
        int QueryCount = 0,
        int BaselinePackageCount = 0,
        int ShadowPackageCount = 0,
        int CandidateAddCount = 0,
        int CandidateRemoveCount = 0,
        int CandidateUnchangedCount = 0,
        int SectionChangedCount = 0,
        int TokenDeltaTotal = 0,
        int TokenDeltaMax = 0,
        double ConstraintCoverageDelta = 0,
        double RelationCoverageDelta = 0,
        int RiskAfterPolicy = 0,
        int MustNotHitRiskAfterPolicy = 0,
        int LifecycleRiskAfterPolicy = 0);
}
