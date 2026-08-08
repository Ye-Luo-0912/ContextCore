using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Tool Dispatch Journal。
/// 替代 <see cref="ContextCore.Core.Services.AgentKernel.InMemoryToolDispatchJournal"/>，
/// 让 HA 场景下 tool 调用状态机可跨进程持久化与崩溃恢复。
/// </summary>
/// <remarks>
/// 设计要点：
/// 1. 表 <c>tool_dispatch_journal_entries</c> 以 <c>request_id</c> 为主键；
/// <c>idempotency_key</c> 上有 UNIQUE partial index，防止不同 request_id 复用同一幂等键。
/// 2. <see cref="PrepareAsync"/> 使用 <c>INSERT ... ON CONFLICT (request_id) DO NOTHING</c> 保证幂等：
/// 重复 Prepare 不覆盖已推进的状态（与 InMemory 的 <c>TryAdd</c> 语义一致）。
/// <b>CAS-2</b>：0 行插入（request_id 已存在）时读取既有行并验证语义等价
/// （ToolName / IdempotencyKey / PayloadDigest / WorkspaceId / RunId），不等价时抛
/// <see cref="InvalidOperationException"/>（RequestIdReuseDetected），防止同一 RequestId 被复用为另一项操作。
/// 3. <see cref="MarkDispatchedAsync"/> / <see cref="MarkCommittedAsync"/> / <see cref="MarkResultDeliveredAsync"/>
/// 使用精确前驱状态 CAS：<c>UPDATE ... WHERE request_id = @id AND state = @expected</c>）。
/// - 若 1 行受影响 → 成功前向推进（Applied）。
/// - 若 0 行受影响且行存在（state = target） → 幂等成功（AlreadyApplied，不报错）。
/// - 若 0 行受影响且行存在（state &gt; target） → 幂等成功（AlreadyAdvanced，不报错）。
/// - 若 0 行受影响且行存在（state &lt; expected） → 抛 <see cref="InvalidOperationException"/>（InvalidTransition，禁止跨级跳跃）。
/// - 若 0 行受影响且行不存在 → 抛 <see cref="InvalidOperationException"/>（MissingPredecessor，审计链断裂）。
/// 不自动创建 stub 条目</b>——缺失记录意味着审计链不完整，必须让调用方感知冲突，
/// 而不是补造高级状态（旧版的 auto-create 允许 不存在→Committed，破坏 exactly-once 审计）。
/// 4. <see cref="GetEntryAsync"/> 通过主键读取整行并映射回 <see cref="ToolDispatchJournalEntry"/>。
/// 
/// <b>外部副作用 exactly-once 边界</b>：
/// 本 Journal 仅保证 ContextCore 内部的"恰好一次编排记录"——同一 request_id 的状态机只向前推进一次。
/// 但完整的外部副作用 exactly-once 还需要：
/// <list type="bullet">
/// <item>调用方提供 IdempotencyKey（UNIQUE 约束兜底去重）；</item>
/// <item>Tool provider 支持幂等键 / 外部操作 ID（外部系统侧去重）；</item>
/// <item>崩溃恢复时对 Dispatched 但未 Committed 的模糊状态进行外部对账。</item>
/// </list>
/// 对于不支持幂等键的 Tool，只能声明 at-least-once 或要求人工确认。
/// </remarks>
public sealed class PostgresToolDispatchJournal : PostgresStoreBase, IPersistentToolDispatchJournal
{
    /// <summary>
    /// 逻辑状态顺序映射。DispatchingIntent=4 的数值大于 Dispatched=1，
    /// 破坏了基于数值大小的状态顺序判断，因此使用此字典按逻辑顺序判断前向推进。
    /// </summary>
    private static readonly Dictionary<ToolDispatchState, int> s_logicalOrder = new()
    {
        { ToolDispatchState.Prepared, 0 },
        { ToolDispatchState.DispatchingIntent, 1 },
        { ToolDispatchState.Dispatched, 2 },
        { ToolDispatchState.Reconciling, 3 },
        { ToolDispatchState.Committed, 4 },
        { ToolDispatchState.ResultDelivered, 5 }
    };
    /// <summary>初始化 Postgres 持久化 Tool Dispatch Journal。</summary>
    public PostgresToolDispatchJournal(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="MarkCommittedWithResultAsync"/> 在同一 DB 事务内同时 UPDATE journal state
    /// 与 UPSERT 结果到 <c>tool_dispatch_results</c>，返回 true，调用方无需再单独调用
    /// <see cref="IDurableToolResultStore.SaveAsync"/>。
    /// </remarks>
    public bool PersistsResults => true;

    /// <inheritdoc />
    public async ValueTask<ToolDispatchPrepareResult> PrepareAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default)
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
        var affected = await InsertEntryAsync(connection, entry, cancellationToken).ConfigureAwait(false);
        if (affected > 0)
        {
            // 成功插入新条目 → ShouldDispatch = true；返回调用方派生的身份（Journal 权威回传）。
            return new ToolDispatchPrepareResult
            {
                CurrentState = ToolDispatchState.Prepared,
                ShouldDispatch = true,
                NeedsReconciliation = false,
                ExternalOperationId = entry.ExternalOperationId,
                CachedResult = null,
                RequestId = entry.RequestId,
                EffectiveIdempotencyKey = entry.IdempotencyKey,
                RecoveryDecision = ToolDispatchRecoveryDecision.Dispatch
            };
        }

