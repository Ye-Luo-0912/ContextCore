using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// 任务 F4：PostgreSQL 持久化 Agent Run Event Store。
/// 替代 <see cref="ContextCore.Core.Services.AgentRunRuntime.InMemoryAgentRunEventStore"/>，
/// 让 HA 场景下 Agent Run 事件流（哈希链）可跨进程持久化与崩溃恢复审计。
/// </summary>
/// <remarks>
/// 设计要点（参考 <see cref="PostgresToolDispatchJournal"/> 的 expected-state CAS）：
///   1. 表 <c>agent_run_events</c> 主键 (workspace_id, run_id, sequence)：
///      UNIQUE 约束防重序列号，保证事件流单调递增。
///   2. <see cref="AppendAsync"/> 使用事务 + SELECT MAX(sequence) 校验连续性：
///      - sequence 必须 = 当前 MAX + 1（链头为 0）；
///      - prev_chain_hash 必须 = 前一事件 content_hash（链头为 null）；
///      - 校验失败抛 <see cref="InvalidOperationException"/>。
///   3. <see cref="ReadAsync"/> 按 sequence 升序读取（fromSequence + take + LIMIT）。
///   4. <see cref="GetLastSequenceAsync"/> 通过 SELECT MAX(sequence) 实现；无事件返回 -1。
///   5. 完整 <see cref="AgentRunEvent"/> 对象保存在 <c>data jsonb</c>，由 store 反序列化。
///   6. G4：<see cref="AppendBatchAsync"/> 单事务批量插入事件 + Run 状态 CAS + checkpoint 游标，
///      将 Turn 内 8-15 次网络往返降为 1 次。
/// </remarks>
public sealed class PostgresAgentRunEventStore : PostgresStoreBase, IAgentRunEventStore, IPersistentAgentRunEventStore
{
    /// <summary>初始化 Postgres 持久化 Agent Run Event Store。</summary>
    public PostgresAgentRunEventStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// 优化：单条 SQL 原子完成"读取 last_event → 校验 sequence 连续性 + prev_hash 链接 → INSERT"。
    /// 相比旧版 SELECT+INSERT 两次往返，成功路径降为 1 次往返；校验失败或并发冲突时（affected=0）
    /// 再回退到 SELECT 给出精确错误信息（错误路径 2 次往返，可接受）。
    /// ON CONFLICT DO NOTHING 兜底防并发重序列号。
    /// </remarks>
    public async ValueTask AppendAsync(AgentRunEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 单条 SQL：CTE 读取 last_event；WHERE 校验 sequence 连续性 + prev_hash 链接；
        // ON CONFLICT DO NOTHING 兜底；RETURNING 用于判断是否插入成功。
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        insertCommand.CommandText = $"""
WITH last_event AS (
    SELECT sequence AS last_seq, content_hash AS last_hash
    FROM {Table("agent_run_events")}
    WHERE workspace_id = @workspace_id AND run_id = @run_id
    ORDER BY sequence DESC
    LIMIT 1
)
INSERT INTO {Table("agent_run_events")} (
    event_id, workspace_id, run_id, sequence,
    event_type, state, payload, content_hash, prev_chain_hash,
    occurred_at, data)
SELECT
    @event_id, @workspace_id, @run_id, @sequence,
    @event_type, @state, @payload, @content_hash, @prev_chain_hash,
    @occurred_at, @data
WHERE
    -- 链头：last_event 不存在 AND sequence=0 AND prev_hash IS NULL
    (NOT EXISTS (SELECT 1 FROM last_event) AND @sequence = 0 AND @prev_chain_hash IS NULL)
    OR
    -- 续链：last_event.sequence + 1 = @sequence AND last_event.content_hash IS NOT DISTINCT FROM @prev_chain_hash
    (EXISTS (SELECT 1 FROM last_event)
        AND (SELECT last_seq + 1 FROM last_event) = @sequence
        AND (SELECT last_hash FROM last_event) IS NOT DISTINCT FROM @prev_chain_hash)
ON CONFLICT (workspace_id, run_id, sequence) DO NOTHING
RETURNING sequence;
""";
        insertCommand.Parameters.AddWithValue("event_id", @event.EventId);
        insertCommand.Parameters.AddWithValue("workspace_id", @event.WorkspaceId);
        insertCommand.Parameters.AddWithValue("run_id", @event.RunId);
        insertCommand.Parameters.AddWithValue("sequence", @event.Sequence);
        insertCommand.Parameters.AddWithValue("event_type", (byte)@event.EventType);
        insertCommand.Parameters.AddWithValue("state", (byte)@event.State);
        insertCommand.Parameters.AddWithValue("payload", @event.Payload ?? string.Empty);
        insertCommand.Parameters.AddWithValue("content_hash", (object?)@event.ContentHash ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("prev_chain_hash", (object?)@event.PrevChainHash ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("occurred_at", @event.OccurredAt);
        AddJson(insertCommand, "data", @event);

        await using var reader = await insertCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return; // 插入成功（RETURNING 返回了 sequence）
        }

        // affected=0：校验失败或并发冲突。执行 SELECT 给出精确错误信息。
        string? expectedPrevHash;
        int expectedSequence;
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            selectCommand.CommandText = $"""
SELECT sequence, content_hash
FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
ORDER BY sequence DESC
LIMIT 1;
""";
            selectCommand.Parameters.AddWithValue("workspace_id", @event.WorkspaceId);
            selectCommand.Parameters.AddWithValue("run_id", @event.RunId);
            await using var selectReader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await selectReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expectedSequence = selectReader.GetInt32(0) + 1;
                expectedPrevHash = selectReader.IsDBNull(1) ? null : selectReader.GetString(1);
            }
            else
            {
                expectedSequence = 0;
                expectedPrevHash = null;
            }
        }

