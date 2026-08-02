using System.Collections.Concurrent;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 任务 E6：AgentRunActor — 单个 Agent Run 的执行者（per-run 实例）
//
// 负责单个 Run 的完整生命周期：
//   1. ContextBuilding → 调用 IContextDecisionRuntime 或直接构造上下文（子问题 1）
//   2. IAgentLoopPolicy.DecideAsync → 决定下一步（含 ModelCallsUsed 预检，子问题 2）
//   3. CallModel → IAgentModelTransport.CallAsync → 记录事件 → 持久化预算（子问题 2/3）
//   4. DispatchTool → IAgentToolCallValidator.ValidateAsync → IAgentApprovalGate →
//      IDurableToolExecutor.ExecuteAsync（子问题 5）→ 记录完整 Tool 身份事件（子问题 6）
//   5. Observing → 追加 Tool 结果到上下文
//   6. Checkpointing → IAgentCheckpointFactory.CreateAsync → IAgentCheckpointStore.SaveAsync
//      （子问题 4）→ 记录事件
//   7. 循环回到 1，直到 Complete/Failed/Cancelled
//
// 设计决策：
//   - 通过 IAgentRunStore.TransitionStateAsync 推进状态（CAS expected-state）
//   - 通过 IAgentRunEventStore.AppendAsync 写入审计事件（哈希链）
//   - 异常时 TransitionStateAsync → Failed 并记录 RunFailed 事件
//   - IAgentModelTransport / IContextDecisionRuntime 为 null 时优雅降级（兼容现有 Kernel）
//   - 子问题 1：ContextBuilding 阶段实际构建上下文（run.Task + session history + observations）
//   - 子问题 2：ModelCallsUsed 计数 + MaxModelCalls 预检防止无限循环
//   - 子问题 3：AgentModelResponse 扩展字段累积到 CostBudget 并 CAS 持久化
//   - 子问题 4：Checkpoint 先 SaveAsync 再记录事件（顺序保证）
//   - 子问题 5：通过 IDurableToolExecutor 走 Durable Journal（不再直接调 IToolDispatcher）
//   - 子问题 6：ToolCallCompleted payload 含完整 Tool 身份（RequestId/SideEffect/IdempotencyKey）
//
// P0-2 修复：
//   - 引入 AgentRunExecutionState 统一管理执行期可变状态（Run/Messages/LastModelResponse/
//     LastCheckpoint/EventSequence/EventChainHash），消除散落实例字段
//   - Bug 3：每次模型调用都计为一次 Turn（TurnBudget 递减），防止无 Tool 的模型循环无限运行
//   - Bug 4：Checkpoint SaveAsync 失败时显式捕获异常并转 Failed 状态（不记录 CheckpointSaved 事件）
//   - Bug 5：在 DispatchToolsAsync 开始时生成 toolCallId，同时用于 ToolCallStarted 和 ToolCallCompleted
// ===========================================================================

/// <summary>
/// 任务 E6：单个 Agent Run 的执行者（per-run 实例）。
/// </summary>
/// <remarks>
/// 每个 Run 由独立的 Actor 实例执行；Actor 之间通过 <see cref="AgentKernelHost"/> 隔离。
/// Actor 持有运行时累积的可变状态（上下文 / 模型响应 / Tool 结果），Run 结束后丢弃。
/// </remarks>
public sealed class AgentRunActor
{
    private readonly IAgentRunStore _runStore;
    private readonly IAgentRunEventStore _eventStore;
    private readonly IAgentModelTransport? _modelTransport;
    private readonly IAgentLoopPolicy _loopPolicy;
    private readonly IToolDispatcher _toolDispatcher;
    private readonly IAgentToolCallValidator? _toolCallValidator;
    private readonly IAgentApprovalGate? _approvalGate;
    private readonly IAgentApprovalStore? _approvalStore;
    private readonly IAgentCheckpointFactory? _checkpointFactory;
    private readonly IContextDecisionRuntime? _decisionRuntime;
    // 子问题 4：Checkpoint Store（保存 checkpoint 持久化）
    private readonly IAgentCheckpointStore? _checkpointStore;
    // 子问题 5：Durable Tool Executor（封装 journal + dispatch）
    private readonly IDurableToolExecutor? _durableToolExecutor;
    // P0-3：模型上下文投影器（从 WorkingSet.Materials 取正文 + Token 预算控制）
    private readonly IAgentModelContextProjector? _modelContextProjector;
    // P0-1：Tool 定义列表（从 RealToolDispatcher 构建，用于原生 function calling 声明）
    private IReadOnlyList<AgentToolDefinition> _toolDefinitions;  // P1-1: mutable for AllowedToolIds filtering in ExecuteAsync

    // 运行时累积状态（预算与计数，不在 AgentRunExecutionState 中，因为它们是 Run 的字段的可变副本）
    private int _currentTurn;
    // 子问题 2：模型调用次数计数（防止无限循环）
    private int _modelCallsUsed;
    // P0-6：当前执行期内的模型轮次计数（每次 ExecuteAsync 重置为 0）。
    // 用于 ComputeRequestId 的 modelTurn 参数，确保同一逻辑轮次在崩溃恢复后产生相同 RequestId
    // （_modelCallsUsed 是累积值，恢复后不重置，会导致同一逻辑轮次产生不同 modelTurn → 误重新 Dispatch）。
    private int _executionModelTurn;
    private AgentTurnBudget? _turnBudget;
    private AgentCostBudget? _costBudget;

    // G4：Turn 内事件批量缓冲（替代每次单独 AppendAsync）
    private readonly List<AgentRunEvent> _pendingTurnEvents = new();
    // G4：Turn 起始状态快照（用于批量提交时的 state CAS）
    private AgentRunState _turnStartState;
    // G4：Turn 内最新 checkpoint（用于批量提交时的 checkpoint cursor）
    private AgentCheckpoint? _pendingTurnCheckpoint;
    // G4：批量提交阈值（超过则 mid-turn 强制 flush）
    private const int PendingEventsFlushThreshold = 32;
    // P1-4: 强制 checkpoint 阈值 — 未 checkpoint 事件数达到此值时强制创建 checkpoint，
    // 防止事件流无限增长导致恢复时重放代价过大。
    private const int ForcedCheckpointEventThreshold = 1000;
    // P1-4: 事件恢复 keyset pagination 页大小（基于 sequence 索引的分页读取）。
    private const int RecoveryEventPageSize = 500;
    // P1-4: 自上次 checkpoint 以来已 flush 的事件数（用于强制 checkpoint 阈值判断）。
    private int _eventsSinceLastCheckpoint;

    // P0-4：当前 Run 的 lease token 与 fencing token（由 AgentKernelHost 在 ExecuteAsync 时注入）。
    // 非空时 FlushPendingEventsAsync 将它们写入 AgentRunStateUpdate，由 Postgres 实现在
    // 状态 CAS + 事件追加的 WHERE 子句中校验 lease 仍由当前实例持有。
    // null = 无 lease 路径（测试 / 外部取消 / 恢复 Worker 等不持有 lease 的调用方）。
    private string? _leaseToken;
    private long? _fencingToken;

    /// <summary>
    /// P0-2 重构：Agent Run 执行期状态（不可变记录，所有阶段方法返回新状态）。
    /// 统一管理 Run 元数据 / 结构化上下文 / 模型响应 / checkpoint / 事件序列与哈希链。
    /// </summary>
    private sealed record AgentRunExecutionState
    {
        /// <summary>当前 Run 元数据（本地副本，含 State/Turn/ModelCallsUsed/预算 等）。</summary>
        public required AgentRun Run { get; init; }

        /// <summary>
        /// G5：结构化 Agent 上下文状态（替代旧 List&lt;AgentMessage&gt; Messages）。
        /// 包含 SystemPrompt / Constraints / CurrentTask / 短期工作集 / Tool Observations /
        /// Stable Memory References / LastModelTurn；由 ProjectForModel 根据 TokenBudget 投影。
        /// </summary>
        public required AgentContextState Context { get; init; }

