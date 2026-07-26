using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

// ===========================================================================
// R18-2：统一决策引擎接口契约（Context Decision Engine Interface Contracts）
//
// 目标：
//   在 R18-1 envelope 契约之上定义 Engine 接口，让 Retrieval 与 Package
//   两条主链可以通过统一入口（IContextDecisionEngine.DecideAsync）消费
//   envelope 集合，并通过不同 Projector 输出最终 DTO。
//
// 设计原则：
//   1. Engine 接口仅定义"编排契约"，不强制替换 HybridContextRetriever /
//      BasicContextPackageBuilder 两条主链。R18-2 阶段只在 ContextDecisionProjector
//      内部增加 envelope-to-decision 投影路径，保留现有 ProjectPackage /
//      ProjectRetrieval 入口不变（向后兼容）。
//   2. Planner 是编排核心：编排 candidate collectors → feature pipeline →
//      safety gate → utility scorer → budget allocator 五个阶段。
//      但 R18-2 阶段仅定义编排接口和默认实现骨架，不实现具体阶段
//      （R18 V2 才真正统一执行链）。
//   3. Projector 接口让 Retrieval 与 Package 两条路径可以独立投影 envelope
//      集合到现有 ContextRetrievalResult / ContextPackageBuildResult，
//      保持输出格式分离（避免 God Object）。
//   4. Request/Result 不耦合具体存储或调用方；Engine 是纯内存编排。
//   5. 复用 ContextDecisionSource 枚举区分 Package/Retrieval 输出方向。
//
// 子阶段进度：
//   R18-2（当前）：Engine 接口 + Planner 骨架 + Projector 投影路径。
//                  不替换两条主链，只新增 envelope-to-decision 投影。
//   R18-3：Retrieval adapter（ContextRetrievalCandidate → Envelope）。
//   R18-4：Package adapter（PackageTraceCandidate → Envelope）。
// ===========================================================================

/// <summary>
/// R18-2：决策引擎请求。统一 Retrieval 与 Package 两条路径的入口请求，
/// 携带 envelope 集合 + 可选的 PolicyBundle 引用 + workspace/collection 作用域。
/// </summary>
/// <remarks>
/// 设计澄清（用户澄清 #3）：Request Policy 是受限 override，不允许替换安全边界和正式模型。
/// 即 Request.PolicyOverride 只能调整非安全相关参数（如 TopK、token budget、section ratios），
/// 不能替换 SafetyPolicyProfile / ModelArtifactReference。
/// </remarks>
public sealed class ContextDecisionRequest
{
    /// <summary>请求唯一 ID（用于 trace 溯源）。</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>决策来源：Package 或 Retrieval。决定使用哪个 Projector。</summary>
    public ContextDecisionSource DecisionSource { get; init; } = ContextDecisionSource.Package;

    /// <summary>workspace 作用域（必填）。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>collection 作用域（必填）。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>参与决策的候选 envelope 集合（必填，至少 1 条）。</summary>
    public IReadOnlyList<ContextCandidateEnvelope> Candidates { get; init; }
        = Array.Empty<ContextCandidateEnvelope>();

    /// <summary>整体 token 预算上限（必填，>0）。</summary>
    /// <remarks>
    /// Engine 内部根据 DecisionSource 选择不同的 BudgetAllocator：
    /// Retrieval 路径使用全局硬上限语义；
    /// Package 路径使用 section 级分层比例分配 + 部分截断接受语义。
    /// </remarks>
    public int TokenBudget { get; init; }

    /// <summary>TopK 候选数上限（Retrieval 路径使用；Package 路径忽略）。</summary>
    public int TopK { get; init; } = int.MaxValue;

    /// <summary>section 比例分配（Package 路径使用；Retrieval 路径忽略）。</summary>
    /// <remarks>key = section 名，value = 比例 [0,1]。null 时使用 PolicyBundle 默认比例。</remarks>
    public IReadOnlyDictionary<string, double>? SectionRatios { get; init; }

    /// <summary>PolicyBundle 引用（可选；null 时使用全局默认 bundle）。</summary>
    /// <remarks>
    /// 设计澄清（用户澄清 #3）：此处仅为引用 ID，不允许直接传入完整 bundle 替换安全边界。
    /// 安全边界（SafetyPolicyProfile）始终由全局 bundle 决定，不允许 per-request override。
    /// </remarks>
    public string? PolicyBundleId { get; init; }

    /// <summary>请求创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>关联的查询文本（可选，用于 trace）。</summary>
    public string? QueryText { get; init; }

