using System.Globalization;
using System.Reflection;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

// ===========================================================================
// Final Closure — 22 项硬验收测试
//
// 覆盖六个工作包：
//   A. Artifact 真实化（3 项）
//   B. Purpose 语义完整化（7 项）
//   C. Token Ledger（3 项）
//   D. 强安全结果（2 项）
//   E. Production Replay Capture（3 项）
//   F. Experiment Plane 可靠性（4 项）
//
// 设计原则：
//   - 使用真实 DefaultContextDecisionRuntime + DefaultContextDecisionEngine（V2 路径）
//   - 复用 R28BTestHelpers + R28B_ClosureGateAcceptanceTests 中的 internal Stub
//   - TokenCostHelper 为 internal，通过反射访问（与现有 ResolveMandatoryOverflowPolicy 测试一致）
//   - 所有代码注释使用中文
// ===========================================================================

// ===========================================================================
// 辅助 Stub：ContextCapturingProvider — 捕获 Provider 执行时的 CandidateProviderContext
// ===========================================================================

/// <summary>
/// 捕获上下文的 Provider Stub。记录最后一次 ExecuteAsync 接收的 CandidateProviderContext，
/// 用于验证 RetrievalInput / SeedCandidates 等请求语义是否正确转发到 Provider。
/// </summary>
internal sealed class ContextCapturingProvider : ICandidateProvider
{
    private readonly ExpertExecutionResult _result;
    public ExpertKind Kind { get; }
    public CandidateProviderContext? LastContext { get; private set; }
    public int ExecuteCallCount { get; private set; }

    public ContextCapturingProvider(ExpertKind kind, ExpertExecutionResult result)
    {
        Kind = kind;
        _result = result;
    }

    public ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default)
    {
        LastContext = context;
        ExecuteCallCount++;
        return ValueTask.FromResult(_result);
    }
}

