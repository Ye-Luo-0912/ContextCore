using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// Tool Journal CAS 严格状态机验收测试（3 项）
//
// 验证任务B 修复后的 Tool Journal 严格 expected-state CAS 语义：
// 1. 状态转换不能跳过 Dispatched 状态（Prepared → Committed 禁止）
// 2. 相同 RequestId 但不同 Payload 的重复 Prepare 请求会被拒绝（fails closed）
// 3. 相同 IdempotencyKey 的重复 Prepare 返回原始操作状态（而非新执行）
//
// 设计原则：
// - 使用真实 InMemoryToolDispatchJournal（不 mock），验证实际行为
// - 所有异步测试使用超时 CancellationTokenSource 防止挂起
// - 中文注释
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Tool-Journal")]
public sealed class R29H_ToolJournalCASAcceptanceTests
{
    /// <summary>
    /// 验证：Tool Journal 状态转换不能跳过 Dispatched 状态
    /// （不能从 Prepared 直接跳到 Committed，必须经过 Dispatched 中间状态）。
    /// </summary>
    [TestMethod]
    public async Task Journal_CannotSkip_DispatchedState_PreparedToCommittedThrows()
    {
        // 准备：创建 InMemoryToolDispatchJournal 并 Prepare 一条记录
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-skip-1";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 1. PrepareAsync 写入 Prepared 条目
        await journal.PrepareAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"),
            cts.Token);

