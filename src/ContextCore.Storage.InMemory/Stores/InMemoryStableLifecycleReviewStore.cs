using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的 Stable memory 生命周期 review 审核历史存储。</summary>
public sealed class InMemoryStableLifecycleReviewStore : IStableLifecycleReviewStore
{
    private readonly ConcurrentDictionary<string, StableLifecycleReviewRecord> _reviews = new(StringComparer.OrdinalIgnoreCase);

    public Task AppendReviewAsync(
        StableLifecycleReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = ReviewRecordNormalizer.Normalize(record);
        _reviews[normalized.ReviewId] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StableLifecycleReviewRecord>> QueryReviewsAsync(
        string stableItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableItemId);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _reviews.Values
            .Where(item => string.Equals(item.StableItemId, stableItemId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt)
            .Select(ReviewRecordNormalizer.Clone)
            .ToArray();
        return Task.FromResult<IReadOnlyList<StableLifecycleReviewRecord>>(results);
    }
}
