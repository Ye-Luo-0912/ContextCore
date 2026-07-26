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
    public async ValueTask AppendAsync(AgentRunEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. 读取当前 MAX(sequence) 与对应行的 content_hash（用于连续性 + 哈希链校验）
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
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expectedSequence = reader.GetInt32(0) + 1;
                expectedPrevHash = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
            else
            {
                // 链头：sequence=0, prev_chain_hash=null
                expectedSequence = 0;
                expectedPrevHash = null;
            }
        }

        // 2. Sequence 连续性校验
        if (@event.Sequence != expectedSequence)
        {
            throw new InvalidOperationException(
                $"事件 Sequence 不连续：workspace_id={@event.WorkspaceId}, run_id={@event.RunId}。" +
                $"期望={expectedSequence}，实际={@event.Sequence}。" +
                $"事件流必须从 0 开始单调递增。");
        }

        // 3. PrevChainHash 链接校验
        if (!string.Equals(expectedPrevHash, @event.PrevChainHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"事件 PrevChainHash 不匹配：workspace_id={@event.WorkspaceId}, run_id={@event.RunId}。" +
                $"期望={expectedPrevHash ?? "<null>"}，实际={@event.PrevChainHash ?? "<null>"}。" +
                $"事件哈希链被破坏或乱序。");
        }

        // 4. 插入（UNIQUE 约束兜底防并发重序列号）
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        insertCommand.CommandText = $"""
INSERT INTO {Table("agent_run_events")} (
    event_id, workspace_id, run_id, sequence,
    event_type, state, payload, content_hash, prev_chain_hash,
    occurred_at, data)
VALUES (
    @event_id, @workspace_id, @run_id, @sequence,
    @event_type, @state, @payload, @content_hash, @prev_chain_hash,
    @occurred_at, @data)
ON CONFLICT (workspace_id, run_id, sequence) DO NOTHING;
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

        var affected = await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            // 并发写入：同 sequence 已被其他实例插入
            throw new InvalidOperationException(
                $"事件 Sequence 冲突：workspace_id={@event.WorkspaceId}, run_id={@event.RunId}, sequence={@event.Sequence}。" +
                $"并发写入导致序列号竞争；请重试 AppendAsync。");
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
