using System.Collections.Concurrent;
using System.Threading;
using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 基于内存的 <see cref="IContextStateCache"/> 实现，同时实现 <see cref="IStateCacheInvalidator"/>。
/// 返工：scope 索引实现 O(M) 失效（M 为该 scope 下的条目数，非全量 N）；
/// 每个条目绑定 <see cref="DependencyScopeSet"/>，支持跨 Store 组合依赖；
/// 版本感知：写入时记录所有 scope 版本快照，读取时通过批量接口一次性校验。
/// 进程内有效，重启丢失；多实例场景需替换为分布式实现。
/// </summary>
/// <remarks>
/// 并发模型：命中路径完全无锁（_entries 为 ConcurrentDictionary，accessed bit 经 Interlocked 更新）；
/// 写路径（SetCore/InvalidateAsync/Clear）在 _lock 下串行以保证 scope 索引一致性。
/// CLOCK 淘汰：超容量时通过 enumerator 采样 <see cref="EvictionSampleSize"/> 个候选（默认 8 个），
/// 淘汰第一个 accessed=false 的条目，扫描过的条目清除 accessed bit（给予第二次机会）。
/// 全部采样 accessed=true 时强制淘汰首个采样，保证每次调用至少淘汰一个。O(1) 采样，非 O(N) 全量复制。
/// 版本失配删除使用条件删除（RemoveConditionalAsync），避免删除并发写入的新条目。
/// #6: 可选 TTL——条目写入后超过 TTL 即视为过期，读取时 lazy 淘汰并返回 null。
/// TTL 检查先于版本检查（TTL 是硬过期，版本是数据一致性校验；TTL 过期无需版本 RPC）。
/// </remarks>
public sealed class InMemoryContextStateCache : IContextStateCache, IStateCacheInvalidator
{
    /// <summary>默认最大缓存项数。</summary>
    public const int DefaultMaxEntries = 10_000;

    /// <summary>CLOCK 淘汰每次扫描的候选数量。</summary>
    private const int EvictionSampleSize = 8;

    private readonly IContextStateVersionStore? _versionStore;
    private readonly int _maxEntries;
    private readonly TimeSpan? _ttl;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    // scope 索引：(StoreKind, WorkspaceId, CollectionId) -> entry keys。失效时 O(M) 定位。
    private readonly Dictionary<VersionScope, HashSet<string>> _scopeIndex = new();
    private readonly object _lock = new();

    // 指标（Interlocked 原子计数）
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _versionMismatches;
    private long _ttlExpirations;

    /// <summary>使用默认容量创建缓存实例。</summary>
    /// <param name="versionStore">可选的版本存储，用于读取时验证缓存项版本是否仍有效。</param>
    /// <param name="ttl">可选的条目生存期（R13.0 #6）。null 表示无 TTL（条目仅由 scope 失效或 CLOCK 淘汰移除）。</param>
    public InMemoryContextStateCache(IContextStateVersionStore? versionStore = null, TimeSpan? ttl = null)
        : this(DefaultMaxEntries, versionStore, ttl)
    {
    }

