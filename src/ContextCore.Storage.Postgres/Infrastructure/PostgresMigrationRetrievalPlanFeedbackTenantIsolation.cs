using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v65 → v66：retrieval_plan_feedback 自适应控制面租户隔离（结构化列）。
/// 新增 workspace_id（NOT NULL DEFAULT ''，隔离边界）+ collection_id / purpose /
/// policy_version / retrieval_profile / task_class 结构化租户维度列，
/// 并建立 (workspace_id, plan_signature) 索引支撑按工作区作用域的查询 / 清除。
/// 既有行（迁移前记录）workspace_id 归入全局默认工作区（''）——签名不可逆，
/// 无法回溯其原始工作区；新记录由控制面端点服务端派生签名并显式写入工作区。
/// 单 Online 阶段幂等执行（ADD COLUMN IF NOT EXISTS / CREATE INDEX IF NOT EXISTS），
/// 与基线 DDL 保持同一套 SQL；新数据库由基线 DDL 直接建好，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationRetrievalPlanFeedbackTenantIsolation : IPostgresMigrationStep
{
    public string MigrationId => "0013_retrieval_plan_feedback_tenant_isolation";

    public string FromSchemaVersion => "cc-schema-v65";

    public string ToSchemaVersion => "cc-schema-v66";

    public string Description =>
        "retrieval_plan_feedback 新增 workspace_id + collection_id / purpose / policy_version / "
        + "retrieval_profile / task_class 结构化租户维度列 + (workspace_id, plan_signature) 索引"
        + "（自适应控制面租户隔离：服务端派生签名 + 按工作区作用域查询/清除）。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "retrieval_plan_feedback");
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
        if (stage != PostgresMigrationStage.Online)
        {
            return;
        }

        var table = PostgresNames.Table(options, "retrieval_plan_feedback");
        var workspaceIndex = PostgresNames.Index(options, "retrieval_plan_feedback", "workspace");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = $"""
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS workspace_id text NOT NULL DEFAULT '';
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS collection_id text NULL;
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS purpose text NULL;
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS policy_version text NULL;
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS retrieval_profile text NULL;
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS task_class text NULL;
CREATE INDEX IF NOT EXISTS {workspaceIndex}
    ON {table} (workspace_id, plan_signature);
""";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
