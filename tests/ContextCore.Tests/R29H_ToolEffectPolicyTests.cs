using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// Tool Policy Engine 严格提交矩阵 Truth 测试
//
// 验证 P0-2：DefaultDurableToolExecutor 的提交判定不再基于
// "effectiveSideEffect != Unknown" 的宽松条件，而是由 IToolEffectPolicy
// 按 Descriptor.DeclaredSideEffect 严格矩阵处置（Commit / Hold / FailClosed）：
//   None/ReadOnly         → Commit（结果确定后，重放安全）
//   Write                 → 成功 Commit / 失败 Hold（副作用是否发生未知）
//   IdempotentWrite       → 幂等键明确 + 成功 Commit / 否则 Hold
//   FencedWrite           → 成功（Fence 窗口内）Commit / 失败 Hold
//   NonIdempotentWrite    → 永不自动提交（Hold + 需对账确认）
//   RequiresReconciliation→ 永不自动提交（Hold + 需对账确认）
//   Unknown               → 永不自动提交（Hold，保守）
//
// 杜绝的危险误提交：NonIdempotentWrite 自动提交、RequiresReconciliation 自动提交、
// 外部调用失败但副作用未知、Handler 失败但声明为写、Fence 未确认的写操作。
// ===========================================================================

[TestClass]
[TestCategory("Kill-Point")]
[TestCategory("External-Effect-Truth")]
public sealed class R29H_ToolEffectPolicyTests
{
    private const string Ws = "ws-policy";
    private const string RunId = "run-policy";

    // ── 策略引擎单元矩阵（直接 Resolve）────────────────────────────────────

    [TestMethod]
    public void Policy_NoneOrReadOnly_CommitsRegardlessOfOutcome()
    {
        var policy = new DefaultToolEffectPolicy();

        var none = Resolve(policy, ToolSideEffect.None, success: true);
        Assert.AreEqual(ToolExecutionDisposition.Commit, none.Disposition);

        var readOnlyFailed = Resolve(policy, ToolSideEffect.ReadOnly, success: false);
        Assert.AreEqual(ToolExecutionDisposition.Commit, readOnlyFailed.Disposition,
            "只读副作用即使失败也安全提交（无外部副作用，结果确定）。");
    }

    [TestMethod]
    public void Policy_Write_CommitsOnSuccess_HoldsOnFailure()
    {
        var policy = new DefaultToolEffectPolicy();

        var ok = Resolve(policy, ToolSideEffect.Write, success: true);
        Assert.AreEqual(ToolExecutionDisposition.Commit, ok.Disposition, "写副作用执行成功 → 提交。");

        var failed = Resolve(policy, ToolSideEffect.Write, success: false);
        Assert.AreEqual(ToolExecutionDisposition.HoldForReconciliation, failed.Disposition,
            "写副作用执行失败 → 副作用是否发生未知，禁止自动提交。");
    }

    [TestMethod]
    public void Policy_IdempotentWrite_RequiresStableKeyAndSuccess()
    {
        var policy = new DefaultToolEffectPolicy();

        var ok = Resolve(policy, ToolSideEffect.IdempotentWrite, success: true, idempotencyKey: "charge:req-1");
        Assert.AreEqual(ToolExecutionDisposition.Commit, ok.Disposition, "幂等键明确 + 成功 → 提交。");

        var missingKey = Resolve(policy, ToolSideEffect.IdempotentWrite, success: true, idempotencyKey: null);
        Assert.AreEqual(ToolExecutionDisposition.HoldForReconciliation, missingKey.Disposition,
            "幂等键缺失 → 无法安全提交（外部系统无法去重）。");

        var failed = Resolve(policy, ToolSideEffect.IdempotentWrite, success: false, idempotencyKey: "charge:req-1");
        Assert.AreEqual(ToolExecutionDisposition.HoldForReconciliation, failed.Disposition,
            "幂等写失败 → 副作用是否发生未知，禁止自动提交。");
    }

