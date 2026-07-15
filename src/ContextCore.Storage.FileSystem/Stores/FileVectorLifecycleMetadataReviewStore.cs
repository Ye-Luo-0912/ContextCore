using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>文件系统版 lifecycle metadata review 历史存储；只记录人工决策。</summary>
public sealed class FileVectorLifecycleMetadataReviewStore : IVectorLifecycleMetadataReviewStore
{
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public FileVectorLifecycleMetadataReviewStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileVectorLifecycleMetadataReviewStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _scopeCatalog = new FileScopeCatalog(paths);
    }

    public async Task SaveAsync(
        VectorLifecycleMetadataReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = ReviewRecordNormalizer.Normalize(record);
        var path = _paths.GetVectorLifecycleMetadataReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);

        await _jsonLines.UpdateAsync<VectorLifecycleMetadataReviewRecord>(
            path,
            existing => existing
                .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.ReviewedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> ListAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        var results = new List<VectorLifecycleMetadataReviewRecord>();
        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetVectorLifecycleMetadataReviewsJsonlPath))
        {
            var path = _paths.GetVectorLifecycleMetadataReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var records = await _jsonLines.ReadAsync<VectorLifecycleMetadataReviewRecord>(path, cancellationToken)
                .ConfigureAwait(false);
            results.AddRange(records.Where(item => string.Equals(item.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase)));
        }

        return
        [
            .. results
                .OrderByDescending(static item => item.ReviewedAt)
                .Select(ReviewRecordNormalizer.Clone)
        ];
    }

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var results = new List<VectorLifecycleMetadataReviewRecord>();
        foreach (var scope in _scopeCatalog.ResolveScopes(workspaceId, collectionId, _paths.GetVectorLifecycleMetadataReviewsJsonlPath))
        {
            var path = _paths.GetVectorLifecycleMetadataReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var records = await _jsonLines.ReadAsync<VectorLifecycleMetadataReviewRecord>(path, cancellationToken)
                .ConfigureAwait(false);
            results.AddRange(records);
        }

        return
        [
            .. results
                .OrderByDescending(static item => item.ReviewedAt)
                .Select(ReviewRecordNormalizer.Clone)
        ];
    }
}
