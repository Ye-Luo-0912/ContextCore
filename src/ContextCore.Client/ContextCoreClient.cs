using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client.Generated;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Serialization.Json;

namespace ContextCore.Client;

/// <summary>
/// ContextCore HTTP API 的轻量客户端封装，供外部系统调用服务入口而不是直接引用 Core/Storage。
/// </summary>
/// <remarks>
/// 本类型按领域拆分为多个 partial 文件（Runtime/Context/Vector/Memory/StableMemory/ShortTerm/
/// Promotion/Learning/Package/Relations/Constraints/Jobs/Admin）。本文件保留构造函数、HTTP 底层帮助方法、
/// 查询字符串构建帮助方法以及若干跨领域复用的私有 review POST 帮助方法。
/// 生成客户端（ContextCoreGeneratedClient）通过 JSON round-trip 与 Abstractions 类型互转，
/// 逐域替换手写 HTTP 调用。
/// </remarks>
public sealed partial class ContextCoreClient
{
    private readonly HttpClient _http;
    private readonly ContextCoreGeneratedClient _generated;

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ISerializationWriterFactory JsonWriterFactory = new JsonSerializationWriterFactory();
    private static readonly IParseNodeFactory JsonParseFactory = new JsonParseNodeFactory();

    public ContextCoreClient(HttpClient http)
    {
        _http = http;
        var adapter = new HttpClientRequestAdapter(
            new AnonymousAuthenticationProvider(),
            httpClient: http);
        if (http.BaseAddress is not null)
        {
            adapter.BaseUrl = http.BaseAddress.AbsoluteUri.TrimEnd('/');
        }
        _generated = new ContextCoreGeneratedClient(adapter);
    }

    // ── 生成客户端 JSON round-trip 映射帮助方法 ──────────────────────────

    /// <summary>将生成的 IParsable 模型序列化为 JSON 字符串。</summary>
    internal static string SerializeParsable(IParsable value)
    {
        using var writer = JsonWriterFactory.GetSerializationWriter("application/json");
        writer.WriteObjectValue(null, value);
        using var stream = writer.GetSerializedContent();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>将 JSON 字符串反序列化为生成的 IParsable 模型。</summary>
    private static async Task<TGenerated?> ParseParsable<TGenerated>(string json, ParsableFactory<TGenerated> factory)
        where TGenerated : IParsable
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var node = await JsonParseFactory.GetRootParseNodeAsync("application/json", stream, CancellationToken.None).ConfigureAwait(false);
        return node.GetObjectValue(factory);
    }

    /// <summary>将生成的模型映射到 Abstractions 类型（Kiota 序列化 → STJ 反序列化）。</summary>
    internal static TAbstraction? MapToAbstraction<TAbstraction>(IParsable? value)
        where TAbstraction : class
    {
        if (value is null) return null;
        var json = SerializeParsable(value);
        return JsonSerializer.Deserialize<TAbstraction>(json, WebJsonOptions);
    }

    /// <summary>将 Abstractions 类型映射到生成的模型（STJ 序列化 → Kiota 反序列化）。</summary>
    private static async Task<TGenerated?> MapToGenerated<TGenerated>(object? value, ParsableFactory<TGenerated> factory)
        where TGenerated : IParsable
    {
        if (value is null) return default;
        var json = JsonSerializer.Serialize(value, WebJsonOptions);
        return await ParseParsable(json, factory).ConfigureAwait(false);
    }

    /// <summary>将 Abstractions 对象映射为 UntypedNode（用于 free-form 请求体端点，STJ 序列化 → Kiota 反序列化）。</summary>
    private static async Task<UntypedNode> MapToUntypedNode(object value)
    {
        var json = JsonSerializer.Serialize(value, WebJsonOptions);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var node = await JsonParseFactory.GetRootParseNodeAsync("application/json", stream, CancellationToken.None).ConfigureAwait(false);
        return node.GetObjectValue(UntypedNode.CreateFromDiscriminatorValue) ?? throw new InvalidOperationException("Failed to convert JSON to an untyped node.");
    }

    /// <summary>将生成的模型集合映射到 Abstractions 类型列表。</summary>
    private static List<TAbstraction> MapCollectionToAbstraction<TAbstraction>(IEnumerable<IParsable>? values)
        where TAbstraction : class
    {
        if (values is null) return new();
        var result = new List<TAbstraction>();
        foreach (var item in values)
        {
            var mapped = MapToAbstraction<TAbstraction>(item);
            if (mapped is not null) result.Add(mapped);
        }
        return result;
    }

    /// <summary>将生成客户端返回的原始 JSON 流反序列化为 Abstractions 类型。</summary>
    private static async Task<TAbstraction?> MapStreamToAbstraction<TAbstraction>(Stream? stream)
        where TAbstraction : class
    {
        if (stream is null) return null;
        using (stream)
        {
            return await JsonSerializer.DeserializeAsync<TAbstraction>(stream, WebJsonOptions).ConfigureAwait(false);
        }
    }

    /// <summary>将生成的错误响应异常转换为 ContextCoreApiException。</summary>
    private static ContextCoreApiException ToApiException(ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
    {
        var error = MapToAbstraction<ContextCoreErrorResponse>(ex) ?? new ContextCoreErrorResponse();
        var statusCode = (HttpStatusCode)ex.ResponseStatusCode;
        return new ContextCoreApiException(error, statusCode);
    }

    /// <summary>将生成客户端的原始 ApiException（无 errorMapping 时抛出）转换为 ContextCoreApiException。</summary>
    private static ContextCoreApiException ToApiException(Microsoft.Kiota.Abstractions.ApiException ex)
    {
        if (ex is ContextCore.Client.Generated.Models.ContextCoreErrorResponse errorResponse)
        {
            return ToApiException(errorResponse);
        }

        var error = new ContextCoreErrorResponse
        {
            ErrorCode = "HttpError",
            Message = ex.Message
        };
        var statusCode = (HttpStatusCode)ex.ResponseStatusCode;
        return new ContextCoreApiException(error, statusCode);
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
            .AddEnum("signal", query.Signal)
            .AddEnum("failureType", query.FailureType)
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
            .AddEnum("signal", query.Signal)
            .AddEnum("failureType", query.FailureType)
            .AddEnum("status", query.Status)
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
