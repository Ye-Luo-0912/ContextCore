using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Run 终态结算 outbox store。
/// Run 推进终态时由 <see cref="PostgresAgentRunStore.TransitionStateAsync"/> 在状态转换
/// 事务内写入（仅当对应预留存在）；本 store 供结算 worker 按租约领取并标记结算结果。
/// 领取语义与 <see cref="PostgresLearningEventOutboxStore.AcquirePendingAsync"/> 对齐：
/// FOR UPDATE SKIP LOCKED + lease_token CAS，多实例并发安全（被锁行跳过，下轮再取）。
/// 结算按账本一致性设计：尝试次数不设上限，多次失败只转入卡住（低频重试），
/// 绝不放弃——放弃意味着预留与 reserved_tokens 永久占用 Workspace 可用额度。
/// </summary>
public sealed class PostgresTerminalRunSettlementStore : PostgresStoreBase, ITerminalRunSettlementStore
{
    /// <summary>尝试达到该次数后转入卡住（status=3，低频重试闸门）；此后仍无限重试。</summary>
    private const int StuckAttemptThreshold = 5;

    /// <summary>卡住状态的重试闸门：转入卡住后至少等待该时长才可被再次领取。</summary>
    private static readonly TimeSpan StuckRetryBackoff = TimeSpan.FromMinutes(15);

