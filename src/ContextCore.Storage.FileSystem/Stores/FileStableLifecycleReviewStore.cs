using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 Stable memory 生命周期 review 审核历史存储。</summary>
public sealed class FileStableLifecycleReviewStore : IStableLifecycleReviewStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;
    private readonly FileScopeCatalog _scopeCatalog;

    public FileStableLifecycleReviewStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
        _scopeCatalog = new FileScopeCatalog(paths);
    }

    public async Task AppendReviewAsync(
        StableLifecycleReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = ReviewRecordNormalizer.Normalize(record);
        if (string.IsNullOrWhiteSpace(normalized.CollectionId))
        {
            throw new ArgumentException("Stable lifecycle review 必须包含 collectionId。", nameof(record));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = _paths.GetStableLifecycleReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            var legacyPath = _paths.GetLegacyStableLifecycleReviewsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);
            // legacy 是只读迁移源，锁外预读即可。
            var legacy = await _jsonLines.ReadAsync<StableLifecycleReviewRecord>(legacyPath, cancellationToken)
                .ConfigureAwait(false);

            // 跨进程锁内 RMW primary 路径——读 primary + 合并 legacy + 过滤+追加+排序 + 原子写回。
            await _jsonLines.UpdateAsync<StableLifecycleReviewRecord>(
                path,
                primaryExisting =>
                {
                    var merged = MergeLegacyReviews(primaryExisting, legacy);
                    return merged
                        .Where(item => !string.Equals(item.ReviewId, normalized.ReviewId, StringComparison.OrdinalIgnoreCase))
                        .Append(normalized)
                        .OrderByDescending(static item => item.CreatedAt)
                        .ToArray();
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 将 legacy review 合并到 primary 集合，按 ReviewId 去重——primary 优先。
    /// </summary>
    private static IReadOnlyList<StableLifecycleReviewRecord> MergeLegacyReviews(
        IReadOnlyList<StableLifecycleReviewRecord> primary,
        IReadOnlyList<StableLifecycleReviewRecord> legacy)
    {
        if (legacy.Count == 0)
        {
            return primary;
        }

        var keys = primary
            .Where(item => !string.IsNullOrWhiteSpace(item.ReviewId))
            .Select(item => item.ReviewId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. primary,
            .. legacy.Where(item => string.IsNullOrWhiteSpace(item.ReviewId) || keys.Add(item.ReviewId))
        ];
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
            foreach (var scope in _scopeCatalog.EnumerateScopes(_paths.GetStableLifecycleReviewsJsonlPath, _paths.GetLegacyStableLifecycleReviewsJsonlPath))
            {
                var items = await ReadReviewsWithLegacyAsync(scope.WorkspaceId, scope.CollectionId, cancellationToken)
                    .ConfigureAwait(false);
                results.AddRange(items.Where(item => string.Equals(item.StableItemId, stableItemId, StringComparison.OrdinalIgnoreCase)));
            }

            return
            [
                .. results
                    .OrderByDescending(static item => item.CreatedAt)
                    .Select(ReviewRecordNormalizer.Clone)
            ];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<StableLifecycleReviewRecord>> ReadReviewsWithLegacyAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var primaryPath = _paths.GetStableLifecycleReviewsJsonlPath(workspaceId, collectionId);
        var primary = await _jsonLines.ReadAsync<StableLifecycleReviewRecord>(primaryPath, cancellationToken)
            .ConfigureAwait(false);
        var legacyPath = _paths.GetLegacyStableLifecycleReviewsJsonlPath(workspaceId, collectionId);
        if (string.Equals(primaryPath, legacyPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(legacyPath))
        {
            return primary;
        }

        var legacy = await _jsonLines.ReadAsync<StableLifecycleReviewRecord>(legacyPath, cancellationToken)
            .ConfigureAwait(false);
        if (legacy.Count == 0)
        {
            return primary;
        }

        var keys = primary
            .Where(item => !string.IsNullOrWhiteSpace(item.ReviewId))
            .Select(item => item.ReviewId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. primary,
            .. legacy.Where(item => string.IsNullOrWhiteSpace(item.ReviewId) || keys.Add(item.ReviewId))
        ];
    }
}
