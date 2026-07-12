using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;

public static class RetrievalCandidateSourceIds
{
    public const string Dense = "dense";
    public const string Lexical = "lexical";
    public const string Anchor = "anchor";
    public const string EvidenceSource = "evidence-source";
    public const string Relation = "relation";
    public const string Metadata = "metadata";
}

public static class RetrievalEvalProtocolRecommendations
{
    public const string ReadyForSourceRepairRecheck = nameof(ReadyForSourceRepairRecheck);
    public const string NeedsSourceDiverseDataset = nameof(NeedsSourceDiverseDataset);
    public const string NeedsInputMetadataEnrichment = nameof(NeedsInputMetadataEnrichment);
    public const string BlockedByProtocolMismatch = nameof(BlockedByProtocolMismatch);
}

public sealed class RetrievalEvalProtocol
{
    public string ProtocolVersion { get; init; } = "retrieval-eval-protocol-v1";
    public int VectorTopK { get; init; } = 5;
    public int MergedTopK { get; init; } = 8;
    public int FinalTopK { get; init; } = 5;
    public double ScoreThreshold { get; init; } = 0.0;
    public string DeterministicTieBreak { get; init; } = "score_desc_source_precedence_candidate_id_ordinal";
    public string TrainSplit { get; init; } = "train";
    public string HoldoutSplit { get; init; } = "holdout";
}

public sealed class RetrievalProtocolMetricSet
{
    public string ProfileName { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int HitCount { get; init; }
    public int MustHitCount { get; init; }
    public double Recall { get; init; }
    public double Mrr { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public string Signature { get; init; } = string.Empty;
}

public sealed class CandidateSourceContributionSummary
{
    public string SourceId { get; init; } = string.Empty;
    public int CandidateCount { get; init; }
    public int UniqueCandidateCount { get; init; }
    public int UniqueMustHitRecoveryCount { get; init; }
    public double SourceRecall { get; init; }
    public double SourceMrr { get; init; }
    public double MarginalRecall { get; init; }
    public double MarginalMrr { get; init; }
    public double OverlapRateWithDense { get; init; }
    public double SourceOverlapRate { get; init; }
    public bool NonDiscriminative { get; init; }
}

public sealed class CandidateSourceDiscriminabilitySplitSummary
{
    public string Split { get; init; } = string.Empty;
    public string Difficulty { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int UniqueCandidateCount { get; init; }
    public int UniqueMustHitRecoveryCount { get; init; }
    public double MarginalRecall { get; init; }
    public double MarginalMrr { get; init; }
    public double SourceOverlapRate { get; init; }
}

public sealed class CandidateSourceDiscriminabilityAuditReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool AuditPassed { get; init; }
    public string Recommendation { get; init; } = RetrievalEvalProtocolRecommendations.BlockedByProtocolMismatch;
    public RetrievalEvalProtocol Protocol { get; init; } = new();
    public int SampleCount { get; init; }
    public int CorpusItemCount { get; init; }
    public double BaselineRecall { get; init; }
    public double BaselineMrr { get; init; }
    public double MergedRecall { get; init; }
    public double MergedMrr { get; init; }
    public IReadOnlyList<CandidateSourceContributionSummary> SourceSummaries { get; init; } = Array.Empty<CandidateSourceContributionSummary>();
    public IReadOnlyList<CandidateSourceDiscriminabilitySplitSummary> SplitSummaries { get; init; } = Array.Empty<CandidateSourceDiscriminabilitySplitSummary>();
    public double TemplateHomogeneityScore { get; init; }
    public bool TemplateHomogeneityDetected { get; init; }
    public int TemplateSignatureCount { get; init; }
    public int DuplicateTemplateSignatureCount { get; init; }
    public bool SourceNonDiscriminativeDetected { get; init; }
    public int NonDiscriminativeSourceCount { get; init; }
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
}

public sealed class RetrievalEvalProtocolGateReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = RetrievalEvalProtocolRecommendations.BlockedByProtocolMismatch;
    public RetrievalEvalProtocol Protocol { get; init; } = new();
    public bool BaselineProtocolReproducible { get; init; }
    public bool TieBreakDeterministic { get; init; }
    public int HashOrderSensitivityCount { get; init; }
    public bool EvalLabelScoringDetected { get; init; }
    public bool EvalLabelCandidateGenerationDetected { get; init; }
    public bool SourceNonDiscriminativeDetected { get; init; }
    public bool TemplateHomogeneityDetected { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
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
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class InputMetadataEnrichmentPreviewRecommendations
{
    public const string ReadyForSourceRepairRecheck = nameof(ReadyForSourceRepairRecheck);
    public const string NeedsSourceDiverseDataset = nameof(NeedsSourceDiverseDataset);
    public const string NeedsInputMetadataEnrichment = nameof(NeedsInputMetadataEnrichment);
    public const string BlockedByProtocolMismatch = nameof(BlockedByProtocolMismatch);
}

public sealed class InputMetadataCoverageSnapshot
{
    public int CorpusItemCount { get; init; }
    public int SourceRefPresentCount { get; init; }
    public int EvidenceRefPresentCount { get; init; }
    public int ProvenancePresentCount { get; init; }
    public int SourceFingerprintPresentCount { get; init; }
    public int RelationMetadataPresentCount { get; init; }
    public int LifecycleMetadataPresentCount { get; init; }
    public int CanonicalMetadataTokenCount { get; init; }
    public int QueryDerivedAnchorCount { get; init; }
    public int CoverageScore { get; init; }
}

public sealed class InputMetadataEnrichmentPreviewReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PreviewPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = InputMetadataEnrichmentPreviewRecommendations.BlockedByProtocolMismatch;
    public RetrievalEvalProtocol Protocol { get; init; } = new();
    public int CorpusItemCount { get; init; }
    public int SampleCount { get; init; }
    public InputMetadataCoverageSnapshot BeforeCoverage { get; init; } = new();
    public InputMetadataCoverageSnapshot AfterCoverage { get; init; } = new();
    public int MetadataCoverageDelta { get; init; }
    public double BeforeRecall { get; init; }
    public double AfterRecall { get; init; }
    public double RecallDelta { get; init; }
    public double BeforeMrr { get; init; }
    public double AfterMrr { get; init; }
    public double MrrDelta { get; init; }
    public double BeforeHoldoutMarginalRecall { get; init; }
    public double AfterHoldoutMarginalRecall { get; init; }
    public double HoldoutMarginalRecallDelta { get; init; }
    public double BeforeHoldoutMarginalMrr { get; init; }
    public double AfterHoldoutMarginalMrr { get; init; }
    public double HoldoutMarginalMrrDelta { get; init; }
    public IReadOnlyList<CandidateSourceContributionSummary> BeforeSourceSummaries { get; init; } = Array.Empty<CandidateSourceContributionSummary>();
    public IReadOnlyList<CandidateSourceContributionSummary> AfterSourceSummaries { get; init; } = Array.Empty<CandidateSourceContributionSummary>();
    public IReadOnlyList<CandidateSourceDiscriminabilitySplitSummary> BeforeSplitSummaries { get; init; } = Array.Empty<CandidateSourceDiscriminabilitySplitSummary>();
    public IReadOnlyList<CandidateSourceDiscriminabilitySplitSummary> AfterSplitSummaries { get; init; } = Array.Empty<CandidateSourceDiscriminabilitySplitSummary>();
    public int IndependentNonDenseSourceCount { get; init; }
    public int NonDiscriminativeSourceCount { get; init; }
    public double TemplateHomogeneityScore { get; init; }
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
    public bool RuntimeChangeGatePassed { get; init; }
    public bool V511ProtocolGatePassed { get; init; }
    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; } = new();
    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>查询驱动候选源修复 profile DTO。</summary>
public sealed class QueryDrivenCandidateSourceRepairProfile
{
    public string ProfileId { get; init; } = ""; public string ProfileLabel { get; init; } = "";
    public double Recall { get; init; } public double Mrr { get; init; }
    public int MustHitBelowTopK { get; init; } public int HitCount { get; init; } public int TotalMustHitCount { get; init; }
}

/// <summary>查询驱动候选源修复报告 DTO。</summary>
public sealed class QueryDrivenCandidateSourceRepairReport
{
    public string OperationId { get; init; } = ""; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ReportPassed { get; init; } public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = "KeepBaselineOnly";
    public string BestProfileId { get; init; } = ""; public string BestProfileLabel { get; init; } = "";
    public int TopK { get; init; } public int SampleCount { get; init; }
    public int TrainSampleCount { get; init; } public int HoldoutSampleCount { get; init; }
    public double TrainBaselineRecall { get; init; } public double TrainBaselineMrr { get; init; }
    public double TrainDerivedRecall { get; init; } public double TrainDerivedMrr { get; init; }
    public double HoldoutBaselineRecall { get; init; } public double HoldoutBaselineMrr { get; init; }
    public double HoldoutDerivedRecall { get; init; } public double HoldoutDerivedMrr { get; init; }
    public QueryDrivenCandidateSourceRepairProfile DenseBaseline { get; init; } = new();
    public QueryDrivenCandidateSourceRepairProfile DenseLexical { get; init; } = new();
    public QueryDrivenCandidateSourceRepairProfile DenseAnchors { get; init; } = new();
    public QueryDrivenCandidateSourceRepairProfile DenseRelation { get; init; } = new();
    public QueryDrivenCandidateSourceRepairProfile DenseMetadata { get; init; } = new();
    public QueryDrivenCandidateSourceRepairProfile CombinedSource { get; init; } = new();
    public double DerivedRecallDelta { get; init; } public double DerivedMrrDelta { get; init; }
    public int RiskAfterPolicy { get; init; } public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; } public int SectionMismatchCount { get; init; }
    public int ForbiddenSampleAnnotationReadCount { get; init; }
    public int FormalOutputChanged { get; init; } public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; } public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; } public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; } public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; } public bool ReadyForRuntimeSwitch { get; init; }
    public bool UseForRuntime { get; init; } public bool NoRuntimeMutationInvariant { get; init; }
    public double MaxAllowedHoldoutRecallRegression { get; init; } public double MaxAllowedHoldoutMrrRegression { get; init; }
    public double MinLexicalScore { get; init; } public double MinAnchorScore { get; init; }
    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; } = new();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

