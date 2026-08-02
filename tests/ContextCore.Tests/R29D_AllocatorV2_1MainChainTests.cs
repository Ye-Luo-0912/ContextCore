using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextCore.Tests;

// ===========================================================================
// Allocator 主链接入验收测试
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
/// Allocator 主链接入验收测试。
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
    /// 间谍 Allocator 委托真实 DefaultAllocatorV2_1，记录 AllocateWithDiversity 是否被调用。
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
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
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
        // 路径下 mandatory 候选始终选入（即使超出预算）
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

// ===========================================================================
// MandatoryOverflowPolicy 接入 V2.1 Allocator 验收测试
//
// 覆盖：
//   1. FailClosed + mandatory 超预算 → 抛 MandatoryContextWindowExceededException
//   2. RejectLowestAuthorityMandatory + mandatory 超预算 → 最低优先级被拒绝
//   3. AllowOverflowWithDiagnostic + mandatory 超预算 → 全部选入（回归）
//   4. 诊断字段验证（MandatoryOverflowTokens / HardWindowViolated / Policy）
//
// 设计原则：
//   - 直接测试 DefaultAllocatorV2_1.AllocateWithDiversity（单元级，不经 Engine）
//   - 复用 MakeMandatoryEnvelope / MakeNonMandatoryEnvelope helper
//   - 所有代码注释使用中文
// ===========================================================================

/// <summary>
/// MandatoryOverflowPolicy 接入 V2.1 Allocator 验收测试。
/// </summary>
[TestClass]
[TestCategory("R29")]
[TestCategory("DecisionEngine")]
public sealed class R29D_MandatoryOverflowPolicyTests
{
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

    /// <summary>构建 AllocationContext（携带指定的 MandatoryOverflowPolicy）。</summary>
    private static AllocationContext MakeContext(
        int tokenBudget,
        MandatoryOverflowPolicy policy,
        ContextDecisionPurpose purpose = ContextDecisionPurpose.Package) => new()
        {
            Purpose = purpose,
            Budget = new BudgetProfile
            {
                ProfileId = "test-budget",
                DefaultTokenBudget = tokenBudget,
                DefaultTopK = 50
            },
            MandatoryOverflowPolicy = policy
        };

    /// <summary>构建 V2.1 分配器实例。</summary>
    private static DefaultAllocatorV2_1 MakeAllocator() => new(new DefaultGlobalAllocator());

    // =======================================================================
    // 1. FailClosed
    // =======================================================================

    [TestMethod]
    public void FailClosed_MandatoryExceedsBudget_ThrowsException()
    {
        // FailClosed + mandatory 总 token 超出预算 → 抛 MandatoryContextWindowExceededException
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 100, policy: MandatoryOverflowPolicy.FailClosed);
        var options = new DiversityOptions();

        var m1 = MakeMandatoryEnvelope("m-1", tokens: 80, score: 0.9);
        var m2 = MakeMandatoryEnvelope("m-2", tokens: 50, score: 0.5); // 总 130 > 预算 100

        var ex = Assert.ThrowsException<MandatoryContextWindowExceededException>(() =>
            allocator.AllocateWithDiversity(new[] { m1, m2 }, context, options));

