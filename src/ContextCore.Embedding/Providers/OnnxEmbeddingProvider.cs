using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Embedding.Services;
using ContextCore.Embedding.Utilities;

namespace ContextCore.Embedding;

/// <summary>通过可插拔 ONNX 会话执行 embedding 的 provider。</summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IAsyncDisposable
{
    private readonly EmbeddingCacheService _cache;
    private readonly OnnxEmbeddingSessionManager _sessionManager;
    private readonly EmbeddingOptions _options;

    public OnnxEmbeddingProvider(
        EmbeddingOptions options,
        OnnxEmbeddingSessionManager sessionManager,
        EmbeddingCacheService? cache = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sessionManager);

        _options = options;
        _sessionManager = sessionManager;
        _cache = cache ?? new EmbeddingCacheService();
    }

    /// <summary>
    /// 便捷构造函数：根据 options 创建 sessionManager，并根据 cacheMaxEntries 创建有上限的缓存。
    /// </summary>
    public OnnxEmbeddingProvider(EmbeddingOptions options, int cacheMaxEntries)
        : this(
            options,
            new OnnxEmbeddingSessionManager(options),
            new EmbeddingCacheService(cacheMaxEntries))
    {
    }

    public async Task<EmbeddingResult> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var modelName = string.IsNullOrWhiteSpace(request.ModelName)
            ? _options.ModelName
            : request.ModelName!;

        // Query 输入：当配置了 QueryInstruction 时，将指令前缀拼接到每个查询文本
        var instruction = request.InputKind == EmbeddingInputKind.Query
            ? _options.QueryInstruction
            : string.Empty;
        var hasInstruction = !string.IsNullOrEmpty(instruction);

        var vectors = new List<EmbeddingVector>(request.Inputs.Count);
        var misses = new List<(EmbeddingInput Original, string EffectiveText)>();
        var missHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cacheHits = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // P5-0.2: 预建 inputId -> inputIndex 字典，排序复杂度从 O(n²) 降为 O(n log n)
        var inputOrder = new Dictionary<string, int>(request.Inputs.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < request.Inputs.Count; i++)
        {
            var inputId = request.Inputs[i].Id;
            if (!string.IsNullOrEmpty(inputId) && !inputOrder.ContainsKey(inputId))
            {
                inputOrder[inputId] = i;
            }
        }
        try
        {
            foreach (var input in request.Inputs)
            {
                var effectiveText = hasInstruction ? instruction + input.Text : input.Text;
                // contentHash 包含 effectiveText（含 instruction），确保缓存与实际 embedding 一致
                var hashText = hasInstruction ? effectiveText : input.Text;
                var contentHash = EmbeddingContentHasher.HashText(hashText, request.InputKind, modelName);
                if (_options.EnableContentHashCache
                    && _cache.TryGet(modelName, contentHash, out var cached))
                {
                    cacheHits++;
                    vectors.Add(WithInputIdentity(cached, input, contentHash, cacheHit: true));
                    continue;
                }

                misses.Add((input, effectiveText));
                missHashes[input.Id] = contentHash;
            }

            if (misses.Count <= 0)
                return new EmbeddingResult
                {
                    OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                        ? Guid.NewGuid().ToString("N")
                        : request.OperationId,
                    ModelName = modelName,
                    Dimensions = vectors.FirstOrDefault()?.Values.Count
                                 ?? (_options.Dimensions > 0 ? _options.Dimensions : 0),
                    Succeeded = true,
                    Vectors =
                    [
                        .. SortByInputOrder(vectors, inputOrder)
                    ],
                    Usage = new ContextOperationUsage
                    {
                        InputTokens = request.Inputs.Sum(input => EstimateTokens(input.Text)),
                        OutputTokens = 0,
                        ModelCalls = misses.Count
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        ["provider"] = "onnx",
                        ["cacheHits"] = cacheHits.ToString(),
                        ["batchSize"] = Math.Max(1, _options.MaxBatchSize).ToString()
                    },
                    CreatedAt = DateTimeOffset.UtcNow
                };
            {
                var session = await _sessionManager.GetSessionAsync(cancellationToken).ConfigureAwait(false);
                foreach (var batch in misses.Chunk(Math.Max(1, _options.MaxBatchSize)))
                {
                    var batchVectors = await session.EmbedBatchAsync(
                        batch.Select(item => item.EffectiveText).ToArray(),
                        cancellationToken).ConfigureAwait(false);

                    if (batchVectors.Count != batch.Length)
                    {
                        return Failure(
                            request,
                            modelName,
                            $"ONNX embedding 会话返回数量不匹配：输入 {batch.Length} 条，输出 {batchVectors.Count} 条。");
                    }

                    for (var i = 0; i < batch.Length; i++)
                    {
                        var (input, _) = batch[i];
                        var values = batchVectors[i];
                        if (request.Normalize && _options.Normalize)
                        {
                            values = EmbeddingNormalization.Normalize(values);
                        }

                        var contentHash = missHashes[input.Id];
                        var vector = new EmbeddingVector
                        {
                            InputId = input.Id,
                            SourceRef = string.IsNullOrWhiteSpace(input.SourceRef) ? input.Id : input.SourceRef,
                            Values = values.ToArray(),
                            Norm = EmbeddingNormalization.CalculateNorm(values),
                            Metadata = new Dictionary<string, string>
                            {
                                ["contentHash"] = contentHash,
                                ["cacheHit"] = "false"
                            }
                        };

                        if (_options.EnableContentHashCache)
                        {
                            _cache.Store(modelName, contentHash, vector);
                        }

                        vectors.Add(vector);
                    }
                }
            }

            return new EmbeddingResult
            {
                OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                    ? Guid.NewGuid().ToString("N")
                    : request.OperationId,
                ModelName = modelName,
                Dimensions = vectors.FirstOrDefault()?.Values.Count
                             ?? (_options.Dimensions > 0 ? _options.Dimensions : 0),
                Succeeded = true,
                Vectors = SortByInputOrder(vectors, inputOrder),
                Usage = new ContextOperationUsage
                {
                    InputTokens = request.Inputs.Sum(input => EstimateTokens(input.Text)),
                    OutputTokens = 0,
                    ModelCalls = misses.Count
                },
                Metadata = new Dictionary<string, string>
                {
                    ["provider"] = "onnx",
                    ["cacheHits"] = cacheHits.ToString(),
                    ["batchSize"] = Math.Max(1, _options.MaxBatchSize).ToString()
                },
                CreatedAt = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            EmbeddingMetrics.EmbedDuration.Record(sw.Elapsed.TotalMilliseconds);
            EmbeddingMetrics.EmbedBatchSize.Record(request.Inputs.Count);
            EmbeddingMetrics.CacheHits.Add(cacheHits);
        }
    }

    private static EmbeddingResult Failure(
        EmbeddingRequest request,
        string modelName,
        string error)
    {
        return new EmbeddingResult
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? Guid.NewGuid().ToString("N")
                : request.OperationId,
            ModelName = modelName,
            Dimensions = 0,
            Succeeded = false,
            ErrorMessage = error,
            Vectors = Array.Empty<EmbeddingVector>(),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static EmbeddingVector WithInputIdentity(
        EmbeddingVector vector,
        EmbeddingInput input,
        string contentHash,
        bool cacheHit)
    {
        var metadata = new Dictionary<string, string>(vector.Metadata)
        {
            ["contentHash"] = contentHash,
            ["cacheHit"] = cacheHit ? "true" : "false"
        };

        return new EmbeddingVector
        {
            InputId = input.Id,
            SourceRef = string.IsNullOrWhiteSpace(input.SourceRef) ? input.Id : input.SourceRef,
            Values = vector.Values.ToArray(),
            Norm = vector.Norm,
            Metadata = metadata
        };
    }

    private static int EstimateTokens(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : Math.Max(1, text.Length / 4);
    }

    /// <summary>
    /// P5-0.2: 按输入顺序排序向量。使用预建的 inputOrder 字典，复杂度 O(n log n)。
    /// 未找到 inputId 的向量排在末尾（保持稳定顺序）。
    /// </summary>
    private static EmbeddingVector[] SortByInputOrder(
        List<EmbeddingVector> vectors,
        Dictionary<string, int> inputOrder)
    {
        if (vectors.Count <= 1)
        {
            return vectors.ToArray();
        }

        return vectors
            .OrderBy(vector => inputOrder.TryGetValue(vector.InputId, out var index) ? index : int.MaxValue)
            .ToArray();
    }

    /// <summary>释放底层 ONNX 会话，供 DI 容器在应用关闭时调用。</summary>
    public async ValueTask DisposeAsync()
    {
        await _sessionManager.ForceUnloadAsync().ConfigureAwait(false);
        _cache.Clear();
    }
}
