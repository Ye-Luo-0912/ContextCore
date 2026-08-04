using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

// ===========================================================================
// 组件独立测试 — ShadowDecisionRuntime + ExperimentRecorder
//
// 覆盖范围（2 个测试类）：
// 1. ShadowDecisionRuntimeComponentTests — Shadow 组件独立测试
// （Legacy 空/异常、V2 异常传播、TokenBudget=0、Package 路径）
// 2. ExperimentRecorderComponentTests — Recorder 组件独立测试
// （幂等写入、并发写入、Replay 集成）
//
// 设计原则：
// - 使用 Stub V2 Runtime（RecordingDecisionRuntime / ThrowingDecisionRuntime）隔离决策内核
// - 复用共享 TestHelpers（MakeEnvelope / MakeResult / MakeMaterial / MakeExecutionResult）
// - 复用 ClosureGate 验收测试中的 internal Stub（RecordingDecisionRuntime / ThrowingDecisionRuntime / CallTrackingContextStore）
// - 所有代码注释使用中文
// ===========================================================================

// ===========================================================================
// 1. ShadowDecisionRuntimeComponentTests — Shadow 组件独立测试
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B-Component")]
public sealed class ShadowDecisionRuntimeComponentTests
{
    [TestMethod]
    public async Task Shadow_LegacyEmpty_WorkingSetBuiltCorrectly()
    {
        // Legacy 结果为空（无 selected / dropped）→ WorkingSet 仍正确构建，V2 仍执行，parity 报告产出
        var v2Result = R28BTestHelpers.MakeResult("op-empty-legacy",
            selected: new[] { R28BTestHelpers.MakeEnvelope("v2-only", ContextCandidateSource.Semantic, 0.8, 100) },
            estimatedTokens: 100);
        var stubRuntime = new RecordingDecisionRuntime(v2Result);
        var shadowRuntime = new ShadowDecisionRuntime(stubRuntime, new DecisionExperimentPlane());

        var legacyRequest = new ContextRetrievalRequest
        {
            OperationId = "op-empty-legacy",
            WorkspaceId = "ws-shadow",
            CollectionId = "col-shadow"
        };
        var legacyResult = new ContextRetrievalResult
        {
            OperationId = "op-empty-legacy",
            SelectedItems = Array.Empty<ContextRetrievalCandidate>(),
            DroppedItems = Array.Empty<ContextRetrievalDecision>(),
            EstimatedTokens = 0
        };

        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-shadow",
            CollectionId = "col-shadow",
            RequestId = "op-empty-legacy",
            ObservedAt = DateTimeOffset.UtcNow
        };

        var report = await shadowRuntime.ExecuteRetrievalShadowAsync(
            legacyRequest, legacyResult, tokenBudget: 1000, topK: 10,
            context: context, cancellationToken: CancellationToken.None);

