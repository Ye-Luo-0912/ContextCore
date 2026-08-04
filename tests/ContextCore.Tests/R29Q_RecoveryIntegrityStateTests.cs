using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

// ===========================================================================
// Agent Run Recovery Integrity State—— 退避重试 + 人工介入告警钩子
//
// 覆盖范围：
// 退避重试：
// RecoveryDependencyUnavailable（17）不再是终态——Actor 每次进入时递增 RecoveryAttempt，
// 按指数退避（base × 2^(attempt-1)，封顶 cap）计算 NextRetryAtUtc，由恢复 Worker 在
// 退避门通过后重新入队；fail-closed 下进入 17 后主循环不得继续执行 Agent 逻辑。
// 人工介入告警钩子：
// RecoveryBlocked / RecoveryCorrupted：每次进入均告警（数据损坏级，需运维介入）；
// RecoveryDependencyUnavailable：仅首次（attempt==1）告警，避免告警风暴；
// DeadLetterExhausted：Durable Scheduler 死信后告警（重试预算耗尽）；
// best-effort：告警接收器抛异常不阻断恢复状态持久化。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Agent-Actor")]
public sealed class R29Q_RecoveryIntegrityStateTests
{
    private const string WorkspaceId = "ws-r29q-recovery";
    private const string SessionId = "session-r29q-recovery";

    // ---------------------------------------------------------------------------
    // 退避重试：RecoveryDependencyUnavailable 递增 RecoveryAttempt + 计算 NextRetryAtUtc
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task RecoveryDependencyUnavailable_FirstFailure_SetsRecoveryAttemptAndBackoffGate()
    {
        var runStore = new InMemoryAgentRunStore();
        var innerEventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("依赖不可用-首次");
        await runStore.CreateAsync(run);
        await SeedEventsAsync(innerEventStore, run, AgentRunState.ContextBuilding);

        var failingStore = new FailingReadEventStore(innerEventStore);
        var resumedRun = await GetRequiredRunAsync(runStore, run.RunId);
        var actor = BuildActor(runStore, failingStore, hostOptions: BackoffOptions());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(resumedRun, cts.Token);

        var finalRun = await GetRequiredRunAsync(runStore, run.RunId);
        Assert.AreEqual(AgentRunState.RecoveryDependencyUnavailable, finalRun.State);
        Assert.AreEqual(1, finalRun.RecoveryAttempt,
            "首次进入 RecoveryDependencyUnavailable 时 RecoveryAttempt 应为 1。");
        Assert.IsNotNull(finalRun.NextRetryAtUtc, "退避重试应设置 NextRetryAtUtc（退避门）。");
        // base = 2s：退避门 ≈ now + 2s（容差内断言，避免时钟抖动）。
        var now = DateTimeOffset.UtcNow;
        Assert.IsTrue(finalRun.NextRetryAtUtc > now.AddSeconds(1)
            && finalRun.NextRetryAtUtc < now.AddSeconds(10),
            $"退避门应在 (now+1s, now+10s) 区间，实际 {finalRun.NextRetryAtUtc:o}（base=2s）。");
    }

    [TestMethod]
    public async Task RecoveryDependencyUnavailable_SecondFailure_ExponentialBackoff()
    {
        var runStore = new InMemoryAgentRunStore();
        var innerEventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("依赖不可用-指数退避");
        await runStore.CreateAsync(run);
        await SeedEventsAsync(innerEventStore, run, AgentRunState.ContextBuilding);

        var failingStore = new FailingReadEventStore(innerEventStore);
        var actor = BuildActor(runStore, failingStore, hostOptions: BackoffOptions());

        // 第一次执行 → RecoveryAttempt=1，退避门 gate1 ≈ now + 2s。
        var resumedRun1 = await GetRequiredRunAsync(runStore, run.RunId);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await actor.ExecuteAsync(resumedRun1, cts.Token);
        }
        var runAfterFirst = await GetRequiredRunAsync(runStore, run.RunId);
        Assert.AreEqual(1, runAfterFirst.RecoveryAttempt);
        var gate1 = runAfterFirst.NextRetryAtUtc;
        Assert.IsNotNull(gate1, "首次失败应设置退避门。");

