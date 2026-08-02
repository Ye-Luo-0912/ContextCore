using System.Security.Cryptography;
using System.Text;

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

    /// <summary>
    /// 当前 inbox 中待处理指令数——<b>本进程 channel 计数</b>，仅反映 <see cref="IAgentKernel.SubmitAsync"/>
    /// 写入但尚未被 <see cref="IAgentKernel.RunAsync"/> 处理的指令，<b>不是</b>全局 Transport 视图。
    /// </summary>
    /// <remarks>
    /// 对 <see cref="ContextCore.Core.Services.AgentKernel.DefaultAgentKernel"/>，此值为进程内 bounded Channel 的 Reader.Count；
    /// 不反映远程 Transport（如 <see cref="IDurableTransport"/>）中持久化的 backlog。
    /// <b>不可用于调度或安全判断</b>（如 shutdown、限流、拒绝请求）；生产指标应使用 DurableTransport.GetPendingInstructionCountAsync
    /// 或后台聚合服务导出的 global_pending_count。
    /// </remarks>
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
    /// P0-6-5：Durable Transport 指令租约自动续租间隔（默认 1 分钟）。
    /// 仅在 <see cref="UseDurableTransport"/> = true 时生效。Kernel 处理指令期间启动后台 Task
    /// 按此间隔调用 <see cref="IDurableTransport.RenewLeaseAsync"/>，避免长耗时处理在 lease
    /// 过期前被 reaper 回滚导致重复执行。
    /// </summary>
    /// <remarks>
    /// 默认 1 分钟对应 <see cref="DurableTransportHostingOptions.InstructionLeaseDuration"/> 默认 5 分钟的 1/3。
    /// 设为 <see cref="Timeout.InfiniteTimeSpan"/> 或 ≤ 0 时禁用续租（仅靠 lease 时长覆盖，不推荐长耗时场景）。
    /// DI 注册时应与 <see cref="DurableTransportHostingOptions.LeaseRenewalInterval"/> 同步。
    /// </remarks>
    public TimeSpan DurableLeaseRenewalInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// P0-6-5：Durable Transport 单条指令最大处理时长（默认 10 分钟）。
    /// 超过此时长未完成的指令视为永久故障，outcome 标记为
    /// <see cref="InstructionProcessingOutcome.PermanentFault"/>，
    /// 结果 Metadata 标记 <see cref="DurableDeliveryStatus.PermanentFault"/>，指令 Ack 删除进入死信对账。
    /// </summary>
    /// <remarks>
    /// 此上限防止僵尸指令无限续租占用 lease。应大于预期最长处理时长，但小于人工介入阈值。
    /// DI 注册时应与 <see cref="DurableTransportHostingOptions.MaxProcessingTime"/> 同步。
    /// </remarks>
    public TimeSpan DurableMaxProcessingTime { get; init; } = TimeSpan.FromMinutes(10);

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

    /// <summary>
    /// 当前 outbox 中待重放的结果数量。
    /// </summary>
    /// <remarks>
    /// <b>语义因实现而异</b>：
    /// <list type="bullet">
    ///   <item><see cref="ContextCore.Core.Services.AgentKernel.InMemoryKernelResultOutbox"/>：返回进程内 Channel 的 Reader.Count（<b>本进程计数</b>，单实例语义）。</item>
    ///   <item><c>PostgresKernelResultOutbox</c>：同步执行 DB COUNT(*) 查询，返回<b>全局精确值</b>（跨实例）；同步阻塞调用线程，<b>避免热路径</b>。</item>
    /// </list>
    /// <b>不可用于调度或安全判断</b>；推荐使用 <see cref="GetPendingCountAsync"/> 或后台聚合服务导出的 global_pending_count 指标。
    /// </remarks>
    int PendingCount { get; }

    /// <summary>
    /// P2：异步获取 pending 数量（推荐用于热路径）。
    /// 同步属性 <see cref="PendingCount"/> 保留向后兼容，但 Postgres 实现内部走 COUNT(*) 同步阻塞，应避免热路径调用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前 outbox 中 state='Pending' 的结果数（不含 Leased）；持久化实现返回<b>DB 精确值（全局，跨实例）</b>。</returns>
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

    /// <summary>
    /// P0-3：调用方提供的幂等键（可选；用于外部系统去重）。
    /// 透传到 Tool provider 与 Journal，让外部系统侧也能基于此键去重，
    /// 配合 <see cref="IToolDispatchJournal"/> 的 UNIQUE 约束兜底实现外部副作用 exactly-once。
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// P0-4：workspace 作用域标识（可选）。
    /// 由调用方（<see cref="ContextCore.Core.Services.AgentRunRuntime.DefaultDurableToolExecutor"/>）填充，
    /// 透传到 <see cref="ToolExecutionContext.WorkspaceId"/> 供 Tool Handler 做作用域校验/审计。
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// P0-4：Agent Run 作用域标识（可选）。
    /// 透传到 <see cref="ToolExecutionContext.RunId"/> 供 Tool Handler 关联 Run 上下文。
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// P0-4：本次 Tool 调用的截止时间（UTC，可选）。
    /// 透传到 <see cref="ToolExecutionContext.DeadlineAt"/>；Tool Handler 应在此时间前完成调用。
    /// </summary>
    public DateTimeOffset? DeadlineAt { get; init; }

    /// <summary>
    /// P0-4：租约围栏（可选）。
    /// 携带 lease token + fencing token，让副作用 Tool Handler 校验调用方仍持有有效租约。
    /// null = 无 lease 路径（测试 / 非 Actor 调用）。
    /// </summary>
    public AgentLeaseFence? LeaseFence { get; init; }
}

