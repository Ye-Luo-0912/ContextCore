using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v61 → v62：tool_reconciliation_entries 追加客户端决策幂等身份列。
/// - 新增 decision_request_id text NULL：人工 resolve 请求携带的 DecisionRequestId，
///   在裁决落终态时持久化——相同决策身份 + 相同 outcome 重试 → 幂等成功；
///   相同决策身份 + 相反 outcome → 决策冲突（409）；无/不同决策身份 → 重复提交被拒绝。
/// 单个 Online 阶段：ADD COLUMN IF NOT EXISTS（幂等，非破坏性）。
/// 新数据库由基线 DDL 直接建好该列，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationToolReconciliationDecisionRequestId : IPostgresMigrationStep
{
    public string MigrationId => "0009_tool_reconciliation_decision_request_id";

    public string FromSchemaVersion => "cc-schema-v61";

    public string ToSchemaVersion => "cc-schema-v62";

    public string Description =>
        "tool_reconciliation_entries 追加 decision_request_id 列（客户端决策幂等身份）。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online
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

        // decision_request_id 列已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.oid = to_regclass(@table_name)
              AND a.attname = 'decision_request_id'
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

        var table = PostgresNames.Table(options, "tool_reconciliation_entries");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = $"""
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS decision_request_id text NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
