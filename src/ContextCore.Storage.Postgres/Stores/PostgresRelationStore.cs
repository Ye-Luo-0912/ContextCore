using ContextCore.Storage.Postgres;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 关系存储，使用结构化列加 jsonb 原文保存关系边。</summary>
public sealed class PostgresRelationStore : PostgresStoreBase, IRelationStore
{
    public PostgresRelationStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        var normalized = Normalize(relation);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("relations")} (
    workspace_id, collection_id, id, source_id, target_id, relation_type,
    weight, confidence, created_at, data)
VALUES (
    @workspace_id, @collection_id, @id, @source_id, @target_id, @relation_type,
    @weight, @confidence, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, id) DO UPDATE SET
    source_id = EXCLUDED.source_id,
    target_id = EXCLUDED.target_id,
    relation_type = EXCLUDED.relation_type,
    weight = EXCLUDED.weight,
    confidence = EXCLUDED.confidence,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("source_id", normalized.SourceId);
        command.Parameters.AddWithValue("target_id", normalized.TargetId);
        command.Parameters.AddWithValue("relation_type", normalized.RelationType);
        command.Parameters.AddWithValue("weight", normalized.Weight);
        command.Parameters.AddWithValue("confidence", normalized.Confidence);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveManyAsync(IEnumerable<ContextRelation> relations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);
        foreach (var relation in relations)
        {
            await SaveAsync(relation, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<ContextRelation>> QueryAsync(ContextRelationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var filters = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            filters.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }

        if (!string.IsNullOrWhiteSpace(query.SourceId))
        {
            filters.Add("source_id = @source_id");
            command.Parameters.AddWithValue("source_id", query.SourceId);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetId))
        {
            filters.Add("target_id = @target_id");
            command.Parameters.AddWithValue("target_id", query.TargetId);
        }

        if (!string.IsNullOrWhiteSpace(query.ItemId))
        {
            filters.Add("(source_id = @item_id OR target_id = @item_id)");
            command.Parameters.AddWithValue("item_id", query.ItemId);
        }

        if (!string.IsNullOrWhiteSpace(query.RelationType))
        {
            filters.Add("relation_type = @relation_type");
            command.Parameters.AddWithValue("relation_type", query.RelationType);
        }

        command.Parameters.AddWithValue("take", TakeOrDefault(query.Take));
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE {string.Join(" AND ", filters)}
ORDER BY weight DESC, confidence DESC, created_at DESC
LIMIT @take;
""";

        var results = new List<ContextRelation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Serializer.Deserialize<ContextRelation>(reader.GetString(0)));
        }

        return results;
    }

    public Task<IReadOnlyList<ContextRelation>> QueryForItemAsync(string workspaceId, string collectionId, string itemId, CancellationToken cancellationToken = default)
    {
        return QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, ItemId = itemId, Take = int.MaxValue }, cancellationToken);
    }

    public Task<IReadOnlyList<ContextRelation>> QueryBySourceAsync(string workspaceId, string collectionId, string sourceId, CancellationToken cancellationToken = default)
    {
        return QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, SourceId = sourceId, Take = int.MaxValue }, cancellationToken);
    }

    public Task<IReadOnlyList<ContextRelation>> QueryByTargetAsync(string workspaceId, string collectionId, string targetId, CancellationToken cancellationToken = default)
    {
        return QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, TargetId = targetId, Take = int.MaxValue }, cancellationToken);
    }

    public Task<IReadOnlyList<ContextRelation>> QueryByTypeAsync(string workspaceId, string collectionId, string relationType, CancellationToken cancellationToken = default)
    {
        return QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, RelationType = relationType, Take = int.MaxValue }, cancellationToken);
    }

    private static ContextRelation Normalize(ContextRelation relation)
    {
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
            SourceRefs = relation.SourceRefs.ToArray(),
            Metadata = new Dictionary<string, string>(relation.Metadata),
            CreatedAt = relation.CreatedAt == default ? DateTimeOffset.UtcNow : relation.CreatedAt
        };
    }
}