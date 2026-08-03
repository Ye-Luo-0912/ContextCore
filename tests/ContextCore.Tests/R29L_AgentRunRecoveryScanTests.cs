using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

// ===========================================================================
// Recovery 扫描防饥饿（Keyset 游标 + 每状态预算 + Round-Robin）生产验收测试
//
// 验证 P1-7 的恢复扫描防饥饿设计：
//   1. ListByState_KeysetCursor_WalksAllRunsOnce — keyset 游标分页遍历全部 Run
//      （无重复、无遗漏，按 (UpdatedAt, RunId) 升序）。
//   2. ListByState_KeysetCursor_TieBreakByRunId — 同 UpdatedAt 时按 RunId 决胜，
//      游标续取不丢页、不重复。
//   3. RecoveryWorker_KeysetScan_VisitsAllRunsInAllStates — Worker 跨轮次推进游标，
//      在每状态 60 个 Run（> 每状态预算 50）下最终访问所有状态的全部 Run，
//      证明早期富状态无法独占扫描预算（修复扫描饥饿）。
//
// 设计原则：
//   - 优先使用真实 InMemory 实现（非 mock）：InMemoryAgentRunStore /
//     InMemoryAgentRunEventStore
//   - RecordingPersistentRunStore 包装器实现 IPersistentAgentRunStore 标记并录制
//     ListByStateAsync 返回的 (状态, RunId)，用于断言 Worker 的实际扫描覆盖
//   - 所有异步测试使用超时 CancellationTokenSource 防止挂起
//   - 中文注释
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Agent-Run-Recovery-Scan")]
public sealed class R29L_AgentRunRecoveryScanTests
{
    /// <summary>
    /// 验证：ListByStateAsync 的 keyset 游标分页能遍历全部 Run——每页 take=50，
    /// 以 (UpdatedAt, RunId) 游标续取，130 个 Run（超过单页上限）应全部覆盖且不重复。
    /// </summary>
    [TestMethod]
    public async Task ListByState_KeysetCursor_WalksAllRunsOnce()
    {
        var store = new InMemoryAgentRunStore();
        const int total = 130;
        var baseTime = DateTimeOffset.UtcNow.AddHours(-2);
        for (var i = 0; i < total; i++)
        {
            var run = BuildRun($"run-{i:000}", AgentRunState.ContextBuilding, baseTime.AddMinutes(i));
            await store.CreateAsync(run);
        }

        // keyset 游标分页：每页 50，从上一页最后一条 (UpdatedAt, RunId) 续取
        var seen = new List<string>();
        DateTimeOffset? cursorUpdatedAt = null;
        string? cursorRunId = null;
        while (true)
        {
            var page = await store.ListByStateAsync(
                AgentRunState.ContextBuilding,
                take: 50,
                afterUpdatedAt: cursorUpdatedAt,
                afterRunId: cursorRunId);
            if (page.Count == 0)
            {
                break;
            }
            seen.AddRange(page.Select(r => r.RunId));
            if (page.Count < 50)
            {
                break;
            }
            cursorUpdatedAt = page[^1].UpdatedAt;
            cursorRunId = page[^1].RunId;
        }

        // 断言：全部 130 个 Run 都被遍历，且严格按 (UpdatedAt, RunId) 升序（无重复、无遗漏）
        var expected = Enumerable.Range(0, total).Select(i => $"run-{i:000}").ToList();
        CollectionAssert.AreEqual(expected, seen,
            "keyset 游标分页应按 UpdatedAt 升序遍历全部 Run（无重复、无遗漏）。");
    }

    /// <summary>
    /// 验证：UpdatedAt 相同（同批写入）时，keyset 游标按 RunId 决胜——
    /// 第一页返回前 50 条（RunId 序），第二页从游标续取剩余 10 条，不丢页、不重复。
    /// </summary>
    [TestMethod]
    public async Task ListByState_KeysetCursor_TieBreakByRunId()
    {
        var store = new InMemoryAgentRunStore();
        const int total = 60;
        var sameTime = DateTimeOffset.UtcNow.AddHours(-2);
        for (var i = 0; i < total; i++)
        {
            var run = BuildRun($"run-{i:000}", AgentRunState.ModelCalling, sameTime);
            await store.CreateAsync(run);
        }

        // 第一页：take=50（无游标）→ 前 50 条，按 RunId 序
        var page1 = await store.ListByStateAsync(AgentRunState.ModelCalling, take: 50);
        Assert.AreEqual(50, page1.Count, "第一页应返回 50 条。");
        Assert.AreEqual("run-000", page1[0].RunId, "同 UpdatedAt 时首条应为 RunId 最小的 Run。");
        Assert.AreEqual("run-049", page1[^1].RunId, "同 UpdatedAt 时第 50 条应为 RunId 第 50 小的 Run。");

        // 第二页：从 (UpdatedAt, RunId) 游标续取 → 剩余 10 条
        var page2 = await store.ListByStateAsync(
            AgentRunState.ModelCalling,
            take: 50,
            afterUpdatedAt: page1[^1].UpdatedAt,
            afterRunId: page1[^1].RunId);
        Assert.AreEqual(10, page2.Count, "第二页应返回剩余 10 条。");
        Assert.AreEqual("run-050", page2[0].RunId, "第二页首条应为 RunId 第 51 小的 Run。");
        Assert.AreEqual("run-059", page2[^1].RunId, "第二页末条应为 RunId 最大的 Run。");

        // 续取后无更多数据
        var page3 = await store.ListByStateAsync(
            AgentRunState.ModelCalling,
            take: 50,
            afterUpdatedAt: page2[^1].UpdatedAt,
            afterRunId: page2[^1].RunId);
        Assert.AreEqual(0, page3.Count, "游标越过末条后不应再有数据。");
    }

