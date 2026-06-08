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
}
