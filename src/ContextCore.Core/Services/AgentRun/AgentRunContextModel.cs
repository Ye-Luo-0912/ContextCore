using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Core.Services.AgentRunRuntime;

/// <summary>
/// Agent Run 的上下文构建与模型调用协作者。
/// </summary>
/// <remarks>
/// 负责结构化上下文构建（决策运行时 / 自适应检索规划）、模型调用与轮次计数、
/// 延迟归因与自适应反馈记录。持有执行期的轮次/预算可变状态（Run 字段的可变副本），
/// 终端方法（Complete / Fail 等）通过属性读取最终值。
/// </remarks>
internal sealed class AgentRunContextModel
{
    private readonly IAgentModelTransport? _modelTransport;
    private readonly IAgentModelContextProjector? _modelContextProjector;
    private readonly IContextDecisionRuntime? _decisionRuntime;
    private readonly IAdaptiveRetrievalPlanner? _adaptivePlanner;
    private readonly AgentRunEventBuffer _eventBuffer;
    private readonly AgentRunRecovery _recovery;
    private readonly Func<AgentRunExecutionState, string, CancellationToken, Task<AgentRunExecutionState>> _failAsync;
    private readonly Func<AgentRunExecutionState, string, CancellationToken, Task<AgentRunExecutionState>> _enterSafetyBlockedAsync;

    // Tool 定义列表（从 IToolCatalog 构建，用于原生 function calling 声明；
    // 未注入 Catalog 或实现无定义 → 空列表，模型不感知 Tool）。
    private IReadOnlyList<AgentToolDefinition> _toolDefinitions;
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
    // 上一轮投影因预算跳过的材料 ID：下一轮找回问句要覆盖「选了但没投影」的条目。
    private IReadOnlyList<string> _lastProjectionSkippedIds = Array.Empty<string>();
    // 本 Run 使用过的检索计划签名集合（延迟归因用；ExecuteAsync 开始清空）。
    private readonly List<string> _usedRetrievalSignatures = new();
    // 自适应检索规划输入的用途维度（与端点派生签名保持同一取值，保证反馈落到同一签名）。
    private const string AdaptiveAgentContextPurpose = "agent-context";

    /// <summary>
    /// 构造上下文 / 模型协作者。
    /// </summary>
    /// <param name="modelTransport">模型调用传输（null 时降级为仅 Tool 分派）。</param>
    /// <param name="modelContextProjector">模型上下文投影器（null 时回退到 AgentContextState.ProjectForModel）。</param>
    /// <param name="decisionRuntime">Context Decision Runtime（null 时直接构造上下文）。</param>
    /// <param name="adaptivePlanner">自适应检索规划器（null = 未注册，ContextBuilding 不应用自适应层）。</param>
    /// <param name="toolCatalog">Tool 目录（提供模型 function calling 声明；null 时回退到 toolDispatcher 的 IToolCatalog 实现）。</param>
    /// <param name="toolDispatcher">Tool 分派器（仅用于从 IToolCatalog 回退取 Tool 定义）。</param>
    /// <param name="eventBuffer">事件缓冲协作者（本地状态推进 / 事件缓冲 / 批量提交）。</param>
    /// <param name="recovery">恢复协作者（决策依赖不可用时进入恢复失败状态）。</param>
    /// <param name="failAsync">终止接缝：将 Run 标记为 Failed。</param>
    /// <param name="enterSafetyBlockedAsync">终止接缝：进入安全阻断状态。</param>
    public AgentRunContextModel(
        IAgentModelTransport? modelTransport,
        IAgentModelContextProjector? modelContextProjector,
        IContextDecisionRuntime? decisionRuntime,
        IAdaptiveRetrievalPlanner? adaptivePlanner,
        IToolCatalog? toolCatalog,
        IToolDispatcher toolDispatcher,
        AgentRunEventBuffer eventBuffer,
        AgentRunRecovery recovery,
        Func<AgentRunExecutionState, string, CancellationToken, Task<AgentRunExecutionState>> failAsync,
        Func<AgentRunExecutionState, string, CancellationToken, Task<AgentRunExecutionState>> enterSafetyBlockedAsync)
    {
        _modelTransport = modelTransport;
        _modelContextProjector = modelContextProjector;
        _decisionRuntime = decisionRuntime;
        _adaptivePlanner = adaptivePlanner;
        _toolDefinitions = toolCatalog?.GetToolDefinitions()
            ?? (toolDispatcher as IToolCatalog)?.GetToolDefinitions()
            ?? Array.Empty<AgentToolDefinition>();
        _eventBuffer = eventBuffer ?? throw new ArgumentNullException(nameof(eventBuffer));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _failAsync = failAsync ?? throw new ArgumentNullException(nameof(failAsync));
        _enterSafetyBlockedAsync = enterSafetyBlockedAsync ?? throw new ArgumentNullException(nameof(enterSafetyBlockedAsync));
        _modelCallsUsed = 0;
    }

