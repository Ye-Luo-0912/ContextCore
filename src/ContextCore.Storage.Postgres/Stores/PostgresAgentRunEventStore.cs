using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Agent Run Event Store。
/// 替代 <see cref="ContextCore.Core.Services.AgentRunRuntime.InMemoryAgentRunEventStore"/>，
/// 让 HA 场景下 Agent Run 事件流（哈希链）可跨进程持久化与崩溃恢复审计。
/// </summary>
/// <remarks>
/// 设计要点（参考 <see cref="PostgresToolDispatchJournal"/> 的 expected-state CAS）：
/// 1. 表 <c>agent_run_events</c> 主键 (workspace_id, run_id, sequence)：
/// UNIQUE 约束防重序列号，保证事件流单调递增。
/// 2. <see cref="AppendAsync"/> 使用事务 + SELECT MAX(sequence) 校验连续性：
/// - sequence 必须 = 当前 MAX + 1（链头为 0）；
/// - prev_chain_hash 必须 = 前一事件 content_hash（链头为 null）；
/// - 校验失败抛 <see cref="InvalidOperationException"/>。
/// 3. <see cref="ReadAsync"/> 按 sequence 升序读取（fromSequence + take + LIMIT）。
/// 4. <see cref="GetLastSequenceAsync"/> 通过 SELECT MAX(sequence) 实现；无事件返回 -1。
/// 5. 批量追加 <see cref="AppendBatchAsync"/> 委托 <see cref="IPersistentAgentRunCommitter"/>
/// 单事务提交（事件 + Run 状态 CAS + checkpoint + 结算 outbox），
/// 将 Turn 内多次网络往返降为 1 次，且不再由本 Store 承担 Run Store 的状态事务。
/// </remarks>
public sealed class PostgresAgentRunEventStore : PostgresStoreBase, IAgentRunEventStore, IPersistentAgentRunEventStore
{
    private readonly IAgentRunEventNotifier? _notifier;
    private readonly IPersistentAgentRunCommitter _committer;

    /// <summary>初始化 Postgres 持久化 Agent Run Event Store（内部自建提交器）。</summary>
    public PostgresAgentRunEventStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : this(connectionFactory, serializer, migrationRunner, null, null)
    {
    }

