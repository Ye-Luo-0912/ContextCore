using System.Diagnostics.Metrics;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// ContextCore.Storage.Postgres 作业队列遥测仪表盘（基于 <see cref="Meter"/>）。
/// 使用 static readonly 字段发布到任意已注册的 MeterListener（包括 OpenTelemetry），
/// 与 <see cref="PostgresMigrationMetrics"/> 保持同一模式。
/// </summary>
public static class PostgresJobQueueMetrics
{
    private static readonly Meter _meter = new("ContextCore.Storage.Postgres", "1.0");

    /// <summary>作业排队等待 SLO 阈值：等待超过该时长视为 SLO 超标（超标计数依据）。</summary>
    public static readonly TimeSpan QueueWaitSlo = TimeSpan.FromSeconds(30);

    /// <summary>作业从入队到被领取的等待时长（毫秒）。
    /// 重试/过期回收的作业以原始 CreatedAt 计算，等待时长含处理间隔。</summary>
    public static readonly Histogram<double> QueueWait =
        _meter.CreateHistogram<double>(
            "contextcore.postgres.jobqueue.wait.duration",
            unit: "ms",
            description: "作业从入队到被领取的等待时长（重试/过期回收作业含处理间隔）");

    /// <summary>排队等待超过 SLO 的作业累计数（按 <see cref="QueueWaitSlo"/> 判定）。</summary>
    public static readonly Counter<long> QueueWaitSloExceeded =
        _meter.CreateCounter<long>(
            "contextcore.postgres.jobqueue.wait.slo_exceeded.total",
            description: "排队等待超过 SLO 阈值的作业累计数");

    /// <summary>当前排队深度（Queued / WaitingRetry / 租约已过期的 Running 作业数）。
    /// 由作业队列在领取批次后更新，供扩缩容与饱和告警观测。</summary>
    public static readonly Gauge<long> QueueDepth =
        _meter.CreateGauge<long>(
            "contextcore.postgres.jobqueue.depth",
            description: "当前排队作业数（含等待重试与租约过期的 Running 作业）");
}
