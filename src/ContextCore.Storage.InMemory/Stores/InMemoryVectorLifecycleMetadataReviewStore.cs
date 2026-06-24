using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

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

        var normalized = Normalize(record);
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
                .Select(Clone)
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
                .Select(Clone)
        ]);
    }

    private static VectorLifecycleMetadataReviewRecord Normalize(VectorLifecycleMetadataReviewRecord record)
        => new()
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            CandidateId = record.CandidateId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            MustHitItemId = record.MustHitItemId,
            Decision = record.Decision,
            ResultStatus = record.ResultStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            ProposedLifecycle = record.ProposedLifecycle,
            ProposedReviewStatus = record.ProposedReviewStatus,
            ProposedTargetSection = record.ProposedTargetSection,
            EvidenceRefs = [.. record.EvidenceRefs],
            SourceRefs = [.. record.SourceRefs],
            SidecarWritten = record.SidecarWritten,
            UnsafeApprovalBlocked = record.UnsafeApprovalBlocked,
            BlockedReason = record.BlockedReason,
            ReviewedAt = record.ReviewedAt == default ? DateTimeOffset.UtcNow : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase)
        };

    private static VectorLifecycleMetadataReviewRecord Clone(VectorLifecycleMetadataReviewRecord record)
        => Normalize(record);
}
