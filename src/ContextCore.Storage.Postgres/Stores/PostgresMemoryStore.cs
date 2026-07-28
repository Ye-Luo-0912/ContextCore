using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 分层记忆存储。
/// 第一版聚焦 <see cref="IMemoryStore"/>：保存、按层/状态/标签查询，以及更新记忆状态。
/// Perf-2：SaveAsync 计算 SHA-256 + token 数并持久化到专用列，Provider 读取时直接复用、跳过在线重算。
/// </summary>
public sealed class PostgresMemoryStore : PostgresStoreBase, IMemoryStore, IMemoryStoreBatchLookup
{
    public PostgresMemoryStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? tokenizerModelName = null)
        : base(connectionFactory, serializer, migrationRunner)
    {
        TokenizerResolver = tokenizerResolver;
        TokenizerModelName = tokenizerModelName;
    }

    public async Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var normalized = CompositeContextNormalizer.Normalize(item);
        // Perf-2：摄取阶段计算 tokenization metadata，写入专用列与 Metadata（与 ContextItem 摄取逻辑一致）。
        var tokenization = ComputeTokenizationMetadata(normalized.Content);
        var metadataWithTokenization = WithTokenizationMetadata(normalized.Metadata, tokenization);
        var persisted = new ContextMemoryItem
        {
            Id = normalized.Id,
            WorkspaceId = normalized.WorkspaceId,
            CollectionId = normalized.CollectionId,
            Layer = normalized.Layer,
            Status = normalized.Status,
            Type = normalized.Type,
            Content = normalized.Content,
            ContentFormat = normalized.ContentFormat,
            Tags = normalized.Tags,
            SourceRefs = normalized.SourceRefs,
            RelationRefs = normalized.RelationRefs,
            Importance = normalized.Importance,
            Confidence = normalized.Confidence,
            Version = normalized.Version,
            Metadata = metadataWithTokenization,
            CreatedAt = normalized.CreatedAt,
            UpdatedAt = normalized.UpdatedAt
        };

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("memory_items")} (
    workspace_id, collection_id, id, layer, status, type, tags, source_refs, relation_refs,
    importance, confidence, version, created_at, updated_at, data,
    content_hash, content_length, tokenizer_id, tokenizer_version, token_count, counted_at)
VALUES (
    @workspace_id, @collection_id, @id, @layer, @status, @type, @tags, @source_refs, @relation_refs,
    @importance, @confidence, @version, @created_at, @updated_at, @data,
    @content_hash, @content_length, @tokenizer_id, @tokenizer_version, @token_count, @counted_at)
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
    data = EXCLUDED.data,
    content_hash = EXCLUDED.content_hash,
    content_length = EXCLUDED.content_length,
    tokenizer_id = EXCLUDED.tokenizer_id,
    tokenizer_version = EXCLUDED.tokenizer_version,
    token_count = EXCLUDED.token_count,
    counted_at = EXCLUDED.counted_at;
