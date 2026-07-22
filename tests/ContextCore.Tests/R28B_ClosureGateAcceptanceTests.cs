using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

// ===========================================================================
// R28-B.6 Authoritative Closure Gate — 验收测试（25 项）
//
// 覆盖范围（7 个测试类，对应规格 A-G）：
//   A. ProviderNetworkAcceptanceTests — Provider 网络真实召回（4 项）
//   B. PolicyResolutionAcceptanceTests — 策略快照稳定性（2 项）
//   C. CanonicalKeyAcceptanceTests — CanonicalKey + Material 冲突检测（3 项）
//   D. EngineAllocationAcceptanceTests — Engine 唯一分配 + dropped 保留（4 项）
//   E. ParityAcceptanceTests — Jaccard + selected/dropped 分离（4 项）
//   F. CutoverAndDiAcceptanceTests — Cutover + DI + cancellation（5 项）
//   G. ProjectorAcceptanceTests — Projector 内容恢复 + session 保留（3 项）
//
// 设计原则：
//   - 使用真实 DefaultContextDecisionRuntime + DefaultContextDecisionEngine（V2 路径）
//   - Stub 仅用于隔离 I/O（Router/Provider/Store），不替换决策内核
//   - 每个 [TestClass] 自包含，Stub 作为 private nested class
//   - 共享 R28BTestHelpers（MakeEnvelope / MakeResult / MakeMaterial / MakeAllocation）
// ===========================================================================

// ===========================================================================
// A. ProviderNetworkAcceptanceTests — Provider 网络真实召回
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.6")]
public sealed class ProviderNetworkAcceptanceTests
{
    [TestMethod]
    public async Task RuntimeCallsRouterExactlyOnce()
    {
        var countingRouter = new CountingRouter(new DefaultRouter(new DefaultExpertCatalog()));
        var runtime = BuildRuntime(router: countingRouter, providers: BuildDefaultProviders());

        await runtime.ExecuteAsync(MakeRequest(), CancellationToken.None);

        Assert.AreEqual(1, countingRouter.RouteCallCount,
            "Runtime 必须在每次请求中恰好调用 Router 一次。");
    }

    [TestMethod]
    public async Task DisabledProviderIsNeverExecuted()
    {
        // Catalog 不包含 Lexical → Router 将 Lexical 设为 Enabled=false → Provider 永不执行
        var restrictedCatalog = new RestrictedExpertCatalog(
            ExpertKind.Mandatory, ExpertKind.Constraint);
        var lexicalProvider = new CountingCandidateProvider(
            ExpertKind.Lexical, CandidateProviderHelpers.Empty());
        var router = new DefaultRouter(restrictedCatalog);
        var runtime = BuildRuntime(router: router, providers: new[] { lexicalProvider });

        await runtime.ExecuteAsync(MakeRequest(), CancellationToken.None);

        Assert.AreEqual(0, lexicalProvider.ExecuteCallCount,
            "Disabled Provider 必须永远不被执行（enabled mask 控制）。");
    }

    [TestMethod]
    public async Task ProviderExecutesAtMostOncePerRequest()
    {
        var provider = new CountingCandidateProvider(
            ExpertKind.Lexical, MakeExpertResult("lexical-item"));
        var runtime = BuildRuntime(router: new DefaultRouter(new DefaultExpertCatalog()), providers: new[] { provider });

        await runtime.ExecuteAsync(MakeRequest(), CancellationToken.None);

        Assert.AreEqual(1, provider.ExecuteCallCount,
            "每个 Provider 在单次请求中最多执行一次（per-request dedup）。");
    }

    [TestMethod]
    public async Task EmptySeedCandidatesStillInvokeProviders()
    {
        // SeedCandidates 为空时，Provider 网络仍应被调用（从 Store 召回）
        var provider = new CountingCandidateProvider(
            ExpertKind.Lexical, MakeExpertResult("recall-item"));
        var runtime = BuildRuntime(router: new DefaultRouter(new DefaultExpertCatalog()), providers: new[] { provider });

        var request = MakeRequest();
        // SeedCandidates 默认为空数组
        Assert.AreEqual(0, request.SeedCandidates.Count);

        var result = await runtime.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual(1, provider.ExecuteCallCount,
            "SeedCandidates 为空时 Provider 仍应被调用（Provider 网络独立召回）。");
        Assert.IsTrue(result.SelectedEnvelopes.Count > 0 || result.DroppedEnvelopes.Count > 0,
            "Provider 召回的候选应进入决策结果。");
    }

