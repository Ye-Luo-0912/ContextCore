namespace ContextCore.ControlRoom.Models;

/// <summary>
/// 紧凑的运维报告快照，用于统一展示历史 eval 报告的状态、建议、指标和阻断原因。
/// </summary>
public sealed class OperationalReportSnapshot
{
    public string ReportKey { get; init; } = string.Empty;

    public string DisplayTitle { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public bool Available { get; init; }

    public bool Passed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> KeyMetrics { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}
