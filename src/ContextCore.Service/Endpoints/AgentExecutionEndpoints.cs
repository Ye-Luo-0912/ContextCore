using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using Microsoft.AspNetCore.Mvc;

namespace ContextCore.Service.Endpoints;

// ===========================================================================
// Agent Execution API — Agent Run 的 HTTP 入口
//
// 提供正式的 REST API 以驱动 Agent 执行循环：
//   POST   /api/agents/runs                       — 创建并启动 AgentRun
//   GET    /api/agents/runs/{id}                  — 获取 Run 状态
//   POST   /api/agents/runs/{id}/cancel           — 取消 Run
//   GET    /api/agents/runs/{id}/events           — SSE 事件流（支持 Last-Event-ID 断线重连）
//   POST   /api/agents/runs/{id}/approvals/{approvalId} — 提交 approval 决策
//
// 设计原则：
//   1. 遵循 ContextCore Minimal API 模式（IEndpointRouteBuilder 扩展方法）。
//   2. Workspace 隔离：workspaceId 优先从 IWorkspaceContextAccessor（认证上下文）读取，
//      未启用 RBAC 时回退到请求体中的 workspaceId，再回退到 "default"。
//   3. 创建 Run 需要 WorkspacePermission.AgentRun 权限位。
//   4. SSE 实现使用原始 HttpContext.Response.Body 流式写入，正确处理 CancellationToken。
//   5. 失败返回 ContextCoreErrorResponse，与其它端点一致。
// ===========================================================================

/// <summary>
/// Agent Execution API 端点。
/// </summary>
internal static class AgentExecutionEndpoints
{
    private const string Tag = "AgentExecution";

    public static IEndpointRouteBuilder MapAgentExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agents/runs").WithTags(Tag);

