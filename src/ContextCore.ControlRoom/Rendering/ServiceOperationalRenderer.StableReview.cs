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
    public static string RenderStableReviewCandidates(ServiceStableReviewCandidatesSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Stable Review Candidates");
        builder.AppendLine($"时间        : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务        : {snapshot.BaseUrl}");
        builder.AppendLine($"Candidates  : {snapshot.Candidates.Count}");
        builder.AppendLine($"Filters     : status={snapshot.Status ?? "-"} validation={snapshot.ValidationStatus ?? "-"} kind={snapshot.Kind ?? "-"} target={snapshot.SuggestedStableTarget ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        if (snapshot.Candidates.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var candidate in snapshot.Candidates)
        {
            builder.AppendLine($"- {candidate.StableReviewCandidateId} [{candidate.Kind}/{candidate.Status}/{candidate.ValidationStatus}]");
            builder.AppendLine($"  title        : {candidate.Title}");
            builder.AppendLine($"  stableTarget : {candidate.SuggestedStableTarget}");
            builder.AppendLine($"  source       : candidate={candidate.SourceCandidateId} target={candidate.SourceTargetItemId} learningCase={candidate.SourceLearningCaseId ?? "-"}");
            builder.AppendLine($"  confidence   : {candidate.Confidence:0.00}");
            builder.AppendLine($"  importance   : {candidate.Importance:0.00}");
            builder.AppendLine($"  riskFlags    : {(candidate.RiskFlags.Count == 0 ? "-" : string.Join(", ", candidate.RiskFlags))}");
            builder.AppendLine($"  evidenceRefs : {string.Join(", ", candidate.EvidenceRefs)}");
        }

        return builder.ToString();
    }

    public static string RenderStableReviewCandidateDetail(StableReviewCandidate candidate)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Stable Review Candidate Detail");
        builder.AppendLine($"StableReviewCandidateId : {candidate.StableReviewCandidateId}");
        builder.AppendLine($"SourceCandidateId       : {candidate.SourceCandidateId}");
        builder.AppendLine($"SourceTargetItemId      : {candidate.SourceTargetItemId}");
        builder.AppendLine($"SourceLearningCaseId    : {candidate.SourceLearningCaseId ?? "-"}");
        builder.AppendLine($"Kind                    : {candidate.Kind}");
        builder.AppendLine($"SuggestedStableTarget   : {candidate.SuggestedStableTarget}");
        builder.AppendLine($"Status                  : {candidate.Status}");
        builder.AppendLine($"ValidationStatus        : {candidate.ValidationStatus}");
        builder.AppendLine($"RiskFlags               : {(candidate.RiskFlags.Count == 0 ? "-" : string.Join(", ", candidate.RiskFlags))}");
        builder.AppendLine($"Confidence              : {candidate.Confidence:0.00}");
        builder.AppendLine($"Importance              : {candidate.Importance:0.00}");
        builder.AppendLine($"Reason                  : {candidate.Reason}");
        builder.AppendLine($"EvidenceRefs            : {string.Join(", ", candidate.EvidenceRefs)}");
        return builder.ToString();
    }

    public static string RenderStableReviewCandidateExplanation(StableReviewCandidateExplanation explanation)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Stable Review Candidate Explain");
        builder.AppendLine($"StableReviewCandidateId : {explanation.StableReviewCandidateId}");
        builder.AppendLine($"ValidationStatus        : {explanation.ValidationStatus}");
        builder.AppendLine($"RiskFlags               : {(explanation.RiskFlags.Count == 0 ? "-" : string.Join(", ", explanation.RiskFlags))}");
        builder.AppendLine($"Reason                  : {explanation.Reason}");
        builder.AppendLine("Source Promotion Candidate");
        builder.AppendLine($"- {explanation.SourceCandidate.CandidateId} [{explanation.SourceCandidate.Kind}/{explanation.SourceCandidate.Status}] target={explanation.SourceCandidate.SuggestedTargetLayer}");
        builder.AppendLine($"  title    : {explanation.SourceCandidate.Title}");
        builder.AppendLine($"  evidence : {string.Join(", ", explanation.SourceCandidate.EvidenceRefs)}");
        if (explanation.SourceLearningCase is not null)
        {
            builder.AppendLine("Source Learning Case");
            builder.AppendLine($"- {explanation.SourceLearningCase.CaseId} [{explanation.SourceLearningCase.CaseKind}/{explanation.SourceLearningCase.Status}]");
            builder.AppendLine($"  evidence : {string.Join(", ", explanation.SourceLearningCase.EvidenceRefs)}");
        }

        if (explanation.SourceMemoryTarget is not null)
        {
            builder.AppendLine("Source Target Memory");
            builder.AppendLine($"- {explanation.SourceMemoryTarget.Id} [{explanation.SourceMemoryTarget.Layer}/{explanation.SourceMemoryTarget.Status}/{explanation.SourceMemoryTarget.Type}]");
            builder.AppendLine($"  sourceRefs: {string.Join(", ", explanation.SourceMemoryTarget.SourceRefs)}");
        }

        if (explanation.SourceConstraintTarget is not null)
        {
            builder.AppendLine("Source Target Constraint");
            builder.AppendLine($"- {explanation.SourceConstraintTarget.Id} [{explanation.SourceConstraintTarget.Level}/{explanation.SourceConstraintTarget.Status}]");
            builder.AppendLine($"  sourceRefs: {string.Join(", ", explanation.SourceConstraintTarget.SourceRefs)}");
        }

        builder.AppendLine($"EvidenceRefs            : {string.Join(", ", explanation.EvidenceRefs)}");
        AppendStringSection(builder, "Warnings", explanation.Warnings);

        return builder.ToString();
    }

    public static string RenderStableReviewDecisionResult(StableReviewDecisionResult response)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Stable Review Decision Result");
        builder.AppendLine($"OperationId             : {response.OperationId}");
        builder.AppendLine($"StableReviewCandidateId : {response.StableReviewCandidateId}");
        builder.AppendLine($"Action                  : {response.Action}");
        builder.AppendLine($"Status                  : {response.Status}");
        builder.AppendLine($"ValidationStatus        : {response.ValidationStatus}");
        builder.AppendLine($"ReviewId                : {response.ReviewId}");
        builder.AppendLine($"Reviewer                : {response.Reviewer}");
        builder.AppendLine($"Reason                  : {response.Reason}");
        builder.AppendLine($"ReviewedAt              : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"StableTargetId          : {response.CreatedStableTargetItemId ?? response.CreatedTargetItemId ?? "-"}");
        builder.AppendLine($"StableTargetKind        : {response.StableTargetItemKind ?? "-"}");
        builder.AppendLine($"TargetLayer             : {response.TargetLayer ?? "-"}");
        AppendStringSection(builder, "Warnings", response.Warnings);

        AppendStringSection(builder, "Errors", response.Errors);

        return builder.ToString();
    }

    public static string RenderStableReviewCandidateReviews(IReadOnlyList<StableReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Stable Review Decision History");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer       : {review.Reviewer}");
            builder.AppendLine($"  reason         : {review.Reason}");
            builder.AppendLine($"  validation     : {review.ValidationStatus}");
            builder.AppendLine($"  riskFlags      : {(review.RiskFlags.Count == 0 ? "-" : string.Join(", ", review.RiskFlags))}");
            builder.AppendLine($"  stableTarget   : {review.StableTargetItemKind ?? "-"} {review.StableTargetItemId ?? "-"} layer={review.TargetLayer ?? "-"}");
            builder.AppendLine($"  source         : promotion={review.SourcePromotionCandidateId} target={review.SourceTargetItemId} learningCase={review.SourceLearningCaseId ?? "-"}");
            builder.AppendLine($"  evidenceRefs   : {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt     : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }
}
