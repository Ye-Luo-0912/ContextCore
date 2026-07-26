namespace ContextCore.Abstractions;

// ===========================================================================
// Agent Run 契约层 — 模型驱动的 Agent 执行循环
//
// 目标（补齐四、Agent 下一步核心功能）：
//   1. 定义 Agent Run 状态机：Created → ContextBuilding → ModelCalling →
//      AwaitingApproval → ToolDispatching → Observing → Checkpointing →
//      Completed / Failed / Cancelled。
//   2. 定义 6 个核心接口：
//      - IAgentModelTransport：模型流式调用抽象（替代直接 IContextDecisionRuntime）
//      - IAgentLoopPolicy：循环策略（决定下一步：CallModel / DispatchTool / Checkpoint / Complete）
//      - IAgentRunStore：Agent Run 元数据持久化（三层模式：基础 + IPersistent 标记）
//      - IAgentApprovalGate：人工/自动审批门（高风险操作需审批后执行）
//      - IAgentToolCallValidator：Tool 调用校验器（参数合法性 + 安全检查）
//      - IAgentRunEventStore：Run 事件流持久化（复用 Checkpoint 哈希链模式）
//   3. 定义数据契约：AgentRun / AgentRunEvent / AgentModelResponse /
//      AgentToolCallRequest / AgentLoopDecision / AgentTurnBudget / AgentCostBudget。
//
// 设计原则：
//   1. 状态机复用 ToolDispatchState 的 expected-state CAS + 不可逆前向推进模式。
//   2. 事件流复用 Checkpoint 哈希链（ContentHash / PrevChainHash）防篡改。
//   3. 契约层不引入存储 I/O；持久化由 IPersistent 标记接口区分。
//   4. AgentKernelHost / AgentRunActor 消费这些契约实现多 Session 隔离。
// ===========================================================================

// ── 状态机 ─────────────────────────────────────────────────────────────────

/// <summary>
/// Agent Run 状态机。
/// </summary>
/// <remarks>
/// 合法状态流转（不可逆前向推进）：
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
public enum AgentRunState : byte
{
    /// <summary>已创建，尚未开始执行。</summary>
    Created = 0,

    /// <summary>构建上下文（调用 IContextDecisionRuntime / BuildContext）。</summary>
    ContextBuilding = 1,

    /// <summary>调用模型（通过 IAgentModelTransport）。</summary>
    ModelCalling = 2,

    /// <summary>等待审批（IAgentApprovalGate 审批高风险操作）。</summary>
    AwaitingApproval = 3,

    /// <summary>分派 Tool（IToolDispatcher + IAgentToolCallValidator）。</summary>
    ToolDispatching = 4,

    /// <summary>观察 Tool 结果并追加到上下文。</summary>
    Observing = 5,

    /// <summary>保存检查点（IAgentCheckpointFactory）。</summary>
    Checkpointing = 6,

    /// <summary>正常完成（产出最终答案）。</summary>
    Completed = 7,

    /// <summary>失败（异常或审批拒绝导致无法继续）。</summary>
    Failed = 8,

    /// <summary>已取消（用户或超时触发）。</summary>
    Cancelled = 9
}

/// <summary>
/// Agent 循环策略决策（IAgentLoopPolicy 返回值）。
/// </summary>
public enum AgentLoopDecision : byte
{
    /// <summary>继续调用模型（进入 ModelCalling）。</summary>
    CallModel = 0,

    /// <summary>分派 Tool（进入 ToolDispatching）。</summary>
    DispatchTool = 1,

    /// <summary>保存检查点（进入 Checkpointing）。</summary>
    Checkpoint = 2,

    /// <summary>完成 Run（进入 Completed，产出最终答案）。</summary>
    Complete = 3,

    /// <summary>失败终止（进入 Failed）。</summary>
    Fail = 4
}

// ── 数据契约 ───────────────────────────────────────────────────────────────

