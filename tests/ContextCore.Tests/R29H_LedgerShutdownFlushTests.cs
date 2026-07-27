using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// R29-Hard-Gate：Ledger（AgentRunEventStore）关机刷盘验收测试
//
// 目标：验证 AgentRunActor 在以下"关机"场景下，缓冲事件被正确刷盘到
// IAgentRunEventStore（Ledger）且哈希链完整无断裂：
//   1. 外部 CancellationToken 取消 → TryTransitionToCancelledAsync → FlushPendingEventsAsync
//      （Cancelled 终态 + RunCancelled 事件 + 全部缓冲事件持久化）
//   2. 执行中抛异常 → FailAsync → FlushPendingEventsAsync
//      （Failed 终态 + RunFailed 事件 + 全部缓冲事件持久化）
//   3. 正常完成 → CompleteAsync → FlushPendingEventsAsync
//      （Completed 终态 + RunCompleted 事件 + 全部缓冲事件持久化）
//   4. mid-turn 缓冲超过阈值（32）→ 强制 flush
//      （长 Turn 内事件分批持久化，最终哈希链仍连续）
//   5. 哈希链完整性：取消/异常/完成 flush 后，PrevChainHash 链接无断裂
//   6. 多次 flush（mid-turn + 终态）后 Sequence 单调递增、PrevChainHash 链接正确
//   7. Postgres 持久化场景：进程A flush 后进程B 可读取完整事件流（Docker 不可用时 Inconclusive）
//   8. AppendBatchAsync 原子性：单事务内事件 + 状态 CAS + checkpoint 游标同时提交
//
// 设计原则：
//   - 使用真实 InMemoryAgentRunEventStore（非 mock）验证刷盘语义；
//     Postgres 场景使用真实 Testcontainers（Docker 不可用时 Assert.Inconclusive）。
//   - 自定义 ThrowingModelTransport / CancellingModelTransport 触发异常/取消路径。
//   - 所有异步测试使用 CancellationTokenSource 超时防止挂起。
//   - 中文注释。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Ledger-Shutdown-Flush")]
public sealed class R29H_LedgerShutdownFlushTests
{
    // =======================================================================
    // 测试 1：外部 CancellationToken 取消触发的刷盘
    //         验证 Cancelled 终态 + 缓冲事件全部持久化 + 哈希链完整
    // =======================================================================

    /// <summary>
    /// 验证：外部 CancellationToken 取消时，AgentRunActor 调用 TryTransitionToCancelledAsync
    /// 将缓冲事件刷盘到 IAgentRunEventStore；Run 进入 Cancelled 终态且事件流完整。
    /// </summary>
    [TestMethod]
    public async Task Shutdown_CancellationTriggersFlush_PendingEventsPersistedAsCancelled()
    {
        // 准备：InMemory 真实组件 + CancellingModelTransport（在第二次调用时阻塞至取消）
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("取消刷盘验证", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 5
        });
        await runStore.CreateAsync(run);

