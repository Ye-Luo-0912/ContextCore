using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextCore.Tests;

// ===========================================================================
// Selected-only Hydration（Perf-2）验收测试
//
// 目标路径：轻量召回 → Score/Allocate → 只 hydrate Selected → 精确 tokenize
//           → 正式 Decision Repair → Model Projection
// 本测试验证 Perf-2 的"只 hydrate Selected"基础设施：
//   1. Mandatory RequiredIds 路径：IncludeContent=false 时走 IContextStoreMetadataLookup
//      （BatchGetMetadataAsync），不调用全量 BatchGetAsync（避免未选中正文 jsonb 入内存）
//   2. Mandatory RequiredIds 路径：IncludeContent=true 时走 IContextStoreBatchLookup（全量批量）
//   3. Semantic Provider：memory hits + IncludeContent=false 时走 IMemoryStoreMetadataLookup
//   4. Semantic Provider：memory hits + IncludeContent=true 时走 IMemoryStoreBatchLookup
//   5. Graph Provider：邻居 hydration + IncludeContent=false 时走 IContextStoreMetadataLookup
//   6. WorkingMemory Provider：IncludeContent=false 透传到 ContextMemoryQuery.IncludeContent
//   7. BuildFromMemoryItem：metadata-only（Content 空 + 持久化 content_length）→ 精确长度估算
//      （避免 token 估算退化为 1）
//   8. Projector：hydrated material 带精确 TokenCost 时 TotalTokens 使用精确 ContentTokens
//   9. Projector：无 TokenCost 时回退整体长度估算（兼容降级路径）
//
// 设计原则：
//   - 使用 RecordingContextStore / RecordingMemoryStore 记录调用路径，精确断言分支选择
//   - 复用 R29D 的 MakeSnapshot / MakeContext 构建模式
//   - 所有代码注释使用中文
// ===========================================================================

