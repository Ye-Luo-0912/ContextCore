using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class GuardedLiveRuntimeActivationExecutionStatuses
{
    public const string GuardedLiveRuntimeActivationApplied = nameof(GuardedLiveRuntimeActivationApplied);
    public const string GuardedLiveRuntimeActivationBlocked = nameof(GuardedLiveRuntimeActivationBlocked);
}

public static class GuardedLiveRuntimeActivationExecutionBlockedReasons
{
    public const string DryRunGateMissing = nameof(DryRunGateMissing);
    public const string DryRunGateNotPassed = nameof(DryRunGateNotPassed);
    public const string DryRunReadyCaseMissing = nameof(DryRunReadyCaseMissing);
    public const string ProbeNotExecuted = nameof(ProbeNotExecuted);
    public const string ProbeModeNotNoOp = nameof(ProbeModeNotNoOp);
    public const string RuntimeStateChangedTrueInUpstream = nameof(RuntimeStateChangedTrueInUpstream);
    public const string KillSwitchCheckNotPassed = nameof(KillSwitchCheckNotPassed);
    public const string ScopeGuardCheckNotPassed = nameof(ScopeGuardCheckNotPassed);
    public const string RollbackBindingCheckNotPassed = nameof(RollbackBindingCheckNotPassed);
    public const string IntegrityGateMissing = nameof(IntegrityGateMissing);
    public const string IntegrityGateNotPassed = nameof(IntegrityGateNotPassed);
    public const string WriteOutGateMissing = nameof(WriteOutGateMissing);
    public const string WriteOutGateNotPassed = nameof(WriteOutGateNotPassed);
    public const string BoundGrantIdEmpty = nameof(BoundGrantIdEmpty);
    public const string BoundCapabilityMismatch = nameof(BoundCapabilityMismatch);
    public const string BoundScopeMismatch = nameof(BoundScopeMismatch);
    public const string RuntimeSwitchArtifactMissing = nameof(RuntimeSwitchArtifactMissing);
    public const string RuntimeGuardManifestMissing = nameof(RuntimeGuardManifestMissing);
    public const string ScopeEnforcementManifestMissing = nameof(ScopeEnforcementManifestMissing);
    public const string ActivationRollbackBindingMissing = nameof(ActivationRollbackBindingMissing);
    public const string RuntimeSwitchModeNotGuardedArtifactOnly = nameof(RuntimeSwitchModeNotGuardedArtifactOnly);
    public const string RuntimeSwitchApplyToRuntimeTrue = nameof(RuntimeSwitchApplyToRuntimeTrue);
    public const string RuntimeSwitchFormalRetrievalAllowedTrue = nameof(RuntimeSwitchFormalRetrievalAllowedTrue);
    public const string RuntimeSwitchRuntimeSwitchAllowedTrue = nameof(RuntimeSwitchRuntimeSwitchAllowedTrue);
    public const string KillSwitchRequiredFalse = nameof(KillSwitchRequiredFalse);
    public const string ScopeGuardRequiredFalse = nameof(ScopeGuardRequiredFalse);
    public const string RollbackRequiredFalse = nameof(RollbackRequiredFalse);
    public const string GlobalDefaultOnTrue = nameof(GlobalDefaultOnTrue);
    public const string WildcardScopeAllowedTrue = nameof(WildcardScopeAllowedTrue);
    public const string AllowedScopeMismatch = nameof(AllowedScopeMismatch);
    public const string RollbackSnapshotReferenceNotFound = nameof(RollbackSnapshotReferenceNotFound);
    public const string RevocationRecordReferenceNotFound = nameof(RevocationRecordReferenceNotFound);
    public const string ConfigPatchReferenceNotFound = nameof(ConfigPatchReferenceNotFound);
    public const string PackageOutputChangedTrueInUpstream = nameof(PackageOutputChangedTrueInUpstream);
    public const string FormalPackageWrittenTrueInUpstream = nameof(FormalPackageWrittenTrueInUpstream);
    public const string VectorStoreBindingChangedTrueInUpstream = nameof(VectorStoreBindingChangedTrueInUpstream);
    public const string GlobalDefaultOnTrueInUpstream = nameof(GlobalDefaultOnTrueInUpstream);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string AttemptedScopeExpansion = nameof(AttemptedScopeExpansion);
    public const string AttemptedGlobalActivation = nameof(AttemptedGlobalActivation);
    public const string AttemptedPackageOutputChange = nameof(AttemptedPackageOutputChange);
    public const string AttemptedFormalPackageWrite = nameof(AttemptedFormalPackageWrite);
}

