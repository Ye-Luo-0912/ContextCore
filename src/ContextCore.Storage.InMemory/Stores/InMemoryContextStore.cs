using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>
/// 基于内存的 <see cref="IContextStore"/> 与 <see cref="IContextCollectionStore"/> 实现，
/// 适用于测试和短生命周期场景。
/// </summary>
public sealed class InMemoryContextStore : IContextStore, IContextCollectionStore, IContextStoreBatchLookup, IContextQueryPageStore
{
    private readonly ConcurrentDictionary<string, ContextCollection> _collections = new();
    private readonly ConcurrentDictionary<string, ContextItem> _items = new();

    public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        _items[ItemKey(item.WorkspaceId, item.CollectionId, item.Id)] = Clone(item);

        return Task.CompletedTask;
    }

    public Task<ContextItem?> GetAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _items.TryGetValue(ItemKey(workspaceId, collectionId, id), out var item)
                ? Clone(item)
                : null);
    }

    public Task<IReadOnlyList<ContextItem>> BatchGetAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();

        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ContextItem>>(Array.Empty<ContextItem>());
        }

        var results = new List<ContextItem>(ids.Count);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (_items.TryGetValue(ItemKey(workspaceId, collectionId, id), out var item))
            {
                results.Add(Clone(item));
            }
        }

        return Task.FromResult<IReadOnlyList<ContextItem>>(results);
    }

    public Task<IReadOnlyList<ContextItem>> QueryAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var skip = query.After is null ? Math.Max(0, query.Skip) : 0;
        var results = _items.Values
            .Where(item => string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.CollectionId)
                || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => !IsExcluded(item, query))
            .Where(item => MatchesTags(item, query.Tags))
            .Where(item => MatchesTypes(item, query.Types))
            .Where(item => MatchesRefs(item, query.Refs))
            .Where(item => MatchesQueryText(item, query.QueryText))
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .Where(item => IsAfterCursor(item, query.After))
            .Skip(skip)
            .Take(query.Take > 0 ? query.Take : 50)
            .Select(item => query.IncludeContent ? Clone(item) : Clone(item, content: string.Empty))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextItem>>(results);
    }

    /// <inheritdoc />
    public Task<ContextQueryPageResult> QueryPageAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        // 取 Take + 1 条判定 HasMore；返回前 Take 条，下一页游标取自末条排序键。
        var take = query.Take > 0 ? query.Take : 50;
        var fetchQuery = query.CloneWith(take: take + 1);
        var items = QueryAsync(fetchQuery, cancellationToken).GetAwaiter().GetResult().ToList();

        var hasMore = items.Count > take;
        var pageItems = hasMore ? items.Take(take).ToList() : items;

        if (pageItems.Count == 0)
        {
            return Task.FromResult(new ContextQueryPageResult { Items = pageItems, HasMore = false, NextCursor = null });
        }

        // 内存存储按 (updated_at, id) 排序——等价于 ID 命中源（SourceOrder=1，无 ts_rank）。
        var last = pageItems[^1];
        var nextCursor = new ContextQueryCursor
        {
            SourceOrder = 1,
            TsRank = 0,
            Importance = last.Importance,
            UpdatedAt = last.UpdatedAt,
            Id = last.Id
        };

        return Task.FromResult(new ContextQueryPageResult
        {
            Items = pageItems,
            HasMore = hasMore,
            NextCursor = hasMore ? nextCursor : null
        });
    }

    /// <summary>
    /// Keyset 续取过滤：仅保留排序上严格位于游标之后的条目。
    /// 内存存储无 ts_rank/importance 排序，按 (updated_at, id) 近似续取（id 为决胜键）。
    /// </summary>
    private static bool IsAfterCursor(ContextItem item, ContextQueryCursor? after)
    {
        if (after is null)
        {
            return true;
        }

        var timeCmp = item.UpdatedAt.CompareTo(after.UpdatedAt);
        if (timeCmp != 0)
        {
            return timeCmp < 0;
        }
        return string.CompareOrdinal(item.Id, after.Id) < 0;
    }

    private static bool IsExcluded(ContextItem item, ContextQuery query)
    {
        if (query.ExcludedIds.Any(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (query.ExcludedTypes.Any(type => string.Equals(type, item.Type, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !query.IncludeDerived
            && item.Metadata.TryGetValue("isDerived", out var isDerived)
            && string.Equals(isDerived, "true", StringComparison.OrdinalIgnoreCase);
    }

    public Task DeleteAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _items.TryRemove(ItemKey(workspaceId, collectionId, id), out _);

        return Task.CompletedTask;
    }

    public Task SaveCollectionAsync(
        ContextCollection collection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        cancellationToken.ThrowIfCancellationRequested();

        _collections[CollectionKey(collection.WorkspaceId, collection.Id)] = Clone(collection);

        return Task.CompletedTask;
    }

    public Task<ContextCollection?> GetCollectionAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _collections.TryGetValue(CollectionKey(workspaceId, collectionId), out var collection)
                ? Clone(collection)
                : null);
    }

    private static bool MatchesTags(ContextItem item, IReadOnlyList<string> queryTags)
    {
        if (queryTags.Count == 0)
        {
            return true;
        }

        return queryTags.All(queryTag => item.Tags.Any(tag => string.Equals(tag, queryTag, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool MatchesTypes(ContextItem item, IReadOnlyList<string> queryTypes)
    {
        return queryTypes.Count == 0
            || queryTypes.Any(type => string.Equals(type, item.Type, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesRefs(ContextItem item, IReadOnlyList<string> queryRefs)
    {
        return queryRefs.Count == 0 || queryRefs.Any(queryRef => ContainsRef(item, queryRef));
    }

    private static bool ContainsRef(ContextItem item, string queryRef)
    {
        return string.Equals(item.Id, queryRef, StringComparison.OrdinalIgnoreCase)
            || item.Refs.Any(itemRef => string.Equals(itemRef, queryRef, StringComparison.OrdinalIgnoreCase))
            || item.SourceRefs.Any(sourceRef => string.Equals(sourceRef, queryRef, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesQueryText(ContextItem item, string? queryText)
        => ContextQueryTextMatcher.Matches(item, queryText);

    private static ContextItem Clone(ContextItem item, string? content = null)
    {
        // 内存实现也返回副本，避免调用方修改对象引用后绕过 Store 的写入路径。
        return new ContextItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Title = item.Title,
            Content = content ?? item.Content,
            ContentFormat = item.ContentFormat,
            Tags = [.. item.Tags],
            Refs = [.. item.Refs],
            SourceRefs = [.. item.SourceRefs],
            Metadata = new Dictionary<string, string>(item.Metadata),
            Importance = item.Importance,
            Version = item.Version,
            Checksum = item.Checksum,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static ContextCollection Clone(ContextCollection collection)
    {
        return new ContextCollection
        {
            Id = collection.Id,
            WorkspaceId = collection.WorkspaceId,
            Name = collection.Name,
            Description = collection.Description,
            Metadata = new Dictionary<string, string>(collection.Metadata),
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt
        };
    }

    private static string ItemKey(string workspaceId, string collectionId, string id)
    {
        // 使用不可见分隔符减少普通 id 文本与复合键格式冲突的概率。
        return $"{workspaceId}\u001f{collectionId}\u001f{id}";
    }

    private static string CollectionKey(string workspaceId, string collectionId)
    {
        return $"{workspaceId}\u001f{collectionId}";
    }
}
