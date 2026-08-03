using System.Globalization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 上下文条目与集合元数据存储。
/// 完整 DTO 保存在 jsonb 中，同时抽取常用筛选列以便查询和索引。
/// </summary>
public sealed class PostgresContextStore : PostgresStoreBase, IContextStore, IContextCollectionStore, IContextStoreBatchLookup, IContextStoreMetadataLookup, ITransactionalContextStore, IContextQueryPageStore
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
    /// 在指定事务作用域内保存条目。复用 scope 持有的连接与事务，不开启新连接。
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
        // 从 Metadata 读取摄取阶段已持久化的 content_hash / content_token_cost。
        // BasicContextIngestionService 在摄取时把这两个值写入 Metadata 字典（键见 ContentMetadataKeys），
        // Store 在写入时提取到专用列，Provider 读取时直接命中列而无需在线重算或解析 jsonb。
        var (contentHash, contentTokenCost) = ReadPersistedContentMetrics(normalized);

        command.CommandText = $"""
INSERT INTO {Table("context_items")} (
    workspace_id, collection_id, id, type, title, tags, refs, source_refs,
    importance, version, created_at, updated_at, data,
    content_hash, content_token_cost)
VALUES (
    @workspace_id, @collection_id, @id, @type, @title, @tags, @refs, @source_refs,
    @importance, @version, @created_at, @updated_at, @data,
    @content_hash, @content_token_cost)
ON CONFLICT (workspace_id, collection_id, id) DO UPDATE SET
    type = EXCLUDED.type,
    title = EXCLUDED.title,
    tags = EXCLUDED.tags,
    refs = EXCLUDED.refs,
    source_refs = EXCLUDED.source_refs,
    importance = EXCLUDED.importance,
    version = EXCLUDED.version,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data,
    content_hash = EXCLUDED.content_hash,
    content_token_cost = EXCLUDED.content_token_cost;
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
        command.Parameters.AddWithValue("content_hash", (object?)contentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("content_token_cost", (object?)contentTokenCost ?? DBNull.Value);
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
    /// 批量查询上下文条目。使用 WHERE id = ANY(@ids) 单次 SQL 替代 N 次 GetAsync 并行，
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

    /// <summary>
    /// 按 ID 批量获取上下文条目元数据（不读取/反序列化完整 jsonb 正文）。
    /// 列集与 QueryAsync 的 IncludeContent=false 投影一致，复用 <see cref="ReadMetadataRow"/>；
    /// Metadata 携带摄取阶段持久化的 content_hash / content_token_cost，Provider 据此跳过在线
    /// SHA-256 + tokenizer 调用。Content 恒为空字符串（Selected-only Hydration 契约）。
    /// 语义与 <see cref="BatchGetAsync"/> 一致：只返回命中的条目，顺序不保证，未命中静默丢弃。
    /// </summary>
    public async Task<IReadOnlyList<ContextItem>> BatchGetMetadataAsync(
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
            "SELECT workspace_id, collection_id, id, type, title, importance, version, " +
            "updated_at, created_at, content_hash, content_token_cost, tags, refs, source_refs, " +
            "data->'Metadata' AS metadata " +
            $"FROM {Table("context_items")} " +
            "WHERE workspace_id = @workspace_id AND collection_id = @collection_id AND id = ANY(@ids)";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        AddTextArray(command, "ids", normalizedIds);

        var results = new List<ContextItem>(normalizedIds.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadMetadataRow(reader));
        }
        return results;
    }

    public async Task<IReadOnlyList<ContextItem>> QueryAsync(ContextQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var hasQueryText = !string.IsNullOrWhiteSpace(query.QueryText);
        // FTS 与 ID 匹配合并为单条 CTE 查询（一次数据库往返）。
        // - websearch_to_tsquery 支持 "phrase search" / OR / AND 等语法，且不包含前导通配符（可命中 GIN）。
        // - ts_rank_cd 返回 [0, +∞) 的相关度分数；乘以 100 后写入 Metadata["__ts_rank"]，
        // LexicalCandidateProvider 读取后作为 Provider score（替代固定 10/60 分）。
        // - fts_hits 走 GIN、id_hits 走 B-tree / trigram，DISTINCT ON (workspace_id, collection_id, id) 去重（FTS 命中优先），
        // 避免 OR 条件退化为全表扫描，同时消除第二次往返与应用层合并去重。
        // 查询侧与写入侧对称：search_vector 生成时已应用 cjk_pre_tokenize（拆分 CJK 单字），
        // 查询侧同样对 @query_text 应用 cjk_pre_tokenize，否则中文查询（如"测试"）被当作单一
        // token，无法命中已拆分的 search_vector。cjk_pre_tokenize 只对 CJK 字符插入空格，
        // 对 ASCII/英文查询与 websearch 操作符（OR/-/"）无影响；"测试" → "测 试" → 测 & 试。
        string? rankExpression = hasQueryText
            ? "ts_rank_cd(search_vector, websearch_to_tsquery('simple', cjk_pre_tokenize(@query_text)))"
            : null;

        var take = TakeOrDefault(query.Take);
        var after = query.After;
        var hasCursor = after is not null;

        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var filters = AppendBaseFilters(command, query);
        var baseFilterSql = string.Join(" AND ", filters);

        if (hasQueryText)
        {
            command.Parameters.AddWithValue("query_text", query.QueryText!);
            command.Parameters.AddWithValue("query_exact", query.QueryText!);
            command.Parameters.AddWithValue("query_prefix", query.QueryText! + "%");
        }

        command.Parameters.AddWithValue("take", take);
        if (hasCursor)
        {
            // Keyset 模式：以游标续取替代 OFFSET，Skip 不再参与分页。
            command.Parameters.AddWithValue("after_ts_rank", after!.TsRank);
            command.Parameters.AddWithValue("after_importance", after!.Importance);
            command.Parameters.AddWithValue("after_updated_at", after!.UpdatedAt);
            command.Parameters.AddWithValue("after_id", after!.Id);
        }
        else
        {
            command.Parameters.AddWithValue("skip", Math.Max(0, query.Skip));
        }

        // Keyset 续取谓词按来源拆分（ts_rank 在 id_hits 中为 NULL，无法共用同一谓词）：
        // - fts_hits：游标位于 FTS 命中（SourceOrder=0）时按 (ts_rank, importance, updated_at, id) 续取；
        // 游标已越过全部 FTS 命中（SourceOrder=1）时不再产出任何行。
        // - id_hits：游标位于 ID 命中（SourceOrder=1）时按 (importance, updated_at, id) 续取；
        // 游标位于 FTS 命中（SourceOrder=0）时全部 ID 命中都排在其后，无需过滤。
        // - 无 QueryText 路径：与 id_hits 相同的 (importance, updated_at, id) 续取。
        var ftsAfterSql = string.Empty;
        var idAfterSql = string.Empty;
        var plainAfterSql = string.Empty;
        if (hasCursor)
        {
            if (after!.SourceOrder == 0)
            {
                ftsAfterSql = "AND (" + BuildAfterPredicate(FtsAfterColumns) + ")";
            }
            else
            {
                ftsAfterSql = "AND false";
                idAfterSql = "AND (" + BuildAfterPredicate(IdAfterColumns) + ")";
            }
            plainAfterSql = "AND (" + BuildAfterPredicate(IdAfterColumns) + ")";
        }

        // 内层 CTE 每源只需取 take 条候选：FTS 命中在去重中全部保留，外层 LIMIT 收敛到恰好 take 条；
        // 无游标时维持 LIMIT @skip + @take 供外层 OFFSET 筛选，避免第二页漏结果。
        var innerLimitSql = hasCursor ? "LIMIT @take" : "LIMIT @skip + @take";
        var finalLimitSql = hasCursor ? "LIMIT @take" : "OFFSET @skip LIMIT @take";

        // IncludeContent=false 时只投影 metadata 列，避免读取/反序列化完整 jsonb 正文。
        // 节省 PostgreSQL 网络传输 + JSON 解析 + 大字符串分配；需要正文时由调用方走 BatchGetAsync 二次读取。
        // 必须返回 workspace_id/collection_id/tags/refs/source_refs/created_at/ts_rank——
        // 否则 ReadMetadataRow 构造的 ContextItem 作用域为空，Provider 用空作用域构造 CanonicalKey
        // 会导致 Lexical/Semantic 无法 Canonical Merge（且 CanonicalCandidateKey.Create 会抛 ArgumentException）。
        var results = new List<ContextItem>();

        if (!query.IncludeContent)
        {
            // ts_rank 列仅在 hasQueryText 时追加（与排序条件一致）。
            // 列顺序固定为：workspace_id(0), collection_id(1), id(2), type(3), title(4),
            // importance(5), version(6), updated_at(7), created_at(8), content_hash(9),
            // content_token_cost(10), tags(11), refs(12), source_refs(13), metadata(14), ts_rank?(15), source_order?(16)
            if (hasQueryText && rankExpression is not null)
            {
                command.CommandText = $"""
