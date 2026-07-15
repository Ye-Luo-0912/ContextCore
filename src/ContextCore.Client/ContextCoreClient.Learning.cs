using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<ContextProvenanceResponse> GetProvenanceAsync(
        string itemId,
        string? workspaceId = null,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var qb = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        var path = $"api/provenance/{Escape(itemId)}{qb}";
        return await GetRequiredAsync<ContextProvenanceResponse>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextLearningRecord>> QueryLearningRecordsAsync(
        ContextLearningRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<IReadOnlyList<ContextLearningRecord>>(
            $"api/learning/records{BuildLearningRecordQueryString(query)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PromotionFeedbackSignal>> GetLearningFeedbackAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        string? candidateId = null,
        string? action = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var qs = new QueryBuilder()
            .Add("limit", limit)
            .Add("offset", offset)
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId)
            .Add("sessionId", sessionId)
            .Add("candidateId", candidateId)
            .Add("action", action);
        return await GetRequiredAsync<IReadOnlyList<PromotionFeedbackSignal>>(
            $"api/learning/feedback{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<LearningFeedbackSubmitResult> SubmitLearningFeedbackAsync(
        LearningFeedbackEvent feedbackEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedbackEvent);
        return await SubmitLearningFeedbackAsync(ToLearningFeedbackSubmitRequest(feedbackEvent), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LearningFeedbackSubmitResult> SubmitLearningFeedbackAsync(
        LearningFeedbackSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<LearningFeedbackSubmitRequest, LearningFeedbackSubmitResult>(
            "api/learning/feedback", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LearningFeedbackEvent>> GetLearningFeedbackAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<IReadOnlyList<LearningFeedbackEvent>>(
            $"api/learning/feedback{BuildRuntimeLearningFeedbackQueryString(query)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<LearningFeedbackSummaryReport> GetLearningFeedbackSummaryAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<LearningFeedbackSummaryReport>(
            $"api/learning/feedback/summary{BuildRuntimeLearningFeedbackQueryString(query, includeRuntimeFlag: false)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExportLearningFeedbackAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var path = $"api/learning/feedback/export{BuildRuntimeLearningFeedbackQueryString(query, includeRuntimeFlag: false)}";
        return await GetRequiredStringAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public Task<LearningFeedbackReviewResult> ApproveLearningFeedbackAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostLearningFeedbackReviewAsync(feedbackId, "approve", request, cancellationToken);
    }

    public Task<LearningFeedbackReviewResult> RejectLearningFeedbackAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostLearningFeedbackReviewAsync(feedbackId, "reject", request, cancellationToken);
    }

    public Task<LearningFeedbackReviewResult> MarkLearningFeedbackNeedsRedactionAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostLearningFeedbackReviewAsync(feedbackId, "needs-redaction", request, cancellationToken);
    }

    public Task<LearningFeedbackReviewResult> MarkLearningFeedbackNeedsEvidenceAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostLearningFeedbackReviewAsync(feedbackId, "needs-evidence", request, cancellationToken);
    }

    public async Task<IReadOnlyList<LearningFeedbackReviewRecord>> GetLearningFeedbackReviewsAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<IReadOnlyList<LearningFeedbackReviewRecord>>(
            $"api/learning/feedback/reviews{BuildLearningFeedbackReviewQueryString(query)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<LearningFeedbackReviewSummaryReport> GetLearningFeedbackReviewSummaryAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<LearningFeedbackReviewSummaryReport>(
            $"api/learning/feedback/reviews/summary{BuildLearningFeedbackReviewQueryString(query)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningRecord> GetLearningRecordAsync(
        string recordId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return await GetRequiredAsync<ContextLearningRecord>(
            $"api/learning/records/{Escape(recordId)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextLearningCase>> QueryLearningCasesAsync(
        ContextLearningCaseQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<IReadOnlyList<ContextLearningCase>>(
            $"api/learning/cases{BuildLearningCaseQueryString(query)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextLearningCase>> GetLearningCasesAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        ContextFeedbackSignal? signal = null,
        ContextFailureType? failureType = null,
        ContextLearningCaseStatus? status = null,
        string? caseKind = null,
        string? sourceRecordId = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return await QueryLearningCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Signal = signal,
            FailureType = failureType,
            Status = status,
            CaseKind = caseKind,
            SourceRecordId = sourceRecordId,
            Limit = limit,
            Offset = offset
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCase> GetLearningCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        return await GetRequiredAsync<ContextLearningCase>(
            $"api/learning/cases/{Escape(caseId)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCase> CreateLearningCaseAsync(
        ContextLearningCase learningCase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(learningCase);
        return await PostRequiredAsync<ContextLearningCase, ContextLearningCase>(
            "api/learning/cases", learningCase, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCaseGenerationResult> GenerateLearningCasesAsync(
        ContextLearningCaseGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextLearningCaseGenerationRequest, ContextLearningCaseGenerationResult>(
            "api/learning/cases/generate", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCaseStatusUpdateResponse> ActivateLearningCaseAsync(
        string caseId,
        ContextLearningCaseStatusUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextLearningCaseStatusUpdateRequest, ContextLearningCaseStatusUpdateResponse>(
            $"api/learning/cases/{Escape(caseId)}/activate", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCaseStatusUpdateResponse> ArchiveLearningCaseAsync(
        string caseId,
        ContextLearningCaseStatusUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextLearningCaseStatusUpdateRequest, ContextLearningCaseStatusUpdateResponse>(
            $"api/learning/cases/{Escape(caseId)}/archive", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCaseStatusUpdateResponse> RejectLearningCaseAsync(
        string caseId,
        ContextLearningCaseStatusUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextLearningCaseStatusUpdateRequest, ContextLearningCaseStatusUpdateResponse>(
            $"api/learning/cases/{Escape(caseId)}/reject", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningSummary> GetLearningSummaryAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId)
            .Add("sessionId", sessionId);
        return await GetRequiredAsync<ContextLearningSummary>(
            $"api/learning/summary{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextLearningCase>> GetRegressionLearningCasesAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var qs = new QueryBuilder()
            .Add("limit", limit)
            .Add("offset", offset)
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId)
            .Add("sessionId", sessionId);
        return await GetRequiredAsync<IReadOnlyList<ContextLearningCase>>(
            $"api/learning/regression/cases{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPromotionRecord> PromoteMemoryAsync(
        ContextCoreMemoryPromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextCoreMemoryPromotionRequest, ContextPromotionRecord>(
            "api/memory/promote", request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LearningFeedbackReviewResult> PostLearningFeedbackReviewAsync(
        string feedbackId,
        string route,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedbackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<LearningFeedbackReviewRequest, LearningFeedbackReviewResult>(
            $"api/learning/feedback/{Escape(feedbackId)}/review/{route}", request, cancellationToken).ConfigureAwait(false);
    }

    private static LearningFeedbackSubmitRequest ToLearningFeedbackSubmitRequest(LearningFeedbackEvent feedbackEvent)
    {
        if (!Enum.TryParse<LearningFeedbackTargetType>(
            feedbackEvent.TargetType,
            ignoreCase: true,
            out var parsedTargetType))
        {
            throw new ArgumentException($"Invalid targetType '{feedbackEvent.TargetType}'.", nameof(feedbackEvent));
        }

        return new LearningFeedbackSubmitRequest
        {
            FeedbackId = feedbackEvent.FeedbackId,
            WorkspaceId = feedbackEvent.WorkspaceId,
            CollectionId = feedbackEvent.CollectionId,
            Source = feedbackEvent.Source,
            SourceOperationId = feedbackEvent.SourceOperationId,
            CapabilityId = feedbackEvent.CapabilityId,
            TargetId = feedbackEvent.TargetId,
            TargetType = parsedTargetType,
            FeedbackKind = feedbackEvent.FeedbackKind,
            FeedbackValue = feedbackEvent.FeedbackValue,
            Reason = feedbackEvent.Reason,
            UserCorrection = feedbackEvent.UserCorrection,
            RedactionMode = feedbackEvent.RedactionMode,
            MetadataOnly = feedbackEvent.MetadataOnly,
            TrainingUse = feedbackEvent.TrainingUse,
            Confidence = feedbackEvent.Confidence,
            CreatedAt = feedbackEvent.CreatedAt,
            Metadata = feedbackEvent.Metadata
        };
    }
}
