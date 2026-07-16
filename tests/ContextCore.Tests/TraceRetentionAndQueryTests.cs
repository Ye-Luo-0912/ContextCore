using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 验证 R12-5：FileSystem trace store 的 QueryRecent 尾部读取优化（budget 提前终止）
/// 和 retention/compaction（FileTraceJanitor 按 yyyyMMdd 分片清理过期数据）。
/// </summary>
[TestClass]
[TestCategory("FileSystem")]
public sealed class TraceRetentionAndQueryTests
{
    private static readonly string WorkspaceId = "workspace-r12-5";
    private static readonly string CollectionId = "collection-r12-5";

    [TestMethod]
    public async Task QueryRecent_WithMultipleShards_ReturnsLatestRecordsByCreatedAt()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new FilePathResolver(new FileStorageOptions { RootPath = root, TraceRetentionDays = 0 });
            var serializer = new FileFormatSerializer();
            var store = new FileRetrievalTraceStore(paths, serializer);

            // 写入今天的 3 条记录
            var now = DateTimeOffset.UtcNow;
            await store.SaveAsync(new ContextRetrievalTrace
            {
                RetrievalId = "t1", WorkspaceId = WorkspaceId, CollectionId = CollectionId,
                QueryText = "today-1", CreatedAt = now.AddMinutes(-10)
            });
            await store.SaveAsync(new ContextRetrievalTrace
            {
                RetrievalId = "t2", WorkspaceId = WorkspaceId, CollectionId = CollectionId,
                QueryText = "today-2", CreatedAt = now.AddMinutes(-5)
            });
            await store.SaveAsync(new ContextRetrievalTrace
            {
                RetrievalId = "t3", WorkspaceId = WorkspaceId, CollectionId = CollectionId,
                QueryText = "today-3", CreatedAt = now
            });

            // 手动写入历史分片（2 天前），模拟历史数据
            var traceDir = paths.GetRetrievalTraceDirectory(WorkspaceId, CollectionId);
            var oldShardDir = Path.Combine(traceDir, now.AddDays(-2).ToString("yyyyMMdd"));
            Directory.CreateDirectory(oldShardDir);
            var oldJsonLines = new FileJsonLineStore(serializer);
            await oldJsonLines.AppendAsync(
                Path.Combine(oldShardDir, "retrieval-traces.jsonl"),
                new ContextRetrievalTrace
                {
                    RetrievalId = "old-1", WorkspaceId = WorkspaceId, CollectionId = CollectionId,
                    QueryText = "old", CreatedAt = now.AddDays(-2)
                });

            var recent = await store.QueryRecentAsync(WorkspaceId, CollectionId, take: 3);

            Assert.AreEqual(3, recent.Count);
            // 最新的 3 条应该是今天的，不含 2 天前的记录
            CollectionAssert.AreEqual(
                new[] { "t3", "t2", "t1" },
                recent.Select(r => r.RetrievalId).ToArray());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task QueryRecent_BudgetEarlyTermination_DoesNotReadExcessivelyOldShards()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = new FilePathResolver(new FileStorageOptions { RootPath = root, TraceRetentionDays = 0 });
            var serializer = new FileFormatSerializer();
            var store = new FileRetrievalTraceStore(paths, serializer);
            var traceDir = paths.GetRetrievalTraceDirectory(WorkspaceId, CollectionId);

