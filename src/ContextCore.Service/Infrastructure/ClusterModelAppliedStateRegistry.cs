using ContextCore.Abstractions;

namespace ContextCore.Service.Infrastructure;

/// <summary>
/// 集群模型已应用状态注册表实现：只读聚合（read model）。
/// 从 <see cref="IClusterModelSlotStore"/> 读取控制面期望状态，
/// 从 <see cref="IModelNodeAppliedStateStore.ListBySlotAsync"/> 枚举各节点已应用记录，
/// 聚合为集群级收敛视图（摘要 + 节点条目）。
/// 纯计算逻辑，不执行任何写入；对存储实现无 Postgres 特化要求，任何 provider 均可。
/// </summary>
/// <remarks>
/// P0-15：当注册了 <see cref="IModelNodeMembershipStore"/> 时，集群当前节点集合 =
/// 活跃成员（租约未过期），而非 Applied State 历史行——
/// <list type="bullet">
/// <item>已下线节点记录不再永久阻止 Converged（租约过期即退出集群）；</item>
/// <item>新启动但尚未写 Applied State 的节点计入 NodeCount（未就绪 → 阻止 Rollout Ready）；</item>
/// <item>租约过期即 stale cutoff，Rollout Ready 只基于当前活跃成员。</item>
/// </list>
/// 未注册成员存储（单节点 / InMemory 部署）时保持旧语义：全部已上报记录视为节点。
/// </remarks>
public sealed class ClusterModelAppliedStateRegistry : IClusterModelAppliedStateRegistry
{
    private readonly IClusterModelSlotStore _slotStore;
    private readonly IModelNodeAppliedStateStore _appliedStateStore;
    private readonly IModelNodeMembershipStore? _membershipStore;

    public ClusterModelAppliedStateRegistry(
        IClusterModelSlotStore slotStore,
        IModelNodeAppliedStateStore appliedStateStore,
        IModelNodeMembershipStore? membershipStore = null)
    {
        _slotStore = slotStore;
        _appliedStateStore = appliedStateStore;
        _membershipStore = membershipStore;
    }

    /// <inheritdoc />
    public async ValueTask<ClusterSlotAppliedSummary> GetSlotSummaryAsync(string slotName = "primary", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);
        ct.ThrowIfCancellationRequested();

        var slot = await _slotStore.GetAsync(slotName, ct).ConfigureAwait(false);
        var nodes = await _appliedStateStore.ListBySlotAsync(slotName, ct).ConfigureAwait(false);

        // P0-15：活跃成员集合（租约未过期）。无成员存储 → 全部已上报记录视为节点（旧语义）。
        var activeMembers = _membershipStore is null
            ? null
            : await _membershipStore.GetActiveMembersAsync(ct).ConfigureAwait(false);
        // 成员与已应用记录均以 (NodeGroupId, InstanceId) 标识——同一节点组可驻留多个实例，
        // 各实例独立参与集群收敛判定（聚合键为实例而非节点组）。
        var memberKeys = activeMembers is null
            ? null
            : new HashSet<(string NodeGroupId, string InstanceId)>(activeMembers.Select(m => (m.NodeGroupId, m.InstanceId)));

        // 相关实例 = 活跃成员的已应用记录（无成员存储时 = 全部记录）。
        var relevantNodes = memberKeys is null
            ? nodes
            : nodes.Where(n => memberKeys.Contains((n.NodeGroupId, n.InstanceId))).ToArray();

        var desiredRevision = slot?.Revision ?? 0;
        var desiredStatus = slot?.DesiredStatus ?? ClusterModelSlotDesiredStatus.Inactive;
        var desiredModelId = slot?.ActiveModelArtifactId;
        var desiredHash = slot?.ContentHash;

        // NodeCount：活跃成员数（含尚未上报 Applied State 的新实例）；无成员存储时回退到已上报记录数。
        var nodeCount = activeMembers is not null ? activeMembers.Count : relevantNodes.Count;

        var (converged, nodesBehind) = memberKeys is null
            ? ComputeConvergenceLegacy(relevantNodes, desiredRevision)
            : ComputeConvergenceByMembership(activeMembers!, relevantNodes, desiredRevision);

        var driftedCount = ComputeDriftedNodeCount(relevantNodes, desiredRevision, desiredModelId, desiredHash);

