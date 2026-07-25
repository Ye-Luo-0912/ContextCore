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
/// </remarks>
public interface IToolDispatchJournal
{
    /// <summary>写入 Prepared 条目（在调用 tool 之前）。</summary>
    /// <param name="entry">journal 条目（State 应为 Prepared）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask PrepareAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default);

    /// <summary>将指定 RequestId 的状态推进到 Dispatched（tool 已返回结果）。</summary>
    /// <param name="requestId">Tool RequestId。</param>
    /// <param name="externalOperationId">可选的外部操作 ID（tool 返回）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask MarkDispatchedAsync(string requestId, string? externalOperationId = null, CancellationToken cancellationToken = default);

    /// <summary>将指定 RequestId 的状态推进到 Committed（结果已提交）。</summary>
    /// <param name="requestId">Tool RequestId。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask MarkCommittedAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>将指定 RequestId 的状态推进到 ResultDelivered（结果已送达 transport）。</summary>
    /// <param name="requestId">Tool RequestId。</param>
    /// <param name="cancellationToken">取消令牌。</param>
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
