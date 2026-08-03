using ContextCore.Abstractions;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using Microsoft.AspNetCore.Mvc;

namespace ContextCore.Service.Endpoints;

/// <summary>
/// Production Runtime Profile 端点：就绪检查与运行时状态报告。
/// <list type="bullet">
///   <item><c>/health/ready</c>：Production Runtime 就绪探针，检查 Worker 启动 / Postgres / Model Activation。</item>
///   <item><c>/api/runtime/status</c>：当前激活组件报告（Profile / Worker 列表 / Model / Transport / Canary）。</item>
///   <item><c>/api/runtime/migrations/coordinator/status</c>：HA 迁移协调器状态（阶段 / 实例 / 锁键 / 版本）。</item>
/// </list>
/// </summary>
internal static class ProductionRuntimeEndpoints
{
    /// <summary>映射 Production Runtime 端点。</summary>
    public static IEndpointRouteBuilder MapProductionRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        // ── /health/ready：Production Runtime 就绪探针 ────────────────────
        // 检查所有关键 Worker 是否已启动、Postgres 连接、Model Activation。
        // 返回 200 OK（就绪）或 503 Service Unavailable（未就绪）。
        app.MapGet("/health/ready", async Task<IResult> (
            ProductionRuntimeReadinessService readinessService,
            CancellationToken ct) =>
        {
            var result = await readinessService.CheckReadinessAsync(ct).ConfigureAwait(false);

            // 整体状态为 error 或 starting 时返回 503
            if (string.Equals(result.OverallStatus, "error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result.OverallStatus, "starting", StringComparison.OrdinalIgnoreCase))
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(result);
        })
        .WithTags("Health")
        .WithName("ProductionRuntimeReady")
        .WithSummary("Production Runtime 就绪探针（检查 Worker / Postgres / Model Activation）")
        .Produces<ProductionRuntimeReadinessResult>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        // ── /api/runtime/status：当前激活组件报告 ────────────────────────
        // 返回当前 Profile、已注册的 Worker 列表及状态、Model Activation 信息、
        // Agent Model Transport 状态、Tool Dispatcher 状态、Canary 状态。
        app.MapGet("/api/runtime/status", (ProductionRuntimeReadinessService readinessService) =>
        {
            var workers = readinessService.GetRegisteredWorkers();
            var modelActivation = readinessService.GetModelActivationStatus();
            var agentModelTransport = readinessService.GetAgentModelTransportStatus();
            var toolDispatcher = readinessService.GetToolDispatcherStatus();
            var canary = readinessService.GetCanaryStatus();

            return Results.Ok(new ProductionRuntimeStatusResponse
            {
                Profile = readinessService.Profile.ToString(),
                ApplicationStarted = readinessService.IsApplicationStarted,
                Workers = workers,
                ModelActivation = modelActivation,
                AgentModelTransport = agentModelTransport,
                ToolDispatcher = toolDispatcher,
                Canary = canary,
                CheckedAt = DateTimeOffset.UtcNow
            });
        })
        .WithTags("Status")
        .WithName("GetRuntimeStatus")
        .RequireWorkspaceRole(WorkspaceRole.Operator)
        .WithSummary("获取 Production Runtime 当前激活组件报告（Profile / Worker / Model / Transport / Canary）")
        .Produces<ProductionRuntimeStatusResponse>(StatusCodes.Status200OK);

        // ── /api/admission/status：生产准入状态（实时探针 + TTL 缓存）────────
        // 返回当前生产准入报告（静态强制项 + 实时探针）。
        // force=true 时忽略 TTL 强制刷新；该路径在请求阶段准入中间件的豁免列表中。
        app.MapGet("/api/admission/status", async Task<IResult> (
            ProductionAdmissionController admissionController,
            bool force = false,
            CancellationToken ct = default) =>
        {
            var report = await admissionController.GetOrRefreshAsync(force, ct).ConfigureAwait(false);
            return Results.Ok(new ProductionAdmissionStatusResponse(
                report.AdmissionRequired,
                report.AllPassed,
                report.Checks,
                report.CheckedAt,
                admissionController.ProbeInterval));
        })
        .WithTags("Status")
        .WithName("GetProductionAdmissionStatus")
        .RequireWorkspaceRole(WorkspaceRole.Operator)
        .WithSummary("获取生产准入状态报告（实时探针 + TTL 缓存；force=true 强制刷新）")
        .Produces<ProductionAdmissionStatusResponse>(StatusCodes.Status200OK);

        // ── /api/runtime/migrations/coordinator/status：HA 迁移协调器状态 ──
        // 返回协调器当前阶段（Idle/AcquiringLock/Migrating/UpToDate/Failed）、实例 ID、
        // 迁移互斥锁键、已应用版本 vs 代码版本、最近一次协调运行结果。
        // 非 Postgres provider 未注册协调器 → 返回 Enabled=false（200，不报错）。
        app.MapGet("/api/runtime/migrations/coordinator/status", async Task<IResult> (
            [FromServices] IMigrationCoordinator? coordinator,
            CancellationToken ct = default) =>
        {
            if (coordinator is null)
            {
                return Results.Ok(new MigrationCoordinatorStatus
                {
                    Phase = MigrationCoordinatorPhase.Idle,
                    Enabled = false,
                    InstanceId = string.Empty,
                    LockKey = 0,
                    UpToDate = false,
                    LastRunSucceeded = false,
                    Note = "迁移协调器仅 Postgres provider 注册（HA 多实例单执行者）。"
                });
            }

            var status = await coordinator.GetStatusAsync(ct).ConfigureAwait(false);
            return Results.Ok(status);
        })
        .WithTags("Status")
        .WithName("GetMigrationCoordinatorStatus")
        .RequireWorkspaceRole(WorkspaceRole.Operator)
        .WithSummary("获取 HA 迁移协调器状态（阶段 / 实例 / 锁键 / 已应用版本 / 最近运行结果）")
        .Produces<MigrationCoordinatorStatus>(StatusCodes.Status200OK);

        return app;
    }
}

// ── 端点响应模型 ─────────────────────────────────────────────────────────

/// <summary>/api/runtime/status 端点响应。</summary>
internal sealed class ProductionRuntimeStatusResponse
{
    /// <summary>当前 RuntimeProfile 名称。</summary>
    public required string Profile { get; init; }

    /// <summary>应用是否已启动（所有 HostedService.StartAsync 已完成）。</summary>
    public required bool ApplicationStarted { get; init; }

    /// <summary>已注册的 Worker 列表及状态。</summary>
    public required IReadOnlyList<WorkerStatus> Workers { get; init; }

    /// <summary>Model Activation 状态（null = 未启用）。</summary>
    public ModelActivationStatus? ModelActivation { get; init; }

    /// <summary>Agent Model Transport 状态（Deterministic vs Real，null = 未注册）。</summary>
    public AgentModelTransportStatus? AgentModelTransport { get; init; }

    /// <summary>Tool Dispatcher 状态（Echo vs Real，null = 未注册）。</summary>
    public ToolDispatcherStatus? ToolDispatcher { get; init; }

    /// <summary>Canary 状态。</summary>
    public required CanaryStatus Canary { get; init; }

    /// <summary>检查时间（UTC）。</summary>
    public required DateTimeOffset CheckedAt { get; init; }
}

/// <summary>/api/admission/status 端点响应。</summary>
internal sealed record ProductionAdmissionStatusResponse(
    bool AdmissionRequired,
    bool AllPassed,
    IReadOnlyList<ProductionAdmissionCheck> Checks,
    DateTimeOffset CheckedAt,
    TimeSpan ProbeInterval);
