using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// ProductionHA Composition Root 验收测试
//
// 修复第五节审查结论："HA 测试仍手工构造 Store、Host 和 Scripted Transport，
// 没有经过正式 ProductionHA Composition Root，因此只证明局部 Lease 行为，
// 不证明真实配置、Readiness、安全和 HostedService 组合正确。"
//
// 本文件所有测试都经过正式 ProductionHA 组合根（与 Program.cs 相同的
// AddContextCorePostgresStorage → AddContextCore → AddContextCoreRuntime 路径 +
// 真实 Postgres（Testcontainers）），唯一替换件为 Scripted Transport（经 DI
// 注册），验证真实配置、Readiness、Lease Fencing 与 HostedService 组合，
// 不再手工 new Store/Host。
//
// Postgres/Docker 不可用时 Assert.Inconclusive 跳过（CI integration-postgres
// job 中 Docker 始终可用，因此该跳过只影响本地）。
// ===========================================================================

[TestClass]
[TestCategory("Integration")]
[TestCategory("R29-Hard-Gate")]
[TestCategory("ProductionHA-CompositionRoot")]
public sealed class R29H_ProductionHACompositionRootTests
{
    // =======================================================================
    // 测试 1：组合根解析持久化 HA 平面（真实配置 + Readiness）
    // =======================================================================
    [TestMethod]
    public async Task ProductionHA_CompositionRoot_ResolvesDurableHAPlane_AndReadiness()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — ProductionHA 组合根测试已跳过。此结果不证明组合根通过。");
            return;
        }

        await using (container)
        await using (var provider = BuildProductionHAComposition(container, "ha_root_resolve_"))
        {
            // ── 调度器：组合根解析的 IAgentRunScheduler 必须是 AgentKernelHost（非手工构造）──
            var scheduler = provider.GetRequiredService<IAgentRunScheduler>();
            Assert.IsInstanceOfType(scheduler, typeof(AgentKernelHost),
                "ProductionHA 组合根必须解析 AgentKernelHost 作为 IAgentRunScheduler。");

            // ── 租约：必须是 Postgres 实现（持久化 fencing），而非进程内实现 ──
            var lease = provider.GetRequiredService<IAgentRunLease>();
            Assert.IsInstanceOfType(lease, typeof(PostgresAgentRunLease),
                "ProductionHA 组合根必须解析 PostgresAgentRunLease。");
            Assert.IsNotInstanceOfType(lease, typeof(InMemoryAgentRunLease),
                "ProductionHA 组合根不允许回退到 InMemoryAgentRunLease（HA 必须持久化租约）。");

            // ── 存储：持久化实现（非 in-memory）──
            Assert.IsInstanceOfType(provider.GetRequiredService<IAgentRunStore>(), typeof(IPersistentAgentRunStore),
                "ProductionHA 组合根必须解析持久化 IAgentRunStore。");
            Assert.IsInstanceOfType(provider.GetRequiredService<IAgentRunEventStore>(), typeof(IPersistentAgentRunEventStore),
                "ProductionHA 组合根必须解析持久化 IAgentRunEventStore。");

            // ── 对账存储与协调器可解析（执行平面完整）──
            // -B1：IToolReconciliationStore 必须是 Postgres 实现（跨进程持久化真相源），
            // 不允许回退到 InMemory（对账记录只在创建它的实例内存中 → 裁决丢失）。
            var reconciliationStore = provider.GetRequiredService<IToolReconciliationStore>();
            Assert.IsNotNull(reconciliationStore, "ProductionHA 组合根必须解析 IToolReconciliationStore。");
            Assert.IsInstanceOfType(reconciliationStore, typeof(PostgresToolReconciliationStore),
                "ProductionHA 组合根必须解析 PostgresToolReconciliationStore（Tool Reconciliation Control Plane 持久化真相源）。");
            Assert.IsNotInstanceOfType(reconciliationStore, typeof(InMemoryToolReconciliationStore),
                "ProductionHA 组合根不允许回退到 InMemoryToolReconciliationStore（对账记录必须跨进程持久化）。");
            Assert.IsNotNull(provider.GetRequiredService<ToolReconciliationCoordinator>(),
                "ProductionHA 组合根必须解析 ToolReconciliationCoordinator。");

            // ── 真实配置：ProductionHA 强制 LeaseEnabled=true ──
            var options = provider.GetRequiredService<AgentHostOptions>();
            Assert.IsTrue(options.LeaseEnabled,
                "ProductionHA 组合根下 AgentHostOptions.LeaseEnabled 必须为 true（HA 多实例租约竞争）。");

            // ── HostedService 组合：WorkerRegistry 必须包含 HA 平面 Worker ──
            var registry = provider.GetRequiredService<ProductionRuntimeWorkerRegistry>();
            CollectionAssert.Contains(registry.WorkerTypeNames.ToList(), nameof(AgentRunRecoveryWorker),
                "ProductionHA 组合根必须注册 AgentRunRecoveryWorker。");
            CollectionAssert.Contains(registry.WorkerTypeNames.ToList(), nameof(ToolReconciliationWorker),
                "ProductionHA 组合根必须注册 ToolReconciliationWorker。");
            CollectionAssert.Contains(registry.WorkerTypeNames.ToList(), nameof(ModelStateReconcilerWorker),
                "ProductionHA 组合根必须注册 ModelStateReconcilerWorker。");
            CollectionAssert.Contains(registry.WorkerTypeNames.ToList(), nameof(CanaryLeaderHostedService),
                "ProductionHA 组合根必须注册 CanaryLeaderHostedService。");

            // ── Readiness：真实 Postgres Ping 通过组合根报告 ready ──
            var readiness = provider.GetRequiredService<ProductionRuntimeReadinessService>();
            var result = await readiness.CheckReadinessAsync();
            var pgCheck = result.Checks.FirstOrDefault(c => c.Name == "postgres-connection");
            Assert.IsNotNull(pgCheck, "Readiness 检查必须包含 postgres-connection 项。");
            Assert.AreEqual("ready", pgCheck!.Status,
                $"Readiness 的 postgres-connection 检查应 ready（真实 Postgres），实际 {pgCheck.Status}。");
        }
    }

    // =======================================================================
    // 测试 2：两个独立组合根（模拟双节点）共享同一 Postgres —— 租约 fencing
    // =======================================================================
    [TestMethod]
    public async Task ProductionHA_CompositionRoot_TwoRoots_SameRun_LeaseFencing()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — ProductionHA 组合根测试已跳过。此结果不证明组合根通过。");
            return;
        }

        await using (container)
        await using (var providerA = BuildProductionHAComposition(container, "ha_root_fence_"))
        await using (var providerB = BuildProductionHAComposition(container, "ha_root_fence_"))
        {
            var leaseA = providerA.GetRequiredService<IAgentRunLease>();
            var leaseB = providerB.GetRequiredService<IAgentRunLease>();
            var runId = "run-fence-" + Guid.NewGuid().ToString("N");
            const string ws = "ws-ha-fence";

            // node-A 获取短租约（2s）
            var leaseAValue = await leaseA.TryAcquireAsync(ws, runId, TimeSpan.FromSeconds(2), "node-A");
            Assert.IsNotNull(leaseAValue, "node-A 应获取租约。");
            Assert.AreEqual(1, leaseAValue!.FencingToken, "首次获取的 fencing token 应为 1。");

            // node-B（独立组合根）尝试获取同一 run 的租约 → 必须失败（fencing 互斥）
            var leaseBValue = await leaseB.TryAcquireAsync(ws, runId, TimeSpan.FromMinutes(2), "node-B");
            Assert.IsNull(leaseBValue,
                "node-B 在 node-A 持有时不应获取租约（组合根解析的 Postgres lease fencing）。");

            // ── 真实过期：node-A 租约到期后，旧 token 续约失败，node-B 抢占且 fencing 递增 ──
            await Task.Delay(TimeSpan.FromSeconds(3));
            var renewed = await leaseA.RenewAsync(ws, runId, leaseAValue.LeaseToken, TimeSpan.FromMinutes(2));
            Assert.IsFalse(renewed, "过期租约的旧 token 续约必须失败（旧 owner 无法再执行副作用）。");

            var leaseBValue2 = await leaseB.TryAcquireAsync(ws, runId, TimeSpan.FromMinutes(2), "node-B");
            Assert.IsNotNull(leaseBValue2, "node-A 租约过期后 node-B 应能抢占租约。");
            Assert.AreEqual(2, leaseBValue2!.FencingToken,
                "抢占后 fencing token 必须递增（旧 owner 的副作用写入会被 fence 拒绝）。");
        }
    }

    // =======================================================================
    // 测试 3：组合根端到端 —— Scripted Transport 经 DI 注入，Run 完整执行
    // =======================================================================
    [TestMethod]
    public async Task ProductionHA_CompositionRoot_ExecutesRunEndToEnd_ThroughDurablePlane()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — ProductionHA 组合根测试已跳过。此结果不证明组合根通过。");
            return;
        }

        await using (container)
        {
            var scripted = new ScriptedModelTransport(
                BuildFinalAnswerResponse("组合根端到端执行完成。"));
            await using var provider = BuildProductionHAComposition(
                container, "ha_root_e2e_",
                services => services.AddSingleton<IAgentModelTransport>(scripted));

            var runStore = provider.GetRequiredService<IAgentRunStore>();
            // 经 IAgentRunScheduler 抽象解析（组合根保证同实例），以具体类型调用 StartRunAsync
            // （IAgentRunScheduler 接口仅暴露非阻塞 TryEnqueueAsync）。
            var scheduler = provider.GetRequiredService<AgentKernelHost>();
            Assert.AreSame(scheduler, provider.GetRequiredService<IAgentRunScheduler>(),
                "IAgentRunScheduler 必须解析为与 AgentKernelHost 相同的单例实例。");
            var run = BuildRun("组合根端到端测试");

            await runStore.CreateAsync(run);
            await scheduler.StartRunAsync(run);

            var completed = await WaitForRunStateAsync(
                runStore, run.WorkspaceId, run.RunId, AgentRunState.Completed, TimeSpan.FromSeconds(60));
            if (!completed)
            {
                var failedRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
                Assert.Fail(
                    $"组合根解析的调度器应在 60s 内将 Run 执行到 Completed；当前状态 {failedRun?.State}，"
                    + $"失败原因：{failedRun?.FailureReason}");
            }

            Assert.IsTrue(scripted.CallCount >= 1,
                "组合根解析的 Scripted Transport 应被调用（真实配置驱动执行）。");

            var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            Assert.IsNotNull(finalRun, "应能取回 Run。");
            Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
                "Run 应最终为 Completed（经组合根解析的持久化 store 记录）。");
        }
    }

    // =======================================================================
    // 组合根构建（与 Program.cs 相同的 ProductionHA 组合路径）
    // =======================================================================

    private static ServiceProvider BuildProductionHAComposition(
        PostgreSqlContainer container,
        string tablePrefix,
        Action<IServiceCollection>? customize = null)
    {
        var connectionString = container.GetConnectionString();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "postgres",
                ["Storage:PostgresConnectionString"] = connectionString,
                ["ContextCoreRuntime:Profile"] = "ProductionHA"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = connectionString,
            AutoMigrate = true, // 与生产启动一致：首次使用即应用幂等 schema
            EnablePgVectorExtension = true,
            TablePrefix = tablePrefix
        });