        Assert.AreEqual(130, ex.MandatoryTokens, "异常应携带 mandatory 总 token 需求。");
        Assert.AreEqual(100, ex.BudgetLimit, "异常应携带预算上限。");
        Assert.AreEqual(2, ex.OverflowedCandidateIds.Count, "异常应列出所有溢出 mandatory 候选 ID。");
        CollectionAssert.AreEquivalent(new[] { "m-1", "m-2" }, ex.OverflowedCandidateIds.ToList());
    }

    [TestMethod]
    public void FailClosed_MandatoryWithinBudget_NoException()
    {
        // FailClosed + mandatory 总 token 在预算内 → 正常返回，不抛异常
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 200, policy: MandatoryOverflowPolicy.FailClosed);
        var options = new DiversityOptions();

        var m1 = MakeMandatoryEnvelope("m-1", tokens: 80, score: 0.9);
        var m2 = MakeMandatoryEnvelope("m-2", tokens: 50, score: 0.5); // 总 130 < 预算 200

        var result = allocator.AllocateWithDiversity(new[] { m1, m2 }, context, options);

        Assert.AreEqual(2, result.Selected.Count, "mandatory 在预算内应全部选入。");
        Assert.IsFalse(result.Outcome.Diagnostics.ContainsKey("MandatoryOverflowTokens"),
            "无溢出时不应记录 MandatoryOverflowTokens 诊断。");
    }

    // =======================================================================
    // 2. RejectLowestAuthorityMandatory
    // =======================================================================

    [TestMethod]
    public void RejectLowestAuthorityMandatory_RejectsLowestScoreFirst()
    {
        // RejectLowestAuthorityMandatory + mandatory 超预算 → 按 FinalScore 升序拒绝最低优先级
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 100, policy: MandatoryOverflowPolicy.RejectLowestAuthorityMandatory);
        var options = new DiversityOptions { Lambda = 1.0 }; // 纯 relevance，禁用 MMR

        var m1 = MakeMandatoryEnvelope("m-high", tokens: 60, score: 0.9); // 高优先级
        var m2 = MakeMandatoryEnvelope("m-low", tokens: 60, score: 0.3);  // 低优先级，总 120 > 100

        var result = allocator.AllocateWithDiversity(new[] { m1, m2 }, context, options);

        // m-low（FinalScore=0.3）应被拒绝，m-high（FinalScore=0.9）应保留
        Assert.AreEqual(1, result.Selected.Count, "应只保留 1 个 mandatory（预算仅够 1 个）。");
        Assert.IsTrue(result.Selected.Any(e => e.CandidateId == "m-high"),
            "高优先级 mandatory 应被保留。");
        Assert.AreEqual(1, result.Dropped.Count, "应拒绝 1 个 mandatory。");
        Assert.IsTrue(result.Dropped.Any(e => e.CandidateId == "m-low"),
            "低优先级 mandatory 应被拒绝。");

        // 被拒绝候选的 decision 应为 TokenBudgetExceeded
        var rejectedDecision = result.AllocationDecisions.First(d => d.CandidateKey == m2.CanonicalKey);
        Assert.AreEqual(CandidateDecisionReasonCode.TokenBudgetExceeded, rejectedDecision.ReasonCode);
        Assert.AreEqual(0, rejectedDecision.IncludedTokens);
    }

    [TestMethod]
    public void RejectLowestAuthorityMandatory_DiagnosticsRecorded()
    {
        // RejectLowestAuthorityMandatory 路径应记录诊断字段
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 50, policy: MandatoryOverflowPolicy.RejectLowestAuthorityMandatory);
        var options = new DiversityOptions();

        var m1 = MakeMandatoryEnvelope("m-1", tokens: 40, score: 0.9);
        var m2 = MakeMandatoryEnvelope("m-2", tokens: 40, score: 0.5); // 总 80 > 预算 50

        var result = allocator.AllocateWithDiversity(new[] { m1, m2 }, context, options);

        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("MandatoryOverflowPolicy"),
            "应记录 MandatoryOverflowPolicy 诊断。");
        Assert.AreEqual("RejectLowestAuthorityMandatory", result.Outcome.Diagnostics["MandatoryOverflowPolicy"]);
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("HardWindowViolated"),
            "应记录 HardWindowViolated 诊断。");
        Assert.AreEqual("true", result.Outcome.Diagnostics["HardWindowViolated"]);
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("Purpose"),
            "应记录 Purpose 诊断。");
    }

    // =======================================================================
    // 3. AllowOverflowWithDiagnostic（回归测试）
    // =======================================================================

    [TestMethod]
    public void AllowOverflowWithDiagnostic_MandatoryAlwaysSelected_RecordsDiagnostic()
    {
        // AllowOverflowWithDiagnostic + mandatory 超预算 → 全部选入（当前行为不变），记录诊断
        var allocator = MakeAllocator();
        var context = MakeContext(tokenBudget: 100, policy: MandatoryOverflowPolicy.AllowOverflowWithDiagnostic);
        var options = new DiversityOptions();

        var m1 = MakeMandatoryEnvelope("m-1", tokens: 80, score: 0.9);
        var m2 = MakeMandatoryEnvelope("m-2", tokens: 50, score: 0.5); // 总 130 > 预算 100

        var result = allocator.AllocateWithDiversity(new[] { m1, m2 }, context, options);

        Assert.AreEqual(2, result.Selected.Count, "AllowOverflowWithDiagnostic 应全部选入 mandatory 候选。");
        Assert.AreEqual(0, result.Dropped.Count, "无 mandatory 应被丢弃。");
        // 诊断应记录溢出量
        Assert.IsTrue(result.Outcome.Diagnostics.ContainsKey("MandatoryOverflowTokens"),
            "应记录 MandatoryOverflowTokens 诊断。");
        Assert.AreEqual("30", result.Outcome.Diagnostics["MandatoryOverflowTokens"],
            "溢出量应为 130-100=30。");
        Assert.AreEqual("AllowOverflowWithDiagnostic", result.Outcome.Diagnostics["MandatoryOverflowPolicy"]);
        Assert.AreEqual("false", result.Outcome.Diagnostics["HardWindowViolated"],
            "AllowOverflowWithDiagnostic 不应触发 HardWindowViolated。");
    }
}