    /// <summary>使用指定容量创建缓存实例。</summary>
    /// <param name="maxEntries">最大缓存项数，超过后按 CLOCK 策略淘汰。</param>
    /// <param name="versionStore">可选的版本存储。</param>
    /// <param name="ttl">可选的条目生存期（R13.0 #6）。</param>
    public InMemoryContextStateCache(int maxEntries, IContextStateVersionStore? versionStore = null, TimeSpan? ttl = null)
    {
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "maxEntries 必须为正数。");
        }
        if (ttl is { } ttlValue && ttlValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "ttl 必须为正 TimeSpan。");
        }

        _maxEntries = maxEntries;
        _versionStore = versionStore;
        _ttl = ttl;
    }

    /// <summary>当前缓存项数量（近似值，并发场景下可能略有偏差）。</summary>
    public int Count => _entries.Count;

    /// <summary>缓存命中次数。</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>缓存未命中次数。</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>淘汰次数。</summary>
    public long Evictions => Interlocked.Read(ref _evictions);

    /// <summary>版本失配次数（命中条目但因版本过期被移除）。</summary>
    public long VersionMismatches => Interlocked.Read(ref _versionMismatches);

    /// <summary>TTL 过期次数（R13.0 #6：条目因超过 TTL 被 lazy 淘汰）。</summary>
    public long TtlExpirations => Interlocked.Read(ref _ttlExpirations);

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

        // #6: TTL 过期检查——先于版本检查（TTL 是硬过期，无需版本 RPC）。
        // 过期则条件删除并返回 miss，避免后续命中读到超期数据。
        if (_ttl is { } ttl && entry.CreatedAt + ttl < DateTimeOffset.UtcNow)
        {
            Interlocked.Increment(ref _ttlExpirations);
            RemoveConditional(key.Value, entry);
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
                    // 任一 scope 版本不匹配（或范围缺失），条件删除过期项并返回 miss
                    // 使用条件删除：仅当 entry 引用未变时删除，避免删除并发 SetAsync 写入的新条目
                    Interlocked.Increment(ref _versionMismatches);
                    RemoveConditional(key.Value, entry);
                    return null;
                }
            }
        }

        Interlocked.Increment(ref _hits);
        // CLOCK：无锁设置 accessed bit，避免命中路径的全局锁争用。
        Interlocked.Exchange(ref entry.Accessed, 1);
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

        // 批量获取版本：一次 RPC 拉取所有 scope 的版本，避免逐 scope 调用
        Dictionary<VersionScope, long>? versionSnapshots = null;
        if (_versionStore is not null)
        {
            // 去重 by VersionScope（EntityId 不影响版本）
            var uniqueScopes = new HashSet<VersionScope>();
            foreach (var scope in scopes.Scopes)
            {
                uniqueScopes.Add(new VersionScope(scope.WorkspaceId, scope.CollectionId, scope.StoreKind));
            }

            if (uniqueScopes.Count > 0)
            {
                var versions = await _versionStore.GetVersionsAsync(uniqueScopes, ct).ConfigureAwait(false);
                versionSnapshots = new Dictionary<VersionScope, long>(versions);
            }
        }

        var entry = new CacheEntry
        {
            Value = value,
            ValueType = typeof(T),
            Scopes = scopes,
            VersionSnapshots = versionSnapshots,
            CreatedAt = DateTimeOffset.UtcNow, // R13.0 #6: 记录写入时间用于 TTL 过期检查
            Accessed = 1  // 新写入条目初始 accessed=1，给予一次 CLOCK 扫描保护
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

            _entries[key] = entry;

            // 注册到新 scope 索引
            AddToScopeIndex(key, scopes);

            // CLOCK 淘汰：超容量时扫描少量候选，淘汰第一个 accessed=false 的条目。
            // 比 O(N) 全量扫描更快，比精确 LRU 更轻量（无 LinkedList 节点移动）。
            while (_entries.Count > _maxEntries)
            {
                if (!TryEvictOne())
                {
                    break; // 防御：若无项可淘汰则退出
                }
            }
        }
    }

    /// <summary>
    /// CLOCK 淘汰一轮：通过 enumerator 采样 <see cref="EvictionSampleSize"/> 个候选，
    /// 淘汰第一个 accessed=0 的条目；扫描过的 accessed=1 条目清除 bit（给予第二次机会）。
    /// 若全部采样 accessed=1（已清除 bit），强制淘汰首个采样条目，保证每次调用至少淘汰一个。
    /// 不再 _entries.Keys.ToArray() 全量复制（O(N) 分配），改用 enumerator 采样固定数量。
    /// </summary>
    private bool TryEvictOne()
    {
        // 必须在 _lock 内调用
        if (_entries.IsEmpty)
        {
            return false;
        }

        // 通过 enumerator 采样 EvictionSampleSize 个候选，避免 O(N) Keys.ToArray() 分配
        var sampleCount = Math.Min(EvictionSampleSize, _entries.Count);
        if (sampleCount == 0)
        {
            return false;
        }

        var sampled = new List<KeyValuePair<string, CacheEntry>>(sampleCount);
        using var enumerator = _entries.GetEnumerator();
        for (var i = 0; i < sampleCount; i++)
        {
            if (!enumerator.MoveNext())
            {
                break;
            }
            sampled.Add(enumerator.Current);
        }

        if (sampled.Count == 0)
        {
            return false;
        }

        // 第一轮：扫描采样，淘汰首个 accessed=0 的条目；清除 accessed=1 的 bit（给予第二次机会）
        foreach (var (k, entry) in sampled)
        {
            // 原子读取并清除 accessed bit
            if (Interlocked.CompareExchange(ref entry.Accessed, 0, 1) == 0)
            {
                // accessed=0，淘汰此条目
                if (_entries.TryRemove(k, out var removed))
                {
                    RemoveFromScopeIndex(k, removed.Scopes);
                    Interlocked.Increment(ref _evictions);
                    return true;
                }
            }
            // accessed=1：bit 已清除（第二次机会），继续扫描
        }

        // 所有采样条目都被访问过（bit 已清除）：强制淘汰首个采样条目，保证收敛到容量
        var forceKey = sampled[0].Key;
        if (_entries.TryRemove(forceKey, out var forceRemoved))
        {
            RemoveFromScopeIndex(forceKey, forceRemoved.Scopes);
            Interlocked.Increment(ref _evictions);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 条件删除：仅当 entry 引用与 expectedEntry 一致时删除。
    /// 避免删除并发 SetAsync 写入的新条目（引用不同则不删除）。
    /// </summary>
    private bool RemoveConditional(string key, CacheEntry expectedEntry)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var current))
            {
                return false;
            }

            // 引用相等检查：确保我们删除的是读取时看到的同一个条目
            if (!ReferenceEquals(current, expectedEntry))
            {
                return false;
            }

            if (_entries.TryRemove(key, out var removed))
            {
                RemoveFromScopeIndex(key, removed.Scopes);
                return true;
            }

            return false;
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
        // #6: 条目写入时间，用于 TTL 过期检查（lazy 淘汰）。
        public DateTimeOffset CreatedAt { get; init; }
        // CLOCK：accessed bit，命中时设为 1，淘汰扫描时清除（给予第二次机会）。
        public int Accessed;
    }
}