    /// <summary>请求级 model enable 标志（false = 强制 deterministic-only）。</summary>
    /// <remarks>
    /// 设计澄清（用户澄清 #3）：即使 PolicyBundle 引用模型，
    /// Request 可强制关闭模型（用于 golden baseline 验证 / 故障回退测试）。
    /// 此字段不影响安全边界，仅影响 utility scorer 是否调用模型。
    /// </remarks>
    public bool EnableModel { get; init; } = true;

    /// <summary>
    /// R19-3：per-request 受限策略 override。
    /// </summary>
    /// <remarks>
    /// 设计澄清（用户澄清 #3）：
    ///   - 仅允许调整 BudgetProfile 部分字段（DefaultTokenBudget / DefaultTopK / SectionRatios）。
    ///   - 仅允许调整 RoutingProfile.EnableModelScoring（不能替换 ModelArtifactId / weights）。
    ///   - <b>不允许</b> 替换 SafetyProfile（安全边界由 bundle 全局决定）。
    ///   - <b>不允许</b> 替换 ModelArtifactReference（正式模型由 bundle 全局决定）。
    /// null = 使用当前激活 bundle 的 profile（无 override）。
    /// </remarks>
    public ContextPolicyOverride? PolicyOverride { get; init; }

    /// <summary>
    /// R28-B.6：有效策略快照（V2 路径专用）。
    /// </summary>
    /// <remarks>
    /// 由 DefaultContextDecisionRuntime 在委托 Engine 前设置。
    /// Engine 注入 IGlobalAllocator 时，将此 snapshot 传给 Allocator.Execute。
    /// null = Legacy 路径（Engine 使用静态内联分配，向后兼容 R18-2 测试）。
    /// </remarks>
    public EffectivePolicySnapshot? PolicySnapshot { get; init; }

    /// <summary>
    /// R28-B.6 P0-5：分配上下文（V2 路径专用）。
    /// </summary>
    /// <remarks>
    /// 由 DefaultContextDecisionRuntime 在委托 Engine 前设置，携带 Purpose + MandatoryOverflowPolicy。
    /// Engine 在 V2 路径调用 Allocator.Allocate(envelopes, snapshot, context) 时使用此 context。
    /// null = Legacy 路径或无 Allocator 注入（Engine 使用旧 Allocate 重载）。
    /// </remarks>
    public AllocationContext? AllocationContext { get; init; }

    /// <summary>
    /// R29 WP-D-1：Diversity 配置（V2.1 Allocator 路径专用）。
    /// </summary>
    /// <remarks>
    /// 由 DefaultContextDecisionRuntime 从 EffectivePolicySnapshot.DiversityOptions 读取并设置。
    /// Engine 在 V2 路径根据此字段决定走 V2.1 AllocateWithDiversity 还是 V2.0 Allocate：
    ///   - 非空 + IAllocatorV2_1 注入 → AllocateWithDiversity（section rollover + MMR）
    ///   - null 或 IAllocatorV2_1 未注入 → Allocate（V2.0 fallback）
    /// null = 使用 V2.0 Allocator（向后兼容 R28-G 之前的行为）。
    /// </remarks>
    public DiversityOptions? DiversityOptions { get; init; }
}

/// <summary>
/// R18-2：决策引擎结果。统一 Retrieval 与 Package 两条路径的输出，
/// 携带选中的 envelope 集合 + 丢弃的 envelope 集合 + 整体产出摘要。
/// </summary>
/// <remarks>
/// Engine 输出 envelope 集合后，由具体的 IResultProjector（RetrievalResultProjector /
/// PackageResultProjector）投影到现有 DTO（ContextRetrievalResult / ContextPackageBuildResult）。
/// Engine 本身不关心最终输出格式，避免 God Object。
/// </remarks>
public sealed class ContextDecisionResult
{
    /// <summary>关联的请求 ID（与 Request.RequestId 对应）。</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>决策来源（与 Request.DecisionSource 对应）。</summary>
    public ContextDecisionSource DecisionSource { get; init; } = ContextDecisionSource.Package;

    /// <summary>选中的 envelope 集合（按 FinalScore 降序 + CandidateId 升序排列）。</summary>
    public IReadOnlyList<ContextCandidateEnvelope> SelectedEnvelopes { get; init; }
        = Array.Empty<ContextCandidateEnvelope>();

    /// <summary>丢弃的 envelope 集合（包含 BlockReasonCode）。</summary>
    public IReadOnlyList<ContextCandidateEnvelope> DroppedEnvelopes { get; init; }
        = Array.Empty<ContextCandidateEnvelope>();

