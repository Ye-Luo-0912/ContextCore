using ContextCore.Client;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Screens;

/// <summary>Service 模式下的 Learning Feature Dataset 只读页面。</summary>
public static class ServiceLearningFeaturesScreen
{
    public static async Task<ControlRoomActionKind> ShowAsync(
        ControlRoomService service,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var snapshot = await service.GetServiceLearningFeaturesSnapshotAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine(ServiceOperationalRenderer.RenderLearningFeatures(snapshot));
            }
            catch (ContextCoreApiException ex)
            {
                Console.WriteLine(ServiceOperationalRenderer.RenderError(ex));
            }

            Console.Write("B/0 back, Q quit, F feedback, A approve, R reject, N needs-redaction, E needs-evidence, H history: ");
            var input = Console.ReadLine();
            if (string.Equals(input, "F", StringComparison.OrdinalIgnoreCase))
            {
                await SubmitFeedbackAsync(service, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (string.Equals(input, "A", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewFeedbackAsync(service, FeedbackReviewStatus.ApprovedForDataset, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            if (string.Equals(input, "R", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewFeedbackAsync(service, FeedbackReviewStatus.Rejected, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            if (string.Equals(input, "N", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewFeedbackAsync(service, FeedbackReviewStatus.NeedsRedaction, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            if (string.Equals(input, "E", StringComparison.OrdinalIgnoreCase))
            {
                await ReviewFeedbackAsync(service, FeedbackReviewStatus.NeedsMoreEvidence, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            if (string.Equals(input, "H", StringComparison.OrdinalIgnoreCase))
            {
                await ShowReviewHistoryAsync(service, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var action = ControlRoomInteraction.InterpretDetailInput(input);
            if (action.Kind is ControlRoomActionKind.Back or ControlRoomActionKind.Quit)
            {
                return action.Kind;
            }
        }
    }

    private static async Task SubmitFeedbackAsync(
        ControlRoomService service,
        CancellationToken cancellationToken)
    {
        Console.Write("CapabilityId: ");
        var capabilityId = Console.ReadLine() ?? string.Empty;
        Console.Write("TargetType: ");
        var targetTypeText = Console.ReadLine() ?? string.Empty;
        Console.Write("TargetId: ");
        var targetId = Console.ReadLine() ?? string.Empty;
        Console.Write("FeedbackKind: ");
        var feedbackKind = Console.ReadLine() ?? string.Empty;
        Console.Write("SourceOperationId: ");
        var sourceOperationId = Console.ReadLine() ?? string.Empty;
        Console.Write("Reason (metadata-only 默认会脱敏正文): ");
        var reason = Console.ReadLine() ?? string.Empty;

        if (!Enum.TryParse<LearningFeedbackTargetType>(targetTypeText, ignoreCase: true, out var targetType))
        {
            Console.WriteLine($"Invalid target type: {targetTypeText}");
            return;
        }

        try
        {
            var result = await service.SubmitLearningFeedbackAsync(new LearningFeedbackSubmitRequest
            {
                WorkspaceId = service.State.WorkspaceId,
                CollectionId = service.State.CollectionId,
                Source = "ControlRoom LearningFeatures",
                SourceOperationId = sourceOperationId,
                CapabilityId = capabilityId,
                TargetId = targetId,
                TargetType = targetType,
                FeedbackKind = feedbackKind,
                FeedbackValue = -1,
                Reason = reason,
                MetadataOnly = true,
                RedactionMode = "metadata-only",
                TrainingUse = "disabled_until_review",
                Confidence = 1.0
            }, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Feedback submitted: {result.FeedbackId} created={result.Created} duplicateReplaced={result.DuplicateReplaced}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Feedback submit failed: {ex.Message}");
        }
    }

    private static async Task ReviewFeedbackAsync(
        ControlRoomService service,
        FeedbackReviewStatus status,
        CancellationToken cancellationToken)
    {
        Console.Write("FeedbackId: ");
        var feedbackId = Console.ReadLine() ?? string.Empty;
        var feedback = await FindFeedbackAsync(service, feedbackId, cancellationToken).ConfigureAwait(false);
        if (feedback is null)
        {
            Console.WriteLine($"Feedback not found: {feedbackId}");
            return;
        }

        PrintFeedbackDetail(feedback);
        Console.Write("Reviewer: ");
        var reviewer = Console.ReadLine() ?? Environment.UserName;
        Console.Write("Reason: ");
        var reason = Console.ReadLine() ?? string.Empty;
        Console.Write("Type YES to confirm review operation: ");
        var confirmation = Console.ReadLine();
        if (!string.Equals(confirmation, "YES", StringComparison.Ordinal))
        {
            Console.WriteLine("Review cancelled.");
            return;
        }

        var request = new LearningFeedbackReviewRequest
        {
            Reviewer = string.IsNullOrWhiteSpace(reviewer) ? Environment.UserName : reviewer.Trim(),
            ReviewReason = reason,
            RedactionChecked = status == FeedbackReviewStatus.ApprovedForDataset,
            TrainingUse = status == FeedbackReviewStatus.ApprovedForDataset
                ? "approved_for_dataset"
                : "disabled_until_review"
        };

        try
        {
            var result = await service.ReviewLearningFeedbackAsync(
                    feedback.FeedbackId,
                    status,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"Review recorded: {result.FeedbackId} status={result.ReviewStatus}");
            foreach (var warning in result.Warnings)
            {
                Console.WriteLine($"warning: {warning}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Review failed: {ex.Message}");
        }
    }

    private static async Task ShowReviewHistoryAsync(
        ControlRoomService service,
        CancellationToken cancellationToken)
    {
        Console.Write("FeedbackId: ");
        var feedbackId = Console.ReadLine() ?? string.Empty;
        var reviews = await QueryReviewsAsync(service, feedbackId, cancellationToken).ConfigureAwait(false);
        if (reviews.Count == 0)
        {
            Console.WriteLine("No review history.");
            return;
        }

        foreach (var item in reviews)
        {
            Console.WriteLine($"{item.ReviewedAt:O} {item.ReviewStatus} reviewer={item.Reviewer} trainingUse={item.TrainingUse} redaction={item.RedactionChecked}");
            if (!string.IsNullOrWhiteSpace(item.ReviewReason))
            {
                Console.WriteLine($"  reason: {item.ReviewReason}");
            }
        }
    }

    private static async Task<LearningFeedbackEvent?> FindFeedbackAsync(
        ControlRoomService service,
        string feedbackId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(feedbackId))
        {
            return null;
        }

        var query = new LearningFeedbackEventQuery
        {
            WorkspaceId = service.State.WorkspaceId,
            CollectionId = service.State.CollectionId,
            Limit = int.MaxValue
        };
        var rows = service.State.IsServiceMode
            ? await service.State.ServiceClient!.GetLearningFeedbackAsync(query, cancellationToken).ConfigureAwait(false)
            : await service.State.LearningFeedbackStore.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(item => string.Equals(item.FeedbackId, feedbackId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryReviewsAsync(
        ControlRoomService service,
        string feedbackId,
        CancellationToken cancellationToken)
    {
        var query = new LearningFeedbackReviewQuery
        {
            FeedbackId = string.IsNullOrWhiteSpace(feedbackId) ? null : feedbackId.Trim(),
            Limit = 20
        };
        return service.State.IsServiceMode
            ? await service.State.ServiceClient!.GetLearningFeedbackReviewsAsync(query, cancellationToken).ConfigureAwait(false)
            : await service.State.LearningFeedbackReviewStore.QueryAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private static void PrintFeedbackDetail(LearningFeedbackEvent feedback)
    {
        Console.WriteLine("Feedback detail");
        Console.WriteLine($"- FeedbackId       : {feedback.FeedbackId}");
        Console.WriteLine($"- CapabilityId     : {feedback.CapabilityId}");
        Console.WriteLine($"- TargetType       : {feedback.TargetType}");
        Console.WriteLine($"- TargetId         : {feedback.TargetId}");
        Console.WriteLine($"- SourceOperationId: {feedback.SourceOperationId}");
        Console.WriteLine($"- FeedbackKind     : {feedback.FeedbackKind}");
        Console.WriteLine($"- metadataOnly     : {feedback.MetadataOnly}");
        Console.WriteLine($"- redactionMode    : {feedback.RedactionMode}");
        Console.WriteLine($"- trainingUse      : {feedback.TrainingUse}");
        Console.WriteLine($"- reason           : {feedback.Reason}");
    }
}
