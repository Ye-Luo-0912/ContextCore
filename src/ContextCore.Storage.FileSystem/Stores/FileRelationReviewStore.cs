using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 Relation review / lifecycle 审核历史存储。</summary>
public sealed class FileRelationReviewStore : IRelationReviewStore
{
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public FileRelationReviewStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _scopeCatalog = new FileScopeCatalog(paths);
    }

    public async Task AppendReviewAsync(
        RelationReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = ReviewRecordNormalizer.Normalize(record);
        if (string.IsNullOrWhiteSpace(normalized.CollectionId))
        {
            throw new ArgumentException("Relation review 必须包含 collectionId。", nameof(record));
        }

        var path = _paths.GetRelationReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
        await _jsonLines.UpdateAsync<RelationReviewRecord>(
            path,
            existing => existing
                .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderByDescending(static item => item.CreatedAt)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelationReviewRecord>> QueryReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);

        var results = new List<RelationReviewRecord>();
        foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetRelationReviewsJsonlPath))
        {
            var path = _paths.GetRelationReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
            var items = await _jsonLines.ReadAsync<RelationReviewRecord>(path, cancellationToken).ConfigureAwait(false);
            results.AddRange(items.Where(item => string.Equals(item.RelationId, relationId, StringComparison.OrdinalIgnoreCase)));
        }

        return
        [
            .. results
                .OrderByDescending(static item => item.CreatedAt)
                .Select(ReviewRecordNormalizer.Clone)
        ];
    }

    public async Task<IReadOnlyList<RelationReviewRecord>> QueryByScopeAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        return await QueryScopedAsync(
            workspaceId,
            collectionId,
            static _ => true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelationReviewRecord>> QueryByReviewStatusAsync(
        string workspaceId,
        string collectionId,
        string reviewStatus,
        CancellationToken cancellationToken = default)
    {
        return await QueryScopedAsync(
            workspaceId,
            collectionId,
            item => string.Equals(ResolveReviewStatus(item), reviewStatus, StringComparison.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelationReviewRecord>> QueryByReviewerAsync(
        string workspaceId,
        string collectionId,
        string reviewer,
        CancellationToken cancellationToken = default)
    {
        return await QueryScopedAsync(
            workspaceId,
            collectionId,
            item => string.Equals(item.Reviewer, reviewer, StringComparison.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelationReviewRecord>> QueryByOperationIdAsync(
        string workspaceId,
        string collectionId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return await QueryScopedAsync(
            workspaceId,
            collectionId,
            item => item.Metadata.TryGetValue("operationId", out var value)
                && string.Equals(value, operationId, StringComparison.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationReviewRecord?> GetLatestReviewAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        var reviews = await QueryReviewsAsync(relationId, cancellationToken).ConfigureAwait(false);
        return reviews.FirstOrDefault();
    }

    private async Task<IReadOnlyList<RelationReviewRecord>> QueryScopedAsync(
        string workspaceId,
        string collectionId,
        Func<RelationReviewRecord, bool> predicate,
        CancellationToken cancellationToken)
    {
        var path = _paths.GetRelationReviewsJsonlPath(workspaceId, collectionId);
        var items = await _jsonLines.ReadAsync<RelationReviewRecord>(path, cancellationToken).ConfigureAwait(false);
        return
        [
            .. items
                .Where(predicate)
                .OrderByDescending(static item => item.CreatedAt)
                .Select(ReviewRecordNormalizer.Clone)
        ];
    }

    private static string ResolveReviewStatus(RelationReviewRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.ToReviewStatus))
        {
            return record.ToReviewStatus;
        }

        return string.IsNullOrWhiteSpace(record.Action) ? "Unknown" : record.Action;
    }
}
