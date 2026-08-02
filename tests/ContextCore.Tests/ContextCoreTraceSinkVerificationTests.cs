using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.Learning.V14_0;

namespace ContextCore.Tests;

/// <summary>
/// Runtime Candidate Trace Sink 验证测试。
///
/// 验证范围：
/// 1. FileRuntimeCandidateTraceSink 现有行为（write count / drop on null writer / write failures / flush）
/// 2. PackageTraceRecorder 与 sink 的解耦（recorder 捕获 sink 异常，主流程不受影响）
/// 3. NullRuntimeCandidateTraceSink 空操作语义
///
/// 已知缺口（标记 [Ignore]，待 async dispatcher 实现后启用）：
/// - bounded queue（当前为同步 lock，无队列）
/// - batch append（当前为单行写入，无批量）
/// - shutdown drain（当前无队列需要 drain）
/// - queue saturation detection（当前无队列饱和概念）
/// - writer recreation（当前构造失败后永久 null writer，运行时失败仅计数不恢复）
///
/// 参考：BoundedChannelContextEventSink（IContextEventSink 的异步分发实现）展示了
/// 上述 5 项缺口的标准实现模式，未来可移植到 IRuntimeCandidateTraceSink。
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
        // 后续所有 Write 调用应被计数为 DroppedCount（而非 WriteFailures）
        // 此为构造期失败的"永久 null writer"语义——无 writer recreation
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
    // 4. 已知缺口文档化（[Ignore] — 待 async dispatcher 实现后启用）
    // =========================================================================

    [Ignore("当前 FileRuntimeCandidateTraceSink 为同步 lock 实现，无 BoundedChannel 队列。" +
            "参考 BoundedChannelContextEventSink（IContextEventSink）的实现模式，" +
            "未来应新增 BoundedRuntimeCandidateTraceSink 包装类提供 bounded queue 能力。")]
    [TestMethod]
    public void FileSink_BoundedQueueCapacity_NotYetImplemented()
    {
        // 期望：bounded queue 满后 TryWrite 返回 false，DroppedCount 递增
        // 当前：无队列，所有 Write 同步执行
        Assert.Inconclusive("bounded queue capacity 未实现");
    }

    [Ignore("当前 FileRuntimeCandidateTraceSink 为单行 WriteLine，无批量写入接口。" +
            "IRuntimeCandidateTraceSink 接口无 WriteBatch 方法，" +
            "未来实现 BoundedRuntimeCandidateTraceSink 时需在 consumer 端聚合批量后调用 inner.Write 循环。")]
    [TestMethod]
    public void FileSink_BatchAppend_NotYetImplemented()
    {
        // 期望：consumer 累积 batchSize 条后调用 inner.EmitBatchAsync
        // 当前：每条 Write 立即同步 WriteLine
        Assert.Inconclusive("batch append 未实现");
    }

    [Ignore("当前 FileRuntimeCandidateTraceSink 无后台 consumer，无队列需要 drain。" +
            "Dispose 仅 flush StreamWriter，无残余队列处理。" +
            "BoundedChannelContextEventSink.DisposeAsync 展示了标准 drain 模式：" +
            "TryComplete + Cancel + 残余循环 + CancellationToken.None 保证 drain 完成。")]
    [TestMethod]
    public void FileSink_ShutdownDrain_NotYetImplemented()
    {
        // 期望：DisposeAsync 触发 channel.TryComplete + 后台 consumer 残余 drain
        // 当前：Dispose 仅 flush writer
        Assert.Inconclusive("shutdown drain 未实现");
    }

    [Ignore("当前 FileRuntimeCandidateTraceSink 无 PendingCount 可观察属性，无饱和检测。" +
            "BoundedChannelContextEventSink.PendingCount 通过 channel.Reader.Count 暴露。")]
    [TestMethod]
    public void FileSink_QueueSaturationDetection_NotYetImplemented()
    {
        // 期望：PendingCount 属性 + TryWrite false 返回值作为饱和信号
        // 当前：无队列，无饱和概念
        Assert.Inconclusive("queue saturation detection 未实现");
    }

    [Ignore("当前 FileRuntimeCandidateTraceSink 构造失败后 _writer 永久 null，" +
            "运行时 Write 异常仅计 _writeFailures 不尝试重建 writer。" +
            "未来应在 Write catch 块中关闭旧 writer + 尝试重新打开文件，并跟踪 recreation count。")]
    [TestMethod]
    public void FileSink_WriterRecreation_NotYetImplemented()
    {
        // 期望：运行时 Write 异常后，关闭旧 writer + 重新打开文件 + 后续 Write 恢复成功
        // 当前：异常仅计数，writer 不重建
        Assert.Inconclusive("writer recreation 未实现");
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
}
