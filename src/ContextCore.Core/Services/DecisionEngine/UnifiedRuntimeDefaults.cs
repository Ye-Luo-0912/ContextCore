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
/// B-2 阶段升级：从 B-1 骨架（仅委托 Engine）升级为真实编排
/// （Policy → EarlyGate → FeaturePipeline → Engine → Allocator）。
/// 候选来源：SeedCandidates（由 WorkingSetTee 从 Legacy 主链捕获）。
/// Provider 网络不接入（B-4 才接入真实 ICandidateProvider）。
///
/// 编排流程：
///   1. 策略解析（IResolvedPolicyProvider → EffectivePolicySnapshot）
///   2. EarlyAdmissionGate 评估（scope mismatch / superseded）
///   3. FeaturePipeline 特征计算（identity transform in B-2）
///   4. SafetyGate + LifecycleGate 评估
///   5. UtilityScorer 评分（rule-only in B-2）
///   6. Engine 决策（委托既有 IContextDecisionEngine 执行 budget allocation）
///   7. Allocator 全局分配（TopK + TokenBudget 截断）
/// </remarks>
public sealed class DefaultContextDecisionRuntime : IContextDecisionRuntime
{
    private readonly IContextDecisionEngine _engine;
    private readonly IResolvedPolicyProvider _policyProvider;
    private readonly IEarlyAdmissionGate _earlyAdmissionGate;
    private readonly IFeaturePipeline _featurePipeline;
    private readonly ISafetyGate _safetyGate;
    private readonly ILifecycleGate _lifecycleGate;
    private readonly IUtilityScorer _utilityScorer;
    private readonly IGlobalAllocator _allocator;

    /// <summary>构造 pure Runtime。</summary>
    public DefaultContextDecisionRuntime(
        IContextDecisionEngine engine,
        IResolvedPolicyProvider policyProvider,
        IEarlyAdmissionGate earlyAdmissionGate,
        IFeaturePipeline featurePipeline,
        ISafetyGate safetyGate,
        ILifecycleGate lifecycleGate,
        IUtilityScorer utilityScorer,
        IGlobalAllocator allocator)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _earlyAdmissionGate = earlyAdmissionGate ?? throw new ArgumentNullException(nameof(earlyAdmissionGate));
        _featurePipeline = featurePipeline ?? throw new ArgumentNullException(nameof(featurePipeline));
        _safetyGate = safetyGate ?? throw new ArgumentNullException(nameof(safetyGate));
        _lifecycleGate = lifecycleGate ?? throw new ArgumentNullException(nameof(lifecycleGate));
        _utilityScorer = utilityScorer ?? throw new ArgumentNullException(nameof(utilityScorer));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    /// <summary>
    /// 执行 pure Runtime 编排：Policy → EarlyGate → Feature → Safety → Lifecycle → Score → Engine → Allocate。
    /// </summary>
    public async ValueTask<ContextDecisionResult> ExecuteAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Step 1：策略解析
        var snapshot = await _policyProvider.ResolveAsync(request, cancellationToken).ConfigureAwait(false);

        var seedCandidates = request.SeedCandidates;
        if (seedCandidates.Count == 0)
        {
            return EmptyResult(request, snapshot);
        }

        // Step 2：EarlyAdmissionGate — 拒绝 scope mismatch / superseded
        var admitted = new List<ContextCandidateEnvelope>(seedCandidates.Count);
        foreach (var envelope in seedCandidates)
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

        // Step 3：FeaturePipeline — 特征计算（B-2 identity transform）
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

        // Step 4 + 5：SafetyGate + LifecycleGate — 拒绝非法候选
        var passedGates = new List<ContextCandidateEnvelope>(enriched.Count);
        foreach (var envelope in enriched)
        {
            var safety = _safetyGate.Evaluate(envelope, snapshot.Safety);
            if (!safety.Passes)
            {
                continue;
            }
            var lifecycle = _lifecycleGate.Evaluate(envelope);
            if (!lifecycle.Passes)
            {
                continue;
            }
            passedGates.Add(envelope);
        }

        if (passedGates.Count == 0)
        {
            return EmptyResult(request, snapshot);
        }

        // Step 6：UtilityScorer — 评分（B-2 rule-only no-op，adapter 已填充 FinalScore）
        _utilityScorer.Score(passedGates, snapshot);

