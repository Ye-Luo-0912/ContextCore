namespace ContextCore.Abstractions.Models;

/// <summary>
/// R14-1：候选项决策原因码。替代 <see cref="ContextDecisionCandidate.Reason"/> 自由文本，
/// 为决策证据提供机器可解析的枚举，使下游 Agent / Router / Reranker / 学习闭环可基于稳定分类聚合分析。
/// </summary>
/// <remarks>
/// 枚举值设计原则：
/// <list type="bullet">
/// <item><b>Selected*</b>：候选被选入 package 输出的原因。</item>
/// <item><b>*Blocked / *Mismatch / *Suppressed / *Exceeded / *Missing</b>：候选被丢弃或排除的原因。</item>
/// <item>同一候选可能命中多个原因；投影时取 PrimaryReasonCode，次要原因填入 SecondaryReasonCodes。</item>
/// <item>新增原因必须扩展此枚举，不允许多态字符串。</item>
/// </list>
/// </remarks>
public enum CandidateDecisionReasonCode
{
    /// <summary>原因未知或尚未分类（过渡值，仅在历史 trace 升级路径使用）。</summary>
    Unknown = 0,

    /// <summary>候选为 mandatory 类型（hard constraint / required tag），无条件选入。</summary>
    SelectedMandatory = 1,

    /// <summary>候选经评分排序后分数最高被选入。</summary>
    SelectedHighestUtility = 2,

    /// <summary>候选为 relation reserve 预留位（图扩展保留配额）。</summary>
    SelectedRelationReserve = 3,

    /// <summary>候选因 lifecycle 状态（deprecated / superseded / frozen）被阻止选入。</summary>
    LifecycleBlocked = 4,

    /// <summary>候选为 deprecated 内容且未被 active chain 引用，被阻止选入。</summary>
    DeprecatedBlocked = 5,

    /// <summary>候选缺少 request.RequiredTags 中的至少一个 tag，被阻止选入。</summary>
    RequiredTagMismatch = 6,

    /// <summary>候选因重复内容（同 ContentHash 或同 ItemId）被抑制，仅保留首条。</summary>
    DuplicateSuppressed = 7,

    /// <summary>候选因 section 配额已满被丢弃（per-section Take 限制）。</summary>
    SectionQuotaExceeded = 8,

    /// <summary>候选因整体 token 预算耗尽被丢弃（截断后未保留）。</summary>
    TokenBudgetExceeded = 9,

    /// <summary>候选分数低于 threshold（min score / attention score threshold）被丢弃。</summary>
    ScoreBelowThreshold = 10,

    /// <summary>候选被同 ItemId 的更新版本取代（supersede 链）。</summary>
    SupersededByCurrentVersion = 11,

    /// <summary>候选缺少必要证据（evidence missing），无法满足审计完整性。</summary>
    EvidenceMissing = 12,

    /// <summary>候选已过时（deprecated 内容但被 active chain 引用，仍写入但标记）。</summary>
    DeprecatedUsedByActiveChain = 13,

    /// <summary>候选为 partial accepted：因 token 截断仅部分保留（P0-6.3 引入的细分）。</summary>
    PartiallyAcceptedDueToTruncation = 14,

    /// <summary>候选因被其他 section 引用而作为 duplicate reference 跳过（不重复写入）。</summary>
    DuplicateSectionReference = 15
}

/// <summary>
/// R14-1：候选项决策原因码集合。包含主原因与次要原因列表，
/// 替代 <see cref="ContextDecisionCandidate.Reason"/> 单字符串字段，提供结构化分类。
/// </summary>
public sealed class CandidateDecisionReason
{
    /// <summary>
    /// 主要决策原因码。投影时按优先级选取：
    /// LifecycleBlocked > RequiredTagMismatch > DuplicateSuppressed > TokenBudgetExceeded >
    /// SectionQuotaExceeded > ScoreBelowThreshold > SupersededByCurrentVersion > EvidenceMissing > Selected*。
    /// </summary>
    public CandidateDecisionReasonCode PrimaryReasonCode { get; init; } = CandidateDecisionReasonCode.Unknown;

    /// <summary>
    /// 次要决策原因码列表（按命中顺序）。同一候选可能同时命中多个原因，
    /// 例如 lifecycle=DeprecatedBlocked + relation=SupersededByCurrentVersion。
    /// </summary>
    public IReadOnlyList<CandidateDecisionReasonCode> SecondaryReasonCodes { get; init; } = Array.Empty<CandidateDecisionReasonCode>();

    /// <summary>
    /// 人类可读的原因详情（如 "section quota reached: working_memory=20/20"）。
    /// 不可作为机器分类依据，仅供 trace 审阅与诊断。
    /// </summary>
    public string ReasonDetail { get; init; } = string.Empty;
}