    /// <summary>
    /// 验证：Recovery Worker 跨轮次推进 keyset 游标，在每状态 60 个 Run
    /// （> 每状态预算 50）下最终访问所有状态的全部 Run——早期富状态无法独占
    /// 扫描预算，状态内后进 Run 与后续状态都不会饥饿。
    /// </summary>
    /// <remarks>
    /// 场景设计：全部 Run 置为 UpdatedAt=-2h（已超 RunExecutionTimeout=1h）且
    /// DeadlineAt=+1h（自身超时未到期）→ Worker 走超时检测的 DeadlineAt 分支
    /// 直接 continue，Run 保持在原状态。这使得每状态始终有 60 个可扫描 Run，
    /// 旧实现（按 CreatedAt 只取前 100 条）只能扫描到部分 Run；
    /// 新实现必须靠 keyset 游标逐轮推进才能覆盖全部。
    /// </remarks>
    [TestMethod]
    public async Task RecoveryWorker_KeysetScan_VisitsAllRunsInAllStates()
    {
        // 准备：录制包装器（IPersistentAgentRunStore 标记 + 录制 ListByStateAsync 结果）
        var innerStore = new InMemoryAgentRunStore();
        var recordingStore = new RecordingPersistentRunStore(innerStore);
        var eventStore = new InMemoryAgentRunEventStore(recordingStore);

        var now = DateTimeOffset.UtcNow;
        var crashTime = now.AddHours(-2);   // 已超时（> RunExecutionTimeout=1h）
        var deadline = now.AddHours(1);     // 自身超时未到期（Worker 应 continue 而非标记终态）

        // 与 AgentRunRecoveryWorker.RecoverableStates 一致的 7 个可恢复状态
        var states = new[]
        {
            AgentRunState.Created,
            AgentRunState.ContextBuilding,
            AgentRunState.ModelCalling,
            AgentRunState.ToolDispatching,
            AgentRunState.Observing,
            AgentRunState.Checkpointing,
            AgentRunState.PendingToolExecution
        };

        const int runsPerState = 60; // > MaxRunsPerStatePerScan=50，迫使 keyset 游标跨轮推进
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var state in states)
        {
            for (var i = 0; i < runsPerState; i++)
            {
                var run = BuildRun($"run-{index:000}", state, crashTime);
                run = run with { DeadlineAt = deadline };
                await recordingStore.CreateAsync(run);
                expected.Add(run.RunId);
                index++;
            }
        }
        Assert.AreEqual(states.Length * runsPerState, expected.Count, "应创建 420 个唯一 Run。");

        // 构建 ServiceProvider（与 R29H 恢复 Worker 测试相同的依赖图）
        var services = new ServiceCollection();
        services.AddSingleton<IAgentRunStore>(recordingStore);
        services.AddSingleton<IPersistentAgentRunStore>(recordingStore);
        services.AddSingleton<IAgentRunEventStore>(eventStore);
        services.AddSingleton<IToolDispatcher>(new EchoToolDispatcher());
        services.AddSingleton<IAgentModelTransport>(new DeterministicAgentModelTransport());
        services.AddSingleton<AgentKernelHost>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var serviceProvider = services.BuildServiceProvider();

        await using (serviceProvider.GetRequiredService<AgentKernelHost>())
        {
            var options = new ContextCoreRuntimeOptions
            {
                EnableAgentRunRecovery = true,
                RunRecoveryInterval = TimeSpan.FromMilliseconds(50),
                RunExecutionTimeout = TimeSpan.FromHours(1)
            };

            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var worker = new AgentRunRecoveryWorker(
                serviceProvider, options, loggerFactory.CreateLogger<AgentRunRecoveryWorker>());

            // 启动 Worker。BackgroundService.StartAsync 在 ExecuteAsync 未完成时立即返回，
            // 因此需轮询等待扫描覆盖（50ms 轮询间隔 × 数十轮，足以跨轮推进全部状态游标）。
            await worker.StartAsync(CancellationToken.None);

            var scanDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < scanDeadline)
            {
                var covered = recordingStore.Visited
                    .Select(x => x.RunId)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                if (covered >= expected.Count)
                {
                    break;
                }
                await Task.Delay(50);
            }