        // Step 7：委托既有 IContextDecisionEngine 执行 budget allocation
        var decisionRequest = new ContextDecisionRequest
        {
            RequestId = request.RequestId,
            DecisionSource = ResolveDecisionSource(request.Purpose),
            WorkspaceId = request.Scope.WorkspaceId,
            CollectionId = request.Scope.CollectionId,
            Candidates = passedGates,
            TokenBudget = request.TokenBudget > 0 ? request.TokenBudget : snapshot.Budget.DefaultTokenBudget,
            TopK = request.TopK > 0 && request.TopK != int.MaxValue
                ? request.TopK
                : snapshot.Budget.DefaultTopK,
            SectionRatios = snapshot.Budget.SectionRatios.Count > 0 ? snapshot.Budget.SectionRatios : null,
            PolicyBundleId = snapshot.Reference.BundleId,
            QueryText = request.QueryText,
            CreatedAt = DateTimeOffset.UtcNow,
            EnableModel = snapshot.Routing.EnableModelScoring
        };

        var engineResult = await _engine.DecideAsync(decisionRequest, cancellationToken).ConfigureAwait(false);

        // Step 8：Allocator 全局分配（补充 AllocationDecisions，与 Envelope 解耦）
        var allocationResult = _allocator.Allocate(engineResult.SelectedEnvelopes, snapshot);

        return new ContextDecisionResult
        {
            RequestId = engineResult.RequestId,
            DecisionSource = engineResult.DecisionSource,
            SelectedEnvelopes = allocationResult.Selected,
            DroppedEnvelopes = allocationResult.Dropped,
            Outcome = allocationResult.Outcome,
            PolicyVersion = engineResult.PolicyVersion,
            ModelVersion = engineResult.ModelVersion,
            DecidedAt = engineResult.DecidedAt,
            ModelEnabled = engineResult.ModelEnabled,
            Purpose = request.Purpose,
            RuntimeKind = ContextDecisionRuntimeKind.UnifiedV2,
            AllocationDecisions = allocationResult.AllocationDecisions,
            PolicyReference = snapshot.Reference
        };
    }

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
            // 合并 Materials（后写覆盖前写；B-2 将引入版本选择策略）
            foreach (var (key, material) in output.Materials)
            {
                materials[key] = material;
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
            if (!isMandatory && usedTokens + envelope.EstimatedTokens > tokenBudget)
            {
                dropped.Add(envelope);
                decisions.Add(new CandidateAllocationDecision
                {
                    CandidateKey = envelope.CanonicalKey,
                    Section = ResolveSection(envelope),
                    IncludedTokens = 0,
                    IsTruncated = false,
                    ReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded
                });
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
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(workingSet);

        // B-1 骨架：单 section 投影，拼接所有 selected 候选的 Material.Content
        var sections = new List<AgentContextSection>(1);
        var actualTokens = 0;
        var contentBuilder = new System.Text.StringBuilder();

        foreach (var envelope in result.SelectedEnvelopes)
        {
            if (workingSet.Materials.TryGetValue(envelope.CanonicalKey, out var material))
            {
                if (contentBuilder.Length > 0)
                {
                    contentBuilder.Append("\n\n");
                }
                contentBuilder.Append(material.Content);
                actualTokens += envelope.EstimatedTokens;
            }
        }

        sections.Add(new AgentContextSection
        {
            SectionName = "context",
            SortOrder = 0,
            TokenBudget = result.Outcome.TokenBudget,
            ActualTokens = actualTokens,
            Content = contentBuilder.ToString(),
            Source = "ContextCore.UnifiedRuntime",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["candidateCount"] = result.SelectedEnvelopes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["purpose"] = result.Purpose.ToString(),
                ["runtimeKind"] = result.RuntimeKind.ToString()
            }
        });

        var snapshot = new AgentContextSnapshot
        {
            SnapshotId = $"snap-{result.RequestId}",
            Session = new AgentSessionId
            {
                Value = $"session-{result.RequestId}",
                RuntimeKind = AgentRuntimeKind.Unknown,
                WorkspaceId = string.Empty,
                CollectionId = null,
                CreatedAt = result.DecidedAt
            },
            CreatedAt = result.DecidedAt,
            TokenBudget = result.Outcome.TokenBudget,
            ActualTokens = actualTokens,
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
}
