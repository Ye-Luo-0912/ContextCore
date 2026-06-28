using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class ScopedLiveActivationSafetyCloseoutStatuses
{
    public const string ScopedLiveActivationSafetyCloseoutReady = nameof(ScopedLiveActivationSafetyCloseoutReady);
    public const string ScopedLiveActivationSafetyCloseoutBlocked = nameof(ScopedLiveActivationSafetyCloseoutBlocked);
}

public static class ScopedLiveActivationSafetyCloseoutBlockedReasons
{
    // V8.25 observation gate
    public const string ObservationGateMissing = nameof(ObservationGateMissing);
    public const string ObservationGateNotPassed = nameof(ObservationGateNotPassed);
    public const string ObservationRuntimeStateChangedOutsideScopeTrue = nameof(ObservationRuntimeStateChangedOutsideScopeTrue);
    // V8.24R execution gate
    public const string ExecutionGateMissing = nameof(ExecutionGateMissing);
    public const string ExecutionGateNotPassed = nameof(ExecutionGateNotPassed);
    public const string ActivationAppliedFalse = nameof(ActivationAppliedFalse);
    public const string RuntimeActivationFalse = nameof(RuntimeActivationFalse);
    public const string FormalRetrievalAllowedFalse = nameof(FormalRetrievalAllowedFalse);
    public const string RuntimeSwitchAllowedFalse = nameof(RuntimeSwitchAllowedFalse);
    public const string ActivationIdEmpty = nameof(ActivationIdEmpty);
    public const string BoundCapabilityMismatch = nameof(BoundCapabilityMismatch);
    public const string BoundScopeMismatch = nameof(BoundScopeMismatch);
    public const string KillSwitchNotArmed = nameof(KillSwitchNotArmed);
    public const string ScopeGuardNotActive = nameof(ScopeGuardNotActive);
    public const string RollbackBindingNotPresent = nameof(RollbackBindingNotPresent);
    public const string ExecGlobalDefaultOnTrue = nameof(ExecGlobalDefaultOnTrue);
    public const string ExecPackageOutputChangedTrue = nameof(ExecPackageOutputChangedTrue);
    public const string ExecFormalPackageWrittenTrue = nameof(ExecFormalPackageWrittenTrue);
    public const string ExecVectorStoreBindingChangedTrue = nameof(ExecVectorStoreBindingChangedTrue);
    // cross-gate
    public const string SourceActivationIdMismatchAcrossGates = nameof(SourceActivationIdMismatchAcrossGates);
    // evidence binding
    public const string AppliedEvidenceMissing = nameof(AppliedEvidenceMissing);
    public const string AuditEvidenceMissing = nameof(AuditEvidenceMissing);
    public const string StateEvidenceMissing = nameof(StateEvidenceMissing);
    public const string AppliedEvidenceActivationIdMismatch = nameof(AppliedEvidenceActivationIdMismatch);
    public const string AuditEvidenceActivationIdMismatch = nameof(AuditEvidenceActivationIdMismatch);
    public const string StateEvidenceActivationIdMismatch = nameof(StateEvidenceActivationIdMismatch);
    public const string AppliedEvidenceScopeMismatch = nameof(AppliedEvidenceScopeMismatch);
    public const string AuditEvidenceScopeMismatch = nameof(AuditEvidenceScopeMismatch);
    public const string StateEvidenceScopeMismatch = nameof(StateEvidenceScopeMismatch);
    public const string StateNotActive = nameof(StateNotActive);
    public const string EvidenceGlobalDefaultOnTrue = nameof(EvidenceGlobalDefaultOnTrue);
    public const string EvidencePackageOutputChangedTrue = nameof(EvidencePackageOutputChangedTrue);
    public const string EvidenceFormalPackageWrittenTrue = nameof(EvidenceFormalPackageWrittenTrue);
    public const string EvidenceVectorStoreBindingChangedTrue = nameof(EvidenceVectorStoreBindingChangedTrue);
    // rollback / revocation readiness
    public const string RollbackBindingArtifactMissing = nameof(RollbackBindingArtifactMissing);
    public const string RestoreTestRequiredFalse = nameof(RestoreTestRequiredFalse);
    public const string RollbackSnapshotArtifactMissing = nameof(RollbackSnapshotArtifactMissing);
    public const string RollbackSnapshotCapabilityMismatch = nameof(RollbackSnapshotCapabilityMismatch);
    public const string RollbackSnapshotScopeMismatch = nameof(RollbackSnapshotScopeMismatch);
    public const string RevocationRecordArtifactMissing = nameof(RevocationRecordArtifactMissing);
    public const string RevocationAlreadyRevoked = nameof(RevocationAlreadyRevoked);
    public const string RevocationPathMissing = nameof(RevocationPathMissing);
    public const string RevocationNotRevocable = nameof(RevocationNotRevocable);
    // environment
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
    // attempted mutations (synthetic)
    public const string AttemptedActualRollback = nameof(AttemptedActualRollback);
    public const string AttemptedActualRevocation = nameof(AttemptedActualRevocation);
    public const string AttemptedGlobalRollout = nameof(AttemptedGlobalRollout);
    public const string AttemptedPackageOutputChange = nameof(AttemptedPackageOutputChange);
}

public sealed class ScopedLiveActivationSafetyCloseoutDryRunContract
{
    public bool RollbackDryRunReady { get; init; }
    public bool KillSwitchDryRunReady { get; init; }
    public bool RevocationDryRunReady { get; init; }
    public string DryRunMode { get; init; } = "ScopedSafetyCloseoutNoOp";
    public bool StateMutationApplied { get; init; }
    public bool ActivationActuallyRevoked { get; init; }
    public bool RuntimeActivationCurrent { get; init; }
    public bool RuntimeActivationWouldBecome { get; init; }
    public bool FormalRetrievalAllowedWouldBecome { get; init; }
    public bool RuntimeSwitchAllowedWouldBecome { get; init; }
    public string Scope { get; init; } = string.Empty;
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
}