    /// <summary>当前 Turn（模型调用计数与 Run 副本同步更新）。</summary>
    public int CurrentTurn => _currentTurn;

    /// <summary>模型调用次数（累计，崩溃恢复后从 Run 元数据续）。</summary>
    public int ModelCallsUsed => _modelCallsUsed;

    /// <summary>执行期模型轮次计数（Recovery 协作者恢复后回填；Tool 分派读取用于 RequestId）。</summary>
    public int ExecutionModelTurn
    {
        get => _executionModelTurn;
        set => _executionModelTurn = value;
    }

    /// <summary>Turn 预算（可变副本，随模型调用递减）。</summary>
    public AgentTurnBudget? TurnBudget => _turnBudget;

    /// <summary>成本预算（可变副本，随模型调用累积 token 与费用）。</summary>
    public AgentCostBudget? CostBudget => _costBudget;

    /// <summary>
    /// 每次执行开始时的初始化：预算与轮次计数从 Run 恢复、清空检索计划签名与投影跳过记录、
    /// 按 AllowedToolIds 过滤模型可见的 Tool 定义（在模型调用前过滤）。
    /// </summary>
    public void BeginExecution(AgentRun run)
    {
        _turnBudget = run.TurnBudget;
        _costBudget = run.CostBudget;
        _currentTurn = run.Turn;
        // 从 Run 元数据恢复 ModelCallsUsed（支持崩溃恢复后续跑）
        _modelCallsUsed = run.ModelCallsUsed;
        // _executionModelTurn 先重置为 0，Resume 时由 Recovery 协作者
        // 从 ModelCallCompleted 事件流统计重建——避免恢复后从 0 重新计数导致 RequestId 改变。
        _executionModelTurn = 0;
        // 每次执行开始清空检索计划签名集合（Run 隔离；延迟归因只归因本 Run 使用的签名）。
        _usedRetrievalSignatures.Clear();
        // 每次执行开始清空上轮投影跳过记录（Run 隔离；崩溃恢复后没有投影线索）。
        _lastProjectionSkippedIds = Array.Empty<string>();
        // 按 Run.AllowedToolIds 过滤模型可见的 Tool 定义（在模型调用前过滤）
        if (run.AllowedToolIds.Count > 0 && _toolDefinitions.Count > 0)
        {
            _toolDefinitions = _toolDefinitions
                .Where(t => run.AllowedToolIds.Contains(t.Name))
                .ToList();
        }
    }

    /// <summary>执行 CallModel 阶段。</summary>
    public async Task<AgentRunExecutionState> CallModelAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        // 在调用模型前检查 DeadlineAt（超时则 Fail，替代旧路径中 StartRunAsync 返回后立即 Dispose 的 linked CTS）。
        // 旧路径 linked CTS 在 HTTP 请求结束时被 Dispose，导致 Actor 收到 ObjectDisposedException；
        // 新路径由 Run.DeadlineAt 字段承载超时控制，Actor 在每次模型调用前检查。
        if (state.Run.DeadlineAt is not null && DateTimeOffset.UtcNow > state.Run.DeadlineAt)
        {
            return await _failAsync(state,
                $"Run 超时：已超过执行截止时间（DeadlineAt={state.Run.DeadlineAt:O}）。",
                cancellationToken).ConfigureAwait(false);
        }