/// <summary>
/// Agent Run 元数据。描述一次 Agent 执行的完整生命周期信息。
/// </summary>
public sealed record AgentRun
{
    /// <summary>Run 唯一 ID（ULID/GUID）。</summary>
    public required string RunId { get; init; }

    /// <summary>Workspace ID（隔离边界）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Session ID（同一 session 的多个 run 共享上下文）。</summary>
    public required string SessionId { get; init; }

    /// <summary>用户输入/任务描述。</summary>
    public required string Task { get; init; }

    /// <summary>当前状态。</summary>
    public required AgentRunState State { get; init; }

    /// <summary>当前循环轮次（每轮 = ContextBuilding→ModelCalling→ToolDispatching→Observing）。</summary>
    public required int Turn { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后更新时间（UTC）。</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>结束时间（Completed/Failed/Cancelled 时设置；运行中为 null）。</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>失败原因（State=Failed 时设置）。</summary>
    public string? FailureReason { get; init; }

    /// <summary>最终答案（State=Completed 时设置）。</summary>
    public string? FinalAnswer { get; init; }

    /// <summary>Turn 预算限制。</summary>
    public AgentTurnBudget? TurnBudget { get; init; }

    /// <summary>Cost 预算限制。</summary>
    public AgentCostBudget? CostBudget { get; init; }
}

/// <summary>
/// Agent Run 事件（不可变审计记录，复用 Checkpoint 哈希链模式防篡改）。
/// </summary>
/// <remarks>
/// 哈希链：
///   - ContentHash = SHA-256(序列化 payload，ContentHash=null)
///   - PrevChainHash = 前一个事件的 ContentHash（链头为 null）
///   - Sequence = 单调递增序列号（从 0 开始）
/// 校验：读取时重算 ContentHash 比对；PrevChainHash 与前一事件 ContentHash 比对。
/// </remarks>
public sealed record AgentRunEvent
{
    /// <summary>事件唯一 ID（ULID）。</summary>
    public required string EventId { get; init; }

    /// <summary>所属 Run ID。</summary>
    public required string RunId { get; init; }

    /// <summary>Workspace ID（隔离边界）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>事件序列号（同一 Run 内单调递增，从 0 开始）。</summary>
    public required int Sequence { get; init; }

    /// <summary>事件类型。</summary>
    public required AgentRunEventType EventType { get; init; }

    /// <summary>事件发生时的 Run 状态快照。</summary>
    public required AgentRunState State { get; init; }

    /// <summary>事件负载（JSON 序列化的事件细节）。</summary>
    public required string Payload { get; init; }

    /// <summary>事件内容哈希（SHA-256；计算时本字段设为 null）。</summary>
    public string? ContentHash { get; init; }

    /// <summary>前一个事件的 ContentHash（哈希链；链头为 null）。</summary>
    public string? PrevChainHash { get; init; }

    /// <summary>事件时间戳（UTC）。</summary>
    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Agent Run 事件类型。
/// </summary>
public enum AgentRunEventType : byte
{
    /// <summary>Run 创建。</summary>
    RunCreated = 0,

    /// <summary>状态转换。</summary>
    StateTransition = 1,

    /// <summary>模型调用请求。</summary>
    ModelCallStarted = 2,

    /// <summary>模型调用完成。</summary>
    ModelCallCompleted = 3,

    /// <summary>审批请求发出。</summary>
    ApprovalRequested = 4,

    /// <summary>审批结果返回。</summary>
    ApprovalResolved = 5,

    /// <summary>Tool 调用开始。</summary>
    ToolCallStarted = 6,

    /// <summary>Tool 调用完成。</summary>
    ToolCallCompleted = 7,

    /// <summary>观察结果追加。</summary>
    ObservationAppended = 8,

    /// <summary>检查点保存。</summary>
    CheckpointSaved = 9,

    /// <summary>Run 完成。</summary>
    RunCompleted = 10,

    /// <summary>Run 失败。</summary>
    RunFailed = 11,

