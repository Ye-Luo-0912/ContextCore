using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>内存版 Router shadow trace 存储，适用于测试和本地调试。</summary>
public sealed class InMemoryRouterIntentShadowTraceStore : IRouterIntentShadowTraceStore
{
    private readonly object _gate = new();
    private readonly List<RouterIntentShadowTrace> _traces = new();

    public Task SaveAsync(
        RouterIntentShadowTrace trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _traces.RemoveAll(item => string.Equals(item.RequestId, trace.RequestId, StringComparison.OrdinalIgnoreCase));
            _traces.Add(trace);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RouterIntentShadowTrace>> QueryAsync(
        RouterIntentShadowTraceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var take = query.Take > 0 ? query.Take : 50;
        lock (_gate)
        {
            var rows = _traces
                .Where(item => Matches(query.WorkspaceId, item.WorkspaceId))
                .Where(item => Matches(query.CollectionId, item.CollectionId))
                .Where(item => Matches(query.EntryPoint, item.EntryPoint))
                .OrderByDescending(item => item.CreatedAt)
                .Take(take)
                .ToArray();
            return Task.FromResult<IReadOnlyList<RouterIntentShadowTrace>>(rows);
        }
    }

    private static bool Matches(string? expected, string? actual)
    {
        return string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }
}
