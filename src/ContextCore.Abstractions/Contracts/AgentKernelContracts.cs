namespace ContextCore.Abstractions;

// ===========================================================================
// R28-C：Agent Kernel 契约 — 极薄 .NET 决策循环
//
// 目标（对齐 Workstream C 规格）：
//   1. 定义 IAgentKernel：极薄 .NET loop，从 Transport 接收指令 → 分派 Tool →
//      检查点。不负责业务逻辑、模型推理、状态管理。
//   2. 定义 IAgentKernelTransport：指令传输抽象（接收指令 / 发送结果）。
//   3. 定义 IToolDispatcher：Tool 分派抽象（按名称执行 tool 并返回结果）。
//   4. 定义数据契约：AgentKernelInstruction / AgentKernelResult / AgentKernelStatus /
//      ToolDispatchRequest / ToolDispatchResult。
//
// 设计原则：
//   1. Kernel 是极薄的编排层；不持有业务状态（状态由 Runtime/Engine/Store 处理）。
//   2. Transport 与 ToolDispatcher 是可替换的抽象；默认实现为 InProcessTransport /
//      EchoToolDispatcher（测试与单机部署用）。
//   3. 所有方法支持 CancellationToken；取消时抛 OperationCanceledException。
//   4. Kernel 自身维护一个 bounded inbox channel（容量 256），SubmitAsync 写入 inbox，
//      RunAsync 从 inbox 读取并处理；结果通过 Transport.SendResultAsync 发出。
// ===========================================================================

/// <summary>
/// R28-C：Agent Kernel — 极薄 .NET 决策循环。
/// </summary>
/// <remarks>
/// 负责：从 Transport 接收指令 → 调用 IContextDecisionRuntime → 分派 Tool → 检查点。
/// 不负责：业务逻辑、模型推理、状态管理（这些由 Runtime/Engine/Store 处理）。
///
/// <b>线程安全</b>：实现应当线程安全；SubmitAsync 可从多线程并发调用，
/// RunAsync 单消费者执行。
/// </remarks>
public interface IAgentKernel
{
    /// <summary>运行 Kernel 循环，直到收到终止信号或取消令牌触发。</summary>
    /// <param name="cancellationToken">取消令牌；取消时循环终止并抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>表示循环执行的 ValueTask；正常终止（Shutdown 指令）时正常完成。</returns>
    ValueTask RunAsync(CancellationToken cancellationToken = default);

    /// <summary>提交一条指令到 Kernel 输入队列。</summary>
    /// <param name="instruction">要提交的指令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask SubmitAsync(AgentKernelInstruction instruction, CancellationToken cancellationToken = default);

    /// <summary>获取当前 Kernel 状态（线程安全快照）。</summary>
    /// <returns>当前状态快照。</returns>
    AgentKernelStatus GetStatus();

    /// <summary>
    /// R28-C WP-B：从 checkpoint 恢复 Kernel 状态。
    /// 恢复已提交的 tool 结果（去重，避免重复执行）和上次 AgentContextSnapshot。
    /// 恢复后可调用 <see cref="RunAsync"/> 继续处理。
    /// </summary>
    /// <param name="checkpoint">之前保存的检查点（含已提交 tool 结果 + snapshot 引用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// R28-E P1-2：若注入了 <see cref="IAgentContextSnapshotStore"/>，将根据
    /// <see cref="AgentCheckpoint.SnapshotId"/> 加载并恢复 _lastSnapshot。
    /// 未注入 store 时仅恢复已提交 tool 结果（snapshot 保持 null）。
    /// </remarks>
    ValueTask ResumeAsync(AgentCheckpoint checkpoint, CancellationToken cancellationToken = default);
}

/// <summary>
/// R28-C：Kernel 指令。
/// </summary>
/// <remarks>
/// 指令由外部调用方构造，通过 <see cref="IAgentKernel.SubmitAsync"/> 提交到 Kernel。
/// Payload 为自由文本（由调用方与 ToolDispatcher 约定语义）；Metadata 携带附加键值对
/// （如 tool 名称、sessionId、workspaceId 等）。
/// </remarks>
public sealed record AgentKernelInstruction
{
    /// <summary>指令唯一 ID（用于关联 <see cref="AgentKernelResult.InstructionId"/>）。</summary>
    public required string InstructionId { get; init; }

    /// <summary>指令类型。</summary>
    public required AgentKernelInstructionKind Kind { get; init; }

    /// <summary>指令负载（自由文本；语义由调用方与 ToolDispatcher 约定）。</summary>
    public string? Payload { get; init; }

    /// <summary>指令元数据（如 tool 名称、sessionId、workspaceId 等附加键值对）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// R28-C：Kernel 指令类型。
/// </summary>
public enum AgentKernelInstructionKind : byte
{
    /// <summary>执行指令：调用 ToolDispatcher 分派 tool。</summary>
    Execute = 0,

    /// <summary>检查点指令：调用 IAgentCheckpointStore 保存当前状态。</summary>
    Checkpoint = 1,

    /// <summary>关闭指令：排空 inbox 后停止 Kernel 循环。</summary>
    Shutdown = 2,

    /// <summary>
    /// R28-C WP-A：构建 Agent Context 指令。
    /// 调用 IContextDecisionRuntime.ExecuteWithWorkingSetAsync(Purpose=AgentContext) →
    /// IAgentContextProjector.Project → 产出 AgentContextSnapshot。
    /// Metadata 必须含 workspaceId / collectionId / sessionId；可选 queryText / tokenBudget / requiredIds。
    /// </summary>
    BuildContext = 3,

    /// <summary>
    /// R28-E P1-3：确认 Unknown 副作用 tool 结果。
    /// 将 <see cref="ToolSideEffect.Unknown"/> 的 tool 结果从 pending 移到 committed，
    /// 恢复时可安全重放。Metadata 必须含 "requestId"（要确认的 tool RequestId）。
    /// </summary>
    AcknowledgeToolResult = 4,

