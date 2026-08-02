namespace ContextCore.Abstractions;

/// <summary>
/// 上下文状态版本存储：按 (workspaceId, collectionId, storeKind) 维护单调递增的版本号。
/// 供失效边界 Decorator 在写入成功后 bump 版本，未来 ContextStateCache 可据版本号判断是否命中。
/// </summary>
public interface IContextStateVersionStore
{
    /// <summary>获取指定范围的当前版本号（从 0 开始，每次 bump 自增）。</summary>
    Task<long> GetVersionAsync(
        string workspaceId,
        string collectionId,
        string storeKind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量获取多个范围的当前版本号。缓存命中路径用此接口一次性校验所有依赖 scope 的版本，
    /// 避免分布式实现下每次命中 N 次网络调用。未包含的范围不在返回字典中。
    /// </summary>
    /// <param name="scopes">需要查询的范围集合（去重由实现负责）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyDictionary<VersionScope, long>> GetVersionsAsync(
        IReadOnlyCollection<VersionScope> scopes,
        CancellationToken cancellationToken = default);

    /// <summary>将指定范围的版本号自增并返回新版本号。</summary>
    Task<long> BumpVersionAsync(
        string workspaceId,
        string collectionId,
        string storeKind,
        CancellationToken cancellationToken = default);
}

/// <summary>版本范围三元组：(workspaceId, collectionId, storeKind)。EntityId 不影响版本。</summary>
public readonly record struct VersionScope(string WorkspaceId, string CollectionId, string StoreKind);
