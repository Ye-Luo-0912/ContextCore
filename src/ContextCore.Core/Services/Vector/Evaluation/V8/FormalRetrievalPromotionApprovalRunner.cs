using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV8PlanGate", "ReadV8ReadinessGate", "ReadV7CloseoutGate",
        "ReadP15Report", "ReadRuntimeChangeGate", "ValidatePlanGatePassed",
        "ValidateUpstreamInvariants", "RequireExplicitApprovalIdentity",
        "RequireExplicitApprovalId", "BindApprovalRecord", "WriteApprovalArtifactsOnly"
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
        if (!planGatePassed) blocked.Add("PlanGateMissingOrNotPassed");
        if (!readinessGatePassed) blocked.Add("ReadinessGateMissingOrNotPassed");
        if (!closeoutGatePassed) blocked.Add("CloseoutGateMissingOrNotPassed");
        if (!rtGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var approvedScopes = planGate?.ApprovedScopes ?? Array.Empty<string>();

        if (isGate && !options.ExplicitlyProvided)
            blocked.Add("ManualApprovalMissing");
        else if (isGate && options.ExplicitlyProvided && string.IsNullOrWhiteSpace(options.ApprovedBy))
            blocked.Add("ApprovalIdentityMissing");

        if (isGate && !options.ApprovalIdExplicitlyProvided)
            blocked.Add("ApprovalIdMissing");
        else if (isGate && options.ApprovalIdExplicitlyProvided && string.IsNullOrWhiteSpace(options.ApprovalId))
            blocked.Add("ApprovalIdEmpty");

        var approvalGranted = isGate && options.ExplicitlyProvided && options.ApprovalIdExplicitlyProvided
            && !string.IsNullOrWhiteSpace(options.ApprovedBy) && !string.IsNullOrWhiteSpace(options.ApprovalId);
        var approvalIdentityBound = !string.IsNullOrWhiteSpace(options.ApprovedBy);
        var approvalScopeBound = approvedScopes.Count > 0;

        var approvalRequestId = $"frp-approval-{now:yyyyMMdd}-{Guid.NewGuid():N}";
        var sourcePlanGateOpId = planGate?.OperationId ?? "";

        var noRuntimeMutationInvariant = true;

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var approvalGatePassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && approvalGatePassed;

        diag.Add($"stage={stage}");
        diag.Add($"planGatePassed={planGatePassed}");
        diag.Add($"readinessGatePassed={readinessGatePassed}");
        diag.Add($"closeoutGatePassed={closeoutGatePassed}");
        diag.Add($"approvalGranted={approvalGranted}");
        diag.Add($"approvedBy={options.ApprovedBy}");
        diag.Add($"approvalIdProvided={options.ApprovalIdExplicitlyProvided}");
        diag.Add($"approvalGatePassed={approvalGatePassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact");

        return new FormalRetrievalPromotionApprovalReport
        {
            OperationId = $"frp-approval-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            ApprovalGatePassed = approvalGatePassed,
            GatePassed = gatePassed,
            Recommendation = approvalGatePassed
                ? FormalRetrievalPromotionApprovalRecommendations.ManualApprovalGranted
                : FormalRetrievalPromotionApprovalRecommendations.BlockedByManualApprovalMissing,
            NextAllowedPhase = approvalGatePassed ? "FormalRetrievalPromotionApproved" : "KeepPreviewOnly",

            ApprovalRequestId = approvalRequestId,
            SourcePromotionPlanId = planGate?.PromotionPlanId ?? "",
            SourcePromotionPlanGateOperationId = sourcePlanGateOpId,
            SourceReadinessGateOperationId = readinessGate?.OperationId ?? "",
            SourceCloseoutGateOperationId = closeoutGate?.OperationId ?? "",
            ApprovedScopes = approvedScopes,
            RequiredManualApproval = true,
            ApprovalGranted = approvalGranted,
            ApprovedBy = options.ApprovedBy,
            ApprovalId = options.ApprovalId,
            ApprovalTimestamp = approvalGranted ? now : DateTimeOffset.MinValue,
            ApprovalIdentityBound = approvalIdentityBound,
            ApprovalScopeBound = approvalScopeBound,
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
        b.AppendLine($"- ApprovalId: `{r.ApprovalId[..Math.Min(12, r.ApprovalId.Length)]}...`");
        b.AppendLine($"- ApprovalIdentityBound: `{r.ApprovalIdentityBound}`");
        b.AppendLine($"- ApprovalScopeBound: `{r.ApprovalScopeBound}`");
        b.AppendLine($"- RequiredManualApproval: `{r.RequiredManualApproval}`");
        b.AppendLine($"- FormalRetrievalStillBlocked: `{r.FormalRetrievalStillBlocked}`");
        b.AppendLine();
        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();
        b.AppendLine("V8.2 formal retrieval promotion approval gate。Manual approval contract。FormalRetrievalAllowed=false。");
        return b.ToString();
    }
}
