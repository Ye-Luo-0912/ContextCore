using System.Text;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// R28-E / R29 WP-E-1：PostgreSQL ConflictSet Store 持久化实现。
/// </summary>
/// <remarks>
/// 设计原则（对齐 R21-2 契约澄清 #4 + R29 学习闭环）：
///   1. 实现 <see cref="IConflictSetLedger"/>：读 API（QueryAsync / GetAsync /
///      GetConflictsForCandidateAsync）+ 异步批量写 API（<see cref="AppendConflictSetsAsync"/>）。
///   2. 写入由 <c>UtilityLedgerMaterializer</c> 通过 <see cref="AppendConflictSetsAsync"/> 调用
///      （生产路径）；与 <see cref="ContextCore.Core.Services.MemoryEvolution.InMemoryConflictSetStore"/>
///      实现同一 <see cref="IConflictSetLedger"/> 契约，materializer 无需感知存储后端。
///   3. 表 <c>conflict_sets</c> 反规范化 workspace_id / collection_id / kind / decision_id /
///      resolution_status / created_at 字段以便索引查询；完整 <see cref="ConflictSet"/> 对象保存在
///      <c>data jsonb</c>，由 store 反序列化。
///   4. <see cref="GetConflictsForCandidateAsync"/> / 按 CandidateItemId 过滤的 QueryAsync 使用
///      jsonb 包含查询 <c>data-&gt;'Entries' @&gt; '[{"CandidateItemId":"..."}]'</c>（PascalCase 对齐
///      PostgresJsonSerializer 默认序列化），由 GIN 索引加速。
///   5. QueryAsync 按 created_at DESC 排序（created_at 列对应 record 的 MaterializedAt，与 InMemory 语义一致）。
/// </remarks>
public sealed class PostgresConflictSetStore : PostgresStoreBase, IConflictSetLedger
{
    public PostgresConflictSetStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConflictSet>> QueryAsync(
        ConflictSetQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        // 动态拼接 WHERE 子句：workspace_id 必填，其余可选过滤对齐 ConflictSetQuery 字段。
        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (query.CollectionId is not null)
        {
            where.Append(" AND collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }
        if (query.Kind is not null)
        {
            where.Append(" AND kind = @kind");
            command.Parameters.AddWithValue("kind", query.Kind.Value.ToString());
        }
        if (query.DecisionId is not null)
        {
            where.Append(" AND decision_id = @decision_id");
            command.Parameters.AddWithValue("decision_id", query.DecisionId);
        }
        if (query.ResolutionStatus is not null)
        {
            where.Append(" AND resolution_status = @resolution_status");
            command.Parameters.AddWithValue("resolution_status", query.ResolutionStatus.Value.ToString());
        }
        if (query.Since is not null)
        {
            where.Append(" AND created_at >= @since");
            command.Parameters.AddWithValue("since", query.Since.Value);
        }
        if (query.Until is not null)
        {
            where.Append(" AND created_at <= @until");
            command.Parameters.AddWithValue("until", query.Until.Value);
        }
        if (query.CandidateItemId is not null)
        {
            // jsonb 包含查询：Entries 数组中存在 CandidateItemId = @candidate 的条目。
            // 用 Serializer 序列化匿名对象作为 @> 操作数，避免字符串拼接注入风险。
            where.Append(" AND data->'Entries' @> @candidate_filter");
            var candidateFilter = command.Parameters.Add("candidate_filter", NpgsqlDbType.Jsonb);
            candidateFilter.Value = Serializer.Serialize(new[] { new { CandidateItemId = query.CandidateItemId } });
        }

        // Take=0 表示不限制；与 InMemory 实现一致。
        var limitClause = query.Take > 0 ? "LIMIT @take" : string.Empty;
        if (query.Take > 0)
        {
            command.Parameters.AddWithValue("take", query.Take);
        }

        command.CommandText = $"""
SELECT data
FROM {Table("conflict_sets")}
{where}
ORDER BY created_at DESC
{limitClause};
""";

        return await ExecuteReaderJsonAsync<ConflictSet>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConflictSet?> GetAsync(
        string workspaceId,
        string collectionId,
        string conflictSetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conflictSetId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("conflict_sets")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND conflict_set_id = @conflict_set_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("conflict_set_id", conflictSetId);

        return await ExecuteScalarJsonAsync<ConflictSet>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConflictSet>> GetConflictsForCandidateAsync(
        string workspaceId,
        string collectionId,
        string candidateItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateItemId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // jsonb 包含查询：Entries 数组中存在 CandidateItemId = @candidate 的条目，由 GIN 索引加速。
        command.CommandText = $"""
SELECT data
FROM {Table("conflict_sets")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND data->'Entries' @> @candidate_filter
ORDER BY created_at DESC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        var candidateFilter = command.Parameters.Add("candidate_filter", NpgsqlDbType.Jsonb);
        candidateFilter.Value = Serializer.Serialize(new[] { new { CandidateItemId = candidateItemId } });

        return await ExecuteReaderJsonAsync<ConflictSet>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量写入 ConflictSet（实现 <see cref="IConflictSetLedger.AppendConflictSetsAsync"/>）。
    /// 幂等：同 conflict_set_id 重复写入时覆盖（ON CONFLICT DO UPDATE），保持最新快照。
    /// </summary>
    /// <remarks>
    /// decision_id / resolved_item_id / chosen_authority / resolved_at / resolver /
    /// memory_state_event_id / relation_id 在 DDL 中为可空；record 字段同样可空，直接传 DBNull。
    /// created_at 列对应 record 的 MaterializedAt（排序键）。
    /// </remarks>
    public async Task AppendConflictSetsAsync(
        IReadOnlyList<ConflictSet> conflictSets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflictSets);
        if (conflictSets.Count == 0) return;

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var set in conflictSets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(set);

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = Options.CommandTimeoutSeconds;
                command.CommandText = $"""
INSERT INTO {Table("conflict_sets")} (
    conflict_set_id, workspace_id, collection_id, kind, decision_id,
    resolved_item_id, resolution_status, chosen_authority, resolved_at, resolver,
    memory_state_event_id, relation_id, created_at, data)
VALUES (
    @conflict_set_id, @workspace_id, @collection_id, @kind, @decision_id,
    @resolved_item_id, @resolution_status, @chosen_authority, @resolved_at, @resolver,
    @memory_state_event_id, @relation_id, @created_at, @data)
ON CONFLICT (conflict_set_id) DO UPDATE SET
    workspace_id = EXCLUDED.workspace_id,
    collection_id = EXCLUDED.collection_id,
    kind = EXCLUDED.kind,
    decision_id = EXCLUDED.decision_id,
    resolved_item_id = EXCLUDED.resolved_item_id,
    resolution_status = EXCLUDED.resolution_status,
    chosen_authority = EXCLUDED.chosen_authority,
    resolved_at = EXCLUDED.resolved_at,
    resolver = EXCLUDED.resolver,
    memory_state_event_id = EXCLUDED.memory_state_event_id,
    relation_id = EXCLUDED.relation_id,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
                command.Parameters.AddWithValue("conflict_set_id", set.ConflictSetId);
                command.Parameters.AddWithValue("workspace_id", set.WorkspaceId);
                command.Parameters.AddWithValue("collection_id", set.CollectionId);
                command.Parameters.AddWithValue("kind", set.Kind.ToString());
                command.Parameters.AddWithValue("decision_id", (object?)set.DecisionId ?? DBNull.Value);
                command.Parameters.AddWithValue("resolved_item_id", (object?)set.ResolvedItemId ?? DBNull.Value);
                command.Parameters.AddWithValue("resolution_status", set.ResolutionStatus.ToString());
                command.Parameters.AddWithValue("chosen_authority", (object?)set.ChosenAuthority ?? DBNull.Value);
                command.Parameters.AddWithValue("resolved_at", (object?)set.ResolvedAt ?? DBNull.Value);
                command.Parameters.AddWithValue("resolver", (object?)set.Resolver ?? DBNull.Value);
                command.Parameters.AddWithValue("memory_state_event_id", (object?)set.MemoryStateEventId ?? DBNull.Value);
                command.Parameters.AddWithValue("relation_id", (object?)set.RelationId ?? DBNull.Value);
                // created_at 列对应 record 的 MaterializedAt（排序键）。
                command.Parameters.AddWithValue("created_at", set.MaterializedAt);
                AddJson(command, "data", set);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* 不掩盖原始异常 */ }
            throw;
        }
    }
}
