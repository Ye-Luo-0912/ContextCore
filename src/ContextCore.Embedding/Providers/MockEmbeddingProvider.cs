using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Embedding.Services;
using ContextCore.Embedding.Utilities;

namespace ContextCore.Embedding.Providers;

/// <summary>确定性的本地 embedding provider，用于开发、测试和无模型环境。</summary>
public sealed class MockEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingCacheService _cache;
    private readonly EmbeddingOptions _options;

    public MockEmbeddingProvider()
        : this(new EmbeddingOptions())
    {
    }

    public MockEmbeddingProvider(
        EmbeddingOptions options,
        EmbeddingCacheService? cache = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _cache = cache ?? new EmbeddingCacheService();
    }

    public int GeneratedVectorCount { get; private set; }

    public Task<EmbeddingResult> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var modelName = ResolveModelName(request);
        var dimensions = _options.Dimensions > 0 ? _options.Dimensions : 384;
        var vectors = new List<EmbeddingVector>(request.Inputs.Count);
        var cacheHits = 0;

        foreach (var batch in request.Inputs.Chunk(Math.Max(1, _options.MaxBatchSize)))
        {
            foreach (var input in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var contentHash = EmbeddingContentHasher.HashInput(input, request.InputKind, modelName);
                if (_options.EnableContentHashCache
                    && _cache.TryGet(modelName, contentHash, out var cached))
                {
                    cacheHits++;
                    vectors.Add(WithInputIdentity(cached, input, contentHash, cacheHit: true));
                    continue;
                }

                var values = BuildVector(input.Text, dimensions);
                if (request.Normalize && _options.Normalize)
                {
                    values = EmbeddingNormalization.Normalize(values);
                }

                var vector = new EmbeddingVector
                {
                    InputId = input.Id,
                    SourceRef = string.IsNullOrWhiteSpace(input.SourceRef) ? input.Id : input.SourceRef,
                    Values = values,
                    Norm = EmbeddingNormalization.CalculateNorm(values),
                    Metadata = new Dictionary<string, string>
                    {
                        ["contentHash"] = contentHash,
                        ["cacheHit"] = "false"
                    }
                };

                GeneratedVectorCount++;
                if (_options.EnableContentHashCache)
                {
                    _cache.Store(modelName, contentHash, vector);
                }

                vectors.Add(vector);
            }
        }

        return Task.FromResult(new EmbeddingResult
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? Guid.NewGuid().ToString("N")
                : request.OperationId,
            ModelName = modelName,
            Dimensions = dimensions,
            Succeeded = true,
            Vectors = vectors,
            Usage = new ContextOperationUsage
            {
                InputTokens = request.Inputs.Sum(input => EstimateTokens(input.Text)),
                OutputTokens = 0,
                ModelCalls = request.Inputs.Count
            },
            Metadata = new Dictionary<string, string>
            {
                ["provider"] = "mock",
                ["cacheHits"] = cacheHits.ToString(),
                ["batchSize"] = Math.Max(1, _options.MaxBatchSize).ToString()
            },
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private string ResolveModelName(EmbeddingRequest request)
    {
        return string.IsNullOrWhiteSpace(request.ModelName)
            ? _options.ModelName
            : request.ModelName!;
    }

    private static IReadOnlyList<float> BuildVector(string text, int dimensions)
    {
        var values = new float[dimensions];
        var normalizedText = string.IsNullOrWhiteSpace(text) ? " " : text;

        foreach (var index in from t in normalizedText select t.ToString().ToLowerInvariant() into token select SHA256.HashData(Encoding.UTF8.GetBytes(token)) into hash select BitConverter.ToUInt32(hash, 0) % dimensions)
        {
            values[index] += 1f;
        }

        return values;
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
