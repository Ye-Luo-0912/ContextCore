using System.Reflection;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

// ===========================================================================
// R30X — Tool 对账裁决租约 + 原子裁决测试（P0-3 / P0-4 / P0-5）
//
// P0-4：对账 Running 必须有租约（lease_owner / lease_token / lease_expires_at /
// fencing_token / attempt_count / next_attempt_at / last_error）——
// - TryBeginAsync 领取裁决租约（Pending → Running），有效租约持有期间不可重复接管；
// - 租约过期后 ListPendingAsync 重新取回、TryBeginAsync 可接管（fencing 递增隔离旧持有者）；
// - RenewLeaseAsync / TryResetToPendingAsync / MarkResolvedAsync / MarkRejectedAsync
// 全部校验 lease_token + 未过期（无效租约 → false）。
//
// P0-3：ResolveReconciliationAtomicallyAsync 单事务原子裁决——
// journal 推进 + Durable Result UPSERT + 记录终态 + Run 推进 + 审计事件整体成功/回滚；
// 唯一裁决者校验（租约 + fencing）失败时返回 ArbitrationLost / VersionMismatch。
//
// P0-5：先原子取得裁决权（租约）再提交 Journal——仲裁权被占用时
// ToolReconciliationCoordinator.ResolveAsync 返回 3，人工裁决不污染自动 Handler 的 Journal。
// ===========================================================================

[TestClass]
[TestCategory("Kill-Point")]
[TestCategory("External-Effect-Truth")]
public sealed class R30X_ReconciliationLeaseAtomicTests
{
    private const string Ws = "ws-recon-lease";
    private const string RunId = "run-recon-lease";

    // ── 1. 裁决租约（P0-4）──────────────────────────────────────────────

