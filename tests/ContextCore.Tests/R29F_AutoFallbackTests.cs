using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextCore.Tests;

// ===========================================================================
// 性能监控 + 自动回退阈值验收测试
//
// 覆盖：
//   DefaultPerformanceMonitor 基础行为（RecordExecutionTime / ShouldFallbackToV20）
//   冷启动保护（MinSamplesBeforeFallback 个样本前不触发回退）
//   自愈机制（连续 RecoverySamples 个低于阈值样本后解除回退）
//   scope 隔离（不同 scope 独立计数）
//   估算（> 阈值的样本触发回退，单次抖动 + 已累积低样本不误触发）
//   Engine 集成：未注入 IPerformanceMonitor 时保持旧行为（向后兼容）
//   Engine 集成：注入 monitor 后，超阈值触发回退 → 下次请求跳过 V2.1 走 V2.0
//   Engine 集成：低于阈值解除后恢复 V2.1 路径
//   Engine 集成：Result.Outcome.Diagnostics 包含 performance.* 字段
//   Engine 集成：6-param 构造函数向后兼容（不注入 monitor）
//
// 设计原则：
//   - 使用 SpyAllocatorV2_1 验证 V2.1 vs V2.0 路径选择
//   - 使用 DefaultPerformanceMonitor 直接构造 + 注入 Engine 端到端验证
//   - 所有代码注释使用中文
// ===========================================================================

/// <summary>
/// 性能监控 + 自动回退阈值验收测试。
/// </summary>
[TestClass]
[TestCategory("R29")]
[TestCategory("DecisionEngine")]
[TestCategory("Performance")]
public sealed class R29F_AutoFallbackTests
{
    // =======================================================================
    // -§5: DefaultPerformanceMonitor 单元行为
    // =======================================================================

    [TestMethod]
    public void Monitor_ShouldNotFallback_WhenNoSamplesRecorded()
    {
        var monitor = new DefaultPerformanceMonitor();
        Assert.IsFalse(monitor.ShouldFallbackToV20("scope-a"),
            "未记录样本时不应触发回退。");
    }

