using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>后台作业相关 HTTP 端点，负责入队和查询作业状态。</summary>
internal static class JobEndpoints
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
	{
		var group = app.MapGroup("/api/jobs")
			.WithTags("Jobs");

		group.MapPost("/compression", async Task<IResult> (
			CompressionRequest request,
			IContextJobQueue queue,
			CancellationToken ct) =>
		{
			var jobId = string.IsNullOrWhiteSpace(request.OperationId)
				? Guid.NewGuid().ToString("N")
				: request.OperationId;
			// 作业载荷保留原始压缩请求，后台 worker 可用同一 DTO 还原处理上下文。
			var job = new ContextJob
			{
				JobId = jobId,
				WorkspaceId = request.WorkspaceId,
				CollectionId = request.CollectionId,
				Kind = ContextJobKind.Compression,
				PayloadJson = JsonSerializer.Serialize(request, JsonOptions),
				Priority = 0,
				MaxRetryCount = 3,
				CreatedAt = DateTimeOffset.UtcNow
			};

			await queue.EnqueueAsync(job, ct);
			return Results.Accepted($"/api/jobs/{job.JobId}", job);
		})
		.WithName("EnqueueCompressionJob")
		.WithSummary("创建压缩后台作业");

		group.MapGet("", async Task<IResult> (
			string? workspaceId,
			string? collectionId,
			ContextJobState? state,
			int? take,
			IContextJobQueryStore jobs,
			CancellationToken ct) =>
		{
			var results = await jobs.QueryAsync(new ContextJobQuery
			{
				WorkspaceId = workspaceId,
				CollectionId = collectionId,
				State = state,
				Take = take.GetValueOrDefault(100)
			}, ct);
			return Results.Ok(results);
		})
		.WithName("QueryJobs")
		.WithSummary("查询后台作业");

		group.MapGet("/", async Task<IResult> (
			string? workspaceId,
			string? collectionId,
			ContextJobState? state,
			int? take,
			IContextJobQueryStore jobs,
			CancellationToken ct) =>
		{
			var results = await jobs.QueryAsync(new ContextJobQuery
			{
				WorkspaceId = workspaceId,
				CollectionId = collectionId,
				State = state,
				Take = take.GetValueOrDefault(100)
			}, ct);
			return Results.Ok(results);
		})
		.WithName("QueryJobsWithTrailingSlash")
		.WithSummary("查询后台作业");

		group.MapGet("/{id}", async Task<IResult> (
			string id,
			IContextJobQueryStore jobs,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			var results = await jobs.QueryAsync(new ContextJobQuery { Take = 10000 }, ct);
			var job = results.FirstOrDefault(item => string.Equals(item.JobId, id, StringComparison.OrdinalIgnoreCase));
			return job is null
				? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "jobs.get", $"未找到作业：{id}", detailCode: "job_not_found")
				: Results.Ok(job);
		})
		.WithName("GetJob")
		.WithSummary("按 ID 获取后台作业");

		// ── Worker 可观测性 ──────────────────────────────────────────────

		group.MapGet("/stats", async Task<IResult> (
			string? workspaceId,
			IContextJobQueryStore jobs,
			CancellationToken ct) =>
		{
			// 抓取最近 5000 条作业在内存中聚合统计，避免引入额外存储契约。
			var all = await jobs.QueryAsync(new ContextJobQuery
			{
				WorkspaceId = workspaceId,
				Take = 5000
			}, ct);

			var pending  = all.Count(j => j.State is ContextJobState.Queued or ContextJobState.WaitingRetry);
			var running  = all.Count(j => j.State == ContextJobState.Running);
			var failed   = all.Count(j => j.State == ContextJobState.Failed);
			var cancelled = all.Count(j => j.State == ContextJobState.Cancelled);
			var succeeded = all.Where(j => j.State == ContextJobState.Succeeded).ToList();
			var totalRetries = all.Sum(j => (long)j.RetryCount);

			var durations = succeeded
				.Where(j => j.StartedAt.HasValue && j.CompletedAt.HasValue)
				.Select(j => (j.CompletedAt!.Value - j.StartedAt!.Value).TotalMilliseconds)
				.ToList();
			var avgDurationMs = durations.Count > 0
				? Math.Round(durations.Average(), 1)
				: (double?)null;

			var lastErrorJob = all
				.Where(j => j.State == ContextJobState.Failed && j.CompletedAt.HasValue)
				.MaxBy(j => j.CompletedAt);
			var lastSuccessTime = succeeded
				.Where(j => j.CompletedAt.HasValue)
				.MaxBy(j => j.CompletedAt)?.CompletedAt;

			return Results.Ok(new ContextCoreJobStatsResponse
			{
				Pending = pending,
				Running = running,
				Succeeded = succeeded.Count,
				Failed = failed,
				Cancelled = cancelled,
				TotalRetries = totalRetries,
				AvgDurationMs = avgDurationMs,
				LastError = lastErrorJob is null ? null : new ContextCoreJobErrorSummary
				{
					JobId = lastErrorJob.JobId,
					Kind = lastErrorJob.Kind.ToString(),
					ErrorMessage = lastErrorJob.ErrorMessage,
					Time = lastErrorJob.CompletedAt
				},
				LastSuccessTime = lastSuccessTime,
				SampledTotal = all.Count
			});
		})
		.WithName("GetWorkerStats")
		.WithSummary("Worker 可观测性统计（pending/running/failed/avgDuration/lastError）");

		group.MapGet("/dead-letter", async Task<IResult> (
			string? workspaceId,
			int? take,
			IContextJobQueryStore jobs,
			CancellationToken ct) =>
		{
			var results = await jobs.QueryAsync(new ContextJobQuery
			{
				WorkspaceId = workspaceId,
				State = ContextJobState.Failed,
				Take = take.GetValueOrDefault(50)
			}, ct);
			// 最新失败优先排列，便于运维查看最近问题。
			var ordered = results.OrderByDescending(j => j.CompletedAt ?? j.CreatedAt).ToList();
			return Results.Ok(new ContextCoreDeadLetterJobsResponse
			{
				Count = ordered.Count,
				Items = ordered
			});
		})
		.WithName("GetDeadLetterJobs")
		.WithSummary("死信队列：查询 Failed 状态（已超过最大重试次数）的作业");

		group.MapPost("/{id}/requeue", async Task<IResult> (
			string id,
			IContextJobQueryStore jobs,
			IContextJobQueue queue,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			var all = await jobs.QueryAsync(new ContextJobQuery { Take = 10000 }, ct);
			var original = all.FirstOrDefault(j =>
				string.Equals(j.JobId, id, StringComparison.OrdinalIgnoreCase));

			if (original is null)
				return ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "jobs.requeue", $"作业 {id} 不存在。", detailCode: "job_not_found");

			if (original.State is not (ContextJobState.Failed or ContextJobState.Cancelled))
				return ContextCoreHttpResultMapper.InvalidRequest(
					httpContext,
					string.Empty,
					"jobs.requeue",
					$"作业 {id} 当前状态为 {original.State}，仅 Failed / Cancelled 状态可重新入队。",
					statusCode: StatusCodes.Status409Conflict);

			var newJobId = Guid.NewGuid().ToString("N");
			var requeued = new ContextJob
			{
				JobId        = newJobId,
				WorkspaceId  = original.WorkspaceId,
				CollectionId = original.CollectionId,
				Kind         = original.Kind,
				PayloadJson  = original.PayloadJson,
				Priority     = original.Priority,
				MaxRetryCount = original.MaxRetryCount,
				CreatedAt    = DateTimeOffset.UtcNow
			};

			await queue.EnqueueAsync(requeued, ct);
			return Results.Accepted($"/api/jobs/{newJobId}", new ContextCoreRequeueJobResponse
			{
				OriginalJobId = id,
				NewJobId = newJobId,
				Job = requeued
			});
		})
		.WithName("RequeueJob")
		.WithSummary("将 Failed / Cancelled 作业重新入队（生成新作业 ID，重置重试计数）");

		return app;
	}
}
