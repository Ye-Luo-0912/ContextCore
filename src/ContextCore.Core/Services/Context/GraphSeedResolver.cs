using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 从工作记忆和锚点中提取图谱扩展种子 ID。
/// 所有方法均为纯函数，不持有状态。
/// </summary>
internal static class GraphSeedResolver
{
    internal static IEnumerable<string> ExtractGraphSeedCandidates(
        IReadOnlyList<ContextMemoryItem> workingMemory,
        IReadOnlyList<ContextAnchor> anchors)
    {
        foreach (var memory in workingMemory.Take(8))
        {
            foreach (var sourceRef in memory.SourceRefs.Take(16))
            {
                yield return sourceRef;
            }

            foreach (var value in ExtractGraphMetadataValues(memory.Metadata).Take(24))
            {
                yield return value;
            }

            foreach (var marker in ExtractPrefixedGraphSeeds(memory.Content).Take(24))
            {
                yield return marker;
            }
        }

        foreach (var anchor in anchors
            .Where(anchor => anchor.Type is AnchorType.Entity or AnchorType.Project or AnchorType.Topic)
            .Take(12))
        {
            yield return anchor.Name;
            foreach (var alias in anchor.Aliases.Take(4))
            {
                yield return alias;
            }
        }
    }

    internal static string? NormalizeGraphSeedCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var value = candidate.Trim().Trim('.', '。', ':', '：', ',', '，', ';', '；');
        while (TryStripGraphSeedPrefix(value, out var stripped))
        {
            value = stripped;
        }

        if (value.Length is < 2 or > 128
            || value.Contains("://", StringComparison.Ordinal)
            || value.Any(char.IsWhiteSpace)
            || IsGenericGraphSeed(value))
        {
            return null;
        }

        return value.All(IsGraphSeedChar) ? value : null;
    }

    private static IEnumerable<string> ExtractGraphMetadataValues(IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var (key, value) in metadata)
        {
            if (!IsGraphSeedMetadataKey(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var part in SplitGraphSeedList(value))
            {
                yield return part;
            }
        }
    }

    private static bool IsGraphSeedMetadataKey(string key)
    {
        return key.Contains("entity", StringComparison.OrdinalIgnoreCase)
            || key.Contains("node", StringComparison.OrdinalIgnoreCase)
            || key.Contains("context", StringComparison.OrdinalIgnoreCase)
            || key.Contains("ref", StringComparison.OrdinalIgnoreCase)
            || key.Contains("source", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitGraphSeedList(string value)
    {
        return value.Split(
                [',', '，', ';', '；', '|', '\r', '\n', '\t', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part));
    }

    private static IEnumerable<string> ExtractPrefixedGraphSeeds(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        foreach (var token in content.Split(
            [' ', '\t', '\r', '\n', ',', '，', ';', '；', '(', ')', '（', '）', '[', ']', '【', '】', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (HasGraphSeedPrefix(token))
            {
                yield return token;
            }
        }
    }

    private static bool HasGraphSeedPrefix(string value)
    {
        return TryStripGraphSeedPrefix(value, out _);
    }

    private static bool TryStripGraphSeedPrefix(string value, out string stripped)
    {
        foreach (var prefix in new[]
        {
            "context:",
            "ctx:",
            "item:",
            "node:",
            "entity:",
            "source:",
            "ref:"
        })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                stripped = value[prefix.Length..].Trim();
                return true;
            }
        }

        stripped = value;
        return false;
    }

    private static bool IsGraphSeedChar(char ch)
    {
        return char.IsLetterOrDigit(ch)
            || ch is '-' or '_' or '.';
    }

    private static bool IsGenericGraphSeed(string value)
    {
        return value.Equals("memory", StringComparison.OrdinalIgnoreCase)
            || value.Equals("task", StringComparison.OrdinalIgnoreCase)
            || value.Equals("state", StringComparison.OrdinalIgnoreCase)
            || value.Equals("active", StringComparison.OrdinalIgnoreCase)
            || value.Equals("current", StringComparison.OrdinalIgnoreCase)
            || value.Equals("package", StringComparison.OrdinalIgnoreCase);
    }
}
