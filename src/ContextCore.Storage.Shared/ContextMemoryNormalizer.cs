using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Shared;

/// <summary>规范化与克隆 <see cref="ContextMemoryItem"/> 的工具方法。</summary>
public static class ContextMemoryNormalizer
{
    /// <summary>规范化 <see cref="ContextMemoryItem"/>：补齐默认 Id、版本与时间戳，并深拷贝集合字段。</summary>
    public static ContextMemoryItem Normalize(ContextMemoryItem item)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextMemoryItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = item.Layer,
            Status = item.Status,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = item.Version <= 0 ? 1 : item.Version,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    /// <summary>深克隆 <see cref="ContextMemoryItem"/>，不改变字段值（包括时间戳）。</summary>
    public static ContextMemoryItem Clone(ContextMemoryItem item)
    {
        return new ContextMemoryItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = item.Layer,
            Status = item.Status,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = item.Version,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }
}
