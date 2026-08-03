using System.Reflection;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

// ===========================================================================
// Tool 对账（Reconciliation）Truth 测试
//
// 验证 P0-3：Journal 处于模糊状态（DispatchingIntent/Dispatched/Reconciling）的
// Tool 调用必须经对账确认外部副作用真相后才能收尾，Run 不得在存在未裁决记录时
// 进入 Completed：
//   1. 存储：InMemoryToolReconciliationStore 的幂等创建 / 未裁决查询 / CAS 推进 /
//      终态裁决语义；
//   2. 协调器：ToolReconciliationCoordinator 裁决唯一入口——journal 显式进入
//      Reconciling 再提交真相结果（occurred → Committed + result；未发生 → Committed +
//      void 失败结果），记录原子落终态（Resolved/Rejected）；重放返回提交的真相，
//      绝不重复执行外部调用；
//   3. Worker：轮询 Pending 记录 → 按 Handler 名称匹配对账 → 无 Handler 保持 Pending
//      等待人工裁决；
//   4. Actor 集成：未裁决 Tool 阻止 Completed（Run 停车在 AwaitingReconciliation），
//      全部裁决后恢复执行 → Completed，外部调用全程只执行一次。
// ===========================================================================

[TestClass]
[TestCategory("Kill-Point")]
[TestCategory("External-Effect-Truth")]
public sealed class R29H_ToolReconciliationTests
{
    private const string Ws = "ws-recon";
    private const string RunId = "run-recon";

    // ── 1. 对账记录存储单元测试 ────────────────────────────────────────────

    /// <summary>
    /// 验证：CreateAsync 按 RunId+RequestId 幂等——重复创建同一 Tool 调用只保留一条记录。
    /// </summary>
    [TestMethod]
    public async Task Store_CreateAsync_IsIdempotentByRunAndRequestId()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var first = BuildRecord("rec-1", "req-1", "bank-transfer");
        var created = await store.CreateAsync(first, cts.Token);
        Assert.AreEqual("rec-1", created.ReconciliationId);

        // 同一 RunId+RequestId 重复创建 → 返回既有记录（不新增）
        var duplicate = await store.CreateAsync(first with { ReconciliationId = "rec-1-dup" }, cts.Token);
        Assert.AreEqual("rec-1", duplicate.ReconciliationId, "同一 Tool 调用只保留一条对账记录。");

        // 不同 RequestId → 独立记录
        var other = await store.CreateAsync(BuildRecord("rec-2", "req-2", "bank-transfer"), cts.Token);
        Assert.AreEqual("rec-2", other.ReconciliationId);

