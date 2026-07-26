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
///   3. <see cref="MarkDispatchedAsync"/> / <see cref="MarkCommittedAsync"/> / <see cref="MarkResultDeliveredAsync"/>
///      使用 expected-state CAS：<c>UPDATE ... WHERE request_id = @id AND state &lt; @target</c>。
///      - 若 1 行受影响 → 成功前向推进。
///      - 若 0 行受影响且行存在（state ≥ target） → 抛 <see cref="InvalidOperationException"/>（不可逆退）。
///      - 若 0 行受影响且行不存在 → 抛 <see cref="InvalidOperationException"/>（缺失 Prepared 前驱，审计链断裂）。
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
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // ON CONFLICT DO NOTHING：重复 Prepare 不覆盖已推进的状态（幂等，与 InMemory TryAdd 语义一致）
        command.CommandText = $"""
INSERT INTO {Table("tool_dispatch_journal_entries")} (
    request_id, tool_name, state, idempotency_key, external_operation_id, updated_at, diagnostic_note)
VALUES (
    @request_id, @tool_name, @state, @idempotency_key, @external_operation_id, @updated_at, @diagnostic_note)
ON CONFLICT (request_id) DO NOTHING;
""";
        command.Parameters.AddWithValue("request_id", entry.RequestId);
        command.Parameters.AddWithValue("tool_name", entry.ToolName);
        command.Parameters.AddWithValue("state", (byte)entry.State);
        command.Parameters.AddWithValue("idempotency_key", (object?)entry.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("external_operation_id", (object?)entry.ExternalOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_at", entry.UpdatedAt);
        command.Parameters.AddWithValue("diagnostic_note", (object?)entry.DiagnosticNote ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            targetState: ToolDispatchState.Dispatched,
            externalOperationId: externalOperationId,
            expectedPrior: "Prepared",
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
            targetState: ToolDispatchState.Committed,
            externalOperationId: null,
            expectedPrior: "Dispatched",
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
            targetState: ToolDispatchState.ResultDelivered,
            externalOperationId: null,
            expectedPrior: "Committed",
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
SELECT tool_name, state, idempotency_key, external_operation_id, updated_at, diagnostic_note
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
            DiagnosticNote = reader.IsDBNull(5) ? null : reader.GetString(5)
        };
    }

    /// <summary>
    /// 原子推进状态机（expected-state CAS）：<c>UPDATE ... WHERE request_id = @id AND state &lt; @target</c>。
    /// <list type="bullet">
    ///   <item>1 行受影响 → 成功前向推进。</item>
    ///   <item>0 行受影响且行存在（state ≥ target） → 抛 <see cref="InvalidOperationException"/>（不可逆退）。</item>
    ///   <item>0 行受影响且行不存在 → 抛 <see cref="InvalidOperationException"/>（缺失前驱状态，审计链断裂）。</item>
    /// </list>
    /// P0-3：不自动创建 stub 条目。缺失记录意味着审计链不完整，必须让调用方感知冲突。
    /// </summary>
    private async Task TransitionStateAsync(
        string requestId,
        ToolDispatchState targetState,
        string? externalOperationId,
        string expectedPrior,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. expected-state CAS：UPDATE WHERE request_id = @id AND state < @target
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            var setExternalOp = targetState == ToolDispatchState.Dispatched
                ? ", external_operation_id = COALESCE(@external_operation_id, external_operation_id)"
                : string.Empty;
            updateCommand.CommandText = $"""
UPDATE {Table("tool_dispatch_journal_entries")}
SET state = @target_state{setExternalOp}, updated_at = @updated_at
WHERE request_id = @request_id AND state < @target_state;
""";
            updateCommand.Parameters.AddWithValue("request_id", requestId);
            updateCommand.Parameters.AddWithValue("target_state", (byte)targetState);
            updateCommand.Parameters.AddWithValue("updated_at", now);
            if (targetState == ToolDispatchState.Dispatched)
            {
                updateCommand.Parameters.AddWithValue("external_operation_id", (object?)externalOperationId ?? DBNull.Value);
            }

            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected > 0)
            {
                return; // 成功推进
            }
        }

        // 2. 0 行受影响：检查行是否存在以区分"逆退"与"缺失前驱"
        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        selectCommand.CommandText = $"""
SELECT state FROM {Table("tool_dispatch_journal_entries")}
WHERE request_id = @request_id
LIMIT 1;
""";
        selectCommand.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // 行存在但 state >= target → 不可逆退
            var currentState = (ToolDispatchState)reader.GetByte(0);
            throw new InvalidOperationException(
                $"Tool dispatch state 不可逆退：当前={currentState}，目标={targetState}。" +
                $"状态机只能向前推进：Prepared → Dispatched → Committed → ResultDelivered。");
        }

        // 3. 行不存在 → 审计链断裂（P0-3：不再 auto-create stub）
        throw new InvalidOperationException(
            $"Tool dispatch journal 缺失前驱记录：request_id={requestId}，目标状态={targetState}（期望前驱={expectedPrior}）。" +
            $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。" +
            $"缺失记录意味着审计链不完整，可能丢失了 Prepare 步骤或数据库状态被外部篡改。");
    }
}
