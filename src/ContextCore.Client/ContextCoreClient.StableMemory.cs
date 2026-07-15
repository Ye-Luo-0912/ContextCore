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
        var qs = new QueryBuilder()
            .Add("collectionId", request.CollectionId)
            .Add("workspaceId", request.WorkspaceId);
        return await PostRequiredAsync<StableLifecycleReviewRequest, StableLifecycleReviewResult>(
            $"api/memory/stable/{Escape(itemId)}/deprecate{qs}", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableLifecycleReviewResult> SupersedeStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        var qs = new QueryBuilder()
            .Add("collectionId", request.CollectionId)
            .Add("workspaceId", request.WorkspaceId);
        return await PostRequiredAsync<StableLifecycleReviewRequest, StableLifecycleReviewResult>(
            $"api/memory/stable/{Escape(itemId)}/supersede{qs}", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableLifecycleReviewResult> RejectStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        var qs = new QueryBuilder()
            .Add("collectionId", request.CollectionId)
            .Add("workspaceId", request.WorkspaceId);
        return await PostRequiredAsync<StableLifecycleReviewRequest, StableLifecycleReviewResult>(
            $"api/memory/stable/{Escape(itemId)}/reject{qs}", request, cancellationToken).ConfigureAwait(false);
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