        var all = await store.ListByRunAsync(RunId, cts.Token);
        Assert.AreEqual(2, all.Count, "两条不同 RequestId 的记录应各自保留。");
    }

    /// <summary>
    /// 验证：HasUnresolvedForRunAsync 仅在存在 Pending/Running 记录时返回 true；
    /// 终态（Resolved/Rejected）不算未裁决；其他 Run 的记录不影响本 Run 判定。
    /// </summary>
    [TestMethod]
    public async Task Store_HasUnresolvedForRun_PendingAndRunningOnly()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);
        Assert.IsTrue(await store.HasUnresolvedForRunAsync(RunId, cts.Token), "Pending 记录 → 未裁决。");

        // 其他 Run 的记录不影响本 Run
        await store.CreateAsync(BuildRecord("rec-other", "req-other", "bank-transfer", runId: "run-other"), cts.Token);
        Assert.IsTrue(await store.HasUnresolvedForRunAsync(RunId, cts.Token));

        // 裁决为 Resolved → 不再未裁决
        await store.MarkResolvedAsync("rec-1", new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-1" }, cts.Token);
        Assert.IsFalse(await store.HasUnresolvedForRunAsync(RunId, cts.Token), "全部 Resolved → 无未裁决记录。");

        // Running 记录 → 未裁决
        await store.CreateAsync(BuildRecord("rec-2", "req-2", "bank-transfer"), cts.Token);
        await store.TryBeginAsync("rec-2", cts.Token);
        Assert.IsTrue(await store.HasUnresolvedForRunAsync(RunId, cts.Token), "Running 记录仍属未裁决。");

        // 裁决为 Rejected → 不再未裁决
        await store.MarkRejectedAsync("rec-2", new ToolReconciliationOutcome { SideEffectOccurred = false, Error = "未发生" }, cts.Token);
        Assert.IsFalse(await store.HasUnresolvedForRunAsync(RunId, cts.Token));
    }

    /// <summary>
    /// 验证：TryBeginAsync CAS Pending→Running（并发互斥），TryResetToPendingAsync 回退。
    /// </summary>
    [TestMethod]
    public async Task Store_TryBeginAndReset_CasSemantics()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);

        Assert.IsTrue(await store.TryBeginAsync("rec-1", cts.Token), "Pending → Running 首次接管成功。");
        Assert.IsFalse(await store.TryBeginAsync("rec-1", cts.Token), "Running 状态不可重复接管。");
        Assert.AreEqual(ToolReconciliationStatus.Running, (await store.GetAsync("rec-1", cts.Token))!.Status);

        Assert.IsTrue(await store.TryResetToPendingAsync("rec-1", cts.Token), "Running → Pending 回退成功。");
        Assert.IsFalse(await store.TryResetToPendingAsync("rec-1", cts.Token), "仅 Running 可回退。");
        Assert.AreEqual(ToolReconciliationStatus.Pending, (await store.GetAsync("rec-1", cts.Token))!.Status);

        // 终态记录不可接管
        await store.MarkResolvedAsync("rec-1", new ToolReconciliationOutcome { SideEffectOccurred = true }, cts.Token);
        Assert.IsFalse(await store.TryBeginAsync("rec-1", cts.Token), "终态记录不可重新接管。");
    }

    /// <summary>
    /// 验证：对不存在的记录执行 CAS 推进 → 抛 InvalidOperationException（fail-closed，不静默吞掉）。
    /// </summary>
    [TestMethod]
    public async Task Store_CasOnMissingRecord_Throws()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => store.TryBeginAsync("rec-missing", cts.Token).AsTask());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => store.MarkResolvedAsync("rec-missing", new ToolReconciliationOutcome { SideEffectOccurred = true }, cts.Token).AsTask());
    }

    /// <summary>
    /// 验证：终态裁决幂等——二次 Mark 返回 false 且不覆盖首次裁决内容。
    /// </summary>
    [TestMethod]
    public async Task Store_MarkTerminal_IsIdempotent()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);

        Assert.IsTrue(await store.MarkResolvedAsync(
            "rec-1", new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-777" }, cts.Token));
        Assert.IsFalse(await store.MarkResolvedAsync(
            "rec-1", new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-999" }, cts.Token),
            "已裁决记录二次裁决 → false。");
        Assert.IsFalse(await store.MarkRejectedAsync(
            "rec-1", new ToolReconciliationOutcome { SideEffectOccurred = false }, cts.Token));

        var record = await store.GetAsync("rec-1", cts.Token);
        Assert.AreEqual(ToolReconciliationStatus.Resolved, record!.Status);
        Assert.AreEqual("txn-777", record.Result, "首次裁决内容不被后续提交覆盖。");
        Assert.AreEqual(true, record.SideEffectOccurred);
    }

    /// <summary>
    /// 验证：ListPendingAsync 仅返回 Pending 记录且按创建时间升序。
    /// </summary>
    [TestMethod]
    public async Task Store_ListPending_OnlyPendingOrderedByCreatedAt()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "tool-a", createdAt: DateTimeOffset.UtcNow.AddSeconds(-5)), cts.Token);
        await store.CreateAsync(BuildRecord("rec-2", "req-2", "tool-b", createdAt: DateTimeOffset.UtcNow.AddSeconds(-3)), cts.Token);
        await store.CreateAsync(BuildRecord("rec-3", "req-3", "tool-c", createdAt: DateTimeOffset.UtcNow.AddSeconds(-1)), cts.Token);
        await store.MarkResolvedAsync("rec-2", new ToolReconciliationOutcome { SideEffectOccurred = true }, cts.Token);

        var pending = await store.ListPendingAsync(10, cts.Token);
        Assert.AreEqual(2, pending.Count, "仅 Pending 记录被列出。");
        Assert.AreEqual("rec-1", pending[0].ReconciliationId, "按创建时间升序。");
        Assert.AreEqual("rec-3", pending[1].ReconciliationId);
    }

    /// <summary>
    /// 验证（P2-B1 Control Plane）：按 ExternalOperationId 跨 Run 反查（InMemory 实现与 Postgres 同语义）。
    /// </summary>
    [TestMethod]
    public async Task Store_QueryByExternalOperationId_AcrossRuns()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "tool-a", runId: "run-a", createdAt: DateTimeOffset.UtcNow.AddSeconds(-3)) with { ExternalOperationId = "ext-op-9" }, cts.Token);
        await store.CreateAsync(BuildRecord("rec-2", "req-2", "tool-b", runId: "run-b", createdAt: DateTimeOffset.UtcNow.AddSeconds(-1)) with { ExternalOperationId = "ext-op-9" }, cts.Token);
        await store.CreateAsync(BuildRecord("rec-3", "req-3", "tool-c", runId: "run-c", createdAt: DateTimeOffset.UtcNow) with { ExternalOperationId = "ext-op-other" }, cts.Token);

        var matches = await store.QueryByExternalOperationIdAsync("ext-op-9", cts.Token);
        Assert.AreEqual(2, matches.Count, "应反查到两条同 externalOperationId 的记录（跨 Run）。");
        Assert.AreEqual("rec-2", matches[0].ReconciliationId, "跨 Run 反查按 CreatedAt 倒序（最新在前）。");
        Assert.AreEqual("rec-1", matches[1].ReconciliationId);

        var none = await store.QueryByExternalOperationIdAsync("ext-op-missing", cts.Token);
        Assert.AreEqual(0, none.Count, "未匹配的 externalOperationId 应返回空列表。");
    }

    /// <summary>
    /// 验证（P2-B1 Control Plane）：ControlRoom 分页列表 —— 过期高亮（DeadlineUtc &lt; now 且未裁决）
    /// + 告警计数（OverdueCount）+ OverdueOnly 过滤（InMemory 实现与 Postgres 同语义）。
    /// </summary>
    [TestMethod]
    public async Task Store_ControlRoomList_OverdueHighlight_AndAlertCount()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var now = DateTimeOffset.UtcNow;

        await store.CreateAsync(BuildRecord("rec-old-1", "req-old-1", "tool-a", createdAt: now.AddSeconds(-5)) with { DeadlineUtc = now - TimeSpan.FromHours(2) }, cts.Token);
        await store.CreateAsync(BuildRecord("rec-fresh", "req-fresh", "tool-b", createdAt: now.AddSeconds(-4)) with { DeadlineUtc = now + TimeSpan.FromHours(2) }, cts.Token);
        // 已裁决但过期：不计告警
        await store.CreateAsync(BuildRecord("rec-resolved", "req-resolved", "tool-c", createdAt: now.AddSeconds(-3)) with { DeadlineUtc = now - TimeSpan.FromHours(1) }, cts.Token);
        await store.MarkRejectedAsync("rec-resolved", new ToolReconciliationOutcome { SideEffectOccurred = false }, cts.Token);
        // 其他 Run 的同 workspace 记录不干扰分页（workspace 过滤）
        await store.CreateAsync(BuildRecord("rec-ws2", "req-ws2", "tool-d", runId: "run-ws2") with { WorkspaceId = "ws-other", DeadlineUtc = now - TimeSpan.FromHours(3) }, cts.Token);

        var all = await store.ListAsync(new ReconciliationQuery { WorkspaceId = Ws, Limit = 50 }, cts.Token);
        Assert.AreEqual(3, all.Total, "workspace 过滤应排除其他 workspace 记录。");
        Assert.AreEqual(1, all.OverdueCount, "告警计数 = 过期未决（Pending 且 deadline<now）1 条。");
        CollectionAssert.AreEquivalent(
            new[] { "rec-old-1", "rec-fresh", "rec-resolved" },
            all.Items.Select(r => r.ReconciliationId).ToList(),
            "列表应包含当前 workspace 全部条目。");

        var overdueOnly = await store.ListAsync(new ReconciliationQuery { WorkspaceId = Ws, OverdueOnly = true }, cts.Token);
        Assert.AreEqual(1, overdueOnly.Total, "OverdueOnly 应只返回过期未决记录。");
        Assert.AreEqual("rec-old-1", overdueOnly.Items.Single().ReconciliationId);

        var paged = await store.ListAsync(new ReconciliationQuery { WorkspaceId = Ws, Limit = 2, Offset = 1 }, cts.Token);
        Assert.AreEqual(2, paged.Items.Count, "分页 offset=1 limit=2 应返回 2 条。");
        Assert.AreEqual(3, paged.Total, "分页结果仍应携带过滤后总数。");
    }

    // ── 2. 对账协调器单元测试 ──────────────────────────────────────────────

    /// <summary>
    /// 验证：ResolveAsync 对不存在的记录返回 1。
    /// </summary>
    [TestMethod]
    public async Task Coordinator_Resolve_NotFound_ReturnsOne()
    {
        var store = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(store, journal: null, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var code = await coordinator.ResolveAsync("rec-missing", new ToolReconciliationOutcome { SideEffectOccurred = true }, cts.Token);
        Assert.AreEqual(1, code, "记录不存在 → 1。");
    }

    /// <summary>
    /// 验证：ResolveAsync 对已裁决记录返回 2（幂等冲突），不重复提交 journal。
    /// </summary>
    [TestMethod]
    public async Task Coordinator_Resolve_AlreadyTerminal_ReturnsTwo()
    {
        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, _) = CreateExecutor(handler);
        var store = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(store, journal, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("bank-transfer", "arg-A"), 0, cts.Token);
        await store.CreateAsync(BuildRecord("rec:" + result.RequestId, result.RequestId, "bank-transfer", result), cts.Token);

        Assert.AreEqual(0, await coordinator.ResolveAsync("rec:" + result.RequestId, new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-1" }, cts.Token));
        var code = await coordinator.ResolveAsync("rec:" + result.RequestId, new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-2" }, cts.Token);
        Assert.AreEqual(2, code, "已裁决记录 → 2。");

        var record = await store.GetAsync("rec:" + result.RequestId, cts.Token);
        Assert.AreEqual("txn-1", record!.Result, "首次裁决内容不被第二次提交覆盖。");
    }

    /// <summary>
    /// 验证：裁决"副作用已发生"→ journal 显式进入 Reconciling 后提交真相结果
    /// （Dispatched → Reconciling → Committed，Succeeded=true + result）；
    /// 记录 → Resolved；重放同一 Tool 调用返回提交的真相且不重复执行外部调用。
    /// </summary>
    [TestMethod]
    public async Task Coordinator_Resolve_SideEffectOccurred_CommitsTruthToJournal()
    {
        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, _) = CreateExecutor(handler);
        var store = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(store, journal, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // 非幂等写成功也不自动提交 → journal 停在 Dispatched（模糊态）
        var toolCall = BuildToolCall("bank-transfer", "arg-B");
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState, "非幂等写成功不得自动提交。");
        Assert.AreEqual(1, handler.InvocationCount);

        await store.CreateAsync(BuildRecord("rec:" + result.RequestId, result.RequestId, "bank-transfer", result), cts.Token);

        // 人工/自动裁决：外部系统确认转账已发生
        var code = await coordinator.ResolveAsync(
            "rec:" + result.RequestId,
            new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-777" },
            cts.Token);
        Assert.AreEqual(0, code);

        // journal 已提交真相结果
        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State, "对账确认已发生 → journal Committed。");

        // 记录终态
        var record = await store.GetAsync("rec:" + result.RequestId, cts.Token);
        Assert.AreEqual(ToolReconciliationStatus.Resolved, record!.Status);
        Assert.AreEqual("txn-777", record.Result);
        Assert.AreEqual(true, record.SideEffectOccurred);

        // 重放同一调用（同 runId + modelTurn=0 → 同一 RequestId）→ 返回提交的真相，绝不重复执行
        var replay = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);
        Assert.IsTrue(replay.Succeeded, "对账提交的结果对重放可见。");
        Assert.AreEqual("txn-777", replay.Result, "重放返回对账确认的外部真相结果。");
        Assert.AreEqual(ToolDispatchState.Committed, replay.JournalState);
        Assert.AreEqual(1, handler.InvocationCount, "对账已确认真相 → 外部调用不得重复执行。");
    }

    /// <summary>
    /// 验证：裁决"副作用未发生"→ journal 提交 void 失败结果（Succeeded=false），
    /// 记录 → Rejected（禁止重放）；重放返回该 void 结果，不重复执行外部调用。
    /// </summary>
    [TestMethod]
    public async Task Coordinator_Resolve_SideEffectNotOccurred_CommitsVoidResult()
    {
        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, _) = CreateExecutor(handler);
        var store = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(store, journal, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var toolCall = BuildToolCall("bank-transfer", "arg-C");
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);
        await store.CreateAsync(BuildRecord("rec:" + result.RequestId, result.RequestId, "bank-transfer", result), cts.Token);

        var code = await coordinator.ResolveAsync(
            "rec:" + result.RequestId,
            new ToolReconciliationOutcome { SideEffectOccurred = false, Error = "外部系统确认转账未发生" },
            cts.Token);
        Assert.AreEqual(0, code);

        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State, "未发生 → journal 提交 void 结果后 Committed。");

        var record = await store.GetAsync("rec:" + result.RequestId, cts.Token);
        Assert.AreEqual(ToolReconciliationStatus.Rejected, record!.Status, "未发生 → 记录 Rejected。");
        Assert.AreEqual(false, record.SideEffectOccurred);

        // 重放 → 返回 void 失败结果（模型看到失败可调整策略），不重新执行
        var replay = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);
        Assert.IsFalse(replay.Succeeded, "重放返回 void 失败结果。");
        Assert.AreEqual(ToolDispatchState.Committed, replay.JournalState);
        Assert.AreEqual(1, handler.InvocationCount, "未发生 → 禁止重放外部调用。");
    }

    /// <summary>
    /// 验证：ReconcileRecordAsync（Worker 路径）——TryBegin 接管 → Handler 确认 → 提交裁决。
    /// </summary>
    [TestMethod]
    public async Task Coordinator_ReconcileRecord_HandlerSuccess_CommitsOutcome()
    {
        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, _) = CreateExecutor(handler);
        var store = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(store, journal, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("bank-transfer", "arg-D"), 0, cts.Token);
        var record = await store.CreateAsync(BuildRecord("rec:" + result.RequestId, result.RequestId, "bank-transfer", result), cts.Token);

        var reconHandler = new FakeReconciliationHandler("bank-recon", new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-888" });
        await coordinator.ReconcileRecordAsync(record, reconHandler, cts.Token);

        Assert.AreEqual(ToolReconciliationStatus.Resolved, (await store.GetAsync(record.ReconciliationId, cts.Token))!.Status);
        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State);
        Assert.AreEqual(1, reconHandler.InvocationCount);
    }

    /// <summary>
    /// 验证：ReconcileRecordAsync 的 Handler 抛异常 → 记录回退 Pending（下轮重试），
    /// 异常向上传播，journal 保持 Dispatched 不被污染。
    /// </summary>
    [TestMethod]
    public async Task Coordinator_ReconcileRecord_HandlerThrows_ResetsToPending()
    {
        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, _) = CreateExecutor(handler);
        var store = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(store, journal, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("bank-transfer", "arg-E"), 0, cts.Token);
        var record = await store.CreateAsync(BuildRecord("rec:" + result.RequestId, result.RequestId, "bank-transfer", result), cts.Token);

        var reconHandler = new FakeReconciliationHandler("bank-recon", new ToolReconciliationOutcome { SideEffectOccurred = true }, throwException: true);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.ReconcileRecordAsync(record, reconHandler, cts.Token));

        Assert.AreEqual(ToolReconciliationStatus.Pending, (await store.GetAsync(record.ReconciliationId, cts.Token))!.Status,
            "Handler 异常 → 记录回退 Pending 等待重试。");
        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State, "对账失败不得污染 journal 状态。");
    }

    // ── 3. Worker 轮询对账测试 ─────────────────────────────────────────────

    /// <summary>
    /// 验证：ToolReconciliationWorker 单轮轮询按 Handler 名称匹配对账 Pending 记录：
    /// 匹配到 Handler → 记录 Resolved + journal Committed；未匹配 → 保持 Pending 等待人工裁决。
    /// </summary>
    [TestMethod]
    public async Task Worker_ReconcileOnce_MatchesHandlerAndKeepsUnmatchedPending()
    {
        var runStore = new InMemoryAgentRunStore();
        var run = BuildRun("worker 对账验证");
        await runStore.CreateAsync(run);

        var toolHandler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, _) = CreateExecutor(toolHandler);
        var store = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(store, journal, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // 两条 Pending 记录：一条声明 handler，一条未声明
        var withHandler = await executor.ExecuteAsync(run.RunId, Ws, BuildToolCall("bank-transfer", "arg-F"), 0, cts.Token);
        var manualOnly = await executor.ExecuteAsync(run.RunId, Ws, BuildToolCall("bank-transfer", "arg-G"), 0, cts.Token);
        var recWithHandler = await store.CreateAsync(BuildRecord("rec:" + withHandler.RequestId, withHandler.RequestId, "bank-transfer", withHandler), cts.Token);
        var recManual = await store.CreateAsync(BuildRecord("rec:" + manualOnly.RequestId, manualOnly.RequestId, "bank-transfer", manualOnly, reconciliationHandler: null), cts.Token);

        var worker = new ToolReconciliationWorker(
            coordinator,
            store,
            new[] { new FakeReconciliationHandler("bank-recon", new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-999" }) },
            kernelHost: null,
            runStore,
            new ContextCoreRuntimeOptions(),
            NullLogger<ToolReconciliationWorker>.Instance);

        await InvokeReconcileOnceAsync(worker, cts.Token);

        // 匹配到 Handler → Resolved + journal Committed
        Assert.AreEqual(ToolReconciliationStatus.Resolved, (await store.GetAsync(recWithHandler.ReconciliationId, cts.Token))!.Status);
        Assert.AreEqual(ToolDispatchState.Committed, (await journal.GetEntryAsync(withHandler.RequestId, cts.Token))!.State);

        // 未匹配 Handler → 保持 Pending + journal 保持 Dispatched
        Assert.AreEqual(ToolReconciliationStatus.Pending, (await store.GetAsync(recManual.ReconciliationId, cts.Token))!.Status,
            "无匹配 Handler 的记录保持 Pending 等待人工裁决。");
        Assert.AreEqual(ToolDispatchState.Dispatched, (await journal.GetEntryAsync(manualOnly.RequestId, cts.Token))!.State);
    }

    /// <summary>
    /// 验证：Worker 公开 ResolveAsync 委托协调器（人工裁决入口）。
    /// </summary>
    [TestMethod]
    public async Task Worker_ResolveAsync_DelegatesToCoordinator()
    {
        var runStore = new InMemoryAgentRunStore();
        var run = BuildRun("worker resolve 委托验证");
        await runStore.CreateAsync(run);

        var toolHandler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, _) = CreateExecutor(toolHandler);
        var store = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(store, journal, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await executor.ExecuteAsync(run.RunId, Ws, BuildToolCall("bank-transfer", "arg-H"), 0, cts.Token);
        await store.CreateAsync(BuildRecord("rec:" + result.RequestId, result.RequestId, "bank-transfer", result), cts.Token);

        var worker = new ToolReconciliationWorker(
            coordinator, store, handlers: null, kernelHost: null, runStore,
            new ContextCoreRuntimeOptions(), NullLogger<ToolReconciliationWorker>.Instance);

        var code = await worker.ResolveAsync("rec:" + result.RequestId, new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-manual" }, cts.Token);
        Assert.AreEqual(0, code);
        Assert.AreEqual(ToolReconciliationStatus.Resolved, (await store.GetAsync("rec:" + result.RequestId, cts.Token))!.Status);
        Assert.AreEqual(ToolDispatchState.Committed, (await journal.GetEntryAsync(result.RequestId, cts.Token))!.State);
    }

    // ── 4. Actor 集成测试（P0-3 核心验收）──────────────────────────────────

    /// <summary>
    /// 验证：存在未裁决对账记录时，模型产出最终答案也不会进入 Completed——
    /// Run 停车在 AwaitingReconciliation（等待 Worker/人工裁决），记录 Pending，journal Dispatched。
    /// </summary>
    [TestMethod]
    public async Task Actor_UnresolvedTool_BlocksCompleted_StopsAtAwaitingReconciliation()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("给 Alice 转账 100 元");
        await runStore.CreateAsync(run);

        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (dispatcher, executor, journal, _) = CreateExecutor(handler);
        var reconciliationStore = new InMemoryToolReconciliationStore();

        // 第 1 次模型调用请求转账工具（成功但非幂等写 → 不自动提交），第 2 次返回最终答案
        var transport = new SequenceModelTransport(new[]
        {
            new AgentModelResponse
            {
                Content = "需要调用转账工具",
                ToolCalls = new[] { new AgentToolCallRequest { ToolName = "bank-transfer", Arguments = "{\"to\":\"Alice\",\"amount\":100}", ToolCallId = "tc-1" } },
                IsFinalAnswer = false,
                TokensConsumed = 5,
                Duration = TimeSpan.FromMilliseconds(1)
            },
            new AgentModelResponse
            {
                Content = "转账处理中",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 5,
                Duration = TimeSpan.FromMilliseconds(1)
            }
        });

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            dispatcher,
            durableToolExecutor: executor,
            reconciliationStore: reconciliationStore);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(run, cts.Token);

        // Run 不得进入 Completed——停车在 AwaitingReconciliation
        var stored = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(stored);
        Assert.AreEqual(AgentRunState.AwaitingReconciliation, stored!.State,
            "存在未裁决对账记录 → Run 不得进入 Completed。");

        // 对账记录已创建（Pending + 声明 Handler + 外部操作 ID）
        var records = await reconciliationStore.ListByRunAsync(run.RunId, cts.Token);
        Assert.AreEqual(1, records.Count, "模糊态 Tool 应创建一条对账记录。");
        var record = records[0];
        Assert.AreEqual(ToolReconciliationStatus.Pending, record.Status);
        Assert.AreEqual("bank-transfer", record.ToolName);
        Assert.AreEqual("bank-recon", record.ReconciliationHandler, "Descriptor 声明的对账 Handler 回传到记录。");
        Assert.IsFalse(string.IsNullOrEmpty(record.ExternalOperationId), "记录携带外部操作 ID 供对账查询。");

        // journal 保持 Dispatched（模糊态）
        var entry = await journal.GetEntryAsync(record.RequestId, cts.Token);
        Assert.IsNotNull(entry);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State, "外部副作用已执行但未提交 → journal Dispatched。");

        // 外部调用已执行一次，但 Run 未完成（等待对账）
        Assert.AreEqual(1, handler.InvocationCount, "外部副作用已执行一次（结果未知，等待对账确认）。");

        // 事件流中存在指向 AwaitingReconciliation 的状态转换
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        Assert.IsTrue(events.Any(e =>
            e.EventType == AgentRunEventType.StateTransition
            && e.Payload.Contains("\"to\":\"AwaitingReconciliation\"", StringComparison.Ordinal)),
            "事件流应包含到 AwaitingReconciliation 的状态转换事件。");
    }

    /// <summary>
    /// 验证：全部对账裁决后恢复执行 → Run 进入 Completed，外部调用全程只执行一次。
    /// </summary>
    [TestMethod]
    public async Task Actor_ResolvedTool_ResumesAndCompletes()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("给 Alice 转账 100 元");
        await runStore.CreateAsync(run);

        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (dispatcher, executor, journal, _) = CreateExecutor(handler);
        var reconciliationStore = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(reconciliationStore, journal, NullLogger<ToolReconciliationCoordinator>.Instance);

        // 阶段 1：工具调用（非幂等写成功 → Hold）+ 最终答案 → 停在 AwaitingReconciliation
        var phase1Transport = new SequenceModelTransport(new[]
        {
            new AgentModelResponse
            {
                Content = "需要调用转账工具",
                ToolCalls = new[] { new AgentToolCallRequest { ToolName = "bank-transfer", Arguments = "{\"to\":\"Alice\",\"amount\":100}", ToolCallId = "tc-1" } },
                IsFinalAnswer = false,
                TokensConsumed = 5,
                Duration = TimeSpan.FromMilliseconds(1)
            },
            new AgentModelResponse
            {
                Content = "转账处理中",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 5,
                Duration = TimeSpan.FromMilliseconds(1)
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var actor = new AgentRunActor(
            runStore, eventStore, phase1Transport,
            new DefaultAgentLoopPolicy(),
            dispatcher,
            durableToolExecutor: executor,
            reconciliationStore: reconciliationStore);
        await actor.ExecuteAsync(run, cts.Token);

        var parked = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.AreEqual(AgentRunState.AwaitingReconciliation, parked!.State, "阶段 1 应停车在 AwaitingReconciliation。");

        var records = await reconciliationStore.ListByRunAsync(run.RunId, cts.Token);
        Assert.AreEqual(1, records.Count);

        // 阶段 2：裁决"外部副作用已发生"（模拟 Worker/人工 resolve 端点）
        var code = await coordinator.ResolveAsync(
            records[0].ReconciliationId,
            new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-777" },
            cts.Token);
        Assert.AreEqual(0, code);
        Assert.AreEqual(ToolReconciliationStatus.Resolved, (await reconciliationStore.GetAsync(records[0].ReconciliationId, cts.Token))!.Status);
        Assert.AreEqual(ToolDispatchState.Committed, (await journal.GetEntryAsync(records[0].RequestId, cts.Token))!.State);

        // 阶段 3：恢复执行（Worker 重新入队 → 新 Actor 从事件流恢复）
        var resumeTransport = new SequenceModelTransport(new[]
        {
            new AgentModelResponse
            {
                Content = "转账已完成",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 5,
                Duration = TimeSpan.FromMilliseconds(1)
            }
        });
        var resumeActor = new AgentRunActor(
            runStore, eventStore, resumeTransport,
            new DefaultAgentLoopPolicy(),
            dispatcher,
            durableToolExecutor: executor,
            reconciliationStore: reconciliationStore);
        await resumeActor.ExecuteAsync(parked, cts.Token);

        // 全部对账已裁决 → Run 进入 Completed
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State, "对账全部完成后恢复执行 → Completed。");
        Assert.IsNotNull(finalRun.FinishedAt, "Completed 终态应设置 FinishedAt。");

        // 外部调用全程只执行一次（对账确认真相后绝不重放）
        Assert.AreEqual(1, handler.InvocationCount, "对账确认真相后恢复执行不得重放外部调用。");

        // journal 保留对账提交的真相结果
        var entry = await journal.GetEntryAsync(records[0].RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State);
    }

    /// <summary>
    /// 验证：裁决"副作用未发生"（Rejected）后恢复执行 → Run 进入 Completed，
    /// 外部调用不重放，模型侧可见失败结果（void）。
    /// </summary>
    [TestMethod]
    public async Task Actor_RejectedTool_ResumesAndCompletes()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("给 Alice 转账 100 元");
        await runStore.CreateAsync(run);

        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (dispatcher, executor, journal, _) = CreateExecutor(handler);
        var reconciliationStore = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(reconciliationStore, journal, NullLogger<ToolReconciliationCoordinator>.Instance);

        var phase1Transport = new SequenceModelTransport(new[]
        {
            new AgentModelResponse
            {
                Content = "需要调用转账工具",
                ToolCalls = new[] { new AgentToolCallRequest { ToolName = "bank-transfer", Arguments = "{\"to\":\"Alice\",\"amount\":100}", ToolCallId = "tc-1" } },
                IsFinalAnswer = false,
                TokensConsumed = 5,
                Duration = TimeSpan.FromMilliseconds(1)
            },
            new AgentModelResponse
            {
                Content = "转账处理中",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 5,
                Duration = TimeSpan.FromMilliseconds(1)
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var actor = new AgentRunActor(
            runStore, eventStore, phase1Transport,
            new DefaultAgentLoopPolicy(),
            dispatcher,
            durableToolExecutor: executor,
            reconciliationStore: reconciliationStore);
        await actor.ExecuteAsync(run, cts.Token);

        var parked = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.AreEqual(AgentRunState.AwaitingReconciliation, parked!.State);

        var records = await reconciliationStore.ListByRunAsync(run.RunId, cts.Token);
        Assert.AreEqual(1, records.Count);

        // 裁决：外部系统确认转账未发生 → Rejected（禁止重放）
        var code = await coordinator.ResolveAsync(
            records[0].ReconciliationId,
            new ToolReconciliationOutcome { SideEffectOccurred = false, Error = "外部系统确认转账未发生" },
            cts.Token);
        Assert.AreEqual(0, code);
        Assert.AreEqual(ToolReconciliationStatus.Rejected, (await reconciliationStore.GetAsync(records[0].ReconciliationId, cts.Token))!.Status);
        Assert.AreEqual(ToolDispatchState.Committed, (await journal.GetEntryAsync(records[0].RequestId, cts.Token))!.State);

        // 恢复执行 → Completed
        var resumeTransport = new SequenceModelTransport(new[]
        {
            new AgentModelResponse
            {
                Content = "转账失败，已告知用户",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 5,
                Duration = TimeSpan.FromMilliseconds(1)
            }
        });
        var resumeActor = new AgentRunActor(
            runStore, eventStore, resumeTransport,
            new DefaultAgentLoopPolicy(),
            dispatcher,
            durableToolExecutor: executor,
            reconciliationStore: reconciliationStore);
        await resumeActor.ExecuteAsync(parked, cts.Token);

        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State, "Rejected 裁决后恢复执行 → Completed。");
        Assert.AreEqual(1, handler.InvocationCount, "Rejected 禁止重放外部调用。");
    }

    /// <summary>
    /// 验证：未注入对账存储时（旧行为），模糊态 Tool 不阻塞 Completed——
    /// 约束仅在对账存储可用时生效（journal 自身仍保证模糊态不被重放）。
    /// </summary>
    [TestMethod]
    public async Task Actor_NoReconciliationStore_CompletesWithoutConstraint()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("无对账存储时行为验证");
        await runStore.CreateAsync(run);

        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (dispatcher, executor, _, _) = CreateExecutor(handler);

        var transport = new SequenceModelTransport(new[]
        {
            new AgentModelResponse
            {
                Content = "需要调用转账工具",
                ToolCalls = new[] { new AgentToolCallRequest { ToolName = "bank-transfer", Arguments = "{}", ToolCallId = "tc-1" } },
                IsFinalAnswer = false,
                TokensConsumed = 5,
                Duration = TimeSpan.FromMilliseconds(1)
            },
            new AgentModelResponse
            {
                Content = "完成",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 5,
                Duration = TimeSpan.FromMilliseconds(1)
            }
        });

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            dispatcher,
            durableToolExecutor: executor);
        // 注意：不注入 reconciliationStore

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(run, cts.Token);

        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            "未注入对账存储 → 不启用“未裁决不完成”约束（兼容旧部署）。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = Ws,
        SessionId = "session-recon",
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 10
        }
    };

    private static ToolReconciliationRecord BuildRecord(
        string reconciliationId,
        string requestId,
        string toolName,
        ToolExecutionResult? result = null,
        string runId = RunId,
        string? reconciliationHandler = "bank-recon",
        DateTimeOffset? createdAt = null) => new()
        {
            ReconciliationId = reconciliationId,
            RunId = runId,
            WorkspaceId = Ws,
            RequestId = requestId,
            ToolName = toolName,
            ExternalOperationId = result?.ExternalOperationId,
            ReconciliationHandler = reconciliationHandler,
            Status = ToolReconciliationStatus.Pending,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    /// <summary>可声明副作用 / 对账 Handler 的 Tool Handler（记录外部调用次数）。</summary>
    private static ReconTestHandler CreateHandler(
        string toolName,
        ToolSideEffect sideEffect,
        string? reconciliationHandler,
        bool succeed = true) => new(toolName, sideEffect, reconciliationHandler, succeed);

    private static (RealToolDispatcher Dispatcher, DefaultDurableToolExecutor Executor, InMemoryToolDispatchJournal Journal, InMemoryDurableToolResultStore ResultStore) CreateExecutor(
        ReconTestHandler handler)
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
        IdempotencyKey = "idem-recon",
        ToolCallId = $"toolcall-{toolName}-0"
    };

    /// <summary>通过反射调用 Worker 的私有单轮对账方法（BackgroundService 无公开触发入口）。</summary>
    private static Task InvokeReconcileOnceAsync(ToolReconciliationWorker worker, CancellationToken ct)
    {
        var method = typeof(ToolReconciliationWorker).GetMethod(
            "ReconcileOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("未找到 ReconcileOnceAsync。");
        return (Task)method.Invoke(worker, new object[] { ct })!;
    }

    /// <summary>按顺序返回预设响应序列的 IAgentModelTransport stub（超出序列返回最后一个）。</summary>
    private sealed class SequenceModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse[] _responses;
        private int _callCount;

        public SequenceModelTransport(AgentModelResponse[] responses)
        {
            _responses = responses;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            var response = index < _responses.Length ? _responses[index] : _responses[^1];
            return ValueTask.FromResult(response);
        }

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);
    }

    /// <summary>可配置的 Tool Handler：可声明副作用类型与对账 Handler 名，统计外部调用次数。</summary>
    private sealed class ReconTestHandler : IToolHandler
    {
        private int _invocationCount;

        public ReconTestHandler(string toolName, ToolSideEffect sideEffect, string? reconciliationHandler, bool succeed)
        {
            ToolName = toolName;
            SideEffect = sideEffect;
            ReconciliationHandlerName = reconciliationHandler;
            Succeed = succeed;
        }

        public string ToolName { get; }
        public ToolSideEffect SideEffect { get; }
        public string? ReconciliationHandlerName { get; }
        public bool Succeed { get; }
        public string? Description => $"Test tool: {ToolName}";
        public string? ParametersJsonSchema => "{}";
        public ToolDescriptor Descriptor => new()
        {
            Name = ToolName,
            DeclaredSideEffect = SideEffect,
            ReconciliationHandler = ReconciliationHandlerName
        };
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult(Succeed
                ? new ToolHandlerResult { Succeeded = true, Result = "external-txn", SideEffect = SideEffect }
                : new ToolHandlerResult { Succeeded = false, Error = "simulated-failure", SideEffect = SideEffect });
        }
    }

    /// <summary>可配置的 IToolReconciliationHandler stub：返回预设真相或抛异常。</summary>
    private sealed class FakeReconciliationHandler : IToolReconciliationHandler
    {
        private readonly ToolReconciliationOutcome _outcome;
        private readonly bool _throwException;
        private int _invocationCount;

        public FakeReconciliationHandler(string handlerName, ToolReconciliationOutcome outcome, bool throwException = false)
        {
            HandlerName = handlerName;
            _outcome = outcome;
            _throwException = throwException;
        }

        public string HandlerName { get; }

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<ToolReconciliationOutcome> ReconcileAsync(
            ToolReconciliationRecord record,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            if (_throwException)
            {
                throw new InvalidOperationException("handler-simulated-failure");
            }
            return ValueTask.FromResult(_outcome);
        }
    }
}
