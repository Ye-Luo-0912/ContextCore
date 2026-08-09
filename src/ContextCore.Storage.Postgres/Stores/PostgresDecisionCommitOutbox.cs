using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL Decision Commit Outbox：决策提交消息的 durable 队列（outbox 模式）。
/// 入队幂等（(workspace_id, decision_id) 唯一）；领取 FOR UPDATE SKIP LOCKED + 租约；
/// 未 Ack 条目崩溃后可重放——决策记录落库与物化意图不因进程崩溃丢失。
/// </summary>
public sealed class PostgresDecisionCommitOutbox : PostgresStoreBase, IDecisionCommitOutbox
{
    /// <summary>尝试达到该次数后转入死信（供运维排查；记录内容保留）。</summary>
    private const int MaxRetryCount = 5;

    public PostgresDecisionCommitOutbox(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async ValueTask EnqueueAsync(
        DecisionCommitOutboxRecord commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("decision_commits")} (
    workspace_id, collection_id, decision_id, commit_type, evidence_ref,
    payload, state, created_at, updated_at)
VALUES (
    @workspace_id, @collection_id, @decision_id, @commit_type, @evidence_ref,
    @payload, 0, @created_at, @created_at)
ON CONFLICT (workspace_id, decision_id) DO UPDATE SET
    commit_type = EXCLUDED.commit_type,
    evidence_ref = EXCLUDED.evidence_ref,
    payload = EXCLUDED.payload,
    state = 0,
    updated_at = EXCLUDED.updated_at;
""";
        command.Parameters.AddWithValue("workspace_id", commit.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", commit.CollectionId);
        command.Parameters.AddWithValue("decision_id", commit.DecisionId);
        command.Parameters.AddWithValue("commit_type", (short)commit.CommitType);
        command.Parameters.AddWithValue("evidence_ref", (object?)commit.EvidenceRef ?? DBNull.Value);
        AddJson(command, "payload", commit.Record);
        command.Parameters.AddWithValue("created_at", commit.CreatedAt == default ? DateTimeOffset.UtcNow : commit.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DecisionCommitOutboxRecord>> AcquirePendingAsync(
        int limit,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (limit <= 0)
        {
            return Array.Empty<DecisionCommitOutboxRecord>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(leaseDuration > TimeSpan.Zero ? leaseDuration : TimeSpan.FromMinutes(5));
        var leaseToken = Guid.NewGuid().ToString("N");

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("lease_owner", owner);
            command.Parameters.AddWithValue("lease_expires_at", leaseUntil);
            command.Parameters.AddWithValue("lease_token", leaseToken);
            command.Parameters.AddWithValue("limit", limit);
            command.CommandText = $$"""
WITH pending AS (
    SELECT outbox_id FROM {{Table("decision_commits")}}
    WHERE state IN (0, 2)
      AND (lease_expires_at IS NULL OR lease_expires_at <= @now)
    ORDER BY created_at ASC
    LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE {{Table("decision_commits")}}
SET state = 2,
    attempts = attempts + 1,
    lease_owner = @lease_owner,
    lease_expires_at = @lease_expires_at,
    lease_token = @lease_token,
    updated_at = @now
FROM pending
WHERE {{Table("decision_commits")}}.outbox_id = pending.outbox_id
RETURNING
    {{Table("decision_commits")}}.outbox_id,
    workspace_id, collection_id, decision_id, commit_type, evidence_ref, payload,
    state, lease_token;
""";

            var results = new List<DecisionCommitOutboxRecord>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    results.Add(new DecisionCommitOutboxRecord
                    {
                        OutboxId = reader.GetInt64(0),
                        WorkspaceId = reader.GetString(1),
                        CollectionId = reader.GetString(2),
                        DecisionId = reader.GetString(3),
                        CommitType = (DecisionCommitType)reader.GetInt16(4),
                        EvidenceRef = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Record = Serializer.Deserialize<ContextDecisionRecord>(reader.GetString(6))!,
                        State = reader.GetInt16(7),
                        LeaseToken = reader.GetString(8),
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return results;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<bool> AckAsync(
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
        command.CommandText = $"""
UPDATE {Table("decision_commits")}
SET state = 1,
    processed_at = @processed_at,
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL,
    updated_at = @updated_at
WHERE outbox_id = @outbox_id
  AND lease_token = @lease_token
  AND state = 2
  AND lease_expires_at > clock_timestamp();
""";
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("processed_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask MarkFailedAsync(
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
        command.CommandText = $"""
UPDATE {Table("decision_commits")}
SET last_error = @error_message,
    state = CASE WHEN attempts >= {MaxRetryCount} THEN 3 ELSE 2 END,
    lease_owner = NULL,
    lease_expires_at = NULL,
    lease_token = NULL,
    updated_at = @updated_at
WHERE outbox_id = @outbox_id
  AND lease_token = @lease_token
  AND state = 2
  AND lease_expires_at > clock_timestamp();
""";
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("error_message", errorMessage ?? string.Empty);
        command.Parameters.AddWithValue("updated_at", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
