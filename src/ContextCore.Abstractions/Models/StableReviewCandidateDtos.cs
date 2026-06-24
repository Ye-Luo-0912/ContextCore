namespace ContextCore.Abstractions.Models;

/// <summary>从已接受短期晋升候选项派生的 Stable review 候选项。</summary>
public sealed class StableReviewCandidate
{
    public string StableReviewCandidateId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string SourceCandidateId { get; init; } = string.Empty;

    public string SourceTargetItemId { get; init; } = string.Empty;

    public string? SourceLearningCaseId { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string SuggestedStableTarget { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public double Importance { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RiskFlags { get; init; } = Array.Empty<string>();

    public string ValidationStatus { get; init; } = StableReviewValidationStatuses.ReadyForReview;

    public DateTimeOffset CreatedAt { get; init; }

    public string Status { get; init; } = StableReviewCandidateStatuses.Candidate;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Stable review candidate 查询条件。</summary>
public sealed class StableReviewCandidateQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string? Status { get; init; }

    public string? ValidationStatus { get; init; }

    public string? Kind { get; init; }

    public string? SuggestedStableTarget { get; init; }

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

/// <summary>Stable review candidate 生成请求。</summary>
public sealed class StableReviewCandidateGenerationRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public int Limit { get; init; } = 100;

    public int Offset { get; init; }
}

/// <summary>Stable review candidate explain 响应。</summary>
public sealed class StableReviewCandidateExplanation
{
    public string StableReviewCandidateId { get; init; } = string.Empty;

    public StableReviewCandidate Candidate { get; init; } = new();

    public ShortTermPromotionCandidate SourceCandidate { get; init; } = new();

    public ContextLearningCase? SourceLearningCase { get; init; }

    public ContextMemoryItem? SourceMemoryTarget { get; init; }

    public ContextConstraint? SourceConstraintTarget { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string Reason { get; init; } = string.Empty;

    public string ValidationStatus { get; init; } = StableReviewValidationStatuses.ReadyForReview;

    public IReadOnlyList<string> RiskFlags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public static class StableReviewCandidateStatuses
{
    public const string Candidate = "Candidate";
    public const string NeedsMoreEvidence = "NeedsMoreEvidence";
    public const string Blocked = "Blocked";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
}

public static class StableReviewValidationStatuses
{
    public const string ReadyForReview = "ReadyForReview";
    public const string Ready = ReadyForReview;
    public const string NeedsMoreEvidence = "NeedsMoreEvidence";
    public const string SourceCandidateMissing = "SourceCandidateMissing";
    public const string SourceTargetMissing = "SourceTargetMissing";
    public const string TargetNotCandidate = "TargetNotCandidate";
    public const string DuplicateStableCandidate = "DuplicateStableCandidate";
    public const string ScopeMismatch = "ScopeMismatch";
    public const string LifecycleConflict = "LifecycleConflict";
}

/// <summary>Stable review 候选项的人工决策请求。</summary>
public sealed class StableReviewDecisionRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Stable review 候选项的人工决策结果。</summary>
public sealed class StableReviewDecisionResult
{
    public string OperationId { get; init; } = string.Empty;

    public string StableReviewCandidateId { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string Status { get; init; } = StableReviewCandidateStatuses.Candidate;

    public string ReviewId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset ReviewedAt { get; init; }

    public string? CreatedStableTargetItemId { get; init; }

    public string? CreatedTargetItemId { get; init; }

    public string? StableTargetItemKind { get; init; }

    public string? TargetLayer { get; init; }

    public string ValidationStatus { get; init; } = StableReviewValidationStatuses.ReadyForReview;

    public StableReviewCandidate Candidate { get; init; } = new();

    public StableReviewRecord Review { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>Stable review 候选项审核审计记录。</summary>
public sealed class StableReviewRecord
{
    public string ReviewId { get; init; } = string.Empty;

    public string StableReviewCandidateId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string Action { get; init; } = string.Empty;

    public string FromStatus { get; init; } = StableReviewCandidateStatuses.Candidate;

    public string ToStatus { get; init; } = StableReviewCandidateStatuses.Candidate;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? StableTargetItemId { get; init; }

    public string? StableTargetItemKind { get; init; }

    public string? TargetLayer { get; init; }

    public string SourcePromotionCandidateId { get; init; } = string.Empty;

    public string SourceTargetItemId { get; init; } = string.Empty;

    public string? SourceLearningCaseId { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string ValidationStatus { get; init; } = StableReviewValidationStatuses.ReadyForReview;

    public IReadOnlyList<string> RiskFlags { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ReviewedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
