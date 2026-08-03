namespace ContextCore.Inference.Onnx;

/// <summary>
/// 推理阶段枚举（用于阶段级耗时记录）。
/// 对应 OnnxInferenceEngine.ExecuteWithSlotAndTimeoutAsync 内的各阶段：
/// - <see cref="Queue"/>：等待推理槽位（SemaphoreSlim）的排队时间。
/// - <see cref="Copy"/>：输入数据准备（FeatureBatch 已是连续内存，copy 阶段通常极短）。
/// - <see cref="Run"/>：session.InferBatchAsync 实际执行时间（含 native session.Run）。
/// - <see cref="Parse"/>：输出结果反序列化与 BatchInferenceResult 构造时间。
/// </summary>
public enum InferencePhase : byte
{
    /// <summary>队列等待（Slot 获取）。</summary>
    Queue = 0,

    /// <summary>输入拷贝（Host → Device）。</summary>
    Copy = 1,

    /// <summary>推理执行（session.Run）。</summary>
    Run = 2,

    /// <summary>输出解析（Device → Host + 反序列化）。</summary>
    Parse = 3
}

/// <summary>
/// OnnxInferenceEngine 配置。
/// 控制 ONNX 模型加载与推理的输入/输出张量映射、线程数与超时。
/// </summary>
/// <remarks>
/// ONNX 模型的输入/输出张量名因模型而异，必须由调用方根据具体模型 schema 提供：
/// - <see cref="InputTensorName"/>：输入张量名（通常为 "input" / "features" / "input_features"）
/// - <see cref="ScoreOutputName"/>：输出张量名（含主分数，通常为 "logits" / "score" / "output"）
/// - <see cref="ConfidenceOutputName"/>：可选的 confidence 输出张量名（与 Score 同张量时为 null）
/// <para>
/// 默认行为：
/// - <see cref="ApplySigmoid"/> = true：把 logits 映射到 [0,1] 概率（二分类场景）
/// - <see cref="ScoreOutputIndex"/> = 0：从输出张量取第 0 列作为正类分数
/// - <see cref="ConfidenceOutputIndex"/> = 1：若 ConfidenceOutputName=null，从 Score 张量取第 1 列
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
    /// 单次 session.Run 的最大行数限制。
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
    /// 是否在引擎构造后自动执行 warmup。
    /// 默认 true；warmup 用一个 1 行全 0 的 dummy FeatureBatch 调用一次 session.InferBatchAsync，
    /// 让 ORT 完成 graph optimization 与内存分配，避免首次真实推理的冷启动延迟。
    /// </summary>
    /// <remarks>
    /// warmup 失败不抛异常（仅记录到引擎内部状态），不影响后续真实推理；
    /// 真实推理时若 ORT 仍报错，由 InferBatchAsync 的异常处理路径捕获。
    /// </remarks>
    public bool EnableWarmup { get; init; } = true;

    /// <summary>
    /// 子问题4：单引擎允许的并发推理槽位数（SemaphoreSlim 容量）。
    /// 默认 0 = 使用 Environment.ProcessorCount；正数表示显式上限。
    /// 用于防止并发请求打满 ORT 线程池导致 P99 飙升。
    /// </summary>
    /// <remarks>
    /// 槽位控制的是 <see cref="OnnxInferenceEngine.InferBatchAsync"/> 同时调用
    /// <see cref="IOnnxInferenceSession.InferBatchAsync"/> 的最大并发数。
    /// 超过此数的请求在 SemaphoreSlim 上等待，不消耗 ORT 线程。
    /// </remarks>
    public int MaxConcurrentInferences { get; init; } = 0;

    /// <summary>
    /// 子问题4：熔断阈值（连续推理超时/失败次数）。
    /// 达到阈值后熔断器打开，所有后续推理请求立即短路返回失败。
    /// 默认 3 次。设置为 0 表示禁用熔断器。
    /// </summary>
    /// <remarks>
    /// 熔断器打开后会持续短路，直到 <see cref="CircuitBreakerResetMs"/> 时间后进入半开状态，
    /// 放行一次探测请求：成功则关闭熔断器，失败则继续保持打开。
    /// </remarks>
    public int CircuitBreakerThreshold { get; init; } = 3;

    /// <summary>
    /// 子问题4：熔断器打开后的恢复时间（毫秒）。
    /// 默认 30000ms = 30s；超时后下一次请求被允许通过（半开状态）。
    /// </summary>
    public int CircuitBreakerResetMs { get; init; } = 30000;

    /// <summary>
    /// 子问题3：热切换后旧引擎的延迟 Dispose 宽限期（毫秒）。
    /// 用于等待在旧引擎上的 in-flight 请求完成；默认 30000ms = 30s。
    /// 宽限期内旧引擎不会被 Dispose，避免 in-flight 请求因 ObjectDisposedException 失败。
    /// </summary>
    public int PreviousEngineGracePeriodMs { get; init; } = 30000;

    /// <summary>
    /// 等待推理槽位（SemaphoreSlim）的最大排队请求数。
    /// 当同时请求推理的数量超过 <see cref="MaxConcurrentInferences"/> 时，额外请求会在此队列上等待；
    /// 队列满后，新请求立即返回失败（QueueFull），避免在过载场景下请求无限期堆积。
    /// 默认 256；0 表示不限制（向后兼容旧行为）。
    /// </summary>
    /// <remarks>
    /// 该容量与 <see cref="MaxConcurrentInferences"/> 配合：
    /// 同时存在的"在飞"请求上限 ≈ MaxConcurrentInferences + BatchQueueCapacity。
    /// 推荐设置为 ASP.NET 请求并发上限的 1~2 倍，以吸收抖动同时拒绝过载。
    /// </remarks>
    public int BatchQueueCapacity { get; init; } = 256;

    /// <summary>
    /// 动态批处理窗口（毫秒）。
    /// 大于 0 时，引擎在窗口内聚集多个请求合并为一次 session.Run 以提升吞吐。
    /// 默认 0 = 禁用（每个请求独立 session.Run，与现有架构一致）。
    /// </summary>
    /// <remarks>
    /// 当前 <see cref="OnnxInferenceEngine"/> 的推理路径为每个 FeatureBatch 独立调用
    /// <see cref="IOnnxInferenceSession.InferBatchAsync"/>，并不实现真正的 dynamic batching。
    /// 此字段作为配置预留：未来引入 Batcher 时读取，调用方可提前设置。
    /// 设置为非 0 在当前版本不会触发合并，但会影响指标采集与未来行为。
    /// </remarks>
    public int DynamicBatchWindow { get; init; } = 0;

    /// <summary>
    /// 是否在构造引擎时启用 CPU 过度订阅保护。
    /// 默认 true：当 <see cref="MaxConcurrentInferences"/> × max(<see cref="IntraOpNumThreads"/>, 1)
    /// 超过 <see cref="System.Environment.ProcessorCount"/> 时，自动将并发槽位收缩为
    /// max(1, ProcessorCount / max(IntraOpNumThreads, 1))，避免 ASP.NET 请求并发 × ORT 线程池
    /// 乘积超过逻辑核心数导致的 P99 飙升与上下文切换开销。
    /// </summary>
    /// <remarks>
    /// 关闭后由调用方自行保证 <see cref="MaxConcurrentInferences"/> 配置合理；
    /// 容器化部署中 cgroup CPU quota 可能与 <see cref="Environment.ProcessorCount"/> 不一致，
    /// 此时应显式设置 <see cref="MaxConcurrentInferences"/> 并关闭本保护。
    /// </remarks>
    public bool CpuOversubscriptionGuard { get; init; } = true;

    /// <summary>
    /// 子问题7：允许的"孤儿"推理任务上限（已超时但 native session.Run 仍在后台运行的任务数）。
    /// </summary>
    /// <remarks>
    /// native <c>session.Run</c> 是同步 native 调用，无法被 <see cref="CancellationToken"/> 中断。
    /// 超时后 <see cref="OnnxInferenceEngine"/> 会释放并发槽位让其他请求继续，但孤儿 native 调用
    /// 仍在后台占用 ORT 线程池线程。若上游持续涌入请求，孤儿任务会累积并最终耗尽 ORT 线程池，
    /// 导致后续推理全部卡死（雪崩）。
    /// <para>
    /// 本字段实现 back-pressure：当孤儿数达到此上限时，新请求立即返回
    /// <c>NativePoolSaturated</c> 失败（不再进入 session.Run），直到孤儿任务完成释放容量。
    /// </para>
    /// 默认 0 = 不限制（向后兼容旧行为）；推荐设置为 <see cref="MaxConcurrentInferences"/> 的 1~2 倍，
    /// 在"吸收抖动"与"快速失败"之间取得平衡。
    /// </remarks>
    public int MaxOrphanedInferences { get; init; } = 0;

    /// <summary>
    /// 热路径优化：推理阶段耗时回调。
    /// 非空时，<see cref="OnnxInferenceEngine"/> 在每次推理的各阶段（Queue/Copy/Run/Parse）
    /// 完成后调用此回调，上报阶段级耗时。
    /// </summary>
    /// <remarks>
    /// 用途：替代用整体 Scoring 耗时作为 Inference 耗时的代理值——调用方（如
    /// <c>DefaultComponentHealthRegistry.RecordInferencePhaseTime</c>）可通过此回调
    /// 精确记录各阶段耗时，实现精确归因（区分 queue 排队 vs run 推理 vs parse 解析）。
    /// <para>
    /// 性能注意：回调在推理热路径上同步调用，实现应避免锁竞争与 IO。
    /// 建议在回调内仅做 DDSketch.Add（O(1)）或写入 channel。
    /// </para>
    /// 默认 null = 不记录阶段耗时（向后兼容）。
    /// </remarks>
    public Action<InferencePhase, TimeSpan>? InferencePhaseTimingCallback { get; init; }

    /// <summary>
    /// ONNX Runtime Execution Provider 选择（默认 <see cref="OnnxExecutionProvider.CPU"/>）。
    /// 由 <see cref="OnnxRuntimeInferenceSessionFactory.CreateSessionOptions"/> 在创建
    /// <c>InferenceSession</c> 时通过 <c>AppendExecutionProvider_*</c> 应用。
    /// </summary>
    /// <remarks>
    /// 选择指南：
    /// - <see cref="OnnxExecutionProvider.CPU"/>：默认值，纯 CPU 推理。无外部依赖，
    /// 适合容器化部署与开发环境。
    /// - <see cref="OnnxExecutionProvider.CUDA"/>：NVIDIA GPU 推理。需要安装
    /// <c>Microsoft.ML.OnnxRuntime.Gpu</c> NuGet 包（含 CUDA native 库）。
    /// 未安装 GPU 包时 session 创建会抛 <c>OnnxRuntimeException</c>，已被
    /// <see cref="ModelActivationManager"/> 捕获并转为激活失败（fail-safe）。
    /// - <see cref="OnnxExecutionProvider.TensorRT"/>：NVIDIA TensorRT 优化推理。
    /// 首次推理有较长（数十秒）的 plan 缓存构建延迟，但稳态吞吐显著高于 CUDA。
    /// 需要 TensorRT native 库与 CUDA 同时可用。
    /// - <see cref="OnnxExecutionProvider.DirectML"/>：Windows 上任意 DirectX 12
    /// 兼容 GPU 的推理（AMD / Intel / NVIDIA）。需要安装
    /// <c>Microsoft.ML.OnnxRuntime.DirectML</c> NuGet 包（含 DML native 库）。
    /// </remarks>
    public OnnxExecutionProvider ExecutionProvider { get; init; } = OnnxExecutionProvider.CPU;

    /// <summary>
    /// GPU 设备 ID（默认 0）。仅在 <see cref="ExecutionProvider"/> 为
    /// <see cref="OnnxExecutionProvider.CUDA"/>、<see cref="OnnxExecutionProvider.TensorRT"/>
    /// 或 <see cref="OnnxExecutionProvider.DirectML"/> 时生效。多 GPU 环境下指定使用哪块卡。
    /// </summary>
    public int ExecutionProviderDeviceId { get; init; } = 0;

    /// <summary>
    /// 是否启用动态批处理（默认 false）。
    /// false 时推理请求直接进入 <see cref="OnnxInferenceEngine"/>，与历史行为一致；
    /// true 时建议在 DI 注册时通过 <c>UseInferenceScheduler</c> 包裹引擎，
    /// 由 <see cref="InferenceScheduler"/> 在 BatchWaitWindow 内攒批执行。
    /// </summary>
    /// <remarks>
    /// 此字段作为元数据开关：仅当 true 且 DI 中已注册 <see cref="InferenceScheduler"/>
    /// 时才生效。启用前应通过真实 profile 验证 micro-batching 收益：
    /// 在低 QPS 单条请求场景下，micro-batching 只增加 BatchWaitWindow 量级延迟
    /// 而不增加吞吐。推荐在 QPS ≥ 100 且单次推理耗时 ≥ 1ms 时启用。
    /// </remarks>
    public bool EnableDynamicBatching { get; init; } = false;
}

