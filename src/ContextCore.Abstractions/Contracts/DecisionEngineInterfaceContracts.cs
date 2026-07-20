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

    /// <summary>选中候选的总 token 数。</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>请求的 token 预算上限。</summary>
    public int TokenBudget { get; init; }

    /// <summary>本次决策涉及的 section 名称集合（package 场景；retrieval 为空）。</summary>
    public IReadOnlyList<string> Sections { get; init; } = Array.Empty<string>();

    /// <summary>本次决策的 safety gate 拦截数（PassesSafetyGate=false 的候选数）。</summary>
    public int SafetyGateBlockedCount { get; init; }

    /// <summary>本次决策的 budget 拦截数（因 token budget 超限被丢弃的候选数）。</summary>
    public int BudgetExceededCount { get; init; }
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
}
