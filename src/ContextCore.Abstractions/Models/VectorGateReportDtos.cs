namespace ContextCore.Abstractions.Models;

/// <summary>单个数据集的 vector lifecycle metadata repair preview 报告。</summary>
public sealed class VectorLifecycleMetadataRepairPlanReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetName { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public int CandidateCount { get; init; }

    public int AutoRepairableCount { get; init; }

    public int HumanReviewRequiredCount { get; init; }

    public int ForbiddenRepairCount { get; init; }

    public int CorrectlyBlockedSkippedCount { get; init; }

    public double EstimatedRecallRecovery { get; init; }

    public int RiskAfterRepairEstimate { get; init; }

    public string Recommendation { get; init; } = VectorLifecycleMetadataRepairPlanRecommendations.KeepPreviewOnly;

    public IReadOnlyList<VectorLifecycleMetadataRepairCandidate> Candidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataRepairCandidate>();
}

/// <summary>A3 / Extended vector lifecycle metadata repair preview 汇总。</summary>
public sealed class VectorLifecycleMetadataRepairPlanSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<VectorLifecycleMetadataRepairPlanReport> Reports { get; init; } =
        Array.Empty<VectorLifecycleMetadataRepairPlanReport>();

    public int CandidateCount { get; init; }

    public int AutoRepairableCount { get; init; }

    public int HumanReviewRequiredCount { get; init; }

    public int ForbiddenRepairCount { get; init; }

    public int CorrectlyBlockedSkippedCount { get; init; }

    public double EstimatedRecallRecovery { get; init; }

    public int RiskAfterRepairEstimate { get; init; }

    public string Recommendation { get; init; } = VectorLifecycleMetadataRepairPlanRecommendations.KeepPreviewOnly;

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}

/// <summary>vector lifecycle metadata review 决策类型。</summary>
public static class VectorLifecycleMetadataReviewDecisions
{
    public const string ApproveForSidecar = nameof(ApproveForSidecar);
    public const string Reject = nameof(Reject);
    public const string NeedsEvidence = nameof(NeedsEvidence);
    public const string Supersede = nameof(Supersede);
}
