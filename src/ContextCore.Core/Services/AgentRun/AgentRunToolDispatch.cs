using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;

namespace ContextCore.Core.Services.AgentRunRuntime;

/// <summary>
/// Agent Run 的 Tool 校验 / 审批 / 分派 / 对账协作者。
/// </summary>
/// <remarks>
/// 负责 Tool 授权快照校验、Schema 校验、人工审批、durable 执行（或回退直接分派）、
/// 观察记录与模糊态对账。持有当前执行期的 lease token 与租约过期提供器
/// （由 Actor 在 ExecuteAsync 开始时注入，用于 Tool 副作用 fence）。
/// </remarks>
internal sealed class AgentRunToolDispatch
{
    private readonly IToolDispatcher _toolDispatcher;
    private readonly IAgentToolCallValidator? _toolCallValidator;
    private readonly IAgentApprovalGate? _approvalGate;
    private readonly IDurableToolExecutor? _durableToolExecutor;
    private readonly IToolReconciliationStore? _reconciliationStore;
    private readonly IToolAuthorizationPolicy? _toolAuthorizationPolicy;
    private readonly IAgentCheckpointFactory? _checkpointFactory;
    private readonly AgentRunEventBuffer _eventBuffer;
    private readonly AgentRunContextModel _modelContext;
    private readonly Func<AgentRunExecutionState, string, CancellationToken, Task<AgentRunExecutionState>> _failAsync;

    // 当前 Run 的 lease token 与 fencing token（由 Actor 在 ExecuteAsync 时注入）。
    // 非空时构造 Tool 副作用 fence 保护外部副作用边界；null = 无 lease 路径。
    private string? _leaseToken;
    private long? _fencingToken;
    // 实际租约过期时间提供器（由 Actor 注入，读取共享心跳维护的 LastConfirmedExpiresTicks）。
    // Tool 副作用 fence 使用它替代 Run.DeadlineAt 推导值，让 fence 边界与数据库 lease_expires_at 一致。
    private Func<DateTimeOffset?>? _leaseExpiresAtProvider;
    // 对账记录默认截止时长（ToolDescriptor.ReconciliationDeadline 未回传时的兜底）。
    private static readonly TimeSpan DefaultReconciliationDeadline = TimeSpan.FromHours(24);

    /// <summary>
    /// 构造 Tool 分派协作者。
    /// </summary>
    /// <param name="toolDispatcher">Tool 分派器（仅当 durableToolExecutor=null 时使用）。</param>
    /// <param name="toolCallValidator">Tool 校验器（null 时跳过校验）。</param>
    /// <param name="approvalGate">审批门（null 时跳过审批）。</param>
    /// <param name="durableToolExecutor">Durable Tool Executor（null 时回退到 IToolDispatcher）。</param>
    /// <param name="reconciliationStore">Tool 对账记录存储（null 时跳过"未裁决不完成"约束）。</param>
    /// <param name="toolAuthorizationPolicy">Tool 授权策略（null = 无快照校验的旧路径）。</param>
    /// <param name="checkpointFactory">检查点工厂（null 时跳过 checkpoint；强制阈值检查用）。</param>
    /// <param name="eventBuffer">事件缓冲协作者（本地状态推进 / 事件缓冲 / 批量提交）。</param>
    /// <param name="modelContext">上下文 / 模型协作者（读取轮次计数与执行期模型轮次）。</param>
    /// <param name="failAsync">终止接缝：将 Run 标记为 Failed。</param>
    public AgentRunToolDispatch(
        IToolDispatcher toolDispatcher,
        IAgentToolCallValidator? toolCallValidator,
        IAgentApprovalGate? approvalGate,
        IDurableToolExecutor? durableToolExecutor,
        IToolReconciliationStore? reconciliationStore,
        IToolAuthorizationPolicy? toolAuthorizationPolicy,
        IAgentCheckpointFactory? checkpointFactory,
        AgentRunEventBuffer eventBuffer,
        AgentRunContextModel modelContext,
        Func<AgentRunExecutionState, string, CancellationToken, Task<AgentRunExecutionState>> failAsync)
    {
        _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        _toolCallValidator = toolCallValidator;
        _approvalGate = approvalGate;
        _durableToolExecutor = durableToolExecutor;
        _reconciliationStore = reconciliationStore;
        _toolAuthorizationPolicy = toolAuthorizationPolicy;
        _checkpointFactory = checkpointFactory;
        _eventBuffer = eventBuffer ?? throw new ArgumentNullException(nameof(eventBuffer));
        _modelContext = modelContext ?? throw new ArgumentNullException(nameof(modelContext));
        _failAsync = failAsync ?? throw new ArgumentNullException(nameof(failAsync));
    }

