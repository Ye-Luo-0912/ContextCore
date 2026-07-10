using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewLiveActivationExecutionPlanRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Freeze",
        "ReadV7NoOpExecution",
        "ReadV7Preflight",
        "ReadV7DryRun",
        "ReadRuntimeChangeGate",
        "ReadP15Report",
        "GenerateExecutionPlan",
        "LockConfigPatchPreview",
        "DefineRollbackTriggerPolicy",
        "DefineKillSwitchTriggerPolicy",
        "DefineMonitoringPlan",
        "DefineAbortConditions",
        "WritePlanArtifactsOnly"
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
        "WriteConfigPatch",
        "ApplyPreviewResult",
        "ChangeFormalSelectedSet",
        "MutateApprovedScopes",
        "OverrideFrozenPlan",
        "BypassFinalApproval",
        "ChangeLockedConfigPatch",
    ];

    public ScopedRuntimePreviewLiveActivationExecutionPlanReport RunPlan(
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationExecutionPlanOptions? options = null)
        => BuildReport("plan", false, freeze, noOpExecution, preflight, dryRun, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewLiveActivationExecutionPlanReport RunGate(
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationExecutionPlanOptions? options = null)
        => BuildReport("gate", true, freeze, noOpExecution, preflight, dryRun, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewLiveActivationExecutionPlanReport BuildReport(
        string stage,
        bool isGate,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationExecutionPlanOptions? options)
    {
        options ??= new ScopedRuntimePreviewLiveActivationExecutionPlanOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var freezePassed = freeze is not null && freeze.FreezePassed;
        var noOpPassed = noOpExecution is not null && noOpExecution.NoOpExecutionPassed;
        var preflightPassed = preflight is not null && preflight.PreflightPassed;
        var dryRunPassed = dryRun is not null && dryRun.DryRunPassed;

        if (!options.Enabled) blocked.Add("PlanDisabled");
        if (!freezePassed) blocked.Add("FreezeMissingOrNotPassed");
        if (!noOpPassed) blocked.Add("NoOpExecutionMissingOrNotPassed");
        if (!preflightPassed) blocked.Add("PreflightMissingOrNotPassed");
        if (!dryRunPassed) blocked.Add("DryRunMissingOrNotPassed");
        if (!runtimeChangeGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed) blocked.Add("P15GateNotPassed");

        var finalApprovalPresent = isGate
            ? options.FinalApprovalExplicitlyProvided && !string.IsNullOrWhiteSpace(options.FinalApprovedBy)
            : ((freeze?.FinalApprovedBy?.Length) > 0);

        if (isGate && !finalApprovalPresent)
            blocked.Add("FinalApprovalNotPresent");

        var now = DateTimeOffset.UtcNow;
        var executionPlanId = $"arsp-exec-plan-{now:yyyyMMdd}-{Guid.NewGuid():N}";
        var finalApprovalId = freeze?.FinalApprovalId ?? "";
        var approvedScopes = freeze?.FinalApprovedScopes ?? Array.Empty<string>();
        var windowDuration = 30;
        var requestCap = 100;
        var rollbackPolicy = freeze?.RollbackTriggerPolicy ?? "AnySafetyBoundaryViolation | AnyKillSwitchTrip | AnyAppliedDelta | OperatorOverride";
        var killSwitchPolicy = freeze?.KillSwitchTriggerPolicy ?? "ConfigPatchWritten | RuntimeActivation | RuntimeSwitchAllowed | AppliedDelta > 0 | OperatorOverride";
        var configPatchPreview = $"config/runtime-preview-v7-scoped-{now:yyyyMMdd}.json (preview only, locked, ConfigPatchWritten=false)";
        var runtimeSwitchPreview = "RuntimeSwitch=previewOnly, RuntimeActivation=false, no formal retrieval bound";
        var monitoringPlan = $"MonitorApprovedScopeHits | MonitorNonApprovedNoOps | MonitorKillSwitchEvents | MonitorAppliedDelta | MonitorRequestCount | MonitorTraceCompleteness | AlertOnAny{requestCap}+Requests | AlertOnWindowDurationExceeded";
        var abortConditions = new List<string>
        {
            "AnySafetyBoundaryViolation",
            "KillSwitchTriggered",
            "AppliedDelta > 0 detected",
            "ConfigPatchWritten detected",
            "RuntimeActivation detected",
            "RuntimeSwitchAllowed changed to true",
            "P15Regression observed",
            "RuntimeChangeGateFailed",
            "AuthorizationExpired",
            "FinalApprovalRevoked",
            "OperatorAbortIssued",
        };

        var configPatchPreviewLocked = !string.IsNullOrWhiteSpace(configPatchPreview);

        var dryRunPresent = dryRun is not null;
        var formalRetrievalAllowed = dryRunPresent && dryRun!.FormalRetrievalAllowed;
        var runtimeSwitchAllowed = dryRunPresent && dryRun!.RuntimeSwitchAllowed;
        var formalPackageWritten = dryRunPresent && dryRun!.FormalPackageWritten;
        var packingPolicyChanged = dryRunPresent && dryRun!.PackingPolicyChanged;
        var packageOutputChanged = dryRunPresent && dryRun!.PackageOutputChanged;
        var vectorBindingChanged = dryRunPresent && dryRun!.VectorStoreBindingChanged;
        var globalDefaultOn = dryRunPresent && dryRun!.GlobalDefaultOn;
        var noRuntimeMutationInvariant = !formalRetrievalAllowed && !runtimeSwitchAllowed && !formalPackageWritten
            && !packingPolicyChanged && !packageOutputChanged && !vectorBindingChanged && !globalDefaultOn;

        if (formalRetrievalAllowed) blocked.Add("SafetyBoundaryFormalRetrievalAllowed");
        if (runtimeSwitchAllowed) blocked.Add("SafetyBoundaryRuntimeSwitchAllowed");
        if (formalPackageWritten) blocked.Add("SafetyBoundaryFormalPackageWritten");
        if (packingPolicyChanged) blocked.Add("SafetyBoundaryPackingPolicyChanged");
        if (packageOutputChanged) blocked.Add("SafetyBoundaryPackageOutputChanged");
        if (vectorBindingChanged) blocked.Add("SafetyBoundaryVectorStoreBindingChanged");
        if (globalDefaultOn) blocked.Add("SafetyBoundaryGlobalDefaultOn");

        if (!configPatchPreviewLocked) blocked.Add("ConfigPatchPreviewNotLocked");
        if (abortConditions.Count < 3) blocked.Add("AbortConditionsInsufficient");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var planPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && planPassed;

        diag.Add($"stage={stage}");
        diag.Add($"freezePassed={freezePassed}");
        diag.Add($"noOpPassed={noOpPassed}");
        diag.Add($"finalApprovalPresent={finalApprovalPresent}");
        diag.Add($"configPatchPreviewLocked={configPatchPreviewLocked}");
        diag.Add($"configPatchWritten=false runtimeActivation=false");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"planPassed={planPassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewLiveActivationExecutionPlanReport
        {
            OperationId = $"arsp-exec-plan-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            PlanPassed = planPassed,
            GatePassed = gatePassed,
            Recommendation = planPassed
                ? ScopedRuntimePreviewLiveActivationExecutionPlanRecommendations.ReadyForScopedRuntimePreviewLiveActivation
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = planPassed ? "ScopedRuntimePreviewLiveActivation" : "KeepPreviewOnly",

            ExecutionPlanId = executionPlanId,
            FinalApprovalId = finalApprovalId,
            ApprovedScopes = approvedScopes,
            ActivationWindowDurationMinutes = windowDuration,
            ActivationWindowRequestCap = requestCap,
            RollbackTriggerPolicy = rollbackPolicy,
            KillSwitchTriggerPolicy = killSwitchPolicy,
            ConfigPatchPreview = configPatchPreview,
            RuntimeSwitchPreview = runtimeSwitchPreview,
            MonitoringPlan = monitoringPlan,
            AbortConditions = abortConditions,

            ConfigPatchWritten = false,
            RuntimeActivation = false,
            ConfigPatchPreviewLocked = configPatchPreviewLocked,

            V7FreezePassed = freezePassed,
            V7NoOpExecutionPassed = noOpPassed,
            V7PreflightPassed = preflightPassed,
            V7DryRunPassed = dryRunPassed,
            P15GatePassed = p15GatePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,
            FinalApprovalPresent = finalApprovalPresent,

            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
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

    public static string BuildMarkdown(string title, ScopedRuntimePreviewLiveActivationExecutionPlanReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- PlanPassed: `{r.PlanPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Execution Plan");
        b.AppendLine($"- ExecutionPlanId: `{r.ExecutionPlanId}`");
        b.AppendLine($"- FinalApprovalId: `{r.FinalApprovalId[..Math.Min(12, r.FinalApprovalId.Length)]}...`");
        b.AppendLine($"- ApprovedScopes: `{string.Join(", ", r.ApprovedScopes)}`");
        b.AppendLine($"- ActivationWindowDurationMinutes: `{r.ActivationWindowDurationMinutes}`");
        b.AppendLine($"- ActivationWindowRequestCap: `{r.ActivationWindowRequestCap}`");
        b.AppendLine($"- RollbackTriggerPolicy: `{r.RollbackTriggerPolicy}`");
        b.AppendLine($"- KillSwitchTriggerPolicy: `{r.KillSwitchTriggerPolicy}`");
        b.AppendLine($"- ConfigPatchPreview: `{r.ConfigPatchPreview}`");
        b.AppendLine($"- RuntimeSwitchPreview: `{r.RuntimeSwitchPreview}`");
        b.AppendLine($"- MonitoringPlan: `{r.MonitoringPlan}`");
        b.AppendLine($"- ConfigPatchPreviewLocked: `{r.ConfigPatchPreviewLocked}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        AppendList(b, "Abort Conditions", r.AbortConditions);
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- V7FreezePassed: `{r.V7FreezePassed}`");
        b.AppendLine($"- V7NoOpExecutionPassed: `{r.V7NoOpExecutionPassed}`");
        b.AppendLine($"- FinalApprovalPresent: `{r.FinalApprovalPresent}`");
        b.AppendLine($"- P15GatePassed: `{r.P15GatePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{r.RuntimeChangeGatePassed}`");

        b.AppendLine();
        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");

        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        AppendList(b, "Blocked Reasons", r.BlockedReasons);
        AppendList(b, "Diagnostics", r.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.12 scoped runtime preview live activation execution plan。冻结 config patch preview，生成最终执行计划。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Freeze", StringComparison.OrdinalIgnoreCase) || r.Contains("Missing", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionPlanRecommendations.BlockedByMissingFreeze;
        if (blocked.Any(static r => r.Contains("NoOp", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionPlanRecommendations.BlockedByMissingNoOpExecution;
        if (blocked.Any(static r => r.Contains("FinalApproval", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionPlanRecommendations.BlockedByFinalApprovalNotPresent;
        if (blocked.Any(static r => r.Contains("ConfigPatch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionPlanRecommendations.BlockedByConfigPatchPreviewNotLocked;
        if (blocked.Any(static r => r.Contains("Monitor", StringComparison.OrdinalIgnoreCase) || r.Contains("Abort", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionPlanRecommendations.BlockedByMonitoringPlanMissing;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationExecutionPlanRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewLiveActivationExecutionPlanRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
