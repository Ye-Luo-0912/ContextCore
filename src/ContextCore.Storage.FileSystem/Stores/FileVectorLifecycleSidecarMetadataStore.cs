using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>文件系统版 lifecycle sidecar metadata 存储；只保存旁路 override。</summary>
public sealed class FileVectorLifecycleSidecarMetadataStore : IVectorLifecycleSidecarMetadataStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileVectorLifecycleSidecarMetadataStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileVectorLifecycleSidecarMetadataStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(
        VectorLifecycleSidecarMetadataEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = Normalize(entry);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetVectorLifecycleSidecarMetadataJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<VectorLifecycleSidecarMetadataEntry>(path, cancellationToken)
                .ConfigureAwait(false);
            var key = BuildKey(normalized);
            var updated = existing
                .Where(item => !string.Equals(BuildKey(item), key, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.CreatedAt)
                .ToArray();
            await _jsonLines.WriteAsync(path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<VectorLifecycleSidecarMetadataEntry>();
            foreach (var scope in ResolveScopes(workspaceId, collectionId))
            {
                var path = _paths.GetVectorLifecycleSidecarMetadataJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var entries = await _jsonLines.ReadAsync<VectorLifecycleSidecarMetadataEntry>(path, cancellationToken)
                    .ConfigureAwait(false);
                results.AddRange(entries);
            }

            return
            [
                .. results
                    .OrderByDescending(static item => item.CreatedAt)
                    .Select(Clone)
            ];
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<ShortTermMemoryScope> ResolveScopes(string workspaceId, string? collectionId)
    {
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            return [new ShortTermMemoryScope { WorkspaceId = workspaceId, CollectionId = collectionId }];
        }

        return
        [
            .. EnumerateScopes()
                .Where(scope => string.Equals(scope.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
        ];
    }

    private IReadOnlyList<ShortTermMemoryScope> EnumerateScopes()
    {
        var workspacesRoot = Path.Combine(_paths.RootPath, "workspaces");
        if (!Directory.Exists(workspacesRoot))
        {
            return Array.Empty<ShortTermMemoryScope>();
        }

        return
        [
            .. Directory.EnumerateDirectories(workspacesRoot)
                .SelectMany(workspaceDirectory =>
                {
                    var workspaceId = Path.GetFileName(workspaceDirectory);
                    var collectionsRoot = Path.Combine(workspaceDirectory, "collections");
                    if (string.IsNullOrWhiteSpace(workspaceId) || !Directory.Exists(collectionsRoot))
                    {
                        return Array.Empty<ShortTermMemoryScope>();
                    }

                    return
                    [
                        .. Directory.EnumerateDirectories(collectionsRoot)
                            .Select(collectionDirectory => new
                            {
                                WorkspaceId = workspaceId,
                                CollectionId = Path.GetFileName(collectionDirectory)
                            })
                            .Where(item => !string.IsNullOrWhiteSpace(item.CollectionId))
                            .Where(item => File.Exists(_paths.GetVectorLifecycleSidecarMetadataJsonlPath(item.WorkspaceId, item.CollectionId!)))
                            .Select(item => new ShortTermMemoryScope
                            {
                                WorkspaceId = item.WorkspaceId,
                                CollectionId = item.CollectionId
                            })
                    ];
                })
                .DistinctBy(scope => $"{scope.WorkspaceId}\u001f{scope.CollectionId}", StringComparer.OrdinalIgnoreCase)
        ];
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
