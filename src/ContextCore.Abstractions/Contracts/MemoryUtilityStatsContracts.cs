using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// Memory Utility Stats 聚合契约
//
// 对齐用户规格中的 Memory Utility Ledger：
//   - Recall count：被检索命中次数
//   - Selected count：被选入 Package 次数
//   - Useful feedback count：产生有效反馈次数（R8 Learning Loop）
//   - Correction count：被纠正次数（R8 Learning Loop）
//   - Conflict count：参与冲突集合次数（R21-2 ConflictSet）
//   - Token cost：累计 token 消耗（selected + dropped）
//   - Unique anchor contribution：唯一锚点贡献次数（无其他 Expert 替代）
//   - Last useful time：最近一次产生有效贡献的时间戳
//
// 设计原则：
//   - MemoryUtilityStats 是 per-Candidate per-workspace-collection 聚合统计，
//     与 UtilityLedgerEntry（per-Decision per-Expert 快照）正交。
//   - StatsStore 公共 API 是 read-only（QueryAsync / GetAsync / GetStatsForCandidateAsync）；
//     写入由 internal AppendSnapshot 方法暴露，仅供 MemoryUtilityStatsMaterializer 调用。
//   - Stats 由 UtilityLedgerEntry 异步聚合产生（materializer 在物化 ledger 时同步更新 stats）。
//   - 模型可基于 stats 建议 promotion/demotion/merge/archive，
//     但正式写入仍经过规则与审查边界（IMemoryDecayEvaluator + 人工 review）。
// ===========================================================================

/// <summary>
/// 单个 item 的 utility 聚合统计。
/// </summary>
/// <remarks>
/// 字段对齐用户规格：
///   RecallCount / SelectedCount / UsefulFeedbackCount / CorrectionCount /
///   ConflictCount / TokenCost / UniqueAnchorContribution / LastUsefulTime。
/// </remarks>
public sealed record MemoryUtilityStats
{
    /// <summary>item ID（必填）。</summary>
    public required string SourceItemId { get; init; }

    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（必填）。</summary>
    public required string CollectionId { get; init; }

    /// <summary>item 类型（"context" / "memory" / "constraint"；必填）。</summary>
    public required string ItemType { get; init; }

    /// <summary>被检索命中次数（candidate 出现在 retrieval result 中）。</summary>
    public int RecallCount { get; init; }

    /// <summary>被选入 Package 次数（candidate 出现在 selected envelopes 中）。</summary>
    public int SelectedCount { get; init; }

    /// <summary>被 drop 次数（candidate 出现在 dropped envelopes 中）。</summary>
    public int DroppedCount { get; init; }

    /// <summary>产生有效反馈次数（来自 R8 Learning Loop 的 PositiveLabels）。</summary>
    public int UsefulFeedbackCount { get; init; }

    /// <summary>被纠正次数（来自 R8 Learning Loop 的 NegativeLabels）。</summary>
    public int CorrectionCount { get; init; }

    /// <summary>参与冲突集合次数（来自 R21-2 ConflictSet）。</summary>
    public int ConflictCount { get; init; }

    /// <summary>累计 token 消耗（所有被选入 Package 的 token 总和）。</summary>
    public int TokenCost { get; init; }

    /// <summary>唯一锚点贡献次数（无其他 Expert 替代的贡献次数）。</summary>
    public int UniqueAnchorContribution { get; init; }

    /// <summary>最近一次产生有效贡献的时间戳（可空 = 从未产生有效贡献）。</summary>
    public DateTimeOffset? LastUsefulTime { get; init; }

    /// <summary>首次被检索命中的时间戳。</summary>
    public DateTimeOffset? FirstRecallTime { get; init; }

    /// <summary>最近一次被检索命中的时间戳。</summary>
    public DateTimeOffset? LastRecallTime { get; init; }

    /// <summary>最近一次更新时间戳（stats 聚合更新时间）。</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>stats 元数据（自定义键值对，用于 trace 与审计）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    // ----- 计算属性 -----

