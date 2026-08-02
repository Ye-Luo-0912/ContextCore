using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// Memory Evolution Engine 统一契约（替换 R21-1 的 SupersededItemState）
//
// 设计变更（用户选择"合并为统一 MemoryState 枚举"）：
//   - 原 SupersededItemState（5 值：Unknown/Active/Superseded/Replaced/Archived）
//     只覆盖 supersede 事件流，不覆盖记忆衰减生命周期。
//   - 新 MemoryState（8 值：Fresh/Active/Cooling/Dormant/Superseded/Replaced/Archived/Rejected）
//     统一了 supersede 事件状态与衰减生命周期，支持"forgetting 与降权"场景。
//
// 状态机转换：
//   Fresh → Active（首次命中/选入/写入）
//   Fresh → Rejected（审核未通过）
//   Active → Cooling（长期未命中）
//   Active → Superseded（被新版本取代）
//   Active → Rejected（审核拒绝）
//   Cooling → Dormant（继续未命中）
//   Cooling → Active（回温：重新被命中）
//   Cooling → Rejected（审核拒绝）
//   Dormant → Archived（彻底降权）
//   Dormant → Active（回温：重新被命中）
//   Dormant → Rejected（审核拒绝）
//   Superseded → Replaced（Consolidation ETL Transform）
//   Replaced → Archived（Consolidation ETL Load）
//   Rejected → Archived（拒绝后归档）
//   Archived：终态，不可逆
//
// 降权因素（R21-4c 实现 DefaultMemoryDecayEvaluator）：
//   1. 长期未命中 → Active → Cooling → Dormant → Archived
//   2. 已有新版本 → Active → Superseded
//   3. evidence 失效 → Active → Rejected
//   4. 任务已完成 → Active → Archived
//   5. 与当前状态冲突 → Active → Rejected
//   6. 多次被选择但未产生有效贡献 → Active → Cooling
//
// 与现有系统的关系：
//   - 不替换 IRelationProjector.ProjectForSupersede / SupersedeProjectionRequest
//   - 不替换 StableMemoryGovernanceService 的统计聚合
//   - 不替换 ContextMemoryStatus / StableMemoryLifecycle 字符串常量
//   - R21-4 是新事件流：MemoryStateEventRecord 记录"何时/何因/由谁触发状态转换"
//     Consolidation ETL 消费事件流驱动 store 状态迁移。
// ===========================================================================

// ---------------------------------------------------------------------------
// MemoryState 枚举（8 值）
// ---------------------------------------------------------------------------

/// <summary>
/// item 在 Memory Evolution 生命周期中的统一状态。
/// 合并了 supersede 事件流（Superseded/Replaced/Archived）与衰减生命周期（Fresh/Cooling/Dormant/Rejected）。
/// </summary>
/// <remarks>
/// 状态分组：
///   - 初始态：Fresh（新创建，未参与过决策）
///   - 活跃态：Active（参与决策且最近命中）
///   - 衰减态：Cooling（长期未命中，可回温）/ Dormant（更长期未命中，可回温）
///   - 取代态：Superseded（被新版本取代，事件已记录）/ Replaced（ETL 处理中）
///   - 终态：Archived（彻底归档，不可逆）/ Rejected（审核拒绝，可归档）
/// </remarks>
public enum MemoryState : byte
{
    /// <summary>新创建的 item，未参与过决策（初始态）。</summary>
    Fresh = 0,

    /// <summary>活跃，最近被命中或选入（活跃态）。</summary>
    Active = 1,

    /// <summary>冷却中，长期未命中但仍可回温（衰减态）。</summary>
    Cooling = 2,

    /// <summary>休眠，更长期未命中但仍可回温（衰减态）。</summary>
    Dormant = 3,

    /// <summary>已被新版本 supersede（事件已记录但 item 仍在 active store）。</summary>
    Superseded = 4,

    /// <summary>已被 Consolidation ETL 标记为"待迁移"（已提取，正在写入归档 store）。</summary>
    Replaced = 5,

    /// <summary>已归档（Consolidation ETL 完成；item 已在归档 store）。终态，不可逆。</summary>
    Archived = 6,

