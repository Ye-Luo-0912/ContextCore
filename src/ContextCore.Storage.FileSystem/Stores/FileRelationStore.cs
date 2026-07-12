using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;
using System.Collections.Concurrent;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 <see cref="IRelationStore"/> 实现，关系数据持久化为 JSONL 文件。</summary>
/// <remarks>
/// GRAPH-11 + P0-fix: 跨实例互斥通过进程级 SemaphoreSlim 字典实现，按文件路径共享。
/// 原先用命名 Mutex，但 Mutex 是线程亲和的——async await 后线程可能切换，
/// 导致 ReleaseMutex 在非持有线程上调用并静默失败（锁泄漏）。
/// SemaphoreSlim 不是线程亲和的，Release 可在任意线程调用，适配 async 模式。
/// 读路径保留 SemaphoreSlim 保证缓存与文件一致性；metadata cache 带 mtime 双重校验。
/// </remarks>
public sealed class FileRelationStore : IRelationStore
{
    private const int MaxCacheEntries = 256;

    // P0-fix: 进程级锁注册表，按文件路径共享。
    // 同一进程内不同 FileRelationStore 实例指向同一文件时，通过此字典获取同一 SemaphoreSlim。
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_processLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    // relation adjacency cache, keyed by relations.jsonl path.
    // Invalidation: file mtime mismatch → cache miss → re-read.
    // P0-fix: mtime recheck after read prevents caching stale content.
    private readonly ConcurrentDictionary<string, RelationCacheEntry> _relationCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record RelationCacheEntry(
        DateTime LastWriteUtc,
        IReadOnlyList<ContextRelation> Relations);

    /// <summary>
    /// P0-fix: 进程级锁租约。SemaphoreSlim 不是线程亲和的，Release 可在任意线程调用。
    /// 修复原先 Mutex 在 async 代码中因线程切换导致 ReleaseMutex 静默失败的问题。
    /// </summary>
    private sealed class ProcessLockLease : IDisposable
    {
        private SemaphoreSlim? _gate;
        private bool _disposed;

