using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v55 → v56：model_node_applied_state 追加引擎代次与漂移隔离列
/// （engine_generation / is_isolated / drift_reported_at / isolation_reason）。
/// 单 Online 阶段幂等执行（ADD COLUMN IF NOT EXISTS），与基线 DDL 保持同一套 SQL；
/// 新数据库由基线 DDL 直接建好这些列，PreCheck 跳过。
/// 目的：分离 SlotRevision（集群槽位期望）与 EngineGeneration（本地引擎代次），
/// 并让漂移自动隔离事实（Isolated + 原因 + 时间）跨进程持久化——集群注册表据此
/// 计算 DriftedNodeCount 与 IsRolloutReady，杜绝"Slot=A、Engine=B"的错位被伪装为收敛。
/// </summary>
public sealed class PostgresMigrationModelNodeAppliedStateIsolation : IPostgresMigrationStep
{
    public string MigrationId => "0004_model_node_applied_state_isolation";

    public string FromSchemaVersion => "cc-schema-v55";

    public string ToSchemaVersion => "cc-schema-v56";

    public string Description =>
        "model_node_applied_state 追加 engine_generation / is_isolated / drift_reported_at / isolation_reason 列"
        + "（SlotRevision 与 EngineGeneration 分离 + 漂移自动隔离持久化，支撑 rollout readiness 判定）。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "model_node_applied_state");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        // 目标表不存在时无需执行：新数据库由基线 DDL 直接以新列创建。
        command.CommandText = "SELECT to_regclass(@table_name)::text;";
        command.Parameters.AddWithValue("table_name", table);
        var tableExists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (tableExists is null or DBNull)
        {
            return null;
        }

        // is_isolated 列已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.oid = to_regclass(@table_name)
              AND a.attname = 'is_isolated'
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
        var table = PostgresNames.Table(options, "model_node_applied_state");
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS engine_generation bigint NULL;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS is_isolated boolean NOT NULL DEFAULT false;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS drift_reported_at timestamptz NULL;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS isolation_reason text NULL;
            """;
        command.CommandTimeout = options.CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