#pragma warning disable CS0618 // AddContextCore(IServiceCollection) 已过时；为与 Program.cs 组合顺序保持一致而保留
        services.AddContextCore();
#pragma warning restore CS0618
        services.AddContextCoreRuntime(config);

        // 提供 IHostApplicationLifetime（Program.cs 中由 WebApplication 提供），
        // 并触发 ApplicationStarted 使 ReadinessService 判定应用已启动。
        var lifetime = new TestHostApplicationLifetime();
        lifetime.TriggerApplicationStarted();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        // 提供日志基础设施（WebApplication 由 host 自动注册；裸 ServiceCollection 需显式注册）。
        services.AddLogging();

        customize?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static async Task<PostgreSqlContainer?> TryStartPostgresAsync()
    {
        const string pgVectorImage = "pgvector/pgvector:pg17";
        try
        {
            var container = new PostgreSqlBuilder(pgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await container.StartAsync(cts.Token);
            return container;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[R29H_ProductionHACompositionRootTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = "run-root-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = "ws-ha-composition-root",
        SessionId = "session-ha-composition-root",
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 5 }
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
        ModelId = "scripted-composition-root-transport"
    };

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

    // =======================================================================
    // 测试 stub
    // =======================================================================

    /// <summary>按顺序返回预设响应序列的 IAgentModelTransport（经 DI 注入组合根）。</summary>
    private sealed class ScriptedModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse[] _responses;
        private int _callCount;

        public ScriptedModelTransport(params AgentModelResponse[] responses)
        {
            _responses = responses;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(NextResponse());

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(NextResponse());

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(NextResponse());

        private AgentModelResponse NextResponse()
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            return index < _responses.Length ? _responses[index] : _responses[^1];
        }
    }

    /// <summary>IHostApplicationLifetime stub（触发 ApplicationStarted 使 Readiness 判定已启动）。</summary>
    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void TriggerApplicationStarted() => _started.Cancel();

        public void StopApplication()
        {
        }
    }
}
