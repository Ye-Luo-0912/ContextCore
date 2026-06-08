using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>提供上下文条目的增删改查操作。</summary>
public interface IContextStore
{
    /// <summary>保存或更新一个上下文条目。</summary>
    Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default);

    /// <summary>按 ID 获取一个上下文条目。</summary>
    Task<ContextItem?> GetAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>按条件查询上下文条目列表。</summary>
    Task<IReadOnlyList<ContextItem>> QueryAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>删除指定 ID 的上下文条目。</summary>
    Task DeleteAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default);
}

/// <summary>提供集合级别的元数据管理操作。</summary>
public interface IContextCollectionStore
{
    /// <summary>保存或更新集合元数据。</summary>
    Task SaveCollectionAsync(
        ContextCollection collection,
        CancellationToken cancellationToken = default);

    /// <summary>获取指定集合的元数据。</summary>
    Task<ContextCollection?> GetCollectionAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default);
}

/// <summary>提供上下文条目的索引写入与搜索功能。</summary>
public interface IContextIndex
{
    /// <summary>插入或更新一条索引条目。</summary>
    Task UpsertAsync(ContextIndexEntry entry, CancellationToken cancellationToken = default);

    /// <summary>按条件搜索索引条目。</summary>
    Task<IReadOnlyList<ContextIndexEntry>> SearchAsync(
        IndexQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>负责根据请求构建结构化上下文包。</summary>
public interface IContextPackageBuilder
{
    /// <summary>构建并返回上下文包。</summary>
    Task<ContextPackage> BuildAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>构建上下文包并返回 selected/dropped 决策日志。</summary>
    Task<ContextPackageBuildResult> BuildDetailedAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>持久化上下文包构建 trace，供 ControlRoom 和后续审计分析使用。</summary>
public interface IContextPackageBuildTraceStore
{
    Task SaveAsync(
        ContextPackageBuildResult result,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextPackageBuildResult>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default);
}
/// <summary>持久化上下文包策略，供服务和 ControlRoom 复用固定打包规则。</summary>
public interface IContextPackagePolicyStore
{
    Task SaveAsync(
        ContextPackagePolicy policy,
        CancellationToken cancellationToken = default);

    Task<ContextPackagePolicy?> GetAsync(
        string workspaceId,
        string collectionId,
        string policyId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextPackagePolicy>> QueryAsync(
        ContextPackagePolicyQuery query,
        CancellationToken cancellationToken = default);
}
