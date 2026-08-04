using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// 不可变 Attempt 验收测试（P0-9）
//
// 覆盖：
// 1. 契约：RunRetryScheduled=14 / AttemptStarted=15 / AttemptFailed=16 枚举值固定
//    （在 ToolReconciliationResolved=13 之后续接，不重排既有事件类型）；
// 2. Attempt 边界查询：GetAttemptBoundarySequenceAsync 空流返回 -1、无重试标记返回 -1、
//    有 RunRetryScheduled 返回其 Sequence（当前 Attempt 的续写锚点）；
// 3. 重试全新启动：RetryCount>0 的 Run 在既有事件链上续写
//    RunRetryScheduled → AttemptStarted → RunCreated，前序 Attempt 事件不删除、
//    哈希链完整、Run 正常完成；
// 4. 重试 Attempt 失败：RunFailed 之后追加 AttemptFailed（Attempt 终结边界），
//    前序 Attempt 历史保留；
// 5. 恢复重放边界：重试后崩溃恢复只重放当前 Attempt（Sequence > 最后一个
//    RunRetryScheduled），前序 Attempt 的模型上下文/工具观察不污染新 Attempt。
//
// 设计原则：
// - 优先使用真实 InMemory 实现（非 mock）：InMemoryAgentRunStore /
//   InMemoryAgentRunEventStore / EchoToolDispatcher / DeterministicAgentModelTransport；
// - 事件流种子使用 AgentRunEventChain.BuildEvent 构造合法哈希链；
// - 所有异步测试使用超时 CancellationTokenSource 防止挂起；
// - 中文注释。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
[TestCategory("Agent-Run-Immutable-Attempt")]
public sealed class R30Z_ImmutableAttemptTests
{
    private const string WorkspaceId = "ws-r30z-attempt";
    private const string SessionId = "session-r30z-attempt";

    // ---------------------------------------------------------------------------
    // 1. 契约：新事件类型枚举值固定
    // ---------------------------------------------------------------------------

    /// <summary>
    /// 验证：RunRetryScheduled / AttemptStarted / AttemptFailed 的枚举值固定在
    /// ToolReconciliationResolved=13 之后（14/15/16），不重排既有事件类型（审计兼容）。
    /// </summary>
    [TestMethod]
    public void EventTypeValues_AttemptMarkers_HaveFixedOrdinals()
    {
        Assert.AreEqual(13, (byte)AgentRunEventType.ToolReconciliationResolved,
            "ToolReconciliationResolved 应保持 13（前序契约锚点）。");
        Assert.AreEqual(14, (byte)AgentRunEventType.RunRetryScheduled,
            "RunRetryScheduled 应为 14（Attempt 边界锚点）。");
        Assert.AreEqual(15, (byte)AgentRunEventType.AttemptStarted,
            "AttemptStarted 应为 15（Attempt 开始标记）。");
        Assert.AreEqual(16, (byte)AgentRunEventType.AttemptFailed,
            "AttemptFailed 应为 16（Attempt 失败终结标记）。");
    }

    // ---------------------------------------------------------------------------
    // 2. Attempt 边界查询（GetAttemptBoundarySequenceAsync）
    // ---------------------------------------------------------------------------

    /// <summary>
    /// 验证：事件流为空时边界查询返回 -1（从未重试过）。
    /// </summary>
    [TestMethod]
    public async Task AttemptBoundary_EmptyStream_ReturnsMinusOne()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("边界空流验证");
        await runStore.CreateAsync(run);

        var boundary = await eventStore.GetAttemptBoundarySequenceAsync(WorkspaceId, run.RunId);

