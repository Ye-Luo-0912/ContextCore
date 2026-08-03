using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>覆盖检索 Selected-only 正文水合：元数据投影召回 → Pack 后批量水合 → token 重算。</summary>
[TestClass]
[TestCategory("Retrieval")]
public sealed class SelectedContentHydrationTests
{
    [TestMethod]
    public void RetrievalCandidateBuilder_EstimateTokens_FallsBackToPersistedTokenCost()
    {
        // Content 为空但元数据携带摄取阶段持久化的 token 数 → 使用精确值，避免预算视为 0
        var metadataOnly = RetrievalCandidateBuilder.FromContextItem(Item("m1", string.Empty, new Dictionary<string, string>
        {
            [ContentMetadataKeys.ContentTokenCost] = "123"
        }));
        var built = metadataOnly.Build(includeContent: true);
        Assert.AreEqual(123, built.EstimatedTokens);
        Assert.AreEqual(string.Empty, built.Content);

        // 无持久化 token 数 → 0（无法估算）
        var noCost = RetrievalCandidateBuilder.FromContextItem(Item("m2", string.Empty, null));
        Assert.AreEqual(0, noCost.Build(includeContent: true).EstimatedTokens);

        // 有正文时始终按 length/4（正文优先于持久化值）
        var withContent = RetrievalCandidateBuilder.FromContextItem(Item("m3", new string('a', 16), new Dictionary<string, string>
        {
            [ContentMetadataKeys.ContentTokenCost] = "999"
        }));
        Assert.AreEqual(4, withContent.Build(includeContent: true).EstimatedTokens);
    }

    [TestMethod]
    public async Task HybridContextRetriever_VectorMetadataPath_HydratesSelectedContent()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new MetadataAwareContextStore();
        store.Seed(Item("vec-a", "向量命中条目 A 的正文内容", new Dictionary<string, string> { ["status"] = "active" }));
        store.Seed(Item("vec-b", "向量命中条目 B 的正文内容", new Dictionary<string, string> { ["status"] = "active" }));

        var vectorStore = new InMemoryVectorStore();
        await vectorStore.UpsertAsync(Vector("v1", "vec-a", "context", [1f, 0f], now));
        await vectorStore.UpsertAsync(Vector("v2", "vec-b", "context", [0.9f, 0.1f], now));

