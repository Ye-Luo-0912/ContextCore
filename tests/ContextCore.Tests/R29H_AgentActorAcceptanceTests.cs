using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// Agent Actor 生产化验收测试（6 项）
//
// 验证任务C 修复后的 AgentRunActor 生产化行为：
// 1. FirstModelCall_ContainsTaskAndBuiltContext — 第一次模型调用包含 Task + 上下文
// 2. EveryModelCall_ConsumesTurnBudget — 每次模型调用消耗 Turn 预算
// 3. ModelOnlyLoop_CannotRunIndefinitely — 无 Tool 的模型循环不能无限运行
// 4. CheckpointSaved_Event_Requires_PersistedCheckpoint — CheckpointSaved 事件需先持久化
// 5. AgentActor_Uses_DurableToolExecutor — Actor 使用 IDurableToolExecutor
// 6. ToolEvent_Preserves_RequestId_IdempotencyAndSideEffect — Tool 事件保留身份信息
//
// 设计原则：
// - 优先使用真实 InMemory 实现（InMemoryAgentRunStore / InMemoryAgentRunEventStore /
// InMemoryAgentCheckpointStore / InMemoryToolDispatchJournal / EchoToolDispatcher）
// - 自定义 RecordingModelTransport 捕获模型调用入参以断言上下文内容
// - 所有异步测试使用超时 CancellationTokenSource 防止挂起
// - 中文注释
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Agent-Actor")]
public sealed class R29H_AgentActorAcceptanceTests
{
    /// <summary>
    /// 验证：Agent Actor 的第一次模型调用包含 Task 信息和构建好的 Context
    /// （而非空 context）。
    /// </summary>
    /// <remarks>
    /// 场景：启动一个带 task description 的 Agent Run，
    /// 第一次调用模型时应传入包含 User(run.Task) 角色的结构化消息列表。
    /// </remarks>
    [TestMethod]
    public async Task FirstModelCall_ContainsTaskAndBuiltContext()
    {
        // 准备：InMemory 依赖 + RecordingModelTransport 捕获调用入参
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var taskDescription = "请帮我搜索关于 R29 的文档";
        var run = BuildRun(taskDescription);
        await runStore.CreateAsync(run);

        // RecordingModelTransport 直接产出最终答案，结束循环（避免无限运行）
        var transport = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "已处理任务",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 10,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言：第一次模型调用被捕获，且包含 User(Task) 角色
        Assert.AreEqual(1, transport.CapturedCalls.Count,
            "应有 1 次模型调用。");
        var firstCall = transport.CapturedCalls[0];
        CollectionAssert.Contains(
            firstCall.Messages.Select(m => m.Role).ToList(),
            AgentMessageRole.User,
            "第一次模型调用应包含 User 角色消息（即 Task）。");

        var userMessage = firstCall.Messages.First(m => m.Role == AgentMessageRole.User);
        Assert.AreEqual(taskDescription, userMessage.Content,
            "User 消息内容应等于 Task 描述（构建好的上下文）。");
        Assert.IsTrue(firstCall.Messages.Count >= 1,
            "第一次模型调用应至少包含 1 条消息（非空 context）。");
    }

    /// <summary>
    /// 验证：每次模型调用都消耗 Turn 预算（ModelCallsUsed 递增），
    /// 超出 MaxModelCalls 上限时停止调用。
    /// </summary>
    /// <remarks>
    /// 场景：模型总是返回非最终答案且无 Tool 调用，迫使 LoopPolicy 一直选择 CallModel，
    /// 直至 MaxModelCalls 触发 Fail。验证 ModelCallCompleted 事件中 modelCallsUsed 单调递增。
    /// </remarks>
    [TestMethod]
    public async Task EveryModelCall_ConsumesTurnBudget_ModelCallsUsedIncrements()
    {
        // 准备：MaxModelCalls=3，模型总是返回非最终答案且无 Tool 调用
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("测试模型预算消耗", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 100,       // 故意设置很高，不靠 Turn 限制终止
            TurnsUsed = 0,
            MaxModelCalls = 3    // 但模型调用上限很低
        });
        await runStore.CreateAsync(run);

