using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Graph;

namespace ContextCore.Core;

/// <summary>
/// 协调图谱扩展（graph expansion）相关的种子解析、关系遍历、section 追加和 contribution 构建。
/// 持有 store / relationStore / traversalEngine / tokenizer 依赖，
/// 使 <see cref="BasicContextPackageBuilder"/> 不再直接持有图谱扩展状态。
/// </summary>
internal sealed class GraphExpansionCoordinator
{
    private readonly IContextStore _store;
    private readonly IRelationStore? _relationStore;
    private readonly RelationTraversalEngine? _traversalEngine;

    /// <summary>
    /// 是否配置了关系存储（仅当 relationStore 非空时才启用图谱种子解析和关系扩展）。
    /// </summary>
    internal bool IsConfigured => _relationStore is not null;

    internal GraphExpansionCoordinator(
        IContextStore store,
        IRelationStore? relationStore,
        RelationTraversalEngine? traversalEngine)
    {
        _store = store;
        _relationStore = relationStore;
        _traversalEngine = traversalEngine;
    }

    internal async Task<IReadOnlyList<string>> ResolveGraphSeedIdsFromWorkingMemoryAsync(
        string workspaceId,
        string collectionId,
        IReadOnlyList<ContextMemoryItem> workingMemory,
        IReadOnlyList<ContextAnchor> anchors,
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        CancellationToken cancellationToken)
    {
        if (workingMemory.Count == 0 && anchors.Count == 0)
        {
            return Array.Empty<string>();
        }

        var maxSeeds = PackagePolicyResolver.ResolveIntSetting(request, policy, "graphSeedMaxNodes", 12, min: 1, max: 50);
        var candidates = GraphSeedResolver.ExtractGraphSeedCandidates(workingMemory, anchors)
            .Select(GraphSeedResolver.NormalizeGraphSeedCandidate)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxSeeds * 4)
            .ToArray();
        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (resolved.Count >= maxSeeds)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var direct = await _store.GetAsync(
                workspaceId,
                collectionId,
                candidate!,
                cancellationToken).ConfigureAwait(false);
            if (direct is not null && seen.Add(direct.Id))
            {
                resolved.Add(direct.Id);
                continue;
            }

            // refs 查询只看元数据索引，避免为了抽取图谱种子而做内容级全量扫描。
            var refMatches = await _store.QueryAsync(
                new ContextQuery
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    Refs = [candidate!],
                    Take = Math.Max(2, maxSeeds - resolved.Count),
                    IncludeContent = false
                },
                cancellationToken).ConfigureAwait(false);
            foreach (var item in refMatches)
            {
                if (resolved.Count >= maxSeeds)
                {
                    break;
                }

                if (seen.Add(item.Id))
                {
                    resolved.Add(item.Id);
                }
            }
        }

        return resolved;
    }

    internal async Task<IReadOnlyList<ContextItem>> ResolveRelatedContextAsync(
        string workspaceId,
        string collectionId,
        IEnumerable<string> sourceIds,
        ContextPackageRequest request,
        ContextPackagePolicy policy,
        ICollection<ContextRelation> lowConfidenceRelations,
        CancellationToken cancellationToken)
    {
        var seedIds = sourceIds
            .Where(sourceId => !string.IsNullOrWhiteSpace(sourceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (seedIds.Length == 0)
        {
            return Array.Empty<ContextItem>();
        }

        var relationTypes = PackagePolicyResolver.ResolveRelationTypeWhitelist(request, policy);
        var maxDepth = PackagePolicyResolver.ResolveIntSetting(request, policy, "relationExpansionDepth", 1, min: 1, max: 2);
        var maxNodes = PackagePolicyResolver.ResolveIntSetting(request, policy, "relationMaxNodes", 20, min: 1, max: 100);
        var maxRelations = PackagePolicyResolver.ResolveIntSetting(request, policy, "relationMaxRelations", 60, min: 1, max: 300);
        var minConfidence = PackagePolicyResolver.ResolveDoubleSetting(request, policy, "relationMinConfidence", 0.35, min: 0, max: 1);

        // 通过统一遍历引擎执行双向 bfs；engine 不过滤置信度（MinConfidence=0），由 caller 做置信度过滤和 low-confidence 收集。
        var profile = new RelationExpansionProfile
        {
            ProfileId = "package-builder",
            Mode = "Normal",
            MaxDepth = maxDepth,
            MaxFanout = Math.Max(20, maxRelations),
            AllowedRelationTypes = [..relationTypes],
            MinConfidence = 0.0,
            AllowDeprecatedRelations = true,
            AllowCandidateRelations = true,
            AllowRejectedRelations = true,
            RequireEvidence = false
        };

        var traversalRequest = new RelationTraversalRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Seeds = seedIds.Select(seedId => new RelationTraversalSeed(seedId)).ToArray(),
            Profile = profile,
            Direction = RelationDirection.Both,
            MaxNodesOverride = maxNodes,
            MaxRelationsOverride = maxRelations
        };

        var engine = _traversalEngine ?? new RelationTraversalEngine(_relationStore);
        var traversalResult = await engine.TraverseAsync(traversalRequest, cancellationToken).ConfigureAwait(false);

        var relatedItems = new List<ContextItem>();
        var relatedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // low-confidence 关系收集：在置信度过滤前采集，按置信度升序取前 20，去重。
        foreach (var relation in traversalResult.Edges
            .Where(edge => edge.Relation.Confidence < minConfidence)
            .OrderBy(edge => edge.Relation.Confidence)
            .Take(20)
            .Select(edge => edge.Relation))
        {
            if (!lowConfidenceRelations.Any(item => string.Equals(item.Id, relation.Id, StringComparison.OrdinalIgnoreCase)))
            {
                lowConfidenceRelations.Add(relation);
            }
        }

        var containsDeprecatedKeywordInQuery = !string.IsNullOrWhiteSpace(request.QueryText) && (
            request.QueryText.Contains("废弃", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("作废", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            || request.QueryText.Contains("deprecated", StringComparison.OrdinalIgnoreCase));

        var scannedRelations = 0;
        foreach (var edge in traversalResult.Edges
            .Where(e => e.Relation.Confidence >= minConfidence)
            .OrderByDescending(e => e.Relation.Weight)
            .ThenByDescending(e => e.Relation.Confidence))
        {
            scannedRelations++;
            if (scannedRelations > maxRelations || relatedItems.Count >= maxNodes)
            {
                break;
            }

            var relatedId = edge.NeighborId;
            if (string.IsNullOrWhiteSpace(relatedId) || !relatedItemIds.Add(relatedId))
            {
                continue;
            }

            var target = await _store.GetAsync(
                workspaceId,
                collectionId,
                relatedId,
                cancellationToken).ConfigureAwait(false);

            if (target is null)
            {
                continue;
            }

            var isDeprecated = target.Tags.Any(tag =>
                string.Equals(tag, "deprecated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "legacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "superseded", StringComparison.OrdinalIgnoreCase));

            if (!isDeprecated || containsDeprecatedKeywordInQuery)
            {
                relatedItems.Add(target);
            }
        }

        return relatedItems
            .OrderByDescending(item => item.Importance)
            .ThenByDescending(item => item.UpdatedAt)
            .ToArray();
    }
}