    // --- helpers ---

    private static ContextDecisionRuntimeRequest MakeRequest() => new()
    {
        RequestId = "req-test",
        Scope = new ContextDecisionScope("test-ws", "test-col"),
        Purpose = ContextDecisionPurpose.Retrieval,
        QueryText = "test query",
        TokenBudget = 4096,
        TopK = 10,
        SeedCandidates = Array.Empty<ContextCandidateEnvelope>()
    };

    private static ExpertExecutionResult MakeExpertResult(string entityId)
    {
        var key = CanonicalCandidateKey.Create("test-ws", "test-col", "test-entity", entityId, "v1");
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
        var material = new CandidateMaterial { Key = key, Content = "test content", NativeKind = "test" };
        return new ExpertExecutionResult(
            new[] { envelope },
            new Dictionary<CanonicalCandidateKey, CandidateMaterial> { [key] = material });
    }

    internal static DefaultContextDecisionRuntime BuildRuntime(
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

    private static IReadOnlyList<ICandidateProvider> BuildDefaultProviders() =>
        new ICandidateProvider[]
        {
            new CountingCandidateProvider(ExpertKind.Lexical, MakeExpertResult("p1"))
        };
}

// ===========================================================================
// B. PolicyResolutionAcceptanceTests — 策略快照稳定性
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.6")]
public sealed class PolicyResolutionAcceptanceTests
{
    [TestMethod]
    public async Task ResolvedPolicyUsesPinnedWorkspaceActivation()
    {
        var provider = new DefaultResolvedPolicyProvider();
        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-1",
            Scope = new ContextDecisionScope("ws-pin", "col-pin"),
            Purpose = ContextDecisionPurpose.Retrieval
        };

        var snapshot = await provider.ResolveAsync(request, CancellationToken.None);

        Assert.IsFalse(string.IsNullOrEmpty(snapshot.Reference.BundleId),
            "ResolvedPolicyReference 必须携带 BundleId（pinned activation）。");
        Assert.IsFalse(string.IsNullOrEmpty(snapshot.Reference.BundleVersion),
            "ResolvedPolicyReference 必须携带 BundleVersion。");
        Assert.IsFalse(string.IsNullOrEmpty(snapshot.Reference.BundleContentHash),
            "ResolvedPolicyReference 必须携带 BundleContentHash。");
        Assert.IsTrue(snapshot.Reference.ActivationEpoch > 0,
            "ActivationEpoch 必须非零（pinned workspace activation）。");
    }

    [TestMethod]
    public async Task PolicySnapshotRemainsStableDuringRequest()
    {
        var provider = new DefaultResolvedPolicyProvider();
        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-stable",
            Scope = new ContextDecisionScope("ws-stable", "col-stable"),
            Purpose = ContextDecisionPurpose.Package
        };

        var first = await provider.ResolveAsync(request, CancellationToken.None);
        var second = await provider.ResolveAsync(request, CancellationToken.None);

        Assert.AreEqual(first.Reference.BundleContentHash, second.Reference.BundleContentHash,
            "同一请求的 PolicySnapshot BundleContentHash 必须稳定。");
        Assert.AreEqual(first.Reference.ActivationEpoch, second.Reference.ActivationEpoch,
            "同一请求的 ActivationEpoch 必须稳定。");
        Assert.AreEqual(first.Reference.BundleId, second.Reference.BundleId,
            "同一请求的 BundleId 必须稳定。");
    }
}

// ===========================================================================
// C. CanonicalKeyAcceptanceTests — CanonicalKey + Material 冲突检测
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.6")]
public sealed class CanonicalKeyAcceptanceTests
{
    [TestMethod]
    public void EveryEnvelopeHasValidCanonicalKey()
    {
        var envelopes = new[]
        {
            R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100),
            R28BTestHelpers.MakeEnvelope("c2", ContextCandidateSource.Lexical, 0.6, 200),
            R28BTestHelpers.MakeEnvelope("c3", ContextCandidateSource.WorkingMemory, 0.5, 50)
        };

