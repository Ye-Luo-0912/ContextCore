using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.IntegrationTests.TestFixtures;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.IntegrationTests;

// ===========================================================================
// 双 Host HA 集成测试
//
// 目标：证明 ProductionHA 部署下"同一时刻仅一个 Agent 执行平面"：
// 1. E2E_TwoHosts_SameRun_ExactlyOneExecutionPlane — 两个 AgentKernelHost 实例
// 共享同一 Postgres Run Lease，同一 Run 只被一个实例执行（租约 CAS 排斥），
// 外部 Tool 不会因第二实例启动而重复执行。
// 2. E2E_TwoHosts_LeaseHandover_AfterExpiry_FencingIncrements_OldTokenRejected —
// 旧 owner 的租约真实过期后，新 owner 抢占（fencing token 递增），
// 旧 token 的续约/释放全部失效——旧 owner 无法再执行副作用。
//
// 设计原则：
// - 使用真实 Postgres stores（PostgresAgentRunStore / PostgresAgentRunEventStore /
// PostgresToolDispatchJournal / PostgresAgentRunLease）。
// - Docker/Postgres 不可用时 Assert.Inconclusive 跳过。
// - 每个测试使用独立 tablePrefix 避免数据交叉污染。
// - 所有异步测试使用 CancellationTokenSource 超时防止挂起。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Production-Evidence")]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
[TestCategory("HaE2E")]
public sealed class R29H_ProductionEvidenceHaE2ETests : IAsyncDisposable
{
    private readonly PostgresE2EFixture _pg = new();

    [TestInitialize]
    public async Task InitializeAsync() => await _pg.StartAsync();

    [TestCleanup]
    public Task CleanupAsync() => _pg.DisposeAsync().AsTask();

    // =======================================================================
    // 测试 1：双 Host 同一 Run —— 恰好一个执行平面
    // =======================================================================

