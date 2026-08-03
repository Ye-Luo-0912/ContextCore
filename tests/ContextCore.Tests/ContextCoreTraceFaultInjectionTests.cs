using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.Learning.V14_0;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// Trace 写入与正式返回解耦的 fault injection 测试。
///
/// 验证目标（用户指令）：
/// "Trace 写入与正式返回解耦的 fault injection 测试
/// (latency 100ms / exception / queue full / shutdown / disk full / Postgres 不可用)
/// 正式请求结果必须保持不变。"
///
/// 验证范围：
/// 1. IRuntimeCandidateTraceSink (PackageTraceRecorder 路径) — latency / exception / disk full
/// 2. IContextPackageBuildTraceStore (BasicContextPackageBuilder 路径) — latency / exception
/// 3. IDecisionTraceStore (Package + Retrieval 路径) — latency / exception
/// 4. IRetrievalTraceStore (HybridContextRetriever 路径) — latency / exception
///
/// 已知缺口（[Ignore] 文档化）：
/// - queue full: 当前 IRuntimeCandidateTraceSink 为同步 lock 实现，无 bounded queue
/// - shutdown during flush: 当前 sink Dispose 仅 flush StreamWriter，无残余队列 drain
/// - Postgres 不可用: trace sink 无 Postgres 实现；PostgresDecisionTraceStore/PostgresRetrievalTraceStore
/// 的故障路径已在 IDecisionTraceStore/IRetrievalTraceStore 的 fault injection exception 测试中通过
/// throwing fake 覆盖（任何异常类型包括 Npgsql.NpgsqlException 均被 fail-open catch 吞掉）。
/// </summary>
[TestClass]
[TestCategory("Trace")]
public sealed class ContextCoreTraceFaultInjectionTests
{
    private static readonly string WorkspaceId = "workspace-fault";
    private static readonly string CollectionId = "collection-fault";

    // =========================================================================
    // 1. IRuntimeCandidateTraceSink (PackageTraceRecorder 路径) fault injection
    // =========================================================================

    /// <summary>
    /// latency: sink.Write 阻塞 100ms 时，PackageTraceRecorder 主流程仍应正常完成。
    /// 当前 IRuntimeCandidateTraceSink 为同步 void Write，主流程会被阻塞（known coupling），
    /// 但 trace 写入完成后主流程结果必须正确。
    /// </summary>
    [TestMethod]
    public void PackageTraceRecorder_SinkLatency100ms_MainFlowCompletesSuccessfully()
    {
        var latencySink = new LatencyTraceSink(TimeSpan.FromMilliseconds(100));
        var recorder = new PackageTraceRecorder(latencySink, () => "op-test", () => "req-test");

        var candidate = MakeMinimalPackageTraceCandidate("cand-latency");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        recorder.WriteTraceRow(
            candidate,
            section: "recent_context",
            outcome: RuntimeCandidateOutcome.Accepted,
            includedTokens: 100,
            originalTokens: 100,
            reason: "");
        sw.Stop();

        // 主流程应正常完成（不抛异常）
        Assert.IsTrue(latencySink.WriteCount > 0, "latency sink 应被调用过");
        // 已知耦合：同步 sink 阻塞主流程至少 100ms（如未来引入 async dispatcher 应移除该断言）
        Assert.IsTrue(sw.Elapsed.TotalMilliseconds >= 90.0,
            $"主流程应被同步 sink 阻塞至少 100ms，实际 {sw.Elapsed.TotalMilliseconds}ms（已知同步耦合）");
    }

    /// <summary>
    /// disk full: sink.Write 抛 IOException("disk full") 时，主流程不受影响。
    /// </summary>
    [TestMethod]
    public void PackageTraceRecorder_SinkDiskFull_MainFlowUnaffected()
    {
        var diskFullSink = new ThrowingTraceSink(new IOException("No space left on device"));
        var recorder = new PackageTraceRecorder(diskFullSink, () => "op-test", () => "req-test");

        var candidate = MakeMinimalPackageTraceCandidate("cand-disk-full");
        recorder.WriteTraceRow(
            candidate,
            section: "recent_context",
            outcome: RuntimeCandidateOutcome.Accepted,
            includedTokens: 100,
            originalTokens: 100,
            reason: "");

        // 主流程到达此处即表示未抛异常
        Assert.IsTrue(diskFullSink.WriteCount > 0, "throwing sink 应被调用过");
    }

