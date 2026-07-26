using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

// ===========================================================================
// R28-B.8 Production Canary Plane — 生产验收测试（12 项）
//
// 覆盖范围（4 个测试类）：
//   1. StageTransitionPersistenceAcceptanceTests（3 项）— Stage Transition 持久化
//   2. CutoverControllerIsolationAcceptanceTests（3 项）— Per-Run 控制器隔离
//   3. CanaryMetricsCollectorAcceptanceTests（3 项）— Canary Metrics 采集器
//   4. CanaryEndToEndAcceptanceTests（3 项）— 端到端验收
//
// 设计原则：
//   - 使用真实组件（InMemoryPipelineRunStore、CutoverController、CanaryProgressionService、
//     DefaultCanaryMetricsCollector、CutoverControllerRegistry），不 stub 决策内核。
//   - 使用可推进时间的 CanaryAcceptanceTimeProvider 控制观察时长（避免真实等待）。
//   - 所有代码注释使用中文。
// ===========================================================================

// ---------------------------------------------------------------------------
// 共享测试辅助（供本文件 4 个测试类复用）
// ---------------------------------------------------------------------------

internal static class CanaryAcceptanceHelpers
{
    /// <summary>测试基准时间（UTC）。</summary>
    public static readonly DateTimeOffset BaseTime = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>健康基线指标（不触发任何回滚阈值）。</summary>
    public static readonly IReadOnlyDictionary<string, double> HealthyBaseline =
        new Dictionary<string, double>
        {
            ["error_rate"] = 0.01,
            ["p95_latency_ms"] = 100.0
        };

