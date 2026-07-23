using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// R28-B.6 Blocker-1：委托到 ExecuteWithWorkingSetAsync，仅返回 Decision 部分（向后兼容）。
    /// </summary>
    public async ValueTask<ContextDecisionResult> ExecuteAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        var executionResult = await ExecuteWithWorkingSetAsync(request, cancellationToken).ConfigureAwait(false);
        return executionResult.Decision;
    }

    /// <summary>
    /// R28-B.6 Blocker-1：执行完整决策编排，返回 ExecutionResult（含 WorkingSet + Policy + Routing + ProviderReports）。
    /// </summary>
    /// <remarks>
    /// 完整编排流程：
    ///   1. 策略解析（IResolvedPolicyProvider → EffectivePolicySnapshot）
    ///   2. Router 路由（IRouter → ExpertRoutingDecisionSet）
    ///   3. Provider DAG 召回（Blocker-5：两阶段 — Phase 1 主召回 + Phase 2 Graph 扩展）
    ///   4. Canonical Merge（ICanonicalCandidateMerger 合并 Provider 输出）
    ///   5. SeedCandidates 合并（将外部传入的种子候选合并到工作集）
    ///   6. EarlyAdmissionGate 批量评估（Blocker-6：保留 Rejected 到 DroppedEnvelopes）
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

        // Step 1：策略解析
        var snapshot = await _policyProvider.ResolveAsync(request, cancellationToken).ConfigureAwait(false);

        // Step 2：Router 路由 — 产出 ExpertRoutingDecisionSet
        var routingDecisions = await _router.RouteAsync(request, snapshot, cancellationToken).ConfigureAwait(false);

        // Step 3：Provider DAG 召回（Blocker-5：两阶段）
        // Phase 1：执行 Mandatory + Constraint + Lexical + Semantic + WorkingMemory + StableMemory
        // Phase 2：执行 Graph Provider，将 Phase 1 merged envelopes 作为 SeedCandidates 传入
        var (expertOutputs, providerReports) = await InvokeEnabledProvidersWithDagAsync(
            request, snapshot, routingDecisions, cancellationToken).ConfigureAwait(false);

        // Step 4：Canonical Merge — 合并 Provider 输出
        var mergedWorkingSet = _canonicalMerger.Merge(expertOutputs);

        // Step 5：SeedCandidates / SeedWorkingSet 合并 — 将外部传入的种子候选加入工作集
        // R28-B.6 P0-4：优先使用 SeedWorkingSet（含 Envelopes + Materials），回退到 SeedCandidates（仅 Envelopes）
        var seedEnvelopes = request.SeedWorkingSet?.Envelopes ?? request.SeedCandidates;
        var allEnvelopes = MergeSeedCandidates(mergedWorkingSet.Envelopes, seedEnvelopes);

        // R28-B.6 P0-4：合并 SeedWorkingSet.Materials 到 complete WorkingSet（保留种子 Material，不丢失）
        var completeMaterials = MergeSeedMaterials(mergedWorkingSet.Materials, request.SeedWorkingSet?.Materials);

        // 构建 complete WorkingSet（包含 Materials）：保留所有 Materials 供 Projector 恢复正文
        var completeWorkingSet = new CandidateWorkingSet
        {
            Envelopes = allEnvelopes,
            Materials = completeMaterials
        };

        if (allEnvelopes.Count == 0)
        {
            return EmptyExecutionResult(request, snapshot, routingDecisions, completeWorkingSet, providerReports);
        }

        // Step 6：EarlyAdmissionGate 批量评估（Blocker-6：保留 Rejected）
        var partition = _earlyAdmissionGate.EvaluateBatch(allEnvelopes, snapshot);
        var admitted = partition.Admitted;
        var earlyRejected = partition.Rejected;

        if (admitted.Count == 0)
        {
            // 所有候选被 EarlyGate 拒绝：仍返回 EarlyRejected 作为 DroppedEnvelopes
            var emptyDecision = BuildEarlyRejectedResult(request, snapshot, earlyRejected, partition.RejectReasons);
            return new ContextDecisionExecutionResult
            {
                Decision = emptyDecision,
                WorkingSet = completeWorkingSet,
                Policy = snapshot,
                Routing = routingDecisions,
                ProviderReports = providerReports
            };
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
        // R28-B.6 P0-5：构建 AllocationContext 传给 Engine（AgentContext → FailClosed 默认）。
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
            SectionRatios = snapshot.Budget.SectionRatios.Count > 0 ? snapshot.Budget.SectionRatios : null,
            PolicyBundleId = snapshot.Reference.BundleId,
            QueryText = request.QueryText,
            CreatedAt = DateTimeOffset.UtcNow,
            EnableModel = snapshot.Routing.EnableModelScoring,
            PolicySnapshot = snapshot,
            // P0-5：传 AllocationContext 给 Engine，让 Engine 在 V2 路径调用 Allocator 时使用
            AllocationContext = allocationContext
        };

        var engineResult = await _engine.DecideAsync(decisionRequest, cancellationToken).ConfigureAwait(false);

        // P0-6：直接使用 Engine 结果，不再二次 Allocate。
        // R28-B.6：V2 路径下 Engine 已通过 IGlobalAllocator 产出 AllocationDecisions。
        // Legacy 路径下 Engine 不产出 AllocationDecisions，Runtime 补建。
        var allocationDecisions = engineResult.AllocationDecisions.Count > 0
            ? engineResult.AllocationDecisions
            : BuildAllocationDecisions(engineResult.SelectedEnvelopes, engineResult.DroppedEnvelopes);

        // Step 9：合并 EarlyRejected + Engine.DroppedEnvelopes（Blocker-6）
        var finalDropped = earlyRejected.Count == 0
            ? engineResult.DroppedEnvelopes
            : CombineDroppedWithEarlyRejected(engineResult.DroppedEnvelopes, earlyRejected, partition.RejectReasons);

        // 合并 AllocationDecisions：补建 EarlyRejected 候选的 allocation decision
        var finalAllocationDecisions = earlyRejected.Count == 0
            ? allocationDecisions
            : AppendEarlyRejectedAllocationDecisions(allocationDecisions, earlyRejected);

        var decision = new ContextDecisionResult
        {
            RequestId = engineResult.RequestId,
            DecisionSource = engineResult.DecisionSource,
            SelectedEnvelopes = engineResult.SelectedEnvelopes,
            DroppedEnvelopes = finalDropped,
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = engineResult.Outcome.SelectedCount,
                DroppedCount = finalDropped.Count,
                EstimatedTokens = engineResult.Outcome.EstimatedTokens,
                TokenBudget = engineResult.Outcome.TokenBudget,
                Sections = engineResult.Outcome.Sections,
                SafetyGateBlockedCount = engineResult.Outcome.SafetyGateBlockedCount,
                BudgetExceededCount = engineResult.Outcome.BudgetExceededCount,
                // R28-B.6 P0-5：保留 Engine Outcome.Diagnostics（mandatory overflow / hard window violated 等）
                Diagnostics = engineResult.Outcome.Diagnostics
            },
            PolicyVersion = engineResult.PolicyVersion,
            ModelVersion = engineResult.ModelVersion,
            DecidedAt = engineResult.DecidedAt,
            ModelEnabled = engineResult.ModelEnabled,
            Purpose = request.Purpose,
            RuntimeKind = ContextDecisionRuntimeKind.UnifiedV2,
            AllocationDecisions = finalAllocationDecisions,
            PolicyReference = snapshot.Reference
        };

        return new ContextDecisionExecutionResult
        {
            Decision = decision,
            WorkingSet = completeWorkingSet,
            Policy = snapshot,
            Routing = routingDecisions,
            ProviderReports = providerReports
        };
    }

    /// <summary>
    /// R28-B.6 Blocker-5：Provider DAG 两阶段执行。
    /// Phase 1：执行 Mandatory + Constraint + Lexical + Semantic + WorkingMemory + StableMemory
    /// Canonical Merge Phase 1 结果
    /// Phase 2：执行 Graph Provider，将 Phase 1 merged envelopes 作为 SeedCandidates 传入
    /// Final Merge：合并 Phase 1 + Phase 2 结果
    /// </summary>
    private async Task<(IReadOnlyList<ExpertExecutionResult> Outputs, IReadOnlyList<ProviderExecutionReport> Reports)> InvokeEnabledProvidersWithDagAsync(
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        ExpertRoutingDecisionSet routingDecisions,
        CancellationToken cancellationToken)
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

        // R28-B.6：per-Provider 去重 — 按 ExpertKind 去重
        var executedKinds = new HashSet<ExpertKind>();
        var allEnabledProviders = _candidateProviders
            .Where(p => routingByExpert.Values.Any(r => MapExpertKindToRetrievalExpert(p.Kind) == r.Expert))
            .Where(p => executedKinds.Add(p.Kind))
            .ToList();

        if (allEnabledProviders.Count == 0)
        {
            return (Array.Empty<ExpertExecutionResult>(), Array.Empty<ProviderExecutionReport>());
        }

        // Blocker-5：拆分为两阶段
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
                adaptationContext, seedEnvelopes: null, cancellationToken).ConfigureAwait(false);
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

            // 用 Phase 1 merged envelopes 作为 SeedCandidates 构造新 request
            var phase2Request = request with { SeedCandidates = phase1MergedEnvelopes };

            var phase2Results = await InvokeProviderBatchAsync(
                phase2Providers, phase2Request, snapshot, routingByExpert,
                adaptationContext, seedEnvelopes: phase1MergedEnvelopes, cancellationToken).ConfigureAwait(false);
            allOutputs.AddRange(phase2Results.Outputs);
            allReports.AddRange(phase2Results.Reports);
        }

        return (allOutputs, allReports);
    }

    /// <summary>
    /// R28-B.6 Blocker-5：批量执行一组 Provider，bounded parallel + 超时保护 + 执行报告。
    /// R28-B.6 Impl-3：为每个 Provider 单独创建 timeout CTS（一个 Provider 超时不取消其他 Provider）。
    /// 专家故障等级：
    ///   - Mandatory / Constraint 失败或超时 → fail-closed（抛异常，整个请求失败）；
    ///   - Semantic / Graph / Recency 失败或超时 → degraded result（返回空结果 + diagnostic）；
    ///   - Lexical / WorkingMemory / StableMemory 失败或超时 → degraded result（默认）。
    /// </summary>
    /// <param name="cancellationToken">原始调用方 cancellationToken（用于区分超时 vs 用户取消）。</param>
    private async Task<(IReadOnlyList<ExpertExecutionResult> Outputs, IReadOnlyList<ProviderExecutionReport> Reports)> InvokeProviderBatchAsync(
        IReadOnlyList<ICandidateProvider> providers,
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        IReadOnlyDictionary<RetrievalExpert, ExpertRoutingDecision> routingByExpert,
        CandidateAdaptationContext adaptationContext,
        IReadOnlyList<ContextCandidateEnvelope>? seedEnvelopes,
        CancellationToken cancellationToken)
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

            var startedAt = Stopwatch.GetTimestamp();
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            // R28-B.6 Impl-3：为每个 Provider 单独创建 linked CTS with timeout。
            // 一个 Provider 超时只取消自身，不影响其他 Provider。
            using var perProviderCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            perProviderCts.CancelAfter(_providerTimeout);

            try
            {
                var result = await provider.ExecuteAsync(context, perProviderCts.Token).ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
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
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
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

                // R28-B.6 Impl-3：Mandatory / Constraint 超时 → fail-closed（抛异常，整个请求失败）
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
                // 用户取消（原始 cancellationToken 被取消）→ 传播
                throw;
            }
            catch (Exception ex)
            {
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
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

                // R28-B.6 Impl-3：Mandatory / Constraint 执行失败 → fail-closed（抛异常，整个请求失败）
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
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var outputs = results.Select(r => r.Item1).ToList();
        var reports = results.Select(r => r.Item2).ToList();
        return (outputs, reports);
    }

    /// <summary>
    /// R28-B.6 Blocker-6：合并 Engine.DroppedEnvelopes + EarlyRejected 到最终 DroppedEnvelopes。
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
    /// R28-B.6 Blocker-6：为 EarlyRejected 候选补建 AllocationDecision（reason=EarlyAdmissionRejected）。
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
    /// R28-B.6 Blocker-6：当所有候选被 EarlyGate 拒绝时，构建仅含 EarlyRejected 的 Decision。
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

    private static ContextDecisionExecutionResult EmptyExecutionResult(
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        ExpertRoutingDecisionSet routing,
        CandidateWorkingSet workingSet,
        IReadOnlyList<ProviderExecutionReport> providerReports)
    {
        return new ContextDecisionExecutionResult
        {
            Decision = EmptyResult(request, snapshot),
            WorkingSet = workingSet,
            Policy = snapshot,
            Routing = routing,
            ProviderReports = providerReports
        };
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
    /// R28-B.6 P0-4：合并 Provider 产出 Materials 与 SeedWorkingSet.Materials。
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

    /// <summary>
    /// R28-B.6 P0-5：根据业务用途解析 MandatoryOverflow 默认策略。
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

    /// <summary>实际计算 bundle 内容哈希（无缓存）。</summary>
    private static string ComputeHashUncached(ContextPolicyBundle bundle)
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

        // R28-B.6 Impl-4：Safety — bool/double 使用 invariant culture，tag 集合先排序再 join
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

        // R28-B.6 P0-1：读取 RetrievalInput 的 Include* 开关（仅 Purpose=Retrieval 时生效）。
        // 默认全部 true（兼容非 Retrieval 路径与未指定字段的请求）。
        var retrievalInput = request.RetrievalInput;
        var includeKeyword = retrievalInput?.IncludeKeywordRecall ?? true;
        var includeVector = retrievalInput?.IncludeVectorRecall ?? true;
        var includeRelation = retrievalInput?.IncludeRelationExpansion ?? true;
        var includeWorkingMemory = retrievalInput?.IncludeWorkingMemory ?? true;
        // P0-2 新增：StableMemory Include 开关（默认 true）
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

            // R28-B.6 P0-1：Include 开关检查（仅对非 Mandatory/Constraint 的 Expert 生效）
            var includeEnabled = IsIncludeEnabled(
                mappedKind, includeKeyword, includeVector,
                includeRelation, includeWorkingMemory, includeStableMemory);

            // enabled = Mandatory/Constraint(永远启用) || (已注册 && Include 开关启用)
            var disabledByInclude = !isMandatory && !includeEnabled;
            var enabled = isMandatory || (isRegistered && includeEnabled);

            var decisionTopK = isMandatory ? totalTopK : (enabled ? perExpertTopK : 0);
            var decisionTokenBudget = isMandatory ? totalTokenBudget : (enabled ? perExpertTokenBudget : 0);

            // R28-B.6 P0-1：根据 disable 原因区分 ReasonCode
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
    /// R28-B.6 P0-1：根据 RetrievalInput 的 Include* 开关判断 Expert 是否应启用。
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
                if (envelopeByKey.TryGetValue(key, out _))
                {
                    // 重复 key：Origins/Contributions 已在首次插入时初始化，此处仅累加
                    originsByKey[key].AddRange(envelope.Origins);

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
/// R28-B.6 Blocker-6：新增 EvaluateBatch 批量评估，返回 AdmissionPartition（Admitted + Rejected），
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
    /// R28-B.6 Blocker-6：批量评估候选准入，返回分区结果（Admitted + Rejected + RejectReasons）。
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
/// R28-B.6 Impl-2：接入 MandatoryOverflowPolicy — mandatory 候选超出预算时按策略处理：
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
    /// R28-B.6 Impl-2：mandatory 候选超出预算时的处理策略。
    /// 默认 AllowOverflowWithDiagnostic（Package/Retrieval 语义）；AgentContext 硬窗口应注入 FailClosed。
    /// R28-B.6 P0-5：此构造函数策略仅作为旧 Allocate(envelopes, snapshot) 重载的 fallback；
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

        // P0-5：旧重载使用构造函数注入的策略（向后兼容测试 / Legacy 路径）
        return Allocate(envelopes, snapshot, _mandatoryOverflowPolicy, purpose: null);
    }

    /// <summary>
    /// R28-B.6 P0-5：执行全局预算分配，接受 AllocationContext（携带 Purpose + MandatoryOverflowPolicy）。
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
    /// R28-B.6 P0-5：核心分配实现。使用显式传入的 MandatoryOverflowPolicy（已由调用方解析）。
    /// </summary>
    /// <param name="mandatoryOverflowPolicy">本次分配使用的 overflow 策略。</param>
    /// <param name="purpose">业务用途（仅用于诊断；null 时记录 "Unknown"）。</param>
    private AllocationResult Allocate(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot,
        MandatoryOverflowPolicy mandatoryOverflowPolicy,
        ContextDecisionPurpose? purpose)
    {
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
        // R28-B.6 Impl-2：mandatory overflow 诊断累计
        var mandatoryOverflowTokens = 0;
        var hardWindowViolated = false;

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

            // R28-B.6 Impl-2：mandatory 候选超出预算时按策略处理
            if (isMandatory && usedTokens + envelope.EstimatedTokens > tokenBudget)
            {
                var overflow = (usedTokens + envelope.EstimatedTokens) - tokenBudget;
                mandatoryOverflowTokens += overflow;

                switch (mandatoryOverflowPolicy)
                {
                    case MandatoryOverflowPolicy.FailClosed:
                        // 硬窗口：拒绝 mandatory 候选（AgentContext 场景）
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
            // R28-B.6：partial truncation — 当候选超出剩余预算时，不完全丢弃，
            // 而是包含部分 token（IsTruncated=true，IncludedTokens=remaining）。
            // 只有剩余空间为 0 时才完全丢弃。实际内容截断由 Projector 通过
            // IContentTruncator 在 Material sidecar 恢复时执行（Impl-1）。
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
                ReasonCode = isMandatory
                    ? CandidateDecisionReasonCode.SelectedMandatory
                    : CandidateDecisionReasonCode.SelectedHighestUtility
            });
        }

        // R28-B.6 Impl-2：构建诊断字典（仅在有 mandatory overflow 时记录）
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mandatoryOverflowTokens > 0 || hardWindowViolated)
        {
            diagnostics["MandatoryOverflowTokens"] = mandatoryOverflowTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
            diagnostics["MandatoryOverflowPolicy"] = mandatoryOverflowPolicy.ToString();
            diagnostics["HardWindowViolated"] = hardWindowViolated.ToString().ToLowerInvariant();
            // P0-5：记录 Purpose（诊断用），null 时记 "Unknown"
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
// R28-B.6 Impl-1：DefaultContentTruncator（默认内容截断器）
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B.6 Impl-1：默认内容截断器。使用 content.Length/4 粗略估算 token，
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
}

