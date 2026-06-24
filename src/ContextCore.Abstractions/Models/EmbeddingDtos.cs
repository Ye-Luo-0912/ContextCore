namespace ContextCore.Abstractions.Models;

/// <summary>Embedding 输入的来源类型。</summary>
public enum EmbeddingInputKind
{
    /// <summary>普通文本。</summary>
    Text,
    /// <summary>上下文条目。</summary>
    ContextItem,
    /// <summary>记忆条目。</summary>
    MemoryItem,
    /// <summary>检索查询文本。</summary>
    Query,
    /// <summary>自定义来源。</summary>
    Custom
}

/// <summary>Embedding 后台任务状态。</summary>
public enum EmbeddingJobState
{
    /// <summary>已入队。</summary>
    Queued,
    /// <summary>运行中。</summary>
    Running,
    /// <summary>已成功完成。</summary>
    Succeeded,
    /// <summary>已失败。</summary>
    Failed,
    /// <summary>已取消。</summary>
    Cancelled
}

/// <summary>Embedding 请求，描述一批需要向量化的输入。</summary>
public sealed class EmbeddingRequest
{
    /// <summary>操作唯一标识符。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>期望使用的 embedding 模型名称（可选）。</summary>
    public string? ModelName { get; init; }

    /// <summary>输入来源类型。</summary>
    public EmbeddingInputKind InputKind { get; init; } = EmbeddingInputKind.Text;

    /// <summary>是否对输出向量做单位化。</summary>
    public bool Normalize { get; init; } = true;

    /// <summary>待向量化输入列表。</summary>
    public IReadOnlyList<EmbeddingInput> Inputs { get; init; } = Array.Empty<EmbeddingInput>();

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>单条 embedding 输入。</summary>
public sealed class EmbeddingInput
{
    /// <summary>输入唯一标识符。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>原始文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>来源引用，如 context item id、memory id 或 query id。</summary>
    public string SourceRef { get; init; } = string.Empty;

    /// <summary>输入标签。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>Embedding 请求的结果。</summary>
public sealed class EmbeddingResult
{
    /// <summary>对应请求的操作 ID。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>实际使用的 embedding 模型名称。</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>向量维度。</summary>
    public int Dimensions { get; init; }

    /// <summary>是否成功。</summary>
    public bool Succeeded { get; init; } = true;

    /// <summary>失败时错误信息（可选）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>每个输入对应的向量。</summary>
    public IReadOnlyList<EmbeddingVector> Vectors { get; init; } = Array.Empty<EmbeddingVector>();

    /// <summary>用量统计。</summary>
    public ContextOperationUsage Usage { get; init; } = new();

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>结果创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>单条 embedding 向量。</summary>
public sealed class EmbeddingVector
{
    /// <summary>对应输入 ID。</summary>
    public string InputId { get; init; } = string.Empty;

    /// <summary>来源引用。</summary>
    public string SourceRef { get; init; } = string.Empty;

    /// <summary>向量值。</summary>
    public IReadOnlyList<float> Values { get; init; } = Array.Empty<float>();

    /// <summary>向量范数（可选）。</summary>
    public double? Norm { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>向量存储中的单条记录。</summary>
public sealed class VectorRecord
{
    /// <summary>向量记录 ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>来源对象 ID。</summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>来源对象类型，如 context、memory、query。</summary>
    public string SourceKind { get; init; } = string.Empty;

    /// <summary>embedding 模型名称。</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>向量维度。</summary>
    public int Dimensions { get; init; }

    /// <summary>向量值。</summary>
    public IReadOnlyList<float> Vector { get; init; } = Array.Empty<float>();

    /// <summary>被向量化内容的哈希，用于缓存与去重。</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>标签列表。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>向量相似度查询条件。</summary>
public sealed class VectorQuery
{
    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>查询向量。</summary>
    public IReadOnlyList<float> Vector { get; init; } = Array.Empty<float>();

    /// <summary>最多返回数量。</summary>
    public int TopK { get; init; } = 10;

    /// <summary>最小相似度分数（可选）。</summary>
    public double? MinScore { get; init; }

    /// <summary>筛选来源对象类型。</summary>
    public IReadOnlyList<string> SourceKinds { get; init; } = Array.Empty<string>();

    /// <summary>必须包含的标签。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>返回结果中是否包含原始向量。</summary>
    public bool IncludeVector { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>向量查询结果。</summary>
public sealed class VectorSearchResult
{
    /// <summary>命中的向量记录。</summary>
    public VectorRecord Record { get; init; } = new();

    /// <summary>相似度分数。</summary>
    public double Score { get; init; }

    /// <summary>结果排序名次。</summary>
    public int Rank { get; init; }
}

/// <summary>Embedding 后台任务。</summary>
public sealed class EmbeddingJob
{
    /// <summary>任务 ID。</summary>
    public string JobId { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID（可选）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>任务状态。</summary>
    public EmbeddingJobState State { get; init; } = EmbeddingJobState.Queued;

    /// <summary>请求内容。</summary>
    public EmbeddingRequest Request { get; init; } = new();

    /// <summary>完成后的结果（可选）。</summary>
    public EmbeddingResult? Result { get; init; }

    /// <summary>失败时错误信息（可选）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>开始时间（可选）。</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>完成时间（可选）。</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}
