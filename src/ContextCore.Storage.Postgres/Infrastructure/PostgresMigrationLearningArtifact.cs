using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v71 → v72：Learning Artifact Plane —— dataset_snapshots 持久化表。
/// 数据集快照工件（完整性 / 血缘 / 可重现 / 版本追责）落库，
/// 按 (workspace_id, snapshot_id) 复合主键点查（可重建入口）。
/// 阶段：Online（建表，幂等 CREATE TABLE IF NOT EXISTS）。
/// </summary>
public sealed class PostgresMigrationLearningArtifact : IPostgresMigrationStep
{
    public string MigrationId => "0019_learning_artifact";

    public string FromSchemaVersion => "cc-schema-v71";

    public string ToSchemaVersion => "cc-schema-v72";

    public string Description =>
        "新增 dataset_snapshots 表（Learning Artifact Plane）：数据集快照工件持久化，"
        + "(workspace_id, snapshot_id) 复合主键点查。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "dataset_snapshots");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        // 目标表不存在时执行；已存在 = 已迁移（或新库基线已建）。
        command.CommandText = "SELECT to_regclass(@table_name)::text;";
        command.Parameters.AddWithValue("table_name", table);
        var tableExists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return tableExists is null or DBNull ? string.Empty : null;
    }

    public async Task ExecuteStageAsync(
        PostgresMigrationStage stage,
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "dataset_snapshots");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = $"""
CREATE TABLE IF NOT EXISTS {table} (
    workspace_id text NOT NULL,
    snapshot_id text NOT NULL,
    schema_version text NOT NULL,
    created_at timestamptz NOT NULL,
    data jsonb NOT NULL,
    PRIMARY KEY (workspace_id, snapshot_id));

CREATE INDEX IF NOT EXISTS {PostgresNames.Index(options, "dataset_snapshots", "created")}
    ON {table} (workspace_id, created_at DESC);
""";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
