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

    /// <summary>
    /// 当前 Run 已发起的模型调用次数（每次 IAgentModelTransport.CallAsync 后递增）。
    /// 用于防止无 Tool 的模型循环无限运行（详见 AgentTurnBudget.MaxModelCalls）。
    /// </summary>
    public int ModelCallsUsed { get; init; }

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

    /// <summary>Token 消耗（用于 cost budget 校验；= InputTokens + OutputTokens）。</summary>
    public required int TokensConsumed { get; init; }

    /// <summary>推理耗时。</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>模型工件 ID（用于审计追踪）。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>原始模型输出（用于调试/审计）。</summary>
    public string? RawOutput { get; init; }

    /// <summary>
    /// 输入 token 数（prompt tokens）。用于精确成本核算与缓存命中率统计。
    /// 默认 0；真实 LLM adapter 应填充此字段。
    /// </summary>
    public int InputTokens { get; init; }

    /// <summary>
    /// 输出 token 数（completion tokens）。用于精确成本核算。
    /// 默认 0；真实 LLM adapter 应填充此字段。
    /// </summary>
    public int OutputTokens { get; init; }

    /// <summary>
    /// 命中缓存的输入 token 数（prompt caching）。用于核算实际计费 token。
    /// 默认 0；不支持缓存的 adapter 保持为 0。
    /// </summary>
    public int CachedInputTokens { get; init; }

    /// <summary>
    /// 模型标识（如 "gpt-4o-2024-08-06" / "claude-3-5-sonnet-20241022"）。
    /// 用于成本核算与审计。null = 未声明（fallback 实现可留空）。
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// 估算费用（美元）。由 adapter 基于输入/输出 token 单价计算。
    /// 默认 0；Actor 累积到此字段并写回 run.CostBudget.CostUsedUsd。
    /// </summary>
    public double EstimatedCost { get; init; }

    /// <summary>
    /// 实际计费费用（美元）。考虑缓存折扣后的真实计费金额。
    /// 默认 = EstimatedCost；adapter 未区分缓存时可与 EstimatedCost 相同。
    /// </summary>
    public double BilledCost { get; init; }
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

// ── AgentMessage：结构化上下文消息（G1：替代 string _accumulatedContext）────────

/// <summary>
/// Agent 消息角色（与 OpenAI/Anthropic chat completion 角色对齐）。
/// </summary>
/// <remarks>
/// G1 重构：AgentRunActor 不再用 <c>string _accumulatedContext</c> 平方级拼接，
/// 改为维护 <c>List&lt;AgentMessage&gt;</c>。模型 Transport 可直接消费结构化消息，
/// 或在调用前由 <c>BuildContextString</c> 一次性序列化为字符串。
/// </remarks>
public enum AgentMessageRole : byte
{
    /// <summary>系统指令（如任务说明、检索上下文）。</summary>
    System = 0,

    /// <summary>用户输入（如 run.Task）。</summary>
    User = 1,

    /// <summary>模型输出（assistant 文本）。</summary>
    Assistant = 2,

    /// <summary>Tool 观察结果（function/tool response）。</summary>
    Tool = 3
}

/// <summary>
/// G1：结构化 Agent 消息（替代 string _accumulatedContext 平方级拼接）。
/// </summary>
/// <remarks>
/// <b>引入背景</b>：旧实现通过 <c>_accumulatedContext = old + "\n---\n" + new</c> 不断拼接，
/// 长会话产生接近 O(total_context²) 的字符复制与 LOH/Gen2 压力。
/// 改为 <c>List&lt;AgentMessage&gt;</c> 后，仅追加引用，不再复制既有内容；
/// 模型调用前由 <c>BuildContextString</c> 一次性序列化。
///
/// <b>与事件流的关系</b>：<see cref="EventId"/> 关联生成此消息的审计事件，
/// 便于从事件流重建上下文（崩溃恢复 / 离线回放）。
/// </remarks>
public sealed record AgentMessage
{
    /// <summary>消息角色。</summary>
    public required AgentMessageRole Role { get; init; }

    /// <summary>消息内容（System/User/Assistant 文本，或 Tool 观察结果）。</summary>
    public required string Content { get; init; }

    /// <summary>
    /// 关联的审计事件 ID（可选）。
    /// 模型响应关联 ModelCallCompleted 事件；Tool 观察关联 ObservationAppended 事件。
    /// </summary>
    public string? EventId { get; init; }

