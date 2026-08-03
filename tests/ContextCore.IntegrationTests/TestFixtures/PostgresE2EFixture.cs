using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests.TestFixtures;

// ===========================================================================
// Production Evidence E2E 共享 PostgreSQL fixture
//
// 目标：消除每个测试类重复的 ClassInitialize/ClassCleanup 样板代码，
// 统一 Docker 不可用时的 Assert.Inconclusive 语义。
//
// 用法（MSTest Class-level fixture）：
// [TestClass]
// public sealed class MyE2ETests : IDisposable
// {
// private readonly PostgresE2EFixture _pg = new();
//
// [TestInitialize]
// public async Task InitializeAsync() => await _pg.StartAsync();
//
// [TestCleanup]
// public Task CleanupAsync() => _pg.DisposeAsync().AsTask();
//
// [TestMethod]
// public async Task MyTest()
// {
// if (_pg.ShouldSkip) { Assert.Inconclusive(...); return; }
// var (factory, runner, serializer) = _pg.CreateInfrastructure("prefix_");
// ...
// }
// }
//
// 设计原则：
// 1. 每个 fixture 实例启动独立的 PostgreSqlContainer（测试类间隔离）。
// 2. Docker 不可用时 ShouldSkip=true，测试应 Assert.Inconclusive 跳过。
// 3. 跳过提示语统一为"此结果不证明生产证据通过"——语义上明确区分"未跑"与"通过"。
// 4. 镜像统一为 pgvector/pgvector:pg17（含 pgvector 扩展，与生产一致）。
// ===========================================================================

/// <summary>
/// Production Evidence E2E 共享 PostgreSQL fixture。
/// 封装 Testcontainers 容器生命周期与基础设施构建，消除样板代码。
/// </summary>
public sealed class PostgresE2EFixture : IAsyncDisposable
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    /// <summary>启动 PostgreSQL 容器。Docker 不可用时静默设置 ShouldSkip=true。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // 直接尝试启动容器（避免 IsDockerAvailableAsync 在 Windows named-pipe Docker Desktop 上误判）。
        try
        {
            _container = new PostgreSqlBuilder(PgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();

            await _container.StartAsync(cancellationToken).ConfigureAwait(false);
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PostgresE2EFixture] Docker 不可用：{ex.GetType().Name}: {ex.Message}");
            _connectionString = null;
        }
    }

    /// <summary>Docker 不可用时为 true，测试应 Assert.Inconclusive 跳过。</summary>
    public bool ShouldSkip => _connectionString is null;

    /// <summary>当前容器连接字符串（ShouldSkip=true 时为 null）。</summary>
    public string ConnectionString => _connectionString ?? throw new InvalidOperationException("PostgreSQL 容器未启动或 Docker 不可用。");

    /// <summary>构建测试用 Postgres 基础设施（factory + migrationRunner + serializer）。</summary>
    /// <param name="tablePrefix">表前缀（测试隔离，每个测试用例使用独立前缀）。</param>
    public (PostgresConnectionFactory factory, PostgresMigrationRunner migrationRunner, PostgresJsonSerializer serializer) CreateInfrastructure(string tablePrefix)
    {
        var options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = tablePrefix
        };
        var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        return (factory, migrationRunner, serializer);
    }

    /// <summary>打开一个 raw Npgsql 连接（用于直接 SQL 断言，绕过仓储抽象）。</summary>
    public async Task<NpgsqlConnection> OpenRawConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
            _container = null;
        }
        _connectionString = null;
    }
}
