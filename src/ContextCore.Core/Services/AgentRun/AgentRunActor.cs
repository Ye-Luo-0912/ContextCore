using System.Collections.Concurrent;
using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 任务 E6：AgentRunActor — 单个 Agent Run 的执行者（per-run 实例）
//
// 负责单个 Run 的完整生命周期：
//   1. ContextBuilding → 调用 IContextDecisionRuntime 或直接构造上下文
//   2. IAgentLoopPolicy.DecideAsync → 决定下一步
//   3. CallModel → IAgentModelTransport.CallAsync → 记录事件
//   4. DispatchTool → IAgentToolCallValidator.ValidateAsync → IAgentApprovalGate →
//      IToolDispatcher.DispatchAsync → 记录事件
//   5. Observing → 追加 Tool 结果到上下文
//   6. Checkpointing → IAgentCheckpointFactory.CreateAsync → 记录事件
//   7. 循环回到 1，直到 Complete/Failed/Cancelled
//
// 设计决策：
//   - 通过 IAgentRunStore.TransitionStateAsync 推进状态（CAS expected-state）
//   - 通过 IAgentRunEventStore.AppendAsync 写入审计事件（哈希链）
//   - 异常时 TransitionStateAsync → Failed 并记录 RunFailed 事件
//   - IAgentModelTransport / IContextDecisionRuntime 为 null 时优雅降级（兼容现有 Kernel）
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

    // 运行时累积状态
    private string _accumulatedContext;
    private AgentModelResponse? _lastModelResponse;
    private int _currentTurn;
    private int _eventSequence;
    private string? _lastEventContentHash;
    private AgentTurnBudget? _turnBudget;
    private AgentCostBudget? _costBudget;

    /// <summary>
    /// 构造 Agent Run Actor。
    /// </summary>
    /// <param name="runStore">Run 元数据存储。</param>
    /// <param name="eventStore">Run 事件流存储（哈希链）。</param>
    /// <param name="modelTransport">模型调用传输（null 时降级为仅 Tool 分派）。</param>
    /// <param name="loopPolicy">循环策略。</param>
    /// <param name="toolDispatcher">Tool 分派器。</param>
    /// <param name="toolCallValidator">Tool 校验器（null 时跳过校验）。</param>
    /// <param name="approvalGate">审批门（null 时跳过审批）。</param>
    /// <param name="checkpointFactory">检查点工厂（null 时跳过 checkpoint）。</param>
    /// <param name="decisionRuntime">Context Decision Runtime（null 时直接构造上下文）。</param>
    public AgentRunActor(
        IAgentRunStore runStore,
        IAgentRunEventStore eventStore,
        IAgentModelTransport? modelTransport,
        IAgentLoopPolicy loopPolicy,
        IToolDispatcher toolDispatcher,
        IAgentToolCallValidator? toolCallValidator = null,
        IAgentApprovalGate? approvalGate = null,
        IAgentCheckpointFactory? checkpointFactory = null,
        IContextDecisionRuntime? decisionRuntime = null)
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
        _accumulatedContext = string.Empty;
    }

    /// <summary>
    /// 执行 Agent Run 主循环，直到 Complete/Failed/Cancelled 或取消。
    /// </summary>
    /// <param name="run">待执行的 Run 元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ExecuteAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        _turnBudget = run.TurnBudget;
        _costBudget = run.CostBudget;
        _currentTurn = run.Turn;
        _eventSequence = 0;
        _lastEventContentHash = null;

        // 记录 RunCreated 事件（审计起点）
        await AppendEventAsync(run, AgentRunEventType.RunCreated, JsonSerializer.Serialize(new
        {
            runId = run.RunId,
            sessionId = run.SessionId,
            task = run.Task
        }), cancellationToken).ConfigureAwait(false);

        try
        {
            // 启动：Created → ContextBuilding
            await TransitionStateAsync(run, AgentRunState.ContextBuilding, cancellationToken).ConfigureAwait(false);

            // 主循环
            while (!AgentRunStateMachine.IsTerminalState(run.State) && !cancellationToken.IsCancellationRequested)
            {
                // 重新读取最新 Run 状态（TransitionStateAsync 已推进）
                var decision = await _loopPolicy.DecideAsync(run, _lastModelResponse, cancellationToken).ConfigureAwait(false);

                switch (decision)
                {
                    case AgentLoopDecision.CallModel:
                        run = await ExecuteCallModelAsync(run, cancellationToken).ConfigureAwait(false);
                        break;

                    case AgentLoopDecision.DispatchTool:
                        run = await ExecuteDispatchToolAsync(run, cancellationToken).ConfigureAwait(false);
                        break;

                    case AgentLoopDecision.Checkpoint:
                        run = await ExecuteCheckpointAsync(run, cancellationToken).ConfigureAwait(false);
                        break;

                    case AgentLoopDecision.Complete:
                        run = await ExecuteCompleteAsync(run, cancellationToken).ConfigureAwait(false);
                        break;

                    case AgentLoopDecision.Fail:
                        await FailAsync(run, "Loop policy decided to fail.", cancellationToken).ConfigureAwait(false);
                        return;

                    default:
                        await FailAsync(run, $"Unknown loop decision: {decision}", cancellationToken).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外部取消 → 转 Cancelled
            await TryTransitionToCancelledAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 任意异常 → 转 Failed
            await FailAsync(run, ex.ToString(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>执行 CallModel 阶段。</summary>
    private async Task<AgentRun> ExecuteCallModelAsync(AgentRun run, CancellationToken cancellationToken)
    {
        // 进入 ModelCalling
        run = await TransitionStateAsync(run, AgentRunState.ModelCalling, cancellationToken).ConfigureAwait(false);

        // 模型传输未注入 → 降级：直接产出空响应，进入下一轮决策
        if (_modelTransport is null)
        {
            _lastModelResponse = new AgentModelResponse
            {
                Content = string.Empty,
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 0,
                Duration = TimeSpan.Zero
            };

            await AppendEventAsync(run, AgentRunEventType.ModelCallCompleted, JsonSerializer.Serialize(new
            {
                mode = "degraded",
                reason = "IAgentModelTransport not injected"
            }), cancellationToken).ConfigureAwait(false);

            // 跳过 Tool 分派 → 直接尝试 Complete
            return await TransitionStateAsync(run, AgentRunState.ContextBuilding, cancellationToken).ConfigureAwait(false);
        }

        // 记录 ModelCallStarted
        await AppendEventAsync(run, AgentRunEventType.ModelCallStarted, JsonSerializer.Serialize(new
        {
            turn = _currentTurn,
            contextLength = _accumulatedContext.Length
        }), cancellationToken).ConfigureAwait(false);

        // 调用模型
        var response = await _modelTransport.CallAsync(run.RunId, _accumulatedContext, cancellationToken).ConfigureAwait(false);
        _lastModelResponse = response;

        // 累积 token 消耗
        if (_costBudget is not null)
        {
            _costBudget = _costBudget with { TokensUsed = _costBudget.TokensUsed + response.TokensConsumed };
        }

        // 累积模型响应到上下文
        if (!string.IsNullOrEmpty(response.Content))
        {
            _accumulatedContext = string.IsNullOrEmpty(_accumulatedContext)
                ? response.Content
                : _accumulatedContext + "\n---\n" + response.Content;
        }

        // 记录 ModelCallCompleted
        await AppendEventAsync(run, AgentRunEventType.ModelCallCompleted, JsonSerializer.Serialize(new
        {
            isFinalAnswer = response.IsFinalAnswer,
            toolCallCount = response.ToolCalls.Count,
            tokensConsumed = response.TokensConsumed,
            durationMs = response.Duration.TotalMilliseconds
        }), cancellationToken).ConfigureAwait(false);

        return run;
    }

    /// <summary>执行 DispatchTool 阶段（含校验 + 审批 + 分派 + 观察）。</summary>
    private async Task<AgentRun> ExecuteDispatchToolAsync(AgentRun run, CancellationToken cancellationToken)
    {
        if (_lastModelResponse is null || _lastModelResponse.ToolCalls.Count == 0)
        {
            // 无 Tool 调用 → 回到 ContextBuilding
            return await TransitionStateAsync(run, AgentRunState.ContextBuilding, cancellationToken).ConfigureAwait(false);
        }

        // 进入 ToolDispatching
        run = await TransitionStateAsync(run, AgentRunState.ToolDispatching, cancellationToken).ConfigureAwait(false);

        foreach (var toolCall in _lastModelResponse.ToolCalls)
        {
            // 1. 校验
            if (_toolCallValidator is not null)
            {
                var validation = await _toolCallValidator.ValidateAsync(run.RunId, toolCall, cancellationToken).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    await AppendEventAsync(run, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(new
                    {
                        toolName = toolCall.ToolName,
                        succeeded = false,
                        error = validation.Error ?? "Validation failed"
                    }), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // 2. 审批（如需）
                if (validation.RequiresApproval && _approvalGate is not null)
                {
                    run = await TransitionStateAsync(run, AgentRunState.AwaitingApproval, cancellationToken).ConfigureAwait(false);

                    await AppendEventAsync(run, AgentRunEventType.ApprovalRequested, JsonSerializer.Serialize(new
                    {
                        toolName = toolCall.ToolName,
                        reason = validation.ApprovalReason
                    }), cancellationToken).ConfigureAwait(false);

                    var approval = await _approvalGate.RequestApprovalAsync(run.RunId, toolCall, cancellationToken).ConfigureAwait(false);

                    await AppendEventAsync(run, AgentRunEventType.ApprovalResolved, JsonSerializer.Serialize(new
                    {
                        approved = approval.Approved,
                        approverId = approval.ApproverId,
                        rejectionReason = approval.RejectionReason
                    }), cancellationToken).ConfigureAwait(false);

                    if (!approval.Approved)
                    {
                        // 审批拒绝 → 回到 ToolDispatching 状态后跳过此 Tool
                        run = await TransitionStateAsync(run, AgentRunState.ToolDispatching, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // 批准后回到 ToolDispatching
                    run = await TransitionStateAsync(run, AgentRunState.ToolDispatching, cancellationToken).ConfigureAwait(false);
                }
            }

            // 3. 记录 ToolCallStarted
            await AppendEventAsync(run, AgentRunEventType.ToolCallStarted, JsonSerializer.Serialize(new
            {
                toolName = toolCall.ToolName,
                requestId = Guid.NewGuid().ToString("N")
            }), cancellationToken).ConfigureAwait(false);

            // 4. 分派
            var dispatchRequest = new ToolDispatchRequest
            {
                ToolName = toolCall.ToolName,
                Payload = toolCall.Arguments,
                RequestId = Guid.NewGuid().ToString("N")
            };

            var result = await _toolDispatcher.DispatchAsync(dispatchRequest, cancellationToken).ConfigureAwait(false);

            // 5. 记录 ToolCallCompleted
            await AppendEventAsync(run, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(new
            {
                toolName = toolCall.ToolName,
                succeeded = result.Succeeded,
                output = result.Result,
                error = result.Error,
                durationMs = result.Duration.TotalMilliseconds
            }), cancellationToken).ConfigureAwait(false);

            // 6. 观察：追加 Tool 结果到上下文
            var observation = result.Succeeded
                ? $"[Tool:{toolCall.ToolName}] {result.Result}"
                : $"[Tool:{toolCall.ToolName}:ERROR] {result.Error}";

            _accumulatedContext = string.IsNullOrEmpty(_accumulatedContext)
                ? observation
                : _accumulatedContext + "\n---\n" + observation;

            await AppendEventAsync(run, AgentRunEventType.ObservationAppended, JsonSerializer.Serialize(new
            {
                toolName = toolCall.ToolName,
                observationLength = observation.Length
            }), cancellationToken).ConfigureAwait(false);
        }

        // Tool 分派完成 → Observing
        run = await TransitionStateAsync(run, AgentRunState.Observing, cancellationToken).ConfigureAwait(false);

        // 累积 Turn
        _currentTurn++;
        if (_turnBudget is not null)
        {
            _turnBudget = _turnBudget with { TurnsUsed = _turnBudget.TurnsUsed + 1 };
        }

        // 更新 Run 元数据
        run = run with
        {
            Turn = _currentTurn,
            TurnBudget = _turnBudget,
            CostBudget = _costBudget,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _runStore.UpdateAsync(run, cancellationToken).ConfigureAwait(false);

        // Checkpointing（若有工厂）→ ContextBuilding（下一轮）
        if (_checkpointFactory is not null)
        {
            run = await ExecuteCheckpointAsync(run, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 无 checkpoint 工厂 → 直接进入下一轮
            run = await TransitionStateAsync(run, AgentRunState.ContextBuilding, cancellationToken).ConfigureAwait(false);
        }

        return run;
    }

    /// <summary>执行 Checkpoint 阶段。</summary>
    private async Task<AgentRun> ExecuteCheckpointAsync(AgentRun run, CancellationToken cancellationToken)
    {
        // 进入 Checkpointing
        run = await TransitionStateAsync(run, AgentRunState.Checkpointing, cancellationToken).ConfigureAwait(false);

        if (_checkpointFactory is not null)
        {
            var checkpointId = $"run-{run.RunId}-turn-{_currentTurn}-{Guid.NewGuid():N}";
            var checkpoint = await _checkpointFactory.CreateCheckpointAsync(
                checkpointId, run.SessionId, run.WorkspaceId, cancellationToken).ConfigureAwait(false);

            await AppendEventAsync(run, AgentRunEventType.CheckpointSaved, JsonSerializer.Serialize(new
            {
                checkpointId = checkpoint.CheckpointId,
                stateJsonLength = checkpoint.StateJson?.Length ?? 0
            }), cancellationToken).ConfigureAwait(false);
        }

        // Checkpointing → ContextBuilding（循环继续）
        run = await TransitionStateAsync(run, AgentRunState.ContextBuilding, cancellationToken).ConfigureAwait(false);

        return run;
    }

    /// <summary>执行 Complete 阶段。</summary>
    private async Task<AgentRun> ExecuteCompleteAsync(AgentRun run, CancellationToken cancellationToken)
    {
        var finalAnswer = _lastModelResponse?.Content ?? string.Empty;

        // 更新 Run 元数据（含最终答案）
        run = run with
        {
            FinalAnswer = finalAnswer,
            Turn = _currentTurn,
            TurnBudget = _turnBudget,
            CostBudget = _costBudget,
            UpdatedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow
        };
        await _runStore.UpdateAsync(run, cancellationToken).ConfigureAwait(false);

        // 推进到 Completed
        run = await TransitionStateAsync(run, AgentRunState.Completed, cancellationToken).ConfigureAwait(false);

        // 记录 RunCompleted
        await AppendEventAsync(run, AgentRunEventType.RunCompleted, JsonSerializer.Serialize(new
        {
            finalAnswerLength = finalAnswer.Length,
            turn = _currentTurn
        }), cancellationToken).ConfigureAwait(false);

        return run;
    }

    /// <summary>推进 Run 状态（CAS），同时校验状态机合法性。</summary>
    private async Task<AgentRun> TransitionStateAsync(
        AgentRun run,
        AgentRunState newState,
        CancellationToken cancellationToken)
    {
        // 校验状态机
        AgentRunStateMachine.ValidateTransition(run.State, newState);

        // 通过 Store CAS 推进（expected-state）
        await _runStore.TransitionStateAsync(
            run.WorkspaceId, run.RunId, run.State, newState, cancellationToken).ConfigureAwait(false);

        // 更新本地 Run 副本
        var updatedRun = run with
        {
            State = newState,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // 记录 StateTransition 事件
        await AppendEventAsync(updatedRun, AgentRunEventType.StateTransition, JsonSerializer.Serialize(new
        {
            from = run.State.ToString(),
            to = newState.ToString()
        }), cancellationToken).ConfigureAwait(false);

        return updatedRun;
    }

    /// <summary>追加审计事件（哈希链）。</summary>
    private async Task AppendEventAsync(
        AgentRun run,
        AgentRunEventType type,
        string payload,
        CancellationToken cancellationToken)
    {
        var @event = AgentRunEventChain.BuildEvent(
            run.RunId,
            run.WorkspaceId,
            _eventSequence,
            type,
            run.State,
            payload,
            _lastEventContentHash);

        await _eventStore.AppendAsync(@event, cancellationToken).ConfigureAwait(false);

        _lastEventContentHash = @event.ContentHash;
        _eventSequence++;
    }

    /// <summary>将 Run 标记为 Failed 并记录 RunFailed 事件。</summary>
    private async Task FailAsync(AgentRun run, string reason, CancellationToken cancellationToken)
    {
        try
        {
            // 更新 Run 元数据（含失败原因）
            var failedRun = run with
            {
                FailureReason = reason,
                UpdatedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow
            };
            await _runStore.UpdateAsync(failedRun, cancellationToken).ConfigureAwait(false);

            // CAS 推进到 Failed（允许任意状态跳转 Failed）
            await _runStore.TransitionStateAsync(
                run.WorkspaceId, run.RunId, run.State, AgentRunState.Failed, cancellationToken).ConfigureAwait(false);

            failedRun = failedRun with { State = AgentRunState.Failed };

            // 记录 RunFailed 事件
            await AppendEventAsync(failedRun, AgentRunEventType.RunFailed, JsonSerializer.Serialize(new
            {
                reason,
                fromState = run.State.ToString()
            }), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 失败处理中的失败静默忽略，避免掩盖原始异常
        }
    }

    /// <summary>尝试将 Run 标记为 Cancelled（外部取消）。</summary>
    private async Task TryTransitionToCancelledAsync(AgentRun run, CancellationToken cancellationToken)
    {
        try
        {
            await _runStore.TransitionStateAsync(
                run.WorkspaceId, run.RunId, run.State, AgentRunState.Cancelled, CancellationToken.None).ConfigureAwait(false);

            var cancelledRun = run with
            {
                State = AgentRunState.Cancelled,
                UpdatedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow
            };
            await _runStore.UpdateAsync(cancelledRun, CancellationToken.None).ConfigureAwait(false);

            await AppendEventAsync(cancelledRun, AgentRunEventType.RunCancelled, JsonSerializer.Serialize(new
            {
                fromState = run.State.ToString()
            }), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 取消处理中的失败静默忽略
        }
    }
}
