using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的检索 trace 存储，适用于测试和调试。</summary>
public sealed class InMemoryRetrievalTraceStore : IRetrievalTraceStore
{
    private readonly List<ContextRetrievalTrace> _traces = new();
    private readonly object _gate = new();

    public Task SaveAsync(
        ContextRetrievalTrace trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _traces.RemoveAll(item => string.Equals(item.RetrievalId, trace.RetrievalId, StringComparison.OrdinalIgnoreCase));
            _traces.Add(trace);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContextRetrievalTrace>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = take > 0 ? take : 50;

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ContextRetrievalTrace>>(_traces
                .Where(item => string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.Equals(item.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAt)
                .Take(count)
                .ToArray());
        }
    }
}
