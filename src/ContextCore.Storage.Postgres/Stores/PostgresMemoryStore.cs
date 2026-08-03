using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 分层记忆存储。
/// 第一版聚焦 <see cref="IMemoryStore"/>：保存、按层/状态/标签查询，以及更新记忆状态。
/// SaveAsync 计算 SHA-256 + token 数并持久化到专用列，Provider 读取时直接复用、跳过在线重算。
/// </summary>
public sealed class PostgresMemoryStore : PostgresStoreBase, IMemoryStore, IMemoryStoreBatchLookup, IMemoryStoreMetadataLookup
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
        // 摄取阶段计算 tokenization metadata，写入专用列与 Metadata（与 ContextItem 摄取逻辑一致）。
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
    /// 批量查询记忆条目。使用 WHERE id = ANY(@ids) 单次 SQL 替代 N 次 GetAsync 并行，
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

    /// <summary>
    /// 按 ID 批量获取记忆条目元数据（不读取/反序列化完整 jsonb 正文）。
    /// SELECT 除 data 外的全列，读取走 <see cref="ReadMemoryItemMetadataRow"/>——Content 恒为空字符串，
    /// Metadata 合并摄取阶段持久化的 content_hash / content_length / token_count 等专用列，
    /// Layer/Status/Type/SourceRefs 等字段齐全（ApplyExcludedStatusesFilter 依赖 Status）。
    /// 语义与 <see cref="BatchGetAsync"/> 一致：只返回命中的条目，顺序不保证，未命中静默丢弃。
    /// </summary>
    public async Task<IReadOnlyList<ContextMemoryItem>> BatchGetMetadataAsync(
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
            "SELECT workspace_id, collection_id, id, layer, status, type, tags, source_refs, relation_refs, " +
            "importance, confidence, version, created_at, updated_at, " +
            "content_hash, content_length, tokenizer_id, tokenizer_version, token_count, counted_at, " +
            "data->'Metadata' AS metadata " +
            $"FROM {Table("memory_items")} " +
            "WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND id = ANY(@ids)";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        AddTextArray(command, "ids", normalizedIds);

        var results = new List<ContextMemoryItem>(normalizedIds.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadMemoryItemMetadataRow(reader));
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

        // IncludeContent=false 时只投影元数据列，避免读取/反序列化完整 jsonb 正文。
        // 节省 PostgreSQL 网络传输 + JSON 解析 + 大字符串分配；需要正文时由调用方（ISelectedCandidateHydrator）二次读取。
        if (!query.IncludeContent)
        {
            command.CommandText = $"""
SELECT workspace_id, collection_id, id, layer, status, type, tags, source_refs, relation_refs,
       importance, confidence, version, created_at, updated_at,
       content_hash, content_length, tokenizer_id, tokenizer_version, token_count, counted_at,
       data->'Metadata' AS metadata
FROM {Table("memory_items")}
WHERE {string.Join(" AND ", filters)}
ORDER BY importance DESC, updated_at DESC
OFFSET @skip
LIMIT @take;
""";

            var metadataResults = new List<ContextMemoryItem>();
            await using var metadataReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await metadataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                metadataResults.Add(ReadMemoryItemMetadataRow(metadataReader));
            }
            return metadataResults;
        }

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
    /// 从 reader 当前行读取 ContextMemoryItem，并把专用列的 tokenization metadata 合并到 Metadata 字典。
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

    /// <summary>把 tokenization metadata 列值绑定到 NpgsqlCommand 参数。</summary>
    private static void AddTokenizationColumnParameters(NpgsqlCommand command, TokenizationMetadata metadata)
    {
        command.Parameters.AddWithValue("content_hash", (object?)metadata.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("content_length", metadata.ContentLength);
        command.Parameters.AddWithValue("tokenizer_id", (object?)metadata.TokenizerId ?? DBNull.Value);
        command.Parameters.AddWithValue("tokenizer_version", (object?)metadata.TokenizerVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("token_count", metadata.TokenCount);
        command.Parameters.AddWithValue("counted_at", (object?)metadata.CountedAt ?? DBNull.Value);
    }

    /// <summary>
    /// 从 reader 当前行读取 ContextMemoryItem 元数据（不反序列化 jsonb 正文）。
    /// Content 恒为空字符串（Selected-only Hydration 契约），Layer/Status/Type/Tags/SourceRefs/
    /// RelationRefs/Importance/Confidence/Version/CreatedAt/UpdatedAt 字段齐全，
    /// 并把专用列的 tokenization metadata 合并到 Metadata 字典（Provider 读取后跳过在线重算）。
    /// 列顺序与 BatchGetMetadataAsync / QueryAsync(IncludeContent=false) 的 SELECT 子句一一对应：
    /// workspace_id(0), collection_id(1), id(2), layer(3), status(4), type(5), tags(6),
    /// source_refs(7), relation_refs(8), importance(9), confidence(10), version(11),
    /// created_at(12), updated_at(13), content_hash(14), content_length(15), tokenizer_id(16),
    /// tokenizer_version(17), token_count(18), counted_at(19)。
    /// </summary>
    private ContextMemoryItem ReadMemoryItemMetadataRow(System.Data.Common.DbDataReader reader)
    {
        var workspaceId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var collectionId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var id = reader.GetString(2);
        var layer = reader.IsDBNull(3) ? ContextMemoryLayer.Working : ParseEnum(reader.GetString(3), ContextMemoryLayer.Working);
        var status = reader.IsDBNull(4) ? ContextMemoryStatus.Active : ParseEnum(reader.GetString(4), ContextMemoryStatus.Active);
        var type = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
        var tags = reader.IsDBNull(6) ? Array.Empty<string>() : reader.GetFieldValue<string[]>(6);
        var sourceRefs = reader.IsDBNull(7) ? Array.Empty<string>() : reader.GetFieldValue<string[]>(7);
        var relationRefs = reader.IsDBNull(8) ? Array.Empty<string>() : reader.GetFieldValue<string[]>(8);
        var importance = reader.IsDBNull(9) ? 0.0 : reader.GetDouble(9);
        var confidence = reader.IsDBNull(10) ? 0.0 : reader.GetDouble(10);
        var version = reader.IsDBNull(11) ? 0L : reader.GetInt64(11);
        var createdAt = reader.IsDBNull(12) ? DateTimeOffset.MinValue : reader.GetFieldValue<DateTimeOffset>(12);
        var updatedAt = reader.IsDBNull(13) ? DateTimeOffset.MinValue : reader.GetFieldValue<DateTimeOffset>(13);
        var contentHash = reader.IsDBNull(14) ? null : reader.GetString(14);
        var contentLength = reader.IsDBNull(15) ? (int?)null : reader.GetInt32(15);
        var tokenizerId = reader.IsDBNull(16) ? null : reader.GetString(16);
        var tokenizerVersion = reader.IsDBNull(17) ? null : reader.GetString(17);
        var tokenCount = reader.IsDBNull(18) ? (int?)null : reader.GetInt32(18);
        var countedAt = reader.IsDBNull(19) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(19);

        // 合并存储的元数据字典（data->'Metadata'），使元数据投影与全量读取路径
        // 在自定义键上保持一致；再叠加摄取阶段持久化的 tokenization 专用列。
        var baseMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!reader.IsDBNull(20))
        {
            var storedJson = reader.GetString(20);
            if (!string.Equals(storedJson, "null", StringComparison.OrdinalIgnoreCase))
            {
                var stored = Serializer.Deserialize<Dictionary<string, string>>(storedJson);
                foreach (var entry in stored)
                {
                    baseMetadata[entry.Key] = entry.Value;
                }
            }
        }
        var metadata = MergePersistedTokenizationColumns(
            baseMetadata,
            contentHash, contentLength, tokenizerId, tokenizerVersion, tokenCount, countedAt);

        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Layer = layer,
            Status = status,
            Type = type,
            // IncludeContent=false → Content 必须为空字符串（与 ContextItem metadata-only 契约一致）
            Content = string.Empty,
            Tags = tags,
            SourceRefs = sourceRefs,
            RelationRefs = relationRefs,
            Importance = importance,
            Confidence = confidence,
            Version = version,
            Metadata = metadata,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };
    }

    /// <summary>从数据库文本列解析枚举；无法解析时回退到默认值（兼容历史脏数据）。</summary>
    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ? parsed : fallback;
}