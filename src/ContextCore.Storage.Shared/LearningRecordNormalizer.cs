using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Shared;

/// <summary>规范化与克隆学习记录（learning record）类型的工具方法。</summary>
public static class LearningRecordNormalizer
{
    /// <summary>规范化 <see cref="ContextLearningRecord"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static ContextLearningRecord Normalize(ContextLearningRecord record)
    {
        return new ContextLearningRecord
        {
            RecordId = string.IsNullOrWhiteSpace(record.RecordId) ? Guid.NewGuid().ToString("N") : record.RecordId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            SessionId = record.SessionId,
            SourceKind = record.SourceKind,
            SourceId = record.SourceId,
            CandidateId = record.CandidateId,
            ReviewId = record.ReviewId,
            EventKind = record.EventKind,
            Signal = record.Signal,
            FailureType = record.FailureType,
            Reason = record.Reason,
            Confidence = record.Confidence,
            Importance = record.Importance,
            EvidenceRefs = [.. record.EvidenceRefs],
            CreatedAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>深克隆 <see cref="ContextLearningRecord"/>。</summary>
    public static ContextLearningRecord Clone(ContextLearningRecord record) => Normalize(record);

    /// <summary>规范化 <see cref="PromotionFeedbackSignal"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static PromotionFeedbackSignal Normalize(PromotionFeedbackSignal feedback)
    {
        return new PromotionFeedbackSignal
        {
            FeedbackId = string.IsNullOrWhiteSpace(feedback.FeedbackId) ? Guid.NewGuid().ToString("N") : feedback.FeedbackId,
            CandidateId = feedback.CandidateId,
            WorkspaceId = feedback.WorkspaceId,
            CollectionId = feedback.CollectionId,
            SessionId = feedback.SessionId,
            Action = feedback.Action,
            Reviewer = feedback.Reviewer,
            Reason = feedback.Reason,
            SourceWorkingItemId = feedback.SourceWorkingItemId,
            CreatedTargetItemId = feedback.CreatedTargetItemId,
            SuggestedTargetLayer = feedback.SuggestedTargetLayer,
            ActualTargetLayer = feedback.ActualTargetLayer,
            Confidence = feedback.Confidence,
            Importance = feedback.Importance,
            EvidenceRefs = [.. feedback.EvidenceRefs],
            CreatedAt = feedback.CreatedAt == default ? DateTimeOffset.UtcNow : feedback.CreatedAt,
            Metadata = new Dictionary<string, string>(feedback.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>深克隆 <see cref="PromotionFeedbackSignal"/>。</summary>
    public static PromotionFeedbackSignal Clone(PromotionFeedbackSignal feedback) => Normalize(feedback);

    /// <summary>规范化 <see cref="ContextLearningCase"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static ContextLearningCase Normalize(ContextLearningCase learningCase)
    {
        return new ContextLearningCase
        {
            CaseId = string.IsNullOrWhiteSpace(learningCase.CaseId) ? Guid.NewGuid().ToString("N") : learningCase.CaseId,
            SourceType = learningCase.SourceType,
            WorkspaceId = learningCase.WorkspaceId,
            CollectionId = learningCase.CollectionId,
            SessionId = learningCase.SessionId,
            SourceRecordId = learningCase.SourceRecordId,
            SourceKind = learningCase.SourceKind,
            SourceId = learningCase.SourceId,
            CaseKind = learningCase.CaseKind,
            Title = learningCase.Title,
            Summary = learningCase.Summary,
            InputSummary = learningCase.InputSummary,
            ExpectedBehavior = learningCase.ExpectedBehavior,
            Signal = learningCase.Signal,
            FailureType = learningCase.FailureType,
            CorrectionReason = learningCase.CorrectionReason,
            Status = learningCase.Status,
            EvidenceRefs = [.. learningCase.EvidenceRefs],
            PositiveRefs = [.. learningCase.PositiveRefs],
            NegativeRefs = [.. learningCase.NegativeRefs],
            CreatedAt = learningCase.CreatedAt == default ? DateTimeOffset.UtcNow : learningCase.CreatedAt,
            Metadata = new Dictionary<string, string>(learningCase.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>深克隆 <see cref="ContextLearningCase"/>。</summary>
    public static ContextLearningCase Clone(ContextLearningCase learningCase) => Normalize(learningCase);
}
