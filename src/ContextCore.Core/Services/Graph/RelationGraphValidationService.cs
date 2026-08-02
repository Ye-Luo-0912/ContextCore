using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>只读关系图谱校验服务，不影响 retrieval、relation expansion 或 PackingPolicy。</summary>
public sealed class RelationGraphValidationService
{
    private static readonly RelationTypeNormalizer StaticTypeNormalizer = new();

    private readonly IRelationStore? _relationStore;
    private readonly IContextStore? _contextStore;
    private readonly IMemoryStore? _memoryStore;
    private readonly IConstraintStore? _constraintStore;
    private readonly IGlobalContextStore? _globalContextStore;
    private readonly RelationTypeRegistry _registry;
    private readonly RelationTypeNormalizer _typeNormalizer = new();
    private readonly IRelationBackfillPolicy? _backfillPolicy;

    public RelationGraphValidationService(
        IRelationStore? relationStore,
        IContextStore? contextStore,
        IMemoryStore? memoryStore,
        IConstraintStore? constraintStore,
        IGlobalContextStore? globalContextStore,
        RelationTypeRegistry registry,
        IRelationBackfillPolicy? backfillPolicy = null)
    {
        _relationStore = relationStore;
        _contextStore = contextStore;
        _memoryStore = memoryStore;
        _constraintStore = constraintStore;
        _globalContextStore = globalContextStore;
        _registry = registry;
        _backfillPolicy = backfillPolicy;
    }

