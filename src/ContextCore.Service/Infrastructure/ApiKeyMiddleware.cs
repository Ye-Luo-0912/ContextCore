using ContextCore.Abstractions;
using ContextCore.Service.Security;

namespace ContextCore.Service.Infrastructure;

/// <summary>
/// 最小 API Key 认证中间件。
/// 在 RequireApiKey=true 时，所有非 PublicPaths 路径的请求必须携带正确的 X-ContextCore-Key（或配置的自定义头）。
/// 支持 API Key 轮换：当 Security:ApiKeyRotation:EnableStaticKeyRotation=true 且 IApiKeyStore 已注册时，
/// 优先通过 IApiKeyStore 校验（含轮换过渡期的 Secondary key）；否则回退到静态字符串比对。
/// 认证成功后将 ApiKeyId 写入 HttpContext.Items[ApiKeyIdItemsKey]，供下游 WorkspaceContextMiddleware 读取。
/// </summary>
public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityOptions _options;
    private readonly IApiKeyStore? _apiKeyStore;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    /// <summary>HttpContext.Items 中存储 ApiKeyId 的键（供 WorkspaceContextMiddleware 读取）。</summary>
    public const string ApiKeyIdItemsKey = "__ContextCore_ApiKeyId";

    /// <summary>HttpContext.Items 中标记是否通过静态 API Key 认证的键（供 RBAC 解析角色时区分）。</summary>
    public const string StaticApiKeyItemsKey = "__ContextCore_StaticApiKey";

    public ApiKeyMiddleware(
        RequestDelegate next,
        SecurityOptions options,
        ILogger<ApiKeyMiddleware> logger,
        IApiKeyStore? apiKeyStore = null)
    {
        _next = next;
        _options = options;
        _logger = logger;
        _apiKeyStore = apiKeyStore;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.RequireApiKey || IsPublicPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!context.Request.Headers.TryGetValue(_options.ApiKeyHeaderName, out var providedKey)
            || string.IsNullOrWhiteSpace(providedKey))
        {
            _logger.LogWarning(
                "未授权的 API 请求：{Method} {Path}（缺少 {Header}）",
                context.Request.Method,
                context.Request.Path,
                _options.ApiKeyHeaderName);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized: 请在请求头中提供有效的 API Key。").ConfigureAwait(false);
            return;
        }

        var providedKeyStr = providedKey.ToString();

        // 优先通过 IApiKeyStore 校验（支持轮换 + workspace 绑定）
        if (_apiKeyStore is not null && _options.ApiKeyRotation.EnableStaticKeyRotation)
        {
            var result = await _apiKeyStore.ValidateAsync(providedKeyStr, context.RequestAborted)
                .ConfigureAwait(false);
            if (!result.IsValid)
            {
                _logger.LogWarning(
                    "未授权的 API 请求：{Method} {Path}（{Reason}）",
                    context.Request.Method,
                    context.Request.Path,
                    result.FailureReason ?? "API Key 校验失败");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized: " + (result.FailureReason ?? "API Key 无效。"))
                    .ConfigureAwait(false);
                return;
            }

            // 将 API Key 身份写入 Items，供下游 WorkspaceContextMiddleware 读取
            if (!string.IsNullOrEmpty(result.ApiKeyId))
            {
                context.Items[ApiKeyIdItemsKey] = result.ApiKeyId;
            }

            // 同时写入 workspaceId（若 API Key 元数据中携带）
            if (!string.IsNullOrEmpty(result.WorkspaceId))
            {
                context.Items[SecurityServiceCollectionExtensions.WorkspaceContextItemsKey] = result.WorkspaceId;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        // 回退路径：静态字符串比对（向后兼容）
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

        if (!string.Equals(providedKeyStr, _options.ApiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "未授权的 API 请求：{Method} {Path}（错误的 {Header}）",
                context.Request.Method,
                context.Request.Path,
                _options.ApiKeyHeaderName);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized: 请在请求头中提供有效的 API Key。").ConfigureAwait(false);
            return;
        }

        // 静态 API Key 认证通过：标记为静态 key（RBAC 服务据此分配 RolesForStaticApiKey 角色）
        context.Items[StaticApiKeyItemsKey] = true;

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
