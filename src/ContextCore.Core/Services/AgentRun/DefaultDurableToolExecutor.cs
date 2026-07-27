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
//   3. Journal.PrepareAsync（若注入 journal）
//   4. IToolDispatcher.DispatchAsync（携带 RequestId + IdempotencyKey）
//   5. Journal.MarkDispatchedAsync（若注入 journal）
//   6. 副作用分类：
//      - None/ReadOnly/Write → Journal.MarkCommittedAsync（自动提交）
//      - Unknown → 不提交（等待调用方裁决，返回 JournalState=Dispatched）
//   7. 返回 ToolExecutionResult（含完整 Tool 身份信息）
//
// 设计决策：
//   - RequestId 由本执行器生成（稳定哈希），确保 Actor 与 Dispatcher 共享同一 ID。
//   - Journal / Outbox 为可选依赖（null 时降级为直接 dispatch，无 durable 保证）。
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
/// </remarks>
public sealed class DefaultDurableToolExecutor : IDurableToolExecutor
{
    private readonly IToolDispatcher _toolDispatcher;
    private readonly IToolDispatchJournal? _dispatchJournal;

    /// <summary>
    /// 构造 Durable Tool 执行器。
    /// </summary>
    /// <param name="toolDispatcher">Tool 分派器（必需）。</param>
    /// <param name="dispatchJournal">Tool 分派 journal（可选；null 时无 durable 保证）。</param>
    public DefaultDurableToolExecutor(
        IToolDispatcher toolDispatcher,
        IToolDispatchJournal? dispatchJournal = null)
    {
        _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
        _dispatchJournal = dispatchJournal;
    }

    /// <inheritdoc />
    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        string runId,
        string workspaceId,
        AgentToolCallRequest toolCall,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(toolCall);

        // 1. 生成稳定 RequestId（基于 runId + toolCall 哈希）
        var requestId = ComputeRequestId(runId, toolCall);
        var idempotencyKey = toolCall.IdempotencyKey;

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

        // 4. Journal.PrepareAsync（若注入 journal）
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
                await _dispatchJournal.PrepareAsync(entry, cancellationToken).ConfigureAwait(false);
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
        }

        // 5. Dispatch（携带 RequestId）
        ToolDispatchResult dispatchResult;
        try
        {
            dispatchResult = await _toolDispatcher.DispatchAsync(new ToolDispatchRequest
            {
                ToolName = toolCall.ToolName,
                Payload = toolCall.Arguments,
                RequestId = requestId
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
            // 自动提交 → Journal.MarkCommittedAsync
            if (_dispatchJournal is not null)
            {
                try
                {
                    await _dispatchJournal.MarkCommittedAsync(requestId, cancellationToken).ConfigureAwait(false);
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
    /// 基于 runId + toolCall 生成稳定 RequestId（SHA-256 截断）。
    /// 同一 runId + 同一 toolCall 产出相同 RequestId，确保崩溃恢复时可重放。
    /// </summary>
    internal static string ComputeRequestId(string runId, AgentToolCallRequest toolCall)
    {
        var raw = $"{runId}|{toolCall.ToolName}|{toolCall.Arguments ?? string.Empty}|{toolCall.IdempotencyKey ?? string.Empty}";
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
