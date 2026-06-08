using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>稳定对象来源链查询响应。</summary>
public sealed class ContextProvenanceResponse
{
    public string ItemId { get; init; } = string.Empty;

    public string TargetItemKind { get; init; } = string.Empty;

    public ContextMemoryItem? TargetMemoryItem { get; init; }

    public ContextConstraint? TargetConstraint { get; init; }

    public StableReviewCandidate? StableReviewCandidate { get; init; }

    public ShortTermPromotionCandidate? PromotionCandidate { get; init; }

    public PromotionFeedbackSignal? FeedbackSignal { get; init; }

    public ContextLearningCase? LearningCase { get; init; }

    public ShortTermWorkingItem? SourceWorkingItem { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<StableReviewRecord> StableReviewHistory { get; init; } = Array.Empty<StableReviewRecord>();

    public IReadOnlyList<PromotionCandidateReviewRecord> PromotionReviewHistory { get; init; } = Array.Empty<PromotionCandidateReviewRecord>();

    public IReadOnlyList<StableDiagnosticWarning> Diagnostics { get; init; } = Array.Empty<StableDiagnosticWarning>();

    public IReadOnlyList<string> MissingLinks { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>稳定来源链只读诊断警告。</summary>
public sealed class StableDiagnosticWarning
{
    public string Code { get; init; } = string.Empty;

    public string Severity { get; init; } = "Warning";

    public string Message { get; init; } = string.Empty;

    public string? TargetItemId { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
