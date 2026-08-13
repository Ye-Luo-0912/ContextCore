using System.Collections.Concurrent;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;

namespace ContextCore.Core.Services.AgentRunRuntime;

// 单个 Agent Run 的执行者（每个 Run 一个实例）。
// 顺序：ContextBuilding → 循环策略 → 调模型 → 校验/审批/执行工具 → 观察 → 检查点 → 下一轮。
// ContextBuilding 若注入了 IContextDecisionRuntime，直接调用它（不经过 HTTP 检索装饰器）。
// 从第二轮起把上一轮选中项作为 SeedWorkingSet；本轮仍搜索。
// 计划查询与成功观察的实体词写入 QueryTexts 分条检索；QueryText 只是诊断拼接。
// 未选中的不带入。Resident 写在 AgentRun 上，上下文构建成功后随 Run 快照落库；
// 模型调用前已持久化，调用中途取消或崩溃也能从 Run 恢复种子。
// 未注入模型通道或决策运行时时降级：无召回也可按策略调模型。
// 状态用 IAgentRunStore 的 CAS 推进；事件用 IAgentRunEventStore 哈希链追加。
// 工具走 IDurableToolExecutor，不直接调 IToolDispatcher。

/// <summary>
/// 单个 Agent Run 的执行者（per-run 实例）。
/// </summary>
/// <remarks>
/// 每个 Run 由独立的 Actor 实例执行；Actor 之间通过 <see cref="AgentKernelHost"/> 隔离。
/// Actor 持有运行时累积的可变状态（上下文 / 模型响应 / Tool 结果），Run 结束后丢弃。
/// </remarks>
public sealed class AgentRunActor
{
    private readonly IAgentRunStore _runStore;
    private readonly IAgentRunEventStore _eventStore;
    // 统一提交入口（可选）：非 null 时 Turn 批量提交走 IPersistentAgentRunCommitter
    // 单事务落库（事件 + 状态 CAS + checkpoint + 结算 outbox）；null 时回退 _eventStore.AppendBatchAsync
    // （测试 InMemory 装配等无提交器场景，行为等价）。
    private readonly IPersistentAgentRunCommitter? _committer;
    private readonly IAgentModelTransport? _modelTransport;
    private readonly IAgentLoopPolicy _loopPolicy;
    private readonly IToolDispatcher _toolDispatcher;
    private readonly IAgentToolCallValidator? _toolCallValidator;
    private readonly IAgentApprovalGate? _approvalGate;
    private readonly IAgentApprovalStore? _approvalStore;
    private readonly IAgentCheckpointFactory? _checkpointFactory;
    private readonly IContextDecisionRuntime? _decisionRuntime;
    // Checkpoint Store（保存 checkpoint 持久化）
    private readonly IAgentCheckpointStore? _checkpointStore;
    // Durable Tool Executor（封装 journal + dispatch）
    private readonly IDurableToolExecutor? _durableToolExecutor;
    // Tool 对账记录存储（null 时禁用"未裁决不完成"约束，仅 journal 自身保证模糊态不被重放）
    private readonly IToolReconciliationStore? _reconciliationStore;
    // 模型上下文投影器（从 WorkingSet.Materials 取正文 + Token 预算控制）
    private readonly IAgentModelContextProjector? _modelContextProjector;
    // Tool 定义列表（从 IToolCatalog 构建，用于原生 function calling 声明；
    // 未注入 Catalog 或实现无定义 → 空列表，模型不感知 Tool）。
    private IReadOnlyList<AgentToolDefinition> _toolDefinitions;  // mutable for AllowedToolIds filtering in ExecuteAsync
    // Tool 授权策略（null 时跳过授权快照校验——旧路径；快照存在但策略缺失则拒绝执行）。
    private readonly IToolAuthorizationPolicy? _toolAuthorizationPolicy;

    // 运行时累积状态（预算与计数，不在 AgentRunExecutionState 中，因为它们是 Run 的字段的可变副本）
    private int _currentTurn;
    // 模型调用次数计数（防止无限循环）
    private int _modelCallsUsed;
    // 当前执行期内的模型轮次计数（每次 ExecuteAsync 重置为 0）。
    // 用于 ComputeRequestId 的 modelTurn 参数，确保同一逻辑轮次在崩溃恢复后产生相同 RequestId
    // （_modelCallsUsed 是累积值，恢复后不重置，会导致同一逻辑轮次产生不同 modelTurn → 误重新 Dispatch）。
    private int _executionModelTurn;
    private AgentTurnBudget? _turnBudget;
    private AgentCostBudget? _costBudget;

    // Turn 内事件批量缓冲（替代每次单独 AppendAsync）
    private readonly List<AgentRunEvent> _pendingTurnEvents = new();
    // Turn 起始状态快照（用于批量提交时的 state CAS）
    private AgentRunState _turnStartState;
    // Turn 内最新 checkpoint（用于批量提交时的 checkpoint cursor）
    private AgentCheckpoint? _pendingTurnCheckpoint;
    // 批量提交阈值（超过则 mid-turn 强制 flush）
    private const int PendingEventsFlushThreshold = 32;
    // 强制 checkpoint 阈值 — 未 checkpoint 事件数达到此值时强制创建 checkpoint，
    // 防止事件流无限增长导致恢复时重放代价过大。
    private const int ForcedCheckpointEventThreshold = 1000;
    // 对账记录默认截止时长（ToolDescriptor.ReconciliationDeadline 未回传时的兜底）。
    private static readonly TimeSpan DefaultReconciliationDeadline = TimeSpan.FromHours(24);
    // 自适应检索规划输入的用途维度（与端点派生签名保持同一取值，保证反馈落到同一签名）。
    private const string AdaptiveAgentContextPurpose = "agent-context";
    // 事件恢复 keyset pagination 页大小（基于 sequence 索引的分页读取）。
    private const int RecoveryEventPageSize = 500;
    // 自上次 checkpoint 以来已 flush 的事件数（用于强制 checkpoint 阈值判断）。
    private int _eventsSinceLastCheckpoint;

    // 当前 Run 的 lease token 与 fencing token（由 AgentKernelHost 在 ExecuteAsync 时注入）。
    // 非空时 FlushPendingEventsAsync 将它们写入 AgentRunStateUpdate，由 Postgres 实现在
    // 状态 CAS + 事件追加的 WHERE 子句中校验 lease 仍由当前实例持有。
    // null = 无 lease 路径（测试 / 外部取消 / 恢复 Worker 等不持有 lease 的调用方）。
    private string? _leaseToken;
    private long? _fencingToken;

    // 实际租约过期时间提供器（由 AgentKernelHost 注入，读取共享心跳维护的 LastConfirmedExpiresTicks）。
    // Tool 副作用 fence 使用它替代 Run.DeadlineAt 推导值，让 fence 边界与数据库 lease_expires_at 一致。
    private Func<DateTimeOffset?>? _leaseExpiresAtProvider;

    // Recovery Integrity State：Host 选项（提供恢复退避参数；null 时用默认值）。
    private readonly AgentHostOptions? _hostOptions;
    // Recovery Integrity State：人工介入告警接收器（null = 不告警，best-effort 钩子）。
    private readonly IRecoveryAlertSink? _alertSink;
    // 正式方案：事件流压缩器（null = 非 Postgres provider，无快照/归档，走全量重放）。
    // Recovery 按 "Snapshot → validate anchor → replay hot delta" 从可恢复快照恢复折叠状态。
    private readonly IAgentRunEventCompactor? _eventCompactor;
    // 自适应检索规划器（规划输入 → 受控计划 → 决策请求 TokenBudget；
    // 执行后记录检索结果反馈，闭环学习信号）。null = 未注册（无自适应层，行为不变）。
    private readonly IAdaptiveRetrievalPlanner? _adaptivePlanner;

    /// <summary>本 Run 使用过的检索计划签名集合（延迟归因用；ExecuteAsync 开始清空）。</summary>
    private readonly List<string> _usedRetrievalSignatures = new();

    /// <summary>
    /// 重构：Agent Run 执行期状态（不可变记录，所有阶段方法返回新状态）。
    /// 统一管理 Run 元数据 / 结构化上下文 / 模型响应 / checkpoint / 事件序列与哈希链。
    /// </summary>
    private sealed record AgentRunExecutionState
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

    /// <summary>
    /// 构造 Agent Run Actor。
    /// </summary>
    /// <param name="runStore">Run 元数据存储。</param>
    /// <param name="eventStore">Run 事件流存储（哈希链）。</param>
    /// <param name="modelTransport">模型调用传输（null 时降级为仅 Tool 分派）。</param>
    /// <param name="loopPolicy">循环策略。</param>
    /// <param name="toolDispatcher">Tool 分派器（仅当 durableToolExecutor=null 时使用）。</param>
    /// <param name="toolCallValidator">Tool 校验器（null 时跳过校验）。</param>
    /// <param name="approvalGate">审批门（null 时跳过审批）。</param>
    /// <param name="checkpointFactory">检查点工厂（null 时跳过 checkpoint）。</param>
    /// <param name="decisionRuntime">Context Decision Runtime（null 时直接构造上下文）。</param>
    /// <param name="checkpointStore">Checkpoint Store（null 时跳过 SaveAsync）。</param>
    /// <param name="durableToolExecutor">Durable Tool Executor（null 时回退到 IToolDispatcher）。</param>
    /// <param name="modelContextProjector">模型上下文投影器（null 时回退到 AgentContextState.ProjectForModel）。</param>
    /// <param name="approvalStore">审批持久化存储（null 时由 Gate 内部处理；注入后 Actor 用正确 workspaceId 创建审批记录）。</param>
    /// <param name="reconciliationStore">Tool 对账记录存储（null 时跳过"未裁决不完成"约束）。</param>
    /// <param name="toolCatalog">Tool 目录（提供模型 function calling 声明；null 时回退到 toolDispatcher 的 IToolCatalog 实现，均无则空列表）。</param>
    /// <param name="hostOptions">Host 选项（提供恢复退避参数；null 时用默认值 30s base / 30min cap）。</param>
    /// <param name="alertSink">人工介入告警接收器（null = 不告警；best-effort 钩子）。</param>
    /// <param name="eventCompactor">
    /// 事件流压缩器（null = 非 Postgres provider；正式方案：Recovery 读取
    /// 可恢复快照 + 归档审计，按 "Snapshot → validate anchor → replay hot delta" 恢复）。
    /// </param>
    /// <param name="toolAuthorizationPolicy">
    /// Tool 授权策略（null = 无快照校验的旧路径；Run 已建立授权快照时缺失策略将拒绝执行）。
    /// </param>
    /// <param name="adaptivePlanner">
    /// 自适应检索规划器（null = 未注册，ContextBuilding 不应用自适应层）。
    /// </param>
    public AgentRunActor(
        IAgentRunStore runStore,
        IAgentRunEventStore eventStore,
        IAgentModelTransport? modelTransport,
        IAgentLoopPolicy loopPolicy,
        IToolDispatcher toolDispatcher,
        IAgentToolCallValidator? toolCallValidator = null,
        IAgentApprovalGate? approvalGate = null,
        IAgentApprovalStore? approvalStore = null,
        IAgentCheckpointFactory? checkpointFactory = null,
        IContextDecisionRuntime? decisionRuntime = null,
        IAgentCheckpointStore? checkpointStore = null,
        IDurableToolExecutor? durableToolExecutor = null,
        IAgentModelContextProjector? modelContextProjector = null,
        IToolReconciliationStore? reconciliationStore = null,
        IToolCatalog? toolCatalog = null,
        AgentHostOptions? hostOptions = null,
        IRecoveryAlertSink? alertSink = null,
        IAgentRunEventCompactor? eventCompactor = null,
        IToolAuthorizationPolicy? toolAuthorizationPolicy = null,
        IAdaptiveRetrievalPlanner? adaptivePlanner = null,
        IPersistentAgentRunCommitter? committer = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _committer = committer;
        _modelTransport = modelTransport;
        _loopPolicy = loopPolicy ?? throw new ArgumentNullException(nameof(loopPolicy));
        _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        _toolCallValidator = toolCallValidator;
        _approvalGate = approvalGate;
        _approvalStore = approvalStore;
        _checkpointFactory = checkpointFactory;
        _decisionRuntime = decisionRuntime;
        _checkpointStore = checkpointStore;
        _durableToolExecutor = durableToolExecutor;
        _modelContextProjector = modelContextProjector;
        _reconciliationStore = reconciliationStore;
        // 从 IToolCatalog 构建 Tool 定义（原生 function calling；与具体分派器解耦）。
        // 未注入 Catalog 时回退到 toolDispatcher 的 IToolCatalog 实现（如 RealToolDispatcher），
        // 两者均无定义（EchoToolDispatcher 等）→ 空列表，模型不感知 Tool。
        _toolDefinitions = toolCatalog?.GetToolDefinitions()
            ?? (toolDispatcher as IToolCatalog)?.GetToolDefinitions()
            ?? Array.Empty<AgentToolDefinition>();
        _modelCallsUsed = 0;
        _turnStartState = AgentRunState.Created;
        _hostOptions = hostOptions;
        _alertSink = alertSink;
        _eventCompactor = eventCompactor;
        _toolAuthorizationPolicy = toolAuthorizationPolicy;
        _adaptivePlanner = adaptivePlanner;
    }