    /// <summary>被审核拒绝（evidence 失效/任务完成/冲突）。可推进到 Archived。</summary>
    Rejected = 7
}

// ---------------------------------------------------------------------------
// MemoryStateEventRecord 事件记录（替换 SupersedeEventRecord）
// ---------------------------------------------------------------------------

/// <summary>
/// memory state 转换事件记录。捕获"何时 / 何因 / 由谁触发状态转换"事件流。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 事件流不可变：一旦写入不可修改（append-only 语义）。
///   2. 同一 item 可能有多次状态转换事件（如 Fresh→Active→Cooling→Active→Superseded→Replaced→Archived）；
///      查询时按 OccurredAt 排序取最新。
///   3. 不直接修改 item 状态；Consolidation ETL 与 DecayEvaluator 消费事件流驱动状态迁移。
///   4. TargetItemId 可空：表示"无替换直接降权"（如 obsolete 知识）。
/// </remarks>
public sealed record MemoryStateEventRecord
{
    /// <summary>事件唯一 ID（如 "evt-{guid}"）。</summary>
    public required string EventId { get; init; }

    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（必填；跨集合时使用 "*"）。</summary>
    public required string CollectionId { get; init; }

    /// <summary>被触发状态转换的 item ID（必填）。</summary>
    public required string SourceItemId { get; init; }

    /// <summary>取代的新 item ID（可空 = 无替换直接降权/拒绝）。</summary>
    public string? TargetItemId { get; init; }

    /// <summary>item 类型（必填；"context" / "memory" / "constraint" / "relation" / "vector"）。</summary>
    public required string ItemType { get; init; }

    /// <summary>新状态（必填；不允许为 Fresh，Fresh 是初始态不是事件目标）。</summary>
    public required MemoryState NewState { get; init; }

    /// <summary>转换原因码（如 "first-hit" / "decay-cooling" / "decay-dormant" /
    /// "supersede" / "consolidation-etl" / "rejected-evidence-invalid" /
    /// "rejected-task-completed" / "rejected-conflict" / "manual" / "reheat"）。</summary>
    public required string Reason { get; init; }

    /// <summary>触发原因详情（人类可读；如 "no hit for 30 days, decay to Cooling"）。</summary>
    public string ReasonDetail { get; init; } = string.Empty;

    /// <summary>触发者（user / system / agent ID；可空 = 自动触发）。</summary>
    public string? Reviewer { get; init; }

    /// <summary>事件发生时间（UTC）。</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>关联的 superseded_by / supersedes 关系 ID（可空 = 无图边关联）。</summary>
    public string? RelationId { get; init; }

    /// <summary>关联的 Consolidation Run ID（仅当 Reason="consolidation-etl" 时填充）。</summary>
    public string? ConsolidationRunId { get; init; }

    /// <summary>事件元数据（自定义键值对，用于 trace 与审计）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// memory state 事件查询条件。
/// </summary>
public sealed record MemoryStateEventQuery
{
    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（可空 = 跨集合）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>按 SourceItemId 过滤（可空 = 不限制）。</summary>
    public string? SourceItemId { get; init; }

    /// <summary>按 TargetItemId 过滤（可空 = 不限制）。</summary>
    public string? TargetItemId { get; init; }

    /// <summary>按 ItemType 过滤（可空 = 不限制）。</summary>
    public string? ItemType { get; init; }

    /// <summary>按 NewState 过滤（可空 = 不限制）。</summary>
    public MemoryState? NewState { get; init; }

