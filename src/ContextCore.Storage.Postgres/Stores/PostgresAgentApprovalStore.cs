using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// 运行时能力补齐：PostgreSQL 持久化 Agent Approval Store。
/// 让 durable approval 状态可跨进程持久化与崩溃恢复。
/// </summary>
/// <remarks>
/// 设计要点（参考 <see cref="PostgresAgentRunStore"/>）：
///   1. 表 <c>agent_run_approvals</c> 反规范化 workspace_id / approval_id / run_id / tool_call_id /
///      tool_name / status 字段以便索引查询；完整 <see cref="AgentApproval"/> 对象保存在 <c>data jsonb</c>。
///   2. 主键 (workspace_id, approval_id)：跨 workspace 隔离 + 同 workspace 内 approval_id 唯一。
///   3. <see cref="CreateAsync"/> 使用 <c>INSERT ... ON CONFLICT DO NOTHING</c> 保证幂等。
///   4. <see cref="ResolveAsync"/> 使用 expected-state CAS：
///      <c>UPDATE ... SET status=@new WHERE status=Pending</c>；0 行受影响时抛 <see cref="InvalidOperationException"/>。
/// </remarks>
public sealed class PostgresAgentApprovalStore : PostgresStoreBase, IAgentApprovalStore, IPersistentAgentApprovalStore
{
    /// <summary>初始化 Postgres 持久化 Agent Approval Store。</summary>
    public PostgresAgentApprovalStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async ValueTask CreateAsync(AgentApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("agent_run_approvals")} (
    workspace_id, approval_id, run_id, tool_call_id, tool_name, status,
    reason, rejection_reason, approver_id, created_at, resolved_at, data)
VALUES (
    @workspace_id, @approval_id, @run_id, @tool_call_id, @tool_name, @status,
    @reason, @rejection_reason, @approver_id, @created_at, @resolved_at, @data)
ON CONFLICT (workspace_id, approval_id) DO NOTHING;
""";
        command.Parameters.AddWithValue("workspace_id", approval.WorkspaceId);
        command.Parameters.AddWithValue("approval_id", approval.ApprovalId);
        command.Parameters.AddWithValue("run_id", approval.RunId);
        command.Parameters.AddWithValue("tool_call_id", approval.ToolCallId);
        command.Parameters.AddWithValue("tool_name", approval.ToolName ?? string.Empty);
        command.Parameters.AddWithValue("status", (byte)approval.Status);
        command.Parameters.AddWithValue("reason", (object?)approval.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("rejection_reason", (object?)approval.RejectionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("approver_id", (object?)approval.ApproverId ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", approval.CreatedAt);
        command.Parameters.AddWithValue("resolved_at", (object?)approval.ResolvedAt ?? DBNull.Value);
        AddJson(command, "data", approval);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<AgentApproval?> GetAsync(string workspaceId, string approvalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_run_approvals")}
WHERE workspace_id = @workspace_id AND approval_id = @approval_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("approval_id", approvalId);
        return await ExecuteScalarJsonAsync<AgentApproval>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentApproval>> ListPendingAsync(
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
SELECT data
FROM {Table("agent_run_approvals")}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND status = @pending_status
ORDER BY created_at ASC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("pending_status", (byte)AgentApprovalStatus.Pending);
        return await ExecuteReaderJsonAsync<AgentApproval>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ResolveAsync(
        string workspaceId,
        string approvalId,
        AgentApprovalStatus decision,
        string? approverId,
        string? rejectionReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        if (decision != AgentApprovalStatus.Approved && decision != AgentApprovalStatus.Rejected)
        {
            throw new ArgumentException(
                $"decision 必须为 Approved 或 Rejected，实际为 {decision}。", nameof(decision));
        }

        var now = DateTimeOffset.UtcNow;
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. expected-state CAS：UPDATE WHERE status = Pending
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            // 同步更新 data JSON 中的 Status / ApproverId / RejectionReason / ResolvedAt 字段
            updateCommand.CommandText = $"""
UPDATE {Table("agent_run_approvals")}
SET status = @new_status,
    approver_id = @approver_id,
    rejection_reason = @rejection_reason,
    resolved_at = @resolved_at,
    data = data || jsonb_build_object(
        'Status', to_jsonb(@new_status_name),
        'ApproverId', to_jsonb(@approver_id::text),
        'RejectionReason', to_jsonb(@rejection_reason::text),
        'ResolvedAt', to_jsonb(@resolved_at))
WHERE workspace_id = @workspace_id AND approval_id = @approval_id AND status = @pending_status;
""";
            updateCommand.Parameters.AddWithValue("workspace_id", workspaceId);
            updateCommand.Parameters.AddWithValue("approval_id", approvalId);
            updateCommand.Parameters.AddWithValue("pending_status", (byte)AgentApprovalStatus.Pending);
            updateCommand.Parameters.AddWithValue("new_status", (byte)decision);
            updateCommand.Parameters.AddWithValue("new_status_name", decision.ToString());
            updateCommand.Parameters.AddWithValue("approver_id", (object?)approverId ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("rejection_reason",
                decision == AgentApprovalStatus.Rejected ? (object?)rejectionReason ?? DBNull.Value : DBNull.Value);
            updateCommand.Parameters.AddWithValue("resolved_at", now);

            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected > 0)
            {
                return; // CAS 成功
            }
        }

        // 2. 0 行受影响：检查行是否存在以区分"已裁决"与"不存在"
        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        selectCommand.CommandText = $"""
SELECT status FROM {Table("agent_run_approvals")}
WHERE workspace_id = @workspace_id AND approval_id = @approval_id
LIMIT 1;
""";
        selectCommand.Parameters.AddWithValue("workspace_id", workspaceId);
        selectCommand.Parameters.AddWithValue("approval_id", approvalId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var currentStatus = (AgentApprovalStatus)reader.GetByte(0);
            throw new InvalidOperationException(
                $"审批 CAS 失败：approval_id={approvalId}，期望当前状态=Pending，实际={currentStatus}。" +
                $"审批已被裁决，不可重复裁决。");
        }

        throw new InvalidOperationException(
            $"审批记录不存在：workspace_id={workspaceId}, approval_id={approvalId}。");
    }

    /// <summary>
    /// 原子裁决审批 + 追加审批事件 + CAS 推进 Run 状态（单 PostgreSQL 事务）。
    /// </summary>
    /// <remarks>
    /// 旧路径 ResolveAsync → AppendAsync → TransitionStateAsync 三步非原子，任一步失败留下不一致状态。
    /// 本方法在单事务内完成：校验 approval.RunId → CAS 裁决审批 → INSERT 事件（哈希链续接）→ CAS 推进 Run 状态。
    /// Run 状态 CAS 改为严格规则——0 行时查询当前状态：已处于目标状态则幂等提交，其他状态或不存在则整事务回滚
    /// （旧版本 best-effort：0 行时仍 COMMIT，审批已裁决但 Run 仍处 AwaitingApproval，外部无法再次 Resolve，Run 永久卡死）。
    /// </remarks>
    public async ValueTask<ApprovalResolveResult> ResolveApprovalAndAdvanceRunAsync(
        string workspaceId,
        string runId,
        string approvalId,
        AgentRunState expectedRunState,
        AgentApprovalStatus decision,
        string? approverId,
        string? rejectionReason,
        AgentRunEvent approvalEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentNullException.ThrowIfNull(approvalEvent);
        if (decision != AgentApprovalStatus.Approved && decision != AgentApprovalStatus.Rejected)
        {
            throw new ArgumentException(
                $"decision 必须为 Approved 或 Rejected，实际为 {decision}。", nameof(decision));
        }

        // 目标状态：批准 → PendingToolExecution（Actor 恢复时直接执行原 Tool）；
        //           拒绝 → Failed（终态，设置 finished_at）。
        var newState = decision == AgentApprovalStatus.Approved
            ? AgentRunState.PendingToolExecution
            : AgentRunState.Failed;
        var isTerminal = newState == AgentRunState.Failed;
        var now = DateTimeOffset.UtcNow;

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 1. 校验 approval 存在且 RunId 匹配（防跨 Run 误裁决）
        byte currentStatusByte;
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            selectCommand.CommandText = $"""
SELECT status
FROM {Table("agent_run_approvals")}
WHERE workspace_id = @workspace_id AND approval_id = @approval_id AND run_id = @run_id
LIMIT 1;
""";
            selectCommand.Parameters.AddWithValue("workspace_id", workspaceId);
            selectCommand.Parameters.AddWithValue("approval_id", approvalId);
            selectCommand.Parameters.AddWithValue("run_id", runId);
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new ApprovalResolveResult
                {
                    Succeeded = false,
                    ApprovalResolved = false,
                    RunStateChanged = false,
                    FailureReason = $"审批记录不存在或 runId 不匹配：workspace_id={workspaceId}, approval_id={approvalId}, run_id={runId}。"
                };
            }
            currentStatusByte = reader.GetByte(0);
        }

        // 2. CAS 裁决审批（UPDATE WHERE status=Pending）
        int approvalAffected;
        await using (var resolveCommand = connection.CreateCommand())
        {
            resolveCommand.Transaction = transaction;
            resolveCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            resolveCommand.CommandText = $"""
UPDATE {Table("agent_run_approvals")}
SET status = @new_status,
    approver_id = @approver_id,
    rejection_reason = @rejection_reason,
    resolved_at = @resolved_at,
    data = data || jsonb_build_object(
        'Status', to_jsonb(@new_status_name),
        'ApproverId', to_jsonb(@approver_id::text),
        'RejectionReason', to_jsonb(@rejection_reason::text),
        'ResolvedAt', to_jsonb(@resolved_at))
WHERE workspace_id = @workspace_id AND approval_id = @approval_id AND status = @pending_status;
""";
            resolveCommand.Parameters.AddWithValue("workspace_id", workspaceId);
            resolveCommand.Parameters.AddWithValue("approval_id", approvalId);
            resolveCommand.Parameters.AddWithValue("pending_status", (byte)AgentApprovalStatus.Pending);
            resolveCommand.Parameters.AddWithValue("new_status", (byte)decision);
            resolveCommand.Parameters.AddWithValue("new_status_name", decision.ToString());
            resolveCommand.Parameters.AddWithValue("approver_id", (object?)approverId ?? DBNull.Value);
            resolveCommand.Parameters.AddWithValue("rejection_reason",
                decision == AgentApprovalStatus.Rejected ? (object?)rejectionReason ?? DBNull.Value : DBNull.Value);
            resolveCommand.Parameters.AddWithValue("resolved_at", now);
            approvalAffected = await resolveCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (approvalAffected == 0)
        {
            // 步骤 1 已确认记录存在且 runId 匹配，0 行 = status ≠ Pending（已被并发裁决）
            var currentStatus = (AgentApprovalStatus)currentStatusByte;
            return new ApprovalResolveResult
            {
                Succeeded = false,
                ApprovalResolved = false,
                RunStateChanged = false,
                FailureReason = $"审批已裁决，不可重复裁决：approval_id={approvalId}，当前状态={currentStatus}。"
            };
        }

        // 3. 校验事件 Sequence 连续性 + PrevChainHash 链接，然后 INSERT 事件
        int expectedSequence;
        string? expectedPrevHash;
        await using (var lastEventCommand = connection.CreateCommand())
        {
            lastEventCommand.Transaction = transaction;
            lastEventCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            lastEventCommand.CommandText = $"""
SELECT sequence, content_hash
FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
ORDER BY sequence DESC
LIMIT 1;
""";
            lastEventCommand.Parameters.AddWithValue("workspace_id", workspaceId);
            lastEventCommand.Parameters.AddWithValue("run_id", runId);
            await using var eventReader = await lastEventCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await eventReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expectedSequence = eventReader.GetInt32(0) + 1;
                expectedPrevHash = eventReader.IsDBNull(1) ? null : eventReader.GetString(1);
            }
            else
            {
                expectedSequence = 0;
                expectedPrevHash = null;
            }
        }

        if (approvalEvent.Sequence != expectedSequence)
        {
            return new ApprovalResolveResult
            {
                Succeeded = false,
                ApprovalResolved = false,
                RunStateChanged = false,
                FailureReason = $"审批事件 Sequence 不连续：期望={expectedSequence}，实际={approvalEvent.Sequence}。"
            };
        }

        if (!string.Equals(expectedPrevHash, approvalEvent.PrevChainHash, StringComparison.Ordinal))
        {
            return new ApprovalResolveResult
            {
                Succeeded = false,
                ApprovalResolved = false,
                RunStateChanged = false,
                FailureReason = "审批事件 PrevChainHash 不匹配，事件哈希链被破坏或乱序。"
            };
        }

        await using (var insertEventCommand = connection.CreateCommand())
        {
            insertEventCommand.Transaction = transaction;
            insertEventCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            insertEventCommand.CommandText = $"""
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
            insertEventCommand.Parameters.AddWithValue("event_id", approvalEvent.EventId);
            insertEventCommand.Parameters.AddWithValue("workspace_id", workspaceId);
            insertEventCommand.Parameters.AddWithValue("run_id", runId);
            insertEventCommand.Parameters.AddWithValue("sequence", approvalEvent.Sequence);
            insertEventCommand.Parameters.AddWithValue("event_type", (short)approvalEvent.EventType);
            insertEventCommand.Parameters.AddWithValue("state", (short)approvalEvent.State);
            insertEventCommand.Parameters.AddWithValue("payload", approvalEvent.Payload ?? string.Empty);
            insertEventCommand.Parameters.AddWithValue("content_hash", (object?)approvalEvent.ContentHash ?? DBNull.Value);
            insertEventCommand.Parameters.AddWithValue("prev_chain_hash", (object?)approvalEvent.PrevChainHash ?? DBNull.Value);
            insertEventCommand.Parameters.AddWithValue("occurred_at", approvalEvent.OccurredAt);
            AddJson(insertEventCommand, "data", approvalEvent);

