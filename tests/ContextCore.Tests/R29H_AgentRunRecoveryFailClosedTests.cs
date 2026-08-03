using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// Agent Run 恢复 fail-closed—— 恢复失败状态 + 哈希重算 + 不回退全新启动
//
// 覆盖范围：
// RecoveryCorrupted：事件 ContentHash 重算不匹配 → 标记 RecoveryCorrupted（不重放 RunCreated）；
// RecoveryDependencyUnavailable：事件存储读取失败 → 标记 RecoveryDependencyUnavailable；
// RecoveryBlocked：非 Created 状态但事件流为空（数据丢失）→ 标记 RecoveryBlocked；
// Created + 零事件：仍走全新启动路径（无回归）；
// 状态机：RecoveryBlocked / RecoveryCorrupted 为终态、RecoveryDependencyUnavailable 可重试
// （退避重试），任意非终态可跳入恢复失败状态，终态不可跳出。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Agent-Actor")]
public sealed class R29H_AgentRunRecoveryFailClosedTests
{
    private const string WorkspaceId = "ws-r29h-recovery";
    private const string SessionId = "session-r29h-recovery";

    // ---------------------------------------------------------------------------
    // RecoveryCorrupted：事件 ContentHash 重算不匹配
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task RecoveryCorrupted_ContentHashMismatch_MarksRunRecoveryCorrupted()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("恢复损坏验证");
        await runStore.CreateAsync(run);

        // 预置合法事件流：RunCreated + StateTransition(ContextBuilding)，Run 状态推进到 ContextBuilding。
        await SeedEventsAsync(eventStore, run, AgentRunState.ContextBuilding);

        // 追加一个 ContentHash 与内容不匹配的损坏事件（AppendAsync 不校验 ContentHash，仅校验链链接）。
        var last = (await eventStore.ReadAsync(WorkspaceId, run.RunId)).Last();
        var corrupted = AgentRunEventChain.BuildEvent(
            run.RunId, WorkspaceId, sequence: 2,
            type: AgentRunEventType.ModelCallStarted,
            state: AgentRunState.ContextBuilding,
            payload: """{"modelCallId":"mc-corrupted"}""",
            prevChainHash: last.ContentHash) with { ContentHash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef" };
        await eventStore.AppendAsync(corrupted);

        var resumedRun = await GetRequiredRunAsync(runStore, run.RunId);
        var actor = BuildActor(runStore, eventStore);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(resumedRun, cts.Token);

        // fail-closed：Run 应被持久化标记为 RecoveryCorrupted，而非回退为全新启动。
        var finalRun = await GetRequiredRunAsync(runStore, run.RunId);
        Assert.AreEqual(AgentRunState.RecoveryCorrupted, finalRun.State,
            "ContentHash 重算不匹配时 Run 必须进入 RecoveryCorrupted（fail-closed）。");

        // 不得重放 RunCreated（fail-open 旧行为会缓冲新 RunCreated 事件）。
        var events = await eventStore.ReadAsync(WorkspaceId, run.RunId);
        var runCreatedCount = events.Count(e => e.EventType == AgentRunEventType.RunCreated);
        Assert.AreEqual(1, runCreatedCount, "恢复失败路径不得重放 RunCreated 事件。");
        // 恢复失败状态采用状态直写（run store state CAS + FailureReason），不向事件流追加
        // StateTransition 事件（事件流可能已损坏，追加需要真实尾事件锚点且可能污染待修复流）。
        StringAssert.Contains(finalRun.FailureReason ?? string.Empty, "RecoveryCorrupted",
            "应通过 FailureReason 记录恢复失败原因（RecoveryCorrupted）。");
    }

    // ---------------------------------------------------------------------------
    // RecoveryDependencyUnavailable：事件存储读取失败
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task RecoveryDependencyUnavailable_ReadFails_MarksRunRecoveryDependencyUnavailable()
    {
        var runStore = new InMemoryAgentRunStore();
        var innerEventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("依赖不可用验证");
        await runStore.CreateAsync(run);
        await SeedEventsAsync(innerEventStore, run, AgentRunState.ContextBuilding);

        // 事件存储读取失败（写入委托内层，模拟读故障但写可用 → 恢复失败状态可持久化）。
        var failingStore = new FailingReadEventStore(innerEventStore);

        var resumedRun = await GetRequiredRunAsync(runStore, run.RunId);
        var actor = BuildActor(runStore, failingStore);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(resumedRun, cts.Token);

        var finalRun = await GetRequiredRunAsync(runStore, run.RunId);
        Assert.AreEqual(AgentRunState.RecoveryDependencyUnavailable, finalRun.State,
            "事件存储读取失败时 Run 必须进入 RecoveryDependencyUnavailable（fail-closed）。");
    }

    // ---------------------------------------------------------------------------
    // RecoveryBlocked：非 Created 状态但事件流为空
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task RecoveryBlocked_NonCreatedRunWithZeroEvents_MarksRunRecoveryBlocked()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("事件丢失验证");
        await runStore.CreateAsync(run);