        return new ClusterSlotAppliedSummary
        {
            SlotName = slotName,
            DesiredRevision = desiredRevision,
            DesiredStatus = desiredStatus,
            DesiredModelArtifactId = desiredModelId,
            DesiredContentHash = desiredHash,
            NodeCount = nodeCount,
            MinAppliedRevision = relevantNodes.Count == 0 ? 0 : relevantNodes.Min(n => n.AppliedRevision),
            MaxAppliedRevision = relevantNodes.Count == 0 ? 0 : relevantNodes.Max(n => n.AppliedRevision),
            Converged = converged,
            NodesBehind = nodesBehind,
            ContentHashConflictCount = ComputeContentHashConflictCount(relevantNodes),
            DriftedNodeCount = driftedCount,
            // 上线就绪：至少一个节点、全部收敛、无漂移/隔离节点（基于当前活跃成员）。
            IsRolloutReady = converged && driftedCount == 0,
            LatestAppliedAtUtc = relevantNodes.Count == 0 ? null : relevantNodes.Max(n => n.AppliedAt),
            ComputedAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// 展开视图保留全部 Applied State 记录（含已下线节点的历史行），供运维审计"谁曾经/现在
    /// 在集群中、应用过什么"；集群当前规模与收敛判定以 <see cref="GetSlotSummaryAsync"/>
    /// 的活跃成员口径为准（P0-15）。
    /// </remarks>
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
            .OrderBy(n => n.NodeGroupId, StringComparer.Ordinal)
            .ThenBy(n => n.InstanceId, StringComparer.Ordinal)
            .Select(n => new ClusterNodeAppliedEntry
            {
                NodeGroupId = n.NodeGroupId,
                InstanceId = n.InstanceId,
                AppliedRevision = n.AppliedRevision,
                ModelArtifactId = n.ModelArtifactId,
                ContentHash = n.ContentHash,
                AppliedAt = n.AppliedAt,
                IsCurrent = n.AppliedRevision == desiredRevision
                    && string.Equals(n.ModelArtifactId, desiredModelId, StringComparison.Ordinal)
                    && string.Equals(n.ContentHash, desiredHash, StringComparison.Ordinal),
                IsBehind = n.AppliedRevision < desiredRevision,
                Isolated = n.Isolated,
                IsolationReason = n.IsolationReason
            })
            .ToArray();
        return entries;
    }

    /// <summary>无成员存储（旧语义）：全部已上报记录视为节点。</summary>
    private static (bool Converged, int NodesBehind) ComputeConvergenceLegacy(
        IReadOnlyList<ModelNodeAppliedState> nodes,
        long desiredRevision)
        => (
            nodes.Count > 0 && nodes.All(n => n.AppliedRevision == desiredRevision),
            nodes.Count(n => n.AppliedRevision < desiredRevision));

    /// <summary>
    /// 基于活跃成员的收敛判定（P0-15）：
    /// 每个活跃成员都必须已有 Applied State 记录且 AppliedRevision == 期望（尚未上报应用的
    /// 新实例视为未就绪 → 不收敛）；NodesBehind 含"无记录"与"记录落后"两类成员。
    /// </summary>
    private static (bool Converged, int NodesBehind) ComputeConvergenceByMembership(
        IReadOnlyList<ModelNodeMembership> members,
        IReadOnlyList<ModelNodeAppliedState> nodes,
        long desiredRevision)
    {
        var appliedByKey = nodes.ToDictionary(
            n => (n.NodeGroupId, n.InstanceId),
            n => n);
        var converged = members.Count > 0 && members.All(m =>
            appliedByKey.TryGetValue((m.NodeGroupId, m.InstanceId), out var state)
            && state.AppliedRevision == desiredRevision);
        var nodesBehind = members.Count(m =>
            !appliedByKey.TryGetValue((m.NodeGroupId, m.InstanceId), out var state)
            || state.AppliedRevision < desiredRevision);
        return (converged, nodesBehind);
    }

    /// <summary>
    /// 漂移节点数：已隔离节点 + 已上报 Revision 与期望一致但模型内容与期望不一致的节点。
    /// 漂移意味着节点实际加载的模型内容与集群期望不一致（Slot=A、Engine=B 错位）。
    /// </summary>
    private static int ComputeDriftedNodeCount(
        IReadOnlyList<ModelNodeAppliedState> nodes,
        long desiredRevision,
        string? desiredModelId,
        string? desiredHash)
    {
        var drifted = 0;
        foreach (var node in nodes)
        {
            if (node.Isolated)
            {
                drifted++;
                continue;
            }

            // 内容不一致仅在同 Revision 下才有意义（落后节点由 NodesBehind 单独统计；
            // 期望 Inactive / 无期望内容时无漂移可比）。
            if (node.AppliedRevision == desiredRevision
                && !string.IsNullOrEmpty(desiredModelId)
                && !string.IsNullOrEmpty(desiredHash)
                && (!string.Equals(node.ModelArtifactId, desiredModelId, StringComparison.Ordinal)
                    || !string.Equals(node.ContentHash, desiredHash, StringComparison.Ordinal)))
            {
                drifted++;
            }
        }

        return drifted;
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
