using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Endpoints;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.Tests;

// ===========================================================================
// 可恢复快照验收测试（P0-10 正式方案：Recoverable Snapshot + Anchor + Hot Delta）
//
// 覆盖：
// 1. AgentRunEventStateRebuilder 纯函数：Rebuild 重建对话流/工具观察/模型轮次/
//    Pending 命令；Serialize/TryDeserialize 往返；旧格式（仅锚点事件）返回 null；
//    RebuildExecutionModelTurn 的 initialValue 语义（绝对最大值 + 旧事件计数）。
// 2. Actor 快照恢复：折叠前缀归档 + 热表只留锚点 + 增量时，Recovery 按
//    "Snapshot → validate anchor → replay hot delta" 恢复——快照还原折叠历史、
//    校验热表锚点 ContentHash == 快照 ChainHeadHash、重放增量；锚点哈希不匹配
//    时 fail-closed 判定 RecoveryCorrupted。
// 3. Raw Events 端点审计拼接：MergeRawEventStreams 按 Sequence 合并归档 + 热表，
//    保证压缩后管理员仍能看到完整事件历史（审计缺口修复）。
//
// 设计原则：
// - 优先使用真实 InMemory 实现；压缩后热表无法由 InMemoryAgentRunEventStore 表示
//   （其要求事件从 Sequence 0 开始），故用 CompactedAgentRunEventStore 模拟
//   Postgres 压缩后的热表语义（首事件 = 锚点，Sequence > 0）；
// - 事件流种子使用 AgentRunEventChain.BuildEvent 构造合法哈希链；
// - 所有异步测试使用超时 CancellationTokenSource 防止挂起；
// - 中文注释。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
[TestCategory("Agent-Run-Recoverable-Snapshot")]
public sealed class R30S_RecoverableSnapshotTests
{
    private const string WorkspaceId = "ws-r30s-snapshot";
    private const string SessionId = "session-r30s-snapshot";

    // ---------------------------------------------------------------------------
    // 1. AgentRunEventStateRebuilder 纯函数
    // ---------------------------------------------------------------------------

    /// <summary>
    /// 验证：Rebuild 从折叠事件重建完整可恢复状态——对话流（Assistant + Tool 消息）、
    /// 工具观察、模型轮次、折叠头 Sequence/ChainHeadHash。
    /// </summary>
    [TestMethod]
    public void Rebuild_BuildsConversationObservationsTurnAndHead()
    {
        var events = BuildMixedChain("run-rebuild-1", "compacted-content", includeTool: true);

        var state = AgentRunEventStateRebuilder.Rebuild(events);

        Assert.AreEqual(events[^1].Sequence, state.Sequence, "快照 Sequence 应为折叠最后事件 sequence。");
        Assert.AreEqual(events[^1].ContentHash, state.ChainHeadHash, "快照 ChainHeadHash 应为折叠最后事件 ContentHash。");

        // 对话流：Assistant（ModelCallCompleted）+ Tool（ToolCallCompleted）
        Assert.AreEqual(2, state.Conversation.Count, "对话流应包含 Assistant + Tool 两条消息。");
        Assert.AreEqual(AgentMessageRole.Assistant, state.Conversation[0].Role);
        Assert.AreEqual("compacted-content", state.Conversation[0].Content);
        Assert.AreEqual(AgentMessageRole.Tool, state.Conversation[1].Role);
        StringAssert.Contains(state.Conversation[1].Content, "echo-out");

        // 工具观察
        Assert.AreEqual(1, state.ToolObservations.Count, "应重建 1 条工具观察。");
        Assert.AreEqual("echo", state.ToolObservations[0].ToolName);
        Assert.IsTrue(state.ToolObservations[0].Succeeded);

        // 模型轮次（事件内嵌 executionModelTurn=1）
        Assert.AreEqual(1, state.ExecutionModelTurn, "应从内嵌 executionModelTurn 重建模型轮次。");

        // 折叠前缀无审批事件 → PendingToolCommands 为 null
        Assert.IsNull(state.PendingToolCommands, "无 ApprovalRequested 事件时 PendingToolCommands 应为 null。");
    }