    /// <summary>
    /// 初始化并注入可选的事件推送通知器。
    /// 注入后批量追加在事务 COMMIT 后调用 <see cref="IAgentRunEventNotifier.Notify"/>，
    /// 让 SSE 端点在事件到达时立即唤醒读取（push），无事件时回退到 500ms 轮询。
    /// </summary>
    public PostgresAgentRunEventStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner,
        IAgentRunEventNotifier? notifier)
        : this(connectionFactory, serializer, migrationRunner, notifier, null)
    {
    }

    /// <summary>
    /// 初始化并注入提交器与可选的事件推送通知器。
    /// 未注入提交器时内部自建 <see cref="PostgresAgentRunCommitter"/>（保持直接构造可用）。
    /// </summary>
    public PostgresAgentRunEventStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner,
        IAgentRunEventNotifier? notifier,
        IPersistentAgentRunCommitter? committer)
        : base(connectionFactory, serializer, migrationRunner)
    {
        _notifier = notifier;
        _committer = committer ?? new PostgresAgentRunCommitter(connectionFactory, serializer, migrationRunner, notifier);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 优化：单条 SQL 原子完成"读取 last_event → 校验 sequence 连续性 + prev_hash 链接 → INSERT"。
    /// 相比旧版 SELECT+INSERT 两次往返，成功路径降为 1 次往返；校验失败或并发冲突时（affected=0）
    /// 再回退到 SELECT 给出精确错误信息（错误路径 2 次往返，可接受）。
    /// ON CONFLICT DO NOTHING 兜底防并发重序列号。
    /// 提供 leaseToken + fencingToken 时，WHERE 追加 EXISTS 子查询校验 agent_run_leases，
    /// lease 被抢占后 0 行插入 → 抛 <see cref="InvalidOperationException"/>。
    /// </remarks>
    public async ValueTask AppendAsync(
        AgentRunEvent @event,
        CancellationToken cancellationToken = default,
        string? leaseToken = null,
        long? fencingToken = null)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var leaseValidated = leaseToken is not null && fencingToken is not null;
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 单条 SQL：CTE 读取 last_event；WHERE 校验 sequence 连续性 + prev_hash 链接；
        // ON CONFLICT DO NOTHING 兜底；RETURNING 用于判断是否插入成功。
        // + leaseValidated 时 WHERE 追加 lease EXISTS 子句，
        // 同时校验 lease_expires_at > clock_timestamp() 防止过期租约仍能写入。
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        var leaseClause = leaseValidated
            ? $" AND EXISTS (SELECT 1 FROM {Table("agent_run_leases")} l WHERE l.workspace_id = @workspace_id AND l.run_id = @run_id AND l.lease_token = @lease_token AND l.fencing_token = @fencing_token AND l.lease_expires_at > clock_timestamp())"
            : string.Empty;
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
    (
    -- 链头：last_event 不存在 AND sequence=0 AND prev_hash IS NULL
    (NOT EXISTS (SELECT 1 FROM last_event) AND @sequence = 0 AND @prev_chain_hash IS NULL)
    OR
    -- 续链：last_event.sequence + 1 = @sequence AND last_event.content_hash IS NOT DISTINCT FROM @prev_chain_hash
    (EXISTS (SELECT 1 FROM last_event)
        AND (SELECT last_seq + 1 FROM last_event) = @sequence
        AND (SELECT last_hash FROM last_event) IS NOT DISTINCT FROM @prev_chain_hash)
    ){leaseClause}
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
        if (leaseValidated)
        {
            insertCommand.Parameters.AddWithValue("lease_token", leaseToken!);
            insertCommand.Parameters.AddWithValue("fencing_token", fencingToken!.Value);
        }

        bool insertSucceeded;
        await using (var reader = await insertCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            insertSucceeded = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        if (insertSucceeded)
        {
            return; // 插入成功（RETURNING 返回了 sequence）
        }

        // affected=0：校验失败或并发冲突。执行 SELECT 给出精确错误信息。
        // 若 lease 校验启用，优先检查 lease 是否已被抢占（这是最严重的双执行风险）。
        if (leaseValidated)
        {
            await using var leaseCheckCommand = connection.CreateCommand();
            leaseCheckCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            leaseCheckCommand.CommandText = $"""
SELECT 1 FROM {Table("agent_run_leases")}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND lease_token = @lease_token AND fencing_token = @fencing_token
  AND lease_expires_at > clock_timestamp()
LIMIT 1;
""";
            leaseCheckCommand.Parameters.AddWithValue("workspace_id", @event.WorkspaceId);
            leaseCheckCommand.Parameters.AddWithValue("run_id", @event.RunId);
            leaseCheckCommand.Parameters.AddWithValue("lease_token", leaseToken!);
            leaseCheckCommand.Parameters.AddWithValue("fencing_token", fencingToken!.Value);
            var leaseExists = await leaseCheckCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (leaseExists is null or DBNull)
            {
                throw new InvalidOperationException(
                    $"事件追加 lease fencing 校验失败：workspace_id={@event.WorkspaceId}, run_id={@event.RunId}, sequence={@event.Sequence}。" +
                    $"lease_token/fencing_token 不匹配——lease 已被其他实例抢占，应立即停止处理该 Run。");
            }
        }

        // Wrap diagnostic SELECT in try-catch to prevent secondary errors from masking the original insert failure
        string? expectedPrevHash = null;
        int expectedSequence = -1;
        try
        {
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
        }
        catch
        {
            // Diagnostic query failed; fall through with unknown expected values
            expectedSequence = -1;
            expectedPrevHash = null;
        }

        if (expectedSequence >= 0 && @event.Sequence != expectedSequence)
        {
            throw new InvalidOperationException(
                $"事件 Sequence 不连续：workspace_id={@event.WorkspaceId}, run_id={@event.RunId}。" +
                $"期望={expectedSequence}，实际={@event.Sequence}。" +
                $"事件流必须从 0 开始单调递增。");
        }

        if (expectedSequence >= 0 && !string.Equals(expectedPrevHash, @event.PrevChainHash, StringComparison.Ordinal))
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
    /// 批量追加委托 <see cref="IPersistentAgentRunCommitter"/> 单事务提交
    /// （事件流 + Run 状态 CAS + checkpoint + 结算 outbox），行为与原内联事务一致；
    /// 本 Store 不再承担 Run Store 的状态事务。
    /// </remarks>
    public async ValueTask AppendBatchAsync(
        IReadOnlyList<AgentRunEvent> events,
        AgentRunStateUpdate? runStateUpdate,
        AgentCheckpointCursor? checkpointCursor,
        AgentCheckpoint? checkpointBody,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        // 空批 + 无状态更新 → 直接返回
        if (events.Count == 0 && runStateUpdate is null)
        {
            return;
        }

        // 还原为 AgentRunCommit 并委托提交器。WorkspaceId/RunId 优先取状态更新负载，
        // 纯事件提交（runStateUpdate 为 null）时取首事件归属。
        var commit = new AgentRunCommit
        {
            WorkspaceId = runStateUpdate?.WorkspaceId ?? events[0].WorkspaceId,
            RunId = runStateUpdate?.RunId ?? events[0].RunId,
            Events = events,
            ExpectedCurrentState = runStateUpdate?.ExpectedCurrentState,
            NewRunSnapshot = runStateUpdate?.RunSnapshot,
            Checkpoint = checkpointBody,
            CheckpointCursor = checkpointCursor,
            UsageSnapshot = runStateUpdate?.RunSnapshot.CostBudget,
            LeaseToken = runStateUpdate?.LeaseToken,
            FencingToken = runStateUpdate?.FencingToken
        };
        await _committer.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public async ValueTask<int> GetAttemptBoundarySequenceAsync(
        string workspaceId, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 不可变 Attempt 边界：最后一个 RunRetryScheduled 事件的 Sequence（无重试 → -1）。
        // 恢复重放以本边界为起点，跳过前序 Attempt 的事件（历史保留，不删除）。
        command.CommandText = $"""
SELECT COALESCE(MAX(sequence), -1)
FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND event_type = @event_type;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("event_type", (byte)AgentRunEventType.RunRetryScheduled);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long l ? (int)l : (result is int i ? i : -1);
    }

    /// <summary>
    /// 获取指定 Run 的最新 checkpoint 游标（从 agent_runs 表的 last_checkpoint_id / last_checkpoint_sequence 列读取）。
    /// </summary>
    public async ValueTask<AgentCheckpointCursor?> GetCheckpointCursorAsync(
        string workspaceId, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT last_checkpoint_id, last_checkpoint_sequence
FROM {Table("agent_runs")}
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
        if (reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return null;
        }
        var checkpointId = reader.GetString(0);
        var lastSequence = reader.GetInt32(1);
        if (string.IsNullOrEmpty(checkpointId))
        {
            return null;
        }
        return new AgentCheckpointCursor
        {
            WorkspaceId = workspaceId,
            RunId = runId,
            CheckpointId = checkpointId,
            LastEventSequence = lastSequence
        };
    }
}