// ===========================================================================
// TokenCost 权威化验收测试
//
// 覆盖：
//   1. Engine Legacy 路径使用 TokenCost.ContentTokens（而非 EstimatedTokens）
//   2. ContextDecisionOutcomeSummary.EffectiveTokens 为权威字段
//   3. EstimatedTokens 别名向后兼容（委托到 EffectiveTokens）
//
// 设计原则：
//   - 测试 Legacy 路径（无 PolicySnapshot → 走静态内联分配）
//   - 构造带 TokenCost 的 envelope，验证 Engine 用精确 token 做预算检查
//   - 所有代码注释使用中文
// ===========================================================================

/// <summary>
/// TokenCost 权威化验收测试。
/// </summary>
[TestClass]
[TestCategory("R29")]
[TestCategory("DecisionEngine")]
public sealed class R29D_TokenCostAuthorityTests
{
    /// <summary>构建带 TokenCost 的候选（精确 token，不同于 EstimatedTokens）。</summary>
    private static ContextCandidateEnvelope MakeEnvelopeWithTokenCost(
        string candidateId,
        int estimatedTokens,
        int preciseTokens,
        double score = 0.8) => new()
        {
            CandidateId = candidateId,
            CanonicalKey = CanonicalCandidateKey.Create(
                workspaceId: "test-ws",
                collectionId: "test-col",
                entityKind: "test-entity",
                entityId: candidateId,
                entityVersion: "v1"),
            Source = ContextCandidateSource.Semantic,
            Type = "test-type",
            #pragma warning disable CS0618 // EstimatedTokens 已标记 [Obsolete]，测试中仍需设置 fallback 值
            EstimatedTokens = estimatedTokens,
            #pragma warning restore CS0618
            Safety = new CandidateSafetyState { PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = score, FinalScore = score, ReasonCode = "test" },
            TokenCost = new CandidateTokenCost
            {
                ContentTokens = preciseTokens,
                TokenizerId = "test-tokenizer",
                IsEstimated = false
            }
        };

    [TestMethod]
    public async Task Engine_LegacyPath_UsesTokenCost_OverEstimatedTokens()
    {
        // Legacy 路径（无 PolicySnapshot）应使用 TokenCost.ContentTokens 做预算检查，
        // 而非 EstimatedTokens（length/4 粗估）。
        // 候选 EstimatedTokens=50（粗估）但 TokenCost.ContentTokens=200（精确），
        // 预算=100：若用 EstimatedTokens 则会被选入（50<100），若用 TokenCost 则被丢弃（200>100）。
        var engine = new DefaultContextDecisionEngine();

        var candidate = MakeEnvelopeWithTokenCost("c-1", estimatedTokens: 50, preciseTokens: 200, score: 0.9);
        var request = new ContextDecisionRequest
        {
            RequestId = "req-token-cost",
            DecisionSource = ContextDecisionSource.Retrieval,
            WorkspaceId = "test-ws",
            CollectionId = "test-col",
            Candidates = new[] { candidate },
            TokenBudget = 100
        };

        var result = await engine.DecideAsync(request);

        // 候选精确 token=200 > 预算=100，应被丢弃（TokenBudgetExceeded）
        Assert.AreEqual(0, result.SelectedEnvelopes.Count,
            "TokenCost.ContentTokens=200 > budget=100，候选应被丢弃。");
        Assert.AreEqual(1, result.DroppedEnvelopes.Count,
            "候选应因 token 预算超限被丢弃。");
        Assert.AreEqual(CandidateDecisionReasonCode.TokenBudgetExceeded,
            result.DroppedEnvelopes[0].Safety.BlockReasonCode);
    }

