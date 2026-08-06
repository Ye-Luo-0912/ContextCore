using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>
/// IModelNodeAppliedStateStore 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 与 PostgresModelNodeAppliedStateStore 实现同一契约，让 FileSystem / InMemory provider
/// 下 Reconciler 的节点已应用状态记录仍可用（数据在进程重启后丢失）。
/// 每 (NodeGroupId, InstanceId, SlotName) 一行：同一节点组的多个实例各自记录已应用状态。
/// Upsert 通过 AppliedRevision CAS 防止陈旧实例回写覆盖更新记录。
/// </remarks>
public sealed class InMemoryModelNodeAppliedStateStore : IModelNodeAppliedStateStore
{
    private readonly ConcurrentDictionary<(string NodeGroupId, string InstanceId, string SlotName), ModelNodeAppliedState> _states = new();

    /// <inheritdoc />
    public ValueTask<ModelNodeAppliedState?> GetAsync(
        string nodeGroupId,
        string instanceId,
        string slotName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(nodeGroupId) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(slotName))
        {
            return default;
        }

        _states.TryGetValue((nodeGroupId, instanceId, slotName), out var state);
        return new ValueTask<ModelNodeAppliedState?>(state);
    }

    /// <inheritdoc />
    public ValueTask<ModelNodeAppliedState> UpsertAsync(ModelNodeAppliedState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(state.NodeGroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.SlotName);

        // CAS：仅当新 AppliedRevision ≥ 已存 AppliedRevision 时覆盖，防止陈旧实例回写。
        // 成功应用（记录反映本地引擎实际内容）时 Isolated=false，漂移隔离随之清除。
        var key = (state.NodeGroupId, state.InstanceId, state.SlotName);
        var updated = _states.AddOrUpdate(
            key,
            state,
            (_, existing) => state.AppliedRevision >= existing.AppliedRevision ? state : existing);
        return new ValueTask<ModelNodeAppliedState>(updated);
    }

    /// <inheritdoc />
    public ValueTask<ModelNodeAppliedState?> MarkIsolatedAsync(
        string nodeGroupId,
        string instanceId,
        string slotName,
        string reason,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeGroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = DateTimeOffset.UtcNow;
        var key = (nodeGroupId, instanceId, slotName);
        var updated = _states.AddOrUpdate(
            key,
            // 无记录时创建隔离标记（保持审计链完整：隔离事实不因缺少应用记录而丢失）。
            _ => new ModelNodeAppliedState
            {
                NodeGroupId = nodeGroupId,
                InstanceId = instanceId,
                SlotName = slotName,
                AppliedRevision = 0,
                AppliedAt = now,
                Isolated = true,
                DriftReportedAt = now,
                IsolationReason = reason
            },
            (_, existing) => existing with
            {
                Isolated = true,
                DriftReportedAt = now,
                IsolationReason = reason
            });
        return new ValueTask<ModelNodeAppliedState?>(updated);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ModelNodeAppliedState>> ListBySlotAsync(string slotName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(slotName))
        {
            return new ValueTask<IReadOnlyList<ModelNodeAppliedState>>(Array.Empty<ModelNodeAppliedState>());
        }

        var entries = _states.Values
            .Where(s => string.Equals(s.SlotName, slotName, StringComparison.Ordinal))
            .OrderBy(s => s.NodeGroupId, StringComparer.Ordinal)
            .ThenBy(s => s.InstanceId, StringComparer.Ordinal)
            .ToArray();
        return new ValueTask<IReadOnlyList<ModelNodeAppliedState>>(entries);
    }
}
