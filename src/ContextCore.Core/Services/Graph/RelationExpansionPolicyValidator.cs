using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>根据治理 profile 校验 relation expansion preview 边。</summary>
public sealed class RelationExpansionPolicyValidator
{
    private readonly RelationTypeRegistry _typeRegistry;
    private readonly RelationTypeNormalizer _typeNormalizer;

    public RelationExpansionPolicyValidator(RelationTypeRegistry typeRegistry)
        : this(typeRegistry, new RelationTypeNormalizer())
    {
    }

    public RelationExpansionPolicyValidator(RelationTypeRegistry typeRegistry, RelationTypeNormalizer typeNormalizer)
    {
        _typeRegistry = typeRegistry;
        _typeNormalizer = typeNormalizer;
    }

    public RelationExpansionPolicyValidationResult Validate(
        ContextRelation relation,
        RelationExpansionProfile profile,
        int depth,
        int fanoutIndex)
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(profile);

        var reasons = new List<string>();
        var warnings = new List<string>();
        var normalizedType = _typeNormalizer.Normalize(relation.RelationType);
        var definition = _typeRegistry.Find(normalizedType);
        if (!string.Equals(normalizedType, relation.RelationType, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"legacy relation type {relation.RelationType} normalized to {normalizedType}.");
        }

        if (depth > profile.MaxDepth)
        {
            reasons.Add(RelationExpansionValidationReasons.DepthExceeded);
        }

        if (fanoutIndex > profile.MaxFanout)
        {
            reasons.Add(RelationExpansionValidationReasons.FanoutExceeded);
        }

        if (definition is null)
        {
            reasons.Add(RelationExpansionValidationReasons.UnknownRelationType);
        }

        if (profile.BlockedRelationTypes.Any(type => Is(type, normalizedType)))
        {
            reasons.Add(RelationExpansionValidationReasons.BlockedRelationType);
        }

        if (profile.AllowedRelationTypes.Count > 0
            && !profile.AllowedRelationTypes.Any(type => Is(type, normalizedType)))
        {
            reasons.Add(RelationExpansionValidationReasons.RelationTypeNotAllowed);
        }

        if (ResolveConfidence(relation) < profile.MinConfidence)
        {
            reasons.Add(RelationExpansionValidationReasons.ConfidenceTooLow);
        }

        if ((profile.RequireEvidence || definition?.RequiresEvidence == true)
            && !RelationTypeNormalizer.HasEvidence(relation))
        {
            reasons.Add(RelationExpansionValidationReasons.MissingEvidence);
        }

        if (IsAuditOnly(profile, definition, normalizedType)
            && !IsAuditProfile(profile))
        {
            reasons.Add(RelationExpansionValidationReasons.AuditOnlyRelationInNormalProfile);
        }

        var lifecycle = ResolveMetadata(relation, "lifecycle");
        var reviewStatus = ResolveMetadata(relation, "reviewStatus");
        if (IsLifecycleBlocked(lifecycle, reviewStatus, profile))
        {
            reasons.Add(RelationExpansionValidationReasons.InvalidLifecycle);
        }

        var traversalPolicy = FindTraversalPolicy(profile, normalizedType);
        var traversalDirection = ResolveTraversalDirection(relation, normalizedType);
        var targetLifecycle = ResolveTargetLifecycle(relation);
        var targetSection = traversalPolicy?.TargetSection ?? GraphExpansionTargetSection.NormalContext;
        var sectionReason = traversalPolicy?.Reason ?? "default normal context route";
        ApplyTraversalPolicy(
            relation,
            normalizedType,
            traversalPolicy,
            traversalDirection,
            targetLifecycle,
            profile,
            reasons,
            warnings,
            ref targetSection,
            ref sectionReason);
        ApplyTargetLifecyclePolicy(normalizedType, targetLifecycle, profile, reasons);
        ApplySectionRouting(
            normalizedType,
            targetLifecycle,
            profile,
            warnings,
            ref targetSection,
            ref sectionReason);
        var previewSectionOverride = ResolveMetadata(relation, "previewTargetSectionOverride");
        if (!string.IsNullOrWhiteSpace(previewSectionOverride))
        {
            targetSection = previewSectionOverride;
            sectionReason = "preview target section override";
        }

