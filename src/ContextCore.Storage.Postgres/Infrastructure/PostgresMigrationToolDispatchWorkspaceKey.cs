using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v68 → v69：Tool Dispatch Journal / Durable Tool Result 工作区复合键（租户隔离加固）。
/// journal 主键由 request_id 单键升级为 (workspace_id, run_id, request_id) 复合主键，
/// 结果表主键同步升级——跨工作区可复用相同 RunId 与相同 request_id 而互不干扰，
/// 与 agent_run_leases / workspace_quota_reservations 的复合键模式对齐。
///
/// 同时将幂等键唯一约束从全局 (idempotency_key) 升级为
/// (workspace_id, run_id, idempotency_key)：不同工作区/不同 Run 可使用相同幂等键
/// 而互不冲突（幂等键是业务级去重键，隔离边界为工作区 + Run）。
///
/// 三阶段执行：
/// 1. Online：workspace_id / run_id 列已存在（基线建表即有），仅防御性置 NOT NULL；
/// 2. Backfill：按 run_id join agent_runs 回填 workspace_id/run_id（防御；
///    写入路径自始携带双键，理论上无 NULL 行），孤儿行（对应 Run 已不存在）删除——
///    journal 无对应 Run 即无审计/对账意义，结果行同理；
/// 3. ConstraintValidate：journal 主键切换为 (workspace_id, run_id, request_id)，
///    幂等键唯一索引切换为 (workspace_id, run_id, idempotency_key)，
///    结果表主键切换为 (workspace_id, run_id, request_id)。
///
/// 新数据库由基线 DDL 直接以复合主键建表，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationToolDispatchWorkspaceKey : IPostgresMigrationStep
{
    public string MigrationId => "0016_tool_dispatch_workspace_key";

    public string FromSchemaVersion => "cc-schema-v68";

    public string ToSchemaVersion => "cc-schema-v69";

    public string Description =>
        "tool_dispatch_journal_entries / tool_dispatch_results 主键由 request_id 升级为 "
        + "(workspace_id, run_id, request_id)，幂等键唯一约束升级为 (workspace_id, run_id, idempotency_key) "
        + "（跨工作区同 RunId/RequestId/幂等键互不干扰）。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online,
        PostgresMigrationStage.Backfill,
        PostgresMigrationStage.ConstraintValidate
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "tool_dispatch_journal_entries");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        // 目标表不存在时无需执行：新数据库由基线 DDL 直接以新结构创建。
        command.CommandText = "SELECT to_regclass(@table_name)::text;";
        command.Parameters.AddWithValue("table_name", table);
        var tableExists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (tableExists is null or DBNull)
        {
            return null;
        }

        // 主键已包含 3 列（workspace_id, run_id, request_id）= 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_constraint c
            WHERE c.conrelid = to_regclass(@table_name)
              AND c.contype = 'p'
              AND array_length(c.conkey, 1) = 3
            LIMIT 1;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("table_name", table);
        var exists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return exists is null or DBNull ? string.Empty : null;
    }

    public async Task ExecuteStageAsync(
        PostgresMigrationStage stage,
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var journal = PostgresNames.Table(options, "tool_dispatch_journal_entries");
        var results = PostgresNames.Table(options, "tool_dispatch_results");
        var runsTable = PostgresNames.Table(options, "agent_runs");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        switch (stage)
        {
            case PostgresMigrationStage.Online:
                // 双键列在基线建表即存在（可空）；防御性置 NOT NULL 前先清理 NULL 行
                // （Backfill 阶段处理），Online 阶段仅确保列存在。
                command.CommandText = $"""
                    ALTER TABLE {journal} ADD COLUMN IF NOT EXISTS workspace_id text;
                    ALTER TABLE {journal} ADD COLUMN IF NOT EXISTS run_id text;
                    ALTER TABLE {results} ADD COLUMN IF NOT EXISTS workspace_id text;
                    ALTER TABLE {results} ADD COLUMN IF NOT EXISTS run_id text;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                break;

            case PostgresMigrationStage.Backfill:
                // 按 run_id join agent_runs 回填双键；无法回填的孤儿行删除
                // （对应 Run 已不存在，journal/结果无审计与对账意义）。
                command.CommandText = $"""
                    UPDATE {journal} j
                    SET workspace_id = r.workspace_id,
                        run_id = COALESCE(j.run_id, r.run_id)
                    FROM {runsTable} r
                    WHERE j.run_id = r.run_id
                      AND (j.workspace_id IS NULL OR j.workspace_id = '');
                    DELETE FROM {journal}
                    WHERE workspace_id IS NULL OR workspace_id = ''
                       OR run_id IS NULL OR run_id = '';

                    UPDATE {results} d
                    SET workspace_id = r.workspace_id,
                        run_id = COALESCE(d.run_id, r.run_id)
                    FROM {runsTable} r
                    WHERE d.run_id = r.run_id
                      AND (d.workspace_id IS NULL OR d.workspace_id = '');
                    DELETE FROM {results}
                    WHERE workspace_id IS NULL OR workspace_id = ''
                       OR run_id IS NULL OR run_id = '';
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                break;

            case PostgresMigrationStage.ConstraintValidate:
                var journalPkey = $"{options.TablePrefix}tool_dispatch_journal_entries_pkey";
                var resultsPkey = $"{options.TablePrefix}tool_dispatch_results_pkey";
                var idempotencyIndex = PostgresNames.Index(options, "tool_dispatch_journal_entries", "idempotency");
                command.CommandText = $"""
                    ALTER TABLE {journal} ALTER COLUMN workspace_id SET NOT NULL;
                    ALTER TABLE {journal} ALTER COLUMN run_id SET NOT NULL;
                    ALTER TABLE {results} ALTER COLUMN workspace_id SET NOT NULL;
                    ALTER TABLE {results} ALTER COLUMN run_id SET NOT NULL;

                    ALTER TABLE {journal} DROP CONSTRAINT IF EXISTS {journalPkey};
                    ALTER TABLE {journal} ADD PRIMARY KEY (workspace_id, run_id, request_id);

                    DROP INDEX IF EXISTS {idempotencyIndex};
                    CREATE UNIQUE INDEX IF NOT EXISTS {idempotencyIndex}
                        ON {journal} (workspace_id, run_id, idempotency_key)
                        WHERE idempotency_key IS NOT NULL;

                    ALTER TABLE {results} DROP CONSTRAINT IF EXISTS {resultsPkey};
                    ALTER TABLE {results} ADD PRIMARY KEY (workspace_id, run_id, request_id);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }
}
