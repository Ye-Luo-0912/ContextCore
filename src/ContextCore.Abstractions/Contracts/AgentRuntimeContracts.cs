namespace ContextCore.Abstractions;

// ===========================================================================
// Agent Runtime Integration 契约
//
// 目标（对齐用户规格第六节）：
//   当 Context Decision Runtime 稳定后，ContextCore 作为 Codex / Claude Code /
//   其他 Agent 的统一上下文层。
//
// 设计原则：
//   1. 定义自己的稳定协议（IAgentRuntime / IAgentSession / IAgentEventStream /
//      IAgentWorkspaceContextProvider / IAgentCheckpointStore）。
//   2. Provider Adapter 在 Core/Adapter 层实现，将 Agent SDK 对象模型转换为
//      ContextCore 内部模型。
//   3. ContextCore 不直接依赖某一个 Agent SDK 的对象模型；所有 SDK 特定类型
//      保留在 Adapter 实现内部，不进入 Abstractions。
//   4. ContextCore 提供的能力：session context snapshot / task state /
//      relevant project context / decision+constraint injection /
//      tool result ingestion / checkpoint+resume / context delta /
//      token-budget-aware package。
//
// 子阶段进度：
//   （当前）：5 个核心接口契约 + AgentRuntimeKind 枚举 + AgentSessionId +
//                  AgentEvent 类型（最小可实施集）。
//   AgentContextSnapshot / AgentTaskState / AgentContextDelta 数据契约。
//   GenericToolAgentAdapter + DefaultAgentWorkspaceContextProvider 实现。
//   CodexAgentRuntimeAdapter / ClaudeAgentRuntimeAdapter + 全量测试。
// ===========================================================================

/// <summary>
/// Agent Runtime 类型。标识当前接入的 Agent SDK 来源。
/// </summary>
/// <remarks>
/// 用于 <see cref="IAgentRuntime"/>) 的标识与路由；ContextCore 内部不依赖此枚举
/// 选择分支逻辑，仅用于 trace / 审计 / adapter 注册。
/// </remarks>
public enum AgentRuntimeKind : byte
{
    /// <summary>未知 runtime（占位；不应出现在正式 session 中）。</summary>
    Unknown = 0,

    /// <summary>通用工具型 Agent（无特定 SDK 适配）。</summary>
    GenericTool = 1,

    /// <summary>OpenAI Codex Agent SDK。</summary>
    Codex = 2,

    /// <summary>Anthropic Claude Code Agent SDK。</summary>
    ClaudeCode = 3,

    /// <summary>自定义 Agent runtime（用户实现）。</summary>
    Custom = 4
}

/// <summary>
/// Agent Session 标识。跨 turn 唯一标识一次 agent 会话。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. SessionId 由 IAgentRuntime.CreateSessionAsync 分配，全局唯一。
///   2. SessionId 不依赖具体 SDK 的 session/conversation id 语义；
///      Adapter 负责将 SDK session id 映射到本类型。
///   3. SessionId 用于 checkpoint、event stream 订阅、workspace context 绑定。
/// </remarks>
public sealed record AgentSessionId
{
    /// <summary>Session 唯一标识（如 "session-{guid}"）。</summary>
    public required string Value { get; init; }

    /// <summary>所属 Agent runtime 类型（用于 trace 与审计）。</summary>
    public AgentRuntimeKind RuntimeKind { get; init; } = AgentRuntimeKind.Unknown;

    /// <summary>workspace 作用域（必填；Agent session 与 workspace 绑定）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（可选；同一 workspace 内多 collection 隔离）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>Session 创建时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Agent 事件类型。覆盖 Agent 生命周期与决策关键节点。
/// </summary>
/// <remarks>
/// 用于 <see cref="IAgentEventStream"/> 的事件分类与过滤；
/// ContextCore 不假设事件顺序，但 checkpoint/resume 时会按事件时间戳排序。
/// </remarks>
public enum AgentEventKind : byte
{
    /// <summary>未知事件（占位）。</summary>
    Unknown = 0,

    /// <summary>Session 创建。</summary>
    SessionCreated = 1,