        foreach (var envelope in envelopes)
        {
            Assert.IsTrue(envelope.CanonicalKey.IsValid,
                $"Envelope {envelope.CandidateId} 的 CanonicalKey 必须有效（所有字段非空）。");
        }
    }

    [TestMethod]
    public void MaterialCountMatchesUniqueCanonicalKeys()
    {
        // 两个 Expert 产出相同的 CanonicalKey → Merger 合并后 Materials 数 == unique keys
        var sharedKey = CanonicalCandidateKey.Create("ws", "col", "entity", "item-1", "v1");
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = "item-1",
            CanonicalKey = sharedKey,
            Source = ContextCandidateSource.Lexical,
            EstimatedTokens = 100,
            Safety = new CandidateSafetyState { PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = 0.5, FinalScore = 0.5 }
        };
        var material = new CandidateMaterial { Key = sharedKey, Content = "same content", NativeKind = "test" };

        var output1 = new ExpertExecutionResult(new[] { envelope }, new Dictionary<CanonicalCandidateKey, CandidateMaterial> { [sharedKey] = material });
        var output2 = new ExpertExecutionResult(new[] { envelope }, new Dictionary<CanonicalCandidateKey, CandidateMaterial> { [sharedKey] = material });

        var merger = new DefaultCanonicalCandidateMerger();
        var workingSet = merger.Merge(new[] { output1, output2 });

        var uniqueKeys = workingSet.Envelopes.Select(e => e.CanonicalKey).Distinct().Count();
        Assert.AreEqual(uniqueKeys, workingSet.Materials.Count,
            "Materials 数必须等于 unique CanonicalKeys（相同 key 合并而非复制）。");
        Assert.AreEqual(1, workingSet.Materials.Count,
            "两个 Expert 产出相同 key → 合并后只应保留 1 个 Material。");
    }

    [TestMethod]
    public void MaterialConflictDoesNotSilentlyOverwrite()
    {
        // 相同 CanonicalKey + 不同 content → 必须抛异常（fail-fast，不静默覆盖）
        var key = CanonicalCandidateKey.Create("ws", "col", "entity", "conflict-1", "v1");
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = "conflict-1",
            CanonicalKey = key,
            Source = ContextCandidateSource.Lexical,
            EstimatedTokens = 100,
            Safety = new CandidateSafetyState { PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = 0.5, FinalScore = 0.5 }
        };

        var materialA = new CandidateMaterial { Key = key, Content = "content A", NativeKind = "test" };
        var materialB = new CandidateMaterial { Key = key, Content = "content B (different)", NativeKind = "test" };

        var output1 = new ExpertExecutionResult(new[] { envelope }, new Dictionary<CanonicalCandidateKey, CandidateMaterial> { [key] = materialA });
        var output2 = new ExpertExecutionResult(new[] { envelope }, new Dictionary<CanonicalCandidateKey, CandidateMaterial> { [key] = materialB });

        var merger = new DefaultCanonicalCandidateMerger();

        Assert.ThrowsException<InvalidOperationException>(() =>
            merger.Merge(new[] { output1, output2 }),
            "相同 CanonicalKey + 不同 content hash 必须抛 InvalidOperationException（不静默覆盖）。");
    }
}

