using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V4.13 activation preflight + guarded runtime dry-run route；只产出 shadow/trace，不修改 runtime。
/// </summary>
public sealed class ScopedRuntimeExperimentActivationPreflightRunner
{
    public ScopedRuntimeExperimentActivationPreflightReport BuildPreflight(
        ContextCoreFoundationFreezeReport? foundationGate,
        ServiceFoundationFreezeReport? serviceFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        GuardedScopedRuntimeExperimentPlanReport? plan,
        ScopedRuntimeExperimentApprovalGateReport? approvalGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ScopedRuntimeExperimentActivationPreflightOptions? options = null)
        => BuildReport(
            "preflight",
            foundationGate,
            serviceFreeze,
            vectorFormalPreviewFreeze,
            plan,
            approvalGate,
            runtimeChangeGate,
            options,
            executeRoute: false,
            existingPreflight: null,
            existingRoute: null);

    public ScopedRuntimeExperimentActivationPreflightReport BuildDryRunRoute(
        ContextCoreFoundationFreezeReport? foundationGate,
        ServiceFoundationFreezeReport? serviceFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        GuardedScopedRuntimeExperimentPlanReport? plan,
        ScopedRuntimeExperimentApprovalGateReport? approvalGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ScopedRuntimeExperimentActivationPreflightOptions? options = null)
        => BuildReport(
            "dry-run-route",
            foundationGate,
            serviceFreeze,
            vectorFormalPreviewFreeze,
            plan,
            approvalGate,
            runtimeChangeGate,
            options,
            executeRoute: true,
            existingPreflight: null,
            existingRoute: null);

    public ScopedRuntimeExperimentActivationPreflightReport BuildGate(
        ContextCoreFoundationFreezeReport? foundationGate,
        ServiceFoundationFreezeReport? serviceFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        GuardedScopedRuntimeExperimentPlanReport? plan,
        ScopedRuntimeExperimentApprovalGateReport? approvalGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ScopedRuntimeExperimentActivationPreflightReport? existingPreflight,
        ScopedRuntimeExperimentActivationPreflightReport? existingRoute,
        ScopedRuntimeExperimentActivationPreflightOptions? options = null)
        => BuildReport(
            "activation-gate",
            foundationGate,
            serviceFreeze,
            vectorFormalPreviewFreeze,
            plan,
            approvalGate,
            runtimeChangeGate,
            options,
            executeRoute: true,
            existingPreflight,
            existingRoute);

    public static string BuildMarkdown(string title, ScopedRuntimeExperimentActivationPreflightReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- PreflightPassed: `{report.PreflightPassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- ApprovalId: `{report.ApprovalId}`");
        builder.AppendLine($"- Mode: `{report.Mode}`");
        builder.AppendLine($"- KillSwitchAvailable: `{report.KillSwitchAvailable}`");
        builder.AppendLine($"- RollbackPlanAvailable: `{report.RollbackPlanAvailable}`");
        builder.AppendLine($"- TraceSinkAvailable: `{report.TraceSinkAvailable}`");
        builder.AppendLine($"- ConfigPatchPreviewed/Written: `{report.ConfigPatchPreviewed}` / `{report.ConfigPatchWritten}`");
        builder.AppendLine($"- RuntimeRouteDryRunExecuted: `{report.RuntimeRouteDryRunExecuted}`");
        builder.AppendLine($"- DryRunRouteHitCount: `{report.DryRunRouteHitCount}`");
        builder.AppendLine($"- NonAllowlistedScopeChecked/Leak: `{report.NonAllowlistedScopeChecked}` / `{report.NonAllowlistedScopeLeakCount}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        AppendList(builder, "Selected Scopes", report.SelectedScopes);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("Route output is DryRunOnly. V4.13 does not write formal packages or mutate runtime bindings.");
        return builder.ToString();
    }

    public static string BuildTraceJsonl(ScopedRuntimeExperimentActivationPreflightReport report)
        => $$"""
{"type":"GuardedRuntimeDryRunRoute","dryRunOnly":true,"proposalId":"{{Escape(report.ProposalId)}}","approvalId":"{{Escape(report.ApprovalId)}}","routeHitCount":{{report.DryRunRouteHitCount}},"runtimeMutated":{{JsonBool(report.RuntimeMutated)}},"formalPackageWritten":{{JsonBool(report.FormalPackageWritten)}},"vectorStoreBindingChanged":{{JsonBool(report.VectorStoreBindingChanged)}}}

""";