    /// <summary>Run 取消。</summary>
    RunCancelled = 12
}

/// <summary>
/// 模型调用响应（IAgentModelTransport 返回值）。
/// </summary>
public sealed record AgentModelResponse
{
    /// <summary>模型输出文本（最终答案或中间推理）。</summary>
    public required string Content { get; init; }

    /// <summary>模型请求的 Tool 调用列表（空 = 无 Tool 调用，可能直接产出最终答案）。</summary>
    public required IReadOnlyList<AgentToolCallRequest> ToolCalls { get; init; } = Array.Empty<AgentToolCallRequest>();

    /// <summary>是否为最终答案（true = 循环应终止）。</summary>
    public required bool IsFinalAnswer { get; init; }

    /// <summary>Token 消耗（用于 cost budget 校验）。</summary>
    public required int TokensConsumed { get; init; }

    /// <summary>推理耗时。</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>模型工件 ID（用于审计追踪）。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>原始模型输出（用于调试/审计）。</summary>
    public string? RawOutput { get; init; }
}

/// <summary>
/// 模型请求的 Tool 调用。
/// </summary>
public sealed record AgentToolCallRequest
{
    /// <summary>Tool 名称。</summary>
    public required string ToolName { get; init; }

    /// <summary>Tool 参数（JSON 字符串）。</summary>
    public required string Arguments { get; init; }

    /// <summary>调用方提供的幂等键（可选；用于外部系统去重）。</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// Tool 调用校验结果（IAgentToolCallValidator 返回值）。
/// </summary>
public sealed record AgentToolCallValidationResult
{
    /// <summary>是否通过校验。</summary>
    public required bool IsValid { get; init; }

    /// <summary>校验失败原因（IsValid=false 时设置）。</summary>
    public string? Error { get; init; }

    /// <summary>是否需要审批（高风险操作由 IAgentApprovalGate 二次确认）。</summary>
    public required bool RequiresApproval { get; init; }

    /// <summary>审批原因（RequiresApproval=true 时设置）。</summary>
    public string? ApprovalReason { get; init; }
}

/// <summary>
/// 审批结果（IAgentApprovalGate 返回值）。
/// </summary>
public sealed record AgentApprovalResult
{
    /// <summary>是否批准。</summary>
    public required bool Approved { get; init; }

    /// <summary>拒绝原因（Approved=false 时设置）。</summary>
    public string? RejectionReason { get; init; }

    /// <summary>审批者标识（人工审批时为用户 ID；自动审批时为规则 ID）。</summary>
    public string? ApproverId { get; init; }

    /// <summary>审批时间（UTC）。</summary>
    public required DateTimeOffset DecidedAt { get; init; }
}

/// <summary>
/// Turn 预算限制。
/// </summary>
public sealed record AgentTurnBudget
{
    /// <summary>最大循环轮次。</summary>
    public required int MaxTurns { get; init; }

    /// <summary>当前已用轮次。</summary>
    public required int TurnsUsed { get; init; }

    /// <summary>剩余轮次（MaxTurns - TurnsUsed）。</summary>
    public int Remaining => Math.Max(0, MaxTurns - TurnsUsed);

    /// <summary>是否已耗尽。</summary>
    public bool IsExhausted => TurnsUsed >= MaxTurns;
}

/// <summary>
/// Cost 预算限制。
/// </summary>
public sealed record AgentCostBudget
{
    /// <summary>最大 Token 消耗。</summary>
    public required int MaxTokens { get; init; }

    /// <summary>当前已消耗 Token。</summary>
    public required int TokensUsed { get; init; }

    /// <summary>最大推理费用（美元）。</summary>
    public required double MaxCostUsd { get; init; }

    /// <summary>当前已产生费用（美元）。</summary>
    public required double CostUsedUsd { get; init; }

    /// <summary>Token 预算是否已耗尽。</summary>
    public bool IsTokenBudgetExhausted => TokensUsed >= MaxTokens;

