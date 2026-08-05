using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Service.Endpoints;

// ===========================================================================
// Agent Execution API — Agent Run 的 HTTP 入口
//
// 提供正式的 REST API 以驱动 Agent 执行循环：
// POST /api/agents/runs — 创建并启动 AgentRun
// GET /api/agents/runs/{id} — 获取 Run 状态
// POST /api/agents/runs/{id}/cancel — 取消 Run
// GET /api/agents/runs/{id}/events — SSE 事件流（支持 Last-Event-ID 断线重连）
// POST /api/agents/runs/{id}/approvals/{approvalId} — 提交 approval 决策
// POST /api/agents/runs/{id}/compact — 压缩事件流前缀（Operator；折叠为快照并归档）
// GET /api/agents/runs/{id}/events/snapshot — 读取压缩快照（Operator；未压缩时 404）
//
// 设计原则：
// 1. 遵循 ContextCore Minimal API 模式（IEndpointRouteBuilder 扩展方法）。
// 2. Workspace 隔离：workspaceId 优先从 IWorkspaceContextAccessor（认证上下文）读取，
// 未启用 RBAC 时回退到请求体中的 workspaceId，再回退到 "default"。
// 3. 创建 Run 需要 WorkspacePermission.AgentRun 权限位。
// 4. SSE 实现使用原始 HttpContext.Response.Body 流式写入，正确处理 CancellationToken。
// 5. 失败返回 ContextCoreErrorResponse，与其它端点一致。
// ===========================================================================

/// <summary>
/// Agent Execution API 端点。
/// </summary>
internal static class AgentExecutionEndpoints
{
    private const string Tag = "AgentExecution";

    /// <summary>
    /// watchdog 补偿间隔。SSE 等待 notifier 推送时，每 30s 强制做一次 DB 补读，
    /// 防止 notifier 遗漏通知（如 channel 满时 TryWrite 丢弃）导致永久挂起。
    /// 间隔远大于正常通知间隔，避免频繁 DB 查询。
    /// </summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 终态补读轮次上限。Run 进入终态后循环补读尾部事件，直至无新事件或达到本上限，
    /// 防御病态路径（终态 Run 仍被追加事件）导致连接永不关闭的资源泄漏。
    /// 每轮最多读取 100 条 → 上限覆盖 5000 条事件，远超正常 Run 的事件量。
    /// </summary>
    private const int MaxTerminalDrainRounds = 50;

    /// <summary>管理员 raw 事件分页默认页大小。</summary>
    private const int DefaultRawEventsPageSize = 200;

    /// <summary>管理员 raw 事件分页服务端页大小上限（clamp 用户 limit，防止无界读取）。</summary>
    private const int MaxRawEventsPageSize = 500;