        // 2. 尝试直接 MarkCommittedAsync（跳过 MarkDispatchedAsync）→ 应抛 InvalidOperationException（InvalidTransition）
        // expected-state 精确匹配，禁止跨级跳跃
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => journal.MarkCommittedAsync(Key, requestId, cts.Token).AsTask(),
            "Prepared → Committed 跨级跳跃必须被拒绝（InvalidTransition）。");

        // 3. 验证状态仍为 Prepared（未被错误推进到 Committed）
        var entry = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.IsNotNull(entry, "条目应仍存在。");
        Assert.AreEqual(
            ToolDispatchState.Prepared,
            entry!.State,
            "状态必须仍为 Prepared（跨级跳跃被拒绝后不应推进）。");
    }

    /// <summary>
    /// 验证：相同 RequestId 但不同 PayloadDigest 的重复 Prepare 请求会被拒绝
    /// （fails closed，不静默接受；原始记录未被覆盖）。
    /// </summary>
    [TestMethod]
    public async Task DuplicateRequestId_WithDifferentPayload_FailsClosed_ThrowsAndPreservesOriginal()
    {
        // 准备：创建 InMemoryToolDispatchJournal
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-dup-1";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 1. 第一次 PrepareAsync（PayloadDigest = digest-A，IdempotencyKey = idem-original）
        var originalEntry = BuildEntry(
            requestId,
            ToolDispatchState.Prepared,
            payloadDigest: "digest-A",
            idempotencyKey: "idem-original");
        await journal.PrepareAsync(originalEntry, cts.Token);

        // 2. 第二次 PrepareAsync（相同 RequestId，但 PayloadDigest = digest-B，IdempotencyKey = idem-conflicting）
        // 语义等价校验失败 → 抛 InvalidOperationException（RequestIdReuseDetected）
        var conflictingEntry = BuildEntry(
            requestId,
            ToolDispatchState.Prepared,
            payloadDigest: "digest-B",
            idempotencyKey: "idem-conflicting");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => journal.PrepareAsync(conflictingEntry, cts.Token).AsTask(),
            "相同 RequestId 但不同 PayloadDigest 的重复 Prepare 必须被拒绝（RequestIdReuseDetected）。");

        // 3. 验证原始记录未被覆盖
        // PayloadDigest 仍是 digest-A；IdempotencyKey 仍是 idem-original
        var stored = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.IsNotNull(stored, "原始条目应仍存在（未被冲突请求覆盖）。");
        Assert.AreEqual(
            "digest-A",
            stored!.PayloadDigest,
            "PayloadDigest 必须保留原始值（fails closed，未被覆盖）。");
        Assert.AreEqual(
            "idem-original",
            stored.IdempotencyKey,
            "IdempotencyKey 必须保留原始值（fails closed，未被覆盖）。");
    }

    /// <summary>
    /// 验证：相同 IdempotencyKey 的重复 Prepare 返回原始操作的状态
    /// （而非执行新操作；不重置已推进的状态）。
    /// </summary>
    /// <remarks>
    /// 场景：第一次完整执行 Prepare → Dispatch → Commit，第二次 Prepare 相同语义条目，
    /// 应幂等命中（不抛异常，不重置状态），保留原始操作推进到的 Committed 状态。
    /// </remarks>
    [TestMethod]
    public async Task DuplicateIdempotencyKey_ReturnsOriginalOperation_NotNewExecution()
    {
        // 准备：创建 InMemoryToolDispatchJournal
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-idem-1";
        var idempotencyKey = "idem-key-X";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 1. 第一次完整执行：Prepare → Dispatch → Commit
        await journal.PrepareAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, idempotencyKey: idempotencyKey, payloadDigest: "digest-shared"),
            cts.Token);
        await journal.MarkDispatchedAsync(Key, requestId, externalOperationId: "ext-op-1", cancellationToken: cts.Token);
        await journal.MarkCommittedAsync(Key, requestId, cts.Token);

        // 验证第一次推进到 Committed
        var entryAfterFirstRun = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.IsNotNull(entryAfterFirstRun);
        Assert.AreEqual(ToolDispatchState.Committed, entryAfterFirstRun!.State);
        Assert.AreEqual("ext-op-1", entryAfterFirstRun.ExternalOperationId);

        // 2. 第二次 PrepareAsync（相同 IdempotencyKey + 相同语义字段）→ 幂等命中
        // PrepareAsync 语义：key 已存在且语义等价 → 不抛异常，不覆盖既有状态
        await journal.PrepareAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, idempotencyKey: idempotencyKey, payloadDigest: "digest-shared"),
            cts.Token);

        // 3. 验证返回的是原始操作的状态（Committed，未被重置为 Prepared）
        // 说明：PrepareAsync 重复时不重置状态，原始操作推进的 Committed 状态被保留
        var entryAfterSecondPrepare = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.IsNotNull(entryAfterSecondPrepare, "条目应仍存在。");
        Assert.AreEqual(
            ToolDispatchState.Committed,
            entryAfterSecondPrepare!.State,
            "重复 PrepareAsync 不应重置状态；应保留原始操作推进到的 Committed 状态。");
        Assert.AreEqual(
            "ext-op-1",
            entryAfterSecondPrepare.ExternalOperationId,
            "ExternalOperationId 应保留原始操作的值（未被覆盖为新操作）。");

        // 4. 二次 MarkDispatchedAsync 也应幂等命中（AlreadyApplied，不抛异常）
        // 说明状态机不会重复推进
        await journal.MarkDispatchedAsync(Key, requestId, externalOperationId: "ext-op-should-be-ignored", cancellationToken: cts.Token);
        var entryAfterSecondMark = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.IsNotNull(entryAfterSecondMark);
        Assert.AreEqual(
            ToolDispatchState.Committed,
            entryAfterSecondMark!.State,
            "重复 MarkDispatchedAsync 应幂等命中（state 已 > Dispatched），不应逆退或重新推进。");
        Assert.AreEqual(
            "ext-op-1",
            entryAfterSecondMark.ExternalOperationId,
            "ExternalOperationId 不应被第二次 MarkDispatchedAsync 覆盖（已是 Committed 状态，幂等）。");
    }

    /// <summary>
    /// 验证：PrepareWithIntentAsync 首次调用单次原子写即落库 DispatchingIntent，
    /// 返回 ShouldDispatch=true（外部调用尚未开始，可直接 Dispatch，无需再单独标记 Intent）。
    /// </summary>
    [TestMethod]
    public async Task PrepareWithIntent_FreshCall_InsertsDispatchingIntentAndShouldDispatch()
    {
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-intent-fresh";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await journal.PrepareWithIntentAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"), cts.Token);

        Assert.IsTrue(result.ShouldDispatch, "首次 PrepareWithIntentAsync 应返回 ShouldDispatch=true。");
        Assert.IsFalse(result.NeedsReconciliation, "首次调用不应要求对账。");
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, result.CurrentState,
            "CurrentState 应为 DispatchingIntent（Intent 已前置落库）。");

        var entry = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.IsNotNull(entry, "条目应已写入。");
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, entry!.State,
            "journal 条目应直接处于 DispatchingIntent（Prepare + Intent 单次原子写）。");
    }

    /// <summary>
    /// 验证：已处于 DispatchingIntent（上次崩溃残留/并发分派）时再次
    /// PrepareWithIntentAsync → NeedsReconciliation=true（外部调用可能已开始，禁止重复 Dispatch）。
    /// </summary>
    [TestMethod]
    public async Task PrepareWithIntent_ExistingDispatchingIntent_NeedsReconciliation()
    {
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-intent-residue";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 第一次调用：写入 DispatchingIntent（模拟崩溃前已标记 Intent）
        var first = await journal.PrepareWithIntentAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"), cts.Token);
        Assert.IsTrue(first.ShouldDispatch);

        // 第二次调用（崩溃恢复重试）：应判定为对账，而非再次 Dispatch
        var second = await journal.PrepareWithIntentAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"), cts.Token);
        Assert.IsFalse(second.ShouldDispatch, "既有 DispatchingIntent 不应再次 Dispatch。");
        Assert.IsTrue(second.NeedsReconciliation, "既有 DispatchingIntent（外部调用可能已开始）应要求对账。");
    }

    /// <summary>
    /// 验证：既有 Prepared 前驱（旧两步流程崩溃残留）经 PrepareWithIntentAsync
    /// 原子推进到 DispatchingIntent，并返回 ShouldDispatch=true。
    /// </summary>
    [TestMethod]
    public async Task PrepareWithIntent_ExistingPrepared_AdvancesAndShouldDispatch()
    {
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-intent-prepared";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 旧两步流程残留：仅 PrepareAsync 写入 Prepared（外部调用从未开始）
        await journal.PrepareAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"), cts.Token);

        var result = await journal.PrepareWithIntentAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"), cts.Token);

        Assert.IsTrue(result.ShouldDispatch, "既有 Prepared（外部调用未开始）应返回 ShouldDispatch=true。");
        Assert.IsFalse(result.NeedsReconciliation);

        var entry = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.IsNotNull(entry);
        Assert.AreEqual(ToolDispatchState.DispatchingIntent, entry!.State,
            "既有 Prepared 前驱应被原子推进到 DispatchingIntent。");
    }

    /// <summary>
    /// 验证：journal 已 Committed 时 PrepareWithIntentAsync 返回缓存结果
    /// （InMemory 自带缓存），ShouldDispatch=false（禁止重新执行外部副作用）。
    /// </summary>
    [TestMethod]
    public async Task PrepareWithIntent_Committed_ReturnsCachedResult()
    {
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-intent-committed";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 完整执行到 Committed（带结果）
        await journal.PrepareAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"), cts.Token);
        await journal.MarkDispatchedAsync(Key, requestId, externalOperationId: "ext-op-1", cancellationToken: cts.Token);
        await journal.MarkCommittedWithResultAsync(Key, requestId, new DurableToolResult
        {
            ToolCallId = "call-1",
            RequestId = requestId,
            WorkspaceId = "ws-test-journal",
            RunId = "run-test-journal",
            InvocationId = requestId,
            SideEffect = ToolSideEffect.None,
            Result = "cached-result",
            Succeeded = true,
            DurationMs = 1
        }, cts.Token);

        var result = await journal.PrepareWithIntentAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"), cts.Token);

        Assert.IsFalse(result.ShouldDispatch, "Committed 后不应再次 Dispatch。");
        Assert.IsFalse(result.NeedsReconciliation);
        Assert.IsNotNull(result.CachedResult, "InMemory journal 应返回缓存结果。");
        Assert.AreEqual("cached-result", result.CachedResult!.Result);
    }

    /// <summary>
    /// 验证：PrepareWithIntentAsync 语义等价校验与 PrepareAsync 一致——
    /// 相同 RequestId 但不同 PayloadDigest → RequestIdReuseDetected（fails closed）。
    /// </summary>
    [TestMethod]
    public async Task PrepareWithIntent_SemanticMismatch_Throws()
    {
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-intent-conflict";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await journal.PrepareWithIntentAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"), cts.Token);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => journal.PrepareWithIntentAsync(
                BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-B"), cts.Token).AsTask(),
            "相同 RequestId 但不同 PayloadDigest 必须被拒绝（RequestIdReuseDetected）。");
    }

    /// <summary>
    /// 验证：对账入口 BeginReconciliationAsync 将 Dispatched 模糊态原子推进到 Reconciling。
    /// </summary>
    [TestMethod]
    public async Task Journal_BeginReconciliation_FromDispatched_AdvancesToReconciling()
    {
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-recon-1";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await journal.PrepareWithIntentAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A", externalOperationId: "ext-op-1"),
            cts.Token);
        await journal.MarkDispatchedAsync(Key, requestId, "ext-op-1", cts.Token);

        await journal.BeginReconciliationAsync(Key, requestId, cts.Token);

        var entry = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Reconciling, entry!.State, "Dispatched 模糊态应原子推进到 Reconciling。");
        Assert.AreEqual("ext-op-1", entry.ExternalOperationId, "ExternalOperationId 应在对账状态下保留。");
    }

    /// <summary>
    /// 验证：Prepared 状态（外部调用从未开始）禁止进入对账——它应被重新 Dispatch 而非对账。
    /// </summary>
    [TestMethod]
    public async Task Journal_BeginReconciliation_FromPrepared_ThrowsInvalidTransition()
    {
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-recon-2";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await journal.PrepareAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"),
            cts.Token);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => journal.BeginReconciliationAsync(Key, requestId, cts.Token).AsTask(),
            "Prepared（外部调用从未开始）进入对账必须被拒绝（InvalidTransition）。");

        var entry = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Prepared, entry!.State, "状态必须保持 Prepared。");
    }

    /// <summary>
    /// 验证：已处于 Reconciling 时重复 BeginReconciliationAsync 幂等成功（不报错、不改变状态）。
    /// </summary>
    [TestMethod]
    public async Task Journal_BeginReconciliation_AlreadyReconciling_Idempotent()
    {
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-recon-3";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await journal.PrepareWithIntentAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"),
            cts.Token);
        await journal.BeginReconciliationAsync(Key, requestId, cts.Token);
        await journal.BeginReconciliationAsync(Key, requestId, cts.Token); // 幂等重入

        var entry = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Reconciling, entry!.State, "重复进入对账应幂等，状态保持 Reconciling。");
    }

    /// <summary>
    /// 验证：对账完成 MarkReconciledWithResultAsync 将 Reconciling 原子推进到 Committed 并缓存对账结果。
    /// </summary>
    [TestMethod]
    public async Task Journal_MarkReconciledWithResult_FromReconciling_CommitsWithResult()
    {
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-recon-4";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await journal.PrepareWithIntentAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"),
            cts.Token);
        await journal.BeginReconciliationAsync(Key, requestId, cts.Token);

        var reconciledResult = new DurableToolResult
        {
            ToolCallId = "tool-call-4",
            RequestId = requestId,
            WorkspaceId = "ws-test-journal",
            RunId = "run-test-journal",
            InvocationId = requestId,
            SideEffect = ToolSideEffect.Write,
            ExternalOperationId = "ext-op-4",
            Result = "reconciled-result",
            Succeeded = true,
            DurationMs = 1.0
        };
        await journal.MarkReconciledWithResultAsync(Key, requestId, reconciledResult, cts.Token);

        var entry = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State, "对账完成后应推进到 Committed。");

        // 对账结果进入缓存：后续 Prepare 应返回 CachedResult（禁止重放）
        var prepare = await journal.PrepareWithIntentAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"),
            cts.Token);
        Assert.IsFalse(prepare.ShouldDispatch, "对账提交后禁止重新 Dispatch。");
        Assert.IsNotNull(prepare.CachedResult, "对账提交的结果应可被缓存读取。");
        Assert.AreEqual("reconciled-result", prepare.CachedResult!.Result);
    }

    /// <summary>
    /// 验证：从 Dispatched（未进入对账）直接 MarkReconciledWithResultAsync 必须被拒绝（跨级跳跃）。
    /// </summary>
    [TestMethod]
    public async Task Journal_MarkReconciledWithResult_FromDispatched_ThrowsInvalidTransition()
    {
        var journal = new InMemoryToolDispatchJournal();
        var requestId = "req-recon-5";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await journal.PrepareWithIntentAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, payloadDigest: "digest-A"),
            cts.Token);
        await journal.MarkDispatchedAsync(Key, requestId, "ext-op-5", cts.Token);

        var result = new DurableToolResult
        {
            ToolCallId = "tool-call-5",
            RequestId = requestId,
            InvocationId = requestId,
            SideEffect = ToolSideEffect.Write,
            Succeeded = true,
            DurationMs = 1.0
        };
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => journal.MarkReconciledWithResultAsync(Key, requestId, result, cts.Token).AsTask(),
            "未进入 Reconciling 直接提交对账结果必须被拒绝（InvalidTransition）。");

        var entry = await journal.GetEntryAsync(Key, requestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State, "状态必须保持 Dispatched。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    /// <summary>测试 Run 复合身份键（与 BuildEntry 的双键一致）。</summary>
    private static readonly TenantRunKey Key = new("ws-test-journal", "run-test-journal");

    private static ToolDispatchJournalEntry BuildEntry(
        string requestId,
        ToolDispatchState state,
        string? payloadDigest = null,
        string? idempotencyKey = null,
        string? externalOperationId = null) => new()
        {
            RequestId = requestId,
            ToolName = "echo",
            State = state,
            IdempotencyKey = idempotencyKey ?? ("idem-" + requestId),
            ExternalOperationId = externalOperationId,
            PayloadDigest = payloadDigest ?? ToolDispatchJournalEntry.ComputePayloadDigest("default-payload"),
            WorkspaceId = "ws-test-journal",
            RunId = "run-test-journal",
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
