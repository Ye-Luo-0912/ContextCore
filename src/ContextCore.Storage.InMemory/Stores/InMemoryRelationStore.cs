using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的 <see cref="IRelationStore"/> 实现，适用于测试和短生命周期场景。</summary>
public sealed class InMemoryRelationStore : IRelationStore
{
    private readonly ConcurrentDictionary<string, ContextRelation> _relations = new();

    public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = relation.Id.Length == 0
            ? Clone(relation, Guid.NewGuid().ToString("N"))
            : Clone(relation);
        _relations[Key(normalized.WorkspaceId, normalized.CollectionId, normalized.Id)] = normalized;

        return Task.CompletedTask;
    }

    public Task SaveManyAsync(
        IEnumerable<ContextRelation> relations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var relation in relations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = relation.Id.Length == 0
                ? Clone(relation, Guid.NewGuid().ToString("N"))
                : Clone(relation);
            _relations[Key(normalized.WorkspaceId, normalized.CollectionId, normalized.Id)] = normalized;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var take = query.Take > 0 ? query.Take : 50;
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
            .Take(take)
            .Select(item => Clone(item))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextRelation>>(results);
    }

    public Task<IReadOnlyList<ContextRelation>> QueryForItemAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ItemId = itemId,
            Take = int.MaxValue
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ContextRelation>> QueryBySourceAsync(
        string workspaceId,
        string collectionId,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SourceId = sourceId,
            Take = int.MaxValue
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ContextRelation>> QueryByTargetAsync(
        string workspaceId,
        string collectionId,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            TargetId = targetId,
            Take = int.MaxValue
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ContextRelation>> QueryByTypeAsync(
        string workspaceId,
        string collectionId,
        string relationType,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            RelationType = relationType,
            Take = int.MaxValue
        }, cancellationToken);
    }

    private static ContextRelation Clone(ContextRelation relation, string? id = null)
    {
        return new ContextRelation
        {
            Id = id ?? relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            RelationType = relation.RelationType,
            Weight = relation.Weight,
            Confidence = relation.Confidence,
            SourceRefs = relation.SourceRefs.ToArray(),
            Metadata = new Dictionary<string, string>(relation.Metadata),
            CreatedAt = relation.CreatedAt == default ? DateTimeOffset.UtcNow : relation.CreatedAt
        };
    }

    private static string Key(string workspaceId, string collectionId, string id)
    {
        return $"{workspaceId}\u001f{collectionId}\u001f{id}";
    }
}
