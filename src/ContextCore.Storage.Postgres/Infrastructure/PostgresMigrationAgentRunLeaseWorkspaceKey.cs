using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v66 → v67：agent_run_leases 租约表工作区复合键（租户隔离加固）。
/// 租约从 run_id 单键升级为 (workspace_id, run_id) 复合主键——全部单 Run 租约操作
/// （获取/续约/释放/查询）按工作区 + Run 复合寻址，杜绝跨工作区误操作。
///
/// 三阶段执行：
/// 1. Online：新增 workspace_id 列（可空）；
/// 2. Backfill：按 run_id join agent_runs 回填 workspace_id；
///    无法回填的孤儿租约（对应 Run 已不存在）删除——租约无意义且无法归位；
/// 3. ConstraintValidate：workspace_id 置 NOT NULL 并切换主键为 (workspace_id, run_id)。
///
/// 新数据库由基线 DDL 直接以复合主键建表，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationAgentRunLeaseWorkspaceKey : IPostgresMigrationStep
{
    public string MigrationId => "0014_agent_run_lease_workspace_key";

    public string FromSchemaVersion => "cc-schema-v66";

    public string ToSchemaVersion => "cc-schema-v67";

    public string Description =>
        "agent_run_leases 新增 workspace_id 列，主键由 run_id 升级为 (workspace_id, run_id) "
        + "（全部单 Run 租约操作按工作区 + Run 复合键寻址，租户隔离加固）。";

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
        var table = PostgresNames.Table(options, "agent_run_leases");
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

        // workspace_id 列已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.oid = to_regclass(@table_name)
              AND a.attname = 'workspace_id'
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
        var table = PostgresNames.Table(options, "agent_run_leases");
        var runsTable = PostgresNames.Table(options, "agent_runs");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        switch (stage)
        {
            case PostgresMigrationStage.Online:
                command.CommandText = $"""
                    ALTER TABLE {table} ADD COLUMN IF NOT EXISTS workspace_id text NULL;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                break;

            case PostgresMigrationStage.Backfill:
                // 按 run_id join agent_runs 回填 workspace_id；孤儿租约（Run 已不存在）删除。
                command.CommandText = $"""
                    UPDATE {table} l
                    SET workspace_id = r.workspace_id
                    FROM {runsTable} r
                    WHERE l.run_id = r.run_id
                      AND l.workspace_id IS NULL;
                    DELETE FROM {table}
                    WHERE workspace_id IS NULL;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                break;

            case PostgresMigrationStage.ConstraintValidate:
                // Postgres 默认主键约束名 {table}_pkey；约束名不参与 schema 限定，
                // 表名带 schema 前缀时不能直接用 {table}_pkey（会变成限定名导致语法错误）。
                var pkey = $"{options.TablePrefix}agent_run_leases_pkey";
                command.CommandText = $"""
                    ALTER TABLE {table} ALTER COLUMN workspace_id SET NOT NULL;
                    ALTER TABLE {table} DROP CONSTRAINT IF EXISTS {pkey};
                    ALTER TABLE {table} ADD PRIMARY KEY (workspace_id, run_id);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }
}
