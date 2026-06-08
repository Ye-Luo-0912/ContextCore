using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.ControlRoom.Services;

/// <summary>Builds lifecycle-aware ranker shadow reports from immutable eval diagnostics.</summary>
public static class LifecycleAwareRankerShadowReportBuilder
{
    public static LifecycleAwareRankerShadowReport Build(
        ContextEvalReport evalReport,
        bool includeSeedBatches,
        string profile = LifecycleAwareRankerShadowScorer.DefaultProfile)
    {
        ArgumentNullException.ThrowIfNull(evalReport);

        var scorer = new LifecycleAwareRankerShadowScorer();
        var samples = evalReport.Results
            .Select(result => BuildSample(result, scorer, profile))
            .ToArray();

        return new LifecycleAwareRankerShadowReport
        {
            OperationId = $"lifecycle-ranker-shadow-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            Profile = string.IsNullOrWhiteSpace(profile)
                ? LifecycleAwareRankerShadowScorer.DefaultProfile
                : profile,
            TotalSamples = samples.Length,
            IncludeSeedBatches = includeSeedBatches,
            FormalOutputChanged = samples.Count(static sample => sample.FormalOutputChanged),
            SelectedSetChanged = samples.Count(static sample => sample.SelectedSetChanged),
            DeprecatedNoiseDemotedCount = samples.Sum(static sample => sample.DeprecatedNoiseDemotedCount),
            VersionConflictFixedCount = samples.Sum(static sample => sample.VersionConflictFixedCount),
            MustHitDemotedCount = samples.Sum(static sample => sample.MustHitDemotedCount),
            MustNotHitPromotedCount = samples.Sum(static sample => sample.MustNotHitPromotedCount),
            LifecycleViolationCount = samples.Sum(static sample => sample.LifecycleViolationCount),
            PotentialMRRDelta = Average(samples.Select(static sample => sample.PotentialMRRDelta)),
            PotentialPairwiseWinRate = Average(samples.Select(static sample => sample.PotentialPairwiseWinRate)),
            Samples = samples,
            PolicyVersion = LifecycleAwareRankerShadowScorer.PolicyVersion
        };
    }

    private static LifecycleAwareRankerShadowSample BuildSample(
        ContextEvalResult result,
        LifecycleAwareRankerShadowScorer scorer,
        string profile)
    {
        var selectedDiagnostics = ResolveSelectedDiagnostics(result);
        var droppedDiagnostics = ResolveDroppedDiagnostics(result, selectedDiagnostics.Count);
        var trace = scorer.Score(
            selectedDiagnostics,
            droppedDiagnostics,
            new LifecycleAwareRankerShadowOptions
            {
                Enabled = true,
                Profile = profile
            });
        var legacySelected = result.SelectedIds.Count > 0
            ? result.SelectedIds.ToArray()
            : selectedDiagnostics.Select(static item => item.ItemId).ToArray();
        var shadowSelected = legacySelected.ToArray();
        var shadowPotentialMrr = ComputeShadowPotentialMrr(trace, legacySelected);
        var legacyMrr = result.RetrievalMrrAnyMustHit;
        var pairwiseWinRate = ComputePotentialPairwiseWinRate(trace);

        return new LifecycleAwareRankerShadowSample
        {
            SampleId = result.SampleId,
            Mode = result.Mode,
            FormalOutputChanged = false,
            SelectedSetChanged = false,
            LegacySelectedIds = legacySelected,
            ShadowSelectedIds = shadowSelected,
            DeprecatedNoiseDemotedCount = trace.DeprecatedDemotions.Count,
            VersionConflictFixedCount = trace.VersionConflictFixes.Count,
            MustHitDemotedCount = trace.MustHitDemotions.Count,
            MustNotHitPromotedCount = trace.MustNotHitPromotions.Count,
            LifecycleViolationCount = CountLifecycleViolations(trace),
            LegacyMRR = legacyMrr,
            ShadowPotentialMRR = shadowPotentialMrr,
            PotentialMRRDelta = shadowPotentialMrr - legacyMrr,
            PotentialPairwiseWinRate = pairwiseWinRate,
            Trace = trace
        };
    }

