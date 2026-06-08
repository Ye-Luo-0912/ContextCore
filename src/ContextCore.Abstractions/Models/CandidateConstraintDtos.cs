using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>CandidateConstraint 查询条件。</summary>
public sealed class CandidateConstraintQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public ContextMemoryStatus? Status { get; init; } = ContextMemoryStatus.Candidate;

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

/// <summary>CandidateConstraint 人工审核请求。</summary>
public sealed class CandidateConstraintReviewRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>CandidateConstraint 人工审核结果。</summary>
public sealed class CandidateConstraintReviewResult
{
    public string OperationId { get; init; } = string.Empty;

    public string ConstraintId { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public ContextMemoryStatus Status { get; init; } = ContextMemoryStatus.Candidate;

    public string ReviewId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset ReviewedAt { get; init; }

    public string? ActivatedConstraintId { get; init; }

    public string? TargetLayer { get; init; }

    public ContextConstraint Constraint { get; init; } = new();

    public CandidateConstraintReviewRecord Review { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>CandidateConstraint 审核审计记录。</summary>
public sealed class CandidateConstraintReviewRecord
{
    public string ReviewId { get; init; } = string.Empty;

    public string ConstraintId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string Action { get; init; } = string.Empty;

    public ContextMemoryStatus FromStatus { get; init; } = ContextMemoryStatus.Candidate;

    public ContextMemoryStatus ToStatus { get; init; } = ContextMemoryStatus.Candidate;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? ActivatedConstraintId { get; init; }

    public string SourceConstraintGapId { get; init; } = string.Empty;

    public string SourceSampleId { get; init; } = string.Empty;

    public string SourceOperationId { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ReviewedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