    /// <summary>仅返回 OccurredAt >= Since 的事件（可空 = 不限制）。</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>仅返回 OccurredAt &lt;= Until 的事件（可空 = 不限制）。</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>最大返回数量（默认 100；0 = 不限制）。</summary>
    public int Take { get; init; } = 100;
}

// ---------------------------------------------------------------------------
// IMemoryStateStore 接口（替换 ISupersededItemStore）
// ---------------------------------------------------------------------------

/// <summary>
/// memory state 事件流存储接口。append-only 语义，记录 item 状态转换事件。
/// </summary>
/// <remarks>
/// 实现层可注入 Postgres / InMemory / FileSystem store。
/// 接口契约最小化：
///   - AppendEventAsync：追加事件（不可变）。
///   - QueryEventsAsync：按条件查询事件。
///   - GetLatestStateAsync：查询 item 当前最新状态（按 OccurredAt 降序取首条 NewState）。
///   - GetRecentAsync：返回最近 N 条事件（按 OccurredAt 降序）。
/// </remarks>
public interface IMemoryStateStore
{
    /// <summary>追加状态转换事件（不可变；重复 EventId 应抛 ArgumentException）。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task AppendEventAsync(
        MemoryStateEventRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>按条件查询事件（按 OccurredAt 降序返回）。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<MemoryStateEventRecord>> QueryEventsAsync(
        MemoryStateEventQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>查询指定 item 的当前最新状态（按 OccurredAt 降序取首条 NewState）。</summary>
    /// <returns>最新事件；item 从未有状态转换事件时返回 null（视为 Fresh）。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    Task<MemoryStateEventRecord?> GetLatestStateAsync(
        string workspaceId,
        string collectionId,
        string sourceItemId,
        CancellationToken cancellationToken = default);

    /// <summary>返回最近 N 条事件（按 OccurredAt 降序）。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<MemoryStateEventRecord>> GetRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Consolidation ETL 契约（保留接口，事件类型改为 MemoryStateEventRecord）
// ---------------------------------------------------------------------------

/// <summary>
/// Consolidation ETL 请求。把 superseded/replaced items 从 active store 迁移到归档 store。
/// </summary>
/// <remarks>
/// ETL 流程：
///   1. Extract：从 IMemoryStateStore 查询符合条件的 Superseded/Replaced 状态事件。
///   2. Transform：把对应 item 状态从 Superseded 推进到 Replaced（中间态），
///      写入新 MemoryStateEventRecord（Reason="consolidation-etl"）。
///   3. Load：把 item 写入归档 store，推进状态到 Archived，写入最终 MemoryStateEventRecord。
///
/// DryRun=true：仅返回预计处理数量，不实际迁移。
/// </remarks>
public sealed record ConsolidationRequest
{
    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（必填）。</summary>
    public required string CollectionId { get; init; }

    /// <summary>仅处理 OccurredAt &lt; OlderThan 的事件（默认 = UtcNow）。</summary>
    public DateTimeOffset OlderThan { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>item 类型过滤（空 = 全部类型）。</summary>
    public IReadOnlyList<string> ItemTypes { get; init; } = Array.Empty<string>();

    /// <summary>批次大小（默认 100；0 = 不分批）。</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>DryRun 模式（true = 仅返回预计处理数量，不实际迁移）。</summary>
    public bool DryRun { get; init; } = false;

    /// <summary>触发者（user / system / agent ID；可空 = 自动触发）。</summary>
    public string? TriggeredBy { get; init; }
}

/// <summary>
/// Consolidation ETL 执行结果。
/// </summary>
public sealed record ConsolidationRunResult
{
    /// <summary>Run 唯一 ID（如 "run-{guid}"）。</summary>
    public required string RunId { get; init; }

    /// <summary>workspace 作用域。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域。</summary>
    public required string CollectionId { get; init; }

    /// <summary>提取的 item 数量（Extract 阶段）。</summary>
    public int ExtractedCount { get; init; }

    /// <summary>转换的 item 数量（Transform 阶段；可能少于 ExtractedCount 因部分 item 已是 Replaced）。</summary>
    public int TransformedCount { get; init; }

    /// <summary>写入归档 store 的 item 数量（Load 阶段）。</summary>
    public int LoadedCount { get; init; }

    /// <summary>跳过的 item 数量（已 Archived / 已被并发 ETL 处理 / 数据校验失败）。</summary>
    public int SkippedCount { get; init; }

    /// <summary>开始时间（UTC）。</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>完成时间（UTC）。</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>是否 DryRun 模式。</summary>
    public bool DryRun { get; init; }

    /// <summary>实际处理的 item ID 列表（DryRun=true 时为预计处理的 ID 列表）。</summary>
    public IReadOnlyList<string> ProcessedItemIds { get; init; } = Array.Empty<string>();

