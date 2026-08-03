using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Core.Services.Policy;

namespace ContextCore.Tests;

// ===========================================================================
// 质量分指标 + Canary 回滚阈值接入验收测试
//
// 覆盖范围（3 个测试类，共 22 项）：
// 1. CanaryQualityScoreCalculatorTests（8 项）— 质量分计算器单元行为
// - null execution / null decision → 0.0
// - 无选中候选 → avg relevance = 0
// - section coverage 主路径（FinalArtifactTokenCost）
// - section coverage 回退路径（Outcome.TokenBudget）
// - section coverage 无预算约束 → 1.0
// - 候选相关性均值（FinalScore）
// - 0.5/0.5 默认权重合成
// - FinalScore > 1.0 被 Clamp；NaN/Infinity 被忽略
// 2. CanaryMetricsCollectorQualityScoreTests（6 项）— 采集器聚合行为
// - RecordObservation 接受 qualityScore 参数
// - qualityScore=null 默认为 0.0
// - AverageQualityScore = sum / total
// - ToExperimentMetrics 包含 quality_score 字段
// - ToBaselineMetrics 不包含 quality_score 字段（仅 V2 路径）
// - Ring buffer 淘汰时 QualityScoreSum 正确回滚
// 3. CanaryProgressionServiceQualityScoreTests（8 项）— 回滚阈值集成
// - quality_score 高于 MinQualityScore → 不回滚（Advance）
// - quality_score 低于 MinQualityScore → 触发回滚（Rollback）
// - MinQualityScore=0.0 时禁用质量分检查
// - experimentMetrics 缺失 quality_score → 不回滚（graceful）
// - quality_score = MinQualityScore 边界 → 不回滚（< 而非 <=）
// - 端到端：AdvanceAsync 在低 quality_score 下走 Rollback 路径
// - 默认 CanaryGateOptions.MinQualityScore = 0.3
// - FromEnvironment 解析 CC_CANARY_MIN_QUALITY_SCORE
//
// 设计原则：
// - 直接测试 CanaryQualityScoreCalculator（internal static，通过 InternalsVisibleTo 暴露）
// - 使用真实 InMemoryPipelineRunStore + CutoverController + DefaultCanaryMetricsCollector
// - 使用可推进时间的 CanaryAcceptanceTimeProvider（复用共享 helper）
// - 所有代码注释使用中文
// ===========================================================================

// ===========================================================================
// 测试类 1：CanaryQualityScoreCalculatorTests
// 验证从 ContextDecisionExecutionResult 计算质量分的正确性
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("R29-C")]
[TestCategory("R29-C-3")]
public sealed class CanaryQualityScoreCalculatorTests
{
    private const string WorkspaceId = "ws-test";
    private const string CollectionId = "col-test";

    // ===========================================================================
    // 1. Compute_ReturnsZero_WhenExecutionIsNull
    // 验证：execution=null 时直接返回 0.0（不抛 NRE）
    // ===========================================================================
    [TestMethod]
    public void Compute_ReturnsZero_WhenExecutionIsNull()
    {
        var score = CanaryQualityScoreCalculator.Compute(execution: null);
        Assert.AreEqual(0.0, score, "execution=null 时应返回 0.0");
    }

    // ===========================================================================
    // 2. Compute_ReturnsZero_WhenDecisionIsNull
    // 验证：直接调用 Compute(decision: null, ...) 重载时返回 0.0
    // 注：ContextDecisionExecutionResult.Decision 是 required 字段不可为 null，
    // 此测试覆盖 Compute(ContextDecisionResult?, ...) 重载的 null 防御逻辑
    // ===========================================================================
    [TestMethod]
    public void Compute_ReturnsZero_WhenDecisionIsNull()
    {
        var score = CanaryQualityScoreCalculator.Compute(decision: null, finalTokenCost: null);
        Assert.AreEqual(0.0, score, "Decision=null 时应返回 0.0");
    }

