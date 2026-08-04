using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v58 → v59：agent_runs 追加 Scheduler Claim Lease 列，并把既有 Created Run 转换为 Queued。
/// - 新增 claim_owner / claim_token / claim_expires_at 列（P0-8：Scheduler Claim 真正落库——
///   领取后写入 claim 持有者/令牌/过期时间，事务提交后行锁释放也不允许其他节点重复领取；
///   节点在领取后崩溃时，claim 过期后其他节点可重新领取）。
/// - Created（state=0）→ Queued（state=21）批量转换（P0-6 Admission 边界）：
///   新语义下 Created 只属于 InMemory/FileSystem provider；Postgres 持久化的待调度 Run
///   必须处于 Queued（Admission 已通过），否则 Durable Scheduler 不再领取（防止绕过配额）。
///   state 列与 data JSON.State 同步更新（单真源约束）。
/// 两个阶段：
///   Online：ADD COLUMN IF NOT EXISTS（幂等）+ Created → Queued 数据转换（幂等）；
///   ConstraintValidate：无约束变更（占位，保持与注册表阶段一致）。
/// 新数据库由基线 DDL 直接建好这些列，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationAgentRunClaimLease : IPostgresMigrationStep
{
    public string MigrationId => "0006_agent_run_claim_lease";

    public string FromSchemaVersion => "cc-schema-v58";

    public string ToSchemaVersion => "cc-schema-v59";

    public string Description =>
        "agent_runs 追加 Scheduler Claim Lease 列（claim_owner/claim_token/claim_expires_at，P0-8），"
        + "并将既有 Created Run 转换为 Queued（P0-6 Admission 边界：Created 不再进入 Claimer 候选集）。";

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

        // claim_owner 列已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
        command.CommandText = """
            SELECT 1
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.oid = to_regclass(@table_name)
              AND a.attname = 'claim_owner'
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
        var table = PostgresNames.Table(options, "agent_runs");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        if (stage == PostgresMigrationStage.Online)
        {
            command.CommandText = $"""
                ALTER TABLE {table} ADD COLUMN IF NOT EXISTS claim_owner text NULL;
                ALTER TABLE {table} ADD COLUMN IF NOT EXISTS claim_token text NULL;
                ALTER TABLE {table} ADD COLUMN IF NOT EXISTS claim_expires_at timestamptz NULL;

                -- P0-6 Admission 边界：既有 Created Run（state=0）转换为 Queued（state=21）。
                -- 新语义下 Created 只属于 InMemory/FileSystem provider；持久化待调度 Run 必须
                -- 处于 Queued，否则 Durable Scheduler 永不领取（防止绕过配额直接执行）。
                -- state 列与 data JSON.State 同步更新（单真源约束，与 TransitionStateAsync 同一模式）。
                UPDATE {table}
                SET state = 21,
                    updated_at = clock_timestamp(),
                    data = data || jsonb_build_object('State', to_jsonb('Queued'::text), 'UpdatedAt', to_jsonb(clock_timestamp()))
                WHERE state = 0;
                """;
        }
        else if (stage == PostgresMigrationStage.ConstraintValidate)
        {
            // 无约束变更：占位（保持注册表阶段一致，重入安全）。
            command.CommandText = "SELECT 1;";
        }
        else
        {
            return;
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
