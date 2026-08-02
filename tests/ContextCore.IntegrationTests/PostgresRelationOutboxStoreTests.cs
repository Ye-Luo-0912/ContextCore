using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// PostgreSQL 关系写入 outbox 存储集成测试。
/// 验证：
/// <list type="bullet">
///   <item>EnqueueAsync 与事务原子提交/回滚（scope-aware）。</item>
///   <item>AcquirePendingAsync 使用 SELECT FOR UPDATE SKIP LOCKED——多 worker 并发无重复消费。</item>
///   <item>Stale lease（Dispatched + lease 过期）可被 AcquirePendingAsync 抢占恢复。</item>
///   <item>MarkAppliedAsync / MarkFailedAsync CAS 语义——仅 Dispatched 状态可转换。</item>
///   <item>MarkFailedAsync retry_count +1 → 未超限时回退 Pending；超限时转 Failed。</item>
///   <item>RenewHeartbeatAsync 仅对 lease_owner 匹配的 Dispatched 记录续约成功。</item>
/// </list>
/// 使用 Testcontainers 启动临时 Postgres 实例；无 Docker 时所有测试标记为 Inconclusive。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("P1-5")]
public sealed class PostgresRelationOutboxStoreTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        // 收口：直接尝试启动容器——失败时设 _connectionString=null 让测试 Inconclusive。
        // 不复用 PostgresIntegrationTests.IsDockerAvailableAsync，因其内部 3 秒 CancellationToken
        // 在 pgvector 镜像首次拉取/启动时可能误判 Docker 不可用（与 PostgresHATests 一致）。
        try
        {
            _container = new PostgreSqlBuilder(PgVectorImage)
                .WithDatabase("p15obtest")
                .WithUsername("p15obtest")
                .WithPassword("p15obtest")
                .Build();

            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PostgresRelationOutboxStoreTests] Docker 不可用：{ex.GetType().Name}: {ex.Message}");
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

    private PostgresConnectionFactory? _factory;
    private PostgresJsonSerializer? _serializer;
    private PostgresMigrationRunner? _migrationRunner;
    private PostgresOptions? _options;
    private string _tablePrefix = "p15_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";

    [TestInitialize]
    public void TestInitialize()
    {
        if (_connectionString is null)
        {
            Assert.Inconclusive("Docker 不可用 — P1-5 outbox 集成测试已跳过。");
        }

        _tablePrefix = "p15_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
        _options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = _tablePrefix
        };
        _factory = new PostgresConnectionFactory(_options);
        _serializer = new PostgresJsonSerializer();
        _migrationRunner = new PostgresMigrationRunner(_factory);
    }

    private async Task<(PostgresRelationOutboxStore outbox, PostgresWriteTransactionScopeFactory txFactory)> BuildStoreAsync()
    {
        await _migrationRunner!.MigrateAsync();
        var outbox = new PostgresRelationOutboxStore(_factory!, _serializer!, _migrationRunner);
        var txFactory = new PostgresWriteTransactionScopeFactory(_factory!);
        return (outbox, txFactory);
    }

    private static RelationOutboxRecord CreateRecord(string relationId = "rel-1", string? outboxId = null, int maxRetryCount = 3) => new()
    {
        OutboxId = outboxId ?? "ob-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = "ws-p15",
        CollectionId = "col-p15",
        RelationId = relationId,
        OperationKind = RelationOutboxOperationKind.Upsert,
        Provenance = "ingest",
        Payload = new ContextRelation
        {
            Id = relationId,
            WorkspaceId = "ws-p15",
            CollectionId = "col-p15",
            SourceId = "src-1",
            TargetId = "tgt-1",
            RelationType = ContextRelationTypes.RelatedTo,
            Weight = 0.8,
            Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow
        },
        State = RelationOutboxStates.Pending,
        MaxRetryCount = maxRetryCount,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>EnqueueAsync 与 scope 共享事务——rollback 时 outbox 行也回滚。</summary>
    [TestMethod]
    public async Task EnqueueAsync_WithScope_Rollback_DoesNotPersist()
    {
        var (outbox, txFactory) = await BuildStoreAsync();
        var record = CreateRecord("rel-rollback");

        await using (var scope = (PostgresWriteTransactionScope)await txFactory.BeginAsync())
        {
            await outbox.EnqueueAsync(record, scope);
            // 不 commit，dispose 时 rollback
            await scope.RollbackAsync();
        }

        var counts = await outbox.CountByStateAsync();
        Assert.IsFalse(counts.TryGetValue(RelationOutboxStates.Pending, out var pendingCount) && pendingCount > 0,
            "Rollback 后不应有 Pending 记录");
    }

    /// <summary>EnqueueAsync 与 scope 共享事务——commit 时 outbox 行持久化。</summary>
    [TestMethod]
    public async Task EnqueueAsync_WithScope_Commit_Persists()
    {
        var (outbox, txFactory) = await BuildStoreAsync();
        var record = CreateRecord("rel-commit");

        await using (var scope = (PostgresWriteTransactionScope)await txFactory.BeginAsync())
        {
            await outbox.EnqueueAsync(record, scope);
            await scope.CommitAsync();
        }

        var counts = await outbox.CountByStateAsync();
        Assert.IsTrue(counts.TryGetValue(RelationOutboxStates.Pending, out var pendingCount) && pendingCount == 1,
            "Commit 后应有 1 条 Pending 记录");
    }

    /// <summary>EnqueueAsync 无 scope 时使用独立事务——立即持久化。</summary>
    [TestMethod]
    public async Task EnqueueAsync_WithoutScope_PersistsIndependently()
    {
        var (outbox, _) = await BuildStoreAsync();
        var record = CreateRecord("rel-no-scope");

        await outbox.EnqueueAsync(record, scope: null);

        var counts = await outbox.CountByStateAsync();
        Assert.IsTrue(counts.TryGetValue(RelationOutboxStates.Pending, out var pendingCount) && pendingCount == 1);
    }

    /// <summary>EnqueueBatchAsync 批量入队——单次往返持久化所有记录。</summary>
    [TestMethod]
    public async Task EnqueueBatchAsync_PersistsAllRecords()
    {
        var (outbox, _) = await BuildStoreAsync();
        var records = new[]
        {
            CreateRecord("rel-batch-1"),
            CreateRecord("rel-batch-2"),
            CreateRecord("rel-batch-3")
        };

        await outbox.EnqueueBatchAsync(records, scope: null);

        var counts = await outbox.CountByStateAsync();
        Assert.AreEqual(3, counts.TryGetValue(RelationOutboxStates.Pending, out var c) ? c : 0);
    }

    /// <summary>AcquirePendingAsync 取出 Pending 记录并原子转换为 Dispatched。</summary>
    [TestMethod]
    public async Task AcquirePendingAsync_ReturnsPendingRecords_AsDispatched()
    {
        var (outbox, _) = await BuildStoreAsync();
        var record = CreateRecord("rel-acquire");
        await outbox.EnqueueAsync(record, scope: null);

        var acquired = await outbox.AcquirePendingAsync(limit: 10, owner: "worker-1", leaseDuration: TimeSpan.FromMinutes(1));

        Assert.AreEqual(1, acquired.Count);
        Assert.AreEqual(record.OutboxId, acquired[0].OutboxId);
        Assert.AreEqual(RelationOutboxStates.Dispatched, acquired[0].State);
        Assert.AreEqual("worker-1", acquired[0].LeaseOwner);

        // 再次 acquire 应返回空（已 Dispatched 且 lease 未过期）
        var acquired2 = await outbox.AcquirePendingAsync(limit: 10, owner: "worker-2", leaseDuration: TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, acquired2.Count);
    }

    /// <summary>AcquirePendingAsync 抢占过期租约——Dispatched + lease_expires_at &lt;= now。</summary>
    [TestMethod]
    public async Task AcquirePendingAsync_PreemptsStaleLease()
    {
        var (outbox, _) = await BuildStoreAsync();
        var record = CreateRecord("rel-stale");
        await outbox.EnqueueAsync(record, scope: null);

        // 第一次 acquire：用较短但不过短的租约（避免立即过期影响断言）
        var acquired1 = await outbox.AcquirePendingAsync(limit: 10, owner: "worker-1", leaseDuration: TimeSpan.FromMilliseconds(50));
        Assert.AreEqual(1, acquired1.Count);
        Assert.AreEqual("worker-1", acquired1[0].LeaseOwner);

        // 等待 lease 过期
        await Task.Delay(150);

        // 第二次 acquire：worker-2 应能抢占
        var acquired2 = await outbox.AcquirePendingAsync(limit: 10, owner: "worker-2", leaseDuration: TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, acquired2.Count);
        Assert.AreEqual(record.OutboxId, acquired2[0].OutboxId);
        Assert.AreEqual("worker-2", acquired2[0].LeaseOwner);
    }

    /// <summary>MarkAppliedAsync 仅对 Dispatched 状态生效——Applied 后再次调用返回 false。</summary>
    [TestMethod]
    public async Task MarkAppliedAsync_CAS_OnlyTransitionsFromDispatched()
    {
        var (outbox, _) = await BuildStoreAsync();
        var record = CreateRecord("rel-applied");
        await outbox.EnqueueAsync(record, scope: null);

        var acquired = await outbox.AcquirePendingAsync(limit: 1, owner: "w1", leaseDuration: TimeSpan.FromMinutes(5));
        Assert.AreEqual(1, acquired.Count);

        // 第一次 MarkApplied 成功（Dispatched → Applied）
        var applied1 = await outbox.MarkAppliedAsync(acquired[0].OutboxId);
        Assert.IsTrue(applied1);

        // 第二次 MarkApplied 应返回 false（已 Applied，CAS WHERE state='Dispatched' 匹配 0 行）
        var applied2 = await outbox.MarkAppliedAsync(acquired[0].OutboxId);
        Assert.IsFalse(applied2);
    }

    /// <summary>MarkFailedAsync 未超 max_retry_count 时回退为 Pending，超限后转 Failed。</summary>
    [TestMethod]
    public async Task MarkFailedAsync_RetriesUntilMaxThenFails()
    {
        var (outbox, _) = await BuildStoreAsync();
        // MaxRetryCount = 2：可以重试 2 次，第 3 次失败时转为 Failed
        var record = CreateRecord("rel-fail", maxRetryCount: 2);
        await outbox.EnqueueAsync(record, scope: null);

        // 第 1 次失败：retry_count 0 → 1，未超限 → Pending
        var acquired1 = await outbox.AcquirePendingAsync(limit: 1, owner: "w1", leaseDuration: TimeSpan.FromMinutes(5));
        Assert.AreEqual(1, acquired1.Count);
        var failed1 = await outbox.MarkFailedAsync(acquired1[0].OutboxId, "error-1");
        Assert.IsTrue(failed1);

        // 应该回到 Pending——可被再次 acquire
        var acquired2 = await outbox.AcquirePendingAsync(limit: 1, owner: "w1", leaseDuration: TimeSpan.FromMinutes(5));
        Assert.AreEqual(1, acquired2.Count);

        // 第 2 次失败：retry_count 1 → 2，等于 max → 转为 Failed
        var failed2 = await outbox.MarkFailedAsync(acquired2[0].OutboxId, "error-2");
        Assert.IsTrue(failed2);

        // Failed 后不再被 acquire
        var acquired3 = await outbox.AcquirePendingAsync(limit: 1, owner: "w1", leaseDuration: TimeSpan.FromMinutes(5));
        Assert.AreEqual(0, acquired3.Count);

        // 验证最终状态为 Failed
        var counts = await outbox.CountByStateAsync();
        Assert.IsTrue(counts.TryGetValue(RelationOutboxStates.Failed, out var failedCount) && failedCount == 1);
    }

    /// <summary>RenewHeartbeatAsync 仅对 lease_owner 匹配的 Dispatched 记录续约成功。</summary>
    [TestMethod]
    public async Task RenewHeartbeatAsync_OnlySucceedsForMatchingOwner()
    {
        var (outbox, _) = await BuildStoreAsync();
        var record = CreateRecord("rel-renew");
        await outbox.EnqueueAsync(record, scope: null);

        var acquired = await outbox.AcquirePendingAsync(limit: 1, owner: "owner-A", leaseDuration: TimeSpan.FromMinutes(5));
        Assert.AreEqual(1, acquired.Count);

        // 正确 owner 续约成功
        var renewed1 = await outbox.RenewHeartbeatAsync(acquired[0].OutboxId, "owner-A", TimeSpan.FromMinutes(10));
        Assert.IsTrue(renewed1);

        // 错误 owner 续约失败
        var renewed2 = await outbox.RenewHeartbeatAsync(acquired[0].OutboxId, "owner-B", TimeSpan.FromMinutes(10));
        Assert.IsFalse(renewed2);
    }

    /// <summary>CountStaleLeasesAsync 统计 Dispatched + lease 过期的记录数。</summary>
    [TestMethod]
    public async Task CountStaleLeasesAsync_CountsExpiredDispatchedRecords()
    {
        var (outbox, _) = await BuildStoreAsync();
        var record = CreateRecord("rel-stale-count");
        await outbox.EnqueueAsync(record, scope: null);

        // acquire 并使用较短但不过短的租约（避免立即过期导致 initialCount 已为 1）
        await outbox.AcquirePendingAsync(limit: 1, owner: "w1", leaseDuration: TimeSpan.FromMilliseconds(50));

        // 立即检查——lease_expires_at 仍在未来，应返回 0
        var initialCount = await outbox.CountStaleLeasesAsync();
        Assert.AreEqual(0, initialCount, "lease 未过期时 stale lease 计数应为 0");

        // 等待 lease 过期
        await Task.Delay(150);

        var staleCount = await outbox.CountStaleLeasesAsync();
        Assert.IsTrue(staleCount > initialCount, "等待后过期租约数应增加");
        Assert.AreEqual(1, staleCount);
    }

    /// <summary>
    /// 并发 AcquirePendingAsync——两个 worker 同时 acquire 不会得到同一条记录
    /// （SELECT FOR UPDATE SKIP LOCKED 保证）。
    /// </summary>
    [TestMethod]
    public async Task AcquirePendingAsync_Concurrent_NoDuplicateAcquisition()
    {
        var (outbox, _) = await BuildStoreAsync();
        // 入队 5 条记录
        for (var i = 0; i < 5; i++)
        {
            await outbox.EnqueueAsync(CreateRecord($"rel-concurrent-{i}"), scope: null);
        }

        // 两个 worker 并发 acquire（每个 limit=5）
        var task1 = outbox.AcquirePendingAsync(limit: 5, owner: "worker-A", leaseDuration: TimeSpan.FromMinutes(5));
        var task2 = outbox.AcquirePendingAsync(limit: 5, owner: "worker-B", leaseDuration: TimeSpan.FromMinutes(5));
        await Task.WhenAll(task1, task2);

        var acquired1 = task1.Result;
        var acquired2 = task2.Result;

        // 总数应为 5（无重复）
        Assert.AreEqual(5, acquired1.Count + acquired2.Count,
            "两个 worker 总 acquire 数应等于入队总数 5");

        // 验证无 outbox_id 重复
        var allOutboxIds = acquired1.Select(r => r.OutboxId)
            .Concat(acquired2.Select(r => r.OutboxId))
            .ToHashSet();
        Assert.AreEqual(5, allOutboxIds.Count, "不应有重复的 outbox_id");
    }

    /// <summary>EnqueueAsync 幂等——同 outbox_id 重复入队不会创建多行。</summary>
    [TestMethod]
    public async Task EnqueueAsync_Idempotent_SameOutboxId()
    {
        var (outbox, _) = await BuildStoreAsync();
        var record = CreateRecord("rel-idempotent", outboxId: "ob-fixed-id");

        await outbox.EnqueueAsync(record, scope: null);
        await outbox.EnqueueAsync(record, scope: null);  // 相同 outbox_id

        var counts = await outbox.CountByStateAsync();
        Assert.AreEqual(1, counts.TryGetValue(RelationOutboxStates.Pending, out var c) ? c : 0);
    }
}
