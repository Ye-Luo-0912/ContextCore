using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v56 → v57：Recovery、Canary 与 Learning Durability。
/// 1. pipeline_runs 追加 canary_percentage / canary_revision / canary_epoch 列：
/// Canary 状态并入 PipelineRunSnapshot（单一真相源），由
/// <see cref="IPipelineRunStore.UpdateCanaryStateAsync"/> CAS 维护；
/// 重启后可从 run snapshot 直接恢复 canary 状态，不再依赖 canary_pipelines 表恢复。
/// 单 Online 阶段幂等执行（ADD COLUMN IF NOT EXISTS），
/// 与基线 DDL 保持同一套 SQL；新数据库由基线 DDL 直接建好，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationRecoveryCanaryLearningDurability : IPostgresMigrationStep
{
    public string MigrationId => "0005_recovery_canary_learning_durability";

    public string FromSchemaVersion => "cc-schema-v56";

    public string ToSchemaVersion => "cc-schema-v57";

    public string Description =>
        "pipeline_runs 追加 canary_percentage / canary_revision / canary_epoch（Canary 单一真相源并入"
        + " PipelineRunSnapshot）。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "pipeline_runs");
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

        // canary_percentage 列已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.oid = to_regclass(@table_name)
              AND a.attname = 'canary_percentage'
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
        var pipelineRuns = PostgresNames.Table(options, "pipeline_runs");
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            ALTER TABLE {pipelineRuns} ADD COLUMN IF NOT EXISTS canary_percentage integer NOT NULL DEFAULT 0;
            ALTER TABLE {pipelineRuns} ADD COLUMN IF NOT EXISTS canary_revision bigint NOT NULL DEFAULT 0;
            ALTER TABLE {pipelineRuns} ADD COLUMN IF NOT EXISTS canary_epoch bigint NOT NULL DEFAULT 0;
            """;
        command.CommandTimeout = options.CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
