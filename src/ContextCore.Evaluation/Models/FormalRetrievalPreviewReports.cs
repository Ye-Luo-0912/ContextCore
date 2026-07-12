using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;


/// <summary>Guarded formal retrieval preview / gate；只输出 would-change 结果，不写正式 package。</summary>
public sealed class GuardedFormalRetrievalPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool V4RecheckPassed { get; init; }

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int BaselineCandidateCount { get; init; }

    public int PreviewVectorCandidateCount { get; init; }

    public int WouldAddCount { get; init; }

    public int WouldRemoveCount { get; init; }

    public int WouldRerankCount { get; init; }

    public int WouldChangeTargetSectionCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } = GuardedFormalRetrievalPreviewRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Scoped formal preview opt-in recommendation。</summary>
public static class ScopedFormalPreviewOptInRecommendations
{
    public const string ReadyForLimitedFormalPreviewObservation = nameof(ReadyForLimitedFormalPreviewObservation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
}


/// <summary>Scoped formal preview opt-in report；只写 shadow artifact，不写正式 package。</summary>
public sealed class ScopedFormalPreviewOptInReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public bool SmokePassed { get; init; }

    public bool GatePassed { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.Off;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string SelectedWorkspaceId { get; init; } = string.Empty;

    public string SelectedCollectionId { get; init; } = string.Empty;

    public string SelectedEvalScope { get; init; } = string.Empty;

    public string NonAllowlistedWorkspaceId { get; init; } = string.Empty;

    public string NonAllowlistedCollectionId { get; init; } = string.Empty;

    public string NonAllowlistedEvalScope { get; init; } = string.Empty;

    public int ScopeCount { get; init; }

    public int AllowlistedScopeCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int PreviewPackageCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string RollbackInstruction { get; init; } = string.Empty;

    public string Recommendation { get; init; } = ScopedFormalPreviewOptInRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> GateDependencySummary { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Limited formal preview observation recommendation。</summary>
public static class LimitedFormalPreviewObservationRecommendations
{
    public const string ReadyForFormalPreviewFreeze = nameof(ReadyForFormalPreviewFreeze);
    public const string NeedsMoreObservation = nameof(NeedsMoreObservation);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Limited formal preview observation report；聚合多轮 preview-only package comparison。</summary>
public sealed class LimitedFormalPreviewObservationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ObservationPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.PreviewOnly;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int MinimumObservationRunCount { get; init; }

    public int ObservationRunCount { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public int PreviewPackageCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int CandidateUnchangedCount { get; init; }

    public int SectionChangedCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int TokenDeltaP95 { get; init; }

    public double ConstraintCoverageDelta { get; init; }

    public double RelationCoverageDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } = LimitedFormalPreviewObservationRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
