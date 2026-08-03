using ContextCore.Abstractions;

namespace ContextCore.Service.Infrastructure;

/// <summary>
/// 集群模型已应用状态注册表实现：只读聚合（read model）。
/// 从 <see cref="IClusterModelSlotStore"/> 读取控制面期望状态，
/// 从 <see cref="IModelNodeAppliedStateStore.ListBySlotAsync"/> 枚举各节点已应用记录，
/// 聚合为集群级收敛视图（摘要 + 节点条目）。
/// 纯计算逻辑，不执行任何写入；对存储实现无 Postgres 特化要求，任何 provider 均可。
/// </summary>
public sealed class ClusterModelAppliedStateRegistry : IClusterModelAppliedStateRegistry
{
    private readonly IClusterModelSlotStore _slotStore;
    private readonly IModelNodeAppliedStateStore _appliedStateStore;

    public ClusterModelAppliedStateRegistry(
        IClusterModelSlotStore slotStore,
        IModelNodeAppliedStateStore appliedStateStore)
    {
        _slotStore = slotStore;
        _appliedStateStore = appliedStateStore;
    }

    /// <inheritdoc />
    public async ValueTask<ClusterSlotAppliedSummary> GetSlotSummaryAsync(string slotName = "primary", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);
        ct.ThrowIfCancellationRequested();

        var slot = await _slotStore.GetAsync(slotName, ct).ConfigureAwait(false);
        var nodes = await _appliedStateStore.ListBySlotAsync(slotName, ct).ConfigureAwait(false);

        var desiredRevision = slot?.Revision ?? 0;
        var desiredStatus = slot?.DesiredStatus ?? ClusterModelSlotDesiredStatus.Inactive;

        return new ClusterSlotAppliedSummary
        {
            SlotName = slotName,
            DesiredRevision = desiredRevision,
            DesiredStatus = desiredStatus,
            DesiredModelArtifactId = slot?.ActiveModelArtifactId,
            DesiredContentHash = slot?.ContentHash,
            NodeCount = nodes.Count,
            MinAppliedRevision = nodes.Count == 0 ? 0 : nodes.Min(n => n.AppliedRevision),
            MaxAppliedRevision = nodes.Count == 0 ? 0 : nodes.Max(n => n.AppliedRevision),
            Converged = nodes.Count > 0 && nodes.All(n => n.AppliedRevision == desiredRevision),
            NodesBehind = nodes.Count(n => n.AppliedRevision < desiredRevision),
            ContentHashConflictCount = ComputeContentHashConflictCount(nodes),
            LatestAppliedAtUtc = nodes.Count == 0 ? null : nodes.Max(n => n.AppliedAt),
            ComputedAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClusterNodeAppliedEntry>> ListNodeStatesAsync(string slotName = "primary", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);
        ct.ThrowIfCancellationRequested();

        var slot = await _slotStore.GetAsync(slotName, ct).ConfigureAwait(false);
        var nodes = await _appliedStateStore.ListBySlotAsync(slotName, ct).ConfigureAwait(false);

        var desiredRevision = slot?.Revision ?? 0;
        var desiredModelId = slot?.ActiveModelArtifactId;
        var desiredHash = slot?.ContentHash;

        var entries = nodes
            .OrderBy(n => n.NodeId, StringComparer.Ordinal)
            .Select(n => new ClusterNodeAppliedEntry
            {
                NodeId = n.NodeId,
                AppliedRevision = n.AppliedRevision,
                ModelArtifactId = n.ModelArtifactId,
                ContentHash = n.ContentHash,
                AppliedAt = n.AppliedAt,
                IsCurrent = n.AppliedRevision == desiredRevision
                    && string.Equals(n.ModelArtifactId, desiredModelId, StringComparison.Ordinal)
                    && string.Equals(n.ContentHash, desiredHash, StringComparison.Ordinal),
                IsBehind = n.AppliedRevision < desiredRevision
            })
            .ToArray();
        return entries;
    }

    /// <summary>
    /// 内容哈希冲突数：对已应用模型的节点按 ContentHash 分组，组数 - 1。
    /// 0 表示无冲突或无可比数据；1 表示存在两组不同内容，即至少两个节点内容不一致。
    /// </summary>
    private static int ComputeContentHashConflictCount(IReadOnlyList<ModelNodeAppliedState> nodes)
    {
        var distinctHashes = nodes
            .Where(n => !string.IsNullOrEmpty(n.ContentHash))
            .Select(n => n.ContentHash!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Math.Max(0, distinctHashes.Length - 1);
    }
}
