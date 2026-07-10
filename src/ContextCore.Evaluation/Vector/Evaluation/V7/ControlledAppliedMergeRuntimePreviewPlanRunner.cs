using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ControlledAppliedMergeRuntimePreviewPlanRunner
{
    private static readonly string[] RequiredObservationMetrics =
    [
        "RequestCount",
        "PreviewPackageCount",
        "BaselinePackageCount",
        "CandidateAddCount",
        "CandidateRemoveCount",
        "TokenDeltaTotal",
        "TokenDeltaMax",
        "RiskAfterPolicy",
        "MustNotHitRiskAfterPolicy",
        "LifecycleRiskAfterPolicy",
        "FormalOutputChanged",
        "PackageOutputChanged",
        "PackingPolicyChanged",
        "ScopeLeakCount",
        "ErrorCount",
        "LatencyP50",
        "LatencyP95",
        "KillSwitchTriggered",
        "RollbackVerified",
        "TraceCompleteness"
    ];

    private static readonly string[] RequiredStopConditions =
    [
        "RiskAfterPolicy > 0",
        "MustNotHitRiskAfterPolicy > 0",
        "LifecycleRiskAfterPolicy > 0",
        "FormalOutputChanged > 0",
        "PackageOutputChanged=true",
        "PackingPolicyChanged=true",
        "RuntimeMutated unexpected",
        "NonAllowlistedScopeLeakCount > 0",
        "ErrorCount > threshold",
        "LatencyP95 > threshold",
        "MissingTraceCount > 0",
        "KillSwitchTriggered=true"
    ];

    private static readonly string[] FrozenAllowedActions =
    [
        "PrepareRuntimePreviewActivationContract",
        "ValidateAllowlistedScopeActivation",
        "CollectPreviewMetricsPlan",
        "ValidateRollbackPlan",
        "ValidateKillSwitchPlan",
        "WritePreviewTraceOnly"
    ];

    private static readonly string[] FrozenForbiddenActions =
    [
        "GlobalDefaultOn",
        "FormalRetrievalEnable",
        "FormalPackageWrite",
        "PackingPolicyMutation",
        "PackageOutputMutationOutsidePreview",
        "VectorStoreBindingMutation",
        "RuntimeSwitch",
        "NonAllowlistedScopeUse",
        "DisablingRuntimeChangeGate",
        "DisablingP15Gate"
    ];

    public ControlledAppliedMergeRuntimePreviewPlanReport BuildPlan(
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        ArchitectureCleanupFreezeReport? optFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewPlanOptions? options = null)
        => BuildReport("plan", v6Freeze, optFreeze, runtimeChangeGate, p15GatePassed, options);

    public ControlledAppliedMergeRuntimePreviewPlanReport BuildGate(
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        ArchitectureCleanupFreezeReport? optFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewPlanOptions? options = null)
        => BuildReport("gate", v6Freeze, optFreeze, runtimeChangeGate, p15GatePassed, options);

    private static ControlledAppliedMergeRuntimePreviewPlanReport BuildReport(
        string stage,
        ControlledAppliedMergePreviewFreezeReport? v6Freeze,
        ArchitectureCleanupFreezeReport? optFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        ControlledAppliedMergeRuntimePreviewPlanOptions? options)
    {
        options ??= new ControlledAppliedMergeRuntimePreviewPlanOptions();
        var blocked = new List<string>();

        var v6FreezePassed = v6Freeze is not null && v6Freeze.FreezePassed;
        var optFreezePassed = optFreeze is not null && optFreeze.FreezePassed;
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        if (!options.Enabled)
            blocked.Add("RuntimePreviewPlanDisabled");

        if (!string.Equals(options.Mode, ControlledAppliedMergeRuntimePreviewPlanModes.PlanOnly, StringComparison.OrdinalIgnoreCase))
            blocked.Add("UnsupportedPlanMode");

        if (!v6FreezePassed)
            blocked.Add("V6FreezeNotPassed");

        if (!optFreezePassed)
            blocked.Add("OPTFreezeNotPassed");

        if (!runtimeChangeGatePassed)
            blocked.Add("RuntimeChangeGateNotPassed");

        if (!p15GatePassed)
            blocked.Add("P15GateNotPassed");

        var allowlistedScopes = BuildAllowlistedScopes(options);
        if (allowlistedScopes.Count == 0)
            blocked.Add("AllowlistedScopeNotConfigured");

        var killSwitchPlan = "Set ControlledAppliedMergeRuntimePreview:Enabled=false; flush preview trace; discard preview packages; revert to baseline retrieval path";
        var rollbackPlan = "Disable config switch → flush trace → discard preview artifacts → verify baseline package output unchanged → verify no formal package written → verify no PackingPolicy mutation";

        if (options.RequireKillSwitch && string.IsNullOrWhiteSpace(killSwitchPlan))
            blocked.Add("KillSwitchPlanMissing");

        if (options.RequireRollbackPlan && string.IsNullOrWhiteSpace(rollbackPlan))
            blocked.Add("RollbackPlanMissing");

        var observationMetrics = options.RequireObservationPlan ? RequiredObservationMetrics : Array.Empty<string>();
        if (options.RequireObservationPlan && observationMetrics.Length == 0)
            blocked.Add("ObservationPlanMissing");

        var stopConditions = options.RequireStopConditions ? RequiredStopConditions : Array.Empty<string>();
        if (options.RequireStopConditions && stopConditions.Length == 0)
            blocked.Add("StopConditionsMissing");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static v => v, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var passed = distinctBlocked.Length == 0;

        var diag = new List<string>
        {
            $"Stage: {stage}",
            $"V6FreezePassed: {v6FreezePassed}",
            $"OPTFreezePassed: {optFreezePassed}",
            $"RuntimeChangeGatePassed: {runtimeChangeGatePassed}",
            $"P15GatePassed: {p15GatePassed}",
            $"AllowlistedScopes: {allowlistedScopes.Count}",
            $"ConfigSwitch: {options.ConfigSwitch}",
            $"ApprovalMode: {options.ApprovalMode}",
            $"TracePath: {options.TracePath}",
            $"MaxRequestCount: {options.MaxRequestCount}",
            $"MaxDurationMinutes: {options.MaxDurationMinutes}",
            "FormalRetrievalAllowed: false",
            "RuntimeSwitchAllowed: false",
            "FormalPackageWritten: false",
            "PackingPolicyChanged: false",
            "PackageOutputChanged: false",
            "RuntimeMutated: false",
            "VectorStoreBindingChanged: false",
            "GlobalDefaultOn: false",
        };

        return new ControlledAppliedMergeRuntimePreviewPlanReport
        {
            OperationId = $"controlled-applied-merge-runtime-preview-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PlanPassed = passed,
            Recommendation = passed
                ? ControlledAppliedMergeRuntimePreviewPlanRecommendations.ReadyForRuntimePreviewActivation
                : ResolveRecommendation(distinctBlocked),
            Mode = options.Mode,
            NextAllowedPhase = passed
                ? "ControlledAppliedMergeRuntimePreviewActivationContract"
                : "KeepPreviewOnly",
            V6FreezePassed = v6FreezePassed,
            OPTFreezePassed = optFreezePassed,
            RuntimeChangeGatePassed = runtimeChangeGatePassed,
            P15GatePassed = p15GatePassed,
            AllowlistedScopes = allowlistedScopes,
            ConfigSwitch = options.ConfigSwitch,
            ApprovalMode = options.ApprovalMode,
            KillSwitchPlan = killSwitchPlan,
            RollbackPlan = rollbackPlan,
            TracePath = options.TracePath,
            ObservationMetrics = observationMetrics,
            StopConditions = stopConditions,
            MaxRequestCount = options.MaxRequestCount,
            MaxDurationMinutes = options.MaxDurationMinutes,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            FormalPackageWritten = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            AllowedActions = FrozenAllowedActions,
            ForbiddenActions = FrozenForbiddenActions,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ControlledAppliedMergeRuntimePreviewPlanReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`");
        b.AppendLine($"OperationId: `{report.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Summary");
        b.AppendLine($"- PlanPassed: `{report.PlanPassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- Mode: `{report.Mode}`");
        b.AppendLine($"- NextAllowedPhase: `{report.NextAllowedPhase}`");
        b.AppendLine();

        b.AppendLine("## Prerequisites");
        b.AppendLine($"- V6FreezePassed: `{report.V6FreezePassed}`");
        b.AppendLine($"- OPTFreezePassed: `{report.OPTFreezePassed}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{report.RuntimeChangeGatePassed}`");
        b.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        b.AppendLine();

        b.AppendLine("## Plan Definition");
        b.AppendLine($"- ConfigSwitch: `{report.ConfigSwitch}`");
        b.AppendLine($"- ApprovalMode: `{report.ApprovalMode}`");
        b.AppendLine($"- TracePath: `{report.TracePath}`");
        b.AppendLine($"- MaxRequestCount: `{report.MaxRequestCount}`");
        b.AppendLine($"- MaxDurationMinutes: `{report.MaxDurationMinutes}`");
        b.AppendLine();

        b.AppendLine("## Allowlisted Scopes");
        if (report.AllowlistedScopes.Count == 0)
            b.AppendLine("- (empty)");
        else
            foreach (var s in report.AllowlistedScopes) b.AppendLine($"- `{s}`");
        b.AppendLine();

        b.AppendLine("## Kill Switch Plan");
        b.AppendLine(report.KillSwitchPlan);
        b.AppendLine();

        b.AppendLine("## Rollback Plan");
        b.AppendLine(report.RollbackPlan);
        b.AppendLine();

        AppendList(b, "Observation Metrics", report.ObservationMetrics);
        AppendList(b, "Stop Conditions", report.StopConditions);
        AppendList(b, "Allowed Actions", report.AllowedActions);
        AppendList(b, "Forbidden Actions", report.ForbiddenActions);

        b.AppendLine();
        b.AppendLine("## Runtime Invariants (all must be false)");
        b.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        b.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        b.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        b.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        b.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        b.AppendLine($"- GlobalDefaultOn: `{report.GlobalDefaultOn}`");
        b.AppendLine();

        AppendList(b, "Blocked Reasons", report.BlockedReasons);
        AppendList(b, "Diagnostics", report.Diagnostics);

        b.AppendLine();
        b.AppendLine("V7.0 controlled applied merge runtime preview plan. 只产出 plan，不实现 runtime preview。不接 formal retrieval，不做 global runtime switch，不写 formal package，不改 PackingPolicy/package output，不绑定正式 IVectorIndexStore。");
        return b.ToString();
    }

    private static IReadOnlyList<string> BuildAllowlistedScopes(ControlledAppliedMergeRuntimePreviewPlanOptions options)
    {
        if (options.WorkspaceAllowlist.Count == 0 || options.CollectionAllowlist.Count == 0)
            return Array.Empty<string>();

        var scopes = new List<string>();
        foreach (var ws in options.WorkspaceAllowlist.Where(static v => !string.IsNullOrWhiteSpace(v)))
            foreach (var col in options.CollectionAllowlist.Where(static v => !string.IsNullOrWhiteSpace(v)))
                scopes.Add($"{ws.Trim()}/{col.Trim()}");
        return scopes;
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static r => r.Contains("V6Freeze", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewPlanRecommendations.BlockedByV6FreezeNotPassed;
        if (blocked.Any(static r => r.Contains("OPTFreeze", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewPlanRecommendations.BlockedByOPTFreezeNotPassed;
        if (blocked.Any(static r => r.Contains("RuntimeChange", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewPlanRecommendations.BlockedByRuntimeChangeGateNotPassed;
        if (blocked.Any(static r => r.Contains("P15", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewPlanRecommendations.BlockedByP15NotPassed;
        if (blocked.Any(static r => r.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewPlanRecommendations.BlockedByMissingKillSwitch;
        if (blocked.Any(static r => r.Contains("Rollback", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewPlanRecommendations.BlockedByMissingRollbackPlan;
        if (blocked.Any(static r => r.Contains("Observation", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewPlanRecommendations.BlockedByMissingObservationPlan;
        if (blocked.Any(static r => r.Contains("StopCondition", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewPlanRecommendations.BlockedByMissingStopConditions;
        if (blocked.Any(static r => r.Contains("Allowlisted", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewPlanRecommendations.BlockedByMissingAllowlistedScope;
        if (blocked.Any(static r => r.Contains("RuntimeSwitch", StringComparison.OrdinalIgnoreCase)))
            return ControlledAppliedMergeRuntimePreviewPlanRecommendations.BlockedByRuntimeSwitchAttempt;
        return ControlledAppliedMergeRuntimePreviewPlanRecommendations.KeepPreviewOnly;
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
