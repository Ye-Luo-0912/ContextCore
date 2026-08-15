using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 批量 vector search 契约测试。
/// 覆盖：Provider 批量路径（一次 store 调用）与回退路径、缺失向量只跳过该问句、
/// InMemory/FileSystem 与逐条 SearchAsync 的结果平价（集合 + 顺序 + 分数 + 名次）、
/// 每问句 TopK 与共享过滤。
/// </summary>
[TestClass]
[TestCategory("Retrieval")]
[TestCategory("LR2C")]
public sealed class SemanticMultiSearchTests
{
    [TestMethod]
    public async Task Provider_MultiQuery_SingleSearchCall_MergesHighestScore()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));
        await store.SaveAsync(Item("note-2", "second note body"));
        await store.SaveAsync(Item("note-3", "third note body"));

        var embedding = new TextAwareEmbeddingProvider();
        var vectorStore = new InMemoryVectorStore();
        await vectorStore.UpsertAsync(VectorRecordFor("note-1", new[] { 1f, 0f }));
        await vectorStore.UpsertAsync(VectorRecordFor("note-2", new[] { 0f, 1f }));
        await vectorStore.UpsertAsync(VectorRecordFor("note-3", new[] { 1f, 1f }));
        var recording = new RealRecordingVectorStore(vectorStore);

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: recording,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        var result = await provider.ExecuteAsync(MakeContext(
            "query text", queryTexts: new[] { "alpha query", "beta query", "gamma query" }));

        Assert.AreEqual(1, recording.MultiSearchCalls, "多问句应走单次批量向量检索。");
        Assert.AreEqual(0, recording.SearchCalls, "批量路径不应再逐问句 SearchAsync。");
        Assert.IsNotNull(recording.LastMultiQuery);
        Assert.AreEqual(3, recording.LastMultiQuery!.Queries.Count, "批量请求应携带全部问句向量。");
        Assert.AreEqual("query-0", recording.LastMultiQuery.Queries[0].Id, "问句 ID 与 embedding input ID 对应。");

        var envelopes = result.Envelopes.ToDictionary(e => e.CanonicalKey.EntityId, e => e);
        Assert.AreEqual(3, envelopes.Count, "三条问句各命中一个候选，合并后共 3 个。");
        Assert.AreEqual(100.0, envelopes["note-1"].Utility.DeterministicScore, 0.001, "note-1 与 alpha 问句余弦 1.0。");
        Assert.AreEqual(100.0, envelopes["note-2"].Utility.DeterministicScore, 0.001, "note-2 与 beta 问句余弦 1.0。");
        Assert.AreEqual(100.0, envelopes["note-3"].Utility.DeterministicScore, 0.001, "note-3 与 gamma 问句余弦 1.0。");
    }

    [TestMethod]
    public async Task Provider_MultiQuery_FallbackToPerQueryLoop_WhenStoreLacksCapability()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));
        await store.SaveAsync(Item("note-2", "second note body"));

        var embedding = new TextAwareEmbeddingProvider();
        var vectorStore = new InMemoryVectorStore();
        await vectorStore.UpsertAsync(VectorRecordFor("note-1", new[] { 1f, 0f }));
        await vectorStore.UpsertAsync(VectorRecordFor("note-2", new[] { 0f, 1f }));
        var recording = new LegacyOnlyVectorStore(vectorStore);

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: recording,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        var result = await provider.ExecuteAsync(MakeContext(
            "query text", queryTexts: new[] { "alpha query", "beta query" }));

        Assert.AreEqual(2, recording.SearchCalls, "store 无批量能力时应回退为逐问句 SearchAsync。");
        var ids = result.Envelopes.Select(e => e.CanonicalKey.EntityId).OrderBy(id => id).ToArray();
        CollectionAssert.AreEquivalent(new[] { "note-1", "note-2" }, ids, "回退路径结果集合应与批量路径一致。");
    }

    [TestMethod]
    public async Task Provider_SingleQueryText_UsesLegacyPath()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));

        var embedding = new TextAwareEmbeddingProvider();
        var vectorStore = new InMemoryVectorStore();
        await vectorStore.UpsertAsync(VectorRecordFor("note-1", new[] { 1f, 0f }));
        var recording = new RealRecordingVectorStore(vectorStore);

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: recording,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        await provider.ExecuteAsync(MakeContext("query text", queryTexts: new[] { "alpha query" }));

        Assert.AreEqual(1, recording.SearchCalls, "单问句保持逐条路径（行为不变量）。");
        Assert.AreEqual(0, recording.MultiSearchCalls, "q=1 不启用批量（避免为单问句引入新路径）。");
    }

    [TestMethod]
    public async Task Provider_MultiQuery_EmbeddingFailure_NoSearch()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));

        var embedding = new FailingEmbeddingProvider();
        var recording = new RealRecordingVectorStore(new InMemoryVectorStore());

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: recording,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        var result = await provider.ExecuteAsync(MakeContext(
            "query text", queryTexts: new[] { "alpha query", "beta query" }));

        Assert.AreEqual(0, recording.SearchCalls, "embedding 失败不应发起搜索。");
        Assert.AreEqual(0, recording.MultiSearchCalls, "embedding 失败不应发起批量搜索。");
        Assert.AreEqual(0, result.Envelopes.Count);
    }

    [TestMethod]
    public async Task Provider_MultiQuery_MissingVector_SkipsOnlyThatQuery()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));
        await store.SaveAsync(Item("note-2", "second note body"));

        // 只返回第二条问句的向量：第一条缺失 → 批量请求只携带 query-1。
        var embedding = new PartialEmbeddingProvider(returnedInputIds: ["query-1"]);
        var vectorStore = new InMemoryVectorStore();
        await vectorStore.UpsertAsync(VectorRecordFor("note-1", new[] { 1f, 0f }));
        await vectorStore.UpsertAsync(VectorRecordFor("note-2", new[] { 0f, 1f }));
        var recording = new RealRecordingVectorStore(vectorStore);

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: recording,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        var result = await provider.ExecuteAsync(MakeContext(
            "query text", queryTexts: new[] { "alpha query", "beta query" }));

        Assert.AreEqual(1, recording.MultiSearchCalls);
        Assert.AreEqual(1, recording.LastMultiQuery!.Queries.Count, "向量缺失的问句不应进入批量请求。");
        Assert.AreEqual("query-1", recording.LastMultiQuery.Queries[0].Id);
        var topEnvelope = result.Envelopes.Single(e => e.CanonicalKey.EntityId == "note-2");
        Assert.AreEqual(100.0, topEnvelope.Utility.DeterministicScore, 0.001, "缺失问句被跳过，其余问句正常命中最高分候选。");
    }

    [TestMethod]
    public async Task Store_Parity_InMemory_MultiVsSequential()
    {
        var store = new InMemoryVectorStore();
        await PopulateAsync(store);

        var multi = new VectorMultiQuery
        {
            WorkspaceId = "ws-vec",
            CollectionId = "col-vec",
            Queries = new[]
            {
                new VectorMultiQueryVector { Id = "q0", Vector = new[] { 1f, 0f } },
                new VectorMultiQueryVector { Id = "q1", Vector = new[] { 0f, 1f } },
                new VectorMultiQueryVector { Id = "q2", Vector = new[] { 1f, 1f } }
            },
            TopK = 5,
            IncludeVector = false
        };

        var multiResults = await store.SearchMultiAsync(multi);

        foreach (var q in multi.Queries)
        {
            var sequential = await store.SearchAsync(new VectorQuery
            {
                WorkspaceId = "ws-vec",
                CollectionId = "col-vec",
                Vector = q.Vector,
                TopK = 5,
                IncludeVector = false
            });
            var multiItem = multiResults.Single(r => r.QueryId == q.Id);
            Assert.AreEqual(sequential.Count, multiItem.Hits.Count, $"问句 [{q.Id}] 命中数应与逐条一致。");
            for (var i = 0; i < sequential.Count; i++)
            {
                Assert.AreEqual(sequential[i].Record.SourceId, multiItem.Hits[i].Record.SourceId, $"问句 [{q.Id}] 第 {i} 名来源应一致。");
                Assert.AreEqual(sequential[i].Score, multiItem.Hits[i].Score, 0.0001, $"问句 [{q.Id}] 第 {i} 名分数应一致。");
                Assert.AreEqual(sequential[i].Rank, multiItem.Hits[i].Rank, $"问句 [{q.Id}] 第 {i} 名名次应一致。");
            }
            Assert.IsTrue(multiItem.Hits.All(h => h.Record.Vector.Count == 0), "IncludeVector=false 批量路径不应返回原始向量。");
        }
    }

    [TestMethod]
    public async Task Store_Parity_FileSystem_MultiVsSequential()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-lr2c-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileVectorStore(new FileStorageOptions { RootPath = root });
            await PopulateAsync(store);

            var multi = new VectorMultiQuery
            {
                WorkspaceId = "ws-vec",
                CollectionId = "col-vec",
                Queries = new[]
                {
                    new VectorMultiQueryVector { Id = "q0", Vector = new[] { 1f, 0f } },
                    new VectorMultiQueryVector { Id = "q1", Vector = new[] { 0f, 1f } },
                    new VectorMultiQueryVector { Id = "q2", Vector = new[] { 1f, 1f } }
                },
                TopK = 5,
                IncludeVector = false
            };

            var multiResults = await store.SearchMultiAsync(multi);

            foreach (var q in multi.Queries)
            {
                var sequential = await store.SearchAsync(new VectorQuery
                {
                    WorkspaceId = "ws-vec",
                    CollectionId = "col-vec",
                    Vector = q.Vector,
                    TopK = 5,
                    IncludeVector = false
                });
                var multiItem = multiResults.Single(r => r.QueryId == q.Id);
                Assert.AreEqual(sequential.Count, multiItem.Hits.Count, $"FileSystem 问句 [{q.Id}] 命中数应与逐条一致。");
                for (var i = 0; i < sequential.Count; i++)
                {
                    Assert.AreEqual(sequential[i].Record.SourceId, multiItem.Hits[i].Record.SourceId, $"问句 [{q.Id}] 第 {i} 名来源应一致。");
                    Assert.AreEqual(sequential[i].Score, multiItem.Hits[i].Score, 0.0001, $"问句 [{q.Id}] 第 {i} 名分数应一致。");
                    Assert.AreEqual(sequential[i].Rank, multiItem.Hits[i].Rank, $"问句 [{q.Id}] 第 {i} 名名次应一致。");
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Store_MultiQuery_PerQueryTopK_ExcludeAndFilters()
    {
        var store = new InMemoryVectorStore();
        await PopulateAsync(store);

        // TopK=1：每条问句只保留最高分；排除 source-a；tags 过滤只保留带 tag-x 的记录。
        var results = await store.SearchMultiAsync(new VectorMultiQuery
        {
            WorkspaceId = "ws-vec",
            CollectionId = "col-vec",
            Queries = new[]
            {
                new VectorMultiQueryVector { Id = "q0", Vector = new[] { 1f, 0f } },
                new VectorMultiQueryVector { Id = "q2", Vector = new[] { 1f, 1f } }
            },
            TopK = 1,
            ExcludeSourceIds = new[] { "source-a" },
            Tags = new[] { "tag-x" },
            IncludeVector = false
        });

        var q0 = results.Single(r => r.QueryId == "q0");
        var q2 = results.Single(r => r.QueryId == "q2");
        Assert.IsTrue(q0.Hits.Count <= 1 && q2.Hits.Count <= 1, "TopK=1 时每条问句最多一条。");
        Assert.IsTrue(q0.Hits.All(h => h.Record.SourceId != "source-a"), "排除 ID 不应出现在结果中。");
        Assert.IsTrue(q2.Hits.All(h => h.Record.SourceId != "source-a"));
        Assert.IsTrue(results.SelectMany(r => r.Hits).All(h => h.Record.Tags.Contains("tag-x")), "共享 tags 过滤生效。");
    }

    [TestMethod]
    public async Task Store_MultiQuery_EmptyQueries_ReturnsEmpty()
    {
        var store = new InMemoryVectorStore();
        var results = await store.SearchMultiAsync(new VectorMultiQuery
        {
            WorkspaceId = "ws-vec",
            Queries = Array.Empty<VectorMultiQueryVector>()
        });
        Assert.AreEqual(0, results.Count, "无问句时返回空结果。");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static async Task PopulateAsync(IVectorStore store)
    {
        await store.UpsertAsync(VectorRecordFor("source-a", new[] { 1f, 0f }, tags: new[] { "tag-x" }));
        await store.UpsertAsync(VectorRecordFor("source-b", new[] { 0f, 1f }, tags: new[] { "tag-x" }));
        await store.UpsertAsync(VectorRecordFor("source-c", new[] { 1f, 1f }, tags: new[] { "tag-y" }));
        await store.UpsertAsync(VectorRecordFor("source-d", new[] { 0.5f, 0.5f }, tags: new[] { "tag-x" }));
    }

    private static VectorRecord VectorRecordFor(string sourceId, float[] vector, IReadOnlyList<string>? tags = null)
        => new()
        {
            Id = "vec-" + sourceId,
            WorkspaceId = "ws-vec",
            CollectionId = "col-vec",
            SourceId = sourceId,
            SourceKind = "context",
            Dimensions = vector.Length,
            Vector = vector,
            Tags = tags ?? Array.Empty<string>(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static ContextItem Item(string id, string content) => new()
    {
        Id = id,
        WorkspaceId = "ws-vec",
        CollectionId = "col-vec",
        Type = "note",
        Title = id,
        Content = content
    };

    private static CandidateProviderContext MakeContext(
        string queryText,
        IReadOnlyList<string>? queryTexts)
    {
        var bundle = DefaultPolicyBundleFactory.Create();
        var snapshot = new EffectivePolicySnapshot
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
            ResolutionScope = new ContextDecisionScope("ws-vec", "col-vec")
        };

        return new CandidateProviderContext(
            Request: new ContextDecisionRuntimeRequest
            {
                RequestId = "req-lr2c",
                Scope = new ContextDecisionScope("ws-vec", "col-vec"),
                Purpose = ContextDecisionPurpose.Retrieval,
                QueryText = queryText,
                TokenBudget = 4096,
                TopK = 10,
                RetrievalInput = new RetrievalInput
                {
                    IncludeContent = true,
                    QueryTexts = queryTexts ?? Array.Empty<string>(),
                    QueryInstruction = "指令前缀"
                }
            },
            Policy: snapshot,
            Routing: new ExpertRoutingDecision
            {
                Expert = RetrievalExpert.Semantic,
                Enabled = true,
                TopK = 10,
                TokenBudget = 4096,
                Weight = 1.0,
                ReasonCode = "test"
            },
            AdaptationContext: new CandidateAdaptationContext
            {
                WorkspaceId = "ws-vec",
                CollectionId = "col-vec",
                ObservedAt = DateTimeOffset.UtcNow
            });
    }

    /// <summary>按问句文本关键词返回固定向量（alpha→[1,0]、beta→[0,1]、gamma→[1,1]），用于确定性余弦。</summary>
    private sealed class TextAwareEmbeddingProvider : IEmbeddingProvider
    {
        public Task<EmbeddingResult> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vectors = request.Inputs.Select(input => new EmbeddingVector
            {
                InputId = input.Id,
                SourceRef = input.SourceRef,
                Values = VectorFor(input.Text),
                Norm = 1.0
            }).ToArray();
            return Task.FromResult(new EmbeddingResult
            {
                OperationId = request.OperationId,
                ModelName = "test",
                Dimensions = 2,
                Succeeded = true,
                Vectors = vectors,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        private static float[] VectorFor(string text)
        {
            if (text.Contains("alpha", StringComparison.OrdinalIgnoreCase)) return new[] { 1f, 0f };
            if (text.Contains("beta", StringComparison.OrdinalIgnoreCase)) return new[] { 0f, 1f };
            if (text.Contains("gamma", StringComparison.OrdinalIgnoreCase)) return new[] { 1f, 1f };
            return new[] { 0.5f, 0.5f };
        }
    }

    private sealed class FailingEmbeddingProvider : IEmbeddingProvider
    {
        public Task<EmbeddingResult> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new EmbeddingResult
            {
                OperationId = request.OperationId,
                ModelName = "test",
                Succeeded = false,
                ErrorMessage = "embedding unavailable",
                Vectors = Array.Empty<EmbeddingVector>()
            });
        }
    }

    private sealed class PartialEmbeddingProvider : IEmbeddingProvider
    {
        private readonly HashSet<string> _returnedInputIds;
        public PartialEmbeddingProvider(IEnumerable<string> returnedInputIds)
        {
            _returnedInputIds = new HashSet<string>(returnedInputIds, StringComparer.Ordinal);
        }

        public Task<EmbeddingResult> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vectors = request.Inputs
                .Where(input => _returnedInputIds.Contains(input.Id))
                .Select(input => new EmbeddingVector
                {
                    InputId = input.Id,
                    SourceRef = input.SourceRef,
                    Values = new[] { 0f, 1f },
                    Norm = 1.0
                })
                .ToArray();
            return Task.FromResult(new EmbeddingResult
            {
                OperationId = request.OperationId,
                ModelName = "test",
                Dimensions = 2,
                Succeeded = true,
                Vectors = vectors
            });
        }
    }

    private sealed class RealRecordingVectorStore : IVectorStore, IVectorStoreMultiSearch
    {
        private readonly InMemoryVectorStore _inner;
        public int SearchCalls;
        public int MultiSearchCalls;
        public VectorMultiQuery? LastMultiQuery;

        public RealRecordingVectorStore(InMemoryVectorStore inner) => _inner = inner;

        public Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
            => _inner.UpsertAsync(record, cancellationToken);

        public Task<VectorRecord?> GetAsync(string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, vectorId, cancellationToken);

        public Task DeleteAsync(string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, vectorId, cancellationToken);

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(VectorQuery query, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return _inner.SearchAsync(query, cancellationToken);
        }

        public Task<IReadOnlyList<VectorMultiSearchResult>> SearchMultiAsync(VectorMultiQuery query, CancellationToken cancellationToken = default)
        {
            MultiSearchCalls++;
            LastMultiQuery = query;
            return _inner.SearchMultiAsync(query, cancellationToken);
        }
    }

    private sealed class LegacyOnlyVectorStore : IVectorStore
    {
        private readonly InMemoryVectorStore _inner;
        public int SearchCalls;

        public LegacyOnlyVectorStore(InMemoryVectorStore inner) => _inner = inner;

        public Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
            => _inner.UpsertAsync(record, cancellationToken);

        public Task<VectorRecord?> GetAsync(string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, vectorId, cancellationToken);

        public Task DeleteAsync(string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, vectorId, cancellationToken);

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(VectorQuery query, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return _inner.SearchAsync(query, cancellationToken);
        }
    }
}