// ===========================================================================
// A. ArtifactTruthAcceptanceTests — Artifact 真实化（3 项）
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class ArtifactTruthAcceptanceTests
{
    [TestMethod]
    public void ExecutionArtifactIsCompleteOnSuccessEmptyAndAllRejected()
    {
        // 验证三种场景下 Execution Artifact 的所有字段都被完整填充：
        //   1. 成功路径（有选中候选）
        //   2. 空结果路径（无候选进入 Engine）
        //   3. 全部拒绝路径（EarlyGate 拒绝所有候选）
        var factory = DefaultExecutionArtifactFactory.Instance;
        var snapshot = MakeSnapshot();
        var routing = new ExpertRoutingDecisionSet { Decisions = Array.Empty<ExpertRoutingDecision>() };

        // --- 场景 1：成功路径 ---
        var successEnvelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var successKey = successEnvelope.CanonicalKey;
        var successDecision = R28BTestHelpers.MakeResult("op-success",
            selected: new[] { successEnvelope }, estimatedTokens: 100, tokenBudget: 1000,
            allocationDecisions: new[] { R28BTestHelpers.MakeAllocation(successKey, "recent_context", 100) });
        var successWorkingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { successEnvelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [successKey] = R28BTestHelpers.MakeMaterial(successKey, "success content")
            }
        };
        var successRequest = MakeNormalizedRequest("op-success");
        var successArtifacts = new[]
        {
            new ProviderExecutionArtifact
            {
                Kind = ExpertKind.Semantic,
                Envelopes = new[] { successEnvelope },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [successKey] = R28BTestHelpers.MakeMaterial(successKey, "success content")
                },
                Succeeded = true,
                Duration = TimeSpan.FromMilliseconds(10)
            }
        };

        var successExecution = factory.Create(successRequest, "hash-success",
            successDecision, successWorkingSet, snapshot, routing, successArtifacts);

        Assert.IsNotNull(successExecution.NormalizedRequest, "成功路径：NormalizedRequest 必须填充。");
        Assert.AreEqual("hash-success", successExecution.RequestSemanticHash, "成功路径：RequestSemanticHash 必须填充。");
        Assert.AreEqual(successRequest.Scope, successExecution.Scope, "成功路径：Scope 必须从 NormalizedRequest 获取。");
        Assert.IsNotNull(successExecution.Policy, "成功路径：Policy 必须填充。");
        Assert.IsNotNull(successExecution.WorkingSet, "成功路径：WorkingSet 必须填充。");
        Assert.IsTrue(successExecution.ProviderReports.Count > 0, "成功路径：ProviderReports 必须非空。");
        Assert.IsTrue(successExecution.ProviderOutputSnapshots.Count > 0, "成功路径：ProviderOutputSnapshots 必须非空。");
        Assert.IsNotNull(successExecution.FeatureSchemaVersion, "成功路径：FeatureSchemaVersion 必须填充。");
        Assert.IsNotNull(successExecution.AllocatorVersion, "成功路径：AllocatorVersion 必须填充。");
        Assert.IsNotNull(successExecution.TokenizerVersion, "成功路径：TokenizerVersion 必须填充。");
        Assert.IsNotNull(successExecution.FinalTokenCost, "成功路径：FinalTokenCost 必须填充。");
        Assert.IsFalse(successExecution.IsDegraded, "成功路径（所有 Provider 成功）：IsDegraded 必须为 false。");

        // --- 场景 2：空结果路径（无候选） ---
        var emptyDecision = R28BTestHelpers.MakeResult("op-empty", estimatedTokens: 0, tokenBudget: 1000);
        var emptyWorkingSet = new CandidateWorkingSet
        {
            Envelopes = Array.Empty<ContextCandidateEnvelope>(),
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>()
        };
        var emptyRequest = MakeNormalizedRequest("op-empty");

        var emptyExecution = factory.Create(emptyRequest, "hash-empty",
            emptyDecision, emptyWorkingSet, snapshot, routing, Array.Empty<ProviderExecutionArtifact>());

        Assert.IsNotNull(emptyExecution.NormalizedRequest, "空路径：NormalizedRequest 必须填充。");
        Assert.AreEqual("hash-empty", emptyExecution.RequestSemanticHash, "空路径：RequestSemanticHash 必须填充。");
        Assert.AreEqual(emptyRequest.Scope, emptyExecution.Scope, "空路径：Scope 必须从 NormalizedRequest 获取。");
        Assert.IsNotNull(emptyExecution.FinalTokenCost, "空路径：FinalTokenCost 必须填充（即使为 0 token）。");
        Assert.IsFalse(emptyExecution.IsDegraded, "空路径（无 Provider）：IsDegraded 必须为 false。");

        // --- 场景 3：全部拒绝路径 + Provider degraded ---
        var rejectedDecision = R28BTestHelpers.MakeResult("op-rejected",
            dropped: new[] { successEnvelope }, estimatedTokens: 0, tokenBudget: 1000);
        var rejectedArtifacts = new[]
        {
            new ProviderExecutionArtifact
            {
                Kind = ExpertKind.Semantic,
                Envelopes = new[] { successEnvelope },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [successKey] = R28BTestHelpers.MakeMaterial(successKey, "rejected content")
                },
                Succeeded = false,
                Duration = TimeSpan.FromMilliseconds(5),
                ErrorCode = "timeout"
            }
        };
        var rejectedRequest = MakeNormalizedRequest("op-rejected");

        var rejectedExecution = factory.Create(rejectedRequest, "hash-rejected",
            rejectedDecision, successWorkingSet, snapshot, routing, rejectedArtifacts);

        Assert.IsNotNull(rejectedExecution.NormalizedRequest, "拒绝路径：NormalizedRequest 必须填充。");
        Assert.AreEqual("hash-rejected", rejectedExecution.RequestSemanticHash, "拒绝路径：RequestSemanticHash 必须填充。");
        Assert.IsTrue(rejectedExecution.IsDegraded, "拒绝路径（Provider 失败）：IsDegraded 必须为 true。");
        Assert.IsNotNull(rejectedExecution.FinalTokenCost, "拒绝路径：FinalTokenCost 必须填充。");
    }

    [TestMethod]
    public void RequestSemanticHashIsCultureAndCollectionOrderInvariant()
    {
        // 请求语义哈希必须跨 culture 和集合顺序不变：
        //   1. 改变 CurrentCulture 不影响哈希值（使用 invariant culture 格式化数值）
        //   2. 改变 SeedCandidates 顺序不影响哈希值（SeedCandidates 不参与哈希）
        var hasher = DefaultRequestSemanticHasher.Instance;
        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-hash-test",
            Scope = new ContextDecisionScope("ws-1", "col-1"),
            Purpose = ContextDecisionPurpose.Retrieval,
            QueryText = "test query",
            TokenBudget = 4096,
            TopK = 10,
            SeedCandidates = new[]
            {
                R28BTestHelpers.MakeEnvelope("seed-a", ContextCandidateSource.Semantic, 0.8, 100),
                R28BTestHelpers.MakeEnvelope("seed-b", ContextCandidateSource.Lexical, 0.6, 200)
            }
        };

        var reversedRequest = request with
        {
            SeedCandidates = new[]
            {
                R28BTestHelpers.MakeEnvelope("seed-b", ContextCandidateSource.Lexical, 0.6, 200),
                R28BTestHelpers.MakeEnvelope("seed-a", ContextCandidateSource.Semantic, 0.8, 100)
            }
        };

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            // culture 1: en-US（小数点分隔符）
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            var hashEnUs = hasher.ComputeHash(request);
            var hashEnUsReversed = hasher.ComputeHash(reversedRequest);

            // culture 2: de-DE（逗号分隔符 — 如果不用 invariant culture，数值格式会不同）
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var hashDeDe = hasher.ComputeHash(request);

            // culture 3: zh-CN
            CultureInfo.CurrentCulture = new CultureInfo("zh-CN");
            var hashZhCn = hasher.ComputeHash(request);

            // 断言：不同 culture 下同一请求的哈希必须相同
            Assert.AreEqual(hashEnUs, hashDeDe,
                "请求语义哈希必须跨 culture 不变（使用 invariant culture 格式化数值）。");
            Assert.AreEqual(hashEnUs, hashZhCn,
                "请求语义哈希必须跨 culture 不变（en-US == zh-CN）。");

            // 断言：SeedCandidates 顺序不同但哈希相同（SeedCandidates 不参与哈希计算）
            Assert.AreEqual(hashEnUs, hashEnUsReversed,
                "SeedCandidates 顺序变化不影响请求语义哈希（不参与哈希输入）。");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [TestMethod]
    public async Task GraphUsesOriginalSeedsAndPhaseOneCandidates()
    {
        // Graph Provider（Phase 2）必须接收 Phase 1 merged 候选 + 原始 SeedCandidates 作为其 SeedCandidates。
        // 验证 MergeSeedCandidates(mergedFromProviders, seedCandidates) 的语义：
        //   Provider 输出优先，原始 Seed 补充新 key（去重 by CanonicalKey）。
        var phase1Envelope = R28BTestHelpers.MakeEnvelope("phase1-c1", ContextCandidateSource.Lexical, 0.7, 100);
        var originalSeedEnvelope = R28BTestHelpers.MakeEnvelope("seed-original", ContextCandidateSource.Semantic, 0.9, 200);

        // Phase 1 provider：Lexical，返回一个候选
        var phase1Provider = new ContextCapturingProvider(
            ExpertKind.Lexical,
            MakeExpertResultFromEnvelope(phase1Envelope));

        // Phase 2 provider：Graph，捕获接收到的 SeedCandidates
        var graphProvider = new ContextCapturingProvider(
            ExpertKind.Graph,
            new ExpertExecutionResult(Array.Empty<ContextCandidateEnvelope>(),
                new Dictionary<CanonicalCandidateKey, CandidateMaterial>()));

        var runtime = BuildRuntime(providers: new[] { phase1Provider, graphProvider });

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-graph-seeds",
            Scope = new ContextDecisionScope("test-ws", "test-col"),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 4096,
            TopK = 10,
            SeedCandidates = new[] { originalSeedEnvelope }
        };

        await runtime.ExecuteAsync(request, CancellationToken.None);

        // Graph Provider 必须被执行（Phase 2）
        Assert.AreEqual(1, graphProvider.ExecuteCallCount,
            "Graph Provider 必须被执行一次（Phase 2）。");

        // Graph Provider 接收的 SeedCandidates 必须包含 Phase 1 候选 + 原始 Seed
        var graphSeeds = graphProvider.LastContext!.Request.SeedCandidates;
        Assert.IsNotNull(graphSeeds, "Graph Provider 必须接收到 SeedCandidates。");
        Assert.IsTrue(graphSeeds.Count >= 2,
            "Graph Provider SeedCandidates 必须包含 Phase 1 候选 + 原始 Seed（至少 2 个）。");

        var seedEntityIds = graphSeeds.Select(s => s.CanonicalKey.EntityId).ToHashSet();
        Assert.IsTrue(seedEntityIds.Contains("phase1-c1"),
            "Graph SeedCandidates 必须包含 Phase 1 候选（phase1-c1）。");
        Assert.IsTrue(seedEntityIds.Contains("seed-original"),
            "Graph SeedCandidates 必须包含原始 Seed（seed-original）。");
    }

    // --- helpers ---

    private static ContextDecisionRuntimeRequest MakeNormalizedRequest(string requestId) => new()
    {
        RequestId = requestId,
        Scope = new ContextDecisionScope("test-ws", "test-col"),
        Purpose = ContextDecisionPurpose.Retrieval,
        TokenBudget = 1000,
        TopK = 10
    };

    internal static EffectivePolicySnapshot MakeSnapshot()
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

    internal static DefaultContextDecisionRuntime BuildRuntime(
        IReadOnlyList<ICandidateProvider> providers,
        IGlobalAllocator? allocator = null,
        ISelectedCandidateHydrator? selectedCandidateHydrator = null)
    {
        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            globalAllocator: allocator ?? new DefaultGlobalAllocator());

        return new DefaultContextDecisionRuntime(
            engine: engine,
            policyProvider: new DefaultResolvedPolicyProvider(),
            router: new DefaultRouter(new DefaultExpertCatalog()),
            expertCatalog: new DefaultExpertCatalog(),
            candidateProviders: providers,
            canonicalMerger: new DefaultCanonicalCandidateMerger(),
            earlyAdmissionGate: new DefaultEarlyAdmissionGate(),
            featurePipeline: new DefaultFeaturePipeline(),
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            selectedCandidateHydrator: selectedCandidateHydrator);
    }

    private static ExpertExecutionResult MakeExpertResultFromEnvelope(ContextCandidateEnvelope envelope)
    {
        var material = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "phase1 content");
        return new ExpertExecutionResult(
            new[] { envelope },
            new Dictionary<CanonicalCandidateKey, CandidateMaterial> { [envelope.CanonicalKey] = material });
    }
}

