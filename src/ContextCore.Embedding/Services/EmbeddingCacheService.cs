using System.Collections.Immutable;
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
                // 直接返回冻结后的向量引用：Values 已是不可变数组、Metadata 在写入时克隆隔离，
                // 因此读取无需再 Clone，消除每次读取的数组+字典分配。
                // 注意：返回向量的 Metadata 与缓存条目共享，调用方应按只读约定使用。
                vector = entry.Vector;
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
            _vectors[key] = new CacheEntry(Freeze(vector), DateTimeOffset.UtcNow);
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
                _vectors[key] = new CacheEntry(Freeze(vector), now);
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

        var excess = _vectors.Count - _maxEntries;
        if (excess <= 0)
        {
            return;
        }

        if (excess == 1)
        {
            // 逐条写入的常见路径：单次 O(n) 扫描定位最旧条目并删除，无额外分配。
            string? oldestKey = null;
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

            return;
        }

        // 批量写入导致一次淘汰多条：用稳定排序一次性选出 excess 个最旧条目再批量删除，
        // 避免原来“每删一条都全表扫描”的 O(excess * n) 复杂度。
        // OrderBy 为稳定排序：LastAccess 相等时保留字典迭代（插入）顺序，
        // 与逐条扫描“首个最小者先淘汰”的语义保持一致。
        var keysToRemove = _vectors
            .OrderBy(static pair => pair.Value.LastAccess)
            .Take(excess)
            .Select(static pair => pair.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _vectors.Remove(key);
        }
    }

    private const string KeySeparator = "\u001f";

    private static string Key(
        string modelName,
        string contentHash)
    {
        // 直接拼接，避免插值字符串 handler 的额外分配。
        return string.Concat(modelName, KeySeparator, contentHash);
    }

    /// <summary>
    /// 冻结向量：将 Values 转为不可变数组、Metadata 克隆隔离后存入缓存。
    /// 之后读取可直接共享引用而无需 Clone。
    /// </summary>
    private static EmbeddingVector Freeze(EmbeddingVector vector)
    {
        return new EmbeddingVector
        {
            InputId = vector.InputId,
            SourceRef = vector.SourceRef,
            Values = vector.Values.ToImmutableArray(),
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