        internal ProcessLockLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _gate?.Release();
            _gate = null;
        }
    }

    public FileRelationStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileRelationStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    /// <summary>
    /// 读取关系列表，带 mtime 缓存。P0-fix: 读后复核 mtime，避免在读取期间文件被替换时缓存脏数据。
    /// </summary>
    private async Task<IReadOnlyList<ContextRelation>> ReadRelationsCachedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        // 快路径：无锁检查缓存（可接受短暂过期）
        var mtimeBefore = TryGetLastWriteUtc(path);
        if (mtimeBefore is not null
            && _relationCache.TryGetValue(path, out var cached)
            && cached.LastWriteUtc == mtimeBefore.Value)
        {
            return cached.Relations;
        }

        // 慢路径：加锁读文件
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 双重检查：另一线程可能已填充缓存
            mtimeBefore = TryGetLastWriteUtc(path);
            if (mtimeBefore is not null
                && _relationCache.TryGetValue(path, out cached)
                && cached.LastWriteUtc == mtimeBefore.Value)
            {
                return cached.Relations;
            }

            var relations = await _jsonLines.ReadAsync<ContextRelation>(path, cancellationToken)
                .ConfigureAwait(false);

            // P0-fix: 读后复核 mtime；持有写锁期间不会有并发写，但仍防御性校验
            var mtimeAfter = TryGetLastWriteUtc(path);
            if (mtimeBefore is not null && mtimeAfter is not null && mtimeBefore == mtimeAfter)
            {
                EnforceCacheBound();
                _relationCache[path] = new RelationCacheEntry(mtimeAfter.Value, relations);
            }

            return relations;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void InvalidateRelationCache(string path)
    {
        _relationCache.TryRemove(path, out _);
    }

    /// <summary>P0-fix: 防止缓存无限增长；超过上限时清空（本地开发场景，简单策略）。</summary>
    private void EnforceCacheBound()
    {
        if (_relationCache.Count >= MaxCacheEntries)
        {
            _relationCache.Clear();
        }
    }

    private static DateTime? TryGetLastWriteUtc(string path)
    {
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    /// <summary>GRAPH-11：SaveAsync 委托 BatchUpsertAsync，保留为单条便利方法。</summary>
    public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
        => BatchUpsertAsync([relation], cancellationToken);

    /// <summary>按关系 ID 读取单条边；供 provider parity/diagnostics 使用。</summary>
    public async Task<ContextRelation?> GetAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetRelationsJsonlPath(workspaceId, collectionId);
        var relations = await ReadRelationsCachedAsync(path, cancellationToken)
            .ConfigureAwait(false);
        return relations.FirstOrDefault(relation =>
            string.Equals(relation.Id, relationId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 删除单条边。P0-fix: 现在走与 BatchUpsertAsync 相同的跨实例锁，避免丢失更新。
    /// </summary>
    public async Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetRelationsJsonlPath(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // P0-fix: 进程级跨实例锁，与 BatchUpsertAsync 统一
            using var fileLock = await AcquireProcessLockAsync(path, cancellationToken);
            var relations = await _jsonLines.ReadAsync<ContextRelation>(path, cancellationToken)
                .ConfigureAwait(false);
            var retained = relations
                .Where(relation => !string.Equals(relation.Id, relationId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (retained.Length == relations.Count)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            await _jsonLines.WriteAsync(path, retained, cancellationToken).ConfigureAwait(false);
            InvalidateRelationCache(path);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 批量 upsert：按 (workspaceId, collectionId) 分组，每组在跨实例互斥锁内完成读改写并原子替换文件。
    /// GRAPH-11 + P0-fix: 使用命名 Mutex 解决不同 store 实例并发读旧数据后覆盖新数据的问题。
    /// Mutex 通过 CrossInstanceLockLease 在 Dispose 中正确调用 ReleaseMutex。
    /// </summary>
    public async Task BatchUpsertAsync(
        IEnumerable<ContextRelation> relations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);

        var normalized = relations.Select(CompositeContextNormalizer.Normalize).ToArray();
        if (normalized.Length == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var group in normalized.GroupBy(r =>
                _paths.GetRelationsJsonlPath(r.WorkspaceId, r.CollectionId)))
            {
                var path = group.Key;
                // GRAPH-11 + P0-fix: 进程级跨实例互斥，通过 lease 正确释放
                using var fileLock = await AcquireProcessLockAsync(path, cancellationToken);
                var incoming = group.ToArray();
                var incomingIds = new HashSet<string>(
                    incoming.Select(r => r.Id),
                    StringComparer.OrdinalIgnoreCase);

                var existing = await _jsonLines.ReadAsync<ContextRelation>(path, cancellationToken)
                    .ConfigureAwait(false);
                var merged = existing
                    .Where(r => !incomingIds.Contains(r.Id))
                    .Concat(incoming);

                await _jsonLines.WriteAsync(path, merged, cancellationToken).ConfigureAwait(false);
                InvalidateRelationCache(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>GRAPH-10：统一邻居查询，在内存中过滤。</summary>
    public async Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(
        RelationNeighborQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var effectiveTake = query.Take > 0 ? query.Take : 100;
        var effectiveSkip = query.Skip > 0 ? query.Skip : 0;
        var maxScan = query.MaxScan > 0 ? query.MaxScan : 1000;
        var excludedLifecycles = query.ExcludedLifecycles.Count > 0
            ? new HashSet<string>(query.ExcludedLifecycles, StringComparer.OrdinalIgnoreCase)
            : null;
        var excludedReviewStatuses = query.ExcludedReviewStatuses.Count > 0
            ? new HashSet<string>(query.ExcludedReviewStatuses, StringComparer.OrdinalIgnoreCase)
            : null;
        // P3-02：多类型过滤优先于单类型
        var allowedTypes = query.AllowedRelationTypes.Count > 0
            ? new HashSet<string>(query.AllowedRelationTypes, StringComparer.OrdinalIgnoreCase)
            : null;

        // P0-fix: ReadRelationsCachedAsync 在慢路径加锁，快路径走缓存
        var path = _paths.GetRelationsJsonlPath(query.WorkspaceId, query.CollectionId ?? string.Empty);
        var relations = await ReadRelationsCachedAsync(path, cancellationToken)
            .ConfigureAwait(false);

        IEnumerable<ContextRelation> filtered = query.Direction switch
        {
            RelationDirection.Outgoing => relations.Where(relation =>
                string.Equals(relation.SourceId, query.ItemId, StringComparison.OrdinalIgnoreCase)),
            RelationDirection.Incoming => relations.Where(relation =>
                string.Equals(relation.TargetId, query.ItemId, StringComparison.OrdinalIgnoreCase)),
            _ => relations.Where(relation =>
                string.Equals(relation.SourceId, query.ItemId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(relation.TargetId, query.ItemId, StringComparison.OrdinalIgnoreCase))
        };

        if (allowedTypes is not null)
        {
            filtered = filtered.Where(relation => allowedTypes.Contains(relation.RelationType));
        }
        else if (!string.IsNullOrWhiteSpace(query.RelationType))
        {
            filtered = filtered.Where(relation =>
                string.Equals(relation.RelationType, query.RelationType, StringComparison.OrdinalIgnoreCase));
        }

        if (query.MinConfidence > 0)
        {
            filtered = filtered.Where(relation => relation.Confidence >= query.MinConfidence);
        }

        if (excludedLifecycles is not null)
        {
            filtered = filtered.Where(relation => !excludedLifecycles.Contains(relation.Lifecycle ?? string.Empty));
        }

        if (excludedReviewStatuses is not null)
        {
            filtered = filtered.Where(relation => !excludedReviewStatuses.Contains(relation.ReviewStatus ?? string.Empty));
        }

        // P5-0.3: 先排序再 Take(maxScan)，避免文件后部高权重关系永远进不了结果。
        // 文件已被完整读入内存，提前 Take 并不减少磁盘 I/O，反而丢失正确性。
        return [.. filtered
            .OrderByDescending(relation => relation.Weight)
            .ThenByDescending(relation => relation.Confidence)
            .ThenByDescending(relation => relation.CreatedAt)
            .Take(maxScan)
            .Skip(effectiveSkip)
            .Take(effectiveTake)];
    }

    public async Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // P0-fix: ReadRelationsCachedAsync 在慢路径加锁，快路径走缓存
        var relations = new List<ContextRelation>();
        var collectionIds = ResolveCollectionIds(query.WorkspaceId, query.CollectionId);

        foreach (var collectionId in collectionIds)
        {
            var path = _paths.GetRelationsJsonlPath(query.WorkspaceId, collectionId);
            relations.AddRange(await ReadRelationsCachedAsync(path, cancellationToken)
                .ConfigureAwait(false));
        }

        var take = query.Take > 0 ? query.Take : 50;
        var skip = query.Skip > 0 ? query.Skip : 0;

        return [.. relations
            .Where(relation => Matches(relation, query))
            .OrderByDescending(relation => relation.Weight)
            .ThenByDescending(relation => relation.Confidence)
            .ThenByDescending(relation => relation.CreatedAt)
            .Skip(skip)
            .Take(take)];
    }

    /// <summary>
    /// P0-fix: 获取进程级跨实例锁。按文件路径共享 SemaphoreSlim。
    /// SemaphoreSlim 不是线程亲和的，Release 可在 async await 后的任意线程调用。
    /// 支持取消，避免无限阻塞。
    /// </summary>
    private static async Task<ProcessLockLease> AcquireProcessLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var gate = s_processLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessLockLease(gate);
    }

    private IReadOnlyList<string> ResolveCollectionIds(string workspaceId, string? collectionId)
    {
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            return [collectionId];
        }

        var collectionsDirectory = _paths.GetCollectionsDirectory(workspaceId);
        if (!Directory.Exists(collectionsDirectory))
        {
            return [];
        }

        return [.. Directory.EnumerateDirectories(collectionsDirectory)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()];
    }

    private static bool Matches(ContextRelation relation, ContextRelationQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.CollectionId)
            && !string.Equals(relation.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.SourceId)
            && !string.Equals(relation.SourceId, query.SourceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.TargetId)
            && !string.Equals(relation.TargetId, query.TargetId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(query.ItemId)
            && !string.Equals(relation.SourceId, query.ItemId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(relation.TargetId, query.ItemId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(query.RelationType)
               || string.Equals(relation.RelationType, query.RelationType, StringComparison.OrdinalIgnoreCase);
    }
}
