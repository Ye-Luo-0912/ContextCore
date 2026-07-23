using System.Text;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// R28-E：PostgreSQL Utility Ledger Store 持久化实现。
/// </summary>
/// <remarks>
/// 设计原则（对齐 R21-2 契约澄清 #4）：
///   1. 公共 API 是 read-only（QueryAsync / GetLatestEntryAsync / GetExpertContributionsAsync），
///      与 <see cref="ContextCore.Core.Services.MemoryEvolution.InMemoryUtilityLedgerStore"/> 行为一致。
///   2. 写入由 internal <see cref="BulkInsertAsync"/> 暴露，仅供 UtilityLedgerMaterializer 调用
///      （生产环境的 materializer 适配器通过此方法批量物化 ledger 条目）。
///   3. 表 <c>utility_ledger_entries</c> 反规范化 workspace_id / collection_id / candidate_item_id /
///      expert / decision_id / materialized_at 等字段以便索引查询；完整 <see cref="UtilityLedgerEntry"/>
///      对象保存在 <c>data jsonb</c>，由 store 反序列化。
///   4. QueryAsync 按 materialized_at DESC 排序（与 InMemory 实现语义一致）。
///   5. GetExpertContributionsAsync 在数据库侧 GROUP BY expert 求平均（避免拉全量行到应用侧）。
/// </remarks>
public sealed class PostgresUtilityLedgerStore : PostgresStoreBase, IUtilityLedgerStore
{
    public PostgresUtilityLedgerStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UtilityLedgerEntry>> QueryAsync(
        UtilityLedgerQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        // 动态拼接 WHERE 子句：workspace_id 必填，其余可选过滤对齐 UtilityLedgerQuery 字段。
        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (query.CollectionId is not null)
        {
            where.Append(" AND collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }
        if (query.CandidateItemId is not null)
        {
            where.Append(" AND candidate_item_id = @candidate_item_id");
            command.Parameters.AddWithValue("candidate_item_id", query.CandidateItemId);
        }
        if (query.Expert is not null)
        {
            where.Append(" AND expert = @expert");
            command.Parameters.AddWithValue("expert", query.Expert.Value.ToString());
        }
        if (query.DecisionId is not null)
        {
            where.Append(" AND decision_id = @decision_id");
            command.Parameters.AddWithValue("decision_id", query.DecisionId);
        }
        if (query.IsSelected is not null)
        {
            where.Append(" AND is_selected = @is_selected");
            command.Parameters.AddWithValue("is_selected", query.IsSelected.Value);
        }
        if (query.Since is not null)
        {
            where.Append(" AND materialized_at >= @since");
            command.Parameters.AddWithValue("since", query.Since.Value);
        }
        if (query.Until is not null)
        {
            where.Append(" AND materialized_at <= @until");
            command.Parameters.AddWithValue("until", query.Until.Value);
        }

        // Take=0 表示不限制；与 InMemory 实现一致。
        var limitClause = query.Take > 0 ? "LIMIT @take" : string.Empty;
        if (query.Take > 0)
        {
            command.Parameters.AddWithValue("take", query.Take);
        }

        command.CommandText = $"""
SELECT data
FROM {Table("utility_ledger_entries")}
{where}
ORDER BY materialized_at DESC
{limitClause};
""";

        return await ExecuteReaderJsonAsync<UtilityLedgerEntry>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UtilityLedgerEntry?> GetLatestEntryAsync(
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
        command.CommandText = $"""
SELECT data
FROM {Table("utility_ledger_entries")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND candidate_item_id = @candidate_item_id
ORDER BY materialized_at DESC
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("candidate_item_id", candidateItemId);

        return await ExecuteScalarJsonAsync<UtilityLedgerEntry>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<RetrievalExpert, double>> GetExpertContributionsAsync(
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
        // 数据库侧按 expert 分组求平均贡献，避免拉全量行到应用侧。
        command.CommandText = $"""
SELECT expert, AVG(utility_contribution)
FROM {Table("utility_ledger_entries")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND candidate_item_id = @candidate_item_id
GROUP BY expert;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("candidate_item_id", candidateItemId);

        var contributions = new Dictionary<RetrievalExpert, double>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var expertString = reader.GetString(0);
            // 与 Serializer 的 JsonStringEnumConverter 一致：expert 列存 enum 字符串名。
            if (Enum.TryParse<RetrievalExpert>(expertString, ignoreCase: false, out var expert))
            {
                // AVG 返回 numeric；Convert.ToDouble 处理 DBNull（理论上 GROUP BY 不会产生 DBNull）。
                contributions[expert] = Convert.ToDouble(reader.GetDouble(1));
            }
        }

        return contributions;
    }

    /// <summary>
    /// 内部批量写入方法（仅供 UtilityLedgerMaterializer 调用）。
    /// 幂等：同 entry_id 重复写入时覆盖（ON CONFLICT DO UPDATE），保持最新快照。
    /// </summary>
    /// <remarks>
    /// router_id / materialization_batch_id 在 DDL 中为 NOT NULL，但 record 字段可空；
    /// 此处将 null 规范化为空字符串写入索引列（data jsonb 仍保留真实 null 值，读路径不受影响）。
    /// </remarks>
    internal async Task BulkInsertAsync(
        IReadOnlyList<UtilityLedgerEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(entry);

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = Options.CommandTimeoutSeconds;
                command.CommandText = $"""
INSERT INTO {Table("utility_ledger_entries")} (
    entry_id, workspace_id, collection_id, candidate_item_id, expert,
    utility_contribution, deterministic_score, model_score, final_score,
    is_selected, drop_reason_code, decision_id, policy_version, router_id,
    materialized_at, materialization_batch_id, data)
VALUES (
    @entry_id, @workspace_id, @collection_id, @candidate_item_id, @expert,
    @utility_contribution, @deterministic_score, @model_score, @final_score,
    @is_selected, @drop_reason_code, @decision_id, @policy_version, @router_id,
    @materialized_at, @materialization_batch_id, @data)
ON CONFLICT (entry_id) DO UPDATE SET
    workspace_id = EXCLUDED.workspace_id,
    collection_id = EXCLUDED.collection_id,
    candidate_item_id = EXCLUDED.candidate_item_id,
    expert = EXCLUDED.expert,
    utility_contribution = EXCLUDED.utility_contribution,
    deterministic_score = EXCLUDED.deterministic_score,
    model_score = EXCLUDED.model_score,
    final_score = EXCLUDED.final_score,
    is_selected = EXCLUDED.is_selected,
    drop_reason_code = EXCLUDED.drop_reason_code,
    decision_id = EXCLUDED.decision_id,
    policy_version = EXCLUDED.policy_version,
    router_id = EXCLUDED.router_id,
    materialized_at = EXCLUDED.materialized_at,
    materialization_batch_id = EXCLUDED.materialization_batch_id,
    data = EXCLUDED.data;
""";
                command.Parameters.AddWithValue("entry_id", entry.EntryId);
                command.Parameters.AddWithValue("workspace_id", entry.WorkspaceId);
                command.Parameters.AddWithValue("collection_id", entry.CollectionId);
                command.Parameters.AddWithValue("candidate_item_id", entry.CandidateItemId);
                command.Parameters.AddWithValue("expert", entry.Expert.ToString());
                command.Parameters.AddWithValue("utility_contribution", entry.UtilityContribution);
                command.Parameters.AddWithValue("deterministic_score", entry.DeterministicScore);
                command.Parameters.AddWithValue("model_score", (object?)entry.ModelScore ?? DBNull.Value);
                command.Parameters.AddWithValue("final_score", entry.FinalScore);
                command.Parameters.AddWithValue("is_selected", entry.IsSelected);
                command.Parameters.AddWithValue("drop_reason_code", (object?)entry.DropReasonCode ?? DBNull.Value);
                command.Parameters.AddWithValue("decision_id", entry.DecisionId);
                command.Parameters.AddWithValue("policy_version", entry.PolicyVersion);
                // router_id / materialization_batch_id DDL 为 NOT NULL；null 规范化为空字符串。
                command.Parameters.AddWithValue("router_id", entry.RouterId ?? string.Empty);
                command.Parameters.AddWithValue("materialized_at", entry.MaterializedAt);
                command.Parameters.AddWithValue("materialization_batch_id", entry.MaterializationBatchId ?? string.Empty);
                AddJson(command, "data", entry);
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
