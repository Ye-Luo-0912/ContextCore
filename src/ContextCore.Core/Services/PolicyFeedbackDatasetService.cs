using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>从既有人工 review history 聚合只读策略反馈数据集。</summary>
public sealed class PolicyFeedbackDatasetService
{
    public const string PolicyVersion = "policy-feedback-dataset/v1";
    public const string EvalBaselineRef =
        "docs/eval-baseline-p15.md;eval/eval-report-p15-a3.json;eval/eval-report-p15-extended.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IShortTermPromotionCandidateStore? _promotionCandidateStore;
    private readonly IStableReviewCandidateStore? _stableReviewCandidateStore;
    private readonly IConstraintGapCandidateStore? _constraintGapStore;
    private readonly ICandidateConstraintReviewStore? _candidateConstraintReviewStore;
    private readonly IConstraintStore? _constraintStore;

    public PolicyFeedbackDatasetService(
        IShortTermPromotionCandidateStore? promotionCandidateStore,
        IStableReviewCandidateStore? stableReviewCandidateStore,
        IConstraintGapCandidateStore? constraintGapStore,
        ICandidateConstraintReviewStore? candidateConstraintReviewStore,
        IConstraintStore? constraintStore)
    {
        _promotionCandidateStore = promotionCandidateStore;
        _stableReviewCandidateStore = stableReviewCandidateStore;
        _constraintGapStore = constraintGapStore;
        _candidateConstraintReviewStore = candidateConstraintReviewStore;
        _constraintStore = constraintStore;
    }

    public async Task<PolicyFeedbackDataset> BuildAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int limit = 200,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var records = new List<PolicyFeedbackRecord>();
        records.AddRange(await LoadPromotionReviewsAsync(workspaceId, collectionId, sessionId, cancellationToken)
            .ConfigureAwait(false));
        records.AddRange(await LoadStableReviewsAsync(workspaceId, collectionId, sessionId, cancellationToken)
            .ConfigureAwait(false));
        records.AddRange(await LoadConstraintGapReviewsAsync(workspaceId, collectionId, sessionId, cancellationToken)
            .ConfigureAwait(false));
        records.AddRange(await LoadCandidateConstraintReviewsAsync(workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false));

