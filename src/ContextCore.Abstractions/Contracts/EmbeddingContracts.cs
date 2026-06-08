namespace ContextCore.Abstractions;

/// <summary>提供文本或上下文条目的 embedding 生成能力。</summary>
public interface IEmbeddingProvider
{
    /// <summary>执行一批 embedding 生成。</summary>
    Task<EmbeddingResult> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>提供向量记录写入与相似度检索能力。</summary>
public interface IVectorStore
{
    /// <summary>插入或更新一条向量记录。</summary>
    Task UpsertAsync(
        VectorRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>按 ID 获取向量记录。</summary>
    Task<VectorRecord?> GetAsync(
        string workspaceId,
        string vectorId,
        CancellationToken cancellationToken = default);

    /// <summary>执行向量相似度检索。</summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        VectorQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>删除指定向量记录。</summary>
    Task DeleteAsync(
        string workspaceId,
        string vectorId,
        CancellationToken cancellationToken = default);
}

/// <summary>编排 embedding 后台任务，从请求生成向量并写入向量存储。</summary>
public interface IEmbeddingJobService
{
    /// <summary>创建 embedding 后台任务。</summary>
    Task<EmbeddingJob> EnqueueAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>执行一个 embedding 后台任务。</summary>
    Task<EmbeddingJob> ProcessAsync(
        EmbeddingJob job,
        CancellationToken cancellationToken = default);

    /// <summary>查询最近的 embedding 后台任务。</summary>
    Task<IReadOnlyList<EmbeddingJob>> QueryRecentAsync(
        string workspaceId,
        string? collectionId,
        int take,
        CancellationToken cancellationToken = default);
}
