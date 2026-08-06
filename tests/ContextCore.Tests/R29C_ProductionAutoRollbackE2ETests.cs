using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

// ===========================================================================
// 生产环境自动回滚验证（shadow 流量 + 真实 metrics）— 端到端验收测试
//
// 目标：把 AuthoritativeRuntime（Retrieval + Package）的 shadow / Mixed / sampled shadow
// 路径与 DefaultCanaryMetricsCollector + CanaryProgressionService 完整串联起来，
// 在真实流量（RetrieveAsync / BuildDetailedAsync 多次调用）下验证自动回滚决策。
//
// 与既有测试的区别：
// - 既有验收测试只验证 RecordObservation 被调用
// （TotalObservations >= 1），从未推进到 CanaryProgressionService.AdvanceAsync。
// - QualityScore 测试用手构 experimentMetrics dict 直接调用 AdvanceAsync，
// 未走 AuthoritativeRuntime → RecordObservation → ToExperimentMetrics 真实链路。
// - 本文件验证：真实 V2 ExecutionResult → CanaryQualityScoreCalculator →
// RecordObservation(qualityScore) → ToExperimentMetrics → AdvanceAsync → 回滚决策。
//
// 设计原则：
// - 使用真实组件（AuthoritativeRetrievalRuntime、AuthoritativePackageRuntime、
// ShadowDecisionRuntime、RetrievalResultProjector、PackageResultProjector、
// DefaultCanaryMetricsCollector、CanaryProgressionService、CutoverController、
// InMemoryPipelineRunStore、CanaryAcceptanceTimeProvider）。
// - 仅 stub IContextDecisionRuntime（V2 Runtime）和 IContextStore（Legacy 数据源）。
// - 所有代码注释使用中文。
// ===========================================================================

// ---------------------------------------------------------------------------
// 测试辅助：QueueDecisionRuntime — 按调用顺序返回预设结果（用于模拟渐进退化）
// ---------------------------------------------------------------------------

/// <summary>
/// 队列驱动的 IContextDecisionRuntime 桩：每次 ExecuteAsync / ExecuteWithWorkingSetAsync
/// 按入队顺序出队一个结果返回。可模拟 V2 产出质量随调用次数渐进退化的场景。
/// </summary>
internal sealed class QueueDecisionRuntime : IContextDecisionRuntime
{
    private readonly Queue<ContextDecisionResult> _results;
    public int ExecuteCallCount { get; private set; }
    public ContextDecisionRuntimeRequest? LastRequest { get; private set; }

    public QueueDecisionRuntime(params ContextDecisionResult[] results)
    {
        _results = new Queue<ContextDecisionResult>(results);
    }

    public ValueTask<ContextDecisionResult> ExecuteAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ExecuteCallCount++;
        LastRequest = request;
        if (_results.Count == 0)
        {
            throw new InvalidOperationException(
                "QueueDecisionRuntime: 队列已空，未提供足够预设结果。");
        }
        return ValueTask.FromResult(_results.Dequeue());
    }

    public ValueTask<ContextDecisionExecutionResult> ExecuteWithWorkingSetAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ExecuteCallCount++;
        LastRequest = request;
        if (_results.Count == 0)
        {
            throw new InvalidOperationException(
                "QueueDecisionRuntime: 队列已空，未提供足够预设结果。");
        }
        var result = _results.Dequeue();
        return ValueTask.FromResult(R28BTestHelpers.MakeExecutionResult(result));
    }

    public int RemainingResults => _results.Count;
}

// ---------------------------------------------------------------------------
// 测试辅助：E2E Helpers — 构建 E2E 测试组件与请求
// ---------------------------------------------------------------------------

