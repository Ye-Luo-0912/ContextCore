using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Scoped runtime experiment design freeze；只冻结设计与边界，不启用 runtime。
/// </summary>
public sealed class ScopedRuntimeExperimentDesignFreezeRunner
{
    public ScopedRuntimeExperimentDesignFreezeReport BuildGate(
        ContextCoreFoundationFreezeReport? foundationReleaseCandidate,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        VectorFormalPreviewFreezeReport? vectorFormalPreviewFreeze,
        ExplicitScopedRuntimeExperimentPlanReport? scopedRuntimeExperimentGate,
        ScopedRuntimeExperimentDryRunObservationReport? dryRunObservationGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        IReadOnlyDictionary<string, string>? sourceReports = null)
    {
        var blocked = new List<string>();
        if (foundationReleaseCandidate is null
            || !foundationReleaseCandidate.FreezePassed
            || !string.Equals(foundationReleaseCandidate.Recommendation, ContextCoreFoundationFreezeRecommendations.ReadyForReleaseCandidate, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("FoundationReleaseCandidateGateNotPassed");
        }

        if (serviceFoundationFreeze is null || !serviceFoundationFreeze.FreezePassed)
        {
            blocked.Add("ServiceFoundationFreezeGateNotPassed");
        }

        if (vectorFormalPreviewFreeze is null
            || !vectorFormalPreviewFreeze.FreezePassed
            || !string.Equals(vectorFormalPreviewFreeze.Recommendation, VectorFormalPreviewFreezeRecommendations.ReadyForScopedOptInPreview, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("VectorFormalPreviewFreezeGateNotPassed");
        }

        if (scopedRuntimeExperimentGate is null
            || !scopedRuntimeExperimentGate.PlanPassed
            || !string.Equals(scopedRuntimeExperimentGate.Recommendation, ExplicitScopedRuntimeExperimentRecommendations.ReadyForExplicitScopedRuntimeExperimentDryRun, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ScopedRuntimeExperimentGateNotPassed");
        }

        if (dryRunObservationGate is null
            || !dryRunObservationGate.GatePassed
            || !string.Equals(dryRunObservationGate.Recommendation, ScopedRuntimeExperimentDryRunObservationRecommendations.ReadyForScopedRuntimeExperimentDesignFreeze, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("DryRunObservationGateNotPassed");
        }

        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (!p15GatePassed)
        {
            blocked.Add("P15GateNotPassed");
        }

        var riskAfterPolicy = dryRunObservationGate?.RiskAfterPolicy ?? 0;
        var mustNotRisk = dryRunObservationGate?.MustNotHitRiskAfterPolicy ?? 0;
        var lifecycleRisk = dryRunObservationGate?.LifecycleRiskAfterPolicy ?? 0;
        var formalOutputChanged = dryRunObservationGate?.FormalOutputChanged ?? 0;
        var runtimeMutated = dryRunObservationGate?.RuntimeMutated ?? false;
        var vectorStoreBindingChanged = dryRunObservationGate?.VectorStoreBindingChanged ?? false;
        var packingPolicyChanged = dryRunObservationGate?.PackingPolicyChanged ?? false;
        var packageOutputChanged = dryRunObservationGate?.PackageOutputChanged ?? false;
        var formalPackageWritten = dryRunObservationGate?.FormalPackageWritten ?? false;
        var nonAllowlistedScopeLeakCount = dryRunObservationGate?.NonAllowlistedScopeLeakCount ?? 0;
        var rollbackPlanAvailable = dryRunObservationGate?.RollbackPlanAvailable ?? false;

        if (riskAfterPolicy != 0 || mustNotRisk != 0 || lifecycleRisk != 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (formalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (runtimeMutated)
        {
            blocked.Add("RuntimeMutated");
        }

        if (vectorStoreBindingChanged)
        {
            blocked.Add("VectorStoreBindingChanged");
        }

        if (packingPolicyChanged)
        {
            blocked.Add("PackingPolicyChanged");
        }

        if (packageOutputChanged)
        {
            blocked.Add("PackageOutputChanged");
        }

        if (formalPackageWritten)
        {
            blocked.Add("FormalPackageWritten");
        }

        if (nonAllowlistedScopeLeakCount != 0)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        if (!rollbackPlanAvailable)
        {
            blocked.Add("RollbackPlanMissing");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var freezePassed = distinctBlocked.Length == 0;

        return new ScopedRuntimeExperimentDesignFreezeReport
        {
            OperationId = $"vector-scoped-runtime-experiment-design-freeze-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            Recommendation = freezePassed
                ? ScopedRuntimeExperimentDesignFreezeRecommendations.ReadyForRuntimeExperimentProposal
                : ResolveRecommendation(distinctBlocked),
            DesignStatus = freezePassed
                ? ScopedRuntimeExperimentDesignFreezeStatuses.Frozen
                : ScopedRuntimeExperimentDesignFreezeStatuses.KeepPreviewOnly,
            AllowedMode = "ExplicitScopedRuntimeExperimentOnly",
            AllowlistedScopeCount = dryRunObservationGate?.AllowlistedScopeCount ?? scopedRuntimeExperimentGate?.AllowlistedScopeCount ?? 0,
            ObservationRunCount = dryRunObservationGate?.ObservationRunCount ?? 0,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            FormalPackageWritten = formalPackageWritten,
            NonAllowlistedScopeLeakCount = nonAllowlistedScopeLeakCount,
            RollbackPlanAvailable = rollbackPlanAvailable,
            ReadyForRuntimeExperimentProposal = freezePassed,
            ReadyForRuntimeSwitch = false,
            RuntimeSwitchAllowed = false,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            FormalPackageWriteAllowed = false,
            PackingPolicyIntegrationAllowed = false,
            GlobalDefaultOnAllowed = false,
            FoundationReleaseCandidateGatePassed = foundationReleaseCandidate?.FreezePassed ?? false,
            ServiceFoundationFreezeGatePassed = serviceFoundationFreeze?.FreezePassed ?? false,
            VectorFormalPreviewFreezeGatePassed = vectorFormalPreviewFreeze?.FreezePassed ?? false,
            ScopedRuntimeExperimentGatePassed = scopedRuntimeExperimentGate?.PlanPassed ?? false,
            DryRunObservationGatePassed = dryRunObservationGate?.GatePassed ?? false,
            RuntimeChangeReadinessGatePassed = runtimeChangeGate?.Passed ?? false,
            P15GatePassed = p15GatePassed,
            AllowedActions =
            [
                "SelectedScopeExperimentPlanning",
                "SelectedScopeDryRunObservation",
                "SelectedScopeRuntimeExperimentProposal",
                "RollbackPlanValidation",
                "MetricsCollectionPlan"
            ],
            ForbiddenActions =
            [
                "GlobalRuntimeSwitch",
                "NonAllowlistedScopeUse",
                "FormalIVectorIndexStoreBinding",
                "FormalPackageWrite",
                "PackingPolicyMutation",
                "PackageOutputMutation",
                "DisablingRuntimeChangeGate",
                "FormalRetrievalWithoutExplicitLaterGate"
            ],
            BlockedReasons = distinctBlocked,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    public static string BuildMarkdown(ScopedRuntimeExperimentDesignFreezeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Vector Scoped Runtime Experiment Design Freeze Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- DesignStatus: `{report.DesignStatus}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- AllowlistedScopeCount: `{report.AllowlistedScopeCount}`");
        builder.AppendLine($"- ObservationRunCount: `{report.ObservationRunCount}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- NonAllowlistedScopeLeakCount: `{report.NonAllowlistedScopeLeakCount}`");
        builder.AppendLine($"- RollbackPlanAvailable: `{report.RollbackPlanAvailable}`");
        builder.AppendLine($"- ReadyForRuntimeExperimentProposal: `{report.ReadyForRuntimeExperimentProposal}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalPackageWriteAllowed: `{report.FormalPackageWriteAllowed}`");
        builder.AppendLine($"- PackingPolicyIntegrationAllowed: `{report.PackingPolicyIntegrationAllowed}`");
        builder.AppendLine($"- GlobalDefaultOnAllowed: `{report.GlobalDefaultOnAllowed}`");
        builder.AppendLine($"- FoundationReleaseCandidateGatePassed: `{report.FoundationReleaseCandidateGatePassed}`");
        builder.AppendLine($"- ServiceFoundationFreezeGatePassed: `{report.ServiceFoundationFreezeGatePassed}`");
        builder.AppendLine($"- VectorFormalPreviewFreezeGatePassed: `{report.VectorFormalPreviewFreezeGatePassed}`");
        builder.AppendLine($"- ScopedRuntimeExperimentGatePassed: `{report.ScopedRuntimeExperimentGatePassed}`");
        builder.AppendLine($"- DryRunObservationGatePassed: `{report.DryRunObservationGatePassed}`");
        builder.AppendLine($"- RuntimeChangeReadinessGatePassed: `{report.RuntimeChangeReadinessGatePassed}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        AppendList(builder, "Allowed Actions", report.AllowedActions);
        AppendList(builder, "Forbidden Actions", report.ForbiddenActions);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- V4.7 只冻结 scoped runtime experiment design；不启用 runtime。");
        builder.AppendLine("- 不允许 global runtime switch、正式 `IVectorIndexStore` 绑定、正式 package 写入、PackingPolicy mutation 或 package output mutation。");
        builder.AppendLine("- `ReadyForRuntimeExperimentProposal=true` 只允许进入后续显式 proposal 阶段，不等于 runtime switch allowed。");
        return builder.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static reason => reason.Contains("DryRunObservation", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByMissingDryRunObservation;
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByRisk;
        }

        if (blocked.Any(static reason => reason.Contains("FormalOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeMutated", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("FormalPackage", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByRuntimeMutation;
        }

        if (blocked.Any(static reason => reason.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByVectorStoreBindingMutation;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("ScopeLeak", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByScopeLeak;
        }

        if (blocked.Any(static reason => reason.Contains("RollbackPlan", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentDesignFreezeRecommendations.BlockedByMissingRollbackPlan;
        }

        return ScopedRuntimeExperimentDesignFreezeRecommendations.KeepPreviewOnly;
    }

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

    private static void AppendMap(StringBuilder builder, string title, IReadOnlyDictionary<string, string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {pair.Key}: `{pair.Value}`");
        }
    }
}
