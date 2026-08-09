using System.Diagnostics.Metrics;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// Learning 管线遥测仪表盘（WP-W）：快照导出耗时 / 质量闸门判定分布 / 工件重建命中率。
/// 基于 <see cref="Meter"/>（Meter 名 "ContextCore.Core"，匹配 Service 的 AddMeter("ContextCore.*")
/// OTLP 通配导出）；无 MeterListener 时记录为 no-op。
/// </summary>
public static class LearningPipelineMetrics
{
    private static readonly Meter _meter = new("ContextCore.Core", "1.0");

    /// <summary>快照导出耗时（毫秒）。</summary>
    public static readonly Histogram<double> ExportDurationMs =
        _meter.CreateHistogram<double>(
            "contextcore.learning.export.duration",
            unit: "ms",
            description: "训练数据快照导出耗时");

    /// <summary>质量闸门判定分布（verdict 维度：passed / warning / blocked）。</summary>
    public static readonly Counter<long> QualityGateVerdicts =
        _meter.CreateCounter<long>(
            "contextcore.learning.quality_gate.verdicts",
            description: "数据质量闸门判定计数（按判定类型）");

    /// <summary>工件重建命中（按 SnapshotId 点查成功）。</summary>
    public static readonly Counter<long> ArtifactRebuildHits =
        _meter.CreateCounter<long>(
            "contextcore.learning.artifact_rebuild.hits",
            description: "快照工件按 SnapshotId 重建命中次数");

    /// <summary>工件重建未命中（点查失败）。</summary>
    public static readonly Counter<long> ArtifactRebuildMisses =
        _meter.CreateCounter<long>(
            "contextcore.learning.artifact_rebuild.misses",
            description: "快照工件按 SnapshotId 重建未命中次数");

    /// <summary>记录一次快照导出耗时。</summary>
    public static void RecordExportDuration(double milliseconds)
        => ExportDurationMs.Record(milliseconds);

    /// <summary>记录一次质量闸门判定。</summary>
    public static void RecordQualityGateVerdict(LearningDataQualityVerdict verdict)
        => QualityGateVerdicts.Add(1, new KeyValuePair<string, object?>("verdict", verdict.ToString()));

    /// <summary>记录一次工件重建结果（命中/未命中）。</summary>
    public static void RecordArtifactRebuild(bool hit)
    {
        if (hit)
        {
            ArtifactRebuildHits.Add(1);
        }
        else
        {
            ArtifactRebuildMisses.Add(1);
        }
    }
}
