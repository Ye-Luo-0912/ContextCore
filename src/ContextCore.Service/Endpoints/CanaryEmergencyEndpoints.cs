using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using Microsoft.AspNetCore.Mvc;

namespace ContextCore.Service.Endpoints;

// ===========================================================================
// Canary Emergency Kill Switch API（集群级紧急覆盖操作面）
//
// 目标（对齐集群级 Canary Kill Switch 契约）：
//   为运维提供触发 / 清除 / 查询集群级 Canary 紧急覆盖（Kill Switch）的 REST API。
//   紧急覆盖由 ICanaryEmergencyOverrideStore 持久化承载（Postgres 实现为
//   canary_emergency_overrides 表，每 run 至多一条活跃覆盖，跨进程重启仍生效）：
//     - 存在活跃覆盖时，路由层（AuthoritativeRuntime）强制回退 V1；
//     - CanaryProgressionService 在覆盖期间拒绝推进并标记非 Consistent，
//       直到运维显式清除。
//
// 端点：
//   POST /api/canary/emergency/{runId}/kill    触发紧急覆盖（Kill Switch）
//   POST /api/canary/emergency/{runId}/clear   清除紧急覆盖（恢复推进）
//   GET  /api/canary/emergency/overrides       查询全部活跃覆盖
//
// 设计原则：
//   1. 全部端点要求 Operator 角色（Kill Switch 属敏感运维操作）。
//   2. 处理器提取为 internal static 方法，可直接单元测试（DefaultHttpContext 执行 IResult）。
//   3. ICanaryEmergencyOverrideStore 未注册时返回 503（不静默降级）。
// ===========================================================================

/// <summary>
/// Canary 紧急 Kill Switch API 端点。
/// </summary>
internal static class CanaryEmergencyEndpoints
{
    private const string Tag = "CanaryEmergency";

    public static IEndpointRouteBuilder MapCanaryEmergencyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/canary/emergency").WithTags(Tag);

