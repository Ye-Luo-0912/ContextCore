using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// R18-1：统一决策内核契约（Context Decision Engine Contracts）
//
// 目标：
//   为 Retrieval 与 Package 两条决策主链建立统一的候选中间模型
//   (ContextCandidateEnvelope)，让候选身份、特征协议、safety gate、
//   utility score、reason code、token cost、policy/model version、
//   decision evidence 共享同一套类型，避免未来 Router / Ranker /
//   Relation Feature / 模型评分 / Agent refinement 必须接入两次。
//
// 设计原则：
//   1. 这些契约仅定义"中间模型"，不替换 ContextRetrievalCandidate /
//      ContextPackageDecision 等现有出口 DTO。Retrieval 与 Package
//      仍通过不同 Projector 输出，避免 God Object。
//   2. 复用 DecisionEvidenceV2 的字段设计（ChannelSources /
//      ScoreBreakdown / FinalScore / MatchedAnchors / RelationPaths /
//      LifecycleState / Rank / TokenBudget），把它从 trace-only
//      投影"提升"为运行时候选 envelope。
//   3. 复用 CandidateDecisionReasonCode 枚举作为统一 reason code。
//   4. 不引入存储 I/O；envelope 是内存中的不可变 record。
//   5. Model failure 时可通过将 Utility.ModelConfidence=0 + ReasonCode
//      精确回退到 deterministic policy（Features + Safety 仍可用）。
//
// 子阶段进度：
//   R18-1（当前）：契约定义 + 单元测试验证可实施性。不触碰
//                  HybridContextRetriever / BasicContextPackageBuilder。
//   R18-2：IContextDecisionEngine 接口 + Planner + Projector。
//   R18-3：Retrieval adapter（ContextRetrievalCandidate → Envelope）。
//   R18-4：Package adapter（PackageTraceCandidate → Envelope）。
// ===========================================================================

/// <summary>
/// R18-1：候选来源类型。统一替代 Retrieval 路径的 ContextRetrievalCandidateKind
/// 与 Package 路径的字符串 Kind。8 个 Expert 概念对齐 R20 Multi-Expert。
/// </summary>
/// <remarks>
/// 取值与 <see cref="ContextRetrievalCandidateKind"/> 保持兼容，
/// 新增的 MandatoryLexical / Constraint 概念为 R20 Expert 划分预留。
/// </remarks>
public enum ContextCandidateSource : byte
{
    /// <summary>未知来源（仅用于 envelope 构造失败或历史 trace 升级）。</summary>
    Unknown = 0,

    /// <summary>Mandatory 候选（hard constraint / required tag / mandatory metadata）。</summary>
    /// <remarks>R20 Expert: Mandatory。永不关闭。</remarks>
    Mandatory = 1,

    /// <summary>Lexical 候选（keyword / context recall）。</summary>
    /// <remarks>R20 Expert: Lexical。</remarks>
    Lexical = 2,

    /// <summary>Semantic 候选（vector recall）。</summary>
    /// <remarks>R20 Expert: Semantic。</remarks>
    Semantic = 3,

    /// <summary>Working Memory 候选（task state / short-term signal）。</summary>
    /// <remarks>R20 Expert: WorkingMemory。</remarks>
    WorkingMemory = 4,

    /// <summary>Stable Memory 候选（长期记忆 / verified memory）。</summary>
    /// <remarks>R20 Expert: StableMemory。</remarks>
    StableMemory = 5,

    /// <summary>Graph 候选（relation expansion / traversal）。</summary>
    /// <remarks>R20 Expert: Graph。</remarks>
    Graph = 6,

    /// <summary>Recency / Task-State 候选（recent_context / current_task）。</summary>
    /// <remarks>R20 Expert: Recency。</remarks>
    Recency = 7,

    /// <summary>Constraint 候选（hard/soft/merged constraint）。</summary>
    /// <remarks>R20 Expert: Constraint。永不关闭。</remarks>
    Constraint = 8,

