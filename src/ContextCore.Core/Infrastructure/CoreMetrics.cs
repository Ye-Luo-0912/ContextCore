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

    // ── R13.4 #2：Event Sink 观测管线指标 ─────────────────────────────────
    // 以下计数器由 BoundedChannelContextEventSink 记录，反映 BestEffort 事件通道的背压与健康度。
    // Required sink 不走通道，不参与这些计数器。

    /// <summary>因通道满而被丢弃的事件数（仅 BestEffort 路径）。</summary>
    public static readonly Counter<long> EventSinkDropped =
        _meter.CreateCounter<long>(
            "contextcore.eventsink.dropped",
            unit: "{events}",
            description: "BoundedChannelContextEventSink 因通道满而丢弃的事件数");

    /// <summary>批量写入失败的次数（仅 BestEffort 路径，fail-open 吞掉异常）。</summary>
    public static readonly Counter<long> EventSinkErrors =
        _meter.CreateCounter<long>(
            "contextcore.eventsink.errors",
            unit: "{batches}",
            description: "BoundedChannelContextEventSink 批量写入失败的次数");

    /// <summary>已成功提交的批量写入次数（仅 BestEffort 路径）。</summary>
    public static readonly Counter<long> EventSinkBatchEmits =
        _meter.CreateCounter<long>(
            "contextcore.eventsink.batch_emits",
            unit: "{batches}",
            description: "BoundedChannelContextEventSink 已成功提交的批量写入次数");
}