    /// <summary>
    /// 验证：空事件流无法构建可恢复状态（防御）。
    /// </summary>
    [TestMethod]
    public void Rebuild_ThrowsOnEmptyStream()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            AgentRunEventStateRebuilder.Rebuild(Array.Empty<AgentRunEvent>()));
    }

    /// <summary>
    /// 验证：Serialize → TryDeserialize 往返一致（压缩器写入与 Actor 读取语义对齐）。
    /// </summary>
    [TestMethod]
    public void Serialize_TryDeserialize_RoundTrips()
    {
        var events = BuildMixedChain("run-roundtrip", "roundtrip-content", includeTool: true);
        var state = AgentRunEventStateRebuilder.Rebuild(events);

        var json = AgentRunEventStateRebuilder.Serialize(state);
        var restored = AgentRunEventStateRebuilder.TryDeserialize(json);

        Assert.IsNotNull(restored, "可恢复状态应能往返反序列化。");
        Assert.AreEqual(state.Sequence, restored!.Sequence);
        Assert.AreEqual(state.ChainHeadHash, restored.ChainHeadHash);
        Assert.AreEqual(state.ExecutionModelTurn, restored.ExecutionModelTurn);
        Assert.AreEqual(state.Conversation.Count, restored.Conversation.Count);
        Assert.AreEqual(state.Conversation[0].Content, restored.Conversation[0].Content);
        Assert.AreEqual(state.Conversation[1].Content, restored.Conversation[1].Content);
        Assert.AreEqual(state.ToolObservations.Count, restored.ToolObservations.Count);
        Assert.AreEqual("echo", restored.ToolObservations[0].ToolName);
    }

    /// <summary>
    /// 验证：旧格式快照（仅序列化锚点事件，非 AgentRunRecoverableState）无法解析 →
    /// 返回 null（调用方降级为现有恢复路径；压缩过的热表会 fail-closed 判定 RecoveryCorrupted）。
    /// </summary>
    [TestMethod]
    public void TryDeserialize_OldFormatAnchorJson_ReturnsNull()
    {
        var events = BuildMixedChain("run-oldformat", "old-content", includeTool: false);
        var anchorJson = JsonSerializer.Serialize(events[^1]); // 旧版 state_json = 锚点事件 data

        var restored = AgentRunEventStateRebuilder.TryDeserialize(anchorJson);

        Assert.IsNull(restored, "旧格式（锚点事件序列化）不应被解析为可恢复状态。");
    }

    /// <summary>
    /// 验证：折叠前缀含 ApprovalRequested 事件时，Rebuild 提取 PendingToolCommands
    /// （审批恢复依赖快照保存的 Pending 命令——折叠后增量中可能已无审批事件）。
    /// </summary>
    [TestMethod]
    public void Rebuild_ExtractsPendingToolCommands_FromApprovalRequested()
    {
        var chain = new List<AgentRunEvent>();
        chain.Add(AgentRunEventChain.BuildEvent("run-approval", WorkspaceId, 0,
            AgentRunEventType.RunCreated, AgentRunState.Created, """{"runId":"seed"}""", null));
        chain.Add(AgentRunEventChain.BuildEvent("run-approval", WorkspaceId, 1,
            AgentRunEventType.ApprovalRequested, AgentRunState.AwaitingApproval,
            JsonSerializer.Serialize(new
            {
                toolName = "echo",
                reason = "needs-approval",
                pendingToolCommands = new[]
                {
                    new { ToolCallId = "tc-1", ToolName = "echo", ArgumentsJson = "{}", IdempotencyKey = "ik-1", ModelTurnRevision = 1 }
                }
            }),
            chain[^1].ContentHash));

        var state = AgentRunEventStateRebuilder.Rebuild(chain);

        Assert.IsNotNull(state.PendingToolCommands, "含 ApprovalRequested 事件时应提取 PendingToolCommands。");
        Assert.AreEqual(1, state.PendingToolCommands!.Count);
        Assert.AreEqual("tc-1", state.PendingToolCommands[0].ToolCallId);
        Assert.AreEqual("echo", state.PendingToolCommands[0].ToolName);
        Assert.AreEqual("ik-1", state.PendingToolCommands[0].IdempotencyKey);
    }

    /// <summary>
    /// 验证：RebuildExecutionModelTurn 的 initialValue 语义——事件内嵌 executionModelTurn
    /// 为绝对计数（取 max(initialValue, embedded)），旧事件（无内嵌字段）在初始值上计数递增。
    /// </summary>
    [TestMethod]
    public void RebuildExecutionModelTurn_WithInitialValue_TakesMaxAndCountsLegacy()
    {
        var chain = new List<AgentRunEvent>();
        chain.Add(AgentRunEventChain.BuildEvent("run-turn", WorkspaceId, 0,
            AgentRunEventType.ModelCallCompleted, AgentRunState.ModelCalling,
            """{"content":"embedded-3","executionModelTurn":3}""", null));
        chain.Add(AgentRunEventChain.BuildEvent("run-turn", WorkspaceId, 1,
            AgentRunEventType.ModelCallCompleted, AgentRunState.ModelCalling,
            """{"content":"legacy"}""", chain[^1].ContentHash)); // 旧事件：无内嵌字段 → 计数

        // initialValue = 2：内嵌 3 > 2 → 3；旧事件计数 → 4
        Assert.AreEqual(4, AgentRunEventStateRebuilder.RebuildExecutionModelTurn(chain, initialValue: 2));
        // initialValue = 5：内嵌 3 < 5 → 保持 5；旧事件计数 → 6
        Assert.AreEqual(6, AgentRunEventStateRebuilder.RebuildExecutionModelTurn(chain, initialValue: 5));
    }

    // ---------------------------------------------------------------------------
    // 1b. 压缩器增量快照重建（既有快照 + 热表增量；无快照时全量）
    // ---------------------------------------------------------------------------

    /// <summary>
    /// 验证：无既有快照时（首次压缩），增量重建退化为对增量事件的全量 Rebuild
    /// （与 AgentRunEventStateRebuilder.Rebuild 语义一致）。
    /// </summary>
    [TestMethod]
    public void RebuildIncrementally_NullBase_FullRebuildsDelta()
    {
        var delta = BuildMixedChain("run-incr-null", "delta-content", includeTool: true);

        var state = PostgresAgentRunEventCompactor.RebuildSnapshotIncrementally(null, delta);
        var full = AgentRunEventStateRebuilder.Rebuild(delta);

        Assert.AreEqual(full.Sequence, state.Sequence);
        Assert.AreEqual(full.ChainHeadHash, state.ChainHeadHash);
        Assert.AreEqual(full.Conversation.Count, state.Conversation.Count);
        Assert.AreEqual(full.Conversation[1].Content, state.Conversation[1].Content);
        Assert.AreEqual(full.ToolObservations.Count, state.ToolObservations.Count);
        Assert.AreEqual(full.ExecutionModelTurn, state.ExecutionModelTurn);
    }

    /// <summary>
    /// 验证：以既有快照为基准时，增量重建只追加增量事件的重建贡献——
    /// 对话流 = 既有 + 增量新增、Sequence/ChainHeadHash = 增量最后事件、
    /// 模型轮次取 max（快照轮次, 增量内嵌轮次）；既有快照列表不被修改（拷贝语义）。
    /// </summary>
    [TestMethod]
    public void RebuildIncrementally_AppendsDeltaToBase()
    {
        var baseEvents = BuildMixedChain("run-incr-append", "base-content", includeTool: true);
        var baseState = AgentRunEventStateRebuilder.Rebuild(baseEvents);
        var baseConversationCount = baseState.Conversation.Count;

        var delta = new List<AgentRunEvent>
        {
            AgentRunEventChain.BuildEvent("run-incr-append", WorkspaceId, 6,
                AgentRunEventType.ModelCallCompleted, AgentRunState.ModelCalling,
                JsonSerializer.Serialize(new
                {
                    content = "delta-content",
                    toolCallCount = 0,
                    isFinalAnswer = true,
                    executionModelTurn = 2
                }),
                baseEvents[^1].ContentHash)
        };

        var state = PostgresAgentRunEventCompactor.RebuildSnapshotIncrementally(baseState, delta);

        Assert.AreEqual(6, state.Sequence, "增量快照 Sequence 应为增量最后事件 sequence。");
        Assert.AreEqual(delta[^1].ContentHash, state.ChainHeadHash);
        Assert.AreEqual(baseConversationCount + 1, state.Conversation.Count,
            "对话流应 = 既有快照 + 增量新增消息。");
        Assert.AreEqual("delta-content", state.Conversation[^1].Content);
        Assert.AreEqual(2, state.ExecutionModelTurn, "模型轮次应取 max(快照, 增量内嵌)。");
        Assert.AreEqual(baseConversationCount, baseState.Conversation.Count,
            "增量重建不得修改既有快照的对话流（拷贝语义）。");
    }

    /// <summary>
    /// 验证：PendingToolCommands 的增量语义——增量含最后一个 ApprovalRequested 时用增量提取值；
    /// 增量无审批事件时沿用既有快照的 Pending 命令（已归档前缀的审批不丢失）。
    /// </summary>
    [TestMethod]
    public void RebuildIncrementally_PendingToolCommands_DeltaWinsElseKeepsBase()
    {
        var baseChain = new List<AgentRunEvent>();
        baseChain.Add(AgentRunEventChain.BuildEvent("run-incr-ptc", WorkspaceId, 0,
            AgentRunEventType.RunCreated, AgentRunState.Created, """{"runId":"seed"}""", null));
        baseChain.Add(AgentRunEventChain.BuildEvent("run-incr-ptc", WorkspaceId, 1,
            AgentRunEventType.ApprovalRequested, AgentRunState.AwaitingApproval,
            JsonSerializer.Serialize(new
            {
                toolName = "echo",
                reason = "needs-approval",
                pendingToolCommands = new[]
                {
                    new { ToolCallId = "tc-1", ToolName = "echo", ArgumentsJson = "{}", IdempotencyKey = "ik-1", ModelTurnRevision = 1 }
                }
            }),
            baseChain[^1].ContentHash));
        var baseState = AgentRunEventStateRebuilder.Rebuild(baseChain);
        Assert.IsNotNull(baseState.PendingToolCommands);

        // 增量无审批事件 → 沿用既有快照的 Pending 命令。
        var noApprovalDelta = new List<AgentRunEvent>
        {
            AgentRunEventChain.BuildEvent("run-incr-ptc", WorkspaceId, 2,
                AgentRunEventType.StateTransition, AgentRunState.ContextBuilding,
                """{"from":"AwaitingApproval","to":"ContextBuilding"}""", baseChain[^1].ContentHash)
        };
        var kept = PostgresAgentRunEventCompactor.RebuildSnapshotIncrementally(baseState, noApprovalDelta);
        Assert.IsNotNull(kept.PendingToolCommands, "增量无审批事件时应沿用既有快照的 Pending 命令。");
        Assert.AreEqual("tc-1", kept.PendingToolCommands![0].ToolCallId);

        // 增量含 ApprovalRequested → 用增量提取值（最后一个审批优先）。
        var approvalDelta = new List<AgentRunEvent>
        {
            AgentRunEventChain.BuildEvent("run-incr-ptc", WorkspaceId, 2,
                AgentRunEventType.ApprovalRequested, AgentRunState.AwaitingApproval,
                JsonSerializer.Serialize(new
                {
                    toolName = "echo",
                    reason = "needs-approval-again",
                    pendingToolCommands = new[]
                    {
                        new { ToolCallId = "tc-2", ToolName = "echo", ArgumentsJson = "{}", IdempotencyKey = "ik-2", ModelTurnRevision = 2 }
                    }
                }),
                baseChain[^1].ContentHash)
        };
        var replaced = PostgresAgentRunEventCompactor.RebuildSnapshotIncrementally(baseState, approvalDelta);
        Assert.IsNotNull(replaced.PendingToolCommands, "增量含审批事件时应提取增量的 Pending 命令。");
        Assert.AreEqual("tc-2", replaced.PendingToolCommands![0].ToolCallId);
    }

    /// <summary>
    /// 验证：增量重建的模型轮次语义——增量内嵌轮次取 max；旧事件（无内嵌字段）在快照轮次上计数递增。
    /// </summary>
    [TestMethod]
    public void RebuildIncrementally_ExecutionModelTurn_TakesMaxAndCountsLegacy()
    {
        var baseState = new AgentRunRecoverableState
        {
            Sequence = 5,
            ChainHeadHash = "base-hash",
            Conversation = [],
            ToolObservations = [],
            ExecutionModelTurn = 3,
            PendingToolCommands = null
        };

        // 增量内嵌轮次 1 < 快照 3 → 保持 3；随后旧事件（无内嵌字段）计数 → 4。
        var delta = new List<AgentRunEvent>
        {
            AgentRunEventChain.BuildEvent("run-incr-turn", WorkspaceId, 6,
                AgentRunEventType.ModelCallCompleted, AgentRunState.ModelCalling,
                """{"content":"embedded-1","executionModelTurn":1}""", null),
            AgentRunEventChain.BuildEvent("run-incr-turn", WorkspaceId, 7,
                AgentRunEventType.ModelCallCompleted, AgentRunState.ModelCalling,
                """{"content":"legacy"}""", null)
        };

        var state = PostgresAgentRunEventCompactor.RebuildSnapshotIncrementally(baseState, delta);

        Assert.AreEqual(4, state.ExecutionModelTurn,
            "内嵌轮次取 max(3,1)=3，旧事件计数 → 4。");
    }

    // ---------------------------------------------------------------------------
    // 2. Actor 快照恢复（Snapshot → validate anchor → replay hot delta）
    // ---------------------------------------------------------------------------

    /// <summary>
    /// 验证：折叠前缀归档 + 热表只留锚点 + 增量时，Actor 从可恢复快照恢复折叠历史
    /// （对话流含压缩前内容）、校验锚点哈希、重放热表增量（含压缩后工具观察），
    /// Run 正常完成，跨归档/热表的完整哈希链保持完整（审计不丢失）。
    /// </summary>
    [TestMethod]
    public async Task Actor_Recovery_UsesSnapshot_ThenReplaysHotDelta()
    {
        var runStore = new InMemoryAgentRunStore();
        var run = BuildRun("快照恢复验证任务") with { State = AgentRunState.ContextBuilding };
        await runStore.CreateAsync(run);

        // 种子完整链（seq 0-6，非终态）：RunCreated / StateTransition / ModelCallStarted /
        // ModelCallCompleted("compacted-content", executionModelTurn=1) / StateTransition /
        // ToolCallCompleted("echo-out") / StateTransition——Run 在工具观察后崩溃（ContextBuilding）。
        var chain = BuildMixedChain(run.RunId, "compacted-content", includeTool: true);
        Assert.AreEqual(7, chain.Count, "种子链应含 7 个事件（seq 0-6）。");

        // 模拟压缩：锚点 = seq 3（ModelCallCompleted）；归档 [0..2]，热表 [3..6]。
        // 快照可恢复状态覆盖折叠前缀 [0..3]（含锚点），与快照记录 Sequence=3 一致。
        var anchor = chain[3];
        var archived = chain.Take(3).ToList();
        var hot = chain.Skip(3).ToList();
        var snapshot = BuildSnapshot(run, anchor, chain.Take(anchor.Sequence + 1).ToList(), archived.Count);

        var hotStore = new CompactedAgentRunEventStore(hot, runStore);
        var compactor = new FakeAgentRunEventCompactor(archived, snapshot);
        var recording = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "恢复后完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 10,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var actor = new AgentRunActor(
            runStore, hotStore, recording, new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            eventCompactor: compactor);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 断言 1：恢复后模型被调用，且入参包含快照重建的折叠历史 + 热表增量工具观察
        Assert.AreEqual(1, recording.CapturedCalls.Count, "恢复后应调用 1 次模型（最终答案后完成）。");
        var projected = string.Join("\n", recording.CapturedCalls[0].Messages.Select(m => m.Content));
        StringAssert.Contains(projected, "compacted-content",
            "模型应看到快照重建的折叠历史（压缩前对话内容不丢失）。");
        StringAssert.Contains(projected, "echo-out",
            "模型应看到热表增量事件重建的工具观察（replay hot delta）。");

        // 断言 2：Run 正常完成（终态）
        var finalRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun, "Run 应存在于 store 中。");
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            "快照恢复执行后 Run 应进入 Completed 终态。");

        // 断言 3：热表从锚点（seq 3）续写；归档 + 热表拼接后完整哈希链连续（审计不丢失）。
        // 种子热表 4 事件（seq 3-6）+ 恢复续写 5 事件（StateTransition / ModelCallStarted /
        // ModelCallCompleted / StateTransition / RunCompleted）= 9 事件（seq 3-11）。
        var hotAfter = hotStore.Snapshot;
        Assert.AreEqual(9, hotAfter.Count, "热表应为种子锚点+增量 + 恢复续写事件。");
        Assert.AreEqual(3, hotAfter[0].Sequence, "热表首事件应为压缩锚点（seq 3）。");
        Assert.AreEqual(AgentRunEventType.RunCompleted, hotAfter[^1].EventType,
            "恢复执行应追加 RunCompleted 终态事件。");
        Assert.AreEqual(anchor.ContentHash, hotAfter[0].ContentHash,
            "热表锚点 ContentHash 应与快照 ChainHeadHash 一致。");

        // 归档 + 热表 = 完整链 [0..11]；VerifyChain 要求 Sequence 从 0 连续 + PrevChainHash 链接。
        var fullChain = archived.Concat(hotAfter).ToList();
        Assert.IsTrue(AgentRunEventChain.VerifyChain(fullChain),
            "归档 + 热表的完整事件链必须无断裂（审计保留 + 哈希链完整）。");
        Assert.AreEqual(3, compactor.Archived.Count, "折叠前缀事件必须保留在归档（审计不丢失）。");
    }

    /// <summary>
    /// 验证：热表锚点 ContentHash 与快照 ChainHeadHash 不一致（锚点被替换/篡改）时，
    /// Recovery fail-closed 判定 RecoveryCorrupted（快照来自不可变归档，是权威基准）。
    /// </summary>
    [TestMethod]
    public async Task Actor_Recovery_SnapshotAnchorMismatch_FailsClosedRecoveryCorrupted()
    {
        var runStore = new InMemoryAgentRunStore();
        var run = BuildRun("锚点篡改验证任务") with { State = AgentRunState.ContextBuilding };
        await runStore.CreateAsync(run);

        var chain = BuildMixedChain(run.RunId, "compacted-content", includeTool: true);
        var anchor = chain[3];
        var archived = chain.Take(3).ToList();
        var hot = chain.Skip(3).ToList();

        // 快照记录 ChainHeadHash 被篡改（与热表锚点真实 ContentHash 不一致）；
        // 可恢复状态仍覆盖折叠前缀 [0..3]。
        var snapshot = BuildSnapshot(run, anchor, chain.Take(anchor.Sequence + 1).ToList(), archived.Count) with
        {
            ChainHeadHash = "tampered-chain-head-hash"
        };

        var hotStore = new CompactedAgentRunEventStore(hot, runStore);
        var compactor = new FakeAgentRunEventCompactor(archived, snapshot);
        var actor = new AgentRunActor(
            runStore, hotStore, new DeterministicAgentModelTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            eventCompactor: compactor);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // fail-closed：快照锚点校验失败 → RecoveryCorrupted 终态（不得回退为全新启动/继续执行）。
        var finalRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun, "Run 应存在于 store 中。");
        Assert.AreEqual(AgentRunState.RecoveryCorrupted, finalRun!.State,
            "快照锚点哈希不匹配必须判定 RecoveryCorrupted（fail-closed）。");
        StringAssert.Contains(finalRun.FailureReason ?? string.Empty, "RecoveryCorrupted",
            "FailureReason 应记录 RecoveryCorrupted 语义（具体异常细节已 Trace 记录）。");
    }

    // ---------------------------------------------------------------------------
    // 3. Raw Events 审计拼接（MergeRawEventStreams）
    // ---------------------------------------------------------------------------

    /// <summary>
    /// 验证：归档（折叠前缀）与热表（锚点 + 增量）按 Sequence 合并，输出保持升序
    /// （压缩后管理员 raw 端点仍能看到完整事件历史）。
    /// </summary>
    [TestMethod]
    public void MergeRawEventStreams_InterleavesArchivedAndHotBySequence()
    {
        // 归档 seq 0-2，热表 seq 3-6（压缩后：热表从锚点开始）。
        var archived = BuildPlainEvents(0, 3);
        var hot = BuildPlainEvents(3, 7);

        var merged = new List<AgentRunEvent>();
        AgentExecutionEndpoints.MergeRawEventStreams(archived, hot, limit: 100, merged);

        Assert.AreEqual(7, merged.Count, "合并应包含归档 + 热表全部事件。");
        for (var i = 0; i < merged.Count; i++)
        {
            Assert.AreEqual(i, merged[i].Sequence, "合并输出必须按 Sequence 升序。");
        }
    }

    /// <summary>
    /// 验证：合并受 limit 截断（调用方用 limit = 页大小 + 1 判定 HasMore）。
    /// </summary>
    [TestMethod]
    public void MergeRawEventStreams_RespectsLimit()
    {
        var archived = BuildPlainEvents(0, 3);
        var hot = BuildPlainEvents(3, 7);

        var merged = new List<AgentRunEvent>();
        AgentExecutionEndpoints.MergeRawEventStreams(archived, hot, limit: 5, merged);

        Assert.AreEqual(5, merged.Count, "合并输出应截断到 limit。");
        Assert.AreEqual(0, merged[0].Sequence);
        Assert.AreEqual(4, merged[^1].Sequence);
    }

    /// <summary>
    /// 验证：非 Postgres provider（compactor 未注册）时仅热表可读的场景下，
    /// 归档为空 → 合并等价于热表本身（向后兼容）。
    /// </summary>
    [TestMethod]
    public void MergeRawEventStreams_EmptyArchived_FallsBackToHot()
    {
        var hot = BuildPlainEvents(3, 7);

        var merged = new List<AgentRunEvent>();
        AgentExecutionEndpoints.MergeRawEventStreams(Array.Empty<AgentRunEvent>(), hot, limit: 100, merged);

        Assert.AreEqual(4, merged.Count, "归档为空时合并应等价于热表。");
        Assert.AreEqual(3, merged[0].Sequence);
        Assert.AreEqual(6, merged[^1].Sequence);
    }

    // ---------------------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------------------

    /// <summary>
    /// 种子混合链（非终态）：RunCreated / StateTransition / ModelCallStarted /
    /// ModelCallCompleted(content, executionModelTurn=1) / StateTransition /
    /// [ToolCallCompleted("echo-out")] / StateTransition。
    /// includeTool=true 时共 7 事件（seq 0-6），false 时 5 事件（seq 0-4）。
    /// </summary>
    private static List<AgentRunEvent> BuildMixedChain(string runId, string modelContent, bool includeTool)
    {
        var chain = new List<AgentRunEvent>();
        chain.Add(AgentRunEventChain.BuildEvent(runId, WorkspaceId, 0,
            AgentRunEventType.RunCreated, AgentRunState.Created, """{"runId":"seed"}""", null));
        chain.Add(AgentRunEventChain.BuildEvent(runId, WorkspaceId, 1,
            AgentRunEventType.StateTransition, AgentRunState.ContextBuilding,
            """{"from":"Created","to":"ContextBuilding"}""", chain[^1].ContentHash));
        chain.Add(AgentRunEventChain.BuildEvent(runId, WorkspaceId, 2,
            AgentRunEventType.ModelCallStarted, AgentRunState.ModelCalling,
            """{"turn":1}""", chain[^1].ContentHash));
        chain.Add(AgentRunEventChain.BuildEvent(runId, WorkspaceId, 3,
            AgentRunEventType.ModelCallCompleted, AgentRunState.ModelCalling,
            JsonSerializer.Serialize(new
            {
                content = modelContent,
                toolCallCount = 0,
                isFinalAnswer = false,
                executionModelTurn = 1
            }),
            chain[^1].ContentHash));
        chain.Add(AgentRunEventChain.BuildEvent(runId, WorkspaceId, 4,
            AgentRunEventType.StateTransition, AgentRunState.ContextBuilding,
            """{"from":"ModelCalling","to":"ContextBuilding"}""", chain[^1].ContentHash));

        if (includeTool)
        {
            chain.Add(AgentRunEventChain.BuildEvent(runId, WorkspaceId, 5,
                AgentRunEventType.ToolCallCompleted, AgentRunState.ContextBuilding,
                """{"succeeded":true,"toolName":"echo","toolCallId":"tc-1","output":"echo-out"}""",
                chain[^1].ContentHash));
            chain.Add(AgentRunEventChain.BuildEvent(runId, WorkspaceId, 6,
                AgentRunEventType.StateTransition, AgentRunState.ContextBuilding,
                """{"from":"ContextBuilding","to":"ContextBuilding"}""", chain[^1].ContentHash));
        }

        return chain;
    }

    /// <summary>
    /// 构造压缩快照：state_json = 折叠前缀 [0..anchor] 重建的可恢复状态
    /// （与 PostgresAgentRunEventCompactor.UpsertSnapshotAsync 语义一致）。
    /// </summary>
    private static AgentRunEventSnapshot BuildSnapshot(
        AgentRun run,
        AgentRunEvent anchor,
        IReadOnlyList<AgentRunEvent> prefixEvents,
        int foldedEventCount)
        => new()
        {
            WorkspaceId = run.WorkspaceId,
            RunId = run.RunId,
            Sequence = anchor.Sequence,
            ChainHeadHash = anchor.ContentHash,
            FoldedEventCount = foldedEventCount,
            StateJson = AgentRunEventStateRebuilder.Serialize(
                AgentRunEventStateRebuilder.Rebuild(prefixEvents)),
            CreatedAt = DateTimeOffset.UtcNow
        };

    /// <summary>构造 sequence 升序的简单事件（merge 测试用；Sequence ∈ [start, end)）。</summary>
    private static List<AgentRunEvent> BuildPlainEvents(int start, int end)
    {
        var list = new List<AgentRunEvent>(end - start);
        for (var i = start; i < end; i++)
        {
            list.Add(new AgentRunEvent
            {
                EventId = $"evt-{i}",
                RunId = "run-merge",
                WorkspaceId = WorkspaceId,
                Sequence = i,
                EventType = AgentRunEventType.RunCreated,
                State = AgentRunState.Created,
                Payload = $"payload-{i}",
                ContentHash = $"hash-{i}",
                PrevChainHash = i == 0 ? null : $"hash-{i - 1}",
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(i)
            });
        }
        return list;
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

    /// <summary>
    /// 模拟压缩后热表的进程内事件存储：事件列表从锚点 sequence 开始（首事件 Sequence &gt; 0），
    /// 追加按"最后事件 Sequence + 1"校验连续性 + PrevChainHash 链接，与 Postgres 压缩后的
    /// 热表语义一致（InMemoryAgentRunEventStore 要求事件从 0 开始，无法表示压缩场景）。
    /// </summary>
    private sealed class CompactedAgentRunEventStore : IAgentRunEventStore
    {
        private readonly object _gate = new();
        private readonly List<AgentRunEvent> _events = new();
        private readonly IAgentRunStore? _runStore;

        public CompactedAgentRunEventStore(IReadOnlyList<AgentRunEvent> hotEvents, IAgentRunStore? runStore = null)
        {
            _events.AddRange(hotEvents.OrderBy(e => e.Sequence));
            _runStore = runStore;
        }

        /// <summary>当前热表快照（供断言）。</summary>
        public IReadOnlyList<AgentRunEvent> Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToList();
                }
            }
        }

        public ValueTask AppendAsync(
            AgentRunEvent @event,
            CancellationToken cancellationToken = default,
            string? leaseToken = null,
            long? fencingToken = null)
        {
            _ = leaseToken;
            _ = fencingToken;
            lock (_gate)
            {
                ValidateAppend(@event);
                _events.Add(@event);
            }
            return ValueTask.CompletedTask;
        }

        public async ValueTask AppendBatchAsync(
            IReadOnlyList<AgentRunEvent> events,
            AgentRunStateUpdate? runStateUpdate,
            AgentCheckpointCursor? checkpointCursor,
            AgentCheckpoint? checkpointBody,
            CancellationToken cancellationToken = default)
        {
            if (events.Count == 0 && runStateUpdate is null)
            {
                return;
            }

            if (events.Count > 0)
            {
                lock (_gate)
                {
                    for (var i = 0; i < events.Count; i++)
                    {
                        ValidateAppend(events[i]);
                        if (i > 0)
                        {
                            var expectedHash = AgentRunEventChain.ComputeContentHash(events[i - 1]);
                            if (!string.Equals(events[i].PrevChainHash, expectedHash, StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException(
                                    $"批量事件内 PrevChainHash 链接断裂：run={events[i].RunId}，" +
                                    $"位置 {i}：期望={expectedHash}，实际={events[i].PrevChainHash ?? "<null>"}。");
                            }
                        }
                        var contentHash = AgentRunEventChain.ComputeContentHash(events[i]);
                        if (!string.Equals(events[i].ContentHash, contentHash, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"批量事件 ContentHash 与 Payload 不一致：run={events[i].RunId}，" +
                                $"位置 {i}：{events[i].ContentHash ?? "<null>"} != {contentHash}。");
                        }
                        _events.Add(events[i]);
                    }
                }
            }

            if (runStateUpdate is not null && _runStore is not null)
            {
                await _runStore.TransitionStateAsync(
                    runStateUpdate.WorkspaceId,
                    runStateUpdate.RunId,
                    runStateUpdate.ExpectedCurrentState,
                    runStateUpdate.NewState,
                    cancellationToken,
                    runStateUpdate.LeaseToken,
                    runStateUpdate.FencingToken).ConfigureAwait(false);
                await _runStore.UpdateAsync(runStateUpdate.RunSnapshot, cancellationToken).ConfigureAwait(false);
            }
        }

        public ValueTask<IReadOnlyList<AgentRunEvent>> ReadAsync(
            string workspaceId,
            string runId,
            int fromSequence = 0,
            int take = 1000,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var results = _events
                    .Where(e => e.Sequence >= fromSequence)
                    .OrderBy(e => e.Sequence)
                    .Take(take)
                    .ToList();
                return ValueTask.FromResult<IReadOnlyList<AgentRunEvent>>(results);
            }
        }

        public ValueTask<int> GetLastSequenceAsync(
            string workspaceId, string runId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(_events.Count == 0 ? -1 : _events[^1].Sequence);
            }
        }

        public ValueTask<int> GetAttemptBoundarySequenceAsync(
            string workspaceId, string runId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                for (var i = _events.Count - 1; i >= 0; i--)
                {
                    if (_events[i].EventType == AgentRunEventType.RunRetryScheduled)
                    {
                        return ValueTask.FromResult(_events[i].Sequence);
                    }
                }
                return ValueTask.FromResult(-1);
            }
        }

        public ValueTask<AgentCheckpointCursor?> GetCheckpointCursorAsync(
            string workspaceId, string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentCheckpointCursor?>(null);

        private void ValidateAppend(AgentRunEvent evt)
        {
            // 压缩后热表首事件 = 锚点（Sequence > 0）；后续 = 最后事件 Sequence + 1。
            var expectedSequence = _events.Count == 0 ? 0 : _events[^1].Sequence + 1;
            if (evt.Sequence != expectedSequence)
            {
                throw new InvalidOperationException(
                    $"事件 Sequence 不连续：run={evt.RunId}。期望={expectedSequence}，实际={evt.Sequence}。");
            }

            var expectedPrevHash = _events.Count == 0 ? null : _events[^1].ContentHash;
            if (!string.Equals(expectedPrevHash, evt.PrevChainHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"事件 PrevChainHash 不匹配：run={evt.RunId}。期望={expectedPrevHash ?? "<null>"}，" +
                    $"实际={evt.PrevChainHash ?? "<null>"}。");
            }
        }
    }

    /// <summary>
    /// 模拟事件流压缩器（测试用）：持有归档事件 + 快照；GetSnapshotAsync / GetArchivedEventsAsync
    /// 返回种子数据；CompactAsync / FindCandidatesAsync 不用于恢复路径。
    /// </summary>
    private sealed class FakeAgentRunEventCompactor : IAgentRunEventCompactor
    {
        private readonly AgentRunEventSnapshot? _snapshot;

        public FakeAgentRunEventCompactor(IReadOnlyList<AgentRunEvent> archived, AgentRunEventSnapshot? snapshot)
        {
            Archived = archived;
            _snapshot = snapshot;
        }

        /// <summary>折叠归档事件（审计保留；供断言完整链）。</summary>
        public IReadOnlyList<AgentRunEvent> Archived { get; }

        public Task<AgentRunCompactionResult> CompactAsync(
            string workspaceId, string runId, int upToSequence, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("测试夹具不模拟压缩写入。");

        public ValueTask<AgentRunEventSnapshot?> GetSnapshotAsync(
            string workspaceId, string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_snapshot);

        public ValueTask<IReadOnlyList<AgentRunEvent>> GetArchivedEventsAsync(
            string workspaceId,
            string runId,
            int fromSequence = 0,
            int take = 1000,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentRunEvent>>(
                Archived.Where(e => e.Sequence >= fromSequence).Take(take).ToList());

        public Task<IReadOnlyList<AgentRunCompactionCandidate>> FindCandidatesAsync(
            int minEventCount, int limit, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("测试夹具不模拟候选扫描。");
    }
}
