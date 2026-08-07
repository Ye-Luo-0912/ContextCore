using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Agent Run 提交器：将 <see cref="AgentRunCommit"/>（事件流 + 状态 CAS +
/// checkpoint + 结算意图）作为一次原子事务落库，是 Agent Actor 主路径与 Event Store 批量追加
/// 的统一提交入口。
/// </summary>
/// <remarks>
/// 单事务提交顺序（与原 <see cref="PostgresAgentRunEventStore.AppendBatchAsync"/> 一致）：
/// 校验首事件连续性 → unnest 批量插入事件 → 状态 CAS（+finished_at + 结算 outbox）→
/// checkpoint 游标与本体 → COMMIT → 事件推送通知。
/// 终态语义（finished_at / 结算 outbox）统一来自 <see cref="AgentRunStateSemantics"/>。
/// </remarks>
public sealed class PostgresAgentRunCommitter : PostgresStoreBase, IPersistentAgentRunCommitter
{
    private readonly IAgentRunEventNotifier? _notifier;

    /// <summary>初始化 Postgres Agent Run 提交器。</summary>
    public PostgresAgentRunCommitter(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner,
        IAgentRunEventNotifier? notifier = null)
        : base(connectionFactory, serializer, migrationRunner)
    {
        _notifier = notifier;
    }

