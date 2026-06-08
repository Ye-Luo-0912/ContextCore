using System.Diagnostics.Metrics;

namespace ContextCore.Core;

/// <summary>
/// ContextCore.Core 遥测仪表盘（基于 <see cref="System.Diagnostics.Metrics.Meter"/>）。
/// 使用 static readonly 字段发布到任意已注册的 MeterListener（包括 OpenTelemetry）。
/// </summary>
public static class CoreMetrics
{
    private static readonly Meter _meter = new("ContextCore.Core", "1.0");

    /// <summary>上下文包构建耗时（毫秒）。</summary>
    public static readonly Histogram<double> PackageBuildDuration =
        _meter.CreateHistogram<double>(
            "contextcore.package.build.duration",
            unit: "ms",
            description: "上下文包（ContextPackage）构建端到端耗时");

    /// <summary>混合检索耗时（毫秒）。</summary>
    public static readonly Histogram<double> RetrievalDuration =
        _meter.CreateHistogram<double>(
            "contextcore.retrieval.duration",
            unit: "ms",
            description: "HybridContextRetriever 检索端到端耗时");

    /// <summary>LLM 压缩耗时（毫秒）。</summary>
    public static readonly Histogram<double> CompressionDuration =
        _meter.CreateHistogram<double>(
            "contextcore.compression.duration",
            unit: "ms",
            description: "LlmContextCompressor 压缩端到端耗时（含模型调用）");

    /// <summary>压缩消耗 Token 数（仅在成功时计入）。</summary>
    public static readonly Counter<long> CompressionTokens =
        _meter.CreateCounter<long>(
            "contextcore.compression.tokens",
            unit: "{tokens}",
            description: "LLM 压缩消耗的 Token 总数（inputTokens + outputTokens）");
}
