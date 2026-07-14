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

/// <summary>缓存失效信号接收器。在 Store 写入成功后触发，供未来 ContextStateCache 订阅。</summary>
/// <remarks>
/// R10-2 失效边界抽象。当前仅建立写入边界契约，P6 引入 ContextStateCache 时由其订阅并真正失效缓存。
/// 在 P6 之前使用 <c>NullStateCacheInvalidator</c> 空实现，不执行任何操作。
/// </remarks>
public interface IStateCacheInvalidator
{
    /// <summary>标记一个缓存范围已失效。实现可以是空操作（当前无缓存）。</summary>
    /// <param name="key">失效范围键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task InvalidateAsync(CacheInvalidationKey key, CancellationToken cancellationToken = default);
}