    /// <inheritdoc />
    public async ValueTask CommitAsync(AgentRunCommit commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(commit.Events);

        // 空批 + 无状态更新 → 直接返回（无提交内容）。
        if (commit.Events.Count == 0 && commit.NewRunSnapshot is null)
        {
            return;
        }

        // 状态 CAS 前件与目标快照必须同时提供或同时为 null。
        if ((commit.ExpectedCurrentState is null) != (commit.NewRunSnapshot is null))
        {
            throw new ArgumentException(
                "AgentRunCommit 的 ExpectedCurrentState 与 NewRunSnapshot 必须同时提供或同时为 null。",
                nameof(commit));
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var events = commit.Events;

            // 1. 校验首事件 Sequence 连续性 + PrevChainHash 链接（仅在有事件时）
            if (events.Count > 0)
            {
                var first = events[0];
                string? expectedPrevHash;
                int expectedSequence;
                await using (var selectCommand = connection.CreateCommand())
                {
                    selectCommand.Transaction = transaction;
                    selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                    selectCommand.CommandText = $"""
SELECT sequence, content_hash
FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
ORDER BY sequence DESC
LIMIT 1;
""";
                    selectCommand.Parameters.AddWithValue("workspace_id", first.WorkspaceId);
                    selectCommand.Parameters.AddWithValue("run_id", first.RunId);
                    await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        expectedSequence = reader.GetInt32(0) + 1;
                        expectedPrevHash = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }
                    else
                    {
                        expectedSequence = 0;
                        expectedPrevHash = null;
                    }
                }

                if (events[0].Sequence != expectedSequence)
                {
                    throw new InvalidOperationException(
                        $"批量事件首事件 Sequence 不连续：workspace_id={first.WorkspaceId}, run_id={first.RunId}。" +
                        $"期望={expectedSequence}，实际={events[0].Sequence}。" +
                        $"事件流必须从 0 开始单调递增。");
                }

                if (!string.Equals(expectedPrevHash, events[0].PrevChainHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"批量事件首事件 PrevChainHash 不匹配：workspace_id={first.WorkspaceId}, run_id={first.RunId}。" +
                        $"期望={expectedPrevHash ?? "<null>"}，实际={events[0].PrevChainHash ?? "<null>"}。" +
                        $"事件哈希链被破坏或乱序。");
                }

                // 校验批量内哈希链：同 Run、Sequence 连续、PrevChainHash 链接、ContentHash 重算一致。
                for (var i = 0; i < events.Count; i++)
                {
                    var evt = events[i];
                    if (evt.WorkspaceId != first.WorkspaceId || evt.RunId != first.RunId)
                    {
                        throw new ArgumentException($"Batch event {i} belongs to different Run: {evt.RunId} != {first.RunId}");
                    }
                    if (i > 0 && evt.Sequence != events[i - 1].Sequence + 1)
                    {
                        throw new ArgumentException($"Batch event {i} Sequence not continuous: {events[i - 1].Sequence} -> {evt.Sequence}");
                    }
                    if (i > 0 && evt.PrevChainHash != events[i - 1].ContentHash)
                    {
                        throw new ArgumentException($"Batch event {i} PrevChainHash mismatch");
                    }
                    var expectedHash = ComputeContentHash(evt);
                    if (evt.ContentHash != expectedHash)
                    {
                        throw new ArgumentException($"Batch event {i} ContentHash inconsistent with Payload");
                    }
                }

                // 2. 批量插入事件（unnest 单 SQL；UNIQUE 约束兜底防并发重序列号）。
                cancellationToken.ThrowIfCancellationRequested();
                var eventCount = events.Count;
                var eventIds = new string[eventCount];
                var sequences = new int[eventCount];
                var eventTypes = new short[eventCount];
                var states = new short[eventCount];
                var payloads = new string[eventCount];
                var contentHashes = new string?[eventCount];
                var prevChainHashes = new string?[eventCount];
                var occurredAts = new DateTimeOffset[eventCount];
                var datas = new string[eventCount];
                for (var i = 0; i < eventCount; i++)
                {
                    var e = events[i];
                    eventIds[i] = e.EventId;
                    sequences[i] = e.Sequence;
                    eventTypes[i] = (short)e.EventType;
                    states[i] = (short)e.State;
                    payloads[i] = e.Payload ?? string.Empty;
                    contentHashes[i] = e.ContentHash;
                    prevChainHashes[i] = e.PrevChainHash;
                    occurredAts[i] = e.OccurredAt;
                    datas[i] = Serializer.Serialize(e);
                }

                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                insertCommand.CommandText = $"""
INSERT INTO {Table("agent_run_events")} (
    event_id, workspace_id, run_id, sequence,
    event_type, state, payload, content_hash, prev_chain_hash,
    occurred_at, data)
SELECT
    evt_id, @workspace_id, @run_id, evt_seq,
    evt_type, evt_state, evt_payload, evt_hash, evt_prev_hash,
    evt_occurred_at, evt_data::jsonb
FROM unnest(
    @event_ids::text[],
    @sequences::integer[],
    @event_types::smallint[],
    @states::smallint[],
    @payloads::text[],
    @content_hashes::text[],
    @prev_chain_hashes::text[],
    @occurred_ats::timestamptz[],
    @datas::jsonb[]
) AS t(evt_id, evt_seq, evt_type, evt_state, evt_payload, evt_hash, evt_prev_hash, evt_occurred_at, evt_data)
ON CONFLICT (workspace_id, run_id, sequence) DO NOTHING
RETURNING sequence;
""";
                insertCommand.Parameters.AddWithValue("workspace_id", first.WorkspaceId);
                insertCommand.Parameters.AddWithValue("run_id", first.RunId);
                AddTextArray(insertCommand, "event_ids", eventIds);
                AddArrayParameter(insertCommand, "sequences", NpgsqlDbType.Array | NpgsqlDbType.Integer, sequences);
                AddArrayParameter(insertCommand, "event_types", NpgsqlDbType.Array | NpgsqlDbType.Smallint, eventTypes);
                AddArrayParameter(insertCommand, "states", NpgsqlDbType.Array | NpgsqlDbType.Smallint, states);
                AddTextArray(insertCommand, "payloads", payloads);
                AddNullableTextArray(insertCommand, "content_hashes", contentHashes);
                AddNullableTextArray(insertCommand, "prev_chain_hashes", prevChainHashes);
                AddArrayParameter(insertCommand, "occurred_ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, occurredAts);
                AddArrayParameter(insertCommand, "datas", NpgsqlDbType.Array | NpgsqlDbType.Jsonb, datas);

                var insertedCount = 0;
                await using (var insertReader = await insertCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await insertReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        insertedCount++;
                    }
                }

                if (insertedCount != eventCount)
                {
                    // 部分或全部冲突：UNIQUE 约束触发 ON CONFLICT DO NOTHING。
                    throw new InvalidOperationException(
                        $"批量事件 Sequence 冲突：workspace_id={first.WorkspaceId}, run_id={first.RunId}。" +
                        $"期望插入 {eventCount} 条，实际插入 {insertedCount} 条。" +
                        $"并发写入导致序列号竞争；请重试 CommitAsync。");
                }
            }

            // 3. Run 状态 CAS + 可变字段更新（NewRunSnapshot 非 null 时）。
            // 提供 leaseToken + fencingToken 时，WHERE 追加 EXISTS 子查询校验 lease 仍由当前实例持有。
            if (commit.NewRunSnapshot is not null)
            {
                var snapshot = commit.NewRunSnapshot;
                var now = DateTimeOffset.UtcNow;
                // 终态语义统一来自 AgentRunStateSemantics：终态写 finished_at；有结算策略的
                // 终态在 CAS 成功后写结算 outbox（仅预留行存在才入队，exactly-once）。
                var semantics = AgentRunStateSemantics.Get(snapshot.State);
                var isTerminal = semantics.FinishedAtRequired;
                var requiresSettlement = semantics.QuotaSettlementPolicy != QuotaSettlementPolicy.None;
                var leaseValidated = commit.LeaseToken is not null && commit.FencingToken is not null;
                // 结算用量快照：显式提供优先，否则取 Run 快照的 CostBudget。
                var usageBudget = commit.UsageSnapshot ?? snapshot.CostBudget;

                await using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                var setFinished = isTerminal ? ", finished_at = @finished_at" : string.Empty;
                // 终态时把 FinishedAt 合并进 data jsonb（保持"state 列 == data JSON"单真源，
                // 与 Run Store 的 TransitionStateAsync 行为一致；调用方快照可能未显式携带 FinishedAt）。
                var dataMergeFinishedAt = isTerminal ? " || jsonb_build_object('FinishedAt', @finished_at)" : string.Empty;
                var leaseClause = leaseValidated
                    ? $" AND EXISTS (SELECT 1 FROM {Table("agent_run_leases")} l WHERE l.run_id = @run_id AND l.lease_token = @lease_token AND l.fencing_token = @fencing_token AND l.lease_expires_at > clock_timestamp())"
                    : string.Empty;
                updateCommand.CommandText = $"""
UPDATE {Table("agent_runs")}
SET state = @new_state,
    turn = @turn,
    updated_at = @updated_at,
    failure_reason = @failure_reason,
    final_answer = @final_answer,
    turn_budget_json = @turn_budget_json,
    cost_budget_json = @cost_budget_json,
    data = @data{dataMergeFinishedAt}{setFinished}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND state = @expected_state{leaseClause};
""";
                updateCommand.Parameters.AddWithValue("workspace_id", commit.WorkspaceId);
                updateCommand.Parameters.AddWithValue("run_id", commit.RunId);
                updateCommand.Parameters.AddWithValue("expected_state", (byte)commit.ExpectedCurrentState!.Value);
                updateCommand.Parameters.AddWithValue("new_state", (byte)snapshot.State);
                updateCommand.Parameters.AddWithValue("turn", snapshot.Turn);
                updateCommand.Parameters.AddWithValue("updated_at", now);
                updateCommand.Parameters.AddWithValue("failure_reason", (object?)snapshot.FailureReason ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("final_answer", (object?)snapshot.FinalAnswer ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("turn_budget_json", snapshot.TurnBudget is null
                    ? DBNull.Value
                    : JsonSerializer.Serialize(snapshot.TurnBudget));
                updateCommand.Parameters.AddWithValue("cost_budget_json", usageBudget is null
                    ? DBNull.Value
                    : JsonSerializer.Serialize(usageBudget));
                AddJson(updateCommand, "data", snapshot);
                if (isTerminal)
                {
                    updateCommand.Parameters.AddWithValue("finished_at", now);
                }
                if (leaseValidated)
                {
                    updateCommand.Parameters.AddWithValue("lease_token", commit.LeaseToken!);
                    updateCommand.Parameters.AddWithValue("fencing_token", commit.FencingToken!.Value);
                }

                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (affected == 0)
                {
                    // CAS 失败：检查行是否存在以区分"逆退/已推进/lease 失效"与"Run 不存在"
                    await using var selectCommand = connection.CreateCommand();
                    selectCommand.Transaction = transaction;
                    selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                    selectCommand.CommandText = $"""
SELECT state FROM {Table("agent_runs")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
LIMIT 1;
""";
                    selectCommand.Parameters.AddWithValue("workspace_id", commit.WorkspaceId);
                    selectCommand.Parameters.AddWithValue("run_id", commit.RunId);
                    await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var currentState = (AgentRunState)reader.GetByte(0);
                        // lease 校验失败时给出专门的错误信息（区分于状态 CAS 失败）
                        if (leaseValidated && currentState == commit.ExpectedCurrentState)
                        {
                            throw new InvalidOperationException(
                                $"Agent Run lease fencing 校验失败（批量提交）：workspace_id={commit.WorkspaceId}, run_id={commit.RunId}。" +
                                $"状态机前件匹配（{commit.ExpectedCurrentState}），但 lease_token/fencing_token 不匹配——" +
                                $"lease 已被其他实例抢占，应立即停止处理该 Run。");
                        }
                        throw new InvalidOperationException(
                            $"Agent Run 状态机 CAS 失败（批量提交）：workspace_id={commit.WorkspaceId}, run_id={commit.RunId}。" +
                            $"期望当前状态={commit.ExpectedCurrentState}，实际={currentState}。" +
                            $"状态已被其他实例推进或不可逆退。");
                    }

                    throw new InvalidOperationException(
                        $"Agent Run 不存在（批量提交）：workspace_id={commit.WorkspaceId}, run_id={commit.RunId}。" +
                        $"无法推进状态机（缺失 Run 元数据）。");
                }

                // CAS 成功：有结算策略的终态写结算 outbox（主路径统一入口——
                // Actor 经提交器落库终态时与 Run Store 的 TransitionStateAsync 一致，
                // 在同一事务内写 outbox，仅当预留行存在才入队，exactly-once）。
                if (requiresSettlement)
                {
                    await using var outboxCommand = connection.CreateCommand();
                    outboxCommand.Transaction = transaction;
                    outboxCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                    outboxCommand.CommandText = $"""
INSERT INTO {Table("terminal_run_settlement_outbox")} (
    workspace_id, run_id, reservation_id, terminal_state, created_at, updated_at)
SELECT @workspace_id, @run_id, @run_id, @new_state, @now, @now
WHERE EXISTS (
    SELECT 1 FROM {Table("workspace_quota_reservations")}
    WHERE reservation_id = @run_id
);
""";
                    outboxCommand.Parameters.AddWithValue("workspace_id", commit.WorkspaceId);
                    outboxCommand.Parameters.AddWithValue("run_id", commit.RunId);
                    outboxCommand.Parameters.AddWithValue("new_state", (byte)snapshot.State);
                    outboxCommand.Parameters.AddWithValue("now", now);
                    await outboxCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            // 4. Checkpoint 游标 + 本体（显式游标优先，否则从事件流尾部派生）。
            var checkpointCursor = commit.CheckpointCursor;
            if (checkpointCursor is null && commit.Checkpoint is not null && events.Count > 0)
            {
                checkpointCursor = new AgentCheckpointCursor
                {
                    WorkspaceId = commit.WorkspaceId,
                    RunId = commit.RunId,
                    CheckpointId = commit.Checkpoint.CheckpointId,
                    LastEventSequence = events[^1].Sequence
                };
            }

            if (checkpointCursor is not null)
            {
                await using var cursorCommand = connection.CreateCommand();
                cursorCommand.Transaction = transaction;
                cursorCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                cursorCommand.CommandText = $"""
UPDATE {Table("agent_runs")}
SET last_checkpoint_id = @checkpoint_id,
    last_checkpoint_sequence = @last_sequence
WHERE workspace_id = @workspace_id AND run_id = @run_id;
""";
                cursorCommand.Parameters.AddWithValue("workspace_id", checkpointCursor.WorkspaceId);
                cursorCommand.Parameters.AddWithValue("run_id", checkpointCursor.RunId);
                cursorCommand.Parameters.AddWithValue("checkpoint_id", checkpointCursor.CheckpointId);
                cursorCommand.Parameters.AddWithValue("last_sequence", checkpointCursor.LastEventSequence);
                await cursorCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // 5. Checkpoint 本体持久化 — 与事件 + 状态 CAS + 游标更新同事务原子提交。
            // 幂等 upsert（ON CONFLICT DO UPDATE）。
            if (commit.Checkpoint is not null)
            {
                var checkpointBody = commit.Checkpoint;
                await using var checkpointCommand = connection.CreateCommand();
                checkpointCommand.Transaction = transaction;
                checkpointCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                checkpointCommand.CommandText = $"""
INSERT INTO {Table("agent_checkpoints")} (
    workspace_id, collection_id, session_value, runtime_kind, checkpoint_id,
    turn_id, snapshot_id, created_at, state_json, data)
VALUES (
    @workspace_id, @collection_id, @session_value, @runtime_kind, @checkpoint_id,
    @turn_id, @snapshot_id, @created_at, @state_json, @data)
ON CONFLICT (workspace_id, checkpoint_id) DO UPDATE SET
    collection_id = EXCLUDED.collection_id,
    session_value = EXCLUDED.session_value,
    runtime_kind = EXCLUDED.runtime_kind,
    turn_id = EXCLUDED.turn_id,
    snapshot_id = EXCLUDED.snapshot_id,
    created_at = EXCLUDED.created_at,
    state_json = EXCLUDED.state_json,
    data = EXCLUDED.data;
""";
                checkpointCommand.Parameters.AddWithValue("workspace_id", checkpointBody.Session.WorkspaceId);
                checkpointCommand.Parameters.AddWithValue("collection_id", (object?)checkpointBody.Session.CollectionId ?? DBNull.Value);
                checkpointCommand.Parameters.AddWithValue("session_value", checkpointBody.Session.Value);
                checkpointCommand.Parameters.AddWithValue("runtime_kind", checkpointBody.Session.RuntimeKind.ToString());
                checkpointCommand.Parameters.AddWithValue("checkpoint_id", checkpointBody.CheckpointId);
                checkpointCommand.Parameters.AddWithValue("turn_id", (object?)checkpointBody.TurnId ?? DBNull.Value);
                checkpointCommand.Parameters.AddWithValue("snapshot_id", (object?)checkpointBody.SnapshotId ?? DBNull.Value);
                checkpointCommand.Parameters.AddWithValue("created_at", checkpointBody.CreatedAt);
                checkpointCommand.Parameters.AddWithValue("state_json", checkpointBody.StateJson ?? string.Empty);
                AddJson(checkpointCommand, "data", checkpointBody);
                await checkpointCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            // 事务 COMMIT 后推送通知，让 SSE 端点立即唤醒读取（push），无事件时回退 500ms 轮询。
            if (_notifier is not null && events.Count > 0)
            {
                var first = events[0];
                _notifier.Notify(first.WorkspaceId, first.RunId, events[^1].Sequence);
            }
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* 不掩盖原始异常 */ }
            throw;
        }
    }

    /// <summary>
    /// 计算事件 ContentHash（与 AgentRunEventChain.ComputeContentHash 一致）。
    /// SHA-256 of serialized event DTO with ContentHash excluded。
    /// </summary>
    private static string ComputeContentHash(AgentRunEvent evt)
    {
        var dto = new
        {
            evt.EventId,
            evt.RunId,
            evt.WorkspaceId,
            evt.Sequence,
            evt.EventType,
            evt.State,
            evt.Payload,
            evt.PrevChainHash,
            evt.OccurredAt
        };
        var json = JsonSerializer.Serialize(dto);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