internal static class R29C_E2E_Helpers
{
    /// <summary>构建带 canaryRunId metadata 的 Retrieval 请求。</summary>
    public static ContextRetrievalRequest BuildRetrievalRequest(string operationId, string runId)
    {
        return new ContextRetrievalRequest
        {
            OperationId = operationId,
            WorkspaceId = "ws-e2e",
            CollectionId = "col-e2e",
            QueryText = "query-" + operationId,
            TopK = 10,
            TokenBudget = 4096,
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = runId
            }
        };
    }

    /// <summary>构建带 canaryRunId metadata 的 Package 请求。</summary>
    public static ContextPackageRequest BuildPackageRequest(string operationId, string runId)
    {
        return new ContextPackageRequest
        {
            OperationId = operationId,
            RequestId = operationId,
            WorkspaceId = "ws-e2e-pkg",
            CollectionId = "col-e2e-pkg",
            QueryText = "query-pkg-" + operationId,
            TokenBudget = 4096,
            Mode = ContextPackageMode.None,
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = runId
            }
        };
    }

    /// <summary>
    /// 构建包含 N 个高 FinalScore 候选的 ContextDecisionResult（高质量产出）。
    /// finalScore ≈ 0.85，tokenBudget 满足 → sectionCoverage=1.0, relevance=0.85 → score≈0.925。
    /// </summary>
    public static ContextDecisionResult BuildHealthyResult(string requestId, int candidateCount = 4)
    {
        var envelopes = Enumerable.Range(0, candidateCount)
            .Select(i => R28BTestHelpers.MakeEnvelope(
                candidateId: $"c-healthy-{requestId}-{i}",
                source: ContextCandidateSource.Semantic,
                score: 0.85,
                tokens: 200))
            .ToArray();
        return R28BTestHelpers.MakeResult(
            requestId: requestId,
            selected: envelopes,
            estimatedTokens: candidateCount * 200,
            tokenBudget: 4096);
    }

    /// <summary>
    /// 构建包含低 FinalScore 候选的 ContextDecisionResult（低质量产出）。
    /// finalScore ≈ 0.05 → relevance=0.05 → score≈0.525（触发 MinQualityScore=0.3 不会，
    /// 但 MinQualityScore=0.6 会触发）。
    /// </summary>
    public static ContextDecisionResult BuildLowRelevanceResult(string requestId, int candidateCount = 4)
    {
        var envelopes = Enumerable.Range(0, candidateCount)
            .Select(i => R28BTestHelpers.MakeEnvelope(
                candidateId: $"c-low-{requestId}-{i}",
                source: ContextCandidateSource.Semantic,
                score: 0.05,
                tokens: 200))
            .ToArray();
        return R28BTestHelpers.MakeResult(
            requestId: requestId,
            selected: envelopes,
            estimatedTokens: candidateCount * 200,
            tokenBudget: 4096);
    }

    /// <summary>
    /// 构建空候选 + 极低 token 利用率的 ContextDecisionResult（极低质量产出）。
    /// SelectedEnvelopes 为空 → relevance=0；effectiveTokens/TokenBudget 极低 → coverage≈0；
    /// score≈0.0（远低于任何 MinQualityScore 阈值）。
    /// </summary>
    public static ContextDecisionResult BuildEmptyResult(string requestId)
    {
        return R28BTestHelpers.MakeResult(
            requestId: requestId,
            selected: Array.Empty<ContextCandidateEnvelope>(),
            estimatedTokens: 0,
            tokenBudget: 4096);
    }

    /// <summary>
    /// 构建启用 quality_score 阈值的 CanaryGateOptions（MinQualityScore = 默认 0.3）。
    /// 使用 99→100 阶梯（初始 99% 确保绝大多数 operationId 走 V2 Mixed mode 触发 RecordObservation），
    /// 最小观察期 1 秒。
    /// </summary>
    /// <remarks>
    /// 注意：PercentageLadder[0] 决定 InitializeCanary 后的初始 cutover 百分比。
    /// 使用 99 而非 1 是因为 CutoverController.ShouldUseV2 基于 operationId 哈希取模：
    /// - 1% cutover → 仅 1% 的 operationId 走 V2（5 个请求中几乎必然 0 个走 V2）
    /// - 99% cutover → 99% 的 operationId 走 V2 Mixed mode（5 个请求几乎必然全部走 V2）
    /// <para>
    /// 关键：MaxDivergenceRate 设为 2.0（>1.0 上限）以禁用 parity 阈值回滚。
    /// 原因：测试用 CallTrackingContextStore（空 Legacy 数据源），Legacy 路径产出空候选，
    /// 与健康 V2 候选完全发散 → Jaccard=0 → DivergenceRate=1.0。若不放宽阈值，
    /// 任何健康 V2 请求都会被 parity 检查误判为回滚，无法验证 quality_score 推进路径。
    /// 本测试文件聚焦 quality_score + V2 错误率回滚场景，parity 由既有覆盖测试验证。
    /// </para>
    /// </remarks>
    public static CanaryGateOptions BuildOptionsWithQuality(double minQualityScore = 0.3)
    {
        return new CanaryGateOptions
        {
            PercentageLadder = [99, 100],
            MinObservationPeriod = TimeSpan.FromSeconds(1),
            // 关键：放宽 parity 阈值（>1.0 上限）禁用此检查；本测试聚焦 quality_score
            MaxDivergenceRate = 2.0,
            MinQualityScore = minQualityScore
        };
    }

    /// <summary>装配完整的 AuthoritativeRetrievalRuntime + Collector + Cutover + Service 串联。</summary>
    public static (
        AuthoritativeRetrievalRuntime Runtime,
        DefaultCanaryMetricsCollector Collector,
        CutoverController Cutover,
        InMemoryPipelineRunStore Store,
        CanaryProgressionService Service,
        CanaryAcceptanceTimeProvider Time) BuildRetrievalE2EStack(
            IContextDecisionRuntime v2Runtime,
            int cutoverPercentage,
            CanaryGateOptions options)
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var shadowRuntime = new ShadowDecisionRuntime(v2Runtime, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var collector = new DefaultCanaryMetricsCollector();
        var cutover = new CutoverController(cutoverPercentage);
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, v2Runtime, shadowRuntime, projector,
            cutover,
            canaryMetricsCollector: collector);
        var store = new InMemoryPipelineRunStore();
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var service = new CanaryProgressionService(store, cutover, options, time);
        return (runtime, collector, cutover, store, service, time);
    }

    /// <summary>装配完整的 AuthoritativePackageRuntime + Collector + Cutover + Service 串联。</summary>
    public static (
        AuthoritativePackageRuntime Runtime,
        DefaultCanaryMetricsCollector Collector,
        CutoverController Cutover,
        InMemoryPipelineRunStore Store,
        CanaryProgressionService Service,
        CanaryAcceptanceTimeProvider Time) BuildPackageE2EStack(
            IContextDecisionRuntime v2Runtime,
            int cutoverPercentage,
            CanaryGateOptions options)
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyBuilder = new BasicContextPackageBuilder(trackingStore);
        var shadowRuntime = new ShadowDecisionRuntime(v2Runtime, new DecisionExperimentPlane());
        var projector = new PackageResultProjector();
        var collector = new DefaultCanaryMetricsCollector();
        var cutover = new CutoverController(cutoverPercentage);
        var runtime = new AuthoritativePackageRuntime(
            legacyBuilder, v2Runtime, shadowRuntime, projector,
            cutover,
            canaryMetricsCollector: collector);
        var store = new InMemoryPipelineRunStore();
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var service = new CanaryProgressionService(store, cutover, options, time);
        return (runtime, collector, cutover, store, service, time);
    }
}

