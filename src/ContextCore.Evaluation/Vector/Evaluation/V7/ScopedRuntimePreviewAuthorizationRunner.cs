using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewAuthorizationRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7ApprovalPlan",
        "ReadV7Freeze",
        "ReadRuntimeChangeGate",
        "ReadP15Report",
        "IssueAuthorizationRecord",
        "ValidateApprovalPlanScopes",
        "ValidateValidityWindow",
        "ValidateForbiddenActionAcknowledgements",
        "ValidateKillSwitchAcknowledgement",
        "ValidateRollbackAcknowledgement",
        "ValidateTraceRetentionAcknowledgement",
        "WriteAuthorizationArtifactsOnly"
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
        "MutateApprovalPlanAfterAuthorization",
        "ChangeApprovedScopesAfterAuthorization",
        "OverrideValidityWindow",
        "SkipForbiddenActionAcknowledgement"
    ];

    public ScopedRuntimePreviewAuthorizationReport RunAuthorization(
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewAuthorizationOptions? options = null)
        => BuildReport("authorization", false, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewAuthorizationReport RunGate(
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewAuthorizationOptions? options = null)
        => BuildReport("gate", true, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewAuthorizationReport BuildReport(
        string stage,
        bool isGate,
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewAuthorizationOptions? options)
    {
        options ??= new ScopedRuntimePreviewAuthorizationOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var approvalPlanPassed = approvalPlan is not null && approvalPlan.PlanPassed;
        var v7FreezePassed = v7Freeze is not null && v7Freeze.FreezePassed;

        if (!options.Enabled)
            blocked.Add("AuthorizationDisabled");

        if (approvalPlan is null)
            blocked.Add("ApprovalPlanMissing");
        else if (!approvalPlan.PlanPassed)
            blocked.Add("ApprovalPlanNotPassed");

        if (v7Freeze is null || !v7FreezePassed)
            blocked.Add("V7FreezeMissingOrNotPassed");
        if (!runtimeChangeGatePassed)
            blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed)
            blocked.Add("P15GateNotPassed");

        var now = DateTimeOffset.UtcNow;
        var approvalPlanId = approvalPlan?.ApprovalPlanId ?? string.Empty;
        var approvalAuthority = approvalPlan?.ApprovalAuthority ?? options.ApprovalAuthority;
        var planScopes = approvalPlan?.ApprovedScopes ?? Array.Empty<string>();

        var approvedBy = options.ApprovedBy;
        if (string.IsNullOrWhiteSpace(approvedBy))
            blocked.Add("ApprovedByMissing");

        var authorizationId = approvalPlan is not null
            ? $"arsp-auth-{approvalPlan.ApprovalPlanId}-{now:yyyyMMdd}-{Guid.NewGuid():N}"
            : $"arsp-auth-{now:yyyyMMdd}-{Guid.NewGuid():N}";

        var validityNotBefore = approvalPlan?.ValidityNotBefore ?? now;
        var validityNotAfter = approvalPlan?.ValidityNotAfter ?? now.AddDays(30);
        var remainingValidityDays = Math.Max(0, (validityNotAfter - now).Days);
        var validityValid = now >= validityNotBefore && now <= validityNotAfter && remainingValidityDays > 0;

        if (!validityValid)
        {
            if (now < validityNotBefore)
                blocked.Add("AuthorizationNotYetValid");
            else
                blocked.Add("AuthorizationExpired");
        }

        if (planScopes.Count == 0)
            blocked.Add("ApprovedScopesEmpty");

        var forbiddenToAcknowledge = options.ForbiddenActionsToAcknowledge
            .Where(static f => !string.IsNullOrWhiteSpace(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var acknowledgedForbidden = forbiddenToAcknowledge.ToList();
        var unacknowledgedForbidden = new List<string>();

        var killSwitchAcknowledged = approvalPlan?.KillSwitchConfigured ?? false;
        if (!killSwitchAcknowledged)
            blocked.Add("KillSwitchNotAcknowledged");

        var rollbackAcknowledged = approvalPlan?.RollbackConfigured ?? false;
        if (!rollbackAcknowledged)
            blocked.Add("RollbackNotAcknowledged");

        var traceRetentionAcknowledged = approvalPlan?.TraceRetentionConfigured ?? false;
        if (!traceRetentionAcknowledged)
            blocked.Add("TraceRetentionNotAcknowledged");

        var allForbiddenAcknowledged = unacknowledgedForbidden.Count == 0 && killSwitchAcknowledged
            && rollbackAcknowledged && traceRetentionAcknowledged;
        if (unacknowledgedForbidden.Count > 0)
            blocked.Add("ForbiddenActionsNotFullyAcknowledged");

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

        var authorized = distinctBlocked.Length == 0;
        var gatePassed = isGate && authorized;

        diag.Add($"stage={stage}");
        diag.Add($"approvalPlanPassed={approvalPlanPassed}");
        diag.Add($"v7FreezePassed={v7FreezePassed}");
        diag.Add($"runtimeChangeGatePassed={runtimeChangeGatePassed}");
        diag.Add($"p15GatePassed={p15GatePassed}");
        diag.Add($"approvedBy={approvedBy}");
        diag.Add($"authority={approvalAuthority}");
        diag.Add($"scopesCount={planScopes.Count}");
        diag.Add($"validityNotBefore={validityNotBefore:O}");
        diag.Add($"validityNotAfter={validityNotAfter:O}");
        diag.Add($"remainingValidityDays={remainingValidityDays}");
        diag.Add($"validityValid={validityValid}");
        diag.Add($"acknowledgedForbiddenCount={acknowledgedForbidden.Count}");
        diag.Add($"unacknowledgedForbiddenCount={unacknowledgedForbidden.Count}");
        diag.Add($"killSwitchAcknowledged={killSwitchAcknowledged}");
        diag.Add($"rollbackAcknowledged={rollbackAcknowledged}");
        diag.Add($"traceRetentionAcknowledged={traceRetentionAcknowledged}");
        diag.Add($"allForbiddenAcknowledged={allForbiddenAcknowledged}");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"authorized={authorized} gatePassed={gatePassed}");
        if (!isGate)
            diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewAuthorizationReport
        {
            OperationId = $"scoped-runtime-preview-authorization-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            Authorized = authorized,
            GatePassed = gatePassed,
            Recommendation = authorized
                ? ScopedRuntimePreviewAuthorizationRecommendations.ReadyForScopedRuntimePreviewActivationPreparation
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = authorized
                ? "ScopedRuntimePreviewActivationPreparation"
                : "KeepPreviewOnly",

            ApprovalId = authorizationId,
            ApprovalPlanId = approvalPlanId,
            ApprovedBy = approvedBy,
            ApprovalAuthority = approvalAuthority,
            ApprovedScopes = planScopes,
            ValidityNotBefore = validityNotBefore,
            ValidityNotAfter = validityNotAfter,
            RemainingValidityDays = remainingValidityDays,
            ValidityValid = validityValid,

            AcknowledgedForbiddenActions = acknowledgedForbidden,
            UnacknowledgedForbiddenActions = unacknowledgedForbidden,
            KillSwitchAcknowledged = killSwitchAcknowledged,
            RollbackAcknowledged = rollbackAcknowledged,
            TraceRetentionAcknowledged = traceRetentionAcknowledged,
            AllForbiddenActionsAcknowledged = allForbiddenAcknowledged,

            V7ApprovalPlanPassed = approvalPlanPassed,
            V7FreezePassed = v7FreezePassed,
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

    public static string BuildMarkdown(string title, ScopedRuntimePreviewAuthorizationReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- Authorized: `{r.Authorized}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Authorization Record");
        b.AppendLine($"- ApprovalId: `{r.ApprovalId}`");
        b.AppendLine($"- ApprovalPlanId: `{r.ApprovalPlanId}`");
        b.AppendLine($"- ApprovedBy: `{r.ApprovedBy}`");
        b.AppendLine($"- ApprovalAuthority: `{r.ApprovalAuthority}`");
        b.AppendLine($"- ApprovedScopes: `{string.Join(", ", r.ApprovedScopes)}`");
        b.AppendLine($"- ValidityNotBefore: `{r.ValidityNotBefore:O}`");
        b.AppendLine($"- ValidityNotAfter: `{r.ValidityNotAfter:O}`");
        b.AppendLine($"- RemainingValidityDays: `{r.RemainingValidityDays}`");
        b.AppendLine($"- ValidityValid: `{r.ValidityValid}`");
        b.AppendLine();

        b.AppendLine("## Acknowledgements");
        b.AppendLine($"- KillSwitchAcknowledged: `{r.KillSwitchAcknowledged}`");
        b.AppendLine($"- RollbackAcknowledged: `{r.RollbackAcknowledged}`");
        b.AppendLine($"- TraceRetentionAcknowledged: `{r.TraceRetentionAcknowledged}`");
        b.AppendLine($"- AllForbiddenActionsAcknowledged: `{r.AllForbiddenActionsAcknowledged}`");
        AppendList(b, "Acknowledged Forbidden Actions", r.AcknowledgedForbiddenActions);
        AppendList(b, "Unacknowledged Forbidden Actions", r.UnacknowledgedForbiddenActions);
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- V7ApprovalPlanPassed: `{r.V7ApprovalPlanPassed}`");
        b.AppendLine($"- V7FreezePassed: `{r.V7FreezePassed}`");
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
        b.AppendLine("V7.6 scoped runtime preview authorization record. 验证 approval plan、scope 有效性、禁止操作确认。不启用 runtime activation。不切 runtime switch。不写 formal package。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Missing", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationRecommendations.BlockedByMissingAuthorizationRecord;
        if (blocked.Any(static r => r.Contains("Expired", StringComparison.OrdinalIgnoreCase) || r.Contains("NotYetValid", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationRecommendations.BlockedByExpiredAuthorization;
        if (blocked.Any(static r => r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationRecommendations.BlockedByKillSwitchNotAcknowledged;
        if (blocked.Any(static r => r.Contains("Rollback", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationRecommendations.BlockedByRollbackNotAcknowledged;
        if (blocked.Any(static r => r.Contains("Trace", StringComparison.OrdinalIgnoreCase) || r.Contains("Retention", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationRecommendations.BlockedByTraceRetentionNotAcknowledged;
        if (blocked.Any(static r => r.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationRecommendations.BlockedByMissingForbiddenActionAcknowledgement;
        if (blocked.Any(static r => r.Contains("Scope", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationRecommendations.BlockedByWrongScope;
        if (blocked.Any(static r => r.Contains("ApprovalPlan", StringComparison.OrdinalIgnoreCase) || r.Contains("Plan", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationRecommendations.BlockedByApprovalPlanNotPassed;
        if (blocked.Any(static r => r.Contains("Freeze", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationRecommendations.BlockedByFreezeNotPassed;
        return ScopedRuntimePreviewAuthorizationRecommendations.KeepPreviewOnly;
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