public sealed class GuardedLiveRuntimeActivationApplyPlan
{
    public string PlanId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string Capability { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string ActivationMode { get; init; } = "GuardedScopedRuntime";
    public bool RuntimeActivation { get; init; } = true;
    public bool FormalRetrievalAllowed { get; init; } = true;
    public bool RuntimeSwitchAllowed { get; init; } = true;
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChangeAllowed { get; init; }
    public bool FormalPackageWriteAllowed { get; init; }
    public bool VectorStoreBindingChangeAllowed { get; init; }
    public bool MainlinePromotionAllowed { get; init; }
}

public sealed class GuardedLiveRuntimeActivationApplyDecision
{
    public string Status { get; init; } = GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked;
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public bool DryRunReadyCasePresent { get; init; }
    public bool ProbeExecuted { get; init; }
    public string ProbeMode { get; init; } = string.Empty;
    public bool RuntimeStateChangedInUpstream { get; init; }
    public bool KillSwitchCheckPassed { get; init; }
    public bool ScopeGuardCheckPassed { get; init; }
    public bool RollbackBindingCheckPassed { get; init; }
    public GuardedLiveRuntimeActivationApplyPlan ApplyPlan { get; init; } = new();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool ActivationApplied { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool KillSwitchArmed { get; init; }
    public bool ScopeGuardActive { get; init; }
    public bool RollbackBindingPresent { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public bool PromotionToMainlinePerformed { get; init; }
}

public static class FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionPolicy
{
    private const string AllowedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string AllowedScope = "demo-workspace/demo-collection";

    public static GuardedLiveRuntimeActivationApplyDecision Evaluate(
        FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport? dryRunReport,
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
        var grantId = dryRunReport?.BoundGrantId ?? integrityReport?.BoundGrantId ?? writeOutReport?.BoundGrantId ?? string.Empty;
        var capability = dryRunReport?.BoundCapability ?? integrityReport?.BoundCapability ?? writeOutReport?.BoundCapability ?? string.Empty;
        var scope = dryRunReport?.BoundScope ?? integrityReport?.BoundScope ?? writeOutReport?.BoundScope ?? string.Empty;

        var readyCasePresent = dryRunReport?.Cases.Any(static c => string.Equals(c.ActualStatus, LiveRuntimeActivationExecutionDryRunStatuses.Ready, StringComparison.Ordinal)) == true;

        if (dryRunReport is null) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.DryRunGateMissing);
        else
        {
            if (!dryRunReport.LiveRuntimeActivationExecutionDryRunPassed || !dryRunReport.GatePassed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.DryRunGateNotPassed);
            if (!readyCasePresent) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.DryRunReadyCaseMissing);
            if (!dryRunReport.ProbeExecuted) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.ProbeNotExecuted);
            if (!string.Equals(dryRunReport.Probe.ProbeMode, "NoOp", StringComparison.Ordinal)) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.ProbeModeNotNoOp);
            if (dryRunReport.RuntimeStateChanged) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeStateChangedTrueInUpstream);
            if (!dryRunReport.Probe.KillSwitchCheckPassed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.KillSwitchCheckNotPassed);
            if (!dryRunReport.Probe.ScopeGuardCheckPassed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.ScopeGuardCheckNotPassed);
            if (!dryRunReport.Probe.RollbackBindingCheckPassed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RollbackBindingCheckNotPassed);
            if (dryRunReport.PackageOutputChanged) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.PackageOutputChangedTrueInUpstream);
            if (dryRunReport.FormalPackageWritten) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.FormalPackageWrittenTrueInUpstream);
            if (dryRunReport.VectorStoreBindingChanged) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.VectorStoreBindingChangedTrueInUpstream);
            if (dryRunReport.GlobalDefaultOn) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.GlobalDefaultOnTrueInUpstream);
        }

        if (integrityReport is null) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.IntegrityGateMissing);
        else if (!integrityReport.RuntimeActivationArtifactIntegrityPassed || !integrityReport.GatePassed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.IntegrityGateNotPassed);

        if (writeOutReport is null) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.WriteOutGateMissing);
        else if (!writeOutReport.GuardedRuntimeActivationArtifactWriteOutPassed || !writeOutReport.GatePassed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.WriteOutGateNotPassed);

        if (string.IsNullOrWhiteSpace(grantId)) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.BoundGrantIdEmpty);
        if (!string.Equals(capability, AllowedCapability, StringComparison.Ordinal)) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.BoundCapabilityMismatch);
        if (!string.Equals(scope, AllowedScope, StringComparison.Ordinal)) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.BoundScopeMismatch);

        var killSwitchArmed = false;
        var scopeGuardActive = false;
        var rollbackBindingPresent = false;

        if (snapshot.RuntimeSwitch is null || string.IsNullOrWhiteSpace(snapshot.RuntimeSwitchPath))
            blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeSwitchArtifactMissing);
        else
        {
            if (!string.Equals(snapshot.RuntimeSwitch.SwitchMode, "GuardedArtifactOnly", StringComparison.Ordinal))
                blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeSwitchModeNotGuardedArtifactOnly);
            if (snapshot.RuntimeSwitch.ApplyToRuntime) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeSwitchApplyToRuntimeTrue);
            if (snapshot.RuntimeSwitch.FormalRetrievalAllowed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeSwitchFormalRetrievalAllowedTrue);
            if (snapshot.RuntimeSwitch.RuntimeSwitchAllowed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeSwitchRuntimeSwitchAllowedTrue);
        }

        if (snapshot.RuntimeGuardManifest is null || string.IsNullOrWhiteSpace(snapshot.RuntimeGuardManifestPath))
            blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeGuardManifestMissing);
        else
        {
            if (!snapshot.RuntimeGuardManifest.KillSwitchRequired) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.KillSwitchRequiredFalse);
            else killSwitchArmed = true;
            if (!snapshot.RuntimeGuardManifest.ScopeGuardRequired) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.ScopeGuardRequiredFalse);
            if (!snapshot.RuntimeGuardManifest.RollbackRequired) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RollbackRequiredFalse);
        }

        if (snapshot.ScopeEnforcementManifest is null || string.IsNullOrWhiteSpace(snapshot.ScopeEnforcementManifestPath))
            blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.ScopeEnforcementManifestMissing);
        else
        {
            if (snapshot.ScopeEnforcementManifest.GlobalDefaultOn) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.GlobalDefaultOnTrue);
            if (snapshot.ScopeEnforcementManifest.WildcardScopeAllowed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.WildcardScopeAllowedTrue);
            if (!string.Equals(snapshot.ScopeEnforcementManifest.AllowedScope, AllowedScope, StringComparison.Ordinal))
                blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.AllowedScopeMismatch);
            else
                scopeGuardActive = !snapshot.ScopeEnforcementManifest.GlobalDefaultOn && !snapshot.ScopeEnforcementManifest.WildcardScopeAllowed;
        }

        if (snapshot.ActivationRollbackBinding is null || string.IsNullOrWhiteSpace(snapshot.ActivationRollbackBindingPath))
            blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.ActivationRollbackBindingMissing);
        else
            rollbackBindingPresent = snapshot.ActivationRollbackBinding.RestoreTestRequired && !snapshot.ActivationRollbackBinding.RuntimeActivation;

        if (!refs.RollbackSnapshotExists) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RollbackSnapshotReferenceNotFound);
        if (!refs.RevocationRecordExists) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RevocationRecordReferenceNotFound);
        if (!refs.ConfigPatchExists) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.ConfigPatchReferenceNotFound);

        if (!rtPassed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.MainlineTrustRegistryPresent);

        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var apply = finalBlocked.Length == 0;
        return new GuardedLiveRuntimeActivationApplyDecision
        {
            Status = apply ? GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationApplied : GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked,
            BoundGrantId = grantId,
            BoundCapability = capability,
            BoundScope = scope,
            DryRunReadyCasePresent = readyCasePresent,
            ProbeExecuted = dryRunReport?.ProbeExecuted ?? false,
            ProbeMode = dryRunReport?.Probe.ProbeMode ?? string.Empty,
            RuntimeStateChangedInUpstream = dryRunReport?.RuntimeStateChanged ?? false,
            KillSwitchCheckPassed = dryRunReport?.Probe.KillSwitchCheckPassed ?? false,
            ScopeGuardCheckPassed = dryRunReport?.Probe.ScopeGuardCheckPassed ?? false,
            RollbackBindingCheckPassed = dryRunReport?.Probe.RollbackBindingCheckPassed ?? false,
            ApplyPlan = new GuardedLiveRuntimeActivationApplyPlan
            {
                PlanId = $"frp-guarded-live-runtime-activation-apply-plan-{Guid.NewGuid():N}",
                BoundGrantId = grantId,
                Capability = AllowedCapability,
                Scope = AllowedScope,
                ActivationMode = "GuardedScopedRuntime",
                RuntimeActivation = true,
                FormalRetrievalAllowed = true,
                RuntimeSwitchAllowed = true,
                GlobalDefaultOn = false,
                PackageOutputChangeAllowed = false,
                FormalPackageWriteAllowed = false,
                VectorStoreBindingChangeAllowed = false,
                MainlinePromotionAllowed = false
            },
            BlockedReasons = finalBlocked,
            Reasoning = apply
                ? "guarded live runtime activation applied within scoped guarded mode; runtime activation true only inside scoped evidence artifact."
                : $"{finalBlocked.Length} blocked reason(s); guarded live runtime activation not applied.",
            ActivationApplied = apply,
            RuntimeActivation = apply,
            FormalRetrievalAllowed = apply,
            RuntimeSwitchAllowed = apply,
            KillSwitchArmed = killSwitchArmed,
            ScopeGuardActive = scopeGuardActive,
            RollbackBindingPresent = rollbackBindingPresent,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            PromotionToMainlinePerformed = false
        };
    }
}