public static class SourceAwareRankingProfileIds
{
    public const string DenseBaseline = "dense-baseline";
    public const string NormalizedSource = "normalized-source";
    public const string ConfidenceGated = "confidence-gated";
    public const string DensePreserving = "dense-preserving";
    public const string CombinedSafe = "combined-safe";
}

public static class SourceAwareRankingRepairRecommendations
{
    public const string ReadyForSourceAwareRankingFreeze = nameof(ReadyForSourceAwareRankingFreeze);
    public const string BlockedByTrainDevNoImprovement = nameof(BlockedByTrainDevNoImprovement);
    public const string BlockedByHoldoutRegression = nameof(BlockedByHoldoutRegression);
    public const string BlockedByBlindHoldoutRegression = nameof(BlockedByBlindHoldoutRegression);
    public const string BlockedByDenseWinnerLoss = nameof(BlockedByDenseWinnerLoss);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByProtocolMismatch = nameof(BlockedByProtocolMismatch);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public sealed class SourceAwareRankingSplitMetrics
{
    public string Split { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public double Recall { get; init; }
    public double Mrr { get; init; }
    public double Precision { get; init; }
    public int HitCount { get; init; }
    public int MustHitCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
}

public sealed class SourceAwareBlindHoldoutManifest
{
    public string DatasetId { get; init; } = string.Empty;
    public int CorpusItemCount { get; init; }
    public int SampleCount { get; init; }
    public string Split { get; init; } = "blind-holdout";
    public int QueryLeakageCount { get; init; }
    public int ItemLeakageCount { get; init; }
    public int TemplateLeakageCount { get; init; }
    public int ContractIssueCount { get; init; }
    public string GeneratedBy { get; init; } = string.Empty;
    public bool UseForRuntime { get; init; }
}

public sealed class SourceAwareRankingProfileReport
{
    public string ProfileId { get; init; } = string.Empty;
    public string ProfileLabel { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public SourceAwareRankingSplitMetrics TrainDev { get; init; } = new() { Split = "train-dev" };
    public SourceAwareRankingSplitMetrics Test { get; init; } = new() { Split = "test" };
    public SourceAwareRankingSplitMetrics Holdout { get; init; } = new() { Split = "holdout" };
    public SourceAwareRankingSplitMetrics BlindHoldout { get; init; } = new() { Split = "blind-holdout" };
    public IReadOnlyList<SourceAwareRankingSplitMetrics> SplitMetrics { get; init; } = Array.Empty<SourceAwareRankingSplitMetrics>();
    public int DenseWinnerLostCount { get; init; }
    public int UniqueSourceRecoveryCount { get; init; }
    public int SourceNoiseCount { get; init; }
    public int FallbackCount { get; init; }
    public double FallbackRate { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
}

public sealed class SourceAwareRankingRepairReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ReportPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = SourceAwareRankingRepairRecommendations.KeepPreviewOnly;
    public string SelectedProfileId { get; init; } = string.Empty;
    public RetrievalEvalProtocol Protocol { get; init; } = new();
    public int CorpusItemCount { get; init; }
    public int SampleCount { get; init; }
    public SourceAwareBlindHoldoutManifest BlindHoldoutManifest { get; init; } = new();
    public IReadOnlyList<RetrievalDatasetV2CorpusItem> BlindHoldoutCorpusItems { get; init; } = Array.Empty<RetrievalDatasetV2CorpusItem>();
    public IReadOnlyList<RetrievalDatasetV2Sample> BlindHoldoutSamples { get; init; } = Array.Empty<RetrievalDatasetV2Sample>();
    public SourceAwareRankingProfileReport DenseBaseline { get; init; } = new();
    public SourceAwareRankingProfileReport SelectedProfile { get; init; } = new();
    public IReadOnlyList<SourceAwareRankingProfileReport> Profiles { get; init; } = Array.Empty<SourceAwareRankingProfileReport>();
    public double TrainDevRecallDelta { get; init; }
    public double TrainDevMrrDelta { get; init; }
    public double TrainDevPrecisionDelta { get; init; }
    public double TestRecallDelta { get; init; }
    public double TestMrrDelta { get; init; }
    public double TestPrecisionDelta { get; init; }
    public double HoldoutRecallDelta { get; init; }
    public double HoldoutMrrDelta { get; init; }
    public double HoldoutPrecisionDelta { get; init; }
    public double BlindHoldoutRecallDelta { get; init; }
    public double BlindHoldoutMrrDelta { get; init; }
    public double BlindHoldoutPrecisionDelta { get; init; }
    public int DenseWinnerLostCount { get; init; }
    public int UniqueSourceRecoveryCount { get; init; }
    public int SourceNoiseCount { get; init; }
    public double FallbackRate { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public bool EvalLabelScoringDetected { get; init; }
    public bool EvalLabelCandidateGenerationDetected { get; init; }
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
    public bool RuntimeChangeGatePassed { get; init; }
    public bool V511ProtocolGatePassed { get; init; }
    public bool V512EnrichmentGatePassed { get; init; }
    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; } = new();
    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class OutputTokenPriorityShadowGateRecommendations
{
    public const string ReadyForOutputPolicyShadowFreeze = nameof(ReadyForOutputPolicyShadowFreeze);
    public const string BlockedByMissingV514Gate = nameof(BlockedByMissingV514Gate);
    public const string BlockedByTokenBudget = nameof(BlockedByTokenBudget);
    public const string BlockedByPriorityInversion = nameof(BlockedByPriorityInversion);
    public const string BlockedByCoverageRegression = nameof(BlockedByCoverageRegression);
    public const string BlockedByDroppedRequiredCandidate = nameof(BlockedByDroppedRequiredCandidate);
    public const string BlockedBySectionMismatch = nameof(BlockedBySectionMismatch);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public sealed class OutputTokenPrioritySectionSummary
{
    public string Section { get; init; } = string.Empty;
    public int ItemCount { get; init; }
    public int TokenTotal { get; init; }
    public int TokenMax { get; init; }
}

public sealed class OutputTokenPriorityShadowGateReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ShadowPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = OutputTokenPriorityShadowGateRecommendations.KeepPreviewOnly;
    public string ProfileName { get; init; } = SourceAwareRankingProfileIds.CombinedSafe;
    public RetrievalEvalProtocol Protocol { get; init; } = new();
    public int CorpusItemCount { get; init; }
    public int SampleCount { get; init; }
    public int BlindHoldoutSampleCount { get; init; }
    public int BaselinePackageCount { get; init; }
    public int ShadowPackageCount { get; init; }
    public int BaselineTokenTotal { get; init; }
    public int ShadowTokenTotal { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public int TokenDeltaP95 { get; init; }
    public int TokenBudgetLimit { get; init; }
    public int PerPackageTokenBudgetLimit { get; init; }
    public int SectionTokenBudgetLimit { get; init; }
    public int TokenBudgetExceededCount { get; init; }
    public int SectionBudgetExceededCount { get; init; }
    public int PriorityDeltaCount { get; init; }
    public int PriorityInversionCount { get; init; }
    public double MandatoryCoverageBaseline { get; init; }
    public double MandatoryCoverageShadow { get; init; }
    public double MandatoryCoverageDelta { get; init; }
    public double HardConstraintCoverageBaseline { get; init; }
    public double HardConstraintCoverageShadow { get; init; }
    public double HardConstraintCoverageDelta { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool UseForRuntime { get; init; }
    public bool V514GatePassed { get; init; }
    public bool V511ProtocolGatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; } = new();
    public IReadOnlyList<OutputTokenPrioritySectionSummary> BaselineSectionSummaries { get; init; } = Array.Empty<OutputTokenPrioritySectionSummary>();
    public IReadOnlyList<OutputTokenPrioritySectionSummary> ShadowSectionSummaries { get; init; } = Array.Empty<OutputTokenPrioritySectionSummary>();
    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class FormalAdapterInputContractRecommendations
{
    public const string ReadyForFormalAdapterInputContractFreeze = nameof(ReadyForFormalAdapterInputContractFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByContractForbiddenField = nameof(BlockedByContractForbiddenField);
    public const string BlockedByFormalSourceForbiddenRead = nameof(BlockedByFormalSourceForbiddenRead);
    public const string BlockedByMissingPrerequisiteGate = nameof(BlockedByMissingPrerequisiteGate);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
}

public sealed class FormalAdapterInputContractField
{
    public string FieldId { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string RuntimeSource { get; init; } = string.Empty;

    public string AllowedUsage { get; init; } = string.Empty;

    public bool Required { get; init; }
}

public sealed class FormalAdapterDeniedInputField
{
    public string FieldId { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}

public sealed class FormalAdapterInputContractSourceHit
{
    public string FilePath { get; init; } = string.Empty;

    public string Token { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public bool IsFormalSource { get; init; }
}

public sealed class FormalAdapterInputContractSourceScan
{
    public bool ScanPerformed { get; init; }

    public int FormalSourceFileCount { get; init; }

    public int EvalOnlySourceFileCount { get; init; }

    public int FormalSourceForbiddenReadCount { get; init; }

    public int EvalOnlyForbiddenReadCount { get; init; }

    public IReadOnlyList<string> FormalSourceFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalOnlySourceFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<FormalAdapterInputContractSourceHit> Hits { get; init; } =
        Array.Empty<FormalAdapterInputContractSourceHit>();
}

public sealed class FormalAdapterInputContractReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ContractPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = FormalAdapterInputContractRecommendations.KeepPreviewOnly;

    public string ContractVersion { get; init; } = "formal-adapter-input-contract-v1";

    public string AllowedMode { get; init; } = "ContractOnly";

    public string RequiredNextPhase { get; init; } = "FormalAdapterImplementationPreflight";

    public IReadOnlyList<string> RuntimeInputTypes { get; init; } = Array.Empty<string>();

    public int RuntimeInputTypeCount { get; init; }

    public int RuntimeInputFieldCount { get; init; }

    public int DeniedFieldCount { get; init; }

    public IReadOnlyList<FormalAdapterInputContractField> AllowedRuntimeInputs { get; init; } =
        Array.Empty<FormalAdapterInputContractField>();

    public IReadOnlyList<FormalAdapterDeniedInputField> DeniedInputs { get; init; } =
        Array.Empty<FormalAdapterDeniedInputField>();

    public int ContractForbiddenPropertyCount { get; init; }

    public IReadOnlyList<string> ContractForbiddenProperties { get; init; } = Array.Empty<string>();

    public int FormalSourceForbiddenReadCount { get; init; }

    public int EvalOnlyForbiddenReadCount { get; init; }

    public bool DatasetEvalFieldsBlocked { get; init; }

    public bool GoldLabelsBlocked { get; init; }

    public bool SampleMetadataBlocked { get; init; }

    public bool ShadowArtifactFieldsBlocked { get; init; }

    public bool CurrentShadowAdapterEvalOnly { get; init; }

    public bool V51PlanGatePassed { get; init; }

    public bool V515OutputPolicyGatePassed { get; init; }

    public bool RuntimeChangeGatePassed { get; init; }

    public FormalAdapterInputContractSourceScan SourceScan { get; init; } = new();

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class SourceDiverseShadowAdapterValidationRecommendations
{
    public const string ReadyForAdapterDeltaDecision = nameof(ReadyForAdapterDeltaDecision);
    public const string NeedsSourceDiverseValidationSet = nameof(NeedsSourceDiverseValidationSet);
    public const string BlockedByMissingV65Gate = nameof(BlockedByMissingV65Gate);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public sealed class SourceDiverseShadowAdapterValidationSampleResult
{
    public string SampleId { get; init; } = string.Empty;
    public string Split { get; init; } = string.Empty;
    public string Difficulty { get; init; } = string.Empty;
    public IReadOnlyList<string> BaselineTopK { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ShadowExpandedPool { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ShadowFinalTopK { get; init; } = Array.Empty<string>();
    public int ShadowOnlyCount { get; init; }
    public int HypotheticalAddCount { get; init; }
    public int HypotheticalRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int UniqueSourceRecoveryCount { get; init; }
    public int TokenDelta { get; init; }
    public bool SectionDelta { get; init; }
}

public sealed class SourceDiverseShadowAdapterValidationReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ValidationPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = SourceDiverseShadowAdapterValidationRecommendations.KeepPreviewOnly;
    public bool V65GatePassed { get; init; }
    public bool ValidationSetSourceDiverse { get; init; }
    public bool AllowlistedScopeMetadataPresent { get; init; }
    public string WorkspaceId { get; init; } = string.Empty;
    public string CollectionId { get; init; } = string.Empty;
    public string EvalScope { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int CorpusItemCount { get; init; }
    public int BaselineCandidateCount { get; init; }
    public int ShadowExpandedCandidateCount { get; init; }
    public int ShadowFinalCandidateCount { get; init; }
    public int OverlapCount { get; init; }
    public double OverlapRate { get; init; }
    public int ShadowOnlyCount { get; init; }
    public int HypotheticalAddCount { get; init; }
    public int HypotheticalRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int UniqueSourceRecoveryCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public int SectionDeltaCount { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool SourceScanClean { get; init; } = true;
    public IReadOnlyList<string> SourceScanFindings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SourceDiverseShadowAdapterValidationSampleResult> SampleResults { get; init; } =
        Array.Empty<SourceDiverseShadowAdapterValidationSampleResult>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ShadowCandidateMergePreviewRecommendations
{
    public const string ReadyForShadowMergeObservation = nameof(ReadyForShadowMergeObservation);
    public const string BlockedByMissingV66Gate = nameof(BlockedByMissingV66Gate);
    public const string BlockedByPreviewDeltaMissing = nameof(BlockedByPreviewDeltaMissing);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByPackageInvariant = nameof(BlockedByPackageInvariant);
    public const string BlockedByTokenBudget = nameof(BlockedByTokenBudget);
    public const string BlockedByPriorityOrSection = nameof(BlockedByPriorityOrSection);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public sealed class ShadowCandidateMergePreviewSampleResult
{
    public string SampleId { get; init; } = string.Empty;
    public string Split { get; init; } = string.Empty;
    public string Difficulty { get; init; } = string.Empty;
    public IReadOnlyList<string> BaselineCandidates { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ShadowAdapterCandidates { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MergedPreviewCandidates { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PreviewAddCandidateIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PreviewRemoveCandidateIds { get; init; } = Array.Empty<string>();
    public int TokenDelta { get; init; }
    public bool SectionMismatch { get; init; }
    public bool PriorityOrderChanged { get; init; }
    public int PriorityInversionCount { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
}

public sealed class ShadowCandidateMergePreviewReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PreviewPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ShadowCandidateMergePreviewRecommendations.KeepPreviewOnly;
    public bool V66GatePassed { get; init; }
    public bool PreviewMergedSetGenerated { get; init; }
    public int SampleCount { get; init; }
    public int BaselineCandidateCount { get; init; }
    public int ShadowAdapterCandidateCount { get; init; }
    public int MergedPreviewCandidateCount { get; init; }
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public bool TokenDeltaWithinBudget { get; init; }
    public int PriorityOrderDeltaCount { get; init; }
    public int PriorityInversionCount { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<ShadowCandidateMergePreviewSampleResult> SampleResults { get; init; } = Array.Empty<ShadowCandidateMergePreviewSampleResult>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ShadowCandidateMergePreviewObservationRecommendations
{
    public const string ReadyForShadowMergeStabilityFreeze = nameof(ReadyForShadowMergeStabilityFreeze);
    public const string NeedsMoreObservation = nameof(NeedsMoreObservation);
    public const string BlockedByMissingV67Gate = nameof(BlockedByMissingV67Gate);
    public const string BlockedByInstability = nameof(BlockedByInstability);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByPriorityOrSection = nameof(BlockedByPriorityOrSection);
    public const string BlockedByTokenBudget = nameof(BlockedByTokenBudget);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public sealed class ShadowCandidateMergePreviewObservationRunResult
{
    public int RunIndex { get; init; }
    public bool PreviewPassed { get; init; }
    public bool GatePassed { get; init; }
    public string StableSignature { get; init; } = string.Empty;
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public int PriorityInversionCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class ShadowCandidateMergePreviewObservationReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ObservationPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ShadowCandidateMergePreviewObservationRecommendations.KeepPreviewOnly;
    public bool V67GatePassed { get; init; }
    public int ObservationRunCount { get; init; }
    public int MinimumObservationRunCount { get; init; }
    public int SampleObservationCount { get; init; }
    public int FailedRunCount { get; init; }
    public int DistinctStableSignatureCount { get; init; }
    public bool DeterministicPreviewStable { get; init; }
    public bool PreviewAddRemoveStable { get; init; }
    public int PreviewAddCountMin { get; init; }
    public int PreviewAddCountMax { get; init; }
    public int PreviewRemoveCountMin { get; init; }
    public int PreviewRemoveCountMax { get; init; }
    public int PreviewAddCountTotal { get; init; }
    public int PreviewRemoveCountTotal { get; init; }
    public int AppliedAddCountMax { get; init; }
    public int AppliedRemoveCountMax { get; init; }
    public int RiskAfterPolicyMax { get; init; }
    public int MustNotHitRiskAfterPolicyMax { get; init; }
    public int LifecycleRiskAfterPolicyMax { get; init; }
    public int TokenDeltaTotalMax { get; init; }
    public int TokenDeltaMaxMax { get; init; }
    public bool TokenDeltaWithinBudget { get; init; }
    public int PriorityInversionCountTotal { get; init; }
    public int SectionMismatchCountTotal { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChangedMax { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<ShadowCandidateMergePreviewObservationRunResult> Runs { get; init; } = Array.Empty<ShadowCandidateMergePreviewObservationRunResult>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ShadowMergeStabilityFreezeRecommendations
{
    public const string ReadyForShadowMergePromotionDecision = nameof(ReadyForShadowMergePromotionDecision);
    public const string ReadyForControlledMergeProposal = nameof(ReadyForControlledMergeProposal);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByInstability = nameof(BlockedByInstability);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByPriorityOrSection = nameof(BlockedByPriorityOrSection);
    public const string BlockedByTokenBudget = nameof(BlockedByTokenBudget);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public static class ShadowMergePromotionDecisions
{
    public const string ReadyForControlledMergeProposal = nameof(ReadyForControlledMergeProposal);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public sealed class ShadowMergeStabilityFreezeReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public bool PromotionDecisionPassed { get; init; }
    public string Recommendation { get; init; } = ShadowMergeStabilityFreezeRecommendations.KeepPreviewOnly;
    public string PromotionDecision { get; init; } = ShadowMergePromotionDecisions.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";
    public string AllowedMode { get; init; } = "PreviewMergeFreezeOnly";
    public bool V66GatePassed { get; init; }
    public bool V67GatePassed { get; init; }
    public bool ObservationGatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public int ObservationRunCount { get; init; }
    public int SampleObservationCount { get; init; }
    public bool DeterministicPreviewStable { get; init; }
    public int DistinctStableSignatureCount { get; init; }
    public bool PreviewAddRemoveStable { get; init; }
    public int PreviewAddCountMin { get; init; }
    public int PreviewAddCountMax { get; init; }
    public int PreviewRemoveCountMin { get; init; }
    public int PreviewRemoveCountMax { get; init; }
    public int AppliedAddCountMax { get; init; }
    public int AppliedRemoveCountMax { get; init; }
    public int RiskAfterPolicyMax { get; init; }
    public int MustNotHitRiskAfterPolicyMax { get; init; }
    public int LifecycleRiskAfterPolicyMax { get; init; }
    public int TokenDeltaTotalMax { get; init; }
    public int TokenDeltaMaxMax { get; init; }
    public int PriorityInversionCountTotal { get; init; }
    public int SectionMismatchCountTotal { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChangedMax { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ControlledShadowMergeProposalModes
{
    public const string ProposalOnly = nameof(ProposalOnly);
}

public static class ControlledShadowMergeProposalRecommendations
{
    public const string ReadyForControlledMergePreviewPlan = nameof(ReadyForControlledMergePreviewPlan);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByMissingLimit = nameof(BlockedByMissingLimit);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingObservationPlan = nameof(BlockedByMissingObservationPlan);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public sealed class ControlledShadowMergeProposalReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ProposalPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ControlledShadowMergeProposalRecommendations.KeepPreviewOnly;
    public string ProposalId { get; init; } = string.Empty;
    public string Mode { get; init; } = ControlledShadowMergeProposalModes.ProposalOnly;
    public string AllowedMode { get; init; } = "PreviewOnly";
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";
    public bool V66GatePassed { get; init; }
    public bool V67GatePassed { get; init; }
    public bool ObservationGatePassed { get; init; }
    public bool PromotionDecisionPassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();
    public int ScopeCount { get; init; }
    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();
    public string ProfileName { get; init; } = string.Empty;
    public int MaxRequestCount { get; init; }
    public int MaxDurationMinutes { get; init; }
    public int MaxErrorCount { get; init; }
    public int MaxPreviewAddCount { get; init; }
    public int MaxPreviewRemoveCount { get; init; }
    public int MaxTokenDeltaTotal { get; init; }
    public int MaxTokenDeltaPerSample { get; init; }
    public int MinObservationRunCount { get; init; }
    public int MinSampleObservationCount { get; init; }
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public string RollbackPlan { get; init; } = string.Empty;
    public string KillSwitchPlan { get; init; } = string.Empty;
    public bool RollbackPlanPresent { get; init; }
    public bool KillSwitchPlanPresent { get; init; }
    public IReadOnlyDictionary<string, string> RequiredGateSummary { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> ScopeConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LimitConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GateConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RollbackConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KillSwitchConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ObservationConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ControlledShadowMergeDryRunGateRecommendations
{
    public const string ReadyForControlledShadowMergeObservation = nameof(ReadyForControlledShadowMergeObservation);
    public const string BlockedByMissingProposal = nameof(BlockedByMissingProposal);
    public const string BlockedByConstraintViolation = nameof(BlockedByConstraintViolation);
    public const string BlockedByAddRemoveLimit = nameof(BlockedByAddRemoveLimit);
    public const string BlockedByTokenSectionPriority = nameof(BlockedByTokenSectionPriority);
    public const string BlockedByRollbackOrKillSwitch = nameof(BlockedByRollbackOrKillSwitch);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public sealed class ControlledShadowMergeDryRunGateReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool DryRunPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ControlledShadowMergeDryRunGateRecommendations.KeepPreviewOnly;
    public string ProposalId { get; init; } = string.Empty;
    public bool ProposalGatePassed { get; init; }
    public bool ProposalConstraintsApplied { get; init; }
    public int ScopeCount { get; init; }
    public int RequestCount { get; init; }
    public int MaxRequestCount { get; init; }
    public int DurationMinutes { get; init; }
    public int MaxDurationMinutes { get; init; }
    public int ErrorCount { get; init; }
    public int MaxErrorCount { get; init; }
    public bool RequestDurationErrorLimitEnforced { get; init; }
    public int ObservationRunCount { get; init; }
    public int MinObservationRunCount { get; init; }
    public int SampleObservationCount { get; init; }
    public int MinSampleObservationCount { get; init; }
    public bool ObservationWindowLimitEnforced { get; init; }
    public int ObservationConditionCount { get; init; }
    public int StopConditionCount { get; init; }
    public bool ObservationPlanConstraintPresent { get; init; }
    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();
    public bool DryRunPreviewGenerated { get; init; }
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public int MaxPreviewAddCount { get; init; }
    public int MaxPreviewRemoveCount { get; init; }
    public bool AddRemoveLimitEnforced { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public int MaxTokenDeltaTotal { get; init; }
    public int MaxTokenDeltaPerSample { get; init; }
    public bool TokenSectionPriorityGatePassed { get; init; }
    public int PriorityInversionCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
    public bool RollbackPlanPresent { get; init; }
    public bool RollbackVerified { get; init; }
    public bool KillSwitchAvailable { get; init; }
    public bool KillSwitchVerified { get; init; }
    public bool KillSwitchTriggered { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public IReadOnlyDictionary<string, string> ConstraintChecks { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ControlledShadowMergeObservationWindowRecommendations
{
    public const string ReadyForControlledShadowMergeObservationFreeze = nameof(ReadyForControlledShadowMergeObservationFreeze);
    public const string BlockedByMissingDryRunGate = nameof(BlockedByMissingDryRunGate);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByConstraintViolation = nameof(BlockedByConstraintViolation);
    public const string BlockedByInstability = nameof(BlockedByInstability);
    public const string BlockedByTokenSectionPriority = nameof(BlockedByTokenSectionPriority);
    public const string BlockedByRollbackOrKillSwitch = nameof(BlockedByRollbackOrKillSwitch);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public sealed class ControlledShadowMergeObservationWindowRunResult
{
    public int RunIndex { get; init; }
    public bool DryRunPassed { get; init; }
    public bool GatePassed { get; init; }
    public string StableSignature { get; init; } = string.Empty;
    public int RequestCount { get; init; }
    public bool ProposalConstraintsApplied { get; init; }
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public int PriorityInversionCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool RollbackVerified { get; init; }
    public bool KillSwitchVerified { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class ControlledShadowMergeObservationWindowReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ObservationPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ControlledShadowMergeObservationWindowRecommendations.KeepPreviewOnly;
    public string ProposalId { get; init; } = string.Empty;
    public bool ProposalGatePassed { get; init; }
    public bool DryRunGatePassed { get; init; }
    public bool ProposalConstraintsApplied { get; init; }
    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();
    public int ObservationRunCount { get; init; }
    public int MinObservationRunCount { get; init; }
    public int FailedRunCount { get; init; }
    public int RequestCountTotal { get; init; }
    public int MaxRequestCount { get; init; }
    public int DurationMinutes { get; init; }
    public int MaxDurationMinutes { get; init; }
    public int ErrorCount { get; init; }
    public int MaxErrorCount { get; init; }
    public bool RequestDurationErrorWindowEnforced { get; init; }
    public int SampleObservationCount { get; init; }
    public int MinSampleObservationCount { get; init; }
    public bool ObservationWindowLimitEnforced { get; init; }
    public int DistinctStableSignatureCount { get; init; }
    public bool DeterministicDryRunStable { get; init; }
    public bool PreviewAddRemoveStable { get; init; }
    public int PreviewAddCountMin { get; init; }
    public int PreviewAddCountMax { get; init; }
    public int PreviewAddCountTotal { get; init; }
    public int PreviewRemoveCountMin { get; init; }
    public int PreviewRemoveCountMax { get; init; }
    public int PreviewRemoveCountTotal { get; init; }
    public int AppliedAddCountMax { get; init; }
    public int AppliedRemoveCountMax { get; init; }
    public bool AppliedDeltaZero { get; init; }
    public int RiskAfterPolicyMax { get; init; }
    public int MustNotHitRiskAfterPolicyMax { get; init; }
    public int LifecycleRiskAfterPolicyMax { get; init; }
    public int TokenDeltaTotalMax { get; init; }
    public int TokenDeltaMaxMax { get; init; }
    public bool TokenDeltaWithinBudget { get; init; }
    public int PriorityInversionCountTotal { get; init; }
    public int SectionMismatchCountTotal { get; init; }
    public int DroppedRequiredCandidateCountTotal { get; init; }
    public bool RollbackVerified { get; init; }
    public bool KillSwitchVerified { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChangedMax { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<ControlledShadowMergeObservationWindowRunResult> Runs { get; init; } = Array.Empty<ControlledShadowMergeObservationWindowRunResult>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ControlledShadowMergeFreezeRecommendations
{
    public const string ReadyForControlledShadowMergePromotionDecision = nameof(ReadyForControlledShadowMergePromotionDecision);
    public const string ReadyForControlledAppliedMergeProposal = nameof(ReadyForControlledAppliedMergeProposal);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByInstability = nameof(BlockedByInstability);
    public const string BlockedByConstraintViolation = nameof(BlockedByConstraintViolation);
    public const string BlockedByTokenSectionPriority = nameof(BlockedByTokenSectionPriority);
    public const string BlockedByRollbackOrKillSwitch = nameof(BlockedByRollbackOrKillSwitch);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public static class ControlledShadowMergePromotionDecisions
{
    public const string ReadyForControlledAppliedMergeProposal = nameof(ReadyForControlledAppliedMergeProposal);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public sealed class ControlledShadowMergeFreezeReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public bool PromotionDecisionPassed { get; init; }
    public string Recommendation { get; init; } = ControlledShadowMergeFreezeRecommendations.KeepPreviewOnly;
    public string PromotionDecision { get; init; } = ControlledShadowMergePromotionDecisions.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";
    public string AllowedMode { get; init; } = "ControlledShadowMergeFreezeOnly";
    public string ProposalId { get; init; } = string.Empty;
    public bool ObservationWindowGatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool ProposalConstraintsApplied { get; init; }
    public int ObservationRunCount { get; init; }
    public int MinObservationRunCount { get; init; }
    public int RequestCountTotal { get; init; }
    public int MaxRequestCount { get; init; }
    public bool RequestDurationErrorWindowEnforced { get; init; }
    public int SampleObservationCount { get; init; }
    public int MinSampleObservationCount { get; init; }
    public bool ObservationWindowLimitEnforced { get; init; }
    public bool DeterministicDryRunStable { get; init; }
    public int DistinctStableSignatureCount { get; init; }
    public bool PreviewAddRemoveStable { get; init; }
    public int PreviewAddCountMin { get; init; }
    public int PreviewAddCountMax { get; init; }
    public int PreviewRemoveCountMin { get; init; }
    public int PreviewRemoveCountMax { get; init; }
    public int AppliedAddCountMax { get; init; }
    public int AppliedRemoveCountMax { get; init; }
    public int RiskAfterPolicyMax { get; init; }
    public int MustNotHitRiskAfterPolicyMax { get; init; }
    public int LifecycleRiskAfterPolicyMax { get; init; }
    public int TokenDeltaTotalMax { get; init; }
    public int TokenDeltaMaxMax { get; init; }
    public int PriorityInversionCountTotal { get; init; }
    public int SectionMismatchCountTotal { get; init; }
    public int DroppedRequiredCandidateCountTotal { get; init; }
    public bool RollbackVerified { get; init; }
    public bool KillSwitchVerified { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChangedMax { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ControlledAppliedMergeProposalModes
{
    public const string ProposalOnly = nameof(ProposalOnly);
}

public static class ControlledAppliedMergeApprovalModes
{
    public const string ControlledAppliedMergePreview = nameof(ControlledAppliedMergePreview);
}

public static class ControlledAppliedMergeProposalRecommendations
{
    public const string ReadyForControlledAppliedMergeDryRunGate = nameof(ReadyForControlledAppliedMergeDryRunGate);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingPromotionDecision = nameof(BlockedByMissingPromotionDecision);
    public const string BlockedByMissingLimit = nameof(BlockedByMissingLimit);
    public const string BlockedByMissingApprovalPlan = nameof(BlockedByMissingApprovalPlan);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingObservationPlan = nameof(BlockedByMissingObservationPlan);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public sealed class ControlledAppliedMergeProposalReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ProposalPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ControlledAppliedMergeProposalRecommendations.KeepPreviewOnly;
    public string ProposalId { get; init; } = string.Empty;
    public string Mode { get; init; } = ControlledAppliedMergeProposalModes.ProposalOnly;
    public string AllowedMode { get; init; } = "KeepPreviewOnly";
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";
    public string RequiredPreviousPhase { get; init; } = string.Empty;
    public string RequiredApprovalMode { get; init; } = ControlledAppliedMergeApprovalModes.ControlledAppliedMergePreview;
    public bool PromotionDecisionGatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();
    public int ScopeCount { get; init; }
    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();
    public string ProfileName { get; init; } = string.Empty;
    public int MaxRequestCount { get; init; }
    public int MaxDurationMinutes { get; init; }
    public int MaxErrorCount { get; init; }
    public int MaxAppliedAddCount { get; init; }
    public int MaxAppliedRemoveCount { get; init; }
    public int MaxTokenDeltaTotal { get; init; }
    public int MaxTokenDeltaPerSample { get; init; }
    public int MinObservationRunCount { get; init; }
    public int MinSampleObservationCount { get; init; }
    public int StablePreviewAddCount { get; init; }
    public int StablePreviewRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool AppliedMergeAllowed { get; init; }
    public bool FormalSelectedSetChangeAllowed { get; init; }
    public bool FormalPackageWriteAllowed { get; init; }
    public bool PackingPolicyMutationAllowed { get; init; }
    public bool PackageOutputMutationAllowed { get; init; }
    public bool RuntimeMutationAllowed { get; init; }
    public bool VectorStoreBindingMutationAllowed { get; init; }
    public bool ManualApprovalRequired { get; init; }
    public bool ApprovalPlanPresent { get; init; }
    public string RollbackPlan { get; init; } = string.Empty;
    public string KillSwitchPlan { get; init; } = string.Empty;
    public bool RollbackPlanPresent { get; init; }
    public bool KillSwitchPlanPresent { get; init; }
    public IReadOnlyDictionary<string, string> RequiredGateSummary { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> ScopeConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LimitConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ApprovalConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RollbackConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KillSwitchConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ObservationConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class FormalRetrievalIntegrationDecisions
{
    public const string ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan =
        nameof(ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan);

    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public static class FormalRetrievalIntegrationDecisionRecommendations
{
    public const string ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan =
        nameof(ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan);

    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingV5Gate = nameof(BlockedByMissingV5Gate);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOrPolicyChange = nameof(BlockedByPackageOrPolicyChange);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
}

public sealed class FormalRetrievalIntegrationDecisionGateStatus
{
    public string GateId { get; init; } = string.Empty;

    public bool Passed { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public string SupersededBy { get; init; } = string.Empty;
}

public sealed class FormalRetrievalIntegrationDecisionReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool DecisionPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = FormalRetrievalIntegrationDecisionRecommendations.KeepPreviewOnly;

    public string IntegrationDecision { get; init; } = FormalRetrievalIntegrationDecisions.KeepPreviewOnly;

    public string CurrentOverallStatus { get; init; } = string.Empty;

    public string AllowedMode { get; init; } = "DecisionOnly";

    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool ReadyForFormalRetrievalIntegrationFreeze { get; init; }

    public bool ReadyForAdapterNoOpBindingPlan { get; init; }

    public bool AdapterNoOpBindingPlanAllowed { get; init; }

    public bool FormalRetrievalIntegrationFreezeAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalVectorStoreBindingAllowed { get; init; }

    public bool FormalPackageWriteAllowed { get; init; }

    public bool PackingPolicyIntegrationAllowed { get; init; }

    public bool PackageOutputMutationAllowed { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool RuntimeChangeGatePassed { get; init; }

    public bool P15GatePassed { get; init; }

    public bool V50ProjectStateAuditPassed { get; init; }

    public bool V50FormalIntegrationPlanGatePassed { get; init; }

    public bool V51ShadowAdapterPlanGatePassed { get; init; }

    public bool V511RetrievalEvalProtocolGatePassed { get; init; }

    public bool V512InputMetadataEnrichmentGatePassed { get; init; }

    public bool V513EnrichedSourceRepairGatePassed { get; init; }

    public bool V514SourceAwareRankingGatePassed { get; init; }

    public bool V515OutputTokenPriorityGatePassed { get; init; }

    public bool V516AdapterInputContractGatePassed { get; init; }

    public IReadOnlyList<FormalRetrievalIntegrationDecisionGateStatus> Gates { get; init; } =
        Array.Empty<FormalRetrievalIntegrationDecisionGateStatus>();

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>正式检索集成冻结报告。</summary>
public sealed class FormalRetrievalIntegrationFreezeReport
{
    public string OperationId { get; init; } = ""; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public string Recommendation { get; init; } = "KeepPreviewOnly";
    public bool FormalRetrievalAllowed { get; init; } public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; } public bool UseForRuntime { get; init; }
    public bool PackageOutputChanged { get; init; } public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; } public bool VectorStoreBindingChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public string SelectedProfile { get; init; } = "combined-safe";
    public string EvalProtocol { get; init; } = "V5.11";
    public string InputContract { get; init; } = "formal-adapter-input-contract-v1";
    public string OutputPolicyShadowGate { get; init; } = "V5.15 passed";
    public string IntegrationDecision { get; init; } = "V5.17 passed";
    public IReadOnlyList<string> FrozenArtifactPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class ShadowAdapterDeltaDiagnosticsReport
{
    public string OperationId { get; init; } = ""; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool DiagnosticsPassed { get; init; }
    public string Recommendations { get; init; } = "Blocked";
    public int SampleCount { get; init; }
    public int BaselinePoolSize { get; init; } public int ShadowPoolSize { get; init; }
    public int OverlapCount { get; init; } public double OverlapRate { get; init; }
    public int BaselineOnlyCount { get; init; } public int ShadowOnlyCount { get; init; }
    public int FilteredByEligibilityCount { get; init; } public int FilteredByLifecycleCount { get; init; }
    public int FilteredByBelowTopKCount { get; init; } public int FilteredByDuplicateCount { get; init; }
    public IReadOnlyList<string> DeltaZeroCauses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}
