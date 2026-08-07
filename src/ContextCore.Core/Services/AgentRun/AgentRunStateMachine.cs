using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// AgentRunStateMachine — Agent Run 状态机校验器
// 
// 校验 AgentRunState 的合法状态流转（参考 AgentRunState 注释中的合法流转图）。
// 状态机复用 ToolDispatchState 的 expected-state CAS + 不可逆前向推进模式：
// - 任意非终态可单向流转到下一阶段；
// - 任意状态可流转到 Failed（异常）/ Cancelled（用户取消）；
// - 终态（Completed / Failed / Cancelled）不可再流转；
// - 非法流转抛 InvalidOperationException。
// ===========================================================================

/// <summary>
/// Agent Run 状态机校验器（静态方法）。
/// </summary>
/// <remarks>
/// 合法流转图（与 <see cref="AgentRunState"/> 注释一致）：
/// <code>
/// PendingAdmission → Queued → Claimed → Running → ContextBuilding → ModelCalling → AwaitingApproval
///     │                     │  └(释放)──────┘   ↑            ↓ ↓ ↓ ↓
///     │                     │  └→ ClaimExpired ┘            └───────────┴────────────────┴───────────────┘
///     └→ AdmissionRejected（终态）                            ↓
///                                                             ToolDispatching → AwaitingApproval（需审批时挂起）
///                                                             ↓ ↓
///                                                             └────────────┘
///                                                             ↓
///                                                             Observing → Checkpointing
///                                                             ↓ ↓ ↓
///                                                             └────────────┴────────────┘
///                                                             ↓
///                                                             ┌────── Completed
///                                                             ├────── Failed
///                                                             └──── Cancelled (仅由外部取消触发)
/// </code>
/// 任意状态可跳转到 Failed（异常）或 Cancelled（用户取消）。
/// Claimed → ClaimExpired（claim 过期）→ Claimed（其他节点重领）构成调度领取闭环。
/// Checkpointing 后回到 ContextBuilding 开启下一轮循环。
/// ToolDispatching → AwaitingApproval 允许 Tool 分派中需审批时挂起，
/// 等待外部 POST /approvals/{approvalId} 决策后回到 ToolDispatching 继续。
/// </remarks>
public static class AgentRunStateMachine
{
    /// <summary>
    /// 校验状态转换是否合法；非法转换抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <param name="from">当前状态。</param>
    /// <param name="to">目标状态。</param>
    /// <exception cref="InvalidOperationException">当 <paramref name="from"/> → <paramref name="to"/> 不在合法流转图中。</exception>
    public static void ValidateTransition(AgentRunState from, AgentRunState to)
    {
        if (from == to)
        {
            // 同状态停留不算流转，允许（如重入 ContextBuilding）
            return;
        }

        // 任意状态可跳转到 Failed / Cancelled（终态短路，幂等收尾）
        // LeaseLost 表示丢租（区别于用户主动 Cancelled），但 Completed/Cancelled
        // 已是确定终态，不可被旧 owner 的丢租写入覆盖（旧 owner 无 fencing token）。
        if (to == AgentRunState.Failed || to == AgentRunState.Cancelled)
        {
            // 已终态再跳 Failed/Cancelled 仍允许（幂等收尾），但不会改变事实
            return;
        }

        if (to == AgentRunState.LeaseLost)
        {
            // LeaseLost 仅可由新 owner/recovery worker 写入，且源状态不得为 Completed/Cancelled/ReconciliationRejected/AdmissionRejected。
            // Completed/Cancelled 是确定终态，不应被丢租覆盖；ReconciliationRejected 是裁决终态，也不应被丢租覆盖；
            // AdmissionRejected 是准入拒绝终态，同样不应被丢租覆盖。
            if (from == AgentRunState.Completed || from == AgentRunState.Cancelled
                || from == AgentRunState.ReconciliationRejected || from == AgentRunState.AdmissionRejected)
            {
                throw new InvalidOperationException(
                    $"Agent Run 状态机非法转换：{from} 不可流转到 {to}。" +
                    $"Completed/Cancelled/ReconciliationRejected/AdmissionRejected 已是确定终态，不应被标记为 LeaseLost。");
            }
            return;
        }

        // 终态不可再流转到非终态
        if (IsTerminalState(from))
        {
            throw new InvalidOperationException(
                $"Agent Run 状态机非法转换：终态 {from} 不可流转到 {to}。" +
                $"终态（Completed/Failed/Cancelled/LeaseLost/ReconciliationRejected/RecoveryBlocked/RecoveryCorrupted/ContextSafetyBlocked/DeadLettered/AdmissionRejected）不可再推进。");
        }

        // 恢复失败状态（fail-closed）：任意非终态可跳入。
        // RecoveryBlocked / RecoveryCorrupted 为终态（数据损坏，等待运维介入，不自动重试）；
        // RecoveryDependencyUnavailable 为可重试状态（依赖恢复后由恢复 Worker 在退避门通过后
        // 重新入队执行），其 → ContextBuilding 的合法流转在 IsValidForwardTransition 中声明。
        // ContextSafetyBlocked 为终态（安全阻断，需人工介入），任意非终态可跳入。
        if (to == AgentRunState.RecoveryBlocked
            || to == AgentRunState.RecoveryCorrupted
            || to == AgentRunState.RecoveryDependencyUnavailable
            || to == AgentRunState.ContextSafetyBlocked)
        {
            return;
        }

        if (!IsValidForwardTransition(from, to))
        {
            throw new InvalidOperationException(
                $"Agent Run 状态机非法转换：{from} → {to} 不在合法流转图中。" +
                $"合法流转：PendingAdmission → Queued → Claimed → Running → ContextBuilding → ModelCalling → " +
                $"AwaitingApproval → ToolDispatching → Observing → Checkpointing → ContextBuilding（下一轮）/ Completed；" +
                $"任意状态可跳转到 Failed/Cancelled；LeaseLost 仅可由非 Completed/Cancelled 状态跳入（P0-5）。");
        }
    }

