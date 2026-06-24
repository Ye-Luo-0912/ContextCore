using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.2 Shadow Formal Retrieval Adapter；只产出 shadow 候选与 trace。
/// 不绑定正式 IVectorIndexStore、不切 runtime、不写 formal package、
/// 不改 PackingPolicy / package output、不更改 formal selected set。
/// 输入：query、workspaceId、collectionId、package context、baseline candidates。
/// 输出：shadow vector candidates、shadow graph candidates、merged shadow candidates、
/// filtered candidates、trace/explain。
/// </summary>
public sealed class ShadowFormalRetrievalAdapter
{
    private const string DefaultGraphCandidateSource = "read-only relation evidence / expansion preview";

    private static readonly string[] AdapterInputs =
    [
        "query",
        "workspaceId",
        "collectionId",
        "package context",
        "baseline candidates"
    ];

    private static readonly string[] AdapterOutputs =
    [
        "shadow vector candidates",
        "shadow graph candidates",
        "merged shadow candidates",
        "filtered candidates",
        "trace/explain"
    ];

    private static readonly string[] GateOrder =
    [
        "provider scope isolation",
        "candidate eligibility",
        "lifecycle projection",
        "risk projection",
        "must-not risk gate",
        "post-scoring risk gate",
        "formal output/package invariant gate"
    ];

    private static readonly string[] DefaultExplainNotes =
    [
        "vector top-k via post-scoring-risk-gated-v1",
        "graph candidates via read-only relation evidence",
        "merge preserves vector-first stable order",
        "filter chain: eligibility -> lifecycle -> must-not -> risk",
        "fallback to baseline package path on adapter failure"
    ];

