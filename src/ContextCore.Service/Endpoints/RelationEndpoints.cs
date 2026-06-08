using ContextCore.Abstractions;
using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>上下文关系查询端点，按条目聚合入边和出边。</summary>
internal static class RelationEndpoints
{
	public static IEndpointRouteBuilder MapRelationEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/relations/types", (
			RelationTypeRegistry registry) =>
		{
			return Results.Ok(registry.GetAll());
		})
		.WithTags("Relations")
		.WithName("GetRelationTypes")
		.WithSummary("获取 relation type taxonomy");

		app.MapGet("/api/relations/diagnostics", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			RelationGraphValidationService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var report = await service.ValidateAsync(workspaceId, collectionId, null, ct).ConfigureAwait(false);
				return Results.Ok(report);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "relations.diagnostics");
			}
		})
		.WithTags("Relations")
		.WithName("GetRelationDiagnostics")
		.WithSummary("获取 relation graph 全局诊断");

		app.MapGet("/api/relations/diagnostics/{itemId}", async Task<IResult> (
			string itemId,
			string workspaceId,
			string? collectionId,
			RelationGraphValidationService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var report = await service.ValidateAsync(workspaceId, collectionId, itemId, ct).ConfigureAwait(false);
				return Results.Ok(report);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "relations.diagnostics.item");
			}
		})
		.WithTags("Relations")
		.WithName("GetItemRelationDiagnostics")
		.WithSummary("获取指定 item 的 relation graph 诊断");

		app.MapGet("/api/relations/{relationId}/explain", async Task<IResult> (
			string relationId,
			string? workspaceId,
			string? collectionId,
			RelationGraphValidationService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(workspaceId))
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					string.Empty,
					"relations.explain",
					"workspaceId 为必填参数。",
					field: "workspaceId");
			}

			try
			{
				var explain = await service.ExplainAsync(relationId, workspaceId, collectionId, ct).ConfigureAwait(false);
				return explain is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "relations.explain", $"未找到关系：{relationId}", detailCode: "relation_not_found")
					: Results.Ok(explain);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "relations.explain");
			}
		})
		.WithTags("Relations")
		.WithName("ExplainRelation")
		.WithSummary("获取单条 relation 的证据、置信度、生命周期和诊断解释");

		app.MapGet("/api/relations/{workspaceId}/{collectionId}/{itemId}", async Task<IResult> (
			string workspaceId,
			string collectionId,
			string itemId,
			IRelationStore relations,
			CancellationToken ct) =>
		{
			var outgoing = await relations.QueryBySourceAsync(workspaceId, collectionId, itemId, ct);
			var incoming = await relations.QueryByTargetAsync(workspaceId, collectionId, itemId, ct);
			return Results.Ok(new ContextCoreRelationLookupResponse
			{
				ItemId = itemId,
				Outgoing = outgoing,
				Incoming = incoming
			});
		})
		.WithTags("Relations")
		.WithName("GetRelations")
		.WithSummary("获取条目的出入关系");

		app.MapGet("/api/relations/{itemId}", async Task<IResult> (
			string itemId,
			string? workspaceId,
			string? collectionId,
			IRelationStore relations,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(collectionId))
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					string.Empty,
					"relations.get.by-id",
					"workspaceId 和 collectionId 为必填参数。",
					field: "workspaceId,collectionId");
			}

			var outgoing = await relations.QueryBySourceAsync(workspaceId, collectionId, itemId, ct);
			var incoming = await relations.QueryByTargetAsync(workspaceId, collectionId, itemId, ct);
			return Results.Ok(new ContextCoreRelationLookupResponse
			{
				ItemId = itemId,
				Outgoing = outgoing,
				Incoming = incoming
			});
		})
		.WithTags("Relations")
		.WithName("GetRelationsByItemId")
		.WithSummary("按路线图短路径获取条目的出入关系");

		return app;
	}
}
