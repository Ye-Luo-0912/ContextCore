using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>V1 mock embedding generator；结果固定可重复，仅用于基础设施测试和预览。</summary>
public sealed class MockEmbeddingGenerator : IEmbeddingGenerator, IEmbeddingGeneratorDescriptor
{
    public string Provider => "mock";

    public string Model => "mock-vector-index-v1";

    public int Dimension { get; }

    public string ProviderType => "Mock";

    public bool Normalize => true;

    public string PoolingStrategy => string.Empty;

    public MockEmbeddingGenerator(int dimension = 8)
    {
        Dimension = dimension > 0 ? dimension : 8;
    }

    public Task<EmbeddingGeneratorResult> GenerateAsync(
        EmbeddingGeneratorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entries = request.Inputs
            .Select(input =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return VectorIndexEntryFactory.Create(
                    request.WorkspaceId,
                    request.CollectionId,
                    input,
                    Provider,
                    Model,
                    Dimension,
                    ProviderType,
                    Normalize,
                    PoolingStrategy,
                    BuildVector(input));
            })
            .ToArray();

        return Task.FromResult(new EmbeddingGeneratorResult
        {
            OperationId = request.OperationId,
            EmbeddingProvider = Provider,
            EmbeddingModel = Model,
            Dimension = Dimension,
            Entries = entries
        });
    }

    private IReadOnlyList<float> BuildVector(EmbeddingGeneratorInput input)
    {
        var seed = $"{input.ItemId}\u001f{input.Text}\u001f{input.ItemKind}\u001f{input.Layer}";
        return VectorIndexMath.Normalize([.. Enumerable.Range(0, Dimension)
            .Select(index => ((seed.Length + index * 17) % 23 + 1) / 23f)]);
    }
}

/// <summary>基于 SHA-256 的确定性 embedding generator；相同输入总是生成相同单位向量。</summary>
public sealed class DeterministicHashEmbeddingGenerator : IEmbeddingGenerator, IEmbeddingGeneratorDescriptor
{
    public string Provider => "deterministic-hash";

    public string Model => "deterministic-hash-v1";

    public int Dimension { get; }

    public string ProviderType => EmbeddingProviderTypes.DeterministicHash;

    public bool Normalize => true;

    public string PoolingStrategy => string.Empty;

    public DeterministicHashEmbeddingGenerator(int dimension = 16)
    {
        Dimension = dimension > 0 ? dimension : 16;
    }

    public Task<EmbeddingGeneratorResult> GenerateAsync(
        EmbeddingGeneratorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entries = request.Inputs
            .Select(input =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return VectorIndexEntryFactory.Create(
                    request.WorkspaceId,
                    request.CollectionId,
                    input,
                    Provider,
                    Model,
                    Dimension,
                    ProviderType,
                    Normalize,
                    PoolingStrategy,
                    BuildVector(input));
            })
            .ToArray();

        return Task.FromResult(new EmbeddingGeneratorResult
        {
            OperationId = request.OperationId,
            EmbeddingProvider = Provider,
            EmbeddingModel = Model,
            Dimension = Dimension,
            Entries = entries
        });
    }

    private IReadOnlyList<float> BuildVector(EmbeddingGeneratorInput input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{input.ItemId}\u001f{input.Text}\u001f{input.ItemKind}\u001f{input.Layer}"));
        var values = new float[Dimension];
        for (var i = 0; i < Dimension; i++)
        {
            var raw = bytes[i % bytes.Length];
            values[i] = (raw - 127.5f) / 127.5f;
        }

        return VectorIndexMath.Normalize(values);
    }
}

internal static class VectorIndexEntryFactory
{
    public static VectorIndexEntry Create(
        string workspaceId,
        string collectionId,
        EmbeddingGeneratorInput input,
        string provider,
        string model,
        int dimension,
        string providerType,
        bool normalize,
        string poolingStrategy,
        IReadOnlyList<float> vector)
    {
        var now = DateTimeOffset.UtcNow;
        var contentHash = VectorIndexContentHasher.Hash(input.Text);
        return new VectorIndexEntry
        {
            EntryId = $"{workspaceId}:{collectionId}:{input.ItemId}:{model}:{provider}",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ItemId = input.ItemId,
            ItemKind = input.ItemKind,
            Layer = input.Layer,
            ContentHash = contentHash,
            EmbeddingModel = model,
            EmbeddingProvider = provider,
            Dimension = dimension,
            Vector = vector.ToArray(),
            CreatedAt = now,
            UpdatedAt = now,
            Metadata = new Dictionary<string, string>(input.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["createdFrom"] = "vector_index_foundation_v1",
                ["embeddingProviderType"] = providerType,
                ["normalize"] = normalize ? "true" : "false",
                ["poolingStrategy"] = poolingStrategy
            }
        };
    }
}

internal sealed record EmbeddingGeneratorDescriptor(
    string Provider,
    string Model,
    int Dimension,
    string ProviderType,
    bool Normalize,
    string PoolingStrategy)
{
    public static EmbeddingGeneratorDescriptor From(IEmbeddingGenerator generator)
    {
        var descriptor = generator as IEmbeddingGeneratorDescriptor;
        return new EmbeddingGeneratorDescriptor(
            generator.Provider,
            generator.Model,
            generator.Dimension,
            descriptor?.ProviderType ?? generator.Provider,
            descriptor?.Normalize ?? true,
            descriptor?.PoolingStrategy ?? string.Empty);
    }
}

internal static class VectorIndexContentHasher
{
    public static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal static class VectorIndexMath
{
    public static IReadOnlyList<float> Normalize(IReadOnlyList<float> vector)
    {
        var norm = Math.Sqrt(vector.Sum(value => value * value));
        
        return norm <= 0 ? vector.ToArray() : vector.Select(value => (float)(value / norm)).ToArray();
    }

    public static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var length = Math.Min(left.Count, right.Count);
        if (length == 0)
        {
            return 0;
        }

        var dot = 0.0;
        var leftNorm = 0.0;
        var rightNorm = 0.0;
        for (var i = 0; i < length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        return leftNorm <= 0 || rightNorm <= 0
            ? 0
            : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}
