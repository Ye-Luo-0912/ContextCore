using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Services;
using ContextCore.Core;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>GRAPH-13: 图 UI 子图集成测试 — 节点丰富、过滤、紧凑渲染。</summary>
[TestClass]
[TestCategory("Relation")]
[TestCategory("GraphUI")]
public sealed class ContextCoreGraphUISubgraphTests
{
    private static readonly string WorkspaceId = "workspace-graph-ui";
    private static readonly string CollectionId = "collection-graph-ui";

    [TestMethod]
    public async Task GetRelationSubgraph_ShouldEnrichNodesWithTitleAndLifecycle()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(MemoryItem("item-a", "First memory item content line", ContextMemoryStatus.Active));
        await fixture.MemoryStore.SaveAsync(MemoryItem("item-b", "Second memory\nmultiline content", ContextMemoryStatus.Active));
        await fixture.RelationStore.SaveAsync(Relation("rel-a-b", "item-a", "item-b", "references", withEvidence: true));

        var subgraph = await fixture.Service.GetRelationSubgraphAsync("item-a", depth: 2, direction: "both", allowedTypes: null);

        Assert.IsTrue(subgraph.Nodes.Count >= 2);
        var nodeA = subgraph.Nodes.Single(n => n.ItemId == "item-a");
        var nodeB = subgraph.Nodes.Single(n => n.ItemId == "item-b");
        Assert.AreEqual("First memory item content line", nodeA.Title);
        Assert.AreEqual("Active", nodeA.Lifecycle);
        Assert.AreEqual("Second memory", nodeB.Title);
    }

    [TestMethod]
    public async Task GetRelationSubgraph_ShouldEnrichNodesWithContextItemTitle()
    {
        var fixture = CreateFixture();
        await fixture.ContextStore.SaveAsync(new ContextItem
        {
            Id = "ctx-a",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Type = "note",
            Title = "Context Title A",
            Content = "context content",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await fixture.ContextStore.SaveAsync(new ContextItem
        {
            Id = "ctx-b",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Type = "rule",
            Title = "Context Title B",
            Content = "rule content",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await fixture.RelationStore.SaveAsync(Relation("rel-ctx", "ctx-a", "ctx-b", "references", withEvidence: true));

        var subgraph = await fixture.Service.GetRelationSubgraphAsync("ctx-a", depth: 2, direction: "both", allowedTypes: null);

        var nodeA = subgraph.Nodes.Single(n => n.ItemId == "ctx-a");
        Assert.AreEqual("Context Title A", nodeA.Title);
    }

    [TestMethod]
    public async Task GetRelationSubgraph_ShouldTraverseMultiDepthEdges()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(MemoryItem("n1", "node1", ContextMemoryStatus.Active));
        await fixture.MemoryStore.SaveAsync(MemoryItem("n2", "node2", ContextMemoryStatus.Active));
        await fixture.MemoryStore.SaveAsync(MemoryItem("n3", "node3", ContextMemoryStatus.Active));
        await fixture.RelationStore.BatchUpsertAsync([
            Relation("e1", "n1", "n2", "references", withEvidence: true),
            Relation("e2", "n2", "n3", "references", withEvidence: true)
        ]);

        var subgraph = await fixture.Service.GetRelationSubgraphAsync("n1", depth: 3, direction: "outgoing", allowedTypes: null);

        Assert.IsTrue(subgraph.Edges.Count >= 2);
        Assert.IsTrue(subgraph.MaxDepthReached >= 2);
        Assert.IsTrue(subgraph.Nodes.Any(n => n.ItemId == "n3" && n.Depth >= 2));
    }

    [TestMethod]
    public async Task GetRelationSubgraph_ShouldFilterByRelationType()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(MemoryItem("n1", "node1", ContextMemoryStatus.Active));
        await fixture.MemoryStore.SaveAsync(MemoryItem("n2", "node2", ContextMemoryStatus.Active));
        await fixture.MemoryStore.SaveAsync(MemoryItem("n3", "node3", ContextMemoryStatus.Active));
        await fixture.RelationStore.BatchUpsertAsync([
            Relation("e-ref", "n1", "n2", "references", withEvidence: true),
            Relation("e-cont", "n1", "n3", "contains", withEvidence: true)
        ]);

        var subgraph = await fixture.Service.GetRelationSubgraphAsync("n1", depth: 2, direction: "outgoing", allowedTypes: ["references"]);

        Assert.AreEqual(1, subgraph.Edges.Count);
        Assert.AreEqual("references", subgraph.Edges[0].RelationType);
    }

    [TestMethod]
    public async Task GetRelationSubgraph_Chain_ShouldFollowSupersedeChain()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(MemoryItem("old", "old version", ContextMemoryStatus.Deprecated));
        await fixture.MemoryStore.SaveAsync(MemoryItem("mid", "mid version", ContextMemoryStatus.Deprecated));
        await fixture.MemoryStore.SaveAsync(MemoryItem("new", "new version", ContextMemoryStatus.Active));
        await fixture.RelationStore.BatchUpsertAsync([
            Relation("e1", "old", "mid", ContextRelationTypes.SupersededBy, withEvidence: true),
            Relation("e2", "mid", "new", ContextRelationTypes.SupersededBy, withEvidence: true),
            Relation("e3", "new", "old", ContextRelationTypes.Replaces, withEvidence: true),
            Relation("e4", "mid", "old", ContextRelationTypes.Replaces, withEvidence: true)
        ]);

        var subgraph = await fixture.Service.GetRelationSubgraphAsync(
            "old", depth: 5, direction: "both",
            allowedTypes: [ContextRelationTypes.SupersededBy, ContextRelationTypes.Replaces, ContextRelationTypes.ReplacedBy, ContextRelationTypes.Supersedes]);

        Assert.IsTrue(subgraph.Edges.Count >= 2);
        Assert.IsTrue(subgraph.Edges.All(e =>
            e.RelationType == ContextRelationTypes.SupersededBy
            || e.RelationType == ContextRelationTypes.Replaces
            || e.RelationType == ContextRelationTypes.ReplacedBy
            || e.RelationType == ContextRelationTypes.Supersedes));
    }

    [TestMethod]
    public async Task GetRelationSubgraph_Conflicts_ShouldFollowConflictRelations()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(MemoryItem("c1", "constraint1", ContextMemoryStatus.Active));
        await fixture.MemoryStore.SaveAsync(MemoryItem("c2", "constraint2", ContextMemoryStatus.Active));
        await fixture.MemoryStore.SaveAsync(MemoryItem("c3", "constraint3", ContextMemoryStatus.Active));
        await fixture.RelationStore.BatchUpsertAsync([
            Relation("e-conf", "c1", "c2", ContextRelationTypes.ConflictsWith, withEvidence: true),
            Relation("e-contr", "c1", "c3", ContextRelationTypes.Contradicts, withEvidence: true),
            Relation("e-ref", "c2", "c3", "references", withEvidence: true)
        ]);

        var subgraph = await fixture.Service.GetRelationSubgraphAsync(
            "c1", depth: 3, direction: "both",
            allowedTypes: [ContextRelationTypes.ConflictsWith, ContextRelationTypes.Contradicts]);

        Assert.IsTrue(subgraph.Edges.Count >= 2);
        Assert.IsTrue(subgraph.Edges.All(e =>
            e.RelationType == ContextRelationTypes.ConflictsWith
            || e.RelationType == ContextRelationTypes.Contradicts));
    }

    [TestMethod]
    public async Task GetRelationSubgraph_ShouldReturnEmptyEdgesForIsolatedItem()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(MemoryItem("solo", "isolated item", ContextMemoryStatus.Active));

        var subgraph = await fixture.Service.GetRelationSubgraphAsync("solo", depth: 2, direction: "both", allowedTypes: null);

        Assert.AreEqual(0, subgraph.Edges.Count);
        Assert.AreEqual(1, subgraph.Nodes.Count);
        Assert.AreEqual("solo", subgraph.Nodes[0].ItemId);
    }

    [TestMethod]
    public async Task GetRelationSubgraph_ShouldEnrichEdgesWithLifecycleAndReviewStatus()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(MemoryItem("n1", "node1", ContextMemoryStatus.Active));
        await fixture.MemoryStore.SaveAsync(MemoryItem("n2", "node2", ContextMemoryStatus.Active));
        await fixture.RelationStore.SaveAsync(new ContextRelation
        {
            Id = "rel-lifecycle",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SourceId = "n1",
            TargetId = "n2",
            RelationType = "references",
            Weight = 1.0,
            Confidence = 0.9,
            SourceRefs = ["evidence-1"],
            Lifecycle = RelationLifecycles.Deprecated,
            ReviewStatus = RelationReviewStatuses.Reviewed,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["reviewStatus"] = RelationReviewStatuses.Reviewed,
                ["lifecycle"] = RelationLifecycles.Deprecated
            },
            CreatedAt = DateTimeOffset.UtcNow
        });

        var subgraph = await fixture.Service.GetRelationSubgraphAsync("n1", depth: 2, direction: "both", allowedTypes: null);

        var edge = subgraph.Edges.Single(e => e.RelationId == "rel-lifecycle");
        Assert.AreEqual(RelationLifecycles.Deprecated, edge.Lifecycle);
        Assert.AreEqual(RelationReviewStatuses.Reviewed, edge.ReviewStatus);
    }

    private static GraphUIFixture CreateFixture()
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var jobQueue = new InMemoryJobQueue();

        var state = new ControlRoomState
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
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
                contextStore, constraintStore, globalStore, memoryStore, relationStore),
            PackagePolicyStore = new InMemoryContextPackagePolicyStore(),
            VectorStore = new InMemoryVectorStore(),
            EmbeddingProvider = null!,
            RetrievalTraceStore = new InMemoryRetrievalTraceStore(),
            Retriever = null!,
            ModelGatewayOptions = new ModelGatewayOptions(),
            ModelHealthService = default!,
            ModelUsageLogStore = new InMemoryModelUsageLogStore()
        };

        return new GraphUIFixture(
            contextStore, memoryStore, relationStore, new ControlRoomService(state));
    }

    private static ContextMemoryItem MemoryItem(string id, string content, ContextMemoryStatus status)
    {
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Layer = ContextMemoryLayer.Stable,
            Status = status,
            Type = "test-memory",
            Content = content,
            ContentFormat = ContextContentFormat.PlainText,
            Tags = [],
            SourceRefs = [],
            RelationRefs = [],
            Importance = 0.5,
            Confidence = 0.8,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextRelation Relation(string id, string sourceId, string targetId, string relationType, bool withEvidence = false)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 1.0,
            Confidence = 0.9,
            SourceRefs = withEvidence ? [$"evidence-{id}"] : [],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["reviewStatus"] = RelationReviewStatuses.Reviewed,
                ["lifecycle"] = RelationLifecycles.Active
            },
            Lifecycle = RelationLifecycles.Active,
            ReviewStatus = RelationReviewStatuses.Reviewed,
            SourceNodeKind = nameof(GraphNodeKind.StableMemory),
            TargetNodeKind = nameof(GraphNodeKind.StableMemory),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed record GraphUIFixture(
        InMemoryContextStore ContextStore,
        InMemoryMemoryStore MemoryStore,
        InMemoryRelationStore RelationStore,
        ControlRoomService Service);
}
