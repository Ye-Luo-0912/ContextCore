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
        try
        {
            var result = await _generated.Api.Constraints.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.Level = (int?)level;
                config.QueryParameters.Take = take.ToString();
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<ContextConstraint>(result);
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

    public async Task<IReadOnlyList<ContextConstraint>> GetCandidateConstraintsAsync(
        string workspaceId,
        string? collectionId = null,
        ContextMemoryStatus? status = ContextMemoryStatus.Candidate,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        try
        {
            var result = await _generated.Api.Constraints.Candidates.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.Status = (int?)status;
                config.QueryParameters.Limit = limit.ToString();
                config.QueryParameters.Offset = offset.ToString();
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<ContextConstraint>(result);
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

    public async Task<ContextConstraint> GetCandidateConstraintAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        try
        {
            var result = await _generated.Api.Constraints.Candidates[constraintId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextConstraint>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/constraints/candidates/{constraintId}.");
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

    public async Task<CandidateConstraintReviewResult> ActivateCandidateConstraintAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.CandidateConstraintReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Constraints.Candidates[constraintId].Activate.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<CandidateConstraintReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/constraints/candidates/{constraintId}/activate.");
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

    public async Task<CandidateConstraintReviewResult> RejectCandidateConstraintAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.CandidateConstraintReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Constraints.Candidates[constraintId].Reject.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<CandidateConstraintReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/constraints/candidates/{constraintId}/reject.");
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

    public async Task<IReadOnlyList<CandidateConstraintReviewRecord>> GetCandidateConstraintReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        try
        {
            var result = await _generated.Api.Constraints.Candidates[constraintId].Reviews.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<CandidateConstraintReviewRecord>(result);
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

    public async Task<ConstraintGapGenerationResult> GenerateConstraintGapsAsync(
        ConstraintGapGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.ConstraintGapGenerationRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Constraints.Gaps.Generate.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ConstraintGapGenerationResult>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/constraints/gaps/generate.");
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
        try
        {
            var result = await _generated.Api.Constraints.Gaps.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = query.WorkspaceId;
                config.QueryParameters.CollectionId = query.CollectionId;
                config.QueryParameters.SessionId = query.SessionId;
                config.QueryParameters.Source = query.Source;
                config.QueryParameters.SourceSampleId = query.SourceSampleId;
                config.QueryParameters.Status = query.Status;
                config.QueryParameters.Severity = query.Severity;
                config.QueryParameters.Limit = query.Limit.ToString();
                config.QueryParameters.Offset = query.Offset.ToString();
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<ConstraintGapCandidate>(result);
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

    public async Task<ConstraintGapCandidate> GetConstraintGapAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        try
        {
            var result = await _generated.Api.Constraints.Gaps[gapId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ConstraintGapCandidate>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/constraints/gaps/{gapId}.");
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

    public async Task<ConstraintGapReviewResult> AcceptConstraintGapAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.ConstraintGapReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Constraints.Gaps[gapId].Accept.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ConstraintGapReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/constraints/gaps/{gapId}/accept.");
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

    public async Task<ConstraintGapReviewResult> RejectConstraintGapAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.ConstraintGapReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Constraints.Gaps[gapId].Reject.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ConstraintGapReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/constraints/gaps/{gapId}/reject.");
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

    public async Task<IReadOnlyList<ConstraintGapReviewRecord>> GetConstraintGapReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        try
        {
            var result = await _generated.Api.Constraints.Gaps[gapId].Reviews.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<ConstraintGapReviewRecord>(result);
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
