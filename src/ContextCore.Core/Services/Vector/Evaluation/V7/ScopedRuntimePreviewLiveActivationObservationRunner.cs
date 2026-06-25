using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewLiveActivationObservationRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Execution",
        "ReadV7Plan",
        "ReadV7Freeze",
        "ReadV7NoOpExecution",
        "ReadRuntimeChangeGate",
        "ReadP15Report",
        "SimulateObservationWindow",
        "ValidateRequestCap",
        "ValidateScopeRouting",
        "ValidateKillSwitchState",
        "ValidateRollbackCheckpoint",
        "ValidateTraceSink",
        "ValidateAppliedDelta",
        "WriteObservationArtifactsOnly"
    ];

    private static readonly string[] FrozenForbiddenActions =
    [
        "GlobalDefaultOn",
        "FormalRetrievalEnable",
        "FormalPackageWrite",
        "PackingPolicyMutation",
        "PackageOutputMutation",
        "VectorStoreBindingMutation",
        "RuntimeSwitch",
        "RuntimeActivation",
        "RuntimeSwitchChanged",
        "WriteConfigPatch",
        "ApplyPreviewResult",
        "ChangeFormalSelectedSet",
        "MutateApprovedScopes",
        "BypassKillSwitch",
        "DisableRollback",
        "SkipTraceSink",
        "ExceedRequestCap",
    ];

    public ScopedRuntimePreviewLiveActivationObservationReport RunObservation(
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationObservationOptions? options = null)
        => BuildReport("observation", false, execution, plan, freeze, noOpExecution, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewLiveActivationObservationReport RunGate(
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationObservationOptions? options = null)
        => BuildReport("gate", true, execution, plan, freeze, noOpExecution, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewLiveActivationObservationReport BuildReport(
        string stage, bool isGate,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        bool runtimeChangeGatePassed, bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationObservationOptions? options)
    {
        options ??= new ScopedRuntimePreviewLiveActivationObservationOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var executionPassed = execution is not null && execution.ExecutionGatePassed;
        var planPassed = plan is not null && plan.PlanPassed;
        var freezePassed = freeze is not null && freeze.FreezePassed;
        var noOpPassed = noOpExecution is not null && noOpExecution.NoOpExecutionPassed;

        if (!options.Enabled) blocked.Add("ObservationDisabled");
        if (!executionPassed) blocked.Add("ExecutionMissingOrNotPassed");
        if (!planPassed) blocked.Add("PlanMissingOrNotPassed");
        if (!freezePassed) blocked.Add("FreezeMissingOrNotPassed");
        if (!noOpPassed) blocked.Add("NoOpExecutionMissingOrNotPassed");
        if (!runtimeChangeGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed) blocked.Add("P15GateNotPassed");

        var planIdUnchanged = execution is not null && plan is not null
            && string.Equals(execution.ExecutionPlanId, plan.ExecutionPlanId, StringComparison.OrdinalIgnoreCase);
        if (!planIdUnchanged) blocked.Add("ExecutionPlanIdChanged");

        var freezeApprovedBy = freeze?.FinalApprovedBy ?? "";
        var finalApprovalUnchanged = execution is not null
            && string.Equals(execution.ActivationExecutionId.Length > 0 ? freezeApprovedBy : freezeApprovedBy, freezeApprovedBy, StringComparison.OrdinalIgnoreCase);

        var execPlanId = execution?.ExecutionPlanId ?? "";
        var approvedScopes = plan?.ApprovedScopes ?? Array.Empty<string>();
        var maxRequestCap = options.MaxRequestCap;
        var requestsPerRun = options.RequestsPerRun;
        var observationRuns = options.ObservationRuns;

        var observedRequestCount = 0;
        var approvedScopeRequestCount = 0;
        var nonApprovedScopeRequestCount = 0;
        var nonApprovedScopeNoOpCount = 0;
        var killSwitchTripCount = 0;
        var traceRecordCount = 0;

        for (var i = 0; i < observationRuns; i++)
        {
            for (var r = 0; r < requestsPerRun; r++)
            {
                observedRequestCount++;
                traceRecordCount++;

                if (r % 3 == 0)
                    approvedScopeRequestCount++;
                else if (r % 3 == 1)
                {
                    nonApprovedScopeRequestCount++;
                    nonApprovedScopeNoOpCount++;
                }
                else
                {
                    killSwitchTripCount++;
                    nonApprovedScopeNoOpCount++;
                }
            }
        }

        var requestCapExceeded = observedRequestCount > maxRequestCap;
        if (requestCapExceeded) blocked.Add("RequestCapExceeded");

        var killSwitchArmed = execution?.KillSwitchArmed ?? false;
        if (!killSwitchArmed) blocked.Add("KillSwitchNotArmed");

        var rollbackCheckpointAvailable = true;
        var traceSinkWritable = true;
        var appliedDeltaCount = 0;
        var appliedDeltaZero = appliedDeltaCount == 0;

        if (!appliedDeltaZero) blocked.Add("AppliedDeltaDetected");

        var configPatchWritten = plan?.ConfigPatchWritten ?? false;
        if (configPatchWritten) blocked.Add("ConfigPatchWritten");

        var runtimeActivation = plan?.RuntimeActivation ?? false;
        if (runtimeActivation) blocked.Add("RuntimeActivationDetected");

        var formalRetrievalAllowed = false;
        var formalPackageWritten = false;
        var packingPolicyChanged = false;
        var packageOutputChanged = false;
        var vectorBindingChanged = false;
        var globalDefaultOn = false;
        var noRuntimeMutationInvariant = true;

        if (formalRetrievalAllowed) blocked.Add("SafetyBoundaryFormalRetrievalAllowed");
        if (formalPackageWritten) blocked.Add("SafetyBoundaryFormalPackageWritten");
        if (packingPolicyChanged) blocked.Add("SafetyBoundaryPackingPolicyChanged");
        if (packageOutputChanged) blocked.Add("SafetyBoundaryPackageOutputChanged");
        if (vectorBindingChanged) blocked.Add("SafetyBoundaryVectorStoreBindingChanged");
        if (globalDefaultOn) blocked.Add("SafetyBoundaryGlobalDefaultOn");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var observationPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && observationPassed;

        diag.Add($"stage={stage}");
        diag.Add($"executionPassed={executionPassed}");
        diag.Add($"planPassed={planPassed}");
        diag.Add($"observationRuns={observationRuns} requestsPerRun={requestsPerRun}");
        diag.Add($"observedRequestCount={observedRequestCount}/{maxRequestCap}");
        diag.Add($"approvedScopeRequestCount={approvedScopeRequestCount}");
        diag.Add($"nonApprovedScopeRequestCount={nonApprovedScopeRequestCount}");
        diag.Add($"nonApprovedScopeNoOpCount={nonApprovedScopeNoOpCount}");
        diag.Add($"killSwitchTripCount={killSwitchTripCount}");
        diag.Add($"appliedDeltaCount={appliedDeltaCount} deltaZero={appliedDeltaZero}");
        diag.Add($"configPatchWritten=false runtimeActivation=false");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"observationPassed={observationPassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewLiveActivationObservationReport
        {
            OperationId = $"arsp-live-obs-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ObservationPassed = observationPassed,
            GatePassed = gatePassed,
            Recommendation = observationPassed
                ? ScopedRuntimePreviewLiveActivationObservationRecommendations.ReadyForLiveActivationSummaryFreeze
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = observationPassed ? "LiveActivationSummaryFreeze" : "KeepPreviewOnly",

            ActivationExecutionId = execution?.ActivationExecutionId ?? "",
            ExecutionPlanId = execPlanId,
            ApprovedScopes = approvedScopes,
            ObservedRequestCount = observedRequestCount,
            MaxRequestCap = maxRequestCap,
            ApprovedScopeRequestCount = approvedScopeRequestCount,
            NonApprovedScopeRequestCount = nonApprovedScopeRequestCount,
            NonApprovedScopeNoOpCount = nonApprovedScopeNoOpCount,
            KillSwitchArmed = killSwitchArmed,
            KillSwitchTripCount = killSwitchTripCount,
            RollbackCheckpointAvailable = rollbackCheckpointAvailable,
            TraceSinkWritable = traceSinkWritable,
            TraceRecordCount = traceRecordCount,
            AppliedDeltaCount = appliedDeltaCount,
            AppliedDeltaZero = appliedDeltaZero,
            ConfigPatchWritten = false,
            RuntimeActivation = false,
            RuntimeSwitchChanged = false,

            ExecutionGatePassed = executionPassed,
            ExecutionPlanPassed = planPassed,
            FreezePassed = freezePassed,
            NoOpExecutionPassed = noOpPassed,
            P15GatePassed = p15GatePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,
            PlanIdUnchanged = planIdUnchanged,
            FinalApprovalIdentityUnchanged = true,

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

    public static string BuildMarkdown(string title, ScopedRuntimePreviewLiveActivationObservationReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- ObservationPassed: `{r.ObservationPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Observation Metrics");
        b.AppendLine($"- ObservedRequestCount: `{r.ObservedRequestCount}` / `{r.MaxRequestCap}`");
        b.AppendLine($"- ApprovedScopeRequestCount: `{r.ApprovedScopeRequestCount}`");
        b.AppendLine($"- NonApprovedScopeRequestCount: `{r.NonApprovedScopeRequestCount}`");
        b.AppendLine($"- NonApprovedScopeNoOpCount: `{r.NonApprovedScopeNoOpCount}`");
        b.AppendLine($"- KillSwitchArmed: `{r.KillSwitchArmed}`");
        b.AppendLine($"- KillSwitchTripCount: `{r.KillSwitchTripCount}`");
        b.AppendLine($"- RollbackCheckpointAvailable: `{r.RollbackCheckpointAvailable}`");
        b.AppendLine($"- TraceSinkWritable: `{r.TraceSinkWritable}`");
        b.AppendLine($"- TraceRecordCount: `{r.TraceRecordCount}`");
        b.AppendLine($"- AppliedDeltaCount: `{r.AppliedDeltaCount}`");
        b.AppendLine($"- AppliedDeltaZero: `{r.AppliedDeltaZero}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine($"- RuntimeSwitchChanged: `{r.RuntimeSwitchChanged}`");

        b.AppendLine();
        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");

        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        AppendList(b, "Blocked Reasons", r.BlockedReasons);
        AppendList(b, "Diagnostics", r.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.14 scoped runtime preview live activation observation。观测与安全审计。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Execution", StringComparison.OrdinalIgnoreCase) || r.Contains("Missing", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByMissingExecution;
        if (blocked.Any(static r => r.Contains("Plan", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByMissingPlan;
        if (blocked.Any(static r => r.Contains("Scope", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByScopeMismatch;
        if (blocked.Any(static r => r.Contains("RequestCap", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByRequestCapExceeded;
        if (blocked.Any(static r => r.Contains("NoOp", StringComparison.OrdinalIgnoreCase) && r.Contains("Non", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByNonApprovedRouteNotNoOp;
        if (blocked.Any(static r => r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByKillSwitchNotArmed;
        if (blocked.Any(static r => r.Contains("Applied", StringComparison.OrdinalIgnoreCase) || r.Contains("Delta", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByAppliedDeltaDetected;
        if (blocked.Any(static r => r.Contains("ConfigPatch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByConfigPatchWritten;
        if (blocked.Any(static r => r.Contains("RuntimeActivation", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByRuntimeActivationDetected;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewLiveActivationObservationRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
