using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

/// <summary>
/// GRAPH-10：IRelationStore provider contract 基类。
/// 同一套断言在 InMemory / FileSystem / Postgres 三个 provider 上运行，验证核心契约一致：
/// Get/Delete/BatchUpsert 行为，以及 QueryNeighborsAsync(RelationNeighborQuery) 的方向、类型、
/// 置信度、生命周期、ReviewStatus 过滤和 Take 分页语义。
/// P1-6：新增 QueryNeighborsBatchAsync(RelationNeighborBatchQuery) 批量邻居查询契约。
/// </summary>
/// <remarks>
/// 派生类必须实现 <see cref="CreateStoreAsync"/> 返回一个干净的 store 实例。
/// 每个测试方法使用独立 collectionId，避免 FileSystem 共享 JSONL 文件导致跨测试干扰。
/// </remarks>
[TestClass]
public abstract class RelationStoreContractBase
{
    private const string WorkspaceId = "ws-contract";
    private static readonly DateTimeOffset BaseTime =
        new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

    /// <summary>创建一个干净的 store 实例，调用方负责释放（若需要）。</summary>
    protected abstract Task<IRelationStore> CreateStoreAsync(CancellationToken cancellationToken);

    /// <summary>释放 store 持有的资源（如 Postgres 连接池）；默认空实现。</summary>
    protected virtual Task DisposeStoreAsync(IRelationStore store, CancellationToken cancellationToken) => Task.CompletedTask;