    /// <summary>SSE 单轮补读事件数上限（统一视图：归档 + 热表合并后按 sequence 取前 N 条）。</summary>
    private const int SseReadBatchSize = 100;

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
            // 处理逻辑提取到 internal static handler（端点单元测试直接调用）：
            // 幂等创建 → workspace 配额预留 → 入队调度。
            return await CreateAgentRunHandlerAsync(
                request, runStore, host, workspaceContextAccessor, httpContext, ct).ConfigureAwait(false);
        })
        .WithName("CreateAgentRun")
        .RequireWorkspacePermission(WorkspacePermission.AgentRun)
        .WithSummary("创建并启动 Agent Run")
        .Produces<RunResponse>(StatusCodes.Status201Created)
        .Produces<RunResponse>(StatusCodes.Status200OK)
        .Produces<RunResponse>(StatusCodes.Status429TooManyRequests)
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
        .RequireWorkspacePermission(WorkspacePermission.AgentRun)
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

            // Run 确定不再执行：释放创建时预留的配额（退回容量；未预留/服务未注册时无操作）
            var quotaService = httpContext.RequestServices.GetService<IWorkspaceQuotaService>();
            if (quotaService is not null)
            {
                await quotaService.ReleaseAsync(workspaceId, id, ct).ConfigureAwait(false);
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
            [FromServices] IAgentRunEventNotifier? eventNotifier,
            [FromServices] IAgentRunEventCompactor? compactor,
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

            // SSE 轮询循环（修复永久丢唤醒窗口）：
            // a. 先注册 subscription（消除"DB 读取与订阅注册之间事件丢失"竞态）
            // b. DB 补读 watermark 之后的事件（catch-up）
            // c. 发送补读事件到 SSE 流
            // d. 检查终态
            // e. 等待 notifier 推送（30s watchdog 补偿，防止遗漏通知导致永久挂起）
            while (!streamCt.IsCancellationRequested)
            {
                // a. 先注册 subscription——注册时刻 = watermark。
                // 注册前到 DB 补读之间提交的事件由 DB 补读捕获；
                // 注册后提交的事件由 notifier 推送捕获。无竞态窗口。
                using var subscription = eventNotifier?.RegisterSubscription(
                    workspaceId, id, lastEventSequence + 1);

                // b+c. DB 补读 watermark 之后的事件并发送到 SSE 流
                int lastSentSequence;
                try
                {
                    lastSentSequence = await StreamNewEventsAsync(
                        writer, eventStore, compactor, workspaceId, id,
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

                // cursor 推进到本轮实际发送的最后一条 Sequence
                // （不是 DB 最新 sequence，否则会跳过未发送的事件导致永久丢失）
                if (lastSentSequence >= 0)
                {
                    lastEventSequence = lastSentSequence;
                }

                // d. 检查 Run 是否已进入终态 → 终态补读后关闭连接
                var currentRun = await runStore.GetAsync(workspaceId, id, streamCt)
                    .ConfigureAwait(false);
                if (currentRun is null || AgentRunStateMachine.IsTerminalState(currentRun.State))
                {
                    // 终态补读循环：Run 进入终态时，尾部事件可能尚未送达——
                    // 状态 CAS 与事件可能在同一批提交（终态判定先于事件落库），
                    // 或单轮补读上限（100 条）内仍有剩余。反复补读直到无新事件，
                    // 确保客户端收到完整事件流后再关闭连接。
                    for (var drainRound = 0; drainRound < MaxTerminalDrainRounds; drainRound++)
                    {
                        int drained;
                        try
                        {
                            drained = await StreamNewEventsAsync(
                                writer, eventStore, compactor, workspaceId, id,
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

                        if (drained < 0)
                        {
                            break;
                        }
                        lastEventSequence = drained;
                    }

                    // 推送终态事件给客户端（确保客户端收到最终状态）
                    await WriteTerminalEventAsync(writer, currentRun, streamCt).ConfigureAwait(false);
                    break;
                }

                // e. 等待下一轮事件。优先使用 push 通道（IAgentRunEventNotifier），
                // 事件到达时立即唤醒读取。添加 30s watchdog 补偿——
                // 若 notifier 遗漏通知（如 channel 满时 TryWrite 丢弃），watchdog 超时后
                // 回到循环顶部做 DB 补读，防止永久挂起。
                // 未注入 notifier 时回退到 500ms 固定轮询。
                if (subscription is not null)
                {
                    using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(streamCt);
                    watchdogCts.CancelAfter(WatchdogInterval);
                    try
                    {
                        // 收到首个 push 通知即 break，回到循环顶部走 ReadAsync 拉取实际事件并推送。
                        await foreach (var _ in subscription
                            .WithCancellation(watchdogCts.Token).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (streamCt.IsCancellationRequested)
                    {
                        break;
                    }
                    // watchdog 超时（非 streamCt 取消）时正常落到下一轮循环做 DB 补读
                }
                else
                {
                    try
                    {
                        await Task.Delay(500, streamCt).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (streamCt.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            return Results.Empty;
        })
        .WithName("StreamAgentRunEvents")
        .RequireWorkspacePermission(WorkspacePermission.AgentRun)
        .WithSummary("订阅 Agent Run 事件流（SSE；支持 Last-Event-ID 断线重连）")
        .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        // ── 管理员审计：读取原始事件流（游标分页；压缩后自动拼接归档）─────────────
        // SSE 公开端点隐藏敏感信息（Tool 参数、原始模型输出、异常堆栈），
        // 管理员需要完整 Payload 进行审计/调试时通过此端点获取原始 AgentRunEvent。
        // 需要 WorkspaceRole.Admin 角色（RBAC 强制校验未启用时自动放行，仅记录审计日志）。
        // 分页参数：after = 上一页 NextSequence（省略或 -1 = 从头）；limit = 页大小（服务端 clamp 到上限）。
        // 事件流压缩（P0-10）后折叠前缀已归档到 agent_run_events_archive，热表只保留
        // 锚点 + 增量：端点按 Sequence 合并归档 + 热表，保证管理员始终能看到完整历史
        // （修复审计缺口；非 Postgres provider 未注册 compactor 时仅读热表）。
        group.MapGet("/{id}/events/raw", async Task<IResult> (
            string id,
            IAgentRunStore runStore,
            IAgentRunEventStore eventStore,
            [FromServices] IAgentRunEventCompactor? compactor,
            IWorkspaceContextAccessor workspaceContextAccessor,
            HttpContext httpContext,
            CancellationToken ct,
            int? after = null,
            int? limit = null) =>
        {
            var workspaceId = ResolveWorkspaceId(workspaceContextAccessor, null);

            var run = await runStore.GetAsync(workspaceId, id, ct).ConfigureAwait(false);
            if (run is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "agents.runs.events.raw",
                    $"未找到 RunId='{id}'。");
            }

            // 游标分页：多读 1 条判定 HasMore，服务端 clamp 页大小上限（替代旧 take=int.MaxValue 无界读取）。
            var fromSequence = (after ?? -1) + 1;
            var pageSize = Math.Clamp(limit ?? DefaultRawEventsPageSize, 1, MaxRawEventsPageSize);

            // 审计拼接：归档（折叠前缀，sequence < 锚点）+ 热表（锚点 + 增量）按 Sequence 合并，
            // 保证压缩后管理员仍能看到完整事件历史（P0-10 审计缺口修复）。
            // 非 Postgres provider 未注册 compactor → 仅读热表（与压缩不可用的部署一致）。
            var merged = await ReadUnifiedEventsAsync(
                compactor, eventStore, workspaceId, id, fromSequence, pageSize + 1, ct).ConfigureAwait(false);

            var hasMore = merged.Count > pageSize;
            var items = hasMore ? merged.Take(pageSize).ToArray() : merged.ToArray();
            var nextSequence = hasMore && items.Length > 0 ? items[^1].Sequence : -1;

            return Results.Ok(new AgentRunRawEventsPage
            {
                Items = items,
                NextSequence = nextSequence,
                HasMore = hasMore
            });
        })
        .WithName("GetAgentRunRawEvents")
        .RequireWorkspaceRole(WorkspaceRole.Admin)
        .WithSummary("管理员审计：读取 Run 的原始事件流（含完整 Tool 参数、模型输出、异常堆栈）")
        .Produces<AgentRunRawEventsPage>(StatusCodes.Status200OK)
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
            [FromServices] IPersistentAgentApprovalStore? persistentApprovalStore,
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

            // Tool 授权复核（仅批准路径）：审批裁决前重复校验——快照有效 + 工具在授权集内 +
            // 审批者能力覆盖。拒绝不授予任何执行权，无需复核。审批 Store 缺失时无法获取
            // Tool 名称，按 fail-closed 拒绝批准。
            if (!string.Equals(request.Decision, "reject", StringComparison.OrdinalIgnoreCase))
            {
                AgentApproval? approvalForAuthorization = null;
                if (approvalStore is not null)
                {
                    try
                    {
                        approvalForAuthorization = await approvalStore
                            .GetAsync(workspaceId, approvalId, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        return ContextCoreHttpResultMapper.Error(
                            httpContext, ex, string.Empty, "agents.runs.approvals");
                    }

                    if (approvalForAuthorization is null)
                    {
                        return ContextCoreHttpResultMapper.NotFound(
                            httpContext, string.Empty, "agents.runs.approvals",
                            $"未找到 approvalId='{approvalId}'。");
                    }

                    if (!string.Equals(approvalForAuthorization.RunId, id, StringComparison.Ordinal))
                    {
                        return ContextCoreHttpResultMapper.InvalidRequest(
                            httpContext, string.Empty, "agents.runs.approvals",
                            $"审批记录的 RunId='{approvalForAuthorization.RunId}' 与路由 RunId='{id}' 不匹配。",
                            statusCode: StatusCodes.Status400BadRequest);
                    }
                }
                else
                {
                    // 审批存储缺失 → 无法复核 Tool 授权（获取不到 Tool 名称），fail-closed 拒绝批准。
                    return Results.Forbid();
                }

                var authorizationError = await CheckApprovalAuthorizationAsync(
                    run,
                    approvalForAuthorization,
                    workspaceContextAccessor?.Current,
                    httpContext.RequestServices.GetService<SecurityOptions>(),
                    httpContext.RequestServices.GetService<IToolAuthorizationPolicy>(),
                    httpContext.RequestServices.GetService<IToolAuthorizer>(),
                    ct).ConfigureAwait(false);

                if (authorizationError is not null)
                {
                    // 审批者无执行/审批权限或快照失效 → 403（与 409 语义区分：
                    // 409 是裁决冲突，403 是授权不足）。
                    return Results.Forbid();
                }
            }

            var isReject = string.Equals(request.Decision, "reject", StringComparison.OrdinalIgnoreCase);
            var approver = request.Approver ?? httpContext.User?.Identity?.Name;
            var decision = isReject
                ? AgentApprovalStatus.Rejected
                : AgentApprovalStatus.Approved;
            // 决策推进的 Run 目标状态：批准 → PendingToolExecution（Actor 恢复时直接执行原 Tool）；
            // 拒绝 → Failed（终态）。持久化用于幂等重试校验。
            var targetRunState = isReject
                ? AgentRunState.Failed
                : AgentRunState.PendingToolExecution;

            // c：先构建审批事件（哈希链计算），供原子方法或回退路径使用。
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

            // c：优先使用 IPersistentAgentApprovalStore 的原子方法（单事务：裁决审批 + 追加事件 + CAS 推进 Run 状态）。
            // 旧路径 ResolveAsync → AppendAsync → TransitionStateAsync 三步非原子，任一步失败留下不一致状态。
            if (persistentApprovalStore is not null)
            {
                try
                {
                    var result = await persistentApprovalStore.ResolveApprovalAndAdvanceRunAsync(
                        workspaceId, id, approvalId,
                        expectedRunState: run.State,
                        decision, approver, request.Reason,
                        approvalEvent, ct, request.DecisionRequestId, targetRunState).ConfigureAwait(false);

                    if (!result.Succeeded)
                    {
                        return ContextCoreHttpResultMapper.InvalidRequest(
                            httpContext, string.Empty, "agents.runs.approvals",
                            $"审批裁决失败：{result.FailureReason}",
                            statusCode: StatusCodes.Status409Conflict);
                    }

                    // c：批准且 Run 已推进到 PendingToolExecution 时，立即入队 AgentKernelHost
                    //（不等 RecoveryWorker 轮询，缩短审批通过到 Tool 执行的延迟）。
                    if (result.RunStateChanged
                        && result.NewRunState == AgentRunState.PendingToolExecution
                        && host is not null)
                    {
                        try
                        {
                            var updatedRun = await runStore.GetAsync(workspaceId, id, ct).ConfigureAwait(false);
                            if (updatedRun is not null)
                            {
                                // 非阻塞入队：队列满/关闭时不阻塞 HTTP 请求（RecoveryWorker 会重新入队执行）。
                                await host.TryEnqueueAsync(updatedRun, CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            // 入队失败（Actor 依赖缺失）非致命：RecoveryWorker 会重新入队执行。
                        }
                    }

                    return Results.Accepted($"/api/agents/runs/{id}");
                }
                catch (InvalidOperationException ex)
                {
                    return ContextCoreHttpResultMapper.InternalError(
                        httpContext, string.Empty, "agents.runs.approvals",
                        $"原子审批裁决失败：{ex.Message}");
                }
            }

            // c：回退路径——Store 不支持原子方法时，走旧的三步非原子流程。
            // ALWAYS validate approval.RunId == route runId：原子方法在 SQL 内校验（WHERE run_id=@run_id），
            // 回退路径显式获取审批记录并校验 RunId 匹配，防跨 Run 误裁决。
            if (approvalStore is not null)
            {
                AgentApproval? approval;
                try
                {
                    approval = await approvalStore.GetAsync(workspaceId, approvalId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "agents.runs.approvals");
                }

                if (approval is null)
                {
                    return ContextCoreHttpResultMapper.NotFound(
                        httpContext, string.Empty, "agents.runs.approvals",
                        $"未找到 approvalId='{approvalId}'。");
                }

                if (!string.Equals(approval.RunId, id, StringComparison.Ordinal))
                {
                    return ContextCoreHttpResultMapper.InvalidRequest(
                        httpContext, string.Empty, "agents.runs.approvals",
                        $"审批记录的 RunId='{approval.RunId}' 与路由 RunId='{id}' 不匹配。",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                try
                {
                    await approvalStore.ResolveAsync(
                        workspaceId, approvalId, decision, approver, request.Reason, ct,
                        request.DecisionRequestId, targetRunState)
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

            // 若 Run 处于 AwaitingApproval：
            // 决策为拒绝 → 推进到 Failed（无法继续）
            // 决策为批准 → 推进到 PendingToolExecution（Actor 恢复时直接执行原 Tool，不重新调用模型）
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

        // ── 裁决 Tool 对账记录 ─────────────────────────────────────
        group.MapPost("/{id}/reconciliations/{reconciliationId}/resolve", async Task<IResult> (
            string id,
            string reconciliationId,
            ResolveReconciliationRequest request,
            IAgentRunStore runStore,
            [FromServices] ToolReconciliationCoordinator? coordinator,
            [FromServices] IToolReconciliationStore? reconciliationStore,
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
                    httpContext, string.Empty, "agents.runs.reconciliations",
                    $"未找到 RunId='{id}'。");
            }

            if (AgentRunStateMachine.IsTerminalState(run.State))
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "agents.runs.reconciliations",
                    $"Run 已处于终态 {run.State}，无法裁决对账记录。",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var outcome = new ToolReconciliationOutcome
            {
                SideEffectOccurred = request.SideEffectOccurred,
                Result = request.Result,
                Error = request.Error
            };

            int code;
            if (coordinator is not null)
            {
                code = await coordinator.ResolveAsync(reconciliationId, outcome, ct, request.DecisionRequestId).ConfigureAwait(false);
            }
            else if (reconciliationStore is not null)
            {
                // 协调器未注册（独立宿主场景）→ 回退到存储幂等裁决（不提交 journal）。
                // P0-5：先原子取得裁决权（租约）再裁决，避免与自动 Handler 竞争写入相反结果。
                var record = await reconciliationStore.GetAsync(reconciliationId, ct).ConfigureAwait(false);
                if (record is null)
                {
                    code = 1;
                }
                else if (record.Status is ToolReconciliationStatus.Resolved or ToolReconciliationStatus.Rejected)
                {
                    // 客户端决策幂等——相同决策身份 + 相同 outcome → 幂等成功（0）；
                    // 相同决策身份 + 相反 outcome → 决策冲突（4）；无/不同决策身份 → 2。
                    if (!string.IsNullOrWhiteSpace(request.DecisionRequestId)
                        && string.Equals(record.DecisionRequestId, request.DecisionRequestId, StringComparison.Ordinal))
                    {
                        code = record.SideEffectOccurred == outcome.SideEffectOccurred ? 0 : 4;
                    }
                    else
                    {
                        code = 2;
                    }
                }
                else
                {
                    var lease = await reconciliationStore.TryBeginAsync(
                        reconciliationId, "manual:endpoint", TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
                    if (lease is null)
                    {
                        code = 3; // 仲裁权被占用（有效租约持有中）
                    }
                    else if (outcome.SideEffectOccurred)
                    {
                        await reconciliationStore.MarkResolvedAsync(reconciliationId, lease.LeaseToken, outcome, ct).ConfigureAwait(false);
                        code = 0;
                    }
                    else
                    {
                        await reconciliationStore.MarkRejectedAsync(reconciliationId, lease.LeaseToken, outcome, ct).ConfigureAwait(false);
                        code = 0;
                    }
                }
            }
            else
            {
                return ContextCoreHttpResultMapper.InternalError(
                    httpContext, string.Empty, "agents.runs.reconciliations",
                    "未注册 ToolReconciliationCoordinator / IToolReconciliationStore，无法裁决对账记录。");
            }

            return code switch
            {
                1 => ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "agents.runs.reconciliations",
                    $"未找到 reconciliationId='{reconciliationId}'。"),
                2 => ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "agents.runs.reconciliations",
                    $"对账记录 '{reconciliationId}' 已裁决，重复提交被拒绝（请携带相同的 DecisionRequestId 以幂等重试）。",
                    statusCode: StatusCodes.Status409Conflict),
                4 => ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "agents.runs.reconciliations",
                    $"对账记录 '{reconciliationId}' 已按相同的 DecisionRequestId 裁决为相反结果（决策冲突），请撤销或更换决策身份。",
                    statusCode: StatusCodes.Status409Conflict),
                3 => ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "agents.runs.reconciliations",
                    $"对账记录 '{reconciliationId}' 的裁决权正被其他 Worker/请求持有，请稍后重试。",
                    statusCode: StatusCodes.Status409Conflict),
                _ => await ResolveAccepted(workspaceId, id, reconciliationId, reconciliationStore, host, httpContext, ct)
            };
        })
        .WithName("ResolveAgentRunReconciliation")
        .RequireWorkspacePermission(WorkspacePermission.AgentRun)
        .WithSummary("裁决 Tool 对账记录（确认外部副作用真相 / 拒绝重放）")
        .Produces(StatusCodes.Status202Accepted)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

        // ── Tool Reconciliation Control Plane（-B1）────────────────────
        // GET /api/agents/reconciliations 双模式：
        // ?externalOperationId=… → 按 journal 外部操作 ID 反查（跨 Run 运维查询）；
        // 无 externalOperationId → ControlRoom 分页待决列表（过期高亮 + 告警计数）。
        var reconciliationGroup = app.MapGroup("/api/agents/reconciliations").WithTags(Tag);
        reconciliationGroup.MapGet("/", async Task<IResult> (
            [FromQuery] string? externalOperationId,
            [FromQuery] string? workspaceId,
            [FromQuery] string? runId,
            [FromQuery] ToolReconciliationStatus? status,
            [FromQuery] bool? overdueOnly,
            [FromQuery] int? offset,
            [FromQuery] int? limit,
            [FromServices] IToolReconciliationStore? reconciliationStore,
            IWorkspaceContextAccessor workspaceContextAccessor,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (reconciliationStore is null)
            {
                return ContextCoreHttpResultMapper.InternalError(
                    httpContext, string.Empty, "agents.reconciliations",
                    "未注册 IToolReconciliationStore，无法提供对账列表。");
            }

            // 模式 1：按 journal externalOperationId 反查（跨 Run）。
            if (!string.IsNullOrWhiteSpace(externalOperationId))
            {
                var matches = await reconciliationStore
                    .QueryByExternalOperationIdAsync(externalOperationId.Trim(), ct).ConfigureAwait(false);
                return Results.Ok(new
                {
                    externalOperationId = externalOperationId.Trim(),
                    items = matches
                });
            }

            // 模式 2：ControlRoom 分页列表（workspace 默认从认证上下文解析）。
            var resolvedWorkspaceId = ResolveWorkspaceId(workspaceContextAccessor, workspaceId);
            var query = new ReconciliationQuery
            {
                WorkspaceId = string.IsNullOrWhiteSpace(resolvedWorkspaceId) ? null : resolvedWorkspaceId,
                RunId = string.IsNullOrWhiteSpace(runId) ? null : runId,
                Status = status,
                OverdueOnly = overdueOnly ?? false,
                Offset = offset ?? 0,
                Limit = limit ?? 50
            };
            var result = await reconciliationStore.ListAsync(query, ct).ConfigureAwait(false);
            return Results.Ok(result);
        })
        .WithName("ListAgentRunReconciliations")
        .RequireWorkspacePermission(WorkspacePermission.AgentRun)
        .WithSummary("Tool 对账 Control Room：分页待决列表（过期高亮 + 告警计数）或按 ExternalOperationId 反查")
        .Produces<ReconciliationListResult>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status500InternalServerError);

        // ── 事件流压缩（Event Snapshot & Compaction）───────────────────
        // Operator 运维端点：将 Run 事件流前缀 [0..upToSequence] 折叠为快照并归档，
        // 控制长生命周期 Run 热表无界增长（锚点事件保留，哈希链完整性不受影响）。
        // 仅 Postgres provider 注册 compactor；未注册时返回 503（不可用）。
        group.MapPost("/{id}/compact", async Task<IResult> (
            string id,
            IAgentRunStore runStore,
            [FromServices] IAgentRunEventCompactor? compactor,
            IWorkspaceContextAccessor workspaceContextAccessor,
            HttpContext httpContext,
            CancellationToken ct,
            int? upToSequence = null) =>
        {
            var workspaceId = ResolveWorkspaceId(workspaceContextAccessor, null);

            var run = await runStore.GetAsync(workspaceId, id, ct).ConfigureAwait(false);
            if (run is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "agents.runs.compact",
                    $"未找到 RunId='{id}'。");
            }

            // R30.1 保守策略：仅终态（或重试已耗尽）的 Run 允许压缩。可恢复快照
            // （P0-10 正式方案：Snapshot + Anchor + Hot Delta）已支持非终态 Run 的崩溃恢复，
            // 但保留终态限制避免意外压缩活跃 Run；与 PostgresAgentRunEventCompactor
            // FindCandidatesAsync 的候选过滤保持一致。
            if (!PostgresAgentRunEventCompactor.IsCompactableRunState(run.State, run.RetryCount, run.MaxRetries))
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "agents.runs.compact",
                    $"RunId='{id}' 当前状态 {run.State} 非终态，不允许压缩事件流（R30.1 保守策略仅限终态 Run）。",
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (compactor is null)
            {
                return ContextCoreHttpResultMapper.Misconfigured(
                    httpContext, string.Empty, "agents.runs.compact",
                    "IAgentRunEventCompactor 未注册到 DI 容器（仅 Postgres provider 支持事件流压缩）。");
            }

            // upToSequence 省略时折叠到当前最后事件（钳制到流末尾，锚点 = 最后事件）。
            var result = await compactor.CompactAsync(
                workspaceId, id, upToSequence ?? -1, ct).ConfigureAwait(false);
            return Results.Ok(result);
        })
        .WithName("CompactAgentRunEvents")
        .RequireWorkspaceRole(WorkspaceRole.Operator)
        .WithSummary("压缩 Run 事件流前缀（折叠为快照并归档，锚点保留哈希链完整性）")
        .Produces<AgentRunCompactionResult>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 读取事件流压缩快照 ────────────────────────────────────────
        // Operator 运维端点：返回 per-run 压缩快照（锚点 sequence + 链头哈希 + 状态摘要）。
        // 未压缩过的 Run 返回 404；非 Postgres provider 未注册 compactor → 503。
        group.MapGet("/{id}/events/snapshot", async Task<IResult> (
            string id,
            IAgentRunStore runStore,
            [FromServices] IAgentRunEventCompactor? compactor,
            IWorkspaceContextAccessor workspaceContextAccessor,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var workspaceId = ResolveWorkspaceId(workspaceContextAccessor, null);

            var run = await runStore.GetAsync(workspaceId, id, ct).ConfigureAwait(false);
            if (run is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "agents.runs.events.snapshot",
                    $"未找到 RunId='{id}'。");
            }

            if (compactor is null)
            {
                return ContextCoreHttpResultMapper.Misconfigured(
                    httpContext, string.Empty, "agents.runs.events.snapshot",
                    "IAgentRunEventCompactor 未注册到 DI 容器（仅 Postgres provider 支持事件流快照）。");
            }

            var snapshot = await compactor.GetSnapshotAsync(workspaceId, id, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "agents.runs.events.snapshot",
                    $"RunId='{id}' 尚未执行事件流压缩。");
            }

            return Results.Ok(snapshot);
        })
        .WithName("GetAgentRunEventSnapshot")
        .RequireWorkspaceRole(WorkspaceRole.Operator)
        .WithSummary("读取 Run 事件流压缩快照（未压缩时 404）")
        .Produces<AgentRunEventSnapshot>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>
    /// 按 Sequence 合并归档事件（折叠前缀）与热表事件（锚点 + 增量），供管理员 raw 事件
    /// 端点分页读取（P0-10 审计缺口修复：压缩后仍能看到完整事件历史）。
    /// </summary>
    /// <remarks>
    /// 两个输入各自按 Sequence 升序；归档（sequence &lt; 锚点）与热表（sequence ≥ 锚点）
    /// 天然不重叠。合并结果截断到 <paramref name="limit"/> 条（调用方用 limit = 页大小 + 1
    /// 判定 HasMore），输出保持 Sequence 升序保证游标分页连续性。
    /// </remarks>
    internal static void MergeRawEventStreams(
        IReadOnlyList<AgentRunEvent> archived,
        IReadOnlyList<AgentRunEvent> hot,
        int limit,
        List<AgentRunEvent> output)
    {
        ArgumentNullException.ThrowIfNull(archived);
        ArgumentNullException.ThrowIfNull(hot);
        ArgumentNullException.ThrowIfNull(output);
        if (limit <= 0)
        {
            return;
        }

        var i = 0;
        var j = 0;
        while (output.Count < limit && (i < archived.Count || j < hot.Count))
        {
            if (i >= archived.Count)
            {
                output.Add(hot[j++]);
            }
            else if (j >= hot.Count)
            {
                output.Add(archived[i++]);
            }
            else if (archived[i].Sequence <= hot[j].Sequence)
            {
                output.Add(archived[i++]);
            }
            else
            {
                output.Add(hot[j++]);
            }
        }
    }

    /// <summary>
    /// 统一视图读取：归档（折叠前缀）+ 热表（锚点 + 增量）按 Sequence 合并的单条读取路径。
    /// raw 审计端点与 SSE 补读共用，保证压缩后完整历史一致可见；
    /// 未注册压缩器（非 Postgres provider）时仅读热表。
    /// </summary>
    private static async Task<IReadOnlyList<AgentRunEvent>> ReadUnifiedEventsAsync(
        IAgentRunEventCompactor? compactor,
        IAgentRunEventStore eventStore,
        string workspaceId,
        string runId,
        int fromSequence,
        int take,
        CancellationToken ct)
    {
        if (compactor is null)
        {
            return await eventStore.ReadAsync(workspaceId, runId, fromSequence, take, ct)
                .ConfigureAwait(false);
        }

        var archived = await compactor.GetArchivedEventsAsync(
            workspaceId, runId, fromSequence, take, ct).ConfigureAwait(false);
        var hot = await eventStore.ReadAsync(workspaceId, runId, fromSequence, take, ct)
            .ConfigureAwait(false);
        var merged = new List<AgentRunEvent>(take);
        MergeRawEventStreams(archived, hot, take, merged);
        return merged;
    }

    /// <summary>
    /// 裁决成功后返回 202，并在 Run 无未裁决记录时立即重新入队（缩短对账完成到恢复执行的延迟）。
    /// </summary>
    private static async Task<IResult> ResolveAccepted(
        string workspaceId,
        string runId,
        string reconciliationId,
        IToolReconciliationStore? reconciliationStore,
        AgentKernelHost? host,
        HttpContext httpContext,
        CancellationToken ct)
    {
        // 仍有未裁决记录 → 保持停车，等待后续裁决全部完成后由 Worker 重新入队。
        if (reconciliationStore is not null
            && await reconciliationStore.HasUnresolvedForRunAsync(runId, ct).ConfigureAwait(false))
        {
            return Results.Accepted($"/api/agents/runs/{runId}");
        }

        // 全部裁决完成 → 立即重新入队（不等 Worker 轮询）。
        if (host is not null)
        {
            try
            {
                var run = await host.GetRunStatusAsync(workspaceId, runId, ct).ConfigureAwait(false);
                if (run is not null)
                {
                    // 非阻塞入队：队列满/关闭时不阻塞 HTTP 请求（RecoveryWorker 会重新入队执行）。
                    await host.TryEnqueueAsync(run, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                // 入队失败（Actor 依赖缺失）非致命：RecoveryWorker 会重新入队执行。
            }
        }

        return Results.Accepted($"/api/agents/runs/{runId}");
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
        IAgentRunEventCompactor? compactor,
        string workspaceId,
        string runId,
        int lastSequence,
        CancellationToken ct)
    {
        var fromSequence = lastSequence + 1;
        // 统一视图补读：归档（折叠前缀）+ 热表（锚点 + 增量）按 Sequence 合并，
        // 保证断线重连（Last-Event-ID 落在压缩锚点之前）时已归档事件不丢失。
        var events = await ReadUnifiedEventsAsync(
            compactor, eventStore, workspaceId, runId, fromSequence, SseReadBatchSize, ct)
            .ConfigureAwait(false);

        // 本轮最后一条事件 Sequence（用于推进 cursor，而非 DB 最新 sequence）
        var lastSentSequence = -1;
        foreach (var evt in events)
        {
            // SSE 序列化公开 DTO（隐藏 Tool 参数/结果、原始模型输出、异常堆栈等敏感信息）。
            // id: {sequence}
            // event: {eventType}
            // data: {json}
            // (空行结束事件)
            await writer.WriteLineAsync($"id: {evt.Sequence}").ConfigureAwait(false);
            await writer.WriteLineAsync($"event: {evt.EventType}").ConfigureAwait(false);
            await writer.WriteLineAsync($"data: {JsonSerializer.Serialize(ToPublicDto(evt))}").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            lastSentSequence = evt.Sequence;
        }

        // 每批次 Flush 一次（而非每条事件 Flush），减少系统调用开销
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
    /// 将 <see cref="AgentRunEvent"/> 映射为公开 DTO（隐藏敏感信息）。
    /// 仅保留 UI 显示进度/用量/状态所需的最小字段集：
    /// <list type="bullet">
    /// <item>Tool 名称（用于进度显示），但隐藏 Tool 参数与结果。</item>
    /// <item>模型 token 统计（用于用量显示），但隐藏原始模型输出。</item>
    /// <item>错误类别与短消息（用于状态显示），但隐藏完整异常堆栈。</item>
    /// </list>
    /// Payload 解析失败或字段缺失时静默降级（返回已知字段 + null 敏感字段），
    /// 不影响 SSE 流的连续性。原始 Payload 仅通过管理员审计端点（/events/raw）暴露。
    /// </summary>
    private static AgentRunEventPublicDto ToPublicDto(AgentRunEvent evt)
    {
        string? toolName = null;
        int? promptTokens = null;
        int? completionTokens = null;
        string? errorCategory = null;
        string? errorMessage = null;

        // Payload 为 JSON 字符串；解析失败时静默降级为 null 敏感字段
        if (!string.IsNullOrEmpty(evt.Payload))
        {
            try
            {
                using var doc = JsonDocument.Parse(evt.Payload);
                var root = doc.RootElement;

                switch (evt.EventType)
                {
                    case AgentRunEventType.ToolCallStarted:
                    case AgentRunEventType.ToolCallCompleted:
                    case AgentRunEventType.ObservationAppended:
                        toolName = TryGetString(root, "toolName");
                        break;

                    case AgentRunEventType.ModelCallCompleted:
                        // token 统计保留（UI 显示用量）；content（原始模型输出）与 toolCalls.arguments 隐藏
                        promptTokens = TryGetInt(root, "inputTokens");
                        completionTokens = TryGetInt(root, "outputTokens");
                        break;

                    case AgentRunEventType.RunFailed:
                        // reason 可能含异常堆栈 → 仅保留为短消息（ErrorCategory 标记为 failure）
                        errorCategory = "failure";
                        errorMessage = TryGetString(root, "reason");
                        break;
                }

                // ToolCallCompleted 失败时：提取 error 短消息 + 标记 errorCategory
                if (evt.EventType == AgentRunEventType.ToolCallCompleted
                    && root.TryGetProperty("succeeded", out var succeededProp)
                    && succeededProp.ValueKind == JsonValueKind.False)
                {
                    errorCategory = "tool_failed";
                    errorMessage = TryGetString(root, "error");
                }
            }
            catch (JsonException)
            {
                // Payload 损坏 → 静默降级（敏感字段保持 null）
            }
        }

        return new AgentRunEventPublicDto
        {
            EventType = evt.EventType.ToString(),
            RunId = evt.RunId,
            WorkspaceId = evt.WorkspaceId,
            Timestamp = evt.OccurredAt,
            RunState = evt.State.ToString(),
            ToolName = toolName,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            ErrorCategory = errorCategory,
            ErrorMessage = errorMessage
        };
    }

    /// <summary>从 JsonElement 安全读取字符串属性（缺失或类型不符时返回 null）。</summary>
    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>从 JsonElement 安全读取 int 属性（缺失或类型不符时返回 null）。</summary>
    private static int? TryGetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out var value)
                ? value
                : null;

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
            Priority = run.Priority,
            MaxRetries = run.MaxRetries,
            RetryCount = run.RetryCount,
            NextRetryAtUtc = run.NextRetryAtUtc,
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

    /// <summary>
    /// 创建并启动 AgentRun 的处理逻辑（internal static，供端点单元测试直接调用）。
    /// </summary>
    /// <remarks>
    /// P0-6/P0-7/P0-8 严格 Admission 语义（持久化 store 路径）：
    /// <list type="number">
    /// <item>以 PendingAdmission 状态原子创建（Claimer 永不领取；配额判定前 Run 永不进入可调度状态）；</item>
    /// <item>幂等重放（IdempotencyKey 命中）→ 200 OK；</item>
    /// <item>配额预留（TryConsumeAsync）失败 → 推进 AdmissionRejected（终态）→ <b>429</b>
    /// （请求不会被执行——AdmissionRejected 不在 Claimer 候选集，保留行仅作审计）；</item>
    /// <item>配额成功 → 推进 Queued → TryClaimSingleAsync 取得 Scheduler Claim Lease（P0-8）；</item>
    /// <item>入队成功 → <b>201</b>（已持久化并成功排入执行队列）；</item>
    /// <item>本地队列饱和（QueueFull/Closed）→ 释放 Scheduler Claim（回 Queued，其他节点/下周期接管）
    /// → <b>202</b>（已持久化、等待后台调度）；</item>
    /// <item>Claim 已被 Claimer 抢先取得 → <b>202</b>（本地不重复入队，避免双调度真源）。</item>
    /// </list>
    /// InMemory/FileSystem 路径保持 Created 流程（进程重启后数据即失，无需 Admission 状态机），
    /// 但 QueueFull 同样返回 202（Run 已持久化，语义不再与 429 混用）。
    /// </remarks>
    internal static async Task<IResult> CreateAgentRunHandlerAsync(
        CreateRunRequest request,
        IAgentRunStore runStore,
        AgentKernelHost? host,
        IWorkspaceContextAccessor workspaceContextAccessor,
        HttpContext httpContext,
        CancellationToken ct)
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

        // Atomic idempotent creation - eliminates TOCTOU race between check and create
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey;

        var runId = Guid.NewGuid().ToString("N");
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? $"session-{runId}"
            : request.SessionId!;
        var now = DateTimeOffset.UtcNow;

        // Build budgets (defaults when not provided)
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

        var timeoutSeconds = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 300;
        var allowedToolIds = request.ToolIds is { Count: > 0 } toolIds
            ? new HashSet<string>(toolIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        // P0-6 Admission 边界：持久化路径以 PendingAdmission 创建（Claimer 永不领取），
        // 配额预留成功才推进 Queued；InMemory/FileSystem 路径保持 Created（进程重启后数据即失，
        // 无需 Admission 状态机——Created 仍由 Actor 判定为全新启动）。
        var persistentStore = runStore as IPersistentAgentRunStore;

        // Tool 授权快照：冻结创建者当时被授予的工具与权限集（审批裁决与 Tool 派发前重复校验）。
        // 过期时间取 Run 执行截止时间——快照在 Run 的整个执行窗口内有效，不早于签发时间。
        var deadlineAt = now + TimeSpan.FromSeconds(timeoutSeconds);
        var authorizationSnapshot = BuildAuthorizationSnapshot(
            request,
            workspaceId,
            deadlineAt,
            workspaceContextAccessor?.Current,
            httpContext.RequestServices.GetService<SecurityOptions>(),
            httpContext.RequestServices.GetService<IToolAuthorizationPolicy>(),
            httpContext.RequestServices.GetService<IToolCatalog>(),
            httpContext.RequestServices.GetService<IToolDispatcher>(),
            now);

        var run = new AgentRun
        {
            RunId = runId,
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            Task = request.Task!,
            State = persistentStore is not null ? AgentRunState.PendingAdmission : AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = now,
            UpdatedAt = now,
            TurnBudget = turnBudget,
            CostBudget = costBudget,
            ModelArtifactId = string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId,
            AllowedToolIds = allowedToolIds,
            AuthorizationSnapshot = authorizationSnapshot,
            DeadlineAt = deadlineAt,
            ModelContextTokenBudget = 8192,
            IdempotencyKey = idempotencyKey,
            // B3 Durable Scheduler：优先级（高者先执行）+ Run 级重试预算（0 = 不重试）。
            // MaxRetries < 0 视为 0（非法负值收敛为"不重试"，与默认语义一致）。
            Priority = request.Priority,
            MaxRetries = request.MaxRetries > 0 ? request.MaxRetries : 0
        };

        AgentRunCreateResult createResult;
        try
        {
            createResult = await runStore.CreateOrGetByIdempotencyKeyAsync(run, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "agents.runs.create");
        }

        if (createResult.WasExisting)
        {
            // Idempotent replay: return existing run (200 OK)
            return Results.Ok(ToRunResponse(createResult.Run));
        }

        // Workspace 配额强制：仅新创建的 Run 预留配额（幂等重放不重复预留）。
        // 配额启用且服务已注册时，按 Run 预算（MaxTokens / MaxCostUsd）预留——reservationId 取 runId，
        // 幂等：同一 run 重复创建不重复占容量。预留失败（workspace 配额已耗尽）→ 429（中间件已做
        // 耗尽快路径，此处为权威预留点）。预留由 Run 取消时 Release（退回容量），实际执行后
        // 由结算方 Actualize 转正（按实际用量多退少补）。
        var securityOptions = httpContext.RequestServices.GetService<SecurityOptions>();
        var quotaService = httpContext.RequestServices.GetService<IWorkspaceQuotaService>();
        if (securityOptions is not null && securityOptions.Quota.Enabled && quotaService is not null)
        {
            var reservation = await quotaService.ReserveAsync(
                workspaceId,
                run.RunId,
                run.CostBudget?.MaxTokens ?? 0,
                run.CostBudget?.MaxCostUsd ?? 0,
                ct).ConfigureAwait(false);
            if (!reservation.Allowed)
            {
                // P0-6：配额判定失败 → 推进 AdmissionRejected（终态，持久化路径）。
                // 429 语义 = 请求未持久化、不会执行：Run 行虽保留（审计），但处于 Claimer
                // 永不领取的 AdmissionRejected 终态，任何后台调度都不会执行它——Admission 边界不再失效。
                if (persistentStore is not null)
                {
                    try
                    {
                        await persistentStore.TransitionStateAsync(
                            workspaceId, createResult.Run.RunId,
                            AgentRunState.PendingAdmission, AgentRunState.AdmissionRejected, ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // CAS 失败：状态已被并发路径推进（幂等重放/另一节点）→ 以现有状态为准，仍返回 429
                    }
                }
                return Results.Json(
                    new
                    {
                        error = "workspace_quota_exhausted",
                        message = reservation.FailureReason ?? "Workspace 配额已耗尽，无法创建新的 Agent Run。",
                        workspaceId
                    },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        // P0-6：配额预留成功 → 推进 Queued（持久化路径）——进入 Scheduler 可领取状态。
        // InMemory 路径无此步骤（Created 直接入队）。
        var admittedRun = createResult.Run;
        if (persistentStore is not null)
        {
            try
            {
                await persistentStore.TransitionStateAsync(
                    workspaceId, createResult.Run.RunId,
                    AgentRunState.PendingAdmission, AgentRunState.Queued, ct).ConfigureAwait(false);
                admittedRun = admittedRun with { State = AgentRunState.Queued };
            }
            catch (InvalidOperationException)
            {
                // 已被并发路径推进（幂等重放/另一节点）→ 读取最新状态，以存储事实为准
                admittedRun = await runStore.GetAsync(workspaceId, createResult.Run.RunId, ct).ConfigureAwait(false)
                              ?? admittedRun;
            }
        }

        // P0-8：入队前先取得 Scheduler Claim Lease（TryClaimSingleAsync）——防止 Claimer 在
        // 另一节点重复领取同一 Run（双调度真源）。领取成功 → 入队 → 201。
        var enqueueTarget = admittedRun;
        if (persistentStore is not null)
        {
            var (claimOwner, claimDuration) = ResolveSchedulerClaim(httpContext);
            var claimed = await persistentStore.TryClaimSingleAsync(
                workspaceId, admittedRun.RunId, claimOwner, claimDuration, ct).ConfigureAwait(false);
            if (claimed is not null)
            {
                enqueueTarget = claimed;
            }
            else
            {
                // 不可领取：已被 Claimer 抢先领取 / 状态已离开 Queued（并发取消等）。
                // 本地不重复入队（避免双调度真源）——Run 已持久化并入调度路径，由持有
                // Claim 的节点（或 claim 过期后的下一领取周期）负责执行 → 202。
                return Results.Accepted(
                    $"/api/agents/runs/{admittedRun.RunId}",
                    ToRunResponse(admittedRun));
            }
        }

        // 入队调度：成功 → 201（已持久化并成功排入执行队列）。
        // 本地队列饱和（QueueFull/Closed）→ 释放 Scheduler Claim（回 Queued，其他节点/
        // 下周期接管）→ 202（P0-7：202 = 已持久化、等待后台调度；429 仅保留给配额拒绝/未持久化）。
        var enqueue = await host.TryEnqueueAsync(enqueueTarget, CancellationToken.None).ConfigureAwait(false);
        if (enqueue.Status == AgentRunEnqueueStatus.QueueFull || enqueue.Status == AgentRunEnqueueStatus.Closed)
        {
            if (persistentStore is not null && enqueueTarget.ClaimToken is not null)
            {
                try
                {
                    await persistentStore.ReleaseClaimAsync(
                        workspaceId, enqueueTarget.RunId, enqueueTarget.ClaimToken, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // 释放失败非致命：claim 过期后由其他节点接管（fail-open 于调度路径，
                    // 不阻断 202 返回——Run 已持久化，由 Claimer 下周期或 claim 过期后接管）
                }
            }
            return Results.Accepted(
                $"/api/agents/runs/{enqueueTarget.RunId}",
                ToRunResponse(enqueueTarget));
        }

        return Results.Created($"/api/agents/runs/{enqueueTarget.RunId}", ToRunResponse(enqueueTarget));
    }

    /// <summary>
    /// 构建 Run 创建时的 Tool 授权快照：冻结创建者当时被授予的工具与权限集。
    /// 显式请求的 ToolIds 与主体可执行集求交；未指定时取 Tool Catalog / Dispatcher
    /// 全部已注册工具中主体可执行的子集。RBAC 未强制或无法解析主体上下文时按全量授权
    /// （与 RequireWorkspacePermission 的放行语义一致）。策略未注册时返回 null
    /// （旧路径，派发时仅受 AllowedToolIds 约束）。
    /// </summary>
    private static ToolAuthorizationSnapshot? BuildAuthorizationSnapshot(
        CreateRunRequest request,
        string workspaceId,
        DateTimeOffset expiresAt,
        WorkspaceContext? principal,
        SecurityOptions? securityOptions,
        IToolAuthorizationPolicy? policy,
        IToolCatalog? toolCatalog,
        IToolDispatcher? toolDispatcher,
        DateTimeOffset now)
    {
        if (policy is null)
        {
            return null;
        }

        // RBAC 未强制或无主体上下文 → 与端点放行语义一致，视为全量授权（信任部署方配置）。
        var rbacEnforced = securityOptions is { Rbac.Enforce: true } && principal is not null;
        var principalPermissions = rbacEnforced
            ? principal!.Permissions
            : WorkspacePermission.AdminAll;
        var principalId = principal?.ApiKeyId ?? workspaceId;

        // 候选工具集：显式请求；未指定时取 Catalog 定义 ∪ Dispatcher 支持集。
        IEnumerable<string> candidates;
        if (request.ToolIds is { Count: > 0 })
        {
            candidates = request.ToolIds.Where(t => !string.IsNullOrWhiteSpace(t));
        }
        else
        {
            var definitions = toolCatalog?.GetToolDefinitions().Select(d => d.Name) ?? Array.Empty<string>();
            IEnumerable<string> supported = (IEnumerable<string>?)toolDispatcher?.SupportedTools ?? Array.Empty<string>();
            candidates = definitions.Concat(supported);
        }

        var granted = new List<string>();
        var grantedPermissions = new List<string> { WorkspacePermission.AgentRun.ToString() };
        var coveredCapabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in candidates.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(tool))
            {
                continue;
            }

            var requirement = policy.GetRequirement(tool);
            if (requirement.RequiredCapability == WorkspacePermission.None)
            {
                // 基础工具：主体持 AgentRun 即可（无额外能力位）。
                if ((principalPermissions & WorkspacePermission.AgentRun) == WorkspacePermission.AgentRun)
                {
                    granted.Add(tool);
                }
                continue;
            }

            // 高危工具：主体必须持有对应能力位，否则不授予（杜绝低权限主体创建高权限 Tool Run）。
            if ((principalPermissions & requirement.RequiredCapability) != requirement.RequiredCapability)
            {
                continue;
            }

            granted.Add(tool);
            grantedPermissions.Add(requirement.ExecutePermissionId);
            grantedPermissions.Add(requirement.ApprovePermissionId);
            coveredCapabilities.Add(requirement.RequiredCapability.ToString());
        }

        grantedPermissions.AddRange(coveredCapabilities);

        return new ToolAuthorizationSnapshot
        {
            WorkspaceId = workspaceId,
            PrincipalId = principalId,
            GrantedToolIds = granted,
            GrantedPermissions = grantedPermissions,
            PolicyVersion = policy.PolicyVersion,
            IssuedAt = now,
            ExpiresAt = expiresAt
        };
    }

    /// <summary>
    /// 审批裁决前的 Tool 授权复核：快照未过期 + 策略版本一致 + 工具在授权集内，
    /// 且（RBAC 强制时）审批者持有该工具对应的能力位。任一不满足返回错误原因（null = 通过）。
    /// 快照为空（旧路径）时仅复核审批者能力。
    /// </summary>
    internal static async ValueTask<string?> CheckApprovalAuthorizationAsync(
        AgentRun run,
        AgentApproval approval,
        WorkspaceContext? approver,
        SecurityOptions? securityOptions,
        IToolAuthorizationPolicy? policy,
        IToolAuthorizer? toolAuthorizer,
        CancellationToken ct)
    {
        // 1. 快照有效性（存在快照时）。
        if (run.AuthorizationSnapshot is { } snapshot)
        {
            if (snapshot.ExpiresAt < DateTimeOffset.UtcNow)
            {
                return "Run 的 Tool 授权快照已过期，拒绝审批。";
            }

            if (policy is null || !string.Equals(snapshot.PolicyVersion, policy.PolicyVersion, StringComparison.Ordinal))
            {
                return "Tool 授权策略版本漂移，拒绝审批。";
            }

            if (!snapshot.GrantedToolIds.Contains(approval.ToolName))
            {
                return $"Tool '{approval.ToolName}' 不在 Run 授权快照的已授权工具集合中，拒绝审批。";
            }
        }

        // 2. 审批者能力（RBAC 强制时）：高危 Tool 的审批要求审批者持有对应能力位，
        // 不能由仅持 AgentRun 的低权限主体批准（审批解决"是否确认执行"，不解决"是否有权执行"）。
        if (securityOptions is { Rbac.Enforce: true })
        {
            if (approver is null)
            {
                return "无法解析审批者上下文，拒绝审批。";
            }

            if (policy is not null)
            {
                var requirement = policy.GetRequirement(approval.ToolName);
                if (requirement.RequiredCapability != WorkspacePermission.None
                    && (approver.Permissions & requirement.RequiredCapability) != requirement.RequiredCapability)
                {
                    return $"审批者缺少审批 Tool '{approval.ToolName}' 所需能力 {requirement.RequiredCapability}。";
                }
            }
            else if (toolAuthorizer is not null)
            {
                var authorization = await toolAuthorizer.AuthorizeAsync(approver, approval.ToolName, ct).ConfigureAwait(false);
                if (!authorization.IsAuthorized)
                {
                    return authorization.FailureReason ?? $"审批者无权限审批 Tool '{approval.ToolName}'。";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 解析 Scheduler Claim Lease 参数（P0-8）：Owner 优先取 AgentHostOptions.Owner，
    /// 否则生成 host-{MachineName}-{guid}；Duration 取 AgentHostOptions.SchedulerClaimDuration（默认 60s）。
    /// </summary>
    private static (string Owner, TimeSpan Duration) ResolveSchedulerClaim(HttpContext httpContext)
    {
        var options = httpContext.RequestServices.GetService<AgentHostOptions>();
        var duration = options is not null && options.SchedulerClaimDuration > TimeSpan.Zero
            ? options.SchedulerClaimDuration
            : TimeSpan.FromSeconds(60);
        var owner = options?.Owner;
        if (string.IsNullOrWhiteSpace(owner))
        {
            try
            {
                owner = $"host-{Environment.MachineName}-{Guid.NewGuid():N}";
            }
            catch
            {
                owner = $"host-{Guid.NewGuid():N}";
            }
        }
        return (owner, duration);
    }

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

    /// <summary>幂等键（可选；客户端提供用于去重，防止重试导致重复执行）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Cost 预算限制（可选；未提供时使用默认值）。</summary>
    public CostBudgetRequest? CostBudget { get; init; }

    /// <summary>执行超时（秒；默认 300）。</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>
    /// 调度优先级（可选；数值越大越先执行，默认 0）。
    /// Durable Scheduler 领取时按优先级倒序 + 创建时间升序排序。
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Run 级最大重试次数（可选；默认 0 = 不重试，失败保持 Failed 终态）。
    /// &gt; 0 时失败 Run 由 Durable Scheduler 自动重试（指数退避），达到上限后进入 DeadLettered 死信。
    /// </summary>
    public int MaxRetries { get; init; }
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

/// <summary>
/// SSE 公开事件 DTO。隐藏敏感数据（Tool 参数、原始模型输出、异常堆栈）。
/// </summary>
/// <remarks>
/// 仅暴露 UI 显示进度 / 用量 / 状态所需的最小字段集：
/// <list type="bullet">
/// <item><see cref="ToolName"/>：Tool 名称（进度显示），不含参数与结果。</item>
/// <item><see cref="PromptTokens"/> / <see cref="CompletionTokens"/>：模型 token 统计（用量显示），不含原始输出。</item>
/// <item><see cref="ErrorCategory"/> / <see cref="ErrorMessage"/>：错误类别与短消息（状态显示），不含完整堆栈。</item>
/// </list>
/// 完整 Payload（含 Tool 参数、模型输出、异常堆栈）仅通过管理员审计端点
/// <c>GET /api/agents/runs/{id}/events/raw</c> 暴露。
/// </remarks>
public sealed record AgentRunEventPublicDto
{
    /// <summary>事件类型（RunCreated / StateTransition / ModelCallStarted / ...）。</summary>
    public required string EventType { get; init; }

    /// <summary>所属 Run ID。</summary>
    public required string RunId { get; init; }

    /// <summary>Workspace ID（隔离边界）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>事件时间戳（UTC）。</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>事件发生时的 Run 状态快照。</summary>
    public required string RunState { get; init; }

    /// <summary>Tool 名称（仅 ToolCallStarted / ToolCallCompleted / ObservationAppended 事件填充；用于 UI 显示进度）。</summary>
    public string? ToolName { get; init; }

    /// <summary>模型输入 token 数（仅 ModelCallCompleted 事件填充；用于 UI 显示用量）。隐藏原始模型输出。</summary>
    public int? PromptTokens { get; init; }

    /// <summary>模型输出 token 数（仅 ModelCallCompleted 事件填充；用于 UI 显示用量）。隐藏原始模型输出。</summary>
    public int? CompletionTokens { get; init; }

    /// <summary>错误类别（仅失败事件填充；如 "failure" / "tool_failed"；用于 UI 显示状态）。隐藏完整异常堆栈。</summary>
    public string? ErrorCategory { get; init; }

    /// <summary>简短错误消息（仅失败事件填充；不含堆栈）。</summary>
    public string? ErrorMessage { get; init; }
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

    /// <summary>
    /// 客户端幂等键（可选）。重试提交同一决策时携带：与已存决策一致 → 幂等成功（200/202）；
    /// 相反决策 → 409 冲突。未提供时退化为旧语义（已裁决即冲突）。
    /// </summary>
    public string? DecisionRequestId { get; init; }
}

/// <summary>裁决 Tool 对账记录请求（POST /runs/{runId}/reconciliations/{id}/resolve）。</summary>
public sealed class ResolveReconciliationRequest
{
    /// <summary>
    /// 客户端决策幂等身份（客户端生成的唯一决策请求 ID，重试时保持不变）。
    /// 相同 DecisionRequestId + 相同 outcome 重试 → 幂等成功（202）；
    /// 相同 DecisionRequestId + 相反 outcome → 409 决策冲突；不携带或不同身份重复提交 → 409 已裁决。
    /// </summary>
    public string? DecisionRequestId { get; init; }

    /// <summary>外部副作用是否确实发生（true=已发生并提交真相结果；false=未发生，提交 void 并拒绝重放）。</summary>
    public bool SideEffectOccurred { get; init; }

    /// <summary>外部系统查得的真相结果（SideEffectOccurred=true 时填充）。</summary>
    public string? Result { get; init; }

    /// <summary>拒绝/失败原因（SideEffectOccurred=false 或无法确认时填充）。</summary>
    public string? Error { get; init; }
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

    /// <summary>调度优先级（数值越大越先执行；默认 0）。</summary>
    public int Priority { get; init; }

    /// <summary>Run 级最大重试次数（0 = 不重试；达到上限后进入 DeadLettered 死信）。</summary>
    public int MaxRetries { get; init; }

    /// <summary>已重试次数（每次重试重置递增）。</summary>
    public int RetryCount { get; init; }

    /// <summary>下一次可重试时间（退避门；null = 立即可领取）。</summary>
    public DateTimeOffset? NextRetryAtUtc { get; init; }

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
