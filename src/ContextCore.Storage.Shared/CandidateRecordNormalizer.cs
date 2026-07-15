using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Shared;

/// <summary>规范化与克隆候选项记录（candidate record）类型的工具方法。</summary>
public static class CandidateRecordNormalizer
{
    /// <summary>规范化 <see cref="ConstraintGapCandidate"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static ConstraintGapCandidate Normalize(ConstraintGapCandidate candidate)
    {
        var expectedText = candidate.ExpectedConstraintText.Trim();
        return new ConstraintGapCandidate
        {
            GapId = string.IsNullOrWhiteSpace(candidate.GapId)
                ? BuildGapId(candidate.WorkspaceId, candidate.CollectionId, expectedText, candidate.SourceSampleId)
                : candidate.GapId.Trim(),
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            Source = candidate.Source,
            SourceSampleId = candidate.SourceSampleId,
            SourceOperationId = candidate.SourceOperationId,
            ExpectedConstraintText = expectedText,
            MatchedConstraintIds = [.. candidate.MatchedConstraintIds],
            SuggestedConstraintTitle = candidate.SuggestedConstraintTitle,
            SuggestedConstraintScope = string.IsNullOrWhiteSpace(candidate.SuggestedConstraintScope)
                ? "Collection"
                : candidate.SuggestedConstraintScope,
            SuggestedConstraintType = string.IsNullOrWhiteSpace(candidate.SuggestedConstraintType)
                ? "Hard"
                : candidate.SuggestedConstraintType,
            Severity = string.IsNullOrWhiteSpace(candidate.Severity) ? ConstraintGapSeverity.High : candidate.Severity,
            Reason = candidate.Reason,
            EvidenceRefs = [.. candidate.EvidenceRefs],
            Status = string.IsNullOrWhiteSpace(candidate.Status) ? ConstraintGapStatus.Pending : candidate.Status,
            CreatedAt = candidate.CreatedAt == default ? DateTimeOffset.UtcNow : candidate.CreatedAt,
            Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>深克隆 <see cref="ConstraintGapCandidate"/>。</summary>
    public static ConstraintGapCandidate Clone(ConstraintGapCandidate candidate) => Normalize(candidate);

    /// <summary>规范化 <see cref="ShortTermPromotionCandidate"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static ShortTermPromotionCandidate Normalize(ShortTermPromotionCandidate item)
    {
        return new ShortTermPromotionCandidate
        {
            CandidateId = string.IsNullOrWhiteSpace(item.CandidateId) ? Guid.NewGuid().ToString("N") : item.CandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            SourceWorkingItemId = item.SourceWorkingItemId,
            Kind = item.Kind,
            Title = item.Title,
            Summary = item.Summary,
            SuggestedTargetLayer = item.SuggestedTargetLayer,
            Reason = item.Reason,
            Confidence = item.Confidence,
            Importance = item.Importance,
            EvidenceRefs = [.. item.EvidenceRefs],
            Tags = [.. item.Tags],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            Status = item.Status,
            DedupeKey = item.DedupeKey,
            SourceFingerprint = item.SourceFingerprint,
            GeneratedBy = item.GeneratedBy,
            PolicyVersion = item.PolicyVersion,
            RuleName = item.RuleName,
            RuleVersion = item.RuleVersion,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>深克隆 <see cref="ShortTermPromotionCandidate"/>。</summary>
    public static ShortTermPromotionCandidate Clone(ShortTermPromotionCandidate item) => Normalize(item);

    /// <summary>规范化 <see cref="StableReviewCandidate"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static StableReviewCandidate Normalize(StableReviewCandidate candidate)
    {
        return new StableReviewCandidate
        {
            StableReviewCandidateId = string.IsNullOrWhiteSpace(candidate.StableReviewCandidateId)
                ? Guid.NewGuid().ToString("N")
                : candidate.StableReviewCandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            SourceCandidateId = candidate.SourceCandidateId,
            SourceTargetItemId = candidate.SourceTargetItemId,
            SourceLearningCaseId = candidate.SourceLearningCaseId,
            Kind = candidate.Kind,
            Title = candidate.Title,
            Summary = candidate.Summary,
            SuggestedStableTarget = candidate.SuggestedStableTarget,
            Reason = candidate.Reason,
            Confidence = candidate.Confidence,
            Importance = candidate.Importance,
            EvidenceRefs = [.. candidate.EvidenceRefs],
            RiskFlags = [.. candidate.RiskFlags],
            ValidationStatus = string.IsNullOrWhiteSpace(candidate.ValidationStatus)
                ? StableReviewValidationStatuses.ReadyForReview
                : candidate.ValidationStatus,
            CreatedAt = candidate.CreatedAt == default ? DateTimeOffset.UtcNow : candidate.CreatedAt,
            Status = string.IsNullOrWhiteSpace(candidate.Status)
                ? StableReviewCandidateStatuses.Candidate
                : candidate.Status,
            Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>深克隆 <see cref="StableReviewCandidate"/>。</summary>
    public static StableReviewCandidate Clone(StableReviewCandidate candidate) => Normalize(candidate);

    /// <summary>规范化 <see cref="VectorLifecycleMetadataReviewCandidate"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static VectorLifecycleMetadataReviewCandidate Normalize(VectorLifecycleMetadataReviewCandidate candidate)
    {
        return new VectorLifecycleMetadataReviewCandidate
        {
            CandidateId = string.IsNullOrWhiteSpace(candidate.CandidateId) ? Guid.NewGuid().ToString("N") : candidate.CandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SourceSampleId = candidate.SourceSampleId,
            SourceEvalSet = candidate.SourceEvalSet,
            MustHitItemId = candidate.MustHitItemId,
            ItemKind = candidate.ItemKind,
            Layer = candidate.Layer,
            CurrentLifecycle = candidate.CurrentLifecycle,
            CurrentReviewStatus = candidate.CurrentReviewStatus,
            CurrentTargetSection = candidate.CurrentTargetSection,
            ProposedLifecycle = candidate.ProposedLifecycle,
            ProposedReviewStatus = candidate.ProposedReviewStatus,
            ProposedTargetSection = candidate.ProposedTargetSection,
            RepairReason = candidate.RepairReason,
            EvidenceRefs = [.. candidate.EvidenceRefs],
            SourceRefs = [.. candidate.SourceRefs],
            ProvenanceAvailable = candidate.ProvenanceAvailable,
            RelationEvidenceAvailable = candidate.RelationEvidenceAvailable,
            ReviewEvidenceAvailable = candidate.ReviewEvidenceAvailable,
            RiskIfApproved = [.. candidate.RiskIfApproved],
            RiskIfRejected = [.. candidate.RiskIfRejected],
            RequiresHumanReview = candidate.RequiresHumanReview,
            Status = string.IsNullOrWhiteSpace(candidate.Status)
                ? VectorLifecycleMetadataReviewCandidateStatuses.PendingReview
                : candidate.Status,
            CreatedAt = candidate.CreatedAt == default ? DateTimeOffset.UtcNow : candidate.CreatedAt,
            Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>深克隆 <see cref="VectorLifecycleMetadataReviewCandidate"/>。</summary>
    public static VectorLifecycleMetadataReviewCandidate Clone(VectorLifecycleMetadataReviewCandidate candidate)
        => Normalize(candidate);

    /// <summary>规范化 <see cref="VectorLifecycleSidecarMetadataEntry"/>：补齐默认时间戳，并深拷贝集合字段。</summary>
    public static VectorLifecycleSidecarMetadataEntry Normalize(VectorLifecycleSidecarMetadataEntry entry)
        => new()
        {
            ItemId = entry.ItemId,
            WorkspaceId = entry.WorkspaceId,
            CollectionId = entry.CollectionId,
            LifecycleOverride = entry.LifecycleOverride,
            ReviewStatusOverride = entry.ReviewStatusOverride,
            TargetSectionOverride = entry.TargetSectionOverride,
            SourceReviewId = entry.SourceReviewId,
            SourceCandidateId = entry.SourceCandidateId,
            Reviewer = entry.Reviewer,
            Reason = entry.Reason,
            EvidenceRefs = [.. entry.EvidenceRefs],
            SourceRefs = [.. entry.SourceRefs],
            CreatedAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt,
            PolicyVersion = string.IsNullOrWhiteSpace(entry.PolicyVersion)
                ? "vector-lifecycle-sidecar/v1"
                : entry.PolicyVersion,
            Metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase)
        };

    /// <summary>深克隆 <see cref="VectorLifecycleSidecarMetadataEntry"/>。</summary>
    public static VectorLifecycleSidecarMetadataEntry Clone(VectorLifecycleSidecarMetadataEntry entry)
        => Normalize(entry);

    private static string BuildGapId(
        string workspaceId,
        string collectionId,
        string expectedConstraintText,
        string sourceSampleId)
    {
        var key = string.Join('\u001f', workspaceId, collectionId, NormalizeText(expectedConstraintText), sourceSampleId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"constraint-gap-{Convert.ToHexString(hash)[..20].ToLowerInvariant()}";
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
