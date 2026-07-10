using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>
/// 统一关系遍历引擎。用一套 BFS 替代 RelationExpansionService、ResolveRelatedContextAsync、RelationExpansionPreviewService 三处独立遍历。
/// 通过 RelationExpansionProfile 决定深度、扇出、类型、生命周期、置信度；通过 RelationDirection 决定方向。
/// </summary>
public sealed class RelationTraversalEngine
{
    private readonly IRelationStore? _relationStore;

    public RelationTraversalEngine(IRelationStore? relationStore)
    {
        _relationStore = relationStore;
    }

    public async Task<RelationTraversalResult> TraverseAsync(
        RelationTraversalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var warnings = new List<string>();
        if (_relationStore is null)
        {
            warnings.Add("relation store is not registered.");
            return new RelationTraversalResult
            {
                Edges = [],
                MaxDepthReached = 0,
                Truncated = false,
                Warnings = warnings.ToArray()
            };
        }

        var profile = request.Profile;
        var maxDepth = Math.Max(1, profile.MaxDepth);
        var maxFanout = Math.Max(1, profile.MaxFanout);
        var maxNodes = request.MaxNodesOverride ?? 100;
        var maxRelations = request.MaxRelationsOverride ?? 300;
        var minConfidence = profile.MinConfidence;

        var seeds = request.Seeds
            .Where(s => !string.IsNullOrWhiteSpace(s.ItemId))
            .ToArray();
        if (seeds.Length == 0)
        {
            return new RelationTraversalResult
            {
                Edges = [],
                MaxDepthReached = 0,
                Truncated = false,
                Warnings = warnings.ToArray()
            };
        }

        var visitedNodes = seeds.Select(s => s.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visitedEdges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<RelationTraversalEdge>();
        var maxDepthReached = 0;
        var truncated = false;
        var relationCount = 0;

        // 分层 BFS
        var currentFrontier = seeds
            .Select(s => new TraversalNode(s.ItemId, 0, s.Score, s.ItemId))
            .ToArray();

        for (var depth = 1; depth <= maxDepth && currentFrontier.Length > 0; depth++)
        {
            if (relationCount >= maxRelations || edges.Count >= maxNodes)
            {
                truncated = true;
                break;
            }

            var nextFrontier = new List<TraversalNode>(capacity: Math.Min(maxFanout, currentFrontier.Length * 2));

            foreach (var node in currentFrontier)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (relationCount >= maxRelations || edges.Count >= maxNodes)
                {
                    truncated = true;
                    break;
                }

                var relations = await QueryRelationsAsync(
                    request.WorkspaceId,
                    request.CollectionId,
                    node.ItemId,
                    request.Direction,
                    cancellationToken).ConfigureAwait(false);

                var filtered = relations
                    .Where(r => IsAllowedType(r, profile))
                    .Where(r => r.Confidence >= minConfidence)
                    .Where(r => IsAllowedLifecycle(r, profile))
                    .Where(r => visitedEdges.Add(EdgeKey(r, node.ItemId, request.Direction)))
                    .OrderByDescending(r => ResolveWeight(r, profile))
                    .ThenByDescending(r => r.Confidence)
                    .ThenByDescending(r => r.CreatedAt)
                    .Take(maxFanout)
                    .ToArray();

                foreach (var relation in filtered)
                {
                    relationCount++;
                    if (relationCount > maxRelations || edges.Count >= maxNodes)
                    {
                        truncated = true;
                        break;
                    }

                    var neighborId = ResolveNeighborId(relation, node.ItemId);
                    if (string.IsNullOrWhiteSpace(neighborId))
                    {
                        continue;
                    }

                    var path = $"{node.Path} -[{relation.RelationType}]-> {neighborId}";
                    maxDepthReached = Math.Max(maxDepthReached, depth);
                    edges.Add(new RelationTraversalEdge(relation, depth, node.Score, path, neighborId));

                    if (visitedNodes.Add(neighborId) && nextFrontier.Count < maxFanout)
                    {
                        nextFrontier.Add(new TraversalNode(neighborId, depth, node.Score, path));
                    }
                }
            }

            currentFrontier = nextFrontier
                .OrderByDescending(n => n.Score)
                .Take(maxFanout)
                .ToArray();
        }

        return new RelationTraversalResult
        {
            Edges = edges.ToArray(),
            MaxDepthReached = maxDepthReached,
            Truncated = truncated,
            Warnings = warnings.ToArray()
        };
    }

    private async Task<IReadOnlyList<ContextRelation>> QueryRelationsAsync(
        string workspaceId,
        string? collectionId,
        string itemId,
        RelationDirection direction,
        CancellationToken cancellationToken)
    {
        var results = new List<ContextRelation>();

        if (direction is RelationDirection.Outgoing or RelationDirection.Both)
        {
            var outgoing = await _relationStore!.QueryBySourceAsync(
                workspaceId, collectionId, itemId, cancellationToken).ConfigureAwait(false);
            results.AddRange(outgoing);
        }

        if (direction is RelationDirection.Incoming or RelationDirection.Both)
        {
            var incoming = await _relationStore!.QueryByTargetAsync(
                workspaceId, collectionId, itemId, cancellationToken).ConfigureAwait(false);
            results.AddRange(incoming);
        }

        return results;
    }

    private static string ResolveNeighborId(ContextRelation relation, string currentNodeId)
    {
        if (string.Equals(relation.SourceId, currentNodeId, StringComparison.OrdinalIgnoreCase))
        {
            return relation.TargetId;
        }
        return relation.SourceId;
    }

    private static string EdgeKey(ContextRelation relation, string currentNodeId, RelationDirection direction)
    {
        // 对于双向遍历，同一条边可能从两个方向被访问，需要规范化 key
        var neighborId = ResolveNeighborId(relation, currentNodeId);
        return string.Equals(relation.SourceId, currentNodeId, StringComparison.OrdinalIgnoreCase)
            ? $"{currentNodeId}\u001f{neighborId}\u001f{relation.RelationType}"
            : $"{neighborId}\u001f{currentNodeId}\u001f{relation.RelationType}";
    }

    private static bool IsAllowedType(ContextRelation relation, RelationExpansionProfile profile)
    {
        if (profile.BlockedRelationTypes.Count > 0
            && profile.BlockedRelationTypes.Contains(relation.RelationType, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (profile.AllowedRelationTypes.Count == 0)
        {
            return true;
        }

        return profile.AllowedRelationTypes.Contains(relation.RelationType, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAllowedLifecycle(ContextRelation relation, RelationExpansionProfile profile)
    {
        var lifecycle = relation.Lifecycle ?? string.Empty;
        var reviewStatus = relation.ReviewStatus ?? string.Empty;

        if (!profile.AllowDeprecatedRelations
            && (string.Equals(lifecycle, RelationLifecycles.Deprecated, StringComparison.OrdinalIgnoreCase)
                || string.Equals(lifecycle, RelationLifecycles.Superseded, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!profile.AllowRejectedRelations
            && string.Equals(reviewStatus, RelationReviewStatuses.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!profile.AllowCandidateRelations
            && string.Equals(reviewStatus, RelationReviewStatuses.NeedsEvidence, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static double ResolveWeight(ContextRelation relation, RelationExpansionProfile profile)
    {
        if (profile.WeightByRelationType.Count > 0
            && profile.WeightByRelationType.TryGetValue(relation.RelationType, out var weighted))
        {
            return weighted * relation.Weight;
        }
        return relation.Weight;
    }

    private sealed record TraversalNode(string ItemId, int Depth, double Score, string Path);
}