public sealed record FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionScenario(
    string CaseName,
    FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport? DryRunReport,
    FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport? IntegrityReport,
    FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? WriteOutReport,
    RuntimeActivationArtifactContentSnapshot Snapshot,
    RuntimeActivationArtifactReferenceExistence ReferenceExistence,
    bool RtPassed,
    bool P15Passed,
    bool MainlineEvidencePresent,
    bool MainlineRegistryPresent,
    string ExpectedStatus,
    string? ExpectedBlockedReason);

public sealed class FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport Run(
        FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport? dryRunReport,
        FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport? integrityReport,
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? writeOutReport,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionOptions? opt = null,
        string? outputRoot = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionPolicy.Evaluate(
                scenario.DryRunReport,
                scenario.IntegrityReport,
                scenario.WriteOutReport,
                scenario.Snapshot,
                scenario.ReferenceExistence,
                scenario.RtPassed,
                scenario.P15Passed,
                scenario.MainlineEvidencePresent,
                scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            var applied = string.Equals(decision.Status, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationApplied, StringComparison.Ordinal);
            // Safety invariants must hold regardless of apply/block:
            // - GlobalDefaultOn / PackageOutputChanged / FormalPackageWritten / VectorStoreBindingChanged / PromotionToMainlinePerformed must remain false in ALL cases.
            // - RuntimeActivation / FormalRetrievalAllowed / RuntimeSwitchAllowed are allowed true ONLY when applied; must be false in blocked cases.
            var runtimeActivationOk = applied ? decision.RuntimeActivation : !decision.RuntimeActivation;
            var formalAllowedOk = applied ? decision.FormalRetrievalAllowed : !decision.FormalRetrievalAllowed;
            var runtimeSwitchAllowedOk = applied ? decision.RuntimeSwitchAllowed : !decision.RuntimeSwitchAllowed;
            return new FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                BoundGrantId = decision.BoundGrantId,
                BoundCapability = decision.BoundCapability,
                BoundScope = decision.BoundScope,
                ActivationApplied = decision.ActivationApplied,
                RuntimeActivation = decision.RuntimeActivation,
                FormalRetrievalAllowed = decision.FormalRetrievalAllowed,
                RuntimeSwitchAllowed = decision.RuntimeSwitchAllowed,
                KillSwitchArmed = decision.KillSwitchArmed,
                ScopeGuardActive = decision.ScopeGuardActive,
                RollbackBindingPresent = decision.RollbackBindingPresent,
                GlobalDefaultOn = decision.GlobalDefaultOn,
                PackageOutputChanged = decision.PackageOutputChanged,
                FormalPackageWritten = decision.FormalPackageWritten,
                VectorStoreBindingChanged = decision.VectorStoreBindingChanged,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                PassedAsExpected = statusMatched && blockedReasonMatched
                    && runtimeActivationOk && formalAllowedOk && runtimeSwitchAllowedOk
                    && !decision.GlobalDefaultOn && !decision.PackageOutputChanged && !decision.FormalPackageWritten
                    && !decision.PackingPolicyChanged && !decision.VectorStoreBindingChanged
                    && !decision.PromotionToMainlinePerformed
            };
        }).ToArray();

        var blocked = new List<string>();
        if (cases.Length < 30) blocked.Add("InsufficientGuardedLiveRuntimeActivationExecutionCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("GuardedLiveRuntimeActivationExecutionMatrixFailed");
        foreach (var status in new[] { GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationApplied, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal))) blocked.Add($"StatusBranchNotCovered:{status}");
        }
        if (cases.Any(static c => c.GlobalDefaultOn)) blocked.Add("GlobalDefaultOnLeaked");
        if (cases.Any(static c => c.PackageOutputChanged)) blocked.Add("PackageOutputChangedLeaked");
        if (cases.Any(static c => c.FormalPackageWritten)) blocked.Add("FormalPackageWrittenLeaked");
        if (cases.Any(static c => c.VectorStoreBindingChanged)) blocked.Add("VectorStoreBindingChangedLeaked");
        // RuntimeActivation must remain false in every BLOCKED case
        if (cases.Where(static c => string.Equals(c.ActualStatus, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, StringComparison.Ordinal)).Any(static c => c.RuntimeActivation))
            blocked.Add("RuntimeActivationLeakedInBlockedCase");

        // Now run the real artifact evaluation
        var realSnapshot = LoadRealArtifactSnapshot(integrityReport, writeOutReport);
        var realRefs = BuildRealReferenceExistence(integrityReport, writeOutReport, realSnapshot);
        var realDecision = FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionPolicy.Evaluate(
            dryRunReport, integrityReport, writeOutReport, realSnapshot, realRefs, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);

        if (dryRunReport is null) blocked.Add("RealLiveRuntimeActivationExecutionDryRunGateArtifactMissing");
        else if (!dryRunReport.GatePassed) blocked.Add("RealLiveRuntimeActivationExecutionDryRunGateNotPassed");
        if (integrityReport is null) blocked.Add("RealRuntimeActivationArtifactIntegrityGateArtifactMissing");
        else if (!integrityReport.GatePassed) blocked.Add("RealRuntimeActivationArtifactIntegrityGateNotPassed");
        if (writeOutReport is null) blocked.Add("RealGuardedRuntimeActivationArtifactWriteOutGateArtifactMissing");
        else if (!writeOutReport.GatePassed) blocked.Add("RealGuardedRuntimeActivationArtifactWriteOutGateNotPassed");
        if (!string.Equals(realDecision.Status, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationApplied, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealGuardedLiveRuntimeActivation:{x}"));
        if (!rtPassed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(GuardedLiveRuntimeActivationExecutionBlockedReasons.MainlineTrustRegistryPresent);

        // Write the 3 evidence artifacts only if the real decision is to apply, and the runner is allowed to write
        var activationId = $"frp-guarded-live-runtime-activation-{Guid.NewGuid():N}";
        var writeResult = new GuardedLiveRuntimeActivationWriter.WriteResult();
        var appliedArtifactPath = string.Empty;
        var auditArtifactPath = string.Empty;
        var stateArtifactPath = string.Empty;
        if (blocked.Count == 0 && string.Equals(realDecision.Status, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationApplied, StringComparison.Ordinal) && opt.WriteEvidence)
        {
            var root = outputRoot ?? Path.Combine("vector", "v8", "runtime-activation");
            writeResult = GuardedLiveRuntimeActivationWriter.WriteAll(
                root,
                activationId,
                realDecision.BoundGrantId,
                realDecision.BoundCapability,
                realDecision.BoundScope,
                dryRunReport?.OperationId ?? string.Empty,
                integrityReport?.OperationId ?? string.Empty,
                writeOutReport?.OperationId ?? string.Empty,
                realDecision.KillSwitchArmed,
                realDecision.ScopeGuardActive,
                realDecision.RollbackBindingPresent,
                now);
            if (!writeResult.AllArtifactsWritten) blocked.AddRange(writeResult.Errors.Select(static e => $"EvidenceWriteFailure:{e}"));
            else
            {
                appliedArtifactPath = writeResult.AppliedPath;
                auditArtifactPath = writeResult.AuditPath;
                stateArtifactPath = writeResult.StatePath;
            }
        }

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;
        return new FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport
        {
            OperationId = $"frp-guarded-live-runtime-activation-execution-{Guid.NewGuid():N}",
            CreatedAt = now,
            GuardedLiveRuntimeActivationExecutionPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            AppliedCases = cases.Count(c => string.Equals(c.ActualStatus, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationApplied, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, StringComparison.Ordinal)),
            Cases = cases,
            BoundGrantId = realDecision.BoundGrantId,
            BoundCapability = realDecision.BoundCapability,
            BoundScope = realDecision.BoundScope,
            ActivationId = activationId,
            ActivationApplied = passed && realDecision.ActivationApplied,
            RuntimeActivation = passed && realDecision.ActivationApplied,
            FormalRetrievalAllowed = passed && realDecision.ActivationApplied,
            RuntimeSwitchAllowed = passed && realDecision.ActivationApplied,
            KillSwitchArmed = realDecision.KillSwitchArmed,
            ScopeGuardActive = realDecision.ScopeGuardActive,
            RollbackBindingPresent = realDecision.RollbackBindingPresent,
            ApplyPlan = realDecision.ApplyPlan,
            UpstreamLiveRuntimeActivationExecutionDryRunGatePresent = dryRunReport is not null,
            UpstreamLiveRuntimeActivationExecutionDryRunGatePassed = dryRunReport?.GatePassed ?? false,
            UpstreamRuntimeActivationArtifactIntegrityGatePresent = integrityReport is not null,
            UpstreamRuntimeActivationArtifactIntegrityGatePassed = integrityReport?.GatePassed ?? false,
            UpstreamGuardedRuntimeActivationArtifactWriteOutGatePresent = writeOutReport is not null,
            UpstreamGuardedRuntimeActivationArtifactWriteOutGatePassed = writeOutReport?.GatePassed ?? false,
            ProbeExecuted = realDecision.ProbeExecuted,
            ProbeMode = realDecision.ProbeMode,
            DryRunReadyCasePresent = realDecision.DryRunReadyCasePresent,
            AppliedArtifactPath = appliedArtifactPath,
            AuditArtifactPath = auditArtifactPath,
            StateArtifactPath = stateArtifactPath,
            EvidenceArtifactPaths = writeResult.WrittenPaths,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            PromotionToMainlinePerformed = false,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"activationApplied={passed && realDecision.ActivationApplied}",
                $"killSwitchArmed={realDecision.KillSwitchArmed}",
                $"scopeGuardActive={realDecision.ScopeGuardActive}",
                $"rollbackBindingPresent={realDecision.RollbackBindingPresent}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                $"evidenceArtifactsWritten={writeResult.WrittenPaths.Count}"
            }
        };
    }

    private static IReadOnlyList<FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionScenario> BuildScenarios()
    {
        var cleanWriteOut = FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner.BuildCleanArtifactWriteOutReport();
        var cleanGuarded = FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner.BuildCleanGuardedRuntimeActivationDryRunReport();
        var cleanIntegrity = BuildCleanIntegrityFixture(cleanWriteOut, cleanGuarded);
        var cleanSnapshot = FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner.BuildCleanArtifactSnapshot(cleanIntegrity.BoundGrantId, cleanGuarded.OperationId);
        var cleanRefs = FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner.BuildCleanReferenceExistence(cleanIntegrity.BoundGrantId, cleanIntegrity.BoundCapability, cleanIntegrity.BoundScope);
        var cleanDryRun = BuildCleanDryRunFixture(cleanIntegrity);

        return [
            new("AllUpstreamClean", cleanDryRun, cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationApplied, null),
            new("DryRunGateMissing", null, cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.DryRunGateMissing),
            new("DryRunGateNotPassed", CloneDryRun(cleanDryRun, passed:false, gatePassed:false), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.DryRunGateNotPassed),
            new("DryRunReadyCaseMissing", CloneDryRun(cleanDryRun, cases:[new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunCase { CaseName = "BlockedOnly", ExpectedStatus = LiveRuntimeActivationExecutionDryRunStatuses.Blocked, ActualStatus = LiveRuntimeActivationExecutionDryRunStatuses.Blocked }]), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.DryRunReadyCaseMissing),
            new("ProbeNotExecuted", CloneDryRun(cleanDryRun, probeExecuted:false), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.ProbeNotExecuted),
            new("ProbeModeNotNoOp", CloneDryRun(cleanDryRun, probe:CloneProbe(cleanDryRun.Probe, mode:"NotExecuted")), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.ProbeModeNotNoOp),
            new("RuntimeStateChangedTrueInUpstream", CloneDryRun(cleanDryRun, runtimeStateChanged:true), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeStateChangedTrueInUpstream),
            new("KillSwitchCheckNotPassed", CloneDryRun(cleanDryRun, probe:CloneProbe(cleanDryRun.Probe, killSwitch:false)), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.KillSwitchCheckNotPassed),
            new("ScopeGuardCheckNotPassed", CloneDryRun(cleanDryRun, probe:CloneProbe(cleanDryRun.Probe, scopeGuard:false)), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.ScopeGuardCheckNotPassed),
            new("RollbackBindingCheckNotPassed", CloneDryRun(cleanDryRun, probe:CloneProbe(cleanDryRun.Probe, rollback:false)), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.RollbackBindingCheckNotPassed),
            new("IntegrityGateMissing", cleanDryRun, null, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.IntegrityGateMissing),
            new("WriteOutGateMissing", cleanDryRun, cleanIntegrity, null, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.WriteOutGateMissing),
            new("BoundGrantIdEmpty", CloneDryRun(cleanDryRun, grantId:string.Empty), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.BoundGrantIdEmpty),
            new("CapabilityMismatch", CloneDryRun(cleanDryRun, capability:"OtherCap"), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.BoundCapabilityMismatch),
            new("ScopeMismatch", CloneDryRun(cleanDryRun, scope:"other/other"), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.BoundScopeMismatch),
            new("RuntimeSwitchArtifactMissing", cleanDryRun, cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, runtimeSwitch:null), cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeSwitchArtifactMissing),
            new("RuntimeGuardManifestMissing", cleanDryRun, cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, runtimeGuard:null), cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeGuardManifestMissing),
            new("ScopeEnforcementManifestMissing", cleanDryRun, cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, scopeManifest:null), cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.ScopeEnforcementManifestMissing),
            new("ActivationRollbackBindingMissing", cleanDryRun, cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, rollbackBinding:null), cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.ActivationRollbackBindingMissing),
            new("KillSwitchNotArmed", cleanDryRun, cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, runtimeGuard:new GuardedRuntimeActivationRuntimeGuardManifestContent { BoundGrantId = cleanSnapshot.RuntimeGuardManifest!.BoundGrantId, Scope = cleanSnapshot.RuntimeGuardManifest.Scope, KillSwitchRequired = false, ScopeGuardRequired = true, RollbackRequired = true, RuntimeActivationAllowed = false, CreatedAt = cleanSnapshot.RuntimeGuardManifest.CreatedAt }), cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.KillSwitchRequiredFalse),
            new("GlobalDefaultOnTrueInArtifact", cleanDryRun, cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, scopeManifest:new GuardedRuntimeActivationScopeEnforcementManifestContent { BoundGrantId = cleanSnapshot.ScopeEnforcementManifest!.BoundGrantId, AllowedScope = cleanSnapshot.ScopeEnforcementManifest.AllowedScope, GlobalDefaultOn = true, WildcardScopeAllowed = false, CreatedAt = cleanSnapshot.ScopeEnforcementManifest.CreatedAt }), cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.GlobalDefaultOnTrue),
            new("WildcardScopeAllowedTrueInArtifact", cleanDryRun, cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, scopeManifest:new GuardedRuntimeActivationScopeEnforcementManifestContent { BoundGrantId = cleanSnapshot.ScopeEnforcementManifest!.BoundGrantId, AllowedScope = cleanSnapshot.ScopeEnforcementManifest.AllowedScope, GlobalDefaultOn = false, WildcardScopeAllowed = true, CreatedAt = cleanSnapshot.ScopeEnforcementManifest.CreatedAt }), cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.WildcardScopeAllowedTrue),
            new("PackageOutputChangedTrueInUpstream", CloneDryRun(cleanDryRun, packageOutputChanged:true), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.PackageOutputChangedTrueInUpstream),
            new("FormalPackageWrittenTrueInUpstream", CloneDryRun(cleanDryRun, formalPackageWritten:true), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.FormalPackageWrittenTrueInUpstream),
            new("VectorStoreBindingChangedTrueInUpstream", CloneDryRun(cleanDryRun, vectorBindingChanged:true), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.VectorStoreBindingChangedTrueInUpstream),
            new("MainlineEvidencePresent", cleanDryRun, cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, true, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", cleanDryRun, cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, true, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.MainlineTrustRegistryPresent),
            new("RuntimeGateNotPassed", cleanDryRun, cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, false, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", cleanDryRun, cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, false, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.P15GateNotPassed),
            new("RollbackSnapshotReferenceMissing", cleanDryRun, cleanIntegrity, cleanWriteOut, cleanSnapshot, CloneRefs(cleanRefs, rollbackExists:false), true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.RollbackSnapshotReferenceNotFound),
            new("RevocationRecordReferenceMissing", cleanDryRun, cleanIntegrity, cleanWriteOut, cleanSnapshot, CloneRefs(cleanRefs, revocationExists:false), true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.RevocationRecordReferenceNotFound),
            new("ConfigPatchReferenceMissing", cleanDryRun, cleanIntegrity, cleanWriteOut, cleanSnapshot, CloneRefs(cleanRefs, configPatchExists:false), true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.ConfigPatchReferenceNotFound),
            new("AllowedScopeMismatchInArtifact", cleanDryRun, cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, scopeManifest:new GuardedRuntimeActivationScopeEnforcementManifestContent { BoundGrantId = cleanSnapshot.ScopeEnforcementManifest!.BoundGrantId, AllowedScope = "different/scope", GlobalDefaultOn = false, WildcardScopeAllowed = false, CreatedAt = cleanSnapshot.ScopeEnforcementManifest.CreatedAt }), cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.AllowedScopeMismatch),
            new("AttemptedScopeExpansionViaWildcard", cleanDryRun, cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, scopeManifest:new GuardedRuntimeActivationScopeEnforcementManifestContent { BoundGrantId = cleanSnapshot.ScopeEnforcementManifest!.BoundGrantId, AllowedScope = cleanSnapshot.ScopeEnforcementManifest.AllowedScope, GlobalDefaultOn = false, WildcardScopeAllowed = true, CreatedAt = cleanSnapshot.ScopeEnforcementManifest.CreatedAt }), cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.WildcardScopeAllowedTrue),
            new("AttemptedGlobalActivationViaDefaultOn", cleanDryRun, cleanIntegrity, cleanWriteOut, CloneSnapshot(cleanSnapshot, scopeManifest:new GuardedRuntimeActivationScopeEnforcementManifestContent { BoundGrantId = cleanSnapshot.ScopeEnforcementManifest!.BoundGrantId, AllowedScope = cleanSnapshot.ScopeEnforcementManifest.AllowedScope, GlobalDefaultOn = true, WildcardScopeAllowed = false, CreatedAt = cleanSnapshot.ScopeEnforcementManifest.CreatedAt }), cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.GlobalDefaultOnTrue),
            new("AttemptedPackageOutputChange", CloneDryRun(cleanDryRun, packageOutputChanged:true), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.PackageOutputChangedTrueInUpstream),
            new("AttemptedFormalPackageWrite", CloneDryRun(cleanDryRun, formalPackageWritten:true), cleanIntegrity, cleanWriteOut, cleanSnapshot, cleanRefs, true, true, false, false, GuardedLiveRuntimeActivationExecutionStatuses.GuardedLiveRuntimeActivationBlocked, GuardedLiveRuntimeActivationExecutionBlockedReasons.FormalPackageWrittenTrueInUpstream)
        ];
    }

    private static FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport BuildCleanIntegrityFixture(
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport writeOut,
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport guarded)
    {
        var contract = new LiveActivationDryRunContract
        {
            BoundGrantId = writeOut.BoundGrantId,
            BoundCapability = writeOut.BoundCapability,
            BoundScope = writeOut.BoundScope,
            SourceGuardedRuntimeActivationDryRunOperationId = guarded.OperationId,
            RuntimeSwitchArtifactPath = writeOut.WrittenArtifactPaths[0],
            ActivationAuditArtifactPath = writeOut.WrittenArtifactPaths[1],
            RuntimeGuardManifestPath = writeOut.WrittenArtifactPaths[2],
            ScopeEnforcementManifestPath = writeOut.WrittenArtifactPaths[3],
            ActivationRollbackBindingPath = writeOut.WrittenArtifactPaths[4],
            RollbackSnapshotReference = writeOut.PlannedGuardedActivationContract.ReferencedRollbackSnapshotPath,
            RevocationRecordReference = writeOut.PlannedGuardedActivationContract.ReferencedRevocationRecordPath,
            ConfigPatchSourceReference = writeOut.PlannedGuardedActivationContract.ReferencedConfigPatchSourcePath,
            Complete = true
        };
        return new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport
        {
            OperationId = "frp-runtime-activation-artifact-integrity-fixture",
            CreatedAt = DateTimeOffset.Parse("2026-06-27T12:00:00Z"),
            RuntimeActivationArtifactIntegrityPassed = true,
            GatePassed = true,
            TotalCases = 38,
            PassedCases = 38,
            FailedCases = 0,
            VerifiedCases = 1,
            BlockedCases = 37,
            Cases = [new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityCase
            {
                CaseName = "AllUpstreamClean",
                ExpectedStatus = RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityVerified,
                ActualStatus = RuntimeActivationArtifactIntegrityStatuses.RuntimeActivationArtifactIntegrityVerified,
                BoundGrantId = writeOut.BoundGrantId,
                BoundCapability = writeOut.BoundCapability,
                BoundScope = writeOut.BoundScope,
                ContentVerifiedArtifactCount = 5,
                LiveActivationDryRunContractComplete = true,
                StatusMatched = true,
                BlockedReasonMatched = true,
                PassedAsExpected = true
            }],
            BoundGrantId = writeOut.BoundGrantId,
            BoundCapability = writeOut.BoundCapability,
            BoundScope = writeOut.BoundScope,
            WrittenArtifactPaths = writeOut.WrittenArtifactPaths,
            ContentVerifiedArtifactCount = 5,
            AllRuntimeActivationArtifactsContentVerified = true,
            LiveActivationDryRunContract = contract,
            LiveActivationDryRunContractComplete = true,
            NoRuntimeMutationInvariant = true
        };
    }

    private static FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport BuildCleanDryRunFixture(
        FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport integrity)
    {
        var probe = new ScopedRuntimeActivationNoOpProbe
        {
            ProbeExecuted = true,
            ProbeMode = "NoOp",
            Scope = integrity.BoundScope,
            RuntimeStateChanged = false,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            PackageOutputChanged = false,
            KillSwitchCheckPassed = true,
            ScopeGuardCheckPassed = true,
            RollbackBindingCheckPassed = true
        };
        var plan = new LiveRuntimeActivationExecutionPlan
        {
            PlanId = "frp-live-runtime-activation-execution-plan-fixture",
            BoundGrantId = integrity.BoundGrantId,
            Capability = integrity.BoundCapability,
            Scope = integrity.BoundScope,
            ExecutionMode = "ScopedNoOpProbe",
            ApplyRuntimeSwitch = false,
            EnableFormalRetrieval = false,
            KillSwitchCheckRequired = true,
            ScopeGuardCheckRequired = true,
            RollbackBindingCheckRequired = true,
            PackageOutputChangeAllowed = false
        };
        return new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport
        {
            OperationId = "frp-live-runtime-activation-execution-dry-run-fixture",
            CreatedAt = DateTimeOffset.Parse("2026-06-27T12:00:00Z"),
            LiveRuntimeActivationExecutionDryRunPassed = true,
            GatePassed = true,
            TotalCases = 33,
            PassedCases = 33,
            FailedCases = 0,
            ReadyCases = 1,
            BlockedCases = 32,
            Cases = [new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunCase
            {
                CaseName = "AllUpstreamClean",
                ExpectedStatus = LiveRuntimeActivationExecutionDryRunStatuses.Ready,
                ActualStatus = LiveRuntimeActivationExecutionDryRunStatuses.Ready,
                BoundGrantId = integrity.BoundGrantId,
                BoundCapability = integrity.BoundCapability,
                BoundScope = integrity.BoundScope,
                ContentVerifiedArtifactCount = 5,
                LiveActivationDryRunContractComplete = true,
                ProbeExecuted = true,
                ProbeMode = "NoOp",
                RuntimeStateChanged = false,
                StatusMatched = true,
                BlockedReasonMatched = true,
                PassedAsExpected = true
            }],
            BoundGrantId = integrity.BoundGrantId,
            BoundCapability = integrity.BoundCapability,
            BoundScope = integrity.BoundScope,
            UpstreamRuntimeActivationArtifactIntegrityGatePresent = true,
            UpstreamRuntimeActivationArtifactIntegrityGatePassed = true,
            UpstreamGuardedRuntimeActivationArtifactWriteOutGatePresent = true,
            UpstreamGuardedRuntimeActivationArtifactWriteOutGatePassed = true,
            RuntimeActivationArtifactIntegrityVerifiedCasePresent = true,
            ContentVerifiedArtifactCount = 5,
            AllRuntimeActivationArtifactsContentVerified = true,
            LiveActivationDryRunContractComplete = true,
            ExecutionPlan = plan,
            Probe = probe,
            ProbeExecuted = true,
            RuntimeStateChanged = false,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            PromotionToMainlinePerformed = false,
            MainlineEvidencePresent = false,
            MainlineTrustRegistryPresent = false,
            NoRuntimeMutationInvariant = true
        };
    }

    private static FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport CloneDryRun(
        FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport source,
        bool? passed = null, bool? gatePassed = null, IReadOnlyList<FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunCase>? cases = null,
        bool? probeExecuted = null, ScopedRuntimeActivationNoOpProbe? probe = null,
        bool? runtimeStateChanged = null, string? grantId = null, string? capability = null, string? scope = null,
        bool? packageOutputChanged = null, bool? formalPackageWritten = null, bool? vectorBindingChanged = null, bool? globalDefaultOn = null)
        => new()
        {
            OperationId = source.OperationId,
            CreatedAt = source.CreatedAt,
            LiveRuntimeActivationExecutionDryRunPassed = passed ?? source.LiveRuntimeActivationExecutionDryRunPassed,
            GatePassed = gatePassed ?? source.GatePassed,
            TotalCases = source.TotalCases,
            PassedCases = source.PassedCases,
            FailedCases = source.FailedCases,
            ReadyCases = source.ReadyCases,
            BlockedCases = source.BlockedCases,
            Cases = cases ?? source.Cases,
            BoundGrantId = grantId ?? source.BoundGrantId,
            BoundCapability = capability ?? source.BoundCapability,
            BoundScope = scope ?? source.BoundScope,
            UpstreamRuntimeActivationArtifactIntegrityGatePresent = source.UpstreamRuntimeActivationArtifactIntegrityGatePresent,
            UpstreamRuntimeActivationArtifactIntegrityGatePassed = source.UpstreamRuntimeActivationArtifactIntegrityGatePassed,
            UpstreamGuardedRuntimeActivationArtifactWriteOutGatePresent = source.UpstreamGuardedRuntimeActivationArtifactWriteOutGatePresent,
            UpstreamGuardedRuntimeActivationArtifactWriteOutGatePassed = source.UpstreamGuardedRuntimeActivationArtifactWriteOutGatePassed,
            RuntimeActivationArtifactIntegrityVerifiedCasePresent = source.RuntimeActivationArtifactIntegrityVerifiedCasePresent,
            ContentVerifiedArtifactCount = source.ContentVerifiedArtifactCount,
            AllRuntimeActivationArtifactsContentVerified = source.AllRuntimeActivationArtifactsContentVerified,
            LiveActivationDryRunContractComplete = source.LiveActivationDryRunContractComplete,
            ExecutionPlan = source.ExecutionPlan,
            Probe = probe ?? source.Probe,
            ProbeExecuted = probeExecuted ?? source.ProbeExecuted,
            RuntimeStateChanged = runtimeStateChanged ?? source.RuntimeStateChanged,
            RuntimeActivation = source.RuntimeActivation,
            FormalRetrievalAllowed = source.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = source.RuntimeSwitchAllowed,
            FormalPackageWritten = formalPackageWritten ?? source.FormalPackageWritten,
            PackageOutputChanged = packageOutputChanged ?? source.PackageOutputChanged,
            PackingPolicyChanged = source.PackingPolicyChanged,
            VectorStoreBindingChanged = vectorBindingChanged ?? source.VectorStoreBindingChanged,
            GlobalDefaultOn = globalDefaultOn ?? source.GlobalDefaultOn,
            PromotionToMainlinePerformed = source.PromotionToMainlinePerformed,
            MainlineEvidencePresent = source.MainlineEvidencePresent,
            MainlineTrustRegistryPresent = source.MainlineTrustRegistryPresent,
            NoRuntimeMutationInvariant = source.NoRuntimeMutationInvariant,
            BlockedReasons = source.BlockedReasons,
            Diagnostics = source.Diagnostics
        };

    private static ScopedRuntimeActivationNoOpProbe CloneProbe(ScopedRuntimeActivationNoOpProbe source, bool? executed = null, string? mode = null, bool? killSwitch = null, bool? scopeGuard = null, bool? rollback = null)
        => new()
        {
            ProbeExecuted = executed ?? source.ProbeExecuted,
            ProbeMode = mode ?? source.ProbeMode,
            Scope = source.Scope,
            RuntimeStateChanged = source.RuntimeStateChanged,
            RuntimeActivation = source.RuntimeActivation,
            FormalRetrievalAllowed = source.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = source.RuntimeSwitchAllowed,
            PackageOutputChanged = source.PackageOutputChanged,
            KillSwitchCheckPassed = killSwitch ?? source.KillSwitchCheckPassed,
            ScopeGuardCheckPassed = scopeGuard ?? source.ScopeGuardCheckPassed,
            RollbackBindingCheckPassed = rollback ?? source.RollbackBindingCheckPassed
        };

    private static RuntimeActivationArtifactContentSnapshot CloneSnapshot(RuntimeActivationArtifactContentSnapshot source,
        GuardedRuntimeActivationRuntimeSwitchArtifactContent? runtimeSwitch = null,
        GuardedRuntimeActivationAuditArtifactEvent? activationAudit = null,
        GuardedRuntimeActivationRuntimeGuardManifestContent? runtimeGuard = null,
        GuardedRuntimeActivationScopeEnforcementManifestContent? scopeManifest = null,
        GuardedRuntimeActivationRollbackBindingContent? rollbackBinding = null,
        bool clearRuntimeSwitch = false, bool clearAudit = false, bool clearGuard = false, bool clearScope = false, bool clearRollback = false)
        => new()
        {
            RuntimeSwitchPath = source.RuntimeSwitchPath,
            ActivationAuditPath = source.ActivationAuditPath,
            RuntimeGuardManifestPath = source.RuntimeGuardManifestPath,
            ScopeEnforcementManifestPath = source.ScopeEnforcementManifestPath,
            ActivationRollbackBindingPath = source.ActivationRollbackBindingPath,
            RuntimeSwitch = runtimeSwitch,
            ActivationAudit = activationAudit ?? source.ActivationAudit,
            RuntimeGuardManifest = runtimeGuard,
            ScopeEnforcementManifest = scopeManifest,
            ActivationRollbackBinding = rollbackBinding
        };

    private static RuntimeActivationArtifactReferenceExistence CloneRefs(RuntimeActivationArtifactReferenceExistence source, bool? rollbackExists = null, bool? revocationExists = null, bool? configPatchExists = null)
        => new()
        {
            RollbackSnapshotPath = source.RollbackSnapshotPath,
            RevocationRecordPath = source.RevocationRecordPath,
            ConfigPatchSourcePath = source.ConfigPatchSourcePath,
            RollbackSnapshotExists = rollbackExists ?? source.RollbackSnapshotExists,
            RevocationRecordExists = revocationExists ?? source.RevocationRecordExists,
            ConfigPatchExists = configPatchExists ?? source.ConfigPatchExists,
            RollbackSnapshot = source.RollbackSnapshot,
            RevocationRecord = source.RevocationRecord,
            ConfigPatch = source.ConfigPatch
        };

    private static RuntimeActivationArtifactContentSnapshot LoadRealArtifactSnapshot(
        FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport? integrityReport,
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? writeOutReport)
    {
        var contract = integrityReport?.LiveActivationDryRunContract;
        var fallback = writeOutReport?.WrittenArtifactPaths.Count >= 5
            ? new[] { writeOutReport.WrittenArtifactPaths[0], writeOutReport.WrittenArtifactPaths[1], writeOutReport.WrittenArtifactPaths[2], writeOutReport.WrittenArtifactPaths[3], writeOutReport.WrittenArtifactPaths[4] }
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
        return new RuntimeActivationArtifactContentSnapshot
        {
            RuntimeSwitchPath = switchPath,
            ActivationAuditPath = auditPath,
            RuntimeGuardManifestPath = guardPath,
            ScopeEnforcementManifestPath = scopePath,
            ActivationRollbackBindingPath = rollbackPath,
            RuntimeSwitch = ReadJsonFile<GuardedRuntimeActivationRuntimeSwitchArtifactContent>(switchPath),
            ActivationAudit = ReadJsonLine<GuardedRuntimeActivationAuditArtifactEvent>(auditPath),
            RuntimeGuardManifest = ReadJsonFile<GuardedRuntimeActivationRuntimeGuardManifestContent>(guardPath),
            ScopeEnforcementManifest = ReadJsonFile<GuardedRuntimeActivationScopeEnforcementManifestContent>(scopePath),
            ActivationRollbackBinding = ReadJsonFile<GuardedRuntimeActivationRollbackBindingContent>(rollbackPath)
        };
    }

    private static RuntimeActivationArtifactReferenceExistence BuildRealReferenceExistence(
        FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport? integrityReport,
        FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport? writeOutReport,
        RuntimeActivationArtifactContentSnapshot snapshot)
    {
        var contract = integrityReport?.LiveActivationDryRunContract;
        var binding = snapshot.ActivationRollbackBinding;
        var planned = writeOutReport?.PlannedGuardedActivationContract;
        var rollback = FirstNonEmpty(binding?.RollbackSnapshotReference, contract?.RollbackSnapshotReference, planned?.ReferencedRollbackSnapshotPath);
        var revocation = FirstNonEmpty(binding?.RevocationRecordReference, contract?.RevocationRecordReference, planned?.ReferencedRevocationRecordPath);
        var patch = FirstNonEmpty(binding?.ConfigPatchSourceReference, contract?.ConfigPatchSourceReference, planned?.ReferencedConfigPatchSourcePath);
        return new RuntimeActivationArtifactReferenceExistence
        {
            RollbackSnapshotPath = rollback,
            RevocationRecordPath = revocation,
            ConfigPatchSourcePath = patch,
            RollbackSnapshotExists = File.Exists(rollback),
            RevocationRecordExists = File.Exists(revocation),
            ConfigPatchExists = File.Exists(patch),
            RollbackSnapshot = ReadJsonFile<CrossingRollbackSnapshotContent>(rollback),
            RevocationRecord = ReadJsonFile<CrossingRevocationRecordContent>(revocation),
            ConfigPatch = ReadJsonFile<CrossingRuntimeConfigPatchContent>(patch)
        };
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    private static T? ReadJsonFile<T>(string path) where T : class => string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
    private static T? ReadJsonLine<T>(string path) where T : class => string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : JsonSerializer.Deserialize<T>(File.ReadLines(path).FirstOrDefault() ?? string.Empty, JsonOptions);

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"- GuardedLiveRuntimeActivationExecutionPassed: `{report.GuardedLiveRuntimeActivationExecutionPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        builder.AppendLine($"- AppliedCases: `{report.AppliedCases}` BlockedCases: `{report.BlockedCases}`");
        builder.AppendLine($"- ActivationApplied: `{report.ActivationApplied}` ActivationId: `{report.ActivationId}`");
        builder.AppendLine($"- BoundGrantId: `{report.BoundGrantId}` BoundCapability: `{report.BoundCapability}` BoundScope: `{report.BoundScope}`");
        builder.AppendLine($"- RuntimeActivation: `{report.RuntimeActivation}` FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}` RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- KillSwitchArmed: `{report.KillSwitchArmed}` ScopeGuardActive: `{report.ScopeGuardActive}` RollbackBindingPresent: `{report.RollbackBindingPresent}`");
        builder.AppendLine($"- GlobalDefaultOn: `{report.GlobalDefaultOn}` PackageOutputChanged: `{report.PackageOutputChanged}` FormalPackageWritten: `{report.FormalPackageWritten}` VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- MainlineEvidencePresent: `{report.MainlineEvidencePresent}` MainlineTrustRegistryPresent: `{report.MainlineTrustRegistryPresent}`");
        if (!string.IsNullOrWhiteSpace(report.AppliedArtifactPath)) builder.AppendLine($"- AppliedArtifactPath: `{report.AppliedArtifactPath}`");
        if (!string.IsNullOrWhiteSpace(report.AuditArtifactPath)) builder.AppendLine($"- AuditArtifactPath: `{report.AuditArtifactPath}`");
        if (!string.IsNullOrWhiteSpace(report.StateArtifactPath)) builder.AppendLine($"- StateArtifactPath: `{report.StateArtifactPath}`");
        if (report.BlockedReasons.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Blocked Reasons");
            foreach (var reason in report.BlockedReasons) builder.AppendLine($"- `{reason}`");
        }
        return builder.ToString();
    }
}

