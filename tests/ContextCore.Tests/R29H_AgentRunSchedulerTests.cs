using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

// ===========================================================================
// Agent Run Scheduler Truth 测试
//
// 验证 P0-6：AgentKernelHost 实现 IAgentRunScheduler，提供非阻塞入队
// TryEnqueueAsync + AgentRunEnqueueResult（队列满 / 已关闭 / 已活跃 / 已入队），
// 消除"队列满时 HTTP 请求无限等待槽位"的阻塞问题：
//   1. Accepted：入队后由 worker 执行到终态；
//   2. AlreadyActive：同进程内重复入队幂等跳过；
//   3. QueueFull：队列满时立即返回（无无限等待），且失败路径清理 _activeRuns；
//   4. Closed：Host 已 Dispose 后拒绝入队；
//   5. 指标：QueueDepth / QueueCapacity / ActiveRunCount / WorkerCount 反映调度状态；
//   6. DI：IAgentRunScheduler 与 AgentKernelHost 同实例注册；
//   7. 配置：AgentHostOptionsDefaultFactory 绑定 ChannelCapacity / WorkerCount / DrainTimeout。
// ===========================================================================

[TestClass]
[TestCategory("Kill-Point")]
[TestCategory("External-Effect-Truth")]
public sealed class R29H_AgentRunSchedulerTests
{
    private const string Ws = "ws-scheduler";

    // ── 1. Accepted：入队并执行到终态 ─────────────────────────────────────

    /// <summary>
    /// 验证：TryEnqueueAsync 返回 Accepted，worker 执行 Run 到 Completed 终态，
    /// 完成后活跃 Run 计数归零（无残留）。
    /// </summary>
    [TestMethod]
    public async Task Scheduler_TryEnqueue_Accepted_ExecutesRunToCompletion()
    {
        await using var harness = await SchedulerHarness.CreateAsync(trackTransport: false);

        var run = BuildRun("调度执行测试");
        await harness.RunStore.CreateAsync(run);

        var result = await harness.Host.TryEnqueueAsync(run, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, result.Status, "入队应成功。");
        Assert.AreEqual(run.RunId, result.RunId, "结果应透传 RunId。");
        Assert.IsTrue(result.QueueDepth >= 0, "应携带队列深度快照。");

        var finalRun = await harness.WaitForTerminalAsync(run).ConfigureAwait(false);
        Assert.AreEqual(AgentRunState.Completed, finalRun.State,
            $"Run 应执行到 Completed，实际 {finalRun.State}。");
        Assert.AreEqual(0, harness.Host.ActiveRunCount, "完成后活跃 Run 应归零。");
    }

    // ── 2. AlreadyActive：幂等跳过 ───────────────────────────────────────

