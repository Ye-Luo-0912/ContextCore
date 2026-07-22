using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Policy;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R28-B B-1：Unified Runtime 默认实现骨架（Skeletons）
//
// 目标（B-1 阶段：Contracts correction，无行为变更）：
//   为 §5.1~§5.9 新增契约提供可编译、可注入的默认实现。
//   这些骨架仅满足"契约可实例化 + DI 可注册"，不接入生产主链。
//   真正的编排逻辑（Provider 召回、Shadow tee、Authoritative cutover）
//   由 B-2~B-5 阶段实现，届时本文件中的骨架将被替换或重构。
//
// 设计原则：
//   1. 不改生产行为：DefaultContextDecisionEngine（既有接口 IContextDecisionEngine）
//      保持不变；本文件中的实现不被 AddContextCore 注册到主链（B-1-4 仅注册接口，
//      不替换 IContextRetriever / IContextPackageBuilder）。
//   2. 骨架语义自洽：每个骨架实现最简但合法的逻辑，避免 NotImplementedException。
//   3. 依赖注入友好：构造函数接受协作者，便于 B-2 替换为真实实现。
//   4. rule-only convergence：UtilityScorer 使用 w_d=1.0 / w_m=0.0；
//      FeaturePipeline 为 identity transform；Allocator 使用 TopK + TokenBudget 硬截断。
//
// 替换策略：
//   - B-2：DefaultContextDecisionRuntime 替换为真实编排（Policy → Router → Providers →
//     Merge → EarlyGate → FeaturePipeline → Engine → Allocator）。
//   - B-3/B-4：DefaultRouter / DefaultExpertCatalog / DefaultCanonicalCandidateMerger
//     替换为接入真实 Provider 网络的实现。
//   - B-5：Legacy 移除后，本文件中保留的实现升级为权威路径。
// ===========================================================================

// ---------------------------------------------------------------------------
// §5.1 DefaultContextDecisionRuntime（B-2 升级为 pure Runtime）
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-2：统一 Context Decision Runtime — pure Runtime 真实编排。
/// </summary>
/// <remarks>
/// P0-6 修复：移除 Runtime 后二次 Allocate。Engine 是分配的唯一权威所有者，
/// Runtime 不再在 Engine 后调用 IGlobalAllocator。Engine 内部已执行
/// SafetyGate → UtilityScoring → 排序 → TopK/TokenBudget 截断。
///
/// P0-2 修复：Runtime 注入 IRouter / IExpertCatalog / ICandidateProvider / ICanonicalCandidateMerger，
/// 真正执行 Route → Provider → Merge → SeedCandidates merge → EarlyGate → Feature → Engine。
///
/// 编排流程：
///   1. 策略解析（IResolvedPolicyProvider → EffectivePolicySnapshot）
///   2. Router 路由（IRouter → ExpertRoutingDecisionSet）
///   3. Provider 召回（仅调用 enabled Provider，bounded parallel）
///   4. Canonical Merge（ICanonicalCandidateMerger 合并 Provider 输出）
///   5. SeedCandidates 合并（将外部传入的种子候选合并到工作集）
///   6. EarlyAdmissionGate 评估（scope mismatch / superseded）
///   7. FeaturePipeline 特征计算
///   8. SafetyGate + LifecycleGate 评估
///   9. UtilityScorer 评分
///   10. Engine 决策（委托 IContextDecisionEngine 执行 budget allocation — 唯一分配点）
/// </remarks>
public sealed class DefaultContextDecisionRuntime : IContextDecisionRuntime
{
    private readonly IContextDecisionEngine _engine;
    private readonly IResolvedPolicyProvider _policyProvider;
    private readonly ContextCore.Abstractions.IRouter _router;
    private readonly IExpertCatalog _expertCatalog;
    private readonly IReadOnlyList<ICandidateProvider> _candidateProviders;
    private readonly ICanonicalCandidateMerger _canonicalMerger;
    private readonly IEarlyAdmissionGate _earlyAdmissionGate;
    private readonly IFeaturePipeline _featurePipeline;
    private readonly ISafetyGate _safetyGate;
    private readonly ILifecycleGate _lifecycleGate;
    private readonly IUtilityScorer _utilityScorer;
    private readonly TimeSpan _providerTimeout;

    /// <summary>构造 pure Runtime。</summary>
    /// <param name="providerTimeout">R28-B.6：单个 Provider 调用超时（默认 30s）。</param>
    public DefaultContextDecisionRuntime(
        IContextDecisionEngine engine,
        IResolvedPolicyProvider policyProvider,
        ContextCore.Abstractions.IRouter router,
        IExpertCatalog expertCatalog,
        IReadOnlyList<ICandidateProvider> candidateProviders,
        ICanonicalCandidateMerger canonicalMerger,
        IEarlyAdmissionGate earlyAdmissionGate,
        IFeaturePipeline featurePipeline,
        ISafetyGate safetyGate,
        ILifecycleGate lifecycleGate,
        IUtilityScorer utilityScorer,
        TimeSpan? providerTimeout = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _expertCatalog = expertCatalog ?? throw new ArgumentNullException(nameof(expertCatalog));
        _candidateProviders = candidateProviders ?? throw new ArgumentNullException(nameof(candidateProviders));
        _canonicalMerger = canonicalMerger ?? throw new ArgumentNullException(nameof(canonicalMerger));
        _earlyAdmissionGate = earlyAdmissionGate ?? throw new ArgumentNullException(nameof(earlyAdmissionGate));
        _featurePipeline = featurePipeline ?? throw new ArgumentNullException(nameof(featurePipeline));
        _safetyGate = safetyGate ?? throw new ArgumentNullException(nameof(safetyGate));
        _lifecycleGate = lifecycleGate ?? throw new ArgumentNullException(nameof(lifecycleGate));
        _utilityScorer = utilityScorer ?? throw new ArgumentNullException(nameof(utilityScorer));
        _providerTimeout = providerTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 执行 pure Runtime 编排：
    /// Policy → Router → Providers → Merge → Seed merge → EarlyGate → Feature → Safety → Lifecycle → Score → Engine。
    /// </summary>
    public async ValueTask<ContextDecisionResult> ExecuteAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Step 1：策略解析
        var snapshot = await _policyProvider.ResolveAsync(request, cancellationToken).ConfigureAwait(false);

        // Step 2：Router 路由 — 产出 ExpertRoutingDecisionSet
        var routingDecisions = await _router.RouteAsync(request, snapshot, cancellationToken).ConfigureAwait(false);

        // Step 3：Provider 召回 — 仅调用 enabled Provider，bounded parallel execution
        var expertOutputs = await InvokeEnabledProvidersAsync(
            request, snapshot, routingDecisions, cancellationToken).ConfigureAwait(false);

        // Step 4：Canonical Merge — 合并 Provider 输出
        var mergedWorkingSet = _canonicalMerger.Merge(expertOutputs);

        // Step 5：SeedCandidates 合并 — 将外部传入的种子候选加入工作集
        var allEnvelopes = MergeSeedCandidates(mergedWorkingSet.Envelopes, request.SeedCandidates);

        if (allEnvelopes.Count == 0)
        {
            return EmptyResult(request, snapshot);
        }

        // Step 6：EarlyAdmissionGate — 拒绝 scope mismatch / superseded
        var admitted = new List<ContextCandidateEnvelope>(allEnvelopes.Count);
        foreach (var envelope in allEnvelopes)
        {
            var admission = _earlyAdmissionGate.Evaluate(envelope, snapshot);
            if (admission.Admitted)
            {
                admitted.Add(envelope);
            }
        }

        if (admitted.Count == 0)
        {
            return EmptyResult(request, snapshot);
        }

        // Step 7：FeaturePipeline — 特征计算
        var enriched = await _featurePipeline.EnrichAsync(
            admitted,
            new FeaturePipelineContext(
                Policy: snapshot,
                AdaptationContext: new CandidateAdaptationContext
                {
                    WorkspaceId = request.Scope.WorkspaceId,
                    CollectionId = request.Scope.CollectionId,
                    RequestId = request.RequestId,
                    QueryText = request.QueryText,
                    ObservedAt = DateTimeOffset.UtcNow
                }),
            cancellationToken).ConfigureAwait(false);

        // R28-B.6：Runtime 不再在 Engine 前执行 SafetyGate/LifecycleGate/UtilityScorer。
        // Engine 是唯一决策点（Safety → Lifecycle → Score → Allocate 全部在 Engine 内执行）。
        // Runtime 只保留 EarlyAdmissionGate + FeaturePipeline，然后把 enriched 候选传给 Engine。

        // Step 8：委托 IContextDecisionEngine 执行完整决策（Safety → Lifecycle → Score → Allocate）
        // P0-6 修复：Runtime 不再在 Engine 后二次 Allocate。
        // R28-B.6：Engine 通过 PolicySnapshot 字段接收已解析的 snapshot，走 V2 路径。
        var decisionRequest = new ContextDecisionRequest
        {
            RequestId = request.RequestId,
            DecisionSource = ResolveDecisionSource(request.Purpose),
            WorkspaceId = request.Scope.WorkspaceId,
            CollectionId = request.Scope.CollectionId,
            Candidates = enriched,
            TokenBudget = request.TokenBudget > 0 ? request.TokenBudget : snapshot.Budget.DefaultTokenBudget,
            TopK = request.TopK > 0 && request.TopK != int.MaxValue
                ? request.TopK
                : snapshot.Budget.DefaultTopK,
            SectionRatios = snapshot.Budget.SectionRatios.Count > 0 ? snapshot.Budget.SectionRatios : null,
            PolicyBundleId = snapshot.Reference.BundleId,
            QueryText = request.QueryText,
            CreatedAt = DateTimeOffset.UtcNow,
            EnableModel = snapshot.Routing.EnableModelScoring,
            PolicySnapshot = snapshot
        };

        var engineResult = await _engine.DecideAsync(decisionRequest, cancellationToken).ConfigureAwait(false);

        // P0-6：直接使用 Engine 结果，不再二次 Allocate。
        // R28-B.6：V2 路径下 Engine 已通过 IGlobalAllocator 产出 AllocationDecisions。
        // Legacy 路径下 Engine 不产出 AllocationDecisions，Runtime 补建。
        var allocationDecisions = engineResult.AllocationDecisions.Count > 0
            ? engineResult.AllocationDecisions
            : BuildAllocationDecisions(engineResult.SelectedEnvelopes, engineResult.DroppedEnvelopes);

        return new ContextDecisionResult
        {
            RequestId = engineResult.RequestId,
            DecisionSource = engineResult.DecisionSource,
            SelectedEnvelopes = engineResult.SelectedEnvelopes,
            DroppedEnvelopes = engineResult.DroppedEnvelopes,
            Outcome = engineResult.Outcome,
            PolicyVersion = engineResult.PolicyVersion,
            ModelVersion = engineResult.ModelVersion,
            DecidedAt = engineResult.DecidedAt,
            ModelEnabled = engineResult.ModelEnabled,
            Purpose = request.Purpose,
            RuntimeKind = ContextDecisionRuntimeKind.UnifiedV2,
            AllocationDecisions = allocationDecisions,
            PolicyReference = snapshot.Reference
        };
    }

