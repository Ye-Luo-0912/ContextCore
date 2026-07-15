using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<IReadOnlyList<ContextConstraint>> QueryConstraintsAsync(
        string workspaceId,
        string? collectionId = null,
        ConstraintLevel? level = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId)
            .Add("level", ((int?)level)?.ToString())
            .Add("take", take);
        return await GetRequiredAsync<IReadOnlyList<ContextConstraint>>($"api/constraints{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextConstraint>> GetCandidateConstraintsAsync(
        string workspaceId,
        string? collectionId = null,
        ContextMemoryStatus? status = ContextMemoryStatus.Candidate,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId)
            .Add("limit", limit)
            .Add("offset", offset)
            .Add("status", ((int?)status)?.ToString());
        return await GetRequiredAsync<IReadOnlyList<ContextConstraint>>($"api/constraints/candidates{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextConstraint> GetCandidateConstraintAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        return await GetRequiredAsync<ContextConstraint>($"api/constraints/candidates/{Escape(constraintId)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateConstraintReviewResult> ActivateCandidateConstraintAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<CandidateConstraintReviewRequest, CandidateConstraintReviewResult>(
            $"api/constraints/candidates/{Escape(constraintId)}/activate", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateConstraintReviewResult> RejectCandidateConstraintAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<CandidateConstraintReviewRequest, CandidateConstraintReviewResult>(
            $"api/constraints/candidates/{Escape(constraintId)}/reject", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CandidateConstraintReviewRecord>> GetCandidateConstraintReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        return await GetRequiredAsync<IReadOnlyList<CandidateConstraintReviewRecord>>($"api/constraints/candidates/{Escape(constraintId)}/reviews", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConstraintGapGenerationResult> GenerateConstraintGapsAsync(
        ConstraintGapGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        return await PostRequiredAsync<ConstraintGapGenerationRequest, ConstraintGapGenerationResult>(
            "api/constraints/gaps/generate", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConstraintGapCandidate>> GetConstraintGapsAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        string? source = null,
        string? sourceSampleId = null,
        string? status = null,
        string? severity = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return await QueryConstraintGapsAsync(new ConstraintGapCandidateQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Source = source,
            SourceSampleId = sourceSampleId,
            Status = status,
            Severity = severity,
            Limit = limit,
            Offset = offset
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConstraintGapCandidate>> QueryConstraintGapsAsync(
        ConstraintGapCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkspaceId);
        var qs = new QueryBuilder()
            .Add("workspaceId", query.WorkspaceId)
            .Add("collectionId", query.CollectionId)
            .Add("limit", query.Limit)
            .Add("offset", query.Offset)
            .Add("sessionId", query.SessionId)
            .Add("severity", query.Severity)
            .Add("source", query.Source)
            .Add("sourceSampleId", query.SourceSampleId)
            .Add("status", query.Status);
        return await GetRequiredAsync<IReadOnlyList<ConstraintGapCandidate>>($"api/constraints/gaps{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConstraintGapCandidate> GetConstraintGapAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        return await GetRequiredAsync<ConstraintGapCandidate>($"api/constraints/gaps/{Escape(gapId)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConstraintGapReviewResult> AcceptConstraintGapAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ConstraintGapReviewRequest, ConstraintGapReviewResult>(
            $"api/constraints/gaps/{Escape(gapId)}/accept", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConstraintGapReviewResult> RejectConstraintGapAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ConstraintGapReviewRequest, ConstraintGapReviewResult>(
            $"api/constraints/gaps/{Escape(gapId)}/reject", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConstraintGapReviewRecord>> GetConstraintGapReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        return await GetRequiredAsync<IReadOnlyList<ConstraintGapReviewRecord>>($"api/constraints/gaps/{Escape(gapId)}/reviews", cancellationToken).ConfigureAwait(false);
    }
}
