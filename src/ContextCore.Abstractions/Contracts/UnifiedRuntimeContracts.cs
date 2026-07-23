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

    /// <summary>
    /// R28-B.6 Blocker-1：执行完整决策编排，返回 ExecutionResult（含 WorkingSet）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ExecuteAsync"/> 的差异：返回 <see cref="ContextDecisionExecutionResult"/>，
    /// 携带完整 <see cref="CandidateWorkingSet"/>（Envelopes + Materials）+ Policy + Routing + ProviderReports，
    /// 让 Projector 始终能从 Material sidecar 恢复候选正文。
    /// </remarks>
    /// <param name="request">运行时请求（含 Scope / Purpose / SeedCandidates）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完整执行结果（Decision + WorkingSet + Policy + Routing + ProviderReports）。</returns>
    ValueTask<ContextDecisionExecutionResult> ExecuteWithWorkingSetAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R28-B.6 Blocker-1：Provider 执行报告。
/// </summary>
public sealed record ProviderExecutionReport
{
    /// <summary>Provider 对应的 Expert 类型。</summary>
    public required ExpertKind Kind { get; init; }

    /// <summary>是否执行成功。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>是否超时。</summary>
    public required bool TimedOut { get; init; }

    /// <summary>执行耗时。</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>产出的候选数。</summary>
    public required int CandidateCount { get; init; }

    /// <summary>Store 调用次数（可选，用于诊断）。</summary>
    public int StoreCallCount { get; init; }

    /// <summary>错误码（失败时填入，如 "timeout" / "store-unavailable"）。</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// R28-B.6 Blocker-1：完整执行结果（Decision + WorkingSet + Policy + Routing + ProviderReports）。
/// </summary>
/// <remarks>
/// Runtime 返回此类型，Projector 始终消费 Decision + WorkingSet + ProjectionContext，
/// 不再依赖仅有 Decision 的旧路径（避免 V2-only 路径丢失 Material）。
/// </remarks>
public sealed record ContextDecisionExecutionResult
{
    /// <summary>决策结果（SelectedEnvelopes + DroppedEnvelopes + AllocationDecisions + Outcome）。</summary>
    public required ContextDecisionResult Decision { get; init; }

    /// <summary>候选工作集（Envelopes + Materials），Projector 从 Materials 恢复正文。</summary>
    public required CandidateWorkingSet WorkingSet { get; init; }

    /// <summary>有效策略快照（请求生命周期内不可变）。</summary>
    public required EffectivePolicySnapshot Policy { get; init; }

    /// <summary>Expert 路由决策集。</summary>
    public required ExpertRoutingDecisionSet Routing { get; init; }

    /// <summary>各 Provider 的执行报告（按执行顺序；Phase 2 Graph 在最后）。</summary>
    public IReadOnlyList<ProviderExecutionReport> ProviderReports { get; init; } = [];

    /// <summary>R28-B.7：标准化后的请求（Purpose Request Normalizer 产出）。</summary>
    public ContextDecisionRuntimeRequest? NormalizedRequest { get; init; }

    /// <summary>R28-B.7：请求语义哈希（用于 replay 匹配）。</summary>
    public string? RequestSemanticHash { get; init; }

    /// <summary>R28-B.7：请求作用域（标准化，不从候选反推）。</summary>
    public ContextDecisionScope Scope { get; init; }

    /// <summary>R28-B.7：Feature Schema 版本（从 Policy 获取，用于 replay 兼容性）。</summary>
    public string? FeatureSchemaVersion { get; init; }

    /// <summary>R28-B.7：Allocator 版本（用于 replay 兼容性）。</summary>
    public string? AllocatorVersion { get; init; }

    /// <summary>R28-B.7：Tokenizer 版本（用于精确 token 计算兼容性）。</summary>
    public string? TokenizerVersion { get; init; }

