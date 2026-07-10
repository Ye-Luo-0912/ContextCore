using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.7 Runtime Retrieval Feature Derivation Preview。
/// 在 runtime-only 假设下，从 query / item metadata / policy 推导出
/// runtime feature envelope（不读 eval 金标），然后用 envelope 跑
/// combined-repair scoring，对比 dense-only baseline。
/// 只读：不接 formal retrieval、不写 formal package、不动 formal selected set、
/// 不改 PackingPolicy / package output、不切 runtime、不绑定 IVectorIndexStore。
/// post-scoring risk gate 仍然最后执行。
/// </summary>
public sealed class RuntimeRetrievalFeatureDerivationPreviewRunner
{
    private const string GraphCandidateSource = "read-only relation evidence / expansion preview";
    private const int MaxAnchorEntries = 20;

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

    public RuntimeRetrievalFeatureDerivationReport BuildPreview(
        RuntimeObservableFeatureContractReport? contractGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        RuntimeRetrievalFeatureDerivationOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(contractGate, dataset, sourceScan, options, sourceReports, gateMode: false);

    public RuntimeRetrievalFeatureDerivationReport BuildGate(
        RuntimeObservableFeatureContractReport? contractGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        RuntimeRetrievalFeatureDerivationOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(contractGate, dataset, sourceScan, options, sourceReports, gateMode: true);

    public static string BuildMarkdown(string title, RuntimeRetrievalFeatureDerivationReport report)
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
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- RequiredNextPhase: `{report.RequiredNextPhase}`");
        builder.AppendLine($"- VectorProviderSource: `{report.VectorProviderSource}`");
        builder.AppendLine($"- GraphCandidateSource: `{report.GraphCandidateSource}`");
        builder.AppendLine($"- TopK: `{report.TopK}`");
        builder.AppendLine($"- SeedTopK: `{report.SeedTopK}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- MaxAllowedRecallRegression: `{report.MaxAllowedRecallRegression:F4}`");
        builder.AppendLine($"- MaxAllowedMrrRegression: `{report.MaxAllowedMrrRegression:F4}`");
        builder.AppendLine($"- ForbiddenSampleAnnotationReadCount: `{report.ForbiddenSampleAnnotationReadCount}`");
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

        builder.AppendLine();
        builder.AppendLine("## Coverage (vs eval ground truth)");
        builder.AppendLine($"- TargetSectionMatchRate: `{report.TargetSectionMatchRate:F4}`");
        builder.AppendLine($"- RequiredRelationCoverageRate: `{report.RequiredRelationCoverageRate:F4}`");
        builder.AppendLine($"- EvidenceAnchorCoverageRate: `{report.EvidenceAnchorCoverageRate:F4}`");
        builder.AppendLine($"- SourceAnchorCoverageRate: `{report.SourceAnchorCoverageRate:F4}`");
        builder.AppendLine($"- DerivationCompletenessRate: `{report.DerivationCompletenessRate:F4}`");

        builder.AppendLine();
        builder.AppendLine("## Scoring Comparison");
        builder.AppendLine($"- baseline (dense-only)        recall=`{report.BaselineRecall:F4}` precision=`{report.BaselinePrecision:F4}` mrr=`{report.BaselineMeanReciprocalRank:F4}` belowTopK=`{report.BaselineMustHitBelowTopKCount}`");
        builder.AppendLine($"- derived combined-repair       recall=`{report.DerivedRecall:F4}` precision=`{report.DerivedPrecision:F4}` mrr=`{report.DerivedMeanReciprocalRank:F4}` belowTopK=`{report.DerivedMustHitBelowTopKCount}`");
        builder.AppendLine($"- eval-driven combined-repair   recall=`{report.EvalDrivenRecall:F4}` precision=`{report.EvalDrivenPrecision:F4}` mrr=`{report.EvalDrivenMeanReciprocalRank:F4}` (V5.5 reference)");
        builder.AppendLine($"- delta (derived − baseline)    recall=`{report.DerivedRecallDelta:+0.0000;-0.0000;0.0000}` mrr=`{report.DerivedMrrDelta:+0.0000;-0.0000;0.0000}`");
        builder.AppendLine($"- baseline risk/mustNot/lifecycle/section: `{report.BaselineRiskAfterPolicy}/{report.BaselineMustNotHitRiskAfterPolicy}/{report.BaselineLifecycleRiskAfterPolicy}/{report.BaselineSectionMismatchCount}`");
        builder.AppendLine($"- derived  risk/mustNot/lifecycle/section: `{report.DerivedRiskAfterPolicy}/{report.DerivedMustNotHitRiskAfterPolicy}/{report.DerivedLifecycleRiskAfterPolicy}/{report.DerivedSectionMismatchCount}`");

        AppendSamples(builder, report.Samples);
        AppendSourceScan(builder, report.SourceScan);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("V5.7 preview only. Runtime-derived features computed without reading eval ground-truth labels. No formal retrieval, formal package write, formal selected set change, package output mutation, packing policy mutation, runtime switch, or vector store binding change.");
        return builder.ToString();
    }

    private static RuntimeRetrievalFeatureDerivationReport Build(
        RuntimeObservableFeatureContractReport? contractGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        RuntimeRetrievalFeatureDerivationOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new RuntimeRetrievalFeatureDerivationOptions();
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName;
        var blocked = new List<string>();

        if (contractGate is null)
        {
            blocked.Add("ContractGateMissing");
        }
        else
        {
            if (options.RequireContractGatePassed && !contractGate.GatePassed)
            {
                blocked.Add("ContractGateNotPassed");
            }

            AddBoundaryBlocks(blocked, "ContractGate",
                contractGate.FormalRetrievalAllowed,
                contractGate.RuntimeSwitchAllowed,
                contractGate.ReadyForRuntimeSwitch,
                contractGate.UseForRuntime,
                contractGate.PackageOutputChanged,
                contractGate.PackingPolicyChanged,
                contractGate.RuntimeMutated,
                contractGate.VectorStoreBindingChanged,
                contractGate.FormalPackageWritten);
        }

        if (options.RequireSourceScan && (sourceScan is null || !sourceScan.ScanPerformed))
        {
            blocked.Add("SourceScanMissing");
        }

        if (sourceScan is not null && sourceScan.FixtureTokenHitCount > 0)
        {
            blocked.Add("FixtureSpecialCasingDetected");
        }

        if (options.UseForRuntime || options.FormalRetrievalAllowed)
        {
            blocked.Add("RuntimeMutationAttempt");
        }

        var hasDataset = dataset is not null && dataset.Samples.Count > 0 && dataset.CorpusItems.Count > 0;
        if (!hasDataset)
        {
            blocked.Add("MissingDataset");
        }

        var topK = Math.Max(1, options.TopK);
        var seedTopK = Math.Max(1, options.SeedTopK);
        var traceLimit = Math.Max(0, options.MaxSampleTraceCount);

        var samples = new List<RuntimeRetrievalFeatureDerivationSampleResult>();
        var totals = new DerivationTotals();
        var hasDerivedOutput = false;
        if (hasDataset)
        {
            var sampleIndex = 0;
            foreach (var sample in dataset!.Samples)
            {
                var (baselineMetrics, result) = RunSample(sample, dataset.CorpusItems, profileName, topK, seedTopK, options);
                totals.Add(result);
                totals.AddBaseline(
                    baselineMetrics.Recall,
                    baselineMetrics.Precision,
                    baselineMetrics.Mrr,
                    baselineMetrics.BelowTopK,
                    baselineMetrics.Risk,
                    baselineMetrics.MustNotRisk,
                    baselineMetrics.LifecycleRisk,
                    baselineMetrics.SectionMismatch);
                if (sampleIndex < traceLimit)
                {
                    samples.Add(result);
                }

                if (result.Envelope.RequiredRelations.Count > 0
                    || result.Envelope.EvidenceAnchors.Count > 0
                    || result.Envelope.SourceAnchors.Count > 0
                    || !string.IsNullOrEmpty(result.Envelope.TargetSection))
                {
                    hasDerivedOutput = true;
                }

                sampleIndex++;
            }

            if (totals.SampleCount == 0 || !hasDerivedOutput)
            {
                blocked.Add("EmptyDerivedEnvelope");
            }
        }

        var sampleCount = totals.SampleCount;
        var baselineRecall = sampleCount == 0 ? 0d : totals.BaselineRecallSum / sampleCount;
        var baselinePrecision = sampleCount == 0 ? 0d : totals.BaselinePrecisionSum / sampleCount;
        var baselineMrr = sampleCount == 0 ? 0d : totals.BaselineMrrSum / sampleCount;
        var derivedRecall = sampleCount == 0 ? 0d : totals.DerivedRecallSum / sampleCount;
        var derivedPrecision = sampleCount == 0 ? 0d : totals.DerivedPrecisionSum / sampleCount;
        var derivedMrr = sampleCount == 0 ? 0d : totals.DerivedMrrSum / sampleCount;
        var targetMatchRate = sampleCount == 0 ? 0d : (double)totals.TargetSectionMatchCount / sampleCount;
        var relationCoverageRate = totals.ExpectedRequiredRelationCount == 0
            ? 0d
            : (double)totals.RequiredRelationOverlap / totals.ExpectedRequiredRelationCount;
        var evidenceCoverageRate = totals.ExpectedEvidenceAnchorCount == 0
            ? 0d
            : (double)totals.EvidenceAnchorOverlap / totals.ExpectedEvidenceAnchorCount;
        var sourceCoverageRate = totals.ExpectedSourceAnchorCount == 0
            ? 0d
            : (double)totals.SourceAnchorOverlap / totals.ExpectedSourceAnchorCount;
        var totalExpected = totals.ExpectedRequiredRelationCount
            + totals.ExpectedEvidenceAnchorCount
            + totals.ExpectedSourceAnchorCount;
        var totalCovered = totals.RequiredRelationOverlap
            + totals.EvidenceAnchorOverlap
            + totals.SourceAnchorOverlap;
        var derivationCompletenessRate = totalExpected == 0
            ? 0d
            : (double)totalCovered / totalExpected;

        if (totals.DerivedMustNotHitRiskAfterPolicy > 0)
        {
            blocked.Add("MustNotHitRiskAfterPolicyNonZero");
        }

        if (totals.DerivedLifecycleRiskAfterPolicy > 0)
        {
            blocked.Add("LifecycleRiskAfterPolicyNonZero");
        }

        if (totals.DerivedSectionMismatchCount > 0)
        {
            blocked.Add("SectionMismatchDetected");
        }

        if (totals.DerivedRiskAfterPolicy > 0)
        {
            blocked.Add("DerivedRiskNonZero");
        }

        var recallRegression = baselineRecall - derivedRecall;
        var mrrRegression = baselineMrr - derivedMrr;
        if (recallRegression > options.MaxAllowedRecallRegression)
        {
            blocked.Add("DerivedRecallRegression");
        }

        if (mrrRegression > options.MaxAllowedMrrRegression)
        {
            blocked.Add("DerivedMrrRegression");
        }

        var evalDrivenRecall = options.EvalDrivenRecall;
        var evalDrivenPrecision = options.EvalDrivenPrecision;
        var evalDrivenMrr = options.EvalDrivenMeanReciprocalRank;

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var operationId = (gateMode
            ? "runtime-feature-derivation-gate-"
            : "runtime-feature-derivation-preview-")
            + Guid.NewGuid().ToString("N");

        return new RuntimeRetrievalFeatureDerivationReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            PreviewPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = ResolveRecommendation(distinctBlocked, passed),
            AllowedMode = "PreviewOnly",
            RequiredNextPhase = "RuntimeFeatureDerivationFreeze",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = GraphCandidateSource,
            TopK = topK,
            SeedTopK = seedTopK,
            SampleCount = sampleCount,
            TargetSectionMatchRate = targetMatchRate,
            RequiredRelationCoverageRate = relationCoverageRate,
            EvidenceAnchorCoverageRate = evidenceCoverageRate,
            SourceAnchorCoverageRate = sourceCoverageRate,
            DerivationCompletenessRate = derivationCompletenessRate,
            BaselineRecall = baselineRecall,
            BaselinePrecision = baselinePrecision,
            BaselineMeanReciprocalRank = baselineMrr,
            BaselineMustHitBelowTopKCount = totals.BaselineMustHitBelowTopKCount,
            BaselineRiskAfterPolicy = totals.BaselineRiskAfterPolicy,
            BaselineMustNotHitRiskAfterPolicy = totals.BaselineMustNotHitRiskAfterPolicy,
            BaselineLifecycleRiskAfterPolicy = totals.BaselineLifecycleRiskAfterPolicy,
            BaselineSectionMismatchCount = totals.BaselineSectionMismatchCount,
            DerivedRecall = derivedRecall,
            DerivedPrecision = derivedPrecision,
            DerivedMeanReciprocalRank = derivedMrr,
            DerivedMustHitBelowTopKCount = totals.DerivedMustHitBelowTopKCount,
            DerivedRiskAfterPolicy = totals.DerivedRiskAfterPolicy,
            DerivedMustNotHitRiskAfterPolicy = totals.DerivedMustNotHitRiskAfterPolicy,
            DerivedLifecycleRiskAfterPolicy = totals.DerivedLifecycleRiskAfterPolicy,
            DerivedSectionMismatchCount = totals.DerivedSectionMismatchCount,
            EvalDrivenRecall = evalDrivenRecall,
            EvalDrivenPrecision = evalDrivenPrecision,
            EvalDrivenMeanReciprocalRank = evalDrivenMrr,
            DerivedRecallDelta = derivedRecall - baselineRecall,
            DerivedMrrDelta = derivedMrr - baselineMrr,
            ForbiddenSampleAnnotationReadCount = 0,
            MaxAllowedRecallRegression = options.MaxAllowedRecallRegression,
            MaxAllowedMrrRegression = options.MaxAllowedMrrRegression,
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
            SourceScan = sourceScan ?? new RuntimeObservableFeatureContractSourceScan(),
            Samples = samples,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BlockedReasons = distinctBlocked
        };
    }

