using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;

namespace ContextCore.Tests;

// ===========================================================================
// Production Canary Gate — 验收测试（8 项）
//
// 覆盖范围：
// 1. CanaryGate_AdvancesThroughPercentageLadder — 百分比阶梯渐进推进
// 2. CanaryGate_RollbacksOnHighDivergence — parity 差异率超阈值回滚
// 3. CanaryGate_RollbacksOnHighErrorRate — 错误率差超阈值回滚
// 4. CanaryGate_RollbacksOnLatencyRegression — p95 延迟倍数超阈值回滚
// 5. CanaryGate_RespectsMinObservationPeriod — 最小观察时长约束
// 6. CanaryGate_IdempotentAdvance — transitionId 幂等去重
// 7. CanaryGate_StageTransitionsRecordedToAuditTable — stage_transitions 审计记录
// 8. CanaryGate_HundredPercentPromotesToV2Only — 100% 晋升为 V2 only
//
// 设计原则：
// - 直接测试 CanaryProgressionService（覆盖渐进推进/回滚/幂等/审计核心逻辑）
// - 使用可推进时间的 FakeTimeProvider 控制观察时长（避免真实等待）
// - 使用真实 InMemoryPipelineRunStore + CutoverController 组件（不 stub 决策内核）
// - 所有代码注释使用中文
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.8")]
public sealed class R28B_CanaryGateTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    // 健康指标（不触发任何回滚阈值）
    private static readonly IReadOnlyDictionary<string, double> HealthyBaseline =
        new Dictionary<string, double>
        {
            ["error_rate"] = 0.01,
            ["p95_latency_ms"] = 100.0
        };

    private static readonly IReadOnlyDictionary<string, double> HealthyExperiment =
        new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.02
        };

    // ===========================================================================
    // 1. CanaryGate_AdvancesThroughPercentageLadder
    // 验证：健康指标 + 观察时长达标时，按 1→5→10→25→50→100 渐进推进
    // ===========================================================================
    [TestMethod]
    public async Task CanaryGate_AdvancesThroughPercentageLadder()
    {
        var (service, cutover, time, store) = BuildService(new CanaryGateOptions
        {
            PercentageLadder = [1, 5, 10, 25, 50, 100],
            MinObservationPeriod = TimeSpan.FromSeconds(1)
        });
        var runId = await CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);
        Assert.AreEqual(1, cutover.CutoverPercentage, "初始化后应为阶梯首档 1%");

        var ladder = new[] { 1, 5, 10, 25, 50, 100 };
        for (var i = 1; i < ladder.Length; i++)
        {
            // 推进时间超过最小观察时长
            time.Advance(TimeSpan.FromSeconds(2));
            var transitionId = $"t-advance-{runId}-{i}";
            var result = await service.AdvanceAsync(
                runId, transitionId, idempotencyKey: null,
                HealthyBaseline, HealthyExperiment);

            Assert.AreEqual(CanaryProgressionDecision.Advance, result.Decision,
                $"第 {i} 次推进应为 Advance；rationale={result.Rationale}");
            Assert.AreEqual(ladder[i], result.CurrentPercentage,
                $"推进后百分比应为 {ladder[i]}%");
            Assert.AreEqual(ladder[i], cutover.CutoverPercentage,
                $"CutoverController 应同步到 {ladder[i]}%");
            Assert.IsTrue(result.Applied, $"第 {i} 次推进应已应用");
        }

        // 末档 100% 后再推进应返回 Promoted（不再继续推进）
        time.Advance(TimeSpan.FromSeconds(2));
        var promotedResult = await service.AdvanceAsync(
            runId, $"t-advance-{runId}-final", idempotencyKey: null,
            HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Promoted, promotedResult.Decision,
            $"达 100% 后应返回 Promoted；rationale={promotedResult.Rationale}");
        Assert.AreEqual(100, cutover.CutoverPercentage, "CutoverController 应保持 100%");
    }

    // ===========================================================================
    // 2. CanaryGate_RollbacksOnHighDivergence
    // 验证：divergence_rate > MaxDivergenceRate 时触发自动回滚
    // ===========================================================================
    [TestMethod]
    public async Task CanaryGate_RollbacksOnHighDivergence()
    {
        var (service, cutover, _, store) = BuildService();
        var runId = await CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);
        Assert.AreEqual(1, cutover.CutoverPercentage, "初始化后应为 1%");

        // divergence_rate=0.10 > 阈值 0.05 → 触发回滚
        var badExperiment = new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.10
        };

        var result = await service.AdvanceAsync(
            runId, "t-rollback-divergence", idempotencyKey: null,
            HealthyBaseline, badExperiment);

        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision,
            $"divergence_rate 超阈值应触发回滚；rationale={result.Rationale}");
        Assert.AreEqual(0, cutover.CutoverPercentage, "回滚后 CutoverController 应重置为 0%（全 Legacy）");
        Assert.AreEqual(0, service.GetCurrentPercentage(runId), "回滚后当前百分比应为 0");
    }

    // ===========================================================================
    // 3. CanaryGate_RollbacksOnHighErrorRate
    // 验证：error_rate 差 > MaxErrorRateDelta 时触发自动回滚
    // ===========================================================================
    [TestMethod]
    public async Task CanaryGate_RollbacksOnHighErrorRate()
    {
        var (service, cutover, _, store) = BuildService();
        var runId = await CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);

        // baseline error_rate=0.01, experiment error_rate=0.05 → delta=0.04 > 阈值 0.02
        var baseline = new Dictionary<string, double> { ["error_rate"] = 0.01, ["p95_latency_ms"] = 100.0 };
        var badExperiment = new Dictionary<string, double>
        {
            ["error_rate"] = 0.05,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.02
        };

        var result = await service.AdvanceAsync(
            runId, "t-rollback-errorrate", idempotencyKey: null,
            baseline, badExperiment);

        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision,
            $"error_rate 差超阈值应触发回滚；rationale={result.Rationale}");
        Assert.AreEqual(0, cutover.CutoverPercentage, "回滚后 CutoverController 应重置为 0%");
        Assert.AreEqual(0, service.GetCurrentPercentage(runId), "回滚后当前百分比应为 0");
    }

    // ===========================================================================
    // 4. CanaryGate_RollbacksOnLatencyRegression
    // 验证：p95 延迟倍数 > MaxLatencyMultiplier 时触发自动回滚
    // ===========================================================================
    [TestMethod]
    public async Task CanaryGate_RollbacksOnLatencyRegression()
    {
        var (service, cutover, _, store) = BuildService();
        var runId = await CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);

        // baseline p95=100ms, experiment p95=250ms → 倍数=2.5 > 阈值 2.0
        var baseline = new Dictionary<string, double> { ["error_rate"] = 0.01, ["p95_latency_ms"] = 100.0 };
        var badExperiment = new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 250.0,
            ["divergence_rate"] = 0.02
        };

        var result = await service.AdvanceAsync(
            runId, "t-rollback-latency", idempotencyKey: null,
            baseline, badExperiment);

        Assert.AreEqual(CanaryProgressionDecision.Rollback, result.Decision,
            $"p95 延迟倍数超阈值应触发回滚；rationale={result.Rationale}");
        Assert.AreEqual(0, cutover.CutoverPercentage, "回滚后 CutoverController 应重置为 0%");
        Assert.AreEqual(0, service.GetCurrentPercentage(runId), "回滚后当前百分比应为 0");
    }

    // ===========================================================================
    // 5. CanaryGate_RespectsMinObservationPeriod
    // 验证：观察时长不足时返回 Hold；达标后才允许 Advance
    // ===========================================================================
    [TestMethod]
    public async Task CanaryGate_RespectsMinObservationPeriod()
    {
        var (service, cutover, time, store) = BuildService(new CanaryGateOptions
        {
            PercentageLadder = [1, 5, 100],
            MinObservationPeriod = TimeSpan.FromSeconds(10)
        });
        var runId = await CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);
        Assert.AreEqual(1, cutover.CutoverPercentage, "初始化后应为 1%");

        // 立即推进（未达观察时长）→ 应返回 Hold
        var holdResult = await service.AdvanceAsync(
            runId, "t-hold-1", idempotencyKey: null,
            HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Hold, holdResult.Decision,
            $"观察时长不足应返回 Hold；rationale={holdResult.Rationale}");
        Assert.AreEqual(1, service.GetCurrentPercentage(runId), "Hold 不应改变百分比");
        Assert.IsFalse(holdResult.Applied, "Hold 不应标记为已应用");

        // 仅推进 5 秒（仍不足 10 秒）→ 应返回 Hold
        time.Advance(TimeSpan.FromSeconds(5));
        var holdResult2 = await service.AdvanceAsync(
            runId, "t-hold-2", idempotencyKey: null,
            HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Hold, holdResult2.Decision,
            "观察时长仍不足应继续返回 Hold");
        Assert.AreEqual(1, service.GetCurrentPercentage(runId));

        // 再推进 6 秒（累计 11 秒 > 10 秒）→ 应返回 Advance
        time.Advance(TimeSpan.FromSeconds(6));
        var advanceResult = await service.AdvanceAsync(
            runId, "t-advance-1", idempotencyKey: null,
            HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Advance, advanceResult.Decision,
            $"观察时长达标后应返回 Advance；rationale={advanceResult.Rationale}");
        Assert.AreEqual(5, service.GetCurrentPercentage(runId), "应推进到 5%");
        Assert.AreEqual(5, cutover.CutoverPercentage);
    }

    // ===========================================================================
    // 6. CanaryGate_IdempotentAdvance
    // 验证：相同 transitionId 重复调用不产生重复推进（幂等去重）
    // ===========================================================================
    [TestMethod]
    public async Task CanaryGate_IdempotentAdvance()
    {
        var (service, cutover, time, store) = BuildService();
        var runId = await CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);
        time.Advance(TimeSpan.FromSeconds(2));

        var transitionId = "t-idempotent-001";
        var idempotencyKey = "idem-key-001";

        // 第一次推进：1% → 5%
        var firstResult = await service.AdvanceAsync(
            runId, transitionId, idempotencyKey,
            HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Advance, firstResult.Decision,
            "首次推进应为 Advance");
        Assert.AreEqual(5, firstResult.CurrentPercentage);
        Assert.AreEqual(5, cutover.CutoverPercentage);
        Assert.IsTrue(firstResult.Applied, "首次推进应已应用");

        // 第二次推进（相同 transitionId）：应幂等返回，不重复推进
        var secondResult = await service.AdvanceAsync(
            runId, transitionId, idempotencyKey,
            HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Hold, secondResult.Decision,
            $"相同 transitionId 应幂等返回 Hold；rationale={secondResult.Rationale}");
        Assert.AreEqual(5, secondResult.CurrentPercentage, "幂等返回应保持当前百分比");
        Assert.IsFalse(secondResult.Applied, "幂等返回不应标记为已应用");
        Assert.AreEqual(5, cutover.CutoverPercentage, "CutoverController 不应被重复推进");

        // 使用不同 transitionId 推进：应正常推进 5% → 10%
        time.Advance(TimeSpan.FromSeconds(2));
        var thirdResult = await service.AdvanceAsync(
            runId, "t-idempotent-002", "idem-key-002",
            HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Advance, thirdResult.Decision,
            "不同 transitionId 应正常推进");
        Assert.AreEqual(10, thirdResult.CurrentPercentage);
        Assert.AreEqual(10, cutover.CutoverPercentage);
    }

    // ===========================================================================
    // 7. CanaryGate_StageTransitionsRecordedToAuditTable
    // 验证：每次推进/回滚都记录到 stage_transitions 审计表（in-memory 投影）
    // ===========================================================================
    [TestMethod]
    public async Task CanaryGate_StageTransitionsRecordedToAuditTable()
    {
        var (service, _, time, store) = BuildService(new CanaryGateOptions
        {
            PercentageLadder = [1, 5, 100],
            MinObservationPeriod = TimeSpan.FromSeconds(1)
        });
        var runId = await CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);

        // 推进 1：1% → 5%
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(runId, "t-audit-1", "idem-1",
            HealthyBaseline, HealthyExperiment);

        // 推进 2：5% → 100%
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(runId, "t-audit-2", "idem-2",
            HealthyBaseline, HealthyExperiment);

        var transitions = await service.ListStageTransitionsAsync(runId);

        Assert.AreEqual(2, transitions.Count, "应有 2 条审计记录");
        Assert.AreEqual("t-audit-1", transitions[0].TransitionId);
        Assert.AreEqual(1, transitions[0].FromPercentage);
        Assert.AreEqual(5, transitions[0].ToPercentage);
        Assert.AreEqual(CanaryProgressionDecision.Advance, transitions[0].Decision);
        Assert.AreEqual("idem-1", transitions[0].IdempotencyKey);

        Assert.AreEqual("t-audit-2", transitions[1].TransitionId);
        Assert.AreEqual(5, transitions[1].FromPercentage);
        Assert.AreEqual(100, transitions[1].ToPercentage);
        Assert.AreEqual(CanaryProgressionDecision.Advance, transitions[1].Decision);
        Assert.AreEqual("idem-2", transitions[1].IdempotencyKey);

        // 验证幂等推进不会产生重复审计记录
        await service.AdvanceAsync(runId, "t-audit-1", "idem-1",
            HealthyBaseline, HealthyExperiment);
        var transitionsAfterIdempotent = await service.ListStageTransitionsAsync(runId);
        Assert.AreEqual(2, transitionsAfterIdempotent.Count, "幂等推进不应新增审计记录");
    }

    // ===========================================================================
    // 8. CanaryGate_HundredPercentPromotesToV2Only
    // 验证：达 100% 后 EvaluateAsync 返回 Promoted；
    // CutoverController.CutoverPercentage=100 使所有请求走 V2 路径
    // ===========================================================================
    [TestMethod]
    public async Task CanaryGate_HundredPercentPromotesToV2Only()
    {
        var (service, cutover, time, store) = BuildService(new CanaryGateOptions
        {
            PercentageLadder = [1, 50, 100],
            MinObservationPeriod = TimeSpan.FromSeconds(1)
        });
        var runId = await CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);
        Assert.AreEqual(1, cutover.CutoverPercentage);

        // 推进到 50%
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(runId, "t-promote-1", null,
            HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(50, cutover.CutoverPercentage);

        // 推进到 100%
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(runId, "t-promote-2", null,
            HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(100, cutover.CutoverPercentage, "应推进到 100%");

        // 100% 后评估应返回 Promoted
        var evaluation = await service.EvaluateAsync(runId, HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Promoted, evaluation.Decision,
            $"100% 后应返回 Promoted；rationale={evaluation.Rationale}");
        Assert.AreEqual(100, evaluation.CurrentPercentage);

        // CutoverController=100 时所有请求都走 V2（V2 only）
        Assert.IsTrue(cutover.ShouldUseV2("req-001"), "100% 时所有请求应走 V2 路径");
        Assert.IsTrue(cutover.ShouldUseV2("req-002"), "100% 时所有请求应走 V2 路径");
        Assert.IsTrue(cutover.ShouldUseV2("any-request-id"), "100% 时所有请求应走 V2 路径");
    }

    // -----------------------------------------------------------------------
    // 辅助方法与类型
    // -----------------------------------------------------------------------

    /// <summary>构建 CanaryProgressionService 测试套件。</summary>
    private static (CanaryProgressionService service, CutoverController cutover, FakeTimeProvider time, IPipelineRunStore store) BuildService(
        CanaryGateOptions? options = null)
    {
        var time = new FakeTimeProvider(BaseTime);
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController();
        var service = new CanaryProgressionService(
            store, cutover,
            options ?? new CanaryGateOptions { MinObservationPeriod = TimeSpan.FromSeconds(1) },
            time);
        return (service, cutover, time, store);
    }

    /// <summary>在 store 中创建一个处于 ScopedCanary 阶段的 pipeline run（供 CanaryProgressionService 直接测试）。</summary>
    private static async Task<string> CreateScopedCanaryRunAsync(IPipelineRunStore store)
    {
        var runId = $"run-canary-{Guid.NewGuid():N}";
        var now = BaseTime;
        var snapshot = new PipelineRunSnapshot
        {
            RunId = runId,
            ProposalId = "prop-canary-test",
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
        ProposalId = "prop-canary-test",
        Version = OptimizationProposalVersion.Initial,
        Title = "Canary Gate Test",
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

    /// <summary>可推进时间的 TimeProvider：测试用，通过 Advance 推进时间。</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _current;

        public FakeTimeProvider(DateTimeOffset initial)
        {
            _current = initial;
        }

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan delta) => _current = _current.Add(delta);
    }
}