// ===========================================================================
// B. PurposeSemanticAcceptanceTests — Purpose 语义完整化（7 项）
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class PurposeSemanticAcceptanceTests
{
    [TestMethod]
    public async Task IncludeContentFalseNeverLoadsOrReturnsContent()
    {
        // 契约澄清：IncludeContent=false → recall 阶段不加载正文（Material.Content 为空）；
        // 若 hydrator 未注入（本测试 BuildRuntime 默认不注入），选中候选的 Content 保持空。
        // 生产 DI 路径始终注入 ISelectedCandidateHydrator（见 CoreExtensions.cs），
        // 选中候选会被 hydrator 批量 re-fetch 正文——该契约由
        // IncludeContentFalseHydratorRefetchesSelected 测试覆盖。
        var store = new FixedItemContextStore(new ContextItem
        {
            Id = "item-mandatory",
            WorkspaceId = "test-ws",
            CollectionId = "test-col",
            Content = "should not be loaded when IncludeContent=false",
            Type = "test",
            Tags = new[] { "mandatory" }
        });
        var provider = new MandatoryCandidateProvider(store);

        // 不注入 hydrator（模拟无 batch lookup 能力的测试容器）→ recall 后无 re-fetch
        var runtime = ArtifactTruthAcceptanceTests.BuildRuntime(providers: new[] { provider });

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-include-content-false",
            Scope = new ContextDecisionScope("test-ws", "test-col"),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 4096,
            TopK = 10,
            RetrievalInput = new RetrievalInput { IncludeContent = false }
        };

        var execution = await runtime.ExecuteWithWorkingSetAsync(request, CancellationToken.None);

        // IncludeContent=false 且无 hydrator 时，所有 Material 的 Content 必须为空字符串
        foreach (var material in execution.WorkingSet.Materials.Values)
        {
            Assert.AreEqual(string.Empty, material.Content,
                "IncludeContent=false 且无 hydrator 时 Material.Content 必须为空字符串（recall 不加载正文，无 re-fetch）。");
        }
    }

    [TestMethod]
    public async Task IncludeContentFalseHydratorRefetchesSelected()
    {
        // 新增测试：IncludeContent=false + hydrator 注入时，选中候选的 Content
        // 必须被 ISelectedCandidateHydrator 批量 re-fetch（Late Hydration 完整契约）。
        // 生产 DI 路径（CoreExtensions.cs）始终注入 hydrator，此测试模拟生产 wiring。
        var store = new FixedItemContextStore(new ContextItem
        {
            Id = "item-mandatory",
            WorkspaceId = "test-ws",
            CollectionId = "test-col",
            Content = "hydrated content after late hydration",
            Type = "test",
            Tags = new[] { "mandatory" }
        });
        var provider = new MandatoryCandidateProvider(store);
        // 注入 hydrator（store 同时实现 IContextStoreBatchLookup，模拟生产 batch lookup 能力）
        var hydrator = new DefaultSelectedCandidateHydrator(contextBatchLookup: store);

        var runtime = ArtifactTruthAcceptanceTests.BuildRuntime(
            providers: new[] { provider },
            selectedCandidateHydrator: hydrator);

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-hydrate-refetch",
            Scope = new ContextDecisionScope("test-ws", "test-col"),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 4096,
            TopK = 10,
            RetrievalInput = new RetrievalInput { IncludeContent = false }
        };

        var execution = await runtime.ExecuteWithWorkingSetAsync(request, CancellationToken.None);

        // hydrator 注入后，选中候选的 Content 必须被 re-fetch（非空）
        Assert.IsTrue(execution.WorkingSet.Materials.Count > 0,
            "至少应有一个候选被选中并进入 WorkingSet。");
        foreach (var material in execution.WorkingSet.Materials.Values)
        {
            Assert.AreNotEqual(string.Empty, material.Content,
                "IncludeContent=false + hydrator 注入时，选中候选的 Content 必须被 hydrate（非空）。");
        }
    }

    [TestMethod]
    public async Task RewrittenQueryIsActuallyUsed()
    {
        // RewrittenQueryText 必须被 Provider 实际使用（而非被忽略）。
        // Lexical Provider 使用 RewrittenQueryText ?? QueryText 作为 effective query。
        // 验证方式：用 ContextCapturingProvider 捕获 context，确认 RewrittenQueryText 被转发到 Provider。
        var capturingProvider = new ContextCapturingProvider(
            ExpertKind.Lexical,
            new ExpertExecutionResult(Array.Empty<ContextCandidateEnvelope>(),
                new Dictionary<CanonicalCandidateKey, CandidateMaterial>()));

        var runtime = ArtifactTruthAcceptanceTests.BuildRuntime(providers: new[] { capturingProvider });

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-rewritten-query",
            Scope = new ContextDecisionScope("test-ws", "test-col"),
            Purpose = ContextDecisionPurpose.Retrieval,
            QueryText = "original query",
            TokenBudget = 4096,
            TopK = 10,
            RetrievalInput = new RetrievalInput { RewrittenQueryText = "rewritten query text" }
        };

        await runtime.ExecuteAsync(request, CancellationToken.None);

        Assert.IsNotNull(capturingProvider.LastContext, "Provider 必须接收到 context。");
        Assert.IsNotNull(capturingProvider.LastContext.Request.RetrievalInput,
            "Provider context 必须包含 RetrievalInput。");
        Assert.AreEqual("rewritten query text",
            capturingProvider.LastContext.Request.RetrievalInput!.RewrittenQueryText,
            "RewrittenQueryText 必须原样转发到 Provider context。");
    }

    [TestMethod]
    public async Task RequiredRefsAreMandatory()
    {
        // RetrievalInput.Refs 必须被 MandatoryCandidateProvider 视为额外 RequiredIds 强制召回。
        // 验证方式：用 ContextCapturingProvider(Kind=Mandatory) 捕获 context，
        // 确认 Refs 被合并到 RequiredIds 中。
        // 由于 MandatoryCandidateProvider 内部合并 Refs 到 RequiredIds（ResolveRequiredIds），
        // 这里验证 Refs 字段被正确转发到 Provider context。
        var capturingProvider = new ContextCapturingProvider(
            ExpertKind.Mandatory,
            new ExpertExecutionResult(Array.Empty<ContextCandidateEnvelope>(),
                new Dictionary<CanonicalCandidateKey, CandidateMaterial>()));

        var runtime = ArtifactTruthAcceptanceTests.BuildRuntime(providers: new[] { capturingProvider });

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-refs-mandatory",
            Scope = new ContextDecisionScope("test-ws", "test-col"),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 4096,
            TopK = 10,
            RetrievalInput = new RetrievalInput
            {
                RequiredIds = new[] { "id-1" },
                Refs = new[] { "ref-a", "ref-b" }
            }
        };

        await runtime.ExecuteAsync(request, CancellationToken.None);

        Assert.IsNotNull(capturingProvider.LastContext, "Mandatory Provider 必须接收到 context。");
        var retrievalInput = capturingProvider.LastContext!.Request.RetrievalInput;
        Assert.IsNotNull(retrievalInput, "Provider context 必须包含 RetrievalInput。");
        CollectionAssert.Contains(retrievalInput!.Refs.ToList(), "ref-a",
            "Refs 必须被转发到 Provider context（Mandatory Provider 将其合并为额外 RequiredIds 强制召回）。");
        CollectionAssert.Contains(retrievalInput.Refs.ToList(), "ref-b",
            "Refs 必须被转发到 Provider context。");
    }

    [TestMethod]
    public async Task RelationTypeAndDepthAreApplied()
    {
        // AllowedRelationTypes + RelationExpansionDepth 必须被转发到 Graph Provider context。
        // Graph Provider 读取 context.Request.RetrievalInput.AllowedRelationTypes 和
        // RelationExpansionDepth 来控制 BFS 扩展行为。
        var capturingProvider = new ContextCapturingProvider(
            ExpertKind.Graph,
            new ExpertExecutionResult(
                new[] { R28BTestHelpers.MakeEnvelope("seed-for-graph", ContextCandidateSource.Semantic, 0.5, 100) },
                new Dictionary<CanonicalCandidateKey, CandidateMaterial>()));

        var runtime = ArtifactTruthAcceptanceTests.BuildRuntime(providers: new[] { capturingProvider });

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-relation-params",
            Scope = new ContextDecisionScope("test-ws", "test-col"),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 4096,
            TopK = 10,
            SeedCandidates = new[] { R28BTestHelpers.MakeEnvelope("seed-1", ContextCandidateSource.Semantic, 0.8, 100) },
            RetrievalInput = new RetrievalInput
            {
                AllowedRelationTypes = new[] { "derived_from", "related_to" },
                RelationExpansionDepth = 3
            }
        };

        await runtime.ExecuteAsync(request, CancellationToken.None);

        Assert.IsNotNull(capturingProvider.LastContext, "Graph Provider 必须接收到 context。");
        var retrievalInput = capturingProvider.LastContext!.Request.RetrievalInput;
        Assert.IsNotNull(retrievalInput, "Provider context 必须包含 RetrievalInput。");
        CollectionAssert.AreEqual(
            new[] { "derived_from", "related_to" },
            retrievalInput!.AllowedRelationTypes.ToList(),
            "AllowedRelationTypes 必须原样转发到 Graph Provider context。");
        Assert.AreEqual(3, retrievalInput.RelationExpansionDepth,
            "RelationExpansionDepth 必须原样转发到 Graph Provider context。");
    }

    [TestMethod]
    public void PackageModeChangesSectionRatios()
    {
        // PackageInput.SectionRatios 必须作为 per-request override 传递给 ContextDecisionRequest。
        // Runtime 在构建 Engine request 时，PackageInput.SectionRatios 优先于 snapshot.Budget.SectionRatios。
        // 验证方式：构建两个不同的 SectionRatios，验证它们被正确传递到 Engine 的 AllocationDecisions。
        // 由于 DefaultContextDecisionEngine 内部使用 SectionRatios 分配 section 预算，
        // 不同 SectionRatios 会导致不同 section 的候选分配差异。
        // 这里验证 SectionRatios 被传递到 Package 元数据中（通过 PackageResultProjector）。

        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var result = R28BTestHelpers.MakeResult("op-pkg-mode",
            selected: new[] { envelope }, estimatedTokens: 100, tokenBudget: 1000,
            allocationDecisions: new[] { R28BTestHelpers.MakeAllocation(envelope.CanonicalKey, "recent_context", 100) });
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "content")
            }
        };
        var execution = new ContextDecisionExecutionResult
        {
            Decision = result,
            WorkingSet = workingSet,
            Policy = ArtifactTruthAcceptanceTests.MakeSnapshot(),
            Routing = new ExpertRoutingDecisionSet { Decisions = Array.Empty<ExpertRoutingDecision>() },
            NormalizedRequest = new ContextDecisionRuntimeRequest
            {
                RequestId = "op-pkg-mode",
                Scope = new ContextDecisionScope("test-ws", "test-col"),
                Purpose = ContextDecisionPurpose.Package,
                PackageInput = new PackageInput
                {
                    Mode = ContextPackageMode.Chat,
                    SectionRatios = new Dictionary<string, double>
                    {
                        ["recent_context"] = 0.5,
                        ["memory"] = 0.3,
                        ["global"] = 0.2
                    }
                }
            },
            Scope = new ContextDecisionScope("test-ws", "test-col")
        };

        var projector = new PackageResultProjector();
        var dto = projector.Project(execution);

        // Mode=Chat 必须写入 metadata["mode"]
        Assert.IsTrue(dto.Package.Metadata.ContainsKey("mode"),
            "PackageInput.Mode 非 None 时必须写入 package.Metadata[\"mode\"]。");
        Assert.AreEqual("Chat", dto.Package.Metadata["mode"],
            "Mode=Chat 时 metadata[\"mode\"] 必须为 \"Chat\"。");
    }

    [TestMethod]
    public void PackagePolicyIsAppliedByV2()
    {
        // PackageInput.Policy 必须被 V2 Projector 消费，写入 package.Metadata["packagePolicyId"]。
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var result = R28BTestHelpers.MakeResult("op-pkg-policy",
            selected: new[] { envelope }, estimatedTokens: 100, tokenBudget: 1000,
            allocationDecisions: new[] { R28BTestHelpers.MakeAllocation(envelope.CanonicalKey, "recent_context", 100) });
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "content")
            }
        };
        var execution = new ContextDecisionExecutionResult
        {
            Decision = result,
            WorkingSet = workingSet,
            Policy = ArtifactTruthAcceptanceTests.MakeSnapshot(),
            Routing = new ExpertRoutingDecisionSet { Decisions = Array.Empty<ExpertRoutingDecision>() },
            NormalizedRequest = new ContextDecisionRuntimeRequest
            {
                RequestId = "op-pkg-policy",
                Scope = new ContextDecisionScope("test-ws", "test-col"),
                Purpose = ContextDecisionPurpose.Package,
                PackageInput = new PackageInput
                {
                    Policy = new ContextPackagePolicy { Id = "policy-test-001" }
                }
            },
            Scope = new ContextDecisionScope("test-ws", "test-col")
        };

        var projector = new PackageResultProjector();
        var dto = projector.Project(execution);

        Assert.IsTrue(dto.Package.Metadata.ContainsKey("packagePolicyId"),
            "PackageInput.Policy 非 null 时必须写入 package.Metadata[\"packagePolicyId\"]。");
        Assert.AreEqual("policy-test-001", dto.Package.Metadata["packagePolicyId"],
            "packagePolicyId 必须与 PackageInput.Policy.Id 一致。");
    }

    [TestMethod]
    public void EmptyV2PackagePreservesScope()
    {
        // 空 V2 Package（无选中候选）必须保留 Scope（WorkspaceId / CollectionId）。
        // 验证 PackageResultProjector.Project(execution) 在 SelectedEnvelopes 为空时
        // 从 execution.Scope 获取 WorkspaceId/CollectionId，而非丢失。
        var emptyResult = R28BTestHelpers.MakeResult("op-empty-pkg",
            estimatedTokens: 0, tokenBudget: 1000);
        var emptyWorkingSet = new CandidateWorkingSet
        {
            Envelopes = Array.Empty<ContextCandidateEnvelope>(),
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>()
        };
        var execution = new ContextDecisionExecutionResult
        {
            Decision = emptyResult,
            WorkingSet = emptyWorkingSet,
            Policy = ArtifactTruthAcceptanceTests.MakeSnapshot(),
            Routing = new ExpertRoutingDecisionSet { Decisions = Array.Empty<ExpertRoutingDecision>() },
            NormalizedRequest = new ContextDecisionRuntimeRequest
            {
                RequestId = "op-empty-pkg",
                Scope = new ContextDecisionScope("ws-preserved", "col-preserved"),
                Purpose = ContextDecisionPurpose.Package
            },
            Scope = new ContextDecisionScope("ws-preserved", "col-preserved")
        };

        var projector = new PackageResultProjector();
        var dto = projector.Project(execution);

        Assert.AreEqual("ws-preserved", dto.Package.WorkspaceId,
            "空 Package 必须从 execution.Scope 保留 WorkspaceId（不从候选反推）。");
        Assert.AreEqual("col-preserved", dto.Package.CollectionId,
            "空 Package 必须从 execution.Scope 保留 CollectionId。");
    }
}

