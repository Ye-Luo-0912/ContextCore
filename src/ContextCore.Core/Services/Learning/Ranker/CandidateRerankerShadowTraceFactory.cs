using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>把 lifecycle-aware ranker trace 投影为 candidate reranker shadow 视图。</summary>
public static class CandidateRerankerShadowTraceFactory
{
    public static CandidateRerankerShadowTrace Build(
        string requestId,
        string mode,
        string intent,
        string queryText,
        LifecycleAwareRankerShadowTrace source,
        int recordTopK)
    {
        ArgumentNullException.ThrowIfNull(source);

        var topK = recordTopK > 0 ? recordTopK : 10;
        var formalTopScores = source.CandidateShadowScores
            .OrderBy(static item => item.LegacyRank)
            .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToArray();
        var shadowTopScores = source.CandidateShadowScores
            .OrderByDescending(static item => item.LifecycleAwareScore)
            .ThenBy(static item => item.LegacyRank)
            .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToArray();
        var formalTop = formalTopScores
            .Select(static item => ToCandidateRef(item, item.LegacyRank, item.LegacyScore))
            .ToArray();
        var shadowTop = shadowTopScores
            .Select((item, index) => ToCandidateRef(item, index + 1, item.LifecycleAwareScore))
            .ToArray();

        return new CandidateRerankerShadowTrace
        {
            RequestId = requestId,
            Mode = mode,
            Intent = intent,
            QueryText = queryText,
            CandidateCount = source.CandidateShadowScores.Count,
            FormalTopCandidates = formalTop,
            ShadowTopCandidates = shadowTop,
            WouldChangeTop1 = !SameCandidate(formalTop.FirstOrDefault(), shadowTop.FirstOrDefault()),
            WouldChangeTopK = !SameTopK(formalTop, shadowTop),
            LifecycleRiskCount = shadowTopScores.Count(IsLifecycleRisk),
            DeprecatedCandidateCount = shadowTopScores.Count(IsDeprecatedRisk),
            MustNotRiskCount = shadowTop.Count(static item => item.IsMustNotHit),
            ScoreBreakdown = source.CandidateShadowScores,
            FormalOutputChanged = false
        };
    }

    private static CandidateRerankerShadowCandidateRef ToCandidateRef(
        LifecycleAwareRankerShadowCandidateScore item,
        int rank,
        double score)
    {
        return new CandidateRerankerShadowCandidateRef
        {
            CandidateId = item.CandidateId,
            Rank = rank,
            Score = score,
            Selected = item.Selected,
            IsMustHit = item.IsMustHit,
            IsMustNotHit = item.IsMustNotHit,
            SectionName = item.SectionName
        };
    }

    private static bool SameTopK(
        IReadOnlyList<CandidateRerankerShadowCandidateRef> formalTop,
        IReadOnlyList<CandidateRerankerShadowCandidateRef> shadowTop)
    {
        if (formalTop.Count != shadowTop.Count)
        {
            return false;
        }

        for (var index = 0; index < formalTop.Count; index++)
        {
            if (!SameCandidate(formalTop[index], shadowTop[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameCandidate(
        CandidateRerankerShadowCandidateRef? formal,
        CandidateRerankerShadowCandidateRef? shadow)
    {
        return string.Equals(formal?.CandidateId, shadow?.CandidateId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLifecycleRisk(LifecycleAwareRankerShadowCandidateScore item)
    {
        return item.IsMustNotHit || item.LifecycleFeatures.IsRejected;
    }

    private static bool IsDeprecatedRisk(LifecycleAwareRankerShadowCandidateScore item)
    {
        return item.LifecycleFeatures.IsDeprecated
            || item.LifecycleFeatures.IsHistorical
            || item.LifecycleFeatures.IsSuperseded
            || item.SectionName.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
            || item.SectionName.Contains("historical", StringComparison.OrdinalIgnoreCase)
            || item.SectionName.Contains("superseded", StringComparison.OrdinalIgnoreCase);
    }
}
