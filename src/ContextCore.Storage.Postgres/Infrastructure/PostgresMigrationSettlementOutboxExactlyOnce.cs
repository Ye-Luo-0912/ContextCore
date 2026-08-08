using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v69 → v70：终态结算 outbox exactly-once 唯一约束 + 结算事实冻结。
///
/// 1. UNIQUE(workspace_id, run_id)：Gap Reconciler 的 NOT EXISTS 在并发下
///    可能双插同一 Run 的结算条目（A 与 B 同时 NOT EXISTS=true 后同时 INSERT）。
///    唯一约束 + ON CONFLICT DO NOTHING 使 outbox 本身 exactly-once——
///    不依赖预检查的竞态窗口。
///
/// 2. 冻结结算事实列：ActualTokens / ActualCostUsd / UsageRevision /
///    FinalAttempt / SettlementPolicy。结算 worker 直接消费冻结值，
///    不再在结算时读取可变的 Run 实体（Run 归档/删除/损坏都不影响
///    已经形成的账务事实）。
///
/// 阶段：Online（加列 + 去重 + 唯一约束）。去重保留最早 created_at 的行。
/// </summary>
public sealed class PostgresMigrationSettlementOutboxExactlyOnce : IPostgresMigrationStep
{
    public string MigrationId => "0017_settlement_outbox_exactly_once";

    public string FromSchemaVersion => "cc-schema-v69";

    public string ToSchemaVersion => "cc-schema-v70";

    public string Description =>
        "terminal_run_settlement_outbox 增加 UNIQUE(workspace_id, run_id)（outbox 自身 exactly-once，"
        + "Gap Reconciler 并发双插阻断）并冻结结算事实列"
        + "（actual_tokens / actual_cost_usd / usage_revision / final_attempt / settlement_policy），"
        + "结算 worker 不再依赖读取可变 Run 实体。";

    public IReadOnlyList<PostgresMigrationStage> Stages { get; } =
    [
        PostgresMigrationStage.Online
    ];

    public async Task<string?> PreCheckAsync(
        NpgsqlConnection connection,
        PostgresOptions options,
        CancellationToken cancellationToken)
    {
        var table = PostgresNames.Table(options, "terminal_run_settlement_outbox");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        // 目标表不存在时无需执行：新数据库由基线 DDL 直接以新结构创建。
        command.CommandText = "SELECT to_regclass(@table_name)::text;";
        command.Parameters.AddWithValue("table_name", table);
        var tableExists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (tableExists is null or DBNull)
        {
            return null;
        }

        // UNIQUE(workspace_id, run_id) 已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_constraint c
            WHERE c.conrelid = to_regclass(@table_name)
              AND c.contype = 'u'
              AND array_length(c.conkey, 1) = 2
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
        var table = PostgresNames.Table(options, "terminal_run_settlement_outbox");
        var uniqueIndex = PostgresNames.Index(options, "terminal_run_settlement_outbox", "run");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = $"""
            -- 冻结结算事实列（幂等）
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS actual_tokens bigint NOT NULL DEFAULT 0;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS actual_cost_usd double precision NOT NULL DEFAULT 0;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS usage_revision integer NOT NULL DEFAULT 0;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS final_attempt integer NOT NULL DEFAULT 0;
            ALTER TABLE {table} ADD COLUMN IF NOT EXISTS settlement_policy smallint NOT NULL DEFAULT 0;

            -- 去重：Gap Reconciler 并发可能已插入重复 (workspace_id, run_id) 行，
            -- 保留 created_at 最早的一条（其余删除——重复条目语义等价，结算恰好一次）。
            DELETE FROM {table} o
            USING {table} o2
            WHERE o.workspace_id = o2.workspace_id
              AND o.run_id = o2.run_id
              AND o.outbox_id <> o2.outbox_id
              AND o.created_at > o2.created_at;

            -- 唯一约束：outbox 自身 exactly-once（与 ON CONFLICT DO NOTHING 配合）。
            DROP INDEX IF EXISTS {uniqueIndex};
            CREATE UNIQUE INDEX IF NOT EXISTS {uniqueIndex}
                ON {table} (workspace_id, run_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
