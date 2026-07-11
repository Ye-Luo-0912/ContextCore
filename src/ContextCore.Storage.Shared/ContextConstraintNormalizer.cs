using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Shared;

/// <summary>规范化与克隆 <see cref="ContextConstraint"/> 的工具方法。</summary>
public static class ContextConstraintNormalizer
{
    /// <summary>规范化 <see cref="ContextConstraint"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static ContextConstraint Normalize(ContextConstraint constraint)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextConstraint
        {
            Id = string.IsNullOrWhiteSpace(constraint.Id) ? Guid.NewGuid().ToString("N") : constraint.Id,
            WorkspaceId = constraint.WorkspaceId,
            CollectionId = constraint.CollectionId,
            Scope = constraint.Scope,
            Level = constraint.Level,
            Content = constraint.Content,
            AppliesToRefs = [.. constraint.AppliesToRefs],
            SourceRefs = [.. constraint.SourceRefs],
            Status = constraint.Status,
            Confidence = constraint.Confidence,
            Metadata = new Dictionary<string, string>(constraint.Metadata),
            CreatedAt = constraint.CreatedAt == default ? now : constraint.CreatedAt,
            UpdatedAt = constraint.UpdatedAt == default ? now : constraint.UpdatedAt
        };
    }

    /// <summary>深克隆 <see cref="ContextConstraint"/>，可选指定新 Id；纯深拷贝，不改变时间戳。</summary>
    public static ContextConstraint Clone(ContextConstraint item, string? id = null)
    {
        return new ContextConstraint
        {
            Id = id ?? item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Scope = item.Scope,
            Level = item.Level,
            Content = item.Content,
            AppliesToRefs = item.AppliesToRefs.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Status = item.Status,
            Confidence = item.Confidence,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }
}
