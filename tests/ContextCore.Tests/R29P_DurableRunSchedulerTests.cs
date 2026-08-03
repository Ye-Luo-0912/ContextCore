using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Hosting;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

// ===========================================================================
// B3 Durable Agent Run Scheduler 生产验收测试
//
// 验证 Durable Run Scheduler（优先级队列 + SKIP LOCKED 领取 + 重试/死信 + 重启恢复）：
// 1. Host_PriorityQueue_HigherPriorityRunsFirst — AgentKernelHost 优先级队列
// 按 Priority DESC 出队（高优先级先执行），同优先级保持 FIFO；
// 2. Host_PriorityQueue_SamePriorityPreservesFifoOrder — 同优先级先入队先执行；
// 3. StateMachine_DeadLettered_IsTerminal — DeadLettered 为终态；
// 4. MigrationSql_AgentRuns_ContainsSchedulingColumnsAndIndex — 基线 DDL 含调度/重试列 + 领取索引；
// 5. MigrationStepRegistry_AgentRunScheduling_DeclaresV52ToV53 — v52→v53 迁移步骤元数据；
// 6. Store_ClaimPending_ZeroTake_ReturnsEmpty / Store_DeadLetter_ZeroTake_ReturnsEmpty —
// 非 DB 参数校验路径（take<=0 短路返回，不触库）；
// 7. Claimer_Loops_ClaimsAndExecutesRuns — PostgresPendingRunClaimer 周期性领取
// pending Run 并驱动执行到终态（重启恢复语义）。
//
// 设计原则：
// - Host 优先级/公平性用真实 InMemory store + 阻塞 transport 验证（确定性时序）；
// - Claimer 用 IPersistentAgentRunStore 包装器（录制领取调用）验证循环与入队；
// - Postgres SKIP LOCKED SQL 语义由 ContextCore.IntegrationTests（Testcontainers）覆盖；
// 本文件仅验证可离线的部分（SQL 字符串 / 步骤元数据 / 参数校验）。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Durable-Run-Scheduler")]
public sealed class R29P_DurableRunSchedulerTests
{
    private const string Ws = "ws-durable-scheduler";

    // ── 1. 优先级队列：高优先级先执行 ────────────────────────────────────

    /// <summary>
    /// 验证：Worker 空闲时先拾取首个入队的 Run（阻塞在 transport）；
    /// 队列中的高优先级 Run（Priority=10）先于低优先级（Priority=5）出队执行。
    /// </summary>
    [TestMethod]
    public async Task Host_PriorityQueue_HigherPriorityRunsFirst()
    {
        await using var harness = await PriorityHarness.CreateAsync();

        var runLow = BuildRun("priority-0", priority: 0);
        var runHigh = BuildRun("priority-10", priority: 10);
        var runMid = BuildRun("priority-5", priority: 5);
        await harness.Store.CreateAsync(runLow);
        await harness.Store.CreateAsync(runHigh);
        await harness.Store.CreateAsync(runMid);

        // runLow 先入队 → worker 拾取并阻塞在 transport（唯一候选）。
        var first = await harness.Host.TryEnqueueAsync(runLow, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, first.Status);
        await harness.WaitForFirstCallAsync().ConfigureAwait(false);

        // runHigh / runMid 排队（worker 忙）。
        var high = await harness.Host.TryEnqueueAsync(runHigh, CancellationToken.None).ConfigureAwait(false);
        var mid = await harness.Host.TryEnqueueAsync(runMid, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, high.Status);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, mid.Status);

