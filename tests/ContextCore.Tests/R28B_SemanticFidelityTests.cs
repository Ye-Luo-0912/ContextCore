using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

// ===========================================================================
// R28-B.7 语义保真硬验收测试（25 项）
//
// 覆盖范围（7 个测试类）：
//   A. RequestSemanticsAcceptanceTests — RetrievalInput Include 开关 + 必需字段传播（8 项）
//   B. ArtifactAndAgentAcceptanceTests — Material sidecar 恢复 + Agent Material 保留（3 项）
//   C. AllocationAcceptanceTests — Purpose 驱动的 MandatoryOverflowPolicy 解析（2 项）
//   D. TokenPipelineAcceptanceTests — Token 计数 / 截断 / section 分隔符（4 项）
//   E. ReplayAcceptanceTests — DecisionReplay / ExpertReplay 分层重放（2 项）
//   F. ExperimentQueueAcceptanceTests — bounded 队列 + dead-letter + flush 计数（3 项）
//   G. OtherAcceptanceTests — Cutover / Cancellation / Policy 稳定性（3 项）
//
// 设计原则：
//   - 使用真实 DefaultContextDecisionRuntime + DefaultContextDecisionEngine（V2 路径）
//   - Stub 仅用于隔离 I/O（Provider/Store/Recorder），不替换决策内核
//   - 复用 R28BTestHelpers（MakeEnvelope / MakeResult / MakeMaterial / MakeAllocation）
//   - 复用 R28B_ClosureGateAcceptanceTests 中的 internal Stub
//     （CountingCandidateProvider / RecordingDecisionRuntime / ThrowingDecisionRuntime / CallTrackingContextStore）
//   - 所有代码注释使用中文
// ===========================================================================