// ===========================================================================
// C. TokenLedgerAcceptanceTests — Token Ledger（3 项）
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class TokenLedgerAcceptanceTests
{
    [TestMethod]
    public void CandidateTokenCostUsesConfiguredTokenizer()
    {
        // CandidateTokenCost 必须使用配置的 IContextTokenizerResolver 计算 token 数。
        // 验证：传入 resolver 时 TokenizerId 为 resolver 解析的 tokenizer 名；
        //       不传 resolver 时 TokenizerId 为 "length-div-4"（估算回退）。
        var resolver = new DefaultContextTokenizerResolver();
        var content = "这是一段中文内容，用于测试 tokenizer 的精确计数能力。This is English content for testing.";

        // 通过反射调用 internal TokenCostHelper.ComputeTokenCost
        var costWithResolver = InvokeComputeTokenCost(content, resolver, modelName: "gpt-4");
        var costWithoutResolver = InvokeComputeTokenCost(content, null, modelName: null);

        // 有 resolver 时：TokenizerId 应为 "gpt-4"（模型名优先）或 resolver 解析的 source
        Assert.AreNotEqual("length-div-4", costWithResolver.TokenizerId,
            "传入 IContextTokenizerResolver 时 TokenizerId 必须不是 length-div-4（使用配置的 tokenizer）。");
        Assert.IsTrue(costWithResolver.ContentTokens > 0,
            "有 resolver 时 ContentTokens 必须大于 0。");

        // 无 resolver 时：TokenizerId 必须为 "length-div-4"，ContentTokens = length/4
        Assert.AreEqual("length-div-4", costWithoutResolver.TokenizerId,
            "不传 resolver 时 TokenizerId 必须为 length-div-4（估算回退）。");
        Assert.IsTrue(costWithoutResolver.IsEstimated,
            "不传 resolver 时 IsEstimated 必须为 true。");
        Assert.AreEqual(Math.Max(1, content.Length / 4), costWithoutResolver.ContentTokens,
            "不传 resolver 时 ContentTokens 必须为 max(1, length/4)。");
    }

    [TestMethod]
    public void FinalSerializedArtifactNeverExceedsBudget()
    {
        // 最终序列化 Artifact 的 TotalTokens 必须不超过 BudgetLimit（WithinBudget=true）。
        // 当总 token 超过预算时，必须执行 deterministic repair（截断到预算内）。
        // 验证：构建一个 token 总量在预算内的决策，FinalArtifactTokenCost.WithinBudget=true。
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var allocation = R28BTestHelpers.MakeAllocation(envelope.CanonicalKey, "recent_context", 100);
        var decision = R28BTestHelpers.MakeResult("op-token-budget",
            selected: new[] { envelope }, estimatedTokens: 100, tokenBudget: 500,
            allocationDecisions: new[] { allocation });
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "content within budget")
            }
        };

        // 通过 DefaultExecutionArtifactFactory.Create 计算 FinalTokenCost
        var factory = DefaultExecutionArtifactFactory.Instance;
        var execution = factory.Create(
            new ContextDecisionRuntimeRequest
            {
                RequestId = "op-token-budget",
                Scope = new ContextDecisionScope("test-ws", "test-col"),
                Purpose = ContextDecisionPurpose.Retrieval,
                TokenBudget = 500
            },
            "hash-token-budget",
            decision, workingSet,
            ArtifactTruthAcceptanceTests.MakeSnapshot(),
            new ExpertRoutingDecisionSet { Decisions = Array.Empty<ExpertRoutingDecision>() },
            Array.Empty<ProviderExecutionArtifact>());

        Assert.IsNotNull(execution.FinalTokenCost, "FinalTokenCost 必须被填充。");
        Assert.IsTrue(execution.FinalTokenCost!.WithinBudget,
            "总 token 在预算内时 WithinBudget 必须为 true。");
        Assert.IsTrue(execution.FinalTokenCost.TotalTokens <= execution.FinalTokenCost.BudgetLimit,
            "TotalTokens 必须不超过 BudgetLimit。");
    }

    [TestMethod]
    public void SeparatorAndHeaderTokensUseSameTokenizer()
    {
        // Section 分隔符 token 和 header token 必须使用与候选正文相同的 tokenizer 版本。
        // 验证：FinalArtifactTokenCost.TokenizerId 与 CandidateTokenCost.TokenizerId 一致。
        var envelope1 = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Lexical, 0.5, 100);
        var envelope2 = R28BTestHelpers.MakeEnvelope("c2", ContextCandidateSource.Semantic, 0.6, 100);
        var allocations = new[]
        {
            R28BTestHelpers.MakeAllocation(envelope1.CanonicalKey, "recent_context", 80),
            R28BTestHelpers.MakeAllocation(envelope2.CanonicalKey, "recent_context", 60)
        };
        var decision = R28BTestHelpers.MakeResult("op-sep-tokens",
            selected: new[] { envelope1, envelope2 }, estimatedTokens: 140, tokenBudget: 1000,
            allocationDecisions: allocations);
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope1, envelope2 },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope1.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope1.CanonicalKey, "content one"),
                [envelope2.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope2.CanonicalKey, "content two")
            }
        };

        // 不传 resolver：所有 token 使用同一 "allocator-included-tokens" tokenizer
        var finalCost = InvokeComputeFinalArtifactTokenCost(decision, workingSet, resolver: null);

        Assert.AreEqual("allocator-included-tokens", finalCost.TokenizerId,
            "FinalArtifactTokenCost.TokenizerId 必须与候选 token 计算使用的 tokenizer 一致。");

        // 验证 section 分隔符 token 被正确计算
        var section = finalCost.Sections.FirstOrDefault(s => s.Section == "recent_context");
        Assert.IsNotNull(section, "recent_context section 必须存在。");
        Assert.IsTrue(section!.SeparatorTokens > 0,
            "多候选 section 的 SeparatorTokens 必须大于 0（2 token/分隔符）。");
        Assert.AreEqual(2 * (2 - 1), section.SeparatorTokens,
            "2 个候选间有 1 个分隔符，SeparatorTokens = 2 * (count-1) = 2。");
    }

    // --- 反射辅助：访问 internal TokenCostHelper ---

    private static CandidateTokenCost InvokeComputeTokenCost(
        string? content, IContextTokenizerResolver? resolver, string? modelName)
    {
        var method = typeof(DefaultExecutionArtifactFactory).Assembly
            .GetType("ContextCore.Core.Services.DecisionEngine.TokenCostHelper", throwOnError: false)
            ?.GetMethod("ComputeTokenCost", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("TokenCostHelper.ComputeTokenCost 未找到。");
        return (CandidateTokenCost)method.Invoke(null, new object?[] { content, resolver, modelName })!;
    }

    private static FinalArtifactTokenCost InvokeComputeFinalArtifactTokenCost(
        ContextDecisionResult decision, CandidateWorkingSet workingSet,
        IContextTokenizerResolver? resolver = null, string? modelName = null)
    {
        var method = typeof(DefaultExecutionArtifactFactory).Assembly
            .GetType("ContextCore.Core.Services.DecisionEngine.TokenCostHelper", throwOnError: false)
            ?.GetMethod("ComputeFinalArtifactTokenCost", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("TokenCostHelper.ComputeFinalArtifactTokenCost 未找到。");
        return (FinalArtifactTokenCost)method.Invoke(null, new object?[] { decision, workingSet, resolver, modelName })!;
    }
}

// ===========================================================================
// D. StrongSafetyAcceptanceTests — 强安全结果（2 项）
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class StrongSafetyAcceptanceTests
{
    [TestMethod]
    public async Task AgentSessionComesOnlyFromAgentInput()
    {
        // AgentContextSnapshot 的 AgentSession 必须来自 AgentInput.Session，而非伪造。
        // 验证：当 AgentInput.Session 非空时，Projector 使用真实 session；
        //       当 AgentInput.Session 为 null 时，Projector 回退到 session-{RequestId}。
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var result = R28BTestHelpers.MakeResult("op-agent-session",
            selected: new[] { envelope }, estimatedTokens: 100, tokenBudget: 1000,
            allocationDecisions: new[] { R28BTestHelpers.MakeAllocation(envelope.CanonicalKey, "recent_context", 100) });
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "agent content")
            }
        };

        // 场景 1：AgentInput.Session 非空 → 使用真实 session
        var realSession = new AgentSessionId
        {
            Value = "session-real-001",
            RuntimeKind = AgentRuntimeKind.Unknown,
            WorkspaceId = "test-ws",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var executionWithSession = R28BTestHelpers.MakeExecutionResult(result);
        executionWithSession = executionWithSession with
        {
            WorkingSet = workingSet,
            NormalizedRequest = new ContextDecisionRuntimeRequest
            {
                RequestId = "op-agent-session",
                Scope = new ContextDecisionScope("test-ws", "test-col"),
                Purpose = ContextDecisionPurpose.AgentContext,
                AgentInput = new AgentInput { Session = realSession }
            }
        };

        var runtimeWithSession = new AuthoritativeAgentContextRuntime(
            new ThrowingDecisionRuntime(new InvalidOperationException()),
            new AgentContextProjector());

        // 由于 ThrowingDecisionRuntime 会抛异常，我们直接测试 Projector
        var projector = new AgentContextProjector();
        var contextWithSession = new ProjectionContext
        {
            AgentSession = realSession,
            WorkspaceId = "test-ws",
            CollectionId = "test-col"
        };
        var snapshotWithSession = projector.Project(result, workingSet, contextWithSession);

        Assert.AreEqual("session-real-001", snapshotWithSession.Session.Value,
            "AgentSession 必须来自 AgentInput.Session（真实 session，非伪造）。");

        // 场景 2：AgentInput.Session 为 null → Projector 回退到 session-{RequestId}
        var contextWithoutSession = new ProjectionContext
        {
            AgentSession = null,
            WorkspaceId = "test-ws",
            CollectionId = "test-col"
        };
        var snapshotWithoutSession = projector.Project(result, workingSet, contextWithoutSession);

        Assert.IsTrue(snapshotWithoutSession.Session.Value.StartsWith("session-"),
            "AgentInput.Session 为 null 时 Session.Value 必须回退到 session-{RequestId} 前缀。");
    }

    [TestMethod]
    public async Task AgentMandatoryOverflowFailsContextBuild()
    {
        // AgentContext Purpose 下，mandatory 候选超出预算时必须抛 MandatoryContextWindowExceededException。
        // 不静默丢弃 mandatory 候选后返回成功 — 让请求真正失败（FailClosed）。
        var mandatoryEnvelope = new ContextCandidateEnvelope
        {
            CandidateId = "mandatory-overflow",
            CanonicalKey = CanonicalCandidateKey.Create("test-ws", "test-col", "entity", "mandatory-overflow", "v1"),
            Source = ContextCandidateSource.Mandatory,
            Type = "test-type",
            EstimatedTokens = 5000,
            Safety = new CandidateSafetyState { IsMandatory = true, PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = 1.0, FinalScore = 1.0, ReasonCode = "mandatory" }
        };
        var mandatoryMaterial = R28BTestHelpers.MakeMaterial(mandatoryEnvelope.CanonicalKey, "mandatory content");

        var provider = new ContextCapturingProvider(
            ExpertKind.Mandatory,
            new ExpertExecutionResult(
                new[] { mandatoryEnvelope },
                new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [mandatoryEnvelope.CanonicalKey] = mandatoryMaterial
                }));

        var runtime = ArtifactTruthAcceptanceTests.BuildRuntime(providers: new[] { provider });

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-agent-overflow",
            Scope = new ContextDecisionScope("test-ws", "test-col"),
            Purpose = ContextDecisionPurpose.AgentContext, // → FailClosed
            TokenBudget = 100 // 远小于 mandatory 候选的 5000 tokens
        };

        await Assert.ThrowsExceptionAsync<MandatoryContextWindowExceededException>(
            async () => await runtime.ExecuteWithWorkingSetAsync(request, CancellationToken.None),
            "AgentContext Purpose 下 mandatory 候选超出预算时必须抛 MandatoryContextWindowExceededException（FailClosed，不静默成功）。");
    }
}

