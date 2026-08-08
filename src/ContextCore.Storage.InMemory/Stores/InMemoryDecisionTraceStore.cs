using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>基于内存的决策记录 trace 存储，适用于测试和调试。</summary>
public sealed class InMemoryDecisionTraceStore : IDecisionTraceStore
{
    private readonly List<ContextDecisionRecord> _records = new();
    private readonly object _gate = new();

    public Task SaveAsync(
        ContextDecisionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _records.RemoveAll(item => string.Equals(item.DecisionId, record.DecisionId, StringComparison.OrdinalIgnoreCase));
            _records.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContextDecisionRecord>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = take > 0 ? take : 50;

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ContextDecisionRecord>>(_records
                .Where(item => string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.Equals(item.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAt)
                .Take(count)
                .ToArray());
        }
    }

    public Task<ContextDecisionRecord?> GetAsync(
        string workspaceId,
        string collectionId,
        string decisionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_records.FirstOrDefault(item =>
                string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.DecisionId, decisionId, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
