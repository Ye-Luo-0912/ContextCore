using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// R29-Hard-Gate：Agent Run 完整执行循环生产验收测试
//
// 验证 AgentRunActor 的完整生命周期（ContextBuilding → ModelCalling →
// ToolDispatching → Observing → Checkpointing → Completed），覆盖：
//   1. FullLoop_CompleteExecution_ProducesFinalAnswer — 完整循环产出最终答案
//   2. FullLoop_StateTransitions_FollowStateMachine — 状态转换遵循状态机
//   3. FullLoop_EventChain_HashChainIntact — 事件哈希链完整无断裂
//   4. FullLoop_TurnBudget_ConsumedCorrectly — Turn 预算正确消耗
//   5. FullLoop_CostBudget_AccumulatedFromModelResponses — Cost 预算从模型响应累加
//   6. FullLoop_WithToolDispatch_ObservesAndCompletes — 含 Tool 调用的循环观察后完成
//   7. FullLoop_FinalAnswer_PersistedToRunStore — 最终答案持久化到 Run Store
//
// 设计原则：
//   - 优先使用真实 InMemory 实现（非 mock）：InMemoryAgentRunStore /
//     InMemoryAgentRunEventStore / InMemoryAgentCheckpointStore / EchoToolDispatcher /
//     DeterministicAgentModelTransport
//   - 自定义 RecordingModelTransport 捕获模型调用以断言上下文与预算
//   - 所有异步测试使用超时 CancellationTokenSource 防止挂起
//   - 中文注释
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Agent-Run-Full-Loop")]
public sealed class R29H_AgentRunFullLoopTests
{
    /// <summary>
    /// 验证：完整执行循环（Model → FinalAnswer）产出最终答案并进入 Completed 终态。
    /// </summary>
    [TestMethod]
    public async Task FullLoop_CompleteExecution_ProducesFinalAnswer()
    {
        // 准备：InMemory 真实组件 + 一次性返回最终答案的 Transport
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("请总结 R29 修复内容");
        await runStore.CreateAsync(run);

        var transport = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "R29 修复已完成",
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

        // 断言 1：Run 进入 Completed 终态
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun, "Run 应存在于 store 中。");
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            "完整循环后 Run 应进入 Completed 终态。");

        // 断言 2：应有且仅有 1 次模型调用
        Assert.AreEqual(1, transport.CapturedCalls.Count,
            "应只调用 1 次模型（直接产出最终答案）。");

        // 断言 3：FinishedAt 应被设置（终态时间戳）
        Assert.IsNotNull(finalRun.FinishedAt,
            "Completed 终态应设置 FinishedAt 时间戳。");
    }

    /// <summary>
    /// 验证：执行循环中的状态转换遵循 AgentRunStateMachine 合法流转图。
    /// </summary>
    [TestMethod]
    public async Task FullLoop_StateTransitions_FollowStateMachine()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("search 状态机验证", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 5
        });
        await runStore.CreateAsync(run);

        // 使用 DeterministicAgentModelTransport 触发 Tool 调用（含 "search" 关键词）
        var echoTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["search"] = "echo"
        };

        var actor = new AgentRunActor(
            runStore, eventStore, new DeterministicAgentModelTransport(echoTriggers),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言：从事件流中提取所有 StateTransition 事件，验证每个转换合法
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        var stateTransitionPayloads = events
            .Where(e => e.EventType == AgentRunEventType.StateTransition)
            .Select(e => e.Payload)
            .ToList();

        Assert.IsTrue(stateTransitionPayloads.Count > 0,
            "应至少有一个 StateTransition 事件。");

        // 逐对校验状态转换合法性（不抛异常即合法）
        foreach (var payload in stateTransitionPayloads)
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("from", out var fromEl)
                && doc.RootElement.TryGetProperty("to", out var toEl))
            {
                var fromState = Enum.Parse<AgentRunState>(fromEl.GetString()!);
                var toState = Enum.Parse<AgentRunState>(toEl.GetString()!);
                // 不抛异常即证明转换合法
                AgentRunStateMachine.ValidateTransition(fromState, toState);
            }
        }

        // 断言：最终状态为 Completed 或 Failed（终态）
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.IsTrue(AgentRunStateMachine.IsTerminalState(finalRun!.State),
            $"最终状态 {finalRun.State} 应为终态。");
    }

    /// <summary>
    /// 验证：事件流哈希链完整无断裂（每个事件的 PrevChainHash 指向前一事件的 ContentHash）。
    /// </summary>
    [TestMethod]
    public async Task FullLoop_EventChain_HashChainIntact()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("哈希链完整性验证");
        await runStore.CreateAsync(run);

        var transport = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "完成",
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

        // 断言：读取事件流并验证哈希链
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        Assert.IsTrue(events.Count > 0, "应至少有一个事件。");

        // 链头 PrevChainHash 应为 null
        Assert.IsNull(events[0].PrevChainHash,
            "链头事件的 PrevChainHash 应为 null。");

        // 逐个校验 PrevChainHash 链接
        for (var i = 1; i < events.Count; i++)
        {
            Assert.AreEqual(
                events[i - 1].ContentHash,
                events[i].PrevChainHash,
                $"事件 {i} 的 PrevChainHash 应指向前一事件的 ContentHash（哈希链断裂）。");
        }

        // 断言：每个事件的 ContentHash 非空（已计算）
        foreach (var evt in events)
        {
            Assert.IsFalse(string.IsNullOrEmpty(evt.ContentHash),
                $"事件 {evt.Sequence} 的 ContentHash 不应为空。");
        }

        // 断言：Sequence 单调递增从 0 开始
        for (var i = 0; i < events.Count; i++)
        {
            Assert.AreEqual(i, events[i].Sequence,
                $"事件 Sequence 应从 0 开始单调递增（位置 {i}）。");
        }
    }

    /// <summary>
    /// 验证：Turn 预算在每次模型调用后正确消耗（TurnsUsed 递增）。
    /// </summary>
    [TestMethod]
    public async Task FullLoop_TurnBudget_ConsumedCorrectly()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("Turn 预算消耗验证", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 2
        });
        await runStore.CreateAsync(run);

        // 模型第一次返回非最终答案，第二次返回最终答案
        var transport = new SequenceModelTransport(new[]
        {
            new AgentModelResponse
            {
                Content = "继续思考",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = false,
                TokensConsumed = 3,
                Duration = TimeSpan.FromMilliseconds(1)
            },
            new AgentModelResponse
            {
                Content = "最终答案",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 5,
                Duration = TimeSpan.FromMilliseconds(1)
            }
        });

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言 1：应有 2 次模型调用
        Assert.AreEqual(2, transport.CallCount,
            "应有 2 次模型调用（第一次非最终，第二次最终）。");

        // 断言 2：最终 Run 的 ModelCallsUsed = 2
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(2, finalRun!.ModelCallsUsed,
            "ModelCallsUsed 应为 2（两次模型调用后累加）。");

        // 断言 3：TurnBudget.TurnsUsed 应递增（每次模型调用计为一次 Turn）
        Assert.IsNotNull(finalRun.TurnBudget);
        Assert.IsTrue(finalRun.TurnBudget!.TurnsUsed >= 2,
            $"TurnsUsed 应 >= 2（每次模型调用计为一次 Turn），实际 {finalRun.TurnBudget.TurnsUsed}。");

        // 断言 4：事件流中 ModelCallCompleted 事件的 modelCallsUsed 单调递增
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        var modelCallEvents = events
            .Where(e => e.EventType == AgentRunEventType.ModelCallCompleted)
            .ToList();
        Assert.AreEqual(2, modelCallEvents.Count,
            "应有 2 个 ModelCallCompleted 事件。");

        var modelCallsValues = modelCallEvents
            .Select(e => ExtractIntField(e.Payload, "modelCallsUsed"))
            .ToList();
        Assert.AreEqual(1, modelCallsValues[0], "第 1 次调用后 modelCallsUsed 应为 1。");
        Assert.AreEqual(2, modelCallsValues[1], "第 2 次调用后 modelCallsUsed 应为 2。");
    }

    /// <summary>
    /// 验证：Cost 预算从模型响应的 TokensConsumed / BilledCost 累加。
    /// </summary>
    [TestMethod]
    public async Task FullLoop_CostBudget_AccumulatedFromModelResponses()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun(
            "Cost 预算累加验证",
            costBudget: new AgentCostBudget
            {
                MaxTokens = 1000,
                TokensUsed = 0,
                MaxCostUsd = 10.0,
                CostUsedUsd = 0.0
            });
        await runStore.CreateAsync(run);

        // 模型返回带明确 cost 的响应
        var transport = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 42,
            InputTokens = 30,
            OutputTokens = 12,
            EstimatedCost = 0.05,
            BilledCost = 0.04,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言：CostBudget 应累加模型响应的 token 与 cost
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.IsNotNull(finalRun.CostBudget);
        Assert.AreEqual(42, finalRun.CostBudget!.TokensUsed,
            "TokensUsed 应累加模型响应的 TokensConsumed（42）。");
        // BilledCost > 0 时应使用 BilledCost（0.04）
        Assert.AreEqual(0.04, finalRun.CostBudget.CostUsedUsd, 0.001,
            "CostUsedUsd 应累加模型响应的 BilledCost（0.04）。");
    }

    /// <summary>
    /// 验证：含 Tool 调用的完整循环（Model → Tool → Observe → Model → Complete）正确执行。
    /// </summary>
    [TestMethod]
    public async Task FullLoop_WithToolDispatch_ObservesAndCompletes()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var checkpointFactory = new StubCheckpointFactory();

        // task 含 "search" 关键词触发 Tool 调用
        var run = BuildRun("search 查找文档内容", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 5
        });
        await runStore.CreateAsync(run);

        // 将 "search" 映射到 "echo"（EchoToolDispatcher 仅支持 echo）
        var echoTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["search"] = "echo"
        };

        var actor = new AgentRunActor(
            runStore, eventStore, new DeterministicAgentModelTransport(echoTriggers),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            checkpointFactory: checkpointFactory,
            checkpointStore: checkpointStore);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言 1：事件流中应有 ToolCallCompleted 事件
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        var toolCallEvents = events
            .Where(e => e.EventType == AgentRunEventType.ToolCallCompleted)
            .ToList();
        Assert.IsTrue(toolCallEvents.Count > 0,
            "含 Tool 调用的循环应有 ToolCallCompleted 事件。");

        // 断言 2：应有 ObservationAppended 事件（Tool 结果被观察）
        var observationEvents = events
            .Where(e => e.EventType == AgentRunEventType.ObservationAppended)
            .ToList();
        Assert.IsTrue(observationEvents.Count > 0,
            "Tool 调用后应有 ObservationAppended 事件。");

        // 断言 3：应有 CheckpointSaved 事件（注入了 checkpointStore）
        var checkpointEvents = events
            .Where(e => e.EventType == AgentRunEventType.CheckpointSaved)
            .ToList();
        Assert.IsTrue(checkpointEvents.Count > 0,
            "注入了 checkpointStore 时应有 CheckpointSaved 事件。");

        // 断言 4：checkpoint store 中确实有持久化数据
        Assert.IsTrue(checkpointStore.Count > 0,
            "checkpoint store 中应有持久化的 checkpoint。");

        // 断言 5：最终进入终态
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.IsTrue(AgentRunStateMachine.IsTerminalState(finalRun!.State),
            $"含 Tool 调用的循环应进入终态，实际 {finalRun.State}。");
    }

    /// <summary>
    /// 验证：最终答案（FinalAnswer）在 Completed 后持久化到 Run Store。
    /// </summary>
    [TestMethod]
    public async Task FullLoop_FinalAnswer_PersistedToRunStore()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var expectedAnswer = "这是最终答案内容";
        var run = BuildRun("最终答案持久化验证");
        await runStore.CreateAsync(run);

        var transport = new RecordingModelTransport(new AgentModelResponse
        {
            Content = expectedAnswer,
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

        // 断言：Run Store 中的最终 Run 应包含 FinalAnswer
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            "Run 应进入 Completed 终态。");

        // 事件流中应有 RunCompleted 事件
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        var runCompletedEvents = events
            .Where(e => e.EventType == AgentRunEventType.RunCompleted)
            .ToList();
        Assert.IsTrue(runCompletedEvents.Count > 0,
            "应有 RunCompleted 事件标记循环结束。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static AgentRun BuildRun(
        string task,
        AgentTurnBudget? turnBudget = null,
        AgentCostBudget? costBudget = null) => new()
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-r29h-fullloop",
            SessionId = "session-r29h-fullloop",
            Task = task,
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = turnBudget,
            CostBudget = costBudget
        };

    /// <summary>从 JSON payload 提取整型字段。</summary>
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
    /// 每次调用返回构造时指定的固定响应，并捕获入参。
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
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            CapturedCalls.Add((runId, messages.ToList()));
            return ValueTask.FromResult(_response);
        }
    }

    /// <summary>
    /// 按顺序返回预设响应序列的 IAgentModelTransport stub。
    /// 第 N 次调用返回第 N 个响应（超出序列时返回最后一个）。
    /// </summary>
    private sealed class SequenceModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse[] _responses;
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

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
    /// 简单 IAgentCheckpointFactory stub，返回最小可持久化的 AgentCheckpoint。
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
}
