using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>Vector Index V1 的只读状态、诊断和 reindex preview 端点。</summary>
internal static class VectorEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapVectorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vector")
            .WithTags("Vector");

        group.MapGet("/status", GetStatusAsync)
            .WithName("GetVectorStatus")
            .WithSummary("查询 V1 vector index 状态");

        group.MapGet("/diagnostics", GetDiagnosticsAsync)
            .WithName("GetVectorDiagnostics")
            .WithSummary("查询 V1 vector index 诊断");

        group.MapPost("/reindex-preview", PreviewReindexAsync)
            .WithName("PreviewVectorReindex")
            .WithSummary("预览 V1 vector index reindex 动作，不写入存储");

        group.MapPost("/query-preview", PreviewQueryAsync)
            .WithName("PreviewVectorQuery")
            .WithSummary("预览 V3 vector query 结果，不影响正式 retrieval/package");

        group.MapPost("/reindex-plan", CreateReindexPlanAsync)
            .WithName("CreateVectorReindexPlan")
            .WithSummary("创建 vector reindex 计划，不写入存储");

        group.MapPost("/reindex-submit", SubmitReindexAsync)
            .WithName("SubmitVectorReindex")
            .WithSummary("提交 vector_reindex 后台作业");

        group.MapGet("/reindex-reports", QueryReindexReportsAsync)
            .WithName("QueryVectorReindexReports")
            .WithSummary("查询 vector reindex 报告");

        group.MapGet("/reindex-reports/{id}", GetReindexReportAsync)
            .WithName("GetVectorReindexReport")
            .WithSummary("按 ID 查询 vector reindex 报告");

        group.MapPost("/lifecycle-metadata/review-candidates/generate", GenerateLifecycleMetadataReviewCandidatesAsync)
            .WithName("GenerateVectorLifecycleMetadataReviewCandidates")
            .WithSummary("从 lifecycle metadata repair plan 生成只读人工 review 候选项");

        group.MapGet("/lifecycle-metadata/review-candidates", QueryLifecycleMetadataReviewCandidatesAsync)
            .WithName("QueryVectorLifecycleMetadataReviewCandidates")
            .WithSummary("查询 lifecycle metadata review 候选项");

        group.MapGet("/lifecycle-metadata/review-candidates/{id}", GetLifecycleMetadataReviewCandidateAsync)
            .WithName("GetVectorLifecycleMetadataReviewCandidate")
            .WithSummary("按 ID 查询 lifecycle metadata review 候选项");

        group.MapGet("/lifecycle-metadata/review-candidates/{id}/explain", ExplainLifecycleMetadataReviewCandidateAsync)
            .WithName("ExplainVectorLifecycleMetadataReviewCandidate")
            .WithSummary("解释 lifecycle metadata review 候选项证据与风险");

        group.MapPost("/lifecycle-metadata/review-candidates/{id}/approve", ApproveLifecycleMetadataReviewCandidateAsync)
            .WithName("ApproveVectorLifecycleMetadataReviewCandidate")
            .WithSummary("批准 lifecycle metadata review candidate 写入 sidecar metadata");

        group.MapPost("/lifecycle-metadata/review-candidates/{id}/reject", RejectLifecycleMetadataReviewCandidateAsync)
            .WithName("RejectVectorLifecycleMetadataReviewCandidate")
            .WithSummary("拒绝 lifecycle metadata review candidate，不写 sidecar");

        group.MapPost("/lifecycle-metadata/review-candidates/{id}/needs-evidence", NeedsEvidenceLifecycleMetadataReviewCandidateAsync)
            .WithName("NeedsEvidenceVectorLifecycleMetadataReviewCandidate")
            .WithSummary("标记 lifecycle metadata review candidate 需要更多证据");

        group.MapPost("/lifecycle-metadata/review-candidates/{id}/supersede", SupersedeLifecycleMetadataReviewCandidateAsync)
            .WithName("SupersedeVectorLifecycleMetadataReviewCandidate")
            .WithSummary("标记 lifecycle metadata review candidate 已被替代");

        group.MapGet("/lifecycle-metadata/review-candidates/{id}/reviews", ListLifecycleMetadataReviewCandidateReviewsAsync)
            .WithName("ListVectorLifecycleMetadataReviewCandidateReviews")
            .WithSummary("查询 lifecycle metadata review candidate 决策历史");

        group.MapGet("/lifecycle-metadata/sidecar", ListLifecycleMetadataSidecarAsync)
            .WithName("ListVectorLifecycleMetadataSidecar")
            .WithSummary("查询 lifecycle metadata sidecar preview");

        return app;
    }

    private static async Task<IResult> GetStatusAsync(
        string workspaceId,
        string collectionId,
        VectorIndexService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.GetStatusAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.status");
        }
    }

    private static async Task<IResult> GetDiagnosticsAsync(
        string workspaceId,
        string collectionId,
        VectorIndexService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.GetDiagnosticsAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.diagnostics");
        }
    }

    private static async Task<IResult> PreviewReindexAsync(
        VectorReindexPreviewRequest request,
        VectorIndexService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.PreviewReindexAsync(request, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.reindex-preview");
        }
    }

    private static async Task<IResult> PreviewQueryAsync(
        VectorQueryPreviewRequest request,
        VectorQueryPreviewService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.PreviewAsync(request, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.query-preview");
        }
    }

    private static async Task<IResult> CreateReindexPlanAsync(
        VectorReindexRequest request,
        VectorReindexPlanner planner,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var normalized = NormalizeRequest(request, forceDryRun: true);
            return Results.Ok(await planner.CreatePlanAsync(normalized, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.reindex-plan");
        }
    }

    private static async Task<IResult> SubmitReindexAsync(
        VectorReindexRequest request,
        VectorReindexPlanner planner,
        IContextJobQueue queue,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.Apply && !request.DryRun && !request.ConfirmApply)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    request.OperationId,
                    "vector.reindex-submit",
                    "Apply reindex 需要 ConfirmApply=true。",
                    field: nameof(request.ConfirmApply));
            }

            var normalized = NormalizeRequest(request, forceDryRun: false);
            var plan = await planner.CreatePlanAsync(normalized, cancellationToken).ConfigureAwait(false);
            var jobId = string.IsNullOrWhiteSpace(normalized.OperationId)
                ? $"vector-reindex-{Guid.NewGuid():N}"
                : normalized.OperationId;
            var job = new ContextJob
            {
                JobId = jobId,
                WorkspaceId = normalized.WorkspaceId,
                CollectionId = normalized.CollectionId,
                Kind = ContextJobKind.VectorReindex,
                PayloadJson = JsonSerializer.Serialize(normalized, JsonOptions),
                Priority = 0,
                MaxRetryCount = 1,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await queue.EnqueueAsync(job, cancellationToken).ConfigureAwait(false);
            return Results.Accepted($"/api/jobs/{job.JobId}", new VectorReindexSubmitResponse
            {
                Job = job,
                Plan = plan
            });
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.reindex-submit");
        }
    }

    private static async Task<IResult> QueryReindexReportsAsync(
        string workspaceId,
        string collectionId,
        int? take,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var store = services.GetService<IVectorReindexReportStore>();
        if (store is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "vector.reindex-reports",
                "当前 provider 未注册 vector reindex report store。");
        }

        try
        {
            var reports = await store.QueryAsync(
                workspaceId,
                collectionId,
                take.GetValueOrDefault(20),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new VectorReindexReportQueryResponse
            {
                Reports = reports,
                Count = reports.Count
            });
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.reindex-reports");
        }
    }

    private static async Task<IResult> GetReindexReportAsync(
        string id,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var store = services.GetService<IVectorReindexReportStore>();
        if (store is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "vector.reindex-report",
                "当前 provider 未注册 vector reindex report store。");
        }

        try
        {
            var report = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return report is null
                ? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "vector.reindex-report", $"未找到 vector reindex report：{id}", detailCode: "vector_reindex_report_not_found")
                : Results.Ok(report);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.reindex-report");
        }
    }

    private static VectorReindexRequest NormalizeRequest(VectorReindexRequest request, bool forceDryRun)
    {
        return new VectorReindexRequest
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? $"vector-reindex-{Guid.NewGuid():N}"
                : request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Layer = request.Layer,
            ItemKind = request.ItemKind,
            Layers = request.Layers,
            DryRun = forceDryRun || request.DryRun || !request.Apply,
            Apply = !forceDryRun && request.Apply,
            ConfirmApply = request.ConfirmApply,
            Force = request.Force,
            BatchSize = request.BatchSize > 0 ? request.BatchSize : 50,
            MaxItems = request.MaxItems > 0 ? request.MaxItems : 200,
            IncludeContextItems = request.IncludeContextItems,
            IncludeMemoryItems = request.IncludeMemoryItems,
            Metadata = request.Metadata
        };
    }

    private static async Task<IResult> GenerateLifecycleMetadataReviewCandidatesAsync(
        VectorLifecycleMetadataReviewCandidateGenerationRequest request,
        VectorLifecycleMetadataReviewCandidateService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var sourcePath = ResolveSafeRepairPlanPath(request.RepairPlanReportPath);
            if (!File.Exists(sourcePath))
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext,
                    string.Empty,
                    "vector.lifecycle-metadata.review-candidates.generate",
                    $"未找到 vector lifecycle metadata repair plan：{sourcePath}",
                    detailCode: "vector_lifecycle_metadata_repair_plan_not_found");
            }

            var json = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            var summary = JsonSerializer.Deserialize<VectorLifecycleMetadataRepairPlanSummaryReport>(json, JsonOptions);
            if (summary is null)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    string.Empty,
                    "vector.lifecycle-metadata.review-candidates.generate",
                    "repair plan summary 无法反序列化。",
                    field: nameof(request.RepairPlanReportPath));
            }

            var result = await service.GenerateAsync(request, summary, NormalizeRelativePath(sourcePath), cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.lifecycle-metadata.review-candidates.generate");
        }
    }

    private static async Task<IResult> QueryLifecycleMetadataReviewCandidatesAsync(
        string workspaceId,
        string? collectionId,
        string? status,
        string? layer,
        string? itemKind,
        string? mustHitItemId,
        string? sourceEvalSet,
        int? limit,
        int? offset,
        VectorLifecycleMetadataReviewCandidateService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await service.QueryAsync(new VectorLifecycleMetadataReviewCandidateQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Status = status,
                Layer = layer,
                ItemKind = itemKind,
                MustHitItemId = mustHitItemId,
                SourceEvalSet = sourceEvalSet,
                Limit = limit.GetValueOrDefault(50),
                Offset = offset.GetValueOrDefault()
            }, cancellationToken).ConfigureAwait(false);
            return Results.Ok(results);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.lifecycle-metadata.review-candidates");
        }
    }

    private static async Task<IResult> GetLifecycleMetadataReviewCandidateAsync(
        string id,
        VectorLifecycleMetadataReviewCandidateService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidate = await service.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return candidate is null
                ? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "vector.lifecycle-metadata.review-candidate", $"未找到 lifecycle metadata review candidate：{id}", detailCode: "vector_lifecycle_metadata_review_candidate_not_found")
                : Results.Ok(candidate);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.lifecycle-metadata.review-candidate");
        }
    }

    private static async Task<IResult> ExplainLifecycleMetadataReviewCandidateAsync(
        string id,
        VectorLifecycleMetadataReviewCandidateService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var explanation = await service.ExplainAsync(id, cancellationToken).ConfigureAwait(false);
            return explanation is null
                ? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "vector.lifecycle-metadata.review-candidate.explain", $"未找到 lifecycle metadata review candidate：{id}", detailCode: "vector_lifecycle_metadata_review_candidate_not_found")
                : Results.Ok(explanation);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.lifecycle-metadata.review-candidate.explain");
        }
    }

    private static Task<IResult> ApproveLifecycleMetadataReviewCandidateAsync(
        string id,
        VectorLifecycleMetadataReviewRequest request,
        VectorLifecycleMetadataReviewService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
        => ReviewLifecycleMetadataCandidateAsync(id, request, VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, service, httpContext, cancellationToken);

    private static Task<IResult> RejectLifecycleMetadataReviewCandidateAsync(
        string id,
        VectorLifecycleMetadataReviewRequest request,
        VectorLifecycleMetadataReviewService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
        => ReviewLifecycleMetadataCandidateAsync(id, request, VectorLifecycleMetadataReviewDecisions.Reject, service, httpContext, cancellationToken);

    private static Task<IResult> NeedsEvidenceLifecycleMetadataReviewCandidateAsync(
        string id,
        VectorLifecycleMetadataReviewRequest request,
        VectorLifecycleMetadataReviewService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
        => ReviewLifecycleMetadataCandidateAsync(id, request, VectorLifecycleMetadataReviewDecisions.NeedsEvidence, service, httpContext, cancellationToken);

    private static Task<IResult> SupersedeLifecycleMetadataReviewCandidateAsync(
        string id,
        VectorLifecycleMetadataReviewRequest request,
        VectorLifecycleMetadataReviewService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
        => ReviewLifecycleMetadataCandidateAsync(id, request, VectorLifecycleMetadataReviewDecisions.Supersede, service, httpContext, cancellationToken);

    private static async Task<IResult> ReviewLifecycleMetadataCandidateAsync(
        string id,
        VectorLifecycleMetadataReviewRequest request,
        string decision,
        VectorLifecycleMetadataReviewService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var normalized = new VectorLifecycleMetadataReviewRequest
            {
                CandidateId = id,
                Decision = decision,
                Reviewer = request.Reviewer,
                Reason = request.Reason,
                ProposedLifecycle = request.ProposedLifecycle,
                ProposedReviewStatus = request.ProposedReviewStatus,
                ProposedTargetSection = request.ProposedTargetSection,
                EvidenceRefs = request.EvidenceRefs,
                SourceRefs = request.SourceRefs,
                Confirmed = request.Confirmed,
                Metadata = request.Metadata
            };
            var result = await service.ReviewAsync(normalized, cancellationToken).ConfigureAwait(false);
            return result.Succeeded || result.UnsafeApprovalBlocked
                ? Results.Ok(result)
                : ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "vector.lifecycle-metadata.review-candidate.review", result.BlockedReason, detailCode: "vector_lifecycle_metadata_review_candidate_not_found");
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.lifecycle-metadata.review-candidate.review");
        }
    }

    private static async Task<IResult> ListLifecycleMetadataReviewCandidateReviewsAsync(
        string id,
        VectorLifecycleMetadataReviewService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.ListReviewsAsync(id, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.lifecycle-metadata.review-candidate.reviews");
        }
    }

    private static async Task<IResult> ListLifecycleMetadataSidecarAsync(
        string workspaceId,
        string? collectionId,
        VectorLifecycleMetadataReviewService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.ListSidecarAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "vector.lifecycle-metadata.sidecar");
        }
    }

    private static string ResolveSafeRepairPlanPath(string? requestedPath)
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "vector", "eligibility"));
        var path = string.IsNullOrWhiteSpace(requestedPath)
            ? Path.Combine(root, "vector-lifecycle-metadata-repair-plan-summary.json")
            : Path.GetFullPath(Path.IsPathRooted(requestedPath)
                ? requestedPath
                : Path.Combine(Environment.CurrentDirectory, requestedPath));

        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("repair plan path 必须位于 vector/eligibility 目录下。", nameof(requestedPath));
        }

        return path;
    }

    private static string NormalizeRelativePath(string path)
    {
        var full = Path.GetFullPath(path);
        var cwd = Path.GetFullPath(Environment.CurrentDirectory);
        return full.StartsWith(cwd + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(cwd, full)
            : full;
    }
}
