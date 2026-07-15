using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<ShortTermPromotionCandidateExplanation> ExplainShortTermPromotionCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Promotion.Candidates[candidateId].Explain.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ShortTermPromotionCandidateExplanation>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/memory/short-term/promotion/candidates/{candidateId}/explain.");
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

    public async Task<ReviewPromotionCandidateResponse> AcceptShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.PromotionCandidateReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Promotion.Candidates[candidateId].Accept.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ReviewPromotionCandidateResponse>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/memory/short-term/promotion/candidates/{candidateId}/accept.");
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

    public async Task<PromotionCandidateReviewResult> AcceptShortTermPromotionCandidateAsync(
        string candidateId,
        PromotionCandidateReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.PromotionCandidateReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Promotion.Candidates[candidateId].Accept.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<PromotionCandidateReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/memory/short-term/promotion/candidates/{candidateId}/accept.");
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

    public async Task<ReviewPromotionCandidateResponse> RejectShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.PromotionCandidateReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Promotion.Candidates[candidateId].Reject.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ReviewPromotionCandidateResponse>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/memory/short-term/promotion/candidates/{candidateId}/reject.");
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

    public async Task<PromotionCandidateReviewResult> RejectShortTermPromotionCandidateAsync(
        string candidateId,
        PromotionCandidateReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.PromotionCandidateReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Promotion.Candidates[candidateId].Reject.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<PromotionCandidateReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/memory/short-term/promotion/candidates/{candidateId}/reject.");
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

    public async Task<ReviewPromotionCandidateResponse> ExpireShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.ReviewPromotionCandidateRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Memory.ShortTerm.Promotion.Candidates[candidateId].Expire.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ReviewPromotionCandidateResponse>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/memory/short-term/promotion/candidates/{candidateId}/expire.");
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

    public async Task<IReadOnlyList<PromotionCandidateReviewRecord>> GetShortTermPromotionCandidateReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        try
        {
            var stream = await _generated.Api.Memory.ShortTerm.Promotion.Candidates[candidateId].Reviews.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return await MapStreamToAbstraction<IReadOnlyList<PromotionCandidateReviewRecord>>(stream).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/memory/short-term/promotion/candidates/{candidateId}/reviews.");
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

    public async Task<IReadOnlyList<StableReviewCandidate>> GenerateStableReviewCandidatesAsync(
        StableReviewCandidateGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<StableReviewCandidateGenerationRequest, IReadOnlyList<StableReviewCandidate>>(
            "api/memory/stable-review/candidates/generate", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StableReviewCandidate>> GetStableReviewCandidatesAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        string? status = null,
        string? validationStatus = null,
        string? kind = null,
        string? suggestedStableTarget = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return await QueryStableReviewCandidatesAsync(new StableReviewCandidateQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Status = status,
            ValidationStatus = validationStatus,
            Kind = kind,
            SuggestedStableTarget = suggestedStableTarget,
            Limit = limit,
            Offset = offset
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StableReviewCandidate>> QueryStableReviewCandidatesAsync(
        StableReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkspaceId);
        return await GetRequiredAsync<IReadOnlyList<StableReviewCandidate>>(
            $"api/memory/stable-review/candidates{BuildStableReviewCandidateQueryString(query)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReviewCandidate> GetStableReviewCandidateAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        return await GetRequiredAsync<StableReviewCandidate>(
            $"api/memory/stable-review/candidates/{Escape(stableReviewCandidateId)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReviewCandidateExplanation> ExplainStableReviewCandidateAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        return await GetRequiredAsync<StableReviewCandidateExplanation>(
            $"api/memory/stable-review/candidates/{Escape(stableReviewCandidateId)}/explain",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReviewDecisionResult> AcceptStableReviewCandidateAsync(
        string stableReviewCandidateId,
        StableReviewDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<StableReviewDecisionRequest, StableReviewDecisionResult>(
            $"api/memory/stable-review/candidates/{Escape(stableReviewCandidateId)}/accept",
            request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReviewDecisionResult> RejectStableReviewCandidateAsync(
        string stableReviewCandidateId,
        StableReviewDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<StableReviewDecisionRequest, StableReviewDecisionResult>(
            $"api/memory/stable-review/candidates/{Escape(stableReviewCandidateId)}/reject",
            request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StableReviewRecord>> GetStableReviewCandidateReviewsAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        return await GetRequiredAsync<IReadOnlyList<StableReviewRecord>>(
            $"api/memory/stable-review/candidates/{Escape(stableReviewCandidateId)}/reviews",
            cancellationToken).ConfigureAwait(false);
    }
}
