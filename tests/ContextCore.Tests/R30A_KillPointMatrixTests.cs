using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// Kill Point 矩阵 —— Tool Effect Truth 补齐
//
// 硬验收："在任意 Kill Point 后都不会重复执行，也不会伪装 Completed"。
// 既有 Kill Point 外部效应测试已覆盖 Prepared / DispatchingIntent /
// Dispatched / Committed / 对账流；本文件补齐矩阵缺口：
// - Prepare 前（journal 无任何记录 → 全新启动，恰好执行一次）；
// - 外部 Effect 前（Intent 已持久化、副作用未发起 → 不执行、不伪装完成）；
// - Dispatched 前（Dispatch 已返回、MarkDispatched 未持久化 → 不重跑）；
// - ResultDelivered 前（AsyncDurable：Committed 后、送达标记前 → 返回缓存、不重跑）；
// - 联合矩阵：所有模糊态（DispatchingIntent / Dispatched / Reconciling）
// 恢复结果必须 Succeeded=false 且 JournalState != Committed（不伪装 Completed）。
// ===========================================================================

[TestClass]
[TestCategory("Kill-Point")]
[TestCategory("External-Effect-Truth")]
public sealed class R30A_KillPointMatrixTests
{
    private const string Ws = "ws-r30a";
    private const string RunId = "run-r30a";

    // ── Kill Point：Prepare 前（journal 无记录，全新启动）────────────────────

    /// <summary>
    /// 验证：崩溃于 journal 条目写入之前（无任何记录）→ 全新启动路径，
    /// 安全执行恰好一次外部副作用（无记录 = 外部从未被调用）。
    /// </summary>
    [TestMethod]
    public async Task KillPoint_NoJournalEntry_BeforePrepare_ExecutesExactlyOnce()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-prepare-before");

