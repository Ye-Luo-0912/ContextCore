using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// P0-1 / P0-2 —— Tool 失败阶段与重试安全契约
//
// P0-1：Tool Dispatch 异常会伪造 Journal 状态。
// Executor 在外部调用前原子写入 DispatchingIntent；但 Dispatcher 抛异常并重试耗尽后，
// 返回结果仍可能声明 JournalState=Prepared，实际数据库状态却是 DispatchingIntent。
// 结果：Actor 不创建对账记录 → Journal 进入模糊态 → Run 可能继续执行。
// 修复：Intent 持久化后的失败必须查询并返回真实 Journal 状态（GetStateAsync），
// 失败阶段 AfterIntentBeforeProvider 及以后强制进入对账；查询失败 fail-closed 返回 DispatchingIntent。
//
// P0-2：普通 Write 自动重试不安全。
// 外部写失败/超时 ≠ 副作用未发生；普通 Write 默认 RetrySafety=Never，
// 仅当 Descriptor 显式声明 ProviderIdempotent（+ 稳定幂等键）或
// ProviderConfirmedNoEffect（+ Provider 返回 NoEffectConfirmed=true）时允许自动重试。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
[TestCategory("Tool-Failure-Phase")]
public sealed class R30X_ToolFailurePhaseTests
{
    private const string Ws = "ws-r30x";
    private const string RunId = "run-r30x";

    /// <summary>测试 Run 复合身份键（与 Ws/RunId 常量一致）。</summary>
    private static readonly TenantRunKey Key = new(Ws, RunId);

    // ── P0-1：异常路径必须返回真实 Journal 状态（不得伪造 Prepared）─────────

    /// <summary>
    /// 验证（P0-1 核心）：Dispatcher 抛异常、无重试配置（MaxRetries=0）时，
    /// 返回结果必须携带真实 Journal 状态 DispatchingIntent（不是伪造的 Prepared），
    /// 使 Actor 的 RequiresReconciliation 命中并创建对账记录。
    /// </summary>
    [TestMethod]
    public async Task Executor_ExceptionNoRetry_ReturnsRealJournalState()
    {
        var descriptor = Descriptor("charge", ToolSideEffect.Write, maxRetries: 0,
            reconciliationHandler: "recon-charge", reconciliationDeadline: TimeSpan.FromHours(2));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatcher = new ThrowingToolDispatcher(descriptor);
        var journal = new InMemoryToolDispatchJournal();
        var executor = new DefaultDurableToolExecutor(dispatcher, journal);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-X1"), 0, cts.Token);

        Assert.IsFalse(result.Succeeded, "Dispatch 异常 → 失败结果。");
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, result.JournalState,
            "P0-1：异常路径必须返回真实 Journal 状态（DispatchingIntent），不得伪造 Prepared。");
        Assert.AreEqual(ToolFailurePhase.ProviderCallAmbiguous, result.FailurePhase,
            "Provider 调用结果不明确 → ProviderCallAmbiguous。");
        Assert.AreEqual("recon-charge", result.ReconciliationHandler,
            "对账处理程序必须随失败结果回传（Actor 据此创建对账记录）。");
        Assert.AreEqual(TimeSpan.FromHours(2), result.ReconciliationDeadline,
            "对账截止时长必须随失败结果回传。");
        Assert.AreEqual("cc:" + result.RequestId, result.ExternalOperationId,
            "异常路径必须保留 ExternalOperationId（对账 Handler 据此查询外部系统身份）。");

