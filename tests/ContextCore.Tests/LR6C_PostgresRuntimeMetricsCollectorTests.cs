using ContextCore.Abstractions;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

// ===========================================================================
// Postgres 运行时指标采集器测试（连接数 / 死元组 / 锁等待 / 复制滞后）
//
// 覆盖：
// 1. WorkerRegistry 无条件注册（非 Postgres provider 自退出 no-op）；
// 2. 未注册 PostgresConnectionFactory（非 Postgres provider）→ 启动即退出，不崩溃；
// 3. 采样 SQL 形状守卫（必须读系统目录视图，不依赖业务表结构）；
// 4. 停止后 gauge 恢复默认委托（不引用已释放的工厂 / 快照，优雅停止）。
// ===========================================================================

[TestClass]
[TestCategory("LR6C")]
public sealed class LR6C_PostgresRuntimeMetricsCollectorTests
{
    [TestMethod]
    public void Development_Profile_WorkerRegistry_ContainsMetricsCollector()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ProductionRuntimeWorkerRegistry>();
        CollectionAssert.Contains(registry.WorkerTypeNames.ToList(), nameof(PostgresRuntimeMetricsCollector),
            "WorkerRegistry 应包含 PostgresRuntimeMetricsCollector（无条件注册；非 Postgres provider 自退出）。");
    }

    [TestMethod]
    public async Task NoFactory_ExitsNoop_CleanStartStop()
    {
        // 非 Postgres provider（未注册 PostgresConnectionFactory）→ 探测后自退出 no-op。
        var services = new ServiceCollection();
        services.AddSingleton(new ContextCoreRuntimeOptions { RunRecoveryInterval = TimeSpan.FromMilliseconds(50) });
        services.AddSingleton(sp => new PostgresRuntimeMetricsCollector(
            sp, sp.GetRequiredService<ContextCoreRuntimeOptions>(), NullLogger<PostgresRuntimeMetricsCollector>.Instance));
        await using var provider = services.BuildServiceProvider();

        var collector = provider.GetRequiredService<PostgresRuntimeMetricsCollector>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await collector.StartAsync(cts.Token);
        await Task.Delay(100);
        await collector.StopAsync(cts.Token);

        // 未抛异常即通过；且 gauge 保持默认 0（采集器未注册采样委托）。
        Assert.AreEqual(0, PostgresRuntimeMetrics.ConnectionCountProvider());
    }

    [TestMethod]
    public async Task EmptyConnectionString_FactoryRegistered_ExitsNoop()
    {
        // filesystem/memory 组合的等价场景：PostgresConnectionFactory 已注册但连接串为空
        // （构造即抛 InvalidOperationException）。探测应视为非 Postgres 自退出，不崩溃
        // （曾因 GetService 传播构造异常导致 BackgroundService StopHost）。
        var services = new ServiceCollection();
        services.AddSingleton(new PostgresOptions { Enabled = false, ConnectionString = string.Empty });
        services.AddSingleton<PostgresConnectionFactory>();
        services.AddSingleton(new ContextCoreRuntimeOptions { RunRecoveryInterval = TimeSpan.FromMilliseconds(50) });
        services.AddSingleton(sp => new PostgresRuntimeMetricsCollector(
            sp, sp.GetRequiredService<ContextCoreRuntimeOptions>(), NullLogger<PostgresRuntimeMetricsCollector>.Instance));
        await using var provider = services.BuildServiceProvider();

        var collector = provider.GetRequiredService<PostgresRuntimeMetricsCollector>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await collector.StartAsync(cts.Token);
        await Task.Delay(100);
        await collector.StopAsync(cts.Token);

        Assert.AreEqual(0, PostgresRuntimeMetrics.ConnectionCountProvider());
    }

    [TestMethod]
    public void SamplingSql_ReferencesSystemStatViews()
    {
        var sql = PostgresRuntimeMetricsCollector.SamplingSql;

        StringAssert.Contains(sql, "pg_stat_activity", "连接数采样应读 pg_stat_activity。");
        StringAssert.Contains(sql, "pg_stat_user_tables", "死元组采样应读 pg_stat_user_tables。");
        StringAssert.Contains(sql, "pg_locks", "等待锁采样应读 pg_locks。");
        StringAssert.Contains(sql, "pg_stat_replication", "复制滞后采样应读 pg_stat_replication。");
    }

    [TestMethod]
    public async Task StartThenStop_RestoresDefaultGaugeProviders()
    {
        // 不可达连接串：NpgsqlDataSource 惰性创建，采样期 OpenConnectionAsync 快速失败并被捕获。
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            ConnectionString = "Host=127.0.0.1;Port=1;Database=stub;Username=stub;Password=stub",
            AutoMigrate = false
        });

        var services = new ServiceCollection();
        services.AddSingleton(factory);
        services.AddSingleton(new ContextCoreRuntimeOptions { RunRecoveryInterval = TimeSpan.FromMilliseconds(50) });
        services.AddSingleton(sp => new PostgresRuntimeMetricsCollector(
            sp, sp.GetRequiredService<ContextCoreRuntimeOptions>(), NullLogger<PostgresRuntimeMetricsCollector>.Instance));
        await using var provider = services.BuildServiceProvider();

        var collector = provider.GetRequiredService<PostgresRuntimeMetricsCollector>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await collector.StartAsync(cts.Token);
        await Task.Delay(200); // 若干轮采样尝试（连接失败被捕获，不中断循环）
        await collector.StopAsync(cts.Token);

        // 停止后 gauge 恢复默认委托（不再引用已释放的工厂 / 快照）。
        Assert.AreEqual(0, PostgresRuntimeMetrics.ConnectionCountProvider());
        Assert.AreEqual(0, PostgresRuntimeMetrics.DeadTupleProvider());
        Assert.AreEqual(0, PostgresRuntimeMetrics.WaitingLockProvider());
        Assert.AreEqual(0.0, PostgresRuntimeMetrics.ReplicationLagProvider());
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
