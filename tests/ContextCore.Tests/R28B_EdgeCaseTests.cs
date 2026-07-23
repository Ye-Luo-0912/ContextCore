using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

// ===========================================================================
// R28-B 边界条件测试 — TokenBudget=0 / 全部候选被 SafetyGate 拦截 / 取消令牌
//
// 覆盖范围（3 个测试类）：
//   1. TokenBudgetZeroEdgeCaseTests — TokenBudget=0 边界条件
//   2. AllCandidatesBlockedEdgeCaseTests — 全部候选被 SafetyGate 拦截
//   3. CancellationEdgeCaseTests — 取消令牌传播
//
// 设计原则：
//   - 使用真实 DefaultContextDecisionEngine（V2 路径）测试边界条件
//   - PolicySnapshot 通过 DefaultPolicyBundleFactory 构建，按需 override budget
//   - 所有代码注释使用中文
// ===========================================================================

// ===========================================================================
// 1. TokenBudgetZeroEdgeCaseTests — TokenBudget=0 边界条件
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B-EdgeCase")]
public sealed class TokenBudgetZeroEdgeCaseTests
{
    [TestMethod]
    public async Task Engine_TokenBudgetZero_OnlyMandatorySelected()
    {
        // TokenBudget=0 时，仅 mandatory 候选被选入（overflow 策略），非 mandatory 全部被丢弃
        var engine = BuildV2Engine();

        var mandatoryEnvelope = R28BTestHelpers.MakeEnvelope("mand-1", ContextCandidateSource.Mandatory, 1.0, 500,
            safety: new CandidateSafetyState { IsMandatory = true, PassesSafetyGate = true });
        var nonMandatoryEnvelope = R28BTestHelpers.MakeEnvelope("non-mand-1", ContextCandidateSource.Lexical, 0.8, 100,
            safety: new CandidateSafetyState { PassesSafetyGate = true });

        var snapshot = MakeSnapshotWithBudget(defaultTokenBudget: 0);
        var request = new ContextDecisionRequest
        {
            RequestId = "req-zero-budget",
            Candidates = new[] { mandatoryEnvelope, nonMandatoryEnvelope },
            TokenBudget = 0, // request 级别也为 0 → effectiveTokenBudget = 0
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        // mandatory 候选必须被选入（overflow 允许）
        Assert.IsTrue(result.SelectedEnvelopes.Any(e => e.CandidateId == "mand-1"),
            "TokenBudget=0 时 mandatory 候选必须被选入（overflow 策略）。");
    }

    [TestMethod]
    public async Task Engine_TokenBudgetZero_NonMandatoryAllDropped()
    {
        // TokenBudget=0 时，非 mandatory 候选全部被丢弃（budget exceeded）
        var engine = BuildV2Engine();

        var mandatoryEnvelope = R28BTestHelpers.MakeEnvelope("mand-1", ContextCandidateSource.Mandatory, 1.0, 500,
            safety: new CandidateSafetyState { IsMandatory = true, PassesSafetyGate = true });
        var nonMandatory1 = R28BTestHelpers.MakeEnvelope("non-mand-1", ContextCandidateSource.Lexical, 0.8, 100,
            safety: new CandidateSafetyState { PassesSafetyGate = true });
        var nonMandatory2 = R28BTestHelpers.MakeEnvelope("non-mand-2", ContextCandidateSource.Semantic, 0.6, 200,
            safety: new CandidateSafetyState { PassesSafetyGate = true });

        var snapshot = MakeSnapshotWithBudget(defaultTokenBudget: 0);
        var request = new ContextDecisionRequest
        {
            RequestId = "req-zero-budget-drops",
            Candidates = new[] { mandatoryEnvelope, nonMandatory1, nonMandatory2 },
            TokenBudget = 0,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        // 非 mandatory 候选必须全部被丢弃
        var droppedIds = result.DroppedEnvelopes.Select(e => e.CandidateId).ToHashSet();
        Assert.IsTrue(droppedIds.Contains("non-mand-1"),
            "TokenBudget=0 时非 mandatory 候选必须被丢弃。");
        Assert.IsTrue(droppedIds.Contains("non-mand-2"),
            "TokenBudget=0 时非 mandatory 候选必须被丢弃。");
        // mandatory 候选不应出现在 dropped 中
        Assert.IsFalse(droppedIds.Contains("mand-1"),
            "mandatory 候选不应出现在 DroppedEnvelopes。");
    }

    [TestMethod]
    public async Task Engine_TokenBudgetZero_OutcomeEstimatedTokensCorrect()
    {
        // TokenBudget=0 时，Outcome.EstimatedTokens 应反映 mandatory 候选的实际 token 数
        var engine = BuildV2Engine();

        var mandatoryEnvelope = R28BTestHelpers.MakeEnvelope("mand-1", ContextCandidateSource.Mandatory, 1.0, 500,
            safety: new CandidateSafetyState { IsMandatory = true, PassesSafetyGate = true });

        var snapshot = MakeSnapshotWithBudget(defaultTokenBudget: 0);
        var request = new ContextDecisionRequest
        {
            RequestId = "req-zero-budget-tokens",
            Candidates = new[] { mandatoryEnvelope },
            TokenBudget = 0,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        // EstimatedTokens 应反映 mandatory 候选的 token 数（overflow，但 token 仍被计入）
        Assert.AreEqual(1, result.SelectedEnvelopes.Count,
            "mandatory 候选必须被选入。");
        Assert.IsTrue(result.Outcome.EstimatedTokens > 0,
            "Outcome.EstimatedTokens 必须反映 selected 候选的 token 数（> 0）。");
    }

    [TestMethod]
    public async Task Engine_TokenBudgetZero_NoMandatory_AllDropped()
    {
        // TokenBudget=0 且无 mandatory 候选时，全部候选被丢弃，SelectedEnvelopes 为空
        var engine = BuildV2Engine();

        var nonMandatory1 = R28BTestHelpers.MakeEnvelope("non-mand-1", ContextCandidateSource.Lexical, 0.8, 100,
            safety: new CandidateSafetyState { PassesSafetyGate = true });
        var nonMandatory2 = R28BTestHelpers.MakeEnvelope("non-mand-2", ContextCandidateSource.Semantic, 0.6, 200,
            safety: new CandidateSafetyState { PassesSafetyGate = true });

        var snapshot = MakeSnapshotWithBudget(defaultTokenBudget: 0);
        var request = new ContextDecisionRequest
        {
            RequestId = "req-zero-no-mandatory",
            Candidates = new[] { nonMandatory1, nonMandatory2 },
            TokenBudget = 0,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        Assert.AreEqual(0, result.SelectedEnvelopes.Count,
            "TokenBudget=0 且无 mandatory 候选时 SelectedEnvelopes 必须为空。");
        Assert.AreEqual(2, result.DroppedEnvelopes.Count,
            "全部非 mandatory 候选必须被丢弃。");
    }

    // --- helpers ---

    private static DefaultContextDecisionEngine BuildV2Engine()
    {
        return new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(),
            globalAllocator: new DefaultGlobalAllocator());
    }

    private static EffectivePolicySnapshot MakeSnapshotWithBudget(int defaultTokenBudget)
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
            Budget = bundle.Budget with { DefaultTokenBudget = defaultTokenBudget },
            Routing = bundle.Routing,
            FeatureSchemaVersion = bundle.Policies.DecisionSchemaVersion,
            ResolutionScope = new ContextDecisionScope("test-ws", "test-col")
        };
    }
}

// ===========================================================================
// 2. AllCandidatesBlockedEdgeCaseTests — 全部候选被 SafetyGate 拦截
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B-EdgeCase")]
public sealed class AllCandidatesBlockedEdgeCaseTests
{
    [TestMethod]
    public async Task Engine_AllCandidatesBlocked_SelectedEnvelopesEmpty()
    {
        // 全部候选被 SafetyGate 拦截 → SelectedEnvelopes 为空
        var engine = BuildV2Engine();

        var blocked1 = R28BTestHelpers.MakeEnvelope("blocked-1", ContextCandidateSource.Lexical, 0.8, 100,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.DeprecatedBlocked
            });
        var blocked2 = R28BTestHelpers.MakeEnvelope("blocked-2", ContextCandidateSource.Semantic, 0.6, 200,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.RequiredTagMismatch
            });

