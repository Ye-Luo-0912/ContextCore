using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// R21-2：Utility Ledger + ConflictSet 契约
//
// 目标：
//   为 R20 Multi-Expert 选择系统提供"per-Expert per-Candidate utility 贡献账本"
//   和"冲突集合"两个独立 store，用于学习闭环 / ablation / 归因分析。
//
// 设计原则（对齐 8 项用户澄清）：
//   1. 澄清 #4（Utility Ledger）：新增独立 Store 但由 Trace/Event 异步批量物化。
//      - IUtilityLedgerStore 是 read-only API：Query / GetLatestEntry / GetExpertContributions。
//      - 写入路径在 R21-3 实现：Trace/Event materializer 异步批量写入。
//      - 不暴露 WriteAsync / AppendAsync 等方法到接口（避免误用为同步写）。
//   2. 澄清 #5（Router 标签）：Expert-level ablation 不做全量 candidate LOO。
//      - Utility Ledger 提供数据基础，但 LOO 计算在更上层执行（不在此 store）。
//   3. 澄清 #7（Expert 重叠）：该 Expert 特征贡献只有无其他来源才删 Candidate。
//      - ConflictSet 记录哪些 Candidate 由多个 Expert 重叠贡献，便于 ablation 决策。
//   4. 澄清 #8（交互归因）：普通 LOO + 困难 pair ablation + 少量近似 Shapley。
//      - Utility Ledger 提供 per-Expert 贡献数据；近似 Shapley 在更上层执行。
//   5. P8 Learning Loop 硬边界：不直接用 selected=positive、dropped=negative。
//      - Utility Ledger 记录所有 candidate 的 utility（无论 selected/dropped），
//        避免将 dropped 简单视为负样本。
//
// 与 R20-R21 已有契约的关系：
//   - RetrievalExpert（R20-1）：Utility Ledger 按 Expert 分组
//   - ExpertRoutingDecisionSet（R20-1/20-2）：路由决策影响 per-Expert TopK
//   - ContextCandidateEnvelope.Utility.CandidateUtilityScore（R18-1）：
//     Utility Ledger 物化 CandidateUtilityScore 的历史快照
//   - MemoryStateEventRecord（R21-4）：ConflictSet 可关联 memory state 转换事件
//   - ContextRelationTypes.Contradicts / Duplicates / ConflictsWith：
//     ConflictSet 引用关系类型常量
//
// 子阶段进度：
//   R21-2（当前）：契约定义（UtilityLedgerEntry / UtilityLedgerQuery / IUtilityLedgerStore /
//      ConflictSetKind / ConflictSetEntry / ConflictSet / ConflictSetQuery / IConflictSetStore）。
//   R21-3：完整状态机 + Materializer 实现 + Postgres/InMemory store 实现。
// ===========================================================================

// ---------------------------------------------------------------------------
// UtilityLedgerEntry（per-Candidate per-Expert utility 贡献账本条目）
// ---------------------------------------------------------------------------

/// <summary>
/// R21-2：Utility Ledger 条目。记录单个 Candidate 的 per-Expert utility 贡献快照。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 异步批量物化：本条目由 Trace/Event materializer 异步写入，不通过同步 API 写入。
///   2. 不可变：一旦写入不可修改（append-only 语义；同 candidate 可有多条历史快照）。
///   3. 物化时机：每次 ContextDecisionResult 生成后，由 materializer 提取
///      SelectedEnvelopes + DroppedEnvelopes 中每个 envelope 的
///      CandidateUtilityScore，写入对应 Expert 的 ledger 条目。
///   4. 不替代运行时决策：本条目仅用于离线分析；不影响 Engine 运行时行为。
///   5. P8 硬边界：所有 candidate（selected/dropped）都写入 ledger，
///      避免"dropped 视为负样本"的简化。
/// </remarks>
public sealed record UtilityLedgerEntry
{
    /// <summary>条目唯一 ID（如 "ledger-{guid}"）。</summary>
    public required string EntryId { get; init; }

    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（必填）。</summary>
    public required string CollectionId { get; init; }

    /// <summary>候选 item ID（必填；与 ContextCandidateEnvelope.CandidateId 对应）。</summary>
    public required string CandidateItemId { get; init; }

    /// <summary>贡献的 Expert（必填；Mandatory/Constraint/Lexical/Semantic/WorkingMemory/StableMemory/Graph/Recency）。</summary>
    public required RetrievalExpert Expert { get; init; }

