using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// Kill Point 外部副作用 Truth 测试
//
// 验证硬验收标准："外部 Tool 在所有 Kill Point 下不会静默重复执行"。
// 每个 Kill Point 模拟崩溃后重启（journal 停留在对应状态），
// 用真实 RecordingToolHandler 的调用计数断言外部副作用未被静默重放：
//   - Prepared（外部调用从未开始）→ 安全重放，恰好执行一次
//   - DispatchingIntent（外部调用可能已开始）→ 对账，不执行（调用计数 0）
//   - Dispatched（外部调用已执行、结果未提交）→ 对账，不重跑（调用计数保持 1）
//   - Committed（结果已持久化）→ 返回缓存结果，不重跑（调用计数保持 1）
// 另覆盖执行器 Truth 行为：ExternalOperationId 生成/下发/覆盖、
// RequiresIdempotencyKey 兜底、RequiresLeaseFence fail-closed、声明副作用权威。
// ===========================================================================

[TestClass]
[TestCategory("Kill-Point")]
[TestCategory("External-Effect-Truth")]
public sealed class R29H_KillPointExternalEffectTests
{
    private const string Ws = "ws-killpoint";
    private const string RunId = "run-killpoint";

    // ── Kill Point 1：崩溃于 Prepared（外部调用从未开始）──────────────────────

    /// <summary>
    /// 验证：journal 停在 Prepared（旧两步流程崩溃残留）时，恢复重跑恰好执行一次外部副作用。
    /// Prepared 表示外部调用从未开始，重放安全。
    /// </summary>
    [TestMethod]
    public async Task KillPoint_Prepared_ReplaysExactlyOnce()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-A");
        var requestId = DefaultDurableToolExecutor.ComputeRequestId(RunId, toolCall, 0);

        // 模拟崩溃在两步流程的 Prepare 之后（旧残留：Prepared 条目）
        await journal.PrepareAsync(
            BuildJournalEntry(requestId, ToolDispatchState.Prepared, ToolDispatchJournalEntry.ComputePayloadDigest("arg-A")), cts.Token);

        // 恢复重跑：Prepared 前驱应被原子推进到 DispatchingIntent 并继续 Dispatch
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsTrue(result.Succeeded, "Prepared 崩溃点恢复后应成功执行。");
        Assert.AreEqual(ToolDispatchState.Committed, result.JournalState);
        Assert.AreEqual(1, handler.InvocationCount, "Prepared kill point：外部副作用应恰好执行一次。");

