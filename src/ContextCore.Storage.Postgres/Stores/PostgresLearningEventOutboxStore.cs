using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// Learning Loop Durable Outbox PostgreSQL 存储。实现 <see cref="ILearningEventOutboxStore"/>。
/// </summary>
/// <remarks>
/// 原子性：当 <see cref="EnqueueAsync"/> 接受非空 <see cref="IWriteTransactionScope"/>（必须是
/// <see cref="PostgresWriteTransactionScope"/>）时，outbox 行插入与调用方的事务共享同一 Postgres 事务。
/// <see cref="AcquirePendingAsync"/> 使用 SELECT ... FOR UPDATE SKIP LOCKED，
/// 让多 worker 并发调度不会重复取出同一记录——与 <see cref="PostgresRelationOutboxStore"/> 一致。
/// </remarks>
public sealed class PostgresLearningEventOutboxStore : PostgresStoreBase, ILearningEventOutboxStore
{
    public PostgresLearningEventOutboxStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(
        LearningEventOutboxRecord record,
        IWriteTransactionScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var normalized = NormalizeRecord(record);

        NpgsqlConnection? ownedConnection = null;
        NpgsqlTransaction? ownedTransaction = null;
        NpgsqlConnection connection;
        NpgsqlTransaction? transaction;
        try
        {
            if (scope is not null)
            {
                var pgScope = scope as PostgresWriteTransactionScope
                    ?? throw new InvalidOperationException(
                        "PostgresLearningEventOutboxStore 仅支持 PostgresWriteTransactionScope；" +
                        "请通过 PostgresWriteTransactionScopeFactory 创建事务作用域。");
                if (!scope.IsActive)
                {
                    throw new InvalidOperationException("事务作用域已结束（Commit/Rollback），无法继续写入。");
                }
                connection = pgScope.Connection;
                transaction = pgScope.Transaction;
            }
            else
            {
                ownedConnection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                ownedTransaction = await ownedConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                connection = ownedConnection;
                transaction = ownedTransaction;
            }

            using var command = connection.CreateCommand();
            if (transaction is not null)
            {
                command.Transaction = transaction;
            }
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.CommandText = $"""
INSERT INTO {Table("learning_event_outbox")} (
    event_id, workspace_id, collection_id, decision_id, payload,
    state, retry_count, max_retry_count,
    created_at, updated_at, processed_at,
    lease_owner, lease_expires_at, lease_token, last_error, dead_letter_reason)
VALUES (
    @event_id, @workspace_id, @collection_id, @decision_id, @payload,
    @state, @retry_count, @max_retry_count,
    @created_at, @updated_at, @processed_at,
    @lease_owner, @lease_expires_at, @lease_token, @last_error, @dead_letter_reason)
ON CONFLICT (event_id) DO NOTHING;
""";
            command.Parameters.AddWithValue("event_id", normalized.EventId);
            command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
            command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
            command.Parameters.AddWithValue("decision_id", normalized.DecisionId);
            var payloadParam = command.Parameters.Add("payload", NpgsqlDbType.Jsonb);
            payloadParam.Value = string.IsNullOrWhiteSpace(normalized.Payload)
                ? DBNull.Value
                : normalized.Payload;
            command.Parameters.AddWithValue("state", normalized.State);
            command.Parameters.AddWithValue("retry_count", normalized.RetryCount);
            command.Parameters.AddWithValue("max_retry_count", normalized.MaxRetryCount);
            command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
            command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
            command.Parameters.AddWithValue("processed_at", (object?)normalized.ProcessedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("lease_owner", (object?)normalized.LeaseOwner ?? DBNull.Value);
            command.Parameters.AddWithValue("lease_expires_at", (object?)normalized.LeaseExpiresAt ?? DBNull.Value);
            command.Parameters.AddWithValue("lease_token", (object?)normalized.LeaseToken ?? DBNull.Value);
            command.Parameters.AddWithValue("last_error", (object?)normalized.LastError ?? DBNull.Value);
            command.Parameters.AddWithValue("dead_letter_reason", (object?)normalized.DeadLetterReason ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                try { await ownedTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* 不掩盖原始异常 */ }
            }
            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            }
            if (ownedConnection is not null)
            {
                await ownedConnection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LearningEventOutboxRecord>> AcquirePendingAsync(
        int limit,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (limit <= 0) return Array.Empty<LearningEventOutboxRecord>();

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(leaseDuration);
        // P0-8：每次 AcquirePending 生成唯一 lease_token，写入数据库并随记录返回。
        // 后续 MarkAcked / MarkFailed / RenewLease 必须回传此 token，store 通过 CAS 校验
        // 仅持有者可 Ack/Nack/Renew——防止旧 Worker 在 lease 过期被抢占后越权 Ack 新 Worker 的 lease。
        var leaseToken = Guid.NewGuid().ToString("N");

        await using var selectCmd = connection.CreateCommand();
        selectCmd.Transaction = transaction;
        selectCmd.CommandTimeout = Options.CommandTimeoutSeconds;
        selectCmd.Parameters.AddWithValue("now", now);
        selectCmd.Parameters.AddWithValue("lease_owner", owner);
        selectCmd.Parameters.AddWithValue("lease_expires_at", leaseUntil);
        selectCmd.Parameters.AddWithValue("lease_token", leaseToken);
        selectCmd.Parameters.AddWithValue("updated_at", now);
        selectCmd.Parameters.AddWithValue("limit", limit);
        selectCmd.CommandText = $$"""
WITH pending AS (
    SELECT event_id FROM {{Table("learning_event_outbox")}}
    WHERE state = 'Pending'
       OR (state = 'Processing' AND lease_expires_at IS NOT NULL AND lease_expires_at <= @now)
    ORDER BY created_at ASC
    LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE {{Table("learning_event_outbox")}}
SET state = 'Processing',
    lease_owner = @lease_owner,
    lease_expires_at = @lease_expires_at,
    lease_token = @lease_token,
    updated_at = @updated_at
FROM pending
WHERE {{Table("learning_event_outbox")}}.event_id = pending.event_id
RETURNING
    {{Table("learning_event_outbox")}}.event_id, workspace_id, collection_id, decision_id, payload::text,
    state, retry_count, max_retry_count,
    created_at, updated_at, processed_at,
    lease_owner, lease_expires_at, lease_token, last_error, dead_letter_reason;
""";

        var results = new List<LearningEventOutboxRecord>();
        await using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(ReadRecord(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    /// <inheritdoc />
    public async Task<bool> MarkAckedAsync(
        string eventId,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var now = DateTimeOffset.UtcNow;
        // P0-8：CAS——仅当 state=Processing 且 lease_token 匹配时才转为 Acked。
        // 0 行受影响表示 lease 已被其他 worker 抢占或已 Ack/Nack——调用方应放弃该记录。
        command.CommandText = $@"
UPDATE {Table("learning_event_outbox")}
SET state = 'Acked',
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL,
    processed_at = @processed_at,
    updated_at = @updated_at
WHERE event_id = @event_id
  AND lease_token = @lease_token
  AND state = 'Processing';";
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("processed_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> MarkFailedAsync(
        string eventId,
        string leaseToken,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var now = DateTimeOffset.UtcNow;
        // P0-8：CAS——仅当 state=Processing 且 lease_token 匹配时才转换为 DeadLettered 或 Pending。
        // 达到 max 时转 DeadLettered（retry_count + 1 >= max_retry_count），未达到则回退 Pending 等待重试。
        // 0 行受影响表示 lease 已被其他 worker 抢占或已 Ack/Nack——调用方应放弃该记录。
        command.CommandText = $@"
UPDATE {Table("learning_event_outbox")}
SET retry_count = retry_count + 1,
    state = CASE WHEN retry_count + 1 >= max_retry_count THEN 'DeadLettered' ELSE 'Pending' END,
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL,
    updated_at = @updated_at,
    last_error = @error_message,
    dead_letter_reason = CASE WHEN retry_count + 1 >= max_retry_count THEN @error_message ELSE NULL END
WHERE event_id = @event_id
  AND lease_token = @lease_token
  AND state = 'Processing';";
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("error_message", errorMessage ?? string.Empty);
        command.Parameters.AddWithValue("updated_at", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> RenewLeaseAsync(
        string eventId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var now = DateTimeOffset.UtcNow;
        // P0-8：CAS——仅当 lease_token 匹配且 state=Processing 时才续约。
        // 用 lease_token 替代原 lease_owner 校验更严格（owner 名可能复用，token 全局唯一）。
        command.CommandText = $@"
UPDATE {Table("learning_event_outbox")}
SET lease_expires_at = @lease_expires_at,
    updated_at = @updated_at
WHERE event_id = @event_id
  AND lease_token = @lease_token
  AND state = 'Processing';";
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("lease_expires_at", now.Add(leaseDuration));
        command.Parameters.AddWithValue("updated_at", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, int>> CountByStateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT state, COUNT(*) FROM {Table("learning_event_outbox")}
GROUP BY state;
""";
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[reader.GetString(0)] = Convert.ToInt32(reader.GetInt64(1));
        }
        return counts;
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetLastSuccessAtAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT MAX(processed_at) FROM {Table("learning_event_outbox")}
WHERE state = 'Acked' AND processed_at IS NOT NULL;
""";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            null or DBNull => null,
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
            _ => null
        };
    }

    private static LearningEventOutboxRecord ReadRecord(NpgsqlDataReader reader)
    {
        return new LearningEventOutboxRecord
        {
            EventId = reader.GetString(reader.GetOrdinal("event_id")),
            WorkspaceId = reader.GetString(reader.GetOrdinal("workspace_id")),
            CollectionId = reader.GetString(reader.GetOrdinal("collection_id")),
            DecisionId = reader.GetString(reader.GetOrdinal("decision_id")),
            Payload = reader.GetString(reader.GetOrdinal("payload")),
            State = reader.GetString(reader.GetOrdinal("state")),
            RetryCount = reader.GetInt32(reader.GetOrdinal("retry_count")),
            MaxRetryCount = reader.GetInt32(reader.GetOrdinal("max_retry_count")),
            CreatedAt = ReadTimestamp(reader, "created_at"),
            UpdatedAt = ReadTimestamp(reader, "updated_at"),
            ProcessedAt = reader.IsDBNull(reader.GetOrdinal("processed_at"))
                ? null
                : ReadTimestamp(reader, "processed_at"),
            LeaseOwner = reader.IsDBNull(reader.GetOrdinal("lease_owner"))
                ? null
                : reader.GetString(reader.GetOrdinal("lease_owner")),
            LeaseExpiresAt = reader.IsDBNull(reader.GetOrdinal("lease_expires_at"))
                ? null
                : ReadTimestamp(reader, "lease_expires_at"),
            LeaseToken = reader.IsDBNull(reader.GetOrdinal("lease_token"))
                ? null
                : reader.GetString(reader.GetOrdinal("lease_token")),
            LastError = reader.IsDBNull(reader.GetOrdinal("last_error"))
                ? null
                : reader.GetString(reader.GetOrdinal("last_error")),
            DeadLetterReason = reader.IsDBNull(reader.GetOrdinal("dead_letter_reason"))
                ? null
                : reader.GetString(reader.GetOrdinal("dead_letter_reason"))
        };
    }

    /// <summary>
    /// 兼容 Npgsql 10+ 读取 timestamptz 列（可能返回 DateTime 或 DateTimeOffset）。
    /// </summary>
    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
            _ => throw new InvalidOperationException(
                $"Cannot read timestamp column '{columnName}': unexpected type {value?.GetType().Name ?? "null"}.")
        };
    }

    private static LearningEventOutboxRecord NormalizeRecord(LearningEventOutboxRecord record)
    {
        var now = DateTimeOffset.UtcNow;
        return new LearningEventOutboxRecord
        {
            EventId = string.IsNullOrWhiteSpace(record.EventId) ? Guid.NewGuid().ToString("N") : record.EventId,
            WorkspaceId = record.WorkspaceId ?? string.Empty,
            CollectionId = record.CollectionId ?? string.Empty,
            DecisionId = record.DecisionId,
            Payload = record.Payload,
            State = string.IsNullOrWhiteSpace(record.State) ? LearningEventOutboxStates.Pending : record.State,
            RetryCount = Math.Max(0, record.RetryCount),
            MaxRetryCount = record.MaxRetryCount > 0 ? record.MaxRetryCount : 5,
            CreatedAt = record.CreatedAt == default ? now : record.CreatedAt,
            UpdatedAt = record.UpdatedAt == default ? now : record.UpdatedAt,
            ProcessedAt = record.ProcessedAt,
            LeaseOwner = record.LeaseOwner,
            LeaseExpiresAt = record.LeaseExpiresAt,
            LeaseToken = record.LeaseToken,
            LastError = record.LastError,
            DeadLetterReason = record.DeadLetterReason
        };
    }
}
