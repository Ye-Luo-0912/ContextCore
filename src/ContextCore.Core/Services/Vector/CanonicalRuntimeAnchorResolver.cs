using System.Text;

namespace ContextCore.Core.Services;

/// <summary>
/// 统一 query / item 两侧的 evidenceRef、sourceRef、provenance 命名空间。
/// 数据集 sample 侧（如 stress-sample-ev-train-0014）和 corpus 侧
/// （如 stress-ev-train-0014）只差一个 <c>-sample-</c> 中缀，去掉即可对齐；
/// 同时做小写化、空白裁剪、可选前缀（evidence-/source-/src-/ev-）剥离，
/// 让未来的 runtime supplier 在跨命名空间情形下也能匹配。
/// 纯字符串处理，不含 fixture / 领域字面量。
/// </summary>
internal static class CanonicalRuntimeAnchorResolver
{
    private static readonly string[] SampleInfixes = { "-sample-", "_sample_" };

    private static readonly string[] KnownPrefixes =
    {
        "evidence-",
        "source-",
        "src-",
        "ev-"
    };

    /// <summary>归一一个 anchor 字符串，用于跨 query/item namespace 比对。</summary>
    public static string Normalize(string? anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return string.Empty;
        }

        var trimmed = anchor.Trim().ToLowerInvariant();
        foreach (var infix in SampleInfixes)
        {
            // sample 侧 anchor 带 "-sample-" 中缀；去掉后与 corpus 侧 anchor 落在同一命名空间。
            trimmed = trimmed.Replace(infix, "-", StringComparison.Ordinal);
        }

        // 命中的第一个已知前缀剥掉；保留剩余的具识别力的尾段（如 "stress-train-0014"）。
        foreach (var prefix in KnownPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                trimmed = trimmed[prefix.Length..];
                break;
            }
        }

        return trimmed;
    }

    public static bool IsMatch(string? a, string? b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    public static int CountOverlap(IReadOnlyList<string> derived, IReadOnlyList<string> expected)
    {
        if (derived.Count == 0 || expected.Count == 0)
        {
            return 0;
        }

        var derivedSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in derived)
        {
            var normalized = Normalize(anchor);
            if (!string.IsNullOrEmpty(normalized))
            {
                derivedSet.Add(normalized);
            }
        }

        if (derivedSet.Count == 0)
        {
            return 0;
        }

        var overlap = 0;
        foreach (var anchor in expected)
        {
            var normalized = Normalize(anchor);
            if (!string.IsNullOrEmpty(normalized) && derivedSet.Contains(normalized))
            {
                overlap++;
            }
        }

        return overlap;
    }

    public static IReadOnlyList<string> NormalizeAll(IReadOnlyList<string> anchors)
    {
        if (anchors.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>(anchors.Count);
        foreach (var anchor in anchors)
        {
            var normalized = Normalize(anchor);
            if (!string.IsNullOrEmpty(normalized) && seen.Add(normalized))
            {
                output.Add(normalized);
            }
        }

        return output;
    }
}
