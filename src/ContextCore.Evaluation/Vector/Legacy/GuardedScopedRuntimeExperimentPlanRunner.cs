using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V4.11 guarded scoped runtime experiment plan；只产出 activation contract，不授权 runtime 切换。
/// </summary>
public sealed class GuardedScopedRuntimeExperimentPlanRunner
{
    private static readonly string[] RequiredObservationMetrics =
    [
        "RequestCount",
        "BaselinePackageCount",
        "ExperimentPackageCount",
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
        "RollbackVerified"
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
        "MissingTraceCount > 0"
    ];

    public GuardedScopedRuntimeExperimentPlanReport BuildPlan(
        ContextCoreFoundationFreezeReport? foundationReleaseCandidate,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        ScopedRuntimeExperimentDesignFreezeReport? designFreeze,
        ScopedRuntimeExperimentHarnessFreezeReport? harnessFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ExplicitScopedRuntimeExperimentProposalReport? proposal,
        GuardedScopedRuntimeExperimentPlanOptions? options = null)
        => BuildReport(
            "plan",
            foundationReleaseCandidate,
            serviceFoundationFreeze,
            vectorFormalPreviewFreeze,
            designFreeze,
            harnessFreeze,
            runtimeChangeGate,
            proposal,
            options);

    public GuardedScopedRuntimeExperimentPlanReport BuildGate(
        ContextCoreFoundationFreezeReport? foundationReleaseCandidate,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        ScopedRuntimeExperimentDesignFreezeReport? designFreeze,
        ScopedRuntimeExperimentHarnessFreezeReport? harnessFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ExplicitScopedRuntimeExperimentProposalReport? proposal,
        GuardedScopedRuntimeExperimentPlanOptions? options = null)
        => BuildReport(
            "gate",
            foundationReleaseCandidate,
            serviceFoundationFreeze,
            vectorFormalPreviewFreeze,
            designFreeze,
            harnessFreeze,
            runtimeChangeGate,
            proposal,
            options);

    public static string BuildMarkdown(string title, GuardedScopedRuntimeExperimentPlanReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine($"- PlanPassed: `{report.PlanPassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- RequiredApprovalMode: `{report.RequiredApprovalMode}`");
        builder.AppendLine($"- MaxRequestCount: `{report.MaxRequestCount}`");
        builder.AppendLine($"- MaxDurationMinutes: `{report.MaxDurationMinutes}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- KillSwitchPlan: `{report.KillSwitchPlan}`");
        builder.AppendLine($"- RollbackPlan: `{report.RollbackPlan}`");
        AppendList(builder, "Selected Scopes", report.SelectedScopes);
        AppendList(builder, "Observation Plan", report.ObservationPlan);
        AppendList(builder, "Stop Conditions", report.StopConditions);
        AppendList(builder, "Allowed Actions", report.AllowedActions);
        AppendList(builder, "Forbidden Actions", report.ForbiddenActions);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("V4.11 只冻结 guarded scoped runtime experiment plan / activation contract；不授权 runtime switch。");
        return builder.ToString();
    }

    private static GuardedScopedRuntimeExperimentPlanReport BuildReport(
        string stage,
        ContextCoreFoundationFreezeReport? foundationReleaseCandidate,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        ScopedRuntimeExperimentDesignFreezeReport? designFreeze,
        ScopedRuntimeExperimentHarnessFreezeReport? harnessFreeze,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ExplicitScopedRuntimeExperimentProposalReport? proposal,
        GuardedScopedRuntimeExperimentPlanOptions? options)
    {
        options ??= new GuardedScopedRuntimeExperimentPlanOptions();
        var proposalId = Clean(options.ProposalId);
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            proposalId = proposal?.ProposalId ?? string.Empty;
        }