    private static (BaselineEvaluation Baseline, RuntimeRetrievalFeatureDerivationSampleResult Sample) RunSample(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        string profileName,
        int topK,
        int seedTopK,
        RuntimeRetrievalFeatureDerivationOptions options)
    {
        var workspaceId = ResolveMetadata(sample, "workspaceId");
        var collectionId = ResolveMetadata(sample, "collectionId");

        // Step 1 — derive envelope from query + corpus seed (no eval ground-truth reads).
        var envelope = DeriveEnvelopeFromQueryContext(
            sample.SampleId,
            workspaceId,
            collectionId,
            sample.QueryText,
            corpusItems,
            seedTopK);

        // Step 2 — baseline (dense-only) scoring.
        var baselineCandidates = RankCandidatesPureDense(sample.QueryText, corpusItems, options.VectorTopK);
        var baselineMerged = baselineCandidates.Take(options.MergedTopK).ToArray();
        var baselineEval = EvaluateAgainstMustHit(sample, baselineMerged, baselineMerged.Take(topK).ToArray(), topK, envelope);
        var baselineMetrics = new BaselineEvaluation(
            baselineEval.Recall,
            baselineEval.Precision,
            baselineEval.Mrr,
            baselineEval.BelowTopK,
            baselineEval.Risk,
            baselineEval.MustNotRisk,
            baselineEval.LifecycleRisk,
            baselineEval.SectionMismatch);

        // Step 3 — derived combined-repair scoring (uses envelope only, never sample.* labels).
        var derivedVectorCandidates = RankCandidatesWithEnvelope(sample.QueryText, corpusItems, profileName, options, envelope);
        var derivedGraphCandidates = CollectGraphCandidatesFromEnvelope(corpusItems, envelope, options.GraphTopK);
        var derivedMerged = MergeCandidates(derivedVectorCandidates, derivedGraphCandidates, options.MergedTopK);
        var derivedEval = EvaluateAgainstMustHit(sample, derivedMerged, derivedMerged.Take(topK).ToArray(), topK, envelope);

        // Step 4 — coverage diff against eval ground-truth (this is *evaluation*, not scoring).
        var targetSectionMatch = string.Equals(envelope.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase);
        var expectedRequiredRelations = sample.RequiredRelations;
        var expectedEvidenceRefs = sample.EvidenceRefs;
        var expectedSourceRefs = sample.SourceRefs;
        var expectedMustNot = sample.MustNotHitItemIds;
        var requiredRelationOverlap = CountOverlap(envelope.RequiredRelations, expectedRequiredRelations);
        var evidenceOverlap = CountOverlap(envelope.EvidenceAnchors, expectedEvidenceRefs);
        var sourceOverlap = CountOverlap(envelope.SourceAnchors, expectedSourceRefs);
        var missingDerivation = 0;
        if (expectedRequiredRelations.Count > 0 && envelope.RequiredRelations.Count == 0)
        {
            missingDerivation++;
        }

        if (expectedEvidenceRefs.Count > 0 && envelope.EvidenceAnchors.Count == 0)
        {
            missingDerivation++;
        }

        if (expectedSourceRefs.Count > 0 && envelope.SourceAnchors.Count == 0)
        {
            missingDerivation++;
        }

        if (expectedMustNot.Count > 0 && envelope.MustNotConstraints.Count == 0)
        {
            missingDerivation++;
        }

        var mustHitCount = sample.MustHitItemIds.Count;

        var sampleResult = new RuntimeRetrievalFeatureDerivationSampleResult
        {
            Envelope = envelope,
            TargetSectionMatch = targetSectionMatch,
            ExpectedRequiredRelationCount = expectedRequiredRelations.Count,
            DerivedRequiredRelationCount = envelope.RequiredRelations.Count,
            RequiredRelationOverlap = requiredRelationOverlap,
            ExpectedEvidenceAnchorCount = expectedEvidenceRefs.Count,
            DerivedEvidenceAnchorCount = envelope.EvidenceAnchors.Count,
            EvidenceAnchorOverlap = evidenceOverlap,
            ExpectedSourceAnchorCount = expectedSourceRefs.Count,
            DerivedSourceAnchorCount = envelope.SourceAnchors.Count,
            SourceAnchorOverlap = sourceOverlap,
            ExpectedMustNotCount = expectedMustNot.Count,
            DerivedMustNotCount = envelope.MustNotConstraints.Count,
            MissingDerivationCount = missingDerivation,
            MustHitCount = mustHitCount,
            MustHitRecalledCount = (int)Math.Round(derivedEval.Recall * Math.Max(1, mustHitCount)),
            MustHitBelowTopKCount = derivedEval.BelowTopK,
            Recall = derivedEval.Recall,
            Precision = derivedEval.Precision,
            MeanReciprocalRank = derivedEval.Mrr,
            RiskAfterPolicy = derivedEval.Risk,
            MustNotHitRiskAfterPolicy = derivedEval.MustNotRisk,
            LifecycleRiskAfterPolicy = derivedEval.LifecycleRisk,
            SectionMismatchCount = derivedEval.SectionMismatch
        };

        return (baselineMetrics, sampleResult);
    }

