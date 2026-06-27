using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class RuntimeActivationArtifactIntegrityStatuses
{
    public const string RuntimeActivationArtifactIntegrityVerified = nameof(RuntimeActivationArtifactIntegrityVerified);
    public const string RuntimeActivationArtifactIntegrityBlocked = nameof(RuntimeActivationArtifactIntegrityBlocked);
}

public static class RuntimeActivationArtifactIntegrityBlockedReasons
{
    public const string GuardedRuntimeActivationArtifactWriteOutGateMissing = nameof(GuardedRuntimeActivationArtifactWriteOutGateMissing);
    public const string GuardedRuntimeActivationArtifactWriteOutGateNotPassed = nameof(GuardedRuntimeActivationArtifactWriteOutGateNotPassed);
    public const string GuardedRuntimeActivationDryRunGateMissing = nameof(GuardedRuntimeActivationDryRunGateMissing);
    public const string GuardedRuntimeActivationDryRunGateNotPassed = nameof(GuardedRuntimeActivationDryRunGateNotPassed);
    public const string RuntimeActivationDryRunGateMissing = nameof(RuntimeActivationDryRunGateMissing);
    public const string RuntimeActivationDryRunGateNotPassed = nameof(RuntimeActivationDryRunGateNotPassed);
    public const string RuntimeActivationArtifactsWrittenFalse = nameof(RuntimeActivationArtifactsWrittenFalse);
    public const string WrittenArtifactCountNotFive = nameof(WrittenArtifactCountNotFive);
    public const string BoundGrantIdEmpty = nameof(BoundGrantIdEmpty);
    public const string BoundCapabilityMismatch = nameof(BoundCapabilityMismatch);
    public const string BoundScopeMismatch = nameof(BoundScopeMismatch);
    public const string RuntimeActivationTrueInUpstream = nameof(RuntimeActivationTrueInUpstream);
    public const string FormalRetrievalAllowedTrueInUpstream = nameof(FormalRetrievalAllowedTrueInUpstream);
    public const string RuntimeSwitchAllowedTrueInUpstream = nameof(RuntimeSwitchAllowedTrueInUpstream);
    public const string ConfigPatchAppliedToRuntimeTrueInUpstream = nameof(ConfigPatchAppliedToRuntimeTrueInUpstream);
    public const string PackageOutputChangedTrueInUpstream = nameof(PackageOutputChangedTrueInUpstream);
    public const string FormalPackageWrittenTrueInUpstream = nameof(FormalPackageWrittenTrueInUpstream);
    public const string VectorStoreBindingChangedTrueInUpstream = nameof(VectorStoreBindingChangedTrueInUpstream);
    public const string GlobalDefaultOnTrueInUpstream = nameof(GlobalDefaultOnTrueInUpstream);
    public const string NoRuntimeMutationInvariantFalseInUpstream = nameof(NoRuntimeMutationInvariantFalseInUpstream);
    public const string RuntimeSwitchArtifactMissing = nameof(RuntimeSwitchArtifactMissing);
    public const string ActivationAuditArtifactMissing = nameof(ActivationAuditArtifactMissing);
    public const string RuntimeGuardManifestMissing = nameof(RuntimeGuardManifestMissing);
    public const string ScopeEnforcementManifestMissing = nameof(ScopeEnforcementManifestMissing);
    public const string ActivationRollbackBindingMissing = nameof(ActivationRollbackBindingMissing);
    public const string RuntimeSwitchBoundGrantIdMismatch = nameof(RuntimeSwitchBoundGrantIdMismatch);
    public const string RuntimeSwitchCapabilityMismatch = nameof(RuntimeSwitchCapabilityMismatch);
    public const string RuntimeSwitchScopeMismatch = nameof(RuntimeSwitchScopeMismatch);
    public const string RuntimeSwitchModeNotGuardedArtifactOnly = nameof(RuntimeSwitchModeNotGuardedArtifactOnly);
    public const string RuntimeSwitchApplyToRuntimeTrue = nameof(RuntimeSwitchApplyToRuntimeTrue);
    public const string RuntimeSwitchRuntimeActivationTrue = nameof(RuntimeSwitchRuntimeActivationTrue);
    public const string RuntimeSwitchFormalRetrievalAllowedTrue = nameof(RuntimeSwitchFormalRetrievalAllowedTrue);
    public const string RuntimeSwitchRuntimeSwitchAllowedTrue = nameof(RuntimeSwitchRuntimeSwitchAllowedTrue);
    public const string RuntimeSwitchSourceOperationMismatch = nameof(RuntimeSwitchSourceOperationMismatch);
    public const string ActivationAuditEventTypeMismatch = nameof(ActivationAuditEventTypeMismatch);
    public const string ActivationAuditBoundGrantIdMismatch = nameof(ActivationAuditBoundGrantIdMismatch);
    public const string ActivationAuditCapabilityMismatch = nameof(ActivationAuditCapabilityMismatch);
    public const string ActivationAuditScopeMismatch = nameof(ActivationAuditScopeMismatch);
    public const string ActivationAuditRuntimeActivationArtifactsWrittenFalse = nameof(ActivationAuditRuntimeActivationArtifactsWrittenFalse);
    public const string ActivationAuditRuntimeActivationTrue = nameof(ActivationAuditRuntimeActivationTrue);
    public const string ActivationAuditFormalRetrievalAllowedTrue = nameof(ActivationAuditFormalRetrievalAllowedTrue);
    public const string ActivationAuditRuntimeSwitchAllowedTrue = nameof(ActivationAuditRuntimeSwitchAllowedTrue);
    public const string ActivationAuditSourceOperationMismatch = nameof(ActivationAuditSourceOperationMismatch);
    public const string RuntimeGuardManifestBoundGrantIdMismatch = nameof(RuntimeGuardManifestBoundGrantIdMismatch);
    public const string RuntimeGuardManifestScopeMismatch = nameof(RuntimeGuardManifestScopeMismatch);
    public const string RuntimeGuardManifestKillSwitchRequiredFalse = nameof(RuntimeGuardManifestKillSwitchRequiredFalse);
    public const string RuntimeGuardManifestScopeGuardRequiredFalse = nameof(RuntimeGuardManifestScopeGuardRequiredFalse);
    public const string RuntimeGuardManifestRollbackRequiredFalse = nameof(RuntimeGuardManifestRollbackRequiredFalse);
    public const string RuntimeGuardManifestRuntimeActivationAllowedTrue = nameof(RuntimeGuardManifestRuntimeActivationAllowedTrue);
    public const string ScopeEnforcementManifestBoundGrantIdMismatch = nameof(ScopeEnforcementManifestBoundGrantIdMismatch);
    public const string ScopeEnforcementManifestAllowedScopeMismatch = nameof(ScopeEnforcementManifestAllowedScopeMismatch);
    public const string ScopeEnforcementManifestGlobalDefaultOnTrue = nameof(ScopeEnforcementManifestGlobalDefaultOnTrue);
    public const string ScopeEnforcementManifestWildcardScopeAllowedTrue = nameof(ScopeEnforcementManifestWildcardScopeAllowedTrue);
    public const string ActivationRollbackBindingBoundGrantIdMismatch = nameof(ActivationRollbackBindingBoundGrantIdMismatch);
    public const string ActivationRollbackBindingRollbackSnapshotReferenceMissing = nameof(ActivationRollbackBindingRollbackSnapshotReferenceMissing);
    public const string ActivationRollbackBindingRevocationRecordReferenceMissing = nameof(ActivationRollbackBindingRevocationRecordReferenceMissing);
    public const string ActivationRollbackBindingConfigPatchSourceReferenceMissing = nameof(ActivationRollbackBindingConfigPatchSourceReferenceMissing);
    public const string ActivationRollbackBindingRestoreTestRequiredFalse = nameof(ActivationRollbackBindingRestoreTestRequiredFalse);
    public const string ActivationRollbackBindingRuntimeActivationTrue = nameof(ActivationRollbackBindingRuntimeActivationTrue);
    public const string RollbackSnapshotReferenceNotFound = nameof(RollbackSnapshotReferenceNotFound);
    public const string RevocationRecordReferenceNotFound = nameof(RevocationRecordReferenceNotFound);
    public const string ConfigPatchReferenceNotFound = nameof(ConfigPatchReferenceNotFound);
    public const string RollbackSnapshotSourceGrantMismatch = nameof(RollbackSnapshotSourceGrantMismatch);
    public const string RollbackSnapshotCapabilityMismatch = nameof(RollbackSnapshotCapabilityMismatch);
    public const string RollbackSnapshotScopeMismatch = nameof(RollbackSnapshotScopeMismatch);
    public const string RevocationRecordGrantMismatch = nameof(RevocationRecordGrantMismatch);
    public const string RevocationRecordCapabilityMismatch = nameof(RevocationRecordCapabilityMismatch);
    public const string RevocationRecordScopeMismatch = nameof(RevocationRecordScopeMismatch);
    public const string ConfigPatchSourceGrantMismatch = nameof(ConfigPatchSourceGrantMismatch);
    public const string ConfigPatchTargetCapabilityMismatch = nameof(ConfigPatchTargetCapabilityMismatch);
    public const string ConfigPatchTargetScopeMismatch = nameof(ConfigPatchTargetScopeMismatch);
    public const string ConfigPatchPatchModeNotArtifactOnly = nameof(ConfigPatchPatchModeNotArtifactOnly);
    public const string ConfigPatchApplyToRuntimeTrue = nameof(ConfigPatchApplyToRuntimeTrue);
    public const string LiveActivationDryRunContractIncomplete = nameof(LiveActivationDryRunContractIncomplete);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
}

