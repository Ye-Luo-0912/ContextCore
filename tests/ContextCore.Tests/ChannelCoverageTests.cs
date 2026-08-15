using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.DecisionEngine.FlowDiagnostics;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 通道覆盖决策契约测试。
/// 覆盖：五个召回通道（Lexical / Semantic / Graph / WorkingMemory / StableMemory）在各自
/// 存储就绪时都有独立质量贡献（独占文档唯一命中），且不串扰其他通道（精度不下降）；
/// 成本有界（embedding 单次批量、向量单次多问句、词法单次多问句、图单次邻居批量、
/// 记忆每层一次查询）；缺存储时通道为空但不降级；单通道失败只降级自身、不影响其他通道；
/// 跨通道重复候选在合并层只保留一条。
/// </summary>
[TestClass]
[TestCategory("LR2E")]
[TestCategory("Retrieval")]
public sealed class ChannelCoverageTests
{
    private const string Ws = "ws";
    private const string Col = "col";
    private const string Q1 = "分布式事务提交";
    private const string Q2 = "coordination across nodes";

    // 语义簇方向 e0：两个问句与仅语义文档共享；其余文档用正交基向量，保证语义通道不串扰。
    private static readonly float[] SemCluster = BasisVector(0);
    private static readonly float[] LexVector = BasisVector(1);
    private static readonly float[] GraphVector = BasisVector(2);

    // ── 全通道世界：唯一命中 + 成本 + 可观测性 ────────────────────────────────

    /// <summary>
    /// 验证：五个通道各自存储就绪时，每个通道的独占文档只被该通道召回（唯一命中），
    /// 全部入选且不重复；外部调用有界（不随问句数线性放大）；诊断报告给出
    /// 每通道 Produced / Unique / Selected，且本世界无跨通道重复候选。
    /// </summary>
    [TestMethod]
    public async Task Channel_Coverage_AllChannels_UniqueHitsAndCosts()
    {
        var context = new CountingContextStore(new InMemoryContextStore());
        var memory = new CountingMemoryStore(new InMemoryMemoryStore());
        var relations = new CountingRelationStore(new InMemoryRelationStore());
        var vectors = new CountingVectorStore(new InMemoryVectorStore());
        var embeddings = new CodebookEmbeddingProvider();
        embeddings.Map(Q1, SemCluster);
        embeddings.Map(Q2, SemCluster);

        await PopulateWorldAsync(context, memory, relations, vectors);

        var runtime = BuildRuntime(BuildRecallProviders(context, memory, relations, vectors, embeddings));
        var result = await runtime.ExecuteWithWorkingSetAsync(
            BuildRequest("req-channel-coverage"), CancellationToken.None);

        // 1. 每个通道产出且只产出自己的独占文档。
        var byKind = result.ProviderOutputSnapshots.ToDictionary(s => s.Kind, s => s.Envelopes);
        AssertIds(byKind[ExpertKind.Lexical], "lex-only");
        AssertIds(byKind[ExpertKind.Semantic], "sem-only");
        AssertIds(byKind[ExpertKind.Graph], "graph-only");
        AssertIds(byKind[ExpertKind.WorkingMemory], "work-only");
        AssertIds(byKind[ExpertKind.StableMemory], "stable-only");

        // 2. 实体级唯一命中：每个独占文档恰好被一个通道产出（无跨通道串扰）。
        var producedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in result.ProviderOutputSnapshots)
        {
            foreach (var env in snapshot.Envelopes)
            {
                var entityId = env.CanonicalKey.EntityId;
                producedBy[entityId] = producedBy.GetValueOrDefault(entityId) + 1;
            }
        }
        foreach (var id in new[] { "lex-only", "sem-only", "graph-only", "work-only", "stable-only" })
        {
            Assert.AreEqual(1, producedBy.GetValueOrDefault(id), $"文档 {id} 应只被一个通道唯一命中。");
        }

