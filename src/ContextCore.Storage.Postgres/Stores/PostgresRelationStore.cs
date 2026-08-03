using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using System.Runtime.CompilerServices;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 关系存储，使用结构化列加 jsonb 原文保存关系边。</summary>
public sealed class PostgresRelationStore : PostgresStoreBase, IRelationStore, ITransactionalRelationStore, IRelationStreamStore, IRelationHydrationStore
{
    public PostgresRelationStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <summary>SaveAsync 委托 BatchUpsertAsync，保留为单条便利方法。</summary>
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
    /// 在指定事务作用域内删除单条边。复用 scope 持有的连接与事务，不开启新连接。
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
    /// 批量 upsert 改用 NpgsqlBatch，单次往返提交所有语句；单事务保证原子性。
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
    /// 在指定事务作用域内批量 upsert 关系。复用 scope 持有的连接与事务。
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
        // NpgsqlBatch 单次往返提交全部 upsert 语句
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
    /// 在指定事务作用域内查询关系。读共享同一事务视图，避免读到其他事务未提交的数据。
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

    /// <summary>统一邻居查询，在 SQL 中过滤和 Limit。</summary>
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
            // 使用真实列 relation_type 而非 JSON 提取（既有索引，且避免大小写敏感问题）
            filters.Add("relation_type = @relation_type");
            command.Parameters.AddWithValue("relation_type", query.RelationType);
        }

        // 多类型过滤优先于单类型，在 LIMIT 前下推到 SQL
        if (query.AllowedRelationTypes.Count > 0)
        {
            var paramNames = new List<string>();
            for (var i = 0; i < query.AllowedRelationTypes.Count; i++)
            {
                var paramName = $"allowed_rt_{i}";
                paramNames.Add($"@{paramName}");
                command.Parameters.AddWithValue(paramName, query.AllowedRelationTypes[i]);
            }
            // 使用真实列 relation_type
            filters.Add($"relation_type IN ({string.Join(", ", paramNames)})");
        }

        if (query.MinConfidence > 0)
        {
            // 修复 cast 优先级 bug。
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

        // 修复 3 个 SQL bug：
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
    /// 批量邻居查询。使用 CROSS JOIN LATERAL 实现 per-seed TopN，
    /// 每个种子独立扫描 + 排序 + LIMIT，命中 (workspace_id, source_id) / (workspace_id, target_id) 索引。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>per-seed LATERAL 替代旧 ANY(@item_ids) 全局扫描方案，命中单侧索引。</item>
    /// <item>全局预算按两阶段分配：阶段 1 每种子保证最低配额 floor(GlobalEdgeLimit/种子数)（LATERAL LIMIT 配额窗口）；
    /// 阶段 2 余额按种子序再分配（LATERAL OFFSET 配额 + LIMIT 窗口余量）。外层全局 LIMIT 兜底，
    /// 数据库只排序/生成 ≤ GlobalEdgeLimit 行结构列，同时早期富种子无法耗尽预算饿死后继种子。</item>
    /// <item>Seed 分批执行：每批 <c>SeedBatchSize</c> 个种子，避免单次 LATERAL unnest 过大。</item>
    /// <item>只返回结构列（id/source/target/relation_type/weight/confidence/created_at）
    /// 加廉价 JSON 提取（lifecycle/review_status/source_node_kind/target_node_kind），不反序列化完整 Relation JSON。
    /// 完整 Metadata 由 <see cref="HydrateRelationsAsync"/> 在客户端 Selected 后批量补全。</item>
    /// <item>Both 方向：(source_id = seed.id OR target_id = seed.id)。自环边只返回一次。</item>
    /// <item>结果按 <see cref="RelationNeighborBatchResult.SeedOrdinal"/> 升序返回，每个种子都有一条结果；
    /// 携带 ScannedCount / CandidateCountBeforeGlobalLimit / SkippedByGlobalBudget 诊断。</item>
    /// <item>truncated 信号：阶段 1/2 的 per-seed LIMIT 命中（候选 ≥ 配额/窗口）或全局预算截断时保守标记。</item>
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

        // 全局硬上限 — 种子数超出 GraphQueryLimits.MaxSeeds 直接截断（保留原序）。
        if (seeds.Count > GraphQueryLimits.MaxSeeds)
        {
            seeds.RemoveRange(GraphQueryLimits.MaxSeeds, seeds.Count - GraphQueryLimits.MaxSeeds);
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // per-seed 扫描上限 = MaxScan（不超过 MaxTotalEdges）。
        var maxScan = Math.Min(query.MaxScan > 0 ? query.MaxScan : 1000, GraphQueryLimits.MaxTotalEdges);
        // per-seed 返回边数不得超过 MaxEdgesPerSeed 硬上限。
        var effectiveTake = Math.Min(query.Take > 0 ? query.Take : 100, GraphQueryLimits.MaxEdgesPerSeed);
        var effectiveSkip = query.Skip > 0 ? query.Skip : 0;

        // 全局边数上限 = 查询声明的 GlobalEdgeLimit（clamp 到 [1, MaxTotalEdges]）。
        // 默认即 MaxTotalEdges；调用方（BFS 引擎 / Provider）可传入自身剩余预算，把上限精确下推到 SQL。
        var globalEdgeLimit = query.GlobalEdgeLimit > 0
            ? Math.Min(query.GlobalEdgeLimit, GraphQueryLimits.MaxTotalEdges)
            : GraphQueryLimits.MaxTotalEdges;

        // 每种子最低配额 = floor(GlobalEdgeLimit / 种子数)，至少 1。
        // 阶段 1 保证每个种子扫描到配额内的候选（per-seed LATERAL LIMIT = phase1Cap）；
        // 阶段 2 把余额按种子序再分配（每种子至多补到 maxScan 窗口）。
        // 由于 seeds * floor ≤ global，正常情形下外层全局 LIMIT 永不提前截断，
        // 早期富种子无法耗尽全部预算 → 后续种子不再饿死。
        var seedCount = seeds.Count;
        var perSeedFloor = Math.Max(1, globalEdgeLimit / seedCount);
        var phase1Cap = Math.Min(maxScan, perSeedFloor);
        var phase2Extra = Math.Max(0, maxScan - phase1Cap);

        // 读取 (seed_id, 结构列) 对，按 seed_id 分桶。LATERAL 内已排序，分桶保持顺序。
        var phase1Buckets = new Dictionary<string, List<ContextRelation>>(seeds.Count, StringComparer.OrdinalIgnoreCase);
        var phase2Buckets = new Dictionary<string, List<ContextRelation>>(seeds.Count, StringComparer.OrdinalIgnoreCase);
        var ordinalOfSeed = new Dictionary<string, int>(seeds.Count, StringComparer.OrdinalIgnoreCase);
        for (var ordinal = 0; ordinal < seeds.Count; ordinal++)
        {
            phase1Buckets[seeds[ordinal]] = new List<ContextRelation>();
            phase2Buckets[seeds[ordinal]] = new List<ContextRelation>();
            ordinalOfSeed[seeds[ordinal]] = ordinal;
        }

        // 全局硬上限 — 总读取边数达到 GlobalEdgeLimit 即停止读取，截断信号传播到 cutoff 种子。
        var totalRead = 0;
        var globalCapHit = false;
        // 阶段 1 / 阶段 2 各自最后产出行的种子序号（-1 表示未产出）。
        // 用于区分"被全局预算跳过"与"已扫描但为空"：序号 > 阶段 1 最后产出序号的种子从未被扫描。
        var phase1LastReadSeedOrdinal = -1;

        // 构建 LATERAL 过滤条件与参数（阶段 1/2 复用）。
        // 过滤条件全部下推到 LATERAL 内，每个种子独立使用 (workspace_id, source_id) / (workspace_id, target_id) 索引。
        var (filterSql, filterParams) = BuildFilters();

        // Seed 分批执行，避免单次 LATERAL unnest(@item_ids) 过大。
        // 全局 LIMIT @global_limit 下推到 SQL：每批 = 全局上限（GlobalEdgeLimit）- totalRead 的剩余预算，
        // 数据库只需为每批排序/生成 ≤ remainingBudget 行结构列。
        const int SeedBatchSize = 10;

        // 阶段 1：每种子最低配额。per-seed LATERAL LIMIT = phase1Cap（配额窗口），
        // 外层 LIMIT 兜底（仅 GlobalEdgeLimit < 种子数 时会命中，此时后续种子标记 SkippedByGlobalBudget）。
        for (var batchStart = 0; batchStart < seeds.Count && totalRead < globalEdgeLimit; batchStart += SeedBatchSize)
        {
            var batchSize = Math.Min(SeedBatchSize, seeds.Count - batchStart);
            var batchSeeds = seeds.Skip(batchStart).Take(batchSize).ToArray();
            var remainingBudget = globalEdgeLimit - totalRead;
            var lastRead = await ReadBatchAsync(batchSeeds, remainingBudget, phase1Buckets, "LIMIT @per_seed_limit", phase1Cap, 0).ConfigureAwait(false);
            if (lastRead > phase1LastReadSeedOrdinal)
            {
                phase1LastReadSeedOrdinal = lastRead;
            }

            if (totalRead >= globalEdgeLimit)
            {
                globalCapHit = true;
                break;
            }
        }

        // 阶段 2：余额按种子序再分配（每种子至多补到 maxScan 窗口）。
        // per-seed LATERAL OFFSET @per_seed_offset 从阶段 1 已交付的配额行之后继续，
        // 外层 LIMIT @global_limit 按种子序截断 → 与 InMemory/FileSystem 的按序再分配语义一致。
        var phase2LastReadSeedOrdinal = -1;
        if (phase2Extra > 0 && totalRead < globalEdgeLimit)
        {
            for (var batchStart = 0; batchStart < seeds.Count && totalRead < globalEdgeLimit; batchStart += SeedBatchSize)
            {
                var batchSize = Math.Min(SeedBatchSize, seeds.Count - batchStart);
                var batchSeeds = seeds.Skip(batchStart).Take(batchSize).ToArray();
                var remainingBudget = globalEdgeLimit - totalRead;
                var lastRead = await ReadBatchAsync(batchSeeds, remainingBudget, phase2Buckets, "OFFSET @per_seed_offset LIMIT @per_seed_limit", phase2Extra, phase1Cap).ConfigureAwait(false);
                if (lastRead > phase2LastReadSeedOrdinal)
                {
                    phase2LastReadSeedOrdinal = lastRead;
                }

                if (totalRead >= globalEdgeLimit)
                {
                    globalCapHit = true;
                    break;
                }
            }
        }

        var results = new List<RelationNeighborBatchResult>(seeds.Count);
        for (var ordinal = 0; ordinal < seeds.Count; ordinal++)
        {
            var seed = seeds[ordinal];
            var p1 = phase1Buckets[seed];
            var p2 = phase2Buckets[seed];
            var phase1Rows = p1.Count;
            var phase2Rows = p2.Count;
            var delivered = phase1Rows + phase2Rows;

            // 跳过信号：全局预算在阶段 1 扫描到该种子之前即已耗尽（仅 GlobalEdgeLimit < 种子数 时出现）。
            // 已扫描过的种子（序号 ≤ 阶段 1 最后产出序号）即使 0 条也视为"空种子"而非被跳过，
            // 与 InMemory/FileSystem 的 skippedStart 语义对齐。
            var skipped = globalCapHit && delivered == 0 && ordinal > phase1LastReadSeedOrdinal;

            // 阶段 2 是否扫描过该种子（阶段 2 最后产出序号 ≥ 该种子序号）。
            var phase2Scanned = phase2LastReadSeedOrdinal >= 0 && ordinal <= phase2LastReadSeedOrdinal;
            // 全局预算恰好在该种子的阶段 2 交付过程中耗尽（外层 LIMIT 命中，余量未读完）。
            var phase2CutHere = globalCapHit && ordinal == phase2LastReadSeedOrdinal;

            // 完整交付判定：阶段 1 未达到配额上限（窗口已尽），
            // 或阶段 2 扫描过该种子且余量未用尽且未被全局预算截断。
            // 其余情形保守标记 truncated——Postgres 无法区分 bucket == limit 与 bucket > limit。
            var complete = phase1Rows < phase1Cap
                || (phase2Extra > 0 && phase2Scanned && phase2Rows < phase2Extra && !phase2CutHere);

            var truncated = !complete || skipped;

            // 桶内已排序（LATERAL 内 ORDER BY），阶段 2 紧随阶段 1 之后，合并后直接 Skip + Take 完成分页。
            var combined = new List<ContextRelation>(delivered);
            combined.AddRange(p1);
            combined.AddRange(p2);
            var seedRelations = combined
                .Skip(effectiveSkip)
                .Take(effectiveTake)
                .ToArray();

            results.Add(new RelationNeighborBatchResult
            {
                ItemId = seed,
                SeedOrdinal = ordinal,
                Relations = seedRelations,
                Truncated = truncated,
                SkippedByGlobalBudget = skipped,
                ScannedCount = delivered,
                CandidateCountBeforeGlobalLimit = skipped ? 0 : delivered
            });
        }

        return results;

        // 构建 LATERAL WHERE 过滤条件与参数列表（阶段 1/2 复用同一组过滤语义）。
        (List<string> Filters, List<(string Name, object? Value)> Params) BuildFilters()
        {
            var filters = new List<string> { "workspace_id = @workspace_id" };
            var parameters = new List<(string, object?)> { ("workspace_id", query.WorkspaceId) };

            if (!string.IsNullOrWhiteSpace(query.CollectionId))
            {
                filters.Add("collection_id = @collection_id");
                parameters.Add(("collection_id", query.CollectionId));
            }

            // 方向过滤改为与 seed.id 的等值比较（替代 ANY(@item_ids)），让 LATERAL 走索引。
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
                parameters.Add(("relation_type", query.RelationType));
            }

            if (query.AllowedRelationTypes.Count > 0)
            {
                var paramNames = new List<string>();
                for (var i = 0; i < query.AllowedRelationTypes.Count; i++)
                {
                    var paramName = $"allowed_rt_{i}";
                    paramNames.Add($"@{paramName}");
                    parameters.Add((paramName, query.AllowedRelationTypes[i]));
                }
                filters.Add($"relation_type IN ({string.Join(", ", paramNames)})");
            }

            if (query.MinConfidence > 0)
            {
                filters.Add("confidence >= @min_confidence");
                parameters.Add(("min_confidence", query.MinConfidence));
            }

            if (query.ExcludedLifecycles.Count > 0)
            {
                var paramNames = new List<string>();
                for (var i = 0; i < query.ExcludedLifecycles.Count; i++)
                {
                    var paramName = $"ex_lc_{i}";
                    paramNames.Add($"@{paramName}");
                    parameters.Add((paramName, query.ExcludedLifecycles[i]));
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
                    parameters.Add((paramName, query.ExcludedReviewStatuses[i]));
                }
                filters.Add($"(data ->> 'ReviewStatus' IS NULL OR data ->> 'ReviewStatus' NOT IN ({string.Join(", ", paramNames)}))");
            }

            return (filters, parameters);
        }

        // 执行一批种子的邻居读取：LATERAL 尾（LIMIT / OFFSET+LIMIT）+ 外层全局 LIMIT，
        // 行按种子序产出，累积到 targetBuckets 并维护全局读取计数；返回本批最后产出行的种子序号（无产出为 -1）。
        async Task<int> ReadBatchAsync(
            IReadOnlyList<string> batchSeeds,
            int remainingBudget,
            Dictionary<string, List<ContextRelation>> targetBuckets,
            string lateralTail,
            int perSeedLimit,
            int perSeedOffset)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Options.CommandTimeoutSeconds;

            foreach (var (name, value) in filterParams)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
            command.Parameters.AddWithValue("per_seed_limit", perSeedLimit);
            if (perSeedOffset > 0)
            {
                command.Parameters.AddWithValue("per_seed_offset", perSeedOffset);
            }
            command.Parameters.AddWithValue("global_limit", remainingBudget);
            command.Parameters.AddWithValue("item_ids", batchSeeds);

            // SELECT 只返回结构列 + 廉价 JSON 提取，不返回完整 data jsonb。
            // 外层 LIMIT @global_limit 把全局上限下推到 SQL，DB 不再为剩余候选生成/序列化行。
            command.CommandText = $"""
SELECT seed.id AS seed_id,
       r.collection_id,
       r.id,
       r.source_id,
       r.target_id,
       r.relation_type,
       r.weight,
       r.confidence,
       r.created_at,
       r.data ->> 'Lifecycle' AS lifecycle,
       r.data ->> 'ReviewStatus' AS review_status,
       r.data ->> 'SourceNodeKind' AS source_node_kind,
       r.data ->> 'TargetNodeKind' AS target_node_kind
FROM unnest(@item_ids) AS seed(id)
CROSS JOIN LATERAL (
    SELECT id, collection_id, source_id, target_id, relation_type, weight, confidence, created_at, data
    FROM {Table("relations")}
    WHERE {string.Join(" AND ", filterSql)}
    ORDER BY weight DESC, confidence DESC, created_at DESC
    {lateralTail}
) r
LIMIT @global_limit;
""";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var batchLastReadOrdinal = -1;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (totalRead >= globalEdgeLimit)
                {
                    globalCapHit = true;
                    break;
                }

                var seedId = reader.GetString(0);
                var relation = ReadStructuralRelation(reader, query.WorkspaceId);
                if (relation is null) continue;

                if (targetBuckets.TryGetValue(seedId, out var bucket))
                {
                    bucket.Add(relation);
                    totalRead++;
                    batchLastReadOrdinal = ordinalOfSeed[seedId];
                }
            }
            return batchLastReadOrdinal;
        }
    }

    /// <summary>
    /// 从结构列 + 廉价 JSON 提取构造 <see cref="ContextRelation"/>，不反序列化完整 data jsonb。
    /// Metadata/SourceRefs/Provenance/UpdatedAt 留空——由 <see cref="HydrateRelationsAsync"/> 在 Selected 后补全。
    /// 列序：0=seed_id, 1=collection_id, 2=id, 3=source_id, 4=target_id, 5=relation_type,
    /// 6=weight, 7=confidence, 8=created_at, 9=lifecycle, 10=review_status, 11=source_node_kind, 12=target_node_kind。
    /// </summary>
    private static ContextRelation ReadStructuralRelation(NpgsqlDataReader reader, string workspaceId)
    {
        return new ContextRelation
        {
            Id = reader.GetString(2),
            WorkspaceId = workspaceId,
            CollectionId = reader.GetString(1),
            SourceId = reader.GetString(3),
            TargetId = reader.GetString(4),
            RelationType = reader.GetString(5),
            Weight = reader.GetDouble(6),
            Confidence = reader.GetDouble(7),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
            Lifecycle = reader.IsDBNull(9) ? RelationLifecycles.Active : (reader.GetString(9) ?? RelationLifecycles.Active),
            ReviewStatus = reader.IsDBNull(10) ? string.Empty : (reader.GetString(10) ?? string.Empty),
            SourceNodeKind = reader.IsDBNull(11) ? string.Empty : (reader.GetString(11) ?? string.Empty),
            TargetNodeKind = reader.IsDBNull(12) ? string.Empty : (reader.GetString(12) ?? string.Empty)
            // Metadata / SourceRefs / Provenance / UpdatedAt 留空：在线主链不使用这些字段。
        };
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
    /// 流式枚举关系，使用 NpgsqlDataReader.ReadAsync 逐行读取，避免一次性将全部结果缓冲到 List。
    /// 不应用调用方提供的 Skip/Take——返回完整候选集，由消费方按需裁剪。
    /// 排序与 QueryAsync 一致（weight/confidence/createdAt desc）。
    /// 禁止无界扫描——SQL 强制 LIMIT @maxTotalEdges（= <see cref="GraphQueryLimits.MaxTotalEdges"/>），
    /// 防止病态全表把整张图拉入内存。在线主链不得调用本方法做候选枚举。
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

        // 构造流式 SQL：与 ApplyQueryCommandText 一致的过滤条件，但不应用调用方 Skip/Take。
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

        // 强制全局上限——未提供 LIMIT 时使用 GraphQueryLimits.MaxTotalEdges 默认上限，
        // 防止无界扫描把整张关系图拉入内存。
        command.Parameters.AddWithValue("max_total_edges", GraphQueryLimits.MaxTotalEdges);

        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE {string.Join(" AND ", filters)}
ORDER BY weight DESC, confidence DESC, created_at DESC
LIMIT @max_total_edges;
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

    /// <summary>
    /// 按关系 ID 批量 hydrate 完整 Relation Metadata（data jsonb 反序列化）。
    /// 供客户端 Selected 特定 edges 后补全 Metadata/SourceRefs/Provenance 等字段。
    /// 在线主链的 <see cref="QueryNeighborsBatchAsync"/> 只返回结构列，不反序列化完整 JSON。
    /// </summary>
    public async Task<IReadOnlyList<ContextRelation>> HydrateRelationsAsync(
        string workspaceId,
        string? collectionId,
        IReadOnlyList<string> relationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(relationIds);
        if (relationIds.Count == 0)
        {
            return Array.Empty<ContextRelation>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var filters = new List<string> { "workspace_id = @workspace_id", "id = ANY(@relation_ids)" };
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("relation_ids", relationIds.ToArray());
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            filters.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", collectionId);
        }

        // hydration 路径命中主键索引 (workspace_id, collection_id, id)；无 collection_id 时
        // 走 (workspace_id, id) 的 ANY 展开 + 索引扫描。结果集大小由调用方控制（已 Selected 的 edges）。
        command.CommandText = $"""
SELECT data
FROM {Table("relations")}
WHERE {string.Join(" AND ", filters)};
""";

        return await ReadRelationsAsync(command, cancellationToken).ConfigureAwait(false);
    }
}
