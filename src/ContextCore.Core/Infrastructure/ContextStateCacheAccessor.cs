using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 读路径缓存访问辅助。R11-P6：提供 GetOrAddAsync 模式，让读路径可选地使用缓存。
/// 不修改现有读路径行为，仅在调用方显式注入本类时生效。
/// 版本感知：写入缓存时记录当前版本，读取时通过 <see cref="IContextStateVersionStore"/> 验证版本是否仍有效。
/// </summary>
public sealed class ContextStateCacheAccessor
{
    private readonly InMemoryContextStateCache _cache;

    /// <summary>使用指定的缓存实例创建访问器。</summary>
    /// <param name="cache">底层缓存实例（需为 <see cref="InMemoryContextStateCache"/> 以支持版本感知写入）。</param>
    public ContextStateCacheAccessor(InMemoryContextStateCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <summary>
    /// 按 key 获取缓存值，未命中时调用 <paramref name="factory"/> 生成值并写入缓存。
    /// 不关联版本范围，仅通过显式 <see cref="IContextStateCache.InvalidateAsync"/> 失效。
    /// </summary>
    /// <typeparam name="T">缓存值类型。</typeparam>
    /// <param name="key">缓存键。</param>
    /// <param name="factory">未命中时的值工厂。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<T> GetOrAddAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        var cached = await _cache.GetAsync<T>(key, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var value = await factory(ct).ConfigureAwait(false);
        await _cache.SetAsync(key, value, ct).ConfigureAwait(false);
        return value;
    }

    /// <summary>
    /// 按 key 获取缓存值，未命中时调用 <paramref name="factory"/> 生成值并写入缓存。
    /// 关联版本范围 (workspaceId, collectionId, storeKind)，读取时验证版本是否仍有效。
    /// 写入成功后 Store Decorator 会 bump 版本，下次读取时版本不匹配自动失效。
    /// </summary>
    /// <typeparam name="T">缓存值类型。</typeparam>
    /// <param name="key">缓存键。</param>
    /// <param name="workspaceId">工作空间 ID（版本范围）。</param>
    /// <param name="collectionId">集合 ID（版本范围）。</param>
    /// <param name="storeKind">Store 种类（版本范围）。</param>
    /// <param name="factory">未命中时的值工厂。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<T> GetOrAddAsync<T>(
        string key,
        string workspaceId,
        string collectionId,
        string storeKind,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKind);
        ArgumentNullException.ThrowIfNull(factory);

        var cached = await _cache.GetAsync<T>(key, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var value = await factory(ct).ConfigureAwait(false);
        await _cache.SetAsync(key, value, workspaceId, collectionId, storeKind, ct).ConfigureAwait(false);
        return value;
    }
}
