using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;


/// <summary>Scoped runtime experiment dry-run observation recommendation。</summary>
public static class ScopedRuntimeExperimentDryRunObservationRecommendations
{
    public const string ReadyForScopedRuntimeExperimentDesignFreeze = nameof(ReadyForScopedRuntimeExperimentDesignFreeze);
    public const string NeedsMoreDryRunObservation = nameof(NeedsMoreDryRunObservation);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByFormalPackageWrite = nameof(BlockedByFormalPackageWrite);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingMutation = nameof(BlockedByVectorStoreBindingMutation);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Scoped runtime experiment dry-run observation report；只聚合 dry-run 观测和边界检查。</summary>
public sealed class ScopedRuntimeExperimentDryRunObservationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ObservationPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Mode { get; init; } = ScopedRuntimeExperimentDryRunObservationModes.DryRun;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int ObservationRunCount { get; init; }

    public int MinimumObservationRunCount { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public int AllowlistedScopeCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int DryRunPackageCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool RollbackPlanAvailable { get; init; }

    public bool RuntimeChangeGateConsistent { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } =
        ScopedRuntimeExperimentDryRunObservationRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Explicit scoped runtime experiment proposal recommendation。</summary>
public static class ExplicitScopedRuntimeExperimentProposalRecommendations
{
    public const string ReadyForManualExperimentApproval = nameof(ReadyForManualExperimentApproval);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Explicit scoped runtime experiment proposal report；不写 runtime 配置。</summary>
public sealed class ExplicitScopedRuntimeExperimentProposalReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public bool ProposalPassed { get; init; }

    public string Recommendation { get; init; } =
        ExplicitScopedRuntimeExperimentProposalRecommendations.KeepPreviewOnly;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string EvalScopeId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyDictionary<string, string> RequiredGateSummary { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ProposedConfigPatch { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string RollbackPlan { get; init; } = string.Empty;

    public string KillSwitchPlan { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> ObservationPlan { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool ApprovalRequired { get; init; }

    public bool Approved { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool ConfigPatchWritten { get; init; }

    public bool DiBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Scoped runtime experiment approval preview/write report。</summary>
public sealed class ScopedRuntimeExperimentApprovalReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public bool ApprovalPassed { get; init; }

    public bool PreviewOnly { get; init; } = true;

    public bool RecordWritten { get; init; }

    public bool Confirmed { get; init; }

    public string ApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly;

    public string ApprovedBy { get; init; } = string.Empty;

    public bool RollbackPlanAvailable { get; init; }

    public bool KillSwitchPlanAvailable { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalPackageWriteAllowed { get; init; }

    public bool PackingPolicyChangeAllowed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly;

    public ScopedRuntimeExperimentApprovalRecord? ApprovalRecord { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>V4.12 runtime approval request preview；只展示审批材料，不写 approval record。</summary>
public sealed class ScopedRuntimeExperimentApprovalRequestPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public string RequiredApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public string ProfileName { get; init; } = string.Empty;

    public string RollbackPlan { get; init; } = string.Empty;

    public string KillSwitchPlan { get; init; } = string.Empty;

    public IReadOnlyList<string> ObservationPlan { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();

    public bool PreviewOnly { get; init; } = true;

    public bool RecordWritten { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.NeedsManualApproval;
}


/// <summary>V4.13 activation preflight recommendation。</summary>
public static class ScopedRuntimeExperimentActivationPreflightRecommendations
{
    public const string ReadyForGuardedScopedRuntimeExperiment = nameof(ReadyForGuardedScopedRuntimeExperiment);
    public const string NeedsActivationConfig = nameof(NeedsActivationConfig);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingTraceSink = nameof(BlockedByMissingTraceSink);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingMutation = nameof(BlockedByVectorStoreBindingMutation);
    public const string BlockedByFormalPackageWrite = nameof(BlockedByFormalPackageWrite);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V4.13 activation preflight / guarded runtime dry-run route report。</summary>
public sealed class ScopedRuntimeExperimentActivationPreflightReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreflightPassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentActivationPreflightRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentActivationPreflightModes.PreflightAndDryRunRoute;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public bool KillSwitchAvailable { get; init; }

    public bool RollbackPlanAvailable { get; init; }

    public bool TraceSinkAvailable { get; init; }

    public bool ConfigPatchPreviewed { get; init; }

    public bool ConfigPatchWritten { get; init; }

    public bool RuntimeRouteDryRunExecuted { get; init; }

    public int DryRunRouteHitCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>V4.14 guarded scoped runtime experiment recommendation。</summary>
public static class GuardedScopedRuntimeExperimentRecommendations
{
    public const string ReadyForScopedRuntimeExperimentObservation = nameof(ReadyForScopedRuntimeExperimentObservation);
    public const string NeedsMoreExperimentRuns = nameof(NeedsMoreExperimentRuns);
    public const string BlockedByMissingActivationGate = nameof(BlockedByMissingActivationGate);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByWrongApprovalMode = nameof(BlockedByWrongApprovalMode);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingMutation = nameof(BlockedByVectorStoreBindingMutation);
    public const string BlockedByFormalPackageWrite = nameof(BlockedByFormalPackageWrite);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByRollbackFailure = nameof(BlockedByRollbackFailure);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V4.14 guarded scoped runtime experiment report；正式 retrieval/package 保持不变。</summary>
public sealed class GuardedScopedRuntimeExperimentReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ExperimentPassed { get; init; }

    public string Recommendation { get; init; } = GuardedScopedRuntimeExperimentRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string ApprovalMode { get; init; } = string.Empty;

    public string Mode { get; init; } = GuardedScopedRuntimeExperimentModes.ShadowRuntimeExperiment;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public int RequestCount { get; init; }

    public int ExperimentRouteHitCount { get; init; }

    public int NonAllowlistedRequestCount { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int ExperimentPreviewPackageCount { get; init; }

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

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool KillSwitchAvailable { get; init; }

    public bool KillSwitchTriggered { get; init; }

    public bool RollbackVerified { get; init; }

    public int ErrorCount { get; init; }

    public int LatencyP50 { get; init; }

    public int LatencyP95 { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool GlobalDefaultOn { get; init; }

    public IReadOnlyList<ScopedRuntimeExperimentTrace> Traces { get; init; } = Array.Empty<ScopedRuntimeExperimentTrace>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>V4.15 scoped runtime experiment observation window recommendation。</summary>
public static class ScopedRuntimeExperimentObservationWindowRecommendations
{
    public const string ReadyForScopedRuntimeExperimentObservationFreeze = nameof(ReadyForScopedRuntimeExperimentObservationFreeze);
    public const string NeedsMoreObservation = nameof(NeedsMoreObservation);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByTraceGap = nameof(BlockedByTraceGap);
    public const string BlockedByLatency = nameof(BlockedByLatency);
    public const string BlockedByRollbackFailure = nameof(BlockedByRollbackFailure);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V4.15 scoped runtime experiment observation window report；只写 shadow artifact/trace。</summary>
public sealed class ScopedRuntimeExperimentObservationWindowReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ObservationWindowId { get; init; } = string.Empty;

    public bool ObservationPassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentObservationWindowRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string Mode { get; init; } = ScopedRuntimeExperimentObservationWindowModes.ScopedShadowObservation;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public int ObservationRunCount { get; init; }

    public int RequestCount { get; init; }

    public int ExperimentRouteHitCount { get; init; }

    public int NonAllowlistedRequestCount { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int ExperimentPreviewPackageCount { get; init; }

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

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool KillSwitchAvailable { get; init; }

    public bool KillSwitchSmokePassed { get; init; }

    public bool RollbackVerified { get; init; }

    public double TraceCompleteness { get; init; }

    public int ErrorCount { get; init; }

    public int LatencyP50 { get; init; }

    public int LatencyP95 { get; init; }

    public bool StopConditionTriggered { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool GlobalDefaultOn { get; init; }

    public IReadOnlyList<ScopedRuntimeExperimentTrace> Traces { get; init; } = Array.Empty<ScopedRuntimeExperimentTrace>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>Scoped runtime experiment no-op harness report；不改变正式 retrieval/package。</summary>
public sealed class ScopedRuntimeExperimentNoOpHarnessReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public bool HarnessPassed { get; init; }

    public string Mode { get; init; } = ScopedRuntimeExperimentNoOpHarnessModes.NoOp;

    public bool SelectedScopeChecked { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int NoOpTraceCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int PreviewPackageCount { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool DiBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool P15GatePassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>Guarded scoped runtime experiment plan recommendation。</summary>
public static class GuardedScopedRuntimeExperimentPlanRecommendations
{
    public const string ReadyForScopedRuntimeExperimentActivationContract = nameof(ReadyForScopedRuntimeExperimentActivationContract);
    public const string NeedsScopeConfiguration = nameof(NeedsScopeConfiguration);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingObservationPlan = nameof(BlockedByMissingObservationPlan);
    public const string BlockedByUnsafeApprovalMode = nameof(BlockedByUnsafeApprovalMode);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}
