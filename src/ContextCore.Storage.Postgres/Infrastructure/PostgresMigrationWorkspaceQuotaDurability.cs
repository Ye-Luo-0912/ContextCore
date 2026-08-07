using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// v63 → v64：Workspace 配额持久化（生产多实例配额真相源）。
/// - workspace_quota_ledger：按 workspace 的配额周期状态（上限 / 已用 / 已预留 / 周期起点），
///   替代单进程字典——多节点各自计算配额的进程内实现不再成立；
/// - workspace_quota_reservations：持久化预留行（reservation_id 幂等），
///   节点重启不丢失预留；同一 Run 的幂等预留跨节点有效；
/// - terminal_run_settlement_outbox：Run 终态结算 outbox（exactly-once），
///   所有终态（Completed / Failed / Cancelled / LeaseLost / DeadLettered /
///   AdmissionRejected）统一由结算 worker 执行 Actualize 或 Release，
///   终结「仅取消端点释放配额、其余终态无结算入口」的路径。
/// 单个 Online 阶段：CREATE TABLE IF NOT EXISTS（幂等，非破坏性）。
/// 新数据库由基线 DDL 直接建好这三张表，PreCheck 跳过。
/// </summary>
public sealed class PostgresMigrationWorkspaceQuotaDurability : IPostgresMigrationStep
{
    public string MigrationId => "0011_workspace_quota_durability";

    public string FromSchemaVersion => "cc-schema-v63";

    public string ToSchemaVersion => "cc-schema-v64";

    public string Description =>
        "新增 workspace_quota_ledger / workspace_quota_reservations / terminal_run_settlement_outbox "
        + "三张表：workspace 配额周期状态、持久化预留与 Run 终态结算 outbox（多实例配额真相源 + exactly-once 结算）。";

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
        var table = PostgresNames.Table(options, "workspace_quota_ledger");
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        // 目标表已存在 = 已迁移（或新库基线已建）；返回 null 跳过。
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
        var ledger = PostgresNames.Table(options, "workspace_quota_ledger");
        var reservations = PostgresNames.Table(options, "workspace_quota_reservations");
        var outbox = PostgresNames.Table(options, "terminal_run_settlement_outbox");

        if (stage == PostgresMigrationStage.Online)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = options.CommandTimeoutSeconds;
            command.CommandText = $"""
-- workspace 配额周期状态表：每 workspace 一行，限额 + 已用 + 已预留 + 周期起点。
-- MaxTokens=0 / MaxCostUsd=0 表示无限制（与 WorkspaceQuota.IsTokenExhausted 语义一致）。
CREATE TABLE IF NOT EXISTS {ledger} (
    workspace_id text NOT NULL,
    max_tokens bigint NOT NULL DEFAULT 0,
    tokens_used bigint NOT NULL DEFAULT 0,
    reserved_tokens bigint NOT NULL DEFAULT 0,
    max_cost_usd double precision NOT NULL DEFAULT 0,
    cost_used_usd double precision NOT NULL DEFAULT 0,
    reserved_cost_usd double precision NOT NULL DEFAULT 0,
    period_seconds bigint NOT NULL DEFAULT 3600,
    period_started_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (workspace_id)
);

-- 持久化预留行：reservation_id 幂等（重复预留不重复占容量），
-- 跨节点共享（节点重启不丢失预留，另一节点可对同一预留执行 Release / Actualize）。
CREATE TABLE IF NOT EXISTS {reservations} (
    reservation_id text NOT NULL,
    workspace_id text NOT NULL,
    tokens bigint NOT NULL,
    cost_usd double precision NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (reservation_id)
);

-- 按 workspace 反查预留（释放/结算按 workspace 维度审计）。
CREATE INDEX IF NOT EXISTS {PostgresNames.Index(options, "workspace_quota_reservations", "workspace")}
    ON {reservations} (workspace_id, created_at ASC);

-- Run 终态结算 outbox：Run 推进终态时在状态转换事务内写入，
-- 结算 worker 按租约领取并执行 Actualize / Release（exactly-once）。
-- status：0 = 待结算，1 = 已结算，2 = 结算中（持有租约），3 = 卡住（低频无限重试，供运维排查）。
CREATE TABLE IF NOT EXISTS {outbox} (
    outbox_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    workspace_id text NOT NULL,
    run_id text NOT NULL,
    reservation_id text NOT NULL,
    terminal_state smallint NOT NULL,
    status smallint NOT NULL DEFAULT 0,
    attempts integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    processed_at timestamptz NULL,
    lease_owner text NULL,
    lease_token text NULL,
    lease_expires_at timestamptz NULL,
    last_error text NULL
);

-- 结算 worker 领取索引：按待结算 + 创建时间升序（最早优先）。
CREATE INDEX IF NOT EXISTS {PostgresNames.Index(options, "terminal_run_settlement_outbox", "status")}
    ON {outbox} (status, created_at ASC);
-- 过期租约回收索引：结算中但租约过期（worker 崩溃）可被重新领取。
CREATE INDEX IF NOT EXISTS {PostgresNames.Index(options, "terminal_run_settlement_outbox", "lease")}
    ON {outbox} (status, lease_expires_at);
""";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (stage == PostgresMigrationStage.ConstraintValidate)
        {
            // 无约束变更：占位（保持注册表阶段一致，重入安全）。
            await using var command = connection.CreateCommand();
            command.CommandTimeout = options.CommandTimeoutSeconds;
            command.CommandText = "SELECT 1;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
