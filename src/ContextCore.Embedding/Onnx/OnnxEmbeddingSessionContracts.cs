namespace ContextCore.Embedding;

/// <summary>ONNX embedding 会话抽象，隔离具体推理库。</summary>
public interface IOnnxEmbeddingSession : IAsyncDisposable
{
    /// <summary>模型名称。</summary>
    string ModelName { get; }

    /// <summary>输出向量维度。</summary>
    int Dimensions { get; }

    /// <summary>执行一批文本 embedding。</summary>
    Task<IReadOnlyList<IReadOnlyList<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}

/// <summary>创建 ONNX embedding 会话的工厂。</summary>
public interface IOnnxEmbeddingSessionFactory
{
    Task<IOnnxEmbeddingSession> CreateAsync(
        EmbeddingOptions options,
        CancellationToken cancellationToken = default);
}
