using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Guarded formal retrieval preview；只做离线 would-change 对比，不写正式 package，也不接正式检索。
/// </summary>
public sealed class GuardedFormalRetrievalPreviewRunner
{
    private const int TopK = 5;

    public GuardedFormalRetrievalPreviewReport BuildPreview(
        RetrievalDatasetV2GeneratedDataset? dataset,
        VectorV4ReadinessRecheckReport? v4Recheck,
        RetrievalDatasetV2StressFreezeReport? stressFreeze,
        HybridUnionScoringRepairReport? hybridScoringRepairGate,
        HybridScoringRiskRegressionTriageReport? hybridScoringRiskTriage,
        GuardedFormalRetrievalPreviewOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport(
            dataset,
            v4Recheck,
            stressFreeze,
            hybridScoringRepairGate,
            hybridScoringRiskTriage,
            options,
            gateMode: false,
            sourceReports);

    public GuardedFormalRetrievalPreviewReport BuildGate(
        RetrievalDatasetV2GeneratedDataset? dataset,
        VectorV4ReadinessRecheckReport? v4Recheck,
        RetrievalDatasetV2StressFreezeReport? stressFreeze,
        HybridUnionScoringRepairReport? hybridScoringRepairGate,
        HybridScoringRiskRegressionTriageReport? hybridScoringRiskTriage,
        GuardedFormalRetrievalPreviewOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => BuildReport(
            dataset,
            v4Recheck,
            stressFreeze,
            hybridScoringRepairGate,
            hybridScoringRiskTriage,
            options,
            gateMode: true,
            sourceReports);

    public static string BuildMarkdown(string title, GuardedFormalRetrievalPreviewReport report)
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
        builder.AppendLine($"- PreviewPassed: `{report.PreviewPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ProfileName: `{report.ProfileName}`");
        builder.AppendLine($"- V4RecheckPassed: `{report.V4RecheckPassed}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- QueryCount: `{report.QueryCount}`");
        builder.AppendLine($"- BaselineCandidateCount: `{report.BaselineCandidateCount}`");
        builder.AppendLine($"- PreviewVectorCandidateCount: `{report.PreviewVectorCandidateCount}`");
        builder.AppendLine($"- WouldAddCount: `{report.WouldAddCount}`");
        builder.AppendLine($"- WouldRemoveCount: `{report.WouldRemoveCount}`");
        builder.AppendLine($"- WouldRerankCount: `{report.WouldRerankCount}`");
        builder.AppendLine($"- WouldChangeTargetSectionCount: `{report.WouldChangeTargetSectionCount}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- 本报告只输出 guarded formal retrieval preview 的 would-change 统计。");
        builder.AppendLine("- 不写正式 package，不改变 PackingPolicy 输入，不改变最终 package output。");
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

    private static GuardedFormalRetrievalPreviewReport BuildReport(
        RetrievalDatasetV2GeneratedDataset? dataset,
        VectorV4ReadinessRecheckReport? v4Recheck,
        RetrievalDatasetV2StressFreezeReport? stressFreeze,
        HybridUnionScoringRepairReport? hybridScoringRepairGate,
        HybridScoringRiskRegressionTriageReport? hybridScoringRiskTriage,
        GuardedFormalRetrievalPreviewOptions? options,
        bool gateMode,
        IReadOnlyDictionary<string, string>? sourceReports)
    {
        options ??= new GuardedFormalRetrievalPreviewOptions();
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName;
        var preview = BuildPreviewStats(dataset, profileName);
        var selectedProfile = hybridScoringRepairGate?.Profiles
            .FirstOrDefault(profile => string.Equals(profile.ProfileName, profileName, StringComparison.OrdinalIgnoreCase));
        var riskAfterPolicy = Max(
            preview.RiskAfterPolicy,
            stressFreeze?.RiskAfterPolicy ?? 0,
            selectedProfile?.RiskAfterPolicy ?? 0,
            hybridScoringRiskTriage?.RiskCandidateCount ?? 0);
        var mustNotRisk = Max(
            preview.MustNotHitRiskAfterPolicy,
            stressFreeze?.MustNotHitRiskAfterPolicy ?? 0,
            selectedProfile?.MustNotHitRiskAfterPolicy ?? 0,
            hybridScoringRiskTriage?.MustNotCandidatePromotedCount ?? 0);
        var lifecycleRisk = Max(
            preview.LifecycleRiskAfterPolicy,
            stressFreeze?.LifecycleRiskAfterPolicy ?? 0,
            selectedProfile?.LifecycleRiskAfterPolicy ?? 0,
            hybridScoringRiskTriage?.LifecycleRiskPromotedCount ?? 0);
        var formalOutputChanged = Max(
            stressFreeze?.FormalOutputChanged ?? 0,
            selectedProfile?.FormalOutputChanged ?? 0,
            v4Recheck?.FormalOutputChanged ?? 0);
        var blocked = new List<string>();
        if (options.RequireV4RecheckPassed && (v4Recheck is null || !v4Recheck.RecheckPassed || !v4Recheck.ReadyForGuardedFormalPreview))
        {
            blocked.Add("V4RecheckNotPassed");
        }

        if (stressFreeze is null || !stressFreeze.FreezePassed || !stressFreeze.V4RecheckAllowed)
        {
            blocked.Add("DatasetV2StressFreezeNotReady");
        }

        if (hybridScoringRepairGate is null || !hybridScoringRepairGate.GatePassed)
        {
            blocked.Add("HybridScoringRepairGateNotPassed");
        }

        if (hybridScoringRiskTriage is null
            || hybridScoringRiskTriage.RiskCandidateCount != 0
            || !string.Equals(hybridScoringRiskTriage.Recommendation, HybridScoringRiskRegressionRecommendations.ReadyForSafeScoringRepair, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("HybridScoringRiskTriageNotClean");
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

        if (options.UseForRuntime || options.FormalRetrievalAllowed)
        {
            blocked.Add("RuntimeSwitchAttempt");
        }

        var packingPolicyChanged = false;
        var packageOutputChanged = false;
        var readyForRuntimeSwitch = false;
        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0
            && !packingPolicyChanged
            && !packageOutputChanged
            && !readyForRuntimeSwitch;
        return new GuardedFormalRetrievalPreviewReport
        {
            OperationId = gateMode
                ? $"vector-guarded-formal-retrieval-preview-gate-{Guid.NewGuid():N}"
                : $"vector-guarded-formal-retrieval-preview-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PreviewPassed = passed,
            GatePassed = gateMode && passed,
            ProfileName = profileName,
            V4RecheckPassed = v4Recheck?.RecheckPassed ?? false,
            SampleCount = preview.SampleCount,
            QueryCount = preview.QueryCount,
            BaselineCandidateCount = preview.BaselineCandidateCount,
            PreviewVectorCandidateCount = preview.PreviewVectorCandidateCount,
            WouldAddCount = preview.WouldAddCount,
            WouldRemoveCount = preview.WouldRemoveCount,
            WouldRerankCount = preview.WouldRerankCount,
            WouldChangeTargetSectionCount = preview.WouldChangeTargetSectionCount,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            Recommendation = passed
                ? GuardedFormalRetrievalPreviewRecommendations.ReadyForShadowPackageComparison
                : ResolveRecommendation(distinctBlocked, formalOutputChanged, packingPolicyChanged, packageOutputChanged),
            BlockedReasons = distinctBlocked,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static PreviewStats BuildPreviewStats(RetrievalDatasetV2GeneratedDataset? dataset, string profileName)
    {
        if (dataset is null || dataset.Samples.Count == 0 || dataset.CorpusItems.Count == 0)
        {
            return new PreviewStats();
        }

        var baselineCandidateCount = 0;
        var previewCandidateCount = 0;
        var wouldAdd = 0;
        var wouldRemove = 0;
        var wouldRerank = 0;
        var wouldChangeTargetSection = 0;
        var risk = 0;
        var mustNotRisk = 0;
        var lifecycleRisk = 0;
        foreach (var sample in dataset.Samples)
        {
            var baseline = RankCandidates(sample, dataset.CorpusItems, "dense-only");
            var preview = RankCandidates(sample, dataset.CorpusItems, profileName);
            baselineCandidateCount += baseline.Count;
            previewCandidateCount += preview.Count;
            var baselineIds = baseline.Select(static item => item.ItemId).ToArray();
            var previewIds = preview.Select(static item => item.ItemId).ToArray();
            wouldAdd += previewIds.Count(id => !baselineIds.Contains(id, StringComparer.OrdinalIgnoreCase));
            wouldRemove += baselineIds.Count(id => !previewIds.Contains(id, StringComparer.OrdinalIgnoreCase));
            if (baselineIds.Length == previewIds.Length
                && !baselineIds.SequenceEqual(previewIds, StringComparer.OrdinalIgnoreCase)
                && !previewIds.Except(baselineIds, StringComparer.OrdinalIgnoreCase).Any()
                && !baselineIds.Except(previewIds, StringComparer.OrdinalIgnoreCase).Any())
            {
                wouldRerank++;
            }

            for (var i = 0; i < preview.Count; i++)
            {
                var previewItem = preview[i];
                if (i < baseline.Count
                    && !string.Equals(previewItem.TargetSection, baseline[i].TargetSection, StringComparison.OrdinalIgnoreCase))
                {
                    wouldChangeTargetSection++;
                }

                if (IsRisk(sample, previewItem))
                {
                    risk++;
                }

                if (sample.MustNotHitItemIds.Contains(previewItem.ItemId, StringComparer.OrdinalIgnoreCase))
                {
                    mustNotRisk++;
                }

                if (IsLifecycleRisk(previewItem))
                {
                    lifecycleRisk++;
                }
            }
        }

        return new PreviewStats(
            dataset.Samples.Count,
            dataset.Samples.Count,
            baselineCandidateCount,
            previewCandidateCount,
            wouldAdd,
            wouldRemove,
            wouldRerank,
            wouldChangeTargetSection,
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
                var score = ScoreForProfile(profileName, dense, lexical, anchor, negative);
                return new ScoredItem(item, score);
            })
            .Where(static scored => scored.Score > 0)
            .OrderByDescending(static scored => scored.Score)
            .ThenBy(static scored => scored.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            scored = scored
                .Where(scored => !IsRisk(sample, scored.Item))
                .ToArray();
        }

        return scored
            .Take(TopK)
            .Select(static scored => scored.Item)
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
        return itemTokens.Count == 0
            ? 0
            : negativeTokens.Count(itemTokens.Contains) / (double)negativeTokens.Count;
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

    private static int Max(params int[] values) => values.Length == 0 ? 0 : values.Max();

    private static string ResolveRecommendation(
        IReadOnlyList<string> blocked,
        int formalOutputChanged,
        bool packingPolicyChanged,
        bool packageOutputChanged)
    {
        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)))
        {
            return GuardedFormalRetrievalPreviewRecommendations.BlockedByRuntimeSwitchAttempt;
        }

        if (packageOutputChanged)
        {
            return GuardedFormalRetrievalPreviewRecommendations.BlockedByPackageOutputChange;
        }

        if (packingPolicyChanged)
        {
            return GuardedFormalRetrievalPreviewRecommendations.BlockedByPackingPolicyChange;
        }

        if (formalOutputChanged != 0 || blocked.Any(static reason => reason.Contains("FormalOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return GuardedFormalRetrievalPreviewRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return GuardedFormalRetrievalPreviewRecommendations.BlockedByRisk;
        }

        return GuardedFormalRetrievalPreviewRecommendations.KeepPreviewOnly;
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

    private readonly record struct PreviewStats(
        int SampleCount = 0,
        int QueryCount = 0,
        int BaselineCandidateCount = 0,
        int PreviewVectorCandidateCount = 0,
        int WouldAddCount = 0,
        int WouldRemoveCount = 0,
        int WouldRerankCount = 0,
        int WouldChangeTargetSectionCount = 0,
        int RiskAfterPolicy = 0,
        int MustNotHitRiskAfterPolicy = 0,
        int LifecycleRiskAfterPolicy = 0);
}
