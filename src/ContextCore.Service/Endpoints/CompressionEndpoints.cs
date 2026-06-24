using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;

namespace ContextCore.Service.Endpoints;

/// <summary>压缩相关 HTTP 端点，提供同步压缩与结果落库能力。</summary>
internal static class CompressionEndpoints
{
	/// <summary>
	/// 同步执行压缩并保存生成结果。适用于需要立即使用压缩结果的场景，或对后续查询/打包复用有较高要求的场景。
	/// </summary>
	/// <param name="app"></param>
	/// <returns></returns>
	public static IEndpointRouteBuilder MapCompressionEndpoints(this IEndpointRouteBuilder app)
	{
		var group = app.MapGroup("/api/compression")
			.WithTags("Compression");

		//
		group.MapPost("/sync", async Task<IResult> (
			CompressionRequest request,
			IContextCompressor compressor,
			IContextStore store,
			IContextIndex index,
			IRelationStore relationStore,
			RelationBuilder relationBuilder,
			CancellationToken ct) =>
		{
			var response = await compressor.CompressAsync(request, ct);

			// 同步压缩不仅返回结果，也把生成条目、索引提示和关系写回存储，便于后续查询/打包复用。
			foreach (var item in response.GeneratedItems)
			{
				await store.SaveAsync(item, ct);
			}

			foreach (var entry in response.IndexHints)
			{
				await index.UpsertAsync(entry, ct);
			}

			foreach (var relation in relationBuilder.BuildForCompressionResponse(response))
			{
				await relationStore.SaveAsync(relation, ct);
			}

			return Results.Ok(response);
		})
		.WithName("RunCompressionSync")
		.WithSummary("同步执行压缩并保存生成结果");

		return app;
	}
}
