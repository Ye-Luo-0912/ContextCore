using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// DefaultDurableToolExecutor — Durable Tool 执行器默认实现
// 
// 封装 Tool 调用的完整 durable 流程，
// 让 AgentRunActor 不再直接调用 IToolDispatcher。
// 
// 流程：
// 1. 生成稳定 RequestId（基于 runId + toolCall 哈希，确保可重放时一致）
// + 框架生成 ExternalOperationId（外部操作 ID，Prepare 时落库，重放稳定）
// 2. 校验 ToolName 非空 + Dispatcher 支持 + 读取 ToolDescriptor 前置声明
// 3. Journal.PrepareWithIntentAsync（若注入 journal）→ 单次原子写完成 Prepare + 前置 Intent
// （合并两次写为一次），根据结果决策：
// a. CachedResult 非空（Journal = Committed/ResultDelivered）→ 直接返回缓存结果，禁止重新 Dispatch
// b. NeedsReconciliation=true（Journal = DispatchingIntent/Dispatched）→ 返回对账结果（携带 ExternalOperationId），不重新 Dispatch
// c. ShouldDispatch=true（本次新插入或既有 Prepared 已推进，journal 已处于 DispatchingIntent）→ 继续 Dispatch
// 4. 声明校验（fail-closed）：RequiresLeaseFence 但无 fence → 拒绝；RequiresIdempotencyKey
// 但无键 → 以 ExternalOperationId 兜底为幂等键
// 5. IToolDispatcher.DispatchAsync（携带 RequestId + IdempotencyKey + ExternalOperationId）
// 6. Journal.MarkDispatchedAsync（若注入 journal）
// 7. 副作用分类（声明优先，运行时结果仅验证）：
// - None/ReadOnly/Write 等（非 Unknown）→ Journal.MarkCommittedWithResultAsync（同事务持久化 state + result）
// 并写入 IDurableToolResultStore（若注入，供 Postgres 路径缓存查询）
// - Unknown → 不提交（等待调用方裁决，返回 JournalState=Dispatched）
// 8. 返回 ToolExecutionResult（含完整 Tool 身份信息）
// 
// 修复要点：
// - PrepareAsync 返回值决定是否 Dispatch，避免对 Committed/ResultDelivered 重复 Dispatch
// - Dispatched 模糊状态返回对账结果，不盲目重新执行外部副作用
// - MarkCommittedWithResultAsync 原子提交 state + result，确保崩溃恢复时缓存可读
// - IDurableToolResultStore 作为 Postgres 路径的结果缓存（InMemory journal 自带缓存）
// - ToolDescriptor 前置副作用声明参与执行前决策与提交分类（声明权威，运行时验证）
// 
// 设计决策：
// - RequestId 由本执行器生成（稳定哈希），确保 Actor 与 Dispatcher 共享同一 ID。
// - ExternalOperationId 由本执行器从稳定 RequestId 派生（"cc" + requestId），
// Prepare 时随 journal 条目持久化；Handler 返回真实外部系统 ID 时在 MarkDispatchedAsync 覆盖。
// - Journal / Outbox / ResultStore 为可选依赖（null 时降级为直接 dispatch，无 durable 保证）。
// - 审批：Actor 在调用本执行器前通过 IAgentApprovalGate 完成审批，并经
// ExecuteAsync 的 approvalGranted=true 显式告知策略层；直连调用（approvalGranted=false）时
// 策略层对 RequiresApproval 的写副作用 fail-safe（禁止自动提交），防止绕过 Actor 门。
// - 失败重试：由策略引擎决定（副作用重试安全 + 未达 MaxRetries 上限），
// 退避等待后以同一 RequestId/幂等键/ExternalOperationId 重试，外部 Provider 幂等记录可命中。
// - 投递模式：AsyncDurable 时 Commit 后显式推进 ResultDelivered（结果已送达事件流）。
// - 异常时返回 Succeeded=false 的结果（不抛异常，让 Actor 决定如何处理）。
// ===========================================================================

/// <summary>
/// Durable Tool 执行器默认实现。
/// 封装 Tool 调用的完整 durable 编排流程（journal + dispatch + commit），
/// 让 <see cref="AgentRunActor"/> 不再直接调用 <see cref="IToolDispatcher"/>。
/// </summary>
/// <remarks>
/// 封装 Tool 调用的完整 durable 编排流程，
/// 让 <see cref="AgentRunActor"/> 不再直接调用 <see cref="IToolDispatcher"/>。
/// 
/// 根据 <see cref="IToolDispatchJournal.PrepareAsync"/> 返回的
/// <see cref="ToolDispatchPrepareResult"/> 决策是否 Dispatch、对账或返回缓存结果，
/// 防止对已 Committed/ResultDelivered 的 Tool 重复执行外部副作用。
/// </remarks>
public sealed class DefaultDurableToolExecutor : IDurableToolExecutor
{
    private readonly IToolDispatcher _toolDispatcher;
    private readonly IToolDispatchJournal? _dispatchJournal;
    private readonly IDurableToolResultStore? _resultStore;
    private readonly IToolEffectPolicy _effectPolicy;

