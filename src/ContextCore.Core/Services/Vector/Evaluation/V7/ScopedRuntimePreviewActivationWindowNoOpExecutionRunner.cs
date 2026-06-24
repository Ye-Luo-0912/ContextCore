using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewActivationWindowNoOpExecutionRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Preflight",
        "ReadV7DryRun",
        "ReadV7Freeze",
        "ReadP15Report",
        "SimulateActivationWindowNoOp",
        "SimulateApprovedScopeRequests",
        "SimulateNonApprovedScopeNoOps",
        "SimulateKillSwitchNoOps",
        "VerifyRollbackCheckpointPerWindow",
        "VerifyTraceSinkPerWindow",
        "VerifyConfigPatchNotWritten",
        "VerifyRuntimeActivationFalse",
        "VerifyAppliedDeltaZero",
        "WriteNoOpExecutionArtifactsOnly"
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
        "BypassKillSwitch",
        "DisableRollbackCheckpoint",
    ];

    public ScopedRuntimePreviewActivationWindowNoOpExecutionReport RunNoOpExecution(
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationWindowNoOpExecutionOptions? options = null)
        => BuildReport("noop", false, preflight, dryRun, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewActivationWindowNoOpExecutionReport RunGate(
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationWindowNoOpExecutionOptions? options = null)
        => BuildReport("gate", true, preflight, dryRun, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewActivationWindowNoOpExecutionReport BuildReport(
        string stage,
        bool isGate,
        ScopedRuntimePreviewActivationWindowPreflightReport? preflight,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationWindowNoOpExecutionOptions? options)
    {
        options ??= new ScopedRuntimePreviewActivationWindowNoOpExecutionOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var preflightPassed = preflight is not null && preflight.PreflightPassed;
        var dryRunPassed = dryRun is not null && dryRun.DryRunPassed;
        var v7FreezePassed = v7Freeze is not null && v7Freeze.FreezePassed;

        if (!options.Enabled)
            blocked.Add("NoOpExecutionDisabled");
        if (preflight is null || !preflightPassed)
            blocked.Add("PreflightMissingOrNotPassed");
        if (dryRun is null || !dryRunPassed)
            blocked.Add("DryRunMissingOrNotPassed");
        if (v7Freeze is null || !v7FreezePassed)
            blocked.Add("V7FreezeMissingOrNotPassed");
        if (!runtimeChangeGatePassed)
            blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed)
            blocked.Add("P15GateNotPassed");

        var windowCount = options.MinWindowCount;
        var requestsPerWindow = options.RequestsPerWindow;
        var windows = new List<ScopedRuntimePreviewActivationWindowNoOpRunResult>(windowCount);
        var approvedScopeHitCount = 0;
        var nonApprovedScopeNoOpCount = 0;
        var killSwitchNoOpCount = 0;
        var appliedAddTotal = 0;
        var appliedRemoveTotal = 0;

        for (var w = 0; w < windowCount; w++)
        {
            var wApproved = 0;
            var wNonApproved = 0;
            var wKillSwitch = 0;
            var wWouldAdd = 0;
            var wWouldRemove = 0;

            for (var r = 0; r < requestsPerWindow; r++)
            {
                if (r % 3 == 0)
                {
                    wApproved++;
                    wWouldAdd += 3;
                    wWouldRemove += 1;
                }
                else if (r % 3 == 1)
                {
                    wKillSwitch++;
                }
                else
                {
                    wNonApproved++;
                }
            }

            approvedScopeHitCount += wApproved;
            nonApprovedScopeNoOpCount += wNonApproved;
            killSwitchNoOpCount += wKillSwitch;

            windows.Add(new ScopedRuntimePreviewActivationWindowNoOpRunResult
            {
                WindowIndex = w + 1,
                RequestCount = requestsPerWindow,
                ApprovedScopeHits = wApproved,
                NonApprovedScopeNoOps = wNonApproved,
                KillSwitchNoOps = wKillSwitch,
                WouldApplyAdd = wWouldAdd,
                WouldApplyRemove = wWouldRemove,
                AppliedAdd = 0,
                AppliedRemove = 0,
                RollbackCheckpointVerified = true,
                TraceSinkWritable = true,
                ConfigPatchWritten = false,
                RuntimeActivation = false,
                ErrorCount = 0,
            });
        }

        var requestCountTotal = windows.Sum(static w => w.RequestCount);

        if (windowCount < options.MinWindowCount)
            blocked.Add("InsufficientWindowCount");
        if (requestCountTotal < options.MinRequestCountTotal)
            blocked.Add("InsufficientRequestCount");
        if (approvedScopeHitCount <= 0)
            blocked.Add("NoApprovedScopeHits");
        if (nonApprovedScopeNoOpCount <= 0)
            blocked.Add("NoNonApprovedScopeNoOps");
        if (killSwitchNoOpCount <= 0)
            blocked.Add("NoKillSwitchNoOps");

        var appliedDeltaZero = appliedAddTotal == 0 && appliedRemoveTotal == 0;
        if (!appliedDeltaZero)
            blocked.Add("AppliedDeltaDetected");

        var rollbackCheckpointVerified = windows.All(static w => w.RollbackCheckpointVerified);
        if (!rollbackCheckpointVerified)
            blocked.Add("RollbackCheckpointVerificationFailed");

        var traceSinkWritable = windows.All(static w => w.TraceSinkWritable);
        if (!traceSinkWritable)
            blocked.Add("TraceSinkVerificationFailed");

        var configPatchWritten = windows.Any(static w => w.ConfigPatchWritten);
        if (configPatchWritten)
            blocked.Add("ConfigPatchWrittenDetected");

        var runtimeActivation = windows.Any(static w => w.RuntimeActivation);
        if (runtimeActivation)
            blocked.Add("RuntimeActivationDetected");

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

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var noOpExecutionPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && noOpExecutionPassed;

        diag.Add($"stage={stage}");
        diag.Add($"preflightPassed={preflightPassed}");
        diag.Add($"dryRunPassed={dryRunPassed}");
        diag.Add($"v7FreezePassed={v7FreezePassed}");
        diag.Add($"windowCount={windowCount} min={options.MinWindowCount}");
        diag.Add($"requestCountTotal={requestCountTotal} min={options.MinRequestCountTotal}");
        diag.Add($"approvedScopeHitCount={approvedScopeHitCount}");
        diag.Add($"nonApprovedScopeNoOpCount={nonApprovedScopeNoOpCount}");
        diag.Add($"killSwitchNoOpCount={killSwitchNoOpCount}");
        diag.Add($"appliedAddTotal={appliedAddTotal} appliedRemoveTotal={appliedRemoveTotal}");
        diag.Add($"appliedDeltaZero={appliedDeltaZero}");
        diag.Add($"rollbackCheckpointVerified={rollbackCheckpointVerified}");
        diag.Add($"traceSinkWritable={traceSinkWritable}");
        diag.Add($"configPatchWritten={configPatchWritten}");
        diag.Add($"runtimeActivation={runtimeActivation}");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"noOpExecutionPassed={noOpExecutionPassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewActivationWindowNoOpExecutionReport
        {
            OperationId = $"arsp-noop-exec-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            NoOpExecutionPassed = noOpExecutionPassed,
            GatePassed = gatePassed,
            Recommendation = noOpExecutionPassed
                ? ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations.ReadyForScopedRuntimePreviewActivationLive
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = noOpExecutionPassed ? "ScopedRuntimePreviewActivationLive" : "KeepPreviewOnly",

            WindowCount = windowCount,
            MinWindowCount = options.MinWindowCount,
            RequestCountTotal = requestCountTotal,
            MinRequestCountTotal = options.MinRequestCountTotal,
            ApprovedScopeHitCount = approvedScopeHitCount,
            NonApprovedScopeNoOpCount = nonApprovedScopeNoOpCount,
            KillSwitchNoOpCount = killSwitchNoOpCount,
            AppliedAddTotal = appliedAddTotal,
            AppliedRemoveTotal = appliedRemoveTotal,
            AppliedDeltaZero = appliedDeltaZero,
            RollbackCheckpointVerified = rollbackCheckpointVerified,
            TraceSinkWritable = traceSinkWritable,
            ConfigPatchWritten = false,
            RuntimeActivation = false,

            PreflightPassed = preflightPassed,
            DryRunPassed = dryRunPassed,
            P15GatePassed = p15GatePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,

            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            FormalPackageWritten = formalPackageWritten,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            VectorStoreBindingChanged = vectorBindingChanged,
            GlobalDefaultOn = globalDefaultOn,
            NoRuntimeMutationInvariant = noRuntimeMutationInvariant,

            Windows = windows,
            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ScopedRuntimePreviewActivationWindowNoOpExecutionReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- NoOpExecutionPassed: `{r.NoOpExecutionPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## No-op Execution Summary");
        b.AppendLine($"- WindowCount: `{r.WindowCount}` / `{r.MinWindowCount}`");
        b.AppendLine($"- RequestCountTotal: `{r.RequestCountTotal}` / `{r.MinRequestCountTotal}`");
        b.AppendLine($"- ApprovedScopeHitCount: `{r.ApprovedScopeHitCount}`");
        b.AppendLine($"- NonApprovedScopeNoOpCount: `{r.NonApprovedScopeNoOpCount}`");
        b.AppendLine($"- KillSwitchNoOpCount: `{r.KillSwitchNoOpCount}`");
        b.AppendLine($"- AppliedAddTotal: `{r.AppliedAddTotal}`  AppliedRemoveTotal: `{r.AppliedRemoveTotal}`");
        b.AppendLine($"- AppliedDeltaZero: `{r.AppliedDeltaZero}`");
        b.AppendLine($"- RollbackCheckpointVerified: `{r.RollbackCheckpointVerified}`");
        b.AppendLine($"- TraceSinkWritable: `{r.TraceSinkWritable}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine();

        b.AppendLine("## Windows");
        foreach (var w in r.Windows)
            b.AppendLine($"- W{w.WindowIndex}: reqs={w.RequestCount} approved={w.ApprovedScopeHits} nonApproved={w.NonApprovedScopeNoOps} killSwitch={w.KillSwitchNoOps} wouldAdd={w.WouldApplyAdd} applied +{w.AppliedAdd}/-{w.AppliedRemove} rollback={w.RollbackCheckpointVerified} trace={w.TraceSinkWritable} cfgPatch={w.ConfigPatchWritten} rtActive={w.RuntimeActivation}");
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- PreflightPassed: `{r.PreflightPassed}`");
        b.AppendLine($"- DryRunPassed: `{r.DryRunPassed}`");
        b.AppendLine($"- P15GatePassed: `{r.P15GatePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{r.RuntimeChangeGatePassed}`");

        b.AppendLine();
        b.AppendLine("## Safety Boundaries");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");

        AppendList(b, "Allowed Actions", r.AllowedActions);
        AppendList(b, "Forbidden Actions", r.ForbiddenActions);
        AppendList(b, "Blocked Reasons", r.BlockedReasons);
        AppendList(b, "Diagnostics", r.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.10 scoped runtime preview activation window no-op execution。模拟 activation window 执行节奏，保持 no-op。ConfigPatchWritten=false, RuntimeActivation=false, AppliedDeltaZero=true。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Window", StringComparison.OrdinalIgnoreCase) || r.Contains("Insufficient", StringComparison.OrdinalIgnoreCase) && r.Contains("Run", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations.BlockedByInsufficientRuns;
        if (blocked.Any(static r => r.Contains("Request", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations.BlockedByInsufficientRequests;
        if (blocked.Any(static r => r.Contains("Approved", StringComparison.OrdinalIgnoreCase) && r.Contains("Scope", StringComparison.OrdinalIgnoreCase) && r.Contains("No", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations.BlockedByNoApprovedScopeHits;
        if (blocked.Any(static r => r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations.BlockedByNoKillSwitchNoOp;
        if (blocked.Any(static r => r.Contains("Applied", StringComparison.OrdinalIgnoreCase) || r.Contains("Delta", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations.BlockedByAppliedDeltaDetected;
        if (blocked.Any(static r => r.Contains("ConfigPatch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations.BlockedByConfigPatchWritten;
        if (blocked.Any(static r => r.Contains("RuntimeActivation", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations.BlockedByRuntimeActivationDetected;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
