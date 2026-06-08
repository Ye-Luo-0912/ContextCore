using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory;

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

        var itemTags = item.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return queryTags.All(itemTags.Contains);
    }

    private static bool MatchesTypes(ContextItem item, IReadOnlyList<string> queryTypes)
    {
        return queryTypes.Count == 0
            || queryTypes.Any(type => string.Equals(type, item.Type, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesRefs(ContextItem item, IReadOnlyList<string> queryRefs)
    {
        if (queryRefs.Count == 0)
        {
            return true;
        }

        var refs = item.Refs
            .Concat(item.SourceRefs)
            .Append(item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return queryRefs.Any(refs.Contains);
    }

    private static readonly string[] ChineseKeywords =
    [
        "语言偏好", "语言", "偏好", "输出", "中文", "英文", "开发", "任务", "计划",
        "运行", "配置", "持久化", "后端", "存储", "生产", "生产后端", "林风", "苍穹",
        "大陆", "苍穹大陆", "拍卖行", "九转金丹", "金丹", "药引", "龙魂草", "人设", "剧情",
        "大纲", "工作流", "错误", "恢复", "接口", "单元测试", "测试", "设定", "密钥", "仓库",
        "明文", "安全", "本地"
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "的", "了", "在", "是", "我", "你", "他", "她", "它", "们", "这", "那", "都", "和", "与", "或", "而", "何", "如",
        "如何", "什么", "怎么", "哪个", "哪里", "为什么", "谁", "几", "多少", "是吗", "以", "去", "来", "个", "只", "条",
        "吗", "吧", "呢", "呀", "这", "那", "的", "地", "得"
    };

    private static bool ContainsChinese(string text)
    {
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF)
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> SegmentChinese(string text)
    {
        foreach (var kw in ChineseKeywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                yield return kw;
            }
        }

        var sb = new System.Text.StringBuilder();
        var chSegments = new List<string>();
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF)
            {
                sb.Append(c);
            }
            else
            {
                if (sb.Length > 0)
                {
                    chSegments.Add(sb.ToString());
                    sb.Clear();
                }
            }
        }
        if (sb.Length > 0)
        {
            chSegments.Add(sb.ToString());
        }

        foreach (var seg in chSegments)
        {
            if (seg.Length < 2)
            {
                continue;
            }

            if (seg.Length <= 4)
            {
                if (!StopWords.Contains(seg))
                {
                    yield return seg;
                }
            }

            for (int i = 0; i < seg.Length - 1; i++)
            {
                var bigram = seg.Substring(i, 2);
                if (StopWords.Contains(bigram))
                {
                    continue;
                }
                var containsStopChar = false;
                foreach (var c in bigram)
                {
                    if (StopWords.Contains(c.ToString()))
                    {
                        containsStopChar = true;
                        break;
                    }
                }
                if (!containsStopChar)
                {
                    yield return bigram;
                }
            }
        }
    }

    private static bool MatchesQueryText(ContextItem item, string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return true;
        }

        var words = ExtractQueryTerms(queryText).ToArray();
        if (words.Length > 0)
        {
            return words.Any(word =>
                Contains(item.Title, word)
                || Contains(item.Type, word)
                || Contains(item.Content, word)
                || item.Tags.Any(tag => Contains(tag, word)));
        }

        return Contains(item.Title, queryText)
            || Contains(item.Type, queryText)
            || Contains(item.Content, queryText)
            || item.Tags.Any(tag => Contains(tag, queryText));
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
            if (ContainsChinese(term))
            {
                foreach (var chTerm in SegmentChinese(term))
                {
                    yield return chTerm;
                    if (++count >= 12)
                    {
                        yield break;
                    }
                }
            }
            else
            {
                yield return term;
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
            Tags = item.Tags.ToArray(),
            Refs = item.Refs.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
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