        var riskIfNormalSelected = IsRiskIfNormalSelected(normalizedType, traversalDirection, targetLifecycle);
        var riskAfterSectionRouting = riskIfNormalSelected
            && string.Equals(targetSection, GraphExpansionTargetSection.NormalContext, StringComparison.OrdinalIgnoreCase);
        if (riskAfterSectionRouting && (IsAuditProfile(profile) || IsConflictProfile(profile)))
        {
            reasons.Add(RelationExpansionValidationReasons.BlockedByWrongSectionRisk);
        }

        if (definition is not null && !definition.AllowsNormalExpansion && !IsAuditProfile(profile))
        {
            warnings.Add($"{normalizedType} is not marked for normal expansion.");
        }

        return new RelationExpansionPolicyValidationResult
        {
            Accepted = reasons.Count == 0,
            Reasons = reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            TraversalDirection = traversalDirection,
            TargetLifecycle = targetLifecycle,
            TargetSection = targetSection,
            SectionReason = sectionReason,
            RiskIfNormalSelected = riskIfNormalSelected,
            RiskAfterSectionRouting = riskAfterSectionRouting
        };
    }

    public double ResolveWeight(ContextRelation relation, RelationExpansionProfile profile)
    {
        var normalizedType = _typeNormalizer.Normalize(relation.RelationType);
        if (profile.WeightByRelationType.TryGetValue(normalizedType, out var weight))
        {
            return weight;
        }

        return _typeRegistry.Find(normalizedType)?.DefaultWeight
            ?? relation.Weight;
    }

    public static double ResolveConfidence(ContextRelation relation)
    {
        if (relation.Confidence > 0)
        {
            return relation.Confidence;
        }

        var value = ResolveMetadata(relation, "confidence");
        return double.TryParse(value, out var parsed) ? parsed : 0.0;
    }

    public static string ResolveLifecycle(ContextRelation relation)
    {
        return NormalizeState(ResolveMetadata(relation, "lifecycle"), "Active");
    }

    public static string ResolveReviewStatus(ContextRelation relation)
    {
        return NormalizeState(ResolveMetadata(relation, "reviewStatus"), string.Empty);
    }

    public static string ResolveTargetLifecycle(ContextRelation relation)
    {
        return NormalizeState(ResolveMetadata(relation, "targetLifecycle", "targetStatus"), StableMemoryLifecycle.Active);
    }

    public string ResolveNormalizedRelationType(ContextRelation relation)
    {
        return _typeNormalizer.Normalize(relation.RelationType);
    }

    private static bool IsAuditOnly(
        RelationExpansionProfile profile,
        RelationTypeDefinition? definition,
        string relationType)
    {
        return definition?.AuditOnly == true
            || profile.AuditOnlyTypes.Any(type => Is(type, relationType));
    }

    private static bool IsAuditProfile(RelationExpansionProfile profile)
    {
        return Is(profile.Mode, "Audit")
            || Is(profile.Intent, "AuditDeprecated")
            || profile.ProfileId.Contains("audit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConflictProfile(RelationExpansionProfile profile)
    {
        return Is(profile.Mode, "Conflict")
            || Is(profile.Intent, "ConflictCheck")
            || profile.ProfileId.Contains("conflict", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLifecycleBlocked(
        string lifecycle,
        string reviewStatus,
        RelationExpansionProfile profile)
    {
        if ((Is(lifecycle, "Candidate") || Is(reviewStatus, "Candidate"))
            && !profile.AllowCandidateRelations)
        {
            return true;
        }

        if ((Is(lifecycle, "Deprecated") || Is(lifecycle, "Superseded"))
            && !profile.AllowDeprecatedRelations)
        {
            return true;
        }

        if ((Is(lifecycle, "Rejected") || Is(reviewStatus, RelationReviewStatuses.Rejected))
            && !profile.AllowRejectedRelations)
        {
            return true;
        }

        return false;
    }

    private static void ApplyTraversalPolicy(
        ContextRelation relation,
        string normalizedType,
        RelationTraversalPolicy? policy,
        string traversalDirection,
        string targetLifecycle,
        RelationExpansionProfile profile,
        List<string> reasons,
        List<string> warnings,
        ref string targetSection,
        ref string sectionReason)
    {
        if (!IsReplacementType(normalizedType))
        {
            return;
        }

        if (IsTargetMissing(relation))
        {
            reasons.Add(RelationExpansionValidationReasons.ReplacementTargetMissing);
            return;
        }

        if (IsRejectedLifecycle(targetLifecycle))
        {
            reasons.Add(RelationExpansionValidationReasons.ReplacementTargetRejected);
        }

        if (policy is not null)
        {
            if (!DirectionAllowed(policy, traversalDirection))
            {
                reasons.Add(RelationExpansionValidationReasons.BackwardReplacementTraversalBlocked);
                if (string.Equals(traversalDirection, RelationTraversalDirections.TowardHistorical, StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add(RelationExpansionValidationReasons.HistoricalTargetBlocked);
                }
            }

            if (policy.BlockedTargetLifecycle.Any(item => IsLifecycle(item, targetLifecycle)))
            {
                AddTargetLifecycleBlockReason(targetLifecycle, reasons);
            }

            if (policy.AllowedTargetLifecycle.Count > 0
                && !policy.AllowedTargetLifecycle.Any(item => IsLifecycle(item, targetLifecycle)))
            {
                AddTargetLifecycleBlockReason(targetLifecycle, reasons);
                if (string.Equals(traversalDirection, RelationTraversalDirections.TowardLatest, StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add(RelationExpansionValidationReasons.ReplacementTargetInactive);
                }
            }

            if (IsHistoricalLifecycle(targetLifecycle))
            {
                if (!policy.AllowHistoricalTarget)
                {
                    AddTargetLifecycleBlockReason(targetLifecycle, reasons);
                }
                else if (IsAuditProfile(profile)
                    && string.Equals(policy.TargetSection, RelationExpansionTargetSections.AuditHistorical, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(RelationExpansionValidationReasons.HistoricalAllowedOnlyInAudit);
                    targetSection = RelationExpansionTargetSections.AuditHistorical;
                    sectionReason = policy.Reason;
                }
                else if (!IsAuditProfile(profile)
                    && string.Equals(policy.TargetSection, RelationExpansionTargetSections.AuditHistorical, StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add(RelationExpansionValidationReasons.AuditOnlyHistoricalTraversal);
                }
            }
        }

        if (string.Equals(traversalDirection, RelationTraversalDirections.TowardHistorical, StringComparison.OrdinalIgnoreCase)
            && !IsAuditProfile(profile)
            && policy?.AllowHistoricalTarget != true)
        {
            reasons.Add(RelationExpansionValidationReasons.BackwardReplacementTraversalBlocked);
        }
    }

    private static void ApplyTargetLifecyclePolicy(
        string normalizedType,
        string targetLifecycle,
        RelationExpansionProfile profile,
        List<string> reasons)
    {
        if (IsReplacementType(normalizedType))
        {
            return;
        }

        if (IsRejectedLifecycle(targetLifecycle) && !profile.AllowRejectedRelations)
        {
            reasons.Add(RelationExpansionValidationReasons.ReplacementTargetRejected);
            return;
        }

        if (IsHistoricalLifecycle(targetLifecycle) && !profile.AllowDeprecatedRelations)
        {
            AddTargetLifecycleBlockReason(targetLifecycle, reasons);
        }
    }

    private static void ApplySectionRouting(
        string normalizedType,
        string targetLifecycle,
        RelationExpansionProfile profile,
        List<string> warnings,
        ref string targetSection,
        ref string sectionReason)
    {
        if (IsAuditProfile(profile))
        {
            targetSection = IsHistoricalLifecycle(targetLifecycle)
                ? GraphExpansionTargetSection.AuditContext
                : GraphExpansionTargetSection.AuditContext;
            sectionReason = IsHistoricalLifecycle(targetLifecycle)
                ? "audit profile routes deprecated/historical target outside normal context"
                : "audit profile routes accepted relation into audit context";
            if (IsHistoricalLifecycle(targetLifecycle))
            {
                warnings.Add(RelationExpansionValidationReasons.HistoricalAllowedOnlyInAudit);
            }

            return;
        }

        if (IsConflictProfile(profile))
        {
            if (IsConflictEvidenceType(normalizedType) || IsReplacementType(normalizedType))
            {
                targetSection = GraphExpansionTargetSection.ConflictEvidence;
                sectionReason = "conflict profile routes accepted relation into conflict evidence";
                return;
            }

            if (IsHistoricalLifecycle(targetLifecycle))
            {
                targetSection = GraphExpansionTargetSection.HistoricalContext;
                sectionReason = "conflict profile routes historical target outside normal context";
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(targetSection))
        {
            targetSection = GraphExpansionTargetSection.NormalContext;
        }

        if (string.IsNullOrWhiteSpace(sectionReason))
        {
            sectionReason = "default normal context route";
        }
    }

    private static RelationTraversalPolicy? FindTraversalPolicy(RelationExpansionProfile profile, string normalizedType)
    {
        return profile.TraversalPolicies.FirstOrDefault(policy =>
            string.Equals(policy.RelationType, normalizedType, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveTraversalDirection(ContextRelation relation, string normalizedType)
    {
        var explicitDirection = ResolveMetadata(relation, "traversalDirection", "replacementDirection");
        if (!string.IsNullOrWhiteSpace(explicitDirection))
        {
            return explicitDirection;
        }

        if (string.Equals(normalizedType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, "replaced_by", StringComparison.OrdinalIgnoreCase))
        {
            return RelationTraversalDirections.TowardLatest;
        }

        if (string.Equals(normalizedType, ContextRelationTypes.Replaces, StringComparison.OrdinalIgnoreCase))
        {
            return RelationTraversalDirections.TowardHistorical;
        }

        return RelationTraversalDirections.Any;
    }

    private static bool DirectionAllowed(RelationTraversalPolicy policy, string traversalDirection)
    {
        return policy.AllowedDirections.Count == 0
            || policy.AllowedDirections.Any(direction =>
                Is(direction, RelationTraversalDirections.Any)
                || Is(direction, RelationTraversalDirections.Both)
                || Is(direction, traversalDirection));
    }

    private static void AddTargetLifecycleBlockReason(string targetLifecycle, List<string> reasons)
    {
        if (string.Equals(targetLifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(RelationExpansionValidationReasons.DeprecatedTargetBlocked);
        }
        else if (IsHistoricalLifecycle(targetLifecycle))
        {
            reasons.Add(RelationExpansionValidationReasons.HistoricalTargetBlocked);
        }
        else if (IsRejectedLifecycle(targetLifecycle))
        {
            reasons.Add(RelationExpansionValidationReasons.ReplacementTargetRejected);
        }
        else
        {
            reasons.Add(RelationExpansionValidationReasons.ReplacementTargetInactive);
        }
    }

    private static bool IsTargetMissing(ContextRelation relation)
    {
        return string.Equals(ResolveMetadata(relation, "targetExists"), "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ResolveMetadata(relation, "targetMissing"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReplacementType(string normalizedType)
    {
        return string.Equals(normalizedType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, ContextRelationTypes.Replaces, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, "replaced_by", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConflictEvidenceType(string normalizedType)
    {
        return string.Equals(normalizedType, "conflicts_with", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, "blocks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, "supports", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedType, ContextRelationTypes.EvidenceFor, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRiskIfNormalSelected(
        string normalizedType,
        string traversalDirection,
        string targetLifecycle)
    {
        return IsHistoricalLifecycle(targetLifecycle)
            || IsRejectedLifecycle(targetLifecycle)
            || string.Equals(normalizedType, "conflicts_with", StringComparison.OrdinalIgnoreCase)
            || string.Equals(traversalDirection, RelationTraversalDirections.TowardHistorical, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoricalLifecycle(string lifecycle)
    {
        return string.Equals(lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)
            || string.Equals(lifecycle, StableMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase)
            || string.Equals(lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lifecycle, "DeprecatedMemory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRejectedLifecycle(string lifecycle)
    {
        return string.Equals(lifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(lifecycle, ContextMemoryStatus.Rejected.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLifecycle(string expected, string actual)
    {
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            || string.Equals(expected, "Historical", StringComparison.OrdinalIgnoreCase) && IsHistoricalLifecycle(actual);
    }

    private static string ResolveMetadata(ContextRelation relation, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var pair in relation.Metadata)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }

    private static string NormalizeState(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static bool Is(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
