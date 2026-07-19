using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 上下文条目与集合元数据存储。
/// 完整 DTO 保存在 jsonb 中，同时抽取常用筛选列以便查询和索引。
/// </summary>
public sealed class PostgresContextStore : PostgresStoreBase, IContextStore, IContextCollectionStore, IContextStoreBatchLookup, ITransactionalContextStore
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
        await ExecuteSaveAsync(command, normalized, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// P0-3：在指定事务作用域内保存条目。复用 scope 持有的连接与事务，不开启新连接。
    /// scope 必须是 <see cref="PostgresWriteTransactionScope"/>。
    /// </summary>
    public async Task SaveAsync(ContextItem item, IWriteTransactionScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope is not PostgresWriteTransactionScope pgScope)
        {
            throw new InvalidOperationException(
                "PostgresContextStore 仅支持 PostgresWriteTransactionScope；请通过 PostgresWriteTransactionScopeFactory 创建事务作用域。");
        }
        if (!scope.IsActive)
        {
            throw new InvalidOperationException("事务作用域已结束（Commit/Rollback），无法继续写入。");
        }

        var normalized = Normalize(item);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        using var command = pgScope.Connection.CreateCommand();
        if (pgScope.Transaction is not null)
        {
            command.Transaction = pgScope.Transaction;
        }
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        await ExecuteSaveAsync(command, normalized, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>共享的 INSERT/ON CONFLICT 逻辑，由无事务与事务重载复用。</summary>
    private async Task ExecuteSaveAsync(NpgsqlCommand command, ContextItem normalized, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// P0-7.1: 批量查询上下文条目。使用 WHERE id = ANY(@ids) 单次 SQL 替代 N 次 GetAsync 并行，
    /// 命中主键 B-tree 索引 (workspace_id, collection_id, id)。
    /// 返回列表只包含命中的条目，顺序不保证；未命中条目静默丢弃。
    /// 语义与 FileContextStore.BatchGetAsync / InMemoryContextStore.BatchGetAsync 保持一致。
    /// </summary>
    public async Task<IReadOnlyList<ContextItem>> BatchGetAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return Array.Empty<ContextItem>();
        }

        // 过滤空白 id；Postgres 对 ANY() 中的重复值会自然去重，无需在客户端去重
        var normalizedIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        if (normalizedIds.Length == 0)
        {
            return Array.Empty<ContextItem>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText =
            $"SELECT data FROM {Table("context_items")} " +
            "WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND id = ANY(@ids)";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        AddTextArray(command, "ids", normalizedIds);

        return await ExecuteReaderJsonAsync<ContextItem>(command, cancellationToken).ConfigureAwait(false);
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