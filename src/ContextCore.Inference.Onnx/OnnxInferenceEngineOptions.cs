namespace ContextCore.Inference.Onnx;

/// <summary>
/// R29 WP-A-2：OnnxInferenceEngine 配置。
/// 控制 ONNX 模型加载与推理的输入/输出张量映射、线程数与超时。
/// </summary>
/// <remarks>
/// ONNX 模型的输入/输出张量名因模型而异，必须由调用方根据具体模型 schema 提供：
///   - <see cref="InputTensorName"/>：输入张量名（通常为 "input" / "features" / "input_features"）
///   - <see cref="ScoreOutputName"/>：输出张量名（含主分数，通常为 "logits" / "score" / "output"）
///   - <see cref="ConfidenceOutputName"/>：可选的 confidence 输出张量名（与 Score 同张量时为 null）
/// <para>
/// 默认行为：
///   - <see cref="ApplySigmoid"/> = true：把 logits 映射到 [0,1] 概率（二分类场景）
///   - <see cref="ScoreOutputIndex"/> = 0：从输出张量取第 0 列作为正类分数
///   - <see cref="ConfidenceOutputIndex"/> = 1：若 ConfidenceOutputName=null，从 Score 张量取第 1 列
/// </para>
/// </remarks>
public sealed class OnnxInferenceEngineOptions
{
    /// <summary>
    /// ONNX 模型文件路径。
    /// 当 <see cref="IOnnxInferenceSessionFactory.CreateAsync"/> 未提供 <see cref="ModelArtifactDescriptor"/> 时，
    /// 工厂从本字段加载模型；为 null 且无 descriptor 时抛 <see cref="InvalidOperationException"/>。
    /// 当 descriptor 提供时，<see cref="ModelArtifactDescriptor.ArtifactPath"/> 优先于此字段。
    /// </summary>
    public string? ModelPath { get; init; }

    /// <summary>
    /// 模型工件 ID（fallback，descriptor 为 null 时使用；默认 "onnx-local"）。
    /// 真实生产路径应通过 <see cref="ModelArtifactDescriptor.ModelArtifactId"/> 提供。
    /// </summary>
    public string ModelArtifactId { get; init; } = "onnx-local";

    /// <summary>
    /// 模型版本号（fallback，descriptor 为 null 时使用；默认 "1.0.0"）。
    /// 真实生产路径应通过 <see cref="ModelArtifactDescriptor.ModelVersion"/> 提供。
    /// </summary>
    public string ModelVersion { get; init; } = "1.0.0";

    /// <summary>
    /// 模型工件内容哈希（fallback，descriptor 为 null 时使用）。
    /// 默认 "sha256:unspecified"；真实生产路径应通过 <see cref="ModelArtifactDescriptor.ContentHash"/> 提供。
    /// </summary>
    public string ContentHash { get; init; } = "sha256:unspecified";

    /// <summary>输入张量名（必填，需与 ONNX 模型 InputMetadata 中的 key 一致）。</summary>
    public required string InputTensorName { get; init; }

    /// <summary>主分数输出张量名（必填，需与 ONNX 模型 OutputMetadata 中的 key 一致）。</summary>
    public required string ScoreOutputName { get; init; }

    /// <summary>
    /// Confidence 输出张量名（可选；为 null 时使用 Score 输出张量的 <see cref="ConfidenceOutputIndex"/> 列）。
    /// </summary>
    public string? ConfidenceOutputName { get; init; }

    /// <summary>
    /// 从 Score 输出张量取第 N 列作为 Score（默认 0，对应二分类正类 logit）。
    /// 仅在输出为二维 [batch, classes] 时生效；一维输出始终取第 0 列。
    /// </summary>
    public int ScoreOutputIndex { get; init; } = 0;

    /// <summary>
    /// 当 <see cref="ConfidenceOutputName"/> 为 null 时，从 Score 输出张量取第 N 列作为 Confidence（默认 1，对应负类 logit）。
    /// 仅在输出为二维 [batch, classes] 时生效。
    /// </summary>
    public int ConfidenceOutputIndex { get; init; } = 1;

    /// <summary>
    /// 是否对 Score 应用 sigmoid 函数（calibrated = 1 / (1 + exp(-raw))）。
    /// 二分类 logits 输出应设为 true；回归输出应设为 false。
    /// </summary>
    public bool ApplySigmoid { get; init; } = true;

    /// <summary>
    /// 是否对 Confidence 应用 sigmoid 函数。
    /// 当 Confidence 与 Score 来自同一 logits 张量的不同列时，应与 <see cref="ApplySigmoid"/> 一致。
    /// </summary>
    public bool ApplySigmoidToConfidence { get; init; } = true;

    /// <summary>
    /// ONNX Runtime IntraOp 线程数（0 表示使用 OnnxRuntime 默认值）。
    /// 在容器化部署中通常设为 1 以避免与上游并发调度冲突。
    /// </summary>
    public int IntraOpNumThreads { get; init; } = 1;

    /// <summary>
    /// ONNX Runtime InterOp 线程数（0 表示使用 OnnxRuntime 默认值）。
    /// 仅在模型内部支持算子级并行时生效。
    /// </summary>
    public int InterOpNumThreads { get; init; } = 0;

    /// <summary>
    /// 单次推理调用（Run）的硬超时（毫秒）；超过此值时取消推理并返回失败结果。
    /// 默认 5000ms，与 <see cref="BatchInferenceRequest.TimeoutMs"/> 对齐。
    /// </summary>
    public int InferenceTimeoutMs { get; init; } = 5000;

    /// <summary>
    /// 是否启用 ONNX Runtime 内存模式（适用于输入 shape 稳定的批量推理）。
    /// 默认 true；输入 shape 频繁变化时关闭以减少内存碎片。
    /// </summary>
    public bool EnableMemoryPattern { get; init; } = true;

    /// <summary>
    /// P3 步骤3：单次 session.Run 的最大行数限制。
    /// 默认 0 = 不限制（不分片）。
    /// 大于 0 时，当 FeatureBatch.RowCount 超过此值，OnnxInferenceEngine 会按 MaxBatchSize
    /// 分片调用 session，合并各片输出。这避免 large batch 一次性加载到 GPU 显存导致 OOM。
    /// </summary>
    /// <remarks>
    /// 典型场景：GPU 部署时显存有限，单次 Run 256 行可能 OOM，设为 32 强制分 8 片执行。
    /// CPU 部署通常不需要分片（默认 0 即可）。
    /// </remarks>
    public int MaxBatchSize { get; init; } = 0;

    /// <summary>
    /// P3 步骤4：是否在引擎构造后自动执行 warmup。
    /// 默认 true；warmup 用一个 1 行全 0 的 dummy FeatureBatch 调用一次 session.InferBatchAsync，
    /// 让 ORT 完成 graph optimization 与内存分配，避免首次真实推理的冷启动延迟。
    /// </summary>
    /// <remarks>
    /// warmup 失败不抛异常（仅记录到引擎内部状态），不影响后续真实推理；
    /// 真实推理时若 ORT 仍报错，由 InferBatchAsync 的异常处理路径捕获。
    /// </remarks>
    public bool EnableWarmup { get; init; } = true;
}
