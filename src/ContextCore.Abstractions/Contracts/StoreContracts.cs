using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>提供上下文条目的增删改查操作。</summary>
public interface IContextStore
{
    /// <summary>保存或更新一个上下文条目。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default);

    /// <summary>按 ID 获取一个上下文条目。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<ContextItem?> GetAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>按条件查询上下文条目列表。</summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<IReadOnlyList<ContextItem>> QueryAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>删除指定 ID 的上下文条目。</summary>
    [StoreOperation(StoreOperationKind.Write)]
    Task DeleteAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Keyset 分页查询能力接口。实现此接口的存储可返回 HasMore + 类型化 NextCursor，
/// 供服务端构造不透明分页 token（<see cref="ContextQueryPage.NextCursor"/>）。
/// </summary>
public interface IContextQueryPageStore
{
    /// <summary>
    /// 按 keyset 分页查询上下文条目，返回是否还有下一页及下一页的类型化游标。
    /// 语义与 <see cref="IContextStore.QueryAsync"/> 完全一致（含 After 续取与 Skip 回退），
    /// 仅额外返回分页元数据；实现应读取 Take + 1 条以判定 HasMore。
    /// </summary>
    [StoreOperation(StoreOperationKind.Read)]
    Task<ContextQueryPageResult> QueryPageAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>Keyset 分页查询结果：条目 + HasMore + 类型化下一页游标。</summary>
public sealed record ContextQueryPageResult
{
    /// <summary>当前页条目（最多请求的 Take 条）。</summary>
    public IReadOnlyList<ContextItem> Items { get; init; } = Array.Empty<ContextItem>();

    /// <summary>是否还有下一页。</summary>
    public bool HasMore { get; init; }

    /// <summary>下一页类型化游标（HasMore=false 时为 null；由服务端编码为不透明 token）。</summary>
    public ContextQueryCursor? NextCursor { get; init; }
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
    [StoreOperation(StoreOperationKind.Write)]
    Task UpsertAsync(ContextIndexEntry entry, CancellationToken cancellationToken = default);

    /// <summary>按条件搜索索引条目。</summary>
    [StoreOperation(StoreOperationKind.Read)]
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

    /// <summary>
    /// 按稳定主键 (workspace_id, collection_id, build_id) 点查单条包构建 trace。
    /// Decision Evidence 审计必须用点查而非"最近 N 条"窗口扫描——数据存在即可查，
    /// 不受后续 trace 数量影响。
    /// </summary>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="collectionId">Collection ID。</param>
    /// <param name="buildId">包构建 ID（= 决策 DecisionId）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的构建结果；不存在时返回 null。</returns>
    Task<ContextPackageBuildResult?> GetAsync(
        string workspaceId,
        string collectionId,
        string buildId,
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

/// <summary>
/// 持久化统一上下文决策记录（V17.0 decision trace）。
/// 该 store 只写只读 trace artifact，不参与 retrieval/package/planning 运行时决策。
/// </summary>
public interface IDecisionTraceStore
{
    Task SaveAsync(
        ContextDecisionRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按稳定主键 (workspace_id, collection_id, decision_id) 点查单条决策记录
    /// （Decision Evidence Plane：Durable / Point Lookup）。
    /// 审计 / 证据重建必须用点查而非"最近 N 条"窗口扫描——数据存在即可查，
    /// 不受后续记录数量影响。
    /// </summary>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="collectionId">Collection ID。</param>
    /// <param name="decisionId">决策 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的决策记录；不存在时返回 null。</returns>
    Task<ContextDecisionRecord?> GetAsync(
        string workspaceId,
        string collectionId,
        string decisionId,
        CancellationToken cancellationToken = default);
}
