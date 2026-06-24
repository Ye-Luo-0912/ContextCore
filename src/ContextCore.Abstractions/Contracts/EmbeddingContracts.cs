using ContextCore.Abstractions.Models;

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

/// <summary>V1 vector index 使用的可重复 embedding 生成器；不接正式 retrieval scorer。</summary>
public interface IEmbeddingGenerator
{
    string Provider { get; }

    string Model { get; }

    int Dimension { get; }

    Task<EmbeddingGeneratorResult> GenerateAsync(
        EmbeddingGeneratorRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>可选的 embedding generator 元数据；用于 vector index 兼容性诊断，不影响正式 retrieval。</summary>
public interface IEmbeddingGeneratorDescriptor
{
    string ProviderType { get; }

    bool Normalize { get; }

    string PoolingStrategy { get; }
}

/// <summary>embedding tokenizer 抽象；ONNX generator 不直接写死具体 tokenizer 实现。</summary>
public interface IEmbeddingTokenizer
{
    EmbeddingTokenizationResult Tokenize(
        IReadOnlyList<string> texts,
        int maxTokens);
}

/// <summary>独立 vector index 存储；V1 仅用于基础设施、诊断与预览。</summary>
public interface IVectorIndexStore
{
    Task UpsertAsync(
        VectorIndexEntry entry,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string workspaceId,
        string collectionId,
        string entryId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorIndexEntry>> GetByItemIdAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorIndexEntry>> ListAsync(
        VectorIndexQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorIndexSearchResult>> SearchAsync(
        VectorIndexSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorIndexDiagnostic>> GetDiagnosticsAsync(
        VectorIndexQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>保存 vector reindex 执行报告，用于审计和 ControlRoom 只读展示。</summary>
public interface IVectorReindexReportStore
{
    Task SaveAsync(
        VectorReindexResult result,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorReindexResult>> QueryAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default);

    Task<VectorReindexResult?> GetAsync(
        string reportId,
        CancellationToken cancellationToken = default);
}

/// <summary>保存 lifecycle metadata review candidate；仅用于人工 review 队列，不写 sidecar 或正式 retrieval。</summary>
public interface IVectorLifecycleMetadataReviewCandidateStore
{
    Task SaveAsync(
        VectorLifecycleMetadataReviewCandidate candidate,
        CancellationToken cancellationToken = default);

    Task<VectorLifecycleMetadataReviewCandidate?> GetAsync(
        string candidateId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>> QueryAsync(
        VectorLifecycleMetadataReviewCandidateQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>保存 lifecycle metadata 人工 review 历史；不修改业务 source item。</summary>
public interface IVectorLifecycleMetadataReviewStore
{
    Task SaveAsync(
        VectorLifecycleMetadataReviewRecord record,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> ListAsync(
        string candidateId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>保存 lifecycle metadata sidecar override；只写旁路 metadata，不触碰正式 source item。</summary>
public interface IVectorLifecycleSidecarMetadataStore
{
    Task SaveAsync(
        VectorLifecycleSidecarMetadataEntry entry,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default);
}