    /// <summary>Global Context 候选（global context section）。</summary>
    GlobalContext = 9,

    /// <summary>Related Context 候选（inferred / related_expansion）。</summary>
    RelatedContext = 10
}

/// <summary>
/// R18-1：候选安全状态。统一 lifecycle / deprecation / required-tag /
/// duplicate 等"是否准入"判定，替代分散在 Retrieval / Package 路径的
/// 各类 metadata["mandatory"] / metadata["lifecycleStatus"] 检查。
/// </summary>
/// <remarks>
/// SafetyGate 是非激活契约：仅描述候选状态，不直接触发运行时变更。
/// Engine 在 Budget Allocator 之前根据此字段决定是否参与评分。
/// </remarks>
public sealed record CandidateSafetyState
{
    /// <summary>
    /// 约束强制级别（P0-1 修复新增）。仅当候选 <see cref="ContextCandidateSource.Constraint"/> 时填充。
    /// </summary>
    /// <remarks>
    /// P0-1 修复：不再从 <see cref="ContextCandidateSource"/> 推导约束强制级别。
    /// hard_constraint → <see cref="ConstraintLevel.Hard"/>
    /// soft_constraint → <see cref="ConstraintLevel.Soft"/>
    /// merged_constraint → <see cref="ConstraintLevel.Mixed"/>（不可直接免预算）
    /// 非 Constraint 来源候选此字段为 null，由 <see cref="IsMandatory"/> 直接表达。
    /// </remarks>
    public ConstraintLevel? ConstraintLevel { get; init; }

    /// <summary>
    /// 候选是否为 mandatory（hard constraint / required tag / system constraint）。
    /// </summary>
    /// <remarks>
    /// P0-1 修复：当 <see cref="ConstraintLevel"/> 非空时，由 Engine 根据
    /// ConstraintLevel is Hard or System or Mixed 推导；adapter 仍可直接设置
    /// 此字段以表达 Mandatory 来源（required tag）的强制选中语义。
    /// </remarks>
    public bool IsMandatory { get; init; }

    /// <summary>
    /// 候选是否为 hard constraint（仅 ConstraintLevel == Hard）。
    /// </summary>
    /// <remarks>
    /// P0-1 修复：adapter 必须基于 <see cref="ConstraintLevel"/> 设置此字段，
    /// 不可对 soft_constraint / merged_constraint 设置为 true。
    /// </remarks>
    public bool IsHardConstraint { get; init; }

    /// <summary>候选 lifecycle 状态（active / deprecated / superseded / frozen）。</summary>
    /// <remarks>复用 DecisionEvidenceV2.LifecycleState 字段语义。</remarks>
    public string LifecycleState { get; init; } = "active";

    /// <summary>候选是否被 deprecated 但仍被 active chain 引用（仍写入但标记）。</summary>
    public bool IsDeprecatedUsedByActiveChain { get; init; }

    /// <summary>候选是否被同 ItemId 的更新版本取代（supersede 链）。</summary>
    public bool IsSuperseded { get; init; }

    /// <summary>候选是否缺少 request.RequiredTags 中的至少一个 tag。</summary>
    public bool IsRequiredTagMismatch { get; init; }

    /// <summary>候选 ContentHash 是否与已选入候选重复（duplicate suppression）。</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>候选是否通过 safety gate（true = 准入评分，false = 直接 drop）。</summary>
    /// <remarks>
    /// Engine 计算：!IsSuperseded &amp;&amp; !IsRequiredTagMismatch &amp;&amp;
    ///   (!IsDeprecatedUsedByActiveChain || allowDeprecatedUsedByActiveChain) &amp;&amp;
    ///   (!IsDuplicate || allowDuplicateReference)。
    /// IsMandatory / IsHardConstraint 不影响准入（仍参与评分，仅在 Budget 中强制保留）。
    /// </remarks>
    public bool PassesSafetyGate { get; init; } = true;