    /// <summary>
    /// R28-E P1-3：拒绝 Unknown 副作用 tool 结果。
    /// 将 pending 的 Unknown 结果丢弃（不提交，不重放）。
    /// Metadata 必须含 "requestId"（要拒绝的 tool RequestId）；
    /// 可选 "reason"（拒绝原因，写入结果 Output）。
    /// </summary>
    RejectToolResult = 5,

    /// <summary>
    /// R28-E P1-3：查询 tool 分派状态。
    /// 返回指定 RequestId 的分派状态（Prepared/Dispatched/Committed/ResultDelivered/Unknown）。
    /// Metadata 必须含 "requestId"。
    /// </summary>
    QueryToolDispatchState = 6
}

/// <summary>
/// R28-C：Kernel 运行状态。
/// </summary>
public sealed record AgentKernelStatus
{
    /// <summary>当前 Kernel 状态。</summary>
    public required AgentKernelState State { get; init; }

    /// <summary>已处理指令数（Execute + Checkpoint；不含 Shutdown）。</summary>
    public required int ProcessedCount { get; init; }

    /// <summary>当前 inbox 中待处理指令数。</summary>
    public required int PendingCount { get; init; }

    /// <summary>上次处理指令的时间（UTC）；未处理过时为 null。</summary>
    public DateTimeOffset? LastProcessedAt { get; init; }
}

/// <summary>
/// R28-C：Kernel 运行状态枚举。
/// </summary>
public enum AgentKernelState : byte
{
    /// <summary>空闲：RunAsync 尚未调用。</summary>
    Idle = 0,

    /// <summary>运行中：RunAsync 正在执行循环。</summary>
    Running = 1,

    /// <summary>排空中：收到 Shutdown 指令，正在排空 inbox。</summary>
    Draining = 2,

    /// <summary>已停止：循环已退出（Shutdown 完成或取消）。</summary>
    Stopped = 3
}

/// <summary>
/// R28-C WP-D：传输失败策略。当 Kernel 通过 Transport 发送/接收失败时的处理方式。
/// </summary>
public enum TransportFailurePolicy : byte
{
    /// <summary>立即失败：Transport 异常直接抛出，Kernel 循环终止。</summary>
    FailFast = 0,

    /// <summary>重试：按 MaxRetries + RetryDelay 重试，全部失败后抛出。</summary>
    Retry = 1,

    /// <summary>
    /// 降级到确定性结果：Transport 失败后，用确定性算法产出 fallback result，
    /// 不中断 Kernel 循环（用于 model transport 不可用时的 fail-safe）。
    /// </summary>
    FallbackToDeterministic = 2
}

/// <summary>
/// R28-C WP-D：Kernel 传输选项。控制 Transport 失败时的重试与降级行为。
/// </summary>
public sealed record KernelTransportOptions
{
    /// <summary>失败策略（默认 FailFast）。</summary>
    public TransportFailurePolicy FailurePolicy { get; init; } = TransportFailurePolicy.FailFast;

    /// <summary>最大重试次数（Retry 策略下生效；默认 3）。</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>重试间隔（Retry 策略下生效；默认 100ms）。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// R28-D P0-5：FallbackToDeterministic 策略下，发送失败的结果是否写入本地 outbox 持久化。
    /// 默认 true：失败结果写入 outbox，待 transport 恢复后重放（而非静默丢弃）。
    /// 设为 false 时回退到旧行为（静默丢弃，不推荐）。
    /// </summary>
    public bool EnableResultOutbox { get; init; } = true;

    /// <summary>
    /// R28-D P0-5：本地 outbox 最大积压数量（默认 1024）。
    /// 超过此数量时最早的结果被丢弃并记录诊断（避免内存耗尽）。
    /// </summary>
    public int MaxOutboxBacklog { get; init; } = 1024;

    /// <summary>
    /// R29 WP-B-4：是否启用 Durable Transport（PostgreSQL-backed Channel）。
    /// 默认 <c>false</c>：使用 <see cref="ContextCore.Core.Services.AgentKernel.InProcessTransport"/>（进程内 Channel，开发环境）。
    /// 设为 <c>true</c> 时，<c>AddContextCorePostgresStorage</c> 的 overload 会替换
    /// <see cref="IAgentKernelTransport"/> 绑定为 <c>PostgresDurableTransport</c>，
    /// 让指令/结果跨进程持久化以支持 HA 崩溃恢复。
    /// 开发环境保留 InMemory（默认）以避免不必要的 DB 依赖。
    /// </summary>
    /// <remarks>
    /// 性能预期：durable 路径延迟 ≤ InMemory × 3（参见 R29 spec §6.3）。
    /// 该开关仅影响 transport 实现；checkpoint / journal / outbox 的持久化由各自的
    /// <c>IPersistent*</c> 标记接口独立控制。
    /// </remarks>
    public bool UseDurableTransport { get; init; }

    /// <summary>
    /// 默认选项（FailFast 策略，与 R28-C 之前行为一致）。
    /// </summary>
    public static KernelTransportOptions Default { get; } = new();
}

/// <summary>
/// R28-D P0-5：Kernel 结果 outbox 抽象。
/// FallbackToDeterministic 策略下，Transport 发送失败的结果写入 outbox 持久化，
/// 待 Transport 恢复后由 Kernel 重放（而非静默丢弃）。
/// </summary>
/// <remarks>
/// 默认实现 <c>InMemoryKernelResultOutbox</c> 提供进程内 Channel 缓冲。
/// 生产部署应替换为持久化实现（如基于文件/DB 的 outbox）。
/// </remarks>
public interface IKernelResultOutbox
{
    /// <summary>将发送失败的结果写入 outbox。</summary>
    /// <param name="result">待持久化的结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask EnqueueAsync(AgentKernelResult result, CancellationToken cancellationToken = default);

    /// <summary>从 outbox 读取待重放的结果（按 FIFO 顺序）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>待重放的结果；outbox 为空时返回 null。</returns>
    ValueTask<AgentKernelResult?> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>当前 outbox 中待重放的结果数量。</summary>
    int PendingCount { get; }