    private readonly record struct BaselineEvaluation(
        double Recall,
        double Precision,
        double Mrr,
        int BelowTopK,
        int Risk,
        int MustNotRisk,
        int LifecycleRisk,
        int SectionMismatch);

    private static RuntimeRetrievalFeatureEnvelope DeriveEnvelopeFromQueryContext(
        string sampleId,
        string workspaceId,
        string collectionId,
        string queryText,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        int seedTopK)
    {
        // Derivation is purely from query text + item metadata. Sample-side labels are NEVER read here.
        var seedItems = RankCandidatesPureDense(queryText, corpusItems, seedTopK);
        var diagnostics = new List<string>();
        diagnostics.Add($"seedTopK={seedTopK} seedItems={seedItems.Count}");

        var targetSection = ResolveTargetSection(seedItems);
        diagnostics.Add($"targetSection={targetSection}");

        var evidenceAnchors = seedItems
            .SelectMany(item => item.EvidenceRefs)
            .Where(static reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAnchorEntries)
            .ToArray();
        var sourceAnchors = seedItems
            .SelectMany(item => item.SourceRefs)
            .Where(static reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAnchorEntries)
            .ToArray();
        var requiredRelations = seedItems
            .SelectMany(item => item.Relations.Select(rel => rel.RelationId))
            .Where(static relationId => !string.IsNullOrWhiteSpace(relationId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAnchorEntries)
            .ToArray();
        diagnostics.Add($"evidenceAnchors={evidenceAnchors.Length}");
        diagnostics.Add($"sourceAnchors={sourceAnchors.Length}");
        diagnostics.Add($"requiredRelations={requiredRelations.Length}");

        var confidence = seedTopK == 0 ? 0d : Math.Min(1d, (double)seedItems.Count / seedTopK);

        return new RuntimeRetrievalFeatureEnvelope
        {
            SampleId = sampleId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            QueryText = queryText,
            TargetSection = targetSection,
            EvidenceAnchors = evidenceAnchors,
            SourceAnchors = sourceAnchors,
            RequiredRelations = requiredRelations,
            MustNotConstraints = Array.Empty<string>(),
            TargetSectionDerivationSource = "dense seed expansion (frequency-major) -> router.targetSection proxy",
            EvidenceAnchorDerivationSource = "dense seed expansion (item.EvidenceRefs union) -> query.evidenceAnchors proxy",
            SourceAnchorDerivationSource = "dense seed expansion (item.SourceRefs union) -> query.sourceAnchors proxy",
            RequiredRelationDerivationSource = "dense seed expansion (item.Relations union) -> planner.requiredRelations proxy",
            MustNotConstraintDerivationSource = "policy lookup (empty in V5.7 preview; runtime supplies query.mustNotItemIds)",
            Confidence = confidence,
            Diagnostics = diagnostics
        };
    }

    private static string ResolveTargetSection(IReadOnlyList<RetrievalDatasetV2CorpusItem> seedItems)
    {
        if (seedItems.Count == 0)
        {
            return VectorQueryTargetSections.NormalContext;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in seedItems)
        {
            if (string.IsNullOrWhiteSpace(item.TargetSection))
            {
                continue;
            }

            counts.TryGetValue(item.TargetSection, out var existing);
            counts[item.TargetSection] = existing + 1;
        }

        if (counts.Count == 0)
        {
            return VectorQueryTargetSections.NormalContext;
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => SectionPriorityRank(pair.Key))
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .First()
            .Key;
    }

    private static int SectionPriorityRank(string sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return SectionPriority.Count;
        }

        return SectionPriority.TryGetValue(sectionName, out var rank) ? rank : SectionPriority.Count;
    }

