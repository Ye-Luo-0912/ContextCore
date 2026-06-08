using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>上下文打包（ContextPackage）相关的 Minimal API 端点。</summary>
internal static class PackageEndpoints
{
	// TODO-GRPC: 后期迁移至 gRPC 时，在 GrpcServices/PackageGrpcService.cs 实现对应方法
	public static IEndpointRouteBuilder MapPackageEndpoints(this IEndpointRouteBuilder app)
	{
		var group = app.MapGroup("/api/package")
			.WithTags("Package");

		// POST /api/package/build
		group.MapPost("/build", async Task<IResult> (
			ContextPackageRequest request,
			IContextRuntimeService runtime,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var package = await runtime.BuildPackageAsync(request, ct);
				return Results.Ok(package);
			}
			catch (ArgumentException ex)
			{
				return ContextCoreHttpResultMapper.InvalidRequest(httpContext, string.Empty, "package.build", ex.Message);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				return ContextCoreHttpResultMapper.StorageUnavailable(httpContext, string.Empty, "package.build", "Package build timed out or was canceled.");
			}
		})
		.WithName("BuildPackage")
		.WithSummary("按请求构建上下文包，返回供 LLM 使用的 ContextPackage");

		// POST /api/package/build-detailed
		// 返回完整的 ContextPackageBuildResult，含 selected/dropped 决策日志和 RetrievalPlan。
		// 调用方可将 result.Plan 透传至后续 POST /api/context/retrieve 的 Plan 字段，实现 plan passthrough。
		group.MapPost("/build-detailed", async Task<IResult> (
			ContextPackageRequest request,
			IContextRuntimeService runtime,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var result = await runtime.BuildPackageDetailedAsync(request, ct);
				return Results.Ok(result);
			}
			catch (ArgumentException ex)
			{
				return ContextCoreHttpResultMapper.InvalidRequest(httpContext, string.Empty, "package.build-detailed", ex.Message);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				return ContextCoreHttpResultMapper.StorageUnavailable(httpContext, string.Empty, "package.build-detailed", "Package build timed out or was canceled.");
			}
		})
		.WithName("BuildPackageDetailed")
		.WithSummary("构建上下文包并返回完整决策日志（含 RetrievalPlan），用于 plan passthrough 场景");

		// POST /api/package/preview
		group.MapPost("/preview", async Task<IResult> (
			ContextPackageRequest request,
			IContextRuntimeService runtime,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var package = await runtime.BuildPackageAsync(request, ct);
				return Results.Ok(package);
			}
			catch (ArgumentException ex)
			{
				return ContextCoreHttpResultMapper.InvalidRequest(httpContext, string.Empty, "package.preview", ex.Message);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				return ContextCoreHttpResultMapper.StorageUnavailable(httpContext, string.Empty, "package.preview", "Package preview timed out or was canceled.");
			}
		})
		.WithName("PreviewPackage")
		.WithSummary("预览上下文包构建结果，不引入新的持久化语义");

		// POST /api/package/index/upsert
		group.MapPost("/index/upsert", async Task<IResult> (
			ContextIndexEntry entry,
			IContextIndex index,
			CancellationToken ct) =>
		{
			await index.UpsertAsync(entry, ct);
			return Results.NoContent();
		})
		.WithName("UpsertIndexEntry")
		.WithSummary("插入或更新一条索引条目");

		// POST /api/package/index/search
		group.MapPost("/index/search", async Task<IResult> (
			IndexQuery query,
			IContextIndex index,
			CancellationToken ct) =>
		{
			var entries = await index.SearchAsync(query, ct);
			return Results.Ok(entries);
		})
		.WithName("SearchIndex")
		.WithSummary("搜索索引条目");

		// GET /api/package/policies
		group.MapGet("/policies", async Task<IResult> (
			string workspaceId,
			string collectionId,
			string? queryText,
			int? take,
			IContextPackagePolicyStore policies,
			CancellationToken ct) =>
		{
			var items = await policies.QueryAsync(new ContextPackagePolicyQuery
			{
				WorkspaceId = workspaceId,
				CollectionId = collectionId,
				QueryText = queryText,
				Take = take.GetValueOrDefault(50)
			}, ct);
			return Results.Ok(items);
		})
		.WithName("QueryPackagePolicies")
		.WithSummary("查询上下文包策略");

		// GET /api/package/policies/{id}
		group.MapGet("/policies/{id}", async Task<IResult> (
			string id,
			string workspaceId,
			string collectionId,
			IContextPackagePolicyStore policies,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			var policy = await policies.GetAsync(workspaceId, collectionId, id, ct);
			return policy is null
				? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "package.policies.get", $"未找到策略：{id}", detailCode: "package_policy_not_found")
				: Results.Ok(policy);
		})
		.WithName("GetPackagePolicy")
		.WithSummary("获取指定上下文包策略");

		return app;
	}
}




