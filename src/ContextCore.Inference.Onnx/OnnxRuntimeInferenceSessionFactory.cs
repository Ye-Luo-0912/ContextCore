using System.Diagnostics;
using ContextCore.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// R29 WP-A-2：OnnxRuntime Inference Session Factory + Session 实现
//
// 目标：
//   1. OnnxRuntimeInferenceSessionFactory：使用 Microsoft.ML.OnnxRuntime.InferenceSession
//      加载本地 ONNX 模型文件，构造 OnnxRuntimeInferenceSession。
//   2. OnnxRuntimeInferenceSession：封装推理调用，把 FeatureBatch 的连续 float 内存
//      映射为 DenseTensor<float>，运行 session.Run，解析输出张量并按
//      OnnxInferenceEngineOptions 配置应用 sigmoid 与列选择。
//
// 设计原则：
//   1. 单一会话对应单个模型工件；多次推理复用同一会话，避免重复加载开销。
//   2. 推理调用同步执行（session.Run 是同步 API），但通过 ValueTask 异步签名
//      与上游 IBatchInferenceEngine 对齐；高并发场景由调用方控制并发度。
//   3. 不在本层做 timeout 取消：OnnxRuntime 的 Run 不支持 CancellationToken；
//      超时控制由 OnnxInferenceEngine 通过 CancellationTokenSource 实现。
//   4. 输出张量解析支持一维 [batch] 与二维 [batch, classes] 两种形态：
//      - 一维：Score 取第 0 列，Confidence = 1 - Score（二分类互补）
//      - 二维：Score 取 ScoreOutputIndex 列，Confidence 取 ConfidenceOutputIndex 列
//        或从独立 ConfidenceOutputName 输出张量读取。
// ===========================================================================

/// <summary>
/// R29 WP-A-2：基于 Microsoft.ML.OnnxRuntime 创建本地推理会话的工厂。
/// </summary>
/// <remarks>
/// 与 <c>OnnxRuntimeEmbeddingSessionFactory</c> 模式对齐：
/// 工厂仅负责加载模型与构造会话；推理调用由 <see cref="IOnnxInferenceSession"/> 承担。
/// </remarks>
public sealed class OnnxRuntimeInferenceSessionFactory : IOnnxInferenceSessionFactory
{
    /// <inheritdoc />
    public ValueTask<IOnnxInferenceSession> CreateAsync(
        OnnxInferenceEngineOptions options,
        ModelArtifactDescriptor? descriptor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var modelPath = ResolveModelPath(options, descriptor);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"未找到 ONNX 模型文件：{modelPath}。请确认 descriptor.ArtifactPath 或 options.ModelPath 指向有效 ONNX 文件。",
                modelPath);
        }

        var sessionOptions = CreateSessionOptions(options);
        var session = new InferenceSession(modelPath, sessionOptions);
        ValidateTensorNames(session, options);

        var modelArtifactId = descriptor?.ModelArtifactId ?? options.ModelArtifactId;
        var modelVersion = descriptor?.ModelVersion ?? options.ModelVersion;
        var contentHash = descriptor?.ContentHash ?? options.ContentHash;

        IOnnxInferenceSession result = new OnnxRuntimeInferenceSession(
            modelArtifactId,
            modelVersion,
            contentHash,
            options,
            session);
        return ValueTask.FromResult(result);
    }

    private static string ResolveModelPath(
        OnnxInferenceEngineOptions options,
        ModelArtifactDescriptor? descriptor)
    {
        if (descriptor?.ArtifactPath is { Length: > 0 } descriptorPath)
        {
            return descriptorPath;
        }

        if (!string.IsNullOrWhiteSpace(options.ModelPath))
        {
            return options.ModelPath!;
        }

        throw new InvalidOperationException(
            "无法解析 ONNX 模型路径：descriptor.ArtifactPath 与 options.ModelPath 均为空。" +
            "请提供 ModelArtifactDescriptor 或在 OnnxInferenceEngineOptions.ModelPath 中指定模型文件路径。");
    }

    private static SessionOptions CreateSessionOptions(OnnxInferenceEngineOptions options)
    {
        var sessionOptions = new SessionOptions
        {
            EnableMemoryPattern = options.EnableMemoryPattern
        };

        if (options.IntraOpNumThreads > 0)
        {
            sessionOptions.IntraOpNumThreads = options.IntraOpNumThreads;
        }

        if (options.InterOpNumThreads > 0)
        {
            sessionOptions.InterOpNumThreads = options.InterOpNumThreads;
        }

        return sessionOptions;
    }

    private static void ValidateTensorNames(InferenceSession session, OnnxInferenceEngineOptions options)
    {
        if (!session.InputMetadata.ContainsKey(options.InputTensorName))
        {
            throw new InvalidOperationException(
                $"ONNX 模型未包含名为 '{options.InputTensorName}' 的输入张量。" +
                $"可用输入：[{string.Join(", ", session.InputMetadata.Keys)}]。");
        }

        if (!session.OutputMetadata.ContainsKey(options.ScoreOutputName))
        {
            throw new InvalidOperationException(
                $"ONNX 模型未包含名为 '{options.ScoreOutputName}' 的输出张量。" +
                $"可用输出：[{string.Join(", ", session.OutputMetadata.Keys)}]。");
        }

        if (options.ConfidenceOutputName is { Length: > 0 } confidenceName
            && !session.OutputMetadata.ContainsKey(confidenceName))
        {
            throw new InvalidOperationException(
                $"ONNX 模型未包含名为 '{confidenceName}' 的 confidence 输出张量。" +
                $"可用输出：[{string.Join(", ", session.OutputMetadata.Keys)}]。");
        }
    }
}