    /// <summary>
    /// P2：异步获取 pending 数量（推荐用于热路径）。
    /// 同步属性 <see cref="PendingCount"/> 保留向后兼容，但 Postgres 实现内部走 COUNT(*) 应避免热路径调用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前 outbox 中 state='Pending' 的结果数（不含 Leased）。</returns>
    ValueTask<int> GetPendingCountAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// R28-C：Transport 抽象。负责指令传输。
/// </summary>
/// <remarks>
/// Transport 是 Kernel 与外部世界之间的双向通道：
///   - <see cref="ReceiveAsync"/>：Kernel 从 Transport 接收指令（远程 / 进程外来源）。
///   - <see cref="SendResultAsync"/>：Kernel 通过 Transport 发送执行结果。
/// 默认实现 <c>InProcessTransport</c> 提供进程内 Channel 传输。
///
/// R28-D P0-4：<b>输入链明确化</b>。
/// <see cref="DefaultAgentKernel"/> 默认维护自身 inbox（通过 <see cref="IAgentKernel.SubmitAsync"/> 提交），
/// <b>不</b>调用 <see cref="ReceiveAsync"/>。Transport 的 inbox 仅用于自定义 Kernel 实现从远程接收指令。
/// 调用方若要驱动默认 Kernel，必须使用 <see cref="IAgentKernel.SubmitAsync"/>，
/// 而非 <c>InProcessTransport.SubmitAsync</c>（后者写入 Transport 的 inbox，默认 Kernel 不读取）。
/// 如需远程指令驱动 Kernel，需自定义 Kernel 实现从 Transport.ReceiveAsync 读取。
/// </remarks>
public interface IAgentKernelTransport
{
    /// <summary>
    /// 从 Transport 接收下一条指令（阻塞直到有指令或取消）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>接收到的指令；Transport 关闭时返回 null。</returns>
    /// <remarks>
    /// R28-D P0-4：<see cref="DefaultAgentKernel"/> 默认不调用此方法。
    /// 仅自定义 Kernel 实现用于从远程 Transport 接收指令时调用。
    /// 默认 Kernel 通过 <see cref="IAgentKernel.SubmitAsync"/> 接收指令。
    /// </remarks>
    ValueTask<AgentKernelInstruction?> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>通过 Transport 发送执行结果。</summary>
    /// <param name="result">要发送的结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask SendResultAsync(AgentKernelResult result, CancellationToken cancellationToken = default);
}

/// <summary>
/// R28-C：Tool Dispatcher 抽象。
/// </summary>
/// <remarks>
/// 负责按名称分派 tool 调用并返回结果。Kernel 不直接调用 tool；通过此抽象解耦。
/// 默认实现 <c>EchoToolDispatcher</c> 原样返回 payload（测试用）。
/// </remarks>
public interface IToolDispatcher
{
    /// <summary>当前 Dispatcher 支持的 tool 名称集合。</summary>
    IReadOnlySet<string> SupportedTools { get; }

    /// <summary>分派 tool 调用。</summary>
    /// <param name="request">分派请求（tool 名称 + payload + requestId）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分派结果（成功/失败 + 输出/错误 + 耗时）。</returns>
    ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// R28-C：Tool 分派请求。
/// </summary>
public sealed record ToolDispatchRequest
{
    /// <summary>要调用的 tool 名称（必须在 <see cref="IToolDispatcher.SupportedTools"/> 范围内）。</summary>
    public required string ToolName { get; init; }

    /// <summary>Tool 调用负载（自由文本；语义由 tool 实现约定）。</summary>
    public required string Payload { get; init; }

    /// <summary>请求唯一 ID（用于关联 <see cref="ToolDispatchResult"/>）。</summary>
    public required string RequestId { get; init; }
}

/// <summary>
/// R28-C WP-C：Tool 副作用分类。决定恢复时是否自动重放。
/// </summary>
public enum ToolSideEffect : byte
{
    /// <summary>纯函数，无副作用（如 echo / 只读查询）。恢复时可安全重放。</summary>
    None = 0,

    /// <summary>只读副作用（如日志/trace 写入，不改变业务状态）。恢复时可重放。</summary>
    ReadOnly = 1,

    /// <summary>写副作用（改变业务状态，如写入文件/DB）。恢复时使用缓存结果，不重新执行。</summary>
    Write = 2,

    /// <summary>
    /// 未知副作用。恢复时<b>不自动重放</b>——需调用方显式确认后才提交。
    /// </summary>
    Unknown = 3
}

/// <summary>
/// R28-C：Tool 分派结果。
/// </summary>
public sealed record ToolDispatchResult
{
    /// <summary>是否成功。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Tool 输出（成功时）。</summary>
    public string? Result { get; init; }

    /// <summary>错误信息（失败时）。</summary>
    public string? Error { get; init; }

    /// <summary>Tool 执行耗时。</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// R28-C WP-C：Tool 副作用分类。默认 Unknown（保守策略：未声明的 tool 不自动重放）。
    /// Tool 实现应显式声明副作用类型。EchoToolDispatcher 声明 None。
    /// </summary>
    public ToolSideEffect SideEffect { get; init; } = ToolSideEffect.Unknown;
}

/// <summary>
/// R28-E P1-4：Tool 分派状态机。
/// 实现恰好一次（exactly-once）tool 执行的核心状态。
/// </summary>
/// <remarks>
/// 状态流转（不可逆，只能向前）：
///   <see cref="Prepared"/> → <see cref="Dispatched"/> → <see cref="Committed"/> → <see cref="ResultDelivered"/>
///
/// 恢复语义：
///   - 无 journal 记录 → 安全重新执行（tool 从未被调用）。
///   - <see cref="Prepared"/> 但未 <see cref="Dispatched"/> → 安全重新执行（tool 未真正执行）。
///   - <see cref="Dispatched"/> 但未 <see cref="Committed"/> → <b>模糊状态</b>：tool 可能已成功执行外部副作用，
///     需调用方查询外部系统或人工裁决；不可盲目重新执行。
///   - <see cref="Committed"/> 但未 <see cref="ResultDelivered"/> → 结果已持久化，可安全重发到 transport。
///   - <see cref="ResultDelivered"/> → 完全完成，无需任何动作。
/// </remarks>
public enum ToolDispatchState : byte
{
    /// <summary>已准备（journal 已写入 Prepared 条目，但 tool 尚未真正调用）。</summary>
    Prepared = 0,

