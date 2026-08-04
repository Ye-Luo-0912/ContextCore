using System.Diagnostics.Metrics;

namespace ContextCore.Inference.Onnx;

/// <summary>
/// ContextCore.Inference.Onnx 遥测仪表盘（基于 <see cref="System.Diagnostics.Metrics.Meter"/>）。
/// 与 ContextCore.Core 的 CoreMetrics 同构：static readonly 字段发布到任意已注册的
/// MeterListener（包括 OpenTelemetry）。推理热路径上仅做 O(1) 的 Add / Record，无锁竞争。
/// </summary>
public static class InferenceMetrics
{
    private static readonly Meter _meter = new("ContextCore.Inference.Onnx", "1.0");

    /// <summary>
    /// 节点标识：Environment.MachineName（与 ModelStateReconcilerWorker 的节点标识约定一致）。
    /// 推理指标统一带 node 维度，多节点部署下可按节点定位会话竞争 / 填充率 / 取消浪费。
    /// </summary>
    public static readonly string NodeId = Environment.MachineName;

    /// <summary>
    /// 构建 (model, node) 标签对。模型与节点在实例生命周期内固定，
    /// 调用方应预计算复用，避免热路径重复分配。
    /// </summary>
    public static KeyValuePair<string, object?>[] ModelNodeTags(string modelId)
        => [new("model", modelId), new("node", NodeId)];

    /// <summary>
    /// 构建 (model, node, batch) 标签组。batch 为本次指标事件涉及的批次行数，
    /// 供批次类指标（排队等待 / 填充率 / 分片）按批次大小维度聚合。
    /// </summary>
    public static KeyValuePair<string, object?>[] ModelNodeBatchTags(string modelId, int batchRows)
        => [new("model", modelId), new("node", NodeId), new("batch", batchRows)];

    /// <summary>
    /// 会话竞争次数：请求到达推理引擎时所有并发槽位均被占用（需排队等待槽位）。
    /// 反映上游并发是否超过引擎承载能力，配合 Queue 阶段耗时（InferencePhaseTimingCallback）归因。
    /// 维度：model（模型版本）、node（节点）。
    /// </summary>
    public static readonly Counter<long> SessionContention =
        _meter.CreateCounter<long>(
            "contextcore.inference.session_contention",
            unit: "{requests}",
            description: "推理请求到达时所有并发槽位均被占用的次数（会话竞争），按 model/node 维度标注");

    /// <summary>
    /// 分片执行次数：单个 batch 超过 MaxBatchSize 时按分片执行的 shard 总数。
    /// 反映大 batch 对 GPU 显存/CPU 内存的压力缓解情况。
    /// 维度：model（模型版本）、node（节点）、batch（被分片的批次行数）。
    /// </summary>
    public static readonly Counter<long> ShardsExecuted =
        _meter.CreateCounter<long>(
            "contextcore.inference.shards_executed",
            unit: "{shards}",
            description: "因超过 MaxBatchSize 而分片执行的 shard 总数，按 model/node/batch 维度标注");

    /// <summary>
    /// 调度器排队等待时长（毫秒）：请求入队到派发执行的等待时间（动态批处理路径）。
    /// 维度：model（模型版本）、node（节点）、batch（该请求贡献的行数）。
    /// </summary>
    public static readonly Histogram<double> QueueWaitDuration =
        _meter.CreateHistogram<double>(
            "contextcore.inference.queue_wait.duration",
            unit: "ms",
            description: "InferenceScheduler 中请求从入队到派发执行的等待时长，按 model/node/batch 维度标注");

    /// <summary>
    /// 微批填充率：派发批次实际行数 / MaxBatchSize（0~1 为正常；>1 表示超限走了分片）。
    /// 反映动态批处理窗口内请求聚合的收益，用于调优 BatchWaitWindow 与 MaxBatchSize。
    /// 维度：model（模型版本）、node（节点）、batch（派发批次行数）。
    /// </summary>
    public static readonly Histogram<double> BatchFillRatio =
        _meter.CreateHistogram<double>(
            "contextcore.inference.batch_fill_ratio",
            unit: "{ratio}",
            description: "InferenceScheduler 派发微批的填充率（实际行数 / MaxBatchSize），按 model/node/batch 维度标注");

    /// <summary>
    /// 取消浪费次数：调用方取消但已进入排队/攒批的请求数。
    /// 这些请求占用过队列或批次容量却未执行推理，反映上游取消率与调度开销。
    /// 维度：model（模型版本）、node（节点）。
    /// </summary>
    public static readonly Counter<long> CancellationWaste =
        _meter.CreateCounter<long>(
            "contextcore.inference.cancellation_waste",
            unit: "{requests}",
            description: "调用方取消但已入队/已攒批、未执行推理的请求数（浪费的调度容量），按 model/node 维度标注");
}