        Assert.AreEqual(-1, boundary, "空事件流（从未重试）应返回 -1。");
    }

    /// <summary>
    /// 验证：事件流存在但从未重试（无 RunRetryScheduled）时边界查询返回 -1。
    /// </summary>
    [TestMethod]
    public async Task AttemptBoundary_NoRetryMarkers_ReturnsMinusOne()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("无重试标记验证");
        await runStore.CreateAsync(run);

        await SeedAttemptOneAsync(eventStore, run);

        var boundary = await eventStore.GetAttemptBoundarySequenceAsync(WorkspaceId, run.RunId);

        Assert.AreEqual(-1, boundary, "无 RunRetryScheduled 事件应返回 -1（首次执行无边界）。");
    }

    /// <summary>
    /// 验证：最后一个 RunRetryScheduled 事件的 Sequence 即当前 Attempt 边界
    /// （前序 Attempt 的最后一个 RunRetryScheduled 被覆盖——多 Attempt 取最新）。
    /// </summary>
    [TestMethod]
    public async Task AttemptBoundary_AfterRunRetryScheduled_ReturnsMarkerSequence()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("边界标记验证");
        await runStore.CreateAsync(run);

        await SeedAttemptOneAsync(eventStore, run);
        var boundary = await AppendRetryMarkersAsync(eventStore, run, retryCount: 1);

        var actual = await eventStore.GetAttemptBoundarySequenceAsync(WorkspaceId, run.RunId);

        Assert.AreEqual(boundary, actual,
            "最后一个 RunRetryScheduled 的 Sequence 应为当前 Attempt 边界。");
    }

    // ---------------------------------------------------------------------------
    // 3. 重试全新启动：续写标记 + 保留前序 Attempt 历史
    // ---------------------------------------------------------------------------

    /// <summary>
    /// 验证：RetryCount&gt;0 的 Run 以全新启动执行时，在既有事件链上续写
    /// RunRetryScheduled → AttemptStarted → RunCreated（Sequence 紧随前序尾部），
    /// 前序 Attempt 事件全部保留（不可变审计）、哈希链完整、Run 正常完成。
    /// </summary>
    [TestMethod]
    public async Task RetryAttempt_FreshStart_AppendsMarkersOnExistingChain_RetainsHistory()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("重试全新启动验证") with { State = AgentRunState.Claimed, RetryCount = 1, MaxRetries = 2 };
        await runStore.CreateAsync(run);

        // 种子：Attempt 1 事件流（RunCreated / StateTransition / RunFailed），模拟
        // Postgres Claim 事务后的状态——事件历史保留，Run 元数据已置 Claimed + RetryCount=1。
        await SeedAttemptOneAsync(eventStore, run);

        var actor = BuildActor(runStore, eventStore, new DeterministicAgentModelTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言 1：前序 Attempt 事件保留（不删除）
        var events = await eventStore.ReadAsync(WorkspaceId, run.RunId);
        Assert.IsTrue(events.Count >= 9, $"事件流应包含前序 Attempt + 新 Attempt 标记 + 执行事件（实际 {events.Count}）。");
        Assert.AreEqual(AgentRunEventType.RunCreated, events[0].EventType, "前序 Attempt 的 RunCreated 必须保留。");
        Assert.AreEqual(AgentRunEventType.RunFailed, events[2].EventType, "前序 Attempt 的 RunFailed 必须保留。");

        // 断言 2：续写标记顺序 = RunRetryScheduled → AttemptStarted → RunCreated（Sequence 3/4/5）
        Assert.AreEqual(AgentRunEventType.RunRetryScheduled, events[3].EventType,
            "新 Attempt 首事件应为 RunRetryScheduled（Attempt 边界锚点）。");
        Assert.AreEqual(AgentRunEventType.AttemptStarted, events[4].EventType,
            "RunRetryScheduled 之后应为 AttemptStarted。");
        Assert.AreEqual(AgentRunEventType.RunCreated, events[5].EventType,
            "AttemptStarted 之后应续 RunCreated（新 Attempt 全新启动）。");

        // 断言 3：标记负载含 attempt=2（RetryCount=1 的下一 Attempt）
        var retryPayload = JsonDocument.Parse(events[3].Payload);
        Assert.AreEqual(2, retryPayload.RootElement.GetProperty("attempt").GetInt32(),
            "RunRetryScheduled 负载 attempt 应为 2。");
        Assert.AreEqual(1, retryPayload.RootElement.GetProperty("retryCount").GetInt32(),
            "RunRetryScheduled 负载 retryCount 应为 1。");

        // 断言 4：哈希链完整（Sequence 0..N 连续 + PrevChainHash 链接 + ContentHash 一致）
        Assert.IsTrue(AgentRunEventChain.VerifyChain(events),
            "跨 Attempt 的事件哈希链必须完整无断裂。");
        Assert.AreEqual(2, events.Count(e => e.EventType == AgentRunEventType.RunCreated),
            "每个 Attempt 各有一个 RunCreated（Attempt 1 + Attempt 2）。");

        // 断言 5：Run 正常完成（终态）
        var finalRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun, "Run 应存在于 store 中。");
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            "重试 Attempt 正常执行后 Run 应进入 Completed 终态。");
    }

    // ---------------------------------------------------------------------------
    // 4. 重试 Attempt 失败：RunFailed + AttemptFailed 终结标记
    // ---------------------------------------------------------------------------

    /// <summary>
    /// 验证：重试 Attempt 执行失败时，RunFailed 之后追加 AttemptFailed（Attempt 终结边界），
    /// 前序 Attempt 历史保留、哈希链完整、Run 进入 Failed 终态。
    /// </summary>
    [TestMethod]
    public async Task RetryAttempt_AttemptFails_AppendsRunFailedAndAttemptFailed()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("重试失败验证") with { State = AgentRunState.Claimed, RetryCount = 1, MaxRetries = 2 };
        await runStore.CreateAsync(run);

        await SeedAttemptOneAsync(eventStore, run);

        // 模型调用抛异常 → Attempt 2 失败
        var actor = BuildActor(runStore, eventStore, new ThrowingModelTransport("模拟模型调用失败"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var events = await eventStore.ReadAsync(WorkspaceId, run.RunId);

        // 断言 1：尾部两个事件为 RunFailed + AttemptFailed（Attempt 终结边界）
        Assert.IsTrue(events.Count >= 12, $"事件流应包含标记 + 失败事件（实际 {events.Count}）。");
        Assert.AreEqual(AgentRunEventType.RunFailed, events[^2].EventType,
            "RunFailed 应在 AttemptFailed 之前。");
        Assert.AreEqual(AgentRunEventType.AttemptFailed, events[^1].EventType,
            "重试 Attempt 失败后必须追加 AttemptFailed（Attempt 终结边界）。");

        // 断言 2：AttemptFailed 负载含 attempt=2
        var attemptFailedPayload = JsonDocument.Parse(events[^1].Payload);
        Assert.AreEqual(2, attemptFailedPayload.RootElement.GetProperty("attempt").GetInt32(),
            "AttemptFailed 负载 attempt 应为 2。");

        // 断言 3：前序 Attempt 历史保留 + 哈希链完整
        Assert.AreEqual(AgentRunEventType.RunFailed, events[2].EventType,
            "Attempt 1 的 RunFailed 必须保留（不可变审计）。");
        Assert.IsTrue(AgentRunEventChain.VerifyChain(events),
            "含 AttemptFailed 的事件哈希链必须完整无断裂。");

        // 断言 4：Run 进入 Failed 终态
        var finalRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun, "Run 应存在于 store 中。");
        Assert.AreEqual(AgentRunState.Failed, finalRun!.State,
            "重试 Attempt 失败后 Run 应进入 Failed 终态。");
        StringAssert.Contains(finalRun.FailureReason ?? string.Empty, "模拟模型调用失败",
            "FailureReason 应记录模型调用异常。");
    }

    // ---------------------------------------------------------------------------
    // 5. 恢复重放边界：只重放当前 Attempt
    // ---------------------------------------------------------------------------

    /// <summary>
    /// 验证：重试后崩溃恢复以最后一个 RunRetryScheduled 为边界只重放当前 Attempt——
    /// 前序 Attempt 的模型上下文（ModelCallCompleted 内容）不污染新 Attempt；
    /// 模型看到的是当前 Attempt 的对话，且哈希链跨 Attempt 连续。
    /// </summary>
    [TestMethod]
    public async Task Recovery_AfterRetry_ReplaysOnlyCurrentAttempt()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("恢复边界验证任务") with { State = AgentRunState.ContextBuilding, RetryCount = 1, MaxRetries = 2 };
        await runStore.CreateAsync(run);

        // 种子完整链：
        // Attempt 1（seq 0-5）：RunCreated / StateTransition / ModelCallStarted /
        //   ModelCallCompleted("attempt-1-content") / StateTransition / RunFailed
        // Attempt 边界（seq 6-7）：RunRetryScheduled / AttemptStarted
        // Attempt 2（seq 8-11）：RunCreated / StateTransition / ModelCallStarted /
        //   ModelCallCompleted("attempt-2-content")——Run 在模型轮次后崩溃（State=ContextBuilding）。
        await SeedRecoveryChainAsync(eventStore, run);

        // 录制模型入参：断言恢复后模型只看到 Attempt 2 的对话
        var recording = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "恢复后完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 10,
            Duration = TimeSpan.FromMilliseconds(1)
        });
        var actor = BuildActor(runStore, eventStore, recording);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言 1：恢复后模型被调用，且入参包含 Attempt 2 的对话、不含 Attempt 1 的上下文
        Assert.AreEqual(1, recording.CapturedCalls.Count, "恢复后应调用 1 次模型（最终答案后完成）。");
        var projected = string.Join("\n", recording.CapturedCalls[0].Messages.Select(m => m.Content));
        StringAssert.Contains(projected, "attempt-2-content",
            "模型应看到当前 Attempt（Attempt 2）的对话内容。");
        Assert.IsFalse(projected.Contains("attempt-1-content", StringComparison.Ordinal),
            "前序 Attempt（Attempt 1）的模型上下文不得污染当前 Attempt。");

        // 断言 2：前序 Attempt 事件全部保留（不可变审计）+ 哈希链完整。
        // 种子 12 事件（Attempt 1 的 seq 0-5 + 重试标记 seq 6-7 + Attempt 2 的 seq 8-11）不被删除；
        // 恢复后 Actor 在既有链上续写 5 个执行事件（StateTransition / ModelCallStarted /
        // ModelCallCompleted / StateTransition / RunCompleted），总计 17。
        var events = await eventStore.ReadAsync(WorkspaceId, run.RunId);
        Assert.AreEqual(17, events.Count,
            "恢复执行不得删除任何历史事件（种子 12 + 恢复续写 5 = 17）。");
        Assert.AreEqual(AgentRunEventType.RunCreated, events[0].EventType,
            "Attempt 1 的 RunCreated 必须保留。");
        Assert.AreEqual(AgentRunEventType.RunFailed, events[5].EventType,
            "Attempt 1 的 RunFailed 必须保留。");
        Assert.AreEqual(AgentRunEventType.RunRetryScheduled, events[6].EventType,
            "重试边界标记必须保留。");
        Assert.AreEqual(AgentRunEventType.AttemptStarted, events[7].EventType,
            "AttemptStarted 边界标记必须保留。");
        Assert.IsTrue(events[3].EventType == AgentRunEventType.ModelCallCompleted
                      && events[3].Payload.Contains("attempt-1-content", StringComparison.Ordinal),
            "Attempt 1 的 ModelCallCompleted 历史必须保留（不可变审计）。");
        Assert.IsTrue(events[11].EventType == AgentRunEventType.ModelCallCompleted
                      && events[11].Payload.Contains("attempt-2-content", StringComparison.Ordinal),
            "Attempt 2 的 ModelCallCompleted 必须保留。");
        Assert.AreEqual(AgentRunEventType.RunCompleted, events[^1].EventType,
            "恢复后执行应追加 RunCompleted 终态事件。");
        Assert.IsTrue(AgentRunEventChain.VerifyChain(events),
            "恢复续写后的事件哈希链必须完整无断裂。");

        // 断言 3：Run 正常完成
        var finalRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun, "Run 应存在于 store 中。");
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            "恢复执行后 Run 应进入 Completed 终态。");
    }

    /// <summary>
    /// 验证：首次执行（无 RunRetryScheduled 标记）的恢复仍从 Sequence 0 全量重放——
    /// 边界钳制对从未重试的 Run 无影响（向后兼容，无回归）。
    /// </summary>
    [TestMethod]
    public async Task Recovery_NoAttemptBoundary_StillFullReplaysFromZero()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("无边界恢复验证任务") with { State = AgentRunState.ContextBuilding, RetryCount = 0 };
        await runStore.CreateAsync(run);

        // 无重试标记的普通链：RunCreated / StateTransition / ModelCallStarted /
        // ModelCallCompleted("first-attempt-content")——Run 在模型轮次后崩溃。
        var seq0 = AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 0,
            AgentRunEventType.RunCreated, AgentRunState.Created, """{"runId":"seed"}""", null);
        var seq1 = AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 1,
            AgentRunEventType.StateTransition, AgentRunState.ContextBuilding,
            """{"from":"Created","to":"ContextBuilding"}""", seq0.ContentHash);
        var seq2 = AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 2,
            AgentRunEventType.ModelCallStarted, AgentRunState.ModelCalling,
            """{"turn":1}""", seq1.ContentHash);
        var seq3 = AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 3,
            AgentRunEventType.ModelCallCompleted, AgentRunState.ModelCalling,
            """{"content":"first-attempt-content","toolCallCount":0,"isFinalAnswer":false}""", seq2.ContentHash);
        await eventStore.AppendAsync(seq0);
        await eventStore.AppendAsync(seq1);
        await eventStore.AppendAsync(seq2);
        await eventStore.AppendAsync(seq3);

        var recording = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "首次执行恢复完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 10,
            Duration = TimeSpan.FromMilliseconds(1)
        });
        var actor = BuildActor(runStore, eventStore, recording);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言：无边界时模型仍看到首个（也是唯一）Attempt 的对话（全量重放，向后兼容）
        Assert.AreEqual(1, recording.CapturedCalls.Count, "恢复后应调用 1 次模型。");
        var projected = string.Join("\n", recording.CapturedCalls[0].Messages.Select(m => m.Content));
        StringAssert.Contains(projected, "first-attempt-content",
            "无重试标记时恢复应从 Sequence 0 全量重放（向后兼容）。");
        var finalRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State, "恢复执行后 Run 应完成。");
    }

    // ---------------------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------------------

    private static AgentRunActor BuildActor(
        IAgentRunStore runStore, IAgentRunEventStore eventStore, IAgentModelTransport transport)
        => new(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher());

    /// <summary>
    /// 种子 Attempt 1 事件流（RunCreated / StateTransition / RunFailed，Sequence 0-2）。
    /// 模拟 Postgres Claim 事务后的状态：事件历史保留，Run 元数据已置 Claimed + RetryCount。
    /// </summary>
    private static async Task SeedAttemptOneAsync(InMemoryAgentRunEventStore eventStore, AgentRun run)
    {
        var seq0 = AgentRunEventChain.BuildEvent(
            run.RunId, WorkspaceId, 0,
            AgentRunEventType.RunCreated, AgentRunState.Created,
            """{"runId":"seed"}""", null);
        var seq1 = AgentRunEventChain.BuildEvent(
            run.RunId, WorkspaceId, 1,
            AgentRunEventType.StateTransition, AgentRunState.ContextBuilding,
            """{"from":"Created","to":"ContextBuilding"}""", seq0.ContentHash);
        var seq2 = AgentRunEventChain.BuildEvent(
            run.RunId, WorkspaceId, 2,
            AgentRunEventType.RunFailed, AgentRunState.Failed,
            """{"reason":"seed-failure"}""", seq1.ContentHash);
        await eventStore.AppendAsync(seq0);
        await eventStore.AppendAsync(seq1);
        await eventStore.AppendAsync(seq2);
    }

    /// <summary>
    /// 追加重试标记（RunRetryScheduled + AttemptStarted），返回 RunRetryScheduled 的 Sequence。
    /// </summary>
    private static async Task<int> AppendRetryMarkersAsync(
        InMemoryAgentRunEventStore eventStore, AgentRun run, int retryCount)
    {
        var tail = (await eventStore.ReadAsync(WorkspaceId, run.RunId)).Last();
        var boundary = AgentRunEventChain.BuildEvent(
            run.RunId, WorkspaceId, tail.Sequence + 1,
            AgentRunEventType.RunRetryScheduled, AgentRunState.Claimed,
            JsonSerializer.Serialize(new { attempt = retryCount + 1, retryCount, scheduledAt = DateTimeOffset.UtcNow }),
            tail.ContentHash);
        var started = AgentRunEventChain.BuildEvent(
            run.RunId, WorkspaceId, boundary.Sequence + 1,
            AgentRunEventType.AttemptStarted, AgentRunState.Claimed,
            JsonSerializer.Serialize(new { attempt = retryCount + 1, retryCount }),
            boundary.ContentHash);
        await eventStore.AppendAsync(boundary);
        await eventStore.AppendAsync(started);
        return boundary.Sequence;
    }

    /// <summary>
    /// 种子完整跨 Attempt 链：Attempt 1（含 ModelCallCompleted "attempt-1-content"）+
    /// 重试标记（seq 6-7）+ Attempt 2 部分执行（含 ModelCallCompleted "attempt-2-content"，seq 8-11）。
    /// </summary>
    private static async Task SeedRecoveryChainAsync(InMemoryAgentRunEventStore eventStore, AgentRun run)
    {
        var chain = new List<AgentRunEvent>();

        // Attempt 1（seq 0-5）
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 0,
            AgentRunEventType.RunCreated, AgentRunState.Created, """{"runId":"seed"}""", null));
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 1,
            AgentRunEventType.StateTransition, AgentRunState.ContextBuilding,
            """{"from":"Created","to":"ContextBuilding"}""", chain[^1].ContentHash));
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 2,
            AgentRunEventType.ModelCallStarted, AgentRunState.ModelCalling,
            """{"turn":1}""", chain[^1].ContentHash));
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 3,
            AgentRunEventType.ModelCallCompleted, AgentRunState.ModelCalling,
            """{"content":"attempt-1-content","toolCallCount":0,"isFinalAnswer":false}""", chain[^1].ContentHash));
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 4,
            AgentRunEventType.StateTransition, AgentRunState.ContextBuilding,
            """{"from":"ModelCalling","to":"ContextBuilding"}""", chain[^1].ContentHash));
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 5,
            AgentRunEventType.RunFailed, AgentRunState.Failed,
            """{"reason":"attempt-1-failure"}""", chain[^1].ContentHash));

        // Attempt 边界（seq 6-7）
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 6,
            AgentRunEventType.RunRetryScheduled, AgentRunState.Claimed,
            JsonSerializer.Serialize(new { attempt = 2, retryCount = 1, scheduledAt = DateTimeOffset.UtcNow }),
            chain[^1].ContentHash));
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 7,
            AgentRunEventType.AttemptStarted, AgentRunState.Claimed,
            JsonSerializer.Serialize(new { attempt = 2, retryCount = 1 }),
            chain[^1].ContentHash));

        // Attempt 2 部分执行（seq 8-11）：Run 在模型轮次后崩溃（State=ContextBuilding）
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 8,
            AgentRunEventType.RunCreated, AgentRunState.Claimed, """{"runId":"seed-attempt-2"}""", chain[^1].ContentHash));
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 9,
            AgentRunEventType.StateTransition, AgentRunState.ContextBuilding,
            """{"from":"Claimed","to":"ContextBuilding"}""", chain[^1].ContentHash));
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 10,
            AgentRunEventType.ModelCallStarted, AgentRunState.ModelCalling,
            """{"turn":1}""", chain[^1].ContentHash));
        chain.Add(AgentRunEventChain.BuildEvent(run.RunId, WorkspaceId, 11,
            AgentRunEventType.ModelCallCompleted, AgentRunState.ModelCalling,
            """{"content":"attempt-2-content","toolCallCount":0,"isFinalAnswer":false}""", chain[^1].ContentHash));

        foreach (var evt in chain)
        {
            await eventStore.AppendAsync(evt);
        }
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

    /// <summary>
    /// 录制模型调用入参的 IAgentModelTransport stub：每次调用返回构造时指定的固定响应，
    /// 并捕获 (RunId, Messages) 供断言恢复后模型看到的对话内容。
    /// </summary>
    private sealed class RecordingModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse _response;
        public List<(string RunId, IReadOnlyList<AgentMessage> Messages)> CapturedCalls { get; } = new();

        public RecordingModelTransport(AgentModelResponse response)
        {
            _response = response;
        }

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            CapturedCalls.Add((runId, messages.ToList()));
            return ValueTask.FromResult(_response);
        }

        public ValueTask<AgentModelResponse> CallAsync(
            AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);
    }

    /// <summary>模型调用抛异常的 IAgentModelTransport stub（模拟模型/上游故障）。</summary>
    private sealed class ThrowingModelTransport : IAgentModelTransport
    {
        private readonly string _message;

        public ThrowingModelTransport(string message) => _message = message;

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, string context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(_message);

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(_message);

        public ValueTask<AgentModelResponse> CallAsync(
            AgentModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(_message);
    }
}