        /// <summary>最近一次模型响应（null = 首轮，尚未调用模型；与 Context.LastModelTurn 同步，保留供 IAgentLoopPolicy 使用）。</summary>
        public AgentModelResponse? LastModelResponse { get; init; }

        /// <summary>
        /// P1-2：最近一次模型响应的规范化 Tool 调用列表（与 <see cref="LastModelResponse"/> 同步生成）。
        /// null = 尚未调用模型或模型响应已分派完毕（DispatchToolsAsync 结束后置 null）。
        /// 非空时，<see cref="DispatchToolsAsync"/> 按 ordinal 索引取出 <see cref="NormalizedToolCall.InvocationId"/>
        /// 作为统一的 ToolCallId，确保 Assistant 消息 / 事件 / Journal / Tool Message 引用同一 ID。
        /// </summary>
        public List<NormalizedToolCall>? NormalizedToolCalls { get; init; }

        /// <summary>
        /// P0-3：最近一次 Context Decision Runtime 的执行结果（含 WorkingSet.Materials）。
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
        /// WP-1#6：待执行的 Tool 命令列表（审批恢复用）。
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
    /// <param name="toolDispatcher">Tool 分派器（子问题 5：仅当 durableToolExecutor=null 时使用）。</param>
    /// <param name="toolCallValidator">Tool 校验器（null 时跳过校验）。</param>
    /// <param name="approvalGate">审批门（null 时跳过审批）。</param>
    /// <param name="checkpointFactory">检查点工厂（null 时跳过 checkpoint）。</param>
    /// <param name="decisionRuntime">Context Decision Runtime（null 时直接构造上下文）。</param>
    /// <param name="checkpointStore">子问题 4：Checkpoint Store（null 时跳过 SaveAsync）。</param>
    /// <param name="durableToolExecutor">子问题 5：Durable Tool Executor（null 时回退到 IToolDispatcher）。</param>
    /// <param name="modelContextProjector">P0-3：模型上下文投影器（null 时回退到 AgentContextState.ProjectForModel）。</param>
    /// <param name="approvalStore">P0-2：审批持久化存储（null 时由 Gate 内部处理；注入后 Actor 用正确 workspaceId 创建审批记录）。</param>
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
        IAgentModelContextProjector? modelContextProjector = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
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
        // P0-1：从 RealToolDispatcher 构建 Tool 定义（原生 function calling）；
        // EchoToolDispatcher 或其他实现无 Tool 定义 → 空列表（模型不感知 Tool）。
        _toolDefinitions = (toolDispatcher as RealToolDispatcher)?.GetToolDefinitions()
            ?? Array.Empty<AgentToolDefinition>();
        _modelCallsUsed = 0;
        _turnStartState = AgentRunState.Created;
    }

