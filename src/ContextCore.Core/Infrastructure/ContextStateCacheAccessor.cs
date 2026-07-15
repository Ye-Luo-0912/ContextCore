using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 读路径缓存访问辅助。P0 返工：依赖 <see cref="IContextStateCache"/> 接口（可替换分布式实现）。
/// 所有写入必须携带 <see cref="DependencyScopeSet"/>，确保条目可被安全失效。
/// Single-flight：热点 miss 时通过 per-key 共享 in-flight task 合并并发工厂调用，避免击穿。
/// </summary>
/// <remarks>
/// 严格 single-flight：使用 Lazy&lt;Task&gt; 包装确保 factory 严格只执行一次。
/// ConcurrentDictionary.GetOrAdd 可能多次调用 value factory，但 Lazy&lt;T&gt; 保证
/// 内部 task 只初始化一次。task 完成后（缓存已写入）从 _inflightTasks 移除，冷 key 自动回收。
/// trace 写入等副作用应放在 factory 内部，由 single-flight 保证只触发一次。
/// </remarks>
public sealed class ContextStateCacheAccessor
{
    private readonly IContextStateCache _cache;
    // single-flight：per-key 共享 in-flight task（Lazy 包装确保只初始化一次）
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inflightTasks = new();

    /// <summary>使用指定的缓存接口创建访问器。</summary>
    /// <param name="cache">缓存接口实例（可为进程内或分布式实现）。</param>
    public ContextStateCacheAccessor(IContextStateCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <summary>
    /// 按 key 获取缓存值，未命中时调用 <paramref name="factory"/> 生成值并写入缓存。
    /// 写入绑定 <paramref name="scopes"/>，任一 scope 失效时条目自动移除。
    /// Single-flight：并发 miss 合并为单次 factory 调用（严格 once 语义）。
    /// <para>
    /// 使用 Lazy&lt;Task&gt; 包装确保 factory 严格只执行一次，即使
    /// ConcurrentDictionary.GetOrAdd 多次调用 value factory。
    /// trace 写入等副作用应放在 factory 内部，由 single-flight 保证只触发一次。
    /// </para>
    /// </summary>
    /// <typeparam name="T">缓存值类型。</typeparam>
    /// <param name="key">结构化缓存键（必须非空，由 <see cref="StateCacheKey.From"/> 构造）。</param>
    /// <param name="scopes">依赖 scope 集合（至少一个）。</param>
    /// <param name="factory">未命中时的值工厂（执行副作用如 trace 写入是安全的，严格只调用一次）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<T> GetOrAddAsync<T>(
        StateCacheKey key,
        DependencyScopeSet scopes,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default) where T : class
    {
        // 缓存边界校验 StateCacheKey 非空（default 与 positional 构造器可绕过 From）
        if (string.IsNullOrWhiteSpace(key.Value))
        {
            throw new ArgumentException("StateCacheKey.Value 不能为 null 或空白。请使用 StateCacheKey.From 构造。", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(factory);

        // 快速路径：缓存命中
        var cached = await _cache.GetAsync<T>(key, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        // single-flight：per-key 共享 in-flight task
        // Lazy<Task> 保证即使 GetOrAdd 多次调用 value factory，task 也只初始化一次
        var lazy = _inflightTasks.GetOrAdd(key.Value, _ => new Lazy<Task<object?>>(
            () => CreateInflightTask(key, scopes, factory, ct),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var result = await lazy.Value.ConfigureAwait(false);
        // task 完成后（缓存已写入）移除 in-flight 记录，后续请求走快速路径
        _inflightTasks.TryRemove(key.Value, out _);
        return (T)result!;
    }

    /// <summary>
    /// 创建 in-flight task：double-check 缓存后执行 factory 并写入缓存。
    /// 返回 object? 以便存储在 Lazy&lt;Task&lt;object?&gt;&gt; 中。
    /// </summary>
    private async Task<object?> CreateInflightTask<T>(
        StateCacheKey key,
        DependencyScopeSet scopes,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct) where T : class
    {
        // double-check：持 task 后重新检查缓存（可能在等待 GetOrAdd 时已被其他请求填充）
        var cached = await _cache.GetAsync<T>(key, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var value = await factory(ct).ConfigureAwait(false);
        await _cache.SetAsync(key, value, scopes, ct).ConfigureAwait(false);
        return value;
    }
}
