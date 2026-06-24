namespace ContextCore.Abstractions.Models;

/// <summary>短期记忆生成的只读晋升候选项。</summary>
public sealed class ShortTermPromotionCandidate
{
    public string CandidateId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string SourceWorkingItemId { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string SuggestedTargetLayer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public double Importance { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public PromotionCandidateStatus Status { get; init; } = PromotionCandidateStatus.Candidate;

    public string DedupeKey { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public string GeneratedBy { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;

    public string RuleName { get; init; } = string.Empty;

    public string RuleVersion { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>短期晋升候选项查询条件。</summary>
public sealed class ShortTermPromotionCandidateQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public PromotionCandidateStatus? Status { get; init; }

    public string? Kind { get; init; }

    public string? SuggestedTargetLayer { get; init; }

    public double? MinConfidence { get; init; }

    public double? MinImportance { get; init; }

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

/// <summary>生成短期晋升候选项的请求。</summary>
public sealed class ShortTermPromotionCandidateGenerationRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }
}

/// <summary>短期晋升候选项 explain 响应。</summary>
public sealed class ShortTermPromotionCandidateExplanation
{
    public string CandidateId { get; init; } = string.Empty;

    public ShortTermPromotionCandidate Candidate { get; init; } = new();

    public ShortTermWorkingItem SourceWorkingItem { get; init; } = new();

    public IReadOnlyList<ShortTermRawEvent> SourceRawEvents { get; init; } = Array.Empty<ShortTermRawEvent>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string Reason { get; init; } = string.Empty;

    public string RuleName { get; init; } = string.Empty;

    public string RuleVersion { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public double Importance { get; init; }

    public string SuggestedTargetLayer { get; init; } = string.Empty;

    public string DedupeKey { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public string GeneratedBy { get; init; } = string.Empty;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>短期晋升候选项的人工审核请求。</summary>
public class PromotionCandidateReviewRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>短期晋升候选项审核结果。</summary>
public class PromotionCandidateReviewResult
{
    public string OperationId { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public PromotionCandidateStatus Status { get; init; } = PromotionCandidateStatus.Candidate;

    public string ReviewId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset ReviewedAt { get; init; }

    public string? TargetItemId { get; init; }

    public string? CreatedTargetItemId { get; init; }

    public string? TargetItemKind { get; init; }

    public string? TargetLayer { get; init; }

    public ShortTermPromotionCandidate Candidate { get; init; } = new();

    public PromotionCandidateReviewRecord Review { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>短期晋升候选项的人工审核请求。</summary>
public sealed class ReviewPromotionCandidateRequest : PromotionCandidateReviewRequest;

/// <summary>短期晋升候选项审核结果。</summary>
public sealed class ReviewPromotionCandidateResponse : PromotionCandidateReviewResult;

/// <summary>短期晋升候选项审核审计记录。</summary>
public sealed class PromotionCandidateReviewRecord
{
    public string ReviewId { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string Action { get; init; } = string.Empty;

    public PromotionCandidateStatus FromStatus { get; init; } = PromotionCandidateStatus.Candidate;

    public PromotionCandidateStatus ToStatus { get; init; } = PromotionCandidateStatus.Candidate;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? TargetItemId { get; init; }

    public string? TargetItemKind { get; init; }

    public string? TargetLayer { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ReviewedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
