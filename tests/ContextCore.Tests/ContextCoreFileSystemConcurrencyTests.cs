using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Storage.FileSystem;

namespace ContextCore.Tests;

/// <summary>覆盖文件系统存储的读写分离、写锁和 JSONL 并发写入行为。</summary>
[TestClass]
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

    public sealed class JsonLineTestRecord
    {
        public string Id { get; init; } = string.Empty;

        public int Value { get; init; }
    }
}


