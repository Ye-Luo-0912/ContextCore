using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的 Stable review 候选项存储。</summary>
public sealed class InMemoryStableReviewCandidateStore : IStableReviewCandidateStore
{
    private readonly ConcurrentDictionary<string, StableReviewCandidate> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StableReviewRecord> _reviews = new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(StableReviewCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(candidate);
        _candidates[normalized.StableReviewCandidateId] = normalized;
        return Task.CompletedTask;
    }

    public Task<StableReviewCandidate?> GetAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _candidates.TryGetValue(stableReviewCandidateId, out var candidate)
                ? Clone(candidate)
                : null);
    }

    public Task<IReadOnlyList<StableReviewCandidate>> QueryAsync(
        StableReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _candidates.Values
            .Where(candidate => Matches(candidate, query))
            .OrderByDescending(static candidate => candidate.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<StableReviewCandidate>>(results);
    }

    public Task AppendReviewAsync(
        StableReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(record);
        _reviews[normalized.ReviewId] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StableReviewRecord>> QueryReviewsAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _reviews.Values
            .Where(item => string.Equals(item.StableReviewCandidateId, stableReviewCandidateId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.CreatedAt)
            .Select(Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<StableReviewRecord>>(results);
    }

    private static bool Matches(StableReviewCandidate candidate, StableReviewCandidateQuery query)
    {
        return string.Equals(candidate.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(candidate.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(candidate.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Status) || string.Equals(candidate.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.ValidationStatus) || string.Equals(candidate.ValidationStatus, query.ValidationStatus, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.Kind) || string.Equals(candidate.Kind, query.Kind, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query.SuggestedStableTarget) || string.Equals(candidate.SuggestedStableTarget, query.SuggestedStableTarget, StringComparison.OrdinalIgnoreCase));
    }

    private static StableReviewCandidate Normalize(StableReviewCandidate candidate)
    {
        return new StableReviewCandidate
        {
            StableReviewCandidateId = string.IsNullOrWhiteSpace(candidate.StableReviewCandidateId)
                ? Guid.NewGuid().ToString("N")
                : candidate.StableReviewCandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            SourceCandidateId = candidate.SourceCandidateId,
            SourceTargetItemId = candidate.SourceTargetItemId,
            SourceLearningCaseId = candidate.SourceLearningCaseId,
            Kind = candidate.Kind,
            Title = candidate.Title,
            Summary = candidate.Summary,
            SuggestedStableTarget = candidate.SuggestedStableTarget,
            Reason = candidate.Reason,
            Confidence = candidate.Confidence,
            Importance = candidate.Importance,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            RiskFlags = candidate.RiskFlags.ToArray(),
            ValidationStatus = string.IsNullOrWhiteSpace(candidate.ValidationStatus)
                ? StableReviewValidationStatuses.ReadyForReview
                : candidate.ValidationStatus,
            CreatedAt = candidate.CreatedAt == default ? DateTimeOffset.UtcNow : candidate.CreatedAt,
            Status = string.IsNullOrWhiteSpace(candidate.Status)
                ? StableReviewCandidateStatuses.Candidate
                : candidate.Status,
            Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static StableReviewCandidate Clone(StableReviewCandidate candidate) => Normalize(candidate);

    private static StableReviewRecord Normalize(StableReviewRecord item)
    {
        var createdAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt;
        return new StableReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            StableReviewCandidateId = item.StableReviewCandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            StableTargetItemId = item.StableTargetItemId,
            StableTargetItemKind = item.StableTargetItemKind,
            TargetLayer = item.TargetLayer,
            SourcePromotionCandidateId = item.SourcePromotionCandidateId,
            SourceTargetItemId = item.SourceTargetItemId,
            SourceLearningCaseId = item.SourceLearningCaseId,
            EvidenceRefs = item.EvidenceRefs.ToArray(),
            ValidationStatus = string.IsNullOrWhiteSpace(item.ValidationStatus)
                ? StableReviewValidationStatuses.ReadyForReview
                : item.ValidationStatus,
            RiskFlags = item.RiskFlags.ToArray(),
            CreatedAt = createdAt,
            ReviewedAt = item.ReviewedAt == default ? createdAt : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = item.Warnings.ToArray(),
            Errors = item.Errors.ToArray()
        };
    }

    private static StableReviewRecord Clone(StableReviewRecord item) => Normalize(item);
}
