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
    public static string RenderMemoryDetail(ContextMemoryItem item)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Memory Detail");
        builder.AppendLine($"Id         : {item.Id}");
        builder.AppendLine($"Layer      : {item.Layer}");
        builder.AppendLine($"Status     : {item.Status}");
        builder.AppendLine($"Type       : {item.Type}");
        builder.AppendLine($"Tags       : {string.Join(',', item.Tags)}");
        builder.AppendLine($"Refs       : {string.Join(',', item.RelationRefs)}");
        builder.AppendLine($"SourceRefs : {string.Join(',', item.SourceRefs)}");
        builder.AppendLine($"Importance : {item.Importance:0.00}");
        builder.AppendLine($"UpdatedAt  : {item.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Content    : {item.Content}");
        return builder.ToString();
    }

    public static string RenderCandidateMemory(ServiceCandidateMemorySnapshot snapshot)
    {
        var view = snapshot.Snapshot;
        var diagnostics = snapshot.Diagnostics;
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Candidate Memory");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Workspace  : {view.WorkspaceId}");
        builder.AppendLine($"Collection : {view.CollectionId ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Snapshot");
        builder.AppendLine($"- CandidateMemoryCount        : {view.CandidateMemoryCount}");
        builder.AppendLine($"- CandidateConstraintCount    : {view.CandidateConstraintCount}");
        builder.AppendLine($"- CandidateDecisionCount      : {view.CandidateDecisionCount}");
        builder.AppendLine($"- PendingReviewCount          : {view.PendingReviewCount}");
        builder.AppendLine($"- AcceptedFromPromotionCount  : {view.AcceptedFromPromotionCount}");
        builder.AppendLine($"- ExpiredCandidateCount       : {view.ExpiredCandidateCount}");
        builder.AppendLine($"- DuplicateCandidateCount     : {view.DuplicateCandidateCount}");
        builder.AppendLine($"- ConflictCandidateCount      : {view.ConflictCandidateCount}");
        builder.AppendLine();
        builder.AppendLine("Recent Candidates");
        foreach (var candidate in view.RecentCandidates.Take(20))
        {
            builder.AppendLine($"- {candidate.Id} [{candidate.CandidateKind}/{candidate.Status}/{candidate.Lifecycle}] type={candidate.Type}");
            builder.AppendLine($"  title    : {candidate.Title}");
            builder.AppendLine($"  evidence : {(candidate.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", candidate.EvidenceRefs))}");
            builder.AppendLine($"  source   : promotion={candidate.PromotionCandidateId ?? "-"} stable={candidate.StableReviewCandidateId ?? "-"} gap={candidate.ConstraintGapId ?? "-"}");
        }

        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        builder.AppendLine($"- Total                 : {diagnostics.DiagnosticCount}");
        builder.AppendLine($"- Duplicate             : {diagnostics.DuplicateCandidateCount}");
        builder.AppendLine($"- Stale                 : {diagnostics.StaleCandidateCount}");
        builder.AppendLine($"- WithoutEvidence       : {diagnostics.CandidateWithoutEvidenceCount}");
        builder.AppendLine($"- RejectedSource        : {diagnostics.CandidateWithRejectedSourceCount}");
        builder.AppendLine($"- StableConflict        : {diagnostics.StableConflictCount}");
        builder.AppendLine($"- Superseded            : {diagnostics.SupersededCandidateCount}");
        foreach (var item in diagnostics.Diagnostics.Take(20))
        {
            builder.AppendLine($"  - {item.CandidateId} [{item.DiagnosticType}/{item.Severity}] {item.Reason}");
            builder.AppendLine($"    suggested: {item.SuggestedAction}");
            if (item.RelatedCandidateIds.Count > 0)
            {
                builder.AppendLine($"    related: {string.Join(", ", item.RelatedCandidateIds)}");
            }
        }

        if (view.Warnings.Count > 0)
        {
            builder.AppendLine();
            AppendStringSection(builder, "Warnings", view.Warnings);
        }

        return builder.ToString();
    }

    public static string RenderCandidateMemoryDetail(CandidateMemoryRecord candidate)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Candidate Memory Detail");
        builder.AppendLine($"Id          : {candidate.Id}");
        builder.AppendLine($"Kind        : {candidate.CandidateKind}");
        builder.AppendLine($"Type        : {candidate.Type}");
        builder.AppendLine($"Status      : {candidate.Status}");
        builder.AppendLine($"Lifecycle   : {candidate.Lifecycle}");
        builder.AppendLine($"Importance  : {candidate.Importance:0.00}");
        builder.AppendLine($"Confidence  : {candidate.Confidence:0.00}");
        builder.AppendLine($"PromotionId : {candidate.PromotionCandidateId ?? "-"}");
        builder.AppendLine($"StableId    : {candidate.StableReviewCandidateId ?? "-"}");
        builder.AppendLine($"GapId       : {candidate.ConstraintGapId ?? "-"}");
        builder.AppendLine($"FeedbackId  : {candidate.FeedbackId ?? "-"}");
        builder.AppendLine($"LearningId  : {candidate.LearningCaseId ?? "-"}");
        builder.AppendLine($"Evidence    : {(candidate.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", candidate.EvidenceRefs))}");
        builder.AppendLine($"SourceRefs  : {(candidate.SourceRefs.Count == 0 ? "-" : string.Join(", ", candidate.SourceRefs))}");
        builder.AppendLine($"UpdatedAt   : {candidate.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Content     : {candidate.Content}");
        return builder.ToString();
    }

    public static string RenderCandidateMemoryExplanation(CandidateMemoryExplanation explanation)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Candidate Memory Explain");
        builder.AppendLine($"Candidate : {explanation.CandidateId}");
        builder.AppendLine($"Kind      : {explanation.Candidate.CandidateKind}");
        builder.AppendLine($"RiskFlags : {(explanation.RiskFlags.Count == 0 ? "-" : string.Join(", ", explanation.RiskFlags))}");
        builder.AppendLine($"Evidence  : {(explanation.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", explanation.EvidenceRefs))}");
        builder.AppendLine();
        builder.AppendLine("Sources");
        builder.AppendLine($"- Promotion    : {explanation.SourcePromotionCandidate?.CandidateId ?? "-"}");
        builder.AppendLine($"- StableReview : {explanation.SourceStableReviewCandidate?.StableReviewCandidateId ?? "-"}");
        builder.AppendLine($"- ConstraintGap: {explanation.SourceConstraintGap?.GapId ?? "-"}");
        builder.AppendLine($"- Feedback     : {explanation.SourceFeedbackSignal?.FeedbackId ?? "-"}");
        builder.AppendLine($"- LearningCase : {explanation.SourceLearningCase?.CaseId ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Review History");
        builder.AppendLine($"- Promotion reviews       : {explanation.PromotionReviewHistory.Count}");
        builder.AppendLine($"- Stable reviews          : {explanation.StableReviewHistory.Count}");
        builder.AppendLine($"- Constraint gap reviews  : {explanation.ConstraintGapReviewHistory.Count}");
        builder.AppendLine($"- Candidate constraint reviews: {explanation.CandidateConstraintReviewHistory.Count}");
        builder.AppendLine($"- Candidate memory reviews    : {explanation.CandidateMemoryReviewHistory.Count}");
        builder.AppendLine();
        builder.AppendLine("Provenance Chain");
        foreach (var link in explanation.ProvenanceChain)
        {
            builder.AppendLine($"- {link.SourceType}:{link.SourceId} relation={link.Relation} status={link.Status}");
        }

        if (explanation.Warnings.Count > 0)
        {
            builder.AppendLine();
            AppendStringSection(builder, "Warnings", explanation.Warnings);
        }

        return builder.ToString();
    }

    public static string RenderCandidateMemoryReviewResult(CandidateMemoryReviewResult result)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Candidate Memory Review Result");
        builder.AppendLine($"OperationId : {result.OperationId}");
        builder.AppendLine($"CandidateId : {result.CandidateId}");
        builder.AppendLine($"Kind        : {result.CandidateKind}");
        builder.AppendLine($"Action      : {result.Action}");
        builder.AppendLine($"Status      : {result.FromStatus} -> {result.ToStatus}");
        builder.AppendLine($"ReviewId    : {result.ReviewId}");
        builder.AppendLine($"Reviewer    : {result.Reviewer}");
        builder.AppendLine($"Reason      : {result.Reason}");
        builder.AppendLine($"ReviewedAt  : {result.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Supersedes  : {result.SupersedeTargetCandidateId ?? "-"}");
        AppendStringSection(builder, "Warnings", result.Warnings);

        AppendStringSection(builder, "Errors", result.Errors);

        return builder.ToString();
    }

    public static string RenderCandidateMemoryReviews(IReadOnlyList<CandidateMemoryReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Candidate Memory Review History");
        builder.AppendLine($"Count: {reviews.Count}");
        foreach (var review in reviews.Take(50))
        {
            builder.AppendLine($"- {review.ReviewId} {review.Action} {review.FromStatus}->{review.ToStatus}");
            builder.AppendLine($"  reviewer={review.Reviewer} reason={review.Reason} reviewedAt={review.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrWhiteSpace(review.SupersedeTargetCandidateId))
            {
                builder.AppendLine($"  supersedeTarget={review.SupersedeTargetCandidateId}");
            }

            if (review.Warnings.Count > 0)
            {
                builder.AppendLine($"  warnings={string.Join("; ", review.Warnings)}");
            }
        }

        return builder.ToString();
    }
}