    [TestMethod]
    public void Policy_FencedWrite_CommitsOnSuccess_HoldsOnFailure()
    {
        var policy = new DefaultToolEffectPolicy();

        var ok = Resolve(policy, ToolSideEffect.FencedWrite, success: true);
        Assert.AreEqual(ToolExecutionDisposition.Commit, ok.Disposition, "Fenced 写成功（Fence 窗口内）→ 提交。");

        var failed = Resolve(policy, ToolSideEffect.FencedWrite, success: false);
        Assert.AreEqual(ToolExecutionDisposition.HoldForReconciliation, failed.Disposition,
            "Fenced 写失败 → Fence 未得到外部系统确认，禁止自动提交。");
    }

    [TestMethod]
    public void Policy_NonIdempotentWrite_NeverAutoCommits()
    {
        var policy = new DefaultToolEffectPolicy();

        var ok = Resolve(policy, ToolSideEffect.NonIdempotentWrite, success: true, idempotencyKey: "k");
        Assert.AreEqual(ToolExecutionDisposition.HoldForReconciliation, ok.Disposition,
            "非幂等写即使成功也禁止自动提交（需 Approval + 外部操作身份 → 对账）。");
        Assert.IsTrue(ok.RequiresReconciliationBeforeCommit, "非幂等写必须要求对账确认后提交。");
    }

    [TestMethod]
    public void Policy_RequiresReconciliation_NeverAutoCommits()
    {
        var policy = new DefaultToolEffectPolicy();

        var ok = Resolve(policy, ToolSideEffect.RequiresReconciliation, success: true);
        Assert.AreEqual(ToolExecutionDisposition.HoldForReconciliation, ok.Disposition,
            "声明 RequiresReconciliation → 必须经 Reconciliation Handler 确认后提交。");
        Assert.IsTrue(ok.RequiresReconciliationBeforeCommit);
    }

    [TestMethod]
    public void Policy_UnknownSideEffect_NeverAutoCommits()
    {
        var policy = new DefaultToolEffectPolicy();

        var ok = Resolve(policy, ToolSideEffect.Unknown, success: true);
        Assert.AreEqual(ToolExecutionDisposition.HoldForReconciliation, ok.Disposition,
            "副作用未知 → 不自动提交（保守策略）。");
    }

    // ── 执行器集成：严格矩阵驱动 journal 提交 ───────────────────────────────

