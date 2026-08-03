using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v52 → v53：agent_runs 追加调度/重试列（priority / max_retries / retry_count / next_retry_at）
/// 与 Durable Scheduler 领取索引 (state, priority DESC, created_at ASC)。
/// 单 Online 阶段幂等执行（ADD COLUMN IF NOT EXISTS + CREATE INDEX IF NOT EXISTS），
/// 与基线 DDL 中的修复块保持同一套 SQL；新数据库由基线 DDL 直接建好这些列，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationAgentRunScheduling : IPostgresMigrationStep
{
    public string MigrationId => "0003_agent_run_scheduling";

    public string FromSchemaVersion => "cc-schema-v52";

    public string ToSchemaVersion => "cc-schema-v53";

    public string Description =>
        "agent_runs 追加 priority / max_retries / retry_count / next_retry_at 列"
        + "（Durable Scheduler 优先级排序 + Run 级重试与死信），"
        + "并建立 (state, priority DESC, created_at ASC) 领取索引支撑 FOR UPDATE SKIP LOCKED 公平领取。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "agent_runs");
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

        // priority 列已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.oid = to_regclass(@table_name)
              AND a.attname = 'priority'
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
        var table = PostgresNames.Table(options, "agent_runs");
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS priority integer NOT NULL DEFAULT 0;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS max_retries integer NOT NULL DEFAULT 0;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS retry_count integer NOT NULL DEFAULT 0;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS next_retry_at timestamptz NULL;
            CREATE INDEX IF NOT EXISTS {PostgresNames.Index(options, "agent_runs", "scheduling")}
                ON {table} (state, priority DESC, created_at ASC);
            """;
        command.CommandTimeout = options.CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
