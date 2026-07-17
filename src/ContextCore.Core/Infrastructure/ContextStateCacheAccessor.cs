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
/// P0-5.2 调用方取消隔离：共享 factory 使用内部 token（shutdown token），
/// 不绑定任何单一调用方的 token。调用方通过 WaitAsync(callerToken) 等待共享 task，
/// 取消只放弃当前调用方的等待，不影响共享计算和其他等待者。
/// </para>
/// <para>
/// R13.0 #1/#2：factory 前后版本向量比较 + stale computation 丢弃或单次重试。
/// factory 执行前取版本快照（beforeVersions），完成后取版本快照（afterVersions）。
/// 若版本变化（说明 factory 执行期间发生了写入），结果为 stale——丢弃不写入缓存，
/// 单次重试（重新执行 factory）。重试后仍 stale 则放弃缓存，直接返回结果（不缓存）。
/// 避免将 stale 结果锁在缓存中造成后续命中读到过期数据。
/// </para>
/// <para>
/// R13.0 #4：factory shutdown token 与 timeout。共享 factory 使用 _shutdownCts.Token
/// （而非 CancellationToken.None），允许进程停机时取消正在执行的 factory。可选的 factoryTimeout
/// 通过 per-call linked CTS 限制单次 factory 执行时长，避免长时间挂起。cache GetAsync/SetAsync
/// 仍使用 CancellationToken.None（快速 in-memory 操作，需保证 SetAsync 完成以避免丢失计算结果）。
/// </para>
/// </remarks>
public sealed class ContextStateCacheAccessor : IAsyncDisposable, IDisposable
{
    private readonly IContextStateCache _cache;
    private readonly IContextStateVersionStore? _versionStore;
    private readonly TimeSpan? _factoryTimeout;
    // R13.0 #4: shutdown token——进程停机时取消正在执行的 factory
    private readonly CancellationTokenSource _shutdownCts = new();
    // single-flight：per-key 共享 in-flight task（Lazy 包装确保只初始化一次）
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inflightTasks = new();
    private int _disposed;

