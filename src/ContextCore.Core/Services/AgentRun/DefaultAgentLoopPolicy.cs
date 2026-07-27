using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 任务 E3：DefaultAgentLoopPolicy — Agent 循环默认策略
//
// 实现 IAgentLoopPolicy 的默认决策逻辑：
//   1. 首轮（lastModelResponse=null）→ CallModel（启动模型调用）
//   2. 模型返回 IsFinalAnswer=true → Complete（产出最终答案，循环终止）
//   3. 模型返回 ToolCalls 非空 → DispatchTool（分派工具）
//   4. 模型返回无 ToolCalls 且非最终答案 → CallModel（再试一次）
//   5. TurnBudget.IsExhausted → Complete（强制终止，避免无限循环）
//   6. CostBudget.IsTokenBudgetExhausted → Fail（成本超限，标记失败）
//   7. 子问题 2：ModelCallsUsed >= MaxModelCalls → Fail（防止无 Tool 的模型循环无限运行）
//
// 设计决策：
//   - 预算校验优先于业务决策（避免超额消耗）；
//   - 首轮强制 CallModel（无模型响应时不能分派工具）；
//   - 完全无 ToolCalls 且非最终答案时选择重试，避免误判 Complete；
//   - 策略本身不修改 Run 状态（由 AgentRunActor 通过 TransitionStateAsync 推进）。
//   - 子问题 2：模型调用预算校验优先于业务决策（防止无限循环消耗 token）。
// ===========================================================================

/// <summary>
/// 任务 E3：Agent 循环默认策略实现。
/// </summary>
public sealed class DefaultAgentLoopPolicy : IAgentLoopPolicy
{
    /// <inheritdoc />
    public ValueTask<AgentLoopDecision> DecideAsync(
        AgentRun run,
        AgentModelResponse? lastModelResponse,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // 1. CostBudget 校验优先（成本超限 → Fail）
        if (run.CostBudget is { IsTokenBudgetExhausted: true })
        {
            return ValueTask.FromResult(AgentLoopDecision.Fail);
        }

        // 1b. 子问题 2：CostBudget 费用超限 → Fail
        if (run.CostBudget is { IsCostBudgetExhausted: true })
        {
            return ValueTask.FromResult(AgentLoopDecision.Fail);
        }

        // 1c. 子问题 2：ModelCallsUsed >= MaxModelCalls → Fail（防止无 Tool 的模型循环无限运行）
        // MaxModelCalls=0 表示未配置上限，不强制终止
        if (run.TurnBudget is { MaxModelCalls: > 0 } tb
            && run.ModelCallsUsed >= tb.MaxModelCalls)
        {
            return ValueTask.FromResult(AgentLoopDecision.Fail);
        }

        // 2. TurnBudget 耗尽 → Complete（强制终止，避免无限循环）
        if (run.TurnBudget is { IsExhausted: true })
        {
            return ValueTask.FromResult(AgentLoopDecision.Complete);
        }

        // 3. 首轮（无模型响应）→ CallModel
        if (lastModelResponse is null)
        {
            return ValueTask.FromResult(AgentLoopDecision.CallModel);
        }

        // 4. 模型返回最终答案 → Complete
        if (lastModelResponse.IsFinalAnswer)
        {
            return ValueTask.FromResult(AgentLoopDecision.Complete);
        }

        // 5. 模型返回 ToolCalls 非空 → DispatchTool
        if (lastModelResponse.ToolCalls.Count > 0)
        {
            return ValueTask.FromResult(AgentLoopDecision.DispatchTool);
        }

        // 6. 无 ToolCalls 且非最终答案 → CallModel（再试一次）
        return ValueTask.FromResult(AgentLoopDecision.CallModel);
    }
}