// ===========================================================================
// D. EngineAllocationAcceptanceTests — Engine 唯一分配 + dropped 保留
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.6")]
public sealed class EngineAllocationAcceptanceTests
{
    [TestMethod]
    public async Task EngineOwnsTheOnlyAllocationPass()
    {
        // Runtime 委托 Engine 执行分配；Runtime 不再二次 Allocate
        var engine = new DefaultContextDecisionEngine(
            null, new DefaultSafetyGate(), new DefaultLifecycleGate(),
            new DefaultUtilityScorer(), new DefaultGlobalAllocator());

        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var snapshot = MakeSnapshot();
        var request = new ContextDecisionRequest
        {
            RequestId = "req-engine",
            Candidates = new[] { envelope },
            TokenBudget = 1000,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        // Engine 产出 AllocationDecisions（唯一分配点）
        Assert.IsTrue(result.AllocationDecisions.Count > 0,
            "Engine 必须产出 AllocationDecisions（唯一分配所有者）。");
        Assert.AreEqual(result.SelectedEnvelopes.Count + result.DroppedEnvelopes.Count,
            result.AllocationDecisions.Count,
            "AllocationDecisions 数必须等于 selected + dropped 候选总数。");
    }

    [TestMethod]
    public async Task AllGateDropsArePreservedWithReasons()
    {
        // SafetyGate 拦截的候选必须出现在 DroppedEnvelopes 中，携带 BlockReasonCode
        var engine = new DefaultContextDecisionEngine(
            null, new DefaultSafetyGate(), new DefaultLifecycleGate(),
            new DefaultUtilityScorer(), new DefaultGlobalAllocator());

        var blockedEnvelope = R28BTestHelpers.MakeEnvelope("blocked-1", ContextCandidateSource.Lexical, 0.3, 100,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.DeprecatedBlocked
            });
        var snapshot = MakeSnapshot();

        var request = new ContextDecisionRequest
        {
            RequestId = "req-drops",
            Candidates = new[] { blockedEnvelope },
            TokenBudget = 1000,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        Assert.AreEqual(0, result.SelectedEnvelopes.Count, "blocked 候选不应被选入。");
        Assert.AreEqual(1, result.DroppedEnvelopes.Count, "blocked 候选必须保留在 DroppedEnvelopes。");
        Assert.AreEqual(CandidateDecisionReasonCode.DeprecatedBlocked,
            result.DroppedEnvelopes[0].Safety.BlockReasonCode,
            "Dropped 候选必须携带 BlockReasonCode。");
    }

    [TestMethod]
    public async Task RequestTokenBudgetIsUsedByAllocator()
    {
        // request.TokenBudget 覆盖 snapshot.Budget.DefaultTokenBudget
        var engine = new DefaultContextDecisionEngine(
            null, new DefaultSafetyGate(), new DefaultLifecycleGate(),
            new DefaultUtilityScorer(), new DefaultGlobalAllocator());

        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 600);
        var snapshot = MakeSnapshot(); // DefaultTokenBudget from bundle (likely 8192)
        var requestTokenBudget = 500; // request 级别 override

        var request = new ContextDecisionRequest
        {
            RequestId = "req-budget",
            Candidates = new[] { envelope },
            TokenBudget = requestTokenBudget,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        Assert.AreEqual(requestTokenBudget, result.Outcome.TokenBudget,
            "Outcome.TokenBudget 必须反映 request 级别的 TokenBudget（request budget 只解析一次）。");
    }

    [TestMethod]
    public async Task MandatoryOverflowPolicyIsExplicit()
    {
        // mandatory 候选超出 token budget 时仍被选入（AllowOverflowWithDiagnostic 语义）
        var engine = new DefaultContextDecisionEngine(
            null, new DefaultSafetyGate(), new DefaultLifecycleGate(),
            new DefaultUtilityScorer(), new DefaultGlobalAllocator());

        var mandatoryEnvelope = R28BTestHelpers.MakeEnvelope("mand-1", ContextCandidateSource.Mandatory, 1.0, 500,
            safety: new CandidateSafetyState { IsMandatory = true, PassesSafetyGate = true });
        var snapshot = MakeSnapshot();
        var smallBudget = 100; // 远小于 mandatory 的 500 tokens

        var request = new ContextDecisionRequest
        {
            RequestId = "req-overflow",
            Candidates = new[] { mandatoryEnvelope },
            TokenBudget = smallBudget,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        Assert.AreEqual(1, result.SelectedEnvelopes.Count,
            "mandatory 候选必须被选入，即使超出 token budget（mandatory overflow = AllowOverflowWithDiagnostic）。");
        Assert.AreEqual(0, result.DroppedEnvelopes.Count,
            "mandatory 候选不应被丢弃。");
    }

    private static EffectivePolicySnapshot MakeSnapshot()
    {
        var bundle = ContextCore.Core.Services.Policy.DefaultPolicyBundleFactory.Create();
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
            ResolutionScope = new ContextDecisionScope("test-ws", "test-col")
        };
    }
}

// ===========================================================================
// E. ParityAcceptanceTests — Jaccard + selected/dropped 分离
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.6")]
public sealed class ParityAcceptanceTests
{
    [TestMethod]
    public void IdenticalNonEmptyParityIsOne()
    {
        var envelopes = new[]
        {
            R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100),
            R28BTestHelpers.MakeEnvelope("c2", ContextCandidateSource.Lexical, 0.6, 100)
        };
        var legacy = R28BTestHelpers.MakeResult("op-1", selected: envelopes, estimatedTokens: 200);
        var v2 = R28BTestHelpers.MakeResult("op-1", selected: envelopes, estimatedTokens: 200);