/// <summary>
/// R29 WP-A-2：封装 ONNX Runtime 推理调用的会话实现。
/// </summary>
/// <remarks>
/// 不可变：构造后 <see cref="Options"/> 与底层 <see cref="InferenceSession"/> 不再变更。
/// 线程安全：<see cref="InferenceSession.Run"/> 内部线程安全，可被多线程并发调用。
/// </remarks>
internal sealed class OnnxRuntimeInferenceSession : IOnnxInferenceSession
{
    private readonly InferenceSession _session;
    private readonly OnnxInferenceEngineOptions _options;

    public OnnxRuntimeInferenceSession(
        string modelArtifactId,
        string modelVersion,
        string contentHash,
        OnnxInferenceEngineOptions options,
        InferenceSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelArtifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(session);

        ModelArtifactId = modelArtifactId;
        ModelVersion = modelVersion;
        ContentHash = contentHash;
        _options = options;
        _session = session;
    }

    public string ModelArtifactId { get; }

    public string ModelVersion { get; }

    public string ContentHash { get; }

    /// <inheritdoc />
    public ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var startedAt = Stopwatch.GetTimestamp();
        if (cancellationToken.IsCancellationRequested)
        {
            return new ValueTask<BatchInferenceResult>(BuildCancelledResult(startedAt));
        }

