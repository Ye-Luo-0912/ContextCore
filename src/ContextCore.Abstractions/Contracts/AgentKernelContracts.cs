using System.Security.Cryptography;
using System.Text;

namespace ContextCore.Abstractions;

// ===========================================================================
// Tool Dispatch 与 Agent Checkpoint 契约
//
// 历史：本文件原为旧 Agent Kernel 契约（IAgentKernel / IAgentKernelTransport /
//   IDurableTransport / IKernelResultOutbox 等旧指令平面契约）。执行平面已收敛到
//   AgentRunStore → AgentKernelHost → AgentRunActor 单一平面，旧平面契约已删除
//   （删除双执行平面）。
//
// 当前保留：
//   1. Tool Dispatch 契约（IToolDispatcher / IToolCatalog / IToolDispatchJournal /
//      IDurableToolResultStore / ToolDispatchState 状态机等）——
//      由 IDurableToolExecutor / AgentRunActor / RealToolDispatcher 使用。
//   2. Agent Checkpoint 契约（IAgentCheckpointFactory / IPersistentAgentCheckpointStore）——
//      由 AgentRunActor 的 checkpoint 持久化与 PostgresAgentCheckpointStore 使用。
// ===========================================================================

/// <summary>
/// Tool Dispatcher 抽象。
/// </summary>
/// <remarks>
/// 负责按名称分派 tool 调用并返回结果。Actor 不直接调用 tool；通过此抽象解耦。
/// 默认实现 <c>EchoToolDispatcher</c> 原样返回 payload（测试用）。
/// </remarks>
public interface IToolDispatcher
{
    /// <summary>当前 Dispatcher 支持的 tool 名称集合。</summary>
    IReadOnlySet<string> SupportedTools { get; }

    /// <summary>
    /// 获取 tool 的静态描述符（前置副作用声明 / 审批 / 幂等 / fence / 恢复策略）。
    /// 由 <see cref="ContextCore.Core.Services.AgentRunRuntime.DefaultDurableToolExecutor"/>
    /// 在 Dispatch 前读取，用于 fail-closed 校验与恢复策略决策；
    /// 未注册的 tool 返回 null（调用方按无声明处理）。
    /// </summary>
    ToolDescriptor? GetDescriptor(string toolName);

    /// <summary>分派 tool 调用。</summary>
    /// <param name="request">分派请求（tool 名称 + payload + requestId）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分派结果（成功/失败 + 输出/错误 + 耗时）。</returns>
    ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tool catalog — provides tool definitions for model function calling.
/// Decoupled from IToolDispatcher to allow decorators/wrappers/MCP adapters to expose definitions
/// without requiring Actor to cast to a concrete RealToolDispatcher.
/// </summary>
public interface IToolCatalog
{
    /// <summary>Get tool definitions for model function calling. Returns empty list if no tools registered.</summary>
    IReadOnlyList<AgentToolDefinition> GetToolDefinitions();
}

/// <summary>
/// Tool 分派请求。
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
    /// 调用方提供的幂等键（可选；用于外部系统去重）。
    /// 透传到 Tool provider 与 Journal，让外部系统侧也能基于此键去重，
    /// 配合 <see cref="IToolDispatchJournal"/> 的 UNIQUE 约束兜底实现外部副作用 exactly-once。
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// workspace 作用域标识（可选）。
    /// 由调用方（<see cref="ContextCore.Core.Services.AgentRunRuntime.DefaultDurableToolExecutor"/>）填充，
    /// 透传到 <see cref="ToolExecutionContext.WorkspaceId"/> 供 Tool Handler 做作用域校验/审计。
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// Agent Run 作用域标识（可选）。
    /// 透传到 <see cref="ToolExecutionContext.RunId"/> 供 Tool Handler 关联 Run 上下文。
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// 外部操作 ID（可选，框架生成，供 Tool provider 关联外部副作用）。
    /// 由执行器在 Prepare 时生成并持久化到 journal（重放时从 journal 读回，保持稳定），
    /// 在外部调用发起前下发给 Tool Handler，让外部系统侧可基于此 ID 去重/对账；
    /// Handler 返回真实外部系统 ID 时可覆盖它。
    /// </summary>
    public string? ExternalOperationId { get; init; }

    /// <summary>
    /// 本次 Tool 调用的截止时间（UTC，可选）。
    /// 透传到 <see cref="ToolExecutionContext.DeadlineAt"/>；Tool Handler 应在此时间前完成调用。
    /// </summary>
    public DateTimeOffset? DeadlineAt { get; init; }

    /// <summary>
    /// 租约围栏（可选）。
    /// 携带 lease token + fencing token，让副作用 Tool Handler 校验调用方仍持有有效租约。
    /// null = 无 lease 路径（测试 / 非 Actor 调用）。
    /// </summary>
    public AgentLeaseFence? LeaseFence { get; init; }
}