    /// <summary>Session 结束。</summary>
    SessionClosed = 2,

    /// <summary>Turn 开始（一次 user→assistant 交互）。</summary>
    TurnStarted = 3,

    /// <summary>Turn 结束。</summary>
    TurnCompleted = 4,

    /// <summary>Tool 调用发起。</summary>
    ToolCallStarted = 5,

    /// <summary>Tool 调用返回结果。</summary>
    ToolCallCompleted = 6,

    /// <summary>Tool 调用失败。</summary>
    ToolCallFailed = 7,

    /// <summary>Context 被注入到 Agent。</summary>
    ContextInjected = 8,

    /// <summary>Agent 决策点（如选择 tool / 选择回复路径）。</summary>
    DecisionPoint = 9,

    /// <summary>Checkpoint 创建。</summary>
    CheckpointCreated = 10,

    /// <summary>Checkpoint 恢复。</summary>
    CheckpointResumed = 11,

    /// <summary>Token 预算警告（接近上限）。</summary>
    TokenBudgetWarning = 12,

    /// <summary>Token 预算耗尽。</summary>
    TokenBudgetExhausted = 13
}

/// <summary>
/// Agent 事件严重级别。复用 <see cref="ContextEventLevel"/> 语义但单独定义，
/// 避免与 ContextRuntime 内部事件混合。
/// </summary>
public enum AgentEventLevel : byte
{
    /// <summary>跟踪级别。</summary>
    Trace = 0,

    /// <summary>信息级别。</summary>
    Information = 1,

    /// <summary>警告级别。</summary>
    Warning = 2,

    /// <summary>错误级别。</summary>
    Error = 3
}

/// <summary>
/// Agent 事件记录。由 <see cref="IAgentEventStream"/> 推送。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 事件为不可变 record；写入后不可修改。
///   2. Payload 为自由 JSON 字符串（由 adapter 负责序列化）；
///      ContextCore 不解析 Payload 内部结构。
///   3. CorrelationId 用于跨事件链路追踪（如同一 turn 内的多个 tool call）。
/// </remarks>
public sealed record AgentEvent
{
    /// <summary>事件唯一 ID（如 "evt-{guid}"）。</summary>
    public required string EventId { get; init; }

    /// <summary>所属 session。</summary>
    public required AgentSessionId Session { get; init; }

    /// <summary>事件类型。</summary>
    public required AgentEventKind Kind { get; init; }

    /// <summary>事件严重级别。</summary>
    public AgentEventLevel Level { get; init; } = AgentEventLevel.Information;

    /// <summary>事件发生时间（UTC）。</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>关联 ID（同一 turn / 同一 tool call 链共享）。</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Turn 标识（可选；用于聚合 turn 内事件）。</summary>
    public string? TurnId { get; init; }

    /// <summary>事件负载（自由 JSON 字符串；由 adapter 序列化）。</summary>
    public string? PayloadJson { get; init; }

    /// <summary>事件元数据（用于 trace 与审计）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Agent Runtime 接口。ContextCore 接入 Agent SDK 的统一入口。
/// </summary>
/// <remarks>
/// 设计原则（对齐用户规格）：
///   1. ContextCore 不直接依赖某一个 Agent SDK 的对象模型；
///      Adapter 实现负责 SDK 对象 → ContextCore 模型转换。
///   2. Runtime 是 session 的工厂；session 创建后由 IAgentSession 管理。
///   3. Runtime 不暴露 SDK 特定配置（如 model name / temperature）；
///      这些由 Adapter 内部管理。
///   4. Runtime 是幂等的：相同输入应产生相同 session id（确定性）。
/// </remarks>
public interface IAgentRuntime
{
    /// <summary>Runtime 标识（如 "codex-v1" / "claude-code-v1" / "generic-v1"）。</summary>
    string RuntimeId { get; }

    /// <summary>Runtime 类型。</summary>
    AgentRuntimeKind RuntimeKind { get; }

