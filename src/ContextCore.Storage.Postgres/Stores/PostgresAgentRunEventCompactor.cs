using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Agent Run 事件流快照与压缩（Event Snapshot &amp; Compaction）。
/// </summary>
/// <remarks>
/// 设计要点：
/// 1. 折叠前缀 [0..upToSequence]：锚点事件（sequence = upToSequence）保留在热表
/// <c>agent_run_events</c> 作为新链头，前缀事件（sequence &lt; upToSequence）归档到
/// <c>agent_run_events_archive</c> 后从热表删除——哈希链完整性不受影响
/// （后续 AppendAsync 的 prev_chain_hash 校验基准 = 锚点 content_hash）。
/// 2. 快照写入 <c>agent_run_event_snapshots</c>（per-run 单行，UPSERT 幂等）。
/// 3. 压缩幂等：重复调用同一 upToSequence 返回相同结果；upToSequence 超过当前最后
/// sequence 时自动钳制到最后事件；锚点不晚于既有快照时无新事件可折叠，直接返回。
/// 4. 归档在数据库侧完成（<c>INSERT INTO archive SELECT ... WHERE sequence &lt; anchor
/// ON CONFLICT DO NOTHING</c> + DELETE），不经过应用层反序列化——热表 <c>data</c> 列
/// 原样拷贝到归档表，避免大批量事件的应用层读取与再序列化。
/// 5. 快照增量重建：以既有快照为基准，只重放热表新增增量事件
/// （<see cref="AgentRunEventStateRebuilder.RebuildFromEvent"/>），无快照时全量重建；
/// 避免重复压缩同一 Run 时对折叠前缀的重复全量读取，并保证增量折叠不丢已归档前缀
/// 的状态。压缩期间对 agent_runs 行加 <c>FOR UPDATE</c>，串行化同 Run 的并发压缩。
/// 6. 快照 state_json 存 <see cref="ContextCore.Abstractions.AgentRunRecoverableState"/>
/// （完整可恢复状态），Recovery 按 "Snapshot → validate anchor → replay hot delta" 恢复。
/// 7. <b>自动压缩仍仅限终态 Run（保守策略）</b>：<see cref="FindCandidatesAsync"/>
/// 只选取终态（或重试已耗尽）的 Run。可恢复快照已支持非终态 Run 的崩溃恢复，但保留
/// 终态限制避免意外压缩活跃 Run；操作员端点同样仅允许终态 Run 压缩。
/// </remarks>
public sealed class PostgresAgentRunEventCompactor : PostgresStoreBase, IAgentRunEventCompactor
{
    /// <summary>
    /// 判定 Run 是否可压缩（仅限终态）：终态语义统一来自
    /// <see cref="AgentRunStateSemantics.IsCompactable"/>（与状态机 / Event Store /
    /// Settlement 共享同一语义来源）。可压缩状态不会再被 Recovery 重放；
    /// Failed 单独处理（重试耗尽才可压缩，因为仍可重试的 Failed 会被调度器
    /// 重新领取并全量重放事件流）。
    /// </summary>
    public static bool IsCompactableRunState(AgentRunState state, int retryCount, int maxRetries)
        => AgentRunStateSemantics.IsCompactable(state, retryCount, maxRetries);