    /// <summary>验证：TryBeginAsync 领取租约——返回 token/fencing/expiry，记录进入 Running 并写入租约字段。</summary>
    [TestMethod]
    public async Task Store_TryBegin_AcquiresLease_WithFencingAndAttempt()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);

        var lease = await store.TryBeginAsync("rec-1", "worker:test", TimeSpan.FromMinutes(5), cts.Token);
        Assert.IsNotNull(lease, "Pending → Running 领取租约成功。");
        Assert.IsFalse(string.IsNullOrEmpty(lease!.LeaseToken), "租约令牌非空。");
        Assert.AreEqual(1, lease.FencingToken, "首次领取 fencing=1。");
        Assert.IsTrue(lease.ExpiresAt > DateTimeOffset.UtcNow, "租约过期时间在未来。");

        var record = await store.GetAsync("rec-1", cts.Token);
        Assert.AreEqual(ToolReconciliationStatus.Running, record!.Status);
        Assert.AreEqual("worker:test", record.LeaseOwner);
        Assert.AreEqual(lease.LeaseToken, record.LeaseToken);
        Assert.AreEqual(lease.ExpiresAt, record.LeaseExpiresAt);
        Assert.AreEqual(1, record.FencingToken);
        Assert.AreEqual(1, record.AttemptCount, "首次领取 attempt_count=1。");
    }

    /// <summary>验证：有效租约持有期间不可重复接管（并发互斥）。</summary>
    [TestMethod]
    public async Task Store_TryBegin_ActiveLease_NotReclaimable()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);
        var lease = await store.TryBeginAsync("rec-1", "worker-a", TimeSpan.FromMinutes(5), cts.Token);
        Assert.IsNotNull(lease);

        var second = await store.TryBeginAsync("rec-1", "worker-b", TimeSpan.FromMinutes(5), cts.Token);
        Assert.IsNull(second, "有效租约持有期间不可被其他 Worker 接管。");

        var record = await store.GetAsync("rec-1", cts.Token);
        Assert.AreEqual("worker-a", record!.LeaseOwner, "租约持有者不变。");
        Assert.AreEqual(1, record.FencingToken, "fencing 不被无效接管推进。");
    }

    /// <summary>验证：租约过期后 TryBeginAsync 可接管（P0-4 崩溃恢复），fencing/attempt 递增隔离旧持有者。</summary>
    [TestMethod]
    public async Task Store_TryBegin_ExpiredLease_Reclaimable_FencingIncrements()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);

        var lease1 = await store.TryBeginAsync("rec-1", "worker-a", TimeSpan.FromMilliseconds(1), cts.Token);
        Assert.IsNotNull(lease1, "首次接管成功。");
        await Task.Delay(50, cts.Token); // 等待租约过期

        var lease2 = await store.TryBeginAsync("rec-1", "worker-b", TimeSpan.FromMinutes(5), cts.Token);
        Assert.IsNotNull(lease2, "过期租约可被接管（Worker 崩溃恢复）。");
        Assert.AreEqual(2, lease2!.FencingToken, "接管后 fencing 递增隔离旧持有者。");
        Assert.AreNotEqual(lease1!.LeaseToken, lease2.LeaseToken, "新租约令牌不同。");

        var record = await store.GetAsync("rec-1", cts.Token);
        Assert.AreEqual("worker-b", record!.LeaseOwner);
        Assert.AreEqual(2, record.AttemptCount, "接管计为第二次尝试。");
    }

    /// <summary>验证：ListPendingAsync 重新取回过期 Running（P0-4），跳过有效 Running。</summary>
    [TestMethod]
    public async Task Store_ListPending_RepicksExpiredRunning_SkipsActiveRunning()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-active", "req-a", "tool-a"), cts.Token);
        await store.CreateAsync(BuildRecord("rec-expired", "req-b", "tool-b"), cts.Token);
        await store.CreateAsync(BuildRecord("rec-pending", "req-c", "tool-c"), cts.Token);

        var active = await store.TryBeginAsync("rec-active", "worker-a", TimeSpan.FromMinutes(5), cts.Token);
        Assert.IsNotNull(active);
        var expired = await store.TryBeginAsync("rec-expired", "worker-b", TimeSpan.FromMilliseconds(1), cts.Token);
        Assert.IsNotNull(expired);
        await Task.Delay(50, cts.Token); // 等待 rec-expired 租约过期

        var pending = await store.ListPendingAsync(10, cts.Token);
        var ids = pending.Select(r => r.ReconciliationId).ToList();
        Assert.IsTrue(ids.Contains("rec-pending"), "Pending 记录始终被列出。");
        Assert.IsTrue(ids.Contains("rec-expired"), "过期 Running 记录被重新取回（崩溃恢复）。");
        Assert.IsFalse(ids.Contains("rec-active"), "有效 Running 记录不被列出。");
    }

    /// <summary>验证：RenewLeaseAsync 校验 lease_token + 未过期（P0-4 心跳）。</summary>
    [TestMethod]
    public async Task Store_RenewLease_ValidatesTokenAndExpiry()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);
        var lease = await store.TryBeginAsync("rec-1", "worker-a", TimeSpan.FromMinutes(5), cts.Token);
        Assert.IsNotNull(lease);

        Assert.IsFalse(await store.RenewLeaseAsync("rec-1", "wrong-token", TimeSpan.FromMinutes(5), cts.Token),
            "错误 token 续租失败。");
        Assert.IsTrue(await store.RenewLeaseAsync("rec-1", lease!.LeaseToken, TimeSpan.FromMinutes(5), cts.Token),
            "正确 token 续租成功。");

        // 过期后续租失败
        await store.CreateAsync(BuildRecord("rec-2", "req-2", "bank-transfer"), cts.Token);
        var shortLease = await store.TryBeginAsync("rec-2", "worker-a", TimeSpan.FromMilliseconds(1), cts.Token);
        Assert.IsNotNull(shortLease);
        await Task.Delay(50, cts.Token);
        Assert.IsFalse(await store.RenewLeaseAsync("rec-2", shortLease!.LeaseToken, TimeSpan.FromMinutes(5), cts.Token),
            "过期租约续租失败。");
    }

    /// <summary>验证：RenewHeartbeatBatchAsync 单次往返批量续约——仅 token 匹配且未过期的 Running 记录被续约。</summary>
    [TestMethod]
    public async Task Store_RenewHeartbeatBatch_OnlyRenewsMatchingToken()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);
        await store.CreateAsync(BuildRecord("rec-2", "req-2", "bank-transfer"), cts.Token);
        await store.CreateAsync(BuildRecord("rec-3", "req-3", "bank-transfer"), cts.Token);
        var lease1 = await store.TryBeginAsync("rec-1", "worker-a", TimeSpan.FromMinutes(5), cts.Token);
        var lease2 = await store.TryBeginAsync("rec-2", "worker-b", TimeSpan.FromMinutes(1), cts.Token);
        Assert.IsNotNull(lease1);
        Assert.IsNotNull(lease2);

        var failed = await store.RenewHeartbeatBatchAsync(
            new[]
            {
                new ToolReconciliationHeartbeat { ReconciliationId = "rec-1", LeaseToken = lease1!.LeaseToken },
                new ToolReconciliationHeartbeat { ReconciliationId = "rec-2", LeaseToken = "wrong-token" },
                new ToolReconciliationHeartbeat { ReconciliationId = "rec-3", LeaseToken = "no-lease" }
            },
            TimeSpan.FromMinutes(5),
            cts.Token);

        CollectionAssert.AreEquivalent(new[] { "rec-2", "rec-3" }, failed.ToList(),
            "仅 token 匹配且持有有效租约的记录被续约，其余返回失败。");
        var expired1 = (await store.GetAsync("rec-1", cts.Token))!.LeaseExpiresAt!.Value;
        Assert.IsTrue(expired1 > DateTimeOffset.UtcNow.AddMinutes(4), "rec-1 租约被延长。");
    }

    /// <summary>验证：TryResetToPendingAsync 必须持有有效租约；成功后携带 last_error + 退避。</summary>
    [TestMethod]
    public async Task Store_TryResetToPending_RequiresValidLease_SetsBackoff()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);

        // 未领取租约直接回退 → false
        Assert.IsFalse(await store.TryResetToPendingAsync("rec-1", "no-lease", "err", null, cts.Token),
            "无租约不可回退。");

        var lease = await store.TryBeginAsync("rec-1", "worker-a", TimeSpan.FromMinutes(5), cts.Token);
        Assert.IsNotNull(lease);
        Assert.IsTrue(await store.TryResetToPendingAsync("rec-1", lease!.LeaseToken, "handler-failed", TimeSpan.FromSeconds(30), cts.Token),
            "有效租约回退成功。");

        var record = await store.GetAsync("rec-1", cts.Token);
        Assert.AreEqual(ToolReconciliationStatus.Pending, record!.Status);
        Assert.AreEqual("handler-failed", record.LastError, "回退记录 last_error。");
        Assert.IsNotNull(record.NextAttemptAt, "回退设置退避时间。");
        Assert.IsTrue(record.NextAttemptAt > DateTimeOffset.UtcNow, "退避时间在未来。");
        Assert.IsNull(record.LeaseToken, "回退清除租约。");
    }

    /// <summary>验证：MarkResolved/MarkRejected 必须持有有效租约（P0-4）。</summary>
    [TestMethod]
    public async Task Store_MarkTerminal_RequiresValidLease()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);

        Assert.IsFalse(await store.MarkResolvedAsync("rec-1", "wrong-token", new ToolReconciliationOutcome { SideEffectOccurred = true }, cts.Token),
            "无有效租约不可裁决。");

        var lease = await store.TryBeginAsync("rec-1", "worker-a", TimeSpan.FromMinutes(5), cts.Token);
        Assert.IsNotNull(lease);
        Assert.IsTrue(await store.MarkResolvedAsync("rec-1", lease!.LeaseToken, new ToolReconciliationOutcome { SideEffectOccurred = true }, cts.Token),
            "有效租约可裁决。");
        Assert.IsNull((await store.GetAsync("rec-1", cts.Token))!.LeaseToken, "终态清除租约。");
    }

    // ── 2. 原子裁决（P0-3）──────────────────────────────────────────────

    /// <summary>
    /// 验证：ResolveReconciliationAtomicallyAsync 单事务完成全链路——
    /// journal Dispatched → Committed、Durable Result UPSERT、记录 → Resolved（租约清除）、
    /// Run 状态推进（停车态 → 目标态）、ToolReconciliationResolved 审计事件追加（哈希链完整）。
    /// </summary>
    [TestMethod]
    public async Task AtomicResolve_FullSevenSteps_JournalResultRunAudit()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("原子裁决全链路验证");
        await runStore.CreateAsync(run);
        await runStore.TransitionStateAsync(Ws, run.RunId, AgentRunState.Created, AgentRunState.AwaitingReconciliation, default);

        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, resultStore) = CreateExecutor(handler);
        var store = new InMemoryToolReconciliationStore(journal: journal, resultStore: resultStore, runStore: runStore, eventStore: eventStore);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("bank-transfer", "arg-atomic"), 0, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, result.JournalState, "非幂等写停在 Dispatched（模糊态）。");

        var record = await store.CreateAsync(BuildRecord("rec-atomic", result.RequestId, "bank-transfer", result), cts.Token);
        var lease = await store.TryBeginAsync(record.ReconciliationId, "test", TimeSpan.FromMinutes(1), cts.Token);
        Assert.IsNotNull(lease, "领取裁决租约。");

        var outcome = new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-atomic" };
        var durableResult = BuildDurableResult(record, outcome);
        var resolution = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, record.RequestId, lease!.LeaseToken, lease.FencingToken, outcome, durableResult,
            AgentRunState.ContextBuilding, cts.Token);

        Assert.AreEqual(ToolReconciliationResolutionStatus.Resolved, resolution.Status);
        Assert.AreEqual(ToolReconciliationStatus.Resolved, resolution.Record!.Status);

        // 1. journal 推进到 Committed（含真相结果）
        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Committed, entry!.State, "journal Committed。");

        // 2. Durable Result UPSERT（按 RequestId 可读）
        var saved = await resultStore.GetByRequestIdAsync(result.RequestId, cts.Token);
        Assert.IsNotNull(saved, "对账结果已持久化。");
        Assert.AreEqual("txn-atomic", saved!.Result);

        // 3. 记录终态 + 租约清除
        var stored = await store.GetAsync(record.ReconciliationId, cts.Token);
        Assert.AreEqual(ToolReconciliationStatus.Resolved, stored!.Status);
        Assert.IsNull(stored.LeaseToken, "终态清除租约。");

        // 4. Run 状态推进（AwaitingReconciliation → ContextBuilding）
        var advanced = await runStore.GetAsync(Ws, RunId);
        Assert.AreEqual(AgentRunState.ContextBuilding, advanced!.State, "Run 从停车态推进到目标态。");

        // 5. 审计事件追加 + 哈希链完整
        var events = await eventStore.ReadAsync(Ws, RunId);
        var audit = events.SingleOrDefault(e => e.EventType == AgentRunEventType.ToolReconciliationResolved);
        Assert.IsNotNull(audit, "应追加 ToolReconciliationResolved 审计事件。");
        Assert.IsTrue(audit!.Payload.Contains("txn-atomic", StringComparison.Ordinal), "审计事件携带对账结果。");
        Assert.IsTrue(AgentRunEventChain.VerifyChain(events), "事件哈希链完整（含新追加审计事件）。");
    }

    /// <summary>验证：唯一裁决者失效（token 不匹配）→ ArbitrationLost，Journal 与记录均不被污染。</summary>
    [TestMethod]
    public async Task AtomicResolve_ArbitrationLost_WhenLeaseInvalid()
    {
        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, _) = CreateExecutor(handler);
        var store = new InMemoryToolReconciliationStore(journal: journal);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("bank-transfer", "arg-lost"), 0, cts.Token);
        var record = await store.CreateAsync(BuildRecord("rec-lost", result.RequestId, "bank-transfer", result), cts.Token);
        var lease = await store.TryBeginAsync(record.ReconciliationId, "worker-a", TimeSpan.FromMinutes(1), cts.Token);
        Assert.IsNotNull(lease);

        var resolution = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, record.RequestId, "wrong-token", lease!.FencingToken,
            new ToolReconciliationOutcome { SideEffectOccurred = true },
            BuildDurableResult(record, new ToolReconciliationOutcome { SideEffectOccurred = true }),
            null, cts.Token);

        Assert.AreEqual(ToolReconciliationResolutionStatus.ArbitrationLost, resolution.Status);
        Assert.AreEqual(ToolDispatchState.Dispatched, (await journal.GetEntryAsync(result.RequestId, cts.Token))!.State,
            "仲裁权失效 → journal 不被污染。");
        Assert.AreEqual(ToolReconciliationStatus.Running, (await store.GetAsync(record.ReconciliationId, cts.Token))!.Status,
            "仲裁权失效 → 记录不被终态化。");
    }

    /// <summary>验证：租约被接管后，持有当前 token 但携带过期版本 → VersionMismatch。</summary>
    [TestMethod]
    public async Task AtomicResolve_VersionMismatch_WhenFencingStale()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);
        var lease1 = await store.TryBeginAsync("rec-1", "worker-a", TimeSpan.FromMilliseconds(1), cts.Token);
        Assert.IsNotNull(lease1);
        await Task.Delay(50, cts.Token);

        // 租约过期 → 新持有者接管（fencing=2）
        var lease2 = await store.TryBeginAsync("rec-1", "worker-b", TimeSpan.FromMinutes(5), cts.Token);
        Assert.IsNotNull(lease2);
        Assert.AreEqual(2, lease2!.FencingToken);

        // 持有当前 token 但用过期版本（1）提交 → VersionMismatch
        var resolution = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, "req-1", lease2.LeaseToken, expectedReconciliationVersion: 1,
            new ToolReconciliationOutcome { SideEffectOccurred = true },
            BuildDurableResult((await store.GetAsync("rec-1", cts.Token))!, new ToolReconciliationOutcome { SideEffectOccurred = true }),
            null, cts.Token);
        Assert.AreEqual(ToolReconciliationResolutionStatus.VersionMismatch, resolution.Status);

        // 用当前版本（2）提交 → 成功
        var ok = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, "req-1", lease2.LeaseToken, lease2.FencingToken,
            new ToolReconciliationOutcome { SideEffectOccurred = true },
            BuildDurableResult((await store.GetAsync("rec-1", cts.Token))!, new ToolReconciliationOutcome { SideEffectOccurred = true }),
            null, cts.Token);
        Assert.AreEqual(ToolReconciliationResolutionStatus.Resolved, ok.Status);
    }

    /// <summary>验证：已终态记录重复原子裁决 → AlreadyTerminal（幂等拒绝，不覆盖首次结果）。</summary>
    [TestMethod]
    public async Task AtomicResolve_AlreadyTerminal_ReturnsAlreadyTerminal()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);
        var lease = await store.TryBeginAsync("rec-1", "worker-a", TimeSpan.FromMinutes(1), cts.Token);
        Assert.IsNotNull(lease);

        var outcome = new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-first" };
        var first = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, "req-1", lease!.LeaseToken, lease.FencingToken, outcome,
            BuildDurableResult((await store.GetAsync("rec-1", cts.Token))!, outcome), null, cts.Token);
        Assert.AreEqual(ToolReconciliationResolutionStatus.Resolved, first.Status);

        var second = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, "req-1", lease.LeaseToken, lease.FencingToken, outcome,
            BuildDurableResult((await store.GetAsync("rec-1", cts.Token))!, outcome), null, cts.Token);
        Assert.AreEqual(ToolReconciliationResolutionStatus.AlreadyTerminal, second.Status);

        var record = await store.GetAsync("rec-1", cts.Token);
        Assert.AreEqual("txn-first", record!.Result, "首次裁决内容不被重复提交覆盖。");
    }

    /// <summary>验证：相同 DecisionRequestId + 相同 outcome 重试 → 幂等成功（Resolved），不覆盖首次真相。</summary>
    [TestMethod]
    public async Task AtomicResolve_SameDecisionRequestId_SameOutcome_ReturnsResolvedIdempotent()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);
        var lease = await store.TryBeginAsync("rec-1", "worker-a", TimeSpan.FromMinutes(1), cts.Token);
        Assert.IsNotNull(lease);

        var outcome = new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-777" };
        var first = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, "req-1", lease!.LeaseToken, lease.FencingToken, outcome,
            BuildDurableResult((await store.GetAsync("rec-1", cts.Token))!, outcome), null, cts.Token,
            decisionRequestId: "decision-1");
        Assert.AreEqual(ToolReconciliationResolutionStatus.Resolved, first.Status);

        // 相同决策身份 + 相同 outcome 重试（租约已清除）→ 幂等成功，不覆盖首次真相
        var retry = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, "req-1", lease.LeaseToken, lease.FencingToken, outcome,
            BuildDurableResult((await store.GetAsync("rec-1", cts.Token))!, outcome), null, cts.Token,
            decisionRequestId: "decision-1");
        Assert.AreEqual(ToolReconciliationResolutionStatus.Resolved, retry.Status, "相同决策身份 + 相同 outcome → 幂等成功。");
        Assert.AreEqual("txn-777", (await store.GetAsync("rec-1", cts.Token))!.Result, "重试不覆盖首次裁决内容。");
    }

    /// <summary>验证：相同 DecisionRequestId 但相反 outcome 重试 → 决策冲突（DecisionConflict）。</summary>
    [TestMethod]
    public async Task AtomicResolve_SameDecisionRequestId_OppositeOutcome_ReturnsDecisionConflict()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);
        var lease = await store.TryBeginAsync("rec-1", "worker-a", TimeSpan.FromMinutes(1), cts.Token);
        Assert.IsNotNull(lease);

        var occurred = new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-777" };
        var first = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, "req-1", lease!.LeaseToken, lease.FencingToken, occurred,
            BuildDurableResult((await store.GetAsync("rec-1", cts.Token))!, occurred), null, cts.Token,
            decisionRequestId: "decision-1");
        Assert.AreEqual(ToolReconciliationResolutionStatus.Resolved, first.Status);

        // 相同决策身份 + 相反 outcome → 决策冲突
        var opposite = new ToolReconciliationOutcome { SideEffectOccurred = false, Error = "确认未发生" };
        var conflict = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, "req-1", lease.LeaseToken, lease.FencingToken, opposite,
            BuildDurableResult((await store.GetAsync("rec-1", cts.Token))!, opposite), null, cts.Token,
            decisionRequestId: "decision-1");
        Assert.AreEqual(ToolReconciliationResolutionStatus.DecisionConflict, conflict.Status, "相同决策身份 + 相反 outcome → 决策冲突。");
        Assert.AreEqual(true, (await store.GetAsync("rec-1", cts.Token))!.SideEffectOccurred, "首次裁决真相不被相反重试覆盖。");
    }

    /// <summary>验证：不同 DecisionRequestId 重复提交 → AlreadyTerminal（非幂等冲突）。</summary>
    [TestMethod]
    public async Task AtomicResolve_DifferentDecisionRequestId_ReturnsAlreadyTerminal()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);
        var lease = await store.TryBeginAsync("rec-1", "worker-a", TimeSpan.FromMinutes(1), cts.Token);
        Assert.IsNotNull(lease);

        var outcome = new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-777" };
        var first = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, "req-1", lease!.LeaseToken, lease.FencingToken, outcome,
            BuildDurableResult((await store.GetAsync("rec-1", cts.Token))!, outcome), null, cts.Token,
            decisionRequestId: "decision-1");
        Assert.AreEqual(ToolReconciliationResolutionStatus.Resolved, first.Status);

        var otherDecision = await store.ResolveReconciliationAtomicallyAsync(
            Ws, RunId, "req-1", lease.LeaseToken, lease.FencingToken, outcome,
            BuildDurableResult((await store.GetAsync("rec-1", cts.Token))!, outcome), null, cts.Token,
            decisionRequestId: "decision-2");
        Assert.AreEqual(ToolReconciliationResolutionStatus.AlreadyTerminal, otherDecision.Status,
            "不同决策身份 → 视为新的重复提交，拒绝（AlreadyTerminal）。");
    }

    /// <summary>验证：租户键 (workspace_id, run_id, request_id) 不匹配 → NotFound。</summary>
    [TestMethod]
    public async Task AtomicResolve_NotFound_WhenTenantKeyMismatch()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);

        var resolution = await store.ResolveReconciliationAtomicallyAsync(
            "ws-other", RunId, "req-1", "token", 1,
            new ToolReconciliationOutcome { SideEffectOccurred = true },
            BuildDurableResult((await store.GetAsync("rec-1", cts.Token))!, new ToolReconciliationOutcome { SideEffectOccurred = true }),
            null, cts.Token);
        Assert.AreEqual(ToolReconciliationResolutionStatus.NotFound, resolution.Status,
            "跨 Workspace 的租户键不匹配 → NotFound（P0-5 完整租户键）。");
    }

    /// <summary>验证：CreateAsync 按 (WorkspaceId, RunId, RequestId) 幂等——跨 Workspace 同 RequestId 各自独立。</summary>
    [TestMethod]
    public async Task Store_CreateAsync_IdempotentByFullTenantKey()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-1", "req-1", "bank-transfer"), cts.Token);
        var duplicate = await store.CreateAsync(BuildRecord("rec-1-dup", "req-1", "bank-transfer"), cts.Token);
        Assert.AreEqual("rec-1", duplicate.ReconciliationId, "同 (ws, run, request) 幂等返回既有记录。");

        // 同 RequestId 但不同 Workspace → 独立记录
        var otherWs = await store.CreateAsync(BuildRecord("rec-2", "req-1", "bank-transfer") with { WorkspaceId = "ws-other" }, cts.Token);
        Assert.AreEqual("rec-2", otherWs.ReconciliationId, "不同 Workspace 的同 RequestId 各自独立。");

        // 同 RequestId 但不同 Run → 独立记录
        var otherRun = await store.CreateAsync(BuildRecord("rec-3", "req-1", "bank-transfer", runId: "run-other"), cts.Token);
        Assert.AreEqual("rec-3", otherRun.ReconciliationId, "不同 Run 的同 RequestId 各自独立。");
    }

    // ── 3. 仲裁权（P0-5）──────────────────────────────────────────────

    /// <summary>验证：自动 Handler 持有仲裁权时，人工裁决返回 3（仲裁权被占用），不污染 Journal。</summary>
    [TestMethod]
    public async Task Coordinator_Resolve_BusyLease_ReturnsThree()
    {
        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, _) = CreateExecutor(handler);
        var store = new InMemoryToolReconciliationStore(journal: journal);
        var coordinator = new ToolReconciliationCoordinator(store, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("bank-transfer", "arg-busy"), 0, cts.Token);
        var record = await store.CreateAsync(BuildRecord("rec-busy", result.RequestId, "bank-transfer", result), cts.Token);

        // 模拟自动 Handler 已领取租约（对账进行中）
        var lease = await store.TryBeginAsync(record.ReconciliationId, "worker:reconcile", TimeSpan.FromMinutes(1), cts.Token);
        Assert.IsNotNull(lease, "自动 Handler 领取裁决租约。");

        // 人工裁决尝试 → 仲裁权被占用 → 3
        var code = await coordinator.ResolveAsync(
            record.ReconciliationId, new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-manual" }, cts.Token);
        Assert.AreEqual(3, code, "仲裁权被占用 → 3。");

        var entry = await journal.GetEntryAsync(result.RequestId, cts.Token);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State, "人工裁决不污染自动 Handler 的 Journal。");
        Assert.AreEqual(ToolReconciliationStatus.Running, (await store.GetAsync(record.ReconciliationId, cts.Token))!.Status);
    }

    /// <summary>验证：ReconcileRecordAsync（Worker 路径）领取租约后提交，记录终态清除租约。</summary>
    [TestMethod]
    public async Task Coordinator_ReconcileRecord_LeaseAcquiredThenCommitted()
    {
        var handler = CreateHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, _) = CreateExecutor(handler);
        var store = new InMemoryToolReconciliationStore(journal: journal);
        var coordinator = new ToolReconciliationCoordinator(store, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await executor.ExecuteAsync(RunId, Ws, BuildToolCall("bank-transfer", "arg-worker"), 0, cts.Token);
        var record = await store.CreateAsync(BuildRecord("rec-worker", result.RequestId, "bank-transfer", result), cts.Token);

        var reconHandler = new FakeReconciliationHandler("bank-recon", new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-worker" });
        var lease = await store.TryBeginAsync(record.ReconciliationId, "worker:test", TimeSpan.FromMinutes(5), cts.Token);
        Assert.IsNotNull(lease, "Worker 领取裁决租约成功。");
        await coordinator.ReconcileWithLeaseAsync(record, lease!, reconHandler, cts.Token);

        var stored = await store.GetAsync(record.ReconciliationId, cts.Token);
        Assert.AreEqual(ToolReconciliationStatus.Resolved, stored!.Status);
        Assert.IsNull(stored.LeaseToken, "提交后租约清除。");
        Assert.IsNull(stored.LeaseOwner, "提交后租约持有者清除。");
        Assert.AreEqual(ToolDispatchState.Committed, (await journal.GetEntryAsync(result.RequestId, cts.Token))!.State);
        Assert.AreEqual(1, reconHandler.InvocationCount);
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = RunId,
        WorkspaceId = Ws,
        SessionId = "session-recon-lease",
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
        string? reconciliationHandler = "bank-recon") => new()
        {
            ReconciliationId = reconciliationId,
            RunId = runId,
            WorkspaceId = Ws,
            RequestId = requestId,
            ToolName = toolName,
            ExternalOperationId = result?.ExternalOperationId,
            ReconciliationHandler = reconciliationHandler,
            Status = ToolReconciliationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static DurableToolResult BuildDurableResult(ToolReconciliationRecord record, ToolReconciliationOutcome outcome) => new()
    {
        ToolCallId = record.RequestId,
        RequestId = record.RequestId,
        WorkspaceId = record.WorkspaceId,
        RunId = record.RunId,
        InvocationId = record.RequestId,
        SideEffect = ToolSideEffect.Write,
        ExternalOperationId = record.ExternalOperationId,
        Result = outcome.SideEffectOccurred ? outcome.Result : null,
        Succeeded = outcome.SideEffectOccurred,
        Error = outcome.SideEffectOccurred ? null : (outcome.Error ?? "void"),
        DurationMs = 0
    };

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
        IdempotencyKey = "idem-recon-lease",
        ToolCallId = $"toolcall-{toolName}-0"
    };

    /// <summary>可声明副作用 / 对账 Handler 的 Tool Handler（记录外部调用次数）。</summary>
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

    /// <summary>可配置的 IToolReconciliationHandler stub：返回预设真相。</summary>
    private sealed class FakeReconciliationHandler : IToolReconciliationHandler
    {
        private readonly ToolReconciliationOutcome _outcome;
        private int _invocationCount;

        public FakeReconciliationHandler(string handlerName, ToolReconciliationOutcome outcome)
        {
            HandlerName = handlerName;
            _outcome = outcome;
        }

        public string HandlerName { get; }

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<ToolReconciliationOutcome> ReconcileAsync(
            ToolReconciliationRecord record,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult(_outcome);
        }
    }
}