        var workspaceAllowlist = options.WorkspaceAllowlist.Count > 0
            ? options.WorkspaceAllowlist.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()).ToArray()
            : SplitFallback(proposal?.WorkspaceId);
        var collectionAllowlist = options.CollectionAllowlist.Count > 0
            ? options.CollectionAllowlist.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()).ToArray()
            : SplitFallback(proposal?.CollectionId);
        var evalScopeAllowlist = options.EvalScopeAllowlist.Count > 0
            ? options.EvalScopeAllowlist.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()).ToArray()
            : SplitFallback(proposal?.EvalScopeId);
        var selectedScopes = BuildSelectedScopes(workspaceAllowlist, collectionAllowlist, evalScopeAllowlist);
        var killSwitchPlan = Clean(proposal?.KillSwitchPlan);
        var rollbackPlan = Clean(proposal?.RollbackPlan);
        var observationPlan = options.RequireObservationPlan ? RequiredObservationMetrics : Array.Empty<string>();
        var stopConditions = RequiredStopConditions;
        var blocked = new List<string>();

        if (!options.Enabled)
        {
            blocked.Add("GuardedScopedRuntimeExperimentPlanDisabled");
        }

        if (!string.Equals(options.Mode, GuardedScopedRuntimeExperimentPlanModes.PlanOnly, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("UnsupportedPlanMode");
        }

        if (foundationReleaseCandidate is null || !foundationReleaseCandidate.FreezePassed)
        {
            blocked.Add("FoundationReleaseCandidateGateNotPassed");
        }

        if (serviceFoundationFreeze is null || !serviceFoundationFreeze.FreezePassed)
        {
            blocked.Add("ServiceFoundationFreezeGateNotPassed");
        }

        if (vectorFormalPreviewFreeze is null || !vectorFormalPreviewFreeze.FreezePassed)
        {
            blocked.Add("VectorFormalPreviewFreezeGateNotPassed");
        }

        if (designFreeze is null || !designFreeze.FreezePassed)
        {
            blocked.Add("ScopedRuntimeExperimentDesignFreezeGateNotPassed");
        }

        if (harnessFreeze is null || !harnessFreeze.FreezePassed)
        {
            blocked.Add("ScopedRuntimeExperimentHarnessFreezeGateNotPassed");
        }

        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (proposal is null || !proposal.ProposalPassed || !string.Equals(proposal.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ProposalGateNotPassed");
        }

        if (selectedScopes.Count == 0)
        {
            blocked.Add("SelectedScopeNotConfigured");
        }

        if (options.RequireKillSwitch && string.IsNullOrWhiteSpace(killSwitchPlan))
        {
            blocked.Add("KillSwitchPlanMissing");
        }

        if (options.RequireRollbackPlan && string.IsNullOrWhiteSpace(rollbackPlan))
        {
            blocked.Add("RollbackPlanMissing");
        }

        if (!options.RequireObservationPlan || observationPlan.Length == 0)
        {
            blocked.Add("ObservationPlanMissing");
        }

        if (stopConditions.Length == 0)
        {
            blocked.Add("StopConditionsMissing");
        }

        if (!string.Equals(options.RequiredApprovalMode, ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add(string.Equals(options.RequiredApprovalMode, ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly, StringComparison.OrdinalIgnoreCase)
                ? "NoOpHarnessOnlyApprovalCannotSatisfyRuntimeApproval"
                : "RequiredApprovalModeNotScopedRuntimeExperiment");
        }

        if (options.UseForRuntime
            || options.FormalRetrievalAllowed
            || options.RuntimeSwitchAllowed
            || options.ReadyForRuntimeSwitch)
        {
            blocked.Add("RuntimeSwitchAttempt");
        }

        if (options.MaxRiskCount != 0)
        {
            blocked.Add("MaxRiskCountMustRemainZero");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        return new GuardedScopedRuntimeExperimentPlanReport
        {
            OperationId = $"vector-guarded-scoped-runtime-experiment-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PlanPassed = passed,
            Recommendation = passed
                ? GuardedScopedRuntimeExperimentPlanRecommendations.ReadyForScopedRuntimeExperimentActivationContract
                : ResolveRecommendation(distinctBlocked),
            ProposalId = proposalId,
            RequiredApprovalMode = options.RequiredApprovalMode,
            SelectedScopes = selectedScopes,
            MaxRequestCount = options.MaxRequestCount,
            MaxDurationMinutes = options.MaxDurationMinutes,
            KillSwitchPlan = killSwitchPlan,
            RollbackPlan = rollbackPlan,
            ObservationPlan = observationPlan,
            StopConditions = stopConditions,
            AllowedActions =
            [
                "PrepareScopedRuntimeExperimentApproval",
                "ValidateSelectedScopeActivationContract",
                "CollectExperimentMetricsPlan",
                "ValidateRollbackPlan",
                "ValidateKillSwitchPlan"
            ],
            ForbiddenActions =
            [
                "GlobalRuntimeSwitch",
                "NonAllowlistedScopeUse",
                "FormalPackageWrite",
                "PackingPolicyMutation",
                "PackageOutputMutationOutsideExperimentArtifact",
                "DIBindingMutation",
                "IVectorIndexStoreGlobalBindingMutation",
                "DisablingRuntimeChangeGate",
                "TreatingNoOpHarnessOnlyApprovalAsRuntimeApproval"
            ],
            RuntimeSwitchAllowed = options.RuntimeSwitchAllowed,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            UseForRuntime = options.UseForRuntime,
            BlockedReasons = distinctBlocked
        };
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static reason => string.Equals(reason, "SelectedScopeNotConfigured", StringComparison.OrdinalIgnoreCase)))
        {
            return GuardedScopedRuntimeExperimentPlanRecommendations.NeedsScopeConfiguration;
        }

        if (blocked.Any(static reason => reason.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
        {
            return GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByMissingKillSwitch;
        }

        if (blocked.Any(static reason => reason.Contains("Rollback", StringComparison.OrdinalIgnoreCase)))
        {
            return GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByMissingRollbackPlan;
        }

        if (blocked.Any(static reason => reason.Contains("Observation", StringComparison.OrdinalIgnoreCase)))
        {
            return GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByMissingObservationPlan;
        }

        if (blocked.Any(static reason => reason.Contains("ApprovalMode", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("NoOpHarnessOnly", StringComparison.OrdinalIgnoreCase)))
        {
            return GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByUnsafeApprovalMode;
        }

        if (blocked.Any(static reason => reason.Contains("Gate", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Proposal", StringComparison.OrdinalIgnoreCase)))
        {
            return GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByMissingGate;
        }

        if (blocked.Any(static reason => string.Equals(reason, "RuntimeSwitchAttempt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason, "MaxRiskCountMustRemainZero", StringComparison.OrdinalIgnoreCase)))
        {
            return GuardedScopedRuntimeExperimentPlanRecommendations.BlockedByRuntimeSwitchAttempt;
        }

        return GuardedScopedRuntimeExperimentPlanRecommendations.KeepPreviewOnly;
    }

    private static IReadOnlyList<string> BuildSelectedScopes(
        IReadOnlyList<string> workspaceAllowlist,
        IReadOnlyList<string> collectionAllowlist,
        IReadOnlyList<string> evalScopeAllowlist)
    {
        if (workspaceAllowlist.Count == 0 || collectionAllowlist.Count == 0 || evalScopeAllowlist.Count == 0)
        {
            return Array.Empty<string>();
        }

        return
        [
            $"{workspaceAllowlist[0]}/{collectionAllowlist[0]}/{evalScopeAllowlist[0]}"
        ];
    }

    private static string[] SplitFallback(string? value)
        => string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : [value.Trim()];

    private static string Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }
}
