using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>覆盖 P3-4 混合检索的规则召回、向量召回、关系扩展和 trace。</summary>
[TestClass]
[TestCategory("Retrieval")]
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
        var expansionService = new RelationExpansionService(new RelationTraversalEngine(relationStore), resolver);
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

}
