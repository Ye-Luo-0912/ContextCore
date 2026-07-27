using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

// ===========================================================================
// R29-Hard-Gate：HA / Concurrency 硬验收门测试
//
// 验证任务C 修复后的两个核心 HA/并发保证：
//   1. OnlyOneInstance_OwnsAgentRunLease
//      同一 AgentRun 在同一时刻仅能有一个实例持有 Lease（HA 隔离基础）。
//   2. ActorConcurrency_IsBounded_PerWorkspace
//      AgentKernelHost 按 workspace 限制并发执行数（bounded Actor scheduler）。
//
// 这些测试是"硬验收门"——任一失败意味着 HA/并发修复回退，不能合并。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("HA-Concurrency")]
public sealed class R29H_HAConcurrencyAcceptanceTests
{
    // ===========================================================================
    // 测试 1：同一 AgentRun 只能有一个实例持有 Lease
    // ===========================================================================

    [TestMethod]
    public async Task OnlyOneInstance_OwnsAgentRunLease()
    {
        // 创建 InMemoryAgentRunLease（进程内租约实现）
        var lease = new InMemoryAgentRunLease();
        var runId = "run-1";
        var leaseDuration = TimeSpan.FromMinutes(5);

        // 实例A 获取 lease
        var leaseA = await lease.TryAcquireAsync(runId, leaseDuration, owner: "instance-A");
        Assert.IsNotNull(leaseA, "实例A 应成功获取 lease");
        Assert.AreEqual("instance-A", leaseA!.Owner);
        Assert.AreEqual(runId, leaseA.RunId);
        Assert.IsFalse(string.IsNullOrEmpty(leaseA.LeaseToken));

        // 实例B 尝试获取同一 runId 的 lease
        var leaseB = await lease.TryAcquireAsync(runId, leaseDuration, owner: "instance-B");

        // 断言实例B 获取失败（返回 null）
        Assert.IsNull(leaseB, "实例B 不应获取已被实例A 持有的 lease");

        // 验证实例A 仍持有 lease（续约成功）
        var renewed = await lease.RenewAsync(runId, leaseA.LeaseToken, leaseDuration);
        Assert.IsTrue(renewed, "实例A 应仍能续约 lease，证明其仍持有租约");

        // 验证 ActiveLeaseCount 仍为 1（实例B 未创建新租约）
        Assert.AreEqual(1, lease.ActiveLeaseCount, "应只有 1 个活跃租约");

        // 实例A 释放 lease 后，实例B 应能获取
        await lease.ReleaseAsync(runId, leaseA.LeaseToken);
        Assert.AreEqual(0, lease.ActiveLeaseCount, "释放后活跃租约应为 0");

        var leaseB2 = await lease.TryAcquireAsync(runId, leaseDuration, owner: "instance-B");
        Assert.IsNotNull(leaseB2, "实例A 释放后，实例B 应能获取 lease");
        Assert.AreEqual("instance-B", leaseB2!.Owner);
    }

    // ===========================================================================
    // 测试 2：Actor 并发按 workspace 有上限
    // ===========================================================================

    [TestMethod]
    public async Task ActorConcurrency_IsBounded_PerWorkspace()
    {
        // 创建阻塞式 ModelTransport，用于观察并发执行数
        var blockingTransport = new BlockingAgentModelTransport();

        // 构建 ServiceProvider，提供 AgentRunActor 所需的最小依赖
        var services = new ServiceCollection();
        services.AddSingleton<IAgentRunEventStore>(new InMemoryAgentRunEventStore());
        services.AddSingleton<IToolDispatcher>(new NoopToolDispatcher());
        services.AddSingleton<IAgentModelTransport>(blockingTransport);
        var serviceProvider = services.BuildServiceProvider();

        var runStore = new InMemoryAgentRunStore();
        var workspaceId = "ws-bounded-test";

        // 设置较小的 MaxWorkspaceRuns=2，WorkerCount=4（让 workspace 信号量成为瓶颈）
        var options = new AgentHostOptions
        {
            MaxGlobalRuns = 10,
            MaxWorkspaceRuns = 2,
            WorkerCount = 4,
            ChannelCapacity = 16,
            LeaseEnabled = false,
            DrainTimeout = TimeSpan.FromSeconds(10)
        };

        await using var host = new AgentKernelHost(serviceProvider, runStore, runLease: null, options);

        // 创建并启动 4 个 Run（同一 workspace）
        const int totalRuns = 4;
        var runIds = new string[totalRuns];
        for (var i = 0; i < totalRuns; i++)
        {
            runIds[i] = $"bounded-run-{i}";
            var run = CreateMinimalRun(runIds[i], workspaceId);
            await runStore.CreateAsync(run);
            await host.StartRunAsync(run);
        }

        // 等待 worker 拉取并执行（最多 2 个并发，其余排队在 workspace 信号量上）
        await Task.Delay(500);

        // 验证并发执行数不超过 MaxWorkspaceRuns=2
        var concurrentCalls = blockingTransport.CurrentConcurrentCount;
        Assert.IsTrue(concurrentCalls <= 2,
            $"并发执行数 {concurrentCalls} 应不超过 MaxWorkspaceRuns=2");
        Assert.IsTrue(concurrentCalls >= 1,
            $"应至少有 1 个 Run 正在执行，实际 {concurrentCalls}");

        // 已开始的调用总数应 <= 2
        var startedCalls = blockingTransport.TotalStartedCount;
        Assert.IsTrue(startedCalls <= 2,
            $"已开始的调用总数 {startedCalls} 应 <= 2（workspace 信号量限制）");

        // 释放 1 个槽位，等待下一个 Run 被拉取
        blockingTransport.ReleaseOne();
        await Task.Delay(500);

        // 释放剩余的所有阻塞，让所有 Run 完成
        blockingTransport.ReleaseAll();
        await Task.Delay(1000);

        // 验证所有 Run 都被调用过（最终全部执行）
        var totalStarted = blockingTransport.TotalStartedCount;
        Assert.AreEqual(totalRuns, totalStarted,
            $"所有 {totalRuns} 个 Run 最终都应被调用，实际 {totalStarted}");

        // 验证所有 Run 完成（host 活跃 Run 数归零）
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.ActiveRunCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.AreEqual(0, host.ActiveRunCount,
            "所有 Run 完成后，host 活跃 Run 数应归零");
    }

