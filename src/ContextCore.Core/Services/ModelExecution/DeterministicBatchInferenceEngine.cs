using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.ModelExecution;

// ===========================================================================
// / Deterministic Batch Inference Engine
//
// 目标：
//   提供 IBatchInferenceEngine 的 fallback 实现，不调用真实模型，
//   仅基于 feature hash 产出确定性分数，用于：
//     1. 真实模型不可用时的降级路径（fail-safe）
//     2. 基础设施测试与本地预览（无需 GPU / 远程服务）
//     3. 单元测试中验证调用契约（输入顺序、超时、取消）
//
// 优化：
//   - 新增 InferBatchAsync(FeatureBatch, ct)：直接消费连续 float 内存，避免装箱。
//   - 新增 ContentHash / CalibrationVersion 属性（接口要求）。
//   - ComputeFeatureHash 优化：消除 StringBuilder + string[] + 排序 + UTF-8 byte[] 分配，
//     直接对 SchemaVersion UTF-8 字节 + float 缓冲字节做 FNV-1a 64-bit。
//     - FeatureBatch 路径：bytes 已是连续内存，0 分配 hash。
//     - FeatureVector 路径（向后兼容）：仍需排序 key，但不再构建中间字符串。
//   - 字典遍历顺序无关性由排序保证（FeatureVector 路径）或固定列顺序保证（FeatureBatch 路径）。
//
// 设计原则：
//   1. 确定性：相同输入（schema version + values 字典或 batch bytes）必须产出相同分数；
//      不依赖时间戳、随机数、环境变量等不稳定因素。
//   2. Fallback 友好：单条输入解析失败时返回该条 fallback 输出，不抛异常；
//      整批失败（如 ct 取消）时返回 Succeeded=false 的结果。
//   3. Score 范围 [-1, 1]，Confidence 范围 [0, 1] —— 由 Calibrate 之后映射到 [0, 1]。
//   4. 不引入 I/O：纯内存计算，适合 Singleton 生命周期。
// ===========================================================================

/// <summary>
/// / 确定性批量推理引擎（fallback 实现）。
/// </summary>
/// <remarks>
/// 相同输入始终产出相同分数；不调用真实模型，适合作为 fallback 或基础设施测试实现。
/// 同时支持字典路径（InferAsync）与连续内存路径（InferBatchAsync）。
/// 子问题1：实现 <see cref="IFallbackInferenceEngine"/> 标记接口，
/// 让 ModelActivationManager 通过 IFallbackInferenceEngine 注入本引擎，
/// 避免 DI 容器解析 IBatchInferenceEngine 时回到 ModelActivationManager 自身（循环依赖）。
/// </remarks>
public sealed class DeterministicBatchInferenceEngine : IFallbackInferenceEngine
{
    private const string DefaultModelVersion = "deterministic-hash-v1";

    // ContentHash 固定字符串（本引擎实现稳定，无外部工件）。
    // 真实模型实现应返回 ONNX/序列化模型的 SHA-256。
    private const string EngineContentHash = "deterministic-hash-v1:fnv1a-64";

    private const string EngineCalibrationVersion = "default-v1";

    /// <summary>引擎使用的固定模型版本号。</summary>
    public string ModelVersion => DefaultModelVersion;

    /// <summary>
    /// 引擎类型 = DeterministicReplay。
    /// 该引擎仅产出 feature hash，不调用真实模型；默认配置下不得改变 FinalScore。
    /// </summary>
    public InferenceEngineKind Kind => InferenceEngineKind.DeterministicReplay;

    /// <summary>
    /// 本引擎实现的内容哈希（固定值，无外部工件）。
    /// </summary>
    public string ContentHash => EngineContentHash;

    /// <summary>
    /// 绑定的校准版本号。
    /// </summary>
    public string CalibrationVersion => EngineCalibrationVersion;