    // ===========================================================================
    // 3. Compute_AvgRelevanceIsZero_WhenNoSelectedEnvelopes
    // 验证：SelectedEnvelopes 为空时 avg relevance=0；section coverage 由预算决定
    // ===========================================================================
    [TestMethod]
    public void Compute_AvgRelevanceIsZero_WhenNoSelectedEnvelopes()
    {
        var decision = BuildDecision(
            selectedEnvelopes: [],
            effectiveTokens: 0,
            tokenBudget: 1000);
        // 无 FinalArtifactTokenCost → 回退到 Outcome：EffectiveTokens=0, Budget=1000 → coverage=0.0
        // avg relevance=0（空集合）
        // score = 0.5*0 + 0.5*0 = 0.0
        var score = CanaryQualityScoreCalculator.Compute(decision, finalTokenCost: null);
        Assert.AreEqual(0.0, score, 0.0001, "无选中候选 + 0 token 利用 → 质量分应为 0.0");
    }

    // ===========================================================================
    // 4. Compute_SectionCoverageFromFinalArtifactTokenCost
    // 验证：FinalArtifactTokenCost.BudgetLimit 优先于 Outcome.TokenBudget
    // ===========================================================================
    [TestMethod]
    public void Compute_SectionCoverageFromFinalArtifactTokenCost()
    {
        var decision = BuildDecision(
            selectedEnvelopes: [],
            effectiveTokens: 500,  // Outcome 字段（应被忽略）
            tokenBudget: 1000);    // Outcome 字段（应被忽略）
        // FinalArtifactTokenCost：TotalTokens=800, BudgetLimit=1000 → coverage=0.8
        // avg relevance=0（空集合）
        // score = 0.5*0.8 + 0.5*0 = 0.4
        var finalTokenCost = new FinalArtifactTokenCost
        {
            Sections = [],
            TotalTokens = 800,
            TokenizerId = "test-tokenizer",
            WithinBudget = true,
            BudgetLimit = 1000
        };
        var score = CanaryQualityScoreCalculator.Compute(decision, finalTokenCost);
        Assert.AreEqual(0.4, score, 0.0001,
            "FinalArtifactTokenCost 应优先：coverage=0.8, relevance=0 → score=0.4");
    }

    // ===========================================================================
    // 5. Compute_SectionCoverageFallsBackToOutcome
    // 验证：FinalArtifactTokenCost=null 时回退到 Outcome.EffectiveTokens/TokenBudget
    // ===========================================================================
    [TestMethod]
    public void Compute_SectionCoverageFallsBackToOutcome()
    {
        var decision = BuildDecision(
            selectedEnvelopes: [],
            effectiveTokens: 600,
            tokenBudget: 1000);
        // 回退路径：coverage = 600/1000 = 0.6
        // avg relevance=0
        // score = 0.5*0.6 + 0.5*0 = 0.3
        var score = CanaryQualityScoreCalculator.Compute(decision, finalTokenCost: null);
        Assert.AreEqual(0.3, score, 0.0001,
            "回退路径：coverage=0.6, relevance=0 → score=0.3");
    }

    // ===========================================================================
    // 6. Compute_SectionCoverageIsOne_WhenNoBudgetConstraint
    // 验证：FinalArtifactTokenCost=null 且 Outcome.TokenBudget=0 → coverage=1.0（视为完整覆盖）
    // ===========================================================================
    [TestMethod]
    public void Compute_SectionCoverageIsOne_WhenNoBudgetConstraint()
    {
        var decision = BuildDecision(
            selectedEnvelopes: [],
            effectiveTokens: 100,
            tokenBudget: 0);  // 无预算约束 → coverage=1.0
        // avg relevance=0
        // score = 0.5*1.0 + 0.5*0 = 0.5
        var score = CanaryQualityScoreCalculator.Compute(decision, finalTokenCost: null);
        Assert.AreEqual(0.5, score, 0.0001,
            "无预算约束：coverage=1.0, relevance=0 → score=0.5");
    }