    /// <summary>safety gate 拦截时的原因码（PassesSafetyGate=false 时填充）。</summary>
    public CandidateDecisionReasonCode BlockReasonCode { get; init; } = CandidateDecisionReasonCode.Unknown;

    /// <summary>safety gate 详情（如 "missing tag: long-term" / "superseded by item-xyz-v2"）。</summary>
    public string BlockReasonDetail { get; init; } = string.Empty;
}

/// <summary>
/// R18-1：候选特征向量。统一替代 Retrieval 路径的 RetrievalChannelCandidate.ScoreBreakdown
/// 与 Package 路径的 ItemScoreBreakdown，提供跨路径可比较的特征矩阵。
/// </summary>
/// <remarks>
/// 字段设计对应 DecisionEvidenceV2.ScoreBreakdown + MatchedAnchors + RelationPaths，
/// 但提升为强类型字段（而非 IReadOnlyDictionary），便于 Router / Ranker 直接消费。
/// ScoreBreakdown 仍以字典形式保留，承载专家级评分贡献（如 "lexical" / "semantic"）。
/// </remarks>
public sealed record CandidateFeatureVector
{
    /// <summary>
    /// 评分细分（key = 评分维度名，value = 该维度的得分贡献）。
    /// 复用 DecisionEvidenceV2.ScoreBreakdown 字段语义。
    /// </summary>
    public IReadOnlyDictionary<string, double> ScoreBreakdown { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>候选命中的 anchor 列表（query token / tag / semantic anchor）。</summary>
    public IReadOnlyList<string> MatchedAnchors { get; init; } = Array.Empty<string>();

    /// <summary>候选参与的关系路径列表（图扩展 BFS 路径签名）。</summary>
    public IReadOnlyList<string> RelationPaths { get; init; } = Array.Empty<string>();

    /// <summary>候选来源的 channel 列表（同一候选可能由多 channel 贡献）。</summary>
    /// <remarks>对应 DecisionEvidenceV2.ChannelSources 字段。</remarks>
    public IReadOnlyList<string> ChannelSources { get; init; } = Array.Empty<string>();

    /// <summary> lexical 评分贡献（关键词匹配 / BM25-like）。</summary>
    public double LexicalScore { get; init; }

    /// <summary> semantic 评分贡献（向量相似度）。</summary>
    public double SemanticScore { get; init; }

    /// <summary> recency 评分贡献（时间衰减）。</summary>
    public double RecencyScore { get; init; }

    /// <summary> relation boost 评分贡献（图扩展加权）。</summary>
    public double RelationBoost { get; init; }

    /// <summary> mandatory 权重（IsMandatory/IsHardConstraint 时为正数，否则 0）。</summary>
    public double MandatoryWeight { get; init; }

    /// <summary>特征 schema 版本（对应 ContextDecisionPolicyVersions.DecisionSchemaV2_0）。</summary>
    public string FeatureSchemaVersion { get; init; } = ContextDecisionPolicyVersions.DecisionSchemaV2_0;
}

/// <summary>
/// R18-1：候选效用评分。统一替代 Retrieval 路径的 ContextRetrievalCandidate.Score
/// 与 Package 路径的 ContextPackageDecision.Score，分离 deterministic / model 评分。
/// </summary>
/// <remarks>
/// 设计原则：
///   - DeterministicScore：规则评分（Features 加权 + mandatory 优先 + tie-break），永不依赖模型。
///   - ModelScore：模型评分（Router / Ranker / Listwise model 输出），可为 null。
///   - FinalScore：Engine 聚合后的最终分数（deterministic + model 加权）。
///   - ModelConfidence：模型置信度 [0,1]；0 或低于阈值时 Engine 回退到 DeterministicScore。
///   - R28-D P0-1：ModelAttempted/ModelApplied/ModelFallbackReason 区分"模型尝试过"和"实际参与排序"，
///     避免把 fallback 误认为模型已生效。
///
/// 这样 Model failure 时可精确回退到 deterministic policy（验收标准 #6）。
/// </remarks>
public sealed record CandidateUtilityScore
{
    /// <summary>deterministic 评分（规则评分，永不依赖模型）。</summary>
    public double DeterministicScore { get; init; }