    /// <summary>费用预算是否已耗尽。</summary>
    public bool IsCostBudgetExhausted => CostUsedUsd >= MaxCostUsd;
}

// ── 6 个核心接口 ────────────────────────────────────────────────────────────

/// <summary>
/// 模型调用传输抽象。替代直接调用 IContextDecisionRuntime，
/// 支持 stream 输出与 Tool 调用解析。
/// </summary>
/// <remarks>
/// 与 IContextDecisionRuntime 的区别：
/// - IContextDecisionRuntime 面向检索/评分场景（输入特征 → 输出分数）。
/// - IAgentModelTransport 面向 Agent 对话场景（输入上下文 → 输出文本 + Tool 调用）。
/// 实现层可对接 ONNX（通过 ModelActivationManager）、远程 LLM 服务等。
/// </remarks>
public interface IAgentModelTransport
{
    /// <summary>
    /// 调用模型，获取响应（可能包含 Tool 调用或最终答案）。
    /// </summary>
    /// <param name="runId">Agent Run ID。</param>
    /// <param name="context">已构建的上下文（JSON 字符串，含历史对话 + Tool 结果）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>模型响应（含文本、Tool 调用、是否最终答案、Token 消耗）。</returns>
    ValueTask<AgentModelResponse> CallAsync(
        string runId,
        string context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent 循环策略抽象。决定每一步的下一步动作。
/// </summary>
/// <remarks>
/// 策略示例：
/// - 默认策略：Model → Tool → Model → Tool → ... → Complete
/// - 保守策略：Model → Tool → Checkpoint → Model → ...
/// - 激进策略：Model → Model → Model → Complete（跳过 Tool）
/// </remarks>
public interface IAgentLoopPolicy
{
    /// <summary>
    /// 根据当前 Run 状态与模型响应，决定下一步动作。
    /// </summary>
    /// <param name="run">当前 Run 状态。</param>
    /// <param name="lastModelResponse">最近一次模型响应（null = 首轮，尚未调用模型）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>循环决策。</returns>
    ValueTask<AgentLoopDecision> DecideAsync(
        AgentRun run,
        AgentModelResponse? lastModelResponse,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent Run 元数据持久化抽象（三层模式：基础接口 + IPersistent 标记）。
/// </summary>
/// <remarks>
/// 复用 IAgentCheckpointStore 的持久化模式：
/// - 基础接口由 InMemory 实现（开发/测试用）。
/// - IPersistentAgentRunStore 标记接口由 Postgres 实现继承。
/// - 主键 (workspace_id, run_id)，强制 workspaceId 防跨 workspace 误读。
/// </remarks>
public interface IAgentRunStore
{
    /// <summary>创建新 Run（幂等：ON CONFLICT DO NOTHING）。</summary>
    ValueTask CreateAsync(AgentRun run, CancellationToken cancellationToken = default);

    /// <summary>按 RunId 获取 Run 元数据。</summary>
    ValueTask<AgentRun?> GetAsync(string workspaceId, string runId, CancellationToken cancellationToken = default);

    /// <summary>更新 Run 状态（expected-state CAS：state 只能向前推进）。</summary>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="runId">Run ID。</param>
    /// <param name="expectedCurrentState">期望的当前状态（CAS 前件）。</param>
    /// <param name="newState">目标状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">当前状态与 expectedCurrentState 不匹配（逆退或已被其他实例推进）。</exception>
    ValueTask TransitionStateAsync(
        string workspaceId,
        string runId,
        AgentRunState expectedCurrentState,
        AgentRunState newState,
        CancellationToken cancellationToken = default);

    /// <summary>更新 Run 的可变字段（Turn / FinalAnswer / FailureReason 等）。</summary>
    ValueTask UpdateAsync(AgentRun run, CancellationToken cancellationToken = default);

    /// <summary>按 SessionId 列出所有 Run。</summary>
    ValueTask<IReadOnlyList<AgentRun>> ListBySessionAsync(string workspaceId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>按状态列出 Run（用于 HostedService 拉取待处理 Run）。</summary>
    ValueTask<IReadOnlyList<AgentRun>> ListByStateAsync(AgentRunState state, int take = 100, CancellationToken cancellationToken = default);
}

/// <summary>
/// 持久化 Agent Run Store 标记接口（复用 IPersistentAgentCheckpointStore 模式）。
/// </summary>
public interface IPersistentAgentRunStore : IAgentRunStore
{
}

/// <summary>
/// 审批门抽象。高风险操作（如删除、外部写入）需经审批后方可执行。
/// </summary>
/// <remarks>
/// 实现层可对接：
/// - 自动审批规则（基于 Tool 名称 + 参数模式匹配）。
/// - 人工审批工作流（发送到外部审批系统，等待人工确认）。
/// - 混合模式（低风险自动通过，高风险转人工）。
/// </remarks>
public interface IAgentApprovalGate
{
    /// <summary>
    /// 请求审批一个 Tool 调用。
    /// </summary>
    /// <param name="runId">Agent Run ID。</param>
    /// <param name="toolCall">待审批的 Tool 调用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>审批结果。</returns>
    ValueTask<AgentApprovalResult> RequestApprovalAsync(
        string runId,
        AgentToolCallRequest toolCall,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Tool 调用校验器抽象。在 Tool 实际执行前校验参数合法性与安全性。
/// </summary>
/// <remarks>
/// 校验维度：
/// - 参数 schema 合法性（必填参数存在、类型正确）。
/// - 安全检查（SQL 注入、路径穿越、命令注入）。
/// - 权限检查（当前 Run 是否有权限调用该 Tool）。
/// - 风险评估（是否需要审批）。
/// </remarks>
public interface IAgentToolCallValidator
{
    /// <summary>
    /// 校验一个 Tool 调用请求。
    /// </summary>
    /// <param name="runId">Agent Run ID。</param>
    /// <param name="toolCall">待校验的 Tool 调用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>校验结果（含是否需要审批）。</returns>
    ValueTask<AgentToolCallValidationResult> ValidateAsync(
        string runId,
        AgentToolCallRequest toolCall,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent Run 事件流持久化抽象（复用 Checkpoint 哈希链模式）。
/// </summary>
/// <remarks>
/// 事件流用于：
/// - 审计追踪（完整重现 Run 的每一步）。
/// - 崩溃恢复（从事件流重建 Run 状态）。
/// - 调试/回放（离线分析 Run 行为）。
///
/// 哈希链防篡改：
/// - 写入时计算 ContentHash = SHA-256(payload, ContentHash=null)。
/// - PrevChainHash = 前一事件的 ContentHash。
/// - 读取时校验 ContentHash + PrevChainHash。
/// </remarks>
public interface IAgentRunEventStore
{
    /// <summary>
    /// 追加一个事件到 Run 事件流。
    /// </summary>
    /// <param name="event">事件（ContentHash 应已计算填入）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">Sequence 不连续或 PrevChainHash 不匹配。</exception>
    ValueTask AppendAsync(AgentRunEvent @event, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取指定 Run 的事件流（按 Sequence 升序）。
    /// </summary>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="runId">Run ID。</param>
    /// <param name="fromSequence">起始序列号（含；默认 0 = 从头读取）。</param>
    /// <param name="take">最多读取条数（默认 1000）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<AgentRunEvent>> ReadAsync(
        string workspaceId,
        string runId,
        int fromSequence = 0,
        int take = 1000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定 Run 的最新序列号（用于断点续读）。
    /// </summary>
    ValueTask<int> GetLastSequenceAsync(string workspaceId, string runId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 持久化 Agent Run Event Store 标记接口。
/// </summary>
public interface IPersistentAgentRunEventStore : IAgentRunEventStore
{
}
