using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V4.16 observation freeze / promotion decision；只读取 V4.14/V4.15 gate 并冻结下一步计划，不启用 runtime。
/// </summary>
public sealed class ScopedRuntimeExperimentObservationFreezeRunner
{
    public ScopedRuntimeExperimentObservationFreezeReport BuildObservationFreeze(
        GuardedScopedRuntimeExperimentReport? v414Gate,
        ScopedRuntimeExperimentObservationWindowReport? v415Gate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build("observation-freeze", v414Gate, v415Gate, runtimeChangeGate, p15GatePassed, sourceReports);

    public ScopedRuntimeExperimentObservationFreezeReport BuildPromotionDecision(
        GuardedScopedRuntimeExperimentReport? v414Gate,
        ScopedRuntimeExperimentObservationWindowReport? v415Gate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build("promotion-decision", v414Gate, v415Gate, runtimeChangeGate, p15GatePassed, sourceReports);

    public static string BuildMarkdown(string title, ScopedRuntimeExperimentObservationFreezeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- PromotionDecision: `{report.PromotionDecision}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ObservationWindowId: `{report.ObservationWindowId}`");
        builder.AppendLine($"- ProposalId/ApprovalId: `{report.ProposalId}` / `{report.ApprovalId}`");
        builder.AppendLine($"- V4.14/V4.15 gates: `{report.V414GatePassed}` / `{report.V415GatePassed}`");
        builder.AppendLine($"- RuntimeChangeGate/P15: `{report.RuntimeChangeGatePassed}` / `{report.P15GatePassed}`");
        builder.AppendLine($"- ObservationRunCount: `{report.ObservationRunCount}`");
        builder.AppendLine($"- RequestCount: `{report.RequestCount}`");
        builder.AppendLine($"- ExperimentRouteHitCount: `{report.ExperimentRouteHitCount}`");
        builder.AppendLine($"- NonAllowlistedScopeLeakCount: `{report.NonAllowlistedScopeLeakCount}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- KillSwitchAvailable/SmokePassed: `{report.KillSwitchAvailable}` / `{report.KillSwitchSmokePassed}`");
        builder.AppendLine($"- RollbackVerified: `{report.RollbackVerified}`");
        builder.AppendLine($"- TraceCompleteness: `{report.TraceCompleteness}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        AppendMap(builder, "Source Reports", report.SourceReports);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("V4.16 only freezes the observation decision. It does not enable formal retrieval, runtime switch, formal package writes, IVectorIndexStore binding changes, PackingPolicy changes, or package output mutation.");
        return builder.ToString();
    }

    private static ScopedRuntimeExperimentObservationFreezeReport Build(
        string stage,
        GuardedScopedRuntimeExperimentReport? v414Gate,
        ScopedRuntimeExperimentObservationWindowReport? v415Gate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        IReadOnlyDictionary<string, string>? sourceReports)
    {
        var blocked = new List<string>();
        if (v414Gate is null || !v414Gate.ExperimentPassed)
        {
            blocked.Add("V414GuardedScopedRuntimeExperimentGateNotPassed");
        }

        if (v415Gate is null || !v415Gate.ObservationPassed)
        {
            blocked.Add("V415ObservationWindowGateNotPassed");
        }

        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (!p15GatePassed)
        {
            blocked.Add("P15GateNotPassed");
        }

        if (v415Gate is not null)
        {
            if (v415Gate.RiskAfterPolicy > 0
                || v415Gate.MustNotHitRiskAfterPolicy > 0
                || v415Gate.LifecycleRiskAfterPolicy > 0)
            {
                blocked.Add("RiskDetected");
            }

            if (v415Gate.FormalOutputChanged > 0
                || v415Gate.PackageOutputChanged
                || v415Gate.PackingPolicyChanged
                || v415Gate.FormalPackageWritten)
            {
                blocked.Add("OutputChangeDetected");
            }

            if (v415Gate.NonAllowlistedScopeLeakCount > 0)
            {
                blocked.Add("NonAllowlistedScopeLeakDetected");
            }

            if (v415Gate.TraceCompleteness < 100)
            {
                blocked.Add("TraceCompletenessBelow100Percent");
            }

            if (v415Gate.RuntimeMutated
                || v415Gate.VectorStoreBindingChanged
                || v415Gate.FormalRetrievalAllowed
                || v415Gate.RuntimeSwitchAllowed
                || v415Gate.ReadyForRuntimeSwitch
                || v415Gate.UseForRuntime
                || v415Gate.GlobalDefaultOn)
            {
                blocked.Add("RuntimeMutationDetected");
            }

            if (!v415Gate.KillSwitchAvailable || !v415Gate.KillSwitchSmokePassed)
            {
                blocked.Add("KillSwitchNotPassed");
            }

            if (!v415Gate.RollbackVerified)
            {
                blocked.Add("RollbackNotVerified");
            }
        }

        var freezePassed = blocked.Count == 0;
        var decision = ResolveDecision(blocked, freezePassed);
        return new ScopedRuntimeExperimentObservationFreezeReport
        {
            OperationId = $"vector-scoped-runtime-experiment-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            PromotionDecision = decision,
            Recommendation = decision,
            ObservationWindowId = v415Gate?.ObservationWindowId ?? string.Empty,
            ProposalId = v415Gate?.ProposalId ?? v414Gate?.ProposalId ?? string.Empty,
            ApprovalId = v415Gate?.ApprovalId ?? v414Gate?.ApprovalId ?? string.Empty,
            V414GatePassed = v414Gate?.ExperimentPassed ?? false,
            V415GatePassed = v415Gate?.ObservationPassed ?? false,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed ?? false,
            P15GatePassed = p15GatePassed,
            ObservationRunCount = v415Gate?.ObservationRunCount ?? 0,
            RequestCount = v415Gate?.RequestCount ?? 0,
            ExperimentRouteHitCount = v415Gate?.ExperimentRouteHitCount ?? 0,
            NonAllowlistedScopeLeakCount = v415Gate?.NonAllowlistedScopeLeakCount ?? 0,
            RiskAfterPolicy = v415Gate?.RiskAfterPolicy ?? 0,
            MustNotHitRiskAfterPolicy = v415Gate?.MustNotHitRiskAfterPolicy ?? 0,
            LifecycleRiskAfterPolicy = v415Gate?.LifecycleRiskAfterPolicy ?? 0,
            FormalOutputChanged = v415Gate?.FormalOutputChanged ?? 0,
            PackageOutputChanged = v415Gate?.PackageOutputChanged ?? false,
            PackingPolicyChanged = v415Gate?.PackingPolicyChanged ?? false,
            RuntimeMutated = v415Gate?.RuntimeMutated ?? false,
            VectorStoreBindingChanged = v415Gate?.VectorStoreBindingChanged ?? false,
            FormalPackageWritten = v415Gate?.FormalPackageWritten ?? false,
            KillSwitchAvailable = v415Gate?.KillSwitchAvailable ?? false,
            KillSwitchSmokePassed = v415Gate?.KillSwitchSmokePassed ?? false,
            RollbackVerified = v415Gate?.RollbackVerified ?? false,
            TraceCompleteness = v415Gate?.TraceCompleteness ?? 0,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            SourceReports = sourceReports ?? new Dictionary<string, string>(),
            BlockedReasons = blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static string ResolveDecision(IReadOnlyList<string> blocked, bool freezePassed)
    {
        if (freezePassed)
        {
            return ScopedRuntimeExperimentObservationFreezeDecisions.ReadyForFormalRetrievalIntegrationPlan;
        }

        if (blocked.Contains("RiskDetected", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationFreezeDecisions.BlockedByRisk;
        }

        if (blocked.Contains("OutputChangeDetected", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationFreezeDecisions.BlockedByOutputChange;
        }

        if (blocked.Contains("NonAllowlistedScopeLeakDetected", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationFreezeDecisions.BlockedByScopeLeak;
        }

        if (blocked.Contains("TraceCompletenessBelow100Percent", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationFreezeDecisions.BlockedByTraceGap;
        }

        if (blocked.Contains("RuntimeMutationDetected", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationFreezeDecisions.BlockedByRuntimeMutation;
        }

        if (blocked.Contains("V415ObservationWindowGateNotPassed", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("V414GuardedScopedRuntimeExperimentGateNotPassed", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("RuntimeChangeReadinessGateNotPassed", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("P15GateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationFreezeDecisions.KeepPreviewOnly;
        }

        return ScopedRuntimeExperimentObservationFreezeDecisions.KeepScopedObservation;
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
