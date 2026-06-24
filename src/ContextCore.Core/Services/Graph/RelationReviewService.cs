using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>Relation 人工 review / lifecycle 操作服务；不改变 retrieval、relation expansion 或 package 输出。</summary>
public sealed class RelationReviewService
{
    private readonly IRelationStore? _relationStore;
    private readonly IRelationReviewStore? _reviewStore;
    private readonly RelationTypeRegistry _registry;
    private readonly RelationGraphValidationService _validationService;

    public RelationReviewService(
        IRelationStore? relationStore,
        IRelationReviewStore? reviewStore,
        RelationTypeRegistry registry,
        RelationGraphValidationService validationService)
    {
        _relationStore = relationStore;
        _reviewStore = reviewStore;
        _registry = registry;
        _validationService = validationService;
    }

    public Task<IReadOnlyList<RelationReviewRecord>> GetReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        EnsureReviewStore();
        return _reviewStore!.QueryReviewsAsync(relationId, cancellationToken);
    }

    public Task<RelationReviewResult?> ReviewAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ApplyAsync(relationId, RelationReviewActions.Review, request, cancellationToken);
    }

    public Task<RelationReviewResult?> RejectAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ApplyAsync(relationId, RelationReviewActions.Reject, request, cancellationToken);
    }

    public Task<RelationReviewResult?> DeprecateAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ApplyAsync(relationId, RelationReviewActions.Deprecate, request, cancellationToken);
    }

    public Task<RelationReviewResult?> MarkNeedsEvidenceAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ApplyAsync(relationId, RelationReviewActions.MarkNeedsEvidence, request, cancellationToken);
    }

    private async Task<RelationReviewResult?> ApplyAsync(
        string relationId,
        string action,
        RelationReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        EnsureRelationStore();
        EnsureReviewStore();

        var relation = await FindRelationAsync(relationId, request.WorkspaceId, request.CollectionId, cancellationToken)
            .ConfigureAwait(false);
        if (relation is null)
        {
            return null;
        }

        var definition = _registry.Find(relation.RelationType)
            ?? throw new InvalidOperationException($"Unknown relation type: {relation.RelationType}");
        ValidateTransition(relation, definition, action, request);

        var explain = await _validationService.ExplainAsync(relation.Id, relation.WorkspaceId, relation.CollectionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Relation explain failed for {relation.Id}.");
        ValidateEndpoints(explain);

        var now = DateTimeOffset.UtcNow;
        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId.Trim();
        var reviewer = string.IsNullOrWhiteSpace(request.Reviewer) ? "manual" : request.Reviewer.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? action : request.Reason.Trim();
        var reviewId = $"rrv-{BuildShortHash($"{relation.Id}\u001f{action}\u001f{now:O}")}";
        var fromLifecycle = ResolveLifecycle(relation);
        var fromReviewStatus = ResolveReviewStatus(relation);
        var toLifecycle = ResolveTargetLifecycle(action, fromLifecycle);
        var toReviewStatus = ResolveTargetReviewStatus(action, fromReviewStatus);
        var warnings = BuildWarnings(explain).ToArray();
        var updated = BuildUpdatedRelation(
            relation,
            action,
            toLifecycle,
            toReviewStatus,
            reviewer,
            reason,
            reviewId,
            operationId,
            request.Metadata,
            now);

        await _relationStore!.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

        var evidenceRefs = ResolveEvidenceRefs(updated);
        var sourceRefs = ResolveSourceRefs(updated);
        var record = new RelationReviewRecord
        {
            ReviewId = reviewId,
            RelationId = relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            Action = action,
            FromLifecycle = fromLifecycle,
            ToLifecycle = toLifecycle,
            FromReviewStatus = fromReviewStatus,
            ToReviewStatus = toReviewStatus,
            Reviewer = reviewer,
            Reason = reason,
            RelationType = relation.RelationType,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            EvidenceRefs = evidenceRefs,
            SourceRefs = sourceRefs,
            CreatedAt = now,
            ReviewedAt = now,
            Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["operationId"] = operationId,
                ["reviewedAt"] = now.ToString("O")
            },
            Warnings = warnings,
            Errors = Array.Empty<string>()
        };
        await _reviewStore!.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);

        return new RelationReviewResult
        {
            OperationId = operationId,
            RelationId = relation.Id,
            Action = action,
            FromLifecycle = fromLifecycle,
            ToLifecycle = toLifecycle,
            FromReviewStatus = fromReviewStatus,
            ToReviewStatus = toReviewStatus,
            Reviewer = reviewer,
            Reason = reason,
            ReviewedAt = now,
            Relation = updated,
            Review = record,
            Warnings = warnings,
            Errors = Array.Empty<string>()
        };
    }

    private async Task<ContextRelation?> FindRelationAsync(
        string relationId,
        string workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        var relations = await _relationStore!.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        return relations.FirstOrDefault(item => string.Equals(item.Id, relationId, StringComparison.OrdinalIgnoreCase));
    }

    private static ContextRelation BuildUpdatedRelation(
        ContextRelation relation,
        string action,
        string toLifecycle,
        string toReviewStatus,
        string reviewer,
        string reason,
        string reviewId,
        string operationId,
        IReadOnlyDictionary<string, string> requestMetadata,
        DateTimeOffset reviewedAt)
    {
        var metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in requestMetadata)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        metadata["lifecycle"] = toLifecycle;
        metadata["reviewStatus"] = toReviewStatus;
        metadata["lastReviewId"] = reviewId;
        metadata["reviewId"] = reviewId;
        metadata["lastReviewAction"] = action;
        metadata["lastReviewedAt"] = reviewedAt.ToString("O");
        metadata["reviewedAt"] = reviewedAt.ToString("O");
        metadata["lastReviewer"] = reviewer;
        metadata["reviewer"] = reviewer;
        metadata["reviewReason"] = reason;
        metadata["operationId"] = operationId;
        metadata["updatedFrom"] = "relation_review";

        return new ContextRelation
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
            Metadata = metadata,
            CreatedAt = relation.CreatedAt == default ? reviewedAt : relation.CreatedAt
        };
    }

    private static void ValidateTransition(
        ContextRelation relation,
        RelationTypeDefinition definition,
        string action,
        RelationReviewRequest request)
    {
        if (IsHighImpact(definition) && string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new InvalidOperationException("High-impact relation operation requires a reason.");
        }

        var lifecycle = ResolveLifecycle(relation);
        var reviewStatus = ResolveReviewStatus(relation);
        if (string.Equals(lifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reviewStatus, RelationReviewStatuses.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Rejected relation cannot be modified until RestoreActive is implemented.");
        }

        if (string.Equals(action, RelationReviewActions.Reject, StringComparison.OrdinalIgnoreCase)
            && string.Equals(reviewStatus, RelationReviewStatuses.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Relation is already rejected.");
        }

        if (string.Equals(action, RelationReviewActions.Deprecate, StringComparison.OrdinalIgnoreCase)
            && string.Equals(lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Relation is already deprecated.");
        }
    }

    private static void ValidateEndpoints(RelationExplainResponse explain)
    {
        if (explain.TypeDefinition is null)
        {
            throw new InvalidOperationException($"Unknown relation type: {explain.Relation?.RelationType ?? explain.RelationId}");
        }

        if (explain.SourceItem is null || explain.SourceItem.Missing)
        {
            throw new InvalidOperationException($"Relation source does not exist: {explain.Relation?.SourceId ?? "-"}");
        }

        if (explain.TargetItem is null || explain.TargetItem.Missing)
        {
            throw new InvalidOperationException($"Relation target does not exist: {explain.Relation?.TargetId ?? "-"}");
        }
    }

    private static IReadOnlyList<string> BuildWarnings(RelationExplainResponse explain)
    {
        var warnings = new List<string>();
        warnings.AddRange(explain.Warnings);
        warnings.AddRange(explain.Diagnostics
            .Where(item => string.Equals(item.DiagnosticType, RelationGraphDiagnosticTypes.MissingInverseRelation, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Reason));
        return warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ResolveTargetLifecycle(string action, string currentLifecycle)
    {
        return action switch
        {
            RelationReviewActions.Reject => StableMemoryLifecycle.Rejected,
            RelationReviewActions.Deprecate => StableMemoryLifecycle.Deprecated,
            _ => string.IsNullOrWhiteSpace(currentLifecycle) ? StableMemoryLifecycle.Active : currentLifecycle
        };
    }

    private static string ResolveTargetReviewStatus(string action, string currentReviewStatus)
    {
        return action switch
        {
            RelationReviewActions.Review => RelationReviewStatuses.Reviewed,
            RelationReviewActions.Reject => RelationReviewStatuses.Rejected,
            RelationReviewActions.MarkNeedsEvidence => RelationReviewStatuses.NeedsEvidence,
            _ => currentReviewStatus
        };
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

    private static string ResolveLifecycle(ContextRelation relation)
    {
        return ReadMetadata(relation.Metadata, "lifecycle") ?? StableMemoryLifecycle.Active;
    }

    private static string ResolveReviewStatus(ContextRelation relation)
    {
        return ReadMetadata(relation.Metadata, "reviewStatus") ?? string.Empty;
    }

    private static IReadOnlyList<string> ResolveEvidenceRefs(ContextRelation relation)
    {
        var refs = new List<string>();
        refs.AddRange(relation.SourceRefs);
        refs.AddRange(ReadMetadataList(relation.Metadata, "evidenceRefs"));
        refs.AddRange(ReadMetadataList(relation.Metadata, "sourceRefs"));
        var reviewId = ReadMetadata(relation.Metadata, "reviewId", "lastReviewId");
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
        return refs
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private void EnsureRelationStore()
    {
        if (_relationStore is null)
        {
            throw new InvalidOperationException("IRelationStore is not registered.");
        }
    }

    private void EnsureReviewStore()
    {
        if (_reviewStore is null)
        {
            throw new InvalidOperationException("IRelationReviewStore is not registered.");
        }
    }

    private static string BuildShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