    private static ScopedRuntimeExperimentActivationPreflightReport BuildReport(
        string stage,
        ContextCoreFoundationFreezeReport? foundationGate,
        ServiceFoundationFreezeReport? serviceFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        GuardedScopedRuntimeExperimentPlanReport? plan,
        ScopedRuntimeExperimentApprovalGateReport? approvalGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ScopedRuntimeExperimentActivationPreflightOptions? options,
        bool executeRoute,
        ScopedRuntimeExperimentActivationPreflightReport? existingPreflight,
        ScopedRuntimeExperimentActivationPreflightReport? existingRoute)
    {
        options ??= new ScopedRuntimeExperimentActivationPreflightOptions();
        var proposalId = Clean(options.ProposalId);
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            proposalId = plan?.ProposalId ?? approvalGate?.ProposalId ?? string.Empty;
        }

        var approvalId = Clean(options.ApprovalId);
        if (string.IsNullOrWhiteSpace(approvalId))
        {
            approvalId = approvalGate?.ApprovalId ?? string.Empty;
        }

        var selectedScopes = plan?.SelectedScopes ?? Array.Empty<string>();
        var killSwitchAvailable = !string.IsNullOrWhiteSpace(plan?.KillSwitchPlan);
        var rollbackPlanAvailable = !string.IsNullOrWhiteSpace(plan?.RollbackPlan);
        var traceSinkAvailable = options.TraceSinkAvailable;
        var blocked = new List<string>();

        if (!options.Enabled)
        {
            blocked.Add("ActivationPreflightDisabled");
        }

