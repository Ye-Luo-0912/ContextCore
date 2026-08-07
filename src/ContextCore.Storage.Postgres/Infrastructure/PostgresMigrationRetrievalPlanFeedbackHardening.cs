using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v60 → v61：retrieval_plan_feedback 自适应反馈加固。
/// 新增 feedback_id / idempotency_key / source / confidence / outcome_quality / subject 列，
/// 并建立 (plan_signature, idempotency_key) 部分唯一索引（WHERE idempotency_key IS NOT NULL），
/// 配合 INSERT ... ON CONFLICT DO NOTHING 实现反馈幂等去重——重放 / 重复提交
/// （如客户端重试）不产生重复反馈。
/// 单 Online 阶段幂等执行（ADD COLUMN IF NOT EXISTS / CREATE UNIQUE INDEX IF NOT EXISTS），
/// 与基线 DDL 保持同一套 SQL；新数据库由基线 DDL 直接建好，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationRetrievalPlanFeedbackHardening : IPostgresMigrationStep
{
    public string MigrationId => "0008_retrieval_plan_feedback_hardening";

    public string FromSchemaVersion => "cc-schema-v60";

    public string ToSchemaVersion => "cc-schema-v61";

    public string Description =>
        "retrieval_plan_feedback 新增 feedback_id / idempotency_key / source / confidence / "
        + "outcome_quality / subject 列 + (plan_signature, idempotency_key) 部分唯一索引"
        + "（P0-16 反馈幂等去重与可信度字段，防跨 Workspace 污染与单源投毒）。";

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

        // idempotency_key 列已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.oid = to_regclass(@table_name)
              AND a.attname = 'idempotency_key'
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
        var idempotencyIndex = PostgresNames.Index(options, "retrieval_plan_feedback", "idempotency");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = $"""
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS feedback_id text NULL;
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS idempotency_key text NULL;
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS source smallint NOT NULL DEFAULT 0;
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS confidence double precision NOT NULL DEFAULT 1.0;
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS outcome_quality double precision NOT NULL DEFAULT 1.0;
ALTER TABLE {table} ADD COLUMN IF NOT EXISTS subject text NULL;
CREATE UNIQUE INDEX IF NOT EXISTS {idempotencyIndex}
    ON {table} (plan_signature, idempotency_key) WHERE idempotency_key IS NOT NULL;
""";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
