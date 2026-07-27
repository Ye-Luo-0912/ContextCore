using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 运行时能力补齐：InMemoryAgentApprovalStore — 进程内审批持久化（开发/测试用）
//
// 实现 IAgentApprovalStore 的进程内默认实现，与 InMemoryAgentRunStore 模式对齐：
//   - ConcurrentDictionary 维护 (workspaceId, approvalId) → AgentApproval 映射；
//   - CreateAsync 幂等（同主键 TryAdd 不覆盖）；
//   - ResolveAsync 使用 expected-state CAS（Status=Pending → Approved/Rejected）。
//
// 设计决策：
//   - 不持久化到磁盘：进程崩溃后状态丢失。生产部署应注入持久化实现。
//   - 线程安全：所有读写通过 ConcurrentDictionary 原子操作。
// ===========================================================================

/// <summary>
/// 进程内 Agent Approval Store 默认实现（开发/测试用）。
/// </summary>
/// <remarks>
/// <b>此实现不持久化</b>：进程崩溃后审批状态丢失。
/// 生产部署应注入基于 DB 的持久化实现（如 <c>PostgresAgentApprovalStore</c>）。
/// </remarks>
public sealed class InMemoryAgentApprovalStore : IAgentApprovalStore
{
    private readonly ConcurrentDictionary<string, AgentApproval> _approvals = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask CreateAsync(AgentApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var key = Key(approval.WorkspaceId, approval.ApprovalId);
        // 幂等：同主键 TryAdd 不覆盖（与 Postgres ON CONFLICT DO NOTHING 一致）
        _approvals.TryAdd(key, approval);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<AgentApproval?> GetAsync(string workspaceId, string approvalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        _approvals.TryGetValue(Key(workspaceId, approvalId), out var approval);
        return ValueTask.FromResult(approval);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AgentApproval>> ListPendingAsync(
        string workspaceId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var results = _approvals.Values
            .Where(a => a.WorkspaceId == workspaceId && a.RunId == runId && a.Status == AgentApprovalStatus.Pending)
            .OrderBy(a => a.CreatedAt)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<AgentApproval>>(results);
    }

    /// <inheritdoc />
    public ValueTask ResolveAsync(
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

        var key = Key(workspaceId, approvalId);
        while (true)
        {
            if (!_approvals.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    $"审批记录不存在：workspace_id={workspaceId}, approval_id={approvalId}。");
            }

            if (existing.Status != AgentApprovalStatus.Pending)
            {
                throw new InvalidOperationException(
                    $"审批 CAS 失败：approval_id={approvalId}，期望当前状态=Pending，实际={existing.Status}。" +
                    $"审批已被裁决，不可重复裁决。");
            }

            var resolved = existing with
            {
                Status = decision,
                ApproverId = approverId,
                RejectionReason = decision == AgentApprovalStatus.Rejected ? rejectionReason : null,
                ResolvedAt = DateTimeOffset.UtcNow
            };

            if (_approvals.TryUpdate(key, resolved, existing))
            {
                return ValueTask.CompletedTask;
            }
            // CAS 失败 = 被并发修改 → 重试
        }
    }

    private static string Key(string workspaceId, string approvalId)
        => $"{workspaceId}:{approvalId}";
}