public sealed class FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public bool ActivationApplied { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool KillSwitchArmed { get; init; }
    public bool ScopeGuardActive { get; init; }
    public bool RollbackBindingPresent { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }
    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool GuardedLiveRuntimeActivationExecutionPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int AppliedCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionCase>();
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public string ActivationId { get; init; } = string.Empty;
    public bool ActivationApplied { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool KillSwitchArmed { get; init; }
    public bool ScopeGuardActive { get; init; }
    public bool RollbackBindingPresent { get; init; }
    public GuardedLiveRuntimeActivationApplyPlan ApplyPlan { get; init; } = new();
    public bool UpstreamLiveRuntimeActivationExecutionDryRunGatePresent { get; init; }
    public bool UpstreamLiveRuntimeActivationExecutionDryRunGatePassed { get; init; }
    public bool UpstreamRuntimeActivationArtifactIntegrityGatePresent { get; init; }
    public bool UpstreamRuntimeActivationArtifactIntegrityGatePassed { get; init; }
    public bool UpstreamGuardedRuntimeActivationArtifactWriteOutGatePresent { get; init; }
    public bool UpstreamGuardedRuntimeActivationArtifactWriteOutGatePassed { get; init; }
    public bool ProbeExecuted { get; init; }
    public string ProbeMode { get; init; } = string.Empty;
    public bool DryRunReadyCasePresent { get; init; }
    public string AppliedArtifactPath { get; init; } = string.Empty;
    public string AuditArtifactPath { get; init; } = string.Empty;
    public string StateArtifactPath { get; init; } = string.Empty;
    public IReadOnlyList<string> EvidenceArtifactPaths { get; init; } = Array.Empty<string>();
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public bool PromotionToMainlinePerformed { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
    public bool WriteEvidence { get; init; } = true;
}
