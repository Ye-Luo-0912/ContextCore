using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

public static partial class ServiceOperationalRenderer
{
    public static string RenderProvenance(ContextProvenanceResponse provenance)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Provenance");
        builder.AppendLine($"ItemId     : {provenance.ItemId}");
        builder.AppendLine($"TargetKind : {(string.IsNullOrWhiteSpace(provenance.TargetItemKind) ? "-" : provenance.TargetItemKind)}");
        if (provenance.TargetMemoryItem is not null)
        {
            builder.AppendLine("Target Memory");
            builder.AppendLine($"- {provenance.TargetMemoryItem.Id} [{provenance.TargetMemoryItem.Layer}/{provenance.TargetMemoryItem.Status}/{provenance.TargetMemoryItem.Type}]");
            builder.AppendLine($"  sourceRefs : {string.Join(", ", provenance.TargetMemoryItem.SourceRefs)}");
        }

        if (provenance.TargetConstraint is not null)
        {
            builder.AppendLine("Target Constraint");
            builder.AppendLine($"- {provenance.TargetConstraint.Id} [{provenance.TargetConstraint.Level}/{provenance.TargetConstraint.Status}]");
            builder.AppendLine($"  sourceRefs : {string.Join(", ", provenance.TargetConstraint.SourceRefs)}");
        }

        if (provenance.StableReviewCandidate is not null)
        {
            builder.AppendLine("Stable Review Candidate");
            builder.AppendLine($"- {provenance.StableReviewCandidate.StableReviewCandidateId} [{provenance.StableReviewCandidate.Status}/{provenance.StableReviewCandidate.ValidationStatus}]");
            builder.AppendLine($"  source     : promotion={provenance.StableReviewCandidate.SourceCandidateId} target={provenance.StableReviewCandidate.SourceTargetItemId} learningCase={provenance.StableReviewCandidate.SourceLearningCaseId ?? "-"}");
        }

        if (provenance.PromotionCandidate is not null)
        {
            builder.AppendLine("Promotion Candidate");
            builder.AppendLine($"- {provenance.PromotionCandidate.CandidateId} [{provenance.PromotionCandidate.Kind}/{provenance.PromotionCandidate.Status}] target={provenance.PromotionCandidate.SuggestedTargetLayer}");
            builder.AppendLine($"  workingItem: {provenance.PromotionCandidate.SourceWorkingItemId}");
        }

        if (provenance.FeedbackSignal is not null)
        {
            builder.AppendLine("Feedback Signal");
            builder.AppendLine($"- {provenance.FeedbackSignal.FeedbackId} [{provenance.FeedbackSignal.Action}] reviewer={provenance.FeedbackSignal.Reviewer}");
        }

        if (provenance.LearningCase is not null)
        {
            builder.AppendLine("Learning Case");
            builder.AppendLine($"- {provenance.LearningCase.CaseId} [{provenance.LearningCase.CaseKind}/{provenance.LearningCase.Signal}/{provenance.LearningCase.Status}]");
        }

        if (provenance.SourceWorkingItem is not null)
        {
            builder.AppendLine("Source Working Item");
            builder.AppendLine($"- {provenance.SourceWorkingItem.ItemId} [{provenance.SourceWorkingItem.Kind}/{provenance.SourceWorkingItem.Status}] {provenance.SourceWorkingItem.Summary}");
        }

        builder.AppendLine($"EvidenceRefs : {(provenance.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", provenance.EvidenceRefs))}");
        builder.AppendLine($"StableReviews: {provenance.StableReviewHistory.Count}");
        foreach (var review in provenance.StableReviewHistory.Take(5))
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus}->{review.ToStatus} target={review.StableTargetItemId ?? "-"}");
        }

        builder.AppendLine($"PromotionReviews: {provenance.PromotionReviewHistory.Count}");
        foreach (var review in provenance.PromotionReviewHistory.Take(5))
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus}->{review.ToStatus} target={review.TargetItemId ?? "-"}");
        }

        if (provenance.Diagnostics.Count > 0)
        {
            builder.AppendLine("Diagnostics");
            foreach (var diagnostic in provenance.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic.Code} [{diagnostic.Severity}] {diagnostic.Message}");
            }
        }

        if (provenance.MissingLinks.Count > 0)
        {
            builder.AppendLine($"MissingLinks: {string.Join(", ", provenance.MissingLinks)}");
        }

        AppendStringSection(builder, "Warnings", provenance.Warnings);

        return builder.ToString();
    }
}
