using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Planning;

public static class PlanningOptInFallbackAnalysisReportBuilder
{
    public const string MustNotHitRisk = "MustNotHitRisk";
    public const string LifecycleRisk = "LifecycleRisk";
    public const string HardConstraintMissing = "HardConstraintMissing";
    public const string ConstraintRepaired = "ConstraintRepaired";
    public const string ConstraintRepairFailed = "ConstraintRepairFailed";
    public const string ConstraintDroppedByBudget = "ConstraintDroppedByBudget";
    public const string ConstraintWrongSection = "ConstraintWrongSection";
    public const string InvalidPlan = "InvalidPlan";
    public const string SelectedSetUnsafe = "SelectedSetUnsafe";
    public const string BudgetPressureRegression = "BudgetPressureRegression";
    public const string QualityRegression = "QualityRegression";
    public const string Unknown = "Unknown";

    private const double Epsilon = 0.0001;

    public static PlanningOptInFallbackAnalysisReport Build(
        ShadowRetrievalComparisonReport comparison,
        IReadOnlyList<string>? currentOptInIntents = null,
        IReadOnlyList<string>? candidateIntents = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var current = NormalizeIntentList(currentOptInIntents);
        var candidates = NormalizeIntentList(candidateIntents);
        var samples = comparison.Samples
            .Select(BuildSample)
            .ToArray();
        var summaries = BuildIntentSummaries(comparison.Samples, samples, current, candidates);
        var recommendation = BuildRecommendation(summaries, samples, current, candidates);

        return new PlanningOptInFallbackAnalysisReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            SampleSet = comparison.SampleSet,
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalSamples = samples.Length,
            CurrentOptInIntents = current,
            CandidateIntents = candidates,
            IntentSummaries = summaries,
            Recommendation = recommendation,
            FallbackReasonCounts = samples
                .GroupBy(sample => string.IsNullOrWhiteSpace(sample.FallbackReasonCategory)
                    ? Unknown
                    : sample.FallbackReasonCategory, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            Samples = samples
        };
    }

    public static PlanningOptInFallbackAnalysisSample BuildSample(
        ShadowRetrievalComparisonItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var diagnostics = item.Diagnostics;
        var optInMatched = ReadBool(diagnostics, "planningOptInMatched");
        var executionStatus = ReadString(diagnostics, "planningExecutionStatus");
        var applied = string.Equals(
            executionStatus,
            RetrievalPlanningOptions.ApplyGuardedMode,
            StringComparison.OrdinalIgnoreCase);
        var fallbackUsed = ReadBool(diagnostics, "planningFallbackUsed")
            || string.Equals(executionStatus, "FallbackUsed", StringComparison.OrdinalIgnoreCase);
        var fallbackReason = ReadString(diagnostics, "planningFallbackReason");
        var constraintRepairCategory = ClassifyConstraintRepair(diagnostics);
        var category = !string.Equals(constraintRepairCategory, Unknown, StringComparison.OrdinalIgnoreCase)
            ? constraintRepairCategory
            : fallbackUsed ? ClassifyFallbackReason(fallbackReason, diagnostics) : Unknown;
        var safetyCheckFailed = fallbackUsed
            && (ReadString(diagnostics, "planningSafetyChecks").Contains("passed=false", StringComparison.OrdinalIgnoreCase)
                || IsSafetyCategory(category));

        return new PlanningOptInFallbackAnalysisSample
        {
            SampleId = item.SampleId,
            Mode = string.IsNullOrWhiteSpace(item.Mode) ? "Unknown" : item.Mode,
            Intent = ResolveIntent(item.ProposalSummary),
            OptInMatched = optInMatched,
            Applied = applied,
            FallbackUsed = fallbackUsed,
            FallbackReason = fallbackReason,
            FallbackReasonCategory = category,
            SafetyCheckFailed = safetyCheckFailed,
            LegacySelected = item.LegacySelected,
            ProposalSelected = ReadCsv(diagnostics, "planningProposalSelected", item.ShadowSelected),
            FinalSelected = ReadCsv(diagnostics, "planningFinalSelected", item.ShadowSelected),
            MustHitDelta = item.MustHitDelta,
            ConstraintDelta = item.ConstraintDelta,
            EntityDelta = item.EntityDelta,
            UncertaintyDelta = item.UncertaintyDelta
        };
    }

