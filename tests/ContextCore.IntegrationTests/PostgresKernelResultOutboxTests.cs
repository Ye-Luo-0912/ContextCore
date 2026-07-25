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
        if (!await PostgresIntegrationTests.IsDockerAvailableAsync())
        {
            Console.WriteLine("[PostgresKernelResultOutboxTests] Docker 不可用，所有测试将标记为 Inconclusive。");
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
}
