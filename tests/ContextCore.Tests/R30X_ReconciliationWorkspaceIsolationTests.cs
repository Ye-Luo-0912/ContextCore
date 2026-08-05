using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

// ===========================================================================
// Tool 对账 Workspace 隔离测试
//
// 验证对账记录的 Workspace 隔离贯穿：
// - ListByRunAsync / HasUnresolvedForRunAsync 按 (WorkspaceId, RunId) 限定——
//   跨租户同 RunId 不互相可见/不互相阻塞；
// - Coordinator.ResolveAsync 校验记录属于指定 Workspace + Run——
//   跨租户 reconciliationId 视为不存在（返回 1），杜绝凭 Run 存在性裁决他人记录；
// - Actor 创建的对账记录 ReconciliationId 包含 Workspace（跨租户唯一）。
// ===========================================================================

[TestClass]
[TestCategory("Agent-Actor")]
public sealed class R30X_ReconciliationWorkspaceIsolationTests
{
    private const string WsA = "ws-tenant-a";
    private const string WsB = "ws-tenant-b";
    private const string RunId = "run-shared-id";

    [TestMethod]
    public async Task Store_ListByRun_IsScopedByWorkspace()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-a-1", "req-1", WsA), cts.Token);
        await store.CreateAsync(BuildRecord("rec-a-2", "req-2", WsA), cts.Token);
        await store.CreateAsync(BuildRecord("rec-b-1", "req-1", WsB), cts.Token);

        // 同一 RunId 下，各 Workspace 只看到自己的记录（跨租户 RequestId 相同也不串扰）。
        var tenantA = await store.ListByRunAsync(WsA, RunId, cts.Token);
        Assert.AreEqual(2, tenantA.Count, "租户 A 应只看到自己的 2 条记录。");

