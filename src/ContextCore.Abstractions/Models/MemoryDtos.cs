namespace ContextCore.Abstractions.Models;

/// <summary>记忆条目所处的层级，表示信息的成熟程度与可信度。</summary>
public enum ContextMemoryLayer
{
    /// <summary>原始层，直接采集的未加工信息。</summary>
    Raw,
    /// <summary>工作记忆层，当前会话活跃使用的信息。</summary>
    Working,
    /// <summary>结构化层，已整理归纳的信息。</summary>
    Structured,
    /// <summary>稳定层，经过验证可长期保留的信息。</summary>
    Stable,
    /// <summary>全局层，跨集合共享的信息。</summary>
    Global,
    /// <summary>约束层，规则与限制条件。</summary>
    Constraint
}

/// <summary>记忆条目的验证与生命周期状态。</summary>
public enum ContextMemoryStatus
{
    /// <summary>候选状态，等待验证。</summary>
    Candidate,
    /// <summary>活跃状态，当前会话任务活跃使用。</summary>
    Active,
    /// <summary>已验证，可信度较高。</summary>
    Verified,
    /// <summary>稳定状态，长期有效。</summary>
    Stable,
    /// <summary>已废弃，不再推荐使用。</summary>
    Deprecated,
    /// <summary>已拒绝，不可用。</summary>
    Rejected
}

/// <summary>上下文信息的作用范围。</summary>
public enum ContextScope
{
    /// <summary>工作空间范围。</summary>
    Workspace,
    /// <summary>集合范围。</summary>
    Collection,
    /// <summary>会话范围。</summary>
    Session,
    /// <summary>任务范围。</summary>
    Task,
    /// <summary>单条目范围。</summary>
    Item
}

/// <summary>约束条件的强制级别。</summary>
public enum ConstraintLevel
{
    /// <summary>硬约束，必须遵守。</summary>
    Hard,
    /// <summary>软约束，尽量遵守。</summary>
    Soft,
    /// <summary>运行时约束，仅在运行时生效。</summary>
    Runtime,
    /// <summary>系统级约束。</summary>
    System,
    /// <summary>用户自定义约束。</summary>
    User,
    /// <summary>领域知识约束。</summary>
    Domain
}

/// <summary>表示两个上下文条目之间的有向关系。</summary>
public sealed class ContextRelation
{
    /// <summary>关系唯一标识符。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>来源条目 ID。</summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>目标条目 ID。</summary>
    public string TargetId { get; init; } = string.Empty;

    /// <summary>关系类型名称（如 "references"、"derives-from"）。</summary>
    public string RelationType { get; init; } = string.Empty;

    /// <summary>关系权重，值越大越重要。</summary>
    public double Weight { get; init; }

    /// <summary>置信度，范围 0～1。</summary>
    public double Confidence { get; init; }

    /// <summary>来源引用 ID 列表。</summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>表示作用于工作空间或集合的约束规则。</summary>
public sealed class ContextConstraint
{
    /// <summary>约束唯一标识符。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID（为空时表示工作空间级约束）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>约束作用范围。</summary>
    public ContextScope Scope { get; init; } = ContextScope.Collection;

    /// <summary>约束级别。</summary>
    public ConstraintLevel Level { get; init; } = ConstraintLevel.Soft;

    /// <summary>约束内容描述。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>该约束适用的条目 ID 列表。</summary>
    public IReadOnlyList<string> AppliesToRefs { get; init; } = Array.Empty<string>();

    /// <summary>来源引用 ID 列表。</summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    /// <summary>当前验证状态。</summary>
    public ContextMemoryStatus Status { get; init; } = ContextMemoryStatus.Candidate;