/// <summary>
/// ONNX Runtime Execution Provider 枚举。
/// 控制 <see cref="OnnxRuntimeInferenceSessionFactory"/> 在创建 session 时附加的 EP。
/// </summary>
public enum OnnxExecutionProvider : byte
{
    /// <summary>
    /// CPU EP（默认）。无外部依赖，使用 ORT 内置 CPU 算子。
    /// 不调用 <c>AppendExecutionProvider_*</c>，由 ORT 默认行为决定。
    /// </summary>
    CPU = 0,

    /// <summary>
    /// CUDA EP。NVIDIA GPU 推理，需安装 Microsoft.ML.OnnxRuntime.Gpu 包。
    /// 通过 <c>SessionOptions.AppendExecutionProvider_CUDA(deviceId)</c> 应用。
    /// </summary>
    CUDA = 1,

    /// <summary>
    /// TensorRT EP。NVIDIA TensorRT 优化推理，稳态吞吐显著高于 CUDA。
    /// 通过 <c>SessionOptions.AppendExecutionProvider_Tensorrt(deviceId)</c> 应用。
    /// 首次推理有较长 plan 缓存构建延迟。
    /// </summary>
    TensorRT = 2,

    /// <summary>
    /// DirectML EP。Windows 上任意 DirectX 12 兼容 GPU（AMD / Intel / NVIDIA）推理。
    /// 通过 <c>SessionOptions.AppendExecutionProvider_DML(deviceId)</c> 应用。
    /// 需要安装 <c>Microsoft.ML.OnnxRuntime.DirectML</c> NuGet 包（含 DML native 库）。
    /// 未安装 DML 包时 session 创建会抛 <c>OnnxRuntimeException</c>，已被
    /// <see cref="ModelActivationManager"/> 捕获并转为激活失败（fail-safe）。
    /// </summary>
    DirectML = 3
}