    [TestMethod]
    public void Monitor_ShouldNotFallback_BeforeMinSamplesThreshold()
    {
        // 默认 MinSamplesBeforeFallback = 3，单次超阈值不应触发
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 100,
            MinSamplesBeforeFallback = 3,
            SampleWindow = 16
        });

        monitor.RecordExecutionTime("scope-a", durationMs: 200, usedV21Path: true);

        Assert.IsFalse(monitor.ShouldFallbackToV20("scope-a"),
            "样本数不足 MinSamplesBeforeFallback 时不应触发回退（冷启动保护）。");
    }

    [TestMethod]
    public void Monitor_ShouldFallback_AfterMinSamplesAllExceedThreshold()
    {
        // 累积 MinSamplesBeforeFallback 个超阈值样本 → 触发回退
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 100,
            MinSamplesBeforeFallback = 3,
            SampleWindow = 16,
            RecoverySamples = 5
        });

        monitor.RecordExecutionTime("scope-a", 250, true);
        monitor.RecordExecutionTime("scope-a", 280, true);
        monitor.RecordExecutionTime("scope-a", 320, true);

        Assert.IsTrue(monitor.ShouldFallbackToV20("scope-a"),
            "累积 MinSamplesBeforeFallback 个超阈值样本后应触发回退。");
    }

    [TestMethod]
    public void Monitor_ScopesAreIsolated()
    {
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 100,
            MinSamplesBeforeFallback = 2,
            SampleWindow = 16
        });

        // scope-a 触发回退
        monitor.RecordExecutionTime("scope-a", 250, true);
        monitor.RecordExecutionTime("scope-a", 280, true);

        // scope-b 始终低于阈值
        monitor.RecordExecutionTime("scope-b", 30, true);
        monitor.RecordExecutionTime("scope-b", 40, true);

        Assert.IsTrue(monitor.ShouldFallbackToV20("scope-a"), "scope-a 应触发回退。");
        Assert.IsFalse(monitor.ShouldFallbackToV20("scope-b"), "scope-b 不应触发回退（scope 隔离）。");
    }

    [TestMethod]
    public void Monitor_SelfHeals_AfterRecoverySamplesBelowThreshold()
    {
        // 触发回退后，连续 RecoverySamples 个低于阈值样本后解除
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 100,
            MinSamplesBeforeFallback = 2,
            SampleWindow = 16,
            RecoverySamples = 3
        });

        // 触发回退
        monitor.RecordExecutionTime("scope-a", 250, true);
        monitor.RecordExecutionTime("scope-a", 280, true);
        Assert.IsTrue(monitor.ShouldFallbackToV20("scope-a"));

        // 连续 3 个低于阈值样本 → 自愈
        monitor.RecordExecutionTime("scope-a", 50, true);
        Assert.IsTrue(monitor.ShouldFallbackToV20("scope-a"), "1 个低于阈值样本不足以解除回退。");
        monitor.RecordExecutionTime("scope-a", 60, true);
        Assert.IsTrue(monitor.ShouldFallbackToV20("scope-a"), "2 个低于阈值样本不足以解除回退。");
        monitor.RecordExecutionTime("scope-a", 40, true);
        Assert.IsFalse(monitor.ShouldFallbackToV20("scope-a"), "3 个连续低于阈值样本后应解除回退。");
    }

    [TestMethod]
    public void Monitor_ResetConsecutiveLowSamples_WhenAboveThreshold()
    {
        // 自愈过程中插入超阈值样本 → 重置连续低样本计数
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 100,
            MinSamplesBeforeFallback = 2,
            SampleWindow = 16,
            RecoverySamples = 3
        });

        // 触发回退
        monitor.RecordExecutionTime("scope-a", 250, true);
        monitor.RecordExecutionTime("scope-a", 280, true);
        Assert.IsTrue(monitor.ShouldFallbackToV20("scope-a"));

        // 2 个低于阈值 → 还差 1 个恢复
        monitor.RecordExecutionTime("scope-a", 50, true);
        monitor.RecordExecutionTime("scope-a", 60, true);

        // 插入 1 个超阈值 → 重置 ConsecutiveLowSamples
        monitor.RecordExecutionTime("scope-a", 200, true);
        Assert.IsTrue(monitor.ShouldFallbackToV20("scope-a"), "插入超阈值后不应自愈（重置计数）。");

        // 再次累积 3 个低样本才能解除
        monitor.RecordExecutionTime("scope-a", 50, true);
        monitor.RecordExecutionTime("scope-a", 60, true);
        Assert.IsTrue(monitor.ShouldFallbackToV20("scope-a"), "重新累积 2 个低样本不足以解除。");
        monitor.RecordExecutionTime("scope-a", 40, true);
        Assert.IsFalse(monitor.ShouldFallbackToV20("scope-a"), "重新累积 3 个低样本后应解除。");
    }

    [TestMethod]
    public void Monitor_GetDiagnostics_ReturnsExpectedFields()
    {
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 100,
            MinSamplesBeforeFallback = 2,
            SampleWindow = 16,
            RecoverySamples = 3
        });

        monitor.RecordExecutionTime("scope-a", 200, true);
        monitor.RecordExecutionTime("scope-a", 250, true);
        monitor.RecordFallback("scope-a", "test_reason", lastDurationMs: 250);

        var diag = monitor.GetDiagnostics("scope-a");

        Assert.IsTrue(diag.ContainsKey("performance.fallback_triggered"));
        Assert.AreEqual("true", diag["performance.fallback_triggered"]);
        Assert.IsTrue(diag.ContainsKey("performance.threshold_ms"));
        Assert.AreEqual("100", diag["performance.threshold_ms"]);
        Assert.IsTrue(diag.ContainsKey("performance.sample_count"));
        Assert.AreEqual("2", diag["performance.sample_count"]);
        Assert.IsTrue(diag.ContainsKey("performance.fallback_recorded_count"));
        Assert.AreEqual("1", diag["performance.fallback_recorded_count"]);
        Assert.IsTrue(diag.ContainsKey("performance.last_fallback_reason"));
        Assert.AreEqual("test_reason", diag["performance.last_fallback_reason"]);
    }

    [TestMethod]
    public void Monitor_RejectsInvalidOptions()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new DefaultPerformanceMonitor(new PerformanceFallbackOptions { ThresholdMs = 0 }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new DefaultPerformanceMonitor(new PerformanceFallbackOptions { SampleWindow = 0 }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new DefaultPerformanceMonitor(new PerformanceFallbackOptions { MinSamplesBeforeFallback = 0 }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new DefaultPerformanceMonitor(new PerformanceFallbackOptions { RecoverySamples = 0 }));
    }

    [TestMethod]
    public void Monitor_NullOrEmptyScopeKey_Throws()
    {
        var monitor = new DefaultPerformanceMonitor();
        Assert.ThrowsException<ArgumentException>(() => monitor.RecordExecutionTime("", 100, true));
        Assert.ThrowsException<ArgumentException>(() => monitor.ShouldFallbackToV20(""));
        Assert.ThrowsException<ArgumentException>(() => monitor.RecordFallback("", "reason", 100));
        Assert.ThrowsException<ArgumentException>(() => monitor.GetDiagnostics(""));
    }

    // =======================================================================
    // 辅助：SpyAllocatorV2_1（复用 R29D 测试模式）
    // =======================================================================

    private sealed class SpyAllocatorV2_1 : IAllocatorV2_1
    {
        private readonly DefaultAllocatorV2_1 _inner;
        internal bool AllocateWithDiversityCalled { get; private set; }
        internal int V21CallCount { get; private set; }

        internal SpyAllocatorV2_1(IGlobalAllocator baseAllocator)
        {
            _inner = new DefaultAllocatorV2_1(baseAllocator);
        }

        public AllocationResult Allocate(
            IReadOnlyList<ContextCandidateEnvelope> envelopes,
            EffectivePolicySnapshot snapshot)
            => _inner.Allocate(envelopes, snapshot);

        public AllocationResult Allocate(
            IReadOnlyList<ContextCandidateEnvelope> envelopes,
            EffectivePolicySnapshot snapshot,
            AllocationContext context)
            => _inner.Allocate(envelopes, snapshot, context);

        public AllocationResult AllocateWithDiversity(
            IReadOnlyList<ContextCandidateEnvelope> candidates,
            AllocationContext context,
            DiversityOptions diversityOptions)
        {
            AllocateWithDiversityCalled = true;
            V21CallCount++;
            return _inner.AllocateWithDiversity(candidates, context, diversityOptions);
        }
    }

    // =======================================================================
    // 辅助方法
    // =======================================================================

    private static ContextCandidateEnvelope MakeEnvelope(string id, double score = 0.8, int tokens = 100) =>
        R28BTestHelpers.MakeEnvelope(id, ContextCandidateSource.Semantic, score, tokens);

    private static AllocationContext MakeContext(int tokenBudget = 1000) => new()
    {
        Purpose = ContextDecisionPurpose.Package,
        Budget = new BudgetProfile
        {
            ProfileId = "perf-test-budget",
            DefaultTokenBudget = tokenBudget,
            DefaultTopK = 50
        },
        MandatoryOverflowPolicy = MandatoryOverflowPolicy.AllowOverflowWithDiagnostic
    };

    private static EffectivePolicySnapshot MakeSnapshot(DiversityOptions? diversityOptions = null)
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
            ResolutionScope = new ContextDecisionScope("perf-ws", "perf-col"),
            DiversityOptions = diversityOptions
        };
    }

    private static ContextDecisionRequest MakeRequest(
        string scopeKey = "perf-ws/perf-col",
        string workspaceId = "perf-ws",
        string collectionId = "perf-col",
        DiversityOptions? diversityOptions = null)
    {
        var candidate = MakeEnvelope("c-perf");
        return new ContextDecisionRequest
        {
            RequestId = $"req-{Guid.NewGuid():N}",
            DecisionSource = ContextDecisionSource.Package,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Candidates = new[] { candidate },
            TokenBudget = 1000,
            PolicySnapshot = MakeSnapshot(diversityOptions ?? new DiversityOptions()),
            AllocationContext = MakeContext(1000),
            DiversityOptions = diversityOptions ?? new DiversityOptions()
        };
    }

    private static DefaultContextDecisionEngine BuildEngine(
        SpyAllocatorV2_1? spy = null,
        IPerformanceMonitor? monitor = null)
    {
        var baseAllocator = new DefaultGlobalAllocator();
        return new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            globalAllocator: baseAllocator,
            allocatorV2_1: spy ?? new SpyAllocatorV2_1(baseAllocator),
            performanceMonitor: monitor);
    }

    // =======================================================================
    // -§10: Engine 集成测试
    // =======================================================================

    [TestMethod]
    public async Task Engine_NoMonitorInjected_KeepsLegacyBehavior()
    {
        // 6-param 构造函数向后兼容：不注入 monitor 时仍走 V2.1 路径
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            globalAllocator: new DefaultGlobalAllocator(),
            allocatorV2_1: spy);

        var request = MakeRequest();
        var result = await engine.DecideAsync(request);

        Assert.IsTrue(spy.AllocateWithDiversityCalled,
            "未注入 monitor 时应保持旧行为：走 V2.1 路径。");
        Assert.AreEqual(1, result.SelectedEnvelopes.Count);
        // 不应包含 performance.* 诊断
        Assert.IsFalse(result.Outcome.Diagnostics.ContainsKey("performance.v21_path_used"),
            "未注入 monitor 时不应写 performance.* 诊断。");
    }

    [TestMethod]
    public async Task Engine_WithMonitor_BelowThreshold_UsesV21Path()
    {
        // 阈值 500ms，每次执行 << 阈值 → 始终走 V2.1 路径
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 500,
            MinSamplesBeforeFallback = 3,
            SampleWindow = 16,
            RecoverySamples = 5
        });
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = BuildEngine(spy, monitor);

        // 跑 10 次，每次都远低于阈值
        for (int i = 0; i < 10; i++)
        {
            var request = MakeRequest();
            await engine.DecideAsync(request);
        }

        Assert.AreEqual(10, spy.V21CallCount,
            "全部低于阈值 → 全部走 V2.1 AllocateWithDiversity。");
        Assert.IsFalse(monitor.ShouldFallbackToV20("perf-ws/perf-col"));
    }

    [TestMethod]
    public async Task Engine_WithMonitor_AboveThreshold_FallsBackToV20()
    {
        // 使用低阈值（1ms）+ MinSamplesBeforeFallback=1 触发回退。
        // 注意：本地测试执行时间通常 < 1ms（仅 1 个候选），所以测试通过手动注入
        // 超阈值样本来模拟慢执行，然后验证 Engine 在下次请求时检测到回退状态。
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 1,
            MinSamplesBeforeFallback = 1,
            SampleWindow = 16,
            RecoverySamples = 100 // 防止自愈
        });
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = BuildEngine(spy, monitor);

        // 第 1 次：尚未注入样本，monitor.ShouldFallbackToV20 返回 false → 走 V2.1
        var result1 = await engine.DecideAsync(MakeRequest());
        Assert.AreEqual(1, spy.V21CallCount, "第 1 次请求应走 V2.1（尚未触发回退）。");
        Assert.AreEqual("true", result1.Outcome.Diagnostics["performance.v21_path_used"]);

        // 模拟一次慢执行：手动注入一个超阈值样本（模拟 V2.1 路径超时）
        monitor.RecordExecutionTime("perf-ws/perf-col", durationMs: 500, usedV21Path: true);
        Assert.IsTrue(monitor.ShouldFallbackToV20("perf-ws/perf-col"),
            "注入超阈值样本后应触发回退状态。");

        // 第 2 次：monitor.ShouldFallbackToV20 返回 true → 强制 V2.0 路径
        var result2 = await engine.DecideAsync(MakeRequest());
        Assert.AreEqual(1, spy.V21CallCount,
            "第 2 次请求应跳过 V2.1（性能回退触发 forceV20Fallback=true）。");
        Assert.AreEqual(1, result2.SelectedEnvelopes.Count, "V2.0 路径仍应正确选入候选。");
        Assert.AreEqual("false", result2.Outcome.Diagnostics["performance.v21_path_used"]);
        Assert.AreEqual("true", result2.Outcome.Diagnostics["performance.fallback_applied"]);
    }

    [TestMethod]
    public async Task Engine_WithMonitor_FallbackRecoversAfterLowSamples()
    {
        // 触发回退后，连续 RecoverySamples 个低于阈值样本 → 自愈恢复 V2.1。
        // 测试通过手动注入超阈值样本触发回退，然后让 Engine 跑两次低样本（执行时间低）
        // 累积到 RecoverySamples=2 后自愈，第 3 次恢复 V2.1。
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 50,
            MinSamplesBeforeFallback = 1,
            SampleWindow = 16,
            RecoverySamples = 2
        });
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = BuildEngine(spy, monitor);

        // 手动注入超阈值样本触发回退
        monitor.RecordExecutionTime("perf-ws/perf-col", 500, true);
        Assert.IsTrue(monitor.ShouldFallbackToV20("perf-ws/perf-col"),
            "人工注入超阈值样本后应触发回退。");

        // 第 1 次：触发回退 → 跑 V2.0 路径，本次执行远低于 50ms
        await engine.DecideAsync(MakeRequest());
        Assert.AreEqual(0, spy.V21CallCount, "回退触发时本次应走 V2.0。");
        Assert.IsTrue(monitor.ShouldFallbackToV20("perf-ws/perf-col"),
            "1 个低样本不足以自愈，仍处于回退状态。");

        // 第 2 次：再走 V2.0 一次，累积到 RecoverySamples=2 → 自愈
        await engine.DecideAsync(MakeRequest());
        Assert.AreEqual(0, spy.V21CallCount, "2 个低样本前仍走 V2.0。");
        Assert.IsFalse(monitor.ShouldFallbackToV20("perf-ws/perf-col"),
            "累积 RecoverySamples=2 个低样本后应解除回退。");

        // 第 3 次：恢复 V2.1 路径
        await engine.DecideAsync(MakeRequest());
        Assert.AreEqual(1, spy.V21CallCount, "自愈后应恢复 V2.1 路径。");
    }

    [TestMethod]
    public async Task Engine_WithMonitor_WritesPerformanceDiagnosticsToResult()
    {
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 1,
            MinSamplesBeforeFallback = 1,
            SampleWindow = 16,
            RecoverySamples = 100
        });
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = BuildEngine(spy, monitor);

        // 第 1 次执行：尚未注入样本，走 V2.1 + 写诊断
        var result1 = await engine.DecideAsync(MakeRequest());
        Assert.IsTrue(result1.Outcome.Diagnostics.ContainsKey("performance.v21_path_used"),
            "注入 monitor 后应写 performance.v21_path_used 诊断。");
        Assert.AreEqual("true", result1.Outcome.Diagnostics["performance.v21_path_used"],
            "第 1 次应走 V2.1 路径。");
        Assert.AreEqual("false", result1.Outcome.Diagnostics["performance.fallback_applied"],
            "第 1 次未触发回退。");
        Assert.IsTrue(result1.Outcome.Diagnostics.ContainsKey("performance.threshold_ms"));
        Assert.IsTrue(result1.Outcome.Diagnostics.ContainsKey("performance.sample_count"));

        // 手动注入超阈值样本，模拟慢执行
        monitor.RecordExecutionTime("perf-ws/perf-col", 500, true);

        // 第 2 次执行：触发回退 → 走 V2.0
        var result2 = await engine.DecideAsync(MakeRequest());
        Assert.AreEqual("false", result2.Outcome.Diagnostics["performance.v21_path_used"],
            "第 2 次应走 V2.0（fallback_applied=true）。");
        Assert.AreEqual("true", result2.Outcome.Diagnostics["performance.fallback_applied"],
            "第 2 次应触发 fallback_applied=true。");
    }

    [TestMethod]
    public async Task Engine_Fallback_PreservesAllocatorDiagnostics()
    {
        // 回退时 Outcome.Diagnostics 应保留 Allocator 原有诊断（不覆盖）
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 1,
            MinSamplesBeforeFallback = 1,
            SampleWindow = 16,
            RecoverySamples = 100
        });
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = BuildEngine(spy, monitor);

        // 手动注入超阈值样本，触发回退状态
        monitor.RecordExecutionTime("perf-ws/perf-col", 500, true);
        Assert.IsTrue(monitor.ShouldFallbackToV20("perf-ws/perf-col"));

        // 执行：走 V2.0 fallback + 注入 mandatory overflow 诊断
        var mandatory = R28BTestHelpers.MakeEnvelope(
            "m-perf", ContextCandidateSource.Mandatory, 1.0, 200,
            safety: new CandidateSafetyState { IsMandatory = true, PassesSafetyGate = true });
        var request = new ContextDecisionRequest
        {
            RequestId = "req-mandatory-overflow",
            DecisionSource = ContextDecisionSource.Package,
            WorkspaceId = "perf-ws",
            CollectionId = "perf-col",
            Candidates = new[] { mandatory },
            TokenBudget = 50, // mandatory 200 超出预算 50 → mandatory overflow
            PolicySnapshot = MakeSnapshot(new DiversityOptions()),
            AllocationContext = MakeContext(50),
            DiversityOptions = new DiversityOptions()
        };

        var result = await engine.DecideAsync(request);

        // 必须走 V2.0 路径（fallback）
        Assert.AreEqual(0, spy.V21CallCount, "应触发 V2.0 回退。");
        // 必须包含 mandatory overflow 诊断 + performance.* 诊断
        Assert.IsTrue(result.Outcome.Diagnostics.Count > 2,
            "应同时包含 Allocator 原有诊断 + performance.* 诊断。");
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("performance.fallback_applied"));
    }

    [TestMethod]
    public async Task Engine_DifferentScopesAreIsolated()
    {
        // scope-A 触发回退不应影响 scope-B 的 V2.1 路径。
        // 测试通过手动注入超阈值样本到 scope-A，让 Engine 在 scope-A 上回退；
        // 验证 scope-B 上仍走 V2.1（scope 隔离）。
        var monitor = new DefaultPerformanceMonitor(new PerformanceFallbackOptions
        {
            ThresholdMs = 1,
            MinSamplesBeforeFallback = 1,
            SampleWindow = 16,
            RecoverySamples = 100
        });
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = BuildEngine(spy, monitor);

        // 手动触发 scope-A 回退（注入超阈值样本）
        monitor.RecordExecutionTime("ws-A/col-A", 500, true);
        Assert.IsTrue(monitor.ShouldFallbackToV20("ws-A/col-A"));

        // scope-A 第 1 次：应走 V2.0（fallback）
        var reqA1 = MakeRequest(workspaceId: "ws-A", collectionId: "col-A");
        await engine.DecideAsync(reqA1);
        Assert.AreEqual(0, spy.V21CallCount, "scope-A 第 1 次应走 V2.0。");

        // scope-B 第 1 次：不受 scope-A 影响，应走 V2.1
        var reqB1 = MakeRequest(workspaceId: "ws-B", collectionId: "col-B");
        await engine.DecideAsync(reqB1);
        Assert.AreEqual(1, spy.V21CallCount, "scope-B 第 1 次应走 V2.1（scope 隔离）。");
        Assert.IsFalse(monitor.ShouldFallbackToV20("ws-B/col-B"));
    }
}