    /// <summary>创建新的 agent session。</summary>
    /// <param name="request">Session 创建请求（workspace + collection + 可选 metadata）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建 session 的标识。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    Task<AgentSessionId> CreateSessionAsync(
        AgentSessionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>关闭 session（释放资源；后续事件不再接受）。</summary>
    /// <param name="sessionId">Session 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>关闭是否成功（false = session 不存在或已关闭）。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    Task<bool> CloseSessionAsync(
        AgentSessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>查询 session 是否仍然活跃。</summary>
    /// <param name="sessionId">Session 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true = 活跃；false = 已关闭或不存在。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    Task<bool> IsSessionActiveAsync(
        AgentSessionId sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent Session 创建请求。
/// </summary>
public sealed record AgentSessionRequest
{
    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（可选；为空使用默认 collection）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>初始 turn ID（可选；由调用方提供时跳过自动生成）。</summary>
    public string? InitialTurnId { get; init; }

    /// <summary>Session 元数据（用于 trace 与审计）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Agent Session 接口。管理一次 agent 会话的状态与事件流。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. Session 是有状态的；同一 session 内的多次 turn 共享 context。
///   2. Session 不直接调用 ContextCore 内部接口；通过 IAgentWorkspaceContextProvider
///      间接访问 context snapshot / task state / decision injection。
///   3. Session 关闭后所有方法抛 InvalidOperationException。
/// </remarks>
public interface IAgentSession
{
    /// <summary>Session 标识。</summary>
    AgentSessionId SessionId { get; }

    /// <summary>Event stream（用于订阅 session 内事件）。</summary>
    IAgentEventStream Events { get; }

    /// <summary>开始新的 turn（生成 TurnId 或使用指定值）。</summary>
    /// <param name="turnId">可选 turn ID（为空时自动生成 "turn-{guid}"）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>turn 标识。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    Task<string> StartTurnAsync(string? turnId = null, CancellationToken cancellationToken = default);

    /// <summary>结束当前 turn。</summary>
    /// <param name="turnId">要结束的 turn ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [StoreOperation(StoreOperationKind.Write)]
    Task CompleteTurnAsync(string turnId, CancellationToken cancellationToken = default);