            // 停止 Worker（取消内部 StoppingToken，等待 ExecuteAsync 退出后再断言）
            await worker.StopAsync(CancellationToken.None);

            // 断言 1：去重后的访问集合覆盖全部 420 个 Run（无遗漏）
            var visitedRunIds = recordingStore.Visited
                .Select(x => x.RunId)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            Assert.AreEqual(expected.Count, visitedRunIds.Count,
                "Worker 应通过 keyset 游标访问到全部 Run（无遗漏）。");
            Assert.IsTrue(expected.SetEquals(visitedRunIds),
                "Worker 访问到的 Run 集合应等于全部创建的 Run。");

            // 断言 2：每个可恢复状态的全部 Run 都被访问（状态间不饥饿）
            foreach (var state in states)
            {
                var visitedForState = recordingStore.Visited
                    .Where(x => x.State == state)
                    .Select(x => x.RunId)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                Assert.AreEqual(runsPerState, visitedForState,
                    $"状态 {state} 的全部 {runsPerState} 个 Run 应被访问到（状态间不饥饿）。");
            }
        }
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static AgentRun BuildRun(string runId, AgentRunState state, DateTimeOffset timestamp) => new()
    {
        RunId = runId,
        WorkspaceId = "ws-r29l-recovery-scan",
        SessionId = "session-r29l-recovery-scan",
        Task = "恢复扫描防饥饿测试",
        State = state,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = timestamp,
        UpdatedAt = timestamp,
        TurnBudget = new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 5
        }
    };

    // ── 测试 stub ─────────────────────────────────────────────────────────────

    /// <summary>
    /// InMemoryAgentRunStore 的持久化标记包装器（实现 IPersistentAgentRunStore），
    /// 并录制每次 ListByStateAsync 返回的 (状态, RunId)，用于断言 Worker 的扫描覆盖。
    /// </summary>
    private sealed class RecordingPersistentRunStore : IPersistentAgentRunStore
    {
        private readonly InMemoryAgentRunStore _inner;

        public RecordingPersistentRunStore(InMemoryAgentRunStore inner) => _inner = inner;

        /// <summary>ListByStateAsync 返回的全部 (状态, RunId) 记录。</summary>
        public ConcurrentBag<(AgentRunState State, string RunId)> Visited { get; } = new();

        public ValueTask CreateAsync(AgentRun run, CancellationToken cancellationToken = default)
            => _inner.CreateAsync(run, cancellationToken);

        public ValueTask<AgentRun?> GetAsync(string workspaceId, string runId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, runId, cancellationToken);

        public ValueTask<AgentRun?> GetByIdempotencyKeyAsync(string workspaceId, string idempotencyKey, CancellationToken cancellationToken = default)
            => _inner.GetByIdempotencyKeyAsync(workspaceId, idempotencyKey, cancellationToken);

        public ValueTask<AgentRunCreateResult> CreateOrGetByIdempotencyKeyAsync(AgentRun run, CancellationToken ct = default)
            => _inner.CreateOrGetByIdempotencyKeyAsync(run, ct);

        public ValueTask TransitionStateAsync(
            string workspaceId, string runId,
            AgentRunState expectedCurrentState, AgentRunState newState,
            CancellationToken cancellationToken = default,
            string? leaseToken = null,
            long? fencingToken = null)
            => _inner.TransitionStateAsync(workspaceId, runId, expectedCurrentState, newState, cancellationToken, leaseToken, fencingToken);

        public ValueTask UpdateAsync(AgentRun run, CancellationToken cancellationToken = default)
            => _inner.UpdateAsync(run, cancellationToken);

        public ValueTask<IReadOnlyList<AgentRun>> ListBySessionAsync(
            string workspaceId, string sessionId, CancellationToken cancellationToken = default)
            => _inner.ListBySessionAsync(workspaceId, sessionId, cancellationToken);

        public async ValueTask<IReadOnlyList<AgentRun>> ListByStateAsync(
            AgentRunState state, int take = 100,
            DateTimeOffset? afterUpdatedAt = null, string? afterRunId = null,
            CancellationToken cancellationToken = default)
        {
            var page = await _inner.ListByStateAsync(state, take, afterUpdatedAt, afterRunId, cancellationToken)
                .ConfigureAwait(false);
            foreach (var run in page)
            {
                Visited.Add((state, run.RunId));
            }
            return page;
        }
    }
}
