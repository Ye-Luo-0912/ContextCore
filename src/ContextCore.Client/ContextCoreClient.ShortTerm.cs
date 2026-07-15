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
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("take", take)
            .Add("collectionId", collectionId)
            .Add("sessionId", sessionId);
        return await GetRequiredAsync<IReadOnlyList<ShortTermRawEvent>>(
            $"api/memory/short-term/raw{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermWorkingItem>> GetShortTermWorkingItemsAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("take", take)
            .Add("collectionId", collectionId)
            .Add("sessionId", sessionId);
        return await GetRequiredAsync<IReadOnlyList<ShortTermWorkingItem>>(
            $"api/memory/short-term/working{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermMemorySummary> GetShortTermSummaryAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int latestRawTake = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("latestRawTake", latestRawTake)
            .Add("collectionId", collectionId)
            .Add("sessionId", sessionId);
        return await GetRequiredAsync<ShortTermMemorySummary>(
            $"api/memory/short-term/summary{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermMemoryCompactionResult> CompactShortTermMemoryAsync(
        ShortTermMemoryCompactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ShortTermMemoryCompactionRequest, ShortTermMemoryCompactionResult>(
            "api/memory/short-term/compact", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermArchiveSummary> GetShortTermArchiveSummaryAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId)
            .Add("sessionId", sessionId);
        return await GetRequiredAsync<ShortTermArchiveSummary>(
            $"api/memory/short-term/archive/summary{qs}", cancellationToken).ConfigureAwait(false);
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
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId)
            .Add("kind", kind)
            .Add("limit", limit)
            .Add("sessionId", sessionId);
        return await GetRequiredAsync<ShortTermArchiveItemsResponse>(
            $"api/memory/short-term/archive/items{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermCompactionRun>> GetShortTermCompactionRunsAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        string? trigger = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var qs = new QueryBuilder()
            .Add("collectionId", collectionId)
            .Add("sessionId", sessionId)
            .Add("take", take)
            .Add("trigger", trigger)
            .Add("workspaceId", workspaceId);
        return await GetRequiredAsync<IReadOnlyList<ShortTermCompactionRun>>(
            $"api/memory/short-term/compact/runs{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermCompactionRun> GetShortTermCompactionRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return await GetRequiredAsync<ShortTermCompactionRun>(
            $"api/memory/short-term/compact/runs/{Escape(runId)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermPromotionCandidate>> GenerateShortTermPromotionCandidatesAsync(
        ShortTermPromotionCandidateGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ShortTermPromotionCandidateGenerationRequest, IReadOnlyList<ShortTermPromotionCandidate>>(
            "api/memory/short-term/promotion/candidates/generate", request, cancellationToken).ConfigureAwait(false);
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
        return await GetRequiredAsync<ShortTermPromotionCandidate>(
            $"api/memory/short-term/promotion/candidates/{Escape(candidateId)}", cancellationToken).ConfigureAwait(false);
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
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId)
            .Add("kind", kind)
            .Add("limit", limit)
            .Add("minConfidence", minConfidence)
            .Add("minImportance", minImportance)
            .Add("offset", offset)
            .Add("sessionId", sessionId)
            .Add("status", ((int?)status)?.ToString())
            .Add("suggestedTargetLayer", suggestedTargetLayer);
        return await GetRequiredAsync<IReadOnlyList<ShortTermPromotionCandidate>>(
            $"api/memory/short-term/promotion/candidates{qs}", cancellationToken).ConfigureAwait(false);
    }
}
