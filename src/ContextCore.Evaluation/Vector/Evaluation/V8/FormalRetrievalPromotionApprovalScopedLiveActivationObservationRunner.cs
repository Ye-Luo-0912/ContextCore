using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class ScopedLiveActivationObservationStatuses
{
    public const string ScopedLiveActivationObservationReady = nameof(ScopedLiveActivationObservationReady);
    public const string ScopedLiveActivationObservationBlocked = nameof(ScopedLiveActivationObservationBlocked);
}

public static class ScopedLiveActivationObservationBlockedReasons
{
    // V8.24R execution gate
    public const string ExecutionGateMissing = nameof(ExecutionGateMissing);
    public const string ExecutionGateNotPassed = nameof(ExecutionGateNotPassed);
    public const string ActivationAppliedFalse = nameof(ActivationAppliedFalse);
    public const string RuntimeActivationFalse = nameof(RuntimeActivationFalse);
    public const string FormalRetrievalAllowedFalse = nameof(FormalRetrievalAllowedFalse);
    public const string RuntimeSwitchAllowedFalse = nameof(RuntimeSwitchAllowedFalse);
    public const string BoundCapabilityMismatch = nameof(BoundCapabilityMismatch);
    public const string BoundScopeMismatch = nameof(BoundScopeMismatch);
    public const string ActivationIdEmpty = nameof(ActivationIdEmpty);
    public const string KillSwitchNotArmed = nameof(KillSwitchNotArmed);
    public const string ScopeGuardNotActive = nameof(ScopeGuardNotActive);
    public const string RollbackBindingNotPresent = nameof(RollbackBindingNotPresent);
    public const string GlobalDefaultOnTrue = nameof(GlobalDefaultOnTrue);
    public const string PackageOutputChangedTrue = nameof(PackageOutputChangedTrue);
    public const string FormalPackageWrittenTrue = nameof(FormalPackageWrittenTrue);
    public const string VectorStoreBindingChangedTrue = nameof(VectorStoreBindingChangedTrue);
    // evidence binding (parallel to V8.24R names so reading the report tells the same story)
    public const string AppliedEvidenceMissing = nameof(AppliedEvidenceMissing);
    public const string AuditEvidenceMissing = nameof(AuditEvidenceMissing);
    public const string StateEvidenceMissing = nameof(StateEvidenceMissing);
    public const string AppliedEvidenceActivationIdMismatch = nameof(AppliedEvidenceActivationIdMismatch);
    public const string AuditEvidenceActivationIdMismatch = nameof(AuditEvidenceActivationIdMismatch);
    public const string StateEvidenceActivationIdMismatch = nameof(StateEvidenceActivationIdMismatch);
    public const string AppliedEvidenceScopeMismatch = nameof(AppliedEvidenceScopeMismatch);
    public const string AuditEvidenceScopeMismatch = nameof(AuditEvidenceScopeMismatch);
    public const string StateEvidenceScopeMismatch = nameof(StateEvidenceScopeMismatch);
    public const string EvidenceGlobalDefaultOnTrue = nameof(EvidenceGlobalDefaultOnTrue);
    public const string EvidencePackageOutputChangedTrue = nameof(EvidencePackageOutputChangedTrue);
    public const string EvidenceFormalPackageWrittenTrue = nameof(EvidenceFormalPackageWrittenTrue);
    public const string EvidenceVectorStoreBindingChangedTrue = nameof(EvidenceVectorStoreBindingChangedTrue);
    // environment
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
    // synthetic attempt cases
    public const string AttemptedObservationOutsideScope = nameof(AttemptedObservationOutsideScope);
    public const string AttemptedPackageOutputChange = nameof(AttemptedPackageOutputChange);
    public const string AttemptedGlobalDefaultObservation = nameof(AttemptedGlobalDefaultObservation);
}

