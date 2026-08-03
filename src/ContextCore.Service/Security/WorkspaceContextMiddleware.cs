using System.Threading;
using ContextCore.Abstractions;
using ContextCore.Service.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Service.Security;

// ===========================================================================
// WorkspaceContextAccessor + WorkspaceContextMiddleware
//
// IWorkspaceContextAccessor：基于 AsyncLocal<WorkspaceContext?> 实现 Scoped 上下文。
// WorkspaceContextMiddleware：从请求头 / API Key 元数据解析 workspace_id 与角色，
// 填充到 accessor，并在请求结束时清理 AsyncLocal。
//
// 中间件链顺序（重要）：
// ApiKeyMiddleware → WorkspaceContextMiddleware → AuditLogMiddleware → Endpoint
// ApiKeyMiddleware 已校验 API Key（含轮换支持，见 InMemoryApiKeyStore），
// WorkspaceContextMiddleware 基于已认证的 ApiKeyId 解析 workspace 与角色。
// ===========================================================================

/// <summary>
/// 基于 AsyncLocal 的 Workspace 上下文访问器（Scoped）。
/// </summary>
public sealed class WorkspaceContextAccessor : IWorkspaceContextAccessor
{
    private static readonly AsyncLocal<WorkspaceContext?> CurrentContext = new();

    /// <inheritdoc />
    public WorkspaceContext? Current => CurrentContext.Value;

    /// <inheritdoc />
    public void Set(WorkspaceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CurrentContext.Value = context;
    }

    /// <inheritdoc />
    public void Clear() => CurrentContext.Value = null;
}

/// <summary>
/// Workspace 上下文中间件。从请求头 / API Key 元数据解析 workspace_id 与角色。
/// 必须放在 ApiKeyMiddleware 之后（依赖 ApiKeyMiddleware 已通过 Headers[ApiKeyHeaderName] 校验 API Key）。
/// </summary>
public sealed class WorkspaceContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityOptions _options;
    private readonly IApiKeyStore? _apiKeyStore;
    private readonly IWorkspaceRbacService _rbacService;
    private readonly ILogger<WorkspaceContextMiddleware> _logger;

    public WorkspaceContextMiddleware(
        RequestDelegate next,
        SecurityOptions options,
        IWorkspaceRbacService rbacService,
        ILogger<WorkspaceContextMiddleware> logger,
        IApiKeyStore? apiKeyStore = null)
    {
        _next = next;
        _options = options;
        _rbacService = rbacService;
        _logger = logger;
        _apiKeyStore = apiKeyStore;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // IWorkspaceContextAccessor 为 Scoped 服务；.NET 10 起约定式中间件在启动时
        // 从根容器解析构造函数依赖（ApplicationBuilder.Build 阶段），因此这里改为
        // 请求期从 RequestServices 解析（与 endpoint filter 一致），避免启动失败。
        var contextAccessor = context.RequestServices.GetService<IWorkspaceContextAccessor>();
        var workspaceId = ResolveWorkspaceId(context, out var source);
        var apiKeyId = ResolveApiKeyId(context);

        // 如果启用了显式 workspace 要求但请求未提供，返回 400
        if (_options.Workspace.RequireExplicitWorkspace && string.IsNullOrWhiteSpace(workspaceId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(
                $"Bad Request: 必须在请求头 '{_options.Workspace.WorkspaceIdHeaderName}' 或 API Key 元数据中提供 workspace_id。")
                .ConfigureAwait(false);
            return;
        }

        // 缺失时回退到默认 workspace（向后兼容）
        workspaceId = string.IsNullOrWhiteSpace(workspaceId)
            ? _options.Workspace.DefaultWorkspaceId
            : workspaceId;

        // 解析角色：通过 API Key 元数据或默认配置
        var roles = await _rbacService.ResolveRolesAsync(apiKeyId, workspaceId, context.RequestAborted)
            .ConfigureAwait(false);

        var apiKeyEntry = await ResolveApiKeyEntryAsync(apiKeyId).ConfigureAwait(false);

        var ctx = new WorkspaceContext
        {
            WorkspaceId = workspaceId,
            Source = source,
            ApiKeyId = apiKeyId,
            ApiKeyName = apiKeyEntry?.Name,
            Roles = roles,
            IsAuthenticated = apiKeyId is not null || !_options.RequireApiKey
        };

        contextAccessor?.Set(ctx);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            contextAccessor?.Clear();
        }
    }

    /// <summary>从请求头 / 查询字符串 / API Key 元数据中解析 workspace_id。</summary>
    private string ResolveWorkspaceId(HttpContext context, out string source)
    {
        // 优先级 1：请求头 X-ContextCore-Workspace
        if (context.Request.Headers.TryGetValue(_options.Workspace.WorkspaceIdHeaderName, out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue))
        {
            source = "header";
            return headerValue.ToString();
        }

        // 优先级 2：查询字符串 workspaceId（GET 请求常见，向后兼容 AuditLogMiddleware 旧实现）
        if (context.Request.Query.TryGetValue("workspaceId", out var queryValue)
            && !string.IsNullOrWhiteSpace(queryValue))
        {
            source = "query";
            return queryValue.ToString();
        }

        // 优先级 3：API Key 元数据中的 workspaceId（由 ApiKeyMiddleware 已设置 Items["ApiKeyId"]）
        // 此处仅返回空字符串占位；实际解析发生在 ResolveRolesAsync 中（需要查询 IApiKeyStore）
        source = "default";
        return string.Empty;
    }

    /// <summary>从 HttpContext.Items 获取 ApiKeyMiddleware 写入的 ApiKeyId（若有）。</summary>
    private string? ResolveApiKeyId(HttpContext context)
    {
        return context.Items.TryGetValue(ApiKeyMiddleware.ApiKeyIdItemsKey, out var value)
            && value is string apiKeyId
            && !string.IsNullOrWhiteSpace(apiKeyId)
                ? apiKeyId
                : null;
    }

    /// <summary>查询 API Key 元数据（用于填充 ApiKeyName，便于审计日志）。</summary>
    private async Task<ApiKeyEntry?> ResolveApiKeyEntryAsync(string? apiKeyId)
    {
        if (string.IsNullOrWhiteSpace(apiKeyId) || _apiKeyStore is null)
        {
            return null;
        }

        try
        {
            return await _apiKeyStore.GetByIdAsync(apiKeyId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // API Key 元数据查询失败不应阻塞请求；记录警告后继续
            _logger.LogWarning(ex, "查询 API Key 元数据失败：ApiKeyId={ApiKeyId}", apiKeyId);
            return null;
        }
    }
}
