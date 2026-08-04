using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// Tool Policy Engine 严格提交矩阵 Truth 测试
//
// 验证 ：DefaultDurableToolExecutor 的提交判定不再基于
// "effectiveSideEffect != Unknown" 的宽松条件，而是由 IToolEffectPolicy
// 按 Descriptor.DeclaredSideEffect 严格矩阵处置（Commit / Hold / FailClosed）：
// None/ReadOnly → Commit（结果确定后，重放安全）
// Write → 成功 Commit / 失败 Hold（副作用是否发生未知）
// IdempotentWrite → 幂等键明确 + 成功 Commit / 否则 Hold
// FencedWrite → 成功（Fence 窗口内）Commit / 失败 Hold
// NonIdempotentWrite → 永不自动提交（Hold + 需对账确认）
// RequiresReconciliation→ 永不自动提交（Hold + 需对账确认）
// Unknown → 永不自动提交（Hold，保守）
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

        Assert.IsFalse(result.Succeeded, "P1-1：Journal 未 Commit（Dispatched 模糊态）→ 模型不得看到成功结果。");
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

        Assert.IsFalse(result.Succeeded, "P1-1：RequiresReconciliation 未对账前 Journal 处于 Dispatched → 不得呈现成功。");
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

        Assert.IsFalse(result.Succeeded, "P1-1：副作用未知 → Journal 保持 Dispatched，未 Commit 前不得呈现成功。");
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

    // ── 审批门扩展（Actor 门 → 策略层）──────────────────────────────────────

    /// <summary>
    /// 验证：RequiresApproval 声明 + 写副作用 + 未确认审批 → 禁止自动提交（fail-safe）。
    /// 防止绕过 Actor 审批门的直连调用自动执行外部写副作用。
    /// </summary>
    [TestMethod]
    public void Policy_ApprovalGate_UnapprovedWrite_Holds()
    {
        var policy = new DefaultToolEffectPolicy();

        var unapproved = Resolve(policy, ToolSideEffect.Write, success: true, requiresApproval: true, approvalGranted: false);
        Assert.AreEqual(ToolExecutionDisposition.HoldForReconciliation, unapproved.Disposition,
            "RequiresApproval 写副作用未经审批 → 禁止自动提交。");
        Assert.IsTrue(unapproved.RequiresApprovalBeforeCommit, "未确认审批必须标记 RequiresApprovalBeforeCommit。");
    }

    /// <summary>
    /// 验证：审批已由 Actor 门确认（approvalGranted=true）→ 保持既有提交流程。
    /// </summary>
    [TestMethod]
    public void Policy_ApprovalGate_ApprovedWrite_Commits()
    {
        var policy = new DefaultToolEffectPolicy();

        var approved = Resolve(policy, ToolSideEffect.Write, success: true, requiresApproval: true, approvalGranted: true);
        Assert.AreEqual(ToolExecutionDisposition.Commit, approved.Disposition,
            "审批已确认 → 提交（不破坏 Actor 审批后的正常提交流程）。");
        Assert.IsFalse(approved.RequiresApprovalBeforeCommit);
    }

    /// <summary>
    /// 验证：只读/无副作用不设策略层审批门（无外部副作用，审批仅由 Actor 门负责）。
    /// </summary>
    [TestMethod]
    public void Policy_ApprovalGate_ReadOnly_NotGated()
    {
        var policy = new DefaultToolEffectPolicy();

        var read = Resolve(policy, ToolSideEffect.ReadOnly, success: true, requiresApproval: true, approvalGranted: false);
        Assert.AreEqual(ToolExecutionDisposition.Commit, read.Disposition,
            "只读副作用无外部副作用，策略层不拦截。");
    }

    // ── 重试决策矩阵（Dispatch 失败时）──────────────────────────────────────

    /// <summary>
    /// 验证：未配置重试策略（MaxRetries=0 / RetryBackoffPolicy=None）→ 不自动重试。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_DefaultNone_Aborts()
    {
        var policy = new DefaultToolEffectPolicy();

        var aborted = Resolve(policy, ToolSideEffect.Write, success: false, maxRetries: 0, backoff: ToolRetryBackoffPolicy.None);
        Assert.IsFalse(aborted.Retry.ShouldRetry, "未配置重试策略 → 不自动重试。");
    }

    /// <summary>
    /// 验证（P0-2）：普通写默认 RetrySafety=Never → 即使显式配置 MaxRetries&gt;0 也不自动重试——
    /// 外部写失败/超时 ≠ 副作用未发生，不能依据本地 retry 配置自动重试。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_WriteDefaultNever_NeverRetries()
    {
        var policy = new DefaultToolEffectPolicy();

        var aborted = Resolve(policy, ToolSideEffect.Write, success: false,
            maxRetries: 3, backoff: ToolRetryBackoffPolicy.Linear, retryDelay: TimeSpan.FromSeconds(2));
        Assert.IsFalse(aborted.Retry.ShouldRetry, "普通写未声明 RetrySafety → 禁止自动重试（外部副作用不可重放）。");
    }

    /// <summary>
    /// 验证（P0-2）：普通写声明 RetrySafety=ProviderIdempotent 且携带稳定幂等键 → 允许自动重试。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_WriteProviderIdempotent_WithKey_Retries()
    {
        var policy = new DefaultToolEffectPolicy();

        var retry = Resolve(policy, ToolSideEffect.Write, success: false,
            maxRetries: 3, backoff: ToolRetryBackoffPolicy.Linear, retryDelay: TimeSpan.FromSeconds(2),
            retrySafety: ToolRetrySafety.ProviderIdempotent);
        Assert.IsTrue(retry.Retry.ShouldRetry, "Provider 明确支持稳定幂等键 → 可凭幂等键去重后安全重试。");
        Assert.AreEqual(TimeSpan.FromSeconds(2), retry.Retry.Delay, "Linear 退避 → 固定 RetryDelay。");
        Assert.AreEqual(2, retry.Retry.AttemptsRemaining, "MaxRetries=3、attempt=0 → 剩余 2 次。");
    }

    /// <summary>
    /// 验证（P0-2）：普通写声明 ProviderIdempotent 但缺少稳定幂等键 → 禁止自动重试。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_WriteProviderIdempotent_WithoutKey_NeverRetries()
    {
        var policy = new DefaultToolEffectPolicy();

        var aborted = Resolve(policy, ToolSideEffect.Write, success: false,
            idempotencyKey: null, maxRetries: 3, backoff: ToolRetryBackoffPolicy.Linear,
            retrySafety: ToolRetrySafety.ProviderIdempotent);
        Assert.IsFalse(aborted.Retry.ShouldRetry, "ProviderIdempotent 但无稳定幂等键 → 外部无法去重，重试不安全。");
    }

    /// <summary>
    /// 验证（P0-2）：普通写声明 ProviderConfirmedNoEffect 且 Provider 返回 NoEffectConfirmed=true → 允许自动重试。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_WriteProviderConfirmedNoEffect_WithConfirmation_Retries()
    {
        var policy = new DefaultToolEffectPolicy();

        var retry = Resolve(policy, ToolSideEffect.Write, success: false,
            maxRetries: 3, backoff: ToolRetryBackoffPolicy.Linear,
            retrySafety: ToolRetrySafety.ProviderConfirmedNoEffect, noEffectConfirmed: true);
        Assert.IsTrue(retry.Retry.ShouldRetry, "Provider 明确确认无副作用 → 可安全重试。");
    }

    /// <summary>
    /// 验证（P0-2）：普通写声明 ProviderConfirmedNoEffect 但 Provider 未确认无副作用 → 禁止自动重试。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_WriteProviderConfirmedNoEffect_WithoutConfirmation_NeverRetries()
    {
        var policy = new DefaultToolEffectPolicy();

        var aborted = Resolve(policy, ToolSideEffect.Write, success: false,
            maxRetries: 3, backoff: ToolRetryBackoffPolicy.Linear,
            retrySafety: ToolRetrySafety.ProviderConfirmedNoEffect, noEffectConfirmed: false);
        Assert.IsFalse(aborted.Retry.ShouldRetry, "未获得 NoEffectConfirmed=true → 失败≠未发生，禁止自动重试。");
    }

    /// <summary>
    /// 验证（P0-2）：幂等写声明 IdempotentWrite 但未声明 RetrySafety=ProviderIdempotent → 禁止自动重试
    /// （声明副作用类型本身不代表 Provider 支持去重）。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_IdempotentWrite_WithoutRetrySafety_NeverRetries()
    {
        var policy = new DefaultToolEffectPolicy();

        var aborted = Resolve(policy, ToolSideEffect.IdempotentWrite, success: false,
            maxRetries: 3, backoff: ToolRetryBackoffPolicy.Linear);
        Assert.IsFalse(aborted.Retry.ShouldRetry, "幂等写未显式声明 RetrySafety=ProviderIdempotent → 禁止自动重试。");
    }

    /// <summary>
    /// 验证（P0-2）：Fenced 写声明 ProviderConfirmedNoEffect 且本次确认无副作用 → 允许自动重试
    /// （外部 Fence 已确认阻止旧请求）。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_FencedWrite_ConfirmedNoEffect_Retries()
    {
        var policy = new DefaultToolEffectPolicy();

        var retry = Resolve(policy, ToolSideEffect.FencedWrite, success: false,
            maxRetries: 3, backoff: ToolRetryBackoffPolicy.Linear,
            retrySafety: ToolRetrySafety.ProviderConfirmedNoEffect, noEffectConfirmed: true);
        Assert.IsTrue(retry.Retry.ShouldRetry, "Fence 已确认阻止旧请求 → 可安全重试。");
    }

    /// <summary>
    /// 验证：Exponential 退避 → 第 n 次重试前等待 RetryDelay * 2^(n-1)。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_ExponentialBackoff_DelayDoublesPerAttempt()
    {
        var policy = new DefaultToolEffectPolicy();

        var first = Resolve(policy, ToolSideEffect.ReadOnly, success: false,
            maxRetries: 3, backoff: ToolRetryBackoffPolicy.Exponential, retryDelay: TimeSpan.FromSeconds(1), attempt: 0);
        Assert.AreEqual(TimeSpan.FromSeconds(1), first.Retry.Delay, "第 1 次重试 → RetryDelay * 2^0。");

        var third = Resolve(policy, ToolSideEffect.ReadOnly, success: false,
            maxRetries: 3, backoff: ToolRetryBackoffPolicy.Exponential, retryDelay: TimeSpan.FromSeconds(1), attempt: 2);
        Assert.AreEqual(TimeSpan.FromSeconds(4), third.Retry.Delay, "第 3 次重试 → RetryDelay * 2^2。");
    }

    /// <summary>
    /// 验证：attempt 达 MaxRetries 上限 → 终止重试。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_ExceedsMaxAttempts_Aborts()
    {
        var policy = new DefaultToolEffectPolicy();

        var aborted = Resolve(policy, ToolSideEffect.Write, success: false,
            maxRetries: 2, backoff: ToolRetryBackoffPolicy.Linear, attempt: 2);
        Assert.IsFalse(aborted.Retry.ShouldRetry, "attempt >= MaxRetries → 终止重试。");
    }

    /// <summary>
    /// 验证：非幂等写外部副作用不可重放 → 永不自动重试。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_NonIdempotentWrite_NeverRetries()
    {
        var policy = new DefaultToolEffectPolicy();

        var aborted = Resolve(policy, ToolSideEffect.NonIdempotentWrite, success: false,
            maxRetries: 5, backoff: ToolRetryBackoffPolicy.Linear);
        Assert.IsFalse(aborted.Retry.ShouldRetry, "非幂等写不可重放 → 永不自动重试。");
    }

    /// <summary>
    /// 验证：副作用未知 → 保守，不自动重试。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_UnknownSideEffect_NeverRetries()
    {
        var policy = new DefaultToolEffectPolicy();

        var aborted = Resolve(policy, ToolSideEffect.Unknown, success: false,
            maxRetries: 5, backoff: ToolRetryBackoffPolicy.Linear);
        Assert.IsFalse(aborted.Retry.ShouldRetry, "副作用未知 → 不自动重试。");
    }

    /// <summary>
    /// 验证：幂等写无稳定幂等键 → 重试不安全，不自动重试。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_IdempotentWrite_WithoutKey_NeverRetries()
    {
        var policy = new DefaultToolEffectPolicy();

        var aborted = Resolve(policy, ToolSideEffect.IdempotentWrite, success: false,
            idempotencyKey: null, maxRetries: 5, backoff: ToolRetryBackoffPolicy.Linear);
        Assert.IsFalse(aborted.Retry.ShouldRetry, "幂等写无稳定幂等键 → 重试不安全。");
    }

    /// <summary>
    /// 验证：只读副作用重放安全 → 允许自动重试。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_ReadOnly_SafeRetry()
    {
        var policy = new DefaultToolEffectPolicy();

        var retry = Resolve(policy, ToolSideEffect.ReadOnly, success: false,
            maxRetries: 3, backoff: ToolRetryBackoffPolicy.Linear);
        Assert.IsTrue(retry.Retry.ShouldRetry, "只读副作用重放安全 → 允许自动重试。");
    }

    /// <summary>
    /// 验证：执行成功 → 无需重试（恒 Abort）。
    /// </summary>
    [TestMethod]
    public void Policy_Retry_Success_Aborts()
    {
        var policy = new DefaultToolEffectPolicy();

        var aborted = Resolve(policy, ToolSideEffect.Write, success: true,
            maxRetries: 3, backoff: ToolRetryBackoffPolicy.Linear);
        Assert.IsFalse(aborted.Retry.ShouldRetry, "执行成功 → 无需重试。");
    }

    // ── 投递模式决策 ────────────────────────────────────────────────────────

    /// <summary>
    /// 验证：投递模式决策回传 Descriptor 声明（Synchronous 默认 / AsyncDurable 透传）。
    /// </summary>
    [TestMethod]
    public void Policy_DeliveryMode_Passthrough()
    {
        var policy = new DefaultToolEffectPolicy();

        var sync = Resolve(policy, ToolSideEffect.Write, success: true);
        Assert.AreEqual(ToolDeliveryMode.Synchronous, sync.DeliveryMode, "默认 Synchronous。");

        var durable = Resolve(policy, ToolSideEffect.Write, success: true, deliveryMode: ToolDeliveryMode.AsyncDurable);
        Assert.AreEqual(ToolDeliveryMode.AsyncDurable, durable.DeliveryMode, "AsyncDurable 声明回传。");
    }

    // ── 执行器集成：重试循环 / 投递模式 / 审批门 ─────────────────────────────

    /// <summary>
    /// 验证：Dispatch 失败 2 次后成功 → 自动重试（MaxRetries=2，声明 ProviderIdempotent 安全契约）→ 提交。
    /// 重试使用同一 RequestId/幂等键/ExternalOperationId（外部 Provider 幂等记录可命中）。
    /// </summary>
    [TestMethod]
    public async Task Executor_RetryLoop_RetriesThenSucceeds()
    {
        var handler = CreateHandler("charge", ToolSideEffect.Write,
            failTimes: 2, descriptor: RetryDescriptor("charge", maxRetries: 2,
                sideEffect: ToolSideEffect.IdempotentWrite, retrySafety: ToolRetrySafety.ProviderIdempotent));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-R1"), 0, cts.Token);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.Committed, result.JournalState, "重试后成功 → 自动提交。");
        Assert.AreEqual(3, handler.InvocationCount, "1 次初始 + 2 次重试 = 3 次分派。");
        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State);
    }

    /// <summary>
    /// 验证：重试耗尽仍失败 → 副作用是否发生未知，禁止自动提交（Hold，journal 保持 Dispatched）。
    /// </summary>
    [TestMethod]
    public async Task Executor_RetryLoop_ExhaustedThenHolds()
    {
        var handler = CreateHandler("charge", ToolSideEffect.Write,
            succeed: false, descriptor: RetryDescriptor("charge", maxRetries: 2,
                sideEffect: ToolSideEffect.IdempotentWrite, retrySafety: ToolRetrySafety.ProviderIdempotent));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-R2"), 0, cts.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState,
            "重试耗尽仍失败 → 禁止自动提交（副作用是否发生未知）。");
        Assert.AreEqual(ToolFailurePhase.ProviderReturned, result.FailurePhase,
            "Provider 返回确定失败 → FailurePhase=ProviderReturned。");
        Assert.AreEqual(3, handler.InvocationCount, "1 次初始 + 2 次重试后放弃。");
        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State);
    }

    /// <summary>
    /// 验证：Dispatch 异常（Dispatcher 级故障）→ 按策略重试后成功。
    /// </summary>
    [TestMethod]
    public async Task Executor_RetryLoop_ExceptionThenSucceeds()
    {
        var handler = CreateHandler("charge", ToolSideEffect.Write,
            throwTimes: 1, descriptor: RetryDescriptor("charge", maxRetries: 2,
                sideEffect: ToolSideEffect.IdempotentWrite, retrySafety: ToolRetrySafety.ProviderIdempotent));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-R3"), 0, cts.Token);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.Committed, result.JournalState);
        Assert.AreEqual(2, handler.InvocationCount, "异常后重试一次成功。");
    }

    /// <summary>
    /// 验证：AsyncDurable 投递 → Commit 后显式推进 ResultDelivered（结果已送达事件流）。
    /// </summary>
    [TestMethod]
    public async Task Executor_AsyncDurableDelivery_MarksResultDelivered()
    {
        var handler = CreateHandler("charge", ToolSideEffect.Write,
            descriptor: RetryDescriptor("charge", maxRetries: 0, deliveryMode: ToolDeliveryMode.AsyncDurable));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-D1"), 0, cts.Token);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.ResultDelivered, result.JournalState,
            "AsyncDurable → Commit 后显式推进 ResultDelivered。");
        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.ResultDelivered, entry!.State);
    }

    /// <summary>
    /// 验证：默认 Synchronous 投递 → journal 停留在 Committed（由调用方完成后续投递语义）。
    /// </summary>
    [TestMethod]
    public async Task Executor_SynchronousDelivery_StaysCommitted()
    {
        var handler = CreateHandler("charge", ToolSideEffect.Write);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-D2"), 0, cts.Token);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.Committed, result.JournalState, "默认 Synchronous → 停留在 Committed。");
        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State);
    }

    /// <summary>
    /// 验证：直连调用（approvalGranted=false）+ RequiresApproval 写副作用 → 策略层禁止自动提交。
    /// </summary>
    [TestMethod]
    public async Task Executor_ApprovalGate_UnapprovedWrite_Holds()
    {
        var handler = CreateHandler("charge", ToolSideEffect.Write,
            descriptor: RetryDescriptor("charge", maxRetries: 0, requiresApproval: true));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-A1"), 0, cts.Token);

        Assert.IsFalse(result.Succeeded, "P1-1：审批未确认 → Journal 保持 Dispatched，未 Commit 前不得呈现成功。");
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState,
            "RequiresApproval 写副作用未确认审批 → 禁止自动提交（fail-safe）。");
        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State);
    }

    /// <summary>
    /// 验证：审批已确认（approvalGranted=true）→ 保持既有提交流程（提交）。
    /// </summary>
    [TestMethod]
    public async Task Executor_ApprovalGate_ApprovedWrite_Commits()
    {
        var handler = CreateHandler("charge", ToolSideEffect.Write,
            descriptor: RetryDescriptor("charge", maxRetries: 0, requiresApproval: true));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, journal, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("charge", "arg-A2"), 0, cts.Token,
            approvalGranted: true);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ToolDispatchState.Committed, result.JournalState,
            "审批已确认 → 提交（不破坏 Actor 审批后的正常提交流程）。");
        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State);
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static ToolExecutionPolicy Resolve(
        DefaultToolEffectPolicy policy,
        ToolSideEffect sideEffect,
        bool success,
        string? idempotencyKey = "idem-policy",
        int maxRetries = 0,
        ToolRetryBackoffPolicy backoff = ToolRetryBackoffPolicy.None,
        TimeSpan? retryDelay = null,
        ToolDeliveryMode deliveryMode = ToolDeliveryMode.Synchronous,
        bool requiresApproval = false,
        int attempt = 0,
        bool approvalGranted = false,
        ToolRetrySafety retrySafety = ToolRetrySafety.Never,
        bool noEffectConfirmed = false) => policy.Resolve(
        new ToolDescriptor
        {
            Name = "t",
            DeclaredSideEffect = sideEffect,
            MaxRetries = maxRetries,
            RetryBackoffPolicy = backoff,
            RetryDelay = retryDelay ?? TimeSpan.FromSeconds(5),
            DeliveryMode = deliveryMode,
            RequiresApproval = requiresApproval,
            RetrySafety = retrySafety
        },
        journal: null,
        result: new ToolExecutionResult
        {
            RequestId = "req-1",
            IdempotencyKey = idempotencyKey,
            SideEffect = sideEffect,
            JournalState = ToolDispatchState.Dispatched,
            Succeeded = success,
            NoEffectConfirmed = noEffectConfirmed,
            Duration = TimeSpan.Zero
        },
        attempt,
        approvalGranted);

    private static PolicyTestHandler CreateHandler(
        string toolName,
        ToolSideEffect sideEffect,
        bool succeed = true,
        ToolSideEffect? runtimeSideEffect = null,
        int failTimes = 0,
        int throwTimes = 0,
        ToolDescriptor? descriptor = null) => new(
            toolName, sideEffect, succeed, runtimeSideEffect ?? sideEffect, failTimes, throwTimes, descriptor);

    /// <summary>构建带重试/投递/审批配置的 ToolDescriptor 测试描述符。</summary>
    private static ToolDescriptor RetryDescriptor(
        string name,
        int maxRetries,
        ToolRetryBackoffPolicy backoff = ToolRetryBackoffPolicy.Linear,
        TimeSpan? retryDelay = null,
        ToolSideEffect sideEffect = ToolSideEffect.Write,
        ToolDeliveryMode deliveryMode = ToolDeliveryMode.Synchronous,
        bool requiresApproval = false,
        ToolRetrySafety retrySafety = ToolRetrySafety.Never) => new()
    {
        Name = name,
        DeclaredSideEffect = sideEffect,
        MaxRetries = maxRetries,
        RetryBackoffPolicy = backoff,
        RetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(1),
        DeliveryMode = deliveryMode,
        RequiresApproval = requiresApproval,
        RetrySafety = retrySafety
    };

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

    /// <summary>可配置的 Tool Handler：可声明副作用、失败/抛异常次数、重试/投递/审批描述符。</summary>
    private sealed class PolicyTestHandler : IToolHandler
    {
        private int _invocationCount;
        private readonly int _failTimes;
        private readonly int _throwTimes;
        private readonly ToolDescriptor _descriptor;

        public PolicyTestHandler(
            string toolName,
            ToolSideEffect sideEffect,
            bool succeed,
            ToolSideEffect runtimeSideEffect,
            int failTimes = 0,
            int throwTimes = 0,
            ToolDescriptor? descriptor = null)
        {
            ToolName = toolName;
            DeclaredSideEffect = sideEffect;
            Succeed = succeed;
            RuntimeSideEffect = runtimeSideEffect;
            _failTimes = failTimes;
            _throwTimes = throwTimes;
            _descriptor = descriptor ?? new ToolDescriptor
            {
                Name = toolName,
                DeclaredSideEffect = sideEffect
            };
        }

        public string ToolName { get; }
        public ToolSideEffect DeclaredSideEffect { get; }
        public bool Succeed { get; }
        public ToolSideEffect RuntimeSideEffect { get; }
        public string? Description => $"Test tool: {ToolName}";
        public string? ParametersJsonSchema => "{}";
        public ToolDescriptor Descriptor => _descriptor;
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _invocationCount);
            // 前 throwTimes 次抛异常（Dispatcher 转为 Succeeded=false + RequiresReconciliation），
            // 随后 failTimes 次返回失败，之后成功。
            if (n <= _throwTimes)
            {
                throw new InvalidOperationException("simulated-dispatch-exception");
            }
            if (!Succeed || n <= _throwTimes + _failTimes)
            {
                return ValueTask.FromResult(new ToolHandlerResult { Succeeded = false, Error = "simulated-failure", SideEffect = RuntimeSideEffect });
            }
            return ValueTask.FromResult(new ToolHandlerResult { Succeeded = true, Result = "ok", SideEffect = RuntimeSideEffect });
        }
    }
}
