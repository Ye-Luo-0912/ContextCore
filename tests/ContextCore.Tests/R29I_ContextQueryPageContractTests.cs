using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Context;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// FTS Keyset 分页公开契约测试（P1-4）：
/// 1) 不透明游标编解码器：roundtrip / 篡改 / 版本 / 垃圾输入 / 密钥失配；
/// 2) IContextQueryPageStore 分页契约：HasMore / NextCursor / 无缝隙续取；
/// 3) ContextQueryRevision 语义修订稳定性。
/// </summary>
[TestClass]
public class R29I_ContextQueryPageContractTests
{
    private static readonly ContextQueryCursorCodec Codec = new();

    // ---------- 不透明游标编解码器 ----------

    [TestMethod]
    public void CursorCodec_RoundTrip_PreservesAllSortFields()
    {
        var cursor = new ContextQueryCursor
        {
            SourceOrder = 0,
            TsRank = 0.4321,
            Importance = 7,
            UpdatedAt = new DateTimeOffset(2026, 8, 3, 10, 30, 0, TimeSpan.Zero),
            Id = "item-42"
        };

        var token = Codec.Encode(cursor);
        var decoded = Codec.Decode(token);

        Assert.AreEqual(0, decoded.SourceOrder);
        Assert.AreEqual(cursor.TsRank, decoded.TsRank, 0.0001);
        Assert.AreEqual(cursor.Importance, decoded.Importance);
        Assert.AreEqual(cursor.UpdatedAt, decoded.UpdatedAt);
        Assert.AreEqual(cursor.Id, decoded.Id);
    }

    [TestMethod]
    public void CursorCodec_RoundTrip_IdHitSource_IsPreserved()
    {
        var cursor = new ContextQueryCursor
        {
            SourceOrder = 1,
            TsRank = 0,
            Importance = 3,
            UpdatedAt = new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.Zero),
            Id = "exact-id"
        };

