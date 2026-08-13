using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// Agent 循环默认策略：预算先于业务；首轮必须调模型；无工具且非最终答案则再试。
// 策略本身不改 Run 状态，由 Actor 做 CAS 推进。

/// <summary>
/// Agent 循环默认策略实现。
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

        // CostBudget 费用超限 → Fail
        if (run.CostBudget is { IsCostBudgetExhausted: true })
        {
            return ValueTask.FromResult(AgentLoopDecision.Fail);
        }

        // ModelCallsUsed >= MaxModelCalls → Fail
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