    /// <summary>R28-B.7：Provider 输出快照（每个 Provider 的 Envelopes+Materials 快照，用于 replay）。</summary>
    public IReadOnlyList<ProviderOutputSnapshot> ProviderOutputSnapshots { get; init; } = [];
}

/// <summary>
/// R28-B.7：Provider 输出快照（用于 replay 和审计）。
/// </summary>
/// <remarks>
/// 捕获每个 Provider 执行后的 Envelopes + Materials + 成功状态 + 耗时，
/// 让 replay 能从快照恢复完整候选工作集，无需重新调用 Provider。
/// </remarks>
public sealed record ProviderOutputSnapshot
{
    /// <summary>Provider 对应的 Expert 类型。</summary>
    public required ExpertKind Kind { get; init; }

    /// <summary>Provider 产出的候选 envelope 集合。</summary>
    public required IReadOnlyList<ContextCandidateEnvelope> Envelopes { get; init; }

    /// <summary>Provider 产出的候选正文 sidecar（按 CanonicalCandidateKey 索引）。</summary>
    public required IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> Materials { get; init; }

    /// <summary>Provider 是否执行成功。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Provider 执行耗时。</summary>
    public required TimeSpan Duration { get; init; }
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

    /// <summary>
    /// R28-B.6 P0-4：种子 WorkingSet（含 Envelopes + Materials）。
    /// 正式路径接受完整 WorkingSet，而非只有 Envelope 的 SeedCandidates。
    /// Replay/Agent 显式注入时 Seed Material 不再丢失。
    /// null 时回退到 <see cref="SeedCandidates"/>（向后兼容）。
    /// </summary>
    public CandidateWorkingSet? SeedWorkingSet { get; init; }

    /// <summary>R28-B.6 Blocker-4：Retrieval 专用输入（Purpose=Retrieval 时使用）。</summary>
    public RetrievalInput? RetrievalInput { get; init; }

    /// <summary>R28-B.6 Blocker-4：Package 专用输入（Purpose=Package 时使用）。</summary>
    public PackageInput? PackageInput { get; init; }

    /// <summary>R28-B.6 Blocker-4：AgentContext 专用输入（Purpose=AgentContext 时使用）。</summary>
    public AgentInput? AgentInput { get; init; }
}

/// <summary>
/// R28-B.6 Blocker-4：Retrieval 专用输入。完整保留原 ContextRetrievalRequest 语义。
/// </summary>
public sealed record RetrievalInput
{
    /// <summary>改写后的查询文本（如 query rewriting 后的结果）。</summary>
    public string? RewrittenQueryText { get; init; }

    /// <summary>必需 tag 列表（命中所有 tag 才入选）。</summary>
    public IReadOnlyList<string> RequiredTags { get; init; } = [];

    /// <summary>必需类型列表。</summary>
    public IReadOnlyList<string> RequiredTypes { get; init; } = [];

    /// <summary>必需 ID 列表（mandatory recall）。</summary>
    public IReadOnlyList<string> RequiredIds { get; init; } = [];

    /// <summary>外部 refs（强制召回）。</summary>
    public IReadOnlyList<string> Refs { get; init; } = [];

    /// <summary>查询向量（语义召回用）。</summary>
    public IReadOnlyList<float> QueryVector { get; init; } = [];

    /// <summary>embedding 模型名。</summary>
    public string? ModelName { get; init; }

    /// <summary>embedding query instruction（如 BGE 前缀）。</summary>
    public string? QueryInstruction { get; init; }

    /// <summary>候选 take（粗排上限）。</summary>
    public int CandidateTake { get; init; }

    /// <summary>向量召回 TopK。</summary>
    public int VectorTopK { get; init; }

    /// <summary>向量召回最低分数。</summary>
    public double? MinVectorScore { get; init; }

    /// <summary>关系扩展允许的关系类型；为空表示不限制。</summary>
    public IReadOnlyList<string> AllowedRelationTypes { get; init; } = [];

    /// <summary>关系扩展最大跳数。</summary>
    public int RelationExpansionDepth { get; init; }

    /// <summary>是否启用关键词召回。</summary>
    public bool IncludeKeywordRecall { get; init; } = true;

    /// <summary>是否启用向量召回。</summary>
    public bool IncludeVectorRecall { get; init; } = true;

    /// <summary>是否启用关系扩展。</summary>
    public bool IncludeRelationExpansion { get; init; } = true;

    /// <summary>是否启用短期记忆召回。</summary>
    public bool IncludeWorkingMemory { get; init; } = true;