    /// <summary>
    /// 验证：声明 Write 的 Tool 执行成功 → 自动提交到 Committed。
    /// </summary>
    [TestMethod]
    public async Task Executor_WriteSuccess_Commits()
    {
        var handler = CreateHandler("charge", ToolSideEffect.Write);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-A"), 0, cts.Token);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.Committed, result.JournalState, "写副作用成功应提交。");

        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State);
    }

    /// <summary>
    /// 验证：声明 Write 的 Tool 执行失败 → 禁止自动提交（副作用是否发生未知），
    /// journal 保持 Dispatched 等待对账。
    /// </summary>
    [TestMethod]
    public async Task Executor_WriteFailure_HoldsForReconciliation()
    {
        var handler = CreateHandler("charge", ToolSideEffect.Write, succeed: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-B"), 0, cts.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState,
            "写副作用失败 → 不得自动提交（P0-2：失败但副作用是否发生未知）。");

        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State, "journal 应保持 Dispatched 等待对账。");
        Assert.AreEqual(1, handler.InvocationCount, "外部副作用已执行一次（结果未知，不能重放）。");
    }

    /// <summary>
    /// 验证：声明 NonIdempotentWrite 的 Tool 即使执行成功也不自动提交——
    /// 必须 Approval + 外部操作身份确认后经对账提交。
    /// </summary>
    [TestMethod]
    public async Task Executor_NonIdempotentWrite_Success_NotAutoCommitted()
    {
        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("bank-transfer", "arg-C"), 0, cts.Token);

        Assert.IsTrue(result.Succeeded, "外部调用本身成功。");
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState,
            "非幂等写成功也不得自动提交（P0-2 严格矩阵）。");

        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State,
            "journal 应保持 Dispatched：非幂等写需对账确认外部副作用真相后提交。");
    }

    /// <summary>
    /// 验证：声明 RequiresReconciliation 的 Tool 即使执行成功也不自动提交。
    /// </summary>
    [TestMethod]
    public async Task Executor_RequiresReconciliation_Success_NotAutoCommitted()
    {
        var handler = CreateHandler("legacy-write", ToolSideEffect.RequiresReconciliation);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("legacy-write", "arg-D"), 0, cts.Token);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState,
            "RequiresReconciliation 声明 → 必须经 Reconciliation Handler 确认后提交。");

        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State);
    }

    /// <summary>
    /// 验证：运行时副作用 Unknown（未声明）→ 不自动提交，保持 Dispatched。
    /// </summary>
    [TestMethod]
    public async Task Executor_UnknownSideEffect_NotAutoCommitted()
    {
        var handler = CreateHandler("mystery", ToolSideEffect.Unknown, runtimeSideEffect: ToolSideEffect.Unknown);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("mystery", "arg-E"), 0, cts.Token);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ToolSideEffect.Unknown, result.SideEffect);
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState,
            "副作用未知 → 不自动提交（保守策略）。");

        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State);
    }

    /// <summary>
    /// 验证：ReadOnly 声明 → 结果确定后提交（重放安全）。
    /// </summary>
    [TestMethod]
    public async Task Executor_ReadOnly_Commits()
    {
        var handler = CreateHandler("read-file", ToolSideEffect.ReadOnly);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("read-file", "arg-F"), 0, cts.Token);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.Committed, result.JournalState);
        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State);
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static ToolExecutionPolicy Resolve(
        DefaultToolEffectPolicy policy,
        ToolSideEffect sideEffect,
        bool success,
        string? idempotencyKey = "idem-policy") => policy.Resolve(
        new ToolDescriptor
        {
            Name = "t",
            DeclaredSideEffect = sideEffect
        },
        journal: null,
        result: new ToolExecutionResult
        {
            RequestId = "req-1",
            IdempotencyKey = idempotencyKey,
            SideEffect = sideEffect,
            JournalState = ToolDispatchState.Dispatched,
            Succeeded = success,
            Duration = TimeSpan.Zero
        });

    private static PolicyTestHandler CreateHandler(
        string toolName,
        ToolSideEffect sideEffect,
        bool succeed = true,
        ToolSideEffect? runtimeSideEffect = null) => new(toolName, sideEffect, succeed, runtimeSideEffect ?? sideEffect);

    private static (RealToolDispatcher Dispatcher, DefaultDurableToolExecutor Executor, InMemoryToolDispatchJournal Journal, InMemoryDurableToolResultStore ResultStore) CreateExecutor(
        PolicyTestHandler handler)
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
        IdempotencyKey = "idem-policy",
        ToolCallId = $"toolcall-{toolName}-0"
    };

    /// <summary>可配置的 Tool Handler：可声明副作用、失败或返回外部操作 ID。</summary>
    private sealed class PolicyTestHandler : IToolHandler
    {
        private int _invocationCount;

        public PolicyTestHandler(string toolName, ToolSideEffect sideEffect, bool succeed, ToolSideEffect runtimeSideEffect)
        {
            ToolName = toolName;
            DeclaredSideEffect = sideEffect;
            Succeed = succeed;
            RuntimeSideEffect = runtimeSideEffect;
        }

        public string ToolName { get; }
        public ToolSideEffect DeclaredSideEffect { get; }
        public bool Succeed { get; }
        public ToolSideEffect RuntimeSideEffect { get; }
        public string? Description => $"Test tool: {ToolName}";
        public string? ParametersJsonSchema => "{}";
        public ToolDescriptor Descriptor => new()
        {
            Name = ToolName,
            DeclaredSideEffect = DeclaredSideEffect
        };
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult(Succeed
                ? new ToolHandlerResult { Succeeded = true, Result = "ok", SideEffect = RuntimeSideEffect }
                : new ToolHandlerResult { Succeeded = false, Error = "simulated-failure", SideEffect = RuntimeSideEffect });
        }
    }
}
