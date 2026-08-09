using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v72 → v73：Decision Commit Outbox —— 决策提交可靠链。
/// Decision Commit = Decision Record + Evidence Manifest 引用 + Learning Materialization
/// Intent 经 Durable Outbox 连成可靠链：决策记录落库 / 物化意图执行失败或进程崩溃后，
/// 未 Ack 的条目由消费方重放（不丢决策、不丢物化意图）。
/// 阶段：Online（建表 + 索引，幂等）。
/// </summary>
public sealed class PostgresMigrationDecisionCommitOutbox : IPostgresMigrationStep
{
    public string MigrationId => "0020_decision_commit_outbox";

    public string FromSchemaVersion => "cc-schema-v72";

    public string ToSchemaVersion => "cc-schema-v73";

    public string Description =>
        "新增 decision_commits 表（Decision Commit Outbox）：决策提交消息 durable 队列，"
        + "(workspace_id, decision_id) 幂等入队 + 租约领取。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "decision_commits");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

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
        var table = PostgresNames.Table(options, "decision_commits");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = $"""
CREATE TABLE IF NOT EXISTS {table} (
    outbox_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    workspace_id text NOT NULL,
    collection_id text NOT NULL,
    decision_id text NOT NULL,
    commit_type smallint NOT NULL DEFAULT 1,
    evidence_ref text NULL,
    payload jsonb NOT NULL,
    state smallint NOT NULL DEFAULT 0,
    attempts integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    processed_at timestamptz NULL,
    lease_owner text NULL,
    lease_token text NULL,
    lease_expires_at timestamptz NULL,
    last_error text NULL,
    CONSTRAINT {PostgresNames.Index(options, "decision_commits", "run")}
        UNIQUE (workspace_id, decision_id));

CREATE INDEX IF NOT EXISTS {PostgresNames.Index(options, "decision_commits", "state")}
    ON {table} (state, created_at ASC);
""";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
