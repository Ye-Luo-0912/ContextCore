using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Embedding.Services;

/// <summary>基于 contentHash 的内存 embedding 缓存。</summary>
public sealed class EmbeddingCacheService
{
    private readonly Dictionary<string, EmbeddingVector> _vectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _vectors.Count;
            }
        }
    }

    public bool TryGet(
        string modelName,
        string contentHash,
        out EmbeddingVector vector)
    {
        lock (_gate)
        {
            if (_vectors.TryGetValue(Key(modelName, contentHash), out var cached))
            {
                vector = Clone(cached);
                return true;
            }
        }

        vector = new EmbeddingVector();
        return false;
    }

    public void Store(
        string modelName,
        string contentHash,
        EmbeddingVector vector)
    {
        lock (_gate)
        {
            _vectors[Key(modelName, contentHash)] = Clone(vector);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _vectors.Clear();
        }
    }

    private static string Key(
        string modelName,
        string contentHash)
    {
        return $"{modelName}\u001f{contentHash}";
    }

    private static EmbeddingVector Clone(EmbeddingVector vector)
    {
        return new EmbeddingVector
        {
            InputId = vector.InputId,
            SourceRef = vector.SourceRef,
            Values = vector.Values.ToArray(),
            Norm = vector.Norm,
            Metadata = new Dictionary<string, string>(vector.Metadata)
        };
    }
}
