using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>内存版 lifecycle sidecar metadata 存储；只保存旁路 override。</summary>
public sealed class InMemoryVectorLifecycleSidecarMetadataStore : IVectorLifecycleSidecarMetadataStore
{
    private readonly ConcurrentDictionary<string, VectorLifecycleSidecarMetadataEntry> _items =
        new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(
        VectorLifecycleSidecarMetadataEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(entry);
        _items[BuildKey(normalized)] = normalized;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>>(
        [
            .. _items.Values
                .Where(item => string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(collectionId)
                    || string.Equals(item.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static item => item.CreatedAt)
                .Select(Clone)
        ]);
    }

    private static string BuildKey(VectorLifecycleSidecarMetadataEntry entry)
        => string.Join('\u001f', entry.WorkspaceId, entry.CollectionId, entry.SourceReviewId, entry.ItemId);

    private static VectorLifecycleSidecarMetadataEntry Normalize(VectorLifecycleSidecarMetadataEntry entry)
        => new()
        {
            ItemId = entry.ItemId,
            WorkspaceId = entry.WorkspaceId,
            CollectionId = entry.CollectionId,
            LifecycleOverride = entry.LifecycleOverride,
            ReviewStatusOverride = entry.ReviewStatusOverride,
            TargetSectionOverride = entry.TargetSectionOverride,
            SourceReviewId = entry.SourceReviewId,
            SourceCandidateId = entry.SourceCandidateId,
            Reviewer = entry.Reviewer,
            Reason = entry.Reason,
            EvidenceRefs = [.. entry.EvidenceRefs],
            SourceRefs = [.. entry.SourceRefs],
            CreatedAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt,
            PolicyVersion = string.IsNullOrWhiteSpace(entry.PolicyVersion)
                ? "vector-lifecycle-sidecar/v1"
                : entry.PolicyVersion,
            Metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase)
        };

    private static VectorLifecycleSidecarMetadataEntry Clone(VectorLifecycleSidecarMetadataEntry entry)
        => Normalize(entry);
}
