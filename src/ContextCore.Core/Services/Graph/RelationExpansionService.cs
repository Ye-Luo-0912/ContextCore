using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>
/// 执行关系扩展：查询 relation store、解析 target、保持 relation paths 和现有评分语义。
/// </summary>
internal sealed class RelationExpansionService
{
    private readonly IRelationStore _relationStore;
    private readonly IContextObjectResolver _contextObjectResolver;

    public RelationExpansionService(
        IRelationStore relationStore,
        IContextObjectResolver contextObjectResolver)
    {
        _relationStore = relationStore;
        _contextObjectResolver = contextObjectResolver;
    }

    public async Task<RetrievalChannelResult> ExpandAsync(
        string workspaceId,
        string collectionId,
        RelationExpansionFrontier frontier,
        CancellationToken cancellationToken = default)
    {
        if (frontier.MaxDepth <= 0 || frontier.Seeds.Count == 0)
        {
            return new RetrievalChannelResult(
                "关系扩展",
                0,
                Array.Empty<RetrievalChannelCandidate>(),
                BuildMetadata(frontier, unresolvedTargets: 0));
        }

        var channelCandidates = new List<RetrievalChannelCandidate>();
        var added = 0;
        var unresolvedTargets = 0;
        var visitedNodes = frontier.Seeds
            .Select(seed => seed.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visitedEdges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentFrontier = frontier.Seeds
            .Select(seed => new RelationExpansionNode(seed.SourceId, seed.Score, seed.Path))
            .ToArray();

        for (var depth = 1; depth <= frontier.MaxDepth && currentFrontier.Length > 0; depth++)
        {
            var nextFrontier = new List<RelationExpansionNode>(capacity: Math.Min(frontier.MaxFanout, currentFrontier.Length * 2));

            foreach (var node in currentFrontier)
            {
                var relations = await _relationStore.QueryForItemAsync(
                    workspaceId,
                    collectionId,
                    node.SourceId,
                    cancellationToken).ConfigureAwait(false);

                var outgoingRelations = relations
                    .Where(relation => string.Equals(relation.SourceId, node.SourceId, StringComparison.OrdinalIgnoreCase))
                    .Where(relation => ShouldIncludeRelationType(relation, frontier.AllowedRelationTypes))
                    .Where(relation => visitedEdges.Add($"{relation.SourceId}\u001f{relation.TargetId}\u001f{relation.RelationType}"))
                    .ToArray();

                if (outgoingRelations.Length == 0)
                {
                    continue;
                }

                var resolutions = await _contextObjectResolver.ResolveManyAsync(
                    workspaceId,
                    collectionId,
                    outgoingRelations.Select(relation => relation.TargetId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    cancellationToken).ConfigureAwait(false);
                var resolutionMap = resolutions.ToDictionary(resolution => resolution.RequestedId, StringComparer.OrdinalIgnoreCase);

                foreach (var relation in outgoingRelations)
                {
                    var resolution = resolutionMap[relation.TargetId];
                    if (!resolution.Found || resolution.ResolvedObject is null)
                    {
                        unresolvedTargets++;
                        continue;
                    }

                    if (!CanUseResolvedTarget(resolution.ResolvedObject, frontier.AllowDeprecated))
                    {
                        continue;
                    }

                    var score = RetrievalCandidatePolicy.ScoreRelationTarget(
                        node.Score,
                        relation,
                        resolution.ResolvedObject.Importance,
                        depth);
                    var relationPath = $"{node.Path} -[{relation.RelationType}]-> {resolution.ResolvedObject.Id}";
                    channelCandidates.Add(RetrievalChannelCandidate.FromRelationTarget(
                        channelSource: "relation",
                        resolution.ResolvedObject.ToRelationTarget(),
                        score,
                        $"关系扩展 d{depth} {relation.RelationType} -> {node.SourceId}",
                        relationPaths: [relationPath],
                        scoreBreakdown: new Dictionary<string, double> { ["relation"] = score }));

                    added++;
                    if (visitedNodes.Add(resolution.ResolvedObject.Id) && nextFrontier.Count < frontier.MaxFanout)
                    {
                        nextFrontier.Add(new RelationExpansionNode(
                            resolution.ResolvedObject.Id,
                            score,
                            relationPath));
                    }
                }
            }

            currentFrontier = nextFrontier
                .OrderByDescending(item => item.Score)
                .Take(frontier.MaxFanout)
                .ToArray();
        }

        return new RetrievalChannelResult(
            "关系扩展",
            added,
            channelCandidates,
            BuildMetadata(frontier, unresolvedTargets));
    }

    private static Dictionary<string, string> BuildMetadata(
        RelationExpansionFrontier frontier,
        int unresolvedTargets)
    {
        return new Dictionary<string, string>
        {
            ["depth"] = frontier.MaxDepth.ToString(),
            ["allowedRelationTypes"] = frontier.AllowedRelationTypes.Count == 0
                ? "全部"
                : string.Join(",", frontier.AllowedRelationTypes),
            ["unresolvedTargets"] = unresolvedTargets.ToString()
        };
    }

    private static bool ShouldIncludeRelationType(
        ContextRelation relation,
        IReadOnlyList<string> allowedRelationTypes)
    {
        return allowedRelationTypes.Count == 0
            || allowedRelationTypes.Contains(relation.RelationType, StringComparer.OrdinalIgnoreCase);
    }

    private static bool CanUseResolvedTarget(ResolvedContextObject resolvedObject, bool allowDeprecated)
    {
        if (resolvedObject.ContextItem is not null)
        {
            return RetrievalCandidatePolicy.CanUseRelatedContextItem(resolvedObject.ContextItem, allowDeprecated);
        }

        if (resolvedObject.MemoryItem is not null)
        {
            return RetrievalCandidatePolicy.CanUseMemoryItem(resolvedObject.MemoryItem, allowDeprecated);
        }

        return false;
    }

    private sealed record RelationExpansionNode(string SourceId, double Score, string Path);
}
