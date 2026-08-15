using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

// Semantic 通道只在有 embedding / 向量存储时工作（默认 Dev 无 embedding，为空是预期）。
// QueryTexts 非空时规范化去重后一次批量 embedding 生成全部向量，再逐条 vector search，
// 按来源 ID 合并保留最高分（与 Lexical 分条对齐）；为空时回退单条 QueryText。

[TestClass]
[TestCategory("Retrieval")]
public sealed class SemanticQueryTextsTests
{
    [TestMethod]
    public async Task QueryTexts_BatchEmbedsOnce_SearchesPerQuery_MergesHighestScore()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));
        await store.SaveAsync(Item("note-2", "second note body"));

        var embedding = new RecordingEmbeddingProvider();
        var vector = new RecordingVectorStore(
            new[]
            {
                MakeHit("note-1", 0.9),
                MakeHit("note-2", 0.5)
            },
            new[]
            {
                MakeHit("note-1", 0.7)
            });

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: vector,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        var result = await provider.ExecuteAsync(
            MakeContext("query text", queryTexts: new[] { "alpha query", "beta query" }));

        Assert.AreEqual(1, embedding.EmbedCalls, "多条 QueryTexts 应合并为一次批量 embedding。");
        Assert.AreEqual(2, embedding.LastRequest!.Inputs.Count, "批量请求应携带全部问句输入。");
        Assert.AreEqual(2, vector.SearchCalls, "每条问句仍应各 search 一次（向量各不相同）。");
        Assert.AreEqual(2, result.Envelopes.Count, "重叠命中应按 ID 合并去重。");
        var note1 = result.Envelopes.Single(e => e.CanonicalKey.EntityId == "note-1");
        var note2 = result.Envelopes.Single(e => e.CanonicalKey.EntityId == "note-2");
        Assert.AreEqual(90.0, note1.Utility.DeterministicScore, 0.001,
            "同一 ID 多次命中应保留最高分（0.9 × 100），不是被低分覆盖。");
        Assert.AreEqual(50.0, note2.Utility.DeterministicScore, 0.001,
            "只在一条问句命中的条目保留该条分数。");
    }

    [TestMethod]
    public async Task QueryTexts_DuplicatesAndWhitespace_EmbeddedOnce()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));

        var embedding = new RecordingEmbeddingProvider();
        var vector = new RecordingVectorStore(
            new[] { MakeHit("note-1", 0.8) },
            new[] { MakeHit("note-1", 0.8) },
            new[] { MakeHit("note-1", 0.8) });

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: vector,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        // 重复问句 + 空白问句：规范化后只剩一条，只 embed 一次、search 一次。
        var result = await provider.ExecuteAsync(
            MakeContext("query text",
                queryTexts: new[] { "  alpha query ", "alpha query", "   ", "alpha query" }));

        Assert.AreEqual(1, embedding.EmbedCalls, "重复/空白问句不得触发额外 embedding。");
        Assert.AreEqual(1, embedding.LastRequest!.Inputs.Count, "批量输入应只含去重后的唯一问句。");
        Assert.AreEqual("指令前缀 alpha query", embedding.LastRequest.Inputs[0].Text, "问句应被 trim 后再拼接指令前缀。");
        Assert.AreEqual(1, vector.SearchCalls);
        Assert.AreEqual(1, result.Envelopes.Count);
    }

    [TestMethod]
    public async Task QueryTexts_AllBlank_NoEmbeddingCall()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));

        var embedding = new RecordingEmbeddingProvider();
        var vector = new RecordingVectorStore();

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: vector,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        var result = await provider.ExecuteAsync(
            MakeContext("query text", queryTexts: new[] { "   ", "", "\t" }));

        Assert.AreEqual(0, embedding.EmbedCalls, "全部空白问句不应调用 embedding。");
        Assert.AreEqual(0, vector.SearchCalls);
        Assert.AreEqual(0, result.Envelopes.Count);
    }

    [TestMethod]
    public async Task QueryTexts_InstructionAndText_PreservedInBatchInput()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));

        var embedding = new RecordingEmbeddingProvider();
        var vector = new RecordingVectorStore(new[] { MakeHit("note-1", 0.8) });

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: vector,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        var context = MakeContext("query text", queryTexts: new[] { "alpha query", "beta query" });
        await provider.ExecuteAsync(context);

        Assert.AreEqual(2, embedding.LastRequest!.Inputs.Count);
        CollectionAssert.AreEqual(
            new[] { "指令前缀 alpha query", "指令前缀 beta query" },
            embedding.LastRequest.Inputs.Select(i => i.Text).ToArray(),
            "批量输入文本必须与旧逐条路径逐位一致（instruction + 空格 + 问句）。");
        Assert.IsTrue(embedding.LastRequest.Inputs.All(i => i.Id.StartsWith("query-", StringComparison.Ordinal)),
            "批量输入应按稳定 ID 命名以便向量按 input ID 映射。");
    }

    [TestMethod]
    public async Task QueryTexts_EmbeddingFailure_ReturnsEmpty_NoSearch()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));

        var embedding = new FailingEmbeddingProvider();
        var vector = new RecordingVectorStore(new[] { MakeHit("note-1", 0.8) });

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: vector,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        var result = await provider.ExecuteAsync(
            MakeContext("query text", queryTexts: new[] { "alpha query" }));

        Assert.AreEqual(1, embedding.EmbedCalls, "批量 embedding 失败也应只调用一次。");
        Assert.AreEqual(0, vector.SearchCalls, "embedding 失败不应发起搜索。");
        Assert.AreEqual(0, result.Envelopes.Count);
    }

    [TestMethod]
    public async Task QueryTexts_MissingVector_SkipsOnlyThatQuery()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));

        // 只返回第二条问句的向量：第一条缺失 → 只搜第二条。
        var embedding = new PartialEmbeddingProvider(returnedInputIds: ["query-1"]);
        var vector = new RecordingVectorStore(new[] { MakeHit("note-1", 0.8) });

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: vector,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        var result = await provider.ExecuteAsync(
            MakeContext("query text", queryTexts: new[] { "alpha query", "beta query" }));

        Assert.AreEqual(1, embedding.EmbedCalls);
        Assert.AreEqual(1, vector.SearchCalls, "向量缺失的问句应跳过，不发起搜索。");
        Assert.AreEqual(1, result.Envelopes.Count);
    }

    [TestMethod]
    public async Task QueryTexts_Empty_FallsBackToSingleQueryText()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));

        var embedding = new RecordingEmbeddingProvider();
        var vector = new RecordingVectorStore(new[] { MakeHit("note-1", 0.8) });

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: embedding, vectorStore: vector,
            tokenizerResolver: new DefaultContextTokenizerResolver());

        var result = await provider.ExecuteAsync(
            MakeContext("single query", queryTexts: null));

        Assert.AreEqual(1, embedding.EmbedCalls, "QueryTexts 为空时应只 embed 单条 QueryText。");
        Assert.AreEqual(1, vector.SearchCalls, "QueryTexts 为空时应只 search 一次。");
        Assert.AreEqual(1, result.Envelopes.Count, "单条回退仍应命中。");
    }

    [TestMethod]
    public async Task NoEmbeddingProvider_QueryTexts_ReturnsEmpty()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));

        var vector = new RecordingVectorStore();
        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: null, vectorStore: vector,
            tokenizerResolver: null);

        var result = await provider.ExecuteAsync(
            MakeContext("query text", queryTexts: new[] { "alpha query" }));

        Assert.AreEqual(0, result.Envelopes.Count, "无 embedding provider 时 Semantic 通道仍为空。");
        Assert.AreEqual(0, vector.SearchCalls, "无 embedding 时不应发起向量搜索。");
    }

    [TestMethod]
    public async Task NoVectorStore_ReturnsEmpty()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(Item("note-1", "first note body"));

        var provider = new SemanticCandidateProvider(
            store, memoryStore: null, embeddingProvider: new RecordingEmbeddingProvider(),
            vectorStore: null, tokenizerResolver: null);

        var result = await provider.ExecuteAsync(
            MakeContext("query text", queryTexts: new[] { "alpha query" }));

        Assert.AreEqual(0, result.Envelopes.Count, "无 vector store 时 Semantic 通道为空。");
    }

    private static ContextItem Item(string id, string content) => new()
    {
        Id = id,
        WorkspaceId = "ws-sem",
        CollectionId = "col-sem",
        Type = "note",
        Title = id,
        Content = content
    };

    private static VectorSearchResult MakeHit(string sourceId, double score) => new()
    {
        Record = new VectorRecord
        {
            Id = "vec-" + sourceId,
            WorkspaceId = "ws-sem",
            CollectionId = "col-sem",
            SourceId = sourceId,
            SourceKind = "context",
            Dimensions = 2,
            Vector = new[] { 0.1f, 0.2f }
        },
        Score = score,
        Rank = 1
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
            ResolutionScope = new ContextDecisionScope("ws-sem", "col-sem")
        };

        return new CandidateProviderContext(
            Request: new ContextDecisionRuntimeRequest
            {
                RequestId = "req-sem-query-texts",
                Scope = new ContextDecisionScope("ws-sem", "col-sem"),
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
                WorkspaceId = "ws-sem",
                CollectionId = "col-sem",
                ObservedAt = DateTimeOffset.UtcNow
            });
    }

    private sealed class RecordingEmbeddingProvider : IEmbeddingProvider
    {
        public int EmbedCalls { get; private set; }

        public EmbeddingRequest? LastRequest { get; private set; }

        public Task<EmbeddingResult> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EmbedCalls++;
            LastRequest = request;

            var vectors = request.Inputs.Select(input => new EmbeddingVector
            {
                InputId = input.Id,
                SourceRef = string.IsNullOrWhiteSpace(input.SourceRef) ? input.Id : input.SourceRef,
                Values = new[] { 0.1f, 0.2f },
                Norm = 1.0
            }).ToArray();

            return Task.FromResult(new EmbeddingResult
            {
                OperationId = request.OperationId,
                ModelName = request.ModelName ?? "test",
                Dimensions = 2,
                Succeeded = true,
                Vectors = vectors,
                Usage = new ContextOperationUsage
                {
                    InputTokens = request.Inputs.Sum(input => Math.Max(1, input.Text.Length / 4)),
                    OutputTokens = 0,
                    ModelCalls = request.Inputs.Count
                },
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private sealed class FailingEmbeddingProvider : IEmbeddingProvider
    {
        public int EmbedCalls { get; private set; }

        public Task<EmbeddingResult> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EmbedCalls++;
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

        public int EmbedCalls { get; private set; }

        public PartialEmbeddingProvider(IEnumerable<string> returnedInputIds)
        {
            _returnedInputIds = new HashSet<string>(returnedInputIds, StringComparer.Ordinal);
        }

        public Task<EmbeddingResult> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EmbedCalls++;
            var vectors = request.Inputs
                .Where(input => _returnedInputIds.Contains(input.Id))
                .Select(input => new EmbeddingVector
                {
                    InputId = input.Id,
                    SourceRef = input.SourceRef,
                    Values = new[] { 0.1f, 0.2f },
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

    private sealed class RecordingVectorStore : IVectorStore
    {
        private readonly Queue<IReadOnlyList<VectorSearchResult>> _responses;

        public int SearchCalls { get; private set; }

        public RecordingVectorStore(params IReadOnlyList<VectorSearchResult>[] responses)
        {
            _responses = new Queue<IReadOnlyList<VectorSearchResult>>(responses);
        }

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
            VectorQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchCalls++;
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : Array.Empty<VectorSearchResult>());
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
}