        if (batch.Values.Length != batch.RowCount * batch.FeatureCount)
        {
            return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = $"FeatureBatch.Values.Length({batch.Values.Length}) != RowCount({batch.RowCount}) * FeatureCount({batch.FeatureCount})",
                Duration = Stopwatch.GetElapsedTime(startedAt)
            });
        }

        if (batch.RowCount == 0)
        {
            return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = true,
                Error = null,
                Duration = Stopwatch.GetElapsedTime(startedAt)
            });
        }

        try
        {
            var inputs = CreateInputs(batch);
            using var results = _session.Run(inputs);
            var outputs = ParseOutputs(results);
            return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
            {
                Outputs = outputs,
                Succeeded = true,
                Error = null,
                Duration = Stopwatch.GetElapsedTime(startedAt)
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = $"ONNX 推理失败：{ex.GetType().Name}: {ex.Message}",
                Duration = Stopwatch.GetElapsedTime(startedAt)
            });
        }
    }

    public ValueTask DisposeAsync()
    {
        _session.Dispose();
        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<NamedOnnxValue> CreateInputs(FeatureBatch batch)
    {
        // DenseTensor<float> 构造需要 Memory<float>，而 FeatureBatch.Values 是 ReadOnlyMemory<float>。
        // 这里通过 ToArray() 复制一份连续内存，避免修改上游不可变 buffer。
        // 高频场景下应由调用方直接使用 InferBatchAsync(FeatureBatch) 路径。
        var dimensions = new[] { batch.RowCount, batch.FeatureCount };
        var buffer = batch.Values.ToArray();
        var tensor = new DenseTensor<float>(buffer, dimensions, false);
        return new[]
        {
            NamedOnnxValue.CreateFromTensor(_options.InputTensorName, tensor)
        };
    }

    private IReadOnlyList<InferenceOutput> ParseOutputs(IReadOnlyList<DisposableNamedOnnxValue> results)
    {
        var scoreTensor = ExtractTensor(results, _options.ScoreOutputName);
        if (scoreTensor is null)
        {
            throw new InvalidOperationException(
                $"ONNX 输出未找到名为 '{_options.ScoreOutputName}' 的张量。" +
                $"可用输出：[{string.Join(", ", results.Select(r => r.Name))}]。");
        }

        var confidenceTensor = _options.ConfidenceOutputName is { Length: > 0 } confidenceName
            ? ExtractTensor(results, confidenceName) ?? scoreTensor
            : scoreTensor;

        var scoreDims = scoreTensor.Dimensions.ToArray();
        var confidenceDims = confidenceTensor.Dimensions.ToArray();
        var batchSize = scoreDims.Length > 0 ? scoreDims[0] : 0;

        var outputs = new InferenceOutput[batchSize];
        var scoreValues = scoreTensor.ToArray();
        var confidenceValues = ReferenceEquals(scoreTensor, confidenceTensor)
            ? scoreValues
            : confidenceTensor.ToArray();

        for (var row = 0; row < batchSize; row++)
        {
            var rawScore = ReadValue(scoreValues, scoreDims, row, _options.ScoreOutputIndex);
            var rawConfidence = ReadValue(confidenceValues, confidenceDims, row, _options.ConfidenceOutputIndex);

            var score = _options.ApplySigmoid ? Sigmoid(rawScore) : rawScore;
            var confidence = _options.ApplySigmoidToConfidence ? Sigmoid(rawConfidence) : rawConfidence;

            outputs[row] = new InferenceOutput
            {
                Score = score,
                Confidence = confidence,
                PerClassScores = null
            };
        }

        return outputs;
    }

    private static Tensor<float>? ExtractTensor(IReadOnlyList<DisposableNamedOnnxValue> results, string name)
    {
        var value = results.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
        return value?.AsTensor<float>();
    }

    private static float ReadValue(float[] values, int[] dims, int row, int columnIndex)
    {
        if (dims.Length == 1)
        {
            // 一维输出 [batch]：每行单值，忽略 columnIndex
            return values[row];
        }

        if (dims.Length == 2)
        {
            // 二维输出 [batch, classes]：取指定列
            var classes = dims[1];
            var clamped = Math.Clamp(columnIndex, 0, classes - 1);
            return values[row * classes + clamped];
        }

        // 其他维度（如 [batch, seq, classes]）：仅支持第 0 列以避免歧义
        var stride = 1;
        for (var i = 1; i < dims.Length; i++)
        {
            stride *= dims[i];
        }
        return values[row * stride];
    }

    private static double Sigmoid(float raw)
    {
        var x = (double)raw;
        if (double.IsNaN(x) || double.IsInfinity(x))
        {
            return x;
        }
        return 1.0 / (1.0 + Math.Exp(-x));
    }

    private static BatchInferenceResult BuildCancelledResult(long startedAt)
    {
        return new BatchInferenceResult
        {
            Outputs = Array.Empty<InferenceOutput>(),
            Succeeded = false,
            Error = "推理被取消。",
            Duration = Stopwatch.GetElapsedTime(startedAt)
        };
    }
}
