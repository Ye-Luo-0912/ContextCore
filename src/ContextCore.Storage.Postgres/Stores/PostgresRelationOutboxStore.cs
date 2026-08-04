using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 关系写入 outbox 存储。实现 <see cref="IRelationOutboxStore"/>。
/// </summary>
/// <remarks>
/// 原子性：当 <see cref="EnqueueAsync"/> / <see cref="EnqueueBatchAsync"/> 接受非空
/// <see cref="IWriteTransactionScope"/>（必须是 <see cref="PostgresWriteTransactionScope"/>）时，
/// outbox 行插入与调用方的事务共享同一 Postgres 事务——commit 一起持久化，rollback 一起回滚。
/// 当 scope 为空时，使用独立短生命周期事务（best-effort，非原子）。
/// <see cref="AcquirePendingAsync"/> 使用 SELECT ... FOR UPDATE SKIP LOCKED，
/// 让多 worker 并发调度不会重复取出同一记录——与 <see cref="PostgresContextJobQueue"/> 一致。
/// </remarks>
public sealed class PostgresRelationOutboxStore : PostgresStoreBase, IRelationOutboxStore
{
    public PostgresRelationOutboxStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public Task EnqueueAsync(
        RelationOutboxRecord record,
        IWriteTransactionScope? scope = null,
        CancellationToken cancellationToken = default)
        => EnqueueBatchAsync([record], scope, cancellationToken);