    public static IReadOnlyList<RetrievalDatasetV2CorpusItem> RankCandidatesPureDense(
        string queryText,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        int topK)
    {
        var queryTokens = Tokenize(queryText);
        var negativeTokens = ExtractNegativeCueTokens(queryText);
        var scored = corpusItems
            .Select(item =>
            {
                var dense = DenseScore(queryTokens, item);
                var lexical = LexicalScore(queryTokens, item);
                var anchor = AnchorScore(queryTokens, item);
                var negative = NegativeCueOverlap(negativeTokens, item);
                return new ScoredItem(item, Math.Max(0, dense + lexical + anchor * 0.5 - negative * 0.85));
            })
            .Where(static entry => entry.Score > 0)
            .OrderByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return scored
            .Take(Math.Max(1, topK))
            .Select(static entry => entry.Item)
            .ToArray();
    }

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> RankCandidatesWithEnvelope(
        string queryText,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        string profileName,
        RuntimeRetrievalFeatureDerivationOptions options,
        RuntimeRetrievalFeatureEnvelope envelope)
    {
        var queryTokens = Tokenize(queryText);
        var negativeTokens = ExtractNegativeCueTokens(queryText);
        var envelopeEvidence = new HashSet<string>(envelope.EvidenceAnchors, StringComparer.OrdinalIgnoreCase);
        var envelopeSource = new HashSet<string>(envelope.SourceAnchors, StringComparer.OrdinalIgnoreCase);
        var envelopeRelations = new HashSet<string>(envelope.RequiredRelations, StringComparer.OrdinalIgnoreCase);
        var envelopeMustNot = new HashSet<string>(envelope.MustNotConstraints, StringComparer.OrdinalIgnoreCase);
        var envelopeTargetSection = string.IsNullOrWhiteSpace(envelope.TargetSection)
            ? VectorQueryTargetSections.NormalContext
            : envelope.TargetSection;

        var scored = corpusItems
            .Select(item =>
            {
                var dense = DenseScore(queryTokens, item);
                var lexical = LexicalScore(queryTokens, item);
                var anchor = AnchorScore(queryTokens, item);
                var negative = NegativeCueOverlap(negativeTokens, item);
                var baseScore = ScoreForProfile(profileName, dense, lexical, anchor, negative);
                if (baseScore <= 0)
                {
                    return new ScoredItem(item, 0);
                }

                var multiplier = 1d;
                if (options.SectionBoost > 1.0
                    && string.Equals(item.TargetSection, envelopeTargetSection, StringComparison.OrdinalIgnoreCase))
                {
                    multiplier *= options.SectionBoost;
                }

                if (options.EvidenceBoost > 1.0
                    && (item.EvidenceRefs.Any(reference => envelopeEvidence.Contains(reference))
                        || item.SourceRefs.Any(reference => envelopeSource.Contains(reference))))
                {
                    multiplier *= options.EvidenceBoost;
                }

                if (options.RelationBoost > 1.0
                    && item.Relations.Any(rel => envelopeRelations.Contains(rel.RelationId)))
                {
                    multiplier *= options.RelationBoost;
                }

                if (options.LexicalBoost > 1.0 && dense > 0)
                {
                    var ratio = lexical / dense;
                    if (ratio > 0.6)
                    {
                        multiplier *= options.LexicalBoost;
                    }
                }

                return new ScoredItem(item, baseScore * multiplier);
            })
            .Where(static entry => entry.Score > 0)
            .OrderByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            scored = scored
                .Where(entry => !IsRiskByEnvelope(entry.Item, envelopeTargetSection, envelopeMustNot))
                .ToArray();
        }

