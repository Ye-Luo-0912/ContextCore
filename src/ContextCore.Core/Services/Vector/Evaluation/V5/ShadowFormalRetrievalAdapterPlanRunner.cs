using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.1 shadow formal retrieval adapter 计划；只定义设计和 gate，不创建正式 adapter 或 runtime binding。
/// </summary>
public sealed class ShadowFormalRetrievalAdapterPlanRunner
{
    private static readonly string[] AdapterInputs =
    [
        "query",
        "workspaceId",
        "collectionId",
        "package context",
        "baseline formal retrieval/package snapshot"
    ];

    private static readonly string[] AdapterOutputs =
    [
        "shadow candidates only",
        "comparison artifact",
        "trace artifact",
        "risk/eligibility diagnostics"
    ];

    private static readonly string[] GateOrder =
    [
        "provider scope isolation",
        "candidate eligibility",
        "lifecycle projection",
        "risk projection",
        "must-not risk gate",
        "post-scoring risk gate",
        "formal output/package invariant gate"
    ];

    private static readonly string[] AllowedActions =
    [
        "Plan ShadowFormalRetrievalAdapter input/output contracts",
        "Plan read-only vector and graph candidate collection",
        "Plan shadow trace and comparison artifact output",
        "Plan latency and allocation baseline measurement",
        "Plan fallback to current formal package path"
    ];

    private static readonly string[] ForbiddenActions =
    [
        "Formal IVectorIndexStore binding",
        "Runtime retrieval provider switch",
        "Formal package write",
        "PackingPolicy mutation",
        "Package output mutation",
        "Graph/vector candidate direct insertion into formal selected set",
        "Global default-on"
    ];