    /// <summary>
    /// 执行 Agent Run 主循环，直到 Complete/Failed/Cancelled 或取消。
    /// </summary>
    /// <param name="run">待执行的 Run 元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="leaseToken">
    /// P0-4：可选 lease token，用于 fencing 校验。提供时（与 <paramref name="fencingToken"/> 同时提供），
    /// 所有副作用操作（状态 CAS / 事件追加）的 WHERE 子句将追加 lease_token + fencing_token 校验；
    /// lease 已被抢占时副作用失败，Actor 应中止。null = 无 lease 路径（测试 / 外部取消等）。
    /// </param>
    /// <param name="fencingToken">P0-4：可选 fencing token，与 <paramref name="leaseToken"/> 配合使用。</param>
    /// <remarks>
    /// 运行时能力补齐 — Resume from checkpoint：
    ///   当 <paramref name="run"/>.State != Created 时判定为崩溃恢复场景。
    ///   Actor 从事件流重建上下文（ToolObservations / EventSequence / EventChainHash），
    ///   并将本地状态规范化为 ContextBuilding（让 LoopPolicy 决定下一步：通常为 CallModel）。
    ///   LastModelResponse 在 resume 时置为 null（事件流中不含完整模型响应内容），
    ///   强制重新调用模型以避免基于残缺状态做决策。durable journal 保证已分派 Tool 不会被重复执行。
    /// </remarks>
    public async Task ExecuteAsync(
        AgentRun run,
        CancellationToken cancellationToken = default,
        string? leaseToken = null,
        long? fencingToken = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        // P0-4：保存 lease token 与 fencing token，供 FlushPendingEventsAsync 在批量提交时校验。
        // 两者必须同时为 null 或同时非 null（接口契约由调用方 AgentKernelHost 保证）。
        _leaseToken = leaseToken;
        _fencingToken = fencingToken;

        // 运行时能力补齐：检测 resume 场景
        // run.State != Created 表示 Run 之前已开始执行（崩溃/重启后由 RecoveryWorker 重新入队）
        var isResume = run.State != AgentRunState.Created;

        // P1-1锛氭寜 Run.AllowedToolIds 杩囨护妯″瀷鍙鐨?Tool Definitions锛堝湪妯″瀷璋冪敤鍓嶈繃婊わ級
        if (run.AllowedToolIds.Count > 0 && _toolDefinitions.Count > 0)
        {
            _toolDefinitions = _toolDefinitions
                .Where(t => run.AllowedToolIds.Contains(t.Name))
                .ToList();
        }

        // P0-2 重构：初始化 AgentRunExecutionState（统一管理执行期状态）
        // G5：用 AgentContextState 替代旧 List<AgentMessage> Messages，
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
        // 子问题 2：从 Run 元数据恢复 ModelCallsUsed（支持崩溃恢复后续跑）
        _modelCallsUsed = run.ModelCallsUsed;
        // P0-5：_executionModelTurn 先重置为 0，Resume 时由 RebuildStateFromEventsAsync
        // 从 ModelCallCompleted 事件流统计重建——避免恢复后从 0 重新计数导致 RequestId 改变。
        _executionModelTurn = 0;
        // G4：记录 Turn 起始状态，用于批量提交时的 state CAS
        // 运行时能力补齐：resume 时 _turnStartState = run.State（store 中的当前状态），
        // 后续 FlushPendingEventsAsync 的 CAS 以此为 expected state
        _turnStartState = run.State;
        _pendingTurnEvents.Clear();
        _pendingTurnCheckpoint = null;
        // P1-4: 重置未 checkpoint 事件计数
        _eventsSinceLastCheckpoint = 0;

        if (isResume)
        {
            // Resume：从事件流重建上下文
            state = await RebuildStateFromEventsAsync(state, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 全新启动：记录 RunCreated 事件（审计起点）— G4：缓冲到 _pendingTurnEvents，待 Turn 结束批量提交
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
                // 全新启动：Created → ContextBuilding（G4：本地推进 + 缓冲 StateTransition 事件，CAS 延后到批量提交）
                state = TransitionStateLocal(state, AgentRunState.ContextBuilding);
            }
            // Resume 场景：RebuildStateFromEventsAsync 已将本地状态规范化为 ContextBuilding，
            // 无需再推进（避免重复 StateTransition 事件）

            // 主循环
            while (!AgentRunStateMachine.IsTerminalState(state.Run.State) && !cancellationToken.IsCancellationRequested)
            {
                // P0-2：审批通过后从 PendingToolExecution 状态恢复——直接执行原 Tool，不重新调用模型。
                // WP-1#6：PendingToolCommands 为列表，依次执行同轮所有未完成 Tool Call。
                if (state.Run.State == AgentRunState.PendingToolExecution && state.PendingToolCommands is { Count: > 0 })
                {
                    state = await ExecutePendingToolAsync(state, cancellationToken).ConfigureAwait(false);
                    // G4：mid-turn 缓冲超过阈值时强制 flush
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
                        // G4：mid-turn 缓冲超过阈值时强制 flush，避免长 Turn 内存膨胀
                        if (_pendingTurnEvents.Count >= PendingEventsFlushThreshold)
                        {
                            await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
                            // P1-4: 强制 checkpoint 阈值检查 — 未 checkpoint 事件达阈值时记录警告。
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
                        // P0-6：若审批挂起（DispatchToolsAsync 已 flush 并返回 AwaitingApproval 状态），
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
            await FailAsync(state, ex.ToString(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 运行时能力补齐：从事件流重建 AgentRunExecutionState（崩溃恢复 / resume）。
    /// </summary>
    /// <param name="state">初始执行状态（Run + 默认 Context）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重建后的执行状态（含 ToolObservations / EventSequence / EventChainHash）。</returns>
    /// <remarks>
    /// <b>重建策略</b>：
    /// <list type="bullet">
    ///   <item>读取 Run 的完整事件流（按 Sequence 升序）。</item>
    ///   <item>从 ToolCallCompleted 事件解析 ToolObservation（含 output / error / succeeded / toolName / toolCallId）。</item>
    ///   <item>EventSequence / EventChainHash 从最后一个事件恢复（保证后续事件哈希链连续）。</item>
    ///   <item>LastModelResponse 置为 null（事件流中不含完整模型响应内容），强制重新调用模型。</item>
    ///   <item>本地状态规范化为 ContextBuilding（LoopPolicy 会决定 CallModel）。</item>
    /// </list>
    ///
    /// <b>状态一致性</b>：
    ///   - 本地状态（ContextBuilding）用于状态机校验（TransitionStateLocal）。
    ///   - Store 状态（run.State）用于 CAS（_turnStartState = run.State）。
    ///   - 两者可以不同：本地状态决定状态机校验是否通过，store 状态决定 CAS 是否匹配。
    ///   - 首次 FlushPendingEventsAsync 时 CAS 从 store 状态推进到新状态（store 不校验状态机流转）。
    ///
    /// <b>幂等性保证</b>：
    ///   重新调用模型后若返回相同 ToolCalls，IDurableToolExecutor 通过 journal 保证
    ///   已 commit 的 Tool 不会被重复执行（返回缓存结果）。
    ///
    /// <b>降级处理</b>：
    ///   若事件流为空（崩溃发生在首次 flush 之前），回退为全新启动路径。
    /// </remarks>
    private async Task<AgentRunExecutionState> RebuildStateFromEventsAsync(
        AgentRunExecutionState state,
        CancellationToken cancellationToken)
    {
        // P1-4: 获取最新 Checkpoint Cursor
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

        // P1-4: Keyset pagination
        var allEvents = new List<AgentRunEvent>();
        var fromSequence = 0;
        string? expectedPrevChainHash = null;

        try
        {
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
                    if (evt.Sequence != expectedSequence)
                    {
                        throw new InvalidOperationException(
                            $"事件序列号不连续：期望 {expectedSequence}，实际 {evt.Sequence}（run={state.Run.RunId}）。");
                    }
                    var expectedHash = (i == 0) ? expectedPrevChainHash : page[i - 1].ContentHash;
                    if (!string.Equals(evt.PrevChainHash, expectedHash, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
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
        }
        catch
        {
            // 事件流读取失败（store 不可用 / 跨 workspace 不可见 / 哈希链断裂）→ 回退为全新启动路径
            // 此时 _turnStartState = run.State（非 Created），首次 CAS 可能失败，
            // Actor 的 catch 块会转 Failed 状态
            state = BufferEvent(state, AgentRunEventType.RunCreated, JsonSerializer.Serialize(new
            {
                runId = state.Run.RunId,
                sessionId = state.Run.SessionId,
                task = state.Run.Task
            }));
            state = TransitionStateLocal(state, AgentRunState.ContextBuilding);
            return state;
        }

        if (allEvents.Count == 0)
        {
            // 无事件 — 崩溃发生在首次 flush 之前 → 回退为全新启动路径
            state = BufferEvent(state, AgentRunEventType.RunCreated, JsonSerializer.Serialize(new
            {
                runId = state.Run.RunId,
                sessionId = state.Run.SessionId,
                task = state.Run.Task
            }));
            state = TransitionStateLocal(state, AgentRunState.ContextBuilding);
            return state;
        }

        // P1-4: 从 Cursor 初始化 _eventsSinceLastCheckpoint。
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

        // P0-2：从事件流按时间顺序无损重建 Conversation（Assistant + Tool 消息）。
        // ModelCallCompleted 事件携带完整模型响应（content + toolCalls[]），重建 Assistant 消息。
        // ToolCallCompleted 事件携带 Tool 执行结果，重建 Tool 消息。
        // 按事件 Sequence 顺序遍历，保证 "assistant tool_calls → tool result" 因果顺序。
        // 旧事件缺少 content/toolCalls[] 字段时跳过 Assistant 重建（仅恢复 Tool 消息，向后兼容）。
        var toolObservations = new List<ToolObservation>();
        var rebuiltConversation = new List<AgentMessage>();
        foreach (var evt in events)
        {
            if (evt.EventType == AgentRunEventType.ModelCallCompleted)
            {
                // P0-2：从 ModelCallCompleted 重建 Assistant 消息
                try
                {
                    using var doc = JsonDocument.Parse(evt.Payload);
                    var root = doc.RootElement;
                    // 旧事件无 content 字段 → 跳过（向后兼容）
                    if (!root.TryGetProperty("content", out var contentProp))
                    {
                        continue;
                    }

                    var content = contentProp.GetString() ?? string.Empty;
                    List<AgentToolCallEntry>? toolCalls = null;
                    if (root.TryGetProperty("toolCalls", out var tcArrayEl)
                        && tcArrayEl.ValueKind == JsonValueKind.Array
                        && tcArrayEl.GetArrayLength() > 0)
                    {
                        toolCalls = new List<AgentToolCallEntry>(tcArrayEl.GetArrayLength());
                        foreach (var tcEl in tcArrayEl.EnumerateArray())
                        {
                            toolCalls.Add(new AgentToolCallEntry
                            {
                                Id = tcEl.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty,
                                Name = tcEl.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty,
                                Arguments = tcEl.TryGetProperty("arguments", out var argsProp) ? argsProp.GetString() ?? string.Empty : string.Empty
                            });
                        }
                    }

                    // 仅当有内容或 ToolCalls 时才追加（避免空消息污染对话流）
                    if (!string.IsNullOrEmpty(content) || toolCalls is { Count: > 0 })
                    {
                        rebuiltConversation.Add(new AgentMessage
                        {
                            Role = AgentMessageRole.Assistant,
                            Content = content,
                            ToolCalls = toolCalls
                        });
                    }
                }
                catch
                {
                    // 解析单个事件失败 → 跳过（不影响整体恢复）
                }
            }
            else if (evt.EventType == AgentRunEventType.ToolCallCompleted)
            {
                // 从 ToolCallCompleted 重建 ToolObservation + Tool 消息
                try
                {
                    using var doc = JsonDocument.Parse(evt.Payload);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("succeeded", out var succeededProp))
                    {
                        continue;
                    }

                    var succeeded = succeededProp.GetBoolean();
                    var toolName = root.TryGetProperty("toolName", out var tnProp) ? tnProp.GetString() ?? string.Empty : string.Empty;
                    var toolCallId = root.TryGetProperty("toolCallId", out var tcProp) ? tcProp.GetString() : null;
                    var output = root.TryGetProperty("output", out var outProp) ? outProp.GetString() : null;
                    var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : null;

                    var obs = new ToolObservation
                    {
                        ToolName = toolName,
                        ToolCallId = toolCallId,
                        Result = output,
                        Error = error,
                        Succeeded = succeeded
                    };
                    toolObservations.Add(obs);
                    rebuiltConversation.Add(obs.ToAgentMessage());
                }
                catch
                {
                    // 解析单个事件失败 → 跳过（不影响整体恢复）
                }
            }
        }

        // 从最后一个事件恢复 EventSequence / EventChainHash（保证哈希链连续）
        var lastEvent = events[events.Count - 1];

        // P0-5：从事件流统计 ModelCallCompleted 数量重建 _executionModelTurn。
        // 避免恢复后从 0 重新计数导致 RequestId 改变（Journal 无法识别原调用）。
        // 优先读取 ModelCallCompleted.executionModelTurn（新事件）；旧事件无此字段时降级为事件计数。
        var rebuiltModelTurn = 0;
        foreach (var evt in events)
        {
            if (evt.EventType != AgentRunEventType.ModelCallCompleted)
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(evt.Payload);
                if (doc.RootElement.TryGetProperty("executionModelTurn", out var emtEl)
                    && emtEl.ValueKind == JsonValueKind.Number)
                {
                    var v = emtEl.GetInt32();
                    if (v > rebuiltModelTurn) { rebuiltModelTurn = v; }
                }
                else
                {
                    // 旧事件无此字段 — 降级为计数（与原 _executionModelTurn 递增语义一致）
                    rebuiltModelTurn++;
                }
            }
            catch
            {
                // 解析失败 — 降级为计数
                rebuiltModelTurn++;
            }
        }
        _executionModelTurn = rebuiltModelTurn;

        // P0-2：审批通过后从 PendingToolExecution 状态恢复——不规范化为 ContextBuilding，
        // 而是从最后一个 ApprovalRequested 事件提取 PendingToolCommands，让主循环直接执行原 Tool。
        // 审批 API 在裁决时将 Run 状态推进到 PendingToolExecution（批准）或 Failed（拒绝）；
        // 此处仅处理 PendingToolExecution（Failed 已是终态，不会进入 resume）。
        if (state.Run.State == AgentRunState.PendingToolExecution)
        {
            var pendingCommands = ExtractPendingToolCommands(events);
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
    /// WP-1#6：从事件流中提取最后一个 ApprovalRequested 事件的 PendingToolCommands 列表。
    /// 审批通过后恢复时，Actor 据此依次执行所有 Pending Tool Call（不依赖模型重生成）。
    /// 兼容旧版单数 pendingToolCommand payload（P0-2 之前的事件）。
    /// </summary>
    /// <param name="events">Run 的完整事件流（按 Sequence 升序）。</param>
    /// <returns>提取的 PendingToolCommands 列表；事件 payload 损坏/无 ApprovalRequested 事件时返回 null。</returns>
    private static List<PendingToolCommand>? ExtractPendingToolCommands(IReadOnlyList<AgentRunEvent> events)
    {
        // 从后往前找最后一个 ApprovalRequested 事件
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var evt = events[i];
            if (evt.EventType != AgentRunEventType.ApprovalRequested)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(evt.Payload);
                var root = doc.RootElement;

                // WP-1#6：优先读取 pendingToolCommands（数组），兼容旧版 pendingToolCommand（单数）
                if (root.TryGetProperty("pendingToolCommands", out var ptcsProp) && ptcsProp.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<PendingToolCommand>();
                    foreach (var ptc in ptcsProp.EnumerateArray())
                    {
                        var cmd = ParsePendingToolCommand(ptc);
                        if (cmd is not null)
                        {
                            list.Add(cmd);
                        }
                    }
                    return list.Count > 0 ? list : null;
                }

                // 旧版事件 payload 仅有 pendingToolCommand（单数）→ 包装为单元素列表
                if (root.TryGetProperty("pendingToolCommand", out var ptcProp))
                {
                    var cmd = ParsePendingToolCommand(ptcProp);
                    return cmd is not null ? new List<PendingToolCommand> { cmd } : null;
                }

                // 旧版事件 payload 未携带 pendingToolCommand（P0-2 之前）→ 无法恢复
                return null;
            }
            catch
            {
                // 解析失败 → 继续找更早的 ApprovalRequested 事件
            }
        }

        return null;
    }

    /// <summary>
    /// WP-1#6：从 JSON 元素解析单个 PendingToolCommand。
    /// </summary>
    private static PendingToolCommand? ParsePendingToolCommand(JsonElement ptc)
    {
        var toolCallId = ptc.TryGetProperty("ToolCallId", out var tciProp) ? tciProp.GetString() ?? string.Empty : string.Empty;
        var toolName = ptc.TryGetProperty("ToolName", out var tnProp) ? tnProp.GetString() ?? string.Empty : string.Empty;
        var argumentsJson = ptc.TryGetProperty("ArgumentsJson", out var ajProp) ? ajProp.GetString() ?? string.Empty : string.Empty;
        var idempotencyKey = ptc.TryGetProperty("IdempotencyKey", out var ikProp) ? ikProp.GetString() : null;
        var modelTurnRevision = ptc.TryGetProperty("ModelTurnRevision", out var mtrProp) && mtrProp.ValueKind == JsonValueKind.Number ? mtrProp.GetInt32() : 0;

        if (string.IsNullOrEmpty(toolCallId) && string.IsNullOrEmpty(toolName))
        {
            return null;
        }

        return new PendingToolCommand
        {
            ToolCallId = toolCallId,
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            IdempotencyKey = idempotencyKey,
            ModelTurnRevision = modelTurnRevision
        };
    }

    /// <summary>
    /// WP-1#6：直接执行审批通过后的所有 Pending Tool（不重新调用模型）。
    /// 从 <see cref="AgentRunExecutionState.PendingToolCommands"/> 依次提取完整 Tool 调用信息，
    /// 通过 <see cref="IDurableToolExecutor"/>（或回退到 <see cref="IToolDispatcher"/>）执行，
    /// 记录 ToolCallStarted/Completed/ObservationAppended 事件，然后进入 Observing 继续循环。
    /// </summary>
    /// <remarks>
    /// WP-1#7：ToolCallStarted 事件必须在外部执行前持久化（先日志后执行）。
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

        // P0-3：首项为被审批的 Tool（已通过审批），直接执行。
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

            // P0-3：后续 Tool Call（非首项）必须走独立校验+审批流程
            if (cmdIndex > 0)
            {
                // P0-3：AllowedToolIds 检查
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

                // P0-3：Tool Schema 校验
                if (_toolCallValidator is not null)
                {
                    var validation = await _toolCallValidator.ValidateAsync(state.Run.RunId, toolCall, cancellationToken).ConfigureAwait(false);
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

                    // P0-3：后续 Tool Call 需要独立审批——不能因首项审批通过而隐式批准
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

                            // P0-3：保存剩余 PendingToolCommands（含当前需审批的 Tool），供恢复后执行
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

            // WP-1#7：先计算 RequestId 并持久化 ToolCallStarted 事件，再执行外部 Tool。
            var requestId = (_durableToolExecutor is not null)
                ? DefaultDurableToolExecutor.ComputeRequestId(state.Run.RunId, toolCall, pendingCommand.ModelTurnRevision)
                : pendingCommand.ToolCallId;

            state = BufferEvent(state, AgentRunEventType.ToolCallStarted, JsonSerializer.Serialize(new
            {
                toolName = pendingCommand.ToolName,
                toolCallId = pendingCommand.ToolCallId,
                requestId = requestId,
                idempotencyKey = pendingCommand.IdempotencyKey,
                resumedFromApproval = cmdIndex == 0
            }));

            // WP-1#7：flush 持久化 ToolCallStarted 后再执行外部 Tool（先日志后执行）。
            await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

            // 执行 Tool（复用 DurableToolExecutor 或回退到直接 Dispatcher）
            ToolExecutionResult? toolResult = null;
            if (_durableToolExecutor is not null)
            {
                // P0-4：构造 leaseFence 并传入，保护 Tool 副作用边界。
                // ExpiresAt 用 Run.DeadlineAt 保守推导（lease 不会超过 Run 超时）。
                var leaseFence1 = (_leaseToken is not null && _fencingToken is not null)
                    ? new AgentLeaseFence
                      {
                          LeaseToken = _leaseToken,
                          FencingToken = _fencingToken.Value,
                          ExpiresAt = state.Run.DeadlineAt ?? DateTimeOffset.UtcNow.AddMinutes(5)
                      }
                    : null;
                toolResult = await _durableToolExecutor.ExecuteAsync(
                    state.Run.RunId, state.Run.WorkspaceId, toolCall, pendingCommand.ModelTurnRevision,
                    cancellationToken, leaseFence1, state.Run.DeadlineAt).ConfigureAwait(false);
            }
            else
            {
                // 回退路径：直接调 IToolDispatcher（无 journal，无 durable 保证）
                var dispatchResult = await _toolDispatcher.DispatchAsync(new ToolDispatchRequest
                {
                    ToolName = toolCall.ToolName,
                    Payload = toolCall.Arguments,
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
            // P0-1：同步追加到统一对话流（审批恢复路径同样需要保持因果顺序）。
            state.Context.Conversation.Add(resumedObservation.ToAgentMessage());

            state = BufferEvent(state, AgentRunEventType.ObservationAppended, JsonSerializer.Serialize(new
            {
                toolName = pendingCommand.ToolName,
                observationLength = observation.Length
            }));

            // G4：mid-loop 缓冲超过阈值时强制 flush
            if (_pendingTurnEvents.Count >= PendingEventsFlushThreshold)
            {
                await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
            }
        }

        // 所有 Pending Tool 执行完成 → Observing（G4：本地推进）
        state = TransitionStateLocal(state, AgentRunState.Observing);

        // P1-4: Checkpointing（若有工厂）→ ContextBuilding（下一轮）
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

        // G4：Turn 结束 → 批量提交所有缓冲事件 + state CAS（单事务）
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
        // P0-5：在调用模型前检查 DeadlineAt（超时则 Fail，替代旧路径中 StartRunAsync 返回后立即 Dispose 的 linked CTS）。
        // 旧路径 linked CTS 在 HTTP 请求结束时被 Dispose，导致 Actor 收到 ObjectDisposedException；
        // 新路径由 Run.DeadlineAt 字段承载超时控制，Actor 在每次模型调用前检查。
        if (state.Run.DeadlineAt is not null && DateTimeOffset.UtcNow > state.Run.DeadlineAt)
        {
            await FailAsync(state,
                $"Run 超时：已超过执行截止时间（DeadlineAt={state.Run.DeadlineAt:O}）。",
                cancellationToken).ConfigureAwait(false);
            return state with { Run = state.Run with { State = AgentRunState.Failed } };
        }

        // 进入 ModelCalling（G4：本地推进 + 缓冲 StateTransition 事件，CAS 延后到批量提交）
        state = TransitionStateLocal(state, AgentRunState.ModelCalling);

        // G1：构建结构化上下文（首次追加 User(run.Task) + 可选 System(decisionContext)；后续轮次复用 Messages）
        state = await BuildContextAsync(state, cancellationToken).ConfigureAwait(false);

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
            // G5：同步更新 Context.LastModelTurn 和 LastModelResponse（后者供 IAgentLoopPolicy 使用）
            // P1-2：degraded 响应无 ToolCalls，NormalizedToolCalls 置空避免上一轮残留。
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

        // P0-3：由 IAgentModelContextProjector 投影最终模型输入（从 WorkingSet.Materials 取正文 + Token 预算控制）。
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

        // G1：仅在事件 payload 中携带 contextLength（不再传字符串给 Transport）
        var contextLength = AgentMessage.Serialize(projectedMessages).Length;

        // 记录 ModelCallStarted
        state = BufferEvent(state, AgentRunEventType.ModelCallStarted, JsonSerializer.Serialize(new
        {
            turn = _currentTurn,
            contextLength
        }));

        // P0-1：调用 AgentModelRequest 重载（携带 Tool 定义 + 模型工件 + 截止时间，支持原生 function calling）。
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
        // G5：同步更新 Context.LastModelTurn 和 LastModelResponse
        state = state with
        {
            LastModelResponse = response,
            Context = state.Context with { LastModelTurn = response }
        };

        // 子问题 2：递增模型调用计数
        _modelCallsUsed++;
        // P0-6：递增执行期内模型轮次计数（用于 RequestId 的 modelTurn）
        _executionModelTurn++;

        // P1-2：模型响应进入 Actor 后立刻生成不可变的 NormalizedToolCall 列表。
        // InvocationId = {runId}_{executionModelTurn}_{ordinal}（确定性、可重建），
        // 后续 Assistant 消息 / ModelCallCompleted 事件 / DispatchToolsAsync / 审批 / Journal / Tool Message
        // 全部引用此 InvocationId，消除两条路径分别 Guid.NewGuid() 产生不同 ID 的问题。
        var normalizedToolCalls = NormalizeToolCalls(state.Run.RunId, _executionModelTurn, response.ToolCalls);
        state = state with { NormalizedToolCalls = normalizedToolCalls };

        // 子问题 3：累积 token + 费用到 _costBudget
        if (_costBudget is not null)
        {
            var billedCost = response.BilledCost > 0 ? response.BilledCost : response.EstimatedCost;
            _costBudget = _costBudget with
            {
                TokensUsed = _costBudget.TokensUsed + response.TokensConsumed,
                CostUsedUsd = _costBudget.CostUsedUsd + billedCost
            };
        }

        // 子问题 2/3：同步 _turnBudget 的 ModelCallsUsed 计数
        if (_turnBudget is not null)
        {
            _turnBudget = _turnBudget with { ModelCallsUsed = _modelCallsUsed };
        }

        // P0-2 Bug 3 修复：每次模型调用都计为一次 Turn（TurnBudget 递减），
        // 防止模型连续返回"非最终答案且无 ToolCalls"时 Turn 不增长导致无限循环
        _currentTurn++;
        if (_turnBudget is not null)
        {
            _turnBudget = _turnBudget with { TurnsUsed = _turnBudget.TurnsUsed + 1 };
        }

        // P0-2：始终追加 Assistant 消息（原生 function calling 响应可能 Content 为空 + ToolCalls 非空）。
        // 旧路径仅在 Content 非空时追加，导致多轮 Tool 调用协议中断——模型在下一轮看不到自己上一轮
        // 发起的 Tool 调用请求，OpenAI / Anthropic 兼容 API 会拒绝无前置 Assistant tool_calls 的 Tool 消息。
        // P1-2：ToolCalls[].Id 使用 NormalizedToolCall.InvocationId（确定性、与分派路径一致），
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
        // P0-1：同步追加到统一对话流，保持 Function Calling 消息因果顺序。
        // 投影器从 Conversation 按原子协议单元保序裁剪，避免 Messages 与 ToolObservations 分离投影。
        state.Context.Conversation.Add(assistantMessage);

        // G4：更新本地 Run 副本（不再单独调 _runStore.UpdateAsync；CAS + 字段更新延后到批量提交）
        // P0-2 Bug 3 修复：同步 run.CostBudget（从模型响应中获取实际 token cost）
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
            // P0-5：持久化执行期模型轮次，支持崩溃恢复时重建 _executionModelTurn，
            // 避免恢复后从 0 重新计数导致 RequestId 改变（Journal 无法识别原调用）。
            executionModelTurn = _executionModelTurn,
            durationMs = response.Duration.TotalMilliseconds,
            // P0-2：持久化完整模型响应，支持崩溃恢复时无损重建 Conversation。
            // 旧事件缺少此字段时恢复路径跳过 Assistant 重建（仅恢复 Tool 消息，向后兼容）。
            // P1-2：toolCalls[].id 使用 NormalizedToolCall.InvocationId（与 Assistant 消息 / 分派路径一致）。
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
    /// G1/G5：构建结构化上下文。
    /// G5：User(run.Task) 不再追加到 Messages，而是由 AgentContextState.CurrentTask 持有，
    ///     ProjectForModel 投影时合成 User 消息（消除 hasUserMessage 去重检查）。
    /// P0-3：若 IContextDecisionRuntime 注入，执行决策并存储 ContextDecisionExecutionResult 到
    ///       state.LastDecisionResult，由 IAgentModelContextProjector 在投影时从 WorkingSet.Materials
    ///       取出候选正文。不再每轮追加 System(retrievedContext) 消息（避免重复 + 让投影器统一管理）。
    /// </summary>
    /// <param name="state">当前执行状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<AgentRunExecutionState> BuildContextAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        // G5：CurrentTask 已在 ExecuteAsync 初始化时设置为 run.Task，
        // ProjectForModel 会将其投影为 User 消息，无需在此追加。
        // 旧路径的 hasUserMessage 去重检查随之移除（CurrentTask 是单值字段，天然不会重复）。

        // P0-3：若 IContextDecisionRuntime 注入，执行决策获取 WorkingSet（含 Materials 正文）
        if (_decisionRuntime is not null)
        {
            var decisionResult = await TryExecuteDecisionAsync(state.Run, cancellationToken).ConfigureAwait(false);
            if (decisionResult is not null)
            {
                // P0-3：存储决策结果供投影器使用（不再追加 System 消息摘要到 Messages）
                state = state with { LastDecisionResult = decisionResult };
            }
            else
            {
                // 决策失败或无选中候选 → 清空上轮的决策结果（避免投影器使用过期 Materials）
                state = state with { LastDecisionResult = null };
            }
        }

        return state;
    }

    /// <summary>
    /// P0-3：调用 IContextDecisionRuntime 执行决策，返回含 WorkingSet.Materials 的完整执行结果。
    /// 失败时返回 null（降级为仅 Task + History，投影器不注入 Retrieved Materials）。
    /// </summary>
    private async Task<ContextDecisionExecutionResult?> TryExecuteDecisionAsync(AgentRun run, CancellationToken cancellationToken)
    {
        if (_decisionRuntime is null)
        {
            return null;
        }

        try
        {
            var scope = new ContextDecisionScope(run.WorkspaceId, run.WorkspaceId);
            var agentSession = new AgentSessionId
            {
                Value = run.SessionId,
                WorkspaceId = run.WorkspaceId,
                CollectionId = run.WorkspaceId,
                CreatedAt = run.CreatedAt
            };

            var request = new ContextDecisionRuntimeRequest
            {
                RequestId = $"{run.RunId}-ctx-{_modelCallsUsed}",
                Scope = scope,
                Purpose = ContextDecisionPurpose.AgentContext,
                QueryText = run.Task,
                TokenBudget = 0,
                TopK = 0,
                // P3 Fix-1：激活 Late Hydration — Provider 仅召回 metadata（IncludeContent=false），
                // Engine 选出 SelectedEnvelopes 后由 ISelectedCandidateHydrator 批量 hydrate 正文，
                // 避免对未选中候选做无用正文 I/O。
                RetrievalInput = new RetrievalInput { IncludeContent = false },
                AgentInput = new AgentInput
                {
                    Session = agentSession,
                    RequiredIds = Array.Empty<string>()
                }
            };

            // P0-3：使用 ExecuteWithWorkingSetAsync 获取完整 WorkingSet（Envelopes + Materials）
            // 让投影器从 Materials 恢复候选正文，而不只是 CandidateId/Type/Score 摘要
            var result = await _decisionRuntime.ExecuteWithWorkingSetAsync(request, cancellationToken).ConfigureAwait(false);

            // P1-1：hydration 失败严重度处理。
            // 1) 日志：hydration.failedCount > 0 / hydration.budgetExceeded 时记录 Trace 警告（可观测性）。
            // 2) fail-closed：任一 Selected hard constraint / mandatory 候选正文为空时，
            //    决策结果不可用（模型绝不能在缺失 mandatory 上下文的情况下运行），返回 null 降级。
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

                // P1-1：AgentContext fail-closed — 预算修复后 mandatory 独占仍超限
                // （exact tokenize 后实际 token 数 > 模型上下文窗口），决策结果不可用。
                if (diagnostics.ContainsKey("hydration.budgetExceeded"))
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "[AgentRunActor] Fail-closed: hydration budget exceeded after exact tokenize for run {0}; mandatory items alone exceed token budget. Decision result discarded.",
                        run.RunId);
                    return null;
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
                        return null;
                    }
                }
            }

            return result;
        }
        catch
        {
            // Decision Runtime 失败 → 降级为 null（不阻断模型调用）
            return null;
        }
    }

    /// <summary>执行 DispatchTool 阶段（含校验 + 审批 + 分派 + 观察）。</summary>
    private async Task<AgentRunExecutionState> DispatchToolsAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        if (state.LastModelResponse is null || state.LastModelResponse.ToolCalls.Count == 0)
        {
            // 无 Tool 调用 → 回到 ContextBuilding
            return TransitionStateLocal(state, AgentRunState.ContextBuilding);
        }

        // 进入 ToolDispatching（G4：本地推进 + 缓冲 StateTransition 事件）
        state = TransitionStateLocal(state, AgentRunState.ToolDispatching);

        for (var toolIndex = 0; toolIndex < state.LastModelResponse.ToolCalls.Count; toolIndex++)
        {
            var toolCall = state.LastModelResponse.ToolCalls[toolIndex];

            // P0-2 Bug 5 修复：在循环开始时生成 toolCallId，同时用于 ToolCallStarted 和 ToolCallCompleted
            // 确保 ToolCallStarted 事件和 ToolCallCompleted 事件的审计 ID 一致。
            // P0-2 多轮协议修复：优先使用模型返回的 ToolCallId（如 OpenAI 的 tool_call_id），
            // 确保 Tool 观察消息的 tool_call_id 与 Assistant 消息的 tool_calls[].id 一致——
            // OpenAI / Anthropic 兼容 API 要求二者匹配，否则第二轮调用会被拒绝。
            // P1-2：优先使用 NormalizedToolCall.InvocationId（由 CallModelAsync 在模型响应进入 Actor
            // 后立即生成，与 Assistant 消息 / ModelCallCompleted 事件 / Tool Message 引用同一 ID）。
            // 回退到 toolCall.ToolCallId / Guid 仅为防御性兼容（如 resume 路径未重建 NormalizedToolCalls）。
            var normalized = (state.NormalizedToolCalls is not null && toolIndex < state.NormalizedToolCalls.Count)
                ? state.NormalizedToolCalls[toolIndex]
                : null;
            var toolCallId = normalized?.InvocationId ?? toolCall.ToolCallId ?? Guid.NewGuid().ToString("N");

            // P0-5：强制 Run 约束的 Tool 白名单（AllowedToolIds 非空时仅允许集合中的 Tool）。
            // 旧路径未写入 AllowedToolIds，Actor 无法按 Run 限定 Tool 集；新路径从 API 入参写入并在此强制。
            if (state.Run.AllowedToolIds.Count > 0
                && !string.IsNullOrWhiteSpace(toolCall.ToolName)
                && !state.Run.AllowedToolIds.Contains(toolCall.ToolName))
            {
                state = BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
                    toolCallId: toolCallId,
                    requestId: null,
                    toolName: toolCall.ToolName,
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

            // 1. 校验
            if (_toolCallValidator is not null)
            {
                var validation = await _toolCallValidator.ValidateAsync(state.Run.RunId, toolCall, cancellationToken).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    // 子问题 6：校验失败的 ToolCallCompleted 也含 toolCallId（便于恢复时定位）
                    state = BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
                        toolCallId: toolCallId,
                        requestId: null,
                        toolName: toolCall.ToolName,
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

                    // P0-2：持久化完整 PendingToolCommand 到 ApprovalRequested 事件 payload，
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

                    // WP-1#6：构建完整 PendingToolCommands 列表（当前 + 同轮后续未处理的 Tool Call）。
                    // 旧路径仅保存单数 PendingToolCommand，审批中断时同轮后续 Tool Call 被丢弃。
                    // P1-2：remainingToolCallId 优先使用 NormalizedToolCall.InvocationId，确保审批恢复后
                    // ExecutePendingToolAsync 使用的 ID 与原 Assistant 消息 / 事件一致。
                    var pendingCommands = new List<PendingToolCommand> { pendingCommand };
                    for (var j = toolIndex + 1; j < state.LastModelResponse.ToolCalls.Count; j++)
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

                    // P0-3：Gate 是审批记录的唯一创建者。Actor 不再直接写 IAgentApprovalStore——
                    // 旧路径 Actor 与 Gate 各 CreateAsync 一次，产生重复 Pending 记录（同 toolCallId 二次插入被
                    // ON CONFLICT DO NOTHING 吞掉，但 workspaceId 不一致时会出现两条记录）。
                    // 现统一由 Gate 用正确 workspaceId 创建记录（见 DefaultAgentApprovalGate.TryPersistApprovalAsync）。
                    // 使用 Actor 生成的 toolCallId 作为 ApprovalId，确保事件流与审批记录一致。
                    var approval = await _approvalGate.RequestApprovalAsync(
                        state.Run.WorkspaceId, state.Run.RunId, toolCall, cancellationToken).ConfigureAwait(false);

                    // P0-6：区分三种审批结果——PendingApproval（挂起等待人工）/ Approved（批准）/ Rejected（拒绝）
                    if (approval.PendingApproval)
                    {
                        // P0-3：使用 Gate 返回的 ApprovalId 作为外部 POST 端点定位键。
                        // Gate 是审批记录的唯一创建者，其内部 toolCallId 即为 store 中的 ApprovalId。
                        // 未注入 store 时 Gate 返回的 ApprovalId 仍可用于事件流审计，回退到 Actor 的 toolCallId。
                        var effectiveApprovalId = approval.ApprovalId ?? toolCallId;

                        // P0-6：审批挂起 — 记录 ApprovalResolved(pending) 事件 + approvalId，
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

                        // WP-1#6：将完整 PendingToolCommands 列表保存到执行状态，供恢复时依次执行。
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

            // WP-1#7：先计算 RequestId 并持久化 ToolCallStarted 事件，再执行外部 Tool。
            // 旧路径在执行后才缓冲 ToolCallStarted，崩溃时无法审计已发起的调用。
            var requestIdForStart = (_durableToolExecutor is not null)
                ? DefaultDurableToolExecutor.ComputeRequestId(state.Run.RunId, toolCall, _executionModelTurn)
                : toolCallId;

            state = BufferEvent(state, AgentRunEventType.ToolCallStarted, JsonSerializer.Serialize(new
            {
                toolName = toolCall.ToolName,
                toolCallId = toolCallId,
                requestId = requestIdForStart,
                idempotencyKey = toolCall.IdempotencyKey
            }));

            // WP-1#7：flush 持久化 ToolCallStarted 后再执行外部 Tool（先日志后执行）。
            await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

            // 子问题 5：通过 IDurableToolExecutor 执行（若注入），否则回退到直接 IToolDispatcher
            ToolExecutionResult? toolResult = null;
            if (_durableToolExecutor is not null)
            {
                // P0-4：构造 leaseFence 并传入，保护 Tool 副作用边界。
                var leaseFence2 = (_leaseToken is not null && _fencingToken is not null)
                    ? new AgentLeaseFence
                      {
                          LeaseToken = _leaseToken,
                          FencingToken = _fencingToken.Value,
                          ExpiresAt = state.Run.DeadlineAt ?? DateTimeOffset.UtcNow.AddMinutes(5)
                      }
                    : null;
                toolResult = await _durableToolExecutor.ExecuteAsync(
                    state.Run.RunId, state.Run.WorkspaceId, toolCall, _executionModelTurn,
                    cancellationToken, leaseFence2, state.Run.DeadlineAt).ConfigureAwait(false);
            }
            else
            {
                // 回退路径：直接调 IToolDispatcher（无 journal，无 durable 保证）
                // P0-2 Bug 5 修复：使用预生成的 toolCallId 作为 RequestId（与 ToolCallStarted/Completed 一致）
                var dispatchResult = await _toolDispatcher.DispatchAsync(new ToolDispatchRequest
                {
                    ToolName = toolCall.ToolName,
                    Payload = toolCall.Arguments,
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

            // 4. 记录 ToolCallCompleted（子问题 6：含完整 Tool 身份信息；P0-2 Bug 5 修复：使用同一 toolCallId）
            state = BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
                toolCallId: toolCallId,
                requestId: toolResult.RequestId,
                toolName: toolCall.ToolName,
                idempotencyKey: toolResult.IdempotencyKey,
                sideEffect: toolResult.SideEffect.ToString(),
                externalOperationId: toolResult.ExternalOperationId,
                journalState: toolResult.JournalState.ToString(),
                succeeded: toolResult.Succeeded,
                output: toolResult.Result,
                error: toolResult.Error,
                durationMs: toolResult.Duration.TotalMilliseconds)));

            // G5：观察结果以结构化 ToolObservation 形式追加到 Context.ToolObservations
            // （替代旧路径直接 Add AgentMessage 到 Messages）；
            // ProjectForModel 投影时一次性合成 Tool 角色 AgentMessage，避免在每次模型响应/Tool 观察时复制既有字符串。
            var observation = toolResult.Succeeded
                ? $"{toolResult.Result}"
                : $"[ERROR] {toolResult.Error}";

            var toolObservation = new ToolObservation
            {
                ToolName = toolCall.ToolName,
                ToolCallId = toolCallId,
                Result = toolResult.Result,
                Error = toolResult.Error,
                Succeeded = toolResult.Succeeded
            };
            state.Context.ToolObservations.Add(toolObservation);
            // P0-1：同步追加 Tool 消息到统一对话流，紧随引发它的 Assistant ToolCall 之后，
            // 保持 "assistant tool_calls → tool result" 因果顺序（OpenAI/Anthropic 协议要求）。
            state.Context.Conversation.Add(toolObservation.ToAgentMessage());

            // 5. ObservationAppended 事件 payload 用序列化后的 observation 长度（与旧路径兼容）
            state = BufferEvent(state, AgentRunEventType.ObservationAppended, JsonSerializer.Serialize(new
            {
                toolName = toolCall.ToolName,
                observationLength = observation.Length
            }));

            // G4：mid-loop 缓冲超过阈值时强制 flush，避免大量 Tool 调用导致内存膨胀
            if (_pendingTurnEvents.Count >= PendingEventsFlushThreshold)
            {
                await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
            }
        }

        // Tool 分派完成 → Observing（G4：本地推进）
        state = TransitionStateLocal(state, AgentRunState.Observing);

        // 清除 LastModelResponse：Tool 已分派完毕，下一轮应由 LoopPolicy 决定 CallModel
        // （而非重复 DispatchTool）。未清除会导致 ContextBuilding → ToolDispatching 非法转换。
        // Context.LastModelTurn 保留（供 ProjectForModel 投影历史上下文）。
        // P1-2：同步清除 NormalizedToolCalls（已分派完毕，避免下一轮残留）。
        state = state with { LastModelResponse = null, NormalizedToolCalls = null };

        // P0-2 Bug 3 修复：Turn 已在 CallModelAsync 中递增（每次模型调用计为一次 Turn）
        // 此处不再重复递增 Turn（避免双重计数）

        // G4：更新本地 Run 副本（不再单独调 _runStore.UpdateAsync；CAS + 字段更新延后到批量提交）
        var updatedRun = state.Run with
        {
            Turn = _currentTurn,
            ModelCallsUsed = _modelCallsUsed,
            TurnBudget = _turnBudget,
            CostBudget = _costBudget,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        state = state with { Run = updatedRun };

        // P1-4: Checkpointing（若有工厂）→ ContextBuilding（下一轮）
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

        // G4：Turn 结束 → 批量提交所有缓冲事件 + state CAS + checkpoint cursor（单事务）
        await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

        return state;
    }

    /// <summary>
    /// 子问题 6：构建 ToolCallCompleted 事件 payload（含完整 Tool 身份信息）。
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
        // 子问题 6：新增字段（toolCallId / requestId / idempotencyKey / sideEffect /
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

    /// <summary>子问题 6：计算结果摘要（SHA-256，小写 hex）。</summary>
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
    /// P1-2：将模型响应的 ToolCalls 规范化为不可变 <see cref="NormalizedToolCall"/> 列表。
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
    /// 子问题 4：Create checkpoint → Save checkpoint（IAgentCheckpointStore.SaveAsync）→
    /// 缓冲 CheckpointSaved event → 本地推进到 ContextBuilding。
    /// 顺序必须是保存成功后才记录事件（不能先记录成功事件再保存）。
    /// P0-2 Bug 4 修复：SaveAsync 失败时显式捕获异常，转 Failed 状态，不记录 CheckpointSaved 事件。
    /// G4：状态推进与事件均缓冲到 _pendingTurnEvents，CAS 延后到 Turn 结束批量提交。
    /// </remarks>
    private async Task<AgentRunExecutionState> PersistCheckpointAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        // 进入 Checkpointing（G4：本地推进 + 缓冲 StateTransition 事件）
        state = TransitionStateLocal(state, AgentRunState.Checkpointing);

        if (_checkpointFactory is not null)
        {
            var checkpointId = $"run-{state.Run.RunId}-turn-{_currentTurn}-{Guid.NewGuid():N}";
            var checkpoint = await _checkpointFactory.CreateCheckpointAsync(
                checkpointId, state.Run.SessionId, state.Run.WorkspaceId, cancellationToken).ConfigureAwait(false);

            // P1-4: 将 executionModelTurn 写入 checkpoint metadata，支持恢复时从 Checkpoint Cursor
            // 重建 _executionModelTurn（无需读取 checkpoint 之前的 ModelCallCompleted 事件）。
            // 与 RebuildStateFromEventsAsync 中的 _executionModelTurn 重建逻辑配合：
            // 有 Cursor 时优先从 metadata 读取，无 Cursor 时降级为事件计数。
            if (checkpoint.Metadata is null || !checkpoint.Metadata.ContainsKey("executionModelTurn"))
            {
                var enrichedMetadata = new Dictionary<string, string>(
                    checkpoint.Metadata ?? new Dictionary<string, string>(0),
                    StringComparer.Ordinal)
                {
                    ["executionModelTurn"] = _executionModelTurn.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
                checkpoint = checkpoint with { Metadata = enrichedMetadata };
            }

            // 3c：checkpoint 本体不再单独 SaveAsync，而是缓冲到 _pendingTurnCheckpoint，
            // 随 Turn 结束的 AppendBatchAsync 在同一事务内持久化（Postgres：INSERT agent_checkpoints；
            // InMemory：委托注入的 IAgentCheckpointStore）。事件与 checkpoint 原子提交，顺序保证更强。
            //
            // P0-2 Bug 4 修复语义保留：若批量提交（含 checkpoint INSERT）失败，CheckpointSaved 事件
            // 也在同一批中回滚，不会出现"事件已记录但 checkpoint 未保存"的不一致。

            // P0-2 重构：更新 state.LastCheckpoint
            state = state with { LastCheckpoint = checkpoint };

            // G4：记录 Turn 内最新 checkpoint，用于批量提交时的 checkpoint cursor + checkpoint 本体
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

        // Checkpointing → ContextBuilding（循环继续）（G4：本地推进）
        state = TransitionStateLocal(state, AgentRunState.ContextBuilding);

        return state;
    }

    /// <summary>执行 Complete 阶段。</summary>
    private async Task<AgentRunExecutionState> CompleteAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        var finalAnswer = state.LastModelResponse?.Content ?? string.Empty;

        // G4：更新本地 Run 副本（含最终答案 + ModelCallsUsed）
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

        // G4：推进到 Completed（本地 + 缓冲 StateTransition 事件）
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

        // G4：终态 flush — 批量提交所有缓冲事件 + state CAS（单事务，立即持久化）
        await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

        return state;
    }

    /// <summary>
    /// G4：本地推进 Run 状态（不直接写 DB），同时缓冲 StateTransition 事件。
    /// CAS 与字段更新延后到 Turn 结束时的 <see cref="FlushPendingEventsAsync"/> 批量提交。
    /// </summary>
    /// <param name="state">当前执行状态。</param>
    /// <param name="newState">目标状态。</param>
    /// <returns>更新后的执行状态（Run.State = newState）。</returns>
    private AgentRunExecutionState TransitionStateLocal(AgentRunExecutionState state, AgentRunState newState)
    {
        // 校验状态机
        AgentRunStateMachine.ValidateTransition(state.Run.State, newState);

        var updatedRun = state.Run with
        {
            State = newState,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var newStateObj = state with { Run = updatedRun };

        // 缓冲 StateTransition 事件
        return BufferEvent(newStateObj, AgentRunEventType.StateTransition, JsonSerializer.Serialize(new
        {
            from = state.Run.State.ToString(),
            to = newState.ToString()
        }));
    }

    /// <summary>
    /// G4：缓冲事件到 <see cref="_pendingTurnEvents"/>（不直接写 DB），
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
    /// G4：批量提交所有缓冲事件 + 可选 Run 状态 CAS + 可选 Checkpoint 游标，单事务提交。
    /// 将原本每事件一次 <see cref="IAgentRunEventStore.AppendAsync"/> 的网络往返
    /// 合并为 Turn 结束时一次 <see cref="IAgentRunEventStore.AppendBatchAsync"/>。
    /// </summary>
    private async Task FlushPendingEventsAsync(AgentRun run, CancellationToken cancellationToken)
    {
        if (_pendingTurnEvents.Count == 0)
        {
            return;
        }

        // P1-4: 记录本批 flush 的事件数与是否含 checkpoint，用于成功后更新 _eventsSinceLastCheckpoint。
        var eventsBeingFlushed = _pendingTurnEvents.Count;
        var hasCheckpoint = _pendingTurnCheckpoint is not null;

        var runStateUpdate = new AgentRunStateUpdate
        {
            WorkspaceId = run.WorkspaceId,
            RunId = run.RunId,
            ExpectedCurrentState = _turnStartState,
            NewState = run.State,
            RunSnapshot = run,
            // P0-4：透传 lease token + fencing token，Postgres 实现在状态 CAS 与事件追加的
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
            await _eventStore.AppendBatchAsync(
                _pendingTurnEvents, runStateUpdate, checkpointCursor, _pendingTurnCheckpoint, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 3c：flush 失败时清除 checkpoint 本体，避免 FailAsync/TryTransitionToCancelledAsync
            // 重试时再次尝试保存已失败的 checkpoint（导致终态 flush 也失败、事件丢失）。
            // checkpoint 本体保存失败不应阻止事件流（含 RunFailed/RunCancelled）的终态持久化。
            // P0-11：同步移除 CheckpointSaved 事件并重建哈希链，避免重试时持久化声明不存在的 checkpoint 的孤立事件。
            // 事件链是 SHA-256 哈希链，RemoveEventsAndRebuildChain 全链重建 Sequence/PrevChainHash/ContentHash。
            // 其他事件（StateTransition/RunFailed/RunCancelled 等）保留，确保终态可持久化。
            _pendingTurnCheckpoint = null;
            RemoveEventsAndRebuildChain(_pendingTurnEvents, AgentRunEventType.CheckpointSaved);
            throw;
        }

        _pendingTurnEvents.Clear();
        _turnStartState = run.State;
        _pendingTurnCheckpoint = null;

        // P1-4: 更新未 checkpoint 事件计数。
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
    /// P0-11：从待提交事件列表中移除指定类型的事件，并重建 SHA-256 哈希链。
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

    /// <summary>将 Run 标记为 Failed 并记录 RunFailed 事件。</summary>
    private async Task FailAsync(AgentRunExecutionState state, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var fromState = state.Run.State;

            // G4：更新本地 Run 副本（含失败原因 + ModelCallsUsed）
            var failedRun = state.Run with
            {
                FailureReason = reason,
                ModelCallsUsed = _modelCallsUsed,
                TurnBudget = _turnBudget,
                CostBudget = _costBudget,
                UpdatedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow
            };
            state = state with { Run = failedRun };

            // G4：推进到 Failed（本地 + 缓冲 StateTransition 事件；允许任意状态跳转 Failed）
            state = TransitionStateLocal(state, AgentRunState.Failed);

            // 缓冲 RunFailed 事件
            state = BufferEvent(state, AgentRunEventType.RunFailed, JsonSerializer.Serialize(new
            {
                reason,
                fromState = fromState.ToString(),
                modelCallsUsed = _modelCallsUsed,
                turn = _currentTurn
            }));

            // G4：终态 flush — 批量提交所有缓冲事件 + state CAS（单事务，立即持久化）
            await FlushPendingEventsAsync(state.Run, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 失败处理中的失败静默忽略，避免掩盖原始异常
        }
    }

    /// <summary>尝试将 Run 标记为 Cancelled（外部取消）。</summary>
    private async Task TryTransitionToCancelledAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        try
        {
            // G4：推进到 Cancelled（本地 + 缓冲 StateTransition 事件；允许任意状态跳转 Cancelled）
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

            // G4：终态 flush — 批量提交所有缓冲事件 + state CAS（单事务，立即持久化）
            await FlushPendingEventsAsync(state.Run, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 取消处理中的失败静默忽略
        }
    }
}
