using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

/// <summary>
/// GRAPH-10：IRelationStore provider contract 基类。
/// 同一套断言在 InMemory / FileSystem / Postgres 三个 provider 上运行，验证核心契约一致：
/// Get/Delete/BatchUpsert 行为，以及 QueryNeighborsAsync(RelationNeighborQuery) 的方向、类型、
/// 置信度、生命周期、ReviewStatus 过滤和 Take 分页语义。
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
}