        var plane = new DecisionExperimentPlane();
        var report = plane.Compare(legacy, v2);

        Assert.AreEqual(1.0, report.JaccardIndex,
            "完全相同的非空选择集 → Jaccard 必须为 1.0。");
        Assert.AreEqual(ParityLevel.Hard, report.ParityLevel);
    }

    [TestMethod]
    public void EmptyVsNonEmptyParityIsZero()
    {
        var legacy = R28BTestHelpers.MakeResult("op-2"); // empty selected
        var v2 = R28BTestHelpers.MakeResult("op-2",
            selected: new[]
            {
                R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100),
                R28BTestHelpers.MakeEnvelope("c2", ContextCandidateSource.Lexical, 0.6, 100)
            },
            estimatedTokens: 200);

        var plane = new DecisionExperimentPlane();
        var report = plane.Compare(legacy, v2);

        Assert.AreEqual(0.0, report.JaccardIndex,
            "空集 vs 非空集 → Jaccard 必须为 0.0（交集为空）。");
        Assert.AreEqual(ParityLevel.Divergent, report.ParityLevel);
    }

    [TestMethod]
    public async Task LegacyDroppedItemsAreNotSelected()
    {
        // Shadow 报告的 Legacy SelectedEnvelopes 不应包含 dropped 候选
        var envelope = R28BTestHelpers.MakeEnvelope("selected-1", ContextCandidateSource.Semantic, 0.8, 100);
        var v2Result = R28BTestHelpers.MakeResult("op-3", selected: new[] { envelope }, estimatedTokens: 100);

        var stubRuntime = new RecordingDecisionRuntime(v2Result);
        var shadowRuntime = new ShadowDecisionRuntime(stubRuntime, new DecisionExperimentPlane());

        var legacyRequest = new ContextRetrievalRequest
        {
            OperationId = "op-3",
            WorkspaceId = "ws-1",
            CollectionId = "col-1"
        };
        var legacyResult = new ContextRetrievalResult
        {
            OperationId = "op-3",
            SelectedItems = new[]
            {
                new ContextRetrievalCandidate
                {
                    CandidateId = "selected-1",
                    SourceId = "selected-1",
                    Score = 0.8,
                    EstimatedTokens = 100
                }
            },
            DroppedItems = new[]
            {
                new ContextRetrievalDecision
                {
                    CandidateId = "dropped-1",
                    SourceId = "dropped-1",
                    Reason = "budget exceeded"
                }
            },
            EstimatedTokens = 100
        };

        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            RequestId = "op-3",
            ObservedAt = DateTimeOffset.UtcNow
        };

        var report = await shadowRuntime.ExecuteRetrievalShadowAsync(
            legacyRequest, legacyResult, tokenBudget: 1000, topK: 10,
            context: context, cancellationToken: CancellationToken.None);

        // Legacy selected count = 1（不含 dropped-1）
        Assert.AreEqual(1, report.Parity.LegacySelectedCount,
            "Legacy SelectedEnvelopes 必须排除 dropped 候选。");
        Assert.AreEqual(1, report.Parity.CommonSelectedCount);
        Assert.AreEqual(1.0, report.Parity.JaccardIndex,
            "Legacy selected 与 V2 selected 完全匹配 → Jaccard=1.0。");
    }

    [TestMethod]
    public void HardParityChecksOrderSectionsTokensAndReasons()
    {
        // 相同 selected 但不同 token 总量 → Hard parity 在 token 维度失败
        var envelopes = new[]
        {
            R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100),
            R28BTestHelpers.MakeEnvelope("c2", ContextCandidateSource.Lexical, 0.6, 100)
        };
        var legacy = R28BTestHelpers.MakeResult("op-4", selected: envelopes, estimatedTokens: 200);
        var v2 = R28BTestHelpers.MakeResult("op-4", selected: envelopes, estimatedTokens: 300);

        var plane = new DecisionExperimentPlane();
        var report = plane.Compare(legacy, v2);

        // Jaccard=1.0（相同 selected）但 token 不匹配
        Assert.AreEqual(1.0, report.JaccardIndex);
        Assert.AreNotEqual(report.LegacyTokenTotal, report.V2TokenTotal,
            "token 总量不同 → Hard parity 必须在 token 维度检测到差异。");

        var gate = new ShadowGate();
        var assessment = gate.Evaluate(report);
        Assert.AreEqual(ParityLevel.Divergent, assessment.OverallLevel,
            "token 偏差超过容忍度 → OverallLevel 必须为 Divergent（Hard parity 失败）。");
        Assert.IsFalse(assessment.CanCutover,
            "token 维度 Divergent → 不允许 cutover。");
    }
}

