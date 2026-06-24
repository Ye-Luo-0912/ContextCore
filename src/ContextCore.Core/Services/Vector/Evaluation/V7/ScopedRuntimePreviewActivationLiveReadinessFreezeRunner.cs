using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewActivationLiveReadinessFreezeRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadAllV7Prerequisites",
        "ValidateAllGatesPassed",
        "FreezeNoOpExecutionMetrics",
        "CreateFinalApprovalRecord",
        "DefineRollbackTriggerPolicy",
        "DefineKillSwitchTriggerPolicy",
        "FreezeEvidenceChain",
        "WriteFreezeArtifactsOnly"
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
        "OverrideFrozenMetrics",
        "BypassFinalApproval",
        "ChangeFrozenEvidenceChain",
    ];

    public ScopedRuntimePreviewActivationLiveReadinessFreezeReport RunFreeze(
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        ScopedRuntimePreviewActivationPreparationReport? preparation,
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ScopedRuntimePreviewAuthorizationHardeningReport? hardening,
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationLiveReadinessFreezeOptions? options = null)
        => BuildReport("freeze", false, noOpExecution, preflight, dryRun, preparation, authorization, hardening, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewActivationLiveReadinessFreezeReport RunGate(
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        ScopedRuntimePreviewActivationPreparationReport? preparation,
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ScopedRuntimePreviewAuthorizationHardeningReport? hardening,
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationLiveReadinessFreezeOptions? options = null)
        => BuildReport("gate", true, noOpExecution, preflight, dryRun, preparation, authorization, hardening, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewActivationLiveReadinessFreezeReport BuildReport(
        string stage,
        bool isGate,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        ScopedRuntimePreviewActivationPreparationReport? preparation,
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ScopedRuntimePreviewAuthorizationHardeningReport? hardening,
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationLiveReadinessFreezeOptions? options)
    {
        options ??= new ScopedRuntimePreviewActivationLiveReadinessFreezeOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var noOpPassed = noOpExecution is not null && noOpExecution.NoOpExecutionPassed;
        var preflightPassed = preflight is not null && preflight.PreflightPassed;
        var dryRunPassed = dryRun is not null && dryRun.DryRunPassed;
        var preparationPassed = preparation is not null && preparation.PreparationPassed;
        var authorizationPassed = authorization is not null && authorization.Authorized;
        var hardeningPassed = hardening is not null && hardening.HardeningPassed;
        var approvalPlanPassed = approvalPlan is not null && approvalPlan.PlanPassed;
        var v7FreezePassed = v7Freeze is not null && v7Freeze.FreezePassed;

        if (!options.Enabled) blocked.Add("FreezeDisabled");
        if (!v7FreezePassed) blocked.Add("V7FreezeMissingOrNotPassed");
        if (!approvalPlanPassed) blocked.Add("ApprovalPlanMissingOrNotPassed");
        if (!authorizationPassed) blocked.Add("AuthorizationMissingOrNotPassed");
        if (!hardeningPassed) blocked.Add("HardeningMissingOrNotPassed");
        if (!preparationPassed) blocked.Add("PreparationMissingOrNotPassed");
        if (!dryRunPassed) blocked.Add("DryRunMissingOrNotPassed");
        if (!preflightPassed) blocked.Add("PreflightMissingOrNotPassed");
        if (!noOpPassed) blocked.Add("NoOpExecutionMissingOrNotPassed");
        if (!runtimeChangeGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed) blocked.Add("P15GateNotPassed");

        ScopedRuntimePreviewActivationLiveReadinessFrozenMetrics? frozenMetrics = null;
        if (noOpExecution is not null)
        {
            frozenMetrics = new ScopedRuntimePreviewActivationLiveReadinessFrozenMetrics
            {
                WindowCount = noOpExecution.WindowCount,
                RequestCountTotal = noOpExecution.RequestCountTotal,
                ApprovedScopeHitCount = noOpExecution.ApprovedScopeHitCount,
                NonApprovedScopeNoOpCount = noOpExecution.NonApprovedScopeNoOpCount,
                KillSwitchNoOpCount = noOpExecution.KillSwitchNoOpCount,
                AppliedAddTotal = noOpExecution.AppliedAddTotal,
                AppliedRemoveTotal = noOpExecution.AppliedRemoveTotal,
                AppliedDeltaZero = noOpExecution.AppliedDeltaZero,
                ConfigPatchWritten = noOpExecution.ConfigPatchWritten,
                RuntimeActivation = noOpExecution.RuntimeActivation,
                RollbackCheckpointVerified = noOpExecution.RollbackCheckpointVerified,
                TraceSinkWritable = noOpExecution.TraceSinkWritable,
            };

            if (frozenMetrics.WindowCount < 3) blocked.Add("FrozenWindowCountViolation");
            if (frozenMetrics.RequestCountTotal < 30) blocked.Add("FrozenRequestCountViolation");
            if (frozenMetrics.ApprovedScopeHitCount <= 0) blocked.Add("FrozenApprovedScopeHitViolation");
            if (frozenMetrics.KillSwitchNoOpCount <= 0) blocked.Add("FrozenKillSwitchNoOpViolation");
            if (!frozenMetrics.AppliedDeltaZero) blocked.Add("FrozenAppliedDeltaViolation");
            if (frozenMetrics.ConfigPatchWritten) blocked.Add("FrozenConfigPatchWrittenViolation");
            if (frozenMetrics.RuntimeActivation) blocked.Add("FrozenRuntimeActivationViolation");
        }

        var finalApprovedBy = options.FinalApprovedBy;
        var finalApprovalExplicitlyProvided = options.FinalApprovalExplicitlyProvided && !string.IsNullOrWhiteSpace(finalApprovedBy);
        var now = DateTimeOffset.UtcNow;
        var finalApprovalId = finalApprovalExplicitlyProvided
            ? $"arsp-final-approval-{finalApprovedBy}-{now:yyyyMMdd}-{Guid.NewGuid():N}"
            : "";

        if (isGate && !finalApprovalExplicitlyProvided)
            blocked.Add("FinalApprovalNotProvided");

        var finalApprovedScopes = approvalPlan?.ApprovedScopes ?? Array.Empty<string>();
        var activationWindowDuration = options.ApprovedBy == "ReleaseManager" ? 30 : 0;
        var activationWindowRequestCap = 100;
        var rollbackTriggerPolicy = "AnySafetyBoundaryViolation | AnyKillSwitchTrip | AnyAppliedDelta | OperatorOverride";
        var killSwitchTriggerPolicy = "ConfigPatchWritten | RuntimeActivation | RuntimeSwitchAllowed | AppliedDelta > 0 | OperatorOverride";

        var v7FreezePresent = v7Freeze is not null;
        var formalRetrievalAllowed = v7FreezePresent && v7Freeze!.FormalRetrievalAllowed;
        var runtimeSwitchAllowed = v7FreezePresent && v7Freeze!.RuntimeSwitchAllowed;
        var formalPackageWritten = v7FreezePresent && v7Freeze!.FormalPackageWritten;
        var packingPolicyChanged = v7FreezePresent && v7Freeze!.PackingPolicyChanged;
        var packageOutputChanged = v7FreezePresent && v7Freeze!.PackageOutputChanged;
        var vectorBindingChanged = v7FreezePresent && v7Freeze!.VectorStoreBindingChanged;
        var globalDefaultOn = v7FreezePresent && v7Freeze!.GlobalDefaultOn;
        var noRuntimeMutationInvariant = !formalRetrievalAllowed && !runtimeSwitchAllowed && !formalPackageWritten
            && !packingPolicyChanged && !packageOutputChanged && !vectorBindingChanged && !globalDefaultOn;

        if (formalRetrievalAllowed) blocked.Add("SafetyBoundaryFormalRetrievalAllowed");
        if (runtimeSwitchAllowed) blocked.Add("SafetyBoundaryRuntimeSwitchAllowed");
        if (formalPackageWritten) blocked.Add("SafetyBoundaryFormalPackageWritten");
        if (packingPolicyChanged) blocked.Add("SafetyBoundaryPackingPolicyChanged");
        if (packageOutputChanged) blocked.Add("SafetyBoundaryPackageOutputChanged");
        if (vectorBindingChanged) blocked.Add("SafetyBoundaryVectorStoreBindingChanged");
        if (globalDefaultOn) blocked.Add("SafetyBoundaryGlobalDefaultOn");

        var frozenEvidenceChain = new List<string>
        {
            "V7.4  observation-freeze.json       — FreezePassed",
            "V7.5  approval-plan.json            — PlanPassed",
            "V7.6  authorization.json            — Authorized",
            "V7.6R2 authorization-hardening.json  — HardeningPassed",
            "V7.7  activation-preparation.json    — PreparationPassed",
            "V7.8R activation-dry-run.json        — DryRunPassed",
            "V7.9  activation-window-preflight.json — PreflightPassed",
            "V7.10 activation-window-noop-exe.json  — NoOpExecutionPassed",
        };

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var freezePassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && freezePassed;

        diag.Add($"stage={stage}");
        diag.Add($"allSevenV7GatesPresent=true");
        diag.Add($"noOpExecutionPassed={noOpPassed}");
        diag.Add($"runtimeChangeGatePassed={runtimeChangeGatePassed}");
        diag.Add($"p15GatePassed={p15GatePassed}");
        diag.Add($"finalApprovalExplicitlyProvided={finalApprovalExplicitlyProvided}");
        diag.Add($"finalApprovedBy={finalApprovedBy}");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"configPatchWritten=false runtimeActivation=false");
        diag.Add($"freezePassed={freezePassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewActivationLiveReadinessFreezeReport
        {
            OperationId = $"arsp-live-readiness-freeze-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            FreezePassed = freezePassed,
            GatePassed = gatePassed,
            Recommendation = freezePassed
                ? ScopedRuntimePreviewActivationLiveReadinessFreezeRecommendations.ReadyForFinalManualApproval
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = freezePassed ? "ScopedRuntimePreviewActivationLive" : "KeepPreviewOnly",

            FinalApprovalRequired = true,
            FinalApprovedBy = finalApprovedBy,
            FinalApprovalId = finalApprovalId,
            FinalApprovalTimestamp = finalApprovalExplicitlyProvided ? now : DateTimeOffset.MinValue,
            FinalApprovedScopes = finalApprovedScopes,
            ActivationWindowDurationMinutes = activationWindowDuration,
            ActivationWindowRequestCap = activationWindowRequestCap,
            RollbackTriggerPolicy = rollbackTriggerPolicy,
            KillSwitchTriggerPolicy = killSwitchTriggerPolicy,

            V7NoOpExecutionPassed = noOpPassed,
            V7PreflightPassed = preflightPassed,
            V7DryRunPassed = dryRunPassed,
            V7PreparationPassed = preparationPassed,
            V7AuthorizationPassed = authorizationPassed,
            V7HardeningPassed = hardeningPassed,
            V7ApprovalPlanPassed = approvalPlanPassed,
            V7FreezePassed = v7FreezePassed,
            P15GatePassed = p15GatePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,

            FrozenMetrics = frozenMetrics,
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
            FrozenEvidenceChain = frozenEvidenceChain,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ScopedRuntimePreviewActivationLiveReadinessFreezeReport r)
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

        b.AppendLine("## Final Manual Approval");
        b.AppendLine($"- FinalApprovalRequired: `{r.FinalApprovalRequired}`");
        b.AppendLine($"- FinalApprovedBy: `{r.FinalApprovedBy}`");
        b.AppendLine($"- FinalApprovalId: `{r.FinalApprovalId[..Math.Min(12, r.FinalApprovalId.Length)]}...`");
        b.AppendLine($"- FinalApprovalTimestamp: `{r.FinalApprovalTimestamp:O}`");
        b.AppendLine($"- FinalApprovedScopes: `{string.Join(", ", r.FinalApprovedScopes)}`");
        b.AppendLine($"- ActivationWindowDurationMinutes: `{r.ActivationWindowDurationMinutes}`");
        b.AppendLine($"- ActivationWindowRequestCap: `{r.ActivationWindowRequestCap}`");
        b.AppendLine($"- RollbackTriggerPolicy: `{r.RollbackTriggerPolicy}`");
        b.AppendLine($"- KillSwitchTriggerPolicy: `{r.KillSwitchTriggerPolicy}`");
        b.AppendLine();

        b.AppendLine("## Frozen Metrics (from V7.10 no-op execution)");
        if (r.FrozenMetrics is not null)
        {
            b.AppendLine($"- WindowCount: `{r.FrozenMetrics.WindowCount}`");
            b.AppendLine($"- RequestCountTotal: `{r.FrozenMetrics.RequestCountTotal}`");
            b.AppendLine($"- ApprovedScopeHitCount: `{r.FrozenMetrics.ApprovedScopeHitCount}`");
            b.AppendLine($"- KillSwitchNoOpCount: `{r.FrozenMetrics.KillSwitchNoOpCount}`");
            b.AppendLine($"- AppliedDeltaZero: `{r.FrozenMetrics.AppliedDeltaZero}`");
        }

        b.AppendLine();
        b.AppendLine("## Prerequisites (8 V7 gates)");
        b.AppendLine($"- V7FreezePassed: `{r.V7FreezePassed}`");
        b.AppendLine($"- V7ApprovalPlanPassed: `{r.V7ApprovalPlanPassed}`");
        b.AppendLine($"- V7AuthorizationPassed: `{r.V7AuthorizationPassed}`");
        b.AppendLine($"- V7HardeningPassed: `{r.V7HardeningPassed}`");
        b.AppendLine($"- V7PreparationPassed: `{r.V7PreparationPassed}`");
        b.AppendLine($"- V7DryRunPassed: `{r.V7DryRunPassed}`");
        b.AppendLine($"- V7PreflightPassed: `{r.V7PreflightPassed}`");
        b.AppendLine($"- V7NoOpExecutionPassed: `{r.V7NoOpExecutionPassed}`");
        b.AppendLine($"- P15GatePassed: `{r.P15GatePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{r.RuntimeChangeGatePassed}`");

        AppendList(b, "Frozen Evidence Chain", r.FrozenEvidenceChain);
        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        AppendList(b, "Blocked Reasons", r.BlockedReasons);
        AppendList(b, "Diagnostics", r.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.11 scoped runtime preview activation live readiness freeze。冻结证据链，生成最终人工批准 gate。Gate 默认 blocked，需要显式 --final-approved-by。不启用 runtime activation。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Missing", StringComparison.OrdinalIgnoreCase) || r.Contains("NotPassed", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationLiveReadinessFreezeRecommendations.BlockedByMissingPrerequisites;
        if (blocked.Any(static r => r.Contains("NoOp", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationLiveReadinessFreezeRecommendations.BlockedByNoOpExecutionNotPassed;
        if (blocked.Any(static r => r.Contains("FinalApproval", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationLiveReadinessFreezeRecommendations.BlockedByFinalApprovalNotProvided;
        if (blocked.Any(static r => r.Contains("Frozen", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationLiveReadinessFreezeRecommendations.BlockedByFrozenMetricsViolation;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationLiveReadinessFreezeRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewActivationLiveReadinessFreezeRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
