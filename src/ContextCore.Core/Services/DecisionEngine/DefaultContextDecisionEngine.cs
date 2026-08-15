using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// / 统一决策引擎默认实现（DefaultContextDecisionEngine）
//
// 目标：
// 实现 IContextDecisionEngine 接口，编排 envelope 集合的
// safety gate → utility scoring → budget allocation 三个阶段，
// 输出 SelectedEnvelopes + DroppedEnvelopes 集合。
//
// 设计原则：
// 1. 不替换 HybridContextRetriever / BasicContextPackageBuilder 两条主链。
// Engine 仅作为可选编排路径，由调用方在 adapter（/）
// 阶段决定是否接入。
// 2. Engine 是纯内存编排，不调用任何存储；候选 envelope 由调用方传入。
// 3. Engine 是幂等的：相同 Request 产生相同 Result（确定性 tie-break）。
// 4. Engine 失败时回退到 deterministic policy（ModelConfidence=0 + FinalScore=DeterministicScore），
// 不抛异常（除非 Request 本身非法）。
// 5. 可选注入 IPolicyRegistry。当 registry 可用时，Engine 通过
// GetActiveBundleAsync(workspaceId, collectionId) 解析当前激活 bundle，
// 应用 Safety/Budget/Routing 三个 profile。未注入时使用 hardcoded defaults
// 保持向后兼容。
//
// 阶段化处理流程：
// 1. PolicyBundle 解析（新增）：
// 若 _policyRegistry 可用且 request.PolicyBundleId 为空 → 调用
// GetActiveBundleAsync(request.WorkspaceId, request.CollectionId) 解析激活 bundle。
// 应用 per-request PolicyOverride（受限：仅 Budget + Routing.EnableModelScoring）。
// 2. SafetyGate：根据 envelope.Safety + bundle.Safety 分离 passing / blocked
// - 候选 PassesSafetyGate=false（adapter 预先标记）→ 直接 blocked
// - IsSuperseded / IsRequiredTagMismatch → 永远 blocked（不受 bundle 控制）
// - IsDeprecatedUsedByActiveChain && !bundle.Safety.AllowDeprecatedUsedByActiveChain → blocked
// - IsDuplicate && !bundle.Safety.AllowDuplicateReference → blocked
// 3. UtilityScoring：应用 deterministic scoring（mandatory 优先 + score + tie-break）
// + 可选 model scoring（bundle.Routing.EnableModelScoring + ModelConfidenceThreshold）
// + Model failure 精确回退（FinalScore=DeterministicScore, ModelScore=null）
// 4. BudgetAllocation：根据 DecisionSource 选择不同策略
// - Retrieval：全局硬上限（按 TopK + TokenBudget 截断）
// - Package：section 级分层比例分配（阶段使用简化版，section ratios 留待后续细化）
// - bundle.Budget.DefaultTokenBudget / DefaultTopK 作为 request 字段为空时的兜底
// 5. 输出：SelectedEnvelopes（按 FinalScore 降序 + CandidateId 升序）
// + DroppedEnvelopes（含 BlockReasonCode）
// + PolicyVersion（来自 bundle.Policies.DecisionSchemaVersion）
// + ModelVersion（来自 bundle.Routing.ModelArtifactId 或候选 ModelArtifactRef）
// ===========================================================================

/// <summary>
/// / / 默认决策引擎实现。编排 envelope 集合的
/// safety gate → lifecycle gate → utility scoring → budget allocation 四个阶段。
/// </summary>
/// <remarks>
/// Closure Gate：
/// - 当注入 ISafetyGate/ILifecycleGate/IUtilityScorer/IGlobalAllocator 且 request.PolicySnapshot 非空时，
/// 走 V2 路径（委托注入的抽象执行全部四阶段）。
/// - 当任一抽象为 null 或 PolicySnapshot 为空时，走 Legacy 静态路径（向后兼容 测试）。
/// - Runtime 不再在 Engine 前执行 Safety/Lifecycle/Score（消除重复）。
/// </remarks>
public sealed class DefaultContextDecisionEngine : IContextDecisionEngine
{
    private readonly IPolicyRegistry? _policyRegistry;
    private readonly ISafetyGate? _safetyGate;
    private readonly ILifecycleGate? _lifecycleGate;
    private readonly IUtilityScorer? _utilityScorer;
    private readonly IGlobalAllocator? _globalAllocator;
    private readonly IAllocatorV2_1? _allocatorV2_1;
    private readonly IPerformanceMonitor? _performanceMonitor;
    private readonly IComponentHealthRegistry? _componentHealthRegistry;
    private readonly ICandidateReranker? _reranker;

    // Legacy 路径的 SafetyGate 评估复用默认实现（无状态，可安全共享）。
    private static readonly DefaultSafetyGate SafetyGateEvaluator = new();

    /// <summary>构造默认 Engine（无注入；使用静态内联逻辑，向后兼容 行为）。</summary>
    public DefaultContextDecisionEngine()
        : this(policyRegistry: null)
    {
    }

    /// <summary>构造 Engine 并注入可选 PolicyRegistry（Legacy 路径）。</summary>
    public DefaultContextDecisionEngine(IPolicyRegistry? policyRegistry)
        : this(policyRegistry, safetyGate: null, lifecycleGate: null, utilityScorer: null, globalAllocator: null)
    {
    }

