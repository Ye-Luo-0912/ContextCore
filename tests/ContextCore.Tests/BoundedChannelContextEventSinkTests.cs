using System.Diagnostics.Metrics;
using ContextCore.Abstractions;
using ContextCore.Core;
using ContextCore.Storage.FileSystem;

namespace ContextCore.Tests;

/// <summary>
/// R13.4 #1：覆盖 BoundedChannelContextEventSink 与 FileContextEventSink.EmitBatchAsync 的批量写入、
/// 有界通道背压、BestEffort fail-open、Required fail-closed、graceful drain 行为。
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class BoundedChannelContextEventSinkTests
{
    /// <summary>
    /// FileContextEventSink.EmitBatchAsync 应在单次写锁内追加多行，
    /// 输出文件包含全部事件且无重复。
    /// </summary>
    [TestMethod]
    public async Task FileContextEventSink_EmitBatchAsync_WritesAllEventsInSingleLock()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "cc-r1341-filebatch-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(rootPath);
            var sink = new FileContextEventSink(Path.Combine(rootPath, "logs"));

            var events = Enumerable.Range(0, 5)
                .Select(i => CreateEvent($"ws-batch", $"evt-{i}"))
                .ToArray();

            await sink.EmitBatchAsync(events);

            var date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var logPath = Path.Combine(rootPath, "logs", "ws-batch", $"events-{date}.jsonl");
            Assert.IsTrue(File.Exists(logPath));

            var lines = await File.ReadAllLinesAsync(logPath);
            Assert.AreEqual(5, lines.Length);

            // 同一工作空间的事件应位于同一文件
            foreach (var line in lines)
            {
                StringAssert.Contains(line, "\"WorkspaceId\":\"ws-batch\"");
            }
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// FileContextEventSink.EmitBatchAsync 按工作空间分组后落入不同文件，
    /// 每个文件只包含对应工作空间的事件。
    /// </summary>
    [TestMethod]
    public async Task FileContextEventSink_EmitBatchAsync_GroupsByWorkspace()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "cc-r1341-group-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(rootPath);
            var sink = new FileContextEventSink(Path.Combine(rootPath, "logs"));

            var events = new[]
            {
                CreateEvent("ws-a", "evt-a-1"),
                CreateEvent("ws-b", "evt-b-1"),
                CreateEvent("ws-a", "evt-a-2"),
                CreateEvent("ws-b", "evt-b-2"),
                CreateEvent("ws-a", "evt-a-3"),
            };

            await sink.EmitBatchAsync(events);

            var date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var pathA = Path.Combine(rootPath, "logs", "ws-a", $"events-{date}.jsonl");
            var pathB = Path.Combine(rootPath, "logs", "ws-b", $"events-{date}.jsonl");

            Assert.IsTrue(File.Exists(pathA));
            Assert.IsTrue(File.Exists(pathB));
            Assert.AreEqual(3, (await File.ReadAllLinesAsync(pathA)).Length);
            Assert.AreEqual(2, (await File.ReadAllLinesAsync(pathB)).Length);
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// FileContextEventSink.EmitBatchAsync 对空列表应直接返回。
    /// </summary>
    [TestMethod]
    public async Task FileContextEventSink_EmitBatchAsync_EmptyListIsNoOp()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "cc-r1341-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(rootPath);
            var sink = new FileContextEventSink(Path.Combine(rootPath, "logs"));

            await sink.EmitBatchAsync(Array.Empty<ContextOperationEvent>());

            // 不应创建任何日志目录
            var logsDir = Path.Combine(rootPath, "logs");
            Assert.IsFalse(Directory.Exists(logsDir) && Directory.EnumerateFiles(logsDir, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// 默认接口实现 EmitBatchAsync 应回退到逐条调用 EmitAsync。
    /// NullContextEventSink / InMemoryContextEventSink 走默认路径不应抛出。
    /// </summary>
    [TestMethod]
    public async Task IContextEventSink_DefaultEmitBatchAsync_LoopsEmitAsync()
    {
        IContextEventSink sink = new InMemoryContextEventSink();
        var events = Enumerable.Range(0, 3)
            .Select(i => CreateEvent("ws-default", $"evt-{i}"))
            .ToArray();

        await sink.EmitBatchAsync(events);

        Assert.AreEqual(3, ((InMemoryContextEventSink)sink).Events.Count);
    }

    /// <summary>
    /// CompositeContextEventSink.EmitBatchAsync 应将批量事件转发到所有子 sink。
    /// </summary>
    [TestMethod]
    public async Task CompositeContextEventSink_EmitBatchAsync_ForwardsToAllSinks()
    {
        var sinkA = new InMemoryContextEventSink();
        var sinkB = new InMemoryContextEventSink();
        var composite = new CompositeContextEventSink(new[] { sinkA, sinkB });

        var events = new[]
        {
            CreateEvent("ws-composite", "evt-1"),
            CreateEvent("ws-composite", "evt-2"),
        };

        await composite.EmitBatchAsync(events);

        Assert.AreEqual(2, sinkA.Events.Count);
        Assert.AreEqual(2, sinkB.Events.Count);
    }

    /// <summary>
    /// BestEffort 路径：BoundedChannelContextEventSink 应将事件写入通道，
    /// 后台消费者按批调用 inner.EmitBatchAsync。
    /// </summary>
    [TestMethod]
    public async Task BoundedChannel_BestEffort_EmitsInBatchesToInner()
    {
        var inner = new InMemoryContextEventSink();
        await using var sink = new BoundedChannelContextEventSink(inner, capacity: 100, batchSize: 8);

        var events = Enumerable.Range(0, 16)
            .Select(i => CreateEvent("ws-channel", $"evt-{i:000}"))
            .ToArray();

        await sink.EmitBatchAsync(events);

        // 等待消费者处理完成（最多 2 秒）
        await WaitForAsync(() => inner.Events.Count == 16, TimeSpan.FromSeconds(2));

        Assert.AreEqual(16, inner.Events.Count);
        Assert.IsTrue(sink.BatchEmitCount >= 2, $"预期至少 2 批，实际 {sink.BatchEmitCount}");
        Assert.AreEqual(0, sink.DroppedCount);
        Assert.AreEqual(0, sink.ErrorCount);
    }

    /// <summary>
    /// BestEffort 路径：通道满时应丢弃新事件，不阻塞调用方。
    /// 使用阻塞 sink 让通道积压至满，再写入应触发 DroppedCount 累加。
    /// </summary>
    [TestMethod]
    public async Task BoundedChannel_BestEffort_DropsEventsWhenFull()
    {
        // 用阻塞 sink 让消费者卡住，让通道快速填满
        var inner = new SignalOnFirstCallSink();
        var sink = new BoundedChannelContextEventSink(inner, capacity: 4, batchSize: 100);
        try
        {
            // 写入第 1 条触发消费者阻塞
            await sink.EmitAsync(CreateEvent("ws-drop", "evt-0"));
            await inner.WaitForFirstCallAsync;

            // 消费者卡在第 1 批，通道最多还能装 4 条（capacity=4）
            // 因为第 1 条已被读出，通道为空；这里填满 4 条
            for (var i = 1; i <= 4; i++)
            {
                Assert.IsTrue(sink.PendingCount < 4, $"填满阶段不应丢弃，第 {i} 条 PendingCount={sink.PendingCount}");
                await sink.EmitAsync(CreateEvent("ws-drop", $"evt-{i}"));
            }

            // 通道已满，后续写入应被丢弃（TryWrite 返回 false）
            for (var i = 5; i < 10; i++)
            {
                await sink.EmitAsync(CreateEvent("ws-drop", $"evt-{i}"));
            }

            Assert.AreEqual(5, sink.DroppedCount, "后 5 条应被丢弃");
            Assert.AreEqual(4, sink.PendingCount, "通道应保持 4 条");
        }
        finally
        {
            await inner.UnblockAsync();
            await sink.DisposeAsync();
        }
    }

    /// <summary>
    /// BestEffort 路径：inner.EmitBatchAsync 抛异常时不应向上传播，仅累加 ErrorCount。
    /// </summary>
    [TestMethod]
    public async Task BoundedChannel_BestEffort_SwallowsInnerErrors()
    {
        var inner = new ThrowingSink();
        await using var sink = new BoundedChannelContextEventSink(inner, capacity: 100, batchSize: 4);

        for (var i = 0; i < 5; i++)
        {
            await sink.EmitAsync(CreateEvent("ws-throw", $"evt-{i}"));
        }

        // 等待消费者处理失败
        await WaitForAsync(() => sink.ErrorCount > 0, TimeSpan.FromSeconds(2));

        Assert.IsTrue(sink.ErrorCount >= 1, $"预期至少 1 次错误，实际 {sink.ErrorCount}");
        Assert.AreEqual(0, sink.DroppedCount);
    }

    /// <summary>
    /// Required 路径：装饰器应绕过通道，直接同步调用 inner.EmitAsync。
    /// </summary>
    [TestMethod]
    public async Task BoundedChannel_Required_BypassesChannelAndCallsSynchronously()
    {
        var inner = new RequiredInMemorySink();
        await using var sink = new BoundedChannelContextEventSink(inner, capacity: 100, batchSize: 8);

        await sink.EmitAsync(CreateEvent("ws-required", "evt-sync"));

        Assert.AreEqual(ContextEventSinkKind.Required, sink.Kind);
        Assert.AreEqual(1, inner.Events.Count, "Required 路径应同步写入");
        Assert.AreEqual(0, sink.PendingCount, "不应使用通道");
        Assert.AreEqual(0, sink.BatchEmitCount);
    }

    /// <summary>
    /// Required 路径：inner.EmitAsync 抛异常时应向上传播，调用方必须感知。
    /// </summary>
    [TestMethod]
    public async Task BoundedChannel_Required_PropagatesErrors()
    {
        var inner = new RequiredThrowingSink();
        await using var sink = new BoundedChannelContextEventSink(inner, capacity: 100, batchSize: 8);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await sink.EmitAsync(CreateEvent("ws-required-throw", "evt")));
    }

    /// <summary>
    /// BestEffort 路径：DisposeAsync 应等待消费者 drain 完所有残留事件。
    /// 使用阻塞 sink 让事件停留在通道中，再释放并 Dispose 验证 flush。
    /// </summary>
    [TestMethod]
    public async Task BoundedChannel_DisposeAsync_DrainsPendingEvents()
    {
        var inner = new SignalOnFirstCallSink();
        // 用大 batchSize 让事件停留在通道中等待凑批
        var sink = new BoundedChannelContextEventSink(inner, capacity: 100, batchSize: 1000);
        try
        {
            // 写入 1 条事件触发消费者阻塞
            await sink.EmitAsync(CreateEvent("ws-drain", "evt-0"));
            await inner.WaitForFirstCallAsync;

            // 消费者阻塞中，写入 4 条事件进入通道
            for (var i = 1; i < 5; i++)
            {
                await sink.EmitAsync(CreateEvent("ws-drain", $"evt-{i}"));
            }

            Assert.AreEqual(4, sink.PendingCount, "预期 4 个待处理事件在通道中");

            // 释放消费者：消费者处理完第一批 (1 event)，然后读取后续 4 个事件
            // DisposeAsync 会触发 TryComplete，让消费者 drain 完所有残留事件后退出
            await inner.UnblockAsync();
            await sink.DisposeAsync();

            Assert.AreEqual(0, sink.PendingCount, "Dispose 后通道应清空");
        }
        finally
        {
            // 防止测试中途失败时 consumer task 泄漏
            await inner.UnblockAsync();
            await sink.DisposeAsync();
        }
    }

    /// <summary>
    /// 复合场景：Composite 包含 BestEffort + Required 子 sink，
    /// 外层 BoundedChannelContextEventSink 装饰时 Composite 整体走通道（因 Kind == BestEffort）。
    /// Composite.EmitBatchAsync 内部按子 sink 的 Kind 分别处理，确保 Required 子 sink 仍接收全部事件。
    /// </summary>
    [TestMethod]
    public async Task BoundedChannel_WrappingComposite_StillRespectsInnerRequiredSemantics()
    {
        var bestEffortInner = new InMemoryContextEventSink();
        var requiredInner = new RequiredInMemorySink();
        var composite = new CompositeContextEventSink(new IContextEventSink[] { bestEffortInner, requiredInner });

        // Composite.Kind == BestEffort，故外层装饰器对整个 Composite 走通道
        await using var sink = new BoundedChannelContextEventSink(composite, capacity: 100, batchSize: 8);

        for (var i = 0; i < 5; i++)
        {
            await sink.EmitAsync(CreateEvent("ws-composite-req", $"evt-{i}"));
        }

        // 等待两个子 sink 都收到全部事件（Composite.EmitBatchAsync 同步转发两个子 sink）
        await WaitForAsync(
            () => bestEffortInner.Events.Count == 5 && requiredInner.Events.Count == 5,
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(5, bestEffortInner.Events.Count);
        Assert.AreEqual(5, requiredInner.Events.Count);
    }

    /// <summary>
    /// R13.4 #2：验证 BoundedChannelContextEventSink 的 drop/error/batch_emit 计数
    /// 通过 CoreMetrics 的 OTel Counter 发布，可用 MeterListener 捕获。
    /// queue 深度（PendingCount）作为实例属性保留供进程内观察（与 InMemoryContextStateCache 一致）。
    /// </summary>
    [TestMethod]
    public async Task BoundedChannel_RecordsOtelCounters_ForDropErrorBatchEmit()
    {
        // 捕获 EventSink 三个计数器的累加值
        var droppedSum = 0L;
        var errorSum = 0L;
        var batchEmitSum = 0L;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name != "ContextCore.Core") return;
            if (instrument.Name is "contextcore.eventsink.dropped"
                or "contextcore.eventsink.errors"
                or "contextcore.eventsink.batch_emits")
            {
                l.EnableMeasurementEvents(instrument, state: null);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            switch (instrument.Name)
            {
                case "contextcore.eventsink.dropped": Interlocked.Add(ref droppedSum, value); break;
                case "contextcore.eventsink.errors": Interlocked.Add(ref errorSum, value); break;
                case "contextcore.eventsink.batch_emits": Interlocked.Add(ref batchEmitSum, value); break;
            }
        });
        listener.Start();

        try
        {
            // 场景 1：成功批量写入 → batch_emits 计数
            var successInner = new InMemoryContextEventSink();
            await using var successSink = new BoundedChannelContextEventSink(successInner, capacity: 100, batchSize: 8);

            for (var i = 0; i < 16; i++)
            {
                await successSink.EmitAsync(CreateEvent("ws-otel-success", $"evt-{i}"));
            }

            await WaitForAsync(() => successInner.Events.Count == 16, TimeSpan.FromSeconds(2));
            await successSink.DisposeAsync();

            // 场景 2：通道满丢弃 → dropped 计数
            var blockInner = new SignalOnFirstCallSink();
            var dropSink = new BoundedChannelContextEventSink(blockInner, capacity: 4, batchSize: 100);
            try
            {
                await dropSink.EmitAsync(CreateEvent("ws-otel-drop", "evt-0"));
                await blockInner.WaitForFirstCallAsync;

                // 填满通道（4 条）+ 3 条应被丢弃
                for (var i = 1; i <= 7; i++)
                {
                    await dropSink.EmitAsync(CreateEvent("ws-otel-drop", $"evt-{i}"));
                }

                Assert.AreEqual(3, dropSink.DroppedCount);
            }
            finally
            {
                await blockInner.UnblockAsync();
                await dropSink.DisposeAsync();
            }

            // 场景 3：批量写入失败 → errors 计数
            var throwingSink = new ThrowingSink();
            await using var errorSink = new BoundedChannelContextEventSink(throwingSink, capacity: 100, batchSize: 4);

            for (var i = 0; i < 5; i++)
            {
                await errorSink.EmitAsync(CreateEvent("ws-otel-error", $"evt-{i}"));
            }

            await WaitForAsync(() => errorSink.ErrorCount > 0, TimeSpan.FromSeconds(2));

            // 等待一小段时间确保所有计数器回调都已执行（回调在记录线程同步执行，
            // 但消费者可能在多次记录间存在调度延迟）
            await Task.Delay(100);

            // 验证 OTel 计数器捕获到对应计数
            Assert.IsTrue(batchEmitSum >= 1, $"OTel batch_emits 计数应 >= 1，实际 {batchEmitSum}");
            Assert.IsTrue(droppedSum >= 3, $"OTel dropped 计数应 >= 3，实际 {droppedSum}");
            Assert.IsTrue(errorSum >= 1, $"OTel errors 计数应 >= 1，实际 {errorSum}");
        }
        finally
        {
            listener.Dispose();
        }
    }

    private static ContextOperationEvent CreateEvent(string workspaceId, string eventId) => new()
    {
        EventId = eventId,
        OperationId = "op-" + eventId,
        OperationName = "test.operation",
        WorkspaceId = workspaceId,
        CollectionId = "col-test",
        Level = ContextEventLevel.Information,
        Message = "Event for " + eventId,
        Duration = TimeSpan.FromMilliseconds(1),
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
    }

    /// <summary>第一次调用时通知主线程、随后阻塞直到 Unblock 的 sink，用于测试通道满时丢弃。</summary>
    private sealed class SignalOnFirstCallSink : IContextEventSink
    {
        private readonly TaskCompletionSource _firstCallTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _unblockTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForFirstCallAsync => _firstCallTcs.Task;

        public Task UnblockAsync()
        {
            _unblockTcs.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task EmitAsync(ContextOperationEvent operationEvent, CancellationToken cancellationToken = default)
        {
            _firstCallTcs.TrySetResult();
            await _unblockTcs.Task.ConfigureAwait(false);
        }

        public async Task EmitBatchAsync(IReadOnlyList<ContextOperationEvent> events, CancellationToken cancellationToken = default)
        {
            _firstCallTcs.TrySetResult();
            await _unblockTcs.Task.ConfigureAwait(false);
        }
    }

    /// <summary>EmitAsync / EmitBatchAsync 总是抛异常的 BestEffort sink。</summary>
    private sealed class ThrowingSink : IContextEventSink
    {
        public Task EmitAsync(ContextOperationEvent operationEvent, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");

        public Task EmitBatchAsync(IReadOnlyList<ContextOperationEvent> events, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom-batch");
    }

    /// <summary>Kind = Required 的内存 sink，记录所有事件。</summary>
    private sealed class RequiredInMemorySink : IContextEventSink
    {
        private readonly List<ContextOperationEvent> _events = new();
        private readonly object _gate = new();

        public IReadOnlyList<ContextOperationEvent> Events
        {
            get
            {
                lock (_gate) return _events.ToArray();
            }
        }

        public ContextEventSinkKind Kind => ContextEventSinkKind.Required;

        public Task EmitAsync(ContextOperationEvent operationEvent, CancellationToken cancellationToken = default)
        {
            lock (_gate) _events.Add(operationEvent);
            return Task.CompletedTask;
        }

        public Task EmitBatchAsync(IReadOnlyList<ContextOperationEvent> events, CancellationToken cancellationToken = default)
        {
            lock (_gate) _events.AddRange(events);
            return Task.CompletedTask;
        }
    }

    /// <summary>Kind = Required 且 EmitAsync 总是抛异常的 sink。</summary>
    private sealed class RequiredThrowingSink : IContextEventSink
    {
        public ContextEventSinkKind Kind => ContextEventSinkKind.Required;

        public Task EmitAsync(ContextOperationEvent operationEvent, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("required-boom");

        public Task EmitBatchAsync(IReadOnlyList<ContextOperationEvent> events, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("required-boom-batch");
    }
}
