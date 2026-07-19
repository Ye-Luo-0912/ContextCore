using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Graph;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreGraphTraversalSafetyTests
{
    private static InMemoryRelationStore CreateStore()
    {
        return new InMemoryRelationStore();
    }

    private static ContextRelation CreateRelation(
        string sourceId, string targetId,
        string relationType = ContextRelationTypes.RelatedTo,
        double weight = 1.0, double confidence = 1.0,
        string lifecycle = RelationLifecycles.Active,
        string? reviewStatus = null)
    {
        return new ContextRelation
        {
            Id = $"rel-{sourceId}-{targetId}-{relationType}",
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = weight,
            Confidence = confidence,
            Lifecycle = lifecycle,
            ReviewStatus = reviewStatus ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            SourceNodeKind = nameof(GraphNodeKind.ContextItem),
            TargetNodeKind = nameof(GraphNodeKind.ContextItem),
            Provenance = "test"
        };
    }

    private static RelationExpansionProfile CreateProfile(
        int maxDepth = 2, int maxFanout = 10,
        string[]? allowedTypes = null) => new()
    {
        ProfileId = "test",
        Mode = "Normal",
        MaxDepth = maxDepth,
        MaxFanout = maxFanout,
        AllowedRelationTypes = allowedTypes ?? [ContextRelationTypes.RelatedTo],
        MinConfidence = 0.0,
        AllowDeprecatedRelations = true,
        AllowCandidateRelations = true,
        AllowRejectedRelations = true,
        RequireEvidence = false
    };

    private static RelationTraversalRequest CreateRequest(
        string seedItemId,
        RelationExpansionProfile profile,
        RelationDirection direction = RelationDirection.Outgoing) => new()
    {
        WorkspaceId = "ws-test",
        CollectionId = "col-test",
        Seeds = [new RelationTraversalSeed(seedItemId)],
        Profile = profile,
        Direction = direction
    };

    /// <summary>1. 高出度节点测试：fanout 限制应在单层 BFS 中生效。</summary>
    [TestMethod]
    public async Task HighOutDegreeHub_FanoutLimitCapsEdges()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        for (var i = 0; i < 500; i++)
        {
            await store.SaveAsync(CreateRelation("hub", $"node-{i}"));
        }

        var profile = CreateProfile(maxDepth: 1, maxFanout: 10);
        var result = await engine.TraverseAsync(CreateRequest("hub", profile));

        Assert.IsTrue(result.Edges.Count <= 10,
            $"Fanout limit should cap edges at 10, got {result.Edges.Count}");
        Assert.IsFalse(result.Truncated,
            "Single-layer fanout-capped traversal should not set Truncated");
        Assert.IsTrue(result.Edges.All(e => e.Depth == 1),
            "All edges from a depth-1 traversal should have depth 1");
    }

    /// <summary>2. 环测试：visitedNodes 应防止 A→B→C→A 环导致死循环。</summary>
    [TestMethod]
    public async Task Cycle_TerminatesWithoutInfiniteLoop()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        await store.SaveAsync(CreateRelation("A", "B"));
        await store.SaveAsync(CreateRelation("B", "C"));
        await store.SaveAsync(CreateRelation("C", "A"));

        var profile = CreateProfile(maxDepth: 5, maxFanout: 10);
        var result = await engine.TraverseAsync(CreateRequest("A", profile));

        Assert.IsTrue(result.Edges.Count <= 3,
            $"Cycle should produce at most 3 unique edges, got {result.Edges.Count}");
        Assert.IsFalse(result.Truncated,
            "Cycle should terminate via visited set, not via truncation");
    }

    /// <summary>3. 多分支测试：BFS 应覆盖所有分支到指定深度。</summary>
    [TestMethod]
    public async Task MultiBranch_AllEdgesCollectedUpToMaxDepth()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        await store.SaveAsync(CreateRelation("A", "B"));
        await store.SaveAsync(CreateRelation("A", "C"));
        await store.SaveAsync(CreateRelation("B", "D"));
        await store.SaveAsync(CreateRelation("C", "E"));

        var profile = CreateProfile(maxDepth: 2, maxFanout: 10);
        var result = await engine.TraverseAsync(CreateRequest("A", profile));

        var pairs = result.Edges
            .Select(e => (e.Relation.SourceId, e.Relation.TargetId))
            .ToHashSet();

        Assert.AreEqual(4, result.Edges.Count,
            $"Expected 4 edges across both branches, got {result.Edges.Count}");
        Assert.IsTrue(pairs.Contains(("A", "B")), "A->B should be in results");
        Assert.IsTrue(pairs.Contains(("A", "C")), "A->C should be in results");
        Assert.IsTrue(pairs.Contains(("B", "D")), "B->D should be in results");
        Assert.IsTrue(pairs.Contains(("C", "E")), "C->E should be in results");
        Assert.AreEqual(2, result.MaxDepthReached, "MaxDepthReached should be 2");
    }

    /// <summary>4. 悬空边测试：target 不存在时不应抛异常，悬空端不应继续扩展。</summary>
    [TestMethod]
    public async Task DanglingEdge_DoesNotThrowAndStopsExpansion()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        await store.SaveAsync(CreateRelation("A", "B"));

        var profile = CreateProfile(maxDepth: 2, maxFanout: 10);
        var result = await engine.TraverseAsync(CreateRequest("A", profile));

        Assert.AreEqual(1, result.Edges.Count, "Only A->B should be in results");
        Assert.AreEqual("A", result.Edges[0].Relation.SourceId);
        Assert.AreEqual("B", result.Edges[0].Relation.TargetId);

        var bOutgoing = await store.QueryAsync(new ContextRelationQuery { WorkspaceId = "ws-test", CollectionId = "col-test", SourceId = "B", Take = int.MaxValue });
        Assert.AreEqual(0, bOutgoing.Count,
            "B has no outgoing relations because B does not exist in the store");
    }

    /// <summary>5. 并发写测试：10 个 BatchUpsertAsync 各写 100 条，共 1000 条，无丢失无重复。</summary>
    [TestMethod]
    public async Task ConcurrentWrites_NoDataLossOrDuplication()
    {
        var store = CreateStore();

        var tasks = Enumerable.Range(0, 10).Select(batchIndex =>
        {
            var relations = Enumerable.Range(0, 100)
                .Select(itemIndex => CreateRelation(
                    $"src-{batchIndex}-{itemIndex}",
                    $"tgt-{batchIndex}-{itemIndex}"))
                .ToArray();
            return store.BatchUpsertAsync(relations);
        }).ToArray();

        await Task.WhenAll(tasks);

        var all = await store.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Take = int.MaxValue
        });

        Assert.AreEqual(1000, all.Count,
            "All 1000 relations should be queryable after concurrent writes");
        var uniqueIds = all.Select(r => r.Id).ToHashSet();
        Assert.AreEqual(1000, uniqueIds.Count,
            "No duplicate relation IDs should exist");
    }

    /// <summary>6. 10 万边基准测试：hub 节点 100k 出边，fanout=50，应在 5 秒内完成。</summary>
    [TestMethod]
    [TestCategory("Benchmark")]
    public async Task HundredThousandEdges_CompletesWithinBudget()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        var relations = new ContextRelation[100_000];
        for (var i = 0; i < 100_000; i++)
        {
            relations[i] = CreateRelation("hub", $"node-{i}");
        }
        await store.BatchUpsertAsync(relations);

        var profile = CreateProfile(maxDepth: 1, maxFanout: 50);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.TraverseAsync(CreateRequest("hub", profile));
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.Elapsed.TotalSeconds < 5,
            $"Traversal of 100k edges took {stopwatch.Elapsed.TotalSeconds:F2}s, expected < 5s");
        Assert.AreEqual(50, result.Edges.Count,
            $"Fanout=50 should return exactly 50 edges, got {result.Edges.Count}");
        // P1-4: 100K 边远超 MaxScan=500（maxFanout=50 × 10），存储层会截断并传播 Truncated=true。
        Assert.IsTrue(result.Truncated,
            "100K edges with MaxScan=500 should propagate storage-side truncation");
        Assert.IsTrue(result.Warnings.Count > 0,
            "Truncated=true should be accompanied by a warning explaining the storage-side cause");
    }

    /// <summary>7. 双向遍历安全测试：Direction=Both 不应重复同一条边，且应包含出边和入边。</summary>
    [TestMethod]
    public async Task BidirectionalTraversal_NoDuplicateAndBothDirections()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        await store.SaveAsync(CreateRelation("A", "B"));

        var profile = CreateProfile(maxDepth: 1, maxFanout: 10);

        var result1 = await engine.TraverseAsync(
            CreateRequest("A", profile, RelationDirection.Both));
        Assert.AreEqual(1, result1.Edges.Count,
            "A->B should appear exactly once with Direction=Both (no duplicate)");

        await store.SaveAsync(CreateRelation("C", "A"));

        var result2 = await engine.TraverseAsync(
            CreateRequest("A", profile, RelationDirection.Both));
        Assert.AreEqual(2, result2.Edges.Count,
            "Both A->B (outgoing) and C->A (incoming) should appear");

        var pairs = result2.Edges
            .Select(e => (e.Relation.SourceId, e.Relation.TargetId))
            .ToHashSet();
        Assert.IsTrue(pairs.Contains(("A", "B")), "A->B should be in results");
        Assert.IsTrue(pairs.Contains(("C", "A")), "C->A should be in results");
    }

    /// <summary>8. 深度截断测试：MaxDepth=2 应在 B→C 处停止，不到达 D→E。</summary>
    [TestMethod]
    public async Task DepthTruncation_StopsAtMaxDepth()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        await store.SaveAsync(CreateRelation("A", "B"));
        await store.SaveAsync(CreateRelation("B", "C"));
        await store.SaveAsync(CreateRelation("C", "D"));
        await store.SaveAsync(CreateRelation("D", "E"));

        var profile = CreateProfile(maxDepth: 2, maxFanout: 10);
        var result = await engine.TraverseAsync(CreateRequest("A", profile));

        Assert.AreEqual(2, result.Edges.Count,
            $"Expected 2 edges (A->B, B->C) at MaxDepth=2, got {result.Edges.Count}");
        Assert.AreEqual(2, result.MaxDepthReached,
            "MaxDepthReached should be 2");
    }
}