    /// <summary>Expert 对该 candidate 的 utility 贡献值（0.0-1.0；表示"该 Expert 对该 candidate 的评分贡献比例"）。</summary>
    /// <remarks>
    /// 贡献值由 materializer 根据 Expert 的 ScoreBreakdown 计算：
    ///   contribution = expert_score_breakdown[candidate] / sum(all_experts_score_breakdown[candidate])
    /// 若 Expert 未贡献分数，contribution = 0.0（仍写入条目，便于 ablation 分析）。
    /// </remarks>
    public required double UtilityContribution { get; init; }

    /// <summary>该 candidate 的 deterministic 评分（永不依赖模型）。</summary>
    public required double DeterministicScore { get; init; }

    /// <summary>该 candidate 的 model 评分（可空；Model failure 时为 null）。</summary>
    public double? ModelScore { get; init; }

    /// <summary>该 candidate 的最终聚合评分（与 ContextCandidateEnvelope.Utility.FinalScore 一致）。</summary>
    public required double FinalScore { get; init; }

    /// <summary>该 candidate 是否被选入 SelectedEnvelopes（false = 在 DroppedEnvelopes 中）。</summary>
    public required bool IsSelected { get; init; }

    /// <summary>该 candidate 被 drop 时的原因码（可空；selected 时为 null）。</summary>
    public string? DropReasonCode { get; init; }

    /// <summary>关联的 DecisionResult ID（与 ContextDecisionResult.RequestId 对应）。</summary>
    public required string DecisionId { get; init; }

    /// <summary>关联的 PolicyVersion（与 ContextDecisionResult.PolicyVersion 对应）。</summary>
    public required string PolicyVersion { get; init; }

    /// <summary>关联的 RouterId（与 ExpertRoutingDecisionSet.RouterId 对应；可空 = 未启用 Router）。</summary>
    public string? RouterId { get; init; }

    /// <summary>物化时间（UTC；由 materializer 写入时记录）。</summary>
    public required DateTimeOffset MaterializedAt { get; init; }

    /// <summary>物化批次 ID（materializer 每次批量写入分配的批次 ID；用于追溯）。</summary>
    public string? MaterializationBatchId { get; init; }

    /// <summary>条目元数据（自定义键值对，用于 trace 与审计）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// R21-2：Utility Ledger 查询条件。
/// </summary>
public sealed record UtilityLedgerQuery
{
    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（可空 = 跨集合）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>按 CandidateItemId 过滤（可空 = 不限制）。</summary>
    public string? CandidateItemId { get; init; }

    /// <summary>按 Expert 过滤（可空 = 不限制）。</summary>
    public RetrievalExpert? Expert { get; init; }

    /// <summary>按 DecisionId 过滤（可空 = 不限制）。</summary>
    public string? DecisionId { get; init; }

    /// <summary>仅返回 IsSelected=true 的条目（null = 不限制）。</summary>
    public bool? IsSelected { get; init; }

    /// <summary>仅返回 MaterializedAt >= Since 的条目（可空 = 不限制）。</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>仅返回 MaterializedAt &lt;= Until 的条目（可空 = 不限制）。</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>最大返回数量（默认 100；0 = 不限制）。</summary>
    public int Take { get; init; } = 100;
}

/// <summary>
/// R21-2：Utility Ledger 存储接口（read-only）。
/// </summary>
/// <remarks>
/// 设计原则（对齐澄清 #4）：
///   1. 此接口仅暴露读 API；写入由 Trace/Event materializer 异步批量完成。
///   2. 实现层可注入 Postgres / InMemory store；契约不依赖存储。
///   3. GetExpertContributions 返回 per-Expert 贡献聚合（用于 ablation 分析）。
/// </remarks>
public interface IUtilityLedgerStore
{
    /// <summary>按条件查询 ledger 条目（按 MaterializedAt 降序返回）。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<UtilityLedgerEntry>> QueryAsync(
        UtilityLedgerQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>查询指定 candidate 的最新 ledger 条目（按 MaterializedAt 降序取首条）。</summary>
    /// <returns>最新条目；candidate 从未被记录时返回 null。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    Task<UtilityLedgerEntry?> GetLatestEntryAsync(
        string workspaceId,
        string collectionId,
        string candidateItemId,
        CancellationToken cancellationToken = default);