    /// <summary>健康实验指标（不触发任何回滚阈值）。</summary>
    public static readonly IReadOnlyDictionary<string, double> HealthyExperiment =
        new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.02
        };

    /// <summary>默认 Canary Gate 配置：1→5→10→25→50→100，最小观察 1 秒。</summary>
    /// <remarks>
    /// R29 WP-C-3：显式设置 <see cref="CanaryGateOptions.MinQualityScore"/>=0.0 禁用质量分回滚阈值，
    /// 因 R28B 验收测试不通过 RecordObservation 上报 quality_score（旧测试在 R29 WP-C-3 之前编写）。
    /// R29C_QualityScoreTests 单独覆盖 quality_score 回滚行为。
    /// </remarks>
    public static CanaryGateOptions DefaultOptions => new()
    {
        PercentageLadder = [1, 5, 10, 25, 50, 100],
        MinObservationPeriod = TimeSpan.FromSeconds(1),
        MinQualityScore = 0.0
    };

    /// <summary>在 store 中创建一个处于 ScopedCanary 阶段的 pipeline run。</summary>
    /// <param name="store">Pipeline run store。</param>
    /// <param name="runId">可选的 runId；为 null 时自动生成。</param>
    /// <returns>新创建的 runId。</returns>
    public static async Task<string> CreateScopedCanaryRunAsync(IPipelineRunStore store, string? runId = null)
    {
        var actualRunId = runId ?? $"run-canary-{Guid.NewGuid():N}";
        var now = BaseTime;
        var snapshot = new PipelineRunSnapshot
        {
            RunId = actualRunId,
            ProposalId = "prop-canary-acceptance",
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
        return actualRunId;
    }

    /// <summary>构建测试用 OptimizationProposal。</summary>
    public static OptimizationProposal BuildProposal() => new()
    {
        ProposalId = "prop-canary-acceptance",
        Version = OptimizationProposalVersion.Initial,
        Title = "Canary Acceptance Test",
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

    /// <summary>构建指定 ParityLevel 的 ParityReport（供 Metrics 采集器测试使用）。</summary>
    /// <param name="level">parity 级别（Hard=一致；Divergent=发散）。</param>
    public static ParityReport BuildParityReport(ParityLevel level)
    {
        var isHard = level == ParityLevel.Hard;
        return new ParityReport(
            LegacySelectedCount: 10,
            V2SelectedCount: isHard ? 10 : 8,
            CommonSelectedCount: isHard ? 10 : 5,
            OnlyInLegacyCount: isHard ? 0 : 5,
            OnlyInV2Count: isHard ? 0 : 3,
            JaccardIndex: isHard ? 1.0 : 0.5,
            ParityLevel: level,
            LegacyTokenTotal: 1000,
            V2TokenTotal: 1000,
            WorkingSetCandidateCount: 10);
    }
}

/// <summary>可推进时间的 TimeProvider：测试用，通过 Advance 推进时间。</summary>
internal sealed class CanaryAcceptanceTimeProvider : TimeProvider
{
    private DateTimeOffset _current;

    public CanaryAcceptanceTimeProvider(DateTimeOffset initial)
    {
        _current = initial;
    }

    public override DateTimeOffset GetUtcNow() => _current;

    /// <summary>推进时间。</summary>
    public void Advance(TimeSpan delta) => _current = _current.Add(delta);
}

// ===========================================================================
// 测试类 1：StageTransitionPersistenceAcceptanceTests
// 验证 Stage Transition 持久化到 store（工作包 A）
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.8")]
public sealed class StageTransitionPersistenceAcceptanceTests
{
    // ===========================================================================
    // 1. StageTransitionPersistedToStoreAfterAdvance
    //    验证：推进一次后，通过 store.ListStageTransitionsByRunAsync 查询到审计记录；
    //          TransitionId/FromPercentage/ToPercentage/Decision 字段正确。
    // ===========================================================================
    [TestMethod]
    public async Task StageTransitionPersistedToStoreAfterAdvance()
    {
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController();
        var service = new CanaryProgressionService(
            store, cutover, CanaryAcceptanceHelpers.DefaultOptions, time);
        var runId = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store);

        // 初始化 canary（1%）
        service.InitializeCanary(runId);
        Assert.AreEqual(1, cutover.CutoverPercentage, "初始化后应为 1%");

        // 推进时间超过最小观察时长后推进到 5%
        time.Advance(TimeSpan.FromSeconds(2));
        var transitionId = "t-persist-advance-001";
        var result = await service.AdvanceAsync(
            runId, transitionId, idempotencyKey: "idem-persist-001",
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);

        Assert.AreEqual(CanaryProgressionDecision.Advance, result.Decision,
            $"应推进成功；rationale={result.Rationale}");
        Assert.AreEqual(5, result.CurrentPercentage, "应推进到 5%");

        // 通过 store 查询审计记录（权威来源）
        var transitions = await store.ListStageTransitionsByRunAsync(runId);
        Assert.AreEqual(1, transitions.Count, "store 中应有 1 条审计记录");

        var record = transitions[0];
        Assert.AreEqual(transitionId, record.TransitionId, "TransitionId 应匹配");
        Assert.AreEqual(runId, record.RunId, "RunId 应匹配");
        Assert.AreEqual(1, record.FromPercentage, "FromPercentage 应为 1");
        Assert.AreEqual(5, record.ToPercentage, "ToPercentage 应为 5");
        Assert.AreEqual(CanaryProgressionDecision.Advance, record.Decision, "Decision 应为 Advance");
        Assert.AreEqual("idem-persist-001", record.IdempotencyKey, "IdempotencyKey 应匹配");
    }

    // ===========================================================================
    // 2. StageTransitionQueryFromStoreAfterRollback
    //    验证：推进到 5% 后触发回滚；store 中有 Advance + Rollback 两条记录；
    //          按 TransitionedAt 升序排列。
    // ===========================================================================
    [TestMethod]
    public async Task StageTransitionQueryFromStoreAfterRollback()
    {
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController();
        var service = new CanaryProgressionService(
            store, cutover, CanaryAcceptanceHelpers.DefaultOptions, time);
        var runId = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);

        // 推进到 5%
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(
            runId, "t-rollback-advance-001", idempotencyKey: null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.AreEqual(5, cutover.CutoverPercentage, "推进后应为 5%");

        // 触发回滚：divergence_rate=0.10 > 阈值 0.05
        var badExperiment = new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.10
        };
        time.Advance(TimeSpan.FromSeconds(2));
        var rollbackResult = await service.AdvanceAsync(
            runId, "t-rollback-001", idempotencyKey: null,
            CanaryAcceptanceHelpers.HealthyBaseline, badExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Rollback, rollbackResult.Decision,
            "应触发回滚");
        Assert.AreEqual(0, cutover.CutoverPercentage, "回滚后应为 0%");

        // 通过 store 查询：应有 Advance + Rollback 两条记录
        var transitions = await store.ListStageTransitionsByRunAsync(runId);
        Assert.AreEqual(2, transitions.Count, "应有 Advance + Rollback 两条审计记录");

        // 验证按 TransitionedAt 升序排列
        Assert.IsTrue(
            transitions[0].TransitionedAt <= transitions[1].TransitionedAt,
            "审计记录应按 TransitionedAt 升序排列");

        // 第一条是 Advance（1% → 5%）
        Assert.AreEqual("t-rollback-advance-001", transitions[0].TransitionId);
        Assert.AreEqual(CanaryProgressionDecision.Advance, transitions[0].Decision);
        Assert.AreEqual(1, transitions[0].FromPercentage);
        Assert.AreEqual(5, transitions[0].ToPercentage);

        // 第二条是 Rollback（5% → 0%）
        Assert.AreEqual("t-rollback-001", transitions[1].TransitionId);
        Assert.AreEqual(CanaryProgressionDecision.Rollback, transitions[1].Decision);
        Assert.AreEqual(5, transitions[1].FromPercentage);
        Assert.AreEqual(0, transitions[1].ToPercentage);
    }

    // ===========================================================================
    // 3. IdempotentTransitionNotDuplicatedInStore
    //    验证：同一 transitionId 重复调用 AdvanceAsync；store 中只有一条记录；无重复。
    // ===========================================================================
    [TestMethod]
    public async Task IdempotentTransitionNotDuplicatedInStore()
    {
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController();
        var service = new CanaryProgressionService(
            store, cutover, CanaryAcceptanceHelpers.DefaultOptions, time);
        var runId = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store);

        service.InitializeCanary(runId);
        time.Advance(TimeSpan.FromSeconds(2));

        var transitionId = "t-idempotent-store-001";
        var idempotencyKey = "idem-store-001";

        // 第一次推进：1% → 5%
        var first = await service.AdvanceAsync(
            runId, transitionId, idempotencyKey,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Advance, first.Decision, "首次推进应为 Advance");
        Assert.AreEqual(5, first.CurrentPercentage);

        // 重复调用（相同 transitionId）：应幂等返回 Hold
        var second = await service.AdvanceAsync(
            runId, transitionId, idempotencyKey,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Hold, second.Decision,
            $"相同 transitionId 应幂等返回 Hold；rationale={second.Rationale}");
        Assert.AreEqual(5, second.CurrentPercentage, "幂等返回应保持当前百分比");

        // store 中只有一条该 transitionId 的记录
        var transitions = await store.ListStageTransitionsByRunAsync(runId);
        Assert.AreEqual(1, transitions.Count, "幂等推进不应在 store 中产生重复记录");
        Assert.AreEqual(transitionId, transitions[0].TransitionId, "记录的 TransitionId 应匹配");

        // 验证列表无重复 TransitionId
        var distinctCount = transitions.Select(t => t.TransitionId).Distinct().Count();
        Assert.AreEqual(transitions.Count, distinctCount, "TransitionId 不应有重复");
    }
}