    [TestMethod]
    public async Task Engine_LegacyPath_TokenCostNull_FallsBackToEstimatedTokens()
    {
        // TokenCost=null 时回退到 EstimatedTokens（向后兼容 Legacy 候选）
        var engine = new DefaultContextDecisionEngine();

        var candidate = R28BTestHelpers.MakeEnvelope("c-1", ContextCandidateSource.Semantic, 0.9, 50);
        // TokenCost 未设置（null）→ 回退到 EstimatedTokens=50
        var request = new ContextDecisionRequest
        {
            RequestId = "req-fallback",
            DecisionSource = ContextDecisionSource.Retrieval,
            WorkspaceId = "test-ws",
            CollectionId = "test-col",
            Candidates = new[] { candidate },
            TokenBudget = 100
        };

        var result = await engine.DecideAsync(request);

        // EstimatedTokens=50 < 预算=100，应被选入
        Assert.AreEqual(1, result.SelectedEnvelopes.Count,
            "TokenCost=null 时回退到 EstimatedTokens=50 < budget=100，候选应被选入。");
    }

    [TestMethod]
    public void OutcomeSummary_EffectiveTokens_IsAuthoritativeField()
    {
        // ContextDecisionOutcomeSummary.EffectiveTokens 是权威字段
        var summary = new ContextDecisionOutcomeSummary
        {
            SelectedCount = 3,
            EffectiveTokens = 150
        };

        Assert.AreEqual(150, summary.EffectiveTokens,
            "EffectiveTokens 应为设置的值。");
        #pragma warning disable CS0618 // 测试 EstimatedTokens 别名向后兼容
        Assert.AreEqual(150, summary.EstimatedTokens,
            "EstimatedTokens 别名应委托到 EffectiveTokens。");
        #pragma warning restore CS0618
    }

    [TestMethod]
    public void OutcomeSummary_EstimatedTokens_Init_Sets_EffectiveTokens()
    {
        // 通过 EstimatedTokens init 设置的值应委托到 EffectiveTokens（向后兼容）
        #pragma warning disable CS0618 // 测试 init 别名向后兼容
        var summary = new ContextDecisionOutcomeSummary
        {
            SelectedCount = 1,
            EstimatedTokens = 200
        };
        #pragma warning restore CS0618

        Assert.AreEqual(200, summary.EffectiveTokens,
            "通过 EstimatedTokens init 设置的值应委托到 EffectiveTokens。");
    }
}

// ===========================================================================
// Provider EnrichTokenCost fail-fast 验收测试
//
// 覆盖：
//   1. Provider 未注入 tokenizer + 非空内容 → 抛 InvalidOperationException
//   2. Provider 注入 tokenizer + 非空内容 → 正常产出 envelope（TokenCost 已填充）
//   3. Provider 未注入 tokenizer + IncludeContent=false（空内容）→ 正常产出（无需 tokenize）
//   4. 多 Provider 类型覆盖（Mandatory / Lexical）
//
// 设计原则：
//   - 复用 FixedItemContextStore 作为 mock store，控制 item.Content
//   - 使用 DefaultContextTokenizerResolver 作为可用 tokenizer
//   - 所有代码注释使用中文
// ===========================================================================

/// <summary>
/// Provider EnrichTokenCost fail-fast 验收测试。
/// </summary>
[TestClass]
[TestCategory("R29")]
[TestCategory("DecisionEngine")]
public sealed class R29D_ProviderTokenCostFailFastTests
{
    /// <summary>构建测试用 ContextItem（带非空 Content）。</summary>
    private static ContextItem MakeItemWithContent(string id, string content) => new()
    {
        Id = id,
        WorkspaceId = "test-ws",
        CollectionId = "test-col",
        Content = content,
        Type = "test",
        Tags = new[] { "mandatory" }
    };