    /// <summary>本次决策的整体产出摘要（计数 + token）。</summary>
    public ContextDecisionOutcomeSummary Outcome { get; init; } = new();

    /// <summary>本次决策使用的策略版本（来自 PolicyBundle 或默认）。</summary>
    public string PolicyVersion { get; init; } = ContextDecisionPolicyVersions.DecisionSchemaV2_0;

    /// <summary>本次决策使用的模型 artifact 引用（null = 纯 deterministic）。</summary>
    public string? ModelVersion { get; init; }

    /// <summary>决策执行时间（UTC）。</summary>
    public DateTimeOffset DecidedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>是否启用了模型评分（false = 纯 deterministic 路径）。</summary>
    public bool ModelEnabled { get; init; }

    /// <summary>R28-B：业务用途轴（Retrieval / Package / AgentContext）。</summary>
    public ContextDecisionPurpose Purpose { get; init; } = ContextDecisionPurpose.Retrieval;

    /// <summary>R28-B：运行实现轴（Legacy / UnifiedV2）。</summary>
    public ContextDecisionRuntimeKind RuntimeKind { get; init; } = ContextDecisionRuntimeKind.Legacy;

    /// <summary>R28-B：Allocator 产出的分配决策（与 Envelope 解耦）。</summary>
    public IReadOnlyList<CandidateAllocationDecision> AllocationDecisions { get; init; }
        = Array.Empty<CandidateAllocationDecision>();

    /// <summary>R28-B：策略引用（provenance；null = 未绑定到有效策略快照）。</summary>
    public ResolvedPolicyReference? PolicyReference { get; init; }
}

/// <summary>
/// R18-2：决策产出摘要。复用 ContextDecisionOutcome 的核心字段但适配 envelope 集合语义。
/// </summary>
public sealed class ContextDecisionOutcomeSummary
{
    /// <summary>选中的候选数。</summary>
    public int SelectedCount { get; init; }

    /// <summary>丢弃的候选数。</summary>
    public int DroppedCount { get; init; }

    /// <summary>选中候选的有效 token 总数（基于 TokenCost.ContentTokens 精确计算）。</summary>
    /// <remarks>
    /// R29 WP-D-3：权威 token 汇总字段。Allocator / Projector 应基于此字段做预算验证。
    /// 旧字段 <see cref="EstimatedTokens"/> 保留为 [Obsolete] 别名，委托到此字段。
    /// </remarks>
    public int EffectiveTokens { get; init; }

    /// <summary>选中候选的 token 数（[Obsolete] 别名，委托到 <see cref="EffectiveTokens"/>）。</summary>
    [Obsolete("Use EffectiveTokens. EstimatedTokens is retained as alias for backward compatibility.")]
    public int EstimatedTokens
    {
        get => EffectiveTokens;
        init => EffectiveTokens = value;
    }

    /// <summary>请求的 token 预算上限。</summary>
    public int TokenBudget { get; init; }

    /// <summary>本次决策涉及的 section 名称集合（package 场景；retrieval 为空）。</summary>
    public IReadOnlyList<string> Sections { get; init; } = Array.Empty<string>();

    /// <summary>本次决策的 safety gate 拦截数（PassesSafetyGate=false 的候选数）。</summary>
    public int SafetyGateBlockedCount { get; init; }

    /// <summary>本次决策的 budget 拦截数（因 token budget 超限被丢弃的候选数）。</summary>
    public int BudgetExceededCount { get; init; }

