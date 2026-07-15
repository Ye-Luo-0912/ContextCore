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

/// <summary>
/// 缓存失效信号接收器。在 Store 写入成功后触发，供 ContextStateCache 订阅。
/// </summary>
/// <remarks>
/// R10-2 失效边界抽象。Store Decorator 在写入成功后调用此接口发出失效信号。
/// R11-P6：由 <see cref="IContextStateCache"/> 实现同时订阅此信号，移除受影响的缓存项。
/// P0 返工：提交后失效必须完成，Decorator 在 commit point 后使用 <see cref="CancellationToken.None"/>。
/// </remarks>
public interface IStateCacheInvalidator
{
    /// <summary>标记一个缓存范围已失效。实现可以是空操作（当前无缓存）。</summary>
    /// <param name="key">失效范围键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task InvalidateAsync(CacheInvalidationKey key, CancellationToken cancellationToken = default);
}

/// <summary>
/// 结构化缓存键，标识一个缓存条目的逻辑身份。
/// P0 返工：取代裸 string，避免无 scope 写入。
/// </summary>
public readonly record struct StateCacheKey(string Value)
{
    /// <summary>从字符串构造缓存键（校验非空）。</summary>
    public static StateCacheKey From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new(value);
    }

    /// <summary>隐式转换为字符串，便于日志与字典 key 复用。</summary>
    public static implicit operator string(StateCacheKey key) => key.Value;

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// 缓存条目的依赖 scope 集合。任一 scope 失效时，条目即失效。
/// </summary>
/// <remarks>
/// P0 返工：取代单 scope 绑定，支持跨 Store 的组合依赖。
/// 例如 Package Builder 可同时依赖 Context/Memory/Constraint/Global/Relation 五个 scope，
/// 任一 Store 写入失效对应 scope 时，该缓存条目都会被移除。
/// 必须包含至少一个 scope，无 scope 缓存无法被安全失效。
/// </remarks>
public sealed class DependencyScopeSet
{
    private readonly IReadOnlySet<CacheInvalidationKey> _scopes;

    /// <summary>构造依赖 scope 集合。</summary>
    /// <param name="scopes">至少一个失效 scope。</param>
    public DependencyScopeSet(IEnumerable<CacheInvalidationKey> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var set = new HashSet<CacheInvalidationKey>(scopes);
        if (set.Count == 0)
        {
            throw new ArgumentException(
                "DependencyScopeSet 必须包含至少一个 scope。无 scope 缓存无法被安全失效。",
                nameof(scopes));
        }

        _scopes = set;
    }

    /// <summary>构造依赖 scope 集合。</summary>
    public DependencyScopeSet(params CacheInvalidationKey[] scopes)
        : this(scopes.AsEnumerable())
    {
    }

    /// <summary>所有依赖 scope（只读视图）。</summary>
    public IReadOnlySet<CacheInvalidationKey> Scopes => _scopes;

    /// <summary>scope 数量。</summary>
    public int Count => _scopes.Count;
}

/// <summary>
/// 上下文状态缓存接口。P0 返工：所有写入必须携带 <see cref="DependencyScopeSet"/>，
/// 确保每个缓存条目都可被 <see cref="InvalidateAsync"/> 命中。
/// </summary>
/// <remarks>
/// 写入边界由 <see cref="IStateCacheInvalidator"/> + Store Decorator 保证。
/// 版本感知：写入时记录所有 scope 的版本快照，读取时验证全部 scope 版本是否仍匹配。
/// 实现可替换为分布式缓存（接口不再依赖具体类）。
/// </remarks>
public interface IContextStateCache
{
    /// <summary>按 key 获取缓存值。未命中、已失效或版本失配返回 null。</summary>
    /// <typeparam name="T">缓存值类型（引用类型）。</typeparam>
    /// <param name="key">结构化缓存键。</param>
    /// <param name="ct">取消令牌。</param>
    Task<T?> GetAsync<T>(StateCacheKey key, CancellationToken ct = default) where T : class;

    /// <summary>
    /// 写入缓存值并绑定依赖 scope 集合。若达到容量上限，按 LRU 策略淘汰最久未访问的项。
    /// </summary>
    /// <typeparam name="T">缓存值类型（引用类型）。</typeparam>
    /// <param name="key">结构化缓存键。</param>
    /// <param name="value">缓存值。</param>
    /// <param name="scopes">依赖 scope 集合（至少一个，任一失效即移除条目）。</param>
    /// <param name="ct">取消令牌。</param>
    Task SetAsync<T>(StateCacheKey key, T value, DependencyScopeSet scopes, CancellationToken ct = default) where T : class;

    /// <summary>根据失效键移除匹配的缓存项。由 <see cref="IStateCacheInvalidator"/> 信号触发。</summary>
    /// <param name="key">失效范围键。</param>
    /// <param name="ct">取消令牌。</param>
    Task InvalidateAsync(CacheInvalidationKey key, CancellationToken ct = default);

    /// <summary>
    /// 条件删除：仅当 key 对应的缓存条目与 <paramref name="expectedEntryReference"/> 一致时删除。
    /// 用于版本失配删除场景，避免删除并发 SetAsync 写入的新条目。
    /// 实现通过引用相等（ReferenceEquals）比较，未匹配则不删除。
    /// </summary>
    /// <param name="key">缓存键。</param>
    /// <param name="expectedEntryReference">期望的缓存值引用（由 GetAsync 返回的对象）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否成功删除（true=已删除，false=引用不匹配或 key 不存在）。</returns>
    Task<bool> RemoveConditionalAsync(StateCacheKey key, object expectedEntryReference, CancellationToken ct = default);
}
