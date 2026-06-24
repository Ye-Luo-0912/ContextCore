using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Promotion;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>记忆层（ContextMemoryItem）相关的 Minimal API 端点。</summary>
internal static class MemoryEndpoints
{
	// TODO-GRPC: 后期迁移至 gRPC 时，在 GrpcServices/MemoryGrpcService.cs 实现对应方法
	public static IEndpointRouteBuilder MapMemoryEndpoints(this IEndpointRouteBuilder app)
	{
		var group = app.MapGroup("/api/memory")
			.WithTags("Memory");

		// POST /api/memory/add
		group.MapPost("/add", async Task<IResult> (
			ContextMemoryItem item,
			IContextRuntimeService runtime,
			CancellationToken ct) =>
		{
			var result = await runtime.AddWorkingMemoryAsync(item, ct);
			return Results.Ok(result);
		})
		.WithName("AddWorkingMemory")
		.WithSummary("添加工作记忆条目");

		// POST /api/memory/working/add
		group.MapPost("/working/add", async Task<IResult> (
			WorkingMemoryItem item,
			IWorkingMemoryService workingMemory,
			CancellationToken ct) =>
		{
			var result = await workingMemory.AddAsync(item, ct);
			return Results.Ok(result);
		})
		.WithName("AddWorkingMemoryItem")
		.WithSummary("添加工作记忆条目（WorkingMemoryItem）");

		// GET /api/memory/working/recent
		group.MapGet("/working/recent", async Task<IResult> (
			string workspaceId,
			string collectionId,
			int? take,
			IWorkingMemoryService workingMemory,
			CancellationToken ct) =>
		{
			var items = await workingMemory.GetRecentAsync(
				workspaceId,
				collectionId,
				take.GetValueOrDefault(20),
				ct);
			return Results.Ok(items);
		})
		.WithName("GetRecentWorkingMemory")
		.WithSummary("获取最近工作记忆条目");

		// POST /api/memory/working/clear
		group.MapPost("/working/clear", async Task<IResult> (
			WorkingMemoryScopeRequest req,
			IWorkingMemoryService workingMemory,
			CancellationToken ct) =>
		{
			await workingMemory.ClearAsync(req.WorkspaceId, req.CollectionId, ct);
			return Results.NoContent();
		})
		.WithName("ClearWorkingMemory")
		.WithSummary("清空工作记忆和活跃上下文");

		// GET /api/memory/working/active-context
		group.MapGet("/working/active-context", async Task<IResult> (
			string workspaceId,
			string collectionId,
			IWorkingMemoryService workingMemory,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			var activeContext = await workingMemory.GetActiveContextAsync(workspaceId, collectionId, ct);
			return activeContext is null
				? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.working.active-context", "未找到活跃上下文。", detailCode: "active_context_not_found")
				: Results.Ok(activeContext);
		})
		.WithName("GetWorkingMemoryActiveContext")
		.WithSummary("获取工作记忆活跃上下文");

		// POST /api/memory/working/active-context
		group.MapPost("/working/active-context", async Task<IResult> (
			WorkingMemoryActiveContext activeContext,
			IWorkingMemoryService workingMemory,
			CancellationToken ct) =>
		{
			var result = await workingMemory.SetActiveContextAsync(activeContext, ct);
			return Results.Ok(result);
		})
		.WithName("SetWorkingMemoryActiveContext")
		.WithSummary("设置工作记忆活跃上下文");

		// GET /api/memory/working/current-task
		group.MapGet("/working/current-task", async Task<IResult> (
			string workspaceId,
			string collectionId,
			IWorkingMemoryService workingMemory,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			var currentTask = await workingMemory.GetCurrentTaskAsync(workspaceId, collectionId, ct);
			return currentTask is null
				? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.working.current-task", "未找到当前任务。", detailCode: "current_task_not_found")
				: Results.Ok(currentTask);
		})
		.WithName("GetWorkingMemoryCurrentTask")
		.WithSummary("获取当前任务");

		// POST /api/memory/working/current-task
		group.MapPost("/working/current-task", async Task<IResult> (
			WorkingMemoryCurrentTask currentTask,
			IWorkingMemoryService workingMemory,
			CancellationToken ct) =>
		{
			var result = await workingMemory.SetCurrentTaskAsync(currentTask, ct);
			return Results.Ok(result);
		})
		.WithName("SetWorkingMemoryCurrentTask")
		.WithSummary("设置当前任务");

		// POST /api/memory/query
		group.MapPost("/query", async Task<IResult> (
			ContextMemoryQuery query,
			IMemoryStore store,
			CancellationToken ct) =>
		{
			var items = await store.QueryAsync(query, ct);
			return Results.Ok(items);
		})
		.WithName("QueryMemory")
		.WithSummary("按条件查询记忆条目");

		// GET /api/memory/global
		group.MapGet("/global", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			ContextScope? scope,
			int? take,
			IGlobalContextStore store,
			CancellationToken ct) =>
		{
			var items = await store.QueryAsync(new ContextGlobalQuery
			{
				WorkspaceId = workspaceId,
				CollectionId = collectionId,
				Scope = scope,
				Take = take.GetValueOrDefault(100)
			}, ct);
			return Results.Ok(items);
		})
		.WithName("QueryGlobalContext")
		.WithSummary("按条件查询全局上下文条目");