    // --- R28-B.6 P0-2：补齐原 ContextRetrievalRequest 完整语义 ---

    /// <summary>R28-B.6 P0-2：是否启用稳定记忆召回（默认 true）。</summary>
    public bool IncludeStableMemory { get; init; } = true;

    /// <summary>R28-B.6 P0-2：是否在召回结果中包含候选正文 Content（默认 true）。</summary>
    public bool IncludeContent { get; init; } = true;

    /// <summary>R28-B.6 P0-2：附加元数据（透传到 Provider/Projector 用于诊断或策略）。</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// R28-B.6 P0-2：RetrievalPlan 序列化字符串（简化为 string，避免引入完整 RetrievalPlan 类型耦合）。
    /// 用于 Provider 在需要时读取 plan 中的细粒度配置。
    /// </summary>
    public string? Plan { get; init; }
}

/// <summary>
/// R28-B.6 Blocker-4：Package 专用输入。
/// </summary>
public sealed record PackageInput
{
    /// <summary>必需 tag 列表。</summary>
    public IReadOnlyList<string> RequiredTags { get; init; } = [];

    /// <summary>必需类型列表。</summary>
    public IReadOnlyList<string> RequiredTypes { get; init; } = [];

    /// <summary>必需 ID 列表（mandatory recall）。</summary>
    public IReadOnlyList<string> RequiredIds { get; init; } = [];

    /// <summary>查询向量（语义召回用）。</summary>
    public IReadOnlyList<float> QueryVector { get; init; } = [];

    /// <summary>embedding 模型名。</summary>
    public string? ModelName { get; init; }

    /// <summary>embedding query instruction。</summary>
    public string? QueryInstruction { get; init; }

    /// <summary>候选 take。</summary>
    public int CandidateTake { get; init; }

    /// <summary>向量召回 TopK。</summary>
    public int VectorTopK { get; init; }

    /// <summary>向量召回最低分数。</summary>
    public double? MinVectorScore { get; init; }

    /// <summary>section 比例（覆盖 policy 默认 SectionRatios）。</summary>
    public IReadOnlyDictionary<string, double>? SectionRatios { get; init; }
}

/// <summary>
/// R28-B.6 Blocker-4：AgentContext 专用输入。
/// </summary>
public sealed record AgentInput
{
    /// <summary>Agent session ID（用于 AgentContext 投影）。</summary>
    public AgentSessionId? Session { get; init; }

    /// <summary>必需 tag 列表。</summary>
    public IReadOnlyList<string> RequiredTags { get; init; } = [];

    /// <summary>必需 ID 列表（mandatory recall）。</summary>
    public IReadOnlyList<string> RequiredIds { get; init; } = [];
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
/// P0-5：所有字段必须非空。EntityVersion 应为 stable content hash 或显式版本号。
/// 使用 <see cref="Create"/> 工厂方法进行验证；直接调用 primary constructor 不做校验
/// （保留为 internal 供 record struct 序列化/反序列化使用）。
/// </remarks>
public readonly record struct CanonicalCandidateKey(
    string WorkspaceId,
    string CollectionId,
    string EntityKind,
    string EntityId,
    string EntityVersion)
{
    /// <summary>
    /// P0-5：创建并验证 CanonicalCandidateKey。所有字段必须非空。
    /// EntityVersion 必须非空（显式版本号或 stable content hash）。
    /// </summary>
    public static CanonicalCandidateKey Create(
        string workspaceId,
        string collectionId,
        string entityKind,
        string entityId,
        string entityVersion)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("WorkspaceId must be non-empty", nameof(workspaceId));
        if (string.IsNullOrEmpty(collectionId))
            throw new ArgumentException("CollectionId must be non-empty", nameof(collectionId));
        if (string.IsNullOrEmpty(entityKind))
            throw new ArgumentException("EntityKind must be non-empty", nameof(entityKind));
        if (string.IsNullOrEmpty(entityId))
            throw new ArgumentException("EntityId must be non-empty", nameof(entityId));
        if (string.IsNullOrEmpty(entityVersion))
            throw new ArgumentException(
                "EntityVersion must be non-empty (use stable content hash or explicit version)",
                nameof(entityVersion));
        return new CanonicalCandidateKey(workspaceId, collectionId, entityKind, entityId, entityVersion);
    }

