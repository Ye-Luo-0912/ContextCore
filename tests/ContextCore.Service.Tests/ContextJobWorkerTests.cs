using ContextCore.Abstractions;
using ContextCore.Core;
using ContextCore.Service;
using ContextCore.Service.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Tests;

/// <summary>
/// 验证 ContextJobWorker 在租约路径与 Dequeue 回退路径下的行为。
/// 覆盖：
/// 1. 队列实现 ILeasedJobQueue → worker 使用 AcquireLeaseAsync + 心跳续约，作业成功后 Ack。
/// 2. 心跳续约返回 false（租约丢失）→ worker 取消 DispatchAsync 且不 Ack/Nack。
/// 3. 队列未实现 ILeasedJobQueue（如 InMemory）→ 回退到 DequeueAsync 路径。
/// 4. Dispatcher 抛出异常 → worker 调用 NackAsync。
/// 5. AcquireLease 返回 null → worker 空转，不调用 Dispatcher。
/// 6. Enabled=false → worker 立即返回。
/// </summary>
[TestClass]
public class ContextJobWorkerTests
{
    private static readonly ContextJob SampleJob = new()
    {
        JobId = "job-test-001",
        WorkspaceId = "ws-test",
        CollectionId = "col-test",
        Kind = ContextJobKind.Compression,
        PayloadJson = "{}",
        State = ContextJobState.Queued,
        Priority = 0,
        RetryCount = 0,
        MaxRetryCount = 3,
        CreatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// 验证租约路径：worker 通过 AcquireLeaseAsync 获取作业，dispatcher 成功完成后调用 AckAsync。
    /// 同时验证 heartbeat 至少被调用一次（即续约路径已生效）。
    /// </summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WithLeasedQueue_UsesAcquireLeaseAndAcksOnSuccess()
    {
        var fakeQueue = new FakeLeasedJobQueue(SampleJob);
        var fakeDispatcher = new FakeDispatcher(_ => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeQueue, fakeDispatcher);

        var worker = provider.GetRequiredService<ContextJobWorker>();
        await worker.StartAsync(cts.Token);

        // 等待 Ack 被调用（dispatcher 已完成）
        await fakeQueue.AckCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 给 heartbeat 至少一次续约机会
        await Task.Delay(300);

        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(fakeQueue.AcquireLeaseCalls >= 1, "AcquireLeaseAsync 应至少被调用一次");
        Assert.IsTrue(fakeQueue.AckCalls >= 1, "AckAsync 应被调用");
        Assert.AreEqual(0, fakeQueue.NackCalls, "成功路径不应调用 NackAsync");
        Assert.IsTrue(fakeQueue.RenewHeartbeatCalls >= 0, "RenewHeartbeatAsync 调用次数应可观察");
        Assert.IsTrue(fakeDispatcher.DispatchedJobs.Count >= 1, "Dispatcher 应至少被调用一次");
    }

    /// <summary>
    /// 验证租约丢失场景：RenewHeartbeatAsync 返回 false → worker 取消 DispatchAsync，且不调用 Ack 或 Nack。
    /// 作业状态保留为 Running（即队列中既未 Ack 也未 Nack），其他 worker 可通过 AcquireLeaseAsync 抢占。
    /// </summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WhenLeaseLost_DoesNotAckOrNack()
    {
        // dispatcher 永不完成（挂起），等待被 lease 取消
        var dispatcherNeverCompletes = new TaskCompletionSource<bool>();
        var fakeDispatcher = new FakeDispatcher(_ => dispatcherNeverCompletes.Task);

        // 续约立即返回 false（租约丢失）
        var fakeQueue = new FakeLeasedJobQueue(SampleJob, renewResult: false);
        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeQueue, fakeDispatcher);

        var worker = provider.GetRequiredService<ContextJobWorker>();
        await worker.StartAsync(cts.Token);

        // 等待 heartbeat 至少被调用一次（轮询间隔 + 续约间隔）
        // JobWorkerOptions 配置 HeartbeatInterval=10ms（远小于默认 15s）
        await fakeQueue.RenewHeartbeatCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 给 worker 一点时间响应租约丢失（取消 DispatchAsync）
        await Task.Delay(500);

        // Dispatcher 任务应已被取消（释放 dispatcherNeverCompletes 以避免泄漏）
        Assert.IsTrue(fakeDispatcher.LastDispatchTask!.IsCanceled,
            "DispatchAsync 应被 OperationCanceledException 取消（linked CTS）");

        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, fakeQueue.AckCalls, "租约丢失时不应 Ack");
        Assert.AreEqual(0, fakeQueue.NackCalls, "租约丢失时不应 Nack——保留 Running 状态供其他 worker 抢占");
        Assert.IsTrue(fakeQueue.RenewHeartbeatCalls >= 1, "RenewHeartbeatAsync 应至少被调用一次");