    [TestMethod]
    public async Task E2E_TwoHosts_SameRun_ExactlyOneExecutionPlane()
    {
        if (_pg.ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        var (factory, migrationRunner, serializer) = _pg.CreateInfrastructure("ha1_");
        try
        {
            await migrationRunner.MigrateAsync();
            var runStore = new PostgresAgentRunStore(factory, serializer, migrationRunner);
            var eventStore = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var leaseStore = new PostgresAgentRunLease(factory, serializer, migrationRunner);

            // 阻塞型 Tool：记录调用次数，首次调用时 signal，随后阻塞在 gate 上。
            var gate = new SemaphoreSlim(0, 1);
            var handler = new GatedToolHandler("search", gate);
            var dispatcher = new RealToolDispatcher(new IToolHandler[] { handler });
            dispatcher.Freeze();
            var durableExecutor = new DefaultDurableToolExecutor(dispatcher, journal);

            var transport = new ScriptedModelTransport(
                BuildToolCallResponse("search", """{"query":"ha-test"}""", "需要搜索 HA 测试数据"),
                BuildFinalAnswerResponse("HA 测试完成。"));

            var run = BuildRun("双 Host 单执行平面测试");
            await runStore.CreateAsync(run);

            // Host A + Host B：各自独立的 AgentKernelHost，共享同一 Postgres lease store。
            // LeaseDuration >= 3 × HeartbeatInterval（2min >= 3 × 40s）。
            var optionsA = new AgentHostOptions
            {
                LeaseEnabled = true,
                LeaseDuration = TimeSpan.FromMinutes(2),
                HeartbeatInterval = TimeSpan.FromSeconds(40),
                Owner = "host-a",
                DrainTimeout = TimeSpan.FromSeconds(10)
            };
            var optionsB = new AgentHostOptions
            {
                LeaseEnabled = true,
                LeaseDuration = TimeSpan.FromMinutes(2),
                HeartbeatInterval = TimeSpan.FromSeconds(40),
                Owner = "host-b",
                DrainTimeout = TimeSpan.FromSeconds(10)
            };

            await using var hostA = new AgentKernelHost(
                BuildServiceProvider(runStore, eventStore, transport, dispatcher, durableExecutor),
                runStore, leaseStore, optionsA);
            await using var hostB = new AgentKernelHost(
                BuildServiceProvider(runStore, eventStore, transport, dispatcher, durableExecutor),
                runStore, leaseStore, optionsB);

            // ── Host A 启动 Run：应获取租约并执行到 Tool（阻塞）──
            await hostA.StartRunAsync(run);
            var invoked = await WaitForTaskAsync(handler.Invoked.Task, TimeSpan.FromSeconds(30));
            Assert.IsTrue(invoked, "Host A 应在 30s 内开始执行 Tool。");
            Assert.AreEqual(1, handler.InvocationCount, "Host A 应恰好执行 Tool 1 次。");

            // ── Host B 启动同一 Run：租约被 Host A 持有 → 应跳过执行 ──
            await hostB.StartRunAsync(run);
            var hostBIdle = await WaitForConditionAsync(
                () => hostB.ActiveRunCount == 0, TimeSpan.FromSeconds(30));
            Assert.IsTrue(hostBIdle, "Host B 应尝试获取租约失败后退出（ActiveRunCount 归零）。");

            // 断言：Tool 仍只执行 1 次（Host B 未重复执行 —— 单执行平面）。
            Assert.AreEqual(1, handler.InvocationCount,
                $"Host B 启动后 Tool 调用次数应仍为 1（租约排斥，单执行平面），实际 {handler.InvocationCount}。");

            // ── 放行 Host A：Tool 返回 → Run 完成 ──
            gate.Release();
            var completed = await WaitForRunStateAsync(runStore, run.WorkspaceId, run.RunId, AgentRunState.Completed, TimeSpan.FromSeconds(30));
            Assert.IsTrue(completed, "放行后 Run 应进入 Completed 终态。");

            var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            Assert.IsNotNull(finalRun, "应能取回 Run。");
            Assert.AreEqual(AgentRunState.Completed, finalRun!.State, "Run 应为 Completed。");
            Assert.AreEqual(1, handler.InvocationCount,
                $"整个生命周期 Tool 应恰好执行 1 次（exactly-once），实际 {handler.InvocationCount}。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 2：双 Host 租约交接 —— 真实过期后 fencing 递增，旧 token 全部失效
    // =======================================================================

    [TestMethod]
    public async Task E2E_TwoHosts_LeaseHandover_AfterExpiry_FencingIncrements_OldTokenRejected()
    {
        if (_pg.ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        var (factory, migrationRunner, serializer) = _pg.CreateInfrastructure("ha2_");
        try
        {
            await migrationRunner.MigrateAsync();
            var leaseStore = new PostgresAgentRunLease(factory, serializer, migrationRunner);
            var runId = "run-ha2-" + Guid.NewGuid().ToString("N");

            // ── Host A 获取短租约（3s）──
            var leaseA = await leaseStore.TryAcquireAsync(runId, TimeSpan.FromSeconds(3), "host-A", CancellationToken.None);
            Assert.IsNotNull(leaseA, "Host A 应获取租约。");
            Assert.AreEqual(1, leaseA!.FencingToken, "首次获取的 fencing token 应为 1。");

            // ── 等待真实过期（lease_expires_at < now）──
            await Task.Delay(TimeSpan.FromSeconds(4));

            // 旧 owner 续约必须失败（lease_expires_at 已过 + token 校验）。
            var renewed = await leaseStore.RenewAsync(runId, leaseA.LeaseToken, TimeSpan.FromMinutes(2));
            Assert.IsFalse(renewed, "过期租约的旧 token 续约必须失败。");

            // ── Host B 抢占：fencing token 递增 ──
            var leaseB = await leaseStore.TryAcquireAsync(runId, TimeSpan.FromMinutes(2), "host-B", CancellationToken.None);
            Assert.IsNotNull(leaseB, "过期后新 owner 应能抢占租约。");
            Assert.AreEqual(2, leaseB!.FencingToken, "抢占后 fencing token 必须递增（旧 owner 的副作用写入会被 fence 拒绝）。");

            // 旧 owner 用旧 token 释放：0 行受影响，不影响新 owner 的租约。
            await leaseStore.ReleaseAsync(runId, leaseA.LeaseToken, CancellationToken.None);
            var stillHeld = await leaseStore.HasActiveLeaseAsync(runId);
            Assert.IsTrue(stillHeld, "旧 owner 用过期 token 释放不应影响新 owner 的活跃租约。");

            // 旧 fence 已真实过期 → 旧 owner 的任何 fence 校验都会拒绝副作用。
            Assert.IsTrue(leaseA.ExpiresAt < DateTimeOffset.UtcNow, "旧 owner 的 lease fence 应已过期。");

            // ── 新 owner 正常释放 ──
            await leaseStore.ReleaseAsync(runId, leaseB.LeaseToken, CancellationToken.None);
            var released = await leaseStore.HasActiveLeaseAsync(runId);
            Assert.IsFalse(released, "新 owner 释放后不应有活跃租约。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    private static IServiceProvider BuildServiceProvider(
        IAgentRunStore runStore,
        IAgentRunEventStore eventStore,
        IAgentModelTransport transport,
        IToolDispatcher dispatcher,
        IDurableToolExecutor durableExecutor)
    {
        var services = new ServiceCollection();
        // AgentKernelHost.CreateActor 通过接口类型解析依赖（GetService(typeof(IAgentModelTransport)) 等），
        // 因此必须以接口类型注册，否则解析为 null 导致 Actor 缺依赖。
        services.AddSingleton<IAgentRunStore>(runStore);
        services.AddSingleton<IAgentRunEventStore>(eventStore);
        services.AddSingleton<IAgentLoopPolicy>(new DefaultAgentLoopPolicy());
        services.AddSingleton<IAgentModelTransport>(transport);
        services.AddSingleton<IToolDispatcher>(dispatcher);
        services.AddSingleton<IDurableToolExecutor>(durableExecutor);
        return services.BuildServiceProvider();
    }

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = "run-ha-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = "ws-ha-prodevidence",
        SessionId = "session-ha-prodevidence",
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 5 }
    };

    private static AgentModelResponse BuildToolCallResponse(string toolName, string arguments, string content) => new()
    {
        Content = content,
        ToolCalls = new[]
        {
            new AgentToolCallRequest { ToolName = toolName, Arguments = arguments }
        },
        IsFinalAnswer = false,
        TokensConsumed = 10,
        Duration = TimeSpan.FromMilliseconds(5),
        InputTokens = 8,
        OutputTokens = 2,
        ModelId = "scripted-ha-transport"
    };

    private static AgentModelResponse BuildFinalAnswerResponse(string content) => new()
    {
        Content = content,
        ToolCalls = Array.Empty<AgentToolCallRequest>(),
        IsFinalAnswer = true,
        TokensConsumed = 15,
        Duration = TimeSpan.FromMilliseconds(5),
        InputTokens = 10,
        OutputTokens = 5,
        ModelId = "scripted-ha-transport"
    };

    private static async Task<bool> WaitForTaskAsync(Task task, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await task.WaitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(100);
        }
        return condition();
    }

    private static async Task<bool> WaitForRunStateAsync(
        IAgentRunStore runStore, string workspaceId, string runId, AgentRunState expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = await runStore.GetAsync(workspaceId, runId);
            if (run is not null && run.State == expected)
            {
                return true;
            }
            await Task.Delay(100);
        }
        var last = await runStore.GetAsync(workspaceId, runId);
        return last is not null && last.State == expected;
    }

    // ── 测试 stub ─────────────────────────────────────────────────────────

    /// <summary>
    /// 记录调用次数、首次调用 signal、随后阻塞在 gate 上的 Tool Handler。
    /// 用于验证双 Host 场景下 Tool 只被持租约的 Host 执行一次。
    /// </summary>
    private sealed class GatedToolHandler : IToolHandler
    {
        private readonly SemaphoreSlim _gate;
        private int _invocationCount;

        public string ToolName { get; }
        public ToolDescriptor Descriptor => new()
        {
            Name = ToolName,
            DeclaredSideEffect = ToolSideEffect.None,
            RequiresApproval = false,
            RequiresIdempotencyKey = false,
            RequiresLeaseFence = false,
            RecoveryStrategy = ToolRecoveryStrategy.SafeReplay,
            MaximumExecutionTime = TimeSpan.FromMinutes(5)
        };
        public string? Description => $"Gated tool: {ToolName}";
        public string? ParametersJsonSchema => "{}";
        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public TaskCompletionSource Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedToolHandler(string toolName, SemaphoreSlim gate)
        {
            ToolName = toolName;
            _gate = gate;
        }

        public async ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            Invoked.TrySetResult();
            await _gate.WaitAsync(cancellationToken);
            return new ToolHandlerResult
            {
                Succeeded = true,
                Result = "gated-tool-returned",
                SideEffect = ToolSideEffect.None
            };
        }
    }

    /// <summary>按顺序返回预设响应序列的 IAgentModelTransport。</summary>
    private sealed class ScriptedModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse[] _responses;
        private int _callCount;

        public ScriptedModelTransport(params AgentModelResponse[] responses)
        {
            _responses = responses;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            var response = index < _responses.Length ? _responses[index] : _responses[^1];
            return ValueTask.FromResult(response);
        }

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _pg.DisposeAsync();
    }
}
