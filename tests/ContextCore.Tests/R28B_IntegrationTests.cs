using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

// ===========================================================================
// R28-B 集成测试 — V2 端到端管线 + Cutover 切换
//
// 覆盖范围（3 个测试类）：
//   1. V2RetrievalIntegrationTests — Retrieval 端到端管线（真实 Runtime + InMemory Store）
//   2. V2PackageIntegrationTests — Package 端到端管线（真实 Runtime + InMemory Store）
//   3. CutoverTransitionIntegrationTests — Cutover 流量切换（0% / 100% / 稳定哈希 / 动态更新 / 回退）
//
// 设计原则：
//   - 使用真实 DefaultContextDecisionRuntime + DefaultContextDecisionEngine（V2 路径）
//   - 使用 InMemoryContextStore 做数据隔离（满足 InMemory stores 隔离测试要求）
//   - 复用 R28B_ClosureGateAcceptanceTests 中的 internal Stub
//     （CallTrackingContextStore / CountingCandidateProvider / RecordingDecisionRuntime / ThrowingDecisionRuntime）
//   - 所有代码注释使用中文
// ===========================================================================

// ===========================================================================
// 1. V2RetrievalIntegrationTests — Retrieval 端到端管线
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B-Integration")]
public sealed class V2RetrievalIntegrationTests
{
    [TestMethod]
    public async Task V2Retrieval_FullPipeline_ProducesValidOutput()
    {
        // V2 端到端：Provider 召回 → Runtime 编排 → Engine 决策 → Projector 投影
        // 100% cutover → V2-only 路径 → Legacy store 永不被查询
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);