    // ===========================================================================
    // 7. Compute_AvgRelevanceFromFinalScore
    // 验证：avg relevance = SelectedEnvelopes.Utility.FinalScore 均值
    // ===========================================================================
    [TestMethod]
    public void Compute_AvgRelevanceFromFinalScore()
    {
        var envelopes = new[]
        {
            BuildEnvelope("c1", finalScore: 0.8),
            BuildEnvelope("c2", finalScore: 0.6),
            BuildEnvelope("c3", finalScore: 1.0)
        };
        var decision = BuildDecision(
            selectedEnvelopes: envelopes,
            effectiveTokens: 1000,
            tokenBudget: 1000);  // coverage=1.0
        // avg relevance = (0.8 + 0.6 + 1.0) / 3 = 0.8
        // score = 0.5*1.0 + 0.5*0.8 = 0.9
        var score = CanaryQualityScoreCalculator.Compute(decision, finalTokenCost: null);
        Assert.AreEqual(0.9, score, 0.0001,
            "coverage=1.0, avg relevance=0.8 → score=0.9");
    }

    // ===========================================================================
    // 8. Compute_ClampsToUnitInterval_WhenFinalScoreExceedsOne
    // 验证：FinalScore > 1.0 被 Clamp 到 1.0；NaN/Infinity 被忽略
    // ===========================================================================
    [TestMethod]
    public void Compute_ClampsToUnitInterval_WhenFinalScoreExceedsOne()
    {
        var envelopes = new[]
        {
            BuildEnvelope("c1", finalScore: 1.5),   // 超过 1.0，应 Clamp 到 1.0
            BuildEnvelope("c2", finalScore: 0.6),
            BuildEnvelope("c3", finalScore: double.NaN),       // 应被忽略
            BuildEnvelope("c4", finalScore: double.PositiveInfinity)  // 应被忽略
        };
        var decision = BuildDecision(
            selectedEnvelopes: envelopes,
            effectiveTokens: 1000,
            tokenBudget: 1000);  // coverage=1.0
        // 有效候选：c1 (clamp→1.0), c2 (0.6)
        // avg relevance = (1.0 + 0.6) / 2 = 0.8
        // score = 0.5*1.0 + 0.5*0.8 = 0.9
        var score = CanaryQualityScoreCalculator.Compute(decision, finalTokenCost: null);
        Assert.AreEqual(0.9, score, 0.0001,
            "FinalScore > 1.0 应 Clamp；NaN/Infinity 应被忽略");
    }

    // -----------------------------------------------------------------------
    // 辅助方法
    // -----------------------------------------------------------------------

    private static ContextDecisionResult BuildDecision(
        IReadOnlyList<ContextCandidateEnvelope> selectedEnvelopes,
        int effectiveTokens,
        int tokenBudget)
    {
        return new ContextDecisionResult
        {
            RequestId = "req-test",
            SelectedEnvelopes = selectedEnvelopes,
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = selectedEnvelopes.Count,
                EffectiveTokens = effectiveTokens,
                TokenBudget = tokenBudget
            }
        };
    }

    private static ContextCandidateEnvelope BuildEnvelope(string candidateId, double finalScore)
    {
        return new ContextCandidateEnvelope
        {
            CandidateId = candidateId,
            Source = ContextCandidateSource.Lexical,
            CanonicalKey = CanonicalCandidateKey.Create(
                WorkspaceId, CollectionId,
                entityKind: "test",
                entityId: candidateId,
                entityVersion: "v1"),
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = finalScore,
                FinalScore = finalScore,
                ReasonCode = "test"
            }
        };
    }

    private static EffectivePolicySnapshot BuildPolicySnapshot()
    {
        var bundle = DefaultPolicyBundleFactory.Create();
        return new EffectivePolicySnapshot
        {
            Reference = new ResolvedPolicyReference
            {
                BundleId = bundle.BundleId,
                BundleVersion = bundle.Version,
                BundleContentHash = DefaultResolvedPolicyProvider.DefaultContentHash,
                ActivationEpoch = DefaultResolvedPolicyProvider.DefaultActivationEpoch
            },
            Safety = bundle.Safety,
            Budget = bundle.Budget,
            Routing = bundle.Routing,
            FeatureSchemaVersion = bundle.Policies.DecisionSchemaVersion,
            ResolutionScope = new ContextDecisionScope(WorkspaceId, CollectionId)
        };
    }
}

