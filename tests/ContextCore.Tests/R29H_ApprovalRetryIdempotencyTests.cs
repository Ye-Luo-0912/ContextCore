using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// Approval 重试幂等（P1-1）
//
// 覆盖范围：
//   ResolveAsync 幂等重试：相同 decisionRequestId + 相同决策 → 幂等成功（不抛异常）；
//   相同 decisionRequestId + 相反决策 → 冲突（拒绝覆盖已生效决策）；
//   无幂等键或键不匹配 + 已裁决 → 冲突（旧语义保持）；
//   CreateResolvedApprovalAsync：原子创建并裁决（单次写入最终状态，不留 Pending 中间态）；
//   自动审批路径：Gate 走 CreateResolvedApprovalAsync（审计记录带 auto: 幂等键）。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Approval-Lease")]
public sealed class R29H_ApprovalRetryIdempotencyTests
{
    private const string Ws = "ws-approval-idem";
    private const string RunId = "run-approval-idem";

    // ── ResolveAsync 幂等重试 ───────────────────────────────────────────────

    [TestMethod]
    public async Task Resolve_DuplicateSameKeySameDecision_IdempotentSuccess()
    {
        var store = new InMemoryAgentApprovalStore();
        var approval = BuildApproval("ap-idem-1");
        await store.CreateAsync(approval);

        // 首次裁决
        await store.ResolveAsync(Ws, approval.ApprovalId, AgentApprovalStatus.Approved,
            "approver-1", null, decisionRequestId: "req-1", targetRunState: AgentRunState.PendingToolExecution);

        // 重试：相同 decisionRequestId + 相同决策 → 幂等成功（不抛异常）
        await store.ResolveAsync(Ws, approval.ApprovalId, AgentApprovalStatus.Approved,
            "approver-1", null, decisionRequestId: "req-1", targetRunState: AgentRunState.PendingToolExecution);

        var resolved = await store.GetAsync(Ws, approval.ApprovalId);
        Assert.IsNotNull(resolved, "审批记录应存在。");
        Assert.AreEqual(AgentApprovalStatus.Approved, resolved!.Status, "状态应为首次裁决的 Approved。");
        Assert.AreEqual("req-1", resolved.DecisionRequestId, "幂等键应持久化。");
        Assert.AreEqual(AgentRunState.PendingToolExecution, resolved.TargetRunState, "目标状态应持久化。");
    }

    [TestMethod]
    public async Task Resolve_DuplicateSameKeyOppositeDecision_Conflicts()
    {
        var store = new InMemoryAgentApprovalStore();
        var approval = BuildApproval("ap-idem-2");
        await store.CreateAsync(approval);

        await store.ResolveAsync(Ws, approval.ApprovalId, AgentApprovalStatus.Approved,
            "approver-1", null, decisionRequestId: "req-2", targetRunState: AgentRunState.PendingToolExecution);

        // 重试：相同 decisionRequestId + 相反决策 → 冲突（409 语义）
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.ResolveAsync(Ws, approval.ApprovalId, AgentApprovalStatus.Rejected,
                "approver-1", "改主意了", decisionRequestId: "req-2", targetRunState: AgentRunState.Failed).AsTask());

        StringAssert.Contains(ex.Message, "幂等键冲突",
            "相反决策必须报告幂等键冲突（拒绝覆盖已生效决策）。");