    /// <summary>查询指定 candidate 的 per-Expert 贡献聚合（按 Expert 分组求平均贡献）。</summary>
    /// <returns>key = Expert，value = 平均贡献（0.0-1.0）；未记录的 Expert 不在字典中。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyDictionary<RetrievalExpert, double>> GetExpertContributionsAsync(
        string workspaceId,
        string collectionId,
        string candidateItemId,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// ConflictSet（冲突集合）
// ---------------------------------------------------------------------------

/// <summary>
/// R21-2：冲突类型枚举。
/// 对齐 ContextRelationTypes（Contradicts / Duplicates / ConflictsWith）+
/// CandidateDecisionReasonCode（DuplicateSuppressed / SupersededByCurrentVersion）。
/// </summary>
public enum ConflictSetKind : byte
{
    /// <summary>未知冲突类型（仅用于历史数据升级）。</summary>
    Unknown = 0,

    /// <summary>重复内容（同 ContentHash 或同 ItemId；对应 CandidateDecisionReasonCode.DuplicateSuppressed）。</summary>
    Duplicate = 1,

    /// <summary>矛盾关系（对应 ContextRelationTypes.Contradicts）。</summary>
    Contradicts = 2,

    /// <summary>Supersede 环（A supersedes B supersedes A；对应 RelationGraphIssueKind.SupersedeCycle）。</summary>
    SupersedeCycle = 3,

    /// <summary>同 ItemId 来自不同 Expert（Expert 重叠；澄清 #7 的核心场景）。</summary>
    SameItemMultipleSources = 4,

    /// <summary>Section 配额竞争（多个候选竞争同一 section slot；对应 SectionQuotaExceeded）。</summary>
    SectionConflict = 5,

    /// <summary>Token budget 竞争（多个候选竞争同一 token budget；对应 TokenBudgetExceeded）。</summary>
    BudgetConflict = 6
}

/// <summary>
/// R21-2：ConflictSet 中的单个候选条目。
/// </summary>
public sealed record ConflictSetEntry
{
    /// <summary>候选 item ID（必填）。</summary>
    public required string CandidateItemId { get; init; }

    /// <summary>贡献此 candidate 的 Expert（必填）。</summary>
    public required RetrievalExpert Expert { get; init; }

    /// <summary>该 candidate 在决策时的 FinalScore。</summary>
    public required double Score { get; init; }

    /// <summary>该 candidate 是否最终被选入（false = 被 drop）。</summary>
    public required bool IsSelected { get; init; }

    /// <summary>该 candidate 被 drop 时的原因码（可空；selected 时为 null）。</summary>
    public string? DropReasonCode { get; init; }

    /// <summary>冲突详情（人类可读；如 "duplicate content hash: abc123"）。</summary>
    public string? ReasonDetail { get; init; }
}

/// <summary>
/// R21-2：冲突集合。记录一次决策中互相冲突的候选组。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 异步批量物化：与 UtilityLedgerEntry 类似，由 materializer 异步写入。
///   2. 一个 ConflictSet 对应一次 DecisionResult 中的一组冲突候选。
///   3. ResolvedItemId 表示"获胜"的 candidate（可空 = 全部被 drop 或未解决）。
///   4. 用于 ablation / 归因分析（澄清 #7 / #8）：识别哪些 candidate 互相竞争，
///      哪些 Expert 重叠贡献。
/// </remarks>
public sealed record ConflictSet
{
    /// <summary>ConflictSet 唯一 ID（如 "conflict-{guid}"）。</summary>
    public required string ConflictSetId { get; init; }

    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（必填）。</summary>
    public required string CollectionId { get; init; }

    /// <summary>冲突类型（必填）。</summary>
    public required ConflictSetKind Kind { get; init; }

    /// <summary>冲突候选列表（必填；至少 2 条）。</summary>
    public required IReadOnlyList<ConflictSetEntry> Entries { get; init; }

    /// <summary>关联的 DecisionResult ID（与 ContextDecisionResult.RequestId 对应）。</summary>
    public required string DecisionId { get; init; }

    /// <summary>"获胜"的 candidate ID（可空 = 全部 drop 或未解决）。</summary>
    public string? ResolvedItemId { get; init; }

    /// <summary>解决状态（R21-5b；默认 Unresolved；materializer 选定 resolved item 时为 AutoResolved）。</summary>
    public ConflictResolutionStatus ResolutionStatus { get; init; } = ConflictResolutionStatus.Unresolved;