        var decoded = Codec.Decode(Codec.Encode(cursor));
        Assert.AreEqual(1, decoded.SourceOrder);
        Assert.AreEqual("exact-id", decoded.Id);
    }

    [TestMethod]
    public void CursorCodec_TamperedPayload_ThrowsInvalidData()
    {
        var token = Codec.Encode(new ContextQueryCursor
        {
            SourceOrder = 0,
            TsRank = 0.5,
            Importance = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            Id = "item-1"
        });

        // 篡改 payload 段（翻转一个字符）。
        var parts = token.Split('.');
        var payload = parts[^2];
        var tamperedPayload = payload[..^1] + (payload[^1] == 'A' ? 'B' : 'A');
        var tampered = $"{string.Join('.', parts[..^2])}.{tamperedPayload}.{parts[^1]}";

        Assert.ThrowsException<InvalidDataException>(() => Codec.Decode(tampered));
    }

    [TestMethod]
    public void CursorCodec_WrongVersion_ThrowsInvalidData()
    {
        var token = Codec.Encode(new ContextQueryCursor
        {
            SourceOrder = 0,
            TsRank = 0.5,
            Importance = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            Id = "item-1"
        });

        var parts = token.Split('.');
        var wrongVersion = $"cqc.v2.{parts[^2]}.{parts[^1]}";

        Assert.ThrowsException<InvalidDataException>(() => Codec.Decode(wrongVersion));
    }

    [TestMethod]
    public void CursorCodec_GarbageInput_ThrowsInvalidData()
    {
        Assert.ThrowsException<InvalidDataException>(() => Codec.Decode("not-a-cursor"));
        Assert.ThrowsException<InvalidDataException>(() => Codec.Decode(string.Empty));
        Assert.ThrowsException<InvalidDataException>(() => Codec.Decode("   "));
    }

    [TestMethod]
    public void CursorCodec_ForgedTokenWithDifferentKey_ThrowsInvalidData()
    {
        // 用另一把密钥签名同一 payload——必须校验失败。
        var forger = new ContextQueryCursorCodec(
            System.Text.Encoding.UTF8.GetBytes("another-key-for-forgery-attempt"));
        var forged = forger.Encode(new ContextQueryCursor
        {
            SourceOrder = 0,
            TsRank = 0.5,
            Importance = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            Id = "item-1"
        });

        Assert.ThrowsException<InvalidDataException>(() => Codec.Decode(forged));
    }

    [TestMethod]
    public void CursorCodec_EncodeWithoutId_Throws()
    {
        Assert.ThrowsException<ArgumentException>(() => Codec.Encode(new ContextQueryCursor
        {
            SourceOrder = 0,
            TsRank = 0.5,
            Importance = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            Id = string.Empty
        }));
    }

    // ---------- IContextQueryPageStore 分页契约 ----------

    private static async Task RunAcrossStoresAsync(Func<IContextStore, Task> test)
    {
        await test(new InMemoryContextStore());

        var root = Path.Combine(Path.GetTempPath(), "ctx-page-" + Guid.NewGuid().ToString("N"));
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

    private static IContextQueryPageStore RequirePageStore(IContextStore store)
        => (IContextQueryPageStore)store;

    private static async Task SeedAsync(IContextStore store, int count)
    {
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 1; i <= count; i++)
        {
            await store.SaveAsync(new ContextItem
            {
                Id = $"item-{i}",
                WorkspaceId = "ws",
                CollectionId = "col",
                Type = "note",
                Title = $"标题 {i}",
                Content = $"shared retrieval content {i}",
                Importance = i,
                CreatedAt = baseTime,
                UpdatedAt = baseTime.AddMinutes(-i)
            });
        }
    }

    [TestMethod]
    public async Task PageContract_HasMoreAndNextCursor_ContinueWithoutGaps()
    {
        await RunAcrossStoresAsync(async store =>
        {
            await SeedAsync(store, 5);
            var pageStore = RequirePageStore(store);

            var page1 = await pageStore.QueryPageAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 2
            });
            Assert.AreEqual(2, page1.Items.Count);
            Assert.IsTrue(page1.HasMore, "还有 3 条未取，HasMore 应为 true");
            Assert.IsNotNull(page1.NextCursor);
            Assert.AreEqual("item-1", page1.Items[0].Id, "更新时间最新者应排第一");

            var page2 = await pageStore.QueryPageAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 2,
                After = page1.NextCursor
            });
            Assert.AreEqual(2, page2.Items.Count);
            Assert.IsTrue(page2.HasMore);
            Assert.IsNotNull(page2.NextCursor);

            var page3 = await pageStore.QueryPageAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 2,
                After = page2.NextCursor
            });
            Assert.AreEqual(1, page3.Items.Count);
            Assert.IsFalse(page3.HasMore, "末页 HasMore 应为 false");
            Assert.IsNull(page3.NextCursor, "末页不应返回游标");

            var ids = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(i => i.Id).ToArray();
            CollectionAssert.AreEqual(new[] { "item-1", "item-2", "item-3", "item-4", "item-5" }, ids);
        });
    }

    [TestMethod]
    public async Task PageContract_QueryTextPath_NextCursorContinuesWithoutDuplicates()
    {
        await RunAcrossStoresAsync(async store =>
        {
            await SeedAsync(store, 4);
            var pageStore = RequirePageStore(store);

            var page1 = await pageStore.QueryPageAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                QueryText = "shared",
                Take = 2
            });
            Assert.AreEqual(2, page1.Items.Count);
            Assert.IsTrue(page1.HasMore);

            var page2 = await pageStore.QueryPageAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                QueryText = "shared",
                Take = 2,
                After = page1.NextCursor
            });
            Assert.AreEqual(2, page2.Items.Count);
            Assert.IsFalse(page2.HasMore);

            var ids = page1.Items.Concat(page2.Items).Select(i => i.Id).ToArray();
            CollectionAssert.AreEqual(new[] { "item-1", "item-2", "item-3", "item-4" }, ids);
        });
    }

    [TestMethod]
    public async Task PageContract_EmptyResult_HasMoreFalseNoCursor()
    {
        await RunAcrossStoresAsync(async store =>
        {
            var page = await RequirePageStore(store).QueryPageAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 10
            });
            Assert.AreEqual(0, page.Items.Count);
            Assert.IsFalse(page.HasMore);
            Assert.IsNull(page.NextCursor);
        });
    }

    [TestMethod]
    public async Task PageContract_ExactTake_NextPageReturnsRest()
    {
        await RunAcrossStoresAsync(async store =>
        {
            await SeedAsync(store, 2);

            var page1 = await RequirePageStore(store).QueryPageAsync(new ContextQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Take = 2
            });
            Assert.AreEqual(2, page1.Items.Count);
            Assert.IsFalse(page1.HasMore, "恰好取完不应有下一页");
            Assert.IsNull(page1.NextCursor);
        });
    }

    /// <summary>
    /// 端到端游标胶水模拟：与 /api/context/query 端点完全相同的路径——
    /// 存储返回类型化游标 → Codec 编码为不透明 token → 请求携带 Cursor →
    /// 端点解码为 After 后续取。验证无缝隙、无重复，且签名 token 可跨页透传。
    /// </summary>
    [TestMethod]
    public async Task PageContract_EndpointCursorGlue_ContinuesWithoutGapsOrDuplicates()
    {
        await RunAcrossStoresAsync(async store =>
        {
            await SeedAsync(store, 5);
            var pageStore = RequirePageStore(store);

            // 第一页：直接查询（无 Cursor）。
            var request1 = new ContextQuery { WorkspaceId = "ws", CollectionId = "col", Take = 2 };
            var page1 = await pageStore.QueryPageAsync(request1, CancellationToken.None);
            Assert.IsTrue(page1.HasMore);
            Assert.IsNotNull(page1.NextCursor);

            // 端点：将类型化游标编码为不透明 token 返回给调用方。
            var token1 = Codec.Encode(page1.NextCursor!);

            // 调用方把 token 放回 Cursor 字段发起第二页请求（端点解码为 After）。
            var request2 = request1.CloneWith(cursor: token1);
            var after2 = Codec.Decode(request2.Cursor!);
            var page2 = await pageStore.QueryPageAsync(request2.CloneWith(after: after2), CancellationToken.None);
            Assert.AreEqual(2, page2.Items.Count);
            Assert.IsTrue(page2.HasMore);

            var token2 = Codec.Encode(page2.NextCursor!);
            var request3 = request1.CloneWith(cursor: token2);
            var after3 = Codec.Decode(request3.Cursor!);
            var page3 = await pageStore.QueryPageAsync(request3.CloneWith(after: after3), CancellationToken.None);
            Assert.AreEqual(1, page3.Items.Count);
            Assert.IsFalse(page3.HasMore);
            Assert.IsNull(page3.NextCursor);

            var ids = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(i => i.Id).ToArray();
            CollectionAssert.AreEqual(new[] { "item-1", "item-2", "item-3", "item-4", "item-5" }, ids);
        });
    }

    // ---------- ContextQueryRevision ----------

    [TestMethod]
    public void QueryRevision_StableForSameQuery_AndChangesOnSemanticChange()
    {
        var query = new ContextQuery
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            QueryText = "shared",
            Tags = ["a", "b"],
            Types = ["note"],
            ExcludedTypes = ["secret"],
            ExcludedIds = ["item-x"],
            Refs = ["ref:1"],
            IncludeContent = true,
            IncludeDerived = false
        };

        var same = new ContextQuery
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            QueryText = "shared",
            Tags = ["a", "b"],
            Types = ["note"],
            ExcludedTypes = ["secret"],
            ExcludedIds = ["item-x"],
            Refs = ["ref:1"],
            IncludeContent = true,
            IncludeDerived = false
        };

        var changedQueryText = new ContextQuery { WorkspaceId = "ws", CollectionId = "col", QueryText = "other", Types = ["note"], ExcludedTypes = ["secret"], ExcludedIds = ["item-x"], Refs = ["ref:1"], IncludeContent = true, IncludeDerived = false };
        var changedCollection = new ContextQuery { WorkspaceId = "ws", CollectionId = "col-other", QueryText = "shared", Types = ["note"], ExcludedTypes = ["secret"], ExcludedIds = ["item-x"], Refs = ["ref:1"], IncludeContent = true, IncludeDerived = false };
        var changedTags = new ContextQuery { WorkspaceId = "ws", CollectionId = "col", QueryText = "shared", Tags = ["b", "a"], Types = ["note"], ExcludedTypes = ["secret"], ExcludedIds = ["item-x"], Refs = ["ref:1"], IncludeContent = true, IncludeDerived = false };

        Assert.AreEqual(ContextQueryRevision.Compute(query), ContextQueryRevision.Compute(same));
        Assert.AreNotEqual(ContextQueryRevision.Compute(query), ContextQueryRevision.Compute(changedQueryText));
        Assert.AreNotEqual(ContextQueryRevision.Compute(query), ContextQueryRevision.Compute(changedCollection));
        // Tags 顺序无关：语义相同的集合修订一致。
        Assert.AreEqual(ContextQueryRevision.Compute(query), ContextQueryRevision.Compute(changedTags));
        StringAssert.StartsWith(ContextQueryRevision.Compute(query), "qrv1:");
    }

    [TestMethod]
    public void QueryRevision_IgnoresPagingFields()
    {
        var query = new ContextQuery { WorkspaceId = "ws", CollectionId = "col", QueryText = "x", Take = 10 };
        var paged = new ContextQuery { WorkspaceId = "ws", CollectionId = "col", QueryText = "x", Take = 3, Skip = 5, After = new ContextQueryCursor { Id = "item-1" } };

        Assert.AreEqual(ContextQueryRevision.Compute(query), ContextQueryRevision.Compute(paged));
    }
}