        var ordered = records
            .OrderByDescending(record => record.CreatedAt)
            .ThenBy(record => record.FeedbackRecordId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var page = ordered
            .Skip(Math.Max(0, offset))
            .Take(limit > 0 ? limit : 200)
            .ToArray();
        var sourceTypes = ordered
            .GroupBy(record => record.SourceType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new PolicyFeedbackDataset
        {
            DatasetId = BuildDatasetId(workspaceId, collectionId, sessionId, ordered.Length),
            Name = "Policy Feedback Dataset",
            Scope = BuildScope(workspaceId, collectionId, sessionId),
            CreatedAt = DateTimeOffset.UtcNow,
            Records = page,
            PositiveCount = ordered.Count(record => IsLabel(record, PolicyFeedbackLabels.Positive)),
            NegativeCount = ordered.Count(record => IsLabel(record, PolicyFeedbackLabels.Negative)),
            NeutralCount = ordered.Count(record => IsLabel(record, PolicyFeedbackLabels.Neutral)),
            SourceTypes = sourceTypes,
            PolicyVersion = PolicyVersion,
            EvalBaselineRef = EvalBaselineRef
        };
    }

    public async Task<string> ExportJsonLinesAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int limit = 1000,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var dataset = await BuildAsync(workspaceId, collectionId, sessionId, limit, offset, cancellationToken)
            .ConfigureAwait(false);
        return string.Join(
            Environment.NewLine,
            dataset.Records.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
    }

    private async Task<IReadOnlyList<PolicyFeedbackRecord>> LoadPromotionReviewsAsync(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (_promotionCandidateStore is null)
        {
            return Array.Empty<PolicyFeedbackRecord>();
        }

        var candidates = await _promotionCandidateStore.QueryAsync(new ShortTermPromotionCandidateQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Limit = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var records = new List<PolicyFeedbackRecord>();
        foreach (var candidate in candidates)
        {
            var reviews = await _promotionCandidateStore.QueryReviewsAsync(candidate.CandidateId, cancellationToken)
                .ConfigureAwait(false);
            records.AddRange(reviews.Select(review => FromPromotionReview(candidate, review)));
        }

        return records;
    }

    private async Task<IReadOnlyList<PolicyFeedbackRecord>> LoadStableReviewsAsync(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (_stableReviewCandidateStore is null)
        {
            return Array.Empty<PolicyFeedbackRecord>();
        }

        var candidates = await _stableReviewCandidateStore.QueryAsync(new StableReviewCandidateQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Limit = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var records = new List<PolicyFeedbackRecord>();
        foreach (var candidate in candidates)
        {
            var reviews = await _stableReviewCandidateStore.QueryReviewsAsync(candidate.StableReviewCandidateId, cancellationToken)
                .ConfigureAwait(false);
            records.AddRange(reviews.Select(review => FromStableReview(candidate, review)));
        }

        return records;
    }

    private async Task<IReadOnlyList<PolicyFeedbackRecord>> LoadConstraintGapReviewsAsync(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (_constraintGapStore is null)
        {
            return Array.Empty<PolicyFeedbackRecord>();
        }

        var gaps = await _constraintGapStore.QueryAsync(new ConstraintGapCandidateQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Limit = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var records = new List<PolicyFeedbackRecord>();
        foreach (var gap in gaps)
        {
            var reviews = await _constraintGapStore.QueryReviewsAsync(gap.GapId, cancellationToken)
                .ConfigureAwait(false);
            records.AddRange(reviews.Select(review => FromConstraintGapReview(gap, review)));
        }

        return records;
    }

    private async Task<IReadOnlyList<PolicyFeedbackRecord>> LoadCandidateConstraintReviewsAsync(
        string workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        if (_constraintStore is null || _candidateConstraintReviewStore is null)
        {
            return Array.Empty<PolicyFeedbackRecord>();
        }

        var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var records = new List<PolicyFeedbackRecord>();
        foreach (var constraint in constraints.Where(IsCandidateConstraintSource))
        {
            var reviews = await _candidateConstraintReviewStore.QueryReviewsAsync(constraint.Id, cancellationToken)
                .ConfigureAwait(false);
            records.AddRange(reviews.Select(review => FromCandidateConstraintReview(constraint, review)));
        }

        return records;
    }

    private static PolicyFeedbackRecord FromPromotionReview(
        ShortTermPromotionCandidate candidate,
        PromotionCandidateReviewRecord review)
    {
        var label = ResolveLabel(review.Action);
        var evidenceRefs = DistinctRefs(review.EvidenceRefs, candidate.EvidenceRefs);
        var metadata = BuildMetadata(review.Metadata, ("candidateId", candidate.CandidateId));
        metadata["sourceWorkingItemId"] = candidate.SourceWorkingItemId;
        metadata["candidateKind"] = candidate.Kind;
        metadata["candidateStatus"] = candidate.Status.ToString();

        return new PolicyFeedbackRecord
        {
            FeedbackRecordId = BuildRecordId("promotion", review.ReviewId),
            WorkspaceId = review.WorkspaceId,
            CollectionId = review.CollectionId,
            SessionId = review.SessionId,
            SourceType = "PromotionCandidateReviewRecord",
            SourceId = review.ReviewId,
            Action = review.Action,
            Label = label,
            Reason = review.Reason,
            PositiveRefs = IsPositive(label) ? evidenceRefs : Array.Empty<string>(),
            NegativeRefs = IsNegative(label) ? evidenceRefs : Array.Empty<string>(),
            EvidenceRefs = evidenceRefs,
            TargetLayer = review.TargetLayer ?? candidate.SuggestedTargetLayer,
            CreatedAt = ResolveCreatedAt(review.CreatedAt, review.ReviewedAt),
            Reviewer = review.Reviewer,
            PolicyVersion = FirstNonEmpty(candidate.PolicyVersion, GetMetadataValue(review.Metadata, "policyVersion"), PolicyVersion),
            Metadata = metadata
        };
    }

    private static PolicyFeedbackRecord FromStableReview(
        StableReviewCandidate candidate,
        StableReviewRecord review)
    {
        var label = ResolveLabel(review.Action);
        var evidenceRefs = DistinctRefs(review.EvidenceRefs, candidate.EvidenceRefs);
        var metadata = BuildMetadata(review.Metadata, ("stableReviewCandidateId", candidate.StableReviewCandidateId));
        metadata["validationStatus"] = review.ValidationStatus;
        metadata["suggestedStableTarget"] = candidate.SuggestedStableTarget;
        metadata["sourcePromotionCandidateId"] = review.SourcePromotionCandidateId;
        metadata["sourceLearningCaseId"] = review.SourceLearningCaseId ?? string.Empty;

        return new PolicyFeedbackRecord
        {
            FeedbackRecordId = BuildRecordId("stable-review", review.ReviewId),
            WorkspaceId = review.WorkspaceId,
            CollectionId = review.CollectionId,
            SessionId = review.SessionId,
            SourceType = "StableReviewRecord",
            SourceId = review.ReviewId,
            Action = review.Action,
            Label = label,
            Reason = review.Reason,
            PositiveRefs = IsPositive(label) ? evidenceRefs : Array.Empty<string>(),
            NegativeRefs = IsNegative(label) ? evidenceRefs : Array.Empty<string>(),
            EvidenceRefs = evidenceRefs,
            TargetLayer = review.TargetLayer ?? candidate.SuggestedStableTarget,
            CreatedAt = ResolveCreatedAt(review.CreatedAt, review.ReviewedAt),
            Reviewer = review.Reviewer,
            PolicyVersion = FirstNonEmpty(GetMetadataValue(candidate.Metadata, "policyVersion"), GetMetadataValue(review.Metadata, "policyVersion"), PolicyVersion),
            Metadata = metadata
        };
    }

    private static PolicyFeedbackRecord FromConstraintGapReview(
        ConstraintGapCandidate gap,
        ConstraintGapReviewRecord review)
    {
        var label = ResolveLabel(review.Action);
        var evidenceRefs = DistinctRefs(review.EvidenceRefs, gap.EvidenceRefs);
        var metadata = BuildMetadata(review.Metadata, ("gapId", gap.GapId));
        metadata["sourceSampleId"] = review.SourceSampleId;
        metadata["sourceOperationId"] = review.SourceOperationId;
        metadata["expectedConstraintText"] = review.ExpectedConstraintText;

        return new PolicyFeedbackRecord
        {
            FeedbackRecordId = BuildRecordId("constraint-gap", review.ReviewId),
            WorkspaceId = review.WorkspaceId,
            CollectionId = review.CollectionId,
            SessionId = review.SessionId,
            SourceType = "ConstraintGapReviewRecord",
            SourceId = review.ReviewId,
            Action = review.Action,
            Label = label,
            Reason = review.Reason,
            PositiveRefs = IsPositive(label) ? evidenceRefs : Array.Empty<string>(),
            NegativeRefs = IsNegative(label) ? evidenceRefs : Array.Empty<string>(),
            EvidenceRefs = evidenceRefs,
            TargetLayer = review.TargetLayer ?? gap.SuggestedConstraintType,
            CreatedAt = ResolveCreatedAt(review.CreatedAt, review.ReviewedAt),
            Reviewer = review.Reviewer,
            PolicyVersion = FirstNonEmpty(GetMetadataValue(review.Metadata, "policyVersion"), GetMetadataValue(gap.Metadata, "policyVersion"), PolicyVersion),
            Metadata = metadata
        };
    }

    private static PolicyFeedbackRecord FromCandidateConstraintReview(
        ContextConstraint constraint,
        CandidateConstraintReviewRecord review)
    {
        var label = ResolveLabel(review.Action);
        var evidenceRefs = DistinctRefs(review.EvidenceRefs, constraint.SourceRefs, SplitRefs(GetMetadataValue(constraint.Metadata, "evidenceRefs")));
        var metadata = BuildMetadata(review.Metadata, ("constraintId", constraint.Id));
        metadata["sourceConstraintGapId"] = review.SourceConstraintGapId;
        metadata["sourceSampleId"] = review.SourceSampleId;
        metadata["sourceOperationId"] = review.SourceOperationId;

        return new PolicyFeedbackRecord
        {
            FeedbackRecordId = BuildRecordId("candidate-constraint", review.ReviewId),
            WorkspaceId = review.WorkspaceId,
            CollectionId = review.CollectionId ?? string.Empty,
            SessionId = null,
            SourceType = "CandidateConstraintReviewRecord",
            SourceId = review.ReviewId,
            Action = review.Action,
            Label = label,
            Reason = review.Reason,
            PositiveRefs = IsPositive(label) ? evidenceRefs : Array.Empty<string>(),
            NegativeRefs = IsNegative(label) ? evidenceRefs : Array.Empty<string>(),
            EvidenceRefs = evidenceRefs,
            TargetLayer = review.ActivatedConstraintId is null ? "RejectedCandidateConstraint" : "ActiveHardConstraint",
            CreatedAt = ResolveCreatedAt(review.CreatedAt, review.ReviewedAt),
            Reviewer = review.Reviewer,
            PolicyVersion = FirstNonEmpty(GetMetadataValue(review.Metadata, "policyVersion"), GetMetadataValue(constraint.Metadata, "policyVersion"), PolicyVersion),
            Metadata = metadata
        };
    }

    private static bool IsCandidateConstraintSource(ContextConstraint constraint)
    {
        return constraint.Metadata.ContainsKey("sourceConstraintGapId")
            || (constraint.Metadata.TryGetValue("createdFrom", out var createdFrom)
                && (string.Equals(createdFrom, "constraint_gap_accept", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(createdFrom, "candidate_constraint_activate", StringComparison.OrdinalIgnoreCase)));
    }

    private static string ResolveLabel(string action)
    {
        if (string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "accepted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "activate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "activated", StringComparison.OrdinalIgnoreCase))
        {
            return PolicyFeedbackLabels.Positive;
        }

        return string.Equals(action, "reject", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "rejected", StringComparison.OrdinalIgnoreCase)
                ? PolicyFeedbackLabels.Negative
                : PolicyFeedbackLabels.Neutral;
    }

    private static string BuildDatasetId(string workspaceId, string? collectionId, string? sessionId, int recordCount)
        => $"pfd-{BuildShortHash($"{workspaceId}\u001f{collectionId}\u001f{sessionId}\u001f{recordCount}\u001f{PolicyVersion}")}";

    private static string BuildRecordId(string prefix, string reviewId)
        => $"pfr-{BuildShortHash($"{prefix}\u001f{reviewId}")}";

    private static string BuildShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string BuildScope(string workspaceId, string? collectionId, string? sessionId)
    {
        var builder = new StringBuilder($"workspace:{workspaceId}");
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            builder.Append($"/collection:{collectionId}");
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            builder.Append($"/session:{sessionId}");
        }

        return builder.ToString();
    }

    private static Dictionary<string, string> BuildMetadata(
        IReadOnlyDictionary<string, string> source,
        params (string Key, string Value)[] pairs)
    {
        var metadata = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase)
        {
            ["generatedBy"] = "PolicyFeedbackDatasetService",
            ["policyVersion"] = PolicyVersion,
            ["evalBaselineRef"] = EvalBaselineRef
        };

        foreach (var pair in pairs)
        {
            metadata[pair.Key] = pair.Value;
        }

        return metadata;
    }

    private static IReadOnlyList<string> DistinctRefs(params IEnumerable<string>[] refs)
    {
        return refs
            .SelectMany(static item => item)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> SplitRefs(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value
                .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
    }

    private static string? GetMetadataValue(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static DateTimeOffset ResolveCreatedAt(DateTimeOffset createdAt, DateTimeOffset reviewedAt)
    {
        if (reviewedAt != default)
        {
            return reviewedAt;
        }

        return createdAt == default ? DateTimeOffset.UtcNow : createdAt;
    }

    private static bool IsLabel(PolicyFeedbackRecord record, string label)
        => string.Equals(record.Label, label, StringComparison.OrdinalIgnoreCase);

    private static bool IsPositive(string label)
        => string.Equals(label, PolicyFeedbackLabels.Positive, StringComparison.OrdinalIgnoreCase);

    private static bool IsNegative(string label)
        => string.Equals(label, PolicyFeedbackLabels.Negative, StringComparison.OrdinalIgnoreCase);
}
