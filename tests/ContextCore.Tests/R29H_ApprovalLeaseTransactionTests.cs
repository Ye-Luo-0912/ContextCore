using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

// ===========================================================================
// Approval / Lease 强事务测试
//
// 验证硬验收保证：
// 1. 人工审批 fail-closed：需人工审批但未注入 IAgentApprovalStore 时抛异常，
// 绝不产生 "Run AwaitingApproval 但无 Approval Row" 的半状态。
// 2. ApprovalId 与 ToolCallId 分离：审批记录主键由 Gate 独立生成，
// ToolCallId 保留模型原始 ID 用于事件流关联。
// 3. 审批裁决 CAS：并发裁决同一审批时恰好一个成功，其余必须失败。
// 4. Tool 副作用 Lease Fence：过期 fence 阻止外部副作用；有效 fence 透传到 Handler。
// 5. 实际 lease expiry 贯穿 Host → Actor → Tool：Tool 收到的 fence.ExpiresAt
// 是数据库租约过期时间（获取时间 + LeaseDuration），而非 Run.DeadlineAt 推导值。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Approval-Lease")]
public sealed class R29H_ApprovalLeaseTransactionTests
{
    private const string Ws = "ws-approval-lease";
    private const string RunId = "run-approval-lease";

    // ── 1. 人工审批 fail-closed ────────────────────────────────────────────

    [TestMethod]
    public async Task HumanApproval_WithoutStore_FailsClosed()
    {
        var gate = new DefaultAgentApprovalGate(
            approvalRequiredTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file_delete" },
            autoApproveAll: false,
            approvalStore: null);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            gate.RequestApprovalAsync(Ws, RunId, BuildToolCall("file_delete", "model-tc-1")).AsTask());

