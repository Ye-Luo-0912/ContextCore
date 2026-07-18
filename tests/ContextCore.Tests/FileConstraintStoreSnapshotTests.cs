using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;

namespace ContextCore.Tests;

/// <summary>
/// R13.2 #2：验证 FileConstraintStore 的 Provider 内按 Level/Layer 复用快照能力。
/// 关键场景：单次 build 内 Hard/Soft/All 三次 Query 共享同一份 global + collection JSONL 反序列化结果，
/// 通过 last-write-time 校验复用，避免 3 次重复文件 I/O。
/// </summary>
[TestClass]
[TestCategory("FileSystem")]
[TestCategory("Package")]
public sealed class FileConstraintStoreSnapshotTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "contextcore-constraint-snapshot-tests",
            Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* 句柄延迟回收，忽略 */ }
        }
    }

    /// <summary>
    /// R13.2 #2 核心场景：Hard/Soft/All 三次 Query 同一 collection，
    /// 第 1 次触发 global + collection 两次 miss，第 2/3 次完全命中快照。
    /// </summary>
    [TestMethod]
    public async Task HardSoftAllQueries_WithinBuild_ReuseSnapshot()
    {
        var store = new FileConstraintStore(new FileStorageOptions { RootPath = _root });

        await store.SaveAsync(CreateConstraint("hard-1", ConstraintLevel.Hard, "Must keep system boundaries."));
        await store.SaveAsync(CreateConstraint("soft-1", ConstraintLevel.Soft, "Prefer short answers."));
        await store.SaveAsync(CreateConstraint("info-1", ConstraintLevel.System, "System info."));

        store.ResetSnapshotCacheForTests();

        // 第 1 次（Hard）：cold path，触发 global + collection 两次 miss
        var hard = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Level = ConstraintLevel.Hard,
            Take = 10
        });
        Assert.AreEqual(1, hard.Count, "Hard query should return 1");
        Assert.AreEqual(2, store.SnapshotMisses, "Hard query: 2 misses (global + collection)");
        Assert.AreEqual(0, store.SnapshotHits, "Hard query: 0 hits");

        // 第 2 次（Soft）：完全命中快照
        var soft = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Level = ConstraintLevel.Soft,
            Take = 10
        });
        Assert.AreEqual(1, soft.Count, "Soft query should return 1");
        Assert.AreEqual(2, store.SnapshotMisses, "Soft query: still 2 misses (no new reads)");
        Assert.AreEqual(2, store.SnapshotHits, "Soft query: 2 hits (global + collection)");

        // 第 3 次（All Levels）：完全命中快照
        var all = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });
        Assert.AreEqual(3, all.Count, "All-levels query should return 3");
        Assert.AreEqual(2, store.SnapshotMisses, "All query: still 2 misses");
        Assert.AreEqual(4, store.SnapshotHits, "All query: 2 more hits (cumulative 4)");
    }

    /// <summary>
    /// SaveAsync 后下次 Query 应 miss（last-write-time 改变）。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_InvalidatesSnapshot_NextQueryMisses()
    {
        var store = new FileConstraintStore(new FileStorageOptions { RootPath = _root });

        await store.SaveAsync(CreateConstraint("c-1", ConstraintLevel.Hard, "Initial constraint."));

        // 首次查询：cold miss
        var first = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });
        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(2, store.SnapshotMisses);

        // SaveAsync 改写 collection 文件 → 该路径快照失效
        await store.SaveAsync(CreateConstraint("c-2", ConstraintLevel.Soft, "New constraint added."));

        // 第二次查询：global 命中，collection miss（被 SaveAsync 失效）
        var second = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });
        Assert.AreEqual(2, second.Count, "新约束应可见");
        Assert.AreEqual(3, store.SnapshotMisses, "collection 文件应重新读取一次");
        Assert.AreEqual(1, store.SnapshotHits, "global 文件仍命中");
    }

    /// <summary>
    /// 外部直接写文件（绕过 SaveAsync）也应触发 miss，因为 last-write-time 改变。
    /// 模拟方式：用第二个 store 实例写入（不同 _snapshots 字典），
    /// 验证第一个 store 通过 last-write-time 检测到外部变更。
    /// </summary>
    [TestMethod]
    public async Task ExternalFileWrite_ChangesLastWriteTime_NextQueryMisses()
    {
        var store = new FileConstraintStore(new FileStorageOptions { RootPath = _root });
        var externalWriter = new FileConstraintStore(new FileStorageOptions { RootPath = _root });

        await store.SaveAsync(CreateConstraint("c-1", ConstraintLevel.Hard, "Initial."));
        var first = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });
        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(2, store.SnapshotMisses);

        // 等待文件系统时间戳精度可区分
        await Task.Delay(20);

        // 另一 store 实例写入：不经过本 store 的 SaveAsync，所以不会显式清空本 store 的 _snapshots
        await externalWriter.SaveAsync(CreateConstraint("c-2", ConstraintLevel.Soft, "External write."));

        var second = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });
        Assert.AreEqual(2, second.Count, "外部写入的约束应通过 last-write-time 失效被读到");
        Assert.AreEqual(3, store.SnapshotMisses, "collection 文件 last-write-time 改变 → miss");
        Assert.AreEqual(1, store.SnapshotHits, "global 文件未变 → 命中");
    }

    /// <summary>
    /// 不同 collectionId 的快照条目互不影响。
    /// </summary>
    [TestMethod]
    public async Task DifferentCollectionIds_HaveIndependentSnapshots()
    {
        var store = new FileConstraintStore(new FileStorageOptions { RootPath = _root });

        await store.SaveAsync(CreateConstraint("a-1", ConstraintLevel.Hard, "A1", collectionId: "col-a"));
        await store.SaveAsync(CreateConstraint("b-1", ConstraintLevel.Hard, "B1", collectionId: "col-b"));

        store.ResetSnapshotCacheForTests();

        // Cold: 2 misses (global + col-a)
        await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "col-a",
            Take = 10
        });
        Assert.AreEqual(2, store.SnapshotMisses);

        // Cold: 2 misses (global hit + col-b miss → 1 miss, 1 hit)
        await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "col-b",
            Take = 10
        });
        Assert.AreEqual(3, store.SnapshotMisses, "col-b collection 文件是新的 → miss");
        Assert.AreEqual(1, store.SnapshotHits, "global 命中");

        // 再次查询 col-a：global + col-a 全命中
        await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "col-a",
            Take = 10
        });
        Assert.AreEqual(3, store.SnapshotMisses, "无新增 miss");
        Assert.AreEqual(3, store.SnapshotHits, "global + col-a 均命中");
    }

    /// <summary>
    /// 文件不存在时缓存空结果（last-write-time = DateTime.MinValue），
    /// 避免反复 File.Exists 检查；写入后失效自动重读。
    /// </summary>
    [TestMethod]
    public async Task NonExistentFile_CachesEmptyResult_ThenMissesAfterWrite()
    {
        var store = new FileConstraintStore(new FileStorageOptions { RootPath = _root });

        // 查询从未写过的 collection：global + collection 文件都不存在
        var first = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "never-written",
            Take = 10
        });
        Assert.AreEqual(0, first.Count);
        Assert.AreEqual(2, store.SnapshotMisses, "两个文件都不存在 → 2 次 miss");

        // 再次查询相同路径：应全部命中（空结果也被缓存）
        var second = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "never-written",
            Take = 10
        });
        Assert.AreEqual(0, second.Count);
        Assert.AreEqual(2, store.SnapshotMisses, "无新增 miss");
        Assert.AreEqual(2, store.SnapshotHits, "两个空快照均命中");

        // 写入后：collection 文件 last-write-time 改变 → miss
        await store.SaveAsync(CreateConstraint("c-1", ConstraintLevel.Hard, "Now exists.", collectionId: "never-written"));

        var third = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "never-written",
            Take = 10
        });
        Assert.AreEqual(1, third.Count);
        Assert.AreEqual(3, store.SnapshotMisses, "collection 文件 last-write-time 改变 → miss");
    }

    /// <summary>
    /// GetAsync 也使用快照（与 QueryAsync 共享缓存）。
    /// </summary>
    [TestMethod]
    public async Task GetAsync_UsesSnapshot_SharedWithQueryAsync()
    {
        var store = new FileConstraintStore(new FileStorageOptions { RootPath = _root });

        await store.SaveAsync(CreateConstraint("c-1", ConstraintLevel.Hard, "Stored constraint."));

        // 先 QueryAsync 填充快照
        await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });
        Assert.AreEqual(2, store.SnapshotMisses);

        // GetAsync 应命中快照（不重复读文件）
        var match = await store.GetAsync("c-1");
        Assert.IsNotNull(match);
        Assert.AreEqual("c-1", match!.Id);
        Assert.AreEqual(2, store.SnapshotMisses, "GetAsync 应命中已填充的快照，无新增 miss");
        Assert.IsTrue(store.SnapshotHits >= 2, "GetAsync 应触发至少 2 次命中（global + collection）");
    }

    /// <summary>
    /// ResetSnapshotCacheForTests 清空快照与计数器。
    /// </summary>
    [TestMethod]
    public async Task ResetSnapshotCacheForTests_ClearsCacheAndCounters()
    {
        var store = new FileConstraintStore(new FileStorageOptions { RootPath = _root });

        await store.SaveAsync(CreateConstraint("c-1", ConstraintLevel.Hard, "Constraint."));
        await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });
        Assert.IsTrue(store.SnapshotMisses > 0);
        Assert.IsTrue(store.SnapshotHits >= 0);

        store.ResetSnapshotCacheForTests();

        Assert.AreEqual(0, store.SnapshotHits);
        Assert.AreEqual(0, store.SnapshotMisses);

        // 重置后再次查询应全部 miss
        await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });
        Assert.AreEqual(2, store.SnapshotMisses, "重置后再次查询应全部 miss");
        Assert.AreEqual(0, store.SnapshotHits);
    }

    /// <summary>
    /// 并发查询场景下快照安全：无异常、计数器正确累加。
    /// </summary>
    [TestMethod]
    public async Task ConcurrentQueries_AreSafe_CountersAccurate()
    {
        var store = new FileConstraintStore(new FileStorageOptions { RootPath = _root });

        await store.SaveAsync(CreateConstraint("c-1", ConstraintLevel.Hard, "H1."));
        await store.SaveAsync(CreateConstraint("c-2", ConstraintLevel.Soft, "S1."));

        store.ResetSnapshotCacheForTests();

        // 第一波 8 个并发查询：竞争填充快照
        var coldTasks = Enumerable.Range(0, 8).Select(_ => store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        }));
        var coldResults = await Task.WhenAll(coldTasks);

        foreach (var result in coldResults)
        {
            Assert.AreEqual(2, result.Count, "所有并发查询结果应一致");
        }

        var totalCold = store.SnapshotHits + store.SnapshotMisses;
        Assert.IsTrue(totalCold >= 2, "至少完成 2 次 miss 才能填充快照");

        // 第二波 8 个并发查询：应全部命中
        var warmTasks = Enumerable.Range(0, 8).Select(_ => store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        }));
        var warmResults = await Task.WhenAll(warmTasks);

        foreach (var result in warmResults)
        {
            Assert.AreEqual(2, result.Count);
        }

        var warmHits = store.SnapshotHits - (totalCold - store.SnapshotMisses);
        // 第二波至少 8 次查询，每次至少 2 个 hit (global + collection)，应该 ≥ 16 hits
        Assert.IsTrue(warmHits >= 16, $"warm path 应全部命中，至少 16 次 hit，实际 {warmHits}");
    }

    private static ContextConstraint CreateConstraint(
        string id,
        ConstraintLevel level,
        string content,
        string? collectionId = "collection-test")
    {
        var now = DateTimeOffset.UtcNow;
        return new ContextConstraint
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = collectionId,
            Scope = ContextScope.Collection,
            Level = level,
            Content = content,
            AppliesToRefs = Array.Empty<string>(),
            SourceRefs = new[] { $"source:{id}" },
            Status = ContextMemoryStatus.Verified,
            Confidence = 1.0,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
