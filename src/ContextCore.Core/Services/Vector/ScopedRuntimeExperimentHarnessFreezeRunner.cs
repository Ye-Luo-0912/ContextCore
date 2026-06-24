using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V4.10 dry-run harness freeze；只汇总 no-op harness readiness，不授权 runtime switch。
/// </summary>
public sealed class ScopedRuntimeExperimentHarnessFreezeRunner
{
    public ScopedRuntimeExperimentHarnessFreezeReport BuildGate(
        ExplicitScopedRuntimeExperimentProposalReport? proposal,
        ScopedRuntimeExperimentApprovalSummaryReport? approval,
        ScopedRuntimeExperimentNoOpHarnessReport? noOpHarness,
        ScopedRuntimeExperimentDesignFreezeReport? designFreeze,
        ServiceFoundationFreezeReport? serviceFoundationFreeze,
        ContextCoreFoundationFreezeReport? foundationReleaseCandidate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed)
    {
        var blocked = new List<string>();
        if (proposal is null || !proposal.ProposalPassed)
        {
            blocked.Add("ProposalGateNotPassed");
        }

        if (approval is null)
        {
            blocked.Add("ApprovalSummaryMissing");
        }
        else
        {
            if (!approval.ApprovalRecordExists)
            {
                blocked.Add("ApprovalRecordMissing");
            }

            if (!string.Equals(approval.ApprovalMode, ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("UnsafeApprovalMode");
            }

            if (approval.Expired)
            {
                blocked.Add("ApprovalExpired");
            }

            if (approval.Revoked)
            {
                blocked.Add("ApprovalRevoked");
            }
        }

        if (noOpHarness is null || !noOpHarness.HarnessPassed)
        {
            blocked.Add("NoOpHarnessGateNotPassed");
        }

        if (designFreeze is null || !designFreeze.FreezePassed)
        {
            blocked.Add("ScopedRuntimeExperimentDesignFreezeGateNotPassed");
        }

        if (serviceFoundationFreeze is null || !serviceFoundationFreeze.FreezePassed)
        {
            blocked.Add("ServiceFoundationFreezeGateNotPassed");
        }

        if (foundationReleaseCandidate is null || !foundationReleaseCandidate.FreezePassed)
        {
            blocked.Add("FoundationReleaseCandidateGateNotPassed");
        }

        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (!p15GatePassed)
        {
            blocked.Add("P15GateNotPassed");
        }

        if (noOpHarness is not null)
        {
            if (noOpHarness.RuntimeMutated)
            {
                blocked.Add("RuntimeMutated");
            }

            if (noOpHarness.VectorStoreBindingChanged || noOpHarness.DiBindingChanged)
            {
                blocked.Add("VectorStoreBindingChanged");
            }

            if (noOpHarness.FormalPackageWritten)
            {
                blocked.Add("FormalPackageWritten");
            }

            if (noOpHarness.PackingPolicyChanged)
            {
                blocked.Add("PackingPolicyChanged");
            }

            if (noOpHarness.PackageOutputChanged)
            {
                blocked.Add("PackageOutputChanged");
            }

            if (noOpHarness.FormalRetrievalAllowed)
            {
                blocked.Add("FormalRetrievalAllowed");
            }

            if (noOpHarness.RuntimeSwitchAllowed)
            {
                blocked.Add("RuntimeSwitchAllowed");
            }

            if (noOpHarness.ReadyForRuntimeSwitch)
            {
                blocked.Add("ReadyForRuntimeSwitch");
            }

            if (noOpHarness.RiskAfterPolicy != 0
                || noOpHarness.MustNotHitRiskAfterPolicy != 0
                || noOpHarness.LifecycleRiskAfterPolicy != 0)
            {
                blocked.Add("RiskCountNonZero");
            }

            if (noOpHarness.FormalOutputChanged != 0)
            {
                blocked.Add("FormalOutputChangedNonZero");
            }
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        return new ScopedRuntimeExperimentHarnessFreezeReport
        {
            OperationId = $"vector-scoped-runtime-experiment-harness-freeze-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = passed,
            Recommendation = passed
                ? ScopedRuntimeExperimentHarnessFreezeRecommendations.ReadyForGuardedRuntimeExperimentPlanning
                : ResolveRecommendation(distinctBlocked),
            ProposalId = proposal?.ProposalId ?? approval?.ProposalId ?? noOpHarness?.ProposalId ?? string.Empty,
            ApprovalId = approval?.LatestApprovalId ?? noOpHarness?.ApprovalId ?? string.Empty,
            ApprovalMode = approval?.ApprovalMode ?? string.Empty,
            HarnessStatus = noOpHarness?.HarnessPassed == true ? "Passed" : "Blocked",
            RuntimeMutated = noOpHarness?.RuntimeMutated ?? false,
            VectorStoreBindingChanged = noOpHarness?.VectorStoreBindingChanged == true || noOpHarness?.DiBindingChanged == true,
            FormalPackageWritten = noOpHarness?.FormalPackageWritten ?? false,
            PackingPolicyChanged = noOpHarness?.PackingPolicyChanged ?? false,
            PackageOutputChanged = noOpHarness?.PackageOutputChanged ?? false,
            FormalRetrievalAllowed = noOpHarness?.FormalRetrievalAllowed ?? false,
            RuntimeSwitchAllowed = noOpHarness?.RuntimeSwitchAllowed ?? false,
            ReadyForRuntimeSwitch = noOpHarness?.ReadyForRuntimeSwitch ?? false,
            RiskAfterPolicy = noOpHarness?.RiskAfterPolicy ?? 0,
            FormalOutputChanged = noOpHarness?.FormalOutputChanged ?? 0,
            AllowedMode = "NoOpHarnessOnly / ExplicitScopedExperimentPlanningOnly",
            ForbiddenActions =
            [
                "RuntimeSwitch",
                "FormalRetrieval",
                "FormalPackageWrite",
                "DIBindingMutation",
                "VectorStoreBindingMutation",
                "PackingPolicyMutation",
                "PackageOutputMutation",
                "GlobalDefaultOn"
            ],
            NextAllowedPhase = "GuardedScopedRuntimeExperimentPlan",
            ProposalGatePassed = proposal?.ProposalPassed ?? false,
            ApprovalSummaryPassed = approval?.ApprovalRecordExists == true
                && !approval.Expired
                && !approval.Revoked
                && string.Equals(approval.ApprovalMode, ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly, StringComparison.OrdinalIgnoreCase),
            NoOpHarnessGatePassed = noOpHarness?.HarnessPassed ?? false,
            DesignFreezeGatePassed = designFreeze?.FreezePassed ?? false,
            ServiceFoundationFreezeGatePassed = serviceFoundationFreeze?.FreezePassed ?? false,
            FoundationReleaseCandidateGatePassed = foundationReleaseCandidate?.FreezePassed ?? false,
            RuntimeChangeReadinessGatePassed = runtimeChangeGate?.Passed ?? false,
            P15GatePassed = p15GatePassed,
            BlockedReasons = distinctBlocked
        };
    }

    public static string BuildMarkdown(ScopedRuntimeExperimentHarnessFreezeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Scoped Runtime Experiment Harness Freeze Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- ApprovalId: `{report.ApprovalId}`");
        builder.AppendLine($"- ApprovalMode: `{report.ApprovalMode}`");
        builder.AppendLine($"- HarnessStatus: `{report.HarnessStatus}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- NextAllowedPhase: `{report.NextAllowedPhase}`");
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
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
        AppendList(builder, "Forbidden Actions", report.ForbiddenActions);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("V4.10 freeze 通过后仍不代表 runtime approval；它只允许进入 GuardedScopedRuntimeExperimentPlan。");
        return builder.ToString();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static reason => reason.Contains("ApprovalRecordMissing", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("ApprovalSummaryMissing", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByMissingApproval;
        }

        if (blocked.Any(static reason => reason.Contains("Expired", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByExpiredApproval;
        }

        if (blocked.Any(static reason => reason.Contains("Revoked", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByRevokedApproval;
        }

        if (blocked.Any(static reason => reason.Contains("UnsafeApprovalMode", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByUnsafeApprovalMode;
        }

        if (blocked.Any(static reason => reason.Contains("Harness", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByHarnessFailure;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Formal", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Packing", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Package", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("VectorStore", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByRuntimeMutation;
        }

        if (blocked.Any(static reason => reason.Contains("Proposal", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByMissingProposal;
        }

        if (blocked.Any(static reason => reason.Contains("Gate", StringComparison.OrdinalIgnoreCase)))
        {
            return ScopedRuntimeExperimentHarnessFreezeRecommendations.BlockedByMissingGate;
        }

        return ScopedRuntimeExperimentHarnessFreezeRecommendations.KeepPreviewOnly;
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
}
