using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Candidate reranker shadow 的离线审计规则；只用于报告，不参与正式排序。</summary>
public static class CandidateRerankerShadowAuditRules
{
    private const double ScoreEpsilon = 0.000001;

    public static bool IsRiskCandidate(LifecycleAwareRankerShadowCandidateScore candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.IsMustNotHit
            || candidate.LifecycleFeatures.IsRejected
            || candidate.LifecycleFeatures.IsDeprecated
            || candidate.LifecycleFeatures.IsSuperseded
            || candidate.LifecycleFeatures.IsHistorical
            || candidate.SectionName.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
            || candidate.SectionName.Contains("historical", StringComparison.OrdinalIgnoreCase)
            || candidate.SectionName.Contains("superseded", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasLifecycleMetadata(LifecycleAwareRankerShadowCandidateScore candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var features = candidate.LifecycleFeatures;
        return features.IsDeprecated
            || features.IsSuperseded
            || features.IsHistorical
            || features.IsRejected
            || features.HasReplacement
            || features.HasSupersedesRelation
            || features.IsCurrentVersion
            || features.VersionDistance > 0
            || candidate.DemotionReasons.Count > 0
            || candidate.PromotionReasons.Count > 0;
    }

    public static bool HasReplacementMetadata(LifecycleAwareRankerShadowCandidateScore candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var features = candidate.LifecycleFeatures;
        return features.HasReplacement
            || features.HasSupersedesRelation
            || features.VersionDistance > 0;
    }

    public static bool HasDeprecatedMetadata(LifecycleAwareRankerShadowCandidateScore candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var features = candidate.LifecycleFeatures;
        return features.IsDeprecated
            || features.IsSuperseded
            || features.IsHistorical
            || features.IsRejected
            || candidate.DemotionReasons.Any(static reason =>
                reason.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("historical", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("superseded", StringComparison.OrdinalIgnoreCase));
    }

    public static string ResolveEligibilityStatus(LifecycleAwareRankerShadowCandidateScore? candidate)
    {
        if (candidate is null)
        {
            return CandidateRerankerEligibilityStatuses.MetadataIncomplete;
        }

        if (IsRiskCandidate(candidate))
        {
            return CandidateRerankerEligibilityStatuses.RiskCandidateAllowed;
        }

        return HasLifecycleMetadata(candidate)
            ? CandidateRerankerEligibilityStatuses.Rankable
            : CandidateRerankerEligibilityStatuses.MetadataIncomplete;
    }

    public static string ResolveScoreDirection(IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        if (scores.Count <= 1)
        {
            return "InsufficientCandidates";
        }

        foreach (var left in scores)
        {
            foreach (var right in scores)
            {
                if (left.CandidateId.Equals(right.CandidateId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (left.LifecycleAwareScore > right.LifecycleAwareScore + ScoreEpsilon
                    && left.ShadowRank > right.ShadowRank)
                {
                    return CandidateRerankerRegressionReasons.ScoreDirectionMismatch;
                }
            }
        }

        return "HigherScoreRanksFirst";
    }

    public static string ResolveRegressionReason(CandidateRerankerShadowFailureAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.Equals(
                record.ScoreDirection,
                CandidateRerankerRegressionReasons.ScoreDirectionMismatch,
                StringComparison.OrdinalIgnoreCase))
        {
            return CandidateRerankerRegressionReasons.ScoreDirectionMismatch;
        }

        var shadowTop = record.ScoreBreakdown.FirstOrDefault(score =>
            string.Equals(score.CandidateId, record.ShadowTop1, StringComparison.OrdinalIgnoreCase));
        if (shadowTop is null)
        {
            return CandidateRerankerRegressionReasons.CandidateScopeMismatch;
        }

        if (IsRiskCandidate(shadowTop))
        {
            if (shadowTop.ScoreDelta < -ScoreEpsilon)
            {
                return CandidateRerankerRegressionReasons.RankerFeatureTooWeak;
            }

            if (HasDeprecatedMetadata(shadowTop))
            {
                return CandidateRerankerRegressionReasons.DeprecatedPenaltyNotApplied;
            }

            return CandidateRerankerRegressionReasons.RiskCandidateAllowed;
        }

        if (!record.LifecycleMetadataPresent)
        {
            return CandidateRerankerRegressionReasons.MissingFeatureMetadata;
        }

        if (record.FormalHit && !record.ShadowHit)
        {
            return CandidateRerankerRegressionReasons.PairwiseToListwiseMismatch;
        }

        if (record.FormalCandidateRank > 0
            && record.ShadowCandidateRank > 0
            && record.ShadowCandidateRank > record.FormalCandidateRank)
        {
            return CandidateRerankerRegressionReasons.ScoreScaleMismatch;
        }

        return CandidateRerankerRegressionReasons.RequiresFeatureTuning;
    }

    public static string ResolveRecommendedAction(string regressionReason)
    {
        return regressionReason switch
        {
            CandidateRerankerRegressionReasons.ScoreDirectionMismatch =>
                "Audit shadow sorting direction before any opt-in.",
            CandidateRerankerRegressionReasons.ScoreScaleMismatch =>
                "Normalize score scale before comparing listwise ranks.",
            CandidateRerankerRegressionReasons.MissingFeatureMetadata =>
                "Align lifecycle and replacement metadata before weight tuning.",
            CandidateRerankerRegressionReasons.LifecyclePenaltyNotApplied =>
                "Require explicit lifecycle penalty signal before shadow promotion.",
            CandidateRerankerRegressionReasons.DeprecatedPenaltyNotApplied =>
                "Require deprecated or historical demotion before safe improvement.",
            CandidateRerankerRegressionReasons.RiskCandidateAllowed =>
                "Keep blocked and add eligibility guard before opt-in.",
            CandidateRerankerRegressionReasons.RankerFeatureTooWeak =>
                "Tune lifecycle-aware feature strength offline; do not opt in.",
            CandidateRerankerRegressionReasons.PairwiseToListwiseMismatch =>
                "Audit pairwise baseline assumptions against listwise topK behavior.",
            CandidateRerankerRegressionReasons.CandidateScopeMismatch =>
                "Align formal and shadow candidate scope before comparison.",
            CandidateRerankerRegressionReasons.ComparisonMetricMismatch =>
                "Review metric definition before interpreting gains.",
            _ => "Keep shadow-only and collect targeted hard negatives."
        };
    }

    public static string ResolveScoreContractStatus(
        IReadOnlyList<CandidateRerankerShadowFailureAuditRecord> regressions,
        int riskCandidateInShadowTopK)
    {
        ArgumentNullException.ThrowIfNull(regressions);
        if (regressions.Any(static item =>
                string.Equals(
                    item.RegressionReason,
                    CandidateRerankerRegressionReasons.ScoreDirectionMismatch,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return CandidateRerankerScoreContractStatuses.Failed;
        }

        return regressions.Count > 0 || riskCandidateInShadowTopK > 0
            ? CandidateRerankerScoreContractStatuses.NeedsAudit
            : CandidateRerankerScoreContractStatuses.Passed;
    }
}
