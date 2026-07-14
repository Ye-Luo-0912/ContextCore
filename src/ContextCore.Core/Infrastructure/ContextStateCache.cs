using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 基于内存的 <see cref="IContextStateCache"/> 实现，同时实现 <see cref="IStateCacheInvalidator"/>。
/// R11-P6：使用 ConcurrentDictionary 存储，LRU 淘汰策略（默认上限 10000 项），
/// 支持 version 检查（通过 <see cref="IContextStateVersionStore"/> 在读取时验证版本是否仍有效）。
/// 进程内有效，重启丢失；多实例场景需替换为分布式实现。
/// </summary>
public sealed class InMemoryContextStateCache : IContextStateCache, IStateCacheInvalidator
{
    /// <summary>默认最大缓存项数。</summary>
    public const int DefaultMaxEntries = 10_000;

    private readonly IContextStateVersionStore? _versionStore;
    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly LinkedList<string> _lruList = new();
    private readonly object _lruLock = new();

    /// <summary>使用默认容量创建缓存实例。</summary>
    /// <param name="versionStore">可选的版本存储，用于读取时验证缓存项版本是否仍有效。</param>
    public InMemoryContextStateCache(IContextStateVersionStore? versionStore = null)
        : this(DefaultMaxEntries, versionStore)
    {
    }

    /// <summary>使用指定容量创建缓存实例。</summary>
    /// <param name="maxEntries">最大缓存项数，超过后按 LRU 淘汰。</param>
    /// <param name="versionStore">可选的版本存储。</param>
    public InMemoryContextStateCache(int maxEntries, IContextStateVersionStore? versionStore = null)
    {
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "maxEntries 必须为正数。");
        }

        _maxEntries = maxEntries;
        _versionStore = versionStore;
    }

    /// <summary>当前缓存项数量（近似值，并发场景下可能略有偏差）。</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        // 版本检查：若缓存项记录了版本范围，验证当前版本是否仍匹配
        if (entry.HasVersionScope && _versionStore is not null)
        {
            var currentVersion = await _versionStore.GetVersionAsync(
                entry.WorkspaceId!, entry.CollectionId!, entry.StoreKind!, ct).ConfigureAwait(false);
            if (currentVersion != entry.Version)
            {
                // 版本不匹配，移除过期项并返回 miss
                TryRemoveEntry(key);
                return null;
            }
        }

        TouchLru(entry);
        return entry.Value as T;
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();

        var entry = new CacheEntry
        {
            Value = value,
            ValueType = typeof(T)
        };

        SetCore(key, entry);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 写入缓存值并关联版本范围。读取时会通过 <see cref="IContextStateVersionStore"/> 验证版本是否仍有效。
    /// 供 <see cref="ContextStateCacheAccessor"/> 等需要版本感知的调用方使用。
    /// </summary>
    /// <typeparam name="T">缓存值类型。</typeparam>
    /// <param name="key">缓存键。</param>
    /// <param name="value">缓存值。</param>
    /// <param name="workspaceId">工作空间 ID（版本范围）。</param>
    /// <param name="collectionId">集合 ID（版本范围）。</param>
    /// <param name="storeKind">Store 种类（版本范围）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task SetAsync<T>(
        string key,
        T value,
        string workspaceId,
        string collectionId,
        string storeKind,
        CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKind);
        ct.ThrowIfCancellationRequested();

        long version = 0;
        if (_versionStore is not null)
        {
            version = await _versionStore.GetVersionAsync(workspaceId, collectionId, storeKind, ct).ConfigureAwait(false);
        }

        var entry = new CacheEntry
        {
            Value = value,
            ValueType = typeof(T),
            Version = version,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            StoreKind = storeKind
        };

        SetCore(key, entry);
    }

    /// <inheritdoc />
    public Task InvalidateAsync(CacheInvalidationKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_entries.IsEmpty)
        {
            return Task.CompletedTask;
        }

        // 扫描并移除所有匹配失效键的缓存项。
        // 失效匹配规则：
        //   - StoreKind 一致
        //   - WorkspaceId 一致
        //   - CollectionId 一致（key.CollectionId 为空串时仅匹配空串）
        //   - EntityId：key.EntityId 为 null 时移除整个集合范围；否则仅移除该 EntityId 对应项。
        //     由于缓存 entry 不直接存储 EntityId（key 是任意字符串），EntityId 级别的精准失效
        //     依赖调用方在 key 中编码 EntityId 或使用集合级失效。
        //     为保证安全，当 key.EntityId 为 null 时移除该范围下所有 entry；
        //     当 key.EntityId 非空时也移除该范围下所有 entry（保守策略，避免漏失效）。
        var keysToRemove = new List<string>();
        foreach (var kvp in _entries)
        {
            var entry = kvp.Value;
            if (!entry.HasVersionScope)
            {
                // 未关联版本范围的 entry 无法按 CacheInvalidationKey 精准失效，跳过
                continue;
            }

            if (MatchesKey(entry, key))
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var k in keysToRemove)
        {
            TryRemoveEntry(k);
        }

        return Task.CompletedTask;
    }

    /// <summary>清空所有缓存项（主要用于测试）。</summary>
    public void Clear()
    {
        lock (_lruLock)
        {
            _lruList.Clear();
        }

        _entries.Clear();
    }

    private void SetCore(string key, CacheEntry entry)
    {
        lock (_lruLock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                // 已存在：先移除旧 LRU 节点
                RemoveLruNode(existing);
            }

            var node = _lruList.AddFirst(key);
            entry.LruNode = node;
            _entries[key] = entry;

            // LRU 淘汰
            while (_lruList.Count > _maxEntries)
            {
                var oldestKey = _lruList.Last!.Value;
                _lruList.RemoveLast();
                if (_entries.TryRemove(oldestKey, out var removed))
                {
                    removed.LruNode = null;
                }
            }
        }
    }

    private void TouchLru(CacheEntry entry)
    {
        lock (_lruLock)
        {
            if (entry.LruNode is null)
            {
                return;
            }

            _lruList.Remove(entry.LruNode);
            entry.LruNode = _lruList.AddFirst(entry.LruNode.Value);
        }
    }

    private void TryRemoveEntry(string key)
    {
        lock (_lruLock)
        {
            if (_entries.TryRemove(key, out var removed))
            {
                RemoveLruNode(removed);
            }
        }
    }

    private void RemoveLruNode(CacheEntry entry)
    {
        if (entry.LruNode is not null)
        {
            _lruList.Remove(entry.LruNode);
            entry.LruNode = null;
        }
    }

    private static bool MatchesKey(CacheEntry entry, CacheInvalidationKey key)
    {
        return string.Equals(entry.StoreKind, key.StoreKind, StringComparison.Ordinal)
            && string.Equals(entry.WorkspaceId, key.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(entry.CollectionId, key.CollectionId, StringComparison.Ordinal);
    }

    private sealed class CacheEntry
    {
        public required object Value { get; init; }
        public required Type ValueType { get; init; }
        public long Version { get; init; }
        public string? WorkspaceId { get; init; }
        public string? CollectionId { get; init; }
        public string? StoreKind { get; init; }
        public LinkedListNode<string>? LruNode { get; set; }

        public bool HasVersionScope => StoreKind is not null
            && WorkspaceId is not null
            && CollectionId is not null;
    }
}
