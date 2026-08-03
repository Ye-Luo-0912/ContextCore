using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>
/// IModelNodeAppliedStateStore 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 与 PostgresModelNodeAppliedStateStore 实现同一契约，让 FileSystem / InMemory provider
/// 下 Reconciler 的节点已应用状态记录仍可用（数据在进程重启后丢失）。
/// Upsert 通过 AppliedRevision CAS 防止陈旧节点回写覆盖更新记录。
/// </remarks>
public sealed class InMemoryModelNodeAppliedStateStore : IModelNodeAppliedStateStore
{
    private readonly ConcurrentDictionary<(string NodeId, string SlotName), ModelNodeAppliedState> _states = new();

    /// <inheritdoc />
    public ValueTask<ModelNodeAppliedState?> GetAsync(string nodeId, string slotName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(slotName))
        {
            return default;
        }

        _states.TryGetValue((nodeId, slotName), out var state);
        return new ValueTask<ModelNodeAppliedState?>(state);
    }

    /// <inheritdoc />
    public ValueTask<ModelNodeAppliedState> UpsertAsync(ModelNodeAppliedState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(state.NodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.SlotName);

        // CAS：仅当新 AppliedRevision ≥ 已存 AppliedRevision 时覆盖，防止陈旧节点回写。
        var updated = _states.AddOrUpdate(
            (state.NodeId, state.SlotName),
            state,
            (_, existing) => state.AppliedRevision >= existing.AppliedRevision ? state : existing);
        return new ValueTask<ModelNodeAppliedState>(updated);
    }
}
