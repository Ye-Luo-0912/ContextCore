using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 短期上下文筛选器：用低成本规则从最近原始上下文中保留当前任务相关内容。
/// 该组件位于打包热路径，避免模型调用、正则回溯和全量长文本扫描。
/// </summary>
public sealed class RecentContextFilter
{
    private const int MaxQueryTerms = 12;
    private const int MaxScannedCharacters = 1024;
    private const double MinimumRelevantScore = 0.18;

    /// <summary>
    /// 按相关性和时序权重筛选最近上下文，同时返回被排除项的原因。
    /// </summary>
    public IReadOnlyList<RecentContextItem> Filter(
        IEnumerable<ContextItem> items,
        ContextPackageRequest request,
        int take,
        DateTimeOffset? now = null,
        IReadOnlyList<ContextAnchor>? anchors = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(request);

        var timestamp = now ?? DateTimeOffset.UtcNow;
        var queryTerms = ExtractQueryTerms(request.QueryText).ToArray();
        var requiredTags = request.RequiredTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredTypes = request.RequiredTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maxTake = take > 0 ? take : 20;

        return [.. items
            .Select(item => ScoreItem(item, request, queryTerms, requiredTags, requiredTypes, anchors, timestamp))
            .OrderByDescending(item => item.ExcludeReason is null)
            .ThenByDescending(item => item.Relevance)
            .ThenByDescending(item => item.RecencyWeight)
            .ThenBy(item => item.SourceItemId, StringComparer.OrdinalIgnoreCase)
            .Take(maxTake)];
    }