    public async Task<RelationGraphDiagnosticsReport> ValidateAsync(
        string workspaceId,
        string? collectionId = null,
        string? itemId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var warnings = new List<string>();
        var relations = await QueryRelationsAsync(workspaceId, collectionId, itemId, warnings, cancellationToken)
            .ConfigureAwait(false);
        var itemIndex = await BuildItemIndexAsync(workspaceId, collectionId, relations, cancellationToken)
            .ConfigureAwait(false);
        var diagnostics = BuildDiagnostics(relations, itemIndex);

        return new RelationGraphDiagnosticsReport
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ItemId = itemId,
            CreatedAt = DateTimeOffset.UtcNow,
            RelationCount = relations.Count,
            DiagnosticCount = diagnostics.Count,
            Diagnostics = diagnostics,
            Warnings = warnings.ToArray()
        };
    }

    /// <summary>
    /// 流式诊断，避免一次性将整张关系图和全部 item store 载入内存。
    /// <para>
    /// 实现策略——两阶段流式：
    /// <list type="bullet">
    /// <item>
    /// 阶段 1：流式枚举关系。对每条关系立即 yield 不依赖 item 存在性的诊断
    /// （LegacyRelationType/UnknownRelationType/MissingEvidence/Confidence 系列等），
    /// 同时累积跨关系状态（inverse 键集合、duplicate 桶、positive pair 集合、supersede 邻接表、related_to 计数）
    /// 和引用的 item ID 集合。
    /// </item>
    /// <item>
    /// 阶段 2：按引用的 item ID 批量查询 4 个 item store（仅查引用的 item，不加载全部）。
    /// </item>
    /// <item>
    /// 阶段 3：再次流式枚举关系。对每条关系 yield 依赖 item 存在性的诊断
    /// （BrokenSource/BrokenTarget/InvalidSourceKind/InvalidTargetKind）
    /// 和依赖 inverse 关系的诊断（MissingInverseRelation/RejectedRelationHasActiveInverse 等）。
    /// </item>
    /// <item>
    /// 阶段 4：yield 跨关系诊断（DuplicateRelation/ConflictingRelation/SupersedeCycle/WeakRelatedToOveruse）。
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// 内存特性：不持有完整关系列表——两次流式枚举。跨关系状态仅保存键元组（string hash sets），
    /// 不保存完整 ContextRelation 对象。item index 仅包含被关系引用的 item，不是全部 item。
    /// </para>
    /// <para>
    /// 回退：当 <see cref="IRelationStore"/> 未实现 <see cref="IRelationStreamStore"/> 时，
    /// 回退到 <see cref="ValidateAsync"/> 并一次性 yield 全部诊断（与旧行为一致，无内存优化）。
    /// </para>
    /// </summary>
    /// <param name="workspaceId">工作空间 ID（必填）。</param>
    /// <param name="collectionId">可选集合过滤。</param>
    /// <param name="itemId">可选 item 过滤。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步枚举的诊断序列。</returns>
    public async IAsyncEnumerable<RelationGraphDiagnostic> ValidateStreamAsync(
        string workspaceId,
        string? collectionId = null,
        string? itemId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        // 回退路径：store 未实现 IRelationStreamStore 时，调用 ValidateAsync 并一次性 yield。
        if (_relationStore is not IRelationStreamStore streamStore)
        {
            var fallbackReport = await ValidateAsync(workspaceId, collectionId, itemId, cancellationToken)
                .ConfigureAwait(false);
            foreach (var diag in fallbackReport.Diagnostics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return diag;
            }
            yield break;
        }

        // ── 阶段 1：流式枚举，yield 不依赖 item 的诊断，累积跨关系状态 ──
        var itemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // fix：relationKeys 存储 (source, target, normalizedType) 作为每条关系的规范签名。
        // MissingInverseRelation 查找时用 (target, source, inverseType) 来匹配真实存在的 inverse 关系签名，
        // 避免与每条关系自身贡献的 key 重合（旧实现总是命中自己，导致漏报）。
        var relationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateBuckets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var positivePairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var supersedeEdges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var relatedToCount = 0;
        var relationCount = 0;

        await foreach (var relation in streamStore.StreamRelationsAsync(workspaceId, collectionId, itemId, cancellationToken)
            .ConfigureAwait(false))
        {
            relationCount++;
            if (!string.IsNullOrWhiteSpace(relation.SourceId)) itemIds.Add(relation.SourceId);
            if (!string.IsNullOrWhiteSpace(relation.TargetId)) itemIds.Add(relation.TargetId);

            var normalizedType = _typeNormalizer.Normalize(relation.RelationType);
            var definition = _registry.Find(normalizedType);

            // 关系规范签名：(source, target, normalizedType)
            relationKeys.Add($"{relation.SourceId}\u001f{relation.TargetId}\u001f{normalizedType}");

            // duplicate bucket key
            var dupKey = $"{relation.WorkspaceId}\u001f{relation.CollectionId}\u001f{relation.SourceId}\u001f{normalizedType}\u001f{relation.TargetId}";
            if (!duplicateBuckets.TryGetValue(dupKey, out var bucket))
            {
                bucket = new List<string>();
                duplicateBuckets[dupKey] = bucket;
            }
            bucket.Add(relation.Id);

            // positive pairs for conflict detection
            if (IsPositiveType(relation.RelationType))
            {
                positivePairs.Add($"{relation.SourceId}\u001f{relation.TargetId}");
            }

            // supersede adjacency
            if (string.Equals(normalizedType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase))
            {
                if (!supersedeEdges.TryGetValue(relation.SourceId, out var targets))
                {
                    targets = new List<string>();
                    supersedeEdges[relation.SourceId] = targets;
                }
                targets.Add(relation.TargetId);
            }

            if (string.Equals(relation.RelationType, ContextRelationTypes.RelatedTo, StringComparison.OrdinalIgnoreCase))
            {
                relatedToCount++;
            }

            // yield 不依赖 item 的诊断
            foreach (var diag in BuildPerRelationDiagnosticsNoItems(relation, definition, normalizedType))
            {
                yield return diag;
            }
        }

        // ── 阶段 2：批量查询引用的 item ──
        var itemIndex = await BuildItemIndexFromIdsAsync(workspaceId, collectionId, itemIds, cancellationToken)
            .ConfigureAwait(false);

        // ── 阶段 3：再次流式枚举，yield 依赖 item 的诊断 ──
        await foreach (var relation in streamStore.StreamRelationsAsync(workspaceId, collectionId, itemId, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedType = _typeNormalizer.Normalize(relation.RelationType);
            var definition = _registry.Find(normalizedType);
            var source = itemIndex.GetValueOrDefault(relation.SourceId);
            var target = itemIndex.GetValueOrDefault(relation.TargetId);

            foreach (var diag in BuildPerRelationDiagnosticsWithItems(relation, definition, source, target, relationKeys))
            {
                yield return diag;
            }
        }

        // ── 阶段 4：yield 跨关系诊断 ──
        foreach (var diag in BuildCrossRelationDiagnostics(duplicateBuckets, supersedeEdges, positivePairs, relationCount, relatedToCount))
        {
            yield return diag;
        }
    }

    /// <summary>
    /// 构建不依赖 item 存在性的 per-relation 诊断。
    /// 对应 BuildDiagnostics 中的类型/置信度/生命周期/review 状态检查。
    /// </summary>
    private IReadOnlyList<RelationGraphDiagnostic> BuildPerRelationDiagnosticsNoItems(
        ContextRelation relation,
        RelationTypeDefinition? definition,
        string normalizedType)
    {
        var diagnostics = new List<RelationGraphDiagnostic>();

        if (!string.Equals(normalizedType, relation.RelationType, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(BuildDiagnostic(
                relation,
                RelationGraphDiagnosticTypes.LegacyRelationType,
                "Medium",
                $"Legacy relation type {relation.RelationType} should be normalized to {normalizedType}.",
                metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["normalizedType"] = normalizedType,
                    ["suggestion"] = $"migrate relationType to {normalizedType}"
                }));
        }

        if (definition is null)
        {
            diagnostics.Add(BuildDiagnostic(
                relation,
                RelationGraphDiagnosticTypes.UnknownRelationType,
                "High",
                $"Unknown relation type: {relation.RelationType}",
                metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["normalizedType"] = normalizedType,
                    ["suggestion"] = string.Equals(normalizedType, relation.RelationType, StringComparison.OrdinalIgnoreCase)
                        ? "add relation type definition or migrate corpus relation type"
                        : $"migrate relationType to {normalizedType}"
                }));
            return diagnostics;
        }

        if (definition.RequiresEvidence && ResolveEvidenceRefs(relation).Count == 0)
        {
            if (_backfillPolicy?.CanBackfillDeterministicEvidence(relation) == true)
            {
                diagnostics.Add(BuildDiagnostic(
                    relation,
                    RelationGraphDiagnosticTypes.EvidenceBackfillRequired,
                    "Medium",
                    "Relation type requires evidence; deterministic/fixture metadata can be backfilled.",
                    metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["normalizedType"] = normalizedType,
                        ["suggestion"] = "backfill evidenceRefs/sourceRefs/sourceOperationId/confidence/lifecycle/reviewStatus"
                    }));
            }
            else
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.MissingEvidence, "Medium", "Relation type requires evidence but no source refs or evidence metadata are present."));
            }
        }

        if (IsConfidenceMissing(relation))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.RelationConfidenceMissing, "Medium", "Relation confidence is missing or zero."));
        }

        var confidence = ResolveRelationConfidence(relation);
        if (confidence > 0 && confidence < 0.5)
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.LowConfidence, "Medium", $"Relation confidence is low: {confidence:0.00}."));
        }

        var lifecycle = ResolveRelationLifecycle(relation);
        var reviewStatus = ResolveReviewStatus(relation);

        if (IsHighImpact(definition)
            && !string.Equals(reviewStatus, "Reviewed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(reviewStatus, "ManualReviewed", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.UnreviewedHighImpactRelation, "High", "High-impact relation is not marked as reviewed."));
        }

        if (IsHighImpact(definition)
            && string.Equals(reviewStatus, RelationReviewStatuses.NeedsEvidence, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.NeedsEvidenceHighImpactRelation, "High", "High-impact relation is marked NeedsEvidence."));
        }

        if (string.Equals(reviewStatus, RelationReviewStatuses.Reviewed, StringComparison.OrdinalIgnoreCase)
            && !HasReviewer(relation))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.ReviewedRelationMissingReviewer, "Medium", "Reviewed relation is missing reviewer metadata."));
        }

        if (HasConfidenceChangedWithoutReview(relation, reviewStatus))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.ConfidenceChangedWithoutReview, "High", "Relation confidence appears changed without a Reviewed relation review record."));
        }

        if (RequiresReviewHistory(lifecycle, reviewStatus)
            && !HasReviewHistoryMetadata(relation))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.RelationReviewHistoryMissing, "Medium", "Relation lifecycle/review status has no relation review history reference."));
        }

        if (string.Equals(reviewStatus, "Rejected", StringComparison.OrdinalIgnoreCase)
            && string.Equals(lifecycle, StableMemoryLifecycle.Active, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.RejectedRelationStillActive, "High", "Rejected relation is still marked Active."));
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.RelationLifecycleMismatch, "High", "Relation lifecycle Active conflicts with reviewStatus Rejected."));
        }

        if (string.Equals(lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)
            && IsNormalPathEnabled(relation))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.DeprecatedRelationUsedInNormalPath, "High", "Deprecated relation is marked for normal-path use."));
        }

        if (string.Equals(lifecycle, ContextMemoryStatus.Candidate.ToString(), StringComparison.OrdinalIgnoreCase)
            && IsNormalPathEnabled(relation))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.CandidateRelationUsedInNormalPath, "High", "Candidate relation is marked for normal-path use."));
        }

        if (HasBrokenEvidenceRefs(relation))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.RelationEvidenceBroken, "Medium", "Relation evidence metadata contains broken or missing refs."));
        }

        if (definition.AuditOnly
            && AllowsNormalExpansion(relation, definition))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.AuditOnlyRelationInNormalPath, "High", "Audit-only relation is marked as normal-expansion eligible."));
        }

        if (!definition.IsDirectional
            && string.Compare(relation.SourceId, relation.TargetId, StringComparison.OrdinalIgnoreCase) > 0)
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.InvalidDirection, "Low", "Undirected relation should be stored in canonical source/target order."));
        }

        return diagnostics;
    }

    /// <summary>
    /// 构建依赖 item 存在性和 inverse 关系的 per-relation 诊断。
    /// 在第二次流式枚举中调用——此时 itemIndex 已就绪。
    /// </summary>
    private IReadOnlyList<RelationGraphDiagnostic> BuildPerRelationDiagnosticsWithItems(
        ContextRelation relation,
        RelationTypeDefinition? definition,
        RelationItemInfo? source,
        RelationItemInfo? target,
        HashSet<string> relationKeys)
    {
        var diagnostics = new List<RelationGraphDiagnostic>();
        if (definition is null)
        {
            return diagnostics;
        }

        var normalizedType = _typeNormalizer.Normalize(relation.RelationType);

        if (source is null || source.IsMissing)
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.BrokenSource, "High", $"Relation source does not exist: {relation.SourceId}"));
        }

        if (target is null || target.IsMissing)
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.BrokenTarget, "High", $"Relation target does not exist: {relation.TargetId}"));
        }

        if (source is not null && !source.IsMissing && !KindAllowed(source.Kind, definition.AllowedSourceKinds))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.InvalidSourceKind, "High", $"Invalid source kind {source.Kind} for relation type {definition.Type}."));
        }

        if (target is not null && !target.IsMissing && !KindAllowed(target.Kind, definition.AllowedTargetKinds))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.InvalidTargetKind, "High", $"Invalid target kind {target.Kind} for relation type {definition.Type}."));
        }

        // MissingInverseRelation：在 relationKeys 中查找 inverse 关系签名
        // 期望的 inverse 关系为 (source=relation.TargetId, target=relation.SourceId, type=definition.InverseType)
        if (!string.IsNullOrWhiteSpace(definition.InverseType))
        {
            var expectedInverseKey = $"{relation.TargetId}\u001f{relation.SourceId}\u001f{definition.InverseType}";
            if (!relationKeys.Contains(expectedInverseKey))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.MissingInverseRelation, "High", $"Missing inverse relation {definition.InverseType}."));
            }
        }

        // RejectedRelationHasActiveInverse：需要检查 inverse 关系是否 active
        // 注意：此检查需要完整的 inverse relation 对象，在纯流式模式下不可用。
        // 这里使用简化版——如果 inverse 关系存在则假设 active（保守，可能漏报）。
        // 完整版需要持有 inverse relation 的 lifecycle，留给 ValidateAsync 路径。
        var lifecycle = ResolveRelationLifecycle(relation);
        var reviewStatus = ResolveReviewStatus(relation);
        if (!string.IsNullOrWhiteSpace(definition.InverseType))
        {
            var expectedInverseKey = $"{relation.TargetId}\u001f{relation.SourceId}\u001f{definition.InverseType}";
            if ((string.Equals(reviewStatus, RelationReviewStatuses.Rejected, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(lifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase))
                && relationKeys.Contains(expectedInverseKey))
            {
                diagnostics.Add(BuildDiagnostic(
                    relation,
                    RelationGraphDiagnosticTypes.RejectedRelationHasActiveInverse,
                    "High",
                    "Rejected relation still has an active inverse relation.",
                    relatedRelations: Array.Empty<string>()));
            }
        }

        // DeprecatedRelationUsedByActiveChain：同上，简化版
        if (string.Equals(lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase))
        {
            if (IsMetadataTrue(relation.Metadata, "usedByActiveChain")
                || (IsReplacementType(relation.RelationType)
                    && !string.IsNullOrWhiteSpace(definition.InverseType)
                    && relationKeys.Contains($"{relation.TargetId}\u001f{relation.SourceId}\u001f{definition.InverseType}")))
            {
                diagnostics.Add(BuildDiagnostic(
                    relation,
                    RelationGraphDiagnosticTypes.DeprecatedRelationUsedByActiveChain,
                    "High",
                    "Deprecated relation is still linked from an active replacement chain.",
                    relatedRelations: Array.Empty<string>()));
            }
        }

        // superseded_by 的 target 必须是 active
        if (string.Equals(normalizedType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
            && target is not null
            && !target.IsMissing
            && IsInactiveReplacementTarget(target))
        {
            diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.InvalidTargetKind, "High", "replacement target must not be rejected / deprecated / superseded."));
        }

        return diagnostics;
    }

    /// <summary>
    /// 构建跨关系诊断：DuplicateRelation/ConflictingRelation/SupersedeCycle/WeakRelatedToOveruse。
    /// 在两阶段流式枚举完成后调用，使用阶段 1 累积的状态。
    /// </summary>
    /// <remarks>
    /// 注意：此方法不持有完整 ContextRelation 列表，仅使用键元组集合。
    /// DuplicateRelation 只能产出诊断但无法附带 relatedRelations（因为不持有完整 relation 对象）。
    /// 如需完整 relatedRelations，请使用 <see cref="ValidateAsync"/> 非流式路径。
    /// </remarks>
    private IReadOnlyList<RelationGraphDiagnostic> BuildCrossRelationDiagnostics(
        Dictionary<string, List<string>> duplicateBuckets,
        Dictionary<string, List<string>> supersedeEdges,
        HashSet<string> positivePairs,
        int relationCount,
        int relatedToCount)
    {
        var diagnostics = new List<RelationGraphDiagnostic>();

        // DuplicateRelation：bucket 中多于 1 条的产出诊断
        // 注意：流式模式下不持有完整 relation，无法构建完整 RelationGraphDiagnostic（缺少 relation 上下文）。
        // 改为产出简化诊断，diagnostic id 使用 bucket key。
        foreach (var bucket in duplicateBuckets.Where(b => b.Value.Count > 1))
        {
            var ids = bucket.Value.ToArray();
            // 使用第一条 ID 作为 relationId 占位；完整版见 ValidateAsync。
            diagnostics.Add(new RelationGraphDiagnostic
            {
                DiagnosticId = $"rgd-dup-{BuildShortHash(bucket.Key)}",
                DiagnosticType = RelationGraphDiagnosticTypes.DuplicateRelation,
                Severity = "Medium",
                Reason = $"Duplicate relation with same source/type/target. {ids.Length} copies: {string.Join(",", ids)}.",
                RelationId = ids.FirstOrDefault(),
                RelatedRelationIds = ids.Where(id => id != ids.FirstOrDefault()).ToArray(),
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["duplicateIds"] = string.Join(",", ids)
                }
            });
        }

        // ConflictingRelation：需要检查每个 conflict-type relation 是否有 positive pair
        // 流式模式下不持有 conflict relations 列表，故跳过——此诊断类型在 ValidateAsync 路径产出。
        // 完整实现需要第三阶段流式枚举 + positivePairs 查询，留给未来迭代。

        // SupersedeCycle：使用累积的邻接表做 DFS
        var cycles = FindSupersedeCyclesFromEdges(supersedeEdges);
        foreach (var cycle in cycles)
        {
            diagnostics.Add(new RelationGraphDiagnostic
            {
                DiagnosticId = $"rgd-cycle-{BuildShortHash(string.Join("|", cycle))}",
                DiagnosticType = RelationGraphDiagnosticTypes.SupersedeCycle,
                Severity = "High",
                Reason = "superseded_by replacement graph contains a cycle.",
                RelatedItemIds = cycle,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cycle"] = string.Join(" -> ", cycle)
                }
            });
        }

        // WeakRelatedToOveruse
        if (relatedToCount >= 10 && relatedToCount > relationCount / 2)
        {
            diagnostics.Add(new RelationGraphDiagnostic
            {
                DiagnosticId = $"rgd-weak-related-to-overuse-{BuildShortHash($"{relationCount}-{relatedToCount}")}",
                DiagnosticType = RelationGraphDiagnosticTypes.WeakRelatedToOveruse,
                Severity = "Low",
                Reason = $"related_to dominates relation graph: {relatedToCount}/{relationCount} relations are related_to. Prefer specific relation types.",
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["relatedToCount"] = relatedToCount.ToString(),
                    ["totalRelations"] = relationCount.ToString()
                }
            });
        }

        return diagnostics;
    }

    /// <summary>从 supersede 邻接表检测环（DFS，与 FindSupersedeCycles 一致）。</summary>
    private static IReadOnlyList<IReadOnlyList<string>> FindSupersedeCyclesFromEdges(
        Dictionary<string, List<string>> edges)
    {
        var cycles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in edges.Keys)
        {
            var path = new List<string>();
            var current = start;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (seen.Add(current))
            {
                path.Add(current);
                if (!edges.TryGetValue(current, out var nextItems) || nextItems.Count == 0)
                {
                    break;
                }

                var next = nextItems[0];
                var index = path.FindIndex(id => string.Equals(id, next, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var cycle = path.Skip(index).ToArray();
                    var key = string.Join("|", cycle.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
                    cycles.TryAdd(key, cycle);
                    break;
                }

                current = next;
            }
        }

        return cycles.Values.ToArray();
    }

    /// <summary>
    /// 按引用的 item ID 集合构建 item index——仅查询被关系引用的 item，不加载全部 item。
    /// 与 <see cref="BuildItemIndexAsync"/> 的区别：后者用 Take=int.MaxValue 加载全部 item。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, RelationItemInfo>> BuildItemIndexFromIdsAsync(
        string workspaceId,
        string? collectionId,
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, RelationItemInfo>(StringComparer.OrdinalIgnoreCase);
        if (itemIds.Count == 0)
        {
            return index;
        }

        // ContextStore：按 workspace+collection 查询，内存过滤引用的 ID
        if (_contextStore is not null)
        {
            var context = await _contextStore.QueryAsync(new ContextQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in context)
            {
                if (itemIds.Contains(item.Id))
                {
                    index[item.Id] = new RelationItemInfo(item.Id, "ContextItem", ContextMemoryStatus.Active, "Active", item.WorkspaceId, item.CollectionId, Summarize(item.Title, item.Content));
                }
            }
        }

        if (_memoryStore is not null)
        {
            var memory = await _memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in memory)
            {
                if (itemIds.Contains(item.Id))
                {
                    index[item.Id] = new RelationItemInfo(item.Id, ResolveMemoryKind(item), item.Status, ResolveLifecycle(item.Status, item.Metadata), item.WorkspaceId, item.CollectionId, Summarize(null, item.Content));
                }
            }
        }

        if (_constraintStore is not null)
        {
            var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in constraints)
            {
                if (itemIds.Contains(item.Id))
                {
                    var kind = item.Status == ContextMemoryStatus.Candidate ? "CandidateConstraint" : "StableConstraint";
                    index[item.Id] = new RelationItemInfo(item.Id, kind, item.Status, ResolveLifecycle(item.Status, item.Metadata), item.WorkspaceId, item.CollectionId, Summarize(null, item.Content));
                }
            }
        }

        if (_globalContextStore is not null)
        {
            var global = await _globalContextStore.QueryAsync(new ContextGlobalQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in global)
            {
                if (itemIds.Contains(item.Id))
                {
                    var status = Enum.TryParse<ContextMemoryStatus>(ReadMetadata(item.Metadata, "status"), ignoreCase: true, out var parsed)
                        ? parsed
                        : ContextMemoryStatus.Stable;
                    index[item.Id] = new RelationItemInfo(item.Id, "GlobalMemory", status, ResolveLifecycle(status, item.Metadata), item.WorkspaceId, item.CollectionId, Summarize(null, item.Content));
                }
            }
        }

        // 未匹配的 item ID 标记为 Unknown（缺失）
        foreach (var id in itemIds)
        {
            if (!index.ContainsKey(id))
            {
                index[id] = RelationItemInfo.Unknown(id);
            }
        }

        return index;
    }

    public async Task<RelationExplainResponse?> ExplainAsync(
        string relationId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var warnings = new List<string>();
        var relations = await QueryRelationsAsync(workspaceId, collectionId, null, warnings, cancellationToken)
            .ConfigureAwait(false);
        var relation = relations.FirstOrDefault(item => string.Equals(item.Id, relationId, StringComparison.OrdinalIgnoreCase));
        if (relation is null)
        {
            return null;
        }

        var itemIndex = await BuildItemIndexAsync(workspaceId, collectionId, relations, cancellationToken)
            .ConfigureAwait(false);
        var definition = _registry.Find(_typeNormalizer.Normalize(relation.RelationType));
        var diagnostics = BuildDiagnostics(relations, itemIndex)
            .Where(item => string.Equals(item.RelationId, relation.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var inverse = definition?.InverseType is null
            ? null
            : FindInverseRelation(relation, definition.InverseType, relations);
        if (definition is null)
        {
            warnings.Add($"unknown relation type: {relation.RelationType}");
        }

        if (definition?.InverseType is not null && inverse is null)
        {
            warnings.Add($"missing inverse relation {definition.InverseType}.");
        }

        var evidenceRefs = ResolveEvidenceRefs(relation);
        var sourceRefs = ResolveSourceRefs(relation);

        return new RelationExplainResponse
        {
            RelationId = relation.Id,
            Relation = relation,
            TypeDefinition = definition,
            SourceItem = ToReference(itemIndex.GetValueOrDefault(relation.SourceId)),
            TargetItem = ToReference(itemIndex.GetValueOrDefault(relation.TargetId)),
            InverseRelation = inverse,
            Evidence = BuildEvidence(relation, sourceRefs, evidenceRefs),
            EvidenceRefs = evidenceRefs,
            SourceRefs = sourceRefs,
            Confidence = ResolveRelationConfidence(relation),
            ConfidenceReason = ResolveConfidenceReason(relation),
            Lifecycle = ResolveRelationLifecycle(relation),
            ReviewStatus = ResolveReviewStatus(relation),
            Diagnostics = diagnostics,
            Warnings = warnings.ToArray()
        };
    }

    private async Task<IReadOnlyList<ContextRelation>> QueryRelationsAsync(
        string workspaceId,
        string? collectionId,
        string? itemId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (_relationStore is null)
        {
            warnings.Add("relation store is not registered.");
            return Array.Empty<ContextRelation>();
        }

        return await _relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ItemId = itemId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, RelationItemInfo>> BuildItemIndexAsync(
        string workspaceId,
        string? collectionId,
        IReadOnlyList<ContextRelation> relations,
        CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, RelationItemInfo>(StringComparer.OrdinalIgnoreCase);

        if (_contextStore is not null)
        {
            var context = await _contextStore.QueryAsync(new ContextQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in context)
            {
                index[item.Id] = new RelationItemInfo(item.Id, "ContextItem", ContextMemoryStatus.Active, "Active", item.WorkspaceId, item.CollectionId, Summarize(item.Title, item.Content));
            }
        }

        if (_memoryStore is not null)
        {
            var memory = await _memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in memory)
            {
                index[item.Id] = new RelationItemInfo(item.Id, ResolveMemoryKind(item), item.Status, ResolveLifecycle(item.Status, item.Metadata), item.WorkspaceId, item.CollectionId, Summarize(null, item.Content));
            }
        }

        if (_constraintStore is not null)
        {
            var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in constraints)
            {
                var kind = item.Status == ContextMemoryStatus.Candidate ? "CandidateConstraint" : "StableConstraint";
                index[item.Id] = new RelationItemInfo(item.Id, kind, item.Status, ResolveLifecycle(item.Status, item.Metadata), item.WorkspaceId, item.CollectionId, Summarize(null, item.Content));
            }
        }

        if (_globalContextStore is not null)
        {
            var global = await _globalContextStore.QueryAsync(new ContextGlobalQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in global)
            {
                var status = Enum.TryParse<ContextMemoryStatus>(ReadMetadata(item.Metadata, "status"), ignoreCase: true, out var parsed)
                    ? parsed
                    : ContextMemoryStatus.Stable;
                index[item.Id] = new RelationItemInfo(item.Id, "GlobalMemory", status, ResolveLifecycle(status, item.Metadata), item.WorkspaceId, item.CollectionId, Summarize(null, item.Content));
            }
        }

        foreach (var relation in relations)
        {
            if (!string.IsNullOrWhiteSpace(relation.SourceId) && !index.ContainsKey(relation.SourceId))
            {
                index[relation.SourceId] = RelationItemInfo.Unknown(relation.SourceId);
            }

            if (!string.IsNullOrWhiteSpace(relation.TargetId) && !index.ContainsKey(relation.TargetId))
            {
                index[relation.TargetId] = RelationItemInfo.Unknown(relation.TargetId);
            }
        }

        return index;
    }

    private IReadOnlyList<RelationGraphDiagnostic> BuildDiagnostics(
        IReadOnlyList<ContextRelation> relations,
        IReadOnlyDictionary<string, RelationItemInfo> itemIndex)
    {
        var diagnostics = new List<RelationGraphDiagnostic>();

        foreach (var relation in relations)
        {
            var normalizedType = _typeNormalizer.Normalize(relation.RelationType);
            var definition = _registry.Find(normalizedType);
            if (!string.Equals(normalizedType, relation.RelationType, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(BuildDiagnostic(
                    relation,
                    RelationGraphDiagnosticTypes.LegacyRelationType,
                    "Medium",
                    $"Legacy relation type {relation.RelationType} should be normalized to {normalizedType}.",
                    metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["normalizedType"] = normalizedType,
                        ["suggestion"] = $"migrate relationType to {normalizedType}"
                    }));
            }

            if (definition is null)
            {
                diagnostics.Add(BuildDiagnostic(
                    relation,
                    RelationGraphDiagnosticTypes.UnknownRelationType,
                    "High",
                    $"Unknown relation type: {relation.RelationType}",
                    metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["normalizedType"] = normalizedType,
                        ["suggestion"] = string.Equals(normalizedType, relation.RelationType, StringComparison.OrdinalIgnoreCase)
                            ? "add relation type definition or migrate corpus relation type"
                            : $"migrate relationType to {normalizedType}"
                    }));
                continue;
            }

            var source = itemIndex.GetValueOrDefault(relation.SourceId);
            var target = itemIndex.GetValueOrDefault(relation.TargetId);
            if (source is null || source.IsMissing)
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.BrokenSource, "High", $"Relation source does not exist: {relation.SourceId}"));
            }

            if (target is null || target.IsMissing)
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.BrokenTarget, "High", $"Relation target does not exist: {relation.TargetId}"));
            }

            if (definition.RequiresEvidence && ResolveEvidenceRefs(relation).Count == 0)
            {
                if (_backfillPolicy?.CanBackfillDeterministicEvidence(relation) == true)
                {
                    diagnostics.Add(BuildDiagnostic(
                        relation,
                        RelationGraphDiagnosticTypes.EvidenceBackfillRequired,
                        "Medium",
                        "Relation type requires evidence; deterministic/fixture metadata can be backfilled.",
                        metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["normalizedType"] = normalizedType,
                            ["suggestion"] = "backfill evidenceRefs/sourceRefs/sourceOperationId/confidence/lifecycle/reviewStatus"
                        }));
                }
                else
                {
                    diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.MissingEvidence, "Medium", "Relation type requires evidence but no source refs or evidence metadata are present."));
                }
            }

            if (IsConfidenceMissing(relation))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.RelationConfidenceMissing, "Medium", "Relation confidence is missing or zero."));
            }

            var confidence = ResolveRelationConfidence(relation);
            if (confidence > 0 && confidence < 0.5)
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.LowConfidence, "Medium", $"Relation confidence is low: {confidence:0.00}."));
            }

            var lifecycle = ResolveRelationLifecycle(relation);
            var reviewStatus = ResolveReviewStatus(relation);
            var inverseRelation = definition.InverseType is null
                ? null
                : FindInverseRelation(relation, definition.InverseType, relations);
            if (IsHighImpact(definition)
                && !string.Equals(reviewStatus, "Reviewed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(reviewStatus, "ManualReviewed", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.UnreviewedHighImpactRelation, "High", "High-impact relation is not marked as reviewed."));
            }

            if (IsHighImpact(definition)
                && string.Equals(reviewStatus, RelationReviewStatuses.NeedsEvidence, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.NeedsEvidenceHighImpactRelation, "High", "High-impact relation is marked NeedsEvidence."));
            }

            if (string.Equals(reviewStatus, RelationReviewStatuses.Reviewed, StringComparison.OrdinalIgnoreCase)
                && !HasReviewer(relation))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.ReviewedRelationMissingReviewer, "Medium", "Reviewed relation is missing reviewer metadata."));
            }

            if (HasConfidenceChangedWithoutReview(relation, reviewStatus))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.ConfidenceChangedWithoutReview, "High", "Relation confidence appears changed without a Reviewed relation review record."));
            }

            if (RequiresReviewHistory(lifecycle, reviewStatus)
                && !HasReviewHistoryMetadata(relation))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.RelationReviewHistoryMissing, "Medium", "Relation lifecycle/review status has no relation review history reference."));
            }

            if (string.Equals(reviewStatus, "Rejected", StringComparison.OrdinalIgnoreCase)
                && string.Equals(lifecycle, StableMemoryLifecycle.Active, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.RejectedRelationStillActive, "High", "Rejected relation is still marked Active."));
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.RelationLifecycleMismatch, "High", "Relation lifecycle Active conflicts with reviewStatus Rejected."));
            }

            if ((string.Equals(reviewStatus, RelationReviewStatuses.Rejected, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(lifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase))
                && inverseRelation is not null
                && IsActiveRelation(inverseRelation))
            {
                diagnostics.Add(BuildDiagnostic(
                    relation,
                    RelationGraphDiagnosticTypes.RejectedRelationHasActiveInverse,
                    "High",
                    "Rejected relation still has an active inverse relation.",
                    relatedRelations: [inverseRelation.Id]));
            }

            if (string.Equals(lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)
                && IsNormalPathEnabled(relation))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.DeprecatedRelationUsedInNormalPath, "High", "Deprecated relation is marked for normal-path use."));
            }

            if (string.Equals(lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)
                && (IsMetadataTrue(relation.Metadata, "usedByActiveChain")
                    || IsReplacementType(relation.RelationType)
                    && inverseRelation is not null
                    && IsActiveRelation(inverseRelation)))
            {
                diagnostics.Add(BuildDiagnostic(
                    relation,
                    RelationGraphDiagnosticTypes.DeprecatedRelationUsedByActiveChain,
                    "High",
                    "Deprecated relation is still linked from an active replacement chain.",
                    relatedRelations: inverseRelation is null ? Array.Empty<string>() : [inverseRelation.Id]));
            }

            if (string.Equals(lifecycle, ContextMemoryStatus.Candidate.ToString(), StringComparison.OrdinalIgnoreCase)
                && IsNormalPathEnabled(relation))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.CandidateRelationUsedInNormalPath, "High", "Candidate relation is marked for normal-path use."));
            }

            if (HasBrokenEvidenceRefs(relation))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.RelationEvidenceBroken, "Medium", "Relation evidence metadata contains broken or missing refs."));
            }

            if (definition.AuditOnly
                && AllowsNormalExpansion(relation, definition))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.AuditOnlyRelationInNormalPath, "High", "Audit-only relation is marked as normal-expansion eligible."));
            }

            if (source is not null && !source.IsMissing && !KindAllowed(source.Kind, definition.AllowedSourceKinds))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.InvalidSourceKind, "High", $"Invalid source kind {source.Kind} for relation type {definition.Type}."));
            }

            if (target is not null && !target.IsMissing && !KindAllowed(target.Kind, definition.AllowedTargetKinds))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.InvalidTargetKind, "High", $"Invalid target kind {target.Kind} for relation type {definition.Type}."));
            }

            if (!definition.IsDirectional
                && string.Compare(relation.SourceId, relation.TargetId, StringComparison.OrdinalIgnoreCase) > 0)
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.InvalidDirection, "Low", "Undirected relation should be stored in canonical source/target order."));
            }

            if (!string.IsNullOrWhiteSpace(definition.InverseType)
                && !HasInverse(relation, definition.InverseType, relations))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.MissingInverseRelation, "High", $"Missing inverse relation {definition.InverseType}."));
            }

            if (string.Equals(normalizedType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
                && target is not null
                && !target.IsMissing
                && IsInactiveReplacementTarget(target))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.InvalidTargetKind, "High", "replacement target must not be rejected / deprecated / superseded."));
            }
        }

        foreach (var duplicate in relations
            .GroupBy(relation => $"{relation.WorkspaceId}\u001f{relation.CollectionId}\u001f{relation.SourceId}\u001f{_typeNormalizer.Normalize(relation.RelationType)}\u001f{relation.TargetId}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            var ids = duplicate.Select(item => item.Id).ToArray();
            diagnostics.AddRange(duplicate.Select(relation => BuildDiagnostic(
                relation,
                RelationGraphDiagnosticTypes.DuplicateRelation,
                "Medium",
                "Duplicate relation with same source/type/target.",
                relatedRelations: ids.Where(id => !string.Equals(id, relation.Id, StringComparison.OrdinalIgnoreCase)).ToArray())));
        }

        foreach (var relation in relations.Where(relation => IsConflictType(relation.RelationType)))
        {
            if (relations.Any(other =>
                string.Equals(other.SourceId, relation.SourceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.TargetId, relation.TargetId, StringComparison.OrdinalIgnoreCase)
                && IsPositiveType(other.RelationType)))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.ConflictingRelation, "High", "Conflicting relation coexists with positive/supportive relation for the same pair."));
            }
        }

        foreach (var cycle in FindSupersedeCycles(relations))
        {
            foreach (var relation in relations.Where(relation =>
                string.Equals(_typeNormalizer.Normalize(relation.RelationType), ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
                && cycle.Contains(relation.SourceId)
                && cycle.Contains(relation.TargetId)))
            {
                diagnostics.Add(BuildDiagnostic(
                    relation,
                    RelationGraphDiagnosticTypes.SupersedeCycle,
                    "High",
                    "superseded_by replacement graph contains a cycle.",
                    relatedItems: cycle));
            }
        }

        var relatedToCount = relations.Count(relation => string.Equals(relation.RelationType, ContextRelationTypes.RelatedTo, StringComparison.OrdinalIgnoreCase));
        if (relatedToCount >= 10 && relatedToCount > relations.Count / 2)
        {
            foreach (var relation in relations.Where(relation => string.Equals(relation.RelationType, ContextRelationTypes.RelatedTo, StringComparison.OrdinalIgnoreCase)).Take(20))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.WeakRelatedToOveruse, "Low", "related_to dominates relation graph; prefer specific relation types."));
            }
        }

        return diagnostics
            .GroupBy(item => item.DiagnosticId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Severity == "High")
            .ThenBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DiagnosticType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasInverse(
        ContextRelation relation,
        string inverseType,
        IReadOnlyList<ContextRelation> relations)
    {
        return FindInverseRelation(relation, inverseType, relations) is not null;
    }

    private static ContextRelation? FindInverseRelation(
        ContextRelation relation,
        string inverseType,
        IReadOnlyList<ContextRelation> relations)
    {
        return relations.FirstOrDefault(other =>
            string.Equals(other.SourceId, relation.TargetId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(other.TargetId, relation.SourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(StaticTypeNormalizer.Normalize(other.RelationType), inverseType, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AllowsNormalExpansion(ContextRelation relation, RelationTypeDefinition definition)
    {
        if (!definition.AllowsNormalExpansion)
        {
            return true;
        }

        return relation.Metadata.TryGetValue("allowsNormalExpansion", out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool KindAllowed(string kind, IReadOnlyList<string> allowedKinds)
    {
        return allowedKinds.Count == 0
            || allowedKinds.Contains("*")
            || allowedKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsInactiveReplacementTarget(RelationItemInfo item)
    {
        return item.Status is ContextMemoryStatus.Rejected or ContextMemoryStatus.Deprecated
            || string.Equals(item.Lifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Lifecycle, StableMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConfidenceMissing(ContextRelation relation)
    {
        return relation.Confidence <= 0
            && !relation.Metadata.ContainsKey("confidence");
    }

    private static double ResolveRelationConfidence(ContextRelation relation)
    {
        if (relation.Confidence > 0)
        {
            return relation.Confidence;
        }

        return double.TryParse(ReadMetadata(relation.Metadata, "confidence"), out var parsed)
            ? Math.Clamp(parsed, 0, 1)
            : 0;
    }

    private static string ResolveConfidenceReason(ContextRelation relation)
    {
        return ReadMetadata(relation.Metadata, "confidenceReason", "source", "createdFrom") ?? string.Empty;
    }

    private static string ResolveRelationLifecycle(ContextRelation relation)
    {
        // GRAPH-08：正式字段作为唯一运行时来源；Metadata 仅在旧数据迁移时兜底
        if (!string.IsNullOrWhiteSpace(relation.Lifecycle)
            && !string.Equals(relation.Lifecycle, RelationLifecycles.Active, StringComparison.OrdinalIgnoreCase))
        {
            return relation.Lifecycle;
        }
        return ReadMetadata(relation.Metadata, "lifecycle") ?? relation.Lifecycle;
    }

    private static string ResolveReviewStatus(ContextRelation relation)
    {
        // GRAPH-08：正式字段作为唯一运行时来源；Metadata 仅在旧数据迁移时兜底
        if (!string.IsNullOrWhiteSpace(relation.ReviewStatus))
        {
            return relation.ReviewStatus;
        }
        return ReadMetadata(relation.Metadata, "reviewStatus") ?? string.Empty;
    }

    private static bool IsHighImpact(RelationTypeDefinition definition)
    {
        return definition.RequiresEvidence
            || string.Equals(definition.Type, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
            || string.Equals(definition.Type, ContextRelationTypes.Replaces, StringComparison.OrdinalIgnoreCase)
            || string.Equals(definition.Type, ContextRelationTypes.AppliesTo, StringComparison.OrdinalIgnoreCase)
            || string.Equals(definition.Type, "requires", StringComparison.OrdinalIgnoreCase)
            || string.Equals(definition.Type, "blocks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(definition.Type, "conflicts_with", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNormalPathEnabled(ContextRelation relation)
    {
        return IsMetadataTrue(relation.Metadata, "allowsNormalExpansion")
            || IsMetadataTrue(relation.Metadata, "normalPath")
            || IsMetadataTrue(relation.Metadata, "usedInNormalPath");
    }

    private static bool IsActiveRelation(ContextRelation relation)
    {
        var lifecycle = ResolveRelationLifecycle(relation);
        var reviewStatus = ResolveReviewStatus(relation);
        return !string.Equals(reviewStatus, RelationReviewStatuses.Rejected, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(lifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(lifecycle, ContextMemoryStatus.Candidate.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReplacementType(string relationType)
    {
        var normalizedType = StaticTypeNormalizer.Normalize(relationType);
        return string.Equals(normalizedType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, ContextRelationTypes.Replaces, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, "replaced_by", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReviewer(ContextRelation relation)
    {
        return !string.IsNullOrWhiteSpace(ReadMetadata(relation.Metadata, "reviewer", "lastReviewer", "reviewedBy", "createdBy"));
    }

    private static bool HasReviewHistoryMetadata(ContextRelation relation)
    {
        return !string.IsNullOrWhiteSpace(ReadMetadata(relation.Metadata, "reviewId", "lastReviewId", "relationReviewId"));
    }

    private static bool RequiresReviewHistory(string lifecycle, string reviewStatus)
    {
        return string.Equals(reviewStatus, RelationReviewStatuses.Reviewed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reviewStatus, RelationReviewStatuses.Rejected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reviewStatus, RelationReviewStatuses.NeedsEvidence, StringComparison.OrdinalIgnoreCase)
            || string.Equals(lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)
            || string.Equals(lifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasConfidenceChangedWithoutReview(ContextRelation relation, string reviewStatus)
    {
        if (IsMetadataTrue(relation.Metadata, "confidenceChangedWithoutReview"))
        {
            return true;
        }

        if (string.Equals(reviewStatus, RelationReviewStatuses.Reviewed, StringComparison.OrdinalIgnoreCase)
            || !relation.Metadata.TryGetValue("previousConfidence", out var previous)
            || !double.TryParse(previous, out var previousConfidence))
        {
            return false;
        }

        var current = ResolveRelationConfidence(relation);
        return Math.Abs(previousConfidence - current) > 0.0001;
    }

    private static bool IsMetadataTrue(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasBrokenEvidenceRefs(ContextRelation relation)
    {
        if (IsMetadataTrue(relation.Metadata, "evidenceBroken")
            || IsMetadataTrue(relation.Metadata, "brokenEvidence"))
        {
            return true;
        }

        return ResolveEvidenceRefs(relation).Any(item =>
            item.StartsWith("missing:", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("broken:", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ResolveEvidenceRefs(ContextRelation relation)
    {
        var refs = new List<string>();
        refs.AddRange(relation.SourceRefs);
        refs.AddRange(ReadMetadataList(relation.Metadata, "evidenceRefs"));
        refs.AddRange(ReadMetadataList(relation.Metadata, "sourceRefs"));
        var reviewId = ReadMetadata(relation.Metadata, "reviewId");
        if (!string.IsNullOrWhiteSpace(reviewId))
        {
            refs.Add(reviewId);
        }

        return refs
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveSourceRefs(ContextRelation relation)
    {
        var refs = new List<string>();
        refs.AddRange(relation.SourceRefs);
        refs.AddRange(ReadMetadataList(relation.Metadata, "sourceRefs"));
        var reviewId = ReadMetadata(relation.Metadata, "reviewId");
        if (!string.IsNullOrWhiteSpace(reviewId))
        {
            refs.Add(reviewId);
        }

        return refs
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<RelationEvidence> BuildEvidence(
        ContextRelation relation,
        IReadOnlyList<string> sourceRefs,
        IReadOnlyList<string> evidenceRefs)
    {
        if (sourceRefs.Count == 0 && evidenceRefs.Count == 0)
        {
            return Array.Empty<RelationEvidence>();
        }

        return
        [
            new RelationEvidence
            {
                EvidenceId = $"re-{BuildShortHash($"{relation.Id}\u001f{string.Join(',', sourceRefs)}\u001f{string.Join(',', evidenceRefs)}")}",
                RelationId = relation.Id,
                SourceRefs = sourceRefs,
                EvidenceRefs = evidenceRefs,
                SourceOperationId = ReadMetadata(relation.Metadata, "sourceOperationId", "operationId"),
                SourceItemId = ReadMetadata(relation.Metadata, "sourceItemId"),
                EvidenceText = ReadMetadata(relation.Metadata, "evidenceText", "reason") ?? string.Empty,
                EvidenceKind = ReadMetadata(relation.Metadata, "evidenceKind", "createdFrom", "source") ?? string.Empty,
                CreatedAt = relation.CreatedAt,
                Metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase)
            }
        ];
    }

    private static RelationItemReference? ToReference(RelationItemInfo? item)
    {
        if (item is null)
        {
            return null;
        }

        return new RelationItemReference
        {
            ItemId = item.Id,
            Kind = item.Kind,
            Status = item.Status.ToString(),
            Lifecycle = item.Lifecycle,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Summary = item.Summary,
            Missing = item.IsMissing
        };
    }

    private static bool IsConflictType(string relationType)
    {
        var normalizedType = StaticTypeNormalizer.Normalize(relationType);
        return string.Equals(normalizedType, ContextRelationTypes.Contradicts, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, "conflicts_with", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, "blocks", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPositiveType(string relationType)
    {
        var normalizedType = StaticTypeNormalizer.Normalize(relationType);
        return string.Equals(normalizedType, "supports", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, ContextRelationTypes.EvidenceFor, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, ContextRelationTypes.RelatedTo, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, "same_as", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindSupersedeCycles(IReadOnlyList<ContextRelation> relations)
    {
        var edges = relations
            .Where(relation => string.Equals(StaticTypeNormalizer.Normalize(relation.RelationType), ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase))
            .GroupBy(relation => relation.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(relation => relation.TargetId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var cycles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in edges.Keys)
        {
            var path = new List<string>();
            var current = start;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (seen.Add(current))
            {
                path.Add(current);
                if (!edges.TryGetValue(current, out var nextItems) || nextItems.Length == 0)
                {
                    break;
                }

                var next = nextItems[0];
                var index = path.FindIndex(id => string.Equals(id, next, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var cycle = path.Skip(index).ToArray();
                    var key = string.Join("|", cycle.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
                    cycles.TryAdd(key, cycle);
                    break;
                }

                current = next;
            }
        }

        return cycles.Values.ToArray();
    }

    private static string ResolveMemoryKind(ContextMemoryItem item)
    {
        if (item.Layer == ContextMemoryLayer.Stable)
        {
            return IsDecision(item.Type, item.Metadata) ? "DecisionRecord" : "StableMemory";
        }

        if (item.Status == ContextMemoryStatus.Candidate)
        {
            return "CandidateMemory";
        }

        return item.Layer.ToString();
    }

    private static bool IsDecision(string type, IReadOnlyDictionary<string, string> metadata)
    {
        return string.Equals(type, "decision", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ReadMetadata(metadata, "suggestedTargetLayer", "targetLayer"), "DecisionRecord", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ReadMetadata(metadata, "stableTargetKind"), "DecisionRecord", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLifecycle(ContextMemoryStatus status, IReadOnlyDictionary<string, string> metadata)
    {
        var metadataLifecycle = ReadMetadata(metadata, "lifecycle", "processState");
        if (!string.IsNullOrWhiteSpace(metadataLifecycle))
        {
            return metadataLifecycle;
        }

        return status switch
        {
            ContextMemoryStatus.Active => StableMemoryLifecycle.Active,
            ContextMemoryStatus.Deprecated => StableMemoryLifecycle.Deprecated,
            ContextMemoryStatus.Rejected => StableMemoryLifecycle.Rejected,
            _ => StableMemoryLifecycle.Current
        };
    }

    private static string? ReadMetadata(IReadOnlyDictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadMetadataList(IReadOnlyDictionary<string, string> metadata, params string[] keys)
    {
        var values = new List<string>();
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                values.AddRange(value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        return values;
    }

    private static string Summarize(string? title, string content)
    {
        var value = !string.IsNullOrWhiteSpace(title) ? title : content;
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 160 ? value : value[..160];
    }

    private static RelationGraphDiagnostic BuildDiagnostic(
        ContextRelation relation,
        string type,
        string severity,
        string reason,
        IReadOnlyList<string>? relatedRelations = null,
        IReadOnlyList<string>? relatedItems = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var mergedMetadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase);
        if (metadata is not null)
        {
            foreach (var pair in metadata)
            {
                mergedMetadata[pair.Key] = pair.Value;
            }
        }

        return new RelationGraphDiagnostic
        {
            DiagnosticId = $"rgd-{BuildShortHash($"{relation.Id}\u001f{type}\u001f{reason}\u001f{string.Join(',', relatedRelations ?? Array.Empty<string>())}\u001f{string.Join(',', relatedItems ?? Array.Empty<string>())}")}",
            DiagnosticType = type,
            Severity = severity,
            Reason = reason,
            RelationId = relation.Id,
            RelationType = relation.RelationType,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            RelatedRelationIds = relatedRelations ?? Array.Empty<string>(),
            RelatedItemIds = relatedItems ?? Array.Empty<string>(),
            Metadata = mergedMetadata
        };
    }

    private static string BuildShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private sealed record RelationItemInfo(
        string Id,
        string Kind,
        ContextMemoryStatus Status,
        string Lifecycle,
        string WorkspaceId,
        string? CollectionId,
        string Summary)
    {
        public bool IsMissing { get; init; }

        public static RelationItemInfo Unknown(string id)
        {
            return new RelationItemInfo(id, "Unknown", ContextMemoryStatus.Rejected, StableMemoryLifecycle.Rejected, string.Empty, null, string.Empty)
            {
                IsMissing = true
            };
        }
    }
}
