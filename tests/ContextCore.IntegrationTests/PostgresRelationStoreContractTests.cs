using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using ContextCore.Tests;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// Postgres provider 的 RelationStore contract 测试。
/// 继承 <see cref="RelationStoreContractBase"/>，与 InMemory / FileSystem 跑同一套断言。
/// 使用 Testcontainers（pgvector 镜像）启动临时 Postgres 实例；无 Docker 时所有测试标记为 Inconclusive。
/// 每次创建 store 使用唯一 TablePrefix，避免测试间数据干扰。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("Graph")]
public sealed class PostgresRelationStoreContractTests : RelationStoreContractBase
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        if (!await PostgresIntegrationTests.IsDockerAvailableAsync())
        {
            Console.WriteLine("[PostgresRelationStoreContractTests] Docker 不可用，所有测试将标记为 Inconclusive。");
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

    protected override async Task<IRelationStore> CreateStoreAsync(CancellationToken cancellationToken)
    {
        if (_connectionString is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres contract 测试已跳过。此结果不证明 Postgres 能力通过。");
        }

        // 每次创建 store 使用唯一前缀，确保测试间数据隔离
        var prefix = "rc" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_";

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

        // 触发迁移建表
        await migrationRunner.MigrateAsync(cancellationToken);

        return new PostgresRelationStore(factory, serializer, migrationRunner);
    }
}
