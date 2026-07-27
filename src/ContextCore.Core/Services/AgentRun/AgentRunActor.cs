using System.Collections.Concurrent;
using System.Text.Json;
using ContextCore.Abstractions;

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
    private readonly IAgentCheckpointFactory? _checkpointFactory;
    private readonly IContextDecisionRuntime? _decisionRuntime;
    // 子问题 4：Checkpoint Store（保存 checkpoint 持久化）
    private readonly IAgentCheckpointStore? _checkpointStore;
    // 子问题 5：Durable Tool Executor（封装 journal + dispatch）
    private readonly IDurableToolExecutor? _durableToolExecutor;

    // 运行时累积状态（预算与计数，不在 AgentRunExecutionState 中，因为它们是 Run 的字段的可变副本）
    private int _currentTurn;
    // 子问题 2：模型调用次数计数（防止无限循环）
    private int _modelCallsUsed;
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

        /// <summary>最近一次 checkpoint（null = 尚未创建 checkpoint）。</summary>
        public AgentCheckpoint? LastCheckpoint { get; init; }

        /// <summary>事件序列号（单调递增，从 0 开始）。</summary>
        public int EventSequence { get; init; }

        /// <summary>最近一个事件的 ContentHash（哈希链；链头为 null）。</summary>
        public string? EventChainHash { get; init; }
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
    public AgentRunActor(
        IAgentRunStore runStore,
        IAgentRunEventStore eventStore,
        IAgentModelTransport? modelTransport,
        IAgentLoopPolicy loopPolicy,
        IToolDispatcher toolDispatcher,
        IAgentToolCallValidator? toolCallValidator = null,
        IAgentApprovalGate? approvalGate = null,
        IAgentCheckpointFactory? checkpointFactory = null,
        IContextDecisionRuntime? decisionRuntime = null,
        IAgentCheckpointStore? checkpointStore = null,
        IDurableToolExecutor? durableToolExecutor = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _modelTransport = modelTransport;
        _loopPolicy = loopPolicy ?? throw new ArgumentNullException(nameof(loopPolicy));
        _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        _toolCallValidator = toolCallValidator;
        _approvalGate = approvalGate;
        _checkpointFactory = checkpointFactory;
        _decisionRuntime = decisionRuntime;
        _checkpointStore = checkpointStore;
        _durableToolExecutor = durableToolExecutor;
        _modelCallsUsed = 0;
        _turnStartState = AgentRunState.Created;
    }

    /// <summary>
    /// 执行 Agent Run 主循环，直到 Complete/Failed/Cancelled 或取消。
    /// </summary>
    /// <param name="run">待执行的 Run 元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 运行时能力补齐 — Resume from checkpoint：
    ///   当 <paramref name="run"/>.State != Created 时判定为崩溃恢复场景。
    ///   Actor 从事件流重建上下文（ToolObservations / EventSequence / EventChainHash），
    ///   并将本地状态规范化为 ContextBuilding（让 LoopPolicy 决定下一步：通常为 CallModel）。
    ///   LastModelResponse 在 resume 时置为 null（事件流中不含完整模型响应内容），
    ///   强制重新调用模型以避免基于残缺状态做决策。durable journal 保证已分派 Tool 不会被重复执行。
    /// </remarks>
    public async Task ExecuteAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // 运行时能力补齐：检测 resume 场景
        // run.State != Created 表示 Run 之前已开始执行（崩溃/重启后由 RecoveryWorker 重新入队）
        var isResume = run.State != AgentRunState.Created;

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
        // G4：记录 Turn 起始状态，用于批量提交时的 state CAS
        // 运行时能力补齐：resume 时 _turnStartState = run.State（store 中的当前状态），
        // 后续 FlushPendingEventsAsync 的 CAS 以此为 expected state
        _turnStartState = run.State;
        _pendingTurnEvents.Clear();
        _pendingTurnCheckpoint = null;

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
                        }
                        break;

                    case AgentLoopDecision.DispatchTool:
                        state = await DispatchToolsAsync(state, cancellationToken).ConfigureAwait(false);
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
        IReadOnlyList<AgentRunEvent> events;
        try
        {
            events = await _eventStore.ReadAsync(
                state.Run.WorkspaceId, state.Run.RunId,
                fromSequence: 0, take: 10000, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // 事件流读取失败（store 不可用 / 跨 workspace 不可见）→ 回退为全新启动路径
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

        if (events.Count == 0)
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

        // 从事件流重建 ToolObservations
        var toolObservations = new List<ToolObservation>();
        foreach (var evt in events)
        {
            if (evt.EventType != AgentRunEventType.ToolCallCompleted)
            {
                continue;
            }

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

                toolObservations.Add(new ToolObservation
                {
                    ToolName = toolName,
                    ToolCallId = toolCallId,
                    Result = output,
                    Error = error,
                    Succeeded = succeeded
                });
            }
            catch
            {
                // 解析单个事件失败 → 跳过（不影响整体恢复）
            }
        }

        // 从最后一个事件恢复 EventSequence / EventChainHash（保证哈希链连续）
        var lastEvent = events[events.Count - 1];

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
                StableMemoryReferences = new List<MemoryReference>(),
                LastModelTurn = null
            },
            LastModelResponse = null,
            EventSequence = lastEvent.Sequence + 1,
            EventChainHash = lastEvent.ContentHash
        };
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
            state = state with
            {
                LastModelResponse = degradedResponse,
                Context = state.Context with { LastModelTurn = degradedResponse }
            };

            BufferEvent(state, AgentRunEventType.ModelCallCompleted, JsonSerializer.Serialize(new
            {
                mode = "degraded",
                reason = "IAgentModelTransport not injected"
            }));

            // 跳过 Tool 分派 → 直接尝试 Complete
            return TransitionStateLocal(state, AgentRunState.ContextBuilding);
        }

        // G5：由 ContextCore 投影最终模型输入（根据 TokenBudget；当前传 0 = 不限制，保持与旧路径等价）
        // ProjectForModel 将 CurrentTask 投影为 User 消息、ToolObservations 投影为 Tool 消息，
        // 替代旧路径直接传 state.Messages 的扁平方式
        var projectedMessages = state.Context.ProjectForModel(tokenBudget: 0);

        // G1：仅在事件 payload 中携带 contextLength（不再传字符串给 Transport）
        var contextLength = AgentMessage.Serialize(projectedMessages).Length;

        // 记录 ModelCallStarted
        state = BufferEvent(state, AgentRunEventType.ModelCallStarted, JsonSerializer.Serialize(new
        {
            turn = _currentTurn,
            contextLength
        }));

        // G1：调用结构化 CallAsync 重载（Transport 直接消费 AgentMessage[]，无需字符串拼接）
        var response = await _modelTransport.CallAsync(state.Run.RunId, projectedMessages, cancellationToken).ConfigureAwait(false);
        // G5：同步更新 Context.LastModelTurn 和 LastModelResponse
        state = state with
        {
            LastModelResponse = response,
            Context = state.Context with { LastModelTurn = response }
        };

        // 子问题 2：递增模型调用计数
        _modelCallsUsed++;

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

        // G1：累积模型响应到 Context.Messages（仅追加引用，不复制既有字符串）
        if (!string.IsNullOrEmpty(response.Content))
        {
            state.Context.Messages.Add(new AgentMessage
            {
                Role = AgentMessageRole.Assistant,
                Content = response.Content,
                EventId = null // 关联事件 ID 在 ModelCallCompleted 事件产出后回填（此处保留 null 即可）
            });
        }

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
            durationMs = response.Duration.TotalMilliseconds
        }));

        return state;
    }

    /// <summary>
    /// G1/G5：构建结构化上下文。
    /// G5：User(run.Task) 不再追加到 Messages，而是由 AgentContextState.CurrentTask 持有，
    ///     ProjectForModel 投影时合成 User 消息（消除 hasUserMessage 去重检查）。
    /// 若 IContextDecisionRuntime 注入，每次调用追加 System(retrievedContext) 到 Messages。
    /// </summary>
    /// <param name="state">当前执行状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<AgentRunExecutionState> BuildContextAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        // G5：CurrentTask 已在 ExecuteAsync 初始化时设置为 run.Task，
        // ProjectForModel 会将其投影为 User 消息，无需在此追加。
        // 旧路径的 hasUserMessage 去重检查随之移除（CurrentTask 是单值字段，天然不会重复）。

        // P0-2 Bug 2 修复：若 IContextDecisionRuntime 注入，调用它构建检索上下文
        if (_decisionRuntime is not null)
        {
            var decisionContext = await TryBuildDecisionContextAsync(state.Run, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(decisionContext))
            {
                state.Context.Messages.Add(new AgentMessage
                {
                    Role = AgentMessageRole.System,
                    Content = $"[RetrievedContext]\n{decisionContext}"
                });
            }
        }

        return state;
    }

    /// <summary>
    /// 子问题 1：调用 IContextDecisionRuntime 构建检索上下文。
    /// 失败时返回 null（降级为仅 Task + History）。
    /// </summary>
    private async Task<string?> TryBuildDecisionContextAsync(AgentRun run, CancellationToken cancellationToken)
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
                AgentInput = new AgentInput
                {
                    Session = agentSession,
                    RequiredIds = Array.Empty<string>()
                }
            };

            var execution = await _decisionRuntime.ExecuteWithWorkingSetAsync(request, cancellationToken).ConfigureAwait(false);

            // 序列化选中候选为可读上下文（CandidateId + Type + 评分摘要）
            var selected = execution.Decision.SelectedEnvelopes;
            if (selected.Count == 0)
            {
                return null;
            }

            var lines = new List<string>(selected.Count);
            foreach (var env in selected)
            {
                var score = env.Utility?.FinalScore ?? 0;
                lines.Add($"- [{env.Type}] {env.CandidateId} (score={score:F3})");
            }
            return string.Join("\n", lines);
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

        foreach (var toolCall in state.LastModelResponse.ToolCalls)
        {
            // P0-2 Bug 5 修复：在循环开始时生成 toolCallId，同时用于 ToolCallStarted 和 ToolCallCompleted
            // 确保 ToolCallStarted 事件和 ToolCallCompleted 事件的审计 ID 一致
            var toolCallId = Guid.NewGuid().ToString("N");

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

                    state = BufferEvent(state, AgentRunEventType.ApprovalRequested, JsonSerializer.Serialize(new
                    {
                        toolName = toolCall.ToolName,
                        reason = validation.ApprovalReason
                    }));

                    var approval = await _approvalGate.RequestApprovalAsync(state.Run.RunId, toolCall, cancellationToken).ConfigureAwait(false);

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

            // 子问题 5：通过 IDurableToolExecutor 执行（若注入），否则回退到直接 IToolDispatcher
            ToolExecutionResult? toolResult = null;
            if (_durableToolExecutor is not null)
            {
                toolResult = await _durableToolExecutor.ExecuteAsync(
                    state.Run.RunId, state.Run.WorkspaceId, toolCall, cancellationToken).ConfigureAwait(false);
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

            // 3. 记录 ToolCallStarted（P0-2 Bug 5 修复：使用预生成的 toolCallId）
            state = BufferEvent(state, AgentRunEventType.ToolCallStarted, JsonSerializer.Serialize(new
            {
                toolName = toolCall.ToolName,
                toolCallId = toolCallId,
                requestId = toolResult.RequestId,
                idempotencyKey = toolResult.IdempotencyKey
            }));

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

            state.Context.ToolObservations.Add(new ToolObservation
            {
                ToolName = toolCall.ToolName,
                ToolCallId = toolCallId,
                Result = toolResult.Result,
                Error = toolResult.Error,
                Succeeded = toolResult.Succeeded
            });

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

        // Checkpointing（若有工厂）→ ContextBuilding（下一轮）
        if (_checkpointFactory is not null)
        {
            state = await PersistCheckpointAsync(state, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 无 checkpoint 工厂 → 直接进入下一轮
            state = TransitionStateLocal(state, AgentRunState.ContextBuilding);
        }

        // G4：Turn 结束 → 批量提交所有缓冲事件 + state CAS + checkpoint cursor（单事务）
        await FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

        return state;
    }

    /// <summary>
    /// 子问题 6：构建 ToolCallCompleted 事件 payload（含完整 Tool 身份信息）。
    /// payload 结构与 DefaultAgentKernel.ToolCallCompletedPayload 对齐（JSON 字段名兼容），
    /// 让 ResumeFromCursorCheckpointAsync 可从 payload 反序列化真实 RequestId/SideEffect/IdempotencyKey。
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

            // P0-2 Bug 4 修复：先 SaveAsync 持久化 checkpoint（若有 Store 注入）
            // SaveAsync 失败时不记录 CheckpointSaved 事件，转为 Failed 状态
            if (_checkpointStore is not null)
            {
                try
                {
                    await _checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // SaveAsync 失败 → 不记录 CheckpointSaved 事件，转为 Failed 状态
                    await FailAsync(state, $"Checkpoint SaveAsync 失败：{ex.Message}", CancellationToken.None).ConfigureAwait(false);
                    // 返回 Failed 状态（FailAsync 已 flush，主循环将检测到终态并退出）
                    return state with { Run = state.Run with { State = AgentRunState.Failed } };
                }
            }

            // P0-2 重构：更新 state.LastCheckpoint
            state = state with { LastCheckpoint = checkpoint };

            // G4：记录 Turn 内最新 checkpoint，用于批量提交时的 checkpoint cursor
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

        var runStateUpdate = new AgentRunStateUpdate
        {
            WorkspaceId = run.WorkspaceId,
            RunId = run.RunId,
            ExpectedCurrentState = _turnStartState,
            NewState = run.State,
            RunSnapshot = run
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

        await _eventStore.AppendBatchAsync(
            _pendingTurnEvents, runStateUpdate, checkpointCursor, cancellationToken).ConfigureAwait(false);

        _pendingTurnEvents.Clear();
        _turnStartState = run.State;
        _pendingTurnCheckpoint = null;
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
