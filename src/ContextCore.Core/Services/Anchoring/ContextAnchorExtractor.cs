using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 轻量锚点提取器，从当前输入、请求元数据和短期筛选结果中提取后续召回线索。
/// 该实现只执行 profile 中的通用规则，不内置领域词表或评测夹具词表。
/// </summary>
public sealed class ContextAnchorExtractor
{
    private readonly ContextAnchorExtractionProfile _profile;

    public ContextAnchorExtractor(ContextAnchorExtractionProfile? profile = null)
    {
        _profile = profile ?? ContextAnchorExtractionProfile.CreateDefault();
    }

    public IReadOnlyList<ContextAnchor> Extract(
        ContextPackageRequest request,
        IReadOnlyList<RecentContextItem> recentItems)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(recentItems);

        var candidates = new List<ContextAnchor>(
            Math.Min(_profile.MaxAnchors * 2, 128));

        AddMetadataAnchors(candidates, request);
        AddRequestAnchors(candidates, request);
        AddRecentAnchors(candidates, recentItems);

        return SelectTopAnchors(candidates);
    }

    private void AddMetadataAnchors(ICollection<ContextAnchor> anchors, ContextPackageRequest request)
    {
        foreach (var rule in _profile.MetadataRules)
        {
            if (!request.Metadata.TryGetValue(rule.Key, out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            AddAnchor(
                anchors,
                value,
                rule.AnchorType,
                rule.Weight,
                $"metadata:{rule.Key}");
        }
    }

    private void AddRequestAnchors(ICollection<ContextAnchor> anchors, ContextPackageRequest request)
    {
        AddAnchor(
            anchors,
            request.WorkspaceId,
            AnchorType.Project,
            _profile.WorkspaceAnchorWeight,
            "request.workspace");
        AddAnchor(
            anchors,
            request.CollectionId,
            AnchorType.Project,
            _profile.CollectionAnchorWeight,
            "request.collection");

        foreach (var tag in request.RequiredTags)
        {
            AddAnchor(anchors, tag, AnchorType.Topic, _profile.RequiredTagWeight, "request.requiredTags");
        }

        foreach (var type in request.RequiredTypes)
        {
            AddAnchor(anchors, type, AnchorType.Entity, _profile.RequiredTypeWeight, "request.requiredTypes");
        }

        foreach (var term in ExtractTerms(request.QueryText).Take(_profile.MaxQueryTerms))
        {
            AddAnchor(anchors, term, GuessType(term), _profile.QueryTermWeight, "request.query");
        }
    }

    private void AddRecentAnchors(ICollection<ContextAnchor> anchors, IReadOnlyList<RecentContextItem> recentItems)
    {
        foreach (var item in recentItems
            .Where(static item => item.ExcludeReason is null)
            .OrderByDescending(static item => item.Relevance)
            .Take(_profile.MaxRecentItemsForAnchor))
        {
            var weight = Math.Clamp(
                item.Relevance * _profile.RecentRelevanceWeight
                + item.RecencyWeight * _profile.RecentRecencyWeight,
                _profile.RecentMinWeight,
                _profile.RecentMaxWeight);

            foreach (var term in ExtractTerms(item.Content).Take(_profile.MaxRecentTermsPerItem))
            {
                AddAnchor(anchors, term, GuessType(term), weight, $"recent:{item.SourceItemId}");
            }
        }
    }

    private static void AddAnchor(
        ICollection<ContextAnchor> anchors,
        string? name,
        AnchorType type,
        double weight,
        string source)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var normalized = name.Trim();
        if (normalized.Length == 0)
        {
            return;
        }

        anchors.Add(new ContextAnchor(
            normalized,
            type,
            Math.Clamp(weight, 0d, 1d),
            source,
            Array.Empty<string>()));
    }

    private IReadOnlyList<ContextAnchor> SelectTopAnchors(IReadOnlyList<ContextAnchor> candidates)
    {
        var byKey = new Dictionary<AnchorKey, ContextAnchor>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                continue;
            }

            var key = new AnchorKey(Normalize(candidate.Name), candidate.Type);
            if (!byKey.TryGetValue(key, out var existing)
                || candidate.Weight > existing.Weight)
            {
                byKey[key] = candidate;
            }
        }

        return byKey.Values
            .OrderByDescending(static anchor => anchor.Weight)
            .ThenBy(static anchor => anchor.Type)
            .ThenBy(static anchor => anchor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(_profile.MaxAnchors)
            .ToArray();
    }

    private AnchorType GuessType(string term)
    {
        foreach (var rule in _profile.TypeRules)
        {
            if (rule.Signals.Any(signal => ContainsSignal(term, signal)))
            {
                return rule.AnchorType;
            }
        }

        return AnchorType.Topic;
    }

    private IEnumerable<string> ExtractTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Tokenize(text))
        {
            foreach (var term in ExpandToken(token))
            {
                if (emitted.Add(term))
                {
                    yield return term;
                }
            }
        }
    }

    private IEnumerable<string> Tokenize(string text)
    {
        var builder = new StringBuilder();
        var currentKind = AnchorTokenKind.None;

        foreach (var rune in text.EnumerateRunes())
        {
            var kind = ResolveTokenKind(rune);
            if (kind == AnchorTokenKind.None)
            {
                foreach (var flushed in FlushToken(builder))
                {
                    yield return flushed;
                }

                currentKind = AnchorTokenKind.None;
                continue;
            }

            if (currentKind != AnchorTokenKind.None && currentKind != kind)
            {
                foreach (var flushed in FlushToken(builder))
                {
                    yield return flushed;
                }
            }

            currentKind = kind;
            builder.Append(rune);
        }

        foreach (var flushed in FlushToken(builder))
        {
            yield return flushed;
        }
    }

    private IEnumerable<string> ExpandToken(string token)
    {
        if (token.Length < _profile.MinTermLength)
        {
            yield break;
        }

        if (!ContainsChinese(token))
        {
            if (token.Length <= _profile.MaxTermLength)
            {
                yield return token;
            }

            yield break;
        }

        if (token.Length <= _profile.MaxTermLength)
        {
            yield return token;
        }

        var maxGram = Math.Min(_profile.MaxChineseGramLength, token.Length);
        for (var gramLength = _profile.MinTermLength; gramLength <= maxGram; gramLength++)
        {
            for (var start = 0; start <= token.Length - gramLength; start++)
            {
                yield return token.Substring(start, gramLength);
            }
        }
    }

    private static IEnumerable<string> FlushToken(StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            yield break;
        }

        var token = builder.ToString().Trim();
        builder.Clear();
        if (!string.IsNullOrWhiteSpace(token))
        {
            yield return token;
        }
    }

    private static AnchorTokenKind ResolveTokenKind(Rune rune)
    {
        if (IsChinese(rune))
        {
            return AnchorTokenKind.Chinese;
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber => AnchorTokenKind.LatinOrDigit,
            _ => AnchorTokenKind.None
        };
    }

    private static bool ContainsChinese(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsChinese(rune))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsChinese(Rune rune)
    {
        return rune.Value is >= 0x4E00 and <= 0x9FFF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x20000 and <= 0x2A6DF
            or >= 0x2A700 and <= 0x2B73F
            or >= 0x2B740 and <= 0x2B81F
            or >= 0x2B820 and <= 0x2CEAF;
    }

    private static bool ContainsSignal(string term, string signal)
    {
        return !string.IsNullOrWhiteSpace(signal)
            && term.Contains(signal, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private readonly record struct AnchorKey(string NormalizedName, AnchorType Type);

    private enum AnchorTokenKind
    {
        None,
        Chinese,
        LatinOrDigit
    }
}