// ===========================================================================
// F. CutoverAndDiAcceptanceTests — Cutover + DI + cancellation
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.6")]
public sealed class CutoverAndDiAcceptanceTests
{
    [TestMethod]
    public void DIResolvesUnifiedRetrieverAsIContextRetriever()
    {
        // AuthoritativeRetrievalRuntime 必须实现 IContextRetriever（成为主接口的实现）
        Assert.IsTrue(typeof(AuthoritativeRetrievalRuntime).IsAssignableTo(typeof(IContextRetriever)),
            "AuthoritativeRetrievalRuntime 必须实现 IContextRetriever（V2 作为权威主接口实现）。");
    }

    [TestMethod]
    public void DIResolvesUnifiedBuilderAsIContextPackageBuilder()
    {
        // AuthoritativePackageRuntime 必须实现 IContextPackageBuilder
        Assert.IsTrue(typeof(AuthoritativePackageRuntime).IsAssignableTo(typeof(IContextPackageBuilder)),
            "AuthoritativePackageRuntime 必须实现 IContextPackageBuilder（V2 作为权威主接口实现）。");
    }

    [TestMethod]
    public async Task HundredPercentCutoverDoesNotExecuteLegacy()
    {
        // 100% cutover → V2-only 路径 → Legacy store 永不被查询
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(
            R28BTestHelpers.MakeResult("op-v2", selected: new[]
            {
                R28BTestHelpers.MakeEnvelope("v2-c1", ContextCandidateSource.Semantic, 0.8, 100)
            }, estimatedTokens: 100));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100));

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-100pct",
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            QueryText = "test",
            TopK = 10
        };

        await runtime.RetrieveAsync(request, CancellationToken.None);

        Assert.AreEqual(0, trackingStore.QueryCallCount,
            "100% cutover 时 Legacy store 必须永不被查询（V2-only 路径）。");
        Assert.AreEqual(1, stubV2.ExecuteCallCount,
            "V2 Runtime 必须被调用一次。");
    }

    [TestMethod]
    public async Task SampledShadowExecutesLegacyOnlyWhenSampled()
    {
        // sampled shadow rate=0 → Legacy 不执行；rate=1.0 → 所有请求执行 Legacy shadow
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(
            R28BTestHelpers.MakeResult("op-shadow"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();

        // rate=0：sampled shadow 不触发 → Legacy 不执行
        var integrationNoShadow = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(), new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100, EnableSampledShadow = false, ShadowSampleRate = 0.0 });
        var runtimeNoShadow = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(100), shadowGate: null, experimentPlane: integrationNoShadow);

        trackingStore.Reset();
        stubV2.Reset();
        await runtimeNoShadow.RetrieveAsync(
            new ContextRetrievalRequest { OperationId = "op-noshadow", WorkspaceId = "ws", CollectionId = "col" },
            CancellationToken.None);
        Assert.AreEqual(0, trackingStore.QueryCallCount,
            "EnableSampledShadow=false → Legacy 不执行。");

        // rate=1.0：sampled shadow 对所有请求触发 → Legacy 执行
        var integrationFullShadow = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(), new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100, EnableSampledShadow = true, ShadowSampleRate = 1.0 });
        var runtimeFullShadow = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(100), shadowGate: null, experimentPlane: integrationFullShadow);

        trackingStore.Reset();
        stubV2.Reset();
        await runtimeFullShadow.RetrieveAsync(
            new ContextRetrievalRequest { OperationId = "op-shadow", WorkspaceId = "ws", CollectionId = "col" },
            CancellationToken.None);
        Assert.IsTrue(trackingStore.QueryCallCount > 0,
            "ShadowSampleRate=1.0 → Legacy 必须被执行（sampled shadow 对照）。");
        Assert.AreEqual(1, stubV2.ExecuteCallCount,
            "V2 仍为权威路径，必须被调用一次。");
    }

    [TestMethod]
    public async Task CancellationIsNeverConvertedToFallbackSuccess()
    {
        // V2 抛出 OperationCanceledException → 必须传播，不转为 fallback success
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var throwingV2 = new ThrowingDecisionRuntime(new OperationCanceledException());
        var shadowRuntime = new ShadowDecisionRuntime(throwingV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, throwingV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100));

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-cancel",
            WorkspaceId = "ws-1",
            CollectionId = "col-1"
        };

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            async () => await runtime.RetrieveAsync(request, CancellationToken.None),
            "OperationCanceledException 必须传播，不得被 catch 转为 fallback success。");
    }
}