    /// <summary>模型评分（Router / Ranker 输出）；null 表示模型未启用或未加载。</summary>
    public double? ModelScore { get; init; }

    /// <summary>最终聚合分数（deterministic + model 加权后）。</summary>
    /// <remarks>
    /// 当 ModelScore=null 或 ModelConfidence 低于阈值时，FinalScore=DeterministicScore。
    /// 否则 FinalScore = w_d * DeterministicScore + w_m * ModelScore（权重由 PolicyBundle 决定）。
    /// </remarks>
    public double FinalScore { get; init; }

    /// <summary>模型置信度 [0,1]；0 = 模型未启用，1 = 完全信任模型。</summary>
    public double ModelConfidence { get; init; }

    /// <summary>评分原因码（如 "deterministic-only" / "model-weighted" / "fallback-to-deterministic"）。</summary>
    public string ReasonCode { get; init; } = "deterministic-only";

    /// <summary>评分使用到的模型 artifact 引用（ModelScore 非 null 时填充）；用于 trace 溯源。</summary>
    public string? ModelArtifactRef { get; init; }

    /// <summary>
    /// R28-D P0-1：模型是否被尝试过（即 EnableModelScoring=true 且引擎/registry 可用）。
    /// true 不代表模型分数已应用，仅代表走过模型路径。
    /// 区分"未启用模型"和"启用但失败"。
    /// </summary>
    public bool ModelAttempted { get; init; }

    /// <summary>
    /// R28-D P0-1：模型分数是否实际参与了 FinalScore 加权。
    /// 仅当 ModelAttempted=true 且 confidence >= threshold 且推理成功时为 true。
    /// </summary>
    public bool ModelApplied { get; init; }

    /// <summary>
    /// R28-D P0-1：模型降级原因（ModelAttempted=true 但 ModelApplied=false 时填充）。
    /// 取值如 "engine-unavailable" / "schema-not-found" / "inference-failed" /
    /// "inference-succeeded-false" / "confidence-below-threshold" / "deterministic-replay-skipped"。
    /// ModelApplied=true 时为 null。
    /// </summary>
    public string? ModelFallbackReason { get; init; }
}

/// <summary>
/// R18-1：证据引用。统一候选的来源溯源链，替代分散在 Retrieval / Package
/// 路径的 SourceRefs 字符串列表。
/// </summary>
/// <remarks>
/// 设计澄清（来自用户 8 项澄清 #1）：
///   - Envelope 使用 ProvenanceRefs（来源引用，如 store path / buildId / traceId）。
///   - V2 决策证据（DecisionEvidenceV2.EvidenceRefs）在其上追加决策引用
///     （如 decisionId / auditRunId / modelArtifactRef），不重复 ProvenanceRefs。
///   - 共享 EvidenceRef 类型使两条路径可聚合溯源链。
/// </remarks>
public sealed record EvidenceRef
{
    /// <summary>证据引用 ID（如 "trace:abc123" / "build:2026-07-20-xyz" / "source:store:item-1"）。</summary>
    public string RefId { get; init; } = string.Empty;

    /// <summary>证据类型（如 "retrieval-trace" / "package-build-trace" / "scoring-breakdown" / "model-decision"）。</summary>
    public string RefType { get; init; } = string.Empty;

