using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Embedding.Services;

/// <summary>
/// 基于 contentHash 的内存 embedding 缓存。
/// 支持容量上限和 LRU 淘汰策略，避免无限制内存增长。
/// </summary>
public sealed class EmbeddingCacheService
{
    private readonly int _maxEntries;
    private readonly Dictionary<string, CacheEntry> _vectors;
    private readonly Lock _gate = new();

    /// <summary>
    /// 创建默认上限（10000 条）的缓存实例。
    /// </summary>
    public EmbeddingCacheService()
        : this(maxEntries: 10000)
    {
    }

    /// <summary>
    /// 创建指定上限的缓存实例。maxEntries <= 0 表示无上限（不推荐生产使用）。
    /// </summary>
    public EmbeddingCacheService(int maxEntries)
    {
        _maxEntries = maxEntries;
        _vectors = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
    }

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
            if (_vectors.TryGetValue(Key(modelName, contentHash), out var entry))
            {
                entry.LastAccess = DateTimeOffset.UtcNow;
                vector = Clone(entry.Vector);
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
            var key = Key(modelName, contentHash);
            _vectors[key] = new CacheEntry(Clone(vector), DateTimeOffset.UtcNow);
            EvictIfNeeded();
        }
    }

    /// <summary>
    /// 批量写入多条缓存条目，在单个锁内完成，避免逐条获取锁的开销。
    /// </summary>
    public void StoreRange(
        string modelName,
        IReadOnlyList<(string ContentHash, EmbeddingVector Vector)> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (contentHash, vector) in items)
            {
                var key = Key(modelName, contentHash);
                _vectors[key] = new CacheEntry(Clone(vector), now);
            }

            EvictIfNeeded();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _vectors.Clear();
        }
    }

    private void EvictIfNeeded()
    {
        if (_maxEntries <= 0)
        {
            return;
        }

        while (_vectors.Count > _maxEntries)
        {
            // LRU 淘汰：移除最久未访问的条目
            var oldestKey = default(string?);
            var oldestAccess = DateTimeOffset.MaxValue;
            foreach (var pair in _vectors)
            {
                if (pair.Value.LastAccess < oldestAccess)
                {
                    oldestAccess = pair.Value.LastAccess;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey is not null)
            {
                _vectors.Remove(oldestKey);
            }
            else
            {
                break;
            }
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

    private sealed class CacheEntry
    {
        public EmbeddingVector Vector { get; }
        public DateTimeOffset LastAccess { get; set; }

        public CacheEntry(EmbeddingVector vector, DateTimeOffset lastAccess)
        {
            Vector = vector;
            LastAccess = lastAccess;
        }
    }
}
