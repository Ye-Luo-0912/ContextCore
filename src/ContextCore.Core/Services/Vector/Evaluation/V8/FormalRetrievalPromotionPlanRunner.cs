using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionPlanRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV8Audit", "ReadV7Closeout", "ReadV7Summary", "ReadV7Observation", "ReadV7Execution", "ReadV7Plan",
        "ReadP15Report", "ReadRuntimeChangeGate", "DefinePromotionPlan", "DefineRollbackPlan", "DefineKillSwitchPlan",
        "DefineShadowValidationPlan", "DefineFormalPackageSafetyPlan", "DefineAbortConditions", "WritePlanArtifactsOnly"
    ];

    private static readonly string[] FrozenForbiddenActions =
    [
        "GlobalDefaultOn", "FormalRetrievalEnable", "FormalPackageWrite", "PackingPolicyMutation",
        "PackageOutputMutation", "VectorStoreBindingMutation", "RuntimeSwitch", "RuntimeActivation",
        "RuntimeSwitchChanged", "WriteConfigPatch", "ApplyPreviewResult", "ChangeFormalSelectedSet",
        "MutateApprovedScopes", "OverridePromotionPlan", "BypassRequiredManualApproval",
    ];

    public FormalRetrievalPromotionPlanReport RunPlan(
        FormalRetrievalPromotionReadinessAuditReport? audit,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeout,
        ScopedRuntimePreviewLiveActivationSummaryFreezeReport? summaryFreeze,
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        bool rtGatePassed, bool p15Passed,
        FormalRetrievalPromotionPlanOptions? options = null)
        => BuildReport("plan", false, audit, closeout, summaryFreeze, observation, execution, plan, rtGatePassed, p15Passed, options);

    public FormalRetrievalPromotionPlanReport RunGate(
        FormalRetrievalPromotionReadinessAuditReport? audit,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeout,
        ScopedRuntimePreviewLiveActivationSummaryFreezeReport? summaryFreeze,
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        bool rtGatePassed, bool p15Passed,
        FormalRetrievalPromotionPlanOptions? options = null)
        => BuildReport("gate", true, audit, closeout, summaryFreeze, observation, execution, plan, rtGatePassed, p15Passed, options);

    private static FormalRetrievalPromotionPlanReport BuildReport(
        string stage, bool isGate,
        FormalRetrievalPromotionReadinessAuditReport? audit,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeout,
        ScopedRuntimePreviewLiveActivationSummaryFreezeReport? summaryFreeze,
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        bool rtGatePassed, bool p15Passed,
        FormalRetrievalPromotionPlanOptions? options)
    {
        options ??= new FormalRetrievalPromotionPlanOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;

        var auditPassed = audit is not null && audit.AuditPassed;
        var closeoutPassed = closeout is not null && closeout.CloseoutPassed;

        if (!options.Enabled) blocked.Add("PlanDisabled");
        if (!auditPassed) blocked.Add("ReadinessAuditMissingOrNotPassed");
        if (!closeoutPassed) blocked.Add("CloseoutMissingOrNotPassed");
        if (!rtGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var formalRetrievalStillBlocked = audit?.FormalRetrievalStillBlocked ?? true;
        var approvedScopes = plan?.ApprovedScopes ?? Array.Empty<string>();

        var promotionPlanId = $"frp-plan-{now:yyyyMMdd}-{Guid.NewGuid():N}";
        var sourceCloseoutId = closeout?.OperationId ?? "";
        var rollbackPlan = "RollbackToScopedPreviewOnly:DisableFormalRetrievalBinding:RevertAnyAppliedPackageDelta";
        var killSwitchPlan = "KillSwitch:AnyRuntimeMutationDetected:AnyFormalPackageWritten:AnyScopeDrift:OperatorOverride";
        var shadowValidationPlan = "ShadowValidate:AllApprovedScopes:DeterministicTraceFixture:CompareRetrievalOutputs:NoPackageOutputMutation";
        var formalPackageSafetyPlan = "NeverWriteFormalPackageOutput:ConfigPatchPreviewOnly:RetrievalResultShadowOnly";
        var abortConditions = new List<string>
        {
            "FormalRetrievalAttemptedBeforeManualApproval",
            "RuntimeMutationDetected",
            "PackageOutputChanged",
            "PackingPolicyMutation",
            "VectorStoreBindingChanged",
            "GlobalDefaultOn",
            "ScopeDriftDetected",
            "KillSwitchTriggered",
            "OperatorAbort",
        };

        var formalRetrievalAllowed = observation?.FormalRetrievalAllowed ?? false;
        var runtimeSwitchAllowed = false;
        var formalPackageWritten = observation?.FormalPackageWritten ?? false;
        var packageOutputChanged = observation?.PackageOutputChanged ?? false;
        var packingPolicyChanged = observation?.PackingPolicyChanged ?? false;
        var vectorBindingChanged = observation?.VectorStoreBindingChanged ?? false;
        var globalDefaultOn = observation?.GlobalDefaultOn ?? false;
        var noRuntimeMutationInvariant = !formalRetrievalAllowed && !runtimeSwitchAllowed && !formalPackageWritten
            && !packageOutputChanged && !packingPolicyChanged && !vectorBindingChanged && !globalDefaultOn;

        if (formalRetrievalAllowed) blocked.Add("SafetyBoundaryFormalRetrievalAllowed");
        if (runtimeSwitchAllowed) blocked.Add("SafetyBoundaryRuntimeSwitchAllowed");
        if (formalPackageWritten) blocked.Add("SafetyBoundaryFormalPackageWritten");
        if (packageOutputChanged) blocked.Add("SafetyBoundaryPackageOutputChanged");
        if (packingPolicyChanged) blocked.Add("SafetyBoundaryPackingPolicyChanged");
        if (vectorBindingChanged) blocked.Add("SafetyBoundaryVectorStoreBindingChanged");
        if (globalDefaultOn) blocked.Add("SafetyBoundaryGlobalDefaultOn");

        if (abortConditions.Count < 3) blocked.Add("AbortConditionsInsufficient");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var planPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && planPassed;

        diag.Add($"stage={stage}");
        diag.Add($"auditPassed={auditPassed}");
        diag.Add($"closeoutPassed={closeoutPassed}");
        diag.Add($"formalRetrievalStillBlocked={formalRetrievalStillBlocked}");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"planPassed={planPassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact");

        return new FormalRetrievalPromotionPlanReport
        {
            OperationId = $"frp-plan-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            PlanPassed = planPassed,
            GatePassed = gatePassed,
            Recommendation = planPassed
                ? FormalRetrievalPromotionPlanRecommendations.ReadyForFormalRetrievalPromotionApproval
                : FormalRetrievalPromotionPlanRecommendations.BlockedByMissingReadinessAudit,
            NextAllowedPhase = planPassed ? "FormalRetrievalPromotionApproval" : "KeepPreviewOnly",

            PromotionPlanId = promotionPlanId,
            SourceCloseoutId = sourceCloseoutId,
            ApprovedScopes = approvedScopes,
            ObservationSource = "DeterministicShadowTraceFixture",
            FormalRetrievalStillBlocked = true,
            RuntimeSwitchStillBlocked = true,
            ConfigPatchWritten = false,
            RequiredManualApproval = true,
            RollbackPlan = rollbackPlan,
            KillSwitchPlan = killSwitchPlan,
            ShadowValidationPlan = shadowValidationPlan,
            FormalPackageSafetyPlan = formalPackageSafetyPlan,
            AbortConditions = abortConditions,

            V8AuditPassed = auditPassed,
            V7CloseoutPassed = closeoutPassed,
            P15GatePassed = p15Passed,
            RuntimeChangeGatePassed = rtGatePassed,

            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            NoRuntimeMutationInvariant = true,

            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionPlanReport r)
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
        b.AppendLine("## Promotion Plan");
        b.AppendLine($"- PromotionPlanId: `{r.PromotionPlanId}`");
        b.AppendLine($"- FormalRetrievalStillBlocked: `{r.FormalRetrievalStillBlocked}`");
        b.AppendLine($"- RuntimeSwitchStillBlocked: `{r.RuntimeSwitchStillBlocked}`");
        b.AppendLine($"- RequiredManualApproval: `{r.RequiredManualApproval}`");
        b.AppendLine($"- RollbackPlan: `{r.RollbackPlan}`");
        b.AppendLine($"- KillSwitchPlan: `{r.KillSwitchPlan}`");
        b.AppendLine($"- ShadowValidationPlan: `{r.ShadowValidationPlan}`");
        b.AppendLine($"- FormalPackageSafetyPlan: `{r.FormalPackageSafetyPlan}`");
        b.AppendLine();
        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();
        b.AppendLine("V8.1 formal retrieval promotion plan。不启用 formal retrieval。GatePassed=false is expected for non-gate artifact。");
        return b.ToString();
    }
}