    /// <summary>
    /// 执行 Agent Run 主循环，直到 Complete/Failed/Cancelled 或取消。
    /// </summary>
    /// <param name="run">待执行的 Run 元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="leaseToken">
    /// 可选 lease token，用于 fencing 校验。提供时（与 <paramref name="fencingToken"/> 同时提供），
    /// 所有副作用操作（状态 CAS / 事件追加）的 WHERE 子句将追加 lease_token + fencing_token 校验；
    /// lease 已被抢占时副作用失败，Actor 应中止。null = 无 lease 路径（测试 / 外部取消等）。
    /// </param>
    /// <param name="fencingToken">可选 fencing token，与 <paramref name="leaseToken"/> 配合使用。</param>
    /// <param name="leaseExpiresAtProvider">
    /// 可选实际租约过期时间提供器：每次 Tool fence 构造时调用，返回数据库 lease_expires_at
    /// （共享心跳续约后的最新确认值）。null = 无 lease 路径，fence 回退到 Run.DeadlineAt 推导。
    /// </param>
    /// <remarks>
    /// 运行时能力补齐 — Resume from checkpoint：
    /// 当 <paramref name="run"/>.State != Created 时判定为崩溃恢复场景。
    /// Actor 从事件流重建上下文（ToolObservations / EventSequence / EventChainHash），
    /// 并将本地状态规范化为 ContextBuilding（让 LoopPolicy 决定下一步：通常为 CallModel）。
    /// LastModelResponse 在 resume 时置为 null（事件流中不含完整模型响应内容），
    /// 强制重新调用模型以避免基于残缺状态做决策。durable journal 保证已分派 Tool 不会被重复执行。
    /// </remarks>
    public async Task ExecuteAsync(
        AgentRun run,
        CancellationToken cancellationToken = default,
        string? leaseToken = null,
        long? fencingToken = null,
        Func<DateTimeOffset?>? leaseExpiresAtProvider = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        // 保存 lease token 与 fencing token，供 FlushPendingEventsAsync 在批量提交时校验。
        // 两者必须同时为 null 或同时非 null（接口契约由调用方 AgentKernelHost 保证）。
        _leaseToken = leaseToken;
        _fencingToken = fencingToken;
        _leaseExpiresAtProvider = leaseExpiresAtProvider;

        // 每次执行开始清空检索计划签名集合（Run 隔离；延迟归因只归因本 Run 使用的签名）。
        _usedRetrievalSignatures.Clear();

        // 运行时能力补齐：检测 resume 场景
        // 全新启动状态集由 AgentRunStateSemantics 权威定义（RecoveryPolicy = NewStart：
        // Created / Queued / Claimed / Running / PendingAdmission / ClaimExpired / ScheduledLocally）——
        // 这些状态代表 Run 尚未产生任何持久化事件（首次 flush 才原子
        // CAS 到 ContextBuilding 并落库 RunCreated），必须走全新启动路径。
        // 其余非终态（ContextBuilding/ModelCalling/...）为崩溃恢复场景（resume）。
        var isResume = AgentRunStateSemantics.Get(run.State).RecoveryPolicy != AgentRunRecoveryPolicy.NewStart;

        // 锛氭寜 Run.AllowedToolIds 杩囨护妯″瀷鍙鐨?Tool Definitions锛堝湪妯″瀷璋冪敤鍓嶈繃婊わ級
        if (run.AllowedToolIds.Count > 0 && _toolDefinitions.Count > 0)
        {
            _toolDefinitions = _toolDefinitions
                .Where(t => run.AllowedToolIds.Contains(t.Name))
                .ToList();
        }

        // 重构：初始化 AgentRunExecutionState（统一管理执行期状态）
        // 用 AgentContextState 替代旧 List<AgentMessage> Messages，
        // CurrentTask 在初始化时设置为 run.Task，后续由 ProjectForModel 投影为 User 消息
        var state = new AgentRunExecutionState
        {
            Run = run,
            Context = new AgentContextState
            {
                CurrentTask = run.Task
            },
            LastModelResponse = null,
            LastCheckpoint = null,
            EventSequence = 0,
            EventChainHash = null
        };

        _turnBudget = run.TurnBudget;
        _costBudget = run.CostBudget;
        _currentTurn = run.Turn;
        // 从 Run 元数据恢复 ModelCallsUsed（支持崩溃恢复后续跑）
        _modelCallsUsed = run.ModelCallsUsed;
        // _executionModelTurn 先重置为 0，Resume 时由 RebuildStateFromEventsAsync
        // 从 ModelCallCompleted 事件流统计重建——避免恢复后从 0 重新计数导致 RequestId 改变。
        _executionModelTurn = 0;
        // 记录 Turn 起始状态，用于批量提交时的 state CAS
        // 运行时能力补齐：resume 时 _turnStartState = run.State（store 中的当前状态），
        // 后续 FlushPendingEventsAsync 的 CAS 以此为 expected state
        _turnStartState = run.State;
        _pendingTurnEvents.Clear();
        _pendingTurnCheckpoint = null;
        // 重置未 checkpoint 事件计数
        _eventsSinceLastCheckpoint = 0;

        if (isResume)
        {
            // Resume：从事件流重建上下文
            state = await RebuildStateFromEventsAsync(state, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 全新启动：记录 RunCreated 事件（审计起点）— 缓冲到 _pendingTurnEvents，待 Turn 结束批量提交
            if (run.RetryCount > 0)
            {
                // 不可变 Attempt：重试尝试在既有事件链上续写（不删除前序 Attempt 历史）。
                // 先写入 RunRetryScheduled（Attempt 边界锚点）+ AttemptStarted 标记，
                // 再续 RunCreated——恢复重放以最后一个 RunRetryScheduled 为界只重放当前 Attempt。
                state = await BeginRetryAttemptAsync(state, cancellationToken).ConfigureAwait(false);
            }
            state = BufferEvent(state, AgentRunEventType.RunCreated, JsonSerializer.Serialize(new
            {
                runId = run.RunId,
                sessionId = run.SessionId,
                task = run.Task
            }));
        }

        try
        {
            if (!isResume)
            {
                // 全新启动：Created → ContextBuilding（本地推进 + 缓冲 StateTransition 事件，CAS 延后到批量提交）
                state = TransitionStateLocal(state, AgentRunState.ContextBuilding);
            }
            // Resume 场景：RebuildStateFromEventsAsync 已将本地状态规范化为 ContextBuilding，
            // 无需再推进（避免重复 StateTransition 事件）

            // 主循环
            // Recovery Integrity State：进入恢复失败状态后立即退出执行槽。
            // RecoveryDependencyUnavailable 虽非终态（依赖恢复后由恢复 Worker 在退避门通过后
            // 重新入队执行），但 fail-closed 下不得在本次执行槽内继续推进——依赖不可用时执行
            // 任何 Agent 逻辑（调用模型 / 分派 Tool）都基于不可信上下文，可能重复外部副作用。
            while (!AgentRunStateMachine.IsTerminalState(state.Run.State)
                   && !AgentRunStateMachine.IsRecoveryFailureState(state.Run.State)
                   && !cancellationToken.IsCancellationRequested)
            {
                // 审批通过后从 PendingToolExecution 状态恢复——直接执行原 Tool，不重新调用模型。
                // PendingToolCommands 为列表，依次执行同轮所有未完成 Tool Call。
                if (state.Run.State == AgentRunState.PendingToolExecution && state.PendingToolCommands is { Count: > 0 })
                {
                    state = await ExecutePendingToolAsync(state, cancellationToken).ConfigureAwait(false);
                    // mid-turn 缓冲超过阈值时强制 flush
                    if (_pendingTurnEvents.Count >= PendingEventsFlushThreshold)
                    {
                        await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                // 重新读取最新 Run 状态（TransitionStateLocal 已推进本地副本）
                var decision = await _loopPolicy.DecideAsync(state.Run, state.LastModelResponse, cancellationToken).ConfigureAwait(false);

                switch (decision)
                {
                    case AgentLoopDecision.CallModel:
                        state = await CallModelAsync(state, cancellationToken).ConfigureAwait(false);
                        // mid-turn 缓冲超过阈值时强制 flush，避免长 Turn 内存膨胀
                        if (_pendingTurnEvents.Count >= PendingEventsFlushThreshold)
                        {
                            await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
                            // 强制 checkpoint 阈值检查 — 未 checkpoint 事件达阈值时记录警告。
                            // ModelCalling 状态无法直接进入 Checkpointing（状态机仅允许 Observing → Checkpointing），
                            // checkpoint 将在下一个 Observing 状态（DispatchToolsAsync Turn 结束）时创建并重置计数。
                            if (_eventsSinceLastCheckpoint >= ForcedCheckpointEventThreshold && _checkpointFactory is not null)
                            {
                                System.Diagnostics.Trace.TraceWarning(
                                    "[AgentRunActor] 未 checkpoint 事件数 ({0}) 达到强制阈值 ({1})，run={2}，状态={3}。" +
                                    "将在下一个 Observing 状态创建 checkpoint。",
                                    _eventsSinceLastCheckpoint, ForcedCheckpointEventThreshold, state.Run.RunId, state.Run.State);
                            }
                        }
                        break;

                    case AgentLoopDecision.DispatchTool:
                        state = await DispatchToolsAsync(state, cancellationToken).ConfigureAwait(false);
                        // 若审批挂起（DispatchToolsAsync 已 flush 并返回 AwaitingApproval 状态），
                        // 退出执行槽（释放 Worker/Semaphore/Lease）。Run 已持久化为 AwaitingApproval；
                        // 外部审批决策通过 POST /approvals/{approvalId} 端点提交，
                        // RecoveryWorker 会重新入队执行。
                        if (state.Run.State == AgentRunState.AwaitingApproval)
                        {
                            return;
                        }
                        break;

                    case AgentLoopDecision.Checkpoint:
                        state = await PersistCheckpointAsync(state, cancellationToken).ConfigureAwait(false);
                        break;

                    case AgentLoopDecision.Complete:
                        state = await CompleteAsync(state, cancellationToken).ConfigureAwait(false);
                        // 存在未裁决 Tool → CompleteAsync 已转为 AwaitingReconciliation 并持久化。
                        // 退出执行槽（释放 Worker/Semaphore/Lease），等待 ToolReconciliationWorker
                        // 对账全部完成后重新入队，Actor 恢复执行。
                        if (state.Run.State == AgentRunState.AwaitingReconciliation)
                        {
                            return;
                        }
                        break;

                    case AgentLoopDecision.Fail:
                        await FailAsync(state, "Loop policy decided to fail.", cancellationToken).ConfigureAwait(false);
                        return;

                    default:
                        await FailAsync(state, $"Unknown loop decision: {decision}", cancellationToken).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外部取消 → 转 Cancelled
            // 阶段方法可能已缓冲事件但未返回新 state（抛异常时 state 赋值未完成），
            // 需从 _pendingTurnEvents 重新同步 EventSequence / EventChainHash，否则
            // FailAsync/TryTransitionToCancelledAsync 会用陈旧的序列号生成重复 Sequence 事件，
            // 导致 AppendBatchAsync 校验失败（被 catch 吞掉 → 事件丢失 + Run 状态不推进）。
            state = ResyncStateFromPendingEvents(state);
            await TryTransitionToCancelledAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 任意异常 → 转 Failed
            // 同上：阶段方法抛异常时 state 赋值未完成，需从 _pendingTurnEvents 重新同步。
            state = ResyncStateFromPendingEvents(state);
            state = await FailAsync(state, ex.ToString(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 延迟归因：Run 到达终态时，把工具观察得到的质量信号归因到本 Run
            // 用过的检索计划签名。没有工具观察则不归因，避免用打分器分数冒充准不准。
            await RecordDeferredAttributionAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 延迟归因：Run 终态时把工具成功率归因到本 Run 用过的检索计划签名。
    /// 没有工具观察则不写。幂等键 (runId, signature)，重试/重放不重复归因。
    /// </summary>
    private async Task RecordDeferredAttributionAsync(
        AgentRunExecutionState state,
        CancellationToken cancellationToken)
    {
        if (_adaptivePlanner is null || _usedRetrievalSignatures.Count == 0)
        {
            return;
        }

        try
        {
            var finalState = state.Run.State;
            // 仅终态归因：RetryPending / Awaiting* 非终态还有后续 Attempt / 对账，不归因。
            if (!AgentRunStateMachine.IsTerminalState(finalState))
            {
                return;
            }

            var quality = AgentTurnSearchQuery.ToolEvidence(state.Context.ToolObservations);
            if (!quality.Effective)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var signature in _usedRetrievalSignatures.Distinct(StringComparer.Ordinal))
            {
                await _adaptivePlanner.RecordOutcomeAsync(new RetrievalPlanFeedback
                {
                    PlanSignature = signature,
                    WorkspaceId = state.Run.WorkspaceId,
                    CollectionId = state.Run.ResolveContextCollectionId(),
                    Purpose = AdaptiveAgentContextPurpose,
                    QueryText = string.Empty,
                    HitsReturned = 0,
                    Effective = true,
                    RecordedAtUtc = now,
                    Source = RetrievalFeedbackSource.AutomatedEvaluation,
                    Confidence = quality.Confidence,
                    OutcomeQuality = quality.Quality,
                    // 幂等：Run 重试 / 恢复重放不产生重复归因反馈。
                    IdempotencyKey = $"run:{state.Run.RunId}:{signature}"
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[AgentRunActor] Deferred retrieval attribution failed for run {0}: {1}", state.Run.RunId, ex.Message);
        }
        finally
        {
            _usedRetrievalSignatures.Clear();
        }
    }

    /// <summary>
    /// 不可变 Attempt：重试尝试开始时从事件流尾部续写 RunRetryScheduled + AttemptStarted 标记。
    /// </summary>
    /// <remarks>
    /// 重试（RetryCount &gt; 0）不再删除前序 Attempt 的事件历史（不可变审计）：
    /// 当前 Attempt 的事件链紧随前序 Attempt 的尾部事件续写。首次 flush 时
    /// AppendBatchAsync 校验首事件 Sequence = 当前 MAX(sequence) + 1，
    /// 因此必须先从事件流读取尾部（Sequence + ContentHash）作为续写锚点。
    /// RunRetryScheduled 同时是恢复重放的 Attempt 边界锚点（见 RebuildStateFromEventsAsync）。
    /// </remarks>
    /// <param name="state">当前执行状态（Run.State 为 Claimed，事件缓冲为空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已续写两个标记事件（RunRetryScheduled + AttemptStarted）的执行状态。</returns>
    private async Task<AgentRunExecutionState> BeginRetryAttemptAsync(
        AgentRunExecutionState state,
        CancellationToken cancellationToken)
    {
        // 尾部读取失败非致命：后续首次 flush 的 AppendBatchAsync 会以 Sequence 不连续
        // 显式失败（fail-closed），不会静默产生错误链。
        try
        {
            var lastSeq = await _eventStore.GetLastSequenceAsync(
                state.Run.WorkspaceId, state.Run.RunId, cancellationToken).ConfigureAwait(false);
            if (lastSeq >= 0)
            {
                var tail = await _eventStore.ReadAsync(
                    state.Run.WorkspaceId, state.Run.RunId, lastSeq, 1, cancellationToken).ConfigureAwait(false);
                if (tail.Count == 1)
                {
                    // 续写锚点：下一事件 Sequence = 尾部 + 1，PrevChainHash = 尾部 ContentHash。
                    state = state with
                    {
                        EventSequence = tail[0].Sequence + 1,
                        EventChainHash = tail[0].ContentHash
                    };
                }
            }
        }
        catch
        {
            // 读取失败：保持当前 EventSequence（0）——首次 flush 会因链不连续显式失败。
        }

        var attempt = state.Run.RetryCount + 1;
        state = BufferEvent(state, AgentRunEventType.RunRetryScheduled, JsonSerializer.Serialize(new
        {
            attempt,
            retryCount = state.Run.RetryCount,
            scheduledAt = DateTimeOffset.UtcNow
        }));
        state = BufferEvent(state, AgentRunEventType.AttemptStarted, JsonSerializer.Serialize(new
        {
            attempt,
            retryCount = state.Run.RetryCount
        }));
        return state;
    }

    /// <summary>
    /// 运行时能力补齐：从事件流重建 AgentRunExecutionState（崩溃恢复 / resume）。
    /// </summary>
    /// <param name="state">初始执行状态（Run + 默认 Context）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重建后的执行状态（含 ToolObservations / EventSequence / EventChainHash）。</returns>
    /// <remarks>
    /// <b>重建策略</b>（优先级从高到低）：
    /// <list type="bullet">
    /// <item>可恢复快照（正式方案）：存在可解析的 Recoverable Snapshot 且覆盖范围
    /// 超过重放起点时，按 "Snapshot → validate anchor → replay hot delta" 恢复——
    /// 快照还原折叠前缀 [0..anchor] 的状态（对话流 / 工具观察 / 模型轮次 / Pending 命令），
    /// 校验热表锚点 ContentHash == 快照 ChainHeadHash，再重放锚点后的热表增量事件。</item>
    /// <item>存在 Checkpoint Cursor 且 checkpoint 本体 metadata 完整时走快路径：
    /// 从 checkpoint 还原对话流 / 工具观察 / 模型轮次，仅重放游标之后的新事件。</item>
    /// <item>否则读取 Run 的完整事件流（按 Sequence 升序），从事件重建全部状态。</item>
    /// <item>从 ToolCallCompleted 事件解析 ToolObservation（含 output / error / succeeded / toolName / toolCallId）。</item>
    /// <item>EventSequence / EventChainHash 从最后一个事件恢复（保证后续事件哈希链连续）。</item>
    /// <item>LastModelResponse 置为 null（事件流中不含完整模型响应内容），强制重新调用模型。</item>
    /// <item>本地状态规范化为 ContextBuilding（LoopPolicy 会决定 CallModel）。</item>
    /// </list>
    /// 
    /// <b>状态一致性</b>：
    /// - 本地状态（ContextBuilding）用于状态机校验（TransitionStateLocal）。
    /// - Store 状态（run.State）用于 CAS（_turnStartState = run.State）。
    /// - 两者可以不同：本地状态决定状态机校验是否通过，store 状态决定 CAS 是否匹配。
    /// - 首次 FlushPendingEventsAsync 时 CAS 从 store 状态推进到新状态（store 不校验状态机流转）。
    /// 
    /// <b>幂等性保证</b>：
    /// 重新调用模型后若返回相同 ToolCalls，IDurableToolExecutor 通过 journal 保证
    /// 已 commit 的 Tool 不会被重复执行（返回缓存结果）。
    /// 
    /// <b>降级处理</b>：
    /// 快路径/快照路径读取失败时降级为全量事件重放；若事件流为空（崩溃发生在首次 flush 之前）
    /// 或无事件可读，回退为全新启动路径。快照缺失/不可解析（旧格式）时，压缩过的热表
    /// 会由全量重放 fail-closed 判定 RecoveryCorrupted（安全，需运维介入）。
    /// </remarks>
    private async Task<AgentRunExecutionState> RebuildStateFromEventsAsync(
        AgentRunExecutionState state,
        CancellationToken cancellationToken)
    {
        // 获取最新 Checkpoint Cursor（失败非致命：降级为全量事件重放）
        AgentCheckpointCursor? cursor = null;
        try
        {
            cursor = await _eventStore.GetCheckpointCursorAsync(
                state.Run.WorkspaceId, state.Run.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cursor 读取失败非致命
        }

        // 尝试从 checkpoint 本体还原可恢复状态（对话流 / 工具观察 / 模型轮次）。
        // 仅当存在 Cursor、checkpoint 本体可读且 metadata 完整时才启用
        // "从游标断点续读"快路径（只重放 checkpoint 之后的新事件）；
        // 否则降级为全量事件重放（兼容旧 checkpoint 与无 checkpoint 场景）。
        var restoredContext = await TryRestoreCheckpointContextAsync(
            cursor, state.Run, cancellationToken).ConfigureAwait(false);
        var useCursorFastPath = restoredContext is not null;

        // Keyset pagination：快路径从游标之后开始读，全量路径从 0 开始
        var allEvents = new List<AgentRunEvent>();
        var fromSequence = useCursorFastPath ? cursor!.LastEventSequence + 1 : 0;

        // 不可变 Attempt（重试不删历史）：以最后一个 RunRetryScheduled 事件为 Attempt 边界，
        // 只重放当前 Attempt 的事件（Sequence > 边界）——前序 Attempt 的对话/工具观察
        // 已由全新启动清空，若连同重放会用旧 Attempt 上下文污染当前 Attempt。
        var attemptBoundaryStart = 0;
        try
        {
            var attemptBoundary = await _eventStore.GetAttemptBoundarySequenceAsync(
                state.Run.WorkspaceId, state.Run.RunId, cancellationToken).ConfigureAwait(false);
            if (attemptBoundary >= 0)
            {
                attemptBoundaryStart = attemptBoundary + 1;
                if (attemptBoundaryStart > fromSequence)
                {
                    fromSequence = attemptBoundaryStart;
                }
            }
        }
        catch
        {
            // 边界查询失败：交由后续事件流读取路径判定（存储不可用 → RecoveryDependencyUnavailable）。
        }

        string? expectedPrevChainHash = null;
        AgentRunEvent? boundaryEvent = null;
        var readFailed = false;
        // 区分恢复失败原因：事件数据损坏（哈希链/序列号/ContentHash） vs 存储不可用。
        var recoveryCorruptionDetected = false;

        // ── 可恢复快照探测（正式方案）────────────────────────────
        // 折叠前缀已归档到 agent_run_events_archive，热表只保留锚点 + 增量事件，
        // Recovery 不能依赖"从 Sequence 0 全量重放"。存在可解析的 Recoverable
        // Snapshot 且覆盖范围超过当前重放起点（fromSequence）时，启用快照路径：
        //   Snapshot → validate anchor → replay hot delta。
        // 忽略快照的场景：
        // - 快照不可解析（旧格式仅序列化锚点事件 / 损坏）→ 降级为现有恢复路径
        //   （压缩过的热表会 fail-closed 判定 RecoveryCorrupted，安全，需运维介入）；
        // - 快照覆盖范围落后于 attempt 边界 / checkpoint 游标（fromSequence 更晚）
        //   → 保留 cursor/attempt 路径，防止旧 Attempt 上下文污染当前 Attempt。
        // 快照读取失败（存储不可用）非致命：降级为现有恢复路径。
        var snapshotRestore = await TryRestoreSnapshotStateAsync(state.Run, cancellationToken).ConfigureAwait(false);
        var recoverableState = snapshotRestore?.State;
        // 快照记录链头（agent_run_event_snapshots.chain_head_hash）是锚点校验的权威基准；
        // state_json 内嵌 ChainHeadHash 与其在同一事务写入，二者一致（不一致视为快照损坏）。
        var snapshotChainHeadHash = snapshotRestore?.ChainHeadHash;
        var useSnapshotPath = recoverableState is not null && fromSequence < recoverableState.Sequence + 1;
        if (useSnapshotPath)
        {
            // 快照优先于 checkpoint 快路径：cursor 指向的事件可能已被归档（< 锚点），
            // 快照本身已覆盖折叠历史，无需 checkpoint metadata 还原。
            useCursorFastPath = false;
            restoredContext = null;
            fromSequence = recoverableState!.Sequence + 1;
            expectedPrevChainHash = snapshotChainHeadHash;
        }

        while (true)
        {
            try
            {
                // 从 fromSequence - 1 读取哈希链锚点事件（快路径 = checkpoint 游标指向的事件；
                // 不可变 Attempt 边界 = RunRetryScheduled 事件；快照路径 = 快照锚点事件）——
                // 首个重放事件的 PrevChainHash 必须等于锚点 ContentHash（跨 Attempt 哈希链连续）。
                if (fromSequence > 0)
                {
                    var boundaryPage = await _eventStore.ReadAsync(
                        state.Run.WorkspaceId, state.Run.RunId,
                        fromSequence: fromSequence - 1, take: 1, cancellationToken).ConfigureAwait(false);
                    if (boundaryPage.Count == 1)
                    {
                        boundaryEvent = boundaryPage[0];
                        expectedPrevChainHash = boundaryEvent.ContentHash;

                        // 快照锚点校验（Snapshot → validate anchor）：热表锚点事件的
                        // ContentHash 必须与快照记录链头（chain_head_hash 列）一致——
                        // 锚点被替换/篡改时立即判定 RecoveryCorrupted（快照记录与归档
                        // 同一事务写入，是权威基准）。
                        if (useSnapshotPath
                            && !string.Equals(boundaryEvent.ContentHash, snapshotChainHeadHash, StringComparison.Ordinal))
                        {
                            throw new AgentRunRecoveryCorruptionException(
                                $"快照锚点哈希校验失败：热表锚点事件 sequence={boundaryEvent.Sequence} 的 " +
                                $"ContentHash 与快照 ChainHeadHash 不一致（run={state.Run.RunId}）。");
                        }
                    }
                    else
                    {
                        // 锚点事件不存在（数据异常）→ 降级为全量重放
                        throw new AgentRunRecoveryCorruptionException(
                            $"重放锚点事件不存在（sequence={fromSequence - 1}，run={state.Run.RunId}）。");
                    }
                }

                while (true)
                {
                    var page = await _eventStore.ReadAsync(
                        state.Run.WorkspaceId, state.Run.RunId,
                        fromSequence: fromSequence, take: RecoveryEventPageSize, cancellationToken)
                        .ConfigureAwait(false);

                    if (page.Count == 0)
                    {
                        break;
                    }

                    for (var i = 0; i < page.Count; i++)
                    {
                        var evt = page[i];
                        var expectedSequence = fromSequence + i;

                        // 重算 ContentHash 校验（fail-closed）：不信任存储的 ContentHash，
                        // 每次恢复读取都按事件内容重算比对，检测篡改/损坏。
                        if (!AgentRunEventChain.VerifyContentHash(evt))
                        {
                            throw new AgentRunRecoveryCorruptionException(
                                $"事件 ContentHash 校验失败：sequence={evt.Sequence} 重算哈希与存储值不匹配（run={state.Run.RunId}）。");
                        }
                        if (evt.Sequence != expectedSequence)
                        {
                            throw new AgentRunRecoveryCorruptionException(
                                $"事件序列号不连续：期望 {expectedSequence}，实际 {evt.Sequence}（run={state.Run.RunId}）。");
                        }
                        var expectedHash = (i == 0) ? expectedPrevChainHash : page[i - 1].ContentHash;
                        if (!string.Equals(evt.PrevChainHash, expectedHash, StringComparison.Ordinal))
                        {
                            throw new AgentRunRecoveryCorruptionException(
                                $"事件哈希链断裂：sequence={evt.Sequence} 的 PrevChainHash 与前一事件 ContentHash 不匹配（run={state.Run.RunId}）。");
                        }
                    }

                    allEvents.AddRange(page);
                    expectedPrevChainHash = page[page.Count - 1].ContentHash;
                    fromSequence += page.Count;

                    if (page.Count < RecoveryEventPageSize)
                    {
                        break;
                    }
                }
                break;
            }
            catch (Exception ex)
            {
                // 事件流读取失败（store 不可用 / 跨 workspace 不可见 / 哈希链断裂 / 内容损坏）。
                // 记录失败类型：损坏异常 → RecoveryCorrupted；其余 → RecoveryDependencyUnavailable。
                // 快路径先降级为全量重放重试一次；仍失败则进入恢复失败终态（fail-closed，
                // 不回退为全新启动——回退可能重复已执行的外部副作用）。
                recoveryCorruptionDetected = ex is AgentRunRecoveryCorruptionException;
                if (useCursorFastPath)
                {
                    useCursorFastPath = false;
                    restoredContext = null;
                    allEvents.Clear();
                    // 降级为全量重放：仍受不可变 Attempt 边界约束（跳过前序 Attempt 事件）。
                    fromSequence = attemptBoundaryStart;
                    expectedPrevChainHash = null;
                    boundaryEvent = null;
                    continue;
                }
                readFailed = true;
                break;
            }
        }

        if (readFailed)
        {
            // fail-closed：恢复读取失败不得回退为全新启动（可能重复外部副作用）。
            // 事件数据损坏 → RecoveryCorrupted；存储不可用 → RecoveryDependencyUnavailable。
            var recoveryState = recoveryCorruptionDetected
                ? AgentRunState.RecoveryCorrupted
                : AgentRunState.RecoveryDependencyUnavailable;
            return await EnterRecoveryFailureStateAsync(state, recoveryState, cancellationToken).ConfigureAwait(false);
        }

        // 快照路径：快照已覆盖锚点之前的折叠历史，仅需重放锚点后的热表增量事件
        // （Snapshot → validate anchor → replay hot delta）。
        if (useSnapshotPath)
        {
            return BuildResumedStateFromSnapshot(state, recoverableState!, boundaryEvent!, allEvents);
        }

        // 快路径：checkpoint 已覆盖游标之前的历史，仅需重放游标之后的新事件
        if (useCursorFastPath)
        {
            return BuildResumedStateFromCheckpoint(
                state, restoredContext!.Value, boundaryEvent!, allEvents);
        }

        if (allEvents.Count == 0)
        {
            // fail-closed：仅「Run 确实处于 Created 且从未写入任何事件」允许回退为全新启动。
            // 本方法仅在 isResume（run.State != Created）时被调用，因此零事件意味着
            // Run 已推进过状态但事件流为空（事件数据丢失）→ 不得回退为全新启动，
            // 标记 RecoveryBlocked 等待运维介入（回退可能重复已执行的外部副作用）。
            return await EnterRecoveryFailureStateAsync(state, AgentRunState.RecoveryBlocked, cancellationToken).ConfigureAwait(false);
        }

        // 从 Cursor 初始化 _eventsSinceLastCheckpoint。
        // cursor.LastEventSequence 是已 checkpoint 的最后事件序列号（0-based），
        // 未 checkpoint 事件数 = 总事件数 - (lastCheckpointedSequence + 1)。
        // 无 Cursor 时全部事件视为未 checkpoint（首次恢复或 InMemory 无游标）。
        if (cursor is not null)
        {
            var checkpointedCount = cursor.LastEventSequence + 1;
            _eventsSinceLastCheckpoint = allEvents.Count > checkpointedCount
                ? allEvents.Count - checkpointedCount
                : 0;
        }
        else
        {
            _eventsSinceLastCheckpoint = allEvents.Count;
        }

        var events = allEvents;

        // 从事件流按时间顺序无损重建 Conversation（Assistant + Tool 消息）。
        // ModelCallCompleted 事件携带完整模型响应（content + toolCalls[]），重建 Assistant 消息；
        // ToolCallCompleted 事件携带 Tool 执行结果，重建 Tool 消息。
        // 按事件 Sequence 顺序遍历，保证 "assistant tool_calls → tool result" 因果顺序。
        // 旧事件缺少字段时跳过对应重建（向后兼容）；单事件解析失败不影响整体恢复。
        var toolObservations = new List<ToolObservation>();
        var rebuiltConversation = new List<AgentMessage>();
        foreach (var evt in events)
        {
            AgentRunEventStateRebuilder.RebuildFromEvent(evt, rebuiltConversation, toolObservations);
        }

        // 从最后一个事件恢复 EventSequence / EventChainHash（保证哈希链连续）
        var lastEvent = events[events.Count - 1];

        // 从事件流统计 ModelCallCompleted 重建 _executionModelTurn，
        // 避免恢复后从 0 重新计数导致 RequestId 改变（Journal 无法识别原调用）。
        _executionModelTurn = AgentRunEventStateRebuilder.RebuildExecutionModelTurn(events);

        // 从 PendingToolExecution / ToolDispatching 状态恢复——不重新调用模型，
        // 而是重放原 Tool（审批路径从 ApprovalRequested 提取；Kill 中断路径从
        // ToolCallStarted 提取，原始 ModelTurnRevision 保证 RequestId 与 journal 一致）。
        // 审批 API 在裁决时将 Run 状态推进到 PendingToolExecution（批准）或 Failed（拒绝）；
        // ToolDispatching 是 Tool 执行中途被 Kill 时持久化的状态。
        // 此处仅处理 PendingToolExecution/ToolDispatching（Failed 已是终态，不会进入 resume）。
        if (state.Run.State == AgentRunState.PendingToolExecution || state.Run.State == AgentRunState.ToolDispatching)
        {
            var pendingCommands = AgentRunEventStateRebuilder.ExtractPendingToolCommands(events)
                ?? AgentRunEventStateRebuilder.ExtractPendingCommandsFromToolCallStarted(events);
            if (pendingCommands is { Count: > 0 })
            {
                // 保持 PendingToolExecution 状态（主循环检测后直接执行原 Tool）
                var pendingRun = state.Run with { State = AgentRunState.PendingToolExecution };
                return state with
                {
                    Run = pendingRun,
                    Context = new AgentContextState
                    {
                        CurrentTask = state.Run.Task,
                        Messages = new List<AgentMessage>(),
                        ToolObservations = toolObservations,
                        Conversation = rebuiltConversation,
                        StableMemoryReferences = new List<MemoryReference>(),
                        LastModelTurn = null
                    },
                    LastModelResponse = null,
                    LastDecisionResult = null,
                    EventSequence = lastEvent.Sequence + 1,
                    EventChainHash = lastEvent.ContentHash,
                    PendingToolCommands = pendingCommands
                };
            }
            // PendingToolCommands 提取失败（事件 payload 损坏/缺失）→ 降级为 ContextBuilding 重新调用模型
        }

        // 本地状态规范化为 ContextBuilding：
        // - LoopPolicy 在 lastModelResponse=null 时返回 CallModel
        // - CallModelAsync 的 TransitionStateLocal(ContextBuilding, ModelCalling) 合法
        // - Store 状态（run.State）仍为崩溃时的状态，首次 CAS 以此为 expected state
        var resumedRun = state.Run with { State = AgentRunState.ContextBuilding };

        return state with
        {
            Run = resumedRun,
            Context = new AgentContextState
            {
                CurrentTask = state.Run.Task,
                Messages = new List<AgentMessage>(),
                ToolObservations = toolObservations,
                Conversation = rebuiltConversation,
                StableMemoryReferences = new List<MemoryReference>(),
                LastModelTurn = null
            },
            LastModelResponse = null,
            EventSequence = lastEvent.Sequence + 1,
            EventChainHash = lastEvent.ContentHash
        };
    }

    /// <summary>
    /// 进入恢复失败状态（fail-closed）并尽力持久化。
    /// </summary>
    /// <param name="state">当前执行状态。</param>
    /// <param name="recoveryState">恢复失败状态（RecoveryBlocked / RecoveryCorrupted / RecoveryDependencyUnavailable）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已标记恢复失败状态的执行状态（本地为恢复失败状态，主循环不会执行）。</returns>
    /// <remarks>
    /// 恢复失败路径不向事件流追加事件：事件流本身可能已损坏或不可用，追加 StateTransition
    /// 事件需要读取真实尾事件作为序列/哈希链锚点，而依赖不可用时无法读取；且向待运维修复的
    /// 事件流写入会引入更多不确定状态。因此采用状态直写（与 RecoveryWorker / Endpoints 的
    /// "无事件状态推进"路径一致）：run store 的 state CAS（expected = _turnStartState）+
    /// 字段更新（FailureReason / RecoveryAttempt / NextRetryAtUtc）。持久化失败（典型场景：
    /// RecoveryDependencyUnavailable 时存储本身不可用）时静默降级并记录警告——Run 保持原
    /// 非终态，由 RecoveryWorker 在依赖恢复后重试恢复；不抛给 FailAsync，避免把恢复失败
    /// 误标为 Failed 而丢失 Recovery* 语义。
    /// 
    /// Recovery Integrity State：
    /// - RecoveryBlocked / RecoveryCorrupted 为终态（数据损坏，等待运维介入），每次进入均告警；
    /// - RecoveryDependencyUnavailable 可重试：按指数退避（base × 2^(attempt-1)，封顶 cap）
    /// 计算 <see cref="AgentRun.NextRetryAtUtc"/>，递增 <see cref="AgentRun.RecoveryAttempt"/>，
    /// 由 Recovery Worker 在退避门通过后重新入队；仅首次（attempt==1）告警避免告警风暴。
    /// </remarks>
    private async Task<AgentRunExecutionState> EnterRecoveryFailureStateAsync(
        AgentRunExecutionState state,
        AgentRunState recoveryState,
        CancellationToken cancellationToken,
        string? reasonOverride = null)
    {
        var failureReason = reasonOverride ?? recoveryState switch
        {
            AgentRunState.RecoveryBlocked => "RecoveryBlocked：事件流为空（事件数据丢失），无法安全重建执行状态，需运维介入。",
            AgentRunState.RecoveryCorrupted => "RecoveryCorrupted：事件流损坏（哈希链断裂 / 序列不连续 / ContentHash 重算不匹配），需运维介入。",
            _ => "RecoveryDependencyUnavailable：事件存储不可用，等待依赖恢复后由恢复 Worker 重试。"
        };

        // 退避重试：仅可重试的恢复状态（依赖暂时不可用，非数据损坏）计算退避。
        // 可重试性统一来自 AgentRunStateSemantics（RecoveryDependencyUnavailable 可重试；
        // RecoveryBlocked / RecoveryCorrupted 为终态，不计算退避、不自动重试）。
        var isRetryable = AgentRunStateSemantics.Get(recoveryState).Retryable;
        var recoveryAttempt = isRetryable ? state.Run.RecoveryAttempt + 1 : 0;
        DateTimeOffset? nextRetryAtUtc = null;
        if (isRetryable)
        {
            var backoffBase = _hostOptions?.RetryBackoffBase > TimeSpan.Zero
                ? _hostOptions.RetryBackoffBase
                : TimeSpan.FromSeconds(30);
            var backoffCap = _hostOptions?.RetryBackoffMax > TimeSpan.Zero
                ? _hostOptions.RetryBackoffMax
                : TimeSpan.FromMinutes(30);
            var baseDelay = backoffBase > backoffCap ? backoffCap : backoffBase;
            // attempt 从 1 开始：首次失败等待 1×base，与 Durable Scheduler 的重试退避语义对齐
            // （retry_count 从 0 起算时同样 base × 2^(retry_count)）。
            var delay = baseDelay * Math.Pow(2, Math.Max(0, recoveryAttempt - 1));
            if (delay > backoffCap)
            {
                delay = backoffCap;
            }
            nextRetryAtUtc = DateTimeOffset.UtcNow + delay;
        }

        // 本地推进为恢复失败状态（不缓冲事件）：主循环据此退出，不执行任何 Agent 逻辑。
        state = TransitionStateLocal(state, recoveryState, bufferEvent: false);
        try
        {
            await _runStore.TransitionStateAsync(
                state.Run.WorkspaceId, state.Run.RunId, _turnStartState, recoveryState,
                cancellationToken, _leaseToken, _fencingToken).ConfigureAwait(false);
            // RecoveryAttempt / NextRetryAtUtc 仅写入 jsonb（data 全量序列化），无独立列、无迁移。
            await _runStore.UpdateAsync(
                state.Run with
                {
                    FailureReason = failureReason,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    RecoveryAttempt = isRetryable ? recoveryAttempt : state.Run.RecoveryAttempt,
                    NextRetryAtUtc = nextRetryAtUtc
                },
                cancellationToken).ConfigureAwait(false);
            _turnStartState = recoveryState;

            // 人工介入告警（best-effort，仅在持久化成功后投递）：
            // - RecoveryBlocked / RecoveryCorrupted：数据损坏级事件，每次进入均告警。
            // - RecoveryDependencyUnavailable：仅首次（RecoveryAttempt == 1）告警，
            // 持续不可用时只记日志，避免告警风暴；依赖长期不恢复仍由运维巡检发现。
            var alertKind = recoveryState switch
            {
                AgentRunState.RecoveryBlocked => AgentRunAlertKind.RecoveryBlocked,
                AgentRunState.RecoveryCorrupted => AgentRunAlertKind.RecoveryCorrupted,
                _ => AgentRunAlertKind.RecoveryDependencyUnavailable
            };
            var shouldAlert = _alertSink is not null
                && (alertKind != AgentRunAlertKind.RecoveryDependencyUnavailable || recoveryAttempt == 1);
            if (shouldAlert)
            {
                var alert = new AgentRunAlert
                {
                    RunId = state.Run.RunId,
                    WorkspaceId = state.Run.WorkspaceId,
                    SessionId = state.Run.SessionId,
                    Kind = alertKind,
                    Reason = failureReason,
                    Attempt = alertKind == AgentRunAlertKind.RecoveryDependencyUnavailable ? recoveryAttempt : 0
                };
                try
                {
                    await _alertSink!.NotifyInterventionRequiredAsync(alert, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception alertEx)
                {
                    // best-effort：告警投递失败不阻断执行（catch + log）。
                    System.Diagnostics.Trace.TraceWarning(
                        "[AgentRunActor] 投递人工介入告警 {0} 失败（run={1}，workspace={2}）：{3}。",
                        alertKind, state.Run.RunId, state.Run.WorkspaceId, alertEx.Message);
                }
            }
        }
        catch (Exception ex)
        {
            // 尽力而为：无法持久化恢复失败状态时记录警告，Run 保持原非终态等待重试。
            System.Diagnostics.Trace.TraceWarning(
                "[AgentRunActor] 持久化恢复失败状态 {0} 失败（run={1}，workspace={2}）：{3}。" +
                "Run 保持原状态，将由 RecoveryWorker 在依赖恢复后重试恢复。",
                recoveryState, state.Run.RunId, state.Run.WorkspaceId, ex.Message);
        }
        return state;
    }

    /// <summary>
    /// 从 checkpoint 本体还原可恢复的执行状态（对话流 / 工具观察 / 模型轮次）。
    /// </summary>
    /// <returns>
    /// 还原成功返回三元组；Cursor 缺失、checkpoint 本体不可读或 metadata 不完整时返回 null
    /// （调用方降级为全量事件重放）。审批恢复路径（PendingToolExecution）不走快路径，
    /// 因为 PendingToolCommands 需从 ApprovalRequested 事件提取，事件可能早于游标。
    /// </returns>
    private async Task<(List<AgentMessage> Conversation, List<ToolObservation> ToolObservations, int ExecutionModelTurn)?> TryRestoreCheckpointContextAsync(
        AgentCheckpointCursor? cursor,
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (cursor is null || _checkpointStore is null || run.State == AgentRunState.PendingToolExecution)
        {
            return null;
        }

        AgentCheckpoint? checkpoint;
        try
        {
            checkpoint = await _checkpointStore.GetAsync(
                cursor.WorkspaceId, cursor.CheckpointId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // checkpoint 本体不可读（存储不可用 / 已被清理）→ 降级为全量重放
            return null;
        }

        if (checkpoint?.Metadata is null
            || !checkpoint.Metadata.TryGetValue("conversationJson", out var conversationJson)
            || string.IsNullOrWhiteSpace(conversationJson)
            || !checkpoint.Metadata.TryGetValue("toolObservationsJson", out var observationsJson)
            || string.IsNullOrWhiteSpace(observationsJson)
            || !checkpoint.Metadata.TryGetValue("executionModelTurn", out var turnText)
            || !int.TryParse(turnText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var executionModelTurn))
        {
            // 旧 checkpoint 未携带完整 metadata → 降级为全量重放
            return null;
        }

        try
        {
            var conversation = JsonSerializer.Deserialize<List<AgentMessage>>(conversationJson);
            var observations = JsonSerializer.Deserialize<List<ToolObservation>>(observationsJson);
            if (conversation is null || observations is null)
            {
                return null;
            }
            return (conversation, observations, executionModelTurn);
        }
        catch (JsonException)
        {
            // metadata 损坏 → 降级为全量重放
            return null;
        }
    }

    /// <summary>
    /// 从 checkpoint 还原的状态 + 游标之后的新事件构建恢复状态（快路径）。
    /// </summary>
    private AgentRunExecutionState BuildResumedStateFromCheckpoint(
        AgentRunExecutionState state,
        (List<AgentMessage> Conversation, List<ToolObservation> ToolObservations, int ExecutionModelTurn) restored,
        AgentRunEvent boundaryEvent,
        IReadOnlyList<AgentRunEvent> newEvents)
    {
        // checkpoint 覆盖游标之前的历史；在此基础上追加游标之后的新事件
        var conversation = new List<AgentMessage>(restored.Conversation);
        var toolObservations = new List<ToolObservation>(restored.ToolObservations);
        foreach (var evt in newEvents)
        {
            AgentRunEventStateRebuilder.RebuildFromEvent(evt, conversation, toolObservations);
        }

        _executionModelTurn = restored.ExecutionModelTurn;
        // 本次读取的事件均为未 checkpoint 事件
        _eventsSinceLastCheckpoint = newEvents.Count;

        // 最后事件：有新事件取最后一个；否则取游标指向的事件（无新事件时即最后事件）
        var lastEvent = newEvents.Count > 0 ? newEvents[^1] : boundaryEvent;

        // 本地状态规范化为 ContextBuilding（快路径已排除 PendingToolExecution）
        var resumedRun = state.Run with { State = AgentRunState.ContextBuilding };

        return state with
        {
            Run = resumedRun,
            Context = new AgentContextState
            {
                CurrentTask = state.Run.Task,
                Messages = new List<AgentMessage>(),
                ToolObservations = toolObservations,
                Conversation = conversation,
                StableMemoryReferences = new List<MemoryReference>(),
                LastModelTurn = null
            },
            LastModelResponse = null,
            LastDecisionResult = null,
            EventSequence = lastEvent.Sequence + 1,
            EventChainHash = lastEvent.ContentHash
        };
    }

    /// <summary>
    /// 尝试从事件流压缩器读取可恢复快照（正式方案）。
    /// </summary>
    /// <returns>
    /// 快照可解析为 <see cref="AgentRunRecoverableState"/> 时返回 (状态, 快照记录链头)；
    /// 以下场景返回 null（调用方降级为现有恢复路径）：
    /// <list type="bullet">
    /// <item>压缩器未注入（非 Postgres provider，无快照/归档）；</item>
    /// <item>快照读取失败（存储不可用）——非致命，降级为现有恢复路径；</item>
    /// <item>快照不存在（从未压缩）或 state_json 为空；</item>
    /// <item>旧格式快照（仅序列化锚点事件，缺少可恢复状态成员，无法解析）或 JSON 损坏——
    /// 压缩过的热表会由后续全量重放 fail-closed 判定 RecoveryCorrupted（安全，需运维介入）。</item>
    /// </list>
    /// </returns>
    private async Task<(AgentRunRecoverableState State, string? ChainHeadHash)?> TryRestoreSnapshotStateAsync(
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (_eventCompactor is null)
        {
            return null;
        }

        AgentRunEventSnapshot? snapshot;
        try
        {
            snapshot = await _eventCompactor.GetSnapshotAsync(
                run.WorkspaceId, run.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 快照读取失败非致命：降级为现有恢复路径（存储不可用由后续事件流读取判定）。
            return null;
        }

        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.StateJson))
        {
            return null;
        }

        var state = AgentRunEventStateRebuilder.TryDeserialize(snapshot.StateJson);
        if (state is null)
        {
            return null;
        }

        return (state, snapshot.ChainHeadHash);
    }

    /// <summary>
    /// 从可恢复快照还原的状态 + 锚点后的热表增量事件构建恢复状态（快照快路径）。
    /// </summary>
    /// <remarks>
    /// 正式方案（Snapshot → validate anchor → replay hot delta）的最后一步：
    /// 快照覆盖折叠前缀 [0..anchor] 的重建状态（Conversation / ToolObservations /
    /// ExecutionModelTurn / PendingToolCommands），在此之上重放锚点后的热表增量事件；
    /// 锚点一致性已由调用方（RebuildStateFromEventsAsync 边界读取）按 ChainHeadHash 校验。
    /// 审批恢复（PendingToolExecution）优先从增量事件提取更新的 ApprovalRequested，
    /// 增量无审批事件时回退到快照保存的 PendingToolCommands。
    /// </remarks>
    private AgentRunExecutionState BuildResumedStateFromSnapshot(
        AgentRunExecutionState state,
        AgentRunRecoverableState recoverable,
        AgentRunEvent boundaryEvent,
        IReadOnlyList<AgentRunEvent> deltaEvents)
    {
        // 快照覆盖锚点之前的折叠历史；在此基础上重放锚点后的热表增量事件
        var conversation = new List<AgentMessage>(recoverable.Conversation);
        var toolObservations = new List<ToolObservation>(recoverable.ToolObservations);
        foreach (var evt in deltaEvents)
        {
            AgentRunEventStateRebuilder.RebuildFromEvent(evt, conversation, toolObservations);
        }

        // _executionModelTurn：快照折叠值 + 增量事件的最大值（增量内嵌更高轮次时取高，
        // 避免恢复后 RequestId 回退导致 Journal 无法识别原调用）。
        _executionModelTurn = AgentRunEventStateRebuilder.RebuildExecutionModelTurn(
            deltaEvents, recoverable.ExecutionModelTurn);
        // 本次读取的事件均为快照之后的新事件（未计入快照折叠范围）
        _eventsSinceLastCheckpoint = deltaEvents.Count;

        // 最后事件：有新事件取最后一个；否则取锚点事件（无增量时即最后事件）
        var lastEvent = deltaEvents.Count > 0 ? deltaEvents[^1] : boundaryEvent;

        // 审批/执行中断恢复：PendingToolExecution/ToolDispatching 状态时优先从增量事件提取
        // （更新的 ApprovalRequested / ToolCallStarted），增量无结果时回退到快照中保存的
        // PendingToolCommands（折叠前缀已归档）。
        if (state.Run.State == AgentRunState.PendingToolExecution || state.Run.State == AgentRunState.ToolDispatching)
        {
            var pendingCommands = AgentRunEventStateRebuilder.ExtractPendingToolCommands(deltaEvents)
                ?? recoverable.PendingToolCommands
                ?? AgentRunEventStateRebuilder.ExtractPendingCommandsFromToolCallStarted(deltaEvents);
            if (pendingCommands is { Count: > 0 })
            {
                // 保持 PendingToolExecution 状态（主循环检测后直接执行原 Tool）
                var pendingRun = state.Run with { State = AgentRunState.PendingToolExecution };
                return state with
                {
                    Run = pendingRun,
                    Context = new AgentContextState
                    {
                        CurrentTask = state.Run.Task,
                        Messages = new List<AgentMessage>(),
                        ToolObservations = toolObservations,
                        Conversation = conversation,
                        StableMemoryReferences = new List<MemoryReference>(),
                        LastModelTurn = null
                    },
                    LastModelResponse = null,
                    LastDecisionResult = null,
                    EventSequence = lastEvent.Sequence + 1,
                    EventChainHash = lastEvent.ContentHash,
                    PendingToolCommands = pendingCommands
                };
            }
            // PendingToolCommands 提取失败（增量与快照均无）→ 降级为 ContextBuilding 重新调用模型
        }

        // 本地状态规范化为 ContextBuilding（与 checkpoint 快路径语义一致）
        var resumedRun = state.Run with { State = AgentRunState.ContextBuilding };

        return state with
        {
            Run = resumedRun,
            Context = new AgentContextState
            {
                CurrentTask = state.Run.Task,
                Messages = new List<AgentMessage>(),
                ToolObservations = toolObservations,
                Conversation = conversation,
                StableMemoryReferences = new List<MemoryReference>(),
                LastModelTurn = null
            },
            LastModelResponse = null,
            LastDecisionResult = null,
            EventSequence = lastEvent.Sequence + 1,
            EventChainHash = lastEvent.ContentHash
        };
    }

    /// <summary>
    /// 直接执行审批通过后的所有 Pending Tool（不重新调用模型）。
    /// 从 <see cref="AgentRunExecutionState.PendingToolCommands"/> 依次提取完整 Tool 调用信息，
    /// 通过 <see cref="IDurableToolExecutor"/>（或回退到 <see cref="IToolDispatcher"/>）执行，
    /// 记录 ToolCallStarted/Completed/ObservationAppended 事件，然后进入 Observing 继续循环。
    /// </summary>
    /// <remarks>
    /// ToolCallStarted 事件必须在外部执行前持久化（先日志后执行）。
    /// 确保被批准的 Tool 确定性执行：审批前 Actor 已退出，模型上下文已丢失；
    /// 此处不重置为 ContextBuilding 重新调用模型，而是直接执行 ApprovalRequested 事件中保存的 Tool。
    /// </remarks>
    private async Task<AgentRunExecutionState> ExecutePendingToolAsync(
        AgentRunExecutionState state,
        CancellationToken cancellationToken)
    {
        var pendingCommands = state.PendingToolCommands
            ?? throw new InvalidOperationException(
                "PendingToolExecution 状态下 PendingToolCommands 为 null（状态不一致）。");

        // 首项为被审批的 Tool（已通过审批），直接执行。
        // 后续项为同轮未处理的 Tool Call，必须逐个走独立校验+审批流程，
        // 不能因首项审批通过而隐式批准后续命令。
        for (var cmdIndex = 0; cmdIndex < pendingCommands.Count; cmdIndex++)
        {
            var pendingCommand = pendingCommands[cmdIndex];
            var toolCall = new AgentToolCallRequest
            {
                ToolName = pendingCommand.ToolName,
                Arguments = pendingCommand.ArgumentsJson,
                IdempotencyKey = pendingCommand.IdempotencyKey,
                ToolCallId = pendingCommand.ToolCallId
            };

            // 授权快照校验（安全边界，覆盖全部 pending 命令——含已审批首项：
            // 审批通过到执行之间的窗口内快照可能过期/策略漂移，一律 fail-closed）。
            var (authorized, authorizationDenial, authorizationReason) = IsToolAuthorizedBySnapshot(state.Run, toolCall.ToolName ?? string.Empty);
            if (!authorized)
            {
                if (authorizationDenial == ToolAuthorizationDenial.SnapshotInvalid)
                {
                    return await FailAsync(state, authorizationReason, cancellationToken).ConfigureAwait(false);
                }

                state = BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
                    toolCallId: pendingCommand.ToolCallId,
                    requestId: null,
                    toolName: pendingCommand.ToolName,
                    idempotencyKey: pendingCommand.IdempotencyKey,
                    sideEffect: ToolSideEffect.Unknown.ToString(),
                    externalOperationId: null,
                    journalState: ToolDispatchState.Prepared.ToString(),
                    succeeded: false,
                    output: null,
                    error: authorizationReason,
                    durationMs: 0)));
                continue;
            }

            // 后续 Tool Call（非首项）必须走独立校验+审批流程
            if (cmdIndex > 0)
            {
                // AllowedToolIds 检查
                if (state.Run.AllowedToolIds.Count > 0
                    && !string.IsNullOrWhiteSpace(toolCall.ToolName)
                    && !state.Run.AllowedToolIds.Contains(toolCall.ToolName))
                {
                    state = BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
                        toolCallId: pendingCommand.ToolCallId,
                        requestId: null,
                        toolName: pendingCommand.ToolName,
                        idempotencyKey: pendingCommand.IdempotencyKey,
                        sideEffect: ToolSideEffect.Unknown.ToString(),
                        externalOperationId: null,
                        journalState: ToolDispatchState.Prepared.ToString(),
                        succeeded: false,
                        output: null,
                        error: $"Tool '{pendingCommand.ToolName}' 不在 Run.AllowedToolIds 白名单中，已被 Run 约束拒绝。",
                        durationMs: 0)));
                    continue;
                }

                // Tool Schema 校验
                if (_toolCallValidator is not null)
                {
                    var validation = await _toolCallValidator.ValidateAsync(state.Run.WorkspaceId, state.Run.RunId, toolCall, cancellationToken).ConfigureAwait(false);
                    if (!validation.IsValid)
                    {
                        state = BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
                            toolCallId: pendingCommand.ToolCallId,
                            requestId: null,
                            toolName: pendingCommand.ToolName,
                            idempotencyKey: pendingCommand.IdempotencyKey,
                            sideEffect: ToolSideEffect.Unknown.ToString(),
                            externalOperationId: null,
                            journalState: ToolDispatchState.Prepared.ToString(),
                            succeeded: false,
                            output: null,
                            error: validation.Error ?? "Validation failed",
                            durationMs: 0)));
                        continue;
                    }

                    // 后续 Tool Call 需要独立审批——不能因首项审批通过而隐式批准
                    if (validation.RequiresApproval && _approvalGate is not null)
                    {
                        state = TransitionStateLocal(state, AgentRunState.AwaitingApproval);

                        // 构建新的 PendingToolCommands：当前后续 Tool + 剩余未处理的 Tool
                        var newPendingCommands = new List<PendingToolCommand> { pendingCommand };
                        for (var j = cmdIndex + 1; j < pendingCommands.Count; j++)
                        {
                            newPendingCommands.Add(pendingCommands[j]);
                        }

                        state = BufferEvent(state, AgentRunEventType.ApprovalRequested, JsonSerializer.Serialize(new
                        {
                            toolName = pendingCommand.ToolName,
                            reason = validation.ApprovalReason,
                            pendingToolCommand = pendingCommand,
                            pendingToolCommands = newPendingCommands
                        }));

                        var approval = await _approvalGate.RequestApprovalAsync(
                            state.Run.WorkspaceId, state.Run.RunId, toolCall, cancellationToken).ConfigureAwait(false);

                        if (approval.PendingApproval)
                        {
                            var effectiveApprovalId = approval.ApprovalId ?? pendingCommand.ToolCallId;
                            state = BufferEvent(state, AgentRunEventType.ApprovalResolved, JsonSerializer.Serialize(new
                            {
                                approved = false,
                                pending = true,
                                approvalId = effectiveApprovalId,
                                approverId = (string?)null,
                                rejectionReason = (string?)null
                            }));

                            await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

                            // 保存剩余 PendingToolCommands（含当前需审批的 Tool），供恢复后执行
                            state = state with { PendingToolCommands = newPendingCommands };
                            return state;
                        }

                        state = BufferEvent(state, AgentRunEventType.ApprovalResolved, JsonSerializer.Serialize(new
                        {
                            approved = approval.Approved,
                            approverId = approval.ApproverId,
                            rejectionReason = approval.RejectionReason
                        }));

                        if (!approval.Approved)
                        {
                            // 审批拒绝 → 跳过此 Tool，继续处理下一个
                            state = TransitionStateLocal(state, AgentRunState.PendingToolExecution);
                            continue;
                        }

                        // 审批通过 → 转回 PendingToolExecution 继续执行此 Tool
                        state = TransitionStateLocal(state, AgentRunState.PendingToolExecution);
                    }
                }
            }

            // 先确定 RequestId 并持久化 ToolCallStarted 事件，再执行外部 Tool。
            // 优先使用 PendingToolCommand 持久化的 RequestId（崩溃恢复/审批恢复路径）——
            // 滚动升级改变派生算法后，历史 Run 的 journal 仍以原 RequestId 寻址；
            // 无持久化值时重新派生（新调用，workspaceId 参与哈希，跨工作区互不冲突）。
            var requestId = (_durableToolExecutor is not null)
                ? pendingCommand.RequestId
                  ?? DefaultDurableToolExecutor.ComputeRequestId(state.Run.WorkspaceId, state.Run.RunId, toolCall, pendingCommand.ModelTurnRevision)
                : pendingCommand.ToolCallId;

            // ToolCallStarted 携带 arguments + modelTurnRevision：
            // 进程在 Tool 执行中被 Kill 时，恢复节点据此重建原 PendingToolCommand（原始轮次），
            // RequestId 与 journal 条目一致 → durable 去重生效，不重复执行外部副作用。
            state = BufferEvent(state, AgentRunEventType.ToolCallStarted, JsonSerializer.Serialize(new
            {
                toolName = pendingCommand.ToolName,
                toolCallId = pendingCommand.ToolCallId,
                requestId = requestId,
                idempotencyKey = pendingCommand.IdempotencyKey,
                arguments = pendingCommand.ArgumentsJson,
                modelTurnRevision = pendingCommand.ModelTurnRevision,
                resumedFromApproval = cmdIndex == 0
            }));

            // flush 持久化 ToolCallStarted 后再执行外部 Tool（先日志后执行）。
            await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

            // 执行 Tool（复用 DurableToolExecutor 或回退到直接 Dispatcher）
            ToolExecutionResult? toolResult = null;
            if (_durableToolExecutor is not null)
            {
                // 构造 leaseFence 并传入，保护 Tool 副作用边界。
                // ExpiresAt 使用实际租约过期时间（共享心跳续约后的最新确认值），
                // 与数据库 lease_expires_at 一致；无 lease 时回退到 Run.DeadlineAt 保守推导。
                var leaseFence1 = (_leaseToken is not null && _fencingToken is not null)
                    ? new AgentLeaseFence
                      {
                          LeaseToken = _leaseToken,
                          FencingToken = _fencingToken.Value,
                          ExpiresAt = _leaseExpiresAtProvider?.Invoke()
                              ?? state.Run.DeadlineAt ?? DateTimeOffset.UtcNow.AddMinutes(5)
                      }
                    : null;
                toolResult = await _durableToolExecutor.ExecuteAsync(
                    state.Run.RunId, state.Run.WorkspaceId, toolCall, pendingCommand.ModelTurnRevision,
                    cancellationToken, leaseFence1, state.Run.DeadlineAt,
                    approvalGranted: true,
                    requestIdOverride: pendingCommand.RequestId).ConfigureAwait(false);
            }
            else
            {
                // 回退路径：直接调 IToolDispatcher（无 journal，无 durable 保证）
                var dispatchResult = await _toolDispatcher.DispatchAsync(new ToolDispatchRequest
                {
                    ToolName = toolCall.ToolName ?? string.Empty,
                    Payload = toolCall.Arguments ?? string.Empty,
                    RequestId = pendingCommand.ToolCallId,
                    IdempotencyKey = toolCall.IdempotencyKey,
                    WorkspaceId = state.Run.WorkspaceId,
                    RunId = state.Run.RunId
                }, cancellationToken).ConfigureAwait(false);

                toolResult = new ToolExecutionResult
                {
                    RequestId = pendingCommand.ToolCallId,
                    IdempotencyKey = toolCall.IdempotencyKey,
                    SideEffect = dispatchResult.SideEffect,
                    ExternalOperationId = dispatchResult.ExternalOperationId,
                    JournalState = ToolDispatchState.Committed,
                    Result = dispatchResult.Result,
                    Succeeded = dispatchResult.Succeeded,
                    Error = dispatchResult.Error,
                    Duration = dispatchResult.Duration
                };
            }

            // 记录 ToolCallCompleted
            state = BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
                toolCallId: pendingCommand.ToolCallId,
                requestId: toolResult.RequestId,
                toolName: pendingCommand.ToolName,
                idempotencyKey: toolResult.IdempotencyKey,
                sideEffect: toolResult.SideEffect.ToString(),
                externalOperationId: toolResult.ExternalOperationId,
                journalState: toolResult.JournalState.ToString(),
                succeeded: toolResult.Succeeded,
                output: toolResult.Result,
                error: toolResult.Error,
                durationMs: toolResult.Duration.TotalMilliseconds)));

            // Journal 处于模糊状态（DispatchingIntent/Dispatched/Reconciling）→ 创建对账记录。
            // 只要 Run 存在未裁决记录，CompleteAsync 就不得推进到 Completed（等待 Worker/人工裁决）。
            await EnsureReconciliationRecordAsync(state, toolResult, pendingCommand.ToolName, cancellationToken).ConfigureAwait(false);

            // 观察结果
            var observation = toolResult.Succeeded
                ? $"{toolResult.Result}"
                : $"[ERROR] {toolResult.Error}";

            var resumedObservation = new ToolObservation
            {
                ToolName = pendingCommand.ToolName,
                ToolCallId = pendingCommand.ToolCallId,
                Result = toolResult.Result,
                Error = toolResult.Error,
                Succeeded = toolResult.Succeeded
            };
            state.Context.ToolObservations.Add(resumedObservation);
            // 同步追加到统一对话流（审批恢复路径同样需要保持因果顺序）。
            state.Context.Conversation.Add(resumedObservation.ToAgentMessage());

            state = BufferEvent(state, AgentRunEventType.ObservationAppended, JsonSerializer.Serialize(new
            {
                toolName = pendingCommand.ToolName,
                observationLength = observation.Length
            }));

            // mid-loop 缓冲超过阈值时强制 flush
            if (_pendingTurnEvents.Count >= PendingEventsFlushThreshold)
            {
                await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
            }
        }

        // 所有 Pending Tool 执行完成 → Observing（本地推进）
        state = TransitionStateLocal(state, AgentRunState.Observing);

        // Checkpointing（若有工厂）→ ContextBuilding（下一轮）
        // 强制 checkpoint 阈值由 _eventsSinceLastCheckpoint 跟踪：Turn 结束的 checkpoint 会重置计数。
        if (_checkpointFactory is not null)
        {
            state = await PersistCheckpointAsync(state, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (_eventsSinceLastCheckpoint >= ForcedCheckpointEventThreshold)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[AgentRunActor] 未 checkpoint 事件数 ({0}) 达到强制阈值 ({1})，run={2}，但无 checkpoint factory 配置。",
                    _eventsSinceLastCheckpoint, ForcedCheckpointEventThreshold, state.Run.RunId);
            }
            state = TransitionStateLocal(state, AgentRunState.ContextBuilding);
        }

        // Turn 结束 → 批量提交所有缓冲事件 + state CAS（单事务）
        await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

        // 清除 PendingToolCommands（已全部执行完成）
        return state with { PendingToolCommands = null };
    }

