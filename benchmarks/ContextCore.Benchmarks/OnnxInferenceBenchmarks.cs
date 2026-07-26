using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ContextCore.Abstractions;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Inference.Onnx;

namespace ContextCore.Benchmarks;

// ===========================================================================
// P3 步骤6：ONNX 推理路径微基准
//
// 覆盖：
//   §1 InferAsync_DictionaryPath：传统 Dictionary<string,object> 路径（对照组，含 boxing）
//   §2 InferBatchAsync_ContinuousMemory：新 FeatureBatch 连续内存路径（P3 优化目标）
//   §3 InferBatchAsync_BatchSize_{1,8,32,128}：不同 batch size 下的连续内存路径
//   §4 InferBatchAsync_LargeBatchSplitting：RowCount=256 + MaxBatchSize=32 验证分片开销
//
// 引擎：DeterministicBatchInferenceEngine（无真实 ONNX 文件依赖，纯内存 hash 计算）。
// 分片场景：OnnxInferenceEngine + MockInferenceSession（包装 DeterministicBatchInferenceEngine）。
//
// 指标：Mean / Median / StdDev / P95（BenchmarkDotNet 默认）+ Allocated bytes（[MemoryDiagnoser]）
// 数据规模：[Params(1, 8, 32, 128)] 覆盖单行/小批次/中批次/大批次
// ===========================================================================

/// <summary>
/// P3 步骤6：ONNX 推理路径微基准。
/// 对比 Dictionary 路径（boxing）与 FeatureBatch 连续内存路径（无 boxing）的吞吐与分配。
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class OnnxInferenceBenchmarks
{
    private const string SchemaVersion = "1.0.0";

    [Params(1, 8, 32, 128)]
    public int BatchSize { get; set; }

    private DeterministicBatchInferenceEngine _engine = null!;
    private BatchInferenceRequest _dictionaryRequest = null!;
    private FeatureBatch _featureBatch = null!;
    private float[] _batchBuffer = null!;

    // 分片场景固定 RowCount=256 + MaxBatchSize=32
    private OnnxInferenceEngine _splittingEngine = null!;
    private FeatureBatch _largeBatch = null!;
    private float[] _largeBatchBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new DeterministicBatchInferenceEngine();

        // 预构造 Dictionary 路径请求（GlobalSetup 不计入 benchmark 测量）
        _dictionaryRequest = BuildDictionaryRequest(BatchSize);

        // 预构造 FeatureBatch（用 ArrayPool 避免在 setup 中产生 GC 噪声）
        // 注意：benchmark 本身只测量 InferBatchAsync 调用，不测量 batch 构造。
        var featureCount = 6;
        _batchBuffer = ArrayPool<float>.Shared.Rent(BatchSize * featureCount);
        _featureBatch = BuildFeatureBatch(BatchSize, featureCount, _batchBuffer);

        // 分片场景：RowCount=256, MaxBatchSize=32
        var largeRowCount = 256;
        _largeBatchBuffer = ArrayPool<float>.Shared.Rent(largeRowCount * featureCount);
        _largeBatch = BuildFeatureBatch(largeRowCount, featureCount, _largeBatchBuffer);

        var splittingOptions = new OnnxInferenceEngineOptions
        {
            InputTensorName = "input",
            ScoreOutputName = "score",
            MaxBatchSize = 32,
            EnableWarmup = false // benchmark 不 warmup，避免首次调用噪声
        };
        _splittingEngine = new OnnxInferenceEngine(
            new MockInferenceSession(_engine),
            splittingOptions);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        ArrayPool<float>.Shared.Return(_batchBuffer);
        ArrayPool<float>.Shared.Return(_largeBatchBuffer);
    }

    // §1 传统 Dictionary 路径（对照组，含 boxing double→object）
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Dictionary")]
    public async Task InferAsync_DictionaryPath()
    {
        var result = await _engine.InferAsync(_dictionaryRequest).ConfigureAwait(false);
        _ = result.Succeeded;
    }

    // §2 新 FeatureBatch 连续内存路径（P3 优化目标，无 boxing）
    [Benchmark]
    [BenchmarkCategory("FeatureBatch")]
    public async Task InferBatchAsync_ContinuousMemory()
    {
        var result = await _engine.InferBatchAsync(_featureBatch).ConfigureAwait(false);
        _ = result.Succeeded;
    }

    // §4 Large batch splitting：RowCount=256 + MaxBatchSize=32 验证分片开销
    // 固定 256 行（不随 BatchSize 参数变化），对比 §2 同规模无分片路径
    [Benchmark]
    [BenchmarkCategory("Splitting")]
    public async Task InferBatchAsync_LargeBatchSplitting()
    {
        var result = await _splittingEngine.InferBatchAsync(_largeBatch).ConfigureAwait(false);
        _ = result.Succeeded;
    }

    // === 数据生成 ===

    private static BatchInferenceRequest BuildDictionaryRequest(int rowCount)
    {
        var inputs = new List<FeatureVector>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            var values = new Dictionary<string, object>(6, StringComparer.Ordinal)
            {
                ["lexical_score"] = 0.1 + i * 0.001,
                ["semantic_score"] = 0.2 + i * 0.001,
                ["recency_score"] = 0.3,
                ["relation_boost"] = 0.0,
                ["mandatory_weight"] = 0.0,
                ["deterministic_score"] = 0.5 + i * 0.001
            };
            inputs.Add(new FeatureVector
            {
                SchemaVersion = SchemaVersion,
                Values = values
            });
        }
        return new BatchInferenceRequest { Inputs = inputs };
    }

    /// <summary>
    /// 构造 row-major float[] FeatureBatch。
    /// 使用调用方提供的 buffer（通常来自 ArrayPool）避免分配噪声。
    /// </summary>
    private static FeatureBatch BuildFeatureBatch(int rowCount, int featureCount, float[] buffer)
    {
        // 填充 row-major：第 i 行第 j 列位于 buffer[i * featureCount + j]
        for (var i = 0; i < rowCount; i++)
        {
            var offset = i * featureCount;
            buffer[offset + 0] = 0.1f + i * 0.001f; // lexical_score
            buffer[offset + 1] = 0.2f + i * 0.001f; // semantic_score
            buffer[offset + 2] = 0.3f;               // recency_score
            buffer[offset + 3] = 0.0f;               // relation_boost
            buffer[offset + 4] = 0.0f;               // mandatory_weight
            buffer[offset + 5] = 0.5f + i * 0.001f; // deterministic_score
        }

        return new FeatureBatch
        {
            SchemaVersion = SchemaVersion,
            Values = buffer.AsMemory(0, rowCount * featureCount),
            RowCount = rowCount,
            FeatureCount = featureCount,
            FeatureNames = new[] { "lexical_score", "semantic_score", "recency_score", "relation_boost", "mandatory_weight", "deterministic_score" }
        };
    }
}

/// <summary>
/// P3 步骤6：Mock IOnnxInferenceSession，包装 DeterministicBatchInferenceEngine。
/// 用于 OnnxInferenceEngine 分片 benchmark，无需真实 ONNX 模型文件。
/// </summary>
internal sealed class MockInferenceSession : IOnnxInferenceSession
{
    private readonly DeterministicBatchInferenceEngine _inner;

    public MockInferenceSession(DeterministicBatchInferenceEngine inner)
    {
        _inner = inner;
    }

    public string ModelArtifactId => "mock-session";
    public string ModelVersion => "mock-v1";
    public string ContentHash => "mock-hash";

    public ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken cancellationToken = default)
    {
        return _inner.InferBatchAsync(batch, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
