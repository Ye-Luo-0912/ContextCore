using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Shared;

/// <summary>规范化与克隆快照/诊断记录（snapshot / diagnostics record）类型的工具方法。</summary>
public static class SnapshotRecordNormalizer
{
    /// <summary>规范化 <see cref="RelationDiagnosticsSnapshot"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static RelationDiagnosticsSnapshot Normalize(RelationDiagnosticsSnapshot snapshot)
    {
        return new RelationDiagnosticsSnapshot
        {
            DiagnosticId = string.IsNullOrWhiteSpace(snapshot.DiagnosticId) ? Guid.NewGuid().ToString("N") : snapshot.DiagnosticId,
            WorkspaceId = snapshot.WorkspaceId,
            CollectionId = snapshot.CollectionId,
            RelationId = snapshot.RelationId,
            ItemId = snapshot.ItemId,
            DiagnosticKind = snapshot.DiagnosticKind,
            Severity = snapshot.Severity,
            Message = snapshot.Message,
            CreatedAt = snapshot.CreatedAt == default ? DateTimeOffset.UtcNow : snapshot.CreatedAt,
            Metadata = new Dictionary<string, string>(snapshot.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>深克隆 <see cref="RelationDiagnosticsSnapshot"/>。</summary>
    public static RelationDiagnosticsSnapshot Clone(RelationDiagnosticsSnapshot snapshot) => Normalize(snapshot);

    /// <summary>规范化 <see cref="ContextGlobalItem"/>：补齐默认 Id 与时间戳，并深拷贝集合字段。</summary>
    public static ContextGlobalItem Normalize(ContextGlobalItem item)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextGlobalItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Scope = item.Scope,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = [.. item.Tags],
            SourceRefs = [.. item.SourceRefs],
            Importance = item.Importance,
            Version = item.Version <= 0 ? 1 : item.Version,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    /// <summary>深克隆 <see cref="ContextGlobalItem"/>。</summary>
    public static ContextGlobalItem Clone(ContextGlobalItem item) => Normalize(item);
}
