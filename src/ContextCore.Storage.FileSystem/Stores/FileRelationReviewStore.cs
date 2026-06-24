using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 Relation review / lifecycle 审核历史存储。</summary>
public sealed class FileRelationReviewStore : IRelationReviewStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileRelationReviewStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task AppendReviewAsync(
        RelationReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);
        if (string.IsNullOrWhiteSpace(normalized.CollectionId))
        {
            throw new ArgumentException("Relation review 必须包含 collectionId。", nameof(record));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetRelationReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<RelationReviewRecord>(path, cancellationToken).ConfigureAwait(false);
            var updated = existing
                .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
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

    public async Task<IReadOnlyList<RelationReviewRecord>> QueryReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<RelationReviewRecord>();
            foreach (var scope in EnumerateScopes())
            {
                var path = _paths.GetRelationReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var items = await _jsonLines.ReadAsync<RelationReviewRecord>(path, cancellationToken).ConfigureAwait(false);
                results.AddRange(items.Where(item => string.Equals(item.RelationId, relationId, StringComparison.OrdinalIgnoreCase)));
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetRelationReviewsJsonlPath(workspaceId, collectionId);
            var items = await _jsonLines.ReadAsync<RelationReviewRecord>(path, cancellationToken).ConfigureAwait(false);
            return
            [
                .. items
                    .Where(predicate)
                    .OrderByDescending(static item => item.CreatedAt)
                    .Select(Clone)
            ];
        }
        finally
        {
            _gate.Release();
        }
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
                    if (string.IsNullOrWhiteSpace(workspaceId))
                    {
                        return Array.Empty<ShortTermMemoryScope>();
                    }

                    var collectionsRoot = Path.Combine(workspaceDirectory, "collections");
                    if (!Directory.Exists(collectionsRoot))
                    {
                        return Array.Empty<ShortTermMemoryScope>();
                    }

                    return
                    [
                        .. Directory.EnumerateDirectories(collectionsRoot)
                            .Select(collectionDirectory => new
                            {
                                WorkspaceId = workspaceId!,
                                CollectionId = Path.GetFileName(collectionDirectory)
                            })
                            .Where(item => !string.IsNullOrWhiteSpace(item.CollectionId))
                            .Where(item =>
                                File.Exists(_paths.GetRelationReviewsJsonlPath(item.WorkspaceId, item.CollectionId!)))
                            .Select(item => new ShortTermMemoryScope
                            {
                                WorkspaceId = item.WorkspaceId,
                                CollectionId = item.CollectionId!
                            })
                    ];
                })
                .DistinctBy(scope => $"{scope.WorkspaceId}\u001f{scope.CollectionId}", StringComparer.OrdinalIgnoreCase)
        ];
    }

    private static RelationReviewRecord Normalize(RelationReviewRecord record)
    {
        var createdAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
        return new RelationReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            RelationId = record.RelationId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            Action = record.Action,
            FromLifecycle = record.FromLifecycle,
            ToLifecycle = record.ToLifecycle,
            FromReviewStatus = record.FromReviewStatus,
            ToReviewStatus = record.ToReviewStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            RelationType = record.RelationType,
            SourceId = record.SourceId,
            TargetId = record.TargetId,
            EvidenceRefs = [.. record.EvidenceRefs],
            SourceRefs = [.. record.SourceRefs],
            CreatedAt = createdAt,
            ReviewedAt = record.ReviewedAt == default ? createdAt : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. record.Warnings],
            Errors = [.. record.Errors]
        };
    }

    private static RelationReviewRecord Clone(RelationReviewRecord record) => Normalize(record);

    private static string ResolveReviewStatus(RelationReviewRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.ToReviewStatus))
        {
            return record.ToReviewStatus;
        }

        return string.IsNullOrWhiteSpace(record.Action) ? "Unknown" : record.Action;
    }
}
