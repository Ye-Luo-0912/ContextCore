using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Shared;

/// <summary>规范化与克隆短期记忆记录（short-term memory record）类型的工具方法。</summary>
public static class ShortTermMemoryRecordNormalizer
{
    /// <summary>规范化 <see cref="ShortTermRawEvent"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static ShortTermRawEvent Normalize(ShortTermRawEvent item)
    {
        return new ShortTermRawEvent
        {
            EventId = string.IsNullOrWhiteSpace(item.EventId) ? Guid.NewGuid().ToString("N") : item.EventId,
            OperationId = item.OperationId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Source = item.Source,
            EventKind = item.EventKind,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            SequenceId = item.SequenceId,
            Tags = [.. item.Tags],
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>深克隆 <see cref="ShortTermRawEvent"/>。</summary>
    public static ShortTermRawEvent Clone(ShortTermRawEvent item) => Normalize(item);

    /// <summary>规范化 <see cref="ShortTermWorkingItem"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static ShortTermWorkingItem Normalize(ShortTermWorkingItem item)
    {
        var now = DateTimeOffset.UtcNow;
        return new ShortTermWorkingItem
        {
            ItemId = string.IsNullOrWhiteSpace(item.ItemId) ? Guid.NewGuid().ToString("N") : item.ItemId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Kind = item.Kind,
            Title = item.Title,
            Summary = item.Summary,
            Status = item.Status,
            Lifecycle = item.Lifecycle,
            Importance = item.Importance,
            Tags = [.. item.Tags],
            Refs = [.. item.Refs],
            SourceRefs = [.. item.SourceRefs],
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt,
            ExpiresAt = item.ExpiresAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>深克隆 <see cref="ShortTermWorkingItem"/>。</summary>
    public static ShortTermWorkingItem Clone(ShortTermWorkingItem item) => Normalize(item);

    /// <summary>规范化 <see cref="ShortTermCompactionRun"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static ShortTermCompactionRun Normalize(ShortTermCompactionRun item)
    {
        return new ShortTermCompactionRun
        {
            RunId = string.IsNullOrWhiteSpace(item.RunId) ? Guid.NewGuid().ToString("N") : item.RunId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Trigger = item.Trigger,
            StartedAt = item.StartedAt == default ? DateTimeOffset.UtcNow : item.StartedAt,
            CompletedAt = item.CompletedAt == default ? DateTimeOffset.UtcNow : item.CompletedAt,
            DurationMs = item.DurationMs,
            CompactedRawEvents = item.CompactedRawEvents,
            CompactedWorkingItems = item.CompactedWorkingItems,
            ArchivedRawEvents = item.ArchivedRawEvents,
            ArchivedWorkingItems = item.ArchivedWorkingItems,
            RemovedDuplicates = item.RemovedDuplicates,
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }

    /// <summary>深克隆 <see cref="ShortTermCompactionRun"/>。</summary>
    public static ShortTermCompactionRun Clone(ShortTermCompactionRun item) => Normalize(item);
}
