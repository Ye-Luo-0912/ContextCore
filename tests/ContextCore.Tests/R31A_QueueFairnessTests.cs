using System.Diagnostics.Metrics;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

// ===========================================================================
// 本地调度队列 Workspace 公平性验收测试
//
// 覆盖：
// 1. per-workspace 排队上限——单 Workspace 填满自己的排队槽位后后续入队 QueueFull，
//    其他 Workspace 不受影响（快路径预检 + 锁内权威判定）；
// 2. 加权公平出队——出队按 min(ServiceCount/Weight) 轮转 Workspace，
//    已服务过的 Workspace 让位给未服务的 Workspace（等权重 = 严格轮转）；
// 3. 优先级老化——排队超过老化间隔的低优先级 Run 提升 aged priority，
//    最终先于新到达的高优先级 Run 出队（防饿死）；
// 4. 保留容量——队列饱和时 Priority >= 阈值的 Run 走保留池仍能入队，
//    普通 Run 获得 QueueFull；
// 5. 排队 SLO——出队等待超过 QueueWaitSlo 计入 contextcore.agent.queue 指标。
// ===========================================================================

[TestClass]
[TestCategory("R31")]
[TestCategory("Queue-Fairness")]
public sealed class R31A_QueueFairnessTests
{
    // ── 1. per-workspace 排队上限 ────────────────────────────────────────

