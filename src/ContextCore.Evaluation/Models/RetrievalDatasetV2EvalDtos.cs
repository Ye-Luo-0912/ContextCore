using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;

/// <summary>retrieval dataset / query-corpus alignment audit 推荐结论。</summary>
public static class RetrievalDatasetAlignmentRecommendations
{
    public const string ReadyForRecallSourceRepair = nameof(ReadyForRecallSourceRepair);
    public const string NeedsCorpusBackfill = nameof(NeedsCorpusBackfill);
    public const string NeedsAnchorMetadataBackfill = nameof(NeedsAnchorMetadataBackfill);
    public const string NeedsQueryNormalizationRepair = nameof(NeedsQueryNormalizationRepair);
    public const string NeedsProviderScopeRepair = nameof(NeedsProviderScopeRepair);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>单条 query-corpus alignment 诊断记录；不进入 retrieval policy。</summary>
public sealed class RetrievalDatasetAlignmentIssue
{
    public string DatasetName { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string IssueType { get; init; } = RetrievalDatasetAlignmentIssueTypes.Unknown;

    public string QueryText { get; init; } = string.Empty;

    public IReadOnlyList<string> QueryTokens { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CorpusOverlapTokens { get; init; } = Array.Empty<string>();

    public string SourceKind { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceTags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string Notes { get; init; } = string.Empty;
}


/// <summary>retrieval dataset / query-corpus alignment audit 报告；只读评估，不改变正式检索。</summary>
public sealed class RetrievalDatasetAlignmentAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetName { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustNotCount { get; init; }

    public int MustHitPresentInCorpusCount { get; init; }

    public int MustHitMissingFromCorpusCount { get; init; }

    public int MustHitPresentInProviderScopeCount { get; init; }

    public int MustHitBlockedByEligibilityCount { get; init; }

    public double QueryTokenCoverageAverage { get; init; }

    public double QueryCorpusTokenOverlapAverage { get; init; }

    public double AnchorCoverageRate { get; init; }

    public double SourceKindCoverageRate { get; init; }

    public int CorpusEntryCount { get; init; }

    public int ProviderScopedEntryCount { get; init; }

    public int AlignmentIssueCount { get; init; }

    public IReadOnlyDictionary<string, int> IssueBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RetrievalDatasetAlignmentIssue> Issues { get; init; } =
        Array.Empty<RetrievalDatasetAlignmentIssue>();

    public string Recommendation { get; init; } = RetrievalDatasetAlignmentRecommendations.KeepPreviewOnly;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>eligibility recall loss triage 推荐结论。</summary>
public static class VectorEligibilityRecallLossTriageRecommendations
{
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string ReadyForSectionRoutedRecallRepair = nameof(ReadyForSectionRoutedRecallRepair);
    public const string NeedsMetadataRepair = nameof(NeedsMetadataRepair);
    public const string NeedsEvalExpectationReview = nameof(NeedsEvalExpectationReview);
    public const string UnsafeToRecover = nameof(UnsafeToRecover);
}


/// <summary>单个数据集的 lifecycle-filtered mustHit triage 报告。</summary>
public sealed class VectorEligibilityRecallLossTriageReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetName { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public int SampleCount { get; init; }

    public int TotalFilteredMustHit { get; init; }

    public int CorrectlyBlockedCount { get; init; }

    public int RouteToHistoricalCount { get; init; }

    public int RouteToAuditCount { get; init; }

    public int MetadataRepairNeededCount { get; init; }

    public int EvalExpectationReviewNeededCount { get; init; }

    public int UnsafeToRecoverCount { get; init; }

    public int RecoverableWithoutNormalContextCount { get; init; }

    public int RecoverableToNormalContextCount { get; init; }

    public string Recommendation { get; init; } = VectorEligibilityRecallLossTriageRecommendations.KeepPreviewOnly;

    public IReadOnlyDictionary<string, int> CategoryBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VectorEligibilityRecallLossTriageDetail> Details { get; init; } =
        Array.Empty<VectorEligibilityRecallLossTriageDetail>();
}


public sealed class RetrievalDatasetV2MetadataContractReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public IReadOnlyList<string> CorpusItemRequiredFields { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> QuerySampleRequiredFields { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRules { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TargetSectionRules { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RelationEvidenceRules { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SplitIsolationRules { get; init; } = Array.Empty<string>();

    public bool GeneratesFormalDataset { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = "ReadyForDatasetV2Authoring";
}


/// <summary>Retrieval Dataset V2 validator recommendation。</summary>
public static class RetrievalDatasetV2ValidationRecommendations
{
    public const string ReadyForDatasetV2Authoring = nameof(ReadyForDatasetV2Authoring);
    public const string NeedsIngestionMetadataBackfill = nameof(NeedsIngestionMetadataBackfill);
    public const string NeedsRelationEvidenceBackfill = nameof(NeedsRelationEvidenceBackfill);
    public const string NeedsQueryLabelHygiene = nameof(NeedsQueryLabelHygiene);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Retrieval Dataset V2 validation 单条问题。</summary>
public sealed class RetrievalDatasetV2ValidationIssue
{
    public string IssueType { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string Split { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}


/// <summary>Retrieval Dataset V2 validation 报告；只读检查，不改变正式检索。</summary>
public sealed class RetrievalDatasetV2ValidationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public int CorpusItemCount { get; init; }

    public int QuerySampleCount { get; init; }

    public int MustHitCount { get; init; }

    public int MustNotCount { get; init; }

    public int MustHitMissingFromCorpusCount { get; init; }

    public int MustNotMissingFromCorpusCount { get; init; }

    public int MustHitMustNotOverlapCount { get; init; }

    public int QueryItemIdLeakCount { get; init; }

    public int MissingSourceRefsCount { get; init; }

    public int MissingEvidenceRefsCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int LifecycleTargetSectionMismatchCount { get; init; }

    public int RelationEvidenceMissingCount { get; init; }

    public int SplitIsolationViolationCount { get; init; }

    public int IssueCount { get; init; }

    public IReadOnlyDictionary<string, int> IssueBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RetrievalDatasetV2ValidationIssue> Issues { get; init; } =
        Array.Empty<RetrievalDatasetV2ValidationIssue>();

    public bool GeneratesFormalDataset { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ValidationRecommendations.KeepPreviewOnly;
}


/// <summary>旧 retrieval/vector eval corpus 限制报告；说明其不适合作为主 recall repair 目标。</summary>
public sealed class RetrievalDatasetLegacyLimitationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string BatchId { get; init; } = string.Empty;

    public int ReviewCandidateCount { get; init; }

    public int MissingEvidenceSourceProvenanceCandidateCount { get; init; }

    public string EvidenceBackfillRecommendation { get; init; } = string.Empty;

    public bool LegacyDatasetSuitableForPrimaryRecallRepair { get; init; }

    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredNextDataWork { get; init; } = Array.Empty<string>();

    public bool GeneratesFormalDataset { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ValidationRecommendations.NeedsIngestionMetadataBackfill;
}


public static class RetrievalDatasetV2GenerationRecommendations
{
    public const string ReadyForDatasetV2ShadowEval = nameof(ReadyForDatasetV2ShadowEval);
    public const string NeedsGenerationRepair = nameof(NeedsGenerationRepair);
    public const string BlockedByValidationIssues = nameof(BlockedByValidationIssues);
    public const string BlockedByMissingEvidence = nameof(BlockedByMissingEvidence);
    public const string BlockedByLeakage = nameof(BlockedByLeakage);
    public const string NotConfigured = nameof(NotConfigured);
}


/// <summary>Retrieval Dataset V2 generation report。</summary>
public sealed class RetrievalDatasetV2GenerationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public RetrievalDatasetV2GenerationOptions Options { get; init; } = new();

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public IReadOnlyDictionary<string, int> DifficultyBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> SplitBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int ValidationIssueCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int MustHitMissingCount { get; init; }

    public int MustNotOverlapCount { get; init; }

    public int ItemIdLeakageCount { get; init; }

    public int RelationInconsistencyCount { get; init; }

    public int JudgeWarningCount { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2GenerationRecommendations.NotConfigured;

    public IReadOnlyList<string> PromptTemplates { get; init; } = Array.Empty<string>();
}


public sealed class RetrievalDatasetV2QualityReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public IReadOnlyDictionary<string, int> DifficultyBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> SplitBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int ValidationIssueCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int MustHitMissingCount { get; init; }

    public int MustNotOverlapCount { get; init; }

    public int ItemIdLeakageCount { get; init; }

    public int RelationInconsistencyCount { get; init; }

    public int JudgeWarningCount { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2GenerationRecommendations.NotConfigured;
}


/// <summary>Retrieval Dataset V2 物化 manifest。</summary>
public sealed class RetrievalDatasetV2Manifest
{
    public string DatasetId { get; init; } = string.Empty;

    public string CorpusPath { get; init; } = string.Empty;

    public string SamplesPath { get; init; } = string.Empty;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public string CorpusHash { get; init; } = string.Empty;

    public string SamplesHash { get; init; } = string.Empty;

    public string GeneratorVersion { get; init; } = "retrieval-dataset-v2-generator/v1";

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
}


/// <summary>Retrieval Dataset V2 materialization gate recommendation。</summary>
public static class RetrievalDatasetV2MaterializationRecommendations
{
    public const string ReadyForDatasetV2ShadowEval = nameof(ReadyForDatasetV2ShadowEval);
    public const string BlockedByMissingArtifact = nameof(BlockedByMissingArtifact);
    public const string BlockedByValidationIssues = nameof(BlockedByValidationIssues);
    public const string BlockedByQualityGate = nameof(BlockedByQualityGate);
    public const string BlockedByHashInstability = nameof(BlockedByHashInstability);
    public const string BlockedByRuntimeUse = nameof(BlockedByRuntimeUse);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Retrieval Dataset V2 materialization / immutability gate report。</summary>
public sealed class RetrievalDatasetV2MaterializationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public string CorpusPath { get; init; } = string.Empty;

    public string SamplesPath { get; init; } = string.Empty;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public string CorpusHash { get; init; } = string.Empty;

    public string SamplesHash { get; init; } = string.Empty;

    public string GeneratorVersion { get; init; } = "retrieval-dataset-v2-generator/v1";

    public string ContractVersion { get; init; } = "retrieval-dataset-v2";

    public bool CorpusExists { get; init; }

    public bool SamplesExists { get; init; }

    public bool ManifestExists { get; init; }

    public bool ValidatePassed { get; init; }

    public string QualityRecommendation { get; init; } = string.Empty;

    public bool CorpusHashStable { get; init; }

    public bool SamplesHashStable { get; init; }

    public int ValidationIssueCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int ItemIdLeakageCount { get; init; }

    public int RelationInconsistencyCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2MaterializationRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


public static class RetrievalDatasetV2ShadowEvalRecommendations
{
    public const string ReadyForDatasetV2RetrievalCandidate = nameof(ReadyForDatasetV2RetrievalCandidate);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByRecall = nameof(BlockedByRecall);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByLifecycleRisk = nameof(BlockedByLifecycleRisk);
    public const string BlockedByMustNotRisk = nameof(BlockedByMustNotRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPgVectorParityMismatch = nameof(BlockedByPgVectorParityMismatch);
    public const string BlockedByDatasetValidation = nameof(BlockedByDatasetValidation);
}


/// <summary>Retrieval Dataset V2 单个 profile 的 shadow eval 报告。</summary>
public sealed class RetrievalDatasetV2ShadowEvalProfileReport
{
    public string DatasetId { get; init; } = string.Empty;

    public string CorpusHash { get; init; } = string.Empty;

    public string SamplesHash { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int CorpusItemCount { get; init; }

    public int CandidateCount { get; init; }

    public double RecallAfterPolicy { get; init; }

    public double MrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int DenseCandidateCount { get; init; }

    public int LexicalCandidateCount { get; init; }

    public int AnchorCandidateCount { get; init; }

    public int UnionCandidateCount { get; init; }

    public int EligibilityBlockedCount { get; init; }

    public int MustHitBlockedByEligibilityCount { get; init; }

    public int MustHitMissingCount { get; init; }

    public int TargetSectionMismatchCount { get; init; }

    public double TopKOverlapRate { get; init; }

    public int OrderingMismatchCount { get; init; }

    public double ScoreDeltaMax { get; init; }

    public int MetadataMismatchCount { get; init; }

    public int EligibilityMetadataMismatchCount { get; init; }

    public int RiskProjectionMismatchCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ShadowEvalRecommendations.KeepPreviewOnly;
}