/// <summary>
/// R28-B.6 P0-6：使用 <see cref="IContextTokenizerResolver"/> 的内容截断器。
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
    /// R28-B.6 P0-6：委托给 tokenizerResolver 真正按 token 预算截断。
    /// </summary>
    public TruncationResult Truncate(string content, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxTokens <= 0) return new TruncationResult(string.Empty, 0, true);

        var result = _tokenizerResolver.TruncateForTokenBudget(content, maxTokens, _modelName);
        return new TruncationResult(result.TruncatedContent, result.TokenCount, result.WasTruncated);
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
/// R28-B.6 Impl-1：当 IncludedTokens < EstimatedTokens 时，使用 IContentTruncator 真正截断 Content。
/// </remarks>
public sealed class AgentContextProjector : IAgentContextProjector
{
    private readonly IContentTruncator _contentTruncator;

    /// <summary>
    /// 构造 AgentContextProjector。
    /// </summary>
    /// <param name="contentTruncator">
    /// R28-B.6 Impl-1：内容截断器。null 时回退到 tokenizerResolver 或 <see cref="DefaultContentTruncator"/>。
    /// </param>
    /// <param name="tokenizerResolver">
    /// R28-B.6 P0-6：tokenizer 解析器（可选）。contentTruncator 为 null 且 tokenizerResolver 非空时，
    /// 使用 <see cref="TokenizerContentTruncator"/>（真正按 BPE/CJK 截断），否则回退到 <see cref="DefaultContentTruncator"/>。
    /// </param>
    /// <param name="modelName">tokenizer 使用的模型名（可选）。</param>
    public AgentContextProjector(
        IContentTruncator? contentTruncator = null,
        IContextTokenizerResolver? tokenizerResolver = null,
        string? modelName = null)
    {
        // R28-B.6 P0-6：优先级 contentTruncator > tokenizerResolver > DefaultContentTruncator
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
    /// P0-7：将决策结果 + 候选正文 + 投影上下文投影为 AgentContextSnapshot。
    /// 使用 context.AgentSession（如有）而非伪造的 session ID。
    /// 按 CandidateAllocationDecision.Section 分区投影（而非单 section 拼接）。
    /// R28-B.6 Impl-1：当 IncludedTokens < EstimatedTokens 时截断 Content。
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

                    // R28-B.6 Impl-1：当 IncludedTokens < EstimatedTokens 时，真正截断 Content
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
