namespace ContextCore.Abstractions;

/// <summary>缓存失效键，标识一个可失效的缓存范围。</summary>
/// <param name="StoreKind">Store 种类，如 "ContextStore"、"MemoryStore"、"RelationStore"。</param>
/// <param name="WorkspaceId">工作空间 ID。</param>
/// <param name="CollectionId">集合 ID（可为空字符串表示工作空间级）。</param>
/// <param name="EntityId">实体 ID（可为 null 表示全集合失效）。</param>
public readonly record struct CacheInvalidationKey(
    string StoreKind,
    string WorkspaceId,
    string CollectionId,
    string? EntityId = null);

/// <summary>缓存失效信号接收器。在 Store 写入成功后触发，供 ContextStateCache 订阅。</summary>
/// <remarks>
/// R10-2 失效边界抽象。Store Decorator 在写入成功后调用此接口发出失效信号。
/// R11-P6：由 <see cref="IContextStateCache"/> 实现同时订阅此信号，移除受影响的缓存项。
/// </remarks>
public interface IStateCacheInvalidator
{
    /// <summary>标记一个缓存范围已失效。实现可以是空操作（当前无缓存）。</summary>
    /// <param name="key">失效范围键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task InvalidateAsync(CacheInvalidationKey key, CancellationToken cancellationToken = default);
}

/// <summary>
/// 上下文状态缓存接口。R11-P6 引入，提供可选的进程内读路径缓存。
/// 写入边界由 <see cref="IStateCacheInvalidator"/> + Store Decorator 保证，缓存项通过 <see cref="InvalidateAsync"/> 主动失效。
/// 读路径可选择性地使用本接口加速重复读取，不影响现有未使用缓存的读路径行为。
/// </summary>
public interface IContextStateCache
{
    /// <summary>按 key 获取缓存值。未命中或已失效返回 null。</summary>
    /// <typeparam name="T">缓存值类型（引用类型）。</typeparam>
    /// <param name="key">缓存键。</param>
    /// <param name="ct">取消令牌。</param>
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;

    /// <summary>写入缓存值。若达到容量上限，按 LRU 策略淘汰最久未访问的项。</summary>
    /// <typeparam name="T">缓存值类型（引用类型）。</typeparam>
    /// <param name="key">缓存键。</param>
    /// <param name="value">缓存值。</param>
    /// <param name="ct">取消令牌。</param>
    Task SetAsync<T>(string key, T value, CancellationToken ct = default) where T : class;

    /// <summary>根据失效键移除匹配的缓存项。由 <see cref="IStateCacheInvalidator"/> 信号触发。</summary>
    /// <param name="key">失效范围键。</param>
    /// <param name="ct">取消令牌。</param>
    Task InvalidateAsync(CacheInvalidationKey key, CancellationToken ct = default);
}
