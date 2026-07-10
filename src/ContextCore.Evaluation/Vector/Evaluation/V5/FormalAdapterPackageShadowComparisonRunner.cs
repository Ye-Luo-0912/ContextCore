using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.3 Formal Adapter Package Shadow Comparison。
/// 把 V5.2 影子 adapter 候选映射到 package sections，对比 baseline package 与
/// shadow package preview。只生成报告，不写 formal package、不改 formal selected
/// set、不改 PackingPolicy / package output、不切 runtime、不绑定 IVectorIndexStore。
/// </summary>
public sealed class FormalAdapterPackageShadowComparisonRunner
{
    private const string DefaultGraphCandidateSource = "read-only relation evidence / expansion preview";

    private static readonly string[] DefaultExplainNotes =
    [
        "shadow package = post-policy filtered candidates mapped to TargetSection",
        "baseline package = dense-only top-k mapped to TargetSection",
        "section priority order: must_hit > normal > working > stable > historical > audit > diagnostics > excluded",
        "token delta = sum(estimated tokens of shadow items) - sum(baseline)",
        "comparison is read-only; never writes formal package or mutates runtime"
    ];

    private static readonly IReadOnlyDictionary<string, int> SectionPriority =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["must_hit_context"] = 0,
            [VectorQueryTargetSections.NormalContext] = 1,
            [VectorQueryTargetSections.WorkingContext] = 2,
            [VectorQueryTargetSections.StableContext] = 3,
            [VectorQueryTargetSections.HistoricalContext] = 4,
            [VectorQueryTargetSections.AuditContext] = 5,
            [VectorQueryTargetSections.DiagnosticsOnly] = 6,
            [VectorQueryTargetSections.Excluded] = 7
        };

    public FormalAdapterPackageShadowComparisonReport BuildComparison(
        ShadowFormalRetrievalAdapterReport? adapterGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        FormalAdapterPackageShadowComparisonOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(adapterGate, dataset, options, sourceReports, gateMode: false);

    public FormalAdapterPackageShadowComparisonReport BuildGate(
        ShadowFormalRetrievalAdapterReport? adapterGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        FormalAdapterPackageShadowComparisonOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(adapterGate, dataset, options, sourceReports, gateMode: true);

    public static string BuildMarkdown(string title, FormalAdapterPackageShadowComparisonReport report)
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
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- RequiredNextPhase: `{report.RequiredNextPhase}`");
        builder.AppendLine($"- VectorProviderSource: `{report.VectorProviderSource}`");
        builder.AppendLine($"- GraphCandidateSource: `{report.GraphCandidateSource}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- TotalBaselinePackageItemCount: `{report.TotalBaselinePackageItemCount}`");
        builder.AppendLine($"- TotalShadowPackageItemCount: `{report.TotalShadowPackageItemCount}`");
        builder.AppendLine($"- SelectedCount: `{report.SelectedCount}`");
        builder.AppendLine($"- DroppedCount: `{report.DroppedCount}`");
        builder.AppendLine($"- AddedCount: `{report.AddedCount}`");
        builder.AppendLine($"- SectionChangedCount: `{report.SectionChangedCount}`");
        builder.AppendLine($"- OrderChangedCount: `{report.OrderChangedCount}`");
        builder.AppendLine($"- PriorityChangedCount: `{report.PriorityChangedCount}`");
        builder.AppendLine($"- BaselineTokenTotal: `{report.BaselineTokenTotal}`");
        builder.AppendLine($"- ShadowTokenTotal: `{report.ShadowTokenTotal}`");
        builder.AppendLine($"- TokenDeltaTotal: `{report.TokenDeltaTotal}`");
        builder.AppendLine($"- TokenDeltaMax: `{report.TokenDeltaMax}`");
        builder.AppendLine($"- TokenDeltaAbsoluteTotal: `{report.TokenDeltaAbsoluteTotal}`");
        builder.AppendLine($"- TokenDeltaBudgetTotal: `{report.TokenDeltaBudgetTotal}`");
        builder.AppendLine($"- TokenDeltaBudgetPerSample: `{report.TokenDeltaBudgetPerSample}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- TargetSectionViolationCount: `{report.TargetSectionViolationCount}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- FormalSelectedSetChanged: `{report.FormalSelectedSetChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- ShadowPackageWritten: `{report.ShadowPackageWritten}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- NoRuntimeMutationInvariant: `{report.NoRuntimeMutationInvariant}`");
        AppendHistogram(builder, "Baseline Section Histogram", report.BaselineSectionHistogram);
        AppendHistogram(builder, "Shadow Section Histogram", report.ShadowSectionHistogram);
        AppendSamples(builder, report.Samples);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("V5.3 shadow only. No formal package write, formal selected set change, package output mutation, packing policy mutation, runtime switch, or vector store binding change.");
        return builder.ToString();
    }

    private static FormalAdapterPackageShadowComparisonReport Build(
        ShadowFormalRetrievalAdapterReport? adapterGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        FormalAdapterPackageShadowComparisonOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new FormalAdapterPackageShadowComparisonOptions();
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName;
        var blocked = new List<string>();

        if (adapterGate is null)
        {
            blocked.Add("AdapterGateMissing");
        }
        else
        {
            if (options.RequireAdapterGatePassed && !adapterGate.GatePassed)
            {
                blocked.Add("AdapterGateNotPassed");
            }

            if (adapterGate.FormalRetrievalAllowed)
            {
                blocked.Add("AdapterGateFormalRetrievalAllowed");
            }

            if (adapterGate.RuntimeSwitchAllowed || adapterGate.ReadyForRuntimeSwitch || adapterGate.UseForRuntime)
            {
                blocked.Add("AdapterGateRuntimeSwitchAllowed");
            }

            if (adapterGate.PackageOutputChanged)
            {
                blocked.Add("AdapterGatePackageOutputChanged");
            }

            if (adapterGate.PackingPolicyChanged)
            {
                blocked.Add("AdapterGatePackingPolicyChanged");
            }

            if (adapterGate.RuntimeMutated)
            {
                blocked.Add("AdapterGateRuntimeMutated");
            }

            if (adapterGate.VectorStoreBindingChanged)
            {
                blocked.Add("AdapterGateVectorStoreBindingChanged");
            }

            if (adapterGate.FormalPackageWritten)
            {
                blocked.Add("AdapterGateFormalPackageWritten");
            }
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

        if (options.MaxTokenDeltaTotal < 0 || options.MaxTokenDeltaPerSample < 0)
        {
            blocked.Add("InvalidTokenBudget");
        }

        var samples = new List<FormalAdapterPackageShadowComparisonSampleResult>();
        var totals = new Totals();
        var baselineHistogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shadowHistogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perSampleBudgetExceeded = false;
        if (hasDataset)
        {
            var traceLimit = Math.Max(0, options.MaxSampleTraceCount);
            var sampleIndex = 0;
            foreach (var sample in dataset!.Samples)
            {
                var result = RunSample(sample, dataset.CorpusItems, profileName, options, baselineHistogram, shadowHistogram);
                totals.Add(result);
                if (sampleIndex < traceLimit)
                {
                    samples.Add(result);
                }

                if (result.TokenDeltaAbsolute > options.MaxTokenDeltaPerSample)
                {
                    perSampleBudgetExceeded = true;
                }

                sampleIndex++;
            }

            if (totals.SampleCount == 0 || totals.ShadowPackageItemCount == 0)
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

        if (totals.TokenDeltaAbsoluteTotal > options.MaxTokenDeltaTotal)
        {
            blocked.Add("TokenDeltaTotalExceedsBudget");
        }

        if (perSampleBudgetExceeded)
        {
            blocked.Add("TokenDeltaPerSampleExceedsBudget");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var operationId = (gateMode
            ? "formal-adapter-package-shadow-comparison-gate-"
            : "formal-adapter-package-shadow-comparison-")
            + Guid.NewGuid().ToString("N");

        return new FormalAdapterPackageShadowComparisonReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            ComparisonPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = ResolveRecommendation(distinctBlocked, passed),
            AllowedMode = "ShadowOnly",
            RequiredNextPhase = "FormalAdapterPackageShadowFreeze",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = DefaultGraphCandidateSource,
            SampleCount = totals.SampleCount,
            TotalBaselinePackageItemCount = totals.BaselinePackageItemCount,
            TotalShadowPackageItemCount = totals.ShadowPackageItemCount,
            SelectedCount = totals.SelectedCount,
            DroppedCount = totals.DroppedCount,
            AddedCount = totals.AddedCount,
            SectionChangedCount = totals.SectionChangedCount,
            OrderChangedCount = totals.OrderChangedCount,
            PriorityChangedCount = totals.PriorityChangedCount,
            BaselineTokenTotal = totals.BaselineTokenTotal,
            ShadowTokenTotal = totals.ShadowTokenTotal,
            TokenDeltaTotal = totals.TokenDeltaTotal,
            TokenDeltaMax = totals.TokenDeltaMax,
            TokenDeltaAbsoluteTotal = totals.TokenDeltaAbsoluteTotal,
            RiskAfterPolicy = totals.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = totals.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = totals.LifecycleRiskAfterPolicy,
            TargetSectionViolationCount = totals.TargetSectionViolationCount,
            BaselineSectionHistogram = baselineHistogram,
            ShadowSectionHistogram = shadowHistogram,
            FormalOutputChanged = 0,
            FormalSelectedSetChanged = false,
            FormalPackageWritten = false,
            ShadowPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            NoRuntimeMutationInvariant = true,
            TokenDeltaBudgetTotal = options.MaxTokenDeltaTotal,
            TokenDeltaBudgetPerSample = options.MaxTokenDeltaPerSample,
            Samples = samples,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BlockedReasons = distinctBlocked
        };
    }

    private static FormalAdapterPackageShadowComparisonSampleResult RunSample(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        string profileName,
        FormalAdapterPackageShadowComparisonOptions options,
        IDictionary<string, int> baselineHistogram,
        IDictionary<string, int> shadowHistogram)
    {
        var workspaceId = ResolveMetadata(sample, "workspaceId");
        var collectionId = ResolveMetadata(sample, "collectionId");

        var baselineRanked = RankCandidates(sample, corpusItems, "dense-only", Math.Max(1, options.BaselineTopK));
        var shadowVectorRanked = RankCandidates(sample, corpusItems, profileName, Math.Max(1, options.ShadowVectorTopK));
        var shadowGraphRanked = CollectGraphCandidates(sample, corpusItems, Math.Max(1, options.ShadowGraphTopK));
        var shadowMerged = MergeCandidates(shadowVectorRanked, shadowGraphRanked, Math.Max(1, options.ShadowMergedTopK));

        var sectionTopK = Math.Max(1, options.PackageSectionTopK);
        var baselinePackage = BuildPackagePreview(baselineRanked, sectionTopK);
        var shadowPackage = BuildPackagePreview(shadowMerged, sectionTopK);

        UpdateHistogram(baselineHistogram, baselinePackage);
        UpdateHistogram(shadowHistogram, shadowPackage);

        var baselineMap = baselinePackage.ToDictionary(p => p.Item.ItemId, p => p, StringComparer.OrdinalIgnoreCase);
        var shadowMap = shadowPackage.ToDictionary(p => p.Item.ItemId, p => p, StringComparer.OrdinalIgnoreCase);

        var selected = new List<string>();
        var dropped = new List<string>();
        var added = new List<string>();
        var sectionChanged = new List<string>();
        var orderChanged = new List<string>();
        var priorityChanged = new List<string>();
        foreach (var pair in baselineMap)
        {
            if (!shadowMap.ContainsKey(pair.Key))
            {
                dropped.Add(pair.Key);
            }
        }

        foreach (var pair in shadowMap)
        {
            if (!baselineMap.TryGetValue(pair.Key, out var baselineEntry))
            {
                added.Add(pair.Key);
                continue;
            }

            selected.Add(pair.Key);
            var shadowEntry = pair.Value;
            if (!string.Equals(shadowEntry.Item.TargetSection, baselineEntry.Item.TargetSection, StringComparison.OrdinalIgnoreCase))
            {
                sectionChanged.Add(pair.Key);
                if (SectionPriorityRank(shadowEntry.Item.TargetSection) != SectionPriorityRank(baselineEntry.Item.TargetSection))
                {
                    priorityChanged.Add(pair.Key);
                }
            }
            else if (shadowEntry.SectionRank != baselineEntry.SectionRank)
            {
                orderChanged.Add(pair.Key);
            }
        }

        var baselineTokens = baselinePackage.Sum(p => EstimateTokens(p.Item));
        var shadowTokens = shadowPackage.Sum(p => EstimateTokens(p.Item));
        var tokenDelta = shadowTokens - baselineTokens;

        var sampleRisk = 0;
        var sampleMustNot = 0;
        var sampleLifecycle = 0;
        var sampleTargetSection = 0;
        foreach (var entry in shadowPackage)
        {
            var item = entry.Item;
            if (sample.MustNotHitItemIds.Contains(item.ItemId, StringComparer.OrdinalIgnoreCase))
            {
                sampleMustNot++;
            }

            if (!string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
            {
                sampleTargetSection++;
            }

            if (IsLifecycleRisk(item))
            {
                sampleLifecycle++;
            }

            if (IsRisk(sample, item))
            {
                sampleRisk++;
            }
        }

        var traceLimit = 5;
        return new FormalAdapterPackageShadowComparisonSampleResult
        {
            SampleId = sample.SampleId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ExpectedTargetSection = sample.ExpectedTargetSection,
            BaselinePackageItemCount = baselinePackage.Count,
            ShadowPackageItemCount = shadowPackage.Count,
            SelectedCount = selected.Count,
            DroppedCount = dropped.Count,
            AddedCount = added.Count,
            SectionChangedCount = sectionChanged.Count,
            OrderChangedCount = orderChanged.Count,
            PriorityChangedCount = priorityChanged.Count,
            BaselineTokenCount = baselineTokens,
            ShadowTokenCount = shadowTokens,
            TokenDelta = tokenDelta,
            TokenDeltaAbsolute = Math.Abs(tokenDelta),
            RiskAfterPolicy = sampleRisk,
            MustNotHitRiskAfterPolicy = sampleMustNot,
            LifecycleRiskAfterPolicy = sampleLifecycle,
            TargetSectionViolationCount = sampleTargetSection,
            BaselinePackageItemIds = baselinePackage.Take(traceLimit).Select(p => p.Item.ItemId).ToArray(),
            ShadowPackageItemIds = shadowPackage.Take(traceLimit).Select(p => p.Item.ItemId).ToArray(),
            AddedItemIds = added.Take(traceLimit).ToArray(),
            DroppedItemIds = dropped.Take(traceLimit).ToArray(),
            SectionChangedItemIds = sectionChanged.Take(traceLimit).ToArray(),
            OrderChangedItemIds = orderChanged.Take(traceLimit).ToArray(),
            PriorityChangedItemIds = priorityChanged.Take(traceLimit).ToArray(),
            ExplainNotes = DefaultExplainNotes
        };
    }

    private static IReadOnlyList<PackageEntry> BuildPackagePreview(
        IReadOnlyList<RetrievalDatasetV2CorpusItem> items,
        int sectionTopK)
    {
        if (items.Count == 0)
        {
            return Array.Empty<PackageEntry>();
        }

        var grouped = items
            .Select((item, index) => new { Item = item, Index = index })
            .GroupBy(entry => entry.Item.TargetSection, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => SectionPriorityRank(group.Key));

        var output = new List<PackageEntry>();
        foreach (var group in grouped)
        {
            var ranked = group
                .OrderBy(entry => entry.Index)
                .Take(sectionTopK)
                .ToArray();
            for (var rank = 0; rank < ranked.Length; rank++)
            {
                output.Add(new PackageEntry(ranked[rank].Item, group.Key, rank));
            }
        }

        return output;
    }

    private static void UpdateHistogram(IDictionary<string, int> histogram, IReadOnlyList<PackageEntry> entries)
    {
        foreach (var entry in entries)
        {
            histogram.TryGetValue(entry.SectionName, out var current);
            histogram[entry.SectionName] = current + 1;
        }
    }

    private static int SectionPriorityRank(string sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return SectionPriority.Count;
        }

        return SectionPriority.TryGetValue(sectionName, out var rank) ? rank : SectionPriority.Count;
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
            return FormalAdapterPackageShadowComparisonRecommendations.ReadyForFormalAdapterPackageShadowFreeze;
        }

        if (blocked.Contains("AdapterGateMissing", StringComparer.OrdinalIgnoreCase))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByMissingAdapterGate;
        }

        if (blocked.Contains("AdapterGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByAdapterGateNotPassed;
        }

        if (blocked.Contains("MissingDataset", StringComparer.OrdinalIgnoreCase))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByMissingDataset;
        }

        if (blocked.Contains("EmptyShadowOutput", StringComparer.OrdinalIgnoreCase))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByEmptyShadowOutput;
        }

        if (blocked.Contains("MustNotHitRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByMustNotHitRisk;
        }

        if (blocked.Contains("LifecycleRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByLifecycleRisk;
        }

        if (blocked.Contains("RiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByRiskAfterPolicy;
        }

        if (blocked.Any(static reason => reason.Contains("TokenDelta", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByTokenBudgetExceeded;
        }

        if (blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static reason => reason.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByVectorStoreBindingChange;
        }

        if (blocked.Any(static reason => reason.Contains("FormalPackageWritten", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByFormalPackageWritten;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalAdapterPackageShadowComparisonRecommendations.BlockedByRuntimeMutation;
        }

        return FormalAdapterPackageShadowComparisonRecommendations.KeepPreviewOnly;
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

    private static void AppendHistogram(StringBuilder builder, string title, IReadOnlyDictionary<string, int> histogram)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (histogram.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var pair in histogram.OrderBy(p => SectionPriorityRank(p.Key)).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {pair.Key}: `{pair.Value}`");
        }
    }

    private static void AppendSamples(StringBuilder builder, IReadOnlyList<FormalAdapterPackageShadowComparisonSampleResult> samples)
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
            builder.AppendLine($"  - counts (baseline/shadow/selected/dropped/added): `{sample.BaselinePackageItemCount}/{sample.ShadowPackageItemCount}/{sample.SelectedCount}/{sample.DroppedCount}/{sample.AddedCount}`");
            builder.AppendLine($"  - section/order/priority changed: `{sample.SectionChangedCount}/{sample.OrderChangedCount}/{sample.PriorityChangedCount}`");
            builder.AppendLine($"  - tokens (baseline/shadow/delta): `{sample.BaselineTokenCount}/{sample.ShadowTokenCount}/{sample.TokenDelta}`");
            builder.AppendLine($"  - risk/mustNot/lifecycle/targetSection: `{sample.RiskAfterPolicy}/{sample.MustNotHitRiskAfterPolicy}/{sample.LifecycleRiskAfterPolicy}/{sample.TargetSectionViolationCount}`");
            if (sample.ShadowPackageItemIds.Count > 0)
            {
                builder.AppendLine($"  - shadow ids: `{string.Join(", ", sample.ShadowPackageItemIds)}`");
            }

            if (sample.BaselinePackageItemIds.Count > 0)
            {
                builder.AppendLine($"  - baseline ids: `{string.Join(", ", sample.BaselinePackageItemIds)}`");
            }

            if (sample.AddedItemIds.Count > 0)
            {
                builder.AppendLine($"  - added: `{string.Join(", ", sample.AddedItemIds)}`");
            }

            if (sample.DroppedItemIds.Count > 0)
            {
                builder.AppendLine($"  - dropped: `{string.Join(", ", sample.DroppedItemIds)}`");
            }
        }
    }

    private readonly record struct ScoredItem(RetrievalDatasetV2CorpusItem Item, double Score);

    private readonly record struct PackageEntry(RetrievalDatasetV2CorpusItem Item, string SectionName, int SectionRank);

    private sealed class Totals
    {
        public int SampleCount { get; private set; }
        public int BaselinePackageItemCount { get; private set; }
        public int ShadowPackageItemCount { get; private set; }
        public int SelectedCount { get; private set; }
        public int DroppedCount { get; private set; }
        public int AddedCount { get; private set; }
        public int SectionChangedCount { get; private set; }
        public int OrderChangedCount { get; private set; }
        public int PriorityChangedCount { get; private set; }
        public int BaselineTokenTotal { get; private set; }
        public int ShadowTokenTotal { get; private set; }
        public int TokenDeltaTotal { get; private set; }
        public int TokenDeltaMax { get; private set; }
        public int TokenDeltaAbsoluteTotal { get; private set; }
        public int RiskAfterPolicy { get; private set; }
        public int MustNotHitRiskAfterPolicy { get; private set; }
        public int LifecycleRiskAfterPolicy { get; private set; }
        public int TargetSectionViolationCount { get; private set; }

        public void Add(FormalAdapterPackageShadowComparisonSampleResult sample)
        {
            SampleCount++;
            BaselinePackageItemCount += sample.BaselinePackageItemCount;
            ShadowPackageItemCount += sample.ShadowPackageItemCount;
            SelectedCount += sample.SelectedCount;
            DroppedCount += sample.DroppedCount;
            AddedCount += sample.AddedCount;
            SectionChangedCount += sample.SectionChangedCount;
            OrderChangedCount += sample.OrderChangedCount;
            PriorityChangedCount += sample.PriorityChangedCount;
            BaselineTokenTotal += sample.BaselineTokenCount;
            ShadowTokenTotal += sample.ShadowTokenCount;
            TokenDeltaTotal += sample.TokenDelta;
            if (sample.TokenDeltaAbsolute > TokenDeltaMax)
            {
                TokenDeltaMax = sample.TokenDeltaAbsolute;
            }

            TokenDeltaAbsoluteTotal += sample.TokenDeltaAbsolute;
            RiskAfterPolicy += sample.RiskAfterPolicy;
            MustNotHitRiskAfterPolicy += sample.MustNotHitRiskAfterPolicy;
            LifecycleRiskAfterPolicy += sample.LifecycleRiskAfterPolicy;
            TargetSectionViolationCount += sample.TargetSectionViolationCount;
        }
    }
}

/// <summary>V5.3 Formal Adapter Package Shadow Comparison 选项。</summary>
public sealed class FormalAdapterPackageShadowComparisonOptions
{
    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int BaselineTopK { get; init; } = 5;

    public int ShadowVectorTopK { get; init; } = 5;

    public int ShadowGraphTopK { get; init; } = 5;

    public int ShadowMergedTopK { get; init; } = 8;

    public int PackageSectionTopK { get; init; } = 5;

    public int MaxSampleTraceCount { get; init; } = 5;

    public int MaxTokenDeltaTotal { get; init; } = 4_000;

    public int MaxTokenDeltaPerSample { get; init; } = 200;

    public bool RequireAdapterGatePassed { get; init; } = true;

    public bool UseForRuntime { get; init; } = false;

    public bool FormalRetrievalAllowed { get; init; } = false;
}