    /// <summary>
    /// 每次执行开始时注入 lease token 与租约过期提供器（供 Tool 副作用 fence 构造使用）。
    /// </summary>
    public void BeginExecution(string? leaseToken, long? fencingToken, Func<DateTimeOffset?>? leaseExpiresAtProvider)
    {
        _leaseToken = leaseToken;
        _fencingToken = fencingToken;
        _leaseExpiresAtProvider = leaseExpiresAtProvider;
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
    public async Task<AgentRunExecutionState> ExecutePendingToolAsync(
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
                    return await _failAsync(state, authorizationReason, cancellationToken).ConfigureAwait(false);
                }

                state = _eventBuffer.BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
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
                    state = _eventBuffer.BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
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
                        state = _eventBuffer.BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
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
                        state = _eventBuffer.TransitionStateLocal(state, AgentRunState.AwaitingApproval);

                        // 构建新的 PendingToolCommands：当前后续 Tool + 剩余未处理的 Tool
                        var newPendingCommands = new List<PendingToolCommand> { pendingCommand };
                        for (var j = cmdIndex + 1; j < pendingCommands.Count; j++)
                        {
                            newPendingCommands.Add(pendingCommands[j]);
                        }

                        state = _eventBuffer.BufferEvent(state, AgentRunEventType.ApprovalRequested, JsonSerializer.Serialize(new
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
                            state = _eventBuffer.BufferEvent(state, AgentRunEventType.ApprovalResolved, JsonSerializer.Serialize(new
                            {
                                approved = false,
                                pending = true,
                                approvalId = effectiveApprovalId,
                                approverId = (string?)null,
                                rejectionReason = (string?)null
                            }));

                            await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

                            // 保存剩余 PendingToolCommands（含当前需审批的 Tool），供恢复后执行
                            state = state with { PendingToolCommands = newPendingCommands };
                            return state;
                        }

                        state = _eventBuffer.BufferEvent(state, AgentRunEventType.ApprovalResolved, JsonSerializer.Serialize(new
                        {
                            approved = approval.Approved,
                            approverId = approval.ApproverId,
                            rejectionReason = approval.RejectionReason
                        }));

                        if (!approval.Approved)
                        {
                            // 审批拒绝 → 跳过此 Tool，继续处理下一个
                            state = _eventBuffer.TransitionStateLocal(state, AgentRunState.PendingToolExecution);
                            continue;
                        }

                        // 审批通过 → 转回 PendingToolExecution 继续执行此 Tool
                        state = _eventBuffer.TransitionStateLocal(state, AgentRunState.PendingToolExecution);
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
            state = _eventBuffer.BufferEvent(state, AgentRunEventType.ToolCallStarted, JsonSerializer.Serialize(new
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
            await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

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
            state = _eventBuffer.BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
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

            state = _eventBuffer.BufferEvent(state, AgentRunEventType.ObservationAppended, JsonSerializer.Serialize(new
            {
                toolName = pendingCommand.ToolName,
                observationLength = observation.Length
            }));

            // mid-loop 缓冲超过阈值时强制 flush
            if (_eventBuffer.PendingEventCount >= AgentRunEventBuffer.PendingEventsFlushThreshold)
            {
                await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
            }
        }

        // 所有 Pending Tool 执行完成 → Observing（本地推进）
        state = _eventBuffer.TransitionStateLocal(state, AgentRunState.Observing);

        // Checkpointing（若有工厂）→ ContextBuilding（下一轮）
        // 强制 checkpoint 阈值由事件缓冲的未 checkpoint 计数跟踪：Turn 结束的 checkpoint 会重置计数。
        if (_checkpointFactory is not null)
        {
            state = await _eventBuffer.PersistCheckpointAsync(state, _modelContext.CurrentTurn, _modelContext.ExecutionModelTurn, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (_eventBuffer.EventsSinceLastCheckpoint >= AgentRunEventBuffer.ForcedCheckpointEventThreshold)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[AgentRunToolDispatch] 未 checkpoint 事件数 ({0}) 达到强制阈值 ({1})，run={2}，但无 checkpoint factory 配置。",
                    _eventBuffer.EventsSinceLastCheckpoint, AgentRunEventBuffer.ForcedCheckpointEventThreshold, state.Run.RunId);
            }
            state = _eventBuffer.TransitionStateLocal(state, AgentRunState.ContextBuilding);
        }

        // Turn 结束 → 批量提交所有缓冲事件 + state CAS（单事务）
        await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

        // 清除 PendingToolCommands（已全部执行完成）
        return state with { PendingToolCommands = null };
    }

    /// <summary>执行 DispatchTool 阶段（含校验 + 审批 + 分派 + 观察）。</summary>
    public async Task<AgentRunExecutionState> DispatchToolsAsync(AgentRunExecutionState state, CancellationToken cancellationToken)
    {
        if (state.LastModelResponse is null || state.LastModelResponse.ToolCalls.Count == 0)
        {
            // 无 Tool 调用 → 回到 ContextBuilding
            return _eventBuffer.TransitionStateLocal(state, AgentRunState.ContextBuilding);
        }

        // 进入 ToolDispatching（本地推进 + 缓冲 StateTransition 事件）
        state = _eventBuffer.TransitionStateLocal(state, AgentRunState.ToolDispatching);

        for (var toolIndex = 0; toolIndex < state.LastModelResponse!.ToolCalls.Count; toolIndex++)
        {
            var toolCall = state.LastModelResponse.ToolCalls[toolIndex];

            // 在循环开始时生成 toolCallId，同时用于 ToolCallStarted 和 ToolCallCompleted
            // 确保 ToolCallStarted 事件和 ToolCallCompleted 事件的审计 ID 一致。
            // 多轮协议修复：优先使用模型返回的 ToolCallId（如 OpenAI 的 tool_call_id），
            // 确保 Tool 观察消息的 tool_call_id 与 Assistant 消息的 tool_calls[].id 一致——
            // OpenAI / Anthropic 兼容 API 要求二者匹配，否则第二轮调用会被拒绝。
            // 优先使用 NormalizedToolCall.InvocationId（由上下文协作者在模型响应进入 Actor
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
                state = _eventBuffer.BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
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
                    return await _failAsync(state, authorizationReason, cancellationToken).ConfigureAwait(false);
                }

                state = _eventBuffer.BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
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
                    state = _eventBuffer.BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
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
                    state = _eventBuffer.TransitionStateLocal(state, AgentRunState.AwaitingApproval);

                    // 持久化完整 PendingToolCommand 到 ApprovalRequested 事件 payload，
                    // 让审批通过后恢复时可直接执行原 Tool（不依赖模型重生成）。
                    // 包含 ToolCallId / ToolName / ArgumentsJson / IdempotencyKey / ModelTurnRevision。
                    var pendingCommand = new PendingToolCommand
                    {
                        ToolCallId = toolCallId,
                        ToolName = toolCall.ToolName ?? string.Empty,
                        ArgumentsJson = toolCall.Arguments ?? string.Empty,
                        IdempotencyKey = toolCall.IdempotencyKey,
                        ModelTurnRevision = _modelContext.ExecutionModelTurn
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
                            ModelTurnRevision = _modelContext.ExecutionModelTurn
                        });
                    }