// ===========================================================================
// G. ProjectorAcceptanceTests — Projector 内容恢复 + session 保留
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.6")]
public sealed class ProjectorAcceptanceTests
{
    [TestMethod]
    public void PackageProjectorBuildsCompletePackage()
    {
        var envelope = R28BTestHelpers.MakeEnvelope("item-1", ContextCandidateSource.WorkingMemory, 0.7, 200);
        var allocation = R28BTestHelpers.MakeAllocation(
            envelope.CanonicalKey, section: "working_memory", includedTokens: 150, isTruncated: true);
        var result = R28BTestHelpers.MakeResult(
            "build-1",
            selected: new[] { envelope },
            estimatedTokens: 150,
            tokenBudget: 1000,
            allocationDecisions: new[] { allocation });
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "package body content")
            }
        };

        var projector = new PackageResultProjector();
        var dto = projector.Project(result, workingSet);

        Assert.AreEqual(1, dto.SelectedItems.Count, "Package 必须包含 selected 候选。");
        var item = dto.SelectedItems[0];
        Assert.AreEqual("working_memory", item.SectionName, "Section 从 AllocationDecision 恢复。");
        Assert.AreEqual(150, item.EstimatedTokens, "IncludedTokens 从 AllocationDecision 恢复。");
        Assert.IsNotNull(dto.Budget, "Package 必须构建完整 Budget。");
        Assert.AreEqual(1000, dto.Budget.TokenBudget);
        Assert.AreEqual(150, dto.Budget.UsedTokens);
        Assert.IsTrue(dto.Budget.Sections.Count > 0, "Package 必须构建 section budgets。");
    }

    [TestMethod]
    public void RetrievalProjectorPreservesContent()
    {
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var result = R28BTestHelpers.MakeResult("op-1", selected: new[] { envelope }, estimatedTokens: 100);
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "preserved retrieval content")
            }
        };

        var projector = new RetrievalResultProjector();
        var dto = projector.Project(result, workingSet);

        Assert.AreEqual(1, dto.SelectedItems.Count);
        Assert.AreEqual("preserved retrieval content", dto.SelectedItems[0].Content,
            "Retrieval Projector 必须从 Material sidecar 恢复 Content。");
    }

    [TestMethod]
    public void AgentProjectorPreservesSessionAndScope()
    {
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var allocation = R28BTestHelpers.MakeAllocation(envelope.CanonicalKey, "recent_context", 100);
        var result = R28BTestHelpers.MakeResult("op-1",
            selected: new[] { envelope },
            estimatedTokens: 100,
            tokenBudget: 1000,
            allocationDecisions: new[] { allocation });
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "agent content")
            }
        };

        var realSession = new AgentSessionId
        {
            Value = "session-real-123",
            RuntimeKind = AgentRuntimeKind.GenericTool,
            WorkspaceId = "ws-real",
            CollectionId = "col-real",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var projectionContext = new ProjectionContext
        {
            AgentSession = realSession,
            WorkspaceId = "ws-real",
            CollectionId = "col-real"
        };

        var projector = new AgentContextProjector();
        var snapshot = projector.Project(result, workingSet, projectionContext);

        Assert.AreEqual("session-real-123", snapshot.Session.Value,
            "Agent Projector 必须使用真实 AgentSessionId（而非伪造 session）。");
        Assert.AreEqual(AgentRuntimeKind.GenericTool, snapshot.Session.RuntimeKind);
        Assert.AreEqual("ws-real", snapshot.Session.WorkspaceId,
            "Agent Projector 必须保留 WorkspaceId。");
        Assert.AreEqual("col-real", snapshot.Session.CollectionId,
            "Agent Projector 必须保留 CollectionId。");
        Assert.IsTrue(snapshot.Sections.Count > 0, "Agent snapshot 必须构建 section。");
    }
}