public sealed record ScopedLiveActivationSafetyCloseoutContext
{
    public FormalRetrievalPromotionApprovalScopedLiveActivationObservationReport? ObservationReport { get; init; }
    public FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport? ExecutionReport { get; init; }
    public EvidenceBindingSnapshot? EvidenceBinding { get; init; }
    public GuardedRuntimeActivationRollbackBindingContent? RollbackBinding { get; init; }
    public bool RollbackBindingFileExists { get; init; }
    public string RollbackBindingPath { get; init; } = string.Empty;
    public CrossingRollbackSnapshotContent? RollbackSnapshot { get; init; }
    public bool RollbackSnapshotFileExists { get; init; }
    public string RollbackSnapshotPath { get; init; } = string.Empty;
    public CrossingRevocationRecordContent? RevocationRecord { get; init; }
    public bool RevocationRecordFileExists { get; init; }
    public string RevocationRecordPath { get; init; } = string.Empty;
}

public sealed class ScopedLiveActivationSafetyCloseoutDecision
{
    public string Status { get; init; } = ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked;
    public string SourceActivationId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public ScopedLiveActivationSafetyCloseoutDryRunContract DryRunContract { get; init; } = new();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool ActivationStillActive { get; init; }
    public bool RuntimeActivationCurrent { get; init; }
    public bool FormalRetrievalAllowedCurrent { get; init; }
    public bool RuntimeSwitchAllowedCurrent { get; init; }
    public bool RollbackDryRunReady { get; init; }
    public bool KillSwitchDryRunReady { get; init; }
    public bool RevocationDryRunReady { get; init; }
    public bool StateMutationApplied { get; init; }
    public bool ActivationActuallyRevoked { get; init; }
    public bool RuntimeStateChangedOutsideScope { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
}

public static class FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutPolicy
{
    private const string AllowedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string AllowedScope = "demo-workspace/demo-collection";
    private const string ExpectedActivationMode = "GuardedScopedRuntime";