WITH fts_hits AS (
    SELECT workspace_id, collection_id, id, type, title, importance, version,
           updated_at, created_at, content_hash, content_token_cost,
           tags, refs, source_refs, data->'Metadata' AS metadata,
           {rankExpression} AS ts_rank, 0 AS source_order
    FROM {Table("context_items")}
    WHERE {baseFilterSql} AND search_vector @@ websearch_to_tsquery('simple', cjk_pre_tokenize(@query_text))
    {ftsAfterSql}
    ORDER BY ts_rank DESC, importance DESC, updated_at DESC, id DESC
    {innerLimitSql}
),
id_hits AS (
    SELECT workspace_id, collection_id, id, type, title, importance, version,
           updated_at, created_at, content_hash, content_token_cost,
           tags, refs, source_refs, data->'Metadata' AS metadata,
           NULL::real AS ts_rank, 1 AS source_order
    FROM {Table("context_items")}
    WHERE {baseFilterSql} AND (id = @query_exact OR id LIKE @query_prefix)
    {idAfterSql}
    ORDER BY importance DESC, updated_at DESC, id DESC
    {innerLimitSql}
),
combined AS (
    SELECT * FROM fts_hits
    UNION ALL
    SELECT * FROM id_hits
),
deduped AS (
    SELECT DISTINCT ON (workspace_id, collection_id, id) * FROM combined ORDER BY workspace_id, collection_id, id, source_order
)
SELECT workspace_id, collection_id, id, type, title, importance, version,
       updated_at, created_at, content_hash, content_token_cost,
       tags, refs, source_refs, data->'Metadata' AS metadata, ts_rank, source_order