        // ── 创建并启动 AgentRun ────────────────────────────────────────
        group.MapPost("/", async Task<IResult> (
            CreateRunRequest request,
            IAgentRunStore runStore,
            [FromServices] AgentKernelHost? host,
            IWorkspaceContextAccessor workspaceContextAccessor,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Task))
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "agents.runs.create",
                    "Task 不能为空。", field: "task");
            }

            if (host is null)
            {
                return ContextCoreHttpResultMapper.Misconfigured(
                    httpContext, string.Empty, "agents.runs.create",
                    "AgentKernelHost 未注册到 DI 容器。");
            }

            // 解析 workspaceId：优先认证上下文，回退请求体，再回退默认值
            var workspaceId = ResolveWorkspaceId(workspaceContextAccessor, request.WorkspaceId);

            // WP-2：幂等去重。客户端提供 IdempotencyKey 时，先查询是否已有同 key 的 Run；
            // 命中则直接返回 200 OK（而非 201 Created），不重复启动 Actor。
            // 防止客户端重试/网络抖动导致同一业务意图被多次执行。
            var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey;
            if (idempotencyKey is not null)
            {
                AgentRun? existing;
                try
                {
                    existing = await runStore.GetByIdempotencyKeyAsync(workspaceId, idempotencyKey, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "agents.runs.create");
                }

                if (existing is not null)
                {
                    // 幂等命中：返回已有 Run（200 OK，不重复创建/启动）
                    return Results.Ok(ToRunResponse(existing));
                }
            }

            var runId = Guid.NewGuid().ToString("N");
            var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
                ? $"session-{runId}"
                : request.SessionId!;
            var now = DateTimeOffset.UtcNow;

            // 构建预算（请求未提供时使用默认值）
            var turnBudget = request.CostBudget is null
                ? new AgentTurnBudget { MaxTurns = 20, TurnsUsed = 0, MaxModelCalls = 60 }
                : new AgentTurnBudget
                {
                    MaxTurns = request.CostBudget.MaxTurns > 0 ? request.CostBudget.MaxTurns : 20,
                    TurnsUsed = 0,
                    MaxModelCalls = request.CostBudget.MaxTurns > 0 ? request.CostBudget.MaxTurns * 3 : 60
                };
            var costBudget = request.CostBudget is null
                ? new AgentCostBudget { MaxTokens = 100000, TokensUsed = 0, MaxCostUsd = 10.0, CostUsedUsd = 0.0 }
                : new AgentCostBudget
                {
                    MaxTokens = request.CostBudget.MaxTokens > 0 ? request.CostBudget.MaxTokens : 100000,
                    TokensUsed = 0,
                    MaxCostUsd = request.CostBudget.MaxCostUsd > 0 ? request.CostBudget.MaxCostUsd : 10.0,
                    CostUsedUsd = 0.0
                };

            // P0-5：从 API 入参写入 Run 约束（ModelArtifactId / AllowedToolIds / DeadlineAt）
            var timeoutSeconds = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 300;
            var allowedToolIds = request.ToolIds is { Count: > 0 } toolIds
                ? new HashSet<string>(toolIds, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var run = new AgentRun
            {
                RunId = runId,
                WorkspaceId = workspaceId,
                SessionId = sessionId,
                Task = request.Task!,
                State = AgentRunState.Created,
                Turn = 0,
                ModelCallsUsed = 0,
                CreatedAt = now,
                UpdatedAt = now,
                TurnBudget = turnBudget,
                CostBudget = costBudget,
                // P0-5：写入约束字段（Actor 在执行时强制校验）
                ModelArtifactId = string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId,
                AllowedToolIds = allowedToolIds,
                DeadlineAt = now + TimeSpan.FromSeconds(timeoutSeconds),
                ModelContextTokenBudget = 8192, // 默认 8192；0 或负数 = 不限制
                IdempotencyKey = idempotencyKey
            };

            try
            {
                await runStore.CreateAsync(run, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "agents.runs.create");
            }

            // P0-5 修复 CTS 释放问题：不再创建 linked timeout CTS（它在请求结束时被 Dispose，
            // 导致 Actor 收到 ObjectDisposedException）。
            // 超时控制改由 Run.DeadlineAt 字段承载，Actor 在每次模型调用前检查。
            // 传入 CancellationToken.None 让 Actor 不受 HTTP 请求生命周期影响（fire-and-forget）。
            // Run 可通过 POST /cancel 端点显式取消。
            try
            {
                await host.StartRunAsync(run, CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // Channel 已关闭或队列已满
                return ContextCoreHttpResultMapper.InternalError(
                    httpContext, string.Empty, "agents.runs.create",
                    $"启动 Run 失败：{ex.Message}");
            }

            var response = ToRunResponse(run);
            return Results.Created($"/api/agents/runs/{runId}", response);
        })
        .WithName("CreateAgentRun")
        .RequireWorkspacePermission(WorkspacePermission.AgentRun)
        .WithSummary("创建并启动 Agent Run")
        .Produces<RunResponse>(StatusCodes.Status201Created)
        .Produces<RunResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status500InternalServerError);

        // ── 获取 Run 状态 ─────────────────────────────────────────────
        group.MapGet("/{id}", async Task<IResult> (
            string id,
            IAgentRunStore runStore,
            IWorkspaceContextAccessor workspaceContextAccessor,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var workspaceId = ResolveWorkspaceId(workspaceContextAccessor, null);

            var run = await runStore.GetAsync(workspaceId, id, ct).ConfigureAwait(false);
            if (run is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "agents.runs.get",
                    $"未找到 RunId='{id}'。");
            }

            return Results.Ok(ToRunResponse(run));
        })
        .WithName("GetAgentRun")
        .WithSummary("获取 Agent Run 状态")
        .Produces<RunResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        // ── 取消 Run ─────────────────────────────────────────────────
        group.MapPost("/{id}/cancel", async Task<IResult> (
            string id,
            CancelRunRequest? request,
            IAgentRunStore runStore,
            [FromServices] AgentKernelHost? host,
            IWorkspaceContextAccessor workspaceContextAccessor,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var workspaceId = ResolveWorkspaceId(workspaceContextAccessor, null);

            var run = await runStore.GetAsync(workspaceId, id, ct).ConfigureAwait(false);
            if (run is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "agents.runs.cancel",
                    $"未找到 RunId='{id}'。");
            }

            if (AgentRunStateMachine.IsTerminalState(run.State))
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "agents.runs.cancel",
                    $"Run 已处于终态 {run.State}，无法取消。",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // 优先通过 AgentKernelHost 取消（触发 Actor CTS + 状态推进）
            if (host is not null)
            {
                var cancelled = await host.CancelRunAsync(workspaceId, id, ct).ConfigureAwait(false);
                if (!cancelled)
                {
                    // Host 未跟踪活跃 Run（可能已结束）→ 直接推进状态
                    try
                    {
                        await runStore.TransitionStateAsync(
                            workspaceId, id, run.State, AgentRunState.Cancelled, ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // CAS 失败 = 状态已被其他实例推进 → 非致命
                    }
                }
            }
            else
            {
                // Host 未注册 → 直接推进状态
                try
                {
                    await runStore.TransitionStateAsync(
                        workspaceId, id, run.State, AgentRunState.Cancelled, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    return ContextCoreHttpResultMapper.InternalError(
                        httpContext, string.Empty, "agents.runs.cancel",
                        $"取消失败：{ex.Message}");
                }
            }

            return Results.Accepted($"/api/agents/runs/{id}");
        })
        .WithName("CancelAgentRun")
        .RequireWorkspacePermission(WorkspacePermission.AgentRun)
        .WithSummary("取消 Agent Run")
        .Produces(StatusCodes.Status202Accepted)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

        // ── SSE 事件流 ───────────────────────────────────────────────
        group.MapGet("/{id}/events", async Task<IResult> (
            string id,
            IAgentRunStore runStore,
            IAgentRunEventStore eventStore,
            IWorkspaceContextAccessor workspaceContextAccessor,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var workspaceId = ResolveWorkspaceId(workspaceContextAccessor, null);

            var run = await runStore.GetAsync(workspaceId, id, ct).ConfigureAwait(false);
            if (run is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "agents.runs.events",
                    $"未找到 RunId='{id}'。");
            }

            // 解析 Last-Event-ID header（断线重连：从 lastSequence+1 开始读取）
            var lastEventSequence = ParseLastEventId(httpContext.Request.Headers["Last-Event-ID"]);

            // 配置 SSE 响应
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers["Cache-Control"] = "no-cache";
            httpContext.Response.Headers["Connection"] = "keep-alive";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no"; // 禁用 nginx 缓冲

            // 使用客户端断开令牌（RequestAborted 在客户端断开时触发）
            var streamCt = httpContext.RequestAborted;

            await using var writer = new StreamWriter(httpContext.Response.Body, leaveOpen: true)
            {
                AutoFlush = false
            };

            // 发送初始 retry 提示（客户端断线后重试间隔，毫秒）
            await writer.WriteLineAsync("retry: 1000").ConfigureAwait(false);
            await writer.FlushAsync(streamCt).ConfigureAwait(false);

            // SSE 轮询循环：读取新事件 → 推送 → 检查终态 → 等待 → 重复
            while (!streamCt.IsCancellationRequested)
            {
                int lastSentSequence;
                try
                {
                    lastSentSequence = await StreamNewEventsAsync(
                        writer, eventStore, workspaceId, id,
                        lastEventSequence, streamCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (streamCt.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // 读取异常时跳出（避免无限重试占用连接）
                    break;
                }

                // P0-6：cursor 推进到本轮实际发送的最后一条 Sequence
                // （不是 DB 最新 sequence，否则会跳过未发送的事件导致永久丢失）
                if (lastSentSequence >= 0)
                {
                    lastEventSequence = lastSentSequence;
                }

                // 检查 Run 是否已进入终态 → 关闭连接
                var currentRun = await runStore.GetAsync(workspaceId, id, streamCt)
                    .ConfigureAwait(false);
                if (currentRun is null || AgentRunStateMachine.IsTerminalState(currentRun.State))
                {
                    // 推送终态事件给客户端（确保客户端收到最终状态）
                    await WriteTerminalEventAsync(writer, currentRun, streamCt).ConfigureAwait(false);
                    break;
                }

                // 等待下一轮轮询（500ms 间隔；客户端断开时 Task.Delay 抛 OperationCanceledException）
                try
                {
                    await Task.Delay(500, streamCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (streamCt.IsCancellationRequested)
                {
                    break;
                }
            }

            return Results.Empty;
        })
        .WithName("StreamAgentRunEvents")
        .WithSummary("订阅 Agent Run 事件流（SSE；支持 Last-Event-ID 断线重连）")
        .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        // ── 提交 approval 决策 ───────────────────────────────────────
        group.MapPost("/{id}/approvals/{approvalId}", async Task<IResult> (
            string id,
            string approvalId,
            ApprovalRequest request,
            IAgentRunStore runStore,
            IAgentRunEventStore eventStore,
            [FromServices] IAgentApprovalGate? approvalGate,
            [FromServices] IAgentApprovalStore? approvalStore,
            IWorkspaceContextAccessor workspaceContextAccessor,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var workspaceId = ResolveWorkspaceId(workspaceContextAccessor, null);

            var run = await runStore.GetAsync(workspaceId, id, ct).ConfigureAwait(false);
            if (run is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "agents.runs.approvals",
                    $"未找到 RunId='{id}'。");
            }

            if (AgentRunStateMachine.IsTerminalState(run.State))
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "agents.runs.approvals",
                    $"Run 已处于终态 {run.State}，无法提交 approval。",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var isReject = string.Equals(request.Decision, "reject", StringComparison.OrdinalIgnoreCase);
            var approver = request.Approver ?? httpContext.User?.Identity?.Name;

            // P0-2：调用 IAgentApprovalStore.ResolveAsync 持久化审批决策（CAS：Pending → Approved/Rejected）。
            // 旧路径只追加事件 + 修改 Run 状态，未调用 ResolveAsync——审批记录永久滞留在 Pending 状态。
            if (approvalStore is not null)
            {
                try
                {
                    var decision = isReject
                        ? AgentApprovalStatus.Rejected
                        : AgentApprovalStatus.Approved;
                    await approvalStore.ResolveAsync(
                        workspaceId, approvalId, decision, approver, request.Reason, ct)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    // 审批不存在或已裁决（CAS 失败）→ 返回 409
                    return ContextCoreHttpResultMapper.InvalidRequest(
                        httpContext, string.Empty, "agents.runs.approvals",
                        $"审批裁决失败：{ex.Message}",
                        statusCode: StatusCodes.Status409Conflict);
                }
            }

            // 记录 ApprovalResolved 事件到事件流（哈希链）
            var lastSequence = await eventStore.GetLastSequenceAsync(workspaceId, id, ct)
                .ConfigureAwait(false);
            var lastEvent = lastSequence >= 0
                ? await eventStore.ReadAsync(workspaceId, id, lastSequence, 1, ct).ConfigureAwait(false)
                : Array.Empty<AgentRunEvent>();
            var prevChainHash = lastEvent.Count > 0 ? lastEvent[0].ContentHash : null;

            var payload = JsonSerializer.Serialize(new
            {
                approvalId,
                decision = request.Decision,
                reason = request.Reason,
                approver,
                runState = run.State.ToString()
            });

            var approvalEvent = AgentRunEventChain.BuildEvent(
                id, workspaceId, lastSequence + 1,
                AgentRunEventType.ApprovalResolved, run.State,
                payload, prevChainHash);

            try
            {
                await eventStore.AppendAsync(approvalEvent, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // 哈希链/sequence 不匹配
                return ContextCoreHttpResultMapper.InternalError(
                    httpContext, string.Empty, "agents.runs.approvals",
                    $"记录 approval 事件失败：{ex.Message}");
            }

            // P0-2：若 Run 处于 AwaitingApproval：
            //   决策为拒绝 → 推进到 Failed（无法继续）
            //   决策为批准 → 推进到 PendingToolExecution（Actor 恢复时直接执行原 Tool，不重新调用模型）
            // 旧路径批准后推进到 ToolDispatching，导致 Actor 重新调用模型、被批准的原 Tool 不会直接执行。
            if (run.State == AgentRunState.AwaitingApproval)
            {
                var targetState = isReject
                    ? AgentRunState.Failed
                    : AgentRunState.PendingToolExecution;
                try
                {
                    await runStore.TransitionStateAsync(
                        workspaceId, id, run.State, targetState, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // CAS 失败 = 状态已被 Actor 推进 → 非致命（决策已记录到事件流 + 已 ResolveAsync）
                }
            }

            return Results.Accepted($"/api/agents/runs/{id}");
        })
        .WithName("SubmitAgentRunApproval")
        .RequireWorkspacePermission(WorkspacePermission.AgentRun)
        .WithSummary("提交 Agent Run 的 approval 决策（approve/reject）")
        .Produces(StatusCodes.Status202Accepted)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

        return app;
    }

    // -----------------------------------------------------------------------
    // 辅助方法
    // -----------------------------------------------------------------------

    /// <summary>
    /// 解析 workspaceId：优先从认证上下文读取，回退到请求提供的值，再回退到 "default"。
    /// </summary>
    private static string ResolveWorkspaceId(IWorkspaceContextAccessor? accessor, string? fallback)
    {
        var fromContext = accessor?.Current?.WorkspaceId;
        if (!string.IsNullOrWhiteSpace(fromContext))
        {
            return fromContext!;
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback!;
        }

        return "default";
    }

    /// <summary>
    /// 解析 Last-Event-ID header 为 sequence 号（无效时返回 -1 表示从头读取）。
    /// </summary>
    private static int ParseLastEventId(string? lastEventId)
    {
        if (string.IsNullOrWhiteSpace(lastEventId))
        {
            return -1;
        }

        return int.TryParse(lastEventId, out var seq) ? seq : -1;
    }

    /// <summary>
    /// 从事件存储读取新事件并推送为 SSE 格式。
    /// </summary>
    /// <returns>本轮实际发送的最后一条事件的 Sequence（无事件时返回 -1）。</returns>
    private static async Task<int> StreamNewEventsAsync(
        StreamWriter writer,
        IAgentRunEventStore eventStore,
        string workspaceId,
        string runId,
        int lastSequence,
        CancellationToken ct)
    {
        var fromSequence = lastSequence + 1;
        var events = await eventStore.ReadAsync(workspaceId, runId, fromSequence, 100, ct)
            .ConfigureAwait(false);

        // P0-6：本轮最后一条事件 Sequence（用于推进 cursor，而非 DB 最新 sequence）
        var lastSentSequence = -1;
        foreach (var evt in events)
        {
            // SSE 格式：
            //   id: {sequence}
            //   event: {eventType}
            //   data: {json}
            //   (空行结束事件)
            await writer.WriteLineAsync($"id: {evt.Sequence}").ConfigureAwait(false);
            await writer.WriteLineAsync($"event: {evt.EventType}").ConfigureAwait(false);
            await writer.WriteLineAsync($"data: {JsonSerializer.Serialize(evt)}").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            lastSentSequence = evt.Sequence;
        }

        // P0-6：每批次 Flush 一次（而非每条事件 Flush），减少系统调用开销
        if (events.Count > 0)
        {
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }

        return lastSentSequence;
    }

    /// <summary>
    /// 推送终态事件（确保客户端收到 Run 最终状态后关闭连接）。
    /// </summary>
    private static async Task WriteTerminalEventAsync(
        StreamWriter writer,
        AgentRun? run,
        CancellationToken ct)
    {
        if (run is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            runId = run.RunId,
            state = run.State.ToString(),
            finishedAt = run.FinishedAt,
            finalAnswer = run.FinalAnswer,
            failureReason = run.FailureReason
        });

        await writer.WriteLineAsync($"event: run.terminal").ConfigureAwait(false);
        await writer.WriteLineAsync($"data: {payload}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 将 AgentRun 转换为 API 响应 DTO。
    /// </summary>
    private static RunResponse ToRunResponse(AgentRun run)
        => new()
        {
            RunId = run.RunId,
            WorkspaceId = run.WorkspaceId,
            SessionId = run.SessionId,
            Task = run.Task,
            State = run.State.ToString(),
            Turn = run.Turn,
            ModelCallsUsed = run.ModelCallsUsed,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
            FinishedAt = run.FinishedAt,
            FailureReason = run.FailureReason,
            FinalAnswer = run.FinalAnswer,
            TurnBudget = run.TurnBudget is null ? null : new TurnBudgetResponse
            {
                MaxTurns = run.TurnBudget.MaxTurns,
                TurnsUsed = run.TurnBudget.TurnsUsed,
                MaxModelCalls = run.TurnBudget.MaxModelCalls,
                ModelCallsUsed = run.TurnBudget.ModelCallsUsed
            },
            CostBudget = run.CostBudget is null ? null : new CostBudgetResponse
            {
                MaxTokens = run.CostBudget.MaxTokens,
                TokensUsed = run.CostBudget.TokensUsed,
                MaxCostUsd = run.CostBudget.MaxCostUsd,
                CostUsedUsd = run.CostBudget.CostUsedUsd
            }
        };
}

// ---------------------------------------------------------------------------
// Agent Execution API 请求 / 响应 DTO
// ---------------------------------------------------------------------------

/// <summary>创建 Agent Run 请求。</summary>
public sealed class CreateRunRequest
{
    /// <summary>用户输入/任务描述（必填）。</summary>
    public string? Task { get; init; }

    /// <summary>Workspace ID（可选；未启用 RBAC 时由请求体提供，否则从认证上下文读取）。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>Session ID（可选；为空时自动生成 "session-{runId}"）。</summary>
    public string? SessionId { get; init; }

    /// <summary>模型工件 ID（可选；用于审计追踪）。</summary>
    public string? ModelId { get; init; }

    /// <summary>允许调用的 Tool ID 列表（可选）。</summary>
    public IReadOnlyList<string>? ToolIds { get; init; }

    /// <summary>WP-2：幂等键（可选；客户端提供用于去重，防止重试导致重复执行）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Cost 预算限制（可选；未提供时使用默认值）。</summary>
    public CostBudgetRequest? CostBudget { get; init; }

    /// <summary>执行超时（秒；默认 300）。</summary>
    public int TimeoutSeconds { get; init; }
}

/// <summary>Cost 预算请求。</summary>
public sealed class CostBudgetRequest
{
    /// <summary>最大 Token 消耗。</summary>
    public int MaxTokens { get; init; }

    /// <summary>最大循环轮次。</summary>
    public int MaxTurns { get; init; }

    /// <summary>最大推理费用（美元）。</summary>
    public double MaxCostUsd { get; init; }
}

/// <summary>取消 Run 请求。</summary>
public sealed class CancelRunRequest
{
    /// <summary>取消原因（可选，用于审计）。</summary>
    public string? Reason { get; init; }

    /// <summary>操作发起者（可选，用于审计）。</summary>
    public string? Operator { get; init; }
}

/// <summary>提交 approval 决策请求。</summary>
public sealed class ApprovalRequest
{
    /// <summary>决策：approve / reject（必填）。</summary>
    public string? Decision { get; init; }

    /// <summary>决策原因（可选，用于审计）。</summary>
    public string? Reason { get; init; }

    /// <summary>审批者标识（可选；未提供时从认证上下文读取）。</summary>
    public string? Approver { get; init; }
}

/// <summary>Agent Run 响应。</summary>
public sealed class RunResponse
{
    /// <summary>Run 唯一 ID。</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Workspace ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>Session ID。</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>用户输入/任务描述。</summary>
    public string Task { get; init; } = string.Empty;

    /// <summary>当前状态（Created/ContextBuilding/.../Completed/Failed/Cancelled）。</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>当前循环轮次。</summary>
    public int Turn { get; init; }

    /// <summary>当前已发起的模型调用次数。</summary>
    public int ModelCallsUsed { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>结束时间（终态时设置；运行中为 null）。</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>失败原因（State=Failed 时设置）。</summary>
    public string? FailureReason { get; init; }

    /// <summary>最终答案（State=Completed 时设置）。</summary>
    public string? FinalAnswer { get; init; }

    /// <summary>Turn 预算使用情况。</summary>
    public TurnBudgetResponse? TurnBudget { get; init; }

    /// <summary>Cost 预算使用情况。</summary>
    public CostBudgetResponse? CostBudget { get; init; }
}

/// <summary>Turn 预算响应。</summary>
public sealed class TurnBudgetResponse
{
    /// <summary>最大循环轮次。</summary>
    public int MaxTurns { get; init; }

    /// <summary>当前已用轮次。</summary>
    public int TurnsUsed { get; init; }

    /// <summary>最大模型调用次数。</summary>
    public int MaxModelCalls { get; init; }

    /// <summary>当前已用模型调用次数。</summary>
    public int ModelCallsUsed { get; init; }
}

/// <summary>Cost 预算响应。</summary>
public sealed class CostBudgetResponse
{
    /// <summary>最大 Token 消耗。</summary>
    public int MaxTokens { get; init; }

    /// <summary>当前已消耗 Token。</summary>
    public int TokensUsed { get; init; }

    /// <summary>最大推理费用（美元）。</summary>
    public double MaxCostUsd { get; init; }

    /// <summary>当前已产生费用（美元）。</summary>
    public double CostUsedUsd { get; init; }
}
