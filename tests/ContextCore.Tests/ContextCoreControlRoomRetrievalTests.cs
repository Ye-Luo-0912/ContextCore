using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>覆盖 ControlRoom 的 Retrieval Debug 展示链路。</summary>
[TestClass]
public sealed class ContextCoreControlRoomRetrievalTests
{
    [TestMethod]
    public async Task ControlRoomRetrievalDebug_ShouldExposeTraceCandidatesAndPackageSections()
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var vectorStore = new InMemoryVectorStore();
        var traceStore = new InMemoryRetrievalTraceStore();
        var embeddingProvider = new MockEmbeddingProvider(new EmbeddingOptions
        {
            ModelName = "control-room-test",
            Dimensions = 2
        });
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore,
            relationStore,
            embeddingProvider,
            vectorStore,
            traceStore,
            new RuleBasedContextAttentionScorer());
        var state = CreateState(
            contextStore,
            memoryStore,
            relationStore,
            vectorStore,
            traceStore,
            embeddingProvider,
            retriever);
        var service = new ControlRoomService(state);
        var now = DateTimeOffset.UtcNow;

        await contextStore.SaveAsync(Item(
            "raw-memory",
            "note",
            "上下文记忆系统需要稳定保存用户偏好，并在检索时找回相关事实。",
            ["memory"],
            now));
        await contextStore.SaveAsync(Item(
            "related-rule",
            "rule",
            "长期记忆固化时需要保留来源引用和关系线索。",
            ["memory"],
            now));
        await contextStore.SaveAsync(Item(
            "unrelated",
            "note",
            "今天气温很高，适合喝冰水。",
            ["weather"],
            now));
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
        await vectorStore.UpsertAsync(Vector("vec-unrelated", "unrelated", "context", [0.1f, 0.9f], now));

        var details = await service.BuildRetrievalDebugAsync(
            "上下文记忆",
            queryVector: [1f, 0f],
            topK: 2,
            tokenBudget: 1000);

        Assert.IsTrue(details.Result.Succeeded);
        Assert.IsTrue(details.Result.Trace.Candidates.Any(item => item.SourceId == "raw-memory"));
        Assert.IsTrue(details.Result.SelectedItems.Any(item => item.SourceId == "raw-memory"));
        Assert.IsTrue(details.Result.SelectedItems.Any(item => item.SourceId == "related-rule"));
        Assert.IsTrue(details.Result.DroppedItems.Any(item => item.SourceId == "unrelated"));
        Assert.IsTrue(details.Result.Trace.Stages.Any(stage => stage.Name == "关键词召回"));
        Assert.IsTrue(details.Result.Trace.Stages.Any(stage => stage.Name == "向量召回"));
        Assert.IsTrue(details.Result.Trace.Stages.Any(stage => stage.Name == "关系扩展"));
        Assert.IsTrue(details.Result.Trace.AttentionScores.Any(item => item.SourceId == "raw-memory"));
        Assert.AreEqual(RetrievalAttentionRerankOptions.OffMode, details.Result.Trace.AttentionRerankComparison.AttentionRerankMode);
        Assert.AreEqual("old-score-anchored-v1-strong", details.Result.Trace.AttentionRerankComparison.AttentionProfile);
        Assert.IsFalse(details.Result.Trace.AttentionRerankComparison.AttentionApplied);
        Assert.IsTrue(details.Result.Trace.AttentionRerankComparison.SelectedSetPreserved);
        Assert.AreEqual(details.Result.SelectedItems.Count, details.Package.Sections.Count);
        Assert.AreEqual(details.Result.OperationId, details.RecentTraces.Single().RetrievalId);

        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            await RetrievalCommand.ExecuteAsync(
                service,
                ["debug", "--query", "上下文记忆", "--vector", "1,0", "--top-k", "2", "--budget", "1000"]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var rendered = output.ToString();
        StringAssert.Contains(rendered, "Attention Shadow Trace");
        StringAssert.Contains(rendered, "Attention Shadow Diff");
        StringAssert.Contains(rendered, "Attention Shadow Summary");
        StringAssert.Contains(rendered, "Attention Rerank Status");
        StringAssert.Contains(rendered, "Planning Execution Status");
        StringAssert.Contains(rendered, "Legacy");
        StringAssert.Contains(rendered, "old-score-anchored-v1-strong");
        StringAssert.Contains(rendered, "Attention Profile Comparison");
        StringAssert.Contains(rendered, "Attention Profile Rank Details");
        StringAssert.Contains(rendered, "conservative-v1");
        StringAssert.Contains(rendered, "learning=");
        StringAssert.Contains(rendered, "noise=");

        using var packageOutput = new StringWriter();
        try
        {
            Console.SetOut(packageOutput);
            await PackagePreviewCommand.ExecuteAsync(
                service,
                ["--budget", "1000"],
                CancellationToken.None);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var packageRendered = packageOutput.ToString();
        StringAssert.Contains(packageRendered, "Attention Rerank Status");
        StringAssert.Contains(packageRendered, "Planning Execution Status");
        StringAssert.Contains(packageRendered, "old-score-anchored-v1-strong");
    }

    [TestMethod]
    public void ControlRoomDashboard_ShouldExposeRetrievalDebugEntry()
    {
        var actionByLetter = ControlRoomInteraction.InterpretDashboardInput("d");
        var actionByNumber = ControlRoomInteraction.InterpretDashboardInput("8");
        var dashboard = new DashboardSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            StorageKind = "memory",
            RootPath = "root",
            Health =
            [
                new SystemHealthItem { Name = "storage", Status = "ok" }
            ],
            Memory = new MemoryLayerSummary(),
            Jobs = new JobsSummary()
        };
        var rendered = DashboardRenderer.RenderToString(dashboard, autoRefresh: false, refreshSeconds: 2, width: 120);

        Assert.AreEqual(ControlRoomActionKind.OpenRetrievalDebug, actionByLetter.Kind);
        Assert.AreEqual(ControlRoomActionKind.OpenRetrievalDebug, actionByNumber.Kind);
        StringAssert.Contains(rendered, "检索");
    }

    private static ControlRoomState CreateState(
        IContextStore contextStore,
        InMemoryMemoryStore memoryStore,
        IRelationStore relationStore,
        IVectorStore vectorStore,
        IRetrievalTraceStore traceStore,
        IEmbeddingProvider embeddingProvider,
        IContextRetriever retriever)
    {
        var jobQueue = new InMemoryJobQueue();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();

        return new ControlRoomState
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            StorageKind = "memory",
            RootPath = "memory",
            ContextStore = contextStore,
            Index = new InMemoryContextIndex(),
            MemoryStore = memoryStore,
            WorkingMemory = memoryStore,
            ConstraintStore = constraintStore,
            RelationStore = relationStore,
            GlobalContextStore = globalStore,
            JobQueue = jobQueue,
            JobQueryStore = jobQueue,
            PromotionService = new BasicMemoryPromotionService(memoryStore, memoryStore),
            PackageBuilder = new BasicContextPackageBuilder(
                contextStore,
                constraintStore,
                globalStore,
                memoryStore,
                relationStore),
            PackagePolicyStore = new InMemoryContextPackagePolicyStore(),
            VectorStore = vectorStore,
            EmbeddingProvider = embeddingProvider,
            RetrievalTraceStore = traceStore,
            Retriever = retriever,
            ModelGatewayOptions = new ModelGatewayOptions(),
            ModelHealthService = default!,
            ModelUsageLogStore = new InMemoryModelUsageLogStore()
        };
    }

    private static ContextItem Item(
        string id,
        string type,
        string content,
        IReadOnlyList<string> tags,
        DateTimeOffset now)
    {
        return new ContextItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = type,
            Content = content,
            Tags = tags,
            Importance = 0.8,
            CreatedAt = now,
            UpdatedAt = now
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
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
