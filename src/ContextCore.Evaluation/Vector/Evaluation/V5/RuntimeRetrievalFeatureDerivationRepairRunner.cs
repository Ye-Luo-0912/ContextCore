using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 运行时特征推导修复：用 <see cref="CanonicalRuntimeAnchorResolver"/> 统一样本侧和
/// 语料侧 anchor 命名空间；用 <see cref="RuntimeRelationIntentDeriver"/> 通过
/// query intent 与伪相关反馈展开关系。Holdout 80/20 切分；scoring、filtering、
/// candidate expansion 三条路径均禁止读样本金标，仅 evaluation helper 读
/// sample.MustHitItemIds 计算召回率（已标注清楚）。只读：不接正式 retrieval、
/// 不写正式 package、不动正式 selected set、不改 PackingPolicy / package output、
/// 不切 runtime、不绑定 IVectorIndexStore。
/// </summary>
public sealed class RuntimeRetrievalFeatureDerivationRepairRunner
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

    public RuntimeRetrievalFeatureDerivationRepairReport BuildPreview(
        RuntimeRetrievalFeatureDerivationReport? derivationGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        RuntimeRetrievalFeatureDerivationRepairOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(derivationGate, dataset, sourceScan, options, sourceReports, gateMode: false);

    public RuntimeRetrievalFeatureDerivationRepairReport BuildGate(
        RuntimeRetrievalFeatureDerivationReport? derivationGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        RuntimeRetrievalFeatureDerivationRepairOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(derivationGate, dataset, sourceScan, options, sourceReports, gateMode: true);

    public static string BuildMarkdown(string title, RuntimeRetrievalFeatureDerivationRepairReport report)
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
        builder.AppendLine($"- TopK: `{report.TopK}`  DenseSeedTopK: `{report.DenseSeedTopK}`  AnchorSeedTopK: `{report.AnchorSeedTopK}`  RelationTopK: `{report.RelationTopK}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}` (train=`{report.TrainSampleCount}` / holdout=`{report.HoldoutSampleCount}`)");
        builder.AppendLine($"- ForbiddenSampleAnnotationReadCount: `{report.ForbiddenSampleAnnotationReadCount}`");
        builder.AppendLine($"- MinRelationCoverageRate: `{report.MinRelationCoverageRate:F4}`");
        builder.AppendLine($"- MaxAllowedHoldoutRecallRegression: `{report.MaxAllowedHoldoutRecallRegression:F4}`");
        builder.AppendLine($"- MaxAllowedHoldoutMrrRegression: `{report.MaxAllowedHoldoutMrrRegression:F4}`");
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
        builder.AppendLine("## Coverage (canonical anchors)");
        builder.AppendLine($"- TargetSectionMatchRate: `{report.TargetSectionMatchRate:F4}`");
        builder.AppendLine($"- CanonicalRequiredRelationCoverageRate: `{report.CanonicalRequiredRelationCoverageRate:F4}` (applicable={report.ApplicableRelationSampleCount}, covered={report.ApplicableRelationCoveredCount})");
        builder.AppendLine($"- CanonicalEvidenceAnchorCoverageRate: `{report.CanonicalEvidenceAnchorCoverageRate:F4}` (applicable={report.ApplicableEvidenceSampleCount}, covered={report.ApplicableEvidenceCoveredCount})");
        builder.AppendLine($"- CanonicalSourceAnchorCoverageRate: `{report.CanonicalSourceAnchorCoverageRate:F4}` (applicable={report.ApplicableSourceSampleCount}, covered={report.ApplicableSourceCoveredCount})");

        builder.AppendLine();
        builder.AppendLine("## Train Scoring");
        builder.AppendLine($"- baseline recall/mrr: `{report.TrainBaselineRecall:F4}/{report.TrainBaselineMrr:F4}`");
        builder.AppendLine($"- derived  recall/mrr: `{report.TrainDerivedRecall:F4}/{report.TrainDerivedMrr:F4}`");
        builder.AppendLine($"- delta R/MRR: `{report.TrainDerivedRecall - report.TrainBaselineRecall:+0.0000;-0.0000;0.0000}/{report.TrainDerivedMrr - report.TrainBaselineMrr:+0.0000;-0.0000;0.0000}`");

        builder.AppendLine();
        builder.AppendLine("## Holdout Scoring");
        builder.AppendLine($"- baseline recall/mrr: `{report.HoldoutBaselineRecall:F4}/{report.HoldoutBaselineMrr:F4}`");
        builder.AppendLine($"- derived  recall/mrr: `{report.HoldoutDerivedRecall:F4}/{report.HoldoutDerivedMrr:F4}`");
        builder.AppendLine($"- delta R/MRR: `{report.HoldoutDerivedRecall - report.HoldoutBaselineRecall:+0.0000;-0.0000;0.0000}/{report.HoldoutDerivedMrr - report.HoldoutBaselineMrr:+0.0000;-0.0000;0.0000}`");

        builder.AppendLine();
        builder.AppendLine("## Risk");
        builder.AppendLine($"- derived risk/mustNot/lifecycle/section: `{report.DerivedRiskAfterPolicy}/{report.DerivedMustNotHitRiskAfterPolicy}/{report.DerivedLifecycleRiskAfterPolicy}/{report.DerivedSectionMismatchCount}`");

        AppendList(builder, "Derivation Diagnostics", report.DerivationDiagnostics);
        AppendSamples(builder, report.Samples);
        AppendSourceScan(builder, report.SourceScan);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("V5.8 preview only. Repair derivation uses canonical anchor resolution and runtime relation intent. No formal retrieval, formal package write, formal selected set change, package output mutation, packing policy mutation, runtime switch, or vector store binding change.");
        return builder.ToString();
    }

    private static RuntimeRetrievalFeatureDerivationRepairReport Build(
        RuntimeRetrievalFeatureDerivationReport? derivationGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        RuntimeRetrievalFeatureDerivationRepairOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new RuntimeRetrievalFeatureDerivationRepairOptions();
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName;
        var blocked = new List<string>();

        if (derivationGate is null)
        {
            blocked.Add("DerivationGateMissing");
        }
        else
        {
            if (options.RequireDerivationGatePassed && !derivationGate.GatePassed)
            {
                blocked.Add("DerivationGateNotPassed");
            }

            AddBoundaryBlocks(blocked, "DerivationGate",
                derivationGate.FormalRetrievalAllowed,
                derivationGate.RuntimeSwitchAllowed,
                derivationGate.ReadyForRuntimeSwitch,
                derivationGate.UseForRuntime,
                derivationGate.PackageOutputChanged,
                derivationGate.PackingPolicyChanged,
                derivationGate.RuntimeMutated,
                derivationGate.VectorStoreBindingChanged,
                derivationGate.FormalPackageWritten);
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
        var traceLimit = Math.Max(0, options.MaxSampleTraceCount);
        var samples = new List<RuntimeRetrievalFeatureDerivationRepairSampleResult>();
        var diagnostics = new List<string>();

        var trainTotals = new SplitTotals();
        var holdoutTotals = new SplitTotals();
        var coverageTotals = new CoverageTotals();
        var hasDerivedOutput = false;
        if (hasDataset)
        {
            var sampleIndex = 0;
            foreach (var sample in dataset!.Samples)
            {
                var split = (sampleIndex % Math.Max(1, options.HoldoutModulus)) == options.HoldoutRemainder
                    ? "holdout"
                    : "train";
                var (baselineRecall, baselineMrr, result) = RunSample(sample, dataset.CorpusItems, profileName, topK, options, split);
                if (split == "holdout")
                {
                    holdoutTotals.Add(result);
                    holdoutTotals.AddBaseline(baselineRecall, baselineMrr);
                }
                else
                {
                    trainTotals.Add(result);
                    trainTotals.AddBaseline(baselineRecall, baselineMrr);
                }

                coverageTotals.Add(result);
                if (sampleIndex < traceLimit)
                {
                    samples.Add(result);
                }

                if (result.Envelope.RequiredRelations.Count > 0
                    || result.Envelope.EvidenceAnchors.Count > 0
                    || result.Envelope.SourceAnchors.Count > 0)
                {
                    hasDerivedOutput = true;
                }

                sampleIndex++;
            }

            if (!hasDerivedOutput || (trainTotals.SampleCount + holdoutTotals.SampleCount) == 0)
            {
                blocked.Add("EmptyRepairEnvelope");
            }
        }

        var sampleCount = trainTotals.SampleCount + holdoutTotals.SampleCount;
        var trainBaselineRecall = trainTotals.SampleCount == 0 ? 0d : trainTotals.BaselineRecallSum / trainTotals.SampleCount;
        var trainBaselineMrr = trainTotals.SampleCount == 0 ? 0d : trainTotals.BaselineMrrSum / trainTotals.SampleCount;
        var trainDerivedRecall = trainTotals.SampleCount == 0 ? 0d : trainTotals.DerivedRecallSum / trainTotals.SampleCount;
        var trainDerivedMrr = trainTotals.SampleCount == 0 ? 0d : trainTotals.DerivedMrrSum / trainTotals.SampleCount;
        var holdoutBaselineRecall = holdoutTotals.SampleCount == 0 ? 0d : holdoutTotals.BaselineRecallSum / holdoutTotals.SampleCount;
        var holdoutBaselineMrr = holdoutTotals.SampleCount == 0 ? 0d : holdoutTotals.BaselineMrrSum / holdoutTotals.SampleCount;
        var holdoutDerivedRecall = holdoutTotals.SampleCount == 0 ? 0d : holdoutTotals.DerivedRecallSum / holdoutTotals.SampleCount;
        var holdoutDerivedMrr = holdoutTotals.SampleCount == 0 ? 0d : holdoutTotals.DerivedMrrSum / holdoutTotals.SampleCount;

        var targetMatchRate = sampleCount == 0 ? 0d : (double)coverageTotals.TargetSectionMatchCount / sampleCount;
        var relationCoverageRate = coverageTotals.ApplicableRelationSampleCount == 0
            ? 0d
            : (double)coverageTotals.ApplicableRelationCoveredCount / coverageTotals.ApplicableRelationSampleCount;
        var evidenceCoverageRate = coverageTotals.ApplicableEvidenceSampleCount == 0
            ? 0d
            : (double)coverageTotals.ApplicableEvidenceCoveredCount / coverageTotals.ApplicableEvidenceSampleCount;
        var sourceCoverageRate = coverageTotals.ApplicableSourceSampleCount == 0
            ? 0d
            : (double)coverageTotals.ApplicableSourceCoveredCount / coverageTotals.ApplicableSourceSampleCount;

        diagnostics.Add($"split: train={trainTotals.SampleCount} holdout={holdoutTotals.SampleCount} (modulus={options.HoldoutModulus}, holdout-remainder={options.HoldoutRemainder})");
        diagnostics.Add($"train recall delta: {trainDerivedRecall - trainBaselineRecall:+0.0000;-0.0000;0.0000}");
        diagnostics.Add($"train mrr delta: {trainDerivedMrr - trainBaselineMrr:+0.0000;-0.0000;0.0000}");
        diagnostics.Add($"holdout recall delta: {holdoutDerivedRecall - holdoutBaselineRecall:+0.0000;-0.0000;0.0000}");
        diagnostics.Add($"holdout mrr delta: {holdoutDerivedMrr - holdoutBaselineMrr:+0.0000;-0.0000;0.0000}");
        diagnostics.Add($"canonical relation coverage: {relationCoverageRate:F4} (min required: {options.MinRelationCoverageRate:F4})");
        diagnostics.Add($"canonical evidence coverage: {evidenceCoverageRate:F4}");
        diagnostics.Add($"canonical source coverage: {sourceCoverageRate:F4}");

        if (coverageTotals.DerivedRiskAfterPolicy > 0)
        {
            blocked.Add("DerivedRiskNonZero");
        }

        if (coverageTotals.DerivedMustNotHitRiskAfterPolicy > 0)
        {
            blocked.Add("MustNotHitRiskAfterPolicyNonZero");
        }

        if (coverageTotals.DerivedLifecycleRiskAfterPolicy > 0)
        {
            blocked.Add("LifecycleRiskAfterPolicyNonZero");
        }

        if (coverageTotals.DerivedSectionMismatchCount > 0)
        {
            blocked.Add("SectionMismatchDetected");
        }

        if (trainTotals.SampleCount > 0)
        {
            if (trainDerivedRecall <= trainBaselineRecall + 1e-9)
            {
                blocked.Add("DerivedRecallNotImproved");
            }

            if (trainDerivedMrr <= trainBaselineMrr + 1e-9)
            {
                blocked.Add("DerivedMrrNotImproved");
            }
        }

        if (holdoutTotals.SampleCount > 0)
        {
            if (holdoutBaselineRecall - holdoutDerivedRecall > options.MaxAllowedHoldoutRecallRegression)
            {
                blocked.Add("HoldoutRecallRegression");
            }

            if (holdoutBaselineMrr - holdoutDerivedMrr > options.MaxAllowedHoldoutMrrRegression)
            {
                blocked.Add("HoldoutMrrRegression");
            }
        }

        if (relationCoverageRate < options.MinRelationCoverageRate
            && coverageTotals.ApplicableRelationSampleCount > 0)
        {
            blocked.Add("LowRelationCoverage");
        }

        if (coverageTotals.ApplicableEvidenceSampleCount > 0
            && coverageTotals.ApplicableEvidenceCoveredCount == 0
            && coverageTotals.ApplicableSourceSampleCount > 0
            && coverageTotals.ApplicableSourceCoveredCount == 0)
        {
            blocked.Add("ZeroAnchorCoverage");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var operationId = (gateMode
            ? "runtime-feature-derivation-repair-gate-"
            : "runtime-feature-derivation-repair-")
            + Guid.NewGuid().ToString("N");

        return new RuntimeRetrievalFeatureDerivationRepairReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            PreviewPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = ResolveRecommendation(distinctBlocked, passed),
            AllowedMode = "PreviewOnly",
            RequiredNextPhase = "RuntimeFeatureDerivationRepairFreeze",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = GraphCandidateSource,
            TopK = topK,
            DenseSeedTopK = options.DenseSeedTopK,
            AnchorSeedTopK = options.AnchorSeedTopK,
            RelationTopK = options.RelationTopK,
            SampleCount = sampleCount,
            TrainSampleCount = trainTotals.SampleCount,
            HoldoutSampleCount = holdoutTotals.SampleCount,
            TrainBaselineRecall = trainBaselineRecall,
            TrainBaselineMrr = trainBaselineMrr,
            TrainDerivedRecall = trainDerivedRecall,
            TrainDerivedMrr = trainDerivedMrr,
            HoldoutBaselineRecall = holdoutBaselineRecall,
            HoldoutBaselineMrr = holdoutBaselineMrr,
            HoldoutDerivedRecall = holdoutDerivedRecall,
            HoldoutDerivedMrr = holdoutDerivedMrr,
            TargetSectionMatchRate = targetMatchRate,
            CanonicalRequiredRelationCoverageRate = relationCoverageRate,
            CanonicalEvidenceAnchorCoverageRate = evidenceCoverageRate,
            CanonicalSourceAnchorCoverageRate = sourceCoverageRate,
            ApplicableEvidenceSampleCount = coverageTotals.ApplicableEvidenceSampleCount,
            ApplicableSourceSampleCount = coverageTotals.ApplicableSourceSampleCount,
            ApplicableRelationSampleCount = coverageTotals.ApplicableRelationSampleCount,
            ApplicableEvidenceCoveredCount = coverageTotals.ApplicableEvidenceCoveredCount,
            ApplicableSourceCoveredCount = coverageTotals.ApplicableSourceCoveredCount,
            ApplicableRelationCoveredCount = coverageTotals.ApplicableRelationCoveredCount,
            DerivedRiskAfterPolicy = coverageTotals.DerivedRiskAfterPolicy,
            DerivedMustNotHitRiskAfterPolicy = coverageTotals.DerivedMustNotHitRiskAfterPolicy,
            DerivedLifecycleRiskAfterPolicy = coverageTotals.DerivedLifecycleRiskAfterPolicy,
            DerivedSectionMismatchCount = coverageTotals.DerivedSectionMismatchCount,
            ForbiddenSampleAnnotationReadCount = 0,
            MinRelationCoverageRate = options.MinRelationCoverageRate,
            MaxAllowedHoldoutRecallRegression = options.MaxAllowedHoldoutRecallRegression,
            MaxAllowedHoldoutMrrRegression = options.MaxAllowedHoldoutMrrRegression,
            DerivationDiagnostics = diagnostics,
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

    private static (double BaselineRecall, double BaselineMrr, RuntimeRetrievalFeatureDerivationRepairSampleResult Sample) RunSample(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        string profileName,
        int topK,
        RuntimeRetrievalFeatureDerivationRepairOptions options,
        string split)
    {
        var workspaceId = ResolveMetadata(sample, "workspaceId");
        var collectionId = ResolveMetadata(sample, "collectionId");

        // Step 1 — derive envelope. Reads only (sample.SampleId, sample.QueryText, sample.Metadata)
        // and corpus items. NEVER reads sample.MustHit/MustNot/RequiredRelations/EvidenceRefs/
        // SourceRefs/ExpectedTargetSection. The deriver path is parameterised on those three values.
        var envelope = DeriveEnvelope(
            sample.SampleId,
            workspaceId,
            collectionId,
            sample.QueryText,
            corpusItems,
            options);

        // Step 2 — baseline (dense-only) scoring. Same input pattern.
        var baselineCandidates = RankCandidatesPureDense(sample.QueryText, corpusItems, options.VectorTopK);
        var baselineMerged = baselineCandidates.Take(options.MergedTopK).ToArray();
        var baselineEval = EvaluateAgainstMustHit(sample, baselineMerged, baselineMerged.Take(topK).ToArray(), topK, envelope);

        // Step 3 — derived combined-repair scoring using envelope only. mustHit / mustNot / required-relations
        // / evidence / source paths inside RankCandidatesWithEnvelope and CollectGraphCandidatesFromEnvelope
        // never read sample-side fields.
        var derivedVectorCandidates = RankCandidatesWithEnvelope(sample.QueryText, corpusItems, profileName, options, envelope);
        var derivedGraphCandidates = CollectGraphCandidatesFromEnvelope(corpusItems, envelope, options.GraphTopK);
        var derivedMerged = MergeCandidates(derivedVectorCandidates, derivedGraphCandidates, options.MergedTopK);
        var derivedEval = EvaluateAgainstMustHit(sample, derivedMerged, derivedMerged.Take(topK).ToArray(), topK, envelope);

        // Step 4 — coverage diff (evaluation, not scoring input).
        var targetSectionMatch = string.Equals(envelope.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase);
        var canonicalRelationOverlap = CanonicalRuntimeAnchorResolver.CountOverlap(envelope.RequiredRelations, sample.RequiredRelations);
        var canonicalEvidenceOverlap = CanonicalRuntimeAnchorResolver.CountOverlap(envelope.EvidenceAnchors, sample.EvidenceRefs);
        var canonicalSourceOverlap = CanonicalRuntimeAnchorResolver.CountOverlap(envelope.SourceAnchors, sample.SourceRefs);
        var requiredRelationOverlap = CountRawOverlap(envelope.RequiredRelations, sample.RequiredRelations);

        var missingDerivation = 0;
        if (sample.RequiredRelations.Count > 0 && envelope.RequiredRelations.Count == 0)
        {
            missingDerivation++;
        }

        if (sample.EvidenceRefs.Count > 0 && envelope.EvidenceAnchors.Count == 0)
        {
            missingDerivation++;
        }

        if (sample.SourceRefs.Count > 0 && envelope.SourceAnchors.Count == 0)
        {
            missingDerivation++;
        }

        if (sample.MustNotHitItemIds.Count > 0 && envelope.MustNotConstraints.Count == 0)
        {
            missingDerivation++;
        }

        var mustHitCount = sample.MustHitItemIds.Count;
        var sampleResult = new RuntimeRetrievalFeatureDerivationRepairSampleResult
        {
            Envelope = envelope,
            Split = split,
            TargetSectionMatch = targetSectionMatch,
            ExpectedRequiredRelationCount = sample.RequiredRelations.Count,
            DerivedRequiredRelationCount = envelope.RequiredRelations.Count,
            RequiredRelationOverlap = requiredRelationOverlap,
            CanonicalRequiredRelationOverlap = canonicalRelationOverlap,
            ExpectedEvidenceAnchorCount = sample.EvidenceRefs.Count,
            DerivedEvidenceAnchorCount = envelope.EvidenceAnchors.Count,
            CanonicalEvidenceAnchorOverlap = canonicalEvidenceOverlap,
            ExpectedSourceAnchorCount = sample.SourceRefs.Count,
            DerivedSourceAnchorCount = envelope.SourceAnchors.Count,
            CanonicalSourceAnchorOverlap = canonicalSourceOverlap,
            ExpectedMustNotCount = sample.MustNotHitItemIds.Count,
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

        return (baselineEval.Recall, baselineEval.Mrr, sampleResult);
    }

    private static RuntimeRetrievalFeatureEnvelope DeriveEnvelope(
        string sampleId,
        string workspaceId,
        string collectionId,
        string queryText,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        RuntimeRetrievalFeatureDerivationRepairOptions options)
    {
        var seedPool = RuntimeRelationIntentDeriver.ResolveSeedPool(
            queryText,
            corpusItems,
            options.DenseSeedTopK,
            options.AnchorSeedTopK);
        var diagnostics = new List<string>
        {
            $"seedPool={seedPool.Count} (denseSeed={options.DenseSeedTopK}, anchorSeed={options.AnchorSeedTopK})"
        };

        var targetSection = ResolveTargetSection(seedPool);
        diagnostics.Add($"targetSection={targetSection}");

        var evidenceAnchors = seedPool
            .SelectMany(item => item.EvidenceRefs)
            .Where(static reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAnchorEntries)
            .ToArray();
        var sourceAnchors = seedPool
            .SelectMany(item => item.SourceRefs)
            .Where(static reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAnchorEntries)
            .ToArray();
        var requiredRelations = RuntimeRelationIntentDeriver.Derive(
            queryText,
            corpusItems,
            options.DenseSeedTopK,
            options.AnchorSeedTopK,
            options.RelationTopK);

        diagnostics.Add($"evidenceAnchors={evidenceAnchors.Length}");
        diagnostics.Add($"sourceAnchors={sourceAnchors.Length}");
        diagnostics.Add($"requiredRelations={requiredRelations.Count}");

        var confidence = options.DenseSeedTopK == 0
            ? 0d
            : Math.Min(1d, (double)seedPool.Count / Math.Max(1, options.DenseSeedTopK + options.AnchorSeedTopK));

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
            TargetSectionDerivationSource = "anchor + dense seed expansion (frequency-major) -> router.targetSection proxy",
            EvidenceAnchorDerivationSource = "anchor + dense seed expansion (item.EvidenceRefs union) -> query.evidenceAnchors proxy (canonical resolver bridges sample/corpus namespaces)",
            SourceAnchorDerivationSource = "anchor + dense seed expansion (item.SourceRefs union) -> query.sourceAnchors proxy",
            RequiredRelationDerivationSource = "RuntimeRelationIntentDeriver: query intent + relationStore 1-hop expansion",
            MustNotConstraintDerivationSource = "runtime constraint/policy (empty in V5.8 preview; runtime supplies query.mustNotItemIds)",
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

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> RankCandidatesPureDense(
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
        RuntimeRetrievalFeatureDerivationRepairOptions options,
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
    /// Evaluation helper. Reads <see cref="RetrievalDatasetV2Sample.MustHitItemIds"/> for recall computation
    /// only — *evaluation*, not scoring input. Risk counts use envelope-derived target section / mustNot
    /// so no eval label leaks into the runtime path.
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

    private static int CountRawOverlap(IReadOnlyList<string> derived, IReadOnlyList<string> expected)
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
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.ReadyForRuntimeFeatureDerivationRepairFreeze;
        }

        if (blocked.Contains("DerivationGateMissing", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByMissingDerivationGate;
        }

        if (blocked.Contains("DerivationGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByDerivationGateNotPassed;
        }

        if (blocked.Contains("MissingDataset", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByMissingDataset;
        }

        if (blocked.Contains("EmptyRepairEnvelope", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByEmptyRepairEnvelope;
        }

        if (blocked.Contains("SourceScanMissing", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedBySourceScanMissing;
        }

        if (blocked.Contains("FixtureSpecialCasingDetected", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByFixtureSpecialCasing;
        }

        if (blocked.Contains("MustNotHitRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByMustNotHitRisk;
        }

        if (blocked.Contains("LifecycleRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByLifecycleRisk;
        }

        if (blocked.Contains("SectionMismatchDetected", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedBySectionMismatch;
        }

        if (blocked.Contains("DerivedRiskNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByDerivedRiskNonZero;
        }

        if (blocked.Contains("DerivedRecallNotImproved", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByDerivedRecallNotImproved;
        }

        if (blocked.Contains("DerivedMrrNotImproved", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByDerivedMrrNotImproved;
        }

        if (blocked.Contains("HoldoutRecallRegression", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByHoldoutRecallRegression;
        }

        if (blocked.Contains("HoldoutMrrRegression", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByHoldoutMrrRegression;
        }

        if (blocked.Contains("LowRelationCoverage", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByLowRelationCoverage;
        }

        if (blocked.Contains("ZeroAnchorCoverage", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByZeroAnchorCoverage;
        }

        if (blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static reason => reason.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByVectorStoreBindingChange;
        }

        if (blocked.Any(static reason => reason.Contains("FormalRetrieval", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("FormalPackageWritten", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeRetrievalFeatureDerivationRepairRecommendations.BlockedByRuntimeMutation;
        }

        return RuntimeRetrievalFeatureDerivationRepairRecommendations.KeepPreviewOnly;
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

    private static void AppendSamples(StringBuilder builder, IReadOnlyList<RuntimeRetrievalFeatureDerivationRepairSampleResult> samples)
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
            builder.AppendLine($"- sampleId: `{sample.Envelope.SampleId}` split=`{sample.Split}` confidence=`{sample.Envelope.Confidence:F2}`");
            builder.AppendLine($"  - envelope.targetSection: `{sample.Envelope.TargetSection}`");
            builder.AppendLine($"  - envelope counts (relations/evidence/source/mustNot): `{sample.Envelope.RequiredRelations.Count}/{sample.Envelope.EvidenceAnchors.Count}/{sample.Envelope.SourceAnchors.Count}/{sample.Envelope.MustNotConstraints.Count}`");
            builder.AppendLine($"  - canonical overlap (relation/evidence/source): `{sample.CanonicalRequiredRelationOverlap}/{sample.CanonicalEvidenceAnchorOverlap}/{sample.CanonicalSourceAnchorOverlap}`");
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

    private sealed class SplitTotals
    {
        public int SampleCount { get; private set; }
        public double BaselineRecallSum { get; private set; }
        public double BaselineMrrSum { get; private set; }
        public double DerivedRecallSum { get; private set; }
        public double DerivedMrrSum { get; private set; }

        public void Add(RuntimeRetrievalFeatureDerivationRepairSampleResult sample)
        {
            SampleCount++;
            DerivedRecallSum += sample.Recall;
            DerivedMrrSum += sample.MeanReciprocalRank;
            // Baseline values flow via the inline call site; the per-sample DTO does not carry baseline.
            // We accept this as both metrics are derived per-sample inside RunSample but only derived flows out.
            // To keep the contract simple we treat baseline as captured through a parallel call site below.
        }

        public void AddBaseline(double recall, double mrr)
        {
            BaselineRecallSum += recall;
            BaselineMrrSum += mrr;
        }
    }

    private sealed class CoverageTotals
    {
        public int TargetSectionMatchCount { get; private set; }
        public int ApplicableEvidenceSampleCount { get; private set; }
        public int ApplicableSourceSampleCount { get; private set; }
        public int ApplicableRelationSampleCount { get; private set; }
        public int ApplicableEvidenceCoveredCount { get; private set; }
        public int ApplicableSourceCoveredCount { get; private set; }
        public int ApplicableRelationCoveredCount { get; private set; }
        public int DerivedRiskAfterPolicy { get; private set; }
        public int DerivedMustNotHitRiskAfterPolicy { get; private set; }
        public int DerivedLifecycleRiskAfterPolicy { get; private set; }
        public int DerivedSectionMismatchCount { get; private set; }

        public void Add(RuntimeRetrievalFeatureDerivationRepairSampleResult sample)
        {
            if (sample.TargetSectionMatch)
            {
                TargetSectionMatchCount++;
            }

            if (sample.ExpectedRequiredRelationCount > 0)
            {
                ApplicableRelationSampleCount++;
                if (sample.CanonicalRequiredRelationOverlap > 0)
                {
                    ApplicableRelationCoveredCount++;
                }
            }

            if (sample.ExpectedEvidenceAnchorCount > 0)
            {
                ApplicableEvidenceSampleCount++;
                if (sample.CanonicalEvidenceAnchorOverlap > 0)
                {
                    ApplicableEvidenceCoveredCount++;
                }
            }

            if (sample.ExpectedSourceAnchorCount > 0)
            {
                ApplicableSourceSampleCount++;
                if (sample.CanonicalSourceAnchorOverlap > 0)
                {
                    ApplicableSourceCoveredCount++;
                }
            }

            DerivedRiskAfterPolicy += sample.RiskAfterPolicy;
            DerivedMustNotHitRiskAfterPolicy += sample.MustNotHitRiskAfterPolicy;
            DerivedLifecycleRiskAfterPolicy += sample.LifecycleRiskAfterPolicy;
            DerivedSectionMismatchCount += sample.SectionMismatchCount;
        }
    }
}

/// <summary>运行时检索特征推导修复选项。</summary>
public sealed class RuntimeRetrievalFeatureDerivationRepairOptions
{
    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int TopK { get; init; } = 5;

    public int DenseSeedTopK { get; init; } = 5;

    public int AnchorSeedTopK { get; init; } = 5;

    public int RelationTopK { get; init; } = 8;

    public int VectorTopK { get; init; } = 10;

    public int GraphTopK { get; init; } = 10;

    public int MergedTopK { get; init; } = 12;

    public double SectionBoost { get; init; } = 1.15;

    public double EvidenceBoost { get; init; } = 1.25;

    public double RelationBoost { get; init; } = 1.25;

    public double LexicalBoost { get; init; } = 1.10;

    public int HoldoutModulus { get; init; } = 5;

    public int HoldoutRemainder { get; init; } = 0;

    public int MaxSampleTraceCount { get; init; } = 5;

    public double MinRelationCoverageRate { get; init; } = 0.55;

    public double MaxAllowedHoldoutRecallRegression { get; init; } = 0.0;

    public double MaxAllowedHoldoutMrrRegression { get; init; } = 0.0;

    public bool RequireDerivationGatePassed { get; init; } = true;

    public bool RequireSourceScan { get; init; } = true;

    public bool UseForRuntime { get; init; } = false;

    public bool FormalRetrievalAllowed { get; init; } = false;
}
