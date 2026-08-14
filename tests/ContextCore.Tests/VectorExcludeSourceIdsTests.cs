using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;
using ContextCore.Embedding.Providers;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextCore.Tests;

// ===========================================================================
// RF-1 验收：向量排除下推
//
// 1. VectorQuery.ExcludeSourceIds 在排序/截断（Take/Limit）前排除来源 ID，
//    InMemory 与 FileSystem 语义一致（Postgres 走同一 SQL 过滤，集成测试覆盖）；
// 2. 已持有的种子 ID 占满原 TopK 时，仍能补足可用的新候选；
// 3. 旧调用方不传排除集合时结果不变；
// 4. SemanticCandidateProvider 把 held ID 与"确认不存在"的排除 ID 下推到向量查询，
//    search 调用次数不增加，末端保留防御去重。
// ===========================================================================

/// <summary>RF-1 向量排除下推验收测试。</summary>
[TestClass]
[TestCategory("RF")]
[TestCategory("DecisionEngine")]
public sealed class VectorExcludeSourceIdsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // =======================================================================
    // Store 层：排除发生在 Take 前
    // =======================================================================

    [TestMethod]
    public async Task InMemoryVectorStore_ExcludeSourceIds_ExcludedBeforeTopK()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(Vector("vec-a", "item-a", [1f, 0f, 0f], Now));
        await store.UpsertAsync(Vector("vec-b", "item-b", [0.8f, 0.2f, 0f], Now));
        await store.UpsertAsync(Vector("vec-c", "item-c", [0.6f, 0.4f, 0f], Now));

        var results = await store.SearchAsync(new VectorQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Vector = [1f, 0f, 0f],
            TopK = 2,
            ExcludeSourceIds = ["item-a"],
            IncludeVector = false
        });

        // 排除最高分来源后，TopK 名额由次高分补足，而不是少返回。
        Assert.AreEqual(2, results.Count, "排除后仍应补足 TopK 个新候选。");
        CollectionAssert.AreEquivalent(
            new[] { "item-b", "item-c" },
            results.Select(r => r.Record.SourceId).ToArray(),
            "应返回排除集之外的候选。");
        Assert.IsFalse(results.Any(r => r.Record.SourceId == "item-a"), "被排除的来源不得返回。");
    }

    [TestMethod]
    public async Task InMemoryVectorStore_NoExclusion_ResultsUnchanged()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(Vector("vec-a", "item-a", [1f, 0f, 0f], Now));
        await store.UpsertAsync(Vector("vec-b", "item-b", [0.8f, 0.2f, 0f], Now));
        await store.UpsertAsync(Vector("vec-c", "item-c", [0.6f, 0.4f, 0f], Now));

        var results = await store.SearchAsync(new VectorQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Vector = [1f, 0f, 0f],
            TopK = 2,
            IncludeVector = false
        });

        // 旧调用方不传排除集合时，按相似度取前 TopK，结果不变。
        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("item-a", results[0].Record.SourceId);
        Assert.AreEqual("item-b", results[1].Record.SourceId);
    }

    [TestMethod]
    public async Task InMemoryVectorStore_ExcludeSourceIds_MatchIsOrdinalIgnoreCase()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(Vector("vec-a", "item-a", [1f, 0f, 0f], Now));
        await store.UpsertAsync(Vector("vec-b", "item-b", [0.8f, 0.2f, 0f], Now));

        var results = await store.SearchAsync(new VectorQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Vector = [1f, 0f, 0f],
            TopK = 5,
            ExcludeSourceIds = ["ITEM-A"],
            IncludeVector = false
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("item-b", results[0].Record.SourceId);
    }

    [TestMethod]
    public async Task FileVectorStore_ExcludeSourceIds_ExcludedBeforeTopK()
    {
        var root = Path.Combine(
            Environment.CurrentDirectory,
            ".appdata",
            "tests",
            "vector-exclude",
            Guid.NewGuid().ToString("N"));
        var vectorStore = new FileVectorStore(new FileStorageOptions { RootPath = root });

        try
        {
            await vectorStore.UpsertAsync(Vector("vec-a", "item-a", [1f, 0f, 0f], Now));
            await vectorStore.UpsertAsync(Vector("vec-b", "item-b", [0.8f, 0.2f, 0f], Now));
            await vectorStore.UpsertAsync(Vector("vec-c", "item-c", [0.6f, 0.4f, 0f], Now));

            var results = await vectorStore.SearchAsync(new VectorQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Vector = [1f, 0f, 0f],
                TopK = 2,
                ExcludeSourceIds = ["item-a"],
                IncludeVector = false
            });

            Assert.AreEqual(2, results.Count, "排除后仍应补足 TopK 个新候选。");
            CollectionAssert.AreEquivalent(
                new[] { "item-b", "item-c" },
                results.Select(r => r.Record.SourceId).ToArray(),
                "FileSystem 与 InMemory 语义应一致。");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // =======================================================================
    // Provider 层：SemanticCandidateProvider 下推 + 防御去重
    // =======================================================================

    [TestMethod]
    public async Task SemanticCandidateProvider_ExcludeSourceIds_PushedDownAndDefensiveFilterKept()
    {
        var store = new RecordingContextStore(
            new[] { MakeItem("held-1"), MakeItem("new-1") });
        var vectorStore = new RecordingVectorStore(
            new[] { MakeContextHit("held-1", 0.9), MakeContextHit("new-1", 0.8) });
        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: null,
            vectorStore: vectorStore, tokenizerResolver: null);

        var seed = new ContextCandidateEnvelope
        {
            CandidateId = "held-1",
            Source = ContextCandidateSource.Mandatory,
            CanonicalKey = CanonicalCandidateKey.Create("test-ws", "test-col", "context", "held-1", "v1")
        };

        var result = await provider.ExecuteAsync(MakeContext(
            seedCandidates: new[] { seed },
            excludedIds: new[] { "gone-1" },
            queryVector: new[] { 0.1f, 0.2f }));

        // 下推：已持有种子 ID 与"确认不存在"的排除 ID 都进入向量查询。
        Assert.AreEqual(1, vectorStore.SearchCalls, "外部 QueryVector 路径只应执行一次向量搜索。");
        Assert.IsNotNull(vectorStore.LastQuery);
        CollectionAssert.Contains(
            vectorStore.LastQuery!.ExcludeSourceIds.ToList(), "held-1",
            "已持有的种子 ID 应下推到向量查询。");
        CollectionAssert.Contains(
            vectorStore.LastQuery!.ExcludeSourceIds.ToList(), "gone-1",
            "确认不存在的排除 ID 应下推到向量查询。");

        // 防御去重：即使 store 忽略排除集返回了已持有命中，末端仍应剔除。
        Assert.AreEqual(1, result.Envelopes.Count, "已持有命中应被末端防御去重剔除。");
        Assert.AreEqual("new-1", result.Envelopes[0].CanonicalKey.EntityId);
    }

    [TestMethod]
    public async Task SemanticCandidateProvider_QueryTexts_ExcludeSourceIds_AppliedPerQuery()
    {
        var store = new RecordingContextStore(
            new[] { MakeItem("held-1"), MakeItem("new-1") });
        var vectorStore = new RecordingVectorStore(
            new[] { MakeContextHit("held-1", 0.9), MakeContextHit("new-1", 0.8) });
        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: new MockEmbeddingProvider(),
            vectorStore: vectorStore, tokenizerResolver: null);

        var seed = new ContextCandidateEnvelope
        {
            CandidateId = "held-1",
            Source = ContextCandidateSource.Mandatory,
            CanonicalKey = CanonicalCandidateKey.Create("test-ws", "test-col", "context", "held-1", "v1")
        };

        var result = await provider.ExecuteAsync(MakeContext(
            seedCandidates: new[] { seed },
            queryTexts: new[] { "alpha query", "beta query" }));

        // 每条 QueryText 一次 embed + 一次 search，调用次数不因下推增加。
        Assert.AreEqual(2, vectorStore.SearchCalls, "两条 QueryText 应各执行一次向量搜索。");
        Assert.IsNotNull(vectorStore.LastQuery);
        CollectionAssert.Contains(
            vectorStore.LastQuery!.ExcludeSourceIds.ToList(), "held-1",
            "分条路径的每次查询都应携带已持有种子 ID。");

        Assert.AreEqual(1, result.Envelopes.Count, "已持有命中应被末端防御去重剔除。");
        Assert.AreEqual("new-1", result.Envelopes[0].CanonicalKey.EntityId);
    }

    // =======================================================================
    // 辅助
    // =======================================================================

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

    private static CandidateProviderContext MakeContext(
        IReadOnlyList<ContextCandidateEnvelope>? seedCandidates = null,
        IReadOnlyList<string>? excludedIds = null,
        IReadOnlyList<float>? queryVector = null,
        IReadOnlyList<string>? queryTexts = null)
    {
        var snapshot = MakeSnapshot();
        return new CandidateProviderContext(
            Request: new ContextDecisionRuntimeRequest
            {
                RequestId = "req-vec-exclude",
                Scope = new ContextDecisionScope("test-ws", "test-col"),
                Purpose = ContextDecisionPurpose.Retrieval,
                TokenBudget = 4096,
                TopK = 10,
                SeedCandidates = seedCandidates ?? Array.Empty<ContextCandidateEnvelope>(),
                RetrievalInput = new RetrievalInput
                {
                    IncludeContent = false,
                    ExcludedIds = excludedIds ?? Array.Empty<string>(),
                    QueryVector = queryVector ?? Array.Empty<float>(),
                    QueryTexts = queryTexts ?? Array.Empty<string>()
                }
            },
            Policy: snapshot,
            Routing: new ExpertRoutingDecision
            {
                Expert = RetrievalExpert.Semantic,
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
                ObservedAt = Now
            });
    }

    private static VectorRecord Vector(
        string id,
        string sourceId,
        IReadOnlyList<float> vector,
        DateTimeOffset now)
    {
        return new VectorRecord
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = sourceId,
            SourceKind = "context",
            ModelName = "test-vector",
            Dimensions = vector.Count,
            Vector = vector,
            ContentHash = id,
            Tags = ["memory"],
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ContextItem MakeItem(string id) => new()
    {
        Id = id,
        WorkspaceId = "test-ws",
        CollectionId = "test-col",
        Content = string.Empty,
        Type = "note",
        Metadata = new Dictionary<string, string>
        {
            [ContentMetadataKeys.ContentLength] = "80"
        }
    };

    private static VectorSearchResult MakeContextHit(string sourceId, double score) => new()
    {
        Record = new VectorRecord
        {
            Id = "vec-" + sourceId,
            WorkspaceId = "test-ws",
            CollectionId = "test-col",
            SourceId = sourceId,
            SourceKind = "context",
            Vector = Array.Empty<float>()
        },
        Score = score,
        Rank = 1
    };

    /// <summary>记录最近一次向量查询与调用次数；返回配置的命中（忽略排除集，模拟未下推的旧 store）。</summary>
    private sealed class RecordingVectorStore : IVectorStore
    {
        private readonly IReadOnlyList<VectorSearchResult> _hits;

        internal RecordingVectorStore(IReadOnlyList<VectorSearchResult> hits) => _hits = hits;

        internal int SearchCalls { get; private set; }

        internal VectorQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
            VectorQuery query, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            LastQuery = query;
            return Task.FromResult(_hits);
        }

        public Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本测试不写入向量。");

        public Task<VectorRecord?> GetAsync(
            string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本测试不单条读取向量。");

        public Task DeleteAsync(
            string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("本测试不删除向量。");
    }

    /// <summary>内存字典版上下文存储，实现元数据批量路径（IncludeContent=false 投影）。</summary>
    private sealed class RecordingContextStore : IContextStore, IContextStoreMetadataLookup
    {
        private readonly Dictionary<string, ContextItem> _items;

        internal RecordingContextStore(IEnumerable<ContextItem> items)
            => _items = items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<ContextItem>> BatchGetMetadataAsync(
            string workspaceId,
            string collectionId,
            IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
        {
            var found = ids
                .Where(id => _items.TryGetValue(id, out _))
                .Select(id => _items[id])
                .ToArray();
            return Task.FromResult<IReadOnlyList<ContextItem>>(found);
        }

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ContextItem?> GetAsync(
            string workspaceId, string collectionId, string id,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_items.TryGetValue(id, out var item) ? item : null);

        public Task<IReadOnlyList<ContextItem>> QueryAsync(
            ContextQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextItem>>(Array.Empty<ContextItem>());

        public Task DeleteAsync(
            string workspaceId, string collectionId, string id,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
