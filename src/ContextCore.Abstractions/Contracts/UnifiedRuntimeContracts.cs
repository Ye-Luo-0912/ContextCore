using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// R28-B：Unified Context Decision Runtime 契约
//
// 目标：
//   把当前双重决策链收敛为唯一 Context Decision Runtime。
//   Runtime 负责 I/O 编排（Policy → Router → Providers → Merge → Early Gate →
//   Feature Pipeline → 调用 Engine）；Engine 保持纯决策内核语义不变。
//
// 设计原则：
//   1. IContextDecisionRuntime 是唯一 I/O 入口；Agent Kernel 只依赖它。
//   2. IContextDecisionEngine 保持现有语义（纯内存：Gate → Score → Allocate）。
//   3. EffectivePolicySnapshot + ResolvedPolicyReference 不与既有
//      ResolvedPolicySnapshot 冲突（后者保持轻量引用语义不变）。
//   4. ContextDecisionPurpose + ContextDecisionRuntimeKind 双轴，不废弃
//      Retrieval/Package 业务语义。
//   5. CanonicalCandidateKey + Material sidecar 分离正文与决策。
//   6. Envelope 不承载分配结果（CandidateAllocationDecision 独立）。
//   7. Recency Expert 不注册 no-op，Router 基于 IExpertCatalog 显式 disable。
//
// 子阶段进度：
//   B-1（当前）：契约定义 + 默认实现骨架，不改生产行为。
//   B-2：Candidate capture + pure Runtime + Tee 影子执行。
//   B-3：Shadow Gate 多维度验收。
//   B-4：Authoritative cutover（Retrieval → Package → AgentContext）。
//   B-5：Legacy removal + DecisionExperimentPlane 保留。
// ===========================================================================

// ---------------------------------------------------------------------------
// §5.1 两层入口
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B：统一 Context Decision Runtime — 唯一 I/O 编排入口。
/// </summary>
/// <remarks>
/// 负责：Policy resolution → Router → CandidateProviders → Canonical Merge →
/// Early Gate → Feature Pipeline → 调用 IContextDecisionEngine → 产出 Result。
/// 可 I/O：读 Store（通过 Provider）、调用 Router、加载 Policy。
/// Agent Kernel 只依赖此接口，不直接依赖 Engine。
/// </remarks>
public interface IContextDecisionRuntime
{
    /// <summary>
    /// 执行完整的 Context 决策编排。
    /// </summary>
    /// <param name="request">运行时请求（含 Scope / Purpose / SeedCandidates）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>决策结果（SelectedEnvelopes + AllocationDecisions + Outcome）。</returns>
    ValueTask<ContextDecisionResult> ExecuteAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R28-B：运行时请求。外部调用方通过此类型发起决策。
/// </summary>
/// <remarks>
/// SeedCandidates 仅用于 Replay / 测试 / 调用方显式注入 / 已有候选复用。
/// 正常生产路径由 CandidateProviders 产出候选。
/// </remarks>
public sealed record ContextDecisionRuntimeRequest
{
    /// <summary>关联的请求 ID。</summary>
    public required string RequestId { get; init; }

    /// <summary>请求作用域（workspace + collection）。</summary>
    public required ContextDecisionScope Scope { get; init; }

    /// <summary>业务用途（Retrieval / Package / AgentContext）。</summary>
    public required ContextDecisionPurpose Purpose { get; init; }

    /// <summary>查询文本（可选，用于 trace 溯源与语义召回）。</summary>
    public string? QueryText { get; init; }

    /// <summary>Token 预算上限。</summary>
    public int TokenBudget { get; init; }

    /// <summary>TopK 上限。</summary>
    public int TopK { get; init; }