    public ShadowFormalRetrievalAdapterReport BuildAdapter(
        ShadowFormalRetrievalAdapterPlanReport? planGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        ShadowFormalRetrievalAdapterOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(planGate, dataset, options, sourceReports, gateMode: false);

    public ShadowFormalRetrievalAdapterReport BuildGate(
        ShadowFormalRetrievalAdapterPlanReport? planGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        ShadowFormalRetrievalAdapterOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(planGate, dataset, options, sourceReports, gateMode: true);

    public static string BuildMarkdown(string title, ShadowFormalRetrievalAdapterReport report)
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
        builder.AppendLine($"- AdapterPassed: `{report.AdapterPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- RequiredNextPhase: `{report.RequiredNextPhase}`");
        builder.AppendLine($"- VectorProviderSource: `{report.VectorProviderSource}`");
        builder.AppendLine($"- GraphCandidateSource: `{report.GraphCandidateSource}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- TotalBaselineCandidateCount: `{report.TotalBaselineCandidateCount}`");
        builder.AppendLine($"- TotalShadowVectorCandidateCount: `{report.TotalShadowVectorCandidateCount}`");
        builder.AppendLine($"- TotalShadowGraphCandidateCount: `{report.TotalShadowGraphCandidateCount}`");
        builder.AppendLine($"- TotalMergedShadowCandidateCount: `{report.TotalMergedShadowCandidateCount}`");
        builder.AppendLine($"- TotalFilteredCandidateCount: `{report.TotalFilteredCandidateCount}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- TargetSectionViolationCount: `{report.TargetSectionViolationCount}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- FormalSelectedSetChanged: `{report.FormalSelectedSetChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- NoRuntimeMutationInvariant: `{report.NoRuntimeMutationInvariant}`");
        AppendList(builder, "Adapter Inputs", report.AdapterInputs);
        AppendList(builder, "Adapter Outputs", report.AdapterOutputs);
        AppendList(builder, "Gate Order", report.GateOrder);
        AppendSamples(builder, report.Samples);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("V5.2 shadow only. No formal IVectorIndexStore binding, runtime switch, formal package write, PackingPolicy mutation, or package output mutation.");
        return builder.ToString();
    }

    private static ShadowFormalRetrievalAdapterReport Build(
        ShadowFormalRetrievalAdapterPlanReport? planGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        ShadowFormalRetrievalAdapterOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new ShadowFormalRetrievalAdapterOptions();
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName;
        var blocked = new List<string>();

        var planRequired = options.RequirePlanGatePassed;
        if (planGate is null)
        {
            blocked.Add("MissingPlanGate");
        }
        else if (planRequired && !planGate.PlanPassed)
        {
            blocked.Add("PlanGateNotPassed");
        }

        var hasDataset = dataset is not null && dataset.Samples.Count > 0 && dataset.CorpusItems.Count > 0;
        if (!hasDataset)
        {
            blocked.Add("MissingDataset");
        }

        if (!string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("VectorProviderSourceNotPostScoringRiskGatedV1");
        }

        if (options.UseForRuntime || options.FormalRetrievalAllowed)
        {
            blocked.Add("RuntimeMutationAttempt");
        }

        if (planGate is not null)
        {
            if (planGate.FormalRetrievalAllowed)
            {
                blocked.Add("PlanGateFormalRetrievalAllowed");
            }

            if (planGate.RuntimeSwitchAllowed || planGate.ReadyForRuntimeSwitch || planGate.UseForRuntime)
            {
                blocked.Add("PlanGateRuntimeSwitchAllowed");
            }

            if (planGate.PackageOutputChanged)
            {
                blocked.Add("PlanGatePackageOutputChanged");
            }

            if (planGate.PackingPolicyChanged)
            {
                blocked.Add("PlanGatePackingPolicyChanged");
            }

            if (planGate.VectorStoreBindingChanged)
            {
                blocked.Add("PlanGateVectorStoreBindingChanged");
            }

            if (planGate.FormalPackageWritten)
            {
                blocked.Add("PlanGateFormalPackageWritten");
            }
        }

        var samples = new List<ShadowFormalRetrievalAdapterSampleResult>();
        var totals = new Totals();
        if (hasDataset)
        {
            var traceLimit = Math.Max(0, options.MaxSampleTraceCount);
            var sampleIndex = 0;
            foreach (var sample in dataset!.Samples)
            {
                var result = RunSample(sample, dataset.CorpusItems, profileName, options);
                totals.Add(result);
                if (sampleIndex < traceLimit)
                {
                    samples.Add(result);
                }

                sampleIndex++;
            }

            if (totals.SampleCount == 0 || totals.MergedShadowCandidateCount == 0)
            {
                blocked.Add("EmptyShadowOutput");
            }
        }

        if (totals.MustNotHitRiskAfterPolicy > 0)
        {
            blocked.Add("MustNotHitRiskAfterPolicyNonZero");
        }

        if (totals.LifecycleRiskAfterPolicy > 0)
        {
            blocked.Add("LifecycleRiskAfterPolicyNonZero");
        }

        if (totals.TargetSectionViolationCount > 0)
        {
            blocked.Add("TargetSectionViolationDetected");
        }

        if (totals.RiskAfterPolicy > 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var operationId = (gateMode ? "shadow-formal-retrieval-adapter-gate-" : "shadow-formal-retrieval-adapter-")
            + Guid.NewGuid().ToString("N");

        return new ShadowFormalRetrievalAdapterReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            AdapterPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = ResolveRecommendation(distinctBlocked, passed),
            AllowedMode = "ShadowOnly",
            RequiredNextPhase = "ShadowFormalRetrievalAdapterFreeze",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = DefaultGraphCandidateSource,
            AdapterInputs = AdapterInputs,
            AdapterOutputs = AdapterOutputs,
            GateOrder = GateOrder,
            SampleCount = totals.SampleCount,
            TotalBaselineCandidateCount = totals.BaselineCandidateCount,
            TotalShadowVectorCandidateCount = totals.ShadowVectorCandidateCount,
            TotalShadowGraphCandidateCount = totals.ShadowGraphCandidateCount,
            TotalMergedShadowCandidateCount = totals.MergedShadowCandidateCount,
            TotalFilteredCandidateCount = totals.FilteredCandidateCount,
            RiskAfterPolicy = totals.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = totals.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = totals.LifecycleRiskAfterPolicy,
            TargetSectionViolationCount = totals.TargetSectionViolationCount,
            FormalOutputChanged = 0,
            FormalSelectedSetChanged = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalPackageWritten = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            NoRuntimeMutationInvariant = true,
            Samples = samples,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BlockedReasons = distinctBlocked
        };
    }

    private static ShadowFormalRetrievalAdapterSampleResult RunSample(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        string profileName,
        ShadowFormalRetrievalAdapterOptions options)
    {
        var workspaceId = ResolveMetadata(sample, "workspaceId");
        var collectionId = ResolveMetadata(sample, "collectionId");
        var baselineCandidates = RankCandidates(sample, corpusItems, "dense-only", Math.Max(1, options.VectorTopK));
        var shadowVectorCandidates = RankCandidates(sample, corpusItems, profileName, Math.Max(1, options.VectorTopK));
        var shadowGraphCandidates = CollectGraphCandidates(sample, corpusItems, Math.Max(1, options.GraphTopK));
        var merged = MergeCandidates(shadowVectorCandidates, shadowGraphCandidates, Math.Max(1, options.MergedTopK));

        var dropReasons = new List<string>();
        var filtered = new List<RetrievalDatasetV2CorpusItem>();
        var sampleRisk = 0;
        var sampleMustNot = 0;
        var sampleLifecycle = 0;
        var sampleTargetSection = 0;
        foreach (var item in merged)
        {
            if (sample.MustNotHitItemIds.Contains(item.ItemId, StringComparer.OrdinalIgnoreCase))
            {
                sampleMustNot++;
                dropReasons.Add($"must-not:{item.ItemId}");
                continue;
            }

            if (!string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
            {
                sampleTargetSection++;
                dropReasons.Add($"target-section:{item.ItemId}");
                continue;
            }

            if (IsBlockedByEligibility(sample, item))
            {
                dropReasons.Add($"eligibility:{item.ItemId}");
                continue;
            }

            if (IsLifecycleRisk(item))
            {
                sampleLifecycle++;
                dropReasons.Add($"lifecycle:{item.ItemId}");
                continue;
            }

            if (IsRisk(sample, item))
            {
                sampleRisk++;
                dropReasons.Add($"risk:{item.ItemId}");
                continue;
            }

            filtered.Add(item);
        }

        var traceLimit = 5;
        return new ShadowFormalRetrievalAdapterSampleResult
        {
            SampleId = sample.SampleId,
            QueryText = sample.QueryText,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ExpectedTargetSection = sample.ExpectedTargetSection,
            BaselineCandidateCount = baselineCandidates.Count,
            ShadowVectorCandidateCount = shadowVectorCandidates.Count,
            ShadowGraphCandidateCount = shadowGraphCandidates.Count,
            MergedShadowCandidateCount = merged.Count,
            FilteredCandidateCount = filtered.Count,
            RiskAfterPolicy = sampleRisk,
            MustNotHitRiskAfterPolicy = sampleMustNot,
            LifecycleRiskAfterPolicy = sampleLifecycle,
            TargetSectionViolationCount = sampleTargetSection,
            BaselineCandidateIds = baselineCandidates.Take(traceLimit).Select(static c => c.ItemId).ToArray(),
            ShadowVectorCandidateIds = shadowVectorCandidates.Take(traceLimit).Select(static c => c.ItemId).ToArray(),
            ShadowGraphCandidateIds = shadowGraphCandidates.Take(traceLimit).Select(static c => c.ItemId).ToArray(),
            MergedShadowCandidateIds = merged.Take(traceLimit).Select(static c => c.ItemId).ToArray(),
            FilteredCandidateIds = filtered.Take(traceLimit).Select(static c => c.ItemId).ToArray(),
            DropReasons = dropReasons.Take(traceLimit).ToArray(),
            ExplainNotes = DefaultExplainNotes
        };
    }

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> RankCandidates(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        string profileName,
        int topK)
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
            .Take(topK)
            .Select(static item => item.Item)
            .ToArray();
    }

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> CollectGraphCandidates(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        int topK)
    {
        if (sample.RequiredRelations.Count == 0
            && sample.EvidenceRefs.Count == 0
            && sample.SourceRefs.Count == 0
            && sample.MustHitItemIds.Count == 0)
        {
            return Array.Empty<RetrievalDatasetV2CorpusItem>();
        }

        var requiredRelations = new HashSet<string>(sample.RequiredRelations, StringComparer.OrdinalIgnoreCase);
        var evidence = new HashSet<string>(sample.EvidenceRefs, StringComparer.OrdinalIgnoreCase);
        var source = new HashSet<string>(sample.SourceRefs, StringComparer.OrdinalIgnoreCase);
        var mustHit = new HashSet<string>(sample.MustHitItemIds, StringComparer.OrdinalIgnoreCase);

        var scored = corpusItems
            .Select(item =>
            {
                var overlap = 0;
                foreach (var relation in item.Relations)
                {
                    if (requiredRelations.Contains(relation.RelationId))
                    {
                        overlap += 2;
                    }
                }

                if (item.EvidenceRefs.Any(reference => evidence.Contains(reference)))
                {
                    overlap += 1;
                }

                if (item.SourceRefs.Any(reference => source.Contains(reference)))
                {
                    overlap += 1;
                }

                if (mustHit.Contains(item.ItemId))
                {
                    overlap += 3;
                }

                return new ScoredItem(item, overlap);
            })
            .Where(static entry => entry.Score > 0)
            .Where(entry => !IsRisk(sample, entry.Item))
            .OrderByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return scored
            .Take(topK)
            .Select(static entry => entry.Item)
            .ToArray();
    }

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> MergeCandidates(
        IReadOnlyList<RetrievalDatasetV2CorpusItem> vectorCandidates,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> graphCandidates,
        int topK)
    {
        if (vectorCandidates.Count == 0 && graphCandidates.Count == 0)
        {
            return Array.Empty<RetrievalDatasetV2CorpusItem>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<RetrievalDatasetV2CorpusItem>();
        foreach (var item in vectorCandidates)
        {
            if (seen.Add(item.ItemId))
            {
                merged.Add(item);
            }

            if (merged.Count >= topK)
            {
                return merged;
            }
        }

        foreach (var item in graphCandidates)
        {
            if (seen.Add(item.ItemId))
            {
                merged.Add(item);
            }

            if (merged.Count >= topK)
            {
                return merged;
            }
        }

        return merged;
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

    private static string ResolveMetadata(RetrievalDatasetV2Sample sample, string key)
    {
        if (sample.Metadata is null || !sample.Metadata.TryGetValue(key, out var value))
        {
            return string.Empty;
        }

        return value ?? string.Empty;
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool passed)
    {
        if (passed)
        {
            return ShadowFormalRetrievalAdapterRecommendations.ReadyForShadowAdapterFreeze;
        }

        if (blocked.Contains("MissingPlanGate", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByMissingPlanGate;
        }

        if (blocked.Contains("PlanGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByPlanGateNotPassed;
        }

        if (blocked.Contains("MissingDataset", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByMissingDataset;
        }

        if (blocked.Contains("EmptyShadowOutput", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByEmptyShadowOutput;
        }

        if (blocked.Contains("MustNotHitRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByMustNotHitRisk;
        }

        if (blocked.Contains("LifecycleRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByLifecycleRisk;
        }

        if (blocked.Contains("TargetSectionViolationDetected", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByTargetSectionViolation;
        }

        if (blocked.Contains("RiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByRiskAfterPolicy;
        }

        if (blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static reason => reason.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByVectorStoreBindingChange;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("FormalPackageWritten", StringComparison.OrdinalIgnoreCase)))
        {
            return ShadowFormalRetrievalAdapterRecommendations.BlockedByRuntimeMutation;
        }

        return ShadowFormalRetrievalAdapterRecommendations.KeepPreviewOnly;
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

    private static void AppendMap(StringBuilder builder, string title, IReadOnlyDictionary<string, string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {pair.Key}: `{pair.Value}`");
        }
    }

    private static void AppendSamples(StringBuilder builder, IReadOnlyList<ShadowFormalRetrievalAdapterSampleResult> samples)
    {
        builder.AppendLine();
        builder.AppendLine("## Per-Sample Trace");
        if (samples.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var sample in samples)
        {
            builder.AppendLine($"- sampleId: `{sample.SampleId}`");
            builder.AppendLine($"  - target: `{sample.ExpectedTargetSection}`");
            builder.AppendLine($"  - counts (baseline/vector/graph/merged/filtered): `{sample.BaselineCandidateCount}/{sample.ShadowVectorCandidateCount}/{sample.ShadowGraphCandidateCount}/{sample.MergedShadowCandidateCount}/{sample.FilteredCandidateCount}`");
            builder.AppendLine($"  - risk/mustNot/lifecycle/targetSection: `{sample.RiskAfterPolicy}/{sample.MustNotHitRiskAfterPolicy}/{sample.LifecycleRiskAfterPolicy}/{sample.TargetSectionViolationCount}`");
            if (sample.MergedShadowCandidateIds.Count > 0)
            {
                builder.AppendLine($"  - merged ids: `{string.Join(", ", sample.MergedShadowCandidateIds)}`");
            }

            if (sample.FilteredCandidateIds.Count > 0)
            {
                builder.AppendLine($"  - filtered ids: `{string.Join(", ", sample.FilteredCandidateIds)}`");
            }

            if (sample.DropReasons.Count > 0)
            {
                builder.AppendLine($"  - drops: `{string.Join(", ", sample.DropReasons)}`");
            }
        }
    }

    private readonly record struct ScoredItem(RetrievalDatasetV2CorpusItem Item, double Score);

    private sealed class Totals
    {
        public int SampleCount { get; private set; }
        public int BaselineCandidateCount { get; private set; }
        public int ShadowVectorCandidateCount { get; private set; }
        public int ShadowGraphCandidateCount { get; private set; }
        public int MergedShadowCandidateCount { get; private set; }
        public int FilteredCandidateCount { get; private set; }
        public int RiskAfterPolicy { get; private set; }
        public int MustNotHitRiskAfterPolicy { get; private set; }
        public int LifecycleRiskAfterPolicy { get; private set; }
        public int TargetSectionViolationCount { get; private set; }

        public void Add(ShadowFormalRetrievalAdapterSampleResult sample)
        {
            SampleCount++;
            BaselineCandidateCount += sample.BaselineCandidateCount;
            ShadowVectorCandidateCount += sample.ShadowVectorCandidateCount;
            ShadowGraphCandidateCount += sample.ShadowGraphCandidateCount;
            MergedShadowCandidateCount += sample.MergedShadowCandidateCount;
            FilteredCandidateCount += sample.FilteredCandidateCount;
            RiskAfterPolicy += sample.RiskAfterPolicy;
            MustNotHitRiskAfterPolicy += sample.MustNotHitRiskAfterPolicy;
            LifecycleRiskAfterPolicy += sample.LifecycleRiskAfterPolicy;
            TargetSectionViolationCount += sample.TargetSectionViolationCount;
        }
    }
}

/// <summary>V5.2 Shadow Formal Retrieval Adapter 选项。</summary>
public sealed class ShadowFormalRetrievalAdapterOptions
{
    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int VectorTopK { get; init; } = 5;

    public int GraphTopK { get; init; } = 5;

    public int MergedTopK { get; init; } = 8;

    public int MaxSampleTraceCount { get; init; } = 5;

    public bool RequirePlanGatePassed { get; init; } = true;

    public bool UseForRuntime { get; init; } = false;

    public bool FormalRetrievalAllowed { get; init; } = false;
}