                    state = _eventBuffer.BufferEvent(state, AgentRunEventType.ApprovalRequested, JsonSerializer.Serialize(new
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
                        state = _eventBuffer.BufferEvent(state, AgentRunEventType.ApprovalResolved, JsonSerializer.Serialize(new
                        {
                            approved = false,
                            pending = true,
                            approvalId = effectiveApprovalId,
                            approverId = (string?)null,
                            rejectionReason = (string?)null
                        }));

                        // 立即 flush：将 AwaitingApproval 状态 + 事件持久化（单事务）
                        await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

                        // 将完整 PendingToolCommands 列表保存到执行状态，供恢复时依次执行。
                        state = state with { PendingToolCommands = pendingCommands };

                        // 退出执行槽：返回 AwaitingApproval 状态，主循环检测后 return
                        return state;
                    }

                    state = _eventBuffer.BufferEvent(state, AgentRunEventType.ApprovalResolved, JsonSerializer.Serialize(new
                    {
                        approved = approval.Approved,
                        approverId = approval.ApproverId,
                        rejectionReason = approval.RejectionReason
                    }));

                    if (!approval.Approved)
                    {
                        // 审批拒绝 → 回到 ToolDispatching 状态后跳过此 Tool
                        state = _eventBuffer.TransitionStateLocal(state, AgentRunState.ToolDispatching);
                        continue;
                    }

                    // 批准后回到 ToolDispatching
                    state = _eventBuffer.TransitionStateLocal(state, AgentRunState.ToolDispatching);
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
                ? DefaultDurableToolExecutor.ComputeRequestId(state.Run.WorkspaceId, state.Run.RunId, effectiveToolCall, _modelContext.ExecutionModelTurn)
                : toolCallId;

            // ToolCallStarted 携带 arguments + modelTurnRevision：
            // 进程在 Tool 执行中被 Kill 时，恢复节点据此重建原 PendingToolCommand（原始轮次），
            // RequestId 与 journal 条目一致 → durable 去重生效，不重复执行外部副作用。
            state = _eventBuffer.BufferEvent(state, AgentRunEventType.ToolCallStarted, JsonSerializer.Serialize(new
            {
                toolName = toolCall.ToolName,
                toolCallId = toolCallId,
                requestId = requestIdForStart,
                idempotencyKey = toolCall.IdempotencyKey,
                arguments = toolCall.Arguments,
                modelTurnRevision = _modelContext.ExecutionModelTurn
            }));

            // flush 持久化 ToolCallStarted 后再执行外部 Tool（先日志后执行）。
            await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

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
                    state.Run.RunId, state.Run.WorkspaceId, effectiveToolCall, _modelContext.ExecutionModelTurn,
                    cancellationToken, leaseFence2, state.Run.DeadlineAt,
                    approvalGranted: true).ConfigureAwait(false);
            }
            else
            {
                // 回退路径：直接调 IToolDispatcher（无 journal，无 durable 保证）
                // 使用预生成的 toolCallId 作为 RequestId（与 ToolCallStarted/Completed 一致）
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
            state = _eventBuffer.BufferEvent(state, AgentRunEventType.ToolCallCompleted, JsonSerializer.Serialize(BuildCompletedPayload(
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
            state = _eventBuffer.BufferEvent(state, AgentRunEventType.ObservationAppended, JsonSerializer.Serialize(new
            {
                toolName = toolCall.ToolName,
                observationLength = observation.Length
            }));

            // mid-loop 缓冲超过阈值时强制 flush，避免大量 Tool 调用导致内存膨胀
            if (_eventBuffer.PendingEventCount >= AgentRunEventBuffer.PendingEventsFlushThreshold)
            {
                await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);
            }
        }

        // Tool 分派完成 → Observing（本地推进）
        state = _eventBuffer.TransitionStateLocal(state, AgentRunState.Observing);

        // 清除 LastModelResponse：Tool 已分派完毕，下一轮应由 LoopPolicy 决定 CallModel
        // （而非重复 DispatchTool）。未清除会导致 ContextBuilding → ToolDispatching 非法转换。
        // Context.LastModelTurn 保留（供 ProjectForModel 投影历史上下文）。
        // 同步清除 NormalizedToolCalls（已分派完毕，避免下一轮残留）。
        state = state with { LastModelResponse = null, NormalizedToolCalls = null };

        // Bug 3 修复：Turn 已在上下文协作者中递增（每次模型调用计为一次 Turn）
        // 此处不再重复递增 Turn（避免双重计数）

        // 更新本地 Run 副本（不再单独调 _runStore.UpdateAsync；CAS + 字段更新延后到批量提交）
        var updatedRun = state.Run with
        {
            Turn = _modelContext.CurrentTurn,
            ModelCallsUsed = _modelContext.ModelCallsUsed,
            TurnBudget = _modelContext.TurnBudget,
            CostBudget = _modelContext.CostBudget,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        state = state with { Run = updatedRun };

        // Checkpointing（若有工厂）→ ContextBuilding（下一轮）
        // 强制 checkpoint 阈值由事件缓冲的未 checkpoint 计数跟踪：Turn 结束的 checkpoint 会重置计数。
        // 若 _checkpointFactory 为 null 但未 checkpoint 事件已达阈值，记录警告（无法强制 checkpoint）。
        if (_checkpointFactory is not null)
        {
            state = await _eventBuffer.PersistCheckpointAsync(state, _modelContext.CurrentTurn, _modelContext.ExecutionModelTurn, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (_eventBuffer.EventsSinceLastCheckpoint >= AgentRunEventBuffer.ForcedCheckpointEventThreshold)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[AgentRunToolDispatch] 未 checkpoint 事件数 ({0}) 达到强制阈值 ({1})，run={2}，但无 checkpoint factory 配置。",
                    _eventBuffer.EventsSinceLastCheckpoint, AgentRunEventBuffer.ForcedCheckpointEventThreshold, state.Run.RunId);
            }
            // 无 checkpoint 工厂 → 直接进入下一轮
            state = _eventBuffer.TransitionStateLocal(state, AgentRunState.ContextBuilding);
        }

        // Turn 结束 → 批量提交所有缓冲事件 + state CAS + checkpoint cursor（单事务）
        await _eventBuffer.FlushPendingEventsAsync(state.Run, cancellationToken).ConfigureAwait(false);

        return state;
    }

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

    /// <summary>
    /// Journal 处于模糊状态（DispatchingIntent/Dispatched/Reconciling）时创建对账记录。
    /// 幂等：按 RunId+RequestId 已存在时返回既有记录。只要 Run 存在未裁决记录，
    /// 终端方法（CompleteAsync）就不得推进到 Completed（等待 Worker 或人工 resolve 端点裁决）。
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
}