        // ── 触发紧急覆盖（Kill Switch）──────────────────────────────────
        group.MapPost("/{runId}/kill", KillAsync)
            .WithName("CanaryEmergencyKill")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("触发集群级 Canary 紧急覆盖：路由层强制回退 V1，Progression 暂停推进直到清除")
            .Produces<CanaryEmergencyOverrideResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 清除紧急覆盖（恢复推进）────────────────────────────────────
        group.MapPost("/{runId}/clear", ClearAsync)
            .WithName("CanaryEmergencyClear")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("清除集群级 Canary 紧急覆盖：恢复 Canary 推进与路由百分比")
            .Produces<CanaryEmergencyOverrideResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 查询活跃覆盖 ────────────────────────────────────────────────
        group.MapGet("/overrides", ListAsync)
            .WithName("CanaryEmergencyListOverrides")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("查询全部活跃的 Canary 紧急覆盖（Kill Switch 状态）")
            .Produces<CanaryEmergencyOverrideListResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>触发紧急覆盖：设置 runId 的活跃覆盖；已存在活跃覆盖时返回 409。</summary>
    internal static async Task<IResult> KillAsync(
        [FromServices] ICanaryEmergencyOverrideStore? store,
        string runId,
        KillCanaryOverrideRequest request,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (store is null)
        {
            return ContextCoreHttpResultMapper.StorageUnavailable(
                httpContext, string.Empty, "canary.emergency.kill",
                "ICanaryEmergencyOverrideStore 未注册（当前 profile 不支持紧急覆盖）。");
        }
        if (string.IsNullOrWhiteSpace(runId))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext, string.Empty, "canary.emergency.kill",
                "runId 不能为空。", field: "runId");
        }
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext, string.Empty, "canary.emergency.kill",
                "Reason（触发原因）为必填。", field: "reason");
        }

        var operatorName = string.IsNullOrWhiteSpace(request.OperatorName) ? "operator" : request.OperatorName!;
        var set = await store.TrySetOverrideAsync(runId, request.Reason, operatorName, ct).ConfigureAwait(false);
        if (!set)
        {
            return Conflict(
                httpContext, "canary.emergency.kill", $"run '{runId}'",
                $"run '{runId}' 已存在活跃紧急覆盖，拒绝重复触发（需先清除）。");
        }

        var active = await store.GetActiveAsync(runId, ct).ConfigureAwait(false);
        return Results.Ok(ToResponse(active ?? throw new InvalidOperationException(
            $"CanaryEmergencyOverride '{runId}' 设置成功但无法读取。")));
    }

    /// <summary>清除紧急覆盖：无活跃覆盖时返回 404；清除后返回被清除的覆盖记录。</summary>
    internal static async Task<IResult> ClearAsync(
        [FromServices] ICanaryEmergencyOverrideStore? store,
        string runId,
        string? operatorName,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (store is null)
        {
            return ContextCoreHttpResultMapper.StorageUnavailable(
                httpContext, string.Empty, "canary.emergency.clear",
                "ICanaryEmergencyOverrideStore 未注册（当前 profile 不支持紧急覆盖）。");
        }
        if (string.IsNullOrWhiteSpace(runId))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext, string.Empty, "canary.emergency.clear",
                "runId 不能为空。", field: "runId");
        }

        var operatorNameResolved = string.IsNullOrWhiteSpace(operatorName) ? "operator" : operatorName;
        var active = await store.GetActiveAsync(runId, ct).ConfigureAwait(false);
        if (active is null)
        {
            return ContextCoreHttpResultMapper.NotFound(
                httpContext, string.Empty, "canary.emergency.clear",
                $"run '{runId}' 无活跃紧急覆盖。");
        }

        var cleared = await store.TryClearOverrideAsync(runId, operatorNameResolved, ct).ConfigureAwait(false);
        if (!cleared)
        {
            return Conflict(
                httpContext, "canary.emergency.clear", $"run '{runId}'",
                $"run '{runId}' 的活跃覆盖已被并发清除，请刷新后重试。");
        }

        return Results.Ok(ToResponse(active with
        {
            ClearedAt = DateTimeOffset.UtcNow,
            ClearedBy = operatorNameResolved
        }));
    }

    /// <summary>查询全部活跃紧急覆盖。</summary>
    internal static async Task<IResult> ListAsync(
        [FromServices] ICanaryEmergencyOverrideStore? store,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (store is null)
        {
            return ContextCoreHttpResultMapper.StorageUnavailable(
                httpContext, string.Empty, "canary.emergency.list",
                "ICanaryEmergencyOverrideStore 未注册（当前 profile 不支持紧急覆盖）。");
        }

        var overrides = await store.GetActiveOverridesAsync(ct).ConfigureAwait(false);
        return Results.Ok(new CanaryEmergencyOverrideListResponse
        {
            Overrides = overrides.Select(ToResponse).ToArray(),
            Count = overrides.Count
        });
    }

    private static CanaryEmergencyOverrideResponse ToResponse(CanaryEmergencyOverride o) => new()
    {
        RunId = o.RunId,
        Reason = o.Reason,
        OperatorName = o.OperatorName,
        CreatedAt = o.CreatedAt,
        ClearedAt = o.ClearedAt,
        ClearedBy = o.ClearedBy,
        IsActive = o.ClearedAt is null
    };

    private static IResult Conflict(HttpContext httpContext, string operationId, string target, string message)
        => Results.Conflict(new ContextCoreErrorResponse
        {
            OperationId = operationId,
            ErrorCode = "CanaryEmergencyOverrideConflict",
            Message = message,
            Target = target,
            TraceId = httpContext.TraceIdentifier,
            Details = [],
            Warnings = []
        });
}

/// <summary>触发紧急覆盖请求。</summary>
public sealed class KillCanaryOverrideRequest
{
    /// <summary>触发原因（人工填写，如 "v2 返回 P95 恶化"）。</summary>
    public string? Reason { get; init; }

    /// <summary>触发人（运维账号；缺省为 "operator"）。</summary>
    public string? OperatorName { get; init; }
}

/// <summary>紧急覆盖响应。</summary>
public sealed class CanaryEmergencyOverrideResponse
{
    /// <summary>被 Kill 的 Canary run ID。</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>触发原因。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>触发人。</summary>
    public string OperatorName { get; init; } = string.Empty;

    /// <summary>触发时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>清除时间（null = 覆盖仍生效）。</summary>
    public DateTimeOffset? ClearedAt { get; init; }

    /// <summary>清除人（null = 尚未清除）。</summary>
    public string? ClearedBy { get; init; }

    /// <summary>是否仍为活跃覆盖。</summary>
    public bool IsActive { get; init; }
}

/// <summary>活跃覆盖列表响应。</summary>
public sealed class CanaryEmergencyOverrideListResponse
{
    /// <summary>活跃覆盖列表。</summary>
    public IReadOnlyList<CanaryEmergencyOverrideResponse> Overrides { get; init; } = Array.Empty<CanaryEmergencyOverrideResponse>();

    /// <summary>活跃覆盖数。</summary>
    public int Count { get; init; }
}
