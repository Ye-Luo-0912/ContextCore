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
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<VectorIndexStatusResponse>($"api/vector/status{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorIndexDiagnosticsReport> GetVectorDiagnosticsAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<VectorIndexDiagnosticsReport>($"api/vector/diagnostics{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorReindexPreviewResponse> PreviewVectorReindexAsync(
        VectorReindexPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        return await PostRequiredAsync<VectorReindexPreviewRequest, VectorReindexPreviewResponse>(
            "api/vector/reindex-preview", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorQueryPreviewResult> PreviewVectorQueryAsync(
        VectorQueryPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.QueryText);
        return await PostRequiredAsync<VectorQueryPreviewRequest, VectorQueryPreviewResult>(
            "api/vector/query-preview", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorReindexPlan> CreateVectorReindexPlanAsync(
        VectorReindexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        return await PostRequiredAsync<VectorReindexRequest, VectorReindexPlan>(
            "api/vector/reindex-plan", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorReindexSubmitResponse> SubmitVectorReindexAsync(
        VectorReindexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        return await PostRequiredAsync<VectorReindexRequest, VectorReindexSubmitResponse>(
            "api/vector/reindex-submit", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorReindexReportQueryResponse> GetVectorReindexReportsAsync(
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
        return await GetRequiredAsync<VectorReindexReportQueryResponse>($"api/vector/reindex-reports{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorReindexResult> GetVectorReindexReportAsync(
        string reportId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        return await GetRequiredAsync<VectorReindexResult>($"api/vector/reindex-reports/{Escape(reportId)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorLifecycleMetadataReviewCandidateGenerationResult> GenerateVectorLifecycleMetadataReviewCandidatesAsync(
        VectorLifecycleMetadataReviewCandidateGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        return await PostRequiredAsync<VectorLifecycleMetadataReviewCandidateGenerationRequest, VectorLifecycleMetadataReviewCandidateGenerationResult>(
            "api/vector/lifecycle-metadata/review-candidates/generate", request, cancellationToken).ConfigureAwait(false);
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
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId)
            .Add("itemKind", itemKind)
            .Add("layer", layer)
            .Add("limit", limit)
            .Add("mustHitItemId", mustHitItemId)
            .Add("offset", offset)
            .Add("sourceEvalSet", sourceEvalSet)
            .Add("status", status);
        return await GetRequiredAsync<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>>($"api/vector/lifecycle-metadata/review-candidates{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorLifecycleMetadataReviewCandidate> GetVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return await GetRequiredAsync<VectorLifecycleMetadataReviewCandidate>($"api/vector/lifecycle-metadata/review-candidates/{Escape(candidateId)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorLifecycleMetadataReviewCandidateExplanation> ExplainVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return await GetRequiredAsync<VectorLifecycleMetadataReviewCandidateExplanation>($"api/vector/lifecycle-metadata/review-candidates/{Escape(candidateId)}/explain", cancellationToken).ConfigureAwait(false);
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
        return await GetRequiredAsync<IReadOnlyList<VectorLifecycleMetadataReviewRecord>>($"api/vector/lifecycle-metadata/review-candidates/{Escape(candidateId)}/reviews", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>> GetVectorLifecycleMetadataSidecarAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>>($"api/vector/lifecycle-metadata/sidecar{qs}", cancellationToken).ConfigureAwait(false);
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
        var path = route switch
        {
            "approve" => $"api/vector/lifecycle-metadata/review-candidates/{Escape(candidateId)}/approve",
            "reject" => $"api/vector/lifecycle-metadata/review-candidates/{Escape(candidateId)}/reject",
            "needs-evidence" => $"api/vector/lifecycle-metadata/review-candidates/{Escape(candidateId)}/needs-evidence",
            "supersede" => $"api/vector/lifecycle-metadata/review-candidates/{Escape(candidateId)}/supersede",
            _ => throw new ArgumentException($"Unknown review route '{route}'.", nameof(route))
        };
        return await PostRequiredAsync<VectorLifecycleMetadataReviewRequest, VectorLifecycleMetadataReviewResult>(
            path, request, cancellationToken).ConfigureAwait(false);
    }
}