/// <summary>
/// P0-4：Tool 执行上下文。
/// 由 <see cref="IToolDispatcher"/> 从 <see cref="ToolDispatchRequest"/> 构造并传递给
/// <see cref="ContextCore.Core.Services.AgentKernel.IToolHandler.HandleAsync"/>，
/// 让 Tool Handler 能访问 WorkspaceId/RunId/RequestId/IdempotencyKey/Payload/Deadline/LeaseFence，
/// 而非仅收到裸 JSON Payload。
/// </summary>
/// <remarks>
/// <b>引入背景</b>：旧 <see cref="IToolHandler"/> 签名仅接收 <c>string payload</c>，
/// Handler 无法获知调用作用域（Workspace/Run）、幂等键、截止时间或租约围栏，
/// 导致副作用 Tool 无法做作用域隔离、去重与 fencing 校验。
/// 本 record 统一携带执行上下文，由 Dispatcher 在分派时构造。
/// </remarks>
public sealed record ToolExecutionContext
{
    /// <summary>workspace 作用域标识（用于作用域隔离/审计；空字符串表示未提供）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Agent Run 作用域标识（用于关联 Run 上下文；空字符串表示未提供）。</summary>
    public required string RunId { get; init; }

    /// <summary>请求唯一 ID（与 <see cref="ToolDispatchRequest.RequestId"/> / Journal 对应）。</summary>
    public required string RequestId { get; init; }

    /// <summary>调用方提供的幂等键（可选；用于外部系统去重）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Tool 调用负载（自由文本；语义由 Tool 实现约定）。</summary>
    public required string Payload { get; init; }

    /// <summary>本次 Tool 调用的截止时间（UTC，可选）；Tool Handler 应在此时间前完成调用。</summary>
    public DateTimeOffset? DeadlineAt { get; init; }

    /// <summary>
    /// 租约围栏（可选）。携带 lease token + fencing token，让副作用 Tool Handler 校验调用方仍持有有效租约。
    /// null = 无 lease 路径（测试 / 非 Actor 调用）。
    /// </summary>
    public AgentLeaseFence? LeaseFence { get; init; }
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
    Unknown = 3,

    /// <summary>
    /// P0-4：幂等写（如带 IdempotencyKey 的 API 调用）。可安全重放，但需 lease fence 保护。
    /// 恢复时：若有缓存结果则使用，否则重放（依赖外部幂等性）。
    /// </summary>
    IdempotentWrite = 4,

    /// <summary>
    /// P0-4：受 Fence 保护的写（如数据库事务 + fencing token 校验）。
    /// 恢复时：必须有有效 lease fence，否则 fail-closed。
    /// </summary>
    FencedWrite = 5,

    /// <summary>
    /// P0-4：非幂等写（如发送邮件 / 扣款）。无外部幂等或 fencing 支持时必须 fail-closed。
    /// 恢复时：使用缓存结果，绝不重放；无缓存结果时需 RequiresReconciliation 对账。
    /// </summary>
    NonIdempotentWrite = 6,

    /// <summary>
    /// P0-4：需对账（如外部系统状态不确定）。恢复时不自动重放，需人工或外部对账流程确认。
    /// </summary>
    RequiresReconciliation = 7
}

/// <summary>
/// P0-2: Tool recovery strategy. Determines how to handle Tool calls in Prepared/DispatchingIntent state during crash recovery.
/// </summary>
public enum ToolRecoveryStrategy : byte
{
    /// <summary>Safe replay: tool is side-effect-free or idempotent, can be re-executed on recovery.</summary>
    SafeReplay = 0,
    /// <summary>Use cached result: do not re-execute on recovery, use persisted result (requires reconciliation if missing).</summary>
    UseCachedResult = 1,
    /// <summary>Never replay: never re-execute, must use cached result or enter reconciliation.</summary>
    NeverReplay = 2,
    /// <summary>Force reconciliation: recovery requires manual or external reconciliation, no automatic handling.</summary>
    RequireReconciliation = 3
}

