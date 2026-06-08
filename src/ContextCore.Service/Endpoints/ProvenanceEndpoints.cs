using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>稳定对象来源链与只读诊断端点。</summary>
internal static class ProvenanceEndpoints
{
    public static IEndpointRouteBuilder MapProvenanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/provenance/{itemId}", async Task<IResult> (
            string itemId,
            string? workspaceId,
            string? collectionId,
            ContextProvenanceService provenance,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            try
            {
                var response = await provenance.GetAsync(
                    itemId,
                    workspaceId,
                    collectionId,
                    ct).ConfigureAwait(false);
                return response is null
                    ? ContextCoreHttpResultMapper.NotFound(
                        httpContext,
                        string.Empty,
                        "provenance",
                        $"未找到来源链目标：{itemId}",
                        detailCode: "provenance_target_not_found")
                    : Results.Ok(response);
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "provenance");
            }
        })
        .WithTags("Provenance")
        .WithName("GetProvenance")
        .WithSummary("查询 stable review accept 生成对象的完整来源链与只读诊断");

        return app;
    }
}