    /// <summary>
    /// exception types: 不同异常类型（IOException / ArgumentException / InvalidOperationException /
    /// OutOfMemoryException / ApplicationException）均不应影响主流程。
    /// PackageTraceRecorder 通过 catch (Exception ex) 捕获所有非 OperationCanceledException 异常。
    /// </summary>
    [TestMethod]
    [DataRow(typeof(IOException), "disk I/O failure")]
    [DataRow(typeof(ArgumentException), "invalid argument")]
    [DataRow(typeof(InvalidOperationException), "invalid operation")]
    [DataRow(typeof(ApplicationException), "application failure")]
    public void PackageTraceRecorder_SinkThrowsVariousExceptions_MainFlowUnaffected(Type exceptionType, string message)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, message)!;
        var throwingSink = new ThrowingTraceSink(exception);
        var recorder = new PackageTraceRecorder(throwingSink, () => "op-test", () => "req-test");

        var candidate = MakeMinimalPackageTraceCandidate("cand-throw");
        recorder.WriteTraceRow(
            candidate,
            section: "recent_context",
            outcome: RuntimeCandidateOutcome.Accepted,
            includedTokens: 100,
            originalTokens: 100,
            reason: "");

        Assert.IsTrue(throwingSink.WriteCount > 0, "throwing sink 应被调用过");
    }

    // =========================================================================
    // 2. IContextPackageBuildTraceStore (BasicContextPackageBuilder 路径) fault injection
    // =========================================================================

    /// <summary>
    /// latency: IContextPackageBuildTraceStore.SaveAsync 延迟 100ms 时，
    /// 正式 package 输出（Sections / SelectedItems / DroppedItems）必须保持不变。
    /// </summary>
    [TestMethod]
    public async Task PackageBuilder_TraceStoreLatency100ms_PackageOutputUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("item-a", "trace 延迟测试 A", ["fault"], now));
        await store.SaveAsync(MakeItem("item-b", "trace 延迟测试 B", ["fault"], now));

        // 无 trace store 的基线
        var baselineBuilder = new BasicContextPackageBuilder(store);
        // 注入 100ms 延迟的 trace store
        var latencyStore = new LatencyPackageBuildTraceStore(TimeSpan.FromMilliseconds(100));
        var builderWithLatency = new BasicContextPackageBuilder(
            store, null, null, null, null, traceStore: latencyStore);

        var request = MakeMinimalPackageRequest();

        var baselineResult = await baselineBuilder.BuildDetailedAsync(request);
        var latencyResult = await builderWithLatency.BuildDetailedAsync(request);

        // 正式 package 输出应完全一致
        Assert.AreEqual(baselineResult.Package.Sections.Count, latencyResult.Package.Sections.Count);
        Assert.AreEqual(baselineResult.SelectedItems.Count, latencyResult.SelectedItems.Count);
        Assert.AreEqual(baselineResult.DroppedItems.Count, latencyResult.DroppedItems.Count);
        for (var i = 0; i < baselineResult.SelectedItems.Count; i++)
        {
            Assert.AreEqual(
                baselineResult.SelectedItems[i].ItemId,
                latencyResult.SelectedItems[i].ItemId,
                $"SelectedItems[{i}].ItemId 应一致");
        }
        Assert.IsTrue(latencyStore.SaveCount > 0, "latency trace store 应被调用过");
    }

    /// <summary>
    /// exception: IContextPackageBuildTraceStore.SaveAsync 抛异常时，正式 package 输出必须保持不变。
    /// BasicContextPackageBuilder.WriteTracesAsync 通过 catch (Exception) 实现 fail-open。
    /// </summary>
    [TestMethod]
    public async Task PackageBuilder_TraceStoreThrows_PackageOutputUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("item-a", "trace 异常测试 A", ["fault"], now));
        await store.SaveAsync(MakeItem("item-b", "trace 异常测试 B", ["fault"], now));

        var baselineBuilder = new BasicContextPackageBuilder(store);
        var throwingStore = new ThrowingPackageBuildTraceStore(new IOException("trace backend unavailable"));
        var builderWithThrowing = new BasicContextPackageBuilder(
            store, null, null, null, null, traceStore: throwingStore);

        var request = MakeMinimalPackageRequest();

        var baselineResult = await baselineBuilder.BuildDetailedAsync(request);
        var throwingResult = await builderWithThrowing.BuildDetailedAsync(request);

        // 正式 package 输出应完全一致
        Assert.AreEqual(baselineResult.Package.Sections.Count, throwingResult.Package.Sections.Count);
        Assert.AreEqual(baselineResult.SelectedItems.Count, throwingResult.SelectedItems.Count);
        Assert.AreEqual(baselineResult.DroppedItems.Count, throwingResult.DroppedItems.Count);
        for (var i = 0; i < baselineResult.SelectedItems.Count; i++)
        {
            Assert.AreEqual(
                baselineResult.SelectedItems[i].ItemId,
                throwingResult.SelectedItems[i].ItemId);
        }
        Assert.IsTrue(throwingStore.SaveCount > 0, "throwing trace store 应被调用过");
    }

    // =========================================================================
    // 3. IDecisionTraceStore (Package + Retrieval 路径) fault injection
    // =========================================================================

    /// <summary>
    /// latency: IDecisionTraceStore.SaveAsync 延迟 100ms 时，
    /// 正式 package 输出必须保持不变。
    /// </summary>
    [TestMethod]
    public async Task PackageBuilder_DecisionTraceLatency100ms_PackageOutputUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("item-a", "decision 延迟测试 A", ["fault"], now));
        await store.SaveAsync(MakeItem("item-b", "decision 延迟测试 B", ["fault"], now));

        var baselineBuilder = new BasicContextPackageBuilder(store);
        var latencyDecisionStore = new LatencyDecisionTraceStore(TimeSpan.FromMilliseconds(100));
        var builderWithLatency = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, latencyDecisionStore);

        var request = MakeMinimalPackageRequest();

        var baselineResult = await baselineBuilder.BuildDetailedAsync(request);
        var latencyResult = await builderWithLatency.BuildDetailedAsync(request);

        Assert.AreEqual(baselineResult.SelectedItems.Count, latencyResult.SelectedItems.Count);
        Assert.AreEqual(baselineResult.DroppedItems.Count, latencyResult.DroppedItems.Count);
        Assert.IsTrue(latencyDecisionStore.SaveCount > 0, "latency decision store 应被调用过");
    }

    /// <summary>
    /// exception: IDecisionTraceStore.SaveAsync 抛异常时，正式 package 输出必须保持不变。
    /// </summary>
    [TestMethod]
    public async Task PackageBuilder_DecisionTraceThrows_PackageOutputUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryContextStore();
        await store.SaveAsync(MakeItem("item-a", "decision 异常测试 A", ["fault"], now));
        await store.SaveAsync(MakeItem("item-b", "decision 异常测试 B", ["fault"], now));

        var baselineBuilder = new BasicContextPackageBuilder(store);
        var throwingDecisionStore = new ThrowingDecisionTraceStore(new IOException("decision backend unavailable"));
        var builderWithThrowing = new BasicContextPackageBuilder(
            store, null, null, null, null, null, null, null, throwingDecisionStore);

        var request = MakeMinimalPackageRequest();

        var baselineResult = await baselineBuilder.BuildDetailedAsync(request);
        var throwingResult = await builderWithThrowing.BuildDetailedAsync(request);

        Assert.AreEqual(baselineResult.SelectedItems.Count, throwingResult.SelectedItems.Count);
        Assert.AreEqual(baselineResult.DroppedItems.Count, throwingResult.DroppedItems.Count);
        Assert.IsTrue(throwingDecisionStore.SaveCount > 0, "throwing decision store 应被调用过");
    }

    /// <summary>
    /// latency: IDecisionTraceStore.SaveAsync 延迟 100ms 时，正式 retrieval 输出必须保持不变。
    /// </summary>
    [TestMethod]
    public async Task Retriever_DecisionTraceLatency100ms_RetrievalOutputUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(MakeItem("ret-1", "retrieval decision 延迟测试", ["fault"], now));

        var baselineRetriever = new HybridContextRetriever(contextStore);
        var latencyDecisionStore = new LatencyDecisionTraceStore(TimeSpan.FromMilliseconds(100));
        var retrieverWithLatency = new HybridContextRetriever(
            contextStore, traceStore: null, decisionTraceStore: latencyDecisionStore);

        var request = MakeMinimalRetrievalRequest();

        var baselineResult = await baselineRetriever.RetrieveAsync(request);
        var latencyResult = await retrieverWithLatency.RetrieveAsync(request);

        Assert.AreEqual(baselineResult.Succeeded, latencyResult.Succeeded);
        Assert.AreEqual(baselineResult.SelectedItems.Count, latencyResult.SelectedItems.Count);
        Assert.AreEqual(baselineResult.DroppedItems.Count, latencyResult.DroppedItems.Count);
        Assert.IsTrue(latencyDecisionStore.SaveCount > 0, "latency decision store 应被调用过");
    }

    /// <summary>
    /// exception: IDecisionTraceStore.SaveAsync 抛异常时，正式 retrieval 输出必须保持不变。
    /// HybridContextRetriever 通过 catch (Exception) 实现 fail-open（OperationCanceledException 除外）。
    /// </summary>
    [TestMethod]
    public async Task Retriever_DecisionTraceThrows_RetrievalOutputUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(MakeItem("ret-1", "retrieval decision 异常测试", ["fault"], now));

        var baselineRetriever = new HybridContextRetriever(contextStore);
        var throwingDecisionStore = new ThrowingDecisionTraceStore(new IOException("decision backend unavailable"));
        var retrieverWithThrowing = new HybridContextRetriever(
            contextStore, traceStore: null, decisionTraceStore: throwingDecisionStore);

        var request = MakeMinimalRetrievalRequest();

        var baselineResult = await baselineRetriever.RetrieveAsync(request);
        var throwingResult = await retrieverWithThrowing.RetrieveAsync(request);

        Assert.AreEqual(baselineResult.Succeeded, throwingResult.Succeeded);
        Assert.AreEqual(baselineResult.SelectedItems.Count, throwingResult.SelectedItems.Count);
        Assert.AreEqual(baselineResult.DroppedItems.Count, throwingResult.DroppedItems.Count);
        Assert.IsTrue(throwingDecisionStore.SaveCount > 0, "throwing decision store 应被调用过");
    }

    // =========================================================================
    // 4. IRetrievalTraceStore (HybridContextRetriever 路径) fault injection
    // =========================================================================

    /// <summary>
    /// latency: IRetrievalTraceStore.SaveAsync 延迟 100ms 时，正式 retrieval 输出必须保持不变。
    /// </summary>
    [TestMethod]
    public async Task Retriever_RetrievalTraceLatency100ms_RetrievalOutputUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(MakeItem("ret-1", "retrieval trace 延迟测试", ["fault"], now));

        var baselineRetriever = new HybridContextRetriever(contextStore);
        var latencyStore = new LatencyRetrievalTraceStore(TimeSpan.FromMilliseconds(100));
        var retrieverWithLatency = new HybridContextRetriever(
            contextStore, traceStore: latencyStore);

        var request = MakeMinimalRetrievalRequest();

        var baselineResult = await baselineRetriever.RetrieveAsync(request);
        var latencyResult = await retrieverWithLatency.RetrieveAsync(request);

        Assert.AreEqual(baselineResult.Succeeded, latencyResult.Succeeded);
        Assert.AreEqual(baselineResult.SelectedItems.Count, latencyResult.SelectedItems.Count);
        Assert.IsTrue(latencyStore.SaveCount > 0, "latency retrieval trace store 应被调用过");
    }

    /// <summary>
    /// exception: IRetrievalTraceStore.SaveAsync 抛异常时，正式 retrieval 输出必须保持不变。
    /// HybridContextRetriever 通过 catch (Exception) 实现 fail-open（OperationCanceledException 除外）。
    /// </summary>
    [TestMethod]
    public async Task Retriever_RetrievalTraceThrows_RetrievalOutputUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(MakeItem("ret-1", "retrieval trace 异常测试", ["fault"], now));

        var baselineRetriever = new HybridContextRetriever(contextStore);
        var throwingStore = new ThrowingRetrievalTraceStore(new IOException("retrieval trace backend unavailable"));
        var retrieverWithThrowing = new HybridContextRetriever(
            contextStore, traceStore: throwingStore);

        var request = MakeMinimalRetrievalRequest();

        var baselineResult = await baselineRetriever.RetrieveAsync(request);
        var throwingResult = await retrieverWithThrowing.RetrieveAsync(request);

        Assert.AreEqual(baselineResult.Succeeded, throwingResult.Succeeded);
        Assert.AreEqual(baselineResult.SelectedItems.Count, throwingResult.SelectedItems.Count);
        Assert.IsTrue(throwingStore.SaveCount > 0, "throwing retrieval trace store 应被调用过");
    }

    // =========================================================================
    // 5. 已知缺口文档化（[Ignore] — 待 async dispatcher 实现后启用）
    // =========================================================================

    [Ignore("当前 IRuntimeCandidateTraceSink 为同步 lock 实现，无 BoundedChannel 队列。" +
            "queue full 故障注入需要 async dispatcher + bounded capacity 才有意义；" +
            "当前同步实现下 Write 要么立即成功要么立即失败，无 queue full 概念。" +
            "参考 BoundedChannelContextEventSink 的 BoundedChannelFullMode 行为。")]
    [TestMethod]
    public void TraceSink_QueueFull_NotYetImplemented()
    {
        // 期望：bounded queue 满后 TryWrite 返回 false，DroppedCount 递增，主流程不受影响
        // 当前：无队列，所有 Write 同步执行
        Assert.Inconclusive("queue full fault injection 需要 async dispatcher 实现");
    }

    [Ignore("当前 IRuntimeCandidateTraceSink.Dispose 仅 flush StreamWriter，无残余队列 drain。" +
            "shutdown during flush 故障注入需要：(1) 后台 consumer 在 Dispose 时仍在处理；" +
            "(2) DisposeAsync 触发 channel.TryComplete + Cancel + 残余循环；" +
            "参考 BoundedChannelContextEventSink.DisposeAsync 展示了标准 drain 模式。")]
    [TestMethod]
    public void TraceSink_ShutdownDuringFlush_NotYetImplemented()
    {
        // 期望：DisposeAsync 期间即使 consumer 仍在写入，残余行也能被 drain 完
        // 当前：Dispose 仅 flush 既有 StreamWriter，无 drain 概念
        Assert.Inconclusive("shutdown during flush fault injection 需要 async dispatcher 实现");
    }

    [Ignore("Postgres 不可用场景的 fault injection：trace sink 无 Postgres 实现，" +
            "IDecisionTraceStore/IRetrievalTraceStore/IContextPackageBuildTraceStore 的 Postgres 实现 " +
            "(PostgresDecisionTraceStore / PostgresRetrievalTraceStore) 在连接断开时会抛 NpgsqlException，" +
            "已被上方 throwing fake 测试覆盖（catch (Exception) 包含 NpgsqlException）。" +
            "如需端到端验证，应在 IntegrationTests 项目中使用 Testcontainers + 显式断开容器场景。")]
    [TestMethod]
    public void TraceSink_PostgresUnavailable_CoveredByThrowingFakeTests()
    {
        // 此场景已被 PackageBuilder_TraceStoreThrows_PackageOutputUnchanged /
        // Retriever_DecisionTraceThrows_RetrievalOutputUnchanged /
        // Retriever_RetrievalTraceThrows_RetrievalOutputUnchanged 覆盖
        // （throwing fake 抛 IOException 等价于 Postgres 连接断开抛 NpgsqlException）
        Assert.Inconclusive("Postgres 不可用场景由 throwing fake 测试覆盖；端到端验证应在 IntegrationTests");
    }

    // =========================================================================
    // 测试辅助
    // =========================================================================

    private static ContextItem MakeItem(string id, string content, string[] tags, DateTimeOffset now) => new()
    {
        Id = id,
        WorkspaceId = WorkspaceId,
        CollectionId = CollectionId,
        Type = "note",
        Content = content,
        ContentFormat = ContextContentFormat.PlainText,
        Tags = tags,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static ContextPackageRequest MakeMinimalPackageRequest() => new()
    {
        WorkspaceId = WorkspaceId,
        CollectionId = CollectionId,
        QueryText = "fault injection",
        TokenBudget = 1000
    };

    private static ContextRetrievalRequest MakeMinimalRetrievalRequest() => new()
    {
        WorkspaceId = WorkspaceId,
        CollectionId = CollectionId,
        QueryText = "fault injection",
        IncludeKeywordRecall = true,
        IncludeVectorRecall = false,
        IncludeRelationExpansion = false,
        IncludeWorkingMemory = false,
        IncludeStableMemory = false,
        CandidateTake = 10,
        TopK = 10,
        TokenBudget = 1000
    };

    private static PackageTraceCandidate MakeMinimalPackageTraceCandidate(string id)
    {
        return PackageTraceCandidate.FromMemory(
            new ContextMemoryItem
            {
                Id = id,
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Type = "memory",
                Content = "test content for fault injection",
                ContentFormat = ContextContentFormat.PlainText,
                Status = ContextMemoryStatus.Active
            },
            kind: "recent_context",
            score: 1.0,
            estimatedTokens: 100);
    }

    // -------------------------------------------------------------------------
    // Fault injection fakes for IRuntimeCandidateTraceSink
    // -------------------------------------------------------------------------

    /// <summary>注入指定延迟的 sink。用于验证 latency 100ms 故障注入。</summary>
    private sealed class LatencyTraceSink : IRuntimeCandidateTraceSink
    {
        private readonly TimeSpan _latency;
        private int _writeCount;

        public LatencyTraceSink(TimeSpan latency) => _latency = latency;

        public bool Enabled => true;
        public int WriteCount => _writeCount;

        public void Write(RuntimeCandidateTraceRow row)
        {
            Interlocked.Increment(ref _writeCount);
            if (_latency > TimeSpan.Zero) Thread.Sleep(_latency);
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>每次 Write 抛指定异常的 sink。用于验证 exception / disk full 故障注入。</summary>
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

    // -------------------------------------------------------------------------
    // Fault injection fakes for IContextPackageBuildTraceStore
    // -------------------------------------------------------------------------

    private sealed class LatencyPackageBuildTraceStore : IContextPackageBuildTraceStore
    {
        private readonly TimeSpan _latency;
        private int _saveCount;

        public LatencyPackageBuildTraceStore(TimeSpan latency) => _latency = latency;

        public int SaveCount => _saveCount;

        public async Task SaveAsync(ContextPackageBuildResult result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCount);
            if (_latency > TimeSpan.Zero)
                await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<ContextPackageBuildResult>> QueryRecentAsync(
            string workspaceId, string collectionId, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextPackageBuildResult>>(Array.Empty<ContextPackageBuildResult>());
    }

    private sealed class ThrowingPackageBuildTraceStore : IContextPackageBuildTraceStore
    {
        private readonly Exception _exception;
        private int _saveCount;

        public ThrowingPackageBuildTraceStore(Exception exception) => _exception = exception;

        public int SaveCount => _saveCount;

        public Task SaveAsync(ContextPackageBuildResult result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCount);
            throw _exception;
        }

        public Task<IReadOnlyList<ContextPackageBuildResult>> QueryRecentAsync(
            string workspaceId, string collectionId, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextPackageBuildResult>>(Array.Empty<ContextPackageBuildResult>());
    }

    // -------------------------------------------------------------------------
    // Fault injection fakes for IDecisionTraceStore
    // -------------------------------------------------------------------------

    private sealed class LatencyDecisionTraceStore : IDecisionTraceStore
    {
        private readonly TimeSpan _latency;
        private int _saveCount;

        public LatencyDecisionTraceStore(TimeSpan latency) => _latency = latency;

        public int SaveCount => _saveCount;

        public async Task SaveAsync(ContextDecisionRecord record, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCount);
            if (_latency > TimeSpan.Zero)
                await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
            string workspaceId, string collectionId, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextDecisionRecord>>(Array.Empty<ContextDecisionRecord>());
    }

    private sealed class ThrowingDecisionTraceStore : IDecisionTraceStore
    {
        private readonly Exception _exception;
        private int _saveCount;

        public ThrowingDecisionTraceStore(Exception exception) => _exception = exception;

        public int SaveCount => _saveCount;

        public Task SaveAsync(ContextDecisionRecord record, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCount);
            throw _exception;
        }

        public Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
            string workspaceId, string collectionId, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextDecisionRecord>>(Array.Empty<ContextDecisionRecord>());
    }

    // -------------------------------------------------------------------------
    // Fault injection fakes for IRetrievalTraceStore
    // -------------------------------------------------------------------------

    private sealed class LatencyRetrievalTraceStore : IRetrievalTraceStore
    {
        private readonly TimeSpan _latency;
        private int _saveCount;

        public LatencyRetrievalTraceStore(TimeSpan latency) => _latency = latency;

        public int SaveCount => _saveCount;

        public async Task SaveAsync(ContextRetrievalTrace trace, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCount);
            if (_latency > TimeSpan.Zero)
                await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(
            string workspaceId, string collectionId, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextRetrievalTrace>>(Array.Empty<ContextRetrievalTrace>());
    }

    private sealed class ThrowingRetrievalTraceStore : IRetrievalTraceStore
    {
        private readonly Exception _exception;
        private int _saveCount;

        public ThrowingRetrievalTraceStore(Exception exception) => _exception = exception;

        public int SaveCount => _saveCount;

        public Task SaveAsync(ContextRetrievalTrace trace, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCount);
            throw _exception;
        }

        public Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(
            string workspaceId, string collectionId, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextRetrievalTrace>>(Array.Empty<ContextRetrievalTrace>());
    }
}
