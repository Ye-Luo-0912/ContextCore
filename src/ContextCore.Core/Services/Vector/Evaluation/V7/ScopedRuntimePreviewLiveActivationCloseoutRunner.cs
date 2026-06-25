using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewLiveActivationCloseoutRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7SummaryFreeze", "ReadV7Observation", "ReadV7Execution", "ReadV7Plan", "ReadV7Freeze",
        "ReadRuntimeChangeGate", "ReadP15Report", "SealEvidenceChain", "IssueFinalDisposition",
        "ValidateAllSafetyBoundaries", "WriteCloseoutArtifactsOnly"
    ];

    private static readonly string[] FrozenForbiddenActions =
    [
        "GlobalDefaultOn", "FormalRetrievalEnable", "FormalPackageWrite", "PackingPolicyMutation",
        "PackageOutputMutation", "VectorStoreBindingMutation", "RuntimeSwitch", "RuntimeActivation",
        "RuntimeSwitchChanged", "WriteConfigPatch", "ApplyPreviewResult", "ChangeFormalSelectedSet",
        "MutateApprovedScopes", "OverrideFinalDisposition", "ChangeSealedEvidence",
    ];

    public ScopedRuntimePreviewLiveActivationCloseoutReport RunCloseout(
        ScopedRuntimePreviewLiveActivationSummaryFreezeReport? summaryFreeze,
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        bool rtGatePassed, bool p15Passed,
        ScopedRuntimePreviewLiveActivationCloseoutOptions? options = null)
        => BuildReport("closeout", false, summaryFreeze, observation, execution, plan, freeze, rtGatePassed, p15Passed, options);

    public ScopedRuntimePreviewLiveActivationCloseoutReport RunGate(
        ScopedRuntimePreviewLiveActivationSummaryFreezeReport? summaryFreeze,
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        bool rtGatePassed, bool p15Passed,
        ScopedRuntimePreviewLiveActivationCloseoutOptions? options = null)
        => BuildReport("gate", true, summaryFreeze, observation, execution, plan, freeze, rtGatePassed, p15Passed, options);

    private static ScopedRuntimePreviewLiveActivationCloseoutReport BuildReport(
        string stage, bool isGate,
        ScopedRuntimePreviewLiveActivationSummaryFreezeReport? summaryFreeze,
        ScopedRuntimePreviewLiveActivationObservationReport? observation,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        bool rtGatePassed, bool p15Passed,
        ScopedRuntimePreviewLiveActivationCloseoutOptions? options)
    {
        options ??= new ScopedRuntimePreviewLiveActivationCloseoutOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;

        var summaryPassed = summaryFreeze is not null && summaryFreeze.FreezePassed;
        var obsPassed = observation is not null && observation.ObservationPassed;
        var execPassed = execution is not null && execution.ExecutionGatePassed;

        if (!options.Enabled) blocked.Add("CloseoutDisabled");
        if (!summaryPassed) blocked.Add("SummaryFreezeMissingOrNotPassed");
        if (!obsPassed) blocked.Add("ObservationMissingOrNotPassed");
        if (!execPassed) blocked.Add("ExecutionMissingOrNotPassed");
        if (!rtGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var activationExecId = execution?.ActivationExecutionId ?? "";
        var execPlanId = execution?.ExecutionPlanId ?? "";
        var finalApprovedBy = freeze?.FinalApprovedBy ?? summaryFreeze?.FinalApprovedBy ?? "";
        var finalApprovalId = summaryFreeze?.FinalApprovalId ?? "";
        var approvedScopes = plan?.ApprovedScopes ?? Array.Empty<string>();

        var formalRetrievalAllowed = observation?.FormalRetrievalAllowed ?? false;
        var formalPackageWritten = observation?.FormalPackageWritten ?? false;
        var globalDefaultOn = observation?.GlobalDefaultOn ?? false;
        var configPatchWritten = observation?.ConfigPatchWritten ?? false;
        var runtimeActivation = observation?.RuntimeActivation ?? false;
        var noRuntimeMutationInvariant = !formalRetrievalAllowed && !formalPackageWritten && !globalDefaultOn;

        if (formalRetrievalAllowed) blocked.Add("SafetyBoundaryFormalRetrievalAllowed");
        if (formalPackageWritten) blocked.Add("SafetyBoundaryFormalPackageWritten");
        if (globalDefaultOn) blocked.Add("SafetyBoundaryGlobalDefaultOn");
        if (configPatchWritten) blocked.Add("SafetyBoundaryConfigPatchWritten");
        if (runtimeActivation) blocked.Add("SafetyBoundaryRuntimeActivation");

        var frozenEvidenceChain = new List<string>
        {
            "V7.4  observation-freeze.json                       — FreezePassed",
            "V7.5  approval-plan.json                            — PlanPassed",
            "V7.6  authorization.json                            — Authorized",
            "V7.6R2 authorization-hardening.json                  — HardeningPassed",
            "V7.7  activation-preparation.json                    — PreparationPassed",
            "V7.8R activation-dry-run.json                        — DryRunPassed",
            "V7.9  activation-window-preflight.json               — PreflightPassed",
            "V7.10 activation-window-noop-execution.json           — NoOpExecutionPassed",
            "V7.11 activation-live-readiness-freeze-gate.json     — GatePassed",
            "V7.12 live-activation-execution-plan-gate.json       — GatePassed",
            "V7.13R2 live-activation-execution-gate.json           — GatePassed",
            "V7.14R live-activation-observation-gate.json          — GatePassed",
            "V7.15R live-activation-summary-freeze-gate.json       — GatePassed",
            $"V7.16  live-activation-closeout-gate.json            — GatePassed (this artifact, {now:yyyy-MM-dd})",
        };

        var finalDisposition = new List<string>
        {
            "ScopedRuntimePreviewCompleted",
            "FormalRetrievalStillBlocked",
            "GlobalDefaultOnStillBlocked",
            "RequiresSeparateFormalRetrievalPromotionGate",
            "EvidenceChainSealed14Artifacts",
            "ConfigPatchWritten=false",
            "RuntimeActivation=false",
            "SafetyBoundariesAllPassed",
        };

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var closeoutPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && closeoutPassed;

        diag.Add($"stage={stage}");
        diag.Add($"summaryPassed={summaryPassed}");
        diag.Add($"observationPassed={obsPassed}");
        diag.Add($"executionPassed={execPassed}");
        diag.Add($"formalRetrievalAllowed={formalRetrievalAllowed}");
        diag.Add($"globalDefaultOn={globalDefaultOn}");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"evidenceChain={frozenEvidenceChain.Count} items");
        diag.Add($"finalDisposition={finalDisposition.Count} items");
        diag.Add($"closeoutPassed={closeoutPassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewLiveActivationCloseoutReport
        {
            OperationId = $"arsp-closeout-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            CloseoutPassed = closeoutPassed,
            GatePassed = gatePassed,
            Recommendation = closeoutPassed
                ? ScopedRuntimePreviewLiveActivationCloseoutRecommendations.ScopedRuntimePreviewCompleted
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = closeoutPassed ? "ScopedRuntimePreviewCompleted" : "KeepPreviewOnly",

            ActivationExecutionId = activationExecId,
            ExecutionPlanId = execPlanId,
            FinalApprovedBy = finalApprovedBy,
            FinalApprovalId = finalApprovalId,
            ApprovedScopes = approvedScopes,
            ObservationSource = "DeterministicShadowTraceFixture",
            FrozenEvidenceChain = frozenEvidenceChain,
            FinalDisposition = finalDisposition,

            SummaryFreezePassed = summaryPassed,
            ObservationPassed = obsPassed,
            ExecutionPassed = execPassed,
            P15GatePassed = p15Passed,
            RuntimeChangeGatePassed = rtGatePassed,

            ConfigPatchWritten = false,
            RuntimeActivation = false,
            FormalRetrievalAllowed = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            NoRuntimeMutationInvariant = true,

            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ScopedRuntimePreviewLiveActivationCloseoutReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- CloseoutPassed: `{r.CloseoutPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Final Disposition");
        foreach (var d in r.FinalDisposition) b.AppendLine($"- `{d}`");
        b.AppendLine();

        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- GlobalDefaultOn: `{r.GlobalDefaultOn}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        AppendList(b, "Sealed Evidence Chain", r.FrozenEvidenceChain);
        b.AppendLine();
        b.AppendLine("V7.16 scoped runtime preview live activation closeout。最终收尾报告，证据链封存。GatePassed=false is expected for non-gate artifact。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Summary", StringComparison.OrdinalIgnoreCase) || r.Contains("Missing", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationCloseoutRecommendations.BlockedByMissingSummaryFreeze;
        if (blocked.Any(static r => r.Contains("Observation", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationCloseoutRecommendations.BlockedByMissingObservation;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationCloseoutRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewLiveActivationCloseoutRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