            var eventAffected = await insertEventCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (eventAffected == 0)
            {
                return new ApprovalResolveResult
                {
                    Succeeded = false,
                    ApprovalResolved = false,
                    RunStateChanged = false,
                    FailureReason = $"审批事件 Sequence 冲突：sequence={approvalEvent.Sequence} 已存在。"
                };
            }
        }

        // 4. CAS 推进 Run 状态（P0-5：严格规则 — 0 行时查询当前状态区分幂等成功与冲突失败）
        int runAffected;
        await using (var runUpdateCommand = connection.CreateCommand())
        {
            runUpdateCommand.Transaction = transaction;
            runUpdateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            var setFinished = isTerminal ? ", finished_at = @finished_at" : string.Empty;
            var dataMerge = isTerminal
                ? "data = data || jsonb_build_object('State', to_jsonb(@new_state_name), 'UpdatedAt', to_jsonb(@updated_at), 'FinishedAt', to_jsonb(@finished_at))"
                : "data = data || jsonb_build_object('State', to_jsonb(@new_state_name), 'UpdatedAt', to_jsonb(@updated_at))";
            runUpdateCommand.CommandText = $"""
UPDATE {Table("agent_runs")}
SET state = @new_state, updated_at = @updated_at{setFinished}, {dataMerge}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND state = @expected_state;
""";
            runUpdateCommand.Parameters.AddWithValue("workspace_id", workspaceId);
            runUpdateCommand.Parameters.AddWithValue("run_id", runId);
            runUpdateCommand.Parameters.AddWithValue("expected_state", (byte)expectedRunState);
            runUpdateCommand.Parameters.AddWithValue("new_state", (byte)newState);
            runUpdateCommand.Parameters.AddWithValue("new_state_name", newState.ToString());
            runUpdateCommand.Parameters.AddWithValue("updated_at", now);
            if (isTerminal)
            {
                runUpdateCommand.Parameters.AddWithValue("finished_at", now);
            }
            runAffected = await runUpdateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 严格规则 — Run CAS 0 行时查询当前状态，区分幂等成功与冲突失败。
        // 旧版本（best-effort）：0 行时仍 COMMIT（RunStateChanged=false），审批已裁决但 Run 状态未推进，
        // 留下半事务状态（Run 仍处 AwaitingApproval，但审批已 Resolved，外部无法再次 Resolve）。
        if (runAffected == 0)
        {
            byte currentRunStateByte;
            await using (var runStateCommand = connection.CreateCommand())
            {
                runStateCommand.Transaction = transaction;
                runStateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                runStateCommand.CommandText = $"""
SELECT state FROM {Table("agent_runs")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
LIMIT 1;
""";
                runStateCommand.Parameters.AddWithValue("workspace_id", workspaceId);
                runStateCommand.Parameters.AddWithValue("run_id", runId);
                await using var runReader = await runStateCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await runReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Run 不存在 — 回滚（审批裁决 + 事件追加一并撤销）
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new ApprovalResolveResult
                    {
                        Succeeded = false,
                        ApprovalResolved = false,
                        RunStateChanged = false,
                        FailureReason = $"Run 不存在：workspace_id={workspaceId}, run_id={runId}。审批裁决已回滚。"
                    };
                }
                currentRunStateByte = runReader.GetByte(0);
            }

            var currentRunState = (AgentRunState)currentRunStateByte;
            if (currentRunState == newState)
            {
                // 幂等成功：Run 已处于目标状态 → 提交事务（审批裁决 + 事件追加生效）
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ApprovalResolveResult
                {
                    Succeeded = true,
                    ApprovalResolved = true,
                    RunStateChanged = true,
                    NewRunState = newState
                };
            }

            // 状态冲突 — 回滚（审批裁决 + 事件追加一并撤销）
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new ApprovalResolveResult
            {
                Succeeded = false,
                ApprovalResolved = false,
                RunStateChanged = false,
                FailureReason = $"Run 状态 CAS 失败：期望={expectedRunState}，目标={newState}，实际={currentRunState}。审批裁决已回滚。"
            };
        }

        // 5. COMMIT（审批裁决 + 事件追加 + Run 状态推进原子提交）
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ApprovalResolveResult
        {
            Succeeded = true,
            ApprovalResolved = true,
            RunStateChanged = true,
            NewRunState = newState
        };
    }
}