        // 第二次执行（同一 Actor 实例，_turnStartState=17）→ RecoveryAttempt=2，
        // 退避门 ≈ now + 2×base = now + 4s（指数增长，严格晚于 gate1）。
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await actor.ExecuteAsync(runAfterFirst, cts.Token);
        }
        var runAfterSecond = await GetRequiredRunAsync(runStore, run.RunId);
        Assert.AreEqual(AgentRunState.RecoveryDependencyUnavailable, runAfterSecond.State);
        Assert.AreEqual(2, runAfterSecond.RecoveryAttempt,
            "第二次进入 RecoveryDependencyUnavailable 时 RecoveryAttempt 应递增为 2。");
        Assert.IsNotNull(runAfterSecond.NextRetryAtUtc);
        Assert.IsTrue(runAfterSecond.NextRetryAtUtc > gate1,
            "指数退避：第二次退避门应严格晚于第一次（base × 2^(attempt-1)）。");
        var now = DateTimeOffset.UtcNow;
        Assert.IsTrue(runAfterSecond.NextRetryAtUtc > now.AddSeconds(3)
            && runAfterSecond.NextRetryAtUtc < now.AddSeconds(20),
            $"第二次退避门应在 (now+3s, now+20s) 区间，实际 {runAfterSecond.NextRetryAtUtc:o}（2×base=4s）。");
    }

    // ---------------------------------------------------------------------------
    // 人工介入告警钩子
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task RecoveryDependencyUnavailable_FirstAttemptAlerts_SubsequentAttemptsDoNot()
    {
        var runStore = new InMemoryAgentRunStore();
        var innerEventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("依赖不可用-告警频控");
        await runStore.CreateAsync(run);
        await SeedEventsAsync(innerEventStore, run, AgentRunState.ContextBuilding);

        var sink = new RecordingAlertSink();
        var failingStore = new FailingReadEventStore(innerEventStore);
        var actor = BuildActor(runStore, failingStore, hostOptions: BackoffOptions(), alertSink: sink);

        var resumedRun1 = await GetRequiredRunAsync(runStore, run.RunId);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await actor.ExecuteAsync(resumedRun1, cts.Token);
        }
        Assert.AreEqual(1, sink.Alerts.Count,
            "首次进入 RecoveryDependencyUnavailable 应投递 1 条告警（需运维关注依赖故障）。");
        var first = sink.Alerts[0];
        Assert.AreEqual(AgentRunAlertKind.RecoveryDependencyUnavailable, first.Kind);
        Assert.AreEqual(1, first.Attempt, "告警 Attempt 应为 RecoveryAttempt（1）。");
        Assert.AreEqual(run.RunId, first.RunId);
        Assert.AreEqual(WorkspaceId, first.WorkspaceId);

        // 第二次进入（attempt=2）→ 不重复告警（避免告警风暴；依赖长期不恢复由日志巡检发现）。
        var runAfterFirst = await GetRequiredRunAsync(runStore, run.RunId);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await actor.ExecuteAsync(runAfterFirst, cts.Token);
        }
        Assert.AreEqual(1, sink.Alerts.Count,
            "RecoveryDependencyUnavailable 非首次（attempt>1）不得重复投递告警。");
    }

    [TestMethod]
    public async Task RecoveryBlocked_FiresInterventionAlert()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("事件丢失-告警");
        await runStore.CreateAsync(run);

        // Run 状态推进到 ContextBuilding，但事件流为空（事件数据丢失）。
        await runStore.TransitionStateAsync(WorkspaceId, run.RunId, AgentRunState.Created, AgentRunState.ContextBuilding);

        var sink = new RecordingAlertSink();
        var resumedRun = await GetRequiredRunAsync(runStore, run.RunId);
        var actor = BuildActor(runStore, eventStore, alertSink: sink);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(resumedRun, cts.Token);

        var finalRun = await GetRequiredRunAsync(runStore, run.RunId);
        Assert.AreEqual(AgentRunState.RecoveryBlocked, finalRun.State);
        Assert.AreEqual(1, sink.Alerts.Count,
            "RecoveryBlocked（事件数据丢失，需运维介入）应投递告警。");
        Assert.AreEqual(AgentRunAlertKind.RecoveryBlocked, sink.Alerts[0].Kind);
        Assert.AreEqual(0, sink.Alerts[0].Attempt, "数据损坏类告警 Attempt 应为 0。");
    }

    [TestMethod]
    public async Task RecoveryCorrupted_FiresInterventionAlert()
    {
        var runStore = new InMemoryAgentRunStore();
        var innerEventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("事件损坏-告警");
        await runStore.CreateAsync(run);
        await SeedEventsAsync(innerEventStore, run, AgentRunState.ContextBuilding);

        // 追加 ContentHash 与内容不匹配的损坏事件。
        var last = (await innerEventStore.ReadAsync(WorkspaceId, run.RunId)).Last();
        var corrupted = AgentRunEventChain.BuildEvent(
            run.RunId, WorkspaceId, sequence: 2,
            type: AgentRunEventType.ModelCallStarted,
            state: AgentRunState.ContextBuilding,
            payload: """{"modelCallId":"mc-corrupted"}""",
            prevChainHash: last.ContentHash) with { ContentHash = new string('d', 64) };
        await innerEventStore.AppendAsync(corrupted);

        var sink = new RecordingAlertSink();
        var resumedRun = await GetRequiredRunAsync(runStore, run.RunId);
        var actor = BuildActor(runStore, innerEventStore, alertSink: sink);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(resumedRun, cts.Token);

        var finalRun = await GetRequiredRunAsync(runStore, run.RunId);
        Assert.AreEqual(AgentRunState.RecoveryCorrupted, finalRun.State);
        Assert.AreEqual(1, sink.Alerts.Count,
            "RecoveryCorrupted（事件流损坏，需运维介入修复）应投递告警。");
        Assert.AreEqual(AgentRunAlertKind.RecoveryCorrupted, sink.Alerts[0].Kind);
    }

    [TestMethod]
    public async Task AlertSinkThrows_DoesNotBreakRecoveryStatePersistence()
    {
        var runStore = new InMemoryAgentRunStore();
        var innerEventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("告警失败-状态仍持久化");
        await runStore.CreateAsync(run);
        await SeedEventsAsync(innerEventStore, run, AgentRunState.ContextBuilding);

        var failingStore = new FailingReadEventStore(innerEventStore);
        var actor = BuildActor(runStore, failingStore, hostOptions: BackoffOptions(), alertSink: new ThrowingAlertSink());

        var resumedRun = await GetRequiredRunAsync(runStore, run.RunId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(resumedRun, cts.Token);

        // best-effort：告警投递抛异常不得阻断恢复状态持久化（也不得抛出到调用方）。
        var finalRun = await GetRequiredRunAsync(runStore, run.RunId);
        Assert.AreEqual(AgentRunState.RecoveryDependencyUnavailable, finalRun.State,
            "告警接收器抛异常时恢复失败状态仍应持久化（best-effort 钩子）。");
        Assert.AreEqual(1, finalRun.RecoveryAttempt);
        Assert.IsNotNull(finalRun.NextRetryAtUtc);
    }

    // ---------------------------------------------------------------------------
    // fail-closed：进入 RecoveryDependencyUnavailable 后主循环不得继续执行 Agent 逻辑
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task RecoveryDependencyUnavailable_FailClosed_LoopDoesNotExecuteAgentLogic()
    {
        var runStore = new InMemoryAgentRunStore();
        var innerEventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("fail-closed-不继续执行");
        await runStore.CreateAsync(run);
        await SeedEventsAsync(innerEventStore, run, AgentRunState.ContextBuilding);

        var failingStore = new FailingReadEventStore(innerEventStore);
        var transport = new FinalAnswerModelTransport();
        var actor = new AgentRunActor(
            runStore, failingStore, transport,
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            hostOptions: BackoffOptions());

        var resumedRun = await GetRequiredRunAsync(runStore, run.RunId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(resumedRun, cts.Token);

        var finalRun = await GetRequiredRunAsync(runStore, run.RunId);
        Assert.AreEqual(AgentRunState.RecoveryDependencyUnavailable, finalRun.State,
            "依赖不可用时应进入 RecoveryDependencyUnavailable（fail-closed，不回退全新启动）。");
        Assert.AreEqual(0, transport.CallCount,
            "进入恢复失败状态后主循环不得继续执行（不得调用模型）。");
        var events = await innerEventStore.ReadAsync(WorkspaceId, run.RunId);
        Assert.AreEqual(2, events.Count,
            "恢复失败路径不得追加新事件（无 ModelCallStarted / RunCreated 重放）。");
    }

    // ---------------------------------------------------------------------------
    // Durable Scheduler：死信告警钩子
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task Claimer_DeadLetterExhausted_FiresInterventionAlert()
    {
        var inner = new InMemoryAgentRunStore();
        var deadRun = BuildRun("死信告警", maxRetries: 3) with
        {
            State = AgentRunState.DeadLettered,
            RetryCount = 3
        };
        await inner.CreateAsync(deadRun);
        // DeadLetterExhaustedRunsAsync 返回已死信 Run（模拟死信批次）；领取返回空。
        var store = new DeadLetterReturningStore(inner, deadRun);
        var sink = new RecordingAlertSink();

        var services = new ServiceCollection();
        services.AddSingleton<IAgentRunStore>(store);
        services.AddSingleton<IPersistentAgentRunStore>(store);
        services.AddSingleton<IAgentRunEventStore>(new InMemoryAgentRunEventStore(inner));
        services.AddSingleton<IAgentModelTransport>(new DeterministicAgentModelTransport());
        services.AddSingleton<IToolDispatcher>(new NoopToolDispatcher());
        services.AddSingleton<IRecoveryAlertSink>(sink);
        services.AddSingleton(new AgentHostOptions
        {
            PendingClaimInterval = TimeSpan.FromMilliseconds(50),
            PendingClaimBatchSize = 10,
            PendingClaimPerWorkspace = 5,
            DeadLetterBatchSize = 10,
            ChannelCapacity = 16,
            WorkerCount = 1,
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
            // 等待 claimer 死信批次投递告警。
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && sink.Alerts.Count == 0)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }

            Assert.IsTrue(sink.Alerts.Count > 0, "死信批次后应投递 DeadLetterExhausted 告警。");
            var alert = sink.Alerts[0];
            Assert.AreEqual(AgentRunAlertKind.DeadLetterExhausted, alert.Kind);
            Assert.AreEqual(deadRun.RunId, alert.RunId);
            Assert.AreEqual(deadRun.WorkspaceId, alert.WorkspaceId);
            Assert.AreEqual(deadRun.RetryCount, alert.Attempt,
                "死信告警 Attempt 应为 RetryCount（重试预算耗尽次数）。");
        }
        finally
        {
            await claimer.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------------------

    private static AgentHostOptions BackoffOptions() => new()
    {
        RetryBackoffBase = TimeSpan.FromSeconds(2),
        RetryBackoffMax = TimeSpan.FromMinutes(1)
    };

    private static AgentRunActor BuildActor(
        IAgentRunStore runStore,
        IAgentRunEventStore eventStore,
        AgentHostOptions? hostOptions = null,
        IRecoveryAlertSink? alertSink = null)
        => new(
            runStore, eventStore, new FinalAnswerModelTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            hostOptions: hostOptions, alertSink: alertSink);

    /// <summary>预置合法事件流（RunCreated + StateTransition(ContextBuilding)）并将 Run 状态推进到指定状态。</summary>
    private static async Task SeedEventsAsync(
        InMemoryAgentRunEventStore eventStore,
        AgentRun run,
        AgentRunState targetState)
    {
        var seq0 = AgentRunEventChain.BuildEvent(
            run.RunId, run.WorkspaceId, sequence: 0,
            type: AgentRunEventType.RunCreated,
            state: AgentRunState.Created,
            payload: """{"runId":"seed"}""",
            prevChainHash: null);
        var seq1 = AgentRunEventChain.BuildEvent(
            run.RunId, run.WorkspaceId, sequence: 1,
            type: AgentRunEventType.StateTransition,
            state: targetState,
            payload: $$"""{"from":"Created","to":"{{targetState}}"}""",
            prevChainHash: seq0.ContentHash);

        var runStateUpdate = new AgentRunStateUpdate
        {
            WorkspaceId = run.WorkspaceId,
            RunId = run.RunId,
            ExpectedCurrentState = AgentRunState.Created,
            NewState = targetState,
            RunSnapshot = run with { State = targetState, UpdatedAt = DateTimeOffset.UtcNow }
        };
        await eventStore.AppendBatchAsync(
            [seq0, seq1], runStateUpdate, checkpointCursor: null, checkpointBody: null, CancellationToken.None);
    }

    private static async Task<AgentRun> GetRequiredRunAsync(IAgentRunStore runStore, string runId)
    {
        var run = await runStore.GetAsync(WorkspaceId, runId);
        Assert.IsNotNull(run, "Run 应存在。");
        return run!;
    }

    private static AgentRun BuildRun(string task, int maxRetries = 0) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
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
        MaxRetries = maxRetries
    };

    /// <summary>直接返回最终答案的模型传输 stub（断言主循环是否执行 Agent 逻辑）。</summary>
    private sealed class FinalAnswerModelTransport : IAgentModelTransport
    {
        public int CallCount { get; private set; }

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, string context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(FinalAnswer());

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(FinalAnswer());

        public ValueTask<AgentModelResponse> CallAsync(
            AgentModelRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(FinalAnswer());

        private AgentModelResponse FinalAnswer()
        {
            CallCount++;
            return new AgentModelResponse
            {
                Content = "已完成",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 10,
                Duration = TimeSpan.FromMilliseconds(1)
            };
        }
    }

    /// <summary>读取失败、写入委托内层的 IAgentRunEventStore 装饰器（模拟存储读故障）。</summary>
    private sealed class FailingReadEventStore : IAgentRunEventStore
    {
        private readonly IAgentRunEventStore _inner;

        public FailingReadEventStore(IAgentRunEventStore inner) => _inner = inner;

        public ValueTask AppendAsync(
            AgentRunEvent @event, CancellationToken cancellationToken = default,
            string? leaseToken = null, long? fencingToken = null)
            => _inner.AppendAsync(@event, cancellationToken, leaseToken, fencingToken);

        public ValueTask AppendBatchAsync(
            IReadOnlyList<AgentRunEvent> events, AgentRunStateUpdate? runStateUpdate,
            AgentCheckpointCursor? checkpointCursor, AgentCheckpoint? checkpointBody,
            CancellationToken cancellationToken = default)
            => _inner.AppendBatchAsync(events, runStateUpdate, checkpointCursor, checkpointBody, cancellationToken);

        public ValueTask<IReadOnlyList<AgentRunEvent>> ReadAsync(
            string workspaceId, string runId, int fromSequence = 0, int take = 1000,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("事件存储读取失败（模拟存储故障）。");

        public ValueTask<int> GetLastSequenceAsync(
            string workspaceId, string runId, CancellationToken cancellationToken = default)
            => _inner.GetLastSequenceAsync(workspaceId, runId, cancellationToken);

        public ValueTask<AgentCheckpointCursor?> GetCheckpointCursorAsync(
            string workspaceId, string runId, CancellationToken cancellationToken = default)
            => _inner.GetCheckpointCursorAsync(workspaceId, runId, cancellationToken);
    }

    /// <summary>线程安全告警录制器。</summary>
    private sealed class RecordingAlertSink : IRecoveryAlertSink
    {
        private readonly ConcurrentQueue<AgentRunAlert> _alerts = new();

        public IReadOnlyList<AgentRunAlert> Alerts => _alerts.ToArray();

        public ValueTask NotifyInterventionRequiredAsync(AgentRunAlert alert, CancellationToken cancellationToken = default)
        {
            _alerts.Enqueue(alert);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>始终抛异常的告警接收器（验证 best-effort 语义）。</summary>
    private sealed class ThrowingAlertSink : IRecoveryAlertSink
    {
        public ValueTask NotifyInterventionRequiredAsync(AgentRunAlert alert, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("告警通道不可用（模拟投递失败）。");
    }

    /// <summary>
    /// IPersistentAgentRunStore 包装器：委托给 InMemoryAgentRunStore；
    /// DeadLetterExhaustedRunsAsync 返回预置死信 Run（模拟死信批次命中），
    /// ClaimPendingBatchAsync 返回空（本测试仅验证死信告警钩子）。
    /// </summary>
    private sealed class DeadLetterReturningStore : IPersistentAgentRunStore
    {
        private readonly InMemoryAgentRunStore _inner;
        private readonly AgentRun _deadRun;

        public DeadLetterReturningStore(InMemoryAgentRunStore inner, AgentRun deadRun)
        {
            _inner = inner;
            _deadRun = deadRun;
        }

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

        public ValueTask<IReadOnlyList<AgentRun>> ClaimPendingBatchAsync(
            int take, int perWorkspace, TimeSpan retryBackoffBase, TimeSpan retryBackoffMax,
            string claimOwner, TimeSpan claimDuration,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentRun>>(Array.Empty<AgentRun>());

        // P0-8 Scheduler Claim 接口成员：死信测试不使用领取路径 → 不可领取/释放失败。
        public ValueTask<AgentRun?> TryClaimSingleAsync(
            string workspaceId, string runId, string claimOwner, TimeSpan claimDuration,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentRun?>(null);

        public ValueTask<bool> ReleaseClaimAsync(
            string workspaceId, string runId, string claimToken,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<IReadOnlyList<AgentRun>> DeadLetterExhaustedRunsAsync(
            int take, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentRun>>([_deadRun]);
    }

    /// <summary>
    /// 空操作 IToolDispatcher 实现，仅支持空 tool 名集合。
    /// 测试中 transport 直接产出最终答案，不会触发 tool 分派。
    /// </summary>
    private sealed class NoopToolDispatcher : IToolDispatcher
    {
        private static readonly IReadOnlySet<string> s_empty = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlySet<string> SupportedTools => s_empty;

        public ToolDescriptor? GetDescriptor(string toolName) => null;

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolDispatchRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ToolDispatchResult
            {
                Succeeded = true,
                Result = "{}",
                Error = null,
                Duration = TimeSpan.Zero,
                SideEffect = ToolSideEffect.None
            });
    }
}
