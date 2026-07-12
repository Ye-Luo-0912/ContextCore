using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;


/// <summary>受控应用合并 dry-run 观察报告。</summary>
public sealed class ControlledAppliedMergeDryRunObservationReport
{
    public string OperationId { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ObservationPassed { get; init; }

    public string Recommendation { get; init; }
        = ControlledAppliedMergeDryRunDecisionRecommendations.KeepDryRunOnly;

    public string ProposalSourcePath { get; init; } = "";

    public int ObservationRuns { get; init; }

    public int WouldApplyAddCount { get; init; }

    public int WouldApplyRemoveCount { get; init; }

    public int AppliedAddCount { get; init; }

    public int AppliedRemoveCount { get; init; }

    public int MaxAddPerSample { get; init; }

    public int MaxRemovePerSample { get; init; }

    public int TotalTokenDelta { get; init; }

    public int MaxTokenDeltaPerSample { get; init; }

    public int SectionChangedCount { get; init; }

    public int PriorityChangedCount { get; init; }

    public bool RollbackPassed { get; init; }

    public bool KillSwitchTested { get; init; }

    public bool StopConditionsChecked { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int SectionMismatchCount { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>受控应用合并批准推荐。</summary>
public static class ControlledAppliedMergeApprovalRecommendations
{
    public const string ReadyForScopedPreview = nameof(ReadyForScopedPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingDryRunDecision = nameof(BlockedByMissingDryRunDecision);
    public const string BlockedByRiskAcknowledgementRequired = nameof(BlockedByRiskAcknowledgementRequired);
    public const string BlockedByRollbackAcknowledgementRequired = nameof(BlockedByRollbackAcknowledgementRequired);
}


/// <summary>受控应用合并批准记录。</summary>
public sealed class ControlledAppliedMergeApprovalReport
{
    public string OperationId { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ApprovalPassed { get; init; }

    public string Recommendation { get; init; }
        = ControlledAppliedMergeApprovalRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = "";

    public string ApprovedBy { get; init; } = "";

    public string Reason { get; init; } = "";

    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddDays(7);

    public string ApprovalMode { get; init; } = "ControlledAppliedMergePreviewOnly";

    public string DryRunDecisionSourcePath { get; init; } = "";

    public int WouldApplyAddCount { get; init; }

    public int WouldApplyRemoveCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public bool RollbackPresent { get; init; }

    public bool KillSwitchPresent { get; init; }

    public bool IsRevoked { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>受控应用合并范围预览推荐。</summary>
public static class ControlledAppliedMergeScopedPreviewRecommendations
{
    public const string ReadyForControlledAppliedMergeScopedPreviewGate = nameof(ReadyForControlledAppliedMergeScopedPreviewGate);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByApprovalExpiredOrRevoked = nameof(BlockedByApprovalExpiredOrRevoked);
    public const string BlockedByPreviewSelectedSetUnchanged = nameof(BlockedByPreviewSelectedSetUnchanged);
    public const string BlockedByFormalSelectedSetChanged = nameof(BlockedByFormalSelectedSetChanged);
    public const string BlockedByRiskAfterPolicy = nameof(BlockedByRiskAfterPolicy);
}


/// <summary>受控应用合并范围预览报告。</summary>
public sealed class ControlledAppliedMergeScopedPreviewReport
{
    public string OperationId { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; }
        = ControlledAppliedMergeScopedPreviewRecommendations.BlockedByMissingApproval;

    public string ApprovalSourcePath { get; init; } = "";

    public string DryRunDecisionSourcePath { get; init; } = "";

    public bool PreviewSelectedSetChanged { get; init; }

    public int PreviewAddCount { get; init; }

    public int PreviewRemoveCount { get; init; }

    public int AppliedFormalAddCount { get; init; }

    public int AppliedFormalRemoveCount { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public bool RollbackPresent { get; init; }

    public bool KillSwitchPresent { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}