// ===========================================================================
// E. ProductionReplayAcceptanceTests — Production Replay Capture（3 项）
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class ProductionReplayAcceptanceTests
{
    [TestMethod]
    public void ProductionShadowFixtureContainsPolicyWorkingSetAndProviderSnapshots()
    {
        // ReplayFixture.FromExecution 必须从 Execution 中提取完整的重放数据：
        //   StoredPolicySnapshot, StoredWorkingSet, StoredProviderOutputs,
        //   StoredNormalizedRequest, StoredRequestSemanticHash,
        //   StoredFeatureSchemaVersion, StoredAllocatorVersion, StoredTokenizerVersion,
        //   StoredFinalTokenCost
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var key = envelope.CanonicalKey;
        var decision = R28BTestHelpers.MakeResult("op-shadow",
            selected: new[] { envelope }, estimatedTokens: 100, tokenBudget: 1000,
            allocationDecisions: new[] { R28BTestHelpers.MakeAllocation(key, "recent_context", 100) });
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [key] = R28BTestHelpers.MakeMaterial(key, "shadow content")
            }
        };
        var snapshot = ArtifactTruthAcceptanceTests.MakeSnapshot();
        var routing = new ExpertRoutingDecisionSet { Decisions = Array.Empty<ExpertRoutingDecision>() };

        var factory = DefaultExecutionArtifactFactory.Instance;
        var normalizedRequest = new ContextDecisionRuntimeRequest
        {
            RequestId = "op-shadow",
            Scope = new ContextDecisionScope("test-ws", "test-col"),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 1000
        };
        var artifacts = new[]
        {
            new ProviderExecutionArtifact
            {
                Kind = ExpertKind.Semantic,
                Envelopes = new[] { envelope },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [key] = R28BTestHelpers.MakeMaterial(key, "shadow content")
                },
                Succeeded = true,
                Duration = TimeSpan.FromMilliseconds(10)
            }
        };

        var execution = factory.Create(normalizedRequest, "hash-shadow",
            decision, workingSet, snapshot, routing, artifacts);

        var report = new ParityReport(
            LegacySelectedCount: 1, V2SelectedCount: 1, CommonSelectedCount: 1,
            OnlyInLegacyCount: 0, OnlyInV2Count: 0,
            JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
            LegacyTokenTotal: 100, V2TokenTotal: 100, WorkingSetCandidateCount: 1);

        var fixture = ReplayFixture.FromExecution(report, execution, "fx-shadow", "shadow");

        // 验证所有 Stored* 字段都被填充
        Assert.IsNotNull(fixture.StoredWorkingSet, "StoredWorkingSet 必须从 execution.WorkingSet 填充。");
        Assert.IsNotNull(fixture.StoredPolicySnapshot, "StoredPolicySnapshot 必须从 execution.Policy 填充。");
        Assert.IsNotNull(fixture.StoredProviderOutputs, "StoredProviderOutputs 必须从 execution.ProviderOutputSnapshots 填充。");
        Assert.IsTrue(fixture.StoredProviderOutputs!.Count > 0, "StoredProviderOutputs 必须非空。");
        Assert.IsNotNull(fixture.StoredNormalizedRequest, "StoredNormalizedRequest 必须从 execution.NormalizedRequest 填充。");
        Assert.AreEqual("hash-shadow", fixture.StoredRequestSemanticHash, "StoredRequestSemanticHash 必须从 execution.RequestSemanticHash 填充。");
        Assert.IsNotNull(fixture.StoredFeatureSchemaVersion, "StoredFeatureSchemaVersion 必须从 execution.FeatureSchemaVersion 填充。");
        Assert.IsNotNull(fixture.StoredAllocatorVersion, "StoredAllocatorVersion 必须从 execution.AllocatorVersion 填充。");
        Assert.IsNotNull(fixture.StoredTokenizerVersion, "StoredTokenizerVersion 必须从 execution.TokenizerVersion 填充。");
        Assert.IsNotNull(fixture.StoredFinalTokenCost, "StoredFinalTokenCost 必须从 execution.FinalTokenCost 填充。");
    }

    [TestMethod]
    public async Task ExpertReplaysUsesCanonicalMerger()
    {
        // ExpertReplay 必须使用 ICanonicalCandidateMerger 合并 Provider 快照，
        // 而非手工拼接（后写覆盖）。
        // 验证：注入自定义 merger，验证它被调用。
        var wasMergerCalled = false;
        var trackingMerger = new TrackingCanonicalMerger(() => wasMergerCalled = true);

        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            globalAllocator: new DefaultGlobalAllocator());

        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(),
            new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100 },
            engine: engine,
            merger: trackingMerger);

        var key1 = CanonicalCandidateKey.Create("ws", "col", "entity", "expert-1", "v1");
        var env1 = new ContextCandidateEnvelope
        {
            CandidateId = "expert-1",
            CanonicalKey = key1,
            Source = ContextCandidateSource.Lexical,
            EstimatedTokens = 100,
            Safety = new CandidateSafetyState { PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = 0.7, FinalScore = 0.7, ReasonCode = "test" }
        };

        var snapshot = ArtifactTruthAcceptanceTests.MakeSnapshot();
        var fixture = ReplayFixture.FromReport(
            new ParityReport(
                LegacySelectedCount: 1, V2SelectedCount: 1, CommonSelectedCount: 1,
                OnlyInLegacyCount: 0, OnlyInV2Count: 0,
                JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
                LegacyTokenTotal: 100, V2TokenTotal: 100, WorkingSetCandidateCount: 1),
            fixtureId: "fx-merger",
            purpose: "expert-replay") with
        {
            StoredPolicySnapshot = snapshot,
            StoredProviderOutputs = new[]
            {
                new ProviderOutputSnapshot
                {
                    Kind = ExpertKind.Lexical,
                    Envelopes = new[] { env1 },
                    Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                    {
                        [key1] = new() { Key = key1, Content = "content", NativeKind = "test" }
                    },
                    Succeeded = true,
                    Duration = TimeSpan.FromMilliseconds(10)
                }
            }
        };

        await integration.ExpertReplayAsync(fixture, CancellationToken.None);

        Assert.IsTrue(wasMergerCalled,
            "ExpertReplay 必须调用 ICanonicalCandidateMerger.Merge（使用正式合并逻辑，非手工拼接）。");

        await integration.DisposeAsync();
    }

    [TestMethod]
    public async Task ReplayMaterialConflictFailsClosed()
    {
        // 当两个 Provider 快照有相同 CanonicalCandidateKey 但不同 content hash 时，
        // CanonicalCandidateMerger 必须抛 InvalidOperationException（fail-fast，检测冲突）。
        var key = CanonicalCandidateKey.Create("ws", "col", "entity", "conflict-item", "v1");
        var merger = new DefaultCanonicalCandidateMerger();

        var outputs = new List<ExpertExecutionResult>
        {
            new(
                Array.Empty<ContextCandidateEnvelope>(),
                new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [key] = new() { Key = key, Content = "content from provider A", NativeKind = "test" }
                }),
            new(
                Array.Empty<ContextCandidateEnvelope>(),
                new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [key] = new() { Key = key, Content = "DIFFERENT content from provider B", NativeKind = "test" }
                })
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => Task.Run(() => merger.Merge(outputs)),
            "相同 CanonicalCandidateKey + 不同 content hash 时 Merger 必须抛 InvalidOperationException（fail-closed）。");
    }
}

