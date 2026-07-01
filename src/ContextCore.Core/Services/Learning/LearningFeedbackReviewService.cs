using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>运行时反馈审核服务；只管理离线数据集准入，不修改任何正式策略。</summary>
public sealed class LearningFeedbackReviewService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    /// V13: "disabled_until_evidence_ready" — human review is no longer a training prerequisite.
    private const string DisabledTrainingUse = "disabled_until_evidence_ready";
    private readonly ILearningFeedbackStore _feedbackStore;
    private readonly ILearningFeedbackReviewStore _reviewStore;

    public LearningFeedbackReviewService(
        ILearningFeedbackStore feedbackStore,
        ILearningFeedbackReviewStore reviewStore)
    {
        _feedbackStore = feedbackStore;
        _reviewStore = reviewStore;
    }

    public Task<LearningFeedbackReviewResult> ApproveAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(feedbackId, FeedbackReviewStatus.ApprovedForDataset, request, cancellationToken);
    }

    public Task<LearningFeedbackReviewResult> RejectAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(feedbackId, FeedbackReviewStatus.Rejected, request, cancellationToken);
    }

    public Task<LearningFeedbackReviewResult> NeedsRedactionAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(feedbackId, FeedbackReviewStatus.NeedsRedaction, request, cancellationToken);
    }

    public Task<LearningFeedbackReviewResult> NeedsMoreEvidenceAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(feedbackId, FeedbackReviewStatus.NeedsMoreEvidence, request, cancellationToken);
    }

    public Task<IReadOnlyList<LearningFeedbackReviewRecord>> ListAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        return _reviewStore.QueryAsync(NormalizeQuery(query), cancellationToken);
    }

    public async Task<LearningFeedbackReviewSummaryReport> BuildSummaryAsync(
        LearningFeedbackEventQuery feedbackQuery,
        LearningFeedbackReviewQuery reviewQuery,
        CancellationToken cancellationToken = default)
    {
        var feedback = await _feedbackStore.QueryAsync(new LearningFeedbackEventQuery
            {
                WorkspaceId = feedbackQuery.WorkspaceId,
                CollectionId = feedbackQuery.CollectionId,
                Source = feedbackQuery.Source,
                SourceOperationId = feedbackQuery.SourceOperationId,
                CapabilityId = feedbackQuery.CapabilityId,
                TargetId = feedbackQuery.TargetId,
                TargetType = feedbackQuery.TargetType,
                FeedbackKind = feedbackQuery.FeedbackKind,
                Limit = int.MaxValue
            }, cancellationToken)
            .ConfigureAwait(false);
        var reviews = await _reviewStore.QueryAsync(new LearningFeedbackReviewQuery
            {
                FeedbackId = reviewQuery.FeedbackId,
                ReviewStatus = reviewQuery.ReviewStatus,
                Reviewer = reviewQuery.Reviewer,
                Limit = int.MaxValue
            }, cancellationToken)
            .ConfigureAwait(false);
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
        var pendingCount = feedback.Count(item => !latestReviews.ContainsKey(item.FeedbackId));

        return new LearningFeedbackReviewSummaryReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            FeedbackCount = feedback.Count,
            PendingReviewCount = pendingCount,
            ApprovedCount = CountStatus(latestReviews.Values, FeedbackReviewStatus.ApprovedForDataset),
            RejectedCount = CountStatus(latestReviews.Values, FeedbackReviewStatus.Rejected),
            NeedsRedactionCount = CountStatus(latestReviews.Values, FeedbackReviewStatus.NeedsRedaction),
            NeedsMoreEvidenceCount = CountStatus(latestReviews.Values, FeedbackReviewStatus.NeedsMoreEvidence),
            ReviewsByStatus = latestReviews.Values
                .GroupBy(static item => item.ReviewStatus.ToString(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase),
            RecentReviews = [.. latestReviews.Values
                .OrderByDescending(static item => item.ReviewedAt)
                .Take(20)],
            Warnings = feedback.Count == 0
                ? ["未找到可审核的运行时反馈。"]
                : Array.Empty<string>()
        };
    }

    public static string BuildMarkdownReport(LearningFeedbackReviewSummaryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Learning Feedback Review Summary");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- FeedbackCount: `{report.FeedbackCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- PendingReviewCount: `{report.PendingReviewCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- ApprovedCount: `{report.ApprovedCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- RejectedCount: `{report.RejectedCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- NeedsRedactionCount: `{report.NeedsRedactionCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- NeedsMoreEvidenceCount: `{report.NeedsMoreEvidenceCount}`");
        builder.AppendLine();
        builder.AppendLine("| Status | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var pair in report.ReviewsByStatus)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(pair.Key)} | {pair.Value} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Recent Reviews");
        builder.AppendLine();
        builder.AppendLine("| FeedbackId | Status | Reviewer | TrainingUse | RedactionChecked | ReviewedAt |");
        builder.AppendLine("|---|---|---|---|---:|---|");
        foreach (var item in report.RecentReviews)
        {
            builder.AppendLine(
                $"| {Escape(item.FeedbackId)} | {item.ReviewStatus} | {Escape(item.Reviewer)} | {Escape(item.TrainingUse)} | {item.RedactionChecked} | {item.ReviewedAt:O} |");
        }

        return builder.ToString();
    }

    private async Task<LearningFeedbackReviewResult> ReviewAsync(
        string feedbackId,
        FeedbackReviewStatus status,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedFeedbackId = Require(feedbackId, nameof(feedbackId));
        var feedback = await _feedbackStore.GetAsync(normalizedFeedbackId, cancellationToken)
            .ConfigureAwait(false);
        if (feedback is null)
        {
            throw new ArgumentException($"Feedback '{normalizedFeedbackId}' was not found.", nameof(feedbackId));
        }

        var warnings = new List<string>();
        var metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["workspaceId"] = feedback.WorkspaceId,
            ["collectionId"] = feedback.CollectionId,
            ["feedbackKind"] = feedback.FeedbackKind,
            ["targetType"] = feedback.TargetType,
            ["targetId"] = feedback.TargetId,
            ["capabilityId"] = feedback.CapabilityId
        };
        var trainingUse = NormalizeTrainingUse(status, request.TrainingUse);
        if (status == FeedbackReviewStatus.ApprovedForDataset
            && string.Equals(trainingUse, DisabledTrainingUse, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("approved feedback still has disabled trainingUse and will not generate feature candidates.");
        }

        var review = new LearningFeedbackReviewRecord
        {
            FeedbackId = normalizedFeedbackId,
            Reviewer = string.IsNullOrWhiteSpace(request.Reviewer) ? "manual" : request.Reviewer.Trim(),
            ReviewStatus = status,
            ReviewReason = request.ReviewReason.Trim(),
            ApprovedCapability = string.IsNullOrWhiteSpace(request.ApprovedCapability)
                ? feedback.CapabilityId
                : request.ApprovedCapability.Trim(),
            ApprovedLabelKind = string.IsNullOrWhiteSpace(request.ApprovedLabelKind)
                ? feedback.FeedbackKind
                : request.ApprovedLabelKind.Trim(),
            RedactionChecked = request.RedactionChecked,
            TrainingUse = trainingUse,
            ReviewedAt = DateTimeOffset.UtcNow,
            Metadata = metadata
        };

        await _reviewStore.UpsertAsync(review, cancellationToken)
            .ConfigureAwait(false);
        return new LearningFeedbackReviewResult
        {
            FeedbackId = normalizedFeedbackId,
            ReviewStatus = status,
            Review = review,
            Warnings = warnings
        };
    }

    private static LearningFeedbackReviewQuery NormalizeQuery(LearningFeedbackReviewQuery query)
    {
        return new LearningFeedbackReviewQuery
        {
            FeedbackId = NormalizeOptional(query.FeedbackId),
            ReviewStatus = query.ReviewStatus,
            Reviewer = NormalizeOptional(query.Reviewer),
            Limit = query.Limit > 0 ? query.Limit : 100,
            Offset = Math.Max(0, query.Offset)
        };
    }

    private static int CountStatus(
        IEnumerable<LearningFeedbackReviewRecord> rows,
        FeedbackReviewStatus status)
    {
        return rows.Count(item => item.ReviewStatus == status);
    }

    private static string NormalizeTrainingUse(FeedbackReviewStatus status, string trainingUse)
    {
        if (!string.IsNullOrWhiteSpace(trainingUse))
        {
            return trainingUse.Trim();
        }

        return status == FeedbackReviewStatus.ApprovedForDataset
            ? "approved_for_dataset"
            : DisabledTrainingUse;
    }

    private static string Require(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string Escape(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace("|", "\\|", StringComparison.Ordinal);
    }
}

