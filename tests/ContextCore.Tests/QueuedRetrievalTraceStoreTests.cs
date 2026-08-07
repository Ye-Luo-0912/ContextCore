using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

/// <summary>
/// 检索 trace 写队列化测试：SaveAsync 入队即返回（热路径不阻塞）、
/// 后台 drain 持久化到内层、队列满丢弃不背压、查询透传、Dispose 排空。
/// </summary>
[TestClass]
[TestCategory("Retrieval")]
public sealed class QueuedRetrievalTraceStoreTests
{
    private const string Ws = "ws-trace";
    private const string Col = "col-trace";

    private static ContextRetrievalTrace BuildTrace(string retrievalId) => new()
    {
        RetrievalId = retrievalId,
        WorkspaceId = Ws,
        CollectionId = Col,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [TestMethod]
    public async Task Save_ReturnsImmediately_AndDrainsToInner()
    {
        var inner = new InMemoryRetrievalTraceStore();
        using var queued = new QueuedRetrievalTraceStore(inner);

        // 入队即返回（不等待持久化）。
        await queued.SaveAsync(BuildTrace("r1"));
        await queued.SaveAsync(BuildTrace("r2"));

        // 后台 drain 异步落地：轮询等待内层可见。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var seen = await inner.QueryRecentAsync(Ws, Col, 10);
            if (seen.Count == 2)
            {
                break;
            }
            await Task.Delay(20);
        }

        var traces = await inner.QueryRecentAsync(Ws, Col, 10);
        Assert.AreEqual(2, traces.Count, "入队的 trace 应由后台 drain 持久化到内层。");
    }

    [TestMethod]
    public async Task QueueFull_DropsTraces_WithoutBlockingSave()
    {
        var inner = new InMemoryRetrievalTraceStore();
        using var queued = new QueuedRetrievalTraceStore(inner, capacity: 16);

        // 批量入队远超容量：DropWrite 模式不阻塞、不抛异常（可丢弃部分）。
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            await queued.SaveAsync(BuildTrace("r" + i));
        }
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            "队列满时 SaveAsync 不应背压阻塞检索热路径（当前耗时 {0}）。", stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task QueryRecent_DelegatesToInner()
    {
        var inner = new InMemoryRetrievalTraceStore();
        await inner.SaveAsync(BuildTrace("pre-seeded"));

        using var queued = new QueuedRetrievalTraceStore(inner);

        var traces = await queued.QueryRecentAsync(Ws, Col, 10);
        Assert.AreEqual(1, traces.Count, "查询应透传内层既有数据。");
        Assert.AreEqual("pre-seeded", traces[0].RetrievalId);
    }

    [TestMethod]
    public void Dispose_DrainsPendingTraces()
    {
        var inner = new InMemoryRetrievalTraceStore();
        var queued = new QueuedRetrievalTraceStore(inner);

        queued.SaveAsync(BuildTrace("r-final")).GetAwaiter().GetResult();
        queued.Dispose(); // 关闭触发排空

        var traces = inner.QueryRecentAsync(Ws, Col, 10).GetAwaiter().GetResult();
        Assert.AreEqual(1, traces.Count, "Dispose 应尽力排空未落库的 trace。");
    }
}