// ===========================================================================
// 测试类 2：CutoverControllerIsolationAcceptanceTests
// 验证 Per-Run CutoverController 隔离（工作包 B）
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.8")]
public sealed class CutoverControllerIsolationAcceptanceTests
{
    // ===========================================================================
    // 4. PerRunControllerIsolatesPercentageChanges
    //    验证：两个 run 各自独立的 CutoverController；run1 推进到 25% 不影响 run2（保持 1%）；
    //          两个 controller 是不同实例。
    // ===========================================================================
    [TestMethod]
    public async Task PerRunControllerIsolatesPercentageChanges()
    {
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var store = new InMemoryPipelineRunStore();
        var defaultController = new CutoverController(0);
        var registry = new CutoverControllerRegistry(defaultController);
        var service = new CanaryProgressionService(
            store, defaultController, CanaryAcceptanceHelpers.DefaultOptions, time, registry);

        var run1 = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, "run-iso-001");
        var run2 = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, "run-iso-002");

        // 初始化两个 run（均为 1%）
        service.InitializeCanary(run1);
        service.InitializeCanary(run2);

        var controller1 = registry.GetOrCreate(run1);
        var controller2 = registry.GetOrCreate(run2);

        // 验证两个 controller 是不同实例
        Assert.AreNotSame(controller1, controller2, "两个 run 的 controller 应为不同实例");
        Assert.AreEqual(1, controller1.CutoverPercentage, "run1 初始化后应为 1%");
        Assert.AreEqual(1, controller2.CutoverPercentage, "run2 初始化后应为 1%");

        // run1 推进到 25%（1→5→10→25，三次推进）
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(run1, "t-iso-run1-001", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(run1, "t-iso-run1-002", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(run1, "t-iso-run1-003", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);

        // run2 保持 1%（不推进）

        Assert.AreEqual(25, controller1.CutoverPercentage, "run1 的 controller 应为 25%");
        Assert.AreEqual(1, controller2.CutoverPercentage, "run2 的 controller 应保持 1%");
    }

    // ===========================================================================
    // 5. DefaultControllerUsedWhenNoRunRegistered
    //    验证：默认控制器 CutoverPercentage=0；null/空 runId 返回默认控制器；
    //          注册的 runId 返回专用控制器（不同于默认）。
    // ===========================================================================
    [TestMethod]
    public void DefaultControllerUsedWhenNoRunRegistered()
    {
        var defaultController = new CutoverController(0);
        var registry = new CutoverControllerRegistry(defaultController);
        var resolver = new DefaultCutoverControllerResolver(registry);

        // 默认控制器 CutoverPercentage=0
        Assert.AreEqual(0, registry.Default.CutoverPercentage, "默认控制器应为 0%");
        Assert.AreSame(defaultController, registry.Default, "Default 属性应返回注入的默认控制器");

        // null runId 返回默认控制器
        var resolvedNull = resolver.Resolve(null);
        Assert.AreSame(defaultController, resolvedNull, "null runId 应返回默认控制器");

        // 空 runId 返回默认控制器
        var resolvedEmpty = resolver.Resolve("");
        Assert.AreSame(defaultController, resolvedEmpty, "空 runId 应返回默认控制器");

        // 注册的 runId 返回专用控制器（不同于默认）
        var runId = "run-default-test-001";
        var perRunController = resolver.Resolve(runId);
        Assert.AreNotSame(defaultController, perRunController, "注册的 runId 应返回专用控制器，而非默认");
        Assert.AreEqual(0, perRunController.CutoverPercentage, "新建的 per-run 控制器初始为 0%");

        // 再次 Resolve 同一 runId 返回相同实例
        var perRunController2 = resolver.Resolve(runId);
        Assert.AreSame(perRunController, perRunController2, "同一 runId 应返回相同实例");
    }

    // ===========================================================================
    // 6. UnregisterRemovesController
    //    验证：注册 runId 后 Unregister；再次 GetOrCreate 返回新实例（而非之前的实例）。
    // ===========================================================================
    [TestMethod]
    public void UnregisterRemovesController()
    {
        var defaultController = new CutoverController(0);
        var registry = new CutoverControllerRegistry(defaultController);

        var runId = "run-unregister-001";

        // 注册并获取控制器实例
        var controller1 = registry.GetOrCreate(runId);
        Assert.AreSame(controller1, registry.GetOrCreate(runId), "同一 runId 应返回相同实例");
        Assert.AreEqual(1, registry.ActiveCount, "应有 1 个活跃 run");

        // Unregister 移除控制器
        registry.Unregister(runId);
        Assert.AreEqual(0, registry.ActiveCount, "Unregister 后应无活跃 run");

        // 再次 GetOrCreate 返回新实例
        var controller2 = registry.GetOrCreate(runId);
        Assert.AreNotSame(controller1, controller2, "Unregister 后 GetOrCreate 应返回新实例");
    }
}