    /// <summary>证据来源 workspace（跨 workspace 时填充；同 workspace 留空）。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>证据来源 collection（跨 collection 时填充；同 collection 留空）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>证据生成时间（UTC）。</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>证据内容摘要（可选，如 hash / signature / fingerprint）。</summary>
    public string? ContentFingerprint { get; init; }
}

/// <summary>
/// R18-1：候选信封（Context Candidate Envelope）。
/// 统一中间模型，让 Retrieval 与 Package 两条路径共享候选身份、特征、
/// safety gate、utility score、token cost、policy/model version、decision evidence。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 不可变 record（支持 with 表达式，便于 Engine 阶段化增强字段）。
///   2. CandidateId 统一替代 Retrieval 的 CandidateId/SourceId 与 Package 的 ItemId。
///   3. Source 统一替代两路径的 Kind/Type 字符串/枚举混合表达。
///   4. Features/Safety/Utility 三个正交维度分离，避免 God Object。
///   5. ProvenanceRefs 承载来源溯源链（EvidenceRef 列表）。
///   6. PolicyVersion + ModelVersion 标识本候选决策使用的策略/模型版本。
///
/// 不包含：
///   - 输出格式字段（Sections / Content / SelectedItems 等）— 由 Projector 输出。
///   - 存储状态（TraceRow / DecisionRecord）— 由 V2 投影写入。
///   - 执行控制（Cancelled / Failed）— 由 Engine 内部状态机管理。
/// </remarks>
public sealed record ContextCandidateEnvelope
{
    /// <summary>候选唯一 ID（统一替代 Retrieval.CandidateId/SourceId 与 Package.ItemId）。</summary>
    public required string CandidateId { get; init; }

    /// <summary>候选来源类型（统一替代两路径的 Kind/Type 混合表达）。</summary>
    public required ContextCandidateSource Source { get; init; }

    /// <summary>候选业务类型（如 "note" / "task" / "memory" / "constraint"）。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>候选特征向量（评分细分 + anchor + relation path + channel sources）。</summary>
    public CandidateFeatureVector Features { get; init; } = new();

    /// <summary>候选安全状态（lifecycle / deprecation / required-tag / duplicate 判定）。</summary>
    public CandidateSafetyState Safety { get; init; } = new();

    /// <summary>候选效用评分（deterministic + model + final + confidence + reason code）。</summary>
    public CandidateUtilityScore Utility { get; init; } = new();

    /// <summary>候选估算 token 数（截断前）。</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>R28-B.7 P0-4：候选 token 成本（精确计算或估算）。</summary>
    /// <remarks>
    /// Provider 召回时若 IContextTokenizerResolver 可用，填充精确 token 成本；
    /// null 时回退到 <see cref="EstimatedTokens"/>（length/4 粗略估算）。
    /// Allocator / Projector 优先消费此字段做预算控制。
    /// </remarks>
    public CandidateTokenCost? TokenCost { get; init; }

    /// <summary>来源溯源链（store path / buildId / traceId / modelArtifactRef）。</summary>
    /// <remarks>
    /// 设计澄清（用户澄清 #1）：Envelope 使用 ProvenanceRefs；
    /// V2 决策证据（DecisionEvidenceV2.EvidenceRefs）在其上追加决策引用，不重复 ProvenanceRefs。
    /// </remarks>
    public IReadOnlyList<EvidenceRef> ProvenanceRefs { get; init; } = Array.Empty<EvidenceRef>();

    /// <summary>本候选决策使用的策略版本（如 ContextDecisionPolicyVersions.DecisionSchemaV2_0）。</summary>
    public string PolicyVersion { get; init; } = ContextDecisionPolicyVersions.DecisionSchemaV2_0;

    /// <summary>本候选决策使用的模型 artifact 引用（null = 未使用模型，纯 deterministic）。</summary>
    public string? ModelVersion { get; init; }

    /// <summary>候选 workspace ID（跨 workspace 决策时填充）。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>候选 collection ID（跨 collection 决策时填充）。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>R28-B：规范化候选标识（跨 Expert 合并去重键）。</summary>
    /// <remarks>
    /// P0-5：required。Adapter/Provider 必须填充，不得使用默认空 struct。
    /// 空 CanonicalKey 会导致 WorkingSetTee 中多个候选互相覆盖。
    /// </remarks>
    public required CanonicalCandidateKey CanonicalKey { get; init; }

    /// <summary>R28-B：多 Expert 来源记录（合并时 union）。</summary>
    public IReadOnlyList<ExpertOrigin> Origins { get; init; } = Array.Empty<ExpertOrigin>();