        // 已存决策不得被覆盖
        var resolved = await store.GetAsync(Ws, approval.ApprovalId);
        Assert.IsNotNull(resolved);
        Assert.AreEqual(AgentApprovalStatus.Approved, resolved!.Status, "已生效决策不得被相反决策覆盖。");
    }

    [TestMethod]
    public async Task Resolve_AlreadyResolved_WithoutMatchingKey_StillConflicts()
    {
        var store = new InMemoryAgentApprovalStore();
        var approval = BuildApproval("ap-idem-3");
        await store.CreateAsync(approval);

        await store.ResolveAsync(Ws, approval.ApprovalId, AgentApprovalStatus.Approved, "approver-1", null);

        // 无幂等键（旧客户端）→ 已裁决即冲突（旧语义保持）
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.ResolveAsync(Ws, approval.ApprovalId, AgentApprovalStatus.Approved, "approver-2", null).AsTask());

        // 不同幂等键 → 冲突
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.ResolveAsync(Ws, approval.ApprovalId, AgentApprovalStatus.Approved,
                "approver-2", null, decisionRequestId: "req-other").AsTask());
    }

    // ── CreateResolvedApprovalAsync 原子创建并裁决 ───────────────────────────

    [TestMethod]
    public async Task CreateResolved_AtomicallyCreatesFinalState()
    {
        var store = new InMemoryAgentApprovalStore();

        var resolved = BuildApproval("ap-idem-4") with
        {
            Status = AgentApprovalStatus.Approved,
            ApproverId = "auto-rule",
            DecisionRequestId = "auto:ap-idem-4",
            TargetRunState = AgentRunState.PendingToolExecution,
            ResolvedAt = DateTimeOffset.UtcNow
        };
        await store.CreateResolvedApprovalAsync(resolved);

        // 单次写入即最终状态：无 Pending 中间态残留
        var pending = await store.ListPendingAsync(Ws, RunId);
        Assert.AreEqual(0, pending.Count, "原子创建不应留下 Pending 中间态。");

        var stored = await store.GetAsync(Ws, "ap-idem-4");
        Assert.IsNotNull(stored, "审批记录应存在。");
        Assert.AreEqual(AgentApprovalStatus.Approved, stored!.Status, "状态应为最终 Approved。");
        Assert.AreEqual("auto:ap-idem-4", stored.DecisionRequestId, "幂等键应持久化。");
        Assert.AreEqual("auto-rule", stored.ApproverId, "审批者应持久化。");
        Assert.IsNotNull(stored.ResolvedAt, "裁决时间应已写入。");
    }

    [TestMethod]
    public async Task CreateResolved_PendingStatus_Rejected()
    {
        var store = new InMemoryAgentApprovalStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            store.CreateResolvedApprovalAsync(BuildApproval("ap-idem-5")).AsTask());
    }

    // ── Gate 自动审批走原子路径 ─────────────────────────────────────────────

    [TestMethod]
    public async Task AutoApprove_UsesCreateResolved_NoPendingIntermediate()
    {
        var store = new InMemoryAgentApprovalStore();
        var gate = new DefaultAgentApprovalGate(
            approvalRequiredTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            autoApproveAll: false,
            approvalStore: store);

        var result = await gate.RequestApprovalAsync(Ws, RunId, new AgentToolCallRequest
        {
            ToolName = "search",
            Arguments = "{}",
            IdempotencyKey = "idem-auto",
            ToolCallId = "model-tc-auto"
        });

        Assert.IsTrue(result.Approved, "自动批准应直接批准。");
        Assert.IsNotNull(result.ApprovalId, "应返回审批 ID。");

        // 自动批准走 CreateResolvedApprovalAsync：行即最终状态 + auto: 幂等键
        var stored = await store.GetAsync(Ws, result.ApprovalId!);
        Assert.IsNotNull(stored, "审批行应存在。");
        Assert.AreEqual(AgentApprovalStatus.Approved, stored!.Status, "自动批准行应为 Approved（无 Pending 中间态）。");
        Assert.AreEqual("auto-rule", stored.ApproverId);
        Assert.IsNotNull(stored.DecisionRequestId, "自动批准应记录幂等键。");
        StringAssert.StartsWith(stored.DecisionRequestId!, "auto:",
            "自动批准幂等键应以 auto: 前缀标识。");
        Assert.IsNotNull(stored.ResolvedAt, "自动批准应记录裁决时间。");

        var pending = await store.ListPendingAsync(Ws, RunId);
        Assert.AreEqual(0, pending.Count, "自动批准不应残留 Pending 审批。");
    }

    // ── 并发：同一幂等键下同决策全部成功（1 CAS 胜出 + 其余幂等重试）────────

    [TestMethod]
    public async Task Resolve_ConcurrentSameKeySameDecision_AllSucceed()
    {
        var store = new InMemoryAgentApprovalStore();
        var approval = BuildApproval("ap-idem-6");
        await store.CreateAsync(approval);

        // 8 个并发同键同决策（Approved）：CAS 恰好 1 个写入，其余在键持久化后
        // 走幂等成功路径（同键同决策 → 不抛异常）。
        const int contenders = 8;
        var successCount = 0;
        var conflictCount = 0;
        var tasks = Enumerable.Range(0, contenders).Select(i => Task.Run(async () =>
        {
            try
            {
                await store.ResolveAsync(Ws, approval.ApprovalId, AgentApprovalStatus.Approved,
                    $"approver-{i}", null, decisionRequestId: "req-concurrent");
                Interlocked.Increment(ref successCount);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref conflictCount);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.AreEqual(contenders, successCount,
            "同键同决策的并发裁决应全部成功（1 CAS 胜出 + 其余幂等重试），不允许冲突。");
        Assert.AreEqual(0, conflictCount, "同键同决策不应产生冲突。");

        var resolved = await store.GetAsync(Ws, approval.ApprovalId);
        Assert.IsNotNull(resolved, "审批记录应存在。");
        Assert.AreEqual(AgentApprovalStatus.Approved, resolved!.Status, "最终状态应为 Approved。");
        Assert.AreEqual("req-concurrent", resolved.DecisionRequestId, "幂等键应持久化。");

        // 竞争结束后，同键同决策重试 → 幂等成功；同键相反决策 → 冲突
        await store.ResolveAsync(Ws, approval.ApprovalId, AgentApprovalStatus.Approved,
            "approver-retry", null, decisionRequestId: "req-concurrent");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.ResolveAsync(Ws, approval.ApprovalId, AgentApprovalStatus.Rejected,
                "approver-retry", "no", decisionRequestId: "req-concurrent").AsTask());
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────

    private static AgentApproval BuildApproval(string approvalId) => new()
    {
        ApprovalId = approvalId,
        RunId = RunId,
        WorkspaceId = Ws,
        ToolCallId = "model-tc-" + approvalId,
        ToolName = "file_delete",
        Status = AgentApprovalStatus.Pending,
        Reason = "幂等重试测试",
        CreatedAt = DateTimeOffset.UtcNow
    };
}
