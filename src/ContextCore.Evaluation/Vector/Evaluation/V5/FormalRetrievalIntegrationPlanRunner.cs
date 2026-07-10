using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.0 formal retrieval integration plan；只规划正式接入边界，不执行 adapter、DI binding 或 package 变更。
/// </summary>
public sealed class FormalRetrievalIntegrationPlanRunner
{
    private static readonly string[] DefaultIntegrationPoints =
    [
        "vector retrieval provider",
        "IVectorIndexStore binding",
        "package builder / context package assembly",
        "fallback path",
        "config switch",
        "trace / comparison output",
        "rollback / kill switch"
    ];

    private static readonly string[] DefaultAllowedActions =
    [
        "Plan formal retrieval integration points",
        "Define ShadowFormalRetrievalAdapter requirements",
        "Define fallback and rollback plan",
        "Define trace and comparison output",
        "Define config switch without enabling runtime"
    ];

    private static readonly string[] DefaultForbiddenActions =
    [
        "Runtime switch",
        "Formal retrieval enablement",
        "Formal IVectorIndexStore binding",
        "Formal package write",
        "PackingPolicy mutation",
        "Package output mutation",
        "Global default-on"
    ];

    public FormalRetrievalIntegrationPlanReport BuildPlan(
        ScopedRuntimeExperimentObservationFreezeReport? promotionDecision,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build("plan", promotionDecision, runtimeChangeGate, p15GatePassed, existingPlan: null, sourceReports);

    public FormalRetrievalIntegrationPlanReport BuildGate(
        ScopedRuntimeExperimentObservationFreezeReport? promotionDecision,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        FormalRetrievalIntegrationPlanReport? existingPlan,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build("gate", promotionDecision, runtimeChangeGate, p15GatePassed, existingPlan, sourceReports);

    public static string BuildMarkdown(string title, FormalRetrievalIntegrationPlanReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- PlanPassed: `{report.PlanPassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- RequiredNextPhase: `{report.RequiredNextPhase}`");
        builder.AppendLine($"- V416PromotionDecisionPassed: `{report.V416PromotionDecisionPassed}`");
        builder.AppendLine($"- PromotionDecision: `{report.PromotionDecision}`");
        builder.AppendLine($"- RuntimeChangeGatePassed: `{report.RuntimeChangeGatePassed}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- FallbackPathPlan: `{report.FallbackPathPlan}`");
        builder.AppendLine($"- ConfigSwitchPlan: `{report.ConfigSwitchPlan}`");
        builder.AppendLine($"- TraceComparisonOutputPlan: `{report.TraceComparisonOutputPlan}`");
        builder.AppendLine($"- RollbackPlan: `{report.RollbackPlan}`");
        builder.AppendLine($"- KillSwitchPlan: `{report.KillSwitchPlan}`");
        AppendList(builder, "Integration Points", report.IntegrationPoints);
        AppendList(builder, "Allowed Actions", report.AllowedActions);
        AppendList(builder, "Forbidden Actions", report.ForbiddenActions);
        AppendMap(builder, "Source Reports", report.SourceReports);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("V5.0 is PlanOnly. It does not bind IVectorIndexStore, enable formal retrieval, switch runtime, write formal packages, mutate PackingPolicy, or mutate package output.");
        return builder.ToString();
    }

    private static FormalRetrievalIntegrationPlanReport Build(
        string stage,
        ScopedRuntimeExperimentObservationFreezeReport? promotionDecision,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        FormalRetrievalIntegrationPlanReport? existingPlan,
        IReadOnlyDictionary<string, string>? sourceReports)
    {
        var blocked = new List<string>();
        var v416Passed = promotionDecision is not null
            && promotionDecision.FreezePassed
            && string.Equals(
                promotionDecision.PromotionDecision,
                ScopedRuntimeExperimentObservationFreezeDecisions.ReadyForFormalRetrievalIntegrationPlan,
                StringComparison.OrdinalIgnoreCase);
        if (!v416Passed)
        {
            blocked.Add("V416PromotionDecisionNotPassed");
        }

        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (!p15GatePassed)
        {
            blocked.Add("P15GateNotPassed");
        }

        if (string.Equals(stage, "gate", StringComparison.OrdinalIgnoreCase) && existingPlan is null)
        {
            blocked.Add("FormalRetrievalIntegrationPlanMissing");
        }

        if (promotionDecision is not null)
        {
            if (promotionDecision.FormalOutputChanged > 0 || promotionDecision.FormalPackageWritten)
            {
                blocked.Add("FormalOutputMutationDetected");
            }

            if (promotionDecision.PackageOutputChanged)
            {
                blocked.Add("PackageOutputMutationDetected");
            }

            if (promotionDecision.PackingPolicyChanged)
            {
                blocked.Add("PackingPolicyMutationDetected");
            }

            if (promotionDecision.VectorStoreBindingChanged)
            {
                blocked.Add("VectorStoreBindingMutationDetected");
            }

            if (promotionDecision.FormalRetrievalAllowed
                || promotionDecision.RuntimeSwitchAllowed
                || promotionDecision.ReadyForRuntimeSwitch
                || promotionDecision.UseForRuntime
                || promotionDecision.RuntimeMutated)
            {
                blocked.Add("RuntimeMutationDetected");
            }
        }

        if (existingPlan is not null)
        {
            if (existingPlan.FormalOutputChanged > 0 || existingPlan.FormalPackageWritten)
            {
                blocked.Add("FormalOutputMutationDetected");
            }

            if (existingPlan.PackageOutputChanged)
            {
                blocked.Add("PackageOutputMutationDetected");
            }

            if (existingPlan.PackingPolicyChanged)
            {
                blocked.Add("PackingPolicyMutationDetected");
            }

            if (existingPlan.VectorStoreBindingChanged)
            {
                blocked.Add("VectorStoreBindingMutationDetected");
            }
        }

        var passed = blocked.Count == 0;
        return new FormalRetrievalIntegrationPlanReport
        {
            OperationId = $"vector-formal-retrieval-integration-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PlanPassed = passed,
            Recommendation = ResolveRecommendation(blocked, passed),
            AllowedMode = FormalRetrievalIntegrationPlanModes.PlanOnly,
            RequiredNextPhase = "ShadowFormalRetrievalAdapter",
            V416PromotionDecisionPassed = v416Passed,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed ?? false,
            P15GatePassed = p15GatePassed,
            PromotionDecision = promotionDecision?.PromotionDecision ?? string.Empty,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            FormalOutputChanged = promotionDecision?.FormalOutputChanged ?? 0,
            PackageOutputChanged = promotionDecision?.PackageOutputChanged ?? false,
            PackingPolicyChanged = promotionDecision?.PackingPolicyChanged ?? false,
            VectorStoreBindingChanged = promotionDecision?.VectorStoreBindingChanged ?? false,
            FormalPackageWritten = promotionDecision?.FormalPackageWritten ?? false,
            IntegrationPoints = DefaultIntegrationPoints,
            AllowedActions = DefaultAllowedActions,
            ForbiddenActions = DefaultForbiddenActions,
            FallbackPathPlan = "Keep current formal retrieval/package assembly as baseline; shadow adapter must fail closed to baseline.",
            ConfigSwitchPlan = "Define an explicit scoped config switch for the next ShadowFormalRetrievalAdapter phase; default remains off.",
            TraceComparisonOutputPlan = "Emit shadow comparison artifacts for baseline versus vector candidates without changing package output.",
            RollbackPlan = "Disable scoped shadow adapter config and continue baseline path; historical traces are retained.",
            KillSwitchPlan = "A config-level kill switch must return all scoped requests to baseline before any adapter activation.",
            SourceReports = sourceReports ?? new Dictionary<string, string>(),
            BlockedReasons = blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool passed)
    {
        if (passed)
        {
            return FormalRetrievalIntegrationPlanRecommendations.ReadyForShadowFormalRetrievalAdapter;
        }

        if (blocked.Contains("V416PromotionDecisionNotPassed", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("FormalRetrievalIntegrationPlanMissing", StringComparer.OrdinalIgnoreCase))
        {
            return FormalRetrievalIntegrationPlanRecommendations.BlockedByMissingPromotionDecision;
        }

        if (blocked.Contains("P15GateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return FormalRetrievalIntegrationPlanRecommendations.BlockedByP15Gate;
        }

        if (blocked.Contains("RuntimeChangeReadinessGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return FormalRetrievalIntegrationPlanRecommendations.BlockedByRuntimeChangeGate;
        }

        if (blocked.Contains("FormalOutputMutationDetected", StringComparer.OrdinalIgnoreCase))
        {
            return FormalRetrievalIntegrationPlanRecommendations.BlockedByFormalOutputMutation;
        }

        if (blocked.Contains("PackageOutputMutationDetected", StringComparer.OrdinalIgnoreCase))
        {
            return FormalRetrievalIntegrationPlanRecommendations.BlockedByPackageOutputMutation;
        }

        if (blocked.Contains("PackingPolicyMutationDetected", StringComparer.OrdinalIgnoreCase))
        {
            return FormalRetrievalIntegrationPlanRecommendations.BlockedByPackingPolicyMutation;
        }

        if (blocked.Contains("VectorStoreBindingMutationDetected", StringComparer.OrdinalIgnoreCase))
        {
            return FormalRetrievalIntegrationPlanRecommendations.BlockedByVectorBindingMutation;
        }

        return FormalRetrievalIntegrationPlanRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }

    private static void AppendMap(StringBuilder builder, string title, IReadOnlyDictionary<string, string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var (key, value) in values.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {key}: `{value}`");
        }
    }
}
