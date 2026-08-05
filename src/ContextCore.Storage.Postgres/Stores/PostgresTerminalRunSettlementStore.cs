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
/// </summary>
public sealed class PostgresTerminalRunSettlementStore : PostgresStoreBase, ITerminalRunSettlementStore
{
    /// <summary>结算尝试上限：超过后转死信（不再自动重试）。</summary>
    private const int MaxAttempts = 5;

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
            claimCmd.CommandText = $$"""
WITH pending AS (
    SELECT outbox_id FROM {{Table("terminal_run_settlement_outbox")}}
    WHERE status IN (0, 2)
      AND attempts < {{MaxAttempts}}
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
        // CAS——仅当 status=结算中 且 lease_token 匹配且租约未过期时记录失败；
        // 状态保持结算中（租约到期后可被重新领取重试），失败原因写入 last_error。
        command.CommandText = $"""
UPDATE {Table("terminal_run_settlement_outbox")}
SET last_error = @error_message,
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
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async ValueTask<int> DeadLetterExhaustedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 尝试耗尽（attempts >= MaxAttempts）且租约已过期的结算中条目 → 死信（不再自动重试）。
        command.CommandText = $"""
UPDATE {Table("terminal_run_settlement_outbox")}
SET status = 3,
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL,
    updated_at = @updated_at
WHERE status = 2
  AND attempts >= {MaxAttempts}
  AND lease_expires_at <= clock_timestamp();
""";
        command.Parameters.AddWithValue("updated_at", now);
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
