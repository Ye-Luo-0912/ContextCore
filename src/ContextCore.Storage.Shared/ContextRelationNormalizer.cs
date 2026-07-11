using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Shared;

/// <summary>规范化与克隆 <see cref="ContextRelation"/> 的工具方法。</summary>
public static class ContextRelationNormalizer
{
    /// <summary>规范化 <see cref="ContextRelation"/>：补齐默认 Id 与时间戳（CreatedAt/UpdatedAt 一致补齐），并深拷贝集合字段。</summary>
    public static ContextRelation Normalize(ContextRelation relation)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextRelation
        {
            Id = string.IsNullOrWhiteSpace(relation.Id) ? Guid.NewGuid().ToString("N") : relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            RelationType = relation.RelationType,
            Weight = relation.Weight,
            Confidence = relation.Confidence,
            SourceRefs = [.. relation.SourceRefs],
            Metadata = new Dictionary<string, string>(relation.Metadata),
            CreatedAt = relation.CreatedAt == default ? now : relation.CreatedAt,
            SourceNodeKind = relation.SourceNodeKind,
            TargetNodeKind = relation.TargetNodeKind,
            Lifecycle = relation.Lifecycle,
            ReviewStatus = relation.ReviewStatus,
            UpdatedAt = relation.UpdatedAt == default ? now : relation.UpdatedAt,
            Provenance = relation.Provenance
        };
    }

    /// <summary>深克隆 <see cref="ContextRelation"/>，可选指定新 Id；纯深拷贝，不改变时间戳。</summary>
    public static ContextRelation Clone(ContextRelation relation, string? id = null)
    {
        return new ContextRelation
        {
            Id = id ?? relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            RelationType = relation.RelationType,
            Weight = relation.Weight,
            Confidence = relation.Confidence,
            SourceRefs = relation.SourceRefs.ToArray(),
            Metadata = new Dictionary<string, string>(relation.Metadata),
            CreatedAt = relation.CreatedAt,
            SourceNodeKind = relation.SourceNodeKind,
            TargetNodeKind = relation.TargetNodeKind,
            Lifecycle = relation.Lifecycle,
            ReviewStatus = relation.ReviewStatus,
            UpdatedAt = relation.UpdatedAt,
            Provenance = relation.Provenance
        };
    }
}