// ===========================================================================
// 测试类 2：CanaryMetricsCollectorQualityScoreTests
// 验证 DefaultCanaryMetricsCollector 对 quality_score 的聚合行为
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("R29-C")]
[TestCategory("R29-C-3")]
public sealed class CanaryMetricsCollectorQualityScoreTests
{
    // ===========================================================================
    // 1. RecordObservation_AcceptsQualityScoreParameter
    // 验证：RecordObservation 接受 qualityScore 参数，AverageQualityScore 反映该值
    // ===========================================================================
    [TestMethod]
    public void RecordObservation_AcceptsQualityScoreParameter()
    {
        var collector = new DefaultCanaryMetricsCollector();
        var parity = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);

        collector.RecordObservation(
            runId: "run-q1",
            parityReport: parity,
            v2Succeeded: true,
            legacySucceeded: true,
            v2Duration: TimeSpan.FromMilliseconds(100),
            legacyDuration: TimeSpan.FromMilliseconds(100),
            qualityScore: 0.85);

        var metrics = collector.GetAggregatedMetrics("run-q1");
        Assert.AreEqual(0.85, metrics.AverageQualityScore, 0.0001,
            "单次观察 quality_score=0.85 → AverageQualityScore 应为 0.85");
    }

    // ===========================================================================
    // 2. RecordObservation_QualityScoreNullDefaultsToZero
    // 验证：qualityScore=null 时记为 0.0（不影响 latency/error/divergence）
    // ===========================================================================
    [TestMethod]
    public void RecordObservation_QualityScoreNullDefaultsToZero()
    {
        var collector = new DefaultCanaryMetricsCollector();
        var parity = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);

        collector.RecordObservation(
            runId: "run-q2",
            parityReport: parity,
            v2Succeeded: true,
            legacySucceeded: true,
            v2Duration: TimeSpan.FromMilliseconds(100),
            legacyDuration: TimeSpan.FromMilliseconds(100),
            qualityScore: null);  // 显式传 null

        var metrics = collector.GetAggregatedMetrics("run-q2");
        Assert.AreEqual(0.0, metrics.AverageQualityScore, 0.0001,
            "qualityScore=null 应记为 0.0");
        // 其他指标不受影响
        Assert.AreEqual(1, metrics.TotalObservations, "TotalObservations 不受 quality_score 影响");
        Assert.AreEqual(0.0, metrics.V2ErrorRate, "V2ErrorRate 不受 quality_score 影响");
    }

    // ===========================================================================
    // 3. AverageQualityScore_IsMeanOfAllSamples
    // 验证：多次观察的 AverageQualityScore = sum / total
    // ===========================================================================
    [TestMethod]
    public void AverageQualityScore_IsMeanOfAllSamples()
    {
        var collector = new DefaultCanaryMetricsCollector();
        var parity = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);

        // 3 次观察：0.6 + 0.8 + 0.4 → 均值 = 0.6
        collector.RecordObservation("run-q3", parity, true, true,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), qualityScore: 0.6);
        collector.RecordObservation("run-q3", parity, true, true,
            TimeSpan.FromMilliseconds(110), TimeSpan.FromMilliseconds(100), qualityScore: 0.8);
        collector.RecordObservation("run-q3", parity, true, true,
            TimeSpan.FromMilliseconds(105), TimeSpan.FromMilliseconds(100), qualityScore: 0.4);

        var metrics = collector.GetAggregatedMetrics("run-q3");
        Assert.AreEqual(3, metrics.TotalObservations, "应有 3 次观察");
        Assert.AreEqual(0.6, metrics.AverageQualityScore, 0.0001,
            "(0.6 + 0.8 + 0.4) / 3 = 0.6");
    }

    // ===========================================================================
    // 4. ToExperimentMetrics_IncludesQualityScore
    // 验证：ToExperimentMetrics 包含 "quality_score" 字段
    // ===========================================================================
    [TestMethod]
    public void ToExperimentMetrics_IncludesQualityScore()
    {
        var collector = new DefaultCanaryMetricsCollector();
        var parity = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);
        collector.RecordObservation("run-q4", parity, true, true,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), qualityScore: 0.72);

        var metrics = collector.GetAggregatedMetrics("run-q4");
        var experimentMetrics = metrics.ToExperimentMetrics();

        Assert.IsTrue(experimentMetrics.ContainsKey("quality_score"),
            "ToExperimentMetrics 应包含 quality_score 字段");
        Assert.AreEqual(0.72, experimentMetrics["quality_score"], 0.0001,
            "quality_score 值应匹配 AverageQualityScore");
        // 同时保留原有指标
        Assert.IsTrue(experimentMetrics.ContainsKey("error_rate"), "应保留 error_rate");
        Assert.IsTrue(experimentMetrics.ContainsKey("p95_latency_ms"), "应保留 p95_latency_ms");
        Assert.IsTrue(experimentMetrics.ContainsKey("divergence_rate"), "应保留 divergence_rate");
    }

    // ===========================================================================
    // 5. ToBaselineMetrics_DoesNotIncludeQualityScore
    // 验证：ToBaselineMetrics 不包含 quality_score（仅 V2 路径有质量分）
    // ===========================================================================
    [TestMethod]
    public void ToBaselineMetrics_DoesNotIncludeQualityScore()
    {
        var collector = new DefaultCanaryMetricsCollector();
        var parity = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);
        collector.RecordObservation("run-q5", parity, true, true,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), qualityScore: 0.9);

        var metrics = collector.GetAggregatedMetrics("run-q5");
        var baselineMetrics = metrics.ToBaselineMetrics();

        Assert.IsFalse(baselineMetrics.ContainsKey("quality_score"),
            "ToBaselineMetrics 不应包含 quality_score（质量分仅适用于 V2 路径）");
        Assert.IsTrue(baselineMetrics.ContainsKey("error_rate"), "应保留 error_rate");
        Assert.IsTrue(baselineMetrics.ContainsKey("p95_latency_ms"), "应保留 p95_latency_ms");
    }

    // ===========================================================================
    // 6. RingBufferEviction_RollsBackQualityScoreSum
    // 验证：ring buffer 容量超限时淘汰最旧样本，QualityScoreSum 正确回滚
    // ===========================================================================
    [TestMethod]
    public void RingBufferEviction_RollsBackQualityScoreSum()
    {
        // 容量=3：写入 4 个样本后，最旧的被淘汰
        var collector = new DefaultCanaryMetricsCollector(maxSamplesPerRun: 3);
        var parity = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);

        // 4 个样本：0.9, 0.5, 0.7, 0.3
        // 容量=3，写入第 4 个时淘汰第 1 个（0.9）
        // 保留：0.5, 0.7, 0.3 → 均值 = 1.5/3 = 0.5
        collector.RecordObservation("run-q6", parity, true, true,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), qualityScore: 0.9);
        collector.RecordObservation("run-q6", parity, true, true,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), qualityScore: 0.5);
        collector.RecordObservation("run-q6", parity, true, true,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), qualityScore: 0.7);
        collector.RecordObservation("run-q6", parity, true, true,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), qualityScore: 0.3);

        var metrics = collector.GetAggregatedMetrics("run-q6");
        // ring buffer 容量 3，EvictOldest 回滚 TotalObservations，使其等于当前 ring buffer 内样本数
        Assert.AreEqual(3, metrics.TotalObservations,
            "ring buffer 容量=3，淘汰后应仅 3 个样本");
        Assert.AreEqual(0.5, metrics.AverageQualityScore, 0.0001,
            "淘汰 0.9 后：(0.5 + 0.7 + 0.3) / 3 = 0.5");
    }
}

