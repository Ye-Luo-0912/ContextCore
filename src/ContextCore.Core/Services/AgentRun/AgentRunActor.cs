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
    private readonly IAgentRunEventStore _eventStore;
    private readonly IAgentLoopPolicy _loopPolicy;
    private readonly IAgentCheckpointFactory? _checkpointFactory;
    // Tool 对账记录存储（null 时禁用"未裁决不完成"约束，仅 journal 自身保证模糊态不被重放）
    private readonly IToolReconciliationStore? _reconciliationStore;
    // Recovery Integrity State：人工介入告警接收器（null = 不告警，best-effort 钩子）。
    private readonly IRecoveryAlertSink? _alertSink;
    // Turn 内事件缓冲与批量提交协作者（持有缓冲状态，Turn 结束时单事务提交）
    private readonly AgentRunEventBuffer _eventBuffer;
    // 崩溃恢复 / resume 协作者（从事件流 / checkpoint / 可恢复快照重建执行状态）
    private readonly AgentRunRecovery _recovery;
    // 上下文构建与模型调用协作者（持有轮次/预算可变状态、检索计划签名与 Tool 定义）
    private readonly AgentRunContextModel _modelContext;
    // Tool 校验 / 审批 / 分派 / 对账协作者（持有 lease token 与租约过期提供器）
    private readonly AgentRunToolDispatch _toolDispatch;

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
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _loopPolicy = loopPolicy ?? throw new ArgumentNullException(nameof(loopPolicy));
        _checkpointFactory = checkpointFactory;
        _reconciliationStore = reconciliationStore;
        _alertSink = alertSink;
        _eventBuffer = new AgentRunEventBuffer(eventStore, committer, checkpointFactory, checkpointStore);
        _recovery = new AgentRunRecovery(runStore, eventStore, checkpointStore, eventCompactor, hostOptions, alertSink, _eventBuffer);
        _modelContext = new AgentRunContextModel(
            modelTransport, modelContextProjector, decisionRuntime, adaptivePlanner, toolCatalog, toolDispatcher,
            _eventBuffer, _recovery, FailAsync, EnterSafetyBlockedStateAsync);
        _toolDispatch = new AgentRunToolDispatch(
            toolDispatcher, toolCallValidator, approvalGate, durableToolExecutor, reconciliationStore,
            toolAuthorizationPolicy, checkpointFactory, _eventBuffer, _modelContext, FailAsync);
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

        // 保存 lease token 与 fencing token，供 Tool 分派协作者构造副作用 fence 时校验。
        // 两者必须同时为 null 或同时非 null（接口契约由调用方 AgentKernelHost 保证）。
        _toolDispatch.BeginExecution(leaseToken, fencingToken, leaseExpiresAtProvider);

        // 初始化模型上下文协作者的执行期状态（轮次/预算计数、检索计划签名、投影跳过记录、Tool 定义过滤）
        _modelContext.BeginExecution(run);

        // 运行时能力补齐：检测 resume 场景
        // 全新启动状态集由 AgentRunStateSemantics 权威定义（RecoveryPolicy = NewStart：
        // Created / Queued / Claimed / Running / PendingAdmission / ClaimExpired / ScheduledLocally）——
        // 这些状态代表 Run 尚未产生任何持久化事件（首次 flush 才原子
        // CAS 到 ContextBuilding 并落库 RunCreated），必须走全新启动路径。
        // 其余非终态（ContextBuilding/ModelCalling/...）为崩溃恢复场景（resume）。
        var isResume = AgentRunStateSemantics.Get(run.State).RecoveryPolicy != AgentRunRecoveryPolicy.NewStart;

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

        // 事件缓冲初始化：记录 Turn 起始状态（resume 时 = store 中的当前状态，作为 CAS expected state）、
        // 清空缓冲并注入 lease token（供批量提交时校验 lease 仍由当前实例持有）
        _eventBuffer.BeginExecution(run.State, leaseToken, fencingToken);

        if (isResume)
        {
            // Resume：从事件流重建上下文（Recovery 协作者返回重建状态与模型轮次计数）
            var (rebuiltState, executionModelTurn) = await _recovery.RebuildStateFromEventsAsync(
                state, leaseToken, fencingToken, cancellationToken).ConfigureAwait(false);
            state = rebuiltState;
            _modelContext.ExecutionModelTurn = executionModelTurn;
        }
        else
        {
            // 全新启动：记录 RunCreated 事件（审计起点）— 缓冲到事件缓冲，待 Turn 结束批量提交
            if (run.RetryCount > 0)
            {
                // 不可变 Attempt：重试尝试在既有事件链上续写（不删除前序 Attempt 历史）。
                // 先写入 RunRetryScheduled（Attempt 边界锚点）+ AttemptStarted 标记，
                // 再续 RunCreated——恢复重放以最后一个 RunRetryScheduled 为界只重放当前 Attempt。
                state = await BeginRetryAttemptAsync(state, cancellationToken).ConfigureAwait(false);
            }
            state = _eventBuffer.BufferEvent(state, AgentRunEventType.RunCreated, JsonSerializer.Serialize(new
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
                state = _eventBuffer.TransitionStateLocal(state, AgentRunState.ContextBuilding);
            }
            // Resume 场景：Recovery 协作者已将本地状态规范化为 ContextBuilding，
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
                    state = await _toolDispatch.ExecutePendingToolAsync(state, cancellationToken).ConfigureAwait(false);
                    // mid-turn 缓冲超过阈值时强制 flush
                    if (_eventBuffer.PendingEventCount >= AgentRunEventBuffer.PendingEventsFlushThreshold)
                    {
                        await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                // 重新读取最新 Run 状态（TransitionStateLocal 已推进本地副本）
                var decision = await _loopPolicy.DecideAsync(state.Run, state.LastModelResponse, cancellationToken).ConfigureAwait(false);

                switch (decision)
                {
                    case AgentLoopDecision.CallModel:
                        state = await _modelContext.CallModelAsync(state, cancellationToken).ConfigureAwait(false);
                        // mid-turn 缓冲超过阈值时强制 flush，避免长 Turn 内存膨胀
                        if (_eventBuffer.PendingEventCount >= AgentRunEventBuffer.PendingEventsFlushThreshold)
                        {
                            await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
                            // 强制 checkpoint 阈值检查 — 未 checkpoint 事件达阈值时记录警告。
                            // ModelCalling 状态无法直接进入 Checkpointing（状态机仅允许 Observing → Checkpointing），
                            // checkpoint 将在下一个 Observing 状态（DispatchToolsAsync Turn 结束）时创建并重置计数。
                            if (_eventBuffer.EventsSinceLastCheckpoint >= AgentRunEventBuffer.ForcedCheckpointEventThreshold && _checkpointFactory is not null)
                            {
                                System.Diagnostics.Trace.TraceWarning(
                                    "[AgentRunActor] 未 checkpoint 事件数 ({0}) 达到强制阈值 ({1})，run={2}，状态={3}。" +
                                    "将在下一个 Observing 状态创建 checkpoint。",
                                    _eventBuffer.EventsSinceLastCheckpoint, AgentRunEventBuffer.ForcedCheckpointEventThreshold, state.Run.RunId, state.Run.State);
                            }
                        }
                        break;

                    case AgentLoopDecision.DispatchTool:
                        state = await _toolDispatch.DispatchToolsAsync(state, cancellationToken).ConfigureAwait(false);
                        // 若审批挂起（Tool 分派协作者已 flush 并返回 AwaitingApproval 状态），
                        // 退出执行槽（释放 Worker/Semaphore/Lease）。Run 已持久化为 AwaitingApproval；
                        // 外部审批决策通过 POST /approvals/{approvalId} 端点提交，
                        // RecoveryWorker 会重新入队执行。
                        if (state.Run.State == AgentRunState.AwaitingApproval)
                        {
                            return;
                        }
                        break;

                    case AgentLoopDecision.Checkpoint:
                        state = await _eventBuffer.PersistCheckpointAsync(state, _modelContext.CurrentTurn, _modelContext.ExecutionModelTurn, cancellationToken).ConfigureAwait(false);
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
            // 需从事件缓冲重新同步 EventSequence / EventChainHash，否则
            // FailAsync/TryTransitionToCancelledAsync 会用陈旧的序列号生成重复 Sequence 事件，
            // 导致 AppendBatchAsync 校验失败（被 catch 吞掉 → 事件丢失 + Run 状态不推进）。
            state = _eventBuffer.ResyncStateFromPendingEvents(state);
            await TryTransitionToCancelledAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 任意异常 → 转 Failed
            // 同上：阶段方法抛异常时 state 赋值未完成，需从事件缓冲重新同步。
            state = _eventBuffer.ResyncStateFromPendingEvents(state);
            state = await FailAsync(state, ex.ToString(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 延迟归因：Run 到达终态时，把工具观察得到的质量信号归因到本 Run
            // 用过的检索计划签名。没有工具观察则不归因，避免用打分器分数冒充准不准。
            await _modelContext.RecordDeferredAttributionAsync(state, cancellationToken).ConfigureAwait(false);
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
    /// RunRetryScheduled 同时是恢复重放的 Attempt 边界锚点（见 Recovery 协作者）。
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
        state = _eventBuffer.BufferEvent(state, AgentRunEventType.RunRetryScheduled, JsonSerializer.Serialize(new
        {
            attempt,
            retryCount = state.Run.RetryCount,
            scheduledAt = DateTimeOffset.UtcNow
        }));
        state = _eventBuffer.BufferEvent(state, AgentRunEventType.AttemptStarted, JsonSerializer.Serialize(new
        {
            attempt,
            retryCount = state.Run.RetryCount
        }));
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
                state = _eventBuffer.TransitionStateLocal(state, AgentRunState.AwaitingReconciliation);
                await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
                return state;
            }
        }

        var finalAnswer = state.LastModelResponse?.Content ?? string.Empty;

        // 更新本地 Run 副本（含最终答案 + ModelCallsUsed）
        var updatedRun = state.Run with
        {
            FinalAnswer = finalAnswer,
            Turn = _modelContext.CurrentTurn,
            ModelCallsUsed = _modelContext.ModelCallsUsed,
            TurnBudget = _modelContext.TurnBudget,
            CostBudget = _modelContext.CostBudget,
            UpdatedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow
        };
        state = state with { Run = updatedRun };

        // 推进到 Completed（本地 + 缓冲 StateTransition 事件）
        state = _eventBuffer.TransitionStateLocal(state, AgentRunState.Completed);

        // 缓冲 RunCompleted 事件
        state = _eventBuffer.BufferEvent(state, AgentRunEventType.RunCompleted, JsonSerializer.Serialize(new
        {
            finalAnswerLength = finalAnswer.Length,
            turn = _modelContext.CurrentTurn,
            modelCallsUsed = _modelContext.ModelCallsUsed,
            tokensUsed = _modelContext.CostBudget?.TokensUsed ?? 0,
            costUsedUsd = _modelContext.CostBudget?.CostUsedUsd ?? 0
        }));

        // 终态 flush — 批量提交所有缓冲事件 + state CAS（单事务，立即持久化）
        await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

        return state;
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
                    state = _eventBuffer.TransitionStateLocal(state, AgentRunState.AwaitingReconciliation);
                    await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
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
                ModelCallsUsed = _modelContext.ModelCallsUsed,
                TurnBudget = _modelContext.TurnBudget,
                CostBudget = _modelContext.CostBudget,
                UpdatedAt = DateTimeOffset.UtcNow,
                FinishedAt = retryAvailable ? null : DateTimeOffset.UtcNow
            };
            state = state with { Run = failedRun };

            // 推进到 RetryPending（重试可用）/ Failed（耗尽；本地 + 缓冲 StateTransition 事件；
            // 任意非终态可跳转；Scheduler 在退避门通过后领取 RetryPending）。
            state = _eventBuffer.TransitionStateLocal(state, targetState);

            // 缓冲 RunFailed 事件（Attempt 失败审计，含目标状态区分）
            state = _eventBuffer.BufferEvent(state, AgentRunEventType.RunFailed, JsonSerializer.Serialize(new
            {
                reason,
                fromState = fromState.ToString(),
                modelCallsUsed = _modelContext.ModelCallsUsed,
                turn = _modelContext.CurrentTurn,
                terminal = !retryAvailable
            }));

            // 不可变 Attempt：重试 Attempt 失败追加 AttemptFailed 审计标记
            // （前序 Attempt 历史保留；Attempt 边界由 RunRetryScheduled 锚定）。
            if (state.Run.RetryCount > 0)
            {
                state = _eventBuffer.BufferEvent(state, AgentRunEventType.AttemptFailed, JsonSerializer.Serialize(new
                {
                    attempt = state.Run.RetryCount + 1,
                    reason
                }));
            }

            // 终态 flush — 批量提交所有缓冲事件 + state CAS（单事务，立即持久化）
            await _eventBuffer.FlushPendingEventsAsync(state.Run, CancellationToken.None).ConfigureAwait(false);
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
                ModelCallsUsed = _modelContext.ModelCallsUsed,
                TurnBudget = _modelContext.TurnBudget,
                CostBudget = _modelContext.CostBudget,
                UpdatedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow
            };
            state = state with { Run = blockedRun };

            // 推进到 ContextSafetyBlocked（本地 + 缓冲 StateTransition 事件；终态）
            state = _eventBuffer.TransitionStateLocal(state, AgentRunState.ContextSafetyBlocked);

            // 缓冲 RunFailed 事件（安全阻断语义），终态 flush 单事务落库
            state = _eventBuffer.BufferEvent(state, AgentRunEventType.RunFailed, JsonSerializer.Serialize(new
            {
                reason,
                fromState = fromState.ToString(),
                modelCallsUsed = _modelContext.ModelCallsUsed,
                turn = _modelContext.CurrentTurn
            }));
            await _eventBuffer.FlushPendingEventsAsync(state.Run, CancellationToken.None).ConfigureAwait(false);

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
            state = _eventBuffer.TransitionStateLocal(state, AgentRunState.Cancelled);
            var cancelledRun = state.Run with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow
            };
            state = state with { Run = cancelledRun };

            // 缓冲 RunCancelled 事件
            state = _eventBuffer.BufferEvent(state, AgentRunEventType.RunCancelled, JsonSerializer.Serialize(new
            {
                fromState = state.Run.State.ToString()
            }));

            // 终态 flush — 批量提交所有缓冲事件 + state CAS（单事务，立即持久化）
            await _eventBuffer.FlushPendingEventsAsync(state.Run, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 取消处理中的失败静默忽略
        }
    }
}
