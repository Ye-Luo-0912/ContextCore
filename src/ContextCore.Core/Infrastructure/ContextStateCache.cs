using System.Collections.Concurrent;
using System.Threading;
using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 基于内存的 <see cref="IContextStateCache"/> 实现，同时实现 <see cref="IStateCacheInvalidator"/>。
/// P0 返工：scope 索引实现 O(M) 失效（M 为该 scope 下的条目数，非全量 N）；
/// 每个条目绑定 <see cref="DependencyScopeSet"/>，支持跨 Store 组合依赖；
/// 版本感知：写入时记录所有 scope 版本快照，读取时通过批量接口一次性校验。
/// 进程内有效，重启丢失；多实例场景需替换为分布式实现。
/// </summary>
/// <remarks>
/// 并发模型：命中路径完全无锁（_entries 为 ConcurrentDictionary，LastAccessTicks 经 Interlocked 更新）；
/// 写路径（SetCore/InvalidateAsync/Clear）在 _lock 下串行以保证 scope 索引一致性。
/// 近似 LRU：淘汰时扫描 LastAccessTicks 找最旧项（O(N)，仅在超容量时触发，罕见），
/// 避免命中路径的 LinkedList 节点移动与全局锁争用。
/// </remarks>
public sealed class InMemoryContextStateCache : IContextStateCache, IStateCacheInvalidator
{
    /// <summary>默认最大缓存项数。</summary>
    public const int DefaultMaxEntries = 10_000;

    private readonly IContextStateVersionStore? _versionStore;
    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    // scope 索引：(StoreKind, WorkspaceId, CollectionId) -> entry keys。失效时 O(M) 定位。
    private readonly Dictionary<VersionScope, HashSet<string>> _scopeIndex = new();
    private readonly object _lock = new();

    // 指标（Interlocked 原子计数）
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _versionMismatches;

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

    /// <summary>缓存命中次数。</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>缓存未命中次数。</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>LRU 淘汰次数。</summary>
    public long Evictions => Interlocked.Read(ref _evictions);

    /// <summary>版本失配次数（命中条目但因版本过期被移除）。</summary>
    public long VersionMismatches => Interlocked.Read(ref _versionMismatches);

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(StateCacheKey key, CancellationToken ct = default) where T : class
    {
        EnsureKey(key);
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(key.Value, out var entry))
        {
            Interlocked.Increment(ref _misses);
            return null;
        }

        // 版本检查：批量拉取所有 scope 的当前版本，本地逐项比对快照。
        // 分布式版本存储下仅一次 RPC；进程内实现为单次同步遍历。
        if (_versionStore is not null && entry.VersionSnapshots is { Count: > 0 })
        {
            var scopes = entry.VersionSnapshots.Keys.ToArray();
            var currentVersions = await _versionStore.GetVersionsAsync(scopes, ct).ConfigureAwait(false);

            foreach (var (scopeKey, recordedVersion) in entry.VersionSnapshots)
            {
                if (!currentVersions.TryGetValue(scopeKey, out var currentVersion) || currentVersion != recordedVersion)
                {
                    // 任一 scope 版本不匹配（或范围缺失），移除过期项并返回 miss
                    Interlocked.Increment(ref _versionMismatches);
                    TryRemoveEntry(key.Value);
                    return null;
                }
            }
        }

