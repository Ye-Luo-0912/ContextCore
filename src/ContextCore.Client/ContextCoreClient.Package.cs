using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<ContextPackage> BuildPackageAsync(ContextPackageRequest request, CancellationToken cancellationToken = default)
    {
        // ContextPackageRequest 包含 IComposedTypeWrapper 字段（Policy）和 UntypedNode 字段（TokenBudget），
        // Kiota 序列化器无法正确处理空组合类型包装器，保留直接 HttpClient + STJ 反序列化。
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextPackageRequest, ContextPackage>(
            "api/package/build", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPackageBuildResult> BuildPackageDetailedAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        // ContextPackageRequest 包含 IComposedTypeWrapper/UntypedNode 字段，保留直接 HttpClient + STJ 反序列化。
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextPackageRequest, ContextPackageBuildResult>(
            "api/package/build-detailed", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPackage> PreviewPackageAsync(ContextPackageRequest request, CancellationToken cancellationToken = default)
    {
        // ContextPackageRequest 包含 IComposedTypeWrapper/UntypedNode 字段，保留直接 HttpClient + STJ 反序列化。
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
        try
        {
            var result = await _generated.Api.Package.Policies.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.QueryText = queryText;
                config.QueryParameters.Take = take.ToString();
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<ContextPackagePolicy>(result);
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

    public async Task<ContextPackagePolicy> GetPackagePolicyAsync(
        string workspaceId,
        string collectionId,
        string policyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        try
        {
            var result = await _generated.Api.Package.Policies[policyId].GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextPackagePolicy>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/package/policies/{policyId}.");
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

    public async Task<CompressionResponse> RunCompressionAsync(CompressionRequest request, CancellationToken cancellationToken = default)
    {
        // Compression 响应包含 UntypedNode 字段，Kiota JSON 序列化器无法正确处理 null UntypedNode，
        // 保留直接 HttpClient + STJ 反序列化。
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<CompressionRequest, CompressionResponse>(
            "api/compression/sync", request, cancellationToken).ConfigureAwait(false);
    }
}