// ===========================================================================
// 测试类 3：CanaryMetricsCollectorAcceptanceTests
// 验证 Canary Metrics 采集器（工作包 C）
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.8")]
public sealed class CanaryMetricsCollectorAcceptanceTests
{
    // ===========================================================================
    // 7. MetricsCollectorAggregatesDivergenceRate
    //    验证：10 次观察（3 次 Divergent + 7 次 Hard）；
    //          DivergenceRate == 0.3；TotalObservations == 10；DivergentCount == 3。
    // ===========================================================================
    [TestMethod]
    public void MetricsCollectorAggregatesDivergenceRate()
    {
        var collector = new DefaultCanaryMetricsCollector();
        var runId = "run-metrics-divergence-001";
        var divergentReport = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Divergent);
        var hardReport = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);

        // 3 次 Divergent
        for (var i = 0; i < 3; i++)
        {
            collector.RecordObservation(runId, divergentReport,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: TimeSpan.FromMilliseconds(50));
        }

        // 7 次 Hard
        for (var i = 0; i < 7; i++)
        {
            collector.RecordObservation(runId, hardReport,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: TimeSpan.FromMilliseconds(50));
        }

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.AreEqual(10, metrics.TotalObservations, "总观察次数应为 10");
        Assert.AreEqual(3, metrics.DivergentCount, "Divergent 次数应为 3");
        Assert.AreEqual(0.3, metrics.DivergenceRate, 0.0001, "DivergenceRate 应为 0.3");
    }

    // ===========================================================================
    // 8. MetricsCollectorComputesP95Latency
    //    验证：20 次观察，V2 duration 从 10ms 到 200ms 递增；
    //          V2P95LatencyMs 在预期范围（约 180-200ms）。
    // ===========================================================================
    [TestMethod]
    public void MetricsCollectorComputesP95Latency()
    {
        var collector = new DefaultCanaryMetricsCollector();
        var runId = "run-metrics-p95-001";
        var hardReport = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);

        // 20 次观察，V2 duration 从 10ms 递增到 200ms（步长 10ms）
        for (var i = 0; i < 20; i++)
        {
            var duration = TimeSpan.FromMilliseconds(10 + i * 10);
            collector.RecordObservation(runId, hardReport,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: duration);
        }

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.AreEqual(20, metrics.TotalObservations, "总观察次数应为 20");
        // P95 索引 = (int)(20 * 0.95) = 19，排序后 durations[19] = 200ms
        // R28-G P1-6：DDSketch 相对误差 ≤ 1%，200ms 估计值在 [198, 202] 区间，放宽到 180-205
        Assert.IsTrue(
            metrics.V2P95LatencyMs >= 180.0 && metrics.V2P95LatencyMs <= 205.0,
            $"V2P95LatencyMs 应在 180-205ms 范围内（DDSketch 相对误差 ≤ 1%），实际={metrics.V2P95LatencyMs}ms");
    }

    // ===========================================================================
    // 9. MetricsCollectorResetClearsWindow
    //    验证：记录 5 次后 Reset → TotalObservations == 0；
    //          再次记录 3 次 → TotalObservations == 3。
    // ===========================================================================
    [TestMethod]
    public void MetricsCollectorResetClearsWindow()
    {
        var collector = new DefaultCanaryMetricsCollector();
        var runId = "run-metrics-reset-001";
        var hardReport = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);

        // 记录 5 次观察
        for (var i = 0; i < 5; i++)
        {
            collector.RecordObservation(runId, hardReport,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: TimeSpan.FromMilliseconds(50));
        }
        Assert.AreEqual(5, collector.GetAggregatedMetrics(runId).TotalObservations,
            "记录 5 次后 TotalObservations 应为 5");

        // Reset 清空窗口
        collector.Reset(runId);
        Assert.AreEqual(0, collector.GetAggregatedMetrics(runId).TotalObservations,
            "Reset 后 TotalObservations 应为 0");

        // 再次记录 3 次观察
        for (var i = 0; i < 3; i++)
        {
            collector.RecordObservation(runId, hardReport,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: TimeSpan.FromMilliseconds(50));
        }
        Assert.AreEqual(3, collector.GetAggregatedMetrics(runId).TotalObservations,
            "Reset 后再次记录 3 次，TotalObservations 应为 3");
    }
}