    /// <summary>
    /// 从 <see cref="_pendingTurnEvents"/> 重新同步 <paramref name="state"/> 的
    /// <see cref="AgentRunExecutionState.EventSequence"/> / <see cref="AgentRunExecutionState.EventChainHash"/>
    /// 以及 <see cref="AgentRun.State"/>。
    /// </summary>
    /// <remarks>
    /// 当阶段方法（<see cref="CallModelAsync"/> / <see cref="DispatchToolsAsync"/> 等）抛异常时，
    /// <c>state = await MethodAsync(state, ...)</c> 的赋值未完成，catch 块中的 <paramref name="state"/>
    /// 是阶段方法调用前的陈旧副本。但 <see cref="_pendingTurnEvents"/> 已被阶段方法修改（追加了事件）。
    /// 若不重新同步，<see cref="FailAsync"/> / <see cref="TryTransitionToCancelledAsync"/> 会用陈旧的
    /// <see cref="AgentRunExecutionState.EventSequence"/> 生成重复 Sequence 的事件，导致
    /// <see cref="IAgentRunEventStore.AppendBatchAsync"/> 校验失败（Sequence 不连续）。
    /// </remarks>
    private AgentRunExecutionState ResyncStateFromPendingEvents(AgentRunExecutionState state)
    {
        if (_pendingTurnEvents.Count == 0)
        {
            // 无缓冲事件 → state 未被阶段方法修改（EventSequence / EventChainHash 已正确），
            // 不能重置为 0（之前 Turn 的事件可能已 flush 持久化，新事件必须从当前 EventSequence 继续）。
            return state;
        }

        var lastEvent = _pendingTurnEvents[^1];
        return state with
        {
            EventSequence = lastEvent.Sequence + 1,
            EventChainHash = lastEvent.ContentHash,
            Run = state.Run with { State = lastEvent.State }
        };
    }

