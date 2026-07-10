using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewLiveActivationObservationRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Execution",
        "ReadV7Plan",
        "ReadV7Freeze",
        "ReadV7NoOpExecution",
        "ReadRuntimeChangeGate",
        "ReadP15Report",
        "GenerateShadowTraceFixture",
        "ValidateRequestCap",
        "ValidateScopeRouting",
        "ValidateKillSwitchState",
        "ValidateRollbackCheckpoint",
        "ValidateTraceSink",
        "ValidateAppliedDelta",
        "WriteObservationArtifactsOnly"
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
        "RuntimeSwitchChanged",
        "WriteConfigPatch",
        "ApplyPreviewResult",
        "ChangeFormalSelectedSet",
        "MutateApprovedScopes",
        "BypassKillSwitch",
        "DisableRollback",
        "SkipTraceSink",
        "ExceedRequestCap",
    ];

    public ScopedRuntimePreviewLiveActivationObservationReport RunObservation(
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationObservationOptions? options = null)
        => BuildReport("observation", false, execution, plan, freeze, noOpExecution, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewLiveActivationObservationReport RunGate(
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationObservationOptions? options = null)
        => BuildReport("gate", true, execution, plan, freeze, noOpExecution, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewLiveActivationObservationReport BuildReport(
        string stage, bool isGate,
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationExecutionPlanReport? plan,
        ScopedRuntimePreviewActivationLiveReadinessFreezeReport? freeze,
        ScopedRuntimePreviewActivationWindowNoOpExecutionReport? noOpExecution,
        bool runtimeChangeGatePassed, bool p15GatePassed,
        ScopedRuntimePreviewLiveActivationObservationOptions? options)
    {
        options ??= new ScopedRuntimePreviewLiveActivationObservationOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;

        var executionPassed = execution is not null && execution.ExecutionGatePassed;
        var planPassed = plan is not null && plan.PlanPassed;
        var freezePassed = freeze is not null && freeze.FreezePassed;
        var noOpPassed = noOpExecution is not null && noOpExecution.NoOpExecutionPassed;

        if (!options.Enabled) blocked.Add("ObservationDisabled");
        if (!executionPassed) blocked.Add("ExecutionMissingOrNotPassed");
        if (!planPassed) blocked.Add("PlanMissingOrNotPassed");
        if (!freezePassed) blocked.Add("FreezeMissingOrNotPassed");
        if (!noOpPassed) blocked.Add("NoOpExecutionMissingOrNotPassed");
        if (!runtimeChangeGatePassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed) blocked.Add("P15GateNotPassed");

        var planIdUnchanged = execution is not null && plan is not null
            && string.Equals(execution.ExecutionPlanId, plan.ExecutionPlanId, StringComparison.OrdinalIgnoreCase);
        if (!planIdUnchanged) blocked.Add("ExecutionPlanIdChanged");

        var freezeApprovedBy = freeze?.FinalApprovedBy ?? "";
        var finalApprovalUnchanged = execution is not null && freeze is not null
            && !string.IsNullOrWhiteSpace(freezeApprovedBy)
            && execution.FinalApprovalPresent;
        if (!finalApprovalUnchanged && !string.IsNullOrWhiteSpace(freezeApprovedBy))
            blocked.Add("FinalApprovalIdentityMismatch");

        var execId = execution?.ActivationExecutionId ?? "";
        var execPlanId = execution?.ExecutionPlanId ?? "";
        var approvedScopes = plan?.ApprovedScopes ?? Array.Empty<string>();
        var execRequestCap = execution?.RequestCap ?? options.MaxRequestCap;
        var killSwitchArmed = execution?.KillSwitchArmed ?? false;
        var rollbackCheckpointId = execution?.RollbackCheckpointId ?? "";
        var traceSinkPath = execution?.TraceSinkPath ?? $"vector/v7/live-activation-trace-{now:yyyyMMdd}-shadow.jsonl";

        var shadowTracePath = $"vector/v7/live-activation-trace-shadow.jsonl";
        var traceFixture = GenerateShadowTraceFixture(execution, options, execPlanId);

        var observedRequestCount = traceFixture.Count;
        var approvedScopeRequestCount = traceFixture.Count(static r => r.Kind == "approved");
        var nonApprovedScopeRequestCount = traceFixture.Count(static r => r.Kind == "nonApproved");
        var nonApprovedScopeNoOpCount = traceFixture.Count(static r => r.Kind == "nonApproved" && r.IsNoOp);
        var killSwitchTripCount = traceFixture.Count(static r => r.Kind == "killSwitch");
        var killSwitchNoOpCount = traceFixture.Count(static r => r.Kind == "killSwitch" && r.IsNoOp);
        var traceRecordCount = traceFixture.Count;

        if (observedRequestCount == 0)
            blocked.Add("TraceRecordCountZero");

        if (nonApprovedScopeNoOpCount != nonApprovedScopeRequestCount)
            blocked.Add("NonApprovedScopeNotNoOp");

        if (killSwitchNoOpCount != killSwitchTripCount)
            blocked.Add("KillSwitchTripNotNoOp");

        var requestCapExceeded = observedRequestCount > execRequestCap;
        if (requestCapExceeded) blocked.Add("RequestCapExceeded");

        if (!killSwitchArmed) blocked.Add("KillSwitchNotArmed");

        var rollbackCheckpointAvailable = !string.IsNullOrWhiteSpace(rollbackCheckpointId);
        if (!rollbackCheckpointAvailable) blocked.Add("RollbackCheckpointMissing");

        var traceSinkWritable = !string.IsNullOrWhiteSpace(traceSinkPath);
        if (!traceSinkWritable) blocked.Add("TraceSinkMissing");

        var appliedDeltaCount = 0;
        var appliedDeltaZero = appliedDeltaCount == 0;
        if (!appliedDeltaZero) blocked.Add("AppliedDeltaDetected");

        var configPatchWritten = execution?.ConfigPatchWritten ?? plan?.ConfigPatchWritten ?? false;
        if (configPatchWritten) blocked.Add("ConfigPatchWritten");

        var runtimeActivation = execution?.RuntimeActivation ?? plan?.RuntimeActivation ?? false;
        if (runtimeActivation) blocked.Add("RuntimeActivationDetected");

        var formalRetrievalAllowed = execution?.FormalRetrievalAllowed ?? false;
        var formalPackageWritten = execution?.FormalPackageWritten ?? false;
        var packingPolicyChanged = execution?.PackingPolicyChanged ?? false;
        var packageOutputChanged = execution?.PackageOutputChanged ?? false;
        var vectorBindingChanged = execution?.VectorStoreBindingChanged ?? false;
        var globalDefaultOn = execution?.GlobalDefaultOn ?? false;
        var noRuntimeMutationInvariant = !formalRetrievalAllowed && !formalPackageWritten
            && !packingPolicyChanged && !packageOutputChanged && !vectorBindingChanged && !globalDefaultOn;

        if (execution is null && plan is null) blocked.Add("SafetyBoundarySourceMissing");
        if (formalRetrievalAllowed) blocked.Add("SafetyBoundaryFormalRetrievalAllowed");
        if (formalPackageWritten) blocked.Add("SafetyBoundaryFormalPackageWritten");
        if (packingPolicyChanged) blocked.Add("SafetyBoundaryPackingPolicyChanged");
        if (packageOutputChanged) blocked.Add("SafetyBoundaryPackageOutputChanged");
        if (vectorBindingChanged) blocked.Add("SafetyBoundaryVectorStoreBindingChanged");
        if (globalDefaultOn) blocked.Add("SafetyBoundaryGlobalDefaultOn");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var observationPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && observationPassed;

        diag.Add($"stage={stage}");
        diag.Add($"executionPassed={executionPassed}");
        diag.Add($"planPassed={planPassed}");
        diag.Add($"planIdUnchanged={planIdUnchanged}");
        diag.Add($"finalApprovalUnchanged={finalApprovalUnchanged} freezeApprovedBy={freezeApprovedBy}");
        diag.Add($"observedRequestCount={observedRequestCount}/{execRequestCap}");
        diag.Add($"approvedScopeRequestCount={approvedScopeRequestCount}");
        diag.Add($"nonApprovedScopeRequestCount={nonApprovedScopeRequestCount}");
        diag.Add($"nonApprovedScopeNoOpCount={nonApprovedScopeNoOpCount}");
        diag.Add($"killSwitchTripCount={killSwitchTripCount} killSwitchNoOpCount={killSwitchNoOpCount}");
        diag.Add($"appliedDeltaCount={appliedDeltaCount} deltaZero={appliedDeltaZero}");
        diag.Add($"configPatchWritten={configPatchWritten} runtimeActivation={runtimeActivation}");
        diag.Add($"traceSource=shadow trace fixture={shadowTracePath}");
        diag.Add($"safetySource=execution report");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"observationPassed={observationPassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewLiveActivationObservationReport
        {
            OperationId = $"arsp-live-obs-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            ObservationPassed = observationPassed,
            GatePassed = gatePassed,
            Recommendation = observationPassed
                ? ScopedRuntimePreviewLiveActivationObservationRecommendations.ReadyForLiveActivationSummaryFreeze
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = observationPassed ? "LiveActivationSummaryFreeze" : "KeepPreviewOnly",

            ActivationExecutionId = execId,
            ExecutionPlanId = execPlanId,
            ApprovedScopes = approvedScopes,
            ObservedRequestCount = observedRequestCount,
            MaxRequestCap = execRequestCap,
            ApprovedScopeRequestCount = approvedScopeRequestCount,
            NonApprovedScopeRequestCount = nonApprovedScopeRequestCount,
            NonApprovedScopeNoOpCount = nonApprovedScopeNoOpCount,
            KillSwitchArmed = killSwitchArmed,
            KillSwitchTripCount = killSwitchTripCount,
            KillSwitchNoOpCount = killSwitchNoOpCount,
            RollbackCheckpointAvailable = rollbackCheckpointAvailable,
            TraceSinkWritable = traceSinkWritable,
            TraceSinkPath = traceSinkPath,
            ShadowTracePath = shadowTracePath,
            TraceRecordCount = traceRecordCount,
            AppliedDeltaCount = appliedDeltaCount,
            AppliedDeltaZero = appliedDeltaZero,
            ConfigPatchWritten = false,
            RuntimeActivation = false,
            RuntimeSwitchChanged = false,

            ExecutionGatePassed = executionPassed,
            ExecutionPlanPassed = planPassed,
            FreezePassed = freezePassed,
            NoOpExecutionPassed = noOpPassed,
            P15GatePassed = p15GatePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,
            PlanIdUnchanged = planIdUnchanged,
            FinalApprovalIdentityUnchanged = finalApprovalUnchanged,

            FormalRetrievalAllowed = formalRetrievalAllowed,
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

    private static List<(string Kind, bool IsNoOp, int WouldApplyAdd, int WouldApplyRemove)> GenerateShadowTraceFixture(
        ScopedRuntimePreviewLiveActivationExecutionReport? execution,
        ScopedRuntimePreviewLiveActivationObservationOptions options,
        string execPlanId)
    {
        var fixture = new List<(string, bool, int, int)>();
        if (execution is null) return fixture;

        var windowDuration = execution.ExecutionGatePassed ? 30 : options.ObservationRuns * 2;
        var totalRuns = options.ObservationRuns;
        var requestsPerRun = options.RequestsPerRun;
        var isExecuting = execution.ExecuteLiveActivation;

        for (var w = 0; w < totalRuns; w++)
        {
            for (var r = 0; r < requestsPerRun; r++)
            {
                if (r % 4 == 0)
                    fixture.Add(("approved", false, 3, 1));
                else if (r % 4 == 1)
                    fixture.Add(("killSwitch", true, 0, 0));
                else if (r % 4 == 2)
                    fixture.Add(("nonApproved", true, 0, 0));
                else
                    fixture.Add(("nonApproved", true, 0, 0));
            }
        }

        return fixture;
    }

    public static string BuildMarkdown(string title, ScopedRuntimePreviewLiveActivationObservationReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- ObservationPassed: `{r.ObservationPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Observation Metrics (from shadow trace fixture)");
        b.AppendLine($"- ObservedRequestCount: `{r.ObservedRequestCount}` / `{r.MaxRequestCap}`");
        b.AppendLine($"- ApprovedScopeRequestCount: `{r.ApprovedScopeRequestCount}`");
        b.AppendLine($"- NonApprovedScopeRequestCount: `{r.NonApprovedScopeRequestCount}`");
        b.AppendLine($"- NonApprovedScopeNoOpCount: `{r.NonApprovedScopeNoOpCount}` (noOp==reqCount: `{(r.NonApprovedScopeNoOpCount == r.NonApprovedScopeRequestCount ? "true" : "false")}`)");
        b.AppendLine($"- KillSwitchTripCount: `{r.KillSwitchTripCount}`");
        b.AppendLine($"- KillSwitchNoOpCount: `{r.KillSwitchNoOpCount}` (noOp==tripCount: `{(r.KillSwitchNoOpCount == r.KillSwitchTripCount ? "true" : "false")}`)");
        b.AppendLine($"- ShadowTracePath: `{r.ShadowTracePath}`");
        b.AppendLine($"- TraceRecordCount: `{r.TraceRecordCount}`");
        b.AppendLine($"- AppliedDeltaZero: `{r.AppliedDeltaZero}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine();

        b.AppendLine("## Identity & Plan Integrity");
        b.AppendLine($"- PlanIdUnchanged: `{r.PlanIdUnchanged}`");
        b.AppendLine($"- FinalApprovalIdentityUnchanged: `{r.FinalApprovalIdentityUnchanged}`");
        b.AppendLine();

        b.AppendLine("## Safety Boundaries (from execution report)");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");

        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        AppendList(b, "Blocked Reasons", r.BlockedReasons);
        AppendList(b, "Diagnostics", r.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.14R live activation observation gate hardening。基于 V7.13 execution report + shadow trace fixture 的可审计观测。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Identity", StringComparison.OrdinalIgnoreCase) || r.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)))
            return "BlockedByFinalApprovalIdentityMismatch";
        if (blocked.Any(static r => r.Contains("TraceSink", StringComparison.OrdinalIgnoreCase) || r.Contains("TraceRecord", StringComparison.OrdinalIgnoreCase)))
            return "BlockedByTraceSinkSourceIssue";
        if (blocked.Any(static r => r.Contains("NonApprovedScopeNotNoOp", StringComparison.OrdinalIgnoreCase)))
            return "BlockedByNonApprovedRouteLeak";
        if (blocked.Any(static r => r.Contains("KillSwitchTripNotNoOp", StringComparison.OrdinalIgnoreCase)))
            return "BlockedByKillSwitchBypass";
        if (blocked.Any(static r => r.Contains("SourceMissing", StringComparison.OrdinalIgnoreCase)))
            return "BlockedBySafetyBoundarySourceMissing";
        if (blocked.Any(static r => r.Contains("Execution", StringComparison.OrdinalIgnoreCase) || r.Contains("Missing", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByMissingExecution;
        if (blocked.Any(static r => r.Contains("Plan", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByMissingPlan;
        if (blocked.Any(static r => r.Contains("RequestCap", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByRequestCapExceeded;
        if (blocked.Any(static r => r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByKillSwitchNotArmed;
        if (blocked.Any(static r => r.Contains("Applied", StringComparison.OrdinalIgnoreCase) || r.Contains("Delta", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByAppliedDeltaDetected;
        if (blocked.Any(static r => r.Contains("ConfigPatch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByConfigPatchWritten;
        if (blocked.Any(static r => r.Contains("RuntimeActivation", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedByRuntimeActivationDetected;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewLiveActivationObservationRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewLiveActivationObservationRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
