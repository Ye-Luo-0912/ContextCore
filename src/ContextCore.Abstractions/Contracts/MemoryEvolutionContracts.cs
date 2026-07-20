using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// R21-1：Memory Evolution Engine 契约（Superseded 状态 + Consolidation ETL）
//
// 目标：
//   统一分散在多处的"Superseded"状态管理（ContextRelationTypes.SupersededBy /
//   StableMemorySnapshot.SupersededCount / PromotionPolicyDtos.Superseded /
//   RelationGraphDtos.Superseded 等）为一个明确的 SupersedeEvent 契约，
//   并定义 Consolidation ETL 接口把 superseded items 从 active store
//   迁移到归档 store，保持历史可追溯。
//
// 设计原则（对齐 R18/R19/R20 子阶段顺序）：
//   1. R21-1（当前）：契约定义（SupersededItemState / SupersedeEventRecord /
//      ISupersededItemStore / ConsolidationRequest / ConsolidationRunResult /
//      IConsolidationETL）。不实现具体存储；接口允许实现层注入 Postgres / InMemory。
//   2. R21-2：Utility Ledger + ConflictSet 契约（per-Expert utility 账本 + 冲突集合）。
//   3. R21-3：完整状态机（SupersededItemState 状态机 + Consolidation ETL 实现 +
//      Utility Ledger 物化 + ConflictSet 检测）。
//
// 与现有系统的关系：
//   - 不替换 IRelationProjector.ProjectForSupersede / SupersedeProjectionRequest
//     （这些负责图边投影：superseded_by / supersedes 关系）。
//   - 不替换 StableMemoryGovernanceService 的 SupersededCount 统计
//     （这些是聚合快照；R21-1 是事件流）。
//   - 不替换 ContextMemoryStatus / StableMemoryLifecycle 字符串常量
//     （这些是 item 自身状态；R21-1 是 supersede 事件）。
//   - R21-1 是新事件流：SupersedeEventRecord 记录"何时 / 何因 / 由谁 supersede"，
//     Consolidation ETL 消费事件流驱动 store 状态迁移。
//
// 8 项澄清对齐：
//   - 澄清 #1（Envelope Evidence）：不冲突，SupersedeEvent 是独立事件流。
//   - 澄清 #2（PolicyBundle scope）：不冲突，bundle supersede（PolicyBundle.SupersededAt）
//     与 item supersede（SupersedeEventRecord）独立。
//   - 澄清 #3（Request Policy）：不冲突，per-request 不影响 supersede 状态。
//   - 澄清 #4（Utility Ledger）：R21-2 处理，R21-1 不实现。
//   - 澄清 #5/#6/#7/#8：与 R20 Multi-Expert 相关，R21-1 不涉及。
// ===========================================================================

// ---------------------------------------------------------------------------
// SupersededItemState 枚举
// ---------------------------------------------------------------------------

/// <summary>
/// R21-1：item 在 Memory Evolution 生命周期中的状态。
/// 统一分散在 ContextMemoryStatus / StableMemoryLifecycle / PromotionCandidateStatus
/// 等多处的"Superseded"语义。
/// </summary>
/// <remarks>
/// 状态流转（由 Consolidation ETL 驱动）：
///   Active → Superseded：发生 supersede 事件（SupersedeEventRecord 写入）
///   Superseded → Replaced：Consolidation ETL 提取并标记为已迁移
///   Replaced → Archived：Consolidation ETL 写入归档 store 完成
///
/// 终态：Archived（不可逆；如需恢复则创建新 item 并通过 supersedes 关系指向旧 item）。
/// </remarks>
public enum SupersededItemState : byte
{
    /// <summary>未知状态（仅用于历史数据升级或 trace 默认值）。</summary>
    Unknown = 0,

    /// <summary>活跃，未被 supersede。等同 ContextMemoryStatus.Stable / StableMemoryLifecycle.Current。</summary>
    Active = 1,

