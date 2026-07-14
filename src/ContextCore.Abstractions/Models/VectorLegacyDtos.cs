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

/// <summary>embedding generator 单条输入。</summary>
public sealed class EmbeddingGeneratorInput
{
    public string ItemId { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
