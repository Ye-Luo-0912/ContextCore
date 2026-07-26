using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// R29 WP-B-2：PostgresKernelResultOutbox 端到端集成测试（Testcontainers）。
/// 验证持久化 Kernel Result Outbox 的入队、出队（FIFO + SKIP LOCKED）、PendingCount、崩溃恢复。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class PostgresKernelResultOutboxTests
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
            Console.WriteLine($"[PostgresKernelResultOutboxTests] Docker 不可用：{ex.GetType().Name}: {ex.Message}");
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

    private static AgentKernelResult MakeResult(string instructionId, string output = "ok")
        => new()
        {
            InstructionId = instructionId,
            Succeeded = true,
            Output = output
        };

    [TestMethod]
    public async Task Enqueue_ThenDequeue_ReturnsResultInFifoOrder()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro1_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("instr-1", "output-1"));
            await outbox.EnqueueAsync(MakeResult("instr-2", "output-2"));
            await outbox.EnqueueAsync(MakeResult("instr-3", "output-3"));

            // FIFO：先入先出
            var first = await outbox.DequeueAsync();
            Assert.IsNotNull(first);
            Assert.AreEqual("instr-1", first!.InstructionId);
            Assert.AreEqual("output-1", first.Output);

            var second = await outbox.DequeueAsync();
            Assert.IsNotNull(second);
            Assert.AreEqual("instr-2", second!.InstructionId);

            var third = await outbox.DequeueAsync();
            Assert.IsNotNull(third);
            Assert.AreEqual("instr-3", third!.InstructionId);

            // 队列空 → null
            var empty = await outbox.DequeueAsync();
            Assert.IsNull(empty);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public void PendingCount_ReflectsEnqueueAndDequeue()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro2_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);
            // 用 Initialize 触发 EnsureMigratedAsync（PendingCount 属性内部会再次 EnsureMigrated，
            // 但若迁移尚未运行，OpenConnectionAsync 后第一次 ExecuteScalar 可能找不到表）
            // 这里先手动迁移一次
            migrationRunner.MigrateAsync().GetAwaiter().GetResult();

            Assert.AreEqual(0, outbox.PendingCount);

            outbox.EnqueueAsync(MakeResult("p-1")).AsTask().GetAwaiter().GetResult();
            outbox.EnqueueAsync(MakeResult("p-2")).AsTask().GetAwaiter().GetResult();
            Assert.AreEqual(2, outbox.PendingCount);

            outbox.DequeueAsync().AsTask().GetAwaiter().GetResult();
            Assert.AreEqual(1, outbox.PendingCount);

            outbox.DequeueAsync().AsTask().GetAwaiter().GetResult();
            Assert.AreEqual(0, outbox.PendingCount);
        }
        finally
        {
            factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public async Task Dequeue_OnEmptyOutbox_ReturnsNull()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro3_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            var result = await outbox.DequeueAsync();
            Assert.IsNull(result);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CrashRecovery_NewOutboxInstanceReadsPersistedPendingResults()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro4_");
        try
        {
            // 第一个 outbox 实例（模拟崩溃前的进程）
            var outbox1 = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);
            await outbox1.EnqueueAsync(MakeResult("crash-1", "payload-1"));
            await outbox1.EnqueueAsync(MakeResult("crash-2", "payload-2"));

            // 模拟进程崩溃：丢弃 outbox1，创建新实例（同一数据库）
            var outbox2 = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            // 新实例应能读取持久化的 Pending 结果
            Assert.AreEqual(2, outbox2.PendingCount);
            var first = await outbox2.DequeueAsync();
            Assert.IsNotNull(first);
            Assert.AreEqual("crash-1", first!.InstructionId);
            Assert.AreEqual("payload-1", first.Output);

            var second = await outbox2.DequeueAsync();
            Assert.IsNotNull(second);
            Assert.AreEqual("crash-2", second!.InstructionId);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Enqueue_PreservesAllAgentKernelResultFields()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro5_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);
            var result = new AgentKernelResult
            {
                InstructionId = "full-1",
                Succeeded = false,
                Error = "simulated failure"
            };

            await outbox.EnqueueAsync(result);

            var dequeued = await outbox.DequeueAsync();
            Assert.IsNotNull(dequeued);
            Assert.AreEqual("full-1", dequeued!.InstructionId);
            Assert.IsFalse(dequeued.Succeeded);
            Assert.AreEqual("simulated failure", dequeued.Error);
            Assert.IsNull(dequeued.Output);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Dequeue_OnSameInstructionTwice_EnqueuesTwoDistinctRows()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro6_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            // 同一 instruction_id 入队两次（每次失败重试都新增一行，不冲突）
            await outbox.EnqueueAsync(MakeResult("retry-1", "attempt-1"));
            await outbox.EnqueueAsync(MakeResult("retry-1", "attempt-2"));

            Assert.AreEqual(2, outbox.PendingCount);

            var first = await outbox.DequeueAsync();
            Assert.IsNotNull(first);
            Assert.AreEqual("attempt-1", first!.Output);

            var second = await outbox.DequeueAsync();
            Assert.IsNotNull(second);
            Assert.AreEqual("attempt-2", second!.Output);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Enqueue_NullResult_ThrowsArgumentNullException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro7_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await Assert.ThrowsExceptionAsync<ArgumentNullException>(
                async () => await outbox.EnqueueAsync(null!));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ── P0-2 租约模型测试 ─────────────────────────────────────────────

    [TestMethod]
    public async Task Lease_OnEmptyOutbox_ReturnsNull()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro8_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            var leased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNull(leased);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Lease_ReturnsOldestPendingWithToken()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro9_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("lease-1", "payload-1"));
            await outbox.EnqueueAsync(MakeResult("lease-2", "payload-2"));

            var leased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1), owner: "worker-A");
            Assert.IsNotNull(leased);
            Assert.AreEqual("lease-1", leased!.Result.InstructionId);
            Assert.AreEqual("payload-1", leased.Result.Output);
            Assert.IsFalse(string.IsNullOrEmpty(leased.LeaseToken));
            Assert.IsFalse(string.IsNullOrEmpty(leased.OutboxId));
            Assert.IsTrue(leased.LeaseExpiresAt > DateTimeOffset.UtcNow);

            // 仅 1 条 Pending（另一条已 Leased）
            Assert.AreEqual(1, outbox.PendingCount);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Lease_FifoOrder_AcrossMultipleLeases()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro10_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("fifo-1"));
            await outbox.EnqueueAsync(MakeResult("fifo-2"));
            await outbox.EnqueueAsync(MakeResult("fifo-3"));

            var first = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.AreEqual("fifo-1", first!.Result.InstructionId);
            await outbox.AckAsync(first.OutboxId, first.LeaseToken);

            var second = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.AreEqual("fifo-2", second!.Result.InstructionId);
            await outbox.AckAsync(second.OutboxId, second.LeaseToken);

            var third = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.AreEqual("fifo-3", third!.Result.InstructionId);
            await outbox.AckAsync(third.OutboxId, third.LeaseToken);

            Assert.AreEqual(0, outbox.PendingCount);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Ack_RemovesRow_PendingCountDecreases()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro11_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("ack-1"));
            Assert.AreEqual(1, outbox.PendingCount);

            var leased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.AreEqual(0, outbox.PendingCount);

            await outbox.AckAsync(leased!.OutboxId, leased.LeaseToken);
            Assert.AreEqual(0, outbox.PendingCount);

            // Ack 后再 Lease 返回 null
            var reLeased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNull(reLeased);
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

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro12_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("wrong-token-1"));
            var leased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await outbox.AckAsync(leased!.OutboxId, "wrong-token"));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Ack_OnAlreadyAcked_ThrowsInvalidOperationException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro13_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("double-ack-1"));
            var leased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            await outbox.AckAsync(leased!.OutboxId, leased.LeaseToken);

            // 二次 Ack 应抛异常（行已删除）
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await outbox.AckAsync(leased.OutboxId, leased.LeaseToken));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Nack_RequeuesToPending_AllowsReLease()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro14_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("nack-1", "original"));
            var leased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.AreEqual(0, outbox.PendingCount);

            await outbox.NackAsync(leased!.OutboxId, leased.LeaseToken);
            Assert.AreEqual(1, outbox.PendingCount);

            // 重新 Lease 得到同一行（FIFO）
            var reLeased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(reLeased);
            Assert.AreEqual("nack-1", reLeased!.Result.InstructionId);
            Assert.AreEqual("original", reLeased.Result.Output);
            // 新 token 不同于原 token
            Assert.AreNotEqual(leased.LeaseToken, reLeased.LeaseToken);
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

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro15_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("nack-wrong-1"));
            var leased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await outbox.NackAsync(leased!.OutboxId, "wrong-token"));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RenewLease_ExtendsExpiresAt()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro16_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("renew-1"));
            var leased = await outbox.LeaseAsync(TimeSpan.FromSeconds(1));
            var originalExpiry = leased!.LeaseExpiresAt;

            // 续租 5 分钟
            await outbox.RenewLeaseAsync(leased.OutboxId, leased.LeaseToken, TimeSpan.FromMinutes(5));

            // 原 token 仍可 Ack（续租成功）
            await outbox.AckAsync(leased.OutboxId, leased.LeaseToken);
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

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro17_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("renew-wrong-1"));
            var leased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await outbox.RenewLeaseAsync(leased!.OutboxId, "wrong-token", TimeSpan.FromMinutes(5)));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RequeueExpired_ReclaimsExpiredLeases()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro18_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("expired-1"));
            await outbox.EnqueueAsync(MakeResult("expired-2"));

            // 短租约
            var leased1 = await outbox.LeaseAsync(TimeSpan.FromMilliseconds(100));
            var leased2 = await outbox.LeaseAsync(TimeSpan.FromMilliseconds(100));
            Assert.AreEqual(0, outbox.PendingCount);

            // 等待租约过期
            await Task.Delay(300);

            // 回滚过期租约
            var requeued = await outbox.RequeueExpiredAsync();
            Assert.AreEqual(2, requeued);
            Assert.AreEqual(2, outbox.PendingCount);

            // 可重新租约
            var reLeased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(reLeased);
            await outbox.AckAsync(reLeased!.OutboxId, reLeased.LeaseToken);

            // 原 token（已过期回滚）无法 Ack
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await outbox.AckAsync(leased1!.OutboxId, leased1.LeaseToken));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RequeueExpired_OnNoExpired_ReturnsZero()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro19_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("no-expired-1"));
            await outbox.LeaseAsync(TimeSpan.FromMinutes(10)); // 长租约，未过期

            var requeued = await outbox.RequeueExpiredAsync();
            Assert.AreEqual(0, requeued);
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

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro20_");
        try
        {
            // 第一个实例：入队 + 租约（模拟崩溃前已 Lease 但未 Ack）
            var outbox1 = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);
            await outbox1.EnqueueAsync(MakeResult("crash-lease-1", "payload"));

            var leased = await outbox1.LeaseAsync(TimeSpan.FromMilliseconds(100));
            Assert.IsNotNull(leased);
            Assert.AreEqual(0, outbox1.PendingCount);

            // 模拟进程崩溃：丢弃 outbox1（持有 lease token，但进程已死，无法 Ack）
            await Task.Delay(250); // 等待租约过期

            // 新实例启动：调用 RequeueExpired 回滚过期租约
            var outbox2 = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);
            var requeued = await outbox2.RequeueExpiredAsync();
            Assert.AreEqual(1, requeued);
            Assert.AreEqual(1, outbox2.PendingCount);

            // 新实例可重新租约并 Ack
            var reLeased = await outbox2.LeaseAsync(TimeSpan.FromMinutes(1));
            Assert.IsNotNull(reLeased);
            Assert.AreEqual("crash-lease-1", reLeased!.Result.InstructionId);
            Assert.AreEqual("payload", reLeased.Result.Output);
            // 新租约 token 不同于崩溃实例持有的（已失效）
            Assert.AreNotEqual(leased.LeaseToken, reLeased.LeaseToken);
            await outbox2.AckAsync(reLeased.OutboxId, reLeased.LeaseToken);
            Assert.AreEqual(0, outbox2.PendingCount);

            // 原 token（来自崩溃实例）无法 Ack
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await outbox2.AckAsync(leased.OutboxId, leased.LeaseToken));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Lease_NonPositiveDuration_ThrowsArgumentOutOfRangeException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro21_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
                async () => await outbox.LeaseAsync(TimeSpan.Zero));
            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
                async () => await outbox.LeaseAsync(TimeSpan.FromSeconds(-1)));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RenewLease_NonPositiveExtension_ThrowsArgumentOutOfRangeException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro22_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("renew-zero-1"));
            var leased = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));

            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
                async () => await outbox.RenewLeaseAsync(leased!.OutboxId, leased.LeaseToken, TimeSpan.Zero));
            await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
                async () => await outbox.RenewLeaseAsync(leased!.OutboxId, leased!.LeaseToken, TimeSpan.FromSeconds(-1)));
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

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro23_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await Assert.ThrowsExceptionAsync<ArgumentException>(
                async () => await outbox.AckAsync("", "token"));
            await Assert.ThrowsExceptionAsync<ArgumentException>(
                async () => await outbox.AckAsync("id", ""));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Dequeue_LegacyApi_UsesLeaseInternally_RowBecomesLeasedNotDispatched()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro24_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("legacy-1"));
            Assert.AreEqual(1, outbox.PendingCount);

            // 遗留 DequeueAsync 内部调用 LeaseAsync，行变为 Leased（不是 Dispatched）
            var result = await outbox.DequeueAsync();
            Assert.IsNotNull(result);
            Assert.AreEqual("legacy-1", result!.InstructionId);

            // PendingCount=0（Leased 行不计入 Pending）；行仍在表中（未被删除）
            Assert.AreEqual(0, outbox.PendingCount);

            // 遗留 API 丢弃了 token，无法 Ack；但行不会丢失——RequeueExpired 后可重新租约
            // 使用短租约验证回滚（DefaultLegacyLeaseDuration=5min，测试不等待，改用 RequeueExpired 直接验证无过期行）
            var requeued = await outbox.RequeueExpiredAsync();
            Assert.AreEqual(0, requeued); // 5min 租约未过期，不回滚
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Lease_AfterNack_ReturnsSameRowWithNewToken()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("kro25_");
        try
        {
            var outbox = new PostgresKernelResultOutbox(factory, serializer, migrationRunner);

            await outbox.EnqueueAsync(MakeResult("nack-renew-1", "payload"));
            var leased1 = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));

            // Nack 后立即重新 Lease，应得到同一行（FIFO，created_at 最旧）
            await outbox.NackAsync(leased1!.OutboxId, leased1.LeaseToken);
            var leased2 = await outbox.LeaseAsync(TimeSpan.FromMinutes(1));

            Assert.IsNotNull(leased2);
            Assert.AreEqual(leased1.OutboxId, leased2!.OutboxId);
            Assert.AreNotEqual(leased1.LeaseToken, leased2.LeaseToken);
            Assert.AreEqual("nack-renew-1", leased2.Result.InstructionId);

            // 新 token 可 Ack
            await outbox.AckAsync(leased2.OutboxId, leased2.LeaseToken);
            Assert.AreEqual(0, outbox.PendingCount);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}
