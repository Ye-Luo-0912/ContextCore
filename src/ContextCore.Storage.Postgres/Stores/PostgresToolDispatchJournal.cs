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
///   1. 表 <c>tool_dispatch_journal_entries</c> 以 <c>request_id</c> 为主键。
///   2. <see cref="PrepareAsync"/> 使用 <c>INSERT ... ON CONFLICT DO NOTHING</c> 保证幂等：
///      重复 Prepare 不覆盖已推进的状态（与 InMemory 的 <c>TryAdd</c> 语义一致）。
///   3. <see cref="MarkDispatchedAsync"/> / <see cref="MarkCommittedAsync"/> / <see cref="MarkResultDeliveredAsync"/>
///      使用 <c>UPDATE ... WHERE state &lt; :target</c> 原子推进状态：
///      - 若 0 行受影响且行存在 → 当前状态 ≥ 目标 → 抛 <see cref="InvalidOperationException"/>（与 InMemory 一致）。
///      - 若 0 行受影响且行不存在 → 自动插入 stub 条目（与 InMemory 的 auto-create 语义一致）。
///   4. <see cref="GetEntryAsync"/> 通过主键读取整行并映射回 <see cref="ToolDispatchJournalEntry"/>。
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
            autoCreateNote: "Dispatched without prior Prepare (auto-created)",
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
            autoCreateNote: "Committed without prior Dispatched (auto-created)",
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
            autoCreateNote: "ResultDelivered without prior Committed (auto-created)",
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
    /// 原子推进状态机：UPDATE WHERE state &lt; target；
    /// 若 0 行受影响且行存在 → 抛 <see cref="InvalidOperationException"/>（不可逆退）；
    /// 若 0 行受影响且行不存在 → 插入 stub 条目（auto-create，与 InMemory 语义一致）。
    /// </summary>
    private async Task TransitionStateAsync(
        string requestId,
        ToolDispatchState targetState,
        string? externalOperationId,
        string autoCreateNote,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. 尝试原子推进：UPDATE WHERE state < target
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

        // 2. 0 行受影响：检查行是否存在
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

        // 3. 行不存在 → 插入 stub 条目（auto-create，与 InMemory 语义一致）
        reader.Close();
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        insertCommand.CommandText = $"""
INSERT INTO {Table("tool_dispatch_journal_entries")} (
    request_id, tool_name, state, idempotency_key, external_operation_id, updated_at, diagnostic_note)
VALUES (
    @request_id, @tool_name, @state, @idempotency_key, @external_operation_id, @updated_at, @diagnostic_note)
ON CONFLICT (request_id) DO UPDATE SET
    state = EXCLUDED.state,
    external_operation_id = COALESCE(EXCLUDED.external_operation_id, tool_dispatch_journal_entries.external_operation_id),
    updated_at = EXCLUDED.updated_at,
    diagnostic_note = EXCLUDED.diagnostic_note;
""";
        insertCommand.Parameters.AddWithValue("request_id", requestId);
        insertCommand.Parameters.AddWithValue("tool_name", string.Empty);
        insertCommand.Parameters.AddWithValue("state", (byte)targetState);
        insertCommand.Parameters.AddWithValue("idempotency_key", DBNull.Value);
        insertCommand.Parameters.AddWithValue("external_operation_id", (object?)externalOperationId ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("updated_at", now);
        insertCommand.Parameters.AddWithValue("diagnostic_note", autoCreateNote);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
