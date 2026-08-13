using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 存储 QueryText 按词元匹配：自然语言问句只要命中正文中的词即可召回。
/// </summary>
[TestClass]
public sealed class ContextStoreQueryTextTermMatchTests
{
    [TestMethod]
    public async Task InMemoryStore_NaturalLanguageQuery_HitsSharedTerm()
    {
        var store = new InMemoryContextStore();
        await SaveNoteAsync(store);
        await AssertNaturalLanguageHitsAsync(store);
        await AssertWholeStringStillHitsAsync(store);
        await AssertUnrelatedMissesAsync(store);
    }

    [TestMethod]
    public async Task FileContextStore_NaturalLanguageQuery_HitsSharedTerm()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-query-terms", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileContextStore(new FileStorageOptions { RootPath = root });
            await SaveNoteAsync(store);
            await AssertNaturalLanguageHitsAsync(store);
            await AssertWholeStringStillHitsAsync(store);
            await AssertUnrelatedMissesAsync(store);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task SaveNoteAsync(IContextStore store)
    {
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new ContextItem
        {
            Id = "note-1",
            WorkspaceId = "ws",
            CollectionId = "demo",
            Type = "note",
            Content = "PurpleBicycle-42: the project uses a working-set plus search, not an append-only transcript.",
            ContentFormat = ContextContentFormat.PlainText,
            Importance = 0.5,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private static async Task AssertNaturalLanguageHitsAsync(IContextStore store)
    {
        var hits = await store.QueryAsync(new ContextQuery
        {
            WorkspaceId = "ws",
            CollectionId = "demo",
            QueryText = "Summarize PurpleBicycle-42 project context approach",
            Take = 10,
            IncludeContent = true
        });
        Assert.AreEqual(1, hits.Count, "自然语言问句应靠词元命中正文");
        StringAssert.Contains(hits[0].Content, "PurpleBicycle-42");
    }

    private static async Task AssertWholeStringStillHitsAsync(IContextStore store)
    {
        var hits = await store.QueryAsync(new ContextQuery
        {
            WorkspaceId = "ws",
            CollectionId = "demo",
            QueryText = "PurpleBicycle-42",
            Take = 10,
            IncludeContent = true
        });
        Assert.AreEqual(1, hits.Count);
    }

    private static async Task AssertUnrelatedMissesAsync(IContextStore store)
    {
        var hits = await store.QueryAsync(new ContextQuery
        {
            WorkspaceId = "ws",
            CollectionId = "demo",
            QueryText = "completely unrelated zucchini recipe",
            Take = 10,
            IncludeContent = true
        });
        Assert.AreEqual(0, hits.Count);
    }
}