    /// <summary>错误信息列表（部分失败时；空 = 全部成功）。</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>触发者（来自请求）。</summary>
    public string? TriggeredBy { get; init; }

    /// <summary>执行是否成功（Errors 为空）。</summary>
    public bool IsSuccess => Errors.Count == 0;

    /// <summary>执行耗时（CompletedAt - StartedAt）。</summary>
    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>
/// Consolidation ETL 接口。把 superseded/replaced items 从 active store 迁移到归档 store。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. ETL 是幂等的：重复执行不会产生副作用（已 Archived 的 item 跳过）。
///   2. ETL 是可中断的：批次大小限制单次处理量；调用方可循环执行直到 ExtractedCount=0。
///   3. ETL 失败不破坏数据：Transform 阶段写入 Replaced 事件后失败，
///      下次 ETL 会从 Replaced 状态继续推进到 Archived。
///   4. ETL 不直接修改 active store 中的 item；只写入归档 store + 推进状态。
///   5. 兼容 MemoryState 衰减路径：Dormant → Archived 也可由 ETL 推进（彻底降权）。
/// </remarks>
public interface IConsolidationETL
{
    /// <summary>执行一次 Consolidation ETL 迁移。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task<ConsolidationRunResult> RunAsync(
        ConsolidationRequest request,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// MemoryState 扩展方法（替换 SupersededItemStateExtensions）
// ---------------------------------------------------------------------------

/// <summary>
/// MemoryState 扩展方法。提供状态机判断逻辑。
/// </summary>
public static class MemoryStateExtensions
{
    /// <summary>判断状态是否为终态（不可逆）。</summary>
    public static bool IsTerminal(this MemoryState state)
        => state == MemoryState.Archived;

    /// <summary>判断状态是否允许推进到下一状态（状态机合法性检查）。</summary>
    public static bool CanTransitionTo(this MemoryState current, MemoryState next)
    {
        if (current == next) return false; // 自环不允许
        return current switch
        {
            // Fresh → Active / Rejected
            MemoryState.Fresh => next == MemoryState.Active || next == MemoryState.Rejected,
            // Active → Cooling / Superseded / Rejected
            MemoryState.Active => next == MemoryState.Cooling
                || next == MemoryState.Superseded
                || next == MemoryState.Rejected,
            // Cooling → Dormant / Active（回温）/ Rejected
            MemoryState.Cooling => next == MemoryState.Dormant
                || next == MemoryState.Active
                || next == MemoryState.Rejected,
            // Dormant → Archived / Active（回温）/ Rejected
            MemoryState.Dormant => next == MemoryState.Archived
                || next == MemoryState.Active
                || next == MemoryState.Rejected,
            // Superseded → Replaced（ETL Transform）
            MemoryState.Superseded => next == MemoryState.Replaced,
            // Replaced → Archived（ETL Load）
            MemoryState.Replaced => next == MemoryState.Archived,
            // Rejected → Archived（拒绝后归档）
            MemoryState.Rejected => next == MemoryState.Archived,
            // Archived 终态
            _ => false
        };
    }

    /// <summary>判断状态是否需要 Consolidation ETL 处理（Superseded 或 Replaced）。</summary>
    public static bool NeedsConsolidation(this MemoryState state)
        => state == MemoryState.Superseded || state == MemoryState.Replaced;

    /// <summary>判断状态是否为衰减态（Cooling 或 Dormant，可回温）。</summary>
    public static bool IsDecaying(this MemoryState state)
        => state == MemoryState.Cooling || state == MemoryState.Dormant;

    /// <summary>判断状态是否为活跃态（Fresh 或 Active）。</summary>
    public static bool IsActiveOrFresh(this MemoryState state)
        => state == MemoryState.Fresh || state == MemoryState.Active;

    /// <summary>判断状态是否可回温（Cooling 或 Dormant；重新被命中时可回到 Active）。</summary>
    public static bool CanReheat(this MemoryState state)
        => state == MemoryState.Cooling || state == MemoryState.Dormant;
}
