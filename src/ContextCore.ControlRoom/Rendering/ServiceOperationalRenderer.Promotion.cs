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
    public static string RenderPromotionCandidates(ServicePromotionCandidatesSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Promotion Candidates");
        builder.AppendLine($"时间        : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务        : {snapshot.BaseUrl}");
        builder.AppendLine($"Candidates  : {snapshot.Candidates.Count}");
        builder.AppendLine($"Filters     : status={snapshot.Status?.ToString() ?? "-"} kind={snapshot.Kind ?? "-"} target={snapshot.SuggestedTargetLayer ?? "-"} minConf={snapshot.MinConfidence?.ToString("0.00") ?? "-"} minImp={snapshot.MinImportance?.ToString("0.00") ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        if (snapshot.Candidates.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var candidate in snapshot.Candidates)
        {
            builder.AppendLine($"- {candidate.CandidateId} [{candidate.Kind}/{candidate.Status}]");
            builder.AppendLine($"  title        : {candidate.Title}");
            builder.AppendLine($"  target       : {candidate.SuggestedTargetLayer}");
            builder.AppendLine($"  confidence   : {candidate.Confidence:0.00}");
            builder.AppendLine($"  importance   : {candidate.Importance:0.00}");
            builder.AppendLine($"  reason       : {candidate.Reason}");
            builder.AppendLine($"  evidenceRefs : {string.Join(", ", candidate.EvidenceRefs)}");
        }

        return builder.ToString();
    }

    public static string RenderPromotionCandidateDetail(ShortTermPromotionCandidate candidate)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Promotion Candidate Detail");
        builder.AppendLine($"CandidateId      : {candidate.CandidateId}");
        builder.AppendLine($"SourceWorkingId  : {candidate.SourceWorkingItemId}");
        builder.AppendLine($"Kind             : {candidate.Kind}");
        builder.AppendLine($"Title            : {candidate.Title}");
        builder.AppendLine($"TargetLayer      : {candidate.SuggestedTargetLayer}");
        builder.AppendLine($"Status           : {candidate.Status}");
        builder.AppendLine($"Confidence       : {candidate.Confidence:0.00}");
        builder.AppendLine($"Importance       : {candidate.Importance:0.00}");
        builder.AppendLine($"Reason           : {candidate.Reason}");
        builder.AppendLine($"DedupeKey        : {candidate.DedupeKey}");
        builder.AppendLine($"SourceFingerprint: {candidate.SourceFingerprint}");
        builder.AppendLine($"GeneratedBy      : {candidate.GeneratedBy}");
        builder.AppendLine($"PolicyVersion    : {candidate.PolicyVersion}");
        builder.AppendLine($"Rule             : {candidate.RuleName} ({candidate.RuleVersion})");
        builder.AppendLine($"EvidenceRefs     : {string.Join(", ", candidate.EvidenceRefs)}");
        return builder.ToString();
    }

    public static string RenderPromotionCandidateExplanation(ShortTermPromotionCandidateExplanation explanation)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Promotion Candidate Explain");
        builder.AppendLine($"CandidateId      : {explanation.CandidateId}");
        builder.AppendLine($"TargetLayer      : {explanation.SuggestedTargetLayer}");
        builder.AppendLine($"Confidence       : {explanation.Confidence:0.00}");
        builder.AppendLine($"Importance       : {explanation.Importance:0.00}");
        builder.AppendLine($"Reason           : {explanation.Reason}");
        builder.AppendLine($"Rule             : {explanation.RuleName} ({explanation.RuleVersion})");
        builder.AppendLine($"PolicyVersion    : {explanation.PolicyVersion}");
        builder.AppendLine($"GeneratedBy      : {explanation.GeneratedBy}");
        builder.AppendLine($"DedupeKey        : {explanation.DedupeKey}");
        builder.AppendLine($"SourceFingerprint: {explanation.SourceFingerprint}");
        builder.AppendLine("SourceWorkingItem");
        builder.AppendLine($"- {explanation.SourceWorkingItem.ItemId} [{explanation.SourceWorkingItem.Kind}/{explanation.SourceWorkingItem.Status}] {explanation.SourceWorkingItem.Summary}");
        builder.AppendLine($"EvidenceRefs     : {string.Join(", ", explanation.EvidenceRefs)}");
        builder.AppendLine($"SourceRawEvents  : {explanation.SourceRawEvents.Count}");
        foreach (var item in explanation.SourceRawEvents)
        {
            builder.AppendLine($"- {item.EventId} [{item.EventKind}] {item.Source}");
        }
        AppendStringSection(builder, "Warnings", explanation.Warnings);

        return builder.ToString();
    }

    public static string RenderPromotionCandidateReviewResult(PromotionCandidateReviewResult response)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Promotion Candidate Review Result");
        builder.AppendLine($"OperationId : {response.OperationId}");
        builder.AppendLine($"CandidateId : {response.CandidateId}");
        builder.AppendLine($"Action      : {response.Action}");
        builder.AppendLine($"Status      : {response.Status}");
        builder.AppendLine($"ReviewId    : {response.ReviewId}");
        builder.AppendLine($"Reviewer    : {response.Reviewer}");
        builder.AppendLine($"Reason      : {response.Reason}");
        builder.AppendLine($"ReviewedAt  : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"TargetId    : {response.CreatedTargetItemId ?? response.TargetItemId ?? "-"}");
        builder.AppendLine($"TargetKind  : {response.TargetItemKind ?? "-"}");
        builder.AppendLine($"TargetLayer : {response.TargetLayer ?? "-"}");
        AppendStringSection(builder, "Warnings", response.Warnings);

        AppendStringSection(builder, "Errors", response.Errors);

        return builder.ToString();
    }

    public static string RenderPromotionCandidateReviews(IReadOnlyList<PromotionCandidateReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Promotion Candidate Review History");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer    : {review.Reviewer}");
            builder.AppendLine($"  reason      : {review.Reason}");
            builder.AppendLine($"  target      : {review.TargetItemKind ?? "-"} {review.TargetItemId ?? "-"} layer={review.TargetLayer ?? "-"}");
            builder.AppendLine($"  evidenceRefs: {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt  : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }
}