public sealed class RuntimeActivationArtifactContentSnapshot
{
    public string RuntimeSwitchPath { get; init; } = string.Empty;
    public string ActivationAuditPath { get; init; } = string.Empty;
    public string RuntimeGuardManifestPath { get; init; } = string.Empty;
    public string ScopeEnforcementManifestPath { get; init; } = string.Empty;
    public string ActivationRollbackBindingPath { get; init; } = string.Empty;
    public GuardedRuntimeActivationRuntimeSwitchArtifactContent? RuntimeSwitch { get; init; }
    public GuardedRuntimeActivationAuditArtifactEvent? ActivationAudit { get; init; }
    public GuardedRuntimeActivationRuntimeGuardManifestContent? RuntimeGuardManifest { get; init; }
    public GuardedRuntimeActivationScopeEnforcementManifestContent? ScopeEnforcementManifest { get; init; }
    public GuardedRuntimeActivationRollbackBindingContent? ActivationRollbackBinding { get; init; }
}

public sealed class RuntimeActivationArtifactReferenceExistence
{
    public string RollbackSnapshotPath { get; init; } = string.Empty;
    public string RevocationRecordPath { get; init; } = string.Empty;
    public string ConfigPatchSourcePath { get; init; } = string.Empty;
    public bool RollbackSnapshotExists { get; init; }
    public bool RevocationRecordExists { get; init; }
    public bool ConfigPatchExists { get; init; }
    public CrossingRollbackSnapshotContent? RollbackSnapshot { get; init; }
    public CrossingRevocationRecordContent? RevocationRecord { get; init; }
    public CrossingRuntimeConfigPatchContent? ConfigPatch { get; init; }
}
public sealed class LiveActivationDryRunContract
{
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public string SourceGuardedRuntimeActivationDryRunOperationId { get; init; } = string.Empty;
    public string RuntimeSwitchArtifactPath { get; init; } = string.Empty;
    public string ActivationAuditArtifactPath { get; init; } = string.Empty;
    public string RuntimeGuardManifestPath { get; init; } = string.Empty;
    public string ScopeEnforcementManifestPath { get; init; } = string.Empty;
    public string ActivationRollbackBindingPath { get; init; } = string.Empty;
    public string RollbackSnapshotReference { get; init; } = string.Empty;
    public string RevocationRecordReference { get; init; } = string.Empty;
    public string ConfigPatchSourceReference { get; init; } = string.Empty;
    public bool Complete { get; init; }
}

public sealed class RuntimeActivationArtifactIntegrityDecision
{
    public string Status { get; init; } = RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked;
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public int ContentVerifiedArtifactCount { get; init; }
    public bool AllRuntimeActivationArtifactsContentVerified { get; init; }
    public LiveActivationDryRunContract LiveActivationDryRunContract { get; init; } = new();
    public bool LiveActivationDryRunContractComplete { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ConfigPatchAppliedToRuntime { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PromotionToMainlinePerformed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }
}

public static class FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityPolicy
{
    private const string AllowedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string AllowedScope = "demo-workspace/demo-collection";

