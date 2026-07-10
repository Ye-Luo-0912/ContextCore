using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>构建 relation expansion preview，不改变 retrieval、packing 或 package 输出。</summary>
public sealed class RelationExpansionPreviewService
{
    private readonly RelationTraversalEngine? _traversalEngine;
    private readonly RelationExpansionProfileRegistry _profileRegistry;
    private readonly RelationExpansionPolicyValidator _validator;

    public RelationExpansionPreviewService(
        RelationTraversalEngine? traversalEngine,
        RelationExpansionProfileRegistry profileRegistry,
        RelationExpansionPolicyValidator validator)
    {
        _traversalEngine = traversalEngine;
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

        if (_traversalEngine is null)
        {
            warnings.Add("relation store is not registered.");
            return BuildResponse(request, profile, accepted, blocked, warnings);
        }

        // engine 做最小化过滤：仅 dedup + depth 控制；不做 type/fanout/confidence/lifecycle 过滤，
        // 让 validator 在 post-processing 中用规范化类型做完整 acceptance/blocking 决策（保持 legacy 类型归一化、FanoutExceeded 等行为）。
        var engineProfile = new RelationExpansionProfile
        {
            ProfileId = profile.ProfileId,
            Mode = profile.Mode,
            Intent = profile.Intent,
            MaxDepth = profile.MaxDepth,
            MaxFanout = 10000,
            AllowedRelationTypes = Array.Empty<string>(),
            BlockedRelationTypes = Array.Empty<string>(),
            MinConfidence = 0.0,
            AllowCandidateRelations = true,
            AllowDeprecatedRelations = true,
            AllowRejectedRelations = true,
            RequireEvidence = false,
            AuditOnlyTypes = profile.AuditOnlyTypes,
            WeightByRelationType = new Dictionary<string, double>(profile.WeightByRelationType, StringComparer.OrdinalIgnoreCase),
            LifecyclePolicy = profile.LifecyclePolicy,
            TraversalPolicies = profile.TraversalPolicies
        };

        var traversalRequest = new RelationTraversalRequest
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Seeds = [new RelationTraversalSeed(request.ItemId)],
            Profile = engineProfile,
            Direction = RelationDirection.Outgoing,
            MaxNodesOverride = 500,
            MaxRelationsOverride = 2000
        };

        var traversalResult = await _traversalEngine.TraverseAsync(traversalRequest, cancellationToken).ConfigureAwait(false);

        // 重新计算 per-node fanoutIndex：按 (Depth, SourceId) 分组，组内按 weight/confidence 降序编号。
        var fanoutIndexByGroup = new Dictionary<(int Depth, string SourceId), int>();
        var orderedEdges = traversalResult.Edges
            .OrderBy(e => e.Depth)
            .ThenBy(e => e.Relation.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(e => _validator.ResolveWeight(e.Relation, profile))
            .ThenByDescending(e => RelationExpansionPolicyValidator.ResolveConfidence(e.Relation))
            .ThenByDescending(e => e.Relation.CreatedAt)
            .ToArray();

        foreach (var edge in orderedEdges)
        {
            var groupKey = (edge.Depth, edge.Relation.SourceId);
            fanoutIndexByGroup.TryGetValue(groupKey, out var fanoutIndex);
            fanoutIndex++;
            fanoutIndexByGroup[groupKey] = fanoutIndex;

            var validation = _validator.Validate(edge.Relation, profile, edge.Depth, fanoutIndex);
            var previewRelation = BuildPreviewRelation(edge.Relation, profile, validation, edge.Depth, edge.Path);
            if (validation.Accepted)
            {
                accepted.Add(previewRelation);
            }
            else
            {
                blocked.Add(previewRelation);
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
            Path = sourcePath,
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
}
