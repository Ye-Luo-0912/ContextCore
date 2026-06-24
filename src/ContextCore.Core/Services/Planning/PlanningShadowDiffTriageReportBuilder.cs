using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public static class PlanningShadowDiffTriageReportBuilder
{
    public static PlanningShadowDiffTriageReport Build(ShadowRetrievalComparisonReport comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var samples = comparison.Samples
            .Select(BuildSample)
            .ToArray();
        var causeCounts = samples
            .GroupBy(sample => sample.SuspectedCause, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new PlanningShadowDiffTriageReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            SampleSet = comparison.SampleSet,
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalSamples = samples.Length,
            DiffSampleCount = samples.Count(sample => sample.AddedByShadow.Count + sample.DroppedByShadow.Count > 0),
            MustNotHitAddedCount = samples.Sum(sample => sample.MustNotHitAdded.Count),
            MustHitDroppedCount = samples.Sum(sample => sample.MustHitDropped.Count),
            LifecycleRiskAddedCount = samples.Sum(sample => sample.LifecycleRiskAdded.Count),
            FallbackToLegacySafePlanCount = samples.Count(sample => sample.ChannelPlan.FallbackToLegacySafePlan),
            RepairedPlanCount = samples.Count(sample => sample.ChannelPlan.RepairedPlan),
            SuspectedCauseCounts = causeCounts,
            Samples = samples
        };
    }

    private static PlanningShadowDiffTriageSample BuildSample(ShadowRetrievalComparisonItem item)
    {
        var intent = ResolveIntent(item.ProposalSummary);
        var suspectedCause = ResolveSuspectedCause(item);

        return new PlanningShadowDiffTriageSample
        {
            SampleId = item.SampleId,
            Intent = intent,
            Mode = item.Mode,
            LegacySelected = item.LegacySelected,
            ShadowSelected = item.ShadowSelected,
            AddedByShadow = item.AddedItems,
            DroppedByShadow = item.DroppedItems,
            MustNotHitAdded = item.MustNotHitViolations,
            MustHitDropped = item.MustHitDropped,
            LifecycleRiskAdded = item.LifecycleRiskAdded,
            ChannelPlan = new PlanningShadowChannelPlan
            {
                UseKeyword = ReadBool(item.Diagnostics, "includeKeyword"),
                UseWorkingMemory = ReadBool(item.Diagnostics, "includeWorkingMemory"),
                UseStableMemory = ReadBool(item.Diagnostics, "includeStableMemory"),
                UseRelations = ReadBool(item.Diagnostics, "includeRelations"),
                UseVector = false,
                AuditMode = ReadBool(item.Diagnostics, "auditMode"),
                ConflictMode = ReadBool(item.Diagnostics, "conflictMode"),
                ValidatorApplied = item.ValidatorApplied,
                ValidPlan = item.ValidPlan,
                RepairedPlan = item.RepairedPlan,
                FallbackToLegacySafePlan = item.FallbackToLegacySafePlan
            },
            ChannelTopK = new Dictionary<string, int>
            {
                ["keywordTopK"] = ReadInt(item.Diagnostics, "keywordTopK"),
                ["memoryTopK"] = ReadInt(item.Diagnostics, "memoryTopK"),
                ["relationTopK"] = ReadInt(item.Diagnostics, "relationTopK"),
                ["vectorTopK"] = ReadInt(item.Diagnostics, "vectorTopK"),
                ["finalTopK"] = ReadInt(item.Diagnostics, "finalTopK"),
                ["topK"] = ReadInt(item.Diagnostics, "topK"),
                ["candidateTake"] = ReadInt(item.Diagnostics, "candidateTake")
            },
            FallbackRootCause = item.FallbackRootCause,
            RepairReasons = item.ValidatorRepairReasons,
            AfterRepairPlanSummary = item.AfterRepairPlanSummary,
            SuspectedCause = suspectedCause,
            SuggestedFix = ResolveSuggestedFix(suspectedCause, item)
        };
    }

    private static string ResolveIntent(string proposalSummary)
    {
        if (string.IsNullOrWhiteSpace(proposalSummary))
        {
            return string.Empty;
        }

        var slash = proposalSummary.IndexOf('/');
        return slash > 0 ? proposalSummary[..slash] : proposalSummary;
    }

    private static string ResolveSuspectedCause(ShadowRetrievalComparisonItem item)
    {
        if (item.MustNotHitViolations.Count > 0)
        {
            return "MustNotHitAdded";
        }

        if (item.LifecycleRiskAdded.Count > 0 || item.LifecycleViolationAfterValidation > 0)
        {
            return "LifecycleRiskAdded";
        }

        if (item.FallbackToLegacySafePlan)
        {
            return "InvalidProposalFallback";
        }

        if (item.MustHitDropped.Count > 0)
        {
            return "MustHitDroppedByShadow";
        }

        if (item.RepairedPlan && item.AddedCount + item.DroppedCount > 0)
        {
            return "RepairedPlanDiff";
        }

        if (item.RepairedPlan)
        {
            return "RepairedPlanNoDiff";
        }

        if (item.AddedCount + item.DroppedCount > 0)
        {
            return "ChannelPlanDiff";
        }

        return "NoDiff";
    }

    private static string ResolveSuggestedFix(
        string suspectedCause,
        ShadowRetrievalComparisonItem item)
    {
        return suspectedCause switch
        {
            "MustNotHitAdded" => "Keep validator must-not-hit gate and audit proposal channel flags before enabling.",
            "LifecycleRiskAdded" => "Keep lifecycle gate; allow deprecated/superseded only for audit or conflict shadow paths.",
            "InvalidProposalFallback" => item.RejectedPlanReasons.Count > 0
                ? $"Use LegacySafePlan until proposal passes validator: {string.Join("; ", item.RejectedPlanReasons)}"
                : "Use LegacySafePlan until proposal passes validator.",
            "RepairedPlanDiff" => item.ValidatorRepairReasons.Count > 0
                ? $"Review repaired plan diff before enablement: {string.Join("; ", item.ValidatorRepairReasons)}"
                : "Review repaired plan diff before enablement.",
            "RepairedPlanNoDiff" => "Repaired plan matched legacy selected set; keep observing in shadow.",
            "MustHitDroppedByShadow" => "Review channel TopK and safe caps; keep official legacy output until must-hit retention matches baseline.",
            "ChannelPlanDiff" => "Triage proposal channel flags and TopK; do not apply plan outside shadow.",
            _ => "No action required."
        };
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> diagnostics,
        string key)
    {
        return diagnostics.TryGetValue(key, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed;
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, string> diagnostics,
        string key)
    {
        return diagnostics.TryGetValue(key, out var value)
            && int.TryParse(value, out var parsed)
            ? parsed
            : 0;
    }
}
