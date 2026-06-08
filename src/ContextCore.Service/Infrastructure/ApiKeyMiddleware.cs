namespace ContextCore.Service.Infrastructure;

/// <summary>
/// 最小 API Key 认证中间件。
/// 在 RequireApiKey=true 时，所有非 PublicPaths 路径的请求必须携带正确的 X-ContextCore-Key（或配置的自定义头）。
/// </summary>
public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityOptions _options;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(
        RequestDelegate next,
        SecurityOptions options,
        ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.RequireApiKey || IsPublicPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            // 服务端未配置密钥：写接口全部拒绝，避免无人守护的裸奔
            _logger.LogWarning(
                "API Key 校验已启用但服务端未配置 Security:ApiKey。请求被拒绝：{Path}",
                context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("服务端未配置 API Key，请联系管理员。").ConfigureAwait(false);
            return;
        }

        if (!context.Request.Headers.TryGetValue(_options.ApiKeyHeaderName, out var providedKey)
            || !string.Equals(providedKey, _options.ApiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "未授权的 API 请求：{Method} {Path}（缺少或错误的 {Header}）",
                context.Request.Method,
                context.Request.Path,
                _options.ApiKeyHeaderName);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized: 请在请求头中提供有效的 API Key。").ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private bool IsPublicPath(PathString path)
    {
        foreach (var publicPath in _options.PublicPaths)
        {
            if (string.Equals(path.Value, publicPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (publicPath.Length > 1
                && path.StartsWithSegments(publicPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