        // 模型总是返回非最终答案且无 Tool 调用 → LoopPolicy 选择 CallModel 直到 MaxModelCalls 触发
        var transport = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "继续思考",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = false,
            TokensConsumed = 5,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言 1：模型调用次数 = 3（MaxModelCalls 触发后停止）
        Assert.AreEqual(3, transport.CapturedCalls.Count,
            "应只调用 3 次模型（MaxModelCalls=3 触发终止）。");

        // 断言 2：事件流中 ModelCallCompleted 事件的 modelCallsUsed 字段单调递增 (1, 2, 3)
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        var modelCallCompletedEvents = events
            .Where(e => e.EventType == AgentRunEventType.ModelCallCompleted)
            .ToList();
        Assert.AreEqual(3, modelCallCompletedEvents.Count,
            "应有 3 个 ModelCallCompleted 事件。");

        var modelCallsUsedValues = modelCallCompletedEvents
            .Select(e => ExtractIntField(e.Payload, "modelCallsUsed"))
            .ToList();
        Assert.AreEqual(1, modelCallsUsedValues[0], "第 1 次调用后 ModelCallsUsed 应为 1。");
        Assert.AreEqual(2, modelCallsUsedValues[1], "第 2 次调用后 ModelCallsUsed 应为 2。");
        Assert.AreEqual(3, modelCallsUsedValues[2], "第 3 次调用后 ModelCallsUsed 应为 3。");

