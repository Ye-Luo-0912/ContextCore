namespace ContextCore.Abstractions.Models;


/// <summary>单条 vector index 诊断结果。</summary>
public sealed class VectorIndexDiagnostic
{
    public string DiagnosticId { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Severity { get; init; } = "Warning";

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string? EntryId { get; init; }

    public string Message { get; init; } = string.Empty;

    public string SuggestedAction { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>vector reindex preview 的单条动作。</summary>
public sealed class VectorReindexPreviewItem
{
    public string ItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string CurrentContentHash { get; init; } = string.Empty;

    public string? ExistingContentHash { get; init; }

    public string Reason { get; init; } = string.Empty;
}


/// <summary>vector reindex 计划，不写入 vector index。</summary>
public sealed class VectorReindexPlan
{
    public string PlanId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? LayerFilter { get; init; }

    public string? ItemKindFilter { get; init; }

    public int TotalCandidates { get; init; }

    public int ToCreate { get; init; }

    public int ToUpdate { get; init; }

    public int ToSkip { get; init; }

    public int ToDeleteOrphan { get; init; }

    public int EstimatedEmbeddingCount { get; init; }

    public bool DryRun { get; init; } = true;

    public IReadOnlyList<string> StaleItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DuplicateItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> OrphanItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<VectorReindexPlanItem> Items { get; init; } = Array.Empty<VectorReindexPlanItem>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }
}


/// <summary>vector reindex 计划中的单条 source / entry 动作。</summary>
public sealed class VectorReindexPlanItem
{
    public string ItemId { get; init; } = string.Empty;

    public string? EntryId { get; init; }

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string CurrentContentHash { get; init; } = string.Empty;

    public string? ExistingContentHash { get; init; }

    public bool NeedsEmbedding { get; init; }

    public bool IsDuplicate { get; init; }

    public bool IsOrphan { get; init; }

    public string Reason { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>vector reindex 执行摘要。</summary>
public sealed class VectorReindexSummary
{
    public int TotalCandidates { get; init; }

    public int Created { get; init; }

    public int Updated { get; init; }

    public int Skipped { get; init; }

    public int Failed { get; init; }

    public int Duplicate { get; init; }

    public int Orphan { get; init; }

    public int EstimatedEmbeddingCount { get; init; }

    public bool DryRun { get; init; } = true;

    public bool Applied { get; init; }
}


/// <summary>vector index 覆盖率分组统计。</summary>
public sealed class VectorIndexCoverageBucket
{
    public string Key { get; init; } = string.Empty;

    public int TotalSourceItems { get; init; }

    public int IndexedItems { get; init; }

    public int MissingItems { get; init; }

    public int StaleItems { get; init; }

    public double CoverageRate { get; init; }
}


/// <summary>向量预览候选被策略阻断的原因。</summary>
public static class VectorCandidateBlockedReason
{
    public const string UnknownLifecycleBlocked = nameof(UnknownLifecycleBlocked);

    public const string LifecycleMetadataIncompleteBlocked = nameof(LifecycleMetadataIncompleteBlocked);

    public const string ReplacementMetadataMissingBlocked = nameof(ReplacementMetadataMissingBlocked);

    public const string LegacySourceRequiresLifecycleMetadata = nameof(LegacySourceRequiresLifecycleMetadata);

    public const string HistoricalSourceRequiresAuditProfile = nameof(HistoricalSourceRequiresAuditProfile);

    public const string DeprecatedCandidateBlocked = nameof(DeprecatedCandidateBlocked);

    public const string HistoricalCandidateBlocked = nameof(HistoricalCandidateBlocked);

    public const string RejectedCandidateBlocked = nameof(RejectedCandidateBlocked);

    public const string CandidateLifecycleBlocked = nameof(CandidateLifecycleBlocked);

    public const string SimilarityBelowThreshold = nameof(SimilarityBelowThreshold);

    public const string DuplicateVectorEntryBlocked = nameof(DuplicateVectorEntryBlocked);

    public const string OrphanVectorEntryBlocked = nameof(OrphanVectorEntryBlocked);

    public const string DimensionMismatchBlocked = nameof(DimensionMismatchBlocked);

    public const string StaleEmbeddingBlocked = nameof(StaleEmbeddingBlocked);

    public const string UnsupportedLayer = nameof(UnsupportedLayer);

    public const string UnsupportedItemKind = nameof(UnsupportedItemKind);

    public const string DiagnosticsOnlyItemKindBlocked = nameof(DiagnosticsOnlyItemKindBlocked);

    public const string SupersededCandidateBlocked = nameof(SupersededCandidateBlocked);
}


/// <summary>单个 mustHit 在 vector shadow 中未召回的离线审计明细。</summary>
public sealed class VectorRecallLossMiss
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public bool WasIndexed { get; init; }

    public bool WasRawCandidate { get; init; }

    public int RawRank { get; init; }

    public double RawSimilarity { get; init; }

    public bool WasEligibleCandidate { get; init; }

    public int EligibleRank { get; init; }

    public string BlockedReason { get; init; } = string.Empty;

    public string MissReason { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public int TopK { get; init; }

    public double? MinSimilarity { get; init; }

    public string LayerFilter { get; init; } = string.Empty;

    public string ItemKindFilter { get; init; } = string.Empty;
}


/// <summary>按 mode 或 intent 聚合的 vector readiness 结果。</summary>
public sealed class VectorIntentReadinessBucket
{
    public string Key { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public int Samples { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustHitMrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int NoCandidateCount { get; init; }

    public IReadOnlyDictionary<string, int> TopMissReasons { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}


/// <summary>被 eligibility policy 阻断的 mustHit 离线分类；只用于报告，不改变策略。</summary>
public static class VectorBlockedMustHitClassifications
{
    public const string MetadataRepairNeeded = nameof(MetadataRepairNeeded);

    public const string ProfileTooNarrow = nameof(ProfileTooNarrow);

    public const string LayerFilterTooStrict = nameof(LayerFilterTooStrict);

    public const string HistoricalMustHitRequiresAuditProfile = nameof(HistoricalMustHitRequiresAuditProfile);

    public const string DeprecatedMustHitBlockedCorrectly = nameof(DeprecatedMustHitBlockedCorrectly);

    public const string RequiresRankerFusion = nameof(RequiresRankerFusion);

    public const string RequiresManualReview = nameof(RequiresManualReview);

    public const string ShouldRemainBlocked = nameof(ShouldRemainBlocked);
}


/// <summary>单个被阻断 mustHit 的安全恢复审计明细。</summary>
public sealed class VectorBlockedMustHitAuditRecord
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string ResolvedLifecycle { get; init; } = string.Empty;

    public string MetadataCompleteness { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public bool CanBeSafelyAllowed { get; init; }

    public string MustRemainBlockedReason { get; init; } = string.Empty;

    public string RecommendedRepair { get; init; } = string.Empty;

    public string Classification { get; init; } = VectorBlockedMustHitClassifications.RequiresManualReview;
}


/// <summary>vector + ranker fusion 的离线 shadow 策略名。</summary>
public static class VectorRankerFusionStrategies
{
    public const string VectorOnly = nameof(VectorOnly);

    public const string RankerOnly = nameof(RankerOnly);

    public const string UnionThenRank = nameof(UnionThenRank);

    public const string VectorBoostedRanker = nameof(VectorBoostedRanker);

    public const string RankerFilteredVector = nameof(RankerFilteredVector);

    public const string LifecycleAwareFusion = nameof(LifecycleAwareFusion);
}


/// <summary>单个 missed mustHit 的 representation 审计明细。</summary>
public sealed class VectorMissSetRepresentationAuditRecord
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public IReadOnlyList<string> QueryAnchors { get; init; } = Array.Empty<string>();

    public string MustHitItemId { get; init; } = string.Empty;

    public string DocumentTitle { get; init; } = string.Empty;

    public IReadOnlyList<string> DocumentAnchors { get; init; } = Array.Empty<string>();

    public string DocumentRepresentationProfile { get; init; } = DocumentRepresentationProfiles.RawContentV1;

    public string QueryRepresentationProfile { get; init; } = QueryRepresentationProfiles.RawQueryV1;

    public double RawSimilarity { get; init; }

    public int RawRank { get; init; }

    public int EligibleRank { get; init; }

    public string MissReason { get; init; } = string.Empty;

    public string RepresentationDiagnosis { get; init; } = string.Empty;

    public string RecommendedRepair { get; init; } = string.Empty;
}


/// <summary>vector source lifecycle metadata coverage 的分组统计。</summary>
public sealed class VectorLifecycleMetadataCoverageBucket
{
    public string Key { get; init; } = string.Empty;

    public int Total { get; init; }

    public int KnownLifecycleCount { get; init; }

    public int UnknownLifecycleCount { get; init; }

    public int MissingReviewStatusCount { get; init; }

    public int MissingReplacementInfoCount { get; init; }

    public int LegacySourceWithoutLifecycleCount { get; init; }

    public int DeprecatedSourceWithoutLifecycleCount { get; init; }

    public double LifecycleCoverageRate { get; init; }
}


/// <summary>vector lifecycle metadata backfill 候选动作。</summary>
public static class VectorLifecycleMetadataBackfillActions
{
    public const string AutoResolve = nameof(AutoResolve);

    public const string ManualReviewRequired = nameof(ManualReviewRequired);

    public const string CannotResolve = nameof(CannotResolve);

    public const string AlreadyKnown = nameof(AlreadyKnown);
}


/// <summary>单个 vector lifecycle metadata backfill 候选；只描述可追踪 metadata 修复，不修改正式检索。</summary>
public sealed class VectorLifecycleMetadataBackfillCandidate
{
    public string ItemId { get; init; } = string.Empty;

    public string EntryId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public string CurrentLifecycle { get; init; } = string.Empty;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string ProposedReviewStatus { get; init; } = string.Empty;

    public string Action { get; init; } = VectorLifecycleMetadataBackfillActions.ManualReviewRequired;

    public string Reason { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public bool CanApply { get; init; }

    public IReadOnlyList<string> EvidenceMetadataKeys { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> BackfilledMetadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>vector lifecycle metadata backfill 计划；默认 dry-run，只允许显式确认后写 vector sidecar metadata。</summary>
public sealed class VectorLifecycleMetadataBackfillPlan
{
    public string PlanId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public int TotalVectorSourceItems { get; init; }

    public int UnknownLifecycleBefore { get; init; }

    public int AutoResolvableCount { get; init; }

    public int ManualReviewRequiredCount { get; init; }

    public int CannotResolveCount { get; init; }

    public int ExpectedKnownLifecycleAfter { get; init; }

    public double ExpectedCoverageAfter { get; init; }

    public string RiskImpact { get; init; } = string.Empty;

    public double RecallRecoveryEstimate { get; init; }

    public bool DryRun { get; init; } = true;

    public IReadOnlyList<VectorLifecycleMetadataBackfillCandidate> Candidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataBackfillCandidate>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }
}


/// <summary>eligibility recall loss triage 分类；只用于离线报告。</summary>
public static class VectorEligibilityRecallLossTriageCategories
{
    public const string CorrectlyBlockedDeprecated = nameof(CorrectlyBlockedDeprecated);
    public const string CorrectlyBlockedHistorical = nameof(CorrectlyBlockedHistorical);
    public const string CorrectlyBlockedSuperseded = nameof(CorrectlyBlockedSuperseded);
    public const string ShouldRouteToHistoricalContext = nameof(ShouldRouteToHistoricalContext);
    public const string ShouldRouteToAuditContext = nameof(ShouldRouteToAuditContext);
    public const string MetadataLifecycleRepairNeeded = nameof(MetadataLifecycleRepairNeeded);
    public const string ReviewStatusRepairNeeded = nameof(ReviewStatusRepairNeeded);
    public const string ReplacementStateRepairNeeded = nameof(ReplacementStateRepairNeeded);
    public const string ProfileTooStrictForAuditMode = nameof(ProfileTooStrictForAuditMode);
    public const string RequiresEvalExpectationReview = nameof(RequiresEvalExpectationReview);
    public const string UnsafeToRecover = nameof(UnsafeToRecover);
}


/// <summary>单条 lifecycle-filtered mustHit 的离线 triage 明细。</summary>
public sealed class VectorEligibilityRecallLossTriageDetail
{
    public string DatasetName { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string BlockedReason { get; init; } = string.Empty;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public string CurrentTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string CandidateTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public bool ShouldRemainBlocked { get; init; }

    public bool CanRouteToAuditOrHistorical { get; init; }

    public bool CanRepairMetadata { get; init; }

    public string RecommendedAction { get; init; } = string.Empty;

    public string Rationale { get; init; } = string.Empty;

    public string TriageCategory { get; init; } = VectorEligibilityRecallLossTriageCategories.UnsafeToRecover;
}


/// <summary>单个 lifecycle-filtered mustHit 的 metadata repair preview 候选。</summary>
public sealed class VectorLifecycleMetadataRepairCandidate
{
    public string DatasetName { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string CurrentLifecycle { get; init; } = string.Empty;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string CurrentReviewStatus { get; init; } = string.Empty;

    public string ProposedReviewStatus { get; init; } = string.Empty;

    public string CurrentTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string ProposedTargetSection { get; init; } = VectorQueryTargetSections.DiagnosticsOnly;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public bool ProvenanceAvailable { get; init; }

    public bool RelationEvidenceAvailable { get; init; }

    public bool ReviewEvidenceAvailable { get; init; }

    public double RepairConfidence { get; init; }

    public string RepairReason { get; init; } = string.Empty;

    public bool CanAutoRepair { get; init; }

    public bool RequiresHumanReview { get; init; }

    public string ForbiddenReason { get; init; } = string.Empty;
}


/// <summary>从 lifecycle metadata repair plan 派生的人工 review 候选项；不会直接改变 runtime eligibility。</summary>
public sealed class VectorLifecycleMetadataReviewCandidate
{
    public string CandidateId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string SourceSampleId { get; init; } = string.Empty;

    public string SourceEvalSet { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string CurrentLifecycle { get; init; } = string.Empty;

    public string CurrentReviewStatus { get; init; } = string.Empty;

    public string CurrentTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string ProposedReviewStatus { get; init; } = string.Empty;

    public string ProposedTargetSection { get; init; } = VectorQueryTargetSections.DiagnosticsOnly;

    public string RepairReason { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public bool ProvenanceAvailable { get; init; }

    public bool RelationEvidenceAvailable { get; init; }

    public bool ReviewEvidenceAvailable { get; init; }

    public IReadOnlyList<string> RiskIfApproved { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RiskIfRejected { get; init; } = Array.Empty<string>();

    public bool RequiresHumanReview { get; init; }

    public string Status { get; init; } = VectorLifecycleMetadataReviewCandidateStatuses.PendingReview;

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>vector lifecycle metadata review candidate explain 响应；只读展示证据与风险。</summary>
public sealed class VectorLifecycleMetadataReviewCandidateExplanation
{
    public string CandidateId { get; init; } = string.Empty;

    public VectorLifecycleMetadataReviewCandidate Candidate { get; init; } = new();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public bool ProvenanceAvailable { get; init; }

    public bool RelationEvidenceAvailable { get; init; }

    public bool ReviewEvidenceAvailable { get; init; }

    public IReadOnlyList<string> RiskIfApproved { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RiskIfRejected { get; init; } = Array.Empty<string>();

    public string RepairReason { get; init; } = string.Empty;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>lifecycle metadata evidence backfill 的外部证据快照；只供 preview/audit 使用。</summary>
public sealed class VectorLifecycleMetadataEvidenceSourceSnapshot
{
    public string ItemId { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string ProvenanceRecordId { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;

    public IReadOnlyList<string> RelationEvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ReviewEvidenceRefs { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> OriginalCorpusMetadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>单条 stress recall failure detail。</summary>
public sealed class RetrievalDatasetV2StressRecallFailureDetail
{
    public string SampleId { get; init; } = string.Empty;

    public string Split { get; init; } = string.Empty;

    public string Difficulty { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string ExpectedTargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> MustHitItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotHitItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DenseTopK { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LexicalTopK { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AnchorTopK { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> HybridTopK { get; init; } = Array.Empty<string>();

    public bool MustHitPresentInCorpus { get; init; }

    public bool MustHitPresentInCandidateSet { get; init; }

    public int MustHitRank { get; init; }

    public bool MustHitBlockedByEligibility { get; init; }

    public bool MustHitTargetSectionMismatch { get; init; }

    public string NearestWrongCandidateId { get; init; } = string.Empty;

    public string NearestWrongCandidateKind { get; init; } = string.Empty;

    public string FailureReason { get; init; } = RetrievalDatasetV2StressFailureReasons.Unknown;

    public string RecommendedRepair { get; init; } = string.Empty;
}


/// <summary>Stress failure triage 的 profile 对比统计。</summary>
public sealed class RetrievalDatasetV2StressProfileComparison
{
    public string ComparisonName { get; init; } = string.Empty;

    public string LeftProfileName { get; init; } = string.Empty;

    public string RightProfileName { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int LeftHitCount { get; init; }

    public int RightHitCount { get; init; }

    public int LeftOnlyWinCount { get; init; }

    public int RightOnlyWinCount { get; init; }

    public int BothHitCount { get; init; }

    public int BothMissCount { get; init; }

    public double LeftRecall { get; init; }

    public double RightRecall { get; init; }
}


/// <summary>Hybrid scoring risk regression 分类。</summary>
public static class HybridScoringRiskRegressionReasons
{
    public const string BlockedCandidateReintroduced = nameof(BlockedCandidateReintroduced);
    public const string EligibilityPolicyBypassed = nameof(EligibilityPolicyBypassed);
    public const string MustNotCandidatePromoted = nameof(MustNotCandidatePromoted);
    public const string DeprecatedCandidatePromoted = nameof(DeprecatedCandidatePromoted);
    public const string HistoricalCandidatePromoted = nameof(HistoricalCandidatePromoted);
    public const string LifecycleRiskPromoted = nameof(LifecycleRiskPromoted);
    public const string TargetSectionMismatchPromoted = nameof(TargetSectionMismatchPromoted);
    public const string NegativePenaltyOverPromotedWrongCandidate = nameof(NegativePenaltyOverPromotedWrongCandidate);
    public const string ScoreFusionOrderBug = nameof(ScoreFusionOrderBug);
    public const string RiskProjectionMismatch = nameof(RiskProjectionMismatch);
    public const string Unknown = nameof(Unknown);
}


/// <summary>Hybrid scoring risk regression 单条风险候选。</summary>
public sealed class HybridScoringRiskRegressionDetail
{
    public string SampleId { get; init; } = string.Empty;

    public string Split { get; init; } = string.Empty;

    public string Difficulty { get; init; } = string.Empty;

    public string QueryText { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public int CandidateRank { get; init; }

    public string CandidateKind { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public string RiskType { get; init; } = string.Empty;

    public string RiskReason { get; init; } = HybridScoringRiskRegressionReasons.Unknown;

    public bool WasEligibleBeforeRepair { get; init; }

    public bool WasBlockedBeforeRepair { get; init; }

    public int DenseRank { get; init; }

    public int LexicalRank { get; init; }

    public int AnchorRank { get; init; }

    public int BaselineHybridRank { get; init; }

    public int RepairedHybridRank { get; init; }

    public double ScoreBeforeRepair { get; init; }

    public double ScoreAfterRepair { get; init; }

    public double ScoreDelta { get; init; }

    public string ContributionSource { get; init; } = string.Empty;

    public int NearestMustHitRank { get; init; }

    public int NearestMustNotRank { get; init; }

    public string RecommendedFix { get; init; } = string.Empty;
}


/// <summary>Scoped runtime experiment manual approval；只授权 no-op harness，不授权 runtime switch。</summary>
public sealed class ScopedRuntimeExperimentApprovalRecord
{
    public string ApprovalId { get; init; } = string.Empty;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovedBy { get; init; } = string.Empty;

    public DateTimeOffset ApprovedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ApprovalScope { get; init; } = string.Empty;

    public string ApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly;

    public string Reason { get; init; } = string.Empty;

    public string RiskAcknowledgement { get; init; } = string.Empty;

    public string RollbackAcknowledgement { get; init; } = string.Empty;

    public string KillSwitchAcknowledgement { get; init; } = string.Empty;

    public string ScopeAcknowledgement { get; init; } = string.Empty;

    public string ObservationPlanAcknowledgement { get; init; } = string.Empty;

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool Revoked { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>V4.14 scoped runtime experiment trace；shadow route 只写观测 trace。</summary>
public sealed class ScopedRuntimeExperimentTrace
{
    public string RequestId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public bool ScopeMatched { get; init; }

    public bool ExperimentRouteHit { get; init; }

    public string BaselinePackageId { get; init; } = string.Empty;

    public string ExperimentPackagePreviewId { get; init; } = string.Empty;

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool KillSwitchTriggered { get; init; }

    public string Error { get; init; } = string.Empty;

    public int DurationMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}


/// <summary>runtime-observable feature classifications。</summary>
public static class RuntimeObservableFeatureClassifications
{
    public const string RuntimeObservable = nameof(RuntimeObservable);
    public const string DerivedAtRuntime = nameof(DerivedAtRuntime);
    public const string EvalOnly = nameof(EvalOnly);
    public const string ForbiddenForScoring = nameof(ForbiddenForScoring);
}


/// <summary>runtime-observable feature usage kinds。</summary>
public static class RuntimeObservableFeatureUsageKinds
{
    public const string Scoring = nameof(Scoring);
    public const string Filtering = nameof(Filtering);
    public const string CandidateExpansion = nameof(CandidateExpansion);
    public const string Knob = nameof(Knob);
}


/// <summary>single feature usage record。</summary>
public sealed class RuntimeObservableFeatureUsage
{
    public string FeatureId { get; init; } = string.Empty;

    public string Classification { get; init; } = string.Empty;

    public string UsageKind { get; init; } = string.Empty;

    public string DerivationPath { get; init; } = string.Empty;

    public string CurrentSource { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<string> ProfileIds { get; init; } = Array.Empty<string>();
}


/// <summary>单个清理项。</summary>
public sealed class ArchitectureCleanupItem
{
    public string Priority { get; init; } = "nice-to-have";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string CurrentState { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public string Risk { get; init; } = "low";
}


/// <summary>lifecycle metadata review 历史记录。</summary>
public sealed class VectorLifecycleMetadataReviewRecord
{
    public string ReviewId { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string Decision { get; init; } = string.Empty;

    public string ResultStatus { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string ProposedReviewStatus { get; init; } = string.Empty;

    public string ProposedTargetSection { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public bool SidecarWritten { get; init; }

    public bool UnsafeApprovalBlocked { get; init; }

    public string BlockedReason { get; init; } = string.Empty;

    public DateTimeOffset ReviewedAt { get; init; } = DateTimeOffset.UtcNow;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>sidecar lifecycle metadata override；只写旁路文件，不修改业务 source item。</summary>
public sealed class VectorLifecycleSidecarMetadataEntry
{
    public string ItemId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string LifecycleOverride { get; init; } = string.Empty;

    public string ReviewStatusOverride { get; init; } = string.Empty;

    public string TargetSectionOverride { get; init; } = string.Empty;

    public string SourceReviewId { get; init; } = string.Empty;

    public string SourceCandidateId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string PolicyVersion { get; init; } = "vector-lifecycle-sidecar/v1";

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>sidecar effective metadata 来源。</summary>
public static class VectorLifecycleSidecarResolutionSources
{
    public const string Base = nameof(Base);

    public const string Sidecar = nameof(Sidecar);

    public const string Conflict = nameof(Conflict);

    public const string NotFound = nameof(NotFound);
}


/// <summary>单个 item 的 sidecar-aware lifecycle metadata 解析结果。</summary>
public sealed class VectorLifecycleSidecarResolution
{
    public string ItemId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string BaseLifecycle { get; init; } = string.Empty;

    public string BaseReviewStatus { get; init; } = string.Empty;

    public string BaseTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string EffectiveLifecycle { get; init; } = string.Empty;

    public string EffectiveReviewStatus { get; init; } = string.Empty;

    public string EffectiveTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string Source { get; init; } = VectorLifecycleSidecarResolutionSources.Base;

    public string SidecarReviewId { get; init; } = string.Empty;

    public bool Valid { get; init; } = true;

    public bool Blocked { get; init; }

    public string BlockedReason { get; init; } = string.Empty;

    public bool SourceItemUnchanged { get; init; } = true;

    public string Explanation { get; init; } = string.Empty;
}


/// <summary>lifecycle metadata 人工 review batch；仅组织人工审阅，不自动写 sidecar。</summary>
public sealed class VectorLifecycleMetadataReviewBatch
{
    public string BatchId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public IReadOnlyList<string> CandidateIds { get; init; } = Array.Empty<string>();

    public int CandidateCount { get; init; }

    public string Status { get; init; } = VectorLifecycleMetadataReviewBatchStatuses.Draft;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string CreatedBy { get; init; } = string.Empty;

    public string ReviewInstructions { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>人工 review sheet 单行；ReviewerDecision 为空时表示尚未审阅。</summary>
public sealed class VectorLifecycleMetadataReviewSheetRow
{
    public string CandidateId { get; init; } = string.Empty;

    public string MustHitItemId { get; init; } = string.Empty;

    public string CurrentLifecycle { get; init; } = string.Empty;

    public string ProposedLifecycle { get; init; } = string.Empty;

    public string CurrentTargetSection { get; init; } = VectorQueryTargetSections.Excluded;

    public string ProposedTargetSection { get; init; } = VectorQueryTargetSections.DiagnosticsOnly;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public string RepairReason { get; init; } = string.Empty;

    public string ReviewerDecision { get; init; } = string.Empty;

    public string ReviewerReason { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string TargetSectionOverride { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;
}


/// <summary>review batch validation 单条问题。</summary>
public sealed class VectorLifecycleMetadataReviewBatchValidationIssue
{
    public string CandidateId { get; init; } = string.Empty;

    public string Severity { get; init; } = "Error";

    public string Reason { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}


/// <summary>embedding generator 单条输入。</summary>
public sealed class EmbeddingGeneratorInput
{
    public string ItemId { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}


/// <summary>formal adapter 可读取的当前 package 上下文快照。</summary>
public sealed class FormalAdapterRuntimePackageContext
{
    public string BaselinePackageId { get; init; } = string.Empty;

    public IReadOnlyList<string> BaselineCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, int> SectionTokenBudgets { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> SectionOccupancy { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int TotalTokenBudget { get; init; }

    public int MaxCandidateCount { get; init; }
}

/// <summary>
/// formal adapter 可读取的候选输入。ItemId 仅作候选身份与稳定 tie-break，不允许作业务特判。
/// </summary>
public sealed class FormalAdapterRuntimeCandidateInput
{
    public string CandidateId { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string ReplacementState { get; init; } = string.Empty;

    public string TargetSection { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Anchors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public FormalAdapterRuntimeProvenanceInput Provenance { get; init; } = new();

    public IReadOnlyList<FormalAdapterRuntimeRelationEvidenceInput> Relations { get; init; } =
        Array.Empty<FormalAdapterRuntimeRelationEvidenceInput>();

    public int EstimatedTokens { get; init; }

    public double Score { get; init; }

    public int DenseRank { get; init; }

    public int LexicalRank { get; init; }

    public int AnchorRank { get; init; }
}


/// <summary>formal adapter 可读取的 provenance 子集。</summary>
public sealed class FormalAdapterRuntimeProvenanceInput
{
    public string RecordId { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public string IngestionBatchId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}


/// <summary>formal adapter 可读取的 relation evidence 子集。</summary>
public sealed class FormalAdapterRuntimeRelationEvidenceInput
{
    public string RelationId { get; init; } = string.Empty;

    public string RelationType { get; init; } = string.Empty;

    public string Direction { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public string SourceItemId { get; init; } = string.Empty;

    public string TargetItemId { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();
}
