using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>
/// 基于内存的 <see cref="IContextStore"/> 与 <see cref="IContextCollectionStore"/> 实现，
/// 适用于测试和短生命周期场景。
/// </summary>
public sealed class InMemoryContextStore : IContextStore, IContextCollectionStore
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

    public Task<IReadOnlyList<ContextItem>> QueryAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

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
            .Skip(Math.Max(0, query.Skip))
            .Take(query.Take > 0 ? query.Take : 50)
            .Select(item => query.IncludeContent ? Clone(item) : Clone(item, content: string.Empty))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextItem>>(results);
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
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return true;
        }

        var normalizedQuery = queryText.Trim();
        return MatchesQueryTerm(item, normalizedQuery) || ExtractQueryTerms(normalizedQuery).Any(term => MatchesQueryTerm(item, term));
    }

    private static bool MatchesQueryTerm(ContextItem item, string queryText)
    {
        return Contains(item.Id, queryText)
            || Contains(item.Title, queryText)
            || Contains(item.Type, queryText)
            || Contains(item.Content, queryText)
            || item.Tags.Any(tag => Contains(tag, queryText))
            || item.Refs.Any(itemRef => Contains(itemRef, queryText))
            || item.SourceRefs.Any(sourceRef => Contains(sourceRef, queryText));
    }

    private static IEnumerable<string> ExtractQueryTerms(string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            yield break;
        }

        var count = 0;
        foreach (var term in SplitTerms(queryText))
        {
            yield return term;
            if (++count >= 12)
            {
                yield break;
            }

            if (!ContainsCjk(term))
            {
                continue;
            }

            foreach (var bigram in EnumerateCjkBigrams(term))
            {
                yield return bigram;
                if (++count >= 12)
                {
                    yield break;
                }
            }
        }
    }

    private static IEnumerable<string> SplitTerms(string text)
    {
        var current = new List<char>();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || IsCjk(ch))
            {
                current.Add(ch);
                continue;
            }

            foreach (var term in Flush(current))
            {
                yield return term;
            }
        }

        foreach (var term in Flush(current))
        {
            yield return term;
        }
    }

    private static IEnumerable<string> Flush(List<char> buffer)
    {
        if (buffer.Count == 0)
        {
            yield break;
        }

        var text = new string(buffer.ToArray()).Trim();
        buffer.Clear();
        if (text.Length >= 2)
        {
            yield return text;
        }
    }

    private static bool IsCjk(char ch)
    {
        return ch is >= '\u4e00' and <= '\u9fff';
    }

    private static bool ContainsCjk(string text)
    {
        return text.Any(IsCjk);
    }

    private static IEnumerable<string> EnumerateCjkBigrams(string text)
    {
        for (var index = 0; index < text.Length - 1; index++)
        {
            if (IsCjk(text[index]) && IsCjk(text[index + 1]))
            {
                yield return text.Substring(index, 2);
            }
        }
    }

    private static bool Contains(string? value, string queryText)
    {
        return value?.Contains(queryText, StringComparison.OrdinalIgnoreCase) == true;
    }

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