/// <summary>
/// P0-2: Tool static descriptor. Declares side-effect properties, approval requirements, recovery strategy, etc.
/// Executor reads this descriptor BEFORE dispatch to decide pre-execution policy;
/// the SideEffect in execution result is only used to VERIFY actual behavior matches declaration.
/// </summary>
public sealed record ToolDescriptor
{
    /// <summary>Tool name (matches IToolHandler.ToolName).</summary>
    public required string Name { get; init; }
    /// <summary>Declared side-effect type. Executor uses this to decide recovery policy and auto-commit.</summary>
    public ToolSideEffect DeclaredSideEffect { get; init; } = ToolSideEffect.Unknown;
    /// <summary>Whether human approval is required. When true, executor must go through IAgentApprovalGate before dispatch.</summary>
    public bool RequiresApproval { get; init; } = false;
    /// <summary>Whether idempotency key is required. When true, executor must generate or validate IdempotencyKey before dispatch.</summary>
    public bool RequiresIdempotencyKey { get; init; } = false;
    /// <summary>Whether lease fence is required. When true, executor must validate a valid LeaseFence before dispatch.</summary>
    public bool RequiresLeaseFence { get; init; } = false;
    /// <summary>Recovery strategy. How to handle unfinished Tool calls during crash recovery.</summary>
    public ToolRecoveryStrategy RecoveryStrategy { get; init; } = ToolRecoveryStrategy.RequireReconciliation;
    /// <summary>Reconciliation handler name (optional). When RecoveryStrategy=RequireReconciliation, an externally registered handler performs reconciliation.</summary>
    public string? ReconciliationHandler { get; init; }
    /// <summary>Maximum execution time. Tool calls exceeding this are treated as timed out (RequiresReconciliation).</summary>
    public TimeSpan MaximumExecutionTime { get; init; } = TimeSpan.FromMinutes(5);
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

    /// <summary>
    /// 子问题 5：外部操作 ID（tool 实际执行后返回的外部系统 ID，可用于查询/对账）。
    /// 默认 null；Tool 实现可选填充（如外部系统的 transaction ID / job ID）。
    /// 由 <see cref="IDurableToolExecutor"/> 透传到 journal 与 <see cref="ToolExecutionResult"/>。
    /// </summary>
    public string? ExternalOperationId { get; init; }
}

/// <summary>
/// R28-E P1-4：Tool 分派状态机。
/// 实现恰好一次（exactly-once）tool 执行的核心状态。
/// </summary>
/// <remarks>
/// 状态流转（不可逆，只能向前）：
///   <see cref="Prepared"/> → <see cref="DispatchingIntent"/> → <see cref="Dispatched"/> → <see cref="Committed"/> → <see cref="ResultDelivered"/>
///
/// <b>P0-1：DispatchingIntent 外部副作用边界</b>。
/// <see cref="DispatchingIntent"/> 在外部 Tool 调用发起<b>前</b>持久化，创建一个 durable 边界：
/// 若进程在此之后崩溃，恢复时知道外部调用可能已开始。
///
/// 恢复语义：
///   - 无 journal 记录 → 安全重新执行（tool 从未被调用）。
///   - <see cref="Prepared"/>（无 DispatchingIntent）→ 安全重新执行（tool 未真正调用）。
///   - <see cref="DispatchingIntent"/> → <b>模糊状态</b>：外部调用可能已开始但未完成，
///     需按 <see cref="ToolDescriptor.RecoveryStrategy"/> 决定（SafeReplay 重放 / UseCachedResult 查缓存 / 其他对账）。
///   - <see cref="Dispatched"/> 但未 <see cref="Committed"/> → <b>模糊状态</b>：tool 可能已成功执行外部副作用，
///     需调用方查询外部系统或人工裁决；不可盲目重新执行。
///   - <see cref="Committed"/> 但未 <see cref="ResultDelivered"/> → 结果已持久化，可安全重发到 transport。
///   - <see cref="ResultDelivered"/> → 完全完成，无需任何动作。
///
/// <b>注意</b>：<see cref="DispatchingIntent"/> 使用数值 4（而非 1），
/// 以避免破坏数据库中已有的 Dispatched=1 / Committed=2 / ResultDelivered=3 的 byte 映射。
/// 状态机的"前向推进"判断基于逻辑顺序（Prepared→DispatchingIntent→Dispatched→Committed→ResultDelivered），
/// 而非数值大小。
/// </remarks>
public enum ToolDispatchState : byte
{
    /// <summary>已准备（journal 已写入 Prepared 条目，但 tool 尚未真正调用）。</summary>
    Prepared = 0,

    /// <summary>
    /// P0-1：分派意图已持久化（外部调用即将开始但尚未返回）。
    /// 在调用 <see cref="IToolDispatcher.DispatchAsync"/> 之前由
    /// <see cref="IToolDispatchJournal.MarkDispatchingIntentAsync"/> 写入，
    /// 创建外部副作用 exactly-once 的 durable 边界。
    /// </summary>
    DispatchingIntent = 4,

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

    /// <summary>
    /// P0-3 CAS-2：tool 调用 payload 的 SHA-256 摘要（小写 hex）。
    /// 用于 <see cref="IToolDispatchJournal.PrepareAsync"/> 在同一 RequestId 已存在时验证语义等价，
    /// 防止同一 RequestId 被复用为另一项操作时静默沿用旧 journal 记录。
    /// 调用方应使用 <see cref="ComputePayloadDigest"/> 计算；未设置时为 null（参与比较时两侧须同为 null）。
    /// </summary>
    public string? PayloadDigest { get; init; }

