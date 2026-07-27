using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// R29-Hard-Gate：Tool Journal CAS 严格状态机验收测试（3 项）
//
// 验证任务B 修复后的 Tool Journal 严格 expected-state CAS 语义：
//   1. 状态转换不能跳过 Dispatched 状态（Prepared → Committed 禁止）
//   2. 相同 RequestId 但不同 Payload 的重复 Prepare 请求会被拒绝（fails closed）
//   3. 相同 IdempotencyKey 的重复 Prepare 返回原始操作状态（而非新执行）
//
// 设计原则：
//   - 使用真实 InMemoryToolDispatchJournal（不 mock），验证实际行为
//   - 所有异步测试使用超时 CancellationTokenSource 防止挂起
//   - 中文注释
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
        //    P0-3 CAS-1：expected-state 精确匹配，禁止跨级跳跃
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => journal.MarkCommittedAsync(requestId, cts.Token).AsTask(),
            "Prepared → Committed 跨级跳跃必须被拒绝（InvalidTransition）。");

        // 3. 验证状态仍为 Prepared（未被错误推进到 Committed）
        var entry = await journal.GetEntryAsync(requestId, cts.Token);
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
        //    P0-3 CAS-2：语义等价校验失败 → 抛 InvalidOperationException（RequestIdReuseDetected）
        var conflictingEntry = BuildEntry(
            requestId,
            ToolDispatchState.Prepared,
            payloadDigest: "digest-B",
            idempotencyKey: "idem-conflicting");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => journal.PrepareAsync(conflictingEntry, cts.Token).AsTask(),
            "相同 RequestId 但不同 PayloadDigest 的重复 Prepare 必须被拒绝（RequestIdReuseDetected）。");

        // 3. 验证原始记录未被覆盖
        //    PayloadDigest 仍是 digest-A；IdempotencyKey 仍是 idem-original
        var stored = await journal.GetEntryAsync(requestId, cts.Token);
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
        await journal.MarkDispatchedAsync(requestId, externalOperationId: "ext-op-1", cancellationToken: cts.Token);
        await journal.MarkCommittedAsync(requestId, cts.Token);

        // 验证第一次推进到 Committed
        var entryAfterFirstRun = await journal.GetEntryAsync(requestId, cts.Token);
        Assert.IsNotNull(entryAfterFirstRun);
        Assert.AreEqual(ToolDispatchState.Committed, entryAfterFirstRun!.State);
        Assert.AreEqual("ext-op-1", entryAfterFirstRun.ExternalOperationId);

        // 2. 第二次 PrepareAsync（相同 IdempotencyKey + 相同语义字段）→ 幂等命中
        //    PrepareAsync 语义：key 已存在且语义等价 → 不抛异常，不覆盖既有状态
        await journal.PrepareAsync(
            BuildEntry(requestId, ToolDispatchState.Prepared, idempotencyKey: idempotencyKey, payloadDigest: "digest-shared"),
            cts.Token);

        // 3. 验证返回的是原始操作的状态（Committed，未被重置为 Prepared）
        //    说明：PrepareAsync 重复时不重置状态，原始操作推进的 Committed 状态被保留
        var entryAfterSecondPrepare = await journal.GetEntryAsync(requestId, cts.Token);
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
        //    说明状态机不会重复推进
        await journal.MarkDispatchedAsync(requestId, externalOperationId: "ext-op-should-be-ignored", cancellationToken: cts.Token);
        var entryAfterSecondMark = await journal.GetEntryAsync(requestId, cts.Token);
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

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static ToolDispatchJournalEntry BuildEntry(
        string requestId,
        ToolDispatchState state,
        string? payloadDigest = null,
        string? idempotencyKey = null) => new()
        {
            RequestId = requestId,
            ToolName = "echo",
            State = state,
            IdempotencyKey = idempotencyKey ?? ("idem-" + requestId),
            PayloadDigest = payloadDigest ?? ToolDispatchJournalEntry.ComputePayloadDigest("default-payload"),
            WorkspaceId = "ws-test-journal",
            RunId = "run-test-journal",
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
