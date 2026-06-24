using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreShortTermMemoryCompactionTests
{
    [TestMethod]
    public async Task ShortTermCompaction_ShouldMergeWorkingItems_ByWorkingKey()
    {
        var store = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        await store.SaveWorkingItemAsync(new ShortTermWorkingItem
        {
            ItemId = "task-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Kind = "ActiveTask",
            Title = "发布任务",
            Summary = "发布任务开始执行",
            Status = "active",
            Tags = ["deploy"],
            Refs = ["event-1", "event-2"],
            SourceRefs = ["source:deploy-1"],
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            Metadata = new Dictionary<string, string>
            {
                ["workingKey"] = "deploy-main"
            }
        });
        await store.SaveWorkingItemAsync(new ShortTermWorkingItem
        {
            ItemId = "task-2",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Kind = "ActiveTask",
            Title = "发布任务",
            Summary = "发布任务进入验证阶段",
            Status = "active",
            Tags = ["verification"],
            Refs = ["event-3"],
            SourceRefs = ["source:deploy-2"],
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Metadata = new Dictionary<string, string>
            {
                ["workingKey"] = "deploy-main"
            }
        });

        var service = new ShortTermMemoryCompactionService(store, new ShortTermMemoryCompactionPolicy
        {
            EnableCompaction = true,
            EnableArchive = true,
            MaxEvidenceRefsPerWorkingItem = 2
        });

        var result = await service.CompactAsync(new ShortTermMemoryCompactionRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });
        var active = await store.QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Take = 10
        });

        Assert.AreEqual(1, active.Count);
        Assert.AreEqual(1, result.MergedWorkingItems);
        Assert.AreEqual(1, result.MergedByWorkingKeyGroups);
        Assert.AreEqual(0, result.MergedByTitleGroups);
        Assert.AreEqual(1, result.ArchivedWorkingItemCount);
        Assert.AreEqual(1, result.EvidenceRefsTrimmed);
        CollectionAssert.Contains(active[0].Tags.ToArray(), "deploy");
        CollectionAssert.Contains(active[0].Tags.ToArray(), "verification");
        CollectionAssert.Contains(active[0].SourceRefs.ToArray(), "source:deploy-1");
        CollectionAssert.Contains(active[0].SourceRefs.ToArray(), "source:deploy-2");
        Assert.AreEqual(2, active[0].Refs.Count);
        StringAssert.Contains(active[0].Summary, "验证阶段");
    }

    [TestMethod]
    public async Task ShortTermCompaction_ShouldMergeWorkingItems_ByNormalizedTitle()
    {
        var store = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        await store.SaveWorkingItemAsync(new ShortTermWorkingItem
        {
            ItemId = "question-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Kind = "OpenQuestion",
            Title = "是否 需要 补 PostgreSQL Provider",
            Summary = "question one",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-8),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-8)
        });
        await store.SaveWorkingItemAsync(new ShortTermWorkingItem
        {
            ItemId = "question-2",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Kind = "OpenQuestion",
            Title = "是否需要补 PostgreSQL provider",
            Summary = "question two",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        var service = new ShortTermMemoryCompactionService(store);

        var result = await service.CompactAsync(new ShortTermMemoryCompactionRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });
        var active = await store.QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Take = 10
        });

        Assert.AreEqual(1, active.Count);
        Assert.AreEqual(1, result.MergedWorkingItems);
        Assert.AreEqual(0, result.MergedByWorkingKeyGroups);
        Assert.AreEqual(1, result.MergedByTitleGroups);
        Assert.AreEqual("question two", active[0].Summary);
    }

    [TestMethod]
    public async Task ShortTermCompaction_ShouldArchiveExpiredRawEvents()
    {
        var store = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        await store.AppendRawEventAsync(new ShortTermRawEvent
        {
            EventId = "raw-old",
            OperationId = "op-old",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            EventKind = "ingest_succeeded",
            Content = "old raw",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-10),
            SequenceId = 1
        });
        await store.AppendRawEventAsync(new ShortTermRawEvent
        {
            EventId = "raw-new",
            OperationId = "op-new",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            EventKind = "ingest_succeeded",
            Content = "new raw",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            SequenceId = 2
        });

        var service = new ShortTermMemoryCompactionService(store, new ShortTermMemoryCompactionPolicy
        {
            EnableCompaction = false,
            EnableArchive = true,
            ArchiveRawEventsAfter = TimeSpan.FromHours(1)
        });

        var result = await service.CompactAsync(new ShortTermMemoryCompactionRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });
        var active = await store.QueryRawEventsAsync(new ShortTermRawEventQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Take = 10
        });
        var archive = await store.GetArchiveSummaryAsync(new ShortTermArchiveSummaryQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });

        Assert.AreEqual(1, result.ArchivedRawEventCount);
        Assert.AreEqual(1, active.Count);
        Assert.AreEqual("raw-new", active[0].EventId);
        Assert.AreEqual(1, archive.ArchivedRawEventCount);
    }

    [TestMethod]
    public async Task ShortTermCompaction_ShouldArchiveExpiredWorkingItems()
    {
        var store = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        await store.SaveWorkingItemAsync(new ShortTermWorkingItem
        {
            ItemId = "issue-old",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Kind = "KnownIssue",
            Title = "old issue",
            Summary = "old issue",
            Status = "resolved",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2)
        });

        var service = new ShortTermMemoryCompactionService(store, new ShortTermMemoryCompactionPolicy
        {
            EnableCompaction = false,
            EnableArchive = true,
            ArchiveWorkingItemsAfter = TimeSpan.FromDays(1),
            ArchiveResolvedItemsAfter = TimeSpan.FromHours(1)
        });

        var result = await service.CompactAsync(new ShortTermMemoryCompactionRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });
        var active = await store.QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Take = 10
        });
        var archive = await store.GetArchiveSummaryAsync(new ShortTermArchiveSummaryQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });

        Assert.AreEqual(1, result.ArchivedWorkingItemCount);
        Assert.AreEqual(1, result.ArchivedResolvedWorkingItemCount);
        Assert.AreEqual(0, active.Count);
        Assert.AreEqual(1, archive.ArchivedWorkingItemCount);
        Assert.AreEqual(1, archive.ArchivedResolvedWorkingItemCount);
    }
}
