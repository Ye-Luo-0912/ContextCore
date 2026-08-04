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

/// <summary>覆盖混合检索的规则召回、向量召回、关系扩展和 trace。</summary>
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

    // Memory Recall 与 Keyword Recall 解耦。
    // 记忆条目不含查询关键词时仍应被召回（基于 importance/confidence/lifecycle），不被 MatchesMemoryQuery 硬过滤丢弃。
    // 这与 Package Build 路径（WorkingMemoryRecaller）的 anchor-based 评分语义一致。
    [TestMethod]
    public async Task MemoryRecallChannelExecutor_DoesNotDropMemoryOnKeywordMiss()
    {
        var memoryStore = new InMemoryMemoryStore();
        var executor = new MemoryRecallChannelExecutor(memoryStore);
        var now = DateTimeOffset.UtcNow;

        // 记忆内容与查询文本完全无关键词重叠——长期稳定事实不依赖查询词命中。
        await memoryStore.SaveAsync(Memory(
            "mem-stable-no-keyword-match",
            "项目使用 NET 10 框架与 Postgres 后端，所有数据层依赖强一致事务。",
            ContextMemoryStatus.Active,
            now,
            tags: ["architecture", "infrastructure"]));

        var result = await executor.ExecuteAsync(RetrievalChannelContext.Create(
            new ContextRetrievalRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "性能优化基准测试",
                IncludeWorkingMemory = true,
                IncludeStableMemory = false,
                CandidateTake = 10
            },
            new RetrievalPlan(),
            new Dictionary<string, string>()));

        Assert.AreEqual("记忆召回", result.StageName);
        // 关键断言：即使查询文本不含记忆内容关键词，记忆仍应被召回（未被 MatchesMemoryQuery 丢弃）。
        Assert.AreEqual(1, result.Candidates.Count,
            $"记忆条目应被召回（lifecycle valid + 解耦后无关键词硬过滤），实际候选数 {result.Candidates.Count}");
        Assert.AreEqual("mem-stable-no-keyword-match", result.Candidates[0].SourceId);
        // 查询未命中关键词，matchedTokens 应为空（trace 观察正确），但候选仍保留。
        Assert.AreEqual(0, result.Candidates[0].MatchedTokens.Count,
            "查询文本未命中记忆内容，matchedTokens 应为空");
    }

    // 4.1 矩阵测试：Memory Recall 必须独立于 Keyword Recall。
    // 当 IncludeKeywordRecall=false 时，Memory Channel 仍应按 IncludeWorkingMemory/IncludeStableMemory 执行。
    [TestMethod]
    public async Task MemoryRecall_KeywordDisabled_WorkingOnly_RecallsWorkingLayer()
    {
        var (retriever, workingId, stableId) = await BuildMemoryMatrixRetrieverAsync();
        var result = await retriever.RetrieveAsync(MatrixRequest(
            includeKeywordRecall: false, includeWorkingMemory: true, includeStableMemory: false));

        var ids = result.SelectedItems.Select(c => c.SourceId).ToArray();
        CollectionAssert.Contains(ids, workingId);
        CollectionAssert.DoesNotContain(ids, stableId);
    }

    [TestMethod]
    public async Task MemoryRecall_KeywordDisabled_StableOnly_RecallsStableLayer()
    {
        var (retriever, workingId, stableId) = await BuildMemoryMatrixRetrieverAsync();
        var result = await retriever.RetrieveAsync(MatrixRequest(
            includeKeywordRecall: false, includeWorkingMemory: false, includeStableMemory: true));

        var ids = result.SelectedItems.Select(c => c.SourceId).ToArray();
        CollectionAssert.DoesNotContain(ids, workingId);
        CollectionAssert.Contains(ids, stableId);
    }

    [TestMethod]
    public async Task MemoryRecall_KeywordDisabled_BothLayers_RecallsBothLayers()
    {
        var (retriever, workingId, stableId) = await BuildMemoryMatrixRetrieverAsync();
        var result = await retriever.RetrieveAsync(MatrixRequest(
            includeKeywordRecall: false, includeWorkingMemory: true, includeStableMemory: true));

        var ids = result.SelectedItems.Select(c => c.SourceId).ToArray();
        CollectionAssert.Contains(ids, workingId);
        CollectionAssert.Contains(ids, stableId);
    }

    [TestMethod]
    public async Task MemoryRecall_KeywordDisabled_NoMemoryFlags_DoesNotRecallMemory()
    {
        var (retriever, workingId, stableId) = await BuildMemoryMatrixRetrieverAsync();
        var result = await retriever.RetrieveAsync(MatrixRequest(
            includeKeywordRecall: false, includeWorkingMemory: false, includeStableMemory: false));

        var ids = result.SelectedItems.Select(c => c.SourceId).ToArray();
        CollectionAssert.DoesNotContain(ids, workingId);
        CollectionAssert.DoesNotContain(ids, stableId);
    }

    // 回归测试：per-layer quota 防止 Working Memory 饱和后压掉 Stable Memory。
    // 旧实现按 Working → Stable → Distinct → Take(candidateTake) 顺序追加截取，
    // 当 Working 返回数 >= candidateTake 时 Stable 会被全部截掉。
    // 新实现使用 per-layer quota + rollover，确保两层共存。

    [TestMethod]
    public async Task MemoryRecall_PerLayerQuota_PreservesStableWhenWorkingSaturates()
    {
        var memoryStore = new InMemoryMemoryStore();
        var executor = new MemoryRecallChannelExecutor(memoryStore);
        var now = DateTimeOffset.UtcNow;

        // 填充 8 个 Working 项（远超 candidateTake=4，旧实现会把 Stable 全部截掉）。
        for (var i = 0; i < 8; i++)
        {
            await memoryStore.SaveAsync(MakeLayeredMemory(
                $"mem-working-{i}",
                $"矩阵测试 Working 候选 {i}",
                ContextMemoryLayer.Working,
                ContextMemoryStatus.Active,
                now));
        }

        // 填充 4 个 Stable 项。
        for (var i = 0; i < 4; i++)
        {
            await memoryStore.SaveAsync(MakeLayeredMemory(
                $"mem-stable-{i}",
                $"矩阵测试 Stable 候选 {i}",
                ContextMemoryLayer.Stable,
                ContextMemoryStatus.Stable,
                now));
        }

        var result = await executor.ExecuteAsync(RetrievalChannelContext.Create(
            new ContextRetrievalRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "矩阵测试",
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                CandidateTake = 4,
                TopK = 10,
                TokenBudget = 1000
            },
            new RetrievalPlan { NeedsStableMemory = true },
            new Dictionary<string, string>()));

        var ids = result.Candidates.Select(c => c.SourceId).ToArray();

        // 关键断言：Stable 项必须出现在结果中（旧实现会被全部截掉）。
        var workingCount = ids.Count(id => id.StartsWith("mem-working-", StringComparison.Ordinal));
        var stableCount = ids.Count(id => id.StartsWith("mem-stable-", StringComparison.Ordinal));
        Assert.AreEqual(2, workingCount,
            $"Working 配额应为 2（candidateTake/2），实际 {workingCount}，全部 ids: {string.Join(", ", ids)}");
        Assert.AreEqual(2, stableCount,
            $"Stable 配额应为 2，实际 {stableCount}，全部 ids: {string.Join(", ", ids)}");
        Assert.AreEqual(4, ids.Length,
            $"结果总数应等于 candidateTake=4，实际 {ids.Length}");
    }

    [TestMethod]
    public async Task MemoryRecall_PerLayerQuota_RollsOverUnusedWorkingSlotsToStable()
    {
        var memoryStore = new InMemoryMemoryStore();
        var executor = new MemoryRecallChannelExecutor(memoryStore);
        var now = DateTimeOffset.UtcNow;

        // Working 只有 1 项（远少于 workingQuota=5）。
        await memoryStore.SaveAsync(MakeLayeredMemory(
            "mem-working-solo",
            "矩阵测试 Working 单项",
            ContextMemoryLayer.Working,
            ContextMemoryStatus.Active,
            now));

        // Stable 有 10 项（足够吸收 rollover）。
        for (var i = 0; i < 10; i++)
        {
            await memoryStore.SaveAsync(MakeLayeredMemory(
                $"mem-stable-{i}",
                $"矩阵测试 Stable 候选 {i}",
                ContextMemoryLayer.Stable,
                ContextMemoryStatus.Stable,
                now));
        }

        var result = await executor.ExecuteAsync(RetrievalChannelContext.Create(
            new ContextRetrievalRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "矩阵测试",
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                CandidateTake = 10,
                TopK = 10,
                TokenBudget = 1000
            },
            new RetrievalPlan { NeedsStableMemory = true },
            new Dictionary<string, string>()));

        var ids = result.Candidates.Select(c => c.SourceId).ToArray();

        // workingQuota=5, stableQuota=5；takenWorking=1 (< 5)，rollover=4 → takenStable=Take(5+4)=9。
        var workingCount = ids.Count(id => id.StartsWith("mem-working-", StringComparison.Ordinal));
        var stableCount = ids.Count(id => id.StartsWith("mem-stable-", StringComparison.Ordinal));
        Assert.AreEqual(1, workingCount,
            $"Working 应保持原数量 1，实际 {workingCount}，全部 ids: {string.Join(", ", ids)}");
        Assert.AreEqual(9, stableCount,
            $"Stable 应吸收 Working 未用配额达到 9 项，实际 {stableCount}，全部 ids: {string.Join(", ", ids)}");
    }

    [TestMethod]
    public async Task MemoryRecall_PerLayerQuota_RollsOverUnusedStableSlotsToWorking()
    {
        var memoryStore = new InMemoryMemoryStore();
        var executor = new MemoryRecallChannelExecutor(memoryStore);
        var now = DateTimeOffset.UtcNow;

        // Working 有 10 项。
        for (var i = 0; i < 10; i++)
        {
            await memoryStore.SaveAsync(MakeLayeredMemory(
                $"mem-working-{i}",
                $"矩阵测试 Working 候选 {i}",
                ContextMemoryLayer.Working,
                ContextMemoryStatus.Active,
                now));
        }

        // Stable 只有 1 项。
        await memoryStore.SaveAsync(MakeLayeredMemory(
            "mem-stable-solo",
            "矩阵测试 Stable 单项",
            ContextMemoryLayer.Stable,
            ContextMemoryStatus.Stable,
            now));

        var result = await executor.ExecuteAsync(RetrievalChannelContext.Create(
            new ContextRetrievalRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "矩阵测试",
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                CandidateTake = 10,
                TopK = 10,
                TokenBudget = 1000
            },
            new RetrievalPlan { NeedsStableMemory = true },
            new Dictionary<string, string>()));

        var ids = result.Candidates.Select(c => c.SourceId).ToArray();

        // workingQuota=5, stableQuota=5；takenWorking=5, takenStable=1 (< 5)，rollover=4 → takenWorking=Take(5+4)=9。
        var workingCount = ids.Count(id => id.StartsWith("mem-working-", StringComparison.Ordinal));
        var stableCount = ids.Count(id => id.StartsWith("mem-stable-", StringComparison.Ordinal));
        Assert.AreEqual(9, workingCount,
            $"Working 应吸收 Stable 未用配额达到 9 项，实际 {workingCount}，全部 ids: {string.Join(", ", ids)}");
        Assert.AreEqual(1, stableCount,
            $"Stable 应保持原数量 1，实际 {stableCount}，全部 ids: {string.Join(", ", ids)}");
    }

    private static ContextMemoryItem MakeLayeredMemory(
        string id, string content, ContextMemoryLayer layer, ContextMemoryStatus status, DateTimeOffset now)
    {
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = layer,
            Status = status,
            Type = "task",
            Content = content,
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["矩阵"],
            SourceRefs = [$"source:{id}"],
            Importance = 0.8,
            Confidence = 0.9,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static async Task<(HybridContextRetriever retriever, string workingId, string stableId)> BuildMemoryMatrixRetrieverAsync()
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

        const string workingId = "mem-matrix-working";
        const string stableId = "mem-matrix-stable";
        await memoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = workingId,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Working,
            Status = ContextMemoryStatus.Active,
            Type = "task",
            Content = "矩阵测试工作层候选内容",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["matrix"],
            SourceRefs = [$"source:{workingId}"],
            Importance = 0.8,
            Confidence = 0.9,
            CreatedAt = now,
            UpdatedAt = now
        });
        await memoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = stableId,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "task",
            Content = "矩阵测试稳定层候选内容",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["matrix"],
            SourceRefs = [$"source:{stableId}"],
            Importance = 0.8,
            Confidence = 0.9,
            CreatedAt = now,
            UpdatedAt = now
        });
        return (retriever, workingId, stableId);
    }

    private static ContextRetrievalRequest MatrixRequest(
        bool includeKeywordRecall, bool includeWorkingMemory, bool includeStableMemory)
    {
        return new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "矩阵测试",
            IncludeKeywordRecall = includeKeywordRecall,
            IncludeVectorRecall = false,
            IncludeWorkingMemory = includeWorkingMemory,
            IncludeStableMemory = includeStableMemory,
            IncludeRelationExpansion = false,
            TopK = 10,
            CandidateTake = 10,
            TokenBudget = 1000
        };
    }

    [TestMethod]
    public async Task VectorRecallChannelExecutor_ShouldReturnEmptyWhenDisabledAndDiagnosticWhenUnavailable()
    {
        var executor = new VectorRecallChannelExecutor(
            new InMemoryContextStore(),
            memoryStore: null,
            embeddingProvider: null,
            vectorStore: null,
            fanout: RetrievalFanoutOptions.Default);

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

    /// <summary>
    /// 验证：Mandatory 通道在 store 支持 <see cref="IContextStoreMetadataLookup"/> 时走元数据投影
    /// （Content 为空，不读正文 jsonb），正文由 Selected 水合阶段按需读取——与向量通道一致。
    /// </summary>
    [TestMethod]
    public async Task MandatoryRecallChannelExecutor_WithMetadataLookupStore_ProjectsMetadataOnly()
    {
        var store = new MetadataAwareInMemoryContextStore();
        var executor = new MandatoryRecallChannelExecutor(
            store, memoryStore: null, RetrievalFanoutOptions.Default);
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new ContextItem
        {
            Id = "required-meta",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Title = "强制项",
            Content = "完整正文（投影阶段不应读取）",
            Tags = [],
            CreatedAt = now,
            UpdatedAt = now
        });

        var result = await executor.ExecuteAsync(RetrievalChannelContext.Create(
            new ContextRetrievalRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                RequiredIds = ["required-meta"],
                CandidateTake = 10
            },
            new RetrievalPlan(),
            new Dictionary<string, string>()));

        Assert.IsTrue(store.MetadataBatchCalls >= 1, "支持元数据投影的 store 应走 BatchGetMetadataAsync。");
        Assert.AreEqual(0, store.FullBatchCalls, "不应调用全量 BatchGetAsync（正文由 Selected 水合）。");
        Assert.AreEqual(1, result.Candidates.Count);
        Assert.AreEqual("required-meta", result.Candidates[0].SourceId);
        Assert.IsTrue(string.IsNullOrEmpty(result.Candidates[0].Content),
            "投影后候选正文应为空（未选中不读正文）。");
    }

    /// <summary>
    /// 验证：元数据投影的 Mandatory 候选被选中后，正文由 SelectedCandidateContentHydrator 批量水合——
    /// 最终结果正文完整，与全量召回路径输出一致。
    /// </summary>
    [TestMethod]
    public async Task HybridContextRetriever_WithMetadataStore_MandatoryItem_HydratedWhenSelected()
    {
        var store = new MetadataAwareInMemoryContextStore();
        var retriever = new HybridContextRetriever(
            store, memoryStore: null, relationStore: null,
            embeddingProvider: null, vectorStore: null, traceStore: null);
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new ContextItem
        {
            Id = "required-hydrate",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Title = "必须保留",
            Content = "强制候选的完整正文，选中后必须水合回来。",
            Tags = [],
            CreatedAt = now,
            UpdatedAt = now
        });

        var result = await retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            OperationId = "hybrid-mandatory-hydrate",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "强制候选",
            RequiredIds = ["required-hydrate"],
            IncludeVectorRecall = false,
            IncludeWorkingMemory = false,
            IncludeStableMemory = false,
            TopK = 5,
            CandidateTake = 10,
            TokenBudget = 1000
        });

        var selected = result.SelectedItems.Single(item => item.SourceId == "required-hydrate");
        Assert.IsFalse(string.IsNullOrEmpty(selected.Content),
            "选中的强制候选应被水合完整正文。");
        Assert.AreEqual("强制候选的完整正文，选中后必须水合回来。", selected.Content);
        Assert.IsTrue(store.MetadataBatchCalls >= 1, "召回阶段应走元数据投影。");
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

    /// <summary>
    /// Retrieval deterministic CandidateId tie-break —
    /// 同 Score + 同 EstimatedTokens 的候选按 CandidateId 升序稳定排序，
    /// 避免依赖输入枚举顺序导致跨 Provider/并发 Channel 结果不稳定。
    /// </summary>
    [TestMethod]
    public async Task Retrieval_TieBreak_SameScore_DeterministicByCandidateId()
    {
        var contextStore = new InMemoryContextStore();
        var retriever = new HybridContextRetriever(contextStore);
        var now = DateTimeOffset.UtcNow;

        // 3 个相同 Score 的候选（通过相同关键词命中数构造），仅 CandidateId 不同
        await contextStore.SaveAsync(Item("zebra", "alpha beta gamma delta", ["tag"], now));
        await contextStore.SaveAsync(Item("alpha", "alpha beta gamma delta", ["tag"], now));
        await contextStore.SaveAsync(Item("mango", "alpha beta gamma delta", ["tag"], now));

        var result = await RetrieveForQueryAsync(retriever, "alpha beta gamma delta");

        // 3 个候选都应返回，Score 相同，按 CandidateId 升序排列
        Assert.IsTrue(result.SelectedItems.Count >= 3,
            $"至少应返回 3 个候选，实际 {result.SelectedItems.Count}");

        var sortedById = result.SelectedItems
            .Take(3)
            .Select(c => c.CandidateId)
            .ToArray();
        var expected = sortedById.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        CollectionAssert.AreEqual(expected, sortedById,
            $"同 Score 候选应按 CandidateId 升序排列，实际 {string.Join(", ", sortedById)}");
    }

    /// <summary>
    /// Relation quota 语义修正——Pack 阶段为 relation-only 候选预留 TopK 名额。
    /// 当 main 候选分数高于 relation-only 候选时，仍应保留部分 TopK 槽位给 relation-only 候选，
    /// 而不是让高分 main 候选全部挤掉 relation-only 候选（之前的 cap-only 语义缺陷）。
    /// </summary>
    [TestMethod]
    public void Pack_RelationQuota_PreservesSlotsForRelationOnlyCandidates()
    {
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TopK = 6,
            TokenBudget = 1000
        };

        // 6 个 main 候选（高分，若无 reservation 会全部占据 TopK）
        var mainCandidates = Enumerable.Range(0, 6)
            .Select(i => new ContextRetrievalCandidate
            {
                CandidateId = $"main-{i}",
                SourceId = $"main-{i}",
                Score = 0.9 - i * 0.01,
                EstimatedTokens = 10,
                Metadata = new Dictionary<string, string>()
            })
            .ToArray();

        // 4 个 relation-only 候选（低分，cap 后 2 个进入排序）
        var relationOnlyCandidates = Enumerable.Range(0, 4)
            .Select(i => new ContextRetrievalCandidate
            {
                CandidateId = $"rel-{i}",
                SourceId = $"rel-{i}",
                Score = 0.5 - i * 0.01,
                EstimatedTokens = 10,
                Metadata = new Dictionary<string, string>()
            })
            .ToArray();

        var relationOnlyIds = relationOnlyCandidates
            .Select(c => c.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ranked = RetrievalPackingPolicy.BuildRankedCandidates(
            request,
            mainCandidates,
            relationOnlyCandidates);

        var result = RetrievalPackingPolicy.Pack(request, ranked, relationOnlyIds);

        var selectedIds = result.SelectedCandidates.Select(c => c.CandidateId).ToArray();
        var relationSelected = selectedIds.Count(id => id.StartsWith("rel-", StringComparison.Ordinal));
        var mainSelected = selectedIds.Count(id => id.StartsWith("main-", StringComparison.Ordinal));

        // topK = 6, topK/3 = 2, max(2, 2) = 2, topK/2 = 3, reservedSlots = min(min(2, 2), 3) = 2
        // （注意：BuildRankedCandidates 的 cap 也是 2，所以 ranked 中只有 2 个 relation-only 候选）
        // mainSlots = 6 - 2 = 4
        // 预期：4 main + 2 relation-only
        Assert.AreEqual(2, relationSelected,
            $"reservation 应保留 2 个 relation-only 槽位，实际 {relationSelected}，全部 ids: {string.Join(", ", selectedIds)}");
        Assert.AreEqual(4, mainSelected,
            $"main 应占 4 个槽位，实际 {mainSelected}，全部 ids: {string.Join(", ", selectedIds)}");
        Assert.AreEqual(6, selectedIds.Length,
            $"TopK=6 应返回 6 个候选，实际 {selectedIds.Length}");
    }

    /// <summary>
    /// 当 relation-only 候选不足预留量时，未填满的槽位 rollover 给 main 候选。
    /// </summary>
    [TestMethod]
    public void Pack_RelationQuota_RollsOverUnusedSlotsToMain()
    {
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TopK = 6,
            TokenBudget = 1000
        };

        // 6 个 main 候选（高分）
        var mainCandidates = Enumerable.Range(0, 6)
            .Select(i => new ContextRetrievalCandidate
            {
                CandidateId = $"main-{i}",
                SourceId = $"main-{i}",
                Score = 0.9 - i * 0.01,
                EstimatedTokens = 10,
                Metadata = new Dictionary<string, string>()
            })
            .ToArray();

        // 1 个 relation-only 候选（少于 reservedSlots=2，剩余 1 槽位应 rollover 给 main）
        var relationOnlyCandidates = new[]
        {
            new ContextRetrievalCandidate
            {
                CandidateId = "rel-0",
                SourceId = "rel-0",
                Score = 0.5,
                EstimatedTokens = 10,
                Metadata = new Dictionary<string, string>()
            }
        };

        var relationOnlyIds = relationOnlyCandidates
            .Select(c => c.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ranked = RetrievalPackingPolicy.BuildRankedCandidates(
            request,
            mainCandidates,
            relationOnlyCandidates);

        var result = RetrievalPackingPolicy.Pack(request, ranked, relationOnlyIds);

        var selectedIds = result.SelectedCandidates.Select(c => c.CandidateId).ToArray();
        var relationSelected = selectedIds.Count(id => id.StartsWith("rel-", StringComparison.Ordinal));
        var mainSelected = selectedIds.Count(id => id.StartsWith("main-", StringComparison.Ordinal));

        // relationOnlyInRanked = 1, reservedSlots = min(min(1, 2), 3) = 1, mainSlots = 5
        // 阶段1: 5 main + 1 relation-only = 6 (满)
        // 预期：5 main + 1 relation-only
        Assert.AreEqual(1, relationSelected,
            $"relation-only 应占 1 个槽位（reservedSlots=1），实际 {relationSelected}");
        Assert.AreEqual(5, mainSelected,
            $"main 应占 5 个槽位（含 rollover），实际 {mainSelected}");
        Assert.AreEqual(6, selectedIds.Length,
            $"TopK=6 应返回 6 个候选，实际 {selectedIds.Length}");
    }

    /// <summary>
    /// 当 main 候选不足 mainSlots 时，未填满的槽位 rollover 给 relation-only 候选。
    /// </summary>
    [TestMethod]
    public void Pack_RelationQuota_RollsOverUnusedMainSlotsToRelationOnly()
    {
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TopK = 6,
            TokenBudget = 1000
        };

        // 2 个 main 候选（少于 mainSlots=4，剩余 2 槽位应 rollover 给 relation-only）
        var mainCandidates = Enumerable.Range(0, 2)
            .Select(i => new ContextRetrievalCandidate
            {
                CandidateId = $"main-{i}",
                SourceId = $"main-{i}",
                Score = 0.9 - i * 0.01,
                EstimatedTokens = 10,
                Metadata = new Dictionary<string, string>()
            })
            .ToArray();

        // 4 个 relation-only 候选（cap 后 2 个进入排序，但 rollover 后 2 个槽位也用完）
        var relationOnlyCandidates = Enumerable.Range(0, 4)
            .Select(i => new ContextRetrievalCandidate
            {
                CandidateId = $"rel-{i}",
                SourceId = $"rel-{i}",
                Score = 0.5 - i * 0.01,
                EstimatedTokens = 10,
                Metadata = new Dictionary<string, string>()
            })
            .ToArray();

        var relationOnlyIds = relationOnlyCandidates
            .Select(c => c.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ranked = RetrievalPackingPolicy.BuildRankedCandidates(
            request,
            mainCandidates,
            relationOnlyCandidates);

        var result = RetrievalPackingPolicy.Pack(request, ranked, relationOnlyIds);

        var selectedIds = result.SelectedCandidates.Select(c => c.CandidateId).ToArray();
        var relationSelected = selectedIds.Count(id => id.StartsWith("rel-", StringComparison.Ordinal));
        var mainSelected = selectedIds.Count(id => id.StartsWith("main-", StringComparison.Ordinal));

        // relationOnlyInRanked = 2 (cap=2), reservedSlots = min(min(2, 2), 3) = 2, mainSlots = 4
        // 阶段1: 2 main + 2 relation-only = 4 (< 6)
        // 阶段2 rollover: 剩余 2 槽位，但 dropped 中无候选（cap 已限制为 2 个 relation-only）
        // 预期：2 main + 2 relation-only = 4（无更多候选可填充）
        Assert.AreEqual(2, relationSelected,
            $"relation-only 应占 2 个槽位（reservedSlots=2），实际 {relationSelected}");
        Assert.AreEqual(2, mainSelected,
            $"main 应占 2 个槽位，实际 {mainSelected}");
        Assert.AreEqual(4, selectedIds.Length,
            $"无更多候选可填充，应返回 4 个，实际 {selectedIds.Length}");
    }

    /// <summary>
    /// 无 relation-only 候选时，reservation 不生效，行为与之前一致。
    /// </summary>
    [TestMethod]
    public void Pack_RelationQuota_NoRelationOnly_BehavesUnchanged()
    {
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TopK = 4,
            TokenBudget = 1000
        };

        var mainCandidates = Enumerable.Range(0, 6)
            .Select(i => new ContextRetrievalCandidate
            {
                CandidateId = $"main-{i}",
                SourceId = $"main-{i}",
                Score = 0.9 - i * 0.01,
                EstimatedTokens = 10,
                Metadata = new Dictionary<string, string>()
            })
            .ToArray();

        var ranked = RetrievalPackingPolicy.BuildRankedCandidates(
            request,
            mainCandidates,
            Array.Empty<ContextRetrievalCandidate>());

        // relationOnlyCandidateIds = null
        var result = RetrievalPackingPolicy.Pack(request, ranked, relationOnlyCandidateIds: null);

        Assert.AreEqual(4, result.SelectedCandidates.Count,
            "无 relation-only 候选时，应按 TopK=4 选前 4 个 main 候选");
        Assert.AreEqual("main-0", result.SelectedCandidates[0].CandidateId);
        Assert.AreEqual("main-1", result.SelectedCandidates[1].CandidateId);
        Assert.AreEqual("main-2", result.SelectedCandidates[2].CandidateId);
        Assert.AreEqual("main-3", result.SelectedCandidates[3].CandidateId);
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

    /// <summary>
    /// InMemoryContextStore 包装器：额外实现 <see cref="IContextStoreMetadataLookup"/>（投影正文），
    /// 并录制批量调用路径（验证 Mandatory 通道的元数据投影行为）。
    /// </summary>
    private sealed class MetadataAwareInMemoryContextStore : IContextStore, IContextStoreBatchLookup, IContextStoreMetadataLookup
    {
        private readonly InMemoryContextStore _inner = new();

        public int MetadataBatchCalls;
        public int FullBatchCalls;

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
            => _inner.SaveAsync(item, cancellationToken);

        public Task<ContextItem?> GetAsync(
            string workspaceId, string collectionId, string id,
            CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, collectionId, id, cancellationToken);

        public Task<IReadOnlyList<ContextItem>> QueryAsync(
            ContextQuery query, CancellationToken cancellationToken = default)
            => _inner.QueryAsync(query, cancellationToken);

        public Task DeleteAsync(
            string workspaceId, string collectionId, string id,
            CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, collectionId, id, cancellationToken);

        public Task<IReadOnlyList<ContextItem>> BatchGetAsync(
            string workspaceId, string collectionId, IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
        {
            FullBatchCalls++;
            return _inner.BatchGetAsync(workspaceId, collectionId, ids, cancellationToken);
        }

        public async Task<IReadOnlyList<ContextItem>> BatchGetMetadataAsync(
            string workspaceId, string collectionId, IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
        {
            MetadataBatchCalls++;
            var items = await _inner.BatchGetAsync(workspaceId, collectionId, ids, cancellationToken).ConfigureAwait(false);
            // 模拟 PostgresContextStore：只投影元数据列，Content 恒为空。
            return items.Select(item => new ContextItem
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Type = item.Type,
                Title = item.Title,
                Content = string.Empty,
                ContentFormat = item.ContentFormat,
                Tags = item.Tags,
                Refs = item.Refs,
                SourceRefs = item.SourceRefs,
                Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
                Importance = item.Importance,
                SourceOrder = item.SourceOrder,
                SearchRank = item.SearchRank,
                Version = item.Version,
                Checksum = item.Checksum,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt
            }).ToArray();
        }
    }

}