/// <summary>
/// Selected-only Hydration（Perf-2）验收测试。
/// </summary>
[TestClass]
[TestCategory("R29")]
[TestCategory("DecisionEngine")]
public sealed class R29H_SelectedOnlyHydrationTests
{
    // =======================================================================
    // 辅助：构建 Policy 快照 / Provider 上下文
    // =======================================================================

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
    private static CandidateProviderContext MakeContext(
        RetrievalExpert expert,
        bool includeContent = true,
        IReadOnlyList<string>? requiredIds = null,
        IReadOnlyList<float>? queryVector = null,
        IReadOnlyList<ContextCandidateEnvelope>? seedCandidates = null)
    {
        var snapshot = MakeSnapshot();
        return new CandidateProviderContext(
            Request: new ContextDecisionRuntimeRequest
            {
                RequestId = "req-soh",
                Scope = new ContextDecisionScope("test-ws", "test-col"),
                Purpose = ContextDecisionPurpose.Retrieval,
                TokenBudget = 4096,
                TopK = 10,
                SeedCandidates = seedCandidates ?? Array.Empty<ContextCandidateEnvelope>(),
                RetrievalInput = new RetrievalInput
                {
                    IncludeContent = includeContent,
                    RequiredIds = requiredIds ?? Array.Empty<string>(),
                    QueryVector = queryVector ?? Array.Empty<float>()
                }
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

    /// <summary>构建 metadata-only 的 ContextItem（Content 空 + 持久化 content_length）。</summary>
    private static ContextItem MakeMetadataOnlyItem(string id, int contentLength) => new()
    {
        Id = id,
        WorkspaceId = "test-ws",
        CollectionId = "test-col",
        Content = string.Empty,
        Type = "note",
        Metadata = new Dictionary<string, string>
        {
            [ContentMetadataKeys.ContentLength] = contentLength.ToString()
        }
    };

    /// <summary>构建 metadata-only 的 ContextMemoryItem（Content 空 + 持久化 content_length）。</summary>
    private static ContextMemoryItem MakeMetadataOnlyMemory(string id, int contentLength) => new()
    {
        Id = id,
        WorkspaceId = "test-ws",
        CollectionId = "test-col",
        Layer = ContextMemoryLayer.Working,
        Status = ContextMemoryStatus.Candidate,
        Content = string.Empty,
        Type = "memory",
        Metadata = new Dictionary<string, string>
        {
            [ContentMetadataKeys.ContentLength] = contentLength.ToString()
        }
    };

    // =======================================================================
    // 1. Mandatory RequiredIds：IncludeContent=false → 元数据批量
    // =======================================================================

    [TestMethod]
    public async Task MandatoryRequiredIds_IncludeContentFalse_UsesMetadataBatch()
    {
        // IncludeContent=false：RequiredIds 路径应走 IContextStoreMetadataLookup（BatchGetMetadataAsync），
        // 不调用全量 BatchGetAsync，避免把未选中候选的正文 jsonb 读入内存。
        var store = new RecordingContextStore(
            metadataItems: new[] { MakeMetadataOnlyItem("m-1", 80), MakeMetadataOnlyItem("m-2", 80) });
        var provider = new MandatoryCandidateProvider(store, tokenizerResolver: null);

        var result = await provider.ExecuteAsync(MakeContext(
            RetrievalExpert.Mandatory, includeContent: false, requiredIds: new[] { "m-1", "m-2" }));

        Assert.IsTrue(store.MetadataBatchCalled, "IncludeContent=false 时应调用 BatchGetMetadataAsync。");
        Assert.IsFalse(store.FullBatchCalled, "IncludeContent=false 时不应调用全量 BatchGetAsync。");
        Assert.AreEqual(2, result.Envelopes.Count, "应召回 2 个强制候选。");
        Assert.AreEqual(2, result.Materials.Count, "应产出 2 个 Material sidecar。");
        foreach (var material in result.Materials.Values)
        {
            Assert.AreEqual(string.Empty, material.Content,
                "metadata-only 召回路径 Material.Content 必须为空（正文由 ISelectedCandidateHydrator 二次读取）。");
        }
        // metadata 持久化 content_length=80 → 估算 80/4=20 token（而非退化为 1）
        Assert.AreEqual(20, result.Envelopes[0].TokenCost!.ContentTokens,
            "ContentTokens 应基于持久化 content_length 估算（80/4=20），而非退化为 1。");
        Assert.IsTrue(result.Envelopes[0].TokenCost!.IsEstimated,
            "metadata-only 路径无 tokenizer 精确计算，TokenCost 应为估算。");
    }

    [TestMethod]
    public async Task MandatoryRequiredIds_IncludeContentTrue_UsesFullBatch()
    {
        // IncludeContent=true：RequiredIds 路径应走 IContextStoreBatchLookup（BatchGetAsync 全量批量），
        // 保留完整正文（旧行为不变）。
        var store = new RecordingContextStore(
            fullItems: new[]
            {
                MakeItem("m-1", "full content one"),
                MakeItem("m-2", "full content two")
            });
        var tokenizer = new DefaultContextTokenizerResolver();
        var provider = new MandatoryCandidateProvider(store, tokenizerResolver: tokenizer);

        var result = await provider.ExecuteAsync(MakeContext(
            RetrievalExpert.Mandatory, includeContent: true, requiredIds: new[] { "m-1", "m-2" }));

        Assert.IsTrue(store.FullBatchCalled, "IncludeContent=true 时应调用全量 BatchGetAsync。");
        Assert.IsFalse(store.MetadataBatchCalled, "IncludeContent=true 时不应调用 BatchGetMetadataAsync。");
        Assert.AreEqual(2, result.Envelopes.Count, "应召回 2 个强制候选。");
        foreach (var material in result.Materials.Values)
        {
            Assert.IsFalse(string.IsNullOrEmpty(material.Content),
                "IncludeContent=true 时 Material.Content 必须保留完整正文。");
        }
        Assert.IsFalse(result.Envelopes[0].TokenCost!.IsEstimated,
            "IncludeContent=true + tokenizer 可用时 TokenCost 应为精确计算（非估算）。");
    }

    // =======================================================================
    // 3/4. Semantic Provider：memory hits 的 metadata / full 批量路径
    // =======================================================================

    [TestMethod]
    public async Task SemanticProvider_MemoryHits_IncludeContentFalse_UsesMetadataBatch()
    {
        // Semantic Provider：memory hits + IncludeContent=false → IMemoryStoreMetadataLookup。
        // 上下文正文不被读取，Material.Content 为空。
        var contextStore = new RecordingContextStore();
        var memoryStore = new RecordingMemoryStore(
            metadataItems: new[] { MakeMetadataOnlyMemory("mem-1", 80), MakeMetadataOnlyMemory("mem-2", 80) });
        var vectorStore = new FakeVectorStore(
            new[]
            {
                MakeMemoryHit("mem-1"),
                MakeMemoryHit("mem-2")
            });
        var provider = new SemanticCandidateProvider(
            contextStore, memoryStore: memoryStore, embeddingProvider: null,
            vectorStore: vectorStore, tokenizerResolver: null);

        var result = await provider.ExecuteAsync(MakeContext(
            RetrievalExpert.Semantic, includeContent: false, queryVector: new[] { 0.1f, 0.2f }));

        Assert.IsTrue(memoryStore.MetadataBatchCalled, "IncludeContent=false 时 memory 应走 BatchGetMetadataAsync。");
        Assert.IsFalse(memoryStore.FullBatchCalled, "IncludeContent=false 时 memory 不应走全量 BatchGetAsync。");
        Assert.AreEqual(2, result.Envelopes.Count, "应召回 2 个语义候选。");
        foreach (var material in result.Materials.Values)
        {
            Assert.AreEqual(string.Empty, material.Content,
                "metadata-only 召回路径 Material.Content 必须为空。");
        }
    }

    [TestMethod]
    public async Task SemanticProvider_MemoryHits_IncludeContentTrue_UsesFullBatch()
    {
        // Semantic Provider：memory hits + IncludeContent=true → IMemoryStoreBatchLookup（全量批量）。
        var contextStore = new RecordingContextStore();
        var memoryStore = new RecordingMemoryStore(
            fullItems: new[]
            {
                MakeMemory("mem-1", "semantic memory content one"),
                MakeMemory("mem-2", "semantic memory content two")
            });
        var vectorStore = new FakeVectorStore(
            new[]
            {
                MakeMemoryHit("mem-1"),
                MakeMemoryHit("mem-2")
            });
        var tokenizer = new DefaultContextTokenizerResolver();
        var provider = new SemanticCandidateProvider(
            contextStore, memoryStore: memoryStore, embeddingProvider: null,
            vectorStore: vectorStore, tokenizerResolver: tokenizer);

        var result = await provider.ExecuteAsync(MakeContext(
            RetrievalExpert.Semantic, includeContent: true, queryVector: new[] { 0.1f, 0.2f }));

        Assert.IsTrue(memoryStore.FullBatchCalled, "IncludeContent=true 时 memory 应走全量 BatchGetAsync。");
        Assert.IsFalse(memoryStore.MetadataBatchCalled, "IncludeContent=true 时 memory 不应走 BatchGetMetadataAsync。");
        Assert.AreEqual(2, result.Envelopes.Count, "应召回 2 个语义候选。");
        foreach (var material in result.Materials.Values)
        {
            Assert.IsFalse(string.IsNullOrEmpty(material.Content),
                "IncludeContent=true 时 Material.Content 必须保留完整正文。");
        }
    }

    // =======================================================================
    // 5. Graph Provider：邻居 hydration 的 metadata 路径
    // =======================================================================

    [TestMethod]
    public async Task GraphProvider_IncludeContentFalse_UsesMetadataBatch()
    {
        // Graph Provider：1 个种子 → 2 个邻居 → IncludeContent=false 时邻居 hydration
        // 走 IContextStoreMetadataLookup（BatchGetMetadataAsync），不读正文 jsonb。
        var contextStore = new RecordingContextStore(
            metadataItems: new[] { MakeMetadataOnlyItem("n-1", 80), MakeMetadataOnlyItem("n-2", 80) });
        var relationStore = new FakeRelationStore(
            new[]
            {
                MakeRelation("r-1", "seed-1", "n-1"),
                MakeRelation("r-2", "seed-1", "n-2")
            });
        var seed = new ContextCandidateEnvelope
        {
            CandidateId = "seed-1",
            Source = ContextCandidateSource.Mandatory,
            CanonicalKey = CanonicalCandidateKey.Create("test-ws", "test-col", "context", "seed-1", "v1")
        };
        var provider = new GraphCandidateProvider(
            contextStore, relationStore: relationStore, memoryStore: null, tokenizerResolver: null);

        var result = await provider.ExecuteAsync(MakeContext(
            RetrievalExpert.Graph, includeContent: false, seedCandidates: new[] { seed }));

        Assert.IsTrue(contextStore.MetadataBatchCalled,
            "IncludeContent=false 时 Graph 邻居 hydration 应走 BatchGetMetadataAsync。");
        Assert.IsFalse(contextStore.FullBatchCalled,
            "IncludeContent=false 时 Graph 邻居 hydration 不应走全量 BatchGetAsync。");
        Assert.AreEqual(2, result.Envelopes.Count, "应召回 2 个图邻居候选。");
        foreach (var material in result.Materials.Values)
        {
            Assert.AreEqual(string.Empty, material.Content,
                "metadata-only 召回路径 Material.Content 必须为空。");
        }
    }

    [TestMethod]
    public async Task GraphProvider_SingleSeed_UsesBatchPath()
    {
        // frontier 为单种子时也必须走 QueryNeighborsBatchAsync（保证全局 LIMIT 下推在存储层生效），
        // 不得回退到无全局上限的 QueryNeighborsAsync 单条路径。
        var contextStore = new RecordingContextStore(
            metadataItems: new[] { MakeMetadataOnlyItem("n-1", 80), MakeMetadataOnlyItem("n-2", 80) });
        var relationStore = new FakeRelationStore(
            new[]
            {
                MakeRelation("r-1", "seed-1", "n-1"),
                MakeRelation("r-2", "seed-1", "n-2")
            });
        var seed = new ContextCandidateEnvelope
        {
            CandidateId = "seed-1",
            Source = ContextCandidateSource.Mandatory,
            CanonicalKey = CanonicalCandidateKey.Create("test-ws", "test-col", "context", "seed-1", "v1")
        };
        var provider = new GraphCandidateProvider(
            contextStore, relationStore: relationStore, memoryStore: null, tokenizerResolver: null);

        var result = await provider.ExecuteAsync(MakeContext(
            RetrievalExpert.Graph, includeContent: false, seedCandidates: new[] { seed }));

        Assert.AreEqual(1, relationStore.BatchQueryCalls, "单种子也必须走批量邻居查询。");
        Assert.AreEqual(0, relationStore.SingleQueryCalls, "不得回退到 QueryNeighborsAsync 单条路径。");
        Assert.AreEqual(2, result.Envelopes.Count, "应召回 2 个图邻居候选。");
    }

    // =======================================================================
    // 6. WorkingMemory Provider：IncludeContent 透传
    // =======================================================================

    [TestMethod]
    public async Task WorkingMemoryProvider_IncludeContentFalse_PassesQueryFlag()
    {
        // WorkingMemory Provider 将 IncludeContent=false 透传到 ContextMemoryQuery，
        // store 只投影元数据列（Content 为空），正文由 ISelectedCandidateHydrator 二次读取。
        var memoryStore = new RecordingMemoryStore(
            metadataItems: new[] { MakeMetadataOnlyMemory("mem-1", 80) });
        var provider = new WorkingMemoryCandidateProvider(memoryStore, tokenizerResolver: null);

        var result = await provider.ExecuteAsync(MakeContext(
            RetrievalExpert.WorkingMemory, includeContent: false));

        Assert.IsNotNull(memoryStore.LastQuery, "Provider 应调用 memory store 的 QueryAsync。");
        Assert.IsFalse(memoryStore.LastQuery!.IncludeContent,
            "ContextMemoryQuery.IncludeContent 应透传 false。");
        Assert.AreEqual(1, result.Envelopes.Count, "应召回 1 个工作记忆候选。");
        Assert.AreEqual(string.Empty, result.Materials.Values.Single().Content,
            "IncludeContent=false 时 Material.Content 必须为空。");
        Assert.AreEqual(20, result.Envelopes[0].TokenCost!.ContentTokens,
            "ContentTokens 应基于持久化 content_length 估算（80/4=20）。");
    }

    [TestMethod]
    public async Task WorkingMemoryProvider_IncludeContentTrue_PassesQueryFlag()
    {
        // 反向验证：IncludeContent=true 时 ContextMemoryQuery.IncludeContent 保持 true（默认行为不变）。
        var memoryStore = new RecordingMemoryStore(
            fullItems: new[] { MakeMemory("mem-1", "working memory content") });
        var tokenizer = new DefaultContextTokenizerResolver();
        var provider = new WorkingMemoryCandidateProvider(memoryStore, tokenizerResolver: tokenizer);

        var result = await provider.ExecuteAsync(MakeContext(
            RetrievalExpert.WorkingMemory, includeContent: true));

        Assert.IsNotNull(memoryStore.LastQuery, "Provider 应调用 memory store 的 QueryAsync。");
        Assert.IsTrue(memoryStore.LastQuery!.IncludeContent,
            "ContextMemoryQuery.IncludeContent 应透传 true。");
        Assert.IsFalse(string.IsNullOrEmpty(result.Materials.Values.Single().Content),
            "IncludeContent=true 时 Material.Content 必须保留完整正文。");
    }

    // =======================================================================
    // 7. BuildFromMemoryItem：metadata-only 的 content_length 估算
    // =======================================================================

    [TestMethod]
    public void BuildFromMemoryItem_MetadataOnly_UsesContentLengthEstimate()
    {
        // metadata-only 路径：Content 为空 + Metadata 持久化 content_length=80。
        // 估算长度应回退到持久化值 → 80/4=20 token，而非退化为 1。
        var memory = MakeMetadataOnlyMemory("mem-1", 80);
        var (envelope, material) = CandidateProviderHelpers.BuildFromMemoryItem(
            memory,
            ContextCandidateSource.StableMemory,
            ExpertKind.StableMemory,
            50.0,
            new CandidateAdaptationContext
            {
                WorkspaceId = "test-ws",
                CollectionId = "test-col",
                ObservedAt = DateTimeOffset.UtcNow
            },
            includeContent: false);

        Assert.AreEqual(string.Empty, material.Content, "IncludeContent=false 时 Material.Content 必须为空。");
        Assert.IsNotNull(envelope.TokenCost, "metadata-only 路径也应填充 TokenCost。");
        Assert.AreEqual(20, envelope.TokenCost!.ContentTokens,
            "ContentTokens 应基于持久化 content_length 估算（80/4=20），而非退化为 1。");
        Assert.IsTrue(envelope.TokenCost!.IsEstimated, "无 tokenizer 精确计算，TokenCost 应为估算。");
    }

    // =======================================================================
    // 8/9. Projector：精确 TokenCost 消费
    // =======================================================================

    [TestMethod]
    public void Projector_UsesExactMaterialTokens_ForHydratedMaterials()
    {
        // hydrated material 带精确 TokenCost（ISelectedCandidateHydrator 用 tokenizer 重算）：
        // TotalTokens = 精确 ContentTokens(5) + 固定包装前缀估算(21) = 26。
        // 若回退整体长度估算则为 38（76 字符 → (76+1)/2），精确路径显著不同。
        const string content = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; // 35 字符
        var key = CanonicalCandidateKey.Create("test-ws", "test-col", "context", "ctx-1", "v1");
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = "mandatory:ctx-1",
            Source = ContextCandidateSource.Mandatory,
            Type = "note",
            CanonicalKey = key,
            Utility = new CandidateUtilityScore { FinalScore = 1000.0 }
        };
        var material = new CandidateMaterial
        {
            Key = key,
            Content = content,
            NativeKind = "note",
            TokenCost = new CandidateTokenCost
            {
                ContentTokens = 5,
                TokenizerId = "test-tokenizer",
                IsEstimated = false
            }
        };

        var projection = Project(envelope, material);

        // 前缀 = "[untrusted_data]\n[RetrievedContext:note]\n"（41 字符）→ (41+1)/2 = 21 token
        Assert.AreEqual(26, projection.TotalTokens,
            "TotalTokens 应使用精确 ContentTokens(5) + 前缀估算(21)，而非整体长度估算。");
        Assert.AreEqual(1, projection.Messages.Count, "应投影 1 条检索材料消息。");
        Assert.IsTrue(projection.SelectedMaterialIds.Contains("mandatory:ctx-1"),
            "SelectedMaterialIds 应记录取到正文的候选。");
    }

    [TestMethod]
    public void Projector_FallsBackToLengthEstimate_WithoutTokenCost()
    {
        // 无精确 TokenCost（测试 stub / 降级路径）→ 回退整体长度估算（与旧行为一致）。
        // 消息 Content = 前缀(41) + 正文(35) = 76 字符 → (76+1)/2 = 38 token。
        const string content = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; // 35 字符
        var key = CanonicalCandidateKey.Create("test-ws", "test-col", "context", "ctx-1", "v1");
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = "mandatory:ctx-1",
            Source = ContextCandidateSource.Mandatory,
            Type = "note",
            CanonicalKey = key,
            Utility = new CandidateUtilityScore { FinalScore = 1000.0 }
        };
        var material = new CandidateMaterial
        {
            Key = key,
            Content = content,
            NativeKind = "note"
        };

        var projection = Project(envelope, material);

        Assert.AreEqual(38, projection.TotalTokens,
            "无 TokenCost 时应回退整体长度估算（76 字符 → 38 token）。");
    }

    /// <summary>构建投影输入并执行投影（空上下文 + 无限预算，隔离检索材料 token 计算）。</summary>
    private static AgentModelContextProjection Project(
        ContextCandidateEnvelope envelope,
        CandidateMaterial material)
    {
        var snapshot = MakeSnapshot();
        var decision = new ContextDecisionResult
        {
            RequestId = "req-soh",
            SelectedEnvelopes = new[] { envelope }
        };
        var execResult = new ContextDecisionExecutionResult
        {
            Decision = decision,
            WorkingSet = new CandidateWorkingSet
            {
                Envelopes = new[] { envelope },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial> { [envelope.CanonicalKey] = material }
            },
            Policy = snapshot,
            Routing = new ExpertRoutingDecisionSet
            {
                Decisions = Array.Empty<ExpertRoutingDecision>()
            }
        };
        var run = new AgentRun
        {
            RunId = "run-soh",
            WorkspaceId = "test-ws",
            SessionId = "ses-soh",
            Task = "soh test",
            State = AgentRunState.ContextBuilding,
            Turn = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var projector = new DefaultAgentModelContextProjector();
        return projector.Project(run, execResult, new AgentContextState(), modelContextTokenBudget: 0);
    }

    // =======================================================================
    // 辅助：测试数据工厂
    // =======================================================================

    private static ContextItem MakeItem(string id, string content) => new()
    {
        Id = id,
        WorkspaceId = "test-ws",
        CollectionId = "test-col",
        Content = content,
        Type = "note"
    };

    private static ContextMemoryItem MakeMemory(string id, string content) => new()
    {
        Id = id,
        WorkspaceId = "test-ws",
        CollectionId = "test-col",
        Layer = ContextMemoryLayer.Working,
        Status = ContextMemoryStatus.Candidate,
        Content = content,
        Type = "memory"
    };

    private static VectorSearchResult MakeMemoryHit(string sourceId) => new()
    {
        Record = new VectorRecord
        {
            Id = "vec-" + sourceId,
            WorkspaceId = "test-ws",
            CollectionId = "test-col",
            SourceId = sourceId,
            SourceKind = "memory",
            Dimensions = 2,
            Vector = new[] { 0.1f, 0.2f }
        },
        Score = 0.9,
        Rank = 1
    };

    private static ContextRelation MakeRelation(string id, string sourceId, string targetId) => new()
    {
        Id = id,
        WorkspaceId = "test-ws",
        CollectionId = "test-col",
        SourceId = sourceId,
        TargetId = targetId,
        RelationType = "references",
        Weight = 1.0,
        Confidence = 1.0
    };

    // =======================================================================
    // RecordingContextStore：实现 IContextStore + 批量能力，记录调用路径
    // =======================================================================

    private sealed class RecordingContextStore : IContextStore, IContextStoreBatchLookup, IContextStoreMetadataLookup
    {
        private readonly IReadOnlyList<ContextItem> _metadataItems;
        private readonly IReadOnlyList<ContextItem> _fullItems;

        internal bool MetadataBatchCalled { get; private set; }
        internal bool FullBatchCalled { get; private set; }

        internal RecordingContextStore(
            IReadOnlyList<ContextItem>? metadataItems = null,
            IReadOnlyList<ContextItem>? fullItems = null)
        {
            _metadataItems = metadataItems ?? Array.Empty<ContextItem>();
            _fullItems = fullItems ?? Array.Empty<ContextItem>();
        }

        public Task<IReadOnlyList<ContextItem>> BatchGetMetadataAsync(
            string workspaceId, string collectionId, IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
        {
            MetadataBatchCalled = true;
            return Task.FromResult(_metadataItems);
        }

        public Task<IReadOnlyList<ContextItem>> BatchGetAsync(
            string workspaceId, string collectionId, IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
        {
            FullBatchCalled = true;
            return Task.FromResult(_fullItems);
        }

        public Task<IReadOnlyList<ContextItem>> QueryAsync(
            ContextQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextItem>>(Array.Empty<ContextItem>());

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
    }

    // =======================================================================
    // RecordingMemoryStore：实现 IMemoryStore + 批量能力，记录调用路径
    // =======================================================================

    private sealed class RecordingMemoryStore : IMemoryStore, IMemoryStoreBatchLookup, IMemoryStoreMetadataLookup
    {
        private readonly IReadOnlyList<ContextMemoryItem> _metadataItems;
        private readonly IReadOnlyList<ContextMemoryItem> _fullItems;

        internal bool MetadataBatchCalled { get; private set; }
        internal bool FullBatchCalled { get; private set; }
        internal ContextMemoryQuery? LastQuery { get; private set; }

        internal RecordingMemoryStore(
            IReadOnlyList<ContextMemoryItem>? metadataItems = null,
            IReadOnlyList<ContextMemoryItem>? fullItems = null)
        {
            _metadataItems = metadataItems ?? Array.Empty<ContextMemoryItem>();
            _fullItems = fullItems ?? Array.Empty<ContextMemoryItem>();
        }

        public Task<IReadOnlyList<ContextMemoryItem>> BatchGetMetadataAsync(
            string workspaceId, string collectionId, IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
        {
            MetadataBatchCalled = true;
            return Task.FromResult(_metadataItems);
        }

        public Task<IReadOnlyList<ContextMemoryItem>> BatchGetAsync(
            string workspaceId, string collectionId, IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
        {
            FullBatchCalled = true;
            return Task.FromResult(_fullItems);
        }

        public Task<IReadOnlyList<ContextMemoryItem>> QueryAsync(
            ContextMemoryQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            // 模拟 PostgresMemoryStore：IncludeContent=false 时只投影元数据（Content 为空）
            return Task.FromResult(query.IncludeContent ? _fullItems : _metadataItems);
        }

        public Task<ContextMemoryItem?> GetAsync(
            string workspaceId, string collectionId, string id,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ContextMemoryItem?>(null);

        public Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateStatusAsync(
            string workspaceId, string collectionId, string id, ContextMemoryStatus status,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // =======================================================================
    // FakeVectorStore：返回配置的向量命中
    // =======================================================================

    private sealed class FakeVectorStore : IVectorStore
    {
        private readonly IReadOnlyList<VectorSearchResult> _hits;

        internal FakeVectorStore(IReadOnlyList<VectorSearchResult> hits) => _hits = hits;

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
            VectorQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(_hits);

        public Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本测试不写入向量。");

        public Task<VectorRecord?> GetAsync(
            string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本测试不单条读取向量。");

        public Task DeleteAsync(
            string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本测试不删除向量。");
    }

    // =======================================================================
    // FakeRelationStore：返回配置的邻居关系
    // =======================================================================

    private sealed class FakeRelationStore : IRelationStore
    {
        private readonly IReadOnlyList<ContextRelation> _relations;

        internal int SingleQueryCalls;
        internal int BatchQueryCalls;
        internal RelationNeighborBatchQuery? LastBatchQuery;

        internal FakeRelationStore(IReadOnlyList<ContextRelation> relations) => _relations = relations;

        public Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(
            RelationNeighborQuery query, CancellationToken cancellationToken = default)
        {
            SingleQueryCalls++;
            return Task.FromResult(_relations);
        }

        public Task<IReadOnlyList<ContextRelation>> QueryAsync(
            ContextRelationQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(_relations);

        public Task<IReadOnlyList<RelationNeighborBatchResult>> QueryNeighborsBatchAsync(
            RelationNeighborBatchQuery query, CancellationToken cancellationToken = default)
        {
            BatchQueryCalls++;
            LastBatchQuery = query;
            // 按种子过滤，构造与 QueryNeighborsAsync 等价的批量结果（Both 方向）。
            var results = new List<RelationNeighborBatchResult>();
            foreach (var seed in query.ItemIds)
            {
                var bucket = _relations
                    .Where(r => string.Equals(r.SourceId, seed, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.TargetId, seed, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (bucket.Length == 0)
                {
                    continue;
                }
                results.Add(new RelationNeighborBatchResult
                {
                    ItemId = seed,
                    Relations = bucket,
                    Truncated = false
                });
            }
            return Task.FromResult<IReadOnlyList<RelationNeighborBatchResult>>(results);
        }

        public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本测试不写入关系。");

        public Task<ContextRelation?> GetAsync(
            string workspaceId, string collectionId, string relationId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本测试不单条读取关系。");

        public Task<bool> DeleteAsync(
            string workspaceId, string collectionId, string relationId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本测试不删除关系。");

        public Task BatchUpsertAsync(
            IEnumerable<ContextRelation> relations, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本测试不批量写入关系。");
    }
}
