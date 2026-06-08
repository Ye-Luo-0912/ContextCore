using ContextCore.Storage.Postgres;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 分层记忆存储。
/// 第一版聚焦 <see cref="IMemoryStore"/>：保存、按层/状态/标签查询，以及更新记忆状态。
/// </summary>
public sealed class PostgresMemoryStore : PostgresStoreBase, IMemoryStore
{
    public PostgresMemoryStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var normalized = Normalize(item);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("memory_items")} (
    workspace_id, collection_id, id, layer, status, type, tags, source_refs, relation_refs,
    importance, confidence, version, created_at, updated_at, data)
VALUES (
    @workspace_id, @collection_id, @id, @layer, @status, @type, @tags, @source_refs, @relation_refs,
    @importance, @confidence, @version, @created_at, @updated_at, @data)
ON CONFLICT (workspace_id, collection_id, id) DO UPDATE SET
    layer = EXCLUDED.layer,
    status = EXCLUDED.status,
    type = EXCLUDED.type,
    tags = EXCLUDED.tags,
    source_refs = EXCLUDED.source_refs,
    relation_refs = EXCLUDED.relation_refs,
    importance = EXCLUDED.importance,
    confidence = EXCLUDED.confidence,
    version = EXCLUDED.version,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("layer", normalized.Layer.ToString());
        command.Parameters.AddWithValue("status", normalized.Status.ToString());
        command.Parameters.AddWithValue("type", normalized.Type);
        AddTextArray(command, "tags", normalized.Tags);
        AddTextArray(command, "source_refs", normalized.SourceRefs);
        AddTextArray(command, "relation_refs", normalized.RelationRefs);
        command.Parameters.AddWithValue("importance", normalized.Importance);
        command.Parameters.AddWithValue("confidence", normalized.Confidence);
        command.Parameters.AddWithValue("version", normalized.Version);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextMemoryItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT data FROM {Table("memory_items")} WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND id = @id";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("id", id);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? null : Serializer.Deserialize<ContextMemoryItem>(json);
    }

    public async Task<IReadOnlyList<ContextMemoryItem>> QueryAsync(ContextMemoryQuery query, CancellationToken cancellationToken = default)
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

        if (query.Layer is not null)
        {
            filters.Add("layer = @layer");
            command.Parameters.AddWithValue("layer", query.Layer.Value.ToString());
        }

        if (query.Status is not null)
        {
            filters.Add("status = @status");
            command.Parameters.AddWithValue("status", query.Status.Value.ToString());
        }

        if (query.Tags.Count > 0)
        {
            filters.Add("tags @> @tags");
            AddTextArray(command, "tags", query.Tags);
        }

        if (query.Types.Count > 0)
        {
            filters.Add("type = ANY(@types)");
            AddTextArray(command, "types", query.Types);
        }

        if (query.SourceRefs.Count > 0)
        {
            filters.Add("(source_refs && @source_refs OR id = ANY(@source_refs))");
            AddTextArray(command, "source_refs", query.SourceRefs);
        }

        command.Parameters.AddWithValue("skip", Math.Max(0, query.Skip));
        command.Parameters.AddWithValue("take", TakeOrDefault(query.Take));
        command.CommandText = $"""
SELECT data
FROM {Table("memory_items")}
WHERE {string.Join(" AND ", filters)}
ORDER BY importance DESC, updated_at DESC
OFFSET @skip
LIMIT @take;
""";

        var results = new List<ContextMemoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Serializer.Deserialize<ContextMemoryItem>(reader.GetString(0)));
        }

        return results;
    }

    public async Task UpdateStatusAsync(string workspaceId, string collectionId, string id, ContextMemoryStatus status, CancellationToken cancellationToken = default)
    {
        var item = await GetAsync(workspaceId, collectionId, id, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        await SaveAsync(new ContextMemoryItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = item.Layer,
            Status = status,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = item.Version + 1,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
    }

    private static ContextMemoryItem Normalize(ContextMemoryItem item)
    {
        var now = DateTimeOffset.UtcNow;
        return new ContextMemoryItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = item.Layer,
            Status = item.Status,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = item.Version <= 0 ? 1 : item.Version,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }
}