        var entry = await journal.GetEntryAsync(requestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State, "journal 应推进到 Committed。");
    }

    // ── Kill Point 2：崩溃于 DispatchingIntent（外部调用可能已开始）───────────

    /// <summary>
    /// 验证：journal 停在 DispatchingIntent（崩溃于 Dispatch 之前）时，
    /// 恢复不静默重放——外部调用可能已开始但未完成，返回对账结果（调用计数 0）。
    /// </summary>
    [TestMethod]
    public async Task KillPoint_DispatchingIntent_NoSilentRerun()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-B");
        var requestId = DefaultDurableToolExecutor.ComputeRequestId(RunId, toolCall, 0);

        // 模拟崩溃在 PrepareWithIntentAsync 之后、Dispatch 之前
        await journal.PrepareWithIntentAsync(
            BuildJournalEntry(requestId, ToolDispatchState.Prepared, ToolDispatchJournalEntry.ComputePayloadDigest("arg-B")), cts.Token);

        // 恢复重跑：应识别为模糊状态，返回对账结果，不触碰外部 handler
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsFalse(result.Succeeded, "DispatchingIntent 模糊态应返回对账失败结果。");
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState, "对账结果应标记为模糊 Dispatched 状态。");
        Assert.AreEqual(0, handler.InvocationCount, "DispatchingIntent kill point：不得静默调用外部副作用。");

        var entry = await journal.GetEntryAsync(requestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, entry!.State, "journal 状态应保持 DispatchingIntent 等待对账。");
    }

    // ── Kill Point 3：崩溃于 Dispatched（外部调用已执行、结果未提交）──────────

    /// <summary>
    /// 验证：journal 停在 Dispatched（外部调用已执行但结果未提交）时，
    /// 恢复不重跑——外部副作用可能已发生，调用计数保持 1（只执行过一次）。
    /// </summary>
    [TestMethod]
    public async Task KillPoint_Dispatched_NoSilentRerun()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (dispatcher, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-C");
        var requestId = DefaultDurableToolExecutor.ComputeRequestId(RunId, toolCall, 0);

        // 模拟完整 Dispatch 已发生（外部副作用执行 1 次）但进程崩溃于 MarkCommitted 之前
        await journal.PrepareWithIntentAsync(
            BuildJournalEntry(requestId, ToolDispatchState.Prepared, ToolDispatchJournalEntry.ComputePayloadDigest("arg-C")), cts.Token);
        await dispatcher.DispatchAsync(new ToolDispatchRequest
        {
            ToolName = "weather",
            Payload = "arg-C",
            RequestId = requestId,
            WorkspaceId = Ws,
            RunId = RunId
        }, cts.Token);
        await journal.MarkDispatchedAsync(requestId, "ext-op-1", cts.Token);
        Assert.AreEqual(1, handler.InvocationCount, "前置：外部副作用已执行 1 次。");

        // 恢复重跑：应识别为 Dispatched 模糊态，返回对账结果，不重跑外部副作用
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsFalse(result.Succeeded, "Dispatched 模糊态应返回对账失败结果。");
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState);
        Assert.AreEqual(1, handler.InvocationCount, "Dispatched kill point：外部副作用不得重复执行。");
        Assert.AreEqual("ext-op-1", result.ExternalOperationId, "对账结果应携带既有外部操作 ID。");
    }

    // ── Kill Point 4：崩溃于 Committed 之后（结果已持久化）───────────────────

    /// <summary>
    /// 验证：journal 已 Committed 时，恢复直接返回缓存结果，不重跑外部副作用。
    /// </summary>
    [TestMethod]
    public async Task KillPoint_Committed_ReturnsCachedResult()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, _, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-D");

        // 第一次完整执行 → Committed
        var first = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);
        Assert.IsTrue(first.Succeeded);
        Assert.AreEqual(1, handler.InvocationCount);

        // 崩溃恢复后的重跑 → 返回缓存结果，禁止重新 Dispatch
        var second = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);
        Assert.IsTrue(second.Succeeded, "Committed 后应返回缓存结果。");
        Assert.AreEqual(ToolDispatchState.Committed, second.JournalState);
        Assert.AreEqual("ok", second.Result);
        Assert.AreEqual(1, handler.InvocationCount, "Committed kill point：外部副作用不得重复执行。");
    }

    // ── Kill Point 5：Dispatched 残留经显式对账提交最终真相 ──────────────────

    /// <summary>
    /// 验证：Dispatched 残留经 BeginReconciliationAsync + MarkReconciledWithResultAsync
    /// 提交对账真相后，恢复返回缓存结果、不再重跑。
    /// </summary>
    [TestMethod]
    public async Task KillPoint_Dispatched_ReconciliationFlow_CommitsTruth()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (dispatcher, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-E");
        var requestId = DefaultDurableToolExecutor.ComputeRequestId(RunId, toolCall, 0);

        await journal.PrepareWithIntentAsync(
            BuildJournalEntry(requestId, ToolDispatchState.Prepared, ToolDispatchJournalEntry.ComputePayloadDigest("arg-E")), cts.Token);
        await dispatcher.DispatchAsync(new ToolDispatchRequest
        {
            ToolName = "weather",
            Payload = "arg-E",
            RequestId = requestId,
            WorkspaceId = Ws,
            RunId = RunId
        }, cts.Token);
        await journal.MarkDispatchedAsync(requestId, "ext-op-5", cts.Token);

        // 对账：确认外部副作用已发生，提交对账结果（以外部系统查询到的真相为准）
        await journal.BeginReconciliationAsync(requestId, cts.Token);
        await journal.MarkReconciledWithResultAsync(requestId, new DurableToolResult
        {
            ToolCallId = "toolcall-weather-0",
            RequestId = requestId,
            WorkspaceId = Ws,
            RunId = RunId,
            InvocationId = requestId,
            SideEffect = ToolSideEffect.Write,
            ExternalOperationId = "ext-op-5",
            Result = "reconciled-truth",
            Succeeded = true,
            DurationMs = 1.0
        }, cts.Token);

        // 恢复重跑 → 返回对账提交的缓存结果，不重跑
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);
        Assert.IsTrue(result.Succeeded, "对账提交后应返回缓存结果。");
        Assert.AreEqual("reconciled-truth", result.Result);
        Assert.AreEqual(1, handler.InvocationCount, "对账流：外部副作用不得重复执行。");

        var entry = await journal.GetEntryAsync(requestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State, "对账完成后 journal 应处于 Committed。");
    }

    // ── 执行器 Truth：ExternalOperationId 生成 / 下发 / 覆盖 ──────────────────

    /// <summary>
    /// 验证：框架在 Prepare 时生成 ExternalOperationId 并下发给 Tool Handler；
    /// Handler 未返回外部 ID 时结果沿用框架生成值。
    /// </summary>
    [TestMethod]
    public async Task Executor_ExternalOperationId_GeneratedAndDeliveredToHandler()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-F");
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.ExternalOperationId, "框架应生成外部操作 ID。");
        Assert.AreEqual(32, result.ExternalOperationId!.Length, "框架生成的外部操作 ID 应为 32 位 hex（GUID N 格式）。");
        Assert.AreEqual(result.ExternalOperationId, handler.LastContext!.ExternalOperationId, "外部操作 ID 应下发给 Tool Handler。");

        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(result.ExternalOperationId, entry!.ExternalOperationId, "外部操作 ID 应持久化到 journal 条目。");
    }

    /// <summary>
    /// 验证：Handler 返回真实外部系统 ID 时覆盖框架生成值。
    /// </summary>
    [TestMethod]
    public async Task Executor_ExternalOperationId_HandlerOverrideWins()
    {
        var handler = CreateHandler("weather");
        handler.RuntimeExternalOperationId = "ext-custom-123";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-G");
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.AreEqual("ext-custom-123", result.ExternalOperationId, "Handler 返回的外部操作 ID 应优先。");

        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual("ext-custom-123", entry!.ExternalOperationId, "覆盖后的外部操作 ID 应持久化到 journal。");
    }

    // ── 执行器 Truth：RequiresIdempotencyKey 兜底 ─────────────────────────────

    /// <summary>
    /// 验证：声明 RequiresIdempotencyKey 但调用方未提供幂等键时，
    /// 框架以 ExternalOperationId 兜底为幂等键下发给 Handler（重放稳定，provider 侧可去重）。
    /// </summary>
    [TestMethod]
    public async Task Executor_RequiresIdempotencyKey_MissingKey_FrameworkProvidesStableKey()
    {
        var handler = CreateHandler("charge", descriptor: new ToolDescriptor
        {
            Name = "charge",
            DeclaredSideEffect = ToolSideEffect.IdempotentWrite,
            RequiresIdempotencyKey = true,
            RecoveryStrategy = ToolRecoveryStrategy.SafeReplay
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, _, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("charge", "arg-H", idempotencyKey: null);
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsTrue(result.Succeeded, "框架应兜底提供幂等键，调用成功。");
        Assert.IsNotNull(handler.LastContext!.IdempotencyKey, "Handler 应收到幂等键。");
        Assert.AreEqual(handler.LastContext.ExternalOperationId, handler.LastContext.IdempotencyKey,
            "兜底幂等键应等于外部操作 ID（同一次调用稳定唯一）。");
        Assert.AreEqual(handler.LastContext.IdempotencyKey, result.IdempotencyKey);
    }

    // ── 执行器 Truth：RequiresLeaseFence fail-closed ─────────────────────────

    /// <summary>
    /// 验证：声明 RequiresLeaseFence 但调用未携带 LeaseFence → fail-closed，不执行外部副作用。
    /// </summary>
    [TestMethod]
    public async Task Executor_RequiresLeaseFence_NoFence_FailsClosed()
    {
        var handler = CreateHandler("write-file", descriptor: new ToolDescriptor
        {
            Name = "write-file",
            DeclaredSideEffect = ToolSideEffect.FencedWrite,
            RequiresLeaseFence = true,
            RecoveryStrategy = ToolRecoveryStrategy.UseCachedResult
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("write-file", "arg-I");
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsFalse(result.Succeeded, "缺少 LeaseFence 必须 fail-closed。");
        Assert.AreEqual(ToolDispatchState.Prepared, result.JournalState);
        Assert.AreEqual(0, handler.InvocationCount, "fail-closed 时不得触碰外部副作用。");
        StringAssert.Contains(result.Error!, "RequiresLeaseFence");
    }

    /// <summary>
    /// 验证：声明 RequiresLeaseFence 且携带有效 LeaseFence → 正常执行。
    /// </summary>
    [TestMethod]
    public async Task Executor_RequiresLeaseFence_WithValidFence_Executes()
    {
        var handler = CreateHandler("write-file", descriptor: new ToolDescriptor
        {
            Name = "write-file",
            DeclaredSideEffect = ToolSideEffect.FencedWrite,
            RequiresLeaseFence = true,
            RecoveryStrategy = ToolRecoveryStrategy.UseCachedResult
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, _, _) = CreateExecutor(handler);

        var fence = new AgentLeaseFence
        {
            LeaseToken = "token-1",
            FencingToken = 1,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        var toolCall = BuildToolCall("write-file", "arg-J");
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token, fence);

        Assert.IsTrue(result.Succeeded, "携带有效 LeaseFence 应正常执行。");
        Assert.AreEqual(1, handler.InvocationCount);
    }

    // ── 执行器 Truth：声明副作用权威 ──────────────────────────────────────────

    /// <summary>
    /// 验证：Descriptor 声明 Write 而运行时返回 None 时，以声明为准提交（声明权威，运行时仅验证）。
    /// </summary>
    [TestMethod]
    public async Task Executor_DeclaredSideEffect_GovernsCommitDecision()
    {
        var handler = CreateHandler("notify", descriptor: new ToolDescriptor
        {
            Name = "notify",
            DeclaredSideEffect = ToolSideEffect.Write,
            RecoveryStrategy = ToolRecoveryStrategy.UseCachedResult
        });
        handler.RuntimeSideEffect = ToolSideEffect.None; // 运行时未如实申报
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, _, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("notify", "arg-K");
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ToolSideEffect.Write, result.SideEffect, "声明副作用应优先于运行时观测值。");
        Assert.AreEqual(ToolDispatchState.Committed, result.JournalState, "声明为写副作用应走提交路径。");
    }

    // ── 执行器 Truth：Result 主键新路径 ───────────────────────────────────────

    /// <summary>
    /// 验证：提交后结果按 request_id 主键可查（新路径 SaveByRequestIdAsync/GetByRequestIdAsync 生效）。
    /// </summary>
    [TestMethod]
    public async Task Executor_ResultPk_GetByRequestIdReturnsCommittedResult()
    {
        var handler = CreateHandler("weather");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, _, resultStore) = CreateExecutor(handler);

        var toolCall = BuildToolCall("weather", "arg-L");
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);
        Assert.IsTrue(result.Succeeded);

        var stored = await resultStore.GetByRequestIdAsync(result.RequestId, cts.Token);
        Assert.IsNotNull(stored, "结果应按 request_id 主键可查。");
        Assert.AreEqual(result.RequestId, stored!.RequestId);
        Assert.AreEqual(Ws, stored.WorkspaceId, "结果应写入隔离键列（workspace_id）。");
        Assert.AreEqual(RunId, stored.RunId, "结果应写入隔离键列（run_id）。");
        Assert.AreEqual("ok", stored.Result);
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

    private static AgentToolCallRequest BuildToolCall(
        string toolName,
        string args,
        string? idempotencyKey = "idem-killpoint") => new()
        {
            ToolName = toolName,
            Arguments = args,
            IdempotencyKey = idempotencyKey,
            ToolCallId = $"toolcall-{toolName}-0"
        };

    private static ToolDispatchJournalEntry BuildJournalEntry(
        string requestId,
        ToolDispatchState state,
        string payloadDigest) => new()
        {
            RequestId = requestId,
            ToolName = "weather",
            State = state,
            IdempotencyKey = "idem-killpoint",
            PayloadDigest = payloadDigest,
            WorkspaceId = Ws,
            RunId = RunId,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    /// <summary>可配置的录制 Tool Handler：记录调用次数与最近一次上下文。</summary>
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
        public ToolExecutionContext? LastContext { get; private set; }
        public ToolSideEffect RuntimeSideEffect { get; set; } = ToolSideEffect.None;
        public string? RuntimeExternalOperationId { get; set; }
        public string ResultContent { get; set; } = "ok";

        public ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            LastContext = context;
            return ValueTask.FromResult(new ToolHandlerResult
            {
                Succeeded = true,
                Result = ResultContent,
                SideEffect = RuntimeSideEffect,
                ExternalOperationId = RuntimeExternalOperationId
            });
        }
    }
}