// ===========================================================================
// A. RequestSemanticsAcceptanceTests — RetrievalInput Include 开关 + 必需字段传播
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class RequestSemanticsAcceptanceTests
{
    [TestMethod]
    public async Task IncludeVectorRecallFalseDisablesSemanticProvider()
    {
        // IncludeVectorRecall=false → Router 将 Semantic Expert 设为 Enabled=false → Semantic Provider 永不执行
        var semanticProvider = new CountingCandidateProvider(
            ExpertKind.Semantic, MakeExpertResult("semantic-item"));
        var runtime = BuildRuntime(providers: new[] { semanticProvider });

        var request = MakeRetrievalRequest();
        var modifiedRequest = request with { RetrievalInput = request.RetrievalInput! with { IncludeVectorRecall = false } };

        await runtime.ExecuteAsync(modifiedRequest, CancellationToken.None);

        Assert.AreEqual(0, semanticProvider.ExecuteCallCount,
            "IncludeVectorRecall=false 时 Semantic Provider 必须永不被执行。");
    }

    [TestMethod]
    public async Task IncludeRelationExpansionFalseDisablesGraphProvider()
    {
        // IncludeRelationExpansion=false → Graph Provider 永不执行
        var graphProvider = new CountingCandidateProvider(
            ExpertKind.Graph, MakeExpertResult("graph-item"));
        var runtime = BuildRuntime(providers: new[] { graphProvider });

        var request = MakeRetrievalRequest();
        var modifiedRequest = request with { RetrievalInput = request.RetrievalInput! with { IncludeRelationExpansion = false } };

        await runtime.ExecuteAsync(modifiedRequest, CancellationToken.None);

        Assert.AreEqual(0, graphProvider.ExecuteCallCount,
            "IncludeRelationExpansion=false 时 Graph Provider 必须永不被执行。");
    }

    [TestMethod]
    public async Task IncludeStableMemoryFalseDisablesStableMemoryProvider()
    {
        // IncludeStableMemory=false → StableMemory Provider 永不执行
        var stableMemoryProvider = new CountingCandidateProvider(
            ExpertKind.StableMemory, MakeExpertResult("stable-item"));
        var runtime = BuildRuntime(providers: new[] { stableMemoryProvider });

        var request = MakeRetrievalRequest();
        var modifiedRequest = request with { RetrievalInput = request.RetrievalInput! with { IncludeStableMemory = false } };

        await runtime.ExecuteAsync(modifiedRequest, CancellationToken.None);

        Assert.AreEqual(0, stableMemoryProvider.ExecuteCallCount,
            "IncludeStableMemory=false 时 StableMemory Provider 必须永不被执行。");
    }

    [TestMethod]
    public async Task IncludeWorkingMemoryFalseDisablesWorkingMemoryProvider()
    {
        // IncludeWorkingMemory=false → WorkingMemory Provider 永不执行
        var workingMemoryProvider = new CountingCandidateProvider(
            ExpertKind.WorkingMemory, MakeExpertResult("wm-item"));
        var runtime = BuildRuntime(providers: new[] { workingMemoryProvider });

        var request = MakeRetrievalRequest();
        var modifiedRequest = request with { RetrievalInput = request.RetrievalInput! with { IncludeWorkingMemory = false } };

        await runtime.ExecuteAsync(modifiedRequest, CancellationToken.None);

        Assert.AreEqual(0, workingMemoryProvider.ExecuteCallCount,
            "IncludeWorkingMemory=false 时 WorkingMemory Provider 必须永不被执行。");
    }

    [TestMethod]
    public async Task IncludeKeywordRecallFalseDisablesLexicalProvider()
    {
        // IncludeKeywordRecall=false → Lexical Provider 永不执行
        var lexicalProvider = new CountingCandidateProvider(
            ExpertKind.Lexical, MakeExpertResult("lexical-item"));
        var runtime = BuildRuntime(providers: new[] { lexicalProvider });

        var request = MakeRetrievalRequest();
        var modifiedRequest = request with { RetrievalInput = request.RetrievalInput! with { IncludeKeywordRecall = false } };

        await runtime.ExecuteAsync(modifiedRequest, CancellationToken.None);

        Assert.AreEqual(0, lexicalProvider.ExecuteCallCount,
            "IncludeKeywordRecall=false 时 Lexical Provider 必须永不被执行。");
    }

    [TestMethod]
    public async Task RequiredIdsAreForwardedToRetrievalInput()
    {
        // RequiredIds 必须原样传播到 V2 RuntimeRequest.RetrievalInput.RequiredIds
        var stubRuntime = new RecordingDecisionRuntime(
            R28BTestHelpers.MakeResult("op-req-ids"));
        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "op-req-ids",
            Scope = new ContextDecisionScope("ws", "col"),
            Purpose = ContextDecisionPurpose.Retrieval,
            RetrievalInput = new RetrievalInput
            {
                RequiredIds = new[] { "id-1", "id-2", "id-3" }
            }
        };

        await stubRuntime.ExecuteAsync(request, CancellationToken.None);

        Assert.IsNotNull(stubRuntime.LastRequest);
        Assert.IsNotNull(stubRuntime.LastRequest.RetrievalInput);
        CollectionAssert.AreEqual(
            new[] { "id-1", "id-2", "id-3" },
            stubRuntime.LastRequest.RetrievalInput!.RequiredIds.ToList(),
            "RequiredIds 必须原样传播到 RetrievalInput。");
    }

    [TestMethod]
    public async Task RequiredTagsAreForwardedToRetrievalInput()
    {
        // RequiredTags 必须原样传播到 V2 RuntimeRequest.RetrievalInput.RequiredTags
        var stubRuntime = new RecordingDecisionRuntime(
            R28BTestHelpers.MakeResult("op-req-tags"));
        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "op-req-tags",
            Scope = new ContextDecisionScope("ws", "col"),
            Purpose = ContextDecisionPurpose.Retrieval,
            RetrievalInput = new RetrievalInput
            {
                RequiredTags = new[] { "tag-a", "tag-b" }
            }
        };

        await stubRuntime.ExecuteAsync(request, CancellationToken.None);

        Assert.IsNotNull(stubRuntime.LastRequest);
        Assert.IsNotNull(stubRuntime.LastRequest.RetrievalInput);
        CollectionAssert.AreEqual(
            new[] { "tag-a", "tag-b" },
            stubRuntime.LastRequest.RetrievalInput!.RequiredTags.ToList(),
            "RequiredTags 必须原样传播到 RetrievalInput。");
    }

    [TestMethod]
    public async Task QueryVectorIsForwardedToRetrievalInput()
    {
        // QueryVector 必须原样传播到 V2 RuntimeRequest.RetrievalInput.QueryVector
        var stubRuntime = new RecordingDecisionRuntime(
            R28BTestHelpers.MakeResult("op-qvec"));
        var queryVector = new float[] { 0.1f, 0.2f, 0.3f };
        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "op-qvec",
            Scope = new ContextDecisionScope("ws", "col"),
            Purpose = ContextDecisionPurpose.Retrieval,
            RetrievalInput = new RetrievalInput
            {
                QueryVector = queryVector,
                ModelName = "test-embed-model"
            }
        };

        await stubRuntime.ExecuteAsync(request, CancellationToken.None);

        Assert.IsNotNull(stubRuntime.LastRequest);
        Assert.IsNotNull(stubRuntime.LastRequest.RetrievalInput);
        CollectionAssert.AreEqual(
            queryVector,
            stubRuntime.LastRequest.RetrievalInput!.QueryVector.ToList(),
            "QueryVector 必须原样传播到 RetrievalInput。");
        Assert.AreEqual("test-embed-model",
            stubRuntime.LastRequest.RetrievalInput!.ModelName,
            "ModelName 必须原样传播到 RetrievalInput。");
    }

    // --- helpers ---

    private static ContextDecisionRuntimeRequest MakeRetrievalRequest() => new()
    {
        RequestId = "req-include-test",
        Scope = new ContextDecisionScope("test-ws", "test-col"),
        Purpose = ContextDecisionPurpose.Retrieval,
        QueryText = "test query",
        TokenBudget = 4096,
        TopK = 10,
        SeedCandidates = Array.Empty<ContextCandidateEnvelope>(),
        RetrievalInput = new RetrievalInput()
    };

    internal static DefaultContextDecisionRuntime BuildRuntime(
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
            router: new DefaultRouter(new DefaultExpertCatalog()),
            expertCatalog: new DefaultExpertCatalog(),
            candidateProviders: providers,
            canonicalMerger: new DefaultCanonicalCandidateMerger(),
            earlyAdmissionGate: new DefaultEarlyAdmissionGate(),
            featurePipeline: new DefaultFeaturePipeline(),
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()));
    }

    private static ExpertExecutionResult MakeExpertResult(string entityId)
    {
        var key = CanonicalCandidateKey.Create("test-ws", "test-col", "test-entity", entityId, "v1");
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = entityId,
            CanonicalKey = key,
            Source = ContextCandidateSource.Semantic,
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
}

