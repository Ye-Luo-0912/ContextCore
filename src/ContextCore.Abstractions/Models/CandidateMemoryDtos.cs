namespace ContextCore.Abstractions.Models;

/// <summary>中期候选记忆的一等治理视图，不参与正式检索排序或打包决策。</summary>
public sealed class CandidateMemoryItem
{
    public string Id { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string Type { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public ContextMemoryStatus Status { get; init; } = ContextMemoryStatus.Candidate;

    public string Lifecycle { get; init; } = CandidateMemoryLifecycle.Current;

    public double Importance { get; init; }

    public double Confidence { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string? PromotionCandidateId { get; init; }

    public string? StableReviewCandidateId { get; init; }

    public string? LearningCaseId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Candidate memory 生命周期标签。</summary>
public static class CandidateMemoryLifecycle
{
    public const string Current = nameof(Current);

    public const string Stale = nameof(Stale);

    public const string Superseded = nameof(Superseded);

    public const string Rejected = nameof(Rejected);
}

/// <summary>Candidate memory 查询条件。</summary>
public sealed class CandidateMemoryQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string? Type { get; init; }

    public ContextMemoryStatus? Status { get; init; }

    public string? Lifecycle { get; init; }

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

/// <summary>Candidate memory 聚合摘要。</summary>
public sealed class CandidateMemorySummary
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public int TotalCount { get; init; }

    public IReadOnlyDictionary<string, int> CountByType { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> CountByStatus { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> CountByLifecycle { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CandidateMemoryItem> StaleCandidates { get; init; } = Array.Empty<CandidateMemoryItem>();

    public IReadOnlyList<CandidateMemoryItem> RecentCandidates { get; init; } = Array.Empty<CandidateMemoryItem>();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Candidate memory governance 的统一候选记录。</summary>
public sealed class CandidateMemoryRecord
{
    public string Id { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string CandidateKind { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public ContextMemoryStatus Status { get; init; } = ContextMemoryStatus.Candidate;

    public string Lifecycle { get; init; } = CandidateMemoryLifecycle.Current;

    public double Importance { get; init; }

    public double Confidence { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string? PromotionCandidateId { get; init; }

    public string? StableReviewCandidateId { get; init; }

    public string? ConstraintGapId { get; init; }

    public string? FeedbackId { get; init; }

    public string? LearningCaseId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Candidate memory governance 聚合快照。</summary>
public sealed class CandidateMemorySnapshot
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public int CandidateMemoryCount { get; init; }

    public int CandidateConstraintCount { get; init; }

    public int CandidateDecisionCount { get; init; }

    public int PendingReviewCount { get; init; }

    public int AcceptedFromPromotionCount { get; init; }

    public int ExpiredCandidateCount { get; init; }

    public int DuplicateCandidateCount { get; init; }

    public int ConflictCandidateCount { get; init; }

    public IReadOnlyList<CandidateMemoryRecord> RecentCandidates { get; init; } = Array.Empty<CandidateMemoryRecord>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Candidate memory explain 响应。</summary>
public sealed class CandidateMemoryExplanation
{
    public string CandidateId { get; init; } = string.Empty;

    public CandidateMemoryRecord Candidate { get; init; } = new();

    public ShortTermPromotionCandidate? SourcePromotionCandidate { get; init; }

    public StableReviewCandidate? SourceStableReviewCandidate { get; init; }

    public ConstraintGapCandidate? SourceConstraintGap { get; init; }

    public PromotionFeedbackSignal? SourceFeedbackSignal { get; init; }

    public ContextLearningCase? SourceLearningCase { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<PromotionCandidateReviewRecord> PromotionReviewHistory { get; init; } =
        Array.Empty<PromotionCandidateReviewRecord>();

    public IReadOnlyList<StableReviewRecord> StableReviewHistory { get; init; } =
        Array.Empty<StableReviewRecord>();

    public IReadOnlyList<ConstraintGapReviewRecord> ConstraintGapReviewHistory { get; init; } =
        Array.Empty<ConstraintGapReviewRecord>();

    public IReadOnlyList<CandidateConstraintReviewRecord> CandidateConstraintReviewHistory { get; init; } =
        Array.Empty<CandidateConstraintReviewRecord>();

    public IReadOnlyList<CandidateMemoryReviewRecord> CandidateMemoryReviewHistory { get; init; } =
        Array.Empty<CandidateMemoryReviewRecord>();

    public IReadOnlyList<CandidateMemoryProvenanceLink> ProvenanceChain { get; init; } =
        Array.Empty<CandidateMemoryProvenanceLink>();

    public IReadOnlyList<string> RiskFlags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Candidate memory 来源链节点。</summary>
public sealed class CandidateMemoryProvenanceLink
{
    public string SourceType { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string Relation { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;
}

/// <summary>Candidate memory 诊断报告。</summary>
public sealed class CandidateMemoryDiagnosticsReport
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public int DiagnosticCount { get; init; }

    public int DuplicateCandidateCount { get; init; }

    public int StaleCandidateCount { get; init; }

    public int CandidateWithoutEvidenceCount { get; init; }

    public int CandidateWithRejectedSourceCount { get; init; }

    public int StableConflictCount { get; init; }

    public int SupersededCandidateCount { get; init; }

    public IReadOnlyList<CandidateMemoryDiagnostic> Diagnostics { get; init; } =
        Array.Empty<CandidateMemoryDiagnostic>();
}

/// <summary>Candidate memory 单条诊断。</summary>
public sealed class CandidateMemoryDiagnostic
{
    public string DiagnosticId { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public string DiagnosticType { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string SuggestedAction { get; init; } = string.Empty;

    public IReadOnlyList<string> RelatedCandidateIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();
}

/// <summary>Candidate memory 人工 review / cleanup 请求。</summary>
public sealed class CandidateMemoryReviewRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? SupersedeTargetCandidateId { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Candidate memory 人工 review / cleanup 结果。</summary>
public sealed class CandidateMemoryReviewResult
{
    public string OperationId { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public string CandidateKind { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public ContextMemoryStatus FromStatus { get; init; } = ContextMemoryStatus.Candidate;

    public ContextMemoryStatus ToStatus { get; init; } = ContextMemoryStatus.Candidate;

    public string ReviewId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset ReviewedAt { get; init; }

    public string? SupersedeTargetCandidateId { get; init; }

    public CandidateMemoryRecord Candidate { get; init; } = new();

    public CandidateMemoryReviewRecord Review { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>Candidate memory 人工 review / cleanup 审计记录。</summary>
public sealed class CandidateMemoryReviewRecord
{
    public string ReviewId { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public string CandidateKind { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string Action { get; init; } = string.Empty;

    public ContextMemoryStatus FromStatus { get; init; } = ContextMemoryStatus.Candidate;

    public ContextMemoryStatus ToStatus { get; init; } = ContextMemoryStatus.Candidate;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? SupersedeTargetCandidateId { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ReviewedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public static class CandidateMemoryKinds
{
    public const string Memory = nameof(Memory);

    public const string Constraint = nameof(Constraint);

    public const string Decision = nameof(Decision);
}

public static class CandidateMemoryDiagnosticTypes
{
    public const string DuplicateCandidate = nameof(DuplicateCandidate);

    public const string StaleCandidate = nameof(StaleCandidate);

    public const string CandidateWithoutEvidence = nameof(CandidateWithoutEvidence);

    public const string CandidateWithRejectedSource = nameof(CandidateWithRejectedSource);

    public const string StableConflict = nameof(StableConflict);

    public const string SupersededByNewerCandidate = nameof(SupersededByNewerCandidate);
}

public static class CandidateMemoryReviewActions
{
    public const string MarkReadyForStableReview = nameof(MarkReadyForStableReview);

    public const string NeedsMoreEvidence = nameof(NeedsMoreEvidence);

    public const string Reject = nameof(Reject);

    public const string Expire = nameof(Expire);

    public const string Supersede = nameof(Supersede);
}

public static class CandidateMemoryReviewStates
{
    public const string ReadyForStableReview = nameof(ReadyForStableReview);

    public const string NeedsMoreEvidence = nameof(NeedsMoreEvidence);

    public const string Rejected = nameof(Rejected);

    public const string Expired = nameof(Expired);

    public const string Superseded = nameof(Superseded);
}