    /// <summary>初始化 Postgres 终态结算 outbox store。</summary>
    public PostgresTerminalRunSettlementStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TerminalSettlementEntry>> ClaimBatchAsync(
        int limit,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (limit <= 0)
        {
            return Array.Empty<TerminalSettlementEntry>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(leaseDuration > TimeSpan.Zero ? leaseDuration : TimeSpan.FromMinutes(5));
        // 每次领取生成唯一 lease_token，随条目返回；标记结算必须回传此 token（CAS），
        // 防止旧 worker 在租约过期被抢占后越权标记新 worker 领取的条目。
        var leaseToken = Guid.NewGuid().ToString("N");

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var claimCmd = connection.CreateCommand();
            claimCmd.Transaction = transaction;
            claimCmd.CommandTimeout = Options.CommandTimeoutSeconds;
            claimCmd.Parameters.AddWithValue("now", now);
            claimCmd.Parameters.AddWithValue("lease_owner", owner);
            claimCmd.Parameters.AddWithValue("lease_expires_at", leaseUntil);
            claimCmd.Parameters.AddWithValue("lease_token", leaseToken);
            claimCmd.Parameters.AddWithValue("updated_at", now);
            claimCmd.Parameters.AddWithValue("limit", limit);
            // 待结算 / 结算中租约过期 / 卡住且重试闸门已过 均可领取；
            // 尝试次数不设上限——卡住条目在闸门过后继续被领取，结算永不放弃。
            claimCmd.CommandText = $$"""
WITH pending AS (
    SELECT outbox_id FROM {{Table("terminal_run_settlement_outbox")}}
    WHERE status IN (0, 2, 3)
      AND (lease_expires_at IS NULL OR lease_expires_at <= @now)
    ORDER BY created_at ASC
    LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE {{Table("terminal_run_settlement_outbox")}}
SET status = 2,
    attempts = attempts + 1,
    lease_owner = @lease_owner,
    lease_expires_at = @lease_expires_at,
    lease_token = @lease_token,
    updated_at = @updated_at
FROM pending
WHERE {{Table("terminal_run_settlement_outbox")}}.outbox_id = pending.outbox_id
RETURNING
    {{Table("terminal_run_settlement_outbox")}}.outbox_id,
    workspace_id, run_id, reservation_id, terminal_state, lease_token;
""";

            var results = new List<TerminalSettlementEntry>();
            await using (var reader = await claimCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    results.Add(ReadEntry(reader));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return results;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkProcessedAsync(
        long outboxId,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // CAS——仅当 status=结算中 且 lease_token 匹配且租约未过期时才转为已结算。
        // 0 行受影响表示租约已被抢占或已结算——调用方应放弃该条目。
        command.CommandText = $"""
UPDATE {Table("terminal_run_settlement_outbox")}
SET status = 1,
    processed_at = @processed_at,
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL,
    updated_at = @updated_at
WHERE outbox_id = @outbox_id
  AND lease_token = @lease_token
  AND status = 2
  AND lease_expires_at > clock_timestamp();
""";
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("processed_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkFailedAsync(
        long outboxId,
        string leaseToken,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // CAS——仅当 status=结算中 且 lease_token 匹配且租约未过期时记录失败。
        // 尝试未达阈值：状态保持结算中（租约到期后可重新领取重试）。
        // 尝试达到阈值：转入卡住（status=3）并设置低频重试闸门（lease_expires_at =
        // 当前时间 + 卡住退避），闸门过后仍可被领取——结算永不放弃，只降低频率。
        command.CommandText = $"""
UPDATE {Table("terminal_run_settlement_outbox")}
SET last_error = @error_message,
    status = CASE WHEN attempts >= {StuckAttemptThreshold} THEN 3 ELSE 2 END,
    lease_expires_at = CASE
        WHEN attempts >= {StuckAttemptThreshold} THEN @stuck_retry_at
        ELSE lease_expires_at
    END,
    lease_owner = CASE WHEN attempts >= {StuckAttemptThreshold} THEN NULL ELSE lease_owner END,
    lease_token = CASE WHEN attempts >= {StuckAttemptThreshold} THEN NULL ELSE lease_token END,
    updated_at = @updated_at
WHERE outbox_id = @outbox_id
  AND lease_token = @lease_token
  AND status = 2
  AND lease_expires_at > clock_timestamp();
""";
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("error_message", errorMessage ?? string.Empty);
        command.Parameters.AddWithValue("updated_at", now);
        command.Parameters.AddWithValue("stuck_retry_at", now.Add(StuckRetryBackoff));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async ValueTask<int> TransitionStuckAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 兜底过渡：尝试达到阈值且租约已过期的结算中条目（worker 崩溃等未走 MarkFailed
        // 的路径）→ 卡住 + 低频重试闸门。卡住不是终点，闸门过后仍可被领取重试。
        command.CommandText = $"""
UPDATE {Table("terminal_run_settlement_outbox")}
SET status = 3,
    lease_owner = NULL,
    lease_expires_at = @stuck_retry_at,
    lease_token = NULL,
    updated_at = @updated_at
WHERE status = 2
  AND attempts >= {StuckAttemptThreshold}
  AND lease_expires_at <= clock_timestamp();
""";
        command.Parameters.AddWithValue("updated_at", now);
        command.Parameters.AddWithValue("stuck_retry_at", now.Add(StuckRetryBackoff));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> ReconcileSettlementGapsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        // 需结算的终态集合：终态且结算策略非 None（执行类转正 + 准入拒绝退回）。
        // 准入拒绝的 Run 无预留，对账 join 自然排除，仅作语义完整性保留。
        var terminalStates = Enum.GetValues<AgentRunState>()
            .Where(s => AgentRunStateSemantics.Get(s) is { IsTerminal: true, QuotaSettlementPolicy: not QuotaSettlementPolicy.None })
            .Select(s => (int)s)
            .ToArray();

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 终态 Run + 有效预留 + 无任何结算记录 → 补写待结算条目（status=0）。
        // 与正常写入路径同一结构：reservation_id = run_id；幂等——已有记录（含卡住）
        // 的 Run 不再补写，卡住条目由低频重试自行收敛。
        command.CommandText = $"""
INSERT INTO {Table("terminal_run_settlement_outbox")} (
    workspace_id, run_id, reservation_id, terminal_state, created_at, updated_at)
SELECT r.workspace_id, r.run_id, r.run_id, r.state, @now, @now
FROM {Table("agent_runs")} r
JOIN {Table("workspace_quota_reservations")} res
  ON res.workspace_id = r.workspace_id
 AND res.reservation_id = r.run_id
WHERE r.state = ANY(@terminal_states)
  AND NOT EXISTS (
      SELECT 1 FROM {Table("terminal_run_settlement_outbox")} o
      WHERE o.workspace_id = r.workspace_id
        AND o.run_id = r.run_id
  );
""";
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("terminal_states", terminalStates);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TerminalSettlementEntry ReadEntry(NpgsqlDataReader reader) => new()
    {
        OutboxId = reader.GetInt64(reader.GetOrdinal("outbox_id")),
        WorkspaceId = reader.GetString(reader.GetOrdinal("workspace_id")),
        RunId = reader.GetString(reader.GetOrdinal("run_id")),
        ReservationId = reader.GetString(reader.GetOrdinal("reservation_id")),
        TerminalState = (AgentRunState)reader.GetInt16(reader.GetOrdinal("terminal_state")),
        LeaseToken = reader.GetString(reader.GetOrdinal("lease_token"))
    };
}
