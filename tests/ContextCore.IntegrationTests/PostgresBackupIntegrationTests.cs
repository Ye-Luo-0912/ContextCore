using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Backup;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Backup;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Shared;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// PostgreSQL 备份/恢复端到端集成测试。
/// <para>
/// 覆盖 PostgresBackupRunner.DumpAsync → BackupManifestGenerator.ForPostgresDumpAsync →
/// pg_restore → staging 数据库校验的完整 roundtrip。
/// </para>
/// <para>
/// 测试环境：使用 <see cref="PostgreSqlContainer"/>（Docker）自动拉取 pgvector 镜像。
/// 若环境无 Docker，测试将标记为 Inconclusive。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class PostgresBackupIntegrationTests
{
    // pgvector 官方镜像，含 pg 17 + vector 扩展；与 PostgresIntegrationTests 保持一致
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;
    private static string? _host;
    private static int _port;
    private static string? _username;
    private static string? _password;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        if (!await PostgresIntegrationTests.IsDockerAvailableAsync())
        {
            Console.WriteLine("[PostgresBackupIntegrationTests] Docker 不可用，所有测试将标记为 Inconclusive。");
            return;
        }

        _container = new PostgreSqlBuilder(PgVectorImage)
            .WithDatabase("cctest")
            .WithUsername("cctest")
            .WithPassword("cctest")
            .Build();

        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        // 解析连接串便于动态创建 staging 数据库
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        _host = builder.Host;
        _port = builder.Port;
        _username = builder.Username;
        _password = builder.Password;
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

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task PostgresBackup_DumpAndRestore_RoundtripsThroughStagingDb()
    {
        if (ShouldSkip)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres 备份集成测试已跳过。此结果不证明备份能力通过。");
            return;
        }

        // ── 准备：源数据库 schema 迁移 + 测试数据 ─────────────────────
        var sourceDbName = $"cc_source_{Guid.NewGuid():N}".ToLowerInvariant();
        var sourceCs = await CreateDatabaseAsync(sourceDbName);

        var sourceOptions = new PostgresOptions
        {
            ConnectionString = sourceCs,
            AutoMigrate = true,
            EnablePgVectorExtension = false,
            SchemaName = "public",
            TablePrefix = "cc_"
        };

        var sourceFactory = new PostgresConnectionFactory(sourceOptions);
        var migrationRunner = new PostgresMigrationRunner(sourceFactory);
        await migrationRunner.MigrateAsync();
        // 再次迁移以验证幂等
        await migrationRunner.MigrateAsync();

        // 插入测试数据到 cc_contexts 表（schema 迁移创建的表）
        await using (sourceFactory)
        {
            await using var conn = await sourceFactory.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO cc_contexts (id, workspace_id, collection_id, type, content, created_at, updated_at)
                VALUES
                    ('ctx-bk-1', 'ws-bk', 'col-bk', 'note', '备份测试条目 1', now(), now()),
                    ('ctx-bk-2', 'ws-bk', 'col-bk', 'note', '备份测试条目 2', now(), now()),
                    ('ctx-bk-3', 'ws-bk', 'col-bk', 'note', '备份测试条目 3', now(), now());
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // ── 步骤 1：DumpAsync 生成 .dump 文件 ────────────────────────
        string dumpPath;
        await using (var dumper = new PostgresBackupRunner(sourceOptions))
        {
            var (envOk, envErr) = await dumper.ValidateEnvironmentAsync();
            if (!envOk)
            {
                Assert.Inconclusive($"Postgres 备份环境未就绪（pg_dump 缺失？）：{envErr}");
                return;
            }

            dumpPath = Path.Combine(Path.GetTempPath(), $"cc-backup-test-{Guid.NewGuid():N}.dump");
            var dumpResult = await dumper.DumpAsync(dumpPath);

            Assert.IsTrue(File.Exists(dumpPath));
            Assert.IsTrue(dumpResult.DumpSizeBytes > 0);
            Assert.IsFalse(string.IsNullOrEmpty(dumpResult.DumpHash));
            Assert.IsTrue(dumpResult.Tables.Count > 0, "源数据库应至少有一张表");
            Console.WriteLine($"[PostgresBackupIntegrationTests] dump 完成：{dumpResult.DumpSizeBytes} bytes, {dumpResult.Tables.Count} 张表");

            // ── 步骤 2：ForPostgresDumpAsync 生成清单 ─────────────────
            var manifest = await BackupManifestGenerator.ForPostgresDumpAsync(
                dumpPath, sourceCs, dumpResult);

            Assert.AreEqual("v1", manifest.SchemaVersion);
            Assert.AreEqual(BackupStorageKind.Postgres, manifest.SourceKind);
            Assert.AreEqual(dumpResult.DumpHash, manifest.ArchiveHash);
            Assert.AreEqual(dumpResult.Tables.Count + 1, manifest.Entries.Count); // +1 for dump entry

            // SourceDescription 应已剥离密码
            Assert.IsFalse(manifest.SourceDescription.Contains(_password!, StringComparison.Ordinal));

            // ── 步骤 3：验证清单——重新哈希 dump 文件 ──────────────────
            var actualHash = Sha256Utility.HashFile(dumpPath);
            Assert.AreEqual(manifest.ArchiveHash, actualHash);
        }

        try
        {
            // ── 步骤 4：准备 staging 数据库（同实例不同数据库）──────────
            var stagingDbName = $"cc_staging_{Guid.NewGuid():N}".ToLowerInvariant();
            var stagingCs = await CreateDatabaseAsync(stagingDbName);
            var stagingOptions = new PostgresOptions
            {
                ConnectionString = stagingCs,
                AutoMigrate = false,
                EnablePgVectorExtension = false,
                SchemaName = "public",
                TablePrefix = "cc_"
            };

            // ── 步骤 5：RestoreAsync 恢复到 staging ──────────────────
            await using (var restorer = new PostgresBackupRunner(stagingOptions))
            {
                await restorer.RestoreAsync(dumpPath, cleanBeforeRestore: false);

                // ── 步骤 6：校验 staging 数据库表清单 + 行数 ──────────
                var stagingTables = await restorer.ListTablesAsync();
                Assert.IsTrue(stagingTables.Count > 0, "staging 数据库应至少有一张表");
                Assert.IsTrue(
                    stagingTables.Any(t => t.Name == "cc_contexts"),
                    "staging 数据库应包含 cc_contexts 表");

                // 校验行数
                var stagingFactory = new PostgresConnectionFactory(stagingOptions);
                await using (stagingFactory)
                {
                    await using var conn = await stagingFactory.OpenConnectionAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT count(*) FROM cc_contexts WHERE workspace_id = 'ws-bk' AND collection_id = 'col-bk';";
                    var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                    Assert.AreEqual(3, count, "staging 数据库应恢复出 3 行测试数据");
                }
            }

            Console.WriteLine("[PostgresBackupIntegrationTests] ✓ dump → manifest → restore → verify 全流程通过");

            // 清理 staging 数据库
            await DropDatabaseAsync(stagingDbName);
        }
        finally
        {
            // 清理 dump 文件
            if (File.Exists(dumpPath))
            {
                try { File.Delete(dumpPath); } catch { /* ignore */ }
            }
            // 清理源数据库
            await DropDatabaseAsync(sourceDbName);
        }
    }

    /// <summary>在容器实例中创建一个新数据库，返回该数据库的连接字符串。</summary>
    private static async Task<string> CreateDatabaseAsync(string dbName)
    {
        var adminCs = new NpgsqlConnectionStringBuilder
        {
            Host = _host,
            Port = _port,
            Username = _username,
            Password = _password,
            Database = "postgres" // 连接到默认 postgres 数据库以执行 CREATE DATABASE
        }.ToString();

        await using var conn = new NpgsqlConnection(adminCs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // 防止标识符注入：数据库名由 Guid 生成，仅含字母数字与下划线
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\";";
        await cmd.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder
        {
            Host = _host,
            Port = _port,
            Username = _username,
            Password = _password,
            Database = dbName
        }.ToString();
    }

    /// <summary>删除容器实例中的指定数据库。</summary>
    private static async Task DropDatabaseAsync(string dbName)
    {
        try
        {
            var adminCs = new NpgsqlConnectionStringBuilder
            {
                Host = _host,
                Port = _port,
                Username = _username,
                Password = _password,
                Database = "postgres"
            }.ToString();

            await using var conn = new NpgsqlConnection(adminCs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // best-effort 清理
        }
    }
}
