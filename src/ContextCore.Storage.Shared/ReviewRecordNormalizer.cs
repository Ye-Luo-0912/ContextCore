using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Shared;

/// <summary>规范化与克隆审核记录（review record）类型的工具方法。</summary>
public static class ReviewRecordNormalizer
{
    /// <summary>规范化 <see cref="CandidateConstraintReviewRecord"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static CandidateConstraintReviewRecord Normalize(CandidateConstraintReviewRecord record)
    {
        var createdAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
        return new CandidateConstraintReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            ConstraintId = record.ConstraintId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            Action = record.Action,
            FromStatus = record.FromStatus,
            ToStatus = record.ToStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            ActivatedConstraintId = record.ActivatedConstraintId,
            SourceConstraintGapId = record.SourceConstraintGapId,
            SourceSampleId = record.SourceSampleId,
            SourceOperationId = record.SourceOperationId,
            EvidenceRefs = [.. record.EvidenceRefs],
            CreatedAt = createdAt,
            ReviewedAt = record.ReviewedAt == default ? createdAt : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. record.Warnings],
            Errors = [.. record.Errors]
        };
    }

    /// <summary>深克隆 <see cref="CandidateConstraintReviewRecord"/>。</summary>
    public static CandidateConstraintReviewRecord Clone(CandidateConstraintReviewRecord record) => Normalize(record);

    /// <summary>规范化 <see cref="CandidateMemoryReviewRecord"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static CandidateMemoryReviewRecord Normalize(CandidateMemoryReviewRecord record)
    {
        var createdAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
        return new CandidateMemoryReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            CandidateId = record.CandidateId,
            CandidateKind = record.CandidateKind,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            Action = record.Action,
            FromStatus = record.FromStatus,
            ToStatus = record.ToStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            SupersedeTargetCandidateId = record.SupersedeTargetCandidateId,
            EvidenceRefs = [.. record.EvidenceRefs],
            SourceRefs = [.. record.SourceRefs],
            CreatedAt = createdAt,
            ReviewedAt = record.ReviewedAt == default ? createdAt : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. record.Warnings],
            Errors = [.. record.Errors]
        };
    }

    /// <summary>深克隆 <see cref="CandidateMemoryReviewRecord"/>。</summary>
    public static CandidateMemoryReviewRecord Clone(CandidateMemoryReviewRecord record) => Normalize(record);

    /// <summary>规范化 <see cref="RelationReviewRecord"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static RelationReviewRecord Normalize(RelationReviewRecord record)
    {
        var createdAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
        return new RelationReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            RelationId = record.RelationId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            Action = record.Action,
            FromLifecycle = record.FromLifecycle,
            ToLifecycle = record.ToLifecycle,
            FromReviewStatus = record.FromReviewStatus,
            ToReviewStatus = record.ToReviewStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            RelationType = record.RelationType,
            SourceId = record.SourceId,
            TargetId = record.TargetId,
            EvidenceRefs = [.. record.EvidenceRefs],
            SourceRefs = [.. record.SourceRefs],
            CreatedAt = createdAt,
            ReviewedAt = record.ReviewedAt == default ? createdAt : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. record.Warnings],
            Errors = [.. record.Errors]
        };
    }

    /// <summary>深克隆 <see cref="RelationReviewRecord"/>。</summary>
    public static RelationReviewRecord Clone(RelationReviewRecord record) => Normalize(record);

    /// <summary>规范化 <see cref="ConstraintGapReviewRecord"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static ConstraintGapReviewRecord Normalize(ConstraintGapReviewRecord record)
    {
        var createdAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
        return new ConstraintGapReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            GapId = record.GapId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            SessionId = record.SessionId,
            Action = record.Action,
            FromStatus = string.IsNullOrWhiteSpace(record.FromStatus) ? ConstraintGapStatus.Pending : record.FromStatus,
            ToStatus = string.IsNullOrWhiteSpace(record.ToStatus) ? ConstraintGapStatus.Pending : record.ToStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            CreatedConstraintId = record.CreatedConstraintId,
            TargetItemKind = record.TargetItemKind,
            TargetLayer = record.TargetLayer,
            SourceSampleId = record.SourceSampleId,
            SourceOperationId = record.SourceOperationId,
            ExpectedConstraintText = record.ExpectedConstraintText,
            EvidenceRefs = [.. record.EvidenceRefs],
            CreatedAt = createdAt,
            ReviewedAt = record.ReviewedAt == default ? createdAt : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. record.Warnings],
            Errors = [.. record.Errors]
        };
    }

    /// <summary>深克隆 <see cref="ConstraintGapReviewRecord"/>。</summary>
    public static ConstraintGapReviewRecord Clone(ConstraintGapReviewRecord record) => Normalize(record);

    /// <summary>规范化 <see cref="StableReviewRecord"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static StableReviewRecord Normalize(StableReviewRecord item)
    {
        var createdAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt;
        return new StableReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            StableReviewCandidateId = item.StableReviewCandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            StableTargetItemId = item.StableTargetItemId,
            StableTargetItemKind = item.StableTargetItemKind,
            TargetLayer = item.TargetLayer,
            SourcePromotionCandidateId = item.SourcePromotionCandidateId,
            SourceTargetItemId = item.SourceTargetItemId,
            SourceLearningCaseId = item.SourceLearningCaseId,
            EvidenceRefs = [.. item.EvidenceRefs],
            ValidationStatus = string.IsNullOrWhiteSpace(item.ValidationStatus)
                ? StableReviewValidationStatuses.ReadyForReview
                : item.ValidationStatus,
            RiskFlags = [.. item.RiskFlags],
            CreatedAt = createdAt,
            ReviewedAt = item.ReviewedAt == default ? createdAt : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }

    /// <summary>深克隆 <see cref="StableReviewRecord"/>。</summary>
    public static StableReviewRecord Clone(StableReviewRecord item) => Normalize(item);

    /// <summary>规范化 <see cref="StableLifecycleReviewRecord"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static StableLifecycleReviewRecord Normalize(StableLifecycleReviewRecord record)
    {
        var createdAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
        return new StableLifecycleReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            StableItemId = record.StableItemId,
            StableKind = record.StableKind,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            Action = record.Action,
            FromStatus = record.FromStatus,
            ToStatus = record.ToStatus,
            FromLifecycle = record.FromLifecycle,
            ToLifecycle = record.ToLifecycle,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            ReplacementItemId = record.ReplacementItemId,
            EvidenceRefs = [.. record.EvidenceRefs],
            SourceRefs = [.. record.SourceRefs],
            CreatedAt = createdAt,
            ReviewedAt = record.ReviewedAt == default ? createdAt : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. record.Warnings],
            Errors = [.. record.Errors]
        };
    }

    /// <summary>深克隆 <see cref="StableLifecycleReviewRecord"/>。</summary>
    public static StableLifecycleReviewRecord Clone(StableLifecycleReviewRecord record) => Normalize(record);

    /// <summary>规范化 <see cref="PromotionCandidateReviewRecord"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static PromotionCandidateReviewRecord Normalize(PromotionCandidateReviewRecord item)
    {
        var createdAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt;
        return new PromotionCandidateReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            CandidateId = item.CandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            TargetItemId = item.TargetItemId,
            TargetItemKind = item.TargetItemKind,
            TargetLayer = item.TargetLayer,
            EvidenceRefs = [.. item.EvidenceRefs],
            CreatedAt = createdAt,
            ReviewedAt = item.ReviewedAt == default ? createdAt : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }

    /// <summary>深克隆 <see cref="PromotionCandidateReviewRecord"/>。</summary>
    public static PromotionCandidateReviewRecord Clone(PromotionCandidateReviewRecord item) => Normalize(item);

    /// <summary>规范化 <see cref="VectorLifecycleMetadataReviewRecord"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static VectorLifecycleMetadataReviewRecord Normalize(VectorLifecycleMetadataReviewRecord record)
        => new()
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            CandidateId = record.CandidateId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            MustHitItemId = record.MustHitItemId,
            Decision = record.Decision,
            ResultStatus = record.ResultStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            ProposedLifecycle = record.ProposedLifecycle,
            ProposedReviewStatus = record.ProposedReviewStatus,
            ProposedTargetSection = record.ProposedTargetSection,
            EvidenceRefs = [.. record.EvidenceRefs],
            SourceRefs = [.. record.SourceRefs],
            SidecarWritten = record.SidecarWritten,
            UnsafeApprovalBlocked = record.UnsafeApprovalBlocked,
            BlockedReason = record.BlockedReason,
            ReviewedAt = record.ReviewedAt == default ? DateTimeOffset.UtcNow : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase)
        };

    /// <summary>深克隆 <see cref="VectorLifecycleMetadataReviewRecord"/>。</summary>
    public static VectorLifecycleMetadataReviewRecord Clone(VectorLifecycleMetadataReviewRecord record)
        => Normalize(record);
}
