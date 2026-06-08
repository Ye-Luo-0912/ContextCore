using ContextCore.Abstractions;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 Stable memory 生命周期 review 审核历史存储。</summary>
public sealed class FileStableLifecycleReviewStore : IStableLifecycleReviewStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileStableLifecycleReviewStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task AppendReviewAsync(
        StableLifecycleReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);
        if (string.IsNullOrWhiteSpace(normalized.CollectionId))
        {
            throw new ArgumentException("Stable lifecycle review 必须包含 collectionId。", nameof(record));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetStableLifecycleReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var existing = await _jsonLines.ReadAsync<StableLifecycleReviewRecord>(path, cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<StableLifecycleReviewRecord>> QueryReviewsAsync(
        string stableItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableItemId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<StableLifecycleReviewRecord>();
            foreach (var scope in EnumerateScopes())
            {
                var path = _paths.GetStableLifecycleReviewsJsonlPath(scope.WorkspaceId, scope.CollectionId);
                var items = await _jsonLines.ReadAsync<StableLifecycleReviewRecord>(path, cancellationToken).ConfigureAwait(false);
                results.AddRange(items.Where(item => string.Equals(item.StableItemId, stableItemId, StringComparison.OrdinalIgnoreCase)));
            }

            return results
                .OrderByDescending(static item => item.CreatedAt)
                .Select(Clone)
                .ToArray();
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

        return Directory.EnumerateDirectories(workspacesRoot)
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

                return Directory.EnumerateDirectories(collectionsRoot)
                    .Select(collectionDirectory => new
                    {
                        WorkspaceId = workspaceId!,
                        CollectionId = Path.GetFileName(collectionDirectory)
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.CollectionId))
                    .Where(item => File.Exists(_paths.GetStableLifecycleReviewsJsonlPath(item.WorkspaceId, item.CollectionId!)))
                    .Select(item => new ShortTermMemoryScope
                    {
                        WorkspaceId = item.WorkspaceId,
                        CollectionId = item.CollectionId!
                    })
                    .ToArray();
            })
            .DistinctBy(scope => $"{scope.WorkspaceId}\u001f{scope.CollectionId}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
