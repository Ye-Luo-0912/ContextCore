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

    /// <summary>作业从入队到被领取的等待时长（毫秒）。
    /// 重试/过期回收的作业以原始 CreatedAt 计算，等待时长含处理间隔。</summary>
    public static readonly Histogram<double> QueueWait =
        _meter.CreateHistogram<double>(
            "contextcore.postgres.jobqueue.wait.duration",
            unit: "ms",
            description: "作业从入队到被领取的等待时长（重试/过期回收作业含处理间隔）");
}
