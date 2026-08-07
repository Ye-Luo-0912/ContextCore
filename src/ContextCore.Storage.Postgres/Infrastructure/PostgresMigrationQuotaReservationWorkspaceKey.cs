using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v67 → v68：workspace_quota_reservations 预留表工作区复合键（租户隔离加固）。
/// 预留从 reservation_id 单键升级为 (workspace_id, reservation_id) 复合主键——
/// 与 agent_runs 的 (workspace_id, run_id) 身份模型对齐（预留 id 即 run id），
/// 允许不同工作区使用相同预留 id 而互不干扰。
///
/// 三阶段执行：
/// 1. Online：workspace_id 列已存在（基线建表即 NOT NULL），仅防御性置 NOT NULL；
/// 2. Backfill：按 reservation_id join agent_runs 回填 workspace_id（防御；
///    写入路径自始携带 workspace_id，理论上无 NULL 行），孤儿预留删除；
/// 3. ConstraintValidate：主键由 (reservation_id) 切换为 (workspace_id, reservation_id)。
///
/// 新数据库由基线 DDL 直接以复合主键建表，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationQuotaReservationWorkspaceKey : IPostgresMigrationStep
{
    public string MigrationId => "0015_quota_reservation_workspace_key";

    public string FromSchemaVersion => "cc-schema-v67";

    public string ToSchemaVersion => "cc-schema-v68";

    public string Description =>
        "workspace_quota_reservations 主键由 reservation_id 升级为 (workspace_id, reservation_id) "
        + "（预留身份与 agent_runs 复合键对齐，跨工作区同预留 id 互不干扰）。";

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
        var table = PostgresNames.Table(options, "workspace_quota_reservations");
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

        // 主键已包含 2 列（workspace_id, reservation_id）= 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_constraint c
            WHERE c.conrelid = to_regclass(@table_name)
              AND c.contype = 'p'
              AND array_length(c.conkey, 1) = 2
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
        var table = PostgresNames.Table(options, "workspace_quota_reservations");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        switch (stage)
        {
            case PostgresMigrationStage.Online:
                // workspace_id 列在基线建表即 NOT NULL；防御性置 NOT NULL（幂等）。
                command.CommandText = $"""
                    ALTER TABLE {table} ALTER COLUMN workspace_id SET NOT NULL;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                break;

            case PostgresMigrationStage.Backfill:
                // 预留行自始携带 workspace_id（写入路径即带），理论上无 NULL 行；
                // 防御性清理 NULL 行即可——不能依赖 agent_runs 回填（基线 DDL 尚未执行，
                // 版本化步骤在基线前运行，agent_runs 可能尚不存在）。
                command.CommandText = $"""
                    DELETE FROM {table}
                    WHERE workspace_id IS NULL;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                break;

            case PostgresMigrationStage.ConstraintValidate:
                command.CommandText = $"""
                    ALTER TABLE {table} DROP CONSTRAINT IF EXISTS {table}_pkey;
                    ALTER TABLE {table} ADD PRIMARY KEY (workspace_id, reservation_id);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }
}