// ===========================================================================
// 测试类 1：RetrievalE2EAutoRollbackTests
// 验证 Retrieval 路径从 AuthoritativeRetrievalRuntime.RetrieveAsync →
// RecordObservation → CanaryProgressionService.AdvanceAsync → 回滚决策
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("R29-C")]
[TestCategory("R29-C-4")]
public sealed class RetrievalE2EAutoRollbackTests
{
    // ===========================================================================
    // 1. HealthyV2_SeveralRequests_AdvancesCanary
    // 验证：Mixed mode（99%）下，多次健康 V2 请求 + 推进时间后，CanaryProgressionService
    // 从 99% → 100% 推进。证明 E2E 链路联通（RecordObservation → ToExperimentMetrics → AdvanceAsync）。
    // ===========================================================================
    [TestMethod]
    public async Task HealthyV2_SeveralRequests_AdvancesCanary()
    {
        var runId = "run-e2e-retrieval-healthy";
        var options = R29C_E2E_Helpers.BuildOptionsWithQuality(minQualityScore: 0.3);
        var stubV2 = new QueueDecisionRuntime(
            R29C_E2E_Helpers.BuildHealthyResult("op-1"),
            R29C_E2E_Helpers.BuildHealthyResult("op-2"),
            R29C_E2E_Helpers.BuildHealthyResult("op-3"),
            R29C_E2E_Helpers.BuildHealthyResult("op-4"),
            R29C_E2E_Helpers.BuildHealthyResult("op-5"));
        var (runtime, collector, cutover, store, service, time) =
            R29C_E2E_Helpers.BuildRetrievalE2EStack(stubV2, cutoverPercentage: 99, options);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, runId);
        service.InitializeCanary(runId);
        Assert.AreEqual(99, cutover.CutoverPercentage, "初始化后应为 99%（PercentageLadder[0]）");

        // 5 次健康请求 → 上报高质量样本（99% cutover 下几乎必然全部走 V2 Mixed mode）
        for (var i = 1; i <= 5; i++)
        {
            await runtime.RetrieveAsync(
                R29C_E2E_Helpers.BuildRetrievalRequest($"op-{i}", runId),
                CancellationToken.None);
        }

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.IsTrue(metrics.TotalObservations >= 1,
            $"应有至少 1 个观察样本（实际 {metrics.TotalObservations}；99% cutover 下 5 个 operationId 几乎必然全部走 V2）");
        Assert.IsTrue(metrics.AverageQualityScore > 0.5,
            $"健康 V2 产出的 quality_score 应 > 0.5（实际 {metrics.AverageQualityScore:F4}）");