// ===========================================================================
// 测试类 3：CanaryProgressionServiceQualityScoreTests
// 验证 CanaryProgressionService 集成 MinQualityScore 阈值后的回滚决策
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("R29-C")]
[TestCategory("R29-C-3")]
public sealed class CanaryProgressionServiceQualityScoreTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    // 健康基线指标（不含 quality_score，因 baseline 不应有此字段）
    private static readonly IReadOnlyDictionary<string, double> HealthyBaseline =
        new Dictionary<string, double>
        {
            ["error_rate"] = 0.01,
            ["p95_latency_ms"] = 100.0
        };

    // ===========================================================================
    // 1. QualityScoreAboveThreshold_AdvancesSuccessfully
    // 验证：quality_score > MinQualityScore 时不触发回滚，正常推进
    // ===========================================================================
    [TestMethod]
    public async Task QualityScoreAboveThreshold_AdvancesSuccessfully()
    {
        var (service, cutover, time, store) = BuildService(new CanaryGateOptions
        {
            MinObservationPeriod = TimeSpan.FromSeconds(1),
            MinQualityScore = 0.3
        });
        var runId = await CreateScopedCanaryRunAsync(store);
        service.InitializeCanary(runId);

        // quality_score=0.85 > 阈值 0.3 → 不应触发回滚
        var experiment = new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.02,
            ["quality_score"] = 0.85
        };

        time.Advance(TimeSpan.FromSeconds(2));
        var result = await service.AdvanceAsync(runId, "t-q-advance-1", null, HealthyBaseline, experiment);

        Assert.AreEqual(CanaryProgressionDecision.Advance, result.Decision,
            $"quality_score=0.85 > 0.3 应推进；rationale={result.Rationale}");
        Assert.AreEqual(5, result.CurrentPercentage, "应推进到 5%");
        Assert.AreEqual(5, cutover.CutoverPercentage);
    }

    // ===========================================================================
    // 2. QualityScoreBelowThreshold_TriggersRollback
    // 验证：quality_score < MinQualityScore 时触发自动回滚
    // ===========================================================================
    [TestMethod]
    public async Task QualityScoreBelowThreshold_TriggersRollback()
    {
        var (service, cutover, _, store) = BuildService(new CanaryGateOptions
        {
            MinObservationPeriod = TimeSpan.FromSeconds(1),
            MinQualityScore = 0.3
        });
        var runId = await CreateScopedCanaryRunAsync(store);
        service.InitializeCanary(runId);

        // quality_score=0.15 < 阈值 0.3 → 应触发回滚
        var experiment = new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.02,
            ["quality_score"] = 0.15
        };

        var result = await service.AdvanceAsync(runId, "t-q-rollback-1", null, HealthyBaseline, experiment);

        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision,
            $"quality_score=0.15 < 0.3 应触发回滚；rationale={result.Rationale}");
        Assert.AreEqual(0, cutover.CutoverPercentage, "回滚后 CutoverController 应重置为 0%");
        Assert.AreEqual(0, service.GetCurrentPercentage(runId), "回滚后当前百分比应为 0");
    }

    // ===========================================================================
    // 3. MinQualityScoreZero_DisablesQualityCheck
    // 验证：MinQualityScore=0.0 时禁用质量分检查（即使 quality_score=0.0 也不回滚）
    // ===========================================================================
    [TestMethod]
    public async Task MinQualityScoreZero_DisablesQualityCheck()
    {
        var (service, cutover, time, store) = BuildService(new CanaryGateOptions
        {
            MinObservationPeriod = TimeSpan.FromSeconds(1),
            MinQualityScore = 0.0  // 禁用质量分检查
        });
        var runId = await CreateScopedCanaryRunAsync(store);
        service.InitializeCanary(runId);

        // quality_score=0.0，但 MinQualityScore=0.0 → 不触发回滚
        var experiment = new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.02,
            ["quality_score"] = 0.0
        };

        time.Advance(TimeSpan.FromSeconds(2));
        var result = await service.AdvanceAsync(runId, "t-q-disabled-1", null, HealthyBaseline, experiment);

        Assert.AreEqual(CanaryProgressionDecision.Advance, result.Decision,
            $"MinQualityScore=0.0 禁用质量分检查；quality_score=0.0 不应回滚；rationale={result.Rationale}");
        Assert.AreEqual(5, result.CurrentPercentage, "应正常推进到 5%");
    }

    // ===========================================================================
    // 4. QualityScoreAbsent_DoesNotTriggerRollback
    // 验证：experimentMetrics 不含 quality_score 字段时，不触发回滚（graceful）
    // ===========================================================================
    [TestMethod]
    public async Task QualityScoreAbsent_DoesNotTriggerRollback()
    {
        var (service, cutover, time, store) = BuildService(new CanaryGateOptions
        {
            MinObservationPeriod = TimeSpan.FromSeconds(1),
            MinQualityScore = 0.3
        });
        var runId = await CreateScopedCanaryRunAsync(store);
        service.InitializeCanary(runId);

        // 不包含 quality_score 字段（旧版 collector 兼容场景）
        var experiment = new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.02
        };

        time.Advance(TimeSpan.FromSeconds(2));
        var result = await service.AdvanceAsync(runId, "t-q-absent-1", null, HealthyBaseline, experiment);

        Assert.AreEqual(CanaryProgressionDecision.Advance, result.Decision,
            $"experimentMetrics 缺失 quality_score 字段不应回滚；rationale={result.Rationale}");
        Assert.AreEqual(5, result.CurrentPercentage);
    }

    // ===========================================================================
    // 5. QualityScoreEqualsThreshold_DoesNotTriggerRollback
    // 验证：quality_score == MinQualityScore 时不回滚（使用 < 而非 <=）
    // ===========================================================================
    [TestMethod]
    public async Task QualityScoreEqualsThreshold_DoesNotTriggerRollback()
    {
        var (service, cutover, time, store) = BuildService(new CanaryGateOptions
        {
            MinObservationPeriod = TimeSpan.FromSeconds(1),
            MinQualityScore = 0.3
        });
        var runId = await CreateScopedCanaryRunAsync(store);
        service.InitializeCanary(runId);

        // quality_score=0.3 = 阈值 0.3 → 不应回滚（< 而非 <=）
        var experiment = new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.02,
            ["quality_score"] = 0.3
        };

        time.Advance(TimeSpan.FromSeconds(2));
        var result = await service.AdvanceAsync(runId, "t-q-equal-1", null, HealthyBaseline, experiment);

        Assert.AreEqual(CanaryProgressionDecision.Advance, result.Decision,
            $"quality_score=0.3 = 阈值 0.3 不应回滚（< 而非 <=）；rationale={result.Rationale}");
    }

    // ===========================================================================
    // 6. EndToEnd_AdvanceAsyncInvokesRollbackOnLowQuality
    // 验证：端到端流程下，AdvanceAsync 检测到低 quality_score 后走 Rollback 路径
    // 并将 CutoverController 重置为 0%
    // ===========================================================================
    [TestMethod]
    public async Task EndToEnd_AdvanceAsyncInvokesRollbackOnLowQuality()
    {
        var (service, cutover, _, store) = BuildService(new CanaryGateOptions
        {
            MinObservationPeriod = TimeSpan.FromSeconds(1),
            MinQualityScore = 0.5  // 提高阈值
        });
        var runId = await CreateScopedCanaryRunAsync(store);
        service.InitializeCanary(runId);
        Assert.AreEqual(1, cutover.CutoverPercentage, "初始化后应为 1%");

        // 模拟 V2 严重退化：quality_score=0.1 << 阈值 0.5
        var badExperiment = new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,        // 健康
            ["p95_latency_ms"] = 110.0,    // 健康
            ["divergence_rate"] = 0.02,    // 健康
            ["quality_score"] = 0.1        // 严重退化（唯一触发回滚的指标）
        };

        var result = await service.AdvanceAsync(
            runId, "t-q-e2e-rollback", "idem-e2e-q",
            HealthyBaseline, badExperiment);

        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision,
            $"低 quality_score 应触发回滚；rationale={result.Rationale}");
        Assert.AreEqual(0, cutover.CutoverPercentage, "CutoverController 应重置为 0%");
        Assert.AreEqual(0, service.GetCurrentPercentage(runId), "run 百分比应回滚到 0");

        // 验证回滚审计记录已写入 store
        var transitions = await service.ListStageTransitionsAsync(runId);
        Assert.IsTrue(transitions.Count >= 1, "应至少有 1 条 transition 记录");
        var rollbackRecord = transitions.FirstOrDefault(t =>
            t.Decision == CanaryProgressionDecision.Rollback);
        Assert.IsNotNull(rollbackRecord, "应找到 Rollback 决策的审计记录");
        Assert.AreEqual("t-q-e2e-rollback", rollbackRecord!.TransitionId);
        Assert.AreEqual(0, rollbackRecord.ToPercentage, "回滚目标百分比应为 0");
    }

    // ===========================================================================
    // 7. DefaultOptions_HasMinQualityScorePointThree
    // 验证：默认 CanaryGateOptions.MinQualityScore = 0.3
    // ===========================================================================
    [TestMethod]
    public void DefaultOptions_HasMinQualityScorePointThree()
    {
        var options = new CanaryGateOptions();
        Assert.AreEqual(0.3, options.MinQualityScore, 0.0001,
            "默认 MinQualityScore 应为 0.3");
    }

    // ===========================================================================
    // 8. FromEnvironment_ParsesMinQualityScore
    // 验证：CC_CANARY_MIN_QUALITY_SCORE 环境变量被正确解析
    // ===========================================================================
    [TestMethod]
    public void FromEnvironment_ParsesMinQualityScore()
    {
        // 注意：环境变量修改可能影响并行测试；使用唯一变量名 + finally 还原
        var envVar = "CC_CANARY_MIN_QUALITY_SCORE";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "0.45");
            var options = CanaryGateOptions.FromEnvironment();
            Assert.AreEqual(0.45, options.MinQualityScore, 0.0001,
                "FromEnvironment 应解析 CC_CANARY_MIN_QUALITY_SCORE=0.45");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    // -----------------------------------------------------------------------
    // 辅助方法
    // -----------------------------------------------------------------------

    private static (CanaryProgressionService service, CutoverController cutover, CanaryAcceptanceTimeProvider time, IPipelineRunStore store) BuildService(
        CanaryGateOptions? options = null)
    {
        var time = new CanaryAcceptanceTimeProvider(BaseTime);
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController();
        var service = new CanaryProgressionService(
            store, cutover,
            options ?? new CanaryGateOptions { MinObservationPeriod = TimeSpan.FromSeconds(1) },
            time);
        return (service, cutover, time, store);
    }

    private static async Task<string> CreateScopedCanaryRunAsync(IPipelineRunStore store)
    {
        var runId = $"run-q-{Guid.NewGuid():N}";
        var now = BaseTime;
        var snapshot = new PipelineRunSnapshot
        {
            RunId = runId,
            ProposalId = "prop-q-test",
            ProposalVersion = OptimizationProposalVersion.Initial,
            Proposal = BuildProposal(),
            CurrentStage = OptimizationStage.ScopedCanary,
            Status = PipelineRunStatus.Running,
            StartedAt = now,
            UpdatedAt = now,
            CompletedAt = null,
            RollbackReason = null,
            StageMetrics = Array.Empty<BaselineComparison>(),
            Revision = 1,
            LeaseOwner = null,
            LeaseExpiresAt = null,
            LastTransitionId = null
        };
        var created = await store.TryCreateRunAsync(snapshot);
        Assert.IsTrue(created, "测试 run 创建失败：TryCreateRunAsync 应返回 true");
        return runId;
    }

    private static OptimizationProposal BuildProposal() => new()
    {
        ProposalId = "prop-q-test",
        Version = OptimizationProposalVersion.Initial,
        Title = "Quality Score Test",
        Hypothesis = "H",
        TargetComponent = OptimizationTargetComponent.PackagePolicy,
        Status = OptimizationProposalStatus.ExperimentReady,
        ExpectedGains = new[]
        {
            new ExpectedGain("duration_ms", -350.0, 0.85, Array.Empty<string>())
        },
        Risks = new[]
        {
            new RiskAssessment("R1", "desc", RiskSeverity.Low, Array.Empty<string>(), Array.Empty<string>())
        },
        RollbackConditions = new[]
        {
            new RollbackCondition("error_rate", ComparisonOperator.GreaterThan, 0.05, "error rate > 5%")
        }
    };
}
