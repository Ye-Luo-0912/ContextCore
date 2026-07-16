using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

/// <summary>
/// R12-4: 验证 retrieval 通道的 provider capability 控制并行度。
/// 当 store 实现 IContextStoreBatchLookup 时，Mandatory executor 应使用 BatchGetAsync 而非 N 次单条 GetAsync。
/// </summary>
[TestClass]
[TestCategory("Infrastructure")]
public sealed class RetrievalParallelismTests
{
    /// <summary>
    /// Mandatory recall 使用支持 BatchGetAsync 的 store 时，应调用 BatchGetAsync 一次而非 N 次 GetAsync。
    /// </summary>
    [TestMethod]
    public async Task MandatoryRecall_WithBatchLookupCapableStore_UsesBatchGetAsync()
    {
        var store = new FakeBatchCapableContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "item-1",
            WorkspaceId = "ws",
            CollectionId = "col",
            Content = "content-1",
            Type = "test"
        }, default);
        await store.SaveAsync(new ContextItem
        {
            Id = "item-2",
            WorkspaceId = "ws",
            CollectionId = "col",
            Content = "content-2",
            Type = "test"
        }, default);

        var retriever = new HybridContextRetriever(store);
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            RequiredIds = ["item-1", "item-2"],
            IncludeKeywordRecall = false,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false
        };

        var result = await retriever.RetrieveAsync(request);

        Assert.AreEqual(1, store.BatchGetCallCount, "BatchGetAsync 应被调用 1 次");
        Assert.AreEqual(0, store.GetCallCount, "GetAsync 不应被调用");
        Assert.AreEqual(2, result.SelectedItems.Count, "应返回 2 个候选");
    }

    /// <summary>
    /// Mandatory recall 使用不支持 BatchGetAsync 的 legacy store 时，应回退到并行单条 GetAsync。
    /// </summary>
    [TestMethod]
    public async Task MandatoryRecall_WithLegacyStore_FallsBackToParallelGetAsync()
    {
        var store = new FakeLegacyContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "item-1",
            WorkspaceId = "ws",
            CollectionId = "col",
            Content = "content-1",
            Type = "test"
        }, default);
        await store.SaveAsync(new ContextItem
        {
            Id = "item-2",
            WorkspaceId = "ws",
            CollectionId = "col",
            Content = "content-2",
            Type = "test"
        }, default);

        var retriever = new HybridContextRetriever(store);
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            RequiredIds = ["item-1", "item-2"],
            IncludeKeywordRecall = false,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false
        };

        var result = await retriever.RetrieveAsync(request);

        Assert.AreEqual(2, store.GetCallCount, "GetAsync 应被调用 2 次（每个 id 一次）");
        Assert.AreEqual(2, result.SelectedItems.Count, "应返回 2 个候选");
    }

    /// <summary>
    /// BatchGetAsync 返回的候选数量应与 RequiredIds 中命中的数量一致。
    /// </summary>
    [TestMethod]
    public async Task MandatoryRecall_WithBatchLookup_MissedIdsDoNotProduceCandidates()
    {
        var store = new FakeBatchCapableContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "exists",
            WorkspaceId = "ws",
            CollectionId = "col",
            Content = "content",
            Type = "test"
        }, default);

        var retriever = new HybridContextRetriever(store);
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            RequiredIds = ["exists", "missing"],
            IncludeKeywordRecall = false,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false
        };

        var result = await retriever.RetrieveAsync(request);

        Assert.AreEqual(1, store.BatchGetCallCount, "BatchGetAsync 应被调用 1 次");
        Assert.AreEqual(0, store.GetCallCount, "GetAsync 不应被调用");
        Assert.AreEqual(1, result.SelectedItems.Count, "应只返回 1 个命中候选");
    }

    /// <summary>
    /// 空 RequiredIds 不触发任何 store 调用。
    /// </summary>
    [TestMethod]
    public async Task MandatoryRecall_WithEmptyRequiredIds_NoStoreCalls()
    {
        var store = new FakeBatchCapableContextStore();
        var retriever = new HybridContextRetriever(store);
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            RequiredIds = [],
            IncludeKeywordRecall = false,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false
        };

        await retriever.RetrieveAsync(request);

        Assert.AreEqual(0, store.BatchGetCallCount, "空 RequiredIds 不应触发 BatchGetAsync");
        Assert.AreEqual(0, store.GetCallCount, "空 RequiredIds 不应触发 GetAsync");
    }

    /// <summary>
    /// 支持 IContextStoreBatchLookup 的 fake store，计数 BatchGetAsync 和 GetAsync 调用。
    /// </summary>
    private sealed class FakeBatchCapableContextStore : IContextStore, IContextStoreBatchLookup
    {
        private readonly Dictionary<string, ContextItem> _items = new(StringComparer.OrdinalIgnoreCase);
        public int BatchGetCallCount { get; private set; }
        public int GetCallCount { get; private set; }

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
        {
            _items[item.Id] = item;
            return Task.CompletedTask;
        }

        public Task<ContextItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
        {
            GetCallCount++;
            return Task.FromResult(_items.TryGetValue(id, out var item) ? item : null);
        }

        public Task<IReadOnlyList<ContextItem>> BatchGetAsync(string workspaceId, string collectionId, IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
        {
            BatchGetCallCount++;
            var results = new List<ContextItem>();
            foreach (var id in ids)
            {
                if (_items.TryGetValue(id, out var item))
                {
                    results.Add(item);
                }
            }
            return Task.FromResult<IReadOnlyList<ContextItem>>(results);
        }

        public Task<IReadOnlyList<ContextItem>> QueryAsync(ContextQuery query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ContextItem>>(Array.Empty<ContextItem>());
        }

        public Task DeleteAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
        {
            _items.Remove(id);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 不支持 IContextStoreBatchLookup 的 legacy fake store，只计数 GetAsync 调用。
    /// </summary>
    private sealed class FakeLegacyContextStore : IContextStore
    {
        private readonly Dictionary<string, ContextItem> _items = new(StringComparer.OrdinalIgnoreCase);
        public int GetCallCount { get; private set; }

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
        {
            _items[item.Id] = item;
            return Task.CompletedTask;
        }

        public Task<ContextItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
        {
            GetCallCount++;
            return Task.FromResult(_items.TryGetValue(id, out var item) ? item : null);
        }

        public Task<IReadOnlyList<ContextItem>> QueryAsync(ContextQuery query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ContextItem>>(Array.Empty<ContextItem>());
        }

        public Task DeleteAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
        {
            _items.Remove(id);
            return Task.CompletedTask;
        }
    }
}
