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

    // ── FS-04: 并发 Ack/Nack 单文件原子更新 ────────────────────────────────

    [TestMethod]
    public async Task FileContextJobQueue_ConcurrentAckAndNack_ShouldPreserveAllStateTransitions()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            var queue = new FileContextJobQueue(options);

            // 入队 12 个作业到同一文件（同 workspace/collection）
            for (var i = 0; i < 12; i++)
            {
                await queue.EnqueueAsync(new ContextJob
                {
                    JobId = $"job-{i:000}",
                    WorkspaceId = "ws-ack",
                    CollectionId = "col-ack",
                    Kind = ContextJobKind.Custom,
                    PayloadJson = "{}",
                    MaxRetryCount = 3
                });
            }

            // Dequeue 全部 12 个（状态变为 Running）
            var dequeued = new List<ContextJob>();
            for (var i = 0; i < 12; i++)
            {
                var job = await queue.DequeueAsync();
                Assert.IsNotNull(job);
                dequeued.Add(job!);
            }

            // 并发 Ack 前 6 个 + Nack 后 6 个：验证单文件原子更新不互相覆盖
            var ackTasks = dequeued.Take(6).Select(j => queue.AckAsync(j.JobId));
            var nackTasks = dequeued.Skip(6).Select(j => queue.NackAsync(j.JobId, "并发重试"));
            await Task.WhenAll(ackTasks.Concat(nackTasks));

            // 查询所有作业，验证状态转换全部保留
            var allJobs = await queue.QueryAsync(new ContextJobQuery { Take = 100 });
            Assert.AreEqual(12, allJobs.Count, "所有 12 个作业都应保留，不应被并发更新覆盖丢失");

            var succeeded = allJobs.Where(j => j.State == ContextJobState.Succeeded).ToArray();
            var waitingRetry = allJobs.Where(j => j.State == ContextJobState.WaitingRetry).ToArray();
            Assert.AreEqual(6, succeeded.Length, "前 6 个 Ack 的作业应全部为 Succeeded");
            Assert.AreEqual(6, waitingRetry.Length, "后 6 个 Nack 的作业应全部为 WaitingRetry");
            Assert.IsTrue(waitingRetry.All(j => j.RetryCount == 1), "Nack 作业的 RetryCount 应为 1");
            Assert.IsTrue(waitingRetry.All(j => j.ErrorMessage == "并发重试"), "Nack 作业的 ErrorMessage 应保留");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task FileContextJobQueue_AckOnNonExistentJob_ShouldBeNoOp()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            var queue = new FileContextJobQueue(options);

            // 对不存在的 jobId 执行 Ack/Nack：应静默返回，不抛异常，不创建文件
            await queue.AckAsync("non-existent-job");
            await queue.NackAsync("non-existent-job", "test");

            var allJobs = await queue.QueryAsync(new ContextJobQuery { Take = 100 });
            Assert.AreEqual(0, allJobs.Count, "不存在的 job Ack/Nack 不应产生任何副作用");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    /// <summary>
    /// #6: FileContextJobQueue Ack/Nack 原子化（CAS）— 仅当 job 处于 Running 时才转换状态。
    /// 验证文件系统队列下过期 Ack/Nack 不还原终态、不增加 RetryCount。
    /// </summary>
    [TestMethod]
    public async Task FileContextJobQueue_AckNack_Cas_OnlyTransitionsFromRunning()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            var queue = new FileContextJobQueue(options);
            var queryStore = (IContextJobQueryStore)queue;

            await queue.EnqueueAsync(new ContextJob
            {
                JobId = "fs-job-cas",
                WorkspaceId = "ws",
                CollectionId = "col",
                Kind = ContextJobKind.Custom,
                PayloadJson = "{}",
                MaxRetryCount = 3,
                CreatedAt = DateTimeOffset.UtcNow
            });

            // Ack on Queued (not Running) → no-op
            await queue.AckAsync("fs-job-cas");
            var jobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
            Assert.AreEqual(ContextJobState.Queued, jobs.Single(j => j.JobId == "fs-job-cas").State,
                "Ack on Queued job 应为 no-op");

            // Nack on Queued (not Running) → no-op, RetryCount 不变
            await queue.NackAsync("fs-job-cas", "stale nack");
            jobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
            Assert.AreEqual(ContextJobState.Queued, jobs.Single(j => j.JobId == "fs-job-cas").State,
                "Nack on Queued job 应为 no-op");
            Assert.AreEqual(0, jobs.Single(j => j.JobId == "fs-job-cas").RetryCount,
                "Nack on Queued job 不应增加 RetryCount");

            // Dequeue → Running → Ack → Succeeded
            var dequeued = await queue.DequeueAsync();
            Assert.AreEqual(ContextJobState.Running, dequeued!.State);
            await queue.AckAsync("fs-job-cas");
            jobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
            Assert.AreEqual(ContextJobState.Succeeded, jobs.Single(j => j.JobId == "fs-job-cas").State);

            // Stale Nack on Succeeded (not Running) → no-op, 不还原终态
            await queue.NackAsync("fs-job-cas", "stale nack after success");
            jobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
            Assert.AreEqual(ContextJobState.Succeeded, jobs.Single(j => j.JobId == "fs-job-cas").State,
                "Nack on Succeeded job 应为 no-op，不应还原终态");
            Assert.AreEqual(0, jobs.Single(j => j.JobId == "fs-job-cas").RetryCount,
                "Nack on Succeeded job 不应增加 RetryCount");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    // ── R13.1 #5：JobId → 文件路径索引 ────────────────────────────────

    /// <summary>
    /// #5：Enqueue 应将 jobId→路径写入进程内索引，后续 Ack 命中索引跳过扫描。
    /// 验证索引条目数在 Enqueue 后正确增长，且 Ack 后保持稳定（Ack 不新增索引条目）。
    /// </summary>
    [TestMethod]
    public async Task FileContextJobQueue_Enqueue_PopulatesJobPathIndex()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            var queue = new FileContextJobQueue(options);

            Assert.AreEqual(0, queue.JobPathIndexCount, "初始索引应为空");

            await queue.EnqueueAsync(new ContextJob
            {
                JobId = "idx-job-1",
                WorkspaceId = "ws-idx",
                CollectionId = "col-idx",
                Kind = ContextJobKind.Custom,
                PayloadJson = "{}",
                MaxRetryCount = 3,
                CreatedAt = DateTimeOffset.UtcNow
            });
            Assert.AreEqual(1, queue.JobPathIndexCount, "Enqueue 后索引应有 1 条");

            await queue.EnqueueAsync(new ContextJob
            {
                JobId = "idx-job-2",
                WorkspaceId = "ws-idx",
                CollectionId = "col-idx",
                Kind = ContextJobKind.Custom,
                PayloadJson = "{}",
                MaxRetryCount = 3,
                CreatedAt = DateTimeOffset.UtcNow
            });
            Assert.AreEqual(2, queue.JobPathIndexCount, "第二个 Enqueue 后索引应有 2 条");

            // Dequeue → Running → Ack：Ack 应命中索引（不新增条目），状态正确转换。
            var dequeued = await queue.DequeueAsync();
            Assert.IsNotNull(dequeued);
            await queue.AckAsync(dequeued!.JobId);
            Assert.AreEqual(2, queue.JobPathIndexCount, "Ack 走索引定位，不应新增索引条目");

            var queryStore = (IContextJobQueryStore)queue;
            var jobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
            Assert.AreEqual(ContextJobState.Succeeded, jobs.Single(j => j.JobId == dequeued.JobId).State,
                "索引命中后 Ack 应正确转换状态");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    /// <summary>
    /// #5：跨 workspace/collection 的多个 jobs.jsonl 文件下，Ack 应通过索引或扫描
    /// 正确解析目标 job 所在的文件，不误操作其他文件的 job。
    /// </summary>
    [TestMethod]
    public async Task FileContextJobQueue_AckAcrossMultipleWorkspaceFiles_ResolvesCorrectFile()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            var queue = new FileContextJobQueue(options);

            // 两个 workspace/collection 各入队一个 job（落在不同 jobs.jsonl）
            await queue.EnqueueAsync(new ContextJob
            {
                JobId = "multi-ws-a",
                WorkspaceId = "ws-a", CollectionId = "col-a",
                Kind = ContextJobKind.Custom, PayloadJson = "{}",
                MaxRetryCount = 3, CreatedAt = DateTimeOffset.UtcNow
            });
            await queue.EnqueueAsync(new ContextJob
            {
                JobId = "multi-ws-b",
                WorkspaceId = "ws-b", CollectionId = "col-b",
                Kind = ContextJobKind.Custom, PayloadJson = "{}",
                MaxRetryCount = 3, CreatedAt = DateTimeOffset.UtcNow
            });
            Assert.AreEqual(2, queue.JobPathIndexCount, "两个 Enqueue 应记录 2 条索引");

            // Dequeue 出第一个可运行 job，Ack 它，再 Dequeue+Ack 第二个
            var first = await queue.DequeueAsync();
            Assert.IsNotNull(first);
            await queue.AckAsync(first!.JobId);

            var second = await queue.DequeueAsync();
            Assert.IsNotNull(second);
            await queue.AckAsync(second!.JobId);

            // 两个 job 都应进入 Succeeded，且不互相干扰
            var queryStore = (IContextJobQueryStore)queue;
            var jobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
            Assert.AreEqual(ContextJobState.Succeeded, jobs.Single(j => j.JobId == "multi-ws-a").State);
            Assert.AreEqual(ContextJobState.Succeeded, jobs.Single(j => j.JobId == "multi-ws-b").State);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    /// <summary>
    /// #5：对未经本队列 Enqueue 直接写入文件的外部 job，Ack 应回退到扫描定位，
    /// 定位后回填索引，后续 Ack 命中索引。验证扫描回退与回填闭环。
    /// </summary>
    [TestMethod]
    public async Task FileContextJobQueue_AckOnExternalJob_FallsBackToScanAndBackfillsIndex()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            var queue = new FileContextJobQueue(options);
            var paths = new FilePathResolver(options);
            var serializer = new FileFormatSerializer();

            // 直接写一个 jobs.jsonl，绕过 Enqueue（模拟他进程写入）
            var externalPath = Path.Combine(paths.GetCollectionDirectory("ws-ext", "col-ext"), "jobs", "jobs.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(externalPath)!);
            var externalJob = new ContextJob
            {
                JobId = "ext-job",
                WorkspaceId = "ws-ext", CollectionId = "col-ext",
                Kind = ContextJobKind.Custom, PayloadJson = "{}",
                State = ContextJobState.Running, // 已 Running，Ack 才会生效
                MaxRetryCount = 3,
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow
            };
            await File.WriteAllTextAsync(externalPath, serializer.Serialize(externalJob) + "\n");

            Assert.AreEqual(0, queue.JobPathIndexCount, "外部写入不应进入本队列索引");

            // Ack 应通过扫描找到外部 job 并转换状态
            await queue.AckAsync("ext-job");
            Assert.AreEqual(1, queue.JobPathIndexCount, "扫描命中后应回填索引");

            var queryStore = (IContextJobQueryStore)queue;
            var jobs = await queryStore.QueryAsync(new ContextJobQuery { Take = 10 });
            Assert.AreEqual(ContextJobState.Succeeded, jobs.Single(j => j.JobId == "ext-job").State,
                "扫描回退应正确定位并 Ack 外部 job");
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