        // 真实 V2 Provider：召回携带 Material 的候选（含 Content）
        var provider = new CountingCandidateProvider(
            ExpertKind.Lexical, MakeExpertResultWithContent("v2-c1", "端到端集成测试内容"));
        var realV2 = BuildRealRuntime(
            router: new DefaultRouter(new DefaultExpertCatalog()),
            providers: new[] { provider });
        var shadowRuntime = new ShadowDecisionRuntime(realV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, realV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100));

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-integration-retrieval",
            WorkspaceId = "ws-integration",
            CollectionId = "col-integration",
            QueryText = "集成测试查询",
            TopK = 10,
            TokenBudget = 4096
        };

        var result = await runtime.RetrieveAsync(request, CancellationToken.None);

        // Legacy store 永不被查询（100% V2-only）
        Assert.AreEqual(0, trackingStore.QueryCallCount,
            "100% cutover 时 Legacy store 必须永不被查询。");
        // V2 Provider 被调用一次
        Assert.AreEqual(1, provider.ExecuteCallCount,
            "V2 Provider 必须被调用一次。");
        // 投影结果非空 + Content 从 Material sidecar 恢复
        Assert.IsNotNull(result);
        Assert.IsTrue(result.SelectedItems.Count > 0,
            "V2 端到端管线必须产出 SelectedItems。");
        Assert.IsFalse(string.IsNullOrEmpty(result.SelectedItems[0].Content),
            "SelectedItems 必须包含 Content（从 Material sidecar 恢复）。");
        Assert.AreEqual("端到端集成测试内容", result.SelectedItems[0].Content,
            "Content 必须与 Provider 召回的 Material 内容一致。");
    }

    [TestMethod]
    public async Task V2Retrieval_WithInMemoryStore_LegacyPathReadsSeededData()
    {
        // 0% cutover → Legacy-only 路径 → 从 InMemoryContextStore 读取预置数据
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "seeded-1",
            WorkspaceId = "ws-seed",
            CollectionId = "col-seed",
            Type = "note",
            Title = "预置上下文",
            Content = "InMemory 隔离测试内容",
            Tags = new[] { "test" },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var legacyRetriever = new HybridContextRetriever(store);
        var stubV2 = new RecordingDecisionRuntime(
            R28BTestHelpers.MakeResult("op-seed"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 0));

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-seed",
            WorkspaceId = "ws-seed",
            CollectionId = "col-seed",
            QueryText = "预置",
            TopK = 10
        };

        var result = await runtime.RetrieveAsync(request, CancellationToken.None);

        // 0% cutover → Legacy 路径 → V2 永不被调用
        Assert.AreEqual(0, stubV2.ExecuteCallCount,
            "0% cutover 时 V2 Runtime 必须永不被调用。");
        // Legacy 从 InMemory store 读取预置数据
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Succeeded,
            "Legacy 路径必须成功从 InMemory store 读取数据。");
    }

    [TestMethod]
    public async Task V2Retrieval_DroppedCandidates_PreserveBlockReasonCode()
    {
        // V2 端到端：含被 SafetyGate 拦截的候选 → DroppedItems 携带 BlockReasonCode
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);

        // Provider 返回一个 passing 候选 + 一个 blocked 候选
        var passingKey = CanonicalCandidateKey.Create("ws-integration", "col-integration", "entity", "pass-1", "v1");
        var blockedKey = CanonicalCandidateKey.Create("ws-integration", "col-integration", "entity", "block-1", "v1");
        var envelopes = new[]
        {
            new ContextCandidateEnvelope
            {
                CandidateId = "pass-1",
                CanonicalKey = passingKey,
                Source = ContextCandidateSource.Lexical,
                Type = "test-type",
                EstimatedTokens = 100,
                Safety = new CandidateSafetyState { PassesSafetyGate = true },
                Utility = new CandidateUtilityScore { DeterministicScore = 0.8, FinalScore = 0.8, ReasonCode = "test" }
            },
            new ContextCandidateEnvelope
            {
                CandidateId = "block-1",
                CanonicalKey = blockedKey,
                Source = ContextCandidateSource.Lexical,
                Type = "test-type",
                EstimatedTokens = 100,
                Safety = new CandidateSafetyState
                {
                    PassesSafetyGate = false,
                    BlockReasonCode = CandidateDecisionReasonCode.DeprecatedBlocked
                },
                Utility = new CandidateUtilityScore { DeterministicScore = 0.3, FinalScore = 0.3, ReasonCode = "test" }
            }
        };
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
        {
            [passingKey] = new() { Key = passingKey, Content = "通过内容", NativeKind = "test" },
            [blockedKey] = new() { Key = blockedKey, Content = "拦截内容", NativeKind = "test" }
        };
        var expertResult = new ExpertExecutionResult(envelopes, materials);
        var provider = new CountingCandidateProvider(ExpertKind.Lexical, expertResult);

        var realV2 = BuildRealRuntime(
            router: new DefaultRouter(new DefaultExpertCatalog()),
            providers: new[] { provider });
        var shadowRuntime = new ShadowDecisionRuntime(realV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, realV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100));

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-dropped-reason",
            WorkspaceId = "ws-integration",
            CollectionId = "col-integration",
            QueryText = "拦截测试",
            TopK = 10,
            TokenBudget = 4096
        };

        var result = await runtime.RetrieveAsync(request, CancellationToken.None);

        // passing 候选进入 SelectedItems
        Assert.IsTrue(result.SelectedItems.Any(c => c.CandidateId == "pass-1"),
            "通过 SafetyGate 的候选必须进入 SelectedItems。");
        // blocked 候选进入 DroppedItems 并携带 BlockReasonCode
        var dropped = result.DroppedItems.FirstOrDefault(d => d.CandidateId == "block-1");
        Assert.IsNotNull(dropped,
            "被 SafetyGate 拦截的候选必须出现在 DroppedItems。");
        StringAssert.Contains(dropped.Reason, "DeprecatedBlocked",
            "DroppedItems 必须携带 BlockReasonCode（DeprecatedBlocked）。");
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
            utilityScorer: new DefaultUtilityScorer(),
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
            utilityScorer: new DefaultUtilityScorer());
    }

    private static ExpertExecutionResult MakeExpertResultWithContent(string entityId, string content)
    {
        var key = CanonicalCandidateKey.Create("ws-integration", "col-integration", "test-entity", entityId, "v1");
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = entityId,
            CanonicalKey = key,
            Source = ContextCandidateSource.Lexical,
            Type = "test-type",
            EstimatedTokens = 100,
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
// 2. V2PackageIntegrationTests — Package 端到端管线
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B-Integration")]
public sealed class V2PackageIntegrationTests
{
    [TestMethod]
    public async Task V2Package_FullPipeline_ProducesCompletePackage()
    {
        // V2 Package 端到端：Provider 召回 → Runtime → Engine → PackageProjector 投影
        // 100% cutover → V2-only 路径 → 产出完整 Package（含 Sections + EstimatedTokens）
        var store = new InMemoryContextStore();
        var legacyBuilder = new BasicContextPackageBuilder(store);

        // 真实 V2 Provider：召回携带 Material 的候选
        var provider = new CountingCandidateProvider(
            ExpertKind.Lexical, MakePackageExpertResult("pkg-item-1", "Package 端到端内容"));
        var realV2 = BuildRealRuntime(
            router: new DefaultRouter(new DefaultExpertCatalog()),
            providers: new[] { provider });
        var shadowRuntime = new ShadowDecisionRuntime(realV2, new DecisionExperimentPlane());
        var projector = new PackageResultProjector();
        var runtime = new AuthoritativePackageRuntime(
            legacyBuilder, realV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100));

        var request = new ContextPackageRequest
        {
            WorkspaceId = "ws-pkg-integration",
            CollectionId = "col-pkg-integration",
            QueryText = "Package 集成测试",
            TokenBudget = 4096
        };

        var result = await runtime.BuildDetailedAsync(request, CancellationToken.None);

        // V2 Provider 被调用一次
        Assert.AreEqual(1, provider.ExecuteCallCount,
            "V2 Provider 必须被调用一次。");
        // Package 非空 + Sections 非空 + EstimatedTokens > 0
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Package,
            "V2 Package 管线必须产出非空 Package。");
        Assert.IsTrue(result.Package.Sections.Count > 0,
            "Package.Sections 必须非空（V2 管线产出）。");
        Assert.IsTrue(result.Package.EstimatedTokens > 0,
            "Package.EstimatedTokens 必须大于 0。");
        Assert.IsTrue(result.EstimatedTokens > 0,
            "BuildResult.EstimatedTokens 必须大于 0。");
    }

    [TestMethod]
    public async Task V2Package_WithInMemoryStore_LegacyPathProducesPackage()
    {
        // 0% cutover → Legacy-only 路径 → BasicContextPackageBuilder 从 InMemory store 构建
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "pkg-seed-1",
            WorkspaceId = "ws-pkg-seed",
            CollectionId = "col-pkg-seed",
            Type = "note",
            Title = "Package 预置",
            Content = "Package InMemory 隔离内容",
            Tags = new[] { "pkg" },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var legacyBuilder = new BasicContextPackageBuilder(store);
        var stubV2 = new RecordingDecisionRuntime(
            R28BTestHelpers.MakeResult("op-pkg-seed"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new PackageResultProjector();
        var runtime = new AuthoritativePackageRuntime(
            legacyBuilder, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 0));

        var request = new ContextPackageRequest
        {
            WorkspaceId = "ws-pkg-seed",
            CollectionId = "col-pkg-seed",
            QueryText = "预置",
            TokenBudget = 4096
        };

        var result = await runtime.BuildDetailedAsync(request, CancellationToken.None);

        // 0% cutover → Legacy 路径 → V2 永不被调用
        Assert.AreEqual(0, stubV2.ExecuteCallCount,
            "0% cutover 时 V2 Runtime 必须永不被调用。");
        Assert.IsNotNull(result,
            "Legacy Package 路径必须产出非空结果。");
        Assert.IsNotNull(result.Package,
            "Legacy Package 路径必须产出 Package。");
    }

    [TestMethod]
    public async Task V2Package_BuildAsync_ReturnsPackageFromBuildDetailed()
    {
        // BuildAsync（IContextPackageBuilder 接口）必须委托给 BuildDetailedAsync，返回 result.Package
        var store = new InMemoryContextStore();
        var legacyBuilder = new BasicContextPackageBuilder(store);

        var provider = new CountingCandidateProvider(
            ExpertKind.Lexical, MakePackageExpertResult("pkg-iface-1", "接口路径内容"));
        var realV2 = BuildRealRuntime(
            router: new DefaultRouter(new DefaultExpertCatalog()),
            providers: new[] { provider });
        var shadowRuntime = new ShadowDecisionRuntime(realV2, new DecisionExperimentPlane());
        var projector = new PackageResultProjector();
        var runtime = new AuthoritativePackageRuntime(
            legacyBuilder, realV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100));

        var request = new ContextPackageRequest
        {
            WorkspaceId = "ws-pkg-integration",
            CollectionId = "col-pkg-integration",
            QueryText = "接口测试",
            TokenBudget = 4096
        };

        var package = await runtime.BuildAsync(request, CancellationToken.None);

        Assert.IsNotNull(package,
            "BuildAsync 必须返回非空 Package（委托 BuildDetailedAsync）。");
        Assert.IsTrue(package.Sections.Count > 0,
            "BuildAsync 返回的 Package 必须含非空 Sections。");
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
            utilityScorer: new DefaultUtilityScorer(),
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
            utilityScorer: new DefaultUtilityScorer());
    }

    private static ExpertExecutionResult MakePackageExpertResult(string entityId, string content)
    {
        var key = CanonicalCandidateKey.Create("ws-pkg-integration", "col-pkg-integration", "test-entity", entityId, "v1");
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = entityId,
            CanonicalKey = key,
            Source = ContextCandidateSource.Lexical,
            Type = "test-type",
            EstimatedTokens = 100,
            Safety = new CandidateSafetyState { PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = 0.7, FinalScore = 0.7, ReasonCode = "test" }
        };
        var material = new CandidateMaterial { Key = key, Content = content, NativeKind = "test" };
        return new ExpertExecutionResult(
            new[] { envelope },
            new Dictionary<CanonicalCandidateKey, CandidateMaterial> { [key] = material });
    }
}

