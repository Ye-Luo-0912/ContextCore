using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LiveRuntimeActivationExecutionDryRunStatuses
{
    public const string Ready = "LiveRuntimeActivationExecutionDryRunReady";
    public const string Blocked = "LiveRuntimeActivationExecutionDryRunBlocked";
}

public static class LiveRuntimeActivationExecutionDryRunBlockedReasons
{
    public const string IntegrityGateMissing = nameof(IntegrityGateMissing);
    public const string IntegrityGateNotPassed = nameof(IntegrityGateNotPassed);
    public const string IntegrityVerifiedCaseMissing = nameof(IntegrityVerifiedCaseMissing);
    public const string ContentVerifiedArtifactCountNotFive = nameof(ContentVerifiedArtifactCountNotFive);
    public const string AllRuntimeActivationArtifactsContentVerifiedFalse = nameof(AllRuntimeActivationArtifactsContentVerifiedFalse);
    public const string LiveActivationDryRunContractCompleteFalse = nameof(LiveActivationDryRunContractCompleteFalse);
    public const string BoundGrantIdEmpty = nameof(BoundGrantIdEmpty);
    public const string BoundCapabilityMismatch = nameof(BoundCapabilityMismatch);
    public const string BoundScopeMismatch = nameof(BoundScopeMismatch);
    public const string WriteOutGateMissing = nameof(WriteOutGateMissing);
    public const string WriteOutGateNotPassed = nameof(WriteOutGateNotPassed);
    public const string ContractBoundGrantIdMismatch = nameof(ContractBoundGrantIdMismatch);
    public const string ContractSourceOperationIdMissing = nameof(ContractSourceOperationIdMissing);
    public const string RuntimeSwitchArtifactMissing = nameof(RuntimeSwitchArtifactMissing);
    public const string ActivationAuditArtifactMissing = nameof(ActivationAuditArtifactMissing);
    public const string RuntimeGuardManifestMissing = nameof(RuntimeGuardManifestMissing);
    public const string ScopeEnforcementManifestMissing = nameof(ScopeEnforcementManifestMissing);
    public const string ActivationRollbackBindingMissing = nameof(ActivationRollbackBindingMissing);
    public const string RollbackSnapshotReferenceNotFound = nameof(RollbackSnapshotReferenceNotFound);
    public const string RevocationRecordReferenceNotFound = nameof(RevocationRecordReferenceNotFound);
    public const string ConfigPatchReferenceNotFound = nameof(ConfigPatchReferenceNotFound);
    public const string RuntimeActivationTrueInUpstream = nameof(RuntimeActivationTrueInUpstream);
    public const string FormalRetrievalAllowedTrueInUpstream = nameof(FormalRetrievalAllowedTrueInUpstream);
    public const string RuntimeSwitchAllowedTrueInUpstream = nameof(RuntimeSwitchAllowedTrueInUpstream);
    public const string PackageOutputChangedTrueInUpstream = nameof(PackageOutputChangedTrueInUpstream);
    public const string KillSwitchCheckMissing = nameof(KillSwitchCheckMissing);
    public const string ScopeGuardCheckMissing = nameof(ScopeGuardCheckMissing);
    public const string RollbackBindingCheckMissing = nameof(RollbackBindingCheckMissing);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
}