    /// <summary>种子候选（Replay / 测试 / 显式注入；正常路径由 Providers 产出）。</summary>
    public IReadOnlyList<ContextCandidateEnvelope> SeedCandidates { get; init; }
        = Array.Empty<ContextCandidateEnvelope>();
}

/// <summary>
/// R28-B：统一作用域值对象。用于 Request.Scope 与 EffectivePolicySnapshot.ResolutionScope 校验。
/// </summary>
public readonly record struct ContextDecisionScope(string WorkspaceId, string CollectionId);

// ---------------------------------------------------------------------------
// §5.2 Policy 双类型
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B：不可变策略引用。用于 Envelope provenance / Allocation / Evidence。
/// </summary>
/// <remarks>
/// 与既有 <see cref="ResolvedPolicySnapshot"/>（BundleId + Version + ResolvedAt 轻量引用）
/// 不冲突。此类型额外携带 BundleContentHash + ActivationEpoch，用于 CAS 精确版本控制。
/// </remarks>
public sealed record ResolvedPolicyReference
{
    /// <summary>策略 bundle ID。</summary>
    public required string BundleId { get; init; }

    /// <summary>策略 bundle 版本。</summary>
    public required string BundleVersion { get; init; }

    /// <summary>bundle 内容哈希（用于内容寻址验证）。</summary>
    public required string BundleContentHash { get; init; }

    /// <summary>激活 epoch（CAS 乐观锁版本号）。</summary>
    public required long ActivationEpoch { get; init; }
}

/// <summary>
/// R28-B：请求生命周期内的有效策略快照。
/// </summary>
/// <remarks>
/// 由 IResolvedPolicyProvider 在请求入口产出，整个请求生命周期内不可变。
/// Safety 不允许 override；Budget / Routing 已合并 override。
/// </remarks>
public sealed record EffectivePolicySnapshot
{
    /// <summary>不可变策略引用。</summary>
    public required ResolvedPolicyReference Reference { get; init; }

    /// <summary>有效 Safety profile（不允许 override）。</summary>
    public required SafetyProfile Safety { get; init; }

    /// <summary>有效 Budget profile（已合并 BudgetOverride）。</summary>
    public required BudgetProfile Budget { get; init; }

    /// <summary>有效 Routing profile（已合并 RoutingOverride）。</summary>
    public required RoutingProfile Routing { get; init; }

    /// <summary>特征 schema 版本（用于 Feature Pipeline 兼容性检查）。</summary>
    public required string FeatureSchemaVersion { get; init; }

    /// <summary>Router 模型哈希（null = deterministic router）。</summary>
    public string? RouterModelHash { get; init; }

    /// <summary>Ranker 模型哈希（null = deterministic scorer）。</summary>
    public string? RankerModelHash { get; init; }

    /// <summary>解析作用域（与 Request.Scope 校验，不一致则 fail-closed）。</summary>
    public required ContextDecisionScope ResolutionScope { get; init; }
}

/// <summary>
/// R28-B：策略快照提供者。在请求入口产出 EffectivePolicySnapshot。
/// </summary>
public interface IResolvedPolicyProvider
{
    /// <summary>
    /// 解析请求对应的有效策略快照。
    /// </summary>
    /// <param name="request">运行时请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>不可变的有效策略快照。</returns>
    ValueTask<EffectivePolicySnapshot> ResolveAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// §5.3 双轴语义
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B：业务用途轴。表示决策的输出语义，不废弃。
/// </summary>
public enum ContextDecisionPurpose : byte
{
    /// <summary>检索决策（产出 ContextRetrievalResult）。</summary>
    Retrieval = 0,

    /// <summary>打包决策（产出 ContextPackageBuildResult，含 section allocation）。</summary>
    Package = 1,

    /// <summary>Agent Context 决策（产出 AgentContextSnapshot）。</summary>
    AgentContext = 2
}

/// <summary>
/// R28-B：运行实现轴。表示决策由哪条运行链执行。
/// </summary>
public enum ContextDecisionRuntimeKind : byte
{
    /// <summary>遗留主链（HybridContextRetriever / BasicContextPackageBuilder）。</summary>
    Legacy = 0,