		// GET /api/memory/stable/snapshot
		group.MapGet("/stable/snapshot", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			int? take,
			StableMemoryGovernanceService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var snapshot = await service.GetSnapshotAsync(
					workspaceId,
					collectionId,
					take.GetValueOrDefault(20),
					ct).ConfigureAwait(false);
				return Results.Ok(snapshot);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.stable.snapshot");
			}
		})
		.WithName("GetStableMemorySnapshot")
		.WithSummary("查询 Stable Memory 治理快照");

		// GET /api/memory/stable/diagnostics
		group.MapGet("/stable/diagnostics", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			StableMemoryGovernanceService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var diagnostics = await service.GetDiagnosticsAsync(workspaceId, collectionId, ct).ConfigureAwait(false);
				return Results.Ok(diagnostics);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.stable.diagnostics");
			}
		})
		.WithName("GetStableMemoryDiagnostics")
		.WithSummary("查询 Stable Memory 诊断报告");

		// GET /api/memory/stable/{id}/explain
		group.MapGet("/stable/{id}/explain", async Task<IResult> (
			string id,
			string workspaceId,
			string? collectionId,
			StableMemoryGovernanceService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var explanation = await service.ExplainAsync(id, workspaceId, collectionId, ct).ConfigureAwait(false);
				return explanation is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.stable.explain", $"未找到 Stable Memory：{id}", detailCode: "stable_memory_not_found")
					: Results.Ok(explanation);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.stable.explain");
			}
		})
		.WithName("ExplainStableMemory")
		.WithSummary("解释 Stable Memory 来源链和诊断");

		// GET /api/memory/stable/{id}/replacement-chain
		group.MapGet("/stable/{id}/replacement-chain", async Task<IResult> (
			string id,
			string workspaceId,
			string? collectionId,
			StableMemoryGovernanceService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var chain = await service.GetReplacementChainAsync(id, workspaceId, collectionId, ct).ConfigureAwait(false);
				return chain is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.stable.replacement-chain", $"未找到 Stable Memory：{id}", detailCode: "stable_memory_not_found")
					: Results.Ok(chain);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.stable.replacement-chain");
			}
		})
		.WithName("GetStableReplacementChain")
		.WithSummary("查询 Stable Memory supersede / replacement chain");

		// POST /api/memory/stable/{id}/deprecate
		group.MapPost("/stable/{id}/deprecate", async Task<IResult> (
			string id,
			string? workspaceId,
			string? collectionId,
			StableLifecycleReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IStableLifecycleReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.stable.deprecate", "当前 provider 未注册 StableLifecycle review 存储；Postgres provider 暂不支持该写路径。");
			}

			try
			{
				var service = services.GetRequiredService<StableLifecycleReviewService>();
				var normalized = NormalizeStableLifecycleReviewRequest(request, workspaceId, collectionId);
				var result = await service.DeprecateAsync(id, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.stable.deprecate", $"未找到 Stable Memory：{id}", detailCode: "stable_memory_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.stable.deprecate");
			}
		})
		.WithName("DeprecateStableMemory")
		.WithSummary("人工废弃 Stable Memory 并记录生命周期 review");

		// POST /api/memory/stable/{id}/supersede
		group.MapPost("/stable/{id}/supersede", async Task<IResult> (
			string id,
			string? workspaceId,
			string? collectionId,
			StableLifecycleReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IStableLifecycleReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.stable.supersede", "当前 provider 未注册 StableLifecycle review 存储；Postgres provider 暂不支持该写路径。");
			}

			try
			{
				var service = services.GetRequiredService<StableLifecycleReviewService>();
				var normalized = NormalizeStableLifecycleReviewRequest(request, workspaceId, collectionId);
				var result = await service.SupersedeAsync(id, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.stable.supersede", $"未找到 Stable Memory：{id}", detailCode: "stable_memory_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.stable.supersede");
			}
		})
		.WithName("SupersedeStableMemory")
		.WithSummary("人工 supersede Stable Memory 并记录生命周期 review");

		// POST /api/memory/stable/{id}/reject
		group.MapPost("/stable/{id}/reject", async Task<IResult> (
			string id,
			string? workspaceId,
			string? collectionId,
			StableLifecycleReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IStableLifecycleReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.stable.reject", "当前 provider 未注册 StableLifecycle review 存储；Postgres provider 暂不支持该写路径。");
			}

			try
			{
				var service = services.GetRequiredService<StableLifecycleReviewService>();
				var normalized = NormalizeStableLifecycleReviewRequest(request, workspaceId, collectionId);
				var result = await service.RejectAsync(id, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.stable.reject", $"未找到 Stable Memory：{id}", detailCode: "stable_memory_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.stable.reject");
			}
		})
		.WithName("RejectStableMemory")
		.WithSummary("人工拒绝 Stable Memory 并记录生命周期 review");

		// GET /api/memory/stable/{id}/reviews
		group.MapGet("/stable/{id}/reviews", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IStableLifecycleReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.stable.reviews", "当前 provider 未注册 StableLifecycle review 存储；Postgres provider 暂不支持该写路径。");
			}

			try
			{
				var service = services.GetRequiredService<StableLifecycleReviewService>();
				var reviews = await service.GetReviewsAsync(id, ct).ConfigureAwait(false);
				return Results.Ok(reviews);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.stable.reviews");
			}
		})
		.WithName("GetStableMemoryReviews")
		.WithSummary("查询 Stable Memory 生命周期 review history");

		// GET /api/memory/candidates/snapshot
		group.MapGet("/candidates/snapshot", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			int? take,
			CandidateMemorySnapshotService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var snapshot = await service.GetSnapshotAsync(
					workspaceId,
					collectionId,
					take.GetValueOrDefault(20),
					ct).ConfigureAwait(false);
				return Results.Ok(snapshot);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.candidates.snapshot");
			}
		})
		.WithName("GetCandidateMemorySnapshot")
		.WithSummary("查询中期 Candidate Memory 治理快照");

		// GET /api/memory/candidates/diagnostics
		group.MapGet("/candidates/diagnostics", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			CandidateMemorySnapshotService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var diagnostics = await service.GetDiagnosticsAsync(workspaceId, collectionId, ct).ConfigureAwait(false);
				return Results.Ok(diagnostics);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.candidates.diagnostics");
			}
		})
		.WithName("GetCandidateMemoryDiagnostics")
		.WithSummary("查询 Candidate Memory 诊断报告");

		// GET /api/memory/candidates/{id}
		group.MapGet("/candidates/{id}", async Task<IResult> (
			string id,
			string workspaceId,
			string? collectionId,
			CandidateMemorySnapshotService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var candidate = await service.GetAsync(id, workspaceId, collectionId, ct).ConfigureAwait(false);
				return candidate is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.candidates.detail", $"未找到 Candidate Memory：{id}", detailCode: "candidate_memory_not_found")
					: Results.Ok(candidate);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.candidates.detail");
			}
		})
		.WithName("GetCandidateMemory")
		.WithSummary("按 ID 查询 Candidate Memory 记录");

		// GET /api/memory/candidates/{id}/explain
		group.MapGet("/candidates/{id}/explain", async Task<IResult> (
			string id,
			string workspaceId,
			string? collectionId,
			CandidateMemorySnapshotService service,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			try
			{
				var explanation = await service.ExplainAsync(id, workspaceId, collectionId, ct).ConfigureAwait(false);
				return explanation is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.candidates.explain", $"未找到 Candidate Memory：{id}", detailCode: "candidate_memory_not_found")
					: Results.Ok(explanation);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.candidates.explain");
			}
		})
		.WithName("ExplainCandidateMemory")
		.WithSummary("解释 Candidate Memory 来源链和风险");

		// POST /api/memory/candidates/{id}/ready-for-stable-review
		group.MapPost("/candidates/{id}/ready-for-stable-review", async Task<IResult> (
			string id,
			string? workspaceId,
			string? collectionId,
			CandidateMemoryReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<ICandidateMemoryReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.candidates.ready", "当前 provider 未注册 CandidateMemory review 存储。");
			}

			try
			{
				var service = services.GetRequiredService<CandidateMemoryReviewService>();
				var normalized = NormalizeCandidateMemoryReviewRequest(request, workspaceId, collectionId);
				var result = await service.MarkReadyForStableReviewAsync(id, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.candidates.ready", $"未找到 Candidate Memory：{id}", detailCode: "candidate_memory_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.candidates.ready");
			}
		})
		.WithName("MarkCandidateMemoryReadyForStableReview")
		.WithSummary("将 CandidateMemory 标记为 ready for stable review");

		// POST /api/memory/candidates/{id}/needs-more-evidence
		group.MapPost("/candidates/{id}/needs-more-evidence", async Task<IResult> (
			string id,
			string? workspaceId,
			string? collectionId,
			CandidateMemoryReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<ICandidateMemoryReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.candidates.needs-more-evidence", "当前 provider 未注册 CandidateMemory review 存储。");
			}

			try
			{
				var service = services.GetRequiredService<CandidateMemoryReviewService>();
				var normalized = NormalizeCandidateMemoryReviewRequest(request, workspaceId, collectionId);
				var result = await service.NeedsMoreEvidenceAsync(id, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.candidates.needs-more-evidence", $"未找到 Candidate Memory：{id}", detailCode: "candidate_memory_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.candidates.needs-more-evidence");
			}
		})
		.WithName("MarkCandidateMemoryNeedsMoreEvidence")
		.WithSummary("将 CandidateMemory 标记为需要更多证据");

		// POST /api/memory/candidates/{id}/reject
		group.MapPost("/candidates/{id}/reject", async Task<IResult> (
			string id,
			string? workspaceId,
			string? collectionId,
			CandidateMemoryReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<ICandidateMemoryReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.candidates.reject", "当前 provider 未注册 CandidateMemory review 存储。");
			}

			try
			{
				var service = services.GetRequiredService<CandidateMemoryReviewService>();
				var normalized = NormalizeCandidateMemoryReviewRequest(request, workspaceId, collectionId);
				var result = await service.RejectAsync(id, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.candidates.reject", $"未找到 Candidate Memory：{id}", detailCode: "candidate_memory_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.candidates.reject");
			}
		})
		.WithName("RejectCandidateMemory")
		.WithSummary("拒绝 CandidateMemory 并记录 review");

		// POST /api/memory/candidates/{id}/expire
		group.MapPost("/candidates/{id}/expire", async Task<IResult> (
			string id,
			string? workspaceId,
			string? collectionId,
			CandidateMemoryReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<ICandidateMemoryReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.candidates.expire", "当前 provider 未注册 CandidateMemory review 存储。");
			}

			try
			{
				var service = services.GetRequiredService<CandidateMemoryReviewService>();
				var normalized = NormalizeCandidateMemoryReviewRequest(request, workspaceId, collectionId);
				var result = await service.ExpireAsync(id, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.candidates.expire", $"未找到 Candidate Memory：{id}", detailCode: "candidate_memory_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.candidates.expire");
			}
		})
		.WithName("ExpireCandidateMemory")
		.WithSummary("过期 CandidateMemory 并记录 review");

		// POST /api/memory/candidates/{id}/supersede
		group.MapPost("/candidates/{id}/supersede", async Task<IResult> (
			string id,
			string? workspaceId,
			string? collectionId,
			CandidateMemoryReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<ICandidateMemoryReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.candidates.supersede", "当前 provider 未注册 CandidateMemory review 存储。");
			}

			try
			{
				var service = services.GetRequiredService<CandidateMemoryReviewService>();
				var normalized = NormalizeCandidateMemoryReviewRequest(request, workspaceId, collectionId);
				var result = await service.SupersedeAsync(id, normalized, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.candidates.supersede", $"未找到 Candidate Memory：{id}", detailCode: "candidate_memory_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.candidates.supersede");
			}
		})
		.WithName("SupersedeCandidateMemory")
		.WithSummary("将 CandidateMemory 标记为被另一个 candidate supersede");

		// GET /api/memory/candidates/{id}/reviews
		group.MapGet("/candidates/{id}/reviews", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<ICandidateMemoryReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.candidates.reviews", "当前 provider 未注册 CandidateMemory review 存储。");
			}

			try
			{
				var service = services.GetRequiredService<CandidateMemoryReviewService>();
				var reviews = await service.GetReviewsAsync(id, ct).ConfigureAwait(false);
				return Results.Ok(reviews);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.candidates.reviews");
			}
		})
		.WithName("GetCandidateMemoryReviews")
		.WithSummary("查询 CandidateMemory review history");

		// GET /api/memory/short-term/raw
		group.MapGet("/short-term/raw", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			string? sessionId,
			int? take,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			var store = services.GetService<IShortTermMemoryStore>();
			if (store is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.raw", "当前 provider 未注册短期记忆存储。");
			}

			var items = await store.QueryRawEventsAsync(new ShortTermRawEventQuery
			{
				WorkspaceId = workspaceId,
				CollectionId = collectionId,
				SessionId = sessionId,
				Take = take.GetValueOrDefault(100)
			}, ct);
			return Results.Ok(items);
		})
		.WithName("GetShortTermRawEvents")
		.WithSummary("查询短期原始事件");

		// GET /api/memory/short-term/working
		group.MapGet("/short-term/working", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			string? sessionId,
			int? take,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			var store = services.GetService<IShortTermMemoryStore>();
			if (store is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.working", "当前 provider 未注册短期记忆存储。");
			}

			var items = await store.QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
			{
				WorkspaceId = workspaceId,
				CollectionId = collectionId,
				SessionId = sessionId,
				Take = take.GetValueOrDefault(100)
			}, ct);
			return Results.Ok(items);
		})
		.WithName("GetShortTermWorkingItems")
		.WithSummary("查询短期工作项");

		// GET /api/memory/short-term/summary
		group.MapGet("/short-term/summary", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			string? sessionId,
			int? latestRawTake,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			var store = services.GetService<IShortTermMemoryStore>();
			if (store is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.summary", "当前 provider 未注册短期记忆存储。");
			}

			var summary = await store.GetSummaryAsync(new ShortTermSummaryQuery
			{
				WorkspaceId = workspaceId,
				CollectionId = collectionId,
				SessionId = sessionId,
				LatestRawTake = latestRawTake.GetValueOrDefault(10)
			}, ct);
			return Results.Ok(summary);
		})
		.WithName("GetShortTermMemorySummary")
		.WithSummary("查询短期记忆摘要");

		// POST /api/memory/short-term/compact
		group.MapPost("/short-term/compact", async Task<IResult> (
			ShortTermMemoryCompactionRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermMemoryStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.compact", "当前 provider 未注册短期记忆压缩服务。");
			}

			try
			{
				var compaction = services.GetRequiredService<ShortTermMemoryCompactionService>();
				var result = await compaction.CompactAsync(request, cancellationToken: ct).ConfigureAwait(false);
				return Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.short-term.compact");
			}
		})
		.WithName("CompactShortTermMemory")
		.WithSummary("执行短期记忆压缩与归档");

		// GET /api/memory/short-term/archive/summary
		group.MapGet("/short-term/archive/summary", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			string? sessionId,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermMemoryStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.archive.summary", "当前 provider 未注册短期记忆压缩服务。");
			}

			try
			{
				var compaction = services.GetRequiredService<ShortTermMemoryCompactionService>();
				var summary = await compaction.GetArchiveSummaryAsync(new ShortTermArchiveSummaryQuery
				{
					WorkspaceId = workspaceId,
					CollectionId = collectionId,
					SessionId = sessionId
				}, ct).ConfigureAwait(false);
				return Results.Ok(summary);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.short-term.archive.summary");
			}
		})
		.WithName("GetShortTermArchiveSummary")
		.WithSummary("查询短期记忆归档摘要");

		// GET /api/memory/short-term/archive/items
		group.MapGet("/short-term/archive/items", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			string? sessionId,
			string? kind,
			int? limit,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermMemoryStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.archive.items", "当前 provider 未注册短期记忆压缩服务。");
			}

			try
			{
				var compaction = services.GetRequiredService<ShortTermMemoryCompactionService>();
				var items = await compaction.GetArchiveItemsAsync(new ShortTermArchiveItemsQuery
				{
					WorkspaceId = workspaceId,
					CollectionId = collectionId,
					SessionId = sessionId,
					Kind = kind,
					Limit = limit.GetValueOrDefault(20)
				}, ct).ConfigureAwait(false);
				return Results.Ok(items);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.short-term.archive.items");
			}
		})
		.WithName("GetShortTermArchiveItems")
		.WithSummary("查询短期记忆归档明细");

		// GET /api/memory/short-term/compact/runs
		group.MapGet("/short-term/compact/runs", async Task<IResult> (
			string? workspaceId,
			string? collectionId,
			string? sessionId,
			string? trigger,
			int? take,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermMemoryStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.compact.runs", "当前 provider 未注册短期记忆压缩服务。");
			}

			try
			{
				var compaction = services.GetRequiredService<ShortTermMemoryCompactionService>();
				var runs = await compaction.GetCompactionRunsAsync(new ShortTermCompactionRunQuery
				{
					WorkspaceId = workspaceId,
					CollectionId = collectionId,
					SessionId = sessionId,
					Trigger = trigger,
					Take = take.GetValueOrDefault(20)
				}, ct).ConfigureAwait(false);
				return Results.Ok(runs);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.short-term.compact.runs");
			}
		})
		.WithName("GetShortTermCompactionRuns")
		.WithSummary("查询短期记忆压缩运行历史");

		// GET /api/memory/short-term/compact/runs/{runId}
		group.MapGet("/short-term/compact/runs/{runId}", async Task<IResult> (
			string runId,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermMemoryStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.compact.run", "当前 provider 未注册短期记忆压缩服务。");
			}

			try
			{
				var compaction = services.GetRequiredService<ShortTermMemoryCompactionService>();
				var run = await compaction.GetCompactionRunAsync(runId, ct).ConfigureAwait(false);
				return run is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.short-term.compact.run", $"未找到短期压缩运行记录：{runId}", detailCode: "short_term_compaction_run_not_found")
					: Results.Ok(run);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.short-term.compact.run");
			}
		})
		.WithName("GetShortTermCompactionRun")
		.WithSummary("按 RunId 查询短期记忆压缩运行记录");

		// POST /api/memory/short-term/promotion/candidates/generate
		group.MapPost("/short-term/promotion/candidates/generate", async Task<IResult> (
			ShortTermPromotionCandidateGenerationRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermPromotionCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.promotion.candidates.generate", "当前 provider 未注册短期晋升候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ShortTermPromotionCandidateService>();
				var candidates = await generator.GenerateAsync(request, ct).ConfigureAwait(false);
				return Results.Ok(candidates);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.short-term.promotion.candidates.generate");
			}
		})
		.WithName("GenerateShortTermPromotionCandidates")
		.WithSummary("生成短期记忆晋升候选项");

		// GET /api/memory/short-term/promotion/candidates
		group.MapGet("/short-term/promotion/candidates", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			string? sessionId,
			PromotionCandidateStatus? status,
			string? kind,
			string? suggestedTargetLayer,
			double? minConfidence,
			double? minImportance,
			int? limit,
			int? take,
			int? offset,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermPromotionCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.promotion.candidates", "当前 provider 未注册短期晋升候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ShortTermPromotionCandidateService>();
				var candidates = await generator.QueryAsync(new ShortTermPromotionCandidateQuery
				{
					WorkspaceId = workspaceId,
					CollectionId = collectionId,
					SessionId = sessionId,
					Status = status,
					Kind = kind,
					SuggestedTargetLayer = suggestedTargetLayer,
					MinConfidence = minConfidence,
					MinImportance = minImportance,
					Limit = limit ?? take.GetValueOrDefault(20),
					Offset = offset.GetValueOrDefault(0)
				}, ct).ConfigureAwait(false);
				return Results.Ok(candidates);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.short-term.promotion.candidates");
			}
		})
		.WithName("GetShortTermPromotionCandidates")
		.WithSummary("查询短期记忆晋升候选项");

		// GET /api/memory/short-term/promotion/candidates/{id}/explain
		group.MapGet("/short-term/promotion/candidates/{id}/explain", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermPromotionCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.promotion.candidate.explain", "当前 provider 未注册短期晋升候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ShortTermPromotionCandidateService>();
				var explanation = await generator.ExplainAsync(id, ct).ConfigureAwait(false);
				return explanation is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.short-term.promotion.candidate.explain", $"未找到短期晋升候选项或其来源：{id}", detailCode: "short_term_promotion_candidate_explain_not_found")
					: Results.Ok(explanation);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.short-term.promotion.candidate.explain");
			}
		})
		.WithName("ExplainShortTermPromotionCandidate")
		.WithSummary("解释短期记忆晋升候选项的来源与规则信息");

		// POST /api/memory/short-term/promotion/candidates/{id}/accept
		group.MapPost("/short-term/promotion/candidates/{id}/accept", async Task<IResult> (
			string id,
			PromotionCandidateReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermPromotionCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.short-term.promotion.candidate.accept", "当前 provider 未注册短期晋升候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ShortTermPromotionCandidateService>();
				var result = await generator.AcceptAsync(id, request, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.short-term.promotion.candidate.accept", $"未找到短期晋升候选项：{id}", detailCode: "short_term_promotion_candidate_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.short-term.promotion.candidate.accept");
			}
		})
		.WithName("AcceptShortTermPromotionCandidate")
		.WithSummary("接受短期晋升候选项并写入候选目标层");

		// POST /api/memory/short-term/promotion/candidates/{id}/reject
		group.MapPost("/short-term/promotion/candidates/{id}/reject", async Task<IResult> (
			string id,
			PromotionCandidateReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermPromotionCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.short-term.promotion.candidate.reject", "当前 provider 未注册短期晋升候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ShortTermPromotionCandidateService>();
				var result = await generator.RejectAsync(id, request, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.short-term.promotion.candidate.reject", $"未找到短期晋升候选项：{id}", detailCode: "short_term_promotion_candidate_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.short-term.promotion.candidate.reject");
			}
		})
		.WithName("RejectShortTermPromotionCandidate")
		.WithSummary("拒绝短期晋升候选项并记录审核历史");

		// POST /api/memory/short-term/promotion/candidates/{id}/expire
		group.MapPost("/short-term/promotion/candidates/{id}/expire", async Task<IResult> (
			string id,
			ReviewPromotionCandidateRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermPromotionCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.short-term.promotion.candidate.expire", "当前 provider 未注册短期晋升候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ShortTermPromotionCandidateService>();
				var result = await generator.ExpireAsync(id, request, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.short-term.promotion.candidate.expire", $"未找到短期晋升候选项：{id}", detailCode: "short_term_promotion_candidate_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.short-term.promotion.candidate.expire");
			}
		})
		.WithName("ExpireShortTermPromotionCandidate")
		.WithSummary("将短期晋升候选项标记为过期并记录审核历史");

		// GET /api/memory/short-term/promotion/candidates/{id}/reviews
		group.MapGet("/short-term/promotion/candidates/{id}/reviews", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermPromotionCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.promotion.candidate.reviews", "当前 provider 未注册短期晋升候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ShortTermPromotionCandidateService>();
				var candidate = await generator.GetAsync(id, ct).ConfigureAwait(false);
				if (candidate is null)
				{
					return ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.short-term.promotion.candidate.reviews", $"未找到短期晋升候选项：{id}", detailCode: "short_term_promotion_candidate_not_found");
				}

				var reviews = await generator.GetReviewsAsync(id, ct).ConfigureAwait(false);
				return Results.Ok(reviews);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.short-term.promotion.candidate.reviews");
			}
		})
		.WithName("GetShortTermPromotionCandidateReviews")
		.WithSummary("查询短期晋升候选项审核历史");

		// GET /api/memory/short-term/promotion/candidates/{id}
		group.MapGet("/short-term/promotion/candidates/{id}", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IShortTermPromotionCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.short-term.promotion.candidate", "当前 provider 未注册短期晋升候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ShortTermPromotionCandidateService>();
				var candidate = await generator.GetAsync(id, ct).ConfigureAwait(false);
				return candidate is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.short-term.promotion.candidate", $"未找到短期晋升候选项：{id}", detailCode: "short_term_promotion_candidate_not_found")
					: Results.Ok(candidate);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.short-term.promotion.candidate");
			}
		})
		.WithName("GetShortTermPromotionCandidate")
		.WithSummary("按 ID 查询短期记忆晋升候选项");

		// POST /api/memory/stable-review/candidates/generate
		group.MapPost("/stable-review/candidates/generate", async Task<IResult> (
			StableReviewCandidateGenerationRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IStableReviewCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.stable-review.candidates.generate", "当前 provider 未注册 Stable Review 候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<StableReviewCandidateService>();
				var candidates = await generator.GenerateAsync(request, ct).ConfigureAwait(false);
				return Results.Ok(candidates);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.stable-review.candidates.generate");
			}
		})
		.WithName("GenerateStableReviewCandidates")
		.WithSummary("从已接受短期晋升候选项生成 Stable Review 候选项");

		// GET /api/memory/stable-review/candidates
		group.MapGet("/stable-review/candidates", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			string? sessionId,
			string? status,
			string? validationStatus,
			string? kind,
			string? suggestedStableTarget,
			int? limit,
			int? take,
			int? offset,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IStableReviewCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.stable-review.candidates", "当前 provider 未注册 Stable Review 候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<StableReviewCandidateService>();
				var candidates = await generator.QueryAsync(new StableReviewCandidateQuery
				{
					WorkspaceId = workspaceId,
					CollectionId = collectionId,
					SessionId = sessionId,
					Status = status,
					ValidationStatus = validationStatus,
					Kind = kind,
					SuggestedStableTarget = suggestedStableTarget,
					Limit = limit ?? take.GetValueOrDefault(20),
					Offset = offset.GetValueOrDefault(0)
				}, ct).ConfigureAwait(false);
				return Results.Ok(candidates);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.stable-review.candidates");
			}
		})
		.WithName("GetStableReviewCandidates")
		.WithSummary("查询 Stable Review 候选项");

		// POST /api/memory/stable-review/candidates/{id}/accept
		group.MapPost("/stable-review/candidates/{id}/accept", async Task<IResult> (
			string id,
			StableReviewDecisionRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IStableReviewCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.stable-review.candidate.accept", "当前 provider 未注册 Stable Review 候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<StableReviewCandidateService>();
				var result = await generator.AcceptAsync(id, request, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.stable-review.candidate.accept", $"未找到 Stable Review 候选项：{id}", detailCode: "stable_review_candidate_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.stable-review.candidate.accept");
			}
		})
		.WithName("AcceptStableReviewCandidate")
		.WithSummary("接受 Stable Review 候选项并写入稳定目标层");

		// POST /api/memory/stable-review/candidates/{id}/reject
		group.MapPost("/stable-review/candidates/{id}/reject", async Task<IResult> (
			string id,
			StableReviewDecisionRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IStableReviewCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "memory.stable-review.candidate.reject", "当前 provider 未注册 Stable Review 候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<StableReviewCandidateService>();
				var result = await generator.RejectAsync(id, request, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "memory.stable-review.candidate.reject", $"未找到 Stable Review 候选项：{id}", detailCode: "stable_review_candidate_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "memory.stable-review.candidate.reject");
			}
		})
		.WithName("RejectStableReviewCandidate")
		.WithSummary("拒绝 Stable Review 候选项并记录审核历史");

		// GET /api/memory/stable-review/candidates/{id}/reviews
		group.MapGet("/stable-review/candidates/{id}/reviews", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IStableReviewCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.stable-review.candidate.reviews", "当前 provider 未注册 Stable Review 候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<StableReviewCandidateService>();
				var candidate = await generator.GetAsync(id, ct).ConfigureAwait(false);
				if (candidate is null)
				{
					return ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.stable-review.candidate.reviews", $"未找到 Stable Review 候选项：{id}", detailCode: "stable_review_candidate_not_found");
				}

				var reviews = await generator.GetReviewsAsync(id, ct).ConfigureAwait(false);
				return Results.Ok(reviews);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.stable-review.candidate.reviews");
			}
		})
		.WithName("GetStableReviewCandidateReviews")
		.WithSummary("查询 Stable Review 候选项审核历史");

		// GET /api/memory/stable-review/candidates/{id}/explain
		group.MapGet("/stable-review/candidates/{id}/explain", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IStableReviewCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.stable-review.candidate.explain", "当前 provider 未注册 Stable Review 候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<StableReviewCandidateService>();
				var explanation = await generator.ExplainAsync(id, ct).ConfigureAwait(false);
				return explanation is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.stable-review.candidate.explain", $"未找到 Stable Review 候选项：{id}", detailCode: "stable_review_candidate_not_found")
					: Results.Ok(explanation);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.stable-review.candidate.explain");
			}
		})
		.WithName("ExplainStableReviewCandidate")
		.WithSummary("解释 Stable Review 候选项来源、学习案例和校验状态");

		// GET /api/memory/stable-review/candidates/{id}
		group.MapGet("/stable-review/candidates/{id}", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IStableReviewCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "memory.stable-review.candidate", "当前 provider 未注册 Stable Review 候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<StableReviewCandidateService>();
				var candidate = await generator.GetAsync(id, ct).ConfigureAwait(false);
				return candidate is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "memory.stable-review.candidate", $"未找到 Stable Review 候选项：{id}", detailCode: "stable_review_candidate_not_found")
					: Results.Ok(candidate);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "memory.stable-review.candidate");
			}
		})
		.WithName("GetStableReviewCandidate")
		.WithSummary("按 ID 查询 Stable Review 候选项");

		// POST /api/memory/promote
		group.MapPost("/promote", async Task<IResult> (
			PromoteRequest req,
			IContextRuntimeService runtime,
			CancellationToken ct) =>
		{
			var record = await runtime.PromoteMemoryAsync(
				req.WorkspaceId,
				req.CollectionId,
				req.SourceMemoryId,
				req.Strategy,
				req.Reason,
				req.Confidence,
				ct,
				req.Reviewer);
			return Results.Ok(record);
		})
		.WithName("PromoteMemory")
		.WithSummary("将工作记忆晋升为稳定记忆");

		// POST /api/memory/reject
		group.MapPost("/reject", async Task<IResult> (
			PromoteRequest req,
			IMemoryPromotionService promotion,
			CancellationToken ct) =>
		{
			var record = await promotion.RejectAsync(
				req.WorkspaceId,
				req.CollectionId,
				req.SourceMemoryId,
				req.Strategy,
				req.Reason,
				req.Confidence,
				ct,
				req.Reviewer);
			return Results.Ok(record);
		})
		.WithName("RejectMemory")
		.WithSummary("将记忆条目标记为拒绝");

		// POST /api/memory/deprecate
		group.MapPost("/deprecate", async Task<IResult> (
			PromoteRequest req,
			IMemoryPromotionService promotion,
			CancellationToken ct) =>
		{
			var record = await promotion.DeprecateAsync(
				req.WorkspaceId,
				req.CollectionId,
				req.SourceMemoryId,
				req.Strategy,
				req.Reason,
				req.Confidence,
				ct,
				req.Reviewer);
			return Results.Ok(record);
		})
		.WithName("DeprecateMemory")
		.WithSummary("将记忆条目标记为废弃");

		return app;
	}

	private static CandidateMemoryReviewRequest NormalizeCandidateMemoryReviewRequest(
		CandidateMemoryReviewRequest request,
		string? workspaceId,
		string? collectionId)
	{
		return new CandidateMemoryReviewRequest
		{
			OperationId = request.OperationId,
			WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId)
				? workspaceId ?? string.Empty
				: request.WorkspaceId,
			CollectionId = string.IsNullOrWhiteSpace(request.CollectionId)
				? collectionId
				: request.CollectionId,
			Reviewer = request.Reviewer,
			Reason = request.Reason,
			SupersedeTargetCandidateId = request.SupersedeTargetCandidateId,
			Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
		};
	}

	private static StableLifecycleReviewRequest NormalizeStableLifecycleReviewRequest(
		StableLifecycleReviewRequest request,
		string? workspaceId,
		string? collectionId)
	{
		return new StableLifecycleReviewRequest
		{
			OperationId = request.OperationId,
			WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId)
				? workspaceId ?? string.Empty
				: request.WorkspaceId,
			CollectionId = string.IsNullOrWhiteSpace(request.CollectionId)
				? collectionId
				: request.CollectionId,
			Reviewer = request.Reviewer,
			Reason = request.Reason,
			ReplacementItemId = request.ReplacementItemId,
			AllowDeprecatedSupersededDeprecation = request.AllowDeprecatedSupersededDeprecation,
			Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
		};
	}

	/// <summary>记忆晋升请求体。</summary>
	internal sealed class PromoteRequest
	{
		/// <summary>工作区 ID。</summary>
		public string WorkspaceId { get; init; } = string.Empty;

		/// <summary>集合 ID。</summary>
		public string CollectionId { get; init; } = string.Empty;
		/// <summary>源记忆条目 ID。</summary>
		public string SourceMemoryId { get; init; } = string.Empty;
		/// <summary>晋升策略，默认为 "manual"。</summary>
		public string Strategy { get; init; } = "manual";
		/// <summary>晋升原因，可选。</summary>
		public string? Reason { get; init; }
		/// <summary>晋升置信度，默认为 1.0。</summary>
		public double Confidence { get; init; } = 1.0;
		/// <summary>审核人或执行人，可选。</summary>
		public string? Reviewer { get; init; }
	}

	/// <summary>工作记忆集合范围请求体。</summary>
	internal sealed class WorkingMemoryScopeRequest
	{
		/// <summary>工作区 ID。</summary>
		public string WorkspaceId { get; init; } = string.Empty;

		/// <summary>集合 ID。</summary>
		public string CollectionId { get; init; } = string.Empty;
	}
}



