using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Graph;
using ContextCore.Service;
using ContextCore.Service.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Tests;

/// <summary>
/// RelationReconciliationWorker 单元测试。
/// 使用 fake IRelationOutboxStore / IRelationStore / IRelationProjectionWriter 验证：
/// <list type="bullet">
/// <item>Enabled=false → worker 立即退出，不调用任何 store。</item>
/// <item>IRelationOutboxStore 未注册 → worker 优雅退出（no-op）。</item>
/// <item>Relation 已落库且字段匹配 → MarkAppliedAsync 被调用。</item>
/// <item>Relation 缺失 → projectionWriter.WriteAsync 回放 → MarkAppliedAsync。</item>
/// <item>Payload 为 null → MarkFailedAsync（无法回放）。</item>
/// <item>RenewHeartbeatAsync 返回 false → 处理中止（不调用 MarkApplied/MarkFailed）。</item>
/// </list>
/// </summary>
[TestClass]
[TestCategory("Unit")]
[TestCategory("P1-5")]
public sealed class RelationReconciliationWorkerTests
{
    private static readonly ContextRelation SampleRelation = new()
    {
        Id = "rel-test-001",
        WorkspaceId = "ws-test",
        CollectionId = "col-test",
        SourceId = "src-1",
        TargetId = "tgt-1",
        RelationType = ContextRelationTypes.RelatedTo,
        Weight = 0.8,
        Confidence = 0.9,
        CreatedAt = DateTimeOffset.UtcNow,
        Provenance = "ingest"
    };

