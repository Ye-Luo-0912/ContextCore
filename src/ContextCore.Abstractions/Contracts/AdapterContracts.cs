namespace ContextCore.Abstractions;

/// <summary>检索适配器请求，包含上下文信息。</summary>
public sealed class RetrievalAdapterRequest
{
    public string OperationId { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = string.Empty;
    public string CollectionId { get; init; } = string.Empty;
    public string QueryText { get; init; } = string.Empty;
    public IReadOnlyList<string> BaselineCandidateIds { get; init; } = Array.Empty<string>();
}

/// <summary>检索适配器结果。</summary>
public sealed class RetrievalAdapterResult
{
    public bool Applied { get; init; }
    public IReadOnlyList<string> AddedCandidateIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RemovedCandidateIds { get; init; } = Array.Empty<string>();
    public string TracePath { get; init; } = string.Empty;
}

/// <summary>检索适配器接缝。主适配器在 pipeline 中调用，可修改候选集。</summary>
public interface IContextRetrievalAdapter
{
    string Name { get; }
    Task<RetrievalAdapterResult> ExecuteAsync(RetrievalAdapterRequest request, CancellationToken cancellationToken = default);
}

/// <summary>影子追踪适配器。仅记录和追踪，不修改候选集。</summary>
public interface IShadowRetrievalAdapter : IContextRetrievalAdapter { }