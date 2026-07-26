using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 任务 E5：DefaultAgentApprovalGate — 默认审批门
//
// 实现 IAgentApprovalGate 的默认审批策略：
//   1. 自动审批模式：低风险操作直接 Approved=true（默认行为）。
//   2. 可配置需要审批的 Tool 列表（构造注入）。
//   3. 默认全部自动批准（测试用；生产环境应替换为人工审批实现）。
//
// 设计决策：
//   - 默认行为是"全部自动批准"（兼容现有 Kernel 测试场景，不阻塞流程）；
//   - 通过构造注入 approvalRequiredTools 可显式标记某些 Tool 需要人工审批；
//   - 自动审批的 ApproverId 为 "auto-rule"，便于审计区分；
//   - 不引入任何持久化状态，无副作用。
// ===========================================================================

/// <summary>
/// 任务 E5：默认审批门实现。
/// 默认全部自动批准（测试用）；可通过构造参数配置需要人工审批的 Tool 列表。
/// </summary>
public sealed class DefaultAgentApprovalGate : IAgentApprovalGate
{
    private readonly IReadOnlySet<string> _approvalRequiredTools;
    private readonly bool _autoApproveAll;

    /// <summary>
    /// 构造默认审批门。
    /// </summary>
    /// <param name="approvalRequiredTools">需要人工审批的 Tool 名称集合（匹配时返回 Approved=false 等待人工裁决）。</param>
    /// <param name="autoApproveAll">是否全部自动批准（true 时忽略 approvalRequiredTools；默认 true 用于测试场景）。</param>
    public DefaultAgentApprovalGate(
        IReadOnlySet<string>? approvalRequiredTools = null,
        bool autoApproveAll = true)
    {
        _approvalRequiredTools = approvalRequiredTools ?? new HashSet<string>(0, StringComparer.OrdinalIgnoreCase);
        _autoApproveAll = autoApproveAll;
    }

    /// <inheritdoc />
    public ValueTask<AgentApprovalResult> RequestApprovalAsync(
        string runId,
        AgentToolCallRequest toolCall,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        // 默认全部自动批准（测试用）
        if (_autoApproveAll)
        {
            return ValueTask.FromResult(new AgentApprovalResult
            {
                Approved = true,
                RejectionReason = null,
                ApproverId = "auto-rule",
                DecidedAt = DateTimeOffset.UtcNow
            });
        }

        // 配置模式下：检查是否在需审批列表中
        if (!string.IsNullOrWhiteSpace(toolCall.ToolName)
            && _approvalRequiredTools.Contains(toolCall.ToolName))
        {
            // 默认审批门不接入人工流程 → 返回拒绝（生产环境应替换为真实人工审批实现）
            return ValueTask.FromResult(new AgentApprovalResult
            {
                Approved = false,
                RejectionReason = $"Tool '{toolCall.ToolName}' 需要人工审批，但当前审批门未配置人工流程。",
                ApproverId = "auto-rule-pending",
                DecidedAt = DateTimeOffset.UtcNow
            });
        }

        // 不在需审批列表中 → 自动批准
        return ValueTask.FromResult(new AgentApprovalResult
        {
            Approved = true,
            RejectionReason = null,
            ApproverId = "auto-rule",
            DecidedAt = DateTimeOffset.UtcNow
        });
    }
}
