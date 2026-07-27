using System.Diagnostics;
using ContextCore.Abstractions;

namespace ContextCore.Service.Infrastructure;

/// <summary>
/// 请求级审计日志中间件，记录 requestId、caller、endpoint、workspaceId、operationKind、statusCode、duration 等。
/// 不记录请求/响应正文，不记录 API Key 内容。
/// workspaceId 优先从 IWorkspaceContextAccessor（由 WorkspaceContextMiddleware 填充）读取，
/// 回退到查询字符串 workspaceId 参数（向后兼容）。
/// </summary>
public sealed class AuditLogMiddleware
{
    private static readonly HashSet<string> SkipPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/health", "/favicon.ico",
    };

    private static readonly string[] SkipPrefixes =
    [
        "/api/health/",
    ];

    private readonly RequestDelegate _next;
    private readonly SecurityOptions _security;
    private readonly ILogger<AuditLogMiddleware> _logger;
    private readonly ContextCoreMetrics _metrics;
    private readonly IWorkspaceContextAccessor? _workspaceContextAccessor;

    public AuditLogMiddleware(
        RequestDelegate next,
        SecurityOptions security,
        ILogger<AuditLogMiddleware> logger,
        ContextCoreMetrics metrics,
        IWorkspaceContextAccessor? workspaceContextAccessor = null)
    {
        _next = next;
        _security = security;
        _logger = logger;
        _metrics = metrics;
        _workspaceContextAccessor = workspaceContextAccessor;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 跳过不需要记录的路径（OpenAPI 文档、Scalar UI、健康检查）
        var path = context.Request.Path.Value ?? string.Empty;
        if (ShouldSkip(path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var requestId = Activity.Current?.Id ?? context.TraceIdentifier;
        var sw = Stopwatch.StartNew();

        // 注入 RequestId 到响应头，方便调用方关联日志
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-ContextCore-RequestId"] = requestId;
            return Task.CompletedTask;
        });

        string? errorMessage = null;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errorMessage = ex.GetType().Name + ": " + ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            var statusCode = context.Response.StatusCode;
            var workspaceId = ExtractWorkspaceId(context);
            var caller = GetCaller(context);
            var apiKeyName = ExtractApiKeyName(context);

            if (statusCode >= 400)
            {
                _logger.LogWarning(
                    "[审计] {Method} {Path} → {StatusCode} | reqId={RequestId} | caller={Caller} | workspace={WorkspaceId} | apiKey={ApiKeyName} | duration={Duration}ms | error={Error}",
                    context.Request.Method,
                    path,
                    statusCode,
                    requestId,
                    caller,
                    workspaceId ?? "-",
                    apiKeyName ?? "-",
                    sw.ElapsedMilliseconds,
                    errorMessage ?? "-");
            }
            else
            {
                _logger.LogInformation(
                    "[审计] {Method} {Path} → {StatusCode} | reqId={RequestId} | caller={Caller} | workspace={WorkspaceId} | apiKey={ApiKeyName} | duration={Duration}ms",
                    context.Request.Method,
                    path,
                    statusCode,
                    requestId,
                    caller,
                    workspaceId ?? "-",
                    apiKeyName ?? "-",
                    sw.ElapsedMilliseconds);
            }

            _metrics.RecordRequest(sw.Elapsed.TotalMilliseconds, statusCode, context.Request.Method, path);
        }
    }

    /// <summary>
    /// 提取 workspaceId：优先从 IWorkspaceContextAccessor（已由 WorkspaceContextMiddleware 填充）读取，
    /// 回退到查询字符串 workspaceId 参数（向后兼容旧实现）。
    /// </summary>
    private string? ExtractWorkspaceId(HttpContext context)
    {
        // 优先从 WorkspaceContext 读取（包含 header / API Key 元数据 / 查询字符串解析结果）
        if (_workspaceContextAccessor?.Current is { } workspaceContext
            && !string.IsNullOrWhiteSpace(workspaceContext.WorkspaceId))
        {
            return workspaceContext.WorkspaceId;
        }

        // 回退：查询字符串 workspaceId 参数
        if (context.Request.Query.TryGetValue("workspaceId", out var ws))
        {
            return ws.FirstOrDefault();
        }

        return null;
    }

    /// <summary>从 WorkspaceContext 提取 API Key 显示名（不暴露 key 本身）。</summary>
    private string? ExtractApiKeyName(HttpContext context)
    {
        return _workspaceContextAccessor?.Current?.ApiKeyName;
    }

    /// <summary>返回调用方标识，优先取 RemoteIp；不暴露 API Key 内容。</summary>
    private string GetCaller(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // 不记录 API Key 值，只记录是否存在
        var hasKey = context.Request.Headers.ContainsKey(_security.ApiKeyHeaderName);
        return hasKey ? $"{ip}(key)" : ip;
    }

    private static bool ShouldSkip(string path)
    {
        if (SkipPaths.Contains(path))
        {
            return true;
        }

        foreach (var prefix in SkipPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase);
    }
}
