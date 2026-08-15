using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Tests;

// ===========================================================================
// 迁移生命周期：fresh install 基线 / 增量 / 版本支持窗口 / schema verify
//
// 分层现状（既有）：
// - fresh install 基线：BaselineMigrationId（0001_operational_store_baseline）累计幂等 DDL；
// - 增量：版本化步骤链（PostgresMigrationStepRegistry，v48 起，PreCheck 三态幂等）；
// - schema verify：VerifySchemaAsync（表 / 索引 / 版本诊断）。
//
// 本文件补齐「版本支持窗口」：
// - MinSupportedSchemaVersion = 步骤链最旧步骤起始版本（当前 v48）；低于窗口的库
//   不再走增量迁移（旧步骤已随主版本删除），必须重新基线；
// - 版本比较纯逻辑（无需数据库）+ 行为验证（需 Docker/Postgres，不可用时跳过）。
// ===========================================================================

[TestClass]
[TestCategory("LR6D")]
public sealed class LR6D_MigrationLifecycleTests
{
    // ── 版本窗口：纯逻辑（无需数据库）──────────────────────────────────────

    [TestMethod]
    public void IsSchemaVersionAtLeast_NullApplied_FreshInstallAllowed()
        => Assert.IsTrue(
            PostgresMigrationRunner.IsSchemaVersionAtLeast(null, PostgresMigrationRunner.MinSupportedSchemaVersion),
            "从未迁移（null）视为全新库，允许走基线路径。");

    [TestMethod]
    public void IsSchemaVersionAtLeast_EqualOrNewer_True()
    {
        Assert.IsTrue(PostgresMigrationRunner.IsSchemaVersionAtLeast("cc-schema-v73", "cc-schema-v48"),
            "当前版本等于 / 高于最低支持版本应放行。");
        Assert.IsTrue(PostgresMigrationRunner.IsSchemaVersionAtLeast("cc-schema-v99", "cc-schema-v48"),
            "更新版本应放行。");
    }

    [TestMethod]
    public void IsSchemaVersionAtLeast_OlderThanWindow_False()
        => Assert.IsFalse(
            PostgresMigrationRunner.IsSchemaVersionAtLeast("cc-schema-v47", "cc-schema-v48"),
            "低于最低支持版本 → 超出支持窗口。");

    [TestMethod]
    public void IsSchemaVersionAtLeast_Unparseable_FailClosed()
    {
        Assert.IsFalse(PostgresMigrationRunner.IsSchemaVersionAtLeast("corrupt-version", "cc-schema-v48"),
            "无法解析的已应用版本 fail-closed（拒绝继续，交由运维人工判定）。");
        Assert.IsFalse(PostgresMigrationRunner.IsSchemaVersionAtLeast("cc-schema-v48", "corrupt-min"),
            "最低支持版本无法解析同样 fail-closed。");
    }

    [TestMethod]
    public void MinSupportedSchemaVersion_EqualsOldestStepFromVersion()
    {
        var expected = PostgresMigrationStepRegistry.Steps[0].FromSchemaVersion;
        Assert.AreEqual(expected, PostgresMigrationRunner.MinSupportedSchemaVersion,
            "支持窗口下界 = 版本化步骤链最旧步骤的起始版本。");
        Assert.IsTrue(
            PostgresMigrationRunner.IsSchemaVersionAtLeast(expected, PostgresMigrationRunner.MinSupportedSchemaVersion),
            "窗口下界自身应在窗口内。");
    }

    // ── 版本窗口：行为验证（需 Docker/Postgres，不可用时跳过）───────────────

    [TestMethod]
    public async Task VerifySchema_TooOldDatabase_ReportsSchemaTooOld()
    {
        var container = await PostgresTestHost.TryStartPostgresAsync(nameof(LR6D_MigrationLifecycleTests));
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 版本窗口行为测试已跳过。");
            return;
        }

        await using (container)
        {
            var options = new PostgresOptions
            {
                ConnectionString = container.GetConnectionString(),
                AutoMigrate = true,
                EnablePgVectorExtension = true
            };
            await using var factory = new PostgresConnectionFactory(options);
            var runner = new PostgresMigrationRunner(factory);

            // 先全新迁移到最新版本（fresh install 基线 + 增量 + 版本记录）。
            await runner.MigrateAsync();

            // 模拟旧库：写入一条 applied_at 最新的旧版本迁移记录
            //（GetAppliedVersionAsync 按 applied_at DESC 取最新一条 → 读到 v47）。
            await using var connection = await factory.OpenConnectionAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    $"INSERT INTO {PostgresNames.Table(options, "context_schema_migrations")} " +
                    "(migration_id, schema_version, applied_at, checksum, metadata) " +
                    "VALUES ('fake_old_v47', 'cc-schema-v47', now() + interval '1 hour', 'fake', jsonb_build_object()) " +
                    "ON CONFLICT (migration_id) DO NOTHING;";
                await command.ExecuteNonQueryAsync();
            }

            var report = await runner.VerifySchemaAsync();
            CollectionAssert.Contains(report.Diagnostics.ToList(), "SchemaTooOld",
                "低于最低支持版本的库应报告 SchemaTooOld。");
            Assert.AreEqual("ReinstallRequired", report.Recommendation,
                "超出支持窗口应建议重新基线（ReinstallRequired），而非普通 SchemaIncomplete。");
        }
    }

    [TestMethod]
    public async Task Migrate_TooOldDatabase_ThrowsClearError()
    {
        var container = await PostgresTestHost.TryStartPostgresAsync(nameof(LR6D_MigrationLifecycleTests));
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 版本窗口行为测试已跳过。");
            return;
        }

        await using (container)
        {
            var options = new PostgresOptions
            {
                ConnectionString = container.GetConnectionString(),
                AutoMigrate = true,
                EnablePgVectorExtension = true
            };
            await using var factory = new PostgresConnectionFactory(options);
            var runner = new PostgresMigrationRunner(factory);
            await runner.MigrateAsync();

            // 模拟旧库（同上：最新一条迁移记录为旧版本）。
            await using var connection = await factory.OpenConnectionAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    $"INSERT INTO {PostgresNames.Table(options, "context_schema_migrations")} " +
                    "(migration_id, schema_version, applied_at, checksum, metadata) " +
                    "VALUES ('fake_old_v47_migrate', 'cc-schema-v47', now() + interval '1 hour', 'fake', jsonb_build_object()) " +
                    "ON CONFLICT (migration_id) DO NOTHING;";
                await command.ExecuteNonQueryAsync();
            }

            // 低于支持窗口 → 增量迁移 fail-closed 抛出明确错误（不得静默跳过旧步骤）。
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => runner.MigrateAsync());
            StringAssert.Contains(ex.Message, "最低支持版本",
                "错误信息应指明版本低于最低支持版本。");
        }
    }
}