/// <summary>从已审核反馈生成离线特征候选；不接入训练或运行时策略。</summary>
public sealed class LearningFeedbackFeatureCandidateBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    /// V13: "disabled_until_evidence_ready"
    private const string DisabledTrainingUse = "disabled_until_evidence_ready";
    private readonly ILearningFeedbackStore _feedbackStore;
    private readonly ILearningFeedbackReviewStore _reviewStore;

    public LearningFeedbackFeatureCandidateBuilder(
        ILearningFeedbackStore feedbackStore,
        ILearningFeedbackReviewStore reviewStore)
    {
        _feedbackStore = feedbackStore;
        _reviewStore = reviewStore;
    }

    public async Task<LearningFeedbackFeatureCandidateReport> BuildAsync(
        LearningFeedbackEventQuery query,
        bool updateNeedsMoreEvidence = false,
        CancellationToken cancellationToken = default)
    {
        var feedback = await _feedbackStore.QueryAsync(new LearningFeedbackEventQuery
            {
                WorkspaceId = query.WorkspaceId,
                CollectionId = query.CollectionId,
                Source = query.Source,
                SourceOperationId = query.SourceOperationId,
                CapabilityId = query.CapabilityId,
                TargetId = query.TargetId,
                TargetType = query.TargetType,
                FeedbackKind = query.FeedbackKind,
                Limit = int.MaxValue
            }, cancellationToken)
            .ConfigureAwait(false);
        var reviews = await _reviewStore.QueryAsync(new LearningFeedbackReviewQuery { Limit = int.MaxValue }, cancellationToken)
            .ConfigureAwait(false);
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

        var candidates = new List<FeedbackFeatureCandidate>();
        var warnings = new List<string>();
        var needsMoreEvidence = 0;
        var needsRedaction = 0;
        var rejected = 0;
        var pending = 0;

        foreach (var item in feedback)
        {
            if (!latestReviews.TryGetValue(item.FeedbackId, out var review))
            {
                pending++;
                continue;
            }

            if (review.ReviewStatus == FeedbackReviewStatus.Rejected)
            {
                rejected++;
                continue;
            }

            if (review.ReviewStatus == FeedbackReviewStatus.NeedsRedaction)
            {
                needsRedaction++;
                continue;
            }

            if (review.ReviewStatus == FeedbackReviewStatus.NeedsMoreEvidence)
            {
                needsMoreEvidence++;
                continue;
            }

            if (review.ReviewStatus != FeedbackReviewStatus.ApprovedForDataset)
            {
                pending++;
                continue;
            }

            if (IsDisabledTrainingUse(review.TrainingUse))
            {
                warnings.Add($"feedback {item.FeedbackId} skipped because trainingUse is disabled.");
                continue;
            }

            if (!review.RedactionChecked)
            {
                needsRedaction++;
                warnings.Add($"feedback {item.FeedbackId} skipped because redaction was not checked.");
                continue;
            }

            if (MissingRequiredBinding(item))
            {
                needsMoreEvidence++;
                warnings.Add($"feedback {item.FeedbackId} requires sourceOperationId, targetId and capabilityId before dataset use.");
                if (updateNeedsMoreEvidence)
                {
                    await MarkNeedsMoreEvidenceAsync(item, review, cancellationToken)
                        .ConfigureAwait(false);
                }
                continue;
            }

            var candidate = TryBuildCandidate(item, review);
            if (candidate is null)
            {
                warnings.Add($"feedback {item.FeedbackId} did not match a supported feature candidate mapping.");
                continue;
            }

            candidates.Add(candidate);
        }

        return new LearningFeedbackFeatureCandidateReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            FeedbackScanned = feedback.Count,
            ReviewScanned = latestReviews.Count,
            GeneratedCandidateCount = candidates.Count,
            PendingReviewCount = pending,
            NeedsMoreEvidenceCount = needsMoreEvidence,
            NeedsRedactionCount = needsRedaction,
            RejectedCount = rejected,
            CandidatesByCapability = candidates
                .GroupBy(static item => item.CapabilityId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase),
            Candidates = candidates
                .OrderBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Warnings = warnings
        };
    }

    public static string ExportJsonLines(LearningFeedbackFeatureCandidateReport report)
    {
        return string.Join(Environment.NewLine, report.Candidates.Select(static item => JsonSerializer.Serialize(item, JsonOptions)));
    }

    public static string BuildMarkdownReport(LearningFeedbackFeatureCandidateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Learning Feedback Feature Candidates");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- FeedbackScanned: `{report.FeedbackScanned}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- ReviewScanned: `{report.ReviewScanned}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- GeneratedCandidateCount: `{report.GeneratedCandidateCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- PendingReviewCount: `{report.PendingReviewCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- NeedsMoreEvidenceCount: `{report.NeedsMoreEvidenceCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- NeedsRedactionCount: `{report.NeedsRedactionCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- RejectedCount: `{report.RejectedCount}`");
        builder.AppendLine();
        builder.AppendLine("| Capability | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var pair in report.CandidatesByCapability)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(pair.Key)} | {pair.Value} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Candidates");
        builder.AppendLine();
        builder.AppendLine("| CandidateId | FeedbackId | Capability | Target | LabelKind | Positive | Negative | TrainingUse |");
        builder.AppendLine("|---|---|---|---|---|---:|---:|---|");
        foreach (var item in report.Candidates.Take(50))
        {
            builder.AppendLine(
                $"| {Escape(item.CandidateId)} | {Escape(item.SourceFeedbackId)} | {Escape(item.CapabilityId)} | {Escape(item.TargetType)}:{Escape(item.TargetRef)} | {Escape(item.LabelKind)} | {item.PositiveLabel} | {item.NegativeLabel} | {Escape(item.TrainingUse)} |");
        }

        return builder.ToString();
    }

    private static FeedbackFeatureCandidate? TryBuildCandidate(
        LearningFeedbackEvent feedback,
        LearningFeedbackReviewRecord review)
    {
        var mapping = ResolveMapping(feedback);
        if (mapping is null)
        {
            return null;
        }

        var metadata = new Dictionary<string, string>(feedback.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceFeedbackId"] = feedback.FeedbackId,
            ["sourceOperationId"] = feedback.SourceOperationId,
            ["metadataOnly"] = feedback.MetadataOnly ? "true" : "false",
            ["reviewedAt"] = review.ReviewedAt.ToString("O"),
            ["metadataSafe"] = feedback.MetadataOnly ? "true" : "false",
            ["excludedFromTraining"] = IsSmokeTrainingUse(review.TrainingUse) || IsEnabled(feedback.Metadata, "excludedFromTraining")
                ? "true"
                : "false"
        };
        foreach (var pair in review.Metadata)
        {
            metadata[$"review.{pair.Key}"] = pair.Value;
        }

        return new FeedbackFeatureCandidate
        {
            CandidateId = BuildCandidateId(feedback, review),
            SourceFeedbackId = feedback.FeedbackId,
            CapabilityId = string.IsNullOrWhiteSpace(review.ApprovedCapability)
                ? feedback.CapabilityId
                : review.ApprovedCapability,
            TargetType = feedback.TargetType,
            LabelKind = string.IsNullOrWhiteSpace(review.ApprovedLabelKind)
                ? mapping.Value.LabelKind
                : review.ApprovedLabelKind,
            PositiveLabel = mapping.Value.Positive,
            NegativeLabel = mapping.Value.Negative,
            QueryText = feedback.MetadataOnly ? string.Empty : feedback.UserCorrection,
            ContextRef = feedback.SourceOperationId,
            TargetRef = feedback.TargetId,
            Reason = feedback.MetadataOnly ? string.Empty : feedback.Reason,
            TrainingUse = review.TrainingUse,
            RedactionStatus = feedback.MetadataOnly ? "metadata-only" : "reviewed",
            ReviewStatus = review.ReviewStatus,
            Metadata = metadata
        };
    }

    private static (string LabelKind, bool Positive, bool Negative)? ResolveMapping(LearningFeedbackEvent feedback)
    {
        return feedback.CapabilityId switch
        {
            ShadowCapabilityIds.RouterIntentClassifier
                when IsKind(feedback, LearningFeedbackKinds.WrongIntent)
                => ("router_intent_correction", false, true),
            ShadowCapabilityIds.CandidateReranker
                when IsKind(feedback, LearningFeedbackKinds.RankingWrong)
                    || IsKind(feedback, LearningFeedbackKinds.WrongCandidate)
                => ("ranking_pair_candidate", false, true),
            ShadowCapabilityIds.VectorRetrieval
                when IsKind(feedback, LearningFeedbackKinds.MissingContext)
                => ("vector_recall_candidate", true, false),
            ShadowCapabilityIds.VectorRetrieval
                when IsKind(feedback, LearningFeedbackKinds.DeprecatedContext)
                => ("vector_risk_candidate", false, true),
            ShadowCapabilityIds.GraphExpansion
                when IsKind(feedback, LearningFeedbackKinds.ConstraintMissing)
                => ("graph_section_candidate", true, false),
            ShadowCapabilityIds.GraphExpansion
                when IsKind(feedback, LearningFeedbackKinds.DeprecatedContext)
                => ("graph_routing_candidate", false, true),
            ShadowCapabilityIds.PromotionJudge
                when IsKind(feedback, LearningFeedbackKinds.ShouldPromote)
                => ("promotion_label_candidate", true, false),
            ShadowCapabilityIds.PromotionJudge
                when IsKind(feedback, LearningFeedbackKinds.PromotionWrong)
                => ("promotion_label_candidate", false, true),
            ShadowCapabilityIds.ConstraintGapJudge
                when IsKind(feedback, LearningFeedbackKinds.ConstraintMissing)
                    || IsKind(feedback, LearningFeedbackKinds.ConstraintIncorrect)
                => ("constraint_gap_label_candidate", IsKind(feedback, LearningFeedbackKinds.ConstraintMissing), IsKind(feedback, LearningFeedbackKinds.ConstraintIncorrect)),
            _ => null
        };
    }

    private async Task MarkNeedsMoreEvidenceAsync(
        LearningFeedbackEvent feedback,
        LearningFeedbackReviewRecord previousReview,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>(previousReview.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["autoMarkedBy"] = "LearningFeedbackFeatureCandidateBuilder",
            ["missingBinding"] = "sourceOperationId,targetId,capabilityId"
        };
        await _reviewStore.UpsertAsync(new LearningFeedbackReviewRecord
            {
                FeedbackId = feedback.FeedbackId,
                Reviewer = previousReview.Reviewer,
                ReviewStatus = FeedbackReviewStatus.NeedsMoreEvidence,
                ReviewReason = "缺少生成离线候选所需的 sourceOperationId、targetId 或 capabilityId。",
                ApprovedCapability = previousReview.ApprovedCapability,
                ApprovedLabelKind = previousReview.ApprovedLabelKind,
                RedactionChecked = previousReview.RedactionChecked,
                TrainingUse = DisabledTrainingUse,
                ReviewedAt = DateTimeOffset.UtcNow,
                Metadata = metadata
            }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool MissingRequiredBinding(LearningFeedbackEvent feedback)
    {
        return string.IsNullOrWhiteSpace(feedback.SourceOperationId)
            || string.IsNullOrWhiteSpace(feedback.TargetId)
            || string.IsNullOrWhiteSpace(feedback.CapabilityId);
    }

    private static bool IsDisabledTrainingUse(string trainingUse)
    {
        return string.IsNullOrWhiteSpace(trainingUse)
            || string.Equals(trainingUse, DisabledTrainingUse, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSmokeTrainingUse(string trainingUse)
    {
        return string.Equals(trainingUse, "smoke_test_only", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnabled(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value)
            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKind(LearningFeedbackEvent feedback, string kind)
    {
        return string.Equals(feedback.FeedbackKind, kind, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCandidateId(
        LearningFeedbackEvent feedback,
        LearningFeedbackReviewRecord review)
    {
        var input = string.Join("|", feedback.FeedbackId, review.ReviewStatus, review.ApprovedCapability, review.ApprovedLabelKind);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "lfc_" + Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static string Escape(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace("|", "\\|", StringComparison.Ordinal);
    }
}