    /// <summary>已分派（tool 已调用并返回，或外部调用已发起但结果未确认）。</summary>
    Dispatched = 1,

    /// <summary>已提交（结果已写入 committed store / _committedToolResults）。</summary>
    Committed = 2,

    /// <summary>结果已送达（已通过 Transport.SendResultAsync 成功发送）。</summary>
    ResultDelivered = 3
}

/// <summary>
/// R28-E P1-4：Tool 分派 journal 条目。
/// 持久化记录每个 tool 调用的状态机进度，用于崩溃恢复时判断是否可安全重放。
/// </summary>
public sealed record ToolDispatchJournalEntry
{
    /// <summary>Tool 调用 RequestId（与 InstructionId 对应）。</summary>
    public required string RequestId { get; init; }

    /// <summary>Tool 名称。</summary>
    public required string ToolName { get; init; }

    /// <summary>当前分派状态。</summary>
    public required ToolDispatchState State { get; init; }

    /// <summary>调用方提供的幂等键（可选；用于外部系统去重）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>外部操作 ID（tool 实际执行后返回的外部系统 ID，可用于查询/对账）。</summary>
    public string? ExternalOperationId { get; init; }

    /// <summary>journal 条目更新时间（UTC）。</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>失败/模糊状态原因诊断（如 Dispatched 但未 Committed 时的说明）。</summary>
    public string? DiagnosticNote { get; init; }
}

/// <summary>
/// R28-E P1-4：Tool 分派 journal 抽象。
/// 持久化 <see cref="ToolDispatchJournalEntry"/> 以支持 exactly-once tool 执行。
/// </summary>
/// <remarks>
/// <b>journal 是可选依赖</b>。未注入时 Kernel 退回到旧行为（仅进程内 Dictionary 去重，
/// 不保证崩溃恢复的 exactly-once）。生产部署应注入持久化实现（如基于 DB/WAL 的 journal）。
///
/// Journal 写入顺序（与 <see cref="DefaultAgentKernel"/> 调用点对应）：
///   1. <see cref="PrepareAsync"/>：在调用 <see cref="IToolDispatcher.DispatchAsync"/> 之前。
///   2. <see cref="MarkDispatchedAsync"/>：tool 返回后、提交结果前。
///   3. <see cref="MarkCommittedAsync"/>：结果写入 _committedToolResults 后。
///   4. <see cref="MarkResultDeliveredAsync"/>：Transport.SendResultAsync 成功后。
///
/// <b>P0-3：expected-state CAS 语义</b>。
/// Mark* 方法使用 expected-state CAS 推进状态机，<b>不自动创建 stub 条目</b>：
/// <list type="bullet">
///   <item>若 request_id 不存在（缺失 Prepared 前驱） → 抛 <see cref="InvalidOperationException"/>，
///     而非补造高级状态。这保证审计链完整：不存在 → Committed 这样的跳跃不再可能。</item>
///   <item>若 request_id 存在但 state ≥ target（逆退） → 抛 <see cref="InvalidOperationException"/>。</item>
/// </list>
///
/// <b>外部副作用 exactly-once 边界（P0-3）</b>。
/// Journal 仅保证 ContextCore 内部的"恰好一次编排记录"——同一 request_id 的状态机只向前推进一次。
/// 完整的外部副作用 exactly-once 还需要：
/// <list type="bullet">
///   <item>调用方提供 <see cref="ToolDispatchJournalEntry.IdempotencyKey"/>（持久化实现应有 UNIQUE 约束兜底去重）；</item>
///   <item>Tool provider 支持幂等键 / 外部操作 ID（外部系统侧去重）；</item>
///   <item>崩溃恢复时对 Dispatched 但未 Committed 的模糊状态进行外部对账。</item>
/// </list>
/// 对于不支持幂等键的 Tool，只能声明 at-least-once 或要求人工确认。
/// </remarks>
public interface IToolDispatchJournal
{
    /// <summary>写入 Prepared 条目（在调用 tool 之前）。</summary>
    /// <param name="entry">journal 条目（State 应为 Prepared）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 幂等：重复 Prepare 同一 request_id 不覆盖已推进的状态（ON CONFLICT DO NOTHING / TryAdd 语义）。
    /// 持久化实现要求 <see cref="ToolDispatchJournalEntry.IdempotencyKey"/> 全局唯一（UNIQUE partial index）。
    /// </remarks>
    ValueTask PrepareAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default);

    /// <summary>将指定 RequestId 的状态推进到 Dispatched（tool 已返回结果）。</summary>
    /// <param name="requestId">Tool RequestId（必须已存在 Prepared 条目）。</param>
    /// <param name="externalOperationId">可选的外部操作 ID（tool 返回）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">request_id 不存在（缺失 Prepared 前驱）或当前 state ≥ Dispatched（逆退）。</exception>
    ValueTask MarkDispatchedAsync(string requestId, string? externalOperationId = null, CancellationToken cancellationToken = default);

