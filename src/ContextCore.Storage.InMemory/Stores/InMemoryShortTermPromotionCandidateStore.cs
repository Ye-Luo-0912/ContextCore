using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

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
        var normalized = CandidateRecordNormalizer.Normalize(candidate);
        _candidates[normalized.CandidateId] = normalized;
        return Task.CompletedTask;
    }

    public Task<ShortTermPromotionCandidate?> GetAsync(string candidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _candidates.TryGetValue(candidateId, out var candidate)
                ? CandidateRecordNormalizer.Clone(candidate)
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
            .Select(CandidateRecordNormalizer.Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ShortTermPromotionCandidate>>(results);
    }

    public Task AppendReviewAsync(
        PromotionCandidateReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = ReviewRecordNormalizer.Normalize(record);
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
            .Select(ReviewRecordNormalizer.Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<PromotionCandidateReviewRecord>>(results);
    }
}
