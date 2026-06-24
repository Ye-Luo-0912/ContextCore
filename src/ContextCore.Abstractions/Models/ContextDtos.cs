namespace ContextCore.Abstractions.Models;

/// <summary>
/// Specifies the format of the content stored in a context item.
/// </summary>
public enum ContextContentFormat
{
    /// <summary>Plain text format.</summary>
    PlainText,
    /// <summary>Markdown formatted text.</summary>
    Markdown,
    /// <summary>JSON format.</summary>
    Json,
    /// <summary>YAML format.</summary>
    Yaml,
    /// <summary>XML format.</summary>
    Xml,
    /// <summary>HTML format.</summary>
    Html,
    /// <summary>Reference to binary data.</summary>
    BinaryRef,
    /// <summary>Custom format.</summary>
    Custom
}

/// <summary>
/// Represents an item of context within a workspace.
/// </summary>
public sealed class ContextItem
{
    /// <summary>
    /// Gets the unique identifier of the context item.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the unique identifier of the workspace the item belongs to.
    /// </summary>
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string? Title { get; init; }

    public string Content { get; init; } = string.Empty;

    public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Refs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new();

    public double Importance { get; init; }

    public long Version { get; init; }

    public string? Checksum { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>上下文集合的元数据，集合是工作区下隔离存储与查询的基本单位。</summary>
public sealed class ContextCollection
{
    public string Id { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>上下文条目的查询条件。</summary>
public sealed class ContextQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? QueryText { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExcludedTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExcludedIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Refs { get; init; } = Array.Empty<string>();

    public int Skip { get; init; }

    public int Take { get; init; } = 50;

    public bool IncludeContent { get; init; } = true;

    public bool IncludeDerived { get; init; } = true;
}

/// <summary>Service / Client 使用的稳定查询请求 DTO，保持与 <see cref="ContextQuery"/> 字段对齐。</summary>
public sealed class ContextQueryRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? QueryText { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExcludedTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExcludedIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Refs { get; init; } = Array.Empty<string>();

    public int Skip { get; init; }

    public int Take { get; init; } = 50;

    public bool IncludeContent { get; init; } = true;

    public bool IncludeDerived { get; init; } = true;

    public ContextQuery ToContextQuery()
    {
        return new ContextQuery
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = QueryText,
            Tags = Tags,
            Types = Types,
            ExcludedTypes = ExcludedTypes,
            ExcludedIds = ExcludedIds,
            Refs = Refs,
            Skip = Skip,
            Take = Take,
            IncludeContent = IncludeContent,
            IncludeDerived = IncludeDerived
        };
    }
}

/// <summary>稳定查询响应 DTO，封装上下文条目列表。</summary>
public sealed class ContextQueryResponse
{
    public IReadOnlyList<ContextItem> Items { get; init; } = Array.Empty<ContextItem>();

    public int Count { get; init; }
}

/// <summary>构建后供 LLM 或下游任务消费的结构化上下文包。</summary>
public sealed class ContextPackage
{
    public string PackageId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public IReadOnlyList<ContextPackageSection> Sections { get; init; } = Array.Empty<ContextPackageSection>();

    public int EstimatedTokens { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>一次上下文包构建的完整结果，包含最终包和可解释的选取/丢弃决策。</summary>
public sealed class ContextPackageBuildResult
{
    public string BuildId { get; init; } = string.Empty;

    public ContextPackage Package { get; init; } = new();

    public IReadOnlyList<ContextPackageDecision> SelectedItems { get; init; } = Array.Empty<ContextPackageDecision>();

    public IReadOnlyList<DroppedContextItem> DroppedItems { get; init; } = Array.Empty<DroppedContextItem>();

    /// <summary>本次打包识别出的不确定性，用于解释风险、预算压力和证据缺口。</summary>
    public IReadOnlyList<ContextPackageUncertainty> Uncertainties { get; init; } = Array.Empty<ContextPackageUncertainty>();

    /// <summary>本次打包的 token 预算使用情况。</summary>
    public ContextPackageBudgetReport Budget { get; init; } = new();

    /// <summary>按标准 schema 组织后的上下文包视图，便于 API、ControlRoom 和测试稳定消费。</summary>
    public ContextPackageStandardOutput Output { get; init; } = new();

    public int TokenBudget { get; init; }

    public int EstimatedTokens { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>本次打包构建的短期锚定召回计划，用于 trace 可视化和后续 HybridContextRetriever 调用。</summary>
    public RetrievalPlan? Plan { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>上下文包的标准输出 schema，对原始 sections 做稳定分组，不替代原始包结构。</summary>
public sealed class ContextPackageStandardOutput
{
    public ContextPackageOutputItem? CurrentTask { get; init; }

    public IReadOnlyList<ContextPackageOutputItem> RecentContext { get; init; } = Array.Empty<ContextPackageOutputItem>();

    public IReadOnlyList<ContextPackageOutputItem> WorkingState { get; init; } = Array.Empty<ContextPackageOutputItem>();

    public IReadOnlyList<ContextPackageOutputItem> StableBackground { get; init; } = Array.Empty<ContextPackageOutputItem>();

    public IReadOnlyList<ContextPackageOutputItem> Constraints { get; init; } = Array.Empty<ContextPackageOutputItem>();

    public IReadOnlyList<ContextPackageOutputItem> Entities { get; init; } = Array.Empty<ContextPackageOutputItem>();

    public IReadOnlyList<ContextPackageOutputItem> Relations { get; init; } = Array.Empty<ContextPackageOutputItem>();

    public IReadOnlyList<ContextPackageOutputItem> Evidence { get; init; } = Array.Empty<ContextPackageOutputItem>();

    public IReadOnlyList<DroppedContextItem> Excluded { get; init; } = Array.Empty<DroppedContextItem>();

    public IReadOnlyList<ContextPackageUncertainty> Uncertainties { get; init; } = Array.Empty<ContextPackageUncertainty>();

    public ContextPackageBudgetReport Budget { get; init; } = new();
}

/// <summary>标准输出 schema 中的一条内容片段，保留原 section 的来源、条目和预算信息。</summary>
public sealed class ContextPackageOutputItem
{
    public string SectionName { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ItemRefs { get; init; } = Array.Empty<string>();

    public int EstimatedTokens { get; init; }
}

/// <summary>上下文包构建过程中发现的一条不确定性或风险信号。</summary>
public sealed class ContextPackageUncertainty
{
    public string Code { get; init; } = string.Empty;

    public string Severity { get; init; } = "Info";

    public string Message { get; init; } = string.Empty;

    public string SectionName { get; init; } = string.Empty;

    public IReadOnlyList<string> ItemRefs { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>上下文包的整体预算报告。</summary>
public sealed class ContextPackageBudgetReport
{
    public int TokenBudget { get; init; }

    public int UsedTokens { get; init; }

    public int RemainingTokens { get; init; }

    public double UsageRatio { get; init; }

    public double WasteRatio { get; init; }

    public IReadOnlyList<ContextPackageSectionBudget> Sections { get; init; } = Array.Empty<ContextPackageSectionBudget>();
}

/// <summary>上下文包单个 section 的预算使用情况。</summary>
public sealed class ContextPackageSectionBudget
{
    public string SectionName { get; init; } = string.Empty;

    public int AllocatedTokens { get; init; }

    public int UsedTokens { get; init; }

    public double UsageRatio { get; init; }
}

/// <summary>记录一个被选入上下文包的候选项及其选中原因。</summary>
public sealed class ContextPackageDecision
{
    public string ItemId { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string SectionName { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public double Score { get; init; }

    public int EstimatedTokens { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>详细评分明细（13 个子分维度），可用于 PackageBuildTrace 可观测输出。Working Memory 项会填充此字段。</summary>
    public ItemScoreBreakdown? ScoreBreakdown { get; init; }
}

/// <summary>记录一个候选项未被选入上下文包的原因。</summary>
public sealed class DroppedContextItem
{
    public string ItemId { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public double Score { get; init; }

    public int EstimatedTokens { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>上下文包中的一个有序内容段。</summary>
public sealed class ContextPackageSection
{
    public string Name { get; init; } = string.Empty;

    public int Priority { get; init; }

    public string Content { get; init; } = string.Empty;

    public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ItemRefs { get; init; } = Array.Empty<string>();

    public int EstimatedTokens { get; init; }
}

/// <summary>锚点类型，用于描述当前任务从输入和短期上下文中提取出的关键线索。</summary>
public enum AnchorType
{
    Entity,
    Topic,
    Task,
    Constraint,
    Intent,
    Project,
    TimeRange,
    Mode
}

/// <summary>上下文打包锚点，作为中期记忆召回和图谱扩展的轻量输入。</summary>
public sealed record ContextAnchor(
    string Name,
    AnchorType Type,
    double Weight,
    string Source,
    IReadOnlyList<string> Aliases);

/// <summary>短期上下文筛选结果，保留选中或排除的原因，供 package trace 和 ControlRoom 解释。</summary>
public sealed class RecentContextItem
{
    public string SourceItemId { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

    public string? SourceTurnId { get; init; }

    public double Relevance { get; init; }

    public double RecencyWeight { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string? ExcludeReason { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();
}

/// <summary>描述对上下文条目的建议性修改。</summary>
public sealed class ContextPatch
{
    public string TargetId { get; init; } = string.Empty;

    public string Operation { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string? ValueJson { get; init; }

    public string? Reason { get; init; }

    public double Confidence { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();
}

/// <summary>上下文索引条目，用于从关键词或提示线索定位相关上下文。</summary>
public sealed class ContextIndexEntry
{
    public string Id { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public IReadOnlyList<string> ContextRefs { get; init; } = Array.Empty<string>();

    public double Weight { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 上下文打包场景模式，决定各记忆层的权重分配和节的默认优先级。
/// 当指定 Mode 时，打包器将使用对应的预设 token 预算比例；
/// 如果同时指定了 <c>SectionTokenBudgets</c> 或 <c>TokenBudget</c>，显式值优先。
/// </summary>
public enum ContextPackageMode
{
    /// <summary>不指定模式，使用策略或请求中的显式参数。</summary>
    None = 0,

    /// <summary>
    /// 聊天模式：近期对话权重最高，长期记忆仅作偏好补充。
    /// 默认预算 2400 tokens，recent_context(28%) / working_memory(24%) 为主体。
    /// </summary>
    Chat = 1,

    /// <summary>
    /// 小说创作模式：稳定世界观/人设优先，近期章节为对话支撑。
    /// 默认预算 6000 tokens，stable_memory(34%) / global_context(24%) 为主体。
    /// </summary>
    Novel = 2,

    /// <summary>
    /// 自动化/工具模式：当前任务步骤与失败记录最高，用户偏好/安全边界次之。
    /// 默认预算 3200 tokens，working_memory(30%) / recent_context(22%) 为主体。
    /// </summary>
    Automation = 3,

    /// <summary>
    /// 编码工作流记忆管理模式：项目上下文、任务状态、约束注入、工具交接。
    /// 默认预算 4000 tokens，working_memory(28%) / stable_memory(22%) 为主体。
    /// </summary>
    Coding = 4,
}

/// <summary>索引查询条件。</summary>
public sealed class IndexQuery
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string? CollectionId { get; init; }

    public string? Key { get; init; }

    public string? Kind { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public int Take { get; init; } = 50;
}

/// <summary>上下文打包请求，指定工作区、集合、过滤条件、预算和可选策略。</summary>
public sealed class ContextPackageRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? QueryText { get; init; }

    public IReadOnlyList<string> RequiredTags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredTypes { get; init; } = Array.Empty<string>();

    public int TokenBudget { get; init; }

    public bool IncludeRecent { get; init; } = true;

    /// <summary>场景模式，优先级高于 metadata["mode"]。指定后按预设权重分配 token 预算。</summary>
    public ContextPackageMode Mode { get; init; } = ContextPackageMode.None;

    public ContextPackagePolicy? Policy { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new();
}