public sealed class LiveRuntimeActivationExecutionPlan
{
    public string PlanId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string Capability { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string ExecutionMode { get; init; } = "ScopedNoOpProbe";
    public bool ApplyRuntimeSwitch { get; init; }
    public bool EnableFormalRetrieval { get; init; }
    public bool KillSwitchCheckRequired { get; init; } = true;
    public bool ScopeGuardCheckRequired { get; init; } = true;
    public bool RollbackBindingCheckRequired { get; init; } = true;
    public bool PackageOutputChangeAllowed { get; init; }
}

public sealed class ScopedRuntimeActivationNoOpProbe
{
    public bool ProbeExecuted { get; init; }
    public string ProbeMode { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public bool RuntimeStateChanged { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool KillSwitchCheckPassed { get; init; }
    public bool ScopeGuardCheckPassed { get; init; }
    public bool RollbackBindingCheckPassed { get; init; }
}

public sealed class LiveRuntimeActivationExecutionDryRunDecision
{
    public string Status { get; init; } = LiveRuntimeActivationExecutionDryRunStatuses.Blocked;
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public bool IntegrityVerifiedCasePresent { get; init; }
    public int ContentVerifiedArtifactCount { get; init; }
    public bool AllRuntimeActivationArtifactsContentVerified { get; init; }
    public bool LiveActivationDryRunContractComplete { get; init; }
    public LiveRuntimeActivationExecutionPlan ExecutionPlan { get; init; } = new();
    public ScopedRuntimeActivationNoOpProbe Probe { get; init; } = new();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
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

public static class FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunPolicy
{
    private const string AllowedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string AllowedScope = "demo-workspace/demo-collection";

    public static LiveRuntimeActivationExecutionDryRunDecision Evaluate(
        FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport? integrityReport,
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? writeOutReport,
        RuntimeActivationArtifactContentSnapshot snapshot,
        RuntimeActivationArtifactReferenceExistence refs,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        var contract = integrityReport?.LiveActivationDryRunContract ?? new LiveActivationDryRunContract();
        var grantId = !string.IsNullOrWhiteSpace(contract.BoundGrantId) ? contract.BoundGrantId : integrityReport?.BoundGrantId ?? writeOutReport?.BoundGrantId ?? string.Empty;
        var capability = !string.IsNullOrWhiteSpace(contract.BoundCapability) ? contract.BoundCapability : integrityReport?.BoundCapability ?? writeOutReport?.BoundCapability ?? string.Empty;
        var scope = !string.IsNullOrWhiteSpace(contract.BoundScope) ? contract.BoundScope : integrityReport?.BoundScope ?? writeOutReport?.BoundScope ?? string.Empty;
        var verifiedCasePresent = integrityReport?.Cases.Any(static c => string.Equals(c.ActualStatus, RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityVerified, StringComparison.Ordinal)) == true;

        if (integrityReport is null) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.IntegrityGateMissing);
        else
        {
            if (!integrityReport.RuntimeActivationArtifactIntegrityPassed || !integrityReport.GatePassed) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.IntegrityGateNotPassed);
            if (!verifiedCasePresent) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.IntegrityVerifiedCaseMissing);
            if (integrityReport.ContentVerifiedArtifactCount != 5) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.ContentVerifiedArtifactCountNotFive);
            if (!integrityReport.AllRuntimeActivationArtifactsContentVerified) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.AllRuntimeActivationArtifactsContentVerifiedFalse);
            if (!integrityReport.LiveActivationDryRunContractComplete) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.LiveActivationDryRunContractCompleteFalse);
            if (string.IsNullOrWhiteSpace(grantId)) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.BoundGrantIdEmpty);
            if (!string.Equals(capability, AllowedCapability, StringComparison.Ordinal)) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.BoundCapabilityMismatch);
            if (!string.Equals(scope, AllowedScope, StringComparison.Ordinal)) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.BoundScopeMismatch);
            if (integrityReport.RuntimeActivation) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.RuntimeActivationTrueInUpstream);
            if (integrityReport.FormalRetrievalAllowed) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.FormalRetrievalAllowedTrueInUpstream);
            if (integrityReport.RuntimeSwitchAllowed) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.RuntimeSwitchAllowedTrueInUpstream);
            if (integrityReport.PackageOutputChanged) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.PackageOutputChangedTrueInUpstream);
        }

        if (writeOutReport is null) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.WriteOutGateMissing);
        else
        {
            if (!writeOutReport.GuardedRuntimeActivationArtifactWriteOutPassed || !writeOutReport.GatePassed) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.WriteOutGateNotPassed);
            if (!string.IsNullOrWhiteSpace(grantId) && !string.Equals(writeOutReport.BoundGrantId, grantId, StringComparison.Ordinal)) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.ContractBoundGrantIdMismatch);
        }

        if (string.IsNullOrWhiteSpace(contract.SourceGuardedRuntimeActivationDryRunOperationId)) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.ContractSourceOperationIdMissing);
        if (snapshot.RuntimeSwitch is null || string.IsNullOrWhiteSpace(snapshot.RuntimeSwitchPath)) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.RuntimeSwitchArtifactMissing);
        if (snapshot.ActivationAudit is null || string.IsNullOrWhiteSpace(snapshot.ActivationAuditPath)) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.ActivationAuditArtifactMissing);
        if (snapshot.RuntimeGuardManifest is null || string.IsNullOrWhiteSpace(snapshot.RuntimeGuardManifestPath)) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.RuntimeGuardManifestMissing);
        if (snapshot.ScopeEnforcementManifest is null || string.IsNullOrWhiteSpace(snapshot.ScopeEnforcementManifestPath)) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.ScopeEnforcementManifestMissing);
        if (snapshot.ActivationRollbackBinding is null || string.IsNullOrWhiteSpace(snapshot.ActivationRollbackBindingPath)) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.ActivationRollbackBindingMissing);
        if (!refs.RollbackSnapshotExists) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.RollbackSnapshotReferenceNotFound);
        if (!refs.RevocationRecordExists) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.RevocationRecordReferenceNotFound);
        if (!refs.ConfigPatchExists) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.ConfigPatchReferenceNotFound);

        var killSwitchCheckPassed = snapshot.RuntimeGuardManifest?.KillSwitchRequired == true;
        var scopeGuardCheckPassed = snapshot.ScopeEnforcementManifest is not null && string.Equals(snapshot.ScopeEnforcementManifest.AllowedScope, AllowedScope, StringComparison.Ordinal) && !snapshot.ScopeEnforcementManifest.GlobalDefaultOn && !snapshot.ScopeEnforcementManifest.WildcardScopeAllowed;
        var rollbackBindingCheckPassed = snapshot.ActivationRollbackBinding is not null && snapshot.ActivationRollbackBinding.RestoreTestRequired && !snapshot.ActivationRollbackBinding.RuntimeActivation && refs.RollbackSnapshotExists && refs.RevocationRecordExists && refs.ConfigPatchExists;
        if (!killSwitchCheckPassed) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.KillSwitchCheckMissing);
        if (!scopeGuardCheckPassed) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.ScopeGuardCheckMissing);
        if (!rollbackBindingCheckPassed) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.RollbackBindingCheckMissing);
        if (!rtPassed) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.MainlineTrustRegistryPresent);

        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LiveRuntimeActivationExecutionDryRunDecision
        {
            Status = ready ? LiveRuntimeActivationExecutionDryRunStatuses.Ready : LiveRuntimeActivationExecutionDryRunStatuses.Blocked,
            BoundGrantId = grantId,
            BoundCapability = capability,
            BoundScope = scope,
            IntegrityVerifiedCasePresent = verifiedCasePresent,
            ContentVerifiedArtifactCount = integrityReport?.ContentVerifiedArtifactCount ?? 0,
            AllRuntimeActivationArtifactsContentVerified = integrityReport?.AllRuntimeActivationArtifactsContentVerified ?? false,
            LiveActivationDryRunContractComplete = integrityReport?.LiveActivationDryRunContractComplete ?? false,
            ExecutionPlan = new LiveRuntimeActivationExecutionPlan { PlanId = $"frp-live-runtime-activation-execution-plan-{Guid.NewGuid():N}", BoundGrantId = grantId, Capability = AllowedCapability, Scope = AllowedScope, ExecutionMode = "ScopedNoOpProbe", ApplyRuntimeSwitch = false, EnableFormalRetrieval = false, KillSwitchCheckRequired = true, ScopeGuardCheckRequired = true, RollbackBindingCheckRequired = true, PackageOutputChangeAllowed = false },
            Probe = new ScopedRuntimeActivationNoOpProbe { ProbeExecuted = ready, ProbeMode = ready ? "NoOp" : "NotExecuted", Scope = AllowedScope, RuntimeStateChanged = false, RuntimeActivation = false, FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false, PackageOutputChanged = false, KillSwitchCheckPassed = killSwitchCheckPassed, ScopeGuardCheckPassed = scopeGuardCheckPassed, RollbackBindingCheckPassed = rollbackBindingCheckPassed },
            BlockedReasons = finalBlocked,
            Reasoning = ready ? "live runtime activation execution dry-run verified; scoped no-op probe executed without runtime mutation." : $"{finalBlocked.Length} blocked reason(s); live runtime activation execution dry-run blocked.",
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
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

public sealed record FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunScenario(string CaseName, FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport? IntegrityReport, FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? WriteOutReport, RuntimeActivationArtifactContentSnapshot Snapshot, RuntimeActivationArtifactReferenceExistence ReferenceExistence, bool RtPassed, bool P15Passed, bool MainlineEvidencePresent, bool MainlineRegistryPresent, string ExpectedStatus, string? ExpectedBlockedReason);
public sealed class FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport Run(FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport? integrityReport, FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? writeOutReport, bool rtPassed, bool p15Passed, bool mainlineEvidencePresent, bool mainlineRegistryPresent, FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunPolicy.Evaluate(scenario.IntegrityReport, scenario.WriteOutReport, scenario.Snapshot, scenario.ReferenceExistence, scenario.RtPassed, scenario.P15Passed, scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                BoundGrantId = decision.BoundGrantId,
                BoundCapability = decision.BoundCapability,
                BoundScope = decision.BoundScope,
                ContentVerifiedArtifactCount = decision.ContentVerifiedArtifactCount,
                LiveActivationDryRunContractComplete = decision.LiveActivationDryRunContractComplete,
                ProbeExecuted = decision.Probe.ProbeExecuted,
                ProbeMode = decision.Probe.ProbeMode,
                RuntimeStateChanged = decision.Probe.RuntimeStateChanged,
                RuntimeActivation = decision.RuntimeActivation,
                FormalRetrievalAllowed = decision.FormalRetrievalAllowed,
                RuntimeSwitchAllowed = decision.RuntimeSwitchAllowed,
                PackageOutputChanged = decision.PackageOutputChanged,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                PassedAsExpected = statusMatched && blockedReasonMatched && !decision.RuntimeActivation && !decision.FormalRetrievalAllowed && !decision.RuntimeSwitchAllowed && !decision.PackageOutputChanged && !decision.FormalPackageWritten && !decision.PackingPolicyChanged && !decision.VectorStoreBindingChanged && !decision.GlobalDefaultOn && !decision.PromotionToMainlinePerformed && decision.NoRuntimeMutationInvariant
            };
        }).ToArray();

        var blocked = new List<string>();
        if (cases.Length < 25) blocked.Add("InsufficientLiveRuntimeActivationExecutionDryRunCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LiveRuntimeActivationExecutionDryRunMatrixFailed");
        foreach (var status in new[] { LiveRuntimeActivationExecutionDryRunStatuses.Ready, LiveRuntimeActivationExecutionDryRunStatuses.Blocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal))) blocked.Add($"StatusBranchNotCovered:{status}");
        }
        if (cases.Any(static c => c.RuntimeActivation)) blocked.Add("RuntimeActivationLeaked");
        if (cases.Any(static c => c.FormalRetrievalAllowed)) blocked.Add("FormalRetrievalAllowedLeaked");
        if (cases.Any(static c => c.RuntimeSwitchAllowed)) blocked.Add("RuntimeSwitchAllowedLeaked");
        if (cases.Any(static c => c.PackageOutputChanged)) blocked.Add("PackageOutputChangedLeaked");

        var realSnapshot = LoadRealArtifactSnapshot(integrityReport, writeOutReport);
        var realRefs = BuildRealReferenceExistence(integrityReport, writeOutReport, realSnapshot);
        var realDecision = FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunPolicy.Evaluate(integrityReport, writeOutReport, realSnapshot, realRefs, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (integrityReport is null) blocked.Add("RealRuntimeActivationArtifactIntegrityGateArtifactMissing");
        else if (!integrityReport.GatePassed) blocked.Add("RealRuntimeActivationArtifactIntegrityGateNotPassed");
        if (writeOutReport is null) blocked.Add("RealGuardedRuntimeActivationArtifactWriteOutGateArtifactMissing");
        else if (!writeOutReport.GatePassed) blocked.Add("RealGuardedRuntimeActivationArtifactWriteOutGateNotPassed");
        if (!string.Equals(realDecision.Status, LiveRuntimeActivationExecutionDryRunStatuses.Ready, StringComparison.Ordinal)) blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLiveRuntimeActivationExecutionDryRun:{x}"));
        if (!rtPassed) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LiveRuntimeActivationExecutionDryRunBlockedReasons.MainlineTrustRegistryPresent);
        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;
        return new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport
        {
            OperationId = $"frp-live-runtime-activation-execution-dry-run-{Guid.NewGuid():N}",
            CreatedAt = now,
            LiveRuntimeActivationExecutionDryRunPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LiveRuntimeActivationExecutionDryRunStatuses.Ready, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, StringComparison.Ordinal)),
            Cases = cases,
            BoundGrantId = realDecision.BoundGrantId,
            BoundCapability = realDecision.BoundCapability,
            BoundScope = realDecision.BoundScope,
            UpstreamRuntimeActivationArtifactIntegrityGatePresent = integrityReport is not null,
            UpstreamRuntimeActivationArtifactIntegrityGatePassed = integrityReport?.GatePassed ?? false,
            UpstreamGuardedRuntimeActivationArtifactWriteOutGatePresent = writeOutReport is not null,
            UpstreamGuardedRuntimeActivationArtifactWriteOutGatePassed = writeOutReport?.GatePassed ?? false,
            RuntimeActivationArtifactIntegrityVerifiedCasePresent = realDecision.IntegrityVerifiedCasePresent,
            ContentVerifiedArtifactCount = realDecision.ContentVerifiedArtifactCount,
            AllRuntimeActivationArtifactsContentVerified = realDecision.AllRuntimeActivationArtifactsContentVerified,
            LiveActivationDryRunContractComplete = realDecision.LiveActivationDryRunContractComplete,
            ExecutionPlan = realDecision.ExecutionPlan,
            Probe = realDecision.Probe,
            ProbeExecuted = realDecision.Probe.ProbeExecuted,
            RuntimeStateChanged = realDecision.Probe.RuntimeStateChanged,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            PromotionToMainlinePerformed = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = finalBlocked,
            Diagnostics = new[] { $"total={cases.Length}", $"realStatus={realDecision.Status}", $"probeExecuted={realDecision.Probe.ProbeExecuted}", $"runtimeGate={rtPassed}", $"p15Gate={p15Passed}", $"mainlineEvidence={mainlineEvidencePresent}", $"mainlineRegistry={mainlineRegistryPresent}" }
        };
    }

    private static IReadOnlyList<FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunScenario> BuildScenarios()
    {
        var cleanWriteOut = FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner.BuildCleanArtifactWriteOutReport();
        var cleanGuarded = FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner.BuildCleanGuardedRuntimeActivationDryRunReport();
        var cleanIntegrity = BuildCleanIntegrityReport(cleanWriteOut, cleanGuarded);
        var cleanSnapshot = FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner.BuildCleanArtifactSnapshot(cleanIntegrity.LiveActivationDryRunContract.BoundGrantId, cleanIntegrity.LiveActivationDryRunContract.SourceGuardedRuntimeActivationDryRunOperationId);
        var cleanRefs = FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner.BuildCleanReferenceExistence(cleanIntegrity.LiveActivationDryRunContract.BoundGrantId, cleanIntegrity.LiveActivationDryRunContract.BoundCapability, cleanIntegrity.LiveActivationDryRunContract.BoundScope);
        return [
            new("AllUpstreamClean", cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Ready, null),
            new("IntegrityGateMissing", null, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.IntegrityGateMissing),
            new("IntegrityGateNotPassed", CloneIntegrity(cleanIntegrity, passed:false, gatePassed:false), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.IntegrityGateNotPassed),
            new("IntegrityVerifiedCaseMissing", CloneIntegrity(cleanIntegrity, cases:[new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityCase { CaseName = "BlockedOnly", ExpectedStatus = RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked, ActualStatus = RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityBlocked }]), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.IntegrityVerifiedCaseMissing),
            new("ContentVerifiedArtifactCountNotFive", CloneIntegrity(cleanIntegrity, contentCount:4), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.ContentVerifiedArtifactCountNotFive),
            new("AllRuntimeActivationArtifactsContentVerifiedFalse", CloneIntegrity(cleanIntegrity, allContent:false), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.AllRuntimeActivationArtifactsContentVerifiedFalse),
            new("LiveActivationDryRunContractCompleteFalse", CloneIntegrity(cleanIntegrity, contractComplete:false), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.LiveActivationDryRunContractCompleteFalse),
            new("BoundGrantIdEmpty", CloneIntegrity(cleanIntegrity, grantId:string.Empty, contract:CloneContract(cleanIntegrity.LiveActivationDryRunContract, grantId:string.Empty)), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.BoundGrantIdEmpty),
            new("CapabilityMismatch", CloneIntegrity(cleanIntegrity, capability:"OtherCapability", contract:CloneContract(cleanIntegrity.LiveActivationDryRunContract, capability:"OtherCapability")), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.BoundCapabilityMismatch),
            new("ScopeMismatch", CloneIntegrity(cleanIntegrity, scope:"other/other", contract:CloneContract(cleanIntegrity.LiveActivationDryRunContract, scope:"other/other")), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.BoundScopeMismatch),
            new("WriteOutGateMissing", cleanIntegrity, null, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.WriteOutGateMissing),
            new("WriteOutGateNotPassed", cleanIntegrity, CloneWriteOut(cleanWriteOut, gatePassed:false), cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.WriteOutGateNotPassed),
            new("ContractBoundGrantIdMismatch", cleanIntegrity, CloneWriteOut(cleanWriteOut, grantId:"other-grant"), cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.ContractBoundGrantIdMismatch),
            new("ContractSourceOperationIdMissing", CloneIntegrity(cleanIntegrity, contract:CloneContract(cleanIntegrity.LiveActivationDryRunContract, sourceOp:string.Empty)), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.ContractSourceOperationIdMissing),
            new("RuntimeSwitchArtifactMissing", cleanIntegrity, cleanWriteOut, new RuntimeActivationArtifactContentSnapshot { RuntimeSwitchPath = cleanSnapshot.RuntimeSwitchPath, ActivationAuditPath = cleanSnapshot.ActivationAuditPath, RuntimeGuardManifestPath = cleanSnapshot.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = cleanSnapshot.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = cleanSnapshot.ActivationRollbackBindingPath, RuntimeSwitch = null, ActivationAudit = cleanSnapshot.ActivationAudit, RuntimeGuardManifest = cleanSnapshot.RuntimeGuardManifest, ScopeEnforcementManifest = cleanSnapshot.ScopeEnforcementManifest, ActivationRollbackBinding = cleanSnapshot.ActivationRollbackBinding }, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.RuntimeSwitchArtifactMissing),
            new("ActivationAuditArtifactMissing", cleanIntegrity, cleanWriteOut, new RuntimeActivationArtifactContentSnapshot { RuntimeSwitchPath = cleanSnapshot.RuntimeSwitchPath, ActivationAuditPath = cleanSnapshot.ActivationAuditPath, RuntimeGuardManifestPath = cleanSnapshot.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = cleanSnapshot.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = cleanSnapshot.ActivationRollbackBindingPath, RuntimeSwitch = cleanSnapshot.RuntimeSwitch, ActivationAudit = null, RuntimeGuardManifest = cleanSnapshot.RuntimeGuardManifest, ScopeEnforcementManifest = cleanSnapshot.ScopeEnforcementManifest, ActivationRollbackBinding = cleanSnapshot.ActivationRollbackBinding }, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.ActivationAuditArtifactMissing),
            new("RuntimeGuardManifestMissing", cleanIntegrity, cleanWriteOut, new RuntimeActivationArtifactContentSnapshot { RuntimeSwitchPath = cleanSnapshot.RuntimeSwitchPath, ActivationAuditPath = cleanSnapshot.ActivationAuditPath, RuntimeGuardManifestPath = cleanSnapshot.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = cleanSnapshot.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = cleanSnapshot.ActivationRollbackBindingPath, RuntimeSwitch = cleanSnapshot.RuntimeSwitch, ActivationAudit = cleanSnapshot.ActivationAudit, RuntimeGuardManifest = null, ScopeEnforcementManifest = cleanSnapshot.ScopeEnforcementManifest, ActivationRollbackBinding = cleanSnapshot.ActivationRollbackBinding }, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.RuntimeGuardManifestMissing),
            new("ScopeEnforcementManifestMissing", cleanIntegrity, cleanWriteOut, new RuntimeActivationArtifactContentSnapshot { RuntimeSwitchPath = cleanSnapshot.RuntimeSwitchPath, ActivationAuditPath = cleanSnapshot.ActivationAuditPath, RuntimeGuardManifestPath = cleanSnapshot.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = cleanSnapshot.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = cleanSnapshot.ActivationRollbackBindingPath, RuntimeSwitch = cleanSnapshot.RuntimeSwitch, ActivationAudit = cleanSnapshot.ActivationAudit, RuntimeGuardManifest = cleanSnapshot.RuntimeGuardManifest, ScopeEnforcementManifest = null, ActivationRollbackBinding = cleanSnapshot.ActivationRollbackBinding }, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.ScopeEnforcementManifestMissing),
            new("ActivationRollbackBindingMissing", cleanIntegrity, cleanWriteOut, new RuntimeActivationArtifactContentSnapshot { RuntimeSwitchPath = cleanSnapshot.RuntimeSwitchPath, ActivationAuditPath = cleanSnapshot.ActivationAuditPath, RuntimeGuardManifestPath = cleanSnapshot.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = cleanSnapshot.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = cleanSnapshot.ActivationRollbackBindingPath, RuntimeSwitch = cleanSnapshot.RuntimeSwitch, ActivationAudit = cleanSnapshot.ActivationAudit, RuntimeGuardManifest = cleanSnapshot.RuntimeGuardManifest, ScopeEnforcementManifest = cleanSnapshot.ScopeEnforcementManifest, ActivationRollbackBinding = null }, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.ActivationRollbackBindingMissing),
            new("RollbackSnapshotReferenceMissing", cleanIntegrity, cleanWriteOut, cleanSnapshot, CloneRefs(cleanRefs, rollbackExists: false), true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.RollbackSnapshotReferenceNotFound),
            new("RevocationRecordReferenceMissing", cleanIntegrity, cleanWriteOut, cleanSnapshot, CloneRefs(cleanRefs, revocationExists: false), true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.RevocationRecordReferenceNotFound),
            new("ConfigPatchReferenceMissing", cleanIntegrity, cleanWriteOut, cleanSnapshot, CloneRefs(cleanRefs, configPatchExists: false), true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.ConfigPatchReferenceNotFound),
            new("RuntimeActivationTrueInUpstream", CloneIntegrity(cleanIntegrity, runtimeActivation:true), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.RuntimeActivationTrueInUpstream),
            new("FormalRetrievalAllowedTrueInUpstream", CloneIntegrity(cleanIntegrity, formalAllowed:true), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.FormalRetrievalAllowedTrueInUpstream),
            new("RuntimeSwitchAllowedTrueInUpstream", CloneIntegrity(cleanIntegrity, runtimeSwitchAllowed:true), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.RuntimeSwitchAllowedTrueInUpstream),
            new("PackageOutputChangedTrueInUpstream", CloneIntegrity(cleanIntegrity, packageOutputChanged:true), cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.PackageOutputChangedTrueInUpstream),
            new("KillSwitchCheckMissing", cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, runtimeGuard: new GuardedRuntimeActivationRuntimeGuardManifestContent { BoundGrantId = cleanSnapshot.RuntimeGuardManifest!.BoundGrantId, Scope = cleanSnapshot.RuntimeGuardManifest.Scope, KillSwitchRequired = false, ScopeGuardRequired = cleanSnapshot.RuntimeGuardManifest.ScopeGuardRequired, RollbackRequired = cleanSnapshot.RuntimeGuardManifest.RollbackRequired, RuntimeActivationAllowed = cleanSnapshot.RuntimeGuardManifest.RuntimeActivationAllowed, CreatedAt = cleanSnapshot.RuntimeGuardManifest.CreatedAt }), cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.KillSwitchCheckMissing),
            new("ScopeGuardCheckMissing", cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, scopeManifest: new GuardedRuntimeActivationScopeEnforcementManifestContent { BoundGrantId = cleanSnapshot.ScopeEnforcementManifest!.BoundGrantId, AllowedScope = string.Empty, GlobalDefaultOn = cleanSnapshot.ScopeEnforcementManifest.GlobalDefaultOn, WildcardScopeAllowed = cleanSnapshot.ScopeEnforcementManifest.WildcardScopeAllowed, CreatedAt = cleanSnapshot.ScopeEnforcementManifest.CreatedAt }), cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.ScopeGuardCheckMissing),
            new("RollbackBindingCheckMissing", cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, rollbackBinding: new GuardedRuntimeActivationRollbackBindingContent { BoundGrantId = cleanSnapshot.ActivationRollbackBinding!.BoundGrantId, RollbackSnapshotReference = cleanSnapshot.ActivationRollbackBinding.RollbackSnapshotReference, RevocationRecordReference = cleanSnapshot.ActivationRollbackBinding.RevocationRecordReference, ConfigPatchSourceReference = cleanSnapshot.ActivationRollbackBinding.ConfigPatchSourceReference, RestoreTestRequired = false, RuntimeActivation = cleanSnapshot.ActivationRollbackBinding.RuntimeActivation, CreatedAt = cleanSnapshot.ActivationRollbackBinding.CreatedAt }), cleanRefs, true, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.RollbackBindingCheckMissing),
            new("RuntimeGateNotPassed", cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, false, true, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, false, false, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, true, false, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, true, LiveRuntimeActivationExecutionDryRunStatuses.Blocked, LiveRuntimeActivationExecutionDryRunBlockedReasons.MainlineTrustRegistryPresent)
        ];
    }

    private static FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport BuildCleanIntegrityReport(FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport writeOut, FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport guarded)
    {
        var contract = new LiveActivationDryRunContract { BoundGrantId = writeOut.BoundGrantId, BoundCapability = writeOut.BoundCapability, BoundScope = writeOut.BoundScope, SourceGuardedRuntimeActivationDryRunOperationId = guarded.OperationId, RuntimeSwitchArtifactPath = writeOut.WrittenArtifactPaths[0], ActivationAuditArtifactPath = writeOut.WrittenArtifactPaths[1], RuntimeGuardManifestPath = writeOut.WrittenArtifactPaths[2], ScopeEnforcementManifestPath = writeOut.WrittenArtifactPaths[3], ActivationRollbackBindingPath = writeOut.WrittenArtifactPaths[4], RollbackSnapshotReference = writeOut.PlannedGuardedActivationContract.ReferencedRollbackSnapshotPath, RevocationRecordReference = writeOut.PlannedGuardedActivationContract.ReferencedRevocationRecordPath, ConfigPatchSourceReference = writeOut.PlannedGuardedActivationContract.ReferencedConfigPatchSourcePath, Complete = true };
        return new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport { OperationId = "frp-runtime-activation-artifact-integrity-fixture", CreatedAt = DateTimeOffset.Parse("2026-06-27T12:00:00Z"), RuntimeActivationArtifactIntegrityPassed = true, GatePassed = true, TotalCases = 38, PassedCases = 38, FailedCases = 0, VerifiedCases = 1, BlockedCases = 37, Cases = [new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityCase { CaseName = "AllUpstreamClean", ExpectedStatus = RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityVerified, ActualStatus = RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityVerified, BoundGrantId = writeOut.BoundGrantId, BoundCapability = writeOut.BoundCapability, BoundScope = writeOut.BoundScope, ContentVerifiedArtifactCount = 5, LiveActivationDryRunContractComplete = true, RuntimeActivation = false, FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false, PackageOutputChanged = false, StatusMatched = true, BlockedReasonMatched = true, PassedAsExpected = true }], BoundGrantId = writeOut.BoundGrantId, BoundCapability = writeOut.BoundCapability, BoundScope = writeOut.BoundScope, WrittenArtifactPaths = writeOut.WrittenArtifactPaths, ContentVerifiedArtifactCount = 5, AllRuntimeActivationArtifactsContentVerified = true, LiveActivationDryRunContract = contract, LiveActivationDryRunContractComplete = true, RuntimeActivation = false, FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false, ConfigPatchAppliedToRuntime = false, FormalPackageWritten = false, PackageOutputChanged = false, PackingPolicyChanged = false, VectorStoreBindingChanged = false, GlobalDefaultOn = false, PromotionToMainlinePerformed = false, MainlineEvidencePresent = false, MainlineTrustRegistryPresent = false, NoRuntimeMutationInvariant = true };
    }
    private static FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport CloneIntegrity(FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport source, bool? passed = null, bool? gatePassed = null, IReadOnlyList<FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityCase>? cases = null, string? grantId = null, string? capability = null, string? scope = null, int? contentCount = null, bool? allContent = null, LiveActivationDryRunContract? contract = null, bool? contractComplete = null, bool? runtimeActivation = null, bool? formalAllowed = null, bool? runtimeSwitchAllowed = null, bool? packageOutputChanged = null)
        => new() { OperationId = source.OperationId, CreatedAt = source.CreatedAt, RuntimeActivationArtifactIntegrityPassed = passed ?? source.RuntimeActivationArtifactIntegrityPassed, GatePassed = gatePassed ?? source.GatePassed, TotalCases = source.TotalCases, PassedCases = source.PassedCases, FailedCases = source.FailedCases, VerifiedCases = source.VerifiedCases, BlockedCases = source.BlockedCases, Cases = cases ?? source.Cases, BoundGrantId = grantId ?? source.BoundGrantId, BoundCapability = capability ?? source.BoundCapability, BoundScope = scope ?? source.BoundScope, WrittenArtifactPaths = source.WrittenArtifactPaths, ContentVerifiedArtifactCount = contentCount ?? source.ContentVerifiedArtifactCount, AllRuntimeActivationArtifactsContentVerified = allContent ?? source.AllRuntimeActivationArtifactsContentVerified, LiveActivationDryRunContract = contract ?? source.LiveActivationDryRunContract, LiveActivationDryRunContractComplete = contractComplete ?? source.LiveActivationDryRunContractComplete, RuntimeActivation = runtimeActivation ?? source.RuntimeActivation, FormalRetrievalAllowed = formalAllowed ?? source.FormalRetrievalAllowed, RuntimeSwitchAllowed = runtimeSwitchAllowed ?? source.RuntimeSwitchAllowed, ConfigPatchAppliedToRuntime = source.ConfigPatchAppliedToRuntime, FormalPackageWritten = source.FormalPackageWritten, PackageOutputChanged = packageOutputChanged ?? source.PackageOutputChanged, PackingPolicyChanged = source.PackingPolicyChanged, VectorStoreBindingChanged = source.VectorStoreBindingChanged, GlobalDefaultOn = source.GlobalDefaultOn, PromotionToMainlinePerformed = source.PromotionToMainlinePerformed, MainlineEvidencePresent = source.MainlineEvidencePresent, MainlineTrustRegistryPresent = source.MainlineTrustRegistryPresent, NoRuntimeMutationInvariant = source.NoRuntimeMutationInvariant };

    private static FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport CloneWriteOut(FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport source, bool? gatePassed = null, string? grantId = null)
        => new() { OperationId = source.OperationId, CreatedAt = source.CreatedAt, GuardedRuntimeActivationArtifactWriteOutPassed = gatePassed ?? source.GuardedRuntimeActivationArtifactWriteOutPassed, GatePassed = gatePassed ?? source.GatePassed, TotalCases = source.TotalCases, PassedCases = source.PassedCases, FailedCases = source.FailedCases, WrittenCases = source.WrittenCases, BlockedCases = source.BlockedCases, Cases = source.Cases, BoundGrantId = grantId ?? source.BoundGrantId, BoundCapability = source.BoundCapability, BoundScope = source.BoundScope, PlannedGuardedActivationContract = source.PlannedGuardedActivationContract, WrittenArtifactPaths = source.WrittenArtifactPaths, RuntimeActivationArtifactsWritten = source.RuntimeActivationArtifactsWritten, RuntimeActivation = source.RuntimeActivation, FormalRetrievalAllowed = source.FormalRetrievalAllowed, RuntimeSwitchAllowed = source.RuntimeSwitchAllowed, ConfigPatchAppliedToRuntime = source.ConfigPatchAppliedToRuntime, FormalPackageWritten = source.FormalPackageWritten, PackageOutputChanged = source.PackageOutputChanged, PackingPolicyChanged = source.PackingPolicyChanged, VectorStoreBindingChanged = source.VectorStoreBindingChanged, GlobalDefaultOn = source.GlobalDefaultOn, Crossed = source.Crossed, ArtifactOnly = source.ArtifactOnly, CapabilityGrantWritten = source.CapabilityGrantWritten, ConfigPatchWritten = source.ConfigPatchWritten, RollbackSnapshotWritten = source.RollbackSnapshotWritten, AuditLogWritten = source.AuditLogWritten, RevocationRecordWritten = source.RevocationRecordWritten, EvidenceCopiedToMainline = source.EvidenceCopiedToMainline, TrustRegistryCopiedToMainline = source.TrustRegistryCopiedToMainline, MainlineEvidencePresent = source.MainlineEvidencePresent, MainlineTrustRegistryPresent = source.MainlineTrustRegistryPresent, ManualReviewRequired = source.ManualReviewRequired, ApprovalSealed = source.ApprovalSealed, GrantApplied = source.GrantApplied, ApplicationApplied = source.ApplicationApplied, RollbackActivated = source.RollbackActivated, PromotionToMainlinePerformed = source.PromotionToMainlinePerformed, NoRuntimeMutationInvariant = source.NoRuntimeMutationInvariant };

    private static LiveActivationDryRunContract CloneContract(LiveActivationDryRunContract source, string? grantId = null, string? capability = null, string? scope = null, string? sourceOp = null)
        => new() { BoundGrantId = grantId ?? source.BoundGrantId, BoundCapability = capability ?? source.BoundCapability, BoundScope = scope ?? source.BoundScope, SourceGuardedRuntimeActivationDryRunOperationId = sourceOp ?? source.SourceGuardedRuntimeActivationDryRunOperationId, RuntimeSwitchArtifactPath = source.RuntimeSwitchArtifactPath, ActivationAuditArtifactPath = source.ActivationAuditArtifactPath, RuntimeGuardManifestPath = source.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = source.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = source.ActivationRollbackBindingPath, RollbackSnapshotReference = source.RollbackSnapshotReference, RevocationRecordReference = source.RevocationRecordReference, ConfigPatchSourceReference = source.ConfigPatchSourceReference, Complete = source.Complete };

    private static RuntimeActivationArtifactContentSnapshot CloneSnapshot(RuntimeActivationArtifactContentSnapshot source, GuardedRuntimeActivationRuntimeSwitchArtifactContent? runtimeSwitch = null, GuardedRuntimeActivationAuditArtifactEvent? activationAudit = null, GuardedRuntimeActivationRuntimeGuardManifestContent? runtimeGuard = null, GuardedRuntimeActivationScopeEnforcementManifestContent? scopeManifest = null, GuardedRuntimeActivationRollbackBindingContent? rollbackBinding = null)
        => new() { RuntimeSwitchPath = source.RuntimeSwitchPath, ActivationAuditPath = source.ActivationAuditPath, RuntimeGuardManifestPath = source.RuntimeGuardManifestPath, ScopeEnforcementManifestPath = source.ScopeEnforcementManifestPath, ActivationRollbackBindingPath = source.ActivationRollbackBindingPath, RuntimeSwitch = runtimeSwitch ?? source.RuntimeSwitch, ActivationAudit = activationAudit ?? source.ActivationAudit, RuntimeGuardManifest = runtimeGuard ?? source.RuntimeGuardManifest, ScopeEnforcementManifest = scopeManifest ?? source.ScopeEnforcementManifest, ActivationRollbackBinding = rollbackBinding ?? source.ActivationRollbackBinding };

    private static RuntimeActivationArtifactReferenceExistence CloneRefs(RuntimeActivationArtifactReferenceExistence source, bool? rollbackExists = null, bool? revocationExists = null, bool? configPatchExists = null)
        => new() { RollbackSnapshotPath = source.RollbackSnapshotPath, RevocationRecordPath = source.RevocationRecordPath, ConfigPatchSourcePath = source.ConfigPatchSourcePath, RollbackSnapshotExists = rollbackExists ?? source.RollbackSnapshotExists, RevocationRecordExists = revocationExists ?? source.RevocationRecordExists, ConfigPatchExists = configPatchExists ?? source.ConfigPatchExists, RollbackSnapshot = source.RollbackSnapshot, RevocationRecord = source.RevocationRecord, ConfigPatch = source.ConfigPatch };

    private static RuntimeActivationArtifactContentSnapshot LoadRealArtifactSnapshot(FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport? integrityReport, FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? writeOutReport)
    {
        var contract = integrityReport?.LiveActivationDryRunContract;
        var fallback = writeOutReport?.WrittenArtifactPaths.Count >= 5
            ? new[]
            {
                writeOutReport.WrittenArtifactPaths[0],
                writeOutReport.WrittenArtifactPaths[1],
                writeOutReport.WrittenArtifactPaths[2],
                writeOutReport.WrittenArtifactPaths[3],
                writeOutReport.WrittenArtifactPaths[4]
            }
            : new[]
            {
                writeOutReport?.PlannedGuardedActivationContract.PlannedRuntimeSwitchArtifactPath ?? string.Empty,
                writeOutReport?.PlannedGuardedActivationContract.PlannedActivationAuditArtifactPath ?? string.Empty,
                writeOutReport?.PlannedGuardedActivationContract.PlannedRuntimeGuardManifestPath ?? string.Empty,
                writeOutReport?.PlannedGuardedActivationContract.PlannedScopeEnforcementManifestPath ?? string.Empty,
                writeOutReport?.PlannedGuardedActivationContract.PlannedActivationRollbackBindingPath ?? string.Empty
            };
        var switchPath = !string.IsNullOrWhiteSpace(contract?.RuntimeSwitchArtifactPath) ? contract.RuntimeSwitchArtifactPath : fallback[0];
        var auditPath = !string.IsNullOrWhiteSpace(contract?.ActivationAuditArtifactPath) ? contract.ActivationAuditArtifactPath : fallback[1];
        var guardPath = !string.IsNullOrWhiteSpace(contract?.RuntimeGuardManifestPath) ? contract.RuntimeGuardManifestPath : fallback[2];
        var scopePath = !string.IsNullOrWhiteSpace(contract?.ScopeEnforcementManifestPath) ? contract.ScopeEnforcementManifestPath : fallback[3];
        var rollbackPath = !string.IsNullOrWhiteSpace(contract?.ActivationRollbackBindingPath) ? contract.ActivationRollbackBindingPath : fallback[4];
        return new RuntimeActivationArtifactContentSnapshot { RuntimeSwitchPath = switchPath, ActivationAuditPath = auditPath, RuntimeGuardManifestPath = guardPath, ScopeEnforcementManifestPath = scopePath, ActivationRollbackBindingPath = rollbackPath, RuntimeSwitch = ReadJsonFile<GuardedRuntimeActivationRuntimeSwitchArtifactContent>(switchPath), ActivationAudit = ReadJsonLine<GuardedRuntimeActivationAuditArtifactEvent>(auditPath), RuntimeGuardManifest = ReadJsonFile<GuardedRuntimeActivationRuntimeGuardManifestContent>(guardPath), ScopeEnforcementManifest = ReadJsonFile<GuardedRuntimeActivationScopeEnforcementManifestContent>(scopePath), ActivationRollbackBinding = ReadJsonFile<GuardedRuntimeActivationRollbackBindingContent>(rollbackPath) };
    }

    private static RuntimeActivationArtifactReferenceExistence BuildRealReferenceExistence(FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport? integrityReport, FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? writeOutReport, RuntimeActivationArtifactContentSnapshot snapshot)
    {
        var contract = integrityReport?.LiveActivationDryRunContract;
        var binding = snapshot.ActivationRollbackBinding;
        var planned = writeOutReport?.PlannedGuardedActivationContract;
        var rollback = FirstNonEmpty(binding?.RollbackSnapshotReference, contract?.RollbackSnapshotReference, planned?.ReferencedRollbackSnapshotPath);
        var revocation = FirstNonEmpty(binding?.RevocationRecordReference, contract?.RevocationRecordReference, planned?.ReferencedRevocationRecordPath);
        var patch = FirstNonEmpty(binding?.ConfigPatchSourceReference, contract?.ConfigPatchSourceReference, planned?.ReferencedConfigPatchSourcePath);
        return new RuntimeActivationArtifactReferenceExistence { RollbackSnapshotPath = rollback, RevocationRecordPath = revocation, ConfigPatchSourcePath = patch, RollbackSnapshotExists = File.Exists(rollback), RevocationRecordExists = File.Exists(revocation), ConfigPatchExists = File.Exists(patch), RollbackSnapshot = ReadJsonFile<CrossingRollbackSnapshotContent>(rollback), RevocationRecord = ReadJsonFile<CrossingRevocationRecordContent>(revocation), ConfigPatch = ReadJsonFile<CrossingRuntimeConfigPatchContent>(patch) };
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    private static T? ReadJsonFile<T>(string path) where T : class => string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
    private static T? ReadJsonLine<T>(string path) where T : class => string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : JsonSerializer.Deserialize<T>(File.ReadLines(path).FirstOrDefault() ?? string.Empty, JsonOptions);

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"- LiveRuntimeActivationExecutionDryRunPassed: `{report.LiveRuntimeActivationExecutionDryRunPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        builder.AppendLine($"- ProbeExecuted: `{report.ProbeExecuted}`");
        builder.AppendLine($"- RuntimeStateChanged: `{report.RuntimeStateChanged}`");
        builder.AppendLine($"- RuntimeActivation: `{report.RuntimeActivation}` FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}` RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        if (report.BlockedReasons.Count > 0) { builder.AppendLine(); builder.AppendLine("## Blocked Reasons"); foreach (var reason in report.BlockedReasons) builder.AppendLine($"- `{reason}`"); }
        return builder.ToString();
    }
}