    /// <summary>"获胜权威"来源（R21-5b；对齐用户规格的"chosen authority"字段）。
    /// 取值："highest-score" / "lowest-token-cost" / "newest-version" / "manual:{reviewerId}" / null（未解决）。</summary>
    public string? ChosenAuthority { get; init; }

    /// <summary>解决时间戳（R21-5b；仅当 ResolutionStatus != Unresolved 时填充）。</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>解决者（R21-5b；仅当 ResolutionStatus=ManuallyResolved 时填充）。</summary>
    public string? Resolver { get; init; }

    /// <summary>关联的 memory state 转换事件 ID（仅当 Kind=SupersedeCycle 时填充）。</summary>
    public string? MemoryStateEventId { get; init; }

    /// <summary>关联的关系 ID（仅当 Kind=Contradicts / Duplicate 时填充；对应 ContextRelation 关系 ID）。</summary>
    public string? RelationId { get; init; }

    /// <summary>物化时间（UTC；由 materializer 写入时记录）。</summary>
    public required DateTimeOffset MaterializedAt { get; init; }

    /// <summary>物化批次 ID（materializer 每次批量写入分配的批次 ID；用于追溯）。</summary>
    public string? MaterializationBatchId { get; init; }

    /// <summary>条目元数据（自定义键值对，用于 trace 与审计）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// R21-5b：ConflictSet 解决状态。对齐用户规格中的"resolution status"字段。
/// </summary>
public enum ConflictResolutionStatus : byte
{
    /// <summary>未解决（默认；materializer 写入时若无法自动决定）。</summary>
    Unresolved = 0,

    /// <summary>已自动解决（materializer 选定 resolved item；后续可人工覆盖）。</summary>
    AutoResolved = 1,

    /// <summary>已人工解决（reviewer 选定 chosen authority）。</summary>
    ManuallyResolved = 2,

    /// <summary>已挂起（reviewer 标记为需要更多 evidence）。</summary>
    Pending = 3,

    /// <summary>已废弃（关联的 items 已被 Archived/Rejected）。</summary>
    Obsolete = 4
}

/// <summary>
/// R21-2：ConflictSet 查询条件。
/// </summary>
public sealed record ConflictSetQuery
{
    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（可空 = 跨集合）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>按 ConflictSetKind 过滤（可空 = 不限制）。</summary>
    public ConflictSetKind? Kind { get; init; }

    /// <summary>按 CandidateItemId 过滤（在 Entries 列表中包含此 candidate；可空 = 不限制）。</summary>
    public string? CandidateItemId { get; init; }

    /// <summary>按 DecisionId 过滤（可空 = 不限制）。</summary>
    public string? DecisionId { get; init; }

    /// <summary>按 ResolutionStatus 过滤（R21-5b；可空 = 不限制）。</summary>
    public ConflictResolutionStatus? ResolutionStatus { get; init; }

    /// <summary>仅返回 MaterializedAt >= Since 的冲突集合（可空 = 不限制）。</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>仅返回 MaterializedAt &lt;= Until 的冲突集合（可空 = 不限制）。</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>最大返回数量（默认 100；0 = 不限制）。</summary>
    public int Take { get; init; } = 100;
}

/// <summary>
/// R21-2：ConflictSet 存储接口（read-only）。
/// </summary>
/// <remarks>
/// 设计原则（对齐澄清 #4）：
///   1. 此接口仅暴露读 API；写入由 Trace/Event materializer 异步批量完成。
///   2. 实现层可注入 Postgres / InMemory store；契约不依赖存储。
///   3. GetConflictsForCandidate 返回包含指定 candidate 的所有 ConflictSet（用于 ablation）。
/// </remarks>
public interface IConflictSetStore
{
    /// <summary>按条件查询 ConflictSet（按 MaterializedAt 降序返回）。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ConflictSet>> QueryAsync(
        ConflictSetQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>按 ID 获取单个 ConflictSet（不存在时返回 null）。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<ConflictSet?> GetAsync(
        string workspaceId,
        string collectionId,
        string conflictSetId,
        CancellationToken cancellationToken = default);

    /// <summary>查询包含指定 candidate 的所有 ConflictSet（用于 ablation 分析）。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ConflictSet>> GetConflictsForCandidateAsync(
        string workspaceId,
        string collectionId,
        string candidateItemId,
        CancellationToken cancellationToken = default);
}
