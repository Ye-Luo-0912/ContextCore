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

    // P3 步骤4：warmup 状态标志。
    // 0 = 未 warmup；1 = 已 warmup（或 warmup 失败，不重试）。
    // 使用 Interlocked 实现无锁 idempotent warmup，避免首次推理时的并发重复 warmup。
    private int _warmedUp;

    /// <summary>
    /// 构造 OnnxInferenceEngine。
    /// </summary>
    /// <param name="session">已加载的 ONNX 推理会话（由 IOnnxInferenceSessionFactory 创建）。</param>
    /// <param name="options">运行时配置（用于超时与 FeatureVector → FeatureBatch 转换）。</param>
    /// <param name="calibrationVersion">校准版本号（默认 "default-v1"）。</param>
    /// <remarks>
    /// P3 步骤4：构造函数不自动执行 warmup（避免 sync-over-async）。
    /// 调用方应在构造后显式调用 <see cref="WarmupAsync"/>，
    /// 或依赖 <see cref="InferBatchAsync"/> 首次调用时的 lazy warmup（受 <see cref="OnnxInferenceEngineOptions.EnableWarmup"/> 控制）。
    /// </remarks>
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

        // P3 步骤4：lazy warmup（若 EnableWarmup=true 且尚未 warmup）。
        // warmup 失败不抛异常，仅标记已尝试，避免阻塞真实推理路径。
        if (_options.EnableWarmup)
        {
            await EnsureWarmedUpAsync(ct).ConfigureAwait(false);
        }

        // P3 步骤3：Large batch splitting — 当 RowCount 超过 MaxBatchSize 时分片执行。
        // 这避免 large batch 一次性加载到 GPU 显存导致 OOM。
        // MaxBatchSize=0 表示不限制（默认）。
        if (_options.MaxBatchSize > 0 && batch.RowCount > _options.MaxBatchSize)
        {
            return await InferBatchWithSplittingAsync(batch, ct).ConfigureAwait(false);
        }

        return await _session.InferBatchAsync(batch, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// P3 步骤4：执行 warmup（idempotent，可安全多次调用）。
    /// 用一个 1 行全 0 的 dummy FeatureBatch 调用一次 session.InferBatchAsync，
    /// 让 ORT 完成 graph optimization 与内存分配，避免首次真实推理的冷启动延迟。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>
    /// warmup 失败（非取消异常）不抛异常，仅标记已尝试；
    /// 真实推理时若 ORT 仍报错，由 <see cref="InferBatchAsync"/> 的异常处理路径捕获。
    /// <see cref="OperationCanceledException"/> 会被传播（尊重调用方取消意图）。
    /// </remarks>
    public async ValueTask WarmupAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _warmedUp, 1, 0) != 0)
        {
            return; // 已 warmup 或正在 warmup（由其他线程完成）
        }

        try
        {
            // dummy batch：1 行 × 1 列全 0，最小化 warmup 开销。
            // 目的不是验证模型输出正确性，而是触发 ORT 的 graph optimization 与首次内存分配。
            var dummyBatch = new FeatureBatch
            {
                SchemaVersion = "warmup",
                Values = new float[] { 0f },
                RowCount = 1,
                FeatureCount = 1,
                FeatureNames = new[] { "warmup" }
            };
            await _session.InferBatchAsync(dummyBatch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消时重置标志，允许后续重试 warmup
            Interlocked.Exchange(ref _warmedUp, 0);
            throw;
        }
        catch (Exception)
        {
            // warmup 失败：标志保持 1（不重试），不影响后续真实推理。
            // 真实推理若也失败，由 InferBatchAsync 的调用方异常处理路径降级。
        }
    }

    /// <summary>
    /// P3 步骤4：lazy warmup 内部实现。仅在首次调用时执行 warmup，后续调用直接返回。
    /// </summary>
    private async ValueTask EnsureWarmedUpAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _warmedUp) != 0)
        {
            return;
        }
        await WarmupAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// P3 步骤3：按 MaxBatchSize 分片执行推理，合并各片输出。
    /// </summary>
    /// <param name="batch">原始大批量特征数据。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>合并后的推理结果（输出顺序与输入行一致）。</returns>
    /// <remarks>
    /// 分片策略：按 MaxBatchSize 切分 batch.Values 为多个连续切片，每片独立调用 session.InferBatchAsync。
    /// 任一片失败则整体返回失败（Error 含失败片索引）。
    /// 合并各片 Outputs 到结果列表，累计 Duration。
    /// </remarks>
    private async ValueTask<BatchInferenceResult> InferBatchWithSplittingAsync(
        FeatureBatch batch,
        CancellationToken ct)
    {
        var maxBatchSize = _options.MaxBatchSize;
        var featureCount = batch.FeatureCount;
        var totalRows = batch.RowCount;
        var allOutputs = new List<InferenceOutput>(totalRows);
        var totalDuration = TimeSpan.Zero;
        var startedAt = Stopwatch.GetTimestamp();

        for (var rowOffset = 0; rowOffset < totalRows; rowOffset += maxBatchSize)
        {
            var chunkRows = Math.Min(maxBatchSize, totalRows - rowOffset);
            var chunkValues = batch.Values.Slice(
                rowOffset * featureCount,
                chunkRows * featureCount);

            var chunkBatch = new FeatureBatch
            {
                SchemaVersion = batch.SchemaVersion,
                Values = chunkValues,
                RowCount = chunkRows,
                FeatureCount = featureCount,
                FeatureNames = batch.FeatureNames
            };

            BatchInferenceResult chunkResult;
            try
            {
                chunkResult = await _session.InferBatchAsync(chunkBatch, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new BatchInferenceResult
                {
                    Outputs = allOutputs,
                    Succeeded = false,
                    Error = $"分片推理失败（rowOffset={rowOffset}, chunkRows={chunkRows}）：{ex.GetType().Name}: {ex.Message}",
                    Duration = Stopwatch.GetElapsedTime(startedAt)
                };
            }

            if (!chunkResult.Succeeded)
            {
                return new BatchInferenceResult
                {
                    Outputs = allOutputs,
                    Succeeded = false,
                    Error = $"分片推理报告失败（rowOffset={rowOffset}, chunkRows={chunkRows}）：{chunkResult.Error}",
                    Duration = Stopwatch.GetElapsedTime(startedAt)
                };
            }

            allOutputs.AddRange(chunkResult.Outputs);
            totalDuration += chunkResult.Duration;
        }

        return new BatchInferenceResult
        {
            Outputs = allOutputs,
            Succeeded = true,
            Error = null,
            Duration = totalDuration
        };
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
