using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 解析上下文条目的 SourceRefs 和 ItemRefs，以及写入锚点元数据。
/// 所有方法均为纯函数，不持有状态。
/// </summary>
internal static class ContextItemRefResolver
{
    internal static IReadOnlyList<string> ResolveSourceRefs(ContextItem item)
    {
        if (item.SourceRefs.Count > 0)
        {
            return item.SourceRefs.ToArray();
        }

        return string.IsNullOrWhiteSpace(item.Id)
            ? Array.Empty<string>()
            : new[] { item.Id };
    }

    internal static IReadOnlyList<string> ResolveItemRefs(ContextItem item)
    {
        return string.IsNullOrWhiteSpace(item.Id)
            ? Array.Empty<string>()
            : new[] { item.Id };
    }

    internal static IReadOnlyList<string> ResolveItemRefs(IEnumerable<ContextItem> items)
    {
        return items.Select(item => item.Id)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ResolveItemRefs(IEnumerable<ContextMemoryItem> items)
    {
        return items.Select(item => item.Id)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ResolveItemRefs(IEnumerable<ContextGlobalItem> items)
    {
        return items.Select(item => item.Id)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ResolveItemRefs(IEnumerable<ContextConstraint> items)
    {
        return items.Select(item => item.Id)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ResolveItemRefs(IEnumerable<RecentContextItem> items)
    {
        return items.Select(item => item.SourceItemId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ResolveSourceRefs(IEnumerable<ContextItem> items)
    {
        return items.SelectMany(ResolveSourceRefs)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ResolveSourceRefs(IEnumerable<ContextMemoryItem> items)
    {
        return items.SelectMany(item => item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ResolveSourceRefs(IEnumerable<ContextGlobalItem> items)
    {
        return items.SelectMany(item => item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ResolveSourceRefs(IEnumerable<ContextConstraint> items)
    {
        return items.SelectMany(item => item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ResolveSourceRefs(IEnumerable<RecentContextItem> items)
    {
        return items.SelectMany(item => item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.SourceItemId })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static void AddAnchorMetadata(
        IDictionary<string, string> metadata,
        IReadOnlyList<ContextAnchor> anchors)
    {
        if (anchors.Count == 0)
        {
            return;
        }

        metadata["anchor.count"] = anchors.Count.ToString();
        metadata["anchor.names"] = string.Join(",", anchors.Select(anchor => anchor.Name));
        metadata["anchor.types"] = string.Join(",", anchors.Select(anchor => anchor.Type.ToString()).Distinct(StringComparer.OrdinalIgnoreCase));

        // 拆分 Raw / Semantic Anchors
        var rawSearchTokens = anchors.Where(a => string.Equals(a.Source, "request.query", StringComparison.OrdinalIgnoreCase)).ToList();
        var semanticAnchors = anchors.Where(a => !string.Equals(a.Source, "request.query", StringComparison.OrdinalIgnoreCase)).ToList();

        metadata["anchor.rawSearchTokens"] = string.Join(",", rawSearchTokens.Select(a => a.Name));
        metadata["anchor.semanticAnchors"] = string.Join(",", semanticAnchors.Select(a => a.Name));
        metadata["anchor.rawSearchTokensCount"] = rawSearchTokens.Count.ToString();
        metadata["anchor.semanticAnchorsCount"] = semanticAnchors.Count.ToString();
    }
}
