using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>构建 relation expansion preview，不改变 retrieval、packing 或 package 输出。</summary>
public sealed class RelationExpansionPreviewService
{
    private readonly IRelationStore? _relationStore;
    private readonly RelationExpansionProfileRegistry _profileRegistry;
    private readonly RelationExpansionPolicyValidator _validator;

    public RelationExpansionPreviewService(
        IRelationStore? relationStore,
        RelationExpansionProfileRegistry profileRegistry,
        RelationExpansionPolicyValidator validator)
    {
        _relationStore = relationStore;
        _profileRegistry = profileRegistry;
        _validator = validator;
    }

    public async Task<RelationExpansionPreviewResponse> PreviewAsync(
        RelationExpansionPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ItemId);

        var profileId = string.IsNullOrWhiteSpace(request.ProfileId)
            ? "normal-v1"
            : request.ProfileId;
        var profile = _profileRegistry.Find(profileId)
            ?? throw new InvalidOperationException($"Unknown relation expansion profile: {profileId}");
        var warnings = new List<string>();
        var accepted = new List<RelationExpansionPreviewRelation>();
        var blocked = new List<RelationExpansionPreviewRelation>();

        if (_relationStore is null)
        {
            warnings.Add("relation store is not registered.");
            return BuildResponse(request, profile, accepted, blocked, warnings);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { request.ItemId };
        var frontier = new Queue<FrontierNode>();
        frontier.Enqueue(new FrontierNode(request.ItemId, 0, request.ItemId));

        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = frontier.Dequeue();
            var nextDepth = node.Depth + 1;
            if (nextDepth > profile.MaxDepth)
            {
                continue;
            }

            var outgoing = await _relationStore
                .QueryBySourceAsync(request.WorkspaceId, request.CollectionId!, node.ItemId, cancellationToken)
                .ConfigureAwait(false);
            var ordered = outgoing
                .OrderByDescending(relation => _validator.ResolveWeight(relation, profile))
                .ThenByDescending(RelationExpansionPolicyValidator.ResolveConfidence)
                .ThenByDescending(relation => relation.CreatedAt)
                .ThenBy(relation => relation.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var fanoutIndex = 0;
            foreach (var relation in ordered)
            {
                fanoutIndex++;
                var validation = _validator.Validate(relation, profile, nextDepth, fanoutIndex);
                var previewRelation = BuildPreviewRelation(relation, profile, validation, nextDepth, node.Path);
                if (validation.Accepted)
                {
                    accepted.Add(previewRelation);
                    if (!visited.Contains(relation.TargetId) && nextDepth < profile.MaxDepth)
                    {
                        visited.Add(relation.TargetId);
                        frontier.Enqueue(new FrontierNode(
                            relation.TargetId,
                            nextDepth,
                            $"{node.Path} --{relation.RelationType}--> {relation.TargetId}"));
                    }
                }
                else
                {
                    blocked.Add(previewRelation);
                }
            }
        }

        return BuildResponse(request, profile, accepted, blocked, warnings);
    }

    private RelationExpansionPreviewRelation BuildPreviewRelation(
        ContextRelation relation,
        RelationExpansionProfile profile,
        RelationExpansionPolicyValidationResult validation,
        int depth,
        string sourcePath)
    {
        return new RelationExpansionPreviewRelation
        {
            RelationId = relation.Id,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            RelationType = _validator.ResolveNormalizedRelationType(relation),
            TraversalDirection = validation.TraversalDirection,
            Depth = depth,
            Confidence = RelationExpansionPolicyValidator.ResolveConfidence(relation),
            Weight = _validator.ResolveWeight(relation, profile),
            Lifecycle = RelationExpansionPolicyValidator.ResolveLifecycle(relation),
            ReviewStatus = RelationExpansionPolicyValidator.ResolveReviewStatus(relation),
            TargetLifecycle = validation.TargetLifecycle,
            TargetSection = validation.TargetSection,
            SectionReason = validation.SectionReason,
            RiskIfNormalSelected = validation.RiskIfNormalSelected,
            RiskAfterSectionRouting = validation.RiskAfterSectionRouting,
            Path = $"{sourcePath} --{_validator.ResolveNormalizedRelationType(relation)}--> {relation.TargetId}",
            Reasons = validation.Reasons,
            Warnings = validation.Warnings,
            Metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static RelationExpansionPreviewResponse BuildResponse(
        RelationExpansionPreviewRequest request,
        RelationExpansionProfile profile,
        IReadOnlyList<RelationExpansionPreviewRelation> accepted,
        IReadOnlyList<RelationExpansionPreviewRelation> blocked,
        IReadOnlyList<string> warnings)
    {
        return new RelationExpansionPreviewResponse
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? $"rel-exp-preview-{Guid.NewGuid():N}"
                : request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            ItemId = request.ItemId,
            Profile = profile,
            CreatedAt = DateTimeOffset.UtcNow,
            AcceptedCount = accepted.Count,
            BlockedCount = blocked.Count,
            AcceptedRelations = accepted.ToArray(),
            BlockedRelations = blocked.ToArray(),
            Warnings = warnings.ToArray()
        };
    }

    private sealed record FrontierNode(string ItemId, int Depth, string Path);
}