    public ShadowFormalRetrievalAdapterPlanReport BuildPlan(
        ProjectStateAuditReport? projectStateAudit,
        VectorFormalPreviewFreezeReport? formalPreviewFreeze,
        ScopedRuntimeExperimentObservationFreezeReport? promotionDecision,
        GuardedScopedRuntimeExperimentReport? guardedRuntimeExperiment,
        VectorShadowPackageComparisonReport? shadowPackageComparison,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(
            "plan",
            projectStateAudit,
            formalPreviewFreeze,
            promotionDecision,
            guardedRuntimeExperiment,
            shadowPackageComparison,
            runtimeChangeGate,
            existingPlan: null,
            sourceReports);

    public ShadowFormalRetrievalAdapterPlanReport BuildGate(
        ProjectStateAuditReport? projectStateAudit,
        VectorFormalPreviewFreezeReport? formalPreviewFreeze,
        ScopedRuntimeExperimentObservationFreezeReport? promotionDecision,
        GuardedScopedRuntimeExperimentReport? guardedRuntimeExperiment,
        VectorShadowPackageComparisonReport? shadowPackageComparison,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ShadowFormalRetrievalAdapterPlanReport? existingPlan,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(
            "gate",
            projectStateAudit,
            formalPreviewFreeze,
            promotionDecision,
            guardedRuntimeExperiment,
            shadowPackageComparison,
            runtimeChangeGate,
            existingPlan,
            sourceReports);

    public static string BuildMarkdown(string title, ShadowFormalRetrievalAdapterPlanReport report)
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
        builder.AppendLine($"- VectorProviderSource: `{report.VectorProviderSource}`");
        builder.AppendLine($"- GraphCandidateSource: `{report.GraphCandidateSource}`");
        builder.AppendLine($"- V50ProjectStateAuditPassed: `{report.V50ProjectStateAuditPassed}`");
        builder.AppendLine($"- V4FormalPreviewFreezeReadable: `{report.V4FormalPreviewFreezeReadable}`");
        builder.AppendLine($"- V416PromotionDecisionReadable: `{report.V416PromotionDecisionReadable}`");
        builder.AppendLine($"- V414GuardedRuntimeExperimentReadable: `{report.V414GuardedRuntimeExperimentReadable}`");
        builder.AppendLine($"- V42ShadowPackageComparisonReadable: `{report.V42ShadowPackageComparisonReadable}`");
        builder.AppendLine($"- RuntimeChangeGatePassed: `{report.RuntimeChangeGatePassed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- NoRuntimeMutationInvariant: `{report.NoRuntimeMutationInvariant}`");
        builder.AppendLine($"- FallbackPath: `{report.FallbackPath}`");
        builder.AppendLine($"- RollbackPlan: `{report.RollbackPlan}`");
        builder.AppendLine($"- TraceArtifactPlan: `{report.TraceArtifactPlan}`");
        builder.AppendLine($"- ComparisonArtifactPlan: `{report.ComparisonArtifactPlan}`");
        builder.AppendLine($"- LatencyBaselinePlan: `{report.LatencyBaselinePlan}`");
        builder.AppendLine($"- AllocationBaselinePlan: `{report.AllocationBaselinePlan}`");
        AppendList(builder, "Adapter Inputs", report.AdapterInputs);
        AppendList(builder, "Adapter Outputs", report.AdapterOutputs);
        AppendList(builder, "Eligibility / Lifecycle / Risk Gate Order", report.GateOrder);
        AppendList(builder, "Allowed Actions", report.AllowedActions);
        AppendList(builder, "Forbidden Actions", report.ForbiddenActions);
        AppendMap(builder, "Source Reports", report.SourceReports);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("V5.1 is a design plan only. It does not bind IVectorIndexStore, switch runtime retrieval, write formal packages, mutate PackingPolicy, or mutate package output.");
        return builder.ToString();
    }

    private static ShadowFormalRetrievalAdapterPlanReport Build(
        string stage,
        ProjectStateAuditReport? projectStateAudit,
        VectorFormalPreviewFreezeReport? formalPreviewFreeze,
        ScopedRuntimeExperimentObservationFreezeReport? promotionDecision,
        GuardedScopedRuntimeExperimentReport? guardedRuntimeExperiment,
        VectorShadowPackageComparisonReport? shadowPackageComparison,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ShadowFormalRetrievalAdapterPlanReport? existingPlan,
        IReadOnlyDictionary<string, string>? sourceReports)
    {
        var blocked = new List<string>();
        var projectAuditPassed = projectStateAudit is not null
            && string.Equals(
                projectStateAudit.Recommendation,
                ProjectStateAuditRecommendations.ReadyForMainlineGapRepairPlanning,
                StringComparison.OrdinalIgnoreCase)
            && !projectStateAudit.FormalRetrievalAllowed
            && !projectStateAudit.RuntimeSwitchAllowed
            && !projectStateAudit.ReadyForRuntimeSwitch
            && !projectStateAudit.PackingPolicyChanged
            && !projectStateAudit.PackageOutputChanged;
        if (!projectAuditPassed)
        {
            blocked.Add("V50ProjectStateAuditNotPassed");
        }

        if (formalPreviewFreeze is null || !formalPreviewFreeze.FreezePassed)
        {
            blocked.Add("V4FormalPreviewFreezeMissingOrNotPassed");
        }

        if (promotionDecision is null || !promotionDecision.FreezePassed)
        {
            blocked.Add("V416PromotionDecisionMissingOrNotPassed");
        }

        if (guardedRuntimeExperiment is null || !guardedRuntimeExperiment.ExperimentPassed)
        {
            blocked.Add("V414GuardedRuntimeExperimentMissingOrNotPassed");
        }

        if (shadowPackageComparison is null || !shadowPackageComparison.GatePassed)
        {
            blocked.Add("V42ShadowPackageComparisonMissingOrNotPassed");
        }

        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (string.Equals(stage, "gate", StringComparison.OrdinalIgnoreCase) && existingPlan is null)
        {
            blocked.Add("ShadowFormalRetrievalAdapterPlanMissing");
        }

        AddBoundaryBlocks(blocked, projectStateAudit);
        AddBoundaryBlocks(blocked, formalPreviewFreeze);
        AddBoundaryBlocks(blocked, promotionDecision);
        AddBoundaryBlocks(blocked, guardedRuntimeExperiment);
        AddBoundaryBlocks(blocked, shadowPackageComparison);
        AddBoundaryBlocks(blocked, existingPlan);

        var fallbackPath = "Adapter failure returns current formal retrieval/package path without changing selected set.";
        var rollbackPlan = "Disable the shadow adapter plan/config and continue baseline formal package assembly; keep trace artifacts.";
        var tracePlan = "Emit vector/v5 shadow adapter traces with query, scope, candidate ids, gate decisions, and latency.";
        var comparisonPlan = "Write baseline-vs-shadow candidate comparison artifacts without feeding them to PackingPolicy.";
        var latencyPlan = "Record p50/p95 adapter planning latency against baseline package snapshot for every shadow run.";
        var allocationPlan = "Record bounded allocation estimates for candidate collection, gate projection, and serialization.";
        if (string.IsNullOrWhiteSpace(fallbackPath)
            || string.IsNullOrWhiteSpace(rollbackPlan)
            || string.IsNullOrWhiteSpace(tracePlan)
            || GateOrder.Length == 0)
        {
            blocked.Add("IncompleteAdapterPlan");
        }

        var passed = blocked.Count == 0;
        return new ShadowFormalRetrievalAdapterPlanReport
        {
            OperationId = $"shadow-formal-retrieval-adapter-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PlanPassed = passed,
            Recommendation = ResolveRecommendation(blocked, passed),
            AllowedMode = "PlanOnly",
            RequiredNextPhase = "ShadowFormalRetrievalAdapterDesignFreeze",
            AdapterInputs = AdapterInputs,
            AdapterOutputs = AdapterOutputs,
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = "read-only relation evidence / expansion preview",
            GateOrder = GateOrder,
            FallbackPath = fallbackPath,
            RollbackPlan = rollbackPlan,
            TraceArtifactPlan = tracePlan,
            ComparisonArtifactPlan = comparisonPlan,
            LatencyBaselinePlan = latencyPlan,
            AllocationBaselinePlan = allocationPlan,
            NoRuntimeMutationInvariant = true,
            AllowedActions = AllowedActions,
            ForbiddenActions = ForbiddenActions,
            V50ProjectStateAuditPassed = projectAuditPassed,
            V4FormalPreviewFreezeReadable = formalPreviewFreeze is not null,
            V416PromotionDecisionReadable = promotionDecision is not null,
            V414GuardedRuntimeExperimentReadable = guardedRuntimeExperiment is not null,
            V42ShadowPackageComparisonReadable = shadowPackageComparison is not null,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed ?? false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            VectorStoreBindingChanged = false,
            FormalPackageWritten = false,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BlockedReasons = blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool passed)
    {
        if (passed)
        {
            return ShadowFormalRetrievalAdapterPlanRecommendations.ReadyForShadowAdapterDesignFreeze;
        }

        if (blocked.Contains("FormalRetrievalAllowedUnexpected", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterPlanRecommendations.BlockedByFormalRetrievalAttempt;
        }

        if (blocked.Contains("RuntimeSwitchAllowedUnexpected", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterPlanRecommendations.BlockedByRuntimeSwitchAttempt;
        }

        if (blocked.Contains("V50ProjectStateAuditNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterPlanRecommendations.BlockedByMissingProjectStateAudit;
        }

        if (blocked.Contains("RuntimeChangeReadinessGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterPlanRecommendations.BlockedByRuntimeChangeGate;
        }

        if (blocked.Any(static reason => reason.Contains("Mutation", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("PackageWritten", StringComparison.OrdinalIgnoreCase)))
        {
            return ShadowFormalRetrievalAdapterPlanRecommendations.BlockedByPackageMutation;
        }

        if (blocked.Contains("IncompleteAdapterPlan", StringComparer.OrdinalIgnoreCase))
        {
            return ShadowFormalRetrievalAdapterPlanRecommendations.BlockedByIncompleteAdapterPlan;
        }

        if (blocked.Any(static reason => reason.Contains("MissingOrNotPassed", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Missing", StringComparison.OrdinalIgnoreCase)))
        {
            return ShadowFormalRetrievalAdapterPlanRecommendations.BlockedByMissingPrerequisiteGate;
        }

        return ShadowFormalRetrievalAdapterPlanRecommendations.KeepPreviewOnly;
    }

    private static void AddBoundaryBlocks(List<string> blocked, ProjectStateAuditReport? report)
    {
        if (report is null)
        {
            return;
        }

        if (report.FormalRetrievalAllowed)
        {
            blocked.Add("FormalRetrievalAllowedUnexpected");
        }

        if (report.RuntimeSwitchAllowed || report.ReadyForRuntimeSwitch)
        {
            blocked.Add("RuntimeSwitchAllowedUnexpected");
        }

        if (report.PackageOutputChanged || report.PackingPolicyChanged)
        {
            blocked.Add("PackageOrPackingPolicyMutationDetected");
        }
    }

    private static void AddBoundaryBlocks(List<string> blocked, VectorFormalPreviewFreezeReport? report)
    {
        if (report is null)
        {
            return;
        }

        if (report.FormalRetrievalAllowed)
        {
            blocked.Add("FormalRetrievalAllowedUnexpected");
        }

        if (report.RuntimeSwitchAllowed || report.ReadyForRuntimeSwitch || report.UseForRuntime)
        {
            blocked.Add("RuntimeSwitchAllowedUnexpected");
        }

        if (report.PackageOutputChanged || report.PackingPolicyChanged || report.RuntimeMutated)
        {
            blocked.Add("PackageOrRuntimeMutationDetected");
        }

        if (report.FormalPackageWritten)
        {
            blocked.Add("FormalPackageWrittenDetected");
        }
    }

    private static void AddBoundaryBlocks(List<string> blocked, ScopedRuntimeExperimentObservationFreezeReport? report)
    {
        if (report is null)
        {
            return;
        }

        if (report.FormalRetrievalAllowed)
        {
            blocked.Add("FormalRetrievalAllowedUnexpected");
        }

        if (report.RuntimeSwitchAllowed || report.ReadyForRuntimeSwitch || report.UseForRuntime)
        {
            blocked.Add("RuntimeSwitchAllowedUnexpected");
        }

        if (report.PackageOutputChanged || report.PackingPolicyChanged || report.RuntimeMutated || report.VectorStoreBindingChanged)
        {
            blocked.Add("PackageOrRuntimeMutationDetected");
        }

        if (report.FormalPackageWritten)
        {
            blocked.Add("FormalPackageWrittenDetected");
        }
    }

    private static void AddBoundaryBlocks(List<string> blocked, GuardedScopedRuntimeExperimentReport? report)
    {
        if (report is null)
        {
            return;
        }

        if (report.FormalRetrievalAllowed)
        {
            blocked.Add("FormalRetrievalAllowedUnexpected");
        }

        if (report.RuntimeSwitchAllowed || report.ReadyForRuntimeSwitch || report.UseForRuntime)
        {
            blocked.Add("RuntimeSwitchAllowedUnexpected");
        }

        if (report.PackageOutputChanged || report.PackingPolicyChanged || report.RuntimeMutated || report.VectorStoreBindingChanged)
        {
            blocked.Add("PackageOrRuntimeMutationDetected");
        }

        if (report.FormalPackageWritten)
        {
            blocked.Add("FormalPackageWrittenDetected");
        }
    }

    private static void AddBoundaryBlocks(List<string> blocked, VectorShadowPackageComparisonReport? report)
    {
        if (report is null)
        {
            return;
        }

        if (report.FormalRetrievalAllowed)
        {
            blocked.Add("FormalRetrievalAllowedUnexpected");
        }

        if (report.ReadyForRuntimeSwitch || report.UseForRuntime)
        {
            blocked.Add("RuntimeSwitchAllowedUnexpected");
        }

        if (report.PackageOutputChanged || report.PackingPolicyChanged || report.RuntimeMutated)
        {
            blocked.Add("PackageOrRuntimeMutationDetected");
        }
    }

    private static void AddBoundaryBlocks(List<string> blocked, ShadowFormalRetrievalAdapterPlanReport? report)
    {
        if (report is null)
        {
            return;
        }

        if (report.FormalRetrievalAllowed)
        {
            blocked.Add("FormalRetrievalAllowedUnexpected");
        }

        if (report.RuntimeSwitchAllowed || report.ReadyForRuntimeSwitch || report.UseForRuntime)
        {
            blocked.Add("RuntimeSwitchAllowedUnexpected");
        }

        if (report.PackageOutputChanged || report.PackingPolicyChanged || report.VectorStoreBindingChanged || report.FormalPackageWritten)
        {
            blocked.Add("PackageOrRuntimeMutationDetected");
        }
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
