using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>执行混合上下文检索，组合规则召回、关系扩展和向量召回。</summary>
public interface IContextRetriever
{
    Task<ContextRetrievalResult> RetrieveAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>持久化检索 trace，供 ControlRoom 和后续调试使用。</summary>
public interface IRetrievalTraceStore
{
    Task SaveAsync(
        ContextRetrievalTrace trace,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按稳定主键 (workspace_id, collection_id, retrieval_id) 点查单条 trace。
    /// Decision Evidence 审计必须用点查而非"最近 N 条"窗口扫描——数据存在即可查，
    /// 不受后续 trace 数量影响（QueryRecent 窗口外的决策仍可审计）。
    /// </summary>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="collectionId">Collection ID。</param>
    /// <param name="retrievalId">检索 trace ID（= 决策 DecisionId）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的 trace；不存在时返回 null。</returns>
    Task<ContextRetrievalTrace?> GetAsync(
        string workspaceId,
        string collectionId,
        string retrievalId,
        CancellationToken cancellationToken = default);
}