/// <summary>
/// Tool 执行上下文。
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

    /// <summary>
    /// 外部操作 ID（可选，框架在 Prepare 时生成）。
    /// 与 journal 条目 / <see cref="ToolDispatchResult.ExternalOperationId"/> 对应，
    /// 供 Tool Handler 在发起外部调用前携带此 ID，让外部系统侧可据此关联与去重；
    /// Handler 可在返回结果中覆盖它。
    /// </summary>
    public string? ExternalOperationId { get; init; }

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
/// Tool 副作用分类。决定恢复时是否自动重放。
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
    /// 幂等写（如带 IdempotencyKey 的 API 调用）。可安全重放，但需 lease fence 保护。
    /// 恢复时：若有缓存结果则使用，否则重放（依赖外部幂等性）。
    /// </summary>
    IdempotentWrite = 4,

    /// <summary>
    /// 受 Fence 保护的写（如数据库事务 + fencing token 校验）。
    /// 恢复时：必须有有效 lease fence，否则 fail-closed。
    /// </summary>
    FencedWrite = 5,

    /// <summary>
    /// 非幂等写（如发送邮件 / 扣款）。无外部幂等或 fencing 支持时必须 fail-closed。
    /// 恢复时：使用缓存结果，绝不重放；无缓存结果时需 RequiresReconciliation 对账。
    /// </summary>
    NonIdempotentWrite = 6,

    /// <summary>
    /// 需对账（如外部系统状态不确定）。恢复时不自动重放，需人工或外部对账流程确认。
    /// </summary>
    RequiresReconciliation = 7
}

