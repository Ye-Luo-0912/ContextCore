using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>
/// 执行关系扩展：通过统一遍历引擎查询 relation store、解析 target、保持 relation paths 和现有评分语义。
/// </summary>
internal sealed class RelationExpansionService
{
    private readonly RelationTraversalEngine _traversalEngine;
    private readonly IContextObjectResolver _contextObjectResolver;

    public RelationExpansionService(
        RelationTraversalEngine traversalEngine,
        IContextObjectResolver contextObjectResolver)
    {
        _traversalEngine = traversalEngine;
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

        var profile = new RelationExpansionProfile
        {
            ProfileId = "retrieval-frontier",
            Mode = "Normal",
            MaxDepth = frontier.MaxDepth,
            MaxFanout = frontier.MaxFanout,
            AllowedRelationTypes = frontier.AllowedRelationTypes,
            // retrieval 路径不做置信度过滤（原实现也没有）；lifecycle 过滤不影响，因为原实现检查 resolved object 的 deprecated 状态
            MinConfidence = 0.0,
            AllowDeprecatedRelations = frontier.AllowDeprecated,
            AllowCandidateRelations = true,
            AllowRejectedRelations = true,
            RequireEvidence = false
        };

        var request = new RelationTraversalRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Seeds = frontier.Seeds
                .Select(seed => new RelationTraversalSeed(seed.SourceId, seed.Score))
                .ToArray(),
            Profile = profile,
            Direction = RelationDirection.Outgoing
        };

        var traversalResult = await _traversalEngine.TraverseAsync(request, cancellationToken).ConfigureAwait(false);

        var channelCandidates = new List<RetrievalChannelCandidate>();
        var added = 0;
        var unresolvedTargets = 0;

        // 出边遍历：邻居即 relation.TargetId，批量解析后逐边评分
        var targetIds = traversalResult.Edges
            .Select(edge => edge.Relation.TargetId)
            .Where(targetId => !string.IsNullOrWhiteSpace(targetId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolutions = targetIds.Length == 0
            ? Array.Empty<ContextObjectResolution>()
            : (await _contextObjectResolver.ResolveManyAsync(
                workspaceId,
                collectionId,
                targetIds,
                cancellationToken).ConfigureAwait(false)).ToArray();
        var resolutionMap = resolutions.ToDictionary(resolution => resolution.RequestedId, StringComparer.OrdinalIgnoreCase);

        foreach (var edge in traversalResult.Edges)
        {
            var targetId = edge.Relation.TargetId;
            if (!resolutionMap.TryGetValue(targetId, out var resolution)
                || !resolution.Found
                || resolution.ResolvedObject is null)
            {
                unresolvedTargets++;
                continue;
            }

            if (!CanUseResolvedTarget(resolution.ResolvedObject, frontier.AllowDeprecated))
            {
                continue;
            }

            var score = RetrievalCandidatePolicy.ScoreRelationTarget(
                edge.SourceScore,
                edge.Relation,
                resolution.ResolvedObject.Importance,
                edge.Depth);
            channelCandidates.Add(RetrievalChannelCandidate.FromRelationTarget(
                channelSource: "relation",
                resolution.ResolvedObject.ToRelationTarget(),
                score,
                $"关系扩展 d{edge.Depth} {edge.Relation.RelationType} -> {edge.Relation.SourceId}",
                relationPaths: [edge.Path],
                scoreBreakdown: new Dictionary<string, double> { ["relation"] = score }));

            added++;
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
}
