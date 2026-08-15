using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 多问句 lexical 单次读取契约测试。
/// 覆盖：Provider 批量路径（一次 store 调用）与回退路径、refs 只作用于首问句、
/// 每问句独立 TopK、共享过滤、InMemory/FileSystem 与逐条 QueryAsync 的结果平价。
/// </summary>
[TestClass]
[TestCategory("Retrieval")]
[TestCategory("LR2B")]
public sealed class LexicalMultiQueryTests
{
    [TestMethod]
    public async Task Provider_MultiQuery_SingleStoreCall_MergesHighestScore()
    {
        var inner = new InMemoryContextStore();
        await SaveAsync(inner, "a-1", title: "alpha", content: "alpha topic");
        await SaveAsync(inner, "b-2", title: "b-title", content: "beta topic");
        await SaveAsync(inner, "c-3", title: "gamma", content: "gamma topic");
        var store = new RecordingContextStore(inner);
        var provider = new LexicalCandidateProvider(store);

        var result = await provider.ExecuteAsync(MakeContext(
            queryText: "task",
            queryTexts: new[] { "alpha", "beta", "gamma" },
            includeContent: false));

        Assert.AreEqual(1, store.MultiQueryCalls, "多问句应走单次批量读取。");
        Assert.AreEqual(0, store.QueryCalls, "批量路径不应再逐问句调用 QueryAsync。");

        var envelopes = result.Envelopes.ToDictionary(e => e.CanonicalKey.EntityId, e => e);
        Assert.AreEqual(3, envelopes.Count, "三条问句各命中一个候选，合并后共 3 个。");
        Assert.AreEqual(100.0, envelopes["a-1"].Utility.DeterministicScore, 0.001, "标题含问句 → 基础 50 + 标题加分 50。");
        Assert.AreEqual(50.0, envelopes["b-2"].Utility.DeterministicScore, 0.001, "仅正文命中 → 基础 50。");
        Assert.AreEqual(100.0, envelopes["c-3"].Utility.DeterministicScore, 0.001, "标题含问句 → 100。");
    }

