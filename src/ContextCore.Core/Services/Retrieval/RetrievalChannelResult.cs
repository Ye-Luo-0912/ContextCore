using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 单个召回通道的统一输出结构。
/// Retriever 只消费该结构，不再感知每个通道各自的候选拼装细节。
/// </summary>
internal sealed class RetrievalChannelResult
{
    public RetrievalChannelResult(
        string stageName,
        int stageCandidateCount,
        IReadOnlyList<RetrievalChannelCandidate> candidates,
        Dictionary<string, string>? metadata = null)
    {
        StageName = stageName;
        StageCandidateCount = stageCandidateCount;
        Candidates = candidates;
        Metadata = metadata ?? new Dictionary<string, string>();
    }

    public string StageName { get; }

    public int StageCandidateCount { get; }

    public IReadOnlyList<RetrievalChannelCandidate> Candidates { get; }

    public Dictionary<string, string> Metadata { get; }
}

/// <summary>
/// 通道产生的候选增量信息。
/// 该对象既承载基础条目内容，也承载本次命中的通道、路径和分数贡献。
/// </summary>
internal sealed class RetrievalChannelCandidate
{
    private RetrievalChannelCandidate(
        string channelSource,
        string sourceId,
        ContextRetrievalCandidateKind kind,
        string type,
        string? title,
        string content,
        ContextContentFormat contentFormat,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, string> metadata,
        double score,
        bool mandatory,
        string reason,
        IReadOnlyList<string> relationPaths,
        IReadOnlyList<string> matchedTokens,
        IReadOnlyList<string> matchedAnchors,
        IReadOnlyDictionary<string, double> scoreBreakdown,
        ContextItem? contextItem,
        ContextMemoryItem? memoryItem,
        RetrievalRelationTarget? relationTarget)
    {
        ChannelSource = channelSource;
        SourceId = sourceId;
        Kind = kind;
        Type = type;
        Title = title;
        Content = content;
        ContentFormat = contentFormat;
        Tags = tags;
        SourceRefs = sourceRefs;
        Metadata = metadata;
        Score = score;
        Mandatory = mandatory;
        Reason = reason;
        RelationPaths = relationPaths;
        MatchedTokens = matchedTokens;
        MatchedAnchors = matchedAnchors;
        ScoreBreakdown = scoreBreakdown;
        ContextItem = contextItem;
        MemoryItem = memoryItem;
        RelationTarget = relationTarget;
    }

    public string ChannelSource { get; }

    public string SourceId { get; }

    public ContextRetrievalCandidateKind Kind { get; }

    public string Type { get; }

    public string? Title { get; }

    public string Content { get; }

    public ContextContentFormat ContentFormat { get; }

    public IReadOnlyList<string> Tags { get; }

    public IReadOnlyList<string> SourceRefs { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public double Score { get; }

    public bool Mandatory { get; }

    public string Reason { get; }

    public IReadOnlyList<string> RelationPaths { get; }

    public IReadOnlyList<string> MatchedTokens { get; }

    public IReadOnlyList<string> MatchedAnchors { get; }

    public IReadOnlyDictionary<string, double> ScoreBreakdown { get; }

    public ContextItem? ContextItem { get; }

    public ContextMemoryItem? MemoryItem { get; }

    public RetrievalRelationTarget? RelationTarget { get; }

    public static RetrievalChannelCandidate FromContextItem(
        string channelSource,
        ContextItem item,
        double score,
        string reason,
        bool mandatory = false,
        IReadOnlyList<string>? relationPaths = null,
        IReadOnlyList<string>? matchedTokens = null,
        IReadOnlyList<string>? matchedAnchors = null,
        IReadOnlyDictionary<string, double>? scoreBreakdown = null)
    {
        return new RetrievalChannelCandidate(
            channelSource,
            item.Id,
            ContextRetrievalCandidateKind.ContextItem,
            item.Type,
            item.Title,
            item.Content,
            item.ContentFormat,
            item.Tags.ToArray(),
            item.SourceRefs.Concat(item.Refs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            new Dictionary<string, string>(item.Metadata),
            score,
            mandatory,
            reason,
            relationPaths ?? Array.Empty<string>(),
            matchedTokens ?? Array.Empty<string>(),
            matchedAnchors ?? Array.Empty<string>(),
            scoreBreakdown ?? new Dictionary<string, double>(),
            item,
            memoryItem: null,
            relationTarget: null);
    }

    public static RetrievalChannelCandidate FromMemoryItem(
        string channelSource,
        ContextMemoryItem item,
        double score,
        string reason,
        bool mandatory = false,
        IReadOnlyList<string>? relationPaths = null,
        IReadOnlyList<string>? matchedTokens = null,
        IReadOnlyList<string>? matchedAnchors = null,
        IReadOnlyDictionary<string, double>? scoreBreakdown = null)
    {
        return new RetrievalChannelCandidate(
            channelSource,
            item.Id,
            ContextRetrievalCandidateKind.MemoryItem,
            item.Type,
            title: null,
            item.Content,
            item.ContentFormat,
            item.Tags.ToArray(),
            item.SourceRefs.ToArray(),
            new Dictionary<string, string>(item.Metadata),
            score,
            mandatory,
            reason,
            relationPaths ?? Array.Empty<string>(),
            matchedTokens ?? Array.Empty<string>(),
            matchedAnchors ?? Array.Empty<string>(),
            scoreBreakdown ?? new Dictionary<string, double>(),
            contextItem: null,
            item,
            relationTarget: null);
    }

    public static RetrievalChannelCandidate FromRelationTarget(
        string channelSource,
        RetrievalRelationTarget target,
        double score,
        string reason,
        IReadOnlyList<string>? relationPaths = null,
        IReadOnlyList<string>? matchedTokens = null,
        IReadOnlyList<string>? matchedAnchors = null,
        IReadOnlyDictionary<string, double>? scoreBreakdown = null)
    {
        return new RetrievalChannelCandidate(
            channelSource,
            target.SourceId,
            target.Kind,
            target.Type,
            target.Title,
            target.Content,
            target.ContentFormat,
            target.Tags,
            target.SourceRefs,
            target.Metadata,
            score,
            mandatory: false,
            reason,
            relationPaths ?? Array.Empty<string>(),
            matchedTokens ?? Array.Empty<string>(),
            matchedAnchors ?? Array.Empty<string>(),
            scoreBreakdown ?? new Dictionary<string, double>(),
            contextItem: null,
            memoryItem: null,
            target);
    }
}

/// <summary>
/// 关系扩展解析出的目标条目快照，供候选构建器统一处理。
/// </summary>
internal sealed record RetrievalRelationTarget(
    string SourceId,
    ContextRetrievalCandidateKind Kind,
    string Type,
    string? Title,
    string Content,
    ContextContentFormat ContentFormat,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> SourceRefs,
    IReadOnlyDictionary<string, string> Metadata,
    double Importance);