    /// <summary>Tool 名称（仅 Role=Tool 时填充；用于审计与 ToolCall 关联）。</summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// G1：将结构化消息列表一次性序列化为模型可消费的字符串（仅在调用模型前执行一次）。
    /// </summary>
    /// <param name="messages">按时间顺序排列的消息列表。</param>
    /// <returns>以 <c>[Role]\nContent</c> 为单元、<c>\n---\n</c> 为分隔的字符串。</returns>
    /// <remarks>
    /// 替代旧路径 <c>_accumulatedContext = old + "\n---\n" + new</c> 的平方级拼接，
    /// 仅在调用模型前一次性 <see cref="StringBuilder"/> 拼接，避免 LOH/Gen2 压力。
    /// 真实 LLM adapter 可不调用此方法，直接消费 <see cref="IReadOnlyList{AgentMessage}"/>。
    /// </remarks>
    public static string Serialize(IReadOnlyList<AgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return string.Empty;
        }

        // 估算容量：每条消息约 Content.Length + 8 字节角色前缀
        var capacity = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            capacity += messages[i].Content.Length + 16;
        }
        var sb = new System.Text.StringBuilder(capacity);
        for (var i = 0; i < messages.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("\n---\n");
            }
            var m = messages[i];
            sb.Append('[').Append(RoleLabel(m.Role)).Append(']');
            if (!string.IsNullOrEmpty(m.ToolName))
            {
                sb.Append(':').Append(m.ToolName);
            }
            sb.Append('\n').Append(m.Content);
        }
        return sb.ToString();
    }

    private static string RoleLabel(AgentMessageRole role) => role switch
    {
        AgentMessageRole.System => "System",
        AgentMessageRole.User => "Task",
        AgentMessageRole.Assistant => "Assistant",
        AgentMessageRole.Tool => "Tool",
        _ => role.ToString()
    };
}

// ── AgentContextState：结构化 Agent 上下文状态（G5：委托 ContextCore 投影）────────

/// <summary>
/// G5：Tool 观察结果（结构化，替代直接拼接为 AgentMessage）。
/// </summary>
/// <remarks>
/// 将 Tool 执行结果以结构化形式保存于 <see cref="AgentContextState.ToolObservations"/>，
/// 由 <see cref="AgentContextState.ProjectForModel"/> 在调用模型前一次性投影为 Tool 角色
/// <see cref="AgentMessage"/>，避免在每次模型响应/Tool 观察时复制既有上下文字符串。
/// </remarks>
public sealed record ToolObservation
{
    /// <summary>Tool 名称。</summary>
    public required string ToolName { get; init; }

    /// <summary>Tool 调用 ID（与 ToolCallStarted/Completed 事件中的 toolCallId 一致）。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Tool 输出（成功时）。</summary>
    public string? Result { get; init; }

    /// <summary>错误信息（失败时）。</summary>
    public string? Error { get; init; }

    /// <summary>是否成功。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>投影为 <see cref="AgentMessage"/>（Tool 角色）。</summary>
    public AgentMessage ToAgentMessage()
    {
        var content = Succeeded ? Result ?? string.Empty : $"[ERROR] {Error}";
        return new AgentMessage
        {
            Role = AgentMessageRole.Tool,
            Content = content,
            ToolName = ToolName
        };
    }
}

/// <summary>
/// G5：稳定记忆引用（指向 ContextCore Stable Memory 中的条目）。
/// </summary>
/// <remarks>
/// 引用而非复制记忆正文；<see cref="AgentContextState.ProjectForModel"/> 投影时
/// 以 System 角色 <see cref="AgentMessage"/> 形式注入模型输入。
/// </remarks>
public sealed record MemoryReference
{
    /// <summary>记忆条目 ID。</summary>
    public required string MemoryId { get; init; }

    /// <summary>记忆内容摘要（投影为 System 消息）。</summary>
    public required string Content { get; init; }

    /// <summary>来源标识（如 "stable" / "working"）。</summary>
    public string? Source { get; init; }
}