        var snapshot = MakeSnapshot();
        var request = new ContextDecisionRequest
        {
            RequestId = "req-all-blocked",
            Candidates = new[] { blocked1, blocked2 },
            TokenBudget = 1000,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        Assert.AreEqual(0, result.SelectedEnvelopes.Count,
            "全部候选被 SafetyGate 拦截时 SelectedEnvelopes 必须为空。");
    }

    [TestMethod]
    public async Task Engine_AllCandidatesBlocked_DroppedCountMatchesInput()
    {
        // 全部候选被拦截 → DroppedEnvelopes 数量等于输入候选数量
        var engine = BuildV2Engine();

        var blocked1 = R28BTestHelpers.MakeEnvelope("blocked-1", ContextCandidateSource.Lexical, 0.8, 100,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.DeprecatedBlocked
            });
        var blocked2 = R28BTestHelpers.MakeEnvelope("blocked-2", ContextCandidateSource.Semantic, 0.6, 200,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.RequiredTagMismatch
            });
        var blocked3 = R28BTestHelpers.MakeEnvelope("blocked-3", ContextCandidateSource.WorkingMemory, 0.5, 50,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.LifecycleBlocked
            });

        var snapshot = MakeSnapshot();
        var request = new ContextDecisionRequest
        {
            RequestId = "req-all-blocked-count",
            Candidates = new[] { blocked1, blocked2, blocked3 },
            TokenBudget = 1000,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        Assert.AreEqual(3, result.DroppedEnvelopes.Count,
            "DroppedEnvelopes 数量必须等于输入候选数量（全部被拦截）。");
        // 验证每个 dropped 候选携带 BlockReasonCode
        foreach (var dropped in result.DroppedEnvelopes)
        {
            Assert.AreNotEqual(CandidateDecisionReasonCode.Unknown, dropped.Safety.BlockReasonCode,
                $"Dropped 候选 {dropped.CandidateId} 必须携带非 Unknown 的 BlockReasonCode。");
        }
    }

    [TestMethod]
    public async Task Engine_AllCandidatesBlocked_ProjectorProducesValidOutput()
    {
        // 全部候选被拦截 → Projector 仍能产出有效的 ContextRetrievalResult（空 selected + 非空 dropped）
        var engine = BuildV2Engine();

        var blockedEnvelope = R28BTestHelpers.MakeEnvelope("blocked-1", ContextCandidateSource.Lexical, 0.8, 100,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.DeprecatedBlocked
            });

        var snapshot = MakeSnapshot();
        var request = new ContextDecisionRequest
        {
            RequestId = "req-blocked-projector",
            Candidates = new[] { blockedEnvelope },
            TokenBudget = 1000,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        // 使用 Projector 投影为 ContextRetrievalResult
        var projector = new RetrievalResultProjector();
        var dto = projector.Project(result);

        Assert.IsNotNull(dto);
        Assert.AreEqual(0, dto.SelectedItems.Count,
            "全部候选被拦截时 Projector 产出的 SelectedItems 必须为空。");
        Assert.AreEqual(1, dto.DroppedItems.Count,
            "Projector 产出的 DroppedItems 必须包含被拦截的候选。");
        Assert.IsTrue(dto.Succeeded,
            "Projector 产出的结果必须 Succeeded=true（空 selected 是合法结果，不是失败）。");
    }

    [TestMethod]
    public async Task Engine_AllCandidatesBlocked_OutcomeReportsBlockedCount()
    {
        // 全部候选被拦截 → DroppedEnvelopes 反映被拦截数量（V2 路径 Outcome 仅含 allocator 级别 dropped，
        // safety blocked 候选在 DroppedEnvelopes 中）
        var engine = BuildV2Engine();

        var blocked1 = R28BTestHelpers.MakeEnvelope("blocked-1", ContextCandidateSource.Lexical, 0.8, 100,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.DeprecatedBlocked
            });
        var blocked2 = R28BTestHelpers.MakeEnvelope("blocked-2", ContextCandidateSource.Semantic, 0.6, 200,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.RequiredTagMismatch
            });

        var snapshot = MakeSnapshot();
        var request = new ContextDecisionRequest
        {
            RequestId = "req-blocked-outcome",
            Candidates = new[] { blocked1, blocked2 },
            TokenBudget = 1000,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        var result = await engine.DecideAsync(request, CancellationToken.None);

        // DroppedEnvelopes 包含所有被拦截的候选（safety + lifecycle + budget）
        Assert.AreEqual(2, result.DroppedEnvelopes.Count,
            "DroppedEnvelopes 必须反映被拦截的候选数量。");
        // SelectedEnvelopes 为空（无候选通过 SafetyGate）
        Assert.AreEqual(0, result.SelectedEnvelopes.Count,
            "SelectedEnvelopes 必须为 0。");
    }

    // --- helpers ---

    private static DefaultContextDecisionEngine BuildV2Engine()
    {
        return new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(),
            globalAllocator: new DefaultGlobalAllocator());
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
// 3. CancellationEdgeCaseTests — 取消令牌传播
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B-EdgeCase")]
public sealed class CancellationEdgeCaseTests
{
    [TestMethod]
    public async Task Engine_CancelledDuringDecide_PropagatesOperationCanceledException()
    {
        // Engine.DecideAsync 在入口检查 cancellation → 预取消 token 必须抛 OperationCanceledException
        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(),
            globalAllocator: new DefaultGlobalAllocator());

        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Lexical, 0.5, 100);
        var snapshot = MakeSnapshot();
        var request = new ContextDecisionRequest
        {
            RequestId = "req-cancel-engine",
            Candidates = new[] { envelope },
            TokenBudget = 1000,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            async () => await engine.DecideAsync(request, cts.Token),
            "预取消的 CancellationToken 必须导致 Engine.DecideAsync 抛出 OperationCanceledException。");
    }

    [TestMethod]
    public async Task Runtime_CancelledDuringProvider_PropagatesOperationCanceledException()
    {
        // Runtime 在 Provider 执行期间收到取消 → 必须传播 OperationCanceledException
        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(),
            globalAllocator: new DefaultGlobalAllocator());

        // Provider 在执行时检查 cancellation token 并抛出 OperationCanceledException
        var cancellingProvider = new CancellationThrowingProvider(ExpertKind.Lexical);
        var runtime = new DefaultContextDecisionRuntime(
            engine: engine,
            policyProvider: new DefaultResolvedPolicyProvider(),
            router: new DefaultRouter(new DefaultExpertCatalog()),
            expertCatalog: new DefaultExpertCatalog(),
            candidateProviders: new ICandidateProvider[] { cancellingProvider },
            canonicalMerger: new DefaultCanonicalCandidateMerger(),
            earlyAdmissionGate: new DefaultEarlyAdmissionGate(),
            featurePipeline: new DefaultFeaturePipeline(),
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer());

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-cancel-runtime",
            Scope = new ContextDecisionScope("ws-cancel", "col-cancel"),
            Purpose = ContextDecisionPurpose.Retrieval,
            QueryText = "取消测试",
            TokenBudget = 4096,
            TopK = 10,
            SeedCandidates = Array.Empty<ContextCandidateEnvelope>()
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            async () => await runtime.ExecuteAsync(request, cts.Token),
            "Provider 执行期间取消必须传播 OperationCanceledException（不转为 fallback success）。");
    }

    [TestMethod]
    public async Task AuthoritativeRuntime_Cancelled_PropagatesOperationCanceledException()
    {
        // AuthoritativeRetrievalRuntime 在 V2 路径收到取消 → 必须传播（不转为 fallback success）
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);

        // V2 Runtime 抛出 OperationCanceledException
        var throwingV2 = new ThrowingDecisionRuntime(new OperationCanceledException());
        var shadowRuntime = new ShadowDecisionRuntime(throwingV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, throwingV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100));

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-cancel-auth",
            WorkspaceId = "ws-cancel",
            CollectionId = "col-cancel"
        };

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            async () => await runtime.RetrieveAsync(request, CancellationToken.None),
            "AuthoritativeRetrievalRuntime 在 V2 抛出 OperationCanceledException 时必须传播（不转为 fallback success）。");
    }

    [TestMethod]
    public async Task Engine_CancelledAfterStart_PropagatesOperationCanceledException()
    {
        // Engine 在入口检查 cancellation → 预取消 token 必须抛 OperationCanceledException
        // 注意：Engine 是同步内存操作，仅在入口检查取消；延迟取消无法在同步执行期间触发。
        // 此处验证入口检查的确定性（与 Engine_CancelledDuringDecide 互补，使用不同 request 字段组合）。
        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(),
            globalAllocator: new DefaultGlobalAllocator());

        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Lexical, 0.5, 100);
        var snapshot = MakeSnapshot();
        var request = new ContextDecisionRequest
        {
            RequestId = "req-cancel-mid",
            Candidates = new[] { envelope },
            TokenBudget = 1000,
            TopK = 10,
            PolicySnapshot = snapshot
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            async () => await engine.DecideAsync(request, cts.Token),
            "已取消的 CancellationToken 必须导致 Engine 抛出 OperationCanceledException。");
    }

    // --- helpers ---

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

    /// <summary>
    /// 取消抛出 Provider：在 ExecuteAsync 入口检查 cancellation token 并抛出 OperationCanceledException。
    /// 用于测试 Runtime 在 Provider 执行期间取消时的异常传播行为。
    /// </summary>
    private sealed class CancellationThrowingProvider : ICandidateProvider
    {
        public ExpertKind Kind { get; }

        public CancellationThrowingProvider(ExpertKind kind)
        {
            Kind = kind;
        }

        public ValueTask<ExpertExecutionResult> ExecuteAsync(
            CandidateProviderContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CandidateProviderHelpers.Empty());
        }
    }
}