    /// <summary>
    /// 构造 Durable Tool 执行器。
    /// </summary>
    /// <param name="toolDispatcher">Tool 分派器（必需）。</param>
    /// <param name="dispatchJournal">Tool 分派 journal（可选；null 时无 durable 保证）。</param>
    /// <param name="resultStore">
    /// Durable Tool 结果缓存存储（可选）。
    /// 注入后在 Committed 时缓存结果，供后续 PrepareAsync 查询（Postgres journal 不自带缓存）。
    /// null 时仅依赖 journal 内置缓存（InMemory journal 自带；Postgres journal 不缓存）。
    /// </param>
    /// <param name="effectPolicy">
    /// Tool 执行策略引擎（可选；null 时使用 <see cref="DefaultToolEffectPolicy"/>）。
    /// 决定 Dispatch 后是否自动提交（严格矩阵），防止危险状态被误提交。
    /// </param>
    public DefaultDurableToolExecutor(
        IToolDispatcher toolDispatcher,
        IToolDispatchJournal? dispatchJournal = null,
        IDurableToolResultStore? resultStore = null,
        IToolEffectPolicy? effectPolicy = null)
    {
        _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        _dispatchJournal = dispatchJournal;
        _resultStore = resultStore;
        _effectPolicy = effectPolicy ?? new DefaultToolEffectPolicy();
    }

    /// <inheritdoc />
    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        string runId,
        string workspaceId,
        AgentToolCallRequest toolCall,
        int modelTurn,
        CancellationToken cancellationToken = default,
        AgentLeaseFence? leaseFence = null,
        DateTimeOffset? deadlineAt = null,
        bool approvalGranted = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(toolCall);

        // Run 复合身份键（工作区 + Run）——journal / 结果缓存一律以完整租户键寻址，
        // 跨工作区同 RunId 与同 RequestId 互不干扰。
        var runKey = new TenantRunKey(workspaceId, runId);

        // 生成稳定 RequestId（基于 workspaceId + runId + modelTurn + toolCallId + toolName + arguments 哈希）。
        // workspaceId 参与哈希——不同工作区可使用相同 RunId 与相同 Tool 调用而互不冲突
        // （journal 以 (workspace_id, run_id, request_id) 复合键寻址，跨工作区隔离）。
        // IdempotencyKey 不参与 RequestId 计算——它是业务级去重键，单独存储于 journal 条目，
        // 不应影响调用身份（InvocationId）。这避免同一 Run 内不同轮次的相同 Tool 调用被误判为重复。
        var requestId = ComputeRequestId(workspaceId, runId, toolCall, modelTurn);
        var idempotencyKey = toolCall.IdempotencyKey;
        // toolCallId 优先使用模型分配的 ToolCallId，缺失时回退到 RequestId
        var toolCallId = !string.IsNullOrWhiteSpace(toolCall.ToolCallId) ? toolCall.ToolCallId! : requestId;

        // 外部操作 ID 从稳定 RequestId 派生（"cc" + requestId），不使用 GUID。
        // 崩溃恢复时同一 (runId, modelTurn, toolCallId, toolName, arguments) 产出相同 RequestId，
        // 派生值即恢复值；Journal 条目持久化的也是该派生值，外部 Provider 幂等记录恢复后可命中。
        // 若 Handler 在 Dispatch 后返回真实外部系统 ID，MarkDispatchedAsync 时以真实 ID 覆盖。
        var externalOperationId = "cc:" + requestId;

        // 读取 Tool 前置声明（副作用 / 审批 / 幂等 / fence / 恢复策略），
        // 用于 Dispatch 前的 fail-closed 校验与提交分类（声明权威，运行时结果仅验证）。
        var descriptor = _toolDispatcher.GetDescriptor(toolCall.ToolName);

        // 幂等键兜底：声明要求幂等键但调用方未提供 → 从稳定 RequestId 派生
        // （providerNamespace + "" + requestId，providerNamespace 取 ToolName 命名空间）。
        // 派生值随 journal 持久化，重放时从 Prepare 结果读回，保证同一次调用键稳定。
        var effectiveIdempotencyKey = descriptor is { RequiresIdempotencyKey: true } && string.IsNullOrWhiteSpace(idempotencyKey)
            ? toolCall.ToolName + ":" + requestId
            : idempotencyKey;

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 2. 校验 ToolName 非空
        if (string.IsNullOrWhiteSpace(toolCall.ToolName))
        {
            return BuildFailedResult(
                requestId, idempotencyKey, ToolSideEffect.Unknown,
                error: "ToolName 不能为空。",
                journalState: ToolDispatchState.Prepared,
                duration: stopwatch.Elapsed,
                failurePhase: ToolFailurePhase.BeforeIntent);
        }

        // 3. 校验 Dispatcher 支持
        if (!_toolDispatcher.SupportedTools.Contains(toolCall.ToolName))
        {
            return BuildFailedResult(
                requestId, idempotencyKey, ToolSideEffect.Unknown,
                error: $"不支持的 tool: {toolCall.ToolName}",
                journalState: ToolDispatchState.Prepared,
                errorKind: DispatchErrorKind.UnregisteredTool,
                duration: stopwatch.Elapsed,
                failurePhase: ToolFailurePhase.BeforeIntent);
        }

