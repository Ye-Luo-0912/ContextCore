using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.Graph;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using ContextCore.Tests;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// P0-3：PostgreSQL 跨 store 写入事务作用域集成测试。
/// 验证：
/// <list type="bullet">
///   <item>CommitAsync 成功后所有 store 写入持久化。</item>
///   <item>RollbackAsync 或异常路径下所有 store 写入回滚（无脏数据）。</item>
///   <item>事务内 Query 可读到同事务未提交数据。</item>
///   <item>BasicContextIngestionService 在事务路径下 ContextStore + RelationStore 原子写入。</item>
///   <item>工厂创建的 scope 是 PostgresWriteTransactionScope（暴露 Connection/Transaction）。</item>
/// </list>
/// 使用 Testcontainers 启动临时 Postgres 实例；无 Docker 时所有测试标记为 Inconclusive。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("P0-3")]
public sealed class PostgresWriteTransactionScopeTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;
    private PostgresConnectionFactory? _factory;
    private PostgresJsonSerializer? _serializer;
    private PostgresMigrationRunner? _migrationRunner;
    private PostgresOptions? _options;
    private string _tablePrefix = "p03_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        if (!await PostgresIntegrationTests.IsDockerAvailableAsync())
        {
            Console.WriteLine("[PostgresWriteTransactionScopeTests] Docker 不可用，所有测试将标记为 Inconclusive。");
            return;
        }

        _container = new PostgreSqlBuilder(PgVectorImage)
            .WithDatabase("p03test")
            .WithUsername("p03test")
            .WithPassword("p03test")
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

    [TestInitialize]
    public void TestInitialize()
    {
        if (_connectionString is null)
        {
            Assert.Inconclusive("Docker 不可用 — P0-3 集成测试已跳过。此结果不证明事务路径通过。");
        }

        // 每个测试使用独立前缀，避免相互干扰
        _tablePrefix = "p03_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";
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

    private async Task<(PostgresContextStore contextStore, PostgresRelationStore relationStore, PostgresWriteTransactionScopeFactory txFactory)> BuildStoresAsync()
    {
        await _migrationRunner!.MigrateAsync();
        var contextStore = new PostgresContextStore(_factory!, _serializer!, _migrationRunner);
        var relationStore = new PostgresRelationStore(_factory!, _serializer!, _migrationRunner);
        var txFactory = new PostgresWriteTransactionScopeFactory(_factory!);
        return (contextStore, relationStore, txFactory);
    }

    private static RelationProjectionWriter BuildProjectionWriter(IRelationStore relationStore)
    {
        var registry = new RelationTypeRegistry();
        var normalizer = new RelationTypeNormalizer();
        var validator = new RelationProjectorOutputValidator(registry, normalizer);
        return new RelationProjectionWriter(relationStore, validator);
    }

    private static ContextItem CreateItem(string id, params string[] refs) => new()
    {
        Id = id,
        WorkspaceId = "ws-p03",
        CollectionId = "col-p03",
        Type = "test-item",
        Title = $"Title {id}",
        Content = $"Content {id}",
        Tags = new[] { "tag-p03" },
        Refs = refs,
        Importance = 0.5
    };

    private static ContextRelation CreateRelation(string id, string sourceId, string targetId) => new()
    {
        Id = id,
        WorkspaceId = "ws-p03",
        CollectionId = "col-p03",
        SourceId = sourceId,
        TargetId = targetId,
        RelationType = ContextRelationTypes.RelatedTo,
        Weight = 1.0,
        Confidence = 0.9,
        CreatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>验证工厂创建的 scope 暴露 NpgsqlConnection + NpgsqlTransaction，且 IsActive=true。</summary>
    [TestMethod]
    public async Task BeginAsync_CreatesActivePostgresScope()
    {
        var (_, _, txFactory) = await BuildStoresAsync();

        await using var scope = (PostgresWriteTransactionScope)await txFactory.BeginAsync();
        Assert.IsTrue(scope.IsActive);
        Assert.IsNotNull(scope.Connection);
        Assert.IsNotNull(scope.Transaction);
        // 未提交前 IsActive 保持 true
        await scope.RollbackAsync();
        Assert.IsFalse(scope.IsActive);
    }

    /// <summary>CommitAsync 成功后 ContextStore + RelationStore 写入都持久化。</summary>
    [TestMethod]
    public async Task CommitAsync_BothStoresPersist()
    {
        var (contextStore, relationStore, txFactory) = await BuildStoresAsync();
        var item = CreateItem("item-commit");
        var relation = CreateRelation("rel-commit", "item-commit", "item-target");

        await using (var scope = await txFactory.BeginAsync())
        {
            await contextStore.SaveAsync(item, scope);
            await relationStore.BatchUpsertAsync(new[] { relation }, scope);
            await scope.CommitAsync();
        }

        // 事务外查询应能读到提交后的数据
        var readItem = await contextStore.GetAsync("ws-p03", "col-p03", "item-commit");
        Assert.IsNotNull(readItem);
        Assert.AreEqual("item-commit", readItem!.Id);

        var readRelations = await relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "ws-p03",
            CollectionId = "col-p03",
            SourceId = "item-commit",
            Take = 100
        });
        CollectionAssert.Contains(readRelations.Select(r => r.Id).ToList(), "rel-commit");
    }

    /// <summary>RollbackAsync 后 ContextStore + RelationStore 写入都回滚（无脏数据）。</summary>
    [TestMethod]
    public async Task RollbackAsync_NoWritesPersist()
    {
        var (contextStore, relationStore, txFactory) = await BuildStoresAsync();
        var item = CreateItem("item-rollback");
        var relation = CreateRelation("rel-rollback", "item-rollback", "item-target");

        await using (var scope = await txFactory.BeginAsync())
        {
            await contextStore.SaveAsync(item, scope);
            await relationStore.BatchUpsertAsync(new[] { relation }, scope);
            // 显式回滚
            await scope.RollbackAsync();
        }

        // 事务外查询应读不到回滚的数据
        var readItem = await contextStore.GetAsync("ws-p03", "col-p03", "item-rollback");
        Assert.IsNull(readItem);

        var readRelations = await relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "ws-p03",
            CollectionId = "col-p03",
            SourceId = "item-rollback",
            Take = 100
        });
        Assert.AreEqual(0, readRelations.Count);
    }

    /// <summary>异常路径下 DisposeAsync 触发 Rollback，无脏数据。</summary>
    [TestMethod]
    public async Task DisposeAsync_WithoutCommit_RollsBack()
    {
        var (contextStore, relationStore, txFactory) = await BuildStoresAsync();
        var item = CreateItem("item-dispose");
        var relation = CreateRelation("rel-dispose", "item-dispose", "item-target");

        try
        {
            await using var scope = await txFactory.BeginAsync();
            await contextStore.SaveAsync(item, scope);
            await relationStore.BatchUpsertAsync(new[] { relation }, scope);
            // 不调用 Commit，直接抛异常——Dispose 应触发 Rollback
            throw new InvalidOperationException("test-exception");
        }
        catch (InvalidOperationException ex) when (ex.Message == "test-exception")
        {
            // 预期异常被吞掉
        }

        // 事务外查询应读不到未提交的数据
        var readItem = await contextStore.GetAsync("ws-p03", "col-p03", "item-dispose");
        Assert.IsNull(readItem);

        var readRelations = await relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "ws-p03",
            CollectionId = "col-p03",
            SourceId = "item-dispose",
            Take = 100
        });
        Assert.AreEqual(0, readRelations.Count);
    }

    /// <summary>事务作用域内 Query 可读到同事务未提交的写入（共享事务视图）。</summary>
    [TestMethod]
    public async Task QueryAsync_WithinScope_SeesUncommittedWrites()
    {
        var (contextStore, relationStore, txFactory) = await BuildStoresAsync();
        var relation = CreateRelation("rel-query", "src-query", "tgt-query");

        await using var scope = await txFactory.BeginAsync();
        await relationStore.BatchUpsertAsync(new[] { relation }, scope);

        // 在同一事务内查询应能读到刚写入的关系
        var readRelations = await relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "ws-p03",
            CollectionId = "col-p03",
            SourceId = "src-query",
            Take = 100
        }, scope);
        CollectionAssert.Contains(readRelations.Select(r => r.Id).ToList(), "rel-query");

        // 不提交，Dispose 时回滚
        await scope.RollbackAsync();
    }

    /// <summary>事务作用域内 DeleteAsync 删除同一事务内的 BatchUpsert 写入。</summary>
    [TestMethod]
    public async Task DeleteAsync_WithinScope_DeletesUncommittedRelation()
    {
        var (_, relationStore, txFactory) = await BuildStoresAsync();
        var relation = CreateRelation("rel-delete-tx", "src-del", "tgt-del");

        await using var scope = await txFactory.BeginAsync();
        await relationStore.BatchUpsertAsync(new[] { relation }, scope);

        // 同事务内删除
        var deleted = await relationStore.DeleteAsync("ws-p03", "col-p03", "rel-delete-tx", scope);
        Assert.IsTrue(deleted);

        // 同事务内查询应读不到已删除的关系
        var readRelations = await relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "ws-p03",
            CollectionId = "col-p03",
            SourceId = "src-del",
            Take = 100
        }, scope);
        Assert.AreEqual(0, readRelations.Count);

        await scope.RollbackAsync();
    }

    /// <summary>PostgresContextStore 未传入 scope 而被请求事务写入时抛 InvalidOperationException。</summary>
    [TestMethod]
    public async Task SaveAsync_WithWrongScopeType_Throws()
    {
        var (contextStore, _, _) = await BuildStoresAsync();
        var item = CreateItem("item-wrong-scope");

        // 传入非 PostgresWriteTransactionScope 应抛异常
        var fakeScope = new FakeScope();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            contextStore.SaveAsync(item, fakeScope));
    }

    /// <summary>BasicContextIngestionService 在事务路径下成功提交：item + related_to 边同时持久化。</summary>
    [TestMethod]
    public async Task IngestAsync_TransactionPath_PersistsItemAndRelations()
    {
        var (contextStore, relationStore, txFactory) = await BuildStoresAsync();
        var projector = new RelationProjector();
        var projectionWriter = BuildProjectionWriter(relationStore);
        var ingestionService = new BasicContextIngestionService(
            contextStore, projector, relationStore, projectionWriter, txFactory);

        var item = CreateItem("item-ingest-tx", "item-target-a", "item-target-b");
        await ingestionService.IngestAsync(item);

        // 验证 item 持久化
        var readItem = await contextStore.GetAsync("ws-p03", "col-p03", "item-ingest-tx");
        Assert.IsNotNull(readItem);

        // 验证 related_to 边持久化（2 条：item-ingest-tx → item-target-a/b）
        var readRelations = await relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "ws-p03",
            CollectionId = "col-p03",
            SourceId = "item-ingest-tx",
            Take = 100
        });
        Assert.AreEqual(2, readRelations.Count);
        CollectionAssert.AreEquivalent(
            new[] { "item-target-a", "item-target-b" }.ToList(),
            readRelations.Select(r => r.TargetId).ToList());
    }

    /// <summary>BasicContextIngestionService 在事务路径下 item 更新 refs 时旧 related_to 边被删除。</summary>
    [TestMethod]
    public async Task IngestAsync_TransactionPath_ReconcilesStaleEdges()
    {
        var (contextStore, relationStore, txFactory) = await BuildStoresAsync();
        var projector = new RelationProjector();
        var projectionWriter = BuildProjectionWriter(relationStore);
        var ingestionService = new BasicContextIngestionService(
            contextStore, projector, relationStore, projectionWriter, txFactory);

        // 第一次 ingest：refs = [target-a, target-b]
        var itemV1 = CreateItem("item-recon", "target-a", "target-b");
        await ingestionService.IngestAsync(itemV1);

        // 第二次 ingest：refs = [target-b, target-c]（target-a 应被删除，target-c 应被新增）
        var itemV2 = CreateItem("item-recon", "target-b", "target-c");
        await ingestionService.IngestAsync(itemV2);

        // 验证 related_to 边：应只剩 target-b 和 target-c（target-a 被删除）
        var readRelations = await relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "ws-p03",
            CollectionId = "col-p03",
            SourceId = "item-recon",
            Take = 100
        });
        var targetIds = readRelations.Select(r => r.TargetId).OrderBy(s => s).ToList();
        CollectionAssert.AreEqual(
            new[] { "target-b", "target-c" }.OrderBy(s => s).ToList(),
            targetIds);
    }

    /// <summary>未注册 IWriteTransactionScopeFactory 时 BasicContextIngestionService 回退到无事务路径。</summary>
    [TestMethod]
    public async Task IngestAsync_NoTransactionFactory_FallsBackToNonTransactional()
    {
        var (contextStore, relationStore, _) = await BuildStoresAsync();
        var projector = new RelationProjector();
        var projectionWriter = BuildProjectionWriter(relationStore);

        // 不传 txFactory——应回退到无事务路径
        var ingestionService = new BasicContextIngestionService(
            contextStore, projector, relationStore, projectionWriter, transactionScopeFactory: null);

        var item = CreateItem("item-no-tx", "target-x");
        await ingestionService.IngestAsync(item);

        // 验证 item 和 relation 都正确持久化
        var readItem = await contextStore.GetAsync("ws-p03", "col-p03", "item-no-tx");
        Assert.IsNotNull(readItem);

        var readRelations = await relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = "ws-p03",
            CollectionId = "col-p03",
            SourceId = "item-no-tx",
            Take = 100
        });
        Assert.AreEqual(1, readRelations.Count);
        Assert.AreEqual("target-x", readRelations[0].TargetId);
    }

    /// <summary>模拟 scope——仅用于测试非 Postgres scope 类型被拒绝。</summary>
    private sealed class FakeScope : IWriteTransactionScope
    {
        public bool IsActive => true;
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
