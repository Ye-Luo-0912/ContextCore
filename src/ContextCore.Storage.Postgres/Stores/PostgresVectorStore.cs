using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL + pgvector 向量存储。
/// 向量列使用 pgvector 原生类型，DTO 原文仍保存在 jsonb 中，便于后续兼容字段扩展。
/// </summary>
public sealed class PostgresVectorStore : PostgresStoreBase, IVectorStore, IVectorStoreMultiSearch
{
    public PostgresVectorStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("vectors")} (
    workspace_id, collection_id, id, source_id, source_kind, model_name, dimensions,
    content_hash, tags, embedding, created_at, updated_at, data)
VALUES (
    @workspace_id, @collection_id, @id, @source_id, @source_kind, @model_name, @dimensions,
    @content_hash, @tags, @embedding::vector, @created_at, @updated_at, @data)
ON CONFLICT (workspace_id, id) DO UPDATE SET
    collection_id = EXCLUDED.collection_id,
    source_id = EXCLUDED.source_id,
    source_kind = EXCLUDED.source_kind,
    model_name = EXCLUDED.model_name,
    dimensions = EXCLUDED.dimensions,
    content_hash = EXCLUDED.content_hash,
    tags = EXCLUDED.tags,
    embedding = EXCLUDED.embedding,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", CollectionKey(normalized.CollectionId));
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("source_id", normalized.SourceId);
        command.Parameters.AddWithValue("source_kind", normalized.SourceKind);
        command.Parameters.AddWithValue("model_name", normalized.ModelName);
        command.Parameters.AddWithValue("dimensions", normalized.Dimensions);
        command.Parameters.AddWithValue("content_hash", normalized.ContentHash);
        AddTextArray(command, "tags", normalized.Tags);
        command.Parameters.AddWithValue("embedding", PostgresVectorFormat.ToVectorLiteral(normalized.Vector));
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorRecord?> GetAsync(string workspaceId, string vectorId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT data FROM {Table("vectors")} WHERE workspace_id = @workspace_id AND id = @id";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("id", vectorId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json) ? null : Serializer.Deserialize<VectorRecord>(json);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(VectorQuery query, CancellationToken cancellationToken = default)
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
            command.Parameters.AddWithValue("collection_id", CollectionKey(query.CollectionId));
        }

        if (query.SourceKinds.Count > 0)
        {
            filters.Add("source_kind = ANY(@source_kinds)");
            AddTextArray(command, "source_kinds", query.SourceKinds);
        }

        if (query.Tags.Count > 0)
        {
            filters.Add("tags @> @tags");
            AddTextArray(command, "tags", query.Tags);
        }

        // 本次查询不返回的来源 ID 在 LIMIT 前排除，避免已持有 ID 占满 TopK。
        if (query.ExcludeSourceIds.Count > 0)
        {
            filters.Add("NOT (source_id = ANY(@exclude_source_ids))");
            AddTextArray(command, "exclude_source_ids", query.ExcludeSourceIds);
        }

        var scoreExpression = "(1 - (embedding <=> @query_vector::vector))";
        if (query.MinScore is not null)
        {
            filters.Add($"{scoreExpression} >= @min_score");
            command.Parameters.AddWithValue("min_score", query.MinScore.Value);
        }

        command.Parameters.AddWithValue("query_vector", PostgresVectorFormat.ToVectorLiteral(query.Vector));
        command.Parameters.AddWithValue("take", query.TopK > 0 ? query.TopK : 10);
        command.CommandText = $"""
SELECT data, {scoreExpression} AS score
FROM {Table("vectors")}
WHERE {string.Join(" AND ", filters)}
ORDER BY embedding <=> @query_vector::vector, updated_at DESC
LIMIT @take;
""";

        var results = new List<VectorSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rank = 1;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = Serializer.Deserialize<VectorRecord>(reader.GetString(0));
            results.Add(new VectorSearchResult
            {
                Record = query.IncludeVector ? record : WithoutVector(record),
                Score = reader.GetDouble(1),
                Rank = rank++
            });
        }

        return results;
    }

    /// <summary>
    /// 多问句向量检索：单条 SQL（一次数据库往返）完成全部问句。
    /// 问句向量以 text[] 传递（pgvector 字面量格式），unnest WITH ORDINALITY 解包后
    /// 逐问句在 LATERAL 内计算相似度；每问句各自保留 TopK（ROW_NUMBER 分区），
    /// 排序与逐条 SearchAsync 一致（score DESC, updated_at DESC），不放大连接池占用。
    /// </summary>
    public async Task<IReadOnlyList<VectorMultiSearchResult>> SearchMultiAsync(
        VectorMultiQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        if (query.Queries.Count == 0)
        {
            return Array.Empty<VectorMultiSearchResult>();
        }

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        // 问句向量以 text[] 传递（Npgsql 负责数组元素转义），元素为 pgvector 字面量格式。
        command.Parameters.AddWithValue(
            "query_literals",
            query.Queries.Select(q => PostgresVectorFormat.ToVectorLiteral(q.Vector)).ToArray());
        command.Parameters.AddWithValue("take", query.TopK > 0 ? query.TopK : 10);

        // 共享过滤（作用域/来源类型/tags/排除）；相似度阈值共享。
        var filters = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            filters.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", CollectionKey(query.CollectionId));
        }

        if (query.SourceKinds.Count > 0)
        {
            filters.Add("source_kind = ANY(@source_kinds)");
            AddTextArray(command, "source_kinds", query.SourceKinds);
        }

        if (query.Tags.Count > 0)
        {
            filters.Add("tags @> @tags");
            AddTextArray(command, "tags", query.Tags);
        }

        // 本次查询不返回的来源 ID 在排序/截断前排除，避免已持有 ID 占满 TopK。
        if (query.ExcludeSourceIds.Count > 0)
        {
            filters.Add("NOT (source_id = ANY(@exclude_source_ids))");
            AddTextArray(command, "exclude_source_ids", query.ExcludeSourceIds);
        }

        var minScoreSql = string.Empty;
        if (query.MinScore is not null)
        {
            minScoreSql = "AND (1 - (embedding <=> q.vector_literal::vector)) >= @min_score";
            command.Parameters.AddWithValue("min_score", query.MinScore.Value);
        }

        command.CommandText = $"""
WITH qs AS (
    SELECT query_index, vector_literal
    FROM unnest(@query_literals) WITH ORDINALITY AS q(vector_literal, query_index)
),
scored AS (
    SELECT q.query_index, v.data, v.updated_at,
           (1 - (v.embedding <=> q.vector_literal::vector)) AS score
    FROM qs q
    CROSS JOIN LATERAL (
        SELECT data, embedding, updated_at
        FROM {Table("vectors")}
        WHERE {string.Join(" AND ", filters)}
          {minScoreSql}
    ) v
),
ranked AS (
    SELECT *, ROW_NUMBER() OVER (
        PARTITION BY query_index
        ORDER BY score DESC, updated_at DESC
    ) AS rn
    FROM scored
)
SELECT query_index, data, score
FROM ranked
WHERE rn <= @take
ORDER BY query_index, score DESC, updated_at DESC;
""";

        var buckets = new List<VectorSearchResult>[query.Queries.Count];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new List<VectorSearchResult>();
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var queryIndex = reader.GetInt32(0);
            var record = Serializer.Deserialize<VectorRecord>(reader.GetString(1));
            var score = reader.GetDouble(2);
            // unnest WITH ORDINALITY 的序号列从 1 开始，桶数组按 0 基索引。
            var bucket = buckets[queryIndex - 1];
            bucket.Add(new VectorSearchResult
            {
                Record = query.IncludeVector ? record : WithoutVector(record),
                Score = score,
                Rank = bucket.Count + 1
            });
        }

        var results = new List<VectorMultiSearchResult>(query.Queries.Count);
        for (var i = 0; i < query.Queries.Count; i++)
        {
            results.Add(new VectorMultiSearchResult
            {
                QueryId = query.Queries[i].Id,
                Hits = buckets[i]
            });
        }

        return results;
    }

    public async Task DeleteAsync(string workspaceId, string vectorId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"DELETE FROM {Table("vectors")} WHERE workspace_id = @workspace_id AND id = @id";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("id", vectorId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static VectorRecord Normalize(VectorRecord record)
    {
        var now = DateTimeOffset.UtcNow;
        return new VectorRecord
        {
            Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            SourceId = record.SourceId,
            SourceKind = record.SourceKind,
            ModelName = record.ModelName,
            Dimensions = record.Dimensions > 0 ? record.Dimensions : record.Vector.Count,
            Vector = [.. record.Vector],
            ContentHash = record.ContentHash,
            Tags = [.. record.Tags],
            Metadata = new Dictionary<string, string>(record.Metadata),
            CreatedAt = record.CreatedAt == default ? now : record.CreatedAt,
            UpdatedAt = record.UpdatedAt == default ? now : record.UpdatedAt
        };
    }

    private static VectorRecord WithoutVector(VectorRecord record)
    {
        return new VectorRecord
        {
            Id = record.Id,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            SourceId = record.SourceId,
            SourceKind = record.SourceKind,
            ModelName = record.ModelName,
            Dimensions = record.Dimensions,
            Vector = Array.Empty<float>(),
            ContentHash = record.ContentHash,
            Tags = record.Tags.ToArray(),
            Metadata = new Dictionary<string, string>(record.Metadata),
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt
        };
    }

    /// <summary>
    /// 诊断（基线采集用）：对向量搜索 SQL 生成 EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)。
    /// 与 <see cref="SearchAsync"/> 完全同参同 SQL，仅前置 EXPLAIN，用于建立 vector 检索
    /// 真实执行计划 / roundtrip 基线（ANALYZE 会真实执行查询）。
    /// </summary>
    internal async Task<string?> ExplainSearchAsync(
        VectorQuery query,
        CancellationToken cancellationToken = default)
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
            command.Parameters.AddWithValue("collection_id", CollectionKey(query.CollectionId));
        }

        if (query.SourceKinds.Count > 0)
        {
            filters.Add("source_kind = ANY(@source_kinds)");
            AddTextArray(command, "source_kinds", query.SourceKinds);
        }

        if (query.Tags.Count > 0)
        {
            filters.Add("tags @> @tags");
            AddTextArray(command, "tags", query.Tags);
        }

        if (query.ExcludeSourceIds.Count > 0)
        {
            filters.Add("NOT (source_id = ANY(@exclude_source_ids))");
            AddTextArray(command, "exclude_source_ids", query.ExcludeSourceIds);
        }

        var scoreExpression = "(1 - (embedding <=> @query_vector::vector))";
        if (query.MinScore is not null)
        {
            filters.Add($"{scoreExpression} >= @min_score");
            command.Parameters.AddWithValue("min_score", query.MinScore.Value);
        }

        command.Parameters.AddWithValue("query_vector", PostgresVectorFormat.ToVectorLiteral(query.Vector));
        command.Parameters.AddWithValue("take", query.TopK > 0 ? query.TopK : 10);
        command.CommandText =
            "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
            + $"""
SELECT data, {scoreExpression} AS score
FROM {Table("vectors")}
WHERE {string.Join(" AND ", filters)}
ORDER BY embedding <=> @query_vector::vector, updated_at DESC
LIMIT @take;
""";

        var sb = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sb.Append(reader.GetString(0));
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}