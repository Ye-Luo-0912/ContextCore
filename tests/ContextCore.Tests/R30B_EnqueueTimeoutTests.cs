using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

// ===========================================================================
// Enqueue Timeout 验收测试
//
// 验收："队列满时 HTTP 在确定时间内返回 202/429，不无限等待"。
//
// 覆盖：
// 1. TryEnqueueAsync（HTTP 入口）：队列满立即返回 QueueFull（不等待槽位）；
// 2. EnqueueAsync(timeout=0)：等价于非阻塞 TryEnqueueAsync（立即返回）；
// 3. EnqueueAsync(timeout>0)：等待槽位最多 timeout，超时仍满 → QueueFull，
// 且耗时在 [timeout 附近, 有限上界] 内（有界等待，绝不无限阻塞）；
// 4. EnqueueAsync 槽位可得时正常 Accepted；
// 5. Host 关闭后 EnqueueAsync 返回 Closed（不抛异常）。
// ===========================================================================

[TestClass]
[TestCategory("Kill-Point")]
[TestCategory("R30")]
public sealed class R30B_EnqueueTimeoutTests
{
    private const string Ws = "ws-enqueue-timeout";

    // ── 1. 非阻塞 TryEnqueueAsync：队列满立即返回 ────────────────────────

    [TestMethod]
    public async Task TryEnqueue_QueueFull_ReturnsImmediately_NoWait()
    {
        await using var harness = await Harness.CreateAsync(channelCapacity: 1, workerCount: 1);

        var runA = BuildRun("A");
        var runB = BuildRun("B");
        var runC = BuildRun("C");
        await harness.RunStore.CreateAsync(runA);
        await harness.RunStore.CreateAsync(runB);
        await harness.RunStore.CreateAsync(runC);

        // A：worker 拾取后阻塞在 transport（活跃，不占队列槽位）
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runA, CancellationToken.None)).Status);
        await harness.WaitForTransportCallAsync();

        // B：填满唯一队列槽位
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runB, CancellationToken.None)).Status);

        // C：队列满 → TryEnqueueAsync 立即返回 QueueFull（关键：无等待）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await harness.Host.TryEnqueueAsync(runC, CancellationToken.None);
        sw.Stop();

        Assert.AreEqual(AgentRunEnqueueStatus.QueueFull, result.Status);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"TryEnqueueAsync 应非阻塞立即返回，实际耗时 {sw.Elapsed.TotalMilliseconds:F0} ms。");
        StringAssert.Contains(result.Detail, "队列已满");

        harness.Release();
        await harness.WaitForTerminalAsync(runA);
        await harness.WaitForTerminalAsync(runB);
    }

    // ── 2. EnqueueAsync(timeout=0)：等价于非阻塞 ─────────────────────────

    [TestMethod]
    public async Task Enqueue_ZeroTimeout_QueueFull_ReturnsImmediately()
    {
        await using var harness = await Harness.CreateAsync(channelCapacity: 1, workerCount: 1);

        var runA = BuildRun("A");
        var runB = BuildRun("B");
        var runC = BuildRun("C");
        await harness.RunStore.CreateAsync(runA);
        await harness.RunStore.CreateAsync(runB);
        await harness.RunStore.CreateAsync(runC);

        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.EnqueueAsync(runA, TimeSpan.Zero, CancellationToken.None)).Status);
        await harness.WaitForTransportCallAsync();
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.EnqueueAsync(runB, TimeSpan.Zero, CancellationToken.None)).Status);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await harness.Host.EnqueueAsync(runC, TimeSpan.Zero, CancellationToken.None);
        sw.Stop();

        Assert.AreEqual(AgentRunEnqueueStatus.QueueFull, result.Status);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"EnqueueAsync(timeout=0) 应等价于非阻塞，实际耗时 {sw.Elapsed.TotalMilliseconds:F0} ms。");

        harness.Release();
        await harness.WaitForTerminalAsync(runA);
        await harness.WaitForTerminalAsync(runB);
    }

    // ── 3. EnqueueAsync(timeout>0)：有界等待，超时返回 QueueFull ─────────

    [TestMethod]
    public async Task Enqueue_PositiveTimeout_QueueFull_ReturnsWithinBoundedTime()
    {
        await using var harness = await Harness.CreateAsync(channelCapacity: 1, workerCount: 1);

        var runA = BuildRun("A");
        var runB = BuildRun("B");
        var runC = BuildRun("C");
        await harness.RunStore.CreateAsync(runA);
        await harness.RunStore.CreateAsync(runB);
        await harness.RunStore.CreateAsync(runC);

        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.EnqueueAsync(runA, TimeSpan.FromSeconds(5), CancellationToken.None)).Status);
        await harness.WaitForTransportCallAsync();
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.EnqueueAsync(runB, TimeSpan.FromSeconds(5), CancellationToken.None)).Status);

        // C：等待槽位最多 300ms；超时仍满 → QueueFull。
        // 断言：确实等待了（>= 250ms，说明是有界等待而非立即放弃），
        // 且耗时 < 3s（绝不无限阻塞——验收的"确定时间返回"）。
        var timeout = TimeSpan.FromMilliseconds(300);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await harness.Host.EnqueueAsync(runC, timeout, CancellationToken.None);
        sw.Stop();

        Assert.AreEqual(AgentRunEnqueueStatus.QueueFull, result.Status);
        Assert.IsTrue(sw.Elapsed >= TimeSpan.FromMilliseconds(250),
            $"EnqueueAsync 应等待槽位约 {timeout.TotalMilliseconds:F0} ms 后超时，实际仅等待 {sw.Elapsed.TotalMilliseconds:F0} ms。");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"EnqueueAsync 应在确定时间内返回（不无限等待），实际耗时 {sw.Elapsed.TotalMilliseconds:F0} ms。");
        StringAssert.Contains(result.Detail, "超时");

        harness.Release();
        await harness.WaitForTerminalAsync(runA);
        await harness.WaitForTerminalAsync(runB);
    }

    // ── 4. EnqueueAsync 槽位可得时正常 Accepted ─────────────────────────

    [TestMethod]
    public async Task Enqueue_PositiveTimeout_SlotAvailable_Accepts()
    {
        await using var harness = await Harness.CreateAsync(channelCapacity: 4, workerCount: 1);

        var run = BuildRun("空闲队列入队");
        await harness.RunStore.CreateAsync(run);

        var result = await harness.Host.EnqueueAsync(run, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, result.Status,
            "队列有槽位时应立即 Accepted。");

        harness.Release();
        await harness.WaitForTerminalAsync(run);
    }

    // ── 5. Host 关闭后 EnqueueAsync 返回 Closed ─────────────────────────

    [TestMethod]
    public async Task Enqueue_AfterDispose_ReturnsClosed()
    {
        var harness = await Harness.CreateAsync(channelCapacity: 4, workerCount: 1);
        var run = BuildRun("关闭后入队");
        await harness.RunStore.CreateAsync(run);

        await harness.Host.DisposeAsync();

        var result = await harness.Host.EnqueueAsync(run, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.AreEqual(AgentRunEnqueueStatus.Closed, result.Status, "Dispose 后应返回 Closed，不抛异常。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = Ws,
        SessionId = "session-enqueue-timeout",
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 10 }
    };

    /// <summary>Host + Store + 阻塞 transport 的组合测试夹具（同 AgentRun Scheduler 测试）。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly BlockingModelTransport? _transport;
        private readonly AgentRunStoreWrapper _store;

        public AgentRunStoreWrapper RunStore => _store;
        public AgentKernelHost Host { get; }

        private Harness(AgentRunStoreWrapper store, AgentKernelHost host, BlockingModelTransport? transport)
        {
            _store = store;
            Host = host;
            _transport = transport;
        }

        public static async Task<Harness> CreateAsync(int channelCapacity = 8, int workerCount = 2)
        {
            var store = new AgentRunStoreWrapper();
            var eventStore = new InMemoryAgentRunEventStore(store.Inner);

            var services = new ServiceCollection();
            services.AddSingleton<IAgentRunStore>(store);
            services.AddSingleton<IAgentRunEventStore>(eventStore);
            services.AddSingleton<IToolDispatcher>(new EchoToolDispatcher());
            services.AddSingleton<AgentKernelHost>();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

            var transport = new BlockingModelTransport();
            services.AddSingleton<IAgentModelTransport>(transport);

            var options = new AgentHostOptions
            {
                ChannelCapacity = channelCapacity,
                WorkerCount = workerCount,
                DrainTimeout = TimeSpan.FromSeconds(5)
            };
            services.AddSingleton(options);

            var provider = services.BuildServiceProvider();
            var host = provider.GetRequiredService<AgentKernelHost>();

            return new Harness(store, host, transport);
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

    /// <summary>IAgentRunStore 包装（暴露 Inner 供事件流使用；同 AgentRun Scheduler 测试）。</summary>
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

    /// <summary>transport stub：首次调用阻塞在 TCS，直到测试主动 Release（同 AgentRun Scheduler 测试）。</summary>
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
