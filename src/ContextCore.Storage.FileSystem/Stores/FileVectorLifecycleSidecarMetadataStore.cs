using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>文件系统版 lifecycle sidecar metadata 存储；只保存旁路 override。</summary>
public sealed class FileVectorLifecycleSidecarMetadataStore : IVectorLifecycleSidecarMetadataStore
{
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public FileVectorLifecycleSidecarMetadataStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileVectorLifecycleSidecarMetadataStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _scopeCatalog = new FileScopeCatalog(paths);
    }

    public async Task SaveAsync(
        VectorLifecycleSidecarMetadataEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = CandidateRecordNormalizer.Normalize(entry);
        var path = _paths.GetVectorLifecycleSidecarMetadataJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
        var key = BuildKey(normalized);

        await _jsonLines.UpdateAsync<VectorLifecycleSidecarMetadataEntry>(
            path,
            existing => existing
                .Where(item => !string.Equals(BuildKey(item), key, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var results = new List<VectorLifecycleSidecarMetadataEntry>();
        foreach (var scope in _scopeCatalog.ResolveScopes(workspaceId, collectionId, _paths.GetVectorLifecycleSidecarMetadataJsonlPath))
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
                .Select(CandidateRecordNormalizer.Clone)
        ];
    }

    private static string BuildKey(VectorLifecycleSidecarMetadataEntry entry)
        => string.Join('\u001f', entry.WorkspaceId, entry.CollectionId, entry.SourceReviewId, entry.ItemId);
}