    /// <summary>统一 V2 主链（IContextDecisionRuntime → IContextDecisionEngine）。</summary>
    UnifiedV2 = 1
}

// ---------------------------------------------------------------------------
// §5.5 Canonical Identity + Material
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B：规范化候选标识。用于跨 Expert 去重合并。
/// </summary>
/// <remarks>
/// 同一实体不同版本（EntityVersion 不同）不直接合并；
/// 相同 EntityId 不同 EntityKind 不得碰撞。
/// </remarks>
public readonly record struct CanonicalCandidateKey(
    string WorkspaceId,
    string CollectionId,
    string EntityKind,
    string EntityId,
    string EntityVersion);

/// <summary>
/// R28-B：Expert 来源记录。合并时 union 到 Envelope.Origins。
/// </summary>
public sealed record ExpertOrigin(
    ExpertKind Expert,
    double Contribution,
    DateTimeOffset ObservedAt);

/// <summary>
/// R28-B：候选正文 Material sidecar。正文与决策分离，Projector 不访问 Store。
/// </summary>
public sealed record CandidateMaterial
{
    /// <summary>对应的规范化候选标识。</summary>
    public required CanonicalCandidateKey Key { get; init; }

    /// <summary>候选正文内容。</summary>
    public required string Content { get; init; }

    /// <summary>原生类型（如 "note" / "memory" / "constraint"）。</summary>
    public required string NativeKind { get; init; }

    /// <summary>来源引用列表（store path / buildId / traceId）。</summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = [];
}

/// <summary>
/// R28-B：候选工作集。Envelopes + Materials 的不可变快照。
/// </summary>
public sealed record CandidateWorkingSet
{
    /// <summary>决策候选 envelope 集合。</summary>
    public required IReadOnlyList<ContextCandidateEnvelope> Envelopes { get; init; }

