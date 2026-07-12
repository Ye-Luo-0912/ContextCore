using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Infrastructure;

namespace ContextCore.Tests;

/// <summary>
/// P5-0.4: AsyncTraceWriter 和异步 trace store decorator 单元测试。
/// 验证 bounded channel 异步写入、丢弃指标、同步模式、dispose drain。
/// </summary>
[TestClass]
[TestCategory("Infrastructure")]
public sealed class AsyncTraceWriterTests
{
    [TestMethod]
    public async Task AsyncMode_SaveAsync_DoesNotBlock_WritesInBackground()
    {
        var written = new List<ContextRetrievalTrace>();
        var writeComplete = new TaskCompletionSource<bool>();
        var slowStore = new SlowFakeRetrievalTraceStore(writeComplete);

        var writer = new AsyncTraceWriter<ContextRetrievalTrace>(
            (trace, ct) => slowStore.SaveAsync(trace, ct),
            new TraceWriterOptions { EnableAsyncWrite = true, ChannelCapacity = 10 });

        try
        {
            var trace = CreateTrace("trace-1");
            await writer.SaveAsync(trace);

            // SaveAsync 应立即返回，不等底层写入完成
            Assert.AreEqual(0, writer.WrittenCount);

            // 允许后台写入完成
            writeComplete.SetResult(true);
            await Task.Delay(100); // 等待后台 drain

            Assert.AreEqual(1, writer.WrittenCount);
        }
        finally
        {
            writeComplete.TrySetResult(true);
            await writer.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AsyncMode_ChannelFull_DropsAndCounts()
    {
        var blockWrite = new TaskCompletionSource<bool>();
        var slowStore = new SlowFakeRetrievalTraceStore(blockWrite);

        var writer = new AsyncTraceWriter<ContextRetrievalTrace>(
            (trace, ct) => slowStore.SaveAsync(trace, ct),
            new TraceWriterOptions { EnableAsyncWrite = true, ChannelCapacity = 3 });

        try
        {
            // 提交 10 条 trace，底层被阻塞，队列容量只有 3
            for (var i = 0; i < 10; i++)
            {
                await writer.SaveAsync(CreateTrace($"trace-{i}"));
            }

            // 队列满后丢弃的 trace 应被计数（至少 6 条被丢弃：10 - 3 容量 - 1 可能已消费）
            Assert.IsTrue(writer.DroppedCount >= 6);
        }
        finally
        {
            // 必须先释放阻塞，否则 DisposeAsync 会死锁
            blockWrite.SetResult(true);
            await Task.Delay(200);
            await writer.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SyncMode_SaveAsync_AwaitsDirectly()
    {
        var written = new List<ContextRetrievalTrace>();
        var fakeStore = new FakeRetrievalTraceStore(written);

        await using var writer = new AsyncTraceWriter<ContextRetrievalTrace>(
            (trace, ct) => fakeStore.SaveAsync(trace, ct),
            new TraceWriterOptions { EnableAsyncWrite = false });

        var trace = CreateTrace("trace-sync");
        await writer.SaveAsync(trace);

        // 同步模式：SaveAsync 返回时已写入
        Assert.AreEqual(1, writer.WrittenCount);
        Assert.AreEqual(1, written.Count);
        Assert.AreEqual("trace-sync", written[0].RetrievalId);
    }

    [TestMethod]
    public async Task DisposeAsync_DrainsPendingWrites()
    {
        var written = new List<ContextRetrievalTrace>();
        var fakeStore = new FakeRetrievalTraceStore(written);

        var writer = new AsyncTraceWriter<ContextRetrievalTrace>(
            (trace, ct) => fakeStore.SaveAsync(trace, ct),
            new TraceWriterOptions { EnableAsyncWrite = true, ChannelCapacity = 100 });

        // 提交 5 条 trace
        for (var i = 0; i < 5; i++)
        {
            await writer.SaveAsync(CreateTrace($"trace-drain-{i}"));
        }

        // Dispose 应 drain 队列
        await writer.DisposeAsync();
        await Task.Delay(100);

        Assert.AreEqual(5, writer.WrittenCount);
        Assert.AreEqual(5, written.Count);
    }

    [TestMethod]
    public async Task AsyncRetrievalTraceStore_DecoratesSaveAsync()
    {
        var written = new List<ContextRetrievalTrace>();
        var inner = new FakeRetrievalTraceStore(written);

        await using var decorator = new AsyncRetrievalTraceStore(inner,
            new TraceWriterOptions { EnableAsyncWrite = true, ChannelCapacity = 10 });

        var trace = CreateTrace("trace-decorator");
        await decorator.SaveAsync(trace);

        await Task.Delay(100); // 等待后台写入

        Assert.AreEqual(1, written.Count);
        Assert.AreEqual("trace-decorator", written[0].RetrievalId);
        Assert.AreEqual(1, decorator.WrittenCount);
    }

    [TestMethod]
    public async Task AsyncRetrievalTraceStore_QueryRecent_DelegatesToInner()
    {
        var inner = new FakeRetrievalTraceStore(new List<ContextRetrievalTrace>());
        await inner.SaveAsync(CreateTrace("trace-query"));

        await using var decorator = new AsyncRetrievalTraceStore(inner);
        var results = await decorator.QueryRecentAsync("ws-1", "col-1", 10);

        Assert.AreEqual(1, results.Count);
    }

    [TestMethod]
    public async Task AsyncDecisionTraceStore_DecoratesSaveAsync()
    {
        var written = new List<ContextDecisionRecord>();
        var inner = new FakeDecisionTraceStore(written);

        await using var decorator = new AsyncDecisionTraceStore(inner,
            new TraceWriterOptions { EnableAsyncWrite = true, ChannelCapacity = 10 });

        var record = new ContextDecisionRecord
        {
            DecisionId = "decision-1",
            Source = ContextDecisionSource.Package,
            WorkspaceId = "ws-1",
            CollectionId = "col-1"
        };
        await decorator.SaveAsync(record);

        await Task.Delay(100);

        Assert.AreEqual(1, written.Count);
        Assert.AreEqual("decision-1", written[0].DecisionId);
    }

    [TestMethod]
    public async Task AsyncContextPackageBuildTraceStore_DecoratesSaveAsync()
    {
        var written = new List<ContextPackageBuildResult>();
        var inner = new FakePackageBuildTraceStore(written);

        await using var decorator = new AsyncContextPackageBuildTraceStore(inner,
            new TraceWriterOptions { EnableAsyncWrite = true, ChannelCapacity = 10 });

        var result = new ContextPackageBuildResult
        {
            BuildId = "pkg-1"
        };
        await decorator.SaveAsync(result);

        await Task.Delay(100);

        Assert.AreEqual(1, written.Count);
        Assert.AreEqual("pkg-1", written[0].BuildId);
    }

    private static ContextRetrievalTrace CreateTrace(string id) => new()
    {
        RetrievalId = id,
        WorkspaceId = "ws-1",
        CollectionId = "col-1",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class FakeRetrievalTraceStore : IRetrievalTraceStore
    {
        private readonly List<ContextRetrievalTrace> _written;

        public FakeRetrievalTraceStore(List<ContextRetrievalTrace> written) => _written = written;

        public Task SaveAsync(ContextRetrievalTrace trace, CancellationToken cancellationToken = default)
        {
            _written.Add(trace);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(
            string workspaceId, string collectionId, int take,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ContextRetrievalTrace>>(_written);
        }
    }

    private sealed class SlowFakeRetrievalTraceStore : IRetrievalTraceStore
    {
        private readonly TaskCompletionSource<bool> _block;

        public SlowFakeRetrievalTraceStore(TaskCompletionSource<bool> block) => _block = block;

        public async Task SaveAsync(ContextRetrievalTrace trace, CancellationToken cancellationToken = default)
        {
            await _block.Task.ConfigureAwait(false);
        }

        public Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(
            string workspaceId, string collectionId, int take,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ContextRetrievalTrace>>(Array.Empty<ContextRetrievalTrace>());
        }
    }

    private sealed class FakeDecisionTraceStore : IDecisionTraceStore
    {
        private readonly List<ContextDecisionRecord> _written;

        public FakeDecisionTraceStore(List<ContextDecisionRecord> written) => _written = written;

        public Task SaveAsync(ContextDecisionRecord record, CancellationToken cancellationToken = default)
        {
            _written.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
            string workspaceId, string collectionId, int take,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ContextDecisionRecord>>(_written);
        }
    }

    private sealed class FakePackageBuildTraceStore : IContextPackageBuildTraceStore
    {
        private readonly List<ContextPackageBuildResult> _written;

        public FakePackageBuildTraceStore(List<ContextPackageBuildResult> written) => _written = written;

        public Task SaveAsync(ContextPackageBuildResult result, CancellationToken cancellationToken = default)
        {
            _written.Add(result);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ContextPackageBuildResult>> QueryRecentAsync(
            string workspaceId, string collectionId, int take,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ContextPackageBuildResult>>(_written);
        }
    }
}
