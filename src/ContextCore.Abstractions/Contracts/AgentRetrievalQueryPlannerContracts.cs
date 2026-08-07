namespace ContextCore.Abstractions;

// ===========================================================================
// Agent 受控检索查询规划器契约（Agent Retrieval Query Planner Contracts）
// 
// 目标：
// 在 Agent 执行循环中，将（原始任务、最新意图、Tool 观察、未解决目标、
// 上一轮检索诊断、Turn 预算）解析为<b>受控</b>的检索计划：
// 有界查询集 + 必需/排除 ID + 图种子 + Token 预算。
// 
// 设计原则：
// 1. 受控优先：无论输入多嘈杂，规划器只产出有界的查询集（MaxControlledQueries
// 上限），绝不让检索查询随对话膨胀为自由检索（uncontrolled）——每次检索的
// 查询数、Token 预算、图种子数都有硬上限。
// 2. 纯内存计算：规划器不调用任何存储 / 检索执行器，只输出计划；
// 检索执行由调用方（如 AgentRunActor 的 ContextBuilding 阶段）按计划驱动。
// 3. 确定性 / 幂等：相同输入产生相同计划（无随机性、无外部状态），
// 便于审计与回归测试。
// 4. 正交于决策引擎：本规划器不替代 IContextDecisionRuntime / IRetrievalRouter；
// 它回答的是"检索什么"（查询 + 约束），而非"候选如何排序/分配"。
// 5. 诊断回退：PreviousRetrievalDiagnostics 表明上一轮预算超限 / 命中率低时，
// 规划器执行受控回退（缩减 Token 预算、收敛查询），避免反复撞墙。
// ===========================================================================

/// <summary>
/// 检索查询类型。
/// </summary>
public enum AgentRetrievalQueryType
{
    /// <summary>关键词召回（FTS / BM25）。</summary>
    Keyword,

    /// <summary>向量召回（语义检索）。</summary>
    Vector,

    /// <summary>混合召回（关键词 + 向量融合）。</summary>
    Hybrid
}

/// <summary>
/// 单条受控检索查询。
/// </summary>
public sealed record AgentRetrievalQuery
{
    /// <summary>查询文本。</summary>
    public required string Text { get; init; }

    /// <summary>查询类型（关键词 / 向量 / 混合）。</summary>
    public AgentRetrievalQueryType Type { get; init; } = AgentRetrievalQueryType.Hybrid;

    /// <summary>相对权重（用于预算分配；值越大分配 Token 越多）。</summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>纳入本查询的原因（中文，便于审计）。</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// 上一轮检索的诊断信息（供规划器做受控回退）。
/// </summary>
public sealed record AgentRetrievalDiagnostic
{
    /// <summary>上一轮执行的查询文本。</summary>
    public required string QueryText { get; init; }

    /// <summary>返回的命中数。</summary>
    public int HitsReturned { get; init; }

    /// <summary>最高命中分数（null = 无命中）。</summary>
    public double? HighestScore { get; init; }

    /// <summary>是否超出 Token 预算（预算超限时规划器应回退收敛）。</summary>
    public bool BudgetExceeded { get; init; }

    /// <summary>人工可读备注（如"命中率低" / "空结果"）。</summary>
    public string? Note { get; init; }
}

/// <summary>
/// 受控检索规划器输入。
/// </summary>
public sealed record AgentRetrievalPlannerInput
{
    /// <summary>原始任务（Run 创建时的用户任务）。</summary>
    public required string OriginalTask { get; init; }

    /// <summary>最新 Assistant 意图（最近一次模型输出的文本；首轮可为 null）。</summary>
    public string? LatestAssistantIntent { get; init; }

    /// <summary>Tool 观察列表（成功/失败结果；失败观察用于推导排除 ID）。</summary>
    public IReadOnlyList<ToolObservation> ToolObservations { get; init; } = Array.Empty<ToolObservation>();

    /// <summary>尚未解决的目标列表（如多步任务中未完成子目标）。</summary>
    public IReadOnlyList<string> UnresolvedGoals { get; init; } = Array.Empty<string>();