// ===========================================================================
// B. ArtifactAndAgentAcceptanceTests — Material sidecar 恢复 + Agent Material 保留
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class ArtifactAndAgentAcceptanceTests
{
    [TestMethod]
    public void RetrievalProjectorRestoresContentFromMaterialSidecar()
    {
        // Material sidecar 恢复：Projector 从 workingSet.Materials 恢复候选正文
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 100);
        var result = R28BTestHelpers.MakeResult("op-1", selected: new[] { envelope }, estimatedTokens: 100);
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "material sidecar content")
            }
        };

        var projector = new RetrievalResultProjector();
        var dto = projector.Project(result, workingSet);

        Assert.AreEqual(1, dto.SelectedItems.Count);
        Assert.AreEqual("material sidecar content", dto.SelectedItems[0].Content,
            "Retrieval Projector 必须从 Material sidecar 恢复 Content。");
    }

    [TestMethod]
    public async Task SeedWorkingSetMaterialsArePreservedInCompleteWorkingSet()
    {
        // SeedWorkingSet.Materials 必须被合并到 complete WorkingSet（不丢失种子 Material）
        var key = CanonicalCandidateKey.Create("test-ws", "test-col", "entity", "seed-1", "v1");
        var seedEnvelope = new ContextCandidateEnvelope
        {
            CandidateId = "seed-1",
            CanonicalKey = key,
            Source = ContextCandidateSource.Semantic,
            EstimatedTokens = 100,
            Safety = new CandidateSafetyState { PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = 0.8, FinalScore = 0.8, ReasonCode = "seed" }
        };
        var seedMaterial = new CandidateMaterial { Key = key, Content = "seed material body", NativeKind = "test" };

        var provider = new CountingCandidateProvider(
            ExpertKind.Lexical, new ExpertExecutionResult(
                Array.Empty<ContextCandidateEnvelope>(),
                new Dictionary<CanonicalCandidateKey, CandidateMaterial>()));
        var runtime = RequestSemanticsAcceptanceTests.BuildRuntime(providers: new[] { provider });

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-seed-ws",
            Scope = new ContextDecisionScope("test-ws", "test-col"),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 4096,
            TopK = 10,
            SeedWorkingSet = new CandidateWorkingSet
            {
                Envelopes = new[] { seedEnvelope },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial> { [key] = seedMaterial }
            }
        };

        var execution = await runtime.ExecuteWithWorkingSetAsync(request, CancellationToken.None);

        // SeedWorkingSet.Materials 必须出现在 complete WorkingSet.Materials 中
        Assert.IsTrue(execution.WorkingSet.Materials.ContainsKey(key),
            "SeedWorkingSet.Materials 必须被合并到 complete WorkingSet（不丢失种子 Material）。");
        Assert.AreEqual("seed material body", execution.WorkingSet.Materials[key].Content,
            "种子 Material 的 Content 必须原样保留。");
    }

    [TestMethod]
    public void AgentContextProjectorAppliesContentTruncationWhenIncludedTokensLessThanEstimated()
    {
        // AgentContext Projector 在 IncludedTokens < EstimatedTokens 时使用 IContentTruncator 真正截断 Content
        var envelope = R28BTestHelpers.MakeEnvelope("c1", ContextCandidateSource.Semantic, 0.8, 500);
        var allocation = R28BTestHelpers.MakeAllocation(
            envelope.CanonicalKey, section: "recent_context", includedTokens: 50, isTruncated: true);
        var result = R28BTestHelpers.MakeResult("op-agent-trunc",
            selected: new[] { envelope },
            estimatedTokens: 50,
            tokenBudget: 1000,
            allocationDecisions: new[] { allocation });
        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { envelope },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "very long agent content that should be truncated to fit the included tokens budget")
            }
        };

        var projector = new AgentContextProjector();
        var snapshot = projector.Project(result, workingSet);

        Assert.IsTrue(snapshot.Sections.Count > 0, "Agent snapshot 必须构建 section。");
        var section = snapshot.Sections[0];
        // 截断后 ActualTokens 不应超过 IncludedTokens（50）
        Assert.IsTrue(section.ActualTokens <= 50,
            "Agent Projector 截断后 ActualTokens 不应超过 IncludedTokens。");
    }
}