/// <summary>
/// G5：结构化 Agent 上下文状态。
/// 替代旧路径中直接维护 <c>List&lt;AgentMessage&gt;</c> 的扁平结构，
/// 将上下文按角色分层（System / Constraints / CurrentTask / Working Set /
/// Tool Observations / Stable Memory），由 <see cref="ProjectForModel"/>
/// 根据 TokenBudget 投影最终模型输入。
/// </summary>
/// <remarks>
/// <b>设计目的</b>：G1 已消除 <c>_accumulatedContext</c> 字符串拼接（O(n²)），
/// G5 进一步引入结构化分层，让 ContextCore 的 TokenBudget / Compression / Snapshot
/// 能力可介入：
/// <list type="bullet">
///   <item><see cref="SystemPrompt"/> / <see cref="Constraints"/>：高优先级，总是保留。</item>
///   <item><see cref="CurrentTask"/>：用户任务（投影为 User 消息），总是保留。</item>
///   <item><see cref="Messages"/>：短期工作集（Assistant 响应、检索上下文等），按预算截断旧消息。</item>
///   <item><see cref="ToolObservations"/>：Tool 观察结果（投影为 Tool 消息），按预算截断旧观察。</item>
///   <item><see cref="StableMemoryReferences"/>：稳定记忆引用（投影为 System 消息）。</item>
///   <item><see cref="LastModelTurn"/>：最近一次模型响应（元数据，不直接投影；用于循环策略与审计）。</item>
/// </list>
///
/// <b>与 ContextCore 已有能力的关系</b>：
/// - Token 预算：<see cref="ProjectForModel"/> 内部使用字符估算（与
///   <c>LegacyCharacterTokenizer</c> 对齐）；生产环境可由调用方传入精确预算并
///   在此方法外用 <c>IContextTokenizerResolver</c> 重算。
/// - Compression：当预算不足时优先截断旧 <see cref="Messages"/> / 旧
///   <see cref="ToolObservations"/>；未来可接入 <c>IContextCompressor</c> 做语义压缩。
/// - Snapshot/Delta：本状态为不可变 record，可直接作为 Snapshot；Delta 由
///   <see cref="Messages"/> / <see cref="ToolObservations"/> 的追加差异构成。
/// </remarks>
public sealed record AgentContextState
{
    /// <summary>系统提示词（投影为 System 消息；高优先级，总是保留）。</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>约束条件（投影为 System 消息；高优先级，总是保留）。</summary>
    public string? Constraints { get; init; }

    /// <summary>当前任务（投影为 User 消息；高优先级，总是保留）。</summary>
    public string CurrentTask { get; init; } = string.Empty;

    /// <summary>短期工作集（Assistant 响应、检索上下文等历史消息）。</summary>
    public List<AgentMessage> Messages { get; init; } = new();

    /// <summary>Tool 观察结果列表（按时间顺序；投影为 Tool 消息）。</summary>
    public List<ToolObservation> ToolObservations { get; init; } = new();

    /// <summary>稳定记忆引用列表（投影为 System 消息）。</summary>
    public List<MemoryReference> StableMemoryReferences { get; init; } = new();

    /// <summary>最近一次模型响应（元数据，不直接投影；用于循环策略与审计）。</summary>
    public AgentModelResponse? LastModelTurn { get; init; }

    /// <summary>
    /// 根据 TokenBudget 投影最终模型输入消息列表。
    /// 优先保留：SystemPrompt → Constraints → StableMemoryReferences → CurrentTask
    /// → 最近 N 条 Messages → Tool Observations。
    /// 超出预算时从末尾（低优先级）截断。
    /// </summary>
    /// <param name="tokenBudget">
    /// Token 预算上限（&lt;=0 表示不限制，返回全部消息）。
    /// 使用字符估算 <c>Max(1, (length+1)/2)</c>，与 <c>LegacyCharacterTokenizer</c> 对齐；
    /// 生产环境可由调用方传入精确预算并在此方法外用
    /// <c>IContextTokenizerResolver</c> 重算。
    /// </param>
    /// <returns>投影后的消息列表（按优先级排序，受 TokenBudget 限制）。</returns>
    public IReadOnlyList<AgentMessage> ProjectForModel(int tokenBudget)
    {
        var projected = new List<AgentMessage>();

        // 1. SystemPrompt（高优先级，总是保留）
        if (!string.IsNullOrEmpty(SystemPrompt))
        {
            projected.Add(new AgentMessage
            {
                Role = AgentMessageRole.System,
                Content = SystemPrompt
            });
        }

        // 2. Constraints（高优先级，总是保留）
        if (!string.IsNullOrEmpty(Constraints))
        {
            projected.Add(new AgentMessage
            {
                Role = AgentMessageRole.System,
                Content = $"[Constraints]\n{Constraints}"
            });
        }

        // 3. StableMemoryReferences（投影为 System 消息）
        foreach (var mem in StableMemoryReferences)
        {
            projected.Add(new AgentMessage
            {
                Role = AgentMessageRole.System,
                Content = mem.Content
            });
        }

        // 4. CurrentTask（投影为 User 消息；高优先级，总是保留）
        if (!string.IsNullOrEmpty(CurrentTask))
        {
            projected.Add(new AgentMessage
            {
                Role = AgentMessageRole.User,
                Content = CurrentTask
            });
        }

        // 5-6. Messages + ToolObservations：根据预算截断
        if (tokenBudget <= 0)
        {
            // 无预算限制 — 全量返回（保持与旧路径 state.Messages 等价的行为）
            projected.AddRange(Messages);
            for (var i = 0; i < ToolObservations.Count; i++)
            {
                projected.Add(ToolObservations[i].ToAgentMessage());
            }
            return projected;
        }

        // 有预算限制 — 从最新向最旧纳入，直到预算耗尽
        var usedTokens = EstimateTokens(projected);
        var remaining = Math.Max(0, tokenBudget - usedTokens);

        // 5. 最近 N 条 Messages（从最新开始纳入，然后反转为时间顺序）
        var recentMessages = new List<AgentMessage>();
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            var msg = Messages[i];
            var tokens = EstimateTokens(msg.Content);
            if (remaining < tokens)
            {
                break;
            }
            recentMessages.Insert(0, msg);
            remaining -= tokens;
        }
        projected.AddRange(recentMessages);

