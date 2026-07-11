using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Shared;

/// <summary>规范化与克隆工作记忆、晋升候选项相关模型的工具方法。</summary>
public static class WorkingMemoryNormalizer
{
    /// <summary>规范化 <see cref="WorkingMemoryItem"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static WorkingMemoryItem Normalize(WorkingMemoryItem item)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkingMemoryItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    /// <summary>规范化 <see cref="WorkingMemoryActiveContext"/>：清理空白引用、去重，并补齐时间戳。</summary>
    public static WorkingMemoryActiveContext Normalize(WorkingMemoryActiveContext item)
    {
        return new WorkingMemoryActiveContext
        {
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            CurrentTaskId = string.IsNullOrWhiteSpace(item.CurrentTaskId) ? null : item.CurrentTaskId,
            Summary = item.Summary,
            MemoryRefs = item.MemoryRefs
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ContextRefs = item.ContextRefs
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            UpdatedAt = item.UpdatedAt == default ? DateTimeOffset.UtcNow : item.UpdatedAt
        };
    }

    /// <summary>规范化 <see cref="WorkingMemoryCurrentTask"/>：补齐默认 TaskId 与时间戳，标签去空白去重。</summary>
    public static WorkingMemoryCurrentTask Normalize(WorkingMemoryCurrentTask item)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkingMemoryCurrentTask
        {
            TaskId = string.IsNullOrWhiteSpace(item.TaskId) ? Guid.NewGuid().ToString("N") : item.TaskId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Title = item.Title,
            Description = item.Description,
            Status = item.Status,
            Tags = item.Tags
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    /// <summary>规范化 <see cref="PromotionCandidate"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static PromotionCandidate Normalize(PromotionCandidate item)
    {
        var now = DateTimeOffset.UtcNow;

        return new PromotionCandidate
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SourceId = item.SourceId,
            SourceKind = item.SourceKind,
            Content = item.Content,
            TargetLayer = item.TargetLayer,
            Status = item.Status,
            Decision = item.Decision,
            Category = item.Category,
            Reason = item.Reason,
            Confidence = item.Confidence,
            MatchedRules = item.MatchedRules.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Reviewer = item.Reviewer,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    /// <summary>克隆 <see cref="PromotionCandidate"/>，可选覆盖 status/reviewer/reason/updatedAt 字段；不改变其它字段值。</summary>
    public static PromotionCandidate Clone(
        PromotionCandidate item,
        PromotionCandidateStatus? status = null,
        string? reviewer = null,
        string? reason = null,
        DateTimeOffset? updatedAt = null)
    {
        return new PromotionCandidate
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SourceId = item.SourceId,
            SourceKind = item.SourceKind,
            Content = item.Content,
            TargetLayer = item.TargetLayer,
            Status = status ?? item.Status,
            Decision = item.Decision,
            Category = item.Category,
            Reason = reason ?? item.Reason,
            Confidence = item.Confidence,
            MatchedRules = item.MatchedRules.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Reviewer = reviewer ?? item.Reviewer,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = updatedAt ?? item.UpdatedAt
        };
    }

    /// <summary>深克隆 <see cref="ContextPromotionRecord"/>，不改变字段值。</summary>
    public static ContextPromotionRecord Clone(ContextPromotionRecord item)
    {
        return new ContextPromotionRecord
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SourceMemoryId = item.SourceMemoryId,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Strategy = item.Strategy,
            Reviewer = item.Reviewer,
            TargetLayer = item.TargetLayer,
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Reason = item.Reason,
            Confidence = item.Confidence,
            CreatedAt = item.CreatedAt
        };
    }

    /// <summary>深克隆 <see cref="WorkingMemoryActiveContext"/>，不改变字段值。</summary>
    public static WorkingMemoryActiveContext Clone(WorkingMemoryActiveContext item)
    {
        return new WorkingMemoryActiveContext
        {
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            CurrentTaskId = item.CurrentTaskId,
            Summary = item.Summary,
            MemoryRefs = item.MemoryRefs.ToArray(),
            ContextRefs = item.ContextRefs.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            UpdatedAt = item.UpdatedAt
        };
    }

    /// <summary>深克隆 <see cref="WorkingMemoryCurrentTask"/>，不改变字段值。</summary>
    public static WorkingMemoryCurrentTask Clone(WorkingMemoryCurrentTask item)
    {
        return new WorkingMemoryCurrentTask
        {
            TaskId = item.TaskId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Title = item.Title,
            Description = item.Description,
            Status = item.Status,
            Tags = item.Tags.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }
}
