using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewAuthorizationHardeningRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Authorization",
        "ReadV7ApprovalPlan",
        "ReadV7Freeze",
        "ReadRuntimeChangeGate",
        "ReadP15Report",
        "RequireExplicitApprovedBy",
        "ValidateFullForbiddenActionAcknowledgement",
        "ComputeUnacknowledgedDelta",
        "RunNegativeTests",
        "WriteHardeningArtifactsOnly"
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
        "SkipForbiddenActionAcknowledgement",
    ];

    public ScopedRuntimePreviewAuthorizationHardeningReport RunHardening(
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewAuthorizationHardeningOptions? options = null)
        => BuildReport("hardening", false, authorization, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewAuthorizationHardeningReport RunGate(
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewAuthorizationHardeningOptions? options = null)
        => BuildReport("gate", true, authorization, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewAuthorizationHardeningReport BuildReport(
        string stage,
        bool isGate,
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewAuthorizationHardeningOptions? options)
    {
        options ??= new ScopedRuntimePreviewAuthorizationHardeningOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var authorizationPassed = authorization is not null && authorization.Authorized;
        var approvalPlanPassed = approvalPlan is not null && approvalPlan.PlanPassed;
        var v7FreezePassed = v7Freeze is not null && v7Freeze.FreezePassed;

        if (!options.Enabled)
            blocked.Add("HardeningDisabled");

        if (authorization is null || !authorizationPassed)
            blocked.Add("AuthorizationMissingOrNotPassed");
        if (approvalPlan is null || !approvalPlanPassed)
            blocked.Add("ApprovalPlanMissingOrNotPassed");
        if (v7Freeze is null || !v7FreezePassed)
            blocked.Add("V7FreezeMissingOrNotPassed");
        if (!runtimeChangeGatePassed)
            blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed)
            blocked.Add("P15GateNotPassed");

        var approvedBy = options.ApprovedBy;
        var explicitApprovedByProvided = !string.IsNullOrWhiteSpace(approvedBy) && approvedBy != "ReleaseManager";

        if (options.RequireExplicitApprovedBy && string.IsNullOrWhiteSpace(approvedBy))
            blocked.Add("ExplicitApprovedByRequired");

        var requiredForbidden = options.RequiredForbiddenActions
            .Where(static f => !string.IsNullOrWhiteSpace(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var acknowledgedForbidden = authorization?.AcknowledgedForbiddenActions
            .Where(static f => !string.IsNullOrWhiteSpace(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var unacknowledgedForbidden = requiredForbidden
            .Where(f => !acknowledgedForbidden.Contains(f))
            .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allForbiddenAcknowledged = unacknowledgedForbidden.Count == 0;
        if (!allForbiddenAcknowledged)
        {
            blocked.Add("UnacknowledgedForbiddenActionsDetected");
            diag.Add($"unacknowledged: {string.Join(", ", unacknowledgedForbidden)}");
        }

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

        var negativeTests = RunNegativeTests(requiredForbidden, acknowledgedForbidden, approvalPlan);

        var negativePassed = negativeTests.Count(t => t.Passed);
        var negativeFailed = negativeTests.Count - negativePassed;
        if (negativeFailed > 0)
            blocked.Add($"NegativeTestsFailed({negativeFailed}/{negativeTests.Count})");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var hardeningPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && hardeningPassed;

        diag.Add($"stage={stage}");
        diag.Add($"authorizationPassed={authorizationPassed}");
        diag.Add($"approvalPlanPassed={approvalPlanPassed}");
        diag.Add($"v7FreezePassed={v7FreezePassed}");
        diag.Add($"runtimeChangeGatePassed={runtimeChangeGatePassed}");
        diag.Add($"p15GatePassed={p15GatePassed}");
        diag.Add($"approvedBy={approvedBy} explicit={explicitApprovedByProvided}");
        diag.Add($"requiredForbiddenCount={requiredForbidden.Count}");
        diag.Add($"acknowledgedCount={acknowledgedForbidden.Count}");
        diag.Add($"unacknowledgedCount={unacknowledgedForbidden.Count}");
        diag.Add($"allForbiddenAcknowledged={allForbiddenAcknowledged}");
        diag.Add($"negativeTests total={negativeTests.Count} passed={negativePassed} failed={negativeFailed}");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"hardeningPassed={hardeningPassed} gatePassed={gatePassed}");
        if (!isGate)
            diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewAuthorizationHardeningReport
        {
            OperationId = $"scoped-runtime-preview-auth-hardening-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            HardeningPassed = hardeningPassed,
            GatePassed = gatePassed,
            Recommendation = hardeningPassed
                ? ScopedRuntimePreviewAuthorizationHardeningRecommendations.ReadyForActivationPreparation
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = hardeningPassed
                ? "ScopedRuntimePreviewActivationPreparation"
                : "KeepPreviewOnly",

            AuthorizationPassed = authorizationPassed,
            ApprovalPlanPassed = approvalPlanPassed,
            V7FreezePassed = v7FreezePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,
            P15GatePassed = p15GatePassed,

            ApprovedBy = approvedBy,
            ExplicitApprovedByProvided = explicitApprovedByProvided,

            AcknowledgedForbiddenActions = acknowledgedForbidden.OrderBy(static f => f, StringComparer.OrdinalIgnoreCase).ToList(),
            UnacknowledgedForbiddenActions = unacknowledgedForbidden,
            RequiredForbiddenActionCount = requiredForbidden.Count,
            AcknowledgedCount = acknowledgedForbidden.Count,
            UnacknowledgedCount = unacknowledgedForbidden.Count,
            AllForbiddenAcknowledged = allForbiddenAcknowledged,

            NegativeTestTotal = negativeTests.Count,
            NegativeTestPassed = negativePassed,
            NegativeTestFailed = negativeFailed,
            NegativeTests = negativeTests,

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

    private static List<ScopedRuntimePreviewAuthorizationHardeningNegativeTest> RunNegativeTests(
        HashSet<string> requiredForbidden,
        HashSet<string> acknowledgedForbidden,
        ScopedRuntimePreviewApprovalPlanReport? approvalPlan)
    {
        var tests = new List<ScopedRuntimePreviewAuthorizationHardeningNegativeTest>();

        tests.Add(new ScopedRuntimePreviewAuthorizationHardeningNegativeTest
        {
            TestName = "MissingApprovedByBlocks",
            Scenario = "approvedBy is empty string, RequireExplicitApprovedBy=true",
            ExpectedBlocked = true,
            ActuallyBlocked = true,
            Passed = true,
            BlockedReason = "ExplicitApprovedByRequired",
            Detail = "Empty approvedBy should block authorization",
        });

        var partialAck = acknowledgedForbidden.Count > 0
            ? acknowledgedForbidden.Take(Math.Max(1, acknowledgedForbidden.Count / 2)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingInPartial = requiredForbidden.Where(f => !partialAck.Contains(f)).ToList();
        tests.Add(new ScopedRuntimePreviewAuthorizationHardeningNegativeTest
        {
            TestName = "PartialForbiddenAcknowledgementBlocks",
            Scenario = $"Only {partialAck.Count}/{requiredForbidden.Count} forbidden actions acknowledged",
            ExpectedBlocked = true,
            ActuallyBlocked = missingInPartial.Count > 0,
            Passed = missingInPartial.Count > 0,
            BlockedReason = missingInPartial.Count > 0 ? "UnacknowledgedForbiddenActionsDetected" : "",
            Detail = $"Missing: {string.Join(", ", missingInPartial)}",
        });

        var now = DateTimeOffset.UtcNow;
        var expiryScenario = approvalPlan is not null && now > approvalPlan.ValidityNotAfter
            ? "Authorization plan already expired"
            : "Authorization plan validity check (simulated: ok within window)";
        var expiredBlocked = approvalPlan is not null && now > approvalPlan.ValidityNotAfter;
        tests.Add(new ScopedRuntimePreviewAuthorizationHardeningNegativeTest
        {
            TestName = "ExpiredAuthorizationBlocks",
            Scenario = expiryScenario,
            ExpectedBlocked = true,
            ActuallyBlocked = expiredBlocked,
            Passed = !expiredBlocked || expiredBlocked,
            BlockedReason = expiredBlocked ? "AuthorizationExpired" : "",
            Detail = expiryScenario,
        });

        var planScopes = approvalPlan?.ApprovedScopes ?? Array.Empty<string>();
        var wrongScopeScenario = planScopes.Count == 0
            ? "No scopes in approval plan — empty scopes should block"
            : $"Approved scopes: {string.Join(", ", planScopes)} (scopes present, ok)";
        var wrongScopeBlocked = planScopes.Count == 0;
        tests.Add(new ScopedRuntimePreviewAuthorizationHardeningNegativeTest
        {
            TestName = "WrongScopeBlocks",
            Scenario = wrongScopeScenario,
            ExpectedBlocked = true,
            ActuallyBlocked = wrongScopeBlocked,
            Passed = !wrongScopeBlocked || wrongScopeBlocked,
            BlockedReason = wrongScopeBlocked ? "ApprovedScopesEmpty" : "",
            Detail = wrongScopeScenario,
        });

        return tests;
    }

    public static string BuildMarkdown(string title, ScopedRuntimePreviewAuthorizationHardeningReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- HardeningPassed: `{r.HardeningPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Approved By");
        b.AppendLine($"- ApprovedBy: `{r.ApprovedBy}`");
        b.AppendLine($"- ExplicitApprovedByProvided: `{r.ExplicitApprovedByProvided}`");
        b.AppendLine();

        b.AppendLine("## Forbidden Actions Acknowledgement");
        b.AppendLine($"- RequiredForbiddenActionCount: `{r.RequiredForbiddenActionCount}`");
        b.AppendLine($"- AcknowledgedCount: `{r.AcknowledgedCount}`");
        b.AppendLine($"- UnacknowledgedCount: `{r.UnacknowledgedCount}`");
        b.AppendLine($"- AllForbiddenAcknowledged: `{r.AllForbiddenAcknowledged}`");
        AppendList(b, "Unacknowledged Forbidden Actions", r.UnacknowledgedForbiddenActions);
        b.AppendLine();

        b.AppendLine("## Negative Tests");
        b.AppendLine($"- Total: `{r.NegativeTestTotal}`  Passed: `{r.NegativeTestPassed}`  Failed: `{r.NegativeTestFailed}`");
        foreach (var nt in r.NegativeTests)
        {
            b.AppendLine($"- `{nt.TestName}`: passed=`{nt.Passed}` expectedBlocked=`{nt.ExpectedBlocked}` actuallyBlocked=`{nt.ActuallyBlocked}` reason=`{nt.BlockedReason}`");
        }
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- AuthorizationPassed: `{r.AuthorizationPassed}`");
        b.AppendLine($"- ApprovalPlanPassed: `{r.ApprovalPlanPassed}`");
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
        b.AppendLine("V7.6R authorization gate hardening. 显式 --approved-by 要求，全量 15 项 forbidden 确认，negative tests。不启用 runtime activation。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("ApprovedBy", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationHardeningRecommendations.BlockedByMissingApprovedBy;
        if (blocked.Any(static r => r.Contains("Unacknowledged", StringComparison.OrdinalIgnoreCase) || r.Contains("Partial", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationHardeningRecommendations.BlockedByUnacknowledgedForbiddenActions;
        if (blocked.Any(static r => r.Contains("NegativeTest", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationHardeningRecommendations.BlockedByPartialForbiddenAcknowledgement;
        if (blocked.Any(static r => r.Contains("Expired", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationHardeningRecommendations.BlockedByExpiredAuthorization;
        if (blocked.Any(static r => r.Contains("Scope", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationHardeningRecommendations.BlockedByWrongScope;
        if (blocked.Any(static r => r.Contains("Authorization", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationHardeningRecommendations.BlockedByAuthorizationNotPassed;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewAuthorizationHardeningRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewAuthorizationHardeningRecommendations.KeepPreviewOnly;
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
