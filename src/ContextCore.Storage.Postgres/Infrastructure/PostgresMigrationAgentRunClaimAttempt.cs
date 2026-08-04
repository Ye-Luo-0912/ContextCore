using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v62 → v63：agent_runs 追加 Scheduler Claim 尝试计数列。
/// - 新增 claim_attempt integer NOT NULL DEFAULT 0：每次领取/重领 +1，
///   供调度诊断区分首次领取与反复接管（claim 过期后其他节点重领）。
/// 单个 Online 阶段：ADD COLUMN IF NOT EXISTS（幂等，非破坏性）。
/// 新数据库由基线 DDL 直接建好该列，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationAgentRunClaimAttempt : IPostgresMigrationStep
{
    public string MigrationId => "0010_agent_run_claim_attempt";

    public string FromSchemaVersion => "cc-schema-v62";

    public string ToSchemaVersion => "cc-schema-v63";

    public string Description =>
        "agent_runs 追加 claim_attempt 列（Scheduler Claim 领取尝试计数）。";

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

        // claim_attempt 列已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.oid = to_regclass(@table_name)
              AND a.attname = 'claim_attempt'
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
        if (stage != PostgresMigrationStage.Online)
        {
            return;
        }

        var table = PostgresNames.Table(options, "agent_runs");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = $"""
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS claim_attempt integer NOT NULL DEFAULT 0;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