        var retriever = new HybridContextRetriever(
            store, memoryStore: null, relationStore: null, embeddingProvider: null, vectorStore);
        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            OperationId = "vector-hydrate",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryVector = [1f, 0f],
            IncludeKeywordRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            IncludeRelationExpansion = false,
            TopK = 2,
            VectorTopK = 4,
            TokenBudget = 1000
        });

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(2, result.SelectedItems.Count);
        // 元数据投影召回 → Selected 水合后正文非空且与 seed 一致，token 按真实正文重算
        var selectedA = result.SelectedItems.Single(item => item.SourceId == "vec-a");
        Assert.AreEqual("向量命中条目 A 的正文内容", selectedA.Content);
        Assert.AreEqual(selectedA.Content.Length / 4, selectedA.EstimatedTokens);
        Assert.IsTrue(store.MetadataLookupCalled, "向量召回应走元数据投影。");
        Assert.IsTrue(store.FullBatchCalled, "Selected 水合应走全量批量读取。");
    }

    [TestMethod]
    public async Task HybridContextRetriever_VectorMetadataPath_ExcludesDeprecatedItems()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new MetadataAwareContextStore();
        store.Seed(Item("deprecated-item", "废弃条目不应进入候选", new Dictionary<string, string> { ["status"] = "deprecated" }));
        store.Seed(Item("normal-item", "正常条目正文", new Dictionary<string, string> { ["status"] = "active" }));

        var vectorStore = new InMemoryVectorStore();
        await vectorStore.UpsertAsync(Vector("v1", "deprecated-item", "context", [1f, 0f], now));
        await vectorStore.UpsertAsync(Vector("v2", "normal-item", "context", [0.99f, 0.01f], now));

        var retriever = new HybridContextRetriever(
            store, memoryStore: null, relationStore: null, embeddingProvider: null, vectorStore);
        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            OperationId = "vector-deprecated",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryVector = [1f, 0f],
            IncludeKeywordRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            IncludeRelationExpansion = false,
            TopK = 2,
            VectorTopK = 4,
            TokenBudget = 1000
        });

        CollectionAssert.DoesNotContain(result.SelectedItems.Select(item => item.SourceId).ToArray(), "deprecated-item");
        CollectionAssert.DoesNotContain(result.Trace.Candidates.Select(item => item.SourceId).ToArray(), "deprecated-item");
    }

    [TestMethod]
    public async Task SelectedCandidateContentHydrator_SkipsWhenContentNotRequested()
    {
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            IncludeContent = false
        };
        var packed = new RetrievalPackingResult(
            Array.Empty<ContextRetrievalCandidate>(),
            Array.Empty<ContextRetrievalDecision>(),
            Array.Empty<ContextRetrievalDecision>());

        var result = await SelectedCandidateContentHydrator.HydrateAsync(
            request, packed, new MetadataAwareContextStore(), memoryStore: null, tokenizerResolver: null);

        Assert.AreSame(packed, result, "IncludeContent=false 时不应执行水合 / token 重算。");
    }

    // =======================================================================
    // 辅助：构造条目 / 向量
    // =======================================================================

    private static ContextItem Item(string id, string content, Dictionary<string, string>? metadata)
    {
        return new ContextItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Title = id,
            Content = content,
            Tags = ["test"],
            Metadata = metadata ?? new Dictionary<string, string>(),
            Importance = 0.8,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static VectorRecord Vector(
        string id,
        string sourceId,
        string sourceKind,
        IReadOnlyList<float> vector,
        DateTimeOffset now)
    {
        return new VectorRecord
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = sourceId,
            SourceKind = sourceKind,
            ModelName = "test-vector",
            Dimensions = vector.Count,
            Vector = vector,
            ContentHash = id,
            Tags = ["test"],
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    // =======================================================================
    // 模拟生产 Postgres 的元数据投影 + 全量批量水合
    // =======================================================================

    /// <summary>
    /// 同时实现 <see cref="IContextStoreMetadataLookup"/>（元数据投影，Content 为空）
    /// 与 <see cref="IContextStoreBatchLookup"/>（全量批量水合），模拟 PostgresContextStore。
    /// 仅向量通道使用，其余 IContextStore 成员不会被调用。
    /// </summary>
    private sealed class MetadataAwareContextStore : IContextStore, IContextStoreBatchLookup, IContextStoreMetadataLookup
    {
        private readonly Dictionary<string, ContextItem> _items = new(StringComparer.OrdinalIgnoreCase);

        public bool MetadataLookupCalled { get; private set; }

        public bool FullBatchCalled { get; private set; }

        public void Seed(ContextItem item) => _items[item.Id] = item;

        public Task<IReadOnlyList<ContextItem>> BatchGetAsync(
            string workspaceId,
            string collectionId,
            IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
        {
            FullBatchCalled = true;
            return Task.FromResult<IReadOnlyList<ContextItem>>(
                ids.Where(_items.ContainsKey).Select(id => _items[id]).ToArray());
        }

        public Task<IReadOnlyList<ContextItem>> BatchGetMetadataAsync(
            string workspaceId,
            string collectionId,
            IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
        {
            MetadataLookupCalled = true;
            var results = new List<ContextItem>(ids.Count);
            foreach (var id in ids)
            {
                if (!_items.TryGetValue(id, out var item))
                {
                    continue;
                }

                // 模拟元数据投影：Content 恒为空，其余字段与存储元数据字典齐全
                results.Add(new ContextItem
                {
                    Id = item.Id,
                    WorkspaceId = item.WorkspaceId,
                    CollectionId = item.CollectionId,
                    Type = item.Type,
                    Title = item.Title,
                    Importance = item.Importance,
                    Tags = item.Tags,
                    Refs = item.Refs,
                    SourceRefs = item.SourceRefs,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt,
                    Content = string.Empty,
                    Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
                });
            }
            return Task.FromResult<IReadOnlyList<ContextItem>>(results);
        }

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ContextItem?> GetAsync(
            string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ContextItem>> QueryAsync(
            ContextQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task DeleteAsync(
            string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
