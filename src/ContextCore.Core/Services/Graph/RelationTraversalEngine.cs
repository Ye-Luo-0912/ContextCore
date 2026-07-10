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

        // GRAPH-10：构建存储层排除列表，将过滤下推到 QueryNeighborsAsync
        var excludedLifecycles = new List<string>();
        if (!profile.AllowDeprecatedRelations)
        {
            excludedLifecycles.Add(RelationLifecycles.Deprecated);
            excludedLifecycles.Add(RelationLifecycles.Superseded);
            excludedLifecycles.Add(StableMemoryLifecycle.Rejected);
        }

        var excludedReviewStatuses = new List<string>();
        if (!profile.AllowRejectedRelations)
        {
            excludedReviewStatuses.Add(RelationReviewStatuses.Rejected);
        }
        if (!profile.AllowCandidateRelations)
        {
            excludedReviewStatuses.Add(RelationReviewStatuses.NeedsEvidence);
        }

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

        // GRAPH-10：visitedNodes 仅用于环检测（含种子）；discoveredCount 统计扩展引入的新节点数（不含种子）。
        // maxNodes 约束的是"图扩展能引入的最大新节点数"，种子作为查询起点不占用扩展预算。
        var visitedNodes = seeds.Select(s => s.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visitedEdges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<RelationTraversalEdge>();
        var maxDepthReached = 0;
        var truncated = false;
        var relationCount = 0;
        var discoveredCount = 0;

        // 分层 BFS
        var currentFrontier = seeds
            .Select(s => new TraversalNode(s.ItemId, 0, s.Score, s.ItemId))
            .ToArray();

        for (var depth = 1; depth <= maxDepth && currentFrontier.Length > 0; depth++)
        {
            // GRAPH-10：maxNodes 约束的是新发现的节点数（不含种子），而非边数或总节点数
            if (relationCount >= maxRelations || discoveredCount >= maxNodes)
            {
                truncated = true;
                break;
            }

            var nextFrontier = new List<TraversalNode>(capacity: Math.Min(maxFanout, currentFrontier.Length * 2));

            foreach (var node in currentFrontier)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (relationCount >= maxRelations || discoveredCount >= maxNodes)
                {
                    truncated = true;
                    break;
                }

                // GRAPH-10：使用统一 RelationNeighborQuery 替代 QueryBySource + QueryByTarget 双查询
                var relations = await QueryRelationsAsync(
                    request.WorkspaceId,
                    request.CollectionId,
                    node.ItemId,
                    request.Direction,
                    profile,
                    minConfidence,
                    excludedLifecycles,
                    excludedReviewStatuses,
                    maxFanout,
                    cancellationToken).ConfigureAwait(false);

                var filtered = relations
                    .Where(r => IsAllowedType(r, profile))
                    .Where(r => r.Confidence >= minConfidence)
                    .Where(r => IsAllowedLifecycle(r, profile))
                    .Where(r => visitedEdges.Add(EdgeKey(r)))
                    .OrderByDescending(r => ResolveWeight(r, profile))
                    .ThenByDescending(r => r.Confidence)
                    .ThenByDescending(r => r.CreatedAt)
                    .Take(maxFanout)
                    .ToArray();

                foreach (var relation in filtered)
                {
                    relationCount++;
                    if (relationCount > maxRelations || discoveredCount >= maxNodes)
                    {
                        truncated = true;
                        break;
                    }

                    var neighborId = ResolveNeighborId(relation, node.ItemId);
                    if (string.IsNullOrWhiteSpace(neighborId))
                    {
                        continue;
                    }

                    // GRAPH-10：修正 incoming path 方向 — 入边用 <-[type]- 箭头
                    var isOutgoing = string.Equals(relation.SourceId, node.ItemId, StringComparison.OrdinalIgnoreCase);
                    var path = isOutgoing
                        ? $"{node.Path} -[{relation.RelationType}]-> {neighborId}"
                        : $"{node.Path} <-[{relation.RelationType}]- {neighborId}";
                    maxDepthReached = Math.Max(maxDepthReached, depth);
                    edges.Add(new RelationTraversalEdge(relation, depth, node.Score, path, neighborId));

                    // GRAPH-10：移除 per-node nextFrontier.Count < maxFanout 限制，统一在层末截断
                    if (visitedNodes.Add(neighborId))
                    {
                        discoveredCount++;
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

    /// <summary>
    /// GRAPH-10：使用统一 RelationNeighborQuery 查询邻居，将方向/类型/置信度/生命周期过滤下推到存储层。
    /// Both 方向只需一次查询（而非原来的 QueryBySource + QueryByTarget 两次）。
    /// </summary>
    private async Task<IReadOnlyList<ContextRelation>> QueryRelationsAsync(
        string workspaceId,
        string? collectionId,
        string itemId,
        RelationDirection direction,
        RelationExpansionProfile profile,
        double minConfidence,
        IReadOnlyList<string> excludedLifecycles,
        IReadOnlyList<string> excludedReviewStatuses,
        int maxFanout,
        CancellationToken cancellationToken)
    {
        // 如果 profile 只允许单一类型，下推到存储层
        string? relationType = null;
        if (profile.AllowedRelationTypes.Count == 1)
        {
            relationType = profile.AllowedRelationTypes[0];
        }

        var query = new RelationNeighborQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ItemId = itemId,
            Direction = direction,
            RelationType = relationType,
            MinConfidence = minConfidence,
            ExcludedLifecycles = excludedLifecycles,
            ExcludedReviewStatuses = excludedReviewStatuses,
            Take = Math.Max(maxFanout * 2, 50),
            Skip = 0,
            MaxScan = Math.Max(maxFanout * 10, 500)
        };

        return await _relationStore!.QueryNeighborsAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveNeighborId(ContextRelation relation, string currentNodeId)
    {
        if (string.Equals(relation.SourceId, currentNodeId, StringComparison.OrdinalIgnoreCase))
        {
            return relation.TargetId;
        }
        return relation.SourceId;
    }

    /// <summary>
    /// GRAPH-10：边去重 key — 使用 relation 的正式 SourceId/TargetId 规范化，
    /// 确保 A→B 和 B→A 两条不同的有向边不会被错误合并。
    /// </summary>
    private static string EdgeKey(ContextRelation relation)
    {
        return $"{relation.SourceId}\u001f{relation.TargetId}\u001f{relation.RelationType}";
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

        // GRAPH-08：正式字段作为唯一来源；Rejected lifecycle 与 Deprecated 同级别排除
        if (!profile.AllowDeprecatedRelations
            && (string.Equals(lifecycle, RelationLifecycles.Deprecated, StringComparison.OrdinalIgnoreCase)
                || string.Equals(lifecycle, RelationLifecycles.Superseded, StringComparison.OrdinalIgnoreCase)
                || string.Equals(lifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase)))
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

/// <summary>
/// 从 <see cref="RelationTraversalResult"/> 构建 <see cref="RelationSubgraph"/> 的静态辅助类。
/// 提取去重后的节点列表与扁平化的边列表，供 ControlRoom 命令和 Service 端点复用。
/// </summary>
public static class RelationSubgraphBuilder
{
    public static RelationSubgraph Build(
        string rootItemId,
        RelationTraversalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var nodes = new List<RelationSubgraphNode>();
        var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 添加根节点
        if (!string.IsNullOrWhiteSpace(rootItemId))
        {
            nodes.Add(new RelationSubgraphNode { ItemId = rootItemId, Depth = 0 });
            nodeIds.Add(rootItemId);
        }

        // 从边中提取 neighbor 节点（depth = edge.Depth）
        foreach (var edge in result.Edges)
        {
            var neighborId = edge.NeighborId;
            if (nodeIds.Add(neighborId))
            {
                nodes.Add(new RelationSubgraphNode
                {
                    ItemId = neighborId,
                    Depth = edge.Depth,
                    NodeKind = string.Equals(edge.Relation.TargetId, neighborId, StringComparison.OrdinalIgnoreCase)
                        ? edge.Relation.TargetNodeKind
                        : edge.Relation.SourceNodeKind
                });
            }
        }

        var edges = result.Edges.Select(edge => new RelationSubgraphEdge
        {
            RelationId = edge.Relation.Id,
            SourceId = edge.Relation.SourceId,
            TargetId = edge.Relation.TargetId,
            RelationType = edge.Relation.RelationType,
            Weight = edge.Relation.Weight,
            Confidence = edge.Relation.Confidence,
            Lifecycle = edge.Relation.Lifecycle,
            ReviewStatus = edge.Relation.ReviewStatus,
            Depth = edge.Depth
        }).ToArray();

        return new RelationSubgraph
        {
            RootItemId = rootItemId ?? string.Empty,
            Nodes = nodes.ToArray(),
            Edges = edges,
            MaxDepthReached = result.MaxDepthReached,
            Truncated = result.Truncated,
            Warnings = result.Warnings
        };
    }
}
