using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>
/// Selected 关系批量水合默认实现。
/// <para>
/// 探测语义与 <see cref="IRelationStreamStore"/> 一致：对已解析的 <see cref="IRelationStore"/>
/// 执行 <c>as IRelationHydrationStore</c>——实现了批量水合能力则单次存储访问批量返回；
/// 未实现则回退 <see cref="IRelationStore.GetAsync"/> 逐条获取（缺失的 ID 记为 missing）。
/// </para>
/// <para>
/// 设计原则：
/// <list type="bullet">
/// <item>不强制存储实现额外接口：批量水合是可选能力，探测失败自动降级。</item>
/// <item>请求 ID 自动去重且保持请求顺序；响应统计恒等（RequestedCount = HydratedCount + MissingCount）。</item>
/// <item>纯只读路径：不写库、不触发物化，供检索/审计/诊断侧安全调用。</item>
/// </list>
/// </para>
/// </summary>
public sealed class DefaultSelectedRelationHydrationService : ISelectedRelationHydrationService
{
    private readonly IRelationStore _relationStore;
    private readonly IRelationHydrationStore? _hydrationStore;

    public DefaultSelectedRelationHydrationService(IRelationStore relationStore)
    {
        _relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
        // 探测批量水合能力（与 IRelationStreamStore 的 as 探测模式一致）。
        _hydrationStore = relationStore as IRelationHydrationStore;
    }

    /// <inheritdoc />
    public async Task<RelationHydrationResponse> HydrateAsync(
        RelationHydrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        if (request.RelationIds is null || request.RelationIds.Count == 0)
        {
            throw new ArgumentException("relationIds 至少需要 1 个关系 ID。", nameof(request));
        }

        // 去重且保持请求顺序；空白 ID 不参与下游查询（避免按空串查询），但计入 missing 统计。
        var requestedIds = request.RelationIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedIds.Length == 0)
        {
            throw new ArgumentException("relationIds 至少需要 1 个关系 ID。", nameof(request));
        }

        var queryIds = requestedIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        var relations = new List<ContextRelation>(queryIds.Length);
        var missing = new List<string>(requestedIds.Length);
        // 空白 ID 直接计入 missing（契约：跳过空白 ID 计入 missing 统计）。
        missing.AddRange(requestedIds.Where(id => string.IsNullOrWhiteSpace(id)));
        string source;

        if (queryIds.Length == 0)
        {
            // 全部为空白 ID：无有效查询键，跳过存储访问，直接返回全 missing。
            source = _hydrationStore is not null ? "relation-hydration-store" : "relation-store-fallback";
        }
        else if (_hydrationStore is not null)
        {
            source = "relation-hydration-store";
            var hydrated = await _hydrationStore.HydrateRelationsAsync(
                request.WorkspaceId, request.CollectionId, queryIds, cancellationToken).ConfigureAwait(false);
            // 存储实现不保证返回顺序（如 Postgres 按主键索引序）；先按 ID 建索引，
            // 再按请求顺序重排——契约要求 Relations 保持请求顺序（与回退路径一致）。
            var hydratedById = new Dictionary<string, ContextRelation>(StringComparer.Ordinal);
            foreach (var relation in hydrated)
            {
                hydratedById.TryAdd(relation.Id, relation);
            }
            foreach (var id in queryIds)
            {
                if (hydratedById.TryGetValue(id, out var relation))
                {
                    relations.Add(relation);
                }
                else
                {
                    missing.Add(id);
                }
            }
        }
        else
        {
            source = "relation-store-fallback";
            foreach (var id in queryIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relation = await _relationStore.GetAsync(
                    request.WorkspaceId, request.CollectionId ?? string.Empty, id, cancellationToken).ConfigureAwait(false);
                if (relation is null)
                {
                    missing.Add(id);
                }
                else
                {
                    relations.Add(relation);
                }
            }
        }

        return new RelationHydrationResponse
        {
            OperationId = request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            RequestedCount = requestedIds.Length,
            HydratedCount = relations.Count,
            MissingCount = missing.Count,
            Source = source,
            Relations = relations.Select(MapEntry).ToArray(),
            MissingIds = missing
        };
    }

    private static RelationHydrationEntry MapEntry(ContextRelation relation) => new()
    {
        RelationId = relation.Id,
        SourceId = relation.SourceId,
        TargetId = relation.TargetId,
        RelationType = relation.RelationType,
        Weight = relation.Weight,
        Confidence = relation.Confidence,
        Lifecycle = relation.Lifecycle,
        ReviewStatus = relation.ReviewStatus,
        SourceRefs = relation.SourceRefs,
        Metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase),
        Provenance = relation.Provenance
    };
}