        var entry = await journal.GetEntryAsync(Key,result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, entry!.State,
            "journal 实际状态保持 DispatchingIntent（外部副作用可能已发生）。");
    }

    /// <summary>
    /// 验证（P0-1）：Dispatcher 抛异常且重试耗尽（配置了重试但始终异常）时，
    /// 返回真实 Journal 状态 + 重试计数错误信息，进入对账而非普通失败。
    /// </summary>
    [TestMethod]
    public async Task Executor_ExceptionRetriesExhausted_ReturnsRealJournalState()
    {
        var descriptor = Descriptor("charge", ToolSideEffect.IdempotentWrite, maxRetries: 2,
            retrySafety: ToolRetrySafety.ProviderIdempotent,
            reconciliationHandler: "recon-charge");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatcher = new ThrowingToolDispatcher(descriptor);
        var journal = new InMemoryToolDispatchJournal();
        var executor = new DefaultDurableToolExecutor(dispatcher, journal);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-X2"), 0, cts.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, result.JournalState,
            "P0-1：重试耗尽仍异常 → 返回真实 Journal 状态（DispatchingIntent），不得伪造 Prepared。");
        Assert.AreEqual(ToolFailurePhase.ProviderCallAmbiguous, result.FailurePhase);
        StringAssert.Contains(result.Error!, "重试 2 次后仍异常", "错误信息应保留重试计数（审计）。");

        var entry = await journal.GetEntryAsync(Key,result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, entry!.State);
    }

    /// <summary>
    /// 验证（P0-1）：Journal 状态查询失败（DB 故障）→ fail-closed 返回 DispatchingIntent，
    /// 绝不伪造 Prepared（按最高安全级别强制对账）。
    /// </summary>
    [TestMethod]
    public async Task Executor_StateQueryFailure_FailClosedDispatchingIntent()
    {
        var descriptor = Descriptor("charge", ToolSideEffect.Write, maxRetries: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatcher = new ThrowingToolDispatcher(descriptor);
        var journal = new FaultyStateQueryJournal(new InMemoryToolDispatchJournal());
        var executor = new DefaultDurableToolExecutor(dispatcher, journal);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-X3"), 0, cts.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, result.JournalState,
            "P0-1：状态查询失败 → fail-closed DispatchingIntent（强制对账），不得伪造 Prepared。");
        Assert.AreEqual(ToolFailurePhase.ProviderCallAmbiguous, result.FailurePhase);
    }

    /// <summary>
    /// 验证（P0-1）：Lease Fence 过期发生在 Intent 持久化之后、Provider 调用之前 →
    /// 返回真实 Journal 状态（DispatchingIntent）+ AfterIntentBeforeProvider，进入对账确认外部无副作用。
    /// </summary>
    [TestMethod]
    public async Task Executor_LeaseFenceExpired_ReturnsRealJournalState()
    {
        var handler = CreateHandler("write-file", ToolSideEffect.FencedWrite, descriptor: new ToolDescriptor
        {
            Name = "write-file",
            DeclaredSideEffect = ToolSideEffect.FencedWrite,
            RequiresLeaseFence = true
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var expiredFence = new AgentLeaseFence
        {
            LeaseToken = "token-expired",
            FencingToken = 1,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-5)
        };
        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("write-file", "arg-X4"), 0, cts.Token,
            leaseFence: expiredFence);

        Assert.IsFalse(result.Succeeded, "Fence 过期必须 fail-closed。");
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, result.JournalState,
            "P0-1：Intent 已持久化 → 返回真实 Journal 状态，不得伪造 Prepared。");
        Assert.AreEqual(ToolFailurePhase.AfterIntentBeforeProvider, result.FailurePhase,
            "Fence 失败在 Provider 调用之前 → AfterIntentBeforeProvider（强制对账确认无副作用）。");
        Assert.AreEqual(0, handler.InvocationCount, "fail-closed 时不得触碰外部副作用。");

        var entry = await journal.GetEntryAsync(Key,result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, entry!.State);
    }

    /// <summary>
    /// 验证（P0-1）：Provider 返回确定失败（非异常）→ FailurePhase=ProviderReturned，
    /// JournalState=Dispatched（模糊态），对账字段随结果回传。
    /// </summary>
    [TestMethod]
    public async Task Executor_ProviderDefiniteFailure_PopulatesReconciliationFields()
    {
        var handler = CreateHandler("charge", ToolSideEffect.Write, succeed: false,
            descriptor: Descriptor("charge", ToolSideEffect.Write, maxRetries: 0,
                reconciliationHandler: "recon-charge", reconciliationDeadline: TimeSpan.FromHours(4)));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-X5"), 0, cts.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState,
            "Provider 返回确定失败 → journal 已 MarkDispatched，保持 Dispatched 模糊态。");
        Assert.AreEqual(ToolFailurePhase.ProviderReturned, result.FailurePhase);
        Assert.AreEqual("recon-charge", result.ReconciliationHandler, "对账处理程序必须回传。");
        Assert.AreEqual(TimeSpan.FromHours(4), result.ReconciliationDeadline, "对账截止时长必须回传。");

        var entry = await journal.GetEntryAsync(Key,result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State);
    }

    // ── P0-2：NoEffectConfirmed 端到端贯通（Provider → 策略 → 重试）─────────

    /// <summary>
    /// 验证（P0-2）：Provider 每次失败都返回 NoEffectConfirmed=true + 描述符声明
    /// ProviderConfirmedNoEffect → 策略允许自动重试（重试循环实际发生），
    /// 耗尽后结果携带 NoEffectConfirmed=true 与 ProviderReturned。
    /// </summary>
    [TestMethod]
    public async Task Executor_ProviderConfirmedNoEffect_RetriesAndExhausts()
    {
        var handler = CreateHandler("charge", ToolSideEffect.Write, succeed: false,
            noEffectConfirmedOnFailure: true,
            descriptor: Descriptor("charge", ToolSideEffect.Write, maxRetries: 2,
                retrySafety: ToolRetrySafety.ProviderConfirmedNoEffect));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-X6"), 0, cts.Token);

        Assert.AreEqual(3, handler.InvocationCount,
            "ProviderConfirmedNoEffect + NoEffectConfirmed=true → 1 次初始 + 2 次重试。");
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState, "重试耗尽仍失败 → Hold，journal 保持 Dispatched。");
        Assert.AreEqual(ToolFailurePhase.ProviderReturned, result.FailurePhase);
        Assert.IsTrue(result.NoEffectConfirmed, "Provider 的 NoEffectConfirmed 必须贯通到最终结果。");

        var entry = await journal.GetEntryAsync(Key,result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State);
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static ToolDescriptor Descriptor(
        string name,
        ToolSideEffect sideEffect,
        int maxRetries,
        ToolRetrySafety retrySafety = ToolRetrySafety.Never,
        string? reconciliationHandler = null,
        TimeSpan? reconciliationDeadline = null) => new()
    {
        Name = name,
        DeclaredSideEffect = sideEffect,
        MaxRetries = maxRetries,
        RetryBackoffPolicy = maxRetries > 0 ? ToolRetryBackoffPolicy.Linear : ToolRetryBackoffPolicy.None,
        RetryDelay = TimeSpan.FromMilliseconds(1),
        RetrySafety = retrySafety,
        ReconciliationHandler = reconciliationHandler,
        ReconciliationDeadline = reconciliationDeadline ?? TimeSpan.FromHours(24)
    };

    private static (RealToolDispatcher Dispatcher, DefaultDurableToolExecutor Executor, InMemoryToolDispatchJournal Journal, InMemoryDurableToolResultStore ResultStore) CreateExecutor(
        FailurePhaseTestHandler handler)
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
        IdempotencyKey = "idem-r30x",
        ToolCallId = $"toolcall-{toolName}-0"
    };

    /// <summary>可配置 Tool Handler：可抛异常 / 返回失败（可选 NoEffectConfirmed=true）。</summary>
    private sealed class FailurePhaseTestHandler : IToolHandler
    {
        private int _invocationCount;

        public FailurePhaseTestHandler(
            string toolName,
            ToolSideEffect sideEffect,
            bool succeed,
            bool throwAlways,
            bool noEffectConfirmedOnFailure,
            ToolDescriptor descriptor)
        {
            ToolName = toolName;
            DeclaredSideEffect = sideEffect;
            Succeed = succeed;
            ThrowAlways = throwAlways;
            NoEffectConfirmedOnFailure = noEffectConfirmedOnFailure;
            Descriptor = descriptor;
        }

        public string ToolName { get; }
        public ToolSideEffect DeclaredSideEffect { get; }
        public bool Succeed { get; }
        public bool ThrowAlways { get; }
        public bool NoEffectConfirmedOnFailure { get; }
        public ToolDescriptor Descriptor { get; }
        public string? Description => $"Test tool: {ToolName}";
        public string? ParametersJsonSchema => "{}";
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            if (ThrowAlways)
            {
                throw new InvalidOperationException("simulated-dispatch-exception");
            }
            if (!Succeed)
            {
                return ValueTask.FromResult(new ToolHandlerResult
                {
                    Succeeded = false,
                    Error = "simulated-failure",
                    SideEffect = DeclaredSideEffect,
                    NoEffectConfirmed = NoEffectConfirmedOnFailure
                });
            }
            return ValueTask.FromResult(new ToolHandlerResult
            {
                Succeeded = true,
                Result = "ok",
                SideEffect = DeclaredSideEffect
            });
        }
    }

    private static FailurePhaseTestHandler CreateHandler(
        string toolName,
        ToolSideEffect sideEffect,
        bool succeed = true,
        bool throwAlways = false,
        bool noEffectConfirmedOnFailure = false,
        ToolDescriptor? descriptor = null) => new(
        toolName, sideEffect, succeed, throwAlways, noEffectConfirmedOnFailure,
        descriptor ?? new ToolDescriptor { Name = toolName, DeclaredSideEffect = sideEffect });

    /// <summary>
    /// GetStateAsync 抛异常的 Journal（模拟状态查询故障）：
    /// 其余方法委托给内部 InMemory journal，P0-1 异常路径仅使用 PrepareWithIntentAsync 与 GetStateAsync。
    /// </summary>
    private sealed class FaultyStateQueryJournal : IToolDispatchJournal
    {
        private readonly InMemoryToolDispatchJournal _inner;

        public FaultyStateQueryJournal(InMemoryToolDispatchJournal inner) => _inner = inner;

        public bool PersistsResults => _inner.PersistsResults;

        public ValueTask<ToolDispatchPrepareResult> PrepareAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default)
            => _inner.PrepareAsync(entry, cancellationToken);

        public ValueTask<ToolDispatchPrepareResult> PrepareWithIntentAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default)
            => _inner.PrepareWithIntentAsync(entry, cancellationToken);

        public ValueTask MarkDispatchingIntentAsync(TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
            => _inner.MarkDispatchingIntentAsync(key, requestId, cancellationToken);

        public ValueTask MarkDispatchedAsync(TenantRunKey key, string requestId, string? externalOperationId = null, CancellationToken cancellationToken = default)
            => _inner.MarkDispatchedAsync(key, requestId, externalOperationId, cancellationToken);

        public ValueTask MarkCommittedAsync(TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
            => _inner.MarkCommittedAsync(key, requestId, cancellationToken);

        public ValueTask MarkCommittedWithResultAsync(TenantRunKey key, string requestId, DurableToolResult result, CancellationToken cancellationToken = default)
            => _inner.MarkCommittedWithResultAsync(key, requestId, result, cancellationToken);

        public ValueTask MarkResultDeliveredAsync(TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
            => _inner.MarkResultDeliveredAsync(key, requestId, cancellationToken);

        public ValueTask BeginReconciliationAsync(TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
            => _inner.BeginReconciliationAsync(key, requestId, cancellationToken);

        public ValueTask MarkReconciledWithResultAsync(TenantRunKey key, string requestId, DurableToolResult result, CancellationToken cancellationToken = default)
            => _inner.MarkReconciledWithResultAsync(key, requestId, result, cancellationToken);

        public ValueTask<ToolDispatchJournalEntry?> GetEntryAsync(TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
            => _inner.GetEntryAsync(key, requestId, cancellationToken);

        public ValueTask<ToolDispatchState?> GetStateAsync(TenantRunKey key, string requestId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated-state-query-failure");
    }

    /// <summary>
    /// DispatchAsync 恒抛异常的 Dispatcher（模拟 Dispatcher 级故障 / Provider 调用异常）：
    /// 用于 P0-1 异常路径测试——RealToolDispatcher 会把 Handler 异常转为 Succeeded=false 结果，
    /// 不会走到执行器的异常分支。
    /// </summary>
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
