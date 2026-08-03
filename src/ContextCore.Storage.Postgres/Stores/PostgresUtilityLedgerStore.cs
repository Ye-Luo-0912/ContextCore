using System.Text;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// / PostgreSQL Utility Ledger 持久化实现。
/// </summary>
/// <remarks>
/// 设计原则（对齐学习闭环）：
/// 1. 实现 <see cref="IUtilityLedger"/>：读 API（QueryAsync / GetLatestEntryAsync /
/// GetExpertContributionsAsync）+ 异步批量写 API（<see cref="AppendEntriesAsync"/>）。
/// 2. 写入由 <c>UtilityLedgerMaterializer</c> 通过 <see cref="AppendEntriesAsync"/> 调用
/// （生产路径）；与 <see cref="ContextCore.Core.Services.MemoryEvolution.InMemoryUtilityLedgerStore"/>
/// 实现同一 <see cref="IUtilityLedger"/> 契约，materializer 无需感知存储后端。
/// 3. 表 <c>utility_ledger_entries</c> 反规范化 workspace_id / collection_id / candidate_item_id /
/// expert / decision_id / materialized_at 等字段以便索引查询；完整 <see cref="UtilityLedgerEntry"/>
/// 对象保存在 <c>data jsonb</c>，由 store 反序列化。
/// 4. QueryAsync 按 materialized_at DESC 排序（与 InMemory 实现语义一致）。
/// 5. GetExpertContributionsAsync 在数据库侧 GROUP BY expert 求平均（避免拉全量行到应用侧）。
/// </remarks>
public sealed class PostgresUtilityLedgerStore : PostgresStoreBase, IUtilityLedger, ITransactionalUtilityLedger
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
    /// 批量写入 ledger 条目（实现 <see cref="IUtilityLedger.AppendEntriesAsync"/>）。
    /// 幂等：同 entry_id 重复写入时覆盖（ON CONFLICT DO UPDATE），保持最新快照。
    /// </summary>
    /// <remarks>
    /// <para>
    /// router_id / materialization_batch_id 在 DDL 中为 NOT NULL，但 record 字段可空；
    /// 此处将 null 规范化为空字符串写入索引列（data jsonb 仍保留真实 null 值，读路径不受影响）。
    /// </para>
    /// <para>
    /// 优化：使用 <c>unnest</c> 将整批条目在单条 SQL 内展开，一次往返完成全部 INSERT。
    /// 相比旧版 foreach 逐条 INSERT（N 次往返），1000 条 Ledger Entry 从 1000 次降为 1 次。
    /// ON CONFLICT (entry_id) DO UPDATE 保留幂等覆盖语义。
    /// </para>
    /// </remarks>
    public async Task AppendEntriesAsync(
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
            await AppendEntriesCoreAsync(entries, connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* 不掩盖原始异常 */ }
            throw;
        }
    }

    /// <summary>
    /// 事务作用域内批量写入 ledger 条目（实现 <see cref="ITransactionalUtilityLedger.AppendEntriesAsync"/>）。
    /// 复用 scope 持有的连接与事务，不开启新连接/事务——与 ConflictSet 写入共享同一事务，
    /// 保证 ledger + ConflictSet 原子提交（避免一边成功、一边失败）。
    /// </summary>
    public async Task AppendEntriesAsync(
        IReadOnlyList<UtilityLedgerEntry> entries,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(scope);
        if (entries.Count == 0) return;

        if (scope is not PostgresWriteTransactionScope pgScope)
        {
            throw new InvalidOperationException(
                "PostgresUtilityLedgerStore 仅支持 PostgresWriteTransactionScope；请通过 PostgresWriteTransactionScopeFactory 创建事务作用域。");
        }
        if (!scope.IsActive)
        {
            throw new InvalidOperationException("事务作用域已结束（Commit/Rollback），无法继续写入。");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await AppendEntriesCoreAsync(entries, pgScope.Connection, pgScope.Transaction, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>共享的 unnest 批量 INSERT/ON CONFLICT 逻辑，由无事务与事务重载复用。</summary>
    private async Task AppendEntriesCoreAsync(
        IReadOnlyList<UtilityLedgerEntry> entries,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = entries.Count;
        var entryIds = new string[count];
        var workspaceIds = new string[count];
        var collectionIds = new string[count];
        var candidateItemIds = new string[count];
        var experts = new string[count];
        var utilityContributions = new double[count];
        var deterministicScores = new double[count];
        var modelScores = new double?[count];
        var finalScores = new double[count];
        var isSelecteds = new bool[count];
        var dropReasonCodes = new string?[count];
        var decisionIds = new string[count];
        var policyVersions = new string[count];
        var routerIds = new string[count];
        var materializedAts = new DateTimeOffset[count];
        var materializationBatchIds = new string[count];
        var datas = new string[count];
        for (var i = 0; i < count; i++)
        {
            var entry = entries[i];
            ArgumentNullException.ThrowIfNull(entry);
            entryIds[i] = entry.EntryId;
            workspaceIds[i] = entry.WorkspaceId;
            collectionIds[i] = entry.CollectionId;
            candidateItemIds[i] = entry.CandidateItemId;
            experts[i] = entry.Expert.ToString();
            utilityContributions[i] = entry.UtilityContribution;
            deterministicScores[i] = entry.DeterministicScore;
            modelScores[i] = entry.ModelScore;
            finalScores[i] = entry.FinalScore;
            isSelecteds[i] = entry.IsSelected;
            dropReasonCodes[i] = entry.DropReasonCode;
            decisionIds[i] = entry.DecisionId;
            policyVersions[i] = entry.PolicyVersion;
            // router_id / materialization_batch_id DDL 为 NOT NULL；null 规范化为空字符串。
            routerIds[i] = entry.RouterId ?? string.Empty;
            materializedAts[i] = entry.MaterializedAt;
            materializationBatchIds[i] = entry.MaterializationBatchId ?? string.Empty;
            datas[i] = Serializer.Serialize(entry);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("utility_ledger_entries")} (
    entry_id, workspace_id, collection_id, candidate_item_id, expert,
    utility_contribution, deterministic_score, model_score, final_score,
    is_selected, drop_reason_code, decision_id, policy_version, router_id,
    materialized_at, materialization_batch_id, data)
SELECT
    entry_id, workspace_id, collection_id, candidate_item_id, expert,
    utility_contribution, deterministic_score, model_score, final_score,
    is_selected, drop_reason_code, decision_id, policy_version, router_id,
    materialized_at, materialization_batch_id, data::jsonb
FROM unnest(
    @entry_ids::text[],
    @workspace_ids::text[],
    @collection_ids::text[],
    @candidate_item_ids::text[],
    @experts::text[],
    @utility_contributions::double precision[],
    @deterministic_scores::double precision[],
    @model_scores::double precision[],
    @final_scores::double precision[],
    @is_selecteds::boolean[],
    @drop_reason_codes::text[],
    @decision_ids::text[],
    @policy_versions::text[],
    @router_ids::text[],
    @materialized_ats::timestamptz[],
    @materialization_batch_ids::text[],
    @datas::jsonb[]
) AS t(entry_id, workspace_id, collection_id, candidate_item_id, expert,
    utility_contribution, deterministic_score, model_score, final_score,
    is_selected, drop_reason_code, decision_id, policy_version, router_id,
    materialized_at, materialization_batch_id, data)
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
        AddTextArray(command, "entry_ids", entryIds);
        AddTextArray(command, "workspace_ids", workspaceIds);
        AddTextArray(command, "collection_ids", collectionIds);
        AddTextArray(command, "candidate_item_ids", candidateItemIds);
        AddTextArray(command, "experts", experts);
        AddArrayParameter(command, "utility_contributions", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double, utilityContributions);
        AddArrayParameter(command, "deterministic_scores", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double, deterministicScores);
        AddNullableDoubleArray(command, "model_scores", modelScores);
        AddArrayParameter(command, "final_scores", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double, finalScores);
        AddArrayParameter(command, "is_selecteds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Boolean, isSelecteds);
        AddNullableTextArray(command, "drop_reason_codes", dropReasonCodes);
        AddTextArray(command, "decision_ids", decisionIds);
        AddTextArray(command, "policy_versions", policyVersions);
        AddTextArray(command, "router_ids", routerIds);
        AddArrayParameter(command, "materialized_ats", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.TimestampTz, materializedAts);
        AddTextArray(command, "materialization_batch_ids", materializationBatchIds);
        AddArrayParameter(command, "datas", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Jsonb, datas);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