    /// <summary>将指定 RequestId 的状态推进到 Committed（结果已提交）。</summary>
    /// <param name="requestId">Tool RequestId（必须已存在 Dispatched 条目）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">request_id 不存在（缺失 Dispatched 前驱）或当前 state ≥ Committed（逆退）。</exception>
    ValueTask MarkCommittedAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>将指定 RequestId 的状态推进到 ResultDelivered（结果已送达 transport）。</summary>
    /// <param name="requestId">Tool RequestId（必须已存在 Committed 条目）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">request_id 不存在（缺失 Committed 前驱）或当前 state ≥ ResultDelivered（逆退）。</exception>
    ValueTask MarkResultDeliveredAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>查询指定 RequestId 的当前 journal 状态（用于恢复时判断）。</summary>
    /// <param name="requestId">Tool RequestId。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>journal 条目；不存在时返回 null（表示 tool 从未被调用，可安全重新执行）。</returns>
    ValueTask<ToolDispatchJournalEntry?> GetEntryAsync(string requestId, CancellationToken cancellationToken = default);
}

/// <summary>
/// R29 WP-B-1：持久化 Tool Dispatch Journal 抽象。
/// 继承 <see cref="IToolDispatchJournal"/> 并标记为持久化实现，用于崩溃恢复的 exactly-once 语义。
/// </summary>
/// <remarks>
/// 生产部署应注入基于 DB/WAL 的持久化实现（如 <c>PostgresToolDispatchJournal</c>）。
/// 开发环境可继续使用 <see cref="ContextCore.Core.Services.AgentKernel.InMemoryToolDispatchJournal"/>。
/// 由于继承自 <see cref="IToolDispatchJournal"/>，可直接注入 <see cref="DefaultAgentKernel"/> 的
/// <c>IToolDispatchJournal?</c> 参数，无需修改 Kernel 构造签名。
/// </remarks>
public interface IPersistentToolDispatchJournal : IToolDispatchJournal
{
}

/// <summary>
/// R29 WP-B-2 / P0-2：持久化 Kernel Result Outbox 抽象。
/// 继承 <see cref="IKernelResultOutbox"/> 并标记为持久化实现，用于崩溃恢复的结果投递。
/// </summary>
/// <remarks>
/// 生产部署应注入基于 DB/WAL 的持久化实现（如 <c>PostgresKernelResultOutbox</c>）。
/// 开发环境可继续使用 <see cref="ContextCore.Core.Services.AgentKernel.InMemoryKernelResultOutbox"/>。
/// 由于继承自 <see cref="IKernelResultOutbox"/>，可直接注入 <see cref="DefaultAgentKernel"/> 的
/// <c>IKernelResultOutbox?</c> 参数，无需修改 Kernel 构造签名。
///
/// <b>P0-2：租约模型（crash-recoverable outbox）</b>。
/// 旧版 <see cref="IKernelResultOutbox.DequeueAsync"/> 在 Postgres 实现中将行标记为 <c>Dispatched</c>，
/// 但若消费方在 Dequeue 后、实际投递前崩溃，该行将永久滞留在 <c>Dispatched</c> 状态（无 Ack/Nack/Retry 机制）。
/// P0-2 在持久化 outbox 上扩展租约状态机：
/// <code>
/// Pending → Leased(owner, expires_at, token) → Acked(DELETE)
///                ↓ (lease expires)
///          RequeueExpired → Pending
/// </code>
/// 生产消费者应使用 <see cref="LeaseAsync"/> + <see cref="AckAsync"/> 显式管理租约生命周期；
/// 遗留 <see cref="IKernelResultOutbox.DequeueAsync"/> 内部调用 <see cref="LeaseAsync"/>（默认租约）并丢弃 token，
/// 仅供单实例/测试场景使用，不 Ack 的行将在租约过期后由 <see cref="RequeueExpiredAsync"/> 回滚为 Pending。
/// </remarks>
public interface IPersistentKernelResultOutbox : IKernelResultOutbox
{
    /// <summary>租约一条 Pending 结果（Pending → Leased），返回结果与租约 token。</summary>
    /// <param name="leaseDuration">租约有效期；过期后由 <see cref="RequeueExpiredAsync"/> 回滚为 Pending。</param>
    /// <param name="owner">可选租约持有者标识（用于诊断，如实例 ID）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>租约到的结果与 token；无 Pending 行时返回 null。</returns>
    /// <remarks>
    /// 使用 <c>FOR UPDATE SKIP LOCKED</c> 支持多 worker 并发；返回的行标记为 Leased，<b>不删除</b>。
    /// 调用方必须在处理完成后调用 <see cref="AckAsync"/> 确认；否则租约过期后由 <see cref="RequeueExpiredAsync"/> 回滚。
    /// </remarks>
    ValueTask<LeasedOutboxResult?> LeaseAsync(TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default);

    /// <summary>确认结果已成功投递（Leased → DELETE）。</summary>
    /// <param name="outboxId">租约返回的 outbox ID。</param>
    /// <param name="leaseToken">租约返回的 token。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">token 不匹配、租约已过期回滚或已确认（0 行受影响）。</exception>
    ValueTask AckAsync(string outboxId, string leaseToken, CancellationToken cancellationToken = default);

    /// <summary>否定确认：立即回滚为 Pending（Leased → Pending），让其他 worker 可重新租约。</summary>
    /// <param name="outboxId">租约返回的 outbox ID。</param>
    /// <param name="leaseToken">租约返回的 token。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">token 不匹配、租约已过期回滚或已确认（0 行受影响）。</exception>
    ValueTask NackAsync(string outboxId, string leaseToken, CancellationToken cancellationToken = default);

    /// <summary>续租：延长 lease_expires_at（需 token 匹配且仍为 Leased）。</summary>
    /// <param name="outboxId">租约返回的 outbox ID。</param>
    /// <param name="leaseToken">租约返回的 token。</param>
    /// <param name="extension">续租时长（从当前 UTC 时间起计算新的 expires_at = now + extension）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">token 不匹配、租约已过期回滚或已确认（0 行受影响）。</exception>
    ValueTask RenewLeaseAsync(string outboxId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken = default);

    /// <summary>扫描所有 state='Leased' AND lease_expires_at &lt; now 的行，回滚为 Pending（崩溃 worker 持有的租约最终释放）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>回滚的行数。</returns>
    ValueTask<int> RequeueExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// P0-2：持久化 Kernel Result Outbox 租约结果。
/// </summary>
/// <remarks>
/// 由 <see cref="IPersistentKernelResultOutbox.LeaseAsync"/> 返回，包含结果本体、outbox ID 与租约信息。
/// 调用方处理完成后需使用 <see cref="LeasedOutboxResult.LeaseToken"/> 调用
/// <see cref="IPersistentKernelResultOutbox.AckAsync"/> 确认。
/// </remarks>
public sealed record LeasedOutboxResult
{
    /// <summary>Outbox 行 ID（GUID），用于 Ack/Nack/Renew 定位。</summary>
    public required string OutboxId { get; init; }

