using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using System.Runtime.CompilerServices;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 关系存储，使用结构化列加 jsonb 原文保存关系边。</summary>
public sealed class PostgresRelationStore : PostgresStoreBase, IRelationStore, ITransactionalRelationStore, IRelationStreamStore
{
    public PostgresRelationStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <summary>GRAPH-11：SaveAsync 委托 BatchUpsertAsync，保留为单条便利方法。</summary>
    public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
        => BatchUpsertAsync([relation], cancellationToken);

    /// <summary>按关系 ID 读取单条边；供 Postgres provider diagnostics/parity 使用，不改变 IRelationStore 契约。</summary>
    public async Task<ContextRelation?> GetAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND id = @id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("id", relationId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string json ? Serializer.Deserialize<ContextRelation>(json) : null;
    }

    /// <summary>删除单条边；仅用于显式 provider parity/cleanup，不参与默认运行时。</summary>
    public async Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        ApplyDeleteCommandText(command);
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("id", relationId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// P0-3：在指定事务作用域内删除单条边。复用 scope 持有的连接与事务，不开启新连接。
    /// scope 必须是 <see cref="PostgresWriteTransactionScope"/>。提交由调用方通过 scope.CommitAsync 完成。
    /// </summary>
    public async Task<bool> DeleteAsync(
        string workspaceId,
        string collectionId,
        string relationId,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var pgScope = scope as PostgresWriteTransactionScope
            ?? throw new InvalidOperationException(
                "PostgresRelationStore 仅支持 PostgresWriteTransactionScope；请通过 PostgresWriteTransactionScopeFactory 创建事务作用域。");
        if (!scope.IsActive)
        {
            throw new InvalidOperationException("事务作用域已结束（Commit/Rollback），无法继续写入。");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        using var command = pgScope.Connection.CreateCommand();
        if (pgScope.Transaction is not null)
        {
            command.Transaction = pgScope.Transaction;
        }
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        ApplyDeleteCommandText(command);
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("id", relationId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private void ApplyDeleteCommandText(NpgsqlCommand command)
    {
        command.CommandText = $"""
DELETE FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND id = @id;
""";
    }

    /// <summary>清理显式 smoke/canary scope；只供受控验证流程调用。</summary>
    public async Task<int> DeleteByScopeAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// GRAPH-11：批量 upsert 改用 NpgsqlBatch，单次往返提交所有语句；单事务保证原子性。
    /// </summary>
    public async Task BatchUpsertAsync(IEnumerable<ContextRelation> relations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);
        var list = relations.ToList();
        if (list.Count == 0) return;

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var batch = BuildBatch(connection, transaction, list, cancellationToken);
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// P0-3：在指定事务作用域内批量 upsert 关系。复用 scope 持有的连接与事务。
    /// 提交由调用方通过 scope.CommitAsync 完成；此方法只执行 batch，不自行提交或回滚。
    /// </summary>
    public async Task BatchUpsertAsync(
        IEnumerable<ContextRelation> relations,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(scope);
        var pgScope = scope as PostgresWriteTransactionScope
            ?? throw new InvalidOperationException(
                "PostgresRelationStore 仅支持 PostgresWriteTransactionScope；请通过 PostgresWriteTransactionScopeFactory 创建事务作用域。");
        if (!scope.IsActive)
        {
            throw new InvalidOperationException("事务作用域已结束（Commit/Rollback），无法继续写入。");
        }

        var list = relations.ToList();
        if (list.Count == 0) return;

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        using var batch = BuildBatch(pgScope.Connection, pgScope.Transaction, list, cancellationToken);
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private NpgsqlBatch BuildBatch(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        List<ContextRelation> list,
        CancellationToken cancellationToken)
    {
        // GRAPH-11：NpgsqlBatch 单次往返提交全部 upsert 语句
        var batch = new NpgsqlBatch(connection, transaction);
        batch.Timeout = Options.CommandTimeoutSeconds;

        foreach (var relation in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = CompositeContextNormalizer.Normalize(relation);
            var batchCommand = new NpgsqlBatchCommand
            {
                CommandText = $"""
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
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
"""
            };
            batchCommand.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
            batchCommand.Parameters.AddWithValue("collection_id", normalized.CollectionId);
            batchCommand.Parameters.AddWithValue("id", normalized.Id);
            batchCommand.Parameters.AddWithValue("source_id", normalized.SourceId);
            batchCommand.Parameters.AddWithValue("target_id", normalized.TargetId);
            batchCommand.Parameters.AddWithValue("relation_type", normalized.RelationType);
            batchCommand.Parameters.AddWithValue("weight", normalized.Weight);
            batchCommand.Parameters.AddWithValue("confidence", normalized.Confidence);
            batchCommand.Parameters.AddWithValue("created_at", normalized.CreatedAt);
            var dataParam = batchCommand.Parameters.Add("data", NpgsqlDbType.Jsonb);
            dataParam.Value = Serializer.Serialize(normalized);
            batch.BatchCommands.Add(batchCommand);
        }

        return batch;
    }

    public async Task<IReadOnlyList<ContextRelation>> QueryAsync(ContextRelationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        ApplyQueryCommandText(command, query);

        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// P0-3：在指定事务作用域内查询关系。读共享同一事务视图，避免读到其他事务未提交的数据。
    /// </summary>
    public async Task<IReadOnlyList<ContextRelation>> QueryAsync(
        ContextRelationQuery query,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);
        var pgScope = scope as PostgresWriteTransactionScope
            ?? throw new InvalidOperationException(
                "PostgresRelationStore 仅支持 PostgresWriteTransactionScope；请通过 PostgresWriteTransactionScopeFactory 创建事务作用域。");
        if (!scope.IsActive)
        {
            throw new InvalidOperationException("事务作用域已结束（Commit/Rollback），无法继续读取。");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        using var command = pgScope.Connection.CreateCommand();
        if (pgScope.Transaction is not null)
        {
            command.Transaction = pgScope.Transaction;
        }
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        ApplyQueryCommandText(command, query);

        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyQueryCommandText(NpgsqlCommand command, ContextRelationQuery query)
    {
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
        var skip = query.Skip > 0 ? query.Skip : 0;
        command.Parameters.AddWithValue("skip", skip);
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE {string.Join(" AND ", filters)}
ORDER BY weight DESC, confidence DESC, created_at DESC
LIMIT @take OFFSET @skip;
""";
    }

    /// <summary>GRAPH-10：统一邻居查询，在 SQL 中过滤和 Limit。</summary>
    public async Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(
        RelationNeighborQuery query,
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
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }

        switch (query.Direction)
        {
            case RelationDirection.Outgoing:
                filters.Add("source_id = @item_id");
                break;
            case RelationDirection.Incoming:
                filters.Add("target_id = @item_id");
                break;
            default:
                filters.Add("(source_id = @item_id OR target_id = @item_id)");
                break;
        }
        command.Parameters.AddWithValue("item_id", query.ItemId);

        if (!string.IsNullOrWhiteSpace(query.RelationType))
        {
            // P0-2：使用真实列 relation_type 而非 JSON 提取（既有索引，且避免大小写敏感问题）
            filters.Add("relation_type = @relation_type");
            command.Parameters.AddWithValue("relation_type", query.RelationType);
        }

        // P3-02：多类型过滤优先于单类型，在 LIMIT 前下推到 SQL
        if (query.AllowedRelationTypes.Count > 0)
        {
            var paramNames = new List<string>();
            for (var i = 0; i < query.AllowedRelationTypes.Count; i++)
            {
                var paramName = $"allowed_rt_{i}";
                paramNames.Add($"@{paramName}");
                command.Parameters.AddWithValue(paramName, query.AllowedRelationTypes[i]);
            }
            // P0-2：使用真实列 relation_type
            filters.Add($"relation_type IN ({string.Join(", ", paramNames)})");
        }

        if (query.MinConfidence > 0)
        {
            // P0-2：修复 cast 优先级 bug。
            // 旧：data ->> 'Confidence'::numeric 实际解析为 data ->> ('Confidence'::numeric)，
            // 即先 cast 字面量 'Confidence' 为 numeric（运行时抛错），而非 cast JSON 提取值。
            // 新：直接使用真实列 confidence（double precision，既有索引）。
            filters.Add("confidence >= @min_confidence");
            command.Parameters.AddWithValue("min_confidence", query.MinConfidence);
        }

        if (query.ExcludedLifecycles.Count > 0)
        {
            var paramNames = new List<string>();
            for (var i = 0; i < query.ExcludedLifecycles.Count; i++)
            {
                var paramName = $"ex_lc_{i}";
                paramNames.Add($"@{paramName}");
                command.Parameters.AddWithValue(paramName, query.ExcludedLifecycles[i]);
            }
            filters.Add($"(data ->> 'Lifecycle' IS NULL OR data ->> 'Lifecycle' NOT IN ({string.Join(", ", paramNames)}))");
        }

        if (query.ExcludedReviewStatuses.Count > 0)
        {
            var paramNames = new List<string>();
            for (var i = 0; i < query.ExcludedReviewStatuses.Count; i++)
            {
                var paramName = $"ex_rs_{i}";
                paramNames.Add($"@{paramName}");
                command.Parameters.AddWithValue(paramName, query.ExcludedReviewStatuses[i]);
            }
            filters.Add($"(data ->> 'ReviewStatus' IS NULL OR data ->> 'ReviewStatus' NOT IN ({string.Join(", ", paramNames)}))");
        }

        var effectiveTake = query.Take > 0 ? query.Take : 100;
        var effectiveSkip = query.Skip > 0 ? query.Skip : 0;
        var maxScan = query.MaxScan > 0 ? query.MaxScan : 1000;
        command.Parameters.AddWithValue("take", effectiveTake);
        command.Parameters.AddWithValue("skip", effectiveSkip);
        command.Parameters.AddWithValue("max_scan", maxScan);

        // P0-2：修复 3 个 SQL bug：
        //   1) 旧：data ->> 'Confidence'::numeric（cast 优先级错误，运行时抛错）→ 改用真实列 confidence
        //   2) 旧：ORDER BY data ->> 'Weight'（字符串排序，"10" 排在 "9" 之前）→ 改用真实列 weight（double precision）
        //   3) 旧：内层 LIMIT @max_scan 无 ORDER BY（截断未排序集合，可能漏掉高权重边）
        //      新：ORDER BY 移入内层，max_scan 作为"取 top N 后再分页"的扫描上限
        command.CommandText = $"""
SELECT data
FROM (
    SELECT data
    FROM {Table("relations")}
    WHERE {string.Join(" AND ", filters)}
    ORDER BY weight DESC, confidence DESC, created_at DESC
    LIMIT @max_scan
) scanned
LIMIT @take OFFSET @skip;
""";
        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// P1-6 / P7：批量邻居查询。使用 CROSS JOIN LATERAL 实现 per-seed TopN，
    /// 每个种子独立扫描 + 排序 + LIMIT @per_seed_scan，命中 (workspace_id, source_id) / (workspace_id, target_id) 索引。
    /// </summary>
    /// <remarks>
    /// P7 优化（替代 P1-6 的 ANY(@item_ids) OR ANY(@item_ids) 全局扫描方案）：
    /// <list type="bullet">
    /// <item>旧方案用 (source_id = ANY(@ids) OR target_id = ANY(@ids)) 削弱单侧索引，全局 LIMIT 上限 100K。</item>
    /// <item>新方案 per-seed LIMIT @per_seed_scan，总返回行数 ≤ seeds.Count × MaxScan，避免一次拉回十万条完整 JSON。</item>
    /// <item>Both 方向：(source_id = seed.id OR target_id = seed.id)。自环边（source == target == seed）只返回一次。</item>
    /// <item>truncated 信号：bucket.Count &gt;= maxScan 表示 LATERAL LIMIT 命中，可能还有更多低权重行未读。</item>
    /// </list>
    /// </remarks>
    public async Task<IReadOnlyList<RelationNeighborBatchResult>> QueryNeighborsBatchAsync(
        RelationNeighborBatchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // 去重种子 ID（保留原序）
        var seedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seeds = new List<string>(query.ItemIds.Count);
        foreach (var id in query.ItemIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && seedSet.Add(id))
            {
                seeds.Add(id);
            }
        }
        if (seeds.Count == 0)
        {
            return Array.Empty<RelationNeighborBatchResult>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        // P7：LATERAL 内部 WHERE 引用外层 seed.id（correlated subquery）。
        // 过滤条件全部下推到 LATERAL 内，每个种子独立使用 (workspace_id, source_id) / (workspace_id, target_id) 索引。
        var filters = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            filters.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }

        // P7：方向过滤改为与 seed.id 的等值比较（替代 ANY(@item_ids)），让 LATERAL 走索引。
        switch (query.Direction)
        {
            case RelationDirection.Outgoing:
                filters.Add("source_id = seed.id");
                break;
            case RelationDirection.Incoming:
                filters.Add("target_id = seed.id");
                break;
            default:
                // Both 方向：source 或 target 命中种子即可。PostgreSQL 优化器可用 BitmapOr 合并两侧索引扫描。
                // 自环边（source == target == seed）只匹配一次（OR 不重复返回）。
                filters.Add("(source_id = seed.id OR target_id = seed.id)");
                break;
        }

        if (!string.IsNullOrWhiteSpace(query.RelationType))
        {
            filters.Add("relation_type = @relation_type");
            command.Parameters.AddWithValue("relation_type", query.RelationType);
        }

        if (query.AllowedRelationTypes.Count > 0)
        {
            var paramNames = new List<string>();
            for (var i = 0; i < query.AllowedRelationTypes.Count; i++)
            {
                var paramName = $"allowed_rt_{i}";
                paramNames.Add($"@{paramName}");
                command.Parameters.AddWithValue(paramName, query.AllowedRelationTypes[i]);
            }
            filters.Add($"relation_type IN ({string.Join(", ", paramNames)})");
        }

        if (query.MinConfidence > 0)
        {
            filters.Add("confidence >= @min_confidence");
            command.Parameters.AddWithValue("min_confidence", query.MinConfidence);
        }

        if (query.ExcludedLifecycles.Count > 0)
        {
            var paramNames = new List<string>();
            for (var i = 0; i < query.ExcludedLifecycles.Count; i++)
            {
                var paramName = $"ex_lc_{i}";
                paramNames.Add($"@{paramName}");
                command.Parameters.AddWithValue(paramName, query.ExcludedLifecycles[i]);
            }
            filters.Add($"(data ->> 'Lifecycle' IS NULL OR data ->> 'Lifecycle' NOT IN ({string.Join(", ", paramNames)}))");
        }

        if (query.ExcludedReviewStatuses.Count > 0)
        {
            var paramNames = new List<string>();
            for (var i = 0; i < query.ExcludedReviewStatuses.Count; i++)
            {
                var paramName = $"ex_rs_{i}";
                paramNames.Add($"@{paramName}");
                command.Parameters.AddWithValue(paramName, query.ExcludedReviewStatuses[i]);
            }
            filters.Add($"(data ->> 'ReviewStatus' IS NULL OR data ->> 'ReviewStatus' NOT IN ({string.Join(", ", paramNames)}))");
        }

        // P7：per-seed 扫描上限 = MaxScan（保留原语义：先按权重取 top MaxScan，再在 C# 端 Skip/Take 分页）。
        // 总返回行数 ≤ seeds.Count × maxScan，远小于旧方案的 100K 全局上限。
        var maxScan = query.MaxScan > 0 ? query.MaxScan : 1000;
        command.Parameters.AddWithValue("per_seed_scan", maxScan);
        command.Parameters.AddWithValue("item_ids", seeds.ToArray());

        command.CommandText = $"""
SELECT seed.id AS seed_id, r.data AS relation_data
FROM unnest(@item_ids) AS seed(id)
CROSS JOIN LATERAL (
    SELECT data
    FROM {Table("relations")}
    WHERE {string.Join(" AND ", filters)}
    ORDER BY weight DESC, confidence DESC, created_at DESC
    LIMIT @per_seed_scan
) r;
""";

        // P7：读取 (seed_id, relation_data) 对，按 seed_id 分桶。LATERAL 内已排序，分桶保持顺序。
        var buckets = new Dictionary<string, List<ContextRelation>>(seeds.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds)
        {
            buckets[seed] = new List<ContextRelation>();
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var seedId = reader.GetString(0);
            var json = reader.GetString(1);
            var relation = Serializer.Deserialize<ContextRelation>(json);
            if (relation is null) continue;

            if (buckets.TryGetValue(seedId, out var bucket))
            {
                bucket.Add(relation);
            }
        }

        var effectiveTake = query.Take > 0 ? query.Take : 100;
        var effectiveSkip = query.Skip > 0 ? query.Skip : 0;
        var results = new List<RelationNeighborBatchResult>(seeds.Count);
        foreach (var seed in seeds)
        {
            var bucket = buckets[seed];
            // P7：truncated 信号来自 LATERAL LIMIT 命中。
            // bucket.Count >= maxScan 表示 LATERAL 返回了 maxScan 行（达到 LIMIT 上限），可能还有更多低权重行未读。
            // bucket.Count < maxScan 表示该种子所有匹配行已读全（无截断）。
            // 相比旧方案，此信号精确到 per-seed，不再依赖 SQL 全局 LIMIT 命中的保守推断。
            var truncated = bucket.Count >= maxScan;
            // 桶内已排序（LATERAL 内 ORDER BY），直接 Skip + Take 完成分页。
            var seedRelations = bucket
                .Skip(effectiveSkip)
                .Take(effectiveTake)
                .ToArray();
            if (seedRelations.Length > 0)
            {
                results.Add(new RelationNeighborBatchResult
                {
                    ItemId = seed,
                    Relations = seedRelations,
                    Truncated = truncated
                });
            }
        }

        return results;
    }

    /// <summary>按 lifecycle 查询；GRAPH-08 起优先查正式字段，兼容旧 Metadata 数据。</summary>
    public async Task<IReadOnlyList<ContextRelation>> QueryByLifecycleAsync(
        string workspaceId,
        string collectionId,
        string lifecycle,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND (
      data ->> 'Lifecycle' = @expected_value
      OR data -> 'Metadata' ->> 'lifecycle' = @expected_value
      OR data -> 'Metadata' ->> 'Lifecycle' = @expected_value
      OR data -> 'metadata' ->> 'lifecycle' = @expected_value
  )
ORDER BY weight DESC, confidence DESC, created_at DESC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("expected_value", lifecycle);
        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 reviewStatus 查询；GRAPH-08 起优先查正式字段，兼容旧 Metadata 数据。</summary>
    public async Task<IReadOnlyList<ContextRelation>> QueryByReviewStatusAsync(
        string workspaceId,
        string collectionId,
        string reviewStatus,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND (
      data ->> 'ReviewStatus' = @expected_value
      OR data -> 'Metadata' ->> 'reviewStatus' = @expected_value
      OR data -> 'Metadata' ->> 'ReviewStatus' = @expected_value
      OR data -> 'metadata' ->> 'reviewStatus' = @expected_value
  )
ORDER BY weight DESC, confidence DESC, created_at DESC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("expected_value", reviewStatus);
        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>查询 replacement chain 相关边，不执行图扩展或运行时排序。</summary>
    public async Task<IReadOnlyList<ContextRelation>> QueryReplacementChainRelationsAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND (source_id = @item_id OR target_id = @item_id)
  AND relation_type = ANY(@relation_types)
ORDER BY weight DESC, confidence DESC, created_at DESC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("item_id", itemId);
        command.Parameters.AddWithValue("relation_types", new[]
        {
            ContextRelationTypes.SupersededBy,
            ContextRelationTypes.Replaces,
            ContextRelationTypes.ReplacedBy
        });

        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>统计当前 schema 中的 relation 数量。</summary>
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT count(*) FROM {Table("relations")};";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<ContextRelation>> QueryByMetadataAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> metadataKeys,
        string expectedValue,
        CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND (
      data -> 'Metadata' ->> @metadata_key_0 = @expected_value
      OR data -> 'Metadata' ->> @metadata_key_1 = @expected_value
      OR data -> 'metadata' ->> @metadata_key_0 = @expected_value
      OR data -> 'metadata' ->> @metadata_key_1 = @expected_value
  )
ORDER BY weight DESC, confidence DESC, created_at DESC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("metadata_key_0", metadataKeys[0]);
        command.Parameters.AddWithValue("metadata_key_1", metadataKeys.Count > 1 ? metadataKeys[1] : metadataKeys[0]);
        command.Parameters.AddWithValue("expected_value", expectedValue);

        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ContextRelation>> ReadRelationsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var results = new List<ContextRelation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Serializer.Deserialize<ContextRelation>(reader.GetString(0)));
        }

        return results;
    }

    /// <summary>
    /// P1-7：流式枚举关系，使用 NpgsqlDataReader.ReadAsync 逐行读取，避免一次性将全部结果缓冲到 List。
    /// 不应用 LIMIT/OFFSET——返回完整候选集，由消费方按需裁剪。
    /// 排序与 QueryAsync 一致（weight/confidence/createdAt desc）。
    /// </summary>
    public async IAsyncEnumerable<ContextRelation> StreamRelationsAsync(
        string workspaceId,
        string? collectionId = null,
        string? itemId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        // 构造流式 SQL：与 ApplyQueryCommandText 一致的过滤条件，但不应用 LIMIT/OFFSET。
        var filters = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            filters.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", collectionId);
        }
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            filters.Add("(source_id = @item_id OR target_id = @item_id)");
            command.Parameters.AddWithValue("item_id", itemId);
        }

        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE {string.Join(" AND ", filters)}
ORDER BY weight DESC, confidence DESC, created_at DESC;
""";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var relation = Serializer.Deserialize<ContextRelation>(json);
            if (relation is not null)
            {
                yield return relation;
            }
        }
    }
}