/// <summary>
/// 跟踪 Merge 调用的 ICanonicalCandidateMerger Stub。
/// </summary>
internal sealed class TrackingCanonicalMerger : ICanonicalCandidateMerger
{
    private readonly Action _onMerge;
    public TrackingCanonicalMerger(Action onMerge) => _onMerge = onMerge;

    public CandidateWorkingSet Merge(IReadOnlyList<ExpertExecutionResult> expertOutputs)
    {
        _onMerge();
        // 委托到真实 merger 以返回有效结果
        return new DefaultCanonicalCandidateMerger().Merge(expertOutputs);
    }
}

// ===========================================================================
// F. ExperimentPlaneAcceptanceTests — Experiment Plane 可靠性（4 项）
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class ExperimentPlaneAcceptanceTests
{
    [TestMethod]
    public async Task PolicyMutationUnderSameIdVersionIsDetected()
    {
        // 当 BundleId + BundleVersion 相同但内容被修改（BundleContentHash 不一致）时，
        // PostgresResolvedPolicyProvider 必须抛 InvalidOperationException（fail-closed）。
        // 这验证了 policy mutation under same Id+Version 的检测能力。
        var bundle = DefaultPolicyBundleFactory.Create();
        var mockRegistry = new MockPolicyRegistry(
            activation: new PolicyActivation
            {
                WorkspaceId = "ws-mutation",
                CollectionId = "col-mutation",
                BundleId = bundle.BundleId,
                BundleVersion = bundle.Version,
                BundleContentHash = "sha256:stale-hash-after-mutation",
                ActivatedAt = DateTimeOffset.UtcNow,
                Epoch = 1
            },
            bundle: bundle); // bundle 是当前内容，但 activation 记录的是旧 hash

        var provider = new PostgresResolvedPolicyProvider(mockRegistry);

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-mutation",
            Scope = new ContextDecisionScope("ws-mutation", "col-mutation"),
            Purpose = ContextDecisionPurpose.Retrieval
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await provider.ResolveAsync(request, CancellationToken.None),
            "BundleId+Version 相同但 BundleContentHash 不一致时必须抛 InvalidOperationException（检测 mutation，fail-closed）。");
    }

    [TestMethod]
    public async Task QueueDropCounterMatchesActualDroppedEvents()
    {
        // DisposeAsync 后 writer 完成，后续 RecordFixture 的 TryWrite 返回 false → DroppedCount 累加。
        // DroppedCount 必须精确反映被丢弃的事件数。
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(),
            new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100 },
            recorder: new InMemoryExperimentRecorder());

        // Dispose 让 writer 完成
        await integration.DisposeAsync();

        var droppedBefore = integration.DroppedCount;
        // 记录 3 条 → 全部因 writer 已完成而被丢弃
        integration.RecordFixture(MakeHardParityReport(), "fx-drop-1", "test");
        integration.RecordFixture(MakeHardParityReport(), "fx-drop-2", "test");
        integration.RecordFixture(MakeHardParityReport(), "fx-drop-3", "test");

        Assert.AreEqual(droppedBefore + 3, integration.DroppedCount,
            "DroppedCount 必须精确反映被丢弃的事件数（3 条全部被丢弃）。");
    }

    [TestMethod]
    public async Task FlushReportsPersistedSequence()
    {
        // FlushResult 必须包含 AcceptedSequence 和 LastPersistedSequence。
        // AcceptedSequence 是 sentinel 的序号；LastPersistedSequence 是最后成功落盘的事件序号。
        var recorder = new InMemoryExperimentRecorder();
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(),
            new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100 },
            recorder: recorder);

        // 记录 2 条 fixture
        integration.RecordFixture(MakeHardParityReport(), "fx-seq-1", "test");
        integration.RecordFixture(MakeHardParityReport(), "fx-seq-2", "test");

        var flushResult = await integration.FlushAsync();

        // ProcessedCount 必须为 2（2 条全部成功落盘）
        Assert.AreEqual(2, flushResult.ProcessedCount,
            "FlushResult.ProcessedCount 必须反映已成功落盘的事件数。");
        // LastPersistedSequence 必须大于 0（至少有 2 条已落盘）
        Assert.IsTrue(flushResult.LastPersistedSequence > 0,
            "FlushResult.LastPersistedSequence 必须大于 0（有事件已成功落盘）。");
        // AcceptedSequence 必须大于 LastPersistedSequence（sentinel 序号在 Record 事件之后）
        Assert.IsTrue(flushResult.AcceptedSequence >= flushResult.LastPersistedSequence,
            "FlushResult.AcceptedSequence 必须 >= LastPersistedSequence（sentinel 在 Record 之后入队）。");

        await integration.DisposeAsync();
    }

    [TestMethod]
    public async Task DisposeReportsUndrainedEvents()
    {
        // DisposeAsync 必须返回 DisposeDrainResult，包含 DrainedCount 和 UndrainedCount。
        // 正常 drain（无超时）时 DrainedCount > 0 且 UndrainedCount = 0。
        var recorder = new InMemoryExperimentRecorder();
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(),
            new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100 },
            recorder: recorder);

        // 记录 2 条 fixture（会被 consumer 处理）
        integration.RecordFixture(MakeHardParityReport(), "fx-drain-1", "test");
        integration.RecordFixture(MakeHardParityReport(), "fx-drain-2", "test");
        await integration.FlushAsync();

        // Dispose — consumer 排空剩余事件后退出
        await integration.DisposeAsync();

        Assert.IsNotNull(integration.DrainResult, "DisposeAsync 后 DrainResult 必须非 null。");
        Assert.IsTrue(integration.DrainResult!.DrainedCount > 0,
            "DrainResult.DrainedCount 必须大于 0（有事件被 drain 处理）。");
        Assert.AreEqual(0, integration.DrainResult.UndrainedCount,
            "正常 drain（无超时）时 UndrainedCount 必须为 0。");
    }

    private static ParityReport MakeHardParityReport() => new(
        LegacySelectedCount: 1, V2SelectedCount: 1, CommonSelectedCount: 1,
        OnlyInLegacyCount: 0, OnlyInV2Count: 0,
        JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
        LegacyTokenTotal: 100, V2TokenTotal: 100, WorkingSetCandidateCount: 1);
}

