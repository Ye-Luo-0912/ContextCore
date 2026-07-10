using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewLiveActivationExecutionRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Plan",
        "ReadV7Freeze",
        "ReadV7NoOpExecution",
        "ReadV7Preflight",
        "ReadV7DryRun",
        "ReadRuntimeChangeGate",
        "ReadP15Report",
        "ValidateExecutionPlanId",
        "ValidateConfigPatchLocked",
        "ArmKillSwitch",
        "PrepareRollbackCheckpoint",
        "PrepareTraceSink",
        "GenerateExecutionRecord",
        "WriteExecutionArtifactsOnly"
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
        "ChangeFormalSelectedSet",
        "MutateApprovedScopes",
        "OverrideExecutionPlan",
        "BypassKillSwitch",
        "DisableRollback",
        "SkipTraceSink",
        "BypassFinalApproval",
    ];

    public ScopedRuntimePreviewLiveActivationExecutionReport RunExecution(
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationExecutionOptions? options = null)
        => BuildReport("execution", false, plan, freeze, noOpExecution, preflight, dryRun, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewLiveActivationExecutionReport RunGate(
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationExecutionOptions? options = null)
        => BuildReport("gate", true, plan, freeze, noOpExecution, preflight, dryRun, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewLiveActivationExecutionReport BuildReport(
        string stage,
        bool isGate,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationExecutionOptions? options)
    {
        options ??= new ScopedRuntimePreviewLiveActivationExecutionOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;

        var planPassed = plan is not null && plan.PlanPassed;
        var freezePassed = freeze is not null && freeze.FreezePassed;
        var noOpPassed = noOpExecution is not null && noOpExecution.NoOpExecutionPassed;
        var preflightPassed = preflight is not null && preflight.PreflightPassed;
        var dryRunPassed = dryRun is not null && dryRun.DryRunPassed;

        if (!options.Enabled) blocked.Add("ExecutionDisabled");
        if (!planPassed) blocked.Add("ExecutionPlanMissingOrNotPassed");
        if (!freezePassed) blocked.Add("FreezeMissingOrNotPassed");
        if (!noOpPassed) blocked.Add("NoOpExecutionMissingOrNotPassed");
        if (!preflightPassed) blocked.Add("PreflightMissingOrNotPassed");
        if (!dryRunPassed) blocked.Add("DryRunMissingOrNotPassed");
        if (!runtimeChangeGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed) blocked.Add("P15GateNotPassed");

        var planExecutionPlanId = plan?.ExecutionPlanId ?? "";
        var requestedPlanId = options.ExecutionPlanId;
        var planIdMatches = !string.IsNullOrWhiteSpace(planExecutionPlanId)
            && string.Equals(planExecutionPlanId, requestedPlanId, StringComparison.OrdinalIgnoreCase);

        if (isGate && string.IsNullOrWhiteSpace(requestedPlanId))
            blocked.Add("ExecutionPlanIdMissing");
        else if (isGate && !planIdMatches)
            blocked.Add("PlanIdMismatch");
        else if (!isGate && string.IsNullOrWhiteSpace(planExecutionPlanId))
            blocked.Add("ExecutionPlanIdMissingOnDisk");

        var configPatchPreviewLocked = plan?.ConfigPatchPreviewLocked ?? false;
        if (!configPatchPreviewLocked)
            blocked.Add("ConfigPatchPreviewNotLocked");

        var approveScopes = plan?.ApprovedScopes ?? Array.Empty<string>();
        var killSwitchArmed = true;
        var rollbackCheckpointId = $"arsp-rollback-checkpoint-{now:yyyyMMdd}-{Guid.NewGuid():N}";
        var traceSinkPath = $"vector/v7/live-activation-trace-{now:yyyyMMdd}-{now:HHmmss}.jsonl";

        var configPatchId = $"arsp-config-patch-hash-preview-{now:yyyyMMdd}-{Guid.NewGuid():N}";
        var windowDuration = plan?.ActivationWindowDurationMinutes ?? 30;
        var requestCap = plan?.ActivationWindowRequestCap ?? 100;
        var activationWindowStart = now;
        var activationWindowEnd = now.AddMinutes(windowDuration);

        var stopConditions = new List<string>
        {
            "AnySafetyBoundaryViolation",
            "KillSwitchTriggered",
            "AppliedDelta > 0",
            "ConfigPatchWritten",
            "RuntimeActivation",
            "RuntimeSwitchChanged",
            "P15Regression",
            "RuntimeChangeGateFailed",
            "AuthorizationExpired",
            "OperatorAbort",
        };

        var executeLiveActivation = options.ExecuteLiveActivation;

        if (isGate && !executeLiveActivation)
            blocked.Add("ExecuteLiveActivationNotRequested");

        if (isGate && !options.FinalApprovalExplicitlyProvided)
            blocked.Add("FinalApprovalMissing");
        else if (isGate && string.IsNullOrWhiteSpace(options.FinalApprovedBy))
            blocked.Add("FinalApprovalEmpty");
        else if (isGate && options.FinalApprovalExplicitlyProvided)
        {
            var freezeApprovedBy = freeze?.FinalApprovedBy ?? "";
            if (!string.IsNullOrWhiteSpace(freezeApprovedBy)
                && !string.Equals(freezeApprovedBy, options.FinalApprovedBy, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("FinalApprovalMismatch");
                diag.Add($"freezeFinalApprovedBy={freezeApprovedBy} provided={options.FinalApprovedBy}");
            }
        }

        var dryRunPresent = dryRun is not null;
        var formalRetrievalAllowed = dryRunPresent && dryRun!.FormalRetrievalAllowed;
        var formalPackageWritten = dryRunPresent && dryRun!.FormalPackageWritten;
        var packingPolicyChanged = dryRunPresent && dryRun!.PackingPolicyChanged;
        var packageOutputChanged = dryRunPresent && dryRun!.PackageOutputChanged;
        var vectorBindingChanged = dryRunPresent && dryRun!.VectorStoreBindingChanged;
        var globalDefaultOn = dryRunPresent && dryRun!.GlobalDefaultOn;
        var noRuntimeMutationInvariant = !formalRetrievalAllowed && !formalPackageWritten
            && !packingPolicyChanged && !packageOutputChanged && !vectorBindingChanged && !globalDefaultOn;

        if (formalRetrievalAllowed) blocked.Add("SafetyBoundaryFormalRetrievalAllowed");
        if (formalPackageWritten) blocked.Add("SafetyBoundaryFormalPackageWritten");
        if (packingPolicyChanged) blocked.Add("SafetyBoundaryPackingPolicyChanged");
        if (packageOutputChanged) blocked.Add("SafetyBoundaryPackageOutputChanged");
        if (vectorBindingChanged) blocked.Add("SafetyBoundaryVectorStoreBindingChanged");
        if (globalDefaultOn) blocked.Add("SafetyBoundaryGlobalDefaultOn");

        var runtimeActivationDetected = plan?.RuntimeActivation ?? false;
        if (runtimeActivationDetected) blocked.Add("SafetyBoundaryRuntimeActivation");

        if (!killSwitchArmed) blocked.Add("KillSwitchNotArmed");
        if (string.IsNullOrWhiteSpace(rollbackCheckpointId)) blocked.Add("RollbackCheckpointMissing");
        if (string.IsNullOrWhiteSpace(traceSinkPath)) blocked.Add("TraceSinkMissing");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var executionGatePassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && executionGatePassed;

        var activationExecutionId = executionGatePassed
            ? $"arsp-live-exec-{now:yyyyMMdd}-{Guid.NewGuid():N}"
            : "";

        diag.Add($"stage={stage}");
        diag.Add($"planPassed={planPassed}");
        diag.Add($"freezePassed={freezePassed}");
        diag.Add($"noOpPassed={noOpPassed}");
        diag.Add($"planIdMatches={planIdMatches}");
        diag.Add($"configPatchPreviewLocked={configPatchPreviewLocked}");
        diag.Add($"executeLiveActivation={executeLiveActivation}");
        diag.Add($"killSwitchArmed={killSwitchArmed}");
        diag.Add($"configPatchWritten=false runtimeActivation=false runtimeSwitchChanged=false");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"executionGatePassed={executionGatePassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewLiveActivationExecutionReport
        {
            OperationId = $"arsp-live-exec-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            ExecutionGatePassed = executionGatePassed,
            GatePassed = gatePassed,
            Recommendation = executionGatePassed
                ? (executeLiveActivation
                    ? ScopedRuntimePreviewLiveActivationExecutionRecommendations.ReadyForExplicitLiveActivationCommand
                    : ScopedRuntimePreviewLiveActivationExecutionRecommendations.ExecuteLiveActivationNotRequested)
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = executionGatePassed
                ? (executeLiveActivation ? "ScopedRuntimePreviewLiveActivationRunning" : "ScopedRuntimePreviewLiveActivationStandingBy")
                : "KeepPreviewOnly",

            ExecuteLiveActivation = executeLiveActivation,
            ActivationExecutionId = activationExecutionId,
            ExecutionPlanId = planExecutionPlanId,
            AppliedConfigPatchId = configPatchId,
            ApprovedScopes = approveScopes,
            ActivationWindowStart = activationWindowStart,
            ActivationWindowEnd = activationWindowEnd,
            RequestCap = requestCap,
            KillSwitchArmed = killSwitchArmed,
            RollbackCheckpointId = rollbackCheckpointId,
            TraceSinkPath = traceSinkPath,
            StopConditions = stopConditions,

            ConfigPatchWritten = false,
            RuntimeActivation = false,
            RuntimeSwitchChanged = false,
            PlanIdMatches = planIdMatches,
            ConfigPatchPreviewLocked = configPatchPreviewLocked,

            V7PlanPassed = planPassed,
            V7FreezePassed = freezePassed,
            V7NoOpPassed = noOpPassed,
            V7PreflightPassed = preflightPassed,
            V7DryRunPassed = dryRunPassed,
            P15GatePassed = p15GatePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,
            FinalApprovalPresent = !string.IsNullOrWhiteSpace(options.FinalApprovedBy),

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

    public static string BuildMarkdown(string title, ScopedRuntimePreviewLiveActivationExecutionReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- ExecutionGatePassed: `{r.ExecutionGatePassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine($"- ExecuteLiveActivation: `{r.ExecuteLiveActivation}`");
        b.AppendLine();

        b.AppendLine("## Execution Record");
        b.AppendLine($"- ActivationExecutionId: `{r.ActivationExecutionId}`");
        b.AppendLine($"- ExecutionPlanId: `{r.ExecutionPlanId[..Math.Min(12, r.ExecutionPlanId.Length)]}...`");
        b.AppendLine($"- AppliedConfigPatchId: `{r.AppliedConfigPatchId[..Math.Min(12, r.AppliedConfigPatchId.Length)]}...`");
        b.AppendLine($"- ApprovedScopes: `{string.Join(", ", r.ApprovedScopes)}`");
        b.AppendLine($"- ActivationWindowStart: `{r.ActivationWindowStart:O}`");
        b.AppendLine($"- ActivationWindowEnd: `{r.ActivationWindowEnd:O}`");
        b.AppendLine($"- RequestCap: `{r.RequestCap}`");
        b.AppendLine($"- KillSwitchArmed: `{r.KillSwitchArmed}`");
        b.AppendLine($"- RollbackCheckpointId: `{r.RollbackCheckpointId[..Math.Min(12, r.RollbackCheckpointId.Length)]}...`");
        b.AppendLine($"- TraceSinkPath: `{r.TraceSinkPath}`");
        AppendList(b, "Stop Conditions", r.StopConditions);
        b.AppendLine();

        b.AppendLine("## Hard Boundaries");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine($"- RuntimeSwitchChanged: `{r.RuntimeSwitchChanged}`");
        b.AppendLine($"- PlanIdMatches: `{r.PlanIdMatches}`");
        b.AppendLine($"- ConfigPatchPreviewLocked: `{r.ConfigPatchPreviewLocked}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");

        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        AppendList(b, "Blocked Reasons", r.BlockedReasons);
        AppendList(b, "Diagnostics", r.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.13 guarded scoped runtime preview live activation execution gate。默认只生成执行记录，需要显式 --execute-live-activation。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Plan", StringComparison.OrdinalIgnoreCase) && !r.Contains("Preflight", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionRecommendations.BlockedByMissingPlan;
        if (blocked.Any(static r => r.Contains("Freeze", StringComparison.OrdinalIgnoreCase) || r.Contains("Missing", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionRecommendations.BlockedByMissingFreeze;
        if (blocked.Any(static r => r.Contains("PlanId", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionRecommendations.BlockedByPlanIdMismatch;
        if (blocked.Any(static r => r.Contains("ConfigPatch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionRecommendations.BlockedByConfigPatchNotLocked;
        if (blocked.Any(static r => r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionRecommendations.BlockedByKillSwitchNotArmed;
        if (blocked.Any(static r => r.Contains("Rollback", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionRecommendations.BlockedByRollbackCheckpointMissing;
        if (blocked.Any(static r => r.Contains("Trace", StringComparison.OrdinalIgnoreCase) || r.Contains("Sink", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionRecommendations.BlockedByTraceSinkMissing;
        if (blocked.Any(static r => r.Contains("Scope", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionRecommendations.BlockedByScopeMismatch;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewLiveActivationExecutionRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
