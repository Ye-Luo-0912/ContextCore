using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionReadinessAuditRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Closeout", "ReadV7SummaryFreeze", "ReadV7Observation", "ReadV7Execution", "ReadV7Plan",
        "ReadP15Report", "ReadRuntimeChangeGate", "AuditV7CloseoutCompleteness",
        "AuditSafetyBoundaries", "AuditNoRuntimeMutation", "WriteAuditArtifactsOnly"
    ];

    private static readonly string[] FrozenForbiddenActions =
    [
        "GlobalDefaultOn", "FormalRetrievalEnable", "FormalPackageWrite", "PackingPolicyMutation",
        "PackageOutputMutation", "VectorStoreBindingMutation", "RuntimeSwitch", "RuntimeActivation",
        "RuntimeSwitchChanged", "WriteConfigPatch", "ApplyPreviewResult", "ChangeFormalSelectedSet",
        "MutateApprovedScopes", "BypassAudit", "OverrideAuditResult",
    ];

    public FormalRetrievalPromotionReadinessAuditReport RunAudit(
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeout,
        ScopedRuntimePreviewLiveActivationSummaryFreezeReport? summaryFreeze,
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        bool rtGatePassed, bool p15Passed,
        FormalRetrievalPromotionReadinessAuditOptions? options = null)
        => BuildReport("audit", false, closeout, summaryFreeze, observation, execution, plan, rtGatePassed, p15Passed, options);

    public FormalRetrievalPromotionReadinessAuditReport RunGate(
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeout,
        ScopedRuntimePreviewLiveActivationSummaryFreezeReport? summaryFreeze,
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        bool rtGatePassed, bool p15Passed,
        FormalRetrievalPromotionReadinessAuditOptions? options = null)
        => BuildReport("gate", true, closeout, summaryFreeze, observation, execution, plan, rtGatePassed, p15Passed, options);

    private static FormalRetrievalPromotionReadinessAuditReport BuildReport(
        string stage, bool isGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeout,
        ScopedRuntimePreviewLiveActivationSummaryFreezeReport? summaryFreeze,
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        bool rtGatePassed, bool p15Passed,
        FormalRetrievalPromotionReadinessAuditOptions? options)
    {
        options ??= new FormalRetrievalPromotionReadinessAuditOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;

        var closeoutPassed = closeout is not null && closeout.CloseoutPassed;

        if (!options.Enabled) blocked.Add("AuditDisabled");
        if (!closeoutPassed) blocked.Add("V7CloseoutMissingOrNotPassed");
        if (!rtGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var auditItems = new List<string>();

        var closeoutDisposition = closeout?.FinalDisposition ?? Array.Empty<string>();
        var formalRetrievalStillBlocked = closeoutDisposition.Contains("FormalRetrievalStillBlocked");
        var requiresSeparateGate = closeoutDisposition.Contains("RequiresSeparateFormalRetrievalPromotionGate");
        var observationSource = closeout?.ObservationSource ?? "";

        auditItems.Add($"V7CloseoutCompleted={closeoutPassed}");
        auditItems.Add($"FormalRetrievalStillBlocked={formalRetrievalStillBlocked}");
        auditItems.Add($"RequiresSeparateFormalRetrievalPromotionGate={requiresSeparateGate}");
        auditItems.Add($"ObservationSource={observationSource}");

        if (!formalRetrievalStillBlocked) blocked.Add("FormalRetrievalNotBlockedInCloseout");
        if (!requiresSeparateGate) blocked.Add("SeparatePromotionGateNotDeclared");

        var formalRetrievalAllowed = observation?.FormalRetrievalAllowed ?? false;
        var runtimeSwitchAllowed = observation?.RuntimeSwitchChanged ?? false;
        var formalPackageWritten = observation?.FormalPackageWritten ?? false;
        var packageOutputChanged = observation?.PackageOutputChanged ?? false;
        var packingPolicyChanged = observation?.PackingPolicyChanged ?? false;
        var vectorBindingChanged = observation?.VectorStoreBindingChanged ?? false;
        var globalDefaultOn = observation?.GlobalDefaultOn ?? false;

        var noFormalRuntimeMutation = !formalRetrievalAllowed && !runtimeSwitchAllowed && !formalPackageWritten;
        var noPackageOutputMutation = !packageOutputChanged;
        var noPackingPolicyMutation = !packingPolicyChanged;
        var noVectorStoreBindingMutation = !vectorBindingChanged;
        var noRuntimeMutationInvariant = noFormalRuntimeMutation && noPackageOutputMutation && noPackingPolicyMutation && noVectorStoreBindingMutation && !globalDefaultOn;

        auditItems.Add($"NoFormalRuntimeMutation={noFormalRuntimeMutation}");
        auditItems.Add($"NoPackageOutputMutation={noPackageOutputMutation}");
        auditItems.Add($"NoPackingPolicyMutation={noPackingPolicyMutation}");
        auditItems.Add($"NoVectorStoreBindingMutation={noVectorStoreBindingMutation}");
        auditItems.Add($"P15GatePassed={p15Passed}");
        auditItems.Add($"RuntimeChangeGatePassed={rtGatePassed}");

        if (formalRetrievalAllowed) blocked.Add("SafetyBoundaryFormalRetrievalAllowed");
        if (runtimeSwitchAllowed) blocked.Add("SafetyBoundaryRuntimeSwitchAllowed");
        if (formalPackageWritten) blocked.Add("SafetyBoundaryFormalPackageWritten");
        if (packageOutputChanged) blocked.Add("SafetyBoundaryPackageOutputChanged");
        if (packingPolicyChanged) blocked.Add("SafetyBoundaryPackingPolicyChanged");
        if (vectorBindingChanged) blocked.Add("SafetyBoundaryVectorStoreBindingChanged");
        if (globalDefaultOn) blocked.Add("SafetyBoundaryGlobalDefaultOn");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var auditPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && auditPassed;

        diag.Add($"stage={stage}");
        diag.Add($"closeoutPassed={closeoutPassed}");
        diag.Add($"formalRetrievalStillBlocked={formalRetrievalStillBlocked}");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"auditPassed={auditPassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new FormalRetrievalPromotionReadinessAuditReport
        {
            OperationId = $"frp-audit-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            AuditPassed = auditPassed,
            GatePassed = gatePassed,
            Recommendation = auditPassed
                ? FormalRetrievalPromotionReadinessRecommendations.ReadyForFormalRetrievalPromotionPlan
                : FormalRetrievalPromotionReadinessRecommendations.BlockedByMissingCloseout,
            NextAllowedPhase = auditPassed ? "FormalRetrievalPromotionPlan" : "KeepPreviewOnly",

            V7CloseoutCompleted = closeoutPassed,
            FormalRetrievalStillBlocked = formalRetrievalStillBlocked,
            RequiresSeparateFormalRetrievalPromotionGate = requiresSeparateGate,
            ObservationSource = observationSource,
            NoFormalRuntimeMutation = noFormalRuntimeMutation,
            NoPackageOutputMutation = noPackageOutputMutation,
            NoPackingPolicyMutation = noPackingPolicyMutation,
            NoVectorStoreBindingMutation = noVectorStoreBindingMutation,
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

            AuditItems = auditItems,
            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionReadinessAuditReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- AuditPassed: `{r.AuditPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Audit Items");
        foreach (var a in r.AuditItems) b.AppendLine($"- `{a}`");
        b.AppendLine();

        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();
        b.AppendLine("V8.0 formal retrieval promotion readiness audit。不启用 formal retrieval。GatePassed=false is expected for non-gate artifact。");
        return b.ToString();
    }
}
