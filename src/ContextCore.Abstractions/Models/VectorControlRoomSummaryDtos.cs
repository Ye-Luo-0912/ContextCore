namespace ContextCore.Abstractions.Models;


/// <summary>A3 / Extended retrieval dataset alignment audit 汇总。</summary>
public sealed class RetrievalDatasetAlignmentAuditSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<RetrievalDatasetAlignmentAuditReport> Reports { get; init; } =
        Array.Empty<RetrievalDatasetAlignmentAuditReport>();

    public string Recommendation { get; init; } = RetrievalDatasetAlignmentRecommendations.KeepPreviewOnly;

    public int AlignmentIssueCount { get; init; }

    public IReadOnlyDictionary<string, int> IssueBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}


/// <summary>A3 / Extended lifecycle-filtered mustHit triage 汇总。</summary>
public sealed class VectorEligibilityRecallLossTriageSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<VectorEligibilityRecallLossTriageReport> Reports { get; init; } =
        Array.Empty<VectorEligibilityRecallLossTriageReport>();

    public int TotalFilteredMustHit { get; init; }

    public int CorrectlyBlockedCount { get; init; }

    public int RouteToHistoricalCount { get; init; }

    public int RouteToAuditCount { get; init; }

    public int MetadataRepairNeededCount { get; init; }

    public int EvalExpectationReviewNeededCount { get; init; }

    public int UnsafeToRecoverCount { get; init; }

    public int RecoverableWithoutNormalContextCount { get; init; }

    public int RecoverableToNormalContextCount { get; init; }

    public string Recommendation { get; init; } = VectorEligibilityRecallLossTriageRecommendations.KeepPreviewOnly;

    public IReadOnlyDictionary<string, int> CategoryBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}


/// <summary>Retrieval Dataset V2 shadow eval 汇总报告。</summary>
public sealed class RetrievalDatasetV2ShadowEvalSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public string CorpusHash { get; init; } = string.Empty;

    public string SamplesHash { get; init; } = string.Empty;

    public int CorpusItemCount { get; init; }

    public int SampleCount { get; init; }

    public string BestProfileName { get; init; } = string.Empty;

    public double BestRecallAfterPolicy { get; init; }

    public double BestMrrAfterPolicy { get; init; }

    public int BestRiskAfterPolicy { get; init; }

    public bool PgVectorParityPassed { get; init; }

    public double PgVectorTopKOverlapRate { get; init; }

    public int PgVectorOrderingMismatchCount { get; init; }

    public double PgVectorScoreDeltaMax { get; init; }

    public int PgVectorMetadataMismatchCount { get; init; }

    public int PgVectorEligibilityMetadataMismatchCount { get; init; }

    public int PgVectorRiskProjectionMismatchCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ShadowEvalRecommendations.KeepPreviewOnly;

    public IReadOnlyList<RetrievalDatasetV2ShadowEvalProfileReport> Profiles { get; init; } =
        Array.Empty<RetrievalDatasetV2ShadowEvalProfileReport>();
}


/// <summary>Scoped runtime experiment approval summary；只读取 approval artifact。</summary>
public sealed class ScopedRuntimeExperimentApprovalSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public int ApprovalCount { get; init; }

    public bool ApprovalRecordExists { get; init; }

    public string LatestApprovalId { get; init; } = string.Empty;

    public string ApprovalMode { get; init; } = string.Empty;

    public bool Expired { get; init; }

    public bool Revoked { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.NeedsManualApproval;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


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