    private static RelationOutboxRecord CreateOutboxRecord(ContextRelation? payload, string? outboxId = null) => new()
    {
        OutboxId = outboxId ?? "ob-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = SampleRelation.WorkspaceId,
        CollectionId = SampleRelation.CollectionId,
        RelationId = SampleRelation.Id,
        OperationKind = RelationOutboxOperationKind.Upsert,
        Provenance = "ingest",
        Payload = payload,
        State = RelationOutboxStates.Pending,
        MaxRetryCount = 3,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>Enabled=false 时 worker 立即退出。</summary>
    [TestMethod]
    [Timeout(10000)]
    public async Task Worker_WhenDisabled_DoesNothing()
    {
        var fakeOutbox = new FakeRelationOutboxStore();
        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeOutbox, relationStore: null, projectionWriter: null, enabled: false);

        var worker = provider.GetRequiredService<RelationReconciliationWorker>();
        await worker.StartAsync(cts.Token);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, fakeOutbox.AcquirePendingCalls, "Disabled 时不调用 AcquirePendingAsync");
    }

    /// <summary>IRelationOutboxStore 未注册时 worker 优雅退出（模拟 FileSystem/InMemory provider）。</summary>
    [TestMethod]
    [Timeout(10000)]
    public async Task Worker_WhenOutboxStoreNotRegistered_ExitsNoOp()
    {
        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(outboxStore: null, relationStore: null, projectionWriter: null, enabled: true);

        var worker = provider.GetRequiredService<RelationReconciliationWorker>();
        await worker.StartAsync(cts.Token);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        // Worker 应该立即退出——验证方式：worker 实际运行时间短，stop 立即返回。
        // 没有异常即视为通过——检查 worker.StopAsync 不超时即可。
    }

    /// <summary>Relation 已落库且字段匹配 → MarkAppliedAsync 被调用。</summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WhenRelationExistsAndMatches_CallsMarkApplied()
    {
        var record = CreateOutboxRecord(SampleRelation);
        var fakeOutbox = new FakeRelationOutboxStore(record);
        var fakeRelationStore = new FakeRelationStore(SampleRelation);
        var fakeProjectionWriter = new FakeRelationProjectionWriter();

        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeOutbox, fakeRelationStore, fakeProjectionWriter, enabled: true);

        var worker = provider.GetRequiredService<RelationReconciliationWorker>();
        await worker.StartAsync(cts.Token);

        await fakeOutbox.MarkAppliedCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(fakeOutbox.AcquirePendingCalls >= 1, "AcquirePendingAsync 应至少被调用一次");
        Assert.AreEqual(1, fakeOutbox.MarkAppliedCalls, "已落库 relation 应调用 MarkAppliedAsync");
        Assert.AreEqual(0, fakeOutbox.MarkFailedCalls, "成功路径不应调用 MarkFailedAsync");
        Assert.AreEqual(0, fakeProjectionWriter.WriteCalls, "已落库 relation 不应回放 projectionWriter.WriteAsync");
    }

    /// <summary>Relation 缺失 → projectionWriter.WriteAsync 回放 → MarkAppliedAsync。</summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WhenRelationMissing_ReplaysAndMarksApplied()
    {
        var record = CreateOutboxRecord(SampleRelation);
        var fakeOutbox = new FakeRelationOutboxStore(record);
        // GetAsync 返回 null——模拟 relation 未落库
        var fakeRelationStore = new FakeRelationStore(relation: null);
        var fakeProjectionWriter = new FakeRelationProjectionWriter();

        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeOutbox, fakeRelationStore, fakeProjectionWriter, enabled: true);

        var worker = provider.GetRequiredService<RelationReconciliationWorker>();
        await worker.StartAsync(cts.Token);

        await fakeOutbox.MarkAppliedCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, fakeProjectionWriter.WriteCalls, "缺失 relation 应回放 projectionWriter.WriteAsync");
        Assert.AreEqual(1, fakeOutbox.MarkAppliedCalls, "回放后应调用 MarkAppliedAsync");
        Assert.AreEqual(0, fakeOutbox.MarkFailedCalls, "回放成功不应调用 MarkFailedAsync");
    }

    /// <summary>Payload 为 null → MarkFailedAsync（无法回放）。</summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WhenPayloadIsNull_CallsMarkFailed()
    {
        var record = CreateOutboxRecord(payload: null);
        var fakeOutbox = new FakeRelationOutboxStore(record);
        var fakeRelationStore = new FakeRelationStore(relation: null);
        var fakeProjectionWriter = new FakeRelationProjectionWriter();

        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeOutbox, fakeRelationStore, fakeProjectionWriter, enabled: true);

        var worker = provider.GetRequiredService<RelationReconciliationWorker>();
        await worker.StartAsync(cts.Token);

        await fakeOutbox.MarkFailedCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, fakeOutbox.MarkFailedCalls, "Payload 为 null 应调用 MarkFailedAsync");
        Assert.AreEqual(0, fakeOutbox.MarkAppliedCalls, "Payload 为 null 不应调用 MarkAppliedAsync");
        Assert.AreEqual(0, fakeProjectionWriter.WriteCalls, "Payload 为 null 不应回放 projectionWriter.WriteAsync");
    }

    /// <summary>RenewHeartbeatAsync 返回 false（租约丢失）→ 不调用 MarkApplied/MarkFailed。</summary>
    /// <remarks>    /// 验证租约丢失时 worker 中止当前记录处理——保留 state='Dispatched'，
    /// 让其他 worker 通过 AcquirePendingAsync 抢占（Dispatched + lease_expired）。
    /// </remarks>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WhenHeartbeatReturnsFalse_AbortsRecord()
    {
        var record = CreateOutboxRecord(SampleRelation);
        // renewResult=false：模拟租约丢失
        // 但为了触发 heartbeat，需要让 leaseDuration > heartbeatInterval 让 worker 进入续约循环
        // 然而续约失败时 worker 会取消处理。我们需要让 worker 等待足够长以触发 heartbeat。
        // 由于处理很快（relation 已存在且匹配 → 立即 MarkApplied），很难触发 heartbeat。
        // 为模拟慢处理，让 GetAsync 阻塞一段时间。
        var fakeOutbox = new FakeRelationOutboxStore(record, renewResult: false);
        var slowRelationStore = new FakeRelationStore(SampleRelation, delay: TimeSpan.FromMilliseconds(500));
        var fakeProjectionWriter = new FakeRelationProjectionWriter();

        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(
            fakeOutbox,
            slowRelationStore,
            fakeProjectionWriter,
            enabled: true,
            heartbeatInterval: TimeSpan.FromMilliseconds(50));

        var worker = provider.GetRequiredService<RelationReconciliationWorker>();
        await worker.StartAsync(cts.Token);

        // 等待 heartbeat 至少被调用一次
        await fakeOutbox.RenewHeartbeatCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(fakeOutbox.RenewHeartbeatCalls >= 1, "RenewHeartbeatAsync 应至少被调用一次");
        // 租约丢失时 worker 不应调用 MarkApplied/MarkFailed——保留 Dispatched 状态供其他 worker 抢占
        Assert.AreEqual(0, fakeOutbox.MarkAppliedCalls, "租约丢失时不应调用 MarkAppliedAsync");
        Assert.AreEqual(0, fakeOutbox.MarkFailedCalls, "租约丢失时不应调用 MarkFailedAsync");
    }

    /// <summary>
    /// 验证满批续扫：outbox 积压超过一批时，worker 在启动首轮内连续排空全部批次，
    /// 不等待轮询间隔（IntervalSeconds 配置为 60s，排空发生在数秒内）。
    /// </summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WhenBacklogExceedsBatch_DrainsWithoutWaitingInterval()
    {
        const int batchSize = 10;
        const int fullBatches = 3;
        var fakeOutbox = new MultiBatchRelationOutboxStore(fullBatches, batchSize);
        var fakeRelationStore = new FakeRelationStore(SampleRelation);
        var fakeProjectionWriter = new FakeRelationProjectionWriter();

        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeOutbox, fakeRelationStore, fakeProjectionWriter, enabled: true);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var worker = provider.GetRequiredService<RelationReconciliationWorker>();
        await worker.StartAsync(cts.Token);

        // 满批 3 次 + 队尾探测 1 次 = 4 次 AcquirePending；全部应在 10s 窗口内完成。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && Volatile.Read(ref fakeOutbox.AcquirePendingCalls) < fullBatches + 1)
        {
            await Task.Delay(50);
        }
        stopwatch.Stop();
        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(fakeOutbox.AcquirePendingCalls >= fullBatches + 1,
            "满批后应立即续扫到队尾探测（不应等待 60s 轮询间隔）。");
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            "积压应在启动后数秒内排空，而非等待一个轮询间隔。");
        Assert.AreEqual(fullBatches * batchSize, fakeOutbox.MarkAppliedCalls, "全部积压记录应被标记 Applied。");
    }

    private static ServiceProvider BuildServiceProvider(
        IRelationOutboxStore? outboxStore,
        IRelationStore? relationStore,
        IRelationProjectionWriter? projectionWriter,
        bool enabled,
        TimeSpan? heartbeatInterval = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        if (outboxStore is not null) services.AddSingleton(outboxStore);
        if (relationStore is not null) services.AddSingleton(relationStore);
        if (projectionWriter is not null) services.AddSingleton(projectionWriter);
        services.Configure<RelationReconciliationOptions>(o =>
        {
            o.Enabled = enabled;
            o.IntervalSeconds = 60;  // 测试不需要后续轮询
            o.BatchSize = 10;
            o.RunOnStartup = true;
            o.LeaseDuration = TimeSpan.FromSeconds(5);
            o.HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(2);
            o.OwnerId = "test-worker";
        });
        services.AddSingleton<RelationReconciliationWorker>();
        return services.BuildServiceProvider();
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    /// <summary>
    /// 多批 outbox：前 <see cref="_fullBatches"/> 次 AcquirePendingAsync 各返回满批
    /// <see cref="_batchSize"/> 条记录，之后返回空批——模拟积压场景。
    /// </summary>
    private sealed class MultiBatchRelationOutboxStore : IRelationOutboxStore
    {
        private readonly RelationOutboxRecord _record;
        private readonly int _fullBatches;
        private readonly int _batchSize;
        private int _batchesServed;
        public int AcquirePendingCalls;
        public int MarkAppliedCalls;

        public MultiBatchRelationOutboxStore(int fullBatches, int batchSize)
        {
            _fullBatches = fullBatches;
            _batchSize = batchSize;
            _record = CreateOutboxRecord(SampleRelation);
        }

        public Task EnqueueAsync(RelationOutboxRecord record, IWriteTransactionScope? scope = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EnqueueBatchAsync(IReadOnlyList<RelationOutboxRecord> records, IWriteTransactionScope? scope = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<RelationOutboxRecord>> AcquirePendingAsync(int limit, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AcquirePendingCalls);
            var served = Interlocked.Increment(ref _batchesServed);
            if (served <= _fullBatches)
            {
                var batch = new List<RelationOutboxRecord>(_batchSize);
                for (var i = 0; i < _batchSize; i++)
                {
                    batch.Add(CreateOutboxRecord(SampleRelation, outboxId: _record.OutboxId + "-" + served + "-" + i));
                }
                return Task.FromResult<IReadOnlyList<RelationOutboxRecord>>(batch);
            }
            return Task.FromResult<IReadOnlyList<RelationOutboxRecord>>(Array.Empty<RelationOutboxRecord>());
        }

        public Task<bool> MarkAppliedAsync(string outboxId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref MarkAppliedCalls);
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailedAsync(string outboxId, string errorMessage, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewHeartbeatAsync(string outboxId, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<string>> RenewHeartbeatBatchAsync(
            IReadOnlyList<RelationOutboxHeartbeat> heartbeats, TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<int> CountStaleLeasesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<IReadOnlyDictionary<string, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
    }

    private sealed class FakeRelationOutboxStore : IRelationOutboxStore
    {
        private readonly RelationOutboxRecord? _record;
        private readonly bool _renewResult;
        private int _acquired;
        public int AcquirePendingCalls;
        public int MarkAppliedCalls;
        public int MarkFailedCalls;
        public int RenewHeartbeatCalls;
        public readonly TaskCompletionSource<bool> MarkAppliedCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> MarkFailedCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> RenewHeartbeatCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeRelationOutboxStore(RelationOutboxRecord? record = null, bool renewResult = true)
        {
            _record = record;
            _renewResult = renewResult;
        }

        public Task EnqueueAsync(RelationOutboxRecord record, IWriteTransactionScope? scope = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EnqueueBatchAsync(IReadOnlyList<RelationOutboxRecord> records, IWriteTransactionScope? scope = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<RelationOutboxRecord>> AcquirePendingAsync(int limit, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AcquirePendingCalls);
            IReadOnlyList<RelationOutboxRecord> result;
            if (_record is not null && Interlocked.CompareExchange(ref _acquired, 1, 0) == 0)
            {
                result = new[] { _record };
            }
            else
            {
                result = Array.Empty<RelationOutboxRecord>();
            }
            return Task.FromResult(result);
        }

        public Task<bool> MarkAppliedAsync(string outboxId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref MarkAppliedCalls);
            MarkAppliedCalled.TrySetResult(true);
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailedAsync(string outboxId, string errorMessage, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref MarkFailedCalls);
            MarkFailedCalled.TrySetResult(true);
            return Task.FromResult(true);
        }

        public Task<bool> RenewHeartbeatAsync(string outboxId, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RenewHeartbeatCalls);
            RenewHeartbeatCalled.TrySetResult(true);
            return Task.FromResult(_renewResult);
        }

        public Task<IReadOnlyList<string>> RenewHeartbeatBatchAsync(
            IReadOnlyList<RelationOutboxHeartbeat> heartbeats, TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RenewHeartbeatCalls);
            RenewHeartbeatCalled.TrySetResult(true);
            if (!_renewResult)
            {
                return Task.FromResult<IReadOnlyList<string>>(
                    heartbeats.Select(h => h.OutboxId).ToList());
            }
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        public Task<int> CountStaleLeasesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<IReadOnlyDictionary<string, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
    }

    private sealed class FakeRelationStore : IRelationStore
    {
        private readonly ContextRelation? _relation;
        private readonly TimeSpan _delay;

        public FakeRelationStore(ContextRelation? relation, TimeSpan? delay = null)
        {
            _relation = relation;
            _delay = delay ?? TimeSpan.Zero;
        }

        public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task<ContextRelation?> GetAsync(string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, cancellationToken);
            return _relation;
        }

        public Task<IReadOnlyList<ContextRelation>> QueryAsync(ContextRelationQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextRelation>>(Array.Empty<ContextRelation>());

        public Task<bool> DeleteAsync(string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task BatchUpsertAsync(IEnumerable<ContextRelation> relations, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ContextRelation>> QueryNeighborsAsync(RelationNeighborQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ContextRelation>>(Array.Empty<ContextRelation>());

        public Task<IReadOnlyList<RelationNeighborBatchResult>> QueryNeighborsBatchAsync(RelationNeighborBatchQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RelationNeighborBatchResult>>(Array.Empty<RelationNeighborBatchResult>());
    }

    private sealed class FakeRelationProjectionWriter : IRelationProjectionWriter
    {
        public int WriteCalls;

        public Task<RelationProjectionWriteResult> WriteAsync(IReadOnlyList<ContextRelation> relations, string provenance, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref WriteCalls);
            return Task.FromResult(new RelationProjectionWriteResult
            {
                Provenance = provenance,
                RequestedCount = relations.Count,
                WrittenCount = relations.Count,
                SkippedCount = 0,
                IsValid = true,
                Diagnostics = Array.Empty<RelationProjectorOutputDiagnostic>(),
                SkippedRelationIds = Array.Empty<string>()
            });
        }
    }
}