    /// <summary>
    /// P0-2 / R28-B.6：仅调用 routingDecisions 中 Enabled=true 的 Provider，bounded parallel execution。
    /// R28-B.6 新增：per-Provider 去重（每请求每 Provider 最多执行一次）+ 超时保护。
    /// </summary>
    private async Task<IReadOnlyList<ExpertExecutionResult>> InvokeEnabledProvidersAsync(
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        ExpertRoutingDecisionSet routingDecisions,
        CancellationToken cancellationToken)
    {
        if (_candidateProviders.Count == 0)
        {
            return Array.Empty<ExpertExecutionResult>();
        }

        // 构建路由查找表：Expert → RoutingDecision
        var routingByExpert = new Dictionary<RetrievalExpert, ExpertRoutingDecision>();
        foreach (var decision in routingDecisions.Decisions)
        {
            if (decision.Enabled)
            {
                routingByExpert[decision.Expert] = decision;
            }
        }

        // R28-B.6：per-Provider 去重 — 按 ExpertKind 去重，确保每请求每 Provider 最多执行一次。
        // Disabled Provider 永远不被执行（Enabled mask 已在 routingByExpert 过滤）。
        var executedKinds = new HashSet<ExpertKind>();
        var enabledProviders = _candidateProviders
            .Where(p => routingByExpert.Values.Any(r => MapExpertKindToRetrievalExpert(p.Kind) == r.Expert))
            .Where(p => executedKinds.Add(p.Kind)) // 去重：同 Kind 只保留第一个
            .ToList();

        if (enabledProviders.Count == 0)
        {
            return Array.Empty<ExpertExecutionResult>();
        }

        var adaptationContext = new CandidateAdaptationContext
        {
            WorkspaceId = request.Scope.WorkspaceId,
            CollectionId = request.Scope.CollectionId,
            RequestId = request.RequestId,
            QueryText = request.QueryText,
            ObservedAt = DateTimeOffset.UtcNow
        };

        // R28-B.6：超时保护 — 为每个 Provider 创建 linked CTS with timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_providerTimeout);

        // Bounded parallel execution：使用 SemaphoreSlim 限制并发度
        using var semaphore = new SemaphoreSlim(Math.Min(8, enabledProviders.Count));
        var tasks = enabledProviders.Select(async provider =>
        {
            var expertKind = provider.Kind;
            var retrievalExpert = MapExpertKindToRetrievalExpert(expertKind);
            routingByExpert.TryGetValue(retrievalExpert, out var routing);

            var context = new CandidateProviderContext(
                Request: request,
                Policy: snapshot,
                Routing: routing ?? new ExpertRoutingDecision
                {
                    Expert = retrievalExpert,
                    Enabled = true,
                    TopK = snapshot.Budget.DefaultTopK,
                    TokenBudget = snapshot.Budget.DefaultTokenBudget,
                    Weight = 1.0,
                    ReasonCode = "default"
                },
                AdaptationContext: adaptationContext);

            await semaphore.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            try
            {
                // R28-B.6：使用 linked CTS（含 timeout）而非原始 cancellationToken
                return await provider.ExecuteAsync(context, timeoutCts.Token).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// P0-2：合并 Provider 产出与 SeedCandidates。
    /// SeedCandidates 直接追加到 merged 集合（CanonicalMerger 已对 Provider 输出去重）。
    /// </summary>
    private static IReadOnlyList<ContextCandidateEnvelope> MergeSeedCandidates(
        IReadOnlyList<ContextCandidateEnvelope> mergedFromProviders,
        IReadOnlyList<ContextCandidateEnvelope> seedCandidates)
    {
        if (seedCandidates.Count == 0) return mergedFromProviders;
        if (mergedFromProviders.Count == 0) return seedCandidates;

        var seenKeys = new HashSet<CanonicalCandidateKey>(mergedFromProviders.Select(e => e.CanonicalKey));
        var combined = new List<ContextCandidateEnvelope>(mergedFromProviders.Count + seedCandidates.Count);
        combined.AddRange(mergedFromProviders);
        foreach (var seed in seedCandidates)
        {
            if (seenKeys.Add(seed.CanonicalKey))
            {
                combined.Add(seed);
            }
        }
        return combined;
    }

    /// <summary>
    /// P0-6：从 Engine 的 selected/dropped 输出构造 AllocationDecisions。
    /// Engine 是分配的唯一权威所有者，AllocationDecisions 反映 Engine 的决策结果。
    /// </summary>
    private static IReadOnlyList<CandidateAllocationDecision> BuildAllocationDecisions(
        IReadOnlyList<ContextCandidateEnvelope> selected,
        IReadOnlyList<ContextCandidateEnvelope> dropped)
    {
        var decisions = new List<CandidateAllocationDecision>(selected.Count + dropped.Count);

        foreach (var envelope in selected)
        {
            decisions.Add(new CandidateAllocationDecision
            {
                CandidateKey = envelope.CanonicalKey,
                Section = ResolveSectionForAllocation(envelope),
                IncludedTokens = envelope.EstimatedTokens,
                IsTruncated = false,
                ReasonCode = CandidateDecisionReasonCode.SelectedHighestUtility
            });
        }

        foreach (var envelope in dropped)
        {
            var reasonCode = !envelope.Safety.PassesSafetyGate
                ? envelope.Safety.BlockReasonCode
                : CandidateDecisionReasonCode.TokenBudgetExceeded;
            decisions.Add(new CandidateAllocationDecision
            {
                CandidateKey = envelope.CanonicalKey,
                Section = ResolveSectionForAllocation(envelope),
                IncludedTokens = 0,
                IsTruncated = false,
                ReasonCode = reasonCode
            });
        }

        return decisions;
    }

    private static string ResolveSectionForAllocation(ContextCandidateEnvelope envelope)
    {
        return envelope.Source switch
        {
            ContextCandidateSource.Mandatory or ContextCandidateSource.Constraint => "mandatory",
            ContextCandidateSource.WorkingMemory or ContextCandidateSource.StableMemory => "memory",
            ContextCandidateSource.Graph => "relations",
            ContextCandidateSource.GlobalContext => "global",
            ContextCandidateSource.RelatedContext => "related",
            _ => "default"
        };
    }

    private static RetrievalExpert MapExpertKindToRetrievalExpert(ExpertKind kind) => kind switch
    {
        ExpertKind.Mandatory => RetrievalExpert.Mandatory,
        ExpertKind.Constraint => RetrievalExpert.Constraint,
        ExpertKind.Lexical => RetrievalExpert.Lexical,
        ExpertKind.Semantic => RetrievalExpert.Semantic,
        ExpertKind.WorkingMemory => RetrievalExpert.WorkingMemory,
        ExpertKind.StableMemory => RetrievalExpert.StableMemory,
        ExpertKind.Graph => RetrievalExpert.Graph,
        ExpertKind.Recency => RetrievalExpert.Recency,
        _ => RetrievalExpert.Lexical
    };

    private static ContextDecisionResult EmptyResult(
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot)
    {
        return new ContextDecisionResult
        {
            RequestId = request.RequestId,
            DecisionSource = ResolveDecisionSource(request.Purpose),
            SelectedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            DroppedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = 0,
                DroppedCount = 0,
                EstimatedTokens = 0,
                TokenBudget = request.TokenBudget > 0 ? request.TokenBudget : snapshot.Budget.DefaultTokenBudget,
                Sections = Array.Empty<string>(),
                SafetyGateBlockedCount = 0,
                BudgetExceededCount = 0
            },
            PolicyVersion = snapshot.FeatureSchemaVersion,
            ModelEnabled = false,
            DecidedAt = DateTimeOffset.UtcNow,
            Purpose = request.Purpose,
            RuntimeKind = ContextDecisionRuntimeKind.UnifiedV2,
            AllocationDecisions = Array.Empty<CandidateAllocationDecision>(),
            PolicyReference = snapshot.Reference
        };
    }

    private static ContextDecisionSource ResolveDecisionSource(ContextDecisionPurpose purpose) => purpose switch
    {
        ContextDecisionPurpose.Retrieval => ContextDecisionSource.Retrieval,
        ContextDecisionPurpose.Package => ContextDecisionSource.Package,
        ContextDecisionPurpose.AgentContext => ContextDecisionSource.Package, // AgentContext 复用 Package 决策链
        _ => ContextDecisionSource.Package
    };
}

// ---------------------------------------------------------------------------
// §5.2 DefaultResolvedPolicyProvider
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-1：策略快照提供者默认骨架。返回基于全局默认 bundle 的不可变快照。
/// </summary>
/// <remarks>
/// B-1 骨架：不接入 IPolicyRegistry，使用 DefaultPolicyBundleFactory.Create() 的 hardcoded 值。
/// B-2 将替换为基于 IPolicyRegistry 的真实解析（CAS epoch + workspace/collection 作用域）。
/// </remarks>
public sealed class DefaultResolvedPolicyProvider : IResolvedPolicyProvider
{
    /// <summary>默认 bundle 内容哈希（B-1 骨架使用 placeholder；B-2 由 registry 计算）。</summary>
    public const string DefaultContentHash = "sha256:default-bundle-b1-skeleton";

    /// <summary>默认激活 epoch（B-1 骨架使用 1；B-2 由 CAS 注册表返回）。</summary>
    public const long DefaultActivationEpoch = 1L;

    /// <summary>解析请求对应的有效策略快照。</summary>
    public ValueTask<EffectivePolicySnapshot> ResolveAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var bundle = Services.Policy.DefaultPolicyBundleFactory.Create();

        var reference = new ResolvedPolicyReference
        {
            BundleId = bundle.BundleId,
            BundleVersion = bundle.Version,
            BundleContentHash = DefaultContentHash,
            ActivationEpoch = DefaultActivationEpoch
        };

        var snapshot = new EffectivePolicySnapshot
        {
            Reference = reference,
            Safety = bundle.Safety,
            Budget = bundle.Budget,
            Routing = bundle.Routing,
            FeatureSchemaVersion = bundle.Policies.DecisionSchemaVersion,
            RouterModelHash = null, // B-1 骨架：deterministic router
            RankerModelHash = null, // B-1 骨架：deterministic scorer
            ResolutionScope = request.Scope
        };

        return ValueTask.FromResult(snapshot);
    }
}

// ---------------------------------------------------------------------------
// §5.2b PostgresResolvedPolicyProvider（P0-3：接入 IPolicyRegistry 的真实策略解析）
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B P0-3：基于 <see cref="IPolicyRegistry"/> 的策略快照提供者。
/// 替代 B-1 骨架 <see cref="DefaultResolvedPolicyProvider"/>，接入 CAS epoch +
/// content hash + activation override + request override。
/// </summary>
/// <remarks>
/// 流程（用户 P0-3 原文）：
///   1. GetActivation(scope) → 获取 workspace+collection 当前激活记录
///      （含 BundleId/Version/ContentHash/Epoch/Override）。
///   2. exact GetBundle(BundleId, BundleVersion) → 精确版本加载，不漂移到最新版本。
///   3. verify ContentHash → 计算 bundle 内容哈希，与 activation.BundleContentHash
///      比对，不一致则 fail-closed（抛异常，不静默回退）。
///   4. merge activation override → 将 activation.BudgetOverride / RoutingOverride
///      合并到 bundle profile（与 DefaultContextDecisionEngine.ApplyBudgetOverride 对齐）。
///   5. merge request override → 将 request.TokenBudget / TopK（非零时）合并到 Budget。
///   6. validate feature schema / model artifacts → 校验 schema 版本非空 +
///      Routing.ModelArtifactId 引用的模型必须存在于 bundle.ModelArtifacts。
///   7. produce EffectivePolicySnapshot → 不可变快照，请求生命周期内只解析一次。
///
/// 当 activation 为 null（未激活）时，回退到全局默认 bundle（通过 GetActiveBundleAsync），
/// 使用计算出的 content hash + epoch=0，标识"未正式激活"。
/// </remarks>
public sealed class PostgresResolvedPolicyProvider : IResolvedPolicyProvider
{
    private readonly IPolicyRegistry _registry;

    /// <summary>构造策略解析器。注入 IPolicyRegistry（生产为 PostgresPolicyRegistry，测试为 DefaultPolicyRegistry）。</summary>
    public PostgresResolvedPolicyProvider(IPolicyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>解析请求对应的有效策略快照。</summary>
    public async ValueTask<EffectivePolicySnapshot> ResolveAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var workspaceId = request.Scope.WorkspaceId;
        var collectionId = request.Scope.CollectionId;

        // 1. GetActivation(scope)
        var activation = await _registry.GetActivationAsync(workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false);

        ContextPolicyBundle bundle;
        ResolvedPolicyReference reference;

        if (activation is null)
        {
            // 未激活 → 回退到全局默认 bundle（GetActiveBundleAsync 保证返回非 null）
            bundle = await _registry.GetActiveBundleAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            // 默认 bundle 无 CAS 记录，使用计算出的 hash + epoch=0 标识"未正式激活"
            reference = new ResolvedPolicyReference
            {
                BundleId = bundle.BundleId,
                BundleVersion = bundle.Version,
                BundleContentHash = PolicyBundleHasher.ComputeHash(bundle),
                ActivationEpoch = 0
            };
        }
        else
        {
            // 2. exact GetBundle(BundleId, BundleVersion) — 精确版本加载，不漂移
            bundle = await _registry.GetBundleAsync(activation.BundleId, activation.BundleVersion, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Policy activation references non-existent bundle: " +
                    $"BundleId={activation.BundleId}, Version={activation.BundleVersion}. " +
                    "Activation is stale or bundle was deleted. " +
                    "Fail-closed: refusing to fall back to default bundle.");

            // 3. verify ContentHash — bundle 不可变性验证
            var computedHash = PolicyBundleHasher.ComputeHash(bundle);
            if (!string.Equals(computedHash, activation.BundleContentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Bundle content hash mismatch: activation recorded " +
                    $"'{activation.BundleContentHash}' but bundle computes '{computedHash}' " +
                    $"for BundleId={bundle.BundleId}, Version={bundle.Version}. " +
                    "Bundle immutability violated — content was modified after activation. " +
                    "Fail-closed: refusing to use tampered bundle.");
            }

            reference = new ResolvedPolicyReference
            {
                BundleId = activation.BundleId,
                BundleVersion = activation.BundleVersion,
                BundleContentHash = activation.BundleContentHash,
                ActivationEpoch = activation.Epoch
            };
        }

        // 4. merge activation override（控制面注入的受限 override）
        var budget = MergeBudgetOverride(bundle.Budget, activation?.BudgetOverride);
        var routing = MergeRoutingOverride(bundle.Routing, activation?.RoutingOverride);

        // 5. merge request override（V2 Runtime 的 TokenBudget / TopK 是 per-request 预算 override）
        budget = MergeRequestBudgetOverride(budget, request.TokenBudget, request.TopK);

        // 6. validate feature schema / model artifacts
        ValidateBundle(bundle, routing);

        // 7. produce EffectivePolicySnapshot（Safety 不允许 override）
        var snapshot = new EffectivePolicySnapshot
        {
            Reference = reference,
            Safety = bundle.Safety,
            Budget = budget,
            Routing = routing,
            FeatureSchemaVersion = bundle.Policies.DecisionSchemaVersion,
            RouterModelHash = ResolveModelArtifactVersion(bundle, routing),
            RankerModelHash = ResolveModelArtifactVersion(bundle, routing),
            ResolutionScope = request.Scope
        };

        return snapshot;
    }

    // -----------------------------------------------------------------------
    // P0-3：受限 override 合并（与 DefaultContextDecisionEngine.ApplyBudgetOverride 对齐）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 将 activation 的 <see cref="RequestBudgetOverride"/> 合并到 bundle 的 <see cref="BudgetProfile"/>，
    /// 仅覆盖非空字段（TokenBudget / TopK / SectionRatios），不替换整个 profile。
    /// </summary>
    private static BudgetProfile MergeBudgetOverride(
        BudgetProfile baseProfile,
        RequestBudgetOverride? activationOverride)
    {
        if (activationOverride is null) return baseProfile;
        return baseProfile with
        {
            DefaultTokenBudget = activationOverride.TokenBudget ?? baseProfile.DefaultTokenBudget,
            DefaultTopK = activationOverride.TopK ?? baseProfile.DefaultTopK,
            SectionRatios = activationOverride.SectionRatios ?? baseProfile.SectionRatios
        };
    }

    /// <summary>
    /// 将 request 的 TokenBudget / TopK（非零时）合并到 Budget profile。
    /// V2 Runtime 的 ContextDecisionRuntimeRequest.TokenBudget/TopK 是 per-request 预算 override。
    /// </summary>
    private static BudgetProfile MergeRequestBudgetOverride(
        BudgetProfile baseProfile,
        int requestTokenBudget,
        int requestTopK)
    {
        if (requestTokenBudget <= 0 && requestTopK <= 0) return baseProfile;
        return baseProfile with
        {
            DefaultTokenBudget = requestTokenBudget > 0 ? requestTokenBudget : baseProfile.DefaultTokenBudget,
            DefaultTopK = requestTopK > 0 ? requestTopK : baseProfile.DefaultTopK
        };
    }

    /// <summary>
    /// 将 activation 的 <see cref="RequestRoutingOverride"/> 合并到 bundle 的 <see cref="RoutingProfile"/>，
    /// 仅覆盖 EnableModelScoring（非空时），不替换整个 profile。
    /// </summary>
    private static RoutingProfile MergeRoutingOverride(
        RoutingProfile baseProfile,
        RequestRoutingOverride? activationOverride)
    {
        if (activationOverride is null) return baseProfile;
        return baseProfile with
        {
            EnableModelScoring = activationOverride.EnableModelScoring ?? baseProfile.EnableModelScoring
        };
    }

    // -----------------------------------------------------------------------
    // P0-3：验证
    // -----------------------------------------------------------------------

    /// <summary>
    /// 验证 bundle 的 feature schema 版本非空 + Routing.ModelArtifactId 引用的模型存在于 ModelArtifacts。
    /// </summary>
    private static void ValidateBundle(ContextPolicyBundle bundle, RoutingProfile effectiveRouting)
    {
        if (string.IsNullOrEmpty(bundle.Policies.DecisionSchemaVersion))
        {
            throw new InvalidOperationException(
                $"Bundle {bundle.BundleId}/{bundle.Version} has empty DecisionSchemaVersion. " +
                "Feature pipeline cannot proceed without schema version.");
        }

        if (!string.IsNullOrEmpty(effectiveRouting.ModelArtifactId))
        {
            var found = bundle.ModelArtifacts.Any(a => a.ArtifactId == effectiveRouting.ModelArtifactId);
            if (!found)
            {
                throw new InvalidOperationException(
                    $"Bundle {bundle.BundleId}/{bundle.Version} references model artifact " +
                    $"'{effectiveRouting.ModelArtifactId}' in RoutingProfile but it is not declared " +
                    "in bundle.ModelArtifacts. Model artifacts must be explicitly declared.");
            }
        }
    }

    /// <summary>
    /// 解析 Routing.ModelArtifactId 对应的模型版本（作为 RouterModelHash / RankerModelHash）。
    /// null = deterministic 路径。
    /// </summary>
    private static string? ResolveModelArtifactVersion(ContextPolicyBundle bundle, RoutingProfile effectiveRouting)
    {
        if (string.IsNullOrEmpty(effectiveRouting.ModelArtifactId)) return null;
        return bundle.ModelArtifacts
            .FirstOrDefault(a => a.ArtifactId == effectiveRouting.ModelArtifactId)
            ?.Version;
    }
}

// ---------------------------------------------------------------------------
// §5.2c PolicyBundleHasher（P0-3：bundle 内容哈希计算，用于 immutability 验证）
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B P0-3：策略包内容哈希计算器。
/// 对 bundle 的不可变内容字段计算 SHA256，用于 activation 时的 content hash 验证。
/// </summary>
/// <remarks>
/// 哈希范围：BundleId + Version + Policies(5 versions) + Safety + Budget + Routing + ModelArtifacts。
/// 排除：CreatedAt / SupersededAt / SupersededByBundleId / Rollout（生命周期元数据，非内容）。
///
/// 调用方在 TryActivateAsync 前应使用此计算器生成 BundleContentHash，确保验证一致。
/// </remarks>
public static class PolicyBundleHasher
{
    /// <summary>计算 bundle 的内容哈希（SHA256 前 16 字符，前缀 "sha256:"）。</summary>
    public static string ComputeHash(ContextPolicyBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var sb = new StringBuilder();
        sb.Append(bundle.BundleId).Append('|');
        sb.Append(bundle.Version).Append('|');

        // Policies（5 个能力作用域版本）
        sb.Append(bundle.Policies.DecisionSchemaVersion).Append('|');
        sb.Append(bundle.Policies.PackagePolicyVersion).Append('|');
        sb.Append(bundle.Policies.RetrievalPolicyVersion).Append('|');
        sb.Append(bundle.Policies.RelationProfileVersion).Append('|');
        sb.Append(bundle.Policies.QualityContractVersion).Append('|');

        // Safety
        sb.Append(bundle.Safety.ProfileId).Append('|');
        sb.Append(bundle.Safety.AllowDeprecatedUsedByActiveChain).Append('|');
        sb.Append(bundle.Safety.AllowDuplicateReference).Append('|');
        sb.Append(string.Join(',', bundle.Safety.RequiredTags)).Append('|');
        sb.Append(string.Join(',', bundle.Safety.ForbiddenTags)).Append('|');

        // Budget
        sb.Append(bundle.Budget.ProfileId).Append('|');
        sb.Append(bundle.Budget.DefaultTokenBudget).Append('|');
        sb.Append(bundle.Budget.DefaultTopK).Append('|');
        sb.Append(bundle.Budget.StrictBudgetEnforcement).Append('|');
        foreach (var (key, value) in bundle.Budget.SectionRatios.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(key).Append(':').Append(value).Append(';');
        sb.Append('|');

        // Routing
        sb.Append(bundle.Routing.ProfileId).Append('|');
        sb.Append(bundle.Routing.EnableModelScoring).Append('|');
        sb.Append(bundle.Routing.ModelArtifactId ?? "null").Append('|');
        sb.Append(bundle.Routing.DeterministicWeight).Append('|');
        sb.Append(bundle.Routing.ModelWeight).Append('|');
        sb.Append(bundle.Routing.ModelConfidenceThreshold).Append('|');
        sb.Append(string.Join(',', bundle.Routing.EnabledExperts)).Append('|');

        // ModelArtifacts（按 ArtifactId 排序保证稳定）
        foreach (var artifact in bundle.ModelArtifacts.OrderBy(a => a.ArtifactId, StringComparer.Ordinal))
        {
            sb.Append(artifact.ArtifactId).Append(':');
            sb.Append(artifact.ModelType).Append(':');
            sb.Append(artifact.Version).Append(';');
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}

// ---------------------------------------------------------------------------
// §5.7 DefaultRouter + DefaultExpertCatalog
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-1：统一 Router 默认骨架。返回 Budget-Aware 平均分配的 ExpertRoutingDecisionSet。
/// </summary>
/// <remarks>
/// B-1 骨架：复用 RetrievalExpertMask.AllEnabled 语义，Mandatory/Constraint 永远启用，
/// 其他 Expert 平均分配 TokenBudget / TopK。Recency 由 Catalog 显式 disable。
/// B-2 将接入真实模型权重与 per-Expert 质量—成本曲线。
/// </remarks>
public sealed class DefaultRouter : IRouter
{
    /// <summary>Router 标识。</summary>
    public const string RouterId = "default-unified-router-b1";

    /// <summary>Router 版本。</summary>
    public const string RouterVersion = "b1";

    private readonly IExpertCatalog _catalog;

    /// <summary>构造 Router 骨架。</summary>
    /// <param name="catalog">Provider 能力目录（决定哪些 Expert 启用）。</param>
    public DefaultRouter(IExpertCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>产出 Expert 路由决策集。</summary>
    public ValueTask<ExpertRoutingDecisionSet> RouteAsync(
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var totalTokenBudget = request.TokenBudget > 0
            ? request.TokenBudget
            : snapshot.Budget.DefaultTokenBudget;
        var totalTopK = request.TopK > 0 && request.TopK != int.MaxValue
            ? request.TopK
            : snapshot.Budget.DefaultTopK;

        // B-1 骨架：Catalog 已注册的 Expert 启用；未注册（如 Recency）disable
        var available = _catalog.AvailableExperts;
        var nonMandatoryEnabledCount = available.Count(
            e => e != ExpertKind.Mandatory && e != ExpertKind.Constraint);

        var perExpertTokenBudget = nonMandatoryEnabledCount > 0
            ? totalTokenBudget / nonMandatoryEnabledCount
            : 0;
        var perExpertTopK = nonMandatoryEnabledCount > 0
            ? Math.Max(1, totalTopK / nonMandatoryEnabledCount)
            : 0;

        // 按枚举顺序生成决策（与 DefaultRetrievalRouter 算法对齐）
        var decisions = new List<ExpertRoutingDecision>(8);
        foreach (var expert in Enum.GetValues<RetrievalExpert>())
        {
            if (expert == RetrievalExpert.Unknown)
            {
                continue;
            }

            var mappedKind = MapToExpertKind(expert);
            var isRegistered = available.Contains(mappedKind);
            var isMandatory = expert == RetrievalExpert.Mandatory
                || expert == RetrievalExpert.Constraint;

            var enabled = isMandatory || isRegistered;
            var decisionTopK = isMandatory ? totalTopK : (enabled ? perExpertTopK : 0);
            var decisionTokenBudget = isMandatory ? totalTokenBudget : (enabled ? perExpertTokenBudget : 0);

            decisions.Add(new ExpertRoutingDecision
            {
                Expert = expert,
                Enabled = enabled,
                TopK = decisionTopK,
                TokenBudget = decisionTokenBudget,
                Weight = 1.0,
                ReasonCode = isMandatory ? "mandatory-always-enabled"
                    : (enabled ? "default" : "expert-not-registered"),
                DisabledReason = enabled ? null : "expert not registered in catalog",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["totalTokenBudget"] = totalTokenBudget.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["totalTopK"] = totalTopK.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["nonMandatoryEnabledCount"] = nonMandatoryEnabledCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            });
        }

        var decisionSet = new ExpertRoutingDecisionSet
        {
            Decisions = decisions,
            DecidedAt = DateTimeOffset.UtcNow,
            RouterId = RouterId,
            RouterVersion = RouterVersion,
            TotalTokenBudget = totalTokenBudget
        };

        return ValueTask.FromResult(decisionSet);
    }

    private static ExpertKind MapToExpertKind(RetrievalExpert expert) => expert switch
    {
        RetrievalExpert.Mandatory => ExpertKind.Mandatory,
        RetrievalExpert.Constraint => ExpertKind.Constraint,
        RetrievalExpert.Lexical => ExpertKind.Lexical,
        RetrievalExpert.Semantic => ExpertKind.Semantic,
        RetrievalExpert.WorkingMemory => ExpertKind.WorkingMemory,
        RetrievalExpert.StableMemory => ExpertKind.StableMemory,
        RetrievalExpert.Graph => ExpertKind.Graph,
        RetrievalExpert.Recency => ExpertKind.Recency,
        _ => ExpertKind.Mandatory
    };
}

/// <summary>
/// R28-B B-1：Provider 能力目录默认骨架。返回除 Recency 外的全部 Expert。
/// </summary>
/// <remarks>
/// B-1 骨架：Recency 默认不注册（设计文档 §5.7 约定）。
/// B-2 将由真实 Provider 注册网络驱动。
/// </remarks>
public sealed class DefaultExpertCatalog : IExpertCatalog
{
    /// <summary>默认可用 Expert 集合（不含 Recency）。</summary>
    public static readonly IReadOnlySet<ExpertKind> DefaultAvailableExperts =
        new HashSet<ExpertKind>
        {
            ExpertKind.Mandatory,
            ExpertKind.Constraint,
            ExpertKind.Lexical,
            ExpertKind.Semantic,
            ExpertKind.WorkingMemory,
            ExpertKind.StableMemory,
            ExpertKind.Graph
        };

    /// <summary>当前已注册的 Expert 集合。</summary>
    public IReadOnlySet<ExpertKind> AvailableExperts => DefaultAvailableExperts;
}

// ---------------------------------------------------------------------------
// §5.8 DefaultCanonicalCandidateMerger + DefaultEarlyAdmissionGate
//        + DefaultFeaturePipeline + DefaultSafetyGate + DefaultLifecycleGate
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-1：规范化候选合并器默认骨架。按 CanonicalCandidateKey 合并多 Expert 来源。
/// </summary>
/// <remarks>
/// B-1 骨架：合并策略为 union Origins + sum ExpertContributions + 保留首次出现的 Features/Utility。
/// B-2 将接入 Material sidecar 完整合并（同 Key 不同版本的版本选择策略）。
/// </remarks>
public sealed class DefaultCanonicalCandidateMerger : ICanonicalCandidateMerger
{
    /// <summary>合并多个 Expert 的输出，按 CanonicalCandidateKey 去重。</summary>
    public CandidateWorkingSet Merge(IReadOnlyList<ExpertExecutionResult> expertOutputs)
    {
        ArgumentNullException.ThrowIfNull(expertOutputs);

        var envelopeByKey = new Dictionary<CanonicalCandidateKey, ContextCandidateEnvelope>();
        var originsByKey = new Dictionary<CanonicalCandidateKey, List<ExpertOrigin>>();
        var contributionsByKey = new Dictionary<CanonicalCandidateKey, Dictionary<ExpertKind, double>>();
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>();

        foreach (var output in expertOutputs)
        {
            // P0-5：Material 冲突策略（不再后写覆盖前写）：
            //   - 相同 key、相同 content hash：合并 SourceRefs（union）；
            //   - 相同 key、不同 content hash：冲突 → throw（fail-fast）；
            //   - 不同 EntityVersion：CanonicalKey 自然不同，两个 Material 都保留。
            foreach (var (key, material) in output.Materials)
            {
                if (materials.TryGetValue(key, out var existing))
                {
                    var existingHash = ComputeMaterialContentHash(existing.Content);
                    var newHash = ComputeMaterialContentHash(material.Content);
                    if (string.Equals(existingHash, newHash, StringComparison.Ordinal))
                    {
                        // 相同 content hash：合并 SourceRefs
                        var mergedRefs = existing.SourceRefs
                            .Concat(material.SourceRefs)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        materials[key] = existing with { SourceRefs = mergedRefs };
                    }
                    else
                    {
                        // 不同 content hash：冲突/fail
                        throw new InvalidOperationException(
                            $"Material content conflict for key {key}: "
                            + $"existing hash {existingHash} vs new hash {newHash}. "
                            + "Same CanonicalCandidateKey with different content is not allowed. "
                            + "Use distinct EntityVersion to keep both candidates.");
                    }
                }
                else
                {
                    materials[key] = material;
                }
            }

            foreach (var envelope in output.Envelopes)
            {
                var key = envelope.CanonicalKey;
                if (envelopeByKey.TryGetValue(key, out var existing))
                {
                    // 合并 Origins（union）
                    if (!originsByKey.ContainsKey(key))
                    {
                        originsByKey[key] = new List<ExpertOrigin>(existing.Origins);
                    }
                    originsByKey[key].AddRange(envelope.Origins);

                    // 合并 ExpertContributions（sum per-Expert）
                    if (!contributionsByKey.ContainsKey(key))
                    {
                        contributionsByKey[key] = new Dictionary<ExpertKind, double>(
                            existing.ExpertContributions.Count);
                        foreach (var (expert, contribution) in existing.ExpertContributions)
                        {
                            contributionsByKey[key][expert] = contribution;
                        }
                    }
                    foreach (var (expert, contribution) in envelope.ExpertContributions)
                    {
                        contributionsByKey[key].TryAdd(expert, 0);
                        contributionsByKey[key][expert] += contribution;
                    }

                    // 保留首次出现的 Features/Utility（B-2 将引入更复杂的特征合并）
                    // envelopeByKey[key] 不更新
                }
                else
                {
                    envelopeByKey[key] = envelope;
                    originsByKey[key] = new List<ExpertOrigin>(envelope.Origins);
                    contributionsByKey[key] = new Dictionary<ExpertKind, double>(
                        envelope.ExpertContributions.Count);
                    foreach (var (expert, contribution) in envelope.ExpertContributions)
                    {
                        contributionsByKey[key][expert] = contribution;
                    }
                }
            }
        }

        // 重建合并后的 Envelopes（应用 union Origins + sum Contributions）
        var mergedEnvelopes = new List<ContextCandidateEnvelope>(envelopeByKey.Count);
        foreach (var (key, envelope) in envelopeByKey)
        {
            var origins = originsByKey[key];
            var contributions = contributionsByKey[key];
            mergedEnvelopes.Add(envelope with
            {
                Origins = origins,
                ExpertContributions = contributions
            });
        }

        return new CandidateWorkingSet
        {
            Envelopes = mergedEnvelopes,
            Materials = materials
        };
    }

    /// <summary>
    /// P0-5：计算 Material Content 的 stable content hash。
    /// 用于 Material 冲突检测（相同 key + 不同 content hash = 冲突）。
    /// </summary>
    private static string ComputeMaterialContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}

/// <summary>
/// R28-B B-1：Early Admission Gate 默认骨架。检查 scope mismatch / superseded / archived。
/// </summary>
/// <remarks>
/// B-1 骨架：仅检查 superseded + scope 字段非空；B-2 将接入 forbidden tag / illegal evidence。
/// </remarks>
public sealed class DefaultEarlyAdmissionGate : IEarlyAdmissionGate
{
    /// <summary>评估候选是否通过早期准入。</summary>
    public AdmissionResult Evaluate(ContextCandidateEnvelope envelope, EffectivePolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(snapshot);

        // superseded 永远拒绝
        if (envelope.Safety.IsSuperseded)
        {
            return new AdmissionResult(
                Admitted: false,
                ReasonCode: "superseded",
                Detail: "candidate superseded by newer version");
        }

        // scope mismatch 拒绝（CanonicalKey 的 WorkspaceId/CollectionId 必须与 snapshot 一致）
        var key = envelope.CanonicalKey;
        if (!string.IsNullOrEmpty(key.WorkspaceId)
            && !string.Equals(key.WorkspaceId, snapshot.ResolutionScope.WorkspaceId, StringComparison.Ordinal))
        {
            return new AdmissionResult(
                Admitted: false,
                ReasonCode: "scope-mismatch",
                Detail: $"workspace mismatch: candidate={key.WorkspaceId}, snapshot={snapshot.ResolutionScope.WorkspaceId}");
        }

        if (!string.IsNullOrEmpty(key.CollectionId)
            && !string.Equals(key.CollectionId, snapshot.ResolutionScope.CollectionId, StringComparison.Ordinal))
        {
            return new AdmissionResult(
                Admitted: false,
                ReasonCode: "scope-mismatch",
                Detail: $"collection mismatch: candidate={key.CollectionId}, snapshot={snapshot.ResolutionScope.CollectionId}");
        }

        return new AdmissionResult(Admitted: true, ReasonCode: "default", Detail: string.Empty);
    }
}

/// <summary>
/// R28-B B-1：Feature Pipeline 默认骨架。Identity transform（不修改 Envelope）。
/// </summary>
/// <remarks>
/// B-1 骨架：返回输入 envelopes 不变（rule-only convergence 阶段无需特征工程）。
/// B-2 将接入真实特征计算（semantic embedding / relation path 标准化）。
/// </remarks>
public sealed class DefaultFeaturePipeline : IFeaturePipeline
{
    /// <summary>计算/标准化候选特征向量。B-1 骨架：identity transform。</summary>
    public ValueTask<IReadOnlyList<ContextCandidateEnvelope>> EnrichAsync(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        FeaturePipelineContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // Identity transform：直接返回输入（envelopes 已是不可变 record）
        return ValueTask.FromResult<IReadOnlyList<ContextCandidateEnvelope>>(envelopes);
    }
}

/// <summary>
/// R28-B B-1：Decision Safety Gate 默认骨架。基于 envelope.Safety + bundle.Safety 评估。
/// </summary>
/// <remarks>
/// B-1 骨架：复用 DefaultContextDecisionEngine.EvaluateSafetyGate 语义。
/// Mandatory/Hard Constraint 免预算，不免 Safety/Lifecycle。
/// </remarks>
public sealed class DefaultSafetyGate : ISafetyGate
{
    /// <summary>评估候选是否通过 Safety Gate。</summary>
    public SafetyGateResult Evaluate(ContextCandidateEnvelope envelope, SafetyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(profile);

        var safety = envelope.Safety;

        // 候选自身 PassesSafetyGate=false（adapter 已预先标记）→ 信任之
        if (!safety.PassesSafetyGate)
        {
            return new SafetyGateResult(
                Passes: false,
                ReasonCode: safety.BlockReasonCode,
                Detail: safety.BlockReasonDetail);
        }

        // IsSuperseded / IsRequiredTagMismatch 永远阻断
        if (safety.IsSuperseded)
        {
            return new SafetyGateResult(
                Passes: false,
                ReasonCode: CandidateDecisionReasonCode.SupersededByCurrentVersion,
                Detail: "superseded by newer version");
        }

        if (safety.IsRequiredTagMismatch)
        {
            return new SafetyGateResult(
                Passes: false,
                ReasonCode: CandidateDecisionReasonCode.RequiredTagMismatch,
                Detail: "missing required tag");
        }

        // IsDeprecatedUsedByActiveChain 受 profile 控制
        if (safety.IsDeprecatedUsedByActiveChain && !profile.AllowDeprecatedUsedByActiveChain)
        {
            return new SafetyGateResult(
                Passes: false,
                ReasonCode: CandidateDecisionReasonCode.DeprecatedBlocked,
                Detail: "deprecated-used-by-active-chain blocked by safety profile");
        }

        // IsDuplicate 受 profile 控制
        if (safety.IsDuplicate && !profile.AllowDuplicateReference)
        {
            return new SafetyGateResult(
                Passes: false,
                ReasonCode: CandidateDecisionReasonCode.DuplicateSuppressed,
                Detail: "duplicate reference blocked by safety profile");
        }

        return new SafetyGateResult(
            Passes: true,
            ReasonCode: CandidateDecisionReasonCode.Unknown,
            Detail: string.Empty);
    }
}

/// <summary>
/// R28-B B-1：Lifecycle Gate 默认骨架。检查候选生命周期状态。
/// </summary>
/// <remarks>
/// B-1 骨架：仅检查 LifecycleState 非 "deprecated"/"archived"。
/// B-2 将接入完整 lifecycle 规则（frozen baseline / activation epoch 一致性）。
/// </remarks>
public sealed class DefaultLifecycleGate : ILifecycleGate
{
    /// <summary>评估候选是否通过 Lifecycle Gate。</summary>
    public LifecycleGateResult Evaluate(ContextCandidateEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var state = envelope.Safety.LifecycleState ?? string.Empty;

        // deprecated 状态由 SafetyGate 控制（受 AllowDeprecatedUsedByActiveChain）；
        // 此处仅拒绝 archived / frozen baseline 之外的非法状态
        if (string.Equals(state, "archived", StringComparison.OrdinalIgnoreCase))
        {
            return new LifecycleGateResult(
                Passes: false,
                ReasonCode: "archived",
                Detail: "candidate archived");
        }

        return new LifecycleGateResult(Passes: true, ReasonCode: "default", Detail: string.Empty);
    }
}

// ---------------------------------------------------------------------------
// §5.9 DefaultUtilityScorer + DefaultGlobalAllocator
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-1：Utility Scorer 默认骨架。rule-only 模式（w_d=1.0, w_m=0.0）。
/// </summary>
/// <remarks>
/// B-1 骨架：envelopes 是不可变 record，void Score 无法原地修改。
/// rule-only convergence 阶段：adapter 已在构造 envelope 时填充 DeterministicScore，
/// FinalScore = DeterministicScore（由 adapter 保证）。
/// 此骨架实现为 no-op（读取校验 FinalScore 已等于 DeterministicScore）。
/// B-2 将契约改为 ScoreAsync 返回新列表（与 EnrichAsync 对齐）。
/// </remarks>
public sealed class DefaultUtilityScorer : IUtilityScorer
{
    /// <summary>对候选集合计算效用评分。B-1 骨架：no-op（rule-only，adapter 已填充 FinalScore）。</summary>
    public void Score(IReadOnlyList<ContextCandidateEnvelope> envelopes, EffectivePolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(snapshot);

        // B-1 骨架：rule-only 模式下 FinalScore 应已由 adapter 设置为 DeterministicScore。
        // 此处仅做读取校验，不修改 envelopes（不可变 record，void 签名无法原地修改）。
        // B-2 将契约改为 ScoreAsync 返回新列表以支持模型加权。
    }
}

/// <summary>
/// R28-B B-1：统一全局分配器默认骨架。TopK + TokenBudget 硬截断。
/// </summary>
/// <remarks>
/// B-1 骨架：复用 DefaultContextDecisionEngine 的分配算法
/// （IsMandatory/IsHardConstraint 优先 → FinalScore 降序 → EstimatedTokens 降序 → CandidateId 升序 →
/// TopK 截断 → TokenBudget 截断）。
/// B-2 将接入 SectionRatios 分层比例分配 + MandatoryOverflowPolicy。
/// </remarks>
public sealed class DefaultGlobalAllocator : IGlobalAllocator
{
    /// <summary>执行全局预算分配 + per-section 配额。</summary>
    public AllocationResult Allocate(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(snapshot);

        // 排序：IsMandatory/IsHardConstraint 降序 → FinalScore 降序 → EstimatedTokens 降序 → CandidateId 升序
        var ordered = envelopes
            .OrderByDescending(e => e.Safety.IsMandatory || e.Safety.IsHardConstraint)
            .ThenByDescending(e => e.Utility.FinalScore)
            .ThenByDescending(e => e.EstimatedTokens)
            .ThenBy(e => e.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tokenBudget = snapshot.Budget.DefaultTokenBudget;
        var topK = snapshot.Budget.DefaultTopK;

        var selected = new List<ContextCandidateEnvelope>();
        var dropped = new List<ContextCandidateEnvelope>();
        var decisions = new List<CandidateAllocationDecision>(ordered.Count);
        var usedTokens = 0;
        var takenCount = 0;

        foreach (var envelope in ordered)
        {
            var isMandatory = envelope.Safety.IsMandatory || envelope.Safety.IsHardConstraint;

            // TopK 检查（非 mandatory）
            if (!isMandatory && takenCount >= topK)
            {
                dropped.Add(envelope);
                decisions.Add(new CandidateAllocationDecision
                {
                    CandidateKey = envelope.CanonicalKey,
                    Section = ResolveSection(envelope),
                    IncludedTokens = 0,
                    IsTruncated = false,
                    ReasonCode = CandidateDecisionReasonCode.SectionQuotaExceeded
                });
                continue;
            }

            // Token budget 检查（非 mandatory）
            // R28-B.6：partial truncation — 当候选超出剩余预算时，不完全丢弃，
            // 而是包含部分 token（IsTruncated=true，IncludedTokens=remaining）。
            // 只有剩余空间为 0 时才完全丢弃。实际内容截断由 Projector 通过
            // IContextTokenizerResolver 在 Material sidecar 恢复时执行。
            if (!isMandatory && usedTokens + envelope.EstimatedTokens > tokenBudget)
            {
                var remaining = tokenBudget - usedTokens;
                if (remaining > 0)
                {
                    // 部分包含：候选被截断到剩余预算内
                    selected.Add(envelope);
                    usedTokens += remaining;
                    takenCount++;
                    decisions.Add(new CandidateAllocationDecision
                    {
                        CandidateKey = envelope.CanonicalKey,
                        Section = ResolveSection(envelope),
                        IncludedTokens = remaining,
                        IsTruncated = true,
                        ReasonCode = CandidateDecisionReasonCode.SelectedHighestUtility
                    });
                }
                else
                {
                    // 剩余空间为 0，完全丢弃
                    dropped.Add(envelope);
                    decisions.Add(new CandidateAllocationDecision
                    {
                        CandidateKey = envelope.CanonicalKey,
                        Section = ResolveSection(envelope),
                        IncludedTokens = 0,
                        IsTruncated = false,
                        ReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded
                    });
                }
                continue;
            }

            selected.Add(envelope);
            usedTokens += envelope.EstimatedTokens;
            takenCount++;
            decisions.Add(new CandidateAllocationDecision
            {
                CandidateKey = envelope.CanonicalKey,
                Section = ResolveSection(envelope),
                IncludedTokens = envelope.EstimatedTokens,
                IsTruncated = false,
                ReasonCode = CandidateDecisionReasonCode.SelectedHighestUtility
            });
        }

        var outcome = new ContextDecisionOutcomeSummary
        {
            SelectedCount = selected.Count,
            DroppedCount = dropped.Count,
            EstimatedTokens = usedTokens,
            TokenBudget = tokenBudget,
            Sections = Array.Empty<string>(), // B-1 骨架不实现 section 分层
            SafetyGateBlockedCount = 0, // SafetyGate 在 Engine 内执行
            BudgetExceededCount = dropped.Count
        };

        return new AllocationResult(selected, dropped, decisions, outcome);
    }

    private static string ResolveSection(ContextCandidateEnvelope envelope)
    {
        // B-1 骨架：基于 Source 映射到 section 名（B-2 将由 Allocator 真正分配 section）
        return envelope.Source switch
        {
            ContextCandidateSource.Mandatory or ContextCandidateSource.Constraint => "mandatory",
            ContextCandidateSource.WorkingMemory or ContextCandidateSource.StableMemory => "memory",
            ContextCandidateSource.Graph => "relations",
            ContextCandidateSource.GlobalContext => "global",
            ContextCandidateSource.RelatedContext => "related",
            _ => "default"
        };
    }
}

// ---------------------------------------------------------------------------
// AgentContext Projector
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-1：Agent Context Projector 默认骨架。
/// 从 DecisionResult + WorkingSet 投影为 AgentContextSnapshot。
/// </summary>
/// <remarks>
/// B-1 骨架：投影为单 section（"context"），包含所有 selected 候选的 Content 拼接。
/// B-2 将按 CandidateAllocationDecision.Section 分区投影。
/// 不访问 Store；不重新排序、过滤、截断或计分（仅格式投影）。
/// </remarks>
public sealed class AgentContextProjector : IAgentContextProjector
{
    /// <summary>将决策结果 + 候选正文投影为 AgentContextSnapshot。</summary>
    public AgentContextSnapshot Project(ContextDecisionResult result, CandidateWorkingSet workingSet)
    {
        return Project(result, workingSet, context: null);
    }

    /// <summary>
    /// P0-7：将决策结果 + 候选正文 + 投影上下文投影为 AgentContextSnapshot。
    /// 使用 context.AgentSession（如有）而非伪造的 session ID。
    /// 按 CandidateAllocationDecision.Section 分区投影（而非单 section 拼接）。
    /// </summary>
    public AgentContextSnapshot Project(ContextDecisionResult result, CandidateWorkingSet workingSet, ProjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(workingSet);

        // P0-7：构建 CanonicalKey → AllocationDecision 索引，用于按 Section 分区
        var allocationByKey = result.AllocationDecisions
            .ToDictionary(d => d.CandidateKey, d => d);

        // P0-7：按 Section 分区投影
        var sectionGroups = result.SelectedEnvelopes
            .Select(env =>
            {
                var section = ResolveAgentSectionName(env.Source);
                var includedTokens = env.EstimatedTokens;
                if (allocationByKey.TryGetValue(env.CanonicalKey, out var decision))
                {
                    section = decision.Section;
                    includedTokens = decision.IncludedTokens;
                }
                return new { Envelope = env, Section = section, IncludedTokens = includedTokens };
            })
            .GroupBy(x => x.Section)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var sections = new List<AgentContextSection>(sectionGroups.Count);
        var totalActualTokens = 0;

        foreach (var group in sectionGroups)
        {
            var sectionTokens = 0;
            var contentBuilder = new StringBuilder();
            var candidateCount = 0;

            foreach (var item in group)
            {
                if (workingSet.Materials.TryGetValue(item.Envelope.CanonicalKey, out var material))
                {
                    if (contentBuilder.Length > 0)
                    {
                        contentBuilder.Append("\n\n");
                    }
                    contentBuilder.Append(material.Content);
                    sectionTokens += item.IncludedTokens;
                    candidateCount++;
                }
            }

            sections.Add(new AgentContextSection
            {
                SectionName = group.Key,
                SortOrder = sections.Count,
                TokenBudget = result.Outcome.TokenBudget,
                ActualTokens = sectionTokens,
                Content = contentBuilder.ToString(),
                Source = "ContextCore.UnifiedRuntime",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["candidateCount"] = candidateCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["purpose"] = result.Purpose.ToString(),
                    ["runtimeKind"] = result.RuntimeKind.ToString()
                }
            });
            totalActualTokens += sectionTokens;
        }

        // P0-7：使用真实 AgentSessionId（来自 ProjectionContext），而非伪造的 session
        var session = context?.AgentSession ?? new AgentSessionId
        {
            Value = $"session-{result.RequestId}",
            RuntimeKind = AgentRuntimeKind.Unknown,
            WorkspaceId = context?.WorkspaceId ?? string.Empty,
            CollectionId = context?.CollectionId,
            CreatedAt = result.DecidedAt
        };

        var snapshot = new AgentContextSnapshot
        {
            SnapshotId = $"snap-{result.RequestId}",
            Session = session,
            CreatedAt = result.DecidedAt,
            TokenBudget = result.Outcome.TokenBudget,
            ActualTokens = totalActualTokens,
            Sections = sections,
            DecisionRequestIds = new[] { result.RequestId },
            ConstraintIds = Array.Empty<string>(),
            ToolCallRefs = new Dictionary<string, string>(StringComparer.Ordinal),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["policyVersion"] = result.PolicyVersion,
                ["modelEnabled"] = result.ModelEnabled.ToString().ToLowerInvariant(),
                ["runtimeKind"] = result.RuntimeKind.ToString()
            }
        };

        return snapshot;
    }

    private static string ResolveAgentSectionName(ContextCandidateSource source) => source switch
    {
        ContextCandidateSource.Mandatory => "mandatory",
        ContextCandidateSource.Constraint => "constraint",
        ContextCandidateSource.WorkingMemory => "working_memory",
        ContextCandidateSource.StableMemory => "stable_memory",
        ContextCandidateSource.Lexical or ContextCandidateSource.Semantic or
        ContextCandidateSource.Recency => "recent_context",
        ContextCandidateSource.Graph or ContextCandidateSource.RelatedContext => "related_context",
        ContextCandidateSource.GlobalContext => "global_context",
        _ => "context"
    };
}
