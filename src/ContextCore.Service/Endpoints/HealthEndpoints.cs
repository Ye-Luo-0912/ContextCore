using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>
/// 标准化健康检查端点。
/// <list type="bullet">
///   <item><c>/api/health/live</c>：存活探针，进程在线即返回 200，不检查依赖。</item>
///   <item><c>/api/health/ready</c>：Service Alpha 运行时探针，检查核心存储链路和运行基线。</item>
/// </list>
/// </summary>
internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health/live", () => Results.Ok(new ContextCoreHealthLiveResponse
        {
            Status = "alive",
            Utc = DateTimeOffset.UtcNow
        }))
        .WithTags("Health")
        .WithName("HealthLive")
        .WithSummary("存活探针（进程在线即 200，不检查依赖）");

        app.MapGet("/api/health/ready", async Task<IResult> (
            ServiceAlphaRuntimeInspector inspector,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var snapshot = await inspector.GetReadySnapshotAsync(cancellationToken: ct).ConfigureAwait(false);
            var hasError = snapshot.Checks.Any(check => string.Equals(check.Status, "error", StringComparison.OrdinalIgnoreCase));
            if (hasError)
            {
                return ContextCoreHttpResultMapper.StorageUnavailable(
                    httpContext,
                    string.Empty,
                    "health.ready",
                    snapshot.Message);
            }

            return Results.Ok(ToRuntimeReadinessResponse(snapshot));
        })
        .WithTags("Health")
        .WithName("HealthReady")
        .WithSummary("Service Alpha 就绪探针（检查存储、事件、作业、模型和 retrieval baseline）");

        return app;
    }

    private static RuntimeReadinessResponse ToRuntimeReadinessResponse(ServiceAlphaRuntimeSnapshot snapshot)
    {
        return new RuntimeReadinessResponse
        {
            Status = NormalizeStatus(snapshot.State),
            Message = snapshot.Message,
            CheckedAt = snapshot.CheckedAt,
            StorageProvider = snapshot.StorageProvider,
            ProductionReady = snapshot.ProductionReady,
            ProviderState = snapshot.ProviderState,
            RetrievalBaseline = snapshot.RetrievalBaseline,
            FromCache = snapshot.FromCache,
            CacheTtlSeconds = snapshot.CacheTtlSeconds,
            ProbeScope = snapshot.ProbeScope,
            Capabilities = snapshot.Capabilities,
            Checks = snapshot.Checks,
            Warnings = snapshot.Warnings,
            ShortTermMaintenance = snapshot.ShortTermMaintenance
        };
    }

    private static string NormalizeStatus(string state)
    {
        return state switch
        {
            "Ready" => "ready",
            "Warning" => "warning",
            "Degraded" => "degraded",
            "NotProductionReady" => "not-production-ready",
            _ => state.ToLowerInvariant()
        };
    }
}