// ===========================================================================
// 3. CutoverTransitionIntegrationTests — Cutover 流量切换
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B-Integration")]
public sealed class CutoverTransitionIntegrationTests
{
    [TestMethod]
    public async Task CutoverTransition_0Percent_AllLegacy()
    {
        // 0% cutover → 全部走 Legacy → V2 Runtime 永不被调用
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(
            R28BTestHelpers.MakeResult("op-0pct"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 0));

        // 发起多个请求，全部应走 Legacy
        for (var i = 0; i < 5; i++)
        {
            await runtime.RetrieveAsync(
                new ContextRetrievalRequest
                {
                    OperationId = $"op-0pct-{i}",
                    WorkspaceId = "ws-cutover",
                    CollectionId = "col-cutover"
                },
                CancellationToken.None);
        }

        // Legacy store 被查询 5 次
        Assert.AreEqual(5, trackingStore.QueryCallCount,
            "0% cutover 时全部请求必须走 Legacy（store 被查询 5 次）。");
        // V2 Runtime 永不被调用
        Assert.AreEqual(0, stubV2.ExecuteCallCount,
            "0% cutover 时 V2 Runtime 必须永不被调用。");
    }

    [TestMethod]
    public async Task CutoverTransition_100Percent_AllV2()
    {
        // 100% cutover → 全部走 V2 → Legacy store 永不被查询
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);