        // 3. 全部入选且不重复。
        var selected = result.Decision.SelectedEnvelopes.Select(e => e.CanonicalKey.EntityId).ToArray();
        Assert.AreEqual(5, selected.Length, "独占世界应恰好选中五条候选。");
        CollectionAssert.AreEquivalent(new[] { "lex-only", "sem-only", "graph-only", "work-only", "stable-only" }, selected);

        // 4. 成本有界：外部调用不随问句数线性放大。
        Assert.AreEqual(1, context.MultiQueryCalls, "Lexical 两问句应单次多问句读取。");
        Assert.AreEqual(0, context.QueryCalls, "Lexical 不应走逐条查询。");
        Assert.AreEqual(1, embeddings.CallCount, "Semantic 两问句应单次批量 embedding。");
        Assert.AreEqual(2, embeddings.InputCount, "批量 embedding 应覆盖两条问句。");
        Assert.AreEqual(1, vectors.MultiSearchCalls, "Semantic 两问句应单次多问句向量检索。");
        Assert.AreEqual(0, vectors.SearchCalls, "Semantic 不应走逐条向量检索。");
        Assert.AreEqual(1, relations.NeighborBatchCalls, "Graph 单跳扩展应单次邻居批量查询。");
        Assert.AreEqual(2, memory.QueryCalls, "WorkingMemory 与 StableMemory 各一次查询。");