        // 断言 3：超出 MaxModelCalls 后最终状态为 Failed（LoopPolicy 返回 Fail）
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Failed, finalRun!.State,
            "超出 MaxModelCalls 应触发 Fail（循环终止）。");
    }

    /// <summary>
    /// 验证：只有模型调用（无 Tool 调用）的循环不能无限运行，
    /// 在 MaxModelCalls 限制下终止。
    /// </summary>
    /// <remarks>
    /// 场景：模型总是返回"继续"响应且无 Tool 调用，Actor 不提供任何 Tool 触发关键词，
    /// 验证循环在预算限制下终止（不无限运行）。
    /// </remarks>
    [TestMethod]
    public async Task ModelOnlyLoop_CannotRunIndefinitely_TerminatesByBudget()
    {
        // 准备：MaxTurns 很高，MaxModelCalls=5（强制限制）
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("无 tool 循环测试", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 1000,     // 故意设置极高，不靠 Turn 限制
            TurnsUsed = 0,
            MaxModelCalls = 5    // 但模型调用上限很低
        });
        await runStore.CreateAsync(run);

        // 模型总是返回"继续"且无 Tool 调用
        var transport = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "我还在思考",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = false,
            TokensConsumed = 1,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言 1：循环在 MaxModelCalls 限制下终止（不会一直运行）
        Assert.IsTrue(transport.CapturedCalls.Count <= 5,
            $"模型调用次数（{transport.CapturedCalls.Count}）不应超过 MaxModelCalls（5）。");
        Assert.AreEqual(5, transport.CapturedCalls.Count,
            "应在 5 次模型调用后终止（MaxModelCalls 触发）。");

        // 断言 2：事件流中无 ToolCallCompleted 事件（确实是无 Tool 调用的循环）
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        var toolCallCompletedEvents = events
            .Where(e => e.EventType == AgentRunEventType.ToolCallCompleted)
            .ToList();
        Assert.AreEqual(0, toolCallCompletedEvents.Count,
            "无 Tool 调用的循环不应产出 ToolCallCompleted 事件。");

        // 断言 3：最终状态为 Failed（预算耗尽触发 Fail）
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Failed, finalRun!.State,
            "MaxModelCalls 触发后应进入 Failed 终态。");
    }

    /// <summary>
    /// 验证：CheckpointSaved 事件只在 Checkpoint 真正持久化后才发出。
    /// </summary>
    /// <remarks>
    /// 场景 1：注入正常 InMemoryAgentCheckpointStore，执行到 checkpoint 点后
    /// - event store 中有 CheckpointSaved 事件
    /// - checkpoint store 中确实有对应的 checkpoint 数据
    /// 场景 2：注入会失败的 checkpoint store，验证失败时不发出 CheckpointSaved 事件
    /// （Actor.ExecuteCheckpointAsync 中 SaveAsync 抛异常 → 异常向上传播，
    /// 被 ExecuteAsync 的 catch 块捕获转 Failed，CheckpointSaved 事件不会写入）
    /// </remarks>
    [TestMethod]
    public async Task CheckpointSaved_Event_Requires_PersistedCheckpoint()
    {
        // ─── 场景 1：正常路径 — 持久化成功后发出 CheckpointSaved 事件 ───
        var runStore1 = new InMemoryAgentRunStore();
        var checkpointStore1 = new InMemoryAgentCheckpointStore();
        // 3c：checkpoint store 必须同时注入 EventStore（AppendBatchAsync 在同事务/同批内委托 SaveAsync）
        var eventStore1 = new InMemoryAgentRunEventStore(runStore1, checkpointStore1);
        var checkpointFactory1 = new StubCheckpointFactory();

        // task 含 "search" 关键词，触发 DeterministicAgentModelTransport 产出 Tool 调用 → 触发 checkpoint
        var run1 = BuildRun("search 验证 checkpoint 持久化");
        await runStore1.CreateAsync(run1);

        var actor1 = new AgentRunActor(
            runStore1, eventStore1, new DeterministicAgentModelTransport(),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            checkpointFactory: checkpointFactory1,
            checkpointStore: checkpointStore1);

        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor1.ExecuteAsync(run1, cts1.Token);

        // 断言 1：event store 中有 CheckpointSaved 事件
        var events1 = await eventStore1.ReadAsync(run1.WorkspaceId, run1.RunId);
        var checkpointSavedEvents1 = events1
            .Where(e => e.EventType == AgentRunEventType.CheckpointSaved)
            .ToList();
        Assert.IsTrue(checkpointSavedEvents1.Count > 0,
            "正常路径下应有 CheckpointSaved 事件。");

        // 断言 2：checkpoint store 中确实有对应的 checkpoint 数据
        Assert.IsTrue(checkpointStore1.Count > 0,
            "checkpoint store 中应有持久化的 checkpoint（先持久化再发事件）。");

        // 断言 3：CheckpointSaved 事件 payload 中 persisted=true（store 已注入）
        var firstCheckpointPayload = JsonDocument.Parse(checkpointSavedEvents1[0].Payload);
        Assert.IsTrue(firstCheckpointPayload.RootElement.TryGetProperty("persisted", out var persistedEl),
            "CheckpointSaved 事件 payload 应含 persisted 字段。");
        Assert.IsTrue(persistedEl.GetBoolean(),
            "注入了 checkpoint store 时 persisted 应为 true。");

        // ─── 场景 2：失败路径 — SaveAsync 抛异常，Run 应进入 Failed 终态 ───
        var runStore2 = new InMemoryAgentRunStore();
        var failingStore = new FailingCheckpointStore();
        // 3c：failingStore 注入 EventStore，使 AppendBatchAsync 在追加事件前尝试 SaveAsync 并失败
        var eventStore2 = new InMemoryAgentRunEventStore(runStore2, failingStore);
        var checkpointFactory2 = new StubCheckpointFactory();

        var run2 = BuildRun("search 验证 checkpoint 失败不发出事件");
        await runStore2.CreateAsync(run2);

        var actor2 = new AgentRunActor(
            runStore2, eventStore2, new DeterministicAgentModelTransport(),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            checkpointFactory: checkpointFactory2,
            checkpointStore: failingStore);

        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor2.ExecuteAsync(run2, cts2.Token);

        // 断言 4：失败路径下最终状态为 Failed
        // 3c：Optimization 3 后语义变化——CheckpointSaved 事件在 PersistCheckpointAsync 中已被缓冲，
        // flush 时 AppendBatchAsync 先尝试 SaveAsync（失败）→ 抛异常 → 不追加事件 → ExecuteAsync catch → FailAsync。
        // FailAsync 重试 flush 时 _pendingTurnCheckpoint 已被清除（不再尝试 SaveAsync），
        // CheckpointSaved 事件已被同步移除，不会随 RunFailed 事件持久化（避免孤立事件）。
        // 关键保证：checkpoint 本体未被持久化，CheckpointSaved 事件也不被持久化，Run 进入 Failed 终态。
        var finalRun2 = await runStore2.GetAsync(run2.WorkspaceId, run2.RunId);
        Assert.IsNotNull(finalRun2);
        Assert.AreEqual(AgentRunState.Failed, finalRun2!.State,
            "checkpoint 持久化失败应导致 Run 进入 Failed 终态。");

        // 断言：失败路径下不应持久化 CheckpointSaved 事件（checkpoint 本体未保存）
        var events2 = await eventStore2.ReadAsync(run2.WorkspaceId, run2.RunId);
        var checkpointSavedEvents2 = events2
            .Where(e => e.EventType == AgentRunEventType.CheckpointSaved)
            .ToList();
        Assert.AreEqual(0, checkpointSavedEvents2.Count,
            "P0-11：checkpoint 本体保存失败时，CheckpointSaved 事件也不应被持久化（避免孤立事件）。");
    }

    /// <summary>
    /// 验证：Agent Actor 使用 IDurableToolExecutor（而非直接调用 tool dispatcher）。
    /// </summary>
    /// <remarks>
    /// 通过行为验证：注入 DefaultDurableToolExecutor + InMemoryToolDispatchJournal，
    /// 执行一个 tool call 后验证 journal 中有完整的状态推进（最终到 Committed）。
    /// 说明 tool 调用经过了 Journal 的 Prepare/Dispatch/Commit 流程，而非直接调 dispatcher。
    /// </remarks>
    [TestMethod]
    public async Task AgentActor_Uses_DurableToolExecutor_JournalAdvancesThroughPrepareDispatchCommit()
    {
        // 准备：注入 DurableToolExecutor + Journal
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var journal = new InMemoryToolDispatchJournal();
        var durableExecutor = new DefaultDurableToolExecutor(new EchoToolDispatcher(), journal);

        // task 含 "search" 关键词触发 Tool 调用
        var run = BuildRun("search 验证 DurableToolExecutor 流程");
        await runStore.CreateAsync(run);

        // 自定义 tool triggers：将 "search" 关键词映射到 "echo" tool 名。
        // EchoToolDispatcher 仅支持 "echo"（见 SupportedTools），
        // DefaultDurableToolExecutor 会在 PrepareAsync 之前校验 SupportedTools，
        // 若 tool 名不匹配则直接返回失败（不写 journal、SideEffect=Unknown），
        // 无法验证 Prepare→Dispatch→Commit 完整流程。
        var echoTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["search"] = "echo"
        };

        var actor = new AgentRunActor(
            runStore, eventStore, new DeterministicAgentModelTransport(echoTriggers),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),  // 构造函数必需（fallback 路径，不会使用）
            durableToolExecutor: durableExecutor);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言：事件流中有 ToolCallCompleted 事件
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        var toolCallCompletedEvents = events
            .Where(e => e.EventType == AgentRunEventType.ToolCallCompleted)
            .ToList();
        Assert.IsTrue(toolCallCompletedEvents.Count > 0,
            "应有 ToolCallCompleted 事件（DeterministicAgentModelTransport 触发了 search tool 调用）。");

        // 断言：从事件 payload 提取 RequestId，查询 journal 验证状态为 Committed
        // 说明 DurableToolExecutor 走了完整 Prepare → Dispatch → Commit 流程
        var journalStates = new List<ToolDispatchState>();
        foreach (var evt in toolCallCompletedEvents)
        {
            using var doc = JsonDocument.Parse(evt.Payload);
            if (doc.RootElement.TryGetProperty("requestId", out var reqIdEl)
                && reqIdEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(reqIdEl.GetString()))
            {
                var requestId = reqIdEl.GetString()!;
                var entry = await journal.GetEntryAsync(new TenantRunKey(run.WorkspaceId, run.RunId), requestId);
                Assert.IsNotNull(entry,
                    $"Journal 应有 RequestId={requestId} 的条目（说明走了 DurableToolExecutor 流程，而非直接调 dispatcher）。");
                Assert.AreEqual(
                    ToolDispatchState.Committed,
                    entry!.State,
                    $"RequestId={requestId} 的 journal 状态应为 Committed（EchoToolDispatcher 声明 SideEffect=None，自动 MarkCommittedAsync）。");
                journalStates.Add(entry.State);
            }
        }

        Assert.IsTrue(journalStates.Count > 0,
            "应至少有一个 ToolCallCompleted 事件含 RequestId，且对应 journal 条目状态为 Committed。");
    }

    /// <summary>
    /// 验证：Tool 事件保留 RequestId、IdempotencyKey 和 SideEffect 信息。
    /// </summary>
    /// <remarks>
    /// 场景：执行一个 tool call，检查 event store 中的 ToolCallCompleted 事件 payload，
    /// 验证包含 RequestId、IdempotencyKey、SideEffect 字段，且 SideEffect 与 dispatcher 声明一致。
    /// </remarks>
    [TestMethod]
    public async Task ToolEvent_Preserves_RequestId_IdempotencyAndSideEffect()
    {
        // 准备：注入 DurableToolExecutor + Journal（让事件 payload 含完整 Tool 身份信息）
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var journal = new InMemoryToolDispatchJournal();
        var durableExecutor = new DefaultDurableToolExecutor(new EchoToolDispatcher(), journal);

        var run = BuildRun("search 验证 Tool 事件身份保留");
        await runStore.CreateAsync(run);

        // 自定义 tool triggers：将 "search" 关键词映射到 "echo" tool 名。
        // EchoToolDispatcher 仅支持 "echo"，DefaultDurableToolExecutor 会在
        // PrepareAsync 之前校验 SupportedTools；若 tool 名不匹配则返回失败结果
        // （SideEffect=Unknown），无法验证事件保留 dispatcher 声明的 SideEffect=None。
        var echoTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["search"] = "echo"
        };

        var actor = new AgentRunActor(
            runStore, eventStore, new DeterministicAgentModelTransport(echoTriggers),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            durableToolExecutor: durableExecutor);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言：事件流中有 ToolCallCompleted 事件
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        var toolCallCompletedEvents = events
            .Where(e => e.EventType == AgentRunEventType.ToolCallCompleted)
            .ToList();
        Assert.IsTrue(toolCallCompletedEvents.Count > 0,
            "应有 ToolCallCompleted 事件。");

        // 检查每个 ToolCallCompleted 事件 payload
        foreach (var evt in toolCallCompletedEvents)
        {
            using var doc = JsonDocument.Parse(evt.Payload);
            var root = doc.RootElement;

            // 断言 1：payload 包含 requestId 字段（非空）
            Assert.IsTrue(root.TryGetProperty("requestId", out var reqIdEl),
                "ToolCallCompleted payload 应包含 requestId 字段。");
            Assert.AreEqual(JsonValueKind.String, reqIdEl.ValueKind,
                "requestId 应为字符串类型。");
            Assert.IsFalse(string.IsNullOrEmpty(reqIdEl.GetString()),
                "requestId 不应为空字符串（DurableToolExecutor 生成稳定 RequestId）。");

            // 断言 2：payload 包含 idempotencyKey 字段（可为 null，但字段必须存在）
            Assert.IsTrue(root.TryGetProperty("idempotencyKey", out _),
                "ToolCallCompleted payload 应包含 idempotencyKey 字段（即使值为 null）。");

            // 断言 3：payload 包含 sideEffect 字段，且值为 EchoToolDispatcher 声明的 "None"
            Assert.IsTrue(root.TryGetProperty("sideEffect", out var sideEffectEl),
                "ToolCallCompleted payload 应包含 sideEffect 字段。");
            Assert.AreEqual(
                ToolSideEffect.None.ToString(),
                sideEffectEl.GetString(),
                "sideEffect 应保留 dispatcher 声明的值（EchoToolDispatcher = None）。");

            // 断言 4：payload 包含 journalState 字段（说明走了 DurableToolExecutor 流程）
            Assert.IsTrue(root.TryGetProperty("journalState", out var journalStateEl),
                "ToolCallCompleted payload 应包含 journalState 字段（DurableToolExecutor 产出）。");
            Assert.AreEqual(
                ToolDispatchState.Committed.ToString(),
                journalStateEl.GetString(),
                "journalState 应为 Committed（SideEffect=None 自动提交）。");

            // 断言 5：payload 包含 toolName 字段
            Assert.IsTrue(root.TryGetProperty("toolName", out _),
                "ToolCallCompleted payload 应包含 toolName 字段。");
        }
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static AgentRun BuildRun(
        string task,
        AgentTurnBudget? turnBudget = null) => new()
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-r29h-actor",
            SessionId = "session-r29h-actor",
            Task = task,
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = turnBudget
        };

    /// <summary>
    /// 从 JSON payload 中提取整型字段值。
    /// </summary>
    private static int ExtractIntField(string payload, string fieldName)
    {
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty(fieldName, out var el) && el.ValueKind == JsonValueKind.Number)
        {
            return el.GetInt32();
        }
        throw new AssertFailedException($"payload 中未找到整型字段 {fieldName}。");
    }

    // ── 测试 stub ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 录制模型调用入参的 IAgentModelTransport stub。
    /// 每次调用都返回构造时指定的固定响应，并将入参捕获到 CapturedCalls 列表。
    /// </summary>
    private sealed class RecordingModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse _response;
        public List<(string RunId, IReadOnlyList<AgentMessage> Messages)> CapturedCalls { get; } = new();

        public RecordingModelTransport(AgentModelResponse response)
        {
            _response = response;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
        {
            // 旧路径不应被调用（G1 重构后 Actor 走结构化 messages 重载）
            throw new NotImplementedException("应调用结构化 messages 重载。");
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentNullException.ThrowIfNull(messages);

            // 捕获调用入参（拷贝一份避免外部修改）
            CapturedCalls.Add((runId, messages.ToList()));
            return ValueTask.FromResult(_response);
        }

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);
    }

    /// <summary>
    /// 简单 IAgentCheckpointFactory stub，返回最小可持久化的 AgentCheckpoint。
    /// 用于测试 CheckpointSaved 事件触发场景（不依赖 DefaultAgentCheckpointFactory 的 KernelStateAccessor）。
    /// </summary>
    private sealed class StubCheckpointFactory : IAgentCheckpointFactory
    {
        public ValueTask<AgentCheckpoint> CreateCheckpointAsync(
            string checkpointId,
            string sessionId,
            string workspaceId,
            CancellationToken cancellationToken = default)
        {
            var checkpoint = new AgentCheckpoint
            {
                CheckpointId = checkpointId,
                Session = new AgentSessionId
                {
                    Value = sessionId,
                    WorkspaceId = workspaceId,
                    CreatedAt = DateTimeOffset.UtcNow
                },
                CreatedAt = DateTimeOffset.UtcNow,
                StateJson = "{\"mode\":\"stub\",\"sessionId\":\"" + sessionId + "\"}"
            };
            return ValueTask.FromResult(checkpoint);
        }
    }

    /// <summary>
    /// 总是抛异常的 IAgentCheckpointStore stub，用于验证 CheckpointSaved 事件
    /// 在 SaveAsync 失败时不被发出。
    /// </summary>
    private sealed class FailingCheckpointStore : IAgentCheckpointStore
    {
        public Task SaveAsync(AgentCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("模拟 checkpoint store 持久化失败。");
        }

        public Task<AgentCheckpoint?> GetAsync(string workspaceId, string checkpointId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("FailingCheckpointStore 不支持 GetAsync。");

        public Task<IReadOnlyList<AgentCheckpoint>> ListAsync(AgentSessionId sessionId, int take = 10, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("FailingCheckpointStore 不支持 ListAsync。");

        public Task<bool> DeleteAsync(string workspaceId, string checkpointId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("FailingCheckpointStore 不支持 DeleteAsync。");
    }
}
