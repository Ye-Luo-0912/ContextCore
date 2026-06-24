using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Planning;

public static class PlanningOptInConstraintSafetyReportBuilder
{
    public static PlanningOptInConstraintSafetyReport Build(
        ShadowRetrievalComparisonReport comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var samples = comparison.Samples
            .Select(BuildSample)
            .Where(sample => sample.FallbackUsed
                || sample.ConstraintRepairStatus is "ConstraintRepaired" or "ConstraintRepairFailed"
                || sample.LostAtStage is "ConstraintDroppedByBudget" or "ConstraintWrongSection")
            .ToArray();

        return new PlanningOptInConstraintSafetyReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            SampleSet = comparison.SampleSet,
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalSamples = comparison.TotalSamples,
            AffectedSampleCount = samples.Length,
            FallbackSampleCount = samples.Count(sample => sample.FallbackUsed),
            ConstraintRepairedCount = samples.Count(sample => sample.ConstraintRepairStatus == "ConstraintRepaired"),
            ConstraintRepairFailedCount = samples.Count(sample => sample.ConstraintRepairStatus == "ConstraintRepairFailed"),
            ConstraintDroppedByBudgetCount = samples.Count(sample => sample.LostAtStage == "ConstraintDroppedByBudget"),
            ConstraintWrongSectionCount = samples.Count(sample => sample.LostAtStage == "ConstraintWrongSection"),
            Samples = samples
        };
    }

    public static PlanningOptInConstraintSafetySample BuildSample(
        ShadowRetrievalComparisonItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var diagnostics = item.Diagnostics;
        var fallbackUsed = ReadBool(diagnostics, "planningFallbackUsed");
        var applied = string.Equals(
            ReadString(diagnostics, "planningExecutionStatus"),
            RetrievalPlanningOptions.ApplyGuardedMode,
            StringComparison.OrdinalIgnoreCase);
        var repairStatus = ReadString(diagnostics, "constraintRepairStatus");
        if (string.IsNullOrWhiteSpace(repairStatus))
        {
            repairStatus = fallbackUsed ? "ConstraintRepairFailed" : "NotEvaluated";
        }

        var missing = ReadCsv(diagnostics, "constraintRepairMissingAfter");
        if (missing.Count == 0)
        {
            missing = item.MissingConstraints;
        }

        var lostAtStage = ResolveLostAtStage(item, diagnostics, fallbackUsed, repairStatus);

        return new PlanningOptInConstraintSafetySample
        {
            SampleId = item.SampleId,
            Mode = item.Mode,
            Intent = ResolveIntent(item.ProposalSummary),
            OptInMatched = ReadBool(diagnostics, "planningOptInMatched"),
            Applied = applied,
            FallbackUsed = fallbackUsed,
            ExpectedHardConstraints = item.ExpectedHardConstraints.Count > 0
                ? item.ExpectedHardConstraints
                : ReadCsv(diagnostics, "expectedHardConstraints"),
            LegacyConstraints = item.LegacyConstraints,
            ProposalConstraints = item.ProposalConstraints,
            MissingConstraints = missing,
            ConstraintSource = ReadString(diagnostics, "constraintSource"),
            LostAtStage = lostAtStage,
            SuggestedFix = ResolveSuggestedFix(lostAtStage, repairStatus),
            ConstraintRepairStatus = repairStatus
        };
    }

    private static string ResolveLostAtStage(
        ShadowRetrievalComparisonItem item,
        IReadOnlyDictionary<string, string> diagnostics,
        bool fallbackUsed,
        string repairStatus)
    {
        if (ReadCsv(diagnostics, "constraintWrongSection").Count > 0)
        {
            return "ConstraintWrongSection";
        }

        if (ReadCsv(diagnostics, "constraintDroppedByBudget").Count > 0)
        {
            return "ConstraintDroppedByBudget";
        }

        if (repairStatus == "ConstraintRepaired")
        {
            return "ConstraintRepaired";
        }

        if (repairStatus == "ConstraintRepairFailed")
        {
            return item.ProposalConstraints.Count == 0
                ? "ConstraintNotRetrieved"
                : "ConstraintRepairFailed";
        }

        return fallbackUsed ? "ConstraintRepairFailed" : "NotLost";
    }

    private static string ResolveSuggestedFix(string lostAtStage, string repairStatus)
    {
        return lostAtStage switch
        {
            "ConstraintWrongSection" => "Keep mandatory constraint injection enabled and map repaired items to constraints section before safety check.",
            "ConstraintDroppedByBudget" => "Keep LockedConstraintItems before normal budget trim and evict diagnostics/historical/low-value items first.",
            "ConstraintNotRetrieved" => "Tune proposal channel coverage for scope-matched hard constraints before fallback.",
            "ConstraintRepairFailed" => "Fallback to legacy remains required until the hard constraint can be retrieved and locked.",
            "ConstraintRepaired" => "No fallback needed; keep repair-before-fallback path.",
            _ => "No constraint safety change needed."
        };
    }

    private static string ResolveIntent(string proposalSummary)
    {
        if (string.IsNullOrWhiteSpace(proposalSummary))
        {
            return "Unknown";
        }

        var slash = proposalSummary.IndexOf('/');
        return slash > 0 ? proposalSummary[..slash] : proposalSummary;
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> diagnostics,
        string key)
    {
        return diagnostics.TryGetValue(key, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed;
    }

    private static string ReadString(
        IReadOnlyDictionary<string, string> diagnostics,
        string key)
    {
        return diagnostics.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static IReadOnlyList<string> ReadCsv(
        IReadOnlyDictionary<string, string> diagnostics,
        string key)
    {
        if (!diagnostics.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
