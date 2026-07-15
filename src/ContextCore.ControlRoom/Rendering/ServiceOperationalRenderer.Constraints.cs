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
    public static string RenderConstraints(ServiceConstraintsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Constraints");
        builder.AppendLine($"Count: {snapshot.Constraints.Count}");
        foreach (var item in snapshot.Constraints.Take(20))
        {
            builder.AppendLine($"- {item.Id} [{item.Level}/{item.Status}] scope={item.Scope} appliesTo={string.Join(',', item.AppliesToRefs)}");
        }
        return builder.ToString();
    }

    public static string RenderConstraintDetail(ContextConstraint item)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Constraint Detail");
        builder.AppendLine($"Id         : {item.Id}");
        builder.AppendLine($"Scope      : {item.Scope}");
        builder.AppendLine($"Type       : {item.Level}");
        builder.AppendLine($"Severity   : {item.Level}");
        builder.AppendLine($"Status     : {item.Status}");
        builder.AppendLine($"AppliesTo  : {string.Join(',', item.AppliesToRefs)}");
        builder.AppendLine($"SourceRefs : {string.Join(',', item.SourceRefs)}");
        builder.AppendLine($"Content    : {item.Content}");
        return builder.ToString();
    }

    public static string RenderConstraintGaps(ServiceConstraintGapsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Constraint Gaps");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine($"Count   : {snapshot.Gaps.Count}");
        builder.AppendLine($"Filter  : status={snapshot.Status ?? "-"} severity={snapshot.Severity ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        foreach (var gap in snapshot.Gaps.Take(20))
        {
            builder.AppendLine($"- {gap.GapId} [{gap.Status}/{gap.Severity}] sample={gap.SourceSampleId} source={gap.Source}");
            builder.AppendLine($"  expected : {gap.ExpectedConstraintText}");
            builder.AppendLine($"  suggest  : scope={gap.SuggestedConstraintScope} type={gap.SuggestedConstraintType} title={gap.SuggestedConstraintTitle}");
            builder.AppendLine($"  evidence : {string.Join(", ", gap.EvidenceRefs)}");
        }

        return builder.ToString();
    }

    public static string RenderConstraintGapDetail(ConstraintGapCandidate gap)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Constraint Gap Detail");
        builder.AppendLine($"GapId                  : {gap.GapId}");
        builder.AppendLine($"Status                 : {gap.Status}");
        builder.AppendLine($"Severity               : {gap.Severity}");
        builder.AppendLine($"Source                 : {gap.Source}");
        builder.AppendLine($"SourceSampleId         : {gap.SourceSampleId}");
        builder.AppendLine($"SourceOperationId      : {gap.SourceOperationId}");
        builder.AppendLine($"ExpectedConstraintText : {gap.ExpectedConstraintText}");
        builder.AppendLine($"MatchedConstraintIds   : {(gap.MatchedConstraintIds.Count == 0 ? "-" : string.Join(", ", gap.MatchedConstraintIds))}");
        builder.AppendLine($"SuggestedTitle         : {gap.SuggestedConstraintTitle}");
        builder.AppendLine($"SuggestedScope         : {gap.SuggestedConstraintScope}");
        builder.AppendLine($"SuggestedType          : {gap.SuggestedConstraintType}");
        builder.AppendLine($"Reason                 : {gap.Reason}");
        builder.AppendLine($"EvidenceRefs           : {string.Join(", ", gap.EvidenceRefs)}");
        if (gap.Metadata.Count > 0)
        {
            builder.AppendLine("Metadata");
            foreach (var pair in gap.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}={pair.Value}");
            }
        }

        return builder.ToString();
    }

    public static string RenderConstraintGapReviewResult(ConstraintGapReviewResult response)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Constraint Gap Review Result");
        builder.AppendLine($"OperationId         : {response.OperationId}");
        builder.AppendLine($"GapId               : {response.GapId}");
        builder.AppendLine($"Action              : {response.Action}");
        builder.AppendLine($"Status              : {response.Status}");
        builder.AppendLine($"ReviewId            : {response.ReviewId}");
        builder.AppendLine($"Reviewer            : {response.Reviewer}");
        builder.AppendLine($"Reason              : {response.Reason}");
        builder.AppendLine($"ReviewedAt          : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"CreatedConstraintId : {response.CreatedConstraintId ?? response.TargetItemId ?? "-"}");
        builder.AppendLine($"TargetKind          : {response.TargetItemKind ?? "-"}");
        builder.AppendLine($"TargetLayer         : {response.TargetLayer ?? "-"}");
        AppendStringSection(builder, "Warnings", response.Warnings);

        AppendStringSection(builder, "Errors", response.Errors);

        return builder.ToString();
    }

    public static string RenderConstraintGapReviews(IReadOnlyList<ConstraintGapReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Constraint Gap Review History");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer            : {review.Reviewer}");
            builder.AppendLine($"  reason              : {review.Reason}");
            builder.AppendLine($"  createdConstraintId : {review.CreatedConstraintId ?? "-"}");
            builder.AppendLine($"  source              : sample={review.SourceSampleId} operation={review.SourceOperationId}");
            builder.AppendLine($"  expected            : {review.ExpectedConstraintText}");
            builder.AppendLine($"  evidenceRefs        : {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt          : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }

    public static string RenderCandidateConstraints(ServiceCandidateConstraintsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Candidate Constraints");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine($"Count   : {snapshot.Constraints.Count}");
        builder.AppendLine($"Filter  : status={snapshot.Status?.ToString() ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        foreach (var item in snapshot.Constraints.Take(20))
        {
            builder.AppendLine($"- {item.Id} [{item.Level}/{item.Status}] scope={item.Scope}");
            builder.AppendLine($"  source   : gap={ReadMetadata(item, "sourceConstraintGapId")} sample={ReadMetadata(item, "sourceSampleId")}");
            builder.AppendLine($"  evidence : {ReadMetadata(item, "evidenceRefs")}");
            builder.AppendLine($"  content  : {item.Content}");
        }

        return builder.ToString();
    }

    public static string RenderCandidateConstraintDetail(ContextConstraint item)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Candidate Constraint Detail");
        builder.AppendLine($"Id         : {item.Id}");
        builder.AppendLine($"Scope      : {item.Scope}");
        builder.AppendLine($"Level      : {item.Level}");
        builder.AppendLine($"Status     : {item.Status}");
        builder.AppendLine($"Confidence : {item.Confidence:0.###}");
        builder.AppendLine($"SourceRefs : {string.Join(", ", item.SourceRefs)}");
        builder.AppendLine($"Content    : {item.Content}");
        if (item.Metadata.Count > 0)
        {
            builder.AppendLine("Metadata");
            foreach (var pair in item.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}={pair.Value}");
            }
        }

        return builder.ToString();
    }

    public static string RenderCandidateConstraintReviewResult(CandidateConstraintReviewResult response)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Candidate Constraint Review Result");
        builder.AppendLine($"OperationId           : {response.OperationId}");
        builder.AppendLine($"ConstraintId          : {response.ConstraintId}");
        builder.AppendLine($"Action                : {response.Action}");
        builder.AppendLine($"Status                : {response.Status}");
        builder.AppendLine($"ReviewId              : {response.ReviewId}");
        builder.AppendLine($"Reviewer              : {response.Reviewer}");
        builder.AppendLine($"Reason                : {response.Reason}");
        builder.AppendLine($"ReviewedAt            : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"ActivatedConstraintId : {response.ActivatedConstraintId ?? "-"}");
        builder.AppendLine($"TargetLayer           : {response.TargetLayer ?? "-"}");
        AppendStringSection(builder, "Warnings", response.Warnings);

        AppendStringSection(builder, "Errors", response.Errors);

        return builder.ToString();
    }

    public static string RenderCandidateConstraintReviews(IReadOnlyList<CandidateConstraintReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Candidate Constraint Review History");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer            : {review.Reviewer}");
            builder.AppendLine($"  reason              : {review.Reason}");
            builder.AppendLine($"  activatedConstraint : {review.ActivatedConstraintId ?? "-"}");
            builder.AppendLine($"  source              : gap={review.SourceConstraintGapId} sample={review.SourceSampleId} operation={review.SourceOperationId}");
            builder.AppendLine($"  evidenceRefs        : {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt          : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }
}
