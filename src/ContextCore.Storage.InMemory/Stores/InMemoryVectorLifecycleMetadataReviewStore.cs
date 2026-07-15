using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.InMemory;

/// <summary>内存版 lifecycle metadata review 历史存储；用于测试和 smoke。</summary>
public sealed class InMemoryVectorLifecycleMetadataReviewStore : IVectorLifecycleMetadataReviewStore
{
    private readonly ConcurrentDictionary<string, VectorLifecycleMetadataReviewRecord> _items =
        new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(
        VectorLifecycleMetadataReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = ReviewRecordNormalizer.Normalize(record);
        _items[normalized.ReviewId] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> ListAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<VectorLifecycleMetadataReviewRecord>>(
        [
            .. _items.Values
                .Where(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static item => item.ReviewedAt)
                .Select(ReviewRecordNormalizer.Clone)
        ]);
    }

    public Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<VectorLifecycleMetadataReviewRecord>>(
        [
            .. _items.Values
                .Where(item => string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(collectionId)
                    || string.Equals(item.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static item => item.ReviewedAt)
                .Select(ReviewRecordNormalizer.Clone)
        ]);
    }
}
