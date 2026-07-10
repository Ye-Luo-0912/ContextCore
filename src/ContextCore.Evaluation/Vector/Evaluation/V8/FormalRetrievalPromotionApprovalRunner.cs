using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV8PlanGate", "ReadV8ReadinessGate", "ReadV7CloseoutGate",
        "ReadP15Report", "ReadRuntimeChangeGate", "ValidateUpstreamInvariants",
        "ValidatePlanRequiredManualApproval", "ValidatePlanFormalRetrievalStillBlocked",
        "ValidateReadinessInvariants", "ValidateCloseoutInvariants",
        "RequireExplicitApprovalIdentity", "RequireExplicitApprovalId",
        "ValidateApprovalScopeSubset", "BindApprovalRecord", "WriteApprovalArtifactsOnly"
    ];

    private static readonly string[] FrozenForbiddenActions =
    [
        "GlobalDefaultOn", "FormalRetrievalEnable", "FormalPackageWrite", "PackingPolicyMutation",
        "PackageOutputMutation", "VectorStoreBindingMutation", "RuntimeSwitch", "RuntimeActivation",
        "WriteConfigPatch", "ApplyPreviewResult", "ChangeFormalSelectedSet",
        "MutateApprovedScopes", "AutoApproveWithoutIdentity", "BypassManualApproval",
        "OverrideApprovalRecord",
    ];

    public FormalRetrievalPromotionApprovalReport RunApproval(
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtGatePassed, bool p15Passed,
        FormalRetrievalPromotionApprovalOptions? options = null)
        => BuildReport("approval", false, planGate, readinessGate, closeoutGate, rtGatePassed, p15Passed, options);

    public FormalRetrievalPromotionApprovalReport RunGate(
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtGatePassed, bool p15Passed,
        FormalRetrievalPromotionApprovalOptions? options = null)
        => BuildReport("gate", true, planGate, readinessGate, closeoutGate, rtGatePassed, p15Passed, options);

    private static FormalRetrievalPromotionApprovalReport BuildReport(
        string stage, bool isGate,
        FormalRetrievalPromotionPlanReport? planGate,
        FormalRetrievalPromotionReadinessAuditReport? readinessGate,
        ScopedRuntimePreviewLiveActivationCloseoutReport? closeoutGate,
        bool rtGatePassed, bool p15Passed,
        FormalRetrievalPromotionApprovalOptions? options)
    {
        options ??= new FormalRetrievalPromotionApprovalOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;

        var planGatePassed = planGate is not null && planGate.GatePassed;
        var readinessGatePassed = readinessGate is not null && readinessGate.GatePassed;
        var closeoutGatePassed = closeoutGate is not null && closeoutGate.GatePassed;

        if (!options.Enabled) blocked.Add("ApprovalDisabled");
        if (!planGatePassed) blocked.Add("PlanGateNotPassed");
        if (!readinessGatePassed) blocked.Add("ReadinessGateNotPassed");
        if (!closeoutGatePassed) blocked.Add("CloseoutGateNotPassed");
        if (!rtGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var planRequiredManualApproval = planGate?.RequiredManualApproval ?? false;
        if (!planRequiredManualApproval) blocked.Add("PlanRequiredManualApprovalMissing");

        var planFormalRetrievalBlocked = planGate?.FormalRetrievalStillBlocked ?? false;
        if (!planFormalRetrievalBlocked) blocked.Add("PlanFormalRetrievalNotBlocked");

        var planRuntimeSwitchBlocked = planGate?.RuntimeSwitchStillBlocked ?? false;
        if (!planRuntimeSwitchBlocked) blocked.Add("PlanRuntimeSwitchNotBlocked");

        var planConfigWritten = planGate?.ConfigPatchWritten ?? false;
        if (planConfigWritten) blocked.Add("PlanConfigPatchWritten");

        var readinessFormalRetrievalBlocked = readinessGate?.FormalRetrievalStillBlocked ?? false;
        if (!readinessFormalRetrievalBlocked) blocked.Add("ReadinessFormalRetrievalNotBlocked");

        var readinessObservationSource = readinessGate?.ObservationSource ?? "";
        if (!string.Equals(readinessObservationSource, "DeterministicShadowTraceFixture", StringComparison.OrdinalIgnoreCase))
            blocked.Add("ReadinessObservationSourceMismatch");

        var readinessSeparateGate = readinessGate?.RequiresSeparateFormalRetrievalPromotionGate ?? false;
        if (!readinessSeparateGate) blocked.Add("ReadinessSeparatePromotionGateMissing");

        var readinessFtAllowed = readinessGate?.FormalRetrievalAllowed ?? false;
        if (readinessFtAllowed) blocked.Add("ReadinessSafetyBoundaryFormalRetrievalAllowed");

        var closeoutConfigWritten = closeoutGate?.ConfigPatchWritten ?? false;
        if (closeoutConfigWritten) blocked.Add("CloseoutConfigPatchWritten");

        var closeoutRtAct = closeoutGate?.RuntimeActivation ?? false;
        if (closeoutRtAct) blocked.Add("CloseoutRuntimeActivationDetected");

        var closeoutFtAllowed = closeoutGate?.FormalRetrievalAllowed ?? false;
        if (closeoutFtAllowed) blocked.Add("CloseoutFormalRetrievalAllowed");

        var approvedScopes = planGate?.ApprovedScopes ?? Array.Empty<string>();

        var hasApproval = options.ExplicitlyProvided && options.ApprovalIdExplicitlyProvided
            && !string.IsNullOrWhiteSpace(options.ApprovedBy) && !string.IsNullOrWhiteSpace(options.ApprovalId);

        if (!options.ExplicitlyProvided)
            blocked.Add("ManualApprovalMissing");
        else if (options.ExplicitlyProvided && string.IsNullOrWhiteSpace(options.ApprovedBy))
            blocked.Add("ApprovalIdentityMissing");

        if (!options.ApprovalIdExplicitlyProvided)
            blocked.Add("ApprovalIdMissing");
        else if (string.IsNullOrWhiteSpace(options.ApprovalId))
            blocked.Add("ApprovalIdEmpty");

        var approvalScopes = options.ApprovalScopes
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        var approvalScopeSubset = approvalScopes.Count == 0
            || (approvedScopes.Count > 0 && approvalScopes.All(s => approvedScopes.Any(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase))));

        if (approvalScopes.Count == 0)
            blocked.Add("ApprovalScopeMissing");
        else if (!approvalScopeSubset)
            blocked.Add("ApprovalScopeNotSubset");

        var approvalGranted = hasApproval;
        var approvalIdentityBound = hasApproval;

        var approvalRequestId = $"frp-approval-{now:yyyyMMdd}-{Guid.NewGuid():N}";

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var approvalGatePassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && approvalGatePassed;

        diag.Add($"stage={stage}");
        diag.Add($"planGatePassed={planGatePassed}");
        diag.Add($"readinessGatePassed={readinessGatePassed}");
        diag.Add($"closeoutGatePassed={closeoutGatePassed}");
        diag.Add($"planRequiredManualApproval={planRequiredManualApproval}");
        diag.Add($"planFormalRetrievalBlocked={planFormalRetrievalBlocked}");
        diag.Add($"readinessFormalRetrievalBlocked={readinessFormalRetrievalBlocked}");
        diag.Add($"readinessObservationSource={readinessObservationSource}");
        diag.Add($"readinessSeparateGate={readinessSeparateGate}");
        diag.Add($"hasApproval={hasApproval}");
        diag.Add($"approvalScopes={approvalScopes.Count}");
        diag.Add($"approvalScopeSubset={approvalScopeSubset}");
        diag.Add($"approvalGatePassed={approvalGatePassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact");

        return new FormalRetrievalPromotionApprovalReport
        {
            OperationId = $"frp-approval-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            ApprovalGatePassed = approvalGatePassed,
            GatePassed = gatePassed,
            Recommendation = hasApproval && approvalGatePassed
                ? FormalRetrievalPromotionApprovalRecommendations.ManualApprovalGranted
                : FormalRetrievalPromotionApprovalRecommendations.BlockedByManualApprovalMissing,
            NextAllowedPhase = hasApproval && approvalGatePassed ? "FormalRetrievalPromotionApproved" : "KeepPreviewOnly",

            ApprovalRequestId = approvalRequestId,
            SourcePromotionPlanId = planGate?.PromotionPlanId ?? "",
            SourcePromotionPlanGateOperationId = planGate?.OperationId ?? "",
            SourceReadinessGateOperationId = readinessGate?.OperationId ?? "",
            SourceCloseoutGateOperationId = closeoutGate?.OperationId ?? "",
            ApprovedScopes = approvedScopes,
            ApprovalScopes = approvalScopes,
            ApprovalScopeSubsetOfApprovedScopes = approvalScopeSubset,
            RequiredManualApproval = true,
            ApprovalGranted = approvalGranted,
            ApprovedBy = options.ApprovedBy,
            ApprovalId = options.ApprovalId,
            ApprovalTimestamp = approvalGranted ? now : DateTimeOffset.MinValue,
            ApprovalIdentityBound = approvalIdentityBound,
            ApprovalScopeBound = false,
            FormalRetrievalStillBlocked = true,
            RuntimeSwitchStillBlocked = true,
            ConfigPatchWritten = false,

            V8PlanGatePassed = planGatePassed,
            V8ReadinessGatePassed = readinessGatePassed,
            V7CloseoutGatePassed = closeoutGatePassed,
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

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- ApprovalGatePassed: `{r.ApprovalGatePassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();
        b.AppendLine("## Approval Record");
        b.AppendLine($"- ApprovalGranted: `{r.ApprovalGranted}`");
        b.AppendLine($"- ApprovedBy: `{r.ApprovedBy}`");
        var aid = r.ApprovalId;
        b.AppendLine($"- ApprovalId: `{(aid.Length > 12 ? aid[..12] : aid)}...`");
        b.AppendLine($"- ApprovalIdentityBound: `{r.ApprovalIdentityBound}`");
        AppendList(b, "Approval Scopes", r.ApprovalScopes);
        b.AppendLine($"- ApprovalScopeSubsetOfApprovedScopes: `{r.ApprovalScopeSubsetOfApprovedScopes}`");
        b.AppendLine();
        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();
        b.AppendLine("V8.2R formal retrieval promotion approval gate。Manual approval with scope validation。FormalRetrievalAllowed=false。");
        return b.ToString();
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
