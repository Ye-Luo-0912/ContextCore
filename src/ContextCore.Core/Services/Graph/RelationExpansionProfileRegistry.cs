using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>关系扩展预览的 profile registry；不影响正式 relation expansion。</summary>
public sealed class RelationExpansionProfileRegistry
{
    private readonly IReadOnlyDictionary<string, RelationExpansionProfile> _profiles;

    public RelationExpansionProfileRegistry()
    {
        var commonTypes = new[]
        {
            "contains",
            "references",
            ContextRelationTypes.DerivedFrom,
            ContextRelationTypes.EvidenceFor,
            "supports",
            ContextRelationTypes.DependsOn,
            "requires",
            "blocks",
            "conflicts_with",
            ContextRelationTypes.AppliesTo,
            ContextRelationTypes.SupersededBy,
            ContextRelationTypes.Replaces,
            "replaced_by",
            "same_as",
            ContextRelationTypes.RelatedTo
        };

        _profiles = new[]
        {
            Profile(
                "normal-v1",
                "Normal",
                "Default",
                maxDepth: 1,
                maxFanout: 8,
                allowedTypes: commonTypes,
                blockedTypes: [],
                minConfidence: 0.5,
                requireEvidence: true,
                auditOnlyTypes: [],
                lifecyclePolicy: "ActiveReviewedEvidence",
                traversalPolicies: NormalTraversalPolicies()),
            Profile(
                "audit-v1",
                "Audit",
                "AuditDeprecated",
                maxDepth: 2,
                maxFanout: 20,
                allowedTypes: [.. commonTypes, "replaced_by"],
                blockedTypes: [],
                minConfidence: 0.0,
                allowDeprecated: true,
                requireEvidence: false,
                auditOnlyTypes: ["replaced_by"],
                lifecyclePolicy: "AuditAllowsHistorical",
                traversalPolicies: AuditTraversalPolicies()),
            Profile(
                "conflict-v1",
                "Conflict",
                "ConflictCheck",
                maxDepth: 2,
                maxFanout: 12,
                allowedTypes: ["conflicts_with", "blocks", "supports", ContextRelationTypes.EvidenceFor, "references", ContextRelationTypes.SupersededBy, ContextRelationTypes.Replaces, "replaced_by"],
                blockedTypes: [],
                minConfidence: 0.6,
                allowDeprecated: true,
                requireEvidence: true,
                lifecyclePolicy: "ActiveConflictEvidence",
                traversalPolicies: ConflictTraversalPolicies()),
            Profile(
                "current-task-v1",
                "Normal",
                "CurrentTask",
                maxDepth: 1,
                maxFanout: 6,
                allowedTypes: [ContextRelationTypes.DependsOn, "requires", "supports", "references", "conflicts_with", ContextRelationTypes.AppliesTo, ContextRelationTypes.EvidenceFor, ContextRelationTypes.SupersededBy, ContextRelationTypes.Replaces, "replaced_by"],
                blockedTypes: [],
                minConfidence: 0.6,
                requireEvidence: true,
                auditOnlyTypes: [],
                lifecyclePolicy: "CurrentTaskActiveEvidence",
                traversalPolicies: CurrentTaskTraversalPolicies())
        }.ToDictionary(item => item.ProfileId, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RelationExpansionProfile> GetAll()
    {
        return _profiles.Values
            .OrderBy(item => item.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToArray();
    }

    public RelationExpansionProfile? Find(string profileId)
    {
        return _profiles.TryGetValue(profileId, out var profile) ? Clone(profile) : null;
    }

    private static RelationExpansionProfile Profile(
        string profileId,
        string mode,
        string intent,
        int maxDepth,
        int maxFanout,
        IReadOnlyList<string> allowedTypes,
        IReadOnlyList<string> blockedTypes,
        double minConfidence,
        bool allowCandidate = false,
        bool allowDeprecated = false,
        bool allowRejected = false,
        bool requireEvidence = true,
        IReadOnlyList<string>? auditOnlyTypes = null,
        string lifecyclePolicy = "",
        IReadOnlyList<RelationTraversalPolicy>? traversalPolicies = null)
    {
        return new RelationExpansionProfile
        {
            ProfileId = profileId,
            Mode = mode,
            Intent = intent,
            MaxDepth = maxDepth,
            MaxFanout = maxFanout,
            AllowedRelationTypes = allowedTypes,
            BlockedRelationTypes = blockedTypes,
            MinConfidence = minConfidence,
            AllowCandidateRelations = allowCandidate,
            AllowDeprecatedRelations = allowDeprecated,
            AllowRejectedRelations = allowRejected,
            RequireEvidence = requireEvidence,
            AuditOnlyTypes = auditOnlyTypes ?? Array.Empty<string>(),
            WeightByRelationType = allowedTypes
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(type => type, _ => 1.0, StringComparer.OrdinalIgnoreCase),
            LifecyclePolicy = lifecyclePolicy,
            TraversalPolicies = traversalPolicies ?? Array.Empty<RelationTraversalPolicy>()
        };
    }

    private static RelationExpansionProfile Clone(RelationExpansionProfile profile)
    {
        return new RelationExpansionProfile
        {
            ProfileId = profile.ProfileId,
            Mode = profile.Mode,
            Intent = profile.Intent,
            MaxDepth = profile.MaxDepth,
            MaxFanout = profile.MaxFanout,
            AllowedRelationTypes = profile.AllowedRelationTypes.ToArray(),
            BlockedRelationTypes = profile.BlockedRelationTypes.ToArray(),
            MinConfidence = profile.MinConfidence,
            AllowCandidateRelations = profile.AllowCandidateRelations,
            AllowDeprecatedRelations = profile.AllowDeprecatedRelations,
            AllowRejectedRelations = profile.AllowRejectedRelations,
            RequireEvidence = profile.RequireEvidence,
            AuditOnlyTypes = profile.AuditOnlyTypes.ToArray(),
            WeightByRelationType = new Dictionary<string, double>(profile.WeightByRelationType, StringComparer.OrdinalIgnoreCase),
            LifecyclePolicy = profile.LifecyclePolicy,
            TraversalPolicies = profile.TraversalPolicies.Select(CloneTraversalPolicy).ToArray()
        };
    }

    private static IReadOnlyList<RelationTraversalPolicy> NormalTraversalPolicies()
    {
        return
        [
            TraversalPolicy(ContextRelationTypes.SupersededBy, [RelationTraversalDirections.TowardLatest], allowHistorical: false, targetSection: GraphExpansionTargetSection.NormalContext, reason: "normal profile follows replacement chains only toward latest items"),
            TraversalPolicy("replaced_by", [RelationTraversalDirections.TowardLatest], allowHistorical: false, targetSection: GraphExpansionTargetSection.NormalContext, reason: "normal profile follows replacement chains only toward latest items"),
            TraversalPolicy(ContextRelationTypes.Replaces, [RelationTraversalDirections.TowardLatest], allowHistorical: false, targetSection: GraphExpansionTargetSection.NormalContext, reason: "normal profile blocks new-to-old replacement traversal")
        ];
    }

    private static IReadOnlyList<RelationTraversalPolicy> CurrentTaskTraversalPolicies()
    {
        return
        [
            TraversalPolicy(ContextRelationTypes.SupersededBy, [RelationTraversalDirections.TowardLatest], allowHistorical: false, targetSection: GraphExpansionTargetSection.NormalContext, reason: "current-task profile follows toward-latest replacement only"),
            TraversalPolicy("replaced_by", [RelationTraversalDirections.TowardLatest], allowHistorical: false, targetSection: GraphExpansionTargetSection.NormalContext, reason: "current-task profile follows toward-latest replacement only"),
            TraversalPolicy(ContextRelationTypes.Replaces, [RelationTraversalDirections.TowardLatest], allowHistorical: false, targetSection: GraphExpansionTargetSection.NormalContext, reason: "current-task profile blocks toward-historical replacement")
        ];
    }

    private static IReadOnlyList<RelationTraversalPolicy> AuditTraversalPolicies()
    {
        return
        [
            TraversalPolicy(ContextRelationTypes.SupersededBy, [RelationTraversalDirections.Both], allowHistorical: true, targetSection: GraphExpansionTargetSection.AuditContext, reason: "audit profile may inspect latest and historical replacement chain items outside normal context"),
            TraversalPolicy(ContextRelationTypes.Replaces, [RelationTraversalDirections.Both], allowHistorical: true, targetSection: GraphExpansionTargetSection.AuditContext, reason: "audit profile may inspect latest and historical replacement chain items outside normal context"),
            TraversalPolicy("replaced_by", [RelationTraversalDirections.Both], allowHistorical: true, targetSection: GraphExpansionTargetSection.AuditContext, reason: "audit profile may inspect latest and historical replacement chain items outside normal context")
        ];
    }

    private static IReadOnlyList<RelationTraversalPolicy> ConflictTraversalPolicies()
    {
        return
        [
            TraversalPolicy(ContextRelationTypes.SupersededBy, [RelationTraversalDirections.Both], allowHistorical: true, targetSection: GraphExpansionTargetSection.ConflictEvidence, reason: "conflict profile can inspect both replacement directions as conflict evidence when evidence and confidence pass"),
            TraversalPolicy(ContextRelationTypes.Replaces, [RelationTraversalDirections.Both], allowHistorical: true, targetSection: GraphExpansionTargetSection.ConflictEvidence, reason: "conflict profile can inspect both replacement directions as conflict evidence when evidence and confidence pass"),
            TraversalPolicy("replaced_by", [RelationTraversalDirections.Both], allowHistorical: true, targetSection: GraphExpansionTargetSection.ConflictEvidence, reason: "conflict profile can inspect both replacement directions as conflict evidence when evidence and confidence pass")
        ];
    }

    private static RelationTraversalPolicy TraversalPolicy(
        string relationType,
        IReadOnlyList<string> directions,
        bool allowHistorical,
        string targetSection,
        string reason)
    {
        return new RelationTraversalPolicy
        {
            RelationType = relationType,
            AllowedDirections = directions,
            AllowedTargetLifecycle = allowHistorical
                ? Array.Empty<string>()
                : [StableMemoryLifecycle.Active, StableMemoryLifecycle.Current, ContextMemoryStatus.Stable.ToString(), ContextMemoryStatus.Active.ToString()],
            BlockedTargetLifecycle = allowHistorical
                ? Array.Empty<string>()
                : [StableMemoryLifecycle.Deprecated, StableMemoryLifecycle.Superseded, StableMemoryLifecycle.Rejected, "Historical"],
            AllowHistoricalTarget = allowHistorical,
            TargetSection = targetSection,
            Reason = reason
        };
    }

    private static RelationTraversalPolicy CloneTraversalPolicy(RelationTraversalPolicy policy)
    {
        return new RelationTraversalPolicy
        {
            RelationType = policy.RelationType,
            AllowedDirections = policy.AllowedDirections.ToArray(),
            AllowedTargetLifecycle = policy.AllowedTargetLifecycle.ToArray(),
            BlockedTargetLifecycle = policy.BlockedTargetLifecycle.ToArray(),
            AllowHistoricalTarget = policy.AllowHistoricalTarget,
            TargetSection = policy.TargetSection,
            Reason = policy.Reason
        };
    }
}
