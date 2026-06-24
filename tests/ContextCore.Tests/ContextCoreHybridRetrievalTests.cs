using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>覆盖 P3-4 混合检索的规则召回、向量召回、关系扩展和 trace。</summary>
[TestClass]
public sealed class ContextCoreHybridRetrievalTests
{
    [TestMethod]
    public async Task ContextRecallChannelExecutor_ShouldReturnKeywordCandidates()
    {
        var contextStore = new InMemoryContextStore();
        var executor = new ContextRecallChannelExecutor(contextStore);
        var now = DateTimeOffset.UtcNow;

        await contextStore.SaveAsync(Item("ctx-keyword", "ContextCore 关键词召回执行器测试。", ["executor"], now));

        var result = await executor.ExecuteAsync(RetrievalChannelContext.Create(
            new ContextRetrievalRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "关键词召回执行器",
                CandidateTake = 10
            },
            new RetrievalPlan(),
            new Dictionary<string, string>()));

        Assert.AreEqual("关键词召回", result.StageName);
        Assert.AreEqual(1, result.Candidates.Count);
        Assert.AreEqual("ctx-keyword", result.Candidates[0].SourceId);
        CollectionAssert.Contains(result.Candidates[0].MatchedTokens.ToArray(), "关键词召回执行器");
    }

    [TestMethod]
    public async Task MemoryRecallChannelExecutor_ShouldReturnMatchedAnchors()
    {
        var memoryStore = new InMemoryMemoryStore();
        var executor = new MemoryRecallChannelExecutor(memoryStore);
        var now = DateTimeOffset.UtcNow;

        await memoryStore.SaveAsync(Memory(
            "memory-keyword",
            "中文输出偏好和性能约束需要稳定召回。",
            ContextMemoryStatus.Active,
            now,
            tags: ["preference", "performance"]));

        var result = await executor.ExecuteAsync(RetrievalChannelContext.Create(
            new ContextRetrievalRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "中文输出偏好",
                IncludeWorkingMemory = true,
                IncludeStableMemory = false,
                CandidateTake = 10
            },
            new RetrievalPlan
            {
                PrimaryAnchors =
                [
                    new RetrievalAnchorEntry("中文输出", RetrievalAnchorRole.Primary, 1.0, "test", AnchorType.Constraint)
                ]
            },
            new Dictionary<string, string>()));

        Assert.AreEqual("记忆召回", result.StageName);
        Assert.AreEqual(1, result.Candidates.Count);
        CollectionAssert.Contains(result.Candidates[0].MatchedAnchors.ToArray(), "中文输出");
    }

    [TestMethod]
    public async Task VectorRecallChannelExecutor_ShouldReturnEmptyWhenDisabledAndDiagnosticWhenUnavailable()
    {
        var executor = new VectorRecallChannelExecutor(
            new InMemoryContextStore(),
            memoryStore: null,
            embeddingProvider: null,
            vectorStore: null);

        var disabled = await executor.ExecuteAsync(RetrievalChannelContext.Create(
            new ContextRetrievalRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                IncludeVectorRecall = false
            },
            new RetrievalPlan(),
            new Dictionary<string, string>()));
        Assert.AreEqual(0, disabled.Candidates.Count);
        Assert.AreEqual("vector recall disabled", disabled.Metadata["skipped"]);

        var unavailable = await executor.ExecuteAsync(RetrievalChannelContext.Create(
            new ContextRetrievalRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                IncludeVectorRecall = true,
                QueryText = "向量召回"
            },
            new RetrievalPlan(),
            new Dictionary<string, string>()));
        Assert.AreEqual(0, unavailable.Candidates.Count);
        Assert.AreEqual("未注册 IVectorStore", unavailable.Metadata["skipped"]);
    }

    [TestMethod]
    public async Task RelationRecallChannelExecutor_ShouldExpandFromMemorySeedCandidates()
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var now = DateTimeOffset.UtcNow;
        await memoryStore.SaveAsync(Memory(
            "memory-seed",
            "当前 memory seed 触发关系扩展。",
            ContextMemoryStatus.Active,
            now,
            tags: ["seed"]));
        await contextStore.SaveAsync(Item("relation-target", "只有通过 relation 才能命中的 context target。", ["target"], now));
        await relationStore.SaveAsync(Relation("rel-seed-target", "memory-seed", "relation-target", ContextRelationTypes.RelatedTo, now));

        var resolver = new DefaultContextObjectResolver(contextStore, memoryStore);
        var expansionService = new RelationExpansionService(relationStore, resolver);
        var executor = new RelationRecallChannelExecutor(new RelationFrontierBuilder(), expansionService);

        var result = await executor.ExecuteAsync(RetrievalChannelContext.Create(
            new ContextRetrievalRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                IncludeRelationExpansion = true,
                RelationExpansionDepth = 1,
                CandidateTake = 10
            },
            new RetrievalPlan(),
            new Dictionary<string, string>(),
            [
                Candidate("memory-seed", ContextRetrievalCandidateKind.MemoryItem, 8.0, new Dictionary<string, string>
                {
                    ["candidateSourceKind"] = "memory",
                    ["lifecycleStatus"] = ContextMemoryStatus.Active.ToString()
                })
            ]));

        Assert.AreEqual("关系扩展", result.StageName);
        Assert.AreEqual(1, result.Candidates.Count);
        Assert.AreEqual("relation-target", result.Candidates[0].SourceId);
        StringAssert.Contains(result.Candidates[0].RelationPaths.Single(), "memory-seed -[related_to]-> relation-target");
    }

    [TestMethod]
    public async Task HybridContextRetriever_ShouldInvokeOnlyEnabledExecutors()
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore,
            relationStore: null,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null);
        var now = DateTimeOffset.UtcNow;

        await contextStore.SaveAsync(Item("enabled-context", "只启用关键词与记忆通道。", ["enabled"], now));
        await memoryStore.SaveAsync(Memory("enabled-memory", "只启用关键词与记忆通道。", ContextMemoryStatus.Active, now, tags: ["enabled"]));

        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "只启用关键词与记忆通道",
            IncludeKeywordRecall = true,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false,
            IncludeWorkingMemory = true,
            IncludeStableMemory = false,
            CandidateTake = 10,
            TopK = 10,
            TokenBudget = 1000
        });

        var stageNames = result.Trace.Stages.Select(stage => stage.Name).ToArray();
        CollectionAssert.Contains(stageNames, "强制注入");
        CollectionAssert.Contains(stageNames, "关键词召回");
        CollectionAssert.Contains(stageNames, "记忆召回");
        CollectionAssert.DoesNotContain(stageNames, "向量召回");
        CollectionAssert.DoesNotContain(stageNames, "关系扩展");
    }

    [TestMethod]
    public async Task HybridContextRetriever_RankerShadowTraceCollection_ShouldStayDisabledByDefault()
    {
        var contextStore = new InMemoryContextStore();
        var traceStore = new InMemoryRetrievalTraceStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            traceStore: traceStore);
        var now = DateTimeOffset.UtcNow;
        await contextStore.SaveAsync(Item("ranker-shadow-default", "当前规则用于 ranker shadow 默认关闭测试。", ["ranker"], now));

        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "ranker shadow 默认关闭",
            IncludeKeywordRecall = true,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false,
            CandidateTake = 10,
            TopK = 5,
            TokenBudget = 1000
        });
        var traces = await traceStore.QueryRecentAsync("workspace-test", "collection-test", 10);

        Assert.IsFalse(result.Trace.RankerShadowTrace.RankerShadowEnabled);
        Assert.AreEqual(0, result.Trace.RankerShadowTrace.CandidateShadowScores.Count);
        Assert.AreEqual("false", result.Trace.Metadata["rankerShadowTraceCollectionEnabled"]);
        Assert.IsFalse(traces[0].RankerShadowTrace.RankerShadowEnabled);
    }

    [TestMethod]
    public async Task HybridContextRetriever_RankerShadowTraceCollection_ShouldRecordWithoutChangingOutput()
    {
        var baselineContextStore = new InMemoryContextStore();
        var shadowContextStore = new InMemoryContextStore();
        var traceStore = new InMemoryRetrievalTraceStore();
        var now = DateTimeOffset.UtcNow;
        var current = Item("ranker-shadow-current-v2", "当前 active 规则用于 lifecycle aware ranker shadow trace。", ["ranker"], now);
        var other = Item("ranker-shadow-other", "其他 ranker shadow trace 候选。", ["ranker"], now.AddMinutes(-1));
        await baselineContextStore.SaveAsync(current);
        await baselineContextStore.SaveAsync(other);
        await shadowContextStore.SaveAsync(current);
        await shadowContextStore.SaveAsync(other);

        var baseline = new HybridContextRetriever(
            baselineContextStore,
            traceStore: null);
        var shadow = new HybridContextRetriever(
            shadowContextStore,
            traceStore: traceStore,
            rankerShadowOptions: new LifecycleAwareRankerShadowOptions
            {
                TraceCollectionEnabled = true,
                Profile = "lifecycle-aware-v1",
                MaxCandidatesPerTrace = 50
            },
            rankerShadowTraceBuilder: new LifecycleAwareRankerTraceBuilder(new LifecycleAwareRankerShadowScorer()));
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "ranker shadow trace active 规则",
            IncludeKeywordRecall = true,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false,
            CandidateTake = 10,
            TopK = 5,
            TokenBudget = 1000
        };

        var baselineResult = await baseline.RetrieveAsync(request);
        var shadowResult = await shadow.RetrieveAsync(request);
        var traces = await traceStore.QueryRecentAsync("workspace-test", "collection-test", 10);

        CollectionAssert.AreEqual(
            baselineResult.SelectedItems.Select(static item => item.CandidateId).ToArray(),
            shadowResult.SelectedItems.Select(static item => item.CandidateId).ToArray());
        Assert.IsTrue(shadowResult.Trace.RankerShadowTrace.RankerShadowEnabled);
        Assert.IsTrue(shadowResult.Trace.RankerShadowTrace.CandidateShadowScores.Count > 0);
        Assert.AreEqual("false", shadowResult.Trace.Metadata["rankerShadowFormalOutputChanged"]);
        Assert.AreEqual("false", shadowResult.Trace.Metadata["rankerShadowSelectedSetChanged"]);
        Assert.AreEqual("false", shadowResult.Trace.Metadata["rankerShadowPackageSectionsChanged"]);
        Assert.IsTrue(traces[0].RankerShadowTrace.CandidateShadowScores.Count > 0);
    }

    [TestMethod]
    public async Task HybridContextRetriever_GraphExpansionShadowTraceCollection_ShouldStayDisabledByDefault()
    {
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var traceStore = new InMemoryRetrievalTraceStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            relationStore: relationStore,
            traceStore: traceStore);
        var now = DateTimeOffset.UtcNow;
        await contextStore.SaveAsync(Item("graph-shadow-default", "graph shadow 默认关闭测试。", ["graph"], now));
        await relationStore.SaveAsync(GraphRelation(
            "rel-default-audit",
            "graph-shadow-default",
            "graph-shadow-old",
            ContextRelationTypes.Replaces,
            GraphExpansionTargetSection.AuditContext,
            StableMemoryLifecycle.Deprecated,
            now));

        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "graph shadow 默认关闭",
            IncludeKeywordRecall = true,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false,
            CandidateTake = 10,
            TopK = 5,
            TokenBudget = 1000
        });
        var traces = await traceStore.QueryRecentAsync("workspace-test", "collection-test", 10);

        Assert.IsFalse(result.Trace.GraphExpansionShadowTrace.GraphExpansionShadowEnabled);
        Assert.AreEqual(0, result.Trace.GraphExpansionShadowTrace.AcceptedRelations.Count);
        Assert.AreEqual("false", result.Trace.Metadata["graphExpansionShadowTraceCollectionEnabled"]);
        Assert.IsFalse(traces[0].GraphExpansionShadowTrace.GraphExpansionShadowEnabled);
    }

    [TestMethod]
    public async Task HybridContextRetriever_GraphExpansionShadowTraceCollection_ShouldRecordWithoutChangingOutput()
    {
        var baselineContextStore = new InMemoryContextStore();
        var shadowContextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var traceStore = new InMemoryRetrievalTraceStore();
        var now = DateTimeOffset.UtcNow;
        var seed = Item("graph-shadow-current", "graph shadow current seed for audit conflict.", ["graph"], now);
        await baselineContextStore.SaveAsync(seed);
        await shadowContextStore.SaveAsync(seed);
        await relationStore.SaveAsync(GraphRelation(
            "rel-graph-shadow-old",
            "graph-shadow-current",
            "graph-shadow-old",
            ContextRelationTypes.Replaces,
            GraphExpansionTargetSection.AuditContext,
            StableMemoryLifecycle.Deprecated,
            now));

        var baseline = new HybridContextRetriever(
            baselineContextStore,
            traceStore: null);
        var shadow = new HybridContextRetriever(
            shadowContextStore,
            relationStore: relationStore,
            traceStore: traceStore,
            graphExpansionShadowOptions: new GraphExpansionShadowOptions
            {
                Enabled = true,
                TraceCollectionEnabled = true,
                Profiles = ["audit-v1", "conflict-v1"],
                MaxRelationsPerTrace = 50
            },
            graphExpansionShadowTraceBuilder: CreateGraphExpansionShadowTraceBuilder(relationStore));
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "graph shadow current seed",
            IncludeKeywordRecall = true,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false,
            CandidateTake = 10,
            TopK = 5,
            TokenBudget = 1000
        };

        var baselineResult = await baseline.RetrieveAsync(request);
        var shadowResult = await shadow.RetrieveAsync(request);
        var traces = await traceStore.QueryRecentAsync("workspace-test", "collection-test", 10);

        CollectionAssert.AreEqual(
            baselineResult.SelectedItems.Select(static item => item.CandidateId).ToArray(),
            shadowResult.SelectedItems.Select(static item => item.CandidateId).ToArray());
        Assert.IsTrue(shadowResult.Trace.GraphExpansionShadowTrace.GraphExpansionShadowEnabled);
        Assert.IsTrue(shadowResult.Trace.GraphExpansionShadowTrace.AcceptedRelations.Count >= 2);
        Assert.IsTrue(shadowResult.Trace.GraphExpansionShadowTrace.TargetSections.ContainsKey(GraphExpansionTargetSection.AuditContext));
        Assert.IsTrue(shadowResult.Trace.GraphExpansionShadowTrace.TargetSections.ContainsKey(GraphExpansionTargetSection.ConflictEvidence));
        Assert.AreEqual(0, shadowResult.Trace.GraphExpansionShadowTrace.RiskAfterRouting);
        Assert.AreEqual("false", shadowResult.Trace.Metadata["graphExpansionFormalOutputChanged"]);
        Assert.AreEqual("false", shadowResult.Trace.Metadata["graphExpansionSelectedSetChanged"]);
        Assert.AreEqual("false", shadowResult.Trace.Metadata["graphExpansionPackageSectionsChanged"]);
        Assert.IsTrue(traces[0].GraphExpansionShadowTrace.AcceptedRelations.Count >= 2);
    }

    [TestMethod]
    public async Task HybridContextRetriever_GraphExpansionShadowTraceCollection_ShouldSuppressDuplicatePayload()
    {
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var traceStore = new InMemoryRetrievalTraceStore();
        var now = DateTimeOffset.UtcNow;
        var seed = Item("graph-shadow-dedupe-current", "graph shadow dedupe current seed.", ["graph", "dedupe"], now);
        await contextStore.SaveAsync(seed);
        await relationStore.SaveAsync(GraphRelation(
            "rel-graph-shadow-dedupe-old",
            "graph-shadow-dedupe-current",
            "graph-shadow-dedupe-old",
            ContextRelationTypes.Replaces,
            GraphExpansionTargetSection.AuditContext,
            StableMemoryLifecycle.Deprecated,
            now));

        var retriever = new HybridContextRetriever(
            contextStore,
            relationStore: relationStore,
            traceStore: traceStore,
            graphExpansionShadowOptions: new GraphExpansionShadowOptions
            {
                Enabled = true,
                TraceCollectionEnabled = true,
                Profiles = ["audit-v1", "conflict-v1"],
                MaxRelationsPerTrace = 50
            },
            graphExpansionShadowTraceBuilder: CreateGraphExpansionShadowTraceBuilder(relationStore));
        var firstRequest = new ContextRetrievalRequest
        {
            OperationId = "graph-shadow-dedupe-first",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "graph shadow dedupe current seed",
            IncludeKeywordRecall = true,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false,
            CandidateTake = 10,
            TopK = 5,
            TokenBudget = 1000
        };
        var secondRequest = new ContextRetrievalRequest
        {
            OperationId = "graph-shadow-dedupe-second",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = firstRequest.QueryText,
            IncludeKeywordRecall = true,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false,
            CandidateTake = 10,
            TopK = 5,
            TokenBudget = 1000
        };

        var first = await retriever.RetrieveAsync(firstRequest);
        var second = await retriever.RetrieveAsync(secondRequest);
        var traces = await traceStore.QueryRecentAsync("workspace-test", "collection-test", 10);

        CollectionAssert.AreEqual(
            first.SelectedItems.Select(static item => item.CandidateId).ToArray(),
            second.SelectedItems.Select(static item => item.CandidateId).ToArray());
        Assert.IsTrue(first.Trace.GraphExpansionShadowTrace.AcceptedRelations.Count > 0);
        Assert.AreEqual(0, second.Trace.GraphExpansionShadowTrace.AcceptedRelations.Count);
        Assert.AreEqual("true", second.Trace.Metadata["graphExpansionDuplicateSuppressed"]);
        Assert.AreEqual("graph-shadow-dedupe-first", second.Trace.Metadata["graphExpansionDuplicateOfRetrievalId"]);
        Assert.AreEqual("true", second.Trace.GraphExpansionShadowTrace.Metadata["duplicateSuppressed"]);
        Assert.AreEqual(2, traces.Count);
    }

    [TestMethod]
    public async Task HybridContextRetriever_ShouldCombineKeywordVectorRelationAndPacking()
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var vectorStore = new InMemoryVectorStore();
        var traceStore = new InMemoryRetrievalTraceStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore,
            relationStore,
            embeddingProvider: null,
            vectorStore,
            traceStore);
        var now = DateTimeOffset.UtcNow;

        await contextStore.SaveAsync(new ContextItem
        {
            Id = "required",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "constraint-note",
            Title = "必须注入",
            Content = "这个条目无论分数如何都必须注入检索结果。",
            Tags = ["system"],
            Importance = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        await contextStore.SaveAsync(new ContextItem
        {
            Id = "raw-memory",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Title = "上下文记忆检索",
            Content = "上下文记忆系统需要稳定保存用户偏好，并在检索时找回相关事实。",
            Tags = ["memory"],
            Importance = 0.8,
            CreatedAt = now,
            UpdatedAt = now
        });
        await contextStore.SaveAsync(new ContextItem
        {
            Id = "related-rule",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "rule",
            Title = "长期记忆规则",
            Content = "长期记忆固化时需要保留来源引用和关系线索。",
            Tags = ["memory"],
            Importance = 0.7,
            CreatedAt = now,
            UpdatedAt = now
        });
        await contextStore.SaveAsync(new ContextItem
        {
            Id = "unrelated",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Title = "天气记录",
            Content = "今天气温很高，适合喝冰水。",
            Tags = ["memory"],
            Importance = 0.1,   // 须 > 0.05 才能通过 importance 过滤，但分数低于其他项从而被 TopK 丢弃
            CreatedAt = now,
            UpdatedAt = now
        });
        await memoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "stable-preference",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "preference",
            Content = "用户偏好中文输出、中文日志和清晰的上下文管理结果。",
            Tags = ["memory"],
            Importance = 0.9,
            Confidence = 0.95,
            CreatedAt = now,
            UpdatedAt = now
        });
        await relationStore.SaveAsync(new ContextRelation
        {
            Id = "rel-raw-rule",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "raw-memory",
            TargetId = "related-rule",
            RelationType = ContextRelationTypes.RelatedTo,
            Weight = 0.9,
            Confidence = 0.9,
            CreatedAt = now
        });
        await vectorStore.UpsertAsync(Vector("vec-raw", "raw-memory", "context", [1f, 0f], now));
        await vectorStore.UpsertAsync(Vector("vec-memory", "stable-preference", "memory", [0.95f, 0.05f], now));
        await vectorStore.UpsertAsync(Vector("vec-unrelated", "unrelated", "context", [0.1f, 0.9f], now));

        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            OperationId = "hybrid-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "上下文记忆",
            RequiredTags = ["memory"],
            RequiredIds = ["required"],
            QueryVector = [1f, 0f],
            TopK = 4,
            CandidateTake = 10,
            VectorTopK = 10,
            TokenBudget = 1000
        });
        var selectedIds = result.SelectedItems.Select(item => item.SourceId).ToArray();
        var rawMemory = result.SelectedItems.Single(item => item.SourceId == "raw-memory");
        var traces = await traceStore.QueryRecentAsync("workspace-test", "collection-test", 5);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.Contains(selectedIds, "required");
        CollectionAssert.Contains(selectedIds, "raw-memory");
        CollectionAssert.Contains(selectedIds, "stable-preference");
        CollectionAssert.Contains(selectedIds, "related-rule");
        Assert.IsTrue(rawMemory.Reasons.Any(reason => reason.Contains("关键词", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(rawMemory.Reasons.Any(reason => reason.Contains("向量", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(result.DroppedItems.Any(item => item.SourceId == "unrelated"));
        Assert.IsTrue(result.Trace.Stages.Any(stage => stage.Name == "关键词召回"));
        Assert.IsTrue(result.Trace.Stages.Any(stage => stage.Name == "向量召回"));
        Assert.IsTrue(result.Trace.Stages.Any(stage => stage.Name == "关系扩展"));
        Assert.AreEqual("hybrid-test", traces.Single().RetrievalId);
    }

    [TestMethod]
    public async Task HybridContextRetriever_ShadowAttention_ShouldNotChangeRetrievalResult()
    {
        var now = DateTimeOffset.UtcNow;
        var baselineContextStore = new InMemoryContextStore();
        var shadowContextStore = new InMemoryContextStore();
        var baselineTraceStore = new InMemoryRetrievalTraceStore();
        var shadowTraceStore = new InMemoryRetrievalTraceStore();
        await baselineContextStore.SaveAsync(Item("shadow-a", "shadow attention 关键词命中高重要性。", ["shadow"], now));
        await baselineContextStore.SaveAsync(Item("shadow-b", "shadow attention 关键词命中低重要性。", ["shadow"], now));
        await shadowContextStore.SaveAsync(Item("shadow-a", "shadow attention 关键词命中高重要性。", ["shadow"], now));
        await shadowContextStore.SaveAsync(Item("shadow-b", "shadow attention 关键词命中低重要性。", ["shadow"], now));

        var baselineRetriever = new HybridContextRetriever(
            baselineContextStore,
            memoryStore: null,
            relationStore: null,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: baselineTraceStore);
        var shadowRetriever = new HybridContextRetriever(
            shadowContextStore,
            memoryStore: null,
            relationStore: null,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: shadowTraceStore,
            attentionScorer: new RuleBasedContextAttentionScorer());
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "shadow attention",
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            IncludeRelationExpansion = false,
            TopK = 2,
            CandidateTake = 10,
            TokenBudget = 1000
        };

        var baseline = await baselineRetriever.RetrieveAsync(request);
        var shadow = await shadowRetriever.RetrieveAsync(request);

        CollectionAssert.AreEqual(
            baseline.SelectedItems.Select(item => item.SourceId).ToArray(),
            shadow.SelectedItems.Select(item => item.SourceId).ToArray());
        CollectionAssert.AreEqual(
            baseline.DroppedItems.Select(item => item.SourceId).ToArray(),
            shadow.DroppedItems.Select(item => item.SourceId).ToArray());
        Assert.AreEqual(0, baseline.Trace.AttentionScores.Count);
        Assert.AreEqual(shadow.Trace.Candidates.Count, shadow.Trace.AttentionScores.Count);
        Assert.AreEqual(shadow.Trace.Candidates.Count, shadow.Trace.AttentionShadowReport.Ranks.Count);
        Assert.IsTrue(shadow.Trace.AttentionShadowReport.Ranks.All(rank => rank.CurrentRank > 0));
        Assert.IsTrue(shadow.Trace.AttentionShadowReport.Ranks.All(rank => rank.AttentionRank > 0));
        var expectedProfileCount = ContextAttentionProfile.CreateShadowExperimentProfiles().Count;
        Assert.AreEqual(expectedProfileCount, shadow.Trace.AttentionProfileComparison.Profiles.Count);
        CollectionAssert.Contains(
            shadow.Trace.AttentionProfileComparison.Profiles.Select(profile => profile.ProfileId).ToArray(),
            "conservative-v1");
        CollectionAssert.Contains(
            shadow.Trace.AttentionProfileComparison.Profiles.Select(profile => profile.ProfileId).ToArray(),
            "guarded-shadow-v1");
        Assert.IsTrue(shadow.Trace.AttentionProfileComparison.Profiles.All(profile =>
            profile.ShadowReport.Ranks.Count == shadow.Trace.Candidates.Count));
        Assert.AreEqual("true", shadow.Trace.Metadata["attentionShadowMode"]);
        Assert.IsFalse(shadow.Trace.AttentionRerankComparison.Applied);
        Assert.IsTrue(shadow.Trace.AttentionRerankComparison.Skipped);
        Assert.AreEqual("off", shadow.Trace.AttentionRerankComparison.SkippedReason);
        Assert.AreEqual(RetrievalAttentionRerankOptions.OffMode, shadow.Trace.AttentionRerankComparison.AttentionRerankMode);
        Assert.AreEqual("old-score-anchored-v1-strong", shadow.Trace.AttentionRerankComparison.AttentionProfile);
        Assert.IsFalse(shadow.Trace.AttentionRerankComparison.AttentionApplied);
        Assert.IsTrue(shadow.Trace.AttentionRerankComparison.SelectedSetPreserved);
        Assert.AreEqual(RetrievalAttentionRerankOptions.OffMode, shadow.Trace.Metadata["attentionRerankMode"]);
        Assert.AreEqual("old-score-anchored-v1-strong", shadow.Trace.Metadata["attentionProfile"]);
        Assert.AreEqual("false", shadow.Trace.Metadata["attentionRerankApplied"]);
    }

    [TestMethod]
    public async Task RetrievalDebugPackageOutput_ShouldRemainUnchangedWithShadowAttention()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var vectorStore = new InMemoryVectorStore();
        var traceStore = new InMemoryRetrievalTraceStore();
        await contextStore.SaveAsync(Item("pkg-a", "package shadow attention 主要命中。", ["pkg"], now));
        await contextStore.SaveAsync(Item("pkg-b", "package shadow attention 次要命中。", ["pkg"], now));

        var baselineRetriever = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore,
            embeddingProvider: null,
            vectorStore: vectorStore,
            traceStore: traceStore);
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "package shadow attention",
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            IncludeRelationExpansion = false,
            TopK = 2,
            CandidateTake = 10,
            TokenBudget = 1000
        };
        var baselineResult = await baselineRetriever.RetrieveAsync(request);

        var shadowTraceStore = new InMemoryRetrievalTraceStore();
        var shadowRetriever = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore,
            embeddingProvider: null,
            vectorStore: vectorStore,
            traceStore: shadowTraceStore,
            attentionScorer: new RuleBasedContextAttentionScorer());
        var shadowResult = await shadowRetriever.RetrieveAsync(request);

        CollectionAssert.AreEqual(
            baselineResult.SelectedItems.Select(item => $"{item.Kind}:{item.SourceId}").ToArray(),
            shadowResult.SelectedItems.Select(item => $"{item.Kind}:{item.SourceId}").ToArray());
        CollectionAssert.AreEqual(
            baselineResult.SelectedItems.Select(item => item.SourceId).ToArray(),
            shadowResult.SelectedItems.Select(item => item.SourceId).ToArray());
        Assert.AreEqual(
            baselineResult.SelectedItems.Sum(item => item.EstimatedTokens),
            shadowResult.SelectedItems.Sum(item => item.EstimatedTokens));
        Assert.IsTrue(shadowResult.Trace.AttentionScores.Count > 0);
        Assert.IsTrue(shadowResult.Trace.AttentionShadowReport.Ranks.Count > 0);
        Assert.AreEqual(
            shadowResult.Trace.SelectedItems.Count,
            shadowResult.Trace.AttentionShadowReport.SelectedCount);
        var expectedProfileCount = ContextAttentionProfile.CreateShadowExperimentProfiles().Count;
        Assert.AreEqual(expectedProfileCount, shadowResult.Trace.AttentionProfileComparison.Profiles.Count);
        Assert.IsTrue(shadowResult.Trace.AttentionProfileComparison.Profiles.All(profile =>
            profile.ShadowReport.SelectedCount == shadowResult.Trace.SelectedItems.Count));
    }

    [TestMethod]
    public async Task HybridContextRetriever_GuardedRerankEnabled_ShouldChangeOrderOnly()
    {
        var contextStore = new InMemoryContextStore();
        var now = DateTimeOffset.UtcNow;
        await contextStore.SaveAsync(new ContextItem
        {
            Id = "rerank-current-top",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Content = "guarded rerank selected set preserving current top",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["rerank"],
            SourceRefs = ["source:rerank-current-top"],
            Importance = 1.0,
            CreatedAt = now,
            UpdatedAt = now
        });
        await contextStore.SaveAsync(new ContextItem
        {
            Id = "rerank-attention-top",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Content = "guarded rerank selected set preserving attention top",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["rerank"],
            SourceRefs = ["source:rerank-attention-top"],
            Importance = 0.6,
            CreatedAt = now,
            UpdatedAt = now
        });
        await contextStore.SaveAsync(new ContextItem
        {
            Id = "rerank-dropped",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Content = "guarded rerank unrelated dropped candidate",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["rerank"],
            SourceRefs = ["source:rerank-dropped"],
            Importance = 0.1,
            CreatedAt = now,
            UpdatedAt = now
        });

        var disabledRetriever = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore: null,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null,
            attentionScorer: new StaticAttentionScorer(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ContextItem:rerank-current-top"] = 2,
                ["ContextItem:rerank-attention-top"] = 1,
                ["ContextItem:rerank-dropped"] = 3
            }));
        var enabledRetriever = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore: null,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null,
            attentionScorer: new StaticAttentionScorer(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ContextItem:rerank-current-top"] = 2,
                ["ContextItem:rerank-attention-top"] = 1,
                ["ContextItem:rerank-dropped"] = 3
            }),
            attentionRerankOptions: new RetrievalAttentionRerankOptions
            {
                Mode = RetrievalAttentionRerankOptions.ApplyGuardedMode,
                Profile = "static-test"
            });
        var shadowRerankRetriever = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore: null,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null,
            attentionScorer: new StaticAttentionScorer(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ContextItem:rerank-current-top"] = 2,
                ["ContextItem:rerank-attention-top"] = 1,
                ["ContextItem:rerank-dropped"] = 3
            }),
            attentionRerankOptions: new RetrievalAttentionRerankOptions
            {
                Mode = RetrievalAttentionRerankOptions.ShadowMode,
                Profile = "static-test"
            });
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "guarded rerank selected set preserving",
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            IncludeRelationExpansion = false,
            TopK = 2,
            CandidateTake = 10,
            TokenBudget = 1000
        };

        var disabled = await disabledRetriever.RetrieveAsync(request);
        var shadowRerank = await shadowRerankRetriever.RetrieveAsync(request);
        var enabled = await enabledRetriever.RetrieveAsync(request);

        CollectionAssert.AreEqual(
            new[] { "rerank-current-top", "rerank-attention-top" },
            disabled.SelectedItems.Select(item => item.SourceId).ToArray());
        CollectionAssert.AreEqual(
            disabled.SelectedItems.Select(item => item.SourceId).ToArray(),
            shadowRerank.SelectedItems.Select(item => item.SourceId).ToArray());
        Assert.IsFalse(shadowRerank.Trace.AttentionRerankComparison.AttentionApplied);
        Assert.IsTrue(shadowRerank.Trace.AttentionRerankComparison.Skipped);
        Assert.AreEqual(RetrievalAttentionRerankOptions.ShadowMode, shadowRerank.Trace.AttentionRerankComparison.AttentionRerankMode);
        Assert.AreEqual(2, shadowRerank.Trace.AttentionRerankComparison.OrderChangedCount);
        Assert.IsTrue(shadowRerank.Trace.AttentionRerankComparison.SelectedSetPreserved);
        CollectionAssert.AreEqual(
            new[] { "rerank-attention-top", "rerank-current-top" },
            enabled.SelectedItems.Select(item => item.SourceId).ToArray());
        CollectionAssert.AreEquivalent(
            disabled.SelectedItems.Select(item => item.SourceId).ToArray(),
            enabled.SelectedItems.Select(item => item.SourceId).ToArray());
        CollectionAssert.AreEqual(
            disabled.DroppedItems.Select(item => item.SourceId).ToArray(),
            enabled.DroppedItems.Select(item => item.SourceId).ToArray());
        Assert.IsTrue(enabled.Trace.AttentionRerankComparison.Applied);
        Assert.IsTrue(enabled.Trace.AttentionRerankComparison.AttentionApplied);
        Assert.AreEqual(RetrievalAttentionRerankOptions.ApplyGuardedMode, enabled.Trace.AttentionRerankComparison.AttentionRerankMode);
        Assert.AreEqual("static-test", enabled.Trace.AttentionRerankComparison.AttentionProfile);
        Assert.IsTrue(enabled.Trace.AttentionRerankComparison.SelectedSetPreserved);
        Assert.AreEqual(2, enabled.Trace.AttentionRerankComparison.OrderChangedCount);
        CollectionAssert.AreEqual(
            new[] { "rerank-current-top", "rerank-attention-top" },
            enabled.Trace.AttentionRerankComparison.OldOrder.ToArray());
        CollectionAssert.AreEqual(
            new[] { "rerank-attention-top", "rerank-current-top" },
            enabled.Trace.AttentionRerankComparison.NewOrder.ToArray());
        Assert.AreEqual(0, enabled.Trace.AttentionRerankComparison.SelectedSetChangeCount);
        Assert.AreEqual(0, enabled.Trace.AttentionRerankComparison.AddedItems.Count);
        Assert.AreEqual(0, enabled.Trace.AttentionRerankComparison.DroppedItems.Count);
        Assert.IsTrue(enabled.Trace.AttentionShadowReport.Ranks.Count > 0);
    }

    [TestMethod]
    public async Task HybridContextRetriever_GuardedRerank_ShouldBlockMustNotHitPromotion()
    {
        var contextStore = new InMemoryContextStore();
        var now = DateTimeOffset.UtcNow;
        await contextStore.SaveAsync(new ContextItem
        {
            Id = "rerank-safe",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Content = "guarded rerank must not hit safe item",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["rerank"],
            SourceRefs = ["source:rerank-safe"],
            Importance = 1.0,
            CreatedAt = now,
            UpdatedAt = now
        });
        await contextStore.SaveAsync(new ContextItem
        {
            Id = "rerank-noise",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Content = "guarded rerank must not hit noise item",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["rerank"],
            SourceRefs = ["source:rerank-noise"],
            Importance = 0.2,
            CreatedAt = now,
            UpdatedAt = now
        });

        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore: null,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null,
            attentionScorer: new StaticAttentionScorer(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ContextItem:rerank-safe"] = 2,
                ["ContextItem:rerank-noise"] = 1
            }),
            attentionRerankOptions: new RetrievalAttentionRerankOptions
            {
                Mode = RetrievalAttentionRerankOptions.ApplyGuardedMode,
                Profile = "static-test"
            });

        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "guarded rerank must not hit",
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            IncludeRelationExpansion = false,
            TopK = 2,
            CandidateTake = 10,
            TokenBudget = 1000,
            Metadata = new Dictionary<string, string>
            {
                ["attention.mustNotHit"] = "rerank-noise"
            }
        });

        CollectionAssert.AreEqual(
            new[] { "rerank-safe", "rerank-noise" },
            result.SelectedItems.Select(item => item.SourceId).ToArray());
        Assert.IsFalse(result.Trace.AttentionRerankComparison.Applied);
        Assert.IsFalse(result.Trace.AttentionRerankComparison.AttentionApplied);
        Assert.IsTrue(result.Trace.AttentionRerankComparison.Blocked);
        Assert.AreEqual("must_not_hit_promotion_blocked", result.Trace.AttentionRerankComparison.BlockedReason);
        Assert.AreEqual("must_not_hit_promotion_blocked", result.Trace.AttentionRerankComparison.GuardViolation);
        Assert.IsTrue(result.Trace.AttentionRerankComparison.SelectedSetPreserved);
        CollectionAssert.AreEqual(
            new[] { "rerank-safe", "rerank-noise" },
            result.Trace.AttentionRerankComparison.NewOrder.ToArray());
        CollectionAssert.AreEqual(
            new[] { "rerank-safe", "rerank-noise" },
            result.Trace.SelectedItems.Select(item => item.SourceId).ToArray());
        Assert.AreEqual(1, result.Trace.AttentionRerankComparison.MustNotHitRankDeltas.Count);
    }

    [TestMethod]
    public async Task HybridContextRetriever_ShouldExpandRelationsByDepthAndWhitelist()
    {
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null);
        var now = DateTimeOffset.UtcNow;

        await contextStore.SaveAsync(Item("graph-seed", "多层召回图谱入口，只应通过关系继续扩展。", ["graph"], now));
        await contextStore.SaveAsync(Item("graph-hop-1", "一跳设计决策：保留关系扩展结果。", ["relation"], now));
        await contextStore.SaveAsync(Item("graph-hop-2", "二跳依赖信息：用于验证深度限制。", ["relation"], now));
        await contextStore.SaveAsync(Item("graph-blocked", "重复噪音信息，不应通过白名单关系进入结果。", ["relation"], now));

        await relationStore.SaveAsync(Relation("rel-hop-1", "graph-seed", "graph-hop-1", ContextRelationTypes.RelatedTo, now));
        await relationStore.SaveAsync(Relation("rel-hop-2", "graph-hop-1", "graph-hop-2", ContextRelationTypes.DependsOn, now));
        await relationStore.SaveAsync(Relation("rel-blocked", "graph-seed", "graph-blocked", ContextRelationTypes.Duplicates, now));

        var oneHop = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "图谱入口",
            RequiredTags = ["graph"],
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            RelationExpansionDepth = 1,
            AllowedRelationTypes = [ContextRelationTypes.RelatedTo, ContextRelationTypes.DependsOn],
            TopK = 5,
            CandidateTake = 10,
            TokenBudget = 1000
        });
        var oneHopIds = oneHop.SelectedItems.Select(item => item.SourceId).ToArray();

        CollectionAssert.Contains(oneHopIds, "graph-seed");
        CollectionAssert.Contains(oneHopIds, "graph-hop-1");
        CollectionAssert.DoesNotContain(oneHopIds, "graph-hop-2");
        CollectionAssert.DoesNotContain(oneHopIds, "graph-blocked");

        var twoHop = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "图谱入口",
            RequiredTags = ["graph"],
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            RelationExpansionDepth = 2,
            AllowedRelationTypes = [ContextRelationTypes.RelatedTo, ContextRelationTypes.DependsOn],
            TopK = 5,
            CandidateTake = 10,
            TokenBudget = 1000
        });
        var twoHopIds = twoHop.SelectedItems.Select(item => item.SourceId).ToArray();
        var relationStage = twoHop.Trace.Stages.Single(stage => stage.Name == "关系扩展");

        CollectionAssert.Contains(twoHopIds, "graph-seed");
        CollectionAssert.Contains(twoHopIds, "graph-hop-1");
        CollectionAssert.Contains(twoHopIds, "graph-hop-2");
        CollectionAssert.DoesNotContain(twoHopIds, "graph-blocked");
        Assert.AreEqual("2", relationStage.Metadata["depth"]);
        StringAssert.Contains(relationStage.Metadata["allowedRelationTypes"], ContextRelationTypes.DependsOn);
    }

    [TestMethod]
    public async Task HybridContextRetriever_ShouldMergeKeywordAndRelationHitsIntoSingleCandidate()
    {
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null);
        var now = DateTimeOffset.UtcNow;

        await contextStore.SaveAsync(Item("merge-seed", "共享候选的关系入口。", ["merge"], now));
        await contextStore.SaveAsync(Item("shared-hit", "共享候选同时命中关键词和关系扩展。", ["merge"], now));
        await relationStore.SaveAsync(Relation("rel-merge", "merge-seed", "shared-hit", ContextRelationTypes.RelatedTo, now));

        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "共享候选",
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            RelationExpansionDepth = 1,
            AllowedRelationTypes = [ContextRelationTypes.RelatedTo],
            TopK = 5,
            CandidateTake = 10,
            TokenBudget = 1000
        });

        var sharedHits = result.SelectedItems.Where(item => item.SourceId == "shared-hit").ToArray();
        var sharedHit = sharedHits.Single();

        Assert.AreEqual(1, sharedHits.Length);
        Assert.IsTrue(sharedHit.Reasons.Any(reason => reason.Contains("关键词", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(sharedHit.Reasons.Any(reason => reason.Contains("关系扩展", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("keyword,relation", sharedHit.Metadata["channelSources"]);
        Assert.AreEqual("relation", sharedHit.Metadata["alsoReferencedBy"]);
        StringAssert.Contains(sharedHit.Metadata["relationPaths"], "merge-seed -[related_to]-> shared-hit");
        StringAssert.Contains(sharedHit.Metadata["scoreBreakdown"], "keyword=");
        StringAssert.Contains(sharedHit.Metadata["scoreBreakdown"], "relation=");
        StringAssert.Contains(sharedHit.Metadata["scoreBreakdown"], "total=");
        StringAssert.Contains(sharedHit.Metadata["matchedTokens"], "共享候选");
    }

    [TestMethod]
    public async Task DefaultContextObjectResolver_ShouldResolveContextMemoryAndReturnMissingDiagnostic()
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var resolver = new DefaultContextObjectResolver(contextStore, memoryStore);
        var now = DateTimeOffset.UtcNow;

        await contextStore.SaveAsync(Item("resolver-context", "resolver 应优先命中 context item。", ["resolver"], now));
        await memoryStore.SaveAsync(Memory("resolver-memory", "resolver 应回退命中 memory item。", ContextMemoryStatus.Active, now));

        var contextResolution = await resolver.ResolveAsync("workspace-test", "collection-test", "resolver-context");
        var memoryResolution = await resolver.ResolveAsync("workspace-test", "collection-test", "resolver-memory");
        var batch = await resolver.ResolveManyAsync("workspace-test", "collection-test", ["resolver-context", "resolver-memory", "missing-target"]);

        Assert.IsTrue(contextResolution.Found);
        Assert.AreEqual(ContextRetrievalCandidateKind.ContextItem, contextResolution.ResolvedObject!.Kind);
        Assert.IsTrue(memoryResolution.Found);
        Assert.AreEqual(ContextRetrievalCandidateKind.MemoryItem, memoryResolution.ResolvedObject!.Kind);

        var missing = batch.Single(item => item.RequestedId == "missing-target");
        Assert.IsFalse(missing.Found);
        Assert.AreEqual("TargetNotFound", missing.DiagnosticCode);
        StringAssert.Contains(missing.DiagnosticMessage!, "missing-target");
    }

    [TestMethod]
    public void RelationFrontierBuilder_ShouldFilterRejectedDeprecatedAndSupersededSeeds()
    {
        var builder = new RelationFrontierBuilder();
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            RelationExpansionDepth = 1,
            CandidateTake = 10
        };
        var candidates = new[]
        {
            Candidate("seed-active", ContextRetrievalCandidateKind.MemoryItem, 8.0, new Dictionary<string, string>
            {
                ["lifecycleStatus"] = ContextMemoryStatus.Active.ToString(),
                ["candidateSourceKind"] = "memory"
            }),
            Candidate("seed-rejected", ContextRetrievalCandidateKind.MemoryItem, 9.0, new Dictionary<string, string>
            {
                ["lifecycleStatus"] = ContextMemoryStatus.Rejected.ToString(),
                ["candidateSourceKind"] = "memory"
            }),
            Candidate("seed-deprecated", ContextRetrievalCandidateKind.MemoryItem, 7.0, new Dictionary<string, string>
            {
                ["lifecycleStatus"] = ContextMemoryStatus.Deprecated.ToString(),
                ["candidateSourceKind"] = "memory"
            }),
            Candidate("seed-superseded", ContextRetrievalCandidateKind.MemoryItem, 6.5, new Dictionary<string, string>
            {
                ["lifecycleStatus"] = ContextMemoryStatus.Active.ToString(),
                ["candidateSourceKind"] = "memory",
                ["supersededBy"] = "seed-new"
            }),
            Candidate("ctx-deprecated", ContextRetrievalCandidateKind.ContextItem, 6.0, new Dictionary<string, string>
            {
                ["candidateSourceKind"] = "context",
                ["status"] = "deprecated"
            })
        };

        var normalFrontier = builder.Build(request, new RetrievalPlan(), candidates);
        CollectionAssert.AreEquivalent(
            new[] { "seed-active" },
            normalFrontier.Seeds.Select(item => item.SourceId).ToArray());

        var auditFrontier = builder.Build(request, new RetrievalPlan
        {
            AuditAnchors =
            [
                new RetrievalAnchorEntry("audit", RetrievalAnchorRole.Audit, 1.0, "test", AnchorType.Intent)
            ]
        }, candidates);

        var auditIds = auditFrontier.Seeds.Select(item => item.SourceId).ToArray();
        CollectionAssert.Contains(auditIds, "seed-active");
        CollectionAssert.Contains(auditIds, "seed-deprecated");
        CollectionAssert.Contains(auditIds, "seed-superseded");
        CollectionAssert.Contains(auditIds, "ctx-deprecated");
        CollectionAssert.DoesNotContain(auditIds, "seed-rejected");
    }

    [TestMethod]
    public async Task HybridContextRetriever_ShouldPreserveS14S25S26RelationExpansionRegressions()
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore,
            relationStore,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null);
        var now = DateTimeOffset.UtcNow;

        await memoryStore.SaveAsync(Memory(
            "memory:active-retrieval-task",
            "当前活跃任务（A5 §7.1）：为 ContextCore 检索服务建立专项评测集。",
            ContextMemoryStatus.Active,
            now,
            tags: ["task", "eval", "retrieval"]));
        await memoryStore.SaveAsync(Memory(
            "memory:side-effect-cache-clear",
            "索引重建后置步骤：调用 EmbeddingCacheService.InvalidateAll() 清除缓存。",
            ContextMemoryStatus.Active,
            now,
            tags: ["cache", "procedure"]));
        await memoryStore.SaveAsync(Memory(
            "memory:arch-hybrid-decision",
            "架构决策（ADR-011）：采用混合检索策略，向量语义相似度与 BM25 线性融合。",
            ContextMemoryStatus.Active,
            now,
            tags: ["architecture", "bm25", "retrieval"]));
        await memoryStore.SaveAsync(Memory(
            "memory:sprint-3-goal",
            "Sprint 3 目标（2026-05 冲刺）：完成存储层可读写健康检查接口和 eval 扩充。",
            ContextMemoryStatus.Active,
            now,
            tags: ["sprint", "2026-05", "task"]));
        await contextStore.SaveAsync(Item(
            "ret:ci-pipeline",
            "CI/CD 配置：GitHub Actions 触发于 PR 合并至 main，执行 build/test/coverage。",
            ["ci-cd", "pipeline"],
            now));
        await contextStore.SaveAsync(Item(
            "ret:storage-health-check",
            "存储层健康检查接口：Context/Memory/Relation 六种存储执行 probe 并记录延迟。",
            ["storage", "health-check", "probe"],
            now));

        await relationStore.SaveAsync(Relation("rel:task-sideeffect", "memory:active-retrieval-task", "memory:side-effect-cache-clear", ContextRelationTypes.RelatedTo, now));
        await relationStore.SaveAsync(Relation("rel:arch-ci", "memory:arch-hybrid-decision", "ret:ci-pipeline", ContextRelationTypes.RelatedTo, now));
        await relationStore.SaveAsync(Relation("rel:sprint-storage-check", "memory:sprint-3-goal", "ret:storage-health-check", ContextRelationTypes.RelatedTo, now));

        var s14 = await RetrieveForQueryAsync(retriever, "A5 §7.1 检索评测集建立任务的当前进展");
        var s25 = await RetrieveForQueryAsync(retriever, "ADR-011 混合检索策略的完整论证记录和评测数据对比");
        var s26 = await RetrieveForQueryAsync(retriever, "2026-05 月度冲刺计划截止节点和全部工作项清单");

        CollectionAssert.Contains(s14.SelectedItems.Select(item => item.SourceId).ToArray(), "memory:active-retrieval-task");
        CollectionAssert.Contains(s14.SelectedItems.Select(item => item.SourceId).ToArray(), "memory:side-effect-cache-clear");
        CollectionAssert.Contains(s25.SelectedItems.Select(item => item.SourceId).ToArray(), "memory:arch-hybrid-decision");
        CollectionAssert.Contains(s25.SelectedItems.Select(item => item.SourceId).ToArray(), "ret:ci-pipeline");
        CollectionAssert.Contains(s26.SelectedItems.Select(item => item.SourceId).ToArray(), "memory:sprint-3-goal");
        CollectionAssert.Contains(s26.SelectedItems.Select(item => item.SourceId).ToArray(), "ret:storage-health-check");
    }

    [TestMethod]
    public async Task HybridContextRetriever_ShouldNotExpandRejectedOrDeprecatedSeedsOutsideAudit()
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore,
            relationStore,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null);
        var now = DateTimeOffset.UtcNow;

        await memoryStore.SaveAsync(Memory("seed-rejected", "被拒绝的 memory seed。", ContextMemoryStatus.Rejected, now));
        await memoryStore.SaveAsync(Memory("seed-deprecated", "已废弃的 memory seed。", ContextMemoryStatus.Deprecated, now));
        await contextStore.SaveAsync(Item("target-from-rejected", "不应从 rejected seed 扩展到这里。", ["target"], now));
        await contextStore.SaveAsync(Item("target-from-deprecated", "非 audit 模式下不应从 deprecated seed 扩展到这里。", ["target"], now));

        await relationStore.SaveAsync(Relation("rel-rejected", "seed-rejected", "target-from-rejected", ContextRelationTypes.RelatedTo, now));
        await relationStore.SaveAsync(Relation("rel-deprecated", "seed-deprecated", "target-from-deprecated", ContextRelationTypes.RelatedTo, now));

        var normal = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            RequiredIds = ["seed-rejected", "seed-deprecated"],
            IncludeKeywordRecall = false,
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            IncludeRelationExpansion = true,
            RelationExpansionDepth = 1,
            TopK = 10,
            CandidateTake = 10,
            TokenBudget = 1000
        });

        CollectionAssert.DoesNotContain(normal.SelectedItems.Select(item => item.SourceId).ToArray(), "target-from-rejected");
        CollectionAssert.DoesNotContain(normal.SelectedItems.Select(item => item.SourceId).ToArray(), "target-from-deprecated");

        var audit = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            RequiredIds = ["seed-rejected", "seed-deprecated"],
            IncludeKeywordRecall = false,
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            IncludeRelationExpansion = true,
            RelationExpansionDepth = 1,
            TopK = 10,
            CandidateTake = 10,
            TokenBudget = 1000,
            Plan = new RetrievalPlan
            {
                AuditAnchors =
                [
                    new RetrievalAnchorEntry("audit", RetrievalAnchorRole.Audit, 1.0, "test", AnchorType.Intent)
                ]
            }
        });

        CollectionAssert.DoesNotContain(audit.SelectedItems.Select(item => item.SourceId).ToArray(), "target-from-rejected");
        CollectionAssert.Contains(audit.SelectedItems.Select(item => item.SourceId).ToArray(), "target-from-deprecated");
    }

    [TestMethod]
    public async Task HybridContextRetriever_ShouldMergeDuplicateRelationCandidatesAndPreserveAllPaths()
    {
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null);
        var now = DateTimeOffset.UtcNow;

        await contextStore.SaveAsync(Item("seed-a", "双入口候选 A。", ["dual"], now));
        await contextStore.SaveAsync(Item("seed-b", "双入口候选 B。", ["dual"], now));
        await contextStore.SaveAsync(Item("shared-relation-target", "这个目标只应出现一次。", ["target"], now));
        await relationStore.SaveAsync(Relation("rel-a", "seed-a", "shared-relation-target", ContextRelationTypes.RelatedTo, now));
        await relationStore.SaveAsync(Relation("rel-b", "seed-b", "shared-relation-target", ContextRelationTypes.RelatedTo, now));

        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "双入口候选",
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            IncludeRelationExpansion = true,
            RelationExpansionDepth = 1,
            TopK = 10,
            CandidateTake = 10,
            TokenBudget = 1000
        });

        var shared = result.SelectedItems.Where(item => item.SourceId == "shared-relation-target").ToArray();
        Assert.AreEqual(1, shared.Length);
        StringAssert.Contains(shared[0].Metadata["relationPaths"], "seed-a -[related_to]-> shared-relation-target");
        StringAssert.Contains(shared[0].Metadata["relationPaths"], "seed-b -[related_to]-> shared-relation-target");
        StringAssert.Contains(shared[0].Metadata["scoreBreakdown"], "relation=");
    }

    [TestMethod]
    public async Task HybridContextRetriever_ShouldKeepMandatoryItemsWhenTokenBudgetIsTight()
    {
        var contextStore = new InMemoryContextStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore: null,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null);
        var now = DateTimeOffset.UtcNow;

        await contextStore.SaveAsync(new ContextItem
        {
            Id = "required-large",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Title = "必须保留",
            Content = new string('A', 240),
            Tags = ["required"],
            Importance = 1.0,
            CreatedAt = now,
            UpdatedAt = now
        });
        await contextStore.SaveAsync(new ContextItem
        {
            Id = "budget-normal",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Title = "预算相关候选",
            Content = "预算打包需要保留强制项，并在预算不足时丢弃普通候选。",
            Tags = ["budget"],
            Importance = 0.8,
            CreatedAt = now,
            UpdatedAt = now
        });

        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "预算打包",
            RequiredIds = ["required-large"],
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            TopK = 5,
            CandidateTake = 10,
            TokenBudget = 20
        });

        CollectionAssert.Contains(result.SelectedItems.Select(item => item.SourceId).ToArray(), "required-large");
        Assert.IsTrue(result.DroppedItems.Any(item =>
            item.SourceId == "budget-normal"
            && item.Reason == "超过 token 预算"));
    }

    [TestMethod]
    public async Task FileVectorStore_ShouldPersistSearchAndRetrievalTrace()
    {
        var root = Path.Combine(
            Environment.CurrentDirectory,
            ".appdata",
            "tests",
            "vector-store",
            Guid.NewGuid().ToString("N"));
        var options = new FileStorageOptions { RootPath = root };
        var vectorStore = new FileVectorStore(options);
        var traceStore = new FileRetrievalTraceStore(options);
        var now = DateTimeOffset.UtcNow;

        try
        {
            await vectorStore.UpsertAsync(Vector("vec-a", "item-a", "context", [1f, 0f], now));
            await vectorStore.UpsertAsync(Vector("vec-b", "item-b", "context", [0f, 1f], now));

            var results = await vectorStore.SearchAsync(new VectorQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Vector = [1f, 0f],
                TopK = 1,
                IncludeVector = false
            });
            await traceStore.SaveAsync(new ContextRetrievalTrace
            {
                RetrievalId = "trace-file-test",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "上下文记忆",
                CreatedAt = now
            });
            var traces = await traceStore.QueryRecentAsync("workspace-test", "collection-test", 10);

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("item-a", results[0].Record.SourceId);
            Assert.AreEqual(0, results[0].Record.Vector.Count);
            Assert.IsTrue(results[0].Score > 0.99);
            Assert.AreEqual("trace-file-test", traces.Single().RetrievalId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
            Tags = ["memory"],
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ContextItem Item(
        string id,
        string content,
        IReadOnlyList<string> tags,
        DateTimeOffset now)
    {
        return new ContextItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Content = content,
            ContentFormat = ContextContentFormat.PlainText,
            Tags = tags,
            SourceRefs = [$"source:{id}"],
            Importance = 0.8,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ContextRelation Relation(
        string id,
        string sourceId,
        string targetId,
        string relationType,
        DateTimeOffset now)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 0.9,
            Confidence = 0.9,
            CreatedAt = now
        };
    }

    private static ContextRelation GraphRelation(
        string id,
        string sourceId,
        string targetId,
        string relationType,
        string targetSection,
        string targetLifecycle,
        DateTimeOffset now)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 1.0,
            Confidence = 1.0,
            SourceRefs = [$"review:{id}"],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["targetLifecycle"] = targetLifecycle,
                ["targetSection"] = targetSection,
                ["lifecycle"] = StableMemoryLifecycle.Active,
                ["reviewStatus"] = RelationReviewStatuses.Reviewed,
                ["evidenceRefs"] = $"review:{id}"
            },
            CreatedAt = now
        };
    }

    private static GraphExpansionShadowTraceBuilder CreateGraphExpansionShadowTraceBuilder(IRelationStore relationStore)
    {
        var profileRegistry = new RelationExpansionProfileRegistry();
        var validator = new RelationExpansionPolicyValidator(new RelationTypeRegistry());
        var previewService = new RelationExpansionPreviewService(relationStore, profileRegistry, validator);
        return new GraphExpansionShadowTraceBuilder(previewService);
    }

    private static ContextMemoryItem Memory(
        string id,
        string content,
        ContextMemoryStatus status,
        DateTimeOffset now,
        IReadOnlyList<string>? tags = null)
    {
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Working,
            Status = status,
            Type = "task",
            Content = content,
            ContentFormat = ContextContentFormat.PlainText,
            Tags = tags ?? ["memory"],
            SourceRefs = [$"source:{id}"],
            Importance = 0.8,
            Confidence = 0.9,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ContextRetrievalCandidate Candidate(
        string sourceId,
        ContextRetrievalCandidateKind kind,
        double score,
        Dictionary<string, string> metadata)
    {
        return new ContextRetrievalCandidate
        {
            CandidateId = $"{kind}:{sourceId}",
            SourceId = sourceId,
            Kind = kind,
            Type = "note",
            Content = string.Empty,
            ContentFormat = ContextContentFormat.PlainText,
            Tags = [],
            SourceRefs = [$"source:{sourceId}"],
            Score = score,
            EstimatedTokens = 1,
            Reasons = [],
            Metadata = metadata
        };
    }

    private static Task<ContextRetrievalResult> RetrieveForQueryAsync(HybridContextRetriever retriever, string query)
    {
        return retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = query,
            IncludeVectorRecall = false,
            IncludeWorkingMemory = true,
            IncludeStableMemory = false,
            IncludeRelationExpansion = true,
            RelationExpansionDepth = 1,
            TopK = 10,
            CandidateTake = 10,
            TokenBudget = 1000
        });
    }

    private sealed class StaticAttentionScorer : IContextAttentionScorer
    {
        private readonly IReadOnlyDictionary<string, int> _attentionRanks;

        public StaticAttentionScorer(IReadOnlyDictionary<string, int> attentionRanks)
        {
            _attentionRanks = attentionRanks;
        }

        public Task<IReadOnlyList<ContextAttentionScore>> ScoreAsync(
            ContextRetrievalRequest request,
            IReadOnlyList<ContextRetrievalCandidate> rankedCandidates,
            CancellationToken cancellationToken = default)
        {
            var scores = rankedCandidates
                .Select((candidate, index) =>
                {
                    var attentionRank = _attentionRanks.GetValueOrDefault(candidate.CandidateId, index + 1);
                    return new ContextAttentionScore
                    {
                        CandidateId = candidate.CandidateId,
                        SourceId = candidate.SourceId,
                        CandidateKind = candidate.Kind,
                        CurrentRank = index + 1,
                        AttentionRank = attentionRank,
                        FinalAttentionScore = Math.Max(0d, 1d - attentionRank / 10d),
                        QueryMatchScore = 1d,
                        Reasons = ["static_test_attention_rank"],
                        ProfileId = "static-test",
                        PolicyVersion = "test/static-test"
                    };
                })
                .ToArray();

            return Task.FromResult<IReadOnlyList<ContextAttentionScore>>(scores);
        }
    }
}