    public static string ClassifyFallbackReason(
        string? fallbackReason,
        IReadOnlyDictionary<string, string>? diagnostics = null)
    {
        var reason = fallbackReason ?? string.Empty;
        var combined = diagnostics is null
            ? reason
            : reason + " " + string.Join(
                " ",
                diagnostics
                    .Where(pair => IsFallbackDiagnosticKey(pair.Key))
                    .Select(pair => $"{pair.Key}={FilterInactiveDiagnosticTerms(pair.Value)}"));

        if (ContainsAny(combined, "must_not_hit", "mustnothit", "must-not-hit"))
        {
            return MustNotHitRisk;
        }

        if (ContainsAny(combined, "hard_constraint_missing", "hardconstraintmissing", "constraint_missing"))
        {
            return HardConstraintMissing;
        }

        if (ContainsAny(combined, "lifecycle", "deprecated", "superseded", "rejected"))
        {
            return LifecycleRisk;
        }

        if (ContainsAny(combined, "invalid_proposal", "invalid plan", "invalid proposal", "fallbacktolegacysafeplan=true", "validplan=false"))
        {
            return InvalidPlan;
        }

        if (ContainsAny(combined, "selected_set", "selectedsetunsafe", "selected set unsafe"))
        {
            return SelectedSetUnsafe;
        }

        if (ContainsAny(combined, "budget", "token pressure", "budgetpressureregression"))
        {
            return BudgetPressureRegression;
        }

        if (ContainsAny(combined, "quality", "mrr", "recall", "qualityregression"))
        {
            return QualityRegression;
        }

        return Unknown;
    }

    private static string ClassifyConstraintRepair(
        IReadOnlyDictionary<string, string> diagnostics)
    {
        var repairStatus = ReadString(diagnostics, "constraintRepairStatus");
        if (repairStatus.Equals(ConstraintRepairFailed, StringComparison.OrdinalIgnoreCase))
        {
            return ConstraintRepairFailed;
        }

        if (!string.IsNullOrWhiteSpace(ReadString(diagnostics, "constraintWrongSection")))
        {
            return ConstraintWrongSection;
        }

        if (!string.IsNullOrWhiteSpace(ReadString(diagnostics, "constraintDroppedByBudget")))
        {
            return ConstraintDroppedByBudget;
        }

        if (repairStatus.Equals(ConstraintRepaired, StringComparison.OrdinalIgnoreCase))
        {
            return ConstraintRepaired;
        }

        return Unknown;
    }