    /// <summary>
    /// 判断指定状态是否为终态（Completed / Failed / Cancelled / LeaseLost / ReconciliationRejected /
    /// RecoveryBlocked / RecoveryCorrupted / ContextSafetyBlocked / DeadLettered / AdmissionRejected）。
    /// </summary>
    /// <remarks>
    /// <see cref="AgentRunState.RecoveryDependencyUnavailable"/> 不是终态：它表示恢复依赖（事件存储）
    /// 暂时不可用，fail-closed 下不得回退为全新启动，但依赖恢复后由恢复 Worker 在退避门
    /// （<c>NextRetryAtUtc</c>）通过后重新入队执行（退避重试）。
    /// <see cref="AgentRunState.AdmissionRejected"/> 是终态（Admission 边界）：
    /// 配额预留失败的 Run 永不进入调度队列，保留行仅作审计。
    /// <see cref="AgentRunState.ContextSafetyBlocked"/> 是终态：mandatory 上下文安全阻断，
    /// 模型未运行，等待人工介入（不自动重试）。
    /// </remarks>
    /// <param name="state">待判断的状态。</param>
    /// <returns>终态返回 true；非终态返回 false。</returns>
    public static bool IsTerminalState(AgentRunState state)
        => state == AgentRunState.Completed
           || state == AgentRunState.Failed
           || state == AgentRunState.Cancelled
           || state == AgentRunState.LeaseLost
           || state == AgentRunState.ReconciliationRejected
           || state == AgentRunState.RecoveryBlocked
           || state == AgentRunState.RecoveryCorrupted
           || state == AgentRunState.ContextSafetyBlocked
           || state == AgentRunState.DeadLettered
           || state == AgentRunState.AdmissionRejected;