        // 0 行插入意味着 request_id 已存在——读取既有行并验证语义等价，
        // 防止同一 RequestId 被复用为另一项操作时静默沿用旧 journal 记录。
        var (existingState, existingExternalOperationId, existingIdempotencyKey) = await ReadAndValidateExistingAsync(connection, entry, cancellationToken).ConfigureAwait(false);

        // 语义等价：幂等成功（重复 Prepare 同一操作）。根据当前状态构建 Prepare 结果。
        // 注意：ToolDispatchState 字节值非逻辑有序（DispatchingIntent=4/Reconciling=5 大于 Committed=2），
        // 必须用显式状态判定而非数值比较。
        return new ToolDispatchPrepareResult
        {
            CurrentState = existingState,
            ShouldDispatch = existingState == ToolDispatchState.Prepared,
            NeedsReconciliation = existingState == ToolDispatchState.Dispatched || existingState == ToolDispatchState.DispatchingIntent,
            ExternalOperationId = existingExternalOperationId,
            CachedResult = null, // Postgres 结果缓存由调用方管理
            RequestId = entry.RequestId,
            EffectiveIdempotencyKey = existingIdempotencyKey,
            RecoveryDecision = existingState == ToolDispatchState.Committed || existingState == ToolDispatchState.ResultDelivered
                ? ToolDispatchRecoveryDecision.UseCachedResult
                : existingState == ToolDispatchState.DispatchingIntent
                  || existingState == ToolDispatchState.Dispatched
                  || existingState == ToolDispatchState.Reconciling
                    ? ToolDispatchRecoveryDecision.Reconcile
                    : ToolDispatchRecoveryDecision.Dispatch
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Prepare + 前置 Intent 合并为单次原子写——新条目直接以 DispatchingIntent 落库
    /// （比 PrepareAsync 少一次 INSERT→UPDATE 往返），既有 Prepared 前驱（旧两步流程崩溃残留）
    /// 经 CAS 原子推进到 DispatchingIntent。返回 ShouldDispatch=true 时 journal 必已处于
    /// DispatchingIntent，调用方可直接 Dispatch，无需再单独 MarkDispatchingIntentAsync。
    /// </remarks>
    public async ValueTask<ToolDispatchPrepareResult> PrepareWithIntentAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State != ToolDispatchState.Prepared && entry.State != ToolDispatchState.DispatchingIntent)
        {
            throw new ArgumentException(
                $"PrepareWithIntentAsync 入口的 State 必须为 Prepared 或 DispatchingIntent，实际为 {entry.State}。", nameof(entry));
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. 直接以 DispatchingIntent 落库（单次 INSERT 即完成 Prepare + Intent）
        var affected = await InsertEntryAsync(
            connection, entry with { State = ToolDispatchState.DispatchingIntent }, cancellationToken).ConfigureAwait(false);
        if (affected > 0)
        {
            // 成功插入 → ShouldDispatch=true；返回调用方派生的身份（Journal 权威回传）。
            return new ToolDispatchPrepareResult
            {
                CurrentState = ToolDispatchState.DispatchingIntent,
                ShouldDispatch = true,
                NeedsReconciliation = false,
                ExternalOperationId = entry.ExternalOperationId,
                CachedResult = null,
                RequestId = entry.RequestId,
                EffectiveIdempotencyKey = entry.IdempotencyKey,
                RecoveryDecision = ToolDispatchRecoveryDecision.Dispatch
            };
        }

        // 2. 已存在：语义等价校验（RequestId 复用检测）
        var (existingState, existingExternalOperationId, existingIdempotencyKey) = await ReadAndValidateExistingAsync(connection, entry, cancellationToken).ConfigureAwait(false);

        // 3. 既有 Prepared 前驱（旧两步流程崩溃残留）→ CAS 原子推进到 DispatchingIntent
        if (existingState == ToolDispatchState.Prepared)
        {
            var key = new TenantRunKey(entry.WorkspaceId ?? string.Empty, entry.RunId ?? string.Empty);
            var advanced = await TryAdvanceToDispatchingIntentAsync(
                connection, key, entry.RequestId, entry.ExternalOperationId, cancellationToken).ConfigureAwait(false);
            if (advanced)
            {
                return new ToolDispatchPrepareResult
                {
                    CurrentState = ToolDispatchState.DispatchingIntent,
                    ShouldDispatch = true,
                    NeedsReconciliation = false,
                    ExternalOperationId = existingExternalOperationId,
                    CachedResult = null,
                    RequestId = entry.RequestId,
                    EffectiveIdempotencyKey = existingIdempotencyKey,
                    RecoveryDecision = ToolDispatchRecoveryDecision.Dispatch
                };
            }

            // 并发推进（0 行受影响）→ 重读实际状态后按矩阵返回
            (existingState, existingExternalOperationId, existingIdempotencyKey) = await ReadAndValidateExistingAsync(connection, entry, cancellationToken).ConfigureAwait(false);
        }

        // 4. 按当前状态构建决策矩阵
        return new ToolDispatchPrepareResult
        {
            CurrentState = existingState,
            ShouldDispatch = existingState == ToolDispatchState.Prepared,
            NeedsReconciliation = existingState == ToolDispatchState.Dispatched || existingState == ToolDispatchState.DispatchingIntent,
            ExternalOperationId = existingExternalOperationId,
            CachedResult = null, // Postgres 结果缓存由调用方管理
            RequestId = entry.RequestId,
            EffectiveIdempotencyKey = existingIdempotencyKey,
            RecoveryDecision = existingState == ToolDispatchState.Committed || existingState == ToolDispatchState.ResultDelivered
                ? ToolDispatchRecoveryDecision.UseCachedResult
                : existingState == ToolDispatchState.DispatchingIntent
                  || existingState == ToolDispatchState.Dispatched
                  || existingState == ToolDispatchState.Reconciling
                    ? ToolDispatchRecoveryDecision.Reconcile
                    : ToolDispatchRecoveryDecision.Dispatch
        };
    }

