using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

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

        var normalized = Normalize(record);
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
            .Select(Clone)
            .ToArray();
        return Task.FromResult<IReadOnlyList<StableLifecycleReviewRecord>>(results);
    }

    private static StableLifecycleReviewRecord Normalize(StableLifecycleReviewRecord record)
    {
        var createdAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
        return new StableLifecycleReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            StableItemId = record.StableItemId,
            StableKind = record.StableKind,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            Action = record.Action,
            FromStatus = record.FromStatus,
            ToStatus = record.ToStatus,
            FromLifecycle = record.FromLifecycle,
            ToLifecycle = record.ToLifecycle,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            ReplacementItemId = record.ReplacementItemId,
            EvidenceRefs = record.EvidenceRefs.ToArray(),
            SourceRefs = record.SourceRefs.ToArray(),
            CreatedAt = createdAt,
            ReviewedAt = record.ReviewedAt == default ? createdAt : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = record.Warnings.ToArray(),
            Errors = record.Errors.ToArray()
        };
    }

    private static StableLifecycleReviewRecord Clone(StableLifecycleReviewRecord record) => Normalize(record);
}