    /// <summary>
    /// 判断指定状态是否为恢复失败状态（RecoveryBlocked / RecoveryCorrupted / RecoveryDependencyUnavailable）。
    /// </summary>
    /// <remarks>
    /// Actor 主循环据此退出执行槽：进入恢复失败状态后不得继续执行任何 Agent 逻辑
    /// （RecoveryDependencyUnavailable 虽非终态，但 fail-closed 下同样不得继续执行，
    /// 需等待恢复 Worker 在退避门通过后重新入队，而非在本次执行槽内继续推进）。
    /// </remarks>
    /// <param name="state">待判断的状态。</param>
    /// <returns>恢复失败状态返回 true；否则返回 false。</returns>
    public static bool IsRecoveryFailureState(AgentRunState state)
        => state == AgentRunState.RecoveryBlocked
           || state == AgentRunState.RecoveryCorrupted
           || state == AgentRunState.RecoveryDependencyUnavailable;

    /// <summary>
    /// 判断 from → to 是否为合法前向推进（不含 Failed/Cancelled 短路；调用方已先短路）。
    /// </summary>
    private static bool IsValidForwardTransition(AgentRunState from, AgentRunState to)
        => from switch
        {
            // Created → ContextBuilding（启动；InMemory / FileSystem provider 路径）
            AgentRunState.Created => to == AgentRunState.ContextBuilding,

            // Admission：PendingAdmission → Queued（配额预留成功）/ AdmissionRejected（配额失败，终态）。
            // 两者均不进入 Claimer 候选集；AdmissionRejected 不可再推进（终态已短路）。
            AgentRunState.PendingAdmission => to == AgentRunState.Queued
                                               || to == AgentRunState.AdmissionRejected,

            // Scheduler Claim：Queued → Claimed（领取 Scheduler Claim Lease）。
            // 入队失败释放后回到 Queued，其他节点可再次领取。
            // / ContextBuilding（防御：Queued 直接到达 Actor 时按全新启动处理——执行前状态）。
            AgentRunState.Queued => to == AgentRunState.Claimed
                                    || to == AgentRunState.ContextBuilding,

            // Claimed → Running（Execution/Fencing Lease 已获取，执行权确立）
            // / ContextBuilding（Actor 首次 flush：领取后直接开始执行，跳过显式 Running 推进的兼容路径）。
            // / ClaimExpired（claim 租约过期未续约 → 显式标记失效，等待其他节点接管）。
            // / ScheduledLocally（本地入队成功即消费 Claim，排队期间不依赖 Claim 续租）。
            AgentRunState.Claimed => to == AgentRunState.Running
                                      || to == AgentRunState.ContextBuilding
                                      || to == AgentRunState.ClaimExpired
                                      || to == AgentRunState.ScheduledLocally,

            // ScheduledLocally（本地已调度，Claim 已消费）：
            // → Running（出队后取得执行租约，执行权确立）
            // / ContextBuilding（Actor 首次 flush 兼容路径：直接开始执行）
            // / Queued（节点崩溃后由 Recovery Worker 回退重新调度）。
            AgentRunState.ScheduledLocally => to == AgentRunState.Running
                                              || to == AgentRunState.ContextBuilding
                                              || to == AgentRunState.Queued,

            // Claim 过期（ClaimExpired）→ Claimed：其他节点重新领取（claim_attempt +1）。
            AgentRunState.ClaimExpired => to == AgentRunState.Claimed,

            // Running → ContextBuilding（Actor 首次 flush：全新启动，从构建上下文开始）。
            AgentRunState.Running => to == AgentRunState.ContextBuilding,

            // ContextBuilding → ModelCalling（开始调用模型）
            // / AwaitingReconciliation（轮次结束且存在未裁决高风险 Tool，暂停等待对账）
            AgentRunState.ContextBuilding => to == AgentRunState.ModelCalling
                                              || to == AgentRunState.AwaitingReconciliation,

            // ModelCalling → AwaitingApproval（高风险需审批）/ ToolDispatching（直接分派）/ Completed（最终答案）/ ContextBuilding（重试）
            // / AwaitingReconciliation（模型产出最终答案但存在未裁决高风险 Tool，暂停等待对账）
            AgentRunState.ModelCalling => to == AgentRunState.AwaitingApproval
                                          || to == AgentRunState.ToolDispatching
                                          || to == AgentRunState.Completed
                                          || to == AgentRunState.ContextBuilding
                                          || to == AgentRunState.AwaitingReconciliation,

            // AwaitingApproval → PendingToolExecution（审批通过后直接执行原 Tool，不重新调用模型）
            // / ToolDispatching（旧路径兼容：批准后继续分派）
            // / ContextBuilding（拒绝后回到上下文构建重试）/ Completed（拒绝且无法继续则完成）
            AgentRunState.AwaitingApproval => to == AgentRunState.PendingToolExecution
                                              || to == AgentRunState.ToolDispatching
                                              || to == AgentRunState.ContextBuilding
                                              || to == AgentRunState.Completed,

            // PendingToolExecution → Observing（原 Tool 执行完成后观察结果，继续 Observation→Model 循环）
            // / Failed（执行异常）/ Cancelled（外部取消，由短路处理）
            AgentRunState.PendingToolExecution => to == AgentRunState.Observing,

            // ToolDispatching → AwaitingApproval（Tool 分派中需审批时挂起等待人工裁决）/ Observing（观察结果）
            // / AwaitingReconciliation（分派后存在未裁决高风险 Tool，等待对账）
            AgentRunState.ToolDispatching => to == AgentRunState.AwaitingApproval
                                              || to == AgentRunState.Observing
                                              || to == AgentRunState.AwaitingReconciliation,

            // Observing → Checkpointing（保存检查点）/ ContextBuilding（直接进入下一轮，跳过 checkpoint）/ Completed（无需继续则完成）
            // / AwaitingReconciliation（观察后存在未裁决高风险 Tool，等待对账）
            AgentRunState.Observing => to == AgentRunState.Checkpointing
                                       || to == AgentRunState.ContextBuilding
                                       || to == AgentRunState.Completed
                                       || to == AgentRunState.AwaitingReconciliation,

            // Checkpointing → ContextBuilding（循环继续）/ Completed（保存后即完成）
            AgentRunState.Checkpointing => to == AgentRunState.ContextBuilding
                                            || to == AgentRunState.Completed,

            // AwaitingReconciliation → ReconciliationRunning（Worker 接管对账）
            // / ContextBuilding（全部裁决完成，恢复执行）/ ReconciliationRejected（裁决被拒绝）
            AgentRunState.AwaitingReconciliation => to == AgentRunState.ReconciliationRunning
                                                    || to == AgentRunState.ContextBuilding
                                                    || to == AgentRunState.ReconciliationRejected,

            // ReconciliationRunning → AwaitingReconciliation（对账仍在进行/重试）
            // / ContextBuilding（对账完成，Actor 恢复执行规范化）
            // / ReconciliationRejected（裁决被拒绝）
            AgentRunState.ReconciliationRunning => to == AgentRunState.AwaitingReconciliation
                                                   || to == AgentRunState.ContextBuilding
                                                   || to == AgentRunState.ReconciliationRejected,

            // ReconciliationRejected 已是终态（仅可跳转 Failed/Cancelled，由调用方短路处理）
            AgentRunState.ReconciliationRejected => false,

            // RecoveryDependencyUnavailable → ContextBuilding（退避重试）：
            // 恢复依赖（事件存储）恢复后，由恢复 Worker 在退避门（NextRetryAtUtc）通过后
            // 重新入队执行；Actor 恢复路径将本地状态规范化为 ContextBuilding。
            // RecoveryBlocked / RecoveryCorrupted 为终态（数据损坏，等待运维介入），不在此声明。
            AgentRunState.RecoveryDependencyUnavailable => to == AgentRunState.ContextBuilding,

            // 终态已在调用方短路；此处不应到达
            _ => false
        };
}
