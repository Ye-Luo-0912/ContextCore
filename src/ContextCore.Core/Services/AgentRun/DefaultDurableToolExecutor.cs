using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 子问题 5：DefaultDurableToolExecutor — Durable Tool 执行器默认实现
//
// 封装 Tool 调用的完整 durable 流程，复用旧 Kernel（DefaultAgentKernel.ProcessExecuteAsync）
// 的 Tool 处理逻辑，让 AgentRunActor 不再直接调用 IToolDispatcher。
//
// 流程：
//   1. 生成稳定 RequestId（基于 runId + toolCall 哈希，确保可重放时一致）
//   2. 校验 ToolName 非空 + Dispatcher 支持
//   3. Journal.PrepareAsync（若注入 journal）→ 根据 ToolDispatchPrepareResult 决策：
//      a. CachedResult 非空（Journal = Committed/ResultDelivered）→ 直接返回缓存结果，禁止重新 Dispatch
//      b. NeedsReconciliation=true（Journal = Dispatched）→ 返回对账结果（携带 ExternalOperationId），不重新 Dispatch
//      c. ShouldDispatch=true（Journal 不存在或 Prepared）→ 继续 Dispatch
//   4. IToolDispatcher.DispatchAsync（携带 RequestId + IdempotencyKey）
//   5. Journal.MarkDispatchedAsync（若注入 journal）
//   6. 副作用分类：
//      - None/ReadOnly/Write → Journal.MarkCommittedWithResultAsync（同事务持久化 state + result）
//        并写入 IDurableToolResultStore（若注入，供 Postgres 路径缓存查询）
//      - Unknown → 不提交（等待调用方裁决，返回 JournalState=Dispatched）
//   7. 返回 ToolExecutionResult（含完整 Tool 身份信息）
//
// P0-3 修复要点：
//   - PrepareAsync 返回值决定是否 Dispatch，避免对 Committed/ResultDelivered 重复 Dispatch
//   - Dispatched 模糊状态返回对账结果，不盲目重新执行外部副作用
//   - MarkCommittedWithResultAsync 原子提交 state + result，确保崩溃恢复时缓存可读
//   - IDurableToolResultStore 作为 Postgres 路径的结果缓存（InMemory journal 自带缓存）
//
// 设计决策：
//   - RequestId 由本执行器生成（稳定哈希），确保 Actor 与 Dispatcher 共享同一 ID。
//   - Journal / Outbox / ResultStore 为可选依赖（null 时降级为直接 dispatch，无 durable 保证）。
//   - 不处理审批（审批由 Actor 在调用本执行器前通过 IAgentApprovalGate 完成）。
//   - 异常时返回 Succeeded=false 的结果（不抛异常，让 Actor 决定如何处理）。
// ===========================================================================

/// <summary>
/// 子问题 5：Durable Tool 执行器默认实现。
/// 封装 Tool 调用的完整 durable 流程（journal + dispatch + commit）。
/// </summary>
/// <remarks>
/// 复用旧 Kernel（<see cref="DefaultAgentKernel"/>）的 Tool 处理逻辑，
/// 让 <see cref="AgentRunActor"/> 不再直接调用 <see cref="IToolDispatcher"/>。
///
/// <b>P0-3</b>：根据 <see cref="IToolDispatchJournal.PrepareAsync"/> 返回的
/// <see cref="ToolDispatchPrepareResult"/> 决策是否 Dispatch、对账或返回缓存结果，
/// 防止对已 Committed/ResultDelivered 的 Tool 重复执行外部副作用。
/// </remarks>
public sealed class DefaultDurableToolExecutor : IDurableToolExecutor
{
    private readonly IToolDispatcher _toolDispatcher;
    private readonly IToolDispatchJournal? _dispatchJournal;
    private readonly IDurableToolResultStore? _resultStore;

