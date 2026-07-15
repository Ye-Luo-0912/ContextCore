using System.Net;
using System.Net.Http.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

/// <summary>
/// ContextCore HTTP API 的轻量客户端封装，供外部系统调用服务入口而不是直接引用 Core/Storage。
/// </summary>
/// <remarks>
/// 本类型按领域拆分为多个 partial 文件（Runtime/Context/Vector/Memory/StableMemory/ShortTerm/
/// Promotion/Learning/Package/Relations/Constraints/Jobs/Admin）。本文件保留构造函数、HTTP 底层帮助方法、
/// 查询字符串构建帮助方法以及若干跨领域复用的私有 review POST 帮助方法。
/// 统一使用泛型 HttpClient + System.Text.Json 直接序列化 Abstractions 类型，无生成代码依赖。
/// </remarks>
public sealed partial class ContextCoreClient
{
    private readonly HttpClient _http;

    public ContextCoreClient(HttpClient http)
    {
        _http = http;
    }

    private static string Escape(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private static string BuildLearningRecordQueryString(ContextLearningRecordQuery query)
    {
        var qb = new QueryBuilder()
            .Add("limit", query.Limit)
            .Add("offset", query.Offset)
            .Add("workspaceId", query.WorkspaceId)
            .Add("collectionId", query.CollectionId)
            .Add("sessionId", query.SessionId)
            .Add("signal", ((int?)query.Signal)?.ToString())
            .Add("failureType", ((int?)query.FailureType)?.ToString())
            .Add("sourceKind", query.SourceKind)
            .Add("sourceId", query.SourceId);
        return qb.ToString();
    }

    private static string BuildPromotionFeedbackQueryString(PromotionFeedbackSignalQuery query)
    {
        var qb = new QueryBuilder()
            .Add("limit", query.Limit)
            .Add("offset", query.Offset)
            .Add("workspaceId", query.WorkspaceId)
            .Add("collectionId", query.CollectionId)
            .Add("sessionId", query.SessionId)
            .Add("candidateId", query.CandidateId)
            .Add("action", query.Action);
        return qb.ToString();
    }

    private static string BuildRuntimeLearningFeedbackQueryString(
        LearningFeedbackEventQuery query,
        bool includeRuntimeFlag = true)
    {
        var qb = new QueryBuilder()
            .Add("limit", query.Limit)
            .Add("offset", query.Offset);
        if (includeRuntimeFlag)
        {
            qb.AddRaw("runtimeFeedback", "true");
        }

        return qb
            .Add("workspaceId", query.WorkspaceId)
            .Add("collectionId", query.CollectionId)
            .Add("source", query.Source)
            .Add("sourceOperationId", query.SourceOperationId)
            .Add("capabilityId", query.CapabilityId)
            .Add("targetId", query.TargetId)
            .Add("targetType", query.TargetType)
            .Add("feedbackKind", query.FeedbackKind)
            .ToString();
    }

    private static string BuildLearningFeedbackReviewQueryString(LearningFeedbackReviewQuery query)
    {
        var qb = new QueryBuilder()
            .Add("limit", query.Limit)
            .Add("offset", query.Offset)
            .Add("feedbackId", query.FeedbackId)
            .AddEnum("reviewStatus", query.ReviewStatus)
            .Add("reviewer", query.Reviewer);
        return qb.ToString();
    }

    private static string BuildStableReviewCandidateQueryString(StableReviewCandidateQuery query)
    {
        var qb = new QueryBuilder()
            .Add("workspaceId", query.WorkspaceId)
            .Add("collectionId", query.CollectionId)
            .Add("kind", query.Kind)
            .Add("limit", query.Limit)
            .Add("offset", query.Offset)
            .Add("sessionId", query.SessionId)
            .Add("status", query.Status)
            .Add("suggestedStableTarget", query.SuggestedStableTarget)
            .Add("validationStatus", query.ValidationStatus);
        return qb.ToString();
    }

    private static string BuildConstraintGapQueryString(ConstraintGapCandidateQuery query)
    {
        var qb = new QueryBuilder()
            .Add("workspaceId", query.WorkspaceId)
            .Add("limit", query.Limit)
            .Add("offset", query.Offset)
            .Add("collectionId", query.CollectionId)
            .Add("sessionId", query.SessionId)
            .Add("source", query.Source)
            .Add("sourceSampleId", query.SourceSampleId)
            .Add("status", query.Status)
            .Add("severity", query.Severity);
        return qb.ToString();
    }

    private static string BuildCandidateConstraintQueryString(
        string workspaceId,
        string? collectionId,
        ContextMemoryStatus? status,
        int limit,
        int offset)
    {
        var qb = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("limit", limit)
            .Add("offset", offset)
            .Add("collectionId", collectionId)
            .AddEnum("status", status);
        return qb.ToString();
    }

    private static string BuildLearningCaseQueryString(ContextLearningCaseQuery query)
    {
        var qb = new QueryBuilder()
            .Add("limit", query.Limit)
            .Add("offset", query.Offset)
            .Add("workspaceId", query.WorkspaceId)
            .Add("collectionId", query.CollectionId)
            .Add("sessionId", query.SessionId)
            .Add("signal", ((int?)query.Signal)?.ToString())
            .Add("failureType", ((int?)query.FailureType)?.ToString())
            .Add("status", ((int?)query.Status)?.ToString())
            .Add("caseKind", query.CaseKind)
            .Add("sourceRecordId", query.SourceRecordId);
        return qb.ToString();
    }

    private async Task<CandidateMemoryReviewResult> PostCandidateMemoryReviewAsync(
        string candidateId,
        string actionPath,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionPath);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);

        var qb = new QueryBuilder()
            .Add("workspaceId", request.WorkspaceId)
            .Add("collectionId", request.CollectionId);

        return await PostRequiredAsync<CandidateMemoryReviewRequest, CandidateMemoryReviewResult>(
            $"api/memory/candidates/{Escape(candidateId)}/{actionPath}{qb}",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StableLifecycleReviewResult> PostStableLifecycleReviewAsync(
        string itemId,
        string actionPath,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionPath);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);

        var qb = new QueryBuilder()
            .Add("workspaceId", request.WorkspaceId)
            .Add("collectionId", request.CollectionId);

        return await PostRequiredAsync<StableLifecycleReviewRequest, StableLifecycleReviewResult>(
            $"api/memory/stable/{Escape(itemId)}/{actionPath}{qb}",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LearningFeedbackReviewResult> ReviewLearningFeedbackAsync(
        string feedbackId,
        string action,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedbackId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<LearningFeedbackReviewRequest, LearningFeedbackReviewResult>(
            $"api/learning/feedback/{Escape(feedbackId)}/review/{action}",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> GetRequiredAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET {path}.");
    }

    private async Task<string> GetRequiredStringAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse?> GetOptionalAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET {path}.");
    }

    private async Task<TResponse> PostRequiredAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(path, request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
        // 服务端的成功响应应总是有 JSON 主体；空主体通常表示端点契约被破坏。
        var result = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST {path}.");
    }

    private async Task PostNoContentAsync<TRequest>(string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(path, request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> PostRequiredNoBodyAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(path, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST {path}.");
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ContextCoreErrorResponse? errorResponse = null;
        try
        {
            errorResponse = await response.Content
                .ReadFromJsonAsync<ContextCoreErrorResponse>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // 回退到默认 HTTP 异常。
        }

        if (errorResponse is not null && !string.IsNullOrWhiteSpace(errorResponse.ErrorCode))
        {
            throw new ContextCoreApiException(errorResponse, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
    }

}
