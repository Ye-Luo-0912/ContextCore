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
/// 内部 task 只初始化一次。
/// <para>
/// P0-5.1 poisoned key 修复：共享 task 完成后（成功/失败/取消）通过 ContinueWith
/// 自动从 _inflightTasks 移除条目。factory 抛异常后 key 不再永久驻留，后续请求可重试。
/// 条件删除（key+value 匹配）避免删除已被新 task 替换的条目。
/// </para>
/// <para>
/// P0-5.2 调用方取消隔离：共享 factory 使用 CancellationToken.None（内部 token），
/// 不绑定任何单一调用方的 token。调用方通过 WaitAsync(callerToken) 等待共享 task，
/// 取消只放弃当前调用方的等待，不影响共享计算和其他等待者。
/// </para>
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
    /// </summary>
    /// <typeparam name="T">缓存值类型。</typeparam>
    /// <param name="key">结构化缓存键（必须非空，由 <see cref="StateCacheKey.From"/> 构造）。</param>
    /// <param name="scopes">依赖 scope 集合（至少一个）。</param>
    /// <param name="factory">未命中时的值工厂。factory 接收 CancellationToken.None，不受调用方取消影响；副作用（如 trace 写入）严格只触发一次。</param>
    /// <param name="ct">调用方取消令牌。取消只放弃当前调用方的等待，不取消共享 factory 计算和其他等待者。</param>
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
        var lazy = _inflightTasks.GetOrAdd(key.Value, _ => CreateLazyEntry(key, scopes, factory));

        // P0-5.2: 调用方用自己的 token 等待共享 task；取消只放弃等待，不取消共享计算。
        // factory 内部使用 CancellationToken.None，不受调用方取消影响。
        var result = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        return (T)result!;
    }

    /// <summary>
    /// 创建 Lazy 包装的共享 in-flight task。
    /// Lazy factory 内部：创建 task → 附加完成 continuation（移除 in-flight 记录）→ 返回 task。
    /// continuation 捕获 lazy 自身用于条件删除，避免删除已被新 task 替换的条目。
    /// </summary>
    private Lazy<Task<object?>> CreateLazyEntry<T>(
        StateCacheKey key,
        DependencyScopeSet scopes,
        Func<CancellationToken, Task<T>> factory) where T : class
    {
        // lazyRef 在 Lambda 体内被赋值前为 null，但 Lazy factory 仅在 .Value 首次访问时执行，
        // 此时 lazyRef 已完成赋值。null-forgiving 操作符 (!) 标记此约定。
        Lazy<Task<object?>>? lazyRef = null;
        lazyRef = new Lazy<Task<object?>>(() =>
        {
            var task = CreateInflightTask(key, scopes, factory);
            // P0-5.1: task 完成后（成功/失败/取消）自动移除 in-flight 记录，防止 poisoned key。
            // 条件删除（key+value）：仅当字典中仍为当前 lazy 时才移除，避免删除重试后的新条目。
            // ExecuteSynchronously：在完成 task 的线程上同步运行，避免调度开销。
            task.ContinueWith(
                _ => _inflightTasks.TryRemove(
                    new KeyValuePair<string, Lazy<Task<object?>>>(key.Value, lazyRef!)),
                TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        return lazyRef;
    }

    /// <summary>
    /// 创建 in-flight task：double-check 缓存后执行 factory 并写入缓存。
    /// 返回 object? 以便存储在 Lazy&lt;Task&lt;object?&gt;&gt; 中。
    /// </summary>
    /// <remarks>
    /// P0-5.2: factory 与 cache 操作使用 CancellationToken.None。共享计算不绑定任何调用方的 token，
    /// 避免首个调用方取消导致所有等待者失败。调用方取消仅放弃自身等待（通过 WaitAsync）。
    /// </remarks>
    private async Task<object?> CreateInflightTask<T>(
        StateCacheKey key,
        DependencyScopeSet scopes,
        Func<CancellationToken, Task<T>> factory) where T : class
    {
        // double-check：持 task 后重新检查缓存（可能在等待 GetOrAdd 时已被其他请求填充）
        var cached = await _cache.GetAsync<T>(key, CancellationToken.None).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        // P0-5.2: 共享 factory 使用 CancellationToken.None（内部 token），不绑定调用方 token
        var value = await factory(CancellationToken.None).ConfigureAwait(false);
        await _cache.SetAsync(key, value, scopes, CancellationToken.None).ConfigureAwait(false);
        return value;
    }
}
