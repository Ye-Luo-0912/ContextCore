using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// Unified Runtime 默认实现骨架（Skeletons）
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
//   - DefaultContextDecisionRuntime 替换为真实编排（Policy → Router → Providers →
//     Merge → EarlyGate → FeaturePipeline → Engine → Allocator）。
//   - DefaultRouter / DefaultExpertCatalog / DefaultCanonicalCandidateMerger
//     替换为接入真实 Provider 网络的实现。
//   - Legacy 移除后，本文件中保留的实现升级为权威路径。
// ===========================================================================

// ---------------------------------------------------------------------------
// DefaultContextDecisionRuntime（B-2 升级为 pure Runtime）
// ---------------------------------------------------------------------------

/// <summary>
/// 统一 Context Decision Runtime — pure Runtime 真实编排。
/// </summary>
/// <remarks>
/// 修复：移除 Runtime 后二次 Allocate。Engine 是分配的唯一权威所有者，
/// Runtime 不再在 Engine 后调用 IGlobalAllocator。Engine 内部已执行
/// SafetyGate → UtilityScoring → 排序 → TopK/TokenBudget 截断。
///
/// 修复：Runtime 注入 IRouter / IExpertCatalog / ICandidateProvider / ICanonicalCandidateMerger，
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
    private readonly IRuntimeRequestNormalizer _requestNormalizer;
    private readonly IRequestSemanticHasher _requestSemanticHasher;
    private readonly IExecutionArtifactFactory _executionArtifactFactory;
    private readonly UtilityLedgerMaterializer? _utilityLedgerMaterializer;
    private readonly LearningMaterializationDispatcher? _materializationDispatcher;
    private readonly IComponentHealthRegistry? _componentHealthRegistry;
    // Selected 候选正文批量 hydrator（可选）。未注入时保持旧行为（IncludeContent=true）。
    private readonly ISelectedCandidateHydrator? _selectedCandidateHydrator;

    /// <summary>构造 pure Runtime。</summary>
    /// <param name="providerTimeout">单个 Provider 调用超时（默认 30s）。</param>
    /// <param name="requestNormalizer">R28-B.7-Final：请求标准化器（null 时使用默认实现）。</param>
    /// <param name="requestSemanticHasher">R28-B.7-Final：请求语义哈希器（null 时使用默认实现）。</param>
    /// <param name="executionArtifactFactory">R28-B.7-Final：执行结果工厂（null 时使用默认实现）。</param>
    /// <param name="utilityLedgerMaterializer">Utility Ledger 物化器（null 时跳过物化；生产路径注入）。
    /// 注意：生产路径应注入 <paramref name="materializationDispatcher"/> 而非直接注入 materializer——
    /// dispatcher 使用 Durable Outbox / bounded Channel 替代每请求 Task.Run fire-and-forget。
    /// 此参数保留用于未注入 dispatcher 时的测试/兼容路径。</param>
    /// <param name="componentHealthRegistry">组件健康注册表（null 时跳过组件级归因与回退，向后兼容 P5 之前的行为）。</param>
    /// <param name="materializationDispatcher">Learning Loop Durable Outbox 调度器。
    /// 非空时优先使用——通过 bounded Channel + 固定 worker 或 Durable Outbox 替代 Task.Run，
    /// 消除进程崩溃静默丢训练数据、Task 风暴、无背压等问题。null 时回退到 materializer 直接调用路径。</param>
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
        TimeSpan? providerTimeout = null,
        IRuntimeRequestNormalizer? requestNormalizer = null,
        IRequestSemanticHasher? requestSemanticHasher = null,
        IExecutionArtifactFactory? executionArtifactFactory = null,
        UtilityLedgerMaterializer? utilityLedgerMaterializer = null,
        IComponentHealthRegistry? componentHealthRegistry = null,
        LearningMaterializationDispatcher? materializationDispatcher = null,
        ISelectedCandidateHydrator? selectedCandidateHydrator = null)
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
        // 注入新的 artifact 服务，null 时回退到默认单例实现
        _requestNormalizer = requestNormalizer ?? DefaultRuntimeRequestNormalizer.Instance;
        _requestSemanticHasher = requestSemanticHasher ?? DefaultRequestSemanticHasher.Instance;
        _executionArtifactFactory = executionArtifactFactory ?? DefaultExecutionArtifactFactory.Instance;
        // Utility Ledger 物化器（可选；未注入时不物化）
        _utilityLedgerMaterializer = utilityLedgerMaterializer;
        // 组件健康注册表（可选；未注入时不归因、不回退）
        _componentHealthRegistry = componentHealthRegistry;
        // Learning Loop Durable Outbox：注入 dispatcher 后，主决策流通过 bounded Channel / outbox
        // 触发物化，消除每请求 Task.Run（生产热路径）。null 时回退到 materializer 直接路径（测试用）。
        _materializationDispatcher = materializationDispatcher;
        // Selected 候选正文批量 hydrator（可选；未注入时保持旧行为）
        _selectedCandidateHydrator = selectedCandidateHydrator;
    }

    /// <summary>
    /// 执行 pure Runtime 编排：
    /// Policy → Router → Providers → Merge → Seed merge → EarlyGate → Feature → Safety → Lifecycle → Score → Engine。
    /// 委托到 ExecuteWithWorkingSetAsync，仅返回 Decision 部分（向后兼容）。
    /// </summary>
    public async ValueTask<ContextDecisionResult> ExecuteAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        var executionResult = await ExecuteWithWorkingSetAsync(request, cancellationToken).ConfigureAwait(false);
        return executionResult.Decision;
    }

    /// <summary>
    /// 执行完整决策编排，返回 ExecutionResult（含 WorkingSet + Policy + Routing + ProviderReports）。
    /// </summary>
    /// <remarks>
    /// 完整编排流程：
    ///   1. 策略解析（IResolvedPolicyProvider → EffectivePolicySnapshot）
    ///   2. Router 路由（IRouter → ExpertRoutingDecisionSet）
    ///   3. Provider DAG 召回（两阶段 — Phase 1 主召回 + Phase 2 Graph 扩展）
    ///   4. Canonical Merge（ICanonicalCandidateMerger 合并 Provider 输出）
    ///   5. SeedCandidates 合并（将外部传入的种子候选合并到工作集）
    ///   6. EarlyAdmissionGate 批量评估（保留 Rejected 到 DroppedEnvelopes）
    ///   7. FeaturePipeline 特征计算
    ///   8. 委托 IContextDecisionEngine 执行完整决策
    ///   9. 合并 EarlyRejected + Engine.DroppedEnvelopes 到最终 DroppedEnvelopes
    ///   10. 构建 ContextDecisionExecutionResult（含完整 WorkingSet）
    /// </remarks>
    public async ValueTask<ContextDecisionExecutionResult> ExecuteWithWorkingSetAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // 请求标准化（填充默认值、规范化 Scope），贯穿整个请求生命周期
        request = _requestNormalizer.Normalize(request);
        // 请求语义哈希（基于标准化请求计算，用于 replay 匹配与审计）
        var requestSemanticHash = _requestSemanticHasher.ComputeHash(request);

        // 组件归因 scopeKey（与 Engine 内部 scopeKey 一致：workspaceId + "/" + collectionId）
        var componentScopeKey = $"{request.Scope.WorkspaceId}/{request.Scope.CollectionId}";
        var componentRegistry = _componentHealthRegistry;

        // Step 1：策略解析
        var snapshot = await _policyProvider.ResolveAsync(request, cancellationToken).ConfigureAwait(false);

        // Step 2：Router 路由 — 产出 ExpertRoutingDecisionSet
        var routingDecisions = await _router.RouteAsync(request, snapshot, cancellationToken).ConfigureAwait(false);

        // 组件级回退查询 — Provider 组件回退时，按 provider kind 切换到 fallback provider
        //（Semantic → Lexical；Graph → 跳过/disabled）。
        // 细化到 ProviderKind 粒度：Semantic 慢不会导致 Graph 被关闭。
        // InvokeEnabledProvidersWithDagAsync 内部根据 ShouldFallbackProvider(ProviderKind, ...) 逐个判断。
        var providerFallbackActive = componentRegistry is not null
            && componentRegistry.ShouldFallbackComponent(ComponentKind.Provider, componentScopeKey);

        // Step 3：Provider DAG 召回（两阶段）
        // Phase 1：执行 Mandatory + Constraint + Lexical + Semantic + WorkingMemory + StableMemory
        // Phase 2：执行 Graph Provider，将 Phase 1 merged envelopes 作为 SeedCandidates 传入
        // 用 Stopwatch 拆分 provider_ms（聚合所有 Provider 调用总耗时），记录到 IComponentHealthRegistry
        // 注：per-provider 耗时由 InvokeProviderBatchAsync 内部通过 RecordProviderTime 单独记录。
        var providerSw = componentRegistry is not null ? Stopwatch.StartNew() : null;
        bool providerSucceeded = false;
        try
        {
            var (expertOutputs, providerReports) = await InvokeEnabledProvidersWithDagAsync(
                request, snapshot, routingDecisions, cancellationToken,
                providerFallbackActive, componentScopeKey).ConfigureAwait(false);
            providerSucceeded = providerReports.Count == 0 || providerReports.All(r => r.Succeeded);

            // 从 expertOutputs + providerReports 构建 ProviderExecutionArtifact[]，
            // 供 IExecutionArtifactFactory 统一构建 ProviderReports 与 ProviderOutputSnapshots
            var providerArtifacts = BuildProviderArtifacts(expertOutputs, providerReports);

            // Step 4：Canonical Merge — 合并 Provider 输出
            // 用 Stopwatch 拆分 merge_ms，记录到 IComponentHealthRegistry
            var mergeSw = componentRegistry is not null ? Stopwatch.StartNew() : null;
            CandidateWorkingSet mergedWorkingSet;
            try
            {
                mergedWorkingSet = _canonicalMerger.Merge(expertOutputs);
            }
            finally
            {
                if (mergeSw is not null)
                {
                    mergeSw.Stop();
                    componentRegistry!.RecordComponentTime(
                        ComponentKind.Merge, mergeSw.Elapsed.TotalMilliseconds, succeeded: true,
                        componentScopeKey, cancellationToken);
                }
            }

            return await ContinueAfterMergeAsync(
                request, requestSemanticHash, snapshot, routingDecisions,
                expertOutputs, providerReports, providerArtifacts, mergedWorkingSet,
                componentRegistry, componentScopeKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            providerSucceeded = false;
            throw;
        }
        finally
        {
            if (providerSw is not null)
            {
                providerSw.Stop();
                componentRegistry!.RecordComponentTime(
                    ComponentKind.Provider, providerSw.Elapsed.TotalMilliseconds, providerSucceeded,
                    componentScopeKey, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 执行 Merge 之后的编排（Feature → Engine → 合并 dropped → 物化）。
    /// 拆分出来便于在 provider_ms / merge_ms 计时 finally 块之后继续执行 feature_ms 等组件计时。
    /// </summary>
    private async ValueTask<ContextDecisionExecutionResult> ContinueAfterMergeAsync(
        ContextDecisionRuntimeRequest request,
        string requestSemanticHash,
        EffectivePolicySnapshot snapshot,
        ExpertRoutingDecisionSet routingDecisions,
        IReadOnlyList<ExpertExecutionResult> expertOutputs,
        IReadOnlyList<ProviderExecutionReport> providerReports,
        IReadOnlyList<ProviderExecutionArtifact> providerArtifacts,
        CandidateWorkingSet mergedWorkingSet,
        IComponentHealthRegistry? componentRegistry,
        string componentScopeKey,
        CancellationToken cancellationToken)
    {

        // Step 5：SeedCandidates / SeedWorkingSet 合并 — 将外部传入的种子候选加入工作集
        // 优先使用 SeedWorkingSet（含 Envelopes + Materials），回退到 SeedCandidates（仅 Envelopes）
        var seedEnvelopes = request.SeedWorkingSet?.Envelopes ?? request.SeedCandidates;
        var allEnvelopes = MergeSeedCandidates(mergedWorkingSet.Envelopes, seedEnvelopes);

        // 合并 SeedWorkingSet.Materials 到 complete WorkingSet（保留种子 Material，不丢失）
        var completeMaterials = MergeSeedMaterials(mergedWorkingSet.Materials, request.SeedWorkingSet?.Materials);

        // 构建 complete WorkingSet（包含 Materials）：保留所有 Materials 供 Projector 恢复正文
        var completeWorkingSet = new CandidateWorkingSet
        {
            Envelopes = allEnvelopes,
            Materials = completeMaterials
        };

        if (allEnvelopes.Count == 0)
        {
            // 空结果路径无 SelectedEnvelopes + DroppedEnvelopes 可物化（materializer 内部为 no-op），
            // 但仍触发以保持 DecisionId 审计链（即便 0 条 entry 也可追踪决策执行）。
            var emptyExecutionResult = EmptyExecutionResult(request, requestSemanticHash, providerArtifacts, snapshot, routingDecisions, completeWorkingSet);
            // await 等待 Learning Event 持久化到 outbox 表完成（与主决策路径一致）。
            // 捕获 LearningPersistenceStatus 并写入 ExecutionResult，让 Durable Failure 不再被静默吞掉。
            var emptyLearningStatus = await TriggerUtilityLedgerMaterializationAsync(emptyExecutionResult.Decision, request).ConfigureAwait(false);
            return emptyExecutionResult with { LearningPersistenceStatus = emptyLearningStatus };
        }

        // Step 6：EarlyAdmissionGate 批量评估（保留 Rejected）
        var partition = _earlyAdmissionGate.EvaluateBatch(allEnvelopes, snapshot);
        var admitted = partition.Admitted;
        var earlyRejected = partition.Rejected;

        if (admitted.Count == 0)
        {
            // 所有候选被 EarlyGate 拒绝：仍返回 EarlyRejected 作为 DroppedEnvelopes
            var emptyDecision = BuildEarlyRejectedResult(request, snapshot, earlyRejected, partition.RejectReasons);
            var earlyRejectedExecutionResult = _executionArtifactFactory.Create(
                request, requestSemanticHash, emptyDecision, completeWorkingSet,
                snapshot, routingDecisions, providerArtifacts);
            // EarlyRejected 候选作为 DroppedEnvelopes 物化到 ledger（P8 硬边界：所有 candidate 都写入）
            // await 等待 Learning Event 持久化到 outbox 表完成（与主决策路径一致）。
            // 捕获 LearningPersistenceStatus 并写入 ExecutionResult，让 Durable Failure 不再被静默吞掉。
            var earlyRejectedLearningStatus = await TriggerUtilityLedgerMaterializationAsync(earlyRejectedExecutionResult.Decision, request).ConfigureAwait(false);
            return earlyRejectedExecutionResult with { LearningPersistenceStatus = earlyRejectedLearningStatus };
        }

        // Step 7：FeaturePipeline — 特征计算
        // 用 Stopwatch 拆分 feature_ms（FeatureVector / FeatureBatch 构造耗时），记录到 IComponentHealthRegistry
        var featureSw = componentRegistry is not null ? Stopwatch.StartNew() : null;
        IReadOnlyList<ContextCandidateEnvelope> enriched;
        bool featureSucceeded = false;
        try
        {
            enriched = await _featurePipeline.EnrichAsync(
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
            featureSucceeded = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            featureSucceeded = false;
            throw;
        }
        finally
        {
            if (featureSw is not null)
            {
                featureSw.Stop();
                componentRegistry!.RecordComponentTime(
                    ComponentKind.Feature, featureSw.Elapsed.TotalMilliseconds, featureSucceeded,
                    componentScopeKey, cancellationToken);
            }
        }

        // Runtime 不再在 Engine 前执行 SafetyGate/LifecycleGate/UtilityScorer。
        // Engine 是唯一决策点（Safety → Lifecycle → Score → Allocate 全部在 Engine 内执行）。
        // Runtime 只保留 EarlyAdmissionGate + FeaturePipeline，然后把 enriched 候选传给 Engine。

        // Step 8：委托 IContextDecisionEngine 执行完整决策（Safety → Lifecycle → Score → Allocate）
        // 修复：Runtime 不再在 Engine 后二次 Allocate。
        // Engine 通过 PolicySnapshot 字段接收已解析的 snapshot，走 V2 路径。
        // 构建 AllocationContext 传给 Engine（AgentContext → FailClosed 默认）。
        var allocationContext = new AllocationContext
        {
            Purpose = request.Purpose,
            Budget = snapshot.Budget,
            MandatoryOverflowPolicy = ResolveMandatoryOverflowPolicy(request.Purpose),
            TokenizerVersion = null
        };
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
            // PackageInput.SectionRatios 作为 per-request override，
            // 优先于 snapshot.Budget.SectionRatios（与 ContextDecisionRequest.SectionRatios 语义一致：
            // 调用方显式值高于 Policy 默认值）。Retrieval/AgentContext 路径无 PackageInput，回退到 snapshot。
            SectionRatios = request.PackageInput?.SectionRatios is { Count: > 0 } pkgRatios
                ? pkgRatios
                : (snapshot.Budget.SectionRatios.Count > 0 ? snapshot.Budget.SectionRatios : null),
            PolicyBundleId = snapshot.Reference.BundleId,
            QueryText = request.QueryText,
            CreatedAt = DateTimeOffset.UtcNow,
            EnableModel = snapshot.Routing.EnableModelScoring,
            PolicySnapshot = snapshot,
            // 传 AllocationContext 给 Engine，让 Engine 在 V2 路径调用 Allocator 时使用
            AllocationContext = allocationContext,
            // 从 EffectivePolicySnapshot 读取 DiversityOptions 传给 Engine，
            // Engine 据此选择 V2.1 AllocateWithDiversity 或回退 V2.0 Allocate。
            DiversityOptions = snapshot.DiversityOptions
        };

        var engineResult = await DecideWithFailClosedPropagationAsync(
            decisionRequest, cancellationToken).ConfigureAwait(false);

        // Late Hydration — Engine 选出 SelectedEnvelopes 后，对 Selected IDs 批量 hydrate 正文。
        // 仅在 hydrator 注入且 SelectedEnvelopes 非空时调用；未注入时保持旧行为（Provider 已加载所有正文）。
        // hydrator 内部跳过已 hydrate 的 Material（Content 非空），避免重复 I/O；
        // 失败时降级为 no-op（Material.Content 保持空，Projector 降级为摘要），不阻塞主决策流。
        HydrationResult? hydrationResult = null;
        if (_selectedCandidateHydrator is not null && engineResult.SelectedEnvelopes.Count > 0)
        {
            // 传最终 token 预算做 hydrate 后二次预算修复；失败计数/超预算合并进 Outcome.Diagnostics
            hydrationResult = await _selectedCandidateHydrator.HydrateAsync(
                engineResult.SelectedEnvelopes, completeWorkingSet, decisionRequest.TokenBudget, cancellationToken).ConfigureAwait(false);
            completeWorkingSet = hydrationResult.WorkingSet;
        }

        // Rebuild ContextDecisionResult based on HydrationRepairDecision.
        // When hydrator returns Repair non-null, must rebuild (not just swap WorkingSet),
        // otherwise SelectedEnvelopes / AllocationDecisions / Outcome.SelectedCount /
        // EstimatedTokens become inconsistent with actual hydrated inputs.
        var repair = hydrationResult?.Repair;
        var rebuildSelected = engineResult.SelectedEnvelopes;
        var rebuildAllocationDecisions = engineResult.AllocationDecisions.Count > 0
            ? engineResult.AllocationDecisions
            : BuildAllocationDecisions(engineResult.SelectedEnvelopes, engineResult.DroppedEnvelopes);
        var rebuildEstimatedTokens = engineResult.Outcome.EstimatedTokens;
        var rebuildBudgetExceededCount = engineResult.Outcome.BudgetExceededCount;
        // 被 Hydration 或预算修复移出的候选（repair.HydrationDropped → DroppedEnvelopes）。
        IReadOnlyList<ContextCandidateEnvelope> hydrationDroppedEnvelopes = Array.Empty<ContextCandidateEnvelope>();
        if (repair is not null)
        {
            // fail-closed: mandatory/hard constraint hydration failure in AgentContext/Package must throw.
            // project_memory: AgentContext fail closed for mandatory/hard constraints,
            // best-effort for Retrieval, degrade with diagnostics for Package.
            // Here both AgentContext + Package fail-closed (mandatory content missing cannot degrade);
            // Retrieval uses best-effort.
            if (repair.HydrationFailures.Count > 0
                && (request.Purpose == ContextDecisionPurpose.AgentContext
                    || request.Purpose == ContextDecisionPurpose.Package))
            {
                var mandatoryHydrationFailures = new List<string>();
                foreach (var envelope in engineResult.SelectedEnvelopes)
                {
                    if ((envelope.Safety.IsMandatory || envelope.Safety.IsHardConstraint)
                        && repair.HydrationFailures.ContainsKey(envelope.CandidateId))
                    {
                        mandatoryHydrationFailures.Add(envelope.CandidateId);
                    }
                }
                if (mandatoryHydrationFailures.Count > 0)
                {
                    throw new MandatoryHydrationFailedException(mandatoryHydrationFailures, repair.HydrationFailures);
                }
            }

            // Remove HydrationDropped candidates from SelectedEnvelopes (retain only kept candidates)
            if (repair.HydrationDropped.Count > 0)
            {
                var droppedIdSet = new HashSet<string>(repair.HydrationDropped, StringComparer.Ordinal);
                var retained = new List<ContextCandidateEnvelope>(engineResult.SelectedEnvelopes.Count);
                foreach (var env in engineResult.SelectedEnvelopes)
                {
                    if (!droppedIdSet.Contains(env.CandidateId))
                    {
                        retained.Add(env);
                    }
                }
                rebuildSelected = retained;
            }

            // Replace AllocationDecisions with Repair's UpdatedAllocationDecisions
            // (reflects actual hydration results: retained = Selected, dropped = TokenBudgetExceeded)
            rebuildAllocationDecisions = repair.UpdatedAllocationDecisions;

            // Update EstimatedTokens with ExactTokenCount (recomputed from real content, not estimate)
            rebuildEstimatedTokens = repair.ExactTokenCount;

            // 被 Hydration 或预算修复移出的候选必须进入最终 DroppedEnvelopes——否则 DroppedCount 偏小、
            // Utility Ledger 不记录真实淘汰项、ConflictSet 缺样本。
            // 候选分区完整重建：Retained Selected（rebuildSelected）/ Hydration Failed Dropped /
            // Budget Repair Dropped / Engine Dropped / Early Admission Dropped。
            hydrationDroppedEnvelopes = BuildHydrationDroppedEnvelopes(engineResult.SelectedEnvelopes, repair);

            // BudgetExceededCount 重算：预算修复裁剪（HydrationDropped 中非 hydration 失败的候选）
            // 计入 budget 拦截；hydration 失败（EvidenceMissing）不计入 budget 拦截。
            var budgetTrimmedCount = 0;
            foreach (var droppedCandidateId in repair.HydrationDropped)
            {
                if (!repair.HydrationFailures.ContainsKey(droppedCandidateId)) budgetTrimmedCount++;
            }
            rebuildBudgetExceededCount += budgetTrimmedCount;
        }

        // Use Engine result directly, no second Allocate.
        // V2 path Engine already produced AllocationDecisions via IGlobalAllocator.
        // Legacy path: Engine does not produce AllocationDecisions, Runtime builds them.
        // If hydration occurred, rebuildAllocationDecisions already replaced by Repair.UpdatedAllocationDecisions.
        var allocationDecisions = rebuildAllocationDecisions;

        // Step 9：合并 EarlyRejected + Engine.DroppedEnvelopes（Blocker-6），
        // 再追加 Hydration/Budget Repair Dropped——重建完整候选分区。
        var finalDropped = earlyRejected.Count == 0
            ? engineResult.DroppedEnvelopes
            : CombineDroppedWithEarlyRejected(engineResult.DroppedEnvelopes, earlyRejected, partition.RejectReasons);
        if (hydrationDroppedEnvelopes.Count > 0)
        {
            finalDropped = AppendDroppedEnvelopes(finalDropped, hydrationDroppedEnvelopes);
        }

        // 合并 AllocationDecisions：补建 EarlyRejected 候选的 allocation decision
        var finalAllocationDecisions = earlyRejected.Count == 0
            ? allocationDecisions
            : AppendEarlyRejectedAllocationDecisions(allocationDecisions, earlyRejected);

        // 合并 EarlyRejected 后重构造 Outcome 时，复制 Engine Outcome.Diagnostics
        // 并添加 Runtime 级别 diagnostics（earlyAdmission.rejectedCount / provider.degraded），不丢失 Engine 诊断。
        // 仅在有 EarlyRejected 或 Provider degraded 时创建新字典（避免无谓分配）；否则直接复用 Engine Diagnostics 引用。
        IReadOnlyDictionary<string, string> mergedDiagnostics = engineResult.Outcome.Diagnostics;
        var hasEarlyRejected = earlyRejected.Count > 0;
        var hasProviderDegraded = providerReports.Count > 0 && providerReports.Any(r => !r.Succeeded);
        // hydrate 失败或预算修复后仍超预算时，合并 hydration 诊断
        var hasHydrationDiagnostics = hydrationResult is not null
            && (hydrationResult.FailedCount > 0 || hydrationResult.BudgetExceeded);
        if (hasEarlyRejected || hasProviderDegraded || hasHydrationDiagnostics)
        {
            var diag = new Dictionary<string, string>(engineResult.Outcome.Diagnostics, StringComparer.Ordinal);
            if (hasEarlyRejected)
            {
                diag["earlyAdmission.rejectedCount"] = earlyRejected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (hasProviderDegraded)
            {
                // Provider degraded 状态进入 Execution Artifact 诊断
                diag["provider.degraded"] = "true";
                var degradedKinds = providerReports
                    .Where(r => !r.Succeeded)
                    .Select(r => r.Kind.ToString())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal);
                diag["provider.degradedKinds"] = string.Join(",", degradedKinds);
            }
            if (hasHydrationDiagnostics)
            {
                // hydrate 计数 / 预算修复结果进入 Outcome.Diagnostics
                diag["hydration.hydratedCount"] = hydrationResult!.HydratedCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                diag["hydration.failedCount"] = hydrationResult.FailedCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (hydrationResult.BudgetExceeded)
                {
                    diag["hydration.budgetExceeded"] = "true";
                }
                if (hydrationResult.BudgetRepairDiagnostics is { Count: > 0 } repairDiagnostics)
                {
                    diag["hydration.budgetRepair"] = string.Join(";", repairDiagnostics);
                }
                // Record hydration failure details (candidate_id -> error) and dropped count.
                // Must be independent of BudgetRepairDiagnostics: hydration failures (store miss /
                // read exception) can occur without budget trimming, and droppedCount covers all drops.
                if (repair is not null && repair.HydrationFailures.Count > 0)
                {
                    diag["hydration.failures"] = string.Join("; ",
                        repair.HydrationFailures.Select(kv => kv.Key + "=" + kv.Value));
                    diag["hydration.droppedCount"] = repair.HydrationDropped.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            mergedDiagnostics = diag;
        }

        var decision = new ContextDecisionResult
        {
            RequestId = engineResult.RequestId,
            DecisionSource = engineResult.DecisionSource,
            // Use actual retained SelectedEnvelopes after hydration (dropped removed)
            SelectedEnvelopes = rebuildSelected,
            DroppedEnvelopes = finalDropped,
            // Outcome 重算（WP-E "结果真相"）：摘要必须是最终候选分区的纯函数——
            // SelectedCount / DroppedCount / Sections（repair 后）/ Diagnostics 全部从
            // 实际保留的 Selected/Dropped 分区派生，避免 Late Hydration 移出候选后仍沿用
            // Engine 旧计数。
            // 精确 token 总数以 Allocator 为准（V2.1 部分截断后 IncludedTokens 为真实
            // 纳入量；hydrate 后为 ExactTokenCount），作为覆盖值传入；Recompute 默认
            // 路径（无覆盖）供 replay / 审计等外部调用方从分区直接汇总。
            // Sections 仅在无 repair 时沿用 Engine 计算值（与旧行为一致），repair 后按分区重算。
            Outcome = DecisionOutcomeRecomputer.Recompute(
                rebuildSelected,
                finalDropped,
                tokenBudget: engineResult.Outcome.TokenBudget,
                safetyGateBlockedCount: engineResult.Outcome.SafetyGateBlockedCount,
                budgetExceededCount: rebuildBudgetExceededCount,
                diagnostics: mergedDiagnostics,
                exactEffectiveTokens: rebuildEstimatedTokens,
                sectionsOverride: repair is null ? engineResult.Outcome.Sections : null),
            PolicyVersion = engineResult.PolicyVersion,
            ModelVersion = engineResult.ModelVersion,
            DecidedAt = engineResult.DecidedAt,
            ModelEnabled = engineResult.ModelEnabled,
            Purpose = request.Purpose,
            RuntimeKind = ContextDecisionRuntimeKind.UnifiedV2,
            AllocationDecisions = finalAllocationDecisions,
            PolicyReference = snapshot.Reference
        };

        var mainExecutionResult = _executionArtifactFactory.Create(
            request, requestSemanticHash, decision, completeWorkingSet,
            snapshot, routingDecisions, providerArtifacts);
        // 主决策路径触发物化（SelectedEnvelopes + DroppedEnvelopes 全部写入 ledger）
        // await 等待 Learning Event 持久化到 outbox 表完成，防止进程退出导致 fire-and-forget 入队丢失数据。
        // 捕获 LearningPersistenceStatus 并写入 ExecutionResult，让 Durable Failure 不再被静默吞掉。
        var mainLearningStatus = await TriggerUtilityLedgerMaterializationAsync(mainExecutionResult.Decision, request).ConfigureAwait(false);
        return mainExecutionResult with { LearningPersistenceStatus = mainLearningStatus };
    }

    /// <summary>
    /// 触发 Utility Ledger 物化。
    /// </summary>
    /// <remarks>
    /// 设计原则：
    ///   1. 学习闭环物化不阻塞主决策流的<b>最终决策结果</b>——主决策已构建完成，
    ///      物化在后台异步执行（worker 池消费 outbox 表）。
    ///   2. 但调用方必须等待 Learning Event <b>入队到 outbox 表</b>完成（PostgreSQL INSERT），
    ///      否则进程退出/崩溃会导致 fire-and-forget 入队丢失数据。等待入队持久化，不等待 Materialize。
    ///   3. 物化失败不影响主决策正确性——dispatcher 内部捕获所有异常并降级（fallback direct materialize）。
    ///   4. 使用 CancellationToken.None——决策请求的取消不应中断物化（数据已生成，需写入 ledger）。
    ///   5. dispatcher 为 null 且 materializer 为 null 时（开发 / 测试路径未注入），直接跳过。
    /// </remarks>
    /// <para>
    /// 路径选择（消除热路径 Task.Run）：
    /// <list type="bullet">
    /// <item>
    /// <b>dispatcher 已注入（生产路径）</b>：通过 <see cref="LearningMaterializationDispatcher.EnqueueDurablyAsync"/>
    /// 入队并等待 PostgreSQL INSERT 完成。dispatcher 内部根据是否注册 <c>ILearningEventOutboxStore</c>
    /// 选择 Durable Outbox（Postgres）或 in-memory bounded Channel 路径，由固定 worker 池消费——
    /// 消除每请求 Task.Run / Task 风暴 / 进程崩溃静默丢数据等问题。
    /// </item>
    /// <item>
    /// <b>dispatcher 未注入但 materializer 已注入（兼容/测试路径）</b>：保留旧 Task.Run fire-and-forget
    /// 行为。此路径不是生产热路径，仅为未升级到 dispatcher 的旧测试保持向后兼容。
    /// </item>
    /// </list>
    /// </para>
    /// <param name="decision">决策结果（已构建完成，含 SelectedEnvelopes + DroppedEnvelopes）。</param>
    /// <param name="request">原始请求（用于提取 WorkspaceId / CollectionId）。</param>
    /// <returns>Learning Event 持久化状态（Persisted / Deferred / Failed），caller 写入 ExecutionResult。</returns>
    private async ValueTask<LearningPersistenceStatus> TriggerUtilityLedgerMaterializationAsync(
        ContextDecisionResult decision,
        ContextDecisionRuntimeRequest request)
    {
        var workspaceId = request.Scope.WorkspaceId;
        var collectionId = request.Scope.CollectionId;

        // 生产路径：dispatcher 已注入 → 通过 bounded Channel / Durable Outbox 入队（消除 Task.Run）。
        if (_materializationDispatcher is not null)
        {
            // await EnqueueDurablyAsync——等待 PostgreSQL durable append 完成（不等待后续 Materialize）。
            // 防止进程在 EnqueueAsync 完成 INSERT 前退出导致 Learning Event 丢失。
            // EnqueueDurablyAsync 失败时直接抛出异常（不 FallbackDirectMaterialize）——
            // 此处 try/catch 兜底以防影响主决策流；Learning Event 持久化失败通过返回 Failed 状态暴露给 caller。
            // 非关键路径（如后台导入）应改用 EnqueueBestEffortAsync 以保留 fallback 降级行为。
            // 注：本类未注入 ILogger——日志由 caller（observability layer）根据 Failed 状态统一记录。
            var learningStatus = LearningPersistenceStatus.Persisted;
            try
            {
                await _materializationDispatcher.EnqueueDurablyAsync(decision, workspaceId, collectionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // EnqueueDurablyAsync 不再内部降级——异常向上抛到此处的 catch。
                // 主决策流不应被 Learning Event 持久化失败中断；通过 LearningPersistenceStatus=Failed 暴露失败，
                // 让 caller 可观测（此前为静默吞掉的 P0 缺陷）。
                // 注：fallback direct materialize 已迁移到 EnqueueBestEffortAsync（非关键路径专用）。
                learningStatus = LearningPersistenceStatus.Failed;
            }
            return learningStatus;
        }

        // 兼容/测试路径：未注入 dispatcher 但注入了 materializer → 保留旧 Task.Run 行为。
        if (_utilityLedgerMaterializer is null)
        {
            return LearningPersistenceStatus.Deferred;
        }

        // 捕获 materializer 引用避免闭包捕获 this（防止潜在的对象生命周期问题）。
        var materializer = _utilityLedgerMaterializer;
        var decisionSnapshot = decision;

        // fire-and-forget：后台执行，主决策流不等待。返回 Deferred 表示未观测最终物化结果。
        _ = Task.Run(async () =>
        {
            try
            {
                await materializer.MaterializeAsync(decisionSnapshot, workspaceId, collectionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 物化失败不影响主决策流；学习闭环数据缺失由下游导出工具的完整性检查暴露。
                // 后续 WP-E-3 训练数据导出工具会检测 ledger 完整性并报警。
            }
        });

        return LearningPersistenceStatus.Deferred;
    }

    /// <summary>
    /// Provider DAG 两阶段执行。
    /// Phase 1：执行 Mandatory + Constraint + Lexical + Semantic + WorkingMemory + StableMemory
    /// Canonical Merge Phase 1 结果
    /// Phase 2：执行 Graph Provider，将 Phase 1 merged envelopes 作为 SeedCandidates 传入
    /// Final Merge：合并 Phase 1 + Phase 2 结果
    /// 当 <paramref name="providerFallbackActive" /> 为 true 时，按 ProviderKind 粒度逐个检查
    /// ShouldFallbackProvider — Semantic 慢不会导致 Graph 被跳过（仅跳过实际 Open 的 Provider）。
    /// </summary>
    /// <param name="providerFallbackActive">Provider 组件是否处于聚合回退激活态（来自 IComponentHealthRegistry）。</param>
    /// <param name="componentScopeKey">组件归因 scopeKey（用于 per-provider ShouldFallbackProvider 查询）。</param>
    private async Task<(IReadOnlyList<ExpertExecutionResult> Outputs, IReadOnlyList<ProviderExecutionReport> Reports)> InvokeEnabledProvidersWithDagAsync(
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        ExpertRoutingDecisionSet routingDecisions,
        CancellationToken cancellationToken,
        bool providerFallbackActive = false,
        string componentScopeKey = "")
    {
        if (_candidateProviders.Count == 0)
        {
            return (Array.Empty<ExpertExecutionResult>(), Array.Empty<ProviderExecutionReport>());
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

        // per-Provider 去重 — 按 ExpertKind 去重
        var executedKinds = new HashSet<ExpertKind>();
        var allEnabledProviders = _candidateProviders
            .Where(p => routingByExpert.Values.Any(r => MapExpertKindToRetrievalExpert(p.Kind) == r.Expert))
            .Where(p => executedKinds.Add(p.Kind))
            .ToList();

        // Provider 子组件回退 — 细化到 ProviderKind 粒度：
        //   - Semantic Provider：ShouldFallbackProvider(Semantic) 为 true 时跳过（依赖 Lexical 兜底）
        //   - Graph Provider：ShouldFallbackProvider(Graph) 为 true 时跳过（不进行关系扩展）
        //   - Mandatory / Constraint / Lexical / WorkingMemory / StableMemory：保留（关键路径，不可跳过）
        // 细化目的：Semantic 慢不会导致 Graph 被误关闭——两者独立熔断。
        // 注：provider 级回退需要 IRouter 配合（路由到 fallback provider）。
        //     此处实现为"跳过慢 provider"的最简形式；后续可扩展为切换到 fallback provider 实例。
        //     若 Semantic 被跳过且 Lexical 也未启用，结果候选可能不足 — 由 EarlyGate / Engine 兜底。
        if (providerFallbackActive && _componentHealthRegistry is not null)
        {
            allEnabledProviders = allEnabledProviders
                .Where(p => !ShouldSkipProvider(p.Kind, _componentHealthRegistry, componentScopeKey))
                .ToList();
        }

        if (allEnabledProviders.Count == 0)
        {
            return (Array.Empty<ExpertExecutionResult>(), Array.Empty<ProviderExecutionReport>());
        }

        // 拆分为两阶段
        // Phase 1：非 Graph Provider（Mandatory + Constraint + Lexical + Semantic + WorkingMemory + StableMemory）
        var phase1Providers = allEnabledProviders
            .Where(p => p.Kind != ExpertKind.Graph)
            .ToList();
        // Phase 2：Graph Provider（基于 Phase 1 结果做关系扩展）
        var phase2Providers = allEnabledProviders
            .Where(p => p.Kind == ExpertKind.Graph)
            .ToList();

        var allOutputs = new List<ExpertExecutionResult>();
        var allReports = new List<ProviderExecutionReport>();

        var adaptationContext = new CandidateAdaptationContext
        {
            WorkspaceId = request.Scope.WorkspaceId,
            CollectionId = request.Scope.CollectionId,
            RequestId = request.RequestId,
            QueryText = request.QueryText,
            ObservedAt = DateTimeOffset.UtcNow
        };

        // Phase 1：执行非 Graph Provider
        if (phase1Providers.Count > 0)
        {
            var phase1Results = await InvokeProviderBatchAsync(
                phase1Providers, request, snapshot, routingByExpert,
                adaptationContext, seedEnvelopes: null, cancellationToken,
                componentScopeKey).ConfigureAwait(false);
            allOutputs.AddRange(phase1Results.Outputs);
            allReports.AddRange(phase1Results.Reports);
        }

        // Phase 2：执行 Graph Provider，将 Phase 1 merged envelopes 作为 SeedCandidates
        if (phase2Providers.Count > 0)
        {
            // Canonical Merge Phase 1 结果
            IReadOnlyList<ContextCandidateEnvelope> phase1MergedEnvelopes;
            if (allOutputs.Count > 0)
            {
                var phase1WorkingSet = _canonicalMerger.Merge(allOutputs);
                phase1MergedEnvelopes = phase1WorkingSet.Envelopes;
            }
            else
            {
                phase1MergedEnvelopes = Array.Empty<ContextCandidateEnvelope>();
            }

            // 修复：Graph Phase 2 seeds = Phase 1 merged envelopes + 原始请求 seeds（去重）
            // 原始 seeds（RequiredIds / 外部注入）必须参与图扩展，否则 mandatory / 显式注入候选
            // 无法被 Graph Expert 用于关系遍历，导致扩展不完整。
            var originalSeeds = request.SeedWorkingSet?.Envelopes ?? request.SeedCandidates;
            var phase2Seeds = MergeSeedCandidates(phase1MergedEnvelopes, originalSeeds);

            // 用合并后的 seeds 构造 Phase 2 request
            var phase2Request = request with { SeedCandidates = phase2Seeds };

            var phase2Results = await InvokeProviderBatchAsync(
                phase2Providers, phase2Request, snapshot, routingByExpert,
                adaptationContext, seedEnvelopes: phase2Seeds, cancellationToken,
                componentScopeKey).ConfigureAwait(false);
            allOutputs.AddRange(phase2Results.Outputs);
            allReports.AddRange(phase2Results.Reports);
        }

        return (allOutputs, allReports);
    }

    /// <summary>
    /// 批量执行一组 Provider，bounded parallel + 超时保护 + 执行报告。
    /// 为每个 Provider 单独创建 timeout CTS（一个 Provider 超时不取消其他 Provider）。
    /// per-provider 耗时记录——每个 Provider 执行后通过 RecordProviderTime 上报到
    /// DefaultComponentHealthRegistry，细化到 ProviderKind 粒度（Semantic 慢不影响 Graph 熔断）。
    /// 专家故障等级：
    ///   - Mandatory / Constraint 失败或超时 → fail-closed（抛异常，整个请求失败）；
    ///   - Semantic / Graph / Recency 失败或超时 → degraded result（返回空结果 + diagnostic）；
    ///   - Lexical / WorkingMemory / StableMemory 失败或超时 → degraded result（默认）。
    /// </summary>
    /// <param name="cancellationToken">原始调用方 cancellationToken（用于区分超时 vs 用户取消）。</param>
    /// <param name="componentScopeKey">组件归因 scopeKey（用于 per-provider RecordProviderTime）。</param>
    private async Task<(IReadOnlyList<ExpertExecutionResult> Outputs, IReadOnlyList<ProviderExecutionReport> Reports)> InvokeProviderBatchAsync(
        IReadOnlyList<ICandidateProvider> providers,
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        IReadOnlyDictionary<RetrievalExpert, ExpertRoutingDecision> routingByExpert,
        CandidateAdaptationContext adaptationContext,
        IReadOnlyList<ContextCandidateEnvelope>? seedEnvelopes,
        CancellationToken cancellationToken,
        string componentScopeKey = "")
    {
        using var semaphore = new SemaphoreSlim(Math.Min(8, providers.Count));
        var tasks = providers.Select(async provider =>
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

            // 拆分 queue_ms 与 execution_ms —— 排队耗时只作诊断指标，不参与熔断判定，
            // 避免本地并发饱和时 Circuit Breaker 误判 Semantic/Graph Store 变慢。
            var queueStartedAt = Stopwatch.GetTimestamp();
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            var executionStartedAt = Stopwatch.GetTimestamp();
            var queueElapsed = Stopwatch.GetElapsedTime(queueStartedAt);

            // 为每个 Provider 单独创建 linked CTS with timeout。
            // 一个 Provider 超时只取消自身，不影响其他 Provider。
            using var perProviderCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            perProviderCts.CancelAfter(_providerTimeout);

            // per-provider 耗时记录变量（finally 块统一上报到 RecordProviderTime）
            // elapsed 仅含 execution（获取 semaphore 之后的真实调用时间），不含 queue wait。
            TimeSpan elapsed = TimeSpan.Zero;
            bool providerSucceeded = false;
            var shouldRecordTiming = false;

            try
            {
                var result = await provider.ExecuteAsync(context, perProviderCts.Token).ConfigureAwait(false);
                elapsed = Stopwatch.GetElapsedTime(executionStartedAt);
                providerSucceeded = true;
                shouldRecordTiming = true;
                var report = new ProviderExecutionReport
                {
                    Kind = expertKind,
                    Succeeded = true,
                    TimedOut = false,
                    Duration = elapsed,
                    CandidateCount = result.Envelopes.Count
                };
                return (result, report);
            }
            catch (OperationCanceledException) when (perProviderCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                elapsed = Stopwatch.GetElapsedTime(executionStartedAt);
                providerSucceeded = false;
                shouldRecordTiming = true;
                var emptyResult = CandidateProviderHelpers.Empty();
                var report = new ProviderExecutionReport
                {
                    Kind = expertKind,
                    Succeeded = false,
                    TimedOut = true,
                    Duration = elapsed,
                    CandidateCount = 0,
                    ErrorCode = "timeout"
                };

                // Mandatory / Constraint 超时 → fail-closed（抛异常，整个请求失败）
                if (expertKind == ExpertKind.Mandatory || expertKind == ExpertKind.Constraint)
                {
                    throw new InvalidOperationException(
                        $"Mandatory/Constraint provider '{expertKind}' timed out after {_providerTimeout}. " +
                        "Fail-closed: mandatory/constraint experts must not be silently degraded.",
                        new OperationCanceledException(perProviderCts.Token));
                }

                // 其他 Expert 超时 → degraded result（返回空结果 + diagnostic）
                return (emptyResult, report);
            }
            catch (OperationCanceledException)
            {
                // 用户取消（原始 cancellationToken 被取消）→ 传播，不记录 timing
                throw;
            }
            catch (Exception ex)
            {
                elapsed = Stopwatch.GetElapsedTime(executionStartedAt);
                providerSucceeded = false;
                shouldRecordTiming = true;
                var emptyResult = CandidateProviderHelpers.Empty();
                var report = new ProviderExecutionReport
                {
                    Kind = expertKind,
                    Succeeded = false,
                    TimedOut = false,
                    Duration = elapsed,
                    CandidateCount = 0,
                    ErrorCode = ex.GetType().Name
                };

                // Mandatory / Constraint 执行失败 → fail-closed（抛异常，整个请求失败）
                if (expertKind == ExpertKind.Mandatory || expertKind == ExpertKind.Constraint)
                {
                    throw new InvalidOperationException(
                        $"Mandatory/Constraint provider '{expertKind}' failed: {ex.GetType().Name}: {ex.Message}. " +
                        "Fail-closed: mandatory/constraint experts must not be silently degraded.", ex);
                }

                // 其他 Expert 失败 → degraded result（返回空结果 + diagnostic）
                return (emptyResult, report);
            }
            finally
            {
                // per-provider 耗时记录（细化到 ProviderKind 粒度）
                // 通过 cast 访问 DefaultComponentHealthRegistry 的新方法，不修改 IComponentHealthRegistry 接口契约。
                // 关键修复：只把 execution（不含 queue wait）上报到 Circuit Breaker，
                // queue_ms 单独走 CoreMetrics.ProviderQueueDuration 直方图（诊断用，不影响熔断）。
                if (shouldRecordTiming && _componentHealthRegistry is DefaultComponentHealthRegistry concreteRegistry)
                {
                    concreteRegistry.RecordProviderTime(
                        MapExpertKindToProviderKind(expertKind),
                        elapsed.TotalMilliseconds,
                        providerSucceeded,
                        componentScopeKey,
                        cancellationToken);
                }

                // queue_ms 与 execution_ms 单独上报到 OTel 直方图（诊断用）
                CoreMetrics.ProviderQueueDuration.Record(queueElapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("provider_kind", expertKind.ToString()));
                if (shouldRecordTiming)
                {
                    CoreMetrics.ProviderExecutionDuration.Record(elapsed.TotalMilliseconds,
                        new KeyValuePair<string, object?>("provider_kind", expertKind.ToString()));
                }

                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var outputs = results.Select(r => r.Item1).ToList();
        var reports = results.Select(r => r.Item2).ToList();
        return (outputs, reports);
    }

    /// <summary>
    /// 为被 Hydration 或预算修复移出的候选构建 DroppedEnvelopes。
    /// 分类：hydration 失败（缺少正文/证据）→ EvidenceMissing；预算修复裁剪 → TokenBudgetExceeded。
    /// 与 AllocationDecisions（repair.UpdatedAllocationDecisions）的 reason code 保持一致。
    /// 按 repair.HydrationDropped 顺序排列（确定性）。
    /// </summary>
    private static IReadOnlyList<ContextCandidateEnvelope> BuildHydrationDroppedEnvelopes(
        IReadOnlyList<ContextCandidateEnvelope> selectedEnvelopes,
        HydrationRepairDecision repair)
    {
        var droppedIdSet = new HashSet<string>(repair.HydrationDropped, StringComparer.Ordinal);
        var result = new List<ContextCandidateEnvelope>(repair.HydrationDropped.Count);
        foreach (var envelope in selectedEnvelopes)
        {
            if (!droppedIdSet.Contains(envelope.CandidateId)) continue;

            var isHydrationFailure = repair.HydrationFailures.ContainsKey(envelope.CandidateId);
            result.Add(envelope with
            {
                Safety = envelope.Safety with
                {
                    PassesSafetyGate = false,
                    BlockReasonCode = isHydrationFailure
                        ? CandidateDecisionReasonCode.EvidenceMissing
                        : CandidateDecisionReasonCode.TokenBudgetExceeded,
                    BlockReasonDetail = isHydrationFailure
                        ? "hydration failed: " + repair.HydrationFailures[envelope.CandidateId]
                        : "budget repair trimmed after hydration"
                }
            });
        }
        return result;
    }

    /// <summary>
    /// 将补充 dropped envelope 追加到基础集合，按 CanonicalKey 去重（候选只出现一次）。
    /// </summary>
    private static IReadOnlyList<ContextCandidateEnvelope> AppendDroppedEnvelopes(
        IReadOnlyList<ContextCandidateEnvelope> baseDropped,
        IReadOnlyList<ContextCandidateEnvelope> additions)
    {
        if (additions.Count == 0) return baseDropped;

        var combined = new List<ContextCandidateEnvelope>(baseDropped.Count + additions.Count);
        combined.AddRange(baseDropped);

        var existingKeys = new HashSet<CanonicalCandidateKey>(baseDropped.Select(e => e.CanonicalKey));
        foreach (var envelope in additions)
        {
            if (existingKeys.Add(envelope.CanonicalKey))
            {
                combined.Add(envelope);
            }
        }
        return combined;
    }

    /// <summary>
    /// 合并 Engine.DroppedEnvelopes + EarlyRejected 到最终 DroppedEnvelopes。
    /// EarlyRejected 候选携带 EarlyAdmissionRejected reason code（在 Safety.BlockReasonCode 字段）。
    /// </summary>
    private static IReadOnlyList<ContextCandidateEnvelope> CombineDroppedWithEarlyRejected(
        IReadOnlyList<ContextCandidateEnvelope> engineDropped,
        IReadOnlyList<ContextCandidateEnvelope> earlyRejected,
        IReadOnlyDictionary<CanonicalCandidateKey, string> rejectReasons)
    {
        if (earlyRejected.Count == 0) return engineDropped;

        var combined = new List<ContextCandidateEnvelope>(engineDropped.Count + earlyRejected.Count);
        combined.AddRange(engineDropped);

        var existingKeys = new HashSet<CanonicalCandidateKey>(engineDropped.Select(e => e.CanonicalKey));
        foreach (var envelope in earlyRejected)
        {
            if (existingKeys.Add(envelope.CanonicalKey))
            {
                // 标记 EarlyAdmissionRejected reason code（通过 Safety.BlockReasonCode）
                combined.Add(envelope with
                {
                    Safety = envelope.Safety with
                    {
                        PassesSafetyGate = false,
                        BlockReasonCode = CandidateDecisionReasonCode.EarlyAdmissionRejected,
                        BlockReasonDetail = rejectReasons.TryGetValue(envelope.CanonicalKey, out var reason)
                            ? $"early admission rejected: {reason}"
                            : "early admission rejected"
                    }
                });
            }
        }
        return combined;
    }

    /// <summary>
    /// 为 EarlyRejected 候选补建 AllocationDecision（reason=EarlyAdmissionRejected）。
    /// </summary>
    private static IReadOnlyList<CandidateAllocationDecision> AppendEarlyRejectedAllocationDecisions(
        IReadOnlyList<CandidateAllocationDecision> existing,
        IReadOnlyList<ContextCandidateEnvelope> earlyRejected)
    {
        if (earlyRejected.Count == 0) return existing;

        var combined = new List<CandidateAllocationDecision>(existing.Count + earlyRejected.Count);
        combined.AddRange(existing);

        var existingKeys = new HashSet<CanonicalCandidateKey>(existing.Select(d => d.CandidateKey));
        foreach (var envelope in earlyRejected)
        {
            if (existingKeys.Add(envelope.CanonicalKey))
            {
                combined.Add(new CandidateAllocationDecision
                {
                    CandidateKey = envelope.CanonicalKey,
                    Section = ResolveSectionForAllocation(envelope),
                    IncludedTokens = 0,
                    IsTruncated = false,
                    ReasonCode = CandidateDecisionReasonCode.EarlyAdmissionRejected
                });
            }
        }
        return combined;
    }

    /// <summary>
    /// 当所有候选被 EarlyGate 拒绝时，构建仅含 EarlyRejected 的 Decision。
    /// </summary>
    private static ContextDecisionResult BuildEarlyRejectedResult(
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        IReadOnlyList<ContextCandidateEnvelope> earlyRejected,
        IReadOnlyDictionary<CanonicalCandidateKey, string> rejectReasons)
    {
        var dropped = CombineDroppedWithEarlyRejected(
            engineDropped: Array.Empty<ContextCandidateEnvelope>(),
            earlyRejected: earlyRejected,
            rejectReasons: rejectReasons);

        var tokenBudget = request.TokenBudget > 0 ? request.TokenBudget : snapshot.Budget.DefaultTokenBudget;

        return new ContextDecisionResult
        {
            RequestId = request.RequestId,
            DecisionSource = ResolveDecisionSource(request.Purpose),
            SelectedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            DroppedEnvelopes = dropped,
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = 0,
                DroppedCount = dropped.Count,
                EstimatedTokens = 0,
                TokenBudget = tokenBudget,
                Sections = Array.Empty<string>(),
                SafetyGateBlockedCount = 0,
                BudgetExceededCount = 0
            },
            PolicyVersion = snapshot.FeatureSchemaVersion,
            ModelEnabled = false,
            DecidedAt = DateTimeOffset.UtcNow,
            Purpose = request.Purpose,
            RuntimeKind = ContextDecisionRuntimeKind.UnifiedV2,
            AllocationDecisions = AppendEarlyRejectedAllocationDecisions(
                Array.Empty<CandidateAllocationDecision>(), earlyRejected),
            PolicyReference = snapshot.Reference
        };
    }

    private ContextDecisionExecutionResult EmptyExecutionResult(
        ContextDecisionRuntimeRequest request,
        string requestSemanticHash,
        IReadOnlyList<ProviderExecutionArtifact> providerArtifacts,
        EffectivePolicySnapshot snapshot,
        ExpertRoutingDecisionSet routing,
        CandidateWorkingSet workingSet)
    {
        return _executionArtifactFactory.Create(
            request, requestSemanticHash, EmptyResult(request, snapshot), workingSet,
            snapshot, routing, providerArtifacts);
    }

    /// <summary>
    /// 从 expertOutputs + providerReports 构建 ProviderExecutionArtifact[]。
    /// </summary>
    /// <remarks>
    /// expertOutputs 与 providerReports 按相同顺序收集（InvokeEnabledProvidersWithDagAsync 产出），
    /// 按索引配对：Kind/Succeeded/Duration/StoreCallCount/ErrorCode 取自 report，
    /// Envelopes/Materials 取自 output。数量不匹配时按 output 为主，Kind 回退到 Semantic。
    /// </remarks>
    private static IReadOnlyList<ProviderExecutionArtifact> BuildProviderArtifacts(
        IReadOnlyList<ExpertExecutionResult> expertOutputs,
        IReadOnlyList<ProviderExecutionReport> providerReports)
    {
        if (expertOutputs.Count == 0)
        {
            return Array.Empty<ProviderExecutionArtifact>();
        }

        var artifacts = new List<ProviderExecutionArtifact>(expertOutputs.Count);
        for (var i = 0; i < expertOutputs.Count; i++)
        {
            var output = expertOutputs[i];
            // 按索引从 providerReports 取 Kind / Succeeded / Duration / StoreCallCount / ErrorCode（同序收集）
            ExpertKind kind;
            bool succeeded;
            TimeSpan duration;
            int storeCallCount;
            string? errorCode;
            if (i < providerReports.Count)
            {
                kind = providerReports[i].Kind;
                succeeded = providerReports[i].Succeeded;
                duration = providerReports[i].Duration;
                storeCallCount = providerReports[i].StoreCallCount;
                errorCode = providerReports[i].ErrorCode;
            }
            else
            {
                kind = ExpertKind.Semantic;
                succeeded = true;
                duration = TimeSpan.Zero;
                storeCallCount = 0;
                errorCode = null;
            }

            artifacts.Add(new ProviderExecutionArtifact
            {
                Kind = kind,
                Envelopes = output.Envelopes,
                Materials = output.Materials,
                Succeeded = succeeded,
                Duration = duration,
                StoreCallCount = storeCallCount,
                ErrorCode = errorCode
            });
        }
        return artifacts;
    }

    /// <summary>
    /// 合并 Provider 产出与 SeedCandidates。
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
    /// 合并 Provider 产出 Materials 与 SeedWorkingSet.Materials。
    /// 按 CanonicalCandidateKey 去重：Provider 产出优先（已包含完整 Material），
    /// SeedWorkingSet.Materials 仅补充 Provider 未产出的候选正文。
    /// </summary>
    private static IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> MergeSeedMaterials(
        IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> providerMaterials,
        IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial>? seedMaterials)
    {
        if (seedMaterials is null || seedMaterials.Count == 0) return providerMaterials;
        if (providerMaterials.Count == 0) return seedMaterials;

        // 复制 provider materials，再补充 seed 中 provider 未产出的 key
        var merged = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(providerMaterials);
        foreach (var (key, material) in seedMaterials)
        {
            // Provider 产出优先（已含 Material），不覆盖
            if (!merged.ContainsKey(key))
            {
                merged[key] = material;
            }
        }
        return merged;
    }

    /// <summary>
    /// 从 Engine 的 selected/dropped 输出构造 AllocationDecisions。
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

    // section 解析统一委托 DecisionOutcomeRecomputer.ResolveSection（WP-E 单一真相源）。
    private static string ResolveSectionForAllocation(ContextCandidateEnvelope envelope)
        => DecisionOutcomeRecomputer.ResolveSection(envelope);

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

    /// <summary>
    /// 将 ExpertKind 映射到 ProviderKind（用于 per-provider Circuit Breaker）。
    /// Mandatory / Constraint / Recency 映射到 Other（不参与 per-provider 熔断）。
    /// </summary>
    private static ProviderKind MapExpertKindToProviderKind(ExpertKind kind) => kind switch
    {
        ExpertKind.Semantic => ProviderKind.Semantic,
        ExpertKind.Graph => ProviderKind.Graph,
        ExpertKind.Lexical => ProviderKind.Lexical,
        ExpertKind.WorkingMemory => ProviderKind.WorkingMemory,
        ExpertKind.StableMemory => ProviderKind.StableMemory,
        _ => ProviderKind.Other
    };

    /// <summary>
    /// 查询单个 Provider 是否应被跳过（per-ProviderKind Circuit Breaker）。
    /// 仅 Semantic / Graph 可被跳过（Mandatory / Constraint / Lexical 等关键路径不可跳过）。
    /// 通过 cast 访问 DefaultComponentHealthRegistry.ShouldFallbackProvider，不修改接口契约。
    /// </summary>
    private static bool ShouldSkipProvider(
        ExpertKind kind,
        IComponentHealthRegistry? registry,
        string scopeKey)
    {
        // 仅 Semantic / Graph 可跳过；其他 Provider 为关键路径
        if (kind != ExpertKind.Semantic && kind != ExpertKind.Graph)
            return false;

        if (registry is not DefaultComponentHealthRegistry concreteRegistry)
            return false;

        return concreteRegistry.ShouldFallbackProvider(MapExpertKindToProviderKind(kind), scopeKey);
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

    /// <summary>
    /// 根据业务用途解析 MandatoryOverflow 默认策略。
    /// AgentContext → FailClosed（硬窗口，拒绝 mandatory 溢出）；
    /// Retrieval/Package → AllowOverflowWithDiagnostic（允许溢出但记录诊断）。
    /// </summary>
    private static MandatoryOverflowPolicy ResolveMandatoryOverflowPolicy(ContextDecisionPurpose purpose) => purpose switch
    {
        ContextDecisionPurpose.AgentContext => MandatoryOverflowPolicy.FailClosed,
        ContextDecisionPurpose.Retrieval or ContextDecisionPurpose.Package =>
            MandatoryOverflowPolicy.AllowOverflowWithDiagnostic,
        _ => MandatoryOverflowPolicy.AllowOverflowWithDiagnostic
    };

    /// <summary>
    /// 调用 Engine 并传播 fail-closed 异常。
    /// </summary>
    /// <remarks>
    /// 异常处理策略（遵循硬约束：Runtime 不捕获 OperationCanceledException）：
    ///   - <see cref="OperationCanceledException"/>：不捕获，向上传播（区分用户取消与超时）。
    ///   - <see cref="MandatoryContextWindowExceededException"/>：不捕获，向上传播（fail-closed 语义：
    ///     mandatory 硬窗口溢出必须让请求真正失败，不回退到 fallback）。
    ///   - 其他异常：结构化回退（带 tracing），返回空决策 + 诊断信息，
    ///     不让 Engine 内部错误导致整个 Runtime 崩溃。
    /// </remarks>
    private async Task<ContextDecisionResult> DecideWithFailClosedPropagationAsync(
        ContextDecisionRequest decisionRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _engine.DecideAsync(decisionRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 不捕获取消异常，向上传播
            throw;
        }
        catch (MandatoryContextWindowExceededException)
        {
            // mandatory 硬窗口溢出 → fail-closed，向上传播（不回退）
            throw;
        }
        catch (Exception ex)
        {
            // 其他异常 → 结构化回退（带 tracing），返回空决策 + 诊断
            return BuildEngineFallbackResult(decisionRequest, ex);
        }
    }

    /// <summary>
    /// Engine 异常时的结构化回退决策。
    /// </summary>
    /// <remarks>
    /// 不抛异常，返回空 SelectedEnvelopes + 诊断信息（engine.faulted=true / engine.error）。
    /// 让调用方能在诊断中看到 Engine 故障原因，而非收到未捕获异常。
    /// </remarks>
    private static ContextDecisionResult BuildEngineFallbackResult(
        ContextDecisionRequest decisionRequest,
        Exception ex)
    {
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["engine.faulted"] = "true",
            ["engine.error"] = ex.GetType().Name,
            ["engine.errorMessage"] = ex.Message
        };

        return new ContextDecisionResult
        {
            RequestId = decisionRequest.RequestId,
            DecisionSource = decisionRequest.DecisionSource,
            SelectedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            DroppedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = 0,
                DroppedCount = 0,
                EstimatedTokens = 0,
                TokenBudget = decisionRequest.TokenBudget,
                Sections = Array.Empty<string>(),
                SafetyGateBlockedCount = 0,
                BudgetExceededCount = 0,
                Diagnostics = diagnostics
            },
            PolicyVersion = decisionRequest.PolicySnapshot?.FeatureSchemaVersion ?? "unknown",
            ModelEnabled = false,
            DecidedAt = DateTimeOffset.UtcNow,
            Purpose = ResolvePurposeFromDecisionSource(decisionRequest.DecisionSource),
            RuntimeKind = ContextDecisionRuntimeKind.UnifiedV2,
            AllocationDecisions = Array.Empty<CandidateAllocationDecision>(),
            PolicyReference = decisionRequest.PolicySnapshot?.Reference
        };
    }

    private static ContextDecisionPurpose ResolvePurposeFromDecisionSource(ContextDecisionSource source) => source switch
    {
        ContextDecisionSource.Retrieval => ContextDecisionPurpose.Retrieval,
        ContextDecisionSource.Package => ContextDecisionPurpose.Package,
        _ => ContextDecisionPurpose.Package
    };
}

// ---------------------------------------------------------------------------
// DefaultRuntimeRequestNormalizer — R28-B.7-Final：请求标准化器默认实现
// ---------------------------------------------------------------------------

/// <summary>
/// Runtime 请求标准化器默认实现。
/// </summary>
/// <remarks>
/// 标准化规则：
///   1. RequestId：空时生成 GUID（保证 replay 可追溯）。
///   2. Scope：trim 空白；WorkspaceId/CollectionId 空时回退到 "default"。
///   3. TokenBudget：&lt;= 0 时填充默认 4096（与 BuildV2RetrievalRequest 一致）。
///   4. TopK：&lt;= 0 或 int.MaxValue 时保留（由后续 Policy/Router 解析默认值）。
///   5. QueryText：trim 空白（空字符串 → null，避免后续 Provider 误判）。
///   6. 专用 Input（RetrievalInput/PackageInput/AgentInput）：原样保留，不做转换。
/// 标准化后的请求不可变，贯穿整个请求生命周期。
/// </remarks>
public sealed class DefaultRuntimeRequestNormalizer : IRuntimeRequestNormalizer
{
    /// <summary>默认 token 预算（与 BuildV2RetrievalRequest / BuildV2PackageRequest 一致）。</summary>
    private const int DefaultTokenBudget = 4096;

    /// <summary>默认 workspace 回退值（Scope 空时使用）。</summary>
    private const string DefaultWorkspaceId = "default";

    /// <summary>默认 collection 回退值（Scope 空时使用）。</summary>
    private const string DefaultCollectionId = "default";

    /// <summary>单例实例（无状态，可共享）。</summary>
    public static readonly DefaultRuntimeRequestNormalizer Instance = new();

    /// <summary>标准化 Runtime 请求。</summary>
    public ContextDecisionRuntimeRequest Normalize(ContextDecisionRuntimeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId.Trim();

        var workspaceId = string.IsNullOrWhiteSpace(request.Scope.WorkspaceId)
            ? DefaultWorkspaceId
            : request.Scope.WorkspaceId.Trim();
        var collectionId = string.IsNullOrWhiteSpace(request.Scope.CollectionId)
            ? DefaultCollectionId
            : request.Scope.CollectionId.Trim();
        var scope = new ContextDecisionScope(workspaceId, collectionId);

        var tokenBudget = request.TokenBudget > 0
            ? request.TokenBudget
            : DefaultTokenBudget;

        // TopK <= 0 或 int.MaxValue 保留原值（由 Policy/Router 解析默认值，避免覆盖 Package 的 int.MaxValue 语义）
        var topK = request.TopK;

        // QueryText trim 空白（空字符串 → null）
        var queryText = string.IsNullOrWhiteSpace(request.QueryText)
            ? null
            : request.QueryText.Trim();

        // 如果所有字段都已标准化，直接返回原请求（避免无谓的 record 拷贝）
        if (ReferenceEquals(request.RequestId, requestId)
            && request.Scope.Equals(scope)
            && request.TokenBudget == tokenBudget
            && request.TopK == topK
            && ReferenceEquals(request.QueryText, queryText))
        {
            return request;
        }

        return request with
        {
            RequestId = requestId,
            Scope = scope,
            TokenBudget = tokenBudget,
            TopK = topK,
            QueryText = queryText
        };
    }
}

// ---------------------------------------------------------------------------
// DefaultRequestSemanticHasher — R28-B.7-Final：请求语义哈希器默认实现
// ---------------------------------------------------------------------------

/// <summary>
/// 请求语义哈希器默认实现。使用 SHA256 + invariant culture 计算稳定哈希。
/// </summary>
/// <remarks>
/// 哈希输入包含完整业务语义，而非仅浅层字段。
///   - 不含 RequestId（RequestId 是 CorrelationId，仅用于链路追踪，不代表业务语义）
///   - 含 Scope / Purpose / QueryText / TokenBudget / TopK
///   - 含 RetrievalInput 关键字段（RequiredIds/RequiredTags/RequiredTypes/QueryVector 哈希/Include* 开关等）
///   - 含 PackageInput 关键字段（Mode/Policy/IncludeRecent/IsAuditMode 等）
///   - 含 SeedCandidates 数量 + SeedWorkingSet 内容 digest（CanonicalKey/Material ContentHash/TokenizerVersion/PolicyReference）
/// 哈希跨进程/跨平台稳定（使用 invariant culture 格式化数值；无序集合排序后拼接；
/// QueryVector 直接哈希 IEEE-754 little-endian bytes，不经字符串中间表示）。
/// 优化：仅在 Replay/Experiment/Audit 模式计算完整 Hash；普通在线请求走轻量 fingerprint，
/// 跳过 QueryVector 哈希、SeedWorkingSet digest 等重型操作（每请求节省大量 StringBuilder/排序/SHA256 开销）。
/// </remarks>
public sealed class DefaultRequestSemanticHasher : IRequestSemanticHasher
{
    /// <summary>单例实例（无状态，可共享）。</summary>
    public static readonly DefaultRequestSemanticHasher Instance = new();

    /// <summary>计算请求的语义哈希（SHA256 hex）。</summary>
    public string ComputeHash(ContextDecisionRuntimeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 请求模式判断 —— 只有 Replay / Experiment / Audit 模式计算完整 Hash。
        // 普通在线请求计算轻量 Request Fingerprint，跳过 QueryVector 哈希、Seed 排序、
        // Material digest 等重型操作。Seed Material 在完整 Hash 路径中直接使用
        // CandidateMaterial.ContentHash（摄取阶段已计算），不重算 SHA256。
        if (RequiresFullSemanticHash(request))
        {
            return ComputeFullHash(request);
        }

        return ComputeLightweightFingerprint(request);
    }

    /// <summary>
    /// 判断是否需要计算完整语义哈希。
    /// 触发条件（任一即走完整路径）：
    ///   1. Replay / 测试 / 显式注入：SeedWorkingSet 非 null（携带完整 Envelopes + Materials）
    ///   2. Audit 模式：PackageInput.IsAuditMode == true
    ///   3. Experiment 场景：SeedCandidates 非空（外部种子注入，需可重放比对）
    /// 普通在线生产路径（无种子、非审计）走轻量 fingerprint。
    /// </summary>
    private static bool RequiresFullSemanticHash(ContextDecisionRuntimeRequest request)
    {
        if (request.SeedWorkingSet is not null) return true;
        if (request.PackageInput?.IsAuditMode == true) return true;
        if (request.SeedCandidates.Count > 0) return true;
        return false;
    }

    /// <summary>
    /// 轻量 Request Fingerprint —— 普通在线请求专用。
    /// 只覆盖核心标识字段（RequestId + Scope + Purpose + 基础预算），跳过：
    ///   - QueryVector 哈希（每向量分配 IncrementalHash + 逐 float 字节写入）
    ///   - 多组 JoinSorted（复制 + 排序 RequiredTags/Types/Ids/Refs/AllowedRelationTypes）
    ///   - SeedWorkingSet digest（envelopes 排序 + 逐 envelope 字段写入 SHA256 流）
    ///   - 大容量 StringBuilder（512+ 字符）一次性 SHA256
    /// RequestId 保证在线请求指纹唯一性（在线路径无需跨请求语义比对）。
    /// 跨 culture 不变（仅使用字符串与 int.ToString(InvariantCulture)）。
    /// </summary>
    private static string ComputeLightweightFingerprint(ContextDecisionRuntimeRequest request)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder(128);
        sb.Append("rid=").Append(request.RequestId);
        sb.Append("|scope=").Append(request.Scope.WorkspaceId).Append(':').Append(request.Scope.CollectionId);
        sb.Append("|purpose=").Append(((int)request.Purpose).ToString(inv));
        sb.Append("|query=").Append(request.QueryText ?? string.Empty);
        sb.Append("|budget=").Append(request.TokenBudget.ToString(inv));
        sb.Append("|topK=").Append(request.TopK.ToString(inv));

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// 完整语义哈希 —— Replay / Experiment / Audit 模式专用。
    /// 保留原有完整业务语义哈希逻辑，供离线 replay 匹配与审计比对使用。
    /// </summary>
    private static string ComputeFullHash(ContextDecisionRuntimeRequest request)
    {
        var sb = new StringBuilder(512);
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // --- 基础语义字段（不含 RequestId，RequestId 仅作 CorrelationId） ---
        sb.Append("scope=").Append(request.Scope.WorkspaceId).Append(':').Append(request.Scope.CollectionId);
        sb.Append("|purpose=").Append((int)request.Purpose);
        sb.Append("|query=").Append(request.QueryText ?? string.Empty);
        sb.Append("|budget=").Append(request.TokenBudget.ToString(inv));
        sb.Append("|topK=").Append(request.TopK.ToString(inv));

        // --- RetrievalInput 关键字段 ---
        if (request.RetrievalInput is { } ri)
        {
            sb.Append("|ri.tags=").Append(JoinSorted(ri.RequiredTags));
            sb.Append("|ri.types=").Append(JoinSorted(ri.RequiredTypes));
            sb.Append("|ri.ids=").Append(JoinSorted(ri.RequiredIds));
            sb.Append("|ri.refs=").Append(JoinSorted(ri.Refs));
            sb.Append("|ri.qv=").Append(HashFloats(ri.QueryVector));
            sb.Append("|ri.model=").Append(ri.ModelName ?? string.Empty);
            sb.Append("|ri.instr=").Append(ri.QueryInstruction ?? string.Empty);
            sb.Append("|ri.ctake=").Append(ri.CandidateTake.ToString(inv));
            sb.Append("|ri.vtopk=").Append(ri.VectorTopK.ToString(inv));
            sb.Append("|ri.minv=").Append(ri.MinVectorScore?.ToString(inv) ?? "null");
            sb.Append("|ri.rel=").Append(JoinSorted(ri.AllowedRelationTypes));
            sb.Append("|ri.rdepth=").Append(ri.RelationExpansionDepth.ToString(inv));
            sb.Append("|ri.ikw=").Append(ri.IncludeKeywordRecall);
            sb.Append("|ri.ivec=").Append(ri.IncludeVectorRecall);
            sb.Append("|ri.irel=").Append(ri.IncludeRelationExpansion);
            sb.Append("|ri.iwm=").Append(ri.IncludeWorkingMemory);
            sb.Append("|ri.ism=").Append(ri.IncludeStableMemory);
            sb.Append("|ri.icontent=").Append(ri.IncludeContent);
            sb.Append("|ri.plan=").Append(ri.Plan ?? string.Empty);
        }

        // --- PackageInput 关键字段 ---
        if (request.PackageInput is { } pi)
        {
            sb.Append("|pi.tags=").Append(JoinSorted(pi.RequiredTags));
            sb.Append("|pi.types=").Append(JoinSorted(pi.RequiredTypes));
            sb.Append("|pi.ids=").Append(JoinSorted(pi.RequiredIds));
            sb.Append("|pi.qv=").Append(HashFloats(pi.QueryVector));
            sb.Append("|pi.model=").Append(pi.ModelName ?? string.Empty);
            sb.Append("|pi.instr=").Append(pi.QueryInstruction ?? string.Empty);
            sb.Append("|pi.ctake=").Append(pi.CandidateTake.ToString(inv));
            sb.Append("|pi.vtopk=").Append(pi.VectorTopK.ToString(inv));
            sb.Append("|pi.minv=").Append(pi.MinVectorScore?.ToString(inv) ?? "null");
            sb.Append("|pi.mode=").Append((int)pi.Mode);
            sb.Append("|pi.policy=").Append(pi.Policy?.ToString() ?? "null");
            sb.Append("|pi.irecent=").Append(pi.IncludeRecent);
        }

        // --- SeedCandidates 数量 + SeedWorkingSet 内容 digest ---
        // 修复：原实现只写入 sws.envs / sws.mats 计数，不同 Seed 内容
        // 但数量相同的请求会得到同一 SemanticHash。现改为对规范化 SeedWorkingSet
        // 计算 digest，覆盖每个 seed 的 CanonicalKey / Material ContentHash /
        // TokenizerVersion / PolicyReference，按 CanonicalKey 排序后顺序 SHA256。
        // Material ContentHash 直接使用 CandidateMaterial.ContentHash（摄取阶段已计算），
        // 不在此处重算 SHA256。
        sb.Append("|seeds.count=").Append(request.SeedCandidates.Count);
        if (request.SeedWorkingSet is { } sws)
        {
            sb.Append("|sws.digest=").Append(HashSeedWorkingSet(sws));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string JoinSorted(IReadOnlyList<string> values)
    {
        if (values is null || values.Count == 0) return string.Empty;
        var arr = new string[values.Count];
        for (var i = 0; i < values.Count; i++) arr[i] = values[i];
        Array.Sort(arr, StringComparer.Ordinal);
        return string.Join(",", arr);
    }

    /// <summary>
    /// 修复：计算 SeedWorkingSet 的内容 digest。
    /// 按 CanonicalKey 排序后，将每个 seed 的 CanonicalKey / Material ContentHash /
    /// TokenizerVersion / PolicyReference 顺序写入 SHA256 流。不同 Seed 内容但数量相同
    /// 的请求不会得到同一 digest。集合顺序不影响结果（排序后哈希）。
    /// </summary>
    private static string HashSeedWorkingSet(CandidateWorkingSet sws)
    {
        var envelopes = sws.Envelopes;
        var materials = sws.Materials;

        if (envelopes is null || envelopes.Count == 0)
        {
            // 无 envelope：仅 material 字典内容参与（极少见路径，但仍需稳定）
            if (materials is null || materials.Count == 0) return "empty";
            return HashMaterialsOnly(materials);
        }

        // 按 CanonicalKey 排序（ordinal 字段依次比较），保证集合顺序不变性
        var sorted = envelopes.ToArray();
        Array.Sort(sorted, CompareEnvelopeByKey);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> epochBytes = stackalloc byte[8]; // ActivationEpoch (int64 LE)
        foreach (var env in sorted)
        {
            // CanonicalKey（5 字段）
            AppendUtf8(hasher, env.CanonicalKey.WorkspaceId);
            AppendUtf8(hasher, env.CanonicalKey.CollectionId);
            AppendUtf8(hasher, env.CanonicalKey.EntityKind);
            AppendUtf8(hasher, env.CanonicalKey.EntityId);
            AppendUtf8(hasher, env.CanonicalKey.EntityVersion);

            // Material ContentHash（按 CanonicalKey 在 sidecar 中查找；缺失则写空标记）
            if (materials is not null && materials.TryGetValue(env.CanonicalKey, out var mat))
            {
                AppendUtf8(hasher, mat.ContentHash ?? string.Empty);
            }
            else
            {
                AppendUtf8(hasher, "\0no-material");
            }

            // TokenizerVersion（来自 envelope.TokenCost?.TokenizerId）
            AppendUtf8(hasher, env.TokenCost?.TokenizerId ?? "\0no-tokenizer");

            // PolicyReference（BundleId / BundleVersion / BundleContentHash / ActivationEpoch）
            if (env.PolicyReference is { } pr)
            {
                AppendUtf8(hasher, pr.BundleId);
                AppendUtf8(hasher, pr.BundleVersion);
                AppendUtf8(hasher, pr.BundleContentHash);
                BinaryPrimitives.WriteInt64LittleEndian(epochBytes, pr.ActivationEpoch);
                hasher.AppendData(epochBytes);
            }
            else
            {
                AppendUtf8(hasher, "\0no-policy");
            }

            // 字段间分隔符避免拼接歧义
            hasher.AppendData([(byte)'|']);
        }

        var hash = hasher.GetHashAndReset();
        return Convert.ToHexString(hash).AsSpan(0, 16).ToString(); // 取前 16 hex 字符作摘要
    }

    /// <summary>无 envelope 但 material 非空的极少见路径：按 key 排序后哈希 ContentHash。</summary>
    private static string HashMaterialsOnly(IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> materials)
    {
        var pairs = materials.ToArray();
        Array.Sort(pairs, (a, b) => CompareKey(a.Key, b.Key));

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var kv in pairs)
        {
            AppendUtf8(hasher, kv.Key.WorkspaceId);
            AppendUtf8(hasher, kv.Key.CollectionId);
            AppendUtf8(hasher, kv.Key.EntityKind);
            AppendUtf8(hasher, kv.Key.EntityId);
            AppendUtf8(hasher, kv.Key.EntityVersion);
            AppendUtf8(hasher, kv.Value.ContentHash ?? string.Empty);
            hasher.AppendData([(byte)'|']);
        }

        var hash = hasher.GetHashAndReset();
        return Convert.ToHexString(hash).AsSpan(0, 16).ToString();
    }

    /// <summary>将字符串以 UTF-8 字节追加到 IncrementalHash；短串走 stackalloc 避免分配。</summary>
    private static void AppendUtf8(IncrementalHash hasher, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount <= 256)
        {
            Span<byte> buf = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(value, buf);
            hasher.AppendData(buf);
        }
        else
        {
            hasher.AppendData(Encoding.UTF8.GetBytes(value));
        }
    }

    private static int CompareEnvelopeByKey(ContextCandidateEnvelope a, ContextCandidateEnvelope b)
        => CompareKey(a.CanonicalKey, b.CanonicalKey);

    private static int CompareKey(CanonicalCandidateKey a, CanonicalCandidateKey b)
    {
        var c = string.CompareOrdinal(a.WorkspaceId, b.WorkspaceId);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.CollectionId, b.CollectionId);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.EntityKind, b.EntityKind);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.EntityId, b.EntityId);
        if (c != 0) return c;
        return string.CompareOrdinal(a.EntityVersion, b.EntityVersion);
    }

    private static string HashFloats(IReadOnlyList<float> values)
    {
        if (values is null || values.Count == 0) return "empty";
        // 修复：直接哈希 IEEE-754 little-endian bytes，避免逐 float ToString("R")
        // 造成的字符串分配与 StringBuilder 开销。BinaryPrimitives.WriteSingleLittleEndian
        // 保证跨平台字节序稳定（不依赖 BitConverter.IsLittleEndian）。
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> floatBytes = stackalloc byte[4];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(floatBytes, values[i]);
            hasher.AppendData(floatBytes);
        }
        var hash = hasher.GetHashAndReset();
        return Convert.ToHexString(hash).AsSpan(0, 16).ToString(); // 取前 16 hex 字符作摘要
    }
}

// ---------------------------------------------------------------------------
// DefaultExecutionArtifactFactory — R28-B.7-Final：Execution Artifact 工厂默认实现
// ---------------------------------------------------------------------------

/// <summary>
/// Execution Artifact 工厂默认实现。
/// 从 <see cref="ProviderExecutionArtifact"/>[] 构建完整 <see cref="ContextDecisionExecutionResult"/>。
/// </summary>
/// <remarks>
/// 替代旧的 internal static ExecutionArtifactFactory，改为可注入的 IExecutionArtifactFactory 实现。
/// 从单一数据源（ProviderExecutionArtifact[]）构建 ProviderReports 与 ProviderOutputSnapshots，
/// 让 Runtime 不再分散处理 expertOutputs + providerReports 的配对逻辑。
/// </remarks>
public sealed class DefaultExecutionArtifactFactory : IExecutionArtifactFactory
{
    /// <summary>当前 Allocator 版本（与 DefaultAllocatorV2_1 诊断字段保持一致）。</summary>
    private const string AllocatorVersion = "V2.1";

    /// <summary>单例实例（无状态，可共享）。</summary>
    public static readonly DefaultExecutionArtifactFactory Instance = new();

    /// <summary>创建完整填充的 <see cref="ContextDecisionExecutionResult"/>。</summary>
    public ContextDecisionExecutionResult Create(
        ContextDecisionRuntimeRequest normalizedRequest,
        string requestSemanticHash,
        ContextDecisionResult decision,
        CandidateWorkingSet workingSet,
        EffectivePolicySnapshot policy,
        ExpertRoutingDecisionSet routing,
        IReadOnlyList<ProviderExecutionArtifact> providerArtifacts)
    {
        ArgumentNullException.ThrowIfNull(normalizedRequest);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(workingSet);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(providerArtifacts);

        // 计算最终 Artifact token 成本（含 section 明细 + 分隔符）。
        // Allocator 与 Projector 使用同一 tokenizer 版本（TokenizerId），确保 token 计数一致。
        var finalTokenCost = TokenCostHelper.ComputeFinalArtifactTokenCost(decision, workingSet);

        return new ContextDecisionExecutionResult
        {
            Decision = decision,
            WorkingSet = workingSet,
            Policy = policy,
            Routing = routing,
            // 从 ProviderExecutionArtifact[] 构建 ProviderReports（向后兼容字段）
            ProviderReports = BuildProviderReports(providerArtifacts),
            // 标准化后的请求
            NormalizedRequest = normalizedRequest,
            // 请求语义哈希（由 IRequestSemanticHasher 计算，用于 replay 匹配）
            RequestSemanticHash = requestSemanticHash,
            // 请求作用域（从标准化请求直接获取，不从候选反推）
            Scope = normalizedRequest.Scope,
            // Feature Schema 版本（从 Policy 获取）
            FeatureSchemaVersion = policy.FeatureSchemaVersion,
            // Allocator 版本（用于 replay 兼容性）
            AllocatorVersion = AllocatorVersion,
            // Tokenizer 版本从 FinalTokenCost.TokenizerId 获取，
            // 确保 TokenizerVersion 与实际 token 计算使用的 tokenizer 一致（可追溯）。
            TokenizerVersion = finalTokenCost.TokenizerId,
            // Provider 输出快照（用于 replay 和审计）
            ProviderOutputSnapshots = BuildProviderOutputSnapshots(providerArtifacts),
            // 最终序列化 token 成本（含 section content + separator + header）
            FinalTokenCost = finalTokenCost,
            // 任一 Provider degraded 时标记 IsDegraded=true
            IsDegraded = providerArtifacts.Count > 0 && providerArtifacts.Any(p => !p.Succeeded)
        };
    }

    /// <summary>从 ProviderExecutionArtifact[] 构建 ProviderExecutionReport 列表。</summary>
    private static IReadOnlyList<ProviderExecutionReport> BuildProviderReports(
        IReadOnlyList<ProviderExecutionArtifact> artifacts)
    {
        if (artifacts.Count == 0) return Array.Empty<ProviderExecutionReport>();

        var reports = new List<ProviderExecutionReport>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            reports.Add(new ProviderExecutionReport
            {
                Kind = artifact.Kind,
                Succeeded = artifact.Succeeded,
                TimedOut = !artifact.Succeeded && artifact.ErrorCode == "timeout",
                Duration = artifact.Duration,
                CandidateCount = artifact.Envelopes.Count,
                StoreCallCount = artifact.StoreCallCount,
                ErrorCode = artifact.ErrorCode
            });
        }
        return reports;
    }

    /// <summary>从 ProviderExecutionArtifact[] 构建 ProviderOutputSnapshot 列表。</summary>
    private static IReadOnlyList<ProviderOutputSnapshot> BuildProviderOutputSnapshots(
        IReadOnlyList<ProviderExecutionArtifact> artifacts)
    {
        if (artifacts.Count == 0) return Array.Empty<ProviderOutputSnapshot>();

        var snapshots = new List<ProviderOutputSnapshot>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            snapshots.Add(new ProviderOutputSnapshot
            {
                Kind = artifact.Kind,
                Envelopes = artifact.Envelopes,
                Materials = artifact.Materials,
                Succeeded = artifact.Succeeded,
                Duration = artifact.Duration
            });
        }
        return snapshots;
    }
}

// ---------------------------------------------------------------------------
// ExecutionArtifactFactory — 执行结果工厂（保留向后兼容）
// ---------------------------------------------------------------------------

/// <summary>
/// 执行结果工厂，统一填充 <see cref="ContextDecisionExecutionResult"/> 的所有字段。
/// </summary>
/// <remarks>
/// 设计目标：
///   - 统一 Runtime 所有返回点（正常路径 / EarlyRejected 空路径 / 完全空候选路径）的结果构造，
///     避免 P0-1 问题（新字段 NormalizedRequest / RequestSemanticHash / Scope /
///     FeatureSchemaVersion / AllocatorVersion / TokenizerVersion / ProviderOutputSnapshots 未填充）。
///   - ProviderOutputSnapshots 从 expertOutputs 构建，用于 Shadow replay 与审计。
///   - RequestSemanticHash 基于请求语义字段计算 SHA256，用于 replay 匹配。
/// </remarks>
internal static class ExecutionArtifactFactory
{
    /// <summary>当前 Allocator 版本（与 DefaultAllocatorV2_1 诊断字段保持一致）。</summary>
    private const string AllocatorVersion = "V2.1";

    /// <summary>Tokenizer 版本占位（无 IContextTokenizerResolver 注入时为 null）。</summary>
    private const string? TokenizerVersionValue = null;

    /// <summary>
    /// 创建完整填充的 <see cref="ContextDecisionExecutionResult"/>。
    /// </summary>
    public static ContextDecisionExecutionResult Create(
        ContextDecisionRuntimeRequest request,
        ContextDecisionResult decision,
        CandidateWorkingSet workingSet,
        EffectivePolicySnapshot snapshot,
        ExpertRoutingDecisionSet routing,
        IReadOnlyList<ProviderExecutionReport> providerReports,
        IReadOnlyList<ExpertExecutionResult>? expertOutputs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(workingSet);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(routing);

        return new ContextDecisionExecutionResult
        {
            Decision = decision,
            WorkingSet = workingSet,
            Policy = snapshot,
            Routing = routing,
            ProviderReports = providerReports,
            // 标准化请求（当前为 identity，后续接入 PurposeRequestNormalizer）
            NormalizedRequest = request,
            // 请求语义哈希（用于 replay 匹配）
            RequestSemanticHash = ComputeRequestSemanticHash(request),
            // 请求作用域（从请求直接获取，不从候选反推）
            Scope = request.Scope,
            // Feature Schema 版本（从 Policy 获取）
            FeatureSchemaVersion = snapshot.FeatureSchemaVersion,
            // Allocator 版本（用于 replay 兼容性）
            AllocatorVersion = AllocatorVersion,
            // Tokenizer 版本（无 resolver 注入时为 null）
            TokenizerVersion = TokenizerVersionValue,
            // Provider 输出快照（用于 replay 和审计）
            ProviderOutputSnapshots = BuildProviderOutputSnapshots(expertOutputs, providerReports),
            // 统一 Token Ledger — 从 AllocationDecisions + WorkingSet 精确计算
            FinalTokenCost = TokenCostHelper.ComputeFinalArtifactTokenCost(decision, workingSet),
            // Provider degraded 标记（任一 Provider 失败时为 true）
            IsDegraded = providerReports.Count > 0 && providerReports.Any(r => !r.Succeeded)
        };
    }

    /// <summary>
    /// 计算请求语义哈希（SHA256，用于 replay 匹配）。
    /// </summary>
    /// <remarks>
    /// 委托给 DefaultRequestSemanticHasher，消除重复实现。
    /// 哈希输入包含完整业务语义（Scope/Purpose/QueryText/TokenBudget/TopK +
    /// RetrievalInput/PackageInput 关键字段 + SeedCandidates 数量），
    /// 不含 RequestId（RequestId 仅作 CorrelationId）。
    /// </remarks>
    private static string ComputeRequestSemanticHash(ContextDecisionRuntimeRequest request)
    {
        return DefaultRequestSemanticHasher.Instance.ComputeHash(request);
    }

    /// <summary>
    /// 从 ExpertExecutionResult + ProviderExecutionReport 构建 ProviderOutputSnapshot 列表。
    /// </summary>
    /// <remarks>
    /// expertOutputs 与 providerReports 按相同顺序收集（InvokeProviderBatchAsync 产出），
    /// 按索引配对：Kind/Succeeded/Duration 取自 report，Envelopes/Materials 取自 output。
    /// 数量不匹配时按 output 为主，Kind 回退到 Semantic。
    /// </remarks>
    private static IReadOnlyList<ProviderOutputSnapshot> BuildProviderOutputSnapshots(
        IReadOnlyList<ExpertExecutionResult>? expertOutputs,
        IReadOnlyList<ProviderExecutionReport> providerReports)
    {
        if (expertOutputs is null || expertOutputs.Count == 0)
        {
            return Array.Empty<ProviderOutputSnapshot>();
        }

        var snapshots = new List<ProviderOutputSnapshot>(expertOutputs.Count);
        for (var i = 0; i < expertOutputs.Count; i++)
        {
            var output = expertOutputs[i];
            // 按索引从 providerReports 取 Kind / Succeeded / Duration / ErrorCode（同序收集）
            var (kind, succeeded, duration, errorCode) = i < providerReports.Count
                ? (providerReports[i].Kind, providerReports[i].Succeeded, providerReports[i].Duration, providerReports[i].ErrorCode)
                : (ExpertKind.Semantic, true, TimeSpan.Zero, (string?)null);

            snapshots.Add(new ProviderOutputSnapshot
            {
                Kind = kind,
                Envelopes = output.Envelopes,
                Materials = output.Materials,
                Succeeded = succeeded,
                Duration = duration,
                // 传播错误码到快照（用于 replay 诊断 degraded 原因）
                ErrorCode = errorCode
            });
        }
        return snapshots;
    }
}

// ---------------------------------------------------------------------------
// TokenCostHelper — token 成本计算辅助
// ---------------------------------------------------------------------------

/// <summary>
/// token 成本计算辅助，提供精确 token 计数（优先使用 tokenizer）。
/// </summary>
/// <remarks>
/// 解决问题：EstimatedTokens 使用 length/4 粗略估算，对中文 / JSON / 代码偏差大
/// （中文每字符约 1 token，length/4 低估 4 倍）。本 helper 在 Provider 召回时
/// 通过 IContextTokenizerResolver 获取精确 token 数；不可用时回退到 length/4 估算。
/// </remarks>
internal static class TokenCostHelper
{
    /// <summary>粗略估算的 tokenizer 标识。</summary>
    private const string EstimatedTokenizerId = "length-div-4";

    /// <summary>section 分隔符（用于 Projector 重算总 token 时附加分隔符 token）。</summary>
    public const string SectionSeparator = "\n---\n";

    /// <summary>
    /// 计算候选正文的 token 成本。
    /// </summary>
    /// <param name="content">候选正文（null 或空时返回 0 token）。</param>
    /// <param name="tokenizerResolver">tokenizer 解析器（null 时回退到 length/4 估算）。</param>
    /// <param name="modelName">tokenizer 使用的模型名（可选）。</param>
    /// <returns>token 成本（精确或估算）。</returns>
    public static CandidateTokenCost ComputeTokenCost(
        string? content,
        IContextTokenizerResolver? tokenizerResolver,
        string? modelName = null)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new CandidateTokenCost
            {
                ContentTokens = 0,
                TokenizerId = tokenizerResolver is not null ? (modelName ?? "default") : EstimatedTokenizerId,
                IsEstimated = tokenizerResolver is null
            };
        }

        // 优先使用 tokenizer 精确计数
        if (tokenizerResolver is not null)
        {
            var estimate = tokenizerResolver.Estimate(content, modelName);
            return new CandidateTokenCost
            {
                ContentTokens = estimate.TokenCount,
                TokenizerId = modelName ?? estimate.Source ?? "default",
                IsEstimated = estimate.IsFallback
            };
        }

        // 回退到 length/4 估算（对中文偏低，但对英文/拉丁文近似）
        var estimatedTokens = Math.Max(1, content!.Length / 4);
        return new CandidateTokenCost
        {
            ContentTokens = estimatedTokens,
            TokenizerId = EstimatedTokenizerId,
            IsEstimated = true
        };
    }

    /// <summary>
    /// 计算包含 section 分隔符的总 token 数。
    /// </summary>
    /// <param name="candidateTokenSum">所有候选 token 之和（不含分隔符）。</param>
    /// <param name="candidateCount">候选数量。</param>
    /// <param name="separatorTokens">单个分隔符的 token 数（默认 3，约 "\n---\n"）。</param>
    /// <returns>含分隔符的总 token 数。</returns>
    public static int CountWithSeparators(int candidateTokenSum, int candidateCount, int separatorTokens = 3)
    {
        if (candidateCount <= 1) return candidateTokenSum;
        return candidateTokenSum + separatorTokens * (candidateCount - 1);
    }

    /// <summary>
    /// 计算最终 Artifact 的统一 Token Ledger（CandidateTokenCost → SectionTokenCost → FinalArtifactTokenCost）。
    /// </summary>
    /// <param name="decision">决策结果（含 AllocationDecisions + Outcome.TokenBudget）。</param>
    /// <param name="workingSet">候选工作集（含 Materials，用于精确 token 计算）。</param>
    /// <param name="tokenizerResolver">tokenizer 解析器（null 时使用 AllocationDecisions.IncludedTokens）。</param>
    /// <param name="modelName">tokenizer 使用的模型名（可选）。</param>
    /// <returns>最终 Artifact token 成本（含 section 明细 + 总 token + 预算状态）。</returns>
    /// <remarks>
    /// Allocator 与 Projector 必须使用同一 tokenizer 版本（TokenizerId）以保证一致性。
    /// 当 tokenizerResolver 可用时，从 Materials 正文精确计算 content tokens；
    /// 否则回退到 AllocationDecisions.IncludedTokens（Allocator 预估值）。
    /// 分隔符 token = 2/候选间（"\n\n"），与 PackageResultProjector 一致。
    /// </remarks>
    public static FinalArtifactTokenCost ComputeFinalArtifactTokenCost(
        ContextDecisionResult decision,
        CandidateWorkingSet workingSet,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(workingSet);

        // 按 Section 分组 AllocationDecisions
        var sectionGroups = decision.AllocationDecisions
            .GroupBy(d => d.Section, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var sections = new List<SectionTokenCost>(sectionGroups.Count);
        var totalTokens = 0;
        var tokenizerId = EstimatedTokenizerId;

        foreach (var group in sectionGroups)
        {
            var allocations = group.ToList();
            var contentTokens = 0;
            // 分隔符 token：2/候选间（"\n\n"），与 PackageResultProjector 一致
            var separatorTokens = 2 * Math.Max(0, allocations.Count - 1);
            // section 名为元数据，不计入内容 token
            var headerTokens = 0;

            if (tokenizerResolver is not null)
            {
                // 精确模式：从 Materials 提取正文，使用 tokenizer 精确计数
                tokenizerId = modelName ?? "default";
                foreach (var alloc in allocations)
                {
                    if (workingSet.Materials.TryGetValue(alloc.CandidateKey, out var material))
                    {
                        var cost = ComputeTokenCost(material.Content, tokenizerResolver, modelName);
                        if (!cost.IsEstimated) tokenizerId = cost.TokenizerId;
                        contentTokens += cost.ContentTokens;
                    }
                    else
                    {
                        // Material 缺失时回退到 IncludedTokens
                        contentTokens += alloc.IncludedTokens;
                    }
                }
            }
            else
            {
                // 估算模式：使用 Allocator 的 IncludedTokens（与 Projector 消费同一数据源）
                tokenizerId = "allocator-included-tokens";
                contentTokens = allocations.Sum(d => d.IncludedTokens);
            }

            sections.Add(new SectionTokenCost
            {
                Section = group.Key,
                ContentTokens = contentTokens,
                SeparatorTokens = separatorTokens,
                HeaderTokens = headerTokens
            });
            totalTokens += contentTokens + separatorTokens + headerTokens;
        }

        // 无 AllocationDecisions 时（如 Retrieval 路径或空决策）：使用 Outcome.EstimatedTokens 作为总 token
        if (sections.Count == 0 && decision.Outcome.EstimatedTokens > 0)
        {
            sections.Add(new SectionTokenCost
            {
                Section = "default",
                ContentTokens = decision.Outcome.EstimatedTokens,
                SeparatorTokens = 0,
                HeaderTokens = 0
            });
            totalTokens = decision.Outcome.EstimatedTokens;
        }

        var budgetLimit = decision.Outcome.TokenBudget;
        return new FinalArtifactTokenCost
        {
            Sections = sections,
            TotalTokens = totalTokens,
            TokenizerId = tokenizerId,
            WithinBudget = budgetLimit <= 0 || totalTokens <= budgetLimit,
            BudgetLimit = budgetLimit
        };
    }
}

// ---------------------------------------------------------------------------
// DefaultResolvedPolicyProvider
// ---------------------------------------------------------------------------

/// <summary>
/// 策略快照提供者默认骨架。返回基于全局默认 bundle 的不可变快照。
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
            ResolutionScope = request.Scope,
            // 默认启用 V2.1 diversity 路径（Lambda=0.5, SectionReserveRatio=0.1）。
            // Engine 仅在 IAllocatorV2_1 注入时才走 AllocateWithDiversity；否则回退 V2.0。
            DiversityOptions = new DiversityOptions()
        };

        return ValueTask.FromResult(snapshot);
    }
}