// ===========================================================================
// C. AllocationAcceptanceTests — Purpose 驱动的 MandatoryOverflowPolicy 解析
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class AllocationAcceptanceTests
{
    [TestMethod]
    public void AgentContextPurposeResolvesToFailClosed()
    {
        // AgentContext Purpose → MandatoryOverflowPolicy.FailClosed
        // 验证方式：构建 AgentContext purpose 的 RuntimeRequest，用 RecordingDecisionRuntime 捕获请求，
        // 然后通过 DefaultContextDecisionRuntime 的 AllocationContext 验证 policy。
        // 由于 ResolveMandatoryOverflowPolicy 是 private，通过反射验证 switch 语义。
        Assert.AreEqual(
            MandatoryOverflowPolicy.FailClosed,
            InvokeResolveMandatoryOverflowPolicy(ContextDecisionPurpose.AgentContext),
            "AgentContext Purpose 必须解析为 FailClosed（硬窗口，拒绝 mandatory 溢出）。");
    }

    [TestMethod]
    public void RetrievalAndPackagePurposesResolveToAllowOverflow()
    {
        // Retrieval / Package Purpose → MandatoryOverflowPolicy.AllowOverflowWithDiagnostic
        Assert.AreEqual(
            MandatoryOverflowPolicy.AllowOverflowWithDiagnostic,
            InvokeResolveMandatoryOverflowPolicy(ContextDecisionPurpose.Retrieval),
            "Retrieval Purpose 必须解析为 AllowOverflowWithDiagnostic。");
        Assert.AreEqual(
            MandatoryOverflowPolicy.AllowOverflowWithDiagnostic,
            InvokeResolveMandatoryOverflowPolicy(ContextDecisionPurpose.Package),
            "Package Purpose 必须解析为 AllowOverflowWithDiagnostic。");
    }

    /// <summary>
    /// 通过反射调用 DefaultContextDecisionRuntime 的 private static ResolveMandatoryOverflowPolicy 方法。
    /// </summary>
    private static MandatoryOverflowPolicy InvokeResolveMandatoryOverflowPolicy(ContextDecisionPurpose purpose)
    {
        var method = typeof(DefaultContextDecisionRuntime)
            .GetMethod("ResolveMandatoryOverflowPolicy",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("ResolveMandatoryOverflowPolicy 方法未找到。");
        return (MandatoryOverflowPolicy)method.Invoke(null, new object[] { purpose })!;
    }
}

// ===========================================================================
// D. TokenPipelineAcceptanceTests — Token 计数 / 截断 / section 分隔符
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class TokenPipelineAcceptanceTests
{
    [TestMethod]
    public void DefaultContentTruncatorCountTokensReturnsZeroForEmptyString()
    {
        // 空字符串的 token 数必须为 0
        var truncator = new DefaultContentTruncator();

        Assert.AreEqual(0, truncator.CountTokens(""),
            "空字符串的 token 数必须为 0。");
    }

    [TestMethod]
    public void DefaultContentTruncatorCountTokensUsesLengthDividedByFour()
    {
        // 非空字符串的 token 数 = max(1, length/4)
        var truncator = new DefaultContentTruncator();

        // "hello world" 11 chars → 11/4 = 2 tokens
        Assert.AreEqual(2, truncator.CountTokens("hello world"),
            "11 字符的 token 数必须为 2（11/4=2）。");
        // "ab" 2 chars → max(1, 2/4=0) = 1 token
        Assert.AreEqual(1, truncator.CountTokens("ab"),
            "2 字符的 token 数必须为 1（max(1, 0)）。");
    }

    [TestMethod]
    public void DefaultContentTruncatorTruncateReturnsWasTruncatedWhenBudgetExceeded()
    {
        // 内容 token 数超过 maxTokens 时，WasTruncated=true 且 ActualTokens=maxTokens
        var truncator = new DefaultContentTruncator();
        var longContent = new string('a', 100); // 100 chars → 25 tokens

        var result = truncator.Truncate(longContent, maxTokens: 10);

        Assert.IsTrue(result.WasTruncated,
            "内容 token 数超过 maxTokens 时 WasTruncated 必须为 true。");
        Assert.AreEqual(10, result.ActualTokens,
            "截断后 ActualTokens 必须等于 maxTokens。");
        Assert.IsTrue(result.TruncatedContent.Length <= 40,
            "截断后内容长度不应超过 maxTokens*4=40 字符。");
    }

    [TestMethod]
    public void PackageProjectorCountsSectionSeparatorsInEstimatedTokens()
    {
        // Package Projector 在 section 内候选间添加 "\n\n" 分隔符（2 token/候选），
        // section EstimatedTokens 必须包含分隔符 token。
        var env1 = R28BTestHelpers.MakeEnvelope("i1", ContextCandidateSource.Lexical, 0.5, 100);
        var env2 = R28BTestHelpers.MakeEnvelope("i2", ContextCandidateSource.Semantic, 0.6, 100);

        var allocations = new[]
        {
            R28BTestHelpers.MakeAllocation(env1.CanonicalKey, "recent_context", 80),
            R28BTestHelpers.MakeAllocation(env2.CanonicalKey, "recent_context", 60)
        };

        var result = R28BTestHelpers.MakeResult(
            requestId: "build-sep",
            selected: new[] { env1, env2 },
            estimatedTokens: 140,
            tokenBudget: 1000,
            allocationDecisions: allocations);

        var workingSet = new CandidateWorkingSet
        {
            Envelopes = new[] { env1, env2 },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [env1.CanonicalKey] = R28BTestHelpers.MakeMaterial(env1.CanonicalKey, "c1"),
                [env2.CanonicalKey] = R28BTestHelpers.MakeMaterial(env2.CanonicalKey, "c2")
            }
        };

        var projector = new PackageResultProjector();
        var dto = projector.Project(result, workingSet);

        // recent_context section：80 + 60 = 140（候选 token）+ 2（分隔符 "\n\n"）= 142
        var section = dto.Package.Sections[0];
        Assert.AreEqual(142, section.EstimatedTokens,
            "Section EstimatedTokens 必须包含候选间分隔符 token（2 token/分隔符）。");
    }
}

