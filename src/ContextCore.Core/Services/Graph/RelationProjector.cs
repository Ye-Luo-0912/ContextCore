using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>
/// 统一的关系生产投影器，在 Ingest/Compression/Promotion/Lifecycle Review 四个流程中生成图边。
/// 实现负责统一填充 GRAPH-01 契约字段（SourceNodeKind/TargetNodeKind/Lifecycle/ReviewStatus/UpdatedAt/Provenance）。
/// 生成的关系列表由调用者通过 <see cref="IRelationStore.BatchUpsertAsync"/> 落库。
/// </summary>
public sealed class RelationProjector : IRelationProjector
{
    private const string ProvenanceIngest = "ingest";
    private const string ProvenanceCompression = "compression";
    private const string ProvenancePromotion = "promotion";
    private const string ProvenanceLifecycleReview = "lifecycle-review";

    /// <inheritdoc />
    public IReadOnlyList<ContextRelation> ProjectForIngest(ContextItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.WorkspaceId)
            || string.IsNullOrWhiteSpace(item.CollectionId)
            || string.IsNullOrWhiteSpace(item.Id))
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var relations = item.Refs
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !string.Equals(value, item.Id, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(targetId => new ContextRelation
            {
                Id = CreateStableId(
                    item.WorkspaceId,
                    item.CollectionId,
                    ContextRelationTypes.RelatedTo,
                    item.Id,
                    targetId),
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                SourceId = item.Id,
                TargetId = targetId,
                RelationType = ContextRelationTypes.RelatedTo,
                Weight = Math.Max(0.1, item.Importance),
                Confidence = 0.8,
                SourceRefs = ResolveContextItemRelationSourceRefs(item, targetId),
                Metadata = new Dictionary<string, string>
                {
                    ["sourceItemType"] = item.Type
                },
                CreatedAt = now,
                SourceNodeKind = nameof(GraphNodeKind.ContextItem),
                TargetNodeKind = nameof(GraphNodeKind.ContextItem),
                Lifecycle = RelationLifecycles.Active,
                ReviewStatus = string.Empty,
                UpdatedAt = now,
                Provenance = ProvenanceIngest
            });

        return [.. relations];
    }

    /// <inheritdoc />
    public IReadOnlyList<ContextRelation> ProjectForCompression(CompressionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var relations = new List<ContextRelation>();
        var now = DateTimeOffset.UtcNow;

        foreach (var generatedItem in response.GeneratedItems)
        {
            if (string.IsNullOrWhiteSpace(generatedItem.WorkspaceId)
                || string.IsNullOrWhiteSpace(generatedItem.CollectionId)
                || string.IsNullOrWhiteSpace(generatedItem.Id))
            {
                continue;
            }

            foreach (var sourceId in ResolveDerivedFrom(generatedItem))
            {
                relations.Add(CreateCompressionRelation(
                    generatedItem,
                    sourceId,
                    ContextRelationTypes.DerivedFrom,
                    response,
                    now));

                if (string.Equals(generatedItem.Type, "summary", StringComparison.OrdinalIgnoreCase))
                {
                    relations.Add(CreateCompressionRelation(
                        generatedItem,
                        sourceId,
                        ContextRelationTypes.Summarizes,
                        response,
                        now));
                }
            }

            if (!string.IsNullOrWhiteSpace(response.OperationId))
            {
                relations.Add(CreateGeneratedByRelation(generatedItem, response, now));
            }
        }

        return [.. relations
            .GroupBy(relation => RelationKey(relation), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    /// <inheritdoc />
    public IReadOnlyList<ContextRelation> ProjectForPromotion(
        ShortTermPromotionCandidate candidate,
        string targetItemId,
        string targetKind,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (string.IsNullOrWhiteSpace(candidate.WorkspaceId)
            || string.IsNullOrWhiteSpace(candidate.CollectionId)
            || string.IsNullOrWhiteSpace(targetItemId))
        {
            return [];
        }

        var targetNodeKind = ResolvePromotionTargetNodeKind(targetKind);
        var candidateNodeKind = nameof(GraphNodeKind.CandidateMemory);

        var relations = new List<ContextRelation>
        {
            BuildPromotionRelation(
                candidate,
                targetItemId,
                candidate.CandidateId,
                ContextRelationTypes.PromotedFrom,
                targetNodeKind,
                candidateNodeKind,
                now),
            BuildPromotionRelation(
                candidate,
                targetItemId,
                candidate.SourceWorkingItemId,
                ContextRelationTypes.DerivedFrom,
                targetNodeKind,
                nameof(GraphNodeKind.ContextItem),
                now)
        };

        relations.AddRange(candidate.EvidenceRefs
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(evidenceRef => BuildPromotionRelation(
                candidate,
                evidenceRef,
                targetItemId,
                ContextRelationTypes.EvidenceFor,
                nameof(GraphNodeKind.Unknown),
                targetNodeKind,
                now)));

        return [.. relations];
    }

    /// <inheritdoc />
    public IReadOnlyList<ContextRelation> ProjectForSupersede(SupersedeProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.WorkspaceId)
            || string.IsNullOrWhiteSpace(request.CollectionId)
            || string.IsNullOrWhiteSpace(request.SourceId)
            || string.IsNullOrWhiteSpace(request.ReplacementId))
        {
            return [];
        }

        var sourceNodeKind = ResolveStableNodeKind(request.SourceStableKind);
        var replacementNodeKind = ResolveStableNodeKind(request.ReplacementStableKind);

        var evidenceRefs = request.EvidenceRefs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sourceRefs = request.SourceRefs
            .Append(request.ReviewId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var policyVersion = request.RequestMetadata.TryGetValue("policyVersion", out var configuredPolicyVersion)
            && !string.IsNullOrWhiteSpace(configuredPolicyVersion)
            ? configuredPolicyVersion
            : "stable-lifecycle-review-v1";
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "stable_lifecycle_review",
            ["reviewId"] = request.ReviewId,
            ["reviewer"] = request.Reviewer,
            ["reason"] = request.Reason,
            ["createdAt"] = request.Now.ToString("O"),
            ["sourceOperationId"] = request.OperationId,
            ["sourceItemId"] = request.SourceId,
            ["createdBy"] = request.Reviewer,
            ["createdFrom"] = "stable_lifecycle_review",
            ["confidence"] = "1.0",
            ["confidenceReason"] = "stable_lifecycle_review",
            ["lifecycle"] = StableMemoryLifecycle.Active,
            ["reviewStatus"] = RelationReviewStatuses.Reviewed,
            ["policyVersion"] = policyVersion,
            ["sourceRefs"] = string.Join(',', sourceRefs),
            ["evidenceRefs"] = string.Join(',', evidenceRefs)
        };

        var supersededBy = new ContextRelation
        {
            Id = CreateStableId(
                request.WorkspaceId,
                request.CollectionId,
                ContextRelationTypes.SupersededBy,
                request.SourceId,
                request.ReplacementId),
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SourceId = request.SourceId,
            TargetId = request.ReplacementId,
            RelationType = ContextRelationTypes.SupersededBy,
            Weight = 1.0,
            Confidence = 1.0,
            SourceRefs = sourceRefs,
            Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
            CreatedAt = request.Now,
            SourceNodeKind = sourceNodeKind,
            TargetNodeKind = replacementNodeKind,
            Lifecycle = RelationLifecycles.Active,
            ReviewStatus = RelationReviewStatuses.Reviewed,
            UpdatedAt = request.Now,
            Provenance = ProvenanceLifecycleReview
        };
        var replaces = new ContextRelation
        {
            Id = CreateStableId(
                request.WorkspaceId,
                request.CollectionId,
                ContextRelationTypes.Replaces,
                request.ReplacementId,
                request.SourceId),
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SourceId = request.ReplacementId,
            TargetId = request.SourceId,
            RelationType = ContextRelationTypes.Replaces,
            Weight = 1.0,
            Confidence = 1.0,
            SourceRefs = sourceRefs,
            Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
            CreatedAt = request.Now,
            SourceNodeKind = replacementNodeKind,
            TargetNodeKind = sourceNodeKind,
            Lifecycle = RelationLifecycles.Active,
            ReviewStatus = RelationReviewStatuses.Reviewed,
            UpdatedAt = request.Now,
            Provenance = ProvenanceLifecycleReview
        };

        return [supersededBy, replaces];
    }

    private static ContextRelation CreateCompressionRelation(
        ContextItem generatedItem,
        string sourceId,
        string relationType,
        CompressionResponse response,
        DateTimeOffset now)
    {
        var metadata = new Dictionary<string, string>(generatedItem.Metadata)
        {
            ["operationId"] = response.OperationId,
            ["generatedItemType"] = generatedItem.Type
        };

        var targetNodeKind = relationType == ContextRelationTypes.GeneratedBy
            ? nameof(GraphNodeKind.Operation)
            : nameof(GraphNodeKind.ContextItem);

        return new ContextRelation
        {
            Id = CreateStableId(
                generatedItem.WorkspaceId,
                generatedItem.CollectionId,
                relationType,
                generatedItem.Id,
                sourceId),
            WorkspaceId = generatedItem.WorkspaceId,
            CollectionId = generatedItem.CollectionId,
            SourceId = generatedItem.Id,
            TargetId = sourceId,
            RelationType = relationType,
            Weight = relationType == ContextRelationTypes.Summarizes ? 0.95 : 1.0,
            Confidence = 1.0,
            SourceRefs = ResolveCompressionRelationSourceRefs(generatedItem, sourceId),
            Metadata = metadata,
            CreatedAt = now,
            SourceNodeKind = nameof(GraphNodeKind.ContextItem),
            TargetNodeKind = targetNodeKind,
            Lifecycle = RelationLifecycles.Active,
            ReviewStatus = string.Empty,
            UpdatedAt = now,
            Provenance = ProvenanceCompression
        };
    }

    private static ContextRelation CreateGeneratedByRelation(
        ContextItem generatedItem,
        CompressionResponse response,
        DateTimeOffset now)
    {
        var metadata = new Dictionary<string, string>(generatedItem.Metadata)
        {
            ["operationId"] = response.OperationId,
            ["targetKind"] = "operation",
            ["generatedItemType"] = generatedItem.Type
        };

        return new ContextRelation
        {
            Id = CreateStableId(
                generatedItem.WorkspaceId,
                generatedItem.CollectionId,
                ContextRelationTypes.GeneratedBy,
                generatedItem.Id,
                response.OperationId),
            WorkspaceId = generatedItem.WorkspaceId,
            CollectionId = generatedItem.CollectionId,
            SourceId = generatedItem.Id,
            TargetId = response.OperationId,
            RelationType = ContextRelationTypes.GeneratedBy,
            Weight = 1.0,
            Confidence = 1.0,
            SourceRefs = ResolveGeneratedBySourceRefs(generatedItem, response.OperationId),
            Metadata = metadata,
            CreatedAt = now,
            SourceNodeKind = nameof(GraphNodeKind.ContextItem),
            TargetNodeKind = nameof(GraphNodeKind.Operation),
            Lifecycle = RelationLifecycles.Active,
            ReviewStatus = string.Empty,
            UpdatedAt = now,
            Provenance = ProvenanceCompression
        };
    }

    private static ContextRelation BuildPromotionRelation(
        ShortTermPromotionCandidate candidate,
        string sourceId,
        string targetId,
        string relationType,
        string sourceNodeKind,
        string targetNodeKind,
        DateTimeOffset now)
    {
        return new ContextRelation
        {
            Id = CreateStableId(
                candidate.WorkspaceId,
                candidate.CollectionId,
                relationType,
                sourceId,
                targetId),
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 1.0,
            Confidence = candidate.Confidence,
            SourceRefs = candidate.EvidenceRefs.ToArray(),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceCandidateId"] = candidate.CandidateId,
                ["sourceWorkingItemId"] = candidate.SourceWorkingItemId,
                ["targetKind"] = targetNodeKind,
                ["promotionFlow"] = "short-term-promotion-review/v1"
            },
            CreatedAt = now,
            SourceNodeKind = sourceNodeKind,
            TargetNodeKind = targetNodeKind,
            Lifecycle = RelationLifecycles.Active,
            ReviewStatus = RelationReviewStatuses.Reviewed,
            UpdatedAt = now,
            Provenance = ProvenancePromotion
        };
    }

    private static string ResolvePromotionTargetNodeKind(string targetKind)
    {
        return string.Equals(targetKind, "constraint", StringComparison.OrdinalIgnoreCase)
            ? nameof(GraphNodeKind.CandidateConstraint)
            : nameof(GraphNodeKind.CandidateMemory);
    }

    private static string ResolveStableNodeKind(string stableKind)
    {
        return stableKind switch
        {
            StableMemoryKinds.StableMemory => nameof(GraphNodeKind.StableMemory),
            StableMemoryKinds.StableConstraint => nameof(GraphNodeKind.StableConstraint),
            StableMemoryKinds.DecisionRecord => nameof(GraphNodeKind.DecisionRecord),
            StableMemoryKinds.GlobalMemory => nameof(GraphNodeKind.GlobalMemory),
            _ => nameof(GraphNodeKind.Unknown)
        };
    }

    private static IReadOnlyList<string> ResolveDerivedFrom(ContextItem generatedItem)
    {
        if (generatedItem.Metadata.TryGetValue("derivedFrom", out var derivedFrom)
            && !string.IsNullOrWhiteSpace(derivedFrom))
        {
            return [.. derivedFrom
                .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        return [.. generatedItem.SourceRefs
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> ResolveCompressionRelationSourceRefs(ContextItem generatedItem, string sourceId)
    {
        return [.. generatedItem.SourceRefs
            .Append(generatedItem.Id)
            .Append(sourceId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> ResolveGeneratedBySourceRefs(
        ContextItem generatedItem,
        string operationId)
    {
        return [.. generatedItem.SourceRefs
            .Append(generatedItem.Id)
            .Append(operationId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<string> ResolveContextItemRelationSourceRefs(
        ContextItem item,
        string targetId)
    {
        return [.. item.SourceRefs
            .Append(item.Id)
            .Append(targetId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string RelationKey(ContextRelation relation)
    {
        return string.Join(
            '\u001f',
            relation.WorkspaceId,
            relation.CollectionId,
            relation.RelationType,
            relation.SourceId,
            relation.TargetId);
    }

    private static string CreateStableId(
        string workspaceId,
        string collectionId,
        string relationType,
        string sourceId,
        string targetId)
    {
        var key = string.Join('\u001f', workspaceId, collectionId, relationType, sourceId, targetId);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"rel-{Convert.ToHexString(bytes)[..24].ToLowerInvariant()}";
    }
}
