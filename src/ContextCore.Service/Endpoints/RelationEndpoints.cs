using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
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

		app.MapGet("/api/relations/expansion/profiles", (
			RelationExpansionProfileRegistry registry) =>
		{
			return Results.Ok(registry.GetAll());
		})
		.WithTags("Relations")
		.WithName("GetRelationExpansionProfiles")
		.WithSummary("获取 relation expansion governance profiles");

		app.MapPost("/api/relations/expansion/preview", async Task<IResult> (
			RelationExpansionPreviewRequest request,
			RelationExpansionPreviewService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(request.WorkspaceId))
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					request.OperationId,
					"relations.expansion.preview",
					"workspaceId 为必填参数。",
					field: "workspaceId");
			}

			if (string.IsNullOrWhiteSpace(request.CollectionId))
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					request.OperationId,
					"relations.expansion.preview",
					"collectionId 为必填参数。",
					field: "collectionId");
			}

			if (string.IsNullOrWhiteSpace(request.ItemId))
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					request.OperationId,
					"relations.expansion.preview",
					"itemId 为必填参数。",
					field: "itemId");
			}

			try
			{
				var preview = await service.PreviewAsync(request, ct).ConfigureAwait(false);
				return Results.Ok(preview);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "relations.expansion.preview");
			}
		})
		.WithTags("Relations")
		.WithName("PreviewRelationExpansion")
		.WithSummary("只读预览 relation expansion profile 对关系边的 accepted / blocked 结果");

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

		app.MapPost("/api/relations/{relationId}/review", async Task<IResult> (
			string relationId,
			string? workspaceId,
			string? collectionId,
			RelationReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IRelationReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "relations.review", "当前 provider 未注册 RelationReview 存储；Postgres provider 暂不支持该写路径。");
			}

			try
			{
				var service = services.GetRequiredService<RelationReviewService>();
				var normalized = NormalizeRelationReviewRequest(request, workspaceId, collectionId);
				var result = await service.ReviewAsync(relationId, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "relations.review", $"未找到关系：{relationId}", detailCode: "relation_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "relations.review");
			}
		})
		.WithTags("Relations")
		.WithName("ReviewRelation")
		.WithSummary("人工 review relation 并记录审核历史");

		app.MapPost("/api/relations/{relationId}/reject", async Task<IResult> (
			string relationId,
			string? workspaceId,
			string? collectionId,
			RelationReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IRelationReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "relations.reject", "当前 provider 未注册 RelationReview 存储；Postgres provider 暂不支持该写路径。");
			}

			try
			{
				var service = services.GetRequiredService<RelationReviewService>();
				var normalized = NormalizeRelationReviewRequest(request, workspaceId, collectionId);
				var result = await service.RejectAsync(relationId, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "relations.reject", $"未找到关系：{relationId}", detailCode: "relation_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "relations.reject");
			}
		})
		.WithTags("Relations")
		.WithName("RejectRelation")
		.WithSummary("人工 reject relation 并记录审核历史");

		app.MapPost("/api/relations/{relationId}/deprecate", async Task<IResult> (
			string relationId,
			string? workspaceId,
			string? collectionId,
			RelationReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IRelationReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "relations.deprecate", "当前 provider 未注册 RelationReview 存储；Postgres provider 暂不支持该写路径。");
			}

			try
			{
				var service = services.GetRequiredService<RelationReviewService>();
				var normalized = NormalizeRelationReviewRequest(request, workspaceId, collectionId);
				var result = await service.DeprecateAsync(relationId, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "relations.deprecate", $"未找到关系：{relationId}", detailCode: "relation_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "relations.deprecate");
			}
		})
		.WithTags("Relations")
		.WithName("DeprecateRelation")
		.WithSummary("人工 deprecate relation 并记录审核历史");

		app.MapPost("/api/relations/{relationId}/needs-evidence", async Task<IResult> (
			string relationId,
			string? workspaceId,
			string? collectionId,
			RelationReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IRelationReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "relations.needs-evidence", "当前 provider 未注册 RelationReview 存储；Postgres provider 暂不支持该写路径。");
			}

			try
			{
				var service = services.GetRequiredService<RelationReviewService>();
				var normalized = NormalizeRelationReviewRequest(request, workspaceId, collectionId);
				var result = await service.MarkNeedsEvidenceAsync(relationId, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "relations.needs-evidence", $"未找到关系：{relationId}", detailCode: "relation_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "relations.needs-evidence");
			}
		})
		.WithTags("Relations")
		.WithName("MarkRelationNeedsEvidence")
		.WithSummary("人工标记 relation 需要更多证据并记录审核历史");

		app.MapGet("/api/relations/{relationId}/reviews", async Task<IResult> (
			string relationId,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IRelationReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "relations.reviews", "当前 provider 未注册 RelationReview 存储；Postgres provider 暂不支持该写路径。");
			}

			try
			{
				var service = services.GetRequiredService<RelationReviewService>();
				var reviews = await service.GetReviewsAsync(relationId, ct).ConfigureAwait(false);
				return Results.Ok(reviews);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "relations.reviews");
			}
		})
		.WithTags("Relations")
		.WithName("GetRelationReviews")
		.WithSummary("查询 relation review history");

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

	private static RelationReviewRequest NormalizeRelationReviewRequest(
		RelationReviewRequest request,
		string? workspaceId,
		string? collectionId)
	{
		return new RelationReviewRequest
		{
			OperationId = request.OperationId,
			WorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? request.WorkspaceId : workspaceId,
			CollectionId = string.IsNullOrWhiteSpace(collectionId) ? request.CollectionId : collectionId,
			Reviewer = request.Reviewer,
			Reason = request.Reason,
			Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
		};
	}
}
