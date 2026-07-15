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