    /// <summary>
    /// 验证：同一 Run 在活跃期间重复入队 → AlreadyActive（幂等跳过，不重复启动）。
    /// </summary>
    [TestMethod]
    public async Task Scheduler_TryEnqueue_SameRunTwice_SecondIsAlreadyActive()
    {
        await using var harness = await SchedulerHarness.CreateAsync(trackTransport: true);

        var run = BuildRun("重复入队测试");
        await harness.RunStore.CreateAsync(run);

        var first = await harness.Host.TryEnqueueAsync(run, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, first.Status);

        // 等待 worker 拾取 Run（transport 阻塞中，Run 处于活跃态）
        await harness.WaitForTransportCallAsync().ConfigureAwait(false);

        var second = await harness.Host.TryEnqueueAsync(run, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(AgentRunEnqueueStatus.AlreadyActive, second.Status,
            "活跃期间重复入队应幂等跳过。");

        harness.Release();
        await harness.WaitForTerminalAsync(run).ConfigureAwait(false);
    }

    // ── 3. QueueFull：无无限等待 + 失败路径清理 ──────────────────────────

    /// <summary>
    /// 验证：队列满时 TryEnqueueAsync 立即返回 QueueFull（不无限等待槽位），
    /// 且被拒绝的 Run 不残留 _activeRuns 跟踪。
    /// </summary>
    [TestMethod]
    public async Task Scheduler_TryEnqueue_QueueFull_ReturnsPromptly_NoIndefiniteWait()
    {
        await using var harness = await SchedulerHarness.CreateAsync(
            trackTransport: true, channelCapacity: 1, workerCount: 1);

        var runA = BuildRun("队列满测试 A");
        var runB = BuildRun("队列满测试 B");
        var runC = BuildRun("队列满测试 C");
        await harness.RunStore.CreateAsync(runA);
        await harness.RunStore.CreateAsync(runB);
        await harness.RunStore.CreateAsync(runC);

        // Run A：worker 拾取后阻塞在 transport（活跃）
        var a = await harness.Host.TryEnqueueAsync(runA, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, a.Status);
        await harness.WaitForTransportCallAsync().ConfigureAwait(false);

        // Run B：填满 1 槽 channel（排队）
        var b = await harness.Host.TryEnqueueAsync(runB, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, b.Status);

        // Run C：队列已满 → 立即 QueueFull（关键断言：调用不阻塞）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var c = await harness.Host.TryEnqueueAsync(runC, CancellationToken.None).ConfigureAwait(false);
        sw.Stop();

        Assert.AreEqual(AgentRunEnqueueStatus.QueueFull, c.Status, "队列满应返回 QueueFull。");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"QueueFull 应立即返回（不应无限等待槽位），实际耗时 {sw.Elapsed.TotalMilliseconds:F0} ms。");
        Assert.AreEqual(2, harness.Host.ActiveRunCount,
            "被拒绝的 Run 不应残留 _activeRuns 跟踪（仅 A/B 活跃）。");
        StringAssert.Contains(c.Detail, "队列已满");

        // 释放阻塞 → 全部执行完成
        harness.Release();
        await harness.WaitForTerminalAsync(runA).ConfigureAwait(false);
        await harness.WaitForTerminalAsync(runB).ConfigureAwait(false);
        Assert.AreEqual(0, harness.Host.ActiveRunCount, "全部完成后活跃 Run 应归零。");
    }

    // ── 4. Closed：Host 已 Dispose 后拒绝入队 ────────────────────────────

    /// <summary>
    /// 验证：Host Dispose 后 TryEnqueueAsync 返回 Closed（不抛异常），
    /// StartRunAsync 保持旧契约抛 InvalidOperationException。
    /// </summary>
    [TestMethod]
    public async Task Scheduler_TryEnqueue_AfterDispose_ReturnsClosed()
    {
        var harness = await SchedulerHarness.CreateAsync(trackTransport: false);
        var run = BuildRun("关闭后入队测试");
        await harness.RunStore.CreateAsync(run);

        await harness.Host.DisposeAsync().ConfigureAwait(false);

        var result = await harness.Host.TryEnqueueAsync(run, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(AgentRunEnqueueStatus.Closed, result.Status, "Dispose 后应返回 Closed。");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Host.StartRunAsync(run, CancellationToken.None)).ConfigureAwait(false);
    }

    // ── 5. 调度指标 ──────────────────────────────────────────────────────

    /// <summary>
    /// 验证：QueueDepth / QueueCapacity / ActiveRunCount / WorkerCount 反映调度状态。
    /// </summary>
    [TestMethod]
    public async Task Scheduler_QueueMetrics_ReflectSchedulingState()
    {
        await using var harness = await SchedulerHarness.CreateAsync(
            trackTransport: true, channelCapacity: 1, workerCount: 1);

        var runA = BuildRun("指标测试 A");
        var runB = BuildRun("指标测试 B");
        await harness.RunStore.CreateAsync(runA);
        await harness.RunStore.CreateAsync(runB);

        Assert.AreEqual(0, harness.Host.ActiveRunCount, "初始无活跃 Run。");
        Assert.AreEqual(0, harness.Host.QueueDepth, "初始队列为空。");
        Assert.AreEqual(1, harness.Host.QueueCapacity, "队列容量应取配置值。");
        Assert.AreEqual(1, harness.Host.WorkerCount, "worker 数应取配置值。");

        await harness.Host.TryEnqueueAsync(runA, CancellationToken.None).ConfigureAwait(false);
        await harness.WaitForTransportCallAsync().ConfigureAwait(false);
        await harness.Host.TryEnqueueAsync(runB, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, harness.Host.ActiveRunCount, "A 活跃 + B 排队 → 2 个活跃跟踪。");
        Assert.AreEqual(1, harness.Host.QueueDepth, "B 在队列中 → 深度 1。");

        harness.Release();
        await harness.WaitForTerminalAsync(runA).ConfigureAwait(false);
        await harness.WaitForTerminalAsync(runB).ConfigureAwait(false);
    }

