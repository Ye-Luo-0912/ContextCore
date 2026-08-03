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
/// sequence 时自动钳制到最后事件。
/// 4. 归档表 ON CONFLICT DO NOTHING：重复压缩同一前缀不产生重复归档行。
/// 5. <b>仅限终态 Run（R30.1 安全限制）</b>：<see cref="FindCandidatesAsync"/> 只选取
/// 终态（或重试已耗尽）的 Run——当前 Recovery 不读取快照/归档，非终态 Run 被压缩后
/// 重启恢复会因事件链断裂判定 RecoveryCorrupted。正式可恢复快照方案（Snapshot + Anchor
/// + Hot Delta + Archived Audit Stream）落地前保持此限制；操作员端点同样拒绝非终态 Run。
/// </remarks>
public sealed class PostgresAgentRunEventCompactor : PostgresStoreBase, IAgentRunEventCompactor
{
    /// <summary>
    /// 可压缩的终态集合（与 <see cref="ContextCore.Abstractions.AgentRunState"/> 字节值一致）：
    /// 这些状态不会再被 Recovery 重放。Failed 单独处理（重试耗尽才可压缩）。
    /// </summary>
    private static readonly byte[] CompactableTerminalStates =
    [
        (byte)AgentRunState.Completed,
        (byte)AgentRunState.Cancelled,
        (byte)AgentRunState.LeaseLost,
        (byte)AgentRunState.ReconciliationRejected,
        (byte)AgentRunState.RecoveryBlocked,
        (byte)AgentRunState.RecoveryCorrupted,
        (byte)AgentRunState.DeadLettered
    ];

    /// <summary>
    /// 判定 Run 是否可压缩（仅限终态）：终态直接可压缩；Failed 只有在重试已耗尽
    /// （<paramref name="retryCount"/> &gt;= <paramref name="maxRetries"/>）时才可压缩，
    /// 因为仍可重试的 Failed 会被调度器重新领取并全量重放事件流。
    /// </summary>
    public static bool IsCompactableRunState(AgentRunState state, int retryCount, int maxRetries)
    {
        if (CompactableTerminalStates.Contains((byte)state))
        {
            return true;
        }

        return state == AgentRunState.Failed && retryCount >= maxRetries;
    }
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
    /// 单事务原子执行：读前缀事件 → 归档 → 删除热表折叠前缀 → UPSERT 快照 → COMMIT。
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

            var anchorSequence = Math.Min(Math.Max(upToSequence, 0), lastSequence);

            // 2. 读取折叠前缀 [0..anchorSequence]（含锚点）。
            var events = await ReadPrefixAsync(connection, transaction, workspaceId, runId, anchorSequence, cancellationToken)
                .ConfigureAwait(false);
            if (events.Count == 0)
            {
                return new AgentRunCompactionResult(
                    workspaceId, runId, -1, 0, 0, null, DateTimeOffset.UtcNow);
            }

            var (anchor, archivedEvents, foldedEventCount) = ComputeFold(events, anchorSequence);

            // 3. 归档折叠事件（幂等：ON CONFLICT DO NOTHING；重复压缩不产生重复行）。
            var archivedRowCount = 0;
            if (archivedEvents.Count > 0)
            {
                archivedRowCount = await ArchiveAsync(
                    connection, transaction, workspaceId, runId, archivedEvents, cancellationToken).ConfigureAwait(false);
            }

            // 4. 从热表删除折叠前缀（锚点保留）。
            await DeleteFoldedPrefixAsync(
                connection, transaction, workspaceId, runId, anchorSequence, cancellationToken).ConfigureAwait(false);

            // 5. UPSERT 快照（per-run 单行，幂等覆盖）。
            await UpsertSnapshotAsync(
                connection, transaction, workspaceId, runId, anchor, foldedEventCount, archivedRowCount, cancellationToken)
                .ConfigureAwait(false);

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
        var terminalStateList = string.Join(", ", CompactableTerminalStates);
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
        var anchorIndex = Math.Min(Math.Max(upToSequence, 0), ordered.Count - 1);
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

    private async Task<IReadOnlyList<AgentRunEvent>> ReadPrefixAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        int upToSequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND sequence <= @up_to_sequence
ORDER BY sequence ASC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("up_to_sequence", upToSequence);
        return await ExecuteReaderJsonAsync<AgentRunEvent>(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ArchiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        IReadOnlyList<AgentRunEvent> archivedEvents,
        CancellationToken cancellationToken)
    {
        // 单条 SQL 批量归档（unnest）；ON CONFLICT DO NOTHING 幂等——重复压缩同一前缀不产生重复行。
        var eventCount = archivedEvents.Count;
        var eventIds = new string[eventCount];
        var sequences = new int[eventCount];
        var eventTypes = new short[eventCount];
        var states = new short[eventCount];
        var payloads = new string[eventCount];
        var contentHashes = new string?[eventCount];
        var occurredAts = new DateTimeOffset[eventCount];
        var datas = new string[eventCount];
        for (var i = 0; i < eventCount; i++)
        {
            var e = archivedEvents[i];
            eventIds[i] = e.EventId;
            sequences[i] = e.Sequence;
            eventTypes[i] = (short)e.EventType;
            states[i] = (short)e.State;
            payloads[i] = e.Payload ?? string.Empty;
            contentHashes[i] = e.ContentHash;
            occurredAts[i] = e.OccurredAt;
            datas[i] = Serializer.Serialize(e);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("agent_run_events_archive")} (
    event_id, workspace_id, run_id, sequence,
    event_type, state, payload, content_hash,
    occurred_at, data)
SELECT
    evt_id, @workspace_id, @run_id, evt_seq,
    evt_type, evt_state, evt_payload, evt_hash,
    evt_occurred_at, evt_data::jsonb
FROM unnest(
    @event_ids::text[],
    @sequences::integer[],
    @event_types::smallint[],
    @states::smallint[],
    @payloads::text[],
    @content_hashes::text[],
    @occurred_ats::timestamptz[],
    @datas::jsonb[]
) AS t(evt_id, evt_seq, evt_type, evt_state, evt_payload, evt_hash, evt_occurred_at, evt_data)
ON CONFLICT (workspace_id, run_id, sequence) DO NOTHING
RETURNING sequence;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        AddTextArray(command, "event_ids", eventIds);
        AddArrayParameter(command, "sequences", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, sequences);
        AddArrayParameter(command, "event_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Smallint, eventTypes);
        AddArrayParameter(command, "states", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Smallint, states);
        AddTextArray(command, "payloads", payloads);
        AddNullableTextArray(command, "content_hashes", contentHashes);
        AddArrayParameter(command, "occurred_ats", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.TimestampTz, occurredAts);
        AddArrayParameter(command, "datas", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Jsonb, datas);

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
        AgentRunEvent anchor,
        int foldedEventCount,
        int archivedRowCount,
        CancellationToken cancellationToken)
    {
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
        command.Parameters.AddWithValue("state_json", Serializer.Serialize(anchor));
        command.Parameters.AddWithValue("folded_event_count", foldedEventCount);
        command.Parameters.AddWithValue("archived_row_count", archivedRowCount);
        command.Parameters.AddWithValue("compacted_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