        // 推进时间 + 调用 AdvanceAsync → 应推进到 100%
        time.Advance(TimeSpan.FromSeconds(2));
        var baseline = CanaryAcceptanceHelpers.HealthyBaseline;
        var experiment = metrics.ToExperimentMetrics();
        var result = await service.AdvanceAsync(
            runId, "t-e2e-healthy-001", idempotencyKey: "idem-e2e-healthy-001",
            baseline, experiment);
        Assert.AreEqual(CanaryProgressionDecision.Advance, result.Decision,
            $"健康质量分应推进；rationale={result.Rationale}");
        Assert.AreEqual(100, result.CurrentPercentage, "应推进到 100%");
    }

    // ===========================================================================
    // 2. LowQualityV2_SeveralRequests_TriggersRollback
    // 验证：Mixed mode（99%）下，多次低质量 V2 请求（空候选 → score≈0.0）→
    // CanaryProgressionService 在 AdvanceAsync 中触发回滚（quality_score < MinQualityScore=0.3）。
    // 完整链路：AuthoritativeRetrievalRuntime → CanaryQualityScoreCalculator →
    // RecordObservation → ToExperimentMetrics → AdvanceAsync → Rollback。
    // ===========================================================================
    [TestMethod]
    public async Task LowQualityV2_SeveralRequests_TriggersRollback()
    {
        var runId = "run-e2e-retrieval-lowquality";
        var options = R29C_E2E_Helpers.BuildOptionsWithQuality(minQualityScore: 0.3);
        var stubV2 = new QueueDecisionRuntime(
            R29C_E2E_Helpers.BuildEmptyResult("op-1"),
            R29C_E2E_Helpers.BuildEmptyResult("op-2"),
            R29C_E2E_Helpers.BuildEmptyResult("op-3"),
            R29C_E2E_Helpers.BuildEmptyResult("op-4"),
            R29C_E2E_Helpers.BuildEmptyResult("op-5"));
        var (runtime, collector, cutover, store, service, time) =
            R29C_E2E_Helpers.BuildRetrievalE2EStack(stubV2, cutoverPercentage: 99, options);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, runId);
        service.InitializeCanary(runId);

        // 5 次空候选请求 → 上报零质量样本
        for (var i = 1; i <= 5; i++)
        {
            await runtime.RetrieveAsync(
                R29C_E2E_Helpers.BuildRetrievalRequest($"op-{i}", runId),
                CancellationToken.None);
        }

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.IsTrue(metrics.TotalObservations >= 1,
            $"应有至少 1 个观察样本（实际 {metrics.TotalObservations}）");
        Assert.IsTrue(metrics.AverageQualityScore < 0.3,
            $"空候选产出的 quality_score 应 < 0.3（实际 {metrics.AverageQualityScore:F4}）");

        // 推进时间 + 调用 AdvanceAsync → 应触发回滚
        time.Advance(TimeSpan.FromSeconds(2));
        var baseline = CanaryAcceptanceHelpers.HealthyBaseline;
        var experiment = metrics.ToExperimentMetrics();
        var result = await service.AdvanceAsync(
            runId, "t-e2e-lowquality-001", idempotencyKey: "idem-e2e-lowquality-001",
            baseline, experiment);
        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision,
            $"低质量分应触发回滚；rationale={result.Rationale}");
        Assert.AreEqual(0, cutover.CutoverPercentage, "回滚后应为 0%");

        // 验证审计记录已持久化
        var transitions = await store.ListStageTransitionsByRunAsync(runId);
        Assert.IsTrue(transitions.Any(t => t.Decision == CanaryProgressionDecision.Rollback),
            "store 中应存在 Rollback 审计记录");
    }

    // ===========================================================================
    // 3. V2Failure_RecordsV2Error_TriggersRollback
    // 验证：V2 Runtime 抛异常时（fail-open 回退 Legacy），RecordObservation 被调用且
    // v2Succeeded=false → V2ErrorRate 高 → AdvanceAsync 触发回滚。
    // 完整链路：ThrowingDecisionRuntime → Exception catch → RecordCanaryObservation
    // → V2ErrorRate > 阈值 → AdvanceAsync → Rollback。
    // ===========================================================================
    [TestMethod]
    public async Task V2Failure_RecordsV2Error_TriggersRollback()
    {
        var runId = "run-e2e-retrieval-v2failure";
        // 错误率回滚阈值默认 5%；这里所有 V2 请求都失败 → V2ErrorRate=1.0 → 触发回滚
        // MinQualityScore=0.0 禁用质量分阈值，仅靠 V2ErrorRate 触发回滚
        var options = R29C_E2E_Helpers.BuildOptionsWithQuality(minQualityScore: 0.0);
        var throwingV2 = new ThrowingDecisionRuntime(new InvalidOperationException("V2 down"));
        var (runtime, collector, cutover, store, service, time) =
            R29C_E2E_Helpers.BuildRetrievalE2EStack(throwingV2, cutoverPercentage: 99, options);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, runId);
        service.InitializeCanary(runId);

        // 5 次 V2 失败请求
        for (var i = 1; i <= 5; i++)
        {
            await runtime.RetrieveAsync(
                R29C_E2E_Helpers.BuildRetrievalRequest($"op-fail-{i}", runId),
                CancellationToken.None);
        }

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.IsTrue(metrics.TotalObservations >= 1,
            $"应有至少 1 个观察样本（实际 {metrics.TotalObservations}）");
        Assert.AreEqual(1.0, metrics.V2ErrorRate, 0.001,
            "所有 V2 请求失败时 V2ErrorRate 应为 1.0");

        time.Advance(TimeSpan.FromSeconds(2));
        var baseline = CanaryAcceptanceHelpers.HealthyBaseline;
        var experiment = metrics.ToExperimentMetrics();
        var result = await service.AdvanceAsync(
            runId, "t-e2e-v2fail-001", idempotencyKey: "idem-e2e-v2fail-001",
            baseline, experiment);
        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision,
            $"V2ErrorRate=1.0 应触发回滚；rationale={result.Rationale}");
        Assert.AreEqual(0, cutover.CutoverPercentage, "回滚后应为 0%");
    }

    // ===========================================================================
    // 3b. AdvanceRollback_ReturnsRealSettlementSemantics
    // 验证：AdvanceAsync 触发 Rollback 后，返回结果反映真实结算状态
    // （CurrentPercentage=0 / Applied=true / LocalApplied / DurableApplied /
    //  OverridePersisted / OperatorActionRequired），而非回滚前档位 + Applied=false。
    // ===========================================================================
    [TestMethod]
    public async Task AdvanceRollback_ReturnsRealSettlementSemantics()
    {
        var runId = "run-rollback-semantics";
        var options = R29C_E2E_Helpers.BuildOptionsWithQuality(minQualityScore: 0.0);
        var throwingV2 = new ThrowingDecisionRuntime(new InvalidOperationException("V2 down"));
        var (runtime, collector, cutover, store, service, time) =
            R29C_E2E_Helpers.BuildRetrievalE2EStack(throwingV2, cutoverPercentage: 99, options);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, runId);
        service.InitializeCanary(runId);

        // 全部 V2 失败 → V2ErrorRate=1.0 → 触发回滚。
        for (var i = 1; i <= 5; i++)
        {
            await runtime.RetrieveAsync(
                R29C_E2E_Helpers.BuildRetrievalRequest($"op-rs-{i}", runId),
                CancellationToken.None);
        }

        var metrics = collector.GetAggregatedMetrics(runId);
        time.Advance(TimeSpan.FromSeconds(2));
        var result = await service.AdvanceAsync(
            runId, "t-rollback-semantics-001", idempotencyKey: null,
            CanaryAcceptanceHelpers.HealthyBaseline, metrics.ToExperimentMetrics());

        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision,
            $"V2ErrorRate=1.0 应触发回滚；rationale={result.Rationale}");
        Assert.AreEqual(0, result.CurrentPercentage, "回滚后 CurrentPercentage 应为 0%（非回滚前档位）。");
        Assert.IsTrue(result.Applied, "回滚应视为已应用（本地路由已切 0%）。");
        Assert.IsTrue(result.LocalApplied, "LocalApplied 应为 true（本地已切 0%）。");
        Assert.IsTrue(result.DurableApplied, "回退路径（无 applier）应视为本地已持久化 0%。");
        Assert.IsFalse(result.OverridePersisted, "未配置 Kill Switch 存储时 OverridePersisted 应为 false。");
        Assert.IsFalse(result.OperatorActionRequired, "无覆盖且 DB 已持久化 → 无需人工介入。");
        Assert.AreEqual(0, cutover.CutoverPercentage, "回滚后本地路由应为 0%。");
    }

    // ===========================================================================
    // 4. SampledShadowPath_RecordsObservationAndCanRollback
    // 验证：100% cutover + EnableSampledShadow=true + ShadowSampleRate=1.0 时，
    // 所有请求走 V2 权威路径 + 旁路执行 Legacy 对照 + RecordObservation 上报样本。
    // 低质量 V2 产出 → CanaryProgressionService 触发回滚。
    // ===========================================================================
    [TestMethod]
    public async Task SampledShadowPath_RecordsObservationAndCanRollback()
    {
        var runId = "run-e2e-retrieval-sampled-shadow";
        var options = R29C_E2E_Helpers.BuildOptionsWithQuality(minQualityScore: 0.3);
        var stubV2 = new QueueDecisionRuntime(
            R29C_E2E_Helpers.BuildEmptyResult("op-ss-1"),
            R29C_E2E_Helpers.BuildEmptyResult("op-ss-2"),
            R29C_E2E_Helpers.BuildEmptyResult("op-ss-3"),
            R29C_E2E_Helpers.BuildEmptyResult("op-ss-4"),
            R29C_E2E_Helpers.BuildEmptyResult("op-ss-5"));
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var collector = new DefaultCanaryMetricsCollector();
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(), new ShadowGateEvaluator(),
            new CutoverConfiguration
            {
                CutoverPercentage = 100,
                EnableSampledShadow = true,
                ShadowSampleRate = 1.0
            });
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100),
            shadowGate: null,
            experimentPlane: integration,
            canaryMetricsCollector: collector);
        var store = new InMemoryPipelineRunStore();
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var service = new CanaryProgressionService(store,
            new CutoverController(cutoverPercentage: 100), options, time);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, runId);
        service.InitializeCanary(runId);

        // 5 次空候选请求（sampled shadow 路径）
        for (var i = 1; i <= 5; i++)
        {
            await runtime.RetrieveAsync(
                R29C_E2E_Helpers.BuildRetrievalRequest($"op-ss-{i}", runId),
                CancellationToken.None);
        }

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.AreEqual(5, metrics.TotalObservations,
            "sampled shadow 路径应上报 5 个样本");
        Assert.IsTrue(metrics.AverageQualityScore < 0.3,
            $"空候选产出的 quality_score 应 < 0.3（实际 {metrics.AverageQualityScore:F4}）");

        time.Advance(TimeSpan.FromSeconds(2));
        var baseline = CanaryAcceptanceHelpers.HealthyBaseline;
        var experiment = metrics.ToExperimentMetrics();
        var result = await service.AdvanceAsync(
            runId, "t-e2e-sampled-001", idempotencyKey: "idem-e2e-sampled-001",
            baseline, experiment);
        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision,
            $"sampled shadow 低质量应触发回滚；rationale={result.Rationale}");
    }
}

