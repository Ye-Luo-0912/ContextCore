using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 关系存储，使用结构化列加 jsonb 原文保存关系边。</summary>
public sealed class PostgresRelationStore : PostgresStoreBase, IRelationStore, ITransactionalRelationStore
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
    /// P1-6：批量邻居查询。单条 SQL 用 = ANY(@item_ids) 一次性获取所有种子邻居，
    /// 在 C# 端按种子分桶并应用 per-seed 排序 + MaxScan + Skip + Take。
    /// </summary>
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

        var filters = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            filters.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }

        // P1-6: 用 = ANY(@item_ids) 单次过滤；Both 方向取并集，C# 端再分桶去重。
        var itemIdsParam = seeds.ToArray();
        switch (query.Direction)
        {
            case RelationDirection.Outgoing:
                filters.Add("source_id = ANY(@item_ids)");
                break;
            case RelationDirection.Incoming:
                filters.Add("target_id = ANY(@item_ids)");
                break;
            default:
                filters.Add("(source_id = ANY(@item_ids) OR target_id = ANY(@item_ids))");
                break;
        }
        command.Parameters.AddWithValue("item_ids", itemIdsParam);

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

        // P1-6: 不在 SQL 内做 per-seed LIMIT（LATERAL JOIN 复杂且收益有限）；
        // 单条 SQL 一次性取回所有匹配行（受 max_scan × seeds 数量隐式约束），C# 端分桶 + per-seed 排序 + Skip/Take。
        // 全局 LIMIT 设为 max_scan × seeds.Count（per-seed 上限 × 种子数），并加 10K 安全上限防止极端输入。
        // P1-4：LIMIT 用 global_limit + 1 探测，避免"恰好等于上限"时的假阳性截断信号。
        var maxScan = query.MaxScan > 0 ? query.MaxScan : 1000;
        var globalLimit = Math.Min((long)maxScan * seeds.Count, 100_000);
        var globalLimitProbe = globalLimit + 1;
        command.Parameters.AddWithValue("global_limit_probe", globalLimitProbe);

        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE {string.Join(" AND ", filters)}
ORDER BY weight DESC, confidence DESC, created_at DESC
LIMIT @global_limit_probe;
""";

        var allRelations = await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);

        // P1-4：检测 SQL 全局 LIMIT 是否命中。若 allRelations.Count > globalLimit，
        // 说明 SQL 还有更多匹配行未读（保守标记所有非空桶为 Truncated）；
        // 若 <= globalLimit，SQL 已读完全部匹配，per-seed Truncated 完全由 bucket.Count > maxScan 决定。
        var sqlGloballyTruncated = allRelations.Count > globalLimit;

        // C# 端分桶
        var effectiveTake = query.Take > 0 ? query.Take : 100;
        var effectiveSkip = query.Skip > 0 ? query.Skip : 0;
        var buckets = new Dictionary<string, List<ContextRelation>>(seeds.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds)
        {
            buckets[seed] = new List<ContextRelation>();
        }

        // allRelations 已按 Weight DESC, Confidence DESC, CreatedAt DESC 排序，分桶时保持顺序
        foreach (var relation in allRelations)
        {
            var sourceIsSeed = seedSet.Contains(relation.SourceId);
            var targetIsSeed = seedSet.Contains(relation.TargetId);
            switch (query.Direction)
            {
                case RelationDirection.Outgoing:
                    if (sourceIsSeed) { buckets[relation.SourceId].Add(relation); }
                    break;
                case RelationDirection.Incoming:
                    if (targetIsSeed) { buckets[relation.TargetId].Add(relation); }
                    break;
                default:
                    if (sourceIsSeed) { buckets[relation.SourceId].Add(relation); }
                    if (targetIsSeed
                        && !string.Equals(relation.SourceId, relation.TargetId, StringComparison.OrdinalIgnoreCase))
                    {
                        buckets[relation.TargetId].Add(relation);
                    }
                    break;
            }
        }

        var results = new List<RelationNeighborBatchResult>(seeds.Count);
        foreach (var seed in seeds)
        {
            var bucket = buckets[seed];
            // P1-4：per-seed MaxScan 截断 + SQL 全局 LIMIT 截断（保守）
            var truncated = bucket.Count > maxScan || (sqlGloballyTruncated && bucket.Count > 0);
            // 桶内已排序，直接 Take(MaxScan) + Skip + Take
            var seedRelations = bucket
                .Take(maxScan)
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
}
