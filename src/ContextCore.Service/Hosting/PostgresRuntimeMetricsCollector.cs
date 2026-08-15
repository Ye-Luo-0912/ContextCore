using System.Threading;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Hosting;

/// <summary>
/// Postgres 运行时指标采集器：周期性采样连接池统计（NpgsqlDataSourceStatistics）、
/// 死元组（pg_stat_user_tables）、等待锁（pg_locks）与复制滞后（pg_stat_replication），
/// 发布到 <see cref="PostgresRuntimeMetrics"/> 的 ObservableGauge（OpenTelemetry / MeterListener 消费）。
/// </summary>
/// <remarks>
/// 仅 Postgres provider 时激活：探测到未注册 <see cref="PostgresConnectionFactory"/>（非 Postgres
/// provider）立即退出 no-op。采样与采集器生命周期解耦——采集器停止时恢复默认委托，
/// 避免 gauge 回调引用已销毁的工厂。
/// </remarks>
public sealed class PostgresRuntimeMetricsCollector : BackgroundService
{
    /// <summary>默认采样间隔（未配置 RunRecoveryInterval 时）。</summary>
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 单条采样 SQL：连接数（pg_stat_activity 按当前数据库）+ 死元组（pg_stat_user_tables 求和）
    /// + 等待锁（pg_locks 未授予）+ 复制滞后（pg_stat_replication 最大 replay 滞后，无 standby 为 0）。
    /// 全部走系统目录视图，不依赖业务表结构；一次往返完成四类采样。
    /// </summary>
    internal static readonly string SamplingSql = """
        SELECT
          (SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database()),
          (SELECT COALESCE(SUM(n_dead_tup), 0) FROM pg_stat_user_tables),
          (SELECT COUNT(*) FROM pg_locks WHERE NOT granted),
          (SELECT COALESCE(MAX(EXTRACT(EPOCH FROM (replay_lag))), 0)
             FROM pg_stat_replication WHERE replay_lag IS NOT NULL);
        """;

    private readonly IServiceProvider _services;
    private readonly ILogger<PostgresRuntimeMetricsCollector> _logger;
    private readonly TimeSpan _interval;

    public PostgresRuntimeMetricsCollector(
        IServiceProvider services,
        ContextCoreRuntimeOptions options,
        ILogger<PostgresRuntimeMetricsCollector> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // 复用 RunRecoveryInterval 作为采样间隔（与 DecisionCommitWorker 同一约定；
        // 未配置时用默认 30 秒）。
        _interval = options is not null && options.RunRecoveryInterval > TimeSpan.Zero
            ? options.RunRecoveryInterval
            : DefaultInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var probeScope = _services.CreateScope();
        PostgresConnectionFactory? factory;
        try
        {
            factory = probeScope.ServiceProvider.GetService<PostgresConnectionFactory>();
        }
        catch (Exception ex)
        {
            // filesystem/memory 组合下 PostgresConnectionFactory 可能已注册但连接串为空
            // （构造即抛 InvalidOperationException）——视为非 Postgres 运行，自退出 no-op。
            _logger.LogInformation(
                "PostgresRuntimeMetricsCollector 检测到 PostgresConnectionFactory 不可用（{Reason}），自退出。",
                ex.Message);
            return;
        }
        if (factory is null)
        {
            _logger.LogInformation(
                "PostgresRuntimeMetricsCollector 检测到未注册 PostgresConnectionFactory（非 Postgres provider），自退出。");
            return;
        }

        var snapshot = new RuntimeSample();
        RegisterProviders(snapshot);
        _logger.LogInformation(
            "PostgresRuntimeMetricsCollector 启动：采样连接池 / 死元组 / 锁等待 / 复制滞后（间隔 {Interval}）。",
            _interval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SampleAsync(factory, snapshot, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 采样失败不中断后续轮询（数据库瞬时不可用 / 权限缺失时指标保持上一轮值）。
                    _logger.LogError(ex, "PostgresRuntimeMetricsCollector 采样异常（继续下一轮）。");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            // 恢复默认委托：采集器销毁后 gauge 不再引用工厂 / 快照。
            ResetProviders();
            _logger.LogInformation("PostgresRuntimeMetricsCollector 已停止。");
        }
    }

    /// <summary>把采样快照注册为各 gauge 的取值委托（快照字段用 Volatile 读写保证跨线程可见）。</summary>
    private static void RegisterProviders(RuntimeSample snapshot)
    {
        PostgresRuntimeMetrics.ConnectionCountProvider = () => Volatile.Read(ref snapshot.ConnectionCount);
        PostgresRuntimeMetrics.DeadTupleProvider = () => Volatile.Read(ref snapshot.DeadTuples);
        PostgresRuntimeMetrics.WaitingLockProvider = () => Volatile.Read(ref snapshot.WaitingLocks);
        PostgresRuntimeMetrics.ReplicationLagProvider = () => Volatile.Read(ref snapshot.ReplicationLagSeconds);
    }

    /// <summary>恢复默认委托（全部归零，不引用任何实例）。</summary>
    private static void ResetProviders()
    {
        PostgresRuntimeMetrics.ConnectionCountProvider = static () => 0;
        PostgresRuntimeMetrics.DeadTupleProvider = static () => 0;
        PostgresRuntimeMetrics.WaitingLockProvider = static () => 0;
        PostgresRuntimeMetrics.ReplicationLagProvider = static () => 0;
    }

    /// <summary>执行一轮采样：连接数 / 死元组 / 锁等待 / 复制滞后走单条 SQL 往返。</summary>
    private static async Task SampleAsync(PostgresConnectionFactory factory, RuntimeSample snapshot, CancellationToken ct)
    {
        await using var connection = await factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = SamplingSql;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            Volatile.Write(ref snapshot.ConnectionCount, reader.GetInt64(0));
            Volatile.Write(ref snapshot.DeadTuples, reader.GetInt64(1));
            Volatile.Write(ref snapshot.WaitingLocks, reader.GetInt64(2));
            Volatile.Write(ref snapshot.ReplicationLagSeconds, reader.GetDouble(3));
        }
    }

    /// <summary>一轮采样的线程安全快照（字段经 Volatile 读写，采样线程写、gauge 回调读）。</summary>
    private sealed class RuntimeSample
    {
        public long ConnectionCount;
        public long DeadTuples;
        public long WaitingLocks;
        public double ReplicationLagSeconds;
    }
}