    /// <summary>已被新版本 supersede（事件已记录但 item 仍在 active store；保留可追溯）。</summary>
    /// <remarks>
    /// 此状态下 item 仍可被检索到（HybridContextRetriever 默认排除；audit anchor 允许）。
    /// Consolidation ETL 会进一步迁移到 Replaced → Archived。
    /// </remarks>
    Superseded = 2,

    /// <summary>已被 Consolidation ETL 标记为"待迁移"（已提取，正在写入归档 store）。</summary>
    /// <remarks>
    /// 中间状态：Consolidation ETL 失败时 item 保持此状态；下次 ETL 重试。
    /// 成功完成后状态推进为 Archived。
    /// </remarks>
    Replaced = 3,

    /// <summary>已归档（Consolidation ETL 完成；item 已在归档 store；active store 中可保留 stub）。</summary>
    /// <remarks>
    /// 终态：不可逆。如需"恢复"，应创建新 item 并通过 supersedes 关系反向指向原 Archived item。
    /// </remarks>
    Archived = 4
}

// ---------------------------------------------------------------------------
// SupersedeEventRecord 事件记录
// ---------------------------------------------------------------------------

/// <summary>
/// R21-1：supersede 事件记录。捕获"何时 / 何因 / 由谁 supersede"事件流。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 事件流不可变：一旦写入不可修改（append-only 语义）。
///   2. 同一 source item 可能有多次 supersede 事件（如 Active→Superseded→Replaced→Archived
///      分别记录 3 个事件）；查询时按 OccurredAt 排序取最新。
///   3. 不直接修改 item 状态；Consolidation ETL 消费事件流驱动状态迁移。
///   4. TargetItemId 可空：表示"无替换直接废弃"（如 obsolete 知识）。
/// </remarks>
public sealed record SupersedeEventRecord
{
    /// <summary>事件唯一 ID（如 "evt-{guid}"）。</summary>
    public required string EventId { get; init; }

    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（必填；跨集合时使用 "*"）。</summary>
    public required string CollectionId { get; init; }

    /// <summary>被 supersede 的 item ID（必填）。</summary>
    public required string SourceItemId { get; init; }

    /// <summary>取代的新 item ID（可空 = 无替换直接废弃）。</summary>
    public string? TargetItemId { get; init; }

    /// <summary>item 类型（必填；"context" / "memory" / "constraint" / "relation" / "vector"）。</summary>
    public required string ItemType { get; init; }

    /// <summary>新状态（必填；Superseded / Replaced / Archived 之一，不允许 Active）。</summary>
    public required SupersededItemState NewState { get; init; }

    /// <summary>supersede 原因码（如 "lifecycle-review" / "manual" / "version-bump" / "auto-detected" / "consolidation-etl"）。</summary>
    public required string Reason { get; init; }

    /// <summary>触发原因详情（人类可读；如 "deprecated by lifecycle review at 2026-07-20"）。</summary>
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
/// R21-1：supersede 事件查询条件。
/// </summary>
public sealed record SupersedeEventQuery
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
    public SupersededItemState? NewState { get; init; }

    /// <summary>仅返回 OccurredAt >= Since 的事件（可空 = 不限制）。</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>仅返回 OccurredAt &lt;= Until 的事件（可空 = 不限制）。</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>最大返回数量（默认 100；0 = 不限制）。</summary>
    public int Take { get; init; } = 100;
}

// ---------------------------------------------------------------------------
// ISupersededItemStore 接口
// ---------------------------------------------------------------------------