    private static RecentContextItem ScoreItem(
        ContextItem item,
        ContextPackageRequest request,
        IReadOnlyList<string> queryTerms,
        ISet<string> requiredTags,
        ISet<string> requiredTypes,
        IReadOnlyList<ContextAnchor>? anchors,
        DateTimeOffset now)
    {
        var recencyWeight = CalculateRecencyWeight(item.UpdatedAt, now);
        var containsDeprecatedKeywordInQuery = !string.IsNullOrWhiteSpace(request.QueryText) && (
            request.QueryText.Contains("废弃", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("作废", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
        );

        var isDeprecated = item.Tags.Any(tag =>
            string.Equals(tag, "deprecated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tag, "legacy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tag, "superseded", StringComparison.OrdinalIgnoreCase))
            || (item.Metadata.TryGetValue("status", out var statusVal) && (
                string.Equals(statusVal, "deprecated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(statusVal, "legacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(statusVal, "superseded", StringComparison.OrdinalIgnoreCase)
            ));

        if (!containsDeprecatedKeywordInQuery && isDeprecated)
        {
            return new RecentContextItem
            {
                SourceItemId = item.Id,
                Content = item.Content,
                ContentFormat = item.ContentFormat,
                SourceTurnId = ResolveSourceTurnId(item),
                Relevance = 0.0,
                RecencyWeight = recencyWeight,
                Reason = "该上下文已被标记为废弃或遗留",
                ExcludeReason = "该上下文已被标记为废弃或遗留",
                SourceRefs = ResolveSourceRefs(item)
            };
        }

        var relevance = item.Importance * 0.2 + recencyWeight * 0.25;
        var reasons = new List<string>();

        if (MatchesRequiredTypes(item, requiredTypes))
        {
            relevance += 0.2;
            reasons.Add("匹配请求类型");
        }

        var tagMatches = CountTagMatches(item, requiredTags);
        if (tagMatches > 0)
        {
            relevance += Math.Min(0.25, tagMatches * 0.08);
            reasons.Add("匹配请求标签");
        }

        var queryMatches = CountQueryTermMatches(item, queryTerms);
        if (queryMatches > 0)
        {
            relevance += Math.Min(0.35, queryMatches * 0.08);
            reasons.Add("匹配当前输入关键词");
        }

        if (IsCurrentTaskSignal(item, request))
        {
            relevance += 0.18;
            reasons.Add("包含当前任务或运行时信号");
        }

        // 结合 anchors 进行匹配大加分
        if (anchors is not null && anchors.Count > 0)
        {
            var searchTxt = CreateSearchText(item);
            var anchorMatchCount = 0;
            foreach (var anchor in anchors)
            {
                if (searchTxt.Contains(anchor.Name, StringComparison.OrdinalIgnoreCase))
                {
                    relevance += anchor.Weight * 0.10; // 结合 Anchor 权重加成，合理加分
                    anchorMatchCount++;
                }
            }
            if (anchorMatchCount > 0)
            {
                reasons.Add($"匹配 {anchorMatchCount} 个语义锚点");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.QueryText) && reasons.Count == 0)
        {
            relevance = 0.0;
        }
        else
        {
            relevance = Math.Clamp(relevance, 0, 1);
        }

        if (isDeprecated && item.Importance < 0.25)
        {
            relevance -= 0.20;
        }

        var minScore = isDeprecated ? 0.45 : MinimumRelevantScore;
        var excludeReason = relevance < minScore
            ? "与当前输入、标签、类型和近期任务信号相关性不足"
            : null;

        return new RecentContextItem
        {
            SourceItemId = item.Id,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            SourceTurnId = ResolveSourceTurnId(item),
            Relevance = relevance,
            RecencyWeight = recencyWeight,
            Reason = reasons.Count == 0 ? "按近期上下文保留" : string.Join("；", reasons),
            ExcludeReason = excludeReason,
            SourceRefs = ResolveSourceRefs(item)
        };
    }

    private static bool MatchesRequiredTypes(ContextItem item, ISet<string> requiredTypes)
    {
        return requiredTypes.Count > 0
            && requiredTypes.Contains(item.Type);
    }

    private static int CountTagMatches(ContextItem item, ISet<string> requiredTags)
    {
        return requiredTags.Count == 0
            ? 0
            : item.Tags.Count(requiredTags.Contains);
    }

    private static int CountQueryTermMatches(ContextItem item, IReadOnlyList<string> queryTerms)
    {
        if (queryTerms.Count == 0)
        {
            return 0;
        }

        var haystack = CreateSearchText(item);
        return queryTerms.Count(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCurrentTaskSignal(ContextItem item, ContextPackageRequest request)
    {
        if (item.Metadata.TryGetValue("currentTask", out var currentTask)
            && string.Equals(currentTask, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (item.Metadata.TryGetValue("taskId", out var taskId)
            && request.Metadata.TryGetValue("taskId", out var requestTaskId))
        {
            return string.Equals(taskId, requestTaskId, StringComparison.OrdinalIgnoreCase);
        }

        return item.Tags.Any(tag =>
            string.Equals(tag, "current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tag, "runtime", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tag, "pending", StringComparison.OrdinalIgnoreCase));
    }

    private static double CalculateRecencyWeight(DateTimeOffset updatedAt, DateTimeOffset now)
    {
        if (updatedAt == default)
        {
            return 0;
        }

        var ageHours = Math.Max(0, (now - updatedAt).TotalHours);
        return Math.Clamp(1 / (1 + ageHours / 24), 0, 1);
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var c in text)
        {
            if (IsCjk(c))
            {
                return true;
            }
        }
        return false;
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

    private static bool IsCjk(char ch)
    {
        return ch is >= '\u4e00' and <= '\u9fff';
    }

    private static string CreateSearchText(ContextItem item)
    {
        var content = item.Content.Length <= MaxScannedCharacters
            ? item.Content
            : item.Content[..MaxScannedCharacters];

        return string.Join(
            ' ',
            item.Id,
            item.Type,
            item.Title,
            string.Join(' ', item.Tags),
            content);
    }

    private static string? ResolveSourceTurnId(ContextItem item)
    {
        return item.Metadata.TryGetValue("sourceTurnId", out var sourceTurnId)
            ? sourceTurnId
            : item.Metadata.GetValueOrDefault("turnId");
    }

    private static IReadOnlyList<string> ResolveSourceRefs(ContextItem item)
    {
        return item.SourceRefs.Count > 0
            ? [.. item.SourceRefs]
            : string.IsNullOrWhiteSpace(item.Id)
                ? Array.Empty<string>()
                : new[] { item.Id };
    }
}
