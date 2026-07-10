using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>已审核反馈数据集门禁；只做离线检查，不触发训练或运行时变更。</summary>
public sealed class LearningApprovedFeedbackDatasetGateBuilder
{
    public LearningApprovedFeedbackDatasetGateReport Build(
        LearningFeedbackQualityReport qualityReport,
        LearningFeedbackFeatureCandidateReport candidateReport)
    {
        ArgumentNullException.ThrowIfNull(qualityReport);
        ArgumentNullException.ThrowIfNull(candidateReport);

        var trainableCandidates = candidateReport.Candidates
            .Where(static item => IsTrainableCandidate(item))
            .ToArray();
        var smokeExcludedCount = candidateReport.Candidates.Count(static item => IsSmokeOrExcluded(item));
        var disabledTrainingUseCount = candidateReport.Candidates.Count(static item =>
            string.IsNullOrWhiteSpace(item.TrainingUse)
            || string.Equals(item.TrainingUse, "disabled_until_review", StringComparison.OrdinalIgnoreCase));
        var failures = new List<string>();

        if (qualityReport.ApprovedCount == 0)
        {
            failures.Add(LearningApprovedFeedbackDatasetGateFailureReasons.NoApprovedFeedback);
        }

        if (qualityReport.PendingReviewCount > 0)
        {
            failures.Add(LearningApprovedFeedbackDatasetGateFailureReasons.NeedsReviewedFeedback);
        }

        if (qualityReport.ApprovedCount > 0 && qualityReport.RedactionCoverageRate < 1.0)
        {
            failures.Add(LearningApprovedFeedbackDatasetGateFailureReasons.RedactionCoverageIncomplete);
        }

        if (candidateReport.GeneratedCandidateCount == 0)
        {
            failures.Add(LearningApprovedFeedbackDatasetGateFailureReasons.NoFeatureCandidates);
        }

        if (trainableCandidates.Length == 0)
        {
            failures.Add(LearningApprovedFeedbackDatasetGateFailureReasons.NoTrainableCandidates);
        }

        if (disabledTrainingUseCount > 0)
        {
            failures.Add(LearningApprovedFeedbackDatasetGateFailureReasons.DisabledTrainingUsePresent);
        }

        var smokeInTrainable = trainableCandidates.Count(static item => IsSmokeOrExcluded(item));
        if (smokeInTrainable > 0)
        {
            failures.Add(LearningApprovedFeedbackDatasetGateFailureReasons.SmokeRecordsInTrainableDataset);
        }

        if (HasInsufficientLabelCoverage(trainableCandidates))
        {
            failures.Add(LearningApprovedFeedbackDatasetGateFailureReasons.CapabilityLabelCoverageTooLow);
        }

        failures = [.. failures.Distinct(StringComparer.OrdinalIgnoreCase)];
        return new LearningApprovedFeedbackDatasetGateReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Passed = failures.Count == 0,
            ApprovedCount = qualityReport.ApprovedCount,
            RedactionCoverageRate = qualityReport.RedactionCoverageRate,
            FeatureCandidateCount = candidateReport.GeneratedCandidateCount,
            TrainableCandidateCount = trainableCandidates.Length,
            SmokeExcludedCount = smokeExcludedCount,
            DisabledTrainingUseCount = disabledTrainingUseCount,
            CandidateCountByCapability = CountBy(trainableCandidates, static item => item.CapabilityId),
            PositiveLabelCountByCapability = CountBy(trainableCandidates.Where(static item => item.PositiveLabel), static item => item.CapabilityId),
            NegativeLabelCountByCapability = CountBy(trainableCandidates.Where(static item => item.NegativeLabel), static item => item.CapabilityId),
            FailureReasons = failures,
            Recommendation = failures.Count == 0
                ? "ReadyForApprovedDatasetExport"
                : "NotReady"
        };
    }

    public static string BuildMarkdownReport(LearningApprovedFeedbackDatasetGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Learning Approved Feedback Dataset Gate");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Passed: `{report.Passed}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- ApprovedCount: `{report.ApprovedCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- RedactionCoverageRate: `{report.RedactionCoverageRate:P2}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- FeatureCandidateCount: `{report.FeatureCandidateCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- TrainableCandidateCount: `{report.TrainableCandidateCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- SmokeExcludedCount: `{report.SmokeExcludedCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- DisabledTrainingUseCount: `{report.DisabledTrainingUseCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        AppendCounts(builder, "Trainable Candidates By Capability", report.CandidateCountByCapability);
        AppendCounts(builder, "Positive Labels By Capability", report.PositiveLabelCountByCapability);
        AppendCounts(builder, "Negative Labels By Capability", report.NegativeLabelCountByCapability);

        if (report.FailureReasons.Count > 0)
        {
            builder.AppendLine("## Failure Reasons");
            builder.AppendLine();
            foreach (var reason in report.FailureReasons)
            {
                builder.AppendLine($"- {reason}");
            }
        }

        return builder.ToString();
    }

    private static bool HasInsufficientLabelCoverage(IReadOnlyList<FeedbackFeatureCandidate> candidates)
    {
        return candidates
            .GroupBy(static item => item.CapabilityId, StringComparer.OrdinalIgnoreCase)
            .Any(static group => !group.Any(item => item.PositiveLabel) || !group.Any(item => item.NegativeLabel));
    }

    private static bool IsTrainableCandidate(FeedbackFeatureCandidate candidate)
    {
        return !string.IsNullOrWhiteSpace(candidate.TrainingUse)
            && !string.Equals(candidate.TrainingUse, "disabled_until_review", StringComparison.OrdinalIgnoreCase)
            && !IsSmokeOrExcluded(candidate);
    }

    private static bool IsSmokeOrExcluded(FeedbackFeatureCandidate candidate)
    {
        return string.Equals(candidate.TrainingUse, "smoke_test_only", StringComparison.OrdinalIgnoreCase)
            || IsEnabled(candidate.Metadata, "excludedFromTraining");
    }

    private static bool IsEnabled(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value)
            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, int> CountBy(
        IEnumerable<FeedbackFeatureCandidate> candidates,
        Func<FeedbackFeatureCandidate, string> selector)
    {
        return candidates
            .Select(selector)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static void AppendCounts(
        StringBuilder builder,
        string title,
        IReadOnlyDictionary<string, int> counts)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        if (counts.Count == 0)
        {
            builder.AppendLine("- none");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| Key | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var pair in counts)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(pair.Key)} | {pair.Value} |");
        }
        builder.AppendLine();
    }

    private static string Escape(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal);
}
