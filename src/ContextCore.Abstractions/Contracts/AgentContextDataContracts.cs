namespace ContextCore.Abstractions;

// ===========================================================================
// Agent Runtime 数据契约
//
// 目标（对齐用户规格第六节）：
//   定义 R23-1 中 AgentContextSnapshotRef.ContentJson 的实际结构，
//   以及 AgentTaskState / AgentContextDelta 等运行时数据模型。
//
// 设计原则：
//   1. 所有数据契约是不可变 record；保存后不可修改。
//   2. 数据契约不依赖具体 Agent SDK 对象模型；
//      Adapter 实现负责 SDK 对象 ↔ ContextCore 数据模型转换。
//   3. 数据契约由 IAgentWorkspaceContextProvider 实现产生；
//      ContextCore 内部接口（如 IContextPackageBuilder）填充内容。
//   4. JSON 序列化由调用方负责；ContextCore 仅定义对象模型。
//
// 与 R22 Bounded Context Orchestrator 的关系：
//   - AgentContextSnapshot 可包含 ContextDecisionResult + PackageQualityReport 引用 ID。
//   - AgentContextDelta 由 R22 修复操作产生（如修复后 snapshot 与原 snapshot 的差异）。
//   - 但 Agent 数据契约不直接引用 R22 类型（避免循环依赖）。
// ===========================================================================

/// <summary>
/// Agent Context Snapshot 数据。
/// 完整上下文快照，由 <see cref="IAgentWorkspaceContextProvider.GetContextSnapshotAsync"/> 产生。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. Snapshot 是不可变 record；同一 session 多次 snapshot 有不同 SnapshotId。
///   2. Snapshot 按 token 预算打包；ActualTokens &lt;= TokenBudget。
///   3. Sections 为 token-budget-aware 分区，每 section 独立计 token。
///   4. Snapshot 不包含 ContextCore 内部类型（如 ContextDecisionResult）；
///      仅包含引用 ID（如 DecisionRequestId），避免循环依赖。
/// </remarks>
public sealed record AgentContextSnapshot
{
    /// <summary>Snapshot 唯一 ID（如 "snap-{guid}"）。</summary>
    public required string SnapshotId { get; init; }

    /// <summary>所属 session。</summary>
    public required AgentSessionId Session { get; init; }

    /// <summary>Snapshot 创建时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>请求的 token 预算上限。</summary>
    public int TokenBudget { get; init; }

    /// <summary>实际使用的 token 数（≤ TokenBudget）。</summary>
    public int ActualTokens { get; init; }

    /// <summary>Snapshot 包含的 section 列表（按 token 预算打包）。</summary>
    public IReadOnlyList<AgentContextSection> Sections { get; init; }
        = Array.Empty<AgentContextSection>();

    /// <summary>关联的决策 ID 列表（引用 ContextDecisionResult.RequestId）。</summary>
    public IReadOnlyList<string> DecisionRequestIds { get; init; }
        = Array.Empty<string>();

    /// <summary>关联的约束 ID 列表（引用 IConstraintStore 中的约束）。</summary>
    public IReadOnlyList<string> ConstraintIds { get; init; }
        = Array.Empty<string>();

    /// <summary>Snapshot 包含的 tool 调用结果引用（toolCallId → toolName）。</summary>
    public IReadOnlyDictionary<string, string> ToolCallRefs { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Snapshot 元数据（摘要信息，如 section 计数、source 计数）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Snapshot 内容版本（用于序列化兼容性检查）。</summary>
    public string SchemaVersion { get; init; } = AgentContextSchemaVersions.SnapshotV1;
}

/// <summary>
/// Agent Context Section。Snapshot 内的单个分区。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. Section 按 token 预算独立打包；ActualTokens ≤ TokenBudget。
///   2. SectionName 由调用方定义（如 "system" / "user-preferences" / "task-context" /
///      "decisions" / "constraints" / "tool-results" / "relevant-context"）。
///   3. Content 为自由文本（已格式化为 LLM 可读字符串）；
///      ContextCore 不解析 Content 内部结构。
/// </remarks>
public sealed record AgentContextSection
{
    /// <summary>Section 名称（如 "system" / "task-context"）。</summary>
    public required string SectionName { get; init; }

    /// <summary>Section 排序序号（按 SortOrder 升序拼接）。</summary>
    public int SortOrder { get; init; }

    /// <summary>Section 分配的 token 预算上限。</summary>
    public int TokenBudget { get; init; }

    /// <summary>Section 实际使用的 token 数。</summary>
    public int ActualTokens { get; init; }

    /// <summary>Section 内容（自由文本，已格式化为 LLM 可读）。</summary>
    public required string Content { get; init; }

