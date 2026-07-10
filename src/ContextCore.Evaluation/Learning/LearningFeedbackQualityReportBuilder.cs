using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>构建 runtime feedback 质量报告；只做离线聚合，不改变训练或运行时策略。</summary>
public sealed class LearningFeedbackQualityReportBuilder
{
    private static readonly string[] CapabilityIds =
    [
        ShadowCapabilityIds.RouterIntentClassifier,
        ShadowCapabilityIds.CandidateReranker,
        ShadowCapabilityIds.VectorRetrieval,
        ShadowCapabilityIds.GraphExpansion,
        ShadowCapabilityIds.PromotionJudge,
        ShadowCapabilityIds.ConstraintGapJudge
    ];

    public LearningFeedbackQualityReport Build(
        IReadOnlyList<LearningFeedbackEvent> feedback,
        IReadOnlyList<LearningFeedbackReviewRecord> reviews,
        LearningFeedbackFeatureCandidateReport featureCandidates,
        LearningFeatureDataset? existingFeatureDataset = null)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(featureCandidates);

        var feedbackIds = feedback
            .Select(static item => item.FeedbackId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var latestReviews = reviews
            .Where(item => feedbackIds.Contains(item.FeedbackId))
            .GroupBy(static item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(item => item.ReviewedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var reviewedFeedbackCount = feedback.Count(item => latestReviews.ContainsKey(item.FeedbackId));
        var pendingReviewCount = feedback.Count(item => !latestReviews.ContainsKey(item.FeedbackId))
            + latestReviews.Values.Count(static item => item.ReviewStatus == FeedbackReviewStatus.PendingReview);
        var approvedReviews = latestReviews.Values
            .Where(static item => item.ReviewStatus == FeedbackReviewStatus.ApprovedForDataset)
            .ToArray();
        var needsRedactionCount = latestReviews.Values.Count(static item => item.ReviewStatus == FeedbackReviewStatus.NeedsRedaction)
            + approvedReviews.Count(static item => !item.RedactionChecked);
        var needsEvidenceCount = latestReviews.Values.Count(static item => item.ReviewStatus == FeedbackReviewStatus.NeedsMoreEvidence);
        var trainingDisabledCount = feedback.Count(static item => IsDisabledTrainingUse(item.TrainingUse))
            + approvedReviews.Count(static item => IsDisabledTrainingUse(item.TrainingUse));
        var candidates = featureCandidates.Candidates;
        var readiness = CapabilityIds
            .Select(capability => BuildReadiness(capability, feedback, latestReviews, candidates))
            .ToArray();
        var recommendation = BuildRecommendation(
            feedback.Count,
            pendingReviewCount,
            approvedReviews.Length,
            needsRedactionCount,
            candidates.Count,
            readiness);
        var warnings = BuildWarnings(feedback.Count, candidates.Count, readiness);

        return new LearningFeedbackQualityReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            FeedbackCount = feedback.Count,
            PendingReviewCount = pendingReviewCount,
            ApprovedCount = approvedReviews.Length,
            RejectedCount = latestReviews.Values.Count(static item => item.ReviewStatus == FeedbackReviewStatus.Rejected),
            NeedsRedactionCount = needsRedactionCount,
            NeedsEvidenceCount = needsEvidenceCount,
            MetadataOnlyCount = feedback.Count(static item => item.MetadataOnly),
            TrainingDisabledCount = trainingDisabledCount,
            FeatureCandidateCount = candidates.Count,
            CandidateCountByCapability = CountBy(candidates, static item => item.CapabilityId),
            CandidateCountByLabelKind = CountBy(candidates, static item => item.LabelKind),
            RedactionCoverageRate = Ratio(approvedReviews.Count(static item => item.RedactionChecked), approvedReviews.Length),
            ReviewCoverageRate = Ratio(reviewedFeedbackCount, feedback.Count),
            ApprovedDatasetReadiness = readiness,
            ExistingPolicyFeatureCount = existingFeatureDataset?.FeatureCount ?? 0,
            ExistingRankingPairCount = existingFeatureDataset?.RankingPairCount ?? 0,
            ExistingRouterIntentExampleCount = existingFeatureDataset?.RouterIntentExampleCount ?? 0,
            Recommendation = recommendation,
            Warnings = warnings
        };
    }

    public static string BuildMarkdownReport(LearningFeedbackQualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Learning Feedback Quality Report");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- FeedbackCount: `{report.FeedbackCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- PendingReviewCount: `{report.PendingReviewCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- ApprovedCount: `{report.ApprovedCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- RejectedCount: `{report.RejectedCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- NeedsRedactionCount: `{report.NeedsRedactionCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- NeedsEvidenceCount: `{report.NeedsEvidenceCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- MetadataOnlyCount: `{report.MetadataOnlyCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- TrainingDisabledCount: `{report.TrainingDisabledCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- FeatureCandidateCount: `{report.FeatureCandidateCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- ReviewCoverageRate: `{report.ReviewCoverageRate:P2}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- RedactionCoverageRate: `{report.RedactionCoverageRate:P2}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        AppendCounts(builder, "Candidate Count By Capability", report.CandidateCountByCapability);
        AppendCounts(builder, "Candidate Count By Label Kind", report.CandidateCountByLabelKind);
        builder.AppendLine("## Dataset Readiness");
        builder.AppendLine();
        builder.AppendLine("| Capability | Ready | Status | Candidates | Positive | Negative | MetadataOnly | NeedsEvidence | BlockedReasons |");
        builder.AppendLine("|---|---:|---|---:|---:|---:|---:|---:|---|");
        foreach (var item in report.ApprovedDatasetReadiness)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {Escape(item.CapabilityId)} | {item.Ready} | {Escape(item.Status)} | {item.ApprovedCandidateCount} | {item.PositiveLabelCount} | {item.NegativeLabelCount} | {item.MetadataOnlyCount} | {item.NeedsMoreEvidenceCount} | {Escape(string.Join(", ", item.BlockedReasons))} |");
        }

        if (report.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Warnings");
            builder.AppendLine();
            foreach (var warning in report.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    private static LearningFeedbackDatasetReadiness BuildReadiness(
        string capabilityId,
        IReadOnlyList<LearningFeedbackEvent> feedback,
        IReadOnlyDictionary<string, LearningFeedbackReviewRecord> latestReviews,
        IReadOnlyList<FeedbackFeatureCandidate> candidates)
    {
        var feedbackForCapability = feedback
            .Where(item => string.Equals(item.CapabilityId, capabilityId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var reviewsForCapability = feedbackForCapability
            .Select(item => latestReviews.TryGetValue(item.FeedbackId, out var review) ? review : null)
            .Where(static item => item is not null)
            .Cast<LearningFeedbackReviewRecord>()
            .ToArray();
        var approvedReviews = reviewsForCapability
            .Where(static item => item.ReviewStatus == FeedbackReviewStatus.ApprovedForDataset)
            .ToArray();
        var candidatesForCapability = candidates
            .Where(item => string.Equals(item.CapabilityId, capabilityId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var blocked = new List<string>();
        var positive = candidatesForCapability.Count(static item => item.PositiveLabel);
        var negative = candidatesForCapability.Count(static item => item.NegativeLabel);
        var metadataOnly = candidatesForCapability.Count(static item =>
            item.Metadata.TryGetValue("metadataOnly", out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        var needsEvidence = reviewsForCapability.Count(static item => item.ReviewStatus == FeedbackReviewStatus.NeedsMoreEvidence);

        if (feedbackForCapability.Length == 0)
        {
            blocked.Add(LearningFeedbackQualityBlockedReasons.NoFeedback);
        }
        if (approvedReviews.Length == 0)
        {
            blocked.Add(LearningFeedbackQualityBlockedReasons.NoApprovedFeedback);
        }
        if (feedbackForCapability.Length > reviewsForCapability.Length
            || reviewsForCapability.Any(static item => item.ReviewStatus == FeedbackReviewStatus.PendingReview))
        {
            blocked.Add(LearningFeedbackQualityBlockedReasons.NeedsReview);
        }
        if (reviewsForCapability.Any(static item => item.ReviewStatus == FeedbackReviewStatus.NeedsRedaction)
            || approvedReviews.Any(static item => !item.RedactionChecked))
        {
            blocked.Add(LearningFeedbackQualityBlockedReasons.NeedsRedaction);
        }
        if (needsEvidence > 0)
        {
            blocked.Add(LearningFeedbackQualityBlockedReasons.NeedsMoreEvidence);
        }
        if (positive == 0)
        {
            blocked.Add(LearningFeedbackQualityBlockedReasons.MissingPositiveSamples);
        }
        if (negative == 0)
        {
            blocked.Add(LearningFeedbackQualityBlockedReasons.MissingNegativeSamples);
        }
        if (candidatesForCapability.Length > 0 && metadataOnly == candidatesForCapability.Length)
        {
            blocked.Add(LearningFeedbackQualityBlockedReasons.MetadataOnlyInsufficient);
        }
        if (candidatesForCapability.Length is > 0 and < 2)
        {
            blocked.Add(LearningFeedbackQualityBlockedReasons.LabelCoverageTooLow);
        }

        blocked = [.. blocked.Distinct(StringComparer.OrdinalIgnoreCase)];
        return new LearningFeedbackDatasetReadiness
        {
            CapabilityId = capabilityId,
            ApprovedCandidateCount = candidatesForCapability.Length,
            PositiveLabelCount = positive,
            NegativeLabelCount = negative,
            MetadataOnlyCount = metadataOnly,
            NeedsMoreEvidenceCount = needsEvidence,
            Ready = blocked.Count == 0,
            Status = blocked.Count == 0
                ? LearningDatasetReadinessStatus.Ready
                : LearningDatasetReadinessStatus.NotReady,
            BlockedReasons = blocked
        };
    }

    private static string BuildRecommendation(
        int feedbackCount,
        int pendingReviewCount,
        int approvedCount,
        int needsRedactionCount,
        int candidateCount,
        IReadOnlyList<LearningFeedbackDatasetReadiness> readiness)
    {
        if (feedbackCount == 0)
        {
            return LearningFeedbackQualityRecommendations.NeedsMoreFeedback;
        }
        if (pendingReviewCount > 0 || approvedCount == 0)
        {
            return LearningFeedbackQualityRecommendations.NeedsReviewedFeedback;
        }
        if (needsRedactionCount > 0)
        {
            return LearningFeedbackQualityRecommendations.NeedsRedactionReview;
        }
        if (candidateCount == 0)
        {
            return LearningFeedbackQualityRecommendations.NotReady;
        }
        if (readiness.Any(static item => item.ApprovedCandidateCount > 0
                && (
                item.BlockedReasons.Contains(LearningFeedbackQualityBlockedReasons.MissingPositiveSamples, StringComparer.OrdinalIgnoreCase)
                || item.BlockedReasons.Contains(LearningFeedbackQualityBlockedReasons.MissingNegativeSamples, StringComparer.OrdinalIgnoreCase)
                || item.BlockedReasons.Contains(LearningFeedbackQualityBlockedReasons.LabelCoverageTooLow, StringComparer.OrdinalIgnoreCase))))
        {
            return LearningFeedbackQualityRecommendations.NeedsLabelCoverage;
        }
        if (readiness.Any(static item => item.Ready))
        {
            return LearningFeedbackQualityRecommendations.ReadyForOfflineBaseline;
        }

        return LearningFeedbackQualityRecommendations.ReadyForDatasetExport;
    }

    private static IReadOnlyList<string> BuildWarnings(
        int feedbackCount,
        int candidateCount,
        IReadOnlyList<LearningFeedbackDatasetReadiness> readiness)
    {
        var warnings = new List<string>();
        if (feedbackCount == 0)
        {
            warnings.Add("未找到 runtime feedback，无法评估数据集 readiness。");
        }
        if (feedbackCount > 0 && candidateCount == 0)
        {
            warnings.Add("已有 runtime feedback，但当前没有可导出的 feature candidate。");
        }
        if (readiness.All(static item => !item.Ready))
        {
            warnings.Add("所有 capability 当前都未达到离线数据集 readiness。");
        }

        return warnings;
    }

    private static IReadOnlyDictionary<string, int> CountBy(
        IReadOnlyList<FeedbackFeatureCandidate> candidates,
        Func<FeedbackFeatureCandidate, string> selector)
    {
        return candidates
            .GroupBy(selector, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static double Ratio(int numerator, int denominator)
    {
        return denominator <= 0 ? 0 : (double)numerator / denominator;
    }

    private static bool IsDisabledTrainingUse(string trainingUse)
    {
        return string.IsNullOrWhiteSpace(trainingUse)
            || string.Equals(trainingUse, "disabled_until_review", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendCounts(
        StringBuilder builder,
        string title,
        IReadOnlyDictionary<string, int> counts)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine("| Key | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var pair in counts)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(pair.Key)} | {pair.Value} |");
        }
        if (counts.Count == 0)
        {
            builder.AppendLine("| - | 0 |");
        }
        builder.AppendLine();
    }

    private static string Escape(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace("|", "\\|", StringComparison.Ordinal);
    }
}
