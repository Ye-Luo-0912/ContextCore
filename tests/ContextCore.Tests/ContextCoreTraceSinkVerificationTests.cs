using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.Learning.V14_0;

namespace ContextCore.Tests;

/// <summary>
/// Runtime Candidate Trace Sink 验证测试。
///
/// 验证范围：
/// 1. FileRuntimeCandidateTraceSink 现有行为（write count / drop on null writer / write failures / flush）
/// 2. BoundedRuntimeCandidateTraceSink 有界队列能力（bounded capacity / batch append / shutdown drain / saturation detection）
/// 3. FileRuntimeCandidateTraceSink writer recreation（构造失败或运行时失败后自动重建）
/// 4. PackageTraceRecorder 与 sink 的解耦（recorder 捕获 sink 异常，主流程不受影响）
/// 5. NullRuntimeCandidateTraceSink 空操作语义
///
/// 有界队列语义参考：BoundedChannelContextEventSink（IContextEventSink 的异步分发实现）。
/// </summary>
[TestClass]
[TestCategory("Trace")]
public sealed class ContextCoreTraceSinkVerificationTests
{
    // =========================================================================
    // 1. FileRuntimeCandidateTraceSink 现有行为验证
    // =========================================================================

    [TestMethod]
    public void FileSink_Enabled_ReturnsTrue()
    {
        var tempPath = GetTempTracePath();
        try
        {
            using var sink = new FileRuntimeCandidateTraceSink(tempPath);
            Assert.IsTrue(sink.Enabled);
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
    }

    [TestMethod]
    public void FileSink_Write_IncrementsWriteCount()
    {
        var tempPath = GetTempTracePath();
        try
        {
            using var sink = new FileRuntimeCandidateTraceSink(tempPath);
            var row = MakeMinimalRow();

            sink.Write(row);
            sink.Write(row);
            sink.Write(row);

            Assert.AreEqual(3, sink.WriteCount);
            Assert.AreEqual(0, sink.WriteFailures);
            Assert.AreEqual(0, sink.DroppedCount);
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
    }

    [TestMethod]
    public async Task FileSink_FlushAsync_PersistsBufferedWrites()
    {
        var tempPath = GetTempTracePath();
        try
        {
            // 注意：StreamWriter 持有文件句柄期间 File.ReadAllLinesAsync 会失败（文件被占用）。
            // 因此先 Dispose 释放句柄，再读取文件内容验证 FlushAsync 已落盘。
            var sink = new FileRuntimeCandidateTraceSink(tempPath);
            sink.Write(MakeMinimalRow());
            sink.Write(MakeMinimalRow());

            // AutoFlush=false，FlushAsync 前文件可能尚未落盘
            await sink.FlushAsync();
            // Dispose 释放文件句柄后才能读取
            sink.Dispose();

            Assert.IsTrue(File.Exists(tempPath), "FlushAsync 后文件应存在");
            var lines = await File.ReadAllLinesAsync(tempPath);
            Assert.AreEqual(2, lines.Length, "FlushAsync 后应能读到 2 行 trace");
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
    }

    [TestMethod]
    public void FileSink_ConstructorFailure_LeavesNullWriterAndDropsAllWrites()
    {
        // 构造函数无法打开文件时（如文件被独占锁定），_writer 保持 null，
        // 后续 Write 会先尝试重建 writer；重建失败（锁未释放）时计入 DroppedCount
        // （而非 WriteFailures）。
        //
        // 模拟方式：先用另一个 FileStream 以 FileShare.None 独占打开文件，
        // 再构造 FileRuntimeCandidateTraceSink，其 FileStream 构造会失败抛 IOException，
        // 被 catch 后 _writer 保持 null。
        var tempPath = GetTempTracePath();
        // 先创建文件
        File.WriteAllText(tempPath, "");
        // 独占锁定文件（FileShare.None 阻止其他 FileStream 打开）
        using var lockStream = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.None);
        try
        {
            using var sink = new FileRuntimeCandidateTraceSink(tempPath);

            Assert.AreEqual(0, sink.WriteCount, "null writer 状态下 WriteCount 应为 0");
            Assert.AreEqual(0, sink.WriteFailures, "null writer 不应计 WriteFailures（应计 DroppedCount）");

            sink.Write(MakeMinimalRow());
            sink.Write(MakeMinimalRow());

            Assert.AreEqual(2, sink.DroppedCount, "null writer 状态下所有 Write 应计 DroppedCount");
            Assert.AreEqual(0, sink.WriteCount, "null writer 状态下 WriteCount 应保持 0");
            Assert.AreEqual(0, sink.WriterRecreations, "锁未释放时 writer 重建应失败，不计数");
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
    }

    [TestMethod]
    public void FileSink_Dispose_IsIdempotentAndDoesNotThrow()
    {
        var tempPath = GetTempTracePath();
        try
        {
            var sink = new FileRuntimeCandidateTraceSink(tempPath);
            sink.Write(MakeMinimalRow());

            sink.Dispose();
            sink.Dispose();  // 二次 dispose 不应抛异常
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
    }

    [TestMethod]
    public void FileSink_Dispose_FlushesPendingWrites()
    {
        var tempPath = GetTempTracePath();
        try
        {
            var sink = new FileRuntimeCandidateTraceSink(tempPath);
            sink.Write(MakeMinimalRow());
            sink.Write(MakeMinimalRow());

            sink.Dispose();

            Assert.IsTrue(File.Exists(tempPath), "Dispose 后应刷新到磁盘");
            var lines = File.ReadAllLines(tempPath);
            Assert.AreEqual(2, lines.Length, "Dispose 后应能读到 2 行 trace");
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
    }

    // =========================================================================
    // 2. NullRuntimeCandidateTraceSink 空操作语义
    // =========================================================================

    [TestMethod]
    public void NullSink_Enabled_ReturnsFalse()
    {
        var sink = new NullRuntimeCandidateTraceSink();
        Assert.IsFalse(sink.Enabled);
    }

    [TestMethod]
    public void NullSink_Write_IsNoOp()
    {
        var sink = new NullRuntimeCandidateTraceSink();
        sink.Write(MakeMinimalRow());
        sink.Write(MakeMinimalRow());

        Assert.AreEqual(0, sink.WriteCount);
    }

    [TestMethod]
    public async Task NullSink_FlushAsync_CompletesImmediately()
    {
        var sink = new NullRuntimeCandidateTraceSink();
        await sink.FlushAsync(CancellationToken.None);
        // 无异常即通过
    }

    // =========================================================================
    // 3. PackageTraceRecorder 与 sink 的解耦验证
    // =========================================================================

    [TestMethod]
    public void PackageTraceRecorder_SinkWriteFailure_DoesNotAffectMainFlow()
    {
        // 验证：sink.Write 抛异常时，PackageTraceRecorder 主流程不受影响
        // PackageTraceRecorder 内部捕获 sink 异常并计数 _traceSinkWriteFailures
        var throwingSink = new ThrowingTraceSink(new InvalidOperationException("simulated sink failure"));
        var recorder = new PackageTraceRecorder(throwingSink, () => "op-test", () => "req-test");

        // 调用 WriteTraceRow（internal 方法，通过 InternalsVisibleTo 可访问）
        var candidate = MakeMinimalPackageTraceCandidate("cand-1");
        recorder.WriteTraceRow(
            candidate,
            section: "recent_context",
            outcome: RuntimeCandidateOutcome.Accepted,
            includedTokens: 100,
            originalTokens: 100,
            reason: "");

        // 主流程不应抛异常（即使 sink.Write 抛异常）
        // 通过此处到达即表示主流程未受影响
        Assert.IsTrue(throwingSink.WriteCount > 0, "sink 应被调用过");
    }

    [TestMethod]
    public void PackageTraceRecorder_NullSink_EarlyReturnsAndDoesNotCallWrite()
    {
        // 验证：sink.Enabled=false 时，PackageTraceRecorder 早返回，不调用 Write
        var nullSink = new NullRuntimeCandidateTraceSink();
        var recorder = new PackageTraceRecorder(nullSink, () => "op-test", () => "req-test");

        var candidate = MakeMinimalPackageTraceCandidate("cand-1");
        recorder.WriteTraceRow(
            candidate,
            section: "recent_context",
            outcome: RuntimeCandidateOutcome.Accepted,
            includedTokens: 100,
            originalTokens: 100,
            reason: "");

        // NullSink.Enabled=false，WriteTraceRow 应早返回
        Assert.AreEqual(0, nullSink.WriteCount);
    }

    // =========================================================================
    // 4. BoundedRuntimeCandidateTraceSink 有界队列能力 + writer recreation
    // =========================================================================

    [TestMethod]
    public async Task FileSink_BoundedQueueCapacity_DropsWhenFull()
    {
        // 期望：bounded queue 满后 TryWrite 返回 false，DroppedCount 递增，调用方不阻塞
        // 用阻塞的 inner sink 把消费者钉在首行，确保通道可被确定性填满
        using var gate = new ManualResetEventSlim(false);
        var blockingInner = new BlockingTraceSink(gate);
        await using var sink = new BoundedRuntimeCandidateTraceSink(blockingInner, capacity: 4, batchSize: 64);
        try
        {
            sink.Write(MakeMinimalRow());
            // 等待消费者取走首行并阻塞在 inner 写入
            await WaitUntilAsync(() => blockingInner.WriteCalls >= 1);

            for (var i = 0; i < 4; i++) sink.Write(MakeMinimalRow());
            sink.Write(MakeMinimalRow()); // 通道满 → 丢弃

            Assert.AreEqual(1, sink.DroppedCount, "通道满后 TryWrite 失败应计 DroppedCount");
            Assert.AreEqual(5, sink.WriteCount, "成功入队 5 行（首行 + 4 行）");
            Assert.AreEqual(4, sink.PendingCount, "通道应保持满");
        }
        finally
        {
            gate.Set();
        }
        await sink.FlushAsync();
    }

    [TestMethod]
    public async Task FileSink_BatchAppend_AggregatesRowsBeforeInnerWrite()
    {
        // 期望：consumer 累积 batchSize 条后调用 inner 写入，全部行最终落盘
        var recordingSink = new RecordingTraceSink();
        await using var sink = new BoundedRuntimeCandidateTraceSink(recordingSink, capacity: 1024, batchSize: 64);

        for (var i = 0; i < 200; i++) sink.Write(MakeMinimalRow());
        await sink.FlushAsync();

        Assert.AreEqual(200, recordingSink.WriteCount, "全部行应被写入 inner");
        Assert.IsTrue(sink.BatchEmitCount >= 4,
            $"200 行按每批最多 64 行至少需要 4 批，实际 {sink.BatchEmitCount}");
    }

    [TestMethod]
    public async Task FileSink_ShutdownDrain_FlushesResidualRowsOnDispose()
    {
        // 期望：DisposeAsync 触发 channel.TryComplete + 后台 consumer 残余 drain，
        // 关闭时未排空的行也必须全部落盘
        var tempPath = GetTempTracePath();
        try
        {
            var fileSink = new FileRuntimeCandidateTraceSink(tempPath);
            var sink = new BoundedRuntimeCandidateTraceSink(fileSink, capacity: 16, batchSize: 4);
            for (var i = 0; i < 10; i++) sink.Write(MakeMinimalRow());

            // 不等待排空即关闭：残余通道中的行必须被 drain
            await sink.DisposeAsync();
            // 释放文件句柄后才能读取
            fileSink.Dispose();

            var lines = await File.ReadAllLinesAsync(tempPath);
            Assert.AreEqual(10, lines.Length, "DisposeAsync 后残余队列中的全部行应落盘");
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
    }

    [TestMethod]
    public async Task FileSink_QueueSaturationDetection_PendingCountReflectsBacklog()
    {
        // 期望：PendingCount 属性反映通道待处理行数；队列饱和后 TryWrite 失败并丢弃
        using var gate = new ManualResetEventSlim(false);
        var blockingInner = new BlockingTraceSink(gate);
        await using var sink = new BoundedRuntimeCandidateTraceSink(blockingInner, capacity: 8, batchSize: 64);
        try
        {
            sink.Write(MakeMinimalRow());
            await WaitUntilAsync(() => blockingInner.WriteCalls >= 1);
            Assert.AreEqual(0, sink.PendingCount, "消费者取走首行后队列应为空");

            for (var i = 0; i < 8; i++) sink.Write(MakeMinimalRow());
            Assert.AreEqual(8, sink.PendingCount, "通道容量 8 应观察到 8 条待处理（饱和）");

            sink.Write(MakeMinimalRow());
            Assert.AreEqual(1, sink.DroppedCount, "饱和后 TryWrite 失败应丢弃");
            Assert.AreEqual(8, sink.PendingCount, "饱和后队列保持满");
        }
        finally
        {
            gate.Set();
        }
        await sink.FlushAsync();
    }

    [TestMethod]
    public void FileSink_WriterRecreation_RecoversAfterLockReleased()
    {
        // 期望：构造失败（文件被独占锁定）后 writer 为 null；锁释放后，
        // 后续 Write 触发 writer 重建并成功写入（recreation count 递增）
        var tempPath = GetTempTracePath();
        File.WriteAllText(tempPath, "");
        try
        {
            FileRuntimeCandidateTraceSink sink;
            using (var lockStream = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                // 构造时文件被独占锁定 → _writer 为 null
                sink = new FileRuntimeCandidateTraceSink(tempPath);
                Assert.AreEqual(0, sink.WriteCount);

                sink.Write(MakeMinimalRow());
                Assert.AreEqual(1, sink.DroppedCount, "锁未释放时写入应计 DroppedCount（重建失败）");
                Assert.AreEqual(0, sink.WriteCount);
                Assert.AreEqual(0, sink.WriterRecreations);
            } // 锁释放 → 文件可写

            // 后续 Write 触发 writer 重建并成功写入
            sink.Write(MakeMinimalRow());
            Assert.AreEqual(1, sink.WriterRecreations, "应重建 writer 一次");
            Assert.AreEqual(1, sink.WriteCount, "重建后写入应成功");
            Assert.AreEqual(1, sink.DroppedCount, "仅锁持有期间的写入被丢弃");
            Assert.AreEqual(0, sink.WriteFailures, "null writer 路径不计 WriteFailures");

            // 释放句柄后读取文件内容
            sink.Dispose();
            var lines = File.ReadAllLines(tempPath);
            Assert.AreEqual(1, lines.Length, "重建后写入的行应落盘");
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
    }

    // =========================================================================
    // 测试辅助
    // =========================================================================

    private static string GetTempTracePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ctx-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "trace.jsonl");
    }

    private static void CleanupTempFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && dir.StartsWith(Path.GetTempPath(), StringComparison.Ordinal))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* 测试清理容忍失败 */ }
    }

    private static RuntimeCandidateTraceRow MakeMinimalRow() => new()
    {
        OperationId = "op-test",
        RequestId = "req-test",
        CandidateId = "cand-1",
        SourceId = "cand-1",
        SourceType = RuntimeCandidateSourceType.Memory,
        Authority = CandidateAuthorityLevel.Unknown,
        StrategyType = CandidateStrategyType.Recent,
        RetrievalChannel = RuntimeCandidateRetrievalChannel.Memory,
        TraceSource = RuntimeCandidateTraceSource.PackageTrace,
        Section = "recent_context",
        Outcome = RuntimeCandidateOutcome.Accepted,
        OriginalTokens = 100,
        IncludedTokens = 100,
        TruncationRatio = 1.0
    };

    private static PackageTraceCandidate MakeMinimalPackageTraceCandidate(string id)
    {
        // 构造最小可用的 PackageTraceCandidate（通过 PackageTraceCandidate.FromMemory 工厂方法）
        return PackageTraceCandidate.FromMemory(
            new ContextMemoryItem
            {
                Id = id,
                WorkspaceId = "ws-test",
                CollectionId = "col-test",
                Type = "memory",
                Content = "test content",
                ContentFormat = ContextContentFormat.PlainText,
                Status = ContextMemoryStatus.Active
            },
            kind: "recent_context",
            score: 1.0,
            estimatedTokens: 100);
    }

    /// <summary>
    /// 测试 fake：所有 Write 调用抛指定异常。
    /// 用于验证 PackageTraceRecorder 捕获 sink 异常后主流程仍正常完成。
    /// </summary>
    private sealed class ThrowingTraceSink : IRuntimeCandidateTraceSink
    {
        private readonly Exception _exception;
        private int _writeCount;

        public ThrowingTraceSink(Exception exception) => _exception = exception;

        public bool Enabled => true;
        public int WriteCount => _writeCount;
        public int WriteFailures => _writeCount;

        public void Write(RuntimeCandidateTraceRow row)
        {
            Interlocked.Increment(ref _writeCount);
            throw _exception;
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// 测试 fake：Write 阻塞直到 gate 放行。
    /// 用于把 BoundedRuntimeCandidateTraceSink 的消费者钉在首行，确定性填满通道。
    /// </summary>
    private sealed class BlockingTraceSink : IRuntimeCandidateTraceSink
    {
        private readonly ManualResetEventSlim _gate;
        private int _writeCalls;

        public BlockingTraceSink(ManualResetEventSlim gate) => _gate = gate;

        public bool Enabled => true;
        public int WriteCount => _writeCalls;
        public int WriteCalls => _writeCalls;

        public void Write(RuntimeCandidateTraceRow row)
        {
            Interlocked.Increment(ref _writeCalls);
            // 带超时等待：即使测试异常未放行，也不会永久挂起消费者线程
            _gate.Wait(TimeSpan.FromSeconds(30));
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// 测试 fake：统计写入行数，不落盘。
    /// 用于验证 BoundedRuntimeCandidateTraceSink 聚合消费后全部行到达 inner。
    /// </summary>
    private sealed class RecordingTraceSink : IRuntimeCandidateTraceSink
    {
        private int _writeCount;

        public bool Enabled => true;
        public int WriteCount => _writeCount;

        public void Write(RuntimeCandidateTraceRow row) => Interlocked.Increment(ref _writeCount);

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                Assert.Fail($"等待条件超时（{timeoutMs}ms）");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