/// <summary>
/// Tool recovery strategy. Determines how to handle Tool calls in Prepared/DispatchingIntent state during crash recovery.
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
/// Tool static descriptor. Declares side-effect properties, approval requirements, recovery strategy, etc.
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
    /// <summary>
    /// 对账截止时长（自对账记录创建起；默认 24 小时）。
    /// 超期未决 → ControlRoom 列表高亮 + ToolReconciliationWorker 告警。
    /// 经 <see cref="ToolExecutionResult.ReconciliationDeadline"/> 回传到对账记录 DeadlineUtc。
    /// </summary>
    public TimeSpan ReconciliationDeadline { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Maximum execution time. Tool calls exceeding this are treated as timed out (RequiresReconciliation).</summary>
    public TimeSpan MaximumExecutionTime { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Tool 执行策略处置结果：提交 / 等待对账 / 拒绝执行。
/// 由 <see cref="IToolEffectPolicy"/> 根据 Descriptor 前置声明 + Journal 状态 + 执行结果解析，
/// 替代执行器"副作用非 Unknown 即自动提交"的宽松判定，
/// 防止 NonIdempotentWrite / RequiresReconciliation / 外部调用失败副作用未知等危险状态被误提交。
/// </summary>
public enum ToolExecutionDisposition : byte
{
    /// <summary>结果确定且策略允许 → 立即提交（MarkCommittedWithResultAsync 原子写 state + result）。</summary>
    Commit = 0,

    /// <summary>
    /// 结果不确定或策略要求 → 不自动提交，journal 保持模糊状态（DispatchingIntent/Dispatched），
    /// 等待对账确认（Reconciliation）后由裁决方决定提交。
    /// </summary>
    HoldForReconciliation = 1,

    /// <summary>前置条件不满足或策略禁止 → 拒绝执行（fail-closed，不触碰外部副作用）。</summary>
    FailClosed = 2
}

/// <summary>Tool 执行策略解析结果。</summary>
public sealed record ToolExecutionPolicy
{
    /// <summary>处置结果（Commit / HoldForReconciliation / FailClosed）。</summary>
    public required ToolExecutionDisposition Disposition { get; init; }

    /// <summary>决策原因（审计/诊断；写入结果 Error 或日志）。</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// 是否要求提交前对账确认（RequiresReconciliation / NonIdempotentWrite 等声明）。
    /// true 时即使结果成功也不自动提交，必须经对账确认外部副作用真相后提交。
    /// </summary>
    public bool RequiresReconciliationBeforeCommit { get; init; }
}

/// <summary>
/// Tool 执行策略引擎：根据 Descriptor 前置声明 + Journal 状态 + 执行结果，
/// 解析提交 / 对账 / 拒绝处置。执行器以此决定是否自动提交。
/// </summary>
/// <remarks>
/// 严格提交矩阵（Descriptor.DeclaredSideEffect → 执行后处置）：
/// <list type="bullet">
/// <item>None / ReadOnly → 结果确定后 Commit（只读，重放安全）。</item>
/// <item>Write → 执行成功（Succeeded）时 Commit；失败时 Hold（副作用是否发生未知）。</item>
/// <item>IdempotentWrite → 稳定幂等键明确返回且执行成功时 Commit；否则 Hold。</item>
/// <item>FencedWrite → 有效 Fence 确认（执行成功且 Fence 窗口内）时 Commit；否则 Hold。</item>
/// <item>NonIdempotentWrite → 永不自动提交：Approval + 外部操作身份确认后经对账提交（Hold）。</item>
/// <item>RequiresReconciliation → 永不自动提交：必须经 Reconciliation Handler 确认后提交（Hold）。</item>
/// <item>Unknown → 永不自动提交（保守策略，Hold）。</item>
/// </list>
/// </remarks>
public interface IToolEffectPolicy
{
    /// <summary>
    /// 解析 Tool 执行策略。
    /// </summary>
    /// <param name="descriptor">Tool 前置声明（副作用 / 审批 / 幂等 / fence / 恢复策略）。</param>
    /// <param name="journal">PrepareWithIntentAsync 返回的 journal 状态与身份；无 journal（降级直连）时为 null。</param>
    /// <param name="result">Dispatch 后的执行结果；未分派（前置校验失败）时为 null。</param>
    /// <returns>执行策略处置。</returns>
    ToolExecutionPolicy Resolve(
        ToolDescriptor descriptor,
        ToolDispatchPrepareResult? journal,
        ToolExecutionResult? result);
}

/// <summary>
/// Tool 对账记录状态。
/// </summary>
public enum ToolReconciliationStatus : byte
{
    /// <summary>待对账（Run 处于 AwaitingReconciliation，等待 Worker 或人工裁决）。</summary>
    Pending = 0,

    /// <summary>对账进行中（ToolReconciliationWorker 已接管，正在确认外部副作用真相）。</summary>
    Running = 1,

    /// <summary>已裁决：外部副作用确认发生并已提交（journal → Committed，含对账结果）。</summary>
    Resolved = 2,

    /// <summary>已拒绝：外部副作用确认未发生或人工拒绝（journal 已提交 void 结果，禁止重放）。</summary>
    Rejected = 3
}

/// <summary>
/// Tool 对账记录：Run 级协调单元，对应一条未裁决的 Tool journal 条目
/// （DispatchingIntent/Dispatched/Reconciling 高风险状态）。
/// 只要 Run 存在未 Resolved/Rejected 的记录，就不得进入 <see cref="AgentRunState.Completed"/>。
/// </summary>
public sealed record ToolReconciliationRecord
{
    /// <summary>对账记录 ID（POST /runs/{runId}/reconciliations/{id}/resolve 用）。</summary>
    public required string ReconciliationId { get; init; }

    /// <summary>所属 Agent Run ID。</summary>
    public required string RunId { get; init; }

    /// <summary>所属 Workspace ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>对应 Tool 调用 RequestId（journal 主键；与 ToolCallStarted/Completed 事件一致）。</summary>
    public required string RequestId { get; init; }

    /// <summary>Tool 名称。</summary>
    public required string ToolName { get; init; }

    /// <summary>外部操作 ID（对账时查询外部系统的操作标识；journal 条目持久化的值）。</summary>
    public string? ExternalOperationId { get; init; }

    /// <summary>对账处理程序名（ToolDescriptor.ReconciliationHandler；null = 仅人工裁决）。</summary>
    public string? ReconciliationHandler { get; init; }

    /// <summary>
    /// 对账截止时间（UTC；null = 无截止）。
    /// 创建时由 Actor 按 ToolDescriptor.ReconciliationDeadline 计算（CreatedAt + 截止时长）。
    /// 超期未决（DeadlineUtc &lt; now 且 Pending/Running）视为过期：
    /// ControlRoom 列表高亮 + ToolReconciliationWorker 告警钩子。
    /// </summary>
    public DateTimeOffset? DeadlineUtc { get; init; }

    /// <summary>当前对账状态。</summary>
    public required ToolReconciliationStatus Status { get; init; }

    /// <summary>对账确认的外部副作用结果（Resolved 时填充）。</summary>
    public string? Result { get; init; }

    /// <summary>外部副作用真相（true = 已发生；false = 未发生）。</summary>
    public bool? SideEffectOccurred { get; init; }

    /// <summary>拒绝/失败原因（Rejected 时填充）。</summary>
    public string? Reason { get; init; }

    /// <summary>记录创建时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>最近更新时间（UTC）。</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>裁决完成时间（UTC；Resolved/Rejected 时填充）。</summary>
    public DateTimeOffset? ResolvedAt { get; init; }
}

/// <summary>
/// 对账结果：Reconciliation Handler 确认的外部副作用真相。
/// </summary>
public sealed record ToolReconciliationOutcome
{
    /// <summary>外部副作用是否确实发生。</summary>
    public required bool SideEffectOccurred { get; init; }

    /// <summary>外部系统查得的真相结果（SideEffectOccurred=true 时填充）。</summary>
    public string? Result { get; init; }

    /// <summary>对账失败原因（无法确认时填充；记录保持 Pending 等待重试/人工）。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Tool 对账处理程序：按 <see cref="ToolDescriptor.ReconciliationHandler"/> 名称注册，
/// 以 <see cref="ToolReconciliationRecord.ExternalOperationId"/> 查询外部系统，
/// 确认模糊 Tool 调用的外部副作用真相（occurred + result，或未发生）。
/// </summary>
public interface IToolReconciliationHandler
{
    /// <summary>处理程序名称（与 ToolDescriptor.ReconciliationHandler 匹配）。</summary>
    string HandlerName { get; }

    /// <summary>对账指定记录：确认外部副作用真相。</summary>
    ValueTask<ToolReconciliationOutcome> ReconcileAsync(
        ToolReconciliationRecord record,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// ControlRoom 对账列表查询（GET /api/agents/reconciliations 分页过滤条件）。
/// </summary>
public sealed record ReconciliationQuery
{
    /// <summary>按 Workspace 过滤（null = 全部 workspace）。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>按 Run 过滤（null = 全部 run）。</summary>
    public string? RunId { get; init; }

    /// <summary>按状态过滤（null = 全部状态）。</summary>
    public ToolReconciliationStatus? Status { get; init; }

    /// <summary>仅返回过期未决记录（DeadlineUtc &lt; now 且 Pending/Running）。</summary>
    public bool OverdueOnly { get; init; }

    /// <summary>分页偏移（默认 0）。</summary>
    public int Offset { get; init; }

    /// <summary>分页大小（默认 50；服务端 clamp ≤ 200）。</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>
/// ControlRoom 对账列表结果：分页条目 + 总数 + 过期未决告警计数。
/// </summary>
public sealed record ReconciliationListResult
{
    /// <summary>当前页条目（按 CreatedAt 倒序，最新在前）。</summary>
    public required IReadOnlyList<ToolReconciliationRecord> Items { get; init; }

    /// <summary>过滤条件下的总条数（分页前）。</summary>
    public required int Total { get; init; }

    /// <summary>过滤条件下过期未决记录数（DeadlineUtc &lt; now 且 Pending/Running；告警计数）。</summary>
    public required int OverdueCount { get; init; }
}

/// <summary>
/// Tool 对账记录存储：持久化 <see cref="ToolReconciliationRecord"/>，
/// 支撑 Run 级"未裁决不完成"约束与 ToolReconciliationWorker 轮询。
/// </summary>
public interface IToolReconciliationStore
{
    /// <summary>
    /// 创建对账记录（按 RunId+RequestId 幂等：已存在时返回既有记录）。
    /// </summary>
    ValueTask<ToolReconciliationRecord> CreateAsync(ToolReconciliationRecord record, CancellationToken cancellationToken = default);

    /// <summary>按对账记录 ID 查询。</summary>
    ValueTask<ToolReconciliationRecord?> GetAsync(string reconciliationId, CancellationToken cancellationToken = default);

    /// <summary>按 Run 列出全部对账记录（含已裁决）。</summary>
    ValueTask<IReadOnlyList<ToolReconciliationRecord>> ListByRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>按外部操作 ID 反查对账记录（跨 Run；ControlRoom / 运维按 journal externalOperationId 查询）。</summary>
    ValueTask<IReadOnlyList<ToolReconciliationRecord>> QueryByExternalOperationIdAsync(string externalOperationId, CancellationToken cancellationToken = default);

    /// <summary>ControlRoom 分页列表：按过滤条件（workspace/run/status/overdue）分页返回，附总数与过期告警计数。</summary>
    ValueTask<ReconciliationListResult> ListAsync(ReconciliationQuery query, CancellationToken cancellationToken = default);

    /// <summary>Run 是否存在未裁决（Pending/Running）对账记录。</summary>
    ValueTask<bool> HasUnresolvedForRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>列出待对账记录（ToolReconciliationWorker 轮询用，按创建时间升序）。</summary>
    ValueTask<IReadOnlyList<ToolReconciliationRecord>> ListPendingAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>CAS 推进 Pending → Running（并发 Worker 互斥，防止重复对账）。</summary>
    ValueTask<bool> TryBeginAsync(string reconciliationId, CancellationToken cancellationToken = default);

    /// <summary>CAS 回退 Running → Pending（Handler 对账失败时重置，等待下轮重试）。</summary>
    ValueTask<bool> TryResetToPendingAsync(string reconciliationId, CancellationToken cancellationToken = default);

    /// <summary>裁决为已发生并提交（记录 → Resolved）。</summary>
    ValueTask<bool> MarkResolvedAsync(string reconciliationId, ToolReconciliationOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>裁决为未发生/拒绝（记录 → Rejected）。</summary>
    ValueTask<bool> MarkRejectedAsync(string reconciliationId, ToolReconciliationOutcome outcome, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tool 分派结果。
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
    /// Tool 副作用分类。默认 Unknown（保守策略：未声明的 tool 不自动重放）。
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
/// Tool 分派状态机。
/// 实现恰好一次（exactly-once）tool 执行的核心状态。
/// </summary>
/// <remarks>
/// 状态流转（不可逆，只能向前）：
///   <see cref="Prepared"/> → <see cref="DispatchingIntent"/> → <see cref="Dispatched"/> → <see cref="Committed"/> → <see cref="ResultDelivered"/>
///
/// DispatchingIntent 外部副作用边界</b>。
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
///   - <see cref="Reconciling"/> → 对账进行中：外部副作用真相正在确认（以
///     <see cref="ExternalOperationId"/> 查询外部系统 / 人工裁决），确认后经
///     <see cref="IToolDispatchJournal.MarkReconciledWithResultAsync"/> 提交到
///     <see cref="Committed"/>；对账未完成前绝不静默重放。
///   - <see cref="Committed"/> 但未 <see cref="ResultDelivered"/> → 结果已持久化，可安全重发。
///   - <see cref="ResultDelivered"/> → 完全完成，无需任何动作。
///
/// <b>注意</b>：<see cref="DispatchingIntent"/> 使用数值 4（而非 1），
/// 以避免破坏数据库中已有的 Dispatched=1 / Committed=2 / ResultDelivered=3 的 byte 映射。
/// 状态机的"前向推进"判断基于逻辑顺序
/// （Prepared→DispatchingIntent→Dispatched→Reconciling→Committed→ResultDelivered），
/// 而非数值大小。
/// </remarks>
public enum ToolDispatchState : byte
{
    /// <summary>已准备（journal 已写入 Prepared 条目，但 tool 尚未真正调用）。</summary>
    Prepared = 0,

    /// <summary>
    /// 分派意图已持久化（外部调用即将开始但尚未返回）。
    /// 在调用 <see cref="IToolDispatcher.DispatchAsync"/> 之前由
    /// <see cref="IToolDispatchJournal.MarkDispatchingIntentAsync"/> 写入，
    /// 创建外部副作用 exactly-once 的 durable 边界。
    /// </summary>
    DispatchingIntent = 4,

    /// <summary>已分派（tool 已调用并返回，或外部调用已发起但结果未确认）。</summary>
    Dispatched = 1,

    /// <summary>
    /// 对账中：模糊状态（DispatchingIntent/Dispatched）经
    /// <see cref="IToolDispatchJournal.BeginReconciliationAsync"/> 显式进入，
    /// 表示外部副作用真相正在确认。确认后经
    /// <see cref="IToolDispatchJournal.MarkReconciledWithResultAsync"/> 提交到
    /// <see cref="Committed"/>（同事务写结果）；对账失败保持本状态等待重试或人工介入，
    /// 绝不静默重放外部副作用。
    /// </summary>
    Reconciling = 5,

    /// <summary>已提交（结果已写入 durable result store）。</summary>
    Committed = 2,

    /// <summary>结果已送达（已通过 transport 成功发送）。</summary>
    ResultDelivered = 3
}

/// <summary>
/// Tool 分派 journal 条目。
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

    /// <summary>
    /// 外部操作 ID（框架在 Prepare 时生成，持久化后重放时读回保持稳定）。
    /// 在外部调用发起前下发给 Tool Handler 供外部系统关联/去重；
    /// Handler 返回真实外部系统 ID 时在 <see cref="ToolDispatchState.Dispatched"/> 转换时覆盖。
    /// </summary>
    public string? ExternalOperationId { get; init; }

    /// <summary>journal 条目更新时间（UTC）。</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>失败/模糊状态原因诊断（如 Dispatched 但未 Committed 时的说明）。</summary>
    public string? DiagnosticNote { get; init; }

    /// <summary>
    /// tool 调用 payload 的 SHA-256 摘要（小写 hex）。
    /// 用于 <see cref="IToolDispatchJournal.PrepareAsync"/> 在同一 RequestId 已存在时验证语义等价，
    /// 防止同一 RequestId 被复用为另一项操作时静默沿用旧 journal 记录。
    /// 调用方应使用 <see cref="ComputePayloadDigest"/> 计算；未设置时为 null（参与比较时两侧须同为 null）。
    /// </summary>
    public string? PayloadDigest { get; init; }

    /// <summary>
    /// workspace 作用域标识。
    /// 用于 PrepareAsync 语义等价校验，确保同一 RequestId 不跨 workspace 复用。
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// run 作用域标识。
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
/// Durable Tool 执行结果缓存（与 Journal Committed 状态同一事务持久化）。
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
    /// 不再作为主键（模型生成，不保证跨 Run/Provider 唯一）；改为 tool_dispatch_results 上的辅助索引列，
    /// 供旧 <see cref="IDurableToolResultStore.GetAsync"/> 查询路径使用。主键为 <see cref="RequestId"/>。
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>本次调用的 RequestId（与 Journal request_id 一致；为 tool_dispatch_results 主键）。</summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// workspace 作用域标识（可选）。
    /// 与 <see cref="RunId"/> / <see cref="InvocationId"/> 共同构成 tool_dispatch_results 的
    /// UNIQUE(workspace_id, run_id, invocation_id) 约束，作为 Workspace 隔离键，防止另一 Run 覆盖已有 Tool Result。
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>Agent Run 作用域标识（可选；配合 <see cref="WorkspaceId"/> / <see cref="InvocationId"/> 构成隔离键）。</summary>
    public string? RunId { get; init; }

    /// <summary>
    /// 本次调用的 Invocation ID（可选；代码层等同于 <see cref="RequestId"/>，作为稳定调用身份）。
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
/// Durable Tool 结果缓存存储抽象。
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
    // 旧方法（按 tool_call_id）保留兼容但已过时——tool_call_id 不保证跨 Run/Provider 唯一，
    //       不能作为主键。新代码应使用 GetByRequestIdAsync / SaveByRequestIdAsync（按稳定 request_id）。

    /// <summary>
    /// 按 toolCallId 获取缓存结果（旧路径，已过时）。
    /// tool_call_id 不再是主键，仅为辅助索引；新代码应使用 <see cref="GetByRequestIdAsync"/>。
    /// </summary>
    /// <param name="toolCallId">Tool 调用 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>缓存结果；不存在时返回 null。</returns>
    Task<DurableToolResult?> GetAsync(string toolCallId, CancellationToken ct);

    /// <summary>
    /// 保存缓存结果（旧路径，已过时；按 toolCallId 幂等覆盖）。
    /// 底层按 <see cref="DurableToolResult.RequestId"/> upsert（tool_call_id 不再唯一）。
    /// 新代码应使用 <see cref="SaveByRequestIdAsync"/>。
    /// </summary>
    /// <param name="toolCallId">Tool 调用 ID。</param>
    /// <param name="result">待缓存的结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveAsync(string toolCallId, DurableToolResult result, CancellationToken ct);

    // 新方法（按 request_id，稳定调用身份哈希，跨 Run/Provider 唯一）

    /// <summary>
    /// 按 RequestId 获取缓存结果（推荐路径）。
    /// request_id 为 tool_dispatch_results 主键，保证跨 Run/Provider 不覆盖。
    /// </summary>
    /// <param name="requestId">Tool 调用 RequestId（与 Journal request_id 一致）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>缓存结果；不存在时返回 null。</returns>
    Task<DurableToolResult?> GetByRequestIdAsync(string requestId, CancellationToken ct);

    /// <summary>
    /// 保存缓存结果（推荐路径；按 RequestId 幂等覆盖）。
    /// 写入 workspace_id / run_id / invocation_id 等隔离键字段，供 UNIQUE 隔离约束与对账查询使用。
    /// </summary>
    /// <param name="result">待缓存的结果（RequestId 作为主键）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveByRequestIdAsync(DurableToolResult result, CancellationToken ct);
}

/// <summary>
/// <see cref="IToolDispatchJournal.PrepareWithIntentAsync"/> 返回的恢复决策。
/// Journal 是调用身份的权威来源——恢复决策与生效身份（ExternalOperationId / IdempotencyKey）
/// 均由 Journal 原子返回，调用方（<see cref="IDurableToolExecutor"/>）只能使用这些值，
/// 不能使用恢复时重新生成的值（否则崩溃恢复后身份漂移，外部幂等记录无法命中）。
/// </summary>
public enum ToolDispatchRecoveryDecision : byte
{
    /// <summary>本次为新调用（journal 新插入或既有 Prepared 已推进），应继续分派。</summary>
    Dispatch = 0,

    /// <summary>journal 已提交（Committed/ResultDelivered），应使用缓存结果，禁止重新分派。</summary>
    UseCachedResult = 1,

    /// <summary>
    /// journal 处于模糊状态（DispatchingIntent/Dispatched），外部副作用可能已发生，
    /// 需对账（携带 Journal 返回的 ExternalOperationId），禁止盲目重新分派。
    /// </summary>
    Reconcile = 2,

    /// <summary>journal 状态异常或身份冲突，应 fail-closed 拒绝执行。</summary>
    FailClosed = 3
}

/// <summary>
/// <see cref="IToolDispatchJournal.PrepareAsync"/> 返回值。
/// 描述 Prepare 后 Journal 的当前状态，供 <see cref="IDurableToolExecutor"/> 决策是否 Dispatch。
/// </summary>
/// <remarks>
/// <b>决策矩阵</b>：
/// <list type="bullet">
/// <item><see cref="ShouldDispatch"/>=true（Journal 不存在或 Prepared）→ 调用方应执行 Dispatch。</item>
/// <item><see cref="NeedsReconciliation"/>=true（Journal = DispatchingIntent 或 Dispatched）→ 调用方应返回对账结果（携带 <see cref="ExternalOperationId"/>），不重新 Dispatch。DispatchingIntent 表示外部调用可能已开始但未完成。</item>
/// <item><see cref="CachedResult"/> 非空（Journal = Committed/ResultDelivered）→ 调用方应直接返回缓存结果，<b>禁止重新 Dispatch</b>。</item>
/// </list>
/// <b>身份权威</b>：<see cref="RequestId"/> / <see cref="ExternalOperationId"/> /
/// <see cref="EffectiveIdempotencyKey"/> 是 Journal 返回的唯一生效身份。
/// 新插入时返回调用方派生的值；重放时返回既有条目持久化值——
/// 调用方后续只能使用这些值，不得重新生成（保证崩溃恢复后身份稳定）。
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

    /// <summary>与条目对应的稳定 RequestId（Journal 返回的唯一调用身份）。</summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// 生效的幂等键（Journal 返回；新插入返回调用方派生的值，重放返回既有条目持久化值）。
    /// 调用方后续只能使用此值——不能以恢复时重新生成的值覆盖。
    /// </summary>
    public string? EffectiveIdempotencyKey { get; init; }

    /// <summary>恢复决策（Dispatch / UseCachedResult / Reconcile / FailClosed）。</summary>
    public ToolDispatchRecoveryDecision RecoveryDecision { get; init; } = ToolDispatchRecoveryDecision.Dispatch;
}

/// <summary>
/// Tool 分派 journal 抽象。
/// 持久化 <see cref="ToolDispatchJournalEntry"/> 以支持 exactly-once tool 执行。
/// </summary>
/// <remarks>
/// <b>journal 是可选依赖</b>。未注入时执行器退回到仅进程内去重，
/// 不保证崩溃恢复的 exactly-once。生产部署应注入持久化实现（如基于 DB/WAL 的 journal）。
///
/// Journal 写入顺序（与 <see cref="ContextCore.Core.Services.AgentRunRuntime.DefaultDurableToolExecutor"/> 调用点对应）：
///   1. <see cref="PrepareWithIntentAsync"/>（Prepare + 前置 Intent 单次原子写）：
///      在调用 <see cref="IToolDispatcher.DispatchAsync"/> 之前。也可用
///      <see cref="PrepareAsync"/> + <see cref="MarkDispatchingIntentAsync"/> 两步完成等价流程。
///   2. <see cref="MarkDispatchedAsync"/>：tool 返回后、提交结果前。
///   3. <see cref="MarkCommittedAsync"/>：结果写入 durable result store 后。
///   4. <see cref="MarkResultDeliveredAsync"/>：结果成功送达后。
///
/// <b>expected-state 精确匹配（state = @expected）</b>。
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
/// <b>PrepareAsync 语义等价校验</b>。
/// <see cref="PrepareAsync"/> 对同一 request_id 重复写入时不再静默沿用旧记录：
/// 既有行必须与新条目在 <see cref="ToolDispatchJournalEntry.ToolName"/> /
/// <see cref="ToolDispatchJournalEntry.IdempotencyKey"/> /
/// <see cref="ToolDispatchJournalEntry.PayloadDigest"/> /
/// <see cref="ToolDispatchJournalEntry.WorkspaceId"/> /
/// <see cref="ToolDispatchJournalEntry.RunId"/> 上语义等价；
/// 否则抛 <see cref="InvalidOperationException"/>（RequestIdReuseDetected）。
///
/// <b>外部副作用 exactly-once 边界</b>。
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
    /// 指示 <see cref="MarkCommittedWithResultAsync"/> 是否在同事务内持久化 Tool 结果缓存。
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
    /// 返回值决策矩阵</b>：
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
    /// Prepare + 前置 Intent 合并为单次原子写。
    /// 在外部 Tool 调用发起前一次性写入条目并推进到 <see cref="ToolDispatchState.DispatchingIntent"/>，
    /// 替代"<see cref="PrepareAsync"/> + <see cref="MarkDispatchingIntentAsync"/> 两次往返"，
    /// 每 Tool 分派减少一次持久化写入，且 durable 边界与条目创建原子化（无 Prepared 空窗）。
    /// </summary>
    /// <param name="entry">journal 条目（State 为 Prepared 或 DispatchingIntent；入口状态不影响写入结果）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 返回值语义与 <see cref="PrepareAsync"/> 一致，但 <see cref="ToolDispatchPrepareResult.ShouldDispatch"/>=true
    /// 时保证 journal 已处于 DispatchingIntent（本次新插入，或既有 Prepared 前驱已原子推进）——
    /// 外部调用尚未开始，可安全执行 <see cref="IToolDispatcher.DispatchAsync"/>，无需再单独标记 Intent。
    /// 既有 DispatchingIntent/Dispatched（上次崩溃残留或并发分派）→ NeedsReconciliation；
    /// 既有 Committed/ResultDelivered → CachedResult（InMemory 自带缓存）或 ShouldDispatch=false（Postgres 由调用方查结果缓存）。
    /// 语义等价校验与 <see cref="PrepareAsync"/> 一致（RequestId 复用检测）。
    /// </remarks>
    ValueTask<ToolDispatchPrepareResult> PrepareWithIntentAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark that the external Tool call is about to start. Must be persisted BEFORE the actual external call.
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
    /// 将指定 RequestId 的状态推进到 Committed，并在<b>同一事务</b>内持久化 Tool 结果缓存。
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

    /// <summary>将指定 RequestId 的状态推进到 ResultDelivered（结果已送达）。</summary>
    /// <param name="requestId">Tool RequestId（必须已存在 Committed 条目）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">request_id 不存在（缺失 Committed 前驱）或当前 state ≥ ResultDelivered（逆退）。</exception>
    ValueTask MarkResultDeliveredAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将对账目标条目（DispatchingIntent/Dispatched 模糊态）显式推进到
    /// <see cref="ToolDispatchState.Reconciling"/>。
    /// </summary>
    /// <param name="requestId">Tool RequestId（必须已存在 DispatchingIntent 或 Dispatched 条目）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 进入 Reconciling 表示外部副作用真相正在确认（以 <see cref="ToolDispatchJournalEntry.ExternalOperationId"/>
    /// 查询外部系统 / 人工裁决）。已处于 Reconciling 或已超过（Committed/ResultDelivered）时幂等成功；
    /// 处于 Prepared（外部调用从未开始）或条目缺失时抛 <see cref="InvalidOperationException"/>
    /// （Prepared 无需对账——它应被重新 Dispatch 而非对账）。
    /// 对账确认后经 <see cref="MarkReconciledWithResultAsync"/> 提交；失败保持 Reconciling 等待重试或人工介入，
    /// 绝不静默重放外部副作用。
    /// </remarks>
    ValueTask BeginReconciliationAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 对账完成——将 <see cref="ToolDispatchState.Reconciling"/> 条目推进到
    /// <see cref="ToolDispatchState.Committed"/> 并在同一事务内写入对账得到的结果。
    /// </summary>
    /// <param name="requestId">Tool RequestId（必须已存在 Reconciling 条目）。</param>
    /// <param name="result">对账确认后的外部副作用结果（Succeeded=true 表示确认副作用已发生并取得结果）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 对账语义：外部副作用确实发生了（以 ExternalOperationId 在外部的实际执行情况为准），
    /// 将对账结果提交为最终真相，后续调用返回缓存结果、禁止重放。
    /// 持久化实现应保证状态推进与结果写入同事务（与 <see cref="MarkCommittedWithResultAsync"/> 一致）。
    /// </remarks>
    ValueTask MarkReconciledWithResultAsync(string requestId, DurableToolResult result, CancellationToken cancellationToken = default);

    /// <summary>查询指定 RequestId 的当前 journal 状态（用于恢复时判断）。</summary>
    /// <param name="requestId">Tool RequestId。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>journal 条目；不存在时返回 null（表示 tool 从未被调用，可安全重新执行）。</returns>
    ValueTask<ToolDispatchJournalEntry?> GetEntryAsync(string requestId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 持久化 Tool Dispatch Journal 抽象。
/// 继承 <see cref="IToolDispatchJournal"/> 并标记为持久化实现，用于崩溃恢复的 exactly-once 语义。
/// </summary>
/// <remarks>
/// 生产部署应注入基于 DB/WAL 的持久化实现（如 <c>PostgresToolDispatchJournal</c>）。
/// 开发环境可继续使用 <see cref="ContextCore.Core.Services.AgentKernel.InMemoryToolDispatchJournal"/>。
/// 由于继承自 <see cref="IToolDispatchJournal"/>，可直接注入 <see cref="ContextCore.Core.Services.AgentRunRuntime.DefaultDurableToolExecutor"/> 的
/// <c>IToolDispatchJournal</c> 参数，无需修改执行器构造签名。
/// </remarks>
public interface IPersistentToolDispatchJournal : IToolDispatchJournal
{
}

/// <summary>
/// 持久化 Agent Checkpoint Store 抽象。
/// 继承 <see cref="IAgentCheckpointStore"/> 并标记为持久化实现，用于崩溃恢复的 checkpoint 链。
/// </summary>
/// <remarks>
/// 生产部署应注入基于 DB/WAL 的持久化实现（如 <c>PostgresAgentCheckpointStore</c>）。
/// 开发环境可继续使用 <see cref="ContextCore.Core.Services.Agent.InMemoryAgentCheckpointStore"/>。
/// 由于继承自 <see cref="IAgentCheckpointStore"/>，可直接注入 <see cref="ContextCore.Core.Services.AgentRun.AgentRunActor"/> 的
/// <c>IAgentCheckpointStore</c> 参数，无需修改 Actor 构造签名。
/// </remarks>
public interface IPersistentAgentCheckpointStore : IAgentCheckpointStore
{
}

/// <summary>
/// Agent Checkpoint 工厂抽象。
/// 统一各 checkpoint 入口的状态格式，确保序列化完整的执行状态（已提交 tool 结果 + snapshot 引用）。
/// </summary>
/// <remarks>
/// <b>引入背景</b>：不同 checkpoint 入口曾直接使用不同 payload 作为 StateJson，
/// 导致恢复时无法可靠重建已提交的 tool 结果。引入工厂后所有 checkpoint 入口
/// 都产出同一 KernelCheckpointState 格式，恢复可靠重建幂等状态。
/// </remarks>
public interface IAgentCheckpointFactory
{
    /// <summary>从当前执行状态构建 checkpoint。</summary>
    /// <param name="checkpointId">Checkpoint 唯一 ID。</param>
    /// <param name="sessionId">当前 session。</param>
    /// <param name="workspaceId">当前 workspace。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>包含完整状态的 AgentCheckpoint。</returns>
    /// <remarks>
    /// 实现应序列化执行状态（含 CommittedResults + SnapshotId）到 <see cref="AgentCheckpoint.StateJson"/>，
    /// 并设置 <see cref="AgentCheckpoint.SnapshotId"/>（若存在）。
    /// </remarks>
    ValueTask<AgentCheckpoint> CreateCheckpointAsync(
        string checkpointId,
        string sessionId,
        string workspaceId,
        CancellationToken cancellationToken = default);
}
