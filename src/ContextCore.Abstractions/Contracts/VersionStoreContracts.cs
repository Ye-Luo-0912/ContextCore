namespace ContextCore.Abstractions;

/// <summary>
/// 上下文状态版本存储：按 (workspaceId, collectionId, storeKind) 维护单调递增的版本号。
/// R10-2 P3：供失效边界 Decorator 在写入成功后 bump 版本，未来 ContextStateCache 可据版本号判断是否命中。
/// </summary>
public interface IContextStateVersionStore
{
    /// <summary>获取指定范围的当前版本号（从 0 开始，每次 bump 自增）。</summary>
    Task<long> GetVersionAsync(
        string workspaceId,
        string collectionId,
        string storeKind,
        CancellationToken cancellationToken = default);

    /// <summary>将指定范围的版本号自增并返回新版本号。</summary>
    Task<long> BumpVersionAsync(
        string workspaceId,
        string collectionId,
        string storeKind,
        CancellationToken cancellationToken = default);
}
