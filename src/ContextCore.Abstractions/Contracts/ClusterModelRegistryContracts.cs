// ===========================================================================
// Cluster Model Applied-State Registry —— 集群模型已应用状态注册表契约
// ===========================================================================
// 角色：将控制面期望状态（IClusterModelSlotStore）与各节点已应用状态
// （IModelNodeAppliedStateStore 的逐节点记录）聚合为集群级收敛视图：
//   1. 集群是否已收敛到期望 Revision（所有节点 AppliedRevision == DesiredRevision）；
//   2. 落后节点（AppliedRevision < DesiredRevision）计数；
//   3. 内容哈希冲突（不同节点加载了不同内容的模型）检测。
//
// 设计决策：
//   - 注册表是只读聚合（read model）：不写入任何持久化状态，
//     数据源即节点已应用状态表（model_node_applied_state）与控制面槽位表。
//   - 收敛以 Revision 为准（Reconciler 仅在远端 Revision > 本地时应用，
//     Revision 相等即表示节点已看到并应用该期望状态）；
//     内容哈希冲突作为独立的告警信号（同一 Revision 下不同内容 = 漂移）。
// ===========================================================================

namespace ContextCore.Abstractions;

/// <summary>
/// 集群槽位已应用状态摘要：聚合所有节点对某槽位的已应用记录，并与控制面期望状态对比，
/// 给出集群收敛视图（已收敛 / 落后节点数 / 内容哈希冲突数）。
/// </summary>
public sealed record ClusterSlotAppliedSummary
{
    /// <summary>槽位名（如 "primary"）。</summary>
    public required string SlotName { get; init; } = "primary";

    /// <summary>控制面期望 Revision（槽位未初始化时为 0）。</summary>
    public required long DesiredRevision { get; init; }

    /// <summary>控制面期望状态（槽位未初始化时为 Inactive）。</summary>
    public required ClusterModelSlotDesiredStatus DesiredStatus { get; init; }

    /// <summary>控制面期望激活的模型工件 Id（Inactive 时为 null）。</summary>
    public string? DesiredModelArtifactId { get; init; }

    /// <summary>控制面期望内容哈希（Inactive 时为 null）。</summary>
    public string? DesiredContentHash { get; init; }

    /// <summary>已上报已应用状态的节点数。</summary>
    public required int NodeCount { get; init; }

    /// <summary>所有节点中最小的已应用 Revision（无节点时为 0）。</summary>
    public required long MinAppliedRevision { get; init; }

    /// <summary>所有节点中最大的已应用 Revision（无节点时为 0）。</summary>
    public required long MaxAppliedRevision { get; init; }

    /// <summary>集群是否已收敛：至少一个节点且所有节点 AppliedRevision == DesiredRevision。</summary>
    public required bool Converged { get; init; }

    /// <summary>落后于期望 Revision 的节点数。</summary>
    public required int NodesBehind { get; init; }

    /// <summary>
    /// 内容哈希冲突数：已应用模型的节点按 ContentHash 分组，组数 - 1
    /// （0 表示无冲突或无可比数据；1 表示存在两组不同内容，即至少两个节点内容不一致）。
    /// </summary>
    public required int ContentHashConflictCount { get; init; }

    /// <summary>最近一次节点应用时间（无节点时为 null）。</summary>
    public DateTimeOffset? LatestAppliedAtUtc { get; init; }

    /// <summary>摘要计算时间。</summary>
    public required DateTimeOffset ComputedAtUtc { get; init; }
}

/// <summary>
/// 集群中单个节点对某槽位的已应用状态条目（注册表展开视图）。
/// </summary>
public sealed record ClusterNodeAppliedEntry
{
    /// <summary>节点 Id。</summary>
    public required string NodeId { get; init; }

    /// <summary>该节点已应用的集群槽位 Revision。</summary>
    public required long AppliedRevision { get; init; }

    /// <summary>该节点已应用的模型工件 Id（Inactive 期望状态下为 null）。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>该节点已应用的内容哈希（Inactive 期望状态下为 null）。</summary>
    public string? ContentHash { get; init; }

    /// <summary>应用时间。</summary>
    public required DateTimeOffset AppliedAt { get; init; }

    /// <summary>是否已与期望一致（AppliedRevision == DesiredRevision 且模型内容与期望匹配）。</summary>
    public required bool IsCurrent { get; init; }

    /// <summary>是否落后于期望 Revision。</summary>
    public required bool IsBehind { get; init; }
}

/// <summary>
/// 集群模型已应用状态注册表：只读聚合（read model），
/// 将控制面期望状态与各节点已应用状态聚合为集群级收敛视图。
/// 不写入持久化状态；数据源为 <see cref="IClusterModelSlotStore"/> 与
/// <see cref="IModelNodeAppliedStateStore"/>，任何存储实现（InMemory / Postgres）均可。
/// </summary>
public interface IClusterModelAppliedStateRegistry
{
    /// <summary>获取某槽位的集群已应用状态摘要（含收敛 / 落后 / 内容哈希冲突分析）。</summary>
    ValueTask<ClusterSlotAppliedSummary> GetSlotSummaryAsync(string slotName = "primary", CancellationToken ct = default);

    /// <summary>列出某槽位下所有节点的已应用状态条目（按 NodeId 字典序排序）。</summary>
    ValueTask<IReadOnlyList<ClusterNodeAppliedEntry>> ListNodeStatesAsync(string slotName = "primary", CancellationToken ct = default);
}