            // 分片 B（昨天 mtime 较旧）：1 条记录，CreatedAt 异常地比今天更新
            // 如果 budget 提前终止生效，分片 B 不会被读取，其异常记录不会出现
            // 先创建 yesterday 分片，确保 mtime 比 today 分片旧
            var yesterdayDir = Path.Combine(traceDir, DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyyMMdd"));
            Directory.CreateDirectory(yesterdayDir);
            var jsonLines = new FileJsonLineStore(serializer);
            await jsonLines.AppendAsync(
                Path.Combine(yesterdayDir, "retrieval-traces.jsonl"),
                new ContextRetrievalTrace
                {
                    RetrievalId = "yesterday-anomaly", WorkspaceId = WorkspaceId, CollectionId = CollectionId,
                    QueryText = "anomaly", CreatedAt = DateTimeOffset.UtcNow // 比 today 的都新
                });

            // 分片 A（今天 mtime 最新）：10 条记录，CreatedAt 都是 1 小时前
            var todayDir = Path.Combine(traceDir, DateTimeOffset.UtcNow.ToString("yyyyMMdd"));
            Directory.CreateDirectory(todayDir);
            var todayPath = Path.Combine(todayDir, "retrieval-traces.jsonl");
            var baseTime = DateTimeOffset.UtcNow.AddMinutes(-60);
            for (var i = 0; i < 10; i++)
            {
                await jsonLines.AppendAsync(todayPath, new ContextRetrievalTrace
                {
                    RetrievalId = $"today-{i}", WorkspaceId = WorkspaceId, CollectionId = CollectionId,
                    QueryText = $"today-{i}", CreatedAt = baseTime.AddSeconds(i)
                });
            }

            // take=3 → budget=6；读 today 分片得 10 条 >= 6，提前终止，不读 yesterday
            var recent = await store.QueryRecentAsync(WorkspaceId, CollectionId, take: 3);

            Assert.AreEqual(3, recent.Count);
            // 如果 yesterday 被读取了，"yesterday-anomaly" 会在结果顶部（CreatedAt 最新）
            // budget 提前终止时它不应出现
            Assert.IsFalse(
                recent.Any(r => r.RetrievalId == "yesterday-anomaly"),
                "budget 提前终止应阻止读取旧分片中的异常记录");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task Retention_PurgesExpiredShards_OnSaveAsync()
    {
        var root = CreateTempRoot();
        try
        {
            var options = new FileStorageOptions { RootPath = root, TraceRetentionDays = 7 };
            var paths = new FilePathResolver(options);
            var serializer = new FileFormatSerializer();
            var store = new FileRetrievalTraceStore(options);

            var traceDir = paths.GetRetrievalTraceDirectory(WorkspaceId, CollectionId);

            // 创建过期分片（10 天前）和当前分片（今天）
            var expiredDate = DateTimeOffset.UtcNow.AddDays(-10).ToString("yyyyMMdd");
            var expiredDir = Path.Combine(traceDir, expiredDate);
            Directory.CreateDirectory(expiredDir);
            await File.WriteAllTextAsync(
                Path.Combine(expiredDir, "retrieval-traces.jsonl"),
                serializer.Serialize(new ContextRetrievalTrace
                {
                    RetrievalId = "expired", WorkspaceId = WorkspaceId, CollectionId = CollectionId,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-10)
                }));

            // SaveAsync 触发 MaybePurge（首次调用 _lastPurgeTicks=0，立即执行）
            await store.SaveAsync(new ContextRetrievalTrace
            {
                RetrievalId = "current", WorkspaceId = WorkspaceId, CollectionId = CollectionId,
                QueryText = "current", CreatedAt = DateTimeOffset.UtcNow
            });

            Assert.IsFalse(
                Directory.Exists(expiredDir),
                $"过期分片 {expiredDate} 应被 retention 清理删除");

            // 今天的分片应保留
            var todayDir = Path.Combine(traceDir, DateTimeOffset.UtcNow.ToString("yyyyMMdd"));
            Assert.IsTrue(Directory.Exists(todayDir), "当前分片不应被清理");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task Retention_DisabledWhenRetentionDaysZero()
    {
        var root = CreateTempRoot();
        try
        {
            var options = new FileStorageOptions { RootPath = root, TraceRetentionDays = 0 };
            var paths = new FilePathResolver(options);
            var serializer = new FileFormatSerializer();
            var store = new FileRetrievalTraceStore(options);

            var traceDir = paths.GetRetrievalTraceDirectory(WorkspaceId, CollectionId);

            // 创建过期分片
            var expiredDate = DateTimeOffset.UtcNow.AddDays(-100).ToString("yyyyMMdd");
            var expiredDir = Path.Combine(traceDir, expiredDate);
            Directory.CreateDirectory(expiredDir);
            await File.WriteAllTextAsync(
                Path.Combine(expiredDir, "retrieval-traces.jsonl"),
                "{}");

            await store.SaveAsync(new ContextRetrievalTrace
            {
                RetrievalId = "current", WorkspaceId = WorkspaceId, CollectionId = CollectionId,
                CreatedAt = DateTimeOffset.UtcNow
            });

            Assert.IsTrue(
                Directory.Exists(expiredDir),
                "TraceRetentionDays=0 时不应清理任何分片");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task Retention_PreservesNonDateShardDirectories()
    {
        var root = CreateTempRoot();
        try
        {
            var options = new FileStorageOptions { RootPath = root, TraceRetentionDays = 1 };
            var paths = new FilePathResolver(options);
            var serializer = new FileFormatSerializer();
            var store = new FileRetrievalTraceStore(options);

            var traceDir = paths.GetRetrievalTraceDirectory(WorkspaceId, CollectionId);

            // 创建非 yyyyMMdd 格式的目录（不应被清理）
            var nonDateDir = Path.Combine(traceDir, "manual-export");
            Directory.CreateDirectory(nonDateDir);

            // 创建过期日期分片
            var expiredDate = DateTimeOffset.UtcNow.AddDays(-5).ToString("yyyyMMdd");
            var expiredDir = Path.Combine(traceDir, expiredDate);
            Directory.CreateDirectory(expiredDir);

            await store.SaveAsync(new ContextRetrievalTrace
            {
                RetrievalId = "current", WorkspaceId = WorkspaceId, CollectionId = CollectionId,
                CreatedAt = DateTimeOffset.UtcNow
            });

            Assert.IsFalse(Directory.Exists(expiredDir), "过期日期分片应被清理");
            Assert.IsTrue(Directory.Exists(nonDateDir), "非日期格式目录不应被清理");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-trace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* 测试清理失败忽略 */ }
        }
    }
}
