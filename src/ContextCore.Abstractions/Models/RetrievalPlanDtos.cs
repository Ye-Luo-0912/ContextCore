namespace ContextCore.Abstractions.Models;

/// <summary>检索锚点在短期锚定召回计划中扮演的角色。</summary>
public enum RetrievalAnchorRole
{
    /// <summary>主锚点：最直接命中当前任务意图的条目。Working Memory 优先召回。</summary>
    Primary,

    /// <summary>支撑锚点：相关背景和辅助信息，提升稳定记忆层召回的信号。</summary>
    Support,

    /// <summary>负锚点：应排除或降权的内容信号。</summary>
    Negative,

    /// <summary>审计锚点：存在时允许废弃/历史条目进入 historical 区块。</summary>
    Audit,

    /// <summary>冲突锚点：存在时允许被替代/冲突条目进入 conflict 区块。</summary>
    Conflict
}

/// <summary>带角色的召回锚点条目，承载对应原始锚点的检索角色信号。</summary>
public sealed record RetrievalAnchorEntry(
    string Name,
    RetrievalAnchorRole Role,
    double Weight,
    string Source,
    AnchorType AnchorType);

/// <summary>短期上下文快照，捕获用于构建召回计划的即时上下文状态。</summary>
public sealed class ShortTermSnapshot
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? CurrentQueryText { get; init; }

    public IReadOnlyList<RecentContextItem> RecentItems { get; init; } = Array.Empty<RecentContextItem>();

    public IReadOnlyList<ContextAnchor> Anchors { get; init; } = Array.Empty<ContextAnchor>();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 短期锚定召回计划（Short-term Anchored Retrieval Plan）。
/// 第一版作为 adapter / trace / intent carrier，不引入 LLM planner，
/// 不改 embedding，只影响召回优先级和过滤策略。
/// </summary>
public sealed class RetrievalPlan
{
    public string PlanId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? QueryText { get; init; }

    /// <summary>主锚点：Working Memory 召回优先匹配这些项，每匹配一项得分 +1.0（最高 +2.0）。</summary>
    public IReadOnlyList<RetrievalAnchorEntry> PrimaryAnchors { get; init; } = Array.Empty<RetrievalAnchorEntry>();

    /// <summary>支撑锚点：辅助召回的背景信号，触发 NeedsStableMemory。</summary>
    public IReadOnlyList<RetrievalAnchorEntry> SupportAnchors { get; init; } = Array.Empty<RetrievalAnchorEntry>();

    /// <summary>负锚点：应降权或排除的内容信号（当前版本仅记录，不主动降权）。</summary>
    public IReadOnlyList<RetrievalAnchorEntry> NegativeAnchors { get; init; } = Array.Empty<RetrievalAnchorEntry>();

    /// <summary>审计锚点：存在时允许废弃/历史条目进入召回结果。</summary>
    public IReadOnlyList<RetrievalAnchorEntry> AuditAnchors { get; init; } = Array.Empty<RetrievalAnchorEntry>();

    /// <summary>冲突锚点：存在时允许被替代/冲突条目进入召回结果。</summary>
    public IReadOnlyList<RetrievalAnchorEntry> ConflictAnchors { get; init; } = Array.Empty<RetrievalAnchorEntry>();

    /// <summary>
    /// 是否需要增强稳定记忆层召回。
    /// false 时，HybridContextRetriever 将跳过 Stable Memory 查询（仅当 Plan 不为空时生效）。
    /// </summary>
    public bool NeedsStableMemory { get; init; }

    /// <summary>是否需要审计历史（废弃/遗留）内容。</summary>
    public bool NeedsAuditHistory { get; init; }

    /// <summary>是否需要冲突证据（被替代/竞争方案）。</summary>
    public bool NeedsConflictEvidence { get; init; }

    /// <summary>应从召回结果中排除的条目状态列表（如 "deprecated", "rejected"）。</summary>
    public IReadOnlyList<string> ExcludedStatuses { get; init; } = Array.Empty<string>();

    /// <summary>关联的短期快照，用于 trace 可视化和审计。</summary>
    public ShortTermSnapshot? Snapshot { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