    /// <summary>单条 INSERT（ON CONFLICT DO NOTHING），返回受影响行数（0 = 复合键已存在）。</summary>
    /// <remarks>
    /// 冲突目标为 (workspace_id, run_id, request_id) 复合主键——跨工作区/跨 Run 可复用
    /// 相同 request_id 而互不干扰（与 agent_run_leases 复合键模式一致）。
    /// </remarks>
    private async Task<int> InsertEntryAsync(
        NpgsqlConnection connection,
        ToolDispatchJournalEntry entry,
        CancellationToken cancellationToken)
    {
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        insertCommand.CommandText = $"""
INSERT INTO {Table("tool_dispatch_journal_entries")} (
    request_id, tool_name, state, idempotency_key, external_operation_id, updated_at, diagnostic_note,
    payload_digest, workspace_id, run_id)
VALUES (
    @request_id, @tool_name, @state, @idempotency_key, @external_operation_id, @updated_at, @diagnostic_note,
    @payload_digest, @workspace_id, @run_id)
ON CONFLICT (workspace_id, run_id, request_id) DO NOTHING;
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
        return await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取既有行并验证与本次条目语义等价（ToolName/IdempotencyKey/PayloadDigest/WorkspaceId/RunId）。
    /// 任一不等价 → 抛 RequestIdReuseDetected；行缺失（并发删除）→ 抛审计链断裂异常。
    /// 查询按 (workspace_id, run_id, request_id) 复合键寻址。
    /// </summary>
    private async Task<(ToolDispatchState State, string? ExternalOperationId, string? IdempotencyKey)> ReadAndValidateExistingAsync(
        NpgsqlConnection connection,
        ToolDispatchJournalEntry entry,
        CancellationToken cancellationToken)
    {
        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        selectCommand.CommandText = $"""
SELECT tool_name, state, idempotency_key, external_operation_id, payload_digest, workspace_id, run_id
FROM {Table("tool_dispatch_journal_entries")}
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
LIMIT 1;
""";
        selectCommand.Parameters.AddWithValue("workspace_id", (object?)entry.WorkspaceId ?? DBNull.Value);
        selectCommand.Parameters.AddWithValue("run_id", (object?)entry.RunId ?? DBNull.Value);
        selectCommand.Parameters.AddWithValue("request_id", entry.RequestId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // 极端竞态：INSERT 0 行但 SELECT 也读不到（行被并发 DELETE）。视为审计链断裂。
            throw new InvalidOperationException(
                $"Tool dispatch journal Prepare 语义校验失败：request_id={entry.RequestId} 的既有行在读取时消失（并发删除？）。");
        }

        var existingToolName = reader.GetString(0);
        var existingState = (ToolDispatchState)reader.GetByte(1);
        var existingIdempotencyKey = reader.IsDBNull(2) ? null : reader.GetString(2);
        var existingExternalOperationId = reader.IsDBNull(3) ? null : reader.GetString(3);
        var existingPayloadDigest = reader.IsDBNull(4) ? null : reader.GetString(4);
        var existingWorkspaceId = reader.IsDBNull(5) ? null : reader.GetString(5);
        var existingRunId = reader.IsDBNull(6) ? null : reader.GetString(6);

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

        return (existingState, existingExternalOperationId, existingIdempotencyKey);
    }

    /// <summary>CAS 推进 Prepared → DispatchingIntent；0 行受影响表示状态已被并发推进。</summary>
    /// <remarks>
    /// 推进时以 COALESCE 写入 external_operation_id——框架在 Prepare 时生成的
    /// 外部操作 ID 需随 Prepared 前驱推进一并落库（旧两步流程的 Prepared 残留可能无该值）。
    /// 按 (workspace_id, run_id, request_id) 复合键寻址。
    /// </remarks>
    private async Task<bool> TryAdvanceToDispatchingIntentAsync(
        NpgsqlConnection connection,
        TenantRunKey key,
        string requestId,
        string? externalOperationId,
        CancellationToken cancellationToken)
    {
        await using var updateCommand = connection.CreateCommand();
        updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        updateCommand.CommandText = $"""
UPDATE {Table("tool_dispatch_journal_entries")}
SET state = @target_state, external_operation_id = COALESCE(external_operation_id, @external_operation_id), updated_at = @updated_at
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
  AND state = @expected_state;
""";
        updateCommand.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
        updateCommand.Parameters.AddWithValue("run_id", key.RunId);
        updateCommand.Parameters.AddWithValue("request_id", requestId);
        updateCommand.Parameters.AddWithValue("expected_state", (byte)ToolDispatchState.Prepared);
        updateCommand.Parameters.AddWithValue("target_state", (byte)ToolDispatchState.DispatchingIntent);
        updateCommand.Parameters.AddWithValue("external_operation_id", (object?)externalOperationId ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
        var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 在外部 Tool 调用发起前持久化 DispatchingIntent 状态，创建 durable 边界。
    /// 与 MarkDispatchedAsync 不同，本方法在状态已超过 DispatchingIntent 时抛异常（而非幂等成功），
    /// 因为继续 Dispatch 会导致外部副作用重复执行。
    /// </remarks>
    public async ValueTask MarkDispatchingIntentAsync(TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.RunId);

        var now = DateTimeOffset.UtcNow;
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. CAS UPDATE Prepared → DispatchingIntent（复合键寻址）
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            updateCommand.CommandText = $"""
UPDATE {Table("tool_dispatch_journal_entries")}
SET state = @target_state, updated_at = @updated_at
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
  AND state = @expected_state;
""";
            updateCommand.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
            updateCommand.Parameters.AddWithValue("run_id", key.RunId);
            updateCommand.Parameters.AddWithValue("request_id", requestId);
            updateCommand.Parameters.AddWithValue("expected_state", (byte)ToolDispatchState.Prepared);
            updateCommand.Parameters.AddWithValue("target_state", (byte)ToolDispatchState.DispatchingIntent);
            updateCommand.Parameters.AddWithValue("updated_at", now);

            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected > 0)
            {
                return; // 成功推进（Applied）
            }
        }

        // 2. 0 行受影响：读取当前行状态（复合键寻址）
        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        selectCommand.CommandText = $"""
SELECT state FROM {Table("tool_dispatch_journal_entries")}
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
LIMIT 1;
""";
        selectCommand.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
        selectCommand.Parameters.AddWithValue("run_id", key.RunId);
        selectCommand.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                $"目标状态=DispatchingIntent（期望前驱=Prepared）。" +
                $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
        }

        var currentState = (ToolDispatchState)reader.GetByte(0);

        // state == target → AlreadyApplied（幂等，不报错）
        if (currentState == ToolDispatchState.DispatchingIntent)
        {
            return;
        }

        // state > DispatchingIntent (logical) → AlreadyAdvanced（抛异常，阻止重复 Dispatch）
        if (s_logicalOrder[currentState] > s_logicalOrder[ToolDispatchState.DispatchingIntent])
        {
            throw new InvalidOperationException(
                $"Tool dispatch state 已超过 DispatchingIntent（AlreadyAdvanced）：request_id={requestId}，" +
                $"当前={currentState}，目标=DispatchingIntent。" +
                $"状态已被并发推进，外部调用可能已开始，禁止重复 Dispatch。");
        }

        // state < Prepared → InvalidTransition（跨级跳跃，禁止）
        throw new InvalidOperationException(
            $"Tool dispatch state 跨级跳跃（InvalidTransition）：request_id={requestId}，" +
            $"当前={currentState}，期望前驱=Prepared，目标=DispatchingIntent。" +
            $"状态机只能逐级向前推进：Prepared → DispatchingIntent → Dispatched → Reconciling → Committed → ResultDelivered。");
    }

    /// <inheritdoc />
    public async ValueTask MarkDispatchedAsync(TenantRunKey key, string requestId, string? externalOperationId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }

        await TransitionStateAsync(
            key,
            requestId,
            expectedStates: new[] { ToolDispatchState.Prepared, ToolDispatchState.DispatchingIntent },
            targetState: ToolDispatchState.Dispatched,
            externalOperationId: externalOperationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MarkCommittedAsync(TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }

        await TransitionStateAsync(
            key,
            requestId,
            expectedStates: new[] { ToolDispatchState.Dispatched },
            targetState: ToolDispatchState.Committed,
            externalOperationId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask MarkCommittedWithResultAsync(TenantRunKey key, string requestId, DurableToolResult result, CancellationToken cancellationToken = default)
        => CommitWithResultCoreAsync(key, requestId, result, new[] { ToolDispatchState.Dispatched }, cancellationToken);

    /// <inheritdoc />
    public ValueTask MarkReconciledWithResultAsync(TenantRunKey key, string requestId, DurableToolResult result, CancellationToken cancellationToken = default)
        => CommitWithResultCoreAsync(key, requestId, result, new[] { ToolDispatchState.Reconciling }, cancellationToken);

    /// <summary>
    /// 在单个 DB 事务内同时推进 journal 状态机到 Committed（CAS，期望前驱可配置）与
    /// UPSERT 结果到 tool_dispatch_results，确保崩溃恢复时不会出现 "state=Committed 但 result 缺失" 的不一致状态。
    /// 正常提交路径期望前驱 = Dispatched；对账提交路径期望前驱 = Reconciling。
    /// </summary>
    private async ValueTask CommitWithResultCoreAsync(
        TenantRunKey key,
        string requestId,
        DurableToolResult result,
        IReadOnlyList<ToolDispatchState> expectedStates,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.RunId);

        // 在单个 DB 事务内同时推进 journal 状态机到 Committed（CAS）与 UPSERT 结果到
        // tool_dispatch_results，确保崩溃恢复时不会出现 "state=Committed 但 result 缺失" 的不一致状态。
        var now = DateTimeOffset.UtcNow;
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 1. 精确前驱状态 CAS：UPDATE WHERE (workspace_id, run_id, request_id) 复合键 AND state = @expected_state
            bool stateAdvanced;
            await using (var stateCmd = connection.CreateCommand())
            {
                stateCmd.Transaction = transaction;
                stateCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                var expectedParams = expectedStates.Select((s, i) => $"@expected_{i}").ToArray();
                var inClause = string.Join(", ", expectedParams);
                stateCmd.CommandText = $"""
UPDATE {Table("tool_dispatch_journal_entries")}
SET state = @target_state, updated_at = @updated_at
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
  AND state IN ({inClause});
""";
                stateCmd.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
                stateCmd.Parameters.AddWithValue("run_id", key.RunId);
                stateCmd.Parameters.AddWithValue("request_id", requestId);
                for (int i = 0; i < expectedStates.Count; i++)
                {
                    stateCmd.Parameters.AddWithValue($"expected_{i}", (byte)expectedStates[i]);
                }
                stateCmd.Parameters.AddWithValue("target_state", (byte)ToolDispatchState.Committed);
                stateCmd.Parameters.AddWithValue("updated_at", now);
                var affected = await stateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                stateAdvanced = affected > 0;
            }

            if (!stateAdvanced)
            {
                // 0 行受影响：读取当前行状态，区分 AlreadyApplied / AlreadyAdvanced / InvalidTransition / MissingPredecessor
                await using var selectCmd = connection.CreateCommand();
                selectCmd.Transaction = transaction;
                selectCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                selectCmd.CommandText = $"""
SELECT state FROM {Table("tool_dispatch_journal_entries")}
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
LIMIT 1;
""";
                selectCmd.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
                selectCmd.Parameters.AddWithValue("run_id", key.RunId);
                selectCmd.Parameters.AddWithValue("request_id", requestId);
                await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                        $"目标状态=Committed（期望前驱=Dispatched）。" +
                        $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
                }

                var currentState = (ToolDispatchState)reader.GetByte(0);
                // state == target 或 state > target → 幂等成功（AlreadyApplied/AlreadyAdvanced），继续 UPSERT 结果
                var minExpectedLogical = expectedStates.Min(s => s_logicalOrder[s]);
                if (s_logicalOrder[currentState] < minExpectedLogical)
                {
                    throw new InvalidOperationException(
                        $"Tool dispatch state 跨级跳跃（InvalidTransition）：request_id={requestId}，" +
                        $"当前={currentState}，期望前驱={string.Join("/", expectedStates)}，目标=Committed。" +
                        $"状态机只能逐级向前推进：Prepared → DispatchingIntent → Dispatched → Reconciling → Committed → ResultDelivered，" +
                        $"不允许跳过中间状态（如 Prepared → Committed）。");
                }
                // state >= Committed → AlreadyApplied/AlreadyAdvanced：继续 UPSERT 结果（幂等覆盖）
            }

            // 2. UPSERT 结果到 tool_dispatch_results（同一事务）
            await using (var resultCmd = connection.CreateCommand())
            {
                resultCmd.Transaction = transaction;
                resultCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                resultCmd.CommandText = $"""
INSERT INTO {Table("tool_dispatch_results")} (
    tool_call_id, request_id, workspace_id, run_id, invocation_id, idempotency_key,
    side_effect, external_operation_id, result, succeeded, error, duration_ms, created_at)
VALUES (
    @tool_call_id, @request_id, @workspace_id, @run_id, @invocation_id, @idempotency_key,
    @side_effect, @external_operation_id, @result, @succeeded, @error, @duration_ms, @created_at)
ON CONFLICT (workspace_id, run_id, request_id) DO UPDATE SET
    tool_call_id = EXCLUDED.tool_call_id,
    invocation_id = EXCLUDED.invocation_id,
    idempotency_key = EXCLUDED.idempotency_key,
    side_effect = EXCLUDED.side_effect,
    external_operation_id = EXCLUDED.external_operation_id,
    result = EXCLUDED.result,
    succeeded = EXCLUDED.succeeded,
    error = EXCLUDED.error,
    duration_ms = EXCLUDED.duration_ms,
    created_at = EXCLUDED.created_at;
""";
                resultCmd.Parameters.AddWithValue("tool_call_id", result.ToolCallId);
                resultCmd.Parameters.AddWithValue("request_id", result.RequestId);
                // 复合主键 (workspace_id, run_id, request_id) 以租户键为准（与 journal 条目一致），
                // 不依赖 result 负载携带（防负载与键不一致时插入失败）。
                resultCmd.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
                resultCmd.Parameters.AddWithValue("run_id", key.RunId);
                resultCmd.Parameters.AddWithValue("invocation_id", (object?)result.InvocationId ?? DBNull.Value);
                resultCmd.Parameters.AddWithValue("idempotency_key", (object?)result.IdempotencyKey ?? DBNull.Value);
                resultCmd.Parameters.AddWithValue("side_effect", result.SideEffect.ToString());
                resultCmd.Parameters.AddWithValue("external_operation_id", (object?)result.ExternalOperationId ?? DBNull.Value);
                AddJson(resultCmd, "result", result);
                resultCmd.Parameters.AddWithValue("succeeded", result.Succeeded);
                resultCmd.Parameters.AddWithValue("error", (object?)result.Error ?? DBNull.Value);
                resultCmd.Parameters.AddWithValue("duration_ms", (long)Math.Round(result.DurationMs));
                resultCmd.Parameters.AddWithValue("created_at", now);
                await resultCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 将模糊态（DispatchingIntent/Dispatched）显式推进到 Reconciling，
    /// 表示外部副作用真相正在确认。CAS 原子推进；已 Reconciling/已提交（>Reconciling）幂等成功；
    /// Prepared（外部调用从未开始）抛 InvalidTransition——它应被重新 Dispatch 而非对账。
    /// </remarks>
    public async ValueTask BeginReconciliationAsync(TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(key.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.RunId);

        var now = DateTimeOffset.UtcNow;
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. CAS：DispatchingIntent/Dispatched → Reconciling（复合键寻址）
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            updateCommand.CommandText = $"""
UPDATE {Table("tool_dispatch_journal_entries")}
SET state = @target_state, updated_at = @updated_at
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
  AND state IN (@expected_0, @expected_1);
""";
            updateCommand.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
            updateCommand.Parameters.AddWithValue("run_id", key.RunId);
            updateCommand.Parameters.AddWithValue("request_id", requestId);
            updateCommand.Parameters.AddWithValue("expected_0", (byte)ToolDispatchState.DispatchingIntent);
            updateCommand.Parameters.AddWithValue("expected_1", (byte)ToolDispatchState.Dispatched);
            updateCommand.Parameters.AddWithValue("target_state", (byte)ToolDispatchState.Reconciling);
            updateCommand.Parameters.AddWithValue("updated_at", now);
            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected > 0)
            {
                return; // 成功进入对账状态
            }
        }

        // 2. 0 行受影响：读取当前行状态，区分 AlreadyApplied / AlreadyAdvanced / InvalidTransition / MissingPredecessor
        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        selectCommand.CommandText = $"""
SELECT state FROM {Table("tool_dispatch_journal_entries")}
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
LIMIT 1;
""";
        selectCommand.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
        selectCommand.Parameters.AddWithValue("run_id", key.RunId);
        selectCommand.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                $"目标状态=Reconciling（期望前驱=DispatchingIntent/Dispatched）。" +
                $"必须先调用 PrepareAsync 写入条目，再推进状态机。");
        }

        var currentState = (ToolDispatchState)reader.GetByte(0);

        // state == target → AlreadyApplied（幂等，不报错）
        if (currentState == ToolDispatchState.Reconciling)
        {
            return;
        }

        // state > Reconciling (logical) → AlreadyAdvanced（幂等：已提交/已送达，对账已完成无需重入）
        if (s_logicalOrder[currentState] > s_logicalOrder[ToolDispatchState.Reconciling])
        {
            return;
        }

        // state == Prepared → InvalidTransition（Prepared 表示外部调用从未开始，应重新 Dispatch 而非对账）
        throw new InvalidOperationException(
            $"Tool dispatch state 跨级跳跃（InvalidTransition）：request_id={requestId}，" +
            $"当前={currentState}，期望前驱=DispatchingIntent/Dispatched，目标=Reconciling。" +
            $"Prepared 状态表示外部调用从未开始——应重新 Dispatch（PrepareWithIntentAsync 会原子推进），而非进入对账。");
    }

    /// <inheritdoc />
    public async ValueTask MarkResultDeliveredAsync(TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }

        await TransitionStateAsync(
            key,
            requestId,
            expectedStates: new[] { ToolDispatchState.Committed },
            targetState: ToolDispatchState.ResultDelivered,
            externalOperationId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ToolDispatchJournalEntry?> GetEntryAsync(TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(key.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.RunId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 完整租户键寻址（workspace_id + run_id + request_id）。
        command.CommandText = $"""
SELECT tool_name, state, idempotency_key, external_operation_id, updated_at, diagnostic_note,
       payload_digest, workspace_id, run_id
FROM {Table("tool_dispatch_journal_entries")}
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
        command.Parameters.AddWithValue("run_id", key.RunId);
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

    /// <inheritdoc />
    public async ValueTask<ToolDispatchState?> GetStateAsync(
        TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(key.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.RunId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 完整租户键（workspace_id + run_id + request_id）——：异常路径必须读取真实状态。
        command.CommandText = $"""
SELECT state
FROM {Table("tool_dispatch_journal_entries")}
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
        command.Parameters.AddWithValue("run_id", key.RunId);
        command.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (ToolDispatchState)reader.GetByte(0);
    }

    /// <summary>
    /// 原子推进状态机（精确前驱状态 CAS）：
    /// <c>UPDATE ... WHERE (workspace_id, run_id, request_id) 复合键 AND state = @expected_state</c>。
    /// <list type="bullet">
    /// <item>1 行受影响（state = expected） → 成功前向推进（Applied）。</item>
    /// <item>0 行受影响且行存在（state = target） → 幂等成功（AlreadyApplied，不报错）。</item>
    /// <item>0 行受影响且行存在（state &gt; target） → 幂等成功（AlreadyAdvanced，不报错）。</item>
    /// <item>0 行受影响且行存在（state &lt; expected） → 抛 <see cref="InvalidOperationException"/>（InvalidTransition，禁止跨级跳跃）。</item>
    /// <item>0 行受影响且行不存在 → 抛 <see cref="InvalidOperationException"/>（MissingPredecessor，审计链断裂）。</item>
    /// </list>
    /// 不自动创建 stub 条目。缺失记录意味着审计链不完整，必须让调用方感知冲突。
    /// </summary>
    private async Task TransitionStateAsync(
        TenantRunKey key,
        string requestId,
        IReadOnlyList<ToolDispatchState> expectedStates,
        ToolDispatchState targetState,
        string? externalOperationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.RunId);

        var now = DateTimeOffset.UtcNow;
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. 精确前驱状态 CAS：UPDATE WHERE 复合键 AND state = @expected_state
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            var setExternalOp = targetState == ToolDispatchState.Dispatched
                ? ", external_operation_id = COALESCE(@external_operation_id, external_operation_id)"
                : string.Empty;
            var expectedParams = expectedStates.Select((s, i) => $"@expected_{i}").ToArray();
            var inClause = string.Join(", ", expectedParams);
            updateCommand.CommandText = $"""
UPDATE {Table("tool_dispatch_journal_entries")}
SET state = @target_state{setExternalOp}, updated_at = @updated_at
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
  AND state IN ({inClause});
""";
            updateCommand.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
            updateCommand.Parameters.AddWithValue("run_id", key.RunId);
            updateCommand.Parameters.AddWithValue("request_id", requestId);
            for (int i = 0; i < expectedStates.Count; i++)
            {
                updateCommand.Parameters.AddWithValue($"expected_{i}", (byte)expectedStates[i]);
            }
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
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND request_id = @request_id
LIMIT 1;
""";
        selectCommand.Parameters.AddWithValue("workspace_id", key.WorkspaceId);
        selectCommand.Parameters.AddWithValue("run_id", key.RunId);
        selectCommand.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // 行不存在 → 审计链断裂（MissingPredecessor）
            throw new InvalidOperationException(
                $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                $"目标状态={targetState}（期望前驱={string.Join("/", expectedStates)}）。" +
                $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。" +
                $"缺失记录意味着审计链不完整，可能丢失了 Prepare 步骤或数据库状态被外部篡改。");
        }

        var currentState = (ToolDispatchState)reader.GetByte(0);

        // state == target → AlreadyApplied（幂等，不报错）
        if (currentState == targetState)
        {
            return;
        }

        // state > target (logical) → AlreadyAdvanced（幂等，不报错）
        if (s_logicalOrder[currentState] > s_logicalOrder[targetState])
        {
            return;
        }

        // state < expected → InvalidTransition（跨级跳跃，禁止）
        throw new InvalidOperationException(
            $"Tool dispatch state 跨级跳跃（InvalidTransition）：request_id={requestId}，" +
            $"当前={currentState}，期望前驱={string.Join("/", expectedStates)}，目标={targetState}。" +
            $"状态机只能逐级向前推进：Prepared → DispatchingIntent → Dispatched → Reconciling → Committed → ResultDelivered，" +
            $"不允许跳过中间状态（如 Prepared → Committed）。");
    }
}