        StringAssert.Contains(ex.Message, "fail-closed",
            "需人工审批但无 store 时必须 fail-closed，禁止返回 PendingApproval=true 而无审批行。");
    }

    [TestMethod]
    public async Task HumanApproval_WithStore_CreatesPendingRow_ApprovalIdSeparateFromToolCallId()
    {
        var store = new InMemoryAgentApprovalStore();
        var gate = new DefaultAgentApprovalGate(
            approvalRequiredTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file_delete" },
            autoApproveAll: false,
            approvalStore: store);

        var toolCall = BuildToolCall("file_delete", "model-tc-42");
        var result = await gate.RequestApprovalAsync(Ws, RunId, toolCall);

        // 挂起等待人工裁决
        Assert.IsTrue(result.PendingApproval, "需人工审批的 Tool 应返回 PendingApproval=true。");
        Assert.IsFalse(result.Approved, "人工审批未裁决前不应 Approved。");
        Assert.IsNotNull(result.ApprovalId, "应返回 Gate 生成的 ApprovalId。");

        // ApprovalId 与 ToolCallId 分离（审批主键 ≠ 模型 ToolCallId）
        Assert.AreNotEqual(toolCall.ToolCallId, result.ApprovalId,
            "ApprovalId 必须独立于 ToolCallId（审批主键与事件流关联键分离）。");

        // 不变式：Run 挂起 AwaitingApproval 必然有对应 Pending 审批行
        var pending = await store.ListPendingAsync(Ws, RunId);
        Assert.AreEqual(1, pending.Count, "应恰好创建 1 条 Pending 审批记录。");
        Assert.AreEqual(result.ApprovalId, pending[0].ApprovalId, "审批行主键应等于 Gate 返回的 ApprovalId。");
        Assert.AreEqual(toolCall.ToolCallId, pending[0].ToolCallId, "审批行 ToolCallId 应保留模型原始 ID。");
        Assert.AreEqual(AgentApprovalStatus.Pending, pending[0].Status, "审批行应为 Pending 状态。");
    }

    [TestMethod]
    public async Task AutoApprove_WithoutStore_StillApproves()
    {
        // 默认构造：全部自动批准 + 无 store —— 向后兼容测试场景，不得抛异常
        var gate = new DefaultAgentApprovalGate();
        var result = await gate.RequestApprovalAsync(Ws, RunId, BuildToolCall("file_delete", "model-tc-3"));

        Assert.IsTrue(result.Approved, "自动审批模式应直接批准。");
        Assert.IsFalse(result.PendingApproval, "自动审批不应进入挂起状态。");
        Assert.AreEqual("auto-rule", result.ApproverId);
    }

    // ── 2. 并发审批裁决 CAS ────────────────────────────────────────────────

    [TestMethod]
    public async Task ApprovalResolve_Concurrent_CasAllowsExactlyOne()
    {
        var store = new InMemoryAgentApprovalStore();
        var approval = new AgentApproval
        {
            ApprovalId = "ap-concurrent-1",
            RunId = RunId,
            WorkspaceId = Ws,
            ToolCallId = "model-tc-9",
            ToolName = "file_delete",
            Status = AgentApprovalStatus.Pending,
            Reason = "并发裁决测试",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await store.CreateAsync(approval);

        const int contenders = 8;
        var successCount = 0;
        var casFailures = 0;
        var tasks = Enumerable.Range(0, contenders).Select(i => Task.Run(async () =>
        {
            try
            {
                await store.ResolveAsync(Ws, approval.ApprovalId, AgentApprovalStatus.Approved, $"approver-{i}", null);
                Interlocked.Increment(ref successCount);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref casFailures);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.AreEqual(1, successCount, $"并发裁决必须恰好一个成功，实际 {successCount}。");
        Assert.AreEqual(contenders - 1, casFailures,
            $"其余 {contenders - 1} 个并发裁决必须 CAS 失败（不可重复裁决）。");

        var resolved = await store.GetAsync(Ws, approval.ApprovalId);
        Assert.IsNotNull(resolved, "应能取回审批记录。");
        Assert.AreEqual(AgentApprovalStatus.Approved, resolved!.Status, "最终状态应为 Approved。");
        Assert.IsNotNull(resolved.ResolvedAt, "裁决时间应已写入。");
        Assert.IsFalse(string.IsNullOrEmpty(resolved.ApproverId), "应记录获胜裁决者。");
    }

    // ── 3. Tool 副作用 Lease Fence ─────────────────────────────────────────

    [TestMethod]
    public async Task ToolExecution_ExpiredLeaseFence_BlocksSideEffect()
    {
        var handler = CreateHandler("weather", new ToolDescriptor
        {
            Name = "weather",
            DeclaredSideEffect = ToolSideEffect.Write,
            RecoveryStrategy = ToolRecoveryStrategy.SafeReplay,
            RequiresLeaseFence = true
        });
        var (_, executor, _, _) = CreateExecutor(handler);

        var expiredFence = new AgentLeaseFence
        {
            LeaseToken = "stale-lease-token",
            FencingToken = 1,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-5)
        };

        var result = await executor.ExecuteAsync(
            RunId, Ws, BuildToolCall("weather", "model-tc-fence-1"), 0,
            CancellationToken.None, expiredFence, deadlineAt: null);

        Assert.IsFalse(result.Succeeded, "过期 lease fence 必须返回失败（fail-closed）。");
        StringAssert.Contains(result.Error ?? string.Empty, "Lease 已过期",
            "失败原因应明确为租约过期。");
        Assert.AreEqual(0, handler.InvocationCount,
            "旧 Owner 在租约过期后不得执行外部副作用。");
    }

    [TestMethod]
    public async Task ToolExecution_LiveLeaseFence_ExecutesAndForwardsFenceToHandler()
    {
        var handler = CreateHandler("weather", new ToolDescriptor
        {
            Name = "weather",
            DeclaredSideEffect = ToolSideEffect.None,
            RecoveryStrategy = ToolRecoveryStrategy.SafeReplay,
            RequiresLeaseFence = true
        });
        var (_, executor, _, _) = CreateExecutor(handler);

        var liveFence = new AgentLeaseFence
        {
            LeaseToken = "live-lease-token",
            FencingToken = 7,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        var result = await executor.ExecuteAsync(
            RunId, Ws, BuildToolCall("weather", "model-tc-fence-2"), 0,
            CancellationToken.None, liveFence, deadlineAt: null);

        Assert.IsTrue(result.Succeeded, "有效 lease fence 应允许执行。");
        Assert.AreEqual(1, handler.InvocationCount, "有效 lease 下 Tool 应恰好执行一次。");

        var forwardedFence = handler.LastContext?.LeaseFence;
        Assert.IsNotNull(forwardedFence, "LeaseFence 应透传到 Tool Handler。");
        Assert.AreEqual("live-lease-token", forwardedFence!.LeaseToken);
        Assert.AreEqual(7, forwardedFence.FencingToken);
        Assert.AreEqual(liveFence.ExpiresAt, forwardedFence.ExpiresAt,
            "fence.ExpiresAt 应原样透传（实际租约过期时间）。");
    }

    [TestMethod]
    public async Task ToolExecution_RequiresLeaseFence_WithoutFence_FailsClosed()
    {
        var handler = CreateHandler("weather", new ToolDescriptor
        {
            Name = "weather",
            DeclaredSideEffect = ToolSideEffect.Write,
            RecoveryStrategy = ToolRecoveryStrategy.SafeReplay,
            RequiresLeaseFence = true
        });
        var (_, executor, _, _) = CreateExecutor(handler);

        var result = await executor.ExecuteAsync(
            RunId, Ws, BuildToolCall("weather", "model-tc-fence-3"), 0,
            CancellationToken.None, leaseFence: null, deadlineAt: null);

        Assert.IsFalse(result.Succeeded, "声明 RequiresLeaseFence 但未携带 fence 必须 fail-closed。");
        Assert.AreEqual(0, handler.InvocationCount, "无 fence 保护时不得执行外部副作用。");
    }

    // ── 4. 实际 lease expiry 贯穿 Host → Actor → Tool ──────────────────────

    [TestMethod]
    public async Task Host_ActualLeaseExpiry_ThreadsThroughToToolFence()
    {
        var runStore = new InMemoryAgentRunStore();
        // 必须注入 runStore：否则 AppendBatchAsync 忽略 runStateUpdate，Run 永远停在 Created
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var handler = CreateHandler("search", new ToolDescriptor
        {
            Name = "search",
            DeclaredSideEffect = ToolSideEffect.None,
            RecoveryStrategy = ToolRecoveryStrategy.SafeReplay
        });
        var dispatcher = new RealToolDispatcher(new IToolHandler[] { handler });
        var journal = new InMemoryToolDispatchJournal();
        var durableExecutor = new DefaultDurableToolExecutor(dispatcher, journal);
        var lease = new InMemoryAgentRunLease();

        var services = new ServiceCollection();
        services.AddSingleton<IAgentRunEventStore>(eventStore);
        services.AddSingleton<IToolDispatcher>(dispatcher);
        services.AddSingleton<IAgentModelTransport>(new DeterministicAgentModelTransport());
        services.AddSingleton<IDurableToolExecutor>(durableExecutor);
        var serviceProvider = services.BuildServiceProvider();

        // LeaseDuration=2min、HeartbeatInterval=40s（满足 >= 3 倍校验）：Run 在 <1s 内完成，
        // 不发生续约，因此 fence.ExpiresAt 应等于租约获取时间 + LeaseDuration（而非 +5min 推导值）。
        var options = new AgentHostOptions
        {
            LeaseEnabled = true,
            LeaseDuration = TimeSpan.FromMinutes(2),
            HeartbeatInterval = TimeSpan.FromSeconds(40),
            MaxGlobalRuns = 2,
            MaxWorkspaceRuns = 1,
            WorkerCount = 1,
            ChannelCapacity = 8,
            Owner = "host-lease-test"
        };

        await using var host = new AgentKernelHost(serviceProvider, runStore, lease, options);

        // DeadlineAt 为 null：旧实现会推导 +5min，新实现必须使用实际租约过期时间
        var run = BuildRun("search 查找文档", Ws);
        await runStore.CreateAsync(run);

        var startedAt = DateTimeOffset.UtcNow;
        await host.StartRunAsync(run);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        AgentRun? finalRun = null;
        while (DateTime.UtcNow < deadline)
        {
            finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            if (finalRun is not null && AgentRunStateMachine.IsTerminalState(finalRun.State))
            {
                break;
            }
            await Task.Delay(50);
        }

        Assert.IsNotNull(finalRun, "Run 应执行完成。");
        Assert.IsTrue(AgentRunStateMachine.IsTerminalState(finalRun!.State),
            $"Run 应进入终态，实际 {finalRun.State}。");
        Assert.AreEqual(1, handler.InvocationCount, "Tool 应恰好执行一次。");

        var fence = handler.LastContext?.LeaseFence;
        Assert.IsNotNull(fence, "Tool Handler 必须收到 lease fence（Host 已启用租约）。");
        Assert.IsFalse(string.IsNullOrEmpty(fence!.LeaseToken), "fence 应携带 lease token。");
        Assert.IsTrue(fence.FencingToken > 0, "fence 应携带 fencing token。");

        var endedAt = DateTimeOffset.UtcNow;
        Assert.IsTrue(fence.ExpiresAt >= startedAt,
            $"fence.ExpiresAt 不应早于租约获取时间（startedAt={startedAt:O}）。");
        Assert.IsTrue(fence.ExpiresAt <= startedAt.AddMinutes(2).AddSeconds(5),
            $"fence.ExpiresAt 应接近实际租约过期时间（获取时间 + LeaseDuration=2min），实际 {fence.ExpiresAt:O}。");
        Assert.IsTrue(fence.ExpiresAt < endedAt.AddMinutes(3),
            $"fence.ExpiresAt 不应是 +5min 推导值（实际 {fence.ExpiresAt:O}，结束时间 {endedAt:O}）。");
    }

    // ── 辅助方法 ───────────────────────────────────────────────────────────

    private static AgentToolCallRequest BuildToolCall(string toolName, string toolCallId, string? idempotencyKey = "idem-approval-lease") => new()
    {
        ToolName = toolName,
        Arguments = "{}",
        IdempotencyKey = idempotencyKey,
        ToolCallId = toolCallId
    };

    private static AgentRun BuildRun(string task, string workspaceId) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = workspaceId,
        SessionId = $"session-{workspaceId}",
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static RecordingToolHandler CreateHandler(string toolName, ToolDescriptor descriptor) => new(toolName, descriptor);

    private static (RealToolDispatcher Dispatcher, DefaultDurableToolExecutor Executor, InMemoryToolDispatchJournal Journal, InMemoryDurableToolResultStore ResultStore) CreateExecutor(
        RecordingToolHandler handler)
    {
        var dispatcher = new RealToolDispatcher(new IToolHandler[] { handler });
        var journal = new InMemoryToolDispatchJournal();
        var resultStore = new InMemoryDurableToolResultStore();
        var executor = new DefaultDurableToolExecutor(dispatcher, journal, resultStore);
        return (dispatcher, executor, journal, resultStore);
    }

    /// <summary>录制 Tool Handler：记录调用次数与最近一次上下文（含透传的 LeaseFence）。</summary>
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

        public ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            LastContext = context;
            return ValueTask.FromResult(new ToolHandlerResult
            {
                Succeeded = true,
                Result = "ok",
                SideEffect = ToolSideEffect.None
            });
        }
    }
}
