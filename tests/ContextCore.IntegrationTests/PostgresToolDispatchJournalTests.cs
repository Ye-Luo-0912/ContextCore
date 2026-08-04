using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// PostgresToolDispatchJournal 端到端集成测试（Testcontainers）。
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
        // 收口：直接尝试启动容器（与 PostgresWriteTransactionScopeTests 一致），
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
            Console.WriteLine($"[PostgresToolDispatchJournalTests] Docker 不可用：{ex.GetType().Name}: {ex.Message}");
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
    public async Task BackwardTransition_IsIdempotent_StateNotRegressed()
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
            await journal.MarkCommittedAsync(requestId);
            await journal.MarkResultDeliveredAsync(requestId);

            // 幂等契约：状态已越过目标（ResultDelivered > Committed/Dispatched）时
            // 逆退调用视为 AlreadyAdvanced，幂等成功、不报错，且状态不倒退。
            await journal.MarkCommittedAsync(requestId);
            await journal.MarkDispatchedAsync(requestId);

            var fetched = await journal.GetEntryAsync(requestId);
            Assert.AreEqual(ToolDispatchState.ResultDelivered, fetched!.State,
                "逆退调用不应使状态倒退（幂等契约：AlreadyAdvanced）。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task MarkDispatched_OnCommittedState_IsIdempotent_StateNotRegressed()
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

            // 幂等契约：Committed 已越过 Dispatched，重复 MarkDispatched 幂等成功、状态不倒退。
            await journal.MarkDispatchedAsync(requestId);

            var fetched = await journal.GetEntryAsync(requestId);
            Assert.AreEqual(ToolDispatchState.Committed, fetched!.State,
                "Committed 状态下重复 MarkDispatched 不应使状态倒退（幂等契约：AlreadyAdvanced）。");
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

            // 重复 Prepare（同一操作的幂等重放，语义字段一致）：不应覆盖已推进的 Dispatched 状态。
            // 注意：语义字段（ToolName/IdempotencyKey/WorkspaceId/RunId）必须与首次一致，
            // 否则视为 RequestId 复用为另一项操作，抛 RequestIdReuseDetected（审计链保护）。
            await journal.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = requestId,
                ToolName = "original_tool",
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
    public async Task MarkDispatched_OnUnknownRequest_ThrowsConflict_MissingPreparedPredecessor()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj7_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var requestId = "req-missing-1";

            // 未 Prepare 直接 MarkDispatched → 抛冲突异常（不再 auto-create stub）
            // 保证审计链完整：不存在 → Dispatched 这样的跳跃不再可能。
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await journal.MarkDispatchedAsync(requestId, "ext-auto-1"));
            StringAssert.Contains(ex.Message, "缺失前驱记录");
            StringAssert.Contains(ex.Message, requestId);

            // 验证确实没有插入任何条目
            var fetched = await journal.GetEntryAsync(requestId);
            Assert.IsNull(fetched, "冲突时不应插入任何条目。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task MarkCommitted_OnUnknownRequest_ThrowsConflict_MissingDispatchedPredecessor()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj7b_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var requestId = "req-missing-committed-1";

            // 未 Prepare/Dispatch 直接 MarkCommitted → 抛冲突异常
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await journal.MarkCommittedAsync(requestId));
            StringAssert.Contains(ex.Message, "缺失前驱记录");
            StringAssert.Contains(ex.Message, "Committed");

            var fetched = await journal.GetEntryAsync(requestId);
            Assert.IsNull(fetched, "冲突时不应插入任何条目。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task MarkResultDelivered_OnUnknownRequest_ThrowsConflict_MissingCommittedPredecessor()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj7c_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);
            var requestId = "req-missing-delivered-1";

            // 未 Prepare/Dispatch/Commit 直接 MarkResultDelivered → 抛冲突异常
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await journal.MarkResultDeliveredAsync(requestId));
            StringAssert.Contains(ex.Message, "缺失前驱记录");
            StringAssert.Contains(ex.Message, "ResultDelivered");

            var fetched = await journal.GetEntryAsync(requestId);
            Assert.IsNull(fetched, "冲突时不应插入任何条目。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Prepare_WithDuplicateIdempotencyKey_ThrowsUniqueViolation()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj7d_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);

            // 第一条 Prepare 带幂等键 "shared-idem-key"
            await journal.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = "req-idem-a",
                ToolName = "tool_a",
                State = ToolDispatchState.Prepared,
                IdempotencyKey = "shared-idem-key",
                UpdatedAt = DateTimeOffset.UtcNow
            });

            // 第二条 Prepare 使用不同 request_id 但相同幂等键 → 应被 UNIQUE 约束拒绝
            // 防止不同 request_id 复用同一幂等键分别执行外部副作用。
            await Assert.ThrowsExceptionAsync<PostgresException>(
                async () => await journal.PrepareAsync(new ToolDispatchJournalEntry
                {
                    RequestId = "req-idem-b",
                    ToolName = "tool_b",
                    State = ToolDispatchState.Prepared,
                    IdempotencyKey = "shared-idem-key",
                    UpdatedAt = DateTimeOffset.UtcNow
                }));

            // 验证第一条仍然存在，第二条未写入
            var a = await journal.GetEntryAsync("req-idem-a");
            Assert.IsNotNull(a, "第一条 Prepare 应保留。");
            var b = await journal.GetEntryAsync("req-idem-b");
            Assert.IsNull(b, "第二条 Prepare（重复幂等键）不应写入。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Prepare_NullIdempotencyKey_AllowsMultipleEntries()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("tdj7e_");
        try
        {
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);

            // NULL 幂等键不参与 UNIQUE 约束（partial index WHERE idempotency_key IS NOT NULL）
            // 多条 NULL 幂等键的 Prepare 应该都能成功（与"未声明幂等键"语义一致）。
            await journal.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = "req-null-idem-1",
                ToolName = "tool_1",
                State = ToolDispatchState.Prepared,
                IdempotencyKey = null,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await journal.PrepareAsync(new ToolDispatchJournalEntry
            {
                RequestId = "req-null-idem-2",
                ToolName = "tool_2",
                State = ToolDispatchState.Prepared,
                IdempotencyKey = null,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            var e1 = await journal.GetEntryAsync("req-null-idem-1");
            var e2 = await journal.GetEntryAsync("req-null-idem-2");
            Assert.IsNotNull(e1, "NULL 幂等键的第一条应写入。");
            Assert.IsNotNull(e2, "NULL 幂等键的第二条应写入。");
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

            // 幂等重放：再次 MarkDispatched（null externalOperationId）幂等成功（AlreadyApplied），
            // 不应把已有 externalOperationId 覆盖为 null（CAS 未命中时 COALESCE 语义）。
            await journal.MarkDispatchedAsync(requestId, externalOperationId: null);

            // 验证 externalOperationId 未被覆盖
            var fetched = await journal.GetEntryAsync(requestId);
            Assert.AreEqual("ext-original", fetched!.ExternalOperationId, "externalOperationId 不应被幂等重放覆盖。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}