    /// <summary>
    /// 构造 Engine 并注入全部 V2 决策抽象。
    /// </summary>
    /// <param name="policyRegistry">策略注册表（null 时使用 hardcoded defaults）。</param>
    /// <param name="safetyGate">Safety Gate 评估器（null 时走 Legacy 静态路径）。</param>
    /// <param name="lifecycleGate">Lifecycle Gate 评估器（null 时跳过 lifecycle 检查）。</param>
    /// <param name="utilityScorer">效用评分器（null 时走 Legacy 静态路径）。</param>
    /// <param name="globalAllocator">全局分配器（null 时走 Legacy 静态路径）。</param>
    public DefaultContextDecisionEngine(
        IPolicyRegistry? policyRegistry,
        ISafetyGate? safetyGate,
        ILifecycleGate? lifecycleGate,
        IUtilityScorer? utilityScorer,
        IGlobalAllocator? globalAllocator)
        : this(policyRegistry, safetyGate, lifecycleGate, utilityScorer, globalAllocator, allocatorV2_1: null)
    {
    }

    /// <summary>
    /// 构造 Engine 并注入全部 V2 决策抽象 + V2.1 Allocator。
    /// </summary>
    /// <param name="policyRegistry">策略注册表（null 时使用 hardcoded defaults）。</param>
    /// <param name="safetyGate">Safety Gate 评估器（null 时走 Legacy 静态路径）。</param>
    /// <param name="lifecycleGate">Lifecycle Gate 评估器（null 时跳过 lifecycle 检查）。</param>
    /// <param name="utilityScorer">效用评分器（null 时走 Legacy 静态路径）。</param>
    /// <param name="globalAllocator">全局分配器（V2.0 基础，null 时走 Legacy 静态路径）。</param>
    /// <param name="allocatorV2_1">V2.1 Allocator（section rollover + MMR；null 时回退 V2.0 Allocate）。</param>
    public DefaultContextDecisionEngine(
        IPolicyRegistry? policyRegistry,
        ISafetyGate? safetyGate,
        ILifecycleGate? lifecycleGate,
        IUtilityScorer? utilityScorer,
        IGlobalAllocator? globalAllocator,
        IAllocatorV2_1? allocatorV2_1)
        : this(policyRegistry, safetyGate, lifecycleGate, utilityScorer, globalAllocator, allocatorV2_1, performanceMonitor: null)
    {
    }

    /// <summary>
    /// 构造 Engine 并注入全部 V2 决策抽象 + V2.1 Allocator + 性能监控。
    /// </summary>
    /// <param name="policyRegistry">策略注册表（null 时使用 hardcoded defaults）。</param>
    /// <param name="safetyGate">Safety Gate 评估器（null 时走 Legacy 静态路径）。</param>
    /// <param name="lifecycleGate">Lifecycle Gate 评估器（null 时跳过 lifecycle 检查）。</param>
    /// <param name="utilityScorer">效用评分器（null 时走 Legacy 静态路径）。</param>
    /// <param name="globalAllocator">全局分配器（V2.0 基础，null 时走 Legacy 静态路径）。</param>
    /// <param name="allocatorV2_1">V2.1 Allocator（section rollover + MMR；null 时回退 V2.0 Allocate）。</param>
    /// <param name="performanceMonitor">性能监控（null 时不监控、不回退，向后兼容 行为）。</param>
    public DefaultContextDecisionEngine(
        IPolicyRegistry? policyRegistry,
        ISafetyGate? safetyGate,
        ILifecycleGate? lifecycleGate,
        IUtilityScorer? utilityScorer,
        IGlobalAllocator? globalAllocator,
        IAllocatorV2_1? allocatorV2_1,
        IPerformanceMonitor? performanceMonitor)
        : this(policyRegistry, safetyGate, lifecycleGate, utilityScorer, globalAllocator,
               allocatorV2_1, performanceMonitor, componentHealthRegistry: null)
    {
    }

    /// <summary>
    /// 构造 Engine 并注入全部 V2 决策抽象 + V2.1 Allocator + 性能监控 + 组件健康注册表。
    /// </summary>
    /// <param name="policyRegistry">策略注册表（null 时使用 hardcoded defaults）。</param>
    /// <param name="safetyGate">Safety Gate 评估器（null 时走 Legacy 静态路径）。</param>
    /// <param name="lifecycleGate">Lifecycle Gate 评估器（null 时跳过 lifecycle 检查）。</param>
    /// <param name="utilityScorer">效用评分器（null 时走 Legacy 静态路径）。</param>
    /// <param name="globalAllocator">全局分配器（V2.0 基础，null 时走 Legacy 静态路径）。</param>
    /// <param name="allocatorV2_1">V2.1 Allocator（section rollover + MMR；null 时回退 V2.0 Allocate）。</param>
    /// <param name="performanceMonitor">性能监控（null 时不监控、不回退，向后兼容 行为）。</param>
    /// <param name="componentHealthRegistry">组件健康注册表（null 时不归因、不回退，向后兼容 之前的行为）。</param>
    public DefaultContextDecisionEngine(
        IPolicyRegistry? policyRegistry,
        ISafetyGate? safetyGate,
        ILifecycleGate? lifecycleGate,
        IUtilityScorer? utilityScorer,
        IGlobalAllocator? globalAllocator,
        IAllocatorV2_1? allocatorV2_1,
        IPerformanceMonitor? performanceMonitor,
        IComponentHealthRegistry? componentHealthRegistry)
        : this(policyRegistry, safetyGate, lifecycleGate, utilityScorer, globalAllocator,
               allocatorV2_1, performanceMonitor, componentHealthRegistry, reranker: null)
    {
    }