    /// <summary>选择率（SelectedCount / RecallCount；RecallCount=0 时为 0）。</summary>
    public double SelectionRate => RecallCount > 0 ? (double)SelectedCount / RecallCount : 0.0;

    /// <summary>有效反馈率（UsefulFeedbackCount / SelectedCount；SelectedCount=0 时为 0）。</summary>
    public double UsefulRate => SelectedCount > 0 ? (double)UsefulFeedbackCount / SelectedCount : 0.0;

    /// <summary>纠正率（CorrectionCount / SelectedCount；SelectedCount=0 时为 0）。</summary>
    public double CorrectionRate => SelectedCount > 0 ? (double)CorrectionCount / SelectedCount : 0.0;

    /// <summary>平均 token 成本（TokenCost / SelectedCount；SelectedCount=0 时为 0）。</summary>
    public double AverageTokenCost => SelectedCount > 0 ? (double)TokenCost / SelectedCount : 0.0;
}

/// <summary>
/// MemoryUtilityStats 查询条件。
/// </summary>
public sealed record MemoryUtilityStatsQuery
{
    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（可空 = 跨集合）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>按 SourceItemId 过滤（可空 = 不限制）。</summary>
    public string? SourceItemId { get; init; }

    /// <summary>按 ItemType 过滤（可空 = 不限制）。</summary>
    public string? ItemType { get; init; }

    /// <summary>仅返回 SelectedCount >= MinSelectedCount 的 stats（可空 = 不限制）。</summary>
    public int? MinSelectedCount { get; init; }

    /// <summary>仅返回 SelectedCount &lt;= MaxSelectedCount 的 stats（可空 = 不限制）。</summary>
    public int? MaxSelectedCount { get; init; }

    /// <summary>仅返回 LastUsefulTime &lt;= BeforeLastUsefulTime 的 stats（衰减候选）。</summary>
    public DateTimeOffset? BeforeLastUsefulTime { get; init; }

    /// <summary>仅返回 RecallCount >= MinRecallCount 的 stats（可空 = 不限制）。</summary>
    public int? MinRecallCount { get; init; }

    /// <summary>最大返回数量（默认 100；0 = 不限制）。</summary>
    public int Take { get; init; } = 100;
}

/// <summary>
/// MemoryUtilityStats 存储（read-only 公共 API）。
/// </summary>
/// <remarks>
/// 设计原则（对齐澄清 #4）：
///   1. 公共 API 是 read-only：QueryAsync / GetAsync / GetStatsForCandidateAsync。
///   2. 写入由 internal AppendSnapshot 方法暴露，仅供 MemoryUtilityStatsMaterializer 调用。
///   3. StatsStore 与 UtilityLedgerStore 正交：
///      - UtilityLedgerStore：per-Decision per-Expert 快照（细粒度 trace）。
///      - StatsStore：per-Candidate per-workspace-collection 聚合统计（粗粒度决策输入）。
///   4. 生产部署应替换为 PostgresMemoryUtilityStatsStore。
/// </remarks>
public interface IMemoryUtilityStatsStore
{
    /// <summary>按条件查询 stats（按 UpdatedAt 降序返回）。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<MemoryUtilityStats>> QueryAsync(
        MemoryUtilityStatsQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>获取指定 item 的 stats。不存在时返回 null。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<MemoryUtilityStats?> GetAsync(
        string workspaceId,
        string collectionId,
        string sourceItemId,
        CancellationToken cancellationToken = default);

    /// <summary>获取指定 item 的聚合贡献指标（按 Expert 分组的 SelectedCount/UsefulFeedbackCount）。
    /// 用于 ablation 分析：移除某 Expert 后该 item 的贡献变化。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyDictionary<RetrievalExpert, int>> GetSelectedCountByExpertAsync(
        string workspaceId,
        string collectionId,
        string sourceItemId,
        CancellationToken cancellationToken = default);
}
