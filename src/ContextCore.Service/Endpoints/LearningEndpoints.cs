using ContextCore.Abstractions;
using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;
using ContextCore.Abstractions.Models;

namespace ContextCore.Service.Endpoints;

/// <summary>Context Learning Loop 的只读与案例创建端点。</summary>
internal static class LearningEndpoints
{
    public static IEndpointRouteBuilder MapLearningEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/learning")
            .WithTags("Learning");

        group.MapPost("/feedback", SubmitRuntimeLearningFeedbackAsync)
            .WithName("SubmitRuntimeLearningFeedback")
            .WithSummary("提交运行时学习反馈事件；仅采集，不改变正式策略")
            .Produces<LearningFeedbackSubmitResult>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/feedback", async Task<IResult> (
            string? workspaceId,
            string? collectionId,
            string? sessionId,
            string? candidateId,
            string? action,
            bool? runtimeFeedback,
            string? source,
            string? sourceOperationId,
            string? capabilityId,
            string? targetId,
            string? targetType,
            string? feedbackKind,
            int? limit,
            int? offset,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (runtimeFeedback.GetValueOrDefault()
                || !string.IsNullOrWhiteSpace(source)
                || !string.IsNullOrWhiteSpace(sourceOperationId)
                || !string.IsNullOrWhiteSpace(capabilityId)
                || !string.IsNullOrWhiteSpace(targetId)
                || !string.IsNullOrWhiteSpace(targetType)
                || !string.IsNullOrWhiteSpace(feedbackKind))
            {
                return await QueryRuntimeLearningFeedbackAsync(
                        workspaceId,
                        collectionId,
                        source,
                        sourceOperationId,
                        capabilityId,
                        targetId,
                        targetType,
                        feedbackKind,
                        limit,
                        offset,
                        services,
                        httpContext,
                        ct)
                    .ConfigureAwait(false);
            }

            var store = services.GetService<IContextLearningStore>();
            if (store is null)
            {
                return ContextCoreHttpResultMapper.Misconfigured(
                    httpContext,
                    string.Empty,
                    "learning.feedback",
                    "当前 provider 未注册晋升反馈信号存储。");
            }

