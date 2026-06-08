using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>上下文约束查询端点，暴露硬约束、软约束等规则数据。</summary>
internal static class ConstraintEndpoints
{
	public static IEndpointRouteBuilder MapConstraintEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/constraints", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			ConstraintLevel? level,
			int? take,
			IConstraintStore constraints,
			CancellationToken ct) =>
		{
			var results = await constraints.QueryAsync(new ContextConstraintQuery
			{
				WorkspaceId = workspaceId,
				CollectionId = collectionId,
				Level = level,
				Take = take.GetValueOrDefault(100)
			}, ct);
			return Results.Ok(results);
		})
		.WithTags("Constraints")
		.WithName("QueryConstraints")
		.WithSummary("查询约束条目");

		app.MapGet("/api/constraints/candidates", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			ContextMemoryStatus? status,
			int? limit,
			int? take,
			int? offset,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<ICandidateConstraintReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "constraints.candidates", "当前 provider 未注册 CandidateConstraint 审核存储。");
			}

			try
			{
				var service = services.GetRequiredService<CandidateConstraintReviewService>();
				var results = await service.QueryAsync(new CandidateConstraintQuery
				{
					WorkspaceId = workspaceId,
					CollectionId = collectionId,
					Status = status ?? ContextMemoryStatus.Candidate,
					Limit = limit ?? take.GetValueOrDefault(20),
					Offset = offset.GetValueOrDefault(0)
				}, ct).ConfigureAwait(false);
				return Results.Ok(results);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "constraints.candidates");
			}
		})
		.WithTags("Constraints")
		.WithName("GetCandidateConstraints")
		.WithSummary("查询 CandidateConstraint");

		app.MapGet("/api/constraints/candidates/{id}", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<ICandidateConstraintReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "constraints.candidate", "当前 provider 未注册 CandidateConstraint 审核存储。");
			}

			try
			{
				var service = services.GetRequiredService<CandidateConstraintReviewService>();
				var candidate = await service.GetAsync(id, ct).ConfigureAwait(false);
				return candidate is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "constraints.candidate", $"未找到 CandidateConstraint：{id}", detailCode: "candidate_constraint_not_found")
					: Results.Ok(candidate);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "constraints.candidate");
			}
		})
		.WithTags("Constraints")
		.WithName("GetCandidateConstraint")
		.WithSummary("按 ID 查询 CandidateConstraint");

		app.MapPost("/api/constraints/candidates/{id}/activate", async Task<IResult> (
			string id,
			CandidateConstraintReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<ICandidateConstraintReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "constraints.candidate.activate", "当前 provider 未注册 CandidateConstraint 审核存储。");
			}

			try
			{
				var service = services.GetRequiredService<CandidateConstraintReviewService>();
				var result = await service.ActivateAsync(id, request, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "constraints.candidate.activate", $"未找到 CandidateConstraint：{id}", detailCode: "candidate_constraint_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "constraints.candidate.activate");
			}
		})
		.WithTags("Constraints")
		.WithName("ActivateCandidateConstraint")
		.WithSummary("激活 CandidateConstraint 为 Active hard constraint");

		app.MapPost("/api/constraints/candidates/{id}/reject", async Task<IResult> (
			string id,
			CandidateConstraintReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<ICandidateConstraintReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "constraints.candidate.reject", "当前 provider 未注册 CandidateConstraint 审核存储。");
			}

			try
			{
				var service = services.GetRequiredService<CandidateConstraintReviewService>();
				var result = await service.RejectAsync(id, request, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "constraints.candidate.reject", $"未找到 CandidateConstraint：{id}", detailCode: "candidate_constraint_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "constraints.candidate.reject");
			}
		})
		.WithTags("Constraints")
		.WithName("RejectCandidateConstraint")
		.WithSummary("拒绝 CandidateConstraint 并记录审核历史");

		app.MapGet("/api/constraints/candidates/{id}/reviews", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<ICandidateConstraintReviewStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "constraints.candidate.reviews", "当前 provider 未注册 CandidateConstraint 审核存储。");
			}

			try
			{
				var service = services.GetRequiredService<CandidateConstraintReviewService>();
				var candidate = await service.GetAsync(id, ct).ConfigureAwait(false);
				if (candidate is null)
				{
					return ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "constraints.candidate.reviews", $"未找到 CandidateConstraint：{id}", detailCode: "candidate_constraint_not_found");
				}

				var reviews = await service.GetReviewsAsync(id, ct).ConfigureAwait(false);
				return Results.Ok(reviews);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "constraints.candidate.reviews");
			}
		})
		.WithTags("Constraints")
		.WithName("GetCandidateConstraintReviews")
		.WithSummary("查询 CandidateConstraint 审核历史");

		app.MapPost("/api/constraints/gaps/generate", async Task<IResult> (
			ConstraintGapGenerationRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IConstraintGapCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "constraints.gaps.generate", "当前 provider 未注册约束缺口候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ConstraintGapCandidateService>();
				var result = await generator.GenerateAsync(request, ct).ConfigureAwait(false);
				return Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "constraints.gaps.generate");
			}
		})
		.WithTags("Constraints")
		.WithName("GenerateConstraintGaps")
		.WithSummary("从 eval/report 输入生成约束缺口候选项，不写入 ConstraintStore");

		app.MapGet("/api/constraints/gaps", async Task<IResult> (
			string workspaceId,
			string? collectionId,
			string? sessionId,
			string? source,
			string? sourceSampleId,
			string? status,
			string? severity,
			int? limit,
			int? take,
			int? offset,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IConstraintGapCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "constraints.gaps", "当前 provider 未注册约束缺口候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ConstraintGapCandidateService>();
				var gaps = await generator.QueryAsync(new ConstraintGapCandidateQuery
				{
					WorkspaceId = workspaceId,
					CollectionId = collectionId,
					SessionId = sessionId,
					Source = source,
					SourceSampleId = sourceSampleId,
					Status = status,
					Severity = severity,
					Limit = limit ?? take.GetValueOrDefault(20),
					Offset = offset.GetValueOrDefault(0)
				}, ct).ConfigureAwait(false);
				return Results.Ok(gaps);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "constraints.gaps");
			}
		})
		.WithTags("Constraints")
		.WithName("GetConstraintGaps")
		.WithSummary("查询约束缺口候选项");

		app.MapGet("/api/constraints/gaps/{id}", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IConstraintGapCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "constraints.gap", "当前 provider 未注册约束缺口候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ConstraintGapCandidateService>();
				var gap = await generator.GetAsync(id, ct).ConfigureAwait(false);
				return gap is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "constraints.gap", $"未找到约束缺口候选项：{id}", detailCode: "constraint_gap_candidate_not_found")
					: Results.Ok(gap);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "constraints.gap");
			}
		})
		.WithTags("Constraints")
		.WithName("GetConstraintGap")
		.WithSummary("按 ID 查询约束缺口候选项");

		app.MapPost("/api/constraints/gaps/{id}/accept", async Task<IResult> (
			string id,
			ConstraintGapReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IConstraintGapCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "constraints.gap.accept", "当前 provider 未注册约束缺口候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ConstraintGapCandidateService>();
				var result = await generator.AcceptAsync(id, request, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "constraints.gap.accept", $"未找到约束缺口候选项：{id}", detailCode: "constraint_gap_candidate_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "constraints.gap.accept");
			}
		})
		.WithTags("Constraints")
		.WithName("AcceptConstraintGap")
		.WithSummary("接受约束缺口候选项并创建 CandidateConstraint");

		app.MapPost("/api/constraints/gaps/{id}/reject", async Task<IResult> (
			string id,
			ConstraintGapReviewRequest request,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IConstraintGapCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, request.OperationId, "constraints.gap.reject", "当前 provider 未注册约束缺口候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ConstraintGapCandidateService>();
				var result = await generator.RejectAsync(id, request, ct).ConfigureAwait(false);
				return result is null
					? ContextCoreHttpResultMapper.NotFound(httpContext, request.OperationId, "constraints.gap.reject", $"未找到约束缺口候选项：{id}", detailCode: "constraint_gap_candidate_not_found")
					: Results.Ok(result);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "constraints.gap.reject");
			}
		})
		.WithTags("Constraints")
		.WithName("RejectConstraintGap")
		.WithSummary("拒绝约束缺口候选项并记录审核历史");

		app.MapGet("/api/constraints/gaps/{id}/reviews", async Task<IResult> (
			string id,
			IServiceProvider services,
			HttpContext httpContext,
			CancellationToken ct) =>
		{
			if (services.GetService<IConstraintGapCandidateStore>() is null)
			{
				return ContextCoreHttpResultMapper.Misconfigured(httpContext, string.Empty, "constraints.gap.reviews", "当前 provider 未注册约束缺口候选项存储。");
			}

			try
			{
				var generator = services.GetRequiredService<ConstraintGapCandidateService>();
				var gap = await generator.GetAsync(id, ct).ConfigureAwait(false);
				if (gap is null)
				{
					return ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "constraints.gap.reviews", $"未找到约束缺口候选项：{id}", detailCode: "constraint_gap_candidate_not_found");
				}

				var reviews = await generator.GetReviewsAsync(id, ct).ConfigureAwait(false);
				return Results.Ok(reviews);
			}
			catch (Exception ex)
			{
				return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "constraints.gap.reviews");
			}
		})
		.WithTags("Constraints")
		.WithName("GetConstraintGapReviews")
		.WithSummary("查询约束缺口候选项审核历史");

		return app;
	}
}