    // ===========================================================================
    // 辅助方法
    // ===========================================================================

    private static AgentRun CreateMinimalRun(string runId, string workspaceId) => new()
    {
        RunId = runId,
        WorkspaceId = workspaceId,
        SessionId = $"session-{runId}",
        Task = "test-task",
        State = AgentRunState.Created,
        Turn = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    // ===========================================================================
    // 私有 Mock：阻塞式 AgentModelTransport
    // ===========================================================================

    /// <summary>
    /// 阻塞式 IAgentModelTransport 实现。
    /// 每次 CallAsync 会阻塞在 TaskCompletionSource 上，直到外部调用 ReleaseOne/ReleaseAll。
    /// 用于观察 AgentKernelHost 的并发上限：只有获得 workspace 信号量的 Run 才会调用此 transport。
    /// </summary>
    private sealed class BlockingAgentModelTransport : IAgentModelTransport
    {
        private int _totalStarted;
        private int _currentConcurrent;
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> _pending = new();
        // ReleaseAll 置位后，后续新 CallAsync 立即完成（避免晚启动的 Call 在 ReleaseAll 之后永久阻塞）
        private int _allReleased;

        /// <summary>累计已开始的调用总数（单调递增）。</summary>
        public int TotalStartedCount => Volatile.Read(ref _totalStarted);

        /// <summary>当前正在阻塞的并发调用数。</summary>
        public int CurrentConcurrentCount => Volatile.Read(ref _currentConcurrent);

        public async ValueTask<AgentModelResponse> CallAsync(
            string runId,
            string context,
            CancellationToken cancellationToken = default)
        {
            return await CallAsyncCore(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<AgentModelResponse> CallAsync(
            string runId,
            IReadOnlyList<AgentMessage> messages,
            CancellationToken cancellationToken = default)
        {
            return await CallAsyncCore(cancellationToken).ConfigureAwait(false);
        }

        private async Task<AgentModelResponse> CallAsyncCore(CancellationToken ct)
        {
            Interlocked.Increment(ref _totalStarted);
            Interlocked.Increment(ref _currentConcurrent);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(tcs);

            // 若 ReleaseAll 已调用（可能在本次 enqueue 之前或之后），立即完成本 TCS，
            // 避免晚启动的 Call 在 ReleaseAll 排空队列后永久阻塞。TrySetResult 失败（已被
            // ReleaseAll 完成）是无害的。
            if (Volatile.Read(ref _allReleased) != 0)
            {
                tcs.TrySetResult(true);
            }

            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrent);
            }

            return new AgentModelResponse
            {
                Content = "done",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 1,
                Duration = TimeSpan.Zero
            };
        }

        /// <summary>释放一个阻塞的调用（FIFO）。</summary>
        public void ReleaseOne()
        {
            while (_pending.TryDequeue(out var tcs))
            {
                if (tcs.TrySetResult(true))
                {
                    return;
                }
            }
        }

        /// <summary>释放所有阻塞的调用，并使后续新 CallAsync 立即完成。</summary>
        public void ReleaseAll()
        {
            Volatile.Write(ref _allReleased, 1);
            while (_pending.TryDequeue(out var tcs))
            {
                tcs.TrySetResult(true);
            }
        }
    }

    // ===========================================================================
    // 私有 Mock：空操作 ToolDispatcher
    // ===========================================================================

    /// <summary>
    /// 空操作 IToolDispatcher 实现，仅支持空 tool 名集合。
    /// AgentRunActor 在无 modelTransport 时直接产出最终答案，不触发 tool 分派。
    /// </summary>
    private sealed class NoopToolDispatcher : IToolDispatcher
    {
        private static readonly IReadOnlySet<string> s_empty = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlySet<string> SupportedTools => s_empty;

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new ToolDispatchResult
            {
                Succeeded = true,
                Result = "{}",
                Error = null,
                Duration = TimeSpan.Zero,
                SideEffect = ToolSideEffect.None
            });
        }
    }
}