    /// <summary>
    /// 构造 Engine 并注入全部 V2 决策抽象 + V2.1 Allocator + 性能监控 + 组件健康注册表 + 两阶段 reranker。
    /// </summary>
    /// <param name="policyRegistry">策略注册表（null 时使用 hardcoded defaults）。</param>
    /// <param name="safetyGate">Safety Gate 评估器（null 时走 Legacy 静态路径）。</param>
    /// <param name="lifecycleGate">Lifecycle Gate 评估器（null 时跳过 lifecycle 检查）。</param>
    /// <param name="utilityScorer">效用评分器（null 时走 Legacy 静态路径）。</param>
    /// <param name="globalAllocator">全局分配器（V2.0 基础，null 时走 Legacy 静态路径）。</param>
    /// <param name="allocatorV2_1">V2.1 Allocator（section rollover + MMR；null 时回退 V2.0 Allocate）。</param>
    /// <param name="performanceMonitor">性能监控（null 时不监控、不回退，向后兼容 行为）。</param>
    /// <param name="componentHealthRegistry">组件健康注册表（null 时不归因、不回退，向后兼容 之前的行为）。</param>
    /// <param name="reranker">两阶段排序第二阶段的确定性 reranker（null 时禁用；EnableTwoStageRerank 也需为 true）。</param>
    public DefaultContextDecisionEngine(
        IPolicyRegistry? policyRegistry,
        ISafetyGate? safetyGate,
        ILifecycleGate? lifecycleGate,
        IUtilityScorer? utilityScorer,
        IGlobalAllocator? globalAllocator,
        IAllocatorV2_1? allocatorV2_1,
        IPerformanceMonitor? performanceMonitor,
        IComponentHealthRegistry? componentHealthRegistry,
        ICandidateReranker? reranker)
    {
        _policyRegistry = policyRegistry;
        _safetyGate = safetyGate;
        _lifecycleGate = lifecycleGate;
        _utilityScorer = utilityScorer;
        _globalAllocator = globalAllocator;
        _allocatorV2_1 = allocatorV2_1;
        _performanceMonitor = performanceMonitor;
        _componentHealthRegistry = componentHealthRegistry;
        _reranker = reranker;
    }

