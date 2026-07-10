using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.13 enriched candidate source repair recheck。
/// 复用 V5.12 的 enriched projection 和 V5.10 source repair runner，判断 metadata enrichment
/// 是否真正转化为 retrieval quality 提升。只读复核，不改变 formal retrieval/runtime/package。
/// </summary>
public sealed class EnrichedCandidateSourceRepairRecheckRunner
{
    private const double Epsilon = 1e-9;

    public EnrichedCandidateSourceRepairRecheckReport BuildPreview(
        RuntimeRetrievalFeatureDerivationReport? derivationGate,
        InputMetadataEnrichmentPreviewReport? enrichmentGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        EnrichedCandidateSourceRepairRecheckOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(derivationGate, enrichmentGate, dataset, runtimeChangeGate, sourceScan, options, sourceReports, gateMode: false);

    public EnrichedCandidateSourceRepairRecheckReport BuildGate(
        RuntimeRetrievalFeatureDerivationReport? derivationGate,
        InputMetadataEnrichmentPreviewReport? enrichmentGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        EnrichedCandidateSourceRepairRecheckOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(derivationGate, enrichmentGate, dataset, runtimeChangeGate, sourceScan, options, sourceReports, gateMode: true);

    public static string BuildMarkdown(string title, EnrichedCandidateSourceRepairRecheckReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine($"CreatedAt: `{report.CreatedAt:O}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine($"- RecheckPassed: `{report.RecheckPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- QualityImproved: `{report.QualityImproved}`");
        builder.AppendLine($"- EnrichedSourceRepairPassed: `{report.EnrichedSourceRepairPassed}`");
        builder.AppendLine($"- MetadataCoverageDelta: `{report.MetadataCoverageDelta}`");
        builder.AppendLine($"- V5.12 enrichment gate passed: `{report.V512EnrichmentGatePassed}`");
        builder.AppendLine($"- Derivation gate passed: `{report.DerivationGatePassed}`");
        builder.AppendLine($"- Runtime change gate passed: `{report.RuntimeChangeGatePassed}`");
        builder.AppendLine();
        builder.AppendLine("## Source Repair Before / After Enrichment");
        builder.AppendLine($"- Train derived recall: `{report.OriginalTrainDerivedRecall:F4}` -> `{report.EnrichedTrainDerivedRecall:F4}` delta `{report.TrainDerivedRecallDelta:+0.0000;-0.0000;0.0000}`");
        builder.AppendLine($"- Train derived MRR: `{report.OriginalTrainDerivedMrr:F4}` -> `{report.EnrichedTrainDerivedMrr:F4}` delta `{report.TrainDerivedMrrDelta:+0.0000;-0.0000;0.0000}`");
        builder.AppendLine($"- Holdout derived recall: `{report.OriginalHoldoutDerivedRecall:F4}` -> `{report.EnrichedHoldoutDerivedRecall:F4}` delta `{report.HoldoutDerivedRecallDelta:+0.0000;-0.0000;0.0000}`");
        builder.AppendLine($"- Holdout derived MRR: `{report.OriginalHoldoutDerivedMrr:F4}` -> `{report.EnrichedHoldoutDerivedMrr:F4}` delta `{report.HoldoutDerivedMrrDelta:+0.0000;-0.0000;0.0000}`");
        builder.AppendLine($"- Must-hit below topK: `{report.OriginalMustHitBelowTopK}` -> `{report.EnrichedMustHitBelowTopK}` delta `{report.MustHitBelowTopKDelta}`");
        builder.AppendLine($"- Best profile: `{report.OriginalBestProfileId}` -> `{report.EnrichedBestProfileId}`");
        builder.AppendLine();
        builder.AppendLine("## Safety Invariants");
        builder.AppendLine($"- Risk/mustNot/lifecycle: `{report.RiskAfterPolicy}` / `{report.MustNotHitRiskAfterPolicy}` / `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendList(builder, "Quality Blocked Reasons", report.QualityBlockedReasons);
        return builder.ToString();
    }

    private EnrichedCandidateSourceRepairRecheckReport Build(
        RuntimeRetrievalFeatureDerivationReport? derivationGate,
        InputMetadataEnrichmentPreviewReport? enrichmentGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        EnrichedCandidateSourceRepairRecheckOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new EnrichedCandidateSourceRepairRecheckOptions();
        var hardBlocked = new List<string>();
        var qualityBlocked = new List<string>();

        if (dataset is null || dataset.CorpusItems.Count == 0 || dataset.Samples.Count == 0)
        {
            hardBlocked.Add("MissingDataset");
            dataset = new RetrievalDatasetV2GeneratedDataset();
        }

        if (options.RequireV512EnrichmentGatePassed && (enrichmentGate is null || !enrichmentGate.GatePassed))
        {
            hardBlocked.Add("V512InputMetadataEnrichmentGateNotPassed");
        }

        if (derivationGate is null || !derivationGate.GatePassed)
        {
            hardBlocked.Add("RuntimeFeatureDerivationGateNotPassed");
        }

        if (options.RequireRuntimeChangeGate && (runtimeChangeGate is null || !runtimeChangeGate.Passed))
        {
            hardBlocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (options.RequireSourceScan && (sourceScan is null || !sourceScan.ScanPerformed))
        {
            hardBlocked.Add("SourceScanMissing");
        }

        if (sourceScan is not null && sourceScan.FixtureTokenHitCount > 0)
        {
            hardBlocked.Add("EvalLabelOrFixtureSpecialCasingDetected");
        }

        var repairOptions = options.SourceRepairOptions ?? new QueryDrivenCandidateSourceRepairOptions();
        var sourceRepairRunner = new QueryDrivenCandidateSourceRepairRunner();
        var originalRepair = sourceRepairRunner.BuildPreview(
            derivationGate,
            dataset,
            sourceScan,
            repairOptions,
            sourceReports);
        var enrichedDataset = dataset.CorpusItems.Count == 0
            ? dataset
            : InputMetadataEnrichmentPreviewRunner.BuildEnrichedProjection(dataset);
        var enrichedRepair = sourceRepairRunner.BuildPreview(
            derivationGate,
            enrichedDataset,
            sourceScan,
            repairOptions,
            sourceReports);

        var trainRecallDelta = enrichedRepair.TrainDerivedRecall - originalRepair.TrainDerivedRecall;
        var trainMrrDelta = enrichedRepair.TrainDerivedMrr - originalRepair.TrainDerivedMrr;
        var holdoutRecallDelta = enrichedRepair.HoldoutDerivedRecall - originalRepair.HoldoutDerivedRecall;
        var holdoutMrrDelta = enrichedRepair.HoldoutDerivedMrr - originalRepair.HoldoutDerivedMrr;
        var belowDelta = enrichedRepair.CombinedSource.MustHitBelowTopK - originalRepair.CombinedSource.MustHitBelowTopK;

        var metricTolerance = Math.Max(Epsilon, options.MetricTolerance);
        var qualityImproved = trainRecallDelta > metricTolerance
            || trainMrrDelta > metricTolerance
            || holdoutRecallDelta > metricTolerance
            || holdoutMrrDelta > metricTolerance
            || belowDelta < 0;
        var noRegression = trainRecallDelta >= -metricTolerance
            && trainMrrDelta >= -metricTolerance
            && holdoutRecallDelta >= -metricTolerance
            && holdoutMrrDelta >= -metricTolerance
            && belowDelta <= 0;

        if (!noRegression)
        {
            hardBlocked.Add("EnrichedSourceRepairRegression");
        }

        if (!qualityImproved)
        {
            qualityBlocked.Add("NoQualityLiftFromEnrichedMetadata");
        }

        if (!enrichedRepair.ReportPassed)
        {
            qualityBlocked.Add("EnrichedSourceRepairGateNotPassed");
        }

        var riskAfterPolicy = enrichedRepair.RiskAfterPolicy;
        var mustNotRisk = enrichedRepair.MustNotHitRiskAfterPolicy;
        var lifecycleRisk = enrichedRepair.LifecycleRiskAfterPolicy;
        if (riskAfterPolicy != 0 || mustNotRisk != 0 || lifecycleRisk != 0)
        {
            hardBlocked.Add("RiskAfterPolicyNonZero");
        }

        if (enrichedRepair.FormalOutputChanged != 0
            || enrichedRepair.FormalPackageWritten
            || enrichedRepair.PackageOutputChanged
            || enrichedRepair.PackingPolicyChanged
            || enrichedRepair.RuntimeMutated
            || enrichedRepair.VectorStoreBindingChanged
            || enrichedRepair.FormalRetrievalAllowed
            || enrichedRepair.RuntimeSwitchAllowed
            || enrichedRepair.ReadyForRuntimeSwitch
            || enrichedRepair.UseForRuntime)
        {
            hardBlocked.Add("RuntimeOrPackageInvariantChanged");
        }

        var hard = hardBlocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var quality = qualityBlocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var recheckPassed = hard.Length == 0;
        var gatePassed = gateMode && recheckPassed && quality.Length == 0 && qualityImproved && enrichedRepair.ReportPassed;

        return new EnrichedCandidateSourceRepairRecheckReport
        {
            OperationId = (gateMode ? "vector-enriched-candidate-source-repair-recheck-gate-" : "vector-enriched-candidate-source-repair-recheck-") + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            RecheckPassed = recheckPassed,
            GatePassed = gatePassed,
            Recommendation = ResolveRecommendation(recheckPassed, qualityImproved, enrichedRepair.ReportPassed, hard, quality, enrichmentGate),
            V512EnrichmentGatePassed = enrichmentGate?.GatePassed ?? false,
            DerivationGatePassed = derivationGate?.GatePassed ?? false,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed ?? false,
            MetadataCoverageDelta = enrichmentGate?.MetadataCoverageDelta ?? 0,
            QualityImproved = qualityImproved,
            EnrichedSourceRepairPassed = enrichedRepair.ReportPassed,
            OriginalBestProfileId = originalRepair.BestProfileId,
            EnrichedBestProfileId = enrichedRepair.BestProfileId,
            OriginalTrainDerivedRecall = originalRepair.TrainDerivedRecall,
            EnrichedTrainDerivedRecall = enrichedRepair.TrainDerivedRecall,
            TrainDerivedRecallDelta = trainRecallDelta,
            OriginalTrainDerivedMrr = originalRepair.TrainDerivedMrr,
            EnrichedTrainDerivedMrr = enrichedRepair.TrainDerivedMrr,
            TrainDerivedMrrDelta = trainMrrDelta,
            OriginalHoldoutDerivedRecall = originalRepair.HoldoutDerivedRecall,
            EnrichedHoldoutDerivedRecall = enrichedRepair.HoldoutDerivedRecall,
            HoldoutDerivedRecallDelta = holdoutRecallDelta,
            OriginalHoldoutDerivedMrr = originalRepair.HoldoutDerivedMrr,
            EnrichedHoldoutDerivedMrr = enrichedRepair.HoldoutDerivedMrr,
            HoldoutDerivedMrrDelta = holdoutMrrDelta,
            OriginalMustHitBelowTopK = originalRepair.CombinedSource.MustHitBelowTopK,
            EnrichedMustHitBelowTopK = enrichedRepair.CombinedSource.MustHitBelowTopK,
            MustHitBelowTopKDelta = belowDelta,
            OriginalSourceRepair = originalRepair,
            EnrichedSourceRepair = enrichedRepair,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = enrichedRepair.FormalOutputChanged,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BlockedReasons = hard,
            QualityBlockedReasons = quality
        };
    }

    private static string ResolveRecommendation(
        bool recheckPassed,
        bool qualityImproved,
        bool enrichedSourceRepairPassed,
        IReadOnlyList<string> hardBlocked,
        IReadOnlyList<string> qualityBlocked,
        InputMetadataEnrichmentPreviewReport? enrichmentGate)
    {
        if (!recheckPassed)
        {
            if (hardBlocked.Any(static reason => reason.Contains("Regression", StringComparison.OrdinalIgnoreCase)))
            {
                return EnrichedCandidateSourceRepairRecheckRecommendations.BlockedByQualityRegression;
            }

            if (hardBlocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
            {
                return EnrichedCandidateSourceRepairRecheckRecommendations.BlockedByRisk;
            }

            if (hardBlocked.Any(static reason => reason.Contains("Invariant", StringComparison.OrdinalIgnoreCase)))
            {
                return EnrichedCandidateSourceRepairRecheckRecommendations.BlockedByRuntimeInvariant;
            }

            return EnrichedCandidateSourceRepairRecheckRecommendations.BlockedByProtocolMismatch;
        }

        if (qualityImproved && enrichedSourceRepairPassed && qualityBlocked.Count == 0)
        {
            return EnrichedCandidateSourceRepairRecheckRecommendations.ReadyForSourceRepairRecheckFreeze;
        }

        if ((enrichmentGate?.IndependentNonDenseSourceCount ?? 0) <= 0)
        {
            return EnrichedCandidateSourceRepairRecheckRecommendations.NeedsSourceDiverseDataset;
        }

        return qualityImproved
            ? EnrichedCandidateSourceRepairRecheckRecommendations.NeedsMoreSourceRepair
            : EnrichedCandidateSourceRepairRecheckRecommendations.NeedsSourceAwareRankingRepair;
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (items.Count == 0)
        {
            builder.AppendLine("- (none)");
            return;
        }

        foreach (var item in items)
        {
            builder.AppendLine($"- `{item}`");
        }
    }
}

public sealed class EnrichedCandidateSourceRepairRecheckOptions
{
    public bool RequireV512EnrichmentGatePassed { get; init; } = true;
    public bool RequireRuntimeChangeGate { get; init; } = true;
    public bool RequireSourceScan { get; init; } = true;
    public double MetricTolerance { get; init; } = 1e-9;
    public QueryDrivenCandidateSourceRepairOptions SourceRepairOptions { get; init; } = new();
}

public sealed class EnrichedCandidateSourceRepairRecheckReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool RecheckPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = EnrichedCandidateSourceRepairRecheckRecommendations.KeepPreviewOnly;
    public bool V512EnrichmentGatePassed { get; init; }
    public bool DerivationGatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public int MetadataCoverageDelta { get; init; }
    public bool QualityImproved { get; init; }
    public bool EnrichedSourceRepairPassed { get; init; }
    public string OriginalBestProfileId { get; init; } = string.Empty;
    public string EnrichedBestProfileId { get; init; } = string.Empty;
    public double OriginalTrainDerivedRecall { get; init; }
    public double EnrichedTrainDerivedRecall { get; init; }
    public double TrainDerivedRecallDelta { get; init; }
    public double OriginalTrainDerivedMrr { get; init; }
    public double EnrichedTrainDerivedMrr { get; init; }
    public double TrainDerivedMrrDelta { get; init; }
    public double OriginalHoldoutDerivedRecall { get; init; }
    public double EnrichedHoldoutDerivedRecall { get; init; }
    public double HoldoutDerivedRecallDelta { get; init; }
    public double OriginalHoldoutDerivedMrr { get; init; }
    public double EnrichedHoldoutDerivedMrr { get; init; }
    public double HoldoutDerivedMrrDelta { get; init; }
    public int OriginalMustHitBelowTopK { get; init; }
    public int EnrichedMustHitBelowTopK { get; init; }
    public int MustHitBelowTopKDelta { get; init; }
    public QueryDrivenCandidateSourceRepairReport OriginalSourceRepair { get; init; } = new();
    public QueryDrivenCandidateSourceRepairReport EnrichedSourceRepair { get; init; } = new();
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool UseForRuntime { get; init; }
    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> QualityBlockedReasons { get; init; } = Array.Empty<string>();
}

public static class EnrichedCandidateSourceRepairRecheckRecommendations
{
    public const string ReadyForSourceRepairRecheckFreeze = "ReadyForSourceRepairRecheckFreeze";
    public const string NeedsSourceAwareRankingRepair = "NeedsSourceAwareRankingRepair";
    public const string NeedsMoreSourceRepair = "NeedsMoreSourceRepair";
    public const string NeedsSourceDiverseDataset = "NeedsSourceDiverseDataset";
    public const string BlockedByQualityRegression = "BlockedByQualityRegression";
    public const string BlockedByProtocolMismatch = "BlockedByProtocolMismatch";
    public const string BlockedByRisk = "BlockedByRisk";
    public const string BlockedByRuntimeInvariant = "BlockedByRuntimeInvariant";
    public const string KeepPreviewOnly = "KeepPreviewOnly";
}