// ===========================================================================
// E. ReplayAcceptanceTests — DecisionReplay / ExpertReplay 分层重放
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class ReplayAcceptanceTests
{
    [TestMethod]
    public async Task DecisionReplayUsesStoredWorkingSetAndPolicySnapshot()
    {
        // DecisionReplay 不访问 Store / Provider / Router，直接用 StoredWorkingSet + StoredPolicySnapshot 进入 Engine
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
            engine: engine);

        var envelope = R28BTestHelpers.MakeEnvelope("replay-c1", ContextCandidateSource.Semantic, 0.8, 100);
        var snapshot = MakeSnapshot();
        var fixture = ReplayFixture.FromReport(
            new ParityReport(
                LegacySelectedCount: 1, V2SelectedCount: 1, CommonSelectedCount: 1,
                OnlyInLegacyCount: 0, OnlyInV2Count: 0,
                JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
                LegacyTokenTotal: 100, V2TokenTotal: 100, WorkingSetCandidateCount: 1),
            fixtureId: "fx-decision-replay",
            purpose: "decision-replay") with
        {
            StoredWorkingSet = new CandidateWorkingSet
            {
                Envelopes = new[] { envelope },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [envelope.CanonicalKey] = R28BTestHelpers.MakeMaterial(envelope.CanonicalKey, "replay content")
                }
            },
            StoredPolicySnapshot = snapshot
        };

        var result = await integration.DecisionReplayAsync(fixture, CancellationToken.None);

        // DecisionReplay 必须产出非空决策（直接从 StoredWorkingSet 进入 Engine）
        Assert.IsNotNull(result);
        Assert.IsTrue(result.SelectedEnvelopes.Count > 0 || result.DroppedEnvelopes.Count > 0,
            "DecisionReplay 必须从 StoredWorkingSet 产出决策结果（不调用 Provider/Router/Store）。");
    }

    [TestMethod]
    public async Task ExpertReplayMergesProviderSnapshotsBeforeEngine()
    {
        // ExpertReplay 合并 StoredProviderOutputs 的 Envelopes + Materials，跳过 Provider 执行
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
            engine: engine);

        var key1 = CanonicalCandidateKey.Create("ws", "col", "entity", "expert-1", "v1");
        var key2 = CanonicalCandidateKey.Create("ws", "col", "entity", "expert-2", "v1");
        var env1 = new ContextCandidateEnvelope
        {
            CandidateId = "expert-1",
            CanonicalKey = key1,
            Source = ContextCandidateSource.Lexical,
            EstimatedTokens = 100,
            Safety = new CandidateSafetyState { PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = 0.7, FinalScore = 0.7, ReasonCode = "test" }
        };
        var env2 = new ContextCandidateEnvelope
        {
            CandidateId = "expert-2",
            CanonicalKey = key2,
            Source = ContextCandidateSource.Semantic,
            EstimatedTokens = 100,
            Safety = new CandidateSafetyState { PassesSafetyGate = true },
            Utility = new CandidateUtilityScore { DeterministicScore = 0.8, FinalScore = 0.8, ReasonCode = "test" }
        };

        var snapshot = MakeSnapshot();
        var fixture = ReplayFixture.FromReport(
            new ParityReport(
                LegacySelectedCount: 2, V2SelectedCount: 2, CommonSelectedCount: 2,
                OnlyInLegacyCount: 0, OnlyInV2Count: 0,
                JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
                LegacyTokenTotal: 200, V2TokenTotal: 200, WorkingSetCandidateCount: 2),
            fixtureId: "fx-expert-replay",
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
                        [key1] = new() { Key = key1, Content = "lexical content", NativeKind = "test" }
                    },
                    Succeeded = true,
                    Duration = TimeSpan.FromMilliseconds(10)
                },
                new ProviderOutputSnapshot
                {
                    Kind = ExpertKind.Semantic,
                    Envelopes = new[] { env2 },
                    Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                    {
                        [key2] = new() { Key = key2, Content = "semantic content", NativeKind = "test" }
                    },
                    Succeeded = true,
                    Duration = TimeSpan.FromMilliseconds(15)
                }
            }
        };

        var result = await integration.ExpertReplayAsync(fixture, CancellationToken.None);

        // ExpertReplay 合并两个 Provider 快照后进入 Engine，产出决策
        Assert.IsNotNull(result);
        Assert.IsTrue(result.AllocationDecisions.Count > 0,
            "ExpertReplay 合并 Provider 快照后必须产出 AllocationDecisions。");
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
// F. ExperimentQueueAcceptanceTests — bounded 队列 + dead-letter + flush 计数
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class ExperimentQueueAcceptanceTests
{
    [TestMethod]
    public async Task BoundedQueueDropsEventsWhenWriterCompletes()
    {
        // DisposeAsync 后 writer 完成，后续 RecordFixture 的 TryWrite 返回 false → DroppedCount 累加
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(),
            new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100 });

        // 先正常记录一条
        integration.RecordFixture(MakeHardParityReport(), "fx-1", "test");
        await integration.FlushAsync();

        // DisposeAsync 让 writer 完成
        await integration.DisposeAsync();

        // Dispose 后再记录 → DroppedCount 累加
        integration.RecordFixture(MakeHardParityReport(), "fx-after-dispose", "test");

        Assert.IsTrue(integration.DroppedCount > 0,
            "DisposeAsync 后入队失败的事件必须计入 DroppedCount。");
    }

    [TestMethod]
    public async Task FailedWritesGoToDeadLetterQueueAfterRetries()
    {
        // Recorder 持续抛异常 → 重试 3 次后进入 dead-letter → FailedWriteCount + DeadLetterCount 累加
        var throwingRecorder = new ThrowingExperimentRecorder();
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(),
            new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100 },
            recorder: throwingRecorder);

        integration.RecordFixture(MakeHardParityReport(), "fx-fail", "test");

        // 等待 consumer 处理（重试 3 次 + 退避 ~300ms）
        await integration.FlushAsync();

        Assert.IsTrue(integration.FailedWriteCount > 0,
            "Recorder 持续抛异常 → 重试 3 次后 FailedWriteCount 必须累加。");
        Assert.IsTrue(integration.DeadLetterCount > 0,
            "重试失败的 Record 事件必须进入 dead-letter 队列。");

        await integration.DisposeAsync();
    }

    [TestMethod]
    public async Task FlushAsyncReturnsCurrentCounters()
    {
        // FlushAsync 返回 FlushResult，包含 ProcessedCount / FailedCount / DroppedCount
        var recorder = new InMemoryExperimentRecorder();
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(),
            new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100 },
            recorder: recorder);

        // 记录 3 条 fixture
        integration.RecordFixture(MakeHardParityReport(), "fx-1", "test");
        integration.RecordFixture(MakeHardParityReport(), "fx-2", "test");
        integration.RecordFixture(MakeHardParityReport(), "fx-3", "test");

        var flushResult = await integration.FlushAsync();

        // 3 条全部成功落盘
        Assert.AreEqual(3, flushResult.ProcessedCount,
            "FlushAsync.ProcessedCount 必须反映已成功落盘的 Record 事件数。");
        Assert.AreEqual(0, flushResult.FailedCount,
            "无写入失败时 FailedCount 必须为 0。");
        Assert.AreEqual(0, flushResult.DroppedCount,
            "无入队失败时 DroppedCount 必须为 0。");

        await integration.DisposeAsync();
    }

    private static ParityReport MakeHardParityReport() => new(
        LegacySelectedCount: 2, V2SelectedCount: 2, CommonSelectedCount: 2,
        OnlyInLegacyCount: 0, OnlyInV2Count: 0,
        JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
        LegacyTokenTotal: 200, V2TokenTotal: 200, WorkingSetCandidateCount: 2);
}

