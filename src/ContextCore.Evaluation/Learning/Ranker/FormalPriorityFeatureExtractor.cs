using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>提取 formal ranking 隐含优先级的 shadow-only 特征；不读取 eval label、sampleId 或 itemId 内容。</summary>
public sealed class FormalPriorityFeatureExtractor
{
    private static readonly Dictionary<string, FormalPriorityShadowProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        [CandidateRerankerShadowProfiles.BaselineLifecycleAware] = new(
            CandidateRerankerShadowProfiles.BaselineLifecycleAware,
            UseFormalPriority: false,
            UseAbstain: false,
            AbstainMarginThreshold: 0,
            FeatureWeights: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)),
        [CandidateRerankerShadowProfiles.FormalPriorityAwareV1] = new(
            CandidateRerankerShadowProfiles.FormalPriorityAwareV1,
            UseFormalPriority: true,
            UseAbstain: false,
            AbstainMarginThreshold: 0,
            FeatureWeights: DefaultWeights()),
        [CandidateRerankerShadowProfiles.FormalPriorityAwareWithAbstainV1] = new(
            CandidateRerankerShadowProfiles.FormalPriorityAwareWithAbstainV1,
            UseFormalPriority: true,
            UseAbstain: true,
            AbstainMarginThreshold: 1.0,
            FeatureWeights: DefaultWeights())
    };

    public FormalPriorityShadowProfile ResolveProfile(string? profileId)
    {
        return !string.IsNullOrWhiteSpace(profileId) && Profiles.TryGetValue(profileId, out var profile)
            ? profile
            : Profiles[CandidateRerankerShadowProfiles.BaselineLifecycleAware];
    }

    public CandidateRerankerFormalPriorityFeatureSet Extract(LifecycleAwareRankerShadowCandidateScore candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var surface = MetadataSurface(candidate);
        var layerPriority = LayerPriority(surface);
        var sourcePriority = SourcePriority(candidate);
        var currentTaskBoost = ContainsSystemToken(surface, "current") || ContainsSystemToken(surface, "task")
            ? 1
            : 0;
        var constraintRelevance = ContainsSystemToken(surface, "constraint") ? 1 : 0;
        var relationEvidenceBoost = ContainsSystemToken(surface, "relation")
            || ContainsSystemToken(surface, "evidence")
            || ContainsSystemToken(surface, "conflict")
                ? 1
                : 0;
        var stableMemoryBias = ContainsSystemToken(surface, "stable") ? 1 : 0;
        var workingMemoryBias = ContainsSystemToken(surface, "working") ? 1 : 0;
        var candidateMemoryPenalty = ContainsSystemToken(surface, "candidate") ? 1 : 0;
        var freshnessPriority = candidate.LegacyRank <= 0
            ? 0
            : 1.0 / candidate.LegacyRank;
        var packagePolicyPriority = candidate.Selected ? 1 : 0;
        var lifecyclePriority = candidate.LifecycleFeatures.IsRejected
            || candidate.LifecycleFeatures.IsDeprecated
            || candidate.LifecycleFeatures.IsSuperseded
            || candidate.LifecycleFeatures.IsHistorical
                ? -1
                : candidate.LifecycleFeatures.IsCurrentVersion ? 1 : 0.25;
        var reasons = BuildReasons(
                layerPriority,
                sourcePriority,
                currentTaskBoost,
                constraintRelevance,
                relationEvidenceBoost,
                stableMemoryBias,
                workingMemoryBias,
                candidateMemoryPenalty,
                freshnessPriority,
                packagePolicyPriority,
                lifecyclePriority)
            .ToArray();

        return new CandidateRerankerFormalPriorityFeatureSet
        {
            LayerPriority = layerPriority,
            SourcePriority = sourcePriority,
            CurrentTaskBoost = currentTaskBoost,
            ConstraintRelevance = constraintRelevance,
            RelationEvidenceBoost = relationEvidenceBoost,
            StableMemoryBias = stableMemoryBias,
            WorkingMemoryBias = workingMemoryBias,
            CandidateMemoryPenalty = candidateMemoryPenalty,
            FreshnessPriority = freshnessPriority,
            PackagePolicyPriority = packagePolicyPriority,
            LifecyclePriority = lifecyclePriority,
            Total = layerPriority
                + sourcePriority
                + currentTaskBoost
                + constraintRelevance
                + relationEvidenceBoost
                + stableMemoryBias
                + workingMemoryBias
                - candidateMemoryPenalty
                + freshnessPriority
                + packagePolicyPriority
                + lifecyclePriority,
            Reasons = reasons
        };
    }

    public CandidateRerankerFormalPriorityComparison Compare(
        LifecycleAwareRankerShadowCandidateScore? formalTop,
        LifecycleAwareRankerShadowCandidateScore? shadowTop)
    {
        if (formalTop is null || shadowTop is null)
        {
            return new CandidateRerankerFormalPriorityComparison
            {
                LayerPriority = "InsufficientCandidates",
                SourcePriority = "InsufficientCandidates",
                ConstraintRelevance = "Unknown",
                CurrentTaskBoost = "Unknown",
                WorkingStableCandidateBias = "Unknown",
                Freshness = "Unknown",
                PackagePolicyPriority = "Unknown",
                RelationPriority = "Unknown"
            };
        }

        var formalFeatures = Extract(formalTop);
        var shadowFeatures = Extract(shadowTop);
        var layerPriority = formalFeatures.LayerPriority > shadowFeatures.LayerPriority
            ? "FormalLayerPriority"
            : "Aligned";
        var sourcePriority = formalFeatures.SourcePriority > shadowFeatures.SourcePriority
            ? "FormalSourcePriority"
            : "Aligned";
        var constraintRelevance = formalFeatures.ConstraintRelevance > shadowFeatures.ConstraintRelevance
            ? "FormalConstraintPriority"
            : "Aligned";
        var currentTaskBoost = formalFeatures.CurrentTaskBoost > shadowFeatures.CurrentTaskBoost
            ? "FormalCurrentTaskPriority"
            : "Aligned";
        var workingStableCandidateBias =
            formalFeatures.WorkingMemoryBias + formalFeatures.StableMemoryBias - formalFeatures.CandidateMemoryPenalty
            > shadowFeatures.WorkingMemoryBias + shadowFeatures.StableMemoryBias - shadowFeatures.CandidateMemoryPenalty
                ? "FormalLayerBias"
                : "Aligned";
        var freshness = formalFeatures.FreshnessPriority > shadowFeatures.FreshnessPriority
            ? "FormalLegacyRankPriority"
            : "Aligned";
        var packagePolicyPriority = formalFeatures.PackagePolicyPriority > shadowFeatures.PackagePolicyPriority
            ? "FormalSelectedSetPriority"
            : "Aligned";
        var relationPriority = formalFeatures.RelationEvidenceBoost > shadowFeatures.RelationEvidenceBoost
            ? "FormalRelationEvidencePriority"
            : "Aligned";
        var hasMismatch = new[]
        {
            layerPriority,
            sourcePriority,
            constraintRelevance,
            currentTaskBoost,
            workingStableCandidateBias,
            freshness,
            packagePolicyPriority,
            relationPriority
        }.Any(static value => !string.Equals(value, "Aligned", StringComparison.OrdinalIgnoreCase));

        return new CandidateRerankerFormalPriorityComparison
        {
            LayerPriority = layerPriority,
            SourcePriority = sourcePriority,
            ConstraintRelevance = constraintRelevance,
            CurrentTaskBoost = currentTaskBoost,
            WorkingStableCandidateBias = workingStableCandidateBias,
            Freshness = freshness,
            PackagePolicyPriority = packagePolicyPriority,
            RelationPriority = relationPriority,
            HasMismatch = hasMismatch,
            FormalTopFeatures = formalFeatures,
            ShadowTopFeatures = shadowFeatures
        };
    }

    public LifecycleAwareRankerShadowTrace ApplyProfile(
        LifecycleAwareRankerShadowTrace source,
        string? profileId)
    {
        ArgumentNullException.ThrowIfNull(source);
        var profile = ResolveProfile(profileId);
        if (!profile.UseFormalPriority || source.CandidateShadowScores.Count == 0)
        {
            return source;
        }

        var adjusted = source.CandidateShadowScores
            .Select(score => ApplyFeatureWeights(score, profile))
            .ToArray();
        var rankById = adjusted
            .OrderByDescending(static item => item.LifecycleAwareScore)
            .ThenBy(static item => item.LegacyRank)
            .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => new { item.CandidateId, Rank = index + 1 })
            .GroupBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Rank, StringComparer.OrdinalIgnoreCase);
        var ranked = adjusted
            .Select(item => WithShadowRank(
                item,
                rankById.TryGetValue(item.CandidateId, out var rank) ? rank : item.LegacyRank))
            .OrderBy(static item => item.LegacyRank)
            .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LifecycleAwareRankerShadowTrace
        {
            RankerShadowEnabled = source.RankerShadowEnabled,
            RankerShadowProfile = profile.ProfileId,
            CandidateShadowScores = ranked,
            DeprecatedDemotions = source.DeprecatedDemotions,
            VersionConflictFixes = source.VersionConflictFixes,
            MustHitDemotions = [.. ranked
                .Where(static item => item.IsMustHit && (item.ScoreDelta < 0 || item.ShadowRank > item.LegacyRank))
                .OrderBy(static item => item.LegacyRank)],
            MustNotHitPromotions = [.. ranked
                .Where(static item => item.IsMustNotHit && (item.ScoreDelta > 0 || item.ShadowRank < item.LegacyRank))
                .OrderBy(static item => item.ShadowRank)]
        };
    }

    private LifecycleAwareRankerShadowCandidateScore ApplyFeatureWeights(
        LifecycleAwareRankerShadowCandidateScore score,
        FormalPriorityShadowProfile profile)
    {
        var features = Extract(score);
        var adjustment = Weighted(features, profile.FeatureWeights);
        var reason = adjustment == 0
            ? score.Reason
            : string.Join(';', new[] { score.Reason, "formal_priority_alignment" }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));

        return new LifecycleAwareRankerShadowCandidateScore
        {
            CandidateId = score.CandidateId,
            Kind = score.Kind,
            Type = score.Type,
            SectionName = score.SectionName,
            Selected = score.Selected,
            IsMustHit = score.IsMustHit,
            IsMustNotHit = score.IsMustNotHit,
            LegacyRank = score.LegacyRank,
            ShadowRank = score.ShadowRank,
            RankDelta = score.RankDelta,
            LegacyScore = score.LegacyScore,
            LifecycleAwareScore = score.LifecycleAwareScore + adjustment,
            ScoreDelta = score.ScoreDelta + adjustment,
            Reason = reason,
            DemotionReasons = score.DemotionReasons,
            PromotionReasons = adjustment > 0
                ? score.PromotionReasons.Concat(["formal_priority_alignment"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : score.PromotionReasons,
            LifecycleFeatures = score.LifecycleFeatures
        };
    }

    private static LifecycleAwareRankerShadowCandidateScore WithShadowRank(
        LifecycleAwareRankerShadowCandidateScore item,
        int shadowRank)
    {
        return new LifecycleAwareRankerShadowCandidateScore
        {
            CandidateId = item.CandidateId,
            Kind = item.Kind,
            Type = item.Type,
            SectionName = item.SectionName,
            Selected = item.Selected,
            IsMustHit = item.IsMustHit,
            IsMustNotHit = item.IsMustNotHit,
            LegacyRank = item.LegacyRank,
            ShadowRank = shadowRank,
            RankDelta = item.LegacyRank - shadowRank,
            LegacyScore = item.LegacyScore,
            LifecycleAwareScore = item.LifecycleAwareScore,
            ScoreDelta = item.ScoreDelta,
            Reason = item.Reason,
            DemotionReasons = item.DemotionReasons,
            PromotionReasons = item.PromotionReasons,
            LifecycleFeatures = item.LifecycleFeatures
        };
    }

    private static double Weighted(
        CandidateRerankerFormalPriorityFeatureSet features,
        IReadOnlyDictionary<string, double> weights)
    {
        return features.LayerPriority * Weight(weights, nameof(features.LayerPriority))
            + features.SourcePriority * Weight(weights, nameof(features.SourcePriority))
            + features.CurrentTaskBoost * Weight(weights, nameof(features.CurrentTaskBoost))
            + features.ConstraintRelevance * Weight(weights, nameof(features.ConstraintRelevance))
            + features.RelationEvidenceBoost * Weight(weights, nameof(features.RelationEvidenceBoost))
            + features.StableMemoryBias * Weight(weights, nameof(features.StableMemoryBias))
            + features.WorkingMemoryBias * Weight(weights, nameof(features.WorkingMemoryBias))
            + features.CandidateMemoryPenalty * Weight(weights, nameof(features.CandidateMemoryPenalty))
            + features.FreshnessPriority * Weight(weights, nameof(features.FreshnessPriority))
            + features.PackagePolicyPriority * Weight(weights, nameof(features.PackagePolicyPriority))
            + features.LifecyclePriority * Weight(weights, nameof(features.LifecyclePriority));
    }

    private static double Weight(IReadOnlyDictionary<string, double> weights, string key)
    {
        return weights.TryGetValue(key, out var value) ? value : 0;
    }

    private static Dictionary<string, double> DefaultWeights()
    {
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(CandidateRerankerFormalPriorityFeatureSet.LayerPriority)] = 0.6,
            [nameof(CandidateRerankerFormalPriorityFeatureSet.SourcePriority)] = 0.4,
            [nameof(CandidateRerankerFormalPriorityFeatureSet.CurrentTaskBoost)] = 2.0,
            [nameof(CandidateRerankerFormalPriorityFeatureSet.ConstraintRelevance)] = 3.0,
            [nameof(CandidateRerankerFormalPriorityFeatureSet.RelationEvidenceBoost)] = 1.5,
            [nameof(CandidateRerankerFormalPriorityFeatureSet.StableMemoryBias)] = 1.2,
            [nameof(CandidateRerankerFormalPriorityFeatureSet.WorkingMemoryBias)] = 1.4,
            [nameof(CandidateRerankerFormalPriorityFeatureSet.CandidateMemoryPenalty)] = -1.5,
            [nameof(CandidateRerankerFormalPriorityFeatureSet.FreshnessPriority)] = 4.0,
            [nameof(CandidateRerankerFormalPriorityFeatureSet.PackagePolicyPriority)] = 2.0,
            [nameof(CandidateRerankerFormalPriorityFeatureSet.LifecyclePriority)] = 1.0
        };
    }

    private static IEnumerable<string> BuildReasons(
        double layerPriority,
        double sourcePriority,
        double currentTaskBoost,
        double constraintRelevance,
        double relationEvidenceBoost,
        double stableMemoryBias,
        double workingMemoryBias,
        double candidateMemoryPenalty,
        double freshnessPriority,
        double packagePolicyPriority,
        double lifecyclePriority)
    {
        if (layerPriority > 0) yield return nameof(CandidateRerankerFormalPriorityFeatureSet.LayerPriority);
        if (sourcePriority > 0) yield return nameof(CandidateRerankerFormalPriorityFeatureSet.SourcePriority);
        if (currentTaskBoost > 0) yield return nameof(CandidateRerankerFormalPriorityFeatureSet.CurrentTaskBoost);
        if (constraintRelevance > 0) yield return nameof(CandidateRerankerFormalPriorityFeatureSet.ConstraintRelevance);
        if (relationEvidenceBoost > 0) yield return nameof(CandidateRerankerFormalPriorityFeatureSet.RelationEvidenceBoost);
        if (stableMemoryBias > 0) yield return nameof(CandidateRerankerFormalPriorityFeatureSet.StableMemoryBias);
        if (workingMemoryBias > 0) yield return nameof(CandidateRerankerFormalPriorityFeatureSet.WorkingMemoryBias);
        if (candidateMemoryPenalty > 0) yield return nameof(CandidateRerankerFormalPriorityFeatureSet.CandidateMemoryPenalty);
        if (freshnessPriority > 0) yield return nameof(CandidateRerankerFormalPriorityFeatureSet.FreshnessPriority);
        if (packagePolicyPriority > 0) yield return nameof(CandidateRerankerFormalPriorityFeatureSet.PackagePolicyPriority);
        if (Math.Abs(lifecyclePriority) > double.Epsilon) yield return nameof(CandidateRerankerFormalPriorityFeatureSet.LifecyclePriority);
    }

    private static string MetadataSurface(LifecycleAwareRankerShadowCandidateScore candidate)
    {
        return string.Join(' ', candidate.Kind, candidate.Type, candidate.SectionName, candidate.Reason);
    }

    private static double LayerPriority(string surface)
    {
        if (ContainsSystemToken(surface, "constraint"))
        {
            return 5;
        }

        if (ContainsSystemToken(surface, "working"))
        {
            return 4;
        }

        if (ContainsSystemToken(surface, "stable"))
        {
            return 3;
        }

        if (ContainsSystemToken(surface, "conflict"))
        {
            return 2;
        }

        if (ContainsSystemToken(surface, "candidate"))
        {
            return 1;
        }

        return 0;
    }

    private static double SourcePriority(LifecycleAwareRankerShadowCandidateScore candidate)
    {
        var surface = MetadataSurface(candidate);
        if (ContainsSystemToken(surface, "constraint"))
        {
            return 4;
        }

        if (ContainsSystemToken(surface, "stable"))
        {
            return 3;
        }

        if (ContainsSystemToken(surface, "working"))
        {
            return 2;
        }

        return candidate.Selected ? 1 : 0;
    }

    private static bool ContainsSystemToken(string value, string token)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record FormalPriorityShadowProfile(
    string ProfileId,
    bool UseFormalPriority,
    bool UseAbstain,
    double AbstainMarginThreshold,
    IReadOnlyDictionary<string, double> FeatureWeights);
