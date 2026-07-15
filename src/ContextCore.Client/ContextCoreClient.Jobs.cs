using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<ContextJob> EnqueueCompressionJobAsync(CompressionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.CompressionRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Jobs.Compression.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextJob>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/jobs/compression.");
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

    public async Task<IReadOnlyList<ContextJob>> QueryJobsAsync(ContextJobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var result = await _generated.Api.Jobs.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = query.WorkspaceId;
                config.QueryParameters.CollectionId = query.CollectionId;
                config.QueryParameters.State = (int?)query.State;
                config.QueryParameters.Take = query.Take.ToString();
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<ContextJob>(result);
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

    public async Task<ContextJob> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        try
        {
            var result = await _generated.Api.Jobs[jobId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextJob>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/jobs/{jobId}.");
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

    public async Task<ContextCoreRequeueJobResponse> RequeueJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        try
        {
            var result = await _generated.Api.Jobs[jobId].Requeue.PostAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextCoreRequeueJobResponse>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/jobs/{jobId}/requeue.");
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