    /// <inheritdoc />
    public ValueTask<BatchInferenceResult> InferAsync(BatchInferenceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = Stopwatch.GetTimestamp();
        if (ct.IsCancellationRequested)
        {
            return new ValueTask<BatchInferenceResult>(BuildCancelledResult(startedAt));
        }

        var inputs = request.Inputs;
        var outputs = new InferenceOutput[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            outputs[i] = ComputeOutputFromVector(inputs[i]);
        }

        var result = new BatchInferenceResult
        {
            Outputs = outputs,
            Succeeded = true,
            Error = null,
            Duration = Stopwatch.GetElapsedTime(startedAt)
        };
        return new ValueTask<BatchInferenceResult>(result);
    }

    /// <summary>
    /// 基于连续 float 内存的批量推理。
    /// 比 InferAsync 减少 Boxing 与字典查找开销，适合高频推理。
    /// </summary>
    public ValueTask<BatchInferenceResult> InferBatchAsync(FeatureBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var startedAt = Stopwatch.GetTimestamp();
        if (ct.IsCancellationRequested)
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

        var outputs = new InferenceOutput[batch.RowCount];
        var valuesSpan = batch.Values.Span;
        for (var row = 0; row < batch.RowCount; row++)
        {
            ct.ThrowIfCancellationRequested();
            var rowSlice = valuesSpan.Slice(row * batch.FeatureCount, batch.FeatureCount);
            outputs[row] = ComputeOutputFromFloatSpan(batch.SchemaVersion, rowSlice);
        }

        var result = new BatchInferenceResult
        {
            Outputs = outputs,
            Succeeded = true,
            Error = null,
            Duration = Stopwatch.GetElapsedTime(startedAt)
        };
        return new ValueTask<BatchInferenceResult>(result);
    }

    // -----------------------------------------------------------------------
    // 分数映射：64 位 hash → Score [-1,1] + Confidence [0,1]
    // -----------------------------------------------------------------------

    private static InferenceOutput ComputeOutputFromVector(FeatureVector vector)
    {
        var hash = ComputeFeatureHashFromVector(vector);
        return HashToOutput(hash);
    }

    private static InferenceOutput ComputeOutputFromFloatSpan(string schemaVersion, ReadOnlySpan<float> row)
    {
        var hash = ComputeFeatureHashFromFloats(schemaVersion, row);
        return HashToOutput(hash);
    }

    private static InferenceOutput HashToOutput(ulong hash)
    {
        var hi = (uint)(hash >> 32);
        var lo = (uint)(hash & 0xFFFFFFFFUL);
        var probHi = hi / (double)(1UL << 32);
        var probLo = lo / (double)(1UL << 32);

        // Score 范围 [-1, 1]：以 probHi 为基准对称展开。
        var score = probHi * 2.0 - 1.0;
        // Confidence 范围 [0, 1]：使用低 32 位独立分量，避免与 score 完全耦合。
        var confidence = probLo;

        return new InferenceOutput
        {
            Score = score,
            Confidence = confidence,
            PerClassScores = null
        };
    }

    // -----------------------------------------------------------------------
    // 优化后的 hash 路径
    // -----------------------------------------------------------------------

    /// <summary>
    /// 基于字典路径的 hash（向后兼容路径）。
    /// 优化点：不再构建中间字符串；按 key 排序后逐个 key/value 写入 FNV-1a。
    /// 仍需 1 次 string[] 分配用于排序（无法避免，除非调用方提供有序字典）。
    /// </summary>
    private static ulong ComputeFeatureHashFromVector(FeatureVector vector)
    {
        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;
        var hash = OffsetBasis;

        // 1. SchemaVersion UTF-8 字节
        hash = HashAppendUtf8(hash, vector.SchemaVersion ?? string.Empty, Prime);
        hash = HashAppendByte(hash, (byte)'|', Prime);

        // 2. 按 key 排序后逐个写入（保证字典遍历顺序无关）
        if (vector.Values.Count > 0)
        {
            var keys = new string[vector.Values.Count];
            var i = 0;
            foreach (var kv in vector.Values)
            {
                keys[i++] = kv.Key;
            }
            Array.Sort(keys, StringComparer.Ordinal);

            for (i = 0; i < keys.Length; i++)
            {
                if (i > 0)
                {
                    hash = HashAppendByte(hash, (byte)',', Prime);
                }
                var key = keys[i];
                hash = HashAppendUtf8(hash, key, Prime);
                hash = HashAppendByte(hash, (byte)'=', Prime);
                var value = vector.Values[key];
                hash = HashAppendObject(hash, value, Prime);
            }
        }

        return hash;
    }