    /// <summary>候选正文 sidecar（按 CanonicalCandidateKey 索引）。</summary>
    public required IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> Materials { get; init; }
        = new Dictionary<CanonicalCandidateKey, CandidateMaterial>();
}

/// <summary>
/// R28-B：Expert 执行结果。每个 CandidateProvider 产出此二元组。
/// </summary>
public sealed record ExpertExecutionResult(
    IReadOnlyList<ContextCandidateEnvelope> Envelopes,
    IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> Materials);

// ---------------------------------------------------------------------------
// §5.7 Router + Provider + Catalog
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B：统一 Router 接口。替代 IRetrievalRouter，扩展到所有 Purpose。
/// </summary>
public interface IRouter
{
    /// <summary>
    /// 基于请求 + 策略快照产出 Expert 路由决策集。
    /// </summary>
    /// <param name="request">运行时请求。</param>
    /// <param name="snapshot">有效策略快照。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Expert 路由决策集（含 mask + per-Expert TopK/Tokens）。</returns>
    ValueTask<ExpertRoutingDecisionSet> RouteAsync(
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R28-B：Provider 能力目录。替代 no-op Expert 注册。
/// </summary>
/// <remarks>
/// Router 基于 AvailableExperts 过滤；未注册的 Expert（如 Recency）被显式 disable，
/// ReasonCode = "expert-not-registered"。
/// </remarks>
public interface IExpertCatalog
{
    /// <summary>当前已注册的 Expert 集合。</summary>
    IReadOnlySet<ExpertKind> AvailableExperts { get; }
}

/// <summary>
/// R28-B：统一 Candidate Provider 接口。每个 Provider 对应一个 ExpertKind。
/// </summary>
public interface ICandidateProvider
{
    /// <summary>此 Provider 对应的 Expert 类型。</summary>
    ExpertKind Kind { get; }

    /// <summary>
    /// 执行候选召回，产出 Envelope + Material 二元组。
    /// </summary>
    /// <param name="context">Provider 执行上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Expert 执行结果（Envelopes + Materials）。</returns>
    ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R28-B：Provider 执行上下文。
/// </summary>
public sealed record CandidateProviderContext(
    ContextDecisionRuntimeRequest Request,
    EffectivePolicySnapshot Policy,
    ExpertRoutingDecision Routing,
    CandidateAdaptationContext AdaptationContext);

/// <summary>
/// R28-B：统一 Expert 类型。与既有 RetrievalExpert 枚举值对齐。
/// </summary>
/// <remarks>
/// Recency 枚举值保留，但默认不注册到 Catalog（Router disable + ReasonCode）。
/// 不定义 PackageShortTermSignal / PackageRecallSection / PackageExpansionDiagnostics。
/// </remarks>
public enum ExpertKind : byte
{
    /// <summary>Mandatory recall（永不关闭）。</summary>
    Mandatory = 0,

    /// <summary>Constraint recall（永不关闭）。</summary>
    Constraint = 1,

    /// <summary>Lexical / keyword recall。</summary>
    Lexical = 2,

    /// <summary>Semantic / vector recall。</summary>
    Semantic = 3,

    /// <summary>Working memory recall（含短期信号）。</summary>
    WorkingMemory = 4,

    /// <summary>Stable memory recall。</summary>
    StableMemory = 5,

    /// <summary>Graph / relation recall。</summary>
    Graph = 6,

    /// <summary>Recency recall（默认不注册，Router disable）。</summary>
    Recency = 7
}

// ---------------------------------------------------------------------------
// §5.8 Canonical Merger + Early/Decision Gate + Feature Pipeline
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B：规范化候选合并器。按 CanonicalCandidateKey 合并多 Expert 来源。
/// </summary>
public interface ICanonicalCandidateMerger
{
    /// <summary>
    /// 合并多个 Expert 的输出，按 CanonicalCandidateKey 去重。
    /// </summary>
    /// <param name="expertOutputs">各 Expert 的执行结果。</param>
    /// <returns>合并后的候选工作集。</returns>
    CandidateWorkingSet Merge(IReadOnlyList<ExpertExecutionResult> expertOutputs);
}

/// <summary>
/// R28-B：Early Admission Gate。在 Feature Pipeline 之前做早期剔除。
/// </summary>
/// <remarks>
/// 检查：scope mismatch / superseded / archived / rejected / forbidden tag /
/// illegal evidence / hard lifecycle block。
/// </remarks>
public interface IEarlyAdmissionGate
{
    /// <summary>
    /// 评估候选是否通过早期准入。
    /// </summary>
    AdmissionResult Evaluate(ContextCandidateEnvelope envelope, EffectivePolicySnapshot snapshot);
}

/// <summary>
/// R28-B：准入结果。
/// </summary>
public sealed record AdmissionResult(
    bool Admitted,
    string ReasonCode,
    string Detail);

/// <summary>
/// R28-B：Decision Safety Gate。在 Feature Pipeline 之后做完整安全检查。
/// </summary>
/// <remarks>
/// 检查：duplicate / required coverage / cross-candidate conflict / full evidence rules。
/// Mandatory/Hard Constraint 免预算，不免 Safety/Lifecycle。
/// </remarks>
public interface ISafetyGate
{
    /// <summary>
    /// 评估候选是否通过 Safety Gate。
    /// </summary>
    SafetyGateResult Evaluate(ContextCandidateEnvelope envelope, SafetyProfile profile);
}

/// <summary>
/// R28-B：Safety Gate 结果。
/// </summary>
public sealed record SafetyGateResult(
    bool Passes,
    CandidateDecisionReasonCode ReasonCode,
    string Detail);

/// <summary>
/// R28-B：Lifecycle Gate。检查候选生命周期状态。
/// </summary>
public interface ILifecycleGate
{
    /// <summary>
    /// 评估候选是否通过 Lifecycle Gate。
    /// </summary>
    LifecycleGateResult Evaluate(ContextCandidateEnvelope envelope);
}

/// <summary>
/// R28-B：Lifecycle Gate 结果。
/// </summary>
public sealed record LifecycleGateResult(
    bool Passes,
    string ReasonCode,
    string Detail);

/// <summary>
/// R28-B：Feature Pipeline。纯转换，返回新 Envelope 列表（immutable record 友好）。
/// </summary>
public interface IFeaturePipeline
{
    /// <summary>
    /// 计算/标准化候选特征向量。不计分，只计算特征。
    /// </summary>
    /// <param name="envelopes">输入候选 envelope 集合（不被修改）。</param>
    /// <param name="context">Feature pipeline 上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>富化后的新 Envelope 列表。</returns>
    ValueTask<IReadOnlyList<ContextCandidateEnvelope>> EnrichAsync(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        FeaturePipelineContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R28-B：Feature Pipeline 上下文。
/// </summary>
public sealed record FeaturePipelineContext(
    EffectivePolicySnapshot Policy,
    CandidateAdaptationContext AdaptationContext);

// ---------------------------------------------------------------------------
// §5.9 Utility Scorer + Allocator
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B：Utility Scorer。计算候选 FinalScore。
/// </summary>
/// <remarks>
/// rule-only 模式：w_d=1.0, w_m=0.0，FinalScore = DeterministicScore。
/// </remarks>
public interface IUtilityScorer
{
    /// <summary>
    /// 对候选集合计算效用评分。
    /// </summary>
    /// <param name="envelopes">待评分的候选集合。</param>
    /// <param name="snapshot">有效策略快照。</param>
    void Score(IReadOnlyList<ContextCandidateEnvelope> envelopes, EffectivePolicySnapshot snapshot);
}

/// <summary>
/// R28-B：统一全局分配器。消费 SectionRatios + TopK + TokenBudget。
/// </summary>
/// <remarks>
/// 产出 CandidateAllocationDecision，不污染 Envelope。
/// diversity extension point 存在但 rule-only convergence 阶段禁用行为变更。
/// </remarks>
public interface IGlobalAllocator
{
    /// <summary>
    /// 执行全局预算分配 + per-section 配额。
    /// </summary>
    /// <param name="envelopes">待分配的候选集合。</param>
    /// <param name="snapshot">有效策略快照。</param>
    /// <returns>分配结果（Selected + Dropped + Decisions + Outcome）。</returns>
    AllocationResult Allocate(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot);
}

/// <summary>
/// R28-B：候选分配决策。与 Envelope 解耦，利于 Replay / counterfactual。
/// </summary>
public sealed record CandidateAllocationDecision
{
    /// <summary>对应的规范化候选标识。</summary>
    public required CanonicalCandidateKey CandidateKey { get; init; }

    /// <summary>分配到的 section 名称。</summary>
    public required string Section { get; init; }

    /// <summary>实际分配的 token 数。</summary>
    public required int IncludedTokens { get; init; }

    /// <summary>是否被截断（token 数小于候选原始估算）。</summary>
    public bool IsTruncated { get; init; }

    /// <summary>分配/丢弃原因码。</summary>
    public required CandidateDecisionReasonCode ReasonCode { get; init; }
}

/// <summary>
/// R28-B：分配结果。
/// </summary>
public sealed record AllocationResult(
    IReadOnlyList<ContextCandidateEnvelope> Selected,
    IReadOnlyList<ContextCandidateEnvelope> Dropped,
    IReadOnlyList<CandidateAllocationDecision> AllocationDecisions,
    ContextDecisionOutcomeSummary Outcome);

/// <summary>
/// R28-B：Mandatory 超预算策略。
/// </summary>
public enum MandatoryOverflowPolicy : byte
{
    /// <summary>严格失败（Agent/model context 硬窗口）。</summary>
    FailClosed = 0,

    /// <summary>允许溢出但记录诊断（普通 Package，默认）。</summary>
    AllowOverflowWithDiagnostic = 1,

    /// <summary>拒绝最低优先级的 mandatory 候选。</summary>
    RejectLowestAuthorityMandatory = 2
}

// ---------------------------------------------------------------------------
// AgentContext Projector
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B：Agent Context Projector。从 DecisionResult + WorkingSet 投影为 AgentContextSnapshot。
/// </summary>
/// <remarks>
/// 复用 R23 AgentContextSnapshot（不新增 AgentContextPackage）。
/// Projector 不访问 Store；不重新排序、过滤、截断或计分。
/// AgentContextSnapshot 仅通过 DecisionRequestIds 引用 ContextDecisionResult，不嵌入实例。
/// </remarks>
public interface IAgentContextProjector
{
    /// <summary>
    /// 将决策结果 + 候选正文投影为 AgentContextSnapshot。
    /// </summary>
    /// <param name="result">决策结果。</param>
    /// <param name="workingSet">候选正文 sidecar。</param>
    /// <returns>AgentContextSnapshot。</returns>
    AgentContextSnapshot Project(ContextDecisionResult result, CandidateWorkingSet workingSet);
}
