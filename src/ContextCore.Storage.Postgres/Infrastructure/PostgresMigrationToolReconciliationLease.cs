using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v57 → v58：tool_reconciliation_entries 追加对账裁决租约列，并将唯一键改为完整租户键。
/// - 新增 lease_owner / lease_token / lease_expires_at / fencing_token / attempt_count /
///   next_attempt_at / last_error 列（Reconciliation Running 必须有租约，
///   Worker 崩溃后 ListPendingAsync 重新领取过期 Running，杜绝永久卡死）。
/// - 唯一键 (run_id, request_id) → (workspace_id, run_id, request_id)（完整租户键）。
/// 两个阶段：
///   Online：ADD COLUMN IF NOT EXISTS（幂等，非破坏性）；
///   ConstraintValidate：DROP 旧唯一约束 + ADD 新唯一约束（先删后建，重入安全）。
/// 新数据库由基线 DDL 直接建好这些列与约束，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationToolReconciliationLease : IPostgresMigrationStep
{
    public string MigrationId => "0005_tool_reconciliation_lease_fencing";

    public string FromSchemaVersion => "cc-schema-v57";

    public string ToSchemaVersion => "cc-schema-v58";

    public string Description =>
        "tool_reconciliation_entries 追加对账裁决租约列（lease_owner/lease_token/lease_expires_at/"
        + "fencing_token/attempt_count/next_attempt_at/last_error），唯一键改为 (workspace_id, run_id, request_id)"
        + "（P0-4 租约 + P0-5 完整租户键）。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online,
        PostgresMigrationStage.ConstraintValidate
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "tool_reconciliation_entries");
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

        // lease_owner 列已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.oid = to_regclass(@table_name)
              AND a.attname = 'lease_owner'
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
        var table = PostgresNames.Table(options, "tool_reconciliation_entries");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        if (stage == PostgresMigrationStage.Online)
        {
            command.CommandText = $"""
                ALTER TABLE {table} ADD COLUMN IF NOT EXISTS lease_owner text NULL;
                ALTER TABLE {table} ADD COLUMN IF NOT EXISTS lease_token text NULL;
                ALTER TABLE {table} ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz NULL;
                ALTER TABLE {table} ADD COLUMN IF NOT EXISTS fencing_token bigint NOT NULL DEFAULT 0;
                ALTER TABLE {table} ADD COLUMN IF NOT EXISTS attempt_count integer NOT NULL DEFAULT 0;
                ALTER TABLE {table} ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz NULL;
                ALTER TABLE {table} ADD COLUMN IF NOT EXISTS last_error text NULL;
                """;
        }
        else if (stage == PostgresMigrationStage.ConstraintValidate)
        {
            var constraint = PostgresNames.Constraint(options, "tool_reconciliation_entries", "ws_run_request_unique");
            command.CommandText = $"""
                ALTER TABLE {table} DROP CONSTRAINT IF EXISTS {constraint};
                ALTER TABLE {table} ADD CONSTRAINT {constraint} UNIQUE (workspace_id, run_id, request_id);
                """;
        }
        else
        {
            return;
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
