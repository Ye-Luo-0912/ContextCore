using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>基于内存的 CandidateConstraint 审核记录存储。</summary>
public sealed class InMemoryCandidateConstraintReviewStore : ICandidateConstraintReviewStore
{
    private readonly ConcurrentDictionary<string, CandidateConstraintReviewRecord> _reviews = new(StringComparer.OrdinalIgnoreCase);

    public Task AppendReviewAsync(
        CandidateConstraintReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = ReviewRecordNormalizer.Normalize(record);
        _reviews[normalized.ReviewId] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CandidateConstraintReviewRecord>> QueryReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _reviews.Values
            .Where(item => string.Equals(item.ConstraintId, constraintId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.CreatedAt)
            .Select(ReviewRecordNormalizer.Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<CandidateConstraintReviewRecord>>(results);
    }
}
