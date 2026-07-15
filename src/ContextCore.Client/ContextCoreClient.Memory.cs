using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<ContextMemoryItem> AddMemoryAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return await PostRequiredAsync<ContextMemoryItem, ContextMemoryItem>(
            "api/memory/add", item, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryItem> AddWorkingMemoryItemAsync(
        WorkingMemoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var generatedRequest = await MapToGenerated(item, ContextCore.Client.Generated.Models.WorkingMemoryItem.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(item));
        try
        {
            var result = await _generated.Api.Memory.Working.Add.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<WorkingMemoryItem>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/memory/working/add.");
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

    public async Task<IReadOnlyList<WorkingMemoryItem>> GetRecentWorkingMemoryAsync(
        string workspaceId,
        string collectionId,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        try
        {
            var result = await _generated.Api.Memory.Working.Recent.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.Take = take.ToString();
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<WorkingMemoryItem>(result);
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

    public async Task ClearWorkingMemoryAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        var scopeRequest = new ContextCoreWorkingMemoryScopeRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId
        };
        var generatedRequest = await MapToGenerated(scopeRequest, ContextCore.Client.Generated.Models.WorkingMemoryScopeRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Failed to map working memory scope request.");
        try
        {
            await _generated.Api.Memory.Working.Clear.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
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

    public async Task<WorkingMemoryActiveContext?> GetWorkingMemoryActiveContextAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        try
        {
            var result = await _generated.Api.Memory.Working.ActiveContext.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<WorkingMemoryActiveContext>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/memory/working/active-context.");
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

    public async Task<WorkingMemoryActiveContext> SetWorkingMemoryActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeContext);
        var generatedRequest = await MapToGenerated(activeContext, ContextCore.Client.Generated.Models.WorkingMemoryActiveContext.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(activeContext));
        try
        {
            var result = await _generated.Api.Memory.Working.ActiveContext.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<WorkingMemoryActiveContext>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/memory/working/active-context.");
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

    public async Task<WorkingMemoryCurrentTask?> GetWorkingMemoryCurrentTaskAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        try
        {
            var result = await _generated.Api.Memory.Working.CurrentTask.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<WorkingMemoryCurrentTask>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/memory/working/current-task.");
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

    public async Task<WorkingMemoryCurrentTask> SetWorkingMemoryCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentTask);
        var generatedRequest = await MapToGenerated(currentTask, ContextCore.Client.Generated.Models.WorkingMemoryCurrentTask.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(currentTask));
        try
        {
            var result = await _generated.Api.Memory.Working.CurrentTask.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<WorkingMemoryCurrentTask>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/memory/working/current-task.");
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

    public async Task<IReadOnlyList<ContextMemoryItem>> QueryMemoryAsync(
        ContextMemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await PostRequiredAsync<ContextMemoryQuery, IReadOnlyList<ContextMemoryItem>>(
            "api/memory/query", query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateMemorySnapshot> GetCandidateMemorySnapshotAsync(
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
        return await GetRequiredAsync<CandidateMemorySnapshot>(
            $"api/memory/candidates/snapshot{qb}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateMemoryRecord> GetCandidateMemoryAsync(
        string candidateId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qb = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<CandidateMemoryRecord>(
            $"api/memory/candidates/{Escape(candidateId)}{qb}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateMemoryExplanation> ExplainCandidateMemoryAsync(
        string candidateId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qb = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<CandidateMemoryExplanation>(
            $"api/memory/candidates/{Escape(candidateId)}/explain{qb}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateMemoryDiagnosticsReport> GetCandidateMemoryDiagnosticsAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qb = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<CandidateMemoryDiagnosticsReport>(
            $"api/memory/candidates/diagnostics{qb}", cancellationToken).ConfigureAwait(false);
    }

    public Task<CandidateMemoryReviewResult> MarkCandidateMemoryReadyForStableReviewAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);
        return PostCandidateMemoryReviewAsync(candidateId, "ready-for-stable-review", request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> MarkCandidateMemoryNeedsMoreEvidenceAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);
        return PostCandidateMemoryReviewAsync(candidateId, "needs-more-evidence", request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> RejectCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);
        return PostCandidateMemoryReviewAsync(candidateId, "reject", request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> ExpireCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);
        return PostCandidateMemoryReviewAsync(candidateId, "expire", request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> SupersedeCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);
        return PostCandidateMemoryReviewAsync(candidateId, "supersede", request, cancellationToken);
    }

    public async Task<IReadOnlyList<CandidateMemoryReviewRecord>> GetCandidateMemoryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return await GetRequiredAsync<IReadOnlyList<CandidateMemoryReviewRecord>>(
            $"api/memory/candidates/{Escape(candidateId)}/reviews", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextGlobalItem>> QueryGlobalContextAsync(
        string workspaceId,
        string? collectionId = null,
        ContextScope? scope = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qb = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("take", take)
            .Add("collectionId", collectionId)
            .AddEnum("scope", scope);
        return await GetRequiredAsync<IReadOnlyList<ContextGlobalItem>>(
            $"api/memory/global{qb}", cancellationToken).ConfigureAwait(false);
    }
}