    // ── 6. DI：IAgentRunScheduler 与 Host 同实例 ─────────────────────────

    /// <summary>
    /// 验证：IAgentRunScheduler 经 DI 解析到与 AgentKernelHost 同一实例。
    /// </summary>
    [TestMethod]
    public async Task DI_AgentRunScheduler_ResolvesToSameAgentKernelHostInstance()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development"
        });

        var services = new ServiceCollection();
        services.AddContextCore();
        services.AddContextCoreRuntime(config);
        await using var provider = services.BuildServiceProvider();

        var scheduler = provider.GetRequiredService<IAgentRunScheduler>();
        var host = provider.GetRequiredService<AgentKernelHost>();

        Assert.IsTrue(ReferenceEquals(scheduler, host),
            "IAgentRunScheduler 与 AgentKernelHost 应为同一实例（调度抽象与实现同源）。");
    }

    // ── 7. 配置绑定：ChannelCapacity / WorkerCount / DrainTimeout ────────

    /// <summary>
    /// 验证：AgentHostOptionsDefaultFactory 从 "AgentHost" 配置节绑定
    /// ChannelCapacity / WorkerCount / DrainTimeout。
    /// </summary>
    [TestMethod]
    public void HostOptions_DefaultFactory_BindsChannelCapacityWorkerCountDrainTimeout()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development",
            ["AgentHost:ChannelCapacity"] = "42",
            ["AgentHost:WorkerCount"] = "7",
            ["AgentHost:DrainTimeout"] = "00:00:05"
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddContextCore();
        services.AddContextCoreRuntime(config);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<AgentHostOptions>();

        Assert.AreEqual(42, options.ChannelCapacity, "ChannelCapacity 应从配置绑定。");
        Assert.AreEqual(7, options.WorkerCount, "WorkerCount 应从配置绑定。");
        Assert.AreEqual(TimeSpan.FromSeconds(5), options.DrainTimeout, "DrainTimeout 应从配置绑定。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = Ws,
        SessionId = "session-scheduler",
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 10
        }
    };

    /// <summary>Host + Store + 可选阻塞 transport 的组合测试夹具。</summary>
    private sealed class SchedulerHarness : IAsyncDisposable
    {
        private readonly BlockingModelTransport? _transport;
        private readonly AgentRunStoreWrapper _store;

        public AgentRunStoreWrapper RunStore => _store;
        public AgentKernelHost Host { get; }

        private SchedulerHarness(
            AgentRunStoreWrapper store,
            AgentKernelHost host,
            BlockingModelTransport? transport)
        {
            _store = store;
            Host = host;
            _transport = transport;
        }

        public static async Task<SchedulerHarness> CreateAsync(
            bool trackTransport,
            int channelCapacity = 8,
            int workerCount = 2)
        {
            var store = new AgentRunStoreWrapper();
            var eventStore = new InMemoryAgentRunEventStore(store.Inner);

            var services = new ServiceCollection();
            services.AddSingleton<IAgentRunStore>(store);
            services.AddSingleton<IAgentRunEventStore>(eventStore);
            services.AddSingleton<IToolDispatcher>(new EchoToolDispatcher());
            services.AddSingleton<AgentKernelHost>();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

            BlockingModelTransport? transport = null;
            if (trackTransport)
            {
                transport = new BlockingModelTransport();
                services.AddSingleton<IAgentModelTransport>(transport);
            }
            else
            {
                services.AddSingleton<IAgentModelTransport>(new DeterministicAgentModelTransport());
            }

            var options = new AgentHostOptions
            {
                ChannelCapacity = channelCapacity,
                WorkerCount = workerCount,
                DrainTimeout = TimeSpan.FromSeconds(5)
            };
            services.AddSingleton(options);

            var provider = services.BuildServiceProvider();
            var host = provider.GetRequiredService<AgentKernelHost>();

            return new SchedulerHarness(store, host, transport);
        }

        public async Task WaitForTransportCallAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_transport is not null && _transport.CallCount == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
            Assert.IsTrue(_transport is not null && _transport.CallCount > 0,
                "worker 应在超时前拾取 Run 并进入 transport 调用。");
        }

        public void Release()
        {
            _transport?.Complete(
                new AgentModelResponse
                {
                    Content = "完成",
                    ToolCalls = Array.Empty<AgentToolCallRequest>(),
                    IsFinalAnswer = true,
                    TokensConsumed = 3,
                    Duration = TimeSpan.FromMilliseconds(1)
                });
        }

        public async Task<AgentRun> WaitForTerminalAsync(AgentRun run)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            AgentRun? current = null;
            while (DateTime.UtcNow < deadline)
            {
                current = await _store.GetAsync(run.WorkspaceId, run.RunId).ConfigureAwait(false);
                if (current is not null && AgentRunStateMachine.IsTerminalState(current.State))
                {
                    return current;
                }
                await Task.Delay(50).ConfigureAwait(false);
            }
            Assert.Fail($"Run 未在超时前进入终态，最后状态 {current?.State}。");
            throw new InvalidOperationException("unreachable");
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>IAgentRunStore 包装（暴露 Inner 供事件流使用）。</summary>
    private sealed class AgentRunStoreWrapper : IAgentRunStore
    {
        public InMemoryAgentRunStore Inner { get; } = new();

        public ValueTask CreateAsync(AgentRun run, CancellationToken cancellationToken = default)
            => Inner.CreateAsync(run, cancellationToken);
        public ValueTask<AgentRun?> GetAsync(string workspaceId, string runId, CancellationToken cancellationToken = default)
            => Inner.GetAsync(workspaceId, runId, cancellationToken);
        public ValueTask<AgentRun?> GetByIdempotencyKeyAsync(string workspaceId, string idempotencyKey, CancellationToken cancellationToken = default)
            => Inner.GetByIdempotencyKeyAsync(workspaceId, idempotencyKey, cancellationToken);
        public ValueTask<AgentRunCreateResult> CreateOrGetByIdempotencyKeyAsync(AgentRun run, CancellationToken ct = default)
            => Inner.CreateOrGetByIdempotencyKeyAsync(run, ct);
        public ValueTask TransitionStateAsync(
            string workspaceId, string runId, AgentRunState expectedState, AgentRunState newState,
            CancellationToken cancellationToken = default, string? leaseToken = null, long? fencingToken = null)
            => Inner.TransitionStateAsync(workspaceId, runId, expectedState, newState, cancellationToken, leaseToken, fencingToken);
        public ValueTask UpdateAsync(AgentRun run, CancellationToken cancellationToken = default)
            => Inner.UpdateAsync(run, cancellationToken);
        public ValueTask<IReadOnlyList<AgentRun>> ListBySessionAsync(string workspaceId, string sessionId, CancellationToken cancellationToken = default)
            => Inner.ListBySessionAsync(workspaceId, sessionId, cancellationToken);
        public ValueTask<IReadOnlyList<AgentRun>> ListByStateAsync(
            AgentRunState state, int take = 100,
            DateTimeOffset? afterUpdatedAt = null, string? afterRunId = null,
            CancellationToken cancellationToken = default)
            => Inner.ListByStateAsync(state, take, afterUpdatedAt, afterRunId, cancellationToken);
    }

    /// <summary>transport stub：首次调用阻塞在 TCS，直到测试主动 Release。</summary>
    private sealed class BlockingModelTransport : IAgentModelTransport
    {
        private readonly TaskCompletionSource<AgentModelResponse> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用 AgentModelRequest 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用 AgentModelRequest 重载。");

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return new ValueTask<AgentModelResponse>(_gate.Task);
        }

        public void Complete(AgentModelResponse response)
        {
            _gate.TrySetResult(response);
        }
    }
}