    /// <summary>执行 CallModel 阶段。</summary>
    private async Task<AgentRunExecutionState> CallModelAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        // 在调用模型前检查 DeadlineAt（超时则 Fail，替代旧路径中 StartRunAsync 返回后立即 Dispose 的 linked CTS）。
        // 旧路径 linked CTS 在 HTTP 请求结束时被 Dispose，导致 Actor 收到 ObjectDisposedException；
        // 新路径由 Run.DeadlineAt 字段承载超时控制，Actor 在每次模型调用前检查。
        if (state.Run.DeadlineAt is not null && DateTimeOffset.UtcNow > state.Run.DeadlineAt)
        {
            return await FailAsync(state,
                $"Run 超时：已超过执行截止时间（DeadlineAt={state.Run.DeadlineAt:O}）。",
                cancellationToken).ConfigureAwait(false);
        }

        // 进入 ModelCalling（本地推进 + 缓冲 StateTransition 事件，CAS 延后到批量提交）
        state = TransitionStateLocal(state, AgentRunState.ModelCalling);

        // 构建结构化上下文（首次追加 User(run.Task) + 可选 System(decisionContext)；后续轮次复用 Messages）。
        // fail-closed：仅 Ready / OptionalRetrievalDegraded 允许继续调用模型；
        // 其余状态终止本轮——模型绝不能在缺失 mandatory 上下文时运行。
        var (contextBuildStatus, contextBuiltState) = await BuildContextAsync(state, cancellationToken).ConfigureAwait(false);
        state = contextBuiltState;
        switch (contextBuildStatus)
        {
            case AgentContextBuildStatus.Ready:
            case AgentContextBuildStatus.OptionalRetrievalDegraded:
                break;
            case AgentContextBuildStatus.SafetyBlocked:
                state = await EnterSafetyBlockedStateAsync(state,
                    "安全阻断：mandatory / hard constraint 上下文缺失或不可用，模型不得在缺失 mandatory 上下文时运行。",
                    cancellationToken).ConfigureAwait(false);
                return state;
            case AgentContextBuildStatus.DependencyUnavailable:
                state = await EnterRecoveryFailureStateAsync(state, AgentRunState.RecoveryDependencyUnavailable, cancellationToken,
                    "决策依赖不可用：Decision Runtime 执行异常，本轮终止，等待依赖恢复后重试。").ConfigureAwait(false);
                return state;
            case AgentContextBuildStatus.BudgetUnsatisfiable:
                return await FailAsync(state,
                    "预算不可满足：mandatory 上下文经精确 tokenize 后仍超出模型上下文窗口，Run 失败（需人工介入调整预算或任务）。",
                    cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"未知上下文构建状态：{contextBuildStatus}");
        }

