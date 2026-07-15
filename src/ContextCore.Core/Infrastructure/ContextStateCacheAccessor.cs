using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 读路径缓存访问辅助。P0 返工：依赖 <see cref="IContextStateCache"/> 接口（可替换分布式实现）。
/// 所有写入必须携带 <see cref="DependencyScopeSet"/>，确保条目可被安全失效。
/// Single-flight：热点 miss 时通过 per-key 信号量合并并发工厂调用，避免击穿。
/// </summary>
/// <remarks>
/// 锁回收：finally 中释放信号量后，若 CurrentCount 恢复为初始值（1，无等待者）则尝试 TryRemove。
/// 热点 key 持续有等待者（CurrentCount==0），不会被回收；冷 key 空闲后被回收，避免锁表随 distinct key 永久增长。
/// TryRemove 与并发 GetOrAdd 之间存在极窄窗口：冷 key 上偶发一次重复 factory 调用，
/// 但缓存 double-check 与 SetAsync（last-write-wins）保证状态一致；factory 应为幂等读操作。
/// </remarks>
public sealed class ContextStateCacheAccessor
{
    private readonly IContextStateCache _cache;
    // single-flight：per-key 信号量，合并并发 miss。空闲 key 在 finally 中回收。
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _inflightLocks = new();

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
    /// Single-flight：并发 miss 合并为单次 factory 调用（<b>尽力而为</b>，非严格 once 语义）。
    /// <para>
    /// 锁回收窗口：冷 key 的信号量在 finally 中被 TryRemove 后，并发 GetOrAdd 可能创建新信号量并重复执行 factory。
    /// 缓存 double-check 与 SetAsync（last-write-wins）保证最终状态一致，但 factory 可能被调用多次。
    /// 因此 <paramref name="factory"/> 必须为幂等读操作。
    /// </para>
    /// </summary>
    /// <typeparam name="T">缓存值类型。</typeparam>
    /// <param name="key">结构化缓存键（必须非空，由 <see cref="StateCacheKey.From"/> 构造）。</param>
    /// <param name="scopes">依赖 scope 集合（至少一个）。</param>
    /// <param name="factory">未命中时的值工厂（必须为幂等读操作）。</param>
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

        // single-flight：per-key 信号量合并并发 miss
        var sem = _inflightLocks.GetOrAdd(key.Value, static _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // double-check：持锁后重新检查缓存
            cached = await _cache.GetAsync<T>(key, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            var value = await factory(ct).ConfigureAwait(false);
            await _cache.SetAsync(key, value, scopes, ct).ConfigureAwait(false);
            return value;
        }
        finally
        {
            sem.Release();
            // 锁回收：仅回收空闲信号量（无等待者）。热点 key 持续被持有，不会被回收。
            if (sem.CurrentCount == 1)
            {
                _inflightLocks.TryRemove(key.Value, out _);
            }
        }
    }
}