    /// <summary>R28-B：per-Expert 贡献权重（保留 per-Expert contribution，不合并为单一值）。</summary>
    public IReadOnlyDictionary<ExpertKind, double> ExpertContributions { get; init; }
        = new Dictionary<ExpertKind, double>();

    /// <summary>R28-B：策略引用（provenance；null = 未绑定到有效策略快照）。</summary>
    public ResolvedPolicyReference? PolicyReference { get; init; }
}

// ---------------------------------------------------------------------------
// P0-5：CandidateAdaptationContext
// ---------------------------------------------------------------------------

/// <summary>
/// P0-5：候选适配上下文。封装适配器（PackageCandidateAdapter / RetrievalCandidateAdapter）
/// 在将原始候选（PackageTraceCandidate / ContextRetrievalCandidate）转换为
/// <see cref="ContextCandidateEnvelope"/> 时所需的作用域信息与时间戳。
/// </summary>
/// <remarks>
/// 设计原则（P0-5 修复）：
///   1. 适配器不再在映射函数内部读取 <c>DateTimeOffset.UtcNow</c>；
///      相同输入应产生相同输出（幂等性契约）。
///   2. <see cref="ObservedAt"/> 由调用方在请求入口处统一传入，
///      用于填充 <see cref="EvidenceRef.GeneratedAt"/>。
///   3. <see cref="WorkspaceId"/> / <see cref="CollectionId"/> / <see cref="QueryText"/>
///      由调用方从 <see cref="ContextDecisionRequest"/> 中带入，避免适配器在
///      <c>ToDecisionRequest</c> 时丢失作用域（导致 PolicyRegistry 按空 workspace
///      解析默认 Bundle）。
///   4. <see cref="PolicySnapshot"/> 为已解析的策略快照引用（仅 BundleId + Version），
///      适配器不直接消费策略内容，仅作为溯源信息附加到 EvidenceRef。
/// </remarks>
public sealed record CandidateAdaptationContext
{
    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（必填）。</summary>
    public required string CollectionId { get; init; }

    /// <summary>请求 ID（用于 trace 溯源；可选）。</summary>
    public string? RequestId { get; init; }

    /// <summary>查询文本（用于 trace 溯源；可选）。</summary>
    public string? QueryText { get; init; }

    /// <summary>
    /// 观察时间（UTC）。由调用方在请求入口处传入，用于填充
    /// <see cref="EvidenceRef.GeneratedAt"/>。适配器不读取系统时间。
    /// P1-2：改为 required，删除默认 UtcNow，强制调用方显式传入以保证确定性。
    /// </summary>
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>
    /// 已解析的策略快照引用（可选）。仅包含 BundleId + Version，
    /// 适配器将其附加到 EvidenceRef 用于溯源；不直接消费策略内容。
    /// </summary>
    public ResolvedPolicySnapshot? PolicySnapshot { get; init; }

    /// <summary>
    /// P0-5：完整策略引用（可选）。Adapter 将其复制到 Envelope.PolicyReference。
    /// 包含 BundleId + BundleVersion + BundleContentHash + ActivationEpoch。
    /// null 时 Envelope.PolicyReference 也为 null（未绑定到有效策略快照）。
    /// </summary>
    public ResolvedPolicyReference? PolicyReference { get; init; }
}

/// <summary>
/// P0-5：已解析的策略快照引用。仅承载 BundleId + Version，
/// 不携带完整 bundle 内容（避免适配器耦合具体策略）。
/// </summary>
public sealed record ResolvedPolicySnapshot
{
    /// <summary>策略 bundle ID。</summary>
    public required string BundleId { get; init; }

    /// <summary>策略 bundle 版本。</summary>
    public required string Version { get; init; }

    /// <summary>bundle 解析时间（UTC）。</summary>
    public DateTimeOffset ResolvedAt { get; init; } = DateTimeOffset.UtcNow;
}