        // 5. 可观测性：候选流诊断报告给出每通道命中摘要（诊断报告即通道覆盖的数据源）。
        var report = CandidatesFlowDiagnosticBuilder.Build(BuildRequest("req-channel-coverage"), result);
        var channels = report.Channels.ToDictionary(c => c.Channel, StringComparer.Ordinal);
        foreach (var name in new[] { "Lexical", "Semantic", "WorkingMemory", "StableMemory", "Graph" })
        {
            Assert.IsTrue(channels.TryGetValue(name, out var ch), $"通道 {name} 应出现在诊断报告。");
            Assert.AreEqual(1, ch.Produced, $"{name} 应产出 1 个候选。");
            Assert.AreEqual(1, ch.Unique, $"{name} 应有 1 个唯一命中（独立贡献）。");
            Assert.AreEqual(1, ch.Selected, $"{name} 的唯一命中应被选中。");
        }
        Assert.AreEqual(0, report.Duplicates.Count, "独占世界不应有跨通道重复候选。");
    }

    // ── 缺存储：预期空，不是降级 ─────────────────────────────────────────────

    /// <summary>
    /// 验证：向量 / 关系 / 记忆存储未配置时，对应通道为空但不标记降级、
    /// 不报错、不影响词法通道；这是「有贡献但默认不可用」通道的生产语义：
    /// 配好存储即启用，未配置即预期空。
    /// </summary>
    [TestMethod]
    public async Task Channel_DefaultOff_StoresNull_EmptyButNotDegraded()
    {
        var context = new InMemoryContextStore();
        await PopulateWorldAsync(context, memory: null, relations: null, vectors: null);

        var runtime = BuildRuntime(BuildRecallProviders(context, memoryStore: null, relationStore: null, vectorStore: null, embeddingProvider: null));
        var result = await runtime.ExecuteWithWorkingSetAsync(
            BuildRequest("req-channel-default-off"), CancellationToken.None);

        foreach (var kind in new[] { ExpertKind.Semantic, ExpertKind.Graph, ExpertKind.WorkingMemory, ExpertKind.StableMemory })
        {
            var snapshot = result.ProviderOutputSnapshots.FirstOrDefault(s => s.Kind == kind);
            Assert.IsTrue(snapshot is null || snapshot.Envelopes.Count == 0, $"{kind} 缺存储时应为空。");
        }

        AssertIds(result.ProviderOutputSnapshots.Single(s => s.Kind == ExpertKind.Lexical).Envelopes, "lex-only");
        Assert.IsFalse(result.IsDegraded, "通道缺存储是预期空，不应标记降级。");
        Assert.IsTrue(
            result.Decision.SelectedEnvelopes.Any(e => e.CanonicalKey.EntityId == "lex-only"),
            "词法通道命中仍应被选中。");
    }

    // ── 单通道失败：只降级自身 ───────────────────────────────────────────────

    /// <summary>
    /// 验证：记忆存储不可用时，WorkingMemory / StableMemory 标记失败并降级，
    /// 但词法通道照常召回，请求整体成功——单通道故障不拖垮其他通道。
    /// </summary>
    [TestMethod]
    public async Task Channel_ProviderFailure_DegradesOnlyThatChannel()
    {
        var context = new InMemoryContextStore();
        await PopulateWorldAsync(context, memory: null, relations: null, vectors: null);

        var runtime = BuildRuntime(BuildRecallProviders(context, new ThrowingMemoryStore(), relationStore: null, vectorStore: null, embeddingProvider: null));
        var result = await runtime.ExecuteWithWorkingSetAsync(
            BuildRequest("req-channel-degraded"), CancellationToken.None);

        Assert.IsTrue(result.IsDegraded, "记忆通道失败应标记降级。");
        var failed = result.ProviderReports.Where(r => !r.Succeeded).Select(r => r.Kind).ToArray();
        CollectionAssert.AreEquivalent(new[] { ExpertKind.WorkingMemory, ExpertKind.StableMemory }, failed);
        AssertIds(result.ProviderOutputSnapshots.Single(s => s.Kind == ExpertKind.Lexical).Envelopes, "lex-only");
        Assert.IsTrue(
            result.Decision.SelectedEnvelopes.Any(e => e.CanonicalKey.EntityId == "lex-only"),
            "其他通道命中不受记忆通道故障影响。");
    }

    // ── 跨通道重复候选：合并层只保留一条 ─────────────────────────────────────

    /// <summary>
    /// 验证：同一文档被词法与语义同时命中时，两个通道都产出它（唯一命中率统计的
    /// 分母口径），但规范合并层按 CanonicalKey 只保留一条，选中集合不重复计数。
    /// </summary>
    [TestMethod]
    public async Task Channel_SharedCandidate_MergedOnce_NotDoubleCounted()
    {
        var context = new InMemoryContextStore();
        var memory = new InMemoryMemoryStore();
        var vectors = new InMemoryVectorStore();
        var embeddings = new CodebookEmbeddingProvider();
        embeddings.Map(Q1, SemCluster);
        embeddings.Map(Q2, SemCluster);

        await PopulateWorldAsync(context, memory, relations: null, vectors);
        await context.SaveAsync(new ContextItem
        {
            Id = "shared",
            WorkspaceId = Ws,
            CollectionId = Col,
            Type = "note",
            Title = "shared coordination note",
            Content = "事务调度 coordination across nodes"
        });
        await vectors.UpsertAsync(new VectorRecord
        {
            Id = "vec-shared",
            WorkspaceId = Ws,
            CollectionId = Col,
            SourceId = "shared",
            SourceKind = "context",
            ModelName = "codebook",
            Dimensions = 64,
            Vector = SemCluster
        });

        var runtime = BuildRuntime(BuildRecallProviders(context, memory, relationStore: null, vectors, embeddings));
        var result = await runtime.ExecuteWithWorkingSetAsync(
            BuildRequest("req-channel-shared"), CancellationToken.None);

        var producedChannels = result.ProviderOutputSnapshots
            .Where(s => s.Envelopes.Any(e => e.CanonicalKey.EntityId == "shared"))
            .Select(s => s.Kind)
            .OrderBy(k => k)
            .ToArray();
        CollectionAssert.AreEquivalent(new[] { ExpertKind.Lexical, ExpertKind.Semantic }, producedChannels);
        Assert.AreEqual(1, result.WorkingSet.Envelopes.Count(e => e.CanonicalKey.EntityId == "shared"),
            "跨通道重复候选应在合并层合并为一条。");
        Assert.AreEqual(1, result.Decision.SelectedEnvelopes.Count(e => e.CanonicalKey.EntityId == "shared"),
            "选中集合不应重复计数同一实体。");
    }

    // ── 世界与运行时构造 ─────────────────────────────────────────────────────

    private static async Task PopulateWorldAsync(
        IContextStore context,
        IMemoryStore? memory,
        IRelationStore? relations,
        IVectorStore? vectors)
    {
        await context.SaveAsync(new ContextItem
        {
            Id = "lex-only",
            WorkspaceId = Ws,
            CollectionId = Col,
            Type = "note",
            Title = "事务调度规则",
            Content = "事务调度 幂等重试 租约续期"
        });
        await context.SaveAsync(new ContextItem
        {
            Id = "sem-only",
            WorkspaceId = Ws,
            CollectionId = Col,
            Type = "note",
            Title = "Two-Phase Atomic Commit",
            Content = "protocol design note"
        });
        await context.SaveAsync(new ContextItem
        {
            Id = "graph-only",
            WorkspaceId = Ws,
            CollectionId = Col,
            Type = "note",
            Title = "Relation Graph Target",
            Content = "graph expansion neighbor note"
        });

        if (memory is not null)
        {
            await memory.SaveAsync(new ContextMemoryItem
            {
                Id = "work-only",
                WorkspaceId = Ws,
                CollectionId = Col,
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Candidate,
                Type = "note",
                Content = "working memory scratch note"
            });
            await memory.SaveAsync(new ContextMemoryItem
            {
                Id = "stable-only",
                WorkspaceId = Ws,
                CollectionId = Col,
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "note",
                Content = "stable memory fact"
            });
        }

        if (relations is not null)
        {
            await relations.BatchUpsertAsync(new[]
            {
                new ContextRelation
                {
                    Id = "rel-lex-graph",
                    WorkspaceId = Ws,
                    CollectionId = Col,
                    SourceId = "lex-only",
                    TargetId = "graph-only",
                    RelationType = "references",
                    Confidence = 1.0,
                    Weight = 1.0
                }
            });
        }

        if (vectors is not null)
        {
            await vectors.UpsertAsync(new VectorRecord
            {
                Id = "vec-lex",
                WorkspaceId = Ws,
                CollectionId = Col,
                SourceId = "lex-only",
                SourceKind = "context",
                ModelName = "codebook",
                Dimensions = 64,
                Vector = LexVector
            });
            await vectors.UpsertAsync(new VectorRecord
            {
                Id = "vec-sem",
                WorkspaceId = Ws,
                CollectionId = Col,
                SourceId = "sem-only",
                SourceKind = "context",
                ModelName = "codebook",
                Dimensions = 64,
                Vector = SemCluster
            });
            await vectors.UpsertAsync(new VectorRecord
            {
                Id = "vec-graph",
                WorkspaceId = Ws,
                CollectionId = Col,
                SourceId = "graph-only",
                SourceKind = "context",
                ModelName = "codebook",
                Dimensions = 64,
                Vector = GraphVector
            });
        }
    }

    private static ContextDecisionRuntimeRequest BuildRequest(string requestId)
        => new()
        {
            RequestId = requestId,
            Scope = new ContextDecisionScope(Ws, Col),
            Purpose = ContextDecisionPurpose.Retrieval,
            QueryText = Q1,
            TopK = 10,
            TokenBudget = 4096,
            RetrievalInput = new RetrievalInput
            {
                QueryTexts = new[] { Q1, Q2 },
                // 语义精度旋钮：低于阈值的命中不进候选（余弦 0 的正交噪声被过滤）。
                MinVectorScore = 0.5
            }
        };

    private static ICandidateProvider[] BuildRecallProviders(
        IContextStore contextStore,
        IMemoryStore? memoryStore,
        IRelationStore? relationStore,
        IVectorStore? vectorStore,
        IEmbeddingProvider? embeddingProvider)
    {
        // 与生产一致注入 tokenizer：正文非空但无 tokenizer 时 Provider 会 fail-fast。
        var tokenizer = new DefaultContextTokenizerResolver();
        return new ICandidateProvider[]
        {
            new LexicalCandidateProvider(contextStore, tokenizer),
            new SemanticCandidateProvider(contextStore, memoryStore, embeddingProvider, vectorStore, tokenizer),
            new WorkingMemoryCandidateProvider(memoryStore, tokenizer),
            new StableMemoryCandidateProvider(memoryStore, tokenizer),
            new GraphCandidateProvider(contextStore, relationStore, memoryStore, tokenizer)
        };
    }

    private static DefaultContextDecisionRuntime BuildRuntime(IReadOnlyList<ICandidateProvider> providers)
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

    private static void AssertIds(IReadOnlyList<ContextCandidateEnvelope> envelopes, params string[] expectedIds)
        => CollectionAssert.AreEquivalent(
            expectedIds,
            envelopes.Select(e => e.CanonicalKey.EntityId).ToArray());

    private static float[] BasisVector(int index)
    {
        var vector = new float[64];
        vector[index] = 1f;
        return vector;
    }

    // ── 计数包装 ─────────────────────────────────────────────────────────────

    private sealed class CountingContextStore : IContextStore, IContextStoreMultiQuery, IContextStoreBatchLookup
    {
        private readonly InMemoryContextStore _inner;

        public CountingContextStore(InMemoryContextStore inner) => _inner = inner;

        public int QueryCalls { get; private set; }

        public int MultiQueryCalls { get; private set; }

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
            => _inner.SaveAsync(item, cancellationToken);

        public Task<ContextItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, collectionId, id, cancellationToken);

        public Task<IReadOnlyList<ContextItem>> QueryAsync(ContextQuery query, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            return _inner.QueryAsync(query, cancellationToken);
        }

        public Task DeleteAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, collectionId, id, cancellationToken);

        public Task<IReadOnlyList<ContextMultiQueryResult>> QueryMultiAsync(ContextMultiQuery query, CancellationToken cancellationToken = default)
        {
            MultiQueryCalls++;
            return _inner.QueryMultiAsync(query, cancellationToken);
        }

        public Task<IReadOnlyList<ContextItem>> BatchGetAsync(string workspaceId, string collectionId, IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
            => _inner.BatchGetAsync(workspaceId, collectionId, ids, cancellationToken);
    }

    private sealed class CountingMemoryStore : IMemoryStore
    {
        private readonly InMemoryMemoryStore _inner;

        public CountingMemoryStore(InMemoryMemoryStore inner) => _inner = inner;

        public int QueryCalls { get; private set; }

        public Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
            => _inner.SaveAsync(item, cancellationToken);

        public Task<ContextMemoryItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, collectionId, id, cancellationToken);

        public Task<IReadOnlyList<ContextMemoryItem>> QueryAsync(ContextMemoryQuery query, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            return _inner.QueryAsync(query, cancellationToken);
        }

        public Task UpdateStatusAsync(string workspaceId, string collectionId, string id, ContextMemoryStatus status, CancellationToken cancellationToken = default)
            => _inner.UpdateStatusAsync(workspaceId, collectionId, id, status, cancellationToken);
    }

    private sealed class CountingRelationStore : IRelationStore
    {
        private readonly InMemoryRelationStore _inner;

        public CountingRelationStore(InMemoryRelationStore inner) => _inner = inner;

        public int NeighborBatchCalls { get; private set; }

        public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
            => _inner.SaveAsync(relation, cancellationToken);

        public Task<IReadOnlyList<ContextRelation>> QueryAsync(ContextRelationQuery query, CancellationToken cancellationToken = default)
            => _inner.QueryAsync(query, cancellationToken);

        public Task<ContextRelation?> GetAsync(string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, collectionId, relationId, cancellationToken);

        public Task<bool> DeleteAsync(string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, collectionId, relationId, cancellationToken);

        public Task BatchUpsertAsync(IEnumerable<ContextRelation> relations, CancellationToken cancellationToken = default)
            => _inner.BatchUpsertAsync(relations, cancellationToken);

        public Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(RelationNeighborQuery query, CancellationToken cancellationToken = default)
            => _inner.QueryNeighborsAsync(query, cancellationToken);

        public Task<IReadOnlyList<RelationNeighborBatchResult>> QueryNeighborsBatchAsync(RelationNeighborBatchQuery query, CancellationToken cancellationToken = default)
        {
            NeighborBatchCalls++;
            return _inner.QueryNeighborsBatchAsync(query, cancellationToken);
        }
    }

    private sealed class CountingVectorStore : IVectorStore, IVectorStoreMultiSearch
    {
        private readonly InMemoryVectorStore _inner;

        public CountingVectorStore(InMemoryVectorStore inner) => _inner = inner;

        public int SearchCalls { get; private set; }

        public int MultiSearchCalls { get; private set; }

        public Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
            => _inner.UpsertAsync(record, cancellationToken);

        public Task<VectorRecord?> GetAsync(string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, vectorId, cancellationToken);

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(VectorQuery query, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return _inner.SearchAsync(query, cancellationToken);
        }

        public Task DeleteAsync(string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, vectorId, cancellationToken);

        public Task<IReadOnlyList<VectorMultiSearchResult>> SearchMultiAsync(VectorMultiQuery query, CancellationToken cancellationToken = default)
        {
            MultiSearchCalls++;
            return _inner.SearchMultiAsync(query, cancellationToken);
        }
    }

    /// <summary>
    /// 码本 embedding：只认识显式登记的问句文本，返回其语义簇向量；
    /// 未登记文本返回确定性哈希向量（非零、不在簇方向）。用于模拟
    /// 「词面不重叠但语义相近」的向量召回，不依赖词元重叠。
    /// </summary>
    private sealed class CodebookEmbeddingProvider : IEmbeddingProvider
    {
        private readonly Dictionary<string, IReadOnlyList<float>> _codebook = new(StringComparer.OrdinalIgnoreCase);

        public int CallCount { get; private set; }

        public int InputCount { get; private set; }

        public void Map(string text, IReadOnlyList<float> vector) => _codebook[text.Trim()] = vector;

        public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            InputCount += request.Inputs.Count;

            var vectors = new List<EmbeddingVector>(request.Inputs.Count);
            foreach (var input in request.Inputs)
            {
                var values = _codebook.TryGetValue(input.Text.Trim(), out var known)
                    ? known
                    : FallbackVector(input.Text);
                vectors.Add(new EmbeddingVector
                {
                    InputId = input.Id,
                    SourceRef = string.IsNullOrWhiteSpace(input.SourceRef) ? input.Id : input.SourceRef,
                    Values = values,
                    Norm = 1.0
                });
            }

            return Task.FromResult(new EmbeddingResult
            {
                OperationId = request.OperationId,
                ModelName = "codebook",
                Dimensions = 64,
                Succeeded = true,
                Vectors = vectors,
                Usage = new ContextOperationUsage
                {
                    InputTokens = request.Inputs.Count,
                    OutputTokens = 0,
                    ModelCalls = request.Inputs.Count
                },
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        private static IReadOnlyList<float> FallbackVector(string text)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
            return BasisVector(3 + (hash[0] % 60));
        }
    }

    private sealed class ThrowingMemoryStore : IMemoryStore
    {
        public Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ContextMemoryItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ContextMemoryItem>> QueryAsync(ContextMemoryQuery query, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("memory store unavailable");

        public Task UpdateStatusAsync(string workspaceId, string collectionId, string id, ContextMemoryStatus status, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
