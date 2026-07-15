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
        try
        {
            var result = await _generated.Api.Relations[workspaceId][collectionId][itemId].Subgraph.GetAsync(config =>
            {
                config.QueryParameters.Depth = depth.ToString();
                config.QueryParameters.Direction = direction;
                if (allowedTypes is { Length: > 0 })
                {
                    config.QueryParameters.Types = string.Join(",", allowedTypes);
                }
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<RelationSubgraph>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/relations/{workspaceId}/{collectionId}/{itemId}/subgraph.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<IReadOnlyList<RelationTypeDefinition>> GetRelationTypesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Relations.Types.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<RelationTypeDefinition>(result);
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<IReadOnlyList<RelationExpansionProfile>> GetRelationExpansionProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Relations.Expansion.Profiles.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<RelationExpansionProfile>(result);
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<RelationExpansionPreviewResponse> PreviewRelationExpansionAsync(
        RelationExpansionPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.RelationExpansionPreviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Relations.Expansion.Preview.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<RelationExpansionPreviewResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/relations/expansion/preview.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<RelationGraphDiagnosticsReport> GetRelationDiagnosticsAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        try
        {
            var result = await _generated.Api.Relations.Diagnostics.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<RelationGraphDiagnosticsReport>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/relations/diagnostics.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<RelationGraphDiagnosticsReport> GetItemRelationDiagnosticsAsync(
        string itemId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        try
        {
            var result = await _generated.Api.Relations.Diagnostics[itemId].GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<RelationGraphDiagnosticsReport>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/relations/diagnostics/{itemId}.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<RelationExplainResponse> ExplainRelationAsync(
        string relationId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        // Explain 响应包含 UntypedNode 字段，Kiota JSON 序列化器无法正确处理 null UntypedNode，
        // 保留直接 HttpClient + STJ 反序列化。
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
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.RelationReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Relations[relationId].Review.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<RelationReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/relations/{relationId}/review.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<RelationReviewResult> RejectRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.RelationReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Relations[relationId].Reject.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<RelationReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/relations/{relationId}/reject.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<RelationReviewResult> DeprecateRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.RelationReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Relations[relationId].Deprecate.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<RelationReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/relations/{relationId}/deprecate.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<RelationReviewResult> MarkRelationNeedsEvidenceAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.RelationReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Relations[relationId].NeedsEvidence.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<RelationReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/relations/{relationId}/needs-evidence.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<IReadOnlyList<RelationReviewRecord>> GetRelationReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        try
        {
            var result = await _generated.Api.Relations[relationId].Reviews.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<RelationReviewRecord>(result);
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }
}
