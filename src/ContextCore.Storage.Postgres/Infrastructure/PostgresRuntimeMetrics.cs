using System.Diagnostics.Metrics;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// ContextCore.Storage.Postgres 运行时遥测仪表盘（连接池 / 死元组 / 锁等待 / 复制滞后）。
/// 采用与 <see cref="PostgresMigrationMetrics"/> 相同的 static readonly Meter 模式，
/// 发布到任意已注册的 MeterListener（包括 OpenTelemetry）。
/// 采样委托由 PostgresRuntimeMetricsCollector（Service 层）注册；未注册（非 Postgres provider）
/// 时保持默认 0，不产生任何 SQL 探针。
/// </summary>
public static class PostgresRuntimeMetrics
{
    private static readonly Meter _meter = new("ContextCore.Storage.Postgres", "1.0");

    /// <summary>当前数据库服务端连接数提供器（采集器注册；默认 0）。</summary>
    public static Func<long> ConnectionCountProvider { get; set; } = static () => 0;

    /// <summary>死元组总数提供器（采集器注册；默认 0）。</summary>
    public static Func<long> DeadTupleProvider { get; set; } = static () => 0;

    /// <summary>等待中锁数量提供器（采集器注册；默认 0）。</summary>
    public static Func<long> WaitingLockProvider { get; set; } = static () => 0;

    /// <summary>复制滞后秒数提供器（采集器注册；无 standby 时 0）。</summary>
    public static Func<double> ReplicationLagProvider { get; set; } = static () => 0;

    /// <summary>
    /// 当前数据库服务端连接数（pg_stat_activity 按当前数据库统计）。
    /// 连接池压力的服务端视角：连接数异常增长（未释放 / 泄漏）时该指标先行告警。
    /// </summary>
    public static readonly ObservableGauge<long> ConnectionCount = _meter.CreateObservableGauge<long>(
        "contextcore.postgres.connections",
        () => ConnectionCountProvider(),
        unit: "{connections}",
        description: "当前数据库的服务端连接数（pg_stat_activity）");

    /// <summary>pg_stat_user_tables 未清理死元组总数（表膨胀信号；VACUUM 健康度）。</summary>
    public static readonly ObservableGauge<long> DeadTupleCount = _meter.CreateObservableGauge<long>(
        "contextcore.postgres.dead_tuples",
        () => DeadTupleProvider(),
        unit: "{tuples}",
        description: "pg_stat_user_tables 未清理死元组总数");

    /// <summary>pg_locks 中未授予（等待中）的锁数量（锁竞争信号）。</summary>
    public static readonly ObservableGauge<long> WaitingLockCount = _meter.CreateObservableGauge<long>(
        "contextcore.postgres.locks.waiting",
        () => WaitingLockProvider(),
        unit: "{locks}",
        description: "pg_locks 中未授予的等待锁数量");

    /// <summary>pg_stat_replication 各 standby 的最大 replay 滞后秒数（无 standby 为 0）。</summary>
    public static readonly ObservableGauge<double> ReplicationLagSeconds = _meter.CreateObservableGauge<double>(
        "contextcore.postgres.replication.lag_seconds",
        () => ReplicationLagProvider(),
        unit: "s",
        description: "pg_stat_replication 最大 replay 滞后秒数");
}