FROM deduped
ORDER BY source_order, ts_rank DESC NULLS LAST, importance DESC, updated_at DESC, id DESC
{finalLimitSql};
""";
            }
            else
            {
                command.CommandText = $"""
SELECT workspace_id, collection_id, id, type, title, importance, version,
       updated_at, created_at, content_hash, content_token_cost,
       tags, refs, source_refs, data->'Metadata' AS metadata
FROM {Table("context_items")}
WHERE {baseFilterSql} {plainAfterSql}
ORDER BY importance DESC, updated_at DESC, id DESC
{finalLimitSql};
""";
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var tsRankColumnIndex = hasQueryText && rankExpression is not null ? 15 : -1;
            var sourceOrderColumnIndex = hasQueryText && rankExpression is not null ? 16 : -1;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(ReadMetadataRow(reader, tsRankColumnIndex, sourceOrderColumnIndex));
            }
        }
        else
        {
            if (hasQueryText && rankExpression is not null)
            {
                command.CommandText = $"""
WITH fts_hits AS (
    SELECT data, workspace_id, collection_id, id, importance, updated_at,
           {rankExpression} AS ts_rank, 0 AS source_order
    FROM {Table("context_items")}
    WHERE {baseFilterSql} AND search_vector @@ websearch_to_tsquery('simple', cjk_pre_tokenize(@query_text))
    {ftsAfterSql}
    ORDER BY ts_rank DESC, importance DESC, updated_at DESC, id DESC
    {innerLimitSql}
),
id_hits AS (
    SELECT data, workspace_id, collection_id, id, importance, updated_at,
           NULL::real AS ts_rank, 1 AS source_order
    FROM {Table("context_items")}
    WHERE {baseFilterSql} AND (id = @query_exact OR id LIKE @query_prefix)
    {idAfterSql}
    ORDER BY importance DESC, updated_at DESC, id DESC
    {innerLimitSql}
),
combined AS (
    SELECT * FROM fts_hits
    UNION ALL
    SELECT * FROM id_hits
),
deduped AS (
    SELECT DISTINCT ON (workspace_id, collection_id, id) * FROM combined ORDER BY workspace_id, collection_id, id, source_order
)
SELECT data, ts_rank, source_order
FROM deduped
ORDER BY source_order, ts_rank DESC NULLS LAST, importance DESC, updated_at DESC, id DESC
{finalLimitSql};
""";
            }
            else
            {
                command.CommandText = $"""