    [TestMethod]
    public async Task Provider_MultiQuery_RefsAppliedToFirstQueryOnly()
    {
        var inner = new InMemoryContextStore();
        await SaveAsync(inner, "ref-t", title: "ref item", content: "alpha beta", refs: new[] { "target-ref" });
        await SaveAsync(inner, "plain", title: "plain item", content: "alpha beta");
        var store = new RecordingContextStore(inner);
        var provider = new LexicalCandidateProvider(store);

        var result = await provider.ExecuteAsync(MakeContext(
            queryText: "task",
            queryTexts: new[] { "alpha", "beta" },
            includeContent: false,
            refs: new[] { "target-ref" }));

        Assert.AreEqual(1, store.MultiQueryCalls);
        Assert.IsNotNull(store.LastMultiQuery);
        Assert.AreEqual(2, store.LastMultiQuery!.Queries.Count);
        CollectionAssert.AreEqual(
            new[] { "target-ref" },
            store.LastMultiQuery.Queries[0].Refs.ToArray(),
            "首问句携带 refs。");
        Assert.AreEqual(0, store.LastMultiQuery.Queries[1].Refs.Count, "其余问句不带 refs（与逐条路径一致）。");

        var ids = result.Envelopes.Select(e => e.CanonicalKey.EntityId).OrderBy(id => id).ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "ref-t", "plain" },
            ids,
            "首问句 refs 过滤到 ref-t，第二问句无 refs 命中 plain，合并后两者都在。");
    }

    [TestMethod]
    public async Task Provider_MultiQuery_FallbackToPerQueryLoop_WhenStoreLacksCapability()
    {
        var inner = new InMemoryContextStore();
        await SaveAsync(inner, "a-1", title: "alpha", content: "alpha topic");
        await SaveAsync(inner, "b-2", title: "beta", content: "beta topic");
        await SaveAsync(inner, "c-3", title: "gamma", content: "gamma topic");
        var store = new LegacyOnlyContextStore(inner);
        var provider = new LexicalCandidateProvider(store);

        var result = await provider.ExecuteAsync(MakeContext(
            queryText: "task",
            queryTexts: new[] { "alpha", "beta", "gamma" },
            includeContent: false));

        Assert.AreEqual(3, store.QueryCalls, "store 无批量能力时应回退为逐问句 QueryAsync。");
        var ids = result.Envelopes.Select(e => e.CanonicalKey.EntityId).OrderBy(id => id).ToArray();
        CollectionAssert.AreEquivalent(new[] { "a-1", "b-2", "c-3" }, ids, "回退路径结果集合应与批量路径一致。");
        Assert.AreEqual(100.0, result.Envelopes.Single(e => e.CanonicalKey.EntityId == "a-1").Utility.DeterministicScore, 0.001);
    }

    [TestMethod]
    public async Task Provider_SingleQueryText_UsesLegacyPath()
    {
        var inner = new InMemoryContextStore();
        await SaveAsync(inner, "a-1", title: "alpha", content: "alpha topic");
        var store = new RecordingContextStore(inner);
        var provider = new LexicalCandidateProvider(store);

        // 单问句（QueryTexts 缺省 → 回退单条）。
        await provider.ExecuteAsync(MakeContext("task", queryTexts: null, includeContent: false));
        Assert.AreEqual(1, store.QueryCalls, "单问句保持逐条路径（行为不变量）。");
        Assert.AreEqual(0, store.MultiQueryCalls);

        // 单条 QueryTexts（数量为 1）同样走逐条路径。
        await provider.ExecuteAsync(MakeContext("task", queryTexts: new[] { "alpha" }, includeContent: false));
        Assert.AreEqual(2, store.QueryCalls, "单条 QueryTexts 也走逐条路径。");
        Assert.AreEqual(0, store.MultiQueryCalls, "q=1 不启用批量（避免为单问句引入新 SQL 路径）。");
    }

    [TestMethod]
    public async Task Provider_MultiQuery_IncludeContentFalse_ReturnsEmptyContent()
    {
        var inner = new InMemoryContextStore();
        await SaveAsync(inner, "a-1", title: "alpha", content: "alpha topic");
        await SaveAsync(inner, "b-2", title: "beta", content: "beta topic");
        var store = new RecordingContextStore(inner);
        var provider = new LexicalCandidateProvider(store);

        var result = await provider.ExecuteAsync(MakeContext(
            "task",
            queryTexts: new[] { "alpha", "beta" },
            includeContent: false));

        Assert.IsTrue(
            result.Materials.Values.All(m => string.IsNullOrEmpty(m.Content)),
            "IncludeContent=false 时批量路径不应加载正文。");
    }

    [TestMethod]
    public async Task Provider_MultiQuery_ExcludedIds_ViaMetadata()
    {
        var inner = new InMemoryContextStore();
        await SaveAsync(inner, "a-1", title: "alpha", content: "alpha topic");
        await SaveAsync(inner, "b-2", title: "beta", content: "beta topic");
        var store = new RecordingContextStore(inner);
        var provider = new LexicalCandidateProvider(store);

        var result = await provider.ExecuteAsync(MakeContext(
            "task",
            queryTexts: new[] { "alpha", "beta" },
            includeContent: false,
            excludedIds: new[] { "a-1" }));

        Assert.AreEqual(1, store.MultiQueryCalls);
        var ids = result.Envelopes.Select(e => e.CanonicalKey.EntityId).ToArray();
        CollectionAssert.DoesNotContain(ids, "a-1", "排除 ID 不应出现在批量路径结果中。");
        CollectionAssert.Contains(ids, "b-2");
    }

    [TestMethod]
    public async Task Store_Parity_InMemory_MultiVsSequential()
    {
        var store = new InMemoryContextStore();
        await PopulateAsync(store);
        var multi = new ContextMultiQuery
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            Queries = new[]
            {
                new ContextMultiQueryText { QueryText = "alpha", Refs = new[] { "ref-x" } },
                new ContextMultiQueryText { QueryText = "beta" },
                new ContextMultiQueryText { QueryText = "gamma" },
                new ContextMultiQueryText { QueryText = string.Empty, Refs = new[] { "ref-x" } }
            },
            Take = 5,
            IncludeContent = false
        };

        var multiResults = await store.QueryMultiAsync(multi);

        for (var i = 0; i < multi.Queries.Count; i++)
        {
            var q = multi.Queries[i];
            var sequential = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                QueryText = q.QueryText,
                Refs = q.Refs,
                Take = 5,
                IncludeContent = false
            });
            var multiItem = multiResults.Single(r => r.QueryIndex == i);
            CollectionAssert.AreEqual(
                sequential.Select(item => item.Id).ToArray(),
                multiItem.Items.Select(item => item.Id).ToArray(),
                $"问句 [{q.QueryText}] refs=[{string.Join(",", q.Refs)}] 的批量结果应与逐条结果完全一致（含顺序）。");
            Assert.IsTrue(multiItem.Items.All(item => item.Content == string.Empty), "IncludeContent=false 批量路径正文为空。");
        }
    }

    [TestMethod]
    public async Task Store_Parity_FileSystem_MultiVsSequential()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-lr2b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileContextStore(new FileStorageOptions { RootPath = root });
            await PopulateAsync(store);
            var multi = new ContextMultiQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Queries = new[]
                {
                    new ContextMultiQueryText { QueryText = "alpha", Refs = new[] { "ref-x" } },
                    new ContextMultiQueryText { QueryText = "beta" },
                    new ContextMultiQueryText { QueryText = "gamma" },
                    new ContextMultiQueryText { QueryText = string.Empty, Refs = new[] { "ref-x" } }
                },
                Take = 5,
                IncludeContent = false
            };

            var multiResults = await store.QueryMultiAsync(multi);

            for (var i = 0; i < multi.Queries.Count; i++)
            {
                var q = multi.Queries[i];
                var sequential = await store.QueryAsync(new ContextQuery
                {
                    WorkspaceId = "ws",
                    CollectionId = "col",
                    QueryText = q.QueryText,
                    Refs = q.Refs,
                    Take = 5,
                    IncludeContent = false
                });
                var multiItem = multiResults.Single(r => r.QueryIndex == i);
                CollectionAssert.AreEqual(
                    sequential.Select(item => item.Id).ToArray(),
                    multiItem.Items.Select(item => item.Id).ToArray(),
                    $"FileSystem 问句 [{q.QueryText}] 的批量结果应与逐条结果完全一致（含顺序）。");
                Assert.IsTrue(multiItem.Items.All(item => item.Content == string.Empty), "IncludeContent=false 批量路径正文为空。");
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
    public async Task Store_MultiQuery_PerQueryTopK_Respected()
    {
        var store = new InMemoryContextStore();
        for (var i = 1; i <= 10; i++)
        {
            await store.SaveAsync(new ContextItem
            {
                Id = $"t-{i}",
                WorkspaceId = "ws",
                CollectionId = "col",
                Type = "note",
                Title = $"title-{i}",
                Content = "alpha common body",
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }

        var results = await store.QueryMultiAsync(new ContextMultiQuery
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            Queries = new[]
            {
                new ContextMultiQueryText { QueryText = "alpha" },
                new ContextMultiQueryText { QueryText = "t-5" }
            },
            Take = 3
        });

        foreach (var result in results)
        {
            Assert.IsTrue(result.Items.Count <= 3, $"问句 [{result.QueryText}] 应各自保留 TopK=3。");
        }
        Assert.AreEqual(1, results.Single(r => r.QueryText == "t-5").Items.Count, "t-5 只命中 id 含 t-5 的一条。");
    }

    [TestMethod]
    public async Task Store_MultiQuery_SharedExcludedAndTags_Applied()
    {
        var store = new InMemoryContextStore();
        await SaveAsync(store, "keep-1", title: "alpha", content: "alpha body", tags: new[] { "proj" });
        await SaveAsync(store, "keep-2", title: "beta", content: "beta body", tags: new[] { "proj" });
        await SaveAsync(store, "other-1", title: "gamma", content: "gamma body", tags: new[] { "other" });

        var results = await store.QueryMultiAsync(new ContextMultiQuery
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            Queries = new[]
            {
                new ContextMultiQueryText { QueryText = "alpha" },
                new ContextMultiQueryText { QueryText = "beta" },
                new ContextMultiQueryText { QueryText = "gamma" }
            },
            Tags = new[] { "proj" },
            ExcludedIds = new[] { "keep-2" },
            Take = 10
        });

        var allIds = results.SelectMany(r => r.Items).Select(item => item.Id).ToArray();
        CollectionAssert.DoesNotContain(allIds, "keep-2", "共享排除 ID 生效。");
        CollectionAssert.DoesNotContain(allIds, "other-1", "共享 tags 过滤生效。");
        CollectionAssert.Contains(allIds, "keep-1", "满足共享过滤的问句命中保留。");
    }

    [TestMethod]
    public async Task Store_MultiQuery_EmptyQueries_ReturnsEmpty()
    {
        var store = new InMemoryContextStore();
        var results = await store.QueryMultiAsync(new ContextMultiQuery
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            Queries = Array.Empty<ContextMultiQueryText>()
        });
        Assert.AreEqual(0, results.Count, "无问句时返回空结果。");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static async Task PopulateAsync(IContextStore store)
    {
        // ref-x 命中：refs-a（refs 含 ref-x）、refs-b（source_refs 含 ref-x）、refs-c（id 即 ref-x）。
        await SaveAsync(store, "refs-a", title: "alpha-a", content: "alpha body", refs: new[] { "ref-x" });
        await SaveAsync(store, "refs-b", title: "alpha-b", content: "alpha beta body", sourceRefs: new[] { "ref-x" });
        await SaveAsync(store, "refs-c", title: "gamma-c", content: "gamma body", refs: Array.Empty<string>());
        await SaveAsync(store, "plain-1", title: "beta-1", content: "beta gamma body");
        await SaveAsync(store, "plain-2", title: "gamma-2", content: "gamma alpha body");
    }

    private static async Task SaveAsync(
        IContextStore store,
        string id,
        string title,
        string content,
        IReadOnlyList<string>? refs = null,
        IReadOnlyList<string>? sourceRefs = null,
        IReadOnlyList<string>? tags = null)
    {
        await store.SaveAsync(new ContextItem
        {
            Id = id,
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "note",
            Title = title,
            Content = content,
            Refs = refs ?? Array.Empty<string>(),
            SourceRefs = sourceRefs ?? Array.Empty<string>(),
            Tags = tags ?? Array.Empty<string>(),
            // 固定 UpdatedAt 保证排序可预测（Id 为决胜键，逐条与批量路径排序一致）。
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private static CandidateProviderContext MakeContext(
        string queryText,
        IReadOnlyList<string>? queryTexts,
        bool includeContent,
        IReadOnlyList<string>? refs = null,
        IReadOnlyList<string>? excludedIds = null)
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
            ResolutionScope = new ContextDecisionScope("ws", "col")
        };

        var metadata = new Dictionary<string, string>();
        if (excludedIds is { Count: > 0 })
        {
            metadata["excludedIds"] = string.Join(",", excludedIds);
        }

        return new CandidateProviderContext(
            Request: new ContextDecisionRuntimeRequest
            {
                RequestId = "req-lr2b",
                Scope = new ContextDecisionScope("ws", "col"),
                Purpose = ContextDecisionPurpose.AgentContext,
                QueryText = queryText,
                TokenBudget = 4096,
                TopK = 10,
                RetrievalInput = new RetrievalInput
                {
                    IncludeContent = includeContent,
                    QueryTexts = queryTexts ?? Array.Empty<string>(),
                    Refs = refs ?? Array.Empty<string>(),
                    Metadata = metadata
                }
            },
            Policy: snapshot,
            Routing: new ExpertRoutingDecision
            {
                Expert = RetrievalExpert.Lexical,
                Enabled = true,
                TopK = 10,
                TokenBudget = 4096,
                Weight = 1.0,
                ReasonCode = "test"
            },
            AdaptationContext: new CandidateAdaptationContext
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                ObservedAt = DateTimeOffset.UtcNow
            });
    }

    private sealed class RecordingContextStore : IContextStore, IContextStoreMultiQuery
    {
        private readonly InMemoryContextStore _inner;
        public int QueryCalls;
        public int MultiQueryCalls;
        public ContextMultiQuery? LastMultiQuery;

        public RecordingContextStore(InMemoryContextStore inner) => _inner = inner;

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
            => _inner.SaveAsync(item, cancellationToken);

        public Task<ContextItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, collectionId, id, cancellationToken);

        public Task DeleteAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, collectionId, id, cancellationToken);

        public Task<IReadOnlyList<ContextItem>> QueryAsync(ContextQuery query, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            return _inner.QueryAsync(query, cancellationToken);
        }

        public Task<IReadOnlyList<ContextMultiQueryResult>> QueryMultiAsync(
            ContextMultiQuery query,
            CancellationToken cancellationToken = default)
        {
            MultiQueryCalls++;
            LastMultiQuery = query;
            return _inner.QueryMultiAsync(query, cancellationToken);
        }
    }

    private sealed class LegacyOnlyContextStore : IContextStore
    {
        private readonly IContextStore _inner;
        public int QueryCalls;

        public LegacyOnlyContextStore(IContextStore inner) => _inner = inner;

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
            => _inner.SaveAsync(item, cancellationToken);

        public Task<ContextItem?> GetAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, collectionId, id, cancellationToken);

        public Task DeleteAsync(string workspaceId, string collectionId, string id, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, collectionId, id, cancellationToken);

        public Task<IReadOnlyList<ContextItem>> QueryAsync(ContextQuery query, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            return _inner.QueryAsync(query, cancellationToken);
        }
    }
}
