namespace ContextCore.Abstractions;

/// <summary>约束语料缺口候选项，用于人工复核缺失的 hard constraint，不直接写入 ConstraintStore。</summary>
public sealed class ConstraintGapCandidate
{
    public string GapId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string Source { get; init; } = string.Empty;

    public string SourceSampleId { get; init; } = string.Empty;

    public string SourceOperationId { get; init; } = string.Empty;

    public string ExpectedConstraintText { get; init; } = string.Empty;

    public IReadOnlyList<string> MatchedConstraintIds { get; init; } = Array.Empty<string>();

    public string SuggestedConstraintTitle { get; init; } = string.Empty;

    public string SuggestedConstraintScope { get; init; } = "Collection";

    public string SuggestedConstraintType { get; init; } = "Hard";

    public string Severity { get; init; } = ConstraintGapSeverity.High;

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string Status { get; init; } = ConstraintGapStatus.Pending;

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>约束缺口候选项查询条件。</summary>
public sealed class ConstraintGapCandidateQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string? Source { get; init; }

    public string? SourceSampleId { get; init; }

    public string? Status { get; init; }

    public string? Severity { get; init; }

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

/// <summary>从 eval/report 输入生成约束缺口候选项的请求。</summary>
public sealed class ConstraintGapGenerationRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string? PlanningConstraintReportPath { get; init; }

    public string? ExtendedFailureTriageReportPath { get; init; }

    public bool IncludePlanningConstraintReport { get; init; } = true;

    public bool IncludeExtendedFailureTriageReport { get; init; } = true;

    public int Limit { get; init; } = 200;
}

/// <summary>约束缺口候选项生成结果。</summary>
public sealed class ConstraintGapGenerationResult
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int ScannedSampleCount { get; init; }

    public int MissingConstraintCount { get; init; }

    public int CreatedCount { get; init; }

    public int ExistingCount { get; init; }

    public int SkippedMatchedCount { get; init; }

    public IReadOnlyList<ConstraintGapCandidate> Gaps { get; init; } = Array.Empty<ConstraintGapCandidate>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>约束缺口候选项的人工审核请求。</summary>
public sealed class ConstraintGapReviewRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>约束缺口候选项审核结果。</summary>
public sealed class ConstraintGapReviewResult
{
    public string OperationId { get; init; } = string.Empty;

    public string GapId { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string Status { get; init; } = ConstraintGapStatus.Pending;

    public string ReviewId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset ReviewedAt { get; init; }

    public string? CreatedConstraintId { get; init; }

    public string? TargetItemId { get; init; }

    public string? TargetItemKind { get; init; }

    public string? TargetLayer { get; init; }

    public ConstraintGapCandidate Gap { get; init; } = new();

    public ConstraintGapReviewRecord Review { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>约束缺口候选项审核审计记录。</summary>
public sealed class ConstraintGapReviewRecord
{
    public string ReviewId { get; init; } = string.Empty;

    public string GapId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string Action { get; init; } = string.Empty;

    public string FromStatus { get; init; } = ConstraintGapStatus.Pending;

    public string ToStatus { get; init; } = ConstraintGapStatus.Pending;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? CreatedConstraintId { get; init; }

    public string? TargetItemKind { get; init; }

    public string? TargetLayer { get; init; }

    public string SourceSampleId { get; init; } = string.Empty;

    public string SourceOperationId { get; init; } = string.Empty;

    public string ExpectedConstraintText { get; init; } = string.Empty;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ReviewedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public static class ConstraintGapStatus
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
    public const string Reviewed = "Reviewed";
    public const string Dismissed = "Dismissed";
}

public static class ConstraintGapSeverity
{
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";
}
