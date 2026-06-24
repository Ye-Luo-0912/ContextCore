using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.ModelGateway.Infrastructure;

/// <summary>将模型用量日志保存在内存中的 <see cref="IModelUsageLogStore"/> 实现，最多保存1000条记录。</summary>
public sealed class InMemoryModelUsageLogStore : IModelUsageLogStore
{
    private readonly ConcurrentQueue<ModelUsageLog> _logs = new();

    public Task SaveAsync(ModelUsageLog log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        cancellationToken.ThrowIfCancellationRequested();

        _logs.Enqueue(log);
        while (_logs.Count > 1_000 && _logs.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModelUsageLog>> QueryRecentAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        take = take > 0 ? take : 50;
        var logs = _logs
            .OrderByDescending(log => log.CreatedAt)
            .Take(take)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ModelUsageLog>>(logs);
    }
}
