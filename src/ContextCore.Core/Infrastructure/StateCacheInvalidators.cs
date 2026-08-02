using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 默认空实现，不执行任何失效操作。
/// 前：<c>AddContextCore</c> 注册此实现作为 <see cref="IStateCacheInvalidator"/>。
/// 后：生产路径由 <see cref="InMemoryContextStateCache"/> 同时实现 <see cref="IContextStateCache"/> 和
/// <see cref="IStateCacheInvalidator"/>，本类保留作为不依赖 DI 的隔离场景回退（如 StorageExtensions）。
/// </summary>
public sealed class NullStateCacheInvalidator : IStateCacheInvalidator
{
    /// <summary>空失效接收器单例，供不依赖 DI 的场景直接引用。</summary>
    public static NullStateCacheInvalidator Instance { get; } = new();

    /// <summary>始终返回 <see cref="Task.CompletedTask"/>，不执行任何失效操作。</summary>
    public Task InvalidateAsync(CacheInvalidationKey key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
