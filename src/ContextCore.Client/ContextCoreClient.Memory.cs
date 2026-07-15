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
        return await PostRequiredAsync<WorkingMemoryItem, WorkingMemoryItem>(
            "api/memory/working/add", item, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkingMemoryItem>> GetRecentWorkingMemoryAsync(
        string workspaceId,
        string collectionId,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        var qs = new QueryBuilder()
            .Add("collectionId", collectionId)
            .Add("workspaceId", workspaceId)
            .Add("take", take);
        return await GetRequiredAsync<IReadOnlyList<WorkingMemoryItem>>(
            $"api/memory/working/recent{qs}", cancellationToken).ConfigureAwait(false);
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
        await PostNoContentAsync<ContextCoreWorkingMemoryScopeRequest>(
            "api/memory/working/clear", scopeRequest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryActiveContext?> GetWorkingMemoryActiveContextAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        var qs = new QueryBuilder()
            .Add("collectionId", collectionId)
            .Add("workspaceId", workspaceId);
        return await GetRequiredAsync<WorkingMemoryActiveContext>(
            $"api/memory/working/active-context{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryActiveContext> SetWorkingMemoryActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeContext);
        return await PostRequiredAsync<WorkingMemoryActiveContext, WorkingMemoryActiveContext>(
            "api/memory/working/active-context", activeContext, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryCurrentTask?> GetWorkingMemoryCurrentTaskAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        var qs = new QueryBuilder()
            .Add("collectionId", collectionId)
            .Add("workspaceId", workspaceId);
        return await GetRequiredAsync<WorkingMemoryCurrentTask>(
            $"api/memory/working/current-task{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryCurrentTask> SetWorkingMemoryCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentTask);
        return await PostRequiredAsync<WorkingMemoryCurrentTask, WorkingMemoryCurrentTask>(
            "api/memory/working/current-task", currentTask, cancellationToken).ConfigureAwait(false);
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
