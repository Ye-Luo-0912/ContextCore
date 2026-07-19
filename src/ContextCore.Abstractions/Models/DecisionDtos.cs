namespace ContextCore.Abstractions.Models;

/// <summary>
/// 统一的上下文决策记录 DTO。
/// V17.0 引入：在不改变 retrieval/package/planning/PackingPolicy/attention/constraints/vector formal runtime
/// 的前提下，把已有的 selected/dropped/context plan 信息投影为只读 decision trace artifact。
/// 该记录本身不触发任何运行时变更，所有 <see cref="ContextDecisionRisk"/> 标志位恒为 false。
/// </summary>
public sealed class ContextDecisionRecord
{
    /// <summary>决策记录唯一标识，通常复用 buildId / retrievalId。</summary>
    public string DecisionId { get; init; } = string.Empty;

    /// <summary>决策来源：Package 或 Retrieval。</summary>
    public ContextDecisionSource Source { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    /// <summary>本次决策关联的查询文本（可能为空）。</summary>
    public string? QueryText { get; init; }

    /// <summary>投影自 selected/dropped 的候选决策列表。</summary>
    public IReadOnlyList<ContextDecisionCandidate> Candidates { get; init; } = Array.Empty<ContextDecisionCandidate>();

    /// <summary>本次决策的整体产出摘要（计数、token、section）。</summary>
    public ContextDecisionOutcome Outcome { get; init; } = new();

    /// <summary>非激活契约：所有标志位恒为 false，仅用于审计断言。</summary>
    public ContextDecisionRisk Risk { get; init; } = new();

    /// <summary>策略版本，用于 trace 兼容性识别。</summary>
    public string PolicyVersion { get; init; } = ContextDecisionPolicyVersions.V17_0;

    public Dictionary<string, string> Metadata { get; init; } = new();

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>决策记录来源类型。</summary>
public enum ContextDecisionSource
{
    Package = 0,
    Retrieval = 1
}

/// <summary>单个候选的选中/丢弃决策投影。</summary>
public sealed record ContextDecisionCandidate
{
    /// <summary>候选条目 ID（package 侧为 itemId，retrieval 侧为 candidateId 或 sourceId）。</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>条目来源类型（ContextItem / MemoryItem）。</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>条目业务类型。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>该候选的决策结果：Selected 或 Dropped。</summary>
    public ContextDecisionCandidateOutcome Outcome { get; init; }

    /// <summary>归属的 section 名称（package 场景）。</summary>
    public string SectionName { get; init; } = string.Empty;

    /// <summary>选中或丢弃原因（V17.0 自由文本，保留向后兼容）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// R14-1：主决策原因码。替代 <see cref="Reason"/> 自由文本的机器可解析版本。
    /// V17.0 历史 trace 升级路径设为 <see cref="CandidateDecisionReasonCode.Unknown"/>。
    /// </summary>
    public CandidateDecisionReasonCode ReasonCode { get; init; } = CandidateDecisionReasonCode.Unknown;

    /// <summary>
    /// R14-1：次要决策原因码列表。同一候选可能命中多个原因。
    /// </summary>
    public IReadOnlyList<CandidateDecisionReasonCode> SecondaryReasonCodes { get; init; } = Array.Empty<CandidateDecisionReasonCode>();

    /// <summary>
    /// R14-1：候选来源 channel 列表（如 "recent_context"、"working_memory"）。
    /// 默认为空列表；由 V2 证据提供者填充。
    /// </summary>
    public IReadOnlyList<string> ChannelSources { get; init; } = Array.Empty<string>();

    /// <summary>
    /// R14-1：候选评分细分（key = 评分维度名）。默认为空字典。
    /// </summary>
    public IReadOnlyDictionary<string, double> ScoreBreakdown { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>
    /// R14-1：候选命中的 anchor 列表。默认为空列表。
    /// </summary>
    public IReadOnlyList<string> MatchedAnchors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// R14-1：候选参与的关系路径列表。默认为空列表。
    /// </summary>
    public IReadOnlyList<string> RelationPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// R14-1：候选 lifecycle 状态（如 "active" / "deprecated" / "superseded"）。默认 "active"。
    /// </summary>
    public string LifecycleState { get; init; } = "active";

    /// <summary>R14-1：候选在排序前的位置（0-based）；-1 表示未提供。</summary>
    public int RankBefore { get; init; } = -1;

    /// <summary>R14-1：候选在排序后的位置（0-based）；-1 表示未提供。</summary>
    public int RankAfter { get; init; } = -1;

    /// <summary>R14-1：决策前剩余 token 预算；-1 表示未提供。</summary>
    public int TokenBudgetBefore { get; init; } = -1;

    /// <summary>R14-1：决策后剩余 token 预算；-1 表示未提供。</summary>
    public int TokenBudgetAfter { get; init; } = -1;

    public double Score { get; init; }

    public int EstimatedTokens { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();
}

/// <summary>候选项决策结果。</summary>
public enum ContextDecisionCandidateOutcome
{
    Selected = 0,
    Dropped = 1
}

/// <summary>
/// 证据审计状态，区分"未接入证据提供者"与"接入但证据不完整"。
/// 替代旧的 EvidenceComplete 布尔值二态语义。
/// </summary>
public enum EvidenceAuditStatus
{
    /// <summary>未注册 IDecisionEvidenceProvider，审计未执行证据解析。</summary>
    NotConfigured = 0,

    /// <summary>接入证据提供者但部分候选缺少证据。</summary>
    Incomplete = 1,

    /// <summary>所有候选都有对应证据。</summary>
    Complete = 2,

    /// <summary>证据提供者抛出异常，解析失败。</summary>
    Failed = 3
}

/// <summary>本次决策的整体产出摘要。</summary>
public sealed class ContextDecisionOutcome
{
    public int SelectedCount { get; init; }

    public int DroppedCount { get; init; }

    public int EstimatedTokens { get; init; }

    public int TokenBudget { get; init; }

    /// <summary>本次决策涉及的 section 名称集合（package 场景）。</summary>
    public IReadOnlyList<string> Sections { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 非激活契约风险标志位集合。
/// V17.0 阶段所有标志位恒为 false，仅用于审计断言：decision trace 不得改变任何正式运行时输出。
/// </summary>
public sealed class ContextDecisionRisk
{
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalVectorStoreBinding { get; init; }
    public bool FormalPackageWrite { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool GraphApplyFormalChanged { get; init; }
    public bool LearningPolicyApplied { get; init; }
    public bool ModelTrainingStarted { get; init; }
}

/// <summary>decision trace 策略版本常量。</summary>
public static class ContextDecisionPolicyVersions
{
    public const string V17_0 = "context-decision-foundation/v17.0";

    /// <summary>
    /// R14-1：Decision Evidence V2 策略版本。引入 <see cref="CandidateDecisionReasonCode"/> 枚举
    /// 与 <see cref="DecisionEvidenceV2"/> 结构化字段，替代 V17.0 自由文本 reason。
    /// </summary>
    public const string V18_0 = "context-decision-evidence/v18.0";
}

/// <summary>decision-audit 审计报告。</summary>
public sealed class ContextDecisionAuditReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public int TraceCount { get; init; }

    public int PackageDecisionCount { get; init; }

    public int RetrievalDecisionCount { get; init; }

    public int TotalSelectedCount { get; init; }

    public int TotalDroppedCount { get; init; }

    /// <summary>非激活契约校验：所有标志位恒为 false 时为 true。</summary>
    public bool NonActivationContractHolds { get; init; }

    /// <summary>违反非激活契约的标志位名称列表（正常应为空）。</summary>
    public IReadOnlyList<string> ContractViolations { get; init; } = Array.Empty<string>();

    /// <summary>投影保留性校验：selected/dropped 的 ItemId 是否完整保留。</summary>
    public bool ProjectionPreservesIds { get; init; }

    /// <summary>证据完整性校验：所有 trace 的证据都完整时为 true。未接入证据提供者时为 false（NotConfigured）。</summary>
    public bool EvidenceComplete { get; init; }

    /// <summary>证据审计状态：NotConfigured / Incomplete / Complete / Failed。</summary>
    public EvidenceAuditStatus EvidenceStatus { get; init; }

    /// <summary>证据未完整的 decision ID 列表（EvidenceComplete=false 时非空）。</summary>
    public IReadOnlyList<string> EvidenceIncompleteDecisionIds { get; init; } = Array.Empty<string>();

    /// <summary>已解析证据的候选总数。</summary>
    public int EvidenceResolvedCount { get; init; }

    /// <summary>缺少证据的候选总数。</summary>
    public int EvidenceMissingCount { get; init; }

    public IReadOnlyList<ContextDecisionAuditSample> Samples { get; init; } = Array.Empty<ContextDecisionAuditSample>();

    public string PolicyVersion { get; init; } = ContextDecisionPolicyVersions.V17_0;
}

/// <summary>单条 decision trace 的审计摘要。</summary>
public sealed class ContextDecisionAuditSample
{
    public string DecisionId { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int SelectedCount { get; init; }

    public int DroppedCount { get; init; }

    public int EstimatedTokens { get; init; }

    public bool NonActivationContractHolds { get; init; }

    public IReadOnlyList<string> ContractViolations { get; init; } = Array.Empty<string>();

    /// <summary>该 trace 的证据是否完整。未接入证据提供者时为 false。</summary>
    public bool EvidenceComplete { get; init; }

    /// <summary>该 trace 的证据审计状态。</summary>
    public EvidenceAuditStatus EvidenceStatus { get; init; }

    /// <summary>该 trace 已解析证据的候选数。</summary>
    public int EvidenceResolvedCount { get; init; }

    /// <summary>该 trace 缺少证据的候选数。</summary>
    public int EvidenceMissingCount { get; init; }
}

/// <summary>
/// 决策证据：为单个候选提供结构化的选择/丢弃依据。
/// 补充 <see cref="ContextDecisionCandidate.Reason"/>（自由文本）和 <see cref="ContextDecisionCandidate.Score"/>（标量），
/// 增加备选方案、置信度和证据引用链。
/// </summary>
public sealed class DecisionEvidence
{
    /// <summary>关联的候选条目 ID（与 <see cref="ContextDecisionCandidate.ItemId"/> 对应）。</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>主要决策依据（如 "token-budget-exceeded"、"score-below-threshold"、"section-cap-reached"）。</summary>
    public string PrimaryRationale { get; init; } = string.Empty;

    /// <summary>次要决策依据列表。</summary>
    public IReadOnlyList<string> SecondaryRationales { get; init; } = Array.Empty<string>();

    /// <summary>本次决策中考虑过但未选中的备选方案。</summary>
    public IReadOnlyList<DecisionAlternative> AlternativesConsidered { get; init; } = Array.Empty<DecisionAlternative>();

    /// <summary>决策置信度 [0,1]。1.0 = 完全确定，0.0 = 无依据。</summary>
    public double Confidence { get; init; }

    /// <summary>证据引用链（trace ID、source path、build ID 等），用于溯源。</summary>
    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    /// <summary>证据来源类型（如 "retrieval-trace"、"package-build-trace"、"scoring-breakdown"）。</summary>
    public string Provenance { get; init; } = string.Empty;
}

/// <summary>决策备选方案：被考虑但未选中的候选。</summary>
public sealed class DecisionAlternative
{
    /// <summary>备选条目 ID。</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>未选中的原因。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>备选项的分数。</summary>
    public double Score { get; init; }
}

/// <summary>决策证据解析结果。</summary>
public sealed class DecisionEvidenceResult
{
    /// <summary>关联的决策记录 ID。</summary>
    public string DecisionId { get; init; } = string.Empty;

    /// <summary>解析出的证据列表（按 ItemId 对应候选）。</summary>
    public IReadOnlyList<DecisionEvidence> Evidence { get; init; } = Array.Empty<DecisionEvidence>();

    /// <summary>是否为完整证据（所有候选都有对应证据）。</summary>
    public bool IsComplete { get; init; }

    /// <summary>缺少证据的候选 ItemId 列表（IsComplete=false 时非空）。</summary>
    public IReadOnlyList<string> MissingItemIds { get; init; } = Array.Empty<string>();

    /// <summary>证据解析时间。</summary>
    public DateTimeOffset ResolvedAt { get; init; }
}
