using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 根据当前已召回候选构建关系扩展 frontier。
/// 只负责种子筛选和 frontier 配置，不负责查询关系或解析 target。
/// </summary>
public sealed class RelationFrontierBuilder
{
    public RelationExpansionFrontier Build(
        ContextRetrievalRequest request,
        RetrievalPlan plan,
        IReadOnlyList<ContextRetrievalCandidate> candidates)
    {
        var maxDepth = request.RelationExpansionDepth <= 0
            ? 0
            : Math.Min(request.RelationExpansionDepth, 3);
        var maxFanout = request.CandidateTake > 0 ? request.CandidateTake : Math.Max(20, request.TopK * 4);
        var allowDeprecated = RetrievalPlanExecutionPolicy.AllowDeprecated(plan);
        var allowedRelationTypes = request.AllowedRelationTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim())
            .ToArray();

        var seeds = candidates
            .Where(IsSupportedSeedKind)
            .Where(candidate => CanUseAsSeed(candidate, allowDeprecated))
            .OrderByDescending(candidate => candidate.Score)
            .Take(maxFanout)
            .Select(candidate => new RelationFrontierSeed(
                candidate.SourceId,
                candidate.Kind,
                candidate.Score,
                candidate.SourceId,
                new Dictionary<string, string>(candidate.Metadata)))
            .ToArray();

        return new RelationExpansionFrontier(
            maxDepth,
            maxFanout,
            allowDeprecated,
            allowedRelationTypes,
            seeds);
    }

    private static bool IsSupportedSeedKind(ContextRetrievalCandidate candidate)
    {
        return candidate.Kind == ContextRetrievalCandidateKind.ContextItem
            || candidate.Kind == ContextRetrievalCandidateKind.MemoryItem;
    }

    private static bool CanUseAsSeed(ContextRetrievalCandidate candidate, bool allowDeprecated)
    {
        if (candidate.Metadata.TryGetValue("lifecycleStatus", out var lifecycleStatus))
        {
            if (string.Equals(lifecycleStatus, ContextMemoryStatus.Rejected.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!allowDeprecated
                && string.Equals(lifecycleStatus, ContextMemoryStatus.Deprecated.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!allowDeprecated
            && candidate.Metadata.TryGetValue("status", out var contextStatus)
            && string.Equals(contextStatus, "deprecated", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!allowDeprecated && candidate.Metadata.ContainsKey("supersededBy"))
        {
            return false;
        }

        return true;
    }
}

public sealed record RelationExpansionFrontier(
    int MaxDepth,
    int MaxFanout,
    bool AllowDeprecated,
    IReadOnlyList<string> AllowedRelationTypes,
    IReadOnlyList<RelationFrontierSeed> Seeds);

public sealed record RelationFrontierSeed(
    string SourceId,
    ContextRetrievalCandidateKind Kind,
    double Score,
    string Path,
    IReadOnlyDictionary<string, string> Metadata);