    /// <summary>
    /// 对候选 envelope 集合执行 safety gate → lifecycle gate → utility scoring → budget allocation 决策。
    /// </summary>
    public async Task<ContextDecisionResult> DecideAsync(
        ContextDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // V2 路径 — 当注入全部决策抽象且 PolicySnapshot 非空时，委托注入的抽象执行。
        // Engine 是唯一决策点：Runtime 不再在 Engine 前执行 Safety/Lifecycle/Score。
        if (_safetyGate is not null && _utilityScorer is not null && _globalAllocator is not null
            && request.PolicySnapshot is not null)
        {
            return await ExecuteV2PathAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // ---- Legacy 静态路径（向后兼容 测试） ----

        // 解析 PolicyBundle
        // PolicyBundleId 非空 → 精确加载（fail-closed：找不到则抛异常，不静默回退默认 bundle）
        // PolicyBundleId 为空 → 解析 workspace/collection 激活的 bundle
        ContextPolicyBundle? bundle = null;
        if (_policyRegistry is not null)
        {
            if (!string.IsNullOrEmpty(request.PolicyBundleId))
            {
                bundle = await _policyRegistry.GetBundleAsync(
                    request.PolicyBundleId, version: null, cancellationToken).ConfigureAwait(false);
                if (bundle is null)
                {
                    throw new InvalidOperationException(
                        $"PolicyBundle not found: BundleId={request.PolicyBundleId}. " +
                        "Explicit bundle reference must resolve; fail-closed.");
                }
            }
            else
            {
                bundle = await _policyRegistry.GetActiveBundleAsync(
                    request.WorkspaceId, request.CollectionId, cancellationToken).ConfigureAwait(false);
            }
        }

        // 应用受限 override（合并到 bundle profile，不替换整个 profile）
        // 不允许替换 SafetyProfile；BudgetOverride 仅调整 TokenBudget/TopK/SectionRatios；
        // RoutingOverride 仅调整 EnableModelScoring。
        var safety = bundle?.Safety;
        var budget = DecisionOutcomeRecomputer.ApplyBudgetOverride(bundle?.Budget, request.PolicyOverride?.BudgetOverride);
        var routing = DecisionOutcomeRecomputer.ApplyRoutingOverride(bundle?.Routing, request.PolicyOverride?.RoutingOverride);

        // 阶段 1：Safety Gate — 分离 passing / blocked
        var passing = new List<ContextCandidateEnvelope>();
        var blocked = new List<ContextCandidateEnvelope>();
        foreach (var envelope in request.Candidates)
        {
            var (passes, reason, detail) = EvaluateSafetyGate(envelope, safety);
            if (passes)
            {
                passing.Add(envelope);
            }
            else
            {
                blocked.Add(envelope with
                {
                    Safety = envelope.Safety with
                    {
                        PassesSafetyGate = false,
                        BlockReasonCode = reason,
                        BlockReasonDetail = detail
                    }
                });
            }
        }

        // 阶段 2：Utility Scoring — 应用 ModelConfidenceThreshold + Model failure 回退
        // enableModel：request.EnableModel && routing.EnableModelScoring（routing=null 时视为 permissive）
        var enableModel = request.EnableModel && (routing?.EnableModelScoring ?? true);
        var scored = passing.Select(e => ApplyUtilityScoring(e, routing, enableModel)).ToList();

        // 排序键：IsMandatory 降序 → FinalScore 降序 → EffectiveTokens 降序 → CandidateId 升序
        // 注意：IsMandatory 不影响 safety gate 准入（已在 SafetyState 注释中说明），
        // 但在排序中强制 mandatory 候选优先于非 mandatory。
        // 使用 GetEffectiveTokens（TokenCost 优先）替代 EstimatedTokens（length/4 粗估）。
        var ordered = scored
            .OrderByDescending(e => e.Safety.IsMandatory || e.Safety.IsHardConstraint)
            .ThenByDescending(e => e.Utility.FinalScore)
            .ThenByDescending(e => DecisionOutcomeRecomputer.GetEffectiveTokens(e))
            .ThenBy(e => e.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 阶段 3：Budget Allocation — bundle.Budget 作为 request 字段为空时的兜底
        var selected = new List<ContextCandidateEnvelope>();
        var droppedByBudget = new List<ContextCandidateEnvelope>();
        var tokenBudget = request.TokenBudget > 0
            ? request.TokenBudget
            : (budget is { DefaultTokenBudget: > 0 } ? budget.DefaultTokenBudget : int.MaxValue);
        var usedTokens = 0;
        var topK = (request.TopK > 0 && request.TopK != int.MaxValue)
            ? request.TopK
            : (budget is { DefaultTopK: > 0 } ? budget.DefaultTopK : int.MaxValue);
        var takenCount = 0;

        foreach (var envelope in ordered)
        {
            // mandatory / hard constraint 候选永远选入（不受 budget 限制）
            var isMandatory = envelope.Safety.IsMandatory || envelope.Safety.IsHardConstraint;

            // TopK 检查（Retrieval 路径）
            if (!isMandatory && takenCount >= topK)
            {
                droppedByBudget.Add(envelope with
                {
                    Safety = envelope.Safety with
                    {
                        BlockReasonCode = CandidateDecisionReasonCode.SectionQuotaExceeded,
                        BlockReasonDetail = $"exceeded TopK={topK}"
                    }
                });
                continue;
            }

            // Token budget 检查（Retrieval 全局硬上限语义）
            // 使用 GetEffectiveTokens（TokenCost 优先）替代 EstimatedTokens（length/4 粗估）。
            var effectiveTokens = DecisionOutcomeRecomputer.GetEffectiveTokens(envelope);
            if (!isMandatory && usedTokens + effectiveTokens > tokenBudget)
            {
                droppedByBudget.Add(envelope with
                {
                    Safety = envelope.Safety with
                    {
                        BlockReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded,
                        BlockReasonDetail = $"exceeded token budget={tokenBudget}, used={usedTokens}"
                    }
                });
                continue;
            }

            selected.Add(envelope);
            usedTokens += effectiveTokens;
            takenCount++;
        }

        // 合并所有 dropped（safety blocked + budget exceeded）
        var allDropped = new List<ContextCandidateEnvelope>(blocked.Count + droppedByBudget.Count);
        allDropped.AddRange(blocked);
        allDropped.AddRange(droppedByBudget);

        // 输出摘要
        var outcomeTokenBudget = request.TokenBudget > 0
            ? request.TokenBudget
            : (budget?.DefaultTokenBudget ?? 0);
        var outcome = new ContextDecisionOutcomeSummary
        {
            SelectedCount = selected.Count,
            DroppedCount = allDropped.Count,
            EffectiveTokens = usedTokens,
            TokenBudget = outcomeTokenBudget,
            Sections = Array.Empty<string>(), // 不实现 section 分层
            SafetyGateBlockedCount = blocked.Count,
            BudgetExceededCount = droppedByBudget.Count
        };

        // 模型启用标志：enableModel && 至少一个 selected 候选仍保留 ModelScore
        // （ModelConfidence 低于阈值的候选已在 ApplyUtilityScoring 中回退为 null）
        var modelEnabled = enableModel && selected.Any(e => e.Utility.ModelScore.HasValue);
        var modelVersion = modelEnabled
            ? (routing?.ModelArtifactId
               ?? selected.FirstOrDefault(e => e.Utility.ModelArtifactRef != null)?.Utility.ModelArtifactRef)
            : null;

        var result = new ContextDecisionResult
        {
            RequestId = request.RequestId,
            DecisionSource = request.DecisionSource,
            SelectedEnvelopes = selected,
            DroppedEnvelopes = allDropped,
            Outcome = outcome,
            PolicyVersion = bundle?.Policies.DecisionSchemaVersion ?? ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            ModelVersion = modelVersion,
            ModelEnabled = modelEnabled
        };

        return result;
    }

    // -----------------------------------------------------------------------
    // V2 路径 — Engine 为唯一决策点，委托注入的抽象执行全部四阶段
    // -----------------------------------------------------------------------

    /// <summary>
    /// V2 决策路径。委托 ISafetyGate → ILifecycleGate → IUtilityScorer → IGlobalAllocator。
    /// Runtime 不再在 Engine 前执行 Safety/Lifecycle/Score（消除重复）。
    /// 注入 IPerformanceMonitor 时，V2 路径执行前后埋点；超过阈值时下次请求自动回退到 V2.0 Allocator。
    /// </summary>
    private async Task<ContextDecisionResult> ExecuteV2PathAsync(
        ContextDecisionRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = request.PolicySnapshot!;

        // 性能埋点 — 启动计时器，构造 scopeKey。
        var scopeKey = $"{request.WorkspaceId}/{request.CollectionId}";
        var monitor = _performanceMonitor;
        var perfEnabled = monitor is not null;
        var sw = perfEnabled ? Stopwatch.StartNew() : null;

        // 组件健康注册表（可选注入；null 时跳过组件级归因与回退）
        var componentRegistry = _componentHealthRegistry;

        // 查询是否应回退到 V2.0 Allocator（避免 V2.1 性能回退拖累主链）。
        // 回退条件：monitor.ShouldFallbackToV20(scopeKey) 返回 true（基于最近样本 P95 + 阈值）。
        // 触发回退时：跳过 V2.1 AllocateWithDiversity，直接走 V2.0 Allocate。
        var forceV20Fallback = perfEnabled && monitor!.ShouldFallbackToV20(scopeKey);
        if (forceV20Fallback)
        {
            monitor!.RecordFallback(scopeKey, "v21_p95_exceeded_threshold", lastDurationMs: 0);
        }

        // 组件级回退查询 — Allocation 组件回退（与 IPerformanceMonitor.ShouldFallbackToV20 互补）。
        // 当 IComponentHealthRegistry 注入且 Allocation 组件 P95 超阈值时，也强制跳过 V2.1 路径。
        // 保留 IPerformanceMonitor.ShouldFallbackToV20 作为兜底入口（向后兼容）。
        var allocationFallbackActive = componentRegistry is not null
            && componentRegistry.ShouldFallbackComponent(ComponentKind.Allocation, scopeKey);
        if (allocationFallbackActive)
        {
            forceV20Fallback = true;
            componentRegistry!.RecordComponentFallback(
                ComponentKind.Allocation, scopeKey, "p95_exceeded_threshold", cancellationToken);
        }

        // Inference 组件回退 — 当 Inference 组件 P95 超阈值时，禁用模型评分路径
        //（等效切换到 DeterministicBatchInferenceEngine：ModelActivationManager 已实现 fallback）。
        // 实现方式：将 snapshot.Routing.EnableModelScoring 置为 false，让 DefaultUtilityScorer 走 rule-only 路径。
        var inferenceFallbackActive = componentRegistry is not null
            && componentRegistry.ShouldFallbackComponent(ComponentKind.Inference, scopeKey);
        if (inferenceFallbackActive && snapshot.Routing.EnableModelScoring)
        {
            snapshot = snapshot with { Routing = snapshot.Routing with { EnableModelScoring = false } };
            componentRegistry!.RecordComponentFallback(
                ComponentKind.Inference, scopeKey, "p95_exceeded_threshold", cancellationToken);
        }

        // 阶段 1：SafetyGate — 委托 ISafetyGate
        var passing = new List<ContextCandidateEnvelope>(request.Candidates.Count);
        var safetyBlocked = new List<ContextCandidateEnvelope>(request.Candidates.Count);
        foreach (var envelope in request.Candidates)
        {
            var result = _safetyGate!.Evaluate(envelope, snapshot.Safety);
            if (result.Passes)
            {
                passing.Add(envelope);
            }
            else
            {
                safetyBlocked.Add(envelope with
                {
                    Safety = envelope.Safety with
                    {
                        PassesSafetyGate = false,
                        BlockReasonCode = result.ReasonCode,
                        BlockReasonDetail = result.Detail
                    }
                });
            }
        }

        // 阶段 2：LifecycleGate — 委托 ILifecycleGate（可选；null 时跳过）
        var lifecyclePassed = new List<ContextCandidateEnvelope>(passing.Count);
        var lifecycleBlocked = new List<ContextCandidateEnvelope>(passing.Count);
        if (_lifecycleGate is not null)
        {
            foreach (var envelope in passing)
            {
                var lcResult = _lifecycleGate.Evaluate(envelope);
                if (lcResult.Passes)
                {
                    lifecyclePassed.Add(envelope);
                }
                else
                {
                    lifecycleBlocked.Add(envelope with
                    {
                        Safety = envelope.Safety with
                        {
                            PassesSafetyGate = false,
                            BlockReasonCode = CandidateDecisionReasonCode.LifecycleBlocked,
                            BlockReasonDetail = $"{lcResult.ReasonCode}: {lcResult.Detail}"
                        }
                    });
                }
            }
        }
        else
        {
            lifecyclePassed = passing;
        }

        // 阶段 3：UtilityScorer — ScoreAsync 返回新列表（immutable record 友好）
        // 用 Stopwatch 拆分 scoring_ms，记录到 IComponentHealthRegistry。
        // 注：inference_ms 不再使用 scoring_ms 作为代理值——Inference 各阶段耗时由
        // OnnxInferenceEngine 通过 RecordInferencePhaseTime 直接上报（queue/copy/run/parse），
        // 避免用整体 Scoring 耗时代替 Inference 耗时导致归因失真。
        var scoringSw = componentRegistry is not null ? Stopwatch.StartNew() : null;
        bool scoringSucceeded = false;
        try
        {
            if (lifecyclePassed.Count > 0)
            {
                var scored = await _utilityScorer!.ScoreAsync(lifecyclePassed, snapshot, cancellationToken).ConfigureAwait(false);
                lifecyclePassed = scored is List<ContextCandidateEnvelope> scoredList
                    ? scoredList
                    : new List<ContextCandidateEnvelope>(scored);
            }
            scoringSucceeded = true;
        }
        finally
        {
            if (scoringSw is not null)
            {
                scoringSw.Stop();
                var scoringMs = scoringSw.Elapsed.TotalMilliseconds;
                componentRegistry!.RecordComponentTime(
                    ComponentKind.Scoring, scoringMs, scoringSucceeded, scopeKey, cancellationToken);
                // Inference 耗时不再用 scoringMs 代理：由 OnnxInferenceEngine 通过
                // RecordInferencePhaseTime(InferencePhaseKind.Run, ...) 直接上报真实推理耗时。
            }
        }

        // 阶段 3.5：两阶段排序 — 先按 FinalScore 建立第一阶段顺序（分配器的 TopK 截断
        // 与预算遍历都按最终顺序消费，因此排序必须发生在分配之前），
        // 启用两阶段重排时再对有限窗口做确定性重排；只改顺序不改 FinalScore，
        // 窗口外候选保持第一阶段顺序；未启用或未注入 reranker 时仅排序、不重排。
        if (lifecyclePassed.Count > 1)
        {
            lifecyclePassed.Sort(CompareStageOneOrder);
        }
        if (_reranker is not null && snapshot.Routing.EnableTwoStageRerank && lifecyclePassed.Count > 0)
        {
            var reranked = await _reranker.RerankAsync(lifecyclePassed, snapshot, cancellationToken).ConfigureAwait(false);
            lifecyclePassed = reranked is List<ContextCandidateEnvelope> rerankList
                ? rerankList
                : new List<ContextCandidateEnvelope>(reranked);
        }

        // 阶段 4：GlobalAllocator — 委托 IGlobalAllocator（唯一分配点）
        // 合并 request 级 budget override 到 snapshot（request budget 只解析一次）。
        // request.TokenBudget > 0 时覆盖 snapshot.Budget.DefaultTokenBudget；
        // request.TopK > 0 且非 int.MaxValue 时覆盖 snapshot.Budget.DefaultTopK。
        var effectiveTokenBudget = request.TokenBudget > 0
            ? request.TokenBudget
            : snapshot.Budget.DefaultTokenBudget;
        var effectiveTopK = request.TopK > 0 && request.TopK != int.MaxValue
            ? request.TopK
            : snapshot.Budget.DefaultTopK;
        var effectiveSnapshot = (effectiveTokenBudget != snapshot.Budget.DefaultTokenBudget
            || effectiveTopK != snapshot.Budget.DefaultTopK)
            ? snapshot with { Budget = snapshot.Budget with { DefaultTokenBudget = effectiveTokenBudget, DefaultTopK = effectiveTopK } }
            : snapshot;
        // 路径选择 — 当 request.DiversityOptions 非空 + IAllocatorV2_1 注入 +
        // AllocationContext 非空时，走 AllocateWithDiversity（section rollover + MMR）；
        // 否则回退 V2.0 Allocate（向后兼容 之前的行为）。
        // 需要 AllocationContext 携带 Purpose + MandatoryOverflowPolicy，保证安全边界不被绕过。
        // forceV20Fallback=true 时强制跳过 V2.1 路径，避免性能回退拖累主链。
        // forceV20Fallback 也由 Allocation 组件回退触发（见上方 allocationFallbackActive）。
        AllocationResult allocation;
        var usedV21Path = false;
        var allocationSw = componentRegistry is not null ? Stopwatch.StartNew() : null;
        bool allocationSucceeded = false;
        try
        {
            if (_allocatorV2_1 is not null
                && request.DiversityOptions is not null
                && request.AllocationContext is not null
                && !forceV20Fallback)
            {
                // 将 request 级 budget override 合并到 AllocationContext（与 effectiveSnapshot 对齐），
                // 保证 V2.1 Allocator 读到的 context.Budget 与 V2.0 路径的 effectiveSnapshot.Budget 一致。
                var effectiveContext = (effectiveTokenBudget != request.AllocationContext.Budget.DefaultTokenBudget
                    || effectiveTopK != request.AllocationContext.Budget.DefaultTopK)
                    ? request.AllocationContext with
                    {
                        Budget = request.AllocationContext.Budget with
                        {
                            DefaultTokenBudget = effectiveTokenBudget,
                            DefaultTopK = effectiveTopK
                        }
                    }
                    : request.AllocationContext;
                allocation = _allocatorV2_1.AllocateWithDiversity(
                    lifecyclePassed, effectiveContext, request.DiversityOptions);
                usedV21Path = true;
            }
            else if (request.AllocationContext is not null)
            {
                // 携带 Purpose + MandatoryOverflowPolicy 的 V2.0 重载
                allocation = _globalAllocator!.Allocate(lifecyclePassed, effectiveSnapshot, request.AllocationContext);
            }
            else
            {
                // Legacy 重载（向后兼容）
                allocation = _globalAllocator!.Allocate(lifecyclePassed, effectiveSnapshot);
            }
            allocationSucceeded = true;
        }
        finally
        {
            if (allocationSw is not null)
            {
                allocationSw.Stop();
                componentRegistry!.RecordComponentTime(
                    ComponentKind.Allocation, allocationSw.Elapsed.TotalMilliseconds,
                    allocationSucceeded, scopeKey, cancellationToken);
            }
        }

        // 记录本次 V2 路径执行耗时（仅在 monitor 注入时）。
        if (perfEnabled)
        {
            sw!.Stop();
            monitor!.RecordExecutionTime(scopeKey, sw.Elapsed.TotalMilliseconds, usedV21Path);
        }

        // 合并所有 dropped（safety + lifecycle + budget）
        // 分配裁掉的候选打上丢弃原因（BlockReasonCode + 详情），兑现
        // DroppedEnvelopes 契约「包含 BlockReasonCode」，让每个 dropped 可解释；
        // gate 拦截的候选已在上游带原因，此处只处理 allocation.Dropped。
        var decisionByKey = new Dictionary<CanonicalCandidateKey, CandidateAllocationDecision>(
            allocation.AllocationDecisions.Count);
        foreach (var decision in allocation.AllocationDecisions)
        {
            decisionByKey[decision.CandidateKey] = decision;
        }
        var allocationDropped = new List<ContextCandidateEnvelope>(allocation.Dropped.Count);
        foreach (var envelope in allocation.Dropped)
        {
            if (decisionByKey.TryGetValue(envelope.CanonicalKey, out var decision))
            {
                allocationDropped.Add(envelope with
                {
                    Safety = envelope.Safety with
                    {
                        BlockReasonCode = decision.ReasonCode,
                        BlockReasonDetail = DescribeAllocationDropReason(decision)
                    }
                });
            }
            else
            {
                allocationDropped.Add(envelope);
            }
        }

        var allDropped = new List<ContextCandidateEnvelope>(
            safetyBlocked.Count + lifecycleBlocked.Count + allocationDropped.Count);
        allDropped.AddRange(safetyBlocked);
        allDropped.AddRange(lifecycleBlocked);
        allDropped.AddRange(allocationDropped);

        // 模型启用标志
        var enableModel = request.EnableModel && snapshot.Routing.EnableModelScoring;
        var modelEnabled = enableModel && allocation.Selected.Any(e => e.Utility.ModelScore.HasValue);
        var modelVersion = modelEnabled
            ? (snapshot.Routing.ModelArtifactId
               ?? allocation.Selected.FirstOrDefault(e => e.Utility.ModelArtifactRef != null)?.Utility.ModelArtifactRef)
            : null;

        // 合并性能诊断到 Outcome.Diagnostics（保留 Allocator 原有诊断）。
        var outcome = perfEnabled
            ? MergePerformanceDiagnostics(allocation.Outcome, monitor!.GetDiagnostics(scopeKey), forceV20Fallback, usedV21Path)
            : allocation.Outcome;

        return new ContextDecisionResult
        {
            RequestId = request.RequestId,
            DecisionSource = request.DecisionSource,
            SelectedEnvelopes = allocation.Selected,
            DroppedEnvelopes = allDropped,
            Outcome = outcome,
            PolicyVersion = snapshot.Reference.BundleVersion,
            ModelVersion = modelVersion,
            ModelEnabled = modelEnabled,
            AllocationDecisions = allocation.AllocationDecisions,
            PolicyReference = snapshot.Reference
        };
    }

    /// <summary>
    /// 将性能诊断合并到 Outcome.Diagnostics（保留 Allocator 原有诊断 + 追加 perf.* 字段）。
    /// ContextDecisionOutcomeSummary 是 init-only class，构造新实例复制原字段并替换 Diagnostics。
    /// </summary>
    private static ContextDecisionOutcomeSummary MergePerformanceDiagnostics(
        ContextDecisionOutcomeSummary outcome,
        IReadOnlyDictionary<string, string> perfDiagnostics,
        bool fallbackTriggered,
        bool usedV21Path)
    {
        var merged = new Dictionary<string, string>(
            outcome.Diagnostics is { Count: > 0 } existing
                ? existing
                : new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var kv in perfDiagnostics)
        {
            merged[kv.Key] = kv.Value;
        }
        merged["performance.v21_path_used"] = usedV21Path.ToString().ToLowerInvariant();
        merged["performance.fallback_applied"] = fallbackTriggered.ToString().ToLowerInvariant();
        return new ContextDecisionOutcomeSummary
        {
            SelectedCount = outcome.SelectedCount,
            DroppedCount = outcome.DroppedCount,
            EffectiveTokens = outcome.EffectiveTokens,
            TokenBudget = outcome.TokenBudget,
            Sections = outcome.Sections,
            SafetyGateBlockedCount = outcome.SafetyGateBlockedCount,
            BudgetExceededCount = outcome.BudgetExceededCount,
            Diagnostics = merged
        };
    }

    // -----------------------------------------------------------------------
    // Legacy SafetyGate 评估（向后兼容 测试）
    // -----------------------------------------------------------------------

    /// <summary>
    /// Legacy 路径的 SafetyGate 评估：候选自带 PassesSafetyGate=false（adapter 已预先标记）
    /// 直接信任；无 bundle 时其余全放行（向后兼容 行为）；有 bundle 时委托
    /// <see cref="DefaultSafetyGate"/>（同一语义，消除重复实现）。
    /// </summary>
    private static (bool Passes, CandidateDecisionReasonCode Reason, string Detail) EvaluateSafetyGate(
        ContextCandidateEnvelope envelope, SafetyProfile? safety)
    {
        // 候选自带 PassesSafetyGate=false（adapter 已预先标记）→ 信任之
        if (!envelope.Safety.PassesSafetyGate)
        {
            return (false, envelope.Safety.BlockReasonCode, envelope.Safety.BlockReasonDetail);
        }

        // 无 bundle → 不应用额外 safety 检查（向后兼容 行为）
        if (safety is null)
        {
            return (true, CandidateDecisionReasonCode.Unknown, string.Empty);
        }

        var result = SafetyGateEvaluator.Evaluate(envelope, safety);
        return (result.Passes, result.ReasonCode, result.Detail);
    }

    // -----------------------------------------------------------------------
    // Utility Scoring 评估（含 Model failure 精确回退）
    // -----------------------------------------------------------------------

    private static ContextCandidateEnvelope ApplyUtilityScoring(
        ContextCandidateEnvelope envelope,
        RoutingProfile? routing,
        bool enableModel)
    {
        var utility = envelope.Utility;

        // 未启用模型 且 候选有 ModelScore → 精确回退到 deterministic
        // （验收标准 ：Model failure 时 ModelConfidence=0 + ModelScore=null + ReasonCode="fallback-to-deterministic"）
        if (!enableModel && utility.ModelScore is not null)
        {
            return envelope with
            {
                Utility = utility with
                {
                    FinalScore = utility.DeterministicScore,
                    ModelScore = null,
                    ModelConfidence = 0,
                    ReasonCode = "fallback-to-deterministic"
                }
            };
        }

        // 模型启用但候选无 ModelScore → 保持原样
        if (!enableModel || utility.ModelScore is null)
        {
            return envelope;
        }

        // 应用 ModelConfidenceThreshold（仅当 routing 显式提供时）
        // ModelConfidence < threshold → 回退到 DeterministicScore
        if (routing is not null && utility.ModelConfidence < routing.ModelConfidenceThreshold)
        {
            return envelope with
            {
                Utility = utility with
                {
                    FinalScore = utility.DeterministicScore,
                    ModelScore = null,
                    ModelConfidence = 0,
                    ReasonCode = "fallback-to-deterministic"
                }
            };
        }

        // 不重新计算 FinalScore；保留 envelope 预设值
        // （Engine 信任调用方/adapter 已正确加权； Router 才会真正注入模型权重）
        return envelope;
    }

    // -----------------------------------------------------------------------
    // 受限 override 合并辅助方法
    // -----------------------------------------------------------------------

    /// <summary>
    /// 将 RequestBudgetOverride 的字段合并到 bundle 的 BudgetProfile（委托权威实现）。
    /// </summary>
    private static BudgetProfile? ApplyBudgetOverride(
        BudgetProfile? baseProfile,
        RequestBudgetOverride? budgetOverride)
        => DecisionOutcomeRecomputer.ApplyBudgetOverride(baseProfile, budgetOverride);

    /// <summary>
    /// 将 RequestRoutingOverride 的字段合并到 bundle 的 RoutingProfile（委托权威实现）。
    /// </summary>
    private static RoutingProfile? ApplyRoutingOverride(
        RoutingProfile? baseProfile,
        RequestRoutingOverride? routingOverride)
        => DecisionOutcomeRecomputer.ApplyRoutingOverride(baseProfile, routingOverride);

    /// <summary>
    /// 第一阶段排序比较器：FinalScore 降序 → EffectiveTokens 降序 → CandidateId 升序。
    /// 与分配器的 mandatory 排序共用同一权威实现，保证进入分配器时输入即为评分降序；
    /// 两阶段重排在此顺序之上对有限窗口做最终排序。
    /// </summary>
    private static int CompareStageOneOrder(ContextCandidateEnvelope a, ContextCandidateEnvelope b)
        => DecisionOutcomeRecomputer.CompareByScoreDesc(a, b);

    /// <summary>
    /// 生成分配丢弃原因的可读详情（中文，供审计与投影展示）。
    /// </summary>
    private static string DescribeAllocationDropReason(CandidateAllocationDecision decision)
        => decision.ReasonCode switch
        {
            CandidateDecisionReasonCode.SectionQuotaExceeded => "TopK 截断：候选数超过 TopK 上限",
            CandidateDecisionReasonCode.TokenBudgetExceeded => "Token 预算超限：有效 token 超出剩余预算",
            _ => decision.ReasonCode.ToString()
        };
}
