using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的短期晋升候选项存储。</summary>
public sealed class InMemoryShortTermPromotionCandidateStore : IShortTermPromotionCandidateStore
{
    private readonly ConcurrentDictionary<string, ShortTermPromotionCandidate> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PromotionCandidateReviewRecord> _reviews = new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(ShortTermPromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(candidate);
        _candidates[normalized.CandidateId] = normalized;
        return Task.CompletedTask;
    }

    public Task<ShortTermPromotionCandidate?> GetAsync(string candidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _candidates.TryGetValue(candidateId, out var candidate)
                ? Clone(candidate)
                : null);
    }

    public Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryAsync(
        ShortTermPromotionCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _candidates.Values
            .Where(item => string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.CollectionId) || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.SessionId) || string.Equals(item.SessionId, query.SessionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => query.Status is null || item.Status == query.Status.Value)
            .Where(item => string.IsNullOrWhiteSpace(query.Kind) || string.Equals(item.Kind, query.Kind, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.SuggestedTargetLayer) || string.Equals(item.SuggestedTargetLayer, query.SuggestedTargetLayer, StringComparison.OrdinalIgnoreCase))
            .Where(item => query.MinConfidence is null || item.Confidence >= query.MinConfidence.Value)
            .Where(item => query.MinImportance is null || item.Importance >= query.MinImportance.Value)
            .OrderByDescending(item => item.CreatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .Select(Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ShortTermPromotionCandidate>>(results);
    }

    public Task AppendReviewAsync(
        PromotionCandidateReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(record);
        _reviews[normalized.ReviewId] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PromotionCandidateReviewRecord>> QueryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _reviews.Values
            .Where(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt)
            .Select(Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<PromotionCandidateReviewRecord>>(results);
    }

    private static ShortTermPromotionCandidate Normalize(ShortTermPromotionCandidate item)
    {
        return new ShortTermPromotionCandidate
        {
            CandidateId = string.IsNullOrWhiteSpace(item.CandidateId) ? Guid.NewGuid().ToString("N") : item.CandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            SourceWorkingItemId = item.SourceWorkingItemId,
            Kind = item.Kind,
            Title = item.Title,
            Summary = item.Summary,
            SuggestedTargetLayer = item.SuggestedTargetLayer,
            Reason = item.Reason,
            Confidence = item.Confidence,
            Importance = item.Importance,
            EvidenceRefs = item.EvidenceRefs.ToArray(),
            Tags = item.Tags.ToArray(),
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            Status = item.Status,
            DedupeKey = item.DedupeKey,
            SourceFingerprint = item.SourceFingerprint,
            GeneratedBy = item.GeneratedBy,
            PolicyVersion = item.PolicyVersion,
            RuleName = item.RuleName,
            RuleVersion = item.RuleVersion,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ShortTermPromotionCandidate Clone(ShortTermPromotionCandidate item) => Normalize(item);

    private static PromotionCandidateReviewRecord Normalize(PromotionCandidateReviewRecord item)
    {
        var createdAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt;
        return new PromotionCandidateReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            CandidateId = item.CandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            TargetItemId = item.TargetItemId,
            TargetItemKind = item.TargetItemKind,
            TargetLayer = item.TargetLayer,
            EvidenceRefs = item.EvidenceRefs.ToArray(),
            CreatedAt = createdAt,
            ReviewedAt = item.ReviewedAt == default ? createdAt : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = item.Warnings.ToArray(),
            Errors = item.Errors.ToArray()
        };
    }

    private static PromotionCandidateReviewRecord Clone(PromotionCandidateReviewRecord item) => Normalize(item);
}