    /// <summary>
    /// P0-5：验证此 key 的所有字段是否非空。
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrEmpty(WorkspaceId)
        && !string.IsNullOrEmpty(CollectionId)
        && !string.IsNullOrEmpty(EntityKind)
        && !string.IsNullOrEmpty(EntityId)
        && !string.IsNullOrEmpty(EntityVersion);
}

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
    private string _content = string.Empty;

    /// <summary>对应的规范化候选标识。</summary>
    public required CanonicalCandidateKey Key { get; init; }

    /// <summary>
    /// 候选正文内容。
    /// </summary>
    /// <remarks>
    /// 运行时非空校验：编译器已通过 required 保证编译期非 null，
    /// 此 init accessor 对反射/序列化等绕过场景补充运行时检查，
    /// 调用方赋 null 时立即抛出 ArgumentNullException。
    /// </remarks>
    public required string Content
    {
        get => _content;
        init => _content = value ?? throw new ArgumentNullException(nameof(Content));
    }

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

    /// <summary>
    /// R28-B.6 Blocker-6：评估候选集合的准入，返回分区结果（Admitted + Rejected）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Evaluate"/> 的差异：批量评估并返回分区结果，
    /// 调用方可保留 Rejected 候选到 <c>DroppedEnvelopes</c>（而非直接丢弃），
    /// 让 Early Gate 失败的候选仍可被 trace / 解释。
    /// </remarks>
    /// <param name="envelopes">待评估的候选集合。</param>
    /// <param name="snapshot">有效策略快照。</param>
    /// <returns>分区结果（Admitted + Rejected + RejectReasons）。</returns>
    AdmissionPartition EvaluateBatch(IReadOnlyList<ContextCandidateEnvelope> envelopes, EffectivePolicySnapshot snapshot);
}

/// <summary>
/// R28-B：准入结果。
/// </summary>
public sealed record AdmissionResult(
    bool Admitted,
    string ReasonCode,
    string Detail);

/// <summary>
/// R28-B.6 Blocker-6：准入分区结果。
/// </summary>
/// <param name="Admitted">通过 Early Admission Gate 的候选集合。</param>
/// <param name="Rejected">被 Early Admission Gate 拒绝的候选集合（保留到 DroppedEnvelopes，不丢失）。</param>
/// <param name="RejectReasons">被拒绝候选的 reason code（按 CanonicalCandidateKey 索引）。</param>
public sealed record AdmissionPartition(
    IReadOnlyList<ContextCandidateEnvelope> Admitted,
    IReadOnlyList<ContextCandidateEnvelope> Rejected,
    IReadOnlyDictionary<CanonicalCandidateKey, string> RejectReasons);

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
/// R28-B.6 P0-5：分配上下文。携带 Purpose + Budget + MandatoryOverflowPolicy + TokenizerVersion。
/// Allocator 不应在构造函数中固定 Purpose 相关策略（如 MandatoryOverflowPolicy），
/// 应在每次 Allocate 时根据 context 选择策略。
/// </summary>
public sealed record AllocationContext
{
    /// <summary>业务用途轴（决定 mandatory overflow 默认策略）。</summary>
    public required ContextDecisionPurpose Purpose { get; init; }

    /// <summary>有效预算 profile（已合并 override）。</summary>
    public required BudgetProfile Budget { get; init; }

    /// <summary>
    /// Mandatory 候选超出预算时的处理策略。
    /// 显式指定时覆盖 Purpose 默认策略（AgentContext → FailClosed，Retrieval/Package → AllowOverflowWithDiagnostic）。
    /// </summary>
    public required MandatoryOverflowPolicy MandatoryOverflowPolicy { get; init; }

    /// <summary>tokenizer 版本（可选，用于诊断）。</summary>
    public string? TokenizerVersion { get; init; }
}

