using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;

namespace ContextCore.Tests;

/// <summary>覆盖文件系统存储的读写分离、写锁和 JSONL 并发写入行为。</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ContextCoreFileSystemConcurrencyTests
{
    [TestMethod]
    public async Task FileJsonLineStore_ConcurrentUpsert_ShouldKeepAllRecords()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var path = Path.Combine(rootPath, "records.jsonl");
            var store = new FileJsonLineStore(new FileFormatSerializer());

            await Task.WhenAll(Enumerable.Range(0, 80).Select(index =>
                store.UpsertAsync(
                    path,
                    new JsonLineTestRecord { Id = $"item-{index:000}", Value = index },
                    item => item.Id)));

            var records = await store.ReadAsync<JsonLineTestRecord>(path);

            Assert.AreEqual(80, records.Count);
            Assert.AreEqual(80, records.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.IsFalse(Directory.EnumerateFiles(rootPath, "*.tmp.*").Any());
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task FileContextEventSink_ConcurrentEmit_ShouldWriteValidJsonLines()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var sink = new FileContextEventSink(Path.Combine(rootPath, "logs"));
            var now = DateTimeOffset.UtcNow;

            await Task.WhenAll(Enumerable.Range(0, 60).Select(index =>
                sink.EmitAsync(new ContextOperationEvent
                {
                    EventId = $"event-{index:000}",
                    OperationId = $"operation-{index:000}",
                    OperationName = "filesystem.concurrent.append",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Level = ContextEventLevel.Information,
                    Message = "并发追加日志测试。",
                    CreatedAt = now.AddMilliseconds(index)
                })));

            var logPath = Directory.EnumerateFiles(
                    Path.Combine(rootPath, "logs", "workspace-test"),
                    "events-*.jsonl")
                .Single();
            var lines = await File.ReadAllLinesAsync(logPath);

            Assert.AreEqual(60, lines.Length);
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                Assert.IsTrue(document.RootElement.TryGetProperty("eventId", out _) || document.RootElement.TryGetProperty("EventId", out _));
            }
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task FileJsonLineInspector_ShouldReportCorruptLines()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            Directory.CreateDirectory(rootPath);
            var path = Path.Combine(rootPath, "corrupt.jsonl");
            await File.WriteAllLinesAsync(path,
            [
                "{\"id\":\"ok-1\"}",
                "",
                "{not-valid-json",
                "{\"id\":\"ok-2\"}"
            ]);
            var inspector = new FileJsonLineInspector();

            var report = await inspector.InspectAsync(path);

            Assert.IsFalse(report.IsHealthy);
            Assert.AreEqual(4, report.TotalLines);
            Assert.AreEqual(2, report.ValidLines);
            Assert.AreEqual(1, report.BlankLines);
            Assert.AreEqual(1, report.CorruptLines);
            Assert.AreEqual(3, report.Issues.Single().LineNumber);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }
    private static string CreateTestRootPath()
    {
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "context-core-test-data",
            Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    // ── FS-01: 并发 Dequeue 双重 claim 防护 ────────────────────────────────

    [TestMethod]
    public async Task FileContextJobQueue_ConcurrentDequeue_ShouldNotDoubleClaim()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            var queue = new FileContextJobQueue(options);

            // 入队 20 个作业
            for (var i = 0; i < 20; i++)
            {
                await queue.EnqueueAsync(new ContextJob
                {
                    JobId = $"job-{i:000}",
                    WorkspaceId = "ws-test",
                    CollectionId = "col-test",
                    Kind = ContextJobKind.Custom,
                    PayloadJson = "{}",
                    Priority = i,
                    MaxRetryCount = 3
                });
            }

            // 多线程并发 Dequeue
            var claimedJobs = new System.Collections.Concurrent.ConcurrentBag<ContextJob>();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                while (true)
                {
                    var job = await queue.DequeueAsync();
                    if (job is null) break;
                    claimedJobs.Add(job);
                }
            })));

            // 每个作业应只被 claim 一次
            var jobIds = claimedJobs.Select(j => j.JobId).ToArray();
            Assert.AreEqual(20, jobIds.Length, "所有 20 个作业都应被 claim");
            Assert.AreEqual(20, jobIds.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "不应有作业被重复 claim");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ── FS-01: 短期事件并发追加 ────────────────────────────────────────────

    [TestMethod]
    public async Task FileShortTermMemoryStore_ConcurrentAppendRawEvents_ShouldKeepAllEvents()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            var resolver = new FilePathResolver(options);
            var store = new FileShortTermMemoryStore(resolver, new FileFormatSerializer(), new ShortTermMemoryPolicy());

            // 并发追加 50 条 raw event
            await Task.WhenAll(Enumerable.Range(0, 50).Select(index =>
                store.AppendRawEventAsync(new ShortTermRawEvent
                {
                    EventId = $"event-{index:000}",
                    WorkspaceId = "ws-test",
                    CollectionId = "col-test",
                    Source = "test",
                    EventKind = "test-event",
                    Content = $"并发事件 {index}",
                    CreatedAt = DateTimeOffset.UtcNow
                })));

            var events = await store.QueryRawEventsAsync(new ShortTermRawEventQuery
            {
                WorkspaceId = "ws-test",
                CollectionId = "col-test",
                Take = int.MaxValue
            });

            Assert.AreEqual(50, events.Count, "所有 50 条并发追加的事件都应保留");
            Assert.AreEqual(50, events.Select(e => e.EventId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "不应有事件重复");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ── FS-01: 损坏行隔离读取 ──────────────────────────────────────────────

    [TestMethod]
    public async Task FileJsonLineStore_ReadWithCorruptLines_ShouldSkipCorruptAndKeepValid()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            Directory.CreateDirectory(rootPath);
            var path = Path.Combine(rootPath, "mixed.jsonl");
            await File.WriteAllLinesAsync(path,
            [
                "{\"Id\":\"ok-1\",\"Value\":1}",
                "",
                "{not-valid-json",
                "{\"Id\":\"ok-2\",\"Value\":2}",
                "   ",
                "{\"Id\":\"ok-3\",\"Value\":3}"
            ]);

            var store = new FileJsonLineStore(new FileFormatSerializer());
            var records = await store.ReadAsync<JsonLineTestRecord>(path);

            // 损坏行和空行应被隔离跳过，只返回 3 条有效记录
            Assert.AreEqual(3, records.Count);
            CollectionAssert.AreEqual(
                new[] { "ok-1", "ok-2", "ok-3" },
                records.Select(r => r.Id).ToArray());
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ── FS-01: 崩溃恢复 — 原子写保证旧文件完整 ─────────────────────────────

    [TestMethod]
    public async Task FileSystemWriter_AtomicWrite_ShouldPreserveOriginalOnTempFailure()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var path = Path.Combine(rootPath, "atomic.jsonl");
            var writer = new FileSystemWriter();

            // 先写入初始内容
            await writer.WriteAllLinesAtomicAsync(path, ["{\"Id\":\"original\"}"]);

            // 模拟"崩溃"：写入失败时（通过让 update 回调抛异常），原文件应保持完整
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            {
                await writer.UpdateLinesAsync(path, _ => throw new InvalidOperationException("模拟崩溃"), default);
            });

            // 原文件应保持完整
            var lines = await File.ReadAllLinesAsync(path);
            Assert.AreEqual(1, lines.Length);
            Assert.IsTrue(lines[0].Contains("original"));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ── FS-01: 作业队列损坏行隔离 ──────────────────────────────────────────

    [TestMethod]
    public async Task FileContextJobQueue_DequeueWithCorruptLine_ShouldSkipCorruptAndProcessValidJobs()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            var queue = new FileContextJobQueue(options);

            // 正常入队 2 个作业
            await queue.EnqueueAsync(new ContextJob
            {
                JobId = "job-valid-1",
                WorkspaceId = "ws-test",
                CollectionId = "col-test",
                Kind = ContextJobKind.Custom,
                PayloadJson = "{}",
                MaxRetryCount = 3
            });
            await queue.EnqueueAsync(new ContextJob
            {
                JobId = "job-valid-2",
                WorkspaceId = "ws-test",
                CollectionId = "col-test",
                Kind = ContextJobKind.Custom,
                PayloadJson = "{}",
                MaxRetryCount = 3
            });

            // 手工在 jobs.jsonl 中插入损坏行
            var jobsPath = Path.Combine(rootPath, "workspaces", "ws-test", "collections", "col-test", "jobs", "jobs.jsonl");
            var existingLines = await File.ReadAllLinesAsync(jobsPath);
            var tamperedLines = new List<string> { existingLines[0], "{corrupt-line", existingLines[1] };
            await File.WriteAllLinesAsync(jobsPath, tamperedLines);

            // Dequeue 应跳过损坏行，仍能 claim 有效作业
            var job1 = await queue.DequeueAsync();
            var job2 = await queue.DequeueAsync();
            var job3 = await queue.DequeueAsync();

            Assert.IsNotNull(job1, "应能 claim 第一个有效作业");
            Assert.IsNotNull(job2, "应能 claim 第二个有效作业");
            Assert.IsNull(job3, "没有更多有效作业时应返回 null");
            Assert.IsTrue(job1!.JobId is "job-valid-1" or "job-valid-2");
            Assert.IsTrue(job2!.JobId is "job-valid-1" or "job-valid-2");
            Assert.AreNotEqual(job1.JobId, job2.JobId, "两个 claim 不应是同一作业");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    public sealed class JsonLineTestRecord
    {
        public string Id { get; init; } = string.Empty;

        public int Value { get; init; }
    }
}


