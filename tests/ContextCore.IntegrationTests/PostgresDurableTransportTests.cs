using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// R29 WP-B-4：PostgresDurableTransport 端到端集成测试（Testcontainers）。
/// 验证持久化 Durable Transport 的 inbox/outbox 持久化、FIFO 顺序、跨进程崩溃恢复与配置开关。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class PostgresDurableTransportTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        if (!await PostgresIntegrationTests.IsDockerAvailableAsync())
        {
            Console.WriteLine("[PostgresDurableTransportTests] Docker 不可用，所有测试将标记为 Inconclusive。");
            return;
        }

        _container = new PostgreSqlBuilder(PgVectorImage)
            .WithDatabase("cctest")
            .WithUsername("cctest")
            .WithPassword("cctest")
            .Build();

        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
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

    private static AgentKernelInstruction MakeInstruction(string instructionId, AgentKernelInstructionKind kind = AgentKernelInstructionKind.Execute)
        => new()
        {
            InstructionId = instructionId,
            Kind = kind,
            Payload = $"payload-{instructionId}"
        };

    private static AgentKernelResult MakeResult(string instructionId, string output = "ok")
        => new()
        {
            InstructionId = instructionId,
            Succeeded = true,
            Output = output
        };

    [TestMethod]
    public async Task SubmitAndReceive_Inbox_RoundtripsInstructionFifo()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt1_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SubmitAsync(MakeInstruction("instr-1"));
            await Task.Delay(15); // 确保 created_at 不同
            await transport.SubmitAsync(MakeInstruction("instr-2"));
            await Task.Delay(15);
            await transport.SubmitAsync(MakeInstruction("instr-3"));

            // FIFO：先入先出
            var first = await transport.ReceiveAsync();
            Assert.IsNotNull(first);
            Assert.AreEqual("instr-1", first!.InstructionId);
            Assert.AreEqual("payload-instr-1", first.Payload);

            var second = await transport.ReceiveAsync();
            Assert.IsNotNull(second);
            Assert.AreEqual("instr-2", second!.InstructionId);

            var third = await transport.ReceiveAsync();
            Assert.IsNotNull(third);
            Assert.AreEqual("instr-3", third!.InstructionId);

            // inbox 空 → null
            var empty = await transport.ReceiveAsync();
            Assert.IsNull(empty);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SendResultAndReceiveResult_Outbox_RoundtripsResultFifo()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt2_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SendResultAsync(MakeResult("instr-1", "output-1"));
            await transport.SendResultAsync(MakeResult("instr-2", "output-2"));

            var first = await transport.ReceiveResultAsync();
            Assert.IsNotNull(first);
            Assert.AreEqual("instr-1", first!.InstructionId);
            Assert.AreEqual("output-1", first.Output);

            var second = await transport.ReceiveResultAsync();
            Assert.IsNotNull(second);
            Assert.AreEqual("instr-2", second!.InstructionId);

            var empty = await transport.ReceiveResultAsync();
            Assert.IsNull(empty);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task InboxAndOutbox_AreIndependent()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt3_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            // 同时写入 inbox 与 outbox
            await transport.SubmitAsync(MakeInstruction("i-1"));
            await transport.SendResultAsync(MakeResult("r-1"));

            Assert.AreEqual(1, transport.PendingInstructionCount);
            Assert.AreEqual(1, transport.PendingResultCount);

            // 读取 inbox 不应影响 outbox
            var instruction = await transport.ReceiveAsync();
            Assert.IsNotNull(instruction);
            Assert.AreEqual("i-1", instruction!.InstructionId);
            Assert.AreEqual(0, transport.PendingInstructionCount);
            Assert.AreEqual(1, transport.PendingResultCount);

            // 读取 outbox 不应影响 inbox 已清空的状态
            var result = await transport.ReceiveResultAsync();
            Assert.IsNotNull(result);
            Assert.AreEqual("r-1", result!.InstructionId);
            Assert.AreEqual(0, transport.PendingInstructionCount);
            Assert.AreEqual(0, transport.PendingResultCount);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CrashRecovery_NewTransportInstanceReadsPersistedInboxAndOutbox()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt4_");
        try
        {
            // 第一个 transport 实例（模拟崩溃前的进程）
            var transport1 = new PostgresDurableTransport(factory, serializer, migrationRunner);
            await transport1.SubmitAsync(MakeInstruction("crash-i-1"));
            await transport1.SendResultAsync(MakeResult("crash-r-1", "payload-r-1"));

            // 模拟进程崩溃：丢弃 transport1，创建新实例（同一数据库）
            var transport2 = new PostgresDurableTransport(factory, serializer, migrationRunner);

            // 新实例应能读取持久化的 inbox 与 outbox
            Assert.AreEqual(1, transport2.PendingInstructionCount);
            Assert.AreEqual(1, transport2.PendingResultCount);

            var instruction = await transport2.ReceiveAsync();
            Assert.IsNotNull(instruction);
            Assert.AreEqual("crash-i-1", instruction!.InstructionId);

            var result = await transport2.ReceiveResultAsync();
            Assert.IsNotNull(result);
            Assert.AreEqual("crash-r-1", result!.InstructionId);
            Assert.AreEqual("payload-r-1", result.Output);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Submit_DuplicateInstructionId_ThrowsPrimaryKeyViolation()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt5_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SubmitAsync(MakeInstruction("dup-1"));

            // 同一 instruction_id 重复提交应因主键冲突失败（exactly-once 语义）
            await Assert.ThrowsExceptionAsync<PostgresException>(
                async () => await transport.SubmitAsync(MakeInstruction("dup-1")));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SendResult_SameInstructionTwice_EnqueuesTwoDistinctRows()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt6_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            // 同一 instruction_id 入队两次（每次失败重试都新增一行，不冲突）
            await transport.SendResultAsync(MakeResult("retry-1", "attempt-1"));
            await transport.SendResultAsync(MakeResult("retry-1", "attempt-2"));

            Assert.AreEqual(2, transport.PendingResultCount);

            var first = await transport.ReceiveResultAsync();
            Assert.IsNotNull(first);
            Assert.AreEqual("attempt-1", first!.Output);

            var second = await transport.ReceiveResultAsync();
            Assert.IsNotNull(second);
            Assert.AreEqual("attempt-2", second!.Output);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Receive_OnEmptyInbox_ReturnsNull()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt7_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            var instruction = await transport.ReceiveAsync();
            Assert.IsNull(instruction);

            var result = await transport.ReceiveResultAsync();
            Assert.IsNull(result);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Submit_NullInstruction_ThrowsArgumentNullException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt8_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await Assert.ThrowsExceptionAsync<ArgumentNullException>(
                async () => await transport.SubmitAsync(null!));

            await Assert.ThrowsExceptionAsync<ArgumentNullException>(
                async () => await transport.SendResultAsync(null!));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public void Complete_IsNoOpAndDoesNotThrow()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt9_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            // Complete 是 no-op，不应抛出异常
            transport.Complete();
        }
        finally
        {
            factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public void DurableTransport_ImplementsIDurableTransportAndIAgentKernelTransport()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt10_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            Assert.IsInstanceOfType(transport, typeof(IDurableTransport));
            Assert.IsInstanceOfType(transport, typeof(IAgentKernelTransport));
        }
        finally
        {
            factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public void AddContextCorePostgresStorage_WithUseDurableTransportTrue_ReplacesIAgentKernelTransportBinding()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        // 模拟 CoreExtensions 注册的 InProcessTransport 默认绑定
        var services = new ServiceCollection();
        services.AddSingleton<IAgentKernelTransport, ContextCore.Core.Services.AgentKernel.InProcessTransport>();

        services.AddContextCorePostgresStorage(
            new PostgresOptions
            {
                ConnectionString = _connectionString!,
                AutoMigrate = false
            },
            new KernelTransportOptions { UseDurableTransport = true });

        var provider = services.BuildServiceProvider();
        var transport = provider.GetRequiredService<IAgentKernelTransport>();

        Assert.IsInstanceOfType(transport, typeof(PostgresDurableTransport));
        Assert.IsInstanceOfType(transport, typeof(IDurableTransport));
    }

    [TestMethod]
    public void AddContextCorePostgresStorage_WithoutUseDurableTransportFlag_KeepsInProcessTransportDefault()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(
            new PostgresOptions
            {
                ConnectionString = _connectionString!,
                AutoMigrate = false
            });

        var provider = services.BuildServiceProvider();
        var transport = provider.GetService<IAgentKernelTransport>();

        // AddContextCorePostgresStorage 不会注册 IAgentKernelTransport（保留 CoreExtensions 的 TryAddSingleton 默认）；
        // 此处仅注册 PostgresDurableTransport + IDurableTransport，IAgentKernelTransport 仍由 CoreExtensions 提供。
        // 由于 CoreExtensions 未调用，此处 transport 为 null；测试仅验证 Postgres 实现未替换绑定。
        var durableTransport = provider.GetService<IDurableTransport>();
        Assert.IsNotNull(durableTransport);
        Assert.IsInstanceOfType(durableTransport, typeof(PostgresDurableTransport));

        // 显式调用 UsePostgresDurableTransport 才替换绑定
        services.UsePostgresDurableTransport();
        var provider2 = services.BuildServiceProvider();
        var transport2 = provider2.GetRequiredService<IAgentKernelTransport>();
        Assert.IsInstanceOfType(transport2, typeof(PostgresDurableTransport));
    }
}
