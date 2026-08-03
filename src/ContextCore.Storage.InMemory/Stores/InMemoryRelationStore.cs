using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的 <see cref="IRelationStore"/> 实现，适用于测试和短生命周期场景。</summary>
public sealed class InMemoryRelationStore : IRelationStore, IRelationStreamStore, IRelationHydrationStore
{
    private readonly ConcurrentDictionary<string, ContextRelation> _relations = new();

    /// <summary>SaveAsync 委托 BatchUpsertAsync，保留为单条便利方法。</summary>
    public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
        => BatchUpsertAsync([relation], cancellationToken);

    public Task BatchUpsertAsync(
        IEnumerable<ContextRelation> relations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var relation in relations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = CompositeContextNormalizer.Normalize(relation);
            _relations[Key(normalized.WorkspaceId, normalized.CollectionId, normalized.Id)] = normalized;
        }

        return Task.CompletedTask;
    }

    public Task<ContextRelation?> GetAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _relations.TryGetValue(Key(workspaceId, collectionId, relationId), out var relation)
                ? CompositeContextNormalizer.Clone(relation)
                : null);
    }

    public Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _relations.TryRemove(Key(workspaceId, collectionId, relationId), out _));
    }

    /// <summary>统一邻居查询，在内存中过滤。</summary>
    public Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(
        RelationNeighborQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

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

        IEnumerable<ContextRelation> filtered = _relations.Values
            .Where(item => string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            filtered = filtered.Where(item => string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase));
        }

        filtered = query.Direction switch
        {
            RelationDirection.Outgoing => filtered.Where(item => string.Equals(item.SourceId, query.ItemId, StringComparison.OrdinalIgnoreCase)),
            RelationDirection.Incoming => filtered.Where(item => string.Equals(item.TargetId, query.ItemId, StringComparison.OrdinalIgnoreCase)),
            _ => filtered.Where(item =>
                string.Equals(item.SourceId, query.ItemId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TargetId, query.ItemId, StringComparison.OrdinalIgnoreCase))
        };

        if (allowedTypes is not null)
        {
            filtered = filtered.Where(item => allowedTypes.Contains(item.RelationType));
        }
        else if (!string.IsNullOrWhiteSpace(query.RelationType))
        {
            filtered = filtered.Where(item => string.Equals(item.RelationType, query.RelationType, StringComparison.OrdinalIgnoreCase));
        }

        if (query.MinConfidence > 0)
        {
            filtered = filtered.Where(item => item.Confidence >= query.MinConfidence);
        }

        if (excludedLifecycles is not null)
        {
            filtered = filtered.Where(item => !excludedLifecycles.Contains(item.Lifecycle ?? string.Empty));
        }

        if (excludedReviewStatuses is not null)
        {
            filtered = filtered.Where(item => !excludedReviewStatuses.Contains(item.ReviewStatus ?? string.Empty));
        }

        // 先排序再 Take(maxScan)，避免高权重关系因未排序集合被截断而漏掉。
        // 与 FileRelationStore 一致；Postgres 端通过 SQL ORDER BY ... LIMIT @max_scan 实现。
        var results = filtered
            .OrderByDescending(item => item.Weight)
            .ThenByDescending(item => item.Confidence)
            .ThenByDescending(item => item.CreatedAt)
            .Take(maxScan)
            .Skip(effectiveSkip)
            .Take(effectiveTake)
            .Select(item => CompositeContextNormalizer.Clone(item))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextRelation>>(results);
    }

    public Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var take = query.Take > 0 ? query.Take : 50;
        var skip = query.Skip > 0 ? query.Skip : 0;
        var results = _relations.Values
            .Where(item => string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.CollectionId)
                || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.SourceId)
                || string.Equals(item.SourceId, query.SourceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.TargetId)
                || string.Equals(item.TargetId, query.TargetId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.ItemId)
                || string.Equals(item.SourceId, query.ItemId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TargetId, query.ItemId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.RelationType)
                || string.Equals(item.RelationType, query.RelationType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Weight)
            .ThenByDescending(item => item.Confidence)
            .ThenByDescending(item => item.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(item => CompositeContextNormalizer.Clone(item))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextRelation>>(results);
    }

    /// <summary>
    /// 批量邻居查询。单次扫描 _relations.Values，按种子 ID 分桶；
    /// per-seed 排序 + MaxScan + Skip + Take；全局预算按每种子最低配额两阶段分配，
    /// 结果按种子序返回且每个种子都带诊断（ScannedCount / CandidateCountBeforeGlobalLimit / SkippedByGlobalBudget）。
    /// </summary>
    public Task<IReadOnlyList<RelationNeighborBatchResult>> QueryNeighborsBatchAsync(
        RelationNeighborBatchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        // 去重种子 ID（保留原序以便结果稳定）
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
            return Task.FromResult<IReadOnlyList<RelationNeighborBatchResult>>(Array.Empty<RelationNeighborBatchResult>());
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

        // 每个种子一个独立的 bucket，Both 方向下一条边可同时入两桶。
        var buckets = new Dictionary<string, List<ContextRelation>>(seeds.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds)
        {
            buckets[seed] = new List<ContextRelation>();
        }

        foreach (var item in _relations.Values)
        {
            if (!string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(query.CollectionId)
                && !string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (allowedTypes is not null)
            {
                if (!allowedTypes.Contains(item.RelationType))
                {
                    continue;
                }
            }
            else if (!string.IsNullOrWhiteSpace(query.RelationType)
                && !string.Equals(item.RelationType, query.RelationType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (query.MinConfidence > 0 && item.Confidence < query.MinConfidence)
            {
                continue;
            }
            if (excludedLifecycles is not null && excludedLifecycles.Contains(item.Lifecycle ?? string.Empty))
            {
                continue;
            }
            if (excludedReviewStatuses is not null && excludedReviewStatuses.Contains(item.ReviewStatus ?? string.Empty))
            {
                continue;
            }

            // 方向匹配 + 桶分配
            // Both 方向：source 是种子入 source 桶；target 是种子入 target 桶；
            // self-loop（source==target 且是种子）只入一次（source 桶）。
            var sourceIsSeed = seedSet.Contains(item.SourceId);
            var targetIsSeed = seedSet.Contains(item.TargetId);
            switch (query.Direction)
            {
                case RelationDirection.Outgoing:
                    if (sourceIsSeed) { buckets[item.SourceId].Add(item); }
                    break;
                case RelationDirection.Incoming:
                    if (targetIsSeed) { buckets[item.TargetId].Add(item); }
                    break;
                default:
                    if (sourceIsSeed) { buckets[item.SourceId].Add(item); }
                    if (targetIsSeed
                        && !string.Equals(item.SourceId, item.TargetId, StringComparison.OrdinalIgnoreCase))
                    {
                        buckets[item.TargetId].Add(item);
                    }
                    break;
            }
        }

        // 每种子最低配额 = floor(GlobalEdgeLimit / 种子数)，至少 1。
        // 阶段 1 保证每个种子扫描到配额内的候选；阶段 2 把余额按种子序再分配（每种子至多补到 maxScan 窗口）。
        // 由于 seeds * floor ≤ global，正常情形下预算永不提前耗尽 → 后续种子不再被早期富种子饿死。
        var seedCount = seeds.Count;
        var perSeedFloor = Math.Max(1, globalEdgeLimit / seedCount);
        var phase1Cap = Math.Min(maxScan, perSeedFloor);

        // 排序窗口按需物化（仅对实际交付的种子排序；被跳过/无候选的种子不浪费排序）。
        var windows = new ContextRelation[seedCount][];
        ContextRelation[] WindowOf(int ordinal)
        {
            var window = windows[ordinal];
            if (window is null)
            {
                window = buckets[seeds[ordinal]]
                    .OrderByDescending(item => item.Weight)
                    .ThenByDescending(item => item.Confidence)
                    .ThenByDescending(item => item.CreatedAt)
                    .Take(maxScan)
                    .ToArray();
                windows[ordinal] = window;
            }
            return window;
        }

        var delivered = new int[seedCount];
        var totalRead = 0;
        var skippedStart = -1;

        // 阶段 1：每种子最低配额，按种子序发放（对齐 Postgres 阶段 1 LATERAL LIMIT 语义）。
        for (var ordinal = 0; ordinal < seedCount; ordinal++)
        {
            if (totalRead >= globalEdgeLimit)
            {
                skippedStart = ordinal;
                break;
            }
            var window = WindowOf(ordinal);
            var take = Math.Min(phase1Cap, window.Length);
            delivered[ordinal] = take;
            totalRead += take;
        }

        // 阶段 2：余额按种子序再分配（每种子至多补到 maxScan 窗口；对齐 Postgres OFFSET+LIMIT 语义）。
        if (totalRead < globalEdgeLimit)
        {
            var remaining = globalEdgeLimit - totalRead;
            for (var ordinal = 0; ordinal < seedCount && remaining > 0; ordinal++)
            {
                var window = WindowOf(ordinal);
                var extra = Math.Min(remaining, window.Length - delivered[ordinal]);
                if (extra > 0)
                {
                    delivered[ordinal] += extra;
                    remaining -= extra;
                }
            }
        }

        var results = new List<RelationNeighborBatchResult>(seedCount);
        for (var ordinal = 0; ordinal < seedCount; ordinal++)
        {
            var seed = seeds[ordinal];
            var bucketCount = buckets[seed].Count;
            var window = windows[ordinal];
            var deliveredCount = delivered[ordinal];

            ContextRelation[] paged;
            if (deliveredCount > 0 && window is not null)
            {
                paged = window
                    .Take(deliveredCount)
                    .Skip(effectiveSkip)
                    .Take(effectiveTake)
                    .Select(item => CompositeContextNormalizer.Clone(item))
                    .ToArray();
            }
            else
            {
                paged = Array.Empty<ContextRelation>();
            }

            // 跳过信号：全局预算在该种子被扫描前即已耗尽（仅 GlobalEdgeLimit < 种子数 时出现）。
            // 空桶种子（无候选）不算被跳过。
            var skipped = skippedStart >= 0 && ordinal >= skippedStart && bucketCount > 0;

            // 截断信号：候选数超过 per-seed 扫描窗口，或全局预算在交付完成前耗尽。
            var truncated = bucketCount > maxScan
                || deliveredCount < Math.Min(bucketCount, maxScan);

            results.Add(new RelationNeighborBatchResult
            {
                ItemId = seed,
                SeedOrdinal = ordinal,
                Relations = paged,
                Truncated = truncated,
                SkippedByGlobalBudget = skipped,
                ScannedCount = deliveredCount,
                CandidateCountBeforeGlobalLimit = Math.Min(bucketCount, maxScan)
            });
        }

        return Task.FromResult<IReadOnlyList<RelationNeighborBatchResult>>(results);
    }

    private static string Key(string workspaceId, string collectionId, string id)
    {
        return $"{workspaceId}\u001f{collectionId}\u001f{id}";
    }

    /// <summary>
    /// 流式枚举关系，避免一次性将全部关系载入 List。
    /// 排序与 QueryAsync 一致（weight/confidence/createdAt desc），但不应用调用方 Skip/Take。
    /// 禁止无界扫描——迭代上限 = <see cref="GraphQueryLimits.MaxTotalEdges"/>，
    /// 防止病态全表把整张图拉入内存。对真正大图请使用 Postgres provider 的流式实现。
    /// </summary>
    public async IAsyncEnumerable<ContextRelation> StreamRelationsAsync(
        string workspaceId,
        string? collectionId = null,
        string? itemId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        cancellationToken.ThrowIfCancellationRequested();

        // 排序需要全量物化后才能稳定；InMemory 场景数据量小，先排序再 yield。
        // 对真正大图请使用 Postgres provider 的流式实现（NpgsqlDataReader.ReadAsync）。
        var sorted = _relations.Values
            .Where(item => string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(collectionId)
                || string.Equals(item.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(itemId)
                || string.Equals(item.SourceId, itemId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TargetId, itemId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Weight)
            .ThenByDescending(item => item.Confidence)
            .ThenByDescending(item => item.CreatedAt)
            .Select(item => CompositeContextNormalizer.Clone(item))
            // 全局上限——未提供 LIMIT 时使用 GraphQueryLimits.MaxTotalEdges 默认上限。
            .Take(GraphQueryLimits.MaxTotalEdges)
            .ToArray();

        foreach (var relation in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return relation;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// 按关系 ID 批量 hydrate 完整 Relation（含 Metadata/SourceRefs 等）。
    /// InMemory 数据已在内存中，直接按 ID 查找并克隆返回。
    /// </summary>
    public Task<IReadOnlyList<ContextRelation>> HydrateRelationsAsync(
        string workspaceId,
        string? collectionId,
        IReadOnlyList<string> relationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(relationIds);
        if (relationIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ContextRelation>>(Array.Empty<ContextRelation>());
        }

        var idSet = new HashSet<string>(relationIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<ContextRelation>(relationIds.Count);
        foreach (var item in _relations.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(collectionId)
                && !string.Equals(item.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (idSet.Contains(item.Id))
            {
                results.Add(CompositeContextNormalizer.Clone(item));
            }
        }

        return Task.FromResult<IReadOnlyList<ContextRelation>>(results);
    }
}