public sealed class FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunCase { public string CaseName { get; init; } = string.Empty; public string ExpectedStatus { get; init; } = string.Empty; public string ActualStatus { get; init; } = string.Empty; public string ExpectedBlockedReason { get; init; } = string.Empty; public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>(); public string BoundGrantId { get; init; } = string.Empty; public string BoundCapability { get; init; } = string.Empty; public string BoundScope { get; init; } = string.Empty; public int ContentVerifiedArtifactCount { get; init; } public bool LiveActivationDryRunContractComplete { get; init; } public bool ProbeExecuted { get; init; } public string ProbeMode { get; init; } = string.Empty; public bool RuntimeStateChanged { get; init; } public bool RuntimeActivation { get; init; } public bool FormalRetrievalAllowed { get; init; } public bool RuntimeSwitchAllowed { get; init; } public bool PackageOutputChanged { get; init; } public string Reasoning { get; init; } = string.Empty; public bool StatusMatched { get; init; } public bool BlockedReasonMatched { get; init; } public bool PassedAsExpected { get; init; } }
public sealed class FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport { public string OperationId { get; init; } = string.Empty; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow; public bool LiveRuntimeActivationExecutionDryRunPassed { get; init; } public bool GatePassed { get; init; } public int TotalCases { get; init; } public int PassedCases { get; init; } public int FailedCases { get; init; } public int ReadyCases { get; init; } public int BlockedCases { get; init; } public IReadOnlyList<FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunCase>(); public string BoundGrantId { get; init; } = string.Empty; public string BoundCapability { get; init; } = string.Empty; public string BoundScope { get; init; } = string.Empty; public bool UpstreamRuntimeActivationArtifactIntegrityGatePresent { get; init; } public bool UpstreamRuntimeActivationArtifactIntegrityGatePassed { get; init; } public bool UpstreamGuardedRuntimeActivationArtifactWriteOutGatePresent { get; init; } public bool UpstreamGuardedRuntimeActivationArtifactWriteOutGatePassed { get; init; } public bool RuntimeActivationArtifactIntegrityVerifiedCasePresent { get; init; } public int ContentVerifiedArtifactCount { get; init; } public bool AllRuntimeActivationArtifactsContentVerified { get; init; } public bool LiveActivationDryRunContractComplete { get; init; } public LiveRuntimeActivationExecutionPlan ExecutionPlan { get; init; } = new(); public ScopedRuntimeActivationNoOpProbe Probe { get; init; } = new(); public bool ProbeExecuted { get; init; } public bool RuntimeStateChanged { get; init; } public bool RuntimeActivation { get; init; } public bool FormalRetrievalAllowed { get; init; } public bool RuntimeSwitchAllowed { get; init; } public bool FormalPackageWritten { get; init; } public bool PackageOutputChanged { get; init; } public bool PackingPolicyChanged { get; init; } public bool VectorStoreBindingChanged { get; init; } public bool GlobalDefaultOn { get; init; } public bool PromotionToMainlinePerformed { get; init; } public bool MainlineEvidencePresent { get; init; } public bool MainlineTrustRegistryPresent { get; init; } public bool NoRuntimeMutationInvariant { get; init; } public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>(); public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>(); }
public sealed class FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunOptions { public bool Enabled { get; init; } = true; public bool IsGate { get; init; } }
