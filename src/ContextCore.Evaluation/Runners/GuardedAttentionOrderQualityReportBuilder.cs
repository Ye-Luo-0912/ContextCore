using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>Builds selected-order quality reports for guarded attention rerank experiments.</summary>
public static class GuardedAttentionOrderQualityReportBuilder
{
    private const double Epsilon = 0.0001;
    private const double RankTolerance = 0.25;

    public static GuardedAttentionOrderQualityReport Build(
        ContextEvalReport evalReport,
        string mode = "SelectedSetPreserving",
        string profileId = "old-score-anchored-v1")
    {
        ArgumentNullException.ThrowIfNull(evalReport);

        var samples = evalReport.Results
            .Select(ToSample)
            .ToArray();
        var baseline = Average(samples.Select(sample => sample.Baseline));
        var reranked = Average(samples.Select(sample => sample.Reranked));
        var delta = Delta(baseline, reranked);
        var selectedSetDiffCount = samples.Sum(sample => sample.SelectedSetDiffCount);
        var addedItems = samples.Sum(sample => sample.AddedItems);
        var droppedItems = samples.Sum(sample => sample.DroppedItems);
        var lifecycleViolationCount = samples.Sum(sample => sample.LifecycleViolationCount);
        var hardConstraintMissingCount = samples.Sum(sample => sample.HardConstraintMissingCount);

        return new GuardedAttentionOrderQualityReport
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Mode = mode,
            ProfileId = profileId,
            TotalSamples = samples.Length,
            AppliedSamples = samples.Count(sample => sample.Applied),
            SkippedSamples = samples.Count(sample => sample.Skipped),
            BlockedSamples = samples.Count(sample => sample.Blocked),
            SelectedSetDiffCount = selectedSetDiffCount,
            AddedItems = addedItems,
            DroppedItems = droppedItems,
            LifecycleViolationCount = lifecycleViolationCount,
            HardConstraintMissingCount = hardConstraintMissingCount,
            Baseline = baseline,
            Reranked = reranked,
            Delta = delta,
            SafetyGates = BuildSafetyGates(
                selectedSetDiffCount,
                addedItems,
                droppedItems,
                lifecycleViolationCount,
                hardConstraintMissingCount),
            SortingGates = BuildSortingGates(baseline, reranked),
            Samples = samples
        };
    }

    private static GuardedAttentionOrderQualitySample ToSample(ContextEvalResult result)
    {
        var report = result.AttentionRerankComparison;
        var oldOrder = report.OldSelectedOrder.Count > 0
            ? report.OldSelectedOrder
            : BuildFallbackOrder(result.SelectedIds);
        var effectiveNewOrder = report.Applied && report.NewSelectedOrder.Count > 0
            ? report.NewSelectedOrder
            : oldOrder;
        var effectiveChanges = report.Applied
            ? report.OrderChanges
            : Array.Empty<AttentionRerankItemChange>();
        var baseline = CalculateMetrics(oldOrder, Array.Empty<AttentionRerankItemChange>());
        var reranked = CalculateMetrics(effectiveNewOrder, effectiveChanges);
        var selectedSetDiffCount = CountSelectedSetDiff(oldOrder, effectiveNewOrder);
        var lifecycleViolationCount = report.Applied
            ? report.MovedUpItems.Count(item => item.IsLifecycleRisk)
            : 0;
        var hardConstraintMissingCount = CountHardConstraintMissing(oldOrder, effectiveNewOrder);

        return new GuardedAttentionOrderQualitySample
        {
            SampleId = result.SampleId,
            Mode = result.Mode,
            Succeeded = result.Succeeded,
            Applied = report.Applied,
            Skipped = report.Skipped,
            Blocked = report.Blocked,
            SkippedReason = report.SkippedReason,
            BlockedReason = report.BlockedReason,
            Baseline = baseline,
            Reranked = reranked,
            Delta = Delta(baseline, reranked),
            SelectedSetDiffCount = selectedSetDiffCount,
            AddedItems = report.AddedItems.Count,
            DroppedItems = report.DroppedItems.Count,
            LifecycleViolationCount = lifecycleViolationCount,
            HardConstraintMissingCount = hardConstraintMissingCount,
            OldSelectedOrder = oldOrder,
            NewSelectedOrder = effectiveNewOrder,
            MovedUpItems = report.Applied ? report.MovedUpItems : Array.Empty<AttentionRerankItemChange>(),
            MovedDownItems = report.Applied ? report.MovedDownItems : Array.Empty<AttentionRerankItemChange>()
        };
    }

    private static SelectedOrderQualityMetrics CalculateMetrics(
        IReadOnlyList<AttentionRerankOrderItem> order,
        IReadOnlyList<AttentionRerankItemChange> changes)
    {
        var mustHitRanks = order
            .Where(item => item.IsMustHit)
            .Select(item => item.Rank)
            .Order()
            .ToArray();
        var constraintRanks = order
            .Where(item => item.IsConstraint || item.IsHardConstraint)
            .Select(item => item.Rank)
            .ToArray();
        var lifecycleRiskRanks = order
            .Where(item => item.IsLifecycleRisk)
            .Select(item => item.Rank)
            .ToArray();

        return new SelectedOrderQualityMetrics
        {
            SelectedOrderMRR = mustHitRanks.Length == 0 ? 0d : 1d / mustHitRanks[0],
            FirstMustHitSelectedRank = mustHitRanks.Length == 0 ? 0d : mustHitRanks[0],
            MustHitAverageSelectedRank = AverageRanks(mustHitRanks),
            ConstraintAverageRank = AverageRanks(constraintRanks),
            LifecycleRiskAverageRank = AverageRanks(lifecycleRiskRanks),
            AttentionOrderDelta = changes.Count == 0 ? 0d : changes.Average(change => Math.Abs(change.RankDelta)),
            MovedUpMustHitCount = changes.Count(change => change.IsMustHit && change.RankDelta > 0),
            MovedDownMustHitCount = changes.Count(change => change.IsMustHit && change.RankDelta < 0)
        };
    }

    private static IReadOnlyList<SelectedOrderQualityGateResult> BuildSafetyGates(
        int selectedSetDiffCount,
        int addedItems,
        int droppedItems,
        int lifecycleViolationCount,
        int hardConstraintMissingCount)
    {
        return
        [
            Gate("selected_set_diff_zero", selectedSetDiffCount, 0, selectedSetDiffCount == 0, "Selected set diff must remain 0."),
            Gate("added_items_zero", addedItems, 0, addedItems == 0, "Guarded rerank must not add selected items."),
            Gate("dropped_items_zero", droppedItems, 0, droppedItems == 0, "Guarded rerank must not drop selected items."),
            Gate("lifecycle_violation_zero", lifecycleViolationCount, 0, lifecycleViolationCount == 0, "Lifecycle-risk items must not be promoted by applied rerank."),
            Gate("hard_constraint_missing_zero", hardConstraintMissingCount, 0, hardConstraintMissingCount == 0, "Hard constraints must remain selected.")
        ];
    }

    private static IReadOnlyList<SelectedOrderQualityGateResult> BuildSortingGates(
        SelectedOrderQualityMetrics baseline,
        SelectedOrderQualityMetrics reranked)
    {
        var firstMustHitDelta = ResolveRankDelta(baseline.FirstMustHitSelectedRank, reranked.FirstMustHitSelectedRank);
        var constraintDelta = ResolveRankDelta(baseline.ConstraintAverageRank, reranked.ConstraintAverageRank);
        var lifecyclePromotedDelta = ResolveLifecyclePromotionDelta(baseline.LifecycleRiskAverageRank, reranked.LifecycleRiskAverageRank);
        var mustHitAverageDelta = ResolveRankDelta(baseline.MustHitAverageSelectedRank, reranked.MustHitAverageSelectedRank);

        return
        [
            Gate(
                "selected_order_mrr_not_lower",
                reranked.SelectedOrderMRR - baseline.SelectedOrderMRR,
                0,
                reranked.SelectedOrderMRR + Epsilon >= baseline.SelectedOrderMRR,
                "SelectedOrderMRR must not be lower than baseline."),
            Gate(
                "first_must_hit_rank_not_clearly_worse",
                firstMustHitDelta,
                RankTolerance,
                firstMustHitDelta <= RankTolerance + Epsilon,
                "FirstMustHitSelectedRank may not regress materially."),
            Gate(
                "constraint_average_rank_not_worse",
                constraintDelta,
                0,
                constraintDelta <= Epsilon,
                "ConstraintAverageRank must not regress."),
            Gate(
                "lifecycle_risk_not_promoted",
                lifecyclePromotedDelta,
                0,
                lifecyclePromotedDelta <= Epsilon,
                "LifecycleRiskAverageRank must not move earlier."),
            Gate(
                "must_hit_average_rank_not_worse",
                mustHitAverageDelta,
                0,
                mustHitAverageDelta <= Epsilon,
                "MustHitAverageSelectedRank must not regress.")
        ];
    }

    private static SelectedOrderQualityMetrics Average(IEnumerable<SelectedOrderQualityMetrics> values)
    {
        var items = values.ToArray();
        if (items.Length == 0)
        {
            return new SelectedOrderQualityMetrics();
        }

        return new SelectedOrderQualityMetrics
        {
            SelectedOrderMRR = items.Average(item => item.SelectedOrderMRR),
            FirstMustHitSelectedRank = items.Average(item => item.FirstMustHitSelectedRank),
            MustHitAverageSelectedRank = items.Average(item => item.MustHitAverageSelectedRank),
            ConstraintAverageRank = items.Average(item => item.ConstraintAverageRank),
            LifecycleRiskAverageRank = items.Average(item => item.LifecycleRiskAverageRank),
            AttentionOrderDelta = items.Average(item => item.AttentionOrderDelta),
            MovedUpMustHitCount = items.Sum(item => item.MovedUpMustHitCount),
            MovedDownMustHitCount = items.Sum(item => item.MovedDownMustHitCount)
        };
    }

    private static SelectedOrderQualityMetrics Delta(
        SelectedOrderQualityMetrics baseline,
        SelectedOrderQualityMetrics reranked)
    {
        return new SelectedOrderQualityMetrics
        {
            SelectedOrderMRR = reranked.SelectedOrderMRR - baseline.SelectedOrderMRR,
            FirstMustHitSelectedRank = reranked.FirstMustHitSelectedRank - baseline.FirstMustHitSelectedRank,
            MustHitAverageSelectedRank = reranked.MustHitAverageSelectedRank - baseline.MustHitAverageSelectedRank,
            ConstraintAverageRank = reranked.ConstraintAverageRank - baseline.ConstraintAverageRank,
            LifecycleRiskAverageRank = reranked.LifecycleRiskAverageRank - baseline.LifecycleRiskAverageRank,
            AttentionOrderDelta = reranked.AttentionOrderDelta - baseline.AttentionOrderDelta,
            MovedUpMustHitCount = reranked.MovedUpMustHitCount - baseline.MovedUpMustHitCount,
            MovedDownMustHitCount = reranked.MovedDownMustHitCount - baseline.MovedDownMustHitCount
        };
    }

    private static double ResolveRankDelta(double baselineRank, double rerankedRank)
    {
        if (baselineRank <= 0 || rerankedRank <= 0)
        {
            return 0d;
        }

        return rerankedRank - baselineRank;
    }

    private static double ResolveLifecyclePromotionDelta(double baselineRank, double rerankedRank)
    {
        if (baselineRank <= 0 || rerankedRank <= 0)
        {
            return 0d;
        }

        return baselineRank - rerankedRank;
    }

    private static double AverageRanks(IReadOnlyList<int> ranks)
    {
        return ranks.Count == 0 ? 0d : ranks.Average();
    }

    private static int CountSelectedSetDiff(
        IReadOnlyList<AttentionRerankOrderItem> oldOrder,
        IReadOnlyList<AttentionRerankOrderItem> newOrder)
    {
        var oldIds = oldOrder.Select(item => item.CandidateId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newIds = newOrder.Select(item => item.CandidateId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return oldIds
            .Union(newIds, StringComparer.OrdinalIgnoreCase)
            .Count(candidateId => oldIds.Contains(candidateId) != newIds.Contains(candidateId));
    }

    private static int CountHardConstraintMissing(
        IReadOnlyList<AttentionRerankOrderItem> oldOrder,
        IReadOnlyList<AttentionRerankOrderItem> newOrder)
    {
        var newIds = newOrder.Select(item => item.CandidateId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return oldOrder.Count(item => item.IsHardConstraint && !newIds.Contains(item.CandidateId));
    }

    private static IReadOnlyList<AttentionRerankOrderItem> BuildFallbackOrder(IReadOnlyList<string> selectedIds)
    {
        return selectedIds
            .Select((id, index) => new AttentionRerankOrderItem
            {
                CandidateId = id,
                SourceId = id,
                Rank = index + 1
            })
            .ToArray();
    }

    private static SelectedOrderQualityGateResult Gate(
        string name,
        double actual,
        double threshold,
        bool passed,
        string message)
    {
        return new SelectedOrderQualityGateResult
        {
            Name = name,
            Passed = passed,
            Actual = actual,
            Threshold = threshold,
            Message = message
        };
    }
}
