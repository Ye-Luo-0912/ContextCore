using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>基于内存的 <see cref="IContextIndex"/> 实现，适用于测试和短生命周期场景。</summary>
public sealed class InMemoryContextIndex : IContextIndex
{
    private readonly ConcurrentDictionary<string, ContextIndexEntry> _entries = new();

    public Task UpsertAsync(ContextIndexEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        _entries[EntryKey(entry.WorkspaceId, entry.CollectionId, entry.Id)] = Clone(entry);

        return Task.CompletedTask;
    }

    // TODO-DEMO [P0-3]：当前仅支持关键词 Contains 匹配，不支持语义向量搜索。
    // 生产使用前需接入 embedding 模型，存储向量并实现相似度检索。参见：TODO.md → P0-3
    public Task<IReadOnlyList<ContextIndexEntry>> SearchAsync(
        IndexQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var results = _entries.Values
            .Where(entry => string.Equals(entry.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.CollectionId)
                || string.Equals(entry.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(entry => MatchesKey(entry, query.Key))
            .Where(entry => MatchesKind(entry, query.Kind))
            .Where(entry => MatchesTags(entry, query.Tags))
            .OrderByDescending(entry => entry.Weight)
            .ThenByDescending(entry => entry.CreatedAt)
            .Take(query.Take > 0 ? query.Take : 50)
            .Select(Clone)
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextIndexEntry>>(results);
    }

    private static bool MatchesKey(ContextIndexEntry entry, string? key)
    {
        return string.IsNullOrWhiteSpace(key)
            || entry.Key.Contains(key, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesKind(ContextIndexEntry entry, string? kind)
    {
        return string.IsNullOrWhiteSpace(kind)
            || string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesTags(ContextIndexEntry entry, IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return true;
        }

        var tagSet = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return string.Equals(entry.Kind, "tag", StringComparison.OrdinalIgnoreCase)
            && tagSet.Contains(entry.Key);
    }

    private static ContextIndexEntry Clone(ContextIndexEntry entry)
    {
        return new ContextIndexEntry
        {
            Id = entry.Id,
            WorkspaceId = entry.WorkspaceId,
            CollectionId = entry.CollectionId,
            Key = entry.Key,
            Kind = entry.Kind,
            ContextRefs = [.. entry.ContextRefs],
            Weight = entry.Weight,
            Metadata = new Dictionary<string, string>(entry.Metadata),
            CreatedAt = entry.CreatedAt
        };
    }

    private static string EntryKey(string workspaceId, string collectionId, string id)
    {
        return $"{workspaceId}\u001f{collectionId}\u001f{id}";
    }
}