    /// <summary>
    /// 构造 Durable Tool 执行器。
    /// </summary>
    /// <param name="toolDispatcher">Tool 分派器（必需）。</param>
    /// <param name="dispatchJournal">Tool 分派 journal（可选；null 时无 durable 保证）。</param>
    /// <param name="resultStore">
    /// P0-3：Durable Tool 结果缓存存储（可选）。
    /// 注入后在 Committed 时缓存结果，供后续 PrepareAsync 查询（Postgres journal 不自带缓存）。
    /// null 时仅依赖 journal 内置缓存（InMemory journal 自带；Postgres journal 不缓存）。
    /// </param>
    public DefaultDurableToolExecutor(
        IToolDispatcher toolDispatcher,
        IToolDispatchJournal? dispatchJournal = null,
        IDurableToolResultStore? resultStore = null)
    {
        _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        _dispatchJournal = dispatchJournal;
        _resultStore = resultStore;
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

        // P0-6：生成稳定 RequestId（基于 runId + modelTurn + toolCallId + toolName + arguments 哈希）。
        // IdempotencyKey 不再参与 RequestId 计算——它是业务级去重键，单独存储于 journal 条目，
        // 不应影响调用身份（InvocationId）。这避免同一 Run 内不同轮次的相同 Tool 调用被误判为重复。
        var requestId = ComputeRequestId(runId, toolCall, modelTurn);
        var idempotencyKey = toolCall.IdempotencyKey;
        // P0-3：toolCallId 优先使用模型分配的 ToolCallId，缺失时回退到 RequestId（作为结果缓存主键）
        var toolCallId = !string.IsNullOrWhiteSpace(toolCall.ToolCallId) ? toolCall.ToolCallId! : requestId;

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

        // 4. Journal.PrepareAsync（若注入 journal）→ 根据 ToolDispatchPrepareResult 决策
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
                    IdempotencyKey = idempotencyKey,
                    PayloadDigest = ToolDispatchJournalEntry.ComputePayloadDigest(toolCall.Arguments),
                    WorkspaceId = workspaceId,
                    RunId = runId,
                    UpdatedAt = startedAt
                };
                prepareResult = await _dispatchJournal.PrepareAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // PrepareAsync 失败（如 RequestId 复用检测）→ 返回失败
                return BuildFailedResult(
                    requestId, idempotencyKey, ToolSideEffect.Unknown,
                    error: $"Journal PrepareAsync 失败：{ex.Message}",
                    journalState: ToolDispatchState.Prepared,
                    duration: stopwatch.Elapsed);
            }

            // P0-3：根据 Prepare 结果决策是否 Dispatch
            // 4a. CachedResult 非空（Journal = Committed/ResultDelivered，InMemory 自带缓存）→ 直接返回缓存，禁止重新 Dispatch
            if (prepareResult.CachedResult is not null)
            {
                stopwatch.Stop();
                return BuildCachedResult(prepareResult.CachedResult, stopwatch.Elapsed);
            }

            // 4b. NeedsReconciliation=true（Journal = Dispatched）→ 返回对账结果，不重新 Dispatch
            //     外部副作用可能已执行但未提交，调用方需查询外部系统或人工裁决
            if (prepareResult.NeedsReconciliation)
            {
                stopwatch.Stop();
                return BuildReconciliationResult(
                    requestId, idempotencyKey, prepareResult.ExternalOperationId, stopwatch.Elapsed);
            }

            // 4c. ShouldDispatch=false（Postgres 路径：journal 已 Committed/ResultDelivered 但 journal 不缓存结果）
            //     查询 IDurableToolResultStore 获取缓存结果；无 resultStore 或缓存未命中 → 返回对账结果
            if (!prepareResult.ShouldDispatch)
            {
                if (_resultStore is not null)
                {
                    DurableToolResult? cached = null;
                    try
                    {
                        cached = await _resultStore.GetAsync(toolCallId, cancellationToken).ConfigureAwait(false);
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
                // 不重新 Dispatch（journal 明确指示 ShouldDispatch=false，重新执行会违反 exactly-once）
                stopwatch.Stop();
                return BuildReconciliationResult(
                    requestId, idempotencyKey, prepareResult.ExternalOperationId, stopwatch.Elapsed);
            }

            // 4d. ShouldDispatch=true（Journal 不存在或 Prepared）→ 继续 Dispatch
        }

        // 5. Dispatch（携带 RequestId + P0-4 执行上下文：WorkspaceId/RunId/IdempotencyKey）
        // P0-4：将 WorkspaceId/RunId/IdempotencyKey 透传到 ToolDispatchRequest，
        // 由 RealToolDispatcher 构造 ToolExecutionContext 传递给 IToolHandler。
        ToolDispatchResult dispatchResult;
        try
        {
            // P0-4：对写副作用 Tool 做 lease fence 校验——lease 已失效时 fail-closed，
            // 阻止旧 Owner 在租约过期后执行外部写操作。
            // ReadOnly/None 可跳过校验（无副作用，重放安全）。
            // NonIdempotentWrite 在 ProductionHA 下无外部幂等或 fencing 支持时必须 fail-closed。
            if (leaseFence is not null && DateTimeOffset.UtcNow >= leaseFence.ExpiresAt)
            {
                return BuildFailedResult(
                    requestId, idempotencyKey, ToolSideEffect.Unknown,
                    error: $"Lease 已过期（ExpiresAt={leaseFence.ExpiresAt:O}），Tool 执行被 fence 阻止。",
                    journalState: ToolDispatchState.Prepared,
                    duration: stopwatch.Elapsed);
            }

            dispatchResult = await _toolDispatcher.DispatchAsync(new ToolDispatchRequest
            {
                ToolName = toolCall.ToolName,
                Payload = toolCall.Arguments,
                RequestId = requestId,
                IdempotencyKey = idempotencyKey,
                WorkspaceId = workspaceId,
                RunId = runId,
                // P0-4：透传 leaseFence + deadlineAt 到 ToolDispatchRequest，
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
                requestId, idempotencyKey, ToolSideEffect.Unknown,
                error: $"Dispatch 异常：{ex.Message}",
                journalState: ToolDispatchState.Prepared,
                duration: stopwatch.Elapsed);
        }

        // 6. Journal.MarkDispatchedAsync（若注入 journal）
        if (_dispatchJournal is not null)
        {
            try
            {
                await _dispatchJournal.MarkDispatchedAsync(
                    requestId, dispatchResult.ExternalOperationId, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // MarkDispatched 失败（如状态已被并发推进）→ 继续处理，journal 状态可能不一致
                // 但 dispatch 已完成，结果有效；返回时 JournalState 反映实际查询结果
            }
        }

        // 7. 副作用分类决定是否自动提交
        ToolDispatchState finalJournalState;
        if (dispatchResult.SideEffect != ToolSideEffect.Unknown)
        {
            // P0-3：构造 DurableToolResult，用 MarkCommittedWithResultAsync 原子提交 state + result
            var durableResult = new DurableToolResult
            {
                ToolCallId = toolCallId,
                RequestId = requestId,
                IdempotencyKey = idempotencyKey,
                SideEffect = dispatchResult.SideEffect,
                ExternalOperationId = dispatchResult.ExternalOperationId,
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

            // P0-6：写入 IDurableToolResultStore（仅当 journal 不在同事务内持久化结果时）。
            // Postgres journal 的 MarkCommittedWithResultAsync 已在同事务内 UPSERT 结果到 tool_dispatch_results，
            // 此处冗余写入可跳过；InMemory journal 自带缓存但 PersistsResults=false，仍需走 resultStore（若注入）。
            // 无 journal 路径（_dispatchJournal=null）也走 resultStore（若注入）。
            var journalPersistsResults = _dispatchJournal?.PersistsResults ?? false;
            if (!journalPersistsResults && _resultStore is not null)
            {
                try
                {
                    await _resultStore.SaveAsync(toolCallId, durableResult, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // 结果缓存写入失败不阻断主流程（journal 已 Committed，结果已持久化在 dispatchResult 中）
                }
            }
        }
        else
        {
            // Unknown 副作用 → 不自动提交，停留在 Dispatched（模糊状态）
            finalJournalState = ToolDispatchState.Dispatched;
        }

        stopwatch.Stop();

        return new ToolExecutionResult
        {
            RequestId = requestId,
            IdempotencyKey = idempotencyKey,
            SideEffect = dispatchResult.SideEffect,
            ExternalOperationId = dispatchResult.ExternalOperationId,
            JournalState = finalJournalState,
            Result = dispatchResult.Result,
            Succeeded = dispatchResult.Succeeded,
            Error = dispatchResult.Error,
            Duration = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// P0-3：从缓存结果构建 ToolExecutionResult（Journal 已 Committed/ResultDelivered 时使用）。
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
    /// P0-3：构建对账结果（Journal = Dispatched 模糊状态，外部副作用可能已执行但未提交）。
    /// 调用方需查询外部系统或人工裁决后决定是否重新执行。
    /// </summary>
    private static ToolExecutionResult BuildReconciliationResult(
        string requestId,
        string? idempotencyKey,
        string? externalOperationId,
        TimeSpan elapsed)
    {
        return new ToolExecutionResult
        {
            RequestId = requestId,
            IdempotencyKey = idempotencyKey,
            SideEffect = ToolSideEffect.Unknown, // 模糊状态视为 Unknown
            ExternalOperationId = externalOperationId,
            JournalState = ToolDispatchState.Dispatched,
            Result = null,
            Succeeded = false,
            Error = $"Tool dispatch 处于 Dispatched 模糊状态（外部副作用可能已执行但未提交）。" +
                     $"ExternalOperationId={externalOperationId ?? "<null>"}，需对账后决定是否重新执行。",
            Duration = elapsed
        };
    }

    /// <summary>
    /// P0-6：基于 runId + modelTurn + toolCallId + toolName + arguments 生成稳定 RequestId（SHA-256 截断）。
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
        TimeSpan duration)
    {
        return new ToolExecutionResult
        {
            RequestId = requestId,
            IdempotencyKey = idempotencyKey,
            SideEffect = sideEffect,
            ExternalOperationId = null,
            JournalState = journalState,
            Result = null,
            Succeeded = false,
            Error = error,
            Duration = duration
        };
    }
}
