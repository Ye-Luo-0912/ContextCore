using System.Diagnostics.Metrics;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// ContextCore.Storage.Postgres 迁移遥测仪表盘（基于 <see cref="Meter"/>）。
/// 使用 static readonly 字段发布到任意已注册的 MeterListener（包括 OpenTelemetry），
/// 与 Core 层 CoreMetrics 保持同一模式。
/// </summary>
public static class PostgresMigrationMetrics
{
    private static readonly Meter _meter = new("ContextCore.Storage.Postgres", "1.0");

    /// <summary>单条迁移 DDL（版本化步骤阶段或基线批次）执行耗时（毫秒）。</summary>
    public static readonly Histogram<double> DdlDuration =
        _meter.CreateHistogram<double>(
            "contextcore.postgres.migration.ddl.duration",
            unit: "ms",
            description: "Postgres 迁移单条 DDL / 基线批次执行耗时");

    /// <summary>等待迁移互斥锁（pg_advisory_lock）的耗时（毫秒）。</summary>
    public static readonly Histogram<double> LockWaitDuration =
        _meter.CreateHistogram<double>(
            "contextcore.postgres.migration.lockwait.duration",
            unit: "ms",
            description: "Postgres 迁移等待 pg_advisory_lock 互斥锁的耗时");

    /// <summary>迁移失败的 schema 版本计数，按 version 标签区分。</summary>
    public static readonly Counter<long> FailedVersions =
        _meter.CreateCounter<long>(
            "contextcore.postgres.migration.failed_versions",
            unit: "{versions}",
            description: "Postgres 迁移失败的 schema 版本数（不含取消）");

    /// <summary>已应用的版本化迁移步骤数，按 step 标签区分。</summary>
    public static readonly Counter<long> StepsApplied =
        _meter.CreateCounter<long>(
            "contextcore.postgres.migration.steps_applied",
            unit: "{steps}",
            description: "Postgres 已执行的版本化迁移步骤阶段数");
}