// ===========================================================================
// 测试类 2：PackageE2EAutoRollbackTests
// 验证 Package 路径从 AuthoritativePackageRuntime.BuildDetailedAsync →
// RecordObservation → CanaryProgressionService.AdvanceAsync → 回滚决策
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("R29-C")]
[TestCategory("R29-C-4")]
public sealed class PackageE2EAutoRollbackTests
{
    // ===========================================================================
    // 1. HealthyV2_PackagePath_AdvancesCanary
    // 验证：Package Mixed mode（99%）下，健康 V2 产出 → 上报高质量样本 → AdvanceAsync 推进。
    // ===========================================================================
    [TestMethod]
    public async Task HealthyV2_PackagePath_AdvancesCanary()
    {
        var runId = "run-e2e-pkg-healthy";
        var options = R29C_E2E_Helpers.BuildOptionsWithQuality(minQualityScore: 0.3);
        var stubV2 = new QueueDecisionRuntime(
            R29C_E2E_Helpers.BuildHealthyResult("pkg-op-1"),
            R29C_E2E_Helpers.BuildHealthyResult("pkg-op-2"),
            R29C_E2E_Helpers.BuildHealthyResult("pkg-op-3"));
        var (runtime, collector, cutover, store, service, time) =
            R29C_E2E_Helpers.BuildPackageE2EStack(stubV2, cutoverPercentage: 99, options);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, runId);
        service.InitializeCanary(runId);