// ---------------------------------------------------------------------------
// PostgresResolvedPolicyProvider（接入 IPolicyRegistry 的真实策略解析）
// ---------------------------------------------------------------------------

/// <summary>
/// 基于 <see cref="IPolicyRegistry"/> 的策略快照提供者。
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
            // 使用 uncached hash，确保 tampered bundle 不会被缓存的旧哈希掩盖。
            // ComputeHash 按 (BundleId, Version) 缓存，假设 bundle 内容不可变；
            // 若 bundle 被篡改（内容修改但 BundleId/Version 不变），缓存会返回旧哈希掩盖篡改。
            // 此处使用 ComputeHashUncached 绕过缓存，每次重新计算 SHA256，保证验证的严肃性。
            var computedHash = PolicyBundleHasher.ComputeHashUncached(bundle);
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
            ResolutionScope = request.Scope,
            // 默认启用 V2.1 diversity 路径（Lambda=0.5, SectionReserveRatio=0.1）。
            // Engine 仅在 IAllocatorV2_1 注入时才走 AllocateWithDiversity；否则回退 V2.0。
            DiversityOptions = new DiversityOptions()
        };

        return snapshot;
    }

    // -----------------------------------------------------------------------
    // 受限 override 合并（与 DefaultContextDecisionEngine.ApplyBudgetOverride 对齐）
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
    // 验证
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
// PolicyBundleHasher（bundle 内容哈希计算，用于 immutability 验证）
// ---------------------------------------------------------------------------