    /// <summary>
    /// R28-B.6 Impl-2：决策诊断字典。记录 mandatory overflow / hard window violation 等
    /// 无法用标量字段表达的诊断信息（key=诊断名，value=字符串值）。
    /// </summary>
    public IReadOnlyDictionary<string, string> Diagnostics { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// R18-2：统一决策引擎接口。编排 envelope 集合的 safety gate → utility scoring →
/// budget allocation 五个阶段，输出 SelectedEnvelopes + DroppedEnvelopes 集合。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. Engine 不感知输出格式（RetrievalResult / PackageBuildResult），
///      由调用方通过 IResultProjector 投影。
///   2. Engine 不依赖具体存储；候选 envelope 由调用方通过 Request.Candidates 传入。
///   3. Engine 失败时（如 PolicyBundle 加载失败）必须能回退到 deterministic policy
///      （验收标准 #6）。
///   4. Engine 是幂等的：相同 Request 应产生相同 Result（确定性 tie-break）。
/// </remarks>
public interface IContextDecisionEngine
{
    /// <summary>
    /// 对候选 envelope 集合执行 safety gate → utility scoring → budget allocation 决策。
    /// </summary>
    /// <param name="request">决策请求（必填 Candidates + TokenBudget + DecisionSource）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 决策结果（SelectedEnvelopes + DroppedEnvelopes + Outcome 摘要）。
    /// 失败时回退到 deterministic policy 而非抛异常（除非 request 本身非法）。
    /// </returns>
    Task<ContextDecisionResult> DecideAsync(
        ContextDecisionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R18-2：结果投影器接口。让 Retrieval 与 Package 两条路径可以独立投影
/// envelope 集合到现有 DTO（ContextRetrievalResult / ContextPackageBuildResult）。
/// </summary>
/// <typeparam name="TResult">目标 DTO 类型（ContextRetrievalResult / ContextPackageBuildResult）。</typeparam>
/// <remarks>
/// 设计原则：
///   1. Projector 仅做"格式投影"，不改变决策结果（envelope 集合不变）。
///   2. Projector 是幂等的：相同 Result 应产生相同 DTO。
///   3. Projector 不调用 Engine 或 Storage；纯内存转换。
/// </remarks>
public interface IResultProjector<TResult>
{
    /// <summary>
    /// 将决策结果投影为目标 DTO。
    /// </summary>
    /// <param name="result">决策结果（SelectedEnvelopes + DroppedEnvelopes）。</param>
    /// <returns>目标 DTO（RetrievalResult / PackageBuildResult 等）。</returns>
    TResult Project(ContextDecisionResult result);

    /// <summary>
    /// P0-7：将决策结果 + 候选正文 sidecar 投影为目标 DTO。
    /// Projector 从 workingSet.Materials 恢复候选 Content，从 result.AllocationDecisions
    /// 消费 Section / IncludedTokens / IsTruncated。
    /// </summary>
    /// <param name="result">决策结果（含 AllocationDecisions）。</param>
    /// <param name="workingSet">候选正文 sidecar（按 CanonicalKey 索引 Material）。</param>
    /// <returns>目标 DTO。</returns>
    TResult Project(ContextDecisionResult result, CandidateWorkingSet workingSet);
}

// ===========================================================================
// R29 WP-F-3：性能监控 + 自动回退阈值契约
//
// 目标：
//   1. 提供 IPerformanceMonitor 抽象，让 DefaultContextDecisionEngine 在 V2 路径
//      执行前后埋点：执行前查询是否应回退到 V2.0 Allocator；执行后记录本次耗时。
//   2. 当 V2 路径（含 V2.1 AllocateWithDiversity）执行时间超过阈值（可配置，默认 500ms）
//      时，标记该 scope（workspaceId+collectionId）需要回退；下次请求 Engine 跳过 V2.1
//      直接走 V2.0 Allocate，避免性能回退拖累主链。
//   3. 接口仅定义"观察 + 查询"契约；具体实现（ring buffer / DDSketch / 持久化 metric store）
//      由 ContextCore.Core 提供，可被生产实现替换。
//
// 设计原则：
//   1. 接口极薄：仅 3 个方法（RecordExecutionTime / ShouldFallbackToV20 / RecordFallback）。
//   2. 接口可选注入：DefaultContextDecisionEngine 在 IPerformanceMonitor 为 null 时
//      保持旧行为（不监控、不回退），向后兼容 R28-G 之前的测试。
//   3. 接口无副作用：调用方负责实际路径切换；Monitor 仅返回布尔值并提供诊断信息。
//   4. 线程安全：实现应线程安全；多个 Engine 实例可能并发调用同一 Monitor。
// ===========================================================================

/// <summary>
/// R29 WP-F-3：性能监控 + 自动回退阈值配置。
/// </summary>
/// <remarks>
/// 当 V2 路径执行时间超过 <see cref="ThresholdMs"/> 时，标记该 scope 需要 V2.0 回退；
/// <see cref="RecoverySamples"/> 个连续低于阈值的样本后解除回退状态（自愈）。
/// P5：新增 <see cref="ComponentPolicies"/> 支持组件级回退阈值（每组件独立 P95 阈值），
/// 与整体 V2 路径阈值 <see cref="ThresholdMs"/> 共存：整体阈值作为兜底，
/// 组件阈值用于细粒度归因与回退（由 IComponentHealthRegistry 使用）。
/// </remarks>
public sealed record PerformanceFallbackOptions
{
    /// <summary>触发回退的执行时间阈值（毫秒，默认 500ms）。</summary>
    public int ThresholdMs { get; init; } = 500;

    /// <summary>每个 scope 保留的最近样本数（ring buffer 容量，默认 16）。</summary>
    public int SampleWindow { get; init; } = 16;

    /// <summary>触发回退的最小样本数（避免冷启动单次抖动误判，默认 3）。</summary>
    public int MinSamplesBeforeFallback { get; init; } = 3;

    /// <summary>解除回退状态所需的连续低于阈值样本数（默认 5）。</summary>
    public int RecoverySamples { get; init; } = 5;

    /// <summary>
    /// P5：组件级回退策略（每组件独立 P95 阈值）。由 IComponentHealthRegistry 使用。
    /// 为 null 或空时使用 ComponentFallbackOptions.Default 默认策略。
    /// 保留 <see cref="ThresholdMs"/> 等 V2 整体阈值作为兜底（IPerformanceMonitor 使用）。
    /// </summary>
    public Dictionary<ComponentKind, ComponentFallbackPolicy>? ComponentPolicies { get; init; }

    /// <summary>
    /// P5：获取指定组件的回退策略。
    /// 优先使用 <see cref="ComponentPolicies"/>；未配置时回退到 <see cref="ComponentFallbackOptions.Default"/> 默认策略。
    /// </summary>
    /// <param name="kind">组件类型。</param>
    /// <returns>该组件的回退策略。</returns>
    public ComponentFallbackPolicy GetComponentPolicy(ComponentKind kind)
    {
        if (ComponentPolicies is not null && ComponentPolicies.TryGetValue(kind, out var policy))
        {
            return policy;
        }
        return ComponentFallbackOptions.Default.GetPolicy(kind);
    }

    /// <summary>默认配置（阈值 500ms / 窗口 16 / 最小样本 3 / 恢复样本 5 / 组件策略默认）。</summary>
    public static PerformanceFallbackOptions Default { get; } = new();
}

/// <summary>
/// R29 WP-F-3：性能监控抽象。让 Engine 在 V2 路径执行前查询是否应回退到 V2.0 Allocator，
/// 执行后记录耗时。实现可选择任意 metric store（in-memory ring buffer / DDSketch / Prometheus）。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 接口线程安全；实现应支持多 Engine 实例并发调用。
///   2. scope（workspaceId + collectionId）是隔离单元：一个 scope 触发回退不影响其他 scope。
///   3. 回退状态可恢复：低于阈值的连续样本累积到 <see cref="PerformanceFallbackOptions.RecoverySamples"/>
///      后，<see cref="ShouldFallbackToV20"/> 返回 false（自愈）。
/// </remarks>
public interface IPerformanceMonitor
{
    /// <summary>获取当前生效的回退阈值配置。</summary>
    PerformanceFallbackOptions Options { get; }

    /// <summary>
    /// 记录一次 V2 路径执行的耗时。
    /// </summary>
    /// <param name="scopeKey">scope 标识（通常是 workspaceId + "/" + collectionId）。</param>
    /// <param name="durationMs">本次执行耗时（毫秒）。</param>
    /// <param name="usedV21Path">本次是否走了 V2.1 AllocateWithDiversity 路径。</param>
    void RecordExecutionTime(string scopeKey, double durationMs, bool usedV21Path);

    /// <summary>
    /// 查询指定 scope 当前是否应回退到 V2.0 Allocator（避免 V2.1 性能回退）。
    /// </summary>
    /// <param name="scopeKey">scope 标识（workspaceId + "/" + collectionId）。</param>
    /// <returns>true = 应回退到 V2.0；false = 可继续走 V2.1。</returns>
    bool ShouldFallbackToV20(string scopeKey);

    /// <summary>
    /// 记录一次回退事件（Engine 因阈值触发而切换到 V2.0 路径）。
    /// 用于诊断与可观测性（不改变 ShouldFallbackToV20 状态）。
    /// </summary>
    /// <param name="scopeKey">scope 标识。</param>
    /// <param name="reason">回退原因（如 "p95_exceeded_threshold"）。</param>
    /// <param name="lastDurationMs">触发回退的最近一次执行耗时（毫秒）。</param>
    void RecordFallback(string scopeKey, string reason, double lastDurationMs);

    /// <summary>
    /// 获取指定 scope 的诊断快照（用于 Result.Diagnostics 投影）。
    /// </summary>
    /// <param name="scopeKey">scope 标识。</param>
    /// <returns>诊断键值对（如 fallback_triggered / threshold_ms / recent_p95_ms / sample_count）。</returns>
    IReadOnlyDictionary<string, string> GetDiagnostics(string scopeKey);
}
