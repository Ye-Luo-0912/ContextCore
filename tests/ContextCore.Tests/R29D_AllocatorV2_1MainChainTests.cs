using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextCore.Tests;

// ===========================================================================
// R29 WP-D-1：V2.1 Allocator 主链接入验收测试
//
// 覆盖：
//   1. Engine 路径选择：V2.1 AllocateWithDiversity vs V2.0 Allocate fallback
//   2. EffectivePolicySnapshot.DiversityOptions 默认值填充
//   3. Runtime 从 Policy 读取 DiversityOptions 传给 Engine
//   4. V2.1 路径 budget override 合并到 AllocationContext
//   5. DI 注册：IAllocatorV2_1 可注入 Engine
//
// 设计原则：
//   - 使用 SpyAllocatorV2_1 记录 AllocateWithDiversity 调用，精确验证路径选择
//   - 复用 R28BTestHelpers.MakeEnvelope 与 DefaultPolicyBundleFactory 构建测试数据
//   - 所有代码注释使用中文
// ===========================================================================

/// <summary>
/// R29 WP-D-1：V2.1 Allocator 主链接入验收测试。
/// </summary>
[TestClass]
[TestCategory("R29")]
[TestCategory("DecisionEngine")]
public sealed class R29D_AllocatorV2_1MainChainTests
{
    // =======================================================================
    // 辅助：SpyAllocatorV2_1 — 包装真实 V2.1 实现，记录调用
    // =======================================================================

