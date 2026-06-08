using ContextCore.Abstractions;

namespace ContextCore.Embedding;

/// <summary>通过可插拔 ONNX 会话执行 embedding 的 provider。</summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider
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

        if (misses.Count > 0)
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
            Vectors = vectors
                .OrderBy(vector => request.Inputs.Select((input, index) => new { input.Id, Index = index })
                    .First(item => item.Id == vector.InputId).Index)
                .ToArray(),
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
}
