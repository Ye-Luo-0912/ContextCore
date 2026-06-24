using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 上下文条目与集合元数据存储。
/// 完整 DTO 保存在 jsonb 中，同时抽取常用筛选列以便查询和索引。
/// </summary>
public sealed class PostgresContextStore : PostgresStoreBase, IContextStore, IContextCollectionStore
{
    public PostgresContextStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var normalized = Normalize(item);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("context_items")} (
    workspace_id, collection_id, id, type, title, tags, refs, source_refs,
    importance, version, created_at, updated_at, data)
VALUES (
    @workspace_id, @collection_id, @id, @type, @title, @tags, @refs, @source_refs,
    @importance, @version, @created_at, @updated_at, @data)
ON CONFLICT (workspace_id, collection_id, id) DO UPDATE SET
    type = EXCLUDED.type,
    title = EXCLUDED.title,
    tags = EXCLUDED.tags,
    refs = EXCLUDED.refs,
    source_refs = EXCLUDED.source_refs,
    importance = EXCLUDED.importance,
    version = EXCLUDED.version,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("type", normalized.Type);
        command.Parameters.AddWithValue("title", (object?)normalized.Title ?? DBNull.Value);
        AddTextArray(command, "tags", normalized.Tags);
        AddTextArray(command, "refs", normalized.Refs);
        AddTextArray(command, "source_refs", normalized.SourceRefs);
        command.Parameters.AddWithValue("importance", normalized.Importance);
        command.Parameters.AddWithValue("version", normalized.Version);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT data FROM {Table("context_items")} WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND id = @id";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("id", id);

        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? null : Serializer.Deserialize<ContextItem>(json);
    }

    public async Task<IReadOnlyList<ContextItem>> QueryAsync(ContextQuery query, CancellationToken cancellationToken = default)
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

        if (!string.IsNullOrWhiteSpace(query.QueryText))
        {
            filters.Add("((data->>'Content') ILIKE @query_text OR (data->>'Title') ILIKE @query_text OR id ILIKE @query_text)");
            command.Parameters.AddWithValue("query_text", $"%{query.QueryText}%");
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

        if (query.ExcludedTypes.Count > 0)
        {
            filters.Add("NOT (type = ANY(@excluded_types))");
            AddTextArray(command, "excluded_types", query.ExcludedTypes);
        }

        if (query.ExcludedIds.Count > 0)
        {
            filters.Add("NOT (id = ANY(@excluded_ids))");
            AddTextArray(command, "excluded_ids", query.ExcludedIds);
        }

        if (query.Refs.Count > 0)
        {
            filters.Add("(refs && @refs OR source_refs && @refs OR id = ANY(@refs))");
            AddTextArray(command, "refs", query.Refs);
        }

        command.Parameters.AddWithValue("skip", Math.Max(0, query.Skip));
        command.Parameters.AddWithValue("take", TakeOrDefault(query.Take));
        command.CommandText = $"""
SELECT data
FROM {Table("context_items")}
WHERE {string.Join(" AND ", filters)}
ORDER BY importance DESC, updated_at DESC
OFFSET @skip
LIMIT @take;
""";

        var results = new List<ContextItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = Serializer.Deserialize<ContextItem>(reader.GetString(0));
            results.Add(query.IncludeContent ? item : WithoutContent(item));
        }

        return results;
    }

    public async Task DeleteAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"DELETE FROM {Table("context_items")} WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND id = @id";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveCollectionAsync(ContextCollection collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var normalized = new ContextCollection
        {
            Id = collection.Id,
            WorkspaceId = collection.WorkspaceId,
            Name = collection.Name,
            Description = collection.Description,
            Metadata = new Dictionary<string, string>(collection.Metadata),
            CreatedAt = collection.CreatedAt == default ? DateTimeOffset.UtcNow : collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt == default ? DateTimeOffset.UtcNow : collection.UpdatedAt
        };

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("collections")} (workspace_id, id, name, updated_at, data)
VALUES (@workspace_id, @id, @name, @updated_at, @data)
ON CONFLICT (workspace_id, id) DO UPDATE SET
    name = EXCLUDED.name,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("name", normalized.Name);
        command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCollection?> GetCollectionAsync(string workspaceId, string collectionId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT data FROM {Table("collections")} WHERE workspace_id = @workspace_id AND id = @id";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("id", collectionId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? null : Serializer.Deserialize<ContextCollection>(json);
    }

    private static ContextItem Normalize(ContextItem item)
    {
        var now = DateTimeOffset.UtcNow;
        return new ContextItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Title = item.Title,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = [.. item.Tags],
            Refs = [.. item.Refs],
            SourceRefs = [.. item.SourceRefs],
            Metadata = new Dictionary<string, string>(item.Metadata),
            Importance = item.Importance,
            Version = item.Version <= 0 ? 1 : item.Version,
            Checksum = item.Checksum,
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    private static ContextItem WithoutContent(ContextItem item)
    {
        return new ContextItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Title = item.Title,
            Content = string.Empty,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            Refs = item.Refs.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            Importance = item.Importance,
            Version = item.Version,
            Checksum = item.Checksum,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }
}