    /// <summary>初始化 Postgres 持久化 Agent Run 事件流压缩器。</summary>
    public PostgresAgentRunEventCompactor(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// 单事务原子执行：钳制锚点 → 锁定 run 行 → 读既有快照 → 数据库侧归档折叠前缀
    /// （INSERT...SELECT）→ 读热表增量 → 删除折叠前缀 → 增量重建快照 UPSERT → COMMIT。
    /// 归档不经过应用层反序列化；快照以既有快照为基准增量重建（无快照时全量重建）。
    /// </remarks>
    public async Task<AgentRunCompactionResult> CompactAsync(
        string workspaceId,
        string runId,
        int upToSequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 1. 读取最后 sequence 以钳制 upToSequence（防越界）。
            var lastSequence = await ReadLastSequenceAsync(connection, transaction, workspaceId, runId, cancellationToken)
                .ConfigureAwait(false);
            if (lastSequence < 0)
            {
                // 空事件流：无锚点可压缩，返回空结果（幂等）。
                return new AgentRunCompactionResult(
                    workspaceId, runId, -1, 0, 0, null, DateTimeOffset.UtcNow);
            }

            // 负数显式解释为 lastSequence（折叠到当前最后事件，前缀全部归档）；
            // 非负值按钳制公式（锚点不越界，防并发新增事件越界）。
            var anchorSequence = upToSequence < 0
                ? lastSequence
                : Math.Min(Math.Max(upToSequence, 0), lastSequence);

            // 2. 锁定 run 行：串行化同 Run 并发压缩，保证快照增量重建读到一致基线与增量
            //    （否则并发折叠时后提交者可能缺失已被先提交者删除的中间事件）。
            await LockRunRowAsync(connection, transaction, workspaceId, runId, cancellationToken)
                .ConfigureAwait(false);

            // 3. 读既有快照（增量重建基准）。
            var existingSnapshot = await ReadSnapshotCoreAsync(
                connection, transaction, workspaceId, runId, cancellationToken).ConfigureAwait(false);

            // 锚点不晚于既有快照 → 无新事件可折叠，幂等返回。
            if (existingSnapshot is not null && anchorSequence <= existingSnapshot.Sequence)
            {
                return new AgentRunCompactionResult(
                    workspaceId, runId, -1, 0, 0, null, DateTimeOffset.UtcNow);
            }

            // 4. 数据库侧归档折叠前缀（sequence < 锚点，锚点保留在热表作为新链头）。
            //    不经过应用层反序列化：热表 data 列原样拷贝到归档表（ON CONFLICT DO
            //    NOTHING 幂等——重复压缩同一前缀不产生重复归档行，RETURNING 计数实际新增行）。
            var archivedRowCount = await ArchivePrefixAsync(
                connection, transaction, workspaceId, runId, anchorSequence, cancellationToken).ConfigureAwait(false);

            // 5. 读取用于快照重建的事件：
            //    - 有可解析的既有快照 → 只读热表增量 [既有快照.Sequence+1 .. anchor]；
            //    - 无既有快照或快照无法解析（旧格式/损坏）→ 从归档 + 热表统一读取
            //      [0..anchor] 全量前缀重建（保证不丢已归档前缀的状态）。
            var baseState = existingSnapshot is null
                ? null
                : AgentRunEventStateRebuilder.TryDeserialize(existingSnapshot.StateJson);
            IReadOnlyList<AgentRunEvent> events;
            if (baseState is null)
            {
                events = await ReadFullPrefixAsync(
                    connection, transaction, workspaceId, runId, anchorSequence, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                events = await ReadSequenceRangeAsync(
                    connection, transaction, workspaceId, runId,
                    baseState.Sequence + 1, anchorSequence, cancellationToken).ConfigureAwait(false);
            }
            if (events.Count == 0)
            {
                return new AgentRunCompactionResult(
                    workspaceId, runId, -1, 0, 0, null, DateTimeOffset.UtcNow);
            }

            // 6. 从热表删除折叠前缀（锚点保留）。
            await DeleteFoldedPrefixAsync(
                connection, transaction, workspaceId, runId, anchorSequence, cancellationToken).ConfigureAwait(false);

            // 7. 快照增量重建 + UPSERT（per-run 单行，幂等覆盖）。
            var anchor = events[events.Count - 1];
            var baseSequence = baseState?.Sequence ?? -1;
            var foldedEventCount = anchorSequence - baseSequence;
            await UpsertSnapshotAsync(
                connection, transaction, workspaceId, runId, baseState, events,
                foldedEventCount, archivedRowCount, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new AgentRunCompactionResult(
                workspaceId,
                runId,
                anchorSequence,
                foldedEventCount,
                archivedRowCount,
                anchor.ContentHash,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* 不掩盖原始异常 */ }
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<AgentRunEventSnapshot?> GetSnapshotAsync(
        string workspaceId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT anchor_sequence, chain_head_hash, state_json, folded_event_count, compacted_at
FROM {Table("agent_run_event_snapshots")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var anchorSequence = reader.GetInt32(0);
        var chainHeadHash = reader.IsDBNull(1) ? null : reader.GetString(1);
        var stateJson = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        var foldedEventCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
        var compactedAt = reader.GetFieldValue<DateTimeOffset>(4);
        return new AgentRunEventSnapshot
        {
            WorkspaceId = workspaceId,
            RunId = runId,
            Sequence = anchorSequence,
            ChainHeadHash = chainHeadHash,
            FoldedEventCount = foldedEventCount,
            StateJson = stateJson,
            CreatedAt = compactedAt
        };
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentRunEvent>> GetArchivedEventsAsync(
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
FROM {Table("agent_run_events_archive")}
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
    /// <remarks>
    /// 热表按 Run 分组统计（PK (workspace_id, run_id, sequence) 覆盖分组扫描），
    /// 按事件数降序取前 limit 个，供后台 worker 逐轮处理。
    /// <b>仅限终态 Run</b>：JOIN <c>agent_runs</c>，只选取终态或重试已耗尽的 Run
    /// （见 <see cref="IsCompactableRunState"/>），避免压缩仍可被 Recovery 重放的非终态 Run。
    /// </remarks>
    public async Task<IReadOnlyList<AgentRunCompactionCandidate>> FindCandidatesAsync(
        int minEventCount,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (minEventCount < 1)
        {
            minEventCount = 1;
        }

        if (limit < 1)
        {
            limit = 50;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 可压缩终态列表由 AgentRunStateSemantics 权威生成（EventCompactable 状态 +
        // Failed 重试耗尽），避免与语义层漂移；IN 列表随语义层自动包含新终态。
        var terminalStateList = string.Join(", ",
            Enum.GetValues<AgentRunState>()
                .Where(s => AgentRunStateSemantics.Get(s).EventCompactable)
                .Select(s => (byte)s));
        command.CommandText = $"""
SELECT e.workspace_id, e.run_id, COUNT(*) AS event_count, MAX(e.sequence) AS last_sequence
FROM {Table("agent_run_events")} e
JOIN {Table("agent_runs")} ar
  ON ar.workspace_id = e.workspace_id AND ar.run_id = e.run_id
WHERE ar.state IN ({terminalStateList})
   OR (ar.state = @failed_state AND ar.retry_count >= ar.max_retries)
GROUP BY e.workspace_id, e.run_id
HAVING COUNT(*) >= @min_event_count
ORDER BY event_count DESC
LIMIT @limit;
""";
        command.Parameters.AddWithValue("min_event_count", minEventCount);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("failed_state", (byte)AgentRunState.Failed);

        var candidates = new List<AgentRunCompactionCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new AgentRunCompactionCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        return candidates;
    }

    /// <summary>
    /// 纯函数折叠计算（供单元测试直接验证）：
    /// 按 sequence 升序排序，锚点 = sequence == upToSequence 的事件（钳制后），
    /// 归档 = sequence &lt; upToSequence 的前缀事件，折叠计数 = 归档事件数。
    /// </summary>
    internal static (AgentRunEvent Anchor, IReadOnlyList<AgentRunEvent> ArchivedEvents, int FoldedEventCount) ComputeFold(
        IReadOnlyList<AgentRunEvent> events,
        int upToSequence)
    {
        if (events.Count == 0)
        {
            throw new ArgumentException("事件流为空，无法折叠。", nameof(events));
        }

        // 防御：按 sequence 升序稳定排序（正常路径调用方已升序传入）。
        var ordered = events.OrderBy(e => e.Sequence).ToList();
        // 负数显式解释为最后事件（折叠到流末尾，前缀全部归档）；非负值按钳制公式。
        var anchorIndex = upToSequence < 0
            ? ordered.Count - 1
            : Math.Min(Math.Max(upToSequence, 0), ordered.Count - 1);
        var anchor = ordered[anchorIndex];
        var archived = ordered.Take(anchorIndex).ToList();
        return (anchor, archived, anchorIndex);
    }

    private async Task<int> ReadLastSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT COALESCE(MAX(sequence), -1)
FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id AND run_id = @run_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long l ? (int)l : (result is int i ? i : -1);
    }

    private async Task<IReadOnlyList<AgentRunEvent>> ReadSequenceRangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        int fromSequenceExclusive,
        int upToSequenceInclusive,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
  AND sequence > @from_sequence AND sequence <= @up_to_sequence
ORDER BY sequence ASC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("from_sequence", fromSequenceExclusive);
        command.Parameters.AddWithValue("up_to_sequence", upToSequenceInclusive);
        return await ExecuteReaderJsonAsync<AgentRunEvent>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从归档表读取 [from..upTo] 区间事件（按 sequence 升序），供全量前缀重建使用。
    /// </summary>
    private async Task<IReadOnlyList<AgentRunEvent>> ReadArchivedRangeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        int fromSequenceInclusive,
        int upToSequenceInclusive,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_run_events_archive")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
  AND sequence >= @from_sequence AND sequence <= @up_to_sequence
ORDER BY sequence ASC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("from_sequence", fromSequenceInclusive);
        command.Parameters.AddWithValue("up_to_sequence", upToSequenceInclusive);
        return await ExecuteReaderJsonAsync<AgentRunEvent>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 统一读取 [0..upTo] 全量前缀：归档（折叠前缀）+ 热表（锚点 + 增量）按 sequence 归并。
    /// 用于无有效既有快照（首次压缩或旧格式/损坏快照）时的全量重建——归档部分保证
    /// 已折叠的历史事件不丢失。
    /// </summary>
    private async Task<IReadOnlyList<AgentRunEvent>> ReadFullPrefixAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        int upToSequence,
        CancellationToken cancellationToken)
    {
        var archived = await ReadArchivedRangeAsync(
            connection, transaction, workspaceId, runId, 0, upToSequence, cancellationToken).ConfigureAwait(false);
        var hot = await ReadSequenceRangeAsync(
            connection, transaction, workspaceId, runId, -1, upToSequence, cancellationToken).ConfigureAwait(false);

        if (archived.Count == 0)
        {
            return hot;
        }
        if (hot.Count == 0)
        {
            return archived;
        }

        var merged = new List<AgentRunEvent>(archived.Count + hot.Count);
        MergeSortedBySequence(archived, hot, merged);
        return merged;
    }

    /// <summary>按 sequence 升序归并两条有序事件流（事件在归档/热表间不重复）。</summary>
    private static void MergeSortedBySequence(
        IReadOnlyList<AgentRunEvent> left,
        IReadOnlyList<AgentRunEvent> right,
        List<AgentRunEvent> merged)
    {
        var i = 0;
        var j = 0;
        while (i < left.Count && j < right.Count)
        {
            if (left[i].Sequence <= right[j].Sequence)
            {
                merged.Add(left[i++]);
            }
            else
            {
                merged.Add(right[j++]);
            }
        }
        while (i < left.Count)
        {
            merged.Add(left[i++]);
        }
        while (j < right.Count)
        {
            merged.Add(right[j++]);
        }
    }

    /// <summary>
    /// 数据库侧归档折叠前缀：把热表 sequence &lt; 锚点的行原样拷贝到归档表。
    /// 不经过应用层反序列化；ON CONFLICT DO NOTHING 幂等（重复压缩不产生重复行），
    /// RETURNING 只返回实际新增行数。
    /// </summary>
    private async Task<int> ArchivePrefixAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        int anchorSequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("agent_run_events_archive")} (
    event_id, workspace_id, run_id, sequence,
    event_type, state, payload, content_hash,
    occurred_at, data)
SELECT event_id, workspace_id, run_id, sequence,
    event_type, state, payload, content_hash,
    occurred_at, data
FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND sequence < @anchor_sequence
ON CONFLICT (workspace_id, run_id, sequence) DO NOTHING
RETURNING sequence;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("anchor_sequence", anchorSequence);

        var insertedCount = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                insertedCount++;
            }
        }
        return insertedCount;
    }

    private async Task DeleteFoldedPrefixAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        int anchorSequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND sequence < @anchor_sequence;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("anchor_sequence", anchorSequence);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        AgentRunRecoverableState? baseState,
        IReadOnlyList<AgentRunEvent> deltaEvents,
        int foldedEventCount,
        int archivedRowCount,
        CancellationToken cancellationToken)
    {
        // 锚点 = 增量最后事件（按 Sequence 升序传入）。
        var anchor = deltaEvents[deltaEvents.Count - 1];
        // state_json = 以既有快照为基准、重放热表增量重建的完整可恢复状态
        // （Conversation / ToolObservations / ExecutionModelTurn / PendingToolCommands
        // + Sequence/ChainHeadHash），供 Recovery 快照恢复使用。
        var state = RebuildSnapshotIncrementally(baseState, deltaEvents);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("agent_run_event_snapshots")} (
    workspace_id, run_id, anchor_sequence, chain_head_hash, state_json,
    folded_event_count, archived_row_count, compacted_at)
VALUES (
    @workspace_id, @run_id, @anchor_sequence, @chain_head_hash, @state_json,
    @folded_event_count, @archived_row_count, @compacted_at)
ON CONFLICT (workspace_id, run_id) DO UPDATE SET
    anchor_sequence = EXCLUDED.anchor_sequence,
    chain_head_hash = EXCLUDED.chain_head_hash,
    state_json = EXCLUDED.state_json,
    folded_event_count = EXCLUDED.folded_event_count,
    archived_row_count = EXCLUDED.archived_row_count,
    compacted_at = EXCLUDED.compacted_at;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("anchor_sequence", anchor.Sequence);
        command.Parameters.AddWithValue("chain_head_hash", (object?)anchor.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("state_json", AgentRunEventStateRebuilder.Serialize(state));
        command.Parameters.AddWithValue("folded_event_count", foldedEventCount);
        command.Parameters.AddWithValue("archived_row_count", archivedRowCount);
        command.Parameters.AddWithValue("compacted_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 增量重建可恢复快照：以既有快照为基准，只重放热表新增增量事件。
    /// 无既有快照时对增量事件全量重建（首次压缩）。
    /// </summary>
    /// <param name="baseState">既有快照（可为 null = 首次压缩，无基准）。</param>
    /// <param name="deltaEvents">本次折叠新增的热表事件（按 Sequence 升序，非空）。</param>
    /// <returns>覆盖到增量最后事件的完整可恢复状态。</returns>
    internal static AgentRunRecoverableState RebuildSnapshotIncrementally(
        AgentRunRecoverableState? baseState,
        IReadOnlyList<AgentRunEvent> deltaEvents)
    {
        ArgumentNullException.ThrowIfNull(deltaEvents);
        if (deltaEvents.Count == 0)
        {
            throw new ArgumentException("增量事件为空，无法重建快照。", nameof(deltaEvents));
        }

        var last = deltaEvents[deltaEvents.Count - 1];
        if (baseState is null)
        {
            return AgentRunEventStateRebuilder.Rebuild(deltaEvents);
        }

        // 拷贝既有快照的对话流 / 工具观察，再追加增量事件的重建贡献（不修改既有列表）。
        var conversation = new List<AgentMessage>(baseState.Conversation);
        var toolObservations = new List<ToolObservation>(baseState.ToolObservations);
        foreach (var evt in deltaEvents)
        {
            AgentRunEventStateRebuilder.RebuildFromEvent(evt, conversation, toolObservations);
        }

        return new AgentRunRecoverableState
        {
            Sequence = last.Sequence,
            ChainHeadHash = last.ContentHash,
            Conversation = conversation,
            ToolObservations = toolObservations,
            ExecutionModelTurn = AgentRunEventStateRebuilder.RebuildExecutionModelTurn(
                deltaEvents, baseState.ExecutionModelTurn),
            // 增量内最后一个审批事件优先；增量无审批事件时沿用既有快照的 Pending 命令。
            PendingToolCommands = AgentRunEventStateRebuilder.ExtractPendingToolCommands(deltaEvents)
                ?? baseState.PendingToolCommands
        };
    }

    private async Task LockRunRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT 1
FROM {Table("agent_runs")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
FOR UPDATE;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentRunEventSnapshot?> ReadSnapshotCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT anchor_sequence, chain_head_hash, state_json, folded_event_count, compacted_at
FROM {Table("agent_run_event_snapshots")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var anchorSequence = reader.GetInt32(0);
        var chainHeadHash = reader.IsDBNull(1) ? null : reader.GetString(1);
        var stateJson = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        var foldedEventCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
        var compactedAt = reader.GetFieldValue<DateTimeOffset>(4);
        return new AgentRunEventSnapshot
        {
            WorkspaceId = workspaceId,
            RunId = runId,
            Sequence = anchorSequence,
            ChainHeadHash = chainHeadHash,
            FoldedEventCount = foldedEventCount,
            StateJson = stateJson,
            CreatedAt = compactedAt
        };
    }
}