    /// <summary>
    /// 基于连续 float 内存的高性能 hash（推荐路径）。
    /// 0 托管分配：直接遍历 float 缓冲的字节表示 + SchemaVersion UTF-8 字节。
    /// 字节顺序由 MemoryMarshal.Cast 决定（小端序，x86/x64 一致；跨架构需注意）。
    /// </summary>
    private static ulong ComputeFeatureHashFromFloats(string schemaVersion, ReadOnlySpan<float> row)
    {
        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;
        var hash = OffsetBasis;

        // 1. SchemaVersion UTF-8 字节
        hash = HashAppendUtf8(hash, schemaVersion ?? string.Empty, Prime);
        hash = HashAppendByte(hash, (byte)'|', Prime);

        // 2. float bytes（连续内存，无装箱）
        var floatBytes = MemoryMarshal.AsBytes(row);
        for (var i = 0; i < floatBytes.Length; i++)
        {
            hash = HashAppendByte(hash, floatBytes[i], Prime);
        }

        return hash;
    }

    private static ulong HashAppendByte(ulong hash, byte b, ulong prime)
    {
        hash ^= b;
        hash *= prime;
        return hash;
    }

    private static ulong HashAppendUtf8(ulong hash, string text, ulong prime)
    {
        // 避免分配：使用 UTF-8 编码到 stackalloc 或 ArrayPool。
        // 对于短字符串（schema version 通常 < 64 字节），使用 stackalloc。
        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount == 0) return hash;

        Span<byte> buffer = byteCount <= 128
            ? stackalloc byte[byteCount]
            : new byte[byteCount];
        Encoding.UTF8.GetBytes(text, buffer);
        for (var i = 0; i < buffer.Length; i++)
        {
            hash ^= buffer[i];
            hash *= prime;
        }
        return hash;
    }

    private static ulong HashAppendObject(ulong hash, object? value, ulong prime)
    {
        // 对常见类型走无装箱路径；仅对未知类型回退 ToString。
        switch (value)
        {
            case null:
                // "null" 的 UTF-8 字节
                hash = HashAppendByte(hash, (byte)'n', prime);
                hash = HashAppendByte(hash, (byte)'u', prime);
                hash = HashAppendByte(hash, (byte)'l', prime);
                hash = HashAppendByte(hash, (byte)'l', prime);
                return hash;
            case double d:
                // 直接对 double 的 8 字节做 hash（避免 ToString 与解析往返）
                var dBytes = BitConverter.DoubleToUInt64Bits(d);
                return HashAppendUInt64(hash, dBytes, prime);
            case float f:
                var fBits = (ulong)BitConverter.SingleToInt32Bits(f);
                return HashAppendUInt64(hash, fBits, prime);
            case int iv:
                return HashAppendUInt64(hash, (ulong)iv, prime);
            case long lv:
                return HashAppendUInt64(hash, (ulong)lv, prime);
            case bool bv:
                return HashAppendByte(hash, bv ? (byte)'1' : (byte)'0', prime);
            case string s:
                return HashAppendUtf8(hash, s, prime);
            default:
                // 回退到 ToString（少见路径；保留原语义）
                return HashAppendUtf8(hash, value.ToString() ?? "null", prime);
        }
    }

    private static ulong HashAppendUInt64(ulong hash, ulong value, ulong prime)
    {
        // 逐字节处理，确保跨平台一致
        hash ^= (byte)(value & 0xFF);
        hash *= prime;
        hash ^= (byte)((value >> 8) & 0xFF);
        hash *= prime;
        hash ^= (byte)((value >> 16) & 0xFF);
        hash *= prime;
        hash ^= (byte)((value >> 24) & 0xFF);
        hash *= prime;
        hash ^= (byte)((value >> 32) & 0xFF);
        hash *= prime;
        hash ^= (byte)((value >> 40) & 0xFF);
        hash *= prime;
        hash ^= (byte)((value >> 48) & 0xFF);
        hash *= prime;
        hash ^= (byte)((value >> 56) & 0xFF);
        hash *= prime;
        return hash;
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
