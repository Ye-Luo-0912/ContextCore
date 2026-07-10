using System.Text;
using System.Text.RegularExpressions;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>构建 extended eval failed 样本的只读归因报告。</summary>
public static class ExtendedFailureTriageReportBuilder
{
    private static readonly Regex SelectedTraceLine = new(
        @"^\s+- \[(?<section>[^\]]+)\] (?<id>.+?) \((?<kind>[^/]+)/(?<type>[^)]+)\) \| Score: (?<score>-?[0-9.]+) \| Tokens: (?<tokens>\d+)(?<suffix>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex DroppedTraceLine = new(
        @"^\s+- (?<id>.+?) \((?<kind>[^/]+)/(?<type>[^)]+)\) \| Score: (?<score>-?[0-9.]+)(?: \| Tokens: (?<tokens>\d+))? \| Reason: (?<reason>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex TokenBudgetLine = new(
        @"Token Budget: (?<budget>\d+) \| Used: (?<used>\d+) \| Remaining: (?<remaining>\d+)",
        RegexOptions.Compiled);

    public static ExtendedFailureTriageReport Build(ContextEvalReport evalReport)
    {
        var samples = evalReport.Results
            .Where(IsFailedResult)
            .Select(BuildSample)
            .ToArray();

        return new ExtendedFailureTriageReport
        {
            OperationId = Guid.NewGuid().ToString("N"),
            TotalSamples = evalReport.TotalSamples,
            FailedSamples = samples.Length,
            CategoryCounts = samples
                .SelectMany(sample => sample.FailureCategories)
                .GroupBy(category => category, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            ModeCounts = samples
                .GroupBy(sample => sample.Mode, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            FixPlan = samples.Select(BuildFixPlanItem).ToArray(),
            Samples = samples
        };
    }

    public static string BuildMarkdownReport(ExtendedFailureTriageReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Extended Eval Failure Triage Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- Total samples: `{report.TotalSamples}`");
        sb.AppendLine($"- Failed samples: `{report.FailedSamples}`");
        sb.AppendLine();
        sb.AppendLine("### Category Counts");
        sb.AppendLine();
        sb.AppendLine("| Category | Count |");
        sb.AppendLine("|---|---:|");
        foreach (var item in report.CategoryCounts.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| {item.Key} | {item.Value} |");
        }

        sb.AppendLine();
        sb.AppendLine("### Mode Counts");
        sb.AppendLine();
        sb.AppendLine("| Mode | Failed |");
        sb.AppendLine("|---|---:|");
        foreach (var item in report.ModeCounts.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| {item.Key} | {item.Value} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Failed Sample Fix Plan");
        sb.AppendLine();
        sb.AppendLine("| Sample | Failure Type | Suspected Root Cause | Fix Type | Expected Regression Test |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var item in report.FixPlan.OrderBy(item => item.SampleId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| {item.SampleId} | {item.FailureType} | {item.SuspectedRootCause} | {item.FixType} | {item.ExpectedRegressionTest} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Failed Samples");
        sb.AppendLine();
        sb.AppendLine("| Sample | Mode | Categories | Uncertainty Failure | Selected | Budget | MustHit | Constraint | Entity | Uncertainty | Fix Type |");
        sb.AppendLine("|---|---|---|---|---:|---:|---|---|---|---|---|");
        foreach (var sample in report.Samples.OrderBy(sample => sample.Mode, StringComparer.OrdinalIgnoreCase).ThenBy(sample => sample.SampleId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"| {sample.SampleId} | {sample.Mode} | {string.Join(", ", sample.FailureCategories)} | {FormatFailureTypes(sample.UncertaintyFailureTypes)} | {sample.SelectedCount} | {sample.TokenBudget} | {FormatMustHits(sample.MustHitStatuses)} | {FormatStatus(sample.ConstraintStatus)} | {FormatStatus(sample.EntityStatus)} | {FormatStatus(sample.UncertaintyStatus)} | {sample.SuggestedFixType} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Details");
        foreach (var sample in report.Samples.OrderBy(sample => sample.SampleId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine($"### {sample.SampleId}");
            sb.AppendLine();
            sb.AppendLine($"- Mode: `{sample.Mode}`");
            sb.AppendLine($"- Failed reason: {sample.FailedReason}");
            sb.AppendLine($"- Suspected root cause: {sample.SuspectedRootCause}");
            sb.AppendLine($"- Suggested fix type: `{sample.SuggestedFixType}`");
            sb.AppendLine($"- Uncertainty failure types: `{FormatFailureTypes(sample.UncertaintyFailureTypes)}`");
            sb.AppendLine($"- Budget pressure: `{sample.BudgetPressure}`");
            sb.AppendLine($"- BudgetPressureBreakdown: mandatory={sample.BudgetPressureBreakdown.MandatoryTokens}, constraints={sample.BudgetPressureBreakdown.ConstraintsTokens}, working={sample.BudgetPressureBreakdown.WorkingTokens}, stable={sample.BudgetPressureBreakdown.StableTokens}, evidence={sample.BudgetPressureBreakdown.EvidenceTokens}, diagnostics={sample.BudgetPressureBreakdown.DiagnosticsTokens}, historical={sample.BudgetPressureBreakdown.HistoricalTokens}, droppedMustHit={sample.BudgetPressureBreakdown.DroppedMustHitTokens}, droppedLowPriority={sample.BudgetPressureBreakdown.DroppedLowPriorityTokens}");
            sb.AppendLine();
            sb.AppendLine("| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |");
            sb.AppendLine("|---|---:|---:|---:|---|---:|");
            foreach (var item in sample.MustHitStatuses)
            {
                sb.AppendLine($"| {item.ItemId} | {Bool(item.Selected)} | {Bool(item.Dropped)} | {Rank(item.SelectedRank)} | {item.DroppedReason} | {item.EstimatedTokens} |");
            }

            if (sample.TopDroppedImportantItems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |");
                sb.AppendLine("|---|---|---|---:|---:|---|");
                foreach (var item in sample.TopDroppedImportantItems)
                {
                    sb.AppendLine($"| {item.ItemId} | {item.Kind} | {item.Type} | {item.Score:F2} | {item.EstimatedTokens} | {item.Reason} |");
                }
            }
        }

        return sb.ToString();
    }

    private static ExtendedFailureTriageSample BuildSample(ContextEvalResult result)
    {
        var selectedItems = ResolveSelectedDiagnostics(result);
        var droppedItems = ResolveDroppedDiagnostics(result);
        var mustHitStatuses = result.MustHit
            .Select(id => BuildMustHitStatus(id, selectedItems, droppedItems))
            .ToArray();
        var budgetPressure = HasBudgetPressure(result, droppedItems);
        var topDroppedImportant = droppedItems
            .Where(IsImportantDroppedItem)
            .OrderByDescending(item => item.IsMustHit)
            .ThenByDescending(item => ContainsText(item.Reason, "token budget"))
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        var constraintStatus = BuildExpectationStatus(result.PackageHasAllConstraints, result.ExpectedConstraints);
        var entityStatus = BuildExpectationStatus(result.PackageHasAllEntities, result.ExpectedEntities);
        var uncertaintyStatus = BuildUncertaintyStatus(result);
        var uncertaintyFailureTypes = ParseUncertaintyFailureTypes(result.PackageBuildTrace);
        var categories = BuildCategories(
            result,
            selectedItems,
            mustHitStatuses,
            constraintStatus,
            entityStatus,
            uncertaintyStatus,
            topDroppedImportant,
            budgetPressure);

        return new ExtendedFailureTriageSample
        {
            SampleId = result.SampleId,
            Mode = result.Mode,
            FailedReason = BuildFailedReason(result, categories),
            FailureCategories = categories,
            SelectedCount = result.SelectedCount,
            TokenBudget = result.TokenBudget > 0 ? result.TokenBudget : ParseTokenBudget(result.PackageBuildTrace),
            BudgetPressure = budgetPressure,
            BudgetPressureBreakdown = result.BudgetPressureBreakdown,
            MustHitStatuses = mustHitStatuses,
            ConstraintStatus = constraintStatus,
            EntityStatus = entityStatus,
            UncertaintyStatus = uncertaintyStatus,
            UncertaintyFailureTypes = uncertaintyFailureTypes,
            TopDroppedImportantItems = topDroppedImportant,
            SuspectedRootCause = ResolveRootCause(categories),
            SuggestedFixType = ResolveSuggestedFixType(categories, result.Mode)
        };
    }

    private static ExtendedFailureFixPlanItem BuildFixPlanItem(ExtendedFailureTriageSample sample)
    {
        var failureType = sample.UncertaintyFailureTypes.FirstOrDefault()
            ?? sample.FailureCategories.FirstOrDefault()
            ?? "UnknownFailure";
        return new ExtendedFailureFixPlanItem
        {
            SampleId = sample.SampleId,
            FailureType = failureType,
            SuspectedRootCause = sample.SuspectedRootCause,
            FixType = sample.SuggestedFixType,
            ExpectedRegressionTest = ResolveExpectedRegressionTest(sample, failureType)
        };
    }

    private static IReadOnlyList<string> BuildCategories(
        ContextEvalResult result,
        IReadOnlyList<ContextEvalItemDiagnostic> selectedItems,
        IReadOnlyList<ExtendedFailureMustHitStatus> mustHitStatuses,
        ExtendedFailureExpectationStatus constraintStatus,
        ExtendedFailureExpectationStatus entityStatus,
        ExtendedFailureExpectationStatus uncertaintyStatus,
        IReadOnlyList<ContextEvalItemDiagnostic> topDroppedImportant,
        bool budgetPressure)
    {
        var categories = new List<string>();

        if (mustHitStatuses.Any(item => !item.Selected))
        {
            categories.Add("MissingMustHit");
        }

        if (!constraintStatus.Satisfied)
        {
            categories.Add("ConstraintMiss");
        }

        if (!entityStatus.Satisfied)
        {
            categories.Add("EntityMiss");
        }

        if (!uncertaintyStatus.Satisfied)
        {
            categories.Add("MissingUncertainty");
        }

        if (topDroppedImportant.Any(item => ContainsText(item.Reason, "token budget") || item.IsMustHit) ||
            result.BudgetPressureBreakdown.DroppedMustHitTokens > 0)
        {
            categories.Add("BudgetDroppedImportantItem");
        }

        if (HasWrongSection(selectedItems, mustHitStatuses))
        {
            categories.Add("WrongSection");
        }

        if (mustHitStatuses.Any(item => item.Selected && item.SelectedRank > 10))
        {
            categories.Add("MustHitSelectedButTooLow");
        }

        if (result.SelectedCount >= 20 &&
            (result.MustHitTokenShare < 0.03 ||
             mustHitStatuses.Any(item => item.SelectedRank > 10) ||
             result.RetrievalRecall10 < 0.99))
        {
            categories.Add("TooManyLowValueSelected");
        }

        if (categories.Count == 0 && budgetPressure)
        {
            categories.Add("BudgetDroppedImportantItem");
        }

        if (categories.Count == 0)
        {
            categories.Add(result.RetrievalRecall10 < 0.99 ? "MissingMustHit" : "WrongSection");
        }

        return categories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsFailedResult(ContextEvalResult result) =>
        string.Equals(result.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
        (!result.Succeeded && !string.Equals(result.Status, "InvalidSample", StringComparison.OrdinalIgnoreCase));

    private static ExtendedFailureMustHitStatus BuildMustHitStatus(
        string mustHitId,
        IReadOnlyList<ContextEvalItemDiagnostic> selectedItems,
        IReadOnlyList<ContextEvalItemDiagnostic> droppedItems)
    {
        var selected = selectedItems.FirstOrDefault(item => IsId(item.ItemId, mustHitId));
        var dropped = droppedItems.FirstOrDefault(item => IsId(item.ItemId, mustHitId));
        return new ExtendedFailureMustHitStatus
        {
            ItemId = mustHitId,
            Selected = selected is not null,
            Dropped = dropped is not null,
            SelectedRank = selected?.Rank ?? 0,
            DroppedReason = dropped?.Reason ?? string.Empty,
            EstimatedTokens = selected?.EstimatedTokens ?? dropped?.EstimatedTokens ?? 0
        };
    }

    private static ExtendedFailureExpectationStatus BuildExpectationStatus(
        bool satisfied,
        IReadOnlyList<string> expected)
    {
        return new ExtendedFailureExpectationStatus
        {
            Satisfied = satisfied,
            Expected = expected,
            Missing = satisfied ? Array.Empty<string>() : expected
        };
    }

    private static ExtendedFailureExpectationStatus BuildUncertaintyStatus(ContextEvalResult result)
    {
        if (result.PackageHasAllUncertainties)
        {
            return BuildExpectationStatus(true, result.ExpectedUncertainties);
        }

        var missing = ParseMissingUncertainties(result.PackageBuildTrace);
        if (missing.Count == 0)
        {
            missing = result.ExpectedUncertainties.ToArray();
        }

        return new ExtendedFailureExpectationStatus
        {
            Satisfied = false,
            Expected = result.ExpectedUncertainties,
            Missing = missing
        };
    }

    private static IReadOnlyList<ContextEvalItemDiagnostic> ResolveSelectedDiagnostics(ContextEvalResult result)
    {
        if (result.SelectedItemDiagnostics.Count > 0)
        {
            return result.SelectedItemDiagnostics;
        }

        var fromTrace = ParseSelectedItems(result);
        if (fromTrace.Count > 0)
        {
            return fromTrace;
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

    private static IReadOnlyList<ContextEvalItemDiagnostic> ResolveDroppedDiagnostics(ContextEvalResult result)
    {
        if (result.DroppedItemDiagnostics.Count > 0)
        {
            return result.DroppedItemDiagnostics;
        }

        var fromTrace = ParseDroppedItems(result);
        if (fromTrace.Count > 0)
        {
            return fromTrace;
        }

        return result.ExcludedIds
            .Select(id => new ContextEvalItemDiagnostic
            {
                ItemId = id,
                IsMustHit = result.MustHit.Any(mustHit => IsId(mustHit, id)),
                IsMustNotHit = result.MustNotHit.Any(mustNotHit => IsId(mustNotHit, id))
            })
            .ToArray();
    }

    private static IReadOnlyList<ContextEvalItemDiagnostic> ParseSelectedItems(ContextEvalResult result)
    {
        var items = new List<ContextEvalItemDiagnostic>();
        var rank = 0;
        foreach (var line in result.PackageBuildTrace.Split('\n'))
        {
            var match = SelectedTraceLine.Match(line.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value;
            items.Add(new ContextEvalItemDiagnostic
            {
                ItemId = id,
                Kind = match.Groups["kind"].Value,
                Type = match.Groups["type"].Value,
                SectionName = match.Groups["section"].Value,
                Score = ParseDouble(match.Groups["score"].Value),
                EstimatedTokens = ParseInt(match.Groups["tokens"].Value),
                Rank = ++rank,
                IsMustHit = result.MustHit.Any(mustHit => IsId(mustHit, id)),
                IsMustNotHit = result.MustNotHit.Any(mustNotHit => IsId(mustNotHit, id))
            });
        }

        return items;
    }

    private static IReadOnlyList<ContextEvalItemDiagnostic> ParseDroppedItems(ContextEvalResult result)
    {
        var items = new List<ContextEvalItemDiagnostic>();
        foreach (var line in result.PackageBuildTrace.Split('\n'))
        {
            var match = DroppedTraceLine.Match(line.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value;
            var reason = match.Groups["reason"].Value.Replace(" [MustHit]✓", string.Empty, StringComparison.Ordinal);
            items.Add(new ContextEvalItemDiagnostic
            {
                ItemId = id,
                Kind = match.Groups["kind"].Value,
                Type = match.Groups["type"].Value,
                Reason = reason,
                Score = ParseDouble(match.Groups["score"].Value),
                EstimatedTokens = ParseInt(match.Groups["tokens"].Value),
                IsMustHit = result.MustHit.Any(mustHit => IsId(mustHit, id)),
                IsMustNotHit = result.MustNotHit.Any(mustNotHit => IsId(mustNotHit, id))
            });
        }

        return items;
    }

    private static IReadOnlyList<string> ParseMissingUncertainties(string trace)
    {
        return trace
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- [✗]", StringComparison.Ordinal))
            .Select(line =>
            {
                var value = line["- [✗]".Length..].Trim();
                var sourceIndex = value.IndexOf(" (source=", StringComparison.OrdinalIgnoreCase);
                return sourceIndex > 0 ? value[..sourceIndex].Trim() : value;
            })
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static IReadOnlyList<string> ParseUncertaintyFailureTypes(string trace)
    {
        return trace
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- [✗]", StringComparison.Ordinal))
            .Select(line => ExtractTraceField(line, "failureType"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ExtractTraceField(string line, string fieldName)
    {
        var pattern = $"{fieldName}=";
        var start = line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += pattern.Length;
        var end = line.IndexOfAny([')', ',', ';'], start);
        return end > start
            ? line[start..end].Trim()
            : line[start..].Trim();
    }

    private static int ParseTokenBudget(string trace)
    {
        var match = TokenBudgetLine.Match(trace);
        return match.Success ? ParseInt(match.Groups["budget"].Value) : 0;
    }

    private static string BuildFailedReason(ContextEvalResult result, IReadOnlyList<string> categories)
    {
        var reasons = new List<string>();
        if (result.RetrievalRecall10 < 0.99)
        {
            reasons.Add($"Recall@10={result.RetrievalRecall10:P0}");
        }

        if (!result.PackageHasAllConstraints)
        {
            reasons.Add("constraint missing");
        }

        if (!result.PackageHasAllEntities)
        {
            reasons.Add("entity missing");
        }

        if (!result.PackageHasAllUncertainties)
        {
            reasons.Add("uncertainty missing");
        }

        if (result.MustNotHitRecalledCount > 0)
        {
            reasons.Add("must-not-hit selected");
        }

        return reasons.Count > 0
            ? string.Join("; ", reasons)
            : string.Join(", ", categories);
    }

    private static bool HasBudgetPressure(
        ContextEvalResult result,
        IReadOnlyList<ContextEvalItemDiagnostic> droppedItems)
    {
        return result.PackageBuildTrace.Contains("TokenBudgetPressure", StringComparison.OrdinalIgnoreCase) ||
               droppedItems.Any(item => ContainsText(item.Reason, "token budget")) ||
               result.BudgetPressureBreakdown.DroppedMustHitTokens > 0 ||
               result.BudgetPressureBreakdown.DroppedLowPriorityTokens > 0;
    }

    private static bool IsImportantDroppedItem(ContextEvalItemDiagnostic item) =>
        item.IsMustHit ||
        ContainsText(item.Reason, "token budget") ||
        ContainsText(item.Kind, "constraint") ||
        ContainsText(item.Type, "constraint") ||
        item.Score >= 25;

    private static bool HasWrongSection(
        IReadOnlyList<ContextEvalItemDiagnostic> selectedItems,
        IReadOnlyList<ExtendedFailureMustHitStatus> mustHitStatuses)
    {
        var mustHitIds = mustHitStatuses
            .Where(item => item.Selected)
            .Select(item => item.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return selectedItems.Any(item =>
            mustHitIds.Contains(item.ItemId) &&
            IsSection(item.SectionName, "diagnostics", "uncertainties", "excluded", "historical_context"));
    }

    private static string ResolveRootCause(IReadOnlyList<string> categories)
    {
        if (categories.Contains("ConstraintMiss", StringComparer.OrdinalIgnoreCase))
        {
            return "Expected constraint text is not represented in constraints/package sections.";
        }

        if (categories.Contains("EntityMiss", StringComparer.OrdinalIgnoreCase))
        {
            return "Expected entity is not represented in the selected package text.";
        }

        if (categories.Contains("MissingUncertainty", StringComparer.OrdinalIgnoreCase))
        {
            return "Expected uncertainty is not mapped from uncertainty, diagnostic, evidence, historical, excluded, or risk surfaces.";
        }

        if (categories.Contains("BudgetDroppedImportantItem", StringComparer.OrdinalIgnoreCase) &&
            categories.Contains("MissingMustHit", StringComparer.OrdinalIgnoreCase))
        {
            return "Important must-hit evidence was dropped or diluted under package budget pressure.";
        }

        if (categories.Contains("MustHitSelectedButTooLow", StringComparer.OrdinalIgnoreCase))
        {
            return "Must-hit evidence is selected but ranked below the top-10 package quality gate.";
        }

        if (categories.Contains("TooManyLowValueSelected", StringComparer.OrdinalIgnoreCase))
        {
            return "The package selected many lower-value items, lowering must-hit rank or token share.";
        }

        if (categories.Contains("MissingMustHit", StringComparer.OrdinalIgnoreCase))
        {
            return "Must-hit evidence is absent from selected package items.";
        }

        return "Failure needs manual inspection of selected/dropped trace.";
    }

    private static string ResolveSuggestedFixType(IReadOnlyList<string> categories, string mode)
    {
        if (categories.Contains("MissingUncertainty", StringComparer.OrdinalIgnoreCase))
        {
            return "uncertainty mapping";
        }

        if (categories.Contains("ConstraintMiss", StringComparer.OrdinalIgnoreCase) ||
            categories.Contains("EntityMiss", StringComparer.OrdinalIgnoreCase) ||
            categories.Contains("WrongSection", StringComparer.OrdinalIgnoreCase) ||
            categories.Contains("MustHitSelectedButTooLow", StringComparer.OrdinalIgnoreCase))
        {
            return "section priority";
        }

        if (categories.Contains("BudgetDroppedImportantItem", StringComparer.OrdinalIgnoreCase) ||
            categories.Contains("TooManyLowValueSelected", StringComparer.OrdinalIgnoreCase))
        {
            return "budget diagnostics";
        }

        return string.Equals(mode, "ChatMode", StringComparison.OrdinalIgnoreCase)
            ? "section priority"
            : "mode-specific package policy";
    }

    private static string ResolveExpectedRegressionTest(
        ExtendedFailureTriageSample sample,
        string failureType)
    {
        if (failureType.Contains("Uncertainty", StringComparison.OrdinalIgnoreCase) ||
            sample.FailureCategories.Contains("MissingUncertainty", StringComparer.OrdinalIgnoreCase))
        {
            return $"Extended sample {sample.SampleId} should satisfy expected uncertainty aliases.";
        }

        if (sample.FailureCategories.Contains("BudgetDroppedImportantItem", StringComparer.OrdinalIgnoreCase))
        {
            return $"Extended sample {sample.SampleId} should keep must-hit/important evidence under budget pressure.";
        }

        if (sample.FailureCategories.Contains("MustHitSelectedButTooLow", StringComparer.OrdinalIgnoreCase))
        {
            return $"Extended sample {sample.SampleId} should rank selected must-hit evidence inside top 10.";
        }

        if (sample.FailureCategories.Contains("ConstraintMiss", StringComparer.OrdinalIgnoreCase))
        {
            return $"Extended sample {sample.SampleId} should preserve expected constraint text in package output.";
        }

        if (sample.FailureCategories.Contains("EntityMiss", StringComparer.OrdinalIgnoreCase))
        {
            return $"Extended sample {sample.SampleId} should preserve expected entity text in package output.";
        }

        return $"Extended sample {sample.SampleId} should keep package quality gates stable.";
    }

    private static ExtendedFailureExpectationStatus BuildExpectationStatus(bool satisfied, IReadOnlyList<string> expected, IReadOnlyList<string> missing)
    {
        return new ExtendedFailureExpectationStatus
        {
            Satisfied = satisfied,
            Expected = expected,
            Missing = missing
        };
    }

    private static string FormatMustHits(IReadOnlyList<ExtendedFailureMustHitStatus> items)
    {
        if (items.Count == 0)
        {
            return "-";
        }

        return string.Join("<br>", items.Select(item =>
            item.Selected
                ? $"{item.ItemId}@{item.SelectedRank}"
                : $"{item.ItemId}:dropped={Bool(item.Dropped)}"));
    }

    private static string FormatStatus(ExtendedFailureExpectationStatus status)
    {
        if (status.Expected.Count == 0)
        {
            return "-";
        }

        return status.Satisfied ? "ok" : "missing";
    }

    private static string FormatFailureTypes(IReadOnlyList<string> failureTypes) =>
        failureTypes.Count == 0 ? "-" : string.Join(", ", failureTypes);

    private static string Bool(bool value) => value ? "yes" : "no";

    private static string Rank(int value) => value > 0 ? value.ToString() : "-";

    private static bool IsId(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsText(string value, string expected) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsSection(string actual, params string[] expected)
    {
        var normalized = NormalizeSection(actual);
        return expected.Any(item =>
            string.Equals(normalized, NormalizeSection(item), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSection(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim()
                .Replace("-", "_", StringComparison.Ordinal)
                .ToLowerInvariant();

    private static int ParseInt(string value) =>
        int.TryParse(value, out var parsed) ? parsed : 0;

    private static double ParseDouble(string value) =>
        double.TryParse(value, out var parsed) ? parsed : 0;
}
