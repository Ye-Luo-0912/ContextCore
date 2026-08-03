using ContextCore.Abstractions;

namespace ContextCore.Abstractions;

// ===========================================================================
// Memory Decay Evaluator 契约
//
// 对齐用户规格中的 6 种降权因素：
// 1. 长期未命中 → Active → Cooling → Dormant → Archived
// 2. 已有新版本 → Active → Superseded
// 3. evidence 失效 → Active → Rejected
// 4. 任务已完成 → Active → Archived
// 5. 与当前状态冲突 → Active → Rejected
// 6. 多次被选择但未产生有效贡献 → Active → Cooling
//
// 设计原则：
// - DecayEvaluator 只输出评估结果（MemoryDecayAssessment），
// 不直接写入 IMemoryStateStore。调用方决定是否落库。
// - 评估结果包含目标状态 + 触发的降权因素 + 理由详情。
// - 多个降权因素同时触发时，取优先级最高（Rejected > Superseded > Archived > Dormant > Cooling）。
// ===========================================================================

/// <summary>
/// memory 降权因素枚举（对齐用户规格的 6 种因素）。
/// </summary>
public enum MemoryDecayFactor : byte
{
    /// <summary>未知因素（不应出现在正式评估结果中）。</summary>
    Unknown = 0,

    /// <summary>长期未命中：item 在阈值时间内未被检索/选入。</summary>
    LongTermNoHit = 1,

    /// <summary>已有新版本：item 被新版本 supersede（由 supersede 事件触发）。</summary>
    NewVersionAvailable = 2,

    /// <summary>evidence 失效：item 引用的证据已失效（被驳回/被冲突）。</summary>
    EvidenceInvalid = 3,

    /// <summary>任务已完成：item 关联的任务已完成，无需保留。</summary>
    TaskCompleted = 4,

    /// <summary>与当前状态冲突：item 与其他 active item 冲突（Contradicts/Duplicate）。</summary>
    ConflictWithCurrent = 5,

    /// <summary>多次被选择但未产生有效贡献：item 多次进入 Package 但无正反馈。</summary>
    NoEffectiveContribution = 6
}

/// <summary>
/// memory 衰减评估结果。包含目标状态 + 触发的降权因素 + 理由详情。
/// </summary>
public sealed record MemoryDecayAssessment
{
    /// <summary>评估的 item ID（必填）。</summary>
    public required string SourceItemId { get; init; }

    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（必填）。</summary>
    public required string CollectionId { get; init; }

    /// <summary>当前状态（评估输入；必填）。</summary>
    public required MemoryState CurrentState { get; init; }

    /// <summary>建议的目标状态（评估输出；必填）。</summary>
    public required MemoryState TargetState { get; init; }

    /// <summary>触发的降权因素（必填；Unknown 不应出现在正式结果中）。</summary>
    public required MemoryDecayFactor DecayFactor { get; init; }

    /// <summary>触发原因详情（人类可读；如 "no hit for 30 days"）。</summary>
    public string ReasonDetail { get; init; } = string.Empty;

    /// <summary>取代的新 item ID（仅当 DecayFactor=NewVersionAvailable 时填充）。</summary>
    public string? TargetItemId { get; init; }

    /// <summary>冲突 item ID 列表（仅当 DecayFactor=ConflictWithCurrent 时填充）。</summary>
    public IReadOnlyList<string> ConflictItemIds { get; init; } = Array.Empty<string>();

    /// <summary>评估时间戳（UTC）。</summary>
    public required DateTimeOffset AssessedAt { get; init; }

    /// <summary>评估元数据（用于 trace 与审计）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>是否需要状态转换（CurrentState != TargetState）。</summary>
    public bool NeedsTransition => CurrentState != TargetState;
}

/// <summary>
/// memory 衰减评估器接口。根据 MemoryUtilityStats 等指标评估 item 是否需要降权。
/// </summary>
/// <remarks>
/// 设计原则：
/// - 只读评估，不写入 store。调用方决定是否落库。
/// - 评估输入：当前状态 + MemoryUtilityStats（提供）+ 显式触发因素。
/// - 评估输出：MemoryDecayAssessment（含目标状态 + 降权因素 + 理由）。
/// - 多个因素同时触发时，取优先级最高（Rejected > Superseded > Archived > Dormant > Cooling）。
/// </remarks>
public interface IMemoryDecayEvaluator
{
    /// <summary>评估单个 item 的衰减状态。</summary>
    /// <param name="currentState">item 当前状态。</param>
    /// <param name="stats">item 的 utility 聚合统计（可空 = 从未参与决策）。</param>
    /// <param name="now">评估时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>评估结果；若不需要降权，TargetState = CurrentState + DecayFactor = Unknown。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    Task<MemoryDecayAssessment> EvaluateAsync(
        string sourceItemId,
        string workspaceId,
        string collectionId,
        MemoryState currentState,
        MemoryUtilityStats? stats,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default);
}