    /// <summary>
    /// 验证：Workspace 排队数达到 MaxQueuedPerWorkspace 后，同 Workspace 后续入队
    /// 立即 QueueFull（detail 含排队上限），其他 Workspace 仍可入队。
    /// </summary>
    [TestMethod]
    public async Task Queue_PerWorkspaceLimit_RejectsExcessFromSameWorkspace()
    {
        await using var harness = await Harness.CreateAsync(configure: o =>
        {
            o.ChannelCapacity = 8;
            o.WorkerCount = 1;
            o.MaxQueuedPerWorkspace = 2;
        });

        var runA = BuildRun("ws-a", "a-run", priority: 0);
        var runB = BuildRun("ws-a", "b-run", priority: 0);
        var runC = BuildRun("ws-a", "c-run", priority: 0);
        var runD = BuildRun("ws-a", "d-run", priority: 0);
        var runE = BuildRun("ws-b", "e-run", priority: 0);
        await harness.RunStore.CreateAsync(runA);
        await harness.RunStore.CreateAsync(runB);
        await harness.RunStore.CreateAsync(runC);
        await harness.RunStore.CreateAsync(runD);
        await harness.RunStore.CreateAsync(runE);

        // A：worker 拾取后阻塞在 transport（活跃，不占排队槽位）。
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runA, CancellationToken.None)).Status);
        await harness.WaitForTransportCallAsync();

        // B/C：填满 ws-a 的排队上限（2）。
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runB, CancellationToken.None)).Status);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runC, CancellationToken.None)).Status);

        // D：ws-a 排队已满 → QueueFull（快路径预检 + 锁内判定）。
        var d = await harness.Host.TryEnqueueAsync(runD, CancellationToken.None);
        Assert.AreEqual(AgentRunEnqueueStatus.QueueFull, d.Status, "同 Workspace 超限应 QueueFull。");
        StringAssert.Contains(d.Detail, "workspace 排队上限", "detail 应指明 Workspace 排队上限。");

        // E：其他 Workspace 不受影响 → Accepted。
        var e = await harness.Host.TryEnqueueAsync(runE, CancellationToken.None);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, e.Status, "其他 Workspace 不应被单 Workspace 超限波及。");

        harness.Release();
        await harness.WaitForTerminalAsync(runA);
        await harness.WaitForTerminalAsync(runB);
        await harness.WaitForTerminalAsync(runC);
        await harness.WaitForTerminalAsync(runE);
    }

    // ── 2. 加权公平出队 ─────────────────────────────────────────────────

    /// <summary>
    /// 验证：等权重公平轮转——ws-a 已服务过首个 Run 后，排队中的 ws-b Run 先出队，
    /// 然后才轮到 ws-a 的第二个 Run（min(ServiceCount/Weight) 选择 Workspace）。
    /// </summary>
    [TestMethod]
    public async Task Queue_WeightedFair_RoundRobinsAcrossWorkspaces()
    {
        await using var harness = await Harness.CreateAsync(configure: o =>
        {
            o.ChannelCapacity = 8;
            o.WorkerCount = 1;
        });

        var runA = BuildRun("ws-fair-a", "a-run", priority: 0);
        var runB = BuildRun("ws-fair-a", "b-run", priority: 0);
        var runC = BuildRun("ws-fair-b", "c-run", priority: 0);
        await harness.RunStore.CreateAsync(runA);
        await harness.RunStore.CreateAsync(runB);
        await harness.RunStore.CreateAsync(runC);

        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runA, CancellationToken.None)).Status);
        await harness.WaitForTransportCallAsync();

        // ws-a 的第二个 Run 与 ws-b 的首个 Run 排队。
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runB, CancellationToken.None)).Status);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runC, CancellationToken.None)).Status);

        // 释放 → ws-a 已服务 1 次（runA），ws-b 未服务 → 公平轮转：ws-b 的 runC 先出队。
        harness.Release();
        await harness.WaitForTerminalAsync(runA);
        await harness.WaitForTerminalAsync(runB);
        await harness.WaitForTerminalAsync(runC);

        CollectionAssert.AreEqual(
            new[] { runA.RunId, runC.RunId, runB.RunId },
            harness.Transport.CallOrder.ToArray(),
            "公平轮转：已服务过 runA 的 ws-a 让位，ws-b 的 runC 先于 ws-a 的 runB 出队。");
    }

    // ── 3. 优先级老化 ───────────────────────────────────────────────────

    /// <summary>
    /// 验证：排队超过 PriorityAgingInterval 的低优先级 Run 经老化提升后，
    /// 先于新到达的高优先级 Run 出队（防低优先级饿死）。
    /// </summary>
    [TestMethod]
    public async Task Queue_PriorityAging_BoostsLongWaitingLowPriority()
    {
        await using var harness = await Harness.CreateAsync(configure: o =>
        {
            o.ChannelCapacity = 8;
            o.WorkerCount = 1;
            o.PriorityAgingInterval = TimeSpan.FromMilliseconds(50);
            o.PriorityAgingStep = 100;
        });

        var runA = BuildRun("ws-age", "a-run", priority: 0);
        var runLow = BuildRun("ws-age", "low-run", priority: 0);
        var runHigh = BuildRun("ws-age", "high-run", priority: 10);
        await harness.RunStore.CreateAsync(runA);
        await harness.RunStore.CreateAsync(runLow);
        await harness.RunStore.CreateAsync(runHigh);

        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runA, CancellationToken.None)).Status);
        await harness.WaitForTransportCallAsync();

        // 低优先级 Run 入队后等待超过 2 个老化间隔（aged 0 + 2×100 = 200）。
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runLow, CancellationToken.None)).Status);
        await Task.Delay(120).ConfigureAwait(false);

        // 高优先级 Run（10）新到达——老化前的 10 << 低优先级老化后的 200。
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runHigh, CancellationToken.None)).Status);

        harness.Release();
        await harness.WaitForTerminalAsync(runA);
        await harness.WaitForTerminalAsync(runLow);
        await harness.WaitForTerminalAsync(runHigh);

        CollectionAssert.AreEqual(
            new[] { runA.RunId, runLow.RunId, runHigh.RunId },
            harness.Transport.CallOrder.ToArray(),
            "老化提升：长时间排队的低优先级 Run 应先于新到达的高优先级 Run 出队。");
    }

    // ── 4. 保留容量 ─────────────────────────────────────────────────────

    /// <summary>
    /// 验证：队列饱和（常规池满）时，Priority >= ReservedPriorityThreshold 的 Run
    /// 走保留池仍 Accepted，普通 Run 获得 QueueFull。
    /// </summary>
    [TestMethod]
    public async Task Queue_ReservedCapacity_AdmitsHighPriorityUnderSaturation()
    {
        await using var harness = await Harness.CreateAsync(configure: o =>
        {
            // 容量 4 → 保留容量自动 = clamp(max(8, 0), 0, 2) = 2；常规池 = 2。
            o.ChannelCapacity = 4;
            o.WorkerCount = 1;
            o.ReservedPriorityThreshold = 1000;
        });

        var runA = BuildRun("ws-res", "a-run", priority: 0);
        var runB = BuildRun("ws-res", "b-run", priority: 0);
        var runC = BuildRun("ws-res2", "c-run", priority: 0);
        var runD = BuildRun("ws-res3", "d-run", priority: 0);
        var runE = BuildRun("ws-res3", "e-run", priority: 5000);
        await harness.RunStore.CreateAsync(runA);
        await harness.RunStore.CreateAsync(runB);
        await harness.RunStore.CreateAsync(runC);
        await harness.RunStore.CreateAsync(runD);
        await harness.RunStore.CreateAsync(runE);

        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runA, CancellationToken.None)).Status);
        await harness.WaitForTransportCallAsync();

        // B/C：填满常规池（2 槽）。
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runB, CancellationToken.None)).Status);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runC, CancellationToken.None)).Status);

        // D：常规池满 → QueueFull。
        var d = await harness.Host.TryEnqueueAsync(runD, CancellationToken.None);
        Assert.AreEqual(AgentRunEnqueueStatus.QueueFull, d.Status, "常规池满 → 普通 Run QueueFull。");

        // E：高优先级 → 保留池 → Accepted。
        var e = await harness.Host.TryEnqueueAsync(runE, CancellationToken.None);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, e.Status, "高优先级 Run 走保留池仍可入队。");

        harness.Release();
        await harness.WaitForTerminalAsync(runA);
        await harness.WaitForTerminalAsync(runB);
        await harness.WaitForTerminalAsync(runC);
        await harness.WaitForTerminalAsync(runE);
    }

    // ── 5. 排队 SLO 指标 ────────────────────────────────────────────────

    /// <summary>
    /// 验证：出队等待超过 QueueWaitSlo 的 Run 计入排队 SLO 超限计数，
    /// 且等待时长直方图有记录（contextcore.agent.queue 指标）。
    /// </summary>
    [TestMethod]
    public async Task Queue_WaitSlo_RecordsExceededMetric()
    {
        long sloExceeded = 0;
        long waitRecords = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name is "contextcore.agent.queue.wait_slo_exceeded" or "contextcore.agent.queue.wait.duration")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "contextcore.agent.queue.wait_slo_exceeded")
            {
                Interlocked.Add(ref sloExceeded, value);
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
        {
            if (instrument.Name == "contextcore.agent.queue.wait.duration")
            {
                Interlocked.Increment(ref waitRecords);
            }
        });
        listener.Start();

        await using var harness = await Harness.CreateAsync(configure: o =>
        {
            o.ChannelCapacity = 4;
            o.WorkerCount = 1;
            o.QueueWaitSlo = TimeSpan.FromMilliseconds(50);
        });

        var runA = BuildRun("ws-slo", "a-run", priority: 0);
        var runB = BuildRun("ws-slo", "b-run", priority: 0);
        await harness.RunStore.CreateAsync(runA);
        await harness.RunStore.CreateAsync(runB);

        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runA, CancellationToken.None)).Status);
        await harness.WaitForTransportCallAsync();

        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runB, CancellationToken.None)).Status);

        // 排队超过 SLO 后再释放 → runB 出队时触发 SLO 超限计数。
        await Task.Delay(150).ConfigureAwait(false);
        harness.Release();
        await harness.WaitForTerminalAsync(runA);
        await harness.WaitForTerminalAsync(runB);

        Assert.IsTrue(Volatile.Read(ref sloExceeded) >= 1, "排队超过 SLO 的 Run 应计入超限计数。");
        Assert.IsTrue(Volatile.Read(ref waitRecords) >= 1, "出队应记录排队等待时长直方图。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────

    private static AgentRun BuildRun(string workspaceId, string task, int priority) => new()
    {
        RunId = "run-" + task + "-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = workspaceId,
        SessionId = "session-" + workspaceId,
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        Priority = priority,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 10 }
    };

    /// <summary>Host + Store + 阻塞 transport 的组合测试夹具（同 Enqueue Timeout 测试）。</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly BlockingModelTransport _transport;
        private readonly AgentRunStoreWrapper _store;

        public AgentRunStoreWrapper RunStore => _store;
        public AgentKernelHost Host { get; }
        public BlockingModelTransport Transport => _transport;

        private Harness(AgentRunStoreWrapper store, AgentKernelHost host, BlockingModelTransport transport)
        {
            _store = store;
            Host = host;
            _transport = transport;
        }

        public static async Task<Harness> CreateAsync(Action<AgentHostOptions>? configure = null)
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
                ChannelCapacity = 8,
                WorkerCount = 2,
                DrainTimeout = TimeSpan.FromSeconds(5)
            };
            configure?.Invoke(options);
            services.AddSingleton(options);

            var provider = services.BuildServiceProvider();
            var host = provider.GetRequiredService<AgentKernelHost>();

            return new Harness(store, host, transport);
        }

        public async Task WaitForTransportCallAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_transport.CallCount == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
            Assert.IsTrue(_transport.CallCount > 0, "worker 应在超时前拾取 Run 并进入 transport 调用。");
        }

        public void Release()
        {
            _transport.Complete(
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

    /// <summary>IAgentRunStore 包装（暴露 Inner 供事件流使用；同 Enqueue Timeout 测试）。</summary>
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

    /// <summary>transport stub：首次调用阻塞在 TCS，直到测试主动 Release（同 Enqueue Timeout 测试）。</summary>
    private sealed class BlockingModelTransport : IAgentModelTransport
    {
        private readonly TaskCompletionSource<AgentModelResponse> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _callOrder = new();
        private readonly object _lock = new();
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<string> CallOrder
        {
            get
            {
                lock (_lock)
                {
                    return _callOrder.ToArray();
                }
            }
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用 AgentModelRequest 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用 AgentModelRequest 重载。");

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            lock (_lock)
            {
                _callOrder.Add(request.RunId);
            }
            return new ValueTask<AgentModelResponse>(_gate.Task);
        }

        public void Complete(AgentModelResponse response)
        {
            _gate.TrySetResult(response);
        }
    }
}
