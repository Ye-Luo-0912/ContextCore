using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;

namespace ContextCore.Tests;

/// <summary>
/// P0-fix: FileSystem Store 并发正确性测试。
/// 验证 Mutex 正确释放、双实例并发 upsert/delete 无丢失更新、取消和超时行为、无死锁。
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("Concurrency")]
public sealed class FileSystemStoreConcurrencyTests
{
    private string? _rootPath;

    [TestInitialize]
    public void Initialize()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cc-concurrency-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_rootPath is not null && Directory.Exists(_rootPath))
        {
            try { Directory.Delete(_rootPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    private FileRelationStore CreateRelationStore()
        => new(new FileStorageOptions { RootPath = _rootPath! });

    private FileContextStore CreateContextStore()
        => new(new FileStorageOptions { RootPath = _rootPath! });

    /// <summary>
    /// P0: 双实例并发 BatchUpsert 不丢失更新。
    /// 两个 store 实例指向同一文件，各写入 50 条不重复关系，最终应有 100 条。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task RelationStore_DualInstance_ConcurrentBatchUpsert_NoLostUpdate()
    {
        const string ws = "ws-concurrent";
        const string col = "col-concurrent";
        var store1 = CreateRelationStore();
        var store2 = CreateRelationStore();

        var batch1 = Enumerable.Range(0, 50)
            .Select(i => new ContextRelation
            {
                Id = $"rel-a-{i:D3}",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = $"src-{i}",
                TargetId = $"tgt-{i}",
                RelationType = "depends-on",
                Weight = 0.5,
                Confidence = 0.9
            }).ToArray();

        var batch2 = Enumerable.Range(0, 50)
            .Select(i => new ContextRelation
            {
                Id = $"rel-b-{i:D3}",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = $"src-b-{i}",
                TargetId = $"tgt-b-{i}",
                RelationType = "depends-on",
                Weight = 0.5,
                Confidence = 0.9
            }).ToArray();

        await Task.WhenAll(
            store1.BatchUpsertAsync(batch1, default),
            store2.BatchUpsertAsync(batch2, default));

        var query = new ContextRelationQuery { WorkspaceId = ws, CollectionId = col, Take = 200 };
        var results = await store1.QueryAsync(query, default);

        Assert.AreEqual(100, results.Count,
            $"双实例并发写入后应存在 100 条关系，实际 {results.Count}（存在丢失更新）");
    }

    /// <summary>
    /// P0: 双实例并发 BatchUpsert 同一 ID 不丢失更新（后写覆盖前写）。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task RelationStore_DualInstance_ConcurrentUpsertSameId_LastWriteWins()
    {
        const string ws = "ws-overwrite";
        const string col = "col-overwrite";
        var store1 = CreateRelationStore();
        var store2 = CreateRelationStore();

        // 先写入初始值
        await store1.SaveAsync(new ContextRelation
        {
            Id = "rel-shared",
            WorkspaceId = ws,
            CollectionId = col,
            SourceId = "src",
            TargetId = "tgt",
            RelationType = "depends-on",
            Weight = 0.1,
            Confidence = 0.5
        }, default);

        // 两个实例同时更新同一条关系
        await Task.WhenAll(
            store1.SaveAsync(new ContextRelation
            {
                Id = "rel-shared",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = "src",
                TargetId = "tgt",
                RelationType = "depends-on",
                Weight = 0.8,
                Confidence = 0.9
            }, default),
            store2.SaveAsync(new ContextRelation
            {
                Id = "rel-shared",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = "src",
                TargetId = "tgt",
                RelationType = "depends-on",
                Weight = 0.9,
                Confidence = 0.95
            }, default));

        var retrieved = await store1.GetAsync(ws, col, "rel-shared", default);
        Assert.IsNotNull(retrieved, "并发更新后关系应存在");
        Assert.IsTrue(retrieved.Weight is 0.8 or 0.9,
            $"最终值应为其中一个写入值 (0.8 或 0.9)，实际 {retrieved.Weight}");
    }

    /// <summary>
    /// P0: BatchUpsert 与 DeleteAsync 并发不丢失更新。
    /// 一个实例删除关系，另一个实例同时 upsert 不同关系，最终状态应正确。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task RelationStore_ConcurrentDeleteAndUpsert_NoCorruption()
    {
        const string ws = "ws-del-upsert";
        const string col = "col-del-upsert";
        var store1 = CreateRelationStore();
        var store2 = CreateRelationStore();

        // 预置 10 条关系
        var initial = Enumerable.Range(0, 10)
            .Select(i => new ContextRelation
            {
                Id = $"rel-{i:D2}",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = $"src-{i}",
                TargetId = $"tgt-{i}",
                RelationType = "depends-on",
                Weight = 0.5,
                Confidence = 0.9
            }).ToArray();
        await store1.BatchUpsertAsync(initial, default);

        // 实例1删除 rel-00..rel-04，实例2同时 upsert rel-10..rel-14
        var deleteTask = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                await store1.DeleteAsync(ws, col, $"rel-{i:D2}", default);
            }
        });

        var upsertBatch = Enumerable.Range(10, 5)
            .Select(i => new ContextRelation
            {
                Id = $"rel-{i:D2}",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = $"src-{i}",
                TargetId = $"tgt-{i}",
                RelationType = "depends-on",
                Weight = 0.5,
                Confidence = 0.9
            }).ToArray();

        var upsertTask = store2.BatchUpsertAsync(upsertBatch, default);

        await Task.WhenAll(deleteTask, upsertTask);

        var query = new ContextRelationQuery { WorkspaceId = ws, CollectionId = col, Take = 200 };
        var results = await store1.QueryAsync(query, default);

        // 应剩余: 5 条原始 (rel-05..rel-09) + 5 条新 (rel-10..rel-14) = 10 条
        Assert.AreEqual(10, results.Count,
            $"并发删除+upsert后应存在 10 条关系，实际 {results.Count}");

        var ids = results.Select(r => r.Id).ToHashSet();
        CollectionAssert.DoesNotContain(ids.ToList(), "rel-00", "已删除的关系不应存在");
        CollectionAssert.DoesNotContain(ids.ToList(), "rel-04", "已删除的关系不应存在");
        CollectionAssert.Contains(ids.ToList(), "rel-05", "未删除的关系应存在");
        CollectionAssert.Contains(ids.ToList(), "rel-09", "未删除的关系应存在");
        CollectionAssert.Contains(ids.ToList(), "rel-10", "新写入的关系应存在");
        CollectionAssert.Contains(ids.ToList(), "rel-14", "新写入的关系应存在");
    }

    /// <summary>
    /// P0: BatchUpsert 支持取消，取消后不阻塞其他操作。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task RelationStore_BatchUpsert_Cancellation_DoesNotDeadlock()
    {
        const string ws = "ws-cancel";
        const string col = "col-cancel";
        var store = CreateRelationStore();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 立即取消

        var batch = Enumerable.Range(0, 10)
            .Select(i => new ContextRelation
            {
                Id = $"rel-cancel-{i}",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = $"src-{i}",
                TargetId = $"tgt-{i}",
                RelationType = "depends-on",
                Weight = 0.5,
                Confidence = 0.9
            }).ToArray();

        // 取消的 BatchUpsert 应抛出 OperationCanceledException (或派生类 TaskCanceledException)
        OperationCanceledException? thrown = null;
        try
        {
            await store.BatchUpsertAsync(batch, cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            thrown = ex;
        }
        Assert.IsNotNull(thrown, "已取消的 BatchUpsert 应抛出 OperationCanceledException");

        // 后续操作不应被死锁
        await store.SaveAsync(new ContextRelation
        {
            Id = "rel-after-cancel",
            WorkspaceId = ws,
            CollectionId = col,
            SourceId = "src",
            TargetId = "tgt",
            RelationType = "depends-on",
            Weight = 0.5,
            Confidence = 0.9
        }, default);

        var retrieved = await store.GetAsync(ws, col, "rel-after-cancel", default);
        Assert.IsNotNull(retrieved, "取消后后续写入应能正常读取");
    }

    /// <summary>
    /// P0: DeleteAsync 支持取消，取消后不阻塞其他操作。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task RelationStore_Delete_Cancellation_DoesNotDeadlock()
    {
        const string ws = "ws-del-cancel";
        const string col = "col-del-cancel";
        var store = CreateRelationStore();

        await store.SaveAsync(new ContextRelation
        {
            Id = "rel-to-delete",
            WorkspaceId = ws,
            CollectionId = col,
            SourceId = "src",
            TargetId = "tgt",
            RelationType = "depends-on",
            Weight = 0.5,
            Confidence = 0.9
        }, default);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OperationCanceledException? thrown = null;
        try
        {
            await store.DeleteAsync(ws, col, "rel-to-delete", cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            thrown = ex;
        }
        Assert.IsNotNull(thrown, "已取消的 Delete 应抛出 OperationCanceledException");

        // 后续操作不应被死锁
        var retrieved = await store.GetAsync(ws, col, "rel-to-delete", default);
        Assert.IsNotNull(retrieved, "取消删除后关系应仍存在");

        // 正常删除应成功
        var deleted = await store.DeleteAsync(ws, col, "rel-to-delete", default);
        Assert.IsTrue(deleted, "正常删除应返回 true");
    }

    /// <summary>
    /// P0: ContextStore 并发 Save + Get 不产生跨文件不一致。
    /// 写入者更新同一 item 的 content 和 metadata，读取者应看到一致的快照。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ContextStore_ConcurrentSaveAndGet_NoCrossFileInconsistency()
    {
        const string ws = "ws-xfile";
        const string col = "col-xfile";
        var store = CreateContextStore();

        // 预置初始 item
        await store.SaveAsync(new ContextItem
        {
            Id = "item-1",
            WorkspaceId = ws,
            CollectionId = col,
            Content = "initial",
            ContentFormat = ContextContentFormat.PlainText,
            UpdatedAt = DateTimeOffset.UtcNow
        }, default);

        // 并发: 写入者连续更新 content，读取者并发读取
        var writeTask = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await store.SaveAsync(new ContextItem
                {
                    Id = "item-1",
                    WorkspaceId = ws,
                    CollectionId = col,
                    Content = $"version-{i}",
                    ContentFormat = ContextContentFormat.PlainText,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, default);
            }
        });

        var readTask = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                var item = await store.GetAsync(ws, col, "item-1", default);
                // 关键断言: 如果 metadata 存在，content 必须可读（不返回 null）
                // 跨文件不一致的标志: metadata 有记录但 content 文件不存在
                Assert.IsNotNull(item, "GetAsync 在并发写入期间应返回非 null（metadata 存在则 content 应存在）");
                Assert.IsFalse(string.IsNullOrEmpty(item.Content),
                    "GetAsync 在并发写入期间 content 不应为空");
            }
        });

        await Task.WhenAll(writeTask, readTask);
    }

    /// <summary>
    /// P0: ContextStore 并发 Save + Query 不产生异常或数据损坏。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ContextStore_ConcurrentSaveAndQuery_NoCorruption()
    {
        const string ws = "ws-query";
        const string col = "col-query";
        var store = CreateContextStore();

        // 预置 items
        for (var i = 0; i < 5; i++)
        {
            await store.SaveAsync(new ContextItem
            {
                Id = $"item-{i}",
                WorkspaceId = ws,
                CollectionId = col,
                Content = $"content-{i}",
                ContentFormat = ContextContentFormat.PlainText,
                UpdatedAt = DateTimeOffset.UtcNow
            }, default);
        }

        var writeTask = Task.Run(async () =>
        {
            for (var i = 5; i < 25; i++)
            {
                await store.SaveAsync(new ContextItem
                {
                    Id = $"item-{i}",
                    WorkspaceId = ws,
                    CollectionId = col,
                    Content = $"content-{i}",
                    ContentFormat = ContextContentFormat.PlainText,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, default);
            }
        });

        var queryTask = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                var results = await store.QueryAsync(new ContextQuery
                {
                    WorkspaceId = ws,
                    CollectionId = col,
                    IncludeContent = true,
                    Take = 50
                }, default);

                // 所有返回的 item 都应有 content（跨文件一致性）
                foreach (var item in results)
                {
                    Assert.IsFalse(string.IsNullOrEmpty(item.Content),
                        $"并发查询期间 item {item.Id} 的 content 不应为空");
                }
            }
        });

        await Task.WhenAll(writeTask, queryTask);

        // 最终一致性检查
        var final = await store.QueryAsync(new ContextQuery
        {
            WorkspaceId = ws,
            CollectionId = col,
            IncludeContent = true,
            Take = 50
        }, default);

        Assert.AreEqual(25, final.Count, "最终应有 25 个 item");
        foreach (var item in final)
        {
            Assert.IsFalse(string.IsNullOrEmpty(item.Content),
                $"最终 item {item.Id} 的 content 不应为空");
        }
    }

    /// <summary>
    /// P0: 连续多次 BatchUpsert + Delete 不死锁（验证 Mutex 正确释放）。
    /// 修复前 Mutex 只 Dispose 不 ReleaseMutex，第二次获取会超时/死锁。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task RelationStore_RepeatedUpsertDelete_NoMutexLeak()
    {
        const string ws = "ws-mutex";
        const string col = "col-mutex";
        var store = CreateRelationStore();

        // 连续 50 次 upsert + delete，验证 Mutex 每次都正确释放
        for (var i = 0; i < 50; i++)
        {
            await store.SaveAsync(new ContextRelation
            {
                Id = $"rel-{i}",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = $"src-{i}",
                TargetId = $"tgt-{i}",
                RelationType = "depends-on",
                Weight = 0.5,
                Confidence = 0.9
            }, default);

            var deleted = await store.DeleteAsync(ws, col, $"rel-{i}", default);
            Assert.IsTrue(deleted, $"第 {i} 次删除应成功");
        }

        // 最终文件应为空
        var query = new ContextRelationQuery { WorkspaceId = ws, CollectionId = col, Take = 10 };
        var results = await store.QueryAsync(query, default);
        Assert.AreEqual(0, results.Count, "连续 upsert+delete 后应无残留关系");
    }

    /// <summary>
    /// P0: 双实例连续交替写入不死锁（验证跨实例 Mutex 释放）。
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task RelationStore_DualInstance_AlternatingWrites_NoDeadlock()
    {
        const string ws = "ws-alt";
        const string col = "col-alt";
        var store1 = CreateRelationStore();
        var store2 = CreateRelationStore();

        // 两个实例交替写入，各 20 次
        for (var i = 0; i < 20; i++)
        {
            await store1.SaveAsync(new ContextRelation
            {
                Id = $"rel-s1-{i}",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = $"s1-{i}",
                TargetId = $"t-{i}",
                RelationType = "depends-on",
                Weight = 0.5,
                Confidence = 0.9
            }, default);

            await store2.SaveAsync(new ContextRelation
            {
                Id = $"rel-s2-{i}",
                WorkspaceId = ws,
                CollectionId = col,
                SourceId = $"s2-{i}",
                TargetId = $"t-{i}",
                RelationType = "depends-on",
                Weight = 0.5,
                Confidence = 0.9
            }, default);
        }

        var query = new ContextRelationQuery { WorkspaceId = ws, CollectionId = col, Take = 100 };
        var results = await store1.QueryAsync(query, default);
        Assert.AreEqual(40, results.Count, "双实例交替写入后应有 40 条关系");
    }
}