        // 释放首个调用 → runLow 完成；worker 按优先级出队：runHigh(10) 先于 runMid(5)。
        harness.Release();
        await harness.WaitForTerminalAsync(runLow).ConfigureAwait(false);
        await harness.WaitForTerminalAsync(runHigh).ConfigureAwait(false);
        await harness.WaitForTerminalAsync(runMid).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[] { runLow.RunId, runHigh.RunId, runMid.RunId },
            harness.Transport.CallOrder.ToArray(),
            "执行顺序应为 优先级0 → 优先级10 → 优先级5（高优先级先于低优先级出队）。");
        Assert.AreEqual(0, harness.Host.ActiveRunCount, "全部完成后活跃 Run 应归零。");
    }

    // ── 2. 优先级队列：同优先级 FIFO ─────────────────────────────────────

    /// <summary>
    /// 验证：同优先级（Priority=0）按入队顺序出队（FIFO），先入队先执行。
    /// </summary>
    [TestMethod]
    public async Task Host_PriorityQueue_SamePriorityPreservesFifoOrder()
    {
        await using var harness = await PriorityHarness.CreateAsync();

        var runA = BuildRun("fifo-a", priority: 0);
        var runB = BuildRun("fifo-b", priority: 0);
        var runC = BuildRun("fifo-c", priority: 0);
        await harness.Store.CreateAsync(runA);
        await harness.Store.CreateAsync(runB);
        await harness.Store.CreateAsync(runC);

        var a = await harness.Host.TryEnqueueAsync(runA, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, a.Status);
        await harness.WaitForFirstCallAsync().ConfigureAwait(false);

        var b = await harness.Host.TryEnqueueAsync(runB, CancellationToken.None).ConfigureAwait(false);
        var c = await harness.Host.TryEnqueueAsync(runC, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, b.Status);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted, c.Status);

        harness.Release();
        await harness.WaitForTerminalAsync(runA).ConfigureAwait(false);
        await harness.WaitForTerminalAsync(runB).ConfigureAwait(false);
        await harness.WaitForTerminalAsync(runC).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[] { runA.RunId, runB.RunId, runC.RunId },
            harness.Transport.CallOrder.ToArray(),
            "同优先级应按入队顺序（FIFO）执行。");
    }

    // ── 3. 状态机：DeadLettered 为终态 ───────────────────────────────────

    /// <summary>
    /// 验证：DeadLettered 是终态（IsTerminalState 返回 true），
    /// 且与 Failed 区分（Failed 可能有重试机会，DeadLettered 重试预算耗尽）。
    /// </summary>
    [TestMethod]
    public void StateMachine_DeadLettered_IsTerminal()
    {
        Assert.IsTrue(AgentRunStateMachine.IsTerminalState(AgentRunState.DeadLettered),
            "DeadLettered 应为终态（重试耗尽，不再自动恢复）。");
        Assert.IsTrue(AgentRunStateMachine.IsTerminalState(AgentRunState.Failed),
            "Failed 仍为终态（MaxRetries=0 时失败即终态）。");
    }

    // ── 4. 迁移 SQL：基线 DDL 含调度/重试列 + 领取索引 ───────────────────

    /// <summary>
    /// 验证：BuildMigrationSql 的 agent_runs 建表 DDL 包含 B3 调度/重试列
    /// （priority / max_retries / retry_count / next_retry_at）与领取索引
    /// （state, priority DESC, created_at ASC）。
    /// </summary>
    [TestMethod]
    public void MigrationSql_AgentRuns_ContainsSchedulingColumnsAndIndex()
    {
        var sql = PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false,
            TablePrefix = "cc_"
        });

        StringAssert.Contains(sql, "priority integer NOT NULL DEFAULT 0", "基线 DDL 应含 priority 列。");
        StringAssert.Contains(sql, "max_retries integer NOT NULL DEFAULT 0", "基线 DDL 应含 max_retries 列。");
        StringAssert.Contains(sql, "retry_count integer NOT NULL DEFAULT 0", "基线 DDL 应含 retry_count 列。");
        StringAssert.Contains(sql, "next_retry_at timestamptz NULL", "基线 DDL 应含 next_retry_at 列。");
        StringAssert.Contains(sql, "ix_cc_agent_runs_scheduling",
            "基线 DDL 应含 Durable Scheduler 领取索引（state, priority DESC, created_at ASC）。");
        StringAssert.Contains(sql, "ADD COLUMN IF NOT EXISTS priority integer NOT NULL DEFAULT 0",
            "基线 DDL 应含 v52→v53 幂等 ALTER（已有表补列）。");
    }

    // ── 5. 迁移步骤元数据 ────────────────────────────────────────────────

    /// <summary>
    /// 验证：v52→v53 agent_runs 调度/重试迁移步骤已注册且元数据正确。
    /// </summary>
    [TestMethod]
    public void MigrationStepRegistry_AgentRunScheduling_DeclaresV52ToV53()
    {
        var step = PostgresMigrationStepRegistry.Steps
            .OfType<PostgresMigrationAgentRunScheduling>()
            .Single();

        Assert.AreEqual("0003_agent_run_scheduling", step.MigrationId);
        Assert.AreEqual("cc-schema-v52", step.FromSchemaVersion);
        Assert.AreEqual("cc-schema-v53", step.ToSchemaVersion);
        CollectionAssert.AreEqual(
            new[] { PostgresMigrationStage.Online },
            step.Stages.ToArray(),
            "v52→v53 应为纯 Online 阶段（ADD COLUMN IF NOT EXISTS + CREATE INDEX IF NOT EXISTS，幂等）。");
    }

    // ── 6. Store 参数校验（take<=0 短路，不触库）────────────────────────

    [TestMethod]
    public async Task Store_ClaimPending_ZeroTake_ReturnsEmpty()
    {
        var store = CreateStoreWithoutConnection();
        var result = await store.ClaimPendingBatchAsync(
            0, 5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30)).ConfigureAwait(false);
        Assert.AreEqual(0, result.Count, "take<=0 应短路返回空列表（不触库）。");
    }

    [TestMethod]
    public async Task Store_DeadLetter_ZeroTake_ReturnsEmpty()
    {
        var store = CreateStoreWithoutConnection();
        var result = await store.DeadLetterExhaustedRunsAsync(0).ConfigureAwait(false);
        Assert.AreEqual(0, result.Count, "take<=0 应短路返回空列表（不触库）。");
    }

    // ── 7. Claimer 循环：领取并驱动执行到终态 ───────────────────────────

    /// <summary>
    /// 验证：PostgresPendingRunClaimer 周期性调用 DeadLetterExhaustedRunsAsync +
    /// ClaimPendingBatchAsync，把领取到的 Created Run 经 AgentKernelHost 入队执行到终态
    /// （进程重启恢复：持久化的 pending Run 由 claimer 自动接管执行）。
    /// </summary>
    [TestMethod]
    public async Task Claimer_Loops_ClaimsAndExecutesRuns()
    {
        var inner = new InMemoryAgentRunStore();
        var recording = new RecordingPersistentRunStore(inner);

        var runA = BuildRun("claimer-a", priority: 1);
        var runB = BuildRun("claimer-b", priority: 0);
        await inner.CreateAsync(runA);
        await inner.CreateAsync(runB);

        var services = new ServiceCollection();
        services.AddSingleton<IAgentRunStore>(recording);
        services.AddSingleton<IPersistentAgentRunStore>(recording);
        services.AddSingleton<IAgentRunEventStore>(new InMemoryAgentRunEventStore(inner));
        services.AddSingleton<IAgentModelTransport>(new DeterministicAgentModelTransport());
        services.AddSingleton<IToolDispatcher>(new NoopToolDispatcher());
        services.AddSingleton(new AgentHostOptions
        {
            PendingClaimInterval = TimeSpan.FromMilliseconds(50),
            PendingClaimBatchSize = 10,
            PendingClaimPerWorkspace = 5,
            DeadLetterBatchSize = 10,
            ChannelCapacity = 16,
            WorkerCount = 2,
            DrainTimeout = TimeSpan.FromSeconds(5)
        });
        services.AddSingleton<AgentKernelHost>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        await using var provider = services.BuildServiceProvider();
        var claimer = new PostgresPendingRunClaimer(
            provider, NullLogger<PostgresPendingRunClaimer>.Instance);

        await claimer.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // 等待 claimer 领取并驱动两个 Run 执行到终态。
            var deadline = DateTime.UtcNow.AddSeconds(15);
            AgentRun? finalA = null;
            AgentRun? finalB = null;
            while (DateTime.UtcNow < deadline)
            {
                finalA = await inner.GetAsync(runA.WorkspaceId, runA.RunId).ConfigureAwait(false);
                finalB = await inner.GetAsync(runB.WorkspaceId, runB.RunId).ConfigureAwait(false);
                if (finalA is not null && AgentRunStateMachine.IsTerminalState(finalA.State)
                    && finalB is not null && AgentRunStateMachine.IsTerminalState(finalB.State))
                {
                    break;
                }
                await Task.Delay(50).ConfigureAwait(false);
            }

            Assert.IsTrue(finalA is not null && AgentRunStateMachine.IsTerminalState(finalA.State),
                $"claimer 应在超时前驱动 Run A 到终态，实际 {finalA?.State}。");
            Assert.IsTrue(finalB is not null && AgentRunStateMachine.IsTerminalState(finalB.State),
                $"claimer 应在超时前驱动 Run B 到终态，实际 {finalB?.State}。");
            Assert.IsTrue(recording.ClaimCalls > 0, "claimer 应至少执行一次领取周期。");
        }
        finally
        {
            await claimer.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static AgentRun BuildRun(string task, int priority = 0, int maxRetries = 0) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = Ws,
        SessionId = "session-durable-scheduler",
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
        },
        Priority = priority,
        MaxRetries = maxRetries
    };

    private static PostgresAgentRunStore CreateStoreWithoutConnection()
        => new(
            new PostgresConnectionFactory(new PostgresOptions
            {
                ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
                AutoMigrate = false
            }),
            new PostgresJsonSerializer(),
            new PostgresMigrationRunner(new PostgresConnectionFactory(new PostgresOptions
            {
                ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
                AutoMigrate = false
            })));

    /// <summary>Host + InMemory store + 阻塞/录制 transport 的优先级调度夹具。</summary>
    private sealed class PriorityHarness : IAsyncDisposable
    {
        private readonly PriorityHarnessStore _store;
        private readonly RecordingBlockingTransport _transport;

        public PriorityHarnessStore Store => _store;
        public AgentKernelHost Host { get; }
        public RecordingBlockingTransport Transport => _transport;

        private PriorityHarness(
            PriorityHarnessStore store,
            AgentKernelHost host,
            RecordingBlockingTransport transport)
        {
            _store = store;
            Host = host;
            _transport = transport;
        }

        public static async Task<PriorityHarness> CreateAsync()
        {
            var store = new PriorityHarnessStore();
            var eventStore = new InMemoryAgentRunEventStore(store.Inner);
            var transport = new RecordingBlockingTransport();

            var services = new ServiceCollection();
            services.AddSingleton<IAgentRunStore>(store);
            services.AddSingleton<IAgentRunEventStore>(eventStore);
            services.AddSingleton<IToolDispatcher>(new NoopToolDispatcher());
            services.AddSingleton<IAgentModelTransport>(transport);
            services.AddSingleton(new AgentHostOptions
            {
                ChannelCapacity = 8,
                WorkerCount = 1,
                DrainTimeout = TimeSpan.FromSeconds(5)
            });
            services.AddSingleton<AgentKernelHost>();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

            var provider = services.BuildServiceProvider();
            var host = provider.GetRequiredService<AgentKernelHost>();
            await Task.CompletedTask;
            return new PriorityHarness(store, host, transport);
        }

        public async Task WaitForFirstCallAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_transport.CallCount == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
            Assert.IsTrue(_transport.CallCount > 0, "worker 应在超时前拾取首个 Run 并进入 transport 调用。");
        }

        public void Release() => _transport.Release();

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
    private sealed class PriorityHarnessStore : IAgentRunStore
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

    /// <summary>
    /// transport stub：首个调用阻塞在 TCS（让队列积压），后续调用立即返回最终答案；
    /// 录制调用顺序（按 RunId）。
    /// </summary>
    private sealed class RecordingBlockingTransport : IAgentModelTransport
    {
        private readonly ConcurrentQueue<string> _callOrder = new();
        private TaskCompletionSource<AgentModelResponse>? _gate;
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public IReadOnlyList<string> CallOrder => _callOrder.ToList();

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用 AgentModelRequest 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用 AgentModelRequest 重载。");

        public async ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _callCount);
            _callOrder.Enqueue(request.RunId);
            if (n == 1)
            {
                var gate = new TaskCompletionSource<AgentModelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                _gate = gate;
                return await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return FinalResponse();
        }

        public void Release() => _gate?.TrySetResult(FinalResponse());

        private static AgentModelResponse FinalResponse() => new()
        {
            Content = "完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 3,
            Duration = TimeSpan.FromMilliseconds(1)
        };
    }

    /// <summary>
    /// IPersistentAgentRunStore 包装器：委托给 InMemoryAgentRunStore，
    /// 并录制 ClaimPendingBatchAsync / DeadLetterExhaustedRunsAsync 调用次数。
    /// ClaimPendingBatchAsync 返回 store 中仍为 Created 的 Run（模拟 SKIP LOCKED 领取语义）。
    /// </summary>
    private sealed class RecordingPersistentRunStore : IPersistentAgentRunStore
    {
        private readonly InMemoryAgentRunStore _inner;
        private int _claimCalls;

        public RecordingPersistentRunStore(InMemoryAgentRunStore inner) => _inner = inner;

        /// <summary>ClaimPendingBatchAsync 调用次数。</summary>
        public int ClaimCalls => Volatile.Read(ref _claimCalls);

        public ValueTask CreateAsync(AgentRun run, CancellationToken cancellationToken = default)
            => _inner.CreateAsync(run, cancellationToken);

        public ValueTask<AgentRun?> GetAsync(string workspaceId, string runId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, runId, cancellationToken);

        public ValueTask<AgentRun?> GetByIdempotencyKeyAsync(string workspaceId, string idempotencyKey, CancellationToken cancellationToken = default)
            => _inner.GetByIdempotencyKeyAsync(workspaceId, idempotencyKey, cancellationToken);

        public ValueTask<AgentRunCreateResult> CreateOrGetByIdempotencyKeyAsync(AgentRun run, CancellationToken ct = default)
            => _inner.CreateOrGetByIdempotencyKeyAsync(run, ct);

        public ValueTask TransitionStateAsync(
            string workspaceId, string runId, AgentRunState expectedState, AgentRunState newState,
            CancellationToken cancellationToken = default, string? leaseToken = null, long? fencingToken = null)
            => _inner.TransitionStateAsync(workspaceId, runId, expectedState, newState, cancellationToken, leaseToken, fencingToken);

        public ValueTask UpdateAsync(AgentRun run, CancellationToken cancellationToken = default)
            => _inner.UpdateAsync(run, cancellationToken);

        public ValueTask<IReadOnlyList<AgentRun>> ListBySessionAsync(string workspaceId, string sessionId, CancellationToken cancellationToken = default)
            => _inner.ListBySessionAsync(workspaceId, sessionId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentRun>> ListByStateAsync(
            AgentRunState state, int take = 100,
            DateTimeOffset? afterUpdatedAt = null, string? afterRunId = null,
            CancellationToken cancellationToken = default)
            => _inner.ListByStateAsync(state, take, afterUpdatedAt, afterRunId, cancellationToken);

        public async ValueTask<IReadOnlyList<AgentRun>> ClaimPendingBatchAsync(
            int take, int perWorkspace, TimeSpan retryBackoffBase, TimeSpan retryBackoffMax,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _claimCalls);
            return await _inner.ListByStateAsync(AgentRunState.Created, take, null, null, cancellationToken)
                .ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<AgentRun>> DeadLetterExhaustedRunsAsync(
            int take, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentRun>>(Array.Empty<AgentRun>());
    }

    /// <summary>
    /// 空操作 IToolDispatcher 实现，仅支持空 tool 名集合。
    /// 测试中 transport 直接产出最终答案（task 不含 "search" 关键词），不会触发 tool 分派。
    /// </summary>
    private sealed class NoopToolDispatcher : IToolDispatcher
    {
        private static readonly IReadOnlySet<string> s_empty = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlySet<string> SupportedTools => s_empty;

        public ToolDescriptor? GetDescriptor(string toolName) => null;

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
