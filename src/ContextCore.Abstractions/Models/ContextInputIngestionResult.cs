namespace ContextCore.Abstractions.Models;

/// <summary>
/// 输入层摄取结果，供 admin ingest path 返回详细的幂等、顺序和持久化信息。
/// </summary>
public sealed class ContextInputIngestionResult
{
    public ContextItem Item { get; init; } = new();

    public bool Created { get; init; }

    public bool Deduped { get; init; }

    public string ContentHash { get; init; } = string.Empty;

    public long SequenceId { get; init; }

    public string OperationId { get; init; } = string.Empty;
}