    /// <summary>
    /// P0-3 CAS-2：workspace 作用域标识。
    /// 用于 PrepareAsync 语义等价校验，确保同一 RequestId 不跨 workspace 复用。
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// P0-3 CAS-2：run 作用域标识。
    /// 用于 PrepareAsync 语义等价校验，确保同一 RequestId 不跨 run 复用。
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// 计算 tool 调用 payload 的 SHA-256 摘要（小写 hex 字符串）。
    /// 调用方在构造 <see cref="ToolDispatchJournalEntry"/> 时应调用此方法设置 <see cref="PayloadDigest"/>。
    /// </summary>
    /// <param name="payload">tool 调用 payload（可为 null，按空字符串处理）。</param>
    /// <returns>SHA-256 摘要的小写 hex 字符串。</returns>
    public static string ComputePayloadDigest(string? payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// P0-3：Durable Tool 执行结果缓存（与 Journal Committed 状态同一事务持久化）。
/// </summary>
/// <remarks>
/// 当 Journal 已处于 <see cref="ToolDispatchState.Committed"/> / <see cref="ToolDispatchState.ResultDelivered"/>
/// 时，<see cref="IDurableToolExecutor"/> 应从缓存返回此结果，<b>禁止重新 Dispatch</b>，
/// 防止外部副作用被重复执行。
/// 字段与 <see cref="ToolExecutionResult"/> 对齐，但 ToolCallId 作为主键用于 <see cref="IDurableToolResultStore"/> 查询。
/// </remarks>
public sealed record DurableToolResult
{
    /// <summary>
    /// Tool 调用 ID（与 ToolCallStarted/Completed 事件中的 toolCallId 一致）。
    /// P0-4：不再作为主键（模型生成，不保证跨 Run/Provider 唯一）；改为 tool_dispatch_results 上的辅助索引列，
    /// 供旧 <see cref="IDurableToolResultStore.GetAsync"/> 查询路径使用。主键为 <see cref="RequestId"/>。
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>本次调用的 RequestId（与 Journal request_id 一致；P0-4 起为 tool_dispatch_results 主键）。</summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// P0-4：workspace 作用域标识（可选）。
    /// 与 <see cref="RunId"/> / <see cref="InvocationId"/> 共同构成 tool_dispatch_results 的
    /// UNIQUE(workspace_id, run_id, invocation_id) 约束，作为 Workspace 隔离键，防止另一 Run 覆盖已有 Tool Result。
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>P0-4：Agent Run 作用域标识（可选；配合 <see cref="WorkspaceId"/> / <see cref="InvocationId"/> 构成隔离键）。</summary>
    public string? RunId { get; init; }

    /// <summary>
    /// P0-4：本次调用的 Invocation ID（可选；代码层等同于 <see cref="RequestId"/>，作为稳定调用身份）。
    /// 非 null 且非空时参与 UNIQUE(workspace_id, run_id, invocation_id) 约束；为空时该行不参与约束（兼容旧数据）。
    /// </summary>
    public string? InvocationId { get; init; }

    /// <summary>调用方提供的幂等键（可选；用于外部系统去重）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Tool 副作用分类。</summary>
    public required ToolSideEffect SideEffect { get; init; }

    /// <summary>外部操作 ID（tool 实际执行后返回的外部系统 ID，可用于查询/对账）。</summary>
    public string? ExternalOperationId { get; init; }

    /// <summary>Tool 输出（成功时）。</summary>
    public string? Result { get; init; }

    /// <summary>是否成功。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>错误信息（失败时）。</summary>
    public string? Error { get; init; }

    /// <summary>执行耗时（毫秒）。</summary>
    public required double DurationMs { get; init; }
}

/// <summary>
/// P0-3：Durable Tool 结果缓存存储抽象。
/// </summary>
/// <remarks>
/// <b>引入背景</b>：<see cref="DefaultDurableToolExecutor"/> 在 Journal 已 Committed/ResultDelivered 时
/// 应从缓存返回结果而非重新 Dispatch。本抽象提供按 toolCallId 查询/保存缓存结果的能力。
///
/// <b>与 <see cref="IToolDispatchJournal"/> 的关系</b>：
/// 写入路径优先走 <see cref="IToolDispatchJournal.MarkCommittedWithResultAsync"/>（同一事务持久化 state + result）；
/// 本接口的 <see cref="SaveAsync"/> 用于无 journal 路径或独立缓存场景。
/// 读取路径通过 <see cref="GetAsync"/> 查询缓存结果。
/// </remarks>
public interface IDurableToolResultStore
{
    // P0-4：旧方法（按 tool_call_id）保留兼容但已过时——tool_call_id 不保证跨 Run/Provider 唯一，
    //       不能作为主键。新代码应使用 GetByRequestIdAsync / SaveByRequestIdAsync（按稳定 request_id）。

    /// <summary>
    /// 按 toolCallId 获取缓存结果（旧路径，已过时）。
    /// P0-4：tool_call_id 不再是主键，仅为辅助索引；新代码应使用 <see cref="GetByRequestIdAsync"/>。
    /// </summary>
    /// <param name="toolCallId">Tool 调用 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>缓存结果；不存在时返回 null。</returns>
    Task<DurableToolResult?> GetAsync(string toolCallId, CancellationToken ct);

    /// <summary>
    /// 保存缓存结果（旧路径，已过时；按 toolCallId 幂等覆盖）。
    /// P0-4：底层按 <see cref="DurableToolResult.RequestId"/> upsert（tool_call_id 不再唯一）。
    /// 新代码应使用 <see cref="SaveByRequestIdAsync"/>。
    /// </summary>
    /// <param name="toolCallId">Tool 调用 ID。</param>
    /// <param name="result">待缓存的结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveAsync(string toolCallId, DurableToolResult result, CancellationToken ct);

    // P0-4：新方法（按 request_id，稳定调用身份哈希，跨 Run/Provider 唯一）

    /// <summary>
    /// P0-4：按 RequestId 获取缓存结果（推荐路径）。
    /// request_id 为 tool_dispatch_results 主键，保证跨 Run/Provider 不覆盖。
    /// </summary>
    /// <param name="requestId">Tool 调用 RequestId（与 Journal request_id 一致）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>缓存结果；不存在时返回 null。</returns>
    Task<DurableToolResult?> GetByRequestIdAsync(string requestId, CancellationToken ct);

    /// <summary>
    /// P0-4：保存缓存结果（推荐路径；按 RequestId 幂等覆盖）。
    /// 写入 workspace_id / run_id / invocation_id 等 P0-4 新字段，供 UNIQUE 隔离约束与对账查询使用。
    /// </summary>
    /// <param name="result">待缓存的结果（RequestId 作为主键）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveByRequestIdAsync(DurableToolResult result, CancellationToken ct);
}

/// <summary>
/// P0-3：<see cref="IToolDispatchJournal.PrepareAsync"/> 返回值。
/// 描述 Prepare 后 Journal 的当前状态，供 <see cref="IDurableToolExecutor"/> 决策是否 Dispatch。
/// </summary>
/// <remarks>
/// <b>决策矩阵</b>：
/// <list type="bullet">
///   <item><see cref="ShouldDispatch"/>=true（Journal 不存在或 Prepared）→ 调用方应执行 Dispatch。</item>
///   <item><see cref="NeedsReconciliation"/>=true（Journal = DispatchingIntent 或 Dispatched）→ 调用方应返回对账结果（携带 <see cref="ExternalOperationId"/>），不重新 Dispatch。DispatchingIntent 表示外部调用可能已开始但未完成。</item>
///   <item><see cref="CachedResult"/> 非空（Journal = Committed/ResultDelivered）→ 调用方应直接返回缓存结果，<b>禁止重新 Dispatch</b>。</item>
/// </list>
/// </remarks>
public sealed record ToolDispatchPrepareResult
{
    /// <summary>Prepare 后 Journal 的当前状态。</summary>
    public required ToolDispatchState CurrentState { get; init; }

    /// <summary>是否需要执行 Dispatch（Journal 不存在或 Prepared）。</summary>
    public required bool ShouldDispatch { get; init; }

    /// <summary>是否需要对账（Journal = Dispatched，外部副作用可能已执行但未提交）。</summary>
    public required bool NeedsReconciliation { get; init; }

    /// <summary>外部操作 ID（NeedsReconciliation=true 时填充，供对账查询）。</summary>
    public string? ExternalOperationId { get; init; }

    /// <summary>
    /// 缓存结果（Journal = Committed/ResultDelivered 时填充）。
    /// 非空时调用方应直接返回此结果，禁止重新 Dispatch。
    /// </summary>
    public DurableToolResult? CachedResult { get; init; }
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
/// <b>P0-3 CAS-1：expected-state 精确匹配（state = @expected）</b>。
/// Mark* 方法使用精确前驱状态 CAS 推进状态机（而非旧版 <c>state &lt; @target</c> 宽松匹配），
/// <b>不自动创建 stub 条目</b>，且<b>禁止跨级跳跃</b>（如 Prepared → Committed）：
/// <list type="bullet">
///   <item>当前 state = expected（精确前驱） → 成功前向推进（Applied）。</item>
///   <item>当前 state = target（已到达目标） → 幂等成功（AlreadyApplied），不报错。</item>
///   <item>当前 state &gt; target（已超过目标） → 幂等成功（AlreadyAdvanced），不报错。</item>
///   <item>当前 state &lt; expected（缺失中间状态，跨级跳跃） → 抛 <see cref="InvalidOperationException"/>（InvalidTransition）。</item>
///   <item>request_id 不存在（缺失前驱记录） → 抛 <see cref="InvalidOperationException"/>（MissingPredecessor），
///     而非补造高级状态。这保证审计链完整：不存在 → Committed 这样的跳跃不再可能。</item>
/// </list>
///
/// <b>P0-3 CAS-2：PrepareAsync 语义等价校验</b>。
/// <see cref="PrepareAsync"/> 对同一 request_id 重复写入时不再静默沿用旧记录：
/// 既有行必须与新条目在 <see cref="ToolDispatchJournalEntry.ToolName"/> /
/// <see cref="ToolDispatchJournalEntry.IdempotencyKey"/> /
/// <see cref="ToolDispatchJournalEntry.PayloadDigest"/> /
/// <see cref="ToolDispatchJournalEntry.WorkspaceId"/> /
/// <see cref="ToolDispatchJournalEntry.RunId"/> 上语义等价；
/// 否则抛 <see cref="InvalidOperationException"/>（RequestIdReuseDetected）。
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
    /// <summary>
    /// P0-6：指示 <see cref="MarkCommittedWithResultAsync"/> 是否在同事务内持久化 Tool 结果缓存。
    /// </summary>
    /// <remarks>
    /// <b>true</b>（如 <c>PostgresToolDispatchJournal</c>）：MarkCommittedWithResultAsync 在同一 DB 事务内
    /// 同时 UPDATE journal state 与 UPSERT 结果到结果表，调用方无需再单独调用
    /// <see cref="IDurableToolResultStore.SaveAsync"/>。
    /// <b>false</b>（如 <c>InMemoryToolDispatchJournal</c>）：MarkCommittedWithResultAsync 仅推进状态机 +
    /// 进程内缓存，调用方需通过 <see cref="IDurableToolResultStore"/>（若注入）单独持久化结果。
    /// </remarks>
    bool PersistsResults { get; }

    /// <summary>写入 Prepared 条目（在调用 tool 之前），返回 Prepare 结果供调用方决策。</summary>
    /// <param name="entry">journal 条目（State 应为 Prepared）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 幂等：重复 Prepare 同一 request_id 不覆盖已推进的状态（ON CONFLICT DO NOTHING / TryAdd 语义）。
    /// 持久化实现要求 <see cref="ToolDispatchJournalEntry.IdempotencyKey"/> 全局唯一（UNIQUE partial index）。
    ///
    /// <b>P0-3：返回值决策矩阵</b>：
    /// <list type="bullet">
    ///   <item>Journal 不存在（新插入）→ <see cref="ToolDispatchPrepareResult.ShouldDispatch"/>=true。</item>
    ///   <item>Journal = Prepared（重复 Prepare）→ <see cref="ToolDispatchPrepareResult.ShouldDispatch"/>=true。</item>
    ///   <item>Journal = DispatchingIntent → <see cref="ToolDispatchPrepareResult.NeedsReconciliation"/>=true（外部调用可能已开始但未完成，需对账）。</item>
    ///   <item>Journal = Dispatched → <see cref="ToolDispatchPrepareResult.NeedsReconciliation"/>=true（外部副作用可能已执行，需对账）。</item>
    ///   <item>Journal = Committed/ResultDelivered → <see cref="ToolDispatchPrepareResult.CachedResult"/> 非空（禁止重新 Dispatch）。</item>
    /// </list>
    /// 调用方（<see cref="IDurableToolExecutor"/>）应根据返回值决定是否 Dispatch、对账或返回缓存结果。
    /// </remarks>
    ValueTask<ToolDispatchPrepareResult> PrepareAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// P0-1: Mark that the external Tool call is about to start. Must be persisted BEFORE the actual external call.
    /// This creates a durable boundary: if a crash occurs after this point, recovery knows the external call may have started.
    /// Transitions state from Prepared to DispatchingIntent (CAS). Throws InvalidOperationException if state has already
    /// advanced past DispatchingIntent (e.g., Dispatched), indicating a concurrent dispatch may have occurred.
    /// </summary>
    /// <param name="requestId">Tool RequestId (must already have a Prepared entry).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">request_id missing (MissingPredecessor), or state already past DispatchingIntent (AlreadyAdvanced).</exception>
    ValueTask MarkDispatchingIntentAsync(string requestId, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// P0-3：将指定 RequestId 的状态推进到 Committed，并在<b>同一事务</b>内持久化 Tool 结果缓存。
    /// </summary>
    /// <param name="requestId">Tool RequestId（必须已存在 Dispatched 条目）。</param>
    /// <param name="result">待缓存的结果（含 ToolCallId 作为主键；与 Committed 状态原子写入）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">request_id 不存在（缺失 Dispatched 前驱）或当前 state ≥ Committed（逆退）。</exception>
    /// <remarks>
    /// <b>同一事务保证</b>：持久化实现（如 <see cref="PostgresToolDispatchJournal"/>）应在单个 DB 事务内
    /// 同时 UPDATE journal state 与 INSERT/UPSERT 结果缓存，确保崩溃恢复时不会出现
    /// "state=Committed 但 result 缺失"的不一致状态。
    /// 进程内实现使用原子字典更新模拟事务语义。
    /// </remarks>
    ValueTask MarkCommittedWithResultAsync(string requestId, DurableToolResult result, CancellationToken cancellationToken = default);

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
    /// P2：批量拒绝（与 <see cref="AckBatchAsync"/> 对称，单次 SQL 事务内批量 UPDATE Leased → Pending）。
    /// 部分成功不抛异常，返回失败的 instruction_id 列表（token 不匹配、租约已过期回滚或已确认）。
    /// 用于配合 <see cref="LeaseBatchAsync"/> 批量消费后批量拒绝；失败的 nack 不抛异常，调用方可根据返回的失败列表决定后续处理。
    /// </summary>
    /// <param name="nacks">待拒绝的 (InstructionId, LeaseToken) 元组列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>失败的 instruction_id 列表（token 不匹配或状态非 Leased）；全部成功时为空列表。</returns>
    ValueTask<IReadOnlyList<string>> NackBatchAsync(IReadOnlyList<(string InstructionId, string LeaseToken)> nacks, CancellationToken cancellationToken = default);

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
    /// P2：批量续租约（与 <see cref="AckBatchAsync"/> 对称，单次 SQL 事务内批量 UPDATE lease_expires_at）。
    /// 所有续租使用相同的 <paramref name="extension"/> 时长；部分成功不抛异常，返回失败的 instruction_id 列表。
    /// 用于配合 <see cref="LeaseBatchAsync"/> 批量消费后批量续租，减少高并发下的网络往返。
    /// </summary>
    /// <param name="renewals">待续租的 (InstructionId, LeaseToken) 元组列表。</param>
    /// <param name="extension">延长的时间量（从当前 UTC 时间开始计算新的 expires_at，所有续租共用此值）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>失败的 instruction_id 列表（token 不匹配或状态非 Leased）；全部成功时为空列表。</returns>
    ValueTask<IReadOnlyList<string>> RenewLeaseBatchAsync(IReadOnlyList<(string InstructionId, string LeaseToken)> renewals, TimeSpan extension, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// P2：批量拒绝结果（与 <see cref="NackBatchAsync"/> 对称，单次 SQL 事务内批量 UPDATE Leased → Pending）。
    /// 部分成功不抛异常，返回失败的 result_id 列表。用于配合 <see cref="LeaseResultBatchAsync"/> 批量消费后批量拒绝。
    /// </summary>
    /// <param name="nacks">待拒绝的 (ResultId, LeaseToken) 元组列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>失败的 result_id 列表（token 不匹配或状态非 Leased）；全部成功时为空列表。</returns>
    ValueTask<IReadOnlyList<string>> NackResultBatchAsync(IReadOnlyList<(string ResultId, string LeaseToken)> nacks, CancellationToken cancellationToken = default);

    /// <summary>P0-1：续结果租约（延长 lease_expires_at）。</summary>
    ValueTask RenewResultLeaseAsync(string resultId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken = default);

    /// <summary>
    /// P2：批量续结果租约（与 <see cref="RenewLeaseBatchAsync"/> 对称，单次 SQL 事务内批量 UPDATE lease_expires_at）。
    /// 所有续租使用相同的 <paramref name="extension"/> 时长；部分成功不抛异常，返回失败的 result_id 列表。
    /// 用于配合 <see cref="LeaseResultBatchAsync"/> 批量消费后批量续租。
    /// </summary>
    /// <param name="renewals">待续租的 (ResultId, LeaseToken) 元组列表。</param>
    /// <param name="extension">延长的时间量（从当前 UTC 时间开始计算新的 expires_at，所有续租共用此值）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>失败的 result_id 列表（token 不匹配或状态非 Leased）；全部成功时为空列表。</returns>
    ValueTask<IReadOnlyList<string>> RenewResultLeaseBatchAsync(IReadOnlyList<(string ResultId, string LeaseToken)> renewals, TimeSpan extension, CancellationToken cancellationToken = default);

    /// <summary>
    /// P2：异步获取 inbox 中 Pending 指令数（推荐用于热路径）。
    /// 持久化实现（如 <c>PostgresDurableTransport</c>）返回<b>DB 精确值（全局，跨实例）</b>——
    /// 反映所有实例的累积 Pending 行数，可用于调度/安全判断。
    /// 同步属性 <c>PendingInstructionCount</c>（具体实现上）仅返回<b>本实例趋势值</b>，<b>不可用于调度/安全判断</b>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前 inbox 中 state='Pending' 的指令数（不含 Leased）。</returns>
    ValueTask<int> GetPendingInstructionCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// P2：异步获取 outbox 中 Pending 结果数（推荐用于热路径）。
    /// 持久化实现（如 <c>PostgresDurableTransport</c>）返回<b>DB 精确值（全局，跨实例）</b>——
    /// 反映所有实例的累积 Pending 行数，可用于调度/安全判断。
    /// 同步属性 <c>PendingResultCount</c>（具体实现上）仅返回<b>本实例趋势值</b>，<b>不可用于调度/安全判断</b>。
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

    /// <summary>
    /// P0-6：结果元数据（键值对，与 <see cref="AgentKernelInstruction.Metadata"/> 对称）。
    /// 用于携带 Durable Transport 投递状态（<see cref="DurableDeliveryStatusKeys"/>）等诊断信息，
    /// 供下游 reconciliation 消费。空字典表示无附加元数据（兼容旧行为）。
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// P0-6-3：指令处理结果分类。决定外层对 Durable lease 的 Ack/Nack/DeadLetter 行为。
/// </summary>
/// <remarks>
/// Kernel 的 ProcessInstructionAsync 内部不再吞掉异常，
/// 而是返回此枚举让外层统一决策：
/// <list type="bullet">
///   <item><see cref="Succeeded"/> → 返回成功结果并 Ack。</item>
///   <item><see cref="BusinessRejected"/> → 返回失败结果并 Ack（业务拒绝，不需重试）。</item>
///   <item><see cref="TransientInfrastructure"/> → Nack / Retry（基础设施临时故障，应重试）。</item>
///   <item><see cref="PermanentFault"/> → Ack + 在结果 Metadata 标记死信（永久故障，进入死信）。</item>
/// </list>
/// </remarks>
public enum InstructionProcessingOutcome : byte
{
    /// <summary>处理成功：返回成功结果并 Ack 输入 lease。</summary>
    Succeeded = 0,

    /// <summary>业务拒绝：返回失败结果并 Ack（不重试，结果已确定性地产出）。</summary>
    BusinessRejected = 1,

    /// <summary>基础设施临时故障：Nack 让 lease 回滚为 Pending 供重试（transport/DB 临时不可用）。</summary>
    TransientInfrastructure = 2,

    /// <summary>永久故障：Ack 删除指令，但在结果 Metadata 标记 DurableDeliveryStatus=PermanentFault 供死信对账。</summary>
    PermanentFault = 3
}

/// <summary>
/// P0-6-6：Durable Transport 投递状态。标记在 <see cref="AgentKernelResult.Metadata"/> 中
/// （键 <see cref="DurableDeliveryStatusKeys.DurableDeliveryStatus"/>），供下游 reconciliation 消费。
/// </summary>
public enum DurableDeliveryStatus : byte
{
    /// <summary>非 Durable 路径（InProcessTransport），无 lease 概念。</summary>
    NotDurable = 0,

    /// <summary>已成功 Ack 输入 lease（正常路径）。</summary>
    Acked = 1,

    /// <summary>Ack 操作本身失败（token 不匹配/已被接管/已确认）；指令 lease 已过期会由 reaper 回滚。</summary>
    AckFailed = 2,

    /// <summary>Ack 前租约已过期被 reaper 回滚为 Pending；pump 会重新租约 + Submit，dedup 保证不重复执行。</summary>
    LeaseExpiredBeforeAck = 3,

    /// <summary>同一指令被重复投递（已被处理过）；本次返回缓存结果，Ack 幂等。</summary>
    DuplicateRedelivery = 4,

    /// <summary>永久故障（处理超时/PermanentFault outcome）：指令已 Ack 删除，结果标记供死信对账。</summary>
    PermanentFault = 5
}

/// <summary>
/// P0-6-6：Durable Transport 投递状态元数据键约定。
/// 写入 <see cref="AgentKernelResult.Metadata"/>，供下游 reconciliation 消费。
/// </summary>
public static class DurableDeliveryStatusKeys
{
    /// <summary>Durable 投递状态（值为 <see cref="DurableDeliveryStatus"/> 的数字字符串）。</summary>
    public const string DurableDeliveryStatus = "durable-delivery-status";

    /// <summary>Ack 失败原因诊断（自由文本，仅当 status=AckFailed/LeaseExpiredBeforeAck 时填充）。</summary>
    public const string AckFailureDiagnostic = "durable-ack-failure-diagnostic";
}

/// <summary>
/// P0-6-6：指令对账抽象。由 Journal/Result Store 实现，用于在重复投递或 Ack 失败时判断
/// 应返回缓存结果、继续恢复还是进入人工处理。
/// </summary>
/// <remarks>
/// <b>可选依赖</b>：未注入时 Kernel 仅在结果 Metadata 中标记 <see cref="DurableDeliveryStatus"/>，
/// 不执行主动对账。生产部署应注入持久化实现（如基于 DB 的 reconciliation service）。
/// </remarks>
public interface IInstructionReconciliation
{
    /// <summary>对账指定指令的投递状态。</summary>
    /// <param name="instructionId">指令 ID。</param>
    /// <param name="leaseToken">当前投递的 lease token（可能为空）。</param>
    /// <param name="status">当前投递状态（来自 <see cref="DurableDeliveryStatus"/>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>对账建议：CachedResult（返回缓存结果）/ Resume（继续恢复）/ ManualIntervention（进入人工处理）。</returns>
    ValueTask<InstructionReconciliationAction> ReconcileAsync(
        string instructionId,
        string? leaseToken,
        DurableDeliveryStatus status,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// P0-6-6：对账建议动作。
/// </summary>
public enum InstructionReconciliationAction : byte
{
    /// <summary>无可用对账信息（如未注入实现）；Kernel 按默认路径处理。</summary>
    None = 0,

    /// <summary>指令已处理过，应返回缓存结果（不重新执行）。</summary>
    ReturnCachedResult = 1,

    /// <summary>指令处理中断，应继续恢复（如从 checkpoint 恢复）。</summary>
    ResumeProcessing = 2,

    /// <summary>指令进入永久故障，需人工介入（如 Dispatched 但未 Committed 的模糊状态）。</summary>
    ManualIntervention = 3
}
