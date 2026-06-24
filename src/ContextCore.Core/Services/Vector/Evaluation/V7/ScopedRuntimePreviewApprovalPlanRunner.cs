using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewApprovalPlanRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Freeze",
        "ReadV7Hardening",
        "ReadV6Freeze",
        "ReadOptFFreeze",
        "ReadRuntimeChangeGate",
        "ReadP15Report",
        "DefineApprovalAuthority",
        "DefineApprovedScopes",
        "DefineValidityWindow",
        "DefineRevocationMechanism",
        "DefineKillSwitchPlan",
        "DefineRollbackPlan",
        "DefineTraceRetentionPolicy",
        "WriteApprovalPlanArtifactsOnly",
        "ValidateSafetyBoundaries"
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
        "WriteFormalPackageToOutput",
        "MutationOfApprovedScopes",
        "MutationOfApprovalPlan",
        "ChangeFrozenSafetyBoundaries"
    ];

    public ScopedRuntimePreviewApprovalPlanReport RunPlan(
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        ControlledAppliedMergeRuntimePreviewObservationHardeningReport? v7Hardening,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewApprovalPlanOptions? options = null)
        => BuildReport("plan", false, v7Freeze, v7Hardening, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewApprovalPlanReport RunGate(
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        ControlledAppliedMergeRuntimePreviewObservationHardeningReport? v7Hardening,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewApprovalPlanOptions? options = null)
        => BuildReport("gate", true, v7Freeze, v7Hardening, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewApprovalPlanReport BuildReport(
        string stage,
        bool isGate,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        ControlledAppliedMergeRuntimePreviewObservationHardeningReport? v7Hardening,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewApprovalPlanOptions? options)
    {
        options ??= new ScopedRuntimePreviewApprovalPlanOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var v7FreezePassed = v7Freeze is not null && v7Freeze.FreezePassed;
        var v7HardeningPassed = v7Hardening is not null && v7Hardening.HardeningPassed;

        if (!options.Enabled)
            blocked.Add("ApprovalPlanDisabled");

        if (v7Freeze is null || !v7FreezePassed)
            blocked.Add("V7FreezeMissingOrNotPassed");
        if (v7Hardening is null || !v7HardeningPassed)
            blocked.Add("V7HardeningMissingOrNotPassed");
        if (!runtimeChangeGatePassed)
            blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed)
            blocked.Add("P15GateNotPassed");

        var authority = options.ApprovalAuthority;
        var approvers = options.AuthorizedApprovers.Where(static a => !string.IsNullOrWhiteSpace(a)).ToList();
        var scopes = options.ApprovedScopes.Where(static s => !string.IsNullOrWhiteSpace(s)).ToList();
        var validityDays = options.ValidityDurationDays;
        var killSwitchResponseSec = options.KillSwitchResponseTimeSeconds;
        var rollbackMaxMin = options.RollbackMaxDurationMinutes;
        var traceRetentionDays = options.TraceRetentionDays;

        var now = DateTimeOffset.UtcNow;
        var validityNotBefore = now;
        var validityNotAfter = now.AddDays(validityDays);
        var validityWithinBounds = validityDays > 0 && validityDays <= 365;

        var approvalPlanId = $"arsp-ap-{now:yyyyMMdd}-{Guid.NewGuid():N}";

        if (string.IsNullOrWhiteSpace(authority))
            blocked.Add("ApprovalAuthorityNotDefined");
        if (approvers.Count == 0)
            blocked.Add("AuthorizedApproversEmpty");
        if (scopes.Count == 0)
            blocked.Add("ApprovedScopesEmpty");
        if (!validityWithinBounds)
            blocked.Add("ValidityNotWithinBounds");
        if (validityDays <= 0)
            blocked.Add("ValidityDurationInvalid");

        var revocationMechanism = "MajorityVoteByAuthorizedApprovers";
        var revocationRequiresMajority = true;
        var revocationTriggers = new List<string>
        {
            "SafetyBoundaryViolation",
            "RuntimeSwitchDetected",
            "FormalPackageWritten",
            "FormalRetrievalEnabled",
            "ObservationWindowThresholdBreach",
            "P15Regression",
            "RuntimeChangeGateFailure",
            "EmergencyEscalation",
        };
        if (revocationTriggers.Count == 0)
            blocked.Add("RevocationTriggersEmpty");

        var emergencyContact = "release-manager@contextcore.local";

        var killSwitchPlan = $"RevertToPreviewOnlyWithin{options.KillSwitchResponseTimeSeconds}s";
        var killSwitchAction = "RollbackAllScopedPreviewToPreviewOnly";
        var killSwitchTested = v7Hardening is not null && v7Hardening.HardeningPassed;
        var killSwitchConfigured = true;

        if (!killSwitchTested)
            blocked.Add("KillSwitchNotTested");
        else if (!killSwitchConfigured)
            blocked.Add("KillSwitchNotConfigured");

        var rollbackPlan = $"RollbackAllScopedPreviewWithin{rollbackMaxMin}Minutes";
        var rollbackVerified = v7Hardening is not null && v7Hardening.HardeningPassed;
        var rollbackCheckpointStrategy = "FullPreviewStateSnapshotBeforeAnyApply";
        var rollbackConfigured = true;

        if (!rollbackVerified)
            blocked.Add("RollbackNotVerified");
        else if (!rollbackConfigured)
            blocked.Add("RollbackNotConfigured");

        var traceRetentionPolicy = $"RetainAllPreviewTracesFor{traceRetentionDays}Days";
        var traceStoragePath = "vector/v7/runtime-preview-trace.jsonl";
        var traceRetentionConfigured = traceRetentionDays >= 30;
        if (!traceRetentionConfigured)
            blocked.Add("TraceRetentionInsufficient");

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

        var planPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && planPassed;

        diag.Add($"stage={stage}");
        diag.Add($"v7FreezePassed={v7FreezePassed}");
        diag.Add($"v7HardeningPassed={v7HardeningPassed}");
        diag.Add($"runtimeChangeGatePassed={runtimeChangeGatePassed}");
        diag.Add($"p15GatePassed={p15GatePassed}");
        diag.Add($"authority={authority}");
        diag.Add($"approvers={approvers.Count}");
        diag.Add($"scopes={scopes.Count}");
        diag.Add($"validityDays={validityDays} withinBounds={validityWithinBounds}");
        diag.Add($"revocationMechanism={revocationMechanism} triggers={revocationTriggers.Count}");
        diag.Add($"killSwitchConfigured={killSwitchConfigured} tested={killSwitchTested}");
        diag.Add($"rollbackConfigured={rollbackConfigured} verified={rollbackVerified}");
        diag.Add($"traceRetentionDays={traceRetentionDays} configured={traceRetentionConfigured}");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"planPassed={planPassed} gatePassed={gatePassed}");
        if (!isGate)
            diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewApprovalPlanReport
        {
            OperationId = $"scoped-runtime-preview-approval-plan-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            PlanPassed = planPassed,
            GatePassed = gatePassed,
            Recommendation = planPassed
                ? ScopedRuntimePreviewApprovalPlanRecommendations.ReadyForScopedRuntimePreviewAuthorization
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = planPassed
                ? "ScopedRuntimePreviewAuthorizationGathering"
                : "KeepPreviewOnly",

            ApprovalPlanId = approvalPlanId,
            ApprovalAuthority = authority,
            AuthorizedApprovers = approvers,
            ApprovedScopes = scopes,
            ValidityNotBefore = validityNotBefore,
            ValidityNotAfter = validityNotAfter,
            ValidityDurationDays = validityDays,
            ValidityWithinBounds = validityWithinBounds,

            RevocationMechanism = revocationMechanism,
            RevocationRequiresMajority = revocationRequiresMajority,
            RevocationTriggers = revocationTriggers,
            EmergencyRevocationContact = emergencyContact,

            KillSwitchPlan = killSwitchPlan,
            KillSwitchAction = killSwitchAction,
            KillSwitchResponseTimeSeconds = killSwitchResponseSec,
            KillSwitchTested = killSwitchTested,
            KillSwitchConfigured = killSwitchConfigured,

            RollbackPlan = rollbackPlan,
            RollbackMaxDurationMinutes = rollbackMaxMin,
            RollbackVerified = rollbackVerified,
            RollbackCheckpointStrategy = rollbackCheckpointStrategy,
            RollbackConfigured = rollbackConfigured,

            TraceRetentionPolicy = traceRetentionPolicy,
            TraceRetentionDays = traceRetentionDays,
            TraceStoragePath = traceStoragePath,
            TraceRetentionConfigured = traceRetentionConfigured,

            V7FreezePassed = v7FreezePassed,
            V7HardeningPassed = v7HardeningPassed,
            V6FreezePassed = v7FreezePresent && v7Freeze!.V6FreezePassed,
            OptFreezePassed = v7FreezePresent && v7Freeze!.OptFreezePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,
            P15GatePassed = p15GatePassed,

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

    public static string BuildMarkdown(string title, ScopedRuntimePreviewApprovalPlanReport r)
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

        b.AppendLine("## Approval Plan");
        b.AppendLine($"- ApprovalPlanId: `{r.ApprovalPlanId}`");
        b.AppendLine($"- ApprovalAuthority: `{r.ApprovalAuthority}`");
        b.AppendLine($"- AuthorizedApprovers: `{string.Join(", ", r.AuthorizedApprovers)}`");
        b.AppendLine($"- ApprovedScopes: `{string.Join(", ", r.ApprovedScopes)}`");
        b.AppendLine($"- ValidityNotBefore: `{r.ValidityNotBefore:O}`");
        b.AppendLine($"- ValidityNotAfter: `{r.ValidityNotAfter:O}`");
        b.AppendLine($"- ValidityDurationDays: `{r.ValidityDurationDays}`");
        b.AppendLine($"- ValidityWithinBounds: `{r.ValidityWithinBounds}`");
        b.AppendLine();

        b.AppendLine("## Revocation Mechanism");
        b.AppendLine($"- RevocationMechanism: `{r.RevocationMechanism}`");
        b.AppendLine($"- RevocationRequiresMajority: `{r.RevocationRequiresMajority}`");
        b.AppendLine($"- EmergencyRevocationContact: `{r.EmergencyRevocationContact}`");
        AppendList(b, "Revocation Triggers", r.RevocationTriggers);
        b.AppendLine();

        b.AppendLine("## Kill Switch");
        b.AppendLine($"- KillSwitchPlan: `{r.KillSwitchPlan}`");
        b.AppendLine($"- KillSwitchAction: `{r.KillSwitchAction}`");
        b.AppendLine($"- KillSwitchResponseTimeSeconds: `{r.KillSwitchResponseTimeSeconds}`");
        b.AppendLine($"- KillSwitchTested: `{r.KillSwitchTested}`");
        b.AppendLine($"- KillSwitchConfigured: `{r.KillSwitchConfigured}`");
        b.AppendLine();

        b.AppendLine("## Rollback");
        b.AppendLine($"- RollbackPlan: `{r.RollbackPlan}`");
        b.AppendLine($"- RollbackMaxDurationMinutes: `{r.RollbackMaxDurationMinutes}`");
        b.AppendLine($"- RollbackVerified: `{r.RollbackVerified}`");
        b.AppendLine($"- RollbackCheckpointStrategy: `{r.RollbackCheckpointStrategy}`");
        b.AppendLine($"- RollbackConfigured: `{r.RollbackConfigured}`");
        b.AppendLine();

        b.AppendLine("## Trace Retention");
        b.AppendLine($"- TraceRetentionPolicy: `{r.TraceRetentionPolicy}`");
        b.AppendLine($"- TraceRetentionDays: `{r.TraceRetentionDays}`");
        b.AppendLine($"- TraceStoragePath: `{r.TraceStoragePath}`");
        b.AppendLine($"- TraceRetentionConfigured: `{r.TraceRetentionConfigured}`");
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- V7FreezePassed: `{r.V7FreezePassed}`");
        b.AppendLine($"- V7HardeningPassed: `{r.V7HardeningPassed}`");
        b.AppendLine($"- V6FreezePassed: `{r.V6FreezePassed}`");
        b.AppendLine($"- OptFreezePassed: `{r.OptFreezePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{r.RuntimeChangeGatePassed}`");
        b.AppendLine($"- P15GatePassed: `{r.P15GatePassed}`");
        b.AppendLine();

        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
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
        b.AppendLine("V7.5 scoped runtime preview approval plan. 明确审批主体、scope、有效期、撤销机制、kill switch、rollback、trace retention。不启用 runtime activation。不切 runtime switch。不写 formal package。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Freeze", StringComparison.OrdinalIgnoreCase) || r.Contains("Hardening", StringComparison.OrdinalIgnoreCase) || r.Contains("Missing", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewApprovalPlanRecommendations.BlockedByMissingFreeze;
        if (blocked.Any(static r => r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewApprovalPlanRecommendations.BlockedByKillSwitchUnavailable;
        if (blocked.Any(static r => r.Contains("Rollback", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewApprovalPlanRecommendations.BlockedByRollbackUnavailable;
        if (blocked.Any(static r => r.Contains("Revocation", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewApprovalPlanRecommendations.BlockedByRevocationUndefined;
        if (blocked.Any(static r => r.Contains("Trace", StringComparison.OrdinalIgnoreCase) || r.Contains("Retention", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewApprovalPlanRecommendations.BlockedByTraceRetentionUnconfigured;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewApprovalPlanRecommendations.BlockedBySafetyBoundaryViolation;
        if (blocked.Any(static r => r.Contains("Authority", StringComparison.OrdinalIgnoreCase) || r.Contains("Scope", StringComparison.OrdinalIgnoreCase) || r.Contains("Validity", StringComparison.OrdinalIgnoreCase) || r.Contains("Approver", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewApprovalPlanRecommendations.BlockedByApprovalPlanIncomplete;
        return ScopedRuntimePreviewApprovalPlanRecommendations.KeepPreviewOnly;
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
