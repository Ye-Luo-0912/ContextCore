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
}
