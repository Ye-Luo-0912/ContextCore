using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>
/// IClusterModelSlotStore 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 与 PostgresClusterModelSlotStore 实现同一契约，让 FileSystem / InMemory provider
/// 下的 Model Control Plane API 仍可通过 CAS 更新集群模型槽位（数据在进程重启后丢失）。
/// 单行 slot 语义：同一 slot_name 仅一条记录，CAS 通过 Revision 字段实现乐观并发控制。
/// </remarks>
public sealed class InMemoryClusterModelSlotStore : IClusterModelSlotStore
{
    private readonly ConcurrentDictionary<string, ClusterModelSlot> _slots = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<ClusterModelSlot?> GetAsync(string slotName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(slotName))
        {
            return default;
        }

        _slots.TryGetValue(slotName, out var slot);
        return new ValueTask<ClusterModelSlot?>(slot);
    }

    /// <inheritdoc />
    public ValueTask<ClusterModelSlot?> TryUpdateAsync(
        string slotName,
        long expectedRevision,
        string? activeModelArtifactId,
        string? contentHash,
        string desiredStatus,
        string? updatedBy,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(slotName))
        {
            return default;
        }

        // CAS：使用 ConcurrentDictionary 的 AddOrUpdate 进行原子更新。
        // 仅当当前 Revision == expectedRevision 时才更新，否则返回 null 表示冲突。
        ClusterModelSlot? conflictSentinel = null;
        var updated = _slots.AddOrUpdate(
            slotName,
            // slot 不存在时不创建（TryUpdate 语义要求 slot 已存在）
            _ => conflictSentinel,
            (_, existing) =>
            {
                if (existing.Revision != expectedRevision)
                {
                    return conflictSentinel;
                }

                return existing with
                {
                    ActiveModelArtifactId = activeModelArtifactId,
                    ContentHash = contentHash,
                    Revision = existing.Revision + 1,
                    DesiredStatus = desiredStatus,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    UpdatedBy = updatedBy
                };
            });

        // AddOrUpdate 在 slot 不存在时返回 conflictSentinel；在 Revision 不匹配时也返回 conflictSentinel。
        // 两种情况均表示 CAS 失败，返回 null。
        var result = ReferenceEquals(updated, conflictSentinel) ? null : updated;
        return new ValueTask<ClusterModelSlot?>(result);
    }

    /// <inheritdoc />
    public ValueTask<ClusterModelSlot> GetOrCreateAsync(string slotName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(slotName))
        {
            throw new ArgumentException("slotName 不能为空。", nameof(slotName));
        }

        var slot = _slots.GetOrAdd(slotName, _ => new ClusterModelSlot
        {
            SlotName = slotName,
            ActiveModelArtifactId = null,
            ContentHash = null,
            Revision = 0,
            DesiredStatus = "Inactive",
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = null
        });

        return new ValueTask<ClusterModelSlot>(slot);
    }
}