        var tenantB = await store.ListByRunAsync(WsB, RunId, cts.Token);
        Assert.AreEqual(1, tenantB.Count, "租户 B 应只看到自己的 1 条记录。");
    }

    [TestMethod]
    public async Task Store_HasUnresolved_IsScopedByWorkspace()
    {
        var store = new InMemoryToolReconciliationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-a-1", "req-1", WsA), cts.Token);

        // 租户 A 有未决记录；租户 B 的同一 RunId 不受影响。
        Assert.IsTrue(await store.HasUnresolvedForRunAsync(WsA, RunId, cts.Token), "A 的未决记录应可见。");
        Assert.IsFalse(await store.HasUnresolvedForRunAsync(WsB, RunId, cts.Token),
            "B 的同一 RunId 不应被 A 的未决记录阻塞。");

        // A 裁决完毕 → A 也无未决；B 全程不受影响。
        var lease = await store.TryBeginAsync("rec-a-1", "test", TimeSpan.FromMinutes(1), cts.Token);
        await store.MarkResolvedAsync("rec-a-1", lease!.LeaseToken, new ToolReconciliationOutcome { SideEffectOccurred = true }, cts.Token);
        Assert.IsFalse(await store.HasUnresolvedForRunAsync(WsA, RunId, cts.Token));
        Assert.IsFalse(await store.HasUnresolvedForRunAsync(WsB, RunId, cts.Token));
    }

    [TestMethod]
    public async Task Coordinator_Resolve_CrossWorkspace_ReturnsNotFound()
    {
        var store = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(store, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-a-1", "req-1", WsA), cts.Token);

        // 用租户 B 的 Workspace 裁决 A 的记录 → 视为不存在（1），记录保持 Pending。
        var code = await coordinator.ResolveAsync(
            WsB, RunId, "rec-a-1",
            new ToolReconciliationOutcome { SideEffectOccurred = true }, cts.Token);
        Assert.AreEqual(1, code, "跨 Workspace 的 reconciliationId 应视为不存在。");
        Assert.AreEqual(ToolReconciliationStatus.Pending, (await store.GetAsync("rec-a-1", cts.Token))!.Status,
            "跨租户裁决不得终态化记录。");
    }

    [TestMethod]
    public async Task Coordinator_Resolve_CrossRun_ReturnsNotFound()
    {
        var store = new InMemoryToolReconciliationStore();
        var coordinator = new ToolReconciliationCoordinator(store, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await store.CreateAsync(BuildRecord("rec-a-1", "req-1", WsA), cts.Token);

        // 同 Workspace 但错 Run → 视为不存在（记录属于其他 Run）。
        var code = await coordinator.ResolveAsync(
            WsA, "run-other", "rec-a-1",
            new ToolReconciliationOutcome { SideEffectOccurred = true }, cts.Token);
        Assert.AreEqual(1, code, "RunId 不匹配应视为不存在。");
        Assert.AreEqual(ToolReconciliationStatus.Pending, (await store.GetAsync("rec-a-1", cts.Token))!.Status);
    }

    [TestMethod]
    public async Task Coordinator_Resolve_SameWorkspaceAndRun_Succeeds()
    {
        var handler = new ReconTestHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (_, executor, journal, _) = CreateExecutor(handler);
        var store = new InMemoryToolReconciliationStore(journal: journal);
        var coordinator = new ToolReconciliationCoordinator(store, NullLogger<ToolReconciliationCoordinator>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var toolCall = BuildToolCall("bank-transfer", "arg-A");
        var result = await executor.ExecuteAsync(RunId, WsA, toolCall, 0, cts.Token);
        await store.CreateAsync(BuildRecord("rec-a-1", result.RequestId, WsA), cts.Token);

        var code = await coordinator.ResolveAsync(
            WsA, RunId, "rec-a-1",
            new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-1" }, cts.Token);
        Assert.AreEqual(0, code, "同 Workspace + Run 应裁决成功。");
        Assert.AreEqual(ToolReconciliationStatus.Resolved, (await store.GetAsync("rec-a-1", cts.Token))!.Status);
    }

    [TestMethod]
    public async Task Actor_ReconciliationId_ContainsWorkspace()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun(WsA, RunId);
        await runStore.CreateAsync(run);

        var handler = new ReconTestHandler("bank-transfer", ToolSideEffect.NonIdempotentWrite, "bank-recon");
        var (dispatcher, executor, _, _) = CreateExecutor(handler);
        var journal = new InMemoryToolDispatchJournal();
        var reconciliationStore = new InMemoryToolReconciliationStore(journal: journal);

        var transport = new SequenceModelTransport(new[]
        {
            new AgentModelResponse
            {
                Content = "调用转账",
                ToolCalls = new[] { BuildToolCall("bank-transfer", "arg-ws") },
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
            durableToolExecutor: executor,
            reconciliationStore: reconciliationStore);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(run, cts.Token);

        var records = await reconciliationStore.ListByRunAsync(WsA, RunId, cts.Token);
        Assert.AreEqual(1, records.Count, "模糊态 Tool 应创建一条对账记录。");
        Assert.IsTrue(records[0].ReconciliationId.StartsWith("rec:" + WsA + ":", StringComparison.Ordinal),
            $"ReconciliationId 应包含 Workspace（实际：{records[0].ReconciliationId}）。");

        // 同一 RunId 在另一 Workspace 无记录（记录 ID 含 Workspace → 跨租户不碰撞）。
        var tenantB = await reconciliationStore.ListByRunAsync(WsB, RunId, cts.Token);
        Assert.AreEqual(0, tenantB.Count, "另一租户的同一 RunId 不应看到本租户的记录。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────

    private static ToolReconciliationRecord BuildRecord(string reconciliationId, string requestId, string workspaceId)
        => new()
        {
            ReconciliationId = reconciliationId,
            RunId = RunId,
            WorkspaceId = workspaceId,
            RequestId = requestId,
            ToolName = "bank-transfer",
            ExternalOperationId = "ext-" + requestId,
            ReconciliationHandler = "bank-recon",
            Status = ToolReconciliationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static AgentRun BuildRun(string workspaceId, string runId) => new()
    {
        RunId = runId,
        WorkspaceId = workspaceId,
        SessionId = "session-ws-isolation",
        Task = "跨租户对账隔离验证",
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 10 }
    };

    private static AgentToolCallRequest BuildToolCall(string toolName, string arguments)
        => new() { ToolName = toolName, Arguments = $"{{\"arg\":\"{arguments}\"}}" };

    private static (RealToolDispatcher Dispatcher, DefaultDurableToolExecutor Executor, InMemoryToolDispatchJournal Journal, InMemoryDurableToolResultStore ResultStore) CreateExecutor(
        ReconTestHandler handler)
    {
        var dispatcher = new RealToolDispatcher(new[] { handler });
        var journal = new InMemoryToolDispatchJournal();
        var resultStore = new InMemoryDurableToolResultStore();
        var executor = new DefaultDurableToolExecutor(dispatcher, journal, resultStore);
        return (dispatcher, executor, journal, resultStore);
    }

    /// <summary>按顺序返回预设响应序列的模型传输（超出序列返回最后一个）。</summary>
    private sealed class SequenceModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse[] _responses;
        private int _callCount;

        public SequenceModelTransport(AgentModelResponse[] responses)
        {
            _responses = responses;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(
            string runId,
            IReadOnlyList<AgentMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            var response = index < _responses.Length ? _responses[index] : _responses[^1];
            return ValueTask.FromResult(response);
        }

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);
    }

    /// <summary>可声明副作用类型与对账 Handler 名的 Tool Handler（统计外部调用次数）。</summary>
    private sealed class ReconTestHandler : IToolHandler
    {
        private int _invocationCount;

        public ReconTestHandler(string toolName, ToolSideEffect sideEffect, string? reconciliationHandler)
        {
            ToolName = toolName;
            SideEffect = sideEffect;
            ReconciliationHandlerName = reconciliationHandler;
        }

        public string ToolName { get; }
        public ToolSideEffect SideEffect { get; }
        public string? ReconciliationHandlerName { get; }
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
            return ValueTask.FromResult(new ToolHandlerResult
            {
                Succeeded = true,
                Result = "ok",
                SideEffect = SideEffect
            });
        }
    }
}