/// <summary>
/// R28-B：统一全局分配器。消费 SectionRatios + TopK + TokenBudget。
/// </summary>
/// <remarks>
/// 产出 CandidateAllocationDecision，不污染 Envelope。
/// diversity extension point 存在但 rule-only convergence 阶段禁用行为变更。
/// R28-B.6 P0-5：新增接受 AllocationContext 的重载。Allocator 不应在构造函数中固定
/// Purpose 相关策略，应在每次 Allocate 时根据 context 选择策略（如 AgentContext 默认 FailClosed）。
/// 旧重载保留向后兼容（测试 / Legacy 路径使用）。
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

    /// <summary>
    /// R28-B.6 P0-5：执行全局预算分配，接受 AllocationContext（携带 Purpose + MandatoryOverflowPolicy）。
    /// Allocator 根据 context.Purpose 选择默认 MandatoryOverflowPolicy（如 AgentContext → FailClosed）；
    /// 若 context.MandatoryOverflowPolicy 显式指定，则覆盖 Purpose 默认策略。
    /// </summary>
    /// <param name="envelopes">待分配的候选集合。</param>
    /// <param name="snapshot">有效策略快照。</param>
    /// <param name="context">分配上下文（Purpose + Budget + MandatoryOverflowPolicy + TokenizerVersion）。</param>
    /// <returns>分配结果（Selected + Dropped + Decisions + Outcome，含 MandatoryOverflow 诊断）。</returns>
    AllocationResult Allocate(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot,
        AllocationContext context);
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

    /// <summary>
    /// P0-7：将决策结果 + 候选正文 + 投影上下文投影为 AgentContextSnapshot。
    /// 使用 context.AgentSession 而非构造假 session ID。
    /// </summary>
    /// <param name="result">决策结果。</param>
    /// <param name="workingSet">候选正文 sidecar。</param>
    /// <param name="context">投影上下文（含真实 AgentSessionId）。</param>
    /// <returns>AgentContextSnapshot。</returns>
    AgentContextSnapshot Project(ContextDecisionResult result, CandidateWorkingSet workingSet, ProjectionContext context);
}

/// <summary>
/// R28-B P0-7：投影上下文。携带调用方提供的 session 信息与作用域，
/// 供 Projector 构造真实 AgentSessionId 而非伪造的 session。
/// </summary>
public sealed record ProjectionContext
{
    /// <summary>真实 Agent session ID（null = 未运行在 Agent 上下文中，Projector 可回退到伪造 session）。</summary>
    public AgentSessionId? AgentSession { get; init; }

    /// <summary>workspace 作用域（从 Request.Scope 传入）。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>collection 作用域（从 Request.Scope 传入）。</summary>
    public string? CollectionId { get; init; }
}

// ---------------------------------------------------------------------------
// R28-B.6 Impl-1：内容截断器
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B.6 Impl-1：内容截断器接口。按 token 数截断候选正文。
/// </summary>
/// <remarks>
/// Allocator 在预算不足时仅做账面截断（IncludedTokens=remaining, IsTruncated=true），
/// 但实际正文可能超出预算。Projector 在恢复 Material.Content 时使用此截断器
/// 真正裁剪正文，确保模型输入不超过预算。
/// </remarks>
public interface IContentTruncator
{
    /// <summary>按指定 token 数截断内容，返回截断后的内容和实际 token 数。</summary>
    /// <param name="content">原始正文。</param>
    /// <param name="maxTokens">允许的最大 token 数。</param>
    /// <returns>截断结果（含截断后正文、实际 token 数、是否发生截断）。</returns>
    TruncationResult Truncate(string content, int maxTokens);

    /// <summary>R28-B.7：计算完整序列的 token 数（包括所有 section、separator、header）。</summary>
    /// <param name="content">待计算的内容（可以是单个候选正文、section 拼接或完整序列化 package）。</param>
    /// <param name="modelName">tokenizer 使用的模型名（可选，null 时使用截断器默认模型）。</param>
    /// <returns>内容的 token 数。</returns>
    int CountTokens(string content, string? modelName = null);
}

/// <summary>R28-B.6 Impl-1：截断结果。</summary>
/// <param name="TruncatedContent">截断后的正文（不超过 maxTokens 对应的字符数）。</param>
/// <param name="ActualTokens">截断后正文的实际 token 估算数。</param>
/// <param name="WasTruncated">是否发生了截断。</param>
public sealed record TruncationResult(string TruncatedContent, int ActualTokens, bool WasTruncated);