    /// <summary>置信度，范围 0～1。</summary>
    public double Confidence { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>记忆层中的单条记忆条目。</summary>
public sealed class ContextMemoryItem
{
    /// <summary>条目唯一标识符。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>记忆层级。</summary>
    public ContextMemoryLayer Layer { get; init; } = ContextMemoryLayer.Working;

    /// <summary>当前验证状态。</summary>
    public ContextMemoryStatus Status { get; init; } = ContextMemoryStatus.Candidate;

    /// <summary>条目类型。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>内容文本。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>内容格式。</summary>
    public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

    /// <summary>标签列表。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>来源引用 ID 列表。</summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    /// <summary>关联关系 ID 列表，用于记忆固化时保留关系线索。</summary>
    public IReadOnlyList<string> RelationRefs { get; init; } = Array.Empty<string>();

    /// <summary>重要性分数。</summary>
    public double Importance { get; init; }

    /// <summary>置信度，范围 0～1。</summary>
    public double Confidence { get; init; }

    /// <summary>版本号。</summary>
    public long Version { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>工作记忆中的短期条目，表示当前任务窗口内仍活跃的信息。</summary>
public sealed class WorkingMemoryItem
{
    /// <summary>条目唯一标识符。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>工作记忆条目类型。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>内容文本。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>内容格式。</summary>
    public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

    /// <summary>标签列表。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>来源引用 ID 列表。</summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    /// <summary>关联关系 ID 列表。</summary>
    public IReadOnlyList<string> RelationRefs { get; init; } = Array.Empty<string>();

    /// <summary>重要性分数。</summary>
    public double Importance { get; init; }

    /// <summary>置信度，范围 0～1。</summary>
    public double Confidence { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>当前集合的活跃上下文快照，用于描述任务窗口中正在被引用的记忆和上下文。</summary>
public sealed class WorkingMemoryActiveContext
{
    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>当前任务 ID，可为空。</summary>
    public string? CurrentTaskId { get; init; }

    /// <summary>活跃上下文摘要。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>当前活跃的工作记忆条目 ID。</summary>
    public IReadOnlyList<string> MemoryRefs { get; init; } = Array.Empty<string>();

    /// <summary>当前活跃的原始上下文条目 ID。</summary>
    public IReadOnlyList<string> ContextRefs { get; init; } = Array.Empty<string>();

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>最后更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>当前集合正在处理的任务信息。</summary>
public sealed class WorkingMemoryCurrentTask
{
    /// <summary>任务唯一标识符。</summary>
    public string TaskId { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>任务标题。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>任务描述。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>任务状态。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>任务标签。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>跨集合共享的全局上下文条目。</summary>
public sealed class ContextGlobalItem
{
    /// <summary>条目唯一标识符。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>关联集合 ID（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>作用范围。</summary>
    public ContextScope Scope { get; init; } = ContextScope.Workspace;

    /// <summary>条目类型。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>内容文本。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>内容格式。</summary>
    public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

    /// <summary>标签列表。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>来源引用 ID 列表。</summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    /// <summary>重要性分数。</summary>
    public double Importance { get; init; }

    /// <summary>版本号。</summary>
    public long Version { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>记录记忆条目晋升（或降级）操作的历史记录。</summary>
public sealed class ContextPromotionRecord
{
    /// <summary>记录唯一标识符。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>被操作的记忆条目 ID。</summary>
    public string SourceMemoryId { get; init; } = string.Empty;

    /// <summary>操作前的状态。</summary>
    public ContextMemoryStatus FromStatus { get; init; }

    /// <summary>操作后的状态。</summary>
    public ContextMemoryStatus ToStatus { get; init; }

    /// <summary>使用的晋升策略名称。</summary>
    public string Strategy { get; init; } = string.Empty;

    /// <summary>审核人或执行人。</summary>
    public string? Reviewer { get; init; }

    /// <summary>操作后的目标记忆层。</summary>
    public ContextMemoryLayer TargetLayer { get; init; }

    /// <summary>操作时保留的来源引用 ID 列表。</summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    /// <summary>操作时保留的关系引用 ID 列表。</summary>
    public IReadOnlyList<string> RelationRefs { get; init; } = Array.Empty<string>();

    /// <summary>操作原因说明（可选）。</summary>
    public string? Reason { get; init; }

    /// <summary>置信度，范围 0～1。</summary>
    public double Confidence { get; init; }

    /// <summary>操作时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>描述记忆固化策略的配置模型。</summary>
public sealed class PromotionStrategy
{
    /// <summary>策略唯一标识符。</summary>
    public string Id { get; init; } = "manual";

    /// <summary>策略显示名称。</summary>
    public string Name { get; init; } = "Manual";

    /// <summary>策略描述。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>默认置信度，范围 0～1。</summary>
    public double DefaultConfidence { get; init; } = 1.0;

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>上下文打包策略，控制打包时各部分内容的包含规则与 Token 预算。</summary>
public sealed class ContextPackagePolicy
{
    /// <summary>策略唯一标识符。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>策略显示名称。</summary>
    public string Name { get; init; } = "Default";

    /// <summary>策略描述。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>场景模式，优先级高于 metadata["mode"]。指定后按预设权重分配 token 预算。</summary>
    public ContextPackageMode Mode { get; init; } = ContextPackageMode.None;

    /// <summary>Token 总预算上限。</summary>
    public int TokenBudget { get; init; }

    /// <summary>是否包含全局上下文。</summary>
    public bool IncludeGlobalContext { get; init; } = true;

    /// <summary>是否包含硬约束。</summary>
    public bool IncludeHardConstraints { get; init; } = true;

    /// <summary>是否包含软约束。</summary>
    public bool IncludeSoftConstraints { get; init; } = true;

    /// <summary>是否包含工作记忆。</summary>
    public bool IncludeWorkingMemory { get; init; } = true;

    /// <summary>是否包含稳定记忆。</summary>
    public bool IncludeStableMemory { get; init; } = true;

    /// <summary>是否包含最近原始上下文。</summary>
    public bool IncludeRecentRawContext { get; init; } = true;

    /// <summary>最多包含的最近条目数量，默认 20。</summary>
    public int MaxRecentItems { get; init; } = 20;

    /// <summary>各节的顺序配置（按声明顺序排序；未列出的节按优先级排序）。</summary>
    public IReadOnlyList<string> SectionOrder { get; init; } = Array.Empty<string>();

    /// <summary>各节的优先级配置（节名 → 优先级）。</summary>
    public Dictionary<string, int> SectionPriorities { get; init; } = new();

    /// <summary>各节的 Token 预算配置（节名 → 最大 Token 数）。</summary>
    public Dictionary<string, int> SectionTokenBudgets { get; init; } = new();

    /// <summary>策略附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>上下文包策略查询条件。</summary>
public sealed class ContextPackagePolicyQuery
{
    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>按名称或描述模糊筛选。</summary>
    public string? QueryText { get; init; }

    /// <summary>最大返回数量。</summary>
    public int Take { get; init; } = 50;
}
/// <summary>约束查询条件。</summary>
public sealed class ContextConstraintQuery
{
    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>筛选指定集合（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>筛选指定作用范围（可选）。</summary>
    public ContextScope? Scope { get; init; }

    /// <summary>筛选指定约束级别（可选）。</summary>
    public ConstraintLevel? Level { get; init; }

    /// <summary>筛选指定状态（可选）。</summary>
    public ContextMemoryStatus? Status { get; init; }

    /// <summary>筛选适用于指定条目 ID 的约束。</summary>
    public IReadOnlyList<string> AppliesToRefs { get; init; } = Array.Empty<string>();

    /// <summary>最多返回的记录数，默认 50。</summary>
    public int Take { get; init; } = 50;
}

/// <summary>记忆条目查询条件。</summary>
public sealed class ContextMemoryQuery
{
    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>筛选指定集合（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>筛选指定记忆层（可选）。</summary>
    public ContextMemoryLayer? Layer { get; init; }

    /// <summary>筛选指定状态（可选）。</summary>
    public ContextMemoryStatus? Status { get; init; }

    /// <summary>必须包含的标签列表。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>筛选指定类型列表。</summary>
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();

    /// <summary>筛选来源引用 ID 列表。</summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    /// <summary>跳过的记录数（分页）。</summary>
    public int Skip { get; init; }

    /// <summary>最多返回的记录数，默认 50。</summary>
    public int Take { get; init; } = 50;
}

/// <summary>全局上下文条目查询条件。</summary>
public sealed class ContextGlobalQuery
{
    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>筛选指定集合（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>筛选指定作用范围（可选）。</summary>
    public ContextScope? Scope { get; init; }

    /// <summary>必须包含的标签列表。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>筛选指定类型列表。</summary>
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();

    /// <summary>最多返回的记录数，默认 50。</summary>
    public int Take { get; init; } = 50;
}

/// <summary>关系查询条件。</summary>
public sealed class ContextRelationQuery
{
    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>筛选指定集合（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>筛选指定来源条目 ID（可选）。</summary>
    public string? SourceId { get; init; }

    /// <summary>筛选指定目标条目 ID（可选）。</summary>
    public string? TargetId { get; init; }

    /// <summary>筛选与指定条目相关的所有入边和出边（可选）。</summary>
    public string? ItemId { get; init; }

    /// <summary>筛选指定关系类型（可选）。</summary>
    public string? RelationType { get; init; }

    /// <summary>最多返回的记录数，默认 50。</summary>
    public int Take { get; init; } = 50;
}
