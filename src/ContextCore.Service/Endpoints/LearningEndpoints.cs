using ContextCore.Abstractions;
using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>Context Learning Loop 的只读与案例创建端点。</summary>
internal static class LearningEndpoints
{
    public static IEndpointRouteBuilder MapLearningEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/learning")
            .WithTags("Learning");

        group.MapGet("/feedback", async Task<IResult> (
            string? workspaceId,
            string? collectionId,
            string? sessionId,
            string? candidateId,
            string? action,
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
        .WithSummary("查询晋升反馈信号");

        group.MapGet("/policy-feedback", GetPolicyFeedbackAsync)
            .WithName("GetPolicyFeedback")
            .WithSummary("查询策略反馈数据集");

        group.MapGet("/policy-feedback/export", ExportPolicyFeedbackAsync)
            .WithName("ExportPolicyFeedback")
            .WithSummary("导出 JSONL 策略反馈记录");

        group.MapGet("/features", GetLearningFeaturesAsync)
            .WithName("GetLearningFeatures")
            .WithSummary("查询 Learning Feature Dataset");

        group.MapGet("/features/export", ExportLearningFeaturesAsync)
            .WithName("ExportLearningFeatures")
            .WithSummary("导出 JSONL Learning Feature Dataset");

        group.MapGet("/features/quality", GetLearningFeatureQualityAsync)
            .WithName("GetLearningFeatureQuality")
            .WithSummary("查询 Learning Feature Dataset 质量报告");

        group.MapGet("/ranker-shadow/traces", GetRankerShadowTracesAsync)
            .WithName("GetRankerShadowTraces")
            .WithSummary("导出 lifecycle-aware ranker shadow traces，不影响 retrieval output");

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
        .WithSummary("查询上下文学习记录");

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
        .WithSummary("按 ID 查询上下文学习记录");

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
        .WithSummary("查询上下文学习案例");

        group.MapPost("/cases/generate", GenerateLearningCasesAsync)
            .WithName("GenerateLearningCases")
            .WithSummary("从学习记录生成规则型学习案例");

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
            .WithSummary("将学习案例激活为回归案例");

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
            .WithSummary("归档学习案例");

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
            .WithSummary("拒绝学习案例");

        group.MapGet("/summary", GetLearningSummaryAsync)
            .WithName("GetLearningSummary")
            .WithSummary("查询上下文学习摘要");

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
        .WithSummary("查询已激活的学习回归案例");

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
        .WithSummary("按 ID 查询上下文学习案例");

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
        .WithSummary("创建上下文学习案例");

        return app;
    }

    private static async Task<IResult> GetPolicyFeedbackAsync(
        string? workspaceId,
        string? collectionId,
        string? sessionId,
        int? limit,
        int? offset,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "learning.policy-feedback",
                "查询策略反馈数据集需要 workspaceId。",
                field: "workspaceId");
        }

        var service = services.GetService<PolicyFeedbackDatasetService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.policy-feedback",
                "当前 provider 未注册策略反馈数据集服务。");
        }

        try
        {
            var dataset = await service.BuildAsync(
                workspaceId,
                collectionId,
                sessionId,
                limit.GetValueOrDefault(200),
                offset.GetValueOrDefault(0),
                ct).ConfigureAwait(false);
            return Results.Ok(dataset);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.policy-feedback");
        }
    }

    private static async Task<IResult> ExportPolicyFeedbackAsync(
        string? workspaceId,
        string? collectionId,
        string? sessionId,
        int? limit,
        int? offset,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "learning.policy-feedback.export",
                "导出策略反馈数据集需要 workspaceId。",
                field: "workspaceId");
        }

        var service = services.GetService<PolicyFeedbackDatasetService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.policy-feedback.export",
                "当前 provider 未注册策略反馈数据集服务。");
        }

        try
        {
            var jsonl = await service.ExportJsonLinesAsync(
                workspaceId,
                collectionId,
                sessionId,
                limit.GetValueOrDefault(1000),
                offset.GetValueOrDefault(0),
                ct).ConfigureAwait(false);
            return Results.Text(jsonl, "application/x-ndjson");
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.policy-feedback.export");
        }
    }

    private static async Task<IResult> GetLearningFeaturesAsync(
        string? workspaceId,
        string? collectionId,
        string? sessionId,
        int? limit,
        int? offset,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "learning.features",
                "查询 Learning Feature Dataset 需要 workspaceId。",
                field: "workspaceId");
        }

        var service = services.GetService<LearningFeatureDatasetService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.features",
                "当前 provider 未注册 Learning Feature Dataset 服务。");
        }

        try
        {
            var dataset = await service.BuildAsync(
                workspaceId,
                collectionId,
                sessionId,
                limit.GetValueOrDefault(500),
                offset.GetValueOrDefault(0),
                cancellationToken: ct).ConfigureAwait(false);
            return Results.Ok(dataset);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.features");
        }
    }

    private static async Task<IResult> ExportLearningFeaturesAsync(
        string? workspaceId,
        string? collectionId,
        string? sessionId,
        string? outputDirectory,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "learning.features.export",
                "导出 Learning Feature Dataset 需要 workspaceId。",
                field: "workspaceId");
        }

        var service = services.GetService<LearningFeatureDatasetService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.features.export",
                "当前 provider 未注册 Learning Feature Dataset 服务。");
        }

        try
        {
            var result = await service.ExportAsync(
                workspaceId,
                collectionId,
                sessionId,
                string.IsNullOrWhiteSpace(outputDirectory) ? "learning/features" : outputDirectory,
                cancellationToken: ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.features.export");
        }
    }

    private static async Task<IResult> GetLearningFeatureQualityAsync(
        string? featureDirectory,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var builder = services.GetService<LearningDatasetQualityReportBuilder>();
        if (builder is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.features.quality",
                "当前 provider 未注册 Learning Dataset Quality Report Builder。");
        }

        try
        {
            var report = await builder.BuildAsync(
                string.IsNullOrWhiteSpace(featureDirectory)
                    ? LearningDatasetQualityReportBuilder.DefaultFeatureDirectory
                    : featureDirectory,
                ct).ConfigureAwait(false);
            return Results.Ok(report);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.features.quality");
        }
    }

    private static async Task<IResult> GetRankerShadowTracesAsync(
        string? workspaceId,
        string? collectionId,
        int? take,
        string? format,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "learning.ranker-shadow.traces",
                "查询 ranker shadow traces 需要 workspaceId。",
                field: "workspaceId");
        }

        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "learning.ranker-shadow.traces",
                "查询 ranker shadow traces 需要 collectionId。",
                field: "collectionId");
        }

        var service = services.GetService<RankerShadowTraceExportService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "learning.ranker-shadow.traces",
                "当前 provider 未注册 ranker shadow trace export 服务。");
        }

        try
        {
            if (string.Equals(format, "jsonl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, "ndjson", StringComparison.OrdinalIgnoreCase))
            {
                var jsonl = await service.ExportJsonLinesAsync(
                        workspaceId,
                        collectionId,
                        take.GetValueOrDefault(50),
                        ct)
                    .ConfigureAwait(false);
                return Results.Text(jsonl, "application/x-ndjson");
            }

            var records = await service.QueryAsync(
                    workspaceId,
                    collectionId,
                    take.GetValueOrDefault(50),
                    ct)
                .ConfigureAwait(false);
            return Results.Ok(records);
        }
        catch (ArgumentException ex)
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "learning.ranker-shadow.traces",
                ex.Message);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "learning.ranker-shadow.traces");
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