        if (@event.Sequence != expectedSequence)
        {
            throw new InvalidOperationException(
                $"事件 Sequence 不连续：workspace_id={@event.WorkspaceId}, run_id={@event.RunId}。" +
                $"期望={expectedSequence}，实际={@event.Sequence}。" +
                $"事件流必须从 0 开始单调递增。");
        }

        if (!string.Equals(expectedPrevHash, @event.PrevChainHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"事件 PrevChainHash 不匹配：workspace_id={@event.WorkspaceId}, run_id={@event.RunId}。" +
                $"期望={expectedPrevHash ?? "<null>"}，实际={@event.PrevChainHash ?? "<null>"}。" +
                $"事件哈希链被破坏或乱序。");
        }

        // Sequence 与 hash 都匹配但仍插入失败 → 并发写入已抢占同 sequence
        throw new InvalidOperationException(
            $"事件 Sequence 冲突：workspace_id={@event.WorkspaceId}, run_id={@event.RunId}, sequence={@event.Sequence}。" +
            $"并发写入导致序列号竞争；请重试 AppendAsync。");
    }

    /// <inheritdoc />
    /// <remarks>
    /// G4：单事务批量提交。BEGIN → 校验首事件连续性 → INSERT all events →
    /// UPDATE agent_runs state CAS + 可变字段 → UPDATE checkpoint 游标 → COMMIT。
    /// </remarks>
    public async ValueTask AppendBatchAsync(
        IReadOnlyList<AgentRunEvent> events,
        AgentRunStateUpdate? runStateUpdate,
        AgentCheckpointCursor? checkpointCursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        // 空批 + 无状态更新 → 直接返回
        if (events.Count == 0 && runStateUpdate is null)
        {
            return;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
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

                // 2. 批量插入事件（unnest 单 SQL；UNIQUE 约束兜底防并发重序列号）
                //    相比旧版 foreach 逐条 INSERT（N 次往返），unnest 将整批事件在单条 SQL 内展开，
                //    一次往返完成全部插入。ON CONFLICT DO NOTHING 兜底；RETURNING 计数检测冲突。
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
                AddArrayParameter(insertCommand, "sequences", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, sequences);
                AddArrayParameter(insertCommand, "event_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Smallint, eventTypes);
                AddArrayParameter(insertCommand, "states", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Smallint, states);
                AddTextArray(insertCommand, "payloads", payloads);
                AddNullableTextArray(insertCommand, "content_hashes", contentHashes);
                AddNullableTextArray(insertCommand, "prev_chain_hashes", prevChainHashes);
                AddArrayParameter(insertCommand, "occurred_ats", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.TimestampTz, occurredAts);
                AddArrayParameter(insertCommand, "datas", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Jsonb, datas);

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
                        $"并发写入导致序列号竞争；请重试 AppendBatchAsync。");
                }
            }

            // 3. Run 状态 CAS + 可变字段更新（若提供）
            if (runStateUpdate is not null)
            {
                var snapshot = runStateUpdate.RunSnapshot;
                var now = DateTimeOffset.UtcNow;
                var isTerminal = runStateUpdate.NewState == AgentRunState.Completed
                                 || runStateUpdate.NewState == AgentRunState.Failed
                                 || runStateUpdate.NewState == AgentRunState.Cancelled;

                await using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                var setFinished = isTerminal ? ", finished_at = @finished_at" : string.Empty;
                updateCommand.CommandText = $"""
UPDATE {Table("agent_runs")}
SET state = @new_state,
    turn = @turn,
    updated_at = @updated_at,
    failure_reason = @failure_reason,
    final_answer = @final_answer,
    turn_budget_json = @turn_budget_json,
    cost_budget_json = @cost_budget_json,
    data = @data{setFinished}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND state = @expected_state;
""";
                updateCommand.Parameters.AddWithValue("workspace_id", runStateUpdate.WorkspaceId);
                updateCommand.Parameters.AddWithValue("run_id", runStateUpdate.RunId);
                updateCommand.Parameters.AddWithValue("expected_state", (byte)runStateUpdate.ExpectedCurrentState);
                updateCommand.Parameters.AddWithValue("new_state", (byte)runStateUpdate.NewState);
                updateCommand.Parameters.AddWithValue("turn", snapshot.Turn);
                updateCommand.Parameters.AddWithValue("updated_at", now);
                updateCommand.Parameters.AddWithValue("failure_reason", (object?)snapshot.FailureReason ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("final_answer", (object?)snapshot.FinalAnswer ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("turn_budget_json", snapshot.TurnBudget is null
                    ? DBNull.Value
                    : JsonSerializer.Serialize(snapshot.TurnBudget));
                updateCommand.Parameters.AddWithValue("cost_budget_json", snapshot.CostBudget is null
                    ? DBNull.Value
                    : JsonSerializer.Serialize(snapshot.CostBudget));
                AddJson(updateCommand, "data", snapshot);
                if (isTerminal)
                {
                    updateCommand.Parameters.AddWithValue("finished_at", now);
                }

                var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (affected == 0)
                {
                    // CAS 失败：检查行是否存在以区分"逆退/已推进"与"Run 不存在"
                    await using var selectCommand = connection.CreateCommand();
                    selectCommand.Transaction = transaction;
                    selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                    selectCommand.CommandText = $"""
SELECT state FROM {Table("agent_runs")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
LIMIT 1;
""";
                    selectCommand.Parameters.AddWithValue("workspace_id", runStateUpdate.WorkspaceId);
                    selectCommand.Parameters.AddWithValue("run_id", runStateUpdate.RunId);
                    await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var currentState = (AgentRunState)reader.GetByte(0);
                        throw new InvalidOperationException(
                            $"Agent Run 状态机 CAS 失败（批量提交）：workspace_id={runStateUpdate.WorkspaceId}, run_id={runStateUpdate.RunId}。" +
                            $"期望当前状态={runStateUpdate.ExpectedCurrentState}，实际={currentState}。" +
                            $"状态已被其他实例推进或不可逆退。");
                    }

                    throw new InvalidOperationException(
                        $"Agent Run 不存在（批量提交）：workspace_id={runStateUpdate.WorkspaceId}, run_id={runStateUpdate.RunId}。" +
                        $"无法推进状态机（缺失 Run 元数据）。");
                }
            }

            // 4. Checkpoint 游标更新（若提供）
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

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* 不掩盖原始异常 */ }
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentRunEvent>> ReadAsync(
        string workspaceId,
        string runId,
        int fromSequence = 0,
        int take = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        if (fromSequence < 0)
        {
            fromSequence = 0;
        }

        if (take <= 0)
        {
            take = 1000;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND sequence >= @from_sequence
ORDER BY sequence ASC
LIMIT @take;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("from_sequence", fromSequence);
        command.Parameters.AddWithValue("take", take);
        return await ExecuteReaderJsonAsync<AgentRunEvent>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> GetLastSequenceAsync(string workspaceId, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT COALESCE(MAX(sequence), -1)
FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id AND run_id = @run_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        // COALESCE 已处理 null → -1；DB 端返回 long/int 统一转 int
        return result is long l ? (int)l : (result is int i ? i : -1);
    }
}