    private static IReadOnlyList<ContextEvalItemDiagnostic> ResolveSelectedDiagnostics(ContextEvalResult result)
    {
        if (result.SelectedItemDiagnostics.Count > 0)
        {
            return result.SelectedItemDiagnostics
                .Select((item, index) => EnsureRank(item, index + 1))
                .ToArray();
        }

        return result.SelectedIds
            .Select((id, index) => new ContextEvalItemDiagnostic
            {
                ItemId = id,
                Rank = index + 1,
                IsMustHit = result.MustHit.Any(mustHit => IsId(mustHit, id)),
                IsMustNotHit = result.MustNotHit.Any(mustNotHit => IsId(mustNotHit, id))
            })
            .ToArray();
    }

    private static IReadOnlyList<ContextEvalItemDiagnostic> ResolveDroppedDiagnostics(
        ContextEvalResult result,
        int selectedCount)
    {
        if (result.DroppedItemDiagnostics.Count > 0)
        {
            return result.DroppedItemDiagnostics
                .Select((item, index) => EnsureRank(item, selectedCount + index + 1))
                .ToArray();
        }

        return result.ExcludedIds
            .Select((id, index) => new ContextEvalItemDiagnostic
            {
                ItemId = id,
                Rank = selectedCount + index + 1,
                IsMustHit = result.MustHit.Any(mustHit => IsId(mustHit, id)),
                IsMustNotHit = result.MustNotHit.Any(mustNotHit => IsId(mustNotHit, id))
            })
            .ToArray();
    }

    private static ContextEvalItemDiagnostic EnsureRank(ContextEvalItemDiagnostic item, int fallbackRank)
    {
        if (item.Rank > 0)
        {
            return item;
        }

        return new ContextEvalItemDiagnostic
        {
            ItemId = item.ItemId,
            Kind = item.Kind,
            Type = item.Type,
            SectionName = item.SectionName,
            Reason = item.Reason,
            Score = item.Score,
            EstimatedTokens = item.EstimatedTokens,
            Rank = fallbackRank,
            IsMustHit = item.IsMustHit,
            IsMustNotHit = item.IsMustNotHit,
            SourceRefs = item.SourceRefs
        };
    }

    private static double ComputeShadowPotentialMrr(
        LifecycleAwareRankerShadowTrace trace,
        IReadOnlyList<string> selectedIds)
    {
        var selectedSet = selectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedScores = trace.CandidateShadowScores
            .Where(candidate => selectedSet.Contains(candidate.CandidateId))
            .OrderByDescending(static candidate => candidate.LifecycleAwareScore)
            .ThenBy(static candidate => candidate.LegacyRank)
            .ThenBy(static candidate => candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var bestMustHitRank = selectedScores
            .Select((candidate, index) => new { candidate.IsMustHit, Rank = index + 1 })
            .Where(static item => item.IsMustHit)
            .Select(static item => item.Rank)
            .DefaultIfEmpty(0)
            .Min();
        return bestMustHitRank <= 0 ? 0 : 1.0 / bestMustHitRank;
    }

    private static double ComputePotentialPairwiseWinRate(LifecycleAwareRankerShadowTrace trace)
    {
        var positives = trace.CandidateShadowScores
            .Where(static candidate => candidate.IsMustHit)
            .ToArray();
        var negatives = trace.CandidateShadowScores
            .Where(static candidate => candidate.IsMustNotHit)
            .ToArray();
        if (positives.Length == 0 || negatives.Length == 0)
        {
            return 1;
        }

        var total = 0;
        var wins = 0;
        foreach (var positive in positives)
        {
            foreach (var negative in negatives)
            {
                total++;
                if (positive.LifecycleAwareScore > negative.LifecycleAwareScore)
                {
                    wins++;
                }
            }
        }

        return total == 0 ? 1 : wins / (double)total;
    }

    private static int CountLifecycleViolations(LifecycleAwareRankerShadowTrace trace)
    {
        return trace.CandidateShadowScores
            .Count(static candidate => candidate.Selected
                && (candidate.IsMustNotHit || candidate.LifecycleFeatures.IsRejected));
    }

    private static bool IsId(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return actual.Contains(expected, StringComparison.OrdinalIgnoreCase)
            || expected.Contains(actual, StringComparison.OrdinalIgnoreCase);
    }

    private static double Average(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? 0 : materialized.Average();
    }
}