        // CancellingModelTransport：第一次返回非最终答案触发下一轮，第二次进入调用即等待取消
        var transport = new CancellingModelTransport();

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        // 5 秒后取消
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言 1：Run 进入 Cancelled 终态
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun, "Run 应存在于 store 中。");
        Assert.AreEqual(AgentRunState.Cancelled, finalRun!.State,
            "外部取消后 Run 应进入 Cancelled 终态。");
        Assert.IsNotNull(finalRun.FinishedAt,
            "Cancelled 终态应设置 FinishedAt 时间戳。");

        // 断言 2：事件流应有事件被持久化（至少 RunCreated + StateTransition + RunCancelled）
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        Assert.IsTrue(events.Count >= 3,
            $"取消刷盘后应至少有 3 个事件（RunCreated + StateTransition + RunCancelled），实际 {events.Count}。");

        // 断言 3：应有 RunCancelled 事件标记取消
        var runCancelledEvents = events
            .Where(e => e.EventType == AgentRunEventType.RunCancelled)
            .ToList();
        Assert.AreEqual(1, runCancelledEvents.Count,
            "应有且仅有 1 个 RunCancelled 事件。");

        // 断言 4：链头为 RunCreated
        Assert.AreEqual(AgentRunEventType.RunCreated, events[0].EventType,
            "链头事件应为 RunCreated。");
        Assert.IsNull(events[0].PrevChainHash,
            "链头事件的 PrevChainHash 应为 null。");
    }

    // =======================================================================
    // 测试 2：执行中抛异常触发的刷盘
    //         验证 Failed 终态 + 缓冲事件持久化 + RunFailed 事件
    // =======================================================================

    /// <summary>
    /// 验证：模型调用抛异常时，AgentRunActor 调用 FailAsync 将缓冲事件刷盘；
    /// Run 进入 Failed 终态，事件流包含 RunFailed 事件且哈希链完整。
    /// </summary>
    [TestMethod]
    public async Task Shutdown_ExceptionTriggersFlush_PendingEventsPersistedAsFailed()
    {
        // 准备：InMemory 真实组件 + ThrowingModelTransport（抛 InvalidOperationException）
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("异常刷盘验证");
        await runStore.CreateAsync(run);

        var expectedError = "模拟模型调用失败";
        var transport = new ThrowingModelTransport(new InvalidOperationException(expectedError));

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言 1：Run 进入 Failed 终态
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun, "Run 应存在于 store 中。");
        Assert.AreEqual(AgentRunState.Failed, finalRun!.State,
            "异常后 Run 应进入 Failed 终态。");
        Assert.IsNotNull(finalRun.FinishedAt,
            "Failed 终态应设置 FinishedAt 时间戳。");
        Assert.IsFalse(string.IsNullOrEmpty(finalRun.FailureReason),
            "Failed 终态应设置 FailureReason。");
        Assert.IsTrue(finalRun.FailureReason!.Contains(expectedError),
            $"FailureReason 应包含原始异常消息（{expectedError}）。");

        // 断言 2：事件流应包含 RunFailed 事件
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        var runFailedEvents = events
            .Where(e => e.EventType == AgentRunEventType.RunFailed)
            .ToList();
        Assert.AreEqual(1, runFailedEvents.Count,
            "应有且仅有 1 个 RunFailed 事件。");

        // 断言 3：RunFailed 事件 payload 应包含 reason 字段
        var failedEvent = runFailedEvents[0];
        using var payloadDoc = JsonDocument.Parse(failedEvent.Payload);
        Assert.IsTrue(payloadDoc.RootElement.TryGetProperty("reason", out var reasonEl),
            "RunFailed 事件 payload 应包含 reason 字段。");
        Assert.IsTrue(reasonEl.GetString()!.Contains(expectedError),
            "RunFailed 事件 reason 应包含原始异常消息。");

        // 断言 4：链头为 RunCreated
        Assert.AreEqual(AgentRunEventType.RunCreated, events[0].EventType,
            "链头事件应为 RunCreated。");
    }

    // =======================================================================
    // 测试 3：正常完成触发的刷盘
    //         验证 Completed 终态 + 全部事件持久化 + RunCompleted 事件
    // =======================================================================

    /// <summary>
    /// 验证：正常完成时，AgentRunActor 调用 CompleteAsync 将缓冲事件刷盘；
    /// Run 进入 Completed 终态，事件流包含 RunCompleted 事件且哈希链完整。
    /// </summary>
    [TestMethod]
    public async Task Shutdown_CompleteTriggersFlush_AllEventsPersistedAsCompleted()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("正常完成刷盘验证");
        await runStore.CreateAsync(run);

        var transport = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "最终答案",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 5,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言 1：Completed 终态
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            "正常完成应进入 Completed 终态。");
        Assert.AreEqual("最终答案", finalRun.FinalAnswer,
            "FinalAnswer 应被持久化到 Run Store。");

        // 断言 2：事件流包含 RunCompleted 事件
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        var runCompletedEvents = events
            .Where(e => e.EventType == AgentRunEventType.RunCompleted)
            .ToList();
        Assert.AreEqual(1, runCompletedEvents.Count,
            "应有且仅有 1 个 RunCompleted 事件。");

        // 断言 3：应有 ModelCallCompleted 事件（含最终答案内容）
        var modelCallEvents = events
            .Where(e => e.EventType == AgentRunEventType.ModelCallCompleted)
            .ToList();
        Assert.AreEqual(1, modelCallEvents.Count,
            "应有 1 个 ModelCallCompleted 事件。");
    }

    // =======================================================================
    // 测试 4：mid-turn 缓冲超过阈值（32）触发的强制 flush
    //         验证长 Turn 内事件分批持久化，最终哈希链仍连续
    // =======================================================================

    /// <summary>
    /// 验证：mid-turn 缓冲事件数 >= 32 时强制 flush；多次 flush 后
    /// 全部事件可读取且 Sequence 连续 + PrevChainHash 链接无断裂。
    /// </summary>
    /// <remarks>
    /// AgentRunActor 的 PendingEventsFlushThreshold = 32；本测试通过多次
    /// 模型调用累积事件（每次调用产生 ~3 个事件：StateTransition + ModelCallStarted + ModelCallCompleted），
    /// 触发 mid-turn flush，然后正常完成。最终事件流应连续无缺。
    /// </remarks>
    [TestMethod]
    public async Task Shutdown_MidTurnThresholdFlush_AllEventsChainedCorrectly()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("mid-turn 阈值刷盘验证", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 50,
            TurnsUsed = 0,
            MaxModelCalls = 20  // 足够多次调用以触发 mid-turn flush（每次 ~3 事件，需 11+ 次触发 32 阈值）
        });
        await runStore.CreateAsync(run);

        // 准备 12 次非最终响应 + 第 13 次最终响应（共 13 × 3 ≈ 39 事件，必触发 mid-turn flush）
        var responses = new List<AgentModelResponse>();
        for (var i = 0; i < 12; i++)
        {
            responses.Add(new AgentModelResponse
            {
                Content = $"中间思考 {i + 1}",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = false,
                TokensConsumed = 1,
                Duration = TimeSpan.FromMilliseconds(1)
            });
        }
        responses.Add(new AgentModelResponse
        {
            Content = "最终答案",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 1,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var transport = new SequenceModelTransport(responses.ToArray());
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言 1：Run 完成
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            "应进入 Completed 终态。");

        // 断言 2：事件流读取并校验哈希链完整性
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId, take: 5000);
        Assert.IsTrue(events.Count >= 32,
            $"mid-turn 多轮调用应产生 >= 32 个事件（实际 {events.Count}）。");

        // 断言 3：Sequence 从 0 开始单调递增无间断
        for (var i = 0; i < events.Count; i++)
        {
            Assert.AreEqual(i, events[i].Sequence,
                $"事件 Sequence 应从 0 开始单调递增（位置 {i}，期望 {i}，实际 {events[i].Sequence}）。");
        }

        // 断言 4：PrevChainHash 链接无断裂
        Assert.IsNull(events[0].PrevChainHash,
            "链头事件的 PrevChainHash 应为 null。");
        for (var i = 1; i < events.Count; i++)
        {
            Assert.AreEqual(
                events[i - 1].ContentHash,
                events[i].PrevChainHash,
                $"事件 {i} 的 PrevChainHash 应指向前一事件的 ContentHash（mid-turn flush 后哈希链断裂）。");
        }

        // 断言 5：每个事件 ContentHash 已计算且非空
        foreach (var evt in events)
        {
            Assert.IsFalse(string.IsNullOrEmpty(evt.ContentHash),
                $"事件 {evt.Sequence} 的 ContentHash 不应为空。");
        }

        // 断言 6：AgentRunEventChain.VerifyChain 整体校验通过
        Assert.IsTrue(AgentRunEventChain.VerifyChain(events),
            "AgentRunEventChain.VerifyChain 应通过（Sequence + PrevChainHash + ContentHash 三重校验）。");
    }

    // =======================================================================
    // 测试 5：取消刷盘后哈希链完整性验证
    //         （独立断言 VerifyChain，确保取消路径不破坏哈希链）
    // =======================================================================

    /// <summary>
    /// 验证：取消触发的 flush 后，AgentRunEventChain.VerifyChain 返回 true
    /// （三重校验：Sequence 连续 + PrevChainHash 链接 + ContentHash 重算一致）。
    /// </summary>
    [TestMethod]
    public async Task Shutdown_CancellationFlush_HashChainVerifyChainPasses()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("取消哈希链完整性验证", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 5
        });
        await runStore.CreateAsync(run);

        var transport = new CancellingModelTransport();
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 读取事件流并整体校验
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId, take: 5000);
        Assert.IsTrue(events.Count > 0, "应有事件被刷盘。");

        // 三重校验通过
        Assert.IsTrue(AgentRunEventChain.VerifyChain(events),
            "取消刷盘后 VerifyChain 应通过（Sequence + PrevChainHash + ContentHash 三重校验）。");

        // 每个事件的 ContentHash 重算应与存储值一致
        foreach (var evt in events)
        {
            Assert.IsTrue(AgentRunEventChain.VerifyContentHash(evt),
                $"事件 {evt.Sequence} 的 ContentHash 重算应与存储值一致。");
        }
    }

    // =======================================================================
    // 测试 6：异常刷盘后哈希链完整性验证
    // =======================================================================

    /// <summary>
    /// 验证：异常触发的 flush 后，AgentRunEventChain.VerifyChain 返回 true。
    /// </summary>
    [TestMethod]
    public async Task Shutdown_ExceptionFlush_HashChainVerifyChainPasses()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("异常哈希链完整性验证", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 5
        });
        await runStore.CreateAsync(run);

        var transport = new ThrowingModelTransport(new InvalidOperationException("哈希链测试异常"));
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId, take: 5000);
        Assert.IsTrue(events.Count > 0, "应有事件被刷盘。");

        Assert.IsTrue(AgentRunEventChain.VerifyChain(events),
            "异常刷盘后 VerifyChain 应通过。");

        // 验证 RunFailed 事件本身也参与哈希链
        var lastEvent = events[^1];
        Assert.AreEqual(AgentRunEventType.RunFailed, lastEvent.EventType,
            "最后一个事件应为 RunFailed。");
        Assert.IsFalse(string.IsNullOrEmpty(lastEvent.ContentHash),
            "RunFailed 事件应有 ContentHash。");
    }

    // =======================================================================
    // 测试 7：多次 flush（mid-turn + 终态）后 Sequence 单调递增
    //         验证 _pendingTurnEvents.Clear() 后下一次 flush 不重置 Sequence
    // =======================================================================

    /// <summary>
    /// 验证：mid-turn flush 后 _pendingTurnEvents 清空，但 EventSequence 不重置；
    /// 后续事件继续从上一次的 Sequence +1 开始，跨 flush 边界 Sequence 连续。
    /// </summary>
    [TestMethod]
    public async Task Shutdown_MultipleFlushes_SequenceContinuesAcrossFlushBoundary()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("多次 flush 跨边界验证", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 50,
            TurnsUsed = 0,
            MaxModelCalls = 20
        });
        await runStore.CreateAsync(run);

        // 12 次中间响应 + 1 次最终响应（触发 mid-turn flush + 终态 flush）
        var responses = new List<AgentModelResponse>();
        for (var i = 0; i < 12; i++)
        {
            responses.Add(new AgentModelResponse
            {
                Content = $"中间 {i + 1}",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = false,
                TokensConsumed = 1,
                Duration = TimeSpan.FromMilliseconds(1)
            });
        }
        responses.Add(new AgentModelResponse
        {
            Content = "完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 1,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var transport = new SequenceModelTransport(responses.ToArray());
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(run, cts.Token);

        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId, take: 5000);

        // 断言：Sequence 从 0 连续递增到 events.Count - 1（无空洞）
        // 这验证了 mid-turn flush 后 _pendingTurnEvents.Clear() 不会重置 EventSequence
        for (var i = 0; i < events.Count; i++)
        {
            Assert.AreEqual(i, events[i].Sequence,
                $"跨 flush 边界 Sequence 应连续（位置 {i}）。");
        }

        // 断言：GetLastSequenceAsync 返回值与 events.Count - 1 一致
        var lastSeq = await eventStore.GetLastSequenceAsync(run.WorkspaceId, run.RunId);
        Assert.AreEqual(events.Count - 1, lastSeq,
            "GetLastSequenceAsync 应返回最大 Sequence（events.Count - 1）。");

        // 断言：哈希链整体校验通过
        Assert.IsTrue(AgentRunEventChain.VerifyChain(events),
            "跨 flush 边界 VerifyChain 应通过。");
    }

    // =======================================================================
    // 测试 8：AppendBatchAsync 原子性 — 单事务提交事件 + 状态 CAS
    //         验证 InMemoryAgentRunEventStore.AppendBatchAsync 在委托 runStore 时
    //         事件追加与状态 CAS 一并完成（无中间状态可见）
    // =======================================================================

    /// <summary>
    /// 验证：AppendBatchAsync 在委托 IAgentRunStore 时，事件追加与状态 CAS 同步完成；
    /// 调用返回后立即读取 Run Store 应反映新状态，事件流也应包含全部事件。
    /// </summary>
    [TestMethod]
    public async Task AppendBatchAsync_AppliesEventsAndStateCasAtomically_InMemory()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);

        var run = BuildRun("批量原子性验证");
        run = run with { State = AgentRunState.Created };
        await runStore.CreateAsync(run);

        // 构造 3 个事件：RunCreated(0) → StateTransition(1) → ModelCallStarted(2)
        var events = new List<AgentRunEvent>();
        string? prevHash = null;
        var state = AgentRunState.ContextBuilding;

        for (var i = 0; i < 3; i++)
        {
            var evt = AgentRunEventChain.BuildEvent(
                runId: run.RunId,
                workspaceId: run.WorkspaceId,
                sequence: i,
                type: i == 0 ? AgentRunEventType.RunCreated :
                       i == 1 ? AgentRunEventType.StateTransition :
                                AgentRunEventType.ModelCallStarted,
                state: state,
                payload: $"{{\"seq\":{i}}}",
                prevChainHash: prevHash);
            events.Add(evt);
            prevHash = evt.ContentHash;
        }

        var runStateUpdate = new AgentRunStateUpdate
        {
            WorkspaceId = run.WorkspaceId,
            RunId = run.RunId,
            ExpectedCurrentState = AgentRunState.Created,
            NewState = AgentRunState.ModelCalling,
            RunSnapshot = run with { State = AgentRunState.ModelCalling, UpdatedAt = DateTimeOffset.UtcNow }
        };

        // 执行批量提交
        await eventStore.AppendBatchAsync(events, runStateUpdate, checkpointCursor: null);

        // 断言 1：Run Store 状态已更新为 ModelCalling（CAS 委托成功）
        var updatedRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(updatedRun);
        Assert.AreEqual(AgentRunState.ModelCalling, updatedRun!.State,
            "AppendBatchAsync 后 Run 状态应被 CAS 推进到 ModelCalling。");

        // 断言 2：事件流包含 3 个事件
        var persisted = await eventStore.ReadAsync(run.WorkspaceId, run.RunId, take: 100);
        Assert.AreEqual(3, persisted.Count,
            "AppendBatchAsync 后应有 3 个事件持久化。");

        // 断言 3：GetLastSequenceAsync 返回 2
        var lastSeq = await eventStore.GetLastSequenceAsync(run.WorkspaceId, run.RunId);
        Assert.AreEqual(2, lastSeq,
            "GetLastSequenceAsync 应返回 2（最后事件 Sequence）。");

        // 断言 4：哈希链完整
        Assert.IsTrue(AgentRunEventChain.VerifyChain(persisted),
            "批量提交后哈希链应完整。");
    }

    // =======================================================================
    // 测试 9：AppendBatchAsync CAS 失败抛异常，事件不被持久化
    //         验证 expected-state CAS 失败时的回滚语义
    // =======================================================================

    /// <summary>
    /// 验证：AppendBatchAsync 中 Run 状态 CAS 失败（ExpectedCurrentState 不匹配）时
    /// 抛 InvalidOperationException；InMemory 实现委托 IAgentRunStore.TransitionStateAsync
    /// 失败，事件虽已追加到 _events 但状态未推进（开发/测试非原子语义，与 Postgres 单事务不同）。
    /// </summary>
    /// <remarks>
    /// 注意：InMemoryAgentRunEventStore 实现非原子（先追加事件，再委托状态 CAS）。
    /// 此测试验证 CAS 失败时抛异常；Postgres 单事务原子性由 Postgres 测试覆盖。
    /// </remarks>
    [TestMethod]
    public async Task AppendBatchAsync_StateCasFails_ThrowsInvalidOperationException()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);

        var run = BuildRun("CAS 失败验证");
        await runStore.CreateAsync(run);
        // 注意：Run 状态为 Created，但 CAS 期望 ContextBuilding → 不匹配

        var evt = AgentRunEventChain.BuildEvent(
            runId: run.RunId,
            workspaceId: run.WorkspaceId,
            sequence: 0,
            type: AgentRunEventType.RunCreated,
            state: AgentRunState.Created,
            payload: "{}",
            prevChainHash: null);

        var wrongStateUpdate = new AgentRunStateUpdate
        {
            WorkspaceId = run.WorkspaceId,
            RunId = run.RunId,
            ExpectedCurrentState = AgentRunState.ContextBuilding,  // 故意写错：实际是 Created
            NewState = AgentRunState.ModelCalling,
            RunSnapshot = run with { State = AgentRunState.ModelCalling }
        };

        // 断言：CAS 失败抛 InvalidOperationException
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await eventStore.AppendBatchAsync(
                new[] { evt }, wrongStateUpdate, checkpointCursor: null),
            "ExpectedCurrentState 不匹配时应抛 InvalidOperationException。");

        // 断言：Run 状态未被推进（仍为 Created）
        var unchangedRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(unchangedRun);
        Assert.AreEqual(AgentRunState.Created, unchangedRun!.State,
            "CAS 失败时 Run 状态不应被推进。");
    }

    // =======================================================================
    // 测试 10：Postgres 持久化刷盘 — 进程A flush 后进程B 可读取完整事件流
    //          Docker 不可用时 Assert.Inconclusive
    // =======================================================================

    /// <summary>
    /// 验证：PostgresAgentRunEventStore.AppendBatchAsync 单事务原子提交；
    /// 进程A（store 实例 1）批量 flush 后，进程B（store 实例 2，同 DB）能读取完整事件流
    /// 且哈希链完整无断裂。模拟进程重启后审计流可读场景。
    /// </summary>
    [TestMethod]
    public async Task Postgres_AppendBatchAsync_PersistsAcrossStoreInstances()
    {
        if (ShouldSkipPostgres) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明 Postgres 持久化刷盘通过。"); return; }

        var (factory, migrationRunner, serializer) = CreatePostgresInfrastructure("lsf1_");
        try
        {
            // ── 进程A：构造 store 实例 1，批量 flush 5 个事件 ──
            var storeA = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);

            var runId = "run-pg-flush-1";
            var workspaceId = "ws-pg-flush";

            var events = new List<AgentRunEvent>();
            string? prevHash = null;
            for (var i = 0; i < 5; i++)
            {
                var evt = AgentRunEventChain.BuildEvent(
                    runId: runId,
                    workspaceId: workspaceId,
                    sequence: i,
                    type: i == 0 ? AgentRunEventType.RunCreated :
                           i == 4 ? AgentRunEventType.RunCompleted :
                                    AgentRunEventType.StateTransition,
                    state: AgentRunState.Completed,
                    payload: $"{{\"i\":{i}}}",
                    prevChainHash: prevHash);
                events.Add(evt);
                prevHash = evt.ContentHash;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await storeA.AppendBatchAsync(events, runStateUpdate: null, checkpointCursor: null, cts.Token);

            // ── 模拟进程重启：丢弃 storeA，构造 storeB（同 DB，新实例）──
            var storeB = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);

            // 断言 1：storeB 能读取全部 5 个事件
            var persisted = await storeB.ReadAsync(workspaceId, runId, 0, 100, cts.Token);
            Assert.AreEqual(5, persisted.Count,
                $"进程B 应能读取进程A flush 的 5 个事件（实际 {persisted.Count}）。");

            // 断言 2：Sequence 从 0 单调递增
            for (var i = 0; i < persisted.Count; i++)
            {
                Assert.AreEqual(i, persisted[i].Sequence,
                    $"Sequence 应从 0 单调递增（位置 {i}）。");
            }

            // 断言 3：哈希链完整
            Assert.IsTrue(AgentRunEventChain.VerifyChain(persisted),
                "Postgres 跨进程刷盘后 VerifyChain 应通过。");

            // 断言 4：GetLastSequenceAsync 返回 4
            var lastSeq = await storeB.GetLastSequenceAsync(workspaceId, runId, cts.Token);
            Assert.AreEqual(4, lastSeq,
                "GetLastSequenceAsync 应返回 4。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 11：Postgres AppendBatchAsync CAS 失败时事务回滚 — 事件不被持久化
    //          验证 Postgres 单事务原子性（与 InMemory 非原子对比）
    // =======================================================================

    /// <summary>
    /// 验证：PostgresAgentRunEventStore.AppendBatchAsync 在 Run 状态 CAS 失败时
    /// 整个事务回滚——事件不被持久化，Run 状态不变。
    /// </summary>
    [TestMethod]
    public async Task Postgres_AppendBatchAsync_CasFails_RollsBackTransaction()
    {
        if (ShouldSkipPostgres) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreatePostgresInfrastructure("lsf2_");
        try
        {
            var runStore = new PostgresAgentRunStore(factory, serializer, migrationRunner);
            var eventStore = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);

            var runId = "run-pg-cas-1";
            var workspaceId = "ws-pg-cas";

            // 创建 Run（state=Created）
            var run = new AgentRun
            {
                RunId = runId,
                WorkspaceId = workspaceId,
                SessionId = "session-pg-cas",
                Task = "Postgres CAS 回滚测试",
                State = AgentRunState.Created,
                Turn = 0,
                ModelCallsUsed = 0,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await runStore.CreateAsync(run, cts.Token);

            // 构造 1 个事件 + 错误的 CAS（期望 ContextBuilding，实际 Created）
            var evt = AgentRunEventChain.BuildEvent(
                runId: runId,
                workspaceId: workspaceId,
                sequence: 0,
                type: AgentRunEventType.RunCreated,
                state: AgentRunState.Created,
                payload: "{}",
                prevChainHash: null);

            var wrongStateUpdate = new AgentRunStateUpdate
            {
                WorkspaceId = workspaceId,
                RunId = runId,
                ExpectedCurrentState = AgentRunState.ContextBuilding,  // 故意写错
                NewState = AgentRunState.ModelCalling,
                RunSnapshot = run with { State = AgentRunState.ModelCalling }
            };

            // 断言 1：CAS 失败抛 InvalidOperationException
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await eventStore.AppendBatchAsync(
                    new[] { evt }, wrongStateUpdate, checkpointCursor: null, cts.Token),
                "Postgres CAS 失败应抛 InvalidOperationException。");

            // 断言 2：事务回滚 — 事件未被持久化
            var persisted = await eventStore.ReadAsync(workspaceId, runId, 0, 100, cts.Token);
            Assert.AreEqual(0, persisted.Count,
                "Postgres 单事务回滚后事件不应被持久化（与 InMemory 非原子行为不同）。");

            // 断言 3：Run 状态仍为 Created
            var unchangedRun = await runStore.GetAsync(workspaceId, runId, cts.Token);
            Assert.IsNotNull(unchangedRun);
            Assert.AreEqual(AgentRunState.Created, unchangedRun!.State,
                "Postgres 事务回滚后 Run 状态不应被推进。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 12：Postgres AppendBatchAsync 含状态 CAS + checkpoint 游标 — 单事务提交
    //          验证三件套（事件 + 状态 CAS + checkpoint 游标）原子提交
    // =======================================================================

    /// <summary>
    /// 验证：PostgresAgentRunEventStore.AppendBatchAsync 同时提交事件 + 状态 CAS + checkpoint 游标
    /// 在单事务内完成；调用返回后 agent_runs 行的 state / last_checkpoint_id / last_checkpoint_sequence
    /// 全部反映新值。
    /// </summary>
    [TestMethod]
    public async Task Postgres_AppendBatchAsync_EventPlusStateCasPlusCursor_SingleTransaction()
    {
        if (ShouldSkipPostgres) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreatePostgresInfrastructure("lsf3_");
        try
        {
            var runStore = new PostgresAgentRunStore(factory, serializer, migrationRunner);
            var eventStore = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);

            var runId = "run-pg-triple-1";
            var workspaceId = "ws-pg-triple";

            var run = new AgentRun
            {
                RunId = runId,
                WorkspaceId = workspaceId,
                SessionId = "session-pg-triple",
                Task = "Postgres 三件套原子提交测试",
                State = AgentRunState.Created,
                Turn = 0,
                ModelCallsUsed = 0,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await runStore.CreateAsync(run, cts.Token);

            // 构造 2 个事件 + 正确的 CAS（Created → ContextBuilding）+ checkpoint 游标
            var events = new List<AgentRunEvent>();
            string? prevHash = null;
            for (var i = 0; i < 2; i++)
            {
                var evt = AgentRunEventChain.BuildEvent(
                    runId: runId,
                    workspaceId: workspaceId,
                    sequence: i,
                    type: i == 0 ? AgentRunEventType.RunCreated : AgentRunEventType.StateTransition,
                    state: AgentRunState.ContextBuilding,
                    payload: $"{{\"i\":{i}}}",
                    prevChainHash: prevHash);
                events.Add(evt);
                prevHash = evt.ContentHash;
            }

            var runStateUpdate = new AgentRunStateUpdate
            {
                WorkspaceId = workspaceId,
                RunId = runId,
                ExpectedCurrentState = AgentRunState.Created,
                NewState = AgentRunState.ContextBuilding,
                RunSnapshot = run with
                {
                    State = AgentRunState.ContextBuilding,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            };

            var checkpointCursor = new AgentCheckpointCursor
            {
                WorkspaceId = workspaceId,
                RunId = runId,
                CheckpointId = "ckpt-triple-1",
                LastEventSequence = 1
            };

            // 执行三件套批量提交
            await eventStore.AppendBatchAsync(events, runStateUpdate, checkpointCursor, cts.Token);

            // 断言 1：事件已持久化（2 个）
            var persisted = await eventStore.ReadAsync(workspaceId, runId, 0, 100, cts.Token);
            Assert.AreEqual(2, persisted.Count,
                "应持久化 2 个事件。");

            // 断言 2：Run 状态已推进到 ContextBuilding
            var updatedRun = await runStore.GetAsync(workspaceId, runId, cts.Token);
            Assert.IsNotNull(updatedRun);
            Assert.AreEqual(AgentRunState.ContextBuilding, updatedRun!.State,
                "Run 状态应被 CAS 推进到 ContextBuilding。");

            // 断言 3：哈希链完整
            Assert.IsTrue(AgentRunEventChain.VerifyChain(persisted),
                "三件套提交后哈希链应完整。");

            // 断言 4：GetLastSequenceAsync 返回 1（最后事件 Sequence）
            var lastSeq = await eventStore.GetLastSequenceAsync(workspaceId, runId, cts.Token);
            Assert.AreEqual(1, lastSeq,
                "GetLastSequenceAsync 应返回 1。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 13：空批 + 无状态更新 → AppendBatchAsync 直接返回（no-op）
    //         验证刷盘边界条件：无事件时不抛异常、不写 DB
    // =======================================================================

    /// <summary>
    /// 验证：AppendBatchAsync 在空批 + 无状态更新时直接返回，不抛异常、不写 DB。
    /// 这对应 FlushPendingEventsAsync 的早退路径（_pendingTurnEvents.Count == 0）。
    /// </summary>
    [TestMethod]
    public async Task AppendBatchAsync_EmptyBatchWithNoStateUpdate_IsNoOp()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);

        // 空批 + 无状态更新 → 应直接返回（不抛异常）
        await eventStore.AppendBatchAsync(
            Array.Empty<AgentRunEvent>(),
            runStateUpdate: null,
            checkpointCursor: null);

        // 断言：无事件被持久化
        var lastSeq = await eventStore.GetLastSequenceAsync("ws-empty", "run-empty");
        Assert.AreEqual(-1, lastSeq,
            "空批后 GetLastSequenceAsync 应返回 -1（无事件）。");
    }

    // =======================================================================
    // 测试 14：Sequence 不连续时 AppendAsync 抛异常（哈希链防断裂保护）
    // =======================================================================

    /// <summary>
    /// 验证：直接调用 AppendAsync 提交 Sequence 不连续的事件时抛 InvalidOperationException；
    /// 这是 Ledger 的不变量保护，确保外部代码无法绕过 BufferEvent 的 Sequence 单调递增逻辑。
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_SequenceGap_ThrowsInvalidOperationException()
    {
        var eventStore = new InMemoryAgentRunEventStore();

        // 链头事件 Sequence=0（合法）
        var evt0 = AgentRunEventChain.BuildEvent(
            runId: "run-gap",
            workspaceId: "ws-gap",
            sequence: 0,
            type: AgentRunEventType.RunCreated,
            state: AgentRunState.Created,
            payload: "{}",
            prevChainHash: null);
        await eventStore.AppendAsync(evt0);

        // 跳过 Sequence=1，直接提交 Sequence=2（非法）
        var evt2 = AgentRunEventChain.BuildEvent(
            runId: "run-gap",
            workspaceId: "ws-gap",
            sequence: 2,  // 故意跳过 1
            type: AgentRunEventType.StateTransition,
            state: AgentRunState.ContextBuilding,
            payload: "{}",
            prevChainHash: evt0.ContentHash);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await eventStore.AppendAsync(evt2),
            "Sequence 不连续应抛 InvalidOperationException（防哈希链断裂）。");
    }

    // =======================================================================
    // 测试 15：PrevChainHash 不匹配时 AppendAsync 抛异常
    // =======================================================================

    /// <summary>
    /// 验证：直接调用 AppendAsync 提交 PrevChainHash 不匹配的事件时抛 InvalidOperationException。
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_PrevChainHashMismatch_ThrowsInvalidOperationException()
    {
        var eventStore = new InMemoryAgentRunEventStore();

        var evt0 = AgentRunEventChain.BuildEvent(
            runId: "run-mismatch",
            workspaceId: "ws-mismatch",
            sequence: 0,
            type: AgentRunEventType.RunCreated,
            state: AgentRunState.Created,
            payload: "{}",
            prevChainHash: null);
        await eventStore.AppendAsync(evt0);

        // 故意构造错误的 PrevChainHash（与 evt0.ContentHash 不匹配）
        var evt1 = AgentRunEventChain.BuildEvent(
            runId: "run-mismatch",
            workspaceId: "ws-mismatch",
            sequence: 1,
            type: AgentRunEventType.StateTransition,
            state: AgentRunState.ContextBuilding,
            payload: "{}",
            prevChainHash: "deadbeef-wrong-hash-not-matching");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await eventStore.AppendAsync(evt1),
            "PrevChainHash 不匹配应抛 InvalidOperationException。");
    }

    // ── Postgres 基础设施 ────────────────────────────────────────────────

    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        // 直接尝试启动容器（与 R29H_DurableKernelCrashRecoveryTests 一致），
        // 避免 IsDockerAvailableAsync 在 Windows named-pipe Docker Desktop 上误判。
        try
        {
            _container = new PostgreSqlBuilder(PgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();

            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[R29H_LedgerShutdownFlushTests] Docker 不可用：{ex.GetType().Name}: {ex.Message}");
            _connectionString = null;
        }
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static async Task ClassCleanup()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static bool ShouldSkipPostgres => _connectionString is null;

    /// <summary>构建 Postgres 测试基础设施（factory + migrationRunner + serializer）。</summary>
    private static (PostgresConnectionFactory factory, PostgresMigrationRunner migrationRunner, PostgresJsonSerializer serializer) CreatePostgresInfrastructure(string prefix)
    {
        var options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = prefix
        };
        var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        return (factory, migrationRunner, serializer);
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────

    /// <summary>构建默认 AgentRun（Created 状态，可选 TurnBudget）。</summary>
    private static AgentRun BuildRun(
        string task,
        AgentTurnBudget? turnBudget = null) => new()
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-r29h-ledger",
            SessionId = "session-r29h-ledger",
            Task = task,
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = turnBudget
        };

    // ── 测试 stub ─────────────────────────────────────────────────────────

    /// <summary>
    /// 录制模型调用入参的 IAgentModelTransport stub。
    /// 每次调用返回构造时指定的固定响应，并捕获入参。
    /// </summary>
    private sealed class RecordingModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse _response;

        public RecordingModelTransport(AgentModelResponse response)
        {
            _response = response;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_response);
    }

    /// <summary>
    /// 按顺序返回预设响应序列的 IAgentModelTransport stub。
    /// 第 N 次调用返回第 N 个响应（超出序列时返回最后一个）。
    /// </summary>
    private sealed class SequenceModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse[] _responses;
        private int _callCount;

        public SequenceModelTransport(AgentModelResponse[] responses)
        {
            _responses = responses;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            var response = index < _responses.Length ? _responses[index] : _responses[^1];
            return ValueTask.FromResult(response);
        }
    }

    /// <summary>
    /// 第一次调用返回非最终答案（触发下一轮），第二次调用阻塞至 cancellationToken 取消。
    /// 用于测试外部 CancellationToken 取消时的 flush 行为。
    /// </summary>
    private sealed class CancellingModelTransport : IAgentModelTransport
    {
        private int _callCount;

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _callCount);
            if (count == 1)
            {
                // 第一次：返回非最终答案，触发下一轮模型调用
                return ValueTask.FromResult(new AgentModelResponse
                {
                    Content = "继续",
                    ToolCalls = Array.Empty<AgentToolCallRequest>(),
                    IsFinalAnswer = false,
                    TokensConsumed = 1,
                    Duration = TimeSpan.FromMilliseconds(1)
                });
            }

            // 第二次：等待取消（模拟长耗时模型调用被外部取消）
            return new ValueTask<AgentModelResponse>(WaitUntilCancelled(cancellationToken));
        }

        private static async Task<AgentModelResponse> WaitUntilCancelled(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null!;  // 永不执行（cancellationToken 触发后 Task.Delay 抛 OperationCanceledException）
        }
    }

    /// <summary>
    /// 模型调用直接抛指定异常的 IAgentModelTransport stub。
    /// 用于测试异常路径的 flush 行为。
    /// </summary>
    private sealed class ThrowingModelTransport : IAgentModelTransport
    {
        private readonly Exception _exception;

        public ThrowingModelTransport(Exception exception)
        {
            _exception = exception;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw _exception;

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => throw _exception;
    }
}