        // 上下文构建成功后立即持久化当前 Run：此时已缓冲 RunCreated / StateTransition 事件，
        // 连同带 Resident 的 Run 快照一并提交。模型第一次调用中途取消或崩溃时，
        // store 上已有本轮种子，新 Actor 可直接从 Run 恢复，不必等 Turn 正常结束。
        await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

        // 模型传输未注入 → 降级：直接产出空响应，进入下一轮决策
        if (_modelTransport is null)
        {
            var degradedResponse = new AgentModelResponse
            {
                Content = string.Empty,
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 0,
                Duration = TimeSpan.Zero
            };
            // 同步更新 Context.LastModelTurn 和 LastModelResponse（后者供 IAgentLoopPolicy 使用）
            // degraded 响应无 ToolCalls，NormalizedToolCalls 置空避免上一轮残留。
            state = state with
            {
                LastModelResponse = degradedResponse,
                Context = state.Context with { LastModelTurn = degradedResponse },
                NormalizedToolCalls = null
            };

            BufferEvent(state, AgentRunEventType.ModelCallCompleted, JsonSerializer.Serialize(new
            {
                mode = "degraded",
                reason = "IAgentModelTransport not injected"
            }));

            // 跳过 Tool 分派 → 直接尝试 Complete
            return TransitionStateLocal(state, AgentRunState.ContextBuilding);
        }

        // 由 IAgentModelContextProjector 投影最终模型输入（从 WorkingSet.Materials 取正文 + Token 预算控制）。
        // 未注入投影器时回退到 AgentContextState.ProjectForModel（旧路径，无 Material 正文）。
        // tokenBudget 使用 Run.ModelContextTokenBudget（默认 8192）；0 或负数 = 不限制。
        var tokenBudget = state.Run.ModelContextTokenBudget;
        IReadOnlyList<AgentMessage> projectedMessages;
        if (_modelContextProjector is not null)
        {
            var projection = _modelContextProjector.Project(
                state.Run, state.LastDecisionResult, state.Context, tokenBudget);
            projectedMessages = projection.Messages;
        }
        else
        {
            // 回退路径：旧 ProjectForModel（不含 Material 正文；传 0 = 不限制，保持向后兼容）
            projectedMessages = state.Context.ProjectForModel(tokenBudget: 0);
        }

        // 仅在事件 payload 中携带 contextLength（不再传字符串给 Transport）
        var contextLength = AgentMessage.Serialize(projectedMessages).Length;

        // 记录 ModelCallStarted
        state = BufferEvent(state, AgentRunEventType.ModelCallStarted, JsonSerializer.Serialize(new
        {
            turn = _currentTurn,
            contextLength
        }));

        // 调用 AgentModelRequest 重载（携带 Tool 定义 + 模型工件 + 截止时间，支持原生 function calling）。
        // 旧路径仅传 messages，模型无法发起 function calling；新路径将 _toolDefinitions（从 RealToolDispatcher 构建）
        // 传给 Transport，让真实 LLM 能声明并调用 Tool。
        var modelRequest = new AgentModelRequest
        {
            RunId = state.Run.RunId,
            ModelArtifactId = state.Run.ModelArtifactId,
            Messages = projectedMessages,
            Tools = _toolDefinitions,
            DeadlineAt = state.Run.DeadlineAt ?? DateTimeOffset.UtcNow.AddMinutes(5)
        };
        var response = await _modelTransport.CallAsync(modelRequest, cancellationToken).ConfigureAwait(false);
        // 同步更新 Context.LastModelTurn 和 LastModelResponse
        state = state with
        {
            LastModelResponse = response,
            Context = state.Context with { LastModelTurn = response }
        };

        // 递增模型调用计数
        _modelCallsUsed++;
        // 递增执行期内模型轮次计数（用于 RequestId 的 modelTurn）
        _executionModelTurn++;

        // 模型响应进入 Actor 后立刻生成不可变的 NormalizedToolCall 列表。
        // InvocationId = {runId}_{executionModelTurn}_{ordinal}（确定性、可重建），
        // 后续 Assistant 消息 / ModelCallCompleted 事件 / DispatchToolsAsync / 审批 / Journal / Tool Message
        // 全部引用此 InvocationId，消除两条路径分别 Guid.NewGuid() 产生不同 ID 的问题。
        var normalizedToolCalls = NormalizeToolCalls(state.Run.RunId, _executionModelTurn, response.ToolCalls);
        state = state with { NormalizedToolCalls = normalizedToolCalls };

        // 累积 token + 费用到 _costBudget
        if (_costBudget is not null)
        {
            var billedCost = response.BilledCost > 0 ? response.BilledCost : response.EstimatedCost;
            _costBudget = _costBudget with
            {
                TokensUsed = _costBudget.TokensUsed + response.TokensConsumed,
                CostUsedUsd = _costBudget.CostUsedUsd + billedCost
            };
        }

        // 同步 _turnBudget 的 ModelCallsUsed 计数
        if (_turnBudget is not null)
        {
            _turnBudget = _turnBudget with { ModelCallsUsed = _modelCallsUsed };
        }

        // Bug 3 修复：每次模型调用都计为一次 Turn（TurnBudget 递减），
        // 防止模型连续返回"非最终答案且无 ToolCalls"时 Turn 不增长导致无限循环
        _currentTurn++;
        if (_turnBudget is not null)
        {
            _turnBudget = _turnBudget with { TurnsUsed = _turnBudget.TurnsUsed + 1 };
        }

        // 始终追加 Assistant 消息（原生 function calling 响应可能 Content 为空 + ToolCalls 非空）。
        // 旧路径仅在 Content 非空时追加，导致多轮 Tool 调用协议中断——模型在下一轮看不到自己上一轮
        // 发起的 Tool 调用请求，OpenAI / Anthropic 兼容 API 会拒绝无前置 Assistant tool_calls 的 Tool 消息。
        // ToolCalls[].Id 使用 NormalizedToolCall.InvocationId（确定性、与分派路径一致），
        // 替代旧路径的 tc.ToolCallId ?? tc.ToolName ?? Guid.NewGuid().ToString("N")。
        var assistantMessage = new AgentMessage
        {
            Role = AgentMessageRole.Assistant,
            Content = response.Content ?? string.Empty,
            EventId = null, // 关联事件 ID 在 ModelCallCompleted 事件产出后回填（此处保留 null 即可）
            ToolCalls = response.ToolCalls.Count > 0
                ? response.ToolCalls.Select((tc, idx) => new AgentToolCallEntry
                  {
                      Id = normalizedToolCalls[idx].InvocationId,
                      Name = tc.ToolName ?? string.Empty,
                      Arguments = tc.Arguments
                  }).ToList()
                : null
        };
        state.Context.Messages.Add(assistantMessage);
        // 同步追加到统一对话流，保持 Function Calling 消息因果顺序。
        // 投影器从 Conversation 按原子协议单元保序裁剪，避免 Messages 与 ToolObservations 分离投影。
        state.Context.Conversation.Add(assistantMessage);

        // 更新本地 Run 副本（不再单独调 _runStore.UpdateAsync；CAS + 字段更新延后到批量提交）
        // Bug 3 修复：同步 run.CostBudget（从模型响应中获取实际 token cost）
        var updatedRun = state.Run with
        {
            ModelCallsUsed = _modelCallsUsed,
            Turn = _currentTurn,
            TurnBudget = _turnBudget,
            CostBudget = _costBudget,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        state = state with { Run = updatedRun };

        // 记录 ModelCallCompleted（含扩展字段：input/output tokens、modelId、cost）
        state = BufferEvent(state, AgentRunEventType.ModelCallCompleted, JsonSerializer.Serialize(new
        {
            isFinalAnswer = response.IsFinalAnswer,
            toolCallCount = response.ToolCalls.Count,
            tokensConsumed = response.TokensConsumed,
            inputTokens = response.InputTokens,
            outputTokens = response.OutputTokens,
            cachedInputTokens = response.CachedInputTokens,
            modelId = response.ModelId,
            estimatedCost = response.EstimatedCost,
            billedCost = response.BilledCost,
            modelCallsUsed = _modelCallsUsed,
            // 持久化执行期模型轮次，支持崩溃恢复时重建 _executionModelTurn，
            // 避免恢复后从 0 重新计数导致 RequestId 改变（Journal 无法识别原调用）。
            executionModelTurn = _executionModelTurn,
            durationMs = response.Duration.TotalMilliseconds,
            // 持久化完整模型响应，支持崩溃恢复时无损重建 Conversation。
            // 旧事件缺少此字段时恢复路径跳过 Assistant 重建（仅恢复 Tool 消息，向后兼容）。
            // toolCalls[].id 使用 NormalizedToolCall.InvocationId（与 Assistant 消息 / 分派路径一致）。
            content = response.Content ?? string.Empty,
            toolCalls = response.ToolCalls.Count > 0
                ? response.ToolCalls.Select((tc, idx) => new
                  {
                      id = normalizedToolCalls[idx].InvocationId,
                      name = tc.ToolName ?? string.Empty,
                      arguments = tc.Arguments ?? string.Empty
                  }).ToArray()
                : Array.Empty<object>()
        }));

        return state;
    }

    /// <summary>
    /// 构建结构化上下文。
    /// User(run.Task) 不再追加到 Messages，而是由 AgentContextState.CurrentTask 持有，
    /// ProjectForModel 投影时合成 User 消息（消除 hasUserMessage 去重检查）。
    /// 若 IContextDecisionRuntime 注入，执行决策并存储 ContextDecisionExecutionResult 到
    /// state.LastDecisionResult，由 IAgentModelContextProjector 在投影时从 WorkingSet.Materials
    /// 取出候选正文。不再每轮追加 System(retrievedContext) 消息（避免重复 + 让投影器统一管理）。
    /// </summary>
    /// <param name="state">当前执行状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<(AgentContextBuildStatus Status, AgentRunExecutionState State)> BuildContextAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        // CurrentTask 已在 ExecuteAsync 初始化时设置为 run.Task，
        // ProjectForModel 会将其投影为 User 消息，无需在此追加。
        // 旧路径的 hasUserMessage 去重检查随之移除（CurrentTask 是单值字段，天然不会重复）。

        // 若 IContextDecisionRuntime 注入，执行决策获取 WorkingSet（含 Materials 正文）。
        // 未注入时按 Ready 处理（按设计的无召回配置，可调用模型）。
        if (_decisionRuntime is null)
        {
            return (AgentContextBuildStatus.Ready, state);
        }

