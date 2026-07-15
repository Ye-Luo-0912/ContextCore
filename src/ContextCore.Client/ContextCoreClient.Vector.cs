using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<VectorIndexStatusResponse> GetVectorStatusAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        try
        {
            var result = await _generated.Api.Vector.Status.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<VectorIndexStatusResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/vector/status.");
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

    public async Task<VectorIndexDiagnosticsReport> GetVectorDiagnosticsAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        try
        {
            var result = await _generated.Api.Vector.Diagnostics.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<VectorIndexDiagnosticsReport>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/vector/diagnostics.");
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

    public async Task<VectorReindexPreviewResponse> PreviewVectorReindexAsync(
        VectorReindexPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.VectorReindexPreviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Vector.ReindexPreview.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<VectorReindexPreviewResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/vector/reindex-preview.");
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

    public async Task<VectorQueryPreviewResult> PreviewVectorQueryAsync(
        VectorQueryPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.QueryText);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.VectorQueryPreviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Vector.QueryPreview.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<VectorQueryPreviewResult>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/vector/query-preview.");
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

    public async Task<VectorReindexPlan> CreateVectorReindexPlanAsync(
        VectorReindexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.VectorReindexRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Vector.ReindexPlan.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<VectorReindexPlan>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/vector/reindex-plan.");
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

    public async Task<VectorReindexSubmitResponse> SubmitVectorReindexAsync(
        VectorReindexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.VectorReindexRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Vector.ReindexSubmit.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<VectorReindexSubmitResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/vector/reindex-submit.");
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

    public async Task<VectorReindexReportQueryResponse> GetVectorReindexReportsAsync(
        string workspaceId,
        string collectionId,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        try
        {
            var result = await _generated.Api.Vector.ReindexReports.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.Take = take.ToString();
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<VectorReindexReportQueryResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/vector/reindex-reports.");
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

    public async Task<VectorReindexResult> GetVectorReindexReportAsync(
        string reportId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        try
        {
            var result = await _generated.Api.Vector.ReindexReports[reportId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<VectorReindexResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/vector/reindex-reports/{reportId}.");
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

    public async Task<VectorLifecycleMetadataReviewCandidateGenerationResult> GenerateVectorLifecycleMetadataReviewCandidatesAsync(
        VectorLifecycleMetadataReviewCandidateGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.VectorLifecycleMetadataReviewCandidateGenerationRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Vector.LifecycleMetadata.ReviewCandidates.Generate.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<VectorLifecycleMetadataReviewCandidateGenerationResult>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/vector/lifecycle-metadata/review-candidates/generate.");
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

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>> GetVectorLifecycleMetadataReviewCandidatesAsync(
        string workspaceId,
        string? collectionId = null,
        string? status = null,
        string? layer = null,
        string? itemKind = null,
        string? mustHitItemId = null,
        string? sourceEvalSet = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        try
        {
            var result = await _generated.Api.Vector.LifecycleMetadata.ReviewCandidates.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.Limit = limit.ToString();
                config.QueryParameters.Offset = offset.ToString();
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.Status = status;
                config.QueryParameters.Layer = layer;
                config.QueryParameters.ItemKind = itemKind;
                config.QueryParameters.MustHitItemId = mustHitItemId;
                config.QueryParameters.SourceEvalSet = sourceEvalSet;
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<VectorLifecycleMetadataReviewCandidate>(result);
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

    public async Task<VectorLifecycleMetadataReviewCandidate> GetVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        try
        {
            var result = await _generated.Api.Vector.LifecycleMetadata.ReviewCandidates[candidateId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<VectorLifecycleMetadataReviewCandidate>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/vector/lifecycle-metadata/review-candidates/{candidateId}.");
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

    public async Task<VectorLifecycleMetadataReviewCandidateExplanation> ExplainVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        try
        {
            var result = await _generated.Api.Vector.LifecycleMetadata.ReviewCandidates[candidateId].Explain.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<VectorLifecycleMetadataReviewCandidateExplanation>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/vector/lifecycle-metadata/review-candidates/{candidateId}/explain.");
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

    public async Task<VectorLifecycleMetadataReviewResult> ApproveVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        VectorLifecycleMetadataReviewRequest request,
        CancellationToken cancellationToken = default)
        => await PostVectorLifecycleMetadataReviewAsync(candidateId, "approve", request, cancellationToken).ConfigureAwait(false);

    public async Task<VectorLifecycleMetadataReviewResult> RejectVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        VectorLifecycleMetadataReviewRequest request,
        CancellationToken cancellationToken = default)
        => await PostVectorLifecycleMetadataReviewAsync(candidateId, "reject", request, cancellationToken).ConfigureAwait(false);

    public async Task<VectorLifecycleMetadataReviewResult> NeedsEvidenceVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        VectorLifecycleMetadataReviewRequest request,
        CancellationToken cancellationToken = default)
        => await PostVectorLifecycleMetadataReviewAsync(candidateId, "needs-evidence", request, cancellationToken).ConfigureAwait(false);

    public async Task<VectorLifecycleMetadataReviewResult> SupersedeVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        VectorLifecycleMetadataReviewRequest request,
        CancellationToken cancellationToken = default)
        => await PostVectorLifecycleMetadataReviewAsync(candidateId, "supersede", request, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> GetVectorLifecycleMetadataReviewHistoryAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        try
        {
            var result = await _generated.Api.Vector.LifecycleMetadata.ReviewCandidates[candidateId].Reviews.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<VectorLifecycleMetadataReviewRecord>(result);
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

    public async Task<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>> GetVectorLifecycleMetadataSidecarAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        try
        {
            var result = await _generated.Api.Vector.LifecycleMetadata.Sidecar.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<VectorLifecycleSidecarMetadataEntry>(result);
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

    private async Task<VectorLifecycleMetadataReviewResult> PostVectorLifecycleMetadataReviewAsync(
        string candidateId,
        string route,
        VectorLifecycleMetadataReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.VectorLifecycleMetadataReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        var item = _generated.Api.Vector.LifecycleMetadata.ReviewCandidates[candidateId];
        try
        {
            Task<ContextCore.Client.Generated.Models.VectorLifecycleMetadataReviewResult?> postTask = route switch
            {
                "approve" => item.Approve.PostAsync(generatedRequest, cancellationToken: cancellationToken),
                "reject" => item.Reject.PostAsync(generatedRequest, cancellationToken: cancellationToken),
                "needs-evidence" => item.NeedsEvidence.PostAsync(generatedRequest, cancellationToken: cancellationToken),
                "supersede" => item.Supersede.PostAsync(generatedRequest, cancellationToken: cancellationToken),
                _ => throw new ArgumentException($"Unknown review route '{route}'.", nameof(route))
            };
            var result = await postTask.ConfigureAwait(false);
            return MapToAbstraction<VectorLifecycleMetadataReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/vector/lifecycle-metadata/review-candidates/{candidateId}/{route}.");
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
