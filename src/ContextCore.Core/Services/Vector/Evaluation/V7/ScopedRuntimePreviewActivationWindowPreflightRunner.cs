using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ScopedRuntimePreviewActivationWindowPreflightRunner
{
    private static readonly string[] FrozenAllowedActions =
    [
        "ReadV7Preparation",
        "ReadV7DryRun",
        "ReadV7Authorization",
        "ReadV7Freeze",
        "ReadRuntimeChangeGate",
        "ReadP15Report",
        "ValidateApprovedScopesUnchanged",
        "ValidateWindowDuration",
        "ValidateRequestCap",
        "ValidateKillSwitchProbe",
        "ValidateRollbackCheckpoint",
        "ValidateTraceSink",
        "ValidateConfigPatchPreviewOnly",
        "ValidateStopConditions",
        "WritePreflightArtifactsOnly"
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

    public ScopedRuntimePreviewActivationWindowPreflightReport RunPreflight(
        ScopedRuntimePreviewActivationPreparationReport? preparation,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationWindowPreflightOptions? options = null)
        => BuildReport("preflight", false, preparation, dryRun, authorization, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    public ScopedRuntimePreviewActivationWindowPreflightReport RunGate(
        ScopedRuntimePreviewActivationPreparationReport? preparation,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationWindowPreflightOptions? options = null)
        => BuildReport("gate", true, preparation, dryRun, authorization, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

    private static ScopedRuntimePreviewActivationWindowPreflightReport BuildReport(
        string stage,
        bool isGate,
        ScopedRuntimePreviewActivationPreparationReport? preparation,
        ScopedRuntimePreviewActivationDryRunReport? dryRun,
        ScopedRuntimePreviewAuthorizationReport? authorization,
        ControlledAppliedMergeRuntimePreviewObservationFreezeReport? v7Freeze,
        bool runtimeChangeGatePassed,
        bool p15GatePassed,
        ScopedRuntimePreviewActivationWindowPreflightOptions? options)
    {
        options ??= new ScopedRuntimePreviewActivationWindowPreflightOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        var preparationPassed = preparation is not null && preparation.PreparationPassed;
        var dryRunPassed = dryRun is not null && dryRun.DryRunPassed;
        var authorizationPassed = authorization is not null && authorization.Authorized;

        if (!options.Enabled)
            blocked.Add("PreflightDisabled");

        if (preparation is null || !preparationPassed)
            blocked.Add("PreparationMissingOrNotPassed");
        if (dryRun is null || !dryRunPassed)
            blocked.Add("DryRunMissingOrNotPassed");
        if (authorization is null || !authorizationPassed)
            blocked.Add("AuthorizationMissingOrNotPassed");
        if (!runtimeChangeGatePassed)
            blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15GatePassed)
            blocked.Add("P15GateNotPassed");

        var prepScopes = preparation?.ApprovedScopes ?? Array.Empty<string>();
        var authScopes = authorization?.ApprovedScopes ?? Array.Empty<string>();
        var scopesUnchanged = prepScopes.Count > 0
            && prepScopes.Count == authScopes.Count
            && prepScopes.All(s => authScopes.Any(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase)));
        if (!scopesUnchanged)
            blocked.Add("ApprovedScopesMismatch");

        var now = DateTimeOffset.UtcNow;
        var authValidUntil = authorization?.ValidityNotAfter ?? DateTimeOffset.UtcNow.AddDays(-1);
        var authorizationValid = now <= authValidUntil;
        if (!authorizationValid)
            blocked.Add("AuthorizationExpired");

        var windowDurationMinutes = options.MaxWindowDurationMinutes;
        var windowDurationWithinLimit = windowDurationMinutes > 0 && windowDurationMinutes <= 30;
        if (!windowDurationWithinLimit)
            blocked.Add("WindowDurationExceeded");

        var maxRequests = options.MaxRequestsPerWindow;
        var requestCapDefined = maxRequests > 0;
        if (!requestCapDefined)
            blocked.Add("RequestCapMissing");

        var killSwitchNoOpCount = dryRun?.KillSwitchNoOpCount ?? 0;
        var killSwitchNoOpVerified = killSwitchNoOpCount > 0;
        if (!killSwitchNoOpVerified)
            blocked.Add("KillSwitchNotVerified");

        var rollbackCheckpointAvailable = preparation?.RollbackConfigured ?? false;
        if (!rollbackCheckpointAvailable)
            blocked.Add("RollbackCheckpointUnavailable");

        var traceSinkWritable = preparation?.TraceRetentionConfigured ?? false;
        if (!traceSinkWritable)
            blocked.Add("TraceSinkUnavailable");

        var configPatchPreviewOnly = !(preparation?.ConfigPatchWritten ?? false) && !(dryRun?.ConfigPatchWritten ?? false);
        if (!configPatchPreviewOnly)
            blocked.Add("ConfigPatchNotPreviewOnly");

        var stopConditionsCount = preparation?.StopConditions?.Count ?? 0;
        var stopConditionsSufficient = stopConditionsCount >= options.MinStopConditions;
        if (!stopConditionsSufficient)
            blocked.Add("StopConditionsInsufficient");

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

        var preflightPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && preflightPassed;

        diag.Add($"stage={stage}");
        diag.Add($"preparationPassed={preparationPassed}");
        diag.Add($"dryRunPassed={dryRunPassed}");
        diag.Add($"authorizationPassed={authorizationPassed}");
        diag.Add($"runtimeChangeGatePassed={runtimeChangeGatePassed}");
        diag.Add($"p15GatePassed={p15GatePassed}");
        diag.Add($"scopesUnchanged={scopesUnchanged}");
        diag.Add($"authorizationValid={authorizationValid}");
        diag.Add($"windowDurationMinutes={windowDurationMinutes} withinLimit={windowDurationWithinLimit}");
        diag.Add($"requestCapDefined={requestCapDefined} maxRequests={maxRequests}");
        diag.Add($"killSwitchNoOpVerified={killSwitchNoOpVerified} count={killSwitchNoOpCount}");
        diag.Add($"rollbackCheckpointAvailable={rollbackCheckpointAvailable}");
        diag.Add($"traceSinkWritable={traceSinkWritable}");
        diag.Add($"configPatchPreviewOnly={configPatchPreviewOnly}");
        diag.Add($"stopConditionsCount={stopConditionsCount} sufficient={stopConditionsSufficient}");
        diag.Add($"configPatchWritten=false");
        diag.Add($"runtimeActivation=false");
        diag.Add($"noRuntimeMutationInvariant={noRuntimeMutationInvariant}");
        diag.Add($"preflightPassed={preflightPassed} gatePassed={gatePassed}");
        if (!isGate)
            diag.Add("GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative");

        return new ScopedRuntimePreviewActivationWindowPreflightReport
        {
            OperationId = $"scoped-runtime-preview-activation-window-preflight-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            PreflightPassed = preflightPassed,
            GatePassed = gatePassed,
            Recommendation = preflightPassed
                ? ScopedRuntimePreviewActivationWindowPreflightRecommendations.ReadyForScopedRuntimePreviewActivationWindow
                : ResolveRecommendation(distinctBlocked),
            NextAllowedPhase = preflightPassed
                ? "ScopedRuntimePreviewActivationWindow"
                : "KeepPreviewOnly",

            PreparationPassed = preparationPassed,
            DryRunPassed = dryRunPassed,
            AuthorizationPassed = authorizationPassed,
            AuthorizationValid = authorizationValid,
            P15GatePassed = p15GatePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,

            ScopesUnchanged = scopesUnchanged,
            WindowDurationMinutes = windowDurationMinutes,
            MaxWindowDurationMinutes = options.MaxWindowDurationMinutes,
            WindowDurationWithinLimit = windowDurationWithinLimit,
            MaxRequestsPerWindow = maxRequests,
            RequestCapDefined = requestCapDefined,

            KillSwitchNoOpVerified = killSwitchNoOpVerified,
            KillSwitchNoOpCount = killSwitchNoOpCount,
            RollbackCheckpointAvailable = rollbackCheckpointAvailable,
            TraceSinkWritable = traceSinkWritable,
            ConfigPatchPreviewOnly = configPatchPreviewOnly,
            ConfigPatchWritten = false,
            StopConditionsCount = stopConditionsCount,
            StopConditionsSufficient = stopConditionsSufficient,

            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            FormalPackageWritten = formalPackageWritten,
            RuntimeActivation = false,
            WriteConfigPatch = false,
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

    public static string BuildMarkdown(string title, ScopedRuntimePreviewActivationWindowPreflightReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- PreflightPassed: `{r.PreflightPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Preflight Checks");
        b.AppendLine($"- ScopesUnchanged: `{r.ScopesUnchanged}`");
        b.AppendLine($"- AuthorizationValid: `{r.AuthorizationValid}`");
        b.AppendLine($"- WindowDuration: `{r.WindowDurationMinutes}` / `{r.MaxWindowDurationMinutes}` min  withinLimit=`{r.WindowDurationWithinLimit}`");
        b.AppendLine($"- MaxRequestsPerWindow: `{r.MaxRequestsPerWindow}`  capDefined=`{r.RequestCapDefined}`");
        b.AppendLine($"- KillSwitchNoOpVerified: `{r.KillSwitchNoOpVerified}` (count: `{r.KillSwitchNoOpCount}`)");
        b.AppendLine($"- RollbackCheckpointAvailable: `{r.RollbackCheckpointAvailable}`");
        b.AppendLine($"- TraceSinkWritable: `{r.TraceSinkWritable}`");
        b.AppendLine($"- ConfigPatchPreviewOnly: `{r.ConfigPatchPreviewOnly}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- StopConditions: `{r.StopConditionsCount}`  sufficient=`{r.StopConditionsSufficient}`");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine($"- WriteConfigPatch: `{r.WriteConfigPatch}`");
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- PreparationPassed: `{r.PreparationPassed}`");
        b.AppendLine($"- DryRunPassed: `{r.DryRunPassed}`");
        b.AppendLine($"- AuthorizationPassed: `{r.AuthorizationPassed}`");
        b.AppendLine($"- P15GatePassed: `{r.P15GatePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{r.RuntimeChangeGatePassed}`");
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
        b.AppendLine("V7.9 scoped runtime preview activation window preflight。启动前预检：验证 activation window contract 可安全执行。ConfigPatchWritten=false, RuntimeActivation=false。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。");
        return b.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("Preparation", StringComparison.OrdinalIgnoreCase) || r.Contains("Missing", StringComparison.OrdinalIgnoreCase) && !r.Contains("Request", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedByMissingPreparation;
        if (blocked.Any(static r => r.Contains("DryRun", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedByMissingDryRun;
        if (blocked.Any(static r => r.Contains("Scope", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedByScopeMismatch;
        if (blocked.Any(static r => r.Contains("Window", StringComparison.OrdinalIgnoreCase) || r.Contains("Duration", StringComparison.OrdinalIgnoreCase) || r.Contains("Exceeded", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedByWindowDurationExceeded;
        if (blocked.Any(static r => r.Contains("Request", StringComparison.OrdinalIgnoreCase) || r.Contains("Cap", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedByRequestCapMissing;
        if (blocked.Any(static r => r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedByKillSwitchNotVerified;
        if (blocked.Any(static r => r.Contains("Rollback", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedByRollbackCheckpointUnavailable;
        if (blocked.Any(static r => r.Contains("Trace", StringComparison.OrdinalIgnoreCase) || r.Contains("Sink", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedByTraceSinkUnavailable;
        if (blocked.Any(static r => r.Contains("ConfigPatch", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedByConfigPatchNotPreviewOnly;
        if (blocked.Any(static r => r.Contains("Stop", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedByStopConditionsMissing;
        if (blocked.Any(static r => r.Contains("Authori", StringComparison.OrdinalIgnoreCase) && r.Contains("Expired", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedByAuthorizationExpired;
        if (blocked.Any(static r => r.Contains("Safety", StringComparison.OrdinalIgnoreCase)))
            return ScopedRuntimePreviewActivationWindowPreflightRecommendations.BlockedBySafetyBoundaryViolation;
        return ScopedRuntimePreviewActivationWindowPreflightRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0) { b.AppendLine("- (empty)"); return; }
        foreach (var v in values) b.AppendLine($"- `{v}`");
    }
}