        var (status, decisionResult) = await TryExecuteDecisionAsync(state, cancellationToken).ConfigureAwait(false);
        // 阻断状态一律清空上轮决策结果（避免投影器使用过期 Materials），交由调用方终止本轮；
        // 可继续的状态把决策结果存入供投影器使用，并把 Resident 写进 Run 以便崩溃后恢复。
        if (status is AgentContextBuildStatus.Ready or AgentContextBuildStatus.OptionalRetrievalDegraded)
        {
            var run = decisionResult is null
                ? state.Run
                : state.Run with
                {
                    ResidentWorkingSetJson = AgentResidentWorkingSet.Serialize(
                        AgentResidentWorkingSet.FromLastDecision(decisionResult))
                };
            state = state with { LastDecisionResult = decisionResult, Run = run };
        }
        else
        {
            state = state with { LastDecisionResult = null };
        }
        return (status, state);
    }

    /// <summary>
    /// 调用 IContextDecisionRuntime 执行决策，返回构建状态 + 含 WorkingSet.Materials 的执行结果。
    /// 仅 Ready / OptionalRetrievalDegraded 允许调用模型；其余状态终止本轮
    /// （模型绝不能在缺失 mandatory 上下文时运行）。
    /// </summary>
    private async Task<(AgentContextBuildStatus Status, ContextDecisionExecutionResult? Result)> TryExecuteDecisionAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        var run = state.Run;
        if (_decisionRuntime is null)
        {
            return (AgentContextBuildStatus.Ready, null);
        }

        // 自适应检索规划器驱动 Agent 上下文构建（planner → Actor）。
        // - 规划：由 run 派生规划输入（任务 + 意图 + 工具观察 + 未解决目标 + 上轮诊断 + 工作区 + 集合），
        //   PlanAsync 产出受控计划（自适应模式 Active 时才改预算/查询权重），
        //   计划 TokenBudget / TopK / RequiredIds 注入决策请求。
        // - 规划失败不阻断主链（自适应是增强层；降级为不设 TokenBudget，走引擎默认）。
        AgentRetrievalPlannerInput? plannerInput = null;
        var plannedTokenBudget = 0;
        var controlledQueryText = string.Empty;
        var plannedTopK = 0;
        IReadOnlyList<string> plannedRequiredIds = Array.Empty<string>();
        IReadOnlyList<string> plannedExcludedIds = Array.Empty<string>();
        IReadOnlyList<AgentRetrievalQuery>? plannedQueries = null;
        if (_adaptivePlanner is not null)
        {
            try
            {
                var currentIntent = string.IsNullOrWhiteSpace(state.Context.CurrentTask)
                    ? run.Task
                    : state.Context.CurrentTask;
                plannerInput = new AgentRetrievalPlannerInput
                {
                    OriginalTask = run.Task,
                    LatestAssistantIntent = currentIntent,
                    ToolObservations = state.Context.ToolObservations.Count == 0
                        ? Array.Empty<ToolObservation>()
                        : state.Context.ToolObservations.ToArray(),
                    UnresolvedGoals = Array.Empty<string>(),
                    PreviousRetrievalDiagnostics = AgentTurnSearchQuery.DiagnosticsFrom(state.LastDecisionResult),
                    TurnBudget = run.TurnBudget,
                    WorkspaceId = run.WorkspaceId,
                    CollectionId = run.ResolveContextCollectionId(),
                    Purpose = AdaptiveAgentContextPurpose
                };
                var plan = await _adaptivePlanner.PlanAsync(plannerInput, cancellationToken).ConfigureAwait(false);
                plannedTokenBudget = plan.TokenBudget;
                plannedQueries = plan.ControlledQueries;
                plannedTopK = plan.TopK;
                plannedRequiredIds = plan.RequiredIds;
                plannedExcludedIds = plan.ExcludedIds;
                if (plannedExcludedIds.Count > 0 && plannedRequiredIds.Count > 0)
                {
                    plannedRequiredIds = plannedRequiredIds
                        .Where(id => !plannedExcludedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                        .ToArray();
                }
                // 记录本 Run 使用的检索计划签名（延迟归因：Run 终态时把最终结果质量归因到该签名）。
                _usedRetrievalSignatures.Add(AdaptiveRetrievalPlanSignature.Compute(plannerInput));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[AgentRunActor] Adaptive retrieval plan failed for run {0}: {1}", run.RunId, ex.Message);
            }
        }

        var plannedQueryTexts = AgentTurnSearchQuery.CollectQueries(
            plannedQueries, run.Task, state.Context.ToolObservations);
        controlledQueryText = AgentTurnSearchQuery.MergeQueries(plannedQueryTexts, run.Task);

        try
        {
            var collectionId = run.ResolveContextCollectionId();
            var scope = new ContextDecisionScope(run.WorkspaceId, collectionId);
            var agentSession = new AgentSessionId
            {
                Value = run.SessionId,
                WorkspaceId = run.WorkspaceId,
                CollectionId = collectionId,
                CreatedAt = run.CreatedAt
            };

            // 上一轮选中项作为 Resident 种子。本轮按计划查询分条检索。
            // 未选中的不带入。不把选中 ID 设为 RequiredIds。失败工具确认不存在的 ID 排除。
            var request = new ContextDecisionRuntimeRequest
            {
                RequestId = $"{run.WorkspaceId}/{run.RunId}-ctx-{_modelCallsUsed}",
                Scope = scope,
                Purpose = ContextDecisionPurpose.AgentContext,
                SeedWorkingSet = AgentResidentWorkingSet.WithoutIds(
                    AgentResidentWorkingSet.ResolveSeed(
                        state.LastDecisionResult, state.Run.ResidentWorkingSetJson),
                    plannedExcludedIds),
                QueryText = string.IsNullOrWhiteSpace(controlledQueryText) ? run.Task : controlledQueryText,
                TokenBudget = plannedTokenBudget,
                TopK = plannedTopK,
                RetrievalInput = new RetrievalInput
                {
                    IncludeContent = false,
                    ExcludedIds = plannedExcludedIds,
                    QueryTexts = plannedQueryTexts
                },
                AgentInput = new AgentInput
                {
                    Session = agentSession,
                    RequiredIds = plannedRequiredIds
                }
            };

            // 使用 ExecuteWithWorkingSetAsync 获取完整 WorkingSet（Envelopes + Materials）
            // 让投影器从 Materials 恢复候选正文，而不只是 CandidateId/Type/Score 摘要
            var result = await _decisionRuntime.ExecuteWithWorkingSetAsync(request, cancellationToken).ConfigureAwait(false);

            // 自适应反馈（best-effort）：命中数 / 预算超限记过程；质量只来自工具观察。
            // 默认模式不应用乘数，失败不阻断主链。
            await RecordAdaptiveOutcomeAsync(run, plannerInput, request.QueryText, result, cancellationToken).ConfigureAwait(false);

            // hydration 失败严重度处理。
            // 1) 日志：hydration.failedCount > 0 / hydration.budgetExceeded 时记录 Trace 警告（可观测性）。
            // 2) fail-closed：任一 Selected hard constraint / mandatory 候选正文为空时，
            // 决策结果不可用（模型绝不能在缺失 mandatory 上下文的情况下运行），返回 null 降级。
            if (result is not null)
            {
                var diagnostics = result.Decision.Outcome.Diagnostics;
                var hasHydrationFailures = diagnostics.TryGetValue("hydration.failedCount", out var failedCountText)
                    && !string.Equals(failedCountText, "0", StringComparison.Ordinal);
                if (hasHydrationFailures || diagnostics.ContainsKey("hydration.budgetExceeded"))
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "[AgentRunActor] Late hydration degraded for run {0}: failedCount={1}, budgetExceeded={2}",
                        run.RunId,
                        failedCountText ?? "0",
                        diagnostics.ContainsKey("hydration.budgetExceeded"));
                }

                // AgentContext fail-closed — 预算修复后 mandatory 独占仍超限
                // （exact tokenize 后实际 token 数 > 模型上下文窗口），决策结果不可用。
                if (diagnostics.ContainsKey("hydration.budgetExceeded"))
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "[AgentRunActor] Fail-closed: hydration budget exceeded after exact tokenize for run {0}; mandatory items alone exceed token budget. Decision result discarded.",
                        run.RunId);
                    return (AgentContextBuildStatus.BudgetUnsatisfiable, null);
                }

                foreach (var envelope in result.Decision.SelectedEnvelopes)
                {
                    if (!envelope.Safety.IsHardConstraint && !envelope.Safety.IsMandatory)
                    {
                        continue;
                    }

                    if (!result.WorkingSet.Materials.TryGetValue(envelope.CanonicalKey, out var material)
                        || string.IsNullOrEmpty(material.Content))
                    {
                        System.Diagnostics.Trace.TraceWarning(
                            "[AgentRunActor] Fail-closed: hard constraint / mandatory candidate {0} has no hydrated content for run {1}; decision result discarded.",
                            envelope.CanonicalKey.EntityId,
                            run.RunId);
                        return (AgentContextBuildStatus.SafetyBlocked, null);
                    }
                }
            }

            // 成功（或仅可选候选降级）→ 允许调用模型。
            return result is not null
                ? (AgentContextBuildStatus.Ready, result)
                : (AgentContextBuildStatus.OptionalRetrievalDegraded, null);
        }
        catch (OperationCanceledException)
        {
            // 外部取消 → 交由主循环转 Cancelled
            throw;
        }
        catch (MandatoryContextWindowExceededException)
        {
            // 分配器 fail-closed：mandatory 独占超出硬窗口（未到 hydration 即被拒绝）。
            System.Diagnostics.Trace.TraceWarning(
                "[AgentRunActor] Fail-closed: mandatory context window exceeded for run {0}; decision discarded.",
                run.RunId);
            return (AgentContextBuildStatus.BudgetUnsatisfiable, null);
        }
        catch (MandatoryHydrationFailedException)
        {
            // 水合器 fail-closed：mandatory / hard constraint 候选正文获取失败。
            System.Diagnostics.Trace.TraceWarning(
                "[AgentRunActor] Fail-closed: mandatory context hydration failed for run {0}; decision discarded.",
                run.RunId);
            return (AgentContextBuildStatus.SafetyBlocked, null);
        }
        catch (Exception)
        {
            // Decision Runtime 执行异常 → 依赖不可用，终止本轮（可重试），不降级调用模型。
            System.Diagnostics.Trace.TraceWarning(
                "[AgentRunActor] Decision Runtime exception for run {0}; run blocked as dependency unavailable.",
                run.RunId);
            return (AgentContextBuildStatus.DependencyUnavailable, null);
        }
    }

    /// <summary>
    /// 记录自适应检索反馈（best-effort，失败不阻断主链）。
    /// 命中数取选中候选数；质量取工具观察成功率。没有工具观察则无效，不把打分器分数当准。
    /// </summary>
    private async Task RecordAdaptiveOutcomeAsync(
        AgentRun run,
        AgentRetrievalPlannerInput? plannerInput,
        string controlledQueryText,
        ContextDecisionExecutionResult? result,
        CancellationToken cancellationToken)
    {
        if (_adaptivePlanner is null || plannerInput is null)
        {
            return;
        }

        try
        {
            var outcome = result?.Decision.Outcome;
            var budgetExceeded = outcome is not null
                && (outcome.BudgetExceededCount > 0
                    || (outcome.TokenBudget > 0 && outcome.EffectiveTokens > outcome.TokenBudget));

            // 质量信号来自工具观察（外部结果），不用选中项的打分器分数。
            // 还没有工具时记为无效，避免把启发式分数学成「准」。
            var selectedCount = Math.Max(0, outcome?.SelectedCount ?? 0);
            var evidence = AgentTurnSearchQuery.ToolEvidence(plannerInput.ToolObservations);

            await _adaptivePlanner.RecordOutcomeAsync(new RetrievalPlanFeedback
            {
                PlanSignature = AdaptiveRetrievalPlanSignature.Compute(plannerInput),
                WorkspaceId = run.WorkspaceId,
                CollectionId = run.ResolveContextCollectionId(),
                Purpose = AdaptiveAgentContextPurpose,
                QueryText = string.IsNullOrWhiteSpace(controlledQueryText) ? run.Task : controlledQueryText,
                HitsReturned = selectedCount,
                BudgetExceeded = budgetExceeded,
                Effective = evidence.Effective,
                RecordedAtUtc = DateTimeOffset.UtcNow,
                Source = RetrievalFeedbackSource.Runtime,
                Confidence = evidence.Confidence,
                OutcomeQuality = evidence.Quality
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[AgentRunActor] Adaptive retrieval outcome record failed for run {0}: {1}", run.RunId, ex.Message);
        }
    }

    /// <summary>执行 DispatchTool 阶段（含校验 + 审批 + 分派 + 观察）。</summary>
    /// <summary>
    /// Tool 派发前授权校验的拒绝分类。
    /// </summary>
    private enum ToolAuthorizationDenial
    {
        /// <summary>通过。</summary>
        None,

        /// <summary>工具不在 Run 授权快照的已授权集合中（跳过该 Tool，与 AllowedToolIds 约束一致）。</summary>
        ToolNotGranted,

        /// <summary>快照整体失效（过期 / 策略版本漂移 / 策略缺失）——终止本轮，不允许继续执行任何 Tool。</summary>
        SnapshotInvalid
    }

    /// <summary>
    /// 校验 Run 的 Tool 授权快照是否允许执行指定 Tool。
    /// 快照为空（历史 Run / 未启用授权快照）时：
    /// - 未注册授权策略（开发/测试路径）→ 兼容放行（无策略可校验）；
    /// - 生产模式（已注册策略）→ 基础无副作用 Tool（ReadOnly/None）兼容放行；
    ///   File / Process / Network / Write 类 Tool 要求重新授权（RequiresReauthorization）——
    ///   旧 Run 不得成为绕过新安全模型的永久例外。
    /// </summary>
    private (bool Allowed, ToolAuthorizationDenial Denial, string Reason) IsToolAuthorizedBySnapshot(AgentRun run, string toolName)
    {
        var snapshot = run.AuthorizationSnapshot;
        if (snapshot is null)
        {
            // 生产模式（已注册授权策略）下旧 Run 的治理边界：
            // 基础无副作用 Tool（仅需 AgentRun 能力位）可兼容；File / Process / Network
            // 类副作用 Tool 要求重新授权（fail-closed 于安全模型）——旧 Run 不得成为
            // 绕过新安全模型的永久例外。
            if (_toolAuthorizationPolicy is not null)
            {
                var legacyRequirement = _toolAuthorizationPolicy.GetRequirement(toolName);
                var requiresCapability = legacyRequirement.RequiredCapability
                    & (WorkspacePermission.FileAccess | WorkspacePermission.ProcessExec | WorkspacePermission.NetworkAccess);
                if (requiresCapability == WorkspacePermission.None)
                {
                    return (true, ToolAuthorizationDenial.None, string.Empty);
                }

                return (false, ToolAuthorizationDenial.SnapshotInvalid,
                    $"Run 未建立 Tool 授权快照（历史 Run），Tool '{toolName}' 需要 " +
                    $"{legacyRequirement.RequiredCapability}（File/Process/Network 类）能力——" +
                    $"生产模式要求重新授权，旧 Run 不得绕过新安全模型。");
            }

            return (true, ToolAuthorizationDenial.None, string.Empty);
        }

        if (_toolAuthorizationPolicy is null)
        {
            return (false, ToolAuthorizationDenial.SnapshotInvalid,
                "Run 已建立 Tool 授权快照但未注册授权策略，拒绝执行。");
        }

        if (snapshot.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return (false, ToolAuthorizationDenial.SnapshotInvalid,
                $"Tool 授权快照已过期（ExpiresAt={snapshot.ExpiresAt:O}），拒绝执行。");
        }

        if (!string.Equals(snapshot.PolicyVersion, _toolAuthorizationPolicy.PolicyVersion, StringComparison.Ordinal))
        {
            return (false, ToolAuthorizationDenial.SnapshotInvalid,
                $"Tool 授权策略版本已漂移（快照 {snapshot.PolicyVersion}，当前 {_toolAuthorizationPolicy.PolicyVersion}），拒绝执行。");
        }

        // AuthorizationEpoch：管理员撤权后纪元递增——固化了旧纪元的快照立即失效，
        // 无需等待 ExpiresAt（一次轻量整数比较使全部旧授权快照失效）。
        if (snapshot.AuthorizationEpoch != _toolAuthorizationPolicy.AuthorizationEpoch)
        {
            return (false, ToolAuthorizationDenial.SnapshotInvalid,
                $"Tool 授权纪元已变更（快照 {snapshot.AuthorizationEpoch}，当前 {_toolAuthorizationPolicy.AuthorizationEpoch}）——" +
                "管理员已撤权或权限已变更，旧快照立即失效，要求重新授权。");
        }

        if (!snapshot.GrantedToolIds.Contains(toolName))
        {
            return (false, ToolAuthorizationDenial.ToolNotGranted,
                $"Tool '{toolName}' 不在 Run 授权快照的已授权工具集合中，已被授权约束拒绝。");
        }

        var requirement = _toolAuthorizationPolicy.GetRequirement(toolName);
        if (!snapshot.GrantedPermissions.Contains(requirement.ExecutePermissionId))
        {
            return (false, ToolAuthorizationDenial.ToolNotGranted,
                $"Run 创建者缺少执行 Tool '{toolName}' 所需的权限 {requirement.ExecutePermissionId}，已被授权约束拒绝。");
        }

        return (true, ToolAuthorizationDenial.None, string.Empty);
    }

    private async Task<AgentRunExecutionState> DispatchToolsAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        if (state.LastModelResponse is null || state.LastModelResponse.ToolCalls.Count == 0)
        {
            // 无 Tool 调用 → 回到 ContextBuilding
            return TransitionStateLocal(state, AgentRunState.ContextBuilding);
        }

        // 进入 ToolDispatching（本地推进 + 缓冲 StateTransition 事件）
        state = TransitionStateLocal(state, AgentRunState.ToolDispatching);

        for (var toolIndex = 0; toolIndex < state.LastModelResponse!.ToolCalls.Count; toolIndex++)
        {
            var toolCall = state.LastModelResponse.ToolCalls[toolIndex];

            //在循环开始时生成 toolCallId，同时用于 ToolCallStarted 和 ToolCallCompleted
            // 确保 ToolCallStarted 事件和 ToolCallCompleted 事件的审计 ID 一致。
            // 多轮协议修复：优先使用模型返回的 ToolCallId（如 OpenAI 的 tool_call_id），
            // 确保 Tool 观察消息的 tool_call_id 与 Assistant 消息的 tool_calls[].id 一致——
            // OpenAI / Anthropic 兼容 API 要求二者匹配，否则第二轮调用会被拒绝。
            // 优先使用 NormalizedToolCall.InvocationId（由 CallModelAsync 在模型响应进入 Actor
            // 后立即生成，与 Assistant 消息 / ModelCallCompleted 事件 / Tool Message 引用同一 ID）。
            // 回退到 toolCall.ToolCallId / Guid 仅为防御性兼容（如 resume 路径未重建 NormalizedToolCalls）。
            var normalized = (state.NormalizedToolCalls is not null && toolIndex < state.NormalizedToolCalls.Count)
                ? state.NormalizedToolCalls[toolIndex]
                : null;
            var toolCallId = normalized?.InvocationId ?? toolCall.ToolCallId ?? Guid.NewGuid().ToString("N");

            // 强制 Run 约束的 Tool 白名单（AllowedToolIds 非空时仅允许集合中的 Tool）。
            // 旧路径未写入 AllowedToolIds，Actor 无法按 Run 限定 Tool 集；新路径从 API 入参写入并在此强制。
            if (state.Run.AllowedToolIds.Count > 0
                && !string.IsNullOrWhiteSpace(toolCall.ToolName)
                && !state.Run.AllowedToolIds.Contains(toolCall.ToolName))
            {
                state = BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
                    toolCallId: toolCallId,
                    requestId: null,
                    toolName: toolCall.ToolName ?? string.Empty,
                    idempotencyKey: toolCall.IdempotencyKey,
                    sideEffect: ToolSideEffect.Unknown.ToString(),
                    externalOperationId: null,
                    journalState: ToolDispatchState.Prepared.ToString(),
                    succeeded: false,
                    output: null,
                    error: $"Tool '{toolCall.ToolName}' 不在 Run.AllowedToolIds 白名单中，已被 Run 约束拒绝。",
                    durationMs: 0)));
                continue;
            }

            // 授权快照校验（安全边界）：快照整体失效（过期/策略漂移/策略缺失）→ 终止本轮，
            // 不允许继续执行任何 Tool；工具不在授权集 → 跳过该 Tool（与 AllowedToolIds 约束一致）。
            var (authorized, authorizationDenial, authorizationReason) = IsToolAuthorizedBySnapshot(state.Run, toolCall.ToolName ?? string.Empty);
            if (!authorized)
            {
                if (authorizationDenial == ToolAuthorizationDenial.SnapshotInvalid)
                {
                    return await FailAsync(state, authorizationReason, cancellationToken).ConfigureAwait(false);
                }

                state = BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
                    toolCallId: toolCallId,
                    requestId: null,
                    toolName: toolCall.ToolName ?? string.Empty,
                    idempotencyKey: toolCall.IdempotencyKey,
                    sideEffect: ToolSideEffect.Unknown.ToString(),
                    externalOperationId: null,
                    journalState: ToolDispatchState.Prepared.ToString(),
                    succeeded: false,
                    output: null,
                    error: authorizationReason,
                    durationMs: 0)));
                continue;
            }

            // 1. 校验
            if (_toolCallValidator is not null)
            {
                var validation = await _toolCallValidator.ValidateAsync(state.Run.WorkspaceId, state.Run.RunId, toolCall, cancellationToken).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    // 校验失败的 ToolCallCompleted 也含 toolCallId（便于恢复时定位）
                    state = BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
                        toolCallId: toolCallId,
                        requestId: null,
                        toolName: toolCall.ToolName ?? string.Empty,
                        idempotencyKey: toolCall.IdempotencyKey,
                        sideEffect: ToolSideEffect.Unknown.ToString(),
                        externalOperationId: null,
                        journalState: ToolDispatchState.Prepared.ToString(),
                        succeeded: false,
                        output: null,
                        error: validation.Error ?? "Validation failed",
                        durationMs: 0)));
                    continue;
                }

                // 2. 审批（如需）
                if (validation.RequiresApproval && _approvalGate is not null)
                {
                    state = TransitionStateLocal(state, AgentRunState.AwaitingApproval);

                    // 持久化完整 PendingToolCommand 到 ApprovalRequested 事件 payload，
                    // 让审批通过后恢复时可直接执行原 Tool（不依赖模型重生成）。
                    // 包含 ToolCallId / ToolName / ArgumentsJson / IdempotencyKey / ModelTurnRevision。
                    var pendingCommand = new PendingToolCommand
                    {
                        ToolCallId = toolCallId,
                        ToolName = toolCall.ToolName ?? string.Empty,
                        ArgumentsJson = toolCall.Arguments ?? string.Empty,
                        IdempotencyKey = toolCall.IdempotencyKey,
                        ModelTurnRevision = _executionModelTurn
                    };

                    // 构建完整 PendingToolCommands 列表（当前 + 同轮后续未处理的 Tool Call）。
                    // 旧路径仅保存单数 PendingToolCommand，审批中断时同轮后续 Tool Call 被丢弃。
                    // remainingToolCallId 优先使用 NormalizedToolCall.InvocationId，确保审批恢复后
                    // ExecutePendingToolAsync 使用的 ID 与原 Assistant 消息 / 事件一致。
                    var pendingCommands = new List<PendingToolCommand> { pendingCommand };
                    for (var j = toolIndex + 1; j < state.LastModelResponse!.ToolCalls.Count; j++)
                    {
                        var remaining = state.LastModelResponse.ToolCalls[j];
                        var remainingNormalized = (state.NormalizedToolCalls is not null && j < state.NormalizedToolCalls.Count)
                            ? state.NormalizedToolCalls[j]
                            : null;
                        var remainingToolCallId = remainingNormalized?.InvocationId
                            ?? remaining.ToolCallId
                            ?? Guid.NewGuid().ToString("N");
                        pendingCommands.Add(new PendingToolCommand
                        {
                            ToolCallId = remainingToolCallId,
                            ToolName = remaining.ToolName ?? string.Empty,
                            ArgumentsJson = remaining.Arguments ?? string.Empty,
                            IdempotencyKey = remaining.IdempotencyKey,
                            ModelTurnRevision = _executionModelTurn
                        });
                    }

                    state = BufferEvent(state, AgentRunEventType.ApprovalRequested, JsonSerializer.Serialize(new
                    {
                        toolName = toolCall.ToolName,
                        reason = validation.ApprovalReason,
                        pendingToolCommand = pendingCommand,
                        pendingToolCommands = pendingCommands
                    }));

                    // Gate 是审批记录的唯一创建者。Actor 不再直接写 IAgentApprovalStore——
                    // 旧路径 Actor 与 Gate 各 CreateAsync 一次，产生重复 Pending 记录。
                    // ApprovalId 由 Gate 独立生成（审批记录主键），与模型 ToolCallId 分离，
                    // 事件流通过 approvalId 字段与审批记录关联。
                    var approval = await _approvalGate.RequestApprovalAsync(
                        state.Run.WorkspaceId, state.Run.RunId, toolCall, cancellationToken).ConfigureAwait(false);

                    // 区分三种审批结果——PendingApproval（挂起等待人工）/ Approved（批准）/ Rejected（拒绝）
                    if (approval.PendingApproval)
                    {
                        // 使用 Gate 返回的 ApprovalId 作为外部 POST 端点定位键。
                        // 未注入 store 时（测试模式）Gate 不生成持久化记录，回退到 Actor 的 toolCallId 仅用于事件流审计。
                        var effectiveApprovalId = approval.ApprovalId ?? toolCallId;

                        // 审批挂起 — 记录 ApprovalResolved(pending) 事件 + approvalId，
                        // flush 持久化 AwaitingApproval 状态，然后退出执行槽（释放 Worker/Semaphore）。
                        // 旧路径返回 Approved=false 导致 Actor 跳过 Tool 继续执行——不是真正的 Human-in-the-loop。
                        // 外部通过 POST /approvals/{approvalId} 端点提交决策；
                        // 决策后 Run 状态推进到 PendingToolExecution（批准）或 Failed（拒绝），
                        // RecoveryWorker 会重新入队执行。
                        state = BufferEvent(state, AgentRunEventType.ApprovalResolved, JsonSerializer.Serialize(new
                        {
                            approved = false,
                            pending = true,
                            approvalId = effectiveApprovalId,
                            approverId = (string?)null,
                            rejectionReason = (string?)null
                        }));

                        // 立即 flush：将 AwaitingApproval 状态 + 事件持久化（单事务）
                        await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

                        // 将完整 PendingToolCommands 列表保存到执行状态，供恢复时依次执行。
                        state = state with { PendingToolCommands = pendingCommands };

                        // 退出执行槽：返回 AwaitingApproval 状态，主循环检测后 return
                        return state;
                    }

                    state = BufferEvent(state, AgentRunEventType.ApprovalResolved, JsonSerializer.Serialize(new
                    {
                        approved = approval.Approved,
                        approverId = approval.ApproverId,
                        rejectionReason = approval.RejectionReason
                    }));

                    if (!approval.Approved)
                    {
                        // 审批拒绝 → 回到 ToolDispatching 状态后跳过此 Tool
                        state = TransitionStateLocal(state, AgentRunState.ToolDispatching);
                        continue;
                    }

                    // 批准后回到 ToolDispatching
                    state = TransitionStateLocal(state, AgentRunState.ToolDispatching);
                }
            }

            // 先计算 RequestId 并持久化 ToolCallStarted 事件，再执行外部 Tool。
            // 旧路径在执行后才缓冲 ToolCallStarted，崩溃时无法审计已发起的调用。
            // RequestId 以 NormalizedToolCall.InvocationId 为基准（与审批恢复路径、
            // ToolCallStarted 事件一致）：模型返回的原始 ToolCallId（如 call_xxx）在
            // 崩溃恢复后不可重建，而 InvocationId 由 (runId, turn, ordinal) 确定性生成，
            // 恢复节点重放原 Tool 时能命中同一 journal 条目（durable 去重）。
            var effectiveToolCall = toolCall with { ToolCallId = toolCallId };
            var requestIdForStart = (_durableToolExecutor is not null)
                ? DefaultDurableToolExecutor.ComputeRequestId(state.Run.WorkspaceId, state.Run.RunId, effectiveToolCall, _executionModelTurn)
                : toolCallId;

            // ToolCallStarted 携带 arguments + modelTurnRevision：
            // 进程在 Tool 执行中被 Kill 时，恢复节点据此重建原 PendingToolCommand（原始轮次），
            // RequestId 与 journal 条目一致 → durable 去重生效，不重复执行外部副作用。
            state = BufferEvent(state, AgentRunEventType.ToolCallStarted, JsonSerializer.Serialize(new
            {
                toolName = toolCall.ToolName,
                toolCallId = toolCallId,
                requestId = requestIdForStart,
                idempotencyKey = toolCall.IdempotencyKey,
                arguments = toolCall.Arguments,
                modelTurnRevision = _executionModelTurn
            }));

            // flush 持久化 ToolCallStarted 后再执行外部 Tool（先日志后执行）。
            await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

            // 通过 IDurableToolExecutor 执行（若注入），否则回退到直接 IToolDispatcher
            ToolExecutionResult? toolResult = null;
            if (_durableToolExecutor is not null)
            {
                // 构造 leaseFence 并传入，保护 Tool 副作用边界。
                var leaseFence2 = (_leaseToken is not null && _fencingToken is not null)
                    ? new AgentLeaseFence
                      {
                          LeaseToken = _leaseToken,
                          FencingToken = _fencingToken.Value,
                          ExpiresAt = _leaseExpiresAtProvider?.Invoke()
                              ?? state.Run.DeadlineAt ?? DateTimeOffset.UtcNow.AddMinutes(5)
                      }
                    : null;
                toolResult = await _durableToolExecutor.ExecuteAsync(
                    state.Run.RunId, state.Run.WorkspaceId, effectiveToolCall, _executionModelTurn,
                    cancellationToken, leaseFence2, state.Run.DeadlineAt,
                    approvalGranted: true).ConfigureAwait(false);
            }
            else
            {
                // 回退路径：直接调 IToolDispatcher（无 journal，无 durable 保证）
                //使用预生成的 toolCallId 作为 RequestId（与 ToolCallStarted/Completed 一致）
                var dispatchResult = await _toolDispatcher.DispatchAsync(new ToolDispatchRequest
                {
                    ToolName = toolCall.ToolName ?? string.Empty,
                    Payload = toolCall.Arguments ?? string.Empty,
                    RequestId = toolCallId
                }, cancellationToken).ConfigureAwait(false);

                toolResult = new ToolExecutionResult
                {
                    RequestId = toolCallId,
                    IdempotencyKey = toolCall.IdempotencyKey,
                    SideEffect = dispatchResult.SideEffect,
                    ExternalOperationId = dispatchResult.ExternalOperationId,
                    JournalState = ToolDispatchState.Committed,
                    Result = dispatchResult.Result,
                    Succeeded = dispatchResult.Succeeded,
                    Error = dispatchResult.Error,
                    Duration = dispatchResult.Duration
                };
            }

            // 4. 记录 ToolCallCompleted（含完整 Tool 身份信息；使用同一 toolCallId）
            state = BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
                toolCallId: toolCallId,
                requestId: toolResult.RequestId,
                toolName: toolCall.ToolName ?? string.Empty,
                idempotencyKey: toolResult.IdempotencyKey,
                sideEffect: toolResult.SideEffect.ToString(),
                externalOperationId: toolResult.ExternalOperationId,
                journalState: toolResult.JournalState.ToString(),
                succeeded: toolResult.Succeeded,
                output: toolResult.Result,
                error: toolResult.Error,
                durationMs: toolResult.Duration.TotalMilliseconds)));

            // Journal 处于模糊状态 → 创建对账记录（幂等；阻止 Run 在未裁决时 Completed）。
            await EnsureReconciliationRecordAsync(state, toolResult, toolCall.ToolName ?? string.Empty, cancellationToken).ConfigureAwait(false);

            // 观察结果以结构化 ToolObservation 形式追加到 Context.ToolObservations
            // （替代旧路径直接 Add AgentMessage 到 Messages）；
            // ProjectForModel 投影时一次性合成 Tool 角色 AgentMessage，避免在每次模型响应/Tool 观察时复制既有字符串。
            var observation = toolResult.Succeeded
                ? $"{toolResult.Result}"
                : $"[ERROR] {toolResult.Error}";

            var toolObservation = new ToolObservation
            {
                ToolName = toolCall.ToolName ?? string.Empty,
                ToolCallId = toolCallId,
                Result = toolResult.Result,
                Error = toolResult.Error,
                Succeeded = toolResult.Succeeded
            };
            state.Context.ToolObservations.Add(toolObservation);
            // 同步追加 Tool 消息到统一对话流，紧随引发它的 Assistant ToolCall 之后，
            // 保持 "assistant tool_calls → tool result" 因果顺序（OpenAI/Anthropic 协议要求）。
            state.Context.Conversation.Add(toolObservation.ToAgentMessage());

            // 5. ObservationAppended 事件 payload 用序列化后的 observation 长度（与旧路径兼容）
            state = BufferEvent(state, AgentRunEventType.ObservationAppended, JsonSerializer.Serialize(new
            {
                toolName = toolCall.ToolName,
                observationLength = observation.Length
            }));

            // mid-loop 缓冲超过阈值时强制 flush，避免大量 Tool 调用导致内存膨胀
            if (_pendingTurnEvents.Count >= PendingEventsFlushThreshold)
            {
                await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
            }
        }

        // Tool 分派完成 → Observing（本地推进）
        state = TransitionStateLocal(state, AgentRunState.Observing);

        // 清除 LastModelResponse：Tool 已分派完毕，下一轮应由 LoopPolicy 决定 CallModel
        // （而非重复 DispatchTool）。未清除会导致 ContextBuilding → ToolDispatching 非法转换。
        // Context.LastModelTurn 保留（供 ProjectForModel 投影历史上下文）。
        // 同步清除 NormalizedToolCalls（已分派完毕，避免下一轮残留）。
        state = state with { LastModelResponse = null, NormalizedToolCalls = null };

        // Bug 3 修复：Turn 已在 CallModelAsync 中递增（每次模型调用计为一次 Turn）
        // 此处不再重复递增 Turn（避免双重计数）

        // 更新本地 Run 副本（不再单独调 _runStore.UpdateAsync；CAS + 字段更新延后到批量提交）
        var updatedRun = state.Run with
        {
            Turn = _currentTurn,
            ModelCallsUsed = _modelCallsUsed,
            TurnBudget = _turnBudget,
            CostBudget = _costBudget,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        state = state with { Run = updatedRun };

        // Checkpointing（若有工厂）→ ContextBuilding（下一轮）
        // 强制 checkpoint 阈值由 _eventsSinceLastCheckpoint 跟踪：Turn 结束的 checkpoint 会重置计数。
        // 若 _checkpointFactory 为 null 但未 checkpoint 事件已达阈值，记录警告（无法强制 checkpoint）。
        if (_checkpointFactory is not null)
        {
            state = await PersistCheckpointAsync(state, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (_eventsSinceLastCheckpoint >= ForcedCheckpointEventThreshold)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[AgentRunActor] 未 checkpoint 事件数 ({0}) 达到强制阈值 ({1})，run={2}，但无 checkpoint factory 配置。",
                    _eventsSinceLastCheckpoint, ForcedCheckpointEventThreshold, state.Run.RunId);
            }
            // 无 checkpoint 工厂 → 直接进入下一轮
            state = TransitionStateLocal(state, AgentRunState.ContextBuilding);
        }

        // Turn 结束 → 批量提交所有缓冲事件 + state CAS + checkpoint cursor（单事务）
        await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

        return state;
    }

    /// <summary>
    /// Journal 处于模糊状态（DispatchingIntent/Dispatched/Reconciling）时创建对账记录。
    /// 幂等：按 RunId+RequestId 已存在时返回既有记录。只要 Run 存在未裁决记录，
    /// <see cref="CompleteAsync"/> 就不得推进到 Completed（等待 Worker 或人工 resolve 端点裁决）。
    /// </summary>
    private async Task EnsureReconciliationRecordAsync(
        AgentRunExecutionState state,
        ToolExecutionResult toolResult,
        string toolName,
        CancellationToken cancellationToken)
    {
        if (_reconciliationStore is null || !RequiresReconciliation(toolResult.JournalState))
        {
            return;
        }

        var record = new ToolReconciliationRecord
        {
            // 对账记录 ID 包含 Workspace（跨租户唯一，防同 RunId/RequestId 碰撞）。
            ReconciliationId = "rec:" + state.Run.WorkspaceId + ":" + toolResult.RequestId,
            RunId = state.Run.RunId,
            WorkspaceId = state.Run.WorkspaceId,
            RequestId = toolResult.RequestId,
            ToolName = toolName,
            ExternalOperationId = toolResult.ExternalOperationId,
            ReconciliationHandler = toolResult.ReconciliationHandler,
            // 对账截止：CreatedAt + ToolDescriptor.ReconciliationDeadline 回传值（未回传时用默认 24h）。
            // 超期未决 → ControlRoom 高亮 + ToolReconciliationWorker 告警。
            DeadlineUtc = DateTimeOffset.UtcNow + (toolResult.ReconciliationDeadline ?? DefaultReconciliationDeadline),
            Status = ToolReconciliationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _reconciliationStore.CreateAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Journal 模糊状态判定：外部副作用可能已执行但未提交，需对账确认真相。</summary>
    private static bool RequiresReconciliation(ToolDispatchState state)
        => state == ToolDispatchState.DispatchingIntent
           || state == ToolDispatchState.Dispatched
           || state == ToolDispatchState.Reconciling;

    /// <summary>
    /// 构建 ToolCallCompleted 事件 payload（含完整 Tool 身份信息）。
    /// payload 结构与事件流中的 ToolCallCompleted payload 对齐（JSON 字段名兼容），
    /// 让恢复路径可从 payload 反序列化真实 RequestId/SideEffect/IdempotencyKey。
    /// </summary>
    private static object BuildCompletedPayload(
        string toolCallId,
        string? requestId,
        string toolName,
        string? idempotencyKey,
        string sideEffect,
        string? externalOperationId,
        string journalState,
        bool succeeded,
        string? output,
        string? error,
        double durationMs)
    {
        // 新增字段（toolCallId / requestId / idempotencyKey / sideEffect /
        // externalOperationId / journalState / resultDigest）
        var resultDigest = ComputeResultDigest(output ?? error);
        return new
        {
            toolName,
            toolCallId,
            requestId,
            idempotencyKey,
            sideEffect,
            externalOperationId,
            journalState,
            succeeded,
            output,
            error,
            durationMs,
            resultDigest
        };
    }

    /// <summary>计算结果摘要（SHA-256，小写 hex）。</summary>
    private static string? ComputeResultDigest(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 将模型响应的 ToolCalls 规范化为不可变 <see cref="NormalizedToolCall"/> 列表。
    /// 在模型响应进入 Actor 后立即调用一次，生成确定性 InvocationId（{runId}_{turn}_{ordinal}）。
    /// 后续 Assistant 消息 / 事件 / 审批 / Journal / Tool Message 全部引用此 InvocationId。
    /// </summary>
    /// <param name="runId">Agent Run ID。</param>
    /// <param name="executionModelTurn">当前执行期内的模型轮次（已递增后的值）。</param>
    /// <param name="toolCalls">模型返回的 Tool 调用列表。</param>
    /// <returns>规范化 Tool 调用列表（与 <paramref name="toolCalls"/> 等长，按 ordinal 0-based 索引）。</returns>
    private static List<NormalizedToolCall> NormalizeToolCalls(
        string runId,
        int executionModelTurn,
        IReadOnlyList<AgentToolCallRequest> toolCalls)
    {
        if (toolCalls.Count == 0)
        {
            return new List<NormalizedToolCall>(0);
        }

        var list = new List<NormalizedToolCall>(toolCalls.Count);
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];
            list.Add(new NormalizedToolCall
            {
                InvocationId = $"{runId}_{executionModelTurn}_{i}",
                ProviderToolCallId = tc.ToolCallId,
                ToolName = tc.ToolName ?? string.Empty,
                Arguments = tc.Arguments ?? string.Empty,
                Turn = executionModelTurn,
                Ordinal = i
            });
        }
        return list;
    }

    /// <summary>执行 Checkpoint 阶段。</summary>
    /// <remarks>
    /// Create checkpoint → Save checkpoint（IAgentCheckpointStore.SaveAsync）→
    /// 缓冲 CheckpointSaved event → 本地推进到 ContextBuilding。
    /// 顺序必须是保存成功后才记录事件（不能先记录成功事件再保存）。
    /// Bug 4 修复：SaveAsync 失败时显式捕获异常，转 Failed 状态，不记录 CheckpointSaved 事件。
    /// 状态推进与事件均缓冲到 _pendingTurnEvents，CAS 延后到 Turn 结束批量提交。
    /// </remarks>
    private async Task<AgentRunExecutionState> PersistCheckpointAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        // 进入 Checkpointing（本地推进 + 缓冲 StateTransition 事件）
        state = TransitionStateLocal(state, AgentRunState.Checkpointing);

        if (_checkpointFactory is not null)
        {
            var checkpointId = $"run-{state.Run.RunId}-turn-{_currentTurn}-{Guid.NewGuid():N}";
            var checkpoint = await _checkpointFactory.CreateCheckpointAsync(
                checkpointId, state.Run.SessionId, state.Run.WorkspaceId, cancellationToken).ConfigureAwait(false);

            // 将可恢复的执行状态写入 checkpoint metadata，支持恢复时从 Checkpoint Cursor
            // 直接还原（无需读取 checkpoint 之前的全部事件）：
            // - executionModelTurn：模型轮次计数（RequestId 稳定性）。
            // - conversationJson / toolObservationsJson：对话流与工具观察（模型上下文，
            // 事件流中同样存在，但 checkpoint 冗余一份使恢复可从游标断点续读）。
            // 与 RebuildStateFromEventsAsync 配合：有 Cursor 且 metadata 完整时走快路径
            // 还原，否则降级为全量事件重放（向后兼容旧 checkpoint）。
            var enrichedMetadata = new Dictionary<string, string>(
                checkpoint.Metadata ?? new Dictionary<string, string>(0),
                StringComparer.Ordinal)
            {
                ["executionModelTurn"] = _executionModelTurn.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["conversationJson"] = JsonSerializer.Serialize(state.Context.Conversation),
                ["toolObservationsJson"] = JsonSerializer.Serialize(state.Context.ToolObservations)
            };
            checkpoint = checkpoint with { Metadata = enrichedMetadata };

            // 3c：checkpoint 本体不再单独 SaveAsync，而是缓冲到 _pendingTurnCheckpoint，
            // 随 Turn 结束的 AppendBatchAsync 在同一事务内持久化（Postgres：INSERT agent_checkpoints；
            // InMemory：委托注入的 IAgentCheckpointStore）。事件与 checkpoint 原子提交，顺序保证更强。
            // 
            // Bug 4 修复语义保留：若批量提交（含 checkpoint INSERT）失败，CheckpointSaved 事件
            // 也在同一批中回滚，不会出现"事件已记录但 checkpoint 未保存"的不一致。

            // 重构：更新 state.LastCheckpoint
            state = state with { LastCheckpoint = checkpoint };

            // 记录 Turn 内最新 checkpoint，用于批量提交时的 checkpoint cursor + checkpoint 本体
            _pendingTurnCheckpoint = checkpoint;

            // 保存成功后才缓冲 CheckpointSaved 事件（顺序保证）
            state = BufferEvent(state, AgentRunEventType.CheckpointSaved, JsonSerializer.Serialize(new
            {
                checkpointId = checkpoint.CheckpointId,
                stateJsonLength = checkpoint.StateJson?.Length ?? 0,
                persisted = _checkpointStore is not null,
                sessionWorkspaceId = checkpoint.Session?.WorkspaceId,
                sessionValue = checkpoint.Session?.Value
            }));
        }

        // Checkpointing → ContextBuilding（循环继续）（本地推进）
        state = TransitionStateLocal(state, AgentRunState.ContextBuilding);

        return state;
    }

    /// <summary>执行 Complete 阶段。</summary>
    private async Task<AgentRunExecutionState> CompleteAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        // 只要 Run 存在未裁决的对账记录（Journal 模糊态 Tool），就禁止进入 Completed。
        // 转为 AwaitingReconciliation 停车（本地推进 + 缓冲 StateTransition 事件后立即 flush）：
        // ToolReconciliationWorker 对账完成后将 Run 重新入队，Actor 从事件流恢复执行。
        if (_reconciliationStore is not null)
        {
            var unresolved = await _reconciliationStore
                .HasUnresolvedForRunAsync(state.Run.WorkspaceId, state.Run.RunId, cancellationToken).ConfigureAwait(false);
            if (unresolved)
            {
                state = TransitionStateLocal(state, AgentRunState.AwaitingReconciliation);
                await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
                return state;
            }
        }

        var finalAnswer = state.LastModelResponse?.Content ?? string.Empty;

        // 更新本地 Run 副本（含最终答案 + ModelCallsUsed）
        var updatedRun = state.Run with
        {
            FinalAnswer = finalAnswer,
            Turn = _currentTurn,
            ModelCallsUsed = _modelCallsUsed,
            TurnBudget = _turnBudget,
            CostBudget = _costBudget,
            UpdatedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow
        };
        state = state with { Run = updatedRun };

        // 推进到 Completed（本地 + 缓冲 StateTransition 事件）
        state = TransitionStateLocal(state, AgentRunState.Completed);

        // 缓冲 RunCompleted 事件
        state = BufferEvent(state, AgentRunEventType.RunCompleted, JsonSerializer.Serialize(new
        {
            finalAnswerLength = finalAnswer.Length,
            turn = _currentTurn,
            modelCallsUsed = _modelCallsUsed,
            tokensUsed = _costBudget?.TokensUsed ?? 0,
            costUsedUsd = _costBudget?.CostUsedUsd ?? 0
        }));

        // 终态 flush — 批量提交所有缓冲事件 + state CAS（单事务，立即持久化）
        await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

        return state;
    }

    /// <summary>
    /// 本地推进 Run 状态（不直接写 DB），同时缓冲 StateTransition 事件。
    /// CAS 与字段更新延后到 Turn 结束时的 <see cref="FlushPendingEventsAsync"/> 批量提交。
    /// </summary>
    /// <param name="state">当前执行状态。</param>
    /// <param name="newState">目标状态。</param>
    /// <returns>更新后的执行状态（Run.State = newState）。</returns>
    private AgentRunExecutionState TransitionStateLocal(AgentRunExecutionState state, AgentRunState newState, bool bufferEvent = true)
    {
        // 校验状态机
        AgentRunStateMachine.ValidateTransition(state.Run.State, newState);

        var updatedRun = state.Run with
        {
            State = newState,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var newStateObj = state with { Run = updatedRun };

        // 缓冲 StateTransition 事件。恢复失败路径（bufferEvent false）不缓冲：
        // 事件流可能已损坏或不可用，且恢复失败状态采用状态直写（见 EnterRecoveryFailureStateAsync）。
        return bufferEvent
            ? BufferEvent(newStateObj, AgentRunEventType.StateTransition, JsonSerializer.Serialize(new
            {
                from = state.Run.State.ToString(),
                to = newState.ToString()
            }))
            : newStateObj;
    }

    /// <summary>
    /// 缓冲事件到 <see cref="_pendingTurnEvents"/>（不直接写 DB），
    /// 延后到 Turn 结束时由 <see cref="FlushPendingEventsAsync"/> 批量提交。
    /// </summary>
    private AgentRunExecutionState BufferEvent(AgentRunExecutionState state, AgentRunEventType type, string payload)
    {
        var @event = AgentRunEventChain.BuildEvent(
            state.Run.RunId,
            state.Run.WorkspaceId,
            state.EventSequence,
            type,
            state.Run.State,
            payload,
            state.EventChainHash);

        _pendingTurnEvents.Add(@event);

        return state with
        {
            EventSequence = state.EventSequence + 1,
            EventChainHash = @event.ContentHash
        };
    }

    /// <summary>
    /// 批量提交所有缓冲事件 + 可选 Run 状态 CAS + 可选 Checkpoint 游标，单事务提交。
    /// 将原本每事件一次 <see cref="IAgentRunEventStore.AppendAsync"/> 的网络往返
    /// 合并为 Turn 结束时一次 <see cref="IAgentRunEventStore.AppendBatchAsync"/>。
    /// </summary>
    private async Task FlushPendingEventsAsync(AgentRun run, CancellationToken cancellationToken)
    {
        if (_pendingTurnEvents.Count == 0)
        {
            return;
        }

        // 记录本批 flush 的事件数与是否含 checkpoint，用于成功后更新 _eventsSinceLastCheckpoint。
        var eventsBeingFlushed = _pendingTurnEvents.Count;
        var hasCheckpoint = _pendingTurnCheckpoint is not null;

        var runStateUpdate = new AgentRunStateUpdate
        {
            WorkspaceId = run.WorkspaceId,
            RunId = run.RunId,
            ExpectedCurrentState = _turnStartState,
            NewState = run.State,
            RunSnapshot = run,
            // 透传 lease token + fencing token，Postgres 实现在状态 CAS 与事件追加的
            // WHERE 子句中校验 lease 仍由当前实例持有；lease 被抢占时事务回滚并抛异常。
            LeaseToken = _leaseToken,
            FencingToken = _fencingToken
        };

        AgentCheckpointCursor? checkpointCursor = null;
        if (_pendingTurnCheckpoint is not null)
        {
            checkpointCursor = new AgentCheckpointCursor
            {
                WorkspaceId = run.WorkspaceId,
                RunId = run.RunId,
                CheckpointId = _pendingTurnCheckpoint.CheckpointId,
                LastEventSequence = _pendingTurnEvents[_pendingTurnEvents.Count - 1].Sequence
            };
        }

        try
        {
            if (_committer is not null)
            {
                // 统一提交入口：事件流 + 状态 CAS + checkpoint + 结算意图作为一次原子提交。
                // 终态语义（finished_at / 结算 outbox）由提交器从状态语义层派生，与 Run Store 一致。
                var commit = new AgentRunCommit
                {
                    Key = new TenantRunKey(run.WorkspaceId, run.RunId),
                    Events = _pendingTurnEvents,
                    ExpectedCurrentState = _turnStartState,
                    NewRunSnapshot = run,
                    Checkpoint = _pendingTurnCheckpoint,
                    UsageSnapshot = run.CostBudget,
                    LeaseToken = _leaseToken,
                    FencingToken = _fencingToken
                };
                await _committer.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _eventStore.AppendBatchAsync(
                    _pendingTurnEvents, runStateUpdate, checkpointCursor, _pendingTurnCheckpoint, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // 3c：flush 失败时清除 checkpoint 本体，避免 FailAsync/TryTransitionToCancelledAsync
            // 重试时再次尝试保存已失败的 checkpoint（导致终态 flush 也失败、事件丢失）。
            // checkpoint 本体保存失败不应阻止事件流（含 RunFailed/RunCancelled）的终态持久化。
            // 同步移除 CheckpointSaved 事件并重建哈希链，避免重试时持久化声明不存在的 checkpoint 的孤立事件。
            // 事件链是 SHA-256 哈希链，RemoveEventsAndRebuildChain 全链重建 Sequence/PrevChainHash/ContentHash。
            // 其他事件（StateTransition/RunFailed/RunCancelled 等）保留，确保终态可持久化。
            _pendingTurnCheckpoint = null;
            RemoveEventsAndRebuildChain(_pendingTurnEvents, AgentRunEventType.CheckpointSaved);
            throw;
        }

        _pendingTurnEvents.Clear();
        _turnStartState = run.State;
        _pendingTurnCheckpoint = null;

        // 更新未 checkpoint 事件计数。
        // 本批含 checkpoint → 计数归零（所有事件已被 checkpoint 覆盖）；
        // 本批无 checkpoint → 累加本批事件数（mid-turn flush 的事件仍未 checkpoint）。
        if (hasCheckpoint)
        {
            _eventsSinceLastCheckpoint = 0;
        }
        else
        {
            _eventsSinceLastCheckpoint += eventsBeingFlushed;
        }
    }

    /// <summary>
    /// 从待提交事件列表中移除指定类型的事件，并重建 SHA-256 哈希链。
    /// 事件链的 Sequence 单调递增、PrevChainHash 指向前一事件 ContentHash、ContentHash 包含 Sequence。
    /// 直接删除中间事件会破坏三者一致性，必须全链重建。
    /// </summary>
    private static void RemoveEventsAndRebuildChain(List<AgentRunEvent> events, AgentRunEventType typeToRemove)
    {
        if (events.Count == 0) return;
        var hasTarget = false;
        foreach (var e in events) { if (e.EventType == typeToRemove) { hasTarget = true; break; } }
        if (!hasTarget) return;
        var startSequence = events[0].Sequence;
        var startPrevChainHash = events[0].PrevChainHash;
        var runId = events[0].RunId;
        var workspaceId = events[0].WorkspaceId;
        var filtered = new List<AgentRunEvent>(events.Count);
        foreach (var e in events) { if (e.EventType != typeToRemove) filtered.Add(e); }
        events.Clear();
        var prevChainHash = startPrevChainHash;
        var sequence = startSequence;
        foreach (var evt in filtered)
        {
            var rebuilt = AgentRunEventChain.BuildEvent(runId, workspaceId, sequence, evt.EventType, evt.State, evt.Payload, prevChainHash);
            events.Add(rebuilt);
            prevChainHash = rebuilt.ContentHash;
            sequence++;
        }
    }

    /// <summary>
    /// 将 Run 标记为 Failed 并记录 RunFailed 事件；若存在未决对账记录（外部副作用真相未确认），
    /// 改停靠 AwaitingReconciliation（fail-closed：真相未确认前不得进入 Failed 并被自动重试）。
    /// 返回最终状态（Failed 或 AwaitingReconciliation）。
    /// </summary>
    private async Task<AgentRunExecutionState> FailAsync(
        AgentRunExecutionState state,
        string reason,
        CancellationToken cancellationToken)
    {
        // 未决对账门禁：存在 Pending/Running 对账记录 → 停靠 AwaitingReconciliation（等待对账完成），
        // 不写 Failed——避免"外部副作用真相未确认 + Run 进入 Failed + Scheduler 自动重试"违背
        // 真相未确认前不得推进的原则。仅当前状态允许停靠时执行（其余状态按原路径 Failed，
        // Scheduler 领取 SQL 另有一层未决对账排除作为兜底）。
        if (_reconciliationStore is not null)
        {
            try
            {
                var unresolved = await _reconciliationStore
                    .HasUnresolvedForRunAsync(state.Run.WorkspaceId, state.Run.RunId, cancellationToken)
                    .ConfigureAwait(false);
                if (unresolved)
                {
                    state = TransitionStateLocal(state, AgentRunState.AwaitingReconciliation);
                    await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
                    return state;
                }
            }
            catch (InvalidOperationException)
            {
                // 当前状态不允许停靠 AwaitingReconciliation → 按原路径 Failed（Scheduler 层另有兜底）。
            }
        }

        try
        {
            var fromState = state.Run.State;

            // Attempt 失败分类：重试预算未耗尽 → RetryPending（非终态，不结算配额、
            // 不写 finished_at——预留保留给下一 Attempt）；预算耗尽 → Failed（真终态，结算）。
            // 彻底拆开"Attempt 失败"与"Run 终态失败"两种含义：Failed 永不再被 Scheduler
            // 领取，Quota Settlement 只在真正终结时发生一次。
            var retryAvailable = state.Run.MaxRetries > 0 && state.Run.RetryCount < state.Run.MaxRetries;
            var targetState = retryAvailable ? AgentRunState.RetryPending : AgentRunState.Failed;

            // 更新本地 Run 副本（含失败原因 + ModelCallsUsed；非终态不写 FinishedAt）
            var failedRun = state.Run with
            {
                FailureReason = reason,
                ModelCallsUsed = _modelCallsUsed,
                TurnBudget = _turnBudget,
                CostBudget = _costBudget,
                UpdatedAt = DateTimeOffset.UtcNow,
                FinishedAt = retryAvailable ? null : DateTimeOffset.UtcNow
            };
            state = state with { Run = failedRun };

            // 推进到 RetryPending（重试可用）/ Failed（耗尽；本地 + 缓冲 StateTransition 事件；
            // 任意非终态可跳转；Scheduler 在退避门通过后领取 RetryPending）。
            state = TransitionStateLocal(state, targetState);

            // 缓冲 RunFailed 事件（Attempt 失败审计，含目标状态区分）
            state = BufferEvent(state, AgentRunEventType.RunFailed, JsonSerializer.Serialize(new
            {
                reason,
                fromState = fromState.ToString(),
                modelCallsUsed = _modelCallsUsed,
                turn = _currentTurn,
                terminal = !retryAvailable
            }));

            // 不可变 Attempt：重试 Attempt 失败追加 AttemptFailed 审计标记
            // （前序 Attempt 历史保留；Attempt 边界由 RunRetryScheduled 锚定）。
            if (state.Run.RetryCount > 0)
            {
                state = BufferEvent(state, AgentRunEventType.AttemptFailed, JsonSerializer.Serialize(new
                {
                    attempt = state.Run.RetryCount + 1,
                    reason
                }));
            }

            // 终态 flush — 批量提交所有缓冲事件 + state CAS（单事务，立即持久化）
            await FlushPendingEventsAsync(state.Run, CancellationToken.None).ConfigureAwait(false);
            return state;
        }
        catch
        {
            // 失败处理中的失败静默忽略，避免掩盖原始异常
            return state with { Run = state.Run with { State = AgentRunState.Failed } };
        }
    }

    /// <summary>
    /// 将 Run 标记为安全阻断（ContextSafetyBlocked，终态）并记录 RunFailed 事件 + 人工介入告警。
    /// 上下文构建判定为安全阻断（mandatory / hard constraint 正文缺失或不可用）时调用——
    /// 模型未运行，Run 终止等待人工介入。
    /// </summary>
    private async Task<AgentRunExecutionState> EnterSafetyBlockedStateAsync(
        AgentRunExecutionState state,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var fromState = state.Run.State;

            // 更新本地 Run 副本（含阻断原因）
            var blockedRun = state.Run with
            {
                FailureReason = reason,
                ModelCallsUsed = _modelCallsUsed,
                TurnBudget = _turnBudget,
                CostBudget = _costBudget,
                UpdatedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow
            };
            state = state with { Run = blockedRun };

            // 推进到 ContextSafetyBlocked（本地 + 缓冲 StateTransition 事件；终态）
            state = TransitionStateLocal(state, AgentRunState.ContextSafetyBlocked);

            // 缓冲 RunFailed 事件（安全阻断语义），终态 flush 单事务落库
            state = BufferEvent(state, AgentRunEventType.RunFailed, JsonSerializer.Serialize(new
            {
                reason,
                fromState = fromState.ToString(),
                modelCallsUsed = _modelCallsUsed,
                turn = _currentTurn
            }));
            await FlushPendingEventsAsync(state.Run, CancellationToken.None).ConfigureAwait(false);

            // 人工介入告警（best-effort，持久化成功后投递）
            if (_alertSink is not null)
            {
                var alert = new AgentRunAlert
                {
                    RunId = state.Run.RunId,
                    WorkspaceId = state.Run.WorkspaceId,
                    SessionId = state.Run.SessionId,
                    Kind = AgentRunAlertKind.ContextSafetyBlocked,
                    Reason = reason,
                    Attempt = 0
                };
                try
                {
                    await _alertSink.NotifyInterventionRequiredAsync(alert, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception alertEx)
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "[AgentRunActor] 投递安全阻断告警失败（run={0}，workspace={1}）：{2}。",
                        state.Run.RunId, state.Run.WorkspaceId, alertEx.Message);
                }
            }

            return state;
        }
        catch (Exception ex)
        {
            // 尽力而为：无法持久化安全阻断状态时记录警告，Run 保持原状态，不掩盖阻断事实。
            System.Diagnostics.Trace.TraceWarning(
                "[AgentRunActor] 持久化安全阻断状态失败（run={0}，workspace={1}）：{2}。",
                state.Run.RunId, state.Run.WorkspaceId, ex.Message);
            return state;
        }
    }

    /// <summary>尝试将 Run 标记为 Cancelled（外部取消）。</summary>
    private async Task TryTransitionToCancelledAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        try
        {
            // 推进到 Cancelled（本地 + 缓冲 StateTransition 事件；允许任意状态跳转 Cancelled）
            state = TransitionStateLocal(state, AgentRunState.Cancelled);
            var cancelledRun = state.Run with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow
            };
            state = state with { Run = cancelledRun };

            // 缓冲 RunCancelled 事件
            state = BufferEvent(state, AgentRunEventType.RunCancelled, JsonSerializer.Serialize(new
            {
                fromState = state.Run.State.ToString()
            }));

            // 终态 flush — 批量提交所有缓冲事件 + state CAS（单事务，立即持久化）
            await FlushPendingEventsAsync(state.Run, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 取消处理中的失败静默忽略
        }
    }
}
