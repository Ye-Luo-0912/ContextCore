namespace ContextCore.Abstractions.Models;


/// <summary>lifecycle metadata review summary 报告。</summary>
public sealed class VectorLifecycleMetadataReviewSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int CandidateCount { get; init; }

    public int PendingCount { get; init; }

    public int ApprovedForSidecarCount { get; init; }

    public int RejectedCount { get; init; }

    public int NeedsEvidenceCount { get; init; }

    public int SupersededCount { get; init; }

    public int SidecarEntryCount { get; init; }

    public int NormalContextApprovalCount { get; init; }

    public int AuditContextApprovalCount { get; init; }

    public int HistoricalContextApprovalCount { get; init; }

    public int DiagnosticsOnlyApprovalCount { get; init; }

    public int UnsafeApprovalBlockedCount { get; init; }

    public string Recommendation { get; init; } = VectorLifecycleMetadataRepairPlanRecommendations.NeedsHumanReview;

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<VectorLifecycleMetadataReviewRecord> RecentReviews { get; init; } =
        Array.Empty<VectorLifecycleMetadataReviewRecord>();
}
