using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;

public sealed class LearningFeedbackScopedServiceModeGateReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public bool ReadinessGatePassed { get; init; }

    public bool DualWriteSmokePassed { get; init; }

    public bool ShadowReadSmokePassed { get; init; }

    public bool ProviderQualityReady { get; init; }

    public bool ScopedAllowlistConfigured { get; init; }

    public bool NonAllowlistedScopeRemainsFileSystem { get; init; }

    public bool ExportProjectionParityPassed { get; init; }

    public bool SummaryParityPassed { get; init; }

    public bool FallbackTested { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int FallbackCount { get; init; }

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class LearningFeedbackSelectedNormalScopeCanaryReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool GatePassed { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderMode { get; init; } = LearningFeedbackProviderMode.GuardedPostgresPrimary.ToString();

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemFallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public bool ExportProjectionParityPassed { get; init; }

    public bool SummaryParityPassed { get; init; }

    public bool ReviewSummaryParityPassed { get; init; }

    public bool FeatureCandidateParityPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string RollbackInstruction { get; init; } =
        "remove selected learning feedback scope allowlist or set LearningFeedbackProviderSwitchOptions.Enabled=false";

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class LearningFeedbackLimitedScopeObservationReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool GatePassed { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int ObservationWindowMinutes { get; init; }

    public string ProviderMode { get; init; } = LearningFeedbackProviderMode.GuardedPostgresPrimary.ToString();

    public int OperationCount { get; init; }

    public int PostgresPrimaryReadCount { get; init; }

    public int PostgresPrimaryWriteCount { get; init; }

    public int FileSystemFallbackCount { get; init; }

    public int ComparisonTraceCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int ScopeLeakCount { get; init; }

    public double ErrorRate { get; init; }

    public double FallbackRate { get; init; }

    public bool ExportProjectionParityPassed { get; init; }

    public bool SummaryParityPassed { get; init; }

    public bool ReviewSummaryParityPassed { get; init; }

    public bool FeatureCandidateParityPassed { get; init; }

    public int TrainableCandidateLeakCount { get; init; }

    public int SmokeCandidateExcludedCount { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string RollbackInstruction { get; init; } =
        "remove limited learning feedback scope allowlist or set LearningFeedbackProviderSwitchOptions.Enabled=false";

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresJobQueueLeaseSmokeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int JobCount { get; init; }

    public int OperationCount { get; init; }

    public int MismatchCount { get; init; }

    public int PostgresFailureCount { get; init; }

    public int LeaseAcquireCount { get; init; }

    public int LeaseConflictCount { get; init; }

    public int LeaseExpiredReacquireCount { get; init; }

    public bool HeartbeatRenewalPassed { get; init; }

    public bool CompleteTransitionPassed { get; init; }

    public bool RetryTransitionPassed { get; init; }

    public bool DeadLetterTransitionPassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}