    /// <summary>上一轮检索诊断（预算超限 / 命中率低时触发受控回退）。</summary>
    public IReadOnlyList<AgentRetrievalDiagnostic> PreviousRetrievalDiagnostics { get; init; } = Array.Empty<AgentRetrievalDiagnostic>();

    /// <summary>Turn 预算（剩余轮次决定 Token 预算上限；null = 未配置）。</summary>
    public AgentTurnBudget? TurnBudget { get; init; }

    // ── 租户隔离维度（自适应计划签名必须包含以下维度，防止跨 Workspace 污染）──

    /// <summary>工作区标识（签名必须包含 workspace，跨租户相同任务文本不得共享反馈状态）。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>集合标识（签名必须包含 collection）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>检索用途（签名必须包含 purpose）。</summary>
    public string? Purpose { get; init; }

    /// <summary>策略版本（签名必须包含 policy version，策略演进即隔离）。</summary>
    public string? PolicyVersion { get; init; }

    /// <summary>检索画像 / Provider profile（签名必须包含 retrieval profile）。</summary>
    public string? RetrievalProfile { get; init; }

    /// <summary>任务类别（签名必须包含 task class）。</summary>
    public string? TaskClass { get; init; }
}

/// <summary>
/// 受控检索计划（规划器输出）。
/// </summary>
public sealed record AgentRetrievalPlan
{
    /// <summary>受控查询集（有界：最多 MaxControlledQueries 条）。</summary>
    public IReadOnlyList<AgentRetrievalQuery> ControlledQueries { get; init; } = Array.Empty<AgentRetrievalQuery>();

    /// <summary>必需召回 ID（任务/意图中显式引用的实体 ID；mandatory recall）。</summary>
    public IReadOnlyList<string> RequiredIds { get; init; } = Array.Empty<string>();

    /// <summary>排除 ID（Tool 观察确认不存在的 ID，召回时过滤）。</summary>
    public IReadOnlyList<string> ExcludedIds { get; init; } = Array.Empty<string>();

    /// <summary>图种子（实体锚点，用于关系图扩展的起点；有界）。</summary>
    public IReadOnlyList<string> GraphSeeds { get; init; } = Array.Empty<string>();

    /// <summary>本轮检索 Token 预算（受控：由 Turn 预算推导并按上限收敛）。</summary>
    public int TokenBudget { get; init; }

    /// <summary>计划说明（中文，解释为何如此规划；用于审计与调试）。</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Agent 受控检索查询规划器抽象。
/// 将 <see cref="AgentRetrievalPlannerInput"/> 解析为受控的 <see cref="AgentRetrievalPlan"/>。
/// </summary>
/// <remarks>
/// <b>受控的含义</b>：规划器对以下维度施加硬上限，保证检索成本可控——
/// <list type="bullet">
/// <item>查询数：最多 <c>MaxControlledQueries</c> 条（默认 4），不随对话增长。</item>
/// <item>必需/排除 ID：最多各 <c>MaxRequiredIds</c> / <c>MaxExcludedIds</c> 条（默认 8）。</item>
/// <item>图种子：最多 <c>MaxGraphSeeds</c> 个（默认 6）。</item>
/// <item>Token 预算：由 Turn 预算推导，钳制在 [MinTokenBudget, MaxTokenBudget]（默认 512–8192）。</item>
/// </list>
/// 实现必须纯内存、确定性、幂等：相同输入产生相同计划。
/// </remarks>
public interface IAgentRetrievalQueryPlanner
{
    /// <summary>
    /// 规划本轮受控检索。
    /// </summary>
    /// <param name="input">规划输入（原始任务 / 最新意图 / Tool 观察 / 未解决目标 / 上一轮诊断 / Turn 预算）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受控检索计划（有界查询集 + 约束 ID + 图种子 + Token 预算 + 说明）。</returns>
    /// <exception cref="System.ArgumentNullException">input 为 null 时抛出。</exception>
    AgentRetrievalPlan Plan(AgentRetrievalPlannerInput input, CancellationToken cancellationToken = default);
}
