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
        string[]? allowedTypes = null,
        double decayFactor = 1.0,
        bool enableScorePropagation = true) => new()
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
        RequireEvidence = false,
        DecayFactor = decayFactor,
        EnableScorePropagation = enableScorePropagation
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
        // 100K 边远超 MaxScan=500（maxFanout=50 × 10），存储层会截断并传播 Truncated=true。
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

    // ── relation weight/confidence/路径衰减传播到多跳评分 ─────────────

    /// <summary>
    /// 默认参数（DecayFactor=1.0, weight=1.0, confidence=1.0）下，
    /// childScore 应等于 parentScore，保持向后兼容。
    /// </summary>
    [TestMethod]
    public async Task P1_8_DefaultProfile_ChildScoreEqualsParentScore()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        // seed (score=1.0) -> A (depth=1) -> B (depth=2)
        await store.SaveAsync(CreateRelation("seed", "A", weight: 1.0, confidence: 1.0));
        await store.SaveAsync(CreateRelation("A", "B", weight: 1.0, confidence: 1.0));

        var profile = CreateProfile(maxDepth: 2, maxFanout: 10);
        var request = new RelationTraversalRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Seeds = [new RelationTraversalSeed("seed", Score: 1.0)],
            Profile = profile,
            Direction = RelationDirection.Outgoing
        };

        var result = await engine.TraverseAsync(request);

        Assert.AreEqual(2, result.Edges.Count, "应遍历到 2 条边");
        var edge1 = result.Edges.Single(e => e.Depth == 1);
        var edge2 = result.Edges.Single(e => e.Depth == 2);
        Assert.AreEqual(1.0, edge1.SourceScore, 0.001, "depth=1 的 SourceScore 应为 seed score");
        Assert.AreEqual(1.0, edge1.TargetScore, 0.001, "默认参数下 childScore 应等于 parentScore");
        Assert.AreEqual(1.0, edge2.SourceScore, 0.001, "depth=2 的 SourceScore 应为 depth=1 的 childScore");
        Assert.AreEqual(1.0, edge2.TargetScore, 0.001, "默认参数下 childScore 应等于 parentScore");
    }

    /// <summary>
    /// DecayFactor=0.5 时，每跳 childScore 衰减为 parentScore * 0.5。
    /// </summary>
    [TestMethod]
    public async Task P1_8_DecayFactor_PropagatesExponentiallyAcrossHops()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        await store.SaveAsync(CreateRelation("seed", "A", weight: 1.0, confidence: 1.0));
        await store.SaveAsync(CreateRelation("A", "B", weight: 1.0, confidence: 1.0));
        await store.SaveAsync(CreateRelation("B", "C", weight: 1.0, confidence: 1.0));

        var profile = CreateProfile(maxDepth: 3, maxFanout: 10, decayFactor: 0.5);
        var request = new RelationTraversalRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Seeds = [new RelationTraversalSeed("seed", Score: 1.0)],
            Profile = profile,
            Direction = RelationDirection.Outgoing
        };

        var result = await engine.TraverseAsync(request);

        Assert.AreEqual(3, result.Edges.Count, "应遍历到 3 条边");
        var edge1 = result.Edges.Single(e => e.Depth == 1);
        var edge2 = result.Edges.Single(e => e.Depth == 2);
        var edge3 = result.Edges.Single(e => e.Depth == 3);
        Assert.AreEqual(1.0, edge1.SourceScore, 0.001);
        Assert.AreEqual(0.5, edge1.TargetScore, 0.001, "depth=1 childScore = 1.0 * 0.5");
        Assert.AreEqual(0.5, edge2.SourceScore, 0.001);
        Assert.AreEqual(0.25, edge2.TargetScore, 0.001, "depth=2 childScore = 0.5 * 0.5 = 0.25");
        Assert.AreEqual(0.25, edge3.SourceScore, 0.001);
        Assert.AreEqual(0.125, edge3.TargetScore, 0.001, "depth=3 childScore = 0.25 * 0.5 = 0.125");
    }

    /// <summary>
    /// 低 weight 边应降低 childScore（weight=0.5 → childScore 减半）。
    /// </summary>
    [TestMethod]
    public async Task P1_8_LowWeight_ReducesChildScore()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        // 两条平行边：high-weight=1.0 vs low-weight=0.5
        await store.SaveAsync(CreateRelation("seed", "high", weight: 1.0, confidence: 1.0));
        await store.SaveAsync(CreateRelation("seed", "low", weight: 0.5, confidence: 1.0));

        var profile = CreateProfile(maxDepth: 1, maxFanout: 10);
        var request = new RelationTraversalRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Seeds = [new RelationTraversalSeed("seed", Score: 1.0)],
            Profile = profile,
            Direction = RelationDirection.Outgoing
        };

        var result = await engine.TraverseAsync(request);

        Assert.AreEqual(2, result.Edges.Count, "应遍历到 2 条边");
        var highEdge = result.Edges.Single(e => e.NeighborId == "high");
        var lowEdge = result.Edges.Single(e => e.NeighborId == "low");
        Assert.AreEqual(1.0, highEdge.TargetScore, 0.001, "weight=1.0 → childScore = 1.0");
        Assert.AreEqual(0.5, lowEdge.TargetScore, 0.001, "weight=0.5 → childScore = 0.5");
    }

    /// <summary>
    /// 低 confidence 边应降低 childScore（confidence=0.4 → childScore = parentScore * 0.4）。
    /// </summary>
    [TestMethod]
    public async Task P1_8_LowConfidence_ReducesChildScore()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        await store.SaveAsync(CreateRelation("seed", "high-conf", weight: 1.0, confidence: 1.0));
        await store.SaveAsync(CreateRelation("seed", "low-conf", weight: 1.0, confidence: 0.4));

        var profile = CreateProfile(maxDepth: 1, maxFanout: 10);
        var request = new RelationTraversalRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Seeds = [new RelationTraversalSeed("seed", Score: 1.0)],
            Profile = profile,
            Direction = RelationDirection.Outgoing
        };

        var result = await engine.TraverseAsync(request);

        Assert.AreEqual(2, result.Edges.Count, "应遍历到 2 条边");
        var highEdge = result.Edges.Single(e => e.NeighborId == "high-conf");
        var lowEdge = result.Edges.Single(e => e.NeighborId == "low-conf");
        Assert.AreEqual(1.0, highEdge.TargetScore, 0.001, "confidence=1.0 → childScore = 1.0");
        Assert.AreEqual(0.4, lowEdge.TargetScore, 0.001, "confidence=0.4 → childScore = 0.4");
    }

    /// <summary>
    /// weight > 1.0 被 cap 到 1.0，防止分数无界增长。
    /// </summary>
    [TestMethod]
    public async Task P1_8_HighWeight_CappedToOneToPreventScoreGrowth()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        await store.SaveAsync(CreateRelation("seed", "high-weight", weight: 10.0, confidence: 1.0));

        var profile = CreateProfile(maxDepth: 1, maxFanout: 10);
        var request = new RelationTraversalRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Seeds = [new RelationTraversalSeed("seed", Score: 1.0)],
            Profile = profile,
            Direction = RelationDirection.Outgoing
        };

        var result = await engine.TraverseAsync(request);

        Assert.AreEqual(1, result.Edges.Count);
        var edge = result.Edges[0];
        Assert.AreEqual(1.0, edge.TargetScore, 0.001,
            "weight=10.0 应被 cap 到 1.0，childScore = 1.0 * 1.0 * 1.0 * 1.0 = 1.0");
    }

    /// <summary>
    /// 高 score 路径应在 frontier 排序中优先于低 score 路径，
    /// 即 BFS 会优先扩展通过高质量边到达的节点。
    /// </summary>
    [TestMethod]
    public async Task P1_8_FrontierOrdering_PrefersHighQualityPaths()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        // seed 有两条出边：
        // - 到 hub-A (weight=1.0, confidence=1.0) → childScore=1.0
        // - 到 hub-B (weight=0.1, confidence=0.1) → childScore=0.01
        // hub-A 又有一条出边到 deep-A
        // 当 maxFanout=1 时，depth=2 只能扩展 1 个节点，应优先扩展 hub-A（score 更高）
        await store.SaveAsync(CreateRelation("seed", "hub-A", weight: 1.0, confidence: 1.0));
        await store.SaveAsync(CreateRelation("seed", "hub-B", weight: 0.1, confidence: 0.1));
        await store.SaveAsync(CreateRelation("hub-A", "deep-A", weight: 1.0, confidence: 1.0));

        var profile = CreateProfile(maxDepth: 2, maxFanout: 1);
        var request = new RelationTraversalRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Seeds = [new RelationTraversalSeed("seed", Score: 1.0)],
            Profile = profile,
            Direction = RelationDirection.Outgoing
        };

        var result = await engine.TraverseAsync(request);

        // depth=1 应有 1 条边（maxFanout=1，选 weight 更高的 hub-A）
        var depth1Edges = result.Edges.Where(e => e.Depth == 1).ToArray();
        Assert.AreEqual(1, depth1Edges.Length, "depth=1 应只保留 1 条边（maxFanout=1）");
        Assert.AreEqual("hub-A", depth1Edges[0].NeighborId,
            "depth=1 应优先选择 hub-A（weight=1.0 > hub-B 的 0.1）");

        // depth=2 应扩展 hub-A → deep-A（因 hub-A 的 childScore=1.0 > hub-B 的 0.01）
        var depth2Edges = result.Edges.Where(e => e.Depth == 2).ToArray();
        Assert.AreEqual(1, depth2Edges.Length, "depth=2 应只保留 1 条边");
        Assert.AreEqual("deep-A", depth2Edges[0].NeighborId,
            "depth=2 应优先扩展通过高质量路径到达的 hub-A → deep-A");
    }

    /// <summary>
    /// EnableScorePropagation=false 时仅应用 DecayFactor，不传播 weight/confidence。
    /// 保持与旧版完全等价的语义。
    /// </summary>
    [TestMethod]
    public async Task P1_8_DisableScorePropagation_OnlyAppliesDecayFactor()
    {
        var store = CreateStore();
        var engine = new RelationTraversalEngine(store);

        await store.SaveAsync(CreateRelation("seed", "low-weight", weight: 0.1, confidence: 0.1));

        var profile = CreateProfile(maxDepth: 1, maxFanout: 10, decayFactor: 0.5, enableScorePropagation: false);
        var request = new RelationTraversalRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Seeds = [new RelationTraversalSeed("seed", Score: 1.0)],
            Profile = profile,
            Direction = RelationDirection.Outgoing
        };

        var result = await engine.TraverseAsync(request);

        Assert.AreEqual(1, result.Edges.Count);
        var edge = result.Edges[0];
        Assert.AreEqual(0.5, edge.TargetScore, 0.001,
            "EnableScorePropagation=false 时 childScore = parentScore * DecayFactor = 1.0 * 0.5 = 0.5，" +
            "weight=0.1 和 confidence=0.1 不参与计算");
    }
}
