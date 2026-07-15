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
        // Provenance 响应包含 UntypedNode 字段，Kiota JSON 序列化器无法正确处理 null UntypedNode，
        // 保留直接 HttpClient + STJ 反序列化。
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
        try
        {
            var result = await _generated.Api.Learning.Records.GetAsync(config =>
            {
                config.QueryParameters.Limit = query.Limit.ToString();
                config.QueryParameters.Offset = query.Offset.ToString();
                config.QueryParameters.WorkspaceId = query.WorkspaceId;
                config.QueryParameters.CollectionId = query.CollectionId;
                config.QueryParameters.SessionId = query.SessionId;
                config.QueryParameters.Signal = (int?)query.Signal;
                config.QueryParameters.FailureType = (int?)query.FailureType;
                config.QueryParameters.SourceKind = query.SourceKind;
                config.QueryParameters.SourceId = query.SourceId;
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<ContextLearningRecord>(result);
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
        try
        {
            var result = await _generated.Api.Learning.Feedback.GetAsync(config =>
            {
                config.QueryParameters.Limit = limit.ToString();
                config.QueryParameters.Offset = offset.ToString();
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.SessionId = sessionId;
                config.QueryParameters.CandidateId = candidateId;
                config.QueryParameters.Action = action;
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<PromotionFeedbackSignal>(result);
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
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.LearningFeedbackSubmitRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Learning.Feedback.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<LearningFeedbackSubmitResult>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/learning/feedback.");
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

    public async Task<IReadOnlyList<LearningFeedbackEvent>> GetLearningFeedbackAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var result = await _generated.Api.Learning.Feedback.GetAsync(config =>
            {
                config.QueryParameters.Limit = query.Limit.ToString();
                config.QueryParameters.Offset = query.Offset.ToString();
                config.QueryParameters.RuntimeFeedback = true;
                config.QueryParameters.WorkspaceId = query.WorkspaceId;
                config.QueryParameters.CollectionId = query.CollectionId;
                config.QueryParameters.Source = query.Source;
                config.QueryParameters.SourceOperationId = query.SourceOperationId;
                config.QueryParameters.CapabilityId = query.CapabilityId;
                config.QueryParameters.TargetId = query.TargetId;
                config.QueryParameters.TargetType = query.TargetType;
                config.QueryParameters.FeedbackKind = query.FeedbackKind;
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<LearningFeedbackEvent>(result);
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

    public async Task<LearningFeedbackSummaryReport> GetLearningFeedbackSummaryAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var result = await _generated.Api.Learning.Feedback.Summary.GetAsync(config =>
            {
                config.QueryParameters.Limit = query.Limit.ToString();
                config.QueryParameters.Offset = query.Offset.ToString();
                config.QueryParameters.WorkspaceId = query.WorkspaceId;
                config.QueryParameters.CollectionId = query.CollectionId;
                config.QueryParameters.Source = query.Source;
                config.QueryParameters.SourceOperationId = query.SourceOperationId;
                config.QueryParameters.CapabilityId = query.CapabilityId;
                config.QueryParameters.TargetId = query.TargetId;
                config.QueryParameters.TargetType = query.TargetType;
                config.QueryParameters.FeedbackKind = query.FeedbackKind;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<LearningFeedbackSummaryReport>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/learning/feedback/summary.");
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
        try
        {
            var result = await _generated.Api.Learning.Feedback.Reviews.GetAsync(config =>
            {
                config.QueryParameters.Limit = query.Limit.ToString();
                config.QueryParameters.Offset = query.Offset.ToString();
                config.QueryParameters.FeedbackId = query.FeedbackId;
                config.QueryParameters.ReviewStatus = (int?)query.ReviewStatus;
                config.QueryParameters.Reviewer = query.Reviewer;
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<LearningFeedbackReviewRecord>(result);
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

    public async Task<LearningFeedbackReviewSummaryReport> GetLearningFeedbackReviewSummaryAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var result = await _generated.Api.Learning.Feedback.Reviews.Summary.GetAsync(config =>
            {
                config.QueryParameters.Limit = query.Limit.ToString();
                config.QueryParameters.Offset = query.Offset.ToString();
                config.QueryParameters.FeedbackId = query.FeedbackId;
                config.QueryParameters.ReviewStatus = (int?)query.ReviewStatus;
                config.QueryParameters.Reviewer = query.Reviewer;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<LearningFeedbackReviewSummaryReport>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/learning/feedback/reviews/summary.");
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

    public async Task<ContextLearningRecord> GetLearningRecordAsync(
        string recordId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        try
        {
            var result = await _generated.Api.Learning.Records[recordId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextLearningRecord>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/learning/records/{recordId}.");
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

    public async Task<IReadOnlyList<ContextLearningCase>> QueryLearningCasesAsync(
        ContextLearningCaseQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var result = await _generated.Api.Learning.Cases.GetAsync(config =>
            {
                config.QueryParameters.Limit = query.Limit.ToString();
                config.QueryParameters.Offset = query.Offset.ToString();
                config.QueryParameters.WorkspaceId = query.WorkspaceId;
                config.QueryParameters.CollectionId = query.CollectionId;
                config.QueryParameters.SessionId = query.SessionId;
                config.QueryParameters.Signal = (int?)query.Signal;
                config.QueryParameters.FailureType = (int?)query.FailureType;
                config.QueryParameters.Status = (int?)query.Status;
                config.QueryParameters.CaseKind = query.CaseKind;
                config.QueryParameters.SourceRecordId = query.SourceRecordId;
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<ContextLearningCase>(result);
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
        try
        {
            var result = await _generated.Api.Learning.Cases[caseId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextLearningCase>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/learning/cases/{caseId}.");
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

    public async Task<ContextLearningCase> CreateLearningCaseAsync(
        ContextLearningCase learningCase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(learningCase);
        var generatedRequest = await MapToGenerated(learningCase, ContextCore.Client.Generated.Models.ContextLearningCase.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(learningCase));
        try
        {
            var result = await _generated.Api.Learning.Cases.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextLearningCase>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/learning/cases.");
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

    public async Task<ContextLearningCaseGenerationResult> GenerateLearningCasesAsync(
        ContextLearningCaseGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.ContextLearningCaseGenerationRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Learning.Cases.Generate.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextLearningCaseGenerationResult>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/learning/cases/generate.");
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

    public async Task<ContextLearningCaseStatusUpdateResponse> ActivateLearningCaseAsync(
        string caseId,
        ContextLearningCaseStatusUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.ContextLearningCaseStatusUpdateRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Learning.Cases[caseId].Activate.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextLearningCaseStatusUpdateResponse>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/learning/cases/{caseId}/activate.");
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

    public async Task<ContextLearningCaseStatusUpdateResponse> ArchiveLearningCaseAsync(
        string caseId,
        ContextLearningCaseStatusUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.ContextLearningCaseStatusUpdateRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Learning.Cases[caseId].Archive.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextLearningCaseStatusUpdateResponse>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/learning/cases/{caseId}/archive.");
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

    public async Task<ContextLearningCaseStatusUpdateResponse> RejectLearningCaseAsync(
        string caseId,
        ContextLearningCaseStatusUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.ContextLearningCaseStatusUpdateRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Learning.Cases[caseId].Reject.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextLearningCaseStatusUpdateResponse>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/learning/cases/{caseId}/reject.");
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

    public async Task<ContextLearningSummary> GetLearningSummaryAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Learning.Summary.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.SessionId = sessionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextLearningSummary>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/learning/summary.");
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

    public async Task<IReadOnlyList<ContextLearningCase>> GetRegressionLearningCasesAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Learning.Regression.Cases.GetAsync(config =>
            {
                config.QueryParameters.Limit = limit.ToString();
                config.QueryParameters.Offset = offset.ToString();
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
                config.QueryParameters.SessionId = sessionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapCollectionToAbstraction<ContextLearningCase>(result);
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

    public async Task<ContextPromotionRecord> PromoteMemoryAsync(
        ContextCoreMemoryPromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.PromoteRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        try
        {
            var result = await _generated.Api.Memory.Promote.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextPromotionRecord>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/memory/promote.");
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

    private async Task<LearningFeedbackReviewResult> PostLearningFeedbackReviewAsync(
        string feedbackId,
        string route,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedbackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(request);
        var generatedRequest = await MapToGenerated(request, ContextCore.Client.Generated.Models.LearningFeedbackReviewRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new ArgumentNullException(nameof(request));
        var review = _generated.Api.Learning.Feedback[feedbackId].Review;
        try
        {
            Task<ContextCore.Client.Generated.Models.LearningFeedbackReviewResult?> postTask = route switch
            {
                "approve" => review.Approve.PostAsync(generatedRequest, cancellationToken: cancellationToken),
                "reject" => review.Reject.PostAsync(generatedRequest, cancellationToken: cancellationToken),
                "needs-evidence" => review.NeedsEvidence.PostAsync(generatedRequest, cancellationToken: cancellationToken),
                "needs-redaction" => review.NeedsRedaction.PostAsync(generatedRequest, cancellationToken: cancellationToken),
                _ => throw new ArgumentException($"Unknown review route '{route}'.", nameof(route))
            };
            var result = await postTask.ConfigureAwait(false);
            return MapToAbstraction<LearningFeedbackReviewResult>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST api/learning/feedback/{feedbackId}/review/{route}.");
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