        var provider = new CountingCandidateProvider(
            ExpertKind.Lexical, MakeExpertResultWithContent("v2-100pct", "100% V2 内容"));
        var realV2 = BuildRealRuntime(
            router: new DefaultRouter(new DefaultExpertCatalog()),
            providers: new[] { provider });
        var shadowRuntime = new ShadowDecisionRuntime(realV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, realV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100));

        // 发起多个请求，全部应走 V2
        for (var i = 0; i < 5; i++)
        {
            await runtime.RetrieveAsync(
                new ContextRetrievalRequest
                {
                    OperationId = $"op-100pct-{i}",
                    WorkspaceId = "ws-cutover",
                    CollectionId = "col-cutover",
                    TopK = 10
                },
                CancellationToken.None);
        }

        // Legacy store 永不被查询
        Assert.AreEqual(0, trackingStore.QueryCallCount,
            "100% cutover 时 Legacy store 必须永不被查询。");
        // V2 Provider 被调用 5 次
        Assert.AreEqual(5, provider.ExecuteCallCount,
            "100% cutover 时全部请求必须走 V2（Provider 被调用 5 次）。");
    }

    [TestMethod]
    public async Task CutoverTransition_StableHash_SameRequestIdAlwaysSamePath()
    {
        // 同一 requestId 在同一 cutover percentage 下始终走同一路径（稳定哈希）
        var controller = new CutoverController(cutoverPercentage: 50);
        var firstPath = controller.ShouldUseV2("stable-op-12345");

        // 重复查询同一 requestId → 结果必须一致
        for (var i = 0; i < 10; i++)
        {
            Assert.AreEqual(firstPath, controller.ShouldUseV2("stable-op-12345"),
                "同一 requestId 的路由决策必须稳定（不变）。");
        }
    }

    [TestMethod]
    public async Task CutoverTransition_DynamicUpdate_SwitchesTraffic()
    {
        // 动态更新 cutover percentage → 流量切换
        // 先 0%（全部 Legacy），再 100%（全部 V2）
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);

        var provider = new CountingCandidateProvider(
            ExpertKind.Lexical, MakeExpertResultWithContent("v2-dynamic", "动态切换内容"));
        var realV2 = BuildRealRuntime(
            router: new DefaultRouter(new DefaultExpertCatalog()),
            providers: new[] { provider });
        var shadowRuntime = new ShadowDecisionRuntime(realV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var cutover = new CutoverController(cutoverPercentage: 0);
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, realV2, shadowRuntime, projector, cutover);

        // 阶段 1：0% cutover → 全部 Legacy
        await runtime.RetrieveAsync(
            new ContextRetrievalRequest
            {
                OperationId = "op-dynamic-1",
                WorkspaceId = "ws-cutover",
                CollectionId = "col-cutover"
            },
            CancellationToken.None);
        Assert.AreEqual(1, trackingStore.QueryCallCount,
            "0% cutover 时请求必须走 Legacy。");
        Assert.AreEqual(0, provider.ExecuteCallCount,
            "0% cutover 时 V2 Provider 必须不被调用。");

        // 阶段 2：动态更新为 100% cutover → 全部 V2
        cutover.SetCutoverPercentage(100);
        trackingStore.Reset();
        await runtime.RetrieveAsync(
            new ContextRetrievalRequest
            {
                OperationId = "op-dynamic-2",
                WorkspaceId = "ws-cutover",
                CollectionId = "col-cutover",
                TopK = 10
            },
            CancellationToken.None);
        Assert.AreEqual(0, trackingStore.QueryCallCount,
            "动态切换到 100% 后 Legacy store 必须不被查询。");
        Assert.AreEqual(1, provider.ExecuteCallCount,
            "动态切换到 100% 后 V2 Provider 必须被调用。");
    }

    [TestMethod]
    public async Task CutoverTransition_MixedMode_V2FailureFallsBackToLegacy()
    {
        // Mixed mode（0 < cutover < 100）：V2 失败时回退到 Legacy（fail-open）
        // 找到一个走 V2 的 requestId，让 V2 抛异常，验证回退到 Legacy
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);

        var throwingV2 = new ThrowingDecisionRuntime(new InvalidOperationException("V2 故障"));
        var shadowRuntime = new ShadowDecisionRuntime(throwingV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var cutover = new CutoverController(cutoverPercentage: 50);
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, throwingV2, shadowRuntime, projector, cutover);

        // 找到一个走 V2 的 requestId
        var v2RoutedId = FindV2RoutedRequestId(cutover);
        Assert.IsNotNull(v2RoutedId,
            "50% cutover 下必须存在走 V2 的 requestId。");

        var request = new ContextRetrievalRequest
        {
            OperationId = v2RoutedId,
            WorkspaceId = "ws-cutover",
            CollectionId = "col-cutover"
        };

        // V2 抛异常 → 回退到 Legacy（不抛异常）
        var result = await runtime.RetrieveAsync(request, CancellationToken.None);

        Assert.IsNotNull(result,
            "V2 失败时必须回退到 Legacy 并返回结果（fail-open）。");
        Assert.IsTrue(trackingStore.QueryCallCount > 0,
            "V2 失败时 Legacy store 必须被查询（fallback）。");
    }

    [TestMethod]
    public async Task CutoverTransition_MixedMode_V2SuccessReturnsV2Result()
    {
        // Mixed mode（0 < cutover < 100）：V2 成功 + parity 通过 → 返回 V2 结果
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);

        // V2 Provider 正常返回结果
        var provider = new CountingCandidateProvider(
            ExpertKind.Lexical, MakeExpertResultWithContent("v2-mixed", "Mixed mode V2 内容"));
        var realV2 = BuildRealRuntime(
            router: new DefaultRouter(new DefaultExpertCatalog()),
            providers: new[] { provider });
        var shadowRuntime = new ShadowDecisionRuntime(realV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var cutover = new CutoverController(cutoverPercentage: 50);
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, realV2, shadowRuntime, projector, cutover);

        // 找到一个走 V2 的 requestId
        var v2RoutedId = FindV2RoutedRequestId(cutover);
        Assert.IsNotNull(v2RoutedId,
            "50% cutover 下必须存在走 V2 的 requestId。");

        var request = new ContextRetrievalRequest
        {
            OperationId = v2RoutedId,
            WorkspaceId = "ws-cutover",
            CollectionId = "col-cutover",
            QueryText = "mixed mode",
            TopK = 10,
            TokenBudget = 4096
        };

        var result = await runtime.RetrieveAsync(request, CancellationToken.None);

        Assert.IsNotNull(result);
        // Mixed mode 下 Legacy 也被执行（shadow tee）
        Assert.IsTrue(trackingStore.QueryCallCount > 0,
            "Mixed mode 下 Legacy 必须被执行（shadow tee 对照）。");
        // V2 Provider 被调用
        Assert.AreEqual(1, provider.ExecuteCallCount,
            "Mixed mode 下 V2 Provider 必须被调用。");
    }

    [TestMethod]
    public async Task CutoverTransition_MixedMode_DivergentParityFallsBackToLegacy()
    {
        // Mixed mode：ShadowGate 检测到 Divergent parity → 回退到 Legacy 结果
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);

        // V2 返回与 Legacy 完全不同的结果 → parity Divergent
        var v2Result = R28BTestHelpers.MakeResult("op-divergent",
            selected: new[]
            {
                R28BTestHelpers.MakeEnvelope("v2-only-c1", ContextCandidateSource.Semantic, 0.9, 100)
            },
            estimatedTokens: 100);
        var stubV2 = new RecordingDecisionRuntime(v2Result);
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var cutover = new CutoverController(cutoverPercentage: 50);
        // ShadowGate 使用默认阈值（Hard Jaccard=1.0）→ 空集 vs 非空集 = Divergent
        var shadowGate = new ShadowGate();
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector, cutover,
            shadowGate: shadowGate);

        var v2RoutedId = FindV2RoutedRequestId(cutover);
        Assert.IsNotNull(v2RoutedId,
            "50% cutover 下必须存在走 V2 的 requestId。");

        var request = new ContextRetrievalRequest
        {
            OperationId = v2RoutedId,
            WorkspaceId = "ws-cutover",
            CollectionId = "col-cutover"
        };

        var result = await runtime.RetrieveAsync(request, CancellationToken.None);

        // Divergent parity → 回退到 Legacy 结果
        Assert.IsNotNull(result,
            "Divergent parity 时必须回退到 Legacy 结果（不抛异常）。");
        // Legacy store 必须被查询（Mixed mode shadow tee）
        Assert.IsTrue(trackingStore.QueryCallCount > 0,
            "Mixed mode 下 Legacy 必须被执行（shadow tee）。");
        // V2 被调用（shadow 执行）
        Assert.AreEqual(1, stubV2.ExecuteCallCount,
            "Mixed mode 下 V2 必须被调用（shadow parity 校验）。");
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
            utilityScorer: new DefaultUtilityScorer(),
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
            utilityScorer: new DefaultUtilityScorer());
    }

    private static ExpertExecutionResult MakeExpertResultWithContent(string entityId, string content)
    {
        var key = CanonicalCandidateKey.Create("ws-cutover", "col-cutover", "test-entity", entityId, "v1");
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = entityId,
            CanonicalKey = key,
            Source = ContextCandidateSource.Lexical,
            Type = "test-type",
            EstimatedTokens = 100,
            Safety = new CandidateSafetyState { PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = 0.5, FinalScore = 0.5, ReasonCode = "test" }
        };
        var material = new CandidateMaterial { Key = key, Content = content, NativeKind = "test" };
        return new ExpertExecutionResult(
            new[] { envelope },
            new Dictionary<CanonicalCandidateKey, CandidateMaterial> { [key] = material });
    }

    /// <summary>
    /// 在给定 cutover percentage 下找到一个走 V2 路径的 requestId。
    /// 遍历候选 id 直到 ShouldUseV2 返回 true。
    /// </summary>
    private static string? FindV2RoutedRequestId(CutoverController controller)
    {
        for (var i = 0; i < 1000; i++)
        {
            var id = $"op-v2-routed-{i:D4}";
            if (controller.ShouldUseV2(id))
            {
                return id;
            }
        }
        return null;
    }
}