    /// <inheritdoc />
    public async Task EnqueueBatchAsync(
        IReadOnlyList<RelationOutboxRecord> records,
        IWriteTransactionScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return;

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

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
                        "PostgresRelationOutboxStore 仅支持 PostgresWriteTransactionScope；" +
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

            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = NormalizeRecord(record);

                using var command = connection.CreateCommand();
                if (transaction is not null)
                {
                    command.Transaction = transaction;
                }
                command.CommandTimeout = Options.CommandTimeoutSeconds;
                command.CommandText = $"""
INSERT INTO {Table("relation_outbox")} (
    outbox_id, workspace_id, collection_id, relation_id, operation_kind,
    provenance, payload, state, retry_count, max_retry_count,
    created_at, updated_at, data)
VALUES (
    @outbox_id, @workspace_id, @collection_id, @relation_id, @operation_kind,
    @provenance, @payload, @state, @retry_count, @max_retry_count,
    @created_at, @updated_at, @data)
ON CONFLICT (outbox_id) DO UPDATE SET
    state = CASE WHEN {Table("relation_outbox")}.state IN ('Applied','Failed')
                 THEN {Table("relation_outbox")}.state ELSE EXCLUDED.state END,
    updated_at = EXCLUDED.updated_at,
    payload = EXCLUDED.payload,
    data = EXCLUDED.data;
""";
                command.Parameters.AddWithValue("outbox_id", normalized.OutboxId);
                command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
                command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
                command.Parameters.AddWithValue("relation_id", normalized.RelationId);
                command.Parameters.AddWithValue("operation_kind", normalized.OperationKind.ToString());
                command.Parameters.AddWithValue("provenance", normalized.Provenance);
                command.Parameters.AddWithValue("state", normalized.State);
                command.Parameters.AddWithValue("retry_count", normalized.RetryCount);
                command.Parameters.AddWithValue("max_retry_count", normalized.MaxRetryCount);
                command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
                command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
                // payload 单独序列化为 jsonb 列（方便 worker 直接读取 payload 字段）
                var payloadParam = command.Parameters.Add("payload", NpgsqlTypes.NpgsqlDbType.Jsonb);
                if (normalized.Payload is null)
                {
                    payloadParam.Value = DBNull.Value;
                }
                else
                {
                    payloadParam.Value = Serializer.Serialize(normalized.Payload);
                }
                AddJson(command, "data", normalized);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

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
    public async Task<IReadOnlyList<RelationOutboxRecord>> AcquirePendingAsync(
        int limit,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (limit <= 0) return Array.Empty<RelationOutboxRecord>();

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(leaseDuration);

        // SELECT ... FOR UPDATE SKIP LOCKED 模式与 PostgresContextJobQueue.AcquireLeaseAsync 一致。
        // 取出 state=Pending 或 Dispatched 但租约过期的记录。
        await using var selectCmd = connection.CreateCommand();
        selectCmd.Transaction = transaction;
        selectCmd.CommandTimeout = Options.CommandTimeoutSeconds;
        selectCmd.Parameters.AddWithValue("now", now);
        selectCmd.Parameters.AddWithValue("lease_owner", owner);
        selectCmd.Parameters.AddWithValue("lease_expires_at", leaseUntil);
        selectCmd.Parameters.AddWithValue("last_heartbeat_at", now);
        selectCmd.Parameters.AddWithValue("updated_at", now);
        selectCmd.Parameters.AddWithValue("limit", limit);
        selectCmd.CommandText = $$"""
WITH pending AS (
    SELECT outbox_id FROM {{Table("relation_outbox")}}
    WHERE state = 'Pending'
       OR (state = 'Dispatched' AND lease_expires_at IS NOT NULL AND lease_expires_at <= @now)
    ORDER BY created_at ASC
    LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE {{Table("relation_outbox")}}
SET state = 'Dispatched',
    lease_owner = @lease_owner,
    lease_expires_at = @lease_expires_at,
    last_heartbeat_at = @last_heartbeat_at,
    updated_at = @updated_at,
    dispatched_at = @last_heartbeat_at,
    data = jsonb_set(
        jsonb_set(
            jsonb_set(
                jsonb_set(
                    jsonb_set(data, '{State}', '"Dispatched"'),
                    '{LeaseOwner}', to_jsonb(@lease_owner)),
                '{LeaseExpiresAt}', to_jsonb(@lease_expires_at)),
            '{LastHeartbeatAt}', to_jsonb(@last_heartbeat_at)),
        '{DispatchedAt}', to_jsonb(@last_heartbeat_at))
FROM pending
WHERE {{Table("relation_outbox")}}.outbox_id = pending.outbox_id
RETURNING {{Table("relation_outbox")}}.data;
""";

        var results = new List<RelationOutboxRecord>();
        await using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var json = reader.GetString(0);
                var record = Serializer.Deserialize<RelationOutboxRecord>(json);
                results.Add(record);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    /// <inheritdoc />
    public async Task<bool> MarkAppliedAsync(
        string outboxId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var now = DateTimeOffset.UtcNow;
        // CAS：仅当 state=Dispatched 时才转为 Applied。过期的 Mark（已被重试为 Pending/Failed）匹配 0 行。
        command.CommandText = $@"
UPDATE {Table("relation_outbox")}
SET state = 'Applied',
    lease_owner = NULL,
    lease_expires_at = NULL,
    last_heartbeat_at = NULL,
    applied_at = @applied_at,
    updated_at = @updated_at,
    data = jsonb_set(data, '{{State}}', '""Applied""')
WHERE outbox_id = @outbox_id
  AND state = 'Dispatched';";
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("applied_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> MarkFailedAsync(
        string outboxId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var now = DateTimeOffset.UtcNow;
        // CAS：仅当 state=Dispatched 时才转换为 Failed 或 Pending（取决于是否达到 max_retry_count）。
        // 达到 max 时转 Failed（retry_count + 1 >= max_retry_count），未达到则回退 Pending 等待重试。
        command.CommandText = $@"
UPDATE {Table("relation_outbox")}
SET retry_count = retry_count + 1,
    state = CASE WHEN retry_count + 1 >= max_retry_count THEN 'Failed' ELSE 'Pending' END,
    lease_owner = NULL,
    lease_expires_at = NULL,
    last_heartbeat_at = NULL,
    updated_at = @updated_at,
    last_error_message = @error_message,
    data = jsonb_set(
        jsonb_set(data, '{{RetryCount}}', to_jsonb(retry_count + 1)),
        '{{State}}',
        CASE WHEN retry_count + 1 >= max_retry_count THEN '""Failed""' ELSE '""Pending""' END::jsonb)
WHERE outbox_id = @outbox_id
  AND state = 'Dispatched';";
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("error_message", errorMessage ?? string.Empty);
        command.Parameters.AddWithValue("updated_at", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> RenewHeartbeatAsync(
        string outboxId,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var now = DateTimeOffset.UtcNow;
        command.CommandText = $@"
UPDATE {Table("relation_outbox")}
SET lease_expires_at = @lease_expires_at,
    last_heartbeat_at = @last_heartbeat_at,
    updated_at = @updated_at
WHERE outbox_id = @outbox_id
  AND lease_owner = @lease_owner
  AND state = 'Dispatched';";
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("lease_owner", owner);
        command.Parameters.AddWithValue("lease_expires_at", now.Add(leaseDuration));
        command.Parameters.AddWithValue("last_heartbeat_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> RenewHeartbeatBatchAsync(
        IReadOnlyList<RelationOutboxHeartbeat> heartbeats,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heartbeats);
        if (heartbeats.Count == 0)
        {
            return Array.Empty<string>();
        }
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var outboxIds = new string[heartbeats.Count];
        var owners = new string[heartbeats.Count];
        for (var i = 0; i < heartbeats.Count; i++)
        {
            var heartbeat = heartbeats[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(heartbeat.OutboxId);
            ArgumentException.ThrowIfNullOrWhiteSpace(heartbeat.Owner);
            outboxIds[i] = heartbeat.OutboxId;
            owners[i] = heartbeat.Owner;
        }

        // 单条 SQL 批量续约：与单条路径同校验（lease_owner 匹配且 state='Dispatched'），
        // RETURNING 返回成功续约的 outbox_id；未返回的即失败（租约被抢占或状态已改变）。
        var renewedIds = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $@"
UPDATE {Table("relation_outbox")}
SET lease_expires_at = @lease_expires_at,
    last_heartbeat_at = @last_heartbeat_at,
    updated_at = @updated_at
FROM unnest(@outbox_ids, @owners) AS req(outbox_id, lease_owner)
WHERE {Table("relation_outbox")}.outbox_id = req.outbox_id
  AND {Table("relation_outbox")}.lease_owner = req.lease_owner
  AND {Table("relation_outbox")}.state = 'Dispatched'
RETURNING {Table("relation_outbox")}.outbox_id;";
        command.Parameters.AddWithValue("outbox_ids", outboxIds);
        command.Parameters.AddWithValue("owners", owners);
        command.Parameters.AddWithValue("lease_expires_at", now.Add(leaseDuration));
        command.Parameters.AddWithValue("last_heartbeat_at", now);
        command.Parameters.AddWithValue("updated_at", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            renewedIds.Add(reader.GetString(0));
        }

        var failed = new List<string>(heartbeats.Count - renewedIds.Count);
        foreach (var heartbeat in heartbeats)
        {
            if (!renewedIds.Contains(heartbeat.OutboxId))
            {
                failed.Add(heartbeat.OutboxId);
            }
        }
        return failed;
    }

    /// <inheritdoc />
    public async Task<int> CountStaleLeasesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT COUNT(*) FROM {Table("relation_outbox")}
WHERE state = 'Dispatched'
  AND lease_expires_at IS NOT NULL
  AND lease_expires_at <= @now;
""";
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, int>> CountByStateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT state, COUNT(*) FROM {Table("relation_outbox")}
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

    private static RelationOutboxRecord NormalizeRecord(RelationOutboxRecord record)
    {
        var now = DateTimeOffset.UtcNow;
        return new RelationOutboxRecord
        {
            OutboxId = string.IsNullOrWhiteSpace(record.OutboxId) ? Guid.NewGuid().ToString("N") : record.OutboxId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            RelationId = record.RelationId,
            OperationKind = record.OperationKind,
            Provenance = record.Provenance,
            Payload = record.Payload,
            State = string.IsNullOrWhiteSpace(record.State) ? RelationOutboxStates.Pending : record.State,
            RetryCount = Math.Max(0, record.RetryCount),
            MaxRetryCount = record.MaxRetryCount > 0 ? record.MaxRetryCount : 3,
            CreatedAt = record.CreatedAt == default ? now : record.CreatedAt,
            UpdatedAt = record.UpdatedAt == default ? now : record.UpdatedAt,
            DispatchedAt = record.DispatchedAt,
            AppliedAt = record.AppliedAt,
            LeaseOwner = record.LeaseOwner,
            LeaseExpiresAt = record.LeaseExpiresAt,
            LastHeartbeatAt = record.LastHeartbeatAt,
            LastErrorMessage = record.LastErrorMessage
        };
    }
}
