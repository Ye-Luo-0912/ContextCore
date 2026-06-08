using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>基于内存的 <see cref="IGlobalContextStore"/> 实现，适用于测试和短生命周期场景。</summary>
public sealed class InMemoryGlobalContextStore : IGlobalContextStore
{
    private readonly ConcurrentDictionary<string, ContextGlobalItem> _items = new();

    public Task SaveAsync(ContextGlobalItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Clone(item, string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id);
        _items[Key(normalized.WorkspaceId, normalized.Id)] = normalized;

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContextGlobalItem>> QueryAsync(
        ContextGlobalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var take = query.Take > 0 ? query.Take : 50;
        var results = _items.Values
            .Where(item => string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.CollectionId)
                || string.IsNullOrWhiteSpace(item.CollectionId)
                || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => query.Scope is null || item.Scope == query.Scope)
            .Where(item => query.Tags.Count == 0
                || query.Tags.All(tag => item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .Where(item => query.Types.Count == 0
                || query.Types.Any(type => string.Equals(type, item.Type, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.Importance)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(take)
            .Select(item => Clone(item))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextGlobalItem>>(results);
    }

    private static ContextGlobalItem Clone(ContextGlobalItem item, string? id = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ContextGlobalItem
        {
            Id = id ?? item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Scope = item.Scope,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = [.. item.Tags],
            SourceRefs = [.. item.SourceRefs],
            Importance = item.Importance,
            Version = item.Version <= 0 ? 1 : item.Version,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    private static string Key(string workspaceId, string id)
    {
        return $"{workspaceId}\u001f{id}";
    }
}
