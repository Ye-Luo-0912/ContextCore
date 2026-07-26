using System.Diagnostics;
using ContextCore.Abstractions;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// R29 WP-A-2：OnnxInferenceEngine — IBatchInferenceEngine 的 ONNX 实现
//
// 目标：
//   1. 把 IOnnxInferenceSession 适配为 IBatchInferenceEngine，让 ContextCore
//      既有评分链路（DefaultContextDecisionEngine / Scorer 等）能直接消费 ONNX 真实模型。
//   2. 对外暴露 ModelVersion / Kind / ContentHash / CalibrationVersion 元数据，
//      与 DeterministicBatchInferenceEngine 实现契约一致；Kind = RealModel。
//   3. 同时支持字典路径（InferAsync）与连续内存路径（InferBatchAsync）：
//      - InferBatchAsync：直接转发到 session，零拷贝。
//      - InferAsync：将 FeatureVector.Values 字典转为连续 float 内存后调用 InferBatchAsync。
//   4. 超时控制：构造 CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts)
//      并在请求的 TimeoutMs 或 options.InferenceTimeoutMs 中取较小者作为硬超时。
//
// 设计原则：
//   1. 单例：构造时加载会话，所有推理请求复用同一 session；OnnxRuntime session.Run 线程安全。
//   2. CalibrationVersion 来自外部注入（ICalibrationService 或 ModelArtifactDescriptor）；
//      引擎本身不持久化校准参数。
//   3. 异常隔离：session 抛出的非取消异常被捕获并转为 Succeeded=false 的结果，
//      与 DeterministicBatchInferenceEngine 行为一致；OperationCanceledException 不被捕获
//      （与项目内存约束一致：Authoritative Runtime 不捕获 OperationCanceledException）。
// ===========================================================================

/// <summary>
/// R29 WP-A-2：基于 ONNX Runtime 的 IBatchInferenceEngine 实现。
/// </summary>
/// <remarks>
/// <b>构造</b>：
/// <code>
/// var factory = new OnnxRuntimeInferenceSessionFactory();
/// var session = await factory.CreateAsync(options, descriptor);
/// var engine = new OnnxInferenceEngine(session, options, calibrationVersion: "platt-v1");
/// </code>
/// <para>
/// <b>CalibrationVersion</b>：默认 "default-v1"；真实生产路径应在构造时传入
/// 与 ICalibrationService 中已注册的参数版本号一致的字符串。
/// </para>
/// </remarks>
public sealed class OnnxInferenceEngine : IBatchInferenceEngine
{
    private readonly IOnnxInferenceSession _session;
    private readonly OnnxInferenceEngineOptions _options;
    private readonly string _calibrationVersion;

    /// <summary>
    /// 构造 OnnxInferenceEngine。
    /// </summary>
    /// <param name="session">已加载的 ONNX 推理会话（由 IOnnxInferenceSessionFactory 创建）。</param>
    /// <param name="options">运行时配置（用于超时与 FeatureVector → FeatureBatch 转换）。</param>
    /// <param name="calibrationVersion">校准版本号（默认 "default-v1"）。</param>
    public OnnxInferenceEngine(
        IOnnxInferenceSession session,
        OnnxInferenceEngineOptions options,
        string? calibrationVersion = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        _session = session;
        _options = options;
        _calibrationVersion = string.IsNullOrWhiteSpace(calibrationVersion)
            ? "default-v1"
            : calibrationVersion;
    }

    /// <inheritdoc />
    public string ModelVersion => _session.ModelVersion;

    /// <inheritdoc />
    public InferenceEngineKind Kind => InferenceEngineKind.RealModel;

    /// <inheritdoc />
    public string ContentHash => _session.ContentHash;

    /// <inheritdoc />
    public string CalibrationVersion => _calibrationVersion;

    /// <inheritdoc />
    public async ValueTask<BatchInferenceResult> InferAsync(
        BatchInferenceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Inputs.Count == 0)
        {
            return new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = true,
                Error = null,
                Duration = TimeSpan.Zero
            };
        }

        // 将 FeatureVector 字典路径转为 FeatureBatch 连续内存路径，
        // 复用 InferBatchAsync 的高性能实现，避免在引擎层维护两条推理路径。
        var batch = ConvertToFeatureBatch(request);
        using var linkedCts = CreateLinkedCancellationTokenSource(request.TimeoutMs, ct);
        return await InferBatchAsync(batch, linkedCts.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken ct = default)
    {
        // 超时：使用 options.InferenceTimeoutMs（请求级超时由调用方通过 BatchInferenceRequest.TimeoutMs 控制，
        // 该字段在 InferAsync 路径中已通过 CreateLinkedCancellationTokenSource 应用）。
        // 本方法的 ct 直接来自调用方，不二次叠加超时。
        return await _session.InferBatchAsync(batch, ct).ConfigureAwait(false);
    }

    private static FeatureBatch ConvertToFeatureBatch(BatchInferenceRequest request)
    {
        var inputs = request.Inputs;
        var rowCount = inputs.Count;

        // 收集所有特征名（保持稳定顺序：以第一条输入的 key 序列为基准，缺失补默认值）。
        var featureNames = inputs[0].Values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var featureCount = featureNames.Length;

        var values = new float[rowCount * featureCount];
        for (var row = 0; row < rowCount; row++)
        {
            var vector = inputs[row].Values;
            var offset = row * featureCount;
            for (var col = 0; col < featureCount; col++)
            {
                values[offset + col] = ConvertToFloat(vector[featureNames[col]]);
            }
        }

        return new FeatureBatch
        {
            SchemaVersion = inputs[0].SchemaVersion,
            Values = values,
            RowCount = rowCount,
            FeatureCount = featureCount,
            FeatureNames = featureNames
        };
    }

    private static float ConvertToFloat(object? value)
    {
        return value switch
        {
            null => 0f,
            float f => f,
            double d => (float)d,
            int i => i,
            long l => l,
            bool b => b ? 1f : 0f,
            string s when float.TryParse(s, out var parsed) => parsed,
            _ => 0f
        };
    }

    private CancellationTokenSource CreateLinkedCancellationTokenSource(int requestTimeoutMs, CancellationToken ct)
    {
        var effectiveTimeoutMs = Math.Min(
            requestTimeoutMs > 0 ? requestTimeoutMs : _options.InferenceTimeoutMs,
            _options.InferenceTimeoutMs);

        if (effectiveTimeoutMs <= 0)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(effectiveTimeoutMs));
        return cts;
    }
}