    public static ScopedLiveActivationSafetyCloseoutDecision Evaluate(
        ScopedLiveActivationSafetyCloseoutContext ctx,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        // V8.25 observation gate
        if (ctx.ObservationReport is null) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ObservationGateMissing);
        else
        {
            if (!ctx.ObservationReport.ScopedLiveActivationObservationPassed || !ctx.ObservationReport.GatePassed)
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ObservationGateNotPassed);
            if (ctx.ObservationReport.RuntimeStateChangedOutsideScope)
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ObservationRuntimeStateChangedOutsideScopeTrue);
        }

        // V8.24R execution gate
        if (ctx.ExecutionReport is null) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecutionGateMissing);
        else
        {
            if (!ctx.ExecutionReport.GuardedLiveRuntimeActivationExecutionPassed || !ctx.ExecutionReport.GatePassed)
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecutionGateNotPassed);
            if (!ctx.ExecutionReport.ActivationApplied) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ActivationAppliedFalse);
            if (!ctx.ExecutionReport.RuntimeActivation) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RuntimeActivationFalse);
            if (!ctx.ExecutionReport.FormalRetrievalAllowed) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.FormalRetrievalAllowedFalse);
            if (!ctx.ExecutionReport.RuntimeSwitchAllowed) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RuntimeSwitchAllowedFalse);
            if (string.IsNullOrWhiteSpace(ctx.ExecutionReport.ActivationId)) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ActivationIdEmpty);
            if (!string.Equals(ctx.ExecutionReport.BoundCapability, AllowedCapability, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.BoundCapabilityMismatch);
            if (!string.Equals(ctx.ExecutionReport.BoundScope, AllowedScope, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.BoundScopeMismatch);
            if (!ctx.ExecutionReport.KillSwitchArmed) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.KillSwitchNotArmed);
            if (!ctx.ExecutionReport.ScopeGuardActive) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ScopeGuardNotActive);
            if (!ctx.ExecutionReport.RollbackBindingPresent) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RollbackBindingNotPresent);
            if (ctx.ExecutionReport.GlobalDefaultOn) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecGlobalDefaultOnTrue);
            if (ctx.ExecutionReport.PackageOutputChanged) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecPackageOutputChangedTrue);
            if (ctx.ExecutionReport.FormalPackageWritten) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecFormalPackageWrittenTrue);
            if (ctx.ExecutionReport.VectorStoreBindingChanged) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecVectorStoreBindingChangedTrue);
        }

        // Cross-gate ActivationId consistency
        if (ctx.ObservationReport is not null && ctx.ExecutionReport is not null
            && !string.IsNullOrWhiteSpace(ctx.ExecutionReport.ActivationId)
            && !string.Equals(ctx.ObservationReport.SourceActivationId, ctx.ExecutionReport.ActivationId, StringComparison.Ordinal))
        {
            blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.SourceActivationIdMismatchAcrossGates);
        }

        // Evidence binding revalidation
        if (ctx.EvidenceBinding is not null && ctx.EvidenceBinding.ExpectValidation)
            blocked.AddRange(ValidateEvidenceBinding(ctx.EvidenceBinding));

        // Rollback binding artifact (V8.21 on-disk)
        if (!ctx.RollbackBindingFileExists || ctx.RollbackBinding is null)
            blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RollbackBindingArtifactMissing);
        else if (!ctx.RollbackBinding.RestoreTestRequired)
            blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RestoreTestRequiredFalse);

        // Rollback snapshot artifact
        if (!ctx.RollbackSnapshotFileExists || ctx.RollbackSnapshot is null)
            blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RollbackSnapshotArtifactMissing);
        else
        {
            if (!string.Equals(ctx.RollbackSnapshot.BoundCapability, AllowedCapability, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RollbackSnapshotCapabilityMismatch);
            if (!string.Equals(ctx.RollbackSnapshot.BoundScope, AllowedScope, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RollbackSnapshotScopeMismatch);
        }

        // Revocation record artifact — must exist, be RevocableNotYetRevoked, have path
        if (!ctx.RevocationRecordFileExists || ctx.RevocationRecord is null)
            blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RevocationRecordArtifactMissing);
        else
        {
            if (!ctx.RevocationRecord.Revocable) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RevocationNotRevocable);
            if (!ctx.RevocationRecord.RevocationPathPresent) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RevocationPathMissing);
            if (!string.Equals(ctx.RevocationRecord.RevocationStatus, "RevocableNotYetRevoked", StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RevocationAlreadyRevoked);
        }

        // Environment
        if (!rtPassed) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.MainlineTrustRegistryPresent);

        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        var sourceActivationId = ctx.ExecutionReport?.ActivationId ?? string.Empty;
        var boundGrantId = ctx.ExecutionReport?.BoundGrantId ?? string.Empty;
        var boundCapability = ctx.ExecutionReport?.BoundCapability ?? string.Empty;
        var boundScope = ctx.ExecutionReport?.BoundScope ?? string.Empty;

        // Build dry-run contract — all "would-become" values represent the hypothetical state AFTER rollback/revocation.
        // The contract itself never executes any mutation; StateMutationApplied / ActivationActuallyRevoked stay false.
        var rollbackReady = ready;
        var killSwitchReady = ready && (ctx.ExecutionReport?.KillSwitchArmed ?? false);
        var revocationReady = ready && (ctx.RevocationRecord?.Revocable ?? false)
            && (ctx.RevocationRecord?.RevocationPathPresent ?? false)
            && string.Equals(ctx.RevocationRecord?.RevocationStatus, "RevocableNotYetRevoked", StringComparison.Ordinal);
        var dryRun = new ScopedLiveActivationSafetyCloseoutDryRunContract
        {
            RollbackDryRunReady = rollbackReady,
            KillSwitchDryRunReady = killSwitchReady,
            RevocationDryRunReady = revocationReady,
            DryRunMode = "ScopedSafetyCloseoutNoOp",
            StateMutationApplied = false,
            ActivationActuallyRevoked = false,
            RuntimeActivationCurrent = ready && (ctx.ExecutionReport?.RuntimeActivation ?? false),
            RuntimeActivationWouldBecome = false,
            FormalRetrievalAllowedWouldBecome = false,
            RuntimeSwitchAllowedWouldBecome = false,
            Scope = AllowedScope,
            GlobalDefaultOn = false,
            PackageOutputChanged = false
        };

        return new ScopedLiveActivationSafetyCloseoutDecision
        {
            Status = ready ? ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutReady : ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked,
            SourceActivationId = sourceActivationId,
            BoundGrantId = boundGrantId,
            BoundCapability = boundCapability,
            BoundScope = boundScope,
            DryRunContract = dryRun,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "scoped activation safety closeout ready — rollback/kill-switch/revocation dry-runs all viable; activation remains scoped and revocable; evidence chain intact."
                : $"{finalBlocked.Length} blocked reason(s); scoped activation safety closeout blocked.",
            ActivationStillActive = ready,
            RuntimeActivationCurrent = ready,
            FormalRetrievalAllowedCurrent = ready,
            RuntimeSwitchAllowedCurrent = ready,
            RollbackDryRunReady = rollbackReady,
            KillSwitchDryRunReady = killSwitchReady,
            RevocationDryRunReady = revocationReady,
            StateMutationApplied = false,
            ActivationActuallyRevoked = false,
            RuntimeStateChangedOutsideScope = false,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            Recommendation = ready ? "V8ScopedActivationClosed" : "Blocked",
            NextAllowedPhase = ready ? "V9LearningLayer" : string.Empty
        };
    }

    /// <summary>V8.26: evidence-binding revalidation specific to closeout, including state.State == "Active".</summary>
    private static IReadOnlyList<string> ValidateEvidenceBinding(EvidenceBindingSnapshot snapshot)
    {
        var blocked = new List<string>();
        if (snapshot.Applied is null) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.AppliedEvidenceMissing);
        else
        {
            if (!string.Equals(snapshot.Applied.ActivationId, snapshot.ExpectedActivationId, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.AppliedEvidenceActivationIdMismatch);
            if (!string.Equals(snapshot.Applied.Scope, AllowedScope, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.AppliedEvidenceScopeMismatch);
            if (snapshot.Applied.GlobalDefaultOn) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.EvidenceGlobalDefaultOnTrue);
            if (snapshot.Applied.PackageOutputChanged) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.EvidencePackageOutputChangedTrue);
            if (snapshot.Applied.FormalPackageWritten) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.EvidenceFormalPackageWrittenTrue);
            if (snapshot.Applied.VectorStoreBindingChanged) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.EvidenceVectorStoreBindingChangedTrue);
        }
        if (snapshot.Audit is null) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.AuditEvidenceMissing);
        else
        {
            if (!string.Equals(snapshot.Audit.ActivationId, snapshot.ExpectedActivationId, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.AuditEvidenceActivationIdMismatch);
            if (!string.Equals(snapshot.Audit.Scope, AllowedScope, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.AuditEvidenceScopeMismatch);
        }
        if (snapshot.State is null) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.StateEvidenceMissing);
        else
        {
            if (!string.Equals(snapshot.State.ActivationId, snapshot.ExpectedActivationId, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.StateEvidenceActivationIdMismatch);
            if (!string.Equals(snapshot.State.Scope, AllowedScope, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.StateEvidenceScopeMismatch);
            // Closeout requires the activation to currently be Active (otherwise rollback dry-run can't validate against a live state)
            if (!string.Equals(snapshot.State.State, "Active", StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.StateNotActive);
        }
        return blocked;
    }
}

public sealed record FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutScenario(
    string CaseName,
    ScopedLiveActivationSafetyCloseoutContext Context,
    bool RtPassed,
    bool P15Passed,
    bool MainlineEvidencePresent,
    bool MainlineRegistryPresent,
    string ExpectedStatus,
    string? ExpectedBlockedReason);

public sealed class FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string AllowedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string AllowedScope = "demo-workspace/demo-collection";
    private const string TestActivationId = "frp-guarded-live-runtime-activation-fixture";
    private const string TestGrantId = "frp-grant-fixture";

    public FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutReport Run(
        ScopedLiveActivationSafetyCloseoutContext realContext,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed, scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                SourceActivationId = decision.SourceActivationId,
                BoundGrantId = decision.BoundGrantId,
                BoundCapability = decision.BoundCapability,
                BoundScope = decision.BoundScope,
                ActivationStillActive = decision.ActivationStillActive,
                RuntimeActivationCurrent = decision.RuntimeActivationCurrent,
                RollbackDryRunReady = decision.RollbackDryRunReady,
                KillSwitchDryRunReady = decision.KillSwitchDryRunReady,
                RevocationDryRunReady = decision.RevocationDryRunReady,
                StateMutationApplied = decision.StateMutationApplied,
                ActivationActuallyRevoked = decision.ActivationActuallyRevoked,
                RuntimeStateChangedOutsideScope = decision.RuntimeStateChangedOutsideScope,
                GlobalDefaultOn = decision.GlobalDefaultOn,
                PackageOutputChanged = decision.PackageOutputChanged,
                FormalPackageWritten = decision.FormalPackageWritten,
                VectorStoreBindingChanged = decision.VectorStoreBindingChanged,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                // Safety: dry-run must NEVER apply state mutation, NEVER actually revoke, NEVER leak out of scope.
                PassedAsExpected = statusMatched && blockedReasonMatched
                    && !decision.StateMutationApplied && !decision.ActivationActuallyRevoked
                    && !decision.RuntimeStateChangedOutsideScope
                    && !decision.GlobalDefaultOn && !decision.PackageOutputChanged
                    && !decision.FormalPackageWritten && !decision.VectorStoreBindingChanged
            };
        }).ToArray();

        var blocked = new List<string>();
        if (cases.Length < 35) blocked.Add("InsufficientScopedLiveActivationSafetyCloseoutCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("ScopedLiveActivationSafetyCloseoutMatrixFailed");
        foreach (var status in new[] {
            ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutReady,
            ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }
        if (cases.Any(static c => c.StateMutationApplied)) blocked.Add("StateMutationLeaked");
        if (cases.Any(static c => c.ActivationActuallyRevoked)) blocked.Add("ActivationRevocationLeaked");
        if (cases.Any(static c => c.RuntimeStateChangedOutsideScope)) blocked.Add("RuntimeStateChangedOutsideScopeLeaked");
        if (cases.Any(static c => c.GlobalDefaultOn)) blocked.Add("GlobalDefaultOnLeaked");
        if (cases.Any(static c => c.PackageOutputChanged)) blocked.Add("PackageOutputChangedLeaked");
        if (cases.Any(static c => c.FormalPackageWritten)) blocked.Add("FormalPackageWrittenLeaked");
        if (cases.Any(static c => c.VectorStoreBindingChanged)) blocked.Add("VectorStoreBindingChangedLeaked");

        // Real evaluation against on-disk artifacts
        var realDecision = FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutPolicy.Evaluate(
            realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (realContext.ObservationReport is null) blocked.Add("RealScopedLiveActivationObservationGateArtifactMissing");
        else if (!realContext.ObservationReport.GatePassed) blocked.Add("RealScopedLiveActivationObservationGateNotPassed");
        if (realContext.ExecutionReport is null) blocked.Add("RealGuardedLiveRuntimeActivationExecutionGateArtifactMissing");
        else if (!realContext.ExecutionReport.GatePassed) blocked.Add("RealGuardedLiveRuntimeActivationExecutionGateNotPassed");
        if (!string.Equals(realDecision.Status, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealScopedLiveActivationSafetyCloseout:{x}"));
        if (!rtPassed) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(ScopedLiveActivationSafetyCloseoutBlockedReasons.MainlineTrustRegistryPresent);

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;
        return new FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutReport
        {
            OperationId = $"frp-scoped-live-activation-safety-closeout-{Guid.NewGuid():N}",
            CreatedAt = now,
            ScopedLiveActivationSafetyCloseoutPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, StringComparison.Ordinal)),
            Cases = cases,
            SourceActivationId = realDecision.SourceActivationId,
            BoundGrantId = realDecision.BoundGrantId,
            BoundCapability = realDecision.BoundCapability,
            BoundScope = realDecision.BoundScope,
            DryRunContract = realDecision.DryRunContract,
            ActivationStillActive = passed && realDecision.ActivationStillActive,
            RuntimeActivationCurrent = passed && realDecision.RuntimeActivationCurrent,
            FormalRetrievalAllowedCurrent = passed && realDecision.FormalRetrievalAllowedCurrent,
            RuntimeSwitchAllowedCurrent = passed && realDecision.RuntimeSwitchAllowedCurrent,
            RollbackDryRunReady = passed && realDecision.RollbackDryRunReady,
            KillSwitchDryRunReady = passed && realDecision.KillSwitchDryRunReady,
            RevocationDryRunReady = passed && realDecision.RevocationDryRunReady,
            StateMutationApplied = false,
            ActivationActuallyRevoked = false,
            RuntimeStateChangedOutsideScope = false,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            UpstreamScopedLiveActivationObservationGatePresent = realContext.ObservationReport is not null,
            UpstreamScopedLiveActivationObservationGatePassed = realContext.ObservationReport?.GatePassed ?? false,
            UpstreamGuardedLiveRuntimeActivationExecutionGatePresent = realContext.ExecutionReport is not null,
            UpstreamGuardedLiveRuntimeActivationExecutionGatePassed = realContext.ExecutionReport?.GatePassed ?? false,
            Recommendation = passed ? "V8ScopedActivationClosed" : "Blocked",
            NextAllowedPhase = passed ? "V9LearningLayer" : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"sourceActivationId={realDecision.SourceActivationId}",
                $"rollbackReady={realDecision.RollbackDryRunReady}",
                $"killSwitchReady={realDecision.KillSwitchDryRunReady}",
                $"revocationReady={realDecision.RevocationDryRunReady}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}"
            }
        };
    }

    private static IReadOnlyList<FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutReady, null),
            // V8.25 observation gate
            new("ObservationGateMissing", clean with { ObservationReport = null }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ObservationGateMissing),
            new("ObservationGateNotPassed", clean with { ObservationReport = CloneObservation(clean.ObservationReport!, passed:false, gatePassed:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ObservationGateNotPassed),
            new("ObservationRuntimeStateChangedOutsideScopeTrue", clean with { ObservationReport = CloneObservation(clean.ObservationReport!, runtimeStateChangedOutsideScope:true) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ObservationRuntimeStateChangedOutsideScopeTrue),
            // V8.24R execution gate
            new("ExecutionGateMissing", clean with { ExecutionReport = null }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecutionGateMissing),
            new("ExecutionGateNotPassed", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, gatePassed:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecutionGateNotPassed),
            new("ActivationAppliedFalse", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, activationApplied:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ActivationAppliedFalse),
            new("RuntimeActivationFalse", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, runtimeActivation:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RuntimeActivationFalse),
            new("FormalRetrievalAllowedFalse", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, formalAllowed:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.FormalRetrievalAllowedFalse),
            new("RuntimeSwitchAllowedFalse", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, runtimeSwitchAllowed:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RuntimeSwitchAllowedFalse),
            new("ActivationIdEmpty", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, activationId:string.Empty) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ActivationIdEmpty),
            new("BoundCapabilityMismatch", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, capability:"OtherCap") }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.BoundCapabilityMismatch),
            new("BoundScopeMismatch", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, scope:"other/scope") }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.BoundScopeMismatch),
            new("KillSwitchNotArmed", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, killSwitchArmed:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.KillSwitchNotArmed),
            new("ScopeGuardNotActive", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, scopeGuardActive:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ScopeGuardNotActive),
            new("RollbackBindingNotPresentInExec", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, rollbackBindingPresent:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RollbackBindingNotPresent),
            new("ExecGlobalDefaultOnTrue", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, globalDefaultOn:true) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecGlobalDefaultOnTrue),
            new("ExecPackageOutputChangedTrue", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, packageOutputChanged:true) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecPackageOutputChangedTrue),
            new("ExecFormalPackageWrittenTrue", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, formalPackageWritten:true) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecFormalPackageWrittenTrue),
            new("ExecVectorStoreBindingChangedTrue", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, vectorBindingChanged:true) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecVectorStoreBindingChangedTrue),
            // cross-gate consistency
            new("SourceActivationIdMismatchAcrossGates", clean with { ObservationReport = CloneObservation(clean.ObservationReport!, sourceActivationId:"different-id") }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.SourceActivationIdMismatchAcrossGates),
            // evidence binding
            new("AppliedEvidenceMissing", clean with { EvidenceBinding = BuildEvidenceBinding(skipApplied:true) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.AppliedEvidenceMissing),
            new("AuditEvidenceMissing", clean with { EvidenceBinding = BuildEvidenceBinding(skipAudit:true) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.AuditEvidenceMissing),
            new("StateEvidenceMissing", clean with { EvidenceBinding = BuildEvidenceBinding(skipState:true) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.StateEvidenceMissing),
            new("AppliedActivationIdMismatch", clean with { EvidenceBinding = BuildEvidenceBinding(appliedActivationId:"different-id") }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.AppliedEvidenceActivationIdMismatch),
            new("AuditActivationIdMismatch", clean with { EvidenceBinding = BuildEvidenceBinding(auditActivationId:"different-id") }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.AuditEvidenceActivationIdMismatch),
            new("StateActivationIdMismatch", clean with { EvidenceBinding = BuildEvidenceBinding(stateActivationId:"different-id") }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.StateEvidenceActivationIdMismatch),
            new("StateNotActive", clean with { EvidenceBinding = BuildEvidenceBinding(stateValue:"Inactive") }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.StateNotActive),
            // rollback / revocation readiness
            new("RollbackBindingArtifactMissing", clean with { RollbackBindingFileExists = false, RollbackBinding = null }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RollbackBindingArtifactMissing),
            new("RestoreTestRequiredFalse", clean with { RollbackBinding = CloneRollbackBinding(clean.RollbackBinding!, restoreTestRequired:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RestoreTestRequiredFalse),
            new("RollbackSnapshotArtifactMissing", clean with { RollbackSnapshotFileExists = false, RollbackSnapshot = null }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RollbackSnapshotArtifactMissing),
            new("RollbackSnapshotScopeMismatch", clean with { RollbackSnapshot = CloneRollbackSnapshot(clean.RollbackSnapshot!, scope:"other/scope") }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RollbackSnapshotScopeMismatch),
            new("RevocationRecordArtifactMissing", clean with { RevocationRecordFileExists = false, RevocationRecord = null }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RevocationRecordArtifactMissing),
            new("RevocationAlreadyRevoked", clean with { RevocationRecord = CloneRevocation(clean.RevocationRecord!, status:"Revoked") }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RevocationAlreadyRevoked),
            new("RevocationPathMissing", clean with { RevocationRecord = CloneRevocation(clean.RevocationRecord!, pathPresent:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RevocationPathMissing),
            new("RevocationNotRevocable", clean with { RevocationRecord = CloneRevocation(clean.RevocationRecord!, revocable:false) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RevocationNotRevocable),
            // environment
            new("RuntimeGateNotPassed", clean, false, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", clean, true, true, true, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.MainlineTrustRegistryPresent),
            // attempted-mutation cases (synthetic — closeout must refuse to acknowledge any actual mutation in upstream)
            new("AttemptedActualRollback", clean with { RevocationRecord = CloneRevocation(clean.RevocationRecord!, status:"Revoked") }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RevocationAlreadyRevoked),
            new("AttemptedActualRevocation", clean with { RevocationRecord = CloneRevocation(clean.RevocationRecord!, status:"Revoked") }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.RevocationAlreadyRevoked),
            new("AttemptedGlobalRollout", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, globalDefaultOn:true) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecGlobalDefaultOnTrue),
            new("AttemptedPackageOutputChange", clean with { ExecutionReport = CloneExec(clean.ExecutionReport!, packageOutputChanged:true) }, true, true, false, false, ScopedLiveActivationSafetyCloseoutStatuses.ScopedLiveActivationSafetyCloseoutBlocked, ScopedLiveActivationSafetyCloseoutBlockedReasons.ExecPackageOutputChangedTrue)
        ];
    }

    private static ScopedLiveActivationSafetyCloseoutContext BuildCleanContext()
    {
        var exec = BuildCleanExecutionReport();
        var observation = BuildCleanObservationReport(exec.ActivationId);
        var binding = BuildEvidenceBinding();
        var rollbackBinding = BuildCleanRollbackBinding();
        var rollbackSnapshot = BuildCleanRollbackSnapshot();
        var revocationRecord = BuildCleanRevocationRecord();
        return new ScopedLiveActivationSafetyCloseoutContext
        {
            ObservationReport = observation,
            ExecutionReport = exec,
            EvidenceBinding = binding,
            RollbackBindingFileExists = true,
            RollbackBindingPath = "vector/v8/runtime-activation/activation-rollback-binding-FormalRetrievalActivation-demo-workspace-demo-collection.json",
            RollbackBinding = rollbackBinding,
            RollbackSnapshotFileExists = true,
            RollbackSnapshotPath = "vector/v8/dedicated-crossing/rollback-snapshot-FormalRetrievalActivation-demo-workspace-demo-collection.json",
            RollbackSnapshot = rollbackSnapshot,
            RevocationRecordFileExists = true,
            RevocationRecordPath = "vector/v8/dedicated-crossing/revocation-record-FormalRetrievalActivation-demo-workspace-demo-collection.json",
            RevocationRecord = revocationRecord
        };
    }

    private static FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport BuildCleanExecutionReport()
        => new()
        {
            OperationId = "frp-exec-fixture",
            CreatedAt = DateTimeOffset.Parse("2026-06-28T12:00:00Z"),
            GuardedLiveRuntimeActivationExecutionPassed = true,
            GatePassed = true,
            ActivationId = TestActivationId,
            BoundGrantId = TestGrantId,
            BoundCapability = AllowedCapability,
            BoundScope = AllowedScope,
            ActivationApplied = true,
            RuntimeActivation = true,
            FormalRetrievalAllowed = true,
            RuntimeSwitchAllowed = true,
            KillSwitchArmed = true,
            ScopeGuardActive = true,
            RollbackBindingPresent = true,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false,
            MainlineEvidencePresent = false,
            MainlineTrustRegistryPresent = false,
            PromotionToMainlinePerformed = false
        };

    private static FormalRetrievalPromotionApprovalScopedLiveActivationObservationReport BuildCleanObservationReport(string activationId)
        => new()
        {
            OperationId = "frp-observation-fixture",
            CreatedAt = DateTimeOffset.Parse("2026-06-28T12:00:00Z"),
            ScopedLiveActivationObservationPassed = true,
            GatePassed = true,
            SourceActivationId = activationId,
            BoundGrantId = TestGrantId,
            BoundCapability = AllowedCapability,
            BoundScope = AllowedScope,
            RuntimeActivationObserved = true,
            FormalRetrievalAllowedObserved = true,
            RuntimeSwitchAllowedObserved = true,
            RuntimeStateChangedOutsideScope = false,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false,
            MainlineEvidencePresent = false,
            MainlineTrustRegistryPresent = false
        };

    private static EvidenceBindingSnapshot BuildEvidenceBinding(
        bool skipApplied = false, bool skipAudit = false, bool skipState = false,
        string? appliedActivationId = null, string? auditActivationId = null, string? stateActivationId = null,
        string? stateValue = null)
    {
        var applied = skipApplied ? null : new LiveRuntimeActivationAppliedArtifactContent
        {
            ActivationId = appliedActivationId ?? TestActivationId,
            BoundGrantId = TestGrantId,
            Capability = AllowedCapability,
            Scope = AllowedScope,
            ActivationMode = "GuardedScopedRuntime",
            RuntimeActivation = true,
            FormalRetrievalAllowed = true,
            RuntimeSwitchAllowed = true,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false,
            KillSwitchArmed = true,
            RollbackBindingPresent = true,
            ScopeGuardActive = true,
            ActivationSource = "V8.23LiveRuntimeActivationExecutionDryRun",
            CreatedAt = "2026-06-28T12:00:00Z"
        };
        var audit = skipAudit ? null : new LiveRuntimeActivationAuditEvent
        {
            EventId = "fixture-event",
            EventType = "GuardedLiveRuntimeActivationApplied",
            ActivationId = auditActivationId ?? TestActivationId,
            BoundGrantId = TestGrantId,
            Capability = AllowedCapability,
            Scope = AllowedScope,
            ActivationMode = "GuardedScopedRuntime",
            RuntimeActivation = true,
            FormalRetrievalAllowed = true,
            RuntimeSwitchAllowed = true,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false,
            KillSwitchArmed = true,
            ScopeGuardActive = true,
            RollbackBindingPresent = true,
            ActivationSource = "V8.23LiveRuntimeActivationExecutionDryRun",
            Timestamp = "2026-06-28T12:00:00Z"
        };
        var state = skipState ? null : new LiveRuntimeActivationStateContent
        {
            ActivationId = stateActivationId ?? TestActivationId,
            BoundGrantId = TestGrantId,
            Capability = AllowedCapability,
            Scope = AllowedScope,
            State = stateValue ?? "Active",
            ActivationMode = "GuardedScopedRuntime",
            RuntimeActivation = true,
            FormalRetrievalAllowed = true,
            RuntimeSwitchAllowed = true,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false,
            KillSwitchArmed = true,
            ScopeGuardActive = true,
            RollbackBindingPresent = true,
            ActivationSource = "V8.23LiveRuntimeActivationExecutionDryRun",
            CreatedAt = "2026-06-28T12:00:00Z"
        };
        return new EvidenceBindingSnapshot
        {
            ExpectedActivationId = TestActivationId,
            Applied = applied,
            Audit = audit,
            State = state,
            ExpectValidation = true
        };
    }

    private static GuardedRuntimeActivationRollbackBindingContent BuildCleanRollbackBinding()
        => new()
        {
            BoundGrantId = TestGrantId,
            RollbackSnapshotReference = "vector/v8/dedicated-crossing/rollback-snapshot-FormalRetrievalActivation-demo-workspace-demo-collection.json",
            RevocationRecordReference = "vector/v8/dedicated-crossing/revocation-record-FormalRetrievalActivation-demo-workspace-demo-collection.json",
            ConfigPatchSourceReference = "vector/v8/dedicated-crossing/runtime-config-patch-FormalRetrievalActivation-demo-workspace-demo-collection.json",
            RestoreTestRequired = true,
            RuntimeActivation = false,
            CreatedAt = "2026-06-27T02:37:41Z"
        };

    private static CrossingRollbackSnapshotContent BuildCleanRollbackSnapshot()
        => new()
        {
            SnapshotId = "frp-rollback-snapshot-fixture",
            BoundCapability = AllowedCapability,
            BoundScope = AllowedScope,
            SourceGrantId = TestGrantId,
            RestoreTestRequired = true
        };

    private static CrossingRevocationRecordContent BuildCleanRevocationRecord()
        => new()
        {
            RevocationRecordId = "frp-revocation-record-fixture",
            GrantId = TestGrantId,
            BoundCapability = AllowedCapability,
            BoundScope = AllowedScope,
            Revocable = true,
            RevocationPathPresent = true,
            RevocationStatus = "RevocableNotYetRevoked"
        };

    private static FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport CloneExec(
        FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport source,
        bool? passed = null, bool? gatePassed = null, bool? activationApplied = null,
        bool? runtimeActivation = null, bool? formalAllowed = null, bool? runtimeSwitchAllowed = null,
        string? capability = null, string? scope = null, string? activationId = null,
        bool? killSwitchArmed = null, bool? scopeGuardActive = null, bool? rollbackBindingPresent = null,
        bool? globalDefaultOn = null, bool? packageOutputChanged = null, bool? formalPackageWritten = null,
        bool? vectorBindingChanged = null)
        => new()
        {
            OperationId = source.OperationId,
            CreatedAt = source.CreatedAt,
            GuardedLiveRuntimeActivationExecutionPassed = passed ?? source.GuardedLiveRuntimeActivationExecutionPassed,
            GatePassed = gatePassed ?? source.GatePassed,
            ActivationId = activationId ?? source.ActivationId,
            BoundGrantId = source.BoundGrantId,
            BoundCapability = capability ?? source.BoundCapability,
            BoundScope = scope ?? source.BoundScope,
            ActivationApplied = activationApplied ?? source.ActivationApplied,
            RuntimeActivation = runtimeActivation ?? source.RuntimeActivation,
            FormalRetrievalAllowed = formalAllowed ?? source.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed ?? source.RuntimeSwitchAllowed,
            KillSwitchArmed = killSwitchArmed ?? source.KillSwitchArmed,
            ScopeGuardActive = scopeGuardActive ?? source.ScopeGuardActive,
            RollbackBindingPresent = rollbackBindingPresent ?? source.RollbackBindingPresent,
            GlobalDefaultOn = globalDefaultOn ?? source.GlobalDefaultOn,
            PackageOutputChanged = packageOutputChanged ?? source.PackageOutputChanged,
            FormalPackageWritten = formalPackageWritten ?? source.FormalPackageWritten,
            VectorStoreBindingChanged = vectorBindingChanged ?? source.VectorStoreBindingChanged,
            MainlineEvidencePresent = source.MainlineEvidencePresent,
            MainlineTrustRegistryPresent = source.MainlineTrustRegistryPresent,
            PromotionToMainlinePerformed = source.PromotionToMainlinePerformed
        };

    private static FormalRetrievalPromotionApprovalScopedLiveActivationObservationReport CloneObservation(
        FormalRetrievalPromotionApprovalScopedLiveActivationObservationReport source,
        bool? passed = null, bool? gatePassed = null, bool? runtimeStateChangedOutsideScope = null,
        string? sourceActivationId = null)
        => new()
        {
            OperationId = source.OperationId,
            CreatedAt = source.CreatedAt,
            ScopedLiveActivationObservationPassed = passed ?? source.ScopedLiveActivationObservationPassed,
            GatePassed = gatePassed ?? source.GatePassed,
            SourceActivationId = sourceActivationId ?? source.SourceActivationId,
            BoundGrantId = source.BoundGrantId,
            BoundCapability = source.BoundCapability,
            BoundScope = source.BoundScope,
            RuntimeActivationObserved = source.RuntimeActivationObserved,
            FormalRetrievalAllowedObserved = source.FormalRetrievalAllowedObserved,
            RuntimeSwitchAllowedObserved = source.RuntimeSwitchAllowedObserved,
            RuntimeStateChangedOutsideScope = runtimeStateChangedOutsideScope ?? source.RuntimeStateChangedOutsideScope,
            GlobalDefaultOn = source.GlobalDefaultOn,
            PackageOutputChanged = source.PackageOutputChanged,
            FormalPackageWritten = source.FormalPackageWritten,
            VectorStoreBindingChanged = source.VectorStoreBindingChanged,
            MainlineEvidencePresent = source.MainlineEvidencePresent,
            MainlineTrustRegistryPresent = source.MainlineTrustRegistryPresent
        };

    private static GuardedRuntimeActivationRollbackBindingContent CloneRollbackBinding(
        GuardedRuntimeActivationRollbackBindingContent source, bool? restoreTestRequired = null)
        => new()
        {
            BoundGrantId = source.BoundGrantId,
            RollbackSnapshotReference = source.RollbackSnapshotReference,
            RevocationRecordReference = source.RevocationRecordReference,
            ConfigPatchSourceReference = source.ConfigPatchSourceReference,
            RestoreTestRequired = restoreTestRequired ?? source.RestoreTestRequired,
            RuntimeActivation = source.RuntimeActivation,
            CreatedAt = source.CreatedAt
        };

    private static CrossingRollbackSnapshotContent CloneRollbackSnapshot(
        CrossingRollbackSnapshotContent source, string? scope = null, string? capability = null)
        => new()
        {
            SnapshotId = source.SnapshotId,
            BoundCapability = capability ?? source.BoundCapability,
            BoundScope = scope ?? source.BoundScope,
            SourceGrantId = source.SourceGrantId,
            RestoreTestRequired = source.RestoreTestRequired
        };

    private static CrossingRevocationRecordContent CloneRevocation(
        CrossingRevocationRecordContent source, bool? revocable = null, bool? pathPresent = null, string? status = null)
        => new()
        {
            RevocationRecordId = source.RevocationRecordId,
            GrantId = source.GrantId,
            BoundCapability = source.BoundCapability,
            BoundScope = source.BoundScope,
            Revocable = revocable ?? source.Revocable,
            RevocationPathPresent = pathPresent ?? source.RevocationPathPresent,
            RevocationStatus = status ?? source.RevocationStatus
        };

    /// <summary>V8.26: load the real on-disk closeout context — V8.25 / V8.24R gates + 3 evidence files + rollback-binding + rollback-snapshot + revocation-record.</summary>
    public static ScopedLiveActivationSafetyCloseoutContext LoadRealContext(
        FormalRetrievalPromotionApprovalScopedLiveActivationObservationReport? observationReport,
        FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport? executionReport)
    {
        EvidenceBindingSnapshot? evidence = null;
        if (executionReport is not null)
            evidence = FormalRetrievalPromotionApprovalScopedLiveActivationObservationRunner.LoadRealEvidenceBindingSnapshot(executionReport);

        var capability = executionReport?.BoundCapability ?? AllowedCapability;
        var scope = executionReport?.BoundScope ?? AllowedScope;
        var scopeToken = scope.Replace('/', '-').Replace('\\', '-');
        var rollbackBindingPath = Path.Combine("vector", "v8", "runtime-activation", $"activation-rollback-binding-{capability}-{scopeToken}.json");
        var rollbackSnapshotPath = Path.Combine("vector", "v8", "dedicated-crossing", $"rollback-snapshot-{capability}-{scopeToken}.json");
        var revocationRecordPath = Path.Combine("vector", "v8", "dedicated-crossing", $"revocation-record-{capability}-{scopeToken}.json");

        return new ScopedLiveActivationSafetyCloseoutContext
        {
            ObservationReport = observationReport,
            ExecutionReport = executionReport,
            EvidenceBinding = evidence,
            RollbackBindingPath = rollbackBindingPath,
            RollbackBindingFileExists = File.Exists(rollbackBindingPath),
            RollbackBinding = ReadJsonFile<GuardedRuntimeActivationRollbackBindingContent>(rollbackBindingPath),
            RollbackSnapshotPath = rollbackSnapshotPath,
            RollbackSnapshotFileExists = File.Exists(rollbackSnapshotPath),
            RollbackSnapshot = ReadJsonFile<CrossingRollbackSnapshotContent>(rollbackSnapshotPath),
            RevocationRecordPath = revocationRecordPath,
            RevocationRecordFileExists = File.Exists(revocationRecordPath),
            RevocationRecord = ReadJsonFile<CrossingRevocationRecordContent>(revocationRecordPath)
        };
    }

    private static T? ReadJsonFile<T>(string path) where T : class
        => string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"- ScopedLiveActivationSafetyCloseoutPassed: `{report.ScopedLiveActivationSafetyCloseoutPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        builder.AppendLine($"- ReadyCases: `{report.ReadyCases}` BlockedCases: `{report.BlockedCases}`");
        builder.AppendLine($"- SourceActivationId: `{report.SourceActivationId}`");
        builder.AppendLine($"- BoundGrantId: `{report.BoundGrantId}` BoundCapability: `{report.BoundCapability}` BoundScope: `{report.BoundScope}`");
        builder.AppendLine($"- ActivationStillActive: `{report.ActivationStillActive}` RuntimeActivationCurrent: `{report.RuntimeActivationCurrent}`");
        builder.AppendLine($"- FormalRetrievalAllowedCurrent: `{report.FormalRetrievalAllowedCurrent}` RuntimeSwitchAllowedCurrent: `{report.RuntimeSwitchAllowedCurrent}`");
        builder.AppendLine($"- RollbackDryRunReady: `{report.RollbackDryRunReady}` KillSwitchDryRunReady: `{report.KillSwitchDryRunReady}` RevocationDryRunReady: `{report.RevocationDryRunReady}`");
        builder.AppendLine($"- StateMutationApplied: `{report.StateMutationApplied}` ActivationActuallyRevoked: `{report.ActivationActuallyRevoked}`");
        builder.AppendLine($"- RuntimeStateChangedOutsideScope: `{report.RuntimeStateChangedOutsideScope}` GlobalDefaultOn: `{report.GlobalDefaultOn}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}` FormalPackageWritten: `{report.FormalPackageWritten}` VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- MainlineEvidencePresent: `{report.MainlineEvidencePresent}` MainlineTrustRegistryPresent: `{report.MainlineTrustRegistryPresent}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}` NextAllowedPhase: `{report.NextAllowedPhase}`");
        builder.AppendLine($"- DryRunMode: `{report.DryRunContract.DryRunMode}`");
        if (report.BlockedReasons.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Blocked Reasons");
            foreach (var reason in report.BlockedReasons) builder.AppendLine($"- `{reason}`");
        }
        return builder.ToString();
    }
}

public sealed class FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public string SourceActivationId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public bool ActivationStillActive { get; init; }
    public bool RuntimeActivationCurrent { get; init; }
    public bool RollbackDryRunReady { get; init; }
    public bool KillSwitchDryRunReady { get; init; }
    public bool RevocationDryRunReady { get; init; }
    public bool StateMutationApplied { get; init; }
    public bool ActivationActuallyRevoked { get; init; }
    public bool RuntimeStateChangedOutsideScope { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }
    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ScopedLiveActivationSafetyCloseoutPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutCase> Cases { get; init; }
        = Array.Empty<FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutCase>();
    public string SourceActivationId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public ScopedLiveActivationSafetyCloseoutDryRunContract DryRunContract { get; init; } = new();
    public bool ActivationStillActive { get; init; }
    public bool RuntimeActivationCurrent { get; init; }
    public bool FormalRetrievalAllowedCurrent { get; init; }
    public bool RuntimeSwitchAllowedCurrent { get; init; }
    public bool RollbackDryRunReady { get; init; }
    public bool KillSwitchDryRunReady { get; init; }
    public bool RevocationDryRunReady { get; init; }
    public bool StateMutationApplied { get; init; }
    public bool ActivationActuallyRevoked { get; init; }
    public bool RuntimeStateChangedOutsideScope { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public bool UpstreamScopedLiveActivationObservationGatePresent { get; init; }
    public bool UpstreamScopedLiveActivationObservationGatePassed { get; init; }
    public bool UpstreamGuardedLiveRuntimeActivationExecutionGatePresent { get; init; }
    public bool UpstreamGuardedLiveRuntimeActivationExecutionGatePassed { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
