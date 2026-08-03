using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.Tests;

/// <summary>
/// HA 迁移协调器（Migration Coordinator）验收测试。
///
/// 覆盖：
///   1. MigrationCoordinatorOptions 默认值
///   2. 迁移互斥锁键派生（确定性 / 正 long / 不同前缀不同键）
///   3. 协调器阶段状态机（Idle → AcquiringLock → Migrating → UpToDate / Failed）
///   4. 已最新版本短路（不执行迁移）
///   5. 并发调用单执行者（SemaphoreSlim 门闩串行化）
///   6. 失败路径（Failed 状态 + 重新抛出）
///
/// 不连接真实 PostgreSQL 数据库：通过 internal 构造函数注入假的迁移执行器与
/// 版本读取器（InternalsVisibleTo），验证协调语义与状态机；pg_advisory_lock
/// 的数据库端互斥已由 PostgresMigrationRunner.MigrateAsync 既有逻辑覆盖
/// （集成测试 ContextCore.IntegrationTests 验证）。
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("R29")]
public sealed class R29T_MigrationCoordinatorTests
{
    // =========================================================================
    // Part 1: Options 默认值
    // =========================================================================

    [TestMethod]
    public void MigrationCoordinatorOptions_Defaults_AreBootFriendly()
    {
        var options = new MigrationCoordinatorOptions();

        Assert.IsTrue(options.StartupRunEnabled);
        Assert.AreEqual(300, options.StartupTimeoutSeconds);
        Assert.IsFalse(string.IsNullOrWhiteSpace(options.InstanceId));
    }

    // =========================================================================
    // Part 2: 迁移互斥锁键派生
    // =========================================================================

    [TestMethod]
    public void ComputeLockKey_IsDeterministicAndPositive()
    {
        var options = new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            TablePrefix = "cc_"
        };

        var first = PostgresMigrationRunner.ComputeMigrationLockKey(options);
        var second = PostgresMigrationRunner.ComputeMigrationLockKey(options);