        for (var i = 1; i <= 3; i++)
        {
            await runtime.BuildDetailedAsync(
                R29C_E2E_Helpers.BuildPackageRequest($"pkg-op-{i}", runId),
                CancellationToken.None);
        }

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.AreEqual(3, metrics.TotalObservations, "应有 3 个观察样本");
        Assert.IsTrue(metrics.AverageQualityScore > 0.5,
            $"健康 V2 产出的 quality_score 应 > 0.5（实际 {metrics.AverageQualityScore:F4}）");

        time.Advance(TimeSpan.FromSeconds(2));
        var result = await service.AdvanceAsync(
            runId, "t-e2e-pkg-healthy-001", idempotencyKey: "idem-e2e-pkg-healthy-001",
            CanaryAcceptanceHelpers.HealthyBaseline, metrics.ToExperimentMetrics());
        Assert.AreEqual(CanaryProgressionDecision.Advance, result.Decision,
            $"健康质量分应推进；rationale={result.Rationale}");
        Assert.AreEqual(100, result.CurrentPercentage, "应推进到 100%（PercentageLadder=[99,100]）");
    }

    // ===========================================================================
    // 2. LowQualityV2_PackagePath_TriggersRollback
    // 验证：Package Mixed mode（99%）下，空候选 V2 产出 → 上报零质量样本 →
    // AdvanceAsync 触发回滚。
    // ===========================================================================
    [TestMethod]
    public async Task LowQualityV2_PackagePath_TriggersRollback()
    {
        var runId = "run-e2e-pkg-lowquality";
        var options = R29C_E2E_Helpers.BuildOptionsWithQuality(minQualityScore: 0.3);
        var stubV2 = new QueueDecisionRuntime(
            R29C_E2E_Helpers.BuildEmptyResult("pkg-op-1"),
            R29C_E2E_Helpers.BuildEmptyResult("pkg-op-2"),
            R29C_E2E_Helpers.BuildEmptyResult("pkg-op-3"));
        var (runtime, collector, cutover, store, service, time) =
            R29C_E2E_Helpers.BuildPackageE2EStack(stubV2, cutoverPercentage: 99, options);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, runId);
        service.InitializeCanary(runId);

        for (var i = 1; i <= 3; i++)
        {
            await runtime.BuildDetailedAsync(
                R29C_E2E_Helpers.BuildPackageRequest($"pkg-op-{i}", runId),
                CancellationToken.None);
        }

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.AreEqual(3, metrics.TotalObservations, "应有 3 个观察样本");
        Assert.IsTrue(metrics.AverageQualityScore < 0.3,
            $"空候选产出的 quality_score 应 < 0.3（实际 {metrics.AverageQualityScore:F4}）");

        time.Advance(TimeSpan.FromSeconds(2));
        var result = await service.AdvanceAsync(
            runId, "t-e2e-pkg-low-001", idempotencyKey: "idem-e2e-pkg-low-001",
            CanaryAcceptanceHelpers.HealthyBaseline, metrics.ToExperimentMetrics());
        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision,
            $"低质量分应触发回滚；rationale={result.Rationale}");
        Assert.AreEqual(0, cutover.CutoverPercentage, "回滚后应为 0%");
    }
}

// ===========================================================================
// 测试类 3：ProgressiveDegradationE2ETests
// 验证：质量分随调用次数渐进退化时，CanaryProgressionService 在多个阶梯推进后触发回滚。
// 模拟生产场景：V2 健康若干次 → 质量下降 → 最终触发回滚。
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("R29-C")]
[TestCategory("R29-C-4")]
public sealed class ProgressiveDegradationE2ETests
{
    // ===========================================================================
    // 1. ProgressiveQualityDegradation_TriggersRollbackAtLaterStage
    // 场景：90% 阶段（3 次健康）→ 推进到 95%；95% 阶段（3 次低质量）→ 触发回滚。
    // 验证完整审计轨迹：Advance（90→95）→ Rollback（95→0）。
    // 使用 90→95 阶梯确保两个阶段都在 Mixed mode（< 100%）触发 RecordObservation。
    // ===========================================================================
    [TestMethod]
    public async Task ProgressiveQualityDegradation_TriggersRollbackAtLaterStage()
    {
        var runId = "run-e2e-progressive-degradation";
        // 使用 90→95 阶梯（避免 100% 切换到 V2-only 路径丢失 RecordObservation）
        // MaxDivergenceRate=2.0 禁用 parity 阈值（参见 BuildOptionsWithQuality 注释）
        var options = new CanaryGateOptions
        {
            PercentageLadder = [90, 95],
            MinObservationPeriod = TimeSpan.FromSeconds(1),
            MaxDivergenceRate = 2.0,
            MinQualityScore = 0.3
        };
        // 6 次结果：前 3 次健康，后 3 次空候选
        var stubV2 = new QueueDecisionRuntime(
            R29C_E2E_Helpers.BuildHealthyResult("prog-1"),
            R29C_E2E_Helpers.BuildHealthyResult("prog-2"),
            R29C_E2E_Helpers.BuildHealthyResult("prog-3"),
            R29C_E2E_Helpers.BuildEmptyResult("prog-4"),
            R29C_E2E_Helpers.BuildEmptyResult("prog-5"),
            R29C_E2E_Helpers.BuildEmptyResult("prog-6"));
        var (runtime, collector, cutover, store, service, time) =
            R29C_E2E_Helpers.BuildRetrievalE2EStack(stubV2, cutoverPercentage: 90, options);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, runId);
        service.InitializeCanary(runId);
        Assert.AreEqual(90, cutover.CutoverPercentage, "初始化后应为 90%");

