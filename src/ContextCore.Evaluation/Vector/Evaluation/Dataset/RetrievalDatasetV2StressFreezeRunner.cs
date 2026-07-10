using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Dataset V2 stress freeze gate；只汇总离线报告，不把 preview profile 接入正式检索。
/// </summary>
public sealed class RetrievalDatasetV2StressFreezeRunner
{
    private const double Epsilon = 0.000000001d;

    public RetrievalDatasetV2StressFreezeReport BuildReport(
        RetrievalDatasetV2MaterializationReport? materializationGate,
        RetrievalDatasetV2ReadinessGateReport? smallSetReadinessGate,
        RetrievalDatasetV2StressReport? stressReadinessGate,
        RetrievalDatasetV2StressReport? leakageAudit,
        RetrievalDatasetV2StressReport? anchorDominanceAudit,
        RetrievalDatasetV2StressRecallFailureTriageReport? stressFailureTriage,
        HybridUnionScoringRepairReport? hybridScoringRepairGate,
        HybridScoringRiskRegressionTriageReport? hybridScoringRiskTriage,
        IReadOnlyDictionary<string, string>? sourceReports = null)
    {
        var blocked = new List<string>();
        AddMissing(blocked, materializationGate, "MissingMaterializationGateReport");
        AddMissing(blocked, smallSetReadinessGate, "MissingSmallSetReadinessGateReport");
        AddMissing(blocked, stressReadinessGate, "MissingStressReadinessGateReport");
        AddMissing(blocked, leakageAudit, "MissingLeakageAuditReport");
        AddMissing(blocked, anchorDominanceAudit, "MissingAnchorDominanceAuditReport");
        AddMissing(blocked, stressFailureTriage, "MissingStressFailureTriageReport");
        AddMissing(blocked, hybridScoringRepairGate, "MissingHybridScoringRepairGateReport");
        AddMissing(blocked, hybridScoringRiskTriage, "MissingHybridScoringRiskTriageReport");

        var bestProfile = hybridScoringRepairGate?.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.ProfileName, hybridScoringRepairGate.BestProfileName, StringComparison.OrdinalIgnoreCase));

        if (materializationGate is not null)
        {
            if (!materializationGate.GatePassed)
            {
                blocked.Add("MaterializationGateNotPassed");
            }

            if (materializationGate.UseForRuntime || materializationGate.FormalRetrievalAllowed)
            {
                blocked.Add("MaterializedDatasetRuntimeFlagEnabled");
            }
        }

        if (smallSetReadinessGate is not null)
        {
            if (!smallSetReadinessGate.GatePassed)
            {
                blocked.Add("SmallSetReadinessGateNotPassed");
            }

            if (smallSetReadinessGate.UseForRuntime || smallSetReadinessGate.FormalRetrievalAllowed)
            {
                blocked.Add("SmallSetReadinessRuntimeFlagEnabled");
            }
        }

        if (stressReadinessGate is not null)
        {
            AddRiskBlocks(blocked, stressReadinessGate.RiskAfterPolicy, stressReadinessGate.MustNotHitRiskAfterPolicy, stressReadinessGate.LifecycleRiskAfterPolicy);
            if (stressReadinessGate.FormalOutputChanged != 0)
            {
                blocked.Add("StressReadinessFormalOutputChanged");
            }

            if (stressReadinessGate.UseForRuntime || stressReadinessGate.FormalRetrievalAllowed)
            {
                blocked.Add("StressReadinessRuntimeFlagEnabled");
            }
        }

        if (leakageAudit is not null && leakageAudit.LeakageIssueCount != 0)
        {
            blocked.Add("LeakageIssueCountNonZero");
        }

        if (anchorDominanceAudit is not null && anchorDominanceAudit.AnchorDominanceScore > Epsilon)
        {
            blocked.Add("AnchorDominanceScoreNonZero");
        }

        if (stressFailureTriage is not null
            && (stressFailureTriage.UseForRuntime || stressFailureTriage.FormalRetrievalAllowed))
        {
            blocked.Add("StressFailureTriageRuntimeFlagEnabled");
        }

        if (hybridScoringRepairGate is not null)
        {
            if (!hybridScoringRepairGate.GatePassed)
            {
                blocked.Add("HybridScoringRepairGateNotPassed");
            }

            if (!string.Equals(hybridScoringRepairGate.Recommendation, HybridUnionScoringRepairRecommendations.ReadyForDatasetV2StressFreeze, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("HybridScoringRepairRecommendationNotReady");
            }

            if (hybridScoringRepairGate.UseForRuntime || hybridScoringRepairGate.FormalRetrievalAllowed)
            {
                blocked.Add("HybridScoringRepairRuntimeFlagEnabled");
            }
        }

        if (bestProfile is null && hybridScoringRepairGate is not null)
        {
            blocked.Add("HybridScoringRepairBestProfileMissing");
        }

        if (bestProfile is not null)
        {
            AddRiskBlocks(blocked, bestProfile.RiskAfterPolicy, bestProfile.MustNotHitRiskAfterPolicy, bestProfile.LifecycleRiskAfterPolicy);
            if (bestProfile.FormalOutputChanged != 0)
            {
                blocked.Add("HybridScoringRepairFormalOutputChanged");
            }
        }

        if (hybridScoringRiskTriage is not null)
        {
            if (hybridScoringRiskTriage.RiskCandidateCount != 0)
            {
                blocked.Add("HybridScoringRiskCandidateCountNonZero");
            }

            if (hybridScoringRiskTriage.RiskProjectionMismatchCount != 0)
            {
                blocked.Add("HybridScoringRiskProjectionMismatchNonZero");
            }

            if (!string.Equals(hybridScoringRiskTriage.Recommendation, HybridScoringRiskRegressionRecommendations.ReadyForSafeScoringRepair, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("HybridScoringRiskTriageRecommendationNotReady");
            }

            if (hybridScoringRiskTriage.UseForRuntime || hybridScoringRiskTriage.FormalRetrievalAllowed)
            {
                blocked.Add("HybridScoringRiskTriageRuntimeFlagEnabled");
            }
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var freezePassed = distinctBlocked.Length == 0;

        return new RetrievalDatasetV2StressFreezeReport
        {
            OperationId = $"retrieval-dataset-v2-stress-freeze-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetId = ResolveDatasetId(materializationGate, smallSetReadinessGate, stressReadinessGate, hybridScoringRepairGate, hybridScoringRiskTriage),
            FreezePassed = freezePassed,
            DatasetV2Stress = freezePassed
                ? RetrievalDatasetV2StressFreezeStatuses.ReadyForV4RecheckInput
                : RetrievalDatasetV2StressFreezeStatuses.KeepPreviewOnly,
            BestPreviewProfile = bestProfile?.ProfileName ?? hybridScoringRepairGate?.BestProfileName ?? string.Empty,
            StressRecall = bestProfile?.RecallAfterPolicy ?? stressReadinessGate?.HybridRecall ?? 0,
            HoldoutRecall = bestProfile?.HoldoutRecallAfterPolicy ?? stressReadinessGate?.HoldoutHybridRecall ?? 0,
            RiskAfterPolicy = Math.Max(bestProfile?.RiskAfterPolicy ?? 0, stressReadinessGate?.RiskAfterPolicy ?? 0),
            MustNotHitRiskAfterPolicy = Math.Max(bestProfile?.MustNotHitRiskAfterPolicy ?? 0, stressReadinessGate?.MustNotHitRiskAfterPolicy ?? 0),
            LifecycleRiskAfterPolicy = Math.Max(bestProfile?.LifecycleRiskAfterPolicy ?? 0, stressReadinessGate?.LifecycleRiskAfterPolicy ?? 0),
            FormalOutputChanged = Math.Max(bestProfile?.FormalOutputChanged ?? 0, stressReadinessGate?.FormalOutputChanged ?? 0),
            LeakageIssueCount = leakageAudit?.LeakageIssueCount ?? stressReadinessGate?.LeakageIssueCount ?? 0,
            AnchorDominanceScore = anchorDominanceAudit?.AnchorDominanceScore ?? stressReadinessGate?.AnchorDominanceScore ?? 0,
            MaterializationGatePassed = materializationGate?.GatePassed ?? false,
            SmallSetReadinessGatePassed = smallSetReadinessGate?.GatePassed ?? false,
            StressReadinessRecommendation = stressReadinessGate?.Recommendation ?? string.Empty,
            StressFailureTriageCompleted = stressFailureTriage is not null,
            HybridScoringRepairGatePassed = hybridScoringRepairGate?.GatePassed ?? false,
            HybridScoringRiskCandidateCount = hybridScoringRiskTriage?.RiskCandidateCount ?? 0,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            V4RecheckAllowed = freezePassed,
            ReadyForFormalRetrieval = false,
            Recommendation = freezePassed
                ? RetrievalDatasetV2StressFreezeRecommendations.ReadyForV4RecheckInput
                : ResolveRecommendation(distinctBlocked),
            BlockedReasons = distinctBlocked,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    public static string BuildMarkdown(RetrievalDatasetV2StressFreezeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Retrieval Dataset V2 Stress Freeze Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- DatasetV2Stress: `{report.DatasetV2Stress}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- DatasetId: `{Blank(report.DatasetId)}`");
        builder.AppendLine($"- BestPreviewProfile: `{Blank(report.BestPreviewProfile)}`");
        builder.AppendLine($"- StressRecall: `{report.StressRecall:P2}`");
        builder.AppendLine($"- HoldoutRecall: `{report.HoldoutRecall:P2}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- LeakageIssueCount: `{report.LeakageIssueCount}`");
        builder.AppendLine($"- AnchorDominanceScore: `{report.AnchorDominanceScore:F4}`");
        builder.AppendLine($"- V4RecheckAllowed: `{report.V4RecheckAllowed}`");
        builder.AppendLine($"- ReadyForFormalRetrieval: `{report.ReadyForFormalRetrieval}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine();
        builder.AppendLine("## Preconditions");
        builder.AppendLine();
        builder.AppendLine($"- MaterializationGatePassed: `{report.MaterializationGatePassed}`");
        builder.AppendLine($"- SmallSetReadinessGatePassed: `{report.SmallSetReadinessGatePassed}`");
        builder.AppendLine($"- StressReadinessRecommendation: `{Blank(report.StressReadinessRecommendation)}`");
        builder.AppendLine($"- StressFailureTriageCompleted: `{report.StressFailureTriageCompleted}`");
        builder.AppendLine($"- HybridScoringRepairGatePassed: `{report.HybridScoringRepairGatePassed}`");
        builder.AppendLine($"- HybridScoringRiskCandidateCount: `{report.HybridScoringRiskCandidateCount}`");
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- `ReadyForV4RecheckInput` 只表示可作为 V4 复核输入。");
        builder.AppendLine("- `ReadyForFormalRetrieval` 保持 `false`。");
        builder.AppendLine("- `post-scoring-risk-gated-v1` 不得直接接入 runtime。");
        builder.AppendLine("- 未通过 V4 formal readiness gate 前不得改变 PackingPolicy / package output。");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        if (report.BlockedReasons.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var reason in report.BlockedReasons)
            {
                builder.AppendLine($"- `{reason}`");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Source Reports");
        if (report.SourceReports.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var item in report.SourceReports.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {item.Key}: `{item.Value}`");
            }
        }

        return builder.ToString();
    }

    private static void AddMissing(List<string> blocked, object? report, string reason)
    {
        if (report is null)
        {
            blocked.Add(reason);
        }
    }

    private static void AddRiskBlocks(List<string> blocked, int risk, int mustNotRisk, int lifecycleRisk)
    {
        if (risk != 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (mustNotRisk != 0)
        {
            blocked.Add("MustNotHitRiskAfterPolicyNonZero");
        }

        if (lifecycleRisk != 0)
        {
            blocked.Add("LifecycleRiskAfterPolicyNonZero");
        }
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static reason => reason.Contains("Missing", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressFreezeRecommendations.BlockedByMissingReport;
        }

        if (blocked.Any(static reason => reason.Contains("Leakage", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressFreezeRecommendations.BlockedByLeakage;
        }

        if (blocked.Any(static reason => reason.Contains("AnchorDominance", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressFreezeRecommendations.BlockedByAnchorDominance;
        }

        if (blocked.Any(static reason => reason.Contains("HybridScoringRisk", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressFreezeRecommendations.BlockedByHybridScoringRisk;
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressFreezeRecommendations.BlockedByRisk;
        }

        if (blocked.Any(static reason => reason.Contains("FormalOutputChanged", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressFreezeRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeFlag", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressFreezeRecommendations.BlockedByRuntimeUse;
        }

        return RetrievalDatasetV2StressFreezeRecommendations.KeepPreviewOnly;
    }

    private static string ResolveDatasetId(
        RetrievalDatasetV2MaterializationReport? materializationGate,
        RetrievalDatasetV2ReadinessGateReport? smallSetReadinessGate,
        RetrievalDatasetV2StressReport? stressReadinessGate,
        HybridUnionScoringRepairReport? hybridScoringRepairGate,
        HybridScoringRiskRegressionTriageReport? hybridScoringRiskTriage)
    {
        foreach (var value in new[]
        {
            stressReadinessGate?.DatasetId,
            hybridScoringRepairGate?.DatasetId,
            hybridScoringRiskTriage?.DatasetId,
            smallSetReadinessGate?.DatasetId,
            materializationGate?.DatasetId
        })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string Blank(string value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;
}
