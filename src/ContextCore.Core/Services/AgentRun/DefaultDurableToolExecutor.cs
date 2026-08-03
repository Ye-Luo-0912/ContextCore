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
//   1. 生成稳定 RequestId（基于 runId + toolCall 哈希，确保可重放时一致）
//      + 框架生成 ExternalOperationId（外部操作 ID，Prepare 时落库，重放稳定）
//   2. 校验 ToolName 非空 + Dispatcher 支持 + 读取 ToolDescriptor 前置声明
//   3. Journal.PrepareWithIntentAsync（若注入 journal）→ 单次原子写完成 Prepare + 前置 Intent
//      （合并两次写为一次），根据结果决策：
//      a. CachedResult 非空（Journal = Committed/ResultDelivered）→ 直接返回缓存结果，禁止重新 Dispatch
//      b. NeedsReconciliation=true（Journal = DispatchingIntent/Dispatched）→ 返回对账结果（携带 ExternalOperationId），不重新 Dispatch
//      c. ShouldDispatch=true（本次新插入或既有 Prepared 已推进，journal 已处于 DispatchingIntent）→ 继续 Dispatch
//   4. 声明校验（fail-closed）：RequiresLeaseFence 但无 fence → 拒绝；RequiresIdempotencyKey
//      但无键 → 以 ExternalOperationId 兜底为幂等键
//   5. IToolDispatcher.DispatchAsync（携带 RequestId + IdempotencyKey + ExternalOperationId）
//   6. Journal.MarkDispatchedAsync（若注入 journal）
//   7. 副作用分类（声明优先，运行时结果仅验证）：
//      - None/ReadOnly/Write 等（非 Unknown）→ Journal.MarkCommittedWithResultAsync（同事务持久化 state + result）
//        并写入 IDurableToolResultStore（若注入，供 Postgres 路径缓存查询）
//      - Unknown → 不提交（等待调用方裁决，返回 JournalState=Dispatched）
//   8. 返回 ToolExecutionResult（含完整 Tool 身份信息）
//
// 修复要点：
//   - PrepareAsync 返回值决定是否 Dispatch，避免对 Committed/ResultDelivered 重复 Dispatch
//   - Dispatched 模糊状态返回对账结果，不盲目重新执行外部副作用
//   - MarkCommittedWithResultAsync 原子提交 state + result，确保崩溃恢复时缓存可读
//   - IDurableToolResultStore 作为 Postgres 路径的结果缓存（InMemory journal 自带缓存）
//   - ToolDescriptor 前置副作用声明参与执行前决策与提交分类（声明权威，运行时验证）
//
// 设计决策：
//   - RequestId 由本执行器生成（稳定哈希），确保 Actor 与 Dispatcher 共享同一 ID。
//   - ExternalOperationId 由本执行器从稳定 RequestId 派生（"cc:" + requestId），
//     Prepare 时随 journal 条目持久化；Handler 返回真实外部系统 ID 时在 MarkDispatchedAsync 覆盖。
//   - Journal / Outbox / ResultStore 为可选依赖（null 时降级为直接 dispatch，无 durable 保证）。
//   - 不处理审批（审批由 Actor 在调用本执行器前通过 IAgentApprovalGate 完成）。
//   - 异常时返回 Succeeded=false 的结果（不抛异常，让 Actor 决定如何处理）。
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
        DateTimeOffset? deadlineAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(toolCall);

        // 生成稳定 RequestId（基于 runId + modelTurn + toolCallId + toolName + arguments 哈希）。
        // IdempotencyKey 不参与 RequestId 计算——它是业务级去重键，单独存储于 journal 条目，
        // 不应影响调用身份（InvocationId）。这避免同一 Run 内不同轮次的相同 Tool 调用被误判为重复。
        var requestId = ComputeRequestId(runId, toolCall, modelTurn);
        var idempotencyKey = toolCall.IdempotencyKey;
        // toolCallId 优先使用模型分配的 ToolCallId，缺失时回退到 RequestId
        var toolCallId = !string.IsNullOrWhiteSpace(toolCall.ToolCallId) ? toolCall.ToolCallId! : requestId;

        // 外部操作 ID 从稳定 RequestId 派生（"cc:" + requestId），不使用 GUID。
        // 崩溃恢复时同一 (runId, modelTurn, toolCallId, toolName, arguments) 产出相同 RequestId，
        // 派生值即恢复值；Journal 条目持久化的也是该派生值，外部 Provider 幂等记录恢复后可命中。
        // 若 Handler 在 Dispatch 后返回真实外部系统 ID，MarkDispatchedAsync 时以真实 ID 覆盖。
        var externalOperationId = "cc:" + requestId;

        // 读取 Tool 前置声明（副作用 / 审批 / 幂等 / fence / 恢复策略），
        // 用于 Dispatch 前的 fail-closed 校验与提交分类（声明权威，运行时结果仅验证）。
        var descriptor = _toolDispatcher.GetDescriptor(toolCall.ToolName);

        // 幂等键兜底：声明要求幂等键但调用方未提供 → 从稳定 RequestId 派生
        // （providerNamespace + ":" + requestId，providerNamespace 取 ToolName 命名空间）。
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
                duration: stopwatch.Elapsed);
        }

        // 3. 校验 Dispatcher 支持
        if (!_toolDispatcher.SupportedTools.Contains(toolCall.ToolName))
        {
            return BuildFailedResult(
                requestId, idempotencyKey, ToolSideEffect.Unknown,
                error: $"不支持的 tool: {toolCall.ToolName}",
                journalState: ToolDispatchState.Prepared,
                duration: stopwatch.Elapsed);
        }

        // 4. Journal.PrepareWithIntentAsync（若注入 journal）→ 单次原子写完成 Prepare + 前置 Intent
        //    （合并 PrepareAsync + MarkDispatchingIntentAsync 两次往返为一次，且 durable 边界
        //      与条目创建原子化——RecoveryDecision=Dispatch 时 journal 必已处于 DispatchingIntent）。
        //    根据 ToolDispatchPrepareResult 决策：
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
                // PrepareWithIntentAsync 失败（如 RequestId 复用检测）→ 返回失败
                return BuildFailedResult(
                    requestId, effectiveIdempotencyKey, ToolSideEffect.Unknown,
                    error: $"Journal PrepareWithIntentAsync 失败：{ex.Message}",
                    journalState: ToolDispatchState.Prepared,
                    duration: stopwatch.Elapsed);
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
                    return BuildCachedResult(prepareResult.CachedResult, stopwatch.Elapsed);

                // 4c. Journal = Committed/ResultDelivered 但 journal 不缓存结果（Postgres 路径）
                //     查询 IDurableToolResultStore；无 resultStore 或缓存未命中 → 返回对账结果
                case ToolDispatchRecoveryDecision.UseCachedResult:
                    if (_resultStore is not null)
                    {
                        DurableToolResult? cached = null;
                        try
                        {
                            cached = await _resultStore.GetByRequestIdAsync(requestId, cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            // ResultStore 查询失败不阻断流程；降级为对账结果
                        }
                        if (cached is not null)
                        {
                            stopwatch.Stop();
                            return BuildCachedResult(cached, stopwatch.Elapsed);
                        }
                    }

                    // 无 resultStore 或缓存未命中，但 journal 已 Committed/ResultDelivered → 模糊状态，返回对账结果
                    // 不重新 Dispatch（journal 明确指示 UseCachedResult，重新执行会违反 exactly-once）
                    stopwatch.Stop();
                    return BuildReconciliationResult(
                        requestId, effectiveIdempotencyKey, externalOperationId, stopwatch.Elapsed,
                        reconciliationHandler: descriptor?.ReconciliationHandler);

                // 4b. Journal = DispatchingIntent/Dispatched/Reconciling 模糊态 → 返回对账结果，不重新 Dispatch。
                //     外部副作用可能已执行但未提交，调用方需经 BeginReconciliationAsync 显式对账或人工裁决。
                case ToolDispatchRecoveryDecision.Reconcile:
                    stopwatch.Stop();
                    return BuildReconciliationResult(
                        requestId, effectiveIdempotencyKey, externalOperationId, stopwatch.Elapsed,
                        reconciliationHandler: descriptor?.ReconciliationHandler);

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
        ToolDispatchResult dispatchResult;
        try
        {
            // 对写副作用 Tool 做 lease fence 校验——lease 已失效时 fail-closed，
            // 阻止旧 Owner 在租约过期后执行外部写操作。
            // ReadOnly/None 可跳过校验（无副作用，重放安全）。
            // NonIdempotentWrite 在 ProductionHA 下无外部幂等或 fencing 支持时必须 fail-closed。
            if (leaseFence is not null && DateTimeOffset.UtcNow >= leaseFence.ExpiresAt)
            {
                return BuildFailedResult(
                    requestId, effectiveIdempotencyKey, ToolSideEffect.Unknown,
                    error: $"Lease 已过期（ExpiresAt={leaseFence.ExpiresAt:O}），Tool 执行被 fence 阻止。",
                    journalState: ToolDispatchState.Prepared,
                    duration: stopwatch.Elapsed);
            }

            // 声明要求 lease fence 但调用方未提供 → fail-closed（副作用 Tool 无 fencing 保护时禁止执行）
            if (descriptor is { RequiresLeaseFence: true } && leaseFence is null)
            {
                return BuildFailedResult(
                    requestId, effectiveIdempotencyKey, ToolSideEffect.Unknown,
                    error: $"Tool '{toolCall.ToolName}' 声明 RequiresLeaseFence，但调用未携带 LeaseFence，执行被拒绝（fail-closed）。",
                    journalState: ToolDispatchState.Prepared,
                    duration: stopwatch.Elapsed);
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
            // Dispatch 异常 → 返回失败（journal 仍停留在 Prepared 状态）
            return BuildFailedResult(
                requestId, effectiveIdempotencyKey, ToolSideEffect.Unknown,
                error: $"Dispatch 异常：{ex.Message}",
                journalState: ToolDispatchState.Prepared,
                duration: stopwatch.Elapsed);
        }

        // 6. Journal.MarkDispatchedAsync（若注入 journal）
        //    外部操作 ID：Handler 返回真实外部系统 ID 时优先，否则保留框架生成值。
        if (_dispatchJournal is not null)
        {
            try
            {
                await _dispatchJournal.MarkDispatchedAsync(
                    requestId, dispatchResult.ExternalOperationId ?? externalOperationId, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // MarkDispatched 失败（如状态已被并发推进）→ 继续处理，journal 状态可能不一致
                // 但 dispatch 已完成，结果有效；返回时 JournalState 反映实际查询结果
            }
        }

        // 7. 执行后策略处置：由 Tool Policy Engine 决定提交 / 对账 / 拒绝
        //    声明权威：Descriptor 明确声明副作用类型（非 Unknown）时以声明为准，
        //    运行时结果仅用于验证（不一致时由调用方审计）；未声明（null/Unknown）时以运行时结果为准。
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
            prepareResult, policyResult);

        ToolDispatchState finalJournalState;
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
                            requestId, durableResult, cancellationToken).ConfigureAwait(false);
                        finalJournalState = ToolDispatchState.Committed;
                    }
                    catch (InvalidOperationException)
                    {
                        // MarkCommitted 失败 → 查询实际状态
                        var entry = await _dispatchJournal.GetEntryAsync(requestId, cancellationToken).ConfigureAwait(false);
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
                // 使用按 request_id 的新路径（Result 主键），写入全部隔离键列。
                var journalPersistsResults = _dispatchJournal?.PersistsResults ?? false;
                if (!journalPersistsResults && _resultStore is not null)
                {
                    try
                    {
                        await _resultStore.SaveByRequestIdAsync(durableResult, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 结果缓存写入失败不阻断主流程（journal 已 Committed，结果已持久化在 dispatchResult 中）
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
                    reconciliationHandler: descriptor?.ReconciliationHandler);
        }

        stopwatch.Stop();

        return new ToolExecutionResult
        {
            RequestId = requestId,
            IdempotencyKey = effectiveIdempotencyKey,
            SideEffect = effectiveSideEffect,
            ExternalOperationId = effectiveExternalOperationId,
            JournalState = finalJournalState,
            ReconciliationHandler = descriptor?.ReconciliationHandler,
            Result = dispatchResult.Result,
            Succeeded = dispatchResult.Succeeded,
            Error = dispatchResult.Error,
            Duration = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// 从缓存结果构建 ToolExecutionResult（Journal 已 Committed/ResultDelivered 时使用）。
    /// </summary>
    private static ToolExecutionResult BuildCachedResult(DurableToolResult cached, TimeSpan elapsed)
    {
        return new ToolExecutionResult
        {
            RequestId = cached.RequestId,
            IdempotencyKey = cached.IdempotencyKey,
            SideEffect = cached.SideEffect,
            ExternalOperationId = cached.ExternalOperationId,
            JournalState = ToolDispatchState.Committed,
            Result = cached.Result,
            Succeeded = cached.Succeeded,
            Error = cached.Error,
            Duration = elapsed
        };
    }

    /// <summary>
    /// 构建对账结果（Journal = Dispatched 模糊状态，外部副作用可能已执行但未提交）。
    /// 调用方需查询外部系统或人工裁决后决定是否重新执行。
    /// </summary>
    private static ToolExecutionResult BuildReconciliationResult(
        string requestId,
        string? idempotencyKey,
        string? externalOperationId,
        TimeSpan elapsed,
        string? reconciliationHandler = null)
    {
        return new ToolExecutionResult
        {
            RequestId = requestId,
            IdempotencyKey = idempotencyKey,
            SideEffect = ToolSideEffect.Unknown, // 模糊状态视为 Unknown
            ExternalOperationId = externalOperationId,
            JournalState = ToolDispatchState.Dispatched,
            ReconciliationHandler = reconciliationHandler,
            Result = null,
            Succeeded = false,
            Error = $"Tool dispatch 处于 Dispatched 模糊状态（外部副作用可能已执行但未提交）。" +
                     $"ExternalOperationId={externalOperationId ?? "<null>"}，需对账后决定是否重新执行。",
            Duration = elapsed
        };
    }

    /// <summary>
    /// 基于 runId + modelTurn + toolCallId + toolName + arguments 生成稳定 RequestId（SHA-256 截断）。
    /// </summary>
    /// <remarks>
    /// RequestId 唯一标识一次具体调用（InvocationId），确保同一 Run 内不同轮次（modelTurn）
    /// 或不同 toolCallId 的相同 Tool 调用产生不同 RequestId，避免误将第二次调用作为重复去重。
    /// 崩溃恢复时同一 (runId, modelTurn, toolCallId, toolName, arguments) 产出相同 RequestId，确保可重放。
    /// <b>IdempotencyKey 不参与哈希</b>——它是业务级去重键，单独存储于 journal 条目，
    /// 不影响调用身份；业务级 IdempotencyKey 去重为未来增强。
    /// </remarks>
    internal static string ComputeRequestId(string runId, AgentToolCallRequest toolCall, int modelTurn)
    {
        var raw = $"{runId}|{modelTurn}|{toolCall.ToolCallId ?? string.Empty}|{toolCall.ToolName}|{toolCall.Arguments ?? string.Empty}";
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
        string? reconciliationHandler = null)
    {
        return new ToolExecutionResult
        {
            RequestId = requestId,
            IdempotencyKey = idempotencyKey,
            SideEffect = sideEffect,
            ExternalOperationId = null,
            JournalState = journalState,
            ReconciliationHandler = reconciliationHandler,
            Result = null,
            Succeeded = false,
            Error = error,
            Duration = duration
        };
    }
}
