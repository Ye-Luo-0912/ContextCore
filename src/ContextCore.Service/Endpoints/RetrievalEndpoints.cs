using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>Retrieval diagnostics endpoints. These endpoints are read-only unless explicitly documented otherwise.</summary>
internal static class RetrievalEndpoints
{
    public static IEndpointRouteBuilder MapRetrievalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/retrieval")
            .WithTags("Retrieval");

        group.MapPost("/ranker-shadow/debug", async Task<IResult> (
            LifecycleAwareRankerShadowDebugRequest request,
            LifecycleAwareRankerDebugService debugService,
            LearningRankerShadowOptions options,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (!options.DebugEndpointEnabled)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    string.Empty,
                    "retrieval.ranker-shadow.debug",
                    "Lifecycle-aware ranker shadow debug endpoint is disabled.");
            }

            try
            {
                var response = await debugService.DebugAsync(
                        request,
                        options.Profile,
                        options.DebugEndpointEnabled,
                        ct)
                    .ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (ArgumentException ex)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    string.Empty,
                    "retrieval.ranker-shadow.debug",
                    ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    string.Empty,
                    "retrieval.ranker-shadow.debug",
                    ex.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ContextCoreHttpResultMapper.StorageUnavailable(
                    httpContext,
                    request?.WorkspaceId ?? string.Empty,
                    "retrieval.ranker-shadow.debug",
                    "Ranker shadow debug timed out or was canceled.");
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.Error(
                    httpContext,
                    ex,
                    request?.WorkspaceId ?? string.Empty,
                    "retrieval.ranker-shadow.debug");
            }
        })
        .WithName("DebugLifecycleAwareRanker")
        .WithSummary("Runs lifecycle-aware ranker shadow diagnostics without changing retrieval output");

        return app;
    }
}
