using System.Diagnostics;
using System.Text;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.ModelExecution;

// ===========================================================================
// R28-D：Deterministic Batch Inference Engine
//
// 目标：
//   提供 IBatchInferenceEngine 的 fallback 实现，不调用真实模型，
//   仅基于 feature hash 产出确定性分数，用于：
//     1. 真实模型不可用时的降级路径（fail-safe）
//     2. 基础设施测试与本地预览（无需 GPU / 远程服务）
//     3. 单元测试中验证调用契约（输入顺序、超时、取消）
//
// 设计原则：
//   1. 确定性：相同输入（schema version + values 字典）必须产出相同分数；
//      不依赖时间戳、随机数、环境变量等不稳定因素。
//   2. Fallback 友好：单条输入解析失败时返回该条 fallback 输出，不抛异常；
//      整批失败（如 ct 取消）时返回 Succeeded=false 的结果。
//   3. Score 范围 [-1, 1]，Confidence 范围 [0, 1] —— 由 Calibrate 之后映射到 [0, 1]。
//   4. 不引入 I/O：纯内存计算，适合 Singleton 生命周期。
// ===========================================================================

/// <summary>
/// R28-D：确定性批量推理引擎（fallback 实现）。
/// </summary>
/// <remarks>
/// 相同输入始终产出相同分数；不调用真实模型，适合作为 fallback 或基础设施测试实现。
/// </remarks>
public sealed class DeterministicBatchInferenceEngine : IBatchInferenceEngine
{
    private const string DefaultModelVersion = "deterministic-hash-v1";

    /// <summary>引擎使用的固定模型版本号。</summary>
    public string ModelVersion => DefaultModelVersion;

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
            outputs[i] = ComputeOutput(inputs[i]);
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
    /// 基于特征向量计算确定性输出。
    /// 将 (schemaVersion + 排序后的 key=value 对) 拼接为字符串后取 SipHash 风格的 64 位哈希，
    /// 再映射到 Score [-1, 1] 与 Confidence [0, 1]。
    /// </summary>
    private static InferenceOutput ComputeOutput(FeatureVector vector)
    {
        var hash = ComputeFeatureHash(vector);
        // 将 64 位哈希映射到 [0, 1)：使用高 32 位作为分子，分母为 uint.MaxValue + 1。
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

    /// <summary>
    /// 计算特征向量的 64 位哈希。
    /// 排序 key 后拼接 key|value 字符串，确保字典遍历顺序不影响哈希结果。
    /// </summary>
    private static ulong ComputeFeatureHash(FeatureVector vector)
    {
        // 拼接 schema version + 排序后的 key=value 对。
        // 使用 UTF-8 编码避免平台默认编码差异。
        var sb = new StringBuilder(64);
        sb.Append(vector.SchemaVersion ?? string.Empty);
        sb.Append('|');

        // 收集并按 key 排序，保证字典遍历顺序不影响哈希。
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
                if (i > 0) sb.Append(',');
                var key = keys[i];
                var value = vector.Values[key];
                sb.Append(key).Append('=').Append(value?.ToString() ?? "null");
            }
        }

        return StableHash64(sb.ToString());
    }

    /// <summary>
    /// FNV-1a 64-bit 变种：确定性、无随机性、跨平台一致。
    /// </summary>
    private static ulong StableHash64(string text)
    {
        const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        var hash = OffsetBasis;
        // 用 UTF-8 字节而非 char，避免 UTF-16 surrogate pair 顺序差异。
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= Prime;
        }
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