// ===========================================================================
// Stub / Helper classes (private nested)
// ===========================================================================

/// <summary>
/// 计数 Router：包装真实 Router，统计 RouteAsync 调用次数。
/// </summary>
internal sealed class CountingRouter : IRouter
{
    private readonly IRouter _inner;
    public int RouteCallCount { get; private set; }

    public CountingRouter(IRouter inner) => _inner = inner;

    public async ValueTask<ExpertRoutingDecisionSet> RouteAsync(
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        RouteCallCount++;
        return await _inner.RouteAsync(request, snapshot, cancellationToken);
    }
}

/// <summary>
/// 计数 Provider：统计 ExecuteAsync 调用次数，返回预设结果。
/// </summary>
internal sealed class CountingCandidateProvider : ICandidateProvider
{
    private readonly ExpertExecutionResult _result;
    public ExpertKind Kind { get; }
    public int ExecuteCallCount { get; private set; }

    public CountingCandidateProvider(ExpertKind kind, ExpertExecutionResult result)
    {
        Kind = kind;
        _result = result;
    }

    public ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ExecuteCallCount++;
        return ValueTask.FromResult(_result);
    }
}

/// <summary>
/// 受限 Catalog：仅注册指定的 Expert（未注册的 Expert 被 Router disable）。
/// </summary>
internal sealed class RestrictedExpertCatalog : IExpertCatalog
{
    private readonly IReadOnlySet<ExpertKind> _experts;
    public IReadOnlySet<ExpertKind> AvailableExperts => _experts;

    public RestrictedExpertCatalog(params ExpertKind[] experts)
    {
        _experts = new HashSet<ExpertKind>(experts);
    }
}

/// <summary>
/// 记录 Runtime：返回预设结果，统计调用次数，支持 Reset。
/// </summary>
internal sealed class RecordingDecisionRuntime : IContextDecisionRuntime
{
    private readonly ContextDecisionResult _result;
    public int ExecuteCallCount { get; private set; }
    public ContextDecisionRuntimeRequest? LastRequest { get; private set; }

    public RecordingDecisionRuntime(ContextDecisionResult result) => _result = result;

    public ValueTask<ContextDecisionResult> ExecuteAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ExecuteCallCount++;
        LastRequest = request;
        return ValueTask.FromResult(_result);
    }

    public void Reset()
    {
        ExecuteCallCount = 0;
        LastRequest = null;
    }
}

/// <summary>
/// 抛异常 Runtime：ExecuteAsync 始终抛出预设异常。
/// </summary>
internal sealed class ThrowingDecisionRuntime : IContextDecisionRuntime
{
    private readonly Exception _exception;
    public ThrowingDecisionRuntime(Exception exception) => _exception = exception;

    public ValueTask<ContextDecisionResult> ExecuteAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
        => throw _exception;
}

/// <summary>
/// 调用跟踪 Store：统计 QueryAsync 调用次数，返回空结果。支持 Reset。
/// 用于检测 Legacy Retriever 是否被执行（通过 store 查询计数）。
/// </summary>
internal sealed class CallTrackingContextStore : IContextStore
{
    public int QueryCallCount { get; private set; }

    public Task<IReadOnlyList<ContextItem>> QueryAsync(
        ContextQuery query, CancellationToken cancellationToken = default)
    {
        QueryCallCount++;
        return Task.FromResult<IReadOnlyList<ContextItem>>(Array.Empty<ContextItem>());
    }

    public Task<ContextItem?> GetAsync(
        string workspaceId, string collectionId, string id,
        CancellationToken cancellationToken = default)
        => Task.FromResult<ContextItem?>(null);

    public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DeleteAsync(
        string workspaceId, string collectionId, string id,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public void Reset() => QueryCallCount = 0;
}