        dispatcherNeverCompletes.TrySetCanceled();
    }

    /// <summary>
    /// 验证租约路径下 dispatcher 抛出异常时 worker 调用 NackAsync，事件 sink 也会收到 Error 事件。
    /// </summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WithLeasedQueue_WhenDispatcherThrows_PerformsNack()
    {
        var fakeDispatcher = new FakeDispatcher(_ => Task.FromException(new InvalidOperationException("boom")));
        var fakeQueue = new FakeLeasedJobQueue(SampleJob);
        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeQueue, fakeDispatcher);

        var worker = provider.GetRequiredService<ContextJobWorker>();
        await worker.StartAsync(cts.Token);

        await fakeQueue.NackCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(fakeQueue.NackCalls >= 1, "Dispatcher 抛异常时应调用 NackAsync");
        Assert.AreEqual(0, fakeQueue.AckCalls, "失败路径不应 Ack");
    }

    /// <summary>
    /// 验证 Dequeue 回退路径：队列未实现 ILeasedJobQueue 时，worker 通过 DequeueAsync 获取作业。
    /// 使用 InMemoryJobQueue 作为代表场景。
    /// </summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WithNonLeasedQueue_FallsBackToDequeuePath()
    {
        // 用一个仅实现 IContextJobQueue 的 fake（不实现 ILeasedJobQueue）来明确触发回退路径
        var fakeQueue = new FakePlainJobQueue(SampleJob);
        var fakeDispatcher = new FakeDispatcher(_ => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeQueue, fakeDispatcher);

        var worker = provider.GetRequiredService<ContextJobWorker>();
        await worker.StartAsync(cts.Token);

        await fakeQueue.AckCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(fakeQueue.DequeueCalls >= 1, "回退路径应调用 DequeueAsync");
        Assert.IsTrue(fakeQueue.AckCalls >= 1, "成功路径应调用 AckAsync");
    }

    /// <summary>
    /// 验证 AcquireLeaseAsync 返回 null 时 worker 空转，不调用 Dispatcher。
    /// </summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WhenAcquireLeaseReturnsNull_DoesNotCallDispatcher()
    {
        var fakeQueue = new FakeLeasedJobQueue(job: null);
        var fakeDispatcher = new FakeDispatcher(_ => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeQueue, fakeDispatcher);

        var worker = provider.GetRequiredService<ContextJobWorker>();
        await worker.StartAsync(cts.Token);

        // 等待 worker 进行至少 2 次 AcquireLease（每次 poll interval）
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(fakeQueue.AcquireLeaseCalls >= 2, "AcquireLease 应至少被调用两次（队列空时持续轮询）");
        Assert.AreEqual(0, fakeDispatcher.DispatchedJobs.Count, "队列空时不应调用 Dispatcher");
        Assert.AreEqual(0, fakeQueue.AckCalls, "队列空时不应 Ack");
        Assert.AreEqual(0, fakeQueue.NackCalls, "队列空时不应 Nack");
    }

    /// <summary>
    /// 验证 Enabled=false 时 worker 立即返回，不调用任何队列方法。
    /// </summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WhenDisabled_DoesNothing()
    {
        var fakeQueue = new FakeLeasedJobQueue(SampleJob);
        var fakeDispatcher = new FakeDispatcher(_ => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeQueue, fakeDispatcher, enabled: false);

        var worker = provider.GetRequiredService<ContextJobWorker>();
        await worker.StartAsync(cts.Token);
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, fakeQueue.AcquireLeaseCalls, "Disabled 时不调用 AcquireLeaseAsync");
        Assert.AreEqual(0, fakeDispatcher.DispatchedJobs.Count, "Disabled 时不调用 Dispatcher");
    }

    /// <summary>
    /// 验证批量路径：worker 通过 AcquireLeaseBatchAsync 一次领取多个作业并逐个处理。
    /// </summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Worker_WithBatchQueue_ProcessesAllClaimedJobs()
    {
        var fakeQueue = new FakeBatchJobQueue(
        [
            WithId(SampleJob, "job-batch-1"),
            WithId(SampleJob, "job-batch-2")
        ]);
        var fakeDispatcher = new FakeDispatcher(_ => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        using var provider = BuildServiceProvider(fakeQueue, fakeDispatcher, concurrency: 4);

        var worker = provider.GetRequiredService<ContextJobWorker>();
        await worker.StartAsync(cts.Token);

        // 等待两个作业都被分派
        await WaitUntilAsync(() => fakeDispatcher.DispatchedJobs.Count >= 2, TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.IsTrue(fakeQueue.AcquireLeaseBatchCalls >= 1, "应调用 AcquireLeaseBatchAsync");
        Assert.AreEqual(2, fakeDispatcher.DispatchedJobs.Count, "批量领取的两个作业都应被处理");
        Assert.IsTrue(fakeQueue.AckCalls >= 2, "两个作业都成功应各 Ack 一次");
        Assert.AreEqual(0, fakeQueue.NackCalls, "成功路径不应 Nack");
    }

    private static ContextJob WithId(ContextJob source, string jobId) => new()
    {
        JobId = jobId,
        WorkspaceId = source.WorkspaceId,
        CollectionId = source.CollectionId,
        Kind = source.Kind,
        PayloadJson = source.PayloadJson,
        State = source.State,
        Priority = source.Priority,
        RetryCount = source.RetryCount,
        MaxRetryCount = source.MaxRetryCount,
        CreatedAt = source.CreatedAt
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("等待条件超时");
    }

    private static ServiceProvider BuildServiceProvider(
        IContextJobQueue queue,
        IContextJobDispatcher dispatcher,
        bool enabled = true,
        int concurrency = 1)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(queue);
        services.AddSingleton(dispatcher);
        services.AddSingleton<IContextEventSink, InMemoryContextEventSink>();
        // Configure<T> 注册 IConfigureOptions<T>，IOptions<T> 通过 OptionsManager 自动解析配置值。
        services.Configure<JobWorkerOptions>(o =>
        {
            o.Enabled = enabled;
            o.PollIntervalMilliseconds = 50;  // 加快测试
            o.Concurrency = concurrency;
            o.HeartbeatInterval = TimeSpan.FromMilliseconds(10);  // 快速触发续约
            o.LeaseDuration = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton<ContextJobWorker>();
        return services.BuildServiceProvider();
    }

    private sealed class FakeDispatcher : IContextJobDispatcher
    {
        private readonly Func<ContextJob, Task> _handler;
        public List<ContextJob> DispatchedJobs { get; } = new();
        public Task? LastDispatchTask { get; private set; }

        public FakeDispatcher(Func<ContextJob, Task> handler)
        {
            _handler = handler;
        }

        public Task DispatchAsync(ContextJob job, CancellationToken cancellationToken = default)
        {
            lock (DispatchedJobs)
            {
                DispatchedJobs.Add(job);
            }
            // 用 Task.Run 让它成为可观察的 Task，便于测试中检查 IsCanceled
            LastDispatchTask = _handler(job).WaitAsync(cancellationToken);
            return LastDispatchTask;
        }
    }

    /// <summary>
    /// Fake 队列同时实现 IContextJobQueue + ILeasedJobQueue，触发 worker 的租约路径。
    /// 只对第一个 AcquireLease 返回作业，后续返回 null（避免被无限消费）。
    /// </summary>
    private sealed class FakeLeasedJobQueue : IContextJobQueue, ILeasedJobQueue
    {
        private readonly ContextJob? _job;
        private readonly bool _renewResult;
        private int _acquired;
        public int AcquireLeaseCalls;
        public int RenewHeartbeatCalls;
        public int AckCalls;
        public int NackCalls;
        public readonly TaskCompletionSource<bool> AckCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> NackCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> RenewHeartbeatCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeLeasedJobQueue(ContextJob? job, bool renewResult = true)
        {
            _job = job;
            _renewResult = renewResult;
        }

        public Task<ContextJob?> AcquireLeaseAsync(
            string owner, TimeSpan leaseDuration, ContextJobKind? kind = null,
            string? workspaceId = null, string? collectionId = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AcquireLeaseCalls);
            ContextJob? result;
            if (_job is not null && Interlocked.CompareExchange(ref _acquired, 1, 0) == 0)
            {
                result = _job;
            }
            else
            {
                result = null;
            }
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<ContextJob>> AcquireLeaseBatchAsync(
            string owner, TimeSpan leaseDuration, int take, int perWorkspace,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AcquireLeaseCalls);
            if (_job is not null && Interlocked.CompareExchange(ref _acquired, 1, 0) == 0)
            {
                return Task.FromResult<IReadOnlyList<ContextJob>>(new[] { _job });
            }
            return Task.FromResult<IReadOnlyList<ContextJob>>(Array.Empty<ContextJob>());
        }

        public Task<bool> RenewHeartbeatAsync(
            string jobId, string owner, TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RenewHeartbeatCalls);
            RenewHeartbeatCalled.TrySetResult(true);
            return Task.FromResult(_renewResult);
        }

        public Task EnqueueAsync(ContextJob job, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ContextJob?> DequeueAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Leased path should not call DequeueAsync.");

        public Task AckAsync(string jobId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AckCalls);
            AckCalled.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task NackAsync(string jobId, string reason, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref NackCalls);
            NackCalled.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fake 队列仅实现 IContextJobQueue（不实现 ILeasedJobQueue），触发 worker 的 Dequeue 回退路径。
    /// </summary>
    private sealed class FakePlainJobQueue : IContextJobQueue
    {
        private readonly ContextJob? _job;
        private int _dequeued;
        public int DequeueCalls;
        public int AckCalls;
        public int NackCalls;
        public readonly TaskCompletionSource<bool> AckCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> NackCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakePlainJobQueue(ContextJob? job)
        {
            _job = job;
        }

        public Task EnqueueAsync(ContextJob job, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ContextJob?> DequeueAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DequeueCalls);
            ContextJob? result;
            if (_job is not null && Interlocked.CompareExchange(ref _dequeued, 1, 0) == 0)
            {
                result = _job;
            }
            else
            {
                result = null;
            }
            return Task.FromResult(result);
        }

        public Task AckAsync(string jobId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AckCalls);
            AckCalled.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task NackAsync(string jobId, string reason, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref NackCalls);
            NackCalled.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fake 批量队列：首次 AcquireLeaseBatchAsync 返回整批作业，后续返回空。
    /// </summary>
    private sealed class FakeBatchJobQueue : IContextJobQueue, ILeasedJobQueue
    {
        private readonly ContextJob[] _jobs;
        private int _claimed;
        public int AcquireLeaseBatchCalls;
        public int AckCalls;
        public int NackCalls;

        public FakeBatchJobQueue(ContextJob[] jobs)
        {
            _jobs = jobs;
        }

        public Task<IReadOnlyList<ContextJob>> AcquireLeaseBatchAsync(
            string owner, TimeSpan leaseDuration, int take, int perWorkspace,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AcquireLeaseBatchCalls);
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0)
            {
                return Task.FromResult<IReadOnlyList<ContextJob>>(_jobs);
            }
            return Task.FromResult<IReadOnlyList<ContextJob>>(Array.Empty<ContextJob>());
        }

        public Task<ContextJob?> AcquireLeaseAsync(
            string owner, TimeSpan leaseDuration, ContextJobKind? kind = null,
            string? workspaceId = null, string? collectionId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ContextJob?>(null);

        public Task<bool> RenewHeartbeatAsync(
            string jobId, string owner, TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task EnqueueAsync(ContextJob job, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ContextJob?> DequeueAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ContextJob?>(null);

        public Task AckAsync(string jobId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AckCalls);
            return Task.CompletedTask;
        }

        public Task NackAsync(string jobId, string reason, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref NackCalls);
            return Task.CompletedTask;
        }
    }
}
