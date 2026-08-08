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
///    写入路径自始携带双键，理论上无 NULL 行）——映射规则：
///    - 唯一映射（run_id 只对应一个 workspace_id）→ 回填；
///    - 歧义映射（run_id 对应多个 workspace_id）→ 迁移前阻断失败（PreCheck 检测），
///      要求人工修复——Tool Journal 是外部副作用审计真相，绝不替系统猜映射；
///    - 未映射（run_id 在 agent_runs 不存在）→ 移入隔离表 tool_dispatch_quarantine
///      （保留审计真相，不删除），再移除原行；
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
        + "（跨工作区同 RunId/RequestId/幂等键互不干扰）；歧义/未映射历史行移入隔离表并阻断迁移（不替系统猜映射）。";

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
        if (exists is not null and not DBNull)
        {
            return null;
        }

        // 歧义映射阻断：缺 workspace 的 journal / results 行，其 run_id 在 agent_runs 中
        // 存在多个不同 workspace_id——映射本身就是歧义的，绝不替系统猜。
        // 要求人工修复（指定映射或清理数据）后重试迁移；未映射行（run_id 无任何
        // agent_runs 行）不算歧义，Backfill 阶段移入隔离表保留审计真相。
        var runsTable = PostgresNames.Table(options, "agent_runs");
        var resultsTable = PostgresNames.Table(options, "tool_dispatch_results");
        command.CommandText = $"""
            SELECT 1
            FROM {table} j
            JOIN {runsTable} r ON r.run_id = j.run_id
            WHERE j.workspace_id IS NULL OR j.workspace_id = ''
            GROUP BY j.request_id, j.run_id
            HAVING COUNT(DISTINCT r.workspace_id) > 1
            LIMIT 1;
            """;
        command.Parameters.Clear();
        var ambiguous = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (ambiguous is not null and not DBNull)
        {
            return "tool_dispatch_journal_entries 存在歧义 run 映射（同一 run_id 对应多个 workspace_id）。"
                + "Tool Journal 是外部副作用审计真相，迁移不替系统猜映射——"
                + "请人工修复（明确归属或清理历史行）后重试迁移。";
        }

        command.CommandText = $"""
            SELECT 1
            FROM {resultsTable} d
            JOIN {runsTable} r ON r.run_id = d.run_id
            WHERE d.workspace_id IS NULL OR d.workspace_id = ''
            GROUP BY d.request_id, d.run_id
            HAVING COUNT(DISTINCT r.workspace_id) > 1
            LIMIT 1;
            """;
        command.Parameters.Clear();
        var ambiguousResults = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (ambiguousResults is not null and not DBNull)
        {
            return "tool_dispatch_results 存在歧义 run 映射（同一 run_id 对应多个 workspace_id）。"
                + "Tool 执行结果审计真相不替系统猜映射——"
                + "请人工修复（明确归属或清理历史行）后重试迁移。";
        }

        return string.Empty;
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
                // 按 run_id join agent_runs 回填双键。映射规则（审计真相不删除）：
                // 1. 唯一映射（run_id 只对应一个 workspace_id）→ 回填；
                // 2. 歧义映射（多 workspace）已被 PreCheck 阻断（fail-closed，人工修复）；
                // 3. 未映射（run_id 无 agent_runs 行）→ 移入隔离表 tool_dispatch_quarantine
                //    （保留审计真相 + 原因），再移除原行——绝不静默删除外部副作用审计数据。
                // 结果表同理。quarantine 表幂等建表（仅存量迁移按需创建）。
                var quarantineTable = PostgresNames.Table(options, "tool_dispatch_quarantine");
                command.CommandText = $"""
                    CREATE TABLE IF NOT EXISTS {quarantineTable} (
                        request_id text NOT NULL,
                        tool_name text NOT NULL DEFAULT '',
                        state smallint NOT NULL DEFAULT 0,
                        idempotency_key text,
                        payload_digest text,
                        external_operation_id text,
                        workspace_id text NULL,
                        run_id text NOT NULL,
                        created_at timestamptz NULL,
                        updated_at timestamptz NULL,
                        diagnostic_note text,
                        quarantine_reason text NOT NULL,
                        quarantined_at timestamptz NOT NULL,
                        PRIMARY KEY (run_id, request_id));

                    -- 唯一映射 → 回填（排除歧义 run_id：多个 workspace 时 UPDATE 结果不确定）
                    UPDATE {journal} j
                    SET workspace_id = r.workspace_id,
                        run_id = COALESCE(j.run_id, r.run_id)
                    FROM {runsTable} r
                    WHERE j.run_id = r.run_id
                      AND (j.workspace_id IS NULL OR j.workspace_id = '')
                      AND NOT EXISTS (
                          SELECT 1 FROM {runsTable} r2
                          WHERE r2.run_id = r.run_id
                            AND r2.workspace_id <> r.workspace_id);

                    -- 未映射（run_id 无任何 agent_runs 行）→ 隔离（保留审计真相，不删除）
                    INSERT INTO {quarantineTable} (
                        request_id, tool_name, state, idempotency_key, payload_digest,
                        external_operation_id, workspace_id, run_id,
                        created_at, updated_at, diagnostic_note,
                        quarantine_reason, quarantined_at)
                    SELECT j.request_id, j.tool_name, j.state, j.idempotency_key, j.payload_digest,
                           j.external_operation_id, j.workspace_id, j.run_id,
                           j.created_at, j.updated_at, j.diagnostic_note,
                           'unmapped-run', @quarantined_at
                    FROM {journal} j
                    WHERE (j.workspace_id IS NULL OR j.workspace_id = '')
                      AND NOT EXISTS (
                          SELECT 1 FROM {runsTable} r WHERE r.run_id = j.run_id)
                      AND NOT EXISTS (
                          SELECT 1 FROM {quarantineTable} q
                          WHERE q.run_id = j.run_id AND q.request_id = j.request_id);

                    -- 已隔离的未映射行从主表移除（真相已保留在隔离表）
                    DELETE FROM {journal} j
                    USING {quarantineTable} q
                    WHERE q.run_id = j.run_id
                      AND q.request_id = j.request_id
                      AND q.quarantine_reason = 'unmapped-run';

                    -- 结果表：同上（唯一映射回填 + 未映射隔离）
                    UPDATE {results} d
                    SET workspace_id = r.workspace_id,
                        run_id = COALESCE(d.run_id, r.run_id)
                    FROM {runsTable} r
                    WHERE d.run_id = r.run_id
                      AND (d.workspace_id IS NULL OR d.workspace_id = '')
                      AND NOT EXISTS (
                          SELECT 1 FROM {runsTable} r2
                          WHERE r2.run_id = r.run_id
                            AND r2.workspace_id <> r.workspace_id);

                    INSERT INTO {quarantineTable} (
                        request_id, tool_name, state, idempotency_key, payload_digest,
                        external_operation_id, workspace_id, run_id,
                        created_at, updated_at, diagnostic_note,
                        quarantine_reason, quarantined_at)
                    SELECT d.request_id, '', 0, NULL, NULL,
                           NULL, d.workspace_id, d.run_id,
                           NULL, NULL, NULL,
                           'unmapped-run', @quarantined_at
                    FROM {results} d
                    WHERE (d.workspace_id IS NULL OR d.workspace_id = '')
                      AND NOT EXISTS (
                          SELECT 1 FROM {runsTable} r WHERE r.run_id = d.run_id)
                      AND NOT EXISTS (
                          SELECT 1 FROM {quarantineTable} q
                          WHERE q.run_id = d.run_id AND q.request_id = d.request_id);

                    DELETE FROM {results} d
                    USING {quarantineTable} q
                    WHERE q.run_id = d.run_id
                      AND q.request_id = d.request_id
                      AND q.quarantine_reason = 'unmapped-run';
                    """;
                command.Parameters.AddWithValue("quarantined_at", DateTimeOffset.UtcNow);
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