    /// <summary>使用指定的缓存接口创建访问器。</summary>
    /// <param name="cache">缓存接口实例（可为进程内或分布式实现）。</param>
    /// <param name="versionStore">可选的版本存储，用于 factory 前后版本向量比较（R13.0 #1/#2）。</param>
    /// <param name="factoryTimeout">可选的 factory 执行超时（per-call）。null 表示仅依赖 shutdown 取消。</param>
    public ContextStateCacheAccessor(
        IContextStateCache cache,
        IContextStateVersionStore? versionStore = null,
        TimeSpan? factoryTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (factoryTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(factoryTimeout), "factoryTimeout 必须为正 TimeSpan。");
        }
        _cache = cache;
        _versionStore = versionStore;
        _factoryTimeout = factoryTimeout;
    }

    /// <summary>
    /// R13.0 #4: 触发 shutdown，取消所有正在执行的 factory。
    /// 后续 GetOrAddAsync 调用会立即在 factory 阶段收到 OperationCanceledException。
    /// 幂等：多次调用安全。DisposeAsync 也会触发 shutdown。
    /// </summary>
    public void Shutdown()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _shutdownCts.Cancel();
        }
    }

    /// <summary>
    /// 按 key 获取缓存值，未命中时调用 <paramref name="factory"/> 生成值并写入缓存。
    /// 写入绑定 <paramref name="scopes"/>，任一 scope 失效时条目自动移除。
    /// Single-flight：并发 miss 合并为单次 factory 调用（严格 once 语义）。
    /// </summary>
    /// <typeparam name="T">缓存值类型。</typeparam>
    /// <param name="key">结构化缓存键（必须非空，由 <see cref="StateCacheKey.From"/> 构造）。</param>
    /// <param name="scopes">依赖 scope 集合（至少一个）。</param>
    /// <param name="factory">未命中时的值工厂。factory 接收 shutdown token（+ 可选 timeout），不受调用方取消影响；副作用（如 trace 写入）严格只触发一次。</param>
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
        // R13.0 #4: factory 内部使用 shutdown token（+ 可选 timeout），不受调用方取消影响。
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
    /// P0-5.2: 共享计算不绑定任何调用方的 token，避免首个调用方取消导致所有等待者失败。
    /// 调用方取消仅放弃自身等待（通过 WaitAsync）。
    /// R13.0 #1/#2: factory 前后版本向量比较，stale 结果丢弃并单次重试。
    /// R13.0 #4: factory 使用 shutdown token（+ 可选 timeout），cache GetAsync/SetAsync
    /// 仍使用 CancellationToken.None（快速 in-memory 操作，SetAsync 必须完成以持久化计算结果）。
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

        // R13.0 #1/#2: 版本向量比较——factory 执行前后比较版本，stale 则丢弃并单次重试。
        var versionScopes = ExtractVersionScopes(scopes);
        var beforeVersions = await CaptureVersionsAsync(versionScopes).ConfigureAwait(false);

        // R13.0 #4: factory 使用 shutdown token（+ 可选 timeout），不绑定调用方 token
        var value = await InvokeFactoryAsync(factory).ConfigureAwait(false);

        // R13.0 #1: factory 执行后比较版本向量——若变化则结果为 stale
        var afterVersions = await CaptureVersionsAsync(versionScopes).ConfigureAwait(false);
        if (VersionsChanged(beforeVersions, afterVersions))
        {
            // R13.0 #2: stale computation 丢弃——不写入缓存，单次重试
            beforeVersions = afterVersions;
            value = await InvokeFactoryAsync(factory).ConfigureAwait(false);
            afterVersions = await CaptureVersionsAsync(versionScopes).ConfigureAwait(false);

            if (VersionsChanged(beforeVersions, afterVersions))
            {
                // 重试后仍 stale——放弃缓存，直接返回结果（不缓存）
                return value;
            }
        }

        await _cache.SetAsync(key, value, scopes, CancellationToken.None).ConfigureAwait(false);
        return value;
    }

    /// <summary>
    /// R13.0 #4: 调用 factory 并应用 shutdown token + 可选 timeout。
    /// 有 factoryTimeout 时创建 per-call linked CTS（shutdown + timeout），方法返回时 dispose。
    /// 无 factoryTimeout 时直接传 _shutdownCts.Token，避免 per-call 分配。
    /// </summary>
    private async Task<T> InvokeFactoryAsync<T>(Func<CancellationToken, Task<T>> factory) where T : class
    {
        if (_factoryTimeout is { } timeout)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, timeoutCts.Token);
            return await factory(linkedCts.Token).ConfigureAwait(false);
        }
        return await factory(_shutdownCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 从 DependencyScopeSet 提取去重的 VersionScope 集合（EntityId 不影响版本）。
    /// </summary>
    private static List<VersionScope> ExtractVersionScopes(DependencyScopeSet scopes)
    {
        var unique = new HashSet<VersionScope>();
        foreach (var scope in scopes.Scopes)
        {
            unique.Add(new VersionScope(scope.WorkspaceId, scope.CollectionId, scope.StoreKind));
        }
        return unique.ToList();
    }

    /// <summary>
    /// 批量获取版本快照。无版本存储时返回 null（跳过版本比较，保持原有行为）。
    /// </summary>
    private async Task<Dictionary<VersionScope, long>?> CaptureVersionsAsync(List<VersionScope> scopes)
    {
        if (_versionStore is null || scopes.Count == 0)
        {
            return null;
        }

        var versions = await _versionStore.GetVersionsAsync(scopes, CancellationToken.None).ConfigureAwait(false);
        return new Dictionary<VersionScope, long>(versions);
    }

    /// <summary>
    /// 比较前后版本快照是否变化。任一 scope 版本不一致（或新增/缺失）视为变化。
    /// </summary>
    private static bool VersionsChanged(
        Dictionary<VersionScope, long>? before,
        Dictionary<VersionScope, long>? after)
    {
        // 无版本存储时跳过比较（保持原有行为）
        if (before is null || after is null)
        {
            return false;
        }

        if (before.Count != after.Count)
        {
            return true;
        }

        foreach (var (scope, beforeVersion) in before)
        {
            if (!after.TryGetValue(scope, out var afterVersion) || afterVersion != beforeVersion)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// R13.0 #4: 同步释放——触发 shutdown 并释放 _shutdownCts。
    /// 用于 using 语法和同步 DI 容器释放路径。优先使用 <see cref="DisposeAsync"/>。
    /// 幂等：多次调用安全（通过 _disposed 标志保护，与 DisposeAsync 共享）。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已被其他路径 dispose——忽略
        }

        _shutdownCts.Dispose();
    }

    /// <summary>
    /// R13.0 #4: 异步释放——触发 shutdown 取消正在执行的 factory，并释放 _shutdownCts。
    /// 幂等：多次调用安全（通过 _disposed 标志保护，与 Dispose 共享）。
    /// 不等待 in-flight task 完成：factory 收到 OperationCanceledException 后会自行传播，
    /// 等待方通过 WaitAsync(callerToken) 已收到取消异常。等待会阻塞宿主关闭。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
