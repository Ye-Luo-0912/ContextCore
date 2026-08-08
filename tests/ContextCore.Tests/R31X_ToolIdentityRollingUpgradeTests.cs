using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// Tool 调用身份滚动升级 Kill Matrix
//
// 背景：RequestId 派生算法从 V1（runId + modelTurn + toolCallId + toolName +
// arguments，无工作区）升级为 V2（加入 workspaceId）。v69 迁移只改数据库
// 主键与索引，不重算历史 RequestId；恢复路径必须沿用事件中已持久化的
// RequestId（绝不重新计算），否则历史 Run 查不到既有 journal 条目，
// 被视为新调用而重复执行外部副作用（exactly-once 破坏）。
//
// 本文件验证修复后的恢复语义：
// - 恢复优先使用持久化 RequestId（requestIdOverride），journal 命中既有条目；
// - V1 各崩溃点（Prepare 后 / Intent 后 / Provider 请求后 / Dispatched 后 /
//   Committed 前）在升级到 V2 后恢复，provider 副作用执行次数均 <= 1；
// - 事件重建（ToolCallStarted / ApprovalRequested payload）保留持久化 RequestId。
// ===========================================================================

[TestClass]
[TestCategory("Kill-Point")]
[TestCategory("Rolling-Upgrade")]
public sealed class R31X_ToolIdentityRollingUpgradeTests
{
    private const string Ws = "ws-rollup";
    private const string RunId = "run-rollup";

    /// <summary>测试 Run 复合身份键（与 Ws/RunId 常量一致）。</summary>
    private static readonly TenantRunKey Key = new(Ws, RunId);

    // ── V1 旧算法（滚动升级前的 RequestId 派生）──────────────────────────

    /// <summary>V1 算法：runId + modelTurn + toolCallId + toolName + arguments（无 workspaceId）。</summary>
    private static string ComputeV1RequestId(string runId, AgentToolCallRequest toolCall, int modelTurn)
    {
        var raw = $"{runId}|{modelTurn}|{toolCall.ToolCallId ?? string.Empty}|{toolCall.ToolName}|{toolCall.Arguments ?? string.Empty}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    // ── 对照：算法漂移本身存在 ───────────────────────────────────────────

    /// <summary>
    /// 验证：V1 与 V2 算法对同一调用产出不同 RequestId——
    /// 证明滚动升级后若重新计算 identity，历史 journal 必然 miss（这就是要防的坑）。
    /// </summary>
    [TestMethod]
    public void V1AndV2Algorithms_ProduceDifferentRequestIds()
    {
        var toolCall = BuildToolCall("weather", "arg-matrix");
        var v1 = ComputeV1RequestId(RunId, toolCall, 0);
        var v2 = DefaultDurableToolExecutor.ComputeRequestId(Ws, RunId, toolCall, 0);

        Assert.AreNotEqual(v1, v2, "V1 与 V2 派生算法必须产出不同 RequestId（工作区参与哈希）。");
    }

    // ── Kill 点 1：V1 Prepare 后 Kill（外部调用从未开始）──────────────────

    /// <summary>
    /// 验证：V1 崩溃残留 journal=Prepared（外部副作用从未开始）→ 升级 V2 后恢复，
    /// 以持久化 RequestId 寻址命中条目，安全重放恰好执行一次。
    /// </summary>
    [TestMethod]
    public async Task V1KillPoint_Prepared_ReplaysExactlyOnce()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-kp1");
        var v1RequestId = ComputeV1RequestId(RunId, toolCall, 0);

        // 旧版本崩溃残留：V1 算法写入的 Prepared 条目。
        await journal.PrepareAsync(BuildV1JournalEntry(v1RequestId, ToolDispatchState.Prepared, "arg-kp1"), cts.Token);

        // 升级后恢复：使用持久化 RequestId（不重新计算 V2），journal 命中 → 推进并 Dispatch。
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token, requestIdOverride: v1RequestId);