        // 阶段 1：3 次健康请求
        for (var i = 1; i <= 3; i++)
        {
            await runtime.RetrieveAsync(
                R29C_E2E_Helpers.BuildRetrievalRequest($"prog-{i}", runId),
                CancellationToken.None);
        }
        var healthyMetrics = collector.GetAggregatedMetrics(runId);
        Assert.IsTrue(healthyMetrics.TotalObservations >= 1,
            "阶段 1 应至少有 1 个观察样本（90% cutover）");
        Assert.IsTrue(healthyMetrics.AverageQualityScore > 0.5,
            "阶段 1 质量分应 > 0.5");

        // 推进到 95%
        time.Advance(TimeSpan.FromSeconds(2));
        var advanceResult = await service.AdvanceAsync(
            runId, "t-prog-advance-001", idempotencyKey: "idem-prog-001",
            CanaryAcceptanceHelpers.HealthyBaseline, healthyMetrics.ToExperimentMetrics());
        Assert.AreEqual(CanaryProgressionDecision.Advance, advanceResult.Decision,
            $"阶段 1 健康应推进；rationale={advanceResult.Rationale}");
        Assert.AreEqual(95, cutover.CutoverPercentage, "推进后应为 95%");

        // 阶段 2：3 次空候选请求（退化）
        for (var i = 4; i <= 6; i++)
        {
            await runtime.RetrieveAsync(
                R29C_E2E_Helpers.BuildRetrievalRequest($"prog-{i}", runId),
                CancellationToken.None);
        }
        var degradedMetrics = collector.GetAggregatedMetrics(runId);
        // 6 次累计：3 健康分（≈0.925）+ 3 空候选（≈0.0）→ 均值 ≈ 0.46
        // 但 ring buffer 容量默认 1000，所以 6 次都在窗口内
        Assert.IsTrue(degradedMetrics.AverageQualityScore < 0.3,
            $"阶段 2 退化后质量分应 < 0.3（实际 {degradedMetrics.AverageQualityScore:F4}）");

        // 触发回滚
        time.Advance(TimeSpan.FromSeconds(2));
        var rollbackResult = await service.AdvanceAsync(
            runId, "t-prog-rollback-001", idempotencyKey: "idem-prog-002",
            CanaryAcceptanceHelpers.HealthyBaseline, degradedMetrics.ToExperimentMetrics());
        Assert.AreEqual(CanaryProgressionDecision.Rollback, rollbackResult.Decision,
            $"阶段 2 退化应触发回滚；rationale={rollbackResult.Rationale}");
        Assert.AreEqual(0, cutover.CutoverPercentage, "回滚后应为 0%");