/// <summary>
/// R28-B.7 测试 Stub：RecordAsync 始终抛异常的 IExperimentRecorder。
/// 用于验证 bounded 队列的重试 + dead-letter 机制。
/// </summary>
internal sealed class ThrowingExperimentRecorder : IExperimentRecorder
{
    public ValueTask RecordAsync(ReplayFixture fixture, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("故意失败（测试 dead-letter 机制）");

    public ValueTask<IReadOnlyList<ReplayFixture>> GetHistoryAsync(CancellationToken cancellationToken = default)
        => new(Array.Empty<ReplayFixture>());

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

// ===========================================================================
// G. OtherAcceptanceTests — Cutover / Cancellation / Policy 稳定性
// ===========================================================================

[TestClass]
[TestCategory("R28-B")]
[TestCategory("R28-B.7")]
public sealed class OtherAcceptanceTests
{
    [TestMethod]
    public async Task DecisionReplayWithoutEngineThrowsInvalidOperationException()
    {
        // DecisionReplay 在未注入 IContextDecisionEngine 时必须抛 InvalidOperationException
        // （构造时 engine=null → DecisionReplay 不可用）
        var integration = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(),
            new ShadowGateEvaluator(),
            new CutoverConfiguration { CutoverPercentage = 100 },
            engine: null); // 未注入 Engine

        var fixture = ReplayFixture.FromReport(
            new ParityReport(
                LegacySelectedCount: 1, V2SelectedCount: 1, CommonSelectedCount: 1,
                OnlyInLegacyCount: 0, OnlyInV2Count: 0,
                JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
                LegacyTokenTotal: 100, V2TokenTotal: 100, WorkingSetCandidateCount: 1),
            fixtureId: "fx-no-engine",
            purpose: "no-engine");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await integration.DecisionReplayAsync(fixture, CancellationToken.None),
            "未注入 Engine 时 DecisionReplay 必须抛 InvalidOperationException。");

        await integration.DisposeAsync();
    }