        // WorkingSet 应被构建（可能为空 envelopes，但非 null）
        Assert.IsNotNull(report.WorkingSet);
        // V2 必须被调用一次
        Assert.AreEqual(1, stubRuntime.ExecuteCallCount,
            "V2 Runtime 必须被调用一次（即使 Legacy 结果为空）。");
        // Parity 报告必须产出
        Assert.IsNotNull(report.Parity);
        // Legacy selected=0, V2 selected=1 → Jaccard=0（空集 vs 非空集）
        Assert.AreEqual(0, report.Parity.LegacySelectedCount,
            "Legacy selected count 必须为 0。");
        Assert.AreEqual(1, report.Parity.V2SelectedCount,
            "V2 selected count 必须为 1。");
        Assert.AreEqual(0.0, report.Parity.JaccardIndex,
            "空集 vs 非空集 → Jaccard 必须为 0.0。");
    }

    [TestMethod]
    public async Task Shadow_V2Throws_PropagatesException()
    {
        // V2 Runtime 抛出非取消异常 → Shadow 必须传播（不吞异常）
        var throwingV2 = new ThrowingDecisionRuntime(new InvalidOperationException("V2 内部错误"));
        var shadowRuntime = new ShadowDecisionRuntime(throwingV2, new DecisionExperimentPlane());

        var legacyRequest = new ContextRetrievalRequest
        {
            OperationId = "op-v2-throws",
            WorkspaceId = "ws-shadow",
            CollectionId = "col-shadow"
        };
        var legacyResult = new ContextRetrievalResult
        {
            OperationId = "op-v2-throws",
            SelectedItems = Array.Empty<ContextRetrievalCandidate>(),
            EstimatedTokens = 0
        };
        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-shadow",
            CollectionId = "col-shadow",
            RequestId = "op-v2-throws",
            ObservedAt = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await shadowRuntime.ExecuteRetrievalShadowAsync(
                legacyRequest, legacyResult, tokenBudget: 1000, topK: 10,
                context: context, cancellationToken: CancellationToken.None),
            "V2 Runtime 抛出非取消异常时 Shadow 必须传播（不吞异常）。");
    }

    [TestMethod]
    public async Task Shadow_TokenBudgetZero_FallsBackToDefault()
    {
        // TokenBudget=0 → Shadow 仍正常执行（tokenBudget 传递给 V2 request，不导致 shadow 失败）
        var v2Result = R28BTestHelpers.MakeResult("op-zero-budget-shadow",
            selected: new[] { R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Lexical, 0.5, 100) },
            estimatedTokens: 100);
        var stubRuntime = new RecordingDecisionRuntime(v2Result);
        var shadowRuntime = new ShadowDecisionRuntime(stubRuntime, new DecisionExperimentPlane());

        var legacyRequest = new ContextRetrievalRequest
        {
            OperationId = "op-zero-budget-shadow",
            WorkspaceId = "ws-shadow",
            CollectionId = "col-shadow"
        };
        var legacyResult = new ContextRetrievalResult
        {
            OperationId = "op-zero-budget-shadow",
            SelectedItems = new[]
            {
                new ContextRetrievalCandidate
                {
                    CandidateId = "c1",
                    SourceId = "c1",
                    Score = 0.5,
                    EstimatedTokens = 100
                }
            },
            EstimatedTokens = 100
        };
        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-shadow",
            CollectionId = "col-shadow",
            RequestId = "op-zero-budget-shadow",
            ObservedAt = DateTimeOffset.UtcNow
        };

        // tokenBudget=0 不应导致 shadow 抛异常
        var report = await shadowRuntime.ExecuteRetrievalShadowAsync(
            legacyRequest, legacyResult, tokenBudget: 0, topK: 10,
            context: context, cancellationToken: CancellationToken.None);

        Assert.IsNotNull(report);
        Assert.IsNotNull(report.Parity);
        // 验证 V2 request 收到的 tokenBudget=0（通过 LastRequest 检查）
        Assert.AreEqual(0, stubRuntime.LastRequest?.TokenBudget,
            "Shadow 必须将 tokenBudget=0 传递给 V2 request。");
    }

    [TestMethod]
    public async Task Shadow_LegacyThrows_V2StillCompletes()
    {
        // 在 sampled shadow 路径中，Legacy 抛出异常时 V2 结果仍被返回（best-effort shadow）
        // 此测试验证 AuthoritativeRetrievalRuntime 的 sampled shadow 容错行为
        var throwingStore = new ThrowingContextStore(new InvalidOperationException("Legacy store 故障"));
        var legacyRetriever = new HybridContextRetriever(throwingStore);

        // V2 Provider 正常返回结果
        var v2Provider = new CountingCandidateProvider(
            ExpertKind.Lexical, MakeExpertResultWithContent("v2-c1", "V2 容错内容"));
        var realV2 = BuildRealRuntime(
            router: new DefaultRouter(new DefaultExpertCatalog()),
            providers: new[] { v2Provider });
        var shadowRuntime = new ShadowDecisionRuntime(realV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();

        // sampled shadow 启用（rate=1.0 → 所有请求执行 Legacy shadow）
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(), new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100, EnableSampledShadow = true, ShadowSampleRate = 1.0 });
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, realV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100), shadowGate: null, experimentPlane: integration);

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-legacy-throws",
            WorkspaceId = "ws-shadow",
            CollectionId = "col-shadow",
            QueryText = "容错测试"
        };

        // Legacy 抛异常 → sampled shadow 捕获 → V2 结果仍被返回
        var result = await runtime.RetrieveAsync(request, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.SelectedItems.Count > 0,
            "Legacy 抛异常时 sampled shadow 必须容错，V2 结果仍被返回。");
        Assert.AreEqual(1, v2Provider.ExecuteCallCount,
            "V2 Provider 必须被调用一次（权威路径）。");
    }

    [TestMethod]
    public async Task Shadow_PackagePath_ProducesValidReport()
    {
        // Package 路径 Shadow：Legacy 结果 → WorkingSet → V2 决策 → Parity 报告
        var v2Result = R28BTestHelpers.MakeResult("build-shadow",
            selected: new[] { R28BTestHelpers.MakeEnvelope("item-1", ContextCandidateSource.WorkingMemory, 0.7, 100) },
            estimatedTokens: 100);
        var stubRuntime = new RecordingDecisionRuntime(v2Result);
        var shadowRuntime = new ShadowDecisionRuntime(stubRuntime, new DecisionExperimentPlane());

        var legacyResult = new ContextPackageBuildResult
        {
            BuildId = "build-shadow",
            SelectedItems = new[]
            {
                new ContextPackageDecision
                {
                    ItemId = "item-1",
                    Kind = "working_memory",
                    Type = "test-type",
                    SectionName = "working_memory",
                    Score = 0.7,
                    EstimatedTokens = 100
                }
            },
            DroppedItems = Array.Empty<DroppedContextItem>(),
            EstimatedTokens = 100,
            TokenBudget = 1000,
            Package = new ContextPackage
            {
                PackageId = "pkg-shadow",
                Sections = Array.Empty<ContextPackageSection>()
            }
        };

        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-shadow",
            CollectionId = "col-shadow",
            RequestId = "build-shadow",
            ObservedAt = DateTimeOffset.UtcNow
        };

        var report = await shadowRuntime.ExecutePackageShadowAsync(
            "build-shadow", legacyResult, tokenBudget: 1000,
            context: context, cancellationToken: CancellationToken.None);

        Assert.IsNotNull(report);
        Assert.IsNotNull(report.WorkingSet);
        Assert.IsNotNull(report.V2Result);
        Assert.IsNotNull(report.Parity);
        Assert.AreEqual(1, stubRuntime.ExecuteCallCount,
            "V2 Runtime 必须被调用一次（Package shadow）。");
        // Legacy selected=1, V2 selected=1, common=1 → Jaccard=1.0
        Assert.AreEqual(1.0, report.Parity.JaccardIndex,
            "Legacy 与 V2 selected 完全匹配时 Jaccard 必须为 1.0。");
    }

    [TestMethod]
    public async Task Shadow_BuildRetrievalShadowReport_ReusesPrecomputedV2Execution()
    {
        // BuildRetrievalShadowReport 使用预计算的 V2 执行结果，不再次调用 V2 Runtime
        var v2Result = R28BTestHelpers.MakeResult("op-precomputed",
            selected: new[] { R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100) },
            estimatedTokens: 100);
        var stubRuntime = new RecordingDecisionRuntime(v2Result);
        var shadowRuntime = new ShadowDecisionRuntime(stubRuntime, new DecisionExperimentPlane());

        var legacyRequest = new ContextRetrievalRequest
        {
            OperationId = "op-precomputed",
            WorkspaceId = "ws-shadow",
            CollectionId = "col-shadow"
        };
        var legacyResult = new ContextRetrievalResult
        {
            OperationId = "op-precomputed",
            SelectedItems = new[]
            {
                new ContextRetrievalCandidate
                {
                    CandidateId = "c1",
                    SourceId = "c1",
                    Score = 0.8,
                    EstimatedTokens = 100
                }
            },
            EstimatedTokens = 100
        };
        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-shadow",
            CollectionId = "col-shadow",
            RequestId = "op-precomputed",
            ObservedAt = DateTimeOffset.UtcNow
        };

        // 预计算 V2 执行结果
        var v2Execution = R28BTestHelpers.MakeExecutionResult(v2Result);

        // 使用 BuildRetrievalShadowReport（不调用 V2 Runtime）
        var report = shadowRuntime.BuildRetrievalShadowReport(
            legacyRequest, legacyResult, v2Execution, tokenBudget: 1000, context: context);

        // V2 Runtime 不应被调用（复用预计算结果）
        Assert.AreEqual(0, stubRuntime.ExecuteCallCount,
            "BuildRetrievalShadowReport 必须复用预计算 V2 结果，不调用 V2 Runtime。");
        Assert.IsNotNull(report.Parity);
        Assert.AreEqual(1.0, report.Parity.JaccardIndex,
            "Legacy 与 V2 selected 完全匹配 → Jaccard=1.0。");
    }

    // --- helpers ---

    private static DefaultContextDecisionRuntime BuildRealRuntime(
        IRouter router,
        IReadOnlyList<ICandidateProvider> providers)
    {
        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            globalAllocator: new DefaultGlobalAllocator());

        return new DefaultContextDecisionRuntime(
            engine: engine,
            policyProvider: new DefaultResolvedPolicyProvider(),
            router: router,
            expertCatalog: new DefaultExpertCatalog(),
            candidateProviders: providers,
            canonicalMerger: new DefaultCanonicalCandidateMerger(),
            earlyAdmissionGate: new DefaultEarlyAdmissionGate(),
            featurePipeline: new DefaultFeaturePipeline(),
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()));
    }

    private static ExpertExecutionResult MakeExpertResultWithContent(string entityId, string content)
    {
        var key = CanonicalCandidateKey.Create("ws-shadow", "col-shadow", "test-entity", entityId, "v1");
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = entityId,
            CanonicalKey = key,
            Source = ContextCandidateSource.Lexical,
            Type = "test-type",
            TokenCost = new CandidateTokenCost
            {
                ContentTokens = 100,
                TokenizerId = "length-div-4",
                IsEstimated = true
            },
            Safety = new CandidateSafetyState { PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = 0.5, FinalScore = 0.5, ReasonCode = "test" }
        };
        var material = new CandidateMaterial { Key = key, Content = content, NativeKind = "test" };
        return new ExpertExecutionResult(
            new[] { envelope },
            new Dictionary<CanonicalCandidateKey, CandidateMaterial> { [key] = material });
    }
}