/// <summary>
/// R21-1：supersede 事件流存储接口。append-only 语义，记录 item supersede 事件。
/// </summary>
/// <remarks>
/// 实现层可注入 Postgres / InMemory / FileSystem store。
/// 接口契约最小化：
///   - AppendEventAsync：追加事件（不可变）。
///   - QueryEventsAsync：按条件查询事件。
///   - GetLatestStateAsync：查询 item 当前最新状态（按 OccurredAt 降序取首条 NewState）。
///   - GetRecentAsync：返回最近 N 条事件（按 OccurredAt 降序）。
/// </remarks>
public interface ISupersededItemStore
{
    /// <summary>追加 supersede 事件（不可变；重复 EventId 应抛 ArgumentException）。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task AppendEventAsync(
        SupersedeEventRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>按条件查询事件（按 OccurredAt 降序返回）。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<SupersedeEventRecord>> QueryEventsAsync(
        SupersedeEventQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>查询指定 item 的当前最新状态（按 OccurredAt 降序取首条 NewState）。</summary>
    /// <returns>最新事件；item 从未有 supersede 事件时返回 null（视为 Active）。</returns>
    [StoreOperation(StoreOperationKind.Read)]
    Task<SupersedeEventRecord?> GetLatestStateAsync(
        string workspaceId,
        string collectionId,
        string sourceItemId,
        CancellationToken cancellationToken = default);

    /// <summary>返回最近 N 条事件（按 OccurredAt 降序）。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<SupersedeEventRecord>> GetRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Consolidation ETL 契约
// ---------------------------------------------------------------------------

/// <summary>
/// R21-1：Consolidation ETL 请求。把 superseded items 从 active store 迁移到归档 store。
/// </summary>
/// <remarks>
/// ETL 流程：
///   1. Extract：从 ISupersededItemStore 查询符合条件的 Superseded 状态事件。
///   2. Transform：把对应 item 状态从 Superseded 推进到 Replaced（中间态），
///      写入新 SupersedeEventRecord（Reason="consolidation-etl"）。
///   3. Load：把 item 写入归档 store（IConsolidationArchiveStore，R21-3 实现），
///      推进状态到 Archived，写入最终 SupersedeEventRecord。
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
/// R21-1：Consolidation ETL 执行结果。
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
/// R21-1：Consolidation ETL 接口。把 superseded items 从 active store 迁移到归档 store。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. ETL 是幂等的：重复执行不会产生副作用（已 Archived 的 item 跳过）。
///   2. ETL 是可中断的：批次大小限制单次处理量；调用方可循环执行直到 ExtractedCount=0。
///   3. ETL 失败不破坏数据：Transform 阶段写入 Replaced 事件后失败，
///      下次 ETL 会从 Replaced 状态继续推进到 Archived。
///   4. ETL 不直接修改 active store 中的 item；只写入归档 store + 推进状态。
///      item 在 active store 中的实际删除由独立 GC 流程处理（R21-3）。
///   5. R21-1 阶段仅定义契约；具体实现（PostgresConsolidationETL）在 R21-3。
/// </remarks>
public interface IConsolidationETL
{
    /// <summary>执行一次 Consolidation ETL 迁移。</summary>
    /// <param name="request">ETL 请求（必填 WorkspaceId + CollectionId）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果（含 ExtractedCount / TransformedCount / LoadedCount / SkippedCount）。</returns>
    [StoreOperation(StoreOperationKind.Write)]
    Task<ConsolidationRunResult> RunAsync(
        ConsolidationRequest request,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// SupersededItemState 扩展方法
// ---------------------------------------------------------------------------

/// <summary>
/// R21-1：SupersededItemState 扩展方法。提供状态机判断逻辑。
/// </summary>
public static class SupersededItemStateExtensions
{
    /// <summary>判断状态是否为终态（不可逆）。</summary>
    public static bool IsTerminal(this SupersededItemState state)
        => state == SupersededItemState.Archived;

    /// <summary>判断状态是否允许推进到下一状态（状态机合法性检查）。</summary>
    public static bool CanTransitionTo(this SupersededItemState current, SupersededItemState next)
    {
        if (current == next) return false; // 自环不允许
        if (current == SupersededItemState.Unknown) return next != SupersededItemState.Unknown;
        if (current == SupersededItemState.Active) return next == SupersededItemState.Superseded;
        if (current == SupersededItemState.Superseded) return next == SupersededItemState.Replaced;
        if (current == SupersededItemState.Replaced) return next == SupersededItemState.Archived;
        // Archived 是终态，不允许推进
        return false;
    }

    /// <summary>判断状态是否需要 Consolidation ETL 处理（Superseded 或 Replaced）。</summary>
    public static bool NeedsConsolidation(this SupersededItemState state)
        => state == SupersededItemState.Superseded || state == SupersededItemState.Replaced;
}
