using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 任务 E1：AgentRunStateMachine — Agent Run 状态机校验器
//
// 校验 AgentRunState 的合法状态流转（参考 AgentRunState 注释中的合法流转图）。
// 状态机复用 ToolDispatchState 的 expected-state CAS + 不可逆前向推进模式：
//   - 任意非终态可单向流转到下一阶段；
//   - 任意状态可流转到 Failed（异常）/ Cancelled（用户取消）；
//   - 终态（Completed / Failed / Cancelled）不可再流转；
//   - 非法流转抛 InvalidOperationException。
// ===========================================================================

/// <summary>
/// 任务 E1：Agent Run 状态机校验器（静态方法）。
/// </summary>
/// <remarks>
/// 合法流转图（与 <see cref="AgentRunState"/> 注释一致）：
/// <code>
/// Created → ContextBuilding → ModelCalling → AwaitingApproval
///    ↓           ↓                ↓               ↓
///    └───────────┴────────────────┴───────────────┘
///                        ↓
///               ToolDispatching → Observing → Checkpointing
///                        ↓            ↓            ↓
///                        └────────────┴────────────┘
///                                     ↓
///                          ┌────── Completed
///                          ├────── Failed
///                          └──── Cancelled (仅由外部取消触发)
/// </code>
/// 任意状态可跳转到 Failed（异常）或 Cancelled（用户取消）。
/// Checkpointing 后回到 ContextBuilding 开启下一轮循环。
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

        // 任意状态可跳转到 Failed / Cancelled（终态短路）
        if (to == AgentRunState.Failed || to == AgentRunState.Cancelled)
        {
            // 已终态再跳 Failed/Cancelled 仍允许（幂等收尾），但不会改变事实
            return;
        }

        // 终态不可再流转到非终态
        if (IsTerminalState(from))
        {
            throw new InvalidOperationException(
                $"Agent Run 状态机非法转换：终态 {from} 不可流转到 {to}。" +
                $"终态（Completed/Failed/Cancelled）不可再推进。");
        }

        if (!IsValidForwardTransition(from, to))
        {
            throw new InvalidOperationException(
                $"Agent Run 状态机非法转换：{from} → {to} 不在合法流转图中。" +
                $"合法流转：Created → ContextBuilding → ModelCalling → AwaitingApproval → " +
                $"ToolDispatching → Observing → Checkpointing → ContextBuilding（下一轮）/ Completed；" +
                $"任意状态可跳转到 Failed/Cancelled。");
        }
    }

    /// <summary>
    /// 判断指定状态是否为终态（Completed / Failed / Cancelled）。
    /// </summary>
    /// <param name="state">待判断的状态。</param>
    /// <returns>终态返回 true；非终态返回 false。</returns>
    public static bool IsTerminalState(AgentRunState state)
        => state == AgentRunState.Completed
           || state == AgentRunState.Failed
           || state == AgentRunState.Cancelled;

    /// <summary>
    /// 判断 from → to 是否为合法前向推进（不含 Failed/Cancelled 短路；调用方已先短路）。
    /// </summary>
    private static bool IsValidForwardTransition(AgentRunState from, AgentRunState to)
        => from switch
        {
            // Created → ContextBuilding（启动）
            AgentRunState.Created => to == AgentRunState.ContextBuilding,

            // ContextBuilding → ModelCalling（开始调用模型）
            AgentRunState.ContextBuilding => to == AgentRunState.ModelCalling,

            // ModelCalling → AwaitingApproval（高风险需审批）/ ToolDispatching（直接分派）/ Completed（最终答案）/ ContextBuilding（重试）
            AgentRunState.ModelCalling => to == AgentRunState.AwaitingApproval
                                          || to == AgentRunState.ToolDispatching
                                          || to == AgentRunState.Completed
                                          || to == AgentRunState.ContextBuilding,

            // AwaitingApproval → ToolDispatching（批准后继续分派）/ ContextBuilding（拒绝后回到上下文构建重试）/ Completed（拒绝且无法继续则完成）
            AgentRunState.AwaitingApproval => to == AgentRunState.ToolDispatching
                                              || to == AgentRunState.ContextBuilding
                                              || to == AgentRunState.Completed,

            // ToolDispatching → Observing（观察结果）
            AgentRunState.ToolDispatching => to == AgentRunState.Observing,

            // Observing → Checkpointing（保存检查点）/ ContextBuilding（直接进入下一轮，跳过 checkpoint）/ Completed（无需继续则完成）
            AgentRunState.Observing => to == AgentRunState.Checkpointing
                                       || to == AgentRunState.ContextBuilding
                                       || to == AgentRunState.Completed,

            // Checkpointing → ContextBuilding（循环继续）/ Completed（保存后即完成）
            AgentRunState.Checkpointing => to == AgentRunState.ContextBuilding
                                            || to == AgentRunState.Completed,

            // 终态已在调用方短路；此处不应到达
            _ => false
        };
}
