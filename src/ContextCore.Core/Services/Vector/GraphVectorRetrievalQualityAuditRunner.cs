using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.4 Graph + Vector Retrieval Quality Audit。
/// 在 V5.1 plan / V5.2 影子 adapter / V5.3 package shadow comparison 的产物之上做
/// retrieval quality audit，按 <see cref="GraphVectorRetrievalQualityAuditFailureClusters"/>
/// 七类聚合失败簇。只生成报告，不接 formal retrieval、不写 formal package、不动
/// formal selected set、不改 PackingPolicy / package output、不切 runtime。
/// </summary>
public sealed class GraphVectorRetrievalQualityAuditRunner
{
    private const string DefaultGraphCandidateSource = "read-only relation evidence / expansion preview";

    private static readonly string[] DefaultExplainNotes =
    [
        "vector candidates via post-scoring-risk-gated-v1",
        "graph candidates via read-only relation evidence",
        "metrics: recall@k / precision@k / MRR over MustHit",
        "noise = candidate without any metadata path back to sample",
        "ranking regression = MustHit's merged rank > dense baseline rank"
    ];

    private static readonly IReadOnlyDictionary<string, string> ClusterDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [GraphVectorRetrievalQualityAuditFailureClusters.MissingCandidate]
                = "MustHit item missing from merged top-K.",
            [GraphVectorRetrievalQualityAuditFailureClusters.RankingTooLow]
                = "MustHit recalled but ranked below TopK or dense baseline rank.",
            [GraphVectorRetrievalQualityAuditFailureClusters.GraphNoise]
                = "Graph candidate admitted via weak evidence/source overlap alone, with no MustHit or RequiredRelations anchor.",
            [GraphVectorRetrievalQualityAuditFailureClusters.VectorNoise]
                = "Vector candidate that violates eligibility despite the post-scoring-risk-gated profile filter.",
            [GraphVectorRetrievalQualityAuditFailureClusters.SectionMismatch]
                = "Merged candidate whose target section differs from sample expectation.",
            [GraphVectorRetrievalQualityAuditFailureClusters.LifecycleMismatch]
                = "Merged candidate whose lifecycle violates NormalContext eligibility.",
            [GraphVectorRetrievalQualityAuditFailureClusters.MetadataEvidenceGap]
                = "Sample has no MustHit-supporting metadata (evidence/source/required relations) yet a MustHit was below TopK."
        };

    public GraphVectorRetrievalQualityAuditReport BuildAudit(
        FormalAdapterPackageShadowComparisonReport? packageShadowGate,
        ShadowFormalRetrievalAdapterReport? adapterGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        GraphVectorRetrievalQualityAuditOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(packageShadowGate, adapterGate, dataset, options, sourceReports, gateMode: false);

    public GraphVectorRetrievalQualityAuditReport BuildGate(
        FormalAdapterPackageShadowComparisonReport? packageShadowGate,
        ShadowFormalRetrievalAdapterReport? adapterGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        GraphVectorRetrievalQualityAuditOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(packageShadowGate, adapterGate, dataset, options, sourceReports, gateMode: true);

    public static string BuildMarkdown(string title, GraphVectorRetrievalQualityAuditReport report)
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
        builder.AppendLine($"- AuditPassed: `{report.AuditPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- RequiredNextPhase: `{report.RequiredNextPhase}`");
        builder.AppendLine($"- VectorProviderSource: `{report.VectorProviderSource}`");
        builder.AppendLine($"- GraphCandidateSource: `{report.GraphCandidateSource}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- MustHitTotal: `{report.MustHitTotal}`");
        builder.AppendLine($"- MustHitRecalledTotal: `{report.MustHitRecalledTotal}`");
        builder.AppendLine($"- Recall: `{report.Recall:F4}`");
        builder.AppendLine($"- Precision: `{report.Precision:F4}`");
        builder.AppendLine($"- MeanReciprocalRank: `{report.MeanReciprocalRank:F4}`");
        builder.AppendLine($"- TopK: `{report.TopK}`");
        builder.AppendLine($"- VectorContributionCount: `{report.VectorContributionCount}`");
        builder.AppendLine($"- GraphContributionCount: `{report.GraphContributionCount}`");
        builder.AppendLine($"- OverlapCount: `{report.OverlapCount}`");
        builder.AppendLine($"- VectorOnlyCount: `{report.VectorOnlyCount}`");
        builder.AppendLine($"- GraphOnlyCount: `{report.GraphOnlyCount}`");
        builder.AppendLine($"- GraphNoiseCount: `{report.GraphNoiseCount}`");
        builder.AppendLine($"- VectorNoiseCount: `{report.VectorNoiseCount}`");
        builder.AppendLine($"- RankingRegressionCount: `{report.RankingRegressionCount}`");
        builder.AppendLine($"- MustHitBelowTopKCount: `{report.MustHitBelowTopKCount}`");
        builder.AppendLine($"- GraphNoiseThreshold: `{report.GraphNoiseThreshold}`");
        builder.AppendLine($"- RankingRegressionThreshold: `{report.RankingRegressionThreshold}`");
        builder.AppendLine($"- MustHitBelowTopKThreshold: `{report.MustHitBelowTopKThreshold}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- SectionMismatchCount: `{report.SectionMismatchCount}`");
        builder.AppendLine($"- MetadataEvidenceGapCount: `{report.MetadataEvidenceGapCount}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- FormalSelectedSetChanged: `{report.FormalSelectedSetChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- NoRuntimeMutationInvariant: `{report.NoRuntimeMutationInvariant}`");
        AppendClusters(builder, report.FailureClusters);
        AppendSamples(builder, report.Samples);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("V5.4 audit only. No formal retrieval, formal package write, formal selected set change, package output mutation, packing policy mutation, runtime switch, or vector store binding change.");
        return builder.ToString();
    }

    private static GraphVectorRetrievalQualityAuditReport Build(
        FormalAdapterPackageShadowComparisonReport? packageShadowGate,
        ShadowFormalRetrievalAdapterReport? adapterGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        GraphVectorRetrievalQualityAuditOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new GraphVectorRetrievalQualityAuditOptions();
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName;
        var blocked = new List<string>();

        if (packageShadowGate is null)
        {
            blocked.Add("PackageShadowGateMissing");
        }
        else
        {
            if (options.RequirePackageShadowGatePassed && !packageShadowGate.GatePassed)
            {
                blocked.Add("PackageShadowGateNotPassed");
            }

            AddBoundaryBlocks(blocked, "PackageShadowGate",
                packageShadowGate.FormalRetrievalAllowed,
                packageShadowGate.RuntimeSwitchAllowed,
                packageShadowGate.ReadyForRuntimeSwitch,
                packageShadowGate.UseForRuntime,
                packageShadowGate.PackageOutputChanged,
                packageShadowGate.PackingPolicyChanged,
                packageShadowGate.RuntimeMutated,
                packageShadowGate.VectorStoreBindingChanged,
                packageShadowGate.FormalPackageWritten);
        }

        if (adapterGate is not null)
        {
            if (options.RequireAdapterGatePassed && !adapterGate.GatePassed)
            {
                blocked.Add("AdapterGateNotPassed");
            }

            AddBoundaryBlocks(blocked, "AdapterGate",
                adapterGate.FormalRetrievalAllowed,
                adapterGate.RuntimeSwitchAllowed,
                adapterGate.ReadyForRuntimeSwitch,
                adapterGate.UseForRuntime,
                adapterGate.PackageOutputChanged,
                adapterGate.PackingPolicyChanged,
                adapterGate.RuntimeMutated,
                adapterGate.VectorStoreBindingChanged,
                adapterGate.FormalPackageWritten);
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

        var topK = Math.Max(1, options.TopK);
        var vectorTopK = Math.Max(1, options.VectorTopK);
        var graphTopK = Math.Max(1, options.GraphTopK);
        var mergedTopK = Math.Max(1, options.MergedTopK);
        var traceLimit = Math.Max(0, options.MaxSampleTraceCount);
        var clusterMemberLimit = Math.Max(0, options.MaxFailureClusterMembers);

        var samples = new List<GraphVectorRetrievalQualityAuditSampleResult>();
        var totals = new Totals();
        var clusters = InitializeClusterAccumulators();
        if (hasDataset)
        {
            var sampleIndex = 0;
            var hasMergedOutput = false;
            foreach (var sample in dataset!.Samples)
            {
                var result = RunSample(
                    sample,
                    dataset.CorpusItems,
                    profileName,
                    topK,
                    vectorTopK,
                    graphTopK,
                    mergedTopK,
                    clusters,
                    clusterMemberLimit);
                totals.Add(result);
                if (sampleIndex < traceLimit)
                {
                    samples.Add(result);
                }

                if (result.MergedCandidateCount > 0)
                {
                    hasMergedOutput = true;
                }

                sampleIndex++;
            }

            if (totals.SampleCount == 0 || !hasMergedOutput)
            {
                blocked.Add("EmptyAuditOutput");
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

        if (totals.SectionMismatchCount > 0)
        {
            blocked.Add("SectionMismatchDetected");
        }

        if (totals.RiskAfterPolicy > 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (totals.GraphNoiseCount > options.GraphNoiseThreshold)
        {
            blocked.Add("GraphNoiseExceedsThreshold");
        }

        if (totals.RankingRegressionCount > options.RankingRegressionThreshold)
        {
            blocked.Add("RankingRegressionExceedsThreshold");
        }

        // MustHitBelowTopK is an informational quality ceiling, not a regression.
        // The V5.4 gate only blocks on must-not / lifecycle / section / risk / graph noise /
        // ranking regression vs dense baseline. mustHitBelowTopK > threshold is reported
        // and surfaces in failure clusters, but does not flip the gate by default.

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var operationId = (gateMode
            ? "graph-vector-retrieval-quality-audit-gate-"
            : "graph-vector-retrieval-quality-audit-")
            + Guid.NewGuid().ToString("N");
        var failureClusters = BuildFailureClusters(clusters);

        var sampleCount = totals.SampleCount;
        var avgRecall = sampleCount == 0 ? 0d : totals.RecallSum / sampleCount;
        var avgPrecision = sampleCount == 0 ? 0d : totals.PrecisionSum / sampleCount;
        var avgMrr = sampleCount == 0 ? 0d : totals.MrrSum / sampleCount;

        return new GraphVectorRetrievalQualityAuditReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            AuditPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = ResolveRecommendation(distinctBlocked, passed),
            AllowedMode = "AuditOnly",
            RequiredNextPhase = "RetrievalQualityFreeze",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = DefaultGraphCandidateSource,
            SampleCount = sampleCount,
            MustHitTotal = totals.MustHitTotal,
            MustHitRecalledTotal = totals.MustHitRecalledTotal,
            Recall = avgRecall,
            Precision = avgPrecision,
            MeanReciprocalRank = avgMrr,
            VectorContributionCount = totals.VectorContributionCount,
            GraphContributionCount = totals.GraphContributionCount,
            OverlapCount = totals.OverlapCount,
            VectorOnlyCount = totals.VectorOnlyCount,
            GraphOnlyCount = totals.GraphOnlyCount,
            GraphNoiseCount = totals.GraphNoiseCount,
            VectorNoiseCount = totals.VectorNoiseCount,
            RankingRegressionCount = totals.RankingRegressionCount,
            MustHitBelowTopKCount = totals.MustHitBelowTopKCount,
            RiskAfterPolicy = totals.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = totals.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = totals.LifecycleRiskAfterPolicy,
            SectionMismatchCount = totals.SectionMismatchCount,
            MetadataEvidenceGapCount = totals.MetadataEvidenceGapCount,
            TopK = topK,
            GraphNoiseThreshold = options.GraphNoiseThreshold,
            RankingRegressionThreshold = options.RankingRegressionThreshold,
            MustHitBelowTopKThreshold = options.MustHitBelowTopKThreshold,
            FormalOutputChanged = 0,
            FormalSelectedSetChanged = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            NoRuntimeMutationInvariant = true,
            FailureClusters = failureClusters,
            Samples = samples,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BlockedReasons = distinctBlocked
        };
    }

    private static GraphVectorRetrievalQualityAuditSampleResult RunSample(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        string profileName,
        int topK,
        int vectorTopK,
        int graphTopK,
        int mergedTopK,
        IDictionary<string, ClusterAccumulator> clusters,
        int clusterMemberLimit)
    {
        var workspaceId = ResolveMetadata(sample, "workspaceId");
        var collectionId = ResolveMetadata(sample, "collectionId");

        var vectorRanked = RankCandidates(sample, corpusItems, profileName, vectorTopK);
        var denseRanked = RankCandidates(sample, corpusItems, "dense-only", vectorTopK);
        var graphRanked = CollectGraphCandidates(sample, corpusItems, graphTopK);
        var merged = MergeCandidates(vectorRanked, graphRanked, mergedTopK);

        var vectorIds = vectorRanked.Select(c => c.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var graphIds = graphRanked.Select(c => c.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overlap = vectorIds.Intersect(graphIds, StringComparer.OrdinalIgnoreCase).Count();
        var vectorOnly = vectorIds.Count - overlap;
        var graphOnly = graphIds.Count - overlap;

        var topKWindow = merged.Take(topK).ToArray();
        var topKWindowIds = topKWindow.Select(c => c.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mergedRankByItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < merged.Count; i++)
        {
            mergedRankByItem[merged[i].ItemId] = i + 1;
        }

        var denseRankByItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < denseRanked.Count; i++)
        {
            denseRankByItem[denseRanked[i].ItemId] = i + 1;
        }

        // Recall / precision / MRR
        var mustHits = sample.MustHitItemIds;
        var mustHitTotal = mustHits.Count;
        var mustHitRecalled = 0;
        var firstMustHitRank = 0;
        var missingMustHits = new List<string>();
        var rankingRegressions = new List<string>();
        var mustHitBelowTopK = 0;
        var metadataEvidenceGaps = new List<string>();
        var hasMetadataAnchor = sample.RequiredRelations.Count > 0
            || sample.EvidenceRefs.Count > 0
            || sample.SourceRefs.Count > 0;
        foreach (var mustHitId in mustHits)
        {
            if (topKWindowIds.Contains(mustHitId))
            {
                mustHitRecalled++;
                if (firstMustHitRank == 0
                    && mergedRankByItem.TryGetValue(mustHitId, out var rank))
                {
                    firstMustHitRank = rank;
                }
            }
            else
            {
                missingMustHits.Add(mustHitId);
                AddCluster(clusters, GraphVectorRetrievalQualityAuditFailureClusters.MissingCandidate, sample.SampleId, mustHitId, clusterMemberLimit);
                if (!hasMetadataAnchor)
                {
                    metadataEvidenceGaps.Add(mustHitId);
                    AddCluster(clusters, GraphVectorRetrievalQualityAuditFailureClusters.MetadataEvidenceGap, sample.SampleId, mustHitId, clusterMemberLimit);
                }
            }

            // Ranking regression: merged rank vs dense rank.
            var mergedRank = mergedRankByItem.TryGetValue(mustHitId, out var mr) ? mr : int.MaxValue;
            var denseRank = denseRankByItem.TryGetValue(mustHitId, out var dr) ? dr : int.MaxValue;
            if (mergedRank > denseRank)
            {
                rankingRegressions.Add(mustHitId);
                AddCluster(clusters, GraphVectorRetrievalQualityAuditFailureClusters.RankingTooLow, sample.SampleId, mustHitId, clusterMemberLimit);
            }

            // MustHit recalled but rank > TopK (in merged not in topK window).
            if (mergedRank != int.MaxValue && mergedRank > topK)
            {
                mustHitBelowTopK++;
                AddCluster(clusters, GraphVectorRetrievalQualityAuditFailureClusters.RankingTooLow, sample.SampleId, mustHitId, clusterMemberLimit);
            }
        }

        var recall = mustHitTotal == 0 ? 0d : (double)mustHitRecalled / mustHitTotal;
        var precision = topKWindow.Length == 0 ? 0d : (double)mustHitRecalled / topKWindow.Length;
        var mrr = firstMustHitRank == 0 ? 0d : 1d / firstMustHitRank;

        // Graph noise: graph candidate admitted via the weaker evidence/source signal alone
        // — neither anchored by MustHit nor by a sample-required relation. The collection
        // gate already enforces metadata overlap, so we use the stronger anchors here.
        var graphNoise = new List<string>();
        var requiredRelationSet = new HashSet<string>(sample.RequiredRelations, StringComparer.OrdinalIgnoreCase);
        var mustHitSet = new HashSet<string>(sample.MustHitItemIds, StringComparer.OrdinalIgnoreCase);
        foreach (var graphItem in graphRanked)
        {
            var hasRelationLink = graphItem.Relations.Any(rel => requiredRelationSet.Contains(rel.RelationId));
            var hasMustHitLink = mustHitSet.Contains(graphItem.ItemId);
            if (!hasRelationLink && !hasMustHitLink)
            {
                graphNoise.Add(graphItem.ItemId);
                AddCluster(clusters, GraphVectorRetrievalQualityAuditFailureClusters.GraphNoise, sample.SampleId, graphItem.ItemId, clusterMemberLimit);
            }
        }

        // Vector noise: vector candidate that violates eligibility despite the post-scoring-risk-gated profile filter.
        var vectorNoise = new List<string>();
        foreach (var vectorItem in vectorRanked)
        {
            if (IsBlockedByEligibility(sample, vectorItem))
            {
                vectorNoise.Add(vectorItem.ItemId);
                AddCluster(clusters, GraphVectorRetrievalQualityAuditFailureClusters.VectorNoise, sample.SampleId, vectorItem.ItemId, clusterMemberLimit);
            }
        }

        // Risk counts on merged top-K window.
        var sampleRisk = 0;
        var sampleMustNot = 0;
        var sampleLifecycle = 0;
        var sampleSectionMismatch = 0;
        var sectionMismatchItems = new List<string>();
        var lifecycleRiskItems = new List<string>();
        foreach (var item in topKWindow)
        {
            if (sample.MustNotHitItemIds.Contains(item.ItemId, StringComparer.OrdinalIgnoreCase))
            {
                sampleMustNot++;
            }

            if (!string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
            {
                sampleSectionMismatch++;
                sectionMismatchItems.Add(item.ItemId);
                AddCluster(clusters, GraphVectorRetrievalQualityAuditFailureClusters.SectionMismatch, sample.SampleId, item.ItemId, clusterMemberLimit);
            }

            if (IsLifecycleRisk(item))
            {
                sampleLifecycle++;
                lifecycleRiskItems.Add(item.ItemId);
                AddCluster(clusters, GraphVectorRetrievalQualityAuditFailureClusters.LifecycleMismatch, sample.SampleId, item.ItemId, clusterMemberLimit);
            }

            if (IsRisk(sample, item))
            {
                sampleRisk++;
            }
        }

        var traceLimit = 5;
        return new GraphVectorRetrievalQualityAuditSampleResult
        {
            SampleId = sample.SampleId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ExpectedTargetSection = sample.ExpectedTargetSection,
            MustHitCount = mustHitTotal,
            MustHitRecalledCount = mustHitRecalled,
            MustHitBelowTopKCount = mustHitBelowTopK,
            Recall = recall,
            Precision = precision,
            MeanReciprocalRank = mrr,
            VectorCandidateCount = vectorRanked.Count,
            GraphCandidateCount = graphRanked.Count,
            MergedCandidateCount = merged.Count,
            OverlapCount = overlap,
            VectorOnlyCount = vectorOnly,
            GraphOnlyCount = graphOnly,
            GraphNoiseCount = graphNoise.Count,
            VectorNoiseCount = vectorNoise.Count,
            RankingRegressionCount = rankingRegressions.Count,
            RiskAfterPolicy = sampleRisk,
            MustNotHitRiskAfterPolicy = sampleMustNot,
            LifecycleRiskAfterPolicy = sampleLifecycle,
            SectionMismatchCount = sampleSectionMismatch,
            MetadataEvidenceGapCount = metadataEvidenceGaps.Count,
            MustHitItemIds = mustHits.Take(traceLimit).ToArray(),
            MissingMustHitItemIds = missingMustHits.Take(traceLimit).ToArray(),
            RankingRegressionItemIds = rankingRegressions.Take(traceLimit).ToArray(),
            GraphNoiseItemIds = graphNoise.Take(traceLimit).ToArray(),
            VectorNoiseItemIds = vectorNoise.Take(traceLimit).ToArray(),
            SectionMismatchItemIds = sectionMismatchItems.Take(traceLimit).ToArray(),
            LifecycleRiskItemIds = lifecycleRiskItems.Take(traceLimit).ToArray(),
            MetadataEvidenceGapItemIds = metadataEvidenceGaps.Take(traceLimit).ToArray(),
            MergedCandidateIds = merged.Take(traceLimit).Select(c => c.ItemId).ToArray(),
            ExplainNotes = DefaultExplainNotes
        };
    }

    private static IDictionary<string, ClusterAccumulator> InitializeClusterAccumulators()
    {
        var accumulators = new Dictionary<string, ClusterAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var clusterId in new[]
        {
            GraphVectorRetrievalQualityAuditFailureClusters.MissingCandidate,
            GraphVectorRetrievalQualityAuditFailureClusters.RankingTooLow,
            GraphVectorRetrievalQualityAuditFailureClusters.GraphNoise,
            GraphVectorRetrievalQualityAuditFailureClusters.VectorNoise,
            GraphVectorRetrievalQualityAuditFailureClusters.SectionMismatch,
            GraphVectorRetrievalQualityAuditFailureClusters.LifecycleMismatch,
            GraphVectorRetrievalQualityAuditFailureClusters.MetadataEvidenceGap
        })
        {
            accumulators[clusterId] = new ClusterAccumulator();
        }

        return accumulators;
    }

    private static void AddCluster(
        IDictionary<string, ClusterAccumulator> accumulators,
        string clusterId,
        string sampleId,
        string itemId,
        int memberLimit)
    {
        if (!accumulators.TryGetValue(clusterId, out var accumulator))
        {
            return;
        }

        accumulator.Count++;
        if (accumulator.SampleIds.Count < memberLimit && !accumulator.SampleIds.Contains(sampleId, StringComparer.OrdinalIgnoreCase))
        {
            accumulator.SampleIds.Add(sampleId);
        }

        if (accumulator.ItemIds.Count < memberLimit && !accumulator.ItemIds.Contains(itemId, StringComparer.OrdinalIgnoreCase))
        {
            accumulator.ItemIds.Add(itemId);
        }
    }

    private static IReadOnlyList<GraphVectorRetrievalQualityAuditFailureCluster> BuildFailureClusters(
        IDictionary<string, ClusterAccumulator> accumulators)
    {
        var clusters = new List<GraphVectorRetrievalQualityAuditFailureCluster>();
        foreach (var entry in accumulators)
        {
            if (entry.Value.Count == 0)
            {
                continue;
            }

            ClusterDescriptions.TryGetValue(entry.Key, out var description);
            clusters.Add(new GraphVectorRetrievalQualityAuditFailureCluster
            {
                ClusterId = entry.Key,
                Count = entry.Value.Count,
                SampleIds = entry.Value.SampleIds.ToArray(),
                ItemIds = entry.Value.ItemIds.ToArray(),
                Description = description ?? string.Empty
            });
        }

        return clusters
            .OrderBy(c => c.ClusterId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddBoundaryBlocks(
        List<string> blocked,
        string prefix,
        bool formalRetrievalAllowed,
        bool runtimeSwitchAllowed,
        bool readyForRuntimeSwitch,
        bool useForRuntime,
        bool packageOutputChanged,
        bool packingPolicyChanged,
        bool runtimeMutated,
        bool vectorStoreBindingChanged,
        bool formalPackageWritten)
    {
        if (formalRetrievalAllowed)
        {
            blocked.Add($"{prefix}FormalRetrievalAllowed");
        }

        if (runtimeSwitchAllowed || readyForRuntimeSwitch || useForRuntime)
        {
            blocked.Add($"{prefix}RuntimeSwitchAllowed");
        }

        if (packageOutputChanged)
        {
            blocked.Add($"{prefix}PackageOutputChanged");
        }

        if (packingPolicyChanged)
        {
            blocked.Add($"{prefix}PackingPolicyChanged");
        }

        if (runtimeMutated)
        {
            blocked.Add($"{prefix}RuntimeMutated");
        }

        if (vectorStoreBindingChanged)
        {
            blocked.Add($"{prefix}VectorStoreBindingChanged");
        }

        if (formalPackageWritten)
        {
            blocked.Add($"{prefix}FormalPackageWritten");
        }
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
            return GraphVectorRetrievalQualityAuditRecommendations.ReadyForRetrievalQualityFreeze;
        }

        if (blocked.Contains("PackageShadowGateMissing", StringComparer.OrdinalIgnoreCase))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByMissingPackageShadowGate;
        }

        if (blocked.Contains("PackageShadowGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByPackageShadowGateNotPassed;
        }

        if (blocked.Contains("MissingDataset", StringComparer.OrdinalIgnoreCase))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByMissingDataset;
        }

        if (blocked.Contains("EmptyAuditOutput", StringComparer.OrdinalIgnoreCase))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByEmptyAuditOutput;
        }

        if (blocked.Contains("MustNotHitRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByMustNotHitRisk;
        }

        if (blocked.Contains("LifecycleRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByLifecycleRisk;
        }

        if (blocked.Contains("SectionMismatchDetected", StringComparer.OrdinalIgnoreCase))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedBySectionMismatch;
        }

        if (blocked.Contains("RiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByRiskAfterPolicy;
        }

        if (blocked.Contains("GraphNoiseExceedsThreshold", StringComparer.OrdinalIgnoreCase))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByGraphNoiseExceedsThreshold;
        }

        if (blocked.Any(static reason => reason.Contains("RankingRegression", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("MustHitBelowTopK", StringComparison.OrdinalIgnoreCase)))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByRankingRegressionExceedsThreshold;
        }

        if (blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static reason => reason.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByVectorStoreBindingChange;
        }

        if (blocked.Any(static reason => reason.Contains("FormalRetrieval", StringComparison.OrdinalIgnoreCase)))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("FormalPackageWritten", StringComparison.OrdinalIgnoreCase)))
        {
            return GraphVectorRetrievalQualityAuditRecommendations.BlockedByRuntimeMutation;
        }

        return GraphVectorRetrievalQualityAuditRecommendations.KeepPreviewOnly;
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

    private static void AppendClusters(StringBuilder builder, IReadOnlyList<GraphVectorRetrievalQualityAuditFailureCluster> clusters)
    {
        builder.AppendLine();
        builder.AppendLine("## Failure Clusters");
        if (clusters.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var cluster in clusters)
        {
            builder.AppendLine($"- clusterId: `{cluster.ClusterId}` count: `{cluster.Count}`");
            if (!string.IsNullOrWhiteSpace(cluster.Description))
            {
                builder.AppendLine($"  - description: {cluster.Description}");
            }

            if (cluster.SampleIds.Count > 0)
            {
                builder.AppendLine($"  - samples: `{string.Join(", ", cluster.SampleIds)}`");
            }

            if (cluster.ItemIds.Count > 0)
            {
                builder.AppendLine($"  - items: `{string.Join(", ", cluster.ItemIds)}`");
            }
        }
    }

    private static void AppendSamples(StringBuilder builder, IReadOnlyList<GraphVectorRetrievalQualityAuditSampleResult> samples)
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
            builder.AppendLine($"  - mustHit (count/recalled/below): `{sample.MustHitCount}/{sample.MustHitRecalledCount}/{sample.MustHitBelowTopKCount}`");
            builder.AppendLine($"  - recall/precision/mrr: `{sample.Recall:F4}/{sample.Precision:F4}/{sample.MeanReciprocalRank:F4}`");
            builder.AppendLine($"  - source counts (vector/graph/merged/overlap/vectorOnly/graphOnly): `{sample.VectorCandidateCount}/{sample.GraphCandidateCount}/{sample.MergedCandidateCount}/{sample.OverlapCount}/{sample.VectorOnlyCount}/{sample.GraphOnlyCount}`");
            builder.AppendLine($"  - graphNoise/vectorNoise/rankingRegression: `{sample.GraphNoiseCount}/{sample.VectorNoiseCount}/{sample.RankingRegressionCount}`");
            builder.AppendLine($"  - risk/mustNot/lifecycle/sectionMismatch/metadataGap: `{sample.RiskAfterPolicy}/{sample.MustNotHitRiskAfterPolicy}/{sample.LifecycleRiskAfterPolicy}/{sample.SectionMismatchCount}/{sample.MetadataEvidenceGapCount}`");
            if (sample.MergedCandidateIds.Count > 0)
            {
                builder.AppendLine($"  - merged ids: `{string.Join(", ", sample.MergedCandidateIds)}`");
            }

            if (sample.MissingMustHitItemIds.Count > 0)
            {
                builder.AppendLine($"  - missing mustHit: `{string.Join(", ", sample.MissingMustHitItemIds)}`");
            }
        }
    }

    private readonly record struct ScoredItem(RetrievalDatasetV2CorpusItem Item, double Score);

    private sealed class ClusterAccumulator
    {
        public int Count { get; set; }
        public List<string> SampleIds { get; } = new();
        public List<string> ItemIds { get; } = new();
    }

    private sealed class Totals
    {
        public int SampleCount { get; private set; }
        public int MustHitTotal { get; private set; }
        public int MustHitRecalledTotal { get; private set; }
        public double RecallSum { get; private set; }
        public double PrecisionSum { get; private set; }
        public double MrrSum { get; private set; }
        public int VectorContributionCount { get; private set; }
        public int GraphContributionCount { get; private set; }
        public int OverlapCount { get; private set; }
        public int VectorOnlyCount { get; private set; }
        public int GraphOnlyCount { get; private set; }
        public int GraphNoiseCount { get; private set; }
        public int VectorNoiseCount { get; private set; }
        public int RankingRegressionCount { get; private set; }
        public int MustHitBelowTopKCount { get; private set; }
        public int RiskAfterPolicy { get; private set; }
        public int MustNotHitRiskAfterPolicy { get; private set; }
        public int LifecycleRiskAfterPolicy { get; private set; }
        public int SectionMismatchCount { get; private set; }
        public int MetadataEvidenceGapCount { get; private set; }

        public void Add(GraphVectorRetrievalQualityAuditSampleResult sample)
        {
            SampleCount++;
            MustHitTotal += sample.MustHitCount;
            MustHitRecalledTotal += sample.MustHitRecalledCount;
            RecallSum += sample.Recall;
            PrecisionSum += sample.Precision;
            MrrSum += sample.MeanReciprocalRank;
            VectorContributionCount += sample.VectorCandidateCount;
            GraphContributionCount += sample.GraphCandidateCount;
            OverlapCount += sample.OverlapCount;
            VectorOnlyCount += sample.VectorOnlyCount;
            GraphOnlyCount += sample.GraphOnlyCount;
            GraphNoiseCount += sample.GraphNoiseCount;
            VectorNoiseCount += sample.VectorNoiseCount;
            RankingRegressionCount += sample.RankingRegressionCount;
            MustHitBelowTopKCount += sample.MustHitBelowTopKCount;
            RiskAfterPolicy += sample.RiskAfterPolicy;
            MustNotHitRiskAfterPolicy += sample.MustNotHitRiskAfterPolicy;
            LifecycleRiskAfterPolicy += sample.LifecycleRiskAfterPolicy;
            SectionMismatchCount += sample.SectionMismatchCount;
            MetadataEvidenceGapCount += sample.MetadataEvidenceGapCount;
        }
    }
}

/// <summary>V5.4 retrieval quality audit 选项。</summary>
public sealed class GraphVectorRetrievalQualityAuditOptions
{
    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int TopK { get; init; } = 5;

    public int VectorTopK { get; init; } = 5;

    public int GraphTopK { get; init; } = 5;

    public int MergedTopK { get; init; } = 8;

    public int MaxSampleTraceCount { get; init; } = 5;

    public int MaxFailureClusterMembers { get; init; } = 5;

    public int GraphNoiseThreshold { get; init; } = 0;

    public int RankingRegressionThreshold { get; init; } = 0;

    public int MustHitBelowTopKThreshold { get; init; } = 0;

    public bool RequirePackageShadowGatePassed { get; init; } = true;

    public bool RequireAdapterGatePassed { get; init; } = true;

    public bool UseForRuntime { get; init; } = false;

    public bool FormalRetrievalAllowed { get; init; } = false;
}
