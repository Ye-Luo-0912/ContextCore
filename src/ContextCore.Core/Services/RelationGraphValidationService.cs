using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>只读关系图谱校验服务，不影响 retrieval、relation expansion 或 PackingPolicy。</summary>
public sealed class RelationGraphValidationService
{
    private readonly IRelationStore? _relationStore;
    private readonly IContextStore? _contextStore;
    private readonly IMemoryStore? _memoryStore;
    private readonly IConstraintStore? _constraintStore;
    private readonly IGlobalContextStore? _globalContextStore;
    private readonly RelationTypeRegistry _registry;

    public RelationGraphValidationService(
        IRelationStore? relationStore,
        IContextStore? contextStore,
        IMemoryStore? memoryStore,
        IConstraintStore? constraintStore,
        IGlobalContextStore? globalContextStore,
        RelationTypeRegistry registry)
    {
        _relationStore = relationStore;
        _contextStore = contextStore;
        _memoryStore = memoryStore;
        _constraintStore = constraintStore;
        _globalContextStore = globalContextStore;
        _registry = registry;
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
        var definition = _registry.Find(relation.RelationType);
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
            var definition = _registry.Find(relation.RelationType);
            if (definition is null)
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.UnknownRelationType, "High", $"Unknown relation type: {relation.RelationType}"));
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
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.MissingEvidence, "Medium", "Relation type requires evidence but no source refs or evidence metadata are present."));
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

            if (string.Equals(relation.RelationType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
                && target is not null
                && !target.IsMissing
                && IsInactiveReplacementTarget(target))
            {
                diagnostics.Add(BuildDiagnostic(relation, RelationGraphDiagnosticTypes.InvalidTargetKind, "High", "replacement target must not be rejected / deprecated / superseded."));
            }
        }

        foreach (var duplicate in relations
            .GroupBy(relation => $"{relation.WorkspaceId}\u001f{relation.CollectionId}\u001f{relation.SourceId}\u001f{relation.RelationType}\u001f{relation.TargetId}", StringComparer.OrdinalIgnoreCase)
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
                string.Equals(relation.RelationType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
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
            && string.Equals(other.RelationType, inverseType, StringComparison.OrdinalIgnoreCase));
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
        return ReadMetadata(relation.Metadata, "lifecycle") ?? StableMemoryLifecycle.Active;
    }

    private static string ResolveReviewStatus(ContextRelation relation)
    {
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
        return string.Equals(relationType, ContextRelationTypes.Contradicts, StringComparison.OrdinalIgnoreCase)
            || string.Equals(relationType, "conflicts_with", StringComparison.OrdinalIgnoreCase)
            || string.Equals(relationType, "blocks", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPositiveType(string relationType)
    {
        return string.Equals(relationType, "supports", StringComparison.OrdinalIgnoreCase)
            || string.Equals(relationType, ContextRelationTypes.EvidenceFor, StringComparison.OrdinalIgnoreCase)
            || string.Equals(relationType, ContextRelationTypes.RelatedTo, StringComparison.OrdinalIgnoreCase)
            || string.Equals(relationType, "same_as", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindSupersedeCycles(IReadOnlyList<ContextRelation> relations)
    {
        var edges = relations
            .Where(relation => string.Equals(relation.RelationType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase))
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
        IReadOnlyList<string>? relatedItems = null)
    {
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
            Metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase)
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
