using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// R29 WP-B-1：PostgreSQL 持久化 Tool Dispatch Journal。
/// 替代 <see cref="ContextCore.Core.Services.AgentKernel.InMemoryToolDispatchJournal"/>，
/// 让 HA 场景下 tool 调用状态机可跨进程持久化与崩溃恢复。
/// </summary>
/// <remarks>
/// 设计要点：
///   1. 表 <c>tool_dispatch_journal_entries</c> 以 <c>request_id</c> 为主键；
///      <c>idempotency_key</c> 上有 UNIQUE partial index（P0-3），防止不同 request_id 复用同一幂等键。
///   2. <see cref="PrepareAsync"/> 使用 <c>INSERT ... ON CONFLICT (request_id) DO NOTHING</c> 保证幂等：
///      重复 Prepare 不覆盖已推进的状态（与 InMemory 的 <c>TryAdd</c> 语义一致）。
///      <b>P0-3 CAS-2</b>：0 行插入（request_id 已存在）时读取既有行并验证语义等价
///      （ToolName / IdempotencyKey / PayloadDigest / WorkspaceId / RunId），不等价时抛
///      <see cref="InvalidOperationException"/>（RequestIdReuseDetected），防止同一 RequestId 被复用为另一项操作。
///   3. <see cref="MarkDispatchedAsync"/> / <see cref="MarkCommittedAsync"/> / <see cref="MarkResultDeliveredAsync"/>
///      使用精确前驱状态 CAS：<c>UPDATE ... WHERE request_id = @id AND state = @expected</c>（P0-3 CAS-1）。
///      - 若 1 行受影响 → 成功前向推进（Applied）。
///      - 若 0 行受影响且行存在（state = target） → 幂等成功（AlreadyApplied，不报错）。
///      - 若 0 行受影响且行存在（state &gt; target） → 幂等成功（AlreadyAdvanced，不报错）。
///      - 若 0 行受影响且行存在（state &lt; expected） → 抛 <see cref="InvalidOperationException"/>（InvalidTransition，禁止跨级跳跃）。
///      - 若 0 行受影响且行不存在 → 抛 <see cref="InvalidOperationException"/>（MissingPredecessor，审计链断裂）。
///      <b>P0-3：不自动创建 stub 条目</b>——缺失记录意味着审计链不完整，必须让调用方感知冲突，
///      而不是补造高级状态（旧版的 auto-create 允许 不存在→Committed，破坏 exactly-once 审计）。
///   4. <see cref="GetEntryAsync"/> 通过主键读取整行并映射回 <see cref="ToolDispatchJournalEntry"/>。
///
/// <b>外部副作用 exactly-once 边界（P0-3）</b>：
///   本 Journal 仅保证 ContextCore 内部的"恰好一次编排记录"——同一 request_id 的状态机只向前推进一次。
///   但完整的外部副作用 exactly-once 还需要：
///     <list type="bullet">
///       <item>调用方提供 IdempotencyKey（UNIQUE 约束兜底去重）；</item>
///       <item>Tool provider 支持幂等键 / 外部操作 ID（外部系统侧去重）；</item>
///       <item>崩溃恢复时对 Dispatched 但未 Committed 的模糊状态进行外部对账。</item>
///     </list>
///   对于不支持幂等键的 Tool，只能声明 at-least-once 或要求人工确认。
/// </remarks>
public sealed class PostgresToolDispatchJournal : PostgresStoreBase, IPersistentToolDispatchJournal
{
    /// <summary>初始化 Postgres 持久化 Tool Dispatch Journal。</summary>
    public PostgresToolDispatchJournal(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async ValueTask PrepareAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State != ToolDispatchState.Prepared)
        {
            throw new ArgumentException(
                $"PrepareAsync 入口的 State 必须为 Prepared，实际为 {entry.State}。", nameof(entry));
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // ON CONFLICT DO NOTHING：重复 Prepare 不覆盖已推进的状态（幂等，与 InMemory TryAdd 语义一致）
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        insertCommand.CommandText = $"""
INSERT INTO {Table("tool_dispatch_journal_entries")} (
    request_id, tool_name, state, idempotency_key, external_operation_id, updated_at, diagnostic_note,
    payload_digest, workspace_id, run_id)
VALUES (
    @request_id, @tool_name, @state, @idempotency_key, @external_operation_id, @updated_at, @diagnostic_note,
    @payload_digest, @workspace_id, @run_id)
ON CONFLICT (request_id) DO NOTHING;
""";
        insertCommand.Parameters.AddWithValue("request_id", entry.RequestId);
        insertCommand.Parameters.AddWithValue("tool_name", entry.ToolName);
        insertCommand.Parameters.AddWithValue("state", (byte)entry.State);
        insertCommand.Parameters.AddWithValue("idempotency_key", (object?)entry.IdempotencyKey ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("external_operation_id", (object?)entry.ExternalOperationId ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("updated_at", entry.UpdatedAt);
        insertCommand.Parameters.AddWithValue("diagnostic_note", (object?)entry.DiagnosticNote ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("payload_digest", (object?)entry.PayloadDigest ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("workspace_id", (object?)entry.WorkspaceId ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("run_id", (object?)entry.RunId ?? DBNull.Value);

        var affected = await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected > 0)
        {
            return; // 成功插入新条目
        }

        // P0-3 CAS-2：0 行插入意味着 request_id 已存在——读取既有行并验证语义等价，
        // 防止同一 RequestId 被复用为另一项操作时静默沿用旧 journal 记录。
        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        selectCommand.CommandText = $"""
SELECT tool_name, idempotency_key, payload_digest, workspace_id, run_id
FROM {Table("tool_dispatch_journal_entries")}
WHERE request_id = @request_id
LIMIT 1;
""";
        selectCommand.Parameters.AddWithValue("request_id", entry.RequestId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // 极端竞态：INSERT 0 行但 SELECT 也读不到（行被并发 DELETE）。视为审计链断裂。
            throw new InvalidOperationException(
                $"Tool dispatch journal PrepareAsync 语义校验失败：request_id={entry.RequestId} 的既有行在读取时消失（并发删除？）。");
        }

        var existingToolName = reader.GetString(0);
        var existingIdempotencyKey = reader.IsDBNull(1) ? null : reader.GetString(1);
        var existingPayloadDigest = reader.IsDBNull(2) ? null : reader.GetString(2);
        var existingWorkspaceId = reader.IsDBNull(3) ? null : reader.GetString(3);
        var existingRunId = reader.IsDBNull(4) ? null : reader.GetString(4);

        var mismatches = new List<string>(5);
        if (!string.Equals(existingToolName, entry.ToolName, StringComparison.Ordinal))
        {
            mismatches.Add($"ToolName（既有={existingToolName}，新={entry.ToolName}）");
        }
        if (!string.Equals(existingIdempotencyKey, entry.IdempotencyKey, StringComparison.Ordinal))
        {
            mismatches.Add($"IdempotencyKey（既有={existingIdempotencyKey ?? "<null>"}，新={entry.IdempotencyKey ?? "<null>"}）");
        }
        if (!string.Equals(existingPayloadDigest, entry.PayloadDigest, StringComparison.Ordinal))
        {
            mismatches.Add($"PayloadDigest（既有={existingPayloadDigest ?? "<null>"}，新={entry.PayloadDigest ?? "<null>"}）");
        }
        if (!string.Equals(existingWorkspaceId, entry.WorkspaceId, StringComparison.Ordinal))
        {
            mismatches.Add($"WorkspaceId（既有={existingWorkspaceId ?? "<null>"}，新={entry.WorkspaceId ?? "<null>"}）");
        }
        if (!string.Equals(existingRunId, entry.RunId, StringComparison.Ordinal))
        {
            mismatches.Add($"RunId（既有={existingRunId ?? "<null>"}，新={entry.RunId ?? "<null>"}）");
        }

        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                $"RequestIdReuseDetected：request_id={entry.RequestId} 已存在但语义字段不等价——" +
                $"同一 RequestId 不能复用为另一项操作。差异：{string.Join("；", mismatches)}。");
        }

        // 语义等价：幂等成功（重复 Prepare 同一操作），不报错。
    }

    /// <inheritdoc />
    public async ValueTask MarkDispatchedAsync(string requestId, string? externalOperationId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }

        await TransitionStateAsync(
            requestId,
            expectedState: ToolDispatchState.Prepared,
            targetState: ToolDispatchState.Dispatched,
            externalOperationId: externalOperationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MarkCommittedAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }

        await TransitionStateAsync(
            requestId,
            expectedState: ToolDispatchState.Dispatched,
            targetState: ToolDispatchState.Committed,
            externalOperationId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MarkResultDeliveredAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }

        await TransitionStateAsync(
            requestId,
            expectedState: ToolDispatchState.Committed,
            targetState: ToolDispatchState.ResultDelivered,
            externalOperationId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ToolDispatchJournalEntry?> GetEntryAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT tool_name, state, idempotency_key, external_operation_id, updated_at, diagnostic_note,
       payload_digest, workspace_id, run_id
FROM {Table("tool_dispatch_journal_entries")}
WHERE request_id = @request_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ToolDispatchJournalEntry
        {
            RequestId = requestId,
            ToolName = reader.GetString(0),
            State = (ToolDispatchState)reader.GetByte(1),
            IdempotencyKey = reader.IsDBNull(2) ? null : reader.GetString(2),
            ExternalOperationId = reader.IsDBNull(3) ? null : reader.GetString(3),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(4),
            DiagnosticNote = reader.IsDBNull(5) ? null : reader.GetString(5),
            PayloadDigest = reader.IsDBNull(6) ? null : reader.GetString(6),
            WorkspaceId = reader.IsDBNull(7) ? null : reader.GetString(7),
            RunId = reader.IsDBNull(8) ? null : reader.GetString(8)
        };
    }

    /// <summary>
    /// 原子推进状态机（精确前驱状态 CAS，P0-3 CAS-1）：
    /// <c>UPDATE ... WHERE request_id = @id AND state = @expected_state</c>。
    /// <list type="bullet">
    ///   <item>1 行受影响（state = expected） → 成功前向推进（Applied）。</item>
    ///   <item>0 行受影响且行存在（state = target） → 幂等成功（AlreadyApplied，不报错）。</item>
    ///   <item>0 行受影响且行存在（state &gt; target） → 幂等成功（AlreadyAdvanced，不报错）。</item>
    ///   <item>0 行受影响且行存在（state &lt; expected） → 抛 <see cref="InvalidOperationException"/>（InvalidTransition，禁止跨级跳跃）。</item>
    ///   <item>0 行受影响且行不存在 → 抛 <see cref="InvalidOperationException"/>（MissingPredecessor，审计链断裂）。</item>
    /// </list>
    /// P0-3：不自动创建 stub 条目。缺失记录意味着审计链不完整，必须让调用方感知冲突。
    /// </summary>
    private async Task TransitionStateAsync(
        string requestId,
        ToolDispatchState expectedState,
        ToolDispatchState targetState,
        string? externalOperationId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. 精确前驱状态 CAS：UPDATE WHERE request_id = @id AND state = @expected_state
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            var setExternalOp = targetState == ToolDispatchState.Dispatched
                ? ", external_operation_id = COALESCE(@external_operation_id, external_operation_id)"
                : string.Empty;
            updateCommand.CommandText = $"""
UPDATE {Table("tool_dispatch_journal_entries")}
SET state = @target_state{setExternalOp}, updated_at = @updated_at
WHERE request_id = @request_id AND state = @expected_state;
""";
            updateCommand.Parameters.AddWithValue("request_id", requestId);
            updateCommand.Parameters.AddWithValue("expected_state", (byte)expectedState);
            updateCommand.Parameters.AddWithValue("target_state", (byte)targetState);
            updateCommand.Parameters.AddWithValue("updated_at", now);
            if (targetState == ToolDispatchState.Dispatched)
            {
                updateCommand.Parameters.AddWithValue("external_operation_id", (object?)externalOperationId ?? DBNull.Value);
            }

            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected > 0)
            {
                return; // 成功推进（Applied）
            }
        }

        // 2. 0 行受影响：读取当前行状态，区分 AlreadyApplied / AlreadyAdvanced / InvalidTransition / MissingPredecessor
        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        selectCommand.CommandText = $"""
SELECT state FROM {Table("tool_dispatch_journal_entries")}
WHERE request_id = @request_id
LIMIT 1;
""";
        selectCommand.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // 行不存在 → 审计链断裂（MissingPredecessor）
            throw new InvalidOperationException(
                $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                $"目标状态={targetState}（期望前驱={expectedState}）。" +
                $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。" +
                $"缺失记录意味着审计链不完整，可能丢失了 Prepare 步骤或数据库状态被外部篡改。");
        }

        var currentState = (ToolDispatchState)reader.GetByte(0);

        // state == target → AlreadyApplied（幂等，不报错）
        if (currentState == targetState)
        {
            return;
        }

        // state > target → AlreadyAdvanced（幂等，不报错）
        if ((int)currentState > (int)targetState)
        {
            return;
        }

        // state < expected → InvalidTransition（跨级跳跃，禁止）
        throw new InvalidOperationException(
            $"Tool dispatch state 跨级跳跃（InvalidTransition）：request_id={requestId}，" +
            $"当前={currentState}，期望前驱={expectedState}，目标={targetState}。" +
            $"状态机只能逐级向前推进：Prepared → Dispatched → Committed → ResultDelivered，" +
            $"不允许跳过中间状态（如 Prepared → Committed）。");
    }
}
