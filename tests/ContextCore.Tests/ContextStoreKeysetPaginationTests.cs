using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.Tests;

/// <summary>
/// Keyset 分页契约测试：同一组数据分别在 InMemory / FileSystem 存储上验证
/// 游标续取的无缝衔接（无遗漏、无重复、决胜键确定）。
/// </summary>
[TestClass]
public class ContextStoreKeysetPaginationTests
{
    private static async Task RunAcrossStoresAsync(Func<IContextStore, Task> test)
    {
        await test(new InMemoryContextStore());

        var root = Path.Combine(Path.GetTempPath(), "ctx-keyset-" + Guid.NewGuid().ToString("N"));
        try
        {
            await test(new FileContextStore(new FileStorageOptions { RootPath = root }));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task SeedItemsAsync(IContextStore store, int count, DateTimeOffset baseTime, bool includeContent = true)
    {
        for (var i = 1; i <= count; i++)
        {
            await store.SaveAsync(new ContextItem
            {
                Id = $"item-{i}",
                WorkspaceId = "ws",
                CollectionId = "col",
                Type = "note",
                Title = $"标题 {i}",
                Content = includeContent ? $"shared retrieval content {i}" : string.Empty,
                Importance = i,
                CreatedAt = baseTime,
                UpdatedAt = baseTime.AddMinutes(-i)
            });
        }
    }

    private static ContextQueryCursor CursorFrom(ContextItem item) => new()
    {
        Importance = item.Importance,
        UpdatedAt = item.UpdatedAt,
        Id = item.Id
    };

    [TestMethod]
    public async Task Keyset_PlainPath_SecondPageContinuesWithoutGapsOrDuplicates()
    {
        await RunAcrossStoresAsync(async store =>
        {
            var baseTime = DateTimeOffset.UtcNow;
            await SeedItemsAsync(store, 6, baseTime);

            var page1 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 3
            });
            Assert.AreEqual(3, page1.Count);
            Assert.AreEqual("item-1", page1[0].Id, "更新时间最新者应排第一");

            var page2 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 3,
                After = CursorFrom(page1[^1])
            });
            Assert.AreEqual(3, page2.Count);

            var ids = page1.Select(i => i.Id).Concat(page2.Select(i => i.Id)).ToArray();
            CollectionAssert.AreEqual(new[] { "item-1", "item-2", "item-3", "item-4", "item-5", "item-6" }, ids);
        });
    }

    [TestMethod]
    public async Task Keyset_PlainPathWithoutContent_ContinuesWithoutGapsOrDuplicates()
    {
        await RunAcrossStoresAsync(async store =>
        {
            var baseTime = DateTimeOffset.UtcNow;
            await SeedItemsAsync(store, 6, baseTime);

            var page1 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 3,
                IncludeContent = false
            });
            Assert.AreEqual(3, page1.Count);

            var page2 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 3,
                IncludeContent = false,
                After = CursorFrom(page1[^1])
            });
            Assert.AreEqual(3, page2.Count);

            var ids = page1.Select(i => i.Id).Concat(page2.Select(i => i.Id)).ToArray();
            CollectionAssert.AreEqual(new[] { "item-1", "item-2", "item-3", "item-4", "item-5", "item-6" }, ids);
            Assert.IsTrue(page1.All(i => i.Content.Length == 0) && page2.All(i => i.Content.Length == 0));
        });
    }

    [TestMethod]
    public async Task Keyset_IgnoresSkip_WhenCursorSet()
    {
        await RunAcrossStoresAsync(async store =>
        {
            var baseTime = DateTimeOffset.UtcNow;
            await SeedItemsAsync(store, 6, baseTime);

            var page1 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 3
            });
            var cursor = CursorFrom(page1[^1]);

            var page2 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 3,
                Skip = 999,
                After = cursor
            });
            var page2NoSkip = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 3,
                After = cursor
            });
            CollectionAssert.AreEqual(page2NoSkip.Select(i => i.Id).ToArray(), page2.Select(i => i.Id).ToArray());
        });
    }

    [TestMethod]
    public async Task Keyset_SameUpdatedAt_UsesIdAsTiebreaker()
    {
        await RunAcrossStoresAsync(async store =>
        {
            var sameTime = DateTimeOffset.UtcNow;
            foreach (var id in new[] { "item-a", "item-b", "item-c", "item-d", "item-e" })
            {
                await store.SaveAsync(new ContextItem
                {
                    Id = id,
                    WorkspaceId = "ws",
                    CollectionId = "col",
                    Type = "note",
                    Content = $"content {id}",
                    UpdatedAt = sameTime
                });
            }

            var page1 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 2
            });
            CollectionAssert.AreEqual(new[] { "item-e", "item-d" }, page1.Select(i => i.Id).ToArray());

            var page2 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 2,
                After = CursorFrom(page1[^1])
            });
            CollectionAssert.AreEqual(new[] { "item-c", "item-b" }, page2.Select(i => i.Id).ToArray());

            var page3 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 2,
                After = CursorFrom(page2[^1])
            });
            CollectionAssert.AreEqual(new[] { "item-a" }, page3.Select(i => i.Id).ToArray());
        });
    }

    [TestMethod]
    public async Task Keyset_QueryTextPath_ContinuesWithoutDuplicates()
    {
        await RunAcrossStoresAsync(async store =>
        {
            var baseTime = DateTimeOffset.UtcNow;
            await SeedItemsAsync(store, 6, baseTime);

            var page1 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                QueryText = "shared",
                Take = 3
            });
            Assert.AreEqual(3, page1.Count);

            var page2 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                QueryText = "shared",
                Take = 3,
                After = CursorFrom(page1[^1])
            });
            Assert.AreEqual(3, page2.Count);

            var ids = page1.Select(i => i.Id).Concat(page2.Select(i => i.Id)).ToArray();
            CollectionAssert.AreEqual(new[] { "item-1", "item-2", "item-3", "item-4", "item-5", "item-6" }, ids);
        });
    }

    [TestMethod]
    public async Task Keyset_Exhausted_ReturnsEmpty()
    {
        await RunAcrossStoresAsync(async store =>
        {
            var baseTime = DateTimeOffset.UtcNow;
            await SeedItemsAsync(store, 2, baseTime);

            var page1 = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 2
            });
            Assert.AreEqual(2, page1.Count);

            var rest = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 5,
                After = CursorFrom(page1[^1])
            });
            Assert.AreEqual(0, rest.Count);
        });
    }

    [TestMethod]
    public void BuildAfterPredicate_GeneratesDescLexicographicChain()
    {
        var threeColumn = PostgresContextStore.BuildAfterPredicate(["importance", "updated_at", "id"]);
        Assert.AreEqual(
            "(importance < @after_importance) OR (importance = @after_importance AND updated_at < @after_updated_at) OR (importance = @after_importance AND updated_at = @after_updated_at AND id < @after_id)",
            threeColumn);

        var fourColumn = PostgresContextStore.BuildAfterPredicate(["ts_rank", "importance", "updated_at", "id"]);
        Assert.AreEqual(
            "(ts_rank < @after_ts_rank) OR (ts_rank = @after_ts_rank AND importance < @after_importance) OR (ts_rank = @after_ts_rank AND importance = @after_importance AND updated_at < @after_updated_at) OR (ts_rank = @after_ts_rank AND importance = @after_importance AND updated_at = @after_updated_at AND id < @after_id)",
            fourColumn);
    }
}