""";
        command.Parameters.AddWithValue("workspace_id", persisted.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", persisted.CollectionId);
        command.Parameters.AddWithValue("id", persisted.Id);
        command.Parameters.AddWithValue("layer", persisted.Layer.ToString());
        command.Parameters.AddWithValue("status", persisted.Status.ToString());
        command.Parameters.AddWithValue("type", persisted.Type);
        AddTextArray(command, "tags", persisted.Tags);
        AddTextArray(command, "source_refs", persisted.SourceRefs);
        AddTextArray(command, "relation_refs", persisted.RelationRefs);
        command.Parameters.AddWithValue("importance", persisted.Importance);
        command.Parameters.AddWithValue("confidence", persisted.Confidence);
        command.Parameters.AddWithValue("version", persisted.Version);
        command.Parameters.AddWithValue("created_at", persisted.CreatedAt);
        command.Parameters.AddWithValue("updated_at", persisted.UpdatedAt);
        AddJson(command, "data", persisted);
        AddTokenizationColumnParameters(command, tokenization);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextMemoryItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data, content_hash, content_length, tokenizer_id, tokenizer_version, token_count, counted_at
FROM {Table("memory_items")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND id = @id
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return ReadMemoryItemWithTokenization(reader);
    }

    /// <summary>
    /// P0-7.1: 批量查询记忆条目。使用 WHERE id = ANY(@ids) 单次 SQL 替代 N 次 GetAsync 并行，
    /// 命中主键 B-tree 索引 (workspace_id, collection_id, id)。
    /// 返回列表只包含命中的条目，顺序不保证；未命中条目静默丢弃。
    /// 语义与 FileMemoryStore.BatchGetAsync / InMemoryMemoryStore.BatchGetAsync 保持一致。
    /// </summary>
    public async Task<IReadOnlyList<ContextMemoryItem>> BatchGetAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return Array.Empty<ContextMemoryItem>();
        }

        // 过滤空白 id；Postgres 对 ANY() 中的重复值会自然去重，无需在客户端去重
        var normalizedIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        if (normalizedIds.Length == 0)
        {
            return Array.Empty<ContextMemoryItem>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText =
            "SELECT data, content_hash, content_length, tokenizer_id, tokenizer_version, token_count, counted_at " +
            $"FROM {Table("memory_items")} " +
            "WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND id = ANY(@ids)";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        AddTextArray(command, "ids", normalizedIds);

        var results = new List<ContextMemoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadMemoryItemWithTokenization(reader));
        }
        return results;
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
SELECT data, content_hash, content_length, tokenizer_id, tokenizer_version, token_count, counted_at
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
            results.Add(ReadMemoryItemWithTokenization(reader));
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

    /// <summary>
    /// Perf-2：从 reader 当前行读取 ContextMemoryItem，并把专用列的 tokenization metadata 合并到 Metadata 字典。
    /// 列顺序：data(0), content_hash(1), content_length(2), tokenizer_id(3), tokenizer_version(4), token_count(5), counted_at(6)。
    /// </summary>
    private ContextMemoryItem ReadMemoryItemWithTokenization(System.Data.Common.DbDataReader reader)
    {
        var json = reader.GetString(0);
        var item = Serializer.Deserialize<ContextMemoryItem>(json);
        var contentHash = reader.IsDBNull(1) ? null : reader.GetString(1);
        var contentLength = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
        var tokenizerId = reader.IsDBNull(3) ? null : reader.GetString(3);
        var tokenizerVersion = reader.IsDBNull(4) ? null : reader.GetString(4);
        var tokenCount = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
        var countedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6);

        var mergedMetadata = MergePersistedTokenizationColumns(
            item.Metadata, contentHash, contentLength, tokenizerId, tokenizerVersion, tokenCount, countedAt);
        return mergedMetadata == item.Metadata
            ? item
            : new ContextMemoryItem
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Layer = item.Layer,
                Status = item.Status,
                Type = item.Type,
                Content = item.Content,
                ContentFormat = item.ContentFormat,
                Tags = item.Tags,
                SourceRefs = item.SourceRefs,
                RelationRefs = item.RelationRefs,
                Importance = item.Importance,
                Confidence = item.Confidence,
                Version = item.Version,
                Metadata = mergedMetadata,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt
            };
    }

    /// <summary>Perf-2：把 tokenization metadata 列值绑定到 NpgsqlCommand 参数。</summary>
    private static void AddTokenizationColumnParameters(NpgsqlCommand command, TokenizationMetadata metadata)
    {
        command.Parameters.AddWithValue("content_hash", (object?)metadata.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("content_length", metadata.ContentLength);
        command.Parameters.AddWithValue("tokenizer_id", (object?)metadata.TokenizerId ?? DBNull.Value);
        command.Parameters.AddWithValue("tokenizer_version", (object?)metadata.TokenizerVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("token_count", metadata.TokenCount);
        command.Parameters.AddWithValue("counted_at", (object?)metadata.CountedAt ?? DBNull.Value);
    }
}