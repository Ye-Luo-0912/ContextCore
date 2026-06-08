using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>服务状态端点，区分轻量状态、就绪探针与深度探针职责。</summary>
internal static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/status", async Task<IResult> (
            StorageOptions storage,
            IContextJobQueryStore jobs,
            ServiceAlphaRuntimeInspector inspector,
            CancellationToken ct) =>
        {
            var queued = await jobs.QueryAsync(new ContextJobQuery
            {
                State = ContextJobState.Queued,
                Take = 1000
            }, ct).ConfigureAwait(false);
            var running = await jobs.QueryAsync(new ContextJobQuery
            {
                State = ContextJobState.Running,
                Take = 1000
            }, ct).ConfigureAwait(false);
            var snapshot = await inspector.GetStatusSnapshotAsync(ct).ConfigureAwait(false);

            return Results.Ok(new RuntimeStatusResponse
            {
                Status = "ok",
                Utc = DateTimeOffset.UtcNow,
                Storage = new ContextCoreStorageInfo
                {
                    Provider = storage.Provider,
                    RootPath = storage.IsFileSystem ? storage.ResolvedRootPath : null
                },
                Jobs = new ContextCoreServiceJobQueueResponse
                {
                    Queued = queued.Count,
                    Running = running.Count
                },
                RetrievalBaseline = snapshot.RetrievalBaseline,
                Capabilities = snapshot.Capabilities,
                Readiness = ToRuntimeReadinessResponse(snapshot),
                ShortTermMaintenance = snapshot.ShortTermMaintenance
            });
        })
        .WithTags("Status")
        .WithName("GetStatus")
        .WithSummary("获取轻量服务状态（只读，不执行写探针）");

        app.MapGet("/api/status/deep", async Task<IResult> (
            ServiceAlphaRuntimeInspector inspector,
            bool? refresh,
            CancellationToken ct) =>
        {
            var deep = await inspector.GetDeepSnapshotAsync(refresh == true, ct).ConfigureAwait(false);
            return Results.Ok(ToRuntimeReadinessResponse(deep));
        })
        .WithTags("Status")
        .WithName("GetStatusDeep")
        .WithSummary("获取深度运行时探针结果（允许 system health scope 写探针，可用 refresh=true 强制重跑）");

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