        // 验证审计轨迹：Advance + Rollback 两条记录
        var transitions = await store.ListStageTransitionsByRunAsync(runId);
        var advanceRecords = transitions.Where(t => t.Decision == CanaryProgressionDecision.Advance).ToList();
        var rollbackRecords = transitions.Where(t => t.Decision == CanaryProgressionDecision.Rollback).ToList();
        Assert.AreEqual(1, advanceRecords.Count, "应有 1 条 Advance 审计记录");
        Assert.AreEqual(1, rollbackRecords.Count, "应有 1 条 Rollback 审计记录");
        Assert.AreEqual(90, advanceRecords[0].FromPercentage, "Advance 从 90% 开始");
        Assert.AreEqual(95, advanceRecords[0].ToPercentage, "Advance 到 95%");
        Assert.AreEqual(95, rollbackRecords[0].FromPercentage, "Rollback 从 95% 开始");
        Assert.AreEqual(0, rollbackRecords[0].ToPercentage, "Rollback 到 0%");
    }

    // ===========================================================================
    // 2. TwoStageProgression_AllHealthy_AdvancesFrom90To95
    // 验证：2 个阶段全部健康时，Canary 推进 90% → 95%。
    // 证明 E2E 链路在多次推进中保持稳定（无状态污染、无指标残留）。
    // 使用 90→95 阶梯（避免达到 100% 后切换到 V2-only 路径，丢失 RecordObservation）。
    // ===========================================================================
    [TestMethod]
    public async Task TwoStageProgression_AllHealthy_AdvancesFrom90To95()
    {
        var runId = "run-e2e-two-stage-healthy";
        // 使用 2 档阶梯：90→95（避免 100% 切换到 V2-only 路径丢失 RecordObservation）
        // MaxDivergenceRate=2.0 禁用 parity 阈值（参见 BuildOptionsWithQuality 注释）
        var options = new CanaryGateOptions
        {
            PercentageLadder = [90, 95],
            MinObservationPeriod = TimeSpan.FromSeconds(1),
            MaxDivergenceRate = 2.0,
            MinQualityScore = 0.3
        };
        // 6 次健康结果（2 阶段 × 3 次）
        var stubV2 = new QueueDecisionRuntime(
            R29C_E2E_Helpers.BuildHealthyResult("ts-1"),
            R29C_E2E_Helpers.BuildHealthyResult("ts-2"),
            R29C_E2E_Helpers.BuildHealthyResult("ts-3"),
            R29C_E2E_Helpers.BuildHealthyResult("ts-4"),
            R29C_E2E_Helpers.BuildHealthyResult("ts-5"),
            R29C_E2E_Helpers.BuildHealthyResult("ts-6"));
        var (runtime, collector, _, store, service, time) =
            R29C_E2E_Helpers.BuildRetrievalE2EStack(stubV2, cutoverPercentage: 90, options);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, runId);
        service.InitializeCanary(runId);

        // 阶段 1：3 次健康请求 → 推进到 95%
        for (var i = 1; i <= 3; i++)
        {
            await runtime.RetrieveAsync(
                R29C_E2E_Helpers.BuildRetrievalRequest($"ts-{i}", runId),
                CancellationToken.None);
        }
        time.Advance(TimeSpan.FromSeconds(2));
        var stage1Metrics = collector.GetAggregatedMetrics(runId);
        Assert.IsTrue(stage1Metrics.TotalObservations >= 1,
            "阶段 1 应至少有 1 个观察样本（90% cutover）");
        var stage1Result = await service.AdvanceAsync(
            runId, "t-ts-stage-1", idempotencyKey: "idem-ts-1",
            CanaryAcceptanceHelpers.HealthyBaseline, stage1Metrics.ToExperimentMetrics());
        Assert.AreEqual(CanaryProgressionDecision.Advance, stage1Result.Decision,
            $"阶段 1 应推进；rationale={stage1Result.Rationale}");
        Assert.AreEqual(95, stage1Result.CurrentPercentage, "阶段 1 应推进到 95%");

        // 阶段 2：3 次健康请求 → 推进到末档（Promoted）
        for (var i = 4; i <= 6; i++)
        {
            await runtime.RetrieveAsync(
                R29C_E2E_Helpers.BuildRetrievalRequest($"ts-{i}", runId),
                CancellationToken.None);
        }
        time.Advance(TimeSpan.FromSeconds(2));
        var stage2Metrics = collector.GetAggregatedMetrics(runId);
        Assert.IsTrue(stage2Metrics.TotalObservations >= stage1Metrics.TotalObservations,
            "阶段 2 累计观察数应 ≥ 阶段 1（ring buffer 不丢失）");
        var stage2Result = await service.AdvanceAsync(
            runId, "t-ts-stage-2", idempotencyKey: "idem-ts-2",
            CanaryAcceptanceHelpers.HealthyBaseline, stage2Metrics.ToExperimentMetrics());
        // 末档 → Promoted（不再推进）
        Assert.AreEqual(CanaryProgressionDecision.Promoted, stage2Result.Decision,
            $"阶段 2 应为 Promoted（末档）；rationale={stage2Result.Rationale}");
    }
}

// ===========================================================================
// 测试类 4：MixedTrafficE2ETests
// 验证：混合流量（带 canaryRunId 与不带 canaryRunId 的请求交替）下，
// Collector 不被无 runId 的请求污染，canary 推进/回滚决策正确。
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("R29-C")]
[TestCategory("R29-C-4")]
public sealed class MixedTrafficE2ETests
{
    // ===========================================================================
    // 1. MixedRunIdAndNoRunId_OnlyRunIdRecorded
    // 验证：5 次带 runId 请求 + 5 次不带 runId 请求交替，Collector 只记录 5 个样本。
    // 无 runId 的请求不污染 canary run 的指标。
    // ===========================================================================
    [TestMethod]
    public async Task MixedRunIdAndNoRunId_OnlyRunIdRecorded()
    {
        var runId = "run-e2e-mixed-traffic";
        var options = R29C_E2E_Helpers.BuildOptionsWithQuality(minQualityScore: 0.3);
        // 10 次结果：偶数索引带 runId，奇数索引不带
        var results = new List<ContextDecisionResult>();
        for (var i = 1; i <= 5; i++)
        {
            results.Add(R29C_E2E_Helpers.BuildHealthyResult($"mix-{i}-with-runid"));
            results.Add(R29C_E2E_Helpers.BuildHealthyResult($"mix-{i}-no-runid"));
        }
        var stubV2 = new QueueDecisionRuntime(results.ToArray());
        var (runtime, collector, _, store, service, _) =
            R29C_E2E_Helpers.BuildRetrievalE2EStack(stubV2, cutoverPercentage: 99, options);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, runId);
        service.InitializeCanary(runId);

        for (var i = 1; i <= 5; i++)
        {
            // 带 runId
            await runtime.RetrieveAsync(
                R29C_E2E_Helpers.BuildRetrievalRequest($"mix-{i}-with-runid", runId),
                CancellationToken.None);
            // 不带 runId
            var noRunIdRequest = new ContextRetrievalRequest
            {
                OperationId = $"mix-{i}-no-runid",
                WorkspaceId = "ws-mix",
                CollectionId = "col-mix",
                QueryText = "q"
            };
            await runtime.RetrieveAsync(noRunIdRequest, CancellationToken.None);
        }

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.AreEqual(5, metrics.TotalObservations,
            "只应记录 5 个带 runId 的样本（无 runId 请求不污染 Collector）");
        Assert.IsTrue(metrics.AverageQualityScore > 0.5,
            "所有带 runId 样本均为健康产出，质量分应 > 0.5");
    }
}
