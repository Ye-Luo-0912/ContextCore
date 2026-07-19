using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的 <see cref="IRelationStore"/> 实现，适用于测试和短生命周期场景。</summary>
public sealed class InMemoryRelationStore : IRelationStore
{
    private readonly ConcurrentDictionary<string, ContextRelation> _relations = new();

    /// <summary>GRAPH-11：SaveAsync 委托 BatchUpsertAsync，保留为单条便利方法。</summary>
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

    /// <summary>GRAPH-10：统一邻居查询，在内存中过滤。</summary>
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
        // P3-02：多类型过滤优先于单类型
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

        // P0-2：先排序再 Take(maxScan)，避免高权重关系因未排序集合被截断而漏掉。
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

    private static string Key(string workspaceId, string collectionId, string id)
    {
        return $"{workspaceId}\u001f{collectionId}\u001f{id}";
    }
}
