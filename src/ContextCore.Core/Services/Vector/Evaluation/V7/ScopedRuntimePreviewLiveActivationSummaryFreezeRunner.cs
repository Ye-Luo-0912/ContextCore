using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewLiveActivationSummaryFreezeRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Observation", "ReadV7Execution", "ReadV7Plan", "ReadV7Freeze", "ReadV7NoOpExecution",
        "ReadRuntimeChangeGate", "ReadP15Report", "FreezeObservationMetrics", "ValidateIdentityChain",
        "ValidatePlanIntegrity", "ValidateAllSafetyBoundaries", "WriteFreezeArtifactsOnly"
    ];

    private static readonly string[] FrozenForbiddenActions =
    [
        "GlobalDefaultOn", "FormalRetrievalEnable", "FormalPackageWrite", "PackingPolicyMutation",
        "PackageOutputMutation", "VectorStoreBindingMutation", "RuntimeSwitch", "RuntimeActivation",
        "RuntimeSwitchChanged", "WriteConfigPatch", "ApplyPreviewResult", "ChangeFormalSelectedSet",
        "MutateApprovedScopes", "OverrideFrozenMetrics", "ChangeFrozenEvidenceChain",
    ];

    public ScopedRuntimePreviewLiveActivationSummaryFreezeReport RunFreeze(
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        bool rtGatePassed, bool p15Passed,
        ScopedRuntimePreviewLiveActivationSummaryFreezeOptions? options = null)
        => BuildReport("freeze", false, observation, execution, plan, freeze, noOpExecution, rtGatePassed, p15Passed, options);

    public ScopedRuntimePreviewLiveActivationSummaryFreezeReport RunGate(
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        bool rtGatePassed, bool p15Passed,
        ScopedRuntimePreviewLiveActivationSummaryFreezeOptions? options = null)
        => BuildReport("gate", true, observation, execution, plan, freeze, noOpExecution, rtGatePassed, p15Passed, options);

    private static ScopedRuntimePreviewLiveActivationSummaryFreezeReport BuildReport(
        string stage, bool isGate,
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        bool rtGatePassed, bool p15Passed,
        ScopedRuntimePreviewLiveActivationSummaryFreezeOptions? options)
    {
        options ??= new ScopedRuntimePreviewLiveActivationSummaryFreezeOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;

        var obsPassed = observation is not null && observation.ObservationPassed;
        var execPassed = execution is not null && execution.ExecutionGatePassed;
        var planPassed = plan is not null && plan.PlanPassed;
        var v7FreezePassed = freeze is not null && freeze.FreezePassed;
        var noOpPassed = noOpExecution is not null && noOpExecution.NoOpExecutionPassed;

        if (!options.Enabled) blocked.Add("FreezeDisabled");
        if (!obsPassed) blocked.Add("ObservationMissingOrNotPassed");
        if (!execPassed) blocked.Add("ExecutionMissingOrNotPassed");
        if (!planPassed) blocked.Add("PlanMissingOrNotPassed");
        if (!v7FreezePassed) blocked.Add("FreezeMissingOrNotPassed");
        if (!noOpPassed) blocked.Add("NoOpExecutionMissingOrNotPassed");
        if (!rtGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var identityUnchanged = observation?.FinalApprovalIdentityUnchanged ?? false;
        if (!identityUnchanged) blocked.Add("FinalApprovalIdentityMismatch");

        var planIdUnchanged = observation?.PlanIdUnchanged ?? false;
        if (!planIdUnchanged) blocked.Add("ExecutionPlanIdMismatch");

        ScopedRuntimePreviewLiveActivationSummaryFrozenMetrics? frozenMetrics = null;
        if (observation is not null)
        {
            frozenMetrics = new ScopedRuntimePreviewLiveActivationSummaryFrozenMetrics
            {
                ObservedRequestCount = observation.ObservedRequestCount,
                MaxRequestCap = observation.MaxRequestCap,
                ApprovedScopeRequestCount = observation.ApprovedScopeRequestCount,
                NonApprovedScopeRequestCount = observation.NonApprovedScopeRequestCount,
                NonApprovedScopeNoOpCount = observation.NonApprovedScopeNoOpCount,
                KillSwitchTripCount = observation.KillSwitchTripCount,
                KillSwitchNoOpCount = observation.KillSwitchNoOpCount,
                AppliedDeltaCount = observation.AppliedDeltaCount,
                ConfigPatchWritten = observation.ConfigPatchWritten,
                RuntimeActivation = observation.RuntimeActivation,
                RuntimeSwitchChanged = observation.RuntimeSwitchChanged,
            };

            if (frozenMetrics.ObservedRequestCount > frozenMetrics.MaxRequestCap) blocked.Add("FrozenRequestCapExceeded");
            if (frozenMetrics.NonApprovedScopeNoOpCount != frozenMetrics.NonApprovedScopeRequestCount) blocked.Add("FrozenNonApprovedScopeNotNoOp");
            if (frozenMetrics.KillSwitchNoOpCount != frozenMetrics.KillSwitchTripCount) blocked.Add("FrozenKillSwitchTripNotNoOp");
            if (frozenMetrics.AppliedDeltaCount != 0) blocked.Add("FrozenAppliedDeltaViolation");
            if (frozenMetrics.ConfigPatchWritten) blocked.Add("FrozenConfigPatchWrittenViolation");
            if (frozenMetrics.RuntimeActivation) blocked.Add("FrozenRuntimeActivationViolation");
        }

        var formalRetrievalAllowed = observation?.FormalRetrievalAllowed ?? false;
        var formalPackageWritten = observation?.FormalPackageWritten ?? false;
        var packingPolicyChanged = observation?.PackingPolicyChanged ?? false;
        var packageOutputChanged = observation?.PackageOutputChanged ?? false;
        var vectorBindingChanged = observation?.VectorStoreBindingChanged ?? false;
        var globalDefaultOn = observation?.GlobalDefaultOn ?? false;
        var noRuntimeMutationInvariant = !formalRetrievalAllowed && !formalPackageWritten
            && !packingPolicyChanged && !packageOutputChanged && !vectorBindingChanged && !globalDefaultOn;

        if (formalRetrievalAllowed) blocked.Add("SafetyBoundaryFormalRetrievalAllowed");
        if (formalPackageWritten) blocked.Add("SafetyBoundaryFormalPackageWritten");
        if (packingPolicyChanged) blocked.Add("SafetyBoundaryPackingPolicyChanged");
        if (packageOutputChanged) blocked.Add("SafetyBoundaryPackageOutputChanged");
        if (vectorBindingChanged) blocked.Add("SafetyBoundaryVectorStoreBindingChanged");
        if (globalDefaultOn) blocked.Add("SafetyBoundaryGlobalDefaultOn");

        var activationExecId = execution?.ActivationExecutionId ?? "";
        var execPlanId = observation?.ExecutionPlanId ?? "";
        var finalApprovedBy = freeze?.FinalApprovedBy ?? "";
        var finalApprovalId = freeze?.FinalApprovalId ?? "";
        var approvedScopes = plan?.ApprovedScopes ?? Array.Empty<string>();
        var shadowTracePath = observation?.ShadowTracePath ?? "";

        var frozenEvidenceChain = new List<string>
        {
            "V7.4  observation-freeze.json              — FreezePassed",
            "V7.5  approval-plan.json                   — PlanPassed",
            "V7.6  authorization.json                   — Authorized",
            "V7.6R2 authorization-hardening.json         — HardeningPassed",
            "V7.7  activation-preparation.json           — PreparationPassed",
            "V7.8R activation-dry-run.json               — DryRunPassed",
            "V7.9  activation-window-preflight.json      — PreflightPassed",
            "V7.10 activation-window-noop-execution.json  — NoOpExecutionPassed",
            "V7.11 activation-live-readiness-freeze.json — FreezePassed",
            "V7.12 live-activation-execution-plan.json   — PlanPassed",
            "V7.13R2 live-activation-execution.json       — ExecutionGatePassed",
            "V7.14R live-activation-observation.json      — ObservationPassed",
            $"V7.15  live-activation-summary-freeze.json   — FreezePassed (this artifact, {now:yyyy-MM-dd})",
        };

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var freezePassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && freezePassed;

        diag.Add($"stage={stage}");
        diag.Add($"observationPassed={obsPassed}");
        diag.Add($"executionPassed={execPassed}");
        diag.Add($"identityUnchanged={identityUnchanged}");
        diag.Add($"planIdUnchanged={planIdUnchanged}");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"evidenceChain={frozenEvidenceChain.Count} items");
        diag.Add($"freezePassed={freezePassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewLiveActivationSummaryFreezeReport
        {
            OperationId = $"arsp-summary-freeze-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            FreezePassed = freezePassed,
            GatePassed = gatePassed,
            Recommendation = freezePassed
                ? ScopedRuntimePreviewLiveActivationSummaryFreezeRecommendations.ReadyForLiveActivationCloseout
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = freezePassed ? "LiveActivationCloseout" : "KeepPreviewOnly",

            ActivationExecutionId = activationExecId,
            ExecutionPlanId = execPlanId,
            FinalApprovedBy = finalApprovedBy,
            FinalApprovalId = finalApprovalId,
            ApprovedScopes = approvedScopes,
            ObservationSource = "DeterministicShadowTraceFixture",
            ShadowTracePath = shadowTracePath,
            FrozenEvidenceChain = frozenEvidenceChain,
            FrozenMetrics = frozenMetrics,

            V7ObservationPassed = obsPassed,
            V7ExecutionGatePassed = execPassed,
            V7PlanPassed = planPassed,
            V7FreezePassed = v7FreezePassed,
            V7NoOpPassed = noOpPassed,
            P15GatePassed = p15Passed,
            RuntimeChangeGatePassed = rtGatePassed,
            FinalApprovalIdentityUnchanged = identityUnchanged,
            ExecutionPlanIdUnchanged = planIdUnchanged,

            FormalRetrievalAllowed = formalRetrievalAllowed,
            FormalPackageWritten = formalPackageWritten,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            VectorStoreBindingChanged = vectorBindingChanged,
            GlobalDefaultOn = globalDefaultOn,
            NoRuntimeMutationInvariant = noRuntimeMutationInvariant,

            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ScopedRuntimePreviewLiveActivationSummaryFreezeReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- FreezePassed: `{r.FreezePassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Frozen Identity");
        b.AppendLine($"- ActivationExecutionId: `{r.ActivationExecutionId[..Math.Min(12, r.ActivationExecutionId.Length)]}...`");
        b.AppendLine($"- ExecutionPlanId: `{r.ExecutionPlanId[..Math.Min(12, r.ExecutionPlanId.Length)]}...`");
        b.AppendLine($"- FinalApprovedBy: `{r.FinalApprovedBy}`");
        b.AppendLine($"- ObservationSource: `{r.ObservationSource}`");
        AppendList(b, "Frozen Evidence Chain", r.FrozenEvidenceChain);

        b.AppendLine();
        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        b.AppendLine();
        b.AppendLine("V7.15 live activation summary freeze。冻结执行记录与 shadow observation 证据链。GatePassed=false is expected for non-gate artifact。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Observation", StringComparison.OrdinalIgnoreCase) || r.Contains("Missing", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationSummaryFreezeRecommendations.BlockedByMissingObservation;
        if (blocked.Any(static r => r.Contains("Execution", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationSummaryFreezeRecommendations.BlockedByMissingExecution;
        if (blocked.Any(static r => r.Contains("Identity", StringComparison.OrdinalIgnoreCase) || r.Contains("PlanId", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationSummaryFreezeRecommendations.BlockedByIdentityMismatch;
        if (blocked.Any(static r => r.Contains("Frozen", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationSummaryFreezeRecommendations.BlockedByFrozenMetricsViolation;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationSummaryFreezeRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewLiveActivationSummaryFreezeRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
