using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// <see cref="IMemoryPromotionService"/> 的基础实现，提供记忆条目的晋升、拒绝和废弃操作，
/// 并可选地将操作历史保存到 <see cref="IPromotionRecordStore"/>。
/// </summary>
public sealed class BasicMemoryPromotionService : IMemoryPromotionService
{
    private readonly IMemoryStore _memoryStore;
    private readonly IPromotionRecordStore? _promotionRecordStore;

    public BasicMemoryPromotionService(
        IMemoryStore memoryStore,
        IPromotionRecordStore? promotionRecordStore = null)
    {
        _memoryStore = memoryStore;
        _promotionRecordStore = promotionRecordStore;
    }

    /// <summary>将记忆条目晋升为 <see cref="ContextMemoryStatus.Stable"/> 并移至稳定层。</summary>
    public Task<ContextPromotionRecord> PromoteAsync(
        string workspaceId,
        string collectionId,
        string sourceMemoryId,
        string strategy,
        string? reason = null,
        double confidence = 1.0,
        CancellationToken cancellationToken = default,
        string? reviewer = null)
    {
        return ChangeStatusAsync(
            workspaceId,
            collectionId,
            sourceMemoryId,
            ContextMemoryStatus.Stable,
            strategy,
            reason,
            confidence,
            forceStableLayer: true,
            reviewer,
            cancellationToken);
    }

    /// <summary>将记忆条目标记为 <see cref="ContextMemoryStatus.Rejected"/>。</summary>
    public Task<ContextPromotionRecord> RejectAsync(
        string workspaceId,
        string collectionId,
        string sourceMemoryId,
        string strategy,
        string? reason = null,
        double confidence = 1.0,
        CancellationToken cancellationToken = default,
        string? reviewer = null)
    {
        return ChangeStatusAsync(
            workspaceId,
            collectionId,
            sourceMemoryId,
            ContextMemoryStatus.Rejected,
            strategy,
            reason,
            confidence,
            forceStableLayer: false,
            reviewer,
            cancellationToken);
    }

    /// <summary>将记忆条目标记为 <see cref="ContextMemoryStatus.Deprecated"/>。</summary>
    public Task<ContextPromotionRecord> DeprecateAsync(
        string workspaceId,
        string collectionId,
        string sourceMemoryId,
        string strategy,
        string? reason = null,
        double confidence = 1.0,
        CancellationToken cancellationToken = default,
        string? reviewer = null)
    {
        return ChangeStatusAsync(
            workspaceId,
            collectionId,
            sourceMemoryId,
            ContextMemoryStatus.Deprecated,
            strategy,
            reason,
            confidence,
            forceStableLayer: false,
            reviewer,
            cancellationToken);
    }

    private async Task<ContextPromotionRecord> ChangeStatusAsync(
        string workspaceId,
        string collectionId,
        string sourceMemoryId,
        ContextMemoryStatus toStatus,
        string strategy,
        string? reason,
        double confidence,
        bool forceStableLayer,
        string? reviewer,
        CancellationToken cancellationToken)
    {
        var existing = await _memoryStore.GetAsync(
            workspaceId,
            collectionId,
            sourceMemoryId,
            cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            throw new InvalidOperationException($"未找到记忆条目：{sourceMemoryId}");
        }

        var now = DateTimeOffset.UtcNow;
        var updated = new ContextMemoryItem
        {
            Id = existing.Id,
            WorkspaceId = existing.WorkspaceId,
            CollectionId = existing.CollectionId,
            Layer = forceStableLayer ? ContextMemoryLayer.Stable : existing.Layer,
            Status = toStatus,
            Type = existing.Type,
            Content = existing.Content,
            ContentFormat = existing.ContentFormat,
            Tags = existing.Tags.ToArray(),
            SourceRefs = existing.SourceRefs.ToArray(),
            RelationRefs = existing.RelationRefs.ToArray(),
            Importance = existing.Importance,
            Confidence = confidence,
            Version = existing.Version + 1,
            Metadata = new Dictionary<string, string>(existing.Metadata),
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now
        };

        await _memoryStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

        var record = new ContextPromotionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SourceMemoryId = sourceMemoryId,
            FromStatus = existing.Status,
            ToStatus = toStatus,
            Strategy = strategy,
            Reviewer = string.IsNullOrWhiteSpace(reviewer) ? strategy : reviewer,
            TargetLayer = updated.Layer,
            SourceRefs = existing.SourceRefs.ToArray(),
            RelationRefs = existing.RelationRefs.ToArray(),
            Reason = reason,
            Confidence = confidence,
            CreatedAt = now
        };

        if (_promotionRecordStore is not null)
        {
            await _promotionRecordStore.SavePromotionRecordAsync(record, cancellationToken)
                .ConfigureAwait(false);
        }

        return record;
    }
}