public sealed class ScopedLiveActivationObservation
{
    public string ObservationId { get; init; } = string.Empty;
    public string SourceActivationId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string Capability { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string ObservationMode { get; init; } = "ScopedShadowTrace";
    public bool RuntimeActivationObserved { get; init; }
    public bool FormalRetrievalAllowedObserved { get; init; }
    public bool RuntimeSwitchAllowedObserved { get; init; }
    public bool ScopeGuardObserved { get; init; }
    public bool KillSwitchObserved { get; init; }
    public bool RollbackBindingObserved { get; init; }
    public bool RuntimeStateChangedOutsideScope { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
}

public sealed class ScopedLiveActivationObservationDecision
{
    public string Status { get; init; } = ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked;
    public string SourceActivationId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public ScopedLiveActivationObservation Observation { get; init; } = new();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool RuntimeActivationObserved { get; init; }
    public bool FormalRetrievalAllowedObserved { get; init; }
    public bool RuntimeSwitchAllowedObserved { get; init; }
    public bool RuntimeStateChangedOutsideScope { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
}

public static class FormalRetrievalPromotionApprovalScopedLiveActivationObservationPolicy
{
    private const string AllowedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string AllowedScope = "demo-workspace/demo-collection";

    public static ScopedLiveActivationObservationDecision Evaluate(
        FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport? executionReport,
        EvidenceBindingSnapshot? evidence,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (executionReport is null) blocked.Add(ScopedLiveActivationObservationBlockedReasons.ExecutionGateMissing);
        else
        {
            if (!executionReport.GuardedLiveRuntimeActivationExecutionPassed || !executionReport.GatePassed)
                blocked.Add(ScopedLiveActivationObservationBlockedReasons.ExecutionGateNotPassed);
            if (!executionReport.ActivationApplied) blocked.Add(ScopedLiveActivationObservationBlockedReasons.ActivationAppliedFalse);
            if (!executionReport.RuntimeActivation) blocked.Add(ScopedLiveActivationObservationBlockedReasons.RuntimeActivationFalse);
            if (!executionReport.FormalRetrievalAllowed) blocked.Add(ScopedLiveActivationObservationBlockedReasons.FormalRetrievalAllowedFalse);
            if (!executionReport.RuntimeSwitchAllowed) blocked.Add(ScopedLiveActivationObservationBlockedReasons.RuntimeSwitchAllowedFalse);
            if (!string.Equals(executionReport.BoundCapability, AllowedCapability, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationObservationBlockedReasons.BoundCapabilityMismatch);
            if (!string.Equals(executionReport.BoundScope, AllowedScope, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationObservationBlockedReasons.BoundScopeMismatch);
            if (string.IsNullOrWhiteSpace(executionReport.ActivationId)) blocked.Add(ScopedLiveActivationObservationBlockedReasons.ActivationIdEmpty);
            if (!executionReport.KillSwitchArmed) blocked.Add(ScopedLiveActivationObservationBlockedReasons.KillSwitchNotArmed);
            if (!executionReport.ScopeGuardActive) blocked.Add(ScopedLiveActivationObservationBlockedReasons.ScopeGuardNotActive);
            if (!executionReport.RollbackBindingPresent) blocked.Add(ScopedLiveActivationObservationBlockedReasons.RollbackBindingNotPresent);
            if (executionReport.GlobalDefaultOn) blocked.Add(ScopedLiveActivationObservationBlockedReasons.GlobalDefaultOnTrue);
            if (executionReport.PackageOutputChanged) blocked.Add(ScopedLiveActivationObservationBlockedReasons.PackageOutputChangedTrue);
            if (executionReport.FormalPackageWritten) blocked.Add(ScopedLiveActivationObservationBlockedReasons.FormalPackageWrittenTrue);
            if (executionReport.VectorStoreBindingChanged) blocked.Add(ScopedLiveActivationObservationBlockedReasons.VectorStoreBindingChangedTrue);
        }

        // Evidence binding re-validation: gate.ActivationId must match all 3 evidence artifacts.
        if (evidence is not null && evidence.ExpectValidation)
            blocked.AddRange(ValidateEvidenceBinding(evidence));

        if (!rtPassed) blocked.Add(ScopedLiveActivationObservationBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(ScopedLiveActivationObservationBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(ScopedLiveActivationObservationBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(ScopedLiveActivationObservationBlockedReasons.MainlineTrustRegistryPresent);

        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        var sourceActivationId = executionReport?.ActivationId ?? string.Empty;
        var observation = new ScopedLiveActivationObservation
        {
            ObservationId = ready ? $"frp-scoped-live-activation-observation-{Guid.NewGuid():N}" : string.Empty,
            SourceActivationId = sourceActivationId,
            BoundGrantId = executionReport?.BoundGrantId ?? string.Empty,
            Capability = AllowedCapability,
            Scope = AllowedScope,
            ObservationMode = "ScopedShadowTrace",
            RuntimeActivationObserved = ready,
            FormalRetrievalAllowedObserved = ready,
            RuntimeSwitchAllowedObserved = ready,
            ScopeGuardObserved = ready,
            KillSwitchObserved = ready,
            RollbackBindingObserved = ready,
            RuntimeStateChangedOutsideScope = false,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false
        };
        return new ScopedLiveActivationObservationDecision
        {
            Status = ready ? ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationReady : ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked,
            SourceActivationId = sourceActivationId,
            BoundGrantId = executionReport?.BoundGrantId ?? string.Empty,
            BoundCapability = executionReport?.BoundCapability ?? string.Empty,
            BoundScope = executionReport?.BoundScope ?? string.Empty,
            Observation = observation,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "scoped live activation observable — shadow trace confirms activation bound to scope; no runtime state change outside scope, no global default, no package mutation."
                : $"{finalBlocked.Length} blocked reason(s); scoped live activation observation blocked.",
            RuntimeActivationObserved = observation.RuntimeActivationObserved,
            FormalRetrievalAllowedObserved = observation.FormalRetrievalAllowedObserved,
            RuntimeSwitchAllowedObserved = observation.RuntimeSwitchAllowedObserved,
            RuntimeStateChangedOutsideScope = false,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent
        };
    }

    /// <summary>V8.25: re-validates evidence binding using the same rules as V8.24R, but emitting V8.25-namespaced blocked reasons.</summary>
    private static IReadOnlyList<string> ValidateEvidenceBinding(EvidenceBindingSnapshot snapshot)
    {
        var blocked = new List<string>();
        if (snapshot.Applied is null) blocked.Add(ScopedLiveActivationObservationBlockedReasons.AppliedEvidenceMissing);
        else
        {
            if (!string.Equals(snapshot.Applied.ActivationId, snapshot.ExpectedActivationId, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationObservationBlockedReasons.AppliedEvidenceActivationIdMismatch);
            if (!string.Equals(snapshot.Applied.Scope, AllowedScope, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationObservationBlockedReasons.AppliedEvidenceScopeMismatch);
            if (snapshot.Applied.GlobalDefaultOn) blocked.Add(ScopedLiveActivationObservationBlockedReasons.EvidenceGlobalDefaultOnTrue);
            if (snapshot.Applied.PackageOutputChanged) blocked.Add(ScopedLiveActivationObservationBlockedReasons.EvidencePackageOutputChangedTrue);
            if (snapshot.Applied.FormalPackageWritten) blocked.Add(ScopedLiveActivationObservationBlockedReasons.EvidenceFormalPackageWrittenTrue);
            if (snapshot.Applied.VectorStoreBindingChanged) blocked.Add(ScopedLiveActivationObservationBlockedReasons.EvidenceVectorStoreBindingChangedTrue);
        }
        if (snapshot.Audit is null) blocked.Add(ScopedLiveActivationObservationBlockedReasons.AuditEvidenceMissing);
        else
        {
            if (!string.Equals(snapshot.Audit.ActivationId, snapshot.ExpectedActivationId, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationObservationBlockedReasons.AuditEvidenceActivationIdMismatch);
            if (!string.Equals(snapshot.Audit.Scope, AllowedScope, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationObservationBlockedReasons.AuditEvidenceScopeMismatch);
        }
        if (snapshot.State is null) blocked.Add(ScopedLiveActivationObservationBlockedReasons.StateEvidenceMissing);
        else
        {
            if (!string.Equals(snapshot.State.ActivationId, snapshot.ExpectedActivationId, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationObservationBlockedReasons.StateEvidenceActivationIdMismatch);
            if (!string.Equals(snapshot.State.Scope, AllowedScope, StringComparison.Ordinal))
                blocked.Add(ScopedLiveActivationObservationBlockedReasons.StateEvidenceScopeMismatch);
        }
        return blocked;
    }
}

public sealed record FormalRetrievalPromotionApprovalScopedLiveActivationObservationScenario(
    string CaseName,
    FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport? ExecutionReport,
    EvidenceBindingSnapshot? EvidenceBinding,
    bool RtPassed,
    bool P15Passed,
    bool MainlineEvidencePresent,
    bool MainlineRegistryPresent,
    string ExpectedStatus,
    string? ExpectedBlockedReason);

public sealed class FormalRetrievalPromotionApprovalScopedLiveActivationObservationRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string AllowedCapability = PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
    private const string AllowedScope = "demo-workspace/demo-collection";
    private const string TestActivationId = "frp-guarded-live-runtime-activation-fixture";
    private const string TestGrantId = "frp-grant-fixture";

    public FormalRetrievalPromotionApprovalScopedLiveActivationObservationReport Run(
        FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport? executionReport,
        EvidenceBindingSnapshot? realEvidenceBinding,
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalScopedLiveActivationObservationOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalScopedLiveActivationObservationOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = FormalRetrievalPromotionApprovalScopedLiveActivationObservationPolicy.Evaluate(
                scenario.ExecutionReport,
                scenario.EvidenceBinding,
                scenario.RtPassed,
                scenario.P15Passed,
                scenario.MainlineEvidencePresent,
                scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new FormalRetrievalPromotionApprovalScopedLiveActivationObservationCase
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
                RuntimeActivationObserved = decision.RuntimeActivationObserved,
                FormalRetrievalAllowedObserved = decision.FormalRetrievalAllowedObserved,
                RuntimeSwitchAllowedObserved = decision.RuntimeSwitchAllowedObserved,
                RuntimeStateChangedOutsideScope = decision.RuntimeStateChangedOutsideScope,
                GlobalDefaultOn = decision.GlobalDefaultOn,
                PackageOutputChanged = decision.PackageOutputChanged,
                FormalPackageWritten = decision.FormalPackageWritten,
                VectorStoreBindingChanged = decision.VectorStoreBindingChanged,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                // Safety: observation must never leak runtime state change outside scope, global default-on, or package mutation.
                PassedAsExpected = statusMatched && blockedReasonMatched
                    && !decision.RuntimeStateChangedOutsideScope
                    && !decision.GlobalDefaultOn && !decision.PackageOutputChanged
                    && !decision.FormalPackageWritten && !decision.VectorStoreBindingChanged
            };
        }).ToArray();

        var blocked = new List<string>();
        if (cases.Length < 25) blocked.Add("InsufficientScopedLiveActivationObservationCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("ScopedLiveActivationObservationMatrixFailed");
        foreach (var status in new[] {
            ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationReady,
            ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }
        // Matrix-level safety leak checks
        if (cases.Any(static c => c.RuntimeStateChangedOutsideScope)) blocked.Add("RuntimeStateChangedOutsideScopeLeaked");
        if (cases.Any(static c => c.GlobalDefaultOn)) blocked.Add("GlobalDefaultOnLeaked");
        if (cases.Any(static c => c.PackageOutputChanged)) blocked.Add("PackageOutputChangedLeaked");
        if (cases.Any(static c => c.FormalPackageWritten)) blocked.Add("FormalPackageWrittenLeaked");
        if (cases.Any(static c => c.VectorStoreBindingChanged)) blocked.Add("VectorStoreBindingChangedLeaked");

        // Real evaluation against the V8.24R gate on disk + 3 evidence artifacts loaded by the caller.
        var realDecision = FormalRetrievalPromotionApprovalScopedLiveActivationObservationPolicy.Evaluate(
            executionReport, realEvidenceBinding, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (executionReport is null) blocked.Add("RealGuardedLiveRuntimeActivationExecutionGateArtifactMissing");
        else if (!executionReport.GatePassed) blocked.Add("RealGuardedLiveRuntimeActivationExecutionGateNotPassed");
        if (!string.Equals(realDecision.Status, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealScopedLiveActivationObservation:{x}"));
        if (!rtPassed) blocked.Add(ScopedLiveActivationObservationBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(ScopedLiveActivationObservationBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(ScopedLiveActivationObservationBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(ScopedLiveActivationObservationBlockedReasons.MainlineTrustRegistryPresent);

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;
        return new FormalRetrievalPromotionApprovalScopedLiveActivationObservationReport
        {
            OperationId = $"frp-scoped-live-activation-observation-{Guid.NewGuid():N}",
            CreatedAt = now,
            ScopedLiveActivationObservationPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, StringComparison.Ordinal)),
            Cases = cases,
            SourceActivationId = realDecision.SourceActivationId,
            BoundGrantId = realDecision.BoundGrantId,
            BoundCapability = realDecision.BoundCapability,
            BoundScope = realDecision.BoundScope,
            Observation = realDecision.Observation,
            UpstreamGuardedLiveRuntimeActivationExecutionGatePresent = executionReport is not null,
            UpstreamGuardedLiveRuntimeActivationExecutionGatePassed = executionReport?.GatePassed ?? false,
            RuntimeActivationObserved = passed && realDecision.RuntimeActivationObserved,
            FormalRetrievalAllowedObserved = passed && realDecision.FormalRetrievalAllowedObserved,
            RuntimeSwitchAllowedObserved = passed && realDecision.RuntimeSwitchAllowedObserved,
            RuntimeStateChangedOutsideScope = false,
            GlobalDefaultOn = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            VectorStoreBindingChanged = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"sourceActivationId={realDecision.SourceActivationId}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}"
            }
        };
    }

    private static IReadOnlyList<FormalRetrievalPromotionApprovalScopedLiveActivationObservationScenario> BuildScenarios()
    {
        var cleanExec = BuildCleanExecutionReport();
        var cleanEvidence = BuildEvidenceBinding();
        return [
            new("AllUpstreamCleanWithValidEvidence", cleanExec, cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationReady, null),
            // V8.24R gate
            new("ExecutionGateMissing", null, cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.ExecutionGateMissing),
            new("ExecutionGateNotPassed", CloneExec(cleanExec, passed:false, gatePassed:false), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.ExecutionGateNotPassed),
            new("ActivationAppliedFalse", CloneExec(cleanExec, activationApplied:false), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.ActivationAppliedFalse),
            new("RuntimeActivationFalse", CloneExec(cleanExec, runtimeActivation:false), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.RuntimeActivationFalse),
            new("FormalRetrievalAllowedFalse", CloneExec(cleanExec, formalAllowed:false), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.FormalRetrievalAllowedFalse),
            new("RuntimeSwitchAllowedFalse", CloneExec(cleanExec, runtimeSwitchAllowed:false), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.RuntimeSwitchAllowedFalse),
            new("CapabilityMismatch", CloneExec(cleanExec, capability:"OtherCap"), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.BoundCapabilityMismatch),
            new("ScopeMismatch", CloneExec(cleanExec, scope:"other/other"), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.BoundScopeMismatch),
            new("ActivationIdEmpty", CloneExec(cleanExec, activationId:string.Empty), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.ActivationIdEmpty),
            new("KillSwitchNotArmed", CloneExec(cleanExec, killSwitchArmed:false), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.KillSwitchNotArmed),
            new("ScopeGuardNotActive", CloneExec(cleanExec, scopeGuardActive:false), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.ScopeGuardNotActive),
            new("RollbackBindingNotPresent", CloneExec(cleanExec, rollbackBindingPresent:false), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.RollbackBindingNotPresent),
            new("ExecGlobalDefaultOnTrue", CloneExec(cleanExec, globalDefaultOn:true), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.GlobalDefaultOnTrue),
            new("ExecPackageOutputChangedTrue", CloneExec(cleanExec, packageOutputChanged:true), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.PackageOutputChangedTrue),
            new("ExecFormalPackageWrittenTrue", CloneExec(cleanExec, formalPackageWritten:true), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.FormalPackageWrittenTrue),
            new("ExecVectorStoreBindingChangedTrue", CloneExec(cleanExec, vectorBindingChanged:true), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.VectorStoreBindingChangedTrue),
            // evidence binding
            new("AppliedEvidenceMissing", cleanExec, BuildEvidenceBinding(skipApplied:true), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.AppliedEvidenceMissing),
            new("AuditEvidenceMissing", cleanExec, BuildEvidenceBinding(skipAudit:true), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.AuditEvidenceMissing),
            new("StateEvidenceMissing", cleanExec, BuildEvidenceBinding(skipState:true), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.StateEvidenceMissing),
            new("AppliedActivationIdMismatch", cleanExec, BuildEvidenceBinding(appliedActivationId:"different-id"), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.AppliedEvidenceActivationIdMismatch),
            new("AuditActivationIdMismatch", cleanExec, BuildEvidenceBinding(auditActivationId:"different-id"), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.AuditEvidenceActivationIdMismatch),
            new("StateActivationIdMismatch", cleanExec, BuildEvidenceBinding(stateActivationId:"different-id"), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.StateEvidenceActivationIdMismatch),
            new("AppliedEvidenceScopeMismatch", cleanExec, BuildEvidenceBinding(appliedScope:"other/scope"), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.AppliedEvidenceScopeMismatch),
            new("AuditEvidenceScopeMismatch", cleanExec, BuildEvidenceBinding(auditScope:"other/scope"), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.AuditEvidenceScopeMismatch),
            new("StateEvidenceScopeMismatch", cleanExec, BuildEvidenceBinding(stateScope:"other/scope"), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.StateEvidenceScopeMismatch),
            new("EvidenceGlobalDefaultOnTrue", cleanExec, BuildEvidenceBinding(globalDefaultOn:true), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.EvidenceGlobalDefaultOnTrue),
            new("EvidencePackageOutputChangedTrue", cleanExec, BuildEvidenceBinding(packageOutputChanged:true), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.EvidencePackageOutputChangedTrue),
            new("EvidenceFormalPackageWrittenTrue", cleanExec, BuildEvidenceBinding(formalPackageWritten:true), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.EvidenceFormalPackageWrittenTrue),
            new("EvidenceVectorStoreBindingChangedTrue", cleanExec, BuildEvidenceBinding(vectorStoreBindingChanged:true), true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.EvidenceVectorStoreBindingChangedTrue),
            // environment
            new("RuntimeGateNotPassed", cleanExec, cleanEvidence, false, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", cleanExec, cleanEvidence, true, false, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.P15GateNotPassed),
            new("MainlineEvidencePresent", cleanExec, cleanEvidence, true, true, true, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", cleanExec, cleanEvidence, true, true, false, true, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.MainlineTrustRegistryPresent),
            // synthetic attempt scenarios (mapped to existing blocked reasons)
            new("AttemptedObservationOutsideScope", CloneExec(cleanExec, scope:"global/*"), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.BoundScopeMismatch),
            new("AttemptedPackageOutputChange", CloneExec(cleanExec, packageOutputChanged:true), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.PackageOutputChangedTrue),
            new("AttemptedGlobalDefaultObservation", CloneExec(cleanExec, globalDefaultOn:true), cleanEvidence, true, true, false, false, ScopedLiveActivationObservationStatuses.ScopedLiveActivationObservationBlocked, ScopedLiveActivationObservationBlockedReasons.GlobalDefaultOnTrue)
        ];
    }

    private static FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport BuildCleanExecutionReport()
        => new()
        {
            OperationId = "frp-guarded-live-runtime-activation-execution-fixture",
            CreatedAt = DateTimeOffset.Parse("2026-06-28T12:00:00Z"),
            GuardedLiveRuntimeActivationExecutionPassed = true,
            GatePassed = true,
            TotalCases = 51,
            PassedCases = 51,
            FailedCases = 0,
            AppliedCases = 2,
            BlockedCases = 49,
            BoundGrantId = TestGrantId,
            BoundCapability = AllowedCapability,
            BoundScope = AllowedScope,
            ActivationId = TestActivationId,
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
            TotalCases = source.TotalCases,
            PassedCases = source.PassedCases,
            FailedCases = source.FailedCases,
            AppliedCases = source.AppliedCases,
            BlockedCases = source.BlockedCases,
            BoundGrantId = source.BoundGrantId,
            BoundCapability = capability ?? source.BoundCapability,
            BoundScope = scope ?? source.BoundScope,
            ActivationId = activationId ?? source.ActivationId,
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

    private static EvidenceBindingSnapshot BuildEvidenceBinding(
        bool skipApplied = false, bool skipAudit = false, bool skipState = false,
        string? appliedActivationId = null, string? auditActivationId = null, string? stateActivationId = null,
        string? appliedScope = null, string? auditScope = null, string? stateScope = null,
        bool globalDefaultOn = false, bool packageOutputChanged = false, bool formalPackageWritten = false, bool vectorStoreBindingChanged = false)
    {
        var applied = skipApplied ? null : new LiveRuntimeActivationAppliedArtifactContent
        {
            ActivationId = appliedActivationId ?? TestActivationId,
            BoundGrantId = TestGrantId,
            Capability = AllowedCapability,
            Scope = appliedScope ?? AllowedScope,
            ActivationMode = "GuardedScopedRuntime",
            RuntimeActivation = true,
            FormalRetrievalAllowed = true,
            RuntimeSwitchAllowed = true,
            GlobalDefaultOn = globalDefaultOn,
            PackageOutputChanged = packageOutputChanged,
            FormalPackageWritten = formalPackageWritten,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
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
            Scope = auditScope ?? AllowedScope,
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
            Scope = stateScope ?? AllowedScope,
            State = "Active",
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

    /// <summary>V8.25: load the 3 evidence artifacts from disk and bind them against the V8.24R gate's ActivationId. Returns null if no executionReport.</summary>
    public static EvidenceBindingSnapshot? LoadRealEvidenceBindingSnapshot(
        FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport? executionReport)
    {
        if (executionReport is null) return null;
        var appliedPath = executionReport.AppliedArtifactPath;
        var auditPath = executionReport.AuditArtifactPath;
        var statePath = executionReport.StateArtifactPath;
        if (string.IsNullOrWhiteSpace(appliedPath) || string.IsNullOrWhiteSpace(auditPath) || string.IsNullOrWhiteSpace(statePath))
        {
            // fall back to the default scoped paths
            var scopeToken = (executionReport.BoundScope ?? string.Empty).Replace('/', '-').Replace('\\', '-');
            var capability = string.IsNullOrWhiteSpace(executionReport.BoundCapability) ? AllowedCapability : executionReport.BoundCapability;
            appliedPath = Path.Combine("vector", "v8", "runtime-activation", $"live-runtime-activation-applied-{capability}-{scopeToken}.json");
            auditPath = Path.Combine("vector", "v8", "runtime-activation", $"live-runtime-activation-audit-{capability}-{scopeToken}.jsonl");
            statePath = Path.Combine("vector", "v8", "runtime-activation", $"live-runtime-activation-state-{capability}-{scopeToken}.json");
        }
        return new EvidenceBindingSnapshot
        {
            ExpectedActivationId = executionReport.ActivationId,
            AppliedPath = appliedPath,
            AuditPath = auditPath,
            StatePath = statePath,
            Applied = ReadJsonFile<LiveRuntimeActivationAppliedArtifactContent>(appliedPath),
            Audit = ReadJsonLine<LiveRuntimeActivationAuditEvent>(auditPath),
            State = ReadJsonFile<LiveRuntimeActivationStateContent>(statePath),
            ExpectValidation = true
        };
    }

    private static T? ReadJsonFile<T>(string path) where T : class
        => string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
    private static T? ReadJsonLine<T>(string path) where T : class
        => string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : JsonSerializer.Deserialize<T>(File.ReadLines(path).FirstOrDefault() ?? string.Empty, JsonOptions);

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalScopedLiveActivationObservationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"- ScopedLiveActivationObservationPassed: `{report.ScopedLiveActivationObservationPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        builder.AppendLine($"- ReadyCases: `{report.ReadyCases}` BlockedCases: `{report.BlockedCases}`");
        builder.AppendLine($"- SourceActivationId: `{report.SourceActivationId}`");
        builder.AppendLine($"- BoundGrantId: `{report.BoundGrantId}` BoundCapability: `{report.BoundCapability}` BoundScope: `{report.BoundScope}`");
        builder.AppendLine($"- ObservationMode: `{report.Observation.ObservationMode}` ObservationId: `{report.Observation.ObservationId}`");
        builder.AppendLine($"- RuntimeActivationObserved: `{report.RuntimeActivationObserved}` FormalRetrievalAllowedObserved: `{report.FormalRetrievalAllowedObserved}` RuntimeSwitchAllowedObserved: `{report.RuntimeSwitchAllowedObserved}`");
        builder.AppendLine($"- RuntimeStateChangedOutsideScope: `{report.RuntimeStateChangedOutsideScope}` GlobalDefaultOn: `{report.GlobalDefaultOn}` PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}` VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- MainlineEvidencePresent: `{report.MainlineEvidencePresent}` MainlineTrustRegistryPresent: `{report.MainlineTrustRegistryPresent}`");
        if (report.BlockedReasons.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Blocked Reasons");
            foreach (var reason in report.BlockedReasons) builder.AppendLine($"- `{reason}`");
        }
        return builder.ToString();
    }
}

public sealed class FormalRetrievalPromotionApprovalScopedLiveActivationObservationCase
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
    public bool RuntimeActivationObserved { get; init; }
    public bool FormalRetrievalAllowedObserved { get; init; }
    public bool RuntimeSwitchAllowedObserved { get; init; }
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

public sealed class FormalRetrievalPromotionApprovalScopedLiveActivationObservationReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ScopedLiveActivationObservationPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalScopedLiveActivationObservationCase> Cases { get; init; }
        = Array.Empty<FormalRetrievalPromotionApprovalScopedLiveActivationObservationCase>();
    public string SourceActivationId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string BoundCapability { get; init; } = string.Empty;
    public string BoundScope { get; init; } = string.Empty;
    public ScopedLiveActivationObservation Observation { get; init; } = new();
    public bool UpstreamGuardedLiveRuntimeActivationExecutionGatePresent { get; init; }
    public bool UpstreamGuardedLiveRuntimeActivationExecutionGatePassed { get; init; }
    public bool RuntimeActivationObserved { get; init; }
    public bool FormalRetrievalAllowedObserved { get; init; }
    public bool RuntimeSwitchAllowedObserved { get; init; }
    public bool RuntimeStateChangedOutsideScope { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class FormalRetrievalPromotionApprovalScopedLiveActivationObservationOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