        // 6. Tool Observations（从最新开始纳入，然后反转为时间顺序）
        var recentTools = new List<AgentMessage>();
        for (var i = ToolObservations.Count - 1; i >= 0; i--)
        {
            var obs = ToolObservations[i];
            var tokens = EstimateTokens(obs.Succeeded ? obs.Result : obs.Error);
            if (remaining < tokens)
            {
                break;
            }
            recentTools.Insert(0, obs.ToAgentMessage());
            remaining -= tokens;
        }
        projected.AddRange(recentTools);

        return projected;
    }

    /// <summary>字符估算 token 数（与 LegacyCharacterTokenizer 对齐：Max(1, (length+1)/2)）。</summary>
    private static int EstimateTokens(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }
        return Math.Max(1, (content.Length + 1) / 2);
    }

    /// <summary>批量估算消息列表的 token 数。</summary>
    private static int EstimateTokens(IReadOnlyList<AgentMessage> messages)
    {
        var total = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            total += EstimateTokens(messages[i].Content);
        }
        return total;
    }
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

    /// <summary>
    /// 最大模型调用次数（防止无 Tool 的模型循环无限运行）。
    /// 默认 0 表示未配置；LoopPolicy 在 MaxModelCalls&gt;0 且 ModelCallsUsed&gt;=MaxModelCalls 时强制 Fail。
    /// 推荐设置为 MaxTurns × 3 以允许模型多次重试。
    /// </summary>
    public int MaxModelCalls { get; init; }

    /// <summary>剩余轮次（MaxTurns - TurnsUsed）。</summary>
    public int Remaining => Math.Max(0, MaxTurns - TurnsUsed);

    /// <summary>是否已耗尽。</summary>
    public bool IsExhausted => TurnsUsed >= MaxTurns;

    /// <summary>
    /// 模型调用预算是否已耗尽（MaxModelCalls&gt;0 且 ModelCallsUsed&gt;=MaxModelCalls）。
    /// MaxModelCalls=0 时返回 false（未配置上限，不强制终止）。
    /// </summary>
    public bool IsModelCallBudgetExhausted => MaxModelCalls > 0 && ModelCallsUsed >= MaxModelCalls;

    /// <summary>当前已用模型调用次数（与 AgentRun.ModelCallsUsed 同步）。</summary>
    public int ModelCallsUsed { get; init; }
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

    /// <summary>
    /// G1：以结构化消息列表调用模型（替代 string context 平方级拼接）。
    /// </summary>
    /// <param name="runId">Agent Run ID。</param>
    /// <param name="messages">已构建的结构化消息列表（System/User/Assistant/Tool 角色）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>模型响应（含文本、Tool 调用、是否最终答案、Token 消耗）。</returns>
    /// <remarks>
    /// <b>设计目的</b>：旧路径 <c>CallAsync(runId, string context)</c> 在 Actor 端通过
    /// <c>_accumulatedContext = old + separator + new</c> 拼接历史，长会话产生接近 O(N²) 字符复制。
    /// 新路径由 Actor 维护 <c>List&lt;AgentMessage&gt;</c> 仅追加引用，
    /// Transport 可直接消费结构化消息（如真实 LLM adapter 的 chat completions），
    /// 或在内部一次性序列化为字符串再走旧路径。
    /// 默认实现可委托到 <see cref="CallAsync(string, string, CancellationToken)"/>。
    /// </remarks>
    ValueTask<AgentModelResponse> CallAsync(
        string runId,
        IReadOnlyList<AgentMessage> messages,
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
    /// G4：批量追加事件 + 可选 Run 状态 CAS + 可选 Checkpoint 游标，单事务提交。
    /// </summary>
    /// <param name="events">待追加的事件列表（已按 Sequence 升序、PrevChainHash 链接好）。</param>
    /// <param name="runStateUpdate">
    /// 可选：Run 状态 CAS + 可变字段更新（Turn / ModelCallsUsed / 预算 等）。
    /// Postgres 实现会在同一事务内 UPDATE agent_runs 行；InMemory 实现委托到注入的
    /// <see cref="IAgentRunStore"/>（若构造时注入；未注入时忽略，调用方需自行更新 Run 状态）。
    /// </param>
    /// <param name="checkpointCursor">
    /// 可选：本批事件覆盖的最新 checkpoint 游标（CheckpointId + LastEventSequence）。
    /// Postgres 实现会在同一事务内 UPDATE agent_runs.last_checkpoint_id / last_checkpoint_sequence。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">Sequence 不连续或 PrevChainHash 不匹配，或 CAS 失败。</exception>
    /// <remarks>
    /// <b>设计目的</b>：旧路径每次状态转换 / 模型开始 / 模型完成 / Tool 开始 / Tool 完成 /
    /// Observation / Checkpoint 都单独 <see cref="AppendAsync"/>，Postgres 下一个 Turn 产生
    /// 8-15 次事务/网络往返。新路径在一个 Turn 内收集所有事件到缓冲，Turn 结束时一次性批量提交，
    /// 将往返次数降到 1（Postgres 单事务：BEGIN → INSERT all events → UPDATE run state (CAS) →
    /// UPDATE checkpoint cursor → COMMIT）。
    ///
    /// <b>幂等性</b>：调用方负责保证 <paramref name="events"/> 的 Sequence 单调递增、
    /// PrevChainHash 正确链接；实现层在事务内校验首事件 Sequence = 当前 MAX(sequence) + 1、
    /// PrevChainHash = 前一事件 ContentHash。
    /// </remarks>
    ValueTask AppendBatchAsync(
        IReadOnlyList<AgentRunEvent> events,
        AgentRunStateUpdate? runStateUpdate,
        AgentCheckpointCursor? checkpointCursor,
        CancellationToken cancellationToken = default);

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
/// G4：Run 状态 CAS + 可变字段更新载荷（用于 <see cref="IAgentRunEventStore.AppendBatchAsync"/>）。
/// </summary>
/// <remarks>
/// 将原本由 <see cref="IAgentRunStore.TransitionStateAsync"/> + <see cref="IAgentRunStore.UpdateAsync"/>
/// 分两次网络往返完成的操作合并到事件批量提交的同一事务内，Postgres 实现走单事务。
/// </remarks>
public sealed record AgentRunStateUpdate
{
    /// <summary>Workspace ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Run ID。</summary>
    public required string RunId { get; init; }

    /// <summary>期望的当前状态（CAS 前件；与 store 中现有 state 不匹配时抛异常）。</summary>
    public required AgentRunState ExpectedCurrentState { get; init; }

    /// <summary>目标状态。</summary>
    public required AgentRunState NewState { get; init; }

    /// <summary>
    /// Run 字段快照（Turn / ModelCallsUsed / TurnBudget / CostBudget / FinalAnswer / FailureReason 等）。
    /// Postgres 实现将这些字段一并 UPDATE 到 agent_runs 行（与 state CAS 同事务）。
    /// </summary>
    public required AgentRun RunSnapshot { get; init; }
}

/// <summary>
/// G4：Checkpoint 游标（用于 <see cref="IAgentRunEventStore.AppendBatchAsync"/>）。
/// </summary>
/// <remarks>
/// 记录"本批事件覆盖的最新 checkpoint"信息，Postgres 实现将其写入 agent_runs 的
/// last_checkpoint_id / last_checkpoint_sequence 列（与事件批量提交同事务）。
/// checkpoint 本体仍由 <see cref="IAgentCheckpointStore.SaveAsync"/> 单独持久化到 agent_checkpoints 表；
/// 本游标只是 agent_runs 行上的反规范化指针，便于快速查询 Run 当前进度而无需扫描事件流。
/// </remarks>
public sealed record AgentCheckpointCursor
{
    /// <summary>Workspace ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Run ID。</summary>
    public required string RunId { get; init; }

    /// <summary>最新持久化的 Checkpoint ID。</summary>
    public required string CheckpointId { get; init; }

    /// <summary>该 Checkpoint 覆盖的最大事件 Sequence（用于断点续读 / 重放判断）。</summary>
    public required int LastEventSequence { get; init; }
}

/// <summary>
/// 持久化 Agent Run Event Store 标记接口。
/// </summary>
public interface IPersistentAgentRunEventStore : IAgentRunEventStore
{
}

// ── Durable Tool Executor（子问题 5）────────────────────────────────────────

/// <summary>
/// 子问题 5：Durable Tool Executor 抽象。
/// 封装 Tool 调用的完整 durable 流程：Validate → Approval → Journal.Prepare →
/// Dispatch → Journal.MarkDispatched → Commit/Unknown → Result Outbox →
/// Journal.MarkResultDelivered，保证 RequestId 在整个生命周期中一致。
/// </summary>
/// <remarks>
/// <b>引入背景</b>：AgentRunActor 直接调用 IToolDispatcher 会绕过 Durable Tool Journal，
/// 导致崩溃恢复时无法判断 tool 是否真正执行、无法重放已 commit 的结果。
/// 本接口封装旧 Kernel（DefaultAgentKernel.ProcessExecuteAsync）的 Tool 处理逻辑，
/// 让 Actor 复用同一套 durable 流程。
///
/// 与 IToolDispatcher 的区别：
/// - IToolDispatcher 仅负责单次 tool 调用（无 journal / 无 outbox / 无审批）。
/// - IDurableToolExecutor 负责完整生命周期（journal 状态机推进 + result outbox + 审批集成）。
/// </remarks>
public interface IDurableToolExecutor
{
    /// <summary>
    /// 执行一次 Tool 调用（完整 durable 流程）。
    /// </summary>
    /// <param name="runId">Agent Run ID（用于 journal 作用域与日志关联）。</param>
    /// <param name="workspaceId">Workspace ID（journal 作用域校验）。</param>
    /// <param name="toolCall">模型请求的 Tool 调用（含 ToolName + Arguments + 可选 IdempotencyKey）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Tool 执行结果（含 RequestId / SideEffect / JournalState / 结果本体）。</returns>
    ValueTask<ToolExecutionResult> ExecuteAsync(
        string runId,
        string workspaceId,
        AgentToolCallRequest toolCall,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 子问题 5：Durable Tool 执行结果。
/// 携带完整 Tool 身份信息（RequestId / IdempotencyKey / SideEffect / JournalState），
/// 供 Actor 写入 ToolCallCompleted 事件 payload（子问题 6）并恢复时重建 _committedToolResults。
/// </summary>
public sealed record ToolExecutionResult
{
    /// <summary>本次调用的 RequestId（Actor 与 Dispatcher 共享，整个生命周期一致）。</summary>
    public required string RequestId { get; init; }

    /// <summary>调用方提供的幂等键（可选；用于外部系统去重）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Tool 副作用分类（决定恢复时是否自动重放）。</summary>
    public required ToolSideEffect SideEffect { get; init; }

    /// <summary>外部操作 ID（tool 实际执行后返回的外部系统 ID，可用于查询/对账）。</summary>
    public string? ExternalOperationId { get; init; }

    /// <summary>
    /// Journal 最终状态（Committed / Dispatched / ResultDelivered）。
    /// Dispatched 表示模糊状态（tool 可能已成功执行但未 commit），需调用方裁决。
    /// </summary>
    public required ToolDispatchState JournalState { get; init; }

    /// <summary>Tool 输出（成功时）。</summary>
    public string? Result { get; init; }

    /// <summary>是否成功。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>错误信息（失败时）。</summary>
    public string? Error { get; init; }

    /// <summary>执行耗时。</summary>
    public required TimeSpan Duration { get; init; }
}

// ── Agent Run Lease（子问题 9）──────────────────────────────────────────────

/// <summary>
/// 子问题 9：Agent Run 租约抽象（HA 隔离）。
/// 复用 ICanaryLeaderLease 模式，确保同一时刻仅一个 Host 实例处理同一 Run。
/// </summary>
/// <remarks>
/// 实现层应使用 Postgres FOR UPDATE SKIP LOCKED 或分布式锁。
/// 进程内实现（InMemoryAgentRunLease）仅供开发/测试；生产部署应注入持久化实现。
/// </remarks>
public interface IAgentRunLease
{
    /// <summary>
    /// 尝试获取指定 Run 的处理租约。
    /// </summary>
    /// <param name="runId">Agent Run ID。</param>
    /// <param name="leaseDuration">租约有效期。</param>
    /// <param name="owner">候选持有者标识（如实例 ID）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>租约信息；已被其他实例持有时返回 null。</returns>
    ValueTask<LeasedAgentRun?> TryAcquireAsync(
        string runId,
        TimeSpan leaseDuration,
        string owner,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 续租约（心跳）。续约失败（租约被抢占或过期）时返回 false，
    /// 调用方应立即停止处理该 Run。
    /// </summary>
    /// <param name="runId">Agent Run ID。</param>
    /// <param name="leaseToken">租约 token（来自 TryAcquireAsync）。</param>
    /// <param name="extension">延长时间量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true = 续约成功；false = 租约已丢失，应停止处理。</returns>
    ValueTask<bool> RenewAsync(
        string runId,
        string leaseToken,
        TimeSpan extension,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 释放租约（主动让出）。通常在 Run 完成（Completed/Failed/Cancelled）后调用。
    /// </summary>
    ValueTask ReleaseAsync(
        string runId,
        string leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 回收过期租约（后台清理）。应由定时任务调用。
    /// </summary>
    /// <returns>回收的过期租约数。</returns>
    ValueTask<int> ReapExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 子问题 9：Agent Run 租约信息（TryAcquireAsync 返回值）。
/// </summary>
public sealed record LeasedAgentRun
{
    /// <summary>Agent Run ID。</summary>
    public required string RunId { get; init; }

    /// <summary>租约 token（续约/释放时必须提供）。</summary>
    public required string LeaseToken { get; init; }

    /// <summary>持有者标识（当前实例）。</summary>
    public required string Owner { get; init; }

    /// <summary>租约过期时间（UTC）。</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// 子问题 9：Agent Kernel Host 配置。
/// 控制 HA Run Lease 与全局/workspace 级并发上限。
/// </summary>
/// <remarks>
/// Learning Loop Durable Outbox 增强：新增 <see cref="ChannelCapacity"/> 与 <see cref="WorkerCount"/>
/// 替代每 Run 一个 <c>Task.Factory.StartNew</c> 的模式，改为 bounded Channel + 固定 worker 池——
/// 提供队列深度管理、拒绝策略、公平调度（FIFO）与优雅 drain。
/// </remarks>
public sealed class AgentHostOptions
{
    /// <summary>
    /// 是否启用 Run Lease（false = 单节点模式，不竞争租约）。
    /// 默认 false；生产部署应启用。
    /// </summary>
    public bool LeaseEnabled { get; set; } = false;

    /// <summary>全局最大并发 Run 数（SemaphoreSlim 控制上限）。</summary>
    public int MaxGlobalRuns { get; set; } = 100;

    /// <summary>单个 Workspace 最大并发 Run 数。</summary>
    public int MaxWorkspaceRuns { get; set; } = 10;

    /// <summary>租约有效期（应大于 HeartbeatInterval × 2 以避免误判）。</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>心跳续租间隔（应小于 LeaseDuration / 2）。</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Host 实例标识（null = 自动生成 host-{MachineName}-{guid}）。
    /// 用于 Run Lease 的 owner 字段。
    /// </summary>
    public string? Owner { get; set; }

    /// <summary>
    /// bounded Channel 容量上限（待执行的 Run 队列深度）。
    /// 超过后 <see cref="AgentKernelHost.StartRunAsync"/> 拒绝入队（拒绝策略）。
    /// 默认 256；应根据内存与吞吐需求调整。设为 0 或负数时使用默认值。
    /// </summary>
    public int ChannelCapacity { get; set; } = 256;

    /// <summary>
    /// 固定 worker 数（从 Channel 拉取 Run 并执行的后台任务数）。
    /// 应 &gt;= <see cref="MaxGlobalRuns"/> 以避免 worker 成为瓶颈（worker 阻塞在 SemaphoreSlim 等待槽位）。
    /// 默认 0 = 自动使用 <see cref="MaxGlobalRuns"/>。
    /// </summary>
    public int WorkerCount { get; set; } = 0;

    /// <summary>优雅 drain 超时（DisposeAsync 时等待 worker 排空的最长时间）。默认 30 秒。</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

// ── Durable Approval（运行时能力补齐：持久化审批状态）──────────────────────

/// <summary>
/// Agent Run 审批状态。
/// </summary>
public enum AgentApprovalStatus : byte
{
    /// <summary>待审批（已持久化，等待人工/自动裁决）。</summary>
    Pending = 0,

    /// <summary>已批准。</summary>
    Approved = 1,

    /// <summary>已拒绝。</summary>
    Rejected = 2
}

/// <summary>
/// Agent Run 审批记录（持久化到 agent_run_approvals 表）。
/// </summary>
/// <remarks>
/// 用于 durable approval：当 Actor 进入 AwaitingApproval 状态时持久化 Pending 记录，
/// 进程崩溃恢复后可重新加载未决审批；外部审批系统通过 approval_id 提交决策。
/// </remarks>
public sealed record AgentApproval
{
    /// <summary>审批唯一 ID（ULID/GUID）。</summary>
    public required string ApprovalId { get; init; }

    /// <summary>所属 Run ID。</summary>
    public required string RunId { get; init; }

    /// <summary>Workspace ID（隔离边界）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>关联的 Tool 调用 ID（与 ToolCallStarted 事件中的 toolCallId 一致）。</summary>
    public required string ToolCallId { get; init; }

    /// <summary>Tool 名称（便于按 Tool 维度查询审批历史）。</summary>
    public required string ToolName { get; init; }

    /// <summary>当前审批状态。</summary>
    public required AgentApprovalStatus Status { get; init; }

    /// <summary>审批原因（请求时填写，说明为何需要审批）。</summary>
    public string? Reason { get; init; }

    /// <summary>拒绝原因（Status=Rejected 时填写）。</summary>
    public string? RejectionReason { get; init; }

    /// <summary>审批者标识（人工审批时为用户 ID；自动审批时为规则 ID）。</summary>
    public string? ApproverId { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>裁决时间（UTC；Status=Pending 时为 null）。</summary>
    public DateTimeOffset? ResolvedAt { get; init; }
}

/// <summary>
/// Agent Run 审批持久化抽象（三层模式：基础接口 + IPersistent 标记）。
/// </summary>
/// <remarks>
/// 实现层：
/// - <c>InMemoryAgentApprovalStore</c>：开发/测试用，进程内 ConcurrentDictionary。
/// - <c>PostgresAgentApprovalStore</c>：生产持久化，agent_run_approals 表。
///
/// <b>与 <see cref="IAgentApprovalGate"/> 的关系</b>：
/// Gate 负责决策（自动/人工）；Store 负责持久化状态。
/// DurableAgentApprovalGate 在 RequestApprovalAsync 时先 Store.CreateAsync(Pending)，
/// 再走自动规则或等待外部 ResolveAsync。
/// </remarks>
public interface IAgentApprovalStore
{
    /// <summary>创建审批记录（Pending 状态；幂等：ON CONFLICT DO NOTHING）。</summary>
    ValueTask CreateAsync(AgentApproval approval, CancellationToken cancellationToken = default);

    /// <summary>按 approval_id 获取审批记录。</summary>
    ValueTask<AgentApproval?> GetAsync(string workspaceId, string approvalId, CancellationToken cancellationToken = default);

    /// <summary>列出指定 Run 的所有未决审批（Status=Pending）。</summary>
    ValueTask<IReadOnlyList<AgentApproval>> ListPendingAsync(string workspaceId, string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 裁决审批（CAS：Status=Pending → Approved/Rejected）。
    /// </summary>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="approvalId">审批 ID。</param>
    /// <param name="decision">决策（Approved / Rejected）。</param>
    /// <param name="approverId">审批者标识。</param>
    /// <param name="rejectionReason">拒绝原因（Rejected 时填写）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">审批不存在或已裁决（CAS 失败）。</exception>
    ValueTask ResolveAsync(
        string workspaceId,
        string approvalId,
        AgentApprovalStatus decision,
        string? approverId,
        string? rejectionReason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 持久化 Agent Approval Store 标记接口（复用 IPersistentAgentRunStore 模式）。
/// </summary>
public interface IPersistentAgentApprovalStore : IAgentApprovalStore
{
}
