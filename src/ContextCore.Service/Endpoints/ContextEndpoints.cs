using ContextCore.Abstractions;
using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;
using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Service.Endpoints;

/// <summary>上下文条目（ContextItem）相关的 Minimal API 端点。</summary>
internal static class ContextEndpoints
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	// TODO-GRPC: 后期迁移至 gRPC 时，在 GrpcServices/ContextGrpcService.cs 实现对应方法
	public static IEndpointRouteBuilder MapContextEndpoints(this IEndpointRouteBuilder app)
	{
		var group = app.MapGroup("/api/context")
			.WithTags("Context");

		// POST /api/context/ingest
		group.MapPost("/ingest", async Task<IResult> (
			JsonElement body,
			IContextRuntimeService runtime,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			var operationId = string.Empty;
			try
			{
				if (LooksLikeInputCommand(body))
				{
					var inputCommand = body.Deserialize<ContextInputCommand>(JsonOptions)
						?? throw new InvalidOperationException("ContextInputCommand 反序列化结果为空。");
					operationId = inputCommand.OperationId;
					var commandResult = await runtime.IngestAsync(inputCommand, ct);
					return ContextCoreHttpResultMapper.Success(commandResult);
				}

				var legacyItem = body.Deserialize<ContextItem>(JsonOptions)
					?? throw new InvalidOperationException("ContextItem 反序列化结果为空。");
				var legacyCommand = ToInputCommand(legacyItem);
				operationId = legacyCommand.OperationId;
				var legacyResult = await runtime.IngestAsync(legacyCommand, ct);
				return ContextCoreHttpResultMapper.Success(legacyResult);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, operationId, "context.ingest");
			}
		})
		.WithName("IngestContextItem")
		.WithSummary("推荐业务入口：支持 ContextInputCommand，旧 ContextItem 请求体兼容保留");

		// GET /api/context/{workspaceId}/{collectionId}/{id}
		group.MapGet("/{workspaceId}/{collectionId}/{id}", async Task<IResult> (
			string workspaceId,
			string collectionId,
			string id,
			IContextStore store,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			var item = await store.GetAsync(workspaceId, collectionId, id, ct);
			return item is null
				? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "context.get", $"未找到上下文条目：{id}", detailCode: "context_item_not_found")
				: Results.Ok(item);
		})
		.WithName("GetContextItem")
		.WithSummary("按 ID 获取上下文条目");

		// GET /api/context/{id}?workspaceId=...&collectionId=...
		group.MapGet("/{id}", async Task<IResult> (
			string id,
			string? workspaceId,
			string? collectionId,
			IContextStore store,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(collectionId))
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					string.Empty,
					"context.get.by-id",
					"workspaceId and collectionId are required.",
					field: "workspaceId,collectionId");
			}

			var item = await store.GetAsync(workspaceId, collectionId, id, ct);
			return item is null
				? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "context.get.by-id", $"未找到上下文条目：{id}", detailCode: "context_item_not_found")
				: Results.Ok(item);
		})
		.WithName("GetContextItemById")
		.WithSummary("按路线图短路径获取上下文条目");

		// POST /api/context/query
		group.MapPost("/query", async Task<IResult> (
			ContextQuery query,
			IContextStore store,
			IServiceProvider services,
			CancellationToken ct) =>
		{
			var items = await store.QueryAsync(query, ct);
			await RecordRouterShadowAsync(
					services,
					new RouterIntentShadowRecordRequest
					{
						RequestId = $"context-query-{Guid.NewGuid():N}",
						WorkspaceId = query.WorkspaceId,
						CollectionId = query.CollectionId,
						EntryPoint = "query",
						QueryText = query.QueryText ?? string.Empty
					},
					ct)
				.ConfigureAwait(false);
			return Results.Ok(items);
		})
		.WithName("QueryContextItems")
		.WithSummary("按条件查询上下文条目列表");

		// GET /api/context/planning/snapshot
		group.MapGet("/planning/snapshot", async Task<IResult> (
			string? workspaceId,
			string? collectionId,
			string? sessionId,
			PlanningSnapshotService planningSnapshotService,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(workspaceId))
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					string.Empty,
					"context.planning.snapshot",
					"workspaceId is required.",
					field: "workspaceId");
			}

			try
			{
				var snapshot = await planningSnapshotService.GetSnapshotAsync(
					workspaceId,
					collectionId,
					sessionId,
					ct).ConfigureAwait(false);
				return Results.Ok(snapshot);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "context.planning.snapshot");
			}
		})
		.WithName("GetPlanningSnapshot")
		.WithSummary("获取只读 planning 输入快照，不影响 retrieval/package");

		// POST /api/context/planning/propose
		group.MapPost("/planning/propose", async Task<IResult> (
			ContextPlanningProposalRequest request,
			RetrievalPlanProposalService proposalService,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(request.WorkspaceId))
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					string.Empty,
					"context.planning.propose",
					"workspaceId is required.",
					field: "workspaceId");
			}

			try
			{
				var proposal = await proposalService.ProposeAsync(request, ct).ConfigureAwait(false);
				await RecordRouterShadowAsync(
						httpContext.RequestServices,
						new RouterIntentShadowRecordRequest
						{
							RequestId = proposal.OperationId,
							WorkspaceId = request.WorkspaceId,
							CollectionId = request.CollectionId,
							SessionId = request.SessionId,
							EntryPoint = "planning",
							Mode = request.Mode ?? proposal.Mode,
							QueryText = request.CurrentInput,
							RuntimeIntent = proposal.Intent,
							RuntimeConfidence = proposal.Confidence,
							Metadata =
							{
								["proposalMode"] = proposal.Mode
							}
						},
						ct)
					.ConfigureAwait(false);
				return Results.Ok(proposal);
			}
			catch (ArgumentException ex)
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					string.Empty,
					"context.planning.propose",
					ex.Message);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "context.planning.propose");
			}
		})
		.WithName("ProposeRetrievalPlan")
		.WithSummary("基于 planning snapshot 生成只读 retrieval plan proposal，不执行 retrieval");

		// DELETE /api/context/{workspaceId}/{collectionId}/{id}
		group.MapDelete("/{workspaceId}/{collectionId}/{id}", async Task<IResult> (
			string workspaceId,
			string collectionId,
			string id,
			IContextStore store,
			CancellationToken ct) =>
		{
			await store.DeleteAsync(workspaceId, collectionId, id, ct);
			return Results.NoContent();
		})
		.WithName("DeleteContextItem")
		.WithSummary("删除指定 ID 的上下文条目");

		// GET /api/context/{workspaceId}/{collectionId}/collection
		group.MapGet("/{workspaceId}/{collectionId}/collection", async Task<IResult> (
			string workspaceId,
			string collectionId,
			IContextCollectionStore collectionStore,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			var collection = await collectionStore.GetCollectionAsync(workspaceId, collectionId, ct);
			return collection is null
				? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "context.collection.get", $"未找到集合：{collectionId}", detailCode: "collection_not_found")
				: Results.Ok(collection);
		})
		.WithName("GetCollection")
		.WithSummary("获取集合元数据");

		// POST /api/context/retrieve
		// 接受可选的 RetrievalPlan（可从 POST /api/package/build-detailed 返回的 result.Plan 获取），
		// 当 Plan 非 null 时 HybridContextRetriever 将按计划调整召回优先级，实现 plan passthrough。
		group.MapPost("/retrieve", async Task<IResult> (
			ContextRetrievalRequest request,
			IContextRetriever retriever,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var result = await retriever.RetrieveAsync(request, ct);
				return Results.Ok(result);
			}
			catch (ArgumentException ex)
			{
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					request.OperationId,
					"context.retrieve",
					ex.Message);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				return ContextCoreHttpResultMapper.StorageUnavailable(
					httpContext,
					request.OperationId,
					"context.retrieve",
					"Retrieval timed out or was canceled.");
			}
		})
		.WithName("RetrieveContext")
		.WithSummary("执行混合检索，可通过 Plan 字段接受来自 /api/package/build-detailed 的 RetrievalPlan 以实现 plan passthrough");

		return app;
	}

	private static async Task RecordRouterShadowAsync(
		IServiceProvider services,
		RouterIntentShadowRecordRequest request,
		CancellationToken cancellationToken)
	{
		var shadow = services.GetService<RouterIntentShadowService>();
		if (shadow is null)
		{
			return;
		}

		await shadow.RecordAsync(request, cancellationToken).ConfigureAwait(false);
	}

	private static bool LooksLikeInputCommand(JsonElement body)
	{
		if (body.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		foreach (var property in body.EnumerateObject())
		{
			if (property.NameEquals("source") || property.NameEquals("Source")
				|| property.NameEquals("inputKind") || property.NameEquals("InputKind"))
			{
				return true;
			}
		}

		return false;
	}

	private static ContextInputCommand ToInputCommand(ContextItem item)
	{
		var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
		{
			["legacy.id"] = item.Id,
			["legacy.type"] = item.Type,
			["legacy.title"] = item.Title ?? string.Empty,
			["legacy.tags"] = string.Join(",", item.Tags),
			["legacy.refs"] = string.Join(",", item.Refs),
			["legacy.importance"] = item.Importance.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["legacy.version"] = item.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["legacy.checksum"] = item.Checksum ?? string.Empty,
			["legacy.createdAt"] = item.CreatedAt == default
				? string.Empty
				: item.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
			["legacy.updatedAt"] = item.UpdatedAt == default
				? string.Empty
				: item.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
		};

		return new ContextInputCommand
		{
			OperationId = metadata.GetValueOrDefault("operationId", string.Empty),
			WorkspaceId = item.WorkspaceId,
			CollectionId = item.CollectionId,
			Source = metadata.GetValueOrDefault("source", "legacy-context-item"),
			InputKind = metadata.GetValueOrDefault("inputKind", item.Type),
			ContentFormat = item.ContentFormat,
			Content = item.Content,
			SessionId = metadata.GetValueOrDefault("sessionId"),
			Mode = metadata.GetValueOrDefault("mode"),
			SourceRefs = item.SourceRefs,
			Metadata = metadata
		};
	}
}
