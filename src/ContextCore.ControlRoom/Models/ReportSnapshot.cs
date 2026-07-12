namespace ContextCore.ControlRoom.Models;

/// <summary>统一的报告快照视图，供 ControlRoom 页面消费。</summary>
public sealed class ReportSnapshot
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = "NotGenerated";
    public string? Recommendation { get; init; }
    public Dictionary<string, string> Metrics { get; init; } = new();
    public string? ArtifactPath { get; init; }
    public string? GatePath { get; init; }
}
