using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>默认空实现，不执行任何失效操作。在 P6 引入 ContextStateCache 前使用。</summary>
public sealed class NullStateCacheInvalidator : IStateCacheInvalidator
{
    /// <summary>空失效接收器单例，供不依赖 DI 的场景直接引用。</summary>
    public static NullStateCacheInvalidator Instance { get; } = new();

    /// <summary>始终返回 <see cref="Task.CompletedTask"/>，不执行任何失效操作。</summary>
    public Task InvalidateAsync(CacheInvalidationKey key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
