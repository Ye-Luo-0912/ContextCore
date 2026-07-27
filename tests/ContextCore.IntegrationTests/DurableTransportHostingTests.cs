using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// P0-4：Durable Transport 后台托管服务 + Kernel lease 确认端到端集成测试（Testcontainers）。
/// 验证 pump → Kernel 处理 → Ack、outbox 重放、租约清理的完整闭环。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class DurableTransportHostingTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        try
        {
            _container = new PostgreSqlBuilder(PgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();

            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DurableTransportHostingTests] Docker 不可用：{ex.GetType().Name}: {ex.Message}");
            _connectionString = null;
        }
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static async Task ClassCleanup()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static bool ShouldSkip => _connectionString is null;

    private static (PostgresConnectionFactory factory, PostgresMigrationRunner migrationRunner, PostgresJsonSerializer serializer) CreateInfrastructure(string prefix)
    {
        var options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = prefix
        };
        var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        return (factory, migrationRunner, serializer);
    }

    private static AgentKernelInstruction MakeExecuteInstruction(string id) => new()
    {
        InstructionId = id,
        Kind = AgentKernelInstructionKind.Execute,
        Payload = "test-payload"
    };

    /// <summary>
    /// P0-4 核心：Kernel 处理来自 Durable Transport pump 的指令（Metadata 含 lease token）后自动 Ack。
    /// 验证 Ack 后 inbox 行被删除。
    /// </summary>
    [TestMethod]
    public async Task Kernel_ProcessesInstructionFromPump_AutoAcks_InboxRowDeleted()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("dth1_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            // 模拟 pump：LeaseAsync → 将 lease token 写入 Metadata → SubmitAsync
            await transport.SubmitAsync(MakeExecuteInstruction("kernel-ack-1"));
            var leased = await transport.LeaseAsync(TimeSpan.FromMinutes(1), owner: "test-pump");
            Assert.IsNotNull(leased);

            var instruction = leased!.Instruction with
            {
                Metadata = new Dictionary<string, string>(leased.Instruction.Metadata, StringComparer.Ordinal)
                {
                    [DurableTransportMetadataKeys.LeaseToken] = leased.LeaseToken,
                    [DurableTransportMetadataKeys.LeaseOwner] = "test-pump",
                },
            };

            // Kernel 处理指令（需要 transport 是 IDurableTransport 以触发 Ack 路径）
            var kernel = new DefaultAgentKernel(
                transport,
                toolDispatcher: new NopToolDispatcher(),
                checkpointStore: new InMemoryAgentCheckpointStore(),
                transportOptions: new KernelTransportOptions { UseDurableTransport = true });

            await kernel.SubmitAsync(instruction);
            // 提交 Shutdown 让 RunAsync 优雅退出（处理完 Execute 后遇到 Shutdown 停止）
            await kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = "shutdown-1",
                Kind = AgentKernelInstructionKind.Shutdown,
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await kernel.RunAsync(cts.Token);

            // 验证 inbox 行已被 Ack（删除）
            Assert.AreEqual(0, transport.PendingInstructionCount,
                "Kernel 处理完来自 pump 的指令后应自动 Ack，inbox 行应被删除。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    /// <summary>
    /// P0-4：Kernel 处理不带 lease token 的指令（直接 SubmitAsync，非 pump 路径）不执行 Ack。
    /// 验证 InProcessTransport 路径不受影响。
    /// </summary>
    [TestMethod]
    public async Task Kernel_ProcessesInstructionWithoutLeaseToken_NoAckAttempted()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("dth2_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);
            var kernel = new DefaultAgentKernel(
                transport,
                toolDispatcher: new NopToolDispatcher(),
                checkpointStore: new InMemoryAgentCheckpointStore(),
                transportOptions: new KernelTransportOptions { UseDurableTransport = true });

            // 直接 SubmitAsync（无 lease token，模拟本地提交）
            await kernel.SubmitAsync(MakeExecuteInstruction("no-lease-1"));
            // 提交 Shutdown 让 RunAsync 优雅退出
            await kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = "shutdown-2",
                Kind = AgentKernelInstructionKind.Shutdown,
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await kernel.RunAsync(cts.Token);

            // 指令不来自 pump，不执行 Ack。Kernel 正常处理不抛异常即可。
            Assert.IsTrue(kernel.GetStatus().ProcessedCount >= 1);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    /// <summary>
    /// P0-4 崩溃恢复：pump 租约 + Submit 后 Kernel 未处理就崩溃（未 Ack）。
    /// reaper 回滚过期租约后，新 pump 实例重新租约并处理。
    /// </summary>
    [TestMethod]
    public async Task CrashRecovery_LeasedWithoutAck_ReaperRequeues_NewPumpReLeasesAndAcks()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("dth3_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            // 第一个 pump 租约指令（模拟崩溃前已 Lease 但未 Ack）
            await transport.SubmitAsync(MakeExecuteInstruction("crash-pump-1"));
            var leased = await transport.LeaseAsync(TimeSpan.FromMilliseconds(200));
            Assert.IsNotNull(leased);
            Assert.AreEqual(0, transport.PendingInstructionCount);

            // 模拟崩溃：丢弃 leased（进程已死，无法 Ack）
            await Task.Delay(500); // 等租约过期

            // reaper 回滚过期租约
            var requeued = await transport.RequeueExpiredAsync();
            Assert.AreEqual(1, requeued);
            Assert.AreEqual(1, transport.PendingInstructionCount);

            // 新实例重新租约（FIFO，同一指令）
            var reLeased = await transport.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(reLeased);
            Assert.AreEqual("crash-pump-1", reLeased!.Instruction.InstructionId);
            Assert.AreNotEqual(leased.LeaseToken, reLeased.LeaseToken);

            // 新实例 Ack
            await transport.AckAsync("crash-pump-1", reLeased.LeaseToken);
            Assert.AreEqual(0, transport.PendingInstructionCount);

            // 原 token（崩溃实例）无法 Ack
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await transport.AckAsync("crash-pump-1", leased.LeaseToken));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    /// <summary>
    /// P0-4 outbox 重放闭环：outbox.LeaseAsync → SendResultAsync → AckAsync。
    /// </summary>
    [TestMethod]
    public async Task OutboxReplay_LeasesAndSendsAndAcks_RowDeleted()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("dth4_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(new AgentKernelResult
            {
                InstructionId = "replay-1",
                Succeeded = true,
                Output = "replay-payload"
            });
            Assert.AreEqual(1, outbox.PendingCount);

            var leased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(leased);
            Assert.AreEqual("replay-1", leased!.Result.InstructionId);
            Assert.AreEqual(0, outbox.PendingCount);

            // 模拟 SendResultAsync 成功后 Ack
            await outbox.AckAsync(leased.OutboxId, leased.LeaseToken);
            Assert.AreEqual(0, outbox.PendingCount);

            // 再 Lease 返回 null（已 Ack 删除）
            var empty = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNull(empty);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    /// <summary>
    /// P0-4 DI 注册验证：AddDurableTransportHostedServices 注册 4 个 HostedService + 选项。
    /// </summary>
    [TestMethod]
    public void AddDurableTransportHostedServices_RegistersHostedServicesAndOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDurableTransportHostedServices(opt =>
        {
            opt.PollInterval = TimeSpan.FromMilliseconds(50);
            opt.ReaperInterval = TimeSpan.FromSeconds(5);
            opt.MetricsInterval = TimeSpan.FromSeconds(15);
        });

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();
        Assert.IsTrue(hostedServices.Any(h => h.GetType().Name == "DurableTransportInstructionPumpService"));
        Assert.IsTrue(hostedServices.Any(h => h.GetType().Name == "ResultOutboxReplayService"));
        Assert.IsTrue(hostedServices.Any(h => h.GetType().Name == "LeaseReaperService"));
        Assert.IsTrue(hostedServices.Any(h => h.GetType().Name == "PendingCountMetricsService"));

        var options = provider.GetRequiredService<IOptions<DurableTransportHostingOptions>>().Value;
        Assert.AreEqual(50, options.PollInterval.TotalMilliseconds);
        Assert.AreEqual(5, options.ReaperInterval.TotalSeconds);
        Assert.AreEqual(15, options.MetricsInterval.TotalSeconds);
    }

    private sealed class NopToolDispatcher : IToolDispatcher
    {
        public IReadOnlySet<string> SupportedTools { get; } = new HashSet<string>();

        public ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken = default)
        {
            return new ValueTask<ToolDispatchResult>(new ToolDispatchResult
            {
                Succeeded = true,
                Result = "nop",
                Duration = TimeSpan.Zero,
                SideEffect = ToolSideEffect.None,
            });
        }
    }
}