    private static IReadOnlyDictionary<string, PlanningOptInIntentSummary> BuildIntentSummaries(
        IReadOnlyList<ShadowRetrievalComparisonItem> comparisonSamples,
        IReadOnlyList<PlanningOptInFallbackAnalysisSample> samples,
        IReadOnlyList<string> currentOptInIntents,
        IReadOnlyList<string> candidateIntents)
    {
        var comparisonByIntent = comparisonSamples
            .GroupBy(sample => ResolveIntent(sample.ProposalSummary), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        return samples
            .GroupBy(sample => string.IsNullOrWhiteSpace(sample.Intent) ? Unknown : sample.Intent, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var groupSamples = group.ToArray();
                    var qualitySamples = comparisonByIntent.TryGetValue(group.Key, out var matchingQualitySamples)
                        ? matchingQualitySamples
                        : Array.Empty<ShadowRetrievalComparisonItem>();
                    var matched = groupSamples.Count(sample => sample.OptInMatched);
                    var fallback = groupSamples.Count(sample => sample.FallbackUsed);
                    var applied = groupSamples.Count(sample => sample.Applied);
                    var partial = new PlanningOptInIntentSummary
                    {
                        Intent = group.Key,
                        Samples = groupSamples.Length,
                        OptInMatched = matched,
                        Applied = applied,
                        Fallback = fallback,
                        FallbackRate = matched == 0 ? 0 : (double)fallback / matched,
                        PassDelta = ResolvePassDelta(qualitySamples),
                        RecallDelta = qualitySamples.Length == 0
                            ? 0
                            : qualitySamples.Average(sample => sample.ShadowRecall10 - sample.LegacyRecall10),
                        MrrDelta = qualitySamples.Length == 0
                            ? 0
                            : qualitySamples.Average(sample => sample.ShadowMrr - sample.LegacyMrr),
                        MustNotHitViolation = qualitySamples.Sum(sample => sample.MustNotHitViolationCount),
                        LifecycleViolation = qualitySamples.Sum(sample => sample.LifecycleViolationCount)
                    };

                    return new PlanningOptInIntentSummary
                    {
                        Intent = partial.Intent,
                        Samples = partial.Samples,
                        OptInMatched = partial.OptInMatched,
                        Applied = partial.Applied,
                        Fallback = partial.Fallback,
                        FallbackRate = partial.FallbackRate,
                        PassDelta = partial.PassDelta,
                        RecallDelta = partial.RecallDelta,
                        MrrDelta = partial.MrrDelta,
                        MustNotHitViolation = partial.MustNotHitViolation,
                        LifecycleViolation = partial.LifecycleViolation,
                        Recommendation = ResolveRecommendation(
                            partial,
                            currentOptInIntents,
                            candidateIntents,
                            HasBlockingFallbackRisk(groupSamples))
                    };
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static PlanningOptInRecommendation BuildRecommendation(
        IReadOnlyDictionary<string, PlanningOptInIntentSummary> summaries,
        IReadOnlyList<PlanningOptInFallbackAnalysisSample> samples,
        IReadOnlyList<string> currentOptInIntents,
        IReadOnlyList<string> candidateIntents)
    {
        var keep = new List<string>();
        var expand = new List<string>();
        var shadowOnly = new List<string>();
        var blocked = new List<string>();
        var tuning = new List<string>();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var summary in summaries.Values.OrderBy(item => item.Intent, StringComparer.OrdinalIgnoreCase))
        {
            var intentSamples = samples
                .Where(sample => string.Equals(sample.Intent, summary.Intent, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var recommendation = ResolveRecommendation(
                summary,
                currentOptInIntents,
                candidateIntents,
                HasBlockingFallbackRisk(intentSamples));
            reasons[summary.Intent] =
                $"recommendation={recommendation}; applied={summary.Applied}; fallback={summary.Fallback}; fallbackRate={summary.FallbackRate:F3}; passDelta={summary.PassDelta:F3}; recallDelta={summary.RecallDelta:F3}; mrrDelta={summary.MrrDelta:F3}; mustNot={summary.MustNotHitViolation}; lifecycle={summary.LifecycleViolation}";

            switch (recommendation)
            {
                case "KeepOptIn":
                    keep.Add(summary.Intent);
                    break;
                case "ExpandCandidate":
                    expand.Add(summary.Intent);
                    break;
                case "Blocked":
                    blocked.Add(summary.Intent);
                    break;
                case "NeedsPolicyTuning":
                    tuning.Add(summary.Intent);
                    break;
                default:
                    shadowOnly.Add(summary.Intent);
                    break;
            }
        }

        return new PlanningOptInRecommendation
        {
            KeepOptIn = keep,
            ExpandCandidate = expand,
            ShadowOnly = shadowOnly,
            Blocked = blocked,
            NeedsPolicyTuning = tuning,
            IntentReasons = reasons
        };
    }

    private static string ResolveRecommendation(
        PlanningOptInIntentSummary summary,
        IReadOnlyList<string> currentOptInIntents,
        IReadOnlyList<string> candidateIntents,
        bool hasBlockingFallbackRisk = false)
    {
        var isCurrent = currentOptInIntents.Contains(summary.Intent, StringComparer.OrdinalIgnoreCase);
        var isCandidate = candidateIntents.Contains(summary.Intent, StringComparer.OrdinalIgnoreCase);
        if (summary.MustNotHitViolation > 0 || summary.LifecycleViolation > 0 || hasBlockingFallbackRisk)
        {
            return "Blocked";
        }

        if (summary.FallbackRate > 0.35 || summary.PassDelta < -Epsilon || summary.RecallDelta < -Epsilon || summary.MrrDelta < -0.05)
        {
            return "NeedsPolicyTuning";
        }

        if (isCurrent && summary.OptInMatched > 0)
        {
            return "KeepOptIn";
        }

        if (isCandidate && summary.OptInMatched > 0 && summary.Applied > 0)
        {
            return "ExpandCandidate";
        }

        return "ShadowOnly";
    }

    private static double ResolvePassDelta(IReadOnlyList<ShadowRetrievalComparisonItem> samples)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        return samples.Average(sample => IsPassed(sample, shadow: true) ? 1d : 0d)
            - samples.Average(sample => IsPassed(sample, shadow: false) ? 1d : 0d);
    }

    private static bool IsPassed(ShadowRetrievalComparisonItem sample, bool shadow)
    {
        return shadow
            ? sample.ShadowRecall10 >= 0.999
                && sample.MustNotHitViolationCount == 0
                && sample.LifecycleViolationCount == 0
                && sample.ShadowConstraintHitRate >= 0.999
                && sample.ShadowEntityHitRate >= 0.999
            : sample.LegacyRecall10 >= 0.999
                && sample.LegacyMustNotHitViolationCount == 0
                && sample.LegacyConstraintHitRate >= 0.999
                && sample.LegacyEntityHitRate >= 0.999;
    }

    private static bool HasBlockingFallbackRisk(IReadOnlyList<PlanningOptInFallbackAnalysisSample> samples)
    {
        return samples.Any(sample =>
            string.Equals(sample.FallbackReasonCategory, MustNotHitRisk, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sample.FallbackReasonCategory, LifecycleRisk, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sample.FallbackReasonCategory, SelectedSetUnsafe, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFallbackDiagnosticKey(string key)
    {
        return key.Equals("planningFallbackReason", StringComparison.OrdinalIgnoreCase)
            || key.Equals("planningSafetyChecks", StringComparison.OrdinalIgnoreCase)
            || key.Equals("fallbackReason", StringComparison.OrdinalIgnoreCase)
            || key.Equals("fallbackToLegacySafePlan", StringComparison.OrdinalIgnoreCase)
            || key.Equals("validatorRepairReasons", StringComparison.OrdinalIgnoreCase)
            || key.Equals("rejectedPlanReasons", StringComparison.OrdinalIgnoreCase);
    }

    private static string FilterInactiveDiagnosticTerms(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            value
                .Split([' ', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(term =>
                    !term.EndsWith("=false", StringComparison.OrdinalIgnoreCase)
                    && !term.EndsWith(":false", StringComparison.OrdinalIgnoreCase)
                    && !term.EndsWith("=0", StringComparison.OrdinalIgnoreCase)
                    && !term.EndsWith(":0", StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<string> NormalizeIntentList(IReadOnlyList<string>? values)
    {
        return values is null
            ? Array.Empty<string>()
            : values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static IReadOnlyList<string> ReadCsv(
        IReadOnlyDictionary<string, string> diagnostics,
        string key,
        IReadOnlyList<string> fallback)
    {
        if (!diagnostics.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static bool IsSafetyCategory(string category)
    {
        return category is MustNotHitRisk or LifecycleRisk or HardConstraintMissing or ConstraintRepairFailed or InvalidPlan or SelectedSetUnsafe;
    }

    private static string ResolveIntent(string proposalSummary)
    {
        if (string.IsNullOrWhiteSpace(proposalSummary))
        {
            return Unknown;
        }

        var slash = proposalSummary.IndexOf('/');
        return slash > 0 ? proposalSummary[..slash] : proposalSummary;
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
