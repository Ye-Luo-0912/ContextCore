using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<StableMemorySnapshot> GetStableMemorySnapshotAsync(
        string workspaceId,
        string? collectionId = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qb = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId)
            .Add("take", take);
        return await GetRequiredAsync<StableMemorySnapshot>(
            $"api/memory/stable/snapshot{qb}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableMemoryDiagnosticsReport> GetStableMemoryDiagnosticsAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qb = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<StableMemoryDiagnosticsReport>(
            $"api/memory/stable/diagnostics{qb}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableMemoryExplanation> ExplainStableMemoryAsync(
        string itemId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qb = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<StableMemoryExplanation>(
            $"api/memory/stable/{Escape(itemId)}/explain{qb}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReplacementChainResponse> GetStableReplacementChainAsync(
        string itemId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qb = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<StableReplacementChainResponse>(
            $"api/memory/stable/{Escape(itemId)}/replacement-chain{qb}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableLifecycleReviewResult> DeprecateStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.StableLifecycleReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Memory.Stable[itemId].Deprecate.PostAsync(generatedRequest, config =>
            {
                config.QueryParameters.WorkspaceId = request.WorkspaceId;
                if (request.CollectionId is not null) config.QueryParameters.CollectionId = request.CollectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<StableLifecycleReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/memory/stable/{itemId}/deprecate.");
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

    public async Task<StableLifecycleReviewResult> SupersedeStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.StableLifecycleReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Memory.Stable[itemId].Supersede.PostAsync(generatedRequest, config =>
            {
                config.QueryParameters.WorkspaceId = request.WorkspaceId;
                if (request.CollectionId is not null) config.QueryParameters.CollectionId = request.CollectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<StableLifecycleReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/memory/stable/{itemId}/supersede.");
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

    public async Task<StableLifecycleReviewResult> RejectStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.StableLifecycleReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Memory.Stable[itemId].Reject.PostAsync(generatedRequest, config =>
            {
                config.QueryParameters.WorkspaceId = request.WorkspaceId;
                if (request.CollectionId is not null) config.QueryParameters.CollectionId = request.CollectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<StableLifecycleReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/memory/stable/{itemId}/reject.");
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

    public async Task<IReadOnlyList<StableLifecycleReviewRecord>> GetStableMemoryReviewsAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        return await GetRequiredAsync<IReadOnlyList<StableLifecycleReviewRecord>>(
            $"api/memory/stable/{Escape(itemId)}/reviews", cancellationToken).ConfigureAwait(false);
    }
}
