using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>基于文件系统的 <see cref="IRelationStore"/> 实现，关系数据持久化为 JSONL 文件。</summary>
public sealed class FileRelationStore : IRelationStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FilePathResolver _paths;
    private readonly FileJsonLineStore _jsonLines;

    public FileRelationStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileRelationStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relation);

        var normalized = Normalize(relation);
        var path = _paths.GetRelationsJsonlPath(normalized.WorkspaceId, normalized.CollectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(path, normalized, item => item.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveManyAsync(
        IEnumerable<ContextRelation> relations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);

        foreach (var relation in relations)
        {
            await SaveAsync(relation, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relations = new List<ContextRelation>();
            var collectionIds = ResolveCollectionIds(query.WorkspaceId, query.CollectionId);

            foreach (var collectionId in collectionIds)
            {
                var path = _paths.GetRelationsJsonlPath(query.WorkspaceId, collectionId);
                relations.AddRange(await _jsonLines.ReadAsync<ContextRelation>(path, cancellationToken)
                    .ConfigureAwait(false));
            }

            var take = query.Take > 0 ? query.Take : 50;

            return [.. relations
                .Where(relation => Matches(relation, query))
                .OrderByDescending(relation => relation.Weight)
                .ThenByDescending(relation => relation.Confidence)
                .ThenByDescending(relation => relation.CreatedAt)
                .Take(take)];
        }
        finally
        {
            _gate.Release();
        }
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
        return QueryAsync(
            workspaceId,
            collectionId,
            relation => string.Equals(relation.SourceId, sourceId, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

    public Task<IReadOnlyList<ContextRelation>> QueryByTargetAsync(
        string workspaceId,
        string collectionId,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            workspaceId,
            collectionId,
            relation => string.Equals(relation.TargetId, targetId, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

    public Task<IReadOnlyList<ContextRelation>> QueryByTypeAsync(
        string workspaceId,
        string collectionId,
        string relationType,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            workspaceId,
            collectionId,
            relation => string.Equals(relation.RelationType, relationType, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

    private async Task<IReadOnlyList<ContextRelation>> QueryAsync(
        string workspaceId,
        string collectionId,
        Func<ContextRelation, bool> predicate,
        CancellationToken cancellationToken)
    {
        var path = _paths.GetRelationsJsonlPath(workspaceId, collectionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var relations = await _jsonLines.ReadAsync<ContextRelation>(path, cancellationToken)
                .ConfigureAwait(false);

            return [.. relations
                .Where(predicate)
                .OrderByDescending(relation => relation.Weight)
                .ThenByDescending(relation => relation.Confidence)
                .ThenByDescending(relation => relation.CreatedAt)];
        }
        finally
        {
            _gate.Release();
        }
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

        if (!string.IsNullOrWhiteSpace(query.RelationType)
            && !string.Equals(relation.RelationType, query.RelationType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static ContextRelation Normalize(ContextRelation relation)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextRelation
        {
            Id = string.IsNullOrWhiteSpace(relation.Id) ? Guid.NewGuid().ToString("N") : relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            RelationType = relation.RelationType,
            Weight = relation.Weight,
            Confidence = relation.Confidence,
            SourceRefs = [.. relation.SourceRefs],
            Metadata = new Dictionary<string, string>(relation.Metadata),
            CreatedAt = relation.CreatedAt == default ? now : relation.CreatedAt
        };
    }
}
