using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
//      - 一维：Score 取第 0 列，Confidence = 1.0（默认高置信，子问题5修正：
//        模型只输出单个分数时无独立 confidence 信号，不再用 1-Score 互补猜测）
//      - 二维：Score 取 ScoreOutputIndex 列，Confidence 取 ConfidenceOutputIndex 列
//        或从独立 ConfidenceOutputName 输出张量读取（classes≥2 时才有效）。
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
    public async ValueTask<IOnnxInferenceSession> CreateAsync(
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

        // 子问题1：流式计算磁盘模型文件的真实 SHA-256，与 descriptor.ContentHash 精确比较。
        // 不匹配时抛 InvalidOperationException（ModelFileHashMismatch），不创建 Session。
        // 即使 descriptor.ContentHash 为空/unspecified，也填充计算出的实际哈希到 Session 元数据。
        var actualHashHex = await ComputeFileSha256HexAsync(modelPath, cancellationToken).ConfigureAwait(false);
        var expectedHashHex = NormalizeHashHex(descriptor?.ContentHash ?? options.ContentHash);
        if (!string.IsNullOrEmpty(expectedHashHex)
            && !string.Equals(expectedHashHex, "unspecified", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(expectedHashHex, actualHashHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ModelFileHashMismatch：模型文件 '{modelPath}' 的实际 SHA-256 与 descriptor.ContentHash 不一致。" +
                $"Expected={expectedHashHex}, Actual={actualHashHex}。" +
                "可能原因：文件被篡改、传输损坏或 descriptor 引用了错误版本。");
        }

        var sessionOptions = CreateSessionOptions(options);
        var session = new InferenceSession(modelPath, sessionOptions);
        ValidateTensorNames(session, options);

        var modelArtifactId = descriptor?.ModelArtifactId ?? options.ModelArtifactId;
        var modelVersion = descriptor?.ModelVersion ?? options.ModelVersion;
        // 使用计算出的实际哈希（带 sha256: 前缀，与 descriptor.ContentHash 格式一致）
        var contentHash = $"sha256:{actualHashHex}";

        IOnnxInferenceSession result = new OnnxRuntimeInferenceSession(
            modelArtifactId,
            modelVersion,
            contentHash,
            options,
            session);
        return result;
    }

    /// <summary>
    /// 子问题1：流式读取文件并计算 SHA-256，返回小写 hex 字符串（不带前缀）。
    /// 使用 useAsync=true 的 FileStream 避免 sync-over-async 阻塞线程池。
    /// </summary>
    private static async ValueTask<string> ComputeFileSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// 子问题1：规范化哈希字符串，去除 "sha256:" 等算法前缀并转小写。
    /// 空或空白时返回空字符串（表示无期望哈希，跳过比较）。
    /// </summary>
    private static string NormalizeHashHex(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return string.Empty;
        }

        var trimmed = hash.Trim();
        var colonIndex = trimmed.IndexOf(':');
        if (colonIndex >= 0 && colonIndex < trimmed.Length - 1)
        {
            trimmed = trimmed[(colonIndex + 1)..];
        }

        return trimmed.ToLowerInvariant();
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

        // Execution Provider 配置：CPU 为默认（不附加 EP，由 ORT 内置 CPU 算子承担）。
        // CUDA / TensorRT 需要相应的 native 包；缺失时抛 OnnxRuntimeException，
        // 由上层 ModelActivationManager 捕获并转为激活失败（fail-safe）。
        AppendExecutionProvider(sessionOptions, options);

        return sessionOptions;
    }

    /// <summary>
    /// 根据 <see cref="OnnxInferenceEngineOptions.ExecutionProvider"/> 附加 EP。
    /// CPU 时不附加（ORT 默认即为 CPU）；CUDA/TensorRT 调用对应的 AppendExecutionProvider_*。
    /// </summary>
    /// <remarks>
    /// GPU 包未安装时 <c>AppendExecutionProvider_CUDA</c> / <c>AppendExecutionProvider_Tensorrt</c>
    /// 会抛 <see cref="Microsoft.ML.OnnxRuntime.OnnxRuntimeException"/>。本方法捕获并重新抛出
    /// 带更清晰提示的 <see cref="InvalidOperationException"/>，让调用方明确"需要安装 GPU 包"。
    /// </remarks>
    private static void AppendExecutionProvider(SessionOptions sessionOptions, OnnxInferenceEngineOptions options)
    {
        switch (options.ExecutionProvider)
        {
            case OnnxExecutionProvider.CPU:
                // 默认 EP，无需附加。ORT 在未指定 EP 时使用 CPU。
                return;

            case OnnxExecutionProvider.CUDA:
                try
                {
                    sessionOptions.AppendExecutionProvider_CUDA(options.ExecutionProviderDeviceId);
                }
                catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex)
                {
                    throw new InvalidOperationException(
                        $"AppendExecutionProvider_CUDA(deviceId={options.ExecutionProviderDeviceId}) 失败：" +
                        $"{ex.Message}。请确认已安装 Microsoft.ML.OnnxRuntime.Gpu NuGet 包，" +
                        "且目标机器具备 NVIDIA GPU 驱动与匹配的 CUDA 运行时。", ex);
                }
                return;

            case OnnxExecutionProvider.TensorRT:
                try
                {
                    sessionOptions.AppendExecutionProvider_Tensorrt(options.ExecutionProviderDeviceId);
                }
                catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex)
                {
                    throw new InvalidOperationException(
                        $"AppendExecutionProvider_Tensorrt(deviceId={options.ExecutionProviderDeviceId}) 失败：" +
                        $"{ex.Message}。请确认已安装 Microsoft.ML.OnnxRuntime.Gpu NuGet 包，" +
                        "且目标机器具备 NVIDIA GPU 驱动、CUDA 运行时与 TensorRT 库。", ex);
                }
                return;

            default:
                // 防御性：未知 EP 回退到 CPU（与默认行为一致）。
                return;
        }
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
            var inputs = CreateInputs(batch, out var rentedBuffer);
            try
            {
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
            finally
            {
                // P3 步骤5：ArrayPool buffer 在 session.Run 完成后归还。
                // ORT session.Run 同步执行，返回时输入张量数据已被复制到 ORT 内部内存，
                // 此后 DenseTensor 的 backing buffer 不再被引用，可安全归还。
                // 零拷贝路径 rentedBuffer=null，跳过归还。
                if (rentedBuffer is not null)
                {
                    ArrayPool<float>.Shared.Return(rentedBuffer);
                }
            }
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

    /// <summary>
    /// 构造 ONNX 输入张量。
    /// P3 步骤2：零拷贝路径 — 当 batch.Values 由完整 float[] 支撑时（生产 Scorer 的 TryBuildFeatureBatch 路径），
    /// 通过 MemoryMarshal.TryGetArray 直接复用底层数组，避免 ToArray 拷贝。
    /// P3 步骤5：ArrayPool 回退路径 — 当零拷贝不可行时（如 batch.Values 是切片或非数组 backing），
    /// 从 ArrayPool.Rent 租借缓冲区并拷贝，session.Run 完成后由调用方归还。
    /// </summary>
    /// <param name="batch">批量特征数据。</param>
    /// <param name="rentedBuffer">若使用 ArrayPool 路径，返回租借的 buffer 供调用方归还；零拷贝路径返回 null。</param>
    /// <returns>ONNX 输入 NamedOnnxValue 列表。</returns>
    private IReadOnlyList<NamedOnnxValue> CreateInputs(FeatureBatch batch, out float[]? rentedBuffer)
    {
        rentedBuffer = null;
        var dimensions = new[] { batch.RowCount, batch.FeatureCount };
        var requiredLength = batch.RowCount * batch.FeatureCount;

        // P3 步骤2：零拷贝路径 — 尝试获取 batch.Values 的底层 array。
        // 当 batch.Values 由完整 float[] 直接构造（offset=0 + count=full）时，
        // 直接复用底层数组，避免 ToArray 拷贝。这是生产 Scorer → ONNX 推理的 hot path。
        if (MemoryMarshal.TryGetArray(batch.Values, out var arraySegment)
            && arraySegment.Array is { } underlyingArray
            && arraySegment.Offset == 0
            && arraySegment.Count == underlyingArray.Length
            && underlyingArray.Length == requiredLength)
        {
            // 零拷贝：直接复用底层 float[]，DenseTensor 引用同一数组。
            // 注意：batch.Values 是 ReadOnlyMemory<float>，ORT input tensor 不会被 ORT 修改，
            // 因此共享只读 backing 是安全的。
            var tensor = new DenseTensor<float>(underlyingArray, dimensions, false);
            return new[]
            {
                NamedOnnxValue.CreateFromTensor(_options.InputTensorName, tensor)
            };
        }

        // P3 步骤5：ArrayPool 回退路径 — 租借缓冲区 + 拷贝，避免每次 ToArray 造成的 GC 压力。
        // 注意：Rent 返回的 buffer.Length >= requiredLength，可能更大；
        // 通过 AsMemory(0, requiredLength) 切片传给 DenseTensor，仅暴露所需范围。
        var buffer = ArrayPool<float>.Shared.Rent(requiredLength);
        try
        {
            batch.Values.Span.CopyTo(buffer.AsSpan(0, requiredLength));
            rentedBuffer = buffer;
            var tensor = new DenseTensor<float>(buffer.AsMemory(0, requiredLength), dimensions, false);
            return new[]
            {
                NamedOnnxValue.CreateFromTensor(_options.InputTensorName, tensor)
            };
        }
        catch
        {
            // 构造失败时立即归还，避免泄漏
            ArrayPool<float>.Shared.Return(buffer);
            throw;
        }
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

        // G7 输出零拷贝：
        //   - 热路径（ORT 始终返回 DenseTensor<float>）：直接用 Buffer.Span 访问底层内存，零分配。
        //   - 回退路径（理论非 DenseTensor，ORT 不会触发）：用 ArrayPool 租借 buffer 并 CopyTo，
        //     避免每批 ToArray 产生堆分配。buffer 在 finally 中归还。
        // Span 的生命周期由 using var results 限定（DisposableNamedOnnxValue Dispose 后失效）。
        var scoreRented = TryGetBuffer(scoreTensor, out var scoreMemory)
            ? null
            : RentAndCopyTensor(scoreTensor, out scoreMemory);
        Memory<float> confidenceMemory;
        float[]? confidenceRented;
        if (ReferenceEquals(scoreTensor, confidenceTensor))
        {
            // 共享 scoreTensor 的同一份内存，无需重复租借。
            confidenceMemory = scoreMemory;
            confidenceRented = null;
        }
        else if (TryGetBuffer(confidenceTensor, out confidenceMemory))
        {
            confidenceRented = null;
        }
        else
        {
            confidenceRented = RentAndCopyTensor(confidenceTensor, out confidenceMemory);
        }

        try
        {
            var scoreSpan = (ReadOnlySpan<float>)scoreMemory.Span;
            var confidenceSpan = ReferenceEquals(scoreTensor, confidenceTensor)
                ? scoreSpan
                : confidenceMemory.Span;

            return BuildOutputs(scoreTensor, confidenceTensor, scoreSpan, confidenceSpan);
        }
        finally
        {
            if (scoreRented is not null)
            {
                ArrayPool<float>.Shared.Return(scoreRented);
            }
            if (confidenceRented is not null)
            {
                ArrayPool<float>.Shared.Return(confidenceRented);
            }
        }
    }

    /// <summary>
    /// 从张量读取 score/confidence 并构造 InferenceOutput 数组。
    /// 抽取自 ParseOutputs 以隔离 ArrayPool 租借/归还与结果构造逻辑。
    /// </summary>
    private IReadOnlyList<InferenceOutput> BuildOutputs(
        Tensor<float> scoreTensor,
        Tensor<float> confidenceTensor,
        ReadOnlySpan<float> scoreSpan,
        ReadOnlySpan<float> confidenceSpan)
    {
        // Dimensions 已是 ReadOnlySpan<int>，直接消费，避免 ToArray 分配。
        var scoreDims = scoreTensor.Dimensions;
        var confidenceDims = confidenceTensor.Dimensions;
        var batchSize = scoreDims.Length > 0 ? scoreDims[0] : 0;

        // 子问题5：Confidence 语义修正。
        // 当 Score 与 Confidence 来自同一张量（confidenceTensor == scoreTensor）且该张量为 1D [batch] 时，
        // 每行只有 1 个元素，Score 与 Confidence 会从同一元素读取，导致 Confidence == Score（而非互补概率）。
        // 修复：1D 同张量场景下，Confidence 不再从同一元素读取，而是使用默认值 1.0（高置信）。
        //  - 1.0 表示"模型只输出了 score，没有独立的 confidence 信号，假定高置信"。
        //  - 这避免下游 Scorer 因 Confidence == Score 而误判置信度（如 Score=0.9 时 Confidence=0.9 看似合理，
        //    但 Score=0.1 时 Confidence=0.1 会触发低置信回退，而模型实际上没有提供 confidence 信息）。
        // 当 confidenceTensor != scoreTensor（独立 confidence 输出张量）或张量为 2D [batch, classes≥2] 时，
        // 仍按原逻辑从指定列读取 confidence。
        var sameTensor1D = ReferenceEquals(scoreTensor, confidenceTensor) && scoreDims.Length == 1;
        // 2D 张量但 classes=1 时也属于"每行单元素"，同样使用默认 confidence。
        var sameTensor2DSingleClass = ReferenceEquals(scoreTensor, confidenceTensor)
            && scoreDims.Length == 2
            && scoreDims[1] <= 1;

        var outputs = new InferenceOutput[batchSize];
        for (var row = 0; row < batchSize; row++)
        {
            var rawScore = ReadValue(scoreSpan, scoreDims, row, _options.ScoreOutputIndex);

            // 子问题5：1D 同张量或 2D 单列场景下，Confidence 使用默认值 1.0（不再读同一元素）。
            double confidence;
            if (sameTensor1D || sameTensor2DSingleClass)
            {
                confidence = 1.0;
            }
            else
            {
                var rawConfidence = ReadValue(confidenceSpan, confidenceDims, row, _options.ConfidenceOutputIndex);
                confidence = _options.ApplySigmoidToConfidence ? Sigmoid(rawConfidence) : rawConfidence;
            }

            var score = _options.ApplySigmoid ? Sigmoid(rawScore) : rawScore;

            outputs[row] = new InferenceOutput
            {
                Score = score,
                Confidence = confidence,
                PerClassScores = null
            };
        }

        return outputs;
    }

    /// <summary>
    /// G7 输出零拷贝：尝试从 Tensor 获取 DenseTensor.Buffer（Memory<float>）。
    /// ORT 输出始终为 DenseTensor<float>；若非 DenseTensor 返回 false（调用方走 ArrayPool 回退路径）。
    /// </summary>
    private static bool TryGetBuffer(Tensor<float> tensor, out Memory<float> buffer)
    {
        if (tensor is DenseTensor<float> dense)
        {
            buffer = dense.Buffer;
            return true;
        }
        buffer = default;
        return false;
    }

    /// <summary>
    /// G7 输出 ArrayPool 回退：为非 DenseTensor 的 Tensor&lt;float&gt; 租借 buffer 并拷贝数据。
    /// 这是 ORT 输出的防御性回退路径（ORT 始终返回 DenseTensor，理论不触发）。
    /// 用 ArrayPool 复用替代每次 ToArray 的堆分配，避免 GC 压力。
    /// </summary>
    /// <param name="tensor">待读取的张量。</param>
    /// <param name="memory">输出 Memory&lt;float&gt;（长度 = tensor.Length）。</param>
    /// <returns>租借的 buffer，调用方需在 finally 中通过 ArrayPool.Return 归还；tensor.Length=0 时返回 null。</returns>
    private static float[]? RentAndCopyTensor(Tensor<float> tensor, out Memory<float> memory)
    {
        var length = (int)tensor.Length;
        if (length <= 0)
        {
            memory = Memory<float>.Empty;
            return null;
        }

        var buffer = ArrayPool<float>.Shared.Rent(length);
        try
        {
            // Tensor<T> 实现 ICollection<T>，通过 CopyTo(T[], int) 把所有元素拷贝到数组。
            // buffer.Length >= length，从 index 0 开始拷贝 length 个元素，超出部分被忽略。
            ((ICollection<float>)tensor).CopyTo(buffer, 0);
            memory = buffer.AsMemory(0, length);
            return buffer;
        }
        catch
        {
            // CopyTo 失败（理论上不会发生）：归还 buffer 并重新抛出，让上层异常路径处理。
            ArrayPool<float>.Shared.Return(buffer);
            throw;
        }
    }

    private static Tensor<float>? ExtractTensor(IReadOnlyList<DisposableNamedOnnxValue> results, string name)
    {
        var value = results.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
        return value?.AsTensor<float>();
    }

    private static float ReadValue(ReadOnlySpan<float> values, ReadOnlySpan<int> dims, int row, int columnIndex)
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
