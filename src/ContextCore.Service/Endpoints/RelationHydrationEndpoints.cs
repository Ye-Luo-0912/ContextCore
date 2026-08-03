using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Graph;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>Selected 关系批量水合端点：客户端选定关系 ID 后按 ID 拉取完整 Relation Metadata。</summary>
internal static class RelationHydrationEndpoints
{
	public static IEndpointRouteBuilder MapRelationHydrationEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/relations/hydration", async Task<IResult> (
			RelationHydrationRequest request,
			ISelectedRelationHydrationService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (request is null)
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					string.Empty,
					"relations.hydration",
					"请求体不能为空。");
			}

			if (string.IsNullOrWhiteSpace(request.WorkspaceId))
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					request.OperationId,
					"relations.hydration",
					"workspaceId 为必填参数。",
					field: "workspaceId");
			}

			if (request.RelationIds is null || request.RelationIds.Count == 0)
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					request.OperationId,
					"relations.hydration",
					"relationIds 至少需要 1 个关系 ID。",
					field: "relationIds");
			}

			try
			{
				var response = await service.HydrateAsync(request, ct).ConfigureAwait(false);
				return Results.Ok(response);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "relations.hydration");
			}
		})
		.WithTags("Relations")
		.WithName("HydrateSelectedRelations")
		.WithSummary("按 ID 批量水合 Selected 关系（完整 Metadata），优先批量水合存储，缺失时回退逐条查询")
		.Produces<RelationHydrationResponse>(StatusCodes.Status200OK)
		.Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

		return app;
	}
}