            try
            {
                var feedback = await store.QueryFeedbackAsync(new PromotionFeedbackSignalQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    SessionId = sessionId,
                    CandidateId = candidateId,
                    Action = action,
                    Limit = limit.GetValueOrDefault(20),
                    Offset = offset.GetValueOrDefault(0)
                }, ct).ConfigureAwait(false);
                return Results.Ok(feedback);
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.feedback");
            }
        })
        .WithName("QueryLearningFeedback")
        .WithSummary("查询晋升反馈信号")
        .Produces<IReadOnlyList<PromotionFeedbackSignal>>(StatusCodes.Status200OK);

        group.MapGet("/feedback/summary", GetRuntimeLearningFeedbackSummaryAsync)
            .WithName("GetRuntimeLearningFeedbackSummary")
            .WithSummary("查询运行时学习反馈汇总；不改变正式策略")
            .Produces<LearningFeedbackSummaryReport>(StatusCodes.Status200OK);

        group.MapGet("/feedback/export", ExportRuntimeLearningFeedbackAsync)
            .WithName("ExportRuntimeLearningFeedback")
            .WithSummary("导出运行时学习反馈 JSONL；不改变正式策略")
            .Produces<string>(StatusCodes.Status200OK);

        group.MapPost("/feedback/{feedbackId}/review/approve", (
            string feedbackId,
            LearningFeedbackReviewRequest request,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
            ReviewRuntimeLearningFeedbackAsync(
                feedbackId,
                FeedbackReviewStatus.ApprovedForDataset,
                request,
                services,
                httpContext,
                ct))
            .WithName("ApproveRuntimeLearningFeedback")
            .WithSummary("批准运行时反馈进入离线数据集候选；不改变正式策略")
            .Produces<LearningFeedbackReviewResult>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapPost("/feedback/{feedbackId}/review/reject", (
            string feedbackId,
            LearningFeedbackReviewRequest request,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
            ReviewRuntimeLearningFeedbackAsync(
                feedbackId,
                FeedbackReviewStatus.Rejected,
                request,
                services,
                httpContext,
                ct))
            .WithName("RejectRuntimeLearningFeedback")
            .WithSummary("拒绝运行时反馈进入离线数据集候选；不改变正式策略")
            .Produces<LearningFeedbackReviewResult>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapPost("/feedback/{feedbackId}/review/needs-redaction", (
            string feedbackId,
            LearningFeedbackReviewRequest request,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
            ReviewRuntimeLearningFeedbackAsync(
                feedbackId,
                FeedbackReviewStatus.NeedsRedaction,
                request,
                services,
                httpContext,
                ct))
            .WithName("MarkRuntimeLearningFeedbackNeedsRedaction")
            .WithSummary("标记运行时反馈需要脱敏后再进入离线数据集候选")
            .Produces<LearningFeedbackReviewResult>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapPost("/feedback/{feedbackId}/review/needs-evidence", (
            string feedbackId,
            LearningFeedbackReviewRequest request,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
            ReviewRuntimeLearningFeedbackAsync(
                feedbackId,
                FeedbackReviewStatus.NeedsMoreEvidence,
                request,
                services,
                httpContext,
                ct))
            .WithName("MarkRuntimeLearningFeedbackNeedsEvidence")
            .WithSummary("标记运行时反馈需要更多证据后再进入离线数据集候选")
            .Produces<LearningFeedbackReviewResult>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/feedback/reviews", GetRuntimeLearningFeedbackReviewsAsync)
            .WithName("GetRuntimeLearningFeedbackReviews")
            .WithSummary("查询运行时反馈审核记录；不改变正式策略")
            .Produces<IReadOnlyList<LearningFeedbackReviewRecord>>(StatusCodes.Status200OK);

        group.MapGet("/feedback/reviews/summary", GetRuntimeLearningFeedbackReviewSummaryAsync)
            .WithName("GetRuntimeLearningFeedbackReviewSummary")
            .WithSummary("查询运行时反馈审核摘要；不改变正式策略")
            .Produces<LearningFeedbackReviewSummaryReport>(StatusCodes.Status200OK);

        group.MapGet("/records", async Task<IResult> (
            string? workspaceId,
            string? collectionId,
            string? sessionId,
            ContextFeedbackSignal? signal,
            ContextFailureType? failureType,
            string? sourceKind,
            string? sourceId,
            int? limit,
            int? offset,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var store = services.GetService<IContextLearningStore>();
            if (store is null)
            {
                return ContextCoreHttpResultMapper.Misconfigured(
                    httpContext,
                    string.Empty,
                    "learning.records",
                    "当前 provider 未注册学习记录存储。");
            }

            try
            {
                var records = await store.QueryRecordsAsync(new ContextLearningRecordQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    SessionId = sessionId,
                    Signal = signal,
                    FailureType = failureType,
                    SourceKind = sourceKind,
                    SourceId = sourceId,
                    Limit = limit.GetValueOrDefault(20),
                    Offset = offset.GetValueOrDefault(0)
                }, ct).ConfigureAwait(false);
                return Results.Ok(records);
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.records");
            }
        })
        .WithName("QueryLearningRecords")
        .WithSummary("查询上下文学习记录")
        .Produces<IReadOnlyList<ContextLearningRecord>>(StatusCodes.Status200OK);

        group.MapGet("/records/{id}", async Task<IResult> (
            string id,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var store = services.GetService<IContextLearningStore>();
            if (store is null)
            {
                return ContextCoreHttpResultMapper.Misconfigured(
                    httpContext,
                    string.Empty,
                    "learning.record",
                    "当前 provider 未注册学习记录存储。");
            }

            try
            {
                var record = await store.GetRecordAsync(id, ct).ConfigureAwait(false);
                return record is null
                    ? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "learning.record", $"未找到学习记录：{id}", detailCode: "learning_record_not_found")
                    : Results.Ok(record);
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.record");
            }
        })
        .WithName("GetLearningRecord")
        .WithSummary("按 ID 查询上下文学习记录")
        .Produces<ContextLearningRecord>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/cases", async Task<IResult> (
            string? workspaceId,
            string? collectionId,
            string? sessionId,
            ContextFeedbackSignal? signal,
            ContextFailureType? failureType,
            ContextLearningCaseStatus? status,
            string? caseKind,
            string? sourceRecordId,
            int? limit,
            int? offset,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var store = services.GetService<IContextLearningStore>();
            if (store is null)
            {
                return ContextCoreHttpResultMapper.Misconfigured(
                    httpContext,
                    string.Empty,
                    "learning.cases",
                    "当前 provider 未注册学习案例存储。");
            }

            try
            {
                var cases = await store.QueryCasesAsync(new ContextLearningCaseQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    SessionId = sessionId,
                    Signal = signal,
                    FailureType = failureType,
                    Status = status,
                    CaseKind = caseKind,
                    SourceRecordId = sourceRecordId,
                    Limit = limit.GetValueOrDefault(20),
                    Offset = offset.GetValueOrDefault(0)
                }, ct).ConfigureAwait(false);
                return Results.Ok(cases);
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.cases");
            }
        })
        .WithName("QueryLearningCases")
        .WithSummary("查询上下文学习案例")
        .Produces<IReadOnlyList<ContextLearningCase>>(StatusCodes.Status200OK);

        group.MapPost("/cases/generate", GenerateLearningCasesAsync)
            .WithName("GenerateLearningCases")
            .WithSummary("从学习记录生成规则型学习案例")
            .Produces<ContextLearningCaseGenerationResult>(StatusCodes.Status200OK);

        group.MapPost("/cases/{id}/activate", (
            string id,
            ContextLearningCaseStatusUpdateRequest request,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
            UpdateLearningCaseStatusAsync(
                id,
                ContextLearningCaseStatus.ActiveRegression,
                request,
                services,
                httpContext,
                ct))
            .WithName("ActivateLearningCase")
            .WithSummary("将学习案例激活为回归案例")
            .Produces<ContextLearningCaseStatusUpdateResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/cases/{id}/archive", (
            string id,
            ContextLearningCaseStatusUpdateRequest request,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
            UpdateLearningCaseStatusAsync(
                id,
                ContextLearningCaseStatus.Archived,
                request,
                services,
                httpContext,
                ct))
            .WithName("ArchiveLearningCase")
            .WithSummary("归档学习案例")
            .Produces<ContextLearningCaseStatusUpdateResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/cases/{id}/reject", (
            string id,
            ContextLearningCaseStatusUpdateRequest request,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
            UpdateLearningCaseStatusAsync(
                id,
                ContextLearningCaseStatus.Rejected,
                request,
                services,
                httpContext,
                ct))
            .WithName("RejectLearningCase")
            .WithSummary("拒绝学习案例")
            .Produces<ContextLearningCaseStatusUpdateResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/summary", GetLearningSummaryAsync)
            .WithName("GetLearningSummary")
            .WithSummary("查询上下文学习摘要")
            .Produces<ContextLearningSummary>(StatusCodes.Status200OK);

        group.MapGet("/regression/cases", async Task<IResult> (
            string? workspaceId,
            string? collectionId,
            string? sessionId,
            int? limit,
            int? offset,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var store = services.GetService<IContextLearningStore>();
            if (store is null)
            {
                return ContextCoreHttpResultMapper.Misconfigured(
                    httpContext,
                    string.Empty,
                    "learning.regression.cases",
                    "当前 provider 未注册学习案例存储。");
            }

            try
            {
                var cases = await store.QueryCasesAsync(new ContextLearningCaseQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    SessionId = sessionId,
                    Status = ContextLearningCaseStatus.ActiveRegression,
                    Limit = limit.GetValueOrDefault(20),
                    Offset = offset.GetValueOrDefault(0)
                }, ct).ConfigureAwait(false);
                return Results.Ok(cases);
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.regression.cases");
            }
        })
        .WithName("GetRegressionLearningCases")
        .WithSummary("查询已激活的学习回归案例")
        .Produces<IReadOnlyList<ContextLearningCase>>(StatusCodes.Status200OK);

        group.MapGet("/cases/{id}", async Task<IResult> (
            string id,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var store = services.GetService<IContextLearningStore>();
            if (store is null)
            {
                return ContextCoreHttpResultMapper.Misconfigured(
                    httpContext,
                    string.Empty,
                    "learning.case",
                    "当前 provider 未注册学习案例存储。");
            }

            try
            {
                var learningCase = await store.GetCaseAsync(id, ct).ConfigureAwait(false);
                return learningCase is null
                    ? ContextCoreHttpResultMapper.NotFound(httpContext, string.Empty, "learning.case", $"未找到学习案例：{id}", detailCode: "learning_case_not_found")
                    : Results.Ok(learningCase);
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.case");
            }
        })
        .WithName("GetLearningCase")
        .WithSummary("按 ID 查询上下文学习案例")
        .Produces<ContextLearningCase>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/cases", async Task<IResult> (
            ContextLearningCase learningCase,
            IServiceProvider services,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var store = services.GetService<IContextLearningStore>();
            if (store is null)
            {
                return ContextCoreHttpResultMapper.Misconfigured(
                    httpContext,
                    string.Empty,
                    "learning.case.create",
                    "当前 provider 未注册学习案例存储。");
            }

            if (string.IsNullOrWhiteSpace(learningCase.WorkspaceId)
                || string.IsNullOrWhiteSpace(learningCase.CollectionId))
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext,
                    string.Empty,
                    "learning.case.create",
                    "创建学习案例需要 workspaceId 和 collectionId。",
                    field: "workspaceId,collectionId");
            }

            try
            {
                var created = await store.AddCaseAsync(learningCase, ct).ConfigureAwait(false);
                return Results.Ok(created);
            }
            catch (Exception ex)
            {
                return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.case.create");
            }
        })
        .WithName("CreateLearningCase")
        .WithSummary("创建上下文学习案例")
        .Produces<ContextLearningCase>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<IResult> SubmitRuntimeLearningFeedbackAsync(
        LearningFeedbackSubmitRequest request,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var service = services.GetService<LearningFeedbackService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.feedback.runtime.submit",
                "当前 provider 未注册运行时学习反馈服务。");
        }

        try
        {
            var result = await service.SubmitAsync(request, ct)
                .ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "learning.feedback.runtime.submit",
                ex.Message);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.feedback.runtime.submit");
        }
    }

    private static async Task<IResult> QueryRuntimeLearningFeedbackAsync(
        string? workspaceId,
        string? collectionId,
        string? source,
        string? sourceOperationId,
        string? capabilityId,
        string? targetId,
        string? targetType,
        string? feedbackKind,
        int? limit,
        int? offset,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var service = services.GetService<LearningFeedbackService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.feedback.runtime",
                "当前 provider 未注册运行时学习反馈服务。");
        }

        try
        {
            var rows = await service.ListAsync(new LearningFeedbackEventQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Source = source,
                SourceOperationId = sourceOperationId,
                CapabilityId = capabilityId,
                TargetId = targetId,
                TargetType = targetType,
                FeedbackKind = feedbackKind,
                Limit = limit.GetValueOrDefault(100),
                Offset = offset.GetValueOrDefault(0)
            }, ct).ConfigureAwait(false);
            return Results.Ok(rows);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.feedback.runtime");
        }
    }

    private static async Task<IResult> GetRuntimeLearningFeedbackSummaryAsync(
        string? workspaceId,
        string? collectionId,
        string? source,
        string? sourceOperationId,
        string? capabilityId,
        string? targetId,
        string? targetType,
        string? feedbackKind,
        int? limit,
        int? offset,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var service = services.GetService<LearningFeedbackService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.feedback.runtime.summary",
                "当前 provider 未注册运行时学习反馈服务。");
        }

        try
        {
            var report = await service.BuildSummaryAsync(new LearningFeedbackEventQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Source = source,
                SourceOperationId = sourceOperationId,
                CapabilityId = capabilityId,
                TargetId = targetId,
                TargetType = targetType,
                FeedbackKind = feedbackKind,
                Limit = limit.GetValueOrDefault(20),
                Offset = offset.GetValueOrDefault(0)
            }, ct).ConfigureAwait(false);
            return Results.Ok(report);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.feedback.runtime.summary");
        }
    }

    private static async Task<IResult> ExportRuntimeLearningFeedbackAsync(
        string? workspaceId,
        string? collectionId,
        string? source,
        string? sourceOperationId,
        string? capabilityId,
        string? targetId,
        string? targetType,
        string? feedbackKind,
        int? limit,
        int? offset,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var service = services.GetService<LearningFeedbackService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.feedback.runtime.export",
                "当前 provider 未注册运行时学习反馈服务。");
        }

        try
        {
            var jsonl = await service.ExportJsonLinesAsync(new LearningFeedbackEventQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Source = source,
                SourceOperationId = sourceOperationId,
                CapabilityId = capabilityId,
                TargetId = targetId,
                TargetType = targetType,
                FeedbackKind = feedbackKind,
                Limit = limit.GetValueOrDefault(1000),
                Offset = offset.GetValueOrDefault(0)
            }, ct).ConfigureAwait(false);
            return Results.Text(jsonl, "application/x-ndjson");
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.feedback.runtime.export");
        }
    }

    private static async Task<IResult> ReviewRuntimeLearningFeedbackAsync(
        string feedbackId,
        FeedbackReviewStatus status,
        LearningFeedbackReviewRequest request,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var service = services.GetService<LearningFeedbackReviewService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.feedback.review",
                "当前 provider 未注册运行时学习反馈审核服务。");
        }

        try
        {
            var result = status switch
            {
                FeedbackReviewStatus.ApprovedForDataset => await service.ApproveAsync(feedbackId, request, ct)
                    .ConfigureAwait(false),
                FeedbackReviewStatus.Rejected => await service.RejectAsync(feedbackId, request, ct)
                    .ConfigureAwait(false),
                FeedbackReviewStatus.NeedsRedaction => await service.NeedsRedactionAsync(feedbackId, request, ct)
                    .ConfigureAwait(false),
                FeedbackReviewStatus.NeedsMoreEvidence => await service.NeedsMoreEvidenceAsync(feedbackId, request, ct)
                    .ConfigureAwait(false),
                _ => throw new ArgumentException($"Unsupported feedback review status: {status}")
            };
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "learning.feedback.review",
                ex.Message);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.feedback.review");
        }
    }

    private static async Task<IResult> GetRuntimeLearningFeedbackReviewsAsync(
        string? feedbackId,
        FeedbackReviewStatus? reviewStatus,
        string? reviewer,
        int? limit,
        int? offset,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var service = services.GetService<LearningFeedbackReviewService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.feedback.reviews",
                "当前 provider 未注册运行时学习反馈审核服务。");
        }

        try
        {
            var rows = await service.ListAsync(new LearningFeedbackReviewQuery
                {
                    FeedbackId = feedbackId,
                    ReviewStatus = reviewStatus,
                    Reviewer = reviewer,
                    Limit = limit.GetValueOrDefault(100),
                    Offset = offset.GetValueOrDefault(0)
                }, ct)
                .ConfigureAwait(false);
            return Results.Ok(rows);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.feedback.reviews");
        }
    }

    private static async Task<IResult> GetRuntimeLearningFeedbackReviewSummaryAsync(
        string? feedbackId,
        FeedbackReviewStatus? reviewStatus,
        string? reviewer,
        int? limit,
        int? offset,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var service = services.GetService<LearningFeedbackReviewService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.feedback.reviews.summary",
                "当前 provider 未注册运行时学习反馈审核服务。");
        }

        try
        {
            var report = await service.BuildSummaryAsync(
                    new LearningFeedbackEventQuery { Limit = int.MaxValue },
                    new LearningFeedbackReviewQuery
                    {
                        FeedbackId = feedbackId,
                        ReviewStatus = reviewStatus,
                        Reviewer = reviewer,
                        Limit = limit.GetValueOrDefault(100),
                        Offset = offset.GetValueOrDefault(0)
                    },
                    ct)
                .ConfigureAwait(false);
            return Results.Ok(report);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.feedback.reviews.summary");
        }
    }

    private static async Task<IResult> GenerateLearningCasesAsync(
        ContextLearningCaseGenerationRequest request,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var store = services.GetService<IContextLearningStore>();
        if (store is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.cases.generate",
                "当前 provider 未注册学习案例存储。");
        }

        var generator = services.GetService<IContextLearningCaseGenerator>();
        if (generator is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.cases.generate",
                "当前 provider 未注册学习案例生成器。");
        }

        try
        {
            var records = await store.QueryRecordsAsync(new ContextLearningRecordQuery
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                SessionId = request.SessionId,
                Signal = request.Signal,
                FailureType = request.FailureType,
                Limit = request.Limit > 0 ? request.Limit : 100,
                Offset = Math.Max(0, request.Offset)
            }, ct).ConfigureAwait(false);

            var cases = new List<ContextLearningCase>();
            var warnings = new List<string>();
            var created = 0;
            var existing = 0;
            foreach (var record in records)
            {
                var generated = generator.Generate(record);
                if (generated is null)
                {
                    warnings.Add($"record {record.RecordId} 未匹配到可生成学习案例的规则。");
                    continue;
                }

                var stored = await store.GetCaseAsync(generated.CaseId, ct).ConfigureAwait(false);
                if (stored is not null)
                {
                    existing++;
                    cases.Add(stored);
                    continue;
                }

                cases.Add(await store.AddCaseAsync(generated, ct).ConfigureAwait(false));
                created++;
            }

            return Results.Ok(new ContextLearningCaseGenerationResult
            {
                RecordsScanned = records.Count,
                Created = created,
                Existing = existing,
                Cases = cases,
                Warnings = warnings
            });
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.cases.generate");
        }
    }

    private static async Task<IResult> UpdateLearningCaseStatusAsync(
        string id,
        ContextLearningCaseStatus targetStatus,
        ContextLearningCaseStatusUpdateRequest request,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var store = services.GetService<IContextLearningStore>();
        if (store is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.case.status",
                "当前 provider 未注册学习案例存储。");
        }

        try
        {
            var learningCase = await store.GetCaseAsync(id, ct).ConfigureAwait(false);
            if (learningCase is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext,
                    request.OperationId,
                    "learning.case.status",
                    $"未找到学习案例：{id}",
                    detailCode: "learning_case_not_found");
            }

            var now = DateTimeOffset.UtcNow;
            var operationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? Guid.NewGuid().ToString("N")
                : request.OperationId.Trim();
            var metadata = new Dictionary<string, string>(learningCase.Metadata, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in request.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }

            metadata["previousStatus"] = learningCase.Status.ToString();
            metadata["status"] = targetStatus.ToString();
            metadata["statusUpdatedAt"] = now.ToString("O");
            metadata["statusOperationId"] = operationId;
            metadata["statusReviewer"] = string.IsNullOrWhiteSpace(request.Reviewer) ? "manual" : request.Reviewer.Trim();
            metadata["statusReason"] = request.Reason;

            var updated = new ContextLearningCase
            {
                CaseId = learningCase.CaseId,
                SourceType = learningCase.SourceType,
                WorkspaceId = learningCase.WorkspaceId,
                CollectionId = learningCase.CollectionId,
                SessionId = learningCase.SessionId,
                SourceRecordId = learningCase.SourceRecordId,
                SourceKind = learningCase.SourceKind,
                SourceId = learningCase.SourceId,
                CaseKind = learningCase.CaseKind,
                Title = learningCase.Title,
                Summary = learningCase.Summary,
                InputSummary = learningCase.InputSummary,
                ExpectedBehavior = learningCase.ExpectedBehavior,
                Signal = learningCase.Signal,
                FailureType = learningCase.FailureType,
                CorrectionReason = learningCase.CorrectionReason,
                Status = targetStatus,
                EvidenceRefs = learningCase.EvidenceRefs.ToArray(),
                PositiveRefs = learningCase.PositiveRefs.ToArray(),
                NegativeRefs = learningCase.NegativeRefs.ToArray(),
                CreatedAt = learningCase.CreatedAt,
                Metadata = metadata
            };

            var saved = await store.AddCaseAsync(updated, ct).ConfigureAwait(false);
            return Results.Ok(new ContextLearningCaseStatusUpdateResponse
            {
                OperationId = operationId,
                CaseId = saved.CaseId,
                Status = saved.Status,
                Case = saved
            });
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, request.OperationId, "learning.case.status");
        }
    }

    private static async Task<IResult> GetLearningSummaryAsync(
        string? workspaceId,
        string? collectionId,
        string? sessionId,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var store = services.GetService<IContextLearningStore>();
        if (store is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.summary",
                "当前 provider 未注册学习存储。");
        }

        try
        {
            var records = await store.QueryRecordsAsync(new ContextLearningRecordQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                SessionId = sessionId,
                Limit = int.MaxValue
            }, ct).ConfigureAwait(false);
            var cases = await store.QueryCasesAsync(new ContextLearningCaseQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                SessionId = sessionId,
                Limit = int.MaxValue
            }, ct).ConfigureAwait(false);

            return Results.Ok(new ContextLearningSummary
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                RecordCount = records.Count,
                CaseCount = cases.Count,
                PositiveCount = records.Count(record => record.Signal == ContextFeedbackSignal.Positive),
                NegativeCount = records.Count(record => record.Signal == ContextFeedbackSignal.Negative),
                StaleCount = records.Count(record => record.Signal == ContextFeedbackSignal.Stale),
                DraftCaseCount = cases.Count(item => item.Status == ContextLearningCaseStatus.Draft),
                CandidateCaseCount = cases.Count(item => item.Status == ContextLearningCaseStatus.Candidate),
                ActiveRegressionCaseCount = cases.Count(item => item.Status == ContextLearningCaseStatus.ActiveRegression),
                ArchivedCaseCount = cases.Count(item => item.Status == ContextLearningCaseStatus.Archived),
                RejectedCaseCount = cases.Count(item => item.Status == ContextLearningCaseStatus.Rejected),
                FailureTypeCounts = records
                    .GroupBy(static record => record.FailureType)
                    .ToDictionary(static group => group.Key, static group => group.Count()),
                CaseKindCounts = cases
                    .GroupBy(static learningCase => learningCase.CaseKind, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase)
            });
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.summary");
        }
    }
}