        // 4. Journal.PrepareWithIntentAsync（若注入 journal）→ 单次原子写完成 Prepare + 前置 Intent
        // （合并 PrepareAsync + MarkDispatchingIntentAsync 两次往返为一次，且 durable 边界
        // 与条目创建原子化——RecoveryDecision=Dispatch 时 journal 必已处于 DispatchingIntent）。
        // 根据 ToolDispatchPrepareResult 决策：
        ToolDispatchPrepareResult? prepareResult = null;
        if (_dispatchJournal is not null)
        {
            try
            {
                var entry = new ToolDispatchJournalEntry
                {
                    RequestId = requestId,
                    ToolName = toolCall.ToolName,
                    State = ToolDispatchState.Prepared,
                    IdempotencyKey = effectiveIdempotencyKey,
                    ExternalOperationId = externalOperationId,
                    PayloadDigest = ToolDispatchJournalEntry.ComputePayloadDigest(toolCall.Arguments),
                    WorkspaceId = workspaceId,
                    RunId = runId,
                    UpdatedAt = startedAt
                };
                prepareResult = await _dispatchJournal.PrepareWithIntentAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // PrepareWithIntentAsync 失败（如 RequestId 复用检测 / 语义等价校验失败）。
                // 不得无条件伪造 Prepared——若既有条目已处于更高级状态
                // （复用检测命中历史调用），必须按真实状态返回并进入对账。
                var prepareState = await QueryJournalStateAsync(workspaceId, runId, requestId, cancellationToken).ConfigureAwait(false);
                var afterIntent = prepareState > ToolDispatchState.Prepared;
                return BuildFailedResult(
                    requestId, effectiveIdempotencyKey, ToolSideEffect.Unknown,
                    error: $"Journal PrepareWithIntentAsync 失败：{ex.Message}",
                    journalState: afterIntent ? prepareState : ToolDispatchState.Prepared,
                    duration: stopwatch.Elapsed,
                    failurePhase: afterIntent ? ToolFailurePhase.AfterIntentBeforeProvider : ToolFailurePhase.BeforeIntent,
                    reconciliationHandler: afterIntent ? descriptor?.ReconciliationHandler : null,
                    reconciliationDeadline: afterIntent ? descriptor?.ReconciliationDeadline : null,
                    externalOperationId: externalOperationId);
            }

            // Journal 是调用身份的权威来源：PrepareWithIntentAsync 原子返回
            // RequestId / ExternalOperationId / EffectiveIdempotencyKey / CurrentState / RecoveryDecision。
            // 新插入返回本次派生的值；崩溃恢复（重放）返回既有条目持久化的值。
            // 后续 Dispatch / MarkDispatched / MarkCommitted 一律使用 Journal 返回的身份——
            // 不能使用恢复时重新生成的值，否则 ExternalOperationId / IdempotencyKey 漂移，
            // 外部 Provider 幂等记录无法命中，Journal 语义等价检查报 RequestIdReuseDetected。
            requestId = prepareResult.RequestId ?? requestId;
            externalOperationId = prepareResult.ExternalOperationId ?? externalOperationId;
            effectiveIdempotencyKey = prepareResult.EffectiveIdempotencyKey ?? effectiveIdempotencyKey;

            switch (prepareResult.RecoveryDecision)
            {
                // 4a. Journal = Committed/ResultDelivered 且缓存结果可用 → 直接返回缓存，禁止重新 Dispatch
                case ToolDispatchRecoveryDecision.UseCachedResult when prepareResult.CachedResult is not null:
                    stopwatch.Stop();
                    return BuildCachedResult(prepareResult.CachedResult, stopwatch.Elapsed,
                        journalState: prepareResult.CurrentState);

                // 4c. Journal = Committed/ResultDelivered 但 journal 不缓存结果（Postgres 路径）
                // 查询 IDurableToolResultStore；无 resultStore 或缓存未命中 → 返回对账结果
                case ToolDispatchRecoveryDecision.UseCachedResult:
                    if (_resultStore is not null)
                    {
                        DurableToolResult? cached = null;
                        try
                        {
                            cached = await _resultStore.GetByRequestIdAsync(runKey, requestId, cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            // ResultStore 查询失败不阻断流程；降级为对账结果
                        }
                        if (cached is not null)
                        {
                            stopwatch.Stop();
                            return BuildCachedResult(cached, stopwatch.Elapsed,
                                journalState: prepareResult.CurrentState);
                        }
                    }

                    // 无 resultStore 或缓存未命中，但 journal 已 Committed/ResultDelivered → 模糊状态，返回对账结果
                    // 不重新 Dispatch（journal 明确指示 UseCachedResult，重新执行会违反 exactly-once）。
                    // 注意：此处 JournalState 保持 Dispatched（安全方向）——journal 虽已 Committed 但结果缺失，
                    // 返回真实状态会使 Actor 跳过对账（RequiresReconciliation(Committed)=false），Run 无法恢复结果；
                    // Dispatched 强制调用方对账找回结果。
                    stopwatch.Stop();
                    return BuildReconciliationResult(
                        requestId, effectiveIdempotencyKey, externalOperationId, stopwatch.Elapsed,
                        reconciliationHandler: descriptor?.ReconciliationHandler,
                        reconciliationDeadline: descriptor?.ReconciliationDeadline);

                // 4b. Journal = DispatchingIntent/Dispatched/Reconciling 模糊态 → 返回对账结果，不重新 Dispatch。
                // 外部副作用可能已执行但未提交，调用方需经 BeginReconciliationAsync 显式对账或人工裁决。
                // JournalState 回传真实状态（prepareResult.CurrentState），不伪造。
                case ToolDispatchRecoveryDecision.Reconcile:
                    stopwatch.Stop();
                    return BuildReconciliationResult(
                        requestId, effectiveIdempotencyKey, externalOperationId, stopwatch.Elapsed,
                        journalState: prepareResult.CurrentState,
                        reconciliationHandler: descriptor?.ReconciliationHandler,
                        reconciliationDeadline: descriptor?.ReconciliationDeadline);

                // 4e. Journal 明确要求 fail-closed → 返回失败，不 Dispatch。
                case ToolDispatchRecoveryDecision.FailClosed:
                    stopwatch.Stop();
                    return BuildFailedResult(
                        requestId, effectiveIdempotencyKey, ToolSideEffect.Unknown,
                        error: $"Journal 恢复决策为 FailClosed（{prepareResult.CurrentState}），禁止执行 Tool '{toolCall.ToolName}'。",
                        journalState: prepareResult.CurrentState,
                        duration: stopwatch.Elapsed);

                // 4d. Dispatch：本次新插入或既有 Prepared 已推进，journal 已处于 DispatchingIntent → 继续 Dispatch
                case ToolDispatchRecoveryDecision.Dispatch:
                default:
                    break;
            }
        }

        // 5. Dispatch（携带 RequestId + 执行上下文：WorkspaceId/RunId/IdempotencyKey/ExternalOperationId）
        // Intent 已在 PrepareWithIntentAsync 中前置落库（DispatchingIntent = durable 边界），
        // 此处无需再单独标记；若进程在此之后崩溃，恢复时知道外部调用可能已开始，需对账而非盲目重放。
        // 
        // 失败重试（策略驱动）：Dispatch 失败且副作用重试安全、未达 Descriptor.MaxRetries 上限时，
        // 按策略 Retry.Delay 退避后重试——同一 RequestId/幂等键/ExternalOperationId
        // （journal 条目与外部 Provider 幂等记录保持一致，重放安全）。
        var maxDispatchAttempts = descriptor is { RetryBackoffPolicy: not ToolRetryBackoffPolicy.None }
            ? 1 + Math.Max(0, descriptor.MaxRetries)
            : 1;
        // 失败重试决策用副作用：以声明为准（失败时运行时结果不可信/不可得），
        // 未声明（null）→ Unknown（保守：不自动重试、不自动提交）。
        var declaredSideEffect = descriptor is { DeclaredSideEffect: not ToolSideEffect.Unknown }
            ? descriptor.DeclaredSideEffect
            : ToolSideEffect.Unknown;

        ToolDispatchResult? dispatchResult = null;
        Exception? dispatchException = null;
        var retryAttemptsPerformed = 0;
        var finalAttempt = 0;

        for (var attempt = 0; attempt < maxDispatchAttempts; attempt++)
        {
            dispatchException = null;
            try
            {
                // 对写副作用 Tool 做 lease fence 校验——lease 已失效时 fail-closed，
                // 阻止旧 Owner 在租约过期后执行外部写操作。
                // ReadOnly/None 可跳过校验（无副作用，重放安全）。
                // NonIdempotentWrite 在 ProductionHA 下无外部幂等或 fencing 支持时必须 fail-closed。
                if (leaseFence is not null && DateTimeOffset.UtcNow >= leaseFence.ExpiresAt)
                {
                    // Intent 已在 PrepareWithIntentAsync 中持久化（journal=DispatchingIntent），
                    // 必须按真实状态返回并进入对账（确认外部无副作用后裁决），不得伪造 Prepared。
                    var fenceState = await QueryJournalStateAsync(workspaceId, runId, requestId, cancellationToken).ConfigureAwait(false);
                    return BuildFailedResult(
                        requestId, effectiveIdempotencyKey, ToolSideEffect.Unknown,
                        error: $"Lease 已过期（ExpiresAt={leaseFence.ExpiresAt:O}），Tool 执行被 fence 阻止（journal={fenceState}，需对账确认外部无副作用）。",
                        journalState: fenceState,
                        duration: stopwatch.Elapsed,
                        errorKind: DispatchErrorKind.LeaseFenceViolation,
                        failurePhase: ToolFailurePhase.AfterIntentBeforeProvider,
                        reconciliationHandler: descriptor?.ReconciliationHandler,
                        reconciliationDeadline: descriptor?.ReconciliationDeadline,
                        externalOperationId: externalOperationId);
                }

                // 声明要求 lease fence 但调用方未提供 → fail-closed（副作用 Tool 无 fencing 保护时禁止执行）
                if (descriptor is { RequiresLeaseFence: true } && leaseFence is null)
                {
                    // 同 fence 过期——Intent 已持久化，按真实状态返回并进入对账。
                    var fenceState = await QueryJournalStateAsync(workspaceId, runId, requestId, cancellationToken).ConfigureAwait(false);
                    return BuildFailedResult(
                        requestId, effectiveIdempotencyKey, ToolSideEffect.Unknown,
                        error: $"Tool '{toolCall.ToolName}' 声明 RequiresLeaseFence，但调用未携带 LeaseFence，执行被拒绝（fail-closed，journal={fenceState}，需对账确认外部无副作用）。",
                        journalState: fenceState,
                        duration: stopwatch.Elapsed,
                        errorKind: DispatchErrorKind.LeaseFenceViolation,
                        failurePhase: ToolFailurePhase.AfterIntentBeforeProvider,
                        reconciliationHandler: descriptor?.ReconciliationHandler,
                        reconciliationDeadline: descriptor?.ReconciliationDeadline,
                        externalOperationId: externalOperationId);
                }

                dispatchResult = await _toolDispatcher.DispatchAsync(new ToolDispatchRequest
                {
                    ToolName = toolCall.ToolName,
                    Payload = toolCall.Arguments,
                    RequestId = requestId,
                    IdempotencyKey = effectiveIdempotencyKey,
                    ExternalOperationId = externalOperationId,
                    WorkspaceId = workspaceId,
                    RunId = runId,
                    // 透传 leaseFence + deadlineAt 到 ToolDispatchRequest，
                    // 让 Tool Handler 在执行外部写操作前校验租约有效性。
                    LeaseFence = leaseFence,
                    DeadlineAt = deadlineAt
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Dispatch 异常（Dispatcher 级故障）→ 记录后按策略决定是否重试。
                dispatchException = ex;
                dispatchResult = null;
            }

            finalAttempt = attempt;

            // 成功 → 退出重试循环。
            if (dispatchException is null && dispatchResult is { Succeeded: true })
            {
                break;
            }

            // 失败 → 策略决定是否重试（重试安全契约 + 未达上限）。
            var failureResult = new ToolExecutionResult
            {
                RequestId = requestId,
                IdempotencyKey = effectiveIdempotencyKey,
                SideEffect = declaredSideEffect,
                ExternalOperationId = externalOperationId,
                JournalState = ToolDispatchState.DispatchingIntent,
                Succeeded = false,
                Error = dispatchException?.Message ?? dispatchResult?.Error ?? "Tool dispatch 失败",
                ErrorKind = dispatchException is not null
                    ? DispatchErrorKind.HandlerException
                    : dispatchResult?.ErrorKind ?? DispatchErrorKind.Unknown,
                // 异常 → Provider 调用结果不明确（可能已发生副作用）；返回确定失败 → ProviderReturned。
                FailurePhase = dispatchException is not null
                    ? ToolFailurePhase.ProviderCallAmbiguous
                    : ToolFailurePhase.ProviderReturned,
                // 仅当 Provider 明确确认无副作用时才允许 ProviderConfirmedNoEffect 重试。
                NoEffectConfirmed = dispatchResult?.NoEffectConfirmed ?? false,
                Duration = stopwatch.Elapsed
            };
            var retryPolicy = _effectPolicy.Resolve(
                descriptor ?? new ToolDescriptor
                {
                    // Dispatcher 未提供前置声明（如 EchoToolDispatcher）→ 以 Unknown 合成描述符，
                    // 策略按观测值保守处置（Unknown → 不自动提交、不自动重试）。
                    Name = toolCall.ToolName,
                    DeclaredSideEffect = declaredSideEffect
                },
                prepareResult, failureResult, attempt);

            if (!retryPolicy.Retry.ShouldRetry)
            {
                break;
            }

            // 退避等待后重试（同一调用身份；幂等键/ExternalOperationId 不变）。
            retryAttemptsPerformed = attempt + 1;
            await Task.Delay(retryPolicy.Retry.Delay, cancellationToken).ConfigureAwait(false);
        }

        // 重试耗尽仍为异常 → 返回失败。：Intent 已持久化，必须查询并返回真实 Journal 状态
        // （库里是 DispatchingIntent 就返回 DispatchingIntent，使 Actor 创建对账记录），
        // 不得伪造 Prepared——外部副作用可能已发生。
        if (dispatchException is not null)
        {
            var error = retryAttemptsPerformed > 0
                ? $"Dispatch 重试 {retryAttemptsPerformed} 次后仍异常：{dispatchException.Message}"
                : $"Dispatch 异常：{dispatchException.Message}";
            var exhaustedState = await QueryJournalStateAsync(workspaceId, runId, requestId, cancellationToken).ConfigureAwait(false);
            return BuildFailedResult(
                requestId, effectiveIdempotencyKey, ToolSideEffect.Unknown,
                error: error,
                journalState: exhaustedState,
                duration: stopwatch.Elapsed,
                failurePhase: ToolFailurePhase.ProviderCallAmbiguous,
                reconciliationHandler: descriptor?.ReconciliationHandler,
                reconciliationDeadline: descriptor?.ReconciliationDeadline,
                externalOperationId: externalOperationId);
        }

        // 非异常路径 → DispatchAsync 已成功返回（结果可能 Succeeded=false），dispatchResult 必非空。
        if (dispatchResult is null)
        {
            // 内部不一致（正常返回但无结果）→ 按模糊失败处理：真实 Journal 状态 + 强制对账。
            var nullState = await QueryJournalStateAsync(workspaceId, runId, requestId, cancellationToken).ConfigureAwait(false);
            return BuildFailedResult(
                requestId, effectiveIdempotencyKey, ToolSideEffect.Unknown,
                error: "Tool dispatch 未返回结果（内部不一致）。",
                journalState: nullState,
                duration: stopwatch.Elapsed,
                failurePhase: ToolFailurePhase.ProviderCallAmbiguous,
                reconciliationHandler: descriptor?.ReconciliationHandler,
                reconciliationDeadline: descriptor?.ReconciliationDeadline,
                externalOperationId: externalOperationId);
        }


        // 6. Journal.MarkDispatchedAsync（若注入 journal）
        // 外部操作 ID：Handler 返回真实外部系统 ID 时优先，否则保留框架生成值。
        if (_dispatchJournal is not null)
        {
            try
            {
                await _dispatchJournal.MarkDispatchedAsync(
                    runKey, requestId, dispatchResult.ExternalOperationId ?? externalOperationId, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // MarkDispatched 失败（如状态已被并发推进）→ 继续处理，journal 状态可能不一致
                // 但 dispatch 已完成，结果有效；返回时 JournalState 反映实际查询结果
            }
        }

        // 7. 执行后策略处置：由 Tool Policy Engine 决定提交 / 对账 / 拒绝
        // 声明权威：Descriptor 明确声明副作用类型（非 Unknown）时以声明为准，
        // 运行时结果仅用于验证（不一致时由调用方审计）；未声明（null/Unknown）时以运行时结果为准。
        var effectiveSideEffect = descriptor is { DeclaredSideEffect: not ToolSideEffect.Unknown }
            ? descriptor.DeclaredSideEffect
            : dispatchResult.SideEffect;
        var effectiveExternalOperationId = dispatchResult.ExternalOperationId ?? externalOperationId;

        // 构造临时执行结果供策略引擎解析（JournalState 暂按 Dispatched——尚未提交）。
        var policyResult = new ToolExecutionResult
        {
            RequestId = requestId,
            IdempotencyKey = effectiveIdempotencyKey,
            SideEffect = effectiveSideEffect,
            ExternalOperationId = effectiveExternalOperationId,
            JournalState = ToolDispatchState.Dispatched,
            Result = dispatchResult.Result,
            Succeeded = dispatchResult.Succeeded,
            Error = dispatchResult.Error,
            ErrorKind = dispatchResult.ErrorKind,
            FailurePhase = dispatchResult.Succeeded ? null : ToolFailurePhase.ProviderReturned,
            NoEffectConfirmed = dispatchResult.NoEffectConfirmed,
            Duration = stopwatch.Elapsed
        };
        var policy = _effectPolicy.Resolve(
            descriptor ?? new ToolDescriptor
            {
                // Dispatcher 未提供前置声明（如 EchoToolDispatcher）→ 以运行时观测副作用合成描述符，
                // 策略按观测值保守处置（Unknown → 不自动提交）。
                Name = toolCall.ToolName,
                DeclaredSideEffect = effectiveSideEffect
            },
            prepareResult, policyResult, finalAttempt, approvalGranted);

        ToolDispatchState finalJournalState;
        // Journal 提交失败标记——MarkCommittedWithResultAsync CAS 失败时置位，
        // 最终结果携带 FailurePhase=JournalCommitFailed（外部副作用已发生但 journal 未达 Committed，必须对账）。
        var journalCommitFailed = false;
        switch (policy.Disposition)
        {
            case ToolExecutionDisposition.Commit:
                // 结果确定且策略允许 → 原子提交 state + result。
                // 构造 DurableToolResult，用 MarkCommittedWithResultAsync 原子提交 state + result，
                // 填充 WorkspaceId / RunId / InvocationId，写入 tool_dispatch_results 的隔离键列，
                // 配合 UNIQUE(workspace_id, run_id, invocation_id) 约束防止另一 Run 覆盖已有 Tool Result。
                // InvocationId 取 requestId（代码层 RequestId 即稳定调用身份 InvocationId）。
                var durableResult = new DurableToolResult
                {
                    ToolCallId = toolCallId,
                    RequestId = requestId,
                    WorkspaceId = workspaceId,
                    RunId = runId,
                    InvocationId = requestId,
                    IdempotencyKey = effectiveIdempotencyKey,
                    SideEffect = effectiveSideEffect,
                    ExternalOperationId = effectiveExternalOperationId,
                    Result = dispatchResult.Result,
                    Succeeded = dispatchResult.Succeeded,
                    Error = dispatchResult.Error,
                    DurationMs = stopwatch.Elapsed.TotalMilliseconds
                };

                if (_dispatchJournal is not null)
                {
                    try
                    {
                        await _dispatchJournal.MarkCommittedWithResultAsync(
                            runKey, requestId, durableResult, cancellationToken).ConfigureAwait(false);
                        finalJournalState = ToolDispatchState.Committed;
                    }
                    catch (InvalidOperationException)
                    {
                        // MarkCommitted 失败 → 查询实际状态（外部副作用已发生，journal 未达 Committed → 对账）
                        journalCommitFailed = true;
                        var entry = await _dispatchJournal.GetEntryAsync(runKey, requestId, cancellationToken).ConfigureAwait(false);
                        finalJournalState = entry?.State ?? ToolDispatchState.Dispatched;
                    }
                }
                else
                {
                    finalJournalState = ToolDispatchState.Committed;
                }

                // 写入 IDurableToolResultStore（仅当 journal 不在同事务内持久化结果时）。
                // Postgres journal 的 MarkCommittedWithResultAsync 已在同事务内 UPSERT 结果到 tool_dispatch_results，
                // 此处冗余写入可跳过；InMemory journal 自带缓存但 PersistsResults=false，仍需走 resultStore（若注入）。
                // 无 journal 路径（_dispatchJournal=null）也走 resultStore（若注入）。
                // 使用按 request_id 的新路径（复合主键），写入全部隔离键列。
                var journalPersistsResults = _dispatchJournal?.PersistsResults ?? false;
                if (!journalPersistsResults && _resultStore is not null)
                {
                    try
                    {
                        await _resultStore.SaveByRequestIdAsync(runKey, durableResult, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 结果缓存写入失败不阻断主流程（journal 已 Committed，结果已持久化在 dispatchResult 中）
                    }
                }

                // 投递模式决策：AsyncDurable → Commit 后显式推进 ResultDelivered（结果已送达事件流）。
                // Synchronous → journal 停留在 Committed，由调用方完成后续投递语义。
                if (policy.DeliveryMode == ToolDeliveryMode.AsyncDurable && _dispatchJournal is not null)
                {
                    try
                    {
                        await _dispatchJournal.MarkResultDeliveredAsync(runKey, requestId, cancellationToken).ConfigureAwait(false);
                        finalJournalState = ToolDispatchState.ResultDelivered;
                    }
                    catch (InvalidOperationException)
                    {
                        // MarkResultDelivered 失败（如状态已被并发推进）→ 查询实际状态
                        var entry = await _dispatchJournal.GetEntryAsync(runKey, requestId, cancellationToken).ConfigureAwait(false);
                        finalJournalState = entry?.State ?? ToolDispatchState.Committed;
                    }
                }
                break;

            case ToolExecutionDisposition.HoldForReconciliation:
                // 策略要求不自动提交：journal 保持 Dispatched（模糊状态），等待对账。
                // 外部副作用可能已发生但未提交，调用方需经对账（Reconciliation）确认后提交。
                finalJournalState = ToolDispatchState.Dispatched;
                break;

            case ToolExecutionDisposition.FailClosed:
            default:
                // 策略禁止 → fail-closed：返回失败，不提交。
                // journal 停留在 Dispatched；若外部副作用已发生则由调用方对账裁决。
                stopwatch.Stop();
                return BuildFailedResult(
                    requestId, effectiveIdempotencyKey, effectiveSideEffect,
                    error: $"Tool 执行策略拒绝（fail-closed）：{policy.Reason}",
                    journalState: ToolDispatchState.Dispatched,
                    duration: stopwatch.Elapsed,
                    reconciliationHandler: descriptor?.ReconciliationHandler,
                    reconciliationDeadline: descriptor?.ReconciliationDeadline,
                    errorKind: DispatchErrorKind.PolicyRejected);
        }

        stopwatch.Stop();

        // 模型不得看到成功 Tool 结果，直到内部真相已 Commit 或对账完成。
        // Journal 处于模糊态（DispatchingIntent/Dispatched/Reconciling）时，即使 Provider
        // 调用本身成功（如非幂等写等待对账、MarkDispatched/MarkCommitted 失败），
        // 也强制 Succeeded=false 并附待对账错误信息——Actor 据此构建失败观察并创建对账记录；
        // 真相经对账提交后，重放路径返回提交后的结果。
        var journalIsAmbiguous = finalJournalState is ToolDispatchState.DispatchingIntent
            or ToolDispatchState.Dispatched
            or ToolDispatchState.Reconciling;
        var succeeded = dispatchResult.Succeeded && !journalIsAmbiguous;
        var resultError = dispatchResult.Error;
        if (!succeeded && resultError is null)
        {
            resultError = journalIsAmbiguous
                ? $"Tool 执行结果待对账：Journal 处于 {finalJournalState}，内部真相尚未提交（外部副作用可能已发生）。"
                : dispatchResult.Error;
        }

        return new ToolExecutionResult
        {
            RequestId = requestId,
            IdempotencyKey = effectiveIdempotencyKey,
            SideEffect = effectiveSideEffect,
            ExternalOperationId = effectiveExternalOperationId,
            JournalState = finalJournalState,
            ReconciliationHandler = descriptor?.ReconciliationHandler,
            ReconciliationDeadline = descriptor?.ReconciliationDeadline,
            Result = dispatchResult.Result,
            Succeeded = succeeded,
            Error = resultError,
            ErrorKind = dispatchResult.ErrorKind,
            // 提交失败 → JournalCommitFailed（对账）；结果未达成功（含模糊态收敛）→ ProviderReturned。
            FailurePhase = journalCommitFailed
                ? ToolFailurePhase.JournalCommitFailed
                : (succeeded ? null : ToolFailurePhase.ProviderReturned),
            NoEffectConfirmed = dispatchResult.NoEffectConfirmed,
            Duration = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// 从缓存结果构建 ToolExecutionResult（Journal 已 Committed/ResultDelivered 时使用）。
    /// JournalState 保真：缓存来源为 ResultDelivered 时回传 ResultDelivered（结果已送达），
    /// 避免下游将已送达的结果误判为需对账。
    /// </summary>
    private static ToolExecutionResult BuildCachedResult(
        DurableToolResult cached,
        TimeSpan elapsed,
        ToolDispatchState journalState = ToolDispatchState.Committed)
    {
        return new ToolExecutionResult
        {
            RequestId = cached.RequestId,
            IdempotencyKey = cached.IdempotencyKey,
            SideEffect = cached.SideEffect,
            ExternalOperationId = cached.ExternalOperationId,
            JournalState = journalState,
            Result = cached.Result,
            Succeeded = cached.Succeeded,
            Error = cached.Error,
            Duration = elapsed
        };
    }

    /// <summary>
    /// 构建对账结果（Journal = DispatchingIntent/Dispatched 模糊状态，外部副作用可能已执行但未提交）。
    /// 调用方需查询外部系统或人工裁决后决定是否重新执行。
    /// JournalState 必须反映真实状态（调用方据此决定是否创建对账记录）。
    /// </summary>
    private static ToolExecutionResult BuildReconciliationResult(
        string requestId,
        string? idempotencyKey,
        string? externalOperationId,
        TimeSpan elapsed,
        ToolDispatchState journalState = ToolDispatchState.Dispatched,
        string? reconciliationHandler = null,
        TimeSpan? reconciliationDeadline = null)
    {
        return new ToolExecutionResult
        {
            RequestId = requestId,
            IdempotencyKey = idempotencyKey,
            SideEffect = ToolSideEffect.Unknown, // 模糊状态视为 Unknown
            ExternalOperationId = externalOperationId,
            JournalState = journalState,
            ReconciliationHandler = reconciliationHandler,
            ReconciliationDeadline = reconciliationDeadline,
            Result = null,
            Succeeded = false,
            FailurePhase = ToolFailurePhase.ProviderCallAmbiguous,
            Error = $"Tool dispatch 处于 {journalState} 模糊状态（外部副作用可能已执行但未提交）。" +
                     $"ExternalOperationId={externalOperationId ?? "<null>"}，需对账后决定是否重新执行。",
            Duration = elapsed
        };
    }

    /// <summary>
    /// 基于 workspaceId + runId + modelTurn + toolCallId + toolName + arguments 生成稳定 RequestId（SHA-256 截断）。
    /// </summary>
    /// <remarks>
    /// RequestId 唯一标识一次具体调用（InvocationId），确保同一 Run 内不同轮次（modelTurn）
    /// 或不同 toolCallId 的相同 Tool 调用产生不同 RequestId，避免误将第二次调用作为重复去重。
    /// workspaceId 参与哈希：跨工作区可复用相同 RunId 与相同 Tool 调用而不产生相同 RequestId，
    /// 与 journal/结果表的 (workspace_id, run_id, request_id) 复合键对齐。
    /// 崩溃恢复时同一 (workspaceId, runId, modelTurn, toolCallId, toolName, arguments) 产出相同
    /// RequestId，确保可重放。
    /// <b>IdempotencyKey 不参与哈希</b>——它是业务级去重键，单独存储于 journal 条目，
    /// 不影响调用身份；业务级 IdempotencyKey 去重为未来增强。
    /// </remarks>
    internal static string ComputeRequestId(string workspaceId, string runId, AgentToolCallRequest toolCall, int modelTurn)
    {
        var raw = $"{workspaceId}|{runId}|{modelTurn}|{toolCall.ToolCallId ?? string.Empty}|{toolCall.ToolName}|{toolCall.Arguments ?? string.Empty}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        // 取前 16 字节（128 位）作为 hex 字符串（32 字符）
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>构建失败结果。</summary>
    private static ToolExecutionResult BuildFailedResult(
        string requestId,
        string? idempotencyKey,
        ToolSideEffect sideEffect,
        string error,
        ToolDispatchState journalState,
        TimeSpan duration,
        string? reconciliationHandler = null,
        TimeSpan? reconciliationDeadline = null,
        DispatchErrorKind errorKind = DispatchErrorKind.Unknown,
        ToolFailurePhase? failurePhase = null,
        string? externalOperationId = null)
    {
        return new ToolExecutionResult
        {
            RequestId = requestId,
            IdempotencyKey = idempotencyKey,
            SideEffect = sideEffect,
            ExternalOperationId = externalOperationId,
            JournalState = journalState,
            ReconciliationHandler = reconciliationHandler,
            ReconciliationDeadline = reconciliationDeadline,
            Result = null,
            Succeeded = false,
            Error = error,
            ErrorKind = errorKind,
            FailurePhase = failurePhase,
            Duration = duration
        };
    }

    /// <summary>
    /// 查询 Journal 真实状态：Intent 持久化后的失败路径必须返回数据库真实状态，不得伪造 Prepared。
    /// 查询失败或条目缺失 → fail-closed：按最高安全级别返回 <see cref="ToolDispatchState.DispatchingIntent"/>
    /// （强制对账，避免外部副作用真相悬空），并保留在错误信息中供审计。
    /// 无 journal（降级直连）→ 返回 Prepared（无 durable 边界，视为从未开始）。
    /// </summary>
    private async ValueTask<ToolDispatchState> QueryJournalStateAsync(
        string workspaceId, string runId, string requestId, CancellationToken cancellationToken)
    {
        if (_dispatchJournal is null)
        {
            return ToolDispatchState.Prepared;
        }

        try
        {
            var state = await _dispatchJournal.GetStateAsync(
                new TenantRunKey(workspaceId, runId), requestId, cancellationToken).ConfigureAwait(false);
            if (state is not null)
            {
                return state.Value;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 查询失败 → fail-closed：DispatchingIntent（强制对账），绝不伪造 Prepared。
        }

        return ToolDispatchState.DispatchingIntent;
    }
}