    private static ContextRelation MakeRelation(
        string id,
        string sourceId,
        string targetId,
        string relationType,
        double confidence = 0.9,
        double weight = 1.0,
        string? lifecycle = null,
        string? reviewStatus = null,
        int createdOffsetSeconds = 0)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            CollectionId = "col",
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Confidence = confidence,
            Weight = weight,
            Lifecycle = lifecycle ?? RelationLifecycles.Active,
            ReviewStatus = reviewStatus ?? string.Empty,
            CreatedAt = BaseTime.AddSeconds(createdOffsetSeconds),
            UpdatedAt = BaseTime.AddSeconds(createdOffsetSeconds)
        };
    }

    private async Task<IRelationStore> PrepareAsync(params ContextRelation[] seed)
    {
        var store = await CreateStoreAsync(CancellationToken.None);
        if (seed.Length > 0)
        {
            await store.BatchUpsertAsync(seed, CancellationToken.None);
        }
        return store;
    }

    // ── Get / BatchUpsert / Delete 契约 ──────────────────────────────────

    [TestMethod]
    public async Task BatchUpsert_Get_RoundTripsRelation()
    {
        var store = await PrepareAsync(MakeRelation("r1", "a", "b", "related_to"));
        try
        {
            var fetched = await store.GetAsync(WorkspaceId, "col", "r1", CancellationToken.None);
            Assert.IsNotNull(fetched, "BatchUpsert 后应能通过 GetAsync 取回");
            Assert.AreEqual("a", fetched!.SourceId);
            Assert.AreEqual("b", fetched.TargetId);
            Assert.AreEqual("related_to", fetched.RelationType);
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task BatchUpsert_OverwritesExistingById()
    {
        var store = await PrepareAsync();
        try
        {
            await store.BatchUpsertAsync([MakeRelation("r1", "a", "b", "related_to", confidence: 0.5)], CancellationToken.None);
            await store.BatchUpsertAsync([MakeRelation("r1", "a", "c", "depends_on", confidence: 0.95)], CancellationToken.None);

            var fetched = await store.GetAsync(WorkspaceId, "col", "r1", CancellationToken.None);
            Assert.IsNotNull(fetched);
            Assert.AreEqual("c", fetched!.TargetId, "相同 ID 第二次 upsert 应覆盖");
            Assert.AreEqual(0.95, fetched.Confidence);
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task Delete_RemovesRelationAndReturnsTrue()
    {
        var store = await PrepareAsync(MakeRelation("r1", "a", "b", "related_to"));
        try
        {
            var deleted = await store.DeleteAsync(WorkspaceId, "col", "r1", CancellationToken.None);
            Assert.IsTrue(deleted, "删除已存在的边应返回 true");

            var fetched = await store.GetAsync(WorkspaceId, "col", "r1", CancellationToken.None);
            Assert.IsNull(fetched, "删除后 GetAsync 应返回 null");

            var secondDelete = await store.DeleteAsync(WorkspaceId, "col", "r1", CancellationToken.None);
            Assert.IsFalse(secondDelete, "删除不存在的边应返回 false");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    // ── QueryNeighborsAsync(RelationNeighborQuery) 方向契约 ───────────────

    [TestMethod]
    public async Task QueryNeighbors_BothDirection_ReturnsOutgoingAndIncoming()
    {
        var store = await PrepareAsync(
            MakeRelation("r-out", "center", "out-neighbor", "related_to"),
            MakeRelation("r-in", "in-neighbor", "center", "related_to"));
        try
        {
            var result = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Both,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(2, result.Count, "Both 方向应返回出边和入边");
            Assert.IsTrue(result.Any(r => r.Id == "r-out"), "应包含出边");
            Assert.IsTrue(result.Any(r => r.Id == "r-in"), "应包含入边");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighbors_Outgoing_ReturnsOnlyOutgoingEdges()
    {
        var store = await PrepareAsync(
            MakeRelation("r-out", "center", "out-neighbor", "related_to"),
            MakeRelation("r-in", "in-neighbor", "center", "related_to"));
        try
        {
            var result = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Outgoing,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("r-out", result[0].Id, "Outgoing 应只返回出边");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighbors_Incoming_ReturnsOnlyIncomingEdges()
    {
        var store = await PrepareAsync(
            MakeRelation("r-out", "center", "out-neighbor", "related_to"),
            MakeRelation("r-in", "in-neighbor", "center", "related_to"));
        try
        {
            var result = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Incoming,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("r-in", result[0].Id, "Incoming 应只返回入边");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    // ── 类型 / 置信度 / 生命周期 / ReviewStatus 过滤契约 ──────────────────

    [TestMethod]
    public async Task QueryNeighbors_RelationTypeFilter_PushesToStore()
    {
        var store = await PrepareAsync(
            MakeRelation("r-related", "center", "n1", "related_to"),
            MakeRelation("r-depends", "center", "n2", "depends_on"));
        try
        {
            var result = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Outgoing,
                RelationType = "depends_on",
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("r-depends", result[0].Id, "RelationType 过滤应只返回匹配类型");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighbors_MinConfidence_FiltersLowConfidence()
    {
        var store = await PrepareAsync(
            MakeRelation("r-high", "center", "n1", "related_to", confidence: 0.95),
            MakeRelation("r-low", "center", "n2", "related_to", confidence: 0.3));
        try
        {
            var result = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Outgoing,
                MinConfidence = 0.5,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("r-high", result[0].Id, "MinConfidence 应过滤掉低置信度边");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighbors_ExcludedLifecycles_FiltersDeprecated()
    {
        var store = await PrepareAsync(
            MakeRelation("r-active", "center", "n1", "related_to", lifecycle: RelationLifecycles.Active),
            MakeRelation("r-deprecated", "center", "n2", "related_to", lifecycle: RelationLifecycles.Deprecated),
            MakeRelation("r-superseded", "center", "n3", "related_to", lifecycle: RelationLifecycles.Superseded));
        try
        {
            var result = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Outgoing,
                ExcludedLifecycles = [RelationLifecycles.Deprecated, RelationLifecycles.Superseded],
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("r-active", result[0].Id, "ExcludedLifecycles 应排除 deprecated 和 superseded");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighbors_ExcludedReviewStatuses_FiltersRejected()
    {
        var store = await PrepareAsync(
            MakeRelation("r-reviewed", "center", "n1", "related_to", reviewStatus: RelationReviewStatuses.Reviewed),
            MakeRelation("r-rejected", "center", "n2", "related_to", reviewStatus: RelationReviewStatuses.Rejected),
            MakeRelation("r-needs", "center", "n3", "related_to", reviewStatus: RelationReviewStatuses.NeedsEvidence));
        try
        {
            var result = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Outgoing,
                ExcludedReviewStatuses = [RelationReviewStatuses.Rejected, RelationReviewStatuses.NeedsEvidence],
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("r-reviewed", result[0].Id, "ExcludedReviewStatuses 应排除 rejected 和 needs-evidence");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighbors_Take_LimitsResultCount()
    {
        var seeds = Enumerable.Range(0, 5)
            .Select(i => MakeRelation($"r-{i}", "center", $"n{i}", "related_to", createdOffsetSeconds: i))
            .ToArray();
        var store = await PrepareAsync(seeds);
        try
        {
            var result = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Outgoing,
                Take = 2
            }, CancellationToken.None);

            Assert.AreEqual(2, result.Count, "Take 应限制返回数量");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    /// <summary>
    /// P0-2 回归：验证 weight 排序为数值降序，而非字符串排序。
    /// 旧实现 ORDER BY data ->> 'Weight' 在 PostgreSQL 中是字符串比较，
    /// "10" 会排在 "9" 之前。此测试用 weight=9.0 和 weight=10.0 验证数值排序正确。
    /// </summary>
    [TestMethod]
    public async Task QueryNeighbors_WeightOrdering_UsesNumericSort_NotStringSort()
    {
        // 4 条边权重为 1.0 / 2.0 / 9.0 / 10.0
        // 字符串排序：1, 10, 2, 9 → 错误顺序
        // 数值排序：10, 9, 2, 1 → 正确顺序
        var store = await PrepareAsync(
            MakeRelation("r-w1", "center", "n1", "related_to", weight: 1.0),
            MakeRelation("r-w2", "center", "n2", "related_to", weight: 2.0),
            MakeRelation("r-w9", "center", "n3", "related_to", weight: 9.0),
            MakeRelation("r-w10", "center", "n4", "related_to", weight: 10.0));
        try
        {
            var result = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Outgoing,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(4, result.Count, "应返回全部 4 条边");
            Assert.AreEqual("r-w10", result[0].Id, "weight=10.0 应排首位（数值降序）");
            Assert.AreEqual("r-w9", result[1].Id, "weight=9.0 应排第二");
            Assert.AreEqual("r-w2", result[2].Id, "weight=2.0 应排第三");
            Assert.AreEqual("r-w1", result[3].Id, "weight=1.0 应排末位");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    /// <summary>
    /// P0-2 回归：验证内层 LIMIT（max_scan）发生在 ORDER BY 之后，
    /// 不会在排序前截断未排序集合导致漏掉高权重边。
    /// 旧实现：内层 SELECT ... LIMIT @max_scan 无 ORDER BY → 截断未排序集合 →
    /// 高权重边可能被丢弃，外层 ORDER BY 后取不到它们。
    /// 新实现：内层 SELECT ... ORDER BY weight DESC LIMIT @max_scan → 取 top N 后再分页。
    /// </summary>
    [TestMethod]
    public async Task QueryNeighbors_MaxScan_PreservesHighWeightResults()
    {
        // 10 条边权重 1.0-10.0，max_scan=5 后取 top 3
        // 旧实现：内层 LIMIT 5 随机取 5 条，外层 ORDER BY 后取 top 3 —— 可能漏掉 weight=10
        // 新实现：内层 ORDER BY weight DESC LIMIT 5 取 weight 10,9,8,7,6 → 外层取 top 3 = 10,9,8
        var seeds = Enumerable.Range(1, 10)
            .Select(i => MakeRelation($"r-w{i}", "center", $"n{i}", "related_to", weight: (double)i))
            .ToArray();
        var store = await PrepareAsync(seeds);
        try
        {
            var result = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Outgoing,
                MaxScan = 5,
                Take = 3
            }, CancellationToken.None);

            Assert.AreEqual(3, result.Count, "应返回 top 3");
            Assert.AreEqual("r-w10", result[0].Id, "max_scan 后 weight=10 仍应在结果中（首位）");
            Assert.AreEqual("r-w9", result[1].Id, "weight=9 应在第二位");
            Assert.AreEqual("r-w8", result[2].Id, "weight=8 应在第三位");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    /// <summary>
    /// P0-2 回归：验证 Skip+Take 分页在数值排序基础上正确翻页。
    /// </summary>
    [TestMethod]
    public async Task QueryNeighbors_SkipTake_PaginatesByWeightDesc()
    {
        var seeds = Enumerable.Range(1, 5)
            .Select(i => MakeRelation($"r-w{i}", "center", $"n{i}", "related_to", weight: (double)i))
            .ToArray();
        var store = await PrepareAsync(seeds);
        try
        {
            // 第一页：top 2 = weight 5, 4
            var page1 = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Outgoing,
                Take = 2,
                Skip = 0
            }, CancellationToken.None);

            Assert.AreEqual(2, page1.Count);
            Assert.AreEqual("r-w5", page1[0].Id);
            Assert.AreEqual("r-w4", page1[1].Id);

            // 第二页：next 2 = weight 3, 2
            var page2 = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Outgoing,
                Take = 2,
                Skip = 2
            }, CancellationToken.None);

            Assert.AreEqual(2, page2.Count);
            Assert.AreEqual("r-w3", page2[0].Id);
            Assert.AreEqual("r-w2", page2[1].Id);
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    // ── P1-6：QueryNeighborsBatchAsync 契约 ────────────────────────────

    [TestMethod]
    public async Task QueryNeighborsBatch_ReturnsEachSeedWithItsNeighbors()
    {
        var store = await PrepareAsync(
            MakeRelation("r-a-out", "seedA", "neighbor-a1", "related_to"),
            MakeRelation("r-a-in", "neighbor-a2", "seedA", "related_to"),
            MakeRelation("r-b-out", "seedB", "neighbor-b1", "related_to"));
        try
        {
            var results = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seedA", "seedB"],
                Direction = RelationDirection.Both,
                Take = 100
            }, CancellationToken.None);

            var bySeed = results.ToDictionary(r => r.ItemId, StringComparer.OrdinalIgnoreCase);
            Assert.IsTrue(bySeed.ContainsKey("seedA"), "应包含 seedA 的结果");
            Assert.IsTrue(bySeed.ContainsKey("seedB"), "应包含 seedB 的结果");
            Assert.AreEqual(2, bySeed["seedA"].Relations.Count, "seedA 应有 2 个邻居（出+入）");
            Assert.AreEqual(1, bySeed["seedB"].Relations.Count, "seedB 应有 1 个邻居");
            Assert.IsTrue(bySeed["seedA"].Relations.Any(r => r.Id == "r-a-out"));
            Assert.IsTrue(bySeed["seedA"].Relations.Any(r => r.Id == "r-a-in"));
            Assert.AreEqual("r-b-out", bySeed["seedB"].Relations[0].Id);
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighborsBatch_BothDirection_SharedEdgeAppearsInBothSeeds()
    {
        // seedA 和 seedB 之间有一条边 A→B。Both 方向下，这条边应同时出现在 seedA 和 seedB 的结果中。
        var store = await PrepareAsync(
            MakeRelation("r-shared", "seedA", "seedB", "related_to"));
        try
        {
            var results = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seedA", "seedB"],
                Direction = RelationDirection.Both,
                Take = 100
            }, CancellationToken.None);

            var bySeed = results.ToDictionary(r => r.ItemId, StringComparer.OrdinalIgnoreCase);
            Assert.AreEqual(1, bySeed["seedA"].Relations.Count, "seedA 视角：出边到 seedB");
            Assert.AreEqual("r-shared", bySeed["seedA"].Relations[0].Id);
            Assert.AreEqual(1, bySeed["seedB"].Relations.Count, "seedB 视角：入边来自 seedA");
            Assert.AreEqual("r-shared", bySeed["seedB"].Relations[0].Id);
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighborsBatch_Direction_FiltersCorrectly()
    {
        var store = await PrepareAsync(
            MakeRelation("r-out", "seedA", "neighbor1", "related_to"),
            MakeRelation("r-in", "neighbor2", "seedA", "related_to"));
        try
        {
            var outgoing = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seedA"],
                Direction = RelationDirection.Outgoing,
                Take = 100
            }, CancellationToken.None);
            Assert.AreEqual(1, outgoing.Count);
            Assert.AreEqual(1, outgoing[0].Relations.Count);
            Assert.AreEqual("r-out", outgoing[0].Relations[0].Id);

            var incoming = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seedA"],
                Direction = RelationDirection.Incoming,
                Take = 100
            }, CancellationToken.None);
            Assert.AreEqual(1, incoming.Count);
            Assert.AreEqual(1, incoming[0].Relations.Count);
            Assert.AreEqual("r-in", incoming[0].Relations[0].Id);
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighborsBatch_RelationType_FiltersPerSeed()
    {
        var store = await PrepareAsync(
            MakeRelation("r-a-related", "seedA", "n1", "related_to"),
            MakeRelation("r-a-depends", "seedA", "n2", "depends_on"),
            MakeRelation("r-b-related", "seedB", "n3", "related_to"),
            MakeRelation("r-b-depends", "seedB", "n4", "depends_on"));
        try
        {
            var results = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seedA", "seedB"],
                Direction = RelationDirection.Outgoing,
                RelationType = "depends_on",
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(2, results.Count);
            foreach (var result in results)
            {
                Assert.AreEqual(1, result.Relations.Count, $"{result.ItemId} 应只返回 depends_on 类型");
                Assert.AreEqual("depends_on", result.Relations[0].RelationType);
            }
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighborsBatch_PerSeedTake_LimitsEachSeed()
    {
        // seedA 和 seedB 各有 5 条出边，权重 1-5；Take=2 应只返回 top 2（weight 5 和 4）
        var seedRelations = Enumerable.Range(1, 5)
            .Select(i => MakeRelation($"r-a-{i}", "seedA", $"na{i}", "related_to", weight: (double)i))
            .Concat(Enumerable.Range(1, 5)
                .Select(i => MakeRelation($"r-b-{i}", "seedB", $"nb{i}", "related_to", weight: (double)i)))
            .ToArray();
        var store = await PrepareAsync(seedRelations);
        try
        {
            var results = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seedA", "seedB"],
                Direction = RelationDirection.Outgoing,
                Take = 2
            }, CancellationToken.None);

            Assert.AreEqual(2, results.Count);
            foreach (var result in results)
            {
                Assert.AreEqual(2, result.Relations.Count, $"{result.ItemId} 应被 Take=2 限制");
                Assert.AreEqual(5.0, result.Relations[0].Weight, "top 1 应是 weight=5");
                Assert.AreEqual(4.0, result.Relations[1].Weight, "top 2 应是 weight=4");
            }
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighborsBatch_EmptyItemIds_ReturnsEmpty()
    {
        var store = await PrepareAsync(MakeRelation("r1", "a", "b", "related_to"));
        try
        {
            var results = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = []
            }, CancellationToken.None);

            Assert.AreEqual(0, results.Count);
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighborsBatch_NoMatches_ReturnsEmpty()
    {
        var store = await PrepareAsync(MakeRelation("r1", "a", "b", "related_to"));
        try
        {
            var results = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["nonexistent-seed"],
                Direction = RelationDirection.Both,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(0, results.Count, "无邻居的种子不应出现在结果中（contract: 调用者容忍缺失）");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task QueryNeighborsBatch_DuplicateSeedIds_Deduped()
    {
        var store = await PrepareAsync(
            MakeRelation("r1", "seedA", "n1", "related_to"),
            MakeRelation("r2", "seedA", "n2", "related_to"));
        try
        {
            var results = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["seedA", "seedA", "seedA"],
                Direction = RelationDirection.Outgoing,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(1, results.Count, "重复的 seedA 应去重");
            Assert.AreEqual(2, results[0].Relations.Count);
            Assert.AreEqual("seedA", results[0].ItemId);
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    /// <summary>
    /// P1-6 关键契约：单个种子的 batch 结果应与 single-seed QueryNeighborsAsync 一致
    /// （方向、过滤、排序、Take/MaxScan 语义都应一致）。
    /// </summary>
    [TestMethod]
    public async Task QueryNeighborsBatch_SingleSeed_MatchesSingleQuery()
    {
        var store = await PrepareAsync(
            MakeRelation("r-w1", "center", "n1", "related_to", weight: 1.0),
            MakeRelation("r-w2", "center", "n2", "related_to", weight: 2.0),
            MakeRelation("r-w9", "center", "n3", "related_to", weight: 9.0),
            MakeRelation("r-w10", "center", "n4", "related_to", weight: 10.0));
        try
        {
            var single = await store.QueryNeighborsAsync(new RelationNeighborQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemId = "center",
                Direction = RelationDirection.Outgoing,
                Take = 100
            }, CancellationToken.None);

            var batch = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["center"],
                Direction = RelationDirection.Outgoing,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(1, batch.Count);
            Assert.AreEqual("center", batch[0].ItemId);
            Assert.AreEqual(single.Count, batch[0].Relations.Count, "batch 应返回与单查询相同数量的邻居");
            for (var i = 0; i < single.Count; i++)
            {
                Assert.AreEqual(single[i].Id, batch[0].Relations[i].Id, $"第 {i} 个邻居 ID 应一致");
                Assert.AreEqual(single[i].Weight, batch[0].Relations[i].Weight, 0.001, $"第 {i} 个邻居 weight 应一致");
            }
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    // ── P1-4：高基数邻居查询语义 + Truncated 信号 ─────────────────────────

    /// <summary>
    /// P1-4：种子邻居数超过 MaxScan 时，结果应标记 Truncated=true。
    /// 三个 provider 应一致：InMemory/FileSystem 全量扫描后判定；
    /// Postgres 用 LIMIT global_limit+1 探测，命中时 Truncated=true。
    /// </summary>
    [TestMethod]
    public async Task QueryNeighborsBatch_HighCardinality_SetsTruncatedTrue()
    {
        // 10 条边，MaxScan=5 → 必然截断
        var seeds = Enumerable.Range(0, 10)
            .Select(i => MakeRelation($"r-{i}", "center", $"n{i}", "related_to", weight: i + 1, createdOffsetSeconds: i))
            .ToArray();
        var store = await PrepareAsync(seeds);
        try
        {
            var batch = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["center"],
                Direction = RelationDirection.Outgoing,
                MaxScan = 5,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(1, batch.Count, "应返回一个种子的结果");
            Assert.IsTrue(batch[0].Truncated, "10 条候选超过 MaxScan=5，应标记 Truncated=true");
            Assert.IsTrue(batch[0].Relations.Count <= 5, "返回结果不应超过 MaxScan 上限");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    /// <summary>
    /// P1-4：种子邻居数远低于 MaxScan 时，结果应标记 Truncated=false。
    /// </summary>
    [TestMethod]
    public async Task QueryNeighborsBatch_LowCardinality_SetsTruncatedFalse()
    {
        // 3 条边，MaxScan=5 → 不会截断
        var store = await PrepareAsync(
            MakeRelation("r-1", "center", "n1", "related_to", weight: 1.0),
            MakeRelation("r-2", "center", "n2", "related_to", weight: 2.0),
            MakeRelation("r-3", "center", "n3", "related_to", weight: 3.0));
        try
        {
            var batch = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["center"],
                Direction = RelationDirection.Outgoing,
                MaxScan = 5,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(1, batch.Count);
            Assert.IsFalse(batch[0].Truncated, "3 条候选 < MaxScan=5，不应截断");
            Assert.AreEqual(3, batch[0].Relations.Count);
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    /// <summary>
    /// P1-4：种子邻居数恰好等于 MaxScan 时，Truncated 应为 false（边界，不算截断）。
    /// Postgres 用 +1 探测保证此边界正确，不发生假阳性。
    /// </summary>
    [TestMethod]
    public async Task QueryNeighborsBatch_AtMaxScanBoundary_SetsTruncatedFalse()
    {
        // 5 条边，MaxScan=5 → 恰好不截断
        var store = await PrepareAsync(
            MakeRelation("r-1", "center", "n1", "related_to", weight: 1.0),
            MakeRelation("r-2", "center", "n2", "related_to", weight: 2.0),
            MakeRelation("r-3", "center", "n3", "related_to", weight: 3.0),
            MakeRelation("r-4", "center", "n4", "related_to", weight: 4.0),
            MakeRelation("r-5", "center", "n5", "related_to", weight: 5.0));
        try
        {
            var batch = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["center"],
                Direction = RelationDirection.Outgoing,
                MaxScan = 5,
                Take = 100
            }, CancellationToken.None);

            Assert.AreEqual(1, batch.Count);
            Assert.IsFalse(batch[0].Truncated, "候选数 == MaxScan 不应截断（边界）");
            Assert.AreEqual(5, batch[0].Relations.Count);
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }

    /// <summary>
    /// P1-4：批量查询中，per-seed Truncated 应独立标记。
    /// 一个高基数种子 + 一个低基数种子，分别得到 Truncated=true 和 Truncated=false。
    /// </summary>
    [TestMethod]
    public async Task QueryNeighborsBatch_MixedCardinality_PerSeedTruncatedSignal()
    {
        // hub-high 有 8 条出边（>MaxScan=5），hub-low 有 3 条出边（<MaxScan=5）
        var relations = new List<ContextRelation>();
        for (var i = 0; i < 8; i++)
        {
            relations.Add(MakeRelation($"r-high-{i}", "hub-high", $"n-high-{i}", "related_to", weight: i + 1, createdOffsetSeconds: i));
        }
        for (var i = 0; i < 3; i++)
        {
            relations.Add(MakeRelation($"r-low-{i}", "hub-low", $"n-low-{i}", "related_to", weight: i + 1, createdOffsetSeconds: i));
        }
        var store = await PrepareAsync(relations.ToArray());
        try
        {
            var batch = await store.QueryNeighborsBatchAsync(new RelationNeighborBatchQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = "col",
                ItemIds = ["hub-high", "hub-low"],
                Direction = RelationDirection.Outgoing,
                MaxScan = 5,
                Take = 100
            }, CancellationToken.None);

            var bySeed = batch.ToDictionary(b => b.ItemId, b => b, StringComparer.OrdinalIgnoreCase);
            Assert.IsTrue(bySeed.ContainsKey("hub-high"), "应包含 hub-high");
            Assert.IsTrue(bySeed.ContainsKey("hub-low"), "应包含 hub-low");
            Assert.IsTrue(bySeed["hub-high"].Truncated, "hub-high 有 8 条候选 > MaxScan=5，应截断");
            Assert.IsFalse(bySeed["hub-low"].Truncated, "hub-low 有 3 条候选 < MaxScan=5，不应截断");
        }
        finally
        {
            await DisposeStoreAsync(store, CancellationToken.None);
        }
    }
}