/// <summary>
/// 策略包内容哈希计算器。
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
    // bundle 不可变，按 (BundleId, Version) 缓存哈希结果，避免每次请求重复 SHA256 + StringBuilder
    private static readonly ConcurrentDictionary<(string BundleId, string Version), string> _hashCache = new();

    /// <summary>计算 bundle 的内容哈希（SHA256 前 16 字符，前缀 "sha256:"）。
    /// bundle 不可变，结果按 (BundleId, Version) 缓存。</summary>
    public static string ComputeHash(ContextPolicyBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var cacheKey = (bundle.BundleId, bundle.Version);
        return _hashCache.GetOrAdd(cacheKey, _ => ComputeHashUncached(bundle));
    }

    /// <summary>
    /// 计算 bundle 的内容哈希（无缓存）。
    /// 用于 bundle 不可变性验证 — 始终重新计算，不受缓存影响，
    /// 确保 tampered bundle 不会被缓存的旧哈希掩盖。
    /// </summary>
    /// <remarks>
    /// 安全性说明：<see cref="ComputeHash"/> 使用 (BundleId, Version) 作为缓存键，
    /// 假设 bundle 内容不可变。若 bundle 被篡改（内容修改但 BundleId/Version 不变），
    /// 缓存会返回旧哈希，掩盖篡改。此 uncached 方法绕过缓存，每次重新计算 SHA256，
    /// 用于 PostgresResolvedPolicyProvider 的 immutability 验证（步骤 3）。
    /// </remarks>
    public static string ComputeHashUncached(ContextPolicyBundle bundle)
    {
        var sb = new StringBuilder();
        sb.Append(bundle.BundleId).Append('|');
        sb.Append(bundle.Version).Append('|');

        // Policies（5 个能力作用域版本）
        sb.Append(bundle.Policies.DecisionSchemaVersion).Append('|');
        sb.Append(bundle.Policies.PackagePolicyVersion).Append('|');
        sb.Append(bundle.Policies.RetrievalPolicyVersion).Append('|');
        sb.Append(bundle.Policies.RelationProfileVersion).Append('|');
        sb.Append(bundle.Policies.QualityContractVersion).Append('|');

        // Safety — bool/double 使用 invariant culture，tag 集合先排序再 join
        sb.Append(bundle.Safety.ProfileId).Append('|');
        sb.Append(bundle.Safety.AllowDeprecatedUsedByActiveChain.ToString()).Append('|');
        sb.Append(bundle.Safety.AllowDuplicateReference.ToString()).Append('|');
        sb.Append(string.Join(',', bundle.Safety.RequiredTags.OrderBy(t => t, StringComparer.Ordinal))).Append('|');
        sb.Append(string.Join(',', bundle.Safety.ForbiddenTags.OrderBy(t => t, StringComparer.Ordinal))).Append('|');

        // Budget — StrictBudgetEnforcement 为 bool，使用 ToString()；SectionRatios 的 value 为 double，使用 invariant
        sb.Append(bundle.Budget.ProfileId).Append('|');
        sb.Append(bundle.Budget.DefaultTokenBudget).Append('|');
        sb.Append(bundle.Budget.DefaultTopK).Append('|');
        sb.Append(bundle.Budget.StrictBudgetEnforcement.ToString()).Append('|');
        foreach (var (key, value) in bundle.Budget.SectionRatios.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(key).Append(':').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(';');
        sb.Append('|');

        // Routing — double 字段使用 invariant culture，bool 使用 ToString()，EnabledExperts 先排序
        sb.Append(bundle.Routing.ProfileId).Append('|');
        sb.Append(bundle.Routing.EnableModelScoring.ToString()).Append('|');
        sb.Append(bundle.Routing.ModelArtifactId ?? "null").Append('|');
        sb.Append(bundle.Routing.DeterministicWeight.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        sb.Append(bundle.Routing.ModelWeight.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        sb.Append(bundle.Routing.ModelConfidenceThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        sb.Append(string.Join(',', bundle.Routing.EnabledExperts.OrderBy(t => t, StringComparer.Ordinal))).Append('|');

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
// DefaultRouter + DefaultExpertCatalog
// ---------------------------------------------------------------------------

/// <summary>
/// 统一 Router 默认骨架。返回 Budget-Aware 平均分配的 ExpertRoutingDecisionSet。
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

        // 读取 RetrievalInput 的 Include* 开关（仅 Purpose=Retrieval 时生效）。
        // 默认全部 true（兼容非 Retrieval 路径与未指定字段的请求）。
        var retrievalInput = request.RetrievalInput;
        var includeKeyword = retrievalInput?.IncludeKeywordRecall ?? true;
        var includeVector = retrievalInput?.IncludeVectorRecall ?? true;
        var includeRelation = retrievalInput?.IncludeRelationExpansion ?? true;
        var includeWorkingMemory = retrievalInput?.IncludeWorkingMemory ?? true;
        // 新增：StableMemory Include 开关（默认 true）
        var includeStableMemory = retrievalInput?.IncludeStableMemory ?? true;

        // 统计启用计数（排除 Mandatory/Constraint — 它们永远启用且不计入预算分配）
        var nonMandatoryEnabledCount = available.Count(
            e => e != ExpertKind.Mandatory && e != ExpertKind.Constraint
                && IsIncludeEnabled(e, includeKeyword, includeVector, includeRelation, includeWorkingMemory, includeStableMemory));

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

            // Include 开关检查（仅对非 Mandatory/Constraint 的 Expert 生效）
            var includeEnabled = IsIncludeEnabled(
                mappedKind, includeKeyword, includeVector,
                includeRelation, includeWorkingMemory, includeStableMemory);

            // enabled = Mandatory/Constraint(永远启用) || (已注册 && Include 开关启用)
            var disabledByInclude = !isMandatory && !includeEnabled;
            var enabled = isMandatory || (isRegistered && includeEnabled);

            var decisionTopK = isMandatory ? totalTopK : (enabled ? perExpertTopK : 0);
            var decisionTokenBudget = isMandatory ? totalTokenBudget : (enabled ? perExpertTokenBudget : 0);

            // 根据 disable 原因区分 ReasonCode
            string reasonCode;
            string? disabledReason;
            if (enabled)
            {
                reasonCode = isMandatory ? "mandatory-always-enabled" : "default";
                disabledReason = null;
            }
            else if (disabledByInclude)
            {
                reasonCode = "disabled-by-request-include-flag";
                disabledReason = "expert disabled by RetrievalInput.Include* flag";
            }
            else
            {
                reasonCode = "expert-not-registered";
                disabledReason = "expert not registered in catalog";
            }

            decisions.Add(new ExpertRoutingDecision
            {
                Expert = expert,
                Enabled = enabled,
                TopK = decisionTopK,
                TokenBudget = decisionTokenBudget,
                Weight = 1.0,
                ReasonCode = reasonCode,
                DisabledReason = disabledReason,
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

    /// <summary>
    /// 根据 RetrievalInput 的 Include* 开关判断 Expert 是否应启用。
    /// Mandatory/Constraint 永远启用（不受 Include 开关控制，由调用方判断）。
    /// </summary>
    private static bool IsIncludeEnabled(
        ExpertKind kind,
        bool includeKeyword,
        bool includeVector,
        bool includeRelation,
        bool includeWorkingMemory,
        bool includeStableMemory) => kind switch
        {
            ExpertKind.Lexical => includeKeyword,
            ExpertKind.Semantic => includeVector,
            ExpertKind.Graph => includeRelation,
            ExpertKind.WorkingMemory => includeWorkingMemory,
            ExpertKind.StableMemory => includeStableMemory,
            // Mandatory/Constraint 永远启用（由调用方 isMandatory 判断处理）
            ExpertKind.Mandatory or ExpertKind.Constraint => true,
            // Recency 默认不注册，Include 开关也无意义
            _ => true
        };

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
/// Provider 能力目录默认骨架。返回除 Recency 外的全部 Expert。
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
// DefaultCanonicalCandidateMerger + DefaultEarlyAdmissionGate
//        + DefaultFeaturePipeline + DefaultSafetyGate + DefaultLifecycleGate
// ---------------------------------------------------------------------------

/// <summary>
/// / 规范化候选合并器默认实现。按 CanonicalCandidateKey 合并多 Expert 来源。
/// </summary>
/// <remarks>
/// 重构：
///   - 单一 accumulator：Dictionary&lt;CanonicalCandidateKey, CandidateAccumulator&gt;，
///     替代 envelopeByKey + originsByKey + contributionsByKey + materials 四重字典。
///   - Material 冲突检测读取 CandidateMaterial.ContentHash 字段（构造时已缓存），
///     不再每次冲突比对都重算 SHA256。
///   - SourceRefs 用 HashSet&lt;string&gt; 累加，避免每次 LINQ Concat+Distinct+ToList 分配。
///   - 修复合并语义 bug：重复候选不再只保留首个 Envelope 的 Features/Utility，
///     改为 Features 取 max（多 Expert 信号强化）、Utility 取 max(FinalScore)（最高分胜出）。
/// </remarks>
public sealed class DefaultCanonicalCandidateMerger : ICanonicalCandidateMerger
{
    /// <summary>合并多个 Expert 的输出，按 CanonicalCandidateKey 去重。</summary>
    public CandidateWorkingSet Merge(IReadOnlyList<ExpertExecutionResult> expertOutputs)
    {
        ArgumentNullException.ThrowIfNull(expertOutputs);

        var accumulators = new Dictionary<CanonicalCandidateKey, CandidateAccumulator>();

        foreach (var output in expertOutputs)
        {
            // Material 冲突策略（不再后写覆盖前写）：
            //   - 相同 key、相同 content hash：合并 SourceRefs（union）；
            //   - 相同 key、不同 content hash：冲突 → throw（fail-fast）；
            //   - 不同 EntityVersion：CanonicalKey 自然不同，两个 Material 都保留。
            // 直接读取 material.ContentHash（CandidateMaterial 构造时已缓存），
            // 避免每次冲突比对都重算 SHA256。
            foreach (var (key, material) in output.Materials)
            {
                // 用 ref 访问 accumulator，避免值类型 Dictionary 副本修改丢失。
                ref var accRef = ref CollectionsMarshal.GetValueRefOrAddDefault(accumulators, key, out var exists);
                if (exists && accRef.Material is { } existing)
                {
                    var existingHash = existing.ContentHash;
                    var newHash = material.ContentHash;
                    if (string.Equals(existingHash, newHash, StringComparison.Ordinal))
                    {
                        // 相同 content hash：合并 SourceRefs（HashSet 累加，O(1) 每条）
                        // accRef.MaterialSourceRefs 在首次初始化时已创建，此处非 null。
                        foreach (var r in material.SourceRefs)
                        {
                            accRef.MaterialSourceRefs!.Add(r);
                        }
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
                    // 首次见到此 key 的 Material：初始化 accumulator
                    accRef.Material = material;
                    accRef.MaterialSourceRefs = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var r in material.SourceRefs)
                    {
                        accRef.MaterialSourceRefs.Add(r);
                    }
                }
            }

            foreach (var envelope in output.Envelopes)
            {
                var key = envelope.CanonicalKey;
                ref var acc = ref CollectionsMarshal.GetValueRefOrAddDefault(accumulators, key, out var exists);
                if (!exists || acc.Envelope is null)
                {
                    // 首次见到此 key 的 Envelope（或 accumulator 仅由 Material 路径初始化过）：
                    //   - 直接采用当前 Envelope 作为基准
                    //   - 初始化 Origins / Contributions（若已存在则保留，因为 Material 路径不会触碰它们）
                    acc.Envelope = envelope;
                    acc.Origins ??= new List<ExpertOrigin>(envelope.Origins.Count);
                    acc.Origins.AddRange(envelope.Origins);
                    acc.Contributions ??= new Dictionary<ExpertKind, double>(envelope.ExpertContributions.Count);
                    foreach (var (expert, contribution) in envelope.ExpertContributions)
                    {
                        acc.Contributions.TryGetValue(expert, out var prev);
                        acc.Contributions[expert] = prev + contribution;
                    }
                }
                else
                {
                    // 重复 key（accumulator 已有 Envelope）：累加 Origins / Contributions
                    acc.Origins ??= new List<ExpertOrigin>();
                    acc.Origins.AddRange(envelope.Origins);

                    acc.Contributions ??= new Dictionary<ExpertKind, double>();
                    foreach (var (expert, contribution) in envelope.ExpertContributions)
                    {
                        acc.Contributions.TryGetValue(expert, out var prev);
                        acc.Contributions[expert] = prev + contribution;
                    }

                    // 修复：合并 Features / Utility（原实现只保留首个 Envelope）。
                    //   - Features：取每维 max（多 Expert 观察到更高信号时应保留）
                    //   - Utility：取 max(FinalScore)（最高分胜出，与 Ranking 阶段排序一致）
                    acc.Envelope = MergeEnvelope(acc.Envelope, envelope);
                }
            }
        }

        // 重建合并后的 Envelopes + Materials
        var mergedEnvelopes = new List<ContextCandidateEnvelope>(accumulators.Count);
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(accumulators.Count);
        foreach (var (key, acc) in accumulators)
        {
            // Envelope：应用 union Origins + sum Contributions + 合并后的 Features/Utility
            var envelope = acc.Envelope!;
            if (acc.Origins is not null)
            {
                envelope = envelope with { Origins = acc.Origins };
            }
            if (acc.Contributions is not null)
            {
                envelope = envelope with { ExpertContributions = acc.Contributions };
            }
            mergedEnvelopes.Add(envelope);

            // Material：应用 union SourceRefs（若有）
            if (acc.Material is { } material)
            {
                var finalMaterial = acc.MaterialSourceRefs is { Count: > 0 } refs
                    ? material with { SourceRefs = refs.ToArray() }
                    : material;
                materials[key] = finalMaterial;
            }
        }

        return new CandidateWorkingSet
        {
            Envelopes = mergedEnvelopes,
            Materials = materials
        };
    }

    /// <summary>
    /// 合并两个相同 CanonicalKey 的 Envelope。
    /// Features 取每维 max，Utility 取 max(FinalScore)；其他字段保留首个（首个已通过 Safety/Provenance 校验）。
    /// </summary>
    private static ContextCandidateEnvelope MergeEnvelope(
        ContextCandidateEnvelope first,
        ContextCandidateEnvelope next)
    {
        // 首个 Envelope 已通过 Safety/Provenance 校验；只合并 Features 与 Utility。
        var mergedFeatures = MergeFeatures(first.Features, next.Features);
        var mergedUtility = MergeUtility(first.Utility, next.Utility);
        return first with
        {
            Features = mergedFeatures,
            Utility = mergedUtility
        };
    }

    /// <summary>合并 Features：每维取 max（多 Expert 信号强化）。</summary>
    private static CandidateFeatureVector MergeFeatures(
        CandidateFeatureVector a,
        CandidateFeatureVector b)
    {
        return new CandidateFeatureVector
        {
            LexicalScore = Math.Max(a.LexicalScore, b.LexicalScore),
            SemanticScore = Math.Max(a.SemanticScore, b.SemanticScore),
            RecencyScore = Math.Max(a.RecencyScore, b.RecencyScore),
            RelationBoost = Math.Max(a.RelationBoost, b.RelationBoost),
            MandatoryWeight = Math.Max(a.MandatoryWeight, b.MandatoryWeight),
            FeatureSchemaVersion = a.FeatureSchemaVersion
        };
    }

    /// <summary>合并 Utility：取 max(FinalScore)；保留模型相关字段（ModelApplied 优先 true）。</summary>
    private static CandidateUtilityScore MergeUtility(
        CandidateUtilityScore a,
        CandidateUtilityScore b)
    {
        // FinalScore 取 max：高分的候选在后续 Allocator 排序中应胜出，
        // 避免被首个低分 Expert "锁死"。
        var pickFinal = a.FinalScore >= b.FinalScore ? a : b;

        // DeterministicScore 取 max（与 FinalScore 取 max 语义一致）。
        var detScore = Math.Max(a.DeterministicScore, b.DeterministicScore);

        // ModelScore：优先取非 null（任一 Expert 观察到模型分数则保留）。
        var modelScore = a.ModelScore ?? b.ModelScore;

        // ModelAttempted / ModelApplied：任一 true 即 true（最宽口径，便于审计）。
        var modelAttempted = a.ModelAttempted || b.ModelAttempted;
        var modelApplied = a.ModelApplied || b.ModelApplied;

        // ModelFallbackReason：若任一 Expert 应用成功则清空；否则保留首个非空原因。
        string? fallbackReason = modelApplied
            ? null
            : (a.ModelFallbackReason ?? b.ModelFallbackReason);

        return pickFinal with
        {
            DeterministicScore = detScore,
            ModelScore = modelScore,
            ModelAttempted = modelAttempted,
            ModelApplied = modelApplied,
            ModelFallbackReason = fallbackReason
        };
    }

    /// <summary>
    /// 单一 accumulator（值类型，避免为每个 key 分配多个小对象）。
    /// 合并完成后再 build 为最终 Envelope/Material。
    /// </summary>
    private struct CandidateAccumulator
    {
        /// <summary>首个出现的 Envelope（合并完成后被 with 替换）。</summary>
        public ContextCandidateEnvelope? Envelope;

        /// <summary>累积的 Origins（union）。</summary>
        public List<ExpertOrigin>? Origins;

        /// <summary>累积的 Contributions（sum per Expert）。</summary>
        public Dictionary<ExpertKind, double>? Contributions;

        /// <summary>首个出现的 Material（若该 key 有正文）。</summary>
        public CandidateMaterial? Material;

        /// <summary>累积的 Material SourceRefs（union）。</summary>
        public HashSet<string>? MaterialSourceRefs;
    }
}

/// <summary>
/// Early Admission Gate 默认骨架。检查 scope mismatch / superseded / archived。
/// </summary>
/// <remarks>
/// B-1 骨架：仅检查 superseded + scope 字段非空；B-2 将接入 forbidden tag / illegal evidence。
/// 新增 EvaluateBatch 批量评估，返回 AdmissionPartition（Admitted + Rejected），
/// 调用方将 Rejected 候选保留到 DroppedEnvelopes（reason=EarlyAdmissionRejected），不再静默丢弃。
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

    /// <summary>
    /// 批量评估候选准入，返回分区结果（Admitted + Rejected + RejectReasons）。
    /// </summary>
    public AdmissionPartition EvaluateBatch(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(snapshot);

        var admitted = new List<ContextCandidateEnvelope>(envelopes.Count);
        var rejected = new List<ContextCandidateEnvelope>();
        var rejectReasons = new Dictionary<CanonicalCandidateKey, string>();

        foreach (var envelope in envelopes)
        {
            var admission = Evaluate(envelope, snapshot);
            if (admission.Admitted)
            {
                admitted.Add(envelope);
            }
            else
            {
                rejected.Add(envelope);
                rejectReasons[envelope.CanonicalKey] = admission.ReasonCode;
            }
        }

        return new AdmissionPartition(admitted, rejected, rejectReasons);
    }
}

/// <summary>
/// Feature Pipeline 默认实现。将 ScoreBreakdown 提升为强类型特征字段，并做 [0,1] 归一化。
/// </summary>
/// <remarks>
/// rule-only 模式：特征仅记录到 envelope.Features 供 trace（不影响 FinalScore）。
/// model 模式：特征作为模型输入 FeatureVector 的数据源。
/// 提升映射：rawTokenMatch→LexicalScore, semanticAnchor→SemanticScore,
/// recency→RecencyScore, relation→RelationBoost, mandatory→MandatoryWeight。
/// 归一化 — LexicalScore/SemanticScore/RecencyScore/RelationBoost clamp 到 [0,1]；
/// MandatoryWeight 保持 0/1（不 clamp）。Provider 已填充的强类型字段（非零）不被覆盖。
/// </remarks>
public sealed class DefaultFeaturePipeline : IFeaturePipeline
{
    /// <summary>计算/标准化候选特征向量。</summary>
    public ValueTask<IReadOnlyList<ContextCandidateEnvelope>> EnrichAsync(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        FeaturePipelineContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // 快速路径：空集合直接返回
        if (envelopes.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<ContextCandidateEnvelope>>(envelopes);
        }

        var result = new List<ContextCandidateEnvelope>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            result.Add(EnrichEnvelope(envelope));
        }
        return ValueTask.FromResult<IReadOnlyList<ContextCandidateEnvelope>>(result);
    }

    /// <summary>将 ScoreBreakdown 值提升为强类型字段，填充 MandatoryWeight，并对特征值做 [0,1] 归一化。</summary>
    private static ContextCandidateEnvelope EnrichEnvelope(ContextCandidateEnvelope envelope)
    {
        var features = envelope.Features;
        var breakdown = features.ScoreBreakdown;

        // 如果强类型字段已非零，说明 adapter 已填充，不覆盖
        var lexical = features.LexicalScore != 0
            ? features.LexicalScore
            : TryGet(breakdown, "rawTokenMatch", out var v1) ? v1
            : TryGet(breakdown, "lexical", out var v1b) ? v1b : 0;
        var semantic = features.SemanticScore != 0
            ? features.SemanticScore
            : TryGet(breakdown, "semanticAnchor", out var v2) ? v2
            : TryGet(breakdown, "semantic", out var v2b) ? v2b : 0;
        var recency = features.RecencyScore != 0
            ? features.RecencyScore
            : TryGet(breakdown, "recency", out var v3) ? v3 : 0;
        var relation = features.RelationBoost != 0
            ? features.RelationBoost
            : TryGet(breakdown, "relation", out var v4) ? v4
            : TryGet(breakdown, "relation_boost", out var v4b) ? v4b : 0;
        var mandatory = features.MandatoryWeight != 0
            ? features.MandatoryWeight
            : envelope.Safety.IsMandatory ? 1.0 : 0;

        // 归一化 — 将特征值 clamp 到 [0,1]（MandatoryWeight 保持 0/1 不 clamp）
        lexical = Math.Clamp(lexical, 0.0, 1.0);
        semantic = Math.Clamp(semantic, 0.0, 1.0);
        recency = Math.Clamp(recency, 0.0, 1.0);
        relation = Math.Clamp(relation, 0.0, 1.0);

        // 如果所有字段都已有值（无需提升），返回原 envelope 避免分配
        if (lexical == features.LexicalScore && semantic == features.SemanticScore
            && recency == features.RecencyScore && relation == features.RelationBoost
            && mandatory == features.MandatoryWeight)
        {
            return envelope;
        }

        return envelope with
        {
            Features = features with
            {
                LexicalScore = lexical,
                SemanticScore = semantic,
                RecencyScore = recency,
                RelationBoost = relation,
                MandatoryWeight = mandatory
            }
        };
    }

    private static bool TryGet(IReadOnlyDictionary<string, double> dict, string key, out double value)
    {
        if (dict is null || dict.Count == 0)
        {
            value = 0;
            return false;
        }
        return dict.TryGetValue(key, out value);
    }
}

/// <summary>
/// Decision Safety Gate 默认骨架。基于 envelope.Safety + bundle.Safety 评估。
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
/// Lifecycle Gate 默认骨架。检查候选生命周期状态。
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
// DefaultUtilityScorer + DefaultGlobalAllocator
// ---------------------------------------------------------------------------

/// <summary>
/// Utility Scorer 默认实现。支持 rule-only 和 model-weighted 两种模式。
/// </summary>
/// <remarks>
/// rule-only 模式（EnableModelScoring=false，默认）：FinalScore = DeterministicScore（adapter 已填充），
/// 直接返回输入不变。
/// model-weighted 模式（EnableModelScoring=true）：FinalScore = w_d * Det + w_m * Model，
/// 仅当 ModelConfidence >= threshold 时使用模型加权，否则回退 deterministic 并标记 ReasonCode。
/// 模型推理失败时静默降级到 deterministic（fail-open，不阻塞主链）。
/// </remarks>
public sealed class DefaultUtilityScorer : IUtilityScorer
{
    private readonly IBatchInferenceEngine? _inferenceEngine;
    private readonly ICalibrationService? _calibrationService;
    private readonly IFeatureRegistry? _featureRegistry;
    private readonly IInferenceResultValidator? _inferenceValidator;

    // 子问题6：特征 schema 验证器（必须，非 null）。
    // 在推理前对输入特征与 FeatureSchema 执行严格匹配验证，防止 schema drift。
    private readonly IFeatureSchemaValidator _featureSchemaValidator;

    /// <summary>
    /// 构造 Utility Scorer。
    /// </summary>
    /// <param name="featureSchemaValidator">子问题6：特征 schema 验证器（必须，非 null）。推理前验证输入特征与 schema 一致性。</param>
    /// <param name="inferenceEngine">模型批量推理引擎（null 时强制 rule-only）。</param>
    /// <param name="calibrationService">分数校准服务（null 时使用原始模型分数）。</param>
    /// <param name="featureRegistry">特征 schema 注册表（null 时无法构造 FeatureVector，强制 rule-only）。</param>
    /// <param name="inferenceValidator">
    /// 推理输出验证器。null 时使用默认 DefaultInferenceResultValidator。
    /// 验证失败时降级到 deterministic（不抛异常，fail-safe）。
    /// </param>
    public DefaultUtilityScorer(
        IFeatureSchemaValidator featureSchemaValidator,
        IBatchInferenceEngine? inferenceEngine = null,
        ICalibrationService? calibrationService = null,
        IFeatureRegistry? featureRegistry = null,
        IInferenceResultValidator? inferenceValidator = null)
    {
        ArgumentNullException.ThrowIfNull(featureSchemaValidator);

        _featureSchemaValidator = featureSchemaValidator;
        _inferenceEngine = inferenceEngine;
        _calibrationService = calibrationService;
        _featureRegistry = featureRegistry;
        _inferenceValidator = inferenceValidator ?? new DefaultInferenceResultValidator();
    }

    /// <summary>对候选集合计算效用评分，返回更新后的 envelope 列表。</summary>
    public async ValueTask<IReadOnlyList<ContextCandidateEnvelope>> ScoreAsync(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(snapshot);

        // rule-only 模式：FinalScore 已由 adapter 填充为 DeterministicScore，直接返回。
        if (!snapshot.Routing.EnableModelScoring)
        {
            return envelopes;
        }

        // model 模式但缺少推理引擎 / registry → 标记 ModelAttempted 并降级
        if (_inferenceEngine is null || _featureRegistry is null)
        {
            return MarkModelAttempted(envelopes, applied: false, fallbackReason: "engine-unavailable");
        }

        // DeterministicReplay 引擎默认不参与 FinalScore 加权，
        // 避免把 feature hash 当成真实模型分数扰动排序。
        // 仅当 Policy 显式允许（AllowDeterministicReplayScoring=true）时才走模型路径。
        if (_inferenceEngine.Kind == InferenceEngineKind.DeterministicReplay
            && !snapshot.AllowDeterministicReplayScoring)
        {
            return MarkModelAttempted(envelopes, applied: false, fallbackReason: "deterministic-replay-skipped");
        }

        // Disabled 引擎立即降级
        if (_inferenceEngine.Kind == InferenceEngineKind.Disabled)
        {
            return MarkModelAttempted(envelopes, applied: false, fallbackReason: "engine-disabled");
        }

        return await ScoreWithModelAsync(envelopes, snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在不应用模型分数的情况下，标记 ModelAttempted=true / ModelApplied=false，
    /// 并记录降级原因。保留原 DeterministicScore 作为 FinalScore。
    /// </summary>
    private static IReadOnlyList<ContextCandidateEnvelope> MarkModelAttempted(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        bool applied,
        string fallbackReason)
    {
        if (envelopes.Count == 0)
        {
            return envelopes;
        }

        var result = new List<ContextCandidateEnvelope>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            // 仅当原始 ModelAttempted=false 时才标记，避免覆盖更精确的下游标记
            if (envelope.Utility.ModelAttempted)
            {
                result.Add(envelope);
                continue;
            }

            result.Add(envelope with
            {
                Utility = envelope.Utility with
                {
                    ModelAttempted = true,
                    ModelApplied = applied,
                    ModelFallbackReason = applied ? null : fallbackReason,
                    // 保留 ReasonCode 原值（可能是 "deterministic-only" 或 "provider-recall"）
                    // 仅在 applied=false 且原 ReasonCode 表示已应用模型时才改写
                    ReasonCode = applied ? envelope.Utility.ReasonCode : "fallback-to-deterministic"
                }
            });
        }
        return result;
    }

    /// <summary>模型加权评分路径。</summary>
    private async ValueTask<IReadOnlyList<ContextCandidateEnvelope>> ScoreWithModelAsync(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var routing = snapshot.Routing;
        var w_d = routing.DeterministicWeight;
        var w_m = routing.ModelWeight;
        var threshold = routing.ModelConfidenceThreshold;
        var modelArtifactId = routing.ModelArtifactId;

        // 验证 ScoreWeights（w_d / w_m 非负且和为 1.0）。
        // 验证失败不抛异常，但记录降级原因（fail-safe）。
        var weightsValidation = _inferenceValidator is DefaultInferenceResultValidator defaultValidator
            ? defaultValidator.ValidateScoreWeights(w_d, w_m)
            : null;

        // 按 snapshot.FeatureSchemaVersion 解析 FeatureSchema（不再用 engine.ModelVersion）。
        // 这是关键解耦：模型版本与特征 schema 版本是不同维度。
        var featureSchema = _featureRegistry!.Get(snapshot.FeatureSchemaVersion);
        if (featureSchema is null)
        {
            // 无匹配 schema → 标记降级
            return MarkModelAttempted(envelopes, applied: false, fallbackReason: "schema-not-found");
        }

        // 构造行号映射（所有候选都参与推理；保留 indexMap 以便后续扩展过滤能力）
        var indexMap = new List<int>(envelopes.Count);
        for (var i = 0; i < envelopes.Count; i++)
        {
            indexMap.Add(i);
        }

        if (indexMap.Count == 0)
        {
            return envelopes; // 无可推理候选 → 保持原状
        }

        // 调用模型批量推理（fail-open：异常降级到 deterministic）
        // ONNX 优化：优先走 FeatureBatch 连续内存路径（row-major float[]，无 boxing），
        // 让 ONNX 引擎直接消费连续内存避免字典拆箱；若引擎不支持则降级到字典路径（向后兼容）。
        BatchInferenceResult inferenceResult;
        BatchInferenceRequest? inferenceRequest = null;
        FeatureBatch? inferenceBatch = null;
        try
        {
            if (TryBuildFeatureBatch(envelopes, featureSchema, out var featureBatch))
            {
                inferenceBatch = featureBatch;

                // 子问题6：推理前强制调用 IFeatureSchemaValidator.Validate(FeatureSchema, FeatureBatch)，
                // 验证 SchemaVersion / FeatureCount / FeatureNames 顺序 / Values 长度 / 值有限性。
                // 验证失败时 fail-safe 降级到 deterministic（不抛异常，避免 schema drift 污染模型输出）。
                var schemaValidation = _featureSchemaValidator.Validate(featureSchema, featureBatch);
                if (!schemaValidation.IsValid)
                {
                    return MarkModelAttempted(envelopes, applied: false,
                        fallbackReason: $"schema-validation-failed: {schemaValidation.Error}");
                }

                try
                {
                    inferenceResult = await _inferenceEngine.InferBatchAsync(featureBatch, cancellationToken).ConfigureAwait(false);
                }
                catch (NotSupportedException)
                {
                    // 引擎不支持 InferBatchAsync → 降级到字典路径（向后兼容）
                    inferenceBatch = null;
                    inferenceRequest = BuildDictionaryRequest(envelopes, featureSchema);

                    // 子问题6：字典路径同样强制 schema 验证（ValidateBatch 校验每行 FeatureVector）。
                    var dictSchemaValidation = _featureSchemaValidator.ValidateBatch(featureSchema, inferenceRequest.Inputs);
                    if (!dictSchemaValidation.IsValid)
                    {
                        return MarkModelAttempted(envelopes, applied: false,
                            fallbackReason: $"schema-validation-failed: {dictSchemaValidation.Error}");
                    }

                    inferenceResult = await _inferenceEngine.InferAsync(inferenceRequest, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // schema 无特征列或 envelopes 为空（理论不可达，前面已过滤）→ 降级字典路径
                inferenceRequest = BuildDictionaryRequest(envelopes, featureSchema);

                // 子问题6：字典路径强制 schema 验证。
                var dictSchemaValidation = _featureSchemaValidator.ValidateBatch(featureSchema, inferenceRequest.Inputs);
                if (!dictSchemaValidation.IsValid)
                {
                    return MarkModelAttempted(envelopes, applied: false,
                        fallbackReason: $"schema-validation-failed: {dictSchemaValidation.Error}");
                }

                inferenceResult = await _inferenceEngine.InferAsync(inferenceRequest, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 模型推理失败 → 标记降级
            return MarkModelAttempted(envelopes, applied: false, fallbackReason: "inference-failed");
        }

        if (!inferenceResult.Succeeded)
        {
            // 推理报告失败 → 标记降级
            return MarkModelAttempted(envelopes, applied: false, fallbackReason: "inference-succeeded-false");
        }

        // 推理输出严格验证（NaN/Infinity/Confidence 范围/Count 一致性）。
        // 验证失败时降级到 deterministic（不抛异常，避免异常模型输出污染排序）。
        // FeatureBatch 路径优先用 Validate(FeatureBatch, result) 重载，
        // 字典路径用 Validate(request, result) 重载。
        InferenceValidationResult validationResult = inferenceBatch is not null
            ? _inferenceValidator!.Validate(inferenceBatch, inferenceResult)
            : _inferenceValidator!.Validate(inferenceRequest!, inferenceResult);
        if (!validationResult.IsValid)
        {
            return MarkModelAttempted(envelopes, applied: false,
                fallbackReason: $"inference-validation-failed: {validationResult.Error}");
        }

        // 应用校准并聚合 FinalScore
        var result = new List<ContextCandidateEnvelope>(envelopes.Count);
        var inferenceIdx = 0;
        for (var i = 0; i < envelopes.Count; i++)
        {
            var envelope = envelopes[i];

            // 未参与推理的候选保持不变
            if (inferenceIdx >= indexMap.Count || indexMap[inferenceIdx] != i)
            {
                result.Add(envelope);
                continue;
            }

            var output = inferenceResult.Outputs[inferenceIdx];
            inferenceIdx++;

            // 校准（如有校准服务）
            var rawScore = output.Score;
            var confidence = output.Confidence;
            var calibratedScore = _calibrationService is not null
                ? _calibrationService.Calibrate(rawScore, _inferenceEngine.ModelVersion)
                : rawScore;

            // 低于置信阈值 → 回退 deterministic（但标记 ModelAttempted=true）
            if (confidence < threshold)
            {
                result.Add(envelope with
                {
                    Utility = envelope.Utility with
                    {
                        ModelScore = calibratedScore,
                        ModelConfidence = confidence,
                        FinalScore = envelope.Utility.DeterministicScore,
                        ReasonCode = "fallback-to-deterministic",
                        ModelArtifactRef = modelArtifactId,
                        ModelAttempted = true,
                        ModelApplied = false,
                        ModelFallbackReason = "confidence-below-threshold"
                    }
                });
                continue;
            }

            // 模型加权：FinalScore = w_d * Det + w_m * Model
            // 子问题3：若 weights 验证未通过（w_d/w_m 非有限、负数、或和≠1.0），
            // 不再计算加权分数（避免 FinalScore 被错误缩放），直接 fallback 到 deterministic score。
            // ModelApplied=false 表示模型分数未实际应用；ModelFallbackReason 记录降级原因。
            var weightsInvalid = weightsValidation is not null && !weightsValidation.IsValid;
            if (weightsInvalid)
            {
                result.Add(envelope with
                {
                    Utility = envelope.Utility with
                    {
                        ModelScore = calibratedScore,
                        ModelConfidence = confidence,
                        FinalScore = envelope.Utility.DeterministicScore,
                        ReasonCode = "model-weighted-weights-invalid",
                        ModelArtifactRef = modelArtifactId,
                        ModelAttempted = true,
                        ModelApplied = false,
                        ModelFallbackReason = $"weights-validation-failed: {weightsValidation!.Error}"
                    }
                });
                continue;
            }

            var finalScore = w_d * envelope.Utility.DeterministicScore + w_m * calibratedScore;
            result.Add(envelope with
            {
                Utility = envelope.Utility with
                {
                    ModelScore = calibratedScore,
                    ModelConfidence = confidence,
                    FinalScore = finalScore,
                    ReasonCode = "model-weighted",
                    ModelArtifactRef = modelArtifactId,
                    ModelAttempted = true,
                    ModelApplied = true,
                    ModelFallbackReason = null
                }
            });
        }

        return result;
    }

    /// <summary>从 envelope 构造模型输入 FeatureVector（按 schema 映射）。</summary>
    private static FeatureVector BuildFeatureVector(ContextCandidateEnvelope envelope, FeatureSchema schema)
    {
        var features = envelope.Features;
        var values = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var def in schema.Features)
        {
            object value = def.Name switch
            {
                "lexical_score" => features.LexicalScore,
                "semantic_score" => features.SemanticScore,
                "recency_score" => features.RecencyScore,
                "relation_boost" => features.RelationBoost,
                "mandatory_weight" => features.MandatoryWeight,
                "deterministic_score" => envelope.Utility.DeterministicScore,
                _ => TryGetScoreBreakdown(features.ScoreBreakdown, def.Name, out var bd)
                    ? bd
                    : 0.0
            };
            values[def.Name] = value;
        }

        return new FeatureVector
        {
            SchemaVersion = schema.Version,
            Values = values
        };
    }

    /// <summary>
    /// ONNX 优化：直接构造 row-major float[] FeatureBatch，避免 Boxing double→object。
    /// 按 schema.Features 固定列序填充：第 i 行第 j 列位于 values[i * featureCount + j]。
    /// 通过 switch 直接读取 envelope 的 double 字段转 float，无 IDictionary 查找与装箱开销。
    /// </summary>
    /// <param name="envelopes">候选列表（每个 envelope 一行）。</param>
    /// <param name="schema">特征 schema（决定列顺序与列名）。</param>
    /// <param name="batch">构造出的 FeatureBatch；schema 无特征列时返回 false。</param>
    /// <returns>true 表示成功构造；false 表示 schema 无特征列（调用方应降级到字典路径）。</returns>
    private static bool TryBuildFeatureBatch(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        FeatureSchema schema,
        out FeatureBatch batch)
    {
        var featureCount = schema.Features.Count;
        if (featureCount == 0 || envelopes.Count == 0)
        {
            batch = default!;
            return false;
        }

        var rowCount = envelopes.Count;
        var values = new float[rowCount * featureCount];
        var featureNames = new string[featureCount];
        for (var j = 0; j < featureCount; j++)
        {
            featureNames[j] = schema.Features[j].Name;
        }

        for (var i = 0; i < rowCount; i++)
        {
            var envelope = envelopes[i];
            var features = envelope.Features;
            var breakdown = features.ScoreBreakdown;
            var det = envelope.Utility.DeterministicScore;
            var offset = i * featureCount;
            for (var j = 0; j < featureCount; j++)
            {
                var name = featureNames[j];
                var dv = name switch
                {
                    "lexical_score" => features.LexicalScore,
                    "semantic_score" => features.SemanticScore,
                    "recency_score" => features.RecencyScore,
                    "relation_boost" => features.RelationBoost,
                    "mandatory_weight" => features.MandatoryWeight,
                    "deterministic_score" => det,
                    _ => TryGetScoreBreakdown(breakdown, name, out var bd) ? bd : 0.0
                };
                values[offset + j] = (float)dv;
            }
        }

        batch = new FeatureBatch
        {
            SchemaVersion = schema.Version,
            Values = values,
            RowCount = rowCount,
            FeatureCount = featureCount,
            FeatureNames = featureNames
        };
        return true;
    }

    /// <summary>
    /// ONNX 优化：批量构造字典路径 BatchInferenceRequest（向后兼容降级路径）。
    /// 仅在 TryBuildFeatureBatch 返回 false 或引擎不支持 InferBatchAsync 时使用。
    /// </summary>
    private static BatchInferenceRequest BuildDictionaryRequest(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        FeatureSchema schema)
    {
        var vectors = new List<FeatureVector>(envelopes.Count);
        for (var i = 0; i < envelopes.Count; i++)
        {
            vectors.Add(BuildFeatureVector(envelopes[i], schema));
        }
        return new BatchInferenceRequest { Inputs = vectors };
    }

    private static bool TryGetScoreBreakdown(IReadOnlyDictionary<string, double> breakdown, string key, out double value)
    {
        if (breakdown is null || breakdown.Count == 0)
        {
            value = 0;
            return false;
        }
        return breakdown.TryGetValue(key, out value);
    }
}

/// <summary>
/// 统一全局分配器默认骨架。TopK + TokenBudget 硬截断。
/// </summary>
/// <remarks>
/// B-1 骨架：复用 DefaultContextDecisionEngine 的分配算法
/// （IsMandatory/IsHardConstraint 优先 → FinalScore 降序 → EstimatedTokens 降序 → CandidateId 升序 →
/// TopK 截断 → TokenBudget 截断）。
/// 接入 MandatoryOverflowPolicy — mandatory 候选超出预算时按策略处理：
///   - FailClosed（AgentContext 硬窗口）：拒绝 mandatory 候选，标记 HardWindowViolated；
///   - AllowOverflowWithDiagnostic（Package/Retrieval 默认）：选入 mandatory，记录 overflow tokens；
///   - RejectLowestAuthorityMandatory：拒绝最低优先级的 mandatory 候选。
/// 诊断信息记录到 Outcome.Diagnostics（MandatoryOverflowTokens / MandatoryOverflowPolicy / HardWindowViolated）。
/// </remarks>
public sealed class DefaultGlobalAllocator : IGlobalAllocator
{
    private readonly MandatoryOverflowPolicy _mandatoryOverflowPolicy;

    /// <summary>
    /// 构造分配器。
    /// </summary>
    /// <param name="mandatoryOverflowPolicy">
    /// mandatory 候选超出预算时的处理策略。
    /// 默认 AllowOverflowWithDiagnostic（Package/Retrieval 语义）；AgentContext 硬窗口应注入 FailClosed。
    /// 此构造函数策略仅作为旧 Allocate(envelopes, snapshot) 重载的 fallback；
    /// 新 Allocate(envelopes, snapshot, context) 重载优先使用 context 中的策略。
    /// </param>
    public DefaultGlobalAllocator(MandatoryOverflowPolicy mandatoryOverflowPolicy = MandatoryOverflowPolicy.AllowOverflowWithDiagnostic)
    {
        _mandatoryOverflowPolicy = mandatoryOverflowPolicy;
    }

    /// <summary>执行全局预算分配 + per-section 配额（Legacy 重载，使用构造函数策略）。</summary>
    public AllocationResult Allocate(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(snapshot);

        // 旧重载使用构造函数注入的策略（向后兼容测试 / Legacy 路径）
        return Allocate(envelopes, snapshot, _mandatoryOverflowPolicy, purpose: null);
    }

    /// <summary>
    /// 执行全局预算分配，接受 AllocationContext（携带 Purpose + MandatoryOverflowPolicy）。
    /// 根据 context.Purpose 选择默认策略（AgentContext → FailClosed；Retrieval/Package → AllowOverflowWithDiagnostic）；
    /// context.MandatoryOverflowPolicy 始终显式传入，调用方（Runtime/Engine）负责解析最终策略。
    /// </summary>
    public AllocationResult Allocate(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot,
        AllocationContext context)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        return Allocate(envelopes, snapshot, context.MandatoryOverflowPolicy, context.Purpose);
    }

    /// <summary>
    /// 核心分配实现。使用显式传入的 MandatoryOverflowPolicy（已由调用方解析）。
    /// </summary>
    /// <remarks>
    /// 优化：把全量 O(n log n) 排序改为：
    ///   1. mandatory partition（按 FinalScore 降序，mandatory 候选数量通常很小）
    ///   2. non-mandatory partial TopK 选择（堆大小 K，仅保留前 K 候选）
    ///   3. 最终对 K 项做稳定排序
    /// 当 n &gt;&gt; K 时复杂度从 O(n log n) 降至 O(n log K)。
    /// 语义与原实现等价：mandatory 优先 → non-mandatory 按 FinalScore/EffectiveTokens/CandidateId 排序 →
    /// TopK 截断 → TokenBudget 截断。
    /// </remarks>
    /// <param name="mandatoryOverflowPolicy">本次分配使用的 overflow 策略。</param>
    /// <param name="purpose">业务用途（仅用于诊断；null 时记录 "Unknown"）。</param>
    private AllocationResult Allocate(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot,
        MandatoryOverflowPolicy mandatoryOverflowPolicy,
        ContextDecisionPurpose? purpose)
    {
        var tokenBudget = snapshot.Budget.DefaultTokenBudget;
        var topK = snapshot.Budget.DefaultTopK;

        // mandatory / non-mandatory 分区（一次遍历，避免 LINQ Where 二次扫描）
        var mandatory = new List<ContextCandidateEnvelope>();
        var nonMandatory = new List<ContextCandidateEnvelope>();
        foreach (var e in envelopes)
        {
            if (e.Safety.IsMandatory || e.Safety.IsHardConstraint)
            {
                mandatory.Add(e);
            }
            else
            {
                nonMandatory.Add(e);
            }
        }

        // mandatory 按 FinalScore 降序 → EffectiveTokens 降序 → CandidateId 升序
        // mandatory 数量通常远小于 non-mandatory，全量排序成本可忽略
        mandatory.Sort(MandatoryComparison);
        // non-mandatory：partial TopK 选择，仅保留前 topK 候选
        var nonMandatoryTopK = SelectTopK(nonMandatory, topK);
        // 对选出的 topK 做稳定排序（K 很小，O(K log K)）
        nonMandatoryTopK.Sort(NonMandatoryComparison);

        // 合并为最终遍历顺序：mandatory 优先，然后 non-mandatory TopK
        // decisions 容量按全部候选预留（dropped 候选也要生成 decision）
        var decisions = new List<CandidateAllocationDecision>(envelopes.Count);

        var selected = new List<ContextCandidateEnvelope>();
        var dropped = new List<ContextCandidateEnvelope>();
        var usedTokens = 0;
        var takenCount = 0;
        // mandatory overflow 诊断累计
        var mandatoryOverflowTokens = 0;
        var hardWindowViolated = false;
        // FailClosed 时收集溢出 mandatory 候选 ID + 总 token 需求（用于抛出异常）
        var overflowedMandatoryIds = new List<string>();
        var mandatoryRequiredTokens = 0;

        // 把 non-mandatory TopK 候选 ID 放入 set，便于后面区分 dropped
        var nonMandatoryTopKSet = new HashSet<ContextCandidateEnvelope>(
            nonMandatoryTopK, ReferenceEqualityComparer.Instance);

        // 先处理 non-mandatory 中未被 TopK 选中的候选（SectionQuotaExceeded）
        // 保持原语义：TopK 截断的候选标记为 dropped（reason=SectionQuotaExceeded）
        foreach (var envelope in nonMandatory)
        {
            if (nonMandatoryTopKSet.Contains(envelope))
            {
                continue; // 已选入 TopK，后续主循环处理
            }
            dropped.Add(envelope);
            decisions.Add(new CandidateAllocationDecision
            {
                CandidateKey = envelope.CanonicalKey,
                Section = ResolveSection(envelope),
                IncludedTokens = 0,
                IsTruncated = false,
                ReasonCode = CandidateDecisionReasonCode.SectionQuotaExceeded
            });
        }

        // 主遍历：mandatory 优先，然后 non-mandatory TopK
        foreach (var envelope in IterateMandatoryThenTopK(mandatory, nonMandatoryTopK))
        {
            var isMandatory = envelope.Safety.IsMandatory || envelope.Safety.IsHardConstraint;

            // mandatory 候选超出预算时按策略处理
            // 使用 EffectiveTokens（TokenCost 优先）
            var effectiveTokens = GetEffectiveTokens(envelope);
            if (isMandatory && usedTokens + effectiveTokens > tokenBudget)
            {
                var overflow = (usedTokens + effectiveTokens) - tokenBudget;
                mandatoryOverflowTokens += overflow;
                mandatoryRequiredTokens += effectiveTokens;

                switch (mandatoryOverflowPolicy)
                {
                    case MandatoryOverflowPolicy.FailClosed:
                        // 硬窗口 fail-closed — 收集溢出 mandatory 候选 ID
                        // 循环结束后抛出 MandatoryContextWindowExceededException（让请求真正失败）
                        hardWindowViolated = true;
                        overflowedMandatoryIds.Add(envelope.CandidateId);
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

                    case MandatoryOverflowPolicy.RejectLowestAuthorityMandatory:
                        // 拒绝最低优先级的 mandatory（当前候选按 FinalScore 降序，末尾即最低优先级）。
                        // 简化实现：当前候选若已超出预算且已有更高优先级 mandatory 选入，则拒绝当前。
                        hardWindowViolated = true;
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

                    case MandatoryOverflowPolicy.AllowOverflowWithDiagnostic:
                    default:
                        // 允许溢出但记录诊断（Package/Retrieval 默认语义）
                        break;
                }
            }

            // Token budget 检查（非 mandatory）
            // partial truncation — 当候选超出剩余预算时，不完全丢弃，
            // 而是包含部分 token（IsTruncated=true，IncludedTokens=remaining）。
            // 只有剩余空间为 0 时才完全丢弃。实际内容截断由 Projector 通过
            // IContentTruncator 在 Material sidecar 恢复时执行（Impl-1）。
            // 使用 EffectiveTokens（TokenCost 优先）
            if (!isMandatory && usedTokens + effectiveTokens > tokenBudget)
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
            // 使用 EffectiveTokens（TokenCost 优先）
            usedTokens += effectiveTokens;
            takenCount++;
            // 累计 mandatory token 需求（用于 FailClosed 异常）
            if (isMandatory) mandatoryRequiredTokens += effectiveTokens;
            decisions.Add(new CandidateAllocationDecision
            {
                CandidateKey = envelope.CanonicalKey,
                Section = ResolveSection(envelope),
                IncludedTokens = effectiveTokens,
                IsTruncated = false,
                ReasonCode = isMandatory
                    ? CandidateDecisionReasonCode.SelectedMandatory
                    : CandidateDecisionReasonCode.SelectedHighestUtility
            });
        }

        // FailClosed 策略下若有 mandatory 候选超出预算，抛出异常（fail-closed）
        // 不静默丢弃 mandatory 候选后返回成功 — 让请求真正失败，调用方可见异常。
        if (mandatoryOverflowPolicy == MandatoryOverflowPolicy.FailClosed
            && overflowedMandatoryIds.Count > 0)
        {
            throw new MandatoryContextWindowExceededException(
                mandatoryTokens: mandatoryRequiredTokens,
                budgetLimit: tokenBudget,
                overflowedCandidateIds: overflowedMandatoryIds);
        }

        // 构建诊断字典（仅在有 mandatory overflow 时记录）
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mandatoryOverflowTokens > 0 || hardWindowViolated)
        {
            diagnostics["MandatoryOverflowTokens"] = mandatoryOverflowTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
            diagnostics["MandatoryOverflowPolicy"] = mandatoryOverflowPolicy.ToString();
            diagnostics["HardWindowViolated"] = hardWindowViolated.ToString().ToLowerInvariant();
            // 记录 Purpose（诊断用），null 时记 "Unknown"
            diagnostics["Purpose"] = purpose?.ToString() ?? "Unknown";
        }

        var outcome = new ContextDecisionOutcomeSummary
        {
            SelectedCount = selected.Count,
            DroppedCount = dropped.Count,
            EstimatedTokens = usedTokens,
            TokenBudget = tokenBudget,
            Sections = Array.Empty<string>(), // B-1 骨架不实现 section 分层
            SafetyGateBlockedCount = 0, // SafetyGate 在 Engine 内执行
            BudgetExceededCount = dropped.Count,
            Diagnostics = diagnostics
        };

        return new AllocationResult(selected, dropped, decisions, outcome);
    }

    /// <summary>
    /// partial TopK 选择。使用最小堆（大小 K）保留前 K 个候选。
    /// 复杂度 O(n log K)，当 n &gt;&gt; K 时优于全量排序 O(n log n)。
    /// 当 topK &lt;= 0 或候选数 &lt;= topK 时直接返回原列表（避免无意义堆操作）。
    /// </summary>
    /// <param name="candidates">候选列表（不会被修改）。</param>
    /// <param name="topK">TopK 上限。</param>
    /// <returns>前 K 个候选（无序；调用方需排序）。</returns>
    private static List<ContextCandidateEnvelope> SelectTopK(
        IReadOnlyList<ContextCandidateEnvelope> candidates,
        int topK)
    {
        if (topK <= 0 || candidates.Count <= topK)
        {
            // topK <= 0 表示无 TopK 限制（保留全部）；候选数 <= topK 时全部保留
            return candidates as List<ContextCandidateEnvelope> ?? new List<ContextCandidateEnvelope>(candidates);
        }

        // 最小堆：堆顶是当前堆中"最小"的候选（按 NonMandatoryComparison，最小者先出堆）。
        // 堆大小 = topK；遍历候选时比堆顶大则替换堆顶。
        var heap = new List<ContextCandidateEnvelope>(topK);
        for (var i = 0; i < topK; i++)
        {
            heap.Add(candidates[i]);
            SiftUp(heap, heap.Count - 1);
        }

        for (var i = topK; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            // 堆顶是最小者；若当前候选更大则替换堆顶并下沉
            if (NonMandatoryComparison(candidate, heap[0]) > 0)
            {
                heap[0] = candidate;
                SiftDown(heap, 0, heap.Count);
            }
        }

        return heap;
    }

    /// <summary>最小堆上浮：把 idx 位置的元素上浮到正确位置。</summary>
    private static void SiftUp(List<ContextCandidateEnvelope> heap, int idx)
    {
        while (idx > 0)
        {
            var parent = (idx - 1) >> 1;
            if (NonMandatoryComparison(heap[idx], heap[parent]) < 0)
            {
                (heap[idx], heap[parent]) = (heap[parent], heap[idx]);
                idx = parent;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>最小堆下沉：把 idx 位置的元素下沉到正确位置。</summary>
    private static void SiftDown(List<ContextCandidateEnvelope> heap, int idx, int count)
    {
        while (true)
        {
            var left = 2 * idx + 1;
            var right = 2 * idx + 2;
            var smallest = idx;
            if (left < count && NonMandatoryComparison(heap[left], heap[smallest]) < 0)
            {
                smallest = left;
            }
            if (right < count && NonMandatoryComparison(heap[right], heap[smallest]) < 0)
            {
                smallest = right;
            }
            if (smallest != idx)
            {
                (heap[idx], heap[smallest]) = (heap[smallest], heap[idx]);
                idx = smallest;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>合并 mandatory 与 non-mandatory TopK 为单一遍历序列。</summary>
    private static IEnumerable<ContextCandidateEnvelope> IterateMandatoryThenTopK(
        IReadOnlyList<ContextCandidateEnvelope> mandatory,
        IReadOnlyList<ContextCandidateEnvelope> nonMandatoryTopK)
    {
        foreach (var e in mandatory)
        {
            yield return e;
        }
        foreach (var e in nonMandatoryTopK)
        {
            yield return e;
        }
    }

    /// <summary>
    /// mandatory 候选排序比较器。
    /// FinalScore 降序 → EffectiveTokens 降序 → CandidateId 升序（与原 OrderBy 链等价）。
    /// </summary>
    private static int MandatoryComparison(ContextCandidateEnvelope a, ContextCandidateEnvelope b)
    {
        // FinalScore 降序
        var cmp = b.Utility.FinalScore.CompareTo(a.Utility.FinalScore);
        if (cmp != 0) return cmp;
        // EffectiveTokens 降序
        var tokensA = GetEffectiveTokens(a);
        var tokensB = GetEffectiveTokens(b);
        cmp = tokensB.CompareTo(tokensA);
        if (cmp != 0) return cmp;
        // CandidateId 升序
        return StringComparer.OrdinalIgnoreCase.Compare(a.CandidateId, b.CandidateId);
    }

    /// <summary>
    /// non-mandatory 候选排序比较器（与 MandatoryComparison 同序，便于统一）。
    /// FinalScore 降序 → EffectiveTokens 降序 → CandidateId 升序（与原 OrderBy 链等价）。
    /// </summary>
    private static int NonMandatoryComparison(ContextCandidateEnvelope a, ContextCandidateEnvelope b)
        => MandatoryComparison(a, b);

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

    /// <summary>
    /// 获取候选的有效 token 数。
    /// 优先使用 CandidateTokenCost.ContentTokens（基于 IContextTokenizer 精确计算），
    /// 回退到 EstimatedTokens（length/4 粗估，仅用于兼容/诊断）。
    /// </summary>
    /// <remarks>
    /// 这是 Allocator 的权威 token 输入：中文/代码/JSON 场景下 EstimatedTokens 严重低估，
    /// 必须使用基于 tokenizer 的精确 TokenCost 才能避免预算超支。
    /// 实现统一委托 DecisionOutcomeRecomputer.GetEffectiveTokens（WP-E 单一真相源）。
    /// </remarks>
    private static int GetEffectiveTokens(ContextCandidateEnvelope envelope)
        => DecisionOutcomeRecomputer.GetEffectiveTokens(envelope);
}

// ---------------------------------------------------------------------------
// DefaultContentTruncator（默认内容截断器）
// ---------------------------------------------------------------------------

/// <summary>
/// 默认内容截断器。使用 content.Length/4 粗略估算 token，
/// 按 char 数截断（确保不超过 maxTokens * 4 字符）。
/// </summary>
/// <remarks>
/// 生产环境可替换为基于真实 tokenizer 的实现（如 tiktoken / BPE）。
/// 此默认实现保证：截断后内容的估算 token 数 <= maxTokens。
/// </remarks>
public sealed class DefaultContentTruncator : IContentTruncator
{
    /// <summary>按指定 token 数截断内容，返回截断后的内容和实际 token 数。</summary>
    public TruncationResult Truncate(string content, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxTokens <= 0) return new TruncationResult(string.Empty, 0, true);

        var estimatedTokens = Math.Max(1, content.Length / 4);
        if (estimatedTokens <= maxTokens)
        {
            return new TruncationResult(content, estimatedTokens, false);
        }

        var maxChars = maxTokens * 4;
        var truncated = content.Length > maxChars
            ? content.Substring(0, maxChars)
            : content;
        return new TruncationResult(truncated, maxTokens, true);
    }

    /// <summary>
    /// 计算内容的 token 数。使用 content.Length/4 粗略估算（与 <see cref="Truncate"/> 的估算口径一致）。
    /// </summary>
    public int CountTokens(string content, string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) return 0;
        return Math.Max(1, content.Length / 4);
    }
}

/// <summary>
/// 使用 <see cref="IContextTokenizerResolver"/> 的内容截断器。
/// </summary>
/// <remarks>
/// 替代 <see cref="DefaultContentTruncator"/>（content.Length/4 粗略估算），
/// 委托给 <see cref="IContextTokenizerResolver.TruncateForTokenBudget"/> 真正按 BPE/CJK 估算并截断，
/// 对中文 / JSON / 代码 / emoji 偏差小。
/// </remarks>
public sealed class TokenizerContentTruncator : IContentTruncator
{
    private readonly IContextTokenizerResolver _tokenizerResolver;
    private readonly string? _modelName;

    /// <summary>
    /// 构造 TokenizerContentTruncator。
    /// </summary>
    /// <param name="tokenizerResolver">tokenizer 解析器（非空）。</param>
    /// <param name="modelName">模型名（可选，传给 tokenizer 选择具体实现）。</param>
    public TokenizerContentTruncator(IContextTokenizerResolver tokenizerResolver, string? modelName = null)
    {
        _tokenizerResolver = tokenizerResolver ?? throw new ArgumentNullException(nameof(tokenizerResolver));
        _modelName = modelName;
    }

    /// <summary>
    /// 委托给 tokenizerResolver 真正按 token 预算截断。
    /// </summary>
    public TruncationResult Truncate(string content, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxTokens <= 0) return new TruncationResult(string.Empty, 0, true);

        var result = _tokenizerResolver.TruncateForTokenBudget(content, maxTokens, _modelName);
        return new TruncationResult(result.TruncatedContent, result.TokenCount, result.WasTruncated);
    }

    /// <summary>
    /// 计算内容的 token 数，委托给 <see cref="IContextTokenizerResolver.Estimate"/>。
    /// 与 <see cref="Truncate"/> 使用同一 tokenizer，保证计数与截断口径一致。
    /// </summary>
    public int CountTokens(string content, string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) return 0;
        var estimate = _tokenizerResolver.Estimate(content, modelName ?? _modelName);
        return estimate.TokenCount;
    }
}

// ---------------------------------------------------------------------------
// AgentContext Projector
// ---------------------------------------------------------------------------

/// <summary>
/// Agent Context Projector 默认骨架。
/// 从 DecisionResult + WorkingSet 投影为 AgentContextSnapshot。
/// </summary>
/// <remarks>
/// B-1 骨架：投影为单 section（"context"），包含所有 selected 候选的 Content 拼接。
/// B-2 将按 CandidateAllocationDecision.Section 分区投影。
/// 不访问 Store；不重新排序、过滤、截断或计分（仅格式投影）。
/// 当 IncludedTokens < EstimatedTokens 时，使用 IContentTruncator 真正截断 Content。
/// </remarks>
public sealed class AgentContextProjector : IAgentContextProjector
{
    private readonly IContentTruncator _contentTruncator;

    /// <summary>
    /// 构造 AgentContextProjector。
    /// </summary>
    /// <param name="contentTruncator">
    /// 内容截断器。null 时回退到 tokenizerResolver 或 <see cref="DefaultContentTruncator"/>。
    /// </param>
    /// <param name="tokenizerResolver">
    /// tokenizer 解析器（可选）。contentTruncator 为 null 且 tokenizerResolver 非空时，
    /// 使用 <see cref="TokenizerContentTruncator"/>（真正按 BPE/CJK 截断），否则回退到 <see cref="DefaultContentTruncator"/>。
    /// </param>
    /// <param name="modelName">tokenizer 使用的模型名（可选）。</param>
    public AgentContextProjector(
        IContentTruncator? contentTruncator = null,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? modelName = null)
    {
        // 优先级 contentTruncator > tokenizerResolver > DefaultContentTruncator
        _contentTruncator = contentTruncator
            ?? (tokenizerResolver is not null
                ? new TokenizerContentTruncator(tokenizerResolver, modelName)
                : new DefaultContentTruncator());
    }

    /// <summary>将决策结果 + 候选正文投影为 AgentContextSnapshot。</summary>
    public AgentContextSnapshot Project(ContextDecisionResult result, CandidateWorkingSet workingSet)
    {
        return Project(result, workingSet, context: null);
    }

    /// <summary>
    /// 从完整执行结果投影为 AgentContextSnapshot。
    /// </summary>
    /// <remarks>便捷重载：从 execution 提取 Decision + WorkingSet。</remarks>
    public AgentContextSnapshot Project(ContextDecisionExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return Project(execution.Decision, execution.WorkingSet, context: null);
    }

    /// <summary>
    /// 从完整执行结果 + 投影上下文投影为 AgentContextSnapshot。
    /// </summary>
    public AgentContextSnapshot Project(ContextDecisionExecutionResult execution, ProjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return Project(execution.Decision, execution.WorkingSet, context);
    }

    /// <summary>
    /// 将决策结果 + 候选正文 + 投影上下文投影为 AgentContextSnapshot。
    /// 使用 context.AgentSession（如有）而非伪造的 session ID。
    /// 按 CandidateAllocationDecision.Section 分区投影（而非单 section 拼接）。
    /// 当 IncludedTokens < EstimatedTokens 时截断 Content。
    /// </summary>
    public AgentContextSnapshot Project(ContextDecisionResult result, CandidateWorkingSet workingSet, ProjectionContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(workingSet);

        // 构建 CanonicalKey → AllocationDecision 索引，用于按 Section 分区
        var allocationByKey = result.AllocationDecisions
            .ToDictionary(d => d.CandidateKey, d => d);

        // 按 Section 分区投影
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

                    // 当 IncludedTokens < EstimatedTokens 时，真正截断 Content
                    var contentToAppend = material.Content;
                    if (item.IncludedTokens < item.Envelope.EstimatedTokens && item.IncludedTokens > 0)
                    {
                        var truncation = _contentTruncator.Truncate(material.Content, item.IncludedTokens);
                        contentToAppend = truncation.TruncatedContent;
                        sectionTokens += truncation.ActualTokens;
                    }
                    else
                    {
                        sectionTokens += item.IncludedTokens;
                    }

                    contentBuilder.Append(contentToAppend);
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

        // 使用真实 AgentSessionId（来自 ProjectionContext），而非伪造的 session
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
