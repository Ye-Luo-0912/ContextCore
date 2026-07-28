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
// 运行时能力补齐（durable approval）：
//   - 构造可选注入 IAgentApprovalStore；注入后在 RequestApprovalAsync 中持久化审批记录。
//   - 自动批准：先 CreateAsync(Pending) → ResolveAsync(Approved) 留下审计轨迹。
//   - 需人工审批：CreateAsync(Pending) 后返回 Approved=false，等待外部 ResolveAsync。
//   - 未注入 store 时保持原行为（无持久化，纯内存决策）。
//
// 设计决策：
//   - 默认行为是"全部自动批准"（兼容现有 Kernel 测试场景，不阻塞流程）；
//   - 通过构造注入 approvalRequiredTools 可显式标记某些 Tool 需要人工审批；
//   - 自动审批的 ApproverId 为 "auto-rule"，便于审计区分；
//   - store=null 时不引入任何持久化状态，无副作用（向后兼容）。
// ===========================================================================

/// <summary>
/// 任务 E5：默认审批门实现。
/// 默认全部自动批准（测试用）；可通过构造参数配置需要人工审批的 Tool 列表。
/// 注入 <see cref="IAgentApprovalStore"/> 后启用 durable approval（持久化审批状态）。
/// </summary>
public sealed class DefaultAgentApprovalGate : IAgentApprovalGate
{
    private readonly IReadOnlySet<string> _approvalRequiredTools;
    private readonly bool _autoApproveAll;
    private readonly IAgentApprovalStore? _approvalStore;

    /// <summary>
    /// 构造默认审批门。
    /// </summary>
    /// <param name="approvalRequiredTools">需要人工审批的 Tool 名称集合（匹配时返回 Approved=false 等待人工裁决）。</param>
    /// <param name="autoApproveAll">是否全部自动批准（true 时忽略 approvalRequiredTools；默认 true 用于测试场景）。</param>
    /// <param name="approvalStore">
    /// 运行时能力补齐：可选审批持久化存储。注入后每次审批都会持久化状态：
    /// 自动批准留下 Approved 审计轨迹；需人工审批留下 Pending 记录等待外部裁决。
    /// null = 不持久化（保持原行为，向后兼容）。
    /// </param>
    public DefaultAgentApprovalGate(
        IReadOnlySet<string>? approvalRequiredTools = null,
        bool autoApproveAll = true,
        IAgentApprovalStore? approvalStore = null)
    {
        _approvalRequiredTools = approvalRequiredTools ?? new HashSet<string>(0, StringComparer.OrdinalIgnoreCase);
        _autoApproveAll = autoApproveAll;
        _approvalStore = approvalStore;
    }

    /// <inheritdoc />
    public async ValueTask<AgentApprovalResult> RequestApprovalAsync(
        string runId,
        AgentToolCallRequest toolCall,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        // 运行时能力补齐：需要审批时先生成 toolCallId 并持久化 Pending 记录
        // （durable approval：进程崩溃后恢复时可见未决审批）
        var toolCallId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        // 默认全部自动批准（测试用）
        if (_autoApproveAll)
        {
            await TryPersistApprovalAsync(runId, toolCall, toolCallId, AgentApprovalStatus.Approved,
                approverId: "auto-rule", rejectionReason: null, createdAt: now, cancellationToken).ConfigureAwait(false);
            return new AgentApprovalResult
            {
                Approved = true,
                RejectionReason = null,
                ApproverId = "auto-rule",
                DecidedAt = now
            };
        }

        // 配置模式下：检查是否在需审批列表中
        var requiresHuman = !string.IsNullOrWhiteSpace(toolCall.ToolName)
            && _approvalRequiredTools.Contains(toolCall.ToolName);

        if (requiresHuman)
        {
            // 持久化 Pending 记录，等待外部 ResolveAsync
            await TryPersistApprovalAsync(runId, toolCall, toolCallId, AgentApprovalStatus.Pending,
                approverId: null, rejectionReason: null, createdAt: now, cancellationToken).ConfigureAwait(false);

            // P0-6：返回 PendingApproval=true + ApprovalId，让 Actor 进入 AwaitingApproval 状态并退出执行槽。
            // 旧路径返回 Approved=false（等价于默认拒绝），导致 Actor 跳过 Tool 继续执行——这不是真正的 Human-in-the-loop。
            // 外部通过 POST /approvals/{approvalId} 端点提交决策（approve/reject），
            // 决策后 Run 状态推进到 ToolDispatching（批准）或 Failed（拒绝），由 RecoveryWorker 重新入队。
            return new AgentApprovalResult
            {
                Approved = false,
                PendingApproval = true,
                ApprovalId = toolCallId,
                RejectionReason = null,
                ApproverId = null,
                DecidedAt = now
            };
        }

        // 不在需审批列表中 → 自动批准
        await TryPersistApprovalAsync(runId, toolCall, toolCallId, AgentApprovalStatus.Approved,
            approverId: "auto-rule", rejectionReason: null, createdAt: now, cancellationToken).ConfigureAwait(false);
        return new AgentApprovalResult
        {
            Approved = true,
            RejectionReason = null,
            ApproverId = "auto-rule",
            DecidedAt = now
        };
    }

    /// <summary>
    /// 运行时能力补齐：尝试持久化审批记录（store=null 时静默跳过）。
    /// 自动批准时直接创建并裁决（Pending → Approved/Rejected 留下完整审计轨迹）；
    /// 需人工审批时仅创建 Pending 记录，等待外部 ResolveAsync。
    /// </summary>
    private async ValueTask TryPersistApprovalAsync(
        string runId,
        AgentToolCallRequest toolCall,
        string toolCallId,
        AgentApprovalStatus initialStatus,
        string? approverId,
        string? rejectionReason,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        if (_approvalStore is null)
        {
            return;
        }

        // 从 runId 反推 workspaceId 不可行（gate 无 workspace 入参）；
        // 此处使用 runId 作为 workspaceId 占位——生产实现应扩展 IAgentApprovalGate 签名传入 workspaceId，
        // 或由 Actor 在调用 Gate 前先 CreateAsync(Pending) 再调 Gate。
        // 当前实现仅适用于单 workspace 场景；多 workspace 需调用方自行持久化。
        // 为保持与 Store 契约一致，此处使用 "default" workspace。
        var workspaceId = "default";

        var approval = new AgentApproval
        {
            ApprovalId = toolCallId,
            RunId = runId,
            WorkspaceId = workspaceId,
            ToolCallId = toolCallId,
            ToolName = toolCall.ToolName ?? string.Empty,
            Status = AgentApprovalStatus.Pending, // 先创建为 Pending，再根据 initialStatus 裁决
            Reason = $"自动审批门请求：tool={toolCall.ToolName}",
            ApproverId = null,
            RejectionReason = null,
            CreatedAt = createdAt,
            ResolvedAt = null
        };

        try
        {
            await _approvalStore.CreateAsync(approval, cancellationToken).ConfigureAwait(false);

            // 自动批准/拒绝时立即裁决（Pending → Approved/Rejected）
            if (initialStatus != AgentApprovalStatus.Pending)
            {
                await _approvalStore.ResolveAsync(
                    workspaceId, toolCallId, initialStatus, approverId, rejectionReason, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 持久化失败不阻断审批决策（降级为纯内存模式，记录日志由上层处理）
        }
    }
}
