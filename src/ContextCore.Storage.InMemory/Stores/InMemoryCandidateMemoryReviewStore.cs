using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>基于内存的 CandidateMemory review / cleanup 审核历史存储。</summary>
public sealed class InMemoryCandidateMemoryReviewStore : ICandidateMemoryReviewStore
{
    private readonly ConcurrentDictionary<string, CandidateMemoryReviewRecord> _reviews = new(StringComparer.OrdinalIgnoreCase);

    public Task AppendReviewAsync(
        CandidateMemoryReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = ReviewRecordNormalizer.Normalize(record);
        _reviews[normalized.ReviewId] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CandidateMemoryReviewRecord>> QueryReviewsAsync(
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
        return Task.FromResult<IReadOnlyList<CandidateMemoryReviewRecord>>(results);
    }
}