        if (!string.Equals(options.Mode, ScopedRuntimeExperimentActivationPreflightModes.PreflightAndDryRunRoute, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("UnsupportedActivationMode");
        }

        if (options.RequireFoundationFreeze && (foundationGate is null || !foundationGate.FreezePassed))
        {
            blocked.Add("FoundationReleaseCandidateGateNotPassed");
        }

        if (options.RequireServiceFoundationFreeze && (serviceFreeze is null || !serviceFreeze.FreezePassed))
        {
            blocked.Add("ServiceFoundationFreezeGateNotPassed");
        }

        if (vectorFormalPreviewFreeze is null || !vectorFormalPreviewFreeze.FreezePassed)
        {
            blocked.Add("VectorFormalPreviewFreezeGateNotPassed");
        }

        if (options.RequireV411PlanPassed && (plan is null || !plan.PlanPassed))
        {
            blocked.Add("GuardedRuntimeExperimentPlanNotPassed");
        }

        if (options.RequireV412ApprovalPassed && (approvalGate is null || !approvalGate.GatePassed))
        {
            blocked.Add("ScopedRuntimeExperimentApprovalGateNotPassed");
        }

        if (options.RequireRuntimeChangeGate && (runtimeChangeGate is null || !runtimeChangeGate.Passed))
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (!string.Equals(plan?.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ProposalIdMismatch");
        }

        if (!string.Equals(approvalGate?.ApprovalId, approvalId, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ApprovalIdMismatch");
        }

        if (!string.Equals(approvalGate?.ApprovalMode, ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ApprovalModeNotScopedRuntimeExperiment");
        }

        if (options.RequireKillSwitch && !killSwitchAvailable)
        {
            blocked.Add("KillSwitchMissing");
        }

        if (options.RequireRollbackPlan && !rollbackPlanAvailable)
        {
            blocked.Add("RollbackPlanMissing");
        }

        if (options.RequireTraceSink && !traceSinkAvailable)
        {
            blocked.Add("TraceSinkMissing");
        }

        if (selectedScopes.Count == 0)
        {
            blocked.Add("SelectedScopeMissing");
        }

        if (options.NonAllowlistedScopeLeakCount > 0)
        {
            blocked.Add("NonAllowlistedScopeLeakDetected");
        }

        if (options.UseForRuntime
            || options.FormalRetrievalAllowed
            || options.RuntimeSwitchAllowed
            || options.ReadyForRuntimeSwitch
            || options.MutateRuntime)
        {
            blocked.Add("RuntimeMutationDetected");
        }

        if (options.VectorStoreBindingChanged)
        {
            blocked.Add("VectorStoreBindingMutationDetected");
        }

        if (options.WriteFormalPackage)
        {
            blocked.Add("FormalPackageWriteDetected");
        }

        if (options.PackingPolicyChanged || options.PackageOutputChanged)
        {
            blocked.Add("PackageOrPackingMutationDetected");
        }

        if (options.RiskAfterPolicy > 0 || options.FormalOutputChanged > 0)
        {
            blocked.Add("RiskOrFormalOutputChangeDetected");
        }

        if (string.Equals(stage, "activation-gate", StringComparison.OrdinalIgnoreCase))
        {
            if (existingPreflight is null || !existingPreflight.PreflightPassed)
            {
                blocked.Add("ActivationPreflightNotPassed");
            }

            if (existingRoute is null || !existingRoute.PreflightPassed || !existingRoute.RuntimeRouteDryRunExecuted)
            {
                blocked.Add("DryRunRouteNotPassed");
            }
        }

        var distinctBlocked = Distinct(blocked);
        var passed = distinctBlocked.Length == 0;
        return new ScopedRuntimeExperimentActivationPreflightReport
        {
            OperationId = $"vector-scoped-runtime-experiment-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PreflightPassed = passed,
            Recommendation = passed
                ? ScopedRuntimeExperimentActivationPreflightRecommendations.ReadyForGuardedScopedRuntimeExperiment
                : ResolveRecommendation(distinctBlocked),
            ProposalId = proposalId,
            ApprovalId = approvalId,
            Mode = options.Mode,
            SelectedScopes = selectedScopes,
            KillSwitchAvailable = killSwitchAvailable,
            RollbackPlanAvailable = rollbackPlanAvailable,
            TraceSinkAvailable = traceSinkAvailable,
            ConfigPatchPreviewed = passed,
            ConfigPatchWritten = false,
            RuntimeRouteDryRunExecuted = executeRoute && passed,
            DryRunRouteHitCount = executeRoute && passed ? 1 : 0,
            NonAllowlistedScopeChecked = true,
            NonAllowlistedScopeLeakCount = options.NonAllowlistedScopeLeakCount,
            RuntimeMutated = options.MutateRuntime,
            VectorStoreBindingChanged = options.VectorStoreBindingChanged,
            FormalPackageWritten = options.WriteFormalPackage,
            PackingPolicyChanged = options.PackingPolicyChanged,
            PackageOutputChanged = options.PackageOutputChanged,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = options.RuntimeSwitchAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            RiskAfterPolicy = options.RiskAfterPolicy,
            FormalOutputChanged = options.FormalOutputChanged,
            BlockedReasons = distinctBlocked
        };
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static reason => reason.Contains("Approval", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByMissingApproval;
        }

        if (blocked.Any(static reason => reason.Contains("KillSwitch", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByMissingKillSwitch;
        }

        if (blocked.Any(static reason => reason.Contains("Rollback", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByMissingRollbackPlan;
        }

        if (blocked.Any(static reason => reason.Contains("TraceSink", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByMissingTraceSink;
        }

        if (blocked.Any(static reason => reason.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByScopeLeak;
        }

        if (blocked.Any(static reason => reason.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByVectorStoreBindingMutation;
        }

        if (blocked.Any(static reason => reason.Contains("FormalPackage", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByFormalPackageWrite;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeMutation", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("PackageOrPacking", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentActivationPreflightRecommendations.BlockedByRuntimeMutation;
        }

        if (blocked.Any(static reason => reason.Contains("Config", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Gate", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Plan", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Mode", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentActivationPreflightRecommendations.NeedsActivationConfig;
        }

        return ScopedRuntimeExperimentActivationPreflightRecommendations.KeepPreviewOnly;
    }

    private static string[] Distinct(IEnumerable<string> values)
        => values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();

    private static string Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string JsonBool(bool value)
        => value ? "true" : "false";

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

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
