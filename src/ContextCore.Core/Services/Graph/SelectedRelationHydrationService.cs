using ContextCore.Abstractions;

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

        // 去重且保持请求顺序；跳过空白 ID（计入 missing 统计，避免下游按空串查询）。
        var uniqueIds = request.RelationIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (uniqueIds.Length == 0)
        {
            throw new ArgumentException("relationIds 至少需要 1 个有效关系 ID。", nameof(request));
        }

        var relations = new List<ContextRelation>(uniqueIds.Length);
        var missing = new List<string>(uniqueIds.Length);
        string source;

        if (_hydrationStore is not null)
        {
            source = "relation-hydration-store";
            var hydrated = await _hydrationStore.HydrateRelationsAsync(
                request.WorkspaceId, request.CollectionId, uniqueIds, cancellationToken).ConfigureAwait(false);
            var hydratedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relation in hydrated)
            {
                if (hydratedIds.Add(relation.Id))
                {
                    relations.Add(relation);
                }
            }
            foreach (var id in uniqueIds)
            {
                if (!hydratedIds.Contains(id))
                {
                    missing.Add(id);
                }
            }
        }
        else
        {
            source = "relation-store-fallback";
            foreach (var id in uniqueIds)
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
            RequestedCount = uniqueIds.Length,
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
