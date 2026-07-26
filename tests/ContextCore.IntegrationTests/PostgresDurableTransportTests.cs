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
/// R29 WP-B-4 / P0-1：PostgresDurableTransport 端到端集成测试（Testcontainers）。
/// 验证持久化 Durable Transport 的 inbox/outbox 持久化、FIFO 顺序、跨进程崩溃恢复与配置开关。
/// P0-1：追加租约模型测试（LeaseAsync/AckAsync/NackAsync/RenewLeaseAsync/RequeueExpiredAsync + 结果变体），
/// 覆盖崩溃恢复、租约过期回滚、token 不匹配、续租等场景。
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
        // R14-PG 收口：直接尝试启动容器（与 PostgresToolDispatchJournalTests 一致），
        // 避免 IsDockerAvailableAsync 在 Windows named-pipe Docker Desktop 上误判。
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
            Console.WriteLine($"[PostgresDurableTransportTests] Docker 不可用：{ex.GetType().Name}: {ex.Message}");
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

    // ── P0-1：租约模型测试 ────────────────────────────────────────────

    [TestMethod]
    public async Task LeaseAndAck_Inbox_RoundtripsFifo()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt11_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);
            var leaseDuration = TimeSpan.FromMinutes(1);

            await transport.SubmitAsync(MakeInstruction("lease-1"));
            await Task.Delay(15);
            await transport.SubmitAsync(MakeInstruction("lease-2"));
            await Task.Delay(15);
            await transport.SubmitAsync(MakeInstruction("lease-3"));

            Assert.AreEqual(3, transport.PendingInstructionCount);

            // FIFO 租约 + Ack：每条指令 Ack 后从表中删除
            var first = await transport.LeaseAsync(leaseDuration);
            Assert.IsNotNull(first);
            Assert.AreEqual("lease-1", first!.Instruction.InstructionId);
            Assert.IsFalse(string.IsNullOrEmpty(first.LeaseToken));
            Assert.IsTrue(first.LeaseExpiresAt > DateTimeOffset.UtcNow);
            Assert.AreEqual(2, transport.PendingInstructionCount); // lease-1 已 Leased，不计入 Pending
            await transport.AckAsync("lease-1", first.LeaseToken);
            Assert.AreEqual(2, transport.PendingInstructionCount); // 仍为 2（lease-2/3 Pending）

            var second = await transport.LeaseAsync(leaseDuration);
            Assert.IsNotNull(second);
            Assert.AreEqual("lease-2", second!.Instruction.InstructionId);
            await transport.AckAsync("lease-2", second.LeaseToken);

            var third = await transport.LeaseAsync(leaseDuration);
            Assert.IsNotNull(third);
            Assert.AreEqual("lease-3", third!.Instruction.InstructionId);
            await transport.AckAsync("lease-3", third.LeaseToken);

            Assert.AreEqual(0, transport.PendingInstructionCount);

            // 无 Pending 行 → null
            var empty = await transport.LeaseAsync(leaseDuration);
            Assert.IsNull(empty);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task LeaseResultAndAckResult_Outbox_RoundtripsFifo()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt12_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);
            var leaseDuration = TimeSpan.FromMinutes(1);

            await transport.SendResultAsync(MakeResult("r-1", "out-1"));
            await transport.SendResultAsync(MakeResult("r-2", "out-2"));

            Assert.AreEqual(2, transport.PendingResultCount);

            var first = await transport.LeaseResultAsync(leaseDuration);
            Assert.IsNotNull(first);
            Assert.AreEqual("r-1", first!.Result.InstructionId);
            Assert.AreEqual("out-1", first.Result.Output);
            Assert.IsFalse(string.IsNullOrEmpty(first.ResultId));
            Assert.IsFalse(string.IsNullOrEmpty(first.LeaseToken));
            Assert.AreEqual(1, transport.PendingResultCount);
            await transport.AckResultAsync(first.ResultId, first.LeaseToken);
            Assert.AreEqual(1, transport.PendingResultCount);

            var second = await transport.LeaseResultAsync(leaseDuration);
            Assert.IsNotNull(second);
            Assert.AreEqual("r-2", second!.Result.InstructionId);
            await transport.AckResultAsync(second.ResultId, second.LeaseToken);

            Assert.AreEqual(0, transport.PendingResultCount);

            var empty = await transport.LeaseResultAsync(leaseDuration);
            Assert.IsNull(empty);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Lease_WithoutAck_RowStaysLeased_NotReLeasedUntilRequeued()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt13_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SubmitAsync(MakeInstruction("stuck-1"));

            // 租约但不 Ack → 行变为 Leased，PendingCount=0
            var leased = await transport.LeaseAsync(TimeSpan.FromSeconds(1));
            Assert.IsNotNull(leased);
            Assert.AreEqual("stuck-1", leased!.Instruction.InstructionId);
            Assert.AreEqual(0, transport.PendingInstructionCount);

            // 再次 Lease → 返回 null（无 Pending 行；Leased 行不会被重复租约）
            var reLeased = await transport.LeaseAsync(TimeSpan.FromSeconds(1));
            Assert.IsNull(reLeased);

            // RequeueExpired 未过期 → 0 行回滚
            var requeued = await transport.RequeueExpiredAsync();
            Assert.AreEqual(0, requeued);
            Assert.AreEqual(0, transport.PendingInstructionCount);

            // 等待租约过期 → RequeueExpired 回滚为 Pending
            await Task.Delay(1200); // 等待 1 秒租约过期
            var requeued2 = await transport.RequeueExpiredAsync();
            Assert.AreEqual(1, requeued2);
            Assert.AreEqual(1, transport.PendingInstructionCount);

            // 回滚后可重新租约
            var reLeased2 = await transport.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(reLeased2);
            Assert.AreEqual("stuck-1", reLeased2!.Instruction.InstructionId);
            // 新租约有新 token（不同于原 token）
            Assert.AreNotEqual(leased.LeaseToken, reLeased2.LeaseToken);
            await transport.AckAsync("stuck-1", reLeased2.LeaseToken);
            Assert.AreEqual(0, transport.PendingInstructionCount);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Nack_ReturnsRowToPending_CanBeReLeased()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt14_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SubmitAsync(MakeInstruction("nack-1"));

            var leased = await transport.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(leased);
            Assert.AreEqual(0, transport.PendingInstructionCount);

            // Nack 立即回滚为 Pending
            await transport.NackAsync("nack-1", leased!.LeaseToken);
            Assert.AreEqual(1, transport.PendingInstructionCount);

            // 可重新租约（FIFO 顺序保持）
            var reLeased = await transport.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(reLeased);
            Assert.AreEqual("nack-1", reLeased!.Instruction.InstructionId);
            Assert.AreNotEqual(leased.LeaseToken, reLeased.LeaseToken);
            await transport.AckAsync("nack-1", reLeased.LeaseToken);
            Assert.AreEqual(0, transport.PendingInstructionCount);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task NackResult_ReturnsRowToPending_CanBeReLeased()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt15_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SendResultAsync(MakeResult("nr-1", "out-1"));

            var leased = await transport.LeaseResultAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(leased);
            Assert.AreEqual(0, transport.PendingResultCount);

            await transport.NackResultAsync(leased!.ResultId, leased.LeaseToken);
            Assert.AreEqual(1, transport.PendingResultCount);

            var reLeased = await transport.LeaseResultAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(reLeased);
            Assert.AreEqual("nr-1", reLeased!.Result.InstructionId);
            await transport.AckResultAsync(reLeased.ResultId, reLeased.LeaseToken);
            Assert.AreEqual(0, transport.PendingResultCount);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RenewLease_ExtendsExpiry_PreventsRequeue()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt16_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SubmitAsync(MakeInstruction("renew-1"));

            // 短租约（100ms）
            var leased = await transport.LeaseAsync(TimeSpan.FromMilliseconds(100));
            Assert.IsNotNull(leased);

            // 立即续租到 1 分钟
            await transport.RenewLeaseAsync("renew-1", leased!.LeaseToken, TimeSpan.FromMinutes(1));

            // 等待原租约过期时间
            await Task.Delay(250);

            // RequeueExpired 不应回滚（已续租）
            var requeued = await transport.RequeueExpiredAsync();
            Assert.AreEqual(0, requeued);
            Assert.AreEqual(0, transport.PendingInstructionCount); // 仍为 Leased

            // Ack 仍可用（续租后 token 不变）
            await transport.AckAsync("renew-1", leased.LeaseToken);
            Assert.AreEqual(0, transport.PendingInstructionCount);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RenewResultLease_ExtendsExpiry_PreventsRequeue()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt17_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SendResultAsync(MakeResult("rr-1", "out-1"));

            var leased = await transport.LeaseResultAsync(TimeSpan.FromMilliseconds(100));
            Assert.IsNotNull(leased);

            await transport.RenewResultLeaseAsync(leased!.ResultId, leased.LeaseToken, TimeSpan.FromMinutes(1));
            await Task.Delay(250);

            var requeued = await transport.RequeueExpiredAsync();
            Assert.AreEqual(0, requeued);

            await transport.AckResultAsync(leased.ResultId, leased.LeaseToken);
            Assert.AreEqual(0, transport.PendingResultCount);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Ack_WithWrongToken_ThrowsInvalidOperationException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt18_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SubmitAsync(MakeInstruction("wrong-ack-1"));
            var leased = await transport.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(leased);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await transport.AckAsync("wrong-ack-1", "wrong-token-not-matching"));

            // 失败的 Ack 不影响行状态（仍为 Leased）
            Assert.AreEqual(0, transport.PendingInstructionCount);

            // 正确 token 仍可 Ack
            await transport.AckAsync("wrong-ack-1", leased!.LeaseToken);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Nack_WithWrongToken_ThrowsInvalidOperationException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt19_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SubmitAsync(MakeInstruction("wrong-nack-1"));
            var leased = await transport.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(leased);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await transport.NackAsync("wrong-nack-1", "wrong-token"));

            // 正确 token 仍可 Nack
            await transport.NackAsync("wrong-nack-1", leased!.LeaseToken);
            Assert.AreEqual(1, transport.PendingInstructionCount);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RenewLease_WithWrongToken_ThrowsInvalidOperationException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt20_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SubmitAsync(MakeInstruction("wrong-renew-1"));
            var leased = await transport.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(leased);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await transport.RenewLeaseAsync("wrong-renew-1", "wrong-token", TimeSpan.FromMinutes(2)));

            // 正确 token 仍可续租
            await transport.RenewLeaseAsync("wrong-renew-1", leased!.LeaseToken, TimeSpan.FromMinutes(2));
            await transport.AckAsync("wrong-renew-1", leased.LeaseToken);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Ack_AfterRequeueExpired_ThrowsInvalidOperationException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt21_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await transport.SubmitAsync(MakeInstruction("expired-ack-1"));
            var leased = await transport.LeaseAsync(TimeSpan.FromMilliseconds(100));
            Assert.IsNotNull(leased);

            await Task.Delay(250); // 等待租约过期
            var requeued = await transport.RequeueExpiredAsync();
            Assert.AreEqual(1, requeued);
            Assert.AreEqual(1, transport.PendingInstructionCount); // 已回滚为 Pending

            // 原 token 已失效（行已回滚为 Pending）→ Ack 应抛
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await transport.AckAsync("expired-ack-1", leased!.LeaseToken));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RequeueExpired_ReturnsTotalCount_InboxAndOutbox()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt22_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            // 2 条 inbox + 1 条 outbox，全部短租约（不 Ack）
            await transport.SubmitAsync(MakeInstruction("exp-1"));
            await transport.SubmitAsync(MakeInstruction("exp-2"));
            await transport.SendResultAsync(MakeResult("exp-r-1"));

            var l1 = await transport.LeaseAsync(TimeSpan.FromMilliseconds(100));
            var l2 = await transport.LeaseAsync(TimeSpan.FromMilliseconds(100));
            var lr1 = await transport.LeaseResultAsync(TimeSpan.FromMilliseconds(100));

            Assert.IsNotNull(l1);
            Assert.IsNotNull(l2);
            Assert.IsNotNull(lr1);
            Assert.AreEqual(0, transport.PendingInstructionCount);
            Assert.AreEqual(0, transport.PendingResultCount);

            await Task.Delay(250); // 等待全部过期

            var requeued = await transport.RequeueExpiredAsync();
            Assert.AreEqual(3, requeued); // 2 inbox + 1 outbox
            Assert.AreEqual(2, transport.PendingInstructionCount);
            Assert.AreEqual(1, transport.PendingResultCount);

            // 二次调用应为 0（已全部回滚）
            var requeued2 = await transport.RequeueExpiredAsync();
            Assert.AreEqual(0, requeued2);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CrashRecovery_LeasedWithoutAck_OnNewInstance_RequeuedAndReLeased()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt23_");
        try
        {
            // 第一个实例：提交 + 租约（模拟崩溃前已 Lease 但未 Ack）
            var transport1 = new PostgresDurableTransport(factory, serializer, migrationRunner);
            await transport1.SubmitAsync(MakeInstruction("crash-lease-1"));

            var leased = await transport1.LeaseAsync(TimeSpan.FromMilliseconds(100));
            Assert.IsNotNull(leased);
            Assert.AreEqual(0, transport1.PendingInstructionCount);

            // 模拟进程崩溃：丢弃 transport1（持有 lease token，但进程已死，无法 Ack）
            await Task.Delay(250); // 等待租约过期

            // 新实例启动：调用 RequeueExpired 回滚过期租约
            var transport2 = new PostgresDurableTransport(factory, serializer, migrationRunner);
            var requeued = await transport2.RequeueExpiredAsync();
            Assert.AreEqual(1, requeued);
            Assert.AreEqual(1, transport2.PendingInstructionCount);

            // 新实例可重新租约并 Ack
            var reLeased = await transport2.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(reLeased);
            Assert.AreEqual("crash-lease-1", reLeased!.Instruction.InstructionId);
            // 新租约 token 不同于崩溃实例持有的（已失效）
            Assert.AreNotEqual(leased.LeaseToken, reLeased.LeaseToken);
            await transport2.AckAsync("crash-lease-1", reLeased.LeaseToken);
            Assert.AreEqual(0, transport2.PendingInstructionCount);

            // 原 token（来自崩溃实例）无法 Ack（已被回滚并重新租约给新实例）
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await transport2.AckAsync("crash-lease-1", leased.LeaseToken));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Lease_OnEmptyInbox_ReturnsNull()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt24_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            var instruction = await transport.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNull(instruction);

            var result = await transport.LeaseResultAsync(TimeSpan.FromMinutes(1));
            Assert.IsNull(result);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Lease_ZeroOrNegativeDuration_ThrowsArgumentOutOfRangeException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt25_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
                async () => await transport.LeaseAsync(TimeSpan.Zero));

            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
                async () => await transport.LeaseAsync(TimeSpan.FromSeconds(-1)));

            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
                async () => await transport.LeaseResultAsync(TimeSpan.Zero));

            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
                async () => await transport.RenewLeaseAsync("x", "t", TimeSpan.Zero));

            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
                async () => await transport.RenewResultLeaseAsync("x", "t", TimeSpan.FromSeconds(-1)));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Ack_NullOrEmptyArgs_ThrowsArgumentException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pdt26_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);

            // 空字符串 → ArgumentException（ThrowIfNullOrWhiteSpace 对 empty/whitespace 抛 ArgumentException）
            await Assert.ThrowsExceptionAsync<ArgumentException>(
                async () => await transport.AckAsync("", "token"));
            await Assert.ThrowsExceptionAsync<ArgumentException>(
                async () => await transport.AckAsync("id", ""));
            // null → ArgumentNullException（ThrowIfNullOrWhiteSpace 对 null 抛 ArgumentNullException，
            //   ArgumentNullException : ArgumentException，但 MSTest ThrowsExceptionAsync<T> 要求精确匹配）
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(
                async () => await transport.NackAsync(null!, "token"));
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(
                async () => await transport.RenewLeaseAsync("id", null!, TimeSpan.FromMinutes(1)));
            await Assert.ThrowsExceptionAsync<ArgumentException>(
                async () => await transport.AckResultAsync("", "token"));
            await Assert.ThrowsExceptionAsync<ArgumentException>(
                async () => await transport.NackResultAsync("id", ""));
            await Assert.ThrowsExceptionAsync<ArgumentNullException>(
                async () => await transport.RenewResultLeaseAsync("id", null!, TimeSpan.FromMinutes(1)));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}
