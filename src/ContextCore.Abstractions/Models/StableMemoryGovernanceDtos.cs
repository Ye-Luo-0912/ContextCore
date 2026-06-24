namespace ContextCore.Abstractions.Models;

/// <summary>Stable memory governance 的统一只读记录。</summary>
public sealed class StableMemoryRecord
{
    public string Id { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string StableKind { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public ContextMemoryStatus Status { get; init; } = ContextMemoryStatus.Stable;

    public string Lifecycle { get; init; } = StableMemoryLifecycle.Current;

    public double Importance { get; init; }

    public double Confidence { get; init; }

    public ContextScope? Scope { get; init; }

    public ConstraintLevel? ConstraintLevel { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string? StableReviewCandidateId { get; init; }

    public string? PromotionCandidateId { get; init; }

    public string? LearningCaseId { get; init; }

    public string? FeedbackId { get; init; }

    public string? WorkingItemId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Stable memory governance 聚合快照。</summary>
public sealed class StableMemorySnapshot
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public int StableMemoryCount { get; init; }

    public int StableConstraintCount { get; init; }

    public int DecisionRecordCount { get; init; }

    public int GlobalMemoryCount { get; init; }

    public int ActiveCount { get; init; }

    public int SupersededCount { get; init; }

    public int DeprecatedCount { get; init; }

    public int RejectedCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int DuplicateCandidateCount { get; init; }

    public int ConflictCandidateCount { get; init; }

    public int WeakEvidenceCount { get; init; }

    public IReadOnlyList<StableMemoryRecord> RecentStableItems { get; init; } = Array.Empty<StableMemoryRecord>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Stable memory governance explain 响应。</summary>
public sealed class StableMemoryExplanation
{
    public string StableItemId { get; init; } = string.Empty;

    public StableMemoryRecord StableItem { get; init; } = new();

    public ContextProvenanceResponse? Provenance { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<StableMemoryDiagnostic> Diagnostics { get; init; } = Array.Empty<StableMemoryDiagnostic>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Stable supersede / replacement relation chain response.</summary>
public sealed class StableReplacementChainResponse
{
    public string ItemId { get; init; } = string.Empty;

    public StableMemoryRecord CurrentItem { get; init; } = new();

    public IReadOnlyList<StableMemoryRecord> PreviousItems { get; init; } = Array.Empty<StableMemoryRecord>();

    public IReadOnlyList<StableMemoryRecord> NextItems { get; init; } = Array.Empty<StableMemoryRecord>();

    public StableMemoryRecord? RootItem { get; init; }

    public StableMemoryRecord? LatestItem { get; init; }

    public IReadOnlyList<ContextRelation> Relations { get; init; } = Array.Empty<ContextRelation>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Stable memory 生命周期人工 review 请求。</summary>
public sealed class StableLifecycleReviewRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? ReplacementItemId { get; init; }

    public bool AllowDeprecatedSupersededDeprecation { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Stable memory 生命周期人工 review 结果。</summary>
public sealed class StableLifecycleReviewResult
{
    public string OperationId { get; init; } = string.Empty;

    public string StableItemId { get; init; } = string.Empty;

    public string StableKind { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public ContextMemoryStatus FromStatus { get; init; } = ContextMemoryStatus.Stable;

    public ContextMemoryStatus ToStatus { get; init; } = ContextMemoryStatus.Stable;

    public string FromLifecycle { get; init; } = StableMemoryLifecycle.Current;

    public string ToLifecycle { get; init; } = StableMemoryLifecycle.Current;

    public string ReviewId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset ReviewedAt { get; init; }

    public string? ReplacementItemId { get; init; }

    public StableMemoryRecord StableItem { get; init; } = new();

    public StableLifecycleReviewRecord Review { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>Stable memory 生命周期人工 review 审计记录。</summary>
public sealed class StableLifecycleReviewRecord
{
    public string ReviewId { get; init; } = string.Empty;

    public string StableItemId { get; init; } = string.Empty;

    public string StableKind { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string Action { get; init; } = string.Empty;

    public ContextMemoryStatus FromStatus { get; init; } = ContextMemoryStatus.Stable;

    public ContextMemoryStatus ToStatus { get; init; } = ContextMemoryStatus.Stable;

    public string FromLifecycle { get; init; } = StableMemoryLifecycle.Current;

    public string ToLifecycle { get; init; } = StableMemoryLifecycle.Current;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? ReplacementItemId { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ReviewedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>Stable memory governance 诊断报告。</summary>
public sealed class StableMemoryDiagnosticsReport
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public int DiagnosticCount { get; init; }

    public int DuplicateStableMemoryCount { get; init; }

    public int PossibleConflictCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public int MissingEvidenceRefsCount { get; init; }

    public int StableWithoutReviewSourceCount { get; init; }

    public int StableConstraintWithoutScopeCount { get; init; }

    public int DecisionRecordWithoutSourceCount { get; init; }

    public int DeprecatedStillActiveCount { get; init; }

    public int SupersededWithoutReplacementCount { get; init; }

    public int GlobalMemoryScopeRiskCount { get; init; }

    public int SupersededWithoutRelationCount { get; init; }

    public int MetadataRelationMismatchCount { get; init; }

    public int BrokenReplacementLinkCount { get; init; }

    public int ReplacementTargetMissingCount { get; init; }

    public int ReplacementTargetInactiveCount { get; init; }

    public int ReplacementCycleCount { get; init; }

    public int MultipleActiveReplacementsCount { get; init; }

    public int ScopeMismatchInReplacementCount { get; init; }

    public IReadOnlyList<StableMemoryDiagnostic> Diagnostics { get; init; } = Array.Empty<StableMemoryDiagnostic>();
}

/// <summary>Stable memory governance 单条诊断。</summary>
public sealed class StableMemoryDiagnostic
{
    public string DiagnosticId { get; init; } = string.Empty;

    public string StableItemId { get; init; } = string.Empty;

    public string StableKind { get; init; } = string.Empty;

    public string DiagnosticType { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> RelatedStableItemIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class StableMemoryKinds
{
    public const string StableMemory = nameof(StableMemory);

    public const string StableConstraint = nameof(StableConstraint);

    public const string DecisionRecord = nameof(DecisionRecord);

    public const string GlobalMemory = nameof(GlobalMemory);
}

public static class StableMemoryLifecycle
{
    public const string Current = nameof(Current);

    public const string Active = nameof(Active);

    public const string Deprecated = nameof(Deprecated);

    public const string Superseded = nameof(Superseded);

    public const string Rejected = nameof(Rejected);
}

public static class StableMemoryDiagnosticTypes
{
    public const string DuplicateStableMemory = nameof(DuplicateStableMemory);

    public const string PossibleConflict = nameof(PossibleConflict);

    public const string MissingProvenance = nameof(MissingProvenance);

    public const string MissingEvidenceRefs = nameof(MissingEvidenceRefs);

    public const string StableWithoutReviewSource = nameof(StableWithoutReviewSource);

    public const string StableConstraintWithoutScope = nameof(StableConstraintWithoutScope);

    public const string DecisionRecordWithoutSource = nameof(DecisionRecordWithoutSource);

    public const string DeprecatedStillActive = nameof(DeprecatedStillActive);

    public const string SupersededWithoutReplacement = nameof(SupersededWithoutReplacement);

    public const string GlobalMemoryScopeRisk = nameof(GlobalMemoryScopeRisk);

    public const string SupersededWithoutRelation = nameof(SupersededWithoutRelation);

    public const string MetadataRelationMismatch = nameof(MetadataRelationMismatch);

    public const string BrokenReplacementLink = nameof(BrokenReplacementLink);

    public const string ReplacementTargetMissing = nameof(ReplacementTargetMissing);

    public const string ReplacementTargetInactive = nameof(ReplacementTargetInactive);

    public const string ReplacementCycle = nameof(ReplacementCycle);

    public const string MultipleActiveReplacements = nameof(MultipleActiveReplacements);

    public const string ScopeMismatchInReplacement = nameof(ScopeMismatchInReplacement);
}

public static class StableLifecycleReviewActions
{
    public const string Deprecate = nameof(Deprecate);

    public const string Supersede = nameof(Supersede);

    public const string Reject = nameof(Reject);

    public const string MarkNeedsMoreEvidence = nameof(MarkNeedsMoreEvidence);
}