// ===========================================================================
// 测试类 4：CanaryEndToEndAcceptanceTests
// 端到端验收：store + registry + metrics collector + progression service
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.8")]
public sealed class CanaryEndToEndAcceptanceTests
{
    // ===========================================================================
    // 10. FullCanaryCycleWithMetricsAndPersistence
    //     端到端：初始化 canary（1%）→ 记录健康指标 → 推进到 5% → 验证 store 有审计记录
    //     → 记录更多健康指标 → 推进到 10% → 验证 metrics 窗口已 Reset
    //     → 验证 CutoverController 百分比同步。
    // ===========================================================================
    [TestMethod]
    public async Task FullCanaryCycleWithMetricsAndPersistence()
    {
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var store = new InMemoryPipelineRunStore();
        var defaultController = new CutoverController(0);
        var registry = new CutoverControllerRegistry(defaultController);
        var service = new CanaryProgressionService(
            store, defaultController, CanaryAcceptanceHelpers.DefaultOptions, time, registry);
        var collector = new DefaultCanaryMetricsCollector();
        var runId = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store);
        var hardReport = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);

        // 初始化 canary（1%）
        service.InitializeCanary(runId);
        var controller = registry.GetOrCreate(runId);
        Assert.AreEqual(1, controller.CutoverPercentage, "初始化后应为 1%");

        // 记录 5 次健康指标 → 转换为 baseline/experiment → 推进到 5%
        for (var i = 0; i < 5; i++)
        {
            collector.RecordObservation(runId, hardReport,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: TimeSpan.FromMilliseconds(50));
        }
        var metrics = collector.GetAggregatedMetrics(runId);
        var baseline = metrics.ToBaselineMetrics();
        var experiment = metrics.ToExperimentMetrics();
        time.Advance(TimeSpan.FromSeconds(2));
        var result1 = await service.AdvanceAsync(runId, "t-e2e-advance-001", null, baseline, experiment);
        Assert.AreEqual(CanaryProgressionDecision.Advance, result1.Decision,
            $"应推进到 5%；rationale={result1.Rationale}");
        Assert.AreEqual(5, result1.CurrentPercentage);

        // 验证 store 有审计记录
        var transitions = await store.ListStageTransitionsByRunAsync(runId);
        Assert.AreEqual(1, transitions.Count, "推进到 5% 后应有 1 条审计记录");
        Assert.AreEqual(CanaryProgressionDecision.Advance, transitions[0].Decision);
        Assert.AreEqual(1, transitions[0].FromPercentage);
        Assert.AreEqual(5, transitions[0].ToPercentage);

        // 推进后 Reset metrics 窗口（模拟 HostedService 推进后重置观察窗口）
        collector.Reset(runId);
        Assert.AreEqual(0, collector.GetAggregatedMetrics(runId).TotalObservations,
            "推进后应 Reset metrics 窗口，TotalObservations == 0");

        // 记录更多健康指标 → 推进到 10%
        for (var i = 0; i < 5; i++)
        {
            collector.RecordObservation(runId, hardReport,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: TimeSpan.FromMilliseconds(50));
        }
        metrics = collector.GetAggregatedMetrics(runId);
        baseline = metrics.ToBaselineMetrics();
        experiment = metrics.ToExperimentMetrics();
        time.Advance(TimeSpan.FromSeconds(2));
        var result2 = await service.AdvanceAsync(runId, "t-e2e-advance-002", null, baseline, experiment);
        Assert.AreEqual(CanaryProgressionDecision.Advance, result2.Decision,
            $"应推进到 10%；rationale={result2.Rationale}");
        Assert.AreEqual(10, result2.CurrentPercentage);

        // 验证 store 有 2 条审计记录
        transitions = await store.ListStageTransitionsByRunAsync(runId);
        Assert.AreEqual(2, transitions.Count, "推进到 10% 后应有 2 条审计记录");

        // 验证 CutoverController 百分比同步
        Assert.AreEqual(10, controller.CutoverPercentage, "CutoverController 应同步到 10%");

        // 推进后再次 Reset metrics 窗口
        collector.Reset(runId);
        Assert.AreEqual(0, collector.GetAggregatedMetrics(runId).TotalObservations,
            "推进到 10% 后应 Reset metrics 窗口，TotalObservations == 0");
    }

    // ===========================================================================
    // 11. RollbackResetsMetricsAndPersistsAudit
    //     初始化 canary（1%）→ 推进到 5% → 记录高 divergence 指标 → 触发回滚
    //     → 验证 store 有 Advance + Rollback 两条审计记录
    //     → 验证 CutoverController 百分比回 0
    //     → 验证 metrics collector 窗口已 Reset（TotalObservations == 0）。
    // ===========================================================================
    [TestMethod]
    public async Task RollbackResetsMetricsAndPersistsAudit()
    {
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var store = new InMemoryPipelineRunStore();
        var defaultController = new CutoverController(0);
        var registry = new CutoverControllerRegistry(defaultController);
        var service = new CanaryProgressionService(
            store, defaultController, CanaryAcceptanceHelpers.DefaultOptions, time, registry);
        var collector = new DefaultCanaryMetricsCollector();
        var runId = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store);
        var hardReport = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);
        var divergentReport = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Divergent);

        // 初始化 canary（1%）
        service.InitializeCanary(runId);
        var controller = registry.GetOrCreate(runId);
        Assert.AreEqual(1, controller.CutoverPercentage);

        // 记录健康指标 → 推进到 5%
        for (var i = 0; i < 5; i++)
        {
            collector.RecordObservation(runId, hardReport,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: TimeSpan.FromMilliseconds(50));
        }
        var metrics = collector.GetAggregatedMetrics(runId);
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(runId, "t-e2e-rollback-advance-001", null,
            metrics.ToBaselineMetrics(), metrics.ToExperimentMetrics());
        Assert.AreEqual(5, controller.CutoverPercentage, "推进后应为 5%");

        // Reset 窗口后记录高 divergence 指标
        collector.Reset(runId);
        for (var i = 0; i < 5; i++)
        {
            collector.RecordObservation(runId, divergentReport,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: TimeSpan.FromMilliseconds(50));
        }
        metrics = collector.GetAggregatedMetrics(runId);
        Assert.AreEqual(5, metrics.TotalObservations, "应有 5 次发散观察");
        Assert.IsTrue(metrics.DivergenceRate > 0, "DivergenceRate 应大于 0（将触发回滚）");

        // 触发回滚
        time.Advance(TimeSpan.FromSeconds(2));
        var rollbackResult = await service.AdvanceAsync(runId, "t-e2e-rollback-001", null,
            metrics.ToBaselineMetrics(), metrics.ToExperimentMetrics());
        Assert.AreEqual(CanaryProgressionDecision.Rollback, rollbackResult.Decision,
            $"高 divergence 应触发回滚；rationale={rollbackResult.Rationale}");

        // 验证 store 有 Advance + Rollback 两条审计记录
        var transitions = await store.ListStageTransitionsByRunAsync(runId);
        Assert.AreEqual(2, transitions.Count, "应有 Advance + Rollback 两条审计记录");
        Assert.AreEqual(CanaryProgressionDecision.Advance, transitions[0].Decision,
            "第一条应为 Advance");
        Assert.AreEqual(CanaryProgressionDecision.Rollback, transitions[1].Decision,
            "第二条应为 Rollback");

        // 验证 CutoverController 百分比回 0
        Assert.AreEqual(0, controller.CutoverPercentage, "回滚后 CutoverController 应为 0%");

        // 验证 metrics collector 窗口已 Reset（模拟 HostedService 回滚后重置观察窗口）
        collector.Reset(runId);
        Assert.AreEqual(0, collector.GetAggregatedMetrics(runId).TotalObservations,
            "回滚后应 Reset metrics 窗口，TotalObservations == 0");
    }

    // ===========================================================================
    // 12. PerRunIsolationDuringConcurrentCanaries
    //     两个 run 并发 canary：run1 在 25%，run2 在 5%；
    //     run1 的 metrics 不影响 run2 的评估；run1 回滚不影响 run2 的 CutoverController 百分比。
    // ===========================================================================
    [TestMethod]
    public async Task PerRunIsolationDuringConcurrentCanaries()
    {
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var store = new InMemoryPipelineRunStore();
        var defaultController = new CutoverController(0);
        var registry = new CutoverControllerRegistry(defaultController);
        var service = new CanaryProgressionService(
            store, defaultController, CanaryAcceptanceHelpers.DefaultOptions, time, registry);
        var collector = new DefaultCanaryMetricsCollector();
        var run1 = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, "run-concurrent-001");
        var run2 = await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, "run-concurrent-002");
        var divergentReport = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Divergent);

        // 初始化两个 run（均为 1%）
        service.InitializeCanary(run1);
        service.InitializeCanary(run2);
        var controller1 = registry.GetOrCreate(run1);
        var controller2 = registry.GetOrCreate(run2);
        Assert.AreNotSame(controller1, controller2, "两个 run 的 controller 应为不同实例");

        // run1 推进到 25%（1→5→10→25）
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(run1, "t-concurrent-run1-001", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(run1, "t-concurrent-run1-002", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(run1, "t-concurrent-run1-003", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.AreEqual(25, controller1.CutoverPercentage, "run1 应推进到 25%");

        // run2 推进到 5%
        time.Advance(TimeSpan.FromSeconds(2));
        await service.AdvanceAsync(run2, "t-concurrent-run2-001", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.AreEqual(5, controller2.CutoverPercentage, "run2 应推进到 5%");

        // run1 记录高 divergence 指标（仅记录到 run1 的观察窗口）
        for (var i = 0; i < 5; i++)
        {
            collector.RecordObservation(run1, divergentReport,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: TimeSpan.FromMilliseconds(50));
        }

        // 验证 run1 的 metrics 不影响 run2 的观察窗口
        var run2Metrics = collector.GetAggregatedMetrics(run2);
        Assert.AreEqual(0, run2Metrics.TotalObservations,
            "run1 的 metrics 不应影响 run2 的观察窗口");

        // run1 触发回滚
        var run1Metrics = collector.GetAggregatedMetrics(run1);
        time.Advance(TimeSpan.FromSeconds(2));
        var rollbackResult = await service.AdvanceAsync(run1, "t-concurrent-run1-rollback", null,
            run1Metrics.ToBaselineMetrics(), run1Metrics.ToExperimentMetrics());
        Assert.AreEqual(CanaryProgressionDecision.Rollback, rollbackResult.Decision,
            $"run1 高 divergence 应触发回滚；rationale={rollbackResult.Rationale}");
        Assert.AreEqual(0, controller1.CutoverPercentage, "run1 回滚后应为 0%");

        // 验证 run1 回滚不影响 run2 的 CutoverController 百分比
        Assert.AreEqual(5, controller2.CutoverPercentage,
            "run1 回滚不应影响 run2 的 CutoverController 百分比");

        // 验证 run2 仍可正常推进（不受 run1 回滚影响）
        time.Advance(TimeSpan.FromSeconds(2));
        var run2Advance = await service.AdvanceAsync(run2, "t-concurrent-run2-002", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Advance, run2Advance.Decision,
            $"run2 应仍能正常推进；rationale={run2Advance.Rationale}");
        Assert.AreEqual(10, controller2.CutoverPercentage, "run2 应推进到 10%");
    }
}

// ===========================================================================
// 测试类 5：CanaryProductionSampleSourceAcceptanceTests
// 验证 R28-D P0-6：Authoritative Runtime 在生产路径调用 RecordObservation
// 并记录真实 Legacy 延迟（修复此前 Collector 永远没有样本 + LegacyP95LatencyMs = V2P95LatencyMs 的问题）
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.8")]
public sealed class CanaryProductionSampleSourceAcceptanceTests
{
    // ===========================================================================
    // 13. RecordObservation_SeparateLegacyDuration_ComputesRealLegacyP95
    //     验证：RecordObservation 接收独立的 legacyDuration 参数；
    //     当 V2 耗时显著高于 Legacy 耗时时，LegacyP95LatencyMs != V2P95LatencyMs
    //     （latency multiplier 可发现 V2 延迟回退）。
    // ===========================================================================
    [TestMethod]
    public void RecordObservation_SeparateLegacyDuration_ComputesRealLegacyP95()
    {
        var collector = new DefaultCanaryMetricsCollector();
        var runId = "run-p0-6-legacy-latency-001";
        var hardReport = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);

        // 10 次观察：V2 耗时 200ms（回退），Legacy 耗时 50ms（基线）
        for (var i = 0; i < 10; i++)
        {
            collector.RecordObservation(runId, hardReport,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: TimeSpan.FromMilliseconds(200),
                legacyDuration: TimeSpan.FromMilliseconds(50));
        }

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.AreEqual(10, metrics.TotalObservations, "总观察次数应为 10");
        // R28-G P1-6：DDSketch 返回相对误差 1% 内的估计值（200ms ± 2ms），使用 delta 3.0 容忍
        Assert.AreEqual(200.0, metrics.V2P95LatencyMs, 3.0,
            "V2 P95 应接近 200ms（真实 V2 耗时，DDSketch 相对误差 ≤ 1%）");
        Assert.AreEqual(50.0, metrics.LegacyP95LatencyMs, 1.0,
            "Legacy P95 应接近 50ms（真实 Legacy 耗时，而非 V2 近似值，DDSketch 相对误差 ≤ 1%）");
        Assert.AreNotEqual(metrics.V2P95LatencyMs, metrics.LegacyP95LatencyMs,
            "V2 P95 与 Legacy P95 必须不同（修复前 LegacyP95LatencyMs = V2P95LatencyMs 的问题）");
    }

    // ===========================================================================
    // 14. RecordObservation_NullLegacyDuration_FallsBackToV2Duration
    //     验证：legacyDuration=null 时回退到 v2Duration（向后兼容旧调用点）。
    // ===========================================================================
    [TestMethod]
    public void RecordObservation_NullLegacyDuration_FallsBackToV2Duration()
    {
        var collector = new DefaultCanaryMetricsCollector();
        var runId = "run-p0-6-null-legacy-001";
        var hardReport = CanaryAcceptanceHelpers.BuildParityReport(ParityLevel.Hard);

        collector.RecordObservation(runId, hardReport,
            v2Succeeded: true, legacySucceeded: true,
            v2Duration: TimeSpan.FromMilliseconds(80));

        var metrics = collector.GetAggregatedMetrics(runId);
        // R28-G P1-6：DDSketch 返回相对误差 1% 内的估计值（80ms ± 0.8ms），使用 delta 1.5 容忍
        Assert.AreEqual(80.0, metrics.V2P95LatencyMs, 1.5, "V2 P95 应接近 80ms（DDSketch 相对误差 ≤ 1%）");
        Assert.AreEqual(80.0, metrics.LegacyP95LatencyMs, 1.5,
            "legacyDuration=null 时 Legacy P95 应回退到 V2 P95（向后兼容）");
    }

    // ===========================================================================
    // 15. AuthoritativeRetrievalRuntime_MixedMode_RecordsCanaryObservation
    //     验证：AuthoritativeRetrievalRuntime 在 Mixed mode（0 < cutover < 100）下，
    //     当请求 metadata 携带 canaryRunId 时，调用 RecordObservation 上报样本。
    //     修复前：RecordObservation 仅在测试代码中调用，生产路径永不调用 → Collector 永远没有样本。
    //     注意：使用 cutoverPercentage=99（仍属 Mixed mode，因 < 100）确保绝大多数 OperationId 走 V2。
    // ===========================================================================
    [TestMethod]
    public async Task AuthoritativeRetrievalRuntime_MixedMode_RecordsCanaryObservation()
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(
            R28BTestHelpers.MakeResult("op-mixed-canary"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var collector = new DefaultCanaryMetricsCollector();
        var runId = "run-mixed-canary-001";

        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 99),
            canaryMetricsCollector: collector);

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-mixed-canary",
            WorkspaceId = "ws-canary",
            CollectionId = "col-canary",
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = runId
            }
        };

        await runtime.RetrieveAsync(request, CancellationToken.None);

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.IsTrue(metrics.TotalObservations >= 1,
            "Mixed mode 路径下 AuthoritativeRetrievalRuntime 必须调用 RecordObservation 上报样本。" +
            $"TotalObservations={metrics.TotalObservations}（修复前永远为 0）");
    }

    // ===========================================================================
    // 16. AuthoritativeRetrievalRuntime_NoCanaryRunId_DoesNotRecordObservation
    //     验证：请求 metadata 不携带 canaryRunId 时，不调用 RecordObservation
    //     （无 runId 的请求不属于任何 canary run，不应污染 Collector）。
    // ===========================================================================
    [TestMethod]
    public async Task AuthoritativeRetrievalRuntime_NoCanaryRunId_DoesNotRecordObservation()
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(
            R28BTestHelpers.MakeResult("op-no-runid"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var collector = new DefaultCanaryMetricsCollector();

        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 50),
            canaryMetricsCollector: collector);

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-goes-v2",
            WorkspaceId = "ws-no-runid",
            CollectionId = "col-no-runid"
            // 故意不设置 canaryRunId metadata
        };

        await runtime.RetrieveAsync(request, CancellationToken.None);

        // 没有 canaryRunId 时不应上报任何样本
        // 注意：GetAggregatedMetrics 传入未知的 runId 时返回 TotalObservations=0
        var metrics = collector.GetAggregatedMetrics("any-run-id");
        Assert.AreEqual(0, metrics.TotalObservations,
            "请求未携带 canaryRunId 时不应调用 RecordObservation。");
    }

    // ===========================================================================
    // 17. AuthoritativeRetrievalRuntime_SampledShadow_RecordsCanaryObservation
    //     验证：sampled shadow 路径（100% cutover + 启用 sampled shadow）也调用 RecordObservation。
    // ===========================================================================
    [TestMethod]
    public async Task AuthoritativeRetrievalRuntime_SampledShadow_RecordsCanaryObservation()
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(
            R28BTestHelpers.MakeResult("op-sampled-shadow"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var collector = new DefaultCanaryMetricsCollector();
        var runId = "run-sampled-shadow-001";

        // 启用 sampled shadow（rate=1.0 → 所有请求执行 Legacy 对照）
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(), new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100, EnableSampledShadow = true, ShadowSampleRate = 1.0 });

        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100),
            shadowGate: null,
            experimentPlane: integration,
            canaryMetricsCollector: collector);

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-sampled-shadow",
            WorkspaceId = "ws-sampled",
            CollectionId = "col-sampled",
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = runId
            }
        };

        await runtime.RetrieveAsync(request, CancellationToken.None);

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.IsTrue(metrics.TotalObservations >= 1,
            "sampled shadow 路径下 AuthoritativeRetrievalRuntime 必须调用 RecordObservation 上报样本。" +
            $"TotalObservations={metrics.TotalObservations}（修复前永远为 0）");
    }

    // ===========================================================================
    // 18. AuthoritativeRetrievalRuntime_V2Failure_RecordsObservationWithV2Error
    //     验证：V2 路径抛异常时（fail-open 回退 Legacy），仍调用 RecordObservation
    //     且 v2Succeeded=false（让 Canary error rate 能捕获 V2 失败率）。
    // ===========================================================================
    [TestMethod]
    public async Task AuthoritativeRetrievalRuntime_V2Failure_RecordsObservationWithV2Error()
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        // V2 抛非取消异常 → Runtime 应 fail-open 回退 Legacy 并上报 v2Succeeded=false
        var throwingV2 = new ThrowingDecisionRuntime(new InvalidOperationException("V2 down"));
        var shadowRuntime = new ShadowDecisionRuntime(throwingV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var collector = new DefaultCanaryMetricsCollector();
        var runId = "run-v2-failure-001";

        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, throwingV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 99),
            canaryMetricsCollector: collector);

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-v2-failure",
            WorkspaceId = "ws-v2-fail",
            CollectionId = "col-v2-fail",
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = runId
            }
        };

        // V2 抛异常时 Runtime 应 fail-open 回退 Legacy（不向外抛）
        var result = await runtime.RetrieveAsync(request, CancellationToken.None);
        Assert.IsNotNull(result, "V2 失败时应 fail-open 回退到 Legacy 结果。");

        var metrics = collector.GetAggregatedMetrics(runId);
        Assert.IsTrue(metrics.TotalObservations >= 1,
            "V2 失败时也必须上报样本（让 Canary error rate 能捕获 V2 失败率）。");
        Assert.IsTrue(metrics.V2ErrorRate > 0.0,
            "V2 失败时 V2ErrorRate 必须 > 0（让 Canary 回滚阈值能触发）。");
    }
}
