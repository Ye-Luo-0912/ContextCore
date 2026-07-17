using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>
/// 可选能力接口：支持按 ID 批量查询上下文条目。
/// 实现此接口的 <see cref="IContextStore"/> 可在一次调用中返回多个条目，
/// 避免 retrieval 通道中的 N+1 单条查询。返回列表只包含找到的条目，顺序不保证。
/// </summary>
public interface IContextStoreBatchLookup
{
    /// <summary>按 ID 批量获取上下文条目。只返回找到的条目。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextItem>> BatchGetAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 可选能力接口：支持按 ID 批量查询记忆条目。
/// 实现此接口的 <see cref="IMemoryStore"/> 可在一次调用中返回多个条目，
/// 避免 retrieval 通道中的 N+1 单条查询。返回列表只包含找到的条目，顺序不保证。
/// </summary>
public interface IMemoryStoreBatchLookup
{
    /// <summary>按 ID 批量获取记忆条目。只返回找到的条目。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextMemoryItem>> BatchGetAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default);
}
