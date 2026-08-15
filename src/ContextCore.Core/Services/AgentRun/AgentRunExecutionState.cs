using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

/// <summary>
/// Agent Run 执行期状态（不可变记录，所有阶段方法返回新状态）。
/// 统一管理 Run 元数据 / 结构化上下文 / 模型响应 / checkpoint / 事件序列与哈希链。
/// </summary>
internal sealed record AgentRunExecutionState
{
    /// <summary>当前 Run 元数据（本地副本，含 State/Turn/ModelCallsUsed/预算 等）。</summary>
    public required AgentRun Run { get; init; }

    /// <summary>
    /// 结构化 Agent 上下文状态（替代旧 List&lt;AgentMessage&gt; Messages）。
    /// 包含 SystemPrompt / Constraints / CurrentTask / 短期工作集 / Tool Observations /
    /// Stable Memory References / LastModelTurn；由 ProjectForModel 根据 TokenBudget 投影。
    /// </summary>
    public required AgentContextState Context { get; init; }

    /// <summary>最近一次模型响应（null = 首轮，尚未调用模型；与 Context.LastModelTurn 同步，保留供 IAgentLoopPolicy 使用）。</summary>
    public AgentModelResponse? LastModelResponse { get; init; }

    /// <summary>
    /// 最近一次模型响应的规范化 Tool 调用列表（与 <see cref="LastModelResponse"/> 同步生成）。
    /// null = 尚未调用模型或模型响应已分派完毕（DispatchToolsAsync 结束后置 null）。
    /// 非空时，<see cref="DispatchToolsAsync"/> 按 ordinal 索引取出 <see cref="NormalizedToolCall.InvocationId"/>
    /// 作为统一的 ToolCallId，确保 Assistant 消息 / 事件 / Journal / Tool Message 引用同一 ID。
    /// </summary>
    public List<NormalizedToolCall>? NormalizedToolCalls { get; init; }

    /// <summary>
    /// 最近一次 Context Decision Runtime 的执行结果（含 WorkingSet.Materials）。
    /// null = 未注入决策运行时或本轮未调用。
    /// 由 IAgentModelContextProjector 在投影时从 Materials 取出候选正文。
    /// </summary>
    public ContextDecisionExecutionResult? LastDecisionResult { get; init; }

    /// <summary>最近一次 checkpoint（null = 尚未创建 checkpoint）。</summary>
    public AgentCheckpoint? LastCheckpoint { get; init; }

    /// <summary>事件序列号（单调递增，从 0 开始）。</summary>
    public int EventSequence { get; init; }

    /// <summary>最近一个事件的 ContentHash（哈希链；链头为 null）。</summary>
    public string? EventChainHash { get; init; }

    /// <summary>
    /// 待执行的 Tool 命令列表（审批恢复用）。
    /// 当 Run 从 AwaitingApproval 恢复（审批通过 → PendingToolExecution）时，
    /// 从 ApprovalRequested 事件 payload 重建此字段，Actor 据此依次执行所有 Pending 命令。
    /// 列表首项为被审批的 Tool；后续项为审批中断时未处理的同轮 Tool Call（旧路径单数时会丢弃）。
    /// 非 PendingToolExecution 状态时为 null。
    /// </summary>
    public List<PendingToolCommand>? PendingToolCommands { get; init; }
}