        // 进入 ModelCalling（本地推进 + 缓冲 StateTransition 事件，CAS 延后到批量提交）
        state = _eventBuffer.TransitionStateLocal(state, AgentRunState.ModelCalling);

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
                state = await _enterSafetyBlockedAsync(state,
                    "安全阻断：mandatory / hard constraint 上下文缺失或不可用，模型不得在缺失 mandatory 上下文时运行。",
                    cancellationToken).ConfigureAwait(false);
                return state;
            case AgentContextBuildStatus.DependencyUnavailable:
                state = await _recovery.EnterRecoveryFailureStateAsync(state, AgentRunState.RecoveryDependencyUnavailable, cancellationToken,
                    "决策依赖不可用：Decision Runtime 执行异常，本轮终止，等待依赖恢复后重试。").ConfigureAwait(false);
                return state;
            case AgentContextBuildStatus.BudgetUnsatisfiable:
                return await _failAsync(state,
                    "预算不可满足：mandatory 上下文经精确 tokenize 后仍超出模型上下文窗口，Run 失败（需人工介入调整预算或任务）。",
                    cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"未知上下文构建状态：{contextBuildStatus}");
        }

        // 上下文构建成功后立即持久化当前 Run：此时已缓冲 RunCreated / StateTransition 事件，
        // 连同带 Resident 的 Run 快照一并提交。模型第一次调用中途取消或崩溃时，
        // store 上已有本轮种子，新 Actor 可直接从 Run 恢复，不必等 Turn 正常结束。
        await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

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

            _eventBuffer.BufferEvent(state, AgentRunEventType.ModelCallCompleted, JsonSerializer.Serialize(new
            {
                mode = "degraded",
                reason = "IAgentModelTransport not injected"
            }));

            // 跳过 Tool 分派 → 直接尝试 Complete
            return _eventBuffer.TransitionStateLocal(state, AgentRunState.ContextBuilding);
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
            // 投影跳过 ≠ 分配器选中：记录因预算跳过的材料，下一轮找回问句要覆盖它们。
            _lastProjectionSkippedIds = projection.SkippedMaterialIds;
        }
        else
        {
            // 回退路径：旧 ProjectForModel（不含 Material 正文；传 0 = 不限制，保持向后兼容）
            projectedMessages = state.Context.ProjectForModel(tokenBudget: 0);
        }

        // 仅在事件 payload 中携带 contextLength（不再传字符串给 Transport）
        var contextLength = AgentMessage.Serialize(projectedMessages).Length;

        // 记录 ModelCallStarted
        state = _eventBuffer.BufferEvent(state, AgentRunEventType.ModelCallStarted, JsonSerializer.Serialize(new
        {
            turn = _currentTurn,
            contextLength
        }));

        // 调用 AgentModelRequest 重载（携带 Tool 定义 + 模型工件 + 截止时间，支持原生 function calling）。
        // 旧路径仅传 messages，模型无法发起 function calling；新路径将 Tool 定义（从 RealToolDispatcher 构建）
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
        // 后续 Assistant 消息 / ModelCallCompleted 事件 / 分派 / 审批 / Journal / Tool Message
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
        state = _eventBuffer.BufferEvent(state, AgentRunEventType.ModelCallCompleted, JsonSerializer.Serialize(new
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
            // 持久化执行期模型轮次，支持崩溃恢复时重建执行期模型轮次，
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
                    // 找回问句：上一轮被分配器裁掉、或投影因预算跳过的条目，用实体词逐条搜（不钉 RequiredIds）。
                    UnresolvedGoals = BuildRecoveryGoals(state.LastDecisionResult, _lastProjectionSkippedIds),
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
                    "[AgentRunContextModel] Adaptive retrieval plan failed for run {0}: {1}", run.RunId, ex.Message);
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
                        "[AgentRunContextModel] Late hydration degraded for run {0}: failedCount={1}, budgetExceeded={2}",
                        run.RunId,
                        failedCountText ?? "0",
                        diagnostics.ContainsKey("hydration.budgetExceeded"));
                }

                // AgentContext fail-closed — 预算修复后 mandatory 独占仍超限
                // （exact tokenize 后实际 token 数 > 模型上下文窗口），决策结果不可用。
                if (diagnostics.ContainsKey("hydration.budgetExceeded"))
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "[AgentRunContextModel] Fail-closed: hydration budget exceeded after exact tokenize for run {0}; mandatory items alone exceed token budget. Decision result discarded.",
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
                            "[AgentRunContextModel] Fail-closed: hard constraint / mandatory candidate {0} has no hydrated content for run {1}; decision result discarded.",
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
                "[AgentRunContextModel] Fail-closed: mandatory context window exceeded for run {0}; decision discarded.",
                run.RunId);
            return (AgentContextBuildStatus.BudgetUnsatisfiable, null);
        }
        catch (MandatoryHydrationFailedException)
        {
            // 水合器 fail-closed：mandatory / hard constraint 候选正文获取失败。
            System.Diagnostics.Trace.TraceWarning(
                "[AgentRunContextModel] Fail-closed: mandatory context hydration failed for run {0}; decision discarded.",
                run.RunId);
            return (AgentContextBuildStatus.SafetyBlocked, null);
        }
        catch (Exception)
        {
            // Decision Runtime 执行异常 → 依赖不可用，终止本轮（可重试），不降级调用模型。
            System.Diagnostics.Trace.TraceWarning(
                "[AgentRunContextModel] Decision Runtime exception for run {0}; run blocked as dependency unavailable.",
                run.RunId);
            return (AgentContextBuildStatus.DependencyUnavailable, null);
        }
    }

    /// <summary>
    /// 从上一轮被分配器裁掉、或投影因预算跳过的条目抽出实体词作为找回问句（逐条 Keyword，不钉 RequiredIds）。
    /// 崩溃恢复后 LastDecisionResult 为 null：本轮没有裁掉线索，只靠 Resident + 任务 + 观察。
    /// </summary>
    private static IReadOnlyList<string> BuildRecoveryGoals(
        ContextDecisionExecutionResult? last,
        IReadOnlyList<string>? projectionSkippedIds)
    {
        var goals = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDistinctive(string? text)
        {
            var distinctive = ObservationQueryText.Distinctive(string.Empty, text);
            if (distinctive.Length == 0 || !seen.Add(distinctive))
            {
                return;
            }
            goals.Add(distinctive);
        }

        if (last is not null)
        {
            foreach (var envelope in last.Decision.DroppedEnvelopes)
            {
                // gate 拦截的候选（superseded/重复/标签不符等）不可恢复：不再重新查询。
                // 只把预算/配额裁掉的条目（通过 gate、被分配器放弃）转成下一轮找回问句。
                if (!envelope.Safety.PassesSafetyGate)
                {
                    continue;
                }
                // Envelope 不带标题，用实体 ID 当找回词；ID 抽不出词时才回退带前缀的 CandidateId。
                var entityId = envelope.CanonicalKey.EntityId;
                if (ObservationQueryText.Distinctive(string.Empty, entityId).Length > 0)
                {
                    AddDistinctive(entityId);
                }
                else
                {
                    AddDistinctive(envelope.CandidateId);
                }
            }
        }

        if (projectionSkippedIds is not null)
        {
            foreach (var skipped in projectionSkippedIds)
            {
                AddDistinctive(skipped);
            }
        }

        return goals;
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
                "[AgentRunContextModel] Adaptive retrieval outcome record failed for run {0}: {1}", run.RunId, ex.Message);
        }
    }

    /// <summary>
    /// 延迟归因：Run 终态时把工具成功率归因到本 Run 用过的检索计划签名。
    /// 没有工具观察则不写。幂等键 (runId, signature)，重试/重放不重复归因。
    /// </summary>
    public async Task RecordDeferredAttributionAsync(
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
                "[AgentRunContextModel] Deferred retrieval attribution failed for run {0}: {1}", state.Run.RunId, ex.Message);
        }
        finally
        {
            _usedRetrievalSignatures.Clear();
        }
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
    public static List<NormalizedToolCall> NormalizeToolCalls(
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
}
