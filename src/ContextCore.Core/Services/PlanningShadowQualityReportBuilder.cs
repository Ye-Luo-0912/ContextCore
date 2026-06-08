using System.Text;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services;

public static class PlanningShadowQualityReportBuilder
{
    private const double Epsilon = 0.0001;

    public static PlanningShadowQualityReport Build(ShadowRetrievalComparisonReport comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var samples = comparison.Samples
            .Select(BuildSample)
            .ToArray();

        return new PlanningShadowQualityReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            SampleSet = comparison.SampleSet,
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalSamples = samples.Length,
            Global = BuildGroup("Global", samples),
            ModeBreakdown = BuildBreakdown(samples, sample => sample.Mode),
            IntentBreakdown = BuildBreakdown(samples, sample => sample.Intent),
            Recommendation = BuildRecommendation(BuildBreakdown(samples, sample => sample.Intent)),
            Samples = samples
        };
    }

    public static string BuildMarkdownReport(PlanningShadowQualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("# Planning Shadow Quality Report");
        sb.AppendLine();
        sb.AppendLine($"更新时间：{DateTimeOffset.Now:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("## 目标");
        sb.AppendLine();
        sb.AppendLine("Phase P7 对比 legacy retrieval 与 planning shadow retrieval 的质量指标。该报告只用于 shadow evaluation，不影响正式 retrieval、scoring、PackingPolicy、vector、LLM router 或 layered retrieval。");
        sb.AppendLine();
        sb.AppendLine("## 全局结果");
        sb.AppendLine();
        AppendGroupTable(sb, [report.Global]);
        sb.AppendLine();
        sb.AppendLine("## Mode Breakdown");
        sb.AppendLine();
        AppendGroupTable(sb, report.ModeBreakdown.Values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase));
        sb.AppendLine();
        sb.AppendLine("## Intent Breakdown");
        sb.AppendLine();
        AppendGroupTable(sb, report.IntentBreakdown.Values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase));
        sb.AppendLine();
        sb.AppendLine("## Recommendation");
        sb.AppendLine();
        sb.AppendLine($"- opt-in candidate intents: `{FormatList(report.Recommendation.OptInCandidateIntents)}`");
        sb.AppendLine($"- blocked intents: `{FormatList(report.Recommendation.BlockedIntents)}`");
        sb.AppendLine($"- needs tuning intents: `{FormatList(report.Recommendation.NeedsTuningIntents)}`");
        sb.AppendLine($"- safe only in shadow intents: `{FormatList(report.Recommendation.SafeOnlyInShadowIntents)}`");
        if (report.Recommendation.IntentReasons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| Intent | Reason |");
            sb.AppendLine("|---|---|");
            foreach (var item in report.Recommendation.IntentReasons.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"| `{item.Key}` | {item.Value} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Sample Diffs");
        sb.AppendLine();
        sb.AppendLine("| Sample | Mode | Intent | Improved | Regressed | Recall@10 Delta | MRR Delta | MustHit +/- | Constraint +/- | Entity +/- | Uncertainty +/- | Selected Delta | Reason |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var sample in report.Samples
                     .Where(sample => sample.Improved || sample.Regressed)
                     .OrderByDescending(sample => sample.Regressed)
                     .ThenBy(sample => sample.SampleId, StringComparer.OrdinalIgnoreCase)
                     .Take(80))
        {
            sb.AppendLine(
                $"| `{sample.SampleId}` | `{sample.Mode}` | `{sample.Intent}` | {Bool(sample.Improved)} | {Bool(sample.Regressed)} | {sample.Recall10Delta:F4} | {sample.MrrDelta:F4} | +{sample.MustHitGained.Count}/-{sample.MustHitLost.Count} | +{sample.ConstraintGained.Count}/-{sample.ConstraintLost.Count} | +{sample.EntityGained.Count}/-{sample.EntityLost.Count} | +{sample.UncertaintyGained.Count}/-{sample.UncertaintyLost.Count} | {sample.SelectedCountDelta} | {sample.SuspectedReason} |");
        }

        return sb.ToString();
    }

    private static PlanningShadowQualitySample BuildSample(ShadowRetrievalComparisonItem item)
    {
        var legacyPassed = IsPassed(
            item.LegacyRecall10,
            item.LegacyMustNotHitViolationCount,
            lifecycleViolations: 0,
            item.LegacyConstraintHitRate,
            item.LegacyEntityHitRate);
        var shadowPassed = IsPassed(
            item.ShadowRecall10,
            item.MustNotHitViolationCount,
            item.LifecycleViolationCount,
            item.ShadowConstraintHitRate,
            item.ShadowEntityHitRate);
        var qualityDelta = QualityScore(
                item.ShadowRecall10,
                item.ShadowMrr,
                item.ShadowConstraintHitRate,
                item.ShadowEntityHitRate,
                item.ShadowUncertaintyHitRate)
            - QualityScore(
                item.LegacyRecall10,
                item.LegacyMrr,
                item.LegacyConstraintHitRate,
                item.LegacyEntityHitRate,
                item.LegacyUncertaintyHitRate);
        var mustNotHitDelta = item.MustNotHitViolationCount - item.LegacyMustNotHitViolationCount;
        var selectedCountDelta = item.ShadowSelected.Count - item.LegacySelected.Count;
        var mustHitTokenShareDelta = item.ShadowMustHitTokenShare - item.LegacyMustHitTokenShare;
        var regressed = !shadowPassed && legacyPassed
            || qualityDelta < -Epsilon
            || item.MustHitDropped.Count > 0
            || mustNotHitDelta > 0
            || item.LifecycleViolationCount > 0;
        var improved = !regressed
            && (shadowPassed && !legacyPassed
                || qualityDelta > Epsilon
                || item.MustHitGained.Count > 0
                || item.ConstraintGained.Count > 0
                || item.EntityGained.Count > 0
                || item.UncertaintyGained.Count > 0);

        return new PlanningShadowQualitySample
        {
            SampleId = item.SampleId,
            Mode = string.IsNullOrWhiteSpace(item.Mode) ? "Unknown" : item.Mode,
            Intent = ResolveIntent(item.ProposalSummary),
            Improved = improved,
            Regressed = regressed,
            LegacyPassed = legacyPassed,
            ShadowPassed = shadowPassed,
            LegacyRecall3 = item.LegacyRecall3,
            ShadowRecall3 = item.ShadowRecall3,
            Recall3Delta = item.ShadowRecall3 - item.LegacyRecall3,
            LegacyRecall5 = item.LegacyRecall5,
            ShadowRecall5 = item.ShadowRecall5,
            Recall5Delta = item.ShadowRecall5 - item.LegacyRecall5,
            LegacyRecall10 = item.LegacyRecall10,
            ShadowRecall10 = item.ShadowRecall10,
            Recall10Delta = item.ShadowRecall10 - item.LegacyRecall10,
            LegacyMrr = item.LegacyMrr,
            ShadowMrr = item.ShadowMrr,
            MrrDelta = item.ShadowMrr - item.LegacyMrr,
            LegacyConstraintHitRate = item.LegacyConstraintHitRate,
            ShadowConstraintHitRate = item.ShadowConstraintHitRate,
            ConstraintHitDelta = item.ShadowConstraintHitRate - item.LegacyConstraintHitRate,
            LegacyEntityHitRate = item.LegacyEntityHitRate,
            ShadowEntityHitRate = item.ShadowEntityHitRate,
            EntityHitDelta = item.ShadowEntityHitRate - item.LegacyEntityHitRate,
            LegacyUncertaintyHitRate = item.LegacyUncertaintyHitRate,
            ShadowUncertaintyHitRate = item.ShadowUncertaintyHitRate,
            UncertaintyHitDelta = item.ShadowUncertaintyHitRate - item.LegacyUncertaintyHitRate,
            LegacyMustNotHitViolationCount = item.LegacyMustNotHitViolationCount,
            ShadowMustNotHitViolationCount = item.MustNotHitViolationCount,
            MustNotHitViolationDelta = mustNotHitDelta,
            LifecycleViolationCount = item.LifecycleViolationCount,
            BudgetPressureDelta = item.BudgetPressureDelta,
            SelectedCountDelta = selectedCountDelta,
            MustHitTokenShareDelta = mustHitTokenShareDelta,
            MustHitGained = item.MustHitGained,
            MustHitLost = item.MustHitDropped,
            ConstraintGained = item.ConstraintGained,
            ConstraintLost = item.ConstraintLost,
            EntityGained = item.EntityGained,
            EntityLost = item.EntityLost,
            UncertaintyGained = item.UncertaintyGained,
            UncertaintyLost = item.UncertaintyLost,
            SuspectedReason = ResolveSuspectedReason(
                regressed,
                improved,
                mustNotHitDelta,
                item.LifecycleViolationCount,
                item.MustHitDropped.Count,
                item.ConstraintLost.Count,
                item.EntityLost.Count,
                item.UncertaintyLost.Count,
                selectedCountDelta,
                qualityDelta)
        };
    }

    private static PlanningShadowQualityGroup BuildGroup(
        string key,
        IReadOnlyList<PlanningShadowQualitySample> samples)
    {
        if (samples.Count == 0)
        {
            return new PlanningShadowQualityGroup { Key = key };
        }

        var legacyPassRate = AverageBool(samples, sample => sample.LegacyPassed);
        var shadowPassRate = AverageBool(samples, sample => sample.ShadowPassed);
        var legacyRecall3 = samples.Average(sample => sample.LegacyRecall3);
        var shadowRecall3 = samples.Average(sample => sample.ShadowRecall3);
        var legacyRecall5 = samples.Average(sample => sample.LegacyRecall5);
        var shadowRecall5 = samples.Average(sample => sample.ShadowRecall5);
        var legacyRecall10 = samples.Average(sample => sample.LegacyRecall10);
        var shadowRecall10 = samples.Average(sample => sample.ShadowRecall10);
        var legacyMrr = samples.Average(sample => sample.LegacyMrr);
        var shadowMrr = samples.Average(sample => sample.ShadowMrr);
        var legacyConstraint = samples.Average(sample => sample.LegacyConstraintHitRate);
        var shadowConstraint = samples.Average(sample => sample.ShadowConstraintHitRate);
        var legacyEntity = samples.Average(sample => sample.LegacyEntityHitRate);
        var shadowEntity = samples.Average(sample => sample.ShadowEntityHitRate);
        var legacyUncertainty = samples.Average(sample => sample.LegacyUncertaintyHitRate);
        var shadowUncertainty = samples.Average(sample => sample.ShadowUncertaintyHitRate);

        return new PlanningShadowQualityGroup
        {
            Key = key,
            TotalSamples = samples.Count,
            LegacyPassRate = legacyPassRate,
            ShadowPassRate = shadowPassRate,
            PassRateDelta = shadowPassRate - legacyPassRate,
            LegacyRecall3 = legacyRecall3,
            ShadowRecall3 = shadowRecall3,
            Recall3Delta = shadowRecall3 - legacyRecall3,
            LegacyRecall5 = legacyRecall5,
            ShadowRecall5 = shadowRecall5,
            Recall5Delta = shadowRecall5 - legacyRecall5,
            LegacyRecall10 = legacyRecall10,
            ShadowRecall10 = shadowRecall10,
            Recall10Delta = shadowRecall10 - legacyRecall10,
            LegacyMrr = legacyMrr,
            ShadowMrr = shadowMrr,
            MrrDelta = shadowMrr - legacyMrr,
            LegacyConstraintHitRate = legacyConstraint,
            ShadowConstraintHitRate = shadowConstraint,
            ConstraintHitDelta = shadowConstraint - legacyConstraint,
            LegacyEntityHitRate = legacyEntity,
            ShadowEntityHitRate = shadowEntity,
            EntityHitDelta = shadowEntity - legacyEntity,
            LegacyUncertaintyHitRate = legacyUncertainty,
            ShadowUncertaintyHitRate = shadowUncertainty,
            UncertaintyHitDelta = shadowUncertainty - legacyUncertainty,
            LegacyMustNotHitViolationCount = samples.Sum(sample => sample.LegacyMustNotHitViolationCount),
            ShadowMustNotHitViolationCount = samples.Sum(sample => sample.ShadowMustNotHitViolationCount),
            MustNotHitViolationDelta = samples.Sum(sample => sample.MustNotHitViolationDelta),
            LifecycleViolationCount = samples.Sum(sample => sample.LifecycleViolationCount),
            BudgetPressureDelta = samples.Average(sample => sample.BudgetPressureDelta),
            SelectedCountDelta = samples.Average(sample => sample.SelectedCountDelta),
            MustHitTokenShareDelta = samples.Average(sample => sample.MustHitTokenShareDelta),
            ImprovedSampleCount = samples.Count(sample => sample.Improved),
            RegressedSampleCount = samples.Count(sample => sample.Regressed),
            MustHitGainedCount = samples.Sum(sample => sample.MustHitGained.Count),
            MustHitLostCount = samples.Sum(sample => sample.MustHitLost.Count),
            ConstraintGainedCount = samples.Sum(sample => sample.ConstraintGained.Count),
            ConstraintLostCount = samples.Sum(sample => sample.ConstraintLost.Count),
            EntityGainedCount = samples.Sum(sample => sample.EntityGained.Count),
            EntityLostCount = samples.Sum(sample => sample.EntityLost.Count),
            UncertaintyGainedCount = samples.Sum(sample => sample.UncertaintyGained.Count),
            UncertaintyLostCount = samples.Sum(sample => sample.UncertaintyLost.Count)
        };
    }

    private static IReadOnlyDictionary<string, PlanningShadowQualityGroup> BuildBreakdown(
        IReadOnlyList<PlanningShadowQualitySample> samples,
        Func<PlanningShadowQualitySample, string> keySelector)
    {
        return samples
            .GroupBy(sample =>
            {
                var key = keySelector(sample);
                return string.IsNullOrWhiteSpace(key) ? "Unknown" : key;
            }, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => BuildGroup(group.Key, group.ToArray()),
                StringComparer.OrdinalIgnoreCase);
    }

    private static PlanningShadowQualityRecommendation BuildRecommendation(
        IReadOnlyDictionary<string, PlanningShadowQualityGroup> intentGroups)
    {
        var optIn = new List<string>();
        var blocked = new List<string>();
        var tuning = new List<string>();
        var shadowOnly = new List<string>();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in intentGroups.Values.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (group.ShadowMustNotHitViolationCount > 0 || group.LifecycleViolationCount > 0)
            {
                blocked.Add(group.Key);
                reasons[group.Key] = $"blocked: mustNotHit={group.ShadowMustNotHitViolationCount}, lifecycle={group.LifecycleViolationCount}";
                continue;
            }

            if (group.PassRateDelta < -Epsilon
                || group.Recall10Delta < -Epsilon
                || group.MrrDelta < -0.05
                || group.MustHitLostCount > 0
                || group.RegressedSampleCount > group.ImprovedSampleCount)
            {
                tuning.Add(group.Key);
                shadowOnly.Add(group.Key);
                reasons[group.Key] = $"needs tuning: passDelta={group.PassRateDelta:F4}, recall10Delta={group.Recall10Delta:F4}, mrrDelta={group.MrrDelta:F4}, mustHitLost={group.MustHitLostCount}";
                continue;
            }

            if (group.PassRateDelta >= -Epsilon
                && group.Recall10Delta >= -Epsilon
                && group.MrrDelta >= -Epsilon
                && group.MustHitLostCount == 0)
            {
                optIn.Add(group.Key);
                reasons[group.Key] = $"opt-in candidate: passDelta={group.PassRateDelta:F4}, recall10Delta={group.Recall10Delta:F4}, mrrDelta={group.MrrDelta:F4}";
                continue;
            }

            shadowOnly.Add(group.Key);
            reasons[group.Key] = "safe only in shadow: safety passed but quality is neutral or inconclusive";
        }

        return new PlanningShadowQualityRecommendation
        {
            OptInCandidateIntents = optIn,
            BlockedIntents = blocked,
            NeedsTuningIntents = tuning,
            SafeOnlyInShadowIntents = shadowOnly.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            IntentReasons = reasons
        };
    }

    private static bool IsPassed(
        double recall10,
        int mustNotHitViolations,
        int lifecycleViolations,
        double constraintHitRate,
        double entityHitRate)
    {
        return recall10 >= 0.999
            && mustNotHitViolations == 0
            && lifecycleViolations == 0
            && constraintHitRate >= 0.999
            && entityHitRate >= 0.999;
    }

    private static double QualityScore(
        double recall10,
        double mrr,
        double constraintHitRate,
        double entityHitRate,
        double uncertaintyHitRate)
    {
        return (recall10 + mrr + constraintHitRate + entityHitRate + uncertaintyHitRate) / 5.0;
    }

    private static string ResolveIntent(string proposalSummary)
    {
        if (string.IsNullOrWhiteSpace(proposalSummary))
        {
            return "Unknown";
        }

        var slash = proposalSummary.IndexOf('/');
        return slash > 0 ? proposalSummary[..slash] : proposalSummary;
    }

    private static string ResolveSuspectedReason(
        bool regressed,
        bool improved,
        int mustNotHitDelta,
        int lifecycleViolations,
        int mustHitLostCount,
        int constraintLostCount,
        int entityLostCount,
        int uncertaintyLostCount,
        int selectedCountDelta,
        double qualityDelta)
    {
        if (mustNotHitDelta > 0)
        {
            return "MustNotHitViolationAdded";
        }

        if (lifecycleViolations > 0)
        {
            return "LifecycleViolationAdded";
        }

        if (mustHitLostCount > 0)
        {
            return "MustHitLostByShadow";
        }

        if (constraintLostCount > 0)
        {
            return "ConstraintLostByShadow";
        }

        if (entityLostCount > 0)
        {
            return "EntityLostByShadow";
        }

        if (uncertaintyLostCount > 0)
        {
            return "UncertaintyLostByShadow";
        }

        if (regressed && selectedCountDelta < 0)
        {
            return "ShadowSelectedFewerItems";
        }

        if (regressed)
        {
            return qualityDelta < 0 ? "QualityScoreRegressed" : "ShadowQualityRegressed";
        }

        if (improved)
        {
            return qualityDelta > 0 ? "QualityScoreImproved" : "ShadowCoverageImproved";
        }

        return "NoMaterialQualityDelta";
    }

    private static double AverageBool(
        IReadOnlyList<PlanningShadowQualitySample> samples,
        Func<PlanningShadowQualitySample, bool> selector)
    {
        return samples.Count == 0 ? 0.0 : (double)samples.Count(selector) / samples.Count;
    }

    private static void AppendGroupTable(
        StringBuilder sb,
        IEnumerable<PlanningShadowQualityGroup> groups)
    {
        sb.AppendLine("| Group | Samples | Pass Delta | R@3 Delta | R@5 Delta | R@10 Delta | MRR Delta | Constraint Delta | Entity Delta | Uncertainty Delta | MustNotHit Delta | Budget Delta | Selected Delta | MustHitShare Delta | Improved | Regressed |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var group in groups)
        {
            sb.AppendLine($"| `{group.Key}` | {group.TotalSamples} | {group.PassRateDelta:F4} | {group.Recall3Delta:F4} | {group.Recall5Delta:F4} | {group.Recall10Delta:F4} | {group.MrrDelta:F4} | {group.ConstraintHitDelta:F4} | {group.EntityHitDelta:F4} | {group.UncertaintyHitDelta:F4} | {group.MustNotHitViolationDelta} | {group.BudgetPressureDelta:F1} | {group.SelectedCountDelta:F1} | {group.MustHitTokenShareDelta:F4} | {group.ImprovedSampleCount} | {group.RegressedSampleCount} |");
        }
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "(none)" : string.Join(", ", values);
    }

    private static string Bool(bool value) => value ? "1" : "0";
}