/// <summary>
/// R14-1：候选项决策证据 V2。替代 <see cref="DecisionEvidence"/> 的 PrimaryRationale/SecondaryRationales 字符串字段，
/// 改为强类型枚举 + 结构化字段，为 Agent / Router / Reranker / 学习闭环提供可聚合的数据基础。
/// </summary>
/// <remarks>
/// V2 设计原则：
/// <list type="bullet">
/// <item>所有原因字段使用 <see cref="CandidateDecisionReasonCode"/> 枚举，不允许多态字符串。</item>
/// <item>保留 <see cref="DecisionEvidence"/> 的备选方案与证据引用链，作为溯源基础。</item>
/// <item>新增 channel sources / score breakdown / matched anchors / relation paths / lifecycle state / rank / token budget 等结构化字段。</item>
/// <item>不改变 V17.0 非激活契约：投影过程只读，不触发运行时变更。</item>
/// </list>
/// </remarks>
public sealed class DecisionEvidenceV2
{
    /// <summary>关联的候选条目 ID（与 <see cref="ContextDecisionCandidate.ItemId"/> 对应）。</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>候选输入指纹（hash of request + section + candidate identity），用于版本对比与去重。</summary>
    public string InputFingerprint { get; init; } = string.Empty;

    /// <summary>策略版本（如 "package-policy/v17.0"、"retrieval-policy/v3.11"），用于 trace 兼容性识别。</summary>
    public string PolicyVersion { get; init; } = string.Empty;

    /// <summary>
    /// 候选来源的 channel 列表（如 "recent_context"、"working_memory"、"relation_expansion"）。
    /// 同一候选可能由多个 channel 同时贡献，此处记录所有命中的 channel。
    /// </summary>
    public IReadOnlyList<string> ChannelSources { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 候选评分细分（key = 评分维度名，如 "lexical" / "semantic" / "recency" / "relation_boost"）。
    /// 替代 <see cref="ContextDecisionCandidate.Score"/> 标量，便于分析评分贡献。
    /// </summary>
    public IReadOnlyDictionary<string, double> ScoreBreakdown { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>最终聚合分数（ScoreBreakdown 加权后），与 <see cref="ContextDecisionCandidate.Score"/> 一致。</summary>
    public double FinalScore { get; init; }

    /// <summary>
    /// 候选命中的 anchor 列表（用于检索候选的锚点匹配，如 query token 命中、tag 命中）。
    /// 每项为 anchor 标识符（如 "tag:long-term" / "token:context" / "anchor:required-task"）。
    /// </summary>
    public IReadOnlyList<string> MatchedAnchors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 候选参与的关系路径列表（如图扩展 BFS 路径）。
    /// 每项为路径签名（如 "root→item_a→item_b"），便于分析图扩展贡献。
    /// </summary>
    public IReadOnlyList<string> RelationPaths { get; init; } = Array.Empty<string>();

    /// <summary>候选 lifecycle 状态（如 "active" / "deprecated" / "superseded" / "frozen"）。</summary>
    public string LifecycleState { get; init; } = "active";

    /// <summary>候选在排序前的位置（0-based）。</summary>
    public int RankBefore { get; init; } = -1;

    /// <summary>候选在排序后的位置（0-based）；与 RankBefore 一致表示无重排。</summary>
    public int RankAfter { get; init; } = -1;

    /// <summary>决策原因集合（主原因 + 次要原因 + 详情）。</summary>
    public CandidateDecisionReason Reason { get; init; } = new();

    /// <summary>决策前剩余 token 预算。</summary>
    public int TokenBudgetBefore { get; init; } = -1;

    /// <summary>决策后剩余 token 预算。</summary>
    public int TokenBudgetAfter { get; init; } = -1;

    /// <summary>本次决策中考虑过但未选中的备选方案。</summary>
    public IReadOnlyList<DecisionAlternative> AlternativesConsidered { get; init; } = Array.Empty<DecisionAlternative>();

    /// <summary>决策置信度 [0,1]。1.0 = 完全确定，0.0 = 无依据。</summary>
    public double Confidence { get; init; }

    /// <summary>证据引用链（trace ID、source path、build ID 等），用于溯源。</summary>
    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    /// <summary>证据来源类型（如 "retrieval-trace"、"package-build-trace"、"scoring-breakdown"）。</summary>
    public string Provenance { get; init; } = string.Empty;
}

/// <summary>
/// R14-1：决策证据 V2 解析结果。替代 <see cref="DecisionEvidenceResult"/>，
/// 携带 V2 结构化证据列表与完整性状态。
/// </summary>
public sealed class DecisionEvidenceV2Result
{
    /// <summary>关联的决策记录 ID。</summary>
    public string DecisionId { get; init; } = string.Empty;

    /// <summary>解析出的 V2 证据列表（按 ItemId 对应候选）。</summary>
    public IReadOnlyList<DecisionEvidenceV2> Evidence { get; init; } = Array.Empty<DecisionEvidenceV2>();

    /// <summary>是否为完整证据（所有候选都有对应证据）。</summary>
    public bool IsComplete { get; init; }

    /// <summary>缺少证据的候选 ItemId 列表（IsComplete=false 时非空）。</summary>
    public IReadOnlyList<string> MissingItemIds { get; init; } = Array.Empty<string>();

    /// <summary>证据解析时间。</summary>
    public DateTimeOffset ResolvedAt { get; init; }

    /// <summary>R14-1：策略版本，标识 V2 证据结构。</summary>
    public string PolicyVersion { get; init; } = ContextDecisionPolicyVersions.V18_0;
}
