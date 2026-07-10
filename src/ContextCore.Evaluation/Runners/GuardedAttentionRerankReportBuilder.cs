using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>Builds an eval-level summary for guarded attention rerank experiments.</summary>
public static class GuardedAttentionRerankReportBuilder
{
    public static GuardedAttentionRerankEvalReport Build(
        ContextEvalReport evalReport,
        string mode = "SelectedSetPreserving",
        string profileId = "old-score-anchored-v1")
    {
        ArgumentNullException.ThrowIfNull(evalReport);

        var samples = evalReport.Results
            .Select(result => ToSample(result))
            .ToArray();
        var selectedSetChangeCount = samples.Sum(sample => sample.SelectedSetChangeCount);

        return new GuardedAttentionRerankEvalReport
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Mode = mode,
            ProfileId = profileId,
            TotalSamples = samples.Length,
            AppliedSamples = samples.Count(sample => sample.Applied),
            SkippedSamples = samples.Count(sample => sample.Skipped),
            BlockedSamples = samples.Count(sample => sample.Blocked),
            AddedItems = samples.Sum(sample => sample.AddedItems),
            DroppedItems = samples.Sum(sample => sample.DroppedItems),
            OrderChanges = samples.Sum(sample => sample.OrderChanges),
            SectionChanges = samples.Sum(sample => sample.SectionChanges),
            MustHitRankDeltaCount = samples.Sum(sample => sample.MustHitRankDeltaCount),
            MustNotHitRankDeltaCount = samples.Sum(sample => sample.MustNotHitRankDeltaCount),
            SelectedSetChangeCount = selectedSetChangeCount,
            SelectedSetChangeRatio = samples.Length == 0
                ? 0d
                : samples.Average(sample => sample.SelectedSetChangeRatio),
            BlockedReasons = CountReasons(samples.Select(sample => sample.BlockedReason)),
            SkippedReasons = CountReasons(samples.Select(sample => sample.SkippedReason)),
            Samples = samples
        };
    }

    private static GuardedAttentionRerankEvalSample ToSample(ContextEvalResult result)
    {
        var report = result.AttentionRerankComparison;
        return new GuardedAttentionRerankEvalSample
        {
            SampleId = result.SampleId,
            Mode = result.Mode,
            Succeeded = result.Succeeded,
            Applied = report.Applied,
            Skipped = report.Skipped,
            Blocked = report.Blocked,
            SkippedReason = report.SkippedReason,
            BlockedReason = report.BlockedReason,
            AddedItems = report.AddedItems.Count,
            DroppedItems = report.DroppedItems.Count,
            OrderChanges = report.OrderChanges.Count,
            SectionChanges = report.SectionChanges.Count,
            MustHitRankDeltaCount = report.MustHitRankDeltas.Count,
            MustNotHitRankDeltaCount = report.MustNotHitRankDeltas.Count,
            SelectedSetChangeCount = report.SelectedSetChangeCount,
            SelectedSetChangeRatio = report.SelectedSetChangeRatio,
            TopOrderChanges = report.OrderChanges
                .OrderByDescending(change => Math.Abs(change.RankDelta))
                .ThenBy(change => change.RerankedRank)
                .Take(10)
                .ToArray()
        };
    }

    private static Dictionary<string, int> CountReasons(IEnumerable<string> reasons)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var reason in reasons.Where(reason => !string.IsNullOrWhiteSpace(reason)))
        {
            counts[reason] = counts.TryGetValue(reason, out var count) ? count + 1 : 1;
        }

        return counts;
    }
}
