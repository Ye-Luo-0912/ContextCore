using System.Diagnostics.Metrics;

namespace ContextCore.Embedding;

/// <summary>
/// ContextCore.Embedding 遥测仪表盘（基于 <see cref="System.Diagnostics.Metrics.Meter"/>）。
/// </summary>
public static class EmbeddingMetrics
{
    private static readonly Meter _meter = new("ContextCore.Embedding", "1.0");

    /// <summary>Embedding 计算耗时（毫秒，含缓存查找与 ONNX 推理）。</summary>
    public static readonly Histogram<double> EmbedDuration =
        _meter.CreateHistogram<double>(
            "contextcore.embedding.duration",
            unit: "ms",
            description: "OnnxEmbeddingProvider.EmbedAsync 调用端到端耗时");

    /// <summary>每次 EmbedAsync 调用的向量数量（batch size）。</summary>
    public static readonly Histogram<int> EmbedBatchSize =
        _meter.CreateHistogram<int>(
            "contextcore.embedding.batch_size",
            unit: "{vectors}",
            description: "每次 EmbedAsync 调用包含的输入向量数");

    /// <summary>Embedding 内容 Hash 缓存命中次数。</summary>
    public static readonly Counter<long> CacheHits =
        _meter.CreateCounter<long>(
            "contextcore.embedding.cache_hits",
            unit: "{hits}",
            description: "EmbeddingCacheService 内容 Hash 命中次数（跳过 ONNX 推理的次数）");
}