    public static RuntimeActivationArtifactIntegrityDecision Evaluate(
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? writeOutReport,
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport? guardedDryRunReport,
        FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport? runtimeActivationDryRunReport,
        RuntimeActivationArtifactContentSnapshot snapshot,
        RuntimeActivationArtifactReferenceExistence referenceExistence,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        var boundGrantId = writeOutReport?.BoundGrantId ?? string.Empty;
        var boundCapability = writeOutReport?.BoundCapability ?? string.Empty;
        var boundScope = writeOutReport?.BoundScope ?? string.Empty;
        if (writeOutReport is null) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.GuardedRuntimeActivationArtifactWriteOutGateMissing);
        else
        {
            if (!writeOutReport.GuardedRuntimeActivationArtifactWriteOutPassed || !writeOutReport.GatePassed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.GuardedRuntimeActivationArtifactWriteOutGateNotPassed);
            if (!writeOutReport.RuntimeActivationArtifactsWritten) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeActivationArtifactsWrittenFalse);
            if (writeOutReport.WrittenArtifactPaths.Count != 5) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.WrittenArtifactCountNotFive);
            if (string.IsNullOrWhiteSpace(boundGrantId)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.BoundGrantIdEmpty);
            if (!string.Equals(boundCapability, AllowedCapability, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.BoundCapabilityMismatch);
            if (!string.Equals(boundScope, AllowedScope, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.BoundScopeMismatch);
            if (writeOutReport.RuntimeActivation) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeActivationTrueInUpstream);
            if (writeOutReport.FormalRetrievalAllowed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.FormalRetrievalAllowedTrueInUpstream);
            if (writeOutReport.RuntimeSwitchAllowed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchAllowedTrueInUpstream);
            if (writeOutReport.ConfigPatchAppliedToRuntime) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ConfigPatchAppliedToRuntimeTrueInUpstream);
            if (writeOutReport.PackageOutputChanged) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.PackageOutputChangedTrueInUpstream);
            if (writeOutReport.FormalPackageWritten) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.FormalPackageWrittenTrueInUpstream);
            if (writeOutReport.VectorStoreBindingChanged) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.VectorStoreBindingChangedTrueInUpstream);
            if (writeOutReport.GlobalDefaultOn) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.GlobalDefaultOnTrueInUpstream);
            if (!writeOutReport.NoRuntimeMutationInvariant) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.NoRuntimeMutationInvariantFalseInUpstream);
        }

        if (guardedDryRunReport is null) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.GuardedRuntimeActivationDryRunGateMissing);
        else if (!guardedDryRunReport.GuardedRuntimeActivationDryRunPassed || !guardedDryRunReport.GatePassed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.GuardedRuntimeActivationDryRunGateNotPassed);
        if (runtimeActivationDryRunReport is null) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeActivationDryRunGateMissing);
        else if (!runtimeActivationDryRunReport.RuntimeActivationDryRunPassed || !runtimeActivationDryRunReport.GatePassed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeActivationDryRunGateNotPassed);

        var guardedDryRunOperationId = guardedDryRunReport?.OperationId ?? string.Empty;
        var runtimeSwitchSourceOperationId = snapshot.RuntimeSwitch?.SourceGuardedRuntimeActivationDryRunOperationId ?? string.Empty;
        var activationAuditSourceOperationId = snapshot.ActivationAudit?.SourceGuardedRuntimeActivationDryRunOperationId ?? string.Empty;
        var sourceOperationId = !string.IsNullOrWhiteSpace(runtimeSwitchSourceOperationId)
            ? runtimeSwitchSourceOperationId
            : !string.IsNullOrWhiteSpace(activationAuditSourceOperationId)
            ? activationAuditSourceOperationId
            : guardedDryRunOperationId;
        var verifiedArtifacts = 0;
        if (snapshot.RuntimeSwitch is null) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchArtifactMissing);
        else
        {
            if (!string.Equals(snapshot.RuntimeSwitch.BoundGrantId, boundGrantId, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchBoundGrantIdMismatch);
            if (!string.Equals(snapshot.RuntimeSwitch.Capability, AllowedCapability, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchCapabilityMismatch);
            if (!string.Equals(snapshot.RuntimeSwitch.Scope, AllowedScope, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchScopeMismatch);
            if (!string.Equals(snapshot.RuntimeSwitch.SwitchMode, "GuardedArtifactOnly", StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchModeNotGuardedArtifactOnly);
            if (snapshot.RuntimeSwitch.ApplyToRuntime) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchApplyToRuntimeTrue);
            if (snapshot.RuntimeSwitch.RuntimeActivation) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchRuntimeActivationTrue);
            if (snapshot.RuntimeSwitch.FormalRetrievalAllowed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchFormalRetrievalAllowedTrue);
            if (snapshot.RuntimeSwitch.RuntimeSwitchAllowed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchRuntimeSwitchAllowedTrue);
            if (!string.Equals(snapshot.RuntimeSwitch.SourceGuardedRuntimeActivationDryRunOperationId, sourceOperationId, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchSourceOperationMismatch);
            if (string.Equals(snapshot.RuntimeSwitch.BoundGrantId, boundGrantId, StringComparison.Ordinal) && string.Equals(snapshot.RuntimeSwitch.Capability, AllowedCapability, StringComparison.Ordinal) && string.Equals(snapshot.RuntimeSwitch.Scope, AllowedScope, StringComparison.Ordinal) && string.Equals(snapshot.RuntimeSwitch.SwitchMode, "GuardedArtifactOnly", StringComparison.Ordinal) && !snapshot.RuntimeSwitch.ApplyToRuntime && !snapshot.RuntimeSwitch.RuntimeActivation && !snapshot.RuntimeSwitch.FormalRetrievalAllowed && !snapshot.RuntimeSwitch.RuntimeSwitchAllowed && string.Equals(snapshot.RuntimeSwitch.SourceGuardedRuntimeActivationDryRunOperationId, sourceOperationId, StringComparison.Ordinal)) verifiedArtifacts++;
        }

        if (snapshot.ActivationAudit is null) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditArtifactMissing);
        else
        {
            if (!string.Equals(snapshot.ActivationAudit.EventType, "GuardedRuntimeActivationArtifactWriteOut", StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditEventTypeMismatch);
            if (!string.Equals(snapshot.ActivationAudit.BoundGrantId, boundGrantId, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditBoundGrantIdMismatch);
            if (!string.Equals(snapshot.ActivationAudit.Capability, AllowedCapability, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditCapabilityMismatch);
            if (!string.Equals(snapshot.ActivationAudit.Scope, AllowedScope, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditScopeMismatch);
            if (!snapshot.ActivationAudit.RuntimeActivationArtifactsWritten) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditRuntimeActivationArtifactsWrittenFalse);
            if (snapshot.ActivationAudit.RuntimeActivation) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditRuntimeActivationTrue);
            if (snapshot.ActivationAudit.FormalRetrievalAllowed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditFormalRetrievalAllowedTrue);
            if (snapshot.ActivationAudit.RuntimeSwitchAllowed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditRuntimeSwitchAllowedTrue);
            if (!string.Equals(snapshot.ActivationAudit.SourceGuardedRuntimeActivationDryRunOperationId, sourceOperationId, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditSourceOperationMismatch);
            if (string.Equals(snapshot.ActivationAudit.EventType, "GuardedRuntimeActivationArtifactWriteOut", StringComparison.Ordinal) && string.Equals(snapshot.ActivationAudit.BoundGrantId, boundGrantId, StringComparison.Ordinal) && string.Equals(snapshot.ActivationAudit.Capability, AllowedCapability, StringComparison.Ordinal) && string.Equals(snapshot.ActivationAudit.Scope, AllowedScope, StringComparison.Ordinal) && snapshot.ActivationAudit.RuntimeActivationArtifactsWritten && !snapshot.ActivationAudit.RuntimeActivation && !snapshot.ActivationAudit.FormalRetrievalAllowed && !snapshot.ActivationAudit.RuntimeSwitchAllowed && string.Equals(snapshot.ActivationAudit.SourceGuardedRuntimeActivationDryRunOperationId, sourceOperationId, StringComparison.Ordinal)) verifiedArtifacts++;
        }
        if (snapshot.RuntimeGuardManifest is null) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeGuardManifestMissing);
        else
        {
            if (!string.Equals(snapshot.RuntimeGuardManifest.BoundGrantId, boundGrantId, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeGuardManifestBoundGrantIdMismatch);
            if (!string.Equals(snapshot.RuntimeGuardManifest.Scope, AllowedScope, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeGuardManifestScopeMismatch);
            if (!snapshot.RuntimeGuardManifest.KillSwitchRequired) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeGuardManifestKillSwitchRequiredFalse);
            if (!snapshot.RuntimeGuardManifest.ScopeGuardRequired) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeGuardManifestScopeGuardRequiredFalse);
            if (!snapshot.RuntimeGuardManifest.RollbackRequired) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeGuardManifestRollbackRequiredFalse);
            if (snapshot.RuntimeGuardManifest.RuntimeActivationAllowed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeGuardManifestRuntimeActivationAllowedTrue);
            if (string.Equals(snapshot.RuntimeGuardManifest.BoundGrantId, boundGrantId, StringComparison.Ordinal) && string.Equals(snapshot.RuntimeGuardManifest.Scope, AllowedScope, StringComparison.Ordinal) && snapshot.RuntimeGuardManifest.KillSwitchRequired && snapshot.RuntimeGuardManifest.ScopeGuardRequired && snapshot.RuntimeGuardManifest.RollbackRequired && !snapshot.RuntimeGuardManifest.RuntimeActivationAllowed) verifiedArtifacts++;
        }

        if (snapshot.ScopeEnforcementManifest is null) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ScopeEnforcementManifestMissing);
        else
        {
            if (!string.Equals(snapshot.ScopeEnforcementManifest.BoundGrantId, boundGrantId, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ScopeEnforcementManifestBoundGrantIdMismatch);
            if (!string.Equals(snapshot.ScopeEnforcementManifest.AllowedScope, AllowedScope, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ScopeEnforcementManifestAllowedScopeMismatch);
            if (snapshot.ScopeEnforcementManifest.GlobalDefaultOn) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ScopeEnforcementManifestGlobalDefaultOnTrue);
            if (snapshot.ScopeEnforcementManifest.WildcardScopeAllowed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ScopeEnforcementManifestWildcardScopeAllowedTrue);
            if (string.Equals(snapshot.ScopeEnforcementManifest.BoundGrantId, boundGrantId, StringComparison.Ordinal) && string.Equals(snapshot.ScopeEnforcementManifest.AllowedScope, AllowedScope, StringComparison.Ordinal) && !snapshot.ScopeEnforcementManifest.GlobalDefaultOn && !snapshot.ScopeEnforcementManifest.WildcardScopeAllowed) verifiedArtifacts++;
        }

        if (snapshot.ActivationRollbackBinding is null) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationRollbackBindingMissing);
        else
        {
            if (!string.Equals(snapshot.ActivationRollbackBinding.BoundGrantId, boundGrantId, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationRollbackBindingBoundGrantIdMismatch);
            if (string.IsNullOrWhiteSpace(snapshot.ActivationRollbackBinding.RollbackSnapshotReference)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationRollbackBindingRollbackSnapshotReferenceMissing);
            if (string.IsNullOrWhiteSpace(snapshot.ActivationRollbackBinding.RevocationRecordReference)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationRollbackBindingRevocationRecordReferenceMissing);
            if (string.IsNullOrWhiteSpace(snapshot.ActivationRollbackBinding.ConfigPatchSourceReference)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationRollbackBindingConfigPatchSourceReferenceMissing);
            if (!snapshot.ActivationRollbackBinding.RestoreTestRequired) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationRollbackBindingRestoreTestRequiredFalse);
            if (snapshot.ActivationRollbackBinding.RuntimeActivation) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ActivationRollbackBindingRuntimeActivationTrue);
            if (string.Equals(snapshot.ActivationRollbackBinding.BoundGrantId, boundGrantId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(snapshot.ActivationRollbackBinding.RollbackSnapshotReference) && !string.IsNullOrWhiteSpace(snapshot.ActivationRollbackBinding.RevocationRecordReference) && !string.IsNullOrWhiteSpace(snapshot.ActivationRollbackBinding.ConfigPatchSourceReference) && snapshot.ActivationRollbackBinding.RestoreTestRequired && !snapshot.ActivationRollbackBinding.RuntimeActivation) verifiedArtifacts++;
        }

        if (!referenceExistence.RollbackSnapshotExists) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RollbackSnapshotReferenceNotFound);
        if (!referenceExistence.RevocationRecordExists) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RevocationRecordReferenceNotFound);
        if (!referenceExistence.ConfigPatchExists) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ConfigPatchReferenceNotFound);
        if (referenceExistence.RollbackSnapshotExists && referenceExistence.RollbackSnapshot is not null)
        {
            if (!string.Equals(referenceExistence.RollbackSnapshot.SourceGrantId, boundGrantId, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RollbackSnapshotSourceGrantMismatch);
            if (!string.Equals(referenceExistence.RollbackSnapshot.BoundCapability, AllowedCapability, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RollbackSnapshotCapabilityMismatch);
            if (!string.Equals(referenceExistence.RollbackSnapshot.BoundScope, AllowedScope, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RollbackSnapshotScopeMismatch);
        }
        if (referenceExistence.RevocationRecordExists && referenceExistence.RevocationRecord is not null)
        {
            if (!string.Equals(referenceExistence.RevocationRecord.GrantId, boundGrantId, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RevocationRecordGrantMismatch);
            if (!string.Equals(referenceExistence.RevocationRecord.BoundCapability, AllowedCapability, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RevocationRecordCapabilityMismatch);
            if (!string.Equals(referenceExistence.RevocationRecord.BoundScope, AllowedScope, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RevocationRecordScopeMismatch);
        }
        if (referenceExistence.ConfigPatchExists && referenceExistence.ConfigPatch is not null)
        {
            if (!string.Equals(referenceExistence.ConfigPatch.SourceGrantId, boundGrantId, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ConfigPatchSourceGrantMismatch);
            if (!string.Equals(referenceExistence.ConfigPatch.TargetCapability, AllowedCapability, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ConfigPatchTargetCapabilityMismatch);
            if (!string.Equals(referenceExistence.ConfigPatch.TargetScope, AllowedScope, StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ConfigPatchTargetScopeMismatch);
            if (!string.Equals(referenceExistence.ConfigPatch.PatchMode, "ArtifactOnly", StringComparison.Ordinal)) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ConfigPatchPatchModeNotArtifactOnly);
            if (referenceExistence.ConfigPatch.ApplyToRuntime) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.ConfigPatchApplyToRuntimeTrue);
        }

        if (!rtPassed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.MainlineTrustRegistryPresent);

        var contract = new LiveActivationDryRunContract
        {
            BoundGrantId = boundGrantId,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            SourceGuardedRuntimeActivationDryRunOperationId = sourceOperationId,
            RuntimeSwitchArtifactPath = snapshot.RuntimeSwitchPath,
            ActivationAuditArtifactPath = snapshot.ActivationAuditPath,
            RuntimeGuardManifestPath = snapshot.RuntimeGuardManifestPath,
            ScopeEnforcementManifestPath = snapshot.ScopeEnforcementManifestPath,
            ActivationRollbackBindingPath = snapshot.ActivationRollbackBindingPath,
            RollbackSnapshotReference = referenceExistence.RollbackSnapshotPath,
            RevocationRecordReference = referenceExistence.RevocationRecordPath,
            ConfigPatchSourceReference = referenceExistence.ConfigPatchSourcePath,
            Complete = !string.IsNullOrWhiteSpace(boundGrantId) && !string.IsNullOrWhiteSpace(boundCapability) && !string.IsNullOrWhiteSpace(boundScope) && !string.IsNullOrWhiteSpace(sourceOperationId) && !string.IsNullOrWhiteSpace(snapshot.RuntimeSwitchPath) && !string.IsNullOrWhiteSpace(snapshot.ActivationAuditPath) && !string.IsNullOrWhiteSpace(snapshot.RuntimeGuardManifestPath) && !string.IsNullOrWhiteSpace(snapshot.ScopeEnforcementManifestPath) && !string.IsNullOrWhiteSpace(snapshot.ActivationRollbackBindingPath) && !string.IsNullOrWhiteSpace(referenceExistence.RollbackSnapshotPath) && !string.IsNullOrWhiteSpace(referenceExistence.RevocationRecordPath) && !string.IsNullOrWhiteSpace(referenceExistence.ConfigPatchSourcePath)
        };
        if (!contract.Complete) blocked.Add(RuntimeActivationArtifactIntegrityBlockedReasons.LiveActivationDryRunContractIncomplete);

        var reasons = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var passed = reasons.Length == 0;
        return new RuntimeActivationArtifactIntegrityDecision
        {
            Status = passed ? RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityVerified : RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked,
            BoundGrantId = boundGrantId,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            ContentVerifiedArtifactCount = verifiedArtifacts,
            AllRuntimeActivationArtifactsContentVerified = verifiedArtifacts == 5,
            LiveActivationDryRunContract = contract,
            LiveActivationDryRunContractComplete = contract.Complete,
            BlockedReasons = reasons,
            Reasoning = passed ? "all runtime-activation artifacts and dedicated-crossing references are consistent." : $"{reasons.Length} blocked reason(s); runtime activation artifact integrity blocked.",
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ConfigPatchAppliedToRuntime = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            PromotionToMainlinePerformed = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            NoRuntimeMutationInvariant = true
        };
    }
}

public sealed class FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner
{
    private const string TestCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string TestScope = "demo-workspace/demo-collection";
    private const string TestGrantId = "frp-grant-fixture-001";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport Run(
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? loadedArtifactWriteOutReport,
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport? loadedGuardedDryRunReport,
        FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport? loadedRuntimeActivationDryRunReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityOptions();
        var cleanGuarded = FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner.BuildCleanGuardedRuntimeActivationDryRunReport();
        var cleanWriteOut = BuildCleanArtifactWriteOutReport();
        var cleanRuntime = BuildCleanRuntimeActivationDryRunReport();
        var cleanSnapshot = BuildCleanArtifactSnapshot(cleanWriteOut.BoundGrantId, cleanGuarded.OperationId);
        var cleanRefs = BuildCleanReferenceExistence(cleanWriteOut.BoundGrantId, cleanWriteOut.BoundCapability, cleanWriteOut.BoundScope);
        var cases = new List<FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityCase>();
        foreach (var scenario in BuildScenarios(cleanWriteOut, cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs))
        {
            var decision = FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityPolicy.Evaluate(scenario.WriteOutReport, scenario.GuardedDryRunReport, scenario.RuntimeActivationDryRunReport, scenario.Snapshot, scenario.ReferenceExistence, scenario.RtPassed, scenario.P15Passed, scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var reasonMatched = scenario.ExpectedBlockedReason is null || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            var passedAsExpected = statusMatched && reasonMatched && !decision.RuntimeActivation && !decision.FormalRetrievalAllowed && !decision.RuntimeSwitchAllowed && !decision.PackageOutputChanged && !decision.FormalPackageWritten && !decision.PackingPolicyChanged && !decision.VectorStoreBindingChanged && !decision.GlobalDefaultOn && !decision.PromotionToMainlinePerformed && decision.NoRuntimeMutationInvariant;
            cases.Add(new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityCase { CaseName = scenario.CaseName, ExpectedStatus = scenario.ExpectedStatus, ActualStatus = decision.Status, ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty, ActualBlockedReasons = decision.BlockedReasons, BoundGrantId = decision.BoundGrantId, BoundCapability = decision.BoundCapability, BoundScope = decision.BoundScope, ContentVerifiedArtifactCount = decision.ContentVerifiedArtifactCount, LiveActivationDryRunContractComplete = decision.LiveActivationDryRunContractComplete, RuntimeActivation = false, FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false, PackageOutputChanged = false, Reasoning = decision.Reasoning, StatusMatched = statusMatched, BlockedReasonMatched = reasonMatched, PassedAsExpected = passedAsExpected });
        }

        var passedCases = cases.Count(static x => x.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var verifiedCases = cases.Count(static x => x.ActualStatus == RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityVerified);
        var blockedCases = cases.Count(static x => x.ActualStatus == RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked);
        var blocked = new List<string>();
        if (cases.Count < 30) blocked.Add("InsufficientRuntimeActivationArtifactIntegrityCases");
        if (failedCases > 0) blocked.Add("RuntimeActivationArtifactIntegrityMatrixFailed");

        var realSnapshot = LoadRealArtifactSnapshot(loadedArtifactWriteOutReport);
        var realRefs = BuildRealReferenceExistence(loadedArtifactWriteOutReport, realSnapshot);
        var realDecision = FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityPolicy.Evaluate(loadedArtifactWriteOutReport, loadedGuardedDryRunReport, loadedRuntimeActivationDryRunReport, realSnapshot, realRefs, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (loadedArtifactWriteOutReport is null) blocked.Add("RealGuardedRuntimeActivationArtifactWriteOutGateArtifactMissing");
        else if (!loadedArtifactWriteOutReport.GuardedRuntimeActivationArtifactWriteOutPassed || !loadedArtifactWriteOutReport.GatePassed) blocked.Add("RealGuardedRuntimeActivationArtifactWriteOutGateNotPassed");
        if (loadedGuardedDryRunReport is null) blocked.Add("RealGuardedRuntimeActivationGateDryRunGateArtifactMissing");
        else if (!loadedGuardedDryRunReport.GuardedRuntimeActivationDryRunPassed || !loadedGuardedDryRunReport.GatePassed) blocked.Add("RealGuardedRuntimeActivationGateDryRunGateNotPassed");
        if (loadedRuntimeActivationDryRunReport is null) blocked.Add("RealRuntimeActivationDryRunGateArtifactMissing");
        else if (!loadedRuntimeActivationDryRunReport.RuntimeActivationDryRunPassed || !loadedRuntimeActivationDryRunReport.GatePassed) blocked.Add("RealRuntimeActivationDryRunGateNotPassed");
        if (realDecision.Status != RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityVerified)
        {
            foreach (var reason in realDecision.BlockedReasons) blocked.Add($"RealRuntimeActivationArtifactIntegrity:{reason}");
        }

        var reasons = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = reasons.Length == 0;
        return new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport
        {
            OperationId = $"frp-runtime-activation-artifact-integrity-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            RuntimeActivationArtifactIntegrityPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            VerifiedCases = verifiedCases,
            BlockedCases = blockedCases,
            Cases = cases,
            BoundGrantId = realDecision.BoundGrantId,
            BoundCapability = realDecision.BoundCapability,
            BoundScope = realDecision.BoundScope,
            UpstreamArtifactWriteOutGatePresent = loadedArtifactWriteOutReport is not null,
            UpstreamArtifactWriteOutGatePassed = loadedArtifactWriteOutReport?.GatePassed ?? false,
            UpstreamGuardedRuntimeActivationDryRunGatePresent = loadedGuardedDryRunReport is not null,
            UpstreamGuardedRuntimeActivationDryRunGatePassed = loadedGuardedDryRunReport?.GatePassed ?? false,
            UpstreamRuntimeActivationDryRunGatePresent = loadedRuntimeActivationDryRunReport is not null,
            UpstreamRuntimeActivationDryRunGatePassed = loadedRuntimeActivationDryRunReport?.GatePassed ?? false,
            WrittenArtifactPaths = loadedArtifactWriteOutReport?.WrittenArtifactPaths ?? Array.Empty<string>(),
            ContentVerifiedArtifactCount = realDecision.ContentVerifiedArtifactCount,
            AllRuntimeActivationArtifactsContentVerified = realDecision.AllRuntimeActivationArtifactsContentVerified,
            LiveActivationDryRunContract = realDecision.LiveActivationDryRunContract,
            LiveActivationDryRunContractComplete = realDecision.LiveActivationDryRunContractComplete,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ConfigPatchAppliedToRuntime = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            PromotionToMainlinePerformed = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = reasons,
            Diagnostics = [$"total={cases.Count}", $"passed={passedCases}", $"failed={failedCases}", $"contentVerified={realDecision.ContentVerifiedArtifactCount}", $"contractComplete={realDecision.LiveActivationDryRunContractComplete}"]
        };
    }

    public static FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport BuildCleanArtifactWriteOutReport()
    {
        var guarded = FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner.BuildCleanGuardedRuntimeActivationDryRunReport();
        return new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner().Run(guarded, true, true, false, false, path => path.Contains("dedicated-crossing", StringComparison.OrdinalIgnoreCase), decision => new FormalRetrievalPromotionApprovalRuntimeActivationArtifactWriter.WriteResult { AllArtifactsWritten = true, WrittenPaths = decision.PlannedArtifactPaths.ToArray() }, new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutOptions { IsGate = true });
    }

    public static FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport BuildCleanRuntimeActivationDryRunReport()
    {
        var execution = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanExecutionReport();
        var grant = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanGrant(TestGrantId, TestCapability, TestScope);
        var configPatch = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanConfigPatch(TestGrantId, TestCapability, TestScope);
        var rollback = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanRollback(TestGrantId, TestCapability, TestScope);
        var audit = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanAudit(TestGrantId, TestCapability, TestScope);
        var revocation = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanRevocation(TestGrantId, TestCapability, TestScope);
        return new FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner().Run(execution, grant, configPatch, rollback, audit, revocation, true, true, false, false, "vector/v8/dedicated-crossing/runtime-config-patch-fixture.json", "vector/v8/dedicated-crossing/rollback-snapshot-fixture.json", "vector/v8/dedicated-crossing/revocation-record-fixture.json", new FormalRetrievalPromotionApprovalRuntimeActivationDryRunOptions { IsGate = true });
    }
    public static RuntimeActivationArtifactContentSnapshot BuildCleanArtifactSnapshot(string boundGrantId, string sourceOperationId) => new()
    {
        RuntimeSwitchPath = "vector/v8/runtime-activation/runtime-switch-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        ActivationAuditPath = "vector/v8/runtime-activation/activation-audit-FormalRetrievalActivation-demo-workspace-demo-collection.jsonl",
        RuntimeGuardManifestPath = "vector/v8/runtime-activation/runtime-guard-manifest-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        ScopeEnforcementManifestPath = "vector/v8/runtime-activation/scope-enforcement-manifest-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        ActivationRollbackBindingPath = "vector/v8/runtime-activation/activation-rollback-binding-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        RuntimeSwitch = new GuardedRuntimeActivationRuntimeSwitchArtifactContent { SwitchId = "fixture-switch", BoundGrantId = boundGrantId, Capability = TestCapability, Scope = TestScope, SwitchMode = "GuardedArtifactOnly", ApplyToRuntime = false, RuntimeActivation = false, FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false, SourceGuardedRuntimeActivationDryRunOperationId = sourceOperationId, CreatedAt = "2026-06-27T12:00:00Z" },
        ActivationAudit = new GuardedRuntimeActivationAuditArtifactEvent { EventId = "fixture-audit", EventType = "GuardedRuntimeActivationArtifactWriteOut", BoundGrantId = boundGrantId, Capability = TestCapability, Scope = TestScope, RuntimeActivationArtifactsWritten = true, RuntimeActivation = false, FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false, SourceGuardedRuntimeActivationDryRunOperationId = sourceOperationId, Timestamp = "2026-06-27T12:00:00Z" },
        RuntimeGuardManifest = new GuardedRuntimeActivationRuntimeGuardManifestContent { BoundGrantId = boundGrantId, Scope = TestScope, KillSwitchRequired = true, ScopeGuardRequired = true, RollbackRequired = true, RuntimeActivationAllowed = false, CreatedAt = "2026-06-27T12:00:00Z" },
        ScopeEnforcementManifest = new GuardedRuntimeActivationScopeEnforcementManifestContent { BoundGrantId = boundGrantId, AllowedScope = TestScope, GlobalDefaultOn = false, WildcardScopeAllowed = false, CreatedAt = "2026-06-27T12:00:00Z" },
        ActivationRollbackBinding = new GuardedRuntimeActivationRollbackBindingContent { BoundGrantId = boundGrantId, RollbackSnapshotReference = "vector/v8/dedicated-crossing/rollback-snapshot-FormalRetrievalActivation-demo-workspace-demo-collection.json", RevocationRecordReference = "vector/v8/dedicated-crossing/revocation-record-FormalRetrievalActivation-demo-workspace-demo-collection.json", ConfigPatchSourceReference = "vector/v8/dedicated-crossing/runtime-config-patch-FormalRetrievalActivation-demo-workspace-demo-collection.json", RestoreTestRequired = true, RuntimeActivation = false, CreatedAt = "2026-06-27T12:00:00Z" }
    };

    public static RuntimeActivationArtifactReferenceExistence BuildCleanReferenceExistence(string boundGrantId, string capability, string scope) => new()
    {
        RollbackSnapshotPath = "vector/v8/dedicated-crossing/rollback-snapshot-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        RevocationRecordPath = "vector/v8/dedicated-crossing/revocation-record-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        ConfigPatchSourcePath = "vector/v8/dedicated-crossing/runtime-config-patch-FormalRetrievalActivation-demo-workspace-demo-collection.json",
        RollbackSnapshotExists = true,
        RevocationRecordExists = true,
        ConfigPatchExists = true,
        RollbackSnapshot = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanRollback(boundGrantId, capability, scope),
        RevocationRecord = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanRevocation(boundGrantId, capability, scope),
        ConfigPatch = FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildCleanConfigPatch(boundGrantId, capability, scope)
    };

    private static IReadOnlyList<FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityScenario> BuildScenarios(FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport cleanWriteOut, FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport cleanGuarded, FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport cleanRuntime, RuntimeActivationArtifactContentSnapshot cleanSnapshot, RuntimeActivationArtifactReferenceExistence cleanRefs)
    {
        return
        [
            new("AllUpstreamClean", cleanWriteOut, cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityVerified, null),
            new("GuardedRuntimeActivationArtifactWriteOutGateMissing", null, cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.GuardedRuntimeActivationArtifactWriteOutGateMissing),
            new("GuardedRuntimeActivationArtifactWriteOutGateNotPassed", MutateWriteOutReport(cleanWriteOut, gatePassed: false), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.GuardedRuntimeActivationArtifactWriteOutGateNotPassed),
            new("RuntimeActivationArtifactsWrittenFalse", MutateWriteOutReport(cleanWriteOut, runtimeActivationArtifactsWritten: false), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeActivationArtifactsWrittenFalse),
            new("WrittenArtifactCountNotFive", MutateWriteOutReport(cleanWriteOut, writtenPaths: cleanWriteOut.WrittenArtifactPaths.Take(4).ToArray()), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.WrittenArtifactCountNotFive),
            new("BoundGrantIdEmpty", MutateWriteOutReport(cleanWriteOut, boundGrantId: string.Empty), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.BoundGrantIdEmpty),
            new("BoundCapabilityMismatch", MutateWriteOutReport(cleanWriteOut, boundCapability: "OtherCapability"), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.BoundCapabilityMismatch),
            new("BoundScopeMismatch", MutateWriteOutReport(cleanWriteOut, boundScope: "other-workspace/other-collection"), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.BoundScopeMismatch),
            new("RuntimeActivationTrueInUpstream", MutateWriteOutReport(cleanWriteOut, runtimeActivation: true), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeActivationTrueInUpstream),
            new("FormalRetrievalAllowedTrueInUpstream", MutateWriteOutReport(cleanWriteOut, formalRetrievalAllowed: true), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.FormalRetrievalAllowedTrueInUpstream),
            new("RuntimeSwitchAllowedTrueInUpstream", MutateWriteOutReport(cleanWriteOut, runtimeSwitchAllowed: true), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchAllowedTrueInUpstream),
            new("ConfigPatchAppliedToRuntimeTrueInUpstream", MutateWriteOutReport(cleanWriteOut, configPatchAppliedToRuntime: true), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.ConfigPatchAppliedToRuntimeTrueInUpstream),
            new("PackageOutputChangedTrueInUpstream", MutateWriteOutReport(cleanWriteOut, packageOutputChanged: true), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.PackageOutputChangedTrueInUpstream),
            new("FormalPackageWrittenTrueInUpstream", MutateWriteOutReport(cleanWriteOut, formalPackageWritten: true), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.FormalPackageWrittenTrueInUpstream),
            new("VectorStoreBindingChangedTrueInUpstream", MutateWriteOutReport(cleanWriteOut, vectorStoreBindingChanged: true), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.VectorStoreBindingChangedTrueInUpstream),
            new("GlobalDefaultOnTrueInUpstream", MutateWriteOutReport(cleanWriteOut, globalDefaultOn: true), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.GlobalDefaultOnTrueInUpstream),
            new("NoRuntimeMutationInvariantFalseInUpstream", MutateWriteOutReport(cleanWriteOut, noRuntimeMutationInvariant: false), cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.NoRuntimeMutationInvariantFalseInUpstream),
            new("GuardedRuntimeActivationDryRunGateMissing", cleanWriteOut, null, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.GuardedRuntimeActivationDryRunGateMissing),
            new("GuardedRuntimeActivationDryRunGateNotPassed", cleanWriteOut, MutateGuardedReport(cleanGuarded, false), cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.GuardedRuntimeActivationDryRunGateNotPassed),
            new("RuntimeActivationDryRunGateMissing", cleanWriteOut, cleanGuarded, null, cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeActivationDryRunGateMissing),
            new("RuntimeActivationDryRunGateNotPassed", cleanWriteOut, cleanGuarded, MutateRuntimeReport(cleanRuntime, false), cleanSnapshot, cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeActivationDryRunGateNotPassed),
            new("RuntimeSwitchArtifactMissing", cleanWriteOut, cleanGuarded, cleanRuntime, WithoutRuntimeSwitch(cleanSnapshot), cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchArtifactMissing),
            new("ActivationAuditArtifactMissing", cleanWriteOut, cleanGuarded, cleanRuntime, WithoutActivationAudit(cleanSnapshot), cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditArtifactMissing),
            new("RuntimeGuardManifestMissing", cleanWriteOut, cleanGuarded, cleanRuntime, WithoutRuntimeGuard(cleanSnapshot), cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeGuardManifestMissing),
            new("ScopeEnforcementManifestMissing", cleanWriteOut, cleanGuarded, cleanRuntime, WithoutScopeManifest(cleanSnapshot), cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.ScopeEnforcementManifestMissing),
            new("ActivationRollbackBindingMissing", cleanWriteOut, cleanGuarded, cleanRuntime, WithoutRollbackBinding(cleanSnapshot), cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.ActivationRollbackBindingMissing),
            new("RuntimeSwitchApplyToRuntimeTrue", cleanWriteOut, cleanGuarded, cleanRuntime, MutateSnapshot(cleanSnapshot, runtimeSwitch: CloneRuntimeSwitch(cleanSnapshot.RuntimeSwitch!, applyToRuntime: true)), cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeSwitchApplyToRuntimeTrue),
            new("ActivationAuditRuntimeActivationTrue", cleanWriteOut, cleanGuarded, cleanRuntime, MutateSnapshot(cleanSnapshot, activationAudit: CloneAudit(cleanSnapshot.ActivationAudit!, runtimeActivation: true)), cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.ActivationAuditRuntimeActivationTrue),
            new("RuntimeGuardManifestRuntimeActivationAllowedTrue", cleanWriteOut, cleanGuarded, cleanRuntime, MutateSnapshot(cleanSnapshot, runtimeGuard: CloneRuntimeGuard(cleanSnapshot.RuntimeGuardManifest!, runtimeActivationAllowed: true)), cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeGuardManifestRuntimeActivationAllowedTrue),
            new("ScopeEnforcementManifestGlobalDefaultOnTrue", cleanWriteOut, cleanGuarded, cleanRuntime, MutateSnapshot(cleanSnapshot, scopeManifest: CloneScopeManifest(cleanSnapshot.ScopeEnforcementManifest!, globalDefaultOn: true)), cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.ScopeEnforcementManifestGlobalDefaultOnTrue),
            new("ActivationRollbackBindingRuntimeActivationTrue", cleanWriteOut, cleanGuarded, cleanRuntime, MutateSnapshot(cleanSnapshot, rollbackBinding: CloneRollbackBinding(cleanSnapshot.ActivationRollbackBinding!, runtimeActivation: true)), cleanRefs, true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.ActivationRollbackBindingRuntimeActivationTrue),
            new("RollbackSnapshotReferenceMissing", cleanWriteOut, cleanGuarded, cleanRuntime, cleanSnapshot, MutateRefs(cleanRefs, rollbackExists: false), true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RollbackSnapshotReferenceNotFound),
            new("RevocationRecordReferenceMissing", cleanWriteOut, cleanGuarded, cleanRuntime, cleanSnapshot, MutateRefs(cleanRefs, revocationExists: false), true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RevocationRecordReferenceNotFound),
            new("ConfigPatchReferenceMissing", cleanWriteOut, cleanGuarded, cleanRuntime, cleanSnapshot, MutateRefs(cleanRefs, configPatchExists: false), true, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.ConfigPatchReferenceNotFound),
            new("RuntimeGateNotPassed", cleanWriteOut, cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, false, true, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", cleanWriteOut, cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, false, false, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", cleanWriteOut, cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, true, false, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", cleanWriteOut, cleanGuarded, cleanRuntime, cleanSnapshot, cleanRefs, true, true, false, true, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, RuntimeActivationArtifactIntegrityBlockedReasons.MainlineTrustRegistryPresent)
        ];
    }
    private static FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport MutateWriteOutReport(FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport s, bool? gatePassed = null, bool? runtimeActivationArtifactsWritten = null, IReadOnlyList<string>? writtenPaths = null, string? boundGrantId = null, string? boundCapability = null, string? boundScope = null, bool? runtimeActivation = null, bool? formalRetrievalAllowed = null, bool? runtimeSwitchAllowed = null, bool? configPatchAppliedToRuntime = null, bool? packageOutputChanged = null, bool? formalPackageWritten = null, bool? vectorStoreBindingChanged = null, bool? globalDefaultOn = null, bool? noRuntimeMutationInvariant = null) => new() { OperationId = s.OperationId, CreatedAt = s.CreatedAt, GuardedRuntimeActivationArtifactWriteOutPassed = gatePassed ?? s.GuardedRuntimeActivationArtifactWriteOutPassed, GatePassed = gatePassed ?? s.GatePassed, TotalCases = s.TotalCases, PassedCases = s.PassedCases, FailedCases = s.FailedCases, WrittenCases = s.WrittenCases, BlockedCases = s.BlockedCases, Cases = s.Cases, BoundGrantId = boundGrantId ?? s.BoundGrantId, BoundCapability = boundCapability ?? s.BoundCapability, BoundScope = boundScope ?? s.BoundScope, PlannedGuardedActivationContract = s.PlannedGuardedActivationContract, UpstreamGuardedRuntimeActivationDryRunGatePresent = s.UpstreamGuardedRuntimeActivationDryRunGatePresent, UpstreamGuardedRuntimeActivationDryRunGatePassed = s.UpstreamGuardedRuntimeActivationDryRunGatePassed, WrittenArtifactPaths = writtenPaths ?? s.WrittenArtifactPaths, RuntimeActivationArtifactsWritten = runtimeActivationArtifactsWritten ?? s.RuntimeActivationArtifactsWritten, RuntimeActivation = runtimeActivation ?? s.RuntimeActivation, FormalRetrievalAllowed = formalRetrievalAllowed ?? s.FormalRetrievalAllowed, RuntimeSwitchAllowed = runtimeSwitchAllowed ?? s.RuntimeSwitchAllowed, ConfigPatchAppliedToRuntime = configPatchAppliedToRuntime ?? s.ConfigPatchAppliedToRuntime, FormalPackageWritten = formalPackageWritten ?? s.FormalPackageWritten, PackageOutputChanged = packageOutputChanged ?? s.PackageOutputChanged, PackingPolicyChanged = s.PackingPolicyChanged, VectorStoreBindingChanged = vectorStoreBindingChanged ?? s.VectorStoreBindingChanged, GlobalDefaultOn = globalDefaultOn ?? s.GlobalDefaultOn, Crossed = s.Crossed, ArtifactOnly = s.ArtifactOnly, CapabilityGrantWritten = s.CapabilityGrantWritten, ConfigPatchWritten = s.ConfigPatchWritten, RollbackSnapshotWritten = s.RollbackSnapshotWritten, AuditLogWritten = s.AuditLogWritten, RevocationRecordWritten = s.RevocationRecordWritten, EvidenceCopiedToMainline = s.EvidenceCopiedToMainline, TrustRegistryCopiedToMainline = s.TrustRegistryCopiedToMainline, MainlineEvidencePresent = s.MainlineEvidencePresent, MainlineTrustRegistryPresent = s.MainlineTrustRegistryPresent, ManualReviewRequired = s.ManualReviewRequired, ApprovalSealed = s.ApprovalSealed, GrantApplied = s.GrantApplied, ApplicationApplied = s.ApplicationApplied, RollbackActivated = s.RollbackActivated, PromotionToMainlinePerformed = s.PromotionToMainlinePerformed, NoRuntimeMutationInvariant = noRuntimeMutationInvariant ?? s.NoRuntimeMutationInvariant, BlockedReasons = s.BlockedReasons, Diagnostics = s.Diagnostics };
    private static FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport MutateGuardedReport(FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport s, bool gatePassed) => new() { OperationId = s.OperationId, CreatedAt = s.CreatedAt, GuardedRuntimeActivationDryRunPassed = gatePassed, GatePassed = gatePassed, TotalCases = s.TotalCases, PassedCases = s.PassedCases, FailedCases = s.FailedCases, ReadyCases = s.ReadyCases, BlockedCases = s.BlockedCases, Cases = s.Cases, BoundGrantId = s.BoundGrantId, BoundCapability = s.BoundCapability, BoundScope = s.BoundScope, PlannedGuardedActivationContract = s.PlannedGuardedActivationContract, UpstreamActivationDryRunGatePresent = s.UpstreamActivationDryRunGatePresent, UpstreamActivationDryRunGatePassed = s.UpstreamActivationDryRunGatePassed, DryRunOnly = s.DryRunOnly, RuntimeActivationWriteAllowed = s.RuntimeActivationWriteAllowed, RuntimeActivation = s.RuntimeActivation, FormalRetrievalAllowed = s.FormalRetrievalAllowed, RuntimeSwitchAllowed = s.RuntimeSwitchAllowed, ConfigPatchAppliedToRuntime = s.ConfigPatchAppliedToRuntime, PackageOutputChanged = s.PackageOutputChanged, FormalPackageWritten = s.FormalPackageWritten, VectorStoreBindingChanged = s.VectorStoreBindingChanged, GlobalDefaultOn = s.GlobalDefaultOn, Crossed = s.Crossed, ArtifactOnly = s.ArtifactOnly, CapabilityGrantWritten = s.CapabilityGrantWritten, ConfigPatchWritten = s.ConfigPatchWritten, RollbackSnapshotWritten = s.RollbackSnapshotWritten, AuditLogWritten = s.AuditLogWritten, RevocationRecordWritten = s.RevocationRecordWritten, ActivationDryRunOnly = s.ActivationDryRunOnly, RuntimeActivationAllowed = s.RuntimeActivationAllowed, PackingPolicyChanged = s.PackingPolicyChanged, EvidenceCopiedToMainline = s.EvidenceCopiedToMainline, TrustRegistryCopiedToMainline = s.TrustRegistryCopiedToMainline, MainlineEvidencePresent = s.MainlineEvidencePresent, MainlineTrustRegistryPresent = s.MainlineTrustRegistryPresent, ManualReviewRequired = s.ManualReviewRequired, ApprovalSealed = s.ApprovalSealed, GrantApplied = s.GrantApplied, ApplicationApplied = s.ApplicationApplied, RollbackActivated = s.RollbackActivated, PromotionToMainlinePerformed = s.PromotionToMainlinePerformed, NoRuntimeMutationInvariant = s.NoRuntimeMutationInvariant, BlockedReasons = s.BlockedReasons, Diagnostics = s.Diagnostics };
    private static FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport MutateRuntimeReport(FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport s, bool gatePassed) => new() { OperationId = s.OperationId, CreatedAt = s.CreatedAt, RuntimeActivationDryRunPassed = gatePassed, GatePassed = gatePassed, TotalCases = s.TotalCases, PassedCases = s.PassedCases, FailedCases = s.FailedCases, ReadyCases = s.ReadyCases, BlockedCases = s.BlockedCases, Cases = s.Cases, BoundGrantId = s.BoundGrantId, BoundCapability = s.BoundCapability, BoundScope = s.BoundScope, PlannedActivationContract = s.PlannedActivationContract, UpstreamExecutionGatePresent = s.UpstreamExecutionGatePresent, UpstreamExecutionGatePassed = s.UpstreamExecutionGatePassed, ActivationDryRunOnly = s.ActivationDryRunOnly, RuntimeActivationAllowed = s.RuntimeActivationAllowed, RuntimeActivation = s.RuntimeActivation, FormalRetrievalAllowed = s.FormalRetrievalAllowed, RuntimeSwitchAllowed = s.RuntimeSwitchAllowed, ConfigPatchAppliedToRuntime = s.ConfigPatchAppliedToRuntime, Crossed = s.Crossed, ArtifactOnly = s.ArtifactOnly, CapabilityGrantWritten = s.CapabilityGrantWritten, ConfigPatchWritten = s.ConfigPatchWritten, RollbackSnapshotWritten = s.RollbackSnapshotWritten, AuditLogWritten = s.AuditLogWritten, RevocationRecordWritten = s.RevocationRecordWritten, FormalPackageWritten = s.FormalPackageWritten, PackageOutputChanged = s.PackageOutputChanged, PackingPolicyChanged = s.PackingPolicyChanged, VectorStoreBindingChanged = s.VectorStoreBindingChanged, GlobalDefaultOn = s.GlobalDefaultOn, ConfigPatchWrittenLeaked = s.ConfigPatchWrittenLeaked, EvidenceCopiedToMainline = s.EvidenceCopiedToMainline, TrustRegistryCopiedToMainline = s.TrustRegistryCopiedToMainline, MainlineEvidencePresent = s.MainlineEvidencePresent, MainlineTrustRegistryPresent = s.MainlineTrustRegistryPresent, ManualReviewRequired = s.ManualReviewRequired, ApprovalSealed = s.ApprovalSealed, GrantApplied = s.GrantApplied, ApplicationApplied = s.ApplicationApplied, RollbackActivated = s.RollbackActivated, PromotionToMainlinePerformed = s.PromotionToMainlinePerformed, NoRuntimeMutationInvariant = s.NoRuntimeMutationInvariant, BlockedReasons = s.BlockedReasons, Diagnostics = s.Diagnostics };
    private static RuntimeActivationArtifactContentSnapshot MutateSnapshot(RuntimeActivationArtifactContentSnapshot s, GuardedRuntimeActivationRuntimeSwitchArtifactContent? runtimeSwitch = null, GuardedRuntimeActivationAuditArtifactEvent? activationAudit = null, GuardedRuntimeActivationRuntimeGuardManifestContent? runtimeGuard = null, GuardedRuntimeActivationScopeEnforcementManifestContent? scopeManifest = null, GuardedRuntimeActivationRollbackBindingContent? rollbackBinding = null) => new() { RuntimeSwitchPath = s.RuntimeSwitchPath, ActivationAuditPath = s.ActivationAuditPath, RuntimeGuardManifestPath = s.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = s.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = s.ActivationRollbackBindingPath, RuntimeSwitch = runtimeSwitch ?? s.RuntimeSwitch, ActivationAudit = activationAudit ?? s.ActivationAudit, RuntimeGuardManifest = runtimeGuard ?? s.RuntimeGuardManifest, ScopeEnforcementManifest = scopeManifest ?? s.ScopeEnforcementManifest, ActivationRollbackBinding = rollbackBinding ?? s.ActivationRollbackBinding };
    private static RuntimeActivationArtifactContentSnapshot WithoutRuntimeSwitch(RuntimeActivationArtifactContentSnapshot s) => new() { RuntimeSwitchPath = s.RuntimeSwitchPath, ActivationAuditPath = s.ActivationAuditPath, RuntimeGuardManifestPath = s.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = s.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = s.ActivationRollbackBindingPath, RuntimeSwitch = null, ActivationAudit = s.ActivationAudit, RuntimeGuardManifest = s.RuntimeGuardManifest, ScopeEnforcementManifest = s.ScopeEnforcementManifest, ActivationRollbackBinding = s.ActivationRollbackBinding };
    private static RuntimeActivationArtifactContentSnapshot WithoutActivationAudit(RuntimeActivationArtifactContentSnapshot s) => new() { RuntimeSwitchPath = s.RuntimeSwitchPath, ActivationAuditPath = s.ActivationAuditPath, RuntimeGuardManifestPath = s.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = s.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = s.ActivationRollbackBindingPath, RuntimeSwitch = s.RuntimeSwitch, ActivationAudit = null, RuntimeGuardManifest = s.RuntimeGuardManifest, ScopeEnforcementManifest = s.ScopeEnforcementManifest, ActivationRollbackBinding = s.ActivationRollbackBinding };
    private static RuntimeActivationArtifactContentSnapshot WithoutRuntimeGuard(RuntimeActivationArtifactContentSnapshot s) => new() { RuntimeSwitchPath = s.RuntimeSwitchPath, ActivationAuditPath = s.ActivationAuditPath, RuntimeGuardManifestPath = s.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = s.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = s.ActivationRollbackBindingPath, RuntimeSwitch = s.RuntimeSwitch, ActivationAudit = s.ActivationAudit, RuntimeGuardManifest = null, ScopeEnforcementManifest = s.ScopeEnforcementManifest, ActivationRollbackBinding = s.ActivationRollbackBinding };
    private static RuntimeActivationArtifactContentSnapshot WithoutScopeManifest(RuntimeActivationArtifactContentSnapshot s) => new() { RuntimeSwitchPath = s.RuntimeSwitchPath, ActivationAuditPath = s.ActivationAuditPath, RuntimeGuardManifestPath = s.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = s.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = s.ActivationRollbackBindingPath, RuntimeSwitch = s.RuntimeSwitch, ActivationAudit = s.ActivationAudit, RuntimeGuardManifest = s.RuntimeGuardManifest, ScopeEnforcementManifest = null, ActivationRollbackBinding = s.ActivationRollbackBinding };
    private static RuntimeActivationArtifactContentSnapshot WithoutRollbackBinding(RuntimeActivationArtifactContentSnapshot s) => new() { RuntimeSwitchPath = s.RuntimeSwitchPath, ActivationAuditPath = s.ActivationAuditPath, RuntimeGuardManifestPath = s.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = s.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = s.ActivationRollbackBindingPath, RuntimeSwitch = s.RuntimeSwitch, ActivationAudit = s.ActivationAudit, RuntimeGuardManifest = s.RuntimeGuardManifest, ScopeEnforcementManifest = s.ScopeEnforcementManifest, ActivationRollbackBinding = null };
    private static RuntimeActivationArtifactReferenceExistence MutateRefs(RuntimeActivationArtifactReferenceExistence s, bool? rollbackExists = null, bool? revocationExists = null, bool? configPatchExists = null) => new() { RollbackSnapshotPath = s.RollbackSnapshotPath, RevocationRecordPath = s.RevocationRecordPath, ConfigPatchSourcePath = s.ConfigPatchSourcePath, RollbackSnapshotExists = rollbackExists ?? s.RollbackSnapshotExists, RevocationRecordExists = revocationExists ?? s.RevocationRecordExists, ConfigPatchExists = configPatchExists ?? s.ConfigPatchExists, RollbackSnapshot = s.RollbackSnapshot, RevocationRecord = s.RevocationRecord, ConfigPatch = s.ConfigPatch };
    private static GuardedRuntimeActivationRuntimeSwitchArtifactContent CloneRuntimeSwitch(GuardedRuntimeActivationRuntimeSwitchArtifactContent s, string? boundGrantId = null, string? capability = null, string? scope = null, bool? applyToRuntime = null, bool? runtimeActivation = null, bool? formalRetrievalAllowed = null, bool? runtimeSwitchAllowed = null, string? sourceOperationId = null) => new() { SwitchId = s.SwitchId, BoundGrantId = boundGrantId ?? s.BoundGrantId, Capability = capability ?? s.Capability, Scope = scope ?? s.Scope, SwitchMode = s.SwitchMode, ApplyToRuntime = applyToRuntime ?? s.ApplyToRuntime, RuntimeActivation = runtimeActivation ?? s.RuntimeActivation, FormalRetrievalAllowed = formalRetrievalAllowed ?? s.FormalRetrievalAllowed, RuntimeSwitchAllowed = runtimeSwitchAllowed ?? s.RuntimeSwitchAllowed, SourceGuardedRuntimeActivationDryRunOperationId = sourceOperationId ?? s.SourceGuardedRuntimeActivationDryRunOperationId, CreatedAt = s.CreatedAt };
    private static GuardedRuntimeActivationAuditArtifactEvent CloneAudit(GuardedRuntimeActivationAuditArtifactEvent s, string? boundGrantId = null, string? capability = null, string? scope = null, bool? runtimeActivation = null, bool? formalRetrievalAllowed = null, bool? runtimeSwitchAllowed = null) => new() { EventId = s.EventId, EventType = s.EventType, BoundGrantId = boundGrantId ?? s.BoundGrantId, Capability = capability ?? s.Capability, Scope = scope ?? s.Scope, RuntimeActivationArtifactsWritten = s.RuntimeActivationArtifactsWritten, RuntimeActivation = runtimeActivation ?? s.RuntimeActivation, FormalRetrievalAllowed = formalRetrievalAllowed ?? s.FormalRetrievalAllowed, RuntimeSwitchAllowed = runtimeSwitchAllowed ?? s.RuntimeSwitchAllowed, SourceGuardedRuntimeActivationDryRunOperationId = s.SourceGuardedRuntimeActivationDryRunOperationId, Timestamp = s.Timestamp };
    private static GuardedRuntimeActivationRuntimeGuardManifestContent CloneRuntimeGuard(GuardedRuntimeActivationRuntimeGuardManifestContent s, bool? runtimeActivationAllowed = null) => new() { BoundGrantId = s.BoundGrantId, Scope = s.Scope, KillSwitchRequired = s.KillSwitchRequired, ScopeGuardRequired = s.ScopeGuardRequired, RollbackRequired = s.RollbackRequired, RuntimeActivationAllowed = runtimeActivationAllowed ?? s.RuntimeActivationAllowed, CreatedAt = s.CreatedAt };
    private static GuardedRuntimeActivationScopeEnforcementManifestContent CloneScopeManifest(GuardedRuntimeActivationScopeEnforcementManifestContent s, bool? globalDefaultOn = null) => new() { BoundGrantId = s.BoundGrantId, AllowedScope = s.AllowedScope, GlobalDefaultOn = globalDefaultOn ?? s.GlobalDefaultOn, WildcardScopeAllowed = s.WildcardScopeAllowed, CreatedAt = s.CreatedAt };
    private static GuardedRuntimeActivationRollbackBindingContent CloneRollbackBinding(GuardedRuntimeActivationRollbackBindingContent s, bool? runtimeActivation = null) => new() { BoundGrantId = s.BoundGrantId, RollbackSnapshotReference = s.RollbackSnapshotReference, RevocationRecordReference = s.RevocationRecordReference, ConfigPatchSourceReference = s.ConfigPatchSourceReference, RestoreTestRequired = s.RestoreTestRequired, RuntimeActivation = runtimeActivation ?? s.RuntimeActivation, CreatedAt = s.CreatedAt };
    private static RuntimeActivationArtifactContentSnapshot LoadRealArtifactSnapshot(FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? r) { var p = BuildArtifactPaths(r); return new RuntimeActivationArtifactContentSnapshot { RuntimeSwitchPath = p[0], ActivationAuditPath = p[1], RuntimeGuardManifestPath = p[2], ScopeEnforcementManifestPath = p[3], ActivationRollbackBindingPath = p[4], RuntimeSwitch = ReadJsonFile<GuardedRuntimeActivationRuntimeSwitchArtifactContent>(p[0]), ActivationAudit = ReadJsonLine<GuardedRuntimeActivationAuditArtifactEvent>(p[1]), RuntimeGuardManifest = ReadJsonFile<GuardedRuntimeActivationRuntimeGuardManifestContent>(p[2]), ScopeEnforcementManifest = ReadJsonFile<GuardedRuntimeActivationScopeEnforcementManifestContent>(p[3]), ActivationRollbackBinding = ReadJsonFile<GuardedRuntimeActivationRollbackBindingContent>(p[4]) }; }
    private static RuntimeActivationArtifactReferenceExistence BuildRealReferenceExistence(FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? report, RuntimeActivationArtifactContentSnapshot snapshot) { var rb = snapshot.ActivationRollbackBinding; var rollbackPath = rb?.RollbackSnapshotReference ?? report?.PlannedGuardedActivationContract.ReferencedRollbackSnapshotPath ?? string.Empty; var revocationPath = rb?.RevocationRecordReference ?? report?.PlannedGuardedActivationContract.ReferencedRevocationRecordPath ?? string.Empty; var configPath = rb?.ConfigPatchSourceReference ?? report?.PlannedGuardedActivationContract.ReferencedConfigPatchSourcePath ?? string.Empty; return new RuntimeActivationArtifactReferenceExistence { RollbackSnapshotPath = rollbackPath, RevocationRecordPath = revocationPath, ConfigPatchSourcePath = configPath, RollbackSnapshotExists = File.Exists(rollbackPath), RevocationRecordExists = File.Exists(revocationPath), ConfigPatchExists = File.Exists(configPath), RollbackSnapshot = ReadJsonFile<CrossingRollbackSnapshotContent>(rollbackPath), RevocationRecord = ReadJsonFile<CrossingRevocationRecordContent>(revocationPath), ConfigPatch = ReadJsonFile<CrossingRuntimeConfigPatchContent>(configPath) }; }
    private static string[] BuildArtifactPaths(FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? report) => report?.WrittenArtifactPaths.Count >= 5 ? [report.WrittenArtifactPaths[0], report.WrittenArtifactPaths[1], report.WrittenArtifactPaths[2], report.WrittenArtifactPaths[3], report.WrittenArtifactPaths[4]] : [report?.PlannedGuardedActivationContract.PlannedRuntimeSwitchArtifactPath ?? string.Empty, report?.PlannedGuardedActivationContract.PlannedActivationAuditArtifactPath ?? string.Empty, report?.PlannedGuardedActivationContract.PlannedRuntimeGuardManifestPath ?? string.Empty, report?.PlannedGuardedActivationContract.PlannedScopeEnforcementManifestPath ?? string.Empty, report?.PlannedGuardedActivationContract.PlannedActivationRollbackBindingPath ?? string.Empty];
    private static T? ReadJsonFile<T>(string path) where T : class => string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
    private static T? ReadJsonLine<T>(string path) where T : class => string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : JsonSerializer.Deserialize<T>(File.ReadLines(path).FirstOrDefault() ?? string.Empty, JsonOptions);
    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport r) { var b = new StringBuilder(); b.AppendLine($"# {title}"); b.AppendLine(); b.AppendLine($"- RuntimeActivationArtifactIntegrityPassed: `{r.RuntimeActivationArtifactIntegrityPassed}`"); b.AppendLine($"- GatePassed: `{r.GatePassed}`"); b.AppendLine($"- TotalCases: `{r.TotalCases}` PassedCases: `{r.PassedCases}` FailedCases: `{r.FailedCases}`"); b.AppendLine($"- ContentVerifiedArtifactCount: `{r.ContentVerifiedArtifactCount}`"); b.AppendLine($"- LiveActivationDryRunContractComplete: `{r.LiveActivationDryRunContractComplete}`"); b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}` FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}` RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`"); if (r.BlockedReasons.Count > 0) { b.AppendLine(); b.AppendLine("## Blocked Reasons"); foreach (var reason in r.BlockedReasons) b.AppendLine($"- `{reason}`"); } return b.ToString(); }
}

public sealed record FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityScenario(string CaseName, FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? WriteOutReport, FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport? GuardedDryRunReport, FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport? RuntimeActivationDryRunReport, RuntimeActivationArtifactContentSnapshot Snapshot, RuntimeActivationArtifactReferenceExistence ReferenceExistence, bool RtPassed, bool P15Passed, bool MainlineEvidencePresent, bool MainlineRegistryPresent, string ExpectedStatus, string? ExpectedBlockedReason);
public sealed class FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityCase { public string CaseName { get; init; } = string.Empty; public string ExpectedStatus { get; init; } = string.Empty; public string ActualStatus { get; init; } = string.Empty; public string ExpectedBlockedReason { get; init; } = string.Empty; public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>(); public string BoundGrantId { get; init; } = string.Empty; public string BoundCapability { get; init; } = string.Empty; public string BoundScope { get; init; } = string.Empty; public int ContentVerifiedArtifactCount { get; init; } public bool LiveActivationDryRunContractComplete { get; init; } public bool RuntimeActivation { get; init; } public bool FormalRetrievalAllowed { get; init; } public bool RuntimeSwitchAllowed { get; init; } public bool PackageOutputChanged { get; init; } public string Reasoning { get; init; } = string.Empty; public bool StatusMatched { get; init; } public bool BlockedReasonMatched { get; init; } public bool PassedAsExpected { get; init; } }
public sealed class FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport { public string OperationId { get; init; } = string.Empty; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow; public bool RuntimeActivationArtifactIntegrityPassed { get; init; } public bool GatePassed { get; init; } public int TotalCases { get; init; } public int PassedCases { get; init; } public int FailedCases { get; init; } public int VerifiedCases { get; init; } public int BlockedCases { get; init; } public IReadOnlyList<FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityCase>(); public string BoundGrantId { get; init; } = string.Empty; public string BoundCapability { get; init; } = string.Empty; public string BoundScope { get; init; } = string.Empty; public bool UpstreamArtifactWriteOutGatePresent { get; init; } public bool UpstreamArtifactWriteOutGatePassed { get; init; } public bool UpstreamGuardedRuntimeActivationDryRunGatePresent { get; init; } public bool UpstreamGuardedRuntimeActivationDryRunGatePassed { get; init; } public bool UpstreamRuntimeActivationDryRunGatePresent { get; init; } public bool UpstreamRuntimeActivationDryRunGatePassed { get; init; } public IReadOnlyList<string> WrittenArtifactPaths { get; init; } = Array.Empty<string>(); public int ContentVerifiedArtifactCount { get; init; } public bool AllRuntimeActivationArtifactsContentVerified { get; init; } public LiveActivationDryRunContract LiveActivationDryRunContract { get; init; } = new(); public bool LiveActivationDryRunContractComplete { get; init; } public bool RuntimeActivation { get; init; } public bool FormalRetrievalAllowed { get; init; } public bool RuntimeSwitchAllowed { get; init; } public bool ConfigPatchAppliedToRuntime { get; init; } public bool FormalPackageWritten { get; init; } public bool PackageOutputChanged { get; init; } public bool PackingPolicyChanged { get; init; } public bool VectorStoreBindingChanged { get; init; } public bool GlobalDefaultOn { get; init; } public bool PromotionToMainlinePerformed { get; init; } public bool MainlineEvidencePresent { get; init; } public bool MainlineTrustRegistryPresent { get; init; } public bool NoRuntimeMutationInvariant { get; init; } public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>(); public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>(); }
public sealed class FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityOptions { public bool Enabled { get; init; } = true; public bool IsGate { get; init; } }