    /// <summary>记录 tool 调用结果（ContextCore 通过此方法摄入 tool 输出）。</summary>
    /// <param name="toolCallId">Tool 调用 ID。</param>
    /// <param name="toolName">Tool 名称。</param>
    /// <param name="resultJson">Tool 输出（JSON 字符串）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [StoreOperation(StoreOperationKind.Write)]
    Task RecordToolCallResultAsync(
        string toolCallId,
        string toolName,
        string resultJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent Event Stream 接口。订阅 session 内事件。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. Event stream 是只读的；事件由 runtime/session 写入。
///   2. 支持按 Kind / 时间范围过滤。
///   3. 订阅是 push 模型（IAsyncEnumerable）+ pull 模型（QueryAsync）。
/// </remarks>
public interface IAgentEventStream
{
    /// <summary>订阅 session 内的实时事件（push 模型）。</summary>
    /// <param name="sessionId">Session 标识。</param>
    /// <param name="cancellationToken">取消令牌（取消时停止订阅）。</param>
    /// <returns>事件流（异步枚举）。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    IAsyncEnumerable<AgentEvent> SubscribeAsync(
        AgentSessionId sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>查询 session 内历史事件（pull 模型）。</summary>
    /// <param name="query">查询条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>事件列表（按 OccurredAt 升序）。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<AgentEvent>> QueryAsync(
        AgentEventQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent 事件查询条件。
/// </summary>
public sealed record AgentEventQuery
{
    /// <summary>Session 标识（必填）。</summary>
    public required AgentSessionId SessionId { get; init; }

    /// <summary>按 Kind 过滤（null = 所有类型）。</summary>
    public AgentEventKind? Kind { get; init; }

    /// <summary>按 Level 过滤（null = 所有级别）。</summary>
    public AgentEventLevel? Level { get; init; }

    /// <summary>按 TurnId 过滤（null = 所有 turn）。</summary>
    public string? TurnId { get; init; }

    /// <summary>按 CorrelationId 过滤（null = 所有链路）。</summary>
    public string? CorrelationId { get; init; }

    /// <summary>起始时间（UTC，包含；null = 不限）。</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>结束时间（UTC，包含；null = 不限）。</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>最大返回数量（默认 100；0 = 不限）。</summary>
    public int Take { get; init; } = 100;
}

/// <summary>
/// Agent Workspace Context Provider 接口。
/// 向 Agent 提供 ContextCore 的能力（snapshot / task state / decision injection）。
/// </summary>
/// <remarks>
/// 设计原则（对齐用户规格）：
///   1. Provider 是 Agent 访问 ContextCore 的唯一入口；
///      Agent 不直接调用 ContextCore 内部接口。
///   2. Provider 提供的能力：
///      - session context snapshot（按 token 预算打包）
///      - task state（agent 当前任务状态）
///      - relevant project context（按相关性过滤的 context）
///      - decision/constraint injection（注入决策与约束到 context）
///      - tool result ingestion（摄入 tool 输出到 context）
///      - checkpoint/resume（session 状态保存与恢复）
///      - context delta（增量 context 变更）
///      - token-budget-aware package（按预算打包）
///   3. R23-1 阶段仅定义接口；具体实现由 R23-2/R23-3 完成。
/// </remarks>
public interface IAgentWorkspaceContextProvider
{
    /// <summary>获取 session 的当前 context snapshot（按 token 预算打包）。</summary>
    /// <param name="sessionId">Session 标识。</param>
    /// <param name="tokenBudget">Token 预算上限（必填，>0）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>打包后的 context snapshot（具体类型在 R23-2 定义）。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    Task<AgentContextSnapshotRef> GetContextSnapshotAsync(
        AgentSessionId sessionId,
        int tokenBudget,
        CancellationToken cancellationToken = default);

    /// <summary>注入决策/约束到 context（影响后续 turn 的 context 打包）。</summary>
    /// <param name="sessionId">Session 标识。</param>
    /// <param name="injection">注入内容（决策 + 约束）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [StoreOperation(StoreOperationKind.Write)]
    Task InjectAsync(
        AgentSessionId sessionId,
        AgentContextInjection injection,
        CancellationToken cancellationToken = default);

    /// <summary>摄入 tool 调用结果到 context（供后续 turn 引用）。</summary>
    /// <param name="sessionId">Session 标识。</param>
    /// <param name="toolCallId">Tool 调用 ID。</param>
    /// <param name="toolName">Tool 名称。</param>
    /// <param name="resultJson">Tool 输出（JSON 字符串）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [StoreOperation(StoreOperationKind.Write)]
    Task IngestToolResultAsync(
        AgentSessionId sessionId,
        string toolCallId,
        string toolName,
        string resultJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent Context Snapshot 引用。
/// 阶段仅定义引用类型；具体 snapshot 内容由 R23-2 定义。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. SnapshotRef 是轻量引用；实际 snapshot 内容可能很大（按 token 预算打包）。
///   2. SnapshotId 用于 checkpoint/resume；同一 session 多次 snapshot 有不同 ID。
///   3. ContentJson 由 provider 序列化；ContextCore 不解析其内部结构。
/// </remarks>
public sealed record AgentContextSnapshotRef
{
    /// <summary>Snapshot 唯一 ID（如 "snap-{guid}"）。</summary>
    public required string SnapshotId { get; init; }

    /// <summary>所属 session。</summary>
    public required AgentSessionId Session { get; init; }

    /// <summary>Snapshot 创建时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>实际 token 数（≤ 请求的 tokenBudget）。</summary>
    public int ActualTokens { get; init; }

    /// <summary>请求的 token 预算上限。</summary>
    public int TokenBudget { get; init; }

    /// <summary>Snapshot 内容（JSON 字符串；具体结构由 R23-2 定义）。</summary>
    public required string ContentJson { get; init; }

    /// <summary>Snapshot 元数据（如 section 计数、source 计数等摘要）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Agent Context Injection。向 context 注入决策与约束。
/// 阶段仅定义注入载体；具体注入内容由 R23-2 定义。
/// </summary>
public sealed record AgentContextInjection
{
    /// <summary>注入的唯一 ID（如 "inj-{guid}"）。</summary>
    public required string InjectionId { get; init; }

    /// <summary>注入的决策 ID 列表（引用 ContextDecisionResult.RequestId）。</summary>
    public IReadOnlyList<string> DecisionRequestIds { get; init; }
        = Array.Empty<string>();

    /// <summary>注入的约束 ID 列表（引用 IConstraintStore 中的约束）。</summary>
    public IReadOnlyList<string> ConstraintIds { get; init; }
        = Array.Empty<string>();

    /// <summary>自由文本注入（如系统提示、用户偏好）。</summary>
    public string? FreeText { get; init; }

    /// <summary>注入时间（UTC）。</summary>
    public required DateTimeOffset InjectedAt { get; init; }

    /// <summary>注入元数据。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Agent Checkpoint Store 接口。持久化 session 状态以支持 checkpoint/resume。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. Checkpoint 是 session 级别的；不同 session 的 checkpoint 隔离。
///   2. Checkpoint 内容由 IAgentSession 实现 + provider 协作产生；
///      Store 仅负责持久化与查询。
///   3. Resume 时恢复 session 状态（包括 context snapshot / task state / event 顺序）。
///   4. Store 是 read-write 接口；写入通过 SaveAsync，读取通过 GetAsync / ListAsync。
///   5. P0-6 修复：GetAsync / DeleteAsync 必须传 workspaceId 以保证跨 workspace 隔离。
///      主键为 (workspace_id, checkpoint_id)；调用方必须显式传入 workspaceId，
///      不允许只按 checkpointId 查询（避免跨 workspace 误读 / 误删）。
/// </remarks>
public interface IAgentCheckpointStore
{
    /// <summary>保存 checkpoint。</summary>
    /// <param name="checkpoint">Checkpoint 数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [StoreOperation(StoreOperationKind.Write)]
    Task SaveAsync(AgentCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定 checkpoint。
    /// </summary>
    /// <param name="workspaceId">workspace 作用域（与 checkpoint 主键组合；P0-6 修复）。</param>
    /// <param name="checkpointId">Checkpoint ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Checkpoint 数据（null = 不存在或跨 workspace 不可见）。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    Task<AgentCheckpoint?> GetAsync(
        string workspaceId,
        string checkpointId,
        CancellationToken cancellationToken = default);

    /// <summary>列出 session 的所有 checkpoint（按时间倒序）。</summary>
    /// <param name="sessionId">Session 标识。</param>
    /// <param name="take">最大返回数量（默认 10）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Checkpoint 列表（按 CreatedAt 倒序）。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<AgentCheckpoint>> ListAsync(
        AgentSessionId sessionId,
        int take = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除 checkpoint。
    /// </summary>
    /// <param name="workspaceId">workspace 作用域（与 checkpoint 主键组合；P0-6 修复）。</param>
    /// <param name="checkpointId">Checkpoint ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true = 删除成功；false = 不存在或跨 workspace 不可见。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    Task<bool> DeleteAsync(
        string workspaceId,
        string checkpointId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent Checkpoint 数据。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. Checkpoint 是不可变 record；保存后不可修改。
///   2. StateJson 由 IAgentSession 实现 + provider 协作序列化；
///      ContextCore 不解析其内部结构。
///   3. CheckpointId 全局唯一（如 "ckpt-{guid}"）。
/// </remarks>
public sealed record AgentCheckpoint
{
    /// <summary>Checkpoint 唯一 ID。</summary>
    public required string CheckpointId { get; init; }

    /// <summary>所属 session。</summary>
    public required AgentSessionId Session { get; init; }

    /// <summary>Checkpoint 创建时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>当前 turn ID（可选；checkpoint 时的 turn）。</summary>
    public string? TurnId { get; init; }

    /// <summary>关联的 context snapshot ID（可选）。</summary>
    public string? SnapshotId { get; init; }

    /// <summary>Checkpoint 状态（JSON 字符串；由 session + provider 序列化）。</summary>
    public required string StateJson { get; init; }

    /// <summary>Checkpoint 元数据。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}
