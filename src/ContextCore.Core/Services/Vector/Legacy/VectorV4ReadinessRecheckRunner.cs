using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Vector V4.R readiness recheck；只汇总离线 gate，不把任何候选路径接入正式检索。
/// </summary>
public sealed class VectorV4ReadinessRecheckRunner
{
    private const double Epsilon = 0.000000001d;

    public VectorV4ReadinessRecheckReport BuildReport(
        VectorRetrievalShadowReadinessGateReport? legacyReadinessGate,
        RetrievalDatasetLegacyLimitationReport? legacyLimitationReport,
        VectorPostgresProviderFreezeGateReport? pgVectorFreezeGate,
        EmbeddingProviderComparisonFreezeReport? qwen3ProviderFreeze,
        HybridRetrievalPreviewFreezeReport? hybridRetrievalFreeze,
        RetrievalDatasetV2MaterializationReport? datasetV2MaterializationGate,
        RetrievalDatasetV2ReadinessGateReport? datasetV2SmallReadinessGate,
        RetrievalDatasetV2StressFreezeReport? datasetV2StressFreezeGate,
        HybridUnionScoringRepairReport? hybridScoringRepairGate,
        HybridScoringRiskRegressionTriageReport? hybridScoringRiskTriage,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        IReadOnlyDictionary<string, string>? sourceReports = null)
    {
        var blocked = new List<string>();
        AddMissing(blocked, legacyReadinessGate, "MissingLegacyVectorReadinessGate");
        AddMissing(blocked, legacyLimitationReport, "MissingLegacyDatasetLimitationReport");
        AddMissing(blocked, pgVectorFreezeGate, "MissingPgVectorProviderFreezeGate");
        AddMissing(blocked, qwen3ProviderFreeze, "MissingQwen3ProviderComparisonFreeze");
        AddMissing(blocked, hybridRetrievalFreeze, "MissingHybridRetrievalFreeze");
        AddMissing(blocked, datasetV2MaterializationGate, "MissingDatasetV2MaterializationGate");
        AddMissing(blocked, datasetV2SmallReadinessGate, "MissingDatasetV2SmallReadinessGate");
        AddMissing(blocked, datasetV2StressFreezeGate, "MissingDatasetV2StressFreezeGate");
        AddMissing(blocked, hybridScoringRepairGate, "MissingHybridScoringRepairGate");
        AddMissing(blocked, hybridScoringRiskTriage, "MissingHybridScoringRiskTriage");
        AddMissing(blocked, runtimeChangeGate, "MissingRuntimeChangeGate");

        ValidateLegacy(blocked, legacyReadinessGate, legacyLimitationReport);
        ValidatePgVector(blocked, pgVectorFreezeGate);
        ValidateQwen3Freeze(blocked, qwen3ProviderFreeze);
        ValidateHybridFreeze(blocked, hybridRetrievalFreeze);
        ValidateDatasetV2Small(blocked, datasetV2MaterializationGate, datasetV2SmallReadinessGate);
        ValidateDatasetV2Stress(blocked, datasetV2StressFreezeGate, hybridScoringRepairGate, hybridScoringRiskTriage);
        ValidateRuntimeSafety(blocked, runtimeChangeGate, datasetV2StressFreezeGate, hybridScoringRepairGate, hybridScoringRiskTriage);

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var stressBest = ResolveBestProfile(datasetV2StressFreezeGate, hybridScoringRepairGate);
        return new VectorV4ReadinessRecheckReport
        {
            OperationId = $"vector-v4-readiness-recheck-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            RecheckPassed = passed,
            Recommendation = passed
                ? VectorV4ReadinessRecheckRecommendations.ReadyForGuardedFormalPreview
                : ResolveRecommendation(distinctBlocked),
            LegacyVectorStatus = ResolveLegacyStatus(legacyReadinessGate, legacyLimitationReport),
            DatasetV2SmallStatus = ResolveDatasetV2SmallStatus(datasetV2MaterializationGate, datasetV2SmallReadinessGate),
            DatasetV2StressStatus = datasetV2StressFreezeGate?.DatasetV2Stress ?? "Missing",
            PgVectorProviderStatus = pgVectorFreezeGate?.VectorPostgresProvider ?? "Missing",
            Qwen3ProviderComparisonStatus = qwen3ProviderFreeze?.ProviderComparison ?? "Missing",
            HybridRetrievalStatus = hybridRetrievalFreeze?.HybridRetrievalStatus ?? "Missing",
            HybridScoringRepairStatus = hybridScoringRepairGate?.Recommendation ?? "Missing",
            RuntimeChangeGateStatus = runtimeChangeGate is null
                ? "Missing"
                : runtimeChangeGate.Passed ? "Passed" : "Failed",
            BestPreviewProfile = stressBest?.ProfileName ?? datasetV2StressFreezeGate?.BestPreviewProfile ?? string.Empty,
            DatasetV2StressRecall = stressBest?.RecallAfterPolicy ?? datasetV2StressFreezeGate?.StressRecall ?? 0,
            DatasetV2HoldoutRecall = stressBest?.HoldoutRecallAfterPolicy ?? datasetV2StressFreezeGate?.HoldoutRecall ?? 0,
            RiskAfterPolicy = Max(
                legacyReadinessGate?.A3RiskAfterPolicy ?? 0,
                legacyReadinessGate?.ExtendedRiskAfterPolicy ?? 0,
                datasetV2SmallReadinessGate?.RiskAfterPolicy ?? 0,
                datasetV2StressFreezeGate?.RiskAfterPolicy ?? 0,
                stressBest?.RiskAfterPolicy ?? 0,
                hybridScoringRiskTriage?.RiskCandidateCount ?? 0),
            MustNotHitRiskAfterPolicy = Max(
                ToIntRisk(legacyReadinessGate?.A3MustNotHitRiskAfterPolicy ?? 0),
                ToIntRisk(legacyReadinessGate?.ExtendedMustNotHitRiskAfterPolicy ?? 0),
                datasetV2SmallReadinessGate?.MustNotHitRiskAfterPolicy ?? 0,
                datasetV2StressFreezeGate?.MustNotHitRiskAfterPolicy ?? 0,
                stressBest?.MustNotHitRiskAfterPolicy ?? 0),
            LifecycleRiskAfterPolicy = Max(
                ToIntRisk(legacyReadinessGate?.A3LifecycleRiskAfterPolicy ?? 0),
                ToIntRisk(legacyReadinessGate?.ExtendedLifecycleRiskAfterPolicy ?? 0),
                datasetV2SmallReadinessGate?.LifecycleRiskAfterPolicy ?? 0,
                datasetV2StressFreezeGate?.LifecycleRiskAfterPolicy ?? 0,
                stressBest?.LifecycleRiskAfterPolicy ?? 0),
            FormalOutputChanged = Max(
                legacyReadinessGate?.A3FormalOutputChanged ?? 0,
                legacyReadinessGate?.ExtendedFormalOutputChanged ?? 0,
                datasetV2SmallReadinessGate?.FormalOutputChanged ?? 0,
                datasetV2StressFreezeGate?.FormalOutputChanged ?? 0,
                stressBest?.FormalOutputChanged ?? 0),
            LeakageIssueCount = datasetV2StressFreezeGate?.LeakageIssueCount ?? 0,
            AnchorDominanceScore = datasetV2StressFreezeGate?.AnchorDominanceScore ?? 0,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            ReadyForGuardedFormalPreview = passed,
            ReadyForRuntimeSwitch = false,
            BlockedReasons = distinctBlocked,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    public static string BuildMarkdown(VectorV4ReadinessRecheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Vector V4 Readiness Recheck");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- RecheckPassed: `{report.RecheckPassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- LegacyVectorStatus: `{report.LegacyVectorStatus}`");
        builder.AppendLine($"- DatasetV2SmallStatus: `{report.DatasetV2SmallStatus}`");
        builder.AppendLine($"- DatasetV2StressStatus: `{report.DatasetV2StressStatus}`");
        builder.AppendLine($"- PgVectorProviderStatus: `{report.PgVectorProviderStatus}`");
        builder.AppendLine($"- Qwen3ProviderComparisonStatus: `{report.Qwen3ProviderComparisonStatus}`");
        builder.AppendLine($"- HybridRetrievalStatus: `{report.HybridRetrievalStatus}`");
        builder.AppendLine($"- HybridScoringRepairStatus: `{report.HybridScoringRepairStatus}`");
        builder.AppendLine($"- RuntimeChangeGateStatus: `{report.RuntimeChangeGateStatus}`");
        builder.AppendLine($"- BestPreviewProfile: `{Blank(report.BestPreviewProfile)}`");
        builder.AppendLine($"- StressRecall: `{report.DatasetV2StressRecall:P2}`");
        builder.AppendLine($"- HoldoutRecall: `{report.DatasetV2HoldoutRecall:P2}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- LeakageIssueCount: `{report.LeakageIssueCount}`");
        builder.AppendLine($"- AnchorDominanceScore: `{report.AnchorDominanceScore:F4}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- ReadyForGuardedFormalPreview: `{report.ReadyForGuardedFormalPreview}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- V4.R 通过不等于 runtime switch。");
        builder.AppendLine("- 本阶段只允许进入 GuardedFormalPreview。");
        builder.AppendLine("- `ReadyForRuntimeSwitch` 恒为 `false`。");
        builder.AppendLine("- `FormalRetrievalAllowed` 在 V4.R 仍为 `false`。");
        builder.AppendLine("- 未通过后续 formal readiness gate 前，不允许绑定正式 `IVectorIndexStore`，也不允许改变 PackingPolicy / package output。");
        AppendList(builder, "BlockedReasons", report.BlockedReasons);
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

    private static void ValidateLegacy(
        ICollection<string> blocked,
        VectorRetrievalShadowReadinessGateReport? readinessGate,
        RetrievalDatasetLegacyLimitationReport? limitationReport)
    {
        if (readinessGate is not null)
        {
            if (readinessGate.A3RiskAfterPolicy != 0
                || readinessGate.ExtendedRiskAfterPolicy != 0
                || readinessGate.A3FusionRiskAfterPolicy != 0
                || readinessGate.ExtendedFusionRiskAfterPolicy != 0
                || readinessGate.A3ExpandedRiskAfterPolicy != 0
                || readinessGate.ExtendedExpandedRiskAfterPolicy != 0)
            {
                blocked.Add("LegacyRiskRegression");
            }

            if (readinessGate.A3FormalOutputChanged != 0 || readinessGate.ExtendedFormalOutputChanged != 0)
            {
                blocked.Add("LegacyFormalOutputChanged");
            }
        }

        if (limitationReport is not null)
        {
            if (limitationReport.LegacyDatasetSuitableForPrimaryRecallRepair)
            {
                blocked.Add("LegacyLimitationReportUnexpectedlySuitableForPrimaryRecallRepair");
            }

            if (limitationReport.FormalRetrievalAllowed || limitationReport.UseForRuntime)
            {
                blocked.Add("LegacyLimitationRuntimeFlagEnabled");
            }
        }
    }

    private static void ValidatePgVector(ICollection<string> blocked, VectorPostgresProviderFreezeGateReport? report)
    {
        if (report is null)
        {
            return;
        }

        if (!report.Passed
            || !string.Equals(report.VectorPostgresProvider, "ReadyForPreviewShadowStorage", StringComparison.OrdinalIgnoreCase)
            || !report.ParityPassed
            || report.ProjectionMismatchCount != 0)
        {
            blocked.Add("PgVectorProviderParityNotReady");
        }

        if (report.UseForRuntime || report.FormalRetrievalAllowed)
        {
            blocked.Add("PgVectorRuntimeFlagEnabled");
        }

        if (report.RiskAfterPolicy != 0
            || report.MustNotHitRiskAfterPolicy > Epsilon
            || report.LifecycleRiskAfterPolicy > Epsilon)
        {
            blocked.Add("PgVectorRiskNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("PgVectorFormalOutputChanged");
        }
    }

    private static void ValidateQwen3Freeze(ICollection<string> blocked, EmbeddingProviderComparisonFreezeReport? report)
    {
        if (report is null)
        {
            return;
        }

        if (!report.ProviderConfigurationSanityPassed
            || string.Equals(report.ProviderComparison, "Inconclusive", StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("Qwen3ProviderConfigurationMismatch");
        }

        if (report.FormalRetrievalAllowed)
        {
            blocked.Add("Qwen3FormalRetrievalAllowed");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("Qwen3FormalOutputChanged");
        }
    }

    private static void ValidateHybridFreeze(ICollection<string> blocked, HybridRetrievalPreviewFreezeReport? report)
    {
        if (report is null)
        {
            return;
        }

        if (!report.FreezePassed)
        {
            blocked.Add("HybridRetrievalFreezeNotPassed");
        }

        if (report.RiskAfterPolicy != 0)
        {
            blocked.Add("HybridRetrievalRiskNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("HybridRetrievalFormalOutputChanged");
        }

        if (report.FormalRetrievalAllowed || report.UseForRuntime)
        {
            blocked.Add("HybridRetrievalRuntimeFlagEnabled");
        }
    }

    private static void ValidateDatasetV2Small(
        ICollection<string> blocked,
        RetrievalDatasetV2MaterializationReport? materialization,
        RetrievalDatasetV2ReadinessGateReport? smallReadiness)
    {
        if (materialization is not null)
        {
            if (!materialization.GatePassed)
            {
                blocked.Add("DatasetV2MaterializationGateNotPassed");
            }

            if (materialization.ValidationIssueCount != 0
                || materialization.MissingEvidenceCount != 0
                || materialization.MissingProvenanceCount != 0
                || materialization.ItemIdLeakageCount != 0
                || materialization.RelationInconsistencyCount != 0)
            {
                blocked.Add("DatasetV2MaterializationValidationIssue");
            }

            if (materialization.UseForRuntime || materialization.FormalRetrievalAllowed)
            {
                blocked.Add("DatasetV2MaterializationRuntimeFlagEnabled");
            }
        }

        if (smallReadiness is not null)
        {
            if (!smallReadiness.GatePassed
                || !smallReadiness.PgVectorParityPassed
                || smallReadiness.ValidationIssueCount != 0
                || smallReadiness.MissingEvidenceCount != 0
                || smallReadiness.MissingProvenanceCount != 0)
            {
                blocked.Add("DatasetV2SmallReadinessNotReady");
            }

            if (smallReadiness.RiskAfterPolicy != 0
                || smallReadiness.MustNotHitRiskAfterPolicy != 0
                || smallReadiness.LifecycleRiskAfterPolicy != 0)
            {
                blocked.Add("DatasetV2SmallRiskNonZero");
            }

            if (smallReadiness.FormalOutputChanged != 0)
            {
                blocked.Add("DatasetV2SmallFormalOutputChanged");
            }

            if (smallReadiness.UseForRuntime || smallReadiness.FormalRetrievalAllowed)
            {
                blocked.Add("DatasetV2SmallRuntimeFlagEnabled");
            }
        }
    }

    private static void ValidateDatasetV2Stress(
        ICollection<string> blocked,
        RetrievalDatasetV2StressFreezeReport? stressFreeze,
        HybridUnionScoringRepairReport? repairGate,
        HybridScoringRiskRegressionTriageReport? riskTriage)
    {
        var best = ResolveBestProfile(stressFreeze, repairGate);
        if (stressFreeze is not null)
        {
            if (!stressFreeze.FreezePassed
                || !stressFreeze.V4RecheckAllowed
                || !string.Equals(stressFreeze.DatasetV2Stress, RetrievalDatasetV2StressFreezeStatuses.ReadyForV4RecheckInput, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("DatasetV2StressFreezeNotReady");
            }

            if (!string.Equals(stressFreeze.BestPreviewProfile, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("DatasetV2StressBestProfileUnexpected");
            }

            if (stressFreeze.RiskAfterPolicy != 0
                || stressFreeze.MustNotHitRiskAfterPolicy != 0
                || stressFreeze.LifecycleRiskAfterPolicy != 0
                || stressFreeze.HybridScoringRiskCandidateCount != 0)
            {
                blocked.Add("DatasetV2StressRiskNonZero");
            }

            if (stressFreeze.FormalOutputChanged != 0)
            {
                blocked.Add("DatasetV2StressFormalOutputChanged");
            }

            if (stressFreeze.LeakageIssueCount != 0)
            {
                blocked.Add("DatasetV2StressLeakageNonZero");
            }

            if (stressFreeze.AnchorDominanceScore > Epsilon)
            {
                blocked.Add("DatasetV2StressAnchorDominanceNonZero");
            }

            if (stressFreeze.UseForRuntime || stressFreeze.FormalRetrievalAllowed || stressFreeze.ReadyForFormalRetrieval)
            {
                blocked.Add("DatasetV2StressRuntimeFlagEnabled");
            }
        }

        if (repairGate is not null)
        {
            if (!repairGate.GatePassed
                || !string.Equals(repairGate.Recommendation, HybridUnionScoringRepairRecommendations.ReadyForDatasetV2StressFreeze, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("HybridScoringRepairGateNotPassed");
            }

            if (!string.Equals(repairGate.BestProfileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("HybridScoringRepairBestProfileUnexpected");
            }

            if (repairGate.UseForRuntime || repairGate.FormalRetrievalAllowed)
            {
                blocked.Add("HybridScoringRepairRuntimeFlagEnabled");
            }
        }

        if (best is not null)
        {
            if (best.RiskAfterPolicy != 0
                || best.MustNotHitRiskAfterPolicy != 0
                || best.LifecycleRiskAfterPolicy != 0)
            {
                blocked.Add("HybridScoringRepairRiskNonZero");
            }

            if (best.FormalOutputChanged != 0)
            {
                blocked.Add("HybridScoringRepairFormalOutputChanged");
            }
        }

        if (riskTriage is not null)
        {
            if (riskTriage.RiskCandidateCount != 0
                || riskTriage.RiskProjectionMismatchCount != 0
                || riskTriage.MustNotCandidatePromotedCount != 0
                || riskTriage.LifecycleRiskPromotedCount != 0
                || riskTriage.EligibilityBypassCount != 0)
            {
                blocked.Add("HybridScoringRiskTriageRiskNonZero");
            }

            if (!string.Equals(riskTriage.Recommendation, HybridScoringRiskRegressionRecommendations.ReadyForSafeScoringRepair, StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add("HybridScoringRiskTriageRecommendationNotReady");
            }

            if (riskTriage.UseForRuntime || riskTriage.FormalRetrievalAllowed)
            {
                blocked.Add("HybridScoringRiskTriageRuntimeFlagEnabled");
            }
        }
    }

    private static void ValidateRuntimeSafety(
        ICollection<string> blocked,
        LearningRuntimeChangeReadinessGateReport? runtimeGate,
        RetrievalDatasetV2StressFreezeReport? stressFreeze,
        HybridUnionScoringRepairReport? repairGate,
        HybridScoringRiskRegressionTriageReport? riskTriage)
    {
        if (runtimeGate is not null && !runtimeGate.Passed)
        {
            blocked.Add("RuntimeChangeGateFailed");
        }

        if ((stressFreeze?.FormalRetrievalAllowed ?? false)
            || (stressFreeze?.ReadyForFormalRetrieval ?? false)
            || (stressFreeze?.UseForRuntime ?? false)
            || (repairGate?.FormalRetrievalAllowed ?? false)
            || (repairGate?.UseForRuntime ?? false)
            || (riskTriage?.FormalRetrievalAllowed ?? false)
            || (riskTriage?.UseForRuntime ?? false))
        {
            blocked.Add("FormalRetrievalOrRuntimeFlagEnabled");
        }
    }

    private static string ResolveLegacyStatus(
        VectorRetrievalShadowReadinessGateReport? readinessGate,
        RetrievalDatasetLegacyLimitationReport? limitationReport)
    {
        if (readinessGate is null || limitationReport is null)
        {
            return "Missing";
        }

        return readinessGate.Passed
            ? "UnexpectedlyReady"
            : "PreviewOnly / legacy limitations recorded";
    }

    private static string ResolveDatasetV2SmallStatus(
        RetrievalDatasetV2MaterializationReport? materialization,
        RetrievalDatasetV2ReadinessGateReport? smallReadiness)
    {
        if (materialization is null || smallReadiness is null)
        {
            return "Missing";
        }

        return materialization.GatePassed && smallReadiness.GatePassed
            ? "ReadyForDatasetV2RetrievalCandidate"
            : "KeepPreviewOnly";
    }

    private static HybridUnionScoringRepairProfileReport? ResolveBestProfile(
        RetrievalDatasetV2StressFreezeReport? stressFreeze,
        HybridUnionScoringRepairReport? repairGate)
    {
        var bestName = stressFreeze?.BestPreviewProfile;
        if (string.IsNullOrWhiteSpace(bestName))
        {
            bestName = repairGate?.BestProfileName;
        }

        return repairGate?.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.ProfileName, bestName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static reason => reason.Contains("RuntimeChangeGate", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorV4ReadinessRecheckRecommendations.BlockedByRuntimeChangeGate;
        }

        if (blocked.Any(static reason => reason.Contains("PgVector", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Provider", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Parity", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Qwen3", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorV4ReadinessRecheckRecommendations.BlockedByProviderParity;
        }

        if (blocked.Any(static reason => reason.Contains("FormalOutputChanged", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("FormalOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorV4ReadinessRecheckRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorV4ReadinessRecheckRecommendations.BlockedByRisk;
        }

        if (blocked.Any(static reason => reason.Contains("DatasetV2Stress", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("HybridScoring", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Leakage", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("AnchorDominance", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorV4ReadinessRecheckRecommendations.BlockedByDatasetV2Stress;
        }

        if (blocked.Any(static reason => reason.Contains("Legacy", StringComparison.OrdinalIgnoreCase)))
        {
            return VectorV4ReadinessRecheckRecommendations.BlockedByLegacyRisk;
        }

        return VectorV4ReadinessRecheckRecommendations.KeepPreviewOnly;
    }

    private static void AddMissing(ICollection<string> blocked, object? report, string reason)
    {
        if (report is null)
        {
            blocked.Add(reason);
        }
    }

    private static int Max(params int[] values)
        => values.Length == 0 ? 0 : values.Max();

    private static int ToIntRisk(double value)
        => value > Epsilon ? (int)Math.Ceiling(value) : 0;

    private static string Blank(string value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static void AppendList(StringBuilder builder, string label, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {label}");
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
