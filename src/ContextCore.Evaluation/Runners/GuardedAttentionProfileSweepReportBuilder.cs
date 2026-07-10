using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>Builds per-profile selected-order summaries for guarded attention weight sweeps.</summary>
public static class GuardedAttentionProfileSweepReportBuilder
{
    private static readonly string[] SweepWeightKeys =
    [
        "oldScoreAnchorWeight",
        "mustHitBoost",
        "constraintBoost",
        "shortTermBoost",
        "lifecycleRiskPenalty",
        "relationEvidenceBoost",
        "recencyBoost"
    ];

    public static GuardedAttentionProfileSweepReport Build(
        IEnumerable<(ContextAttentionProfile Profile, GuardedAttentionOrderQualityReport OrderReport)> entries,
        string mode = "SelectedSetPreserving",
        bool includeSeedBatches = false)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var profiles = entries
            .Select(entry => ToProfile(entry.Profile, entry.OrderReport))
            .ToArray();

        return new GuardedAttentionProfileSweepReport
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Mode = mode,
            TotalSamples = profiles.Length == 0 ? 0 : profiles.Max(profile => profile.TotalSamples),
            IncludeSeedBatches = includeSeedBatches,
            Profiles = profiles
        };
    }

    private static GuardedAttentionProfileSweepProfile ToProfile(
        ContextAttentionProfile profile,
        GuardedAttentionOrderQualityReport orderReport)
    {
        return new GuardedAttentionProfileSweepProfile
        {
            ProfileId = profile.ProfileId,
            PolicyVersion = profile.PolicyVersion,
            Weights = BuildSweepWeights(profile),
            TotalSamples = orderReport.TotalSamples,
            AppliedSamples = orderReport.AppliedSamples,
            SkippedSamples = orderReport.SkippedSamples,
            BlockedSamples = orderReport.BlockedSamples,
            SelectedSetDiffCount = orderReport.SelectedSetDiffCount,
            AddedItems = orderReport.AddedItems,
            DroppedItems = orderReport.DroppedItems,
            LifecycleViolationCount = orderReport.LifecycleViolationCount,
            HardConstraintMissingCount = orderReport.HardConstraintMissingCount,
            SelectedOrderMRR = orderReport.Reranked.SelectedOrderMRR,
            FirstMustHitSelectedRank = orderReport.Reranked.FirstMustHitSelectedRank,
            MustHitAverageSelectedRank = orderReport.Reranked.MustHitAverageSelectedRank,
            ConstraintAverageRank = orderReport.Reranked.ConstraintAverageRank,
            LifecycleRiskAverageRank = orderReport.Reranked.LifecycleRiskAverageRank,
            AttentionOrderDelta = orderReport.Reranked.AttentionOrderDelta,
            MovedUpMustHitCount = orderReport.Reranked.MovedUpMustHitCount,
            MovedDownMustHitCount = orderReport.Reranked.MovedDownMustHitCount,
            SafetyGatePassed = orderReport.SafetyGates.All(gate => gate.Passed),
            SortingGatePassed = orderReport.SortingGates.All(gate => gate.Passed)
        };
    }

    private static Dictionary<string, double> BuildSweepWeights(ContextAttentionProfile profile)
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in SweepWeightKeys)
        {
            weights[key] = GetWeight(profile, key);
        }

        return weights;
    }

    private static double GetWeight(ContextAttentionProfile profile, string key)
    {
        if (profile.Controls.TryGetValue(key, out var value))
        {
            return value;
        }

        return string.Equals(key, "oldScoreAnchorWeight", StringComparison.OrdinalIgnoreCase)
            ? 1d
            : 0d;
    }
}
