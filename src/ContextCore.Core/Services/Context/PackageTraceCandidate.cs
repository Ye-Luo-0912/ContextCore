using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

public sealed class PackageTraceCandidate
{
    private PackageTraceCandidate(
        string id,
        string kind,
        string type,
        double score,
        int estimatedTokens,
        IReadOnlyList<string> sourceRefs,
        string content,
        Dictionary<string, string>? metadata = null,
        ItemScoreBreakdown? scoreBreakdown = null)
    {
        Id = id;
        Kind = kind;
        Type = type;
        Score = score;
        EstimatedTokens = estimatedTokens;
        SourceRefs = sourceRefs;
        Content = content ?? string.Empty;
        Metadata = metadata ?? new Dictionary<string, string>();
        ScoreBreakdown = scoreBreakdown;
    }

    public string Id { get; }
    public string Kind { get; }
    public string Type { get; }
    public double Score { get; }
    public int EstimatedTokens { get; }
    public IReadOnlyList<string> SourceRefs { get; }
    public string Content { get; }
    public Dictionary<string, string> Metadata { get; }

    /// <summary>评分明细，仅 working_memory / historical_context 路径下填充。</summary>
    public ItemScoreBreakdown? ScoreBreakdown { get; }

    public static PackageTraceCandidate FromContextItem(
        ContextItem item,
        string kind,
        double score,
        int? estimatedTokens = null)
    {
        return new PackageTraceCandidate(
            item.Id,
            kind,
            item.Type,
            score,
            estimatedTokens ?? BasicContextPackageBuilder.EstimateTokens(item.Content),
            BasicContextPackageBuilder.ResolveSourceRefs(item),
            item.Content,
            item.Metadata);
    }

    /// <summary>从 旧式 double score 创建（兼容）。</summary>
    public static PackageTraceCandidate FromMemory(
        ContextMemoryItem item,
        string kind,
        double score,
        int? estimatedTokens = null)
    {
        return new PackageTraceCandidate(
            item.Id,
            kind,
            item.Type,
            score,
            estimatedTokens ?? BasicContextPackageBuilder.EstimateTokens(item.Content),
            item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.Id },
            item.Content,
            item.Metadata);
    }

    /// <summary>从 ItemScoreBreakdown 创建，自动使用 FinalScore。</summary>
    public static PackageTraceCandidate FromMemory(
        ContextMemoryItem item,
        string kind,
        ItemScoreBreakdown breakdown,
        int? estimatedTokens = null)
    {
        return new PackageTraceCandidate(
            item.Id,
            kind,
            item.Type,
            breakdown.FinalScore,
            estimatedTokens ?? BasicContextPackageBuilder.EstimateTokens(item.Content),
            item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.Id },
            item.Content,
            new Dictionary<string, string>(item.Metadata),
            breakdown);
    }

    public static PackageTraceCandidate FromGlobal(
        ContextGlobalItem item,
        string kind,
        double score,
        int? estimatedTokens = null)
    {
        return new PackageTraceCandidate(
            item.Id,
            kind,
            item.Type,
            score,
            estimatedTokens ?? BasicContextPackageBuilder.EstimateTokens(item.Content),
            item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.Id },
            item.Content,
            item.Metadata);
    }

    public static PackageTraceCandidate FromConstraint(
        ContextConstraint item,
        string kind,
        double score,
        int? estimatedTokens = null)
    {
        return new PackageTraceCandidate(
            item.Id,
            kind,
            "constraint",
            score + item.Confidence * 5,
            estimatedTokens ?? BasicContextPackageBuilder.EstimateTokens(item.Content),
            item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.Id },
            item.Content,
            item.Metadata);
    }

    public static PackageTraceCandidate FromCurrentTask(
        WorkingMemoryCurrentTask item,
        int? estimatedTokens = null)
    {
        return new PackageTraceCandidate(
            item.TaskId,
            "current_task",
            "task",
            110,
            estimatedTokens ?? BasicContextPackageBuilder.EstimateTokens(item.Description),
            [.. new[] { $"task:{item.TaskId}" }
                .Concat(item.Metadata.TryGetValue("sourceRef", out var sourceRef) && !string.IsNullOrWhiteSpace(sourceRef)
                    ? new[] { sourceRef }
                    : Array.Empty<string>())],
            item.Title + " " + item.Description,
            item.Metadata);
    }

    public static PackageTraceCandidate FromRecent(
        RecentContextItem item,
        string kind,
        double score,
        int? estimatedTokens = null)
    {
        return new PackageTraceCandidate(
            item.SourceItemId,
            kind,
            "recent",
            score,
            estimatedTokens ?? BasicContextPackageBuilder.EstimateTokens(item.Content),
            item.SourceRefs.Count > 0 ? item.SourceRefs.ToArray() : new[] { item.SourceItemId },
            item.Content,
            new Dictionary<string, string>
            {
                ["relevance"] = item.Relevance.ToString("0.000"),
                ["recencyWeight"] = item.RecencyWeight.ToString("0.000"),
                ["reason"] = item.Reason,
                ["sourceTurnId"] = item.SourceTurnId ?? string.Empty
            });
    }
}