        // 崩溃于 Prepare 前：journal 无任何条目。
        var before = await journal.GetEntryAsync(
            DefaultDurableToolExecutor.ComputeRequestId(RunId, toolCall, 0), cts.Token);
        Assert.IsNull(before, "前置：journal 应无任何记录（崩溃于 Prepare 前）。");

        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsTrue(result.Succeeded, "Prepare 前崩溃点：全新启动应成功执行。");
        Assert.AreEqual(ToolDispatchState.Committed, result.JournalState);
        Assert.AreEqual(1, handler.InvocationCount, "Prepare 前 kill point：外部副作用应恰好执行一次。");
    }

    // ── Kill Point：外部 Effect 前（Intent 已持久化、副作用未发起）───────────

    /// <summary>
    /// 验证：崩溃于 Intent 提交之后、外部副作用发起之前（journal=DispatchingIntent）→
    /// 恢复不得执行外部副作用（调用计数 0），且不得伪装 Completed（Succeeded=false）。
    /// </summary>
    [TestMethod]
    public async Task KillPoint_ExternalEffectBefore_IntentPersisted_NoRerun_NoFakeCompleted()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-effect-before");
        var requestId = DefaultDurableToolExecutor.ComputeRequestId(RunId, toolCall, 0);

        // 崩溃于 Intent 持久化之后、Dispatch（外部副作用）之前。
        await journal.PrepareWithIntentAsync(
            BuildJournalEntry(requestId, ToolDispatchState.Prepared, ToolDispatchJournalEntry.ComputePayloadDigest("arg-effect-before")), cts.Token);

        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsFalse(result.Succeeded, "外部 Effect 前 kill point：不得伪装 Completed（Succeeded=false）。");
        Assert.AreNotEqual(ToolDispatchState.Committed, result.JournalState, "模糊态不得进入 Committed。");
        Assert.AreEqual(0, handler.InvocationCount, "外部 Effect 前 kill point：外部副作用从未发起，不得调用。");
    }

    // ── Kill Point：Dispatched 前（Dispatch 已返回、MarkDispatched 未持久化）─

    /// <summary>
    /// 验证：崩溃于 Dispatch 返回之后、MarkDispatchedAsync 持久化之前
    /// （journal 仍为 DispatchingIntent，但外部副作用已执行一次）→
    /// 恢复不重跑（调用计数保持 1），返回对账结果。
    /// </summary>
    [TestMethod]
    public async Task KillPoint_DispatchedBefore_MarkDispatchedNotPersisted_NoRerun()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (dispatcher, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-dispatched-before");
        var requestId = DefaultDurableToolExecutor.ComputeRequestId(RunId, toolCall, 0);

        // 模拟完整 Dispatch 已发生（外部副作用执行 1 次），但崩溃于 MarkDispatchedAsync 持久化之前。
        await journal.PrepareWithIntentAsync(
            BuildJournalEntry(requestId, ToolDispatchState.Prepared, ToolDispatchJournalEntry.ComputePayloadDigest("arg-dispatched-before")), cts.Token);
        await dispatcher.DispatchAsync(new ToolDispatchRequest
        {
            ToolName = "weather",
            Payload = "arg-dispatched-before",
            RequestId = requestId,
            WorkspaceId = Ws,
            RunId = RunId
        }, cts.Token);
        // 注意：不调用 MarkDispatchedAsync —— 模拟崩溃于持久化前。
        Assert.AreEqual(1, handler.InvocationCount, "前置：外部副作用已执行 1 次（崩溃窗口内）。");

        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsFalse(result.Succeeded, "Dispatched 前 kill point：不得伪装 Completed。");
        Assert.AreEqual(1, handler.InvocationCount, "Dispatched 前 kill point：外部副作用不得重复执行。");
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, result.JournalState,
            "P0-1：对账结果回传真实 Journal 状态（journal 仍为 DispatchingIntent）。");
    }

    // ── Kill Point：ResultDelivered 前（AsyncDurable：Committed 后、送达标记前）─

    /// <summary>
    /// 验证：AsyncDurable 工具崩溃于 MarkCommitted 之后、MarkResultDelivered 之前
    /// （journal=Committed）→ 恢复直接返回已提交缓存结果，不重跑外部副作用，
    /// 且结果仍标记为成功（结果已持久化，可安全重发）。
    /// </summary>
    [TestMethod]
    public async Task KillPoint_ResultDeliveredBefore_CommittedNotDelivered_ReturnsCached()
    {
        var handler = CreateHandler("email", descriptor: new ToolDescriptor
        {
            Name = "email",
            DeclaredSideEffect = ToolSideEffect.Write,
            DeliveryMode = ToolDeliveryMode.AsyncDurable,
            RecoveryStrategy = ToolRecoveryStrategy.UseCachedResult
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (dispatcher, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("email", "arg-delivered-before");
        var requestId = DefaultDurableToolExecutor.ComputeRequestId(RunId, toolCall, 0);

        // 模拟崩溃于 Committed 之后、ResultDelivered 之前：
        // 完整执行 Dispatch（外部副作用 1 次）+ MarkDispatched + MarkCommittedWithResult，
        // 但跳过 MarkResultDelivered —— 等价于 AsyncDurable 崩溃窗口残留。
        await journal.PrepareWithIntentAsync(
            BuildJournalEntry(requestId, ToolDispatchState.Prepared, ToolDispatchJournalEntry.ComputePayloadDigest("arg-delivered-before"), "email"), cts.Token);
        await dispatcher.DispatchAsync(new ToolDispatchRequest
        {
            ToolName = "email",
            Payload = "arg-delivered-before",
            RequestId = requestId,
            WorkspaceId = Ws,
            RunId = RunId
        }, cts.Token);
        await journal.MarkDispatchedAsync(requestId, "ext-delivered", cts.Token);
        await journal.MarkCommittedWithResultAsync(requestId, new DurableToolResult
        {
            ToolCallId = "toolcall-email-0",
            RequestId = requestId,
            WorkspaceId = Ws,
            RunId = RunId,
            InvocationId = requestId,
            SideEffect = ToolSideEffect.Write,
            ExternalOperationId = "ext-delivered",
            Result = "sent",
            Succeeded = true,
            DurationMs = 1.0
        }, cts.Token);
        Assert.AreEqual(1, handler.InvocationCount, "前置：外部副作用已执行 1 次（崩溃窗口内）。");
        Assert.AreEqual(ToolDispatchState.Committed, (await journal.GetEntryAsync(requestId, cts.Token))!.State,
            "前置：journal 应停留在 Committed（送达标记前崩溃残留）。");

        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsTrue(result.Succeeded, "ResultDelivered 前 kill point：已提交结果应安全重发（Succeeded=true）。");
        Assert.AreEqual(ToolDispatchState.Committed, result.JournalState);
        Assert.AreEqual("sent", result.Result, "应返回已提交的缓存结果。");
        Assert.AreEqual(1, handler.InvocationCount, "ResultDelivered 前 kill point：外部副作用不得重复执行。");
    }

    // ── 联合矩阵：模糊态恢复结果不得伪装 Completed ───────────────────────────

    /// <summary>
    /// 验证矩阵验收："任意 Kill Point 后不伪装 Completed"——对所有模糊状态
    /// （DispatchingIntent / Dispatched / Reconciling）逐一断言恢复结果
    /// Succeeded=false 且 JournalState != Committed / ResultDelivered。
    /// </summary>
    [TestMethod]
    public async Task KillPoint_Matrix_AmbiguousStates_NeverFakeCompleted()
    {
        // 每个模糊态：先构造对应的 journal 残留，再执行恢复，断言不伪装完成。
        var matrix = new (ToolDispatchState State, string Args, string Desc)[]
        {
            (ToolDispatchState.DispatchingIntent, "arg-amb-1", "DispatchingIntent（外部 Effect 前）"),
            (ToolDispatchState.Dispatched, "arg-amb-2", "Dispatched（外部 Effect 后、提交前）"),
            (ToolDispatchState.Reconciling, "arg-amb-3", "Reconciling（对账进行中）")
        };

        foreach (var (state, args, desc) in matrix)
        {
            var handler = CreateHandler("weather");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (dispatcher, executor, journal, _) = CreateExecutor(handler);

            var toolCall = BuildToolCall("weather", args);
            var requestId = DefaultDurableToolExecutor.ComputeRequestId(RunId, toolCall, 0);

            await journal.PrepareWithIntentAsync(
                BuildJournalEntry(requestId, ToolDispatchState.Prepared, ToolDispatchJournalEntry.ComputePayloadDigest(args)), cts.Token);
            if (state is ToolDispatchState.Dispatched or ToolDispatchState.Reconciling)
            {
                await dispatcher.DispatchAsync(new ToolDispatchRequest
                {
                    ToolName = "weather",
                    Payload = args,
                    RequestId = requestId,
                    WorkspaceId = Ws,
                    RunId = RunId
                }, cts.Token);
                await journal.MarkDispatchedAsync(requestId, "ext-matrix", cts.Token);
                if (state == ToolDispatchState.Reconciling)
                {
                    await journal.BeginReconciliationAsync(requestId, cts.Token);
                }
            }

            var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

            Assert.IsFalse(result.Succeeded, $"{desc}：不得伪装 Completed（Succeeded 必须为 false）。");
            Assert.AreNotEqual(ToolDispatchState.Committed, result.JournalState, $"{desc}：journal 不得进入 Committed。");
            Assert.AreNotEqual(ToolDispatchState.ResultDelivered, result.JournalState, $"{desc}：journal 不得进入 ResultDelivered。");
        }
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static RecordingToolHandler CreateHandler(string toolName, ToolDescriptor? descriptor = null) => new(
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
        IdempotencyKey = "idem-r30a",
        ToolCallId = $"toolcall-{toolName}-0"
    };

    private static ToolDispatchJournalEntry BuildJournalEntry(
        string requestId,
        ToolDispatchState state,
        string payloadDigest,
        string toolName = "weather") => new()
        {
            RequestId = requestId,
            ToolName = toolName,
            State = state,
            IdempotencyKey = "idem-r30a",
            PayloadDigest = payloadDigest,
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
}
