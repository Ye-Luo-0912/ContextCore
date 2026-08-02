using ContextCore.Abstractions;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// ONNX Inference Session 契约
//
// 目标（对齐 R29 Production Intelligence Spec §8 Workstream A）：
//   1. 把 ONNX Runtime 的会话生命周期与推理调用从 IBatchInferenceEngine 实现中
//      剥离出来，便于在 OnnxInferenceEngine 之外做单元测试（mock session），
//      同时与 ContextCore.Embedding.IOnnxEmbeddingSessionFactory 模式对齐。
//   2. 契约层不暴露 Microsoft.ML.OnnxRuntime 类型，让测试与替代实现可绕过
//      原生库依赖（OnnxRuntime 在容器外环境加载真实模型代价较高）。
//   3. IOnnxInferenceSession 暴露 ModelArtifactId / ModelVersion / ContentHash
//      三个元数据属性，让 OnnxInferenceEngine 直接读取并填充
//      IBatchInferenceEngine.ModelVersion / ContentHash / Kind。
//
// 设计边界：
//   - 推理路径仅暴露 InferBatchAsync(FeatureBatch)：FeatureBatch 是 R28-F 引入的
//     连续 float 内存表示；字典路径（FeatureVector）由 OnnxInferenceEngine 在
//     内部转换为 FeatureBatch 后调用本接口，避免在 session 层维护两条路径。
//   - 工厂方法接受 OnnxInferenceEngineOptions + ModelArtifactDescriptor：
//     前者负责张量映射与运行时参数，后者负责模型工件路径与元数据。
//     descriptor 为 null 时退化为不带元数据的会话（仅供本地测试使用）。
// ===========================================================================

/// <summary>
/// ONNX 推理会话抽象，隔离具体推理库。
/// </summary>
/// <remarks>
/// 与 <c>IOnnxEmbeddingSession</c> 模式对齐：单一会话封装模型加载与批量推理，
/// 让 <see cref="OnnxInferenceEngine"/> 在创建时调用工厂、在推理时直接调用
/// <see cref="InferBatchAsync"/>，避免每次推理都重新加载模型。
/// </remarks>
public interface IOnnxInferenceSession : IAsyncDisposable
{
    /// <summary>模型工件 ID（来自 <see cref="ModelArtifactDescriptor.ModelArtifactId"/>）。</summary>
    string ModelArtifactId { get; }

    /// <summary>模型版本号（来自 <see cref="ModelArtifactDescriptor.ModelVersion"/>）。</summary>
    string ModelVersion { get; }

    /// <summary>
    /// 模型工件内容哈希（来自 <see cref="ModelArtifactDescriptor.ContentHash"/>）。
    /// 用于在 <see cref="IBatchInferenceEngine.ContentHash"/> 中暴露给上层。
    /// </summary>
    string ContentHash { get; }

    /// <summary>
    /// 模型的主输入张量是否接受 float 数据类型。
    /// <para>
    /// score 模型（float 特征输入）返回 true；embedding 模型（int64 input_ids）返回 false。
    /// Golden Probe 据此决定是否执行 float warmup batch 验证——非 float 输入模型跳过 float probe，
    /// 改为基本 warmup（触发 graph optimization），让激活成功，真实推理时再由 ONNX Runtime 优雅报告类型不匹配。
    /// </para>
    /// 默认实现返回 true（向后兼容：score 模型与 mock session）。
    /// </summary>
    bool SupportsFloatInput => true;

    /// <summary>
    /// 执行一批特征向量的推理。
    /// 直接消费 <see cref="FeatureBatch"/> 的连续 float 内存，避免装箱与字典查找。
    /// </summary>
    /// <param name="batch">批量特征数据（row-major 连续内存）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>批量推理结果（顺序与输入行一致）。</returns>
    ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 创建 ONNX 推理会话的工厂。
/// </summary>
/// <remarks>
/// 默认实现 <see cref="OnnxRuntimeInferenceSessionFactory"/> 使用
/// <c>Microsoft.ML.OnnxRuntime.InferenceSession</c> 加载本地 ONNX 模型。
/// 测试场景可注入自定义工厂返回 mock <see cref="IOnnxInferenceSession"/>。
/// </remarks>
public interface IOnnxInferenceSessionFactory
{
    /// <summary>
    /// 创建 ONNX 推理会话。
    /// </summary>
    /// <param name="options">运行时配置（张量映射、线程数、超时、激活函数）。</param>
    /// <param name="descriptor">
    /// 模型工件描述符（提供 ArtifactPath 与元数据）；为 null 时
    /// <see cref="OnnxRuntimeInferenceSessionFactory"/> 会从 <paramref name="options"/>
    /// 之外的备用路径加载模型，仅供本地测试使用。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已加载的 ONNX 推理会话。</returns>
    ValueTask<IOnnxInferenceSession> CreateAsync(
        OnnxInferenceEngineOptions options,
        ModelArtifactDescriptor? descriptor = null,
        CancellationToken cancellationToken = default);
}