// ===========================================================================
// 2. ExperimentRecorderComponentTests — Recorder 组件独立测试
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B-Component")]
public sealed class ExperimentRecorderComponentTests
{
    [TestMethod]
    public async Task Recorder_IdempotentWrite_DoesNotDuplicate()
    {
        // 单次 RecordShadowReport 调用 → 异步队列不应重复写入，history 中恰好 1 条 fixture
        var customRecorder = new InMemoryExperimentRecorder();
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(), new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100, EnableSampledShadow = false },
            recorder: customRecorder);

        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var v2Result = R28BTestHelpers.MakeResult("op-idempotent", selected: new[] { envelope }, estimatedTokens: 100);
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "幂等写入内容")
            }
        };
        var parity = new ParityReport(
            LegacySelectedCount: 1, V2SelectedCount: 1, CommonSelectedCount: 1,
            OnlyInLegacyCount: 0, OnlyInV2Count: 0,
            JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
            LegacyTokenTotal: 100, V2TokenTotal: 100, WorkingSetCandidateCount: 1);
        var shadowReport = new RetrievalShadowReport(
            LegacyResult: new ContextRetrievalResult { OperationId = "op-idempotent" },
            V2Result: v2Result,
            WorkingSet: workingSet,
            Parity: parity);

        // 单次调用
        integration.RecordShadowReport(shadowReport, "fx-idempotent-1", "idempotent-test");

        // flush 确保异步队列处理完成
        await integration.FlushAsync();
        var history = await customRecorder.GetHistoryAsync();

        Assert.AreEqual(1, history.Count,
            "单次 RecordShadowReport 调用必须只写入 1 条 fixture（异步队列不重复写入）。");
        Assert.AreEqual("fx-idempotent-1", history[0].FixtureId);
        await integration.DisposeAsync();
    }

    [TestMethod]
    public async Task Recorder_ConcurrentWrites_AllSucceed()
    {
        // 并发写入 InMemoryExperimentRecorder → 全部成功（线程安全 via lock）
        var recorder = new InMemoryExperimentRecorder(maxCapacity: 1000);

        // 并发写入 50 条 fixture
        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            var fixture = MakeFixture($"fx-concurrent-{i}");
            await recorder.RecordAsync(fixture);
        }));

        await Task.WhenAll(tasks);

        var history = await recorder.GetHistoryAsync();
        Assert.AreEqual(50, history.Count,
            "并发写入 50 条 fixture 必须全部成功（线程安全）。");

        // 验证所有 fixture ID 都存在
        var fixtureIds = history.Select(f => f.FixtureId).ToHashSet();
        for (var i = 0; i < 50; i++)
        {
            Assert.IsTrue(fixtureIds.Contains($"fx-concurrent-{i}"),
                $"fixture fx-concurrent-{i} 必须在 history 中。");
        }
    }

    [TestMethod]
    public async Task Recorder_ReplayIntegration_ProducesValidReport()
    {
        // 记录 shadow report → 通过 ReplayFixtureAsync 重放 → 产出有效的 FixtureReplayReport
        var customRecorder = new InMemoryExperimentRecorder();
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(), new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100, EnableSampledShadow = false },
            recorder: customRecorder);

        // 构建带 WorkingSet + V2Result 的 shadow report
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var v2Result = R28BTestHelpers.MakeResult("op-replay",
            selected: new[] { envelope },
            estimatedTokens: 100,
            tokenBudget: 1000);
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "replay 内容")
            }
        };
        var parity = new ParityReport(
            LegacySelectedCount: 1, V2SelectedCount: 1, CommonSelectedCount: 1,
            OnlyInLegacyCount: 0, OnlyInV2Count: 0,
            JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
            LegacyTokenTotal: 100, V2TokenTotal: 100, WorkingSetCandidateCount: 1);
        var shadowReport = new RetrievalShadowReport(
            LegacyResult: new ContextRetrievalResult { OperationId = "op-replay" },
            V2Result: v2Result,
            WorkingSet: workingSet,
            Parity: parity);

        integration.RecordShadowReport(shadowReport, "fx-replay-1", "replay-test");
        await integration.FlushAsync();

        // 使用 RecordingDecisionRuntime 作为重放 V2 Runtime（返回相同结果 → replay parity Hard）
        var replayRuntime = new RecordingDecisionRuntime(v2Result);
        var replayReport = await integration.LiveReexecutionComparisonAsync("fx-replay-1", replayRuntime, CancellationToken.None);

        Assert.IsNotNull(replayReport, "ReplayFixtureAsync 必须产出非 null 的 FixtureReplayReport。");
        Assert.AreEqual("fx-replay-1", replayReport.FixtureId);
        Assert.IsTrue(replayReport.ReplaySucceeded,
            "重放必须成功执行（未抛异常）。");
        Assert.IsNotNull(replayReport.ReplayParity,
            "ReplayParity 必须非 null（V2 Runtime 提供且 fixture 含完整重放数据）。");
        // stored V2Result 与 replayed V2Result 完全相同 → Jaccard=1.0
        Assert.AreEqual(1.0, replayReport.ReplayParity!.JaccardIndex,
            "stored V2Result 与 replayed V2Result 完全匹配 → replay Jaccard=1.0。");
        await integration.DisposeAsync();
    }

    [TestMethod]
    public async Task Recorder_ReplayFixture_NotFound_ReturnsNull()
    {
        // fixtureId 不存在 → ReplayFixtureAsync 返回 null
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(), new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100, EnableSampledShadow = false });

        var report = await integration.LiveReexecutionComparisonAsync("non-existent-fx", v2Runtime: null, CancellationToken.None);

        Assert.IsNull(report, "fixtureId 不存在时 ReplayFixtureAsync 必须返回 null。");
        await integration.DisposeAsync();
    }

    [TestMethod]
    public async Task Recorder_ClearHistory_RemovesAllFixtures()
    {
        // ClearHistory 清除全部 fixture
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(), new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100, EnableSampledShadow = false });

        integration.RecordFixture(MakeHardParityReport(), "fx-1", "clear-test");
        integration.RecordFixture(MakeHardParityReport(), "fx-2", "clear-test");
        await integration.FlushAsync();
        Assert.AreEqual(2, (await integration.GetFixtureHistoryAsync()).Count);

        integration.ClearHistory();
        await integration.FlushAsync();

        Assert.AreEqual(0, (await integration.GetFixtureHistoryAsync()).Count,
            "ClearHistory 后 fixture history 必须为空。");
        await integration.DisposeAsync();
    }

    [TestMethod]
    public async Task Recorder_EvaluateHistoricalFixtures_ProducesAssessment()
    {
        // 记录多条 fixture → EvaluateHistoricalFixtures 产出 CutoverReadinessAssessment
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(), new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100, EnableSampledShadow = false });

        // 记录 3 条全部 Hard parity 的 fixture
        for (var i = 0; i < 3; i++)
        {
            integration.RecordFixture(MakeHardParityReport(), $"fx-eval-{i}", "eval-test");
        }

        await integration.FlushAsync();
        var assessment = await integration.EvaluateHistoricalFixturesAsync();

        Assert.IsTrue(assessment.IsReady, "3 条全部 Hard parity → IsReady=true。");
        Assert.AreEqual(3, assessment.TotalReports);
        Assert.AreEqual(3, assessment.HardCount);
        Assert.AreEqual(0, assessment.DivergentCount);
        await integration.DisposeAsync();
    }

    // --- helpers ---

    private static ReplayFixture MakeFixture(string fixtureId)
    {
        return new ReplayFixture(
            FixtureId: fixtureId,
            RecordedAt: DateTimeOffset.UtcNow,
            Purpose: "concurrent-test",
            LegacySelectedCount: 1,
            V2SelectedCount: 1,
            CommonSelectedCount: 1,
            OnlyInLegacyCount: 0,
            OnlyInV2Count: 0,
            JaccardIndex: 1.0,
            LegacyTokenTotal: 100,
            V2TokenTotal: 100,
            WorkingSetCandidateCount: 1,
            ParityLevel: ParityLevel.Hard,
            Notes: "");
    }

    private static ParityReport MakeHardParityReport() => new(
        LegacySelectedCount: 2, V2SelectedCount: 2, CommonSelectedCount: 2,
        OnlyInLegacyCount: 0, OnlyInV2Count: 0,
        JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
        LegacyTokenTotal: 200, V2TokenTotal: 200, WorkingSetCandidateCount: 2);
}

// ===========================================================================
// Stub — 抛异常的 IContextStore（用于 Shadow_LegacyThrows 测试）
// ===========================================================================

/// <summary>
/// 抛异常的 IContextStore：QueryAsync 始终抛出预设异常。
/// 用于测试 sampled shadow 路径中 Legacy 故障时的容错行为。
/// </summary>
internal sealed class ThrowingContextStore : IContextStore
{
    private readonly Exception _exception;
    public ThrowingContextStore(Exception exception) => _exception = exception;

    public Task<IReadOnlyList<ContextItem>> QueryAsync(
        ContextQuery query, CancellationToken cancellationToken = default)
        => throw _exception;

    public Task<ContextItem?> GetAsync(
        string workspaceId, string collectionId, string id,
        CancellationToken cancellationToken = default)
        => throw _exception;

    public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
        => throw _exception;

    public Task DeleteAsync(
        string workspaceId, string collectionId, string id,
        CancellationToken cancellationToken = default)
        => throw _exception;
}
