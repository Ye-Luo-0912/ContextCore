using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// R29 WP-B-1：PostgresToolDispatchJournal 端到端集成测试（Testcontainers）。
/// 验证持久化 Tool Dispatch Journal 的状态机推进、幂等、崩溃恢复与逆退保护。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class PostgresToolDispatchJournalTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        if (!await PostgresIntegrationTests.IsDockerAvailableAsync())
        {
            Console.WriteLine("[PostgresToolDispatchJournalTests] Docker 不可用，所有测试将标记为 Inconclusive。");
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

    // ── 测试方法 ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Prepare_ThenGetEntry_ReturnsPreparedState()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj1_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var entry = new ToolDispatchJournalEntry
            {
                RequestId = "req-prepare-1",
                ToolName = "search_tool",
                State = ToolDispatchState.Prepared,
                IdempotencyKey = "idem-1",
                UpdatedAt = DateTimeOffset.UtcNow,
                DiagnosticNote = "test-prepare"
            };

            await journal.PrepareAsync(entry);

            var fetched = await journal.GetEntryAsync("req-prepare-1");
            Assert.IsNotNull(fetched, "Prepare 后 GetEntry 应返回条目。");
            Assert.AreEqual("req-prepare-1", fetched!.RequestId);
            Assert.AreEqual("search_tool", fetched.ToolName);
            Assert.AreEqual(ToolDispatchState.Prepared, fetched.State);
            Assert.AreEqual("idem-1", fetched.IdempotencyKey);
            Assert.AreEqual("test-prepare", fetched.DiagnosticNote);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ForwardTransitions_PreparedToDispatchedToCommittedToResultDelivered()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj2_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var requestId = "req-forward-1";

            await journal.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = requestId,
                ToolName = "forward_tool",
                State = ToolDispatchState.Prepared,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            // Prepared → Dispatched（带 externalOperationId）
            await journal.MarkDispatchedAsync(requestId, externalOperationId: "ext-op-1");
            var afterDispatch = await journal.GetEntryAsync(requestId);
            Assert.AreEqual(ToolDispatchState.Dispatched, afterDispatch!.State);
            Assert.AreEqual("ext-op-1", afterDispatch.ExternalOperationId);

            // Dispatched → Committed
            await journal.MarkCommittedAsync(requestId);
            var afterCommit = await journal.GetEntryAsync(requestId);
            Assert.AreEqual(ToolDispatchState.Committed, afterCommit!.State);

            // Committed → ResultDelivered
            await journal.MarkResultDeliveredAsync(requestId);
            var afterDelivered = await journal.GetEntryAsync(requestId);
            Assert.AreEqual(ToolDispatchState.ResultDelivered, afterDelivered!.State);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task BackwardTransition_ThrowsInvalidOperationException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj3_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var requestId = "req-backward-1";

            await journal.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = requestId,
                ToolName = "backward_tool",
                State = ToolDispatchState.Prepared,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await journal.MarkDispatchedAsync(requestId);

            // 逆退：Dispatched → Prepared 应抛异常
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await journal.PrepareAsync(new ToolDispatchJournalEntry
                {
                    RequestId = requestId,
                    ToolName = "backward_tool",
                    State = ToolDispatchState.Prepared,
                    UpdatedAt = DateTimeOffset.UtcNow
                }));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task MarkCommitted_OnCommittedState_ThrowsInvalidOperationException()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj4_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var requestId = "req-backward-2";

            await journal.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = requestId,
                ToolName = "tool",
                State = ToolDispatchState.Prepared,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await journal.MarkDispatchedAsync(requestId);
            await journal.MarkCommittedAsync(requestId);

            // 逆退：Committed → Dispatched 应抛异常（MarkDispatched 目标 = Dispatched < Committed）
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await journal.MarkDispatchedAsync(requestId));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Prepare_IsIdempotent_DuplicatePrepareDoesNotOverwriteAdvancedState()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj5_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var requestId = "req-idem-1";

            await journal.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = requestId,
                ToolName = "original_tool",
                State = ToolDispatchState.Prepared,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await journal.MarkDispatchedAsync(requestId, "ext-1");

            // 重复 Prepare（ON CONFLICT DO NOTHING，不应覆盖已推进的 Dispatched 状态）
            await journal.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = requestId,
                ToolName = "duplicate_tool",
                State = ToolDispatchState.Prepared,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            var fetched = await journal.GetEntryAsync(requestId);
            Assert.AreEqual(ToolDispatchState.Dispatched, fetched!.State, "重复 Prepare 不应覆盖已推进的状态。");
            Assert.AreEqual("original_tool", fetched.ToolName, "重复 Prepare 不应覆盖 ToolName。");
            Assert.AreEqual("ext-1", fetched.ExternalOperationId);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CrashRecovery_NewJournalInstanceReadsPersistedState()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj6_");
        try
        {
            // 第一个 journal 实例（模拟崩溃前的进程）
            var journal1 = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var requestId = "req-crash-1";

            await journal1.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = requestId,
                ToolName = "crash_tool",
                State = ToolDispatchState.Prepared,
                IdempotencyKey = "idem-crash-1",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await journal1.MarkDispatchedAsync(requestId, "ext-crash-1");

            // 模拟进程崩溃：丢弃 journal1，创建新实例（同一数据库）
            var journal2 = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);

            // 新实例应能读取持久化的 Dispatched 状态
            var fetched = await journal2.GetEntryAsync(requestId);
            Assert.IsNotNull(fetched, "崩溃恢复后应能读取持久化条目。");
            Assert.AreEqual(ToolDispatchState.Dispatched, fetched!.State);
            Assert.AreEqual("crash_tool", fetched.ToolName);
            Assert.AreEqual("ext-crash-1", fetched.ExternalOperationId);
            Assert.AreEqual("idem-crash-1", fetched.IdempotencyKey);

            // 新实例可继续推进到 Committed（恢复后继续执行）
            await journal2.MarkCommittedAsync(requestId);
            var afterRecover = await journal2.GetEntryAsync(requestId);
            Assert.AreEqual(ToolDispatchState.Committed, afterRecover!.State);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task MarkDispatched_OnUnknownRequest_AutoCreatesStub()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj7_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var requestId = "req-auto-1";

            // 未 Prepare 直接 MarkDispatched → 自动创建 stub（与 InMemory 语义一致）
            await journal.MarkDispatchedAsync(requestId, "ext-auto-1");

            var fetched = await journal.GetEntryAsync(requestId);
            Assert.IsNotNull(fetched, "auto-create 应插入条目。");
            Assert.AreEqual(ToolDispatchState.Dispatched, fetched!.State);
            Assert.AreEqual(string.Empty, fetched.ToolName, "stub 的 ToolName 应为空字符串。");
            Assert.AreEqual("ext-auto-1", fetched.ExternalOperationId);
            Assert.AreEqual("Dispatched without prior Prepare (auto-created)", fetched.DiagnosticNote);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task GetEntry_OnUnknownRequest_ReturnsNull()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj8_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);

            var fetched = await journal.GetEntryAsync("nonexistent-request");
            Assert.IsNull(fetched, "不存在的 RequestId 应返回 null。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task MarkDispatched_ExternalOperationId_MergesWhenNullProvided()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj9_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var requestId = "req-merge-1";

            await journal.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = requestId,
                ToolName = "merge_tool",
                State = ToolDispatchState.Prepared,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            // 首次 MarkDispatched 带 externalOperationId
            await journal.MarkDispatchedAsync(requestId, "ext-original");

            // 此时已 Dispatched；再次调用 MarkDispatched（null externalOperationId）应抛逆退异常
            // （因为 Dispatched → Dispatched 不是前向推进）
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await journal.MarkDispatchedAsync(requestId, externalOperationId: null));

            // 验证 externalOperationId 未被覆盖
            var fetched = await journal.GetEntryAsync(requestId);
            Assert.AreEqual("ext-original", fetched!.ExternalOperationId, "externalOperationId 不应被逆退调用覆盖。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}