        Assert.AreEqual(first, second);
        Assert.IsTrue(first > 0, "锁键必须为正 long（掩掉 FNV-1a 最高位）。");
    }

    [TestMethod]
    public void ComputeLockKey_DiffersForDifferentPrefixOrSchema()
    {
        var baseOptions = new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            TablePrefix = "cc_"
        };
        var otherPrefix = new PostgresOptions
        {
            ConnectionString = baseOptions.ConnectionString,
            TablePrefix = "cc_prod_"
        };
        var otherSchema = new PostgresOptions
        {
            ConnectionString = baseOptions.ConnectionString,
            TablePrefix = "cc_",
            SchemaName = "tenant_b"
        };

        var baseKey = PostgresMigrationRunner.ComputeMigrationLockKey(baseOptions);
        Assert.AreNotEqual(baseKey, PostgresMigrationRunner.ComputeMigrationLockKey(otherPrefix));
        Assert.AreNotEqual(baseKey, PostgresMigrationRunner.ComputeMigrationLockKey(otherSchema));
    }

    // =========================================================================
    // Part 3: 协调器状态机
    // =========================================================================

    [TestMethod]
    public async Task EnsureSchema_UpToDate_ShortCircuitsWithoutMigrating()
    {
        var migrateInvocations = 0;
        var coordinator = new PostgresMigrationCoordinator(
            migrateExecutor: _ => { Interlocked.Increment(ref migrateInvocations); return Task.CompletedTask; },
            appliedVersionReader: _ => Task.FromResult<string?>(PostgresMigrationRunner.SchemaVersion),
            options: CreateOptions(),
            coordinatorOptions: new MigrationCoordinatorOptions());

        var status = await coordinator.EnsureSchemaAsync().ConfigureAwait(false);

        Assert.AreEqual(0, migrateInvocations);
        Assert.AreEqual(MigrationCoordinatorPhase.UpToDate, status.Phase);
        Assert.IsTrue(status.LastRunSucceeded);
        Assert.IsTrue(status.UpToDate);
        Assert.AreEqual("already up to date", status.LastRunMessage);
        Assert.IsNotNull(status.LastRunAtUtc);
    }

    [TestMethod]
    public async Task EnsureSchema_StaleVersion_InvokesMigrationOnce()
    {
        var migrated = 0;
        var migrateInvocations = 0;
        // 迁移执行前版本陈旧；执行后返回最新版本（waiters 复查短路）。
        Func<CancellationToken, Task<string?>> reader = _ => Task.FromResult<string?>(
            Volatile.Read(ref migrated) == 0
                ? "cc-schema-v1"
                : PostgresMigrationRunner.SchemaVersion);
        var coordinator = new PostgresMigrationCoordinator(
            migrateExecutor: _ =>
            {
                Interlocked.Increment(ref migrateInvocations);
                Interlocked.Exchange(ref migrated, 1);
                return Task.CompletedTask;
            },
            appliedVersionReader: reader,
            options: CreateOptions(),
            coordinatorOptions: new MigrationCoordinatorOptions());

        var status = await coordinator.EnsureSchemaAsync().ConfigureAwait(false);

        Assert.AreEqual(1, migrateInvocations);
        Assert.AreEqual(MigrationCoordinatorPhase.UpToDate, status.Phase);
        Assert.IsTrue(status.LastRunSucceeded);
        Assert.AreEqual("migration applied", status.LastRunMessage);
    }

    [TestMethod]
    public async Task EnsureSchema_MigrationFailure_MarksFailedAndRethrows()
    {
        var coordinator = new PostgresMigrationCoordinator(
            migrateExecutor: _ => throw new InvalidOperationException("DDL 执行失败"),
            appliedVersionReader: _ => Task.FromResult<string?>("cc-schema-v1"),
            options: CreateOptions(),
            coordinatorOptions: new MigrationCoordinatorOptions());

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.EnsureSchemaAsync()).ConfigureAwait(false);

        var status = await coordinator.GetStatusAsync().ConfigureAwait(false);
        Assert.AreEqual(MigrationCoordinatorPhase.Failed, status.Phase);
        Assert.IsFalse(status.LastRunSucceeded);
        Assert.IsNotNull(status.LastRunMessage);
    }

    [TestMethod]
    public async Task EnsureSchema_ConcurrentCalls_ExecuteMigrationOnce()
    {
        var migrated = 0;
        var migrateInvocations = 0;
        Func<CancellationToken, Task<string?>> reader = _ => Task.FromResult<string?>(
            Volatile.Read(ref migrated) == 0
                ? "cc-schema-v1"
                : PostgresMigrationRunner.SchemaVersion);
        var coordinator = new PostgresMigrationCoordinator(
            migrateExecutor: async _ =>
            {
                Interlocked.Increment(ref migrateInvocations);
                Interlocked.Exchange(ref migrated, 1);
                // 模拟迁移耗时：让并发调用真实地在门闩上等待。
                await Task.Delay(50).ConfigureAwait(false);
            },
            appliedVersionReader: reader,
            options: CreateOptions(),
            coordinatorOptions: new MigrationCoordinatorOptions());

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => coordinator.EnsureSchemaAsync())
            .ToArray();
        var statuses = await Task.WhenAll(tasks).ConfigureAwait(false);

        // 单执行者：5 个并发调用只有一个执行迁移，其余等待后复查短路。
        Assert.AreEqual(1, migrateInvocations);
        foreach (var status in statuses)
        {
            Assert.AreEqual(MigrationCoordinatorPhase.UpToDate, status.Phase);
            Assert.IsTrue(status.LastRunSucceeded);
        }
    }

    [TestMethod]
    public async Task GetStatus_BeforeAnyRun_ReportsIdle()
    {
        var coordinator = new PostgresMigrationCoordinator(
            migrateExecutor: _ => Task.CompletedTask,
            appliedVersionReader: _ => Task.FromResult<string?>(null),
            options: CreateOptions(),
            coordinatorOptions: new MigrationCoordinatorOptions());

        var status = await coordinator.GetStatusAsync().ConfigureAwait(false);

        Assert.AreEqual(MigrationCoordinatorPhase.Idle, status.Phase);
        Assert.IsTrue(status.Enabled);
        Assert.IsFalse(status.UpToDate);
        Assert.IsNotNull(status.InstanceId);
        Assert.IsNull(status.LastRunAtUtc);
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static PostgresOptions CreateOptions()
    {
        return new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            TablePrefix = "cc_"
        };
    }
}