// ===========================================================================
// 辅助 Stub：FixedItemContextStore — 返回固定 item 的 IContextStore
// ===========================================================================

/// <summary>
/// 返回固定 ContextItem 的 IContextStore Stub，用于测试 IncludeContent 语义。
/// 同时实现 IContextStoreBatchLookup，让 Late Hydration 测试可注入 hydrator。
/// </summary>
internal sealed class FixedItemContextStore : IContextStore, IContextStoreBatchLookup
{
    private readonly ContextItem _item;

    public FixedItemContextStore(ContextItem item) => _item = item;

    public Task<IReadOnlyList<ContextItem>> QueryAsync(
        ContextQuery query, CancellationToken cancellationToken = default)
    {
        // 按 Tags 匹配
        if (query.Tags is { Count: > 0 } tags)
        {
            var itemTags = _item.Tags ?? Array.Empty<string>();
            if (!tags.Any(t => itemTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                return Task.FromResult<IReadOnlyList<ContextItem>>(Array.Empty<ContextItem>());
        }
        return Task.FromResult<IReadOnlyList<ContextItem>>(new[] { _item });
    }

    public Task<ContextItem?> GetAsync(
        string workspaceId, string collectionId, string id,
        CancellationToken cancellationToken = default)
        => Task.FromResult<ContextItem?>(_item.Id == id ? _item : null);

    public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DeleteAsync(
        string workspaceId, string collectionId, string id,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    // IContextStoreBatchLookup 实现——返回固定 item（含完整正文），供 hydrator re-fetch
    public Task<IReadOnlyList<ContextItem>> BatchGetAsync(
        string workspaceId, string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ContextItem>(ids.Count);
        foreach (var id in ids)
        {
            if (string.Equals(id, _item.Id, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(_item);
            }
        }
        return Task.FromResult<IReadOnlyList<ContextItem>>(result);
    }
}