        Interlocked.Increment(ref _hits);
        // 近似 LRU：无锁更新最后访问时间戳，避免命中路径的全局锁争用。
        Interlocked.Exchange(ref entry.LastAccessTicks, DateTimeOffset.UtcNow.Ticks);
        return entry.Value as T;
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(
        StateCacheKey key,
        T value,
        DependencyScopeSet scopes,
        CancellationToken ct = default) where T : class
    {
        EnsureKey(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(scopes);
        ct.ThrowIfCancellationRequested();

        // 记录所有 scope 的版本快照（去重 by VersionScope，EntityId 不影响版本）
        Dictionary<VersionScope, long>? versionSnapshots = null;
        if (_versionStore is not null)
        {
            versionSnapshots = new Dictionary<VersionScope, long>();
            foreach (var scope in scopes.Scopes)
            {
                var indexKey = new VersionScope(scope.WorkspaceId, scope.CollectionId, scope.StoreKind);
                if (!versionSnapshots.ContainsKey(indexKey))
                {
                    var v = await _versionStore.GetVersionAsync(
                        scope.WorkspaceId, scope.CollectionId, scope.StoreKind, ct).ConfigureAwait(false);
                    versionSnapshots[indexKey] = v;
                }
            }
        }

        var entry = new CacheEntry
        {
            Value = value,
            ValueType = typeof(T),
            Scopes = scopes,
            VersionSnapshots = versionSnapshots
        };

        SetCore(key.Value, entry, scopes);
    }

    /// <inheritdoc />
    public Task InvalidateAsync(CacheInvalidationKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var indexKey = new VersionScope(key.WorkspaceId, key.CollectionId, key.StoreKind);

        // 匹配与删除均在 _lock 内完成，避免与并发 SetAsync 覆盖产生误删：
        // 若在锁外删除，期间同名 key 被新 scope 覆盖，失效线程会删掉新条目造成抖动。
        lock (_lock)
        {
            if (!_scopeIndex.TryGetValue(indexKey, out var entryKeys) || entryKeys.Count == 0)
            {
                return Task.CompletedTask;
            }

            // 先收集匹配 key（不能边遍历 entryKeys 边删除其中的条目，因为 RemoveFromScopeIndex 会改 entryKeys）
            List<string>? keysToRemove = null;
            foreach (var entryKey in entryKeys)
            {
                if (!_entries.TryGetValue(entryKey, out var entry) || entry.Scopes is null)
                {
                    continue;
                }

                if (ScopeMatchesInvalidation(entry.Scopes, key))
                {
                    (keysToRemove ??= new List<string>()).Add(entryKey);
                }
            }

            if (keysToRemove is null)
            {
                return Task.CompletedTask;
            }

            foreach (var k in keysToRemove)
            {
                if (_entries.TryRemove(k, out var removed))
                {
                    RemoveFromScopeIndex(k, removed.Scopes);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>清空所有缓存项（主要用于测试）。所有结构在 _lock 下一次性清空，保持一致。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _scopeIndex.Clear();
            _entries.Clear();
        }
    }

    private static void EnsureKey(StateCacheKey key)
    {
        // StateCacheKey 是 readonly record struct，default 与 positional 构造器可绕过 From 校验；
        // 缓存边界必须再次校验非空，避免 null/空 Value 作为字典 key。
        if (string.IsNullOrWhiteSpace(key.Value))
        {
            throw new ArgumentException("StateCacheKey.Value 不能为 null 或空白。请使用 StateCacheKey.From 构造。", nameof(key));
        }
    }

    private void SetCore(string key, CacheEntry entry, DependencyScopeSet scopes)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                // 已存在：先从旧 scope 索引移除
                RemoveFromScopeIndex(key, existing.Scopes);
            }

            entry.LastAccessTicks = DateTimeOffset.UtcNow.Ticks;
            _entries[key] = entry;

            // 注册到新 scope 索引
            AddToScopeIndex(key, scopes);

            // 近似 LRU 淘汰：扫描找最久未访问项。O(N) 但仅在超容量时触发（罕见）。
            while (_entries.Count > _maxEntries)
            {
                string? oldestKey = null;
                var oldestTicks = long.MaxValue;
                foreach (var (k, e) in _entries)
                {
                    if (e.LastAccessTicks < oldestTicks)
                    {
                        oldestTicks = e.LastAccessTicks;
                        oldestKey = k;
                    }
                }

                if (oldestKey is not null && _entries.TryRemove(oldestKey, out var removed))
                {
                    RemoveFromScopeIndex(oldestKey, removed.Scopes);
                    Interlocked.Increment(ref _evictions);
                }
                else
                {
                    break; // 防御：若无项可淘汰则退出
                }
            }
        }
    }

    private void TryRemoveEntry(string key)
    {
        lock (_lock)
        {
            if (_entries.TryRemove(key, out var removed))
            {
                RemoveFromScopeIndex(key, removed.Scopes);
            }
        }
    }

    private void AddToScopeIndex(string entryKey, DependencyScopeSet scopes)
    {
        foreach (var scope in scopes.Scopes)
        {
            var indexKey = new VersionScope(scope.WorkspaceId, scope.CollectionId, scope.StoreKind);
            if (!_scopeIndex.TryGetValue(indexKey, out var set))
            {
                set = new HashSet<string>();
                _scopeIndex[indexKey] = set;
            }

            set.Add(entryKey);
        }
    }

    private void RemoveFromScopeIndex(string entryKey, DependencyScopeSet? scopes)
    {
        if (scopes is null)
        {
            return;
        }

        foreach (var scope in scopes.Scopes)
        {
            var indexKey = new VersionScope(scope.WorkspaceId, scope.CollectionId, scope.StoreKind);
            if (_scopeIndex.TryGetValue(indexKey, out var set))
            {
                set.Remove(entryKey);
                if (set.Count == 0)
                {
                    _scopeIndex.Remove(indexKey);
                }
            }
        }
    }

    /// <summary>
    /// 判断条目的依赖 scope 集合中是否有 scope 匹配失效键。
    /// 匹配规则：StoreKind/WorkspaceId/CollectionId 一致；
    /// EntityId：scope.EntityId 为 null（依赖全集合）时匹配任意失效；
    /// 失效 EntityId 为 null（全集合失效）时匹配任意 scope；
    /// 否则要求 EntityId 相等。
    /// </summary>
    private static bool ScopeMatchesInvalidation(DependencyScopeSet scopes, CacheInvalidationKey invalidation)
    {
        foreach (var scope in scopes.Scopes)
        {
            if (!string.Equals(scope.StoreKind, invalidation.StoreKind, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(scope.WorkspaceId, invalidation.WorkspaceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(scope.CollectionId, invalidation.CollectionId, StringComparison.Ordinal))
            {
                continue;
            }

            // EntityId 匹配
            if (scope.EntityId is null || invalidation.EntityId is null
                || string.Equals(scope.EntityId, invalidation.EntityId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CacheEntry
    {
        public required object Value { get; init; }
        public required Type ValueType { get; init; }
        public DependencyScopeSet? Scopes { get; init; }
        public IReadOnlyDictionary<VersionScope, long>? VersionSnapshots { get; init; }
        // 近似 LRU：最后访问时间戳，命中时经 Interlocked.Exchange 无锁更新。
        public long LastAccessTicks;
    }
}
