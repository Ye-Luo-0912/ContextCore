using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Candidate reranker shadow 前置 eligibility guard；只用于离线 shadow，不改变正式排序。</summary>
public sealed class RankerCandidateEligibilityGuard
{
    private const double MinimumCompleteness = 0.75;

    public IReadOnlyList<RankerCandidateEligibilityDecision> Evaluate(
        IReadOnlyList<CandidateFeatureEnvelope> envelopes)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        return envelopes.Select(EvaluateOne).ToArray();
    }

    public RankerCandidateEligibilityDecision EvaluateOne(CandidateFeatureEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var reasons = new List<string>();
        reasons.AddRange(envelope.Diagnostics);

        if (envelope.FeatureCompleteness < MinimumCompleteness)
        {
            AddReason(reasons, CandidateRerankerBlockedReasons.IncompleteFeatureEnvelope);
        }

        if (string.Equals(envelope.Lifecycle, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            AddReason(reasons, CandidateRerankerBlockedReasons.MissingLifecycleMetadata);
        }

        if (envelope.IsDeprecated)
        {
            AddReason(reasons, CandidateRerankerBlockedReasons.DeprecatedCandidateBlocked);
            AddReason(reasons, CandidateRerankerBlockedReasons.RiskCandidateBlocked);
            return Create(envelope, CandidateRerankerEligibilityStatuses.AuditOnly, reasons);
        }

        if (envelope.IsHistorical)
        {
            AddReason(reasons, CandidateRerankerBlockedReasons.HistoricalCandidateBlocked);
            AddReason(reasons, CandidateRerankerBlockedReasons.RiskCandidateBlocked);
            return Create(envelope, CandidateRerankerEligibilityStatuses.AuditOnly, reasons);
        }

        if (envelope.IsSuperseded)
        {
            AddReason(reasons, CandidateRerankerBlockedReasons.SupersededCandidateBlocked);
            AddReason(reasons, CandidateRerankerBlockedReasons.RiskCandidateBlocked);
            if (!envelope.HasActiveReplacement)
            {
                AddReason(reasons, CandidateRerankerBlockedReasons.MissingReplacementMetadata);
            }

            return Create(envelope, CandidateRerankerEligibilityStatuses.Blocked, reasons);
        }

        if (reasons.Contains(CandidateRerankerBlockedReasons.MissingLifecycleMetadata, StringComparer.OrdinalIgnoreCase)
            || reasons.Contains(CandidateRerankerBlockedReasons.MissingReviewStatus, StringComparer.OrdinalIgnoreCase)
            || reasons.Contains(CandidateRerankerBlockedReasons.IncompleteFeatureEnvelope, StringComparer.OrdinalIgnoreCase))
        {
            return Create(envelope, CandidateRerankerEligibilityStatuses.Blocked, reasons);
        }

        if (reasons.Contains(CandidateRerankerBlockedReasons.MissingProvenance, StringComparer.OrdinalIgnoreCase))
        {
            return Create(envelope, CandidateRerankerEligibilityStatuses.DiagnosticsOnly, reasons);
        }

        return Create(envelope, CandidateRerankerEligibilityStatuses.Rankable, reasons);
    }

    public static bool IsRankable(RankerCandidateEligibilityDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return string.Equals(decision.Status, CandidateRerankerEligibilityStatuses.Rankable, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNonRankableRisk(RankerCandidateEligibilityDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return !IsRankable(decision)
            && (decision.Envelope.IsDeprecated
                || decision.Envelope.IsHistorical
                || decision.Envelope.IsSuperseded
                || decision.BlockedReasons.Contains(CandidateRerankerBlockedReasons.RiskCandidateBlocked, StringComparer.OrdinalIgnoreCase));
    }

    public static string ResolveGuardStatus(IReadOnlyList<RankerCandidateEligibilityDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        if (decisions.Count == 0)
        {
            return CandidateRerankerEligibilityGuardStatuses.Unknown;
        }

        return decisions.Any(IsNonRankableRisk)
            ? CandidateRerankerEligibilityGuardStatuses.Guarded
            : CandidateRerankerEligibilityGuardStatuses.Passed;
    }

    private static RankerCandidateEligibilityDecision Create(
        CandidateFeatureEnvelope envelope,
        string status,
        IReadOnlyList<string> reasons)
    {
        return new RankerCandidateEligibilityDecision
        {
            CandidateId = envelope.CandidateId,
            Status = status,
            BlockedReasons = reasons
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Envelope = envelope
        };
    }

    private static void AddReason(List<string> reasons, string reason)
    {
        if (!reasons.Contains(reason, StringComparer.OrdinalIgnoreCase))
        {
            reasons.Add(reason);
        }
    }
}
