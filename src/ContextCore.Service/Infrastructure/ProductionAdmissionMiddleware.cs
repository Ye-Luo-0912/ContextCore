using System.Text.Json;
using ContextCore.Abstractions;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Infrastructure;

// ===========================================================================
// 请求阶段生产准入门中间件
//
// 目标：
// ProductionHA 下，每个请求（除豁免路径外）在执行前检查当前生产准入报告；
// 未通过时返回 503，防止启动后运行时降级（Postgres 断连 / Model Slot 停用 /
// 应用重启窗口）仍静默放行业务流量。
//
// 设计决策：
// - 仅 ProductionHA 生效；其他 Profile 直接透传（validator 返回 Skipped + AllPassed=true）。
// - 位于 AuditLogMiddleware 之后：被拒绝的请求仍经过认证与审计（安全可追踪）。
// - 豁免路径（探活 / 运维诊断，不被准入门阻断）：
// /health*、/api/health*、/api/runtime/status、/api/admission/status、/openapi*、/scalar*。
// - 复用 ProductionAdmissionController 的 TTL 缓存，大部分请求零额外 IO。
// ===========================================================================

/// <summary>
/// 请求阶段生产准入门中间件：ProductionHA 下准入未通过时对业务请求返回 503。
/// </summary>
public sealed class ProductionAdmissionMiddleware
{
    private static readonly string[] _exemptPrefixes =
    [
        "/health",
        "/api/health",
        "/api/runtime/status",
        "/api/admission/status",
        "/openapi",
        "/scalar"
    ];

    private readonly RequestDelegate _next;
    private readonly ProductionAdmissionController _controller;
    private readonly ContextCoreRuntimeOptions _runtimeOptions;
    private readonly ILogger<ProductionAdmissionMiddleware> _logger;

    /// <summary>构造函数。</summary>
    public ProductionAdmissionMiddleware(
        RequestDelegate next,
        ProductionAdmissionController controller,
        ContextCoreRuntimeOptions runtimeOptions,
        ILogger<ProductionAdmissionMiddleware> logger)
    {
        _next = next;
        _controller = controller;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    /// <summary>请求处理入口。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (_runtimeOptions.Profile != RuntimeProfile.ProductionHA
            || IsExemptPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var report = await _controller.GetOrRefreshAsync(forceRefresh: false, context.RequestAborted)
            .ConfigureAwait(false);
        if (report.AdmissionRequired && !report.AllPassed)
        {
            var failedNames = report.Checks
                .Where(c => c.Status == ProductionAdmissionCheckStatus.Fail)
                .Select(c => c.Name)
                .ToArray();
            _logger.LogWarning(
                "请求阶段准入拒绝 {Method} {Path}（{FailedCount} 项实时检查未通过：{FailedNames}）。",
                context.Request.Method,
                context.Request.Path,
                failedNames.Length,
                string.Join(",", failedNames));

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                new
                {
                    status = "admission-denied",
                    reason = "生产准入未通过——运行时强制项存在失败，请求被拒绝。",
                    failedChecks = failedNames,
                    checkedAtUtc = report.CheckedAt
                },
                JsonSerializerOptions.Web)).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsExemptPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var prefix in _exemptPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