    /// <summary>构建最小化 EffectivePolicySnapshot（默认 bundle）。</summary>
    private static EffectivePolicySnapshot MakeSnapshot()
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
            ResolutionScope = new ContextDecisionScope("test-ws", "test-col")
        };
    }

    /// <summary>构建最小化 CandidateProviderContext（Retrieval 用途）。</summary>
    private static CandidateProviderContext MakeContext(RetrievalExpert expert, bool includeContent = true)
    {
        var snapshot = MakeSnapshot();
        return new CandidateProviderContext(
            Request: new ContextDecisionRuntimeRequest
            {
                RequestId = "req-fail-fast",
                Scope = new ContextDecisionScope("test-ws", "test-col"),
                Purpose = ContextDecisionPurpose.Retrieval,
                TokenBudget = 4096,
                TopK = 10,
                RetrievalInput = new RetrievalInput { IncludeContent = includeContent }
            },
            Policy: snapshot,
            Routing: new ExpertRoutingDecision
            {
                Expert = expert,
                Enabled = true,
                TopK = snapshot.Budget.DefaultTopK,
                TokenBudget = snapshot.Budget.DefaultTokenBudget,
                Weight = 1.0,
                ReasonCode = "test"
            },
            AdaptationContext: new CandidateAdaptationContext
            {
                WorkspaceId = "test-ws",
                CollectionId = "test-col",
                ObservedAt = DateTimeOffset.UtcNow
            });
    }

    // =======================================================================
    // 1. 未注入 tokenizer + 非空内容 → 抛 InvalidOperationException
    // =======================================================================

    [TestMethod]
    public async Task MandatoryProvider_NoTokenizer_NonEmptyContent_Throws()
    {
        // Provider 未注入 tokenizer（null）+ item.Content 非空 → 调用 EnrichTokenCost 时抛异常
        var store = new FixedItemContextStore(MakeItemWithContent("m-1", "this is real content"));
        var provider = new MandatoryCandidateProvider(store, tokenizerResolver: null);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(MakeContext(RetrievalExpert.Mandatory, includeContent: true)).AsTask());

        Assert.IsTrue(ex.Message.Contains("IContextTokenizerResolver", StringComparison.Ordinal),
            "异常消息应明确指出 IContextTokenizerResolver 不可用。");
        Assert.IsTrue(ex.Message.Contains("R29 WP-D-3", StringComparison.Ordinal),
            "异常消息应标记 R29 WP-D-3 fail-fast 来源。");
    }

    [TestMethod]
    public async Task LexicalProvider_NoTokenizer_NonEmptyContent_Throws()
    {
        // LexicalCandidateProvider 同样 fail-fast（不同 Provider 类型覆盖）
        // 注意：Lexical 需要 QueryText 才会进入召回路径（否则早期返回 Empty）
        var store = new FixedItemContextStore(MakeItemWithContent("lex-1", "lexical content here"));
        var provider = new LexicalCandidateProvider(store, tokenizerResolver: null);

        var ctx = MakeContext(RetrievalExpert.Lexical, includeContent: true);
        // 设置 QueryText 让 Lexical 进入召回路径
        ctx = ctx with { Request = ctx.Request with { QueryText = "lexical" } };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            provider.ExecuteAsync(ctx).AsTask());
    }

    // =======================================================================
    // 2. 注入 tokenizer + 非空内容 → 正常产出 envelope（TokenCost 已填充）
    // =======================================================================

    [TestMethod]
    public async Task MandatoryProvider_WithTokenizer_NonEmptyContent_Succeeds()
    {
        // Provider 注入 DefaultContextTokenizerResolver → 调用 EnrichTokenCost 正常填充 TokenCost
        var store = new FixedItemContextStore(MakeItemWithContent("m-1", "this is real content"));
        var tokenizer = new DefaultContextTokenizerResolver();
        var provider = new MandatoryCandidateProvider(store, tokenizerResolver: tokenizer);

        var result = await provider.ExecuteAsync(MakeContext(RetrievalExpert.Mandatory, includeContent: true));

        Assert.AreEqual(1, result.Envelopes.Count, "应召回 1 个候选。");
        var envelope = result.Envelopes[0];
        Assert.IsNotNull(envelope.TokenCost, "TokenCost 必须被填充。");
        Assert.IsTrue(envelope.TokenCost!.ContentTokens > 0, "ContentTokens 应大于 0（非空内容）。");
        Assert.IsFalse(envelope.TokenCost!.IsEstimated,
            "DefaultContextTokenizerResolver 返回的 TokenCost 不应是 length/4 估算。");
    }

    // =======================================================================
    // 3. 未注入 tokenizer + IncludeContent=false（空内容）→ 正常产出
    // =======================================================================

    [TestMethod]
    public async Task MandatoryProvider_NoTokenizer_EmptyContent_Succeeds()
    {
        // IncludeContent=false → material.Content 为空字符串 → 无需 tokenize → 不抛异常
        var store = new FixedItemContextStore(MakeItemWithContent("m-1", "raw content ignored"));
        var provider = new MandatoryCandidateProvider(store, tokenizerResolver: null);

        var result = await provider.ExecuteAsync(MakeContext(RetrievalExpert.Mandatory, includeContent: false));

        Assert.AreEqual(1, result.Envelopes.Count, "IncludeContent=false 不影响候选召回数量。");
        var envelope = result.Envelopes[0];
        Assert.IsNotNull(envelope.TokenCost, "空内容也应填充 TokenCost（ContentTokens=0）。");
        Assert.AreEqual(0, envelope.TokenCost!.ContentTokens,
            "IncludeContent=false 时 ContentTokens 应为 0。");
        // Material.Content 应为空字符串
        Assert.AreEqual(string.Empty, result.Materials[envelope.CanonicalKey].Content);
    }
}