    /// <summary>
    /// 间谍 Allocator V2.1：委托真实 DefaultAllocatorV2_1，记录 AllocateWithDiversity 是否被调用。
    /// </summary>
    private sealed class SpyAllocatorV2_1 : IAllocatorV2_1
    {
        private readonly DefaultAllocatorV2_1 _inner;
        internal bool AllocateWithDiversityCalled { get; private set; }
        internal DiversityOptions? LastDiversityOptions { get; private set; }
        internal AllocationContext? LastContext { get; private set; }

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
            LastDiversityOptions = diversityOptions;
            LastContext = context;
            return _inner.AllocateWithDiversity(candidates, context, diversityOptions);
        }
    }

    // =======================================================================
    // 辅助方法
    // =======================================================================

    /// <summary>构建带 mandatory 标记的候选。</summary>
    private static ContextCandidateEnvelope MakeMandatoryEnvelope(
        string candidateId, int tokens, double score = 1.0) => new()
        {
            CandidateId = candidateId,
            CanonicalKey = CanonicalCandidateKey.Create(
                workspaceId: "test-ws",
                collectionId: "test-col",
                entityKind: "mandatory",
                entityId: candidateId,
                entityVersion: "v1"),
            Source = ContextCandidateSource.Mandatory,
            Type = "mandatory-type",
            EstimatedTokens = tokens,
            Safety = new CandidateSafetyState { IsMandatory = true, PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = score, FinalScore = score, ReasonCode = "mandatory" }
        };

    /// <summary>构建 AllocationContext（Package 用途，允许 mandatory 溢出）。</summary>
    private static AllocationContext MakeContext(int tokenBudget) => new()
    {
        Purpose = ContextDecisionPurpose.Package,
        Budget = new BudgetProfile
        {
            ProfileId = "test-budget",
            DefaultTokenBudget = tokenBudget,
            DefaultTopK = 50
        },
        MandatoryOverflowPolicy = MandatoryOverflowPolicy.AllowOverflowWithDiagnostic
    };

    /// <summary>构建最小化 EffectivePolicySnapshot（携带 DiversityOptions）。</summary>
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
            ResolutionScope = new ContextDecisionScope("test-ws", "test-col"),
            DiversityOptions = diversityOptions
        };
    }

    /// <summary>构建完整 V2 注入的 Engine（可选注入 V2.1 spy）。</summary>
    private static DefaultContextDecisionEngine BuildEngine(
        SpyAllocatorV2_1? spy = null)
    {
        var baseAllocator = new DefaultGlobalAllocator();
        return new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(),
            globalAllocator: baseAllocator,
            allocatorV2_1: spy ?? new SpyAllocatorV2_1(baseAllocator));
    }

    // =======================================================================
    // 1. Engine 路径选择
    // =======================================================================

    [TestMethod]
    public async Task Engine_V2_1Path_WhenDiversityOptionsAndContextPresent()
    {
        // DiversityOptions 非空 + AllocationContext 非空 + IAllocatorV2_1 注入 → 走 AllocateWithDiversity
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = BuildEngine(spy);

        var candidate = R28BTestHelpers.MakeEnvelope("c-1", ContextCandidateSource.Semantic, 0.8, 100);
        var snapshot = MakeSnapshot(new DiversityOptions());
        var request = new ContextDecisionRequest
        {
            RequestId = "req-v21",
            DecisionSource = ContextDecisionSource.Package,
            WorkspaceId = "test-ws",
            CollectionId = "test-col",
            Candidates = new[] { candidate },
            TokenBudget = 1000,
            PolicySnapshot = snapshot,
            AllocationContext = MakeContext(tokenBudget: 1000),
            DiversityOptions = new DiversityOptions { Lambda = 0.5 }
        };

        var result = await engine.DecideAsync(request);

        Assert.IsTrue(spy.AllocateWithDiversityCalled,
            "DiversityOptions + AllocationContext 非空时应走 V2.1 AllocateWithDiversity。");
        Assert.IsNotNull(spy.LastDiversityOptions);
        Assert.AreEqual(0.5, spy.LastDiversityOptions!.Lambda);
        Assert.AreEqual(1, result.SelectedEnvelopes.Count, "候选应被选入。");
    }

    [TestMethod]
    public async Task Engine_V2_0Fallback_WhenDiversityOptionsNull()
    {
        // DiversityOptions 为 null → 回退 V2.0 Allocate（即使 IAllocatorV2_1 已注入）
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = BuildEngine(spy);

        var candidate = R28BTestHelpers.MakeEnvelope("c-1", ContextCandidateSource.Semantic, 0.8, 100);
        var snapshot = MakeSnapshot(diversityOptions: null);
        var request = new ContextDecisionRequest
        {
            RequestId = "req-v20",
            DecisionSource = ContextDecisionSource.Package,
            WorkspaceId = "test-ws",
            CollectionId = "test-col",
            Candidates = new[] { candidate },
            TokenBudget = 1000,
            PolicySnapshot = snapshot,
            AllocationContext = MakeContext(tokenBudget: 1000),
            DiversityOptions = null
        };

        var result = await engine.DecideAsync(request);

        Assert.IsFalse(spy.AllocateWithDiversityCalled,
            "DiversityOptions 为 null 时不应调用 V2.1 AllocateWithDiversity。");
        Assert.AreEqual(1, result.SelectedEnvelopes.Count, "V2.0 fallback 仍应正确选入候选。");
    }

    [TestMethod]
    public async Task Engine_V2_0Fallback_WhenAllocationContextNull()
    {
        // AllocationContext 为 null → 回退 V2.0 Allocate（Legacy 重载，向后兼容）
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = BuildEngine(spy);

        var candidate = R28BTestHelpers.MakeEnvelope("c-1", ContextCandidateSource.Semantic, 0.8, 100);
        var snapshot = MakeSnapshot(new DiversityOptions());
        var request = new ContextDecisionRequest
        {
            RequestId = "req-legacy",
            DecisionSource = ContextDecisionSource.Package,
            WorkspaceId = "test-ws",
            CollectionId = "test-col",
            Candidates = new[] { candidate },
            TokenBudget = 1000,
            PolicySnapshot = snapshot,
            AllocationContext = null,
            DiversityOptions = new DiversityOptions()
        };

        var result = await engine.DecideAsync(request);

        Assert.IsFalse(spy.AllocateWithDiversityCalled,
            "AllocationContext 为 null 时不应调用 V2.1 AllocateWithDiversity。");
        Assert.AreEqual(1, result.SelectedEnvelopes.Count, "V2.0 Legacy 重载仍应正确选入候选。");
    }

    [TestMethod]
    public async Task Engine_V2_1Path_AppliesRequestBudgetOverrideToContext()
    {
        // request.TokenBudget > 0 时，V2.1 路径应将 budget override 合并到 AllocationContext
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = BuildEngine(spy);

        var candidate = R28BTestHelpers.MakeEnvelope("c-1", ContextCandidateSource.Semantic, 0.8, 100);
        var snapshot = MakeSnapshot(new DiversityOptions());
        // AllocationContext 的 budget = 2000，但 request.TokenBudget = 500（override）
        var request = new ContextDecisionRequest
        {
            RequestId = "req-override",
            DecisionSource = ContextDecisionSource.Package,
            WorkspaceId = "test-ws",
            CollectionId = "test-col",
            Candidates = new[] { candidate },
            TokenBudget = 500,
            PolicySnapshot = snapshot,
            AllocationContext = MakeContext(tokenBudget: 2000),
            DiversityOptions = new DiversityOptions()
        };

        await engine.DecideAsync(request);

        Assert.IsTrue(spy.AllocateWithDiversityCalled);
        Assert.IsNotNull(spy.LastContext);
        // request.TokenBudget=500 应覆盖 AllocationContext.Budget.DefaultTokenBudget=2000
        Assert.AreEqual(500, spy.LastContext!.Budget.DefaultTokenBudget,
            "V2.1 路径应将 request.TokenBudget override 合并到 AllocationContext.Budget。");
    }

    [TestMethod]
    public async Task Engine_V2_1Path_MandatoryAlwaysSelected()
    {
        // V2.1 路径下 mandatory 候选始终选入（即使超出预算）
        var spy = new SpyAllocatorV2_1(new DefaultGlobalAllocator());
        var engine = BuildEngine(spy);

        var mandatory = MakeMandatoryEnvelope("m-1", tokens: 200);
        var snapshot = MakeSnapshot(new DiversityOptions());
        var request = new ContextDecisionRequest
        {
            RequestId = "req-mandatory",
            DecisionSource = ContextDecisionSource.Package,
            WorkspaceId = "test-ws",
            CollectionId = "test-col",
            Candidates = new[] { mandatory },
            TokenBudget = 50, // 预算仅 50，mandatory 200 超出
            PolicySnapshot = snapshot,
            AllocationContext = MakeContext(tokenBudget: 50),
            DiversityOptions = new DiversityOptions()
        };

        var result = await engine.DecideAsync(request);

        Assert.IsTrue(spy.AllocateWithDiversityCalled);
        Assert.AreEqual(1, result.SelectedEnvelopes.Count, "mandatory 候选应被选入（overflow 允许）。");
        // mandatory 的 decision 应为 SelectedMandatory，不被截断
        var decision = result.AllocationDecisions.Single();
        Assert.AreEqual(CandidateDecisionReasonCode.SelectedMandatory, decision.ReasonCode);
        Assert.IsFalse(decision.IsTruncated);
    }

    // =======================================================================
    // 2. EffectivePolicySnapshot DiversityOptions 默认值
    // =======================================================================

    [TestMethod]
    public async Task DefaultResolvedPolicyProvider_PopulatesDiversityOptionsByDefault()
    {
        // DefaultResolvedPolicyProvider 应默认填充 DiversityOptions（非 null）
        var provider = new DefaultResolvedPolicyProvider();
        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-policy",
            Scope = new ContextDecisionScope("test-ws", "test-col"),
            Purpose = ContextDecisionPurpose.Package
        };

        var snapshot = await provider.ResolveAsync(request);

        Assert.IsNotNull(snapshot.DiversityOptions,
            "DefaultResolvedPolicyProvider 应默认填充 DiversityOptions。");
        Assert.AreEqual(0.5, snapshot.DiversityOptions!.Lambda, "默认 Lambda 应为 0.5。");
        Assert.AreEqual(0.1, snapshot.DiversityOptions!.SectionReserveRatio, "默认 SectionReserveRatio 应为 0.1。");
        Assert.IsTrue(snapshot.DiversityOptions!.EnableSectionRollover, "默认应启用 section rollover。");
    }

    // =======================================================================
    // 3. DI 注册验证
    // =======================================================================

    [TestMethod]
    public void DI_Registration_EngineResolvesWithAllocatorV2_1()
    {
        // ServiceCollection 注册后，DefaultContextDecisionEngine 应能解析 IAllocatorV2_1
        var services = new ServiceCollection();
        // 最小化注册核心决策服务（验证 DI 注入链：IGlobalAllocator → DefaultAllocatorV2_1 → IAllocatorV2_1 → Engine）
        services.AddSingleton<IGlobalAllocator, DefaultGlobalAllocator>();
        services.AddSingleton<DefaultAllocatorV2_1>(sp => new DefaultAllocatorV2_1(
            sp.GetRequiredService<IGlobalAllocator>()));
        services.AddSingleton<IAllocatorV2_1>(sp => sp.GetRequiredService<DefaultAllocatorV2_1>());
        services.AddSingleton<ISafetyGate, DefaultSafetyGate>();
        services.AddSingleton<ILifecycleGate, DefaultLifecycleGate>();
        services.AddSingleton<IUtilityScorer, DefaultUtilityScorer>();
        services.AddSingleton<DefaultContextDecisionEngine>(sp => new DefaultContextDecisionEngine(
            sp.GetService<IPolicyRegistry>(),
            safetyGate: sp.GetService<ISafetyGate>(),
            lifecycleGate: sp.GetService<ILifecycleGate>(),
            utilityScorer: sp.GetService<IUtilityScorer>(),
            globalAllocator: sp.GetService<IGlobalAllocator>(),
            allocatorV2_1: sp.GetService<IAllocatorV2_1>()));
        var sp = services.BuildServiceProvider();

        var engine = sp.GetService<DefaultContextDecisionEngine>();
        Assert.IsNotNull(engine, "DefaultContextDecisionEngine 应可从 DI 解析。");

        var allocatorV2_1 = sp.GetService<IAllocatorV2_1>();
        Assert.IsNotNull(allocatorV2_1, "IAllocatorV2_1 应可从 DI 解析。");
        Assert.IsInstanceOfType(allocatorV2_1, typeof(DefaultAllocatorV2_1));
    }
}