        // Run 状态推进到 ContextBuilding，但事件流为空（事件数据丢失）。
        await runStore.TransitionStateAsync(WorkspaceId, run.RunId, AgentRunState.Created, AgentRunState.ContextBuilding);

        var resumedRun = await GetRequiredRunAsync(runStore, run.RunId);
        var actor = BuildActor(runStore, eventStore);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(resumedRun, cts.Token);

        var finalRun = await GetRequiredRunAsync(runStore, run.RunId);
        Assert.AreEqual(AgentRunState.RecoveryBlocked, finalRun.State,
            "非 Created 状态 + 零事件（事件数据丢失）时 Run 必须进入 RecoveryBlocked，不得回退全新启动。");
    }

    // ---------------------------------------------------------------------------
    // 无回归：Created + 零事件仍走全新启动
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task CreatedRunWithZeroEvents_StillFreshStarts()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("全新启动验证");
        await runStore.CreateAsync(run);

        var transport = new FinalAnswerModelTransport();
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 全新启动路径不应受影响：RunCreated 已写入、模型被调用、Run 正常完成。
        var finalRun = await GetRequiredRunAsync(runStore, run.RunId);
        Assert.AreEqual(AgentRunState.Completed, finalRun.State,
            "Created + 零事件的 Run 应正常走全新启动路径并完成。");
        var events = await eventStore.ReadAsync(WorkspaceId, run.RunId);
        Assert.IsTrue(events.Any(e => e.EventType == AgentRunEventType.RunCreated),
            "全新启动路径应写入 RunCreated 事件。");
        Assert.AreEqual(1, transport.CallCount, "全新启动应调用 1 次模型（返回最终答案后完成）。");
    }

    // ---------------------------------------------------------------------------
    // 状态机：RecoveryBlocked/RecoveryCorrupted 为终态；RecoveryDependencyUnavailable 可重试
    // ---------------------------------------------------------------------------

    [TestMethod]
    public void StateMachine_RecoveryStates_AreTerminalAndReachableFromNonTerminal()
    {
        // RecoveryBlocked / RecoveryCorrupted（数据损坏）为终态；
        // RecoveryDependencyUnavailable（依赖暂时不可用）不再是终态——退避重试，由恢复 Worker 重新入队。
        Assert.IsTrue(AgentRunStateMachine.IsTerminalState(AgentRunState.RecoveryBlocked));
        Assert.IsTrue(AgentRunStateMachine.IsTerminalState(AgentRunState.RecoveryCorrupted));
        Assert.IsFalse(AgentRunStateMachine.IsTerminalState(AgentRunState.RecoveryDependencyUnavailable));
        // 三个状态均属"恢复失败状态"：Actor 主循环据此退出执行槽（fail-closed，不得继续执行）。
        Assert.IsTrue(AgentRunStateMachine.IsRecoveryFailureState(AgentRunState.RecoveryBlocked));
        Assert.IsTrue(AgentRunStateMachine.IsRecoveryFailureState(AgentRunState.RecoveryCorrupted));
        Assert.IsTrue(AgentRunStateMachine.IsRecoveryFailureState(AgentRunState.RecoveryDependencyUnavailable));

        // 任意非终态可跳入恢复失败状态（fail-closed 短路）。
        AgentRunStateMachine.ValidateTransition(AgentRunState.ContextBuilding, AgentRunState.RecoveryBlocked);
        AgentRunStateMachine.ValidateTransition(AgentRunState.ModelCalling, AgentRunState.RecoveryCorrupted);
        AgentRunStateMachine.ValidateTransition(AgentRunState.ToolDispatching, AgentRunState.RecoveryDependencyUnavailable);
        AgentRunStateMachine.ValidateTransition(AgentRunState.AwaitingApproval, AgentRunState.RecoveryBlocked);

        // RecoveryDependencyUnavailable → ContextBuilding 为合法前向流转（退避重试恢复执行）。
        AgentRunStateMachine.ValidateTransition(AgentRunState.RecoveryDependencyUnavailable, AgentRunState.ContextBuilding);
    }

    [TestMethod]
    public void StateMachine_RecoveryStates_NotReachableFromTerminal()
    {
        // 终态不可再流转（含跳入恢复失败状态）。
        Assert.ThrowsException<InvalidOperationException>(() =>
            AgentRunStateMachine.ValidateTransition(AgentRunState.Completed, AgentRunState.RecoveryBlocked));
        Assert.ThrowsException<InvalidOperationException>(() =>
            AgentRunStateMachine.ValidateTransition(AgentRunState.RecoveryCorrupted, AgentRunState.ContextBuilding));
    }

    // ---------------------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------------------

    private static AgentRunActor BuildActor(IAgentRunStore runStore, IAgentRunEventStore eventStore)
        => new(
            runStore, eventStore, new FinalAnswerModelTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher());

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

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>直接返回最终答案的模型传输 stub（全新启动路径验证用）。</summary>
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
}
