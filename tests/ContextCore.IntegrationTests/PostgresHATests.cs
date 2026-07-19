using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// R14-PG-9：PostgreSQL HA（高可用）场景集成测试。
/// 覆盖 failover（容器重启）、连接池耗尽、慢查询超时、事务回滚恢复。
/// 使用 Testcontainers 启动 pgvector/pgvector:pg17 镜像；Docker 不可用时跳过（Inconclusive）。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class PostgresHATests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        // R14-PG-9：直接尝试启动容器——失败时设 _connectionString=null 让测试 Inconclusive。
        // 不复用 PostgresIntegrationTests.IsDockerAvailableAsync，因其内部 3 秒 CancellationToken
        // 在 pgvector 镜像首次拉取/启动时可能误判 Docker 不可用。
        try
        {
            _container = new PostgreSqlBuilder(PgVectorImage)
                .WithDatabase("ha_test")
                .WithUsername("ha_test")
                .WithPassword("ha_test")
                .Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PostgresHATests] Docker/容器启动失败：{ex.GetType().Name}: {ex.Message}");
            _connectionString = null;
            _container = null;
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

    /// <summary>
    /// R14-PG-9：模拟 DB failover——停止容器再启动，验证恢复后可重新建立连接。
    /// NpgsqlDataSource 内部维护连接池，重启后旧连接失效；Testcontainers 在 Stop/Start 后
    /// 可能重新分配主机端口，因此重启后需重新获取 connection string 并用新 factory 验证。
    /// 这模拟真实 HA 场景：failover 后服务发现更新端点，应用用新连接配置重连。
    /// </summary>
    [TestMethod]
    public async Task Failover_ConnectionRecoversAfterContainerRestart()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres HA 集成测试已跳过。"); return; }

        var csBuilder = new NpgsqlConnectionStringBuilder(_connectionString!)
        {
            Timeout = 5  // 连接超时 5 秒，避免恢复期间 ping 卡住
        };
        var options = new PostgresOptions
        {
            ConnectionString = csBuilder.ConnectionString,
            AutoMigrate = false,
            EnablePgVectorExtension = false
        };
        await using var factory = new PostgresConnectionFactory(options);

        // 初始 ping 应成功
        var (success1, error1) = await factory.PingAsync();
        Assert.IsTrue(success1, $"初始 ping 应成功：{error1}");

        // 停止容器（模拟 DB 故障）
        await _container!.StopAsync();

        // 故障期间 ping 应失败（旧 NpgsqlDataSource 的连接池中连接全部失效）
        var (success2, error2) = await factory.PingAsync();
        Assert.IsFalse(success2, "容器停止后 ping 应失败");

        // 重启容器（模拟 failover 切换后恢复）
        await _container.StartAsync();

        // Testcontainers 在 Stop/Start 后可能重新分配主机端口——重新获取 connection string。
        // 同时更新静态字段，让后续测试用新端点。
        _connectionString = _container.GetConnectionString();
        Console.WriteLine($"[PostgresHATests] 重启后 connection string 已刷新：{_connectionString}");

        // 用新 connection string 创建新 factory 验证恢复
        var recoveredCs = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Timeout = 5
        }.ConnectionString;
        var recoveredOptions = new PostgresOptions
        {
            ConnectionString = recoveredCs,
            AutoMigrate = false,
            EnablePgVectorExtension = false
        };
        await using var recoveredFactory = new PostgresConnectionFactory(recoveredOptions);

        // 等待容器完全恢复（最多 15 秒，每次重试间隔 1 秒）
        var recovered = false;
        for (var i = 0; i < 15; i++)
        {
            var (ok, err) = await recoveredFactory.PingAsync();
            if (ok) { recovered = true; break; }
            await Task.Delay(1000);
        }
        Assert.IsTrue(recovered, "容器重启后应在 15 秒内恢复 ping");
    }

    /// <summary>
    /// R14-PG-9：CommandTimeoutSeconds 应在慢查询时触发 NpgsqlException（超时）。
    /// 用 pg_sleep(5) 模拟慢查询，CommandTimeoutSeconds=1 让超时在 1 秒内触发。
    /// 验证：抛 NpgsqlException，且耗时在 [1s, 4s) 区间内。
    /// </summary>
    [TestMethod]
    public async Task SlowQuery_HonorsCommandTimeout()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres HA 集成测试已跳过。"); return; }

        var options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = false,
            EnablePgVectorExtension = false,
            CommandTimeoutSeconds = 1  // 1 秒超时
        };
        await using var factory = new PostgresConnectionFactory(options);
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = "SELECT pg_sleep(5);";  // 睡 5 秒

        var start = DateTimeOffset.UtcNow;
        NpgsqlException? caught = null;
        try
        {
            await command.ExecuteScalarAsync();
        }
        catch (NpgsqlException ex)
        {
            caught = ex;
        }
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.IsNotNull(caught, "慢查询应抛 NpgsqlException");
        Assert.IsTrue(elapsed < TimeSpan.FromSeconds(4), $"超时应早于 4 秒触发，实际 {elapsed}");
        Assert.IsTrue(elapsed >= TimeSpan.FromSeconds(1), $"超时应至少等待 1 秒，实际 {elapsed}");
    }

    /// <summary>
    /// R14-PG-9：MaxPoolSize=N 时，N 个并发连接占满池后，第 N+1 个连接应等待或失败。
    /// Npgsql 在池耗尽时会等待（默认 Timeout=15 秒），超时后抛 NpgsqlException。
    /// 这里设置 Timeout=3 让测试快失败。
    /// </summary>
    [TestMethod]
    public async Task PoolExhaustion_RespectsMaxPoolSize()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres HA 集成测试已跳过。"); return; }

        var poolSize = 3;
        var csBuilder = new NpgsqlConnectionStringBuilder(_connectionString!)
        {
            MaxPoolSize = poolSize,
            Timeout = 3  // 连接超时 3 秒，避免测试卡太久
        };
        var options = new PostgresOptions
        {
            ConnectionString = csBuilder.ConnectionString,
            AutoMigrate = false,
            EnablePgVectorExtension = false
        };
        await using var factory = new PostgresConnectionFactory(options);

        // 占满连接池
        var heldConnections = new List<NpgsqlConnection>();
        try
        {
            for (var i = 0; i < poolSize; i++)
            {
                heldConnections.Add(await factory.OpenConnectionAsync());
            }

            // 第 N+1 个连接：应在 Timeout 秒内抛 NpgsqlException（池耗尽）
            var start = DateTimeOffset.UtcNow;
            NpgsqlException? caught = null;
            try
            {
                _ = await factory.OpenConnectionAsync();
            }
            catch (NpgsqlException ex)
            {
                caught = ex;
            }
            var elapsed = DateTimeOffset.UtcNow - start;

            Assert.IsNotNull(caught, "池耗尽时应抛 NpgsqlException");
            Assert.IsTrue(elapsed <= TimeSpan.FromSeconds(5), $"应在超时时间内失败，实际 {elapsed}");
        }
        finally
        {
            foreach (var c in heldConnections)
            {
                await c.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// R14-PG-9：事务失败回滚后，后续操作应能正常进行（连接池未损坏）。
    /// 故意触发 PostgresException（重复主键）后回滚事务，
    /// 验证后续从连接池获取新连接执行 SELECT 1 仍能成功。
    /// </summary>
    [TestMethod]
    public async Task TransactionRollback_AllowsSubsequentOperations()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres HA 集成测试已跳过。"); return; }

        var options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = false,
            EnablePgVectorExtension = false
        };
        await using var factory = new PostgresConnectionFactory(options);

        // 第一次事务：故意触发错误并回滚
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await using var setupCmd = connection.CreateCommand();
                setupCmd.Transaction = transaction;
                setupCmd.CommandText = "CREATE TABLE IF NOT EXISTS ha_test_rollback (id int primary key, val text);";
                await setupCmd.ExecuteNonQueryAsync();

                await using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = "INSERT INTO ha_test_rollback VALUES (1, 'first');";
                await insertCmd.ExecuteNonQueryAsync();

                // 第二次插入相同主键应失败
                await using var dupCmd = connection.CreateCommand();
                dupCmd.Transaction = transaction;
                dupCmd.CommandText = "INSERT INTO ha_test_rollback VALUES (1, 'duplicate');";
                await Assert.ThrowsExceptionAsync<PostgresException>(() => dupCmd.ExecuteNonQueryAsync());
            }
            finally
            {
                await transaction.RollbackAsync();
            }
        }

        // 后续操作：应能正常执行（连接池未损坏）
        await using (var cleanupConnection = await factory.OpenConnectionAsync())
        {
            await using var verifyCmd = cleanupConnection.CreateCommand();
            verifyCmd.CommandText = "SELECT 1;";
            var result = await verifyCmd.ExecuteScalarAsync();
            Assert.AreEqual(1, result, "事务回滚后连接池应可用");
        }
    }
}
