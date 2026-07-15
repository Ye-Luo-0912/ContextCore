using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<ContextPackage> BuildPackageAsync(ContextPackageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextPackageRequest, ContextPackage>(
            "api/package/build", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPackageBuildResult> BuildPackageDetailedAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextPackageRequest, ContextPackageBuildResult>(
            "api/package/build-detailed", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPackage> PreviewPackageAsync(ContextPackageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextPackageRequest, ContextPackage>(
            "api/package/preview", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextPackagePolicy>> QueryPackagePoliciesAsync(
        string workspaceId,
        string collectionId,
        string? queryText = null,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId)
            .Add("queryText", queryText)
            .Add("take", take);
        return await GetRequiredAsync<IReadOnlyList<ContextPackagePolicy>>($"api/package/policies{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPackagePolicy> GetPackagePolicyAsync(
        string workspaceId,
        string collectionId,
        string policyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<ContextPackagePolicy>(
            $"api/package/policies/{Escape(policyId)}{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompressionResponse> RunCompressionAsync(CompressionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<CompressionRequest, CompressionResponse>(
            "api/compression/sync", request, cancellationToken).ConfigureAwait(false);
    }
}
