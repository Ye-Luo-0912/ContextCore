using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewActivationPreparationRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Authorization",
        "ReadV7Hardening",
        "ReadV7ApprovalPlan",
        "ReadV7Freeze",
        "ReadRuntimeChangeGate",
        "ReadP15Report",
        "PrepareActivationContract",
        "DefineKillSwitchProbePlan",
        "DefineRollbackCheckpointPlan",
        "DefineTraceSinkPlan",
        "DefineConfigPatchPreview",
        "DefineObservationStartPlan",
        "DefineStopConditions",
        "ValidateScopesUnchanged",
        "WritePreparationArtifactsOnly"
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
        "OverrideValidityWindow",
        "SkipForbiddenActionAcknowledgement",
        "ChangeFrozenSafetyBoundaries"
    ];

    public ScopedRuntimePreviewActivationPreparationReport RunPreparation(
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ScopedRuntimePreviewAuthorizationHardeningReport? hardening,
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationPreparationOptions? options = null)
        => BuildReport("preparation", false, authorization, hardening, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewActivationPreparationReport RunGate(
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ScopedRuntimePreviewAuthorizationHardeningReport? hardening,
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationPreparationOptions? options = null)
        => BuildReport("gate", true, authorization, hardening, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewActivationPreparationReport BuildReport(
        string stage,
        bool isGate,
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ScopedRuntimePreviewAuthorizationHardeningReport? hardening,
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationPreparationOptions? options)
    {
        options ??= new ScopedRuntimePreviewActivationPreparationOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var authorizationPassed = authorization is not null && authorization.Authorized;
        var hardeningPassed = hardening is not null && hardening.HardeningPassed;
        var approvalPlanPassed = approvalPlan is not null && approvalPlan.PlanPassed;
        var v7FreezePassed = v7Freeze is not null && v7Freeze.FreezePassed;

        if (!options.Enabled)
            blocked.Add("PreparationDisabled");

        if (authorization is null || !authorizationPassed)
            blocked.Add("AuthorizationMissingOrNotPassed");
        if (hardening is null || !hardeningPassed)
            blocked.Add("HardeningMissingOrNotPassed");
        if (approvalPlan is null || !approvalPlanPassed)
            blocked.Add("ApprovalPlanMissingOrNotPassed");
        if (v7Freeze is null || !v7FreezePassed)
            blocked.Add("V7FreezeMissingOrNotPassed");
        if (!runtimeChangeGatePassed)
            blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed)
            blocked.Add("P15GateNotPassed");

        var approveAuthority = approvalPlan?.ApprovalAuthority ?? "ArchitectureReviewBoard";
        var authorizationId = authorization?.ApprovalId ?? "";
        var approvalPlanId = approvalPlan?.ApprovalPlanId ?? "";
        var planScopes = approvalPlan?.ApprovedScopes ?? Array.Empty<string>();
        var authScopes = authorization?.ApprovedScopes ?? Array.Empty<string>();

        var scopesUnchanged = planScopes.Count > 0
            && planScopes.Count == authScopes.Count
            && planScopes.All(s => authScopes.Any(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase)));
        if (!scopesUnchanged)
            blocked.Add("ApprovedScopesChanged");

        var validityNotBefore = authorization?.ValidityNotBefore ?? approvalPlan?.ValidityNotBefore ?? DateTimeOffset.UtcNow;
        var validityNotAfter = authorization?.ValidityNotAfter ?? approvalPlan?.ValidityNotAfter ?? DateTimeOffset.UtcNow.AddDays(30);
        var now = DateTimeOffset.UtcNow;
        var authorizationValid = now >= validityNotBefore && now <= validityNotAfter;
        if (!authorizationValid)
            blocked.Add("AuthorizationNotValid");

        var explicitApprovedByPresent = options.ExplicitlyProvided;
        if (!explicitApprovedByPresent)
            blocked.Add("ExplicitApprovedByNotPresent");

        var allForbiddenAcknowledged = hardening?.AllForbiddenAcknowledged ?? false;
        if (!allForbiddenAcknowledged)
            blocked.Add("ForbiddenActionsNotFullyAcknowledged");

        var killSwitchConfigured = approvalPlan?.KillSwitchConfigured ?? false;
        if (!killSwitchConfigured)
            blocked.Add("KillSwitchNotConfigured");

        var rollbackConfigured = approvalPlan?.RollbackConfigured ?? false;
        if (!rollbackConfigured)
            blocked.Add("RollbackNotConfigured");

        var traceRetentionConfigured = approvalPlan?.TraceRetentionConfigured ?? false;
        if (!traceRetentionConfigured)
            blocked.Add("TraceRetentionNotConfigured");

        var activationPreparationId = $"arsp-actprep-{now:yyyyMMdd}-{Guid.NewGuid():N}";
        var killSwitchProbePlan = $"ProbeKillSwitchBeforeEachObservation({approveAuthority})";
        var rollbackCheckpointPlan = "FullPreviewStateSnapshotBeforeAnyApply";
        var traceSinkPlan = $"vector/v7/activation-trace-{now:yyyyMMdd}-{now:HHmmss}.jsonl";
        var configPatchPreview = $"config/runtime-preview-v7-scoped.json (preview only, ConfigPatchWritten=false)";
        var observationStartPlan = $"StartObservation({options.MaxObservationsBeforeActivation} cycles, {options.ObservationIntervalSeconds}s intervals, max {options.MaxObservationDurationMinutes}min)";
        var stopConditions = new List<string>
        {
            "AnySafetyBoundaryViolationDetected",
            "KillSwitchTriggered",
            $"ObservationCountExceeded({options.MaxObservationsBeforeActivation})",
            "RunawayTokenDeltaDetected",
            "P15RegressionObserved",
            "RuntimeChangeGateFailure",
            "AuthorizationExpired",
            "ApprovedByRevoked",
            "EmergencyEscalationReceived",
            "OperatorManualStop",
        };

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

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var preparationPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && preparationPassed;

        diag.Add($"stage={stage}");
        diag.Add($"authorizationPassed={authorizationPassed}");
        diag.Add($"hardeningPassed={hardeningPassed}");
        diag.Add($"approvalPlanPassed={approvalPlanPassed}");
        diag.Add($"v7FreezePassed={v7FreezePassed}");
        diag.Add($"runtimeChangeGatePassed={runtimeChangeGatePassed}");
        diag.Add($"p15GatePassed={p15GatePassed}");
        diag.Add($"scopesUnchanged={scopesUnchanged}");
        diag.Add($"authorizationValid={authorizationValid}");
        diag.Add($"explicitApprovedByPresent={explicitApprovedByPresent}");
        diag.Add($"allForbiddenAcknowledged={allForbiddenAcknowledged}");
        diag.Add($"killSwitchConfigured={killSwitchConfigured}");
        diag.Add($"rollbackConfigured={rollbackConfigured}");
        diag.Add($"traceRetentionConfigured={traceRetentionConfigured}");
        diag.Add($"configPatchWritten=false");
        diag.Add($"runtimeActivation=false");
        diag.Add($"runtimeSwitchAllowed=false");
        diag.Add($"formalRetrievalAllowed={formalRetrievalAllowed}");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"preparationPassed={preparationPassed} gatePassed={gatePassed}");
        if (!isGate)
            diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewActivationPreparationReport
        {
            OperationId = $"scoped-runtime-preview-activation-prep-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            PreparationPassed = preparationPassed,
            GatePassed = gatePassed,
            Recommendation = preparationPassed
                ? ScopedRuntimePreviewActivationPreparationRecommendations.ReadyForScopedRuntimePreviewActivation
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = preparationPassed
                ? "ScopedRuntimePreviewActivation"
                : "KeepPreviewOnly",

            ActivationPreparationId = activationPreparationId,
            AuthorizationId = authorizationId,
            ApprovalPlanId = approvalPlanId,
            ApprovedScopes = planScopes,
            ValidityNotBefore = validityNotBefore,
            ValidityNotAfter = validityNotAfter,
            AuthorizationValid = authorizationValid,

            KillSwitchProbePlan = killSwitchProbePlan,
            KillSwitchConfigured = killSwitchConfigured,
            RollbackCheckpointPlan = rollbackCheckpointPlan,
            RollbackConfigured = rollbackConfigured,
            TraceSinkPlan = traceSinkPlan,
            TraceRetentionConfigured = traceRetentionConfigured,
            ConfigPatchPreview = configPatchPreview,
            ConfigPatchWritten = false,
            ObservationStartPlan = observationStartPlan,
            MaxObservationsBeforeActivation = options.MaxObservationsBeforeActivation,
            StopConditions = stopConditions,

            AuthorizationPassed = authorizationPassed,
            AuthorizationHardeningPassed = hardeningPassed,
            ApprovalPlanPassed = approvalPlanPassed,
            ObservationFreezePassed = v7FreezePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,
            P15GatePassed = p15GatePassed,

            ExplicitApprovedByPresent = explicitApprovedByPresent,
            AllForbiddenActionsAcknowledged = allForbiddenAcknowledged,
            ApprovedScopesUnchanged = scopesUnchanged,

            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            FormalPackageWritten = formalPackageWritten,
            RuntimeActivation = false,
            WriteConfigPatch = false,
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

    public static string BuildMarkdown(string title, ScopedRuntimePreviewActivationPreparationReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- PreparationPassed: `{r.PreparationPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Activation Contract");
        b.AppendLine($"- ActivationPreparationId: `{r.ActivationPreparationId}`");
        b.AppendLine($"- AuthorizationId: `{r.AuthorizationId[..Math.Min(12, r.AuthorizationId.Length)]}...`");
        b.AppendLine($"- ApprovalPlanId: `{r.ApprovalPlanId[..Math.Min(12, r.ApprovalPlanId.Length)]}...`");
        b.AppendLine($"- ApprovedScopes: `{string.Join(", ", r.ApprovedScopes)}`");
        b.AppendLine($"- ValidityNotBefore: `{r.ValidityNotBefore:O}`");
        b.AppendLine($"- ValidityNotAfter: `{r.ValidityNotAfter:O}`");
        b.AppendLine($"- AuthorizationValid: `{r.AuthorizationValid}`");
        b.AppendLine();

        b.AppendLine("## Activation Preparation");
        b.AppendLine($"- KillSwitchProbePlan: `{r.KillSwitchProbePlan}`");
        b.AppendLine($"- KillSwitchConfigured: `{r.KillSwitchConfigured}`");
        b.AppendLine($"- RollbackCheckpointPlan: `{r.RollbackCheckpointPlan}`");
        b.AppendLine($"- RollbackConfigured: `{r.RollbackConfigured}`");
        b.AppendLine($"- TraceSinkPlan: `{r.TraceSinkPlan}`");
        b.AppendLine($"- TraceRetentionConfigured: `{r.TraceRetentionConfigured}`");
        b.AppendLine($"- ConfigPatchPreview: `{r.ConfigPatchPreview}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- ObservationStartPlan: `{r.ObservationStartPlan}`");
        b.AppendLine($"- MaxObservationsBeforeActivation: `{r.MaxObservationsBeforeActivation}`");
        AppendList(b, "Stop Conditions", r.StopConditions);
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- AuthorizationPassed: `{r.AuthorizationPassed}`");
        b.AppendLine($"- AuthorizationHardeningPassed: `{r.AuthorizationHardeningPassed}`");
        b.AppendLine($"- ApprovalPlanPassed: `{r.ApprovalPlanPassed}`");
        b.AppendLine($"- ObservationFreezePassed: `{r.ObservationFreezePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{r.RuntimeChangeGatePassed}`");
        b.AppendLine($"- P15GatePassed: `{r.P15GatePassed}`");
        b.AppendLine($"- ExplicitApprovedByPresent: `{r.ExplicitApprovedByPresent}`");
        b.AppendLine($"- AllForbiddenActionsAcknowledged: `{r.AllForbiddenActionsAcknowledged}`");
        b.AppendLine($"- ApprovedScopesUnchanged: `{r.ApprovedScopesUnchanged}`");
        b.AppendLine();

        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine($"- WriteConfigPatch: `{r.WriteConfigPatch}`");
        b.AppendLine($"- PackingPolicyChanged: `{r.PackingPolicyChanged}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- GlobalDefaultOn: `{r.GlobalDefaultOn}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();

        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        AppendList(b, "Blocked Reasons", r.BlockedReasons);
        AppendList(b, "Diagnostics", r.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.7 scoped runtime preview activation preparation. 准备 activation contract 和 pre-activation artifacts。ConfigPatchWritten=false, RuntimeActivation=false。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Authorization", StringComparison.OrdinalIgnoreCase) && !r.Contains("NotValid", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedByMissingAuthorization;
        if (blocked.Any(static r => r.Contains("Hardening", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedByMissingHardening;
        if (blocked.Any(static r => r.Contains("ApprovalPlan", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedByMissingApprovalPlan;
        if (blocked.Any(static r => r.Contains("Freeze", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedByMissingFreeze;
        if (blocked.Any(static r => r.Contains("NotValid", StringComparison.OrdinalIgnoreCase) || r.Contains("Expired", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedByExpiredAuthorization;
        if (blocked.Any(static r => r.Contains("Scope", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedByScopeMismatch;
        if (blocked.Any(static r => r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedByKillSwitchUnavailable;
        if (blocked.Any(static r => r.Contains("Rollback", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedByRollbackUnavailable;
        if (blocked.Any(static r => r.Contains("Trace", StringComparison.OrdinalIgnoreCase) || r.Contains("Retention", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedByTraceRetentionUnconfigured;
        if (blocked.Any(static r => r.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedByForbiddenActionNotAcknowledged;
        if (blocked.Any(static r => r.Contains("ApprovedBy", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedByApprovedByMissing;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationPreparationRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewActivationPreparationRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            b.AppendLine("- (empty)");
            return;
        }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
