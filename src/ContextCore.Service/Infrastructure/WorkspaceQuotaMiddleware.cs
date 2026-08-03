using ContextCore.Abstractions;
using ContextCore.Service.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Service.Infrastructure;

// ===========================================================================
// WorkspaceQuotaMiddleware — workspace 级配额强制（请求阶段快路径）
//
// 目标：请求阶段强制 workspace 配额（Security:Quota:Enabled=true 时生效）。
//   - 快路径：POST /api/agents/runs 前检查 workspace 配额是否已耗尽；
//     已耗尽 → 429（不进入处理管道，节省下游资源）；
//   - 权威扣减：在 AgentExecutionEndpoints.CreateAgentRunHandlerAsync 中通过
//     IWorkspaceQuotaService.TryConsumeAsync 预留 Run 预算（配额实际扣减点，
//     与 Run 创建绑定，避免中间件与业务双重扣减）；
//   - 中间件只做"已耗尽"门禁，不做扣减。
//
// 设计决策：
//   - 仅配额启用（SecurityOptions.Quota.Enabled）且命中配额边界路径时拦截；
//   - workspaceId 从 HttpContext.Items 读取（由 WorkspaceContextMiddleware 填充），
//     未解析到 workspace 时放行（由端点按 RBAC / fallback 逻辑处理）；
//   - 配额服务未注册时放行（未配置配额即无强制；启用配额但服务缺失属配置错误，
//     由 ProductionAdmission / Readiness 探针暴露为 error）。
// ===========================================================================

/// <summary>
/// 请求阶段 workspace 配额门禁中间件。
/// 配额已耗尽时对创建 Agent Run 的请求返回 429，避免进入执行管道。
/// </summary>
public sealed class WorkspaceQuotaMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityOptions _securityOptions;
    private readonly ILogger<WorkspaceQuotaMiddleware> _logger;

    /// <summary>构造函数。</summary>
    public WorkspaceQuotaMiddleware(
        RequestDelegate next,
        SecurityOptions securityOptions,
        ILogger<WorkspaceQuotaMiddleware> logger)
    {
        _next = next;
        _securityOptions = securityOptions;
        _logger = logger;
    }

    /// <summary>请求处理入口。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // 配额未启用 / 非配额边界路径 → 放行
        if (!_securityOptions.Quota.Enabled || !IsQuotaBoundRequest(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // workspaceId 由 WorkspaceContextMiddleware 写入 HttpContext.Items
        var workspaceId = context.Items.TryGetValue(SecurityServiceCollectionExtensions.WorkspaceContextItemsKey, out var v)
            && v is string ws && !string.IsNullOrWhiteSpace(ws)
                ? ws
                : null;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var quotaService = context.RequestServices.GetService<IWorkspaceQuotaService>();
        if (quotaService is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var quota = await quotaService.GetQuotaAsync(workspaceId, context.RequestAborted).ConfigureAwait(false);
        if ((quota.MaxTokens > 0 && quota.IsTokenExhausted)
            || (quota.MaxCostUsd > 0 && quota.IsCostExhausted))
        {
            _logger.LogWarning(
                "Workspace {WorkspaceId} 配额已耗尽（Tokens={TokensUsed}/{MaxTokens}, Cost={CostUsed:F2}/{MaxCost:F2} USD），拒绝创建 Agent Run。",
                workspaceId, quota.TokensUsed, quota.MaxTokens, quota.CostUsedUsd, quota.MaxCostUsd);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "workspace_quota_exhausted",
                message = "Workspace 配额已耗尽，无法创建新的 Agent Run。",
                workspaceId,
                tokensUsed = quota.TokensUsed,
                maxTokens = quota.MaxTokens,
                costUsedUsd = quota.CostUsedUsd,
                maxCostUsd = quota.MaxCostUsd
            }, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>配额边界路径：创建 Agent Run 的 POST 请求。</summary>
    private static bool IsQuotaBoundRequest(HttpRequest request)
        => request.Method == HttpMethods.Post
           && request.Path.StartsWithSegments("/api/agents/runs");
}
