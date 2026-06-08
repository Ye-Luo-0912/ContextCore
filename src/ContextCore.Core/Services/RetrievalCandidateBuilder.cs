using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 聚合同一候选项来自多个通道的增量信息，并统一构建最终 RetrievalCandidate。
/// </summary>
internal sealed class RetrievalCandidateBuilder
{
    private const string MandatoryKey = "mandatory";
    private const string ChannelSourcesKey = "channelSources";
    private const string AlsoReferencedByKey = "alsoReferencedBy";
    private const string RelationPathsKey = "relationPaths";
    private const string MatchedTokensKey = "matchedTokens";
    private const string MatchedAnchorsKey = "matchedAnchors";
    private const string ScoreBreakdownKey = "scoreBreakdown";

    private static readonly string[] ScoreBreakdownOrder =
    [
        "mandatory",
        "keyword",
        "memory",
        "vector",
        "relation",
        "total"
    ];

    private readonly HashSet<string> _reasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _channelOrder = new();
    private readonly HashSet<string> _channelSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _alsoReferencedBy = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _relationPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _matchedTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _matchedAnchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _scoreBreakdown = new(StringComparer.OrdinalIgnoreCase);

    private RetrievalCandidateBuilder(
        string sourceId,
        ContextRetrievalCandidateKind kind,
        string type,
        string? title,
        string content,
        ContextContentFormat contentFormat,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, string> metadata)
    {
        SourceId = sourceId;
        Kind = kind;
        Type = type;
        Title = title;
        Content = content;
        ContentFormat = contentFormat;
        Tags = tags;
        SourceRefs = sourceRefs;
        Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);
    }

    public string SourceId { get; }

    public ContextRetrievalCandidateKind Kind { get; }

    public string Type { get; }

    public string? Title { get; }

    public string Content { get; }

    public ContextContentFormat ContentFormat { get; }

    public IReadOnlyList<string> Tags { get; }

    public IReadOnlyList<string> SourceRefs { get; }

    public Dictionary<string, string> Metadata { get; }

    public double Score { get; private set; }

    public bool Mandatory { get; private set; }

    public static RetrievalCandidateBuilder FromContextItem(ContextItem item)
    {
        var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["candidateSourceKind"] = "context",
            ["importance"] = item.Importance.ToString("0.###"),
            ["createdAt"] = item.CreatedAt.ToString("O"),
            ["updatedAt"] = item.UpdatedAt.ToString("O")
        };

        return new RetrievalCandidateBuilder(
            item.Id,
            ContextRetrievalCandidateKind.ContextItem,
            item.Type,
            item.Title,
            item.Content,
            item.ContentFormat,
            item.Tags.ToArray(),
            item.SourceRefs.Concat(item.Refs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            metadata);
    }

    public static RetrievalCandidateBuilder FromMemoryItem(ContextMemoryItem item)
    {
        var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["candidateSourceKind"] = "memory",
            ["lifecycleStatus"] = item.Status.ToString(),
            ["memoryLayer"] = item.Layer.ToString(),
            ["importance"] = item.Importance.ToString("0.###"),
            ["createdAt"] = item.CreatedAt.ToString("O"),
            ["updatedAt"] = item.UpdatedAt.ToString("O")
        };

        return new RetrievalCandidateBuilder(
            item.Id,
            ContextRetrievalCandidateKind.MemoryItem,
            item.Type,
            title: null,
            item.Content,
            item.ContentFormat,
            item.Tags.ToArray(),
            item.SourceRefs.ToArray(),
            metadata);
    }

    public static RetrievalCandidateBuilder FromRelationTarget(RetrievalRelationTarget target)
    {
        return new RetrievalCandidateBuilder(
            target.SourceId,
            target.Kind,
            target.Type,
            target.Title,
            target.Content,
            target.ContentFormat,
            target.Tags,
            target.SourceRefs,
            target.Metadata);
    }

    public void AddOrMerge(RetrievalChannelCandidate candidate)
    {
        Score += candidate.Score;
        Mandatory |= candidate.Mandatory;
        _reasons.Add(candidate.Reason);

        if (_channelSources.Add(candidate.ChannelSource))
        {
            if (_channelOrder.Count > 0)
            {
                _alsoReferencedBy.Add(candidate.ChannelSource);
            }

            _channelOrder.Add(candidate.ChannelSource);
        }

        foreach (var relationPath in candidate.RelationPaths)
        {
            if (!string.IsNullOrWhiteSpace(relationPath))
            {
                _relationPaths.Add(relationPath);
            }
        }

        foreach (var token in candidate.MatchedTokens)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                _matchedTokens.Add(token);
            }
        }

        foreach (var anchor in candidate.MatchedAnchors)
        {
            if (!string.IsNullOrWhiteSpace(anchor))
            {
                _matchedAnchors.Add(anchor);
            }
        }

        foreach (var entry in candidate.ScoreBreakdown)
        {
            _scoreBreakdown[entry.Key] = _scoreBreakdown.TryGetValue(entry.Key, out var existing)
                ? existing + entry.Value
                : entry.Value;
        }
    }

    public ContextRetrievalCandidate Build(bool includeContent)
    {
        var metadata = new Dictionary<string, string>(Metadata, StringComparer.OrdinalIgnoreCase)
        {
            [MandatoryKey] = Mandatory ? "true" : "false",
            [ChannelSourcesKey] = string.Join(",", _channelOrder)
        };

        if (_alsoReferencedBy.Count > 0)
        {
            metadata[AlsoReferencedByKey] = string.Join(",", _alsoReferencedBy.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        }

        if (_relationPaths.Count > 0)
        {
            metadata[RelationPathsKey] = string.Join(" | ", _relationPaths.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        }

        if (_matchedTokens.Count > 0)
        {
            metadata[MatchedTokensKey] = string.Join(",", _matchedTokens.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        }

        if (_matchedAnchors.Count > 0)
        {
            metadata[MatchedAnchorsKey] = string.Join(",", _matchedAnchors.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        }

        if (_scoreBreakdown.Count > 0)
        {
            metadata[ScoreBreakdownKey] = FormatScoreBreakdown();
        }

        return new ContextRetrievalCandidate
        {
            CandidateId = $"{Kind}:{SourceId}",
            SourceId = SourceId,
            Kind = Kind,
            Type = Type,
            Title = Title,
            Content = includeContent ? Content : string.Empty,
            ContentFormat = ContentFormat,
            Tags = [.. Tags],
            SourceRefs = [.. SourceRefs],
            Score = Score,
            EstimatedTokens = EstimateTokens(Content),
            Reasons = [.. _reasons],
            Metadata = metadata
        };
    }

    private string FormatScoreBreakdown()
    {
        var parts = new List<string>();
        foreach (var key in ScoreBreakdownOrder)
        {
            if (key.Equals("total", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"total={Score:0.###}");
                continue;
            }

            if (_scoreBreakdown.TryGetValue(key, out var value))
            {
                parts.Add($"{key}={value:0.###}");
            }
        }

        foreach (var pair in _scoreBreakdown
            .Where(pair => !ScoreBreakdownOrder.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add($"{pair.Key}={pair.Value:0.###}");
        }

        return string.Join(";", parts);
    }

    private static int EstimateTokens(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : Math.Max(1, text.Length / 4);
    }
}