        Assert.IsTrue(result.Succeeded, "Prepared 崩溃点恢复后应成功执行。");
        Assert.AreEqual(ToolDispatchState.Committed, result.JournalState);
        Assert.AreEqual(v1RequestId, result.RequestId, "恢复结果应保留持久化的 V1 RequestId。");
        Assert.AreEqual(1, handler.InvocationCount, "Prepare 后 kill point：外部副作用应恰好执行一次。");
    }

    // ── Kill 点 2：V1 Intent 后 Kill（外部调用可能已开始）─────────────────

    /// <summary>
    /// 验证：V1 崩溃残留 journal=DispatchingIntent → 升级 V2 后恢复，
    /// 以持久化 RequestId 命中 → 识别为模糊态返回对账结果，不触碰外部 handler。
    /// </summary>
    [TestMethod]
    public async Task V1KillPoint_DispatchingIntent_NoSilentRerun()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-kp2");
        var v1RequestId = ComputeV1RequestId(RunId, toolCall, 0);

        // 旧版本崩溃残留：Intent 已持久化、Dispatch 之前。
        await journal.PrepareWithIntentAsync(BuildV1JournalEntry(v1RequestId, ToolDispatchState.Prepared, "arg-kp2"), cts.Token);

        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token, requestIdOverride: v1RequestId);

        Assert.IsFalse(result.Succeeded, "DispatchingIntent 模糊态应返回对账失败结果。");
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, result.JournalState,
            "对账结果必须回传真实 Journal 状态（不伪造）。");
        Assert.AreEqual(0, handler.InvocationCount, "DispatchingIntent kill point：不得静默调用外部副作用。");
    }

    // ── Kill 点 3：V1 Provider Request 后 Kill（Dispatch 抛异常）───────────

    /// <summary>
    /// 验证：V1 崩溃残留（Dispatch 抛异常，journal=DispatchingIntent）→ 升级 V2 后恢复，
    /// 以持久化 RequestId 命中 → 返回真实 Journal 状态强制对账，不重试外部副作用。
    /// </summary>
    [TestMethod]
    public async Task V1KillPoint_ProviderRequestThrows_NoSilentRerun()
    {
        var descriptor = Descriptor("charge", ToolSideEffect.Write, maxRetries: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatcher = new ThrowingToolDispatcher(descriptor);
        var journal = new InMemoryToolDispatchJournal();
        var executor = new DefaultDurableToolExecutor(dispatcher, journal);

        var toolCall = BuildToolCall("charge", "arg-kp3");
        var v1RequestId = ComputeV1RequestId(RunId, toolCall, 0);

        // 旧版本崩溃残留：Intent 已持久化、Provider 调用抛异常。
        await journal.PrepareWithIntentAsync(BuildV1JournalEntry(v1RequestId, ToolDispatchState.Prepared, "arg-kp3", "charge"), cts.Token);

        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token, requestIdOverride: v1RequestId);

        Assert.IsFalse(result.Succeeded, "Provider 调用异常 → 失败结果。");
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, result.JournalState,
            "P0-1：异常路径必须返回真实 Journal 状态（DispatchingIntent），不得伪造 Prepared。");
        Assert.AreEqual(ToolFailurePhase.ProviderCallAmbiguous, result.FailurePhase);
    }

    // ── Kill 点 4：V1 Dispatched 后 Kill（外部已执行、结果未提交）──────────

    /// <summary>
    /// 验证：V1 崩溃残留 journal=Dispatched（外部副作用已执行 1 次）→ 升级 V2 后恢复，
    /// 以持久化 RequestId 命中 → 返回对账结果，不重跑（调用计数保持 1）。
    /// </summary>
    [TestMethod]
    public async Task V1KillPoint_Dispatched_NoSilentRerun()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (dispatcher, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-kp4");
        var v1RequestId = ComputeV1RequestId(RunId, toolCall, 0);

        // 旧版本完整 Dispatch 已发生（外部副作用 1 次），崩溃于 MarkCommitted 之前。
        await journal.PrepareWithIntentAsync(BuildV1JournalEntry(v1RequestId, ToolDispatchState.Prepared, "arg-kp4"), cts.Token);
        await dispatcher.DispatchAsync(new ToolDispatchRequest
        {
            ToolName = "weather",
            Payload = "arg-kp4",
            RequestId = v1RequestId,
            WorkspaceId = Ws,
            RunId = RunId
        }, cts.Token);
        await journal.MarkDispatchedAsync(Key, v1RequestId, "ext-op-1", cts.Token);
        Assert.AreEqual(1, handler.InvocationCount, "前置：外部副作用已执行 1 次（崩溃窗口内）。");

        // 升级后恢复：以持久化 RequestId 命中 Dispatched 条目 → 对账，不重跑。
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token, requestIdOverride: v1RequestId);

        Assert.IsFalse(result.Succeeded, "Dispatched 模糊态应返回对账失败结果。");
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState);
        Assert.AreEqual(1, handler.InvocationCount, "Dispatched kill point：外部副作用不得重复执行。");
        Assert.AreEqual("ext-op-1", result.ExternalOperationId, "对账结果应携带既有外部操作 ID。");
    }

    // ── Kill 点 5：V1 Committed 前 Kill（结果已提交，送达标记前）──────────

    /// <summary>
    /// 验证：V1 已 Committed（结果已持久化）→ 升级 V2 后恢复，
    /// 以持久化 RequestId 命中 → 返回缓存结果，不重跑外部副作用。
    /// </summary>
    [TestMethod]
    public async Task V1KillPoint_Committed_ReturnsCachedResult()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, _, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-kp5");
        var v1RequestId = ComputeV1RequestId(RunId, toolCall, 0);

        // 旧版本第一次完整执行（V1 身份）→ Committed。
        var first = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token, requestIdOverride: v1RequestId);
        Assert.IsTrue(first.Succeeded);
        Assert.AreEqual(v1RequestId, first.RequestId);
        Assert.AreEqual(1, handler.InvocationCount);

        // 升级后崩溃恢复重跑 → 持久化 RequestId 命中缓存结果，禁止重新 Dispatch。
        var second = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token, requestIdOverride: v1RequestId);
        Assert.IsTrue(second.Succeeded, "Committed 后应返回缓存结果。");
        Assert.AreEqual(ToolDispatchState.Committed, second.JournalState);
        Assert.AreEqual(1, handler.InvocationCount, "Committed kill point：外部副作用不得重复执行。");
    }

    // ── 联合矩阵：全部 V1 kill point 恢复后副作用 <= 1 ────────────────────

    /// <summary>
    /// 验证：五个 V1 崩溃点升级 V2 后恢复，provider side effect 计数全部 <= 1
    /// （Prepared=1 次安全重放，其余=0 或保持 1，绝不超 1）。
    /// </summary>
    [TestMethod]
    public async Task V1KillMatrix_AllPoints_SideEffectCountNeverExceedsOne()
    {
        var matrix = new (string Args, int ExpectedInvocationCount)[]
        {
            ("arg-m1", 1), // Prepared：安全重放恰好 1 次
            ("arg-m2", 0), // DispatchingIntent：对账不执行
            ("arg-m3", 1), // Dispatched：保持 1 次（不重跑）
            ("arg-m4", 1)  // Committed：缓存结果（不重跑）
        };

        foreach (var (args, expectedCount) in matrix)
        {
            var handler = CreateHandler("weather");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (dispatcher, executor, journal, _) = CreateExecutor(handler);

            var toolCall = BuildToolCall("weather", args);
            var v1RequestId = ComputeV1RequestId(RunId, toolCall, 0);

            if (args == "arg-m1")
            {
                // Prepared 残留：外部从未开始。
                await journal.PrepareAsync(BuildV1JournalEntry(v1RequestId, ToolDispatchState.Prepared, args), cts.Token);
            }
            else if (args is "arg-m2")
            {
                // DispatchingIntent 残留：外部可能已开始。
                await journal.PrepareWithIntentAsync(BuildV1JournalEntry(v1RequestId, ToolDispatchState.Prepared, args), cts.Token);
            }
            else if (args is "arg-m3")
            {
                // Dispatched 残留：外部已执行 1 次。
                await journal.PrepareWithIntentAsync(BuildV1JournalEntry(v1RequestId, ToolDispatchState.Prepared, args), cts.Token);
                await dispatcher.DispatchAsync(new ToolDispatchRequest
                {
                    ToolName = "weather",
                    Payload = args,
                    RequestId = v1RequestId,
                    WorkspaceId = Ws,
                    RunId = RunId
                }, cts.Token);
                await journal.MarkDispatchedAsync(Key, v1RequestId, "ext-m3", cts.Token);
            }
            else
            {
                // Committed 残留：结果已持久化。
                await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token, requestIdOverride: v1RequestId);
            }

            var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token, requestIdOverride: v1RequestId);
            Assert.IsTrue(handler.InvocationCount <= 1,
                $"{args}：升级恢复后外部副作用执行次数必须 <= 1，实际 {handler.InvocationCount}。");
            Assert.AreEqual(v1RequestId, result.RequestId,
                $"{args}：恢复结果必须保留持久化的 V1 RequestId（不得重算为 V2）。");
        }
    }

    // ── 事件重建：持久化 RequestId 必须被带回 ─────────────────────────────

    /// <summary>
    /// 验证：V1 时代写入的 ToolCallStarted 事件（camelCase payload）经重建后
    /// PendingToolCommand.RequestId 保留 V1 值——恢复节点无需重新计算 identity。
    /// </summary>
    [TestMethod]
    public void RebuildPendingCommand_FromV1ToolCallStarted_PreservesRequestId()
    {
        var toolCall = BuildToolCall("weather", "arg-event");
        var v1RequestId = ComputeV1RequestId(RunId, toolCall, 0);

        var events = new[]
        {
            new AgentRunEvent
            {
                EventId = "evt-v1-toolcall-started",
                RunId = RunId,
                WorkspaceId = Ws,
                Sequence = 1,
                EventType = AgentRunEventType.ToolCallStarted,
                State = AgentRunState.ToolDispatching,
                OccurredAt = DateTimeOffset.UtcNow,
                Payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    toolName = toolCall.ToolName,
                    toolCallId = toolCall.ToolCallId,
                    requestId = v1RequestId,
                    idempotencyKey = toolCall.IdempotencyKey,
                    arguments = toolCall.Arguments,
                    modelTurnRevision = 0
                })
            }
        };

        var pending = AgentRunEventStateRebuilder.ExtractPendingCommandsFromToolCallStarted(events);
        Assert.IsNotNull(pending, "ToolCallStarted 事件应重建出 PendingToolCommand。");
        Assert.AreEqual(v1RequestId, pending![0].RequestId,
            "重建命令必须保留事件中持久化的 RequestId（滚动升级后不得重新计算）。");
    }

    /// <summary>
    /// 验证：V1 时代写入的 ApprovalRequested payload（PascalCase PendingToolCommand）
    /// 解析后 RequestId 保留 V1 值——审批恢复路径同样使用持久化 identity。
    /// </summary>
    [TestMethod]
    public void ParsePendingToolCommand_FromV1ApprovalPayload_PreservesRequestId()
    {
        var toolCall = BuildToolCall("weather", "arg-approval");
        var v1RequestId = ComputeV1RequestId(RunId, toolCall, 0);

        var pending = new PendingToolCommand
        {
            ToolCallId = toolCall.ToolCallId!,
            ToolName = toolCall.ToolName,
            ArgumentsJson = toolCall.Arguments,
            IdempotencyKey = toolCall.IdempotencyKey,
            ModelTurnRevision = 1,
            RequestId = v1RequestId
        };

        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(pending));
        var parsed = AgentRunEventStateRebuilder.ParsePendingToolCommand(doc.RootElement);
        Assert.IsNotNull(parsed);
        Assert.AreEqual(v1RequestId, parsed!.RequestId,
            "审批 payload 解析必须保留持久化 RequestId。");
        Assert.AreEqual(toolCall.ToolCallId, parsed.ToolCallId);
        Assert.AreEqual(1, parsed.ModelTurnRevision);
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static RecordingToolHandler CreateHandler(
        string toolName,
        ToolDescriptor? descriptor = null) => new(
            toolName,
            descriptor ?? new ToolDescriptor
            {
                Name = toolName,
                DeclaredSideEffect = ToolSideEffect.None,
                RecoveryStrategy = ToolRecoveryStrategy.SafeReplay
            });

    private static (RealToolDispatcher Dispatcher, DefaultDurableToolExecutor Executor, InMemoryToolDispatchJournal Journal, InMemoryDurableToolResultStore ResultStore) CreateExecutor(
        RecordingToolHandler handler)
    {
        var dispatcher = new RealToolDispatcher(new[] { handler });
        var journal = new InMemoryToolDispatchJournal();
        var resultStore = new InMemoryDurableToolResultStore();
        var executor = new DefaultDurableToolExecutor(dispatcher, journal, resultStore);
        return (dispatcher, executor, journal, resultStore);
    }

    private static AgentToolCallRequest BuildToolCall(string toolName, string args) => new()
    {
        ToolName = toolName,
        Arguments = args,
        IdempotencyKey = "idem-rollup",
        ToolCallId = $"toolcall-{toolName}-0"
    };

    private static ToolDescriptor Descriptor(
        string name,
        ToolSideEffect sideEffect,
        int maxRetries,
        ToolRetrySafety retrySafety = ToolRetrySafety.Never) => new()
    {
        Name = name,
        DeclaredSideEffect = sideEffect,
        MaxRetries = maxRetries,
        RetryBackoffPolicy = maxRetries > 0 ? ToolRetryBackoffPolicy.Linear : ToolRetryBackoffPolicy.None,
        RetryDelay = TimeSpan.FromMilliseconds(1),
        RetrySafety = retrySafety
    };

    private static ToolDispatchJournalEntry BuildV1JournalEntry(
        string requestId,
        ToolDispatchState state,
        string args,
        string toolName = "weather") => new()
        {
            RequestId = requestId,
            ToolName = toolName,
            State = state,
            IdempotencyKey = "idem-rollup",
            PayloadDigest = ToolDispatchJournalEntry.ComputePayloadDigest(args),
            WorkspaceId = Ws,
            RunId = RunId,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    /// <summary>录制 Tool Handler：记录调用次数与最近一次上下文。</summary>
    private sealed class RecordingToolHandler : IToolHandler
    {
        private int _invocationCount;

        public RecordingToolHandler(string toolName, ToolDescriptor descriptor)
        {
            ToolName = toolName;
            Descriptor = descriptor;
        }

        public string ToolName { get; }
        public ToolDescriptor Descriptor { get; }
        public string? Description => $"Test tool: {ToolName}";
        public string? ParametersJsonSchema => "{}";
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult(new ToolHandlerResult
            {
                Succeeded = true,
                Result = "ok",
                SideEffect = ToolSideEffect.None
            });
        }
    }

    /// <summary>DispatchAsync 恒抛异常的 Dispatcher（模拟 Dispatcher 级故障 / Provider 调用异常）。</summary>
    private sealed class ThrowingToolDispatcher : IToolDispatcher
    {
        public ThrowingToolDispatcher(ToolDescriptor descriptor) => DescriptorValue = descriptor;

        private ToolDescriptor DescriptorValue { get; }

        public IReadOnlySet<string> SupportedTools { get; } = new HashSet<string> { "charge" };

        public ToolDescriptor? GetDescriptor(string toolName)
            => toolName == "charge" ? DescriptorValue : null;

        public ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated-dispatch-exception");
    }
}