    /// <summary>租约到的结果本体。</summary>
    public required AgentKernelResult Result { get; init; }

    /// <summary>租约 token（Ack/Nack/Renew 需匹配此 token）。</summary>
    public required string LeaseToken { get; init; }

    /// <summary>租约过期时间（UTC）。</summary>
    public required DateTimeOffset LeaseExpiresAt { get; init; }
}

/// <summary>
/// R29 WP-B-3：持久化 Agent Checkpoint Store 抽象。
/// 继承 <see cref="IAgentCheckpointStore"/> 并标记为持久化实现，用于崩溃恢复的 checkpoint 链。
/// </summary>
/// <remarks>
/// 生产部署应注入基于 DB/WAL 的持久化实现（如 <c>PostgresAgentCheckpointStore</c>）。
/// 开发环境可继续使用 <see cref="ContextCore.Core.Services.Agent.InMemoryAgentCheckpointStore"/>。
/// 由于继承自 <see cref="IAgentCheckpointStore"/>，可直接注入 <see cref="DefaultAgentKernel"/> 的
/// <c>IAgentCheckpointStore</c> 参数，无需修改 Kernel 构造签名。
///
/// <b>R28-G P1-5 delta 链路复用</b>：delta checkpoint 机制完全封装在 <c>StateJson</c> 负载
/// （<c>KernelCheckpointStateDto</c> 的 <c>Mode</c>/<c>BaseCheckpointId</c>/<c>LastSequence</c> 字段），
/// 由 <see cref="DefaultAgentKernel.ResumeAsync"/> 通过标准 <see cref="GetAsync"/> 递归走链。
/// Store 本身不需要感知 delta 语义 — 只需持久化完整 <c>AgentCheckpoint</c> blob。
/// </remarks>
public interface IPersistentAgentCheckpointStore : IAgentCheckpointStore
{
}

/// <summary>
/// R29 WP-B-4：Durable Transport 抽象。继承 <see cref="IAgentKernelTransport"/> 并标记为持久化实现，
/// 用于跨进程 / 跨实例的指令与结果传输（PostgreSQL-backed Channel）。
/// </summary>
/// <remarks>
/// 默认实现 <c>InProcessTransport</c>（Core 层）使用进程内 <c>System.Threading.Channels.Channel&lt;T&gt;</c>，
/// 仅适用于单进程部署。<c>PostgresDurableTransport</c>（Storage.Postgres 层）将 inbox / outbox 持久化到
/// PostgreSQL 表，让多个 Kernel 实例可共享同一 transport，支持 HA 崩溃恢复。
///
/// 通过 <see cref="KernelTransportOptions.UseDurableTransport"/> 开关启用：
/// 开发环境保留 <c>InMemory</c>（默认）；生产环境通过 <c>AddContextCorePostgresStorage</c> overload
/// 传入 <c>KernelTransportOptions { UseDurableTransport = true }</c> 或调用
/// <c>UsePostgresDurableTransport()</c> 扩展方法显式启用。
///
/// 由于继承自 <see cref="IAgentKernelTransport"/>，可直接注入 <see cref="DefaultAgentKernel"/> 的
/// <c>IAgentKernelTransport</c> 参数，无需修改 Kernel 构造签名。
///
/// <b>P0-1：租约模型（crash-recoverable durable transport）</b>。
/// 旧版 <see cref="IAgentKernelTransport.ReceiveAsync"/> 在 Postgres 实现中使用破坏性 DELETE 出队，
/// 崩溃窗口（DELETE 成功 → Kernel 未处理 → 进程崩溃）会导致指令永久丢失。
/// P0-1 改为租约模型：
/// <code>
/// Pending → Leased(owner, expires_at) → Acked(DELETE)
///                ↓ (lease expires)
///          RequeueExpired → Pending
/// </code>
/// 生产消费者应使用 <see cref="LeaseAsync"/> + <see cref="AckAsync"/> 显式管理租约生命周期，
/// 而非依赖遗留的 <see cref="IAgentKernelTransport.ReceiveAsync"/>（后者内部改为 lease + 内部跟踪 token，
/// 调用方仍需调用 <see cref="AckAsync"/> 确认；未确认的行在租约过期后由 <see cref="RequeueExpiredAsync"/> 回滚）。
/// </remarks>
public interface IDurableTransport : IAgentKernelTransport
{
    /// <summary>
    /// P0-1：从 inbox 租约下一条 Pending 指令（原子 CAS：Pending → Leased）。
    /// 使用 <c>FOR UPDATE SKIP LOCKED</c> 支持多 worker 并发；返回的行标记为 Leased，<b>不删除</b>。
    /// 调用方必须在处理完成后调用 <see cref="AckAsync"/> 确认；否则租约过期后由 <see cref="RequeueExpiredAsync"/> 回滚。
    /// </summary>
    /// <param name="leaseDuration">租约有效期（从当前 UTC 时间开始计算）。</param>
    /// <param name="owner">可选的租约持有者标识（如 worker ID），用于诊断。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>租约的指令 + lease token；inbox 为空（无 Pending 行）时返回 null。</returns>
    ValueTask<LeasedInstruction?> LeaseAsync(TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// P1：批量租约多条 Pending 指令（原子 CAS：Pending → Leased）。
    /// 生产高并发下减少网络往返；调用方处理完成后逐条 <see cref="AckAsync"/> 或批量 <see cref="AckBatchAsync"/>。
    /// </summary>
    /// <param name="limit">单次批量租约的最大条数（必须 &gt; 0）。</param>
    /// <param name="leaseDuration">租约有效期（从当前 UTC 时间开始计算）。</param>
    /// <param name="owner">可选的租约持有者标识（如 worker ID），用于诊断。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>租约到的指令列表（按 FIFO 顺序）；inbox 为空时返回空列表。</returns>
    ValueTask<IReadOnlyList<LeasedInstruction>> LeaseBatchAsync(int limit, TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// P0-1：确认指令已处理完成（Leased → Acked → DELETE）。
    /// 必须提供正确的 <paramref name="leaseToken"/>；token 不匹配时抛 <see cref="InvalidOperationException"/>（租约被其他 worker 接管或已过期回滚）。
    /// </summary>
    /// <param name="instructionId">指令 ID。</param>
    /// <param name="leaseToken">租约 token（来自 <see cref="LeaseAsync"/> 返回值）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask AckAsync(string instructionId, string leaseToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// P1：批量确认（逐条校验 lease token，部分成功不抛异常，返回失败的 instruction_id 列表）。
    /// 用于配合 <see cref="LeaseBatchAsync"/> 批量消费后批量确认；失败的 ack（token 不匹配、租约已过期回滚或已确认）
    /// 不抛异常，调用方可根据返回的失败列表决定重试或丢弃。
    /// </summary>
    /// <param name="acks">待确认的 (InstructionId, LeaseToken) 元组列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>失败的 instruction_id 列表（token 不匹配或状态非 Leased）；全部成功时为空列表。</returns>
    ValueTask<IReadOnlyList<string>> AckBatchAsync(IReadOnlyList<(string InstructionId, string LeaseToken)> acks, CancellationToken cancellationToken = default);

    /// <summary>
    /// P0-1：拒绝指令（Leased → Pending），立即将行回滚为 Pending 供其他 worker 重新租约。
    /// 必须提供正确的 <paramref name="leaseToken"/>；token 不匹配时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <param name="instructionId">指令 ID。</param>
    /// <param name="leaseToken">租约 token。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask NackAsync(string instructionId, string leaseToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// P0-1：续租约（延长 lease_expires_at）。适用于长耗时处理需要更多时间。
    /// 必须提供正确的 <paramref name="leaseToken"/>；token 不匹配时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <param name="instructionId">指令 ID。</param>
    /// <param name="leaseToken">租约 token。</param>
    /// <param name="extension">延长的时间量（从当前 UTC 时间开始计算新的 expires_at）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RenewLeaseAsync(string instructionId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken = default);

    /// <summary>
    /// P0-1：扫描并回滚所有过期的 Leased 行（lease_expires_at &lt; now → state = Pending）。
    /// 应由后台定时任务或新实例启动时调用，确保崩溃 worker 持有的租约最终被释放。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>回滚的行数。</returns>
    ValueTask<int> RequeueExpiredAsync(CancellationToken cancellationToken = default);

    // ── Outbox（结果）租约模型 ──────────────────────────────────────────

    /// <summary>
    /// P0-1：从 outbox 租约下一条 Pending 结果（原子 CAS：Pending → Leased）。
    /// 与 <see cref="LeaseAsync"/> 对称，用于结果消费方管理租约生命周期。
    /// </summary>
    ValueTask<LeasedResult?> LeaseResultAsync(TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// P1：批量租约多条 Pending 结果（与 <see cref="LeaseBatchAsync"/> 对称）。
    /// 生产高并发下减少网络往返；调用方处理完成后逐条 <see cref="AckResultAsync"/> 或批量 <see cref="AckResultBatchAsync"/>。
    /// </summary>
    /// <param name="limit">单次批量租约的最大条数（必须 &gt; 0）。</param>
    /// <param name="leaseDuration">租约有效期（从当前 UTC 时间开始计算）。</param>
    /// <param name="owner">可选的租约持有者标识（如 worker ID），用于诊断。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>租约到的结果列表（按 FIFO 顺序）；outbox 为空时返回空列表。</returns>
    ValueTask<IReadOnlyList<LeasedResult>> LeaseResultBatchAsync(int limit, TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default);

    /// <summary>P0-1：确认结果已处理完成（Leased → Acked → DELETE）。</summary>
    ValueTask AckResultAsync(string resultId, string leaseToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// P1：批量确认结果（与 <see cref="AckBatchAsync"/> 对称，返回失败的 result_id 列表）。
    /// 用于配合 <see cref="LeaseResultBatchAsync"/> 批量消费后批量确认。
    /// </summary>
    /// <param name="acks">待确认的 (ResultId, LeaseToken) 元组列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>失败的 result_id 列表（token 不匹配或状态非 Leased）；全部成功时为空列表。</returns>
    ValueTask<IReadOnlyList<string>> AckResultBatchAsync(IReadOnlyList<(string ResultId, string LeaseToken)> acks, CancellationToken cancellationToken = default);

    /// <summary>P0-1：拒绝结果（Leased → Pending），立即回滚为 Pending 供重新租约。</summary>
    ValueTask NackResultAsync(string resultId, string leaseToken, CancellationToken cancellationToken = default);

    /// <summary>P0-1：续结果租约（延长 lease_expires_at）。</summary>
    ValueTask RenewResultLeaseAsync(string resultId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken = default);

    /// <summary>
    /// P2：异步获取 inbox 中 Pending 指令数（推荐用于热路径）。
    /// 同步属性 <c>PendingInstructionCount</c>（具体实现上）保留向后兼容，但 Postgres 实现内部走 COUNT(*) 应避免热路径调用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前 inbox 中 state='Pending' 的指令数（不含 Leased）。</returns>
    ValueTask<int> GetPendingInstructionCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// P2：异步获取 outbox 中 Pending 结果数（推荐用于热路径）。
    /// 同步属性 <c>PendingResultCount</c>（具体实现上）保留向后兼容，但 Postgres 实现内部走 COUNT(*) 应避免热路径调用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前 outbox 中 state='Pending' 的结果数（不含 Leased）。</returns>
    ValueTask<int> GetPendingResultCountAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// P0-1：租约的指令（LeaseAsync 返回值）。
/// 包含指令本体 + lease token（用于后续 Ack/Nack/Renew）+ 过期时间。
/// </summary>
public sealed record LeasedInstruction
{
    /// <summary>租约的指令。</summary>
    public required AgentKernelInstruction Instruction { get; init; }

    /// <summary>租约 token（调用 Ack/Nack/Renew 时必须提供）。</summary>
    public required string LeaseToken { get; init; }

    /// <summary>租约过期时间（UTC）。超过此时间未 Ack 的行将被 RequeueExpiredAsync 回滚。</summary>
    public required DateTimeOffset LeaseExpiresAt { get; init; }
}

/// <summary>
/// P0-1：租约的结果（LeaseResultAsync 返回值）。
/// 包含结果本体 + result_id + lease token + 过期时间。
/// </summary>
public sealed record LeasedResult
{
    /// <summary>租约的结果。</summary>
    public required AgentKernelResult Result { get; init; }

    /// <summary>结果行 ID（outbox 主键，用于 Ack/Nack/Renew）。</summary>
    public required string ResultId { get; init; }

    /// <summary>租约 token。</summary>
    public required string LeaseToken { get; init; }

    /// <summary>租约过期时间（UTC）。</summary>
    public required DateTimeOffset LeaseExpiresAt { get; init; }
}

/// <summary>
/// P0-4：Durable Transport lease 元数据键约定。
/// <see cref="ContextCore.Service.Hosting.DurableTransportInstructionPumpService"/>（指令 pump）租约指令后，
/// 将 lease token 写入 <see cref="AgentKernelInstruction.Metadata"/>，使
/// <see cref="DefaultAgentKernel"/> 在处理完成后能调用 <see cref="IDurableTransport.AckAsync"/> 确认。
/// </summary>
/// <remarks>
/// 键名使用 <c>durable-*</c> 前缀避免与业务元数据冲突。
/// Kernel 读取这些键是可选的——若 Metadata 中无此键，Kernel 不执行 Ack（兼容 InProcessTransport 路径）。
/// </remarks>
public static class DurableTransportMetadataKeys
{
    /// <summary>租约 token（Ack/Nack 时必须提供）。值为 lease token 字符串。</summary>
    public const string LeaseToken = "durable-lease-token";

    /// <summary>租约持有者标识（诊断用，如 pump 实例 ID）。值可能为空字符串。</summary>
    public const string LeaseOwner = "durable-lease-owner";
}

/// <summary>
/// R28-E P1-1：Agent Checkpoint 工厂抽象。
/// 统一手动 Checkpoint 指令与自动 AutoCheckpoint 的状态格式，
/// 确保两者都序列化完整的 Kernel 状态（已提交 tool 结果 + snapshot 引用）。
/// </summary>
/// <remarks>
/// <b>引入背景</b>：手动 Checkpoint 曾直接使用 instruction.Payload 作为 StateJson，
/// 导致 ResumeAsync 无法恢复已提交的 tool 结果。引入工厂后所有 checkpoint 入口
/// 都产出同一 KernelCheckpointState 格式，Resume 可靠恢复幂等状态。
/// </remarks>
public interface IAgentCheckpointFactory
{
    /// <summary>从当前 Kernel 状态构建 checkpoint。</summary>
    /// <param name="checkpointId">Checkpoint 唯一 ID。</param>
    /// <param name="sessionId">当前 session。</param>
    /// <param name="workspaceId">当前 workspace。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>包含完整 Kernel 状态的 AgentCheckpoint。</returns>
    /// <remarks>
    /// 实现应序列化 KernelCheckpointState（含 CommittedResults + SnapshotId）到 <see cref="AgentCheckpoint.StateJson"/>，
    /// 并设置 <see cref="AgentCheckpoint.SnapshotId"/>（若存在 _lastSnapshot）。
    /// </remarks>
    ValueTask<AgentCheckpoint> CreateCheckpointAsync(
        string checkpointId,
        string sessionId,
        string workspaceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R28-E P1-2：Agent Context Snapshot Store 抽象。
/// 按 SnapshotId 加载 <see cref="AgentContextSnapshot"/>，供 ResumeAsync 恢复 _lastSnapshot。
/// </summary>
/// <remarks>
/// <b>可选依赖</b>：未注入时 ResumeAsync 只恢复已提交 tool 结果，不恢复 snapshot。
/// 生产部署应注入持久化实现（如基于 DB 的 snapshot store）。
/// </remarks>
public interface IAgentContextSnapshotStore
{
    /// <summary>按 workspace + snapshotId 加载 snapshot。</summary>
    /// <param name="workspaceId">workspace 作用域（保证跨 workspace 隔离）。</param>
    /// <param name="snapshotId">Snapshot 唯一 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Snapshot；不存在或跨 workspace 不可见时返回 null。</returns>
    ValueTask<AgentContextSnapshot?> GetAsync(
        string workspaceId,
        string snapshotId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R28-C：Kernel 执行结果。
/// </summary>
/// <remarks>
/// 由 Kernel 处理完指令后通过 <see cref="IAgentKernelTransport.SendResultAsync"/> 发出。
/// InstructionId 与 <see cref="AgentKernelInstruction.InstructionId"/> 对应。
/// </remarks>
public sealed record AgentKernelResult
{
    /// <summary>关联的指令 ID。</summary>
    public required string InstructionId { get; init; }

    /// <summary>是否成功。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>执行输出（成功时）。</summary>
    public string? Output { get; init; }

    /// <summary>错误信息（失败时）。</summary>
    public string? Error { get; init; }

    /// <summary>
    /// R28-C WP-A：BuildContext 指令产出的 AgentContextSnapshot。
    /// 非 BuildContext 指令时为 null。
    /// </summary>
    public AgentContextSnapshot? Snapshot { get; init; }

    /// <summary>
    /// R28-C WP-B：Checkpoint 指令关联的 SnapshotId（若 BuildContext 在前已产出 snapshot）。
    /// 用于恢复时定位上次 context 快照。
    /// </summary>
    public string? LastSnapshotId { get; init; }

    /// <summary>
    /// R28-E P1-3：QueryToolDispatchState 指令产出的 tool 分派状态。
    /// 非 QueryToolDispatchState 指令时为 null。
    /// </summary>
    public ToolDispatchState? DispatchState { get; init; }

    /// <summary>
    /// R28-E P1-3：AcknowledgeToolResult/RejectToolResult 指令影响的 RequestId。
    /// 用于调用方关联被确认/拒绝的 tool 结果。
    /// </summary>
    public string? AffectedRequestId { get; init; }
}
