using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Endpoints;

/// <summary>
/// R29 WP-E-5：Utility Ledger 用户反馈接入端点。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 用户显式反馈（thumbs up/down / 评分修正 / 文本反馈 / 举报）通过 POST /api/utility-ledger/feedback 提交。
///   2. 反馈与 Utility Ledger 条目通过 (workspace_id, collection_id, decision_id, candidate_item_id) 关联。
///   3. 反馈不修改原始 ledger 条目（append-only 语义），独立写入 user_feedback_entries 表。
///   4. 幂等键由调用方按需提供或自动生成；同键重复写入由 Store 保证覆盖或忽略。
///   5. 反馈查询通过 GET /api/utility-ledger/feedback 暴露只读 API（按 workspace / decision / candidate / kind / 时间窗过滤）。
/// </remarks>
internal static class UtilityLedgerEndpoints
{
    public static IEndpointRouteBuilder MapUtilityLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/utility-ledger")
            .WithTags("UtilityLedger");

        group.MapPost("/feedback", SubmitUserFeedbackAsync)
            .WithName("SubmitUserFeedback")
            .WithSummary("提交用户显式反馈（thumbs up/down / 评分修正 / 文本反馈 / 举报）写入 Utility Ledger")
            .Produces<UserFeedbackSubmitResult>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/feedback", QueryUserFeedbackAsync)
            .WithName("QueryUserFeedback")
            .WithSummary("查询用户反馈条目（按 workspace / decision / candidate / kind / 时间窗过滤）")
            .Produces<IReadOnlyList<UserFeedbackEntry>>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/feedback/latest", GetLatestUserFeedbackAsync)
            .WithName("GetLatestUserFeedback")
            .WithSummary("查询指定 candidate 的最新反馈条目")
            .Produces<UserFeedbackEntry?>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> SubmitUserFeedbackAsync(
        UserFeedbackSubmitRequest request,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var service = services.GetService<UserFeedbackService>();
        if (service is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "utility-ledger.feedback.submit",
                "当前 provider 未注册用户反馈服务（UserFeedbackService）。");
        }

        try
        {
            var result = await service.SubmitAsync(request, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "utility-ledger.feedback.submit",
                ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // 关联校验失败：(decision_id, candidate_item_id, workspace_id, collection_id) 在 utility_ledger_entries 中不存在。
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "utility-ledger.feedback.submit",
                ex.Message);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "utility-ledger.feedback.submit");
        }
    }

    private static async Task<IResult> QueryUserFeedbackAsync(
        string? workspaceId,
        string? collectionId,
        string? decisionId,
        string? candidateItemId,
        string? kind,
        string? givenBy,
        DateTimeOffset? since,
        DateTimeOffset? until,
        int? take,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var ledger = services.GetService<IUserFeedbackLedger>();
        if (ledger is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "utility-ledger.feedback.query",
                "当前 provider 未注册用户反馈 Ledger（IUserFeedbackLedger）。");
        }

        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "utility-ledger.feedback.query",
                "workspaceId 为必填查询参数。");
        }

        UserFeedbackKind? kindFilter = null;
        if (!string.IsNullOrWhiteSpace(kind)
            && Enum.TryParse<UserFeedbackKind>(kind, ignoreCase: true, out var parsedKind))
        {
            kindFilter = parsedKind;
        }

        try
        {
            var query = new UserFeedbackQuery
            {
                WorkspaceId = workspaceId!,
                CollectionId = collectionId,
                DecisionId = decisionId,
                CandidateItemId = candidateItemId,
                Kind = kindFilter,
                GivenBy = givenBy,
                Since = since,
                Until = until,
                Take = take.GetValueOrDefault(100)
            };

            var results = await ledger.QueryFeedbackAsync(query, ct).ConfigureAwait(false);
            return Results.Ok(results);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "utility-ledger.feedback.query");
        }
    }

    private static async Task<IResult> GetLatestUserFeedbackAsync(
        string workspaceId,
        string collectionId,
        string candidateItemId,
        IServiceProvider services,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var ledger = services.GetService<IUserFeedbackLedger>();
        if (ledger is null)
        {
            return ContextCoreHttpResultMapper.Misconfigured(
                httpContext,
                string.Empty,
                "utility-ledger.feedback.latest",
                "当前 provider 未注册用户反馈 Ledger（IUserFeedbackLedger）。");
        }

        if (string.IsNullOrWhiteSpace(workspaceId)
            || string.IsNullOrWhiteSpace(collectionId)
            || string.IsNullOrWhiteSpace(candidateItemId))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext,
                string.Empty,
                "utility-ledger.feedback.latest",
                "workspaceId / collectionId / candidateItemId 均为必填查询参数。");
        }

        try
        {
            var latest = await ledger.GetLatestFeedbackForCandidateAsync(
                workspaceId!,
                collectionId!,
                candidateItemId!,
                ct).ConfigureAwait(false);
            return Results.Ok(latest);
        }
        catch (Exception ex)
        {
            return ContextCoreHttpResultMapper.Error(httpContext, ex, string.Empty, "utility-ledger.feedback.latest");
        }
    }
}
