using System.Text.RegularExpressions;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Retrieval;

// 从成功工具观察里抽出还没搜过的实体词。
// 不用整段结果当问句，避免 found/notes 这类套话跟任务词一起 OR 命中。

internal static class ObservationQueryText
{
    internal const int MaxSnippetChars = 240;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "的", "了", "是", "在", "我", "有", "和", "就", "不", "人", "都", "一", "一个", "这个", "那个", "我们", "你们", "他们",
        "the", "a", "an", "of", "to", "in", "on", "for", "with", "and", "or", "is", "are", "was", "were", "be", "it", "this", "that"
    };

    public static IEnumerable<string> DistinctiveQueries(
        string alreadyCovered,
        IReadOnlyList<ToolObservation>? observations)
    {
        if (observations is null || observations.Count == 0)
        {
            yield break;
        }

        var covered = alreadyCovered ?? string.Empty;
        foreach (var observation in observations)
        {
            if (observation is null || !observation.Succeeded)
            {
                continue;
            }

            var distinctive = Distinctive(covered, observation.Result);
            if (distinctive.Length == 0)
            {
                continue;
            }

            yield return distinctive;
            covered = covered + " " + distinctive;
        }
    }

    public static string Distinctive(string alreadyCovered, string? observationResult)
    {
        var snippet = BoundSnippet(observationResult);
        if (snippet.Length == 0)
        {
            return string.Empty;
        }

        var covered = alreadyCovered ?? string.Empty;
        var distinctive = new List<string>();
        var entityLike = new List<string>();
        foreach (var term in SplitTerms(snippet))
        {
            if (term.Length < 2 || StopWords.Contains(term) || covered.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            distinctive.Add(term);
            if (LooksLikeEntity(term))
            {
                entityLike.Add(term);
            }
        }

        var chosen = entityLike.Count > 0 ? entityLike : distinctive;
        return string.Join(" ", chosen);
    }

    private static bool LooksLikeEntity(string term)
    {
        foreach (var ch in term)
        {
            if (char.IsDigit(ch) || ch is '-' or '_')
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SplitTerms(string text)
    {
        foreach (var term in Regex.Split(text, @"[^\p{L}\p{N}_\-]+"))
        {
            if (term.Length > 0)
            {
                yield return term;
            }
        }
    }

    private static string BoundSnippet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = new List<char>(Math.Min(value.Length, MaxSnippetChars));
        var pendingSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = chars.Count > 0;
                continue;
            }

            if (pendingSpace)
            {
                chars.Add(' ');
                pendingSpace = false;
            }

            chars.Add(ch);
            if (chars.Count >= MaxSnippetChars)
            {
                break;
            }
        }

        return new string(chars.ToArray());
    }
}