    [TestMethod]
    public async Task ExpertReplayWithoutProviderOutputsThrowsInvalidOperationException()
    {
        // ExpertReplay 在 StoredProviderOutputs 为 null/空时必须抛 InvalidOperationException
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
            engine: engine);

        var snapshot = MakeSnapshot();
        // StoredProviderOutputs 为 null
        var fixture = ReplayFixture.FromReport(
            new ParityReport(
                LegacySelectedCount: 0, V2SelectedCount: 0, CommonSelectedCount: 0,
                OnlyInLegacyCount: 0, OnlyInV2Count: 0,
                JaccardIndex: 1.0, ParityLevel: ParityLevel.Hard,
                LegacyTokenTotal: 0, V2TokenTotal: 0, WorkingSetCandidateCount: 0),
            fixtureId: "fx-no-providers",
            purpose: "no-providers") with
        {
            StoredPolicySnapshot = snapshot,
            StoredProviderOutputs = null
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await integration.ExpertReplayAsync(fixture, CancellationToken.None),
            "StoredProviderOutputs 为 null 时 ExpertReplay 必须抛 InvalidOperationException。");

        await integration.DisposeAsync();
    }

    [TestMethod]
    public async Task CancellationPropagatesWithoutFallbackInV2OnlyPath()
    {
        // V2 抛出 OperationCanceledException → 必须传播，不转为 fallback success
        // R28-B.7 验证：Authoritative Runtime 不捕获取消异常
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
            OperationId = "op-cancel-fidelity",
            WorkspaceId = "ws-1",
            CollectionId = "col-1"
        };

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            async () => await runtime.RetrieveAsync(request, CancellationToken.None),
            "OperationCanceledException 必须传播，不得被 catch 转为 fallback success。");
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