        return scored
            .Take(Math.Max(1, options.VectorTopK))
            .Select(static entry => entry.Item)
            .ToArray();
    }

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> CollectGraphCandidatesFromEnvelope(
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        RuntimeRetrievalFeatureEnvelope envelope,
        int topK)
    {
        if (envelope.RequiredRelations.Count == 0
            && envelope.EvidenceAnchors.Count == 0
            && envelope.SourceAnchors.Count == 0)
        {
            return Array.Empty<RetrievalDatasetV2CorpusItem>();
        }

        var requiredRelations = new HashSet<string>(envelope.RequiredRelations, StringComparer.OrdinalIgnoreCase);
        var evidence = new HashSet<string>(envelope.EvidenceAnchors, StringComparer.OrdinalIgnoreCase);
        var source = new HashSet<string>(envelope.SourceAnchors, StringComparer.OrdinalIgnoreCase);
        var envelopeMustNot = new HashSet<string>(envelope.MustNotConstraints, StringComparer.OrdinalIgnoreCase);
        var envelopeTargetSection = string.IsNullOrWhiteSpace(envelope.TargetSection)
            ? VectorQueryTargetSections.NormalContext
            : envelope.TargetSection;

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

                return new ScoredItem(item, overlap);
            })
            .Where(static entry => entry.Score > 0)
            .Where(entry => !IsRiskByEnvelope(entry.Item, envelopeTargetSection, envelopeMustNot))
            .OrderByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return scored
            .Take(Math.Max(1, topK))
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

    private static bool IsRiskByEnvelope(RetrievalDatasetV2CorpusItem item, string envelopeTargetSection, ISet<string> envelopeMustNot)
        => envelopeMustNot.Contains(item.ItemId)
            || IsBlockedByEligibility(item, envelopeTargetSection)
            || IsLifecycleRisk(item)
            || !string.Equals(item.TargetSection, envelopeTargetSection, StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockedByEligibility(RetrievalDatasetV2CorpusItem item, string envelopeTargetSection)
    {
        if (!string.Equals(item.TargetSection, envelopeTargetSection, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Evaluation helper. Reads <see cref="RetrievalDatasetV2Sample.MustHitItemIds"/> for recall computation only;
    /// this is *evaluation*, not scoring input. Risk counts use envelope-derived target section to ensure
    /// no eval label leaks into the runtime path.
    /// </summary>
    private static (double Recall, double Precision, double Mrr, int BelowTopK,
                    int Risk, int MustNotRisk, int LifecycleRisk, int SectionMismatch)
        EvaluateAgainstMustHit(
            RetrievalDatasetV2Sample sample,
            IReadOnlyList<RetrievalDatasetV2CorpusItem> merged,
            IReadOnlyList<RetrievalDatasetV2CorpusItem> topKWindow,
            int topK,
            RuntimeRetrievalFeatureEnvelope envelope)
    {
        var mustHits = sample.MustHitItemIds;
        var topKWindowIds = topKWindow.Select(c => c.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mergedRankByItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < merged.Count; i++)
        {
            mergedRankByItem[merged[i].ItemId] = i + 1;
        }

        var recalled = 0;
        var firstRank = 0;
        var belowTopK = 0;
        foreach (var mustHitId in mustHits)
        {
            if (topKWindowIds.Contains(mustHitId))
            {
                recalled++;
                if (firstRank == 0
                    && mergedRankByItem.TryGetValue(mustHitId, out var rank))
                {
                    firstRank = rank;
                }
            }

            if (mergedRankByItem.TryGetValue(mustHitId, out var mr) && mr > topK)
            {
                belowTopK++;
            }
        }

        var recall = mustHits.Count == 0 ? 0d : (double)recalled / mustHits.Count;
        var precision = topKWindow.Count == 0 ? 0d : (double)recalled / topKWindow.Count;
        var mrr = firstRank == 0 ? 0d : 1d / firstRank;

        var envelopeMustNot = new HashSet<string>(envelope.MustNotConstraints, StringComparer.OrdinalIgnoreCase);
        var envelopeTarget = string.IsNullOrWhiteSpace(envelope.TargetSection)
            ? VectorQueryTargetSections.NormalContext
            : envelope.TargetSection;
        var risk = 0;
        var mustNotRisk = 0;
        var lifecycleRisk = 0;
        var sectionMismatch = 0;
        foreach (var item in topKWindow)
        {
            if (envelopeMustNot.Contains(item.ItemId))
            {
                mustNotRisk++;
            }

            if (!string.Equals(item.TargetSection, envelopeTarget, StringComparison.OrdinalIgnoreCase))
            {
                sectionMismatch++;
            }

            if (IsLifecycleRisk(item))
            {
                lifecycleRisk++;
            }

            if (IsRiskByEnvelope(item, envelopeTarget, envelopeMustNot))
            {
                risk++;
            }
        }

        return (recall, precision, mrr, belowTopK, risk, mustNotRisk, lifecycleRisk, sectionMismatch);
    }

    private static int CountOverlap(IReadOnlyList<string> derived, IReadOnlyList<string> expected)
    {
        if (derived.Count == 0 || expected.Count == 0)
        {
            return 0;
        }

        var derivedSet = new HashSet<string>(derived, StringComparer.OrdinalIgnoreCase);
        return expected.Count(item => derivedSet.Contains(item));
    }

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

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool passed)
    {
        if (passed)
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.ReadyForRuntimeFeatureFreeze;
        }

        if (blocked.Contains("ContractGateMissing", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByMissingContractGate;
        }

        if (blocked.Contains("ContractGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByContractGateNotPassed;
        }

        if (blocked.Contains("MissingDataset", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByMissingDataset;
        }

        if (blocked.Contains("EmptyDerivedEnvelope", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByEmptyDerivedEnvelope;
        }

        if (blocked.Contains("SourceScanMissing", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedBySourceScanMissing;
        }

        if (blocked.Contains("FixtureSpecialCasingDetected", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByFixtureSpecialCasing;
        }

        if (blocked.Contains("MustNotHitRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByMustNotHitRisk;
        }

        if (blocked.Contains("LifecycleRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByLifecycleRisk;
        }

        if (blocked.Contains("SectionMismatchDetected", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedBySectionMismatch;
        }

        if (blocked.Contains("DerivedRiskNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByDerivedRiskNonZero;
        }

        if (blocked.Contains("DerivedRecallRegression", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByDerivedRecallRegression;
        }

        if (blocked.Contains("DerivedMrrRegression", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByDerivedMrrRegression;
        }

        if (blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static reason => reason.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByVectorStoreBindingChange;
        }

        if (blocked.Any(static reason => reason.Contains("FormalRetrieval", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("FormalPackageWritten", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeRetrievalFeatureDerivationRecommendations.BlockedByRuntimeMutation;
        }

        return RuntimeRetrievalFeatureDerivationRecommendations.KeepPreviewOnly;
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

    private static void AppendSamples(StringBuilder builder, IReadOnlyList<RuntimeRetrievalFeatureDerivationSampleResult> samples)
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
            builder.AppendLine($"- sampleId: `{sample.Envelope.SampleId}` confidence=`{sample.Envelope.Confidence:F2}`");
            builder.AppendLine($"  - envelope.targetSection: `{sample.Envelope.TargetSection}`");
            builder.AppendLine($"  - envelope counts (relations/evidence/source/mustNot): `{sample.Envelope.RequiredRelations.Count}/{sample.Envelope.EvidenceAnchors.Count}/{sample.Envelope.SourceAnchors.Count}/{sample.Envelope.MustNotConstraints.Count}`");
            builder.AppendLine($"  - coverage (target/relation/evidence/source overlap): `{sample.TargetSectionMatch}/{sample.RequiredRelationOverlap}/{sample.EvidenceAnchorOverlap}/{sample.SourceAnchorOverlap}`");
            builder.AppendLine($"  - missingDerivation: `{sample.MissingDerivationCount}`");
            builder.AppendLine($"  - mustHit (count/recalled/below): `{sample.MustHitCount}/{sample.MustHitRecalledCount}/{sample.MustHitBelowTopKCount}`");
            builder.AppendLine($"  - recall/precision/mrr: `{sample.Recall:F4}/{sample.Precision:F4}/{sample.MeanReciprocalRank:F4}`");
            builder.AppendLine($"  - risk/mustNot/lifecycle/section: `{sample.RiskAfterPolicy}/{sample.MustNotHitRiskAfterPolicy}/{sample.LifecycleRiskAfterPolicy}/{sample.SectionMismatchCount}`");
        }
    }

    private static void AppendSourceScan(StringBuilder builder, RuntimeObservableFeatureContractSourceScan scan)
    {
        builder.AppendLine();
        builder.AppendLine("## Source Scan");
        builder.AppendLine($"- scanPerformed: `{scan.ScanPerformed}`");
        builder.AppendLine($"- scannedFileCount: `{scan.ScannedFileCount}`");
        builder.AppendLine($"- fixtureTokenHitCount: `{scan.FixtureTokenHitCount}`");
        if (scan.FlaggedTokens.Count > 0)
        {
            builder.AppendLine($"- flaggedTokens: `{string.Join(", ", scan.FlaggedTokens)}`");
        }

        if (scan.FlaggedFiles.Count > 0)
        {
            builder.AppendLine($"- flaggedFiles: `{string.Join(", ", scan.FlaggedFiles)}`");
        }
    }

    private readonly record struct ScoredItem(RetrievalDatasetV2CorpusItem Item, double Score);

    private sealed class DerivationTotals
    {
        public int SampleCount { get; private set; }
        public int TargetSectionMatchCount { get; private set; }
        public int ExpectedRequiredRelationCount { get; private set; }
        public int RequiredRelationOverlap { get; private set; }
        public int ExpectedEvidenceAnchorCount { get; private set; }
        public int EvidenceAnchorOverlap { get; private set; }
        public int ExpectedSourceAnchorCount { get; private set; }
        public int SourceAnchorOverlap { get; private set; }
        public double BaselineRecallSum { get; private set; }
        public double BaselinePrecisionSum { get; private set; }
        public double BaselineMrrSum { get; private set; }
        public int BaselineMustHitBelowTopKCount { get; private set; }
        public int BaselineRiskAfterPolicy { get; private set; }
        public int BaselineMustNotHitRiskAfterPolicy { get; private set; }
        public int BaselineLifecycleRiskAfterPolicy { get; private set; }
        public int BaselineSectionMismatchCount { get; private set; }
        public double DerivedRecallSum { get; private set; }
        public double DerivedPrecisionSum { get; private set; }
        public double DerivedMrrSum { get; private set; }
        public int DerivedMustHitBelowTopKCount { get; private set; }
        public int DerivedRiskAfterPolicy { get; private set; }
        public int DerivedMustNotHitRiskAfterPolicy { get; private set; }
        public int DerivedLifecycleRiskAfterPolicy { get; private set; }
        public int DerivedSectionMismatchCount { get; private set; }

        public void Add(RuntimeRetrievalFeatureDerivationSampleResult sample)
        {
            SampleCount++;
            if (sample.TargetSectionMatch)
            {
                TargetSectionMatchCount++;
            }

            ExpectedRequiredRelationCount += sample.ExpectedRequiredRelationCount;
            RequiredRelationOverlap += sample.RequiredRelationOverlap;
            ExpectedEvidenceAnchorCount += sample.ExpectedEvidenceAnchorCount;
            EvidenceAnchorOverlap += sample.EvidenceAnchorOverlap;
            ExpectedSourceAnchorCount += sample.ExpectedSourceAnchorCount;
            SourceAnchorOverlap += sample.SourceAnchorOverlap;

            DerivedRecallSum += sample.Recall;
            DerivedPrecisionSum += sample.Precision;
            DerivedMrrSum += sample.MeanReciprocalRank;
            DerivedMustHitBelowTopKCount += sample.MustHitBelowTopKCount;
            DerivedRiskAfterPolicy += sample.RiskAfterPolicy;
            DerivedMustNotHitRiskAfterPolicy += sample.MustNotHitRiskAfterPolicy;
            DerivedLifecycleRiskAfterPolicy += sample.LifecycleRiskAfterPolicy;
            DerivedSectionMismatchCount += sample.SectionMismatchCount;
        }

        public void AddBaseline(
            double recall,
            double precision,
            double mrr,
            int belowTopK,
            int risk,
            int mustNotRisk,
            int lifecycleRisk,
            int sectionMismatch)
        {
            BaselineRecallSum += recall;
            BaselinePrecisionSum += precision;
            BaselineMrrSum += mrr;
            BaselineMustHitBelowTopKCount += belowTopK;
            BaselineRiskAfterPolicy += risk;
            BaselineMustNotHitRiskAfterPolicy += mustNotRisk;
            BaselineLifecycleRiskAfterPolicy += lifecycleRisk;
            BaselineSectionMismatchCount += sectionMismatch;
        }
    }
}

/// <summary>V5.7 runtime retrieval feature derivation 选项。</summary>
public sealed class RuntimeRetrievalFeatureDerivationOptions
{
    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int TopK { get; init; } = 5;

    public int SeedTopK { get; init; } = 5;

    public int VectorTopK { get; init; } = 10;

    public int GraphTopK { get; init; } = 10;

    public int MergedTopK { get; init; } = 12;

    public double SectionBoost { get; init; } = 1.5;

    public double EvidenceBoost { get; init; } = 1.75;

    public double RelationBoost { get; init; } = 1.6;

    public double LexicalBoost { get; init; } = 1.4;

    public int MaxSampleTraceCount { get; init; } = 5;

    public double MaxAllowedRecallRegression { get; init; } = 0.0;

    public double MaxAllowedMrrRegression { get; init; } = 0.0;

    public bool RequireContractGatePassed { get; init; } = true;

    public bool RequireSourceScan { get; init; } = true;

    public bool UseForRuntime { get; init; } = false;

    public bool FormalRetrievalAllowed { get; init; } = false;

    public double EvalDrivenRecall { get; init; }

    public double EvalDrivenPrecision { get; init; }

    public double EvalDrivenMeanReciprocalRank { get; init; }
}
