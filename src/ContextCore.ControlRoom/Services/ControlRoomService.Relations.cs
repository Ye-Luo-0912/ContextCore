using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.ControlRoom.Services;

public sealed partial class ControlRoomService
{

    public async Task<RelationGraph> GetRelationGraphAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var upstream = await _state.RelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = _state.WorkspaceId, CollectionId = _state.CollectionId, TargetId = id, Take = int.MaxValue }, cancellationToken).ConfigureAwait(false);
        var downstream = await _state.RelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = _state.WorkspaceId, CollectionId = _state.CollectionId, SourceId = id, Take = int.MaxValue }, cancellationToken).ConfigureAwait(false);

        return new RelationGraph
        {
            Id = id,
            Upstream = upstream,
            Downstream = downstream
        };
    }

    /// <summary>
    /// 构建以 <paramref name="itemId"/> 为根的关系子图。Direct 模式使用 <see cref="RelationTraversalEngine"/>；
    /// Service 模式调用 <c>GET /api/relations/{workspaceId}/{collectionId}/{itemId}/subgraph</c>。
    /// </summary>
    /// <param name="itemId">根条目 ID。</param>
    /// <param name="depth">最大遍历深度，默认 2。</param>
    /// <param name="direction">遍历方向（outgoing|incoming|both），默认 both。</param>
    /// <param name="allowedTypes">可选的允许关系类型白名单；为空表示不过滤。</param>
    public async Task<RelationSubgraph> GetRelationSubgraphAsync(
        string itemId,
        int depth = 2,
        string direction = "both",
        string[]? allowedTypes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        if (_state.IsServiceMode)
        {
            return await GetServiceClient().GetRelationSubgraphAsync(
                itemId,
                _state.WorkspaceId,
                _state.CollectionId,
                depth,
                direction,
                allowedTypes,
                cancellationToken).ConfigureAwait(false);
        }

        var parsedDirection = ParseRelationDirection(direction);
        var profile = BuildSubgraphProfile(depth, allowedTypes);
        var request = new RelationTraversalRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Seeds = [new RelationTraversalSeed(itemId)],
            Profile = profile,
            Direction = parsedDirection
        };

        var engine = new RelationTraversalEngine(_state.RelationStore);
        var result = await engine.TraverseAsync(request, cancellationToken).ConfigureAwait(false);
        var subgraph = RelationSubgraphBuilder.Build(itemId, result);
        return await EnrichSubgraphNodesAsync(subgraph, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GRAPH-13: 从记忆存储中查找节点元数据（标题/摘要/状态），丰富子图节点。</summary>
    private async Task<RelationSubgraph> EnrichSubgraphNodesAsync(RelationSubgraph subgraph, CancellationToken cancellationToken)
    {
        if (subgraph.Nodes.Count == 0)
        {
            return subgraph;
        }

        var itemIds = subgraph.Nodes.Select(static n => n.ItemId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lookup = new Dictionary<string, (string? Title, string? Summary, string? Lifecycle, string? ReviewStatus)>(StringComparer.OrdinalIgnoreCase);

        foreach (var itemId in itemIds)
        {
            var memory = await _state.MemoryStore.GetAsync(_state.WorkspaceId, _state.CollectionId, itemId, cancellationToken).ConfigureAwait(false);
            if (memory is not null)
            {
                var firstLine = memory.Content.AsSpan().Trim();
                var newlineIdx = firstLine.IndexOf('\n');
                var title = newlineIdx >= 0 ? firstLine[..newlineIdx].Trim().ToString() : firstLine.ToString();
                if (title.Length > 60) title = title[..60] + "…";
                lookup[itemId] = (title, memory.Content.Length > 120 ? memory.Content[..120] + "…" : memory.Content, memory.Status.ToString(), null);
                continue;
            }

            var context = await _state.ContextStore.GetAsync(_state.WorkspaceId, _state.CollectionId, itemId, cancellationToken).ConfigureAwait(false);
            if (context is not null)
            {
                var title = string.IsNullOrWhiteSpace(context.Title) ? context.Type : context.Title;
                lookup[itemId] = (title, context.Content.Length > 120 ? context.Content[..120] + "…" : context.Content, null, null);
            }
        }

        if (lookup.Count == 0)
        {
            return subgraph;
        }

        var enrichedNodes = subgraph.Nodes.Select(n =>
        {
            if (!lookup.TryGetValue(n.ItemId, out var meta))
            {
                return n;
            }
            return new RelationSubgraphNode
            {
                ItemId = n.ItemId,
                Depth = n.Depth,
                NodeKind = n.NodeKind,
                Title = meta.Title ?? n.Title,
                Summary = meta.Summary ?? n.Summary,
                Lifecycle = meta.Lifecycle ?? n.Lifecycle,
                ReviewStatus = meta.ReviewStatus ?? n.ReviewStatus
            };
        }).ToArray();

        return new RelationSubgraph
        {
            RootItemId = subgraph.RootItemId,
            Nodes = enrichedNodes,
            Edges = subgraph.Edges,
            MaxDepthReached = subgraph.MaxDepthReached,
            Truncated = subgraph.Truncated,
            Warnings = subgraph.Warnings
        };
    }

    private static RelationDirection ParseRelationDirection(string direction)
    {
        return direction?.ToLowerInvariant() switch
        {
            "outgoing" or "out" => RelationDirection.Outgoing,
            "incoming" or "in" => RelationDirection.Incoming,
            _ => RelationDirection.Both,
        };
    }

    /// <summary>
    /// P3.1-d：关系旧数据迁移 — 回填 NodeKind/Provenance/Lifecycle/ReviewStatus 正式字段。
    /// 支持 collection 范围限定、dry-run（默认）/--apply 实际写入、批量节点加载。
    /// 从 Metadata fallback 读取值写入正式字段，从 MemoryStore/ContextStore 批量推断 NodeKind，
    /// 从 Metadata createdFrom/source/generatedBy 推断 Provenance。
    /// 返回迁移统计。
    /// </summary>
    public async Task<RelationMigrationReport> MigrateRelationsAsync(
        RelationMigrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RelationMigrationOptions();

        var relations = await _state.RelationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = options.CollectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        // P3.1-d: 批量加载节点类型查找表（按关系所属 collection 分组），避免 N+1 GetAsync 调用。
        var nodeKindLookups = await BuildNodeKindLookupsAsync(relations, cancellationToken).ConfigureAwait(false);

        var toUpdate = new List<ContextRelation>();
        var stats = new RelationMigrationReport
        {
            TotalRelations = relations.Count,
            DryRun = !options.Apply
        };

        foreach (var relation in relations)
        {
            var changed = false;
            var sourceNodeKind = relation.SourceNodeKind;
            var targetNodeKind = relation.TargetNodeKind;
            var lifecycle = relation.Lifecycle;
            var reviewStatus = relation.ReviewStatus;
            var provenance = relation.Provenance;

            var lookup = nodeKindLookups.TryGetValue(relation.CollectionId, out var l)
                ? l
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 回填 NodeKind：从批量查找表推断
            if (string.IsNullOrWhiteSpace(sourceNodeKind))
            {
                sourceNodeKind = InferNodeKind(relation.SourceId, lookup);
                if (!string.IsNullOrWhiteSpace(sourceNodeKind))
                {
                    changed = true;
                    stats.NodeKindBackfilled++;
                }
            }
            if (string.IsNullOrWhiteSpace(targetNodeKind))
            {
                targetNodeKind = InferNodeKind(relation.TargetId, lookup);
                if (!string.IsNullOrWhiteSpace(targetNodeKind))
                {
                    changed = true;
                    stats.NodeKindBackfilled++;
                }
            }

            // 回填 Lifecycle：从 Metadata fallback 读取
            if (string.IsNullOrWhiteSpace(lifecycle) || lifecycle == RelationLifecycles.Active)
            {
                var metadataLifecycle = ReadMetadataValue(relation.Metadata, "lifecycle");
                if (!string.IsNullOrWhiteSpace(metadataLifecycle)
                    && !string.Equals(metadataLifecycle, lifecycle, StringComparison.OrdinalIgnoreCase))
                {
                    lifecycle = metadataLifecycle;
                    changed = true;
                    stats.LifecycleBackfilled++;
                }
            }

            // 回填 ReviewStatus：从 Metadata fallback 读取
            if (string.IsNullOrWhiteSpace(reviewStatus))
            {
                var metadataReviewStatus = ReadMetadataValue(relation.Metadata, "reviewStatus");
                if (!string.IsNullOrWhiteSpace(metadataReviewStatus))
                {
                    reviewStatus = metadataReviewStatus;
                    changed = true;
                    stats.ReviewStatusBackfilled++;
                }
            }

            // 回填 Provenance：从 Metadata createdFrom/source/generatedBy 推断
            if (string.IsNullOrWhiteSpace(provenance))
            {
                provenance = InferProvenance(relation.Metadata);
                if (!string.IsNullOrWhiteSpace(provenance))
                {
                    changed = true;
                    stats.ProvenanceBackfilled++;
                }
            }

            if (changed)
            {
                toUpdate.Add(new ContextRelation
                {
                    Id = relation.Id,
                    WorkspaceId = relation.WorkspaceId,
                    CollectionId = relation.CollectionId,
                    SourceId = relation.SourceId,
                    TargetId = relation.TargetId,
                    RelationType = relation.RelationType,
                    Weight = relation.Weight,
                    Confidence = relation.Confidence,
                    SourceRefs = relation.SourceRefs.ToArray(),
                    Metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase),
                    CreatedAt = relation.CreatedAt,
                    SourceNodeKind = sourceNodeKind,
                    TargetNodeKind = targetNodeKind,
                    Lifecycle = lifecycle,
                    ReviewStatus = reviewStatus,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Provenance = provenance
                });
            }
            else
            {
                stats.SkippedRelations++;
            }
        }

        // P3.1-d: 仅在显式 --apply 时写入，dry-run 不落盘
        if (options.Apply && toUpdate.Count > 0)
        {
            await _state.RelationStore.BatchUpsertAsync(toUpdate, cancellationToken).ConfigureAwait(false);
        }

        stats.UpdatedRelations = toUpdate.Count;
        return stats;
    }

    /// <summary>P3.1-d：按 collection 批量加载 MemoryStore/ContextStore 构建 itemId -> NodeKind 查找表。</summary>
    private async Task<Dictionary<string, Dictionary<string, string>>> BuildNodeKindLookupsAsync(
        IReadOnlyList<ContextRelation> relations,
        CancellationToken cancellationToken)
    {
        var lookups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // 仅对需要推断 NodeKind 的关系所属 collection 建表
        var collectionsNeedingLookup = relations
            .Where(r => string.IsNullOrWhiteSpace(r.SourceNodeKind) || string.IsNullOrWhiteSpace(r.TargetNodeKind))
            .Select(r => r.CollectionId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var collectionId in collectionsNeedingLookup)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var memories = await _state.MemoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = _state.WorkspaceId,
                CollectionId = collectionId.Length == 0 ? null : collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            foreach (var memory in memories)
            {
                lookup[memory.Id] = ClassifyNodeKind(memory);
            }

            var contexts = await _state.ContextStore.QueryAsync(new ContextQuery
            {
                WorkspaceId = _state.WorkspaceId,
                CollectionId = collectionId.Length == 0 ? null : collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            foreach (var context in contexts)
            {
                // MemoryStore 优先（已分类），仅对未命中的 context 条目补 ContextItem
                if (!lookup.ContainsKey(context.Id))
                {
                    lookup[context.Id] = nameof(GraphNodeKind.ContextItem);
                }
            }

            lookups[collectionId] = lookup;
        }

        return lookups;
    }

    /// <summary>P3.1-d：从批量查找表推断单个条目的 NodeKind。</summary>
    private static string InferNodeKind(string itemId, Dictionary<string, string> lookup)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return string.Empty;
        }

        return lookup.TryGetValue(itemId, out var kind) ? kind : string.Empty;
    }

    /// <summary>P3.1-d：根据记忆层与类型分类 NodeKind（原 InferNodeKindAsync 的分类逻辑）。</summary>
    private static string ClassifyNodeKind(ContextMemoryItem memory)
    {
        if (memory.Layer == ContextMemoryLayer.Global)
        {
            return nameof(GraphNodeKind.GlobalMemory);
        }
        if (memory.Layer == ContextMemoryLayer.Stable)
        {
            if (memory.Type.Contains("constraint", StringComparison.OrdinalIgnoreCase))
            {
                return nameof(GraphNodeKind.StableConstraint);
            }
            if (memory.Type.Contains("decision", StringComparison.OrdinalIgnoreCase))
            {
                return nameof(GraphNodeKind.DecisionRecord);
            }
            return nameof(GraphNodeKind.StableMemory);
        }
        // Working/Candidate layer
        if (memory.Type.Contains("constraint", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(GraphNodeKind.CandidateConstraint);
        }
        return nameof(GraphNodeKind.CandidateMemory);
    }

    /// <summary>P3-03：从 Metadata createdFrom/source/generatedBy 推断 Provenance。</summary>
    private static string? InferProvenance(IReadOnlyDictionary<string, string> metadata)
    {
        var createdFrom = ReadMetadataValue(metadata, "createdFrom", "source", "generatedBy");
        if (string.IsNullOrWhiteSpace(createdFrom))
        {
            return null;
        }

        if (createdFrom.Contains("compression", StringComparison.OrdinalIgnoreCase))
        {
            return "compression";
        }
        if (createdFrom.Contains("promotion", StringComparison.OrdinalIgnoreCase))
        {
            return "promotion";
        }
        if (createdFrom.Contains("lifecycle", StringComparison.OrdinalIgnoreCase)
            || createdFrom.Contains("supersede", StringComparison.OrdinalIgnoreCase))
        {
            return "lifecycle-review";
        }
        if (createdFrom.Contains("fixture", StringComparison.OrdinalIgnoreCase)
            || createdFrom.Contains("deterministic", StringComparison.OrdinalIgnoreCase))
        {
            return "eval-fixture";
        }
        if (createdFrom.Contains("ingest", StringComparison.OrdinalIgnoreCase))
        {
            return "ingest";
        }
        return createdFrom;
    }

    private static string ReadMetadataValue(IReadOnlyDictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return string.Empty;
    }

    private static RelationExpansionProfile BuildSubgraphProfile(int depth, string[]? allowedTypes)
    {
        var safeDepth = Math.Max(1, depth);
        return new RelationExpansionProfile
        {
            MaxDepth = safeDepth,
            MaxFanout = 16,
            MinConfidence = 0.0,
            AllowCandidateRelations = true,
            AllowDeprecatedRelations = true,
            AllowRejectedRelations = true,
            RequireEvidence = false,
            AllowedRelationTypes = allowedTypes is { Length: > 0 }
                ? allowedTypes
                : Array.Empty<string>()
        };
    }

    private Task<IReadOnlyList<ContextRelation>> QueryRelationsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        return _state.RelationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = take
        }, cancellationToken);
    }

    private Task<IReadOnlyList<ContextRelation>> GetRelationsForIdAsync(
        string id,
        CancellationToken cancellationToken)
    {
        return _state.RelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = _state.WorkspaceId, CollectionId = _state.CollectionId, ItemId = id, Take = int.MaxValue }, cancellationToken);
    }
}
