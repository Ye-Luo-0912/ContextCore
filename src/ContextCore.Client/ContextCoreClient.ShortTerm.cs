using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<IReadOnlyList<ShortTermRawEvent>> GetShortTermRawEventsAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Raw.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.Take = take.ToString();
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.SessionId = sessionId;
            }, cancellationToken).ConfigureAwait(false);
            var mapped = await MapStreamToAbstraction<IReadOnlyList<ShortTermRawEvent>>(result).ConfigureAwait(false);
            return mapped
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/memory/short-term/raw.");
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

    public async Task<IReadOnlyList<ShortTermWorkingItem>> GetShortTermWorkingItemsAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Working.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.Take = take.ToString();
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.SessionId = sessionId;
            }, cancellationToken).ConfigureAwait(false);
            var mapped = await MapStreamToAbstraction<IReadOnlyList<ShortTermWorkingItem>>(result).ConfigureAwait(false);
            return mapped
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/memory/short-term/working.");
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

    public async Task<ShortTermMemorySummary> GetShortTermSummaryAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int latestRawTake = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Summary.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.LatestRawTake = latestRawTake.ToString();
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.SessionId = sessionId;
            }, cancellationToken).ConfigureAwait(false);
            var mapped = await MapStreamToAbstraction<ShortTermMemorySummary>(result).ConfigureAwait(false);
            return mapped
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/memory/short-term/summary.");
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

    public async Task<ShortTermMemoryCompactionResult> CompactShortTermMemoryAsync(
        ShortTermMemoryCompactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.ShortTermMemoryCompactionRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Compact.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            var mapped = await MapStreamToAbstraction<ShortTermMemoryCompactionResult>(result).ConfigureAwait(false);
            return mapped
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/memory/short-term/compact.");
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

    public async Task<ShortTermArchiveSummary> GetShortTermArchiveSummaryAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Archive.Summary.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.SessionId = sessionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ShortTermArchiveSummary>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/memory/short-term/archive/summary.");
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

    public async Task<ShortTermArchiveItemsResponse> GetShortTermArchiveItemsAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        string? kind = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Archive.Items.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.Limit = limit.ToString();
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.SessionId = sessionId;
                config.QueryParameters.Kind = kind;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ShortTermArchiveItemsResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/memory/short-term/archive/items.");
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

    public async Task<IReadOnlyList<ShortTermCompactionRun>> GetShortTermCompactionRunsAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        string? trigger = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Compact.Runs.GetAsync(config =>
            {
                config.QueryParameters.Take = take.ToString();
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.SessionId = sessionId;
                config.QueryParameters.Trigger = trigger;
            }, cancellationToken).ConfigureAwait(false);
            var mapped = await MapStreamToAbstraction<IReadOnlyList<ShortTermCompactionRun>>(result).ConfigureAwait(false);
            return mapped
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/memory/short-term/compact/runs.");
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

    public async Task<ShortTermCompactionRun> GetShortTermCompactionRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Compact.Runs[runId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ShortTermCompactionRun>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/memory/short-term/compact/runs/{runId}.");
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

    public async Task<IReadOnlyList<ShortTermPromotionCandidate>> GenerateShortTermPromotionCandidatesAsync(
        ShortTermPromotionCandidateGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.ShortTermPromotionCandidateGenerationRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Promotion.Candidates.Generate.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            var mapped = await MapStreamToAbstraction<IReadOnlyList<ShortTermPromotionCandidate>>(result).ConfigureAwait(false);
            return mapped
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/memory/short-term/promotion/candidates/generate.");
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

    public async Task<IReadOnlyList<ShortTermPromotionCandidate>> GetShortTermPromotionCandidatesAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        PromotionCandidateStatus? status = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return await QueryShortTermPromotionCandidatesAsync(
            workspaceId,
            collectionId,
            sessionId,
            status,
            kind: null,
            suggestedTargetLayer: null,
            minConfidence: null,
            minImportance: null,
            limit: take,
            offset: 0,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermPromotionCandidate> GetShortTermPromotionCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Promotion.Candidates[candidateId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ShortTermPromotionCandidate>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/memory/short-term/promotion/candidates/{candidateId}.");
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

    public async Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryShortTermPromotionCandidatesAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        PromotionCandidateStatus? status = null,
        string? kind = null,
        string? suggestedTargetLayer = null,
        double? minConfidence = null,
        double? minImportance = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Promotion.Candidates.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.Limit = limit.ToString();
                config.QueryParameters.Offset = offset.ToString();
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.SessionId = sessionId;
                config.QueryParameters.Status = (int?)status;
                config.QueryParameters.Kind = kind;
                config.QueryParameters.SuggestedTargetLayer = suggestedTargetLayer;
                config.QueryParameters.MinConfidence = minConfidence?.ToString();
                config.QueryParameters.MinImportance = minImportance?.ToString();
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<ShortTermPromotionCandidate>(result);
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
