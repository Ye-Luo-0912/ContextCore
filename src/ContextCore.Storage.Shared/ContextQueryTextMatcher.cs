using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Shared;

/// <summary>
/// 上下文条目的 QueryText 匹配：整句命中，或任一词元（含中文二元组）命中。
/// FileSystem 与 InMemory 共用，避免自然语言问句因整段 substring 而漏召回。
/// </summary>
public static class ContextQueryTextMatcher
{
    private const int MaxQueryTerms = 12;

    public static bool Matches(ContextItem item, string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return true;
        }

        var normalizedQuery = queryText.Trim();
        return MatchesTerm(item, normalizedQuery)
               || ExtractTerms(normalizedQuery).Any(term => MatchesTerm(item, term));
    }

    private static bool MatchesTerm(ContextItem item, string queryText)
    {
        return Contains(item.Id, queryText)
            || Contains(item.Title, queryText)
            || Contains(item.Type, queryText)
            || Contains(item.Content, queryText)
            || item.Tags.Any(tag => Contains(tag, queryText))
            || item.Refs.Any(itemRef => Contains(itemRef, queryText))
            || item.SourceRefs.Any(sourceRef => Contains(sourceRef, queryText));
    }

    private static IEnumerable<string> ExtractTerms(string queryText)
    {
        var count = 0;
        foreach (var term in SplitTerms(queryText))
        {
            yield return term;
            if (++count >= MaxQueryTerms)
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
                if (++count >= MaxQueryTerms)
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

    private static bool IsCjk(char ch) => ch is >= '\u4e00' and <= '\u9fff';

    private static bool ContainsCjk(string text) => text.Any(IsCjk);

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
        => value?.Contains(queryText, StringComparison.OrdinalIgnoreCase) == true;
}
