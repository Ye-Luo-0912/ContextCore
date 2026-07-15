using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<ContextJob> EnqueueCompressionJobAsync(CompressionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<CompressionRequest, ContextJob>(
            "api/jobs/compression", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextJob>> QueryJobsAsync(ContextJobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var qs = new QueryBuilder()
            .Add("collectionId", query.CollectionId)
            .Add("state", ((int?)query.State)?.ToString())
            .Add("take", query.Take)
            .Add("workspaceId", query.WorkspaceId);
        return await GetRequiredAsync<IReadOnlyList<ContextJob>>($"api/jobs{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextJob> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return await GetRequiredAsync<ContextJob>($"api/jobs/{Escape(jobId)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreRequeueJobResponse> RequeueJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return await PostRequiredNoBodyAsync<ContextCoreRequeueJobResponse>(
            $"api/jobs/{Escape(jobId)}/requeue", cancellationToken).ConfigureAwait(false);
    }
}
