using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 <see cref="IRelationStore"/> 实现，关系数据持久化为 JSONL 文件。</summary>
/// <remarks>
/// RMW 完整流程在跨进程锁内执行——通过 <see cref="FileJsonLineStore.UpdateAsync{T}"/>
/// / <see cref="FileSystemWriter.UpdateLinesAsync"/> 包装读+改+写，FileLockProvider 在路径上加
/// 进程内 SemaphoreSlim + 跨进程 FileShare.None 哨兵文件，两进程不会各自读旧数据后互相覆盖。
/// <see cref="SemaphoreSlim"/> _gate 仅用于进程内读路径与写路径的串行化（保证 cache 一致性），
/// 跨进程 RMW 互斥完全交给 FileLockProvider。
/// metadata cache 带 mtime 双重校验。
/// </remarks>
public sealed class FileRelationStore : IRelationStore, IRelationStreamStore, IRelationHydrationStore
{
    private const int MaxCacheEntries = 256;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    // relation adjacency cache, keyed by relations.jsonl path.
    // Invalidation: file mtime mismatch → cache miss → re-read.
    // -fix: mtime recheck after read prevents caching stale content.
    private readonly ConcurrentDictionary<string, RelationCacheEntry> _relationCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record RelationCacheEntry(
        DateTime LastWriteUtc,
        IReadOnlyList<ContextRelation> Relations);

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