SELECT data
FROM {Table("context_items")}
WHERE {baseFilterSql} {plainAfterSql}
ORDER BY importance DESC, updated_at DESC, id DESC
{finalLimitSql};
""";
            }

            await using var fullReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var hasRankColumn = hasQueryText && fullReader.FieldCount > 1;
            while (await fullReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var item = Serializer.Deserialize<ContextItem>(fullReader.GetString(0));
                // 把真实 TS rank 注入 Metadata，Provider 读取后替代固定 10/60 分。
                if (hasRankColumn && !fullReader.IsDBNull(1))
                {
                    var rank = fullReader.GetDouble(1);
                    // 查询文本路径的最终排序按 source_order 分组（0=FTS，1=ID 命中）——
                    // 注入 source_order，让分页游标与条目自身携带来源信息。
                    var sourceOrder = fullReader.FieldCount > 2 && !fullReader.IsDBNull(2)
                        ? fullReader.GetInt32(2)
                        : 1;
                    item = WithTsRank(item, rank, sourceOrder);
                }
                else if (!hasRankColumn)
                {
                    // 无查询文本路径：排序键为 (importance, updated_at, id)，等价于 ID 命中源。
                    item = WithSourceOrder(item, 1);
                }
                results.Add(item);
            }
        }

        return results;
    }

    /// <summary>FTS 命中源的 keyset 排序键（该源内 ts_rank 恒非空，无需 NULLS LAST 处理）。</summary>
    private static readonly string[] FtsAfterColumns = ["ts_rank", "importance", "updated_at", "id"];

    /// <summary>ID 命中源与无 QueryText 路径的 keyset 排序键。</summary>
    private static readonly string[] IdAfterColumns = ["importance", "updated_at", "id"];

    /// <inheritdoc />
    public async Task<ContextQueryPageResult> QueryPageAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        // 取 Take + 1 条判定 HasMore；返回前 Take 条，下一页游标取自末条排序键。
        var take = TakeOrDefault(query.Take);
        var fetchQuery = query.CloneWith(take: take + 1);
        var items = (await QueryAsync(fetchQuery, cancellationToken).ConfigureAwait(false)).ToList();

        var hasMore = items.Count > take;
        var pageItems = hasMore ? items.Take(take).ToList() : items;

        if (pageItems.Count == 0)
        {
            return new ContextQueryPageResult { Items = pageItems, HasMore = false, NextCursor = null };
        }

        // 游标排序键与 SQL 最终排序一致：
        // 查询文本路径按 (source_order, ts_rank, importance, updated_at, id) 排序；
        // 无查询文本路径按 (importance, updated_at, id) 排序（等价于 ID 命中源）。
        var hasQueryText = !string.IsNullOrWhiteSpace(query.QueryText);
        var last = pageItems[^1];
        var nextCursor = new ContextQueryCursor
        {
            SourceOrder = hasQueryText ? last.SourceOrder : 1,
            TsRank = hasQueryText ? last.SearchRank : 0,
            Importance = last.Importance,
            UpdatedAt = last.UpdatedAt,
            Id = last.Id
        };

        return new ContextQueryPageResult
        {
            Items = pageItems,
            HasMore = hasMore,
            NextCursor = hasMore ? nextCursor : null
        };
    }

    /// <summary>
    /// 生成 DESC 排序下“严格位于游标之后”的 keyset 谓词：
    /// (k1 &lt; c1) OR (k1 = c1 AND k2 &lt; c2) OR (k1 = c1 AND k2 = c2 AND k3 &lt; c3) ...
    /// 参与比较的列约定 NOT NULL；参数名取 @after_&lt;列名&gt;，调用方负责绑定。
    /// </summary>
    internal static string BuildAfterPredicate(IReadOnlyList<string> columns)
    {
        var clauses = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var prefix = columns.Take(i).Select(c => $"{c} = @after_{c}");
            clauses[i] = "(" + string.Join(" AND ", prefix.Append($"{columns[i]} < @after_{columns[i]}")) + ")";
        }
        return string.Join(" OR ", clauses);
    }

    /// <summary>
    /// 构建 ContextQuery 的基础过滤条件（workspace/collection/tags/types/excluded/refs），
    /// 供 FTS 与 ID 两个查询分支共享。返回 filter 片段列表，参数已绑定到 command。
    /// </summary>
    private List<string> AppendBaseFilters(NpgsqlCommand command, ContextQuery query)
    {
        var filters = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            filters.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
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

        return filters;
    }

    /// <summary>
    /// 从 metadata-only 行构造 <see cref="ContextItem"/>（Content 为空，不触发 jsonb 反序列化）。
    /// 解析 workspace_id/collection_id/tags/refs/source_refs/created_at/ts_rank——
    /// 确保构造的 ContextItem 携带完整作用域与检索评分，Provider 可正确构造 CanonicalKey 与 score。
    /// </summary>
    /// <param name="reader">数据读取器（已定位到当前行）。</param>
    /// <param name="tsRankColumnIndex">ts_rank 列索引；-1 表示该列不存在（无 QueryText 路径）。</param>
    /// <remarks>
    /// 列顺序与 QueryAsync 中 IncludeContent=false 的 SELECT 子句一一对应：
    /// workspace_id(0), collection_id(1), id(2), type(3), title(4), importance(5), version(6),
    /// updated_at(7), created_at(8), content_hash(9), content_token_cost(10),
    /// tags(11), refs(12), source_refs(13), metadata(14), ts_rank?(15), source_order?(16)。
    /// </remarks>
    /// <param name="tsRankColumnIndex">ts_rank 列索引（查询文本路径）；-1 = 无该列。</param>
    /// <param name="sourceOrderColumnIndex">source_order 列索引（查询文本路径）；-1 = 无该列。</param>
    private ContextItem ReadMetadataRow(
        System.Data.Common.DbDataReader reader,
        int tsRankColumnIndex = -1,
        int sourceOrderColumnIndex = -1)
    {
        var workspaceId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var collectionId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var id = reader.GetString(2);
        var type = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        var title = reader.IsDBNull(4) ? null : reader.GetString(4);
        var importance = reader.IsDBNull(5) ? 0.0 : reader.GetDouble(5);
        var version = reader.IsDBNull(6) ? 0L : reader.GetInt64(6);
        var updatedAt = reader.IsDBNull(7) ? DateTimeOffset.MinValue : reader.GetFieldValue<DateTimeOffset>(7);
        var createdAt = reader.IsDBNull(8) ? DateTimeOffset.MinValue : reader.GetFieldValue<DateTimeOffset>(8);
        var contentHash = reader.IsDBNull(9) ? null : reader.GetString(9);
        var contentTokenCost = reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10);
        var tags = reader.IsDBNull(11) ? Array.Empty<string>() : reader.GetFieldValue<string[]>(11);
        var refs = reader.IsDBNull(12) ? Array.Empty<string>() : reader.GetFieldValue<string[]>(12);
        var sourceRefs = reader.IsDBNull(13) ? Array.Empty<string>() : reader.GetFieldValue<string[]>(13);

        // 合并存储的元数据字典（data->'Metadata'），使元数据投影与全量读取路径
        // 在 status 等自定义键上保持一致——检索阶段的废弃项过滤与候选元数据输出不因投影而丢失信息。
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!reader.IsDBNull(14))
        {
            var storedJson = reader.GetString(14);
            if (!string.Equals(storedJson, "null", StringComparison.OrdinalIgnoreCase))
            {
                var stored = Serializer.Deserialize<Dictionary<string, string>>(storedJson);
                foreach (var entry in stored)
                {
                    metadata[entry.Key] = entry.Value;
                }
            }
        }
        // 把持久化的 content_hash / content_token_cost 写入 Metadata，
        // Provider 在 BuildFromContextItem 中读取后跳过在线 SHA-256 + tokenizer 调用。
        if (!string.IsNullOrEmpty(contentHash))
        {
            metadata[ContentMetadataKeys.ContentHash] = contentHash;
        }
        if (contentTokenCost.HasValue)
        {
            metadata[ContentMetadataKeys.ContentTokenCost] = contentTokenCost.Value.ToString(CultureInfo.InvariantCulture);
        }
        // IncludeContent=false 路径下，ts_rank 仍需写入 Metadata——
        // LexicalCandidateProvider 读取后作为 Provider score（替代固定 10/60 分）。
        double searchRank = 0;
        if (tsRankColumnIndex >= 0 && !reader.IsDBNull(tsRankColumnIndex))
        {
            searchRank = reader.GetDouble(tsRankColumnIndex);
            metadata[ContentMetadataKeys.TsRank] = (searchRank * 100.0).ToString(CultureInfo.InvariantCulture);
        }
        // source_order：0 = FTS 命中，1 = ID 精确/前缀命中；无该列（无 QueryText 路径）时按 ID 命中源处理。
        var sourceOrder = sourceOrderColumnIndex >= 0 && !reader.IsDBNull(sourceOrderColumnIndex)
            ? reader.GetInt32(sourceOrderColumnIndex)
            : 1;

        return new ContextItem
        {
            Id = id,
            // 填充作用域——BuildFromContextItem 用这两个字段构造 CanonicalKey，
            // 空值会导致 CanonicalCandidateKey.Create 抛 ArgumentException，破坏 Canonical Merge。
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Type = type,
            Title = title,
            // IncludeContent=false → Content 必须为空字符串（与既有 WithoutContent 契约一致）
            Content = string.Empty,
            // 填充 tags/refs/source_refs——BuildFromContextItem 用 SourceRefs+Refs 构造 Material.SourceRefs。
            Tags = tags,
            Refs = refs,
            SourceRefs = sourceRefs,
            Importance = importance,
            SourceOrder = sourceOrder,
            SearchRank = searchRank,
            Version = version,
            Checksum = contentHash,
            // 填充 created_at——Provider 派生 EntityVersion 时可能用到。
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Metadata = metadata
        };
    }

    /// <summary>返回带 __ts_rank 注入与来源排序信息的 ContextItem 副本（不可变 init 属性 → 显式重建）。</summary>
    private static ContextItem WithTsRank(ContextItem item, double rank, int sourceOrder = 0)
    {
        var metadata = new Dictionary<string, string>(item.Metadata)
        {
            [ContentMetadataKeys.TsRank] = (rank * 100.0).ToString(CultureInfo.InvariantCulture)
        };
        return new ContextItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Title = item.Title,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags,
            Refs = item.Refs,
            SourceRefs = item.SourceRefs,
            Metadata = metadata,
            Importance = item.Importance,
            SourceOrder = sourceOrder,
            SearchRank = rank,
            Version = item.Version,
            Checksum = item.Checksum,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    /// <summary>返回仅更新来源排序的 ContextItem 副本（无查询文本路径按 ID 命中源排序）。</summary>
    private static ContextItem WithSourceOrder(ContextItem item, int sourceOrder)
    {
        return new ContextItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Title = item.Title,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags,
            Refs = item.Refs,
            SourceRefs = item.SourceRefs,
            Metadata = item.Metadata,
            Importance = item.Importance,
            SourceOrder = sourceOrder,
            SearchRank = item.SearchRank,
            Version = item.Version,
            Checksum = item.Checksum,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    /// <summary>
    /// 从 ContextItem.Metadata 读取摄取阶段持久化的 content_hash / content_token_cost。
    /// BasicContextIngestionService 在摄取时写入这两个键；未持久化时返回 (null, null)。
    /// </summary>
    private static (string? ContentHash, int? ContentTokenCost) ReadPersistedContentMetrics(ContextItem item)
    {
        string? contentHash = null;
        int? contentTokenCost = null;

        if (item.Metadata is not null)
        {
            if (item.Metadata.TryGetValue(ContentMetadataKeys.ContentHash, out var hashValue)
                && !string.IsNullOrWhiteSpace(hashValue))
            {
                contentHash = hashValue;
            }
            if (item.Metadata.TryGetValue(ContentMetadataKeys.ContentTokenCost, out var tokenValue)
                && int.TryParse(tokenValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTokens)
                && parsedTokens >= 0)
            {
                contentTokenCost = parsedTokens;
            }
        }

        // Checksum 字段作为 content_hash 的回退源（与 BasicContextIngestionService.ComputeChecksum 一致）。
        contentHash ??= string.IsNullOrWhiteSpace(item.Checksum) ? null : item.Checksum;

        return (contentHash, contentTokenCost);
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

/// <summary>
/// metadata-only 查询路径的候选投影 DTO。
/// </summary>
/// <remarks>
/// <para>
/// 设计动机：原 <c>PostgresContextStore.QueryAsync</c> 的 <c>IncludeContent=false</c> 路径
/// 直接构造 <see cref="ContextItem"/>，但 SELECT 列表缺失 workspace_id/collection_id/tags/refs/
/// source_refs/created_at/ts_rank 等字段，导致 ContextItem 作用域为空、Provider 用空作用域构造
/// <see cref="ContextCore.Abstractions.CanonicalCandidateKey"/> 时抛 <see cref="ArgumentException"/>
/// 或产生错误 key 破坏 Canonical Merge。
/// </para>
/// <para>
/// 此 DTO 显式声明 metadata-only 路径必须返回的字段集合，作为存储层与 Provider 之间的契约。
/// 当前实现仍将字段填充到 <see cref="ContextItem"/> 以保持向后兼容；此 DTO 为未来重构（直接返回
/// 投影而非 ContextItem）保留类型签名。
/// </para>
/// </remarks>
public sealed record ContextCandidateProjection
{
    /// <summary>所属 workspace 作用域（不可空，<see cref="CanonicalCandidateKey"/> 必需）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>所属 collection 作用域（不可空，<see cref="CanonicalCandidateKey"/> 必需）。</summary>
    public required string CollectionId { get; init; }

    /// <summary>条目 ID（<see cref="CanonicalCandidateKey"/> 的 EntityId）。</summary>
    public required string Id { get; init; }

    /// <summary>
    /// 持久化的 content hash（来自 metadata）。Provider 派生 EntityVersion 时复用，
    /// 避免在线 SHA-256 重复计算。
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// 持久化的 content token count（来自 metadata）。Provider 跳过在线 tokenizer 调用。
    /// </summary>
    public int? ContentTokenCount { get; init; }

    /// <summary>
    /// 检索评分（来自 PostgreSQL ts_rank_cd × 100，或 importance / 关键词匹配的回退分）。
    /// Provider 读取后作为 Provider score。
    /// </summary>
    public double RetrievalScore { get; init; }

    /// <summary>
    /// SourceRefs 列表（PostgreSQL source_refs 列）。Provider 用于构造 Material.SourceRefs。
    /// </summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();
}
