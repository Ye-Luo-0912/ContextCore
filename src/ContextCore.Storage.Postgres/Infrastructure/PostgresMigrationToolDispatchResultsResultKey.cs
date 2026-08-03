using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v48 → v49：tool_dispatch_results 主键由 tool_call_id 迁移为 request_id。
/// 主键切换前先做数据冲突预检（重复 request_id 会阻断迁移并给出明确原因，
/// 而不是在 ADD PRIMARY KEY 时抛出难以定位的唯一约束冲突），
/// 再按 Online / Backfill / ConstraintValidate 三阶段幂等执行，与基线 DDL 中的修复块保持同一套 SQL。
/// </summary>
public sealed class PostgresMigrationToolDispatchResultsResultKey : IPostgresMigrationStep
{
    public string MigrationId => "0002_tool_dispatch_results_result_key";

    public string FromSchemaVersion => "cc-schema-v48";

    public string ToSchemaVersion => "cc-schema-v49";

    public string Description =>
        "tool_dispatch_results 主键由 tool_call_id 迁移为 request_id，追加 workspace_id / run_id / invocation_id 列，"
        + "并建立 UNIQUE(workspace_id, run_id, invocation_id) partial 约束与 tool_call_id / idempotency_key 辅助索引。";

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
        var table = PostgresNames.Table(options, "tool_dispatch_results");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        // 目标表不存在时无需执行：新数据库由基线 DDL 直接以 request_id 为主键创建。
        // ::text 将 regclass 转为文本，避免 Npgsql 无法以 object 读取 regclass 列。
        command.CommandText = "SELECT to_regclass(@table_name)::text;";
        command.Parameters.AddWithValue("table_name", table);
        var tableExists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (tableExists is null or DBNull)
        {
            return null;
        }

        // 查询当前主键列：request_id 表示已迁移；tool_call_id 表示需要迁移。
        command.CommandText = """
            SELECT a.attname
            FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
            WHERE i.indrelid = to_regclass(@table_name)
              AND i.indisprimary
            LIMIT 1;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("table_name", table);
        var primaryKeyColumn = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (string.Equals(primaryKeyColumn, "request_id", StringComparison.Ordinal))
        {
            return null;
        }

        // 仍为 tool_call_id 主键：检查存量数据是否包含重复 request_id，重复则阻断迁移。
        command.CommandText = $"""
            SELECT request_id
            FROM {table}
            GROUP BY request_id
            HAVING COUNT(*) > 1
            LIMIT 1;
            """;
        command.Parameters.Clear();
        var duplicate = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (duplicate is not null and not DBNull)
        {
            return $"tool_dispatch_results 存在重复 request_id（示例：{duplicate}）。"
                + "request_id 将升级为主键，重复数据会违反唯一约束。请先合并或删除重复行后重试迁移。";
        }

        return string.Empty;
    }

    public async Task ExecuteStageAsync(
        PostgresMigrationStage stage,
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var commandText = stage switch
        {
            PostgresMigrationStage.Online => BuildOnlineSql(options),
            PostgresMigrationStage.Backfill => BuildBackfillSql(options),
            PostgresMigrationStage.ConstraintValidate => BuildConstraintValidateSql(options),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "未知迁移阶段。")
        };

        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = options.CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>阶段一（Online）：追加 workspace_id / run_id / invocation_id 列，幂等。</summary>
    private static string BuildOnlineSql(PostgresOptions options)
    {
        var table = PostgresNames.Table(options, "tool_dispatch_results");
        return $"""
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS workspace_id text;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS run_id text;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS invocation_id text;
            """;
    }

    /// <summary>阶段二（Backfill）：对存量行做 COALESCE 回填，空字符串不参与 UNIQUE 约束。</summary>
    private static string BuildBackfillSql(PostgresOptions options)
    {
        var table = PostgresNames.Table(options, "tool_dispatch_results");
        return $"""
            UPDATE {table} SET workspace_id = COALESCE(workspace_id, '') WHERE workspace_id IS NULL;
            UPDATE {table} SET run_id = COALESCE(run_id, '') WHERE run_id IS NULL;
            UPDATE {table} SET invocation_id = COALESCE(invocation_id, '') WHERE invocation_id IS NULL;
            """;
    }

    /// <summary>阶段三（ConstraintValidate）：切换主键并建立 UNIQUE / 辅助索引。</summary>
    private static string BuildConstraintValidateSql(PostgresOptions options)
    {
        var table = PostgresNames.Table(options, "tool_dispatch_results");
        // Postgres 默认主键约束名 {table}_pkey，与基线 DDL 保持一致的命名推导。
        var pkey = $"{options.TablePrefix}tool_dispatch_results_pkey";
        return $"""
            ALTER TABLE {table} DROP CONSTRAINT IF EXISTS {pkey};
            DROP INDEX IF EXISTS {PostgresNames.Index(options, "tool_dispatch_results", "request")};
            ALTER TABLE {table} ADD PRIMARY KEY (request_id);
            CREATE UNIQUE INDEX IF NOT EXISTS {PostgresNames.Index(options, "tool_dispatch_results", "ws_run_invocation")}
                ON {table} (workspace_id, run_id, invocation_id) WHERE invocation_id != '';
            CREATE INDEX IF NOT EXISTS {PostgresNames.Index(options, "tool_dispatch_results", "tool_call_id")} ON {table} (tool_call_id);
            CREATE INDEX IF NOT EXISTS {PostgresNames.Index(options, "tool_dispatch_results", "idempotency_key")} ON {table} (idempotency_key) WHERE idempotency_key IS NOT NULL;
            """;
    }
}