            // -fix: 读后复核 mtime；持有写锁期间不会有并发写，但仍防御性校验
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
    /// 删除单条边。P1-1: 走与 BatchUpsertAsync 相同的跨进程锁 RMW，避免丢失更新。
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
            // TryUpdateAsync 在 FileLockProvider 跨进程锁内完成读+改+写；
            // 未匹配到 relationId 时返回 null 跳过写入，文件不存在或无变更时不创建空文件。
            var deleted = await _jsonLines.TryUpdateAsync<ContextRelation>(
                path,
                existing =>
                {
                    var retained = existing
                        .Where(relation => !string.Equals(relation.Id, relationId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    return retained.Length == existing.Count ? null : retained;
                },
                cancellationToken).ConfigureAwait(false);

            if (deleted)
            {
                InvalidateRelationCache(path);
            }
            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 批量 upsert：按 (workspaceId, collectionId) 分组，每组在跨进程锁内完成读改写并原子替换文件。
    /// 走 <see cref="FileJsonLineStore.UpdateAsync{T}"/>，FileLockProvider 保证跨进程 RMW 原子性。
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
                var incoming = group.ToArray();
                var incomingIds = new HashSet<string>(
                    incoming.Select(r => r.Id),
                    StringComparer.OrdinalIgnoreCase);

                await _jsonLines.UpdateAsync<ContextRelation>(
                    path,
                    existing => existing
                        .Where(r => !incomingIds.Contains(r.Id))
                        .Concat(incoming)
                        .ToArray(),
                    cancellationToken).ConfigureAwait(false);

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
        // 多类型过滤优先于单类型
        var allowedTypes = query.AllowedRelationTypes.Count > 0
            ? new HashSet<string>(query.AllowedRelationTypes, StringComparer.OrdinalIgnoreCase)
            : null;

        // -fix: ReadRelationsCachedAsync 在慢路径加锁，快路径走缓存
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

        // 先排序再 Take(maxScan)，避免文件后部高权重关系永远进不了结果。
        // 文件已被完整读入内存，提前 Take 并不减少磁盘 I/O，反而丢失正确性。
        return [.. filtered
            .OrderByDescending(relation => relation.Weight)
            .ThenByDescending(relation => relation.Confidence)
            .ThenByDescending(relation => relation.CreatedAt)
            .Take(maxScan)
            .Skip(effectiveSkip)
            .Take(effectiveTake)];
    }

    /// <summary>
    /// 批量邻居查询。单次读文件 + 单次内存扫描，按种子 ID 分桶；
    /// per-seed 排序 + MaxScan + Skip + Take。
    /// </summary>
    public async Task<IReadOnlyList<RelationNeighborBatchResult>> QueryNeighborsBatchAsync(
        RelationNeighborBatchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // 去重种子 ID（保留原序）
        var seedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seeds = new List<string>(query.ItemIds.Count);
        foreach (var id in query.ItemIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && seedSet.Add(id))
            {
                seeds.Add(id);
            }
        }
        if (seeds.Count == 0)
        {
            return Array.Empty<RelationNeighborBatchResult>();
        }

        // 全局硬上限：种子数超出 GraphQueryLimits.MaxSeeds 直接截断（保留原序），与 Postgres 语义一致。
        // 被截断的种子同时从 seedSet 移除：其关系不再进入任何桶（对齐 Postgres 只处理前 MaxSeeds 个种子）。
        if (seeds.Count > GraphQueryLimits.MaxSeeds)
        {
            for (var i = GraphQueryLimits.MaxSeeds; i < seeds.Count; i++)
            {
                seedSet.Remove(seeds[i]);
            }
            seeds.RemoveRange(GraphQueryLimits.MaxSeeds, seeds.Count - GraphQueryLimits.MaxSeeds);
        }

        var effectiveTake = query.Take > 0 ? query.Take : 100;
        var effectiveSkip = query.Skip > 0 ? query.Skip : 0;
        var maxScan = query.MaxScan > 0 ? query.MaxScan : 1000;
        // 全局边数上限 = 查询声明的 GlobalEdgeLimit（clamp 到 [1, MaxTotalEdges]），与 Postgres 语义一致。
        var globalEdgeLimit = query.GlobalEdgeLimit > 0
            ? Math.Min(query.GlobalEdgeLimit, GraphQueryLimits.MaxTotalEdges)
            : GraphQueryLimits.MaxTotalEdges;
        var excludedLifecycles = query.ExcludedLifecycles.Count > 0
            ? new HashSet<string>(query.ExcludedLifecycles, StringComparer.OrdinalIgnoreCase)
            : null;
        var excludedReviewStatuses = query.ExcludedReviewStatuses.Count > 0
            ? new HashSet<string>(query.ExcludedReviewStatuses, StringComparer.OrdinalIgnoreCase)
            : null;
        var allowedTypes = query.AllowedRelationTypes.Count > 0
            ? new HashSet<string>(query.AllowedRelationTypes, StringComparer.OrdinalIgnoreCase)
            : null;

        var path = _paths.GetRelationsJsonlPath(query.WorkspaceId, query.CollectionId ?? string.Empty);
        var relations = await ReadRelationsCachedAsync(path, cancellationToken).ConfigureAwait(false);

        var buckets = new Dictionary<string, List<ContextRelation>>(seeds.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds)
        {
            buckets[seed] = new List<ContextRelation>();
        }

        foreach (var relation in relations)
        {
            if (!string.Equals(relation.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(query.CollectionId)
                && !string.Equals(relation.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (allowedTypes is not null)
            {
                if (!allowedTypes.Contains(relation.RelationType)) { continue; }
            }
            else if (!string.IsNullOrWhiteSpace(query.RelationType)
                && !string.Equals(relation.RelationType, query.RelationType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (query.MinConfidence > 0 && relation.Confidence < query.MinConfidence) { continue; }
            if (excludedLifecycles is not null && excludedLifecycles.Contains(relation.Lifecycle ?? string.Empty)) { continue; }
            if (excludedReviewStatuses is not null && excludedReviewStatuses.Contains(relation.ReviewStatus ?? string.Empty)) { continue; }

            // 方向匹配 + 桶分配（与 InMemory 实现一致）
            var sourceIsSeed = seedSet.Contains(relation.SourceId);
            var targetIsSeed = seedSet.Contains(relation.TargetId);
            switch (query.Direction)
            {
                case RelationDirection.Outgoing:
                    if (sourceIsSeed) { buckets[relation.SourceId].Add(relation); }
                    break;
                case RelationDirection.Incoming:
                    if (targetIsSeed) { buckets[relation.TargetId].Add(relation); }
                    break;
                default:
                    if (sourceIsSeed) { buckets[relation.SourceId].Add(relation); }
                    if (targetIsSeed
                        && !string.Equals(relation.SourceId, relation.TargetId, StringComparison.OrdinalIgnoreCase))
                    {
                        buckets[relation.TargetId].Add(relation);
                    }
                    break;
            }
        }

        var results = new List<RelationNeighborBatchResult>(seeds.Count);
        var totalRead = 0;
        foreach (var seed in seeds)
        {
            // 先排序并物化，便于检测 MaxScan 截断。
            var sorted = buckets[seed]
                .OrderByDescending(r => r.Weight)
                .ThenByDescending(r => r.Confidence)
                .ThenByDescending(r => r.CreatedAt)
                .ToArray();
            var truncated = sorted.Length > maxScan;
            if (totalRead >= globalEdgeLimit)
            {
                // 全局预算已耗尽，后续种子不再返回结果（对齐 Postgres 外层 LIMIT @global_limit 语义）。
                break;
            }

            // per-seed 扫描窗口（对齐 Postgres LATERAL 顺序：先 per-seed 扫描上限 → 全局上限 → Skip/Take 分页）。
            var scanWindow = sorted.Length > maxScan ? sorted.Take(maxScan).ToArray() : sorted;
            var remaining = globalEdgeLimit - totalRead;
            ContextRelation[] window;
            if (scanWindow.Length >= remaining)
            {
                // 预算在该种子窗口内耗尽（含恰好用尽）：只发剩余行数，并保守标记 Truncated
                // （Postgres 无法区分"桶恰好读完"与"还有更多"，此处复刻同一保守语义保证跨存储一致）。
                window = scanWindow.Take(remaining).ToArray();
                truncated = true;
            }
            else
            {
                window = scanWindow;
            }
            totalRead += window.Length;

            var paged = window
                .Skip(effectiveSkip)
                .Take(effectiveTake)
                .ToArray();
            if (paged.Length > 0)
            {
                results.Add(new RelationNeighborBatchResult
                {
                    ItemId = seed,
                    Relations = paged,
                    Truncated = truncated
                });
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // -fix: ReadRelationsCachedAsync 在慢路径加锁，快路径走缓存
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

    /// <summary>
    /// 流式枚举关系，逐行读取 JSONL 而非全量载入 List。
    /// 跨集合枚举时不保持全局排序——每个集合内部按文件顺序产出。
    /// 调用方（如 ValidateStreamAsync）按需在消费端累积排序状态。
    /// 禁止无界扫描——累计产出达到 <see cref="GraphQueryLimits.MaxTotalEdges"/> 即停止，
    /// 防止病态全表把整张图拉入内存。
    /// </summary>
    public async IAsyncEnumerable<ContextRelation> StreamRelationsAsync(
        string workspaceId,
        string? collectionId = null,
        string? itemId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        cancellationToken.ThrowIfCancellationRequested();

        // 全局上限——未提供 LIMIT 时使用 GraphQueryLimits.MaxTotalEdges 默认上限。
        var yielded = 0;
        var collectionIds = ResolveCollectionIds(workspaceId, collectionId);
        foreach (var cid in collectionIds)
        {
            if (yielded >= GraphQueryLimits.MaxTotalEdges)
            {
                break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var path = _paths.GetRelationsJsonlPath(workspaceId, cid);
            await foreach (var relation in _jsonLines.StreamAsync<ContextRelation>(path, cancellationToken).ConfigureAwait(false))
            {
                if (yielded >= GraphQueryLimits.MaxTotalEdges)
                {
                    break;
                }
                // item 过滤在产出前应用，避免无效 yield。
                if (!string.IsNullOrWhiteSpace(itemId)
                    && !string.Equals(relation.SourceId, itemId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(relation.TargetId, itemId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                yielded++;
                yield return CompositeContextNormalizer.Clone(relation);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// 按关系 ID 批量 hydrate 完整 Relation（含 Metadata/SourceRefs 等）。
    /// FileSystem 实现逐集合读取 JSONL 并按 ID 集合过滤，命中即收集并克隆返回。
    /// </summary>
    public async Task<IReadOnlyList<ContextRelation>> HydrateRelationsAsync(
        string workspaceId,
        string? collectionId,
        IReadOnlyList<string> relationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(relationIds);
        if (relationIds.Count == 0)
        {
            return Array.Empty<ContextRelation>();
        }

        var idSet = new HashSet<string>(relationIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<ContextRelation>(relationIds.Count);
        var collectionIds = ResolveCollectionIds(workspaceId, collectionId);
        foreach (var cid in collectionIds)
        {
            if (idSet.Count == 0)
            {
                break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var path = _paths.GetRelationsJsonlPath(workspaceId, cid);
            await foreach (var relation in _jsonLines.StreamAsync<ContextRelation>(path, cancellationToken).ConfigureAwait(false))
            {
                if (idSet.Remove(relation.Id))
                {
                    results.Add(CompositeContextNormalizer.Clone(relation));
                    if (idSet.Count == 0)
                    {
                        break;
                    }
                }
            }
        }

        return results;
    }
}
