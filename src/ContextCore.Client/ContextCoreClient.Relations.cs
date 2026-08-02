using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<ContextCoreRelationsResponse> QueryRelationsAsync(
        string itemId,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        var qb = new QueryBuilder()
            .Add("collectionId", collectionId)
            .Add("workspaceId", workspaceId);
        return await GetRequiredAsync<ContextCoreRelationsResponse>(
            $"api/relations/{Escape(itemId)}{qb}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用 <c>GET /api/relations/{workspaceId}/{collectionId}/{itemId}/subgraph</c> 获取关系子图。
    /// </summary>
    /// <param name="itemId">根条目 ID。</param>
    /// <param name="workspaceId">工作空间 ID。</param>
    /// <param name="collectionId">集合 ID。</param>
    /// <param name="depth">最大遍历深度，默认 2。</param>
    /// <param name="direction">遍历方向（outgoing|incoming|both），默认 both。</param>
    /// <param name="allowedTypes">可选的关系类型白名单。</param>
    public async Task<RelationSubgraph> GetRelationSubgraphAsync(
        string itemId,
        string workspaceId,
        string collectionId,
        int depth = 2,
        string direction = "both",
        string[]? allowedTypes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        var qs = new QueryBuilder()
            .Add("depth", depth)
            .Add("direction", direction)
            .Add("types", allowedTypes is { Length: > 0 } ? string.Join(",", allowedTypes) : null);
        return await GetRequiredAsync<RelationSubgraph>(
            $"api/relations/{Escape(workspaceId)}/{Escape(collectionId)}/{Escape(itemId)}/subgraph{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelationTypeDefinition>> GetRelationTypesAsync(
        CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<IReadOnlyList<RelationTypeDefinition>>("api/relations/types", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelationExpansionProfile>> GetRelationExpansionProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<IReadOnlyList<RelationExpansionProfile>>("api/relations/expansion/profiles", cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationExpansionPreviewResponse> PreviewRelationExpansionAsync(
        RelationExpansionPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<RelationExpansionPreviewRequest, RelationExpansionPreviewResponse>(
            "api/relations/expansion/preview", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationGraphDiagnosticsReport> GetRelationDiagnosticsAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<RelationGraphDiagnosticsReport>($"api/relations/diagnostics{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationGraphDiagnosticsReport> GetItemRelationDiagnosticsAsync(
        string itemId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<RelationGraphDiagnosticsReport>($"api/relations/diagnostics/{Escape(itemId)}{qs}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 流式获取 relation graph 诊断。调用 <c>GET /api/relations/diagnostics/stream</c>，
    /// 以 NDJSON（每行一个 JSON 对象）形式逐条返回 <see cref="RelationGraphDiagnostic"/>，避免一次性将整张
    /// 关系图载入内存。客户端按需消费枚举，HTTP 流在枚举释放时自动关闭。
    /// </summary>
    /// <param name="workspaceId">工作空间 ID。</param>
    /// <param name="collectionId">可选集合 ID。</param>
    /// <param name="cancellationToken">取消令牌；客户端断开时终止流。</param>
    public async IAsyncEnumerable<RelationGraphDiagnostic> StreamRelationDiagnosticsAsync(
        string workspaceId,
        string? collectionId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/relations/diagnostics/stream{qs}");
        // ResponseHeadersRead：仅读取响应头，body 留待 stream 逐行消费
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            RelationGraphDiagnostic? diagnostic;
            try
            {
                diagnostic = JsonSerializer.Deserialize<RelationGraphDiagnostic>(line);
            }
            catch (JsonException)
            {
                // 服务端契约应保证每行一个有效 JSON；跳过坏行不阻塞下游消费。
                continue;
            }
            if (diagnostic is not null)
            {
                yield return diagnostic;
            }
        }
    }

    public async Task<RelationExplainResponse> ExplainRelationAsync(
        string relationId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qb = new QueryBuilder()
            .Add("collectionId", collectionId)
            .Add("workspaceId", workspaceId);
        return await GetRequiredAsync<RelationExplainResponse>(
            $"api/relations/{Escape(relationId)}/explain{qb}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationReviewResult> ReviewRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<RelationReviewRequest, RelationReviewResult>(
            $"api/relations/{Escape(relationId)}/review", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationReviewResult> RejectRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<RelationReviewRequest, RelationReviewResult>(
            $"api/relations/{Escape(relationId)}/reject", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationReviewResult> DeprecateRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<RelationReviewRequest, RelationReviewResult>(
            $"api/relations/{Escape(relationId)}/deprecate", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationReviewResult> MarkRelationNeedsEvidenceAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<RelationReviewRequest, RelationReviewResult>(
            $"api/relations/{Escape(relationId)}/needs-evidence", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelationReviewRecord>> GetRelationReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        return await GetRequiredAsync<IReadOnlyList<RelationReviewRecord>>($"api/relations/{Escape(relationId)}/reviews", cancellationToken).ConfigureAwait(false);
    }
}