    /// <summary>Section 来源标识（如 "ContextCore.PackageBuilder" / "Agent.Injected"）。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Section 元数据。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Agent Task State。Agent 当前任务状态。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. TaskState 是不可变 record；任务状态变更通过新 record 表达（事件溯源模式）。
///   2. TaskState 不假设任务执行模型（如 ReAct / Plan-and-Execute / Tree of Thought）；
///      由 Agent 内部决定。
///   3. TaskState 由 IAgentSession 实现维护；ContextCore 不直接修改。
/// </remarks>
public sealed record AgentTaskState
{
    /// <summary>Task 唯一 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>所属 session。</summary>
    public required AgentSessionId Session { get; init; }

    /// <summary>Task 创建时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Task 最后更新时间（UTC）。</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Task 状态（自定义字符串，如 "planning" / "executing" / "completed" / "failed"）。</summary>
    public required string Status { get; init; }

    /// <summary>Task 描述（用户原始请求或 agent 内部计划）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>当前 turn ID（可选）。</summary>
    public string? CurrentTurnId { get; init; }

    /// <summary>已完成步骤数。</summary>
    public int CompletedSteps { get; init; }

    /// <summary>预计总步骤数（0 = 未知）。</summary>
    public int EstimatedSteps { get; init; }

    /// <summary>已消耗 token 数。</summary>
    public int ConsumedTokens { get; init; }

    /// <summary>关联的 context snapshot ID（最近一次打包）。</summary>
    public string? LastSnapshotId { get; init; }

    /// <summary>Task 错误信息（Status="failed" 时填充）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Task 元数据。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Agent Context Delta。两次 snapshot 之间的增量变更。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. Delta 是不可变 record；描述 FromSnapshotId → ToSnapshotId 之间的差异。
///   2. Delta 用于增量推送（避免每次都发送完整 snapshot）；
///      Agent 收到 Delta 后可在本地应用变更。
///   3. Delta 不包含内容本身，仅包含变更引用（item ID / section name）；
///      实际内容由 Agent 重新请求或从缓存读取。
///   4. Delta 可由 R22 修复操作产生（修复后 snapshot 与原 snapshot 的差异）。
/// </remarks>
public sealed record AgentContextDelta
{
    /// <summary>Delta 唯一 ID。</summary>
    public required string DeltaId { get; init; }

    /// <summary>所属 session。</summary>
    public required AgentSessionId Session { get; init; }

    /// <summary>起始 snapshot ID（变更前）。</summary>
    public required string FromSnapshotId { get; init; }

    /// <summary>目标 snapshot ID（变更后）。</summary>
    public required string ToSnapshotId { get; init; }

    /// <summary>Delta 创建时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>新增的 section 名称列表。</summary>
    public IReadOnlyList<string> AddedSections { get; init; }
        = Array.Empty<string>();

    /// <summary>修改的 section 名称列表（内容已变更）。</summary>
    public IReadOnlyList<string> ModifiedSections { get; init; }
        = Array.Empty<string>();

    /// <summary>删除的 section 名称列表。</summary>
    public IReadOnlyList<string> RemovedSections { get; init; }
        = Array.Empty<string>();

    /// <summary>新增的决策 ID 列表。</summary>
    public IReadOnlyList<string> AddedDecisionIds { get; init; }
        = Array.Empty<string>();

    /// <summary>删除的决策 ID 列表。</summary>
    public IReadOnlyList<string> RemovedDecisionIds { get; init; }
        = Array.Empty<string>();

    /// <summary>新增的约束 ID 列表。</summary>
    public IReadOnlyList<string> AddedConstraintIds { get; init; }
        = Array.Empty<string>();

    /// <summary>删除的约束 ID 列表。</summary>
    public IReadOnlyList<string> RemovedConstraintIds { get; init; }
        = Array.Empty<string>();

    /// <summary>新增的 tool 调用结果引用（toolCallId → toolName）。</summary>
    public IReadOnlyDictionary<string, string> AddedToolCallRefs { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Token 变化量（ToSnapshot.ActualTokens - FromSnapshot.ActualTokens）。</summary>
    public int TokenDelta { get; init; }

    /// <summary>Delta 来源（如 "agent-turn" / "tool-result-ingestion" / "context-injection" /
    /// "bounded-repair"）。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Delta 元数据。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Agent Context Schema 版本。集中管理所有数据契约的 schema 版本。
/// </summary>
public static class AgentContextSchemaVersions
{
    /// <summary>AgentContextSnapshot schema 版本。</summary>
    public const string SnapshotV1 = "agent-context-snapshot/1.0";

    /// <summary>AgentTaskState schema 版本。</summary>
    public const string TaskStateV1 = "agent-task-state/1.0";

    /// <summary>AgentContextDelta schema 版本。</summary>
    public const string DeltaV1 = "agent-context-delta/1.0";
}
