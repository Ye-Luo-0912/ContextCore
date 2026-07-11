using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>对 graph expansion guarded opt-in 与 baseline package 进行离线对比。</summary>
public sealed class GraphExpansionOptInComparisonRunner
{
    public async Task<GraphExpansionOptInComparisonReport> RunAsync(
        string contextsRootPath,
        string? categoryFilter = null,
        bool includeSeedBatches = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextsRootPath);

        var baselineReport = await new ContextEvalRunner()
            .RunAsync(contextsRootPath, categoryFilter, includeSeedBatches)
            .ConfigureAwait(false);
        var applyReport = await new ContextEvalRunner(graphExpansionApplyOptions: CreateApplyOptions())
            .RunAsync(contextsRootPath, categoryFilter, includeSeedBatches)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var applyById = applyReport.Results
            .ToDictionary(result => result.SampleId, StringComparer.OrdinalIgnoreCase);
        var samples = new List<GraphExpansionOptInComparisonSample>();
        foreach (var baseline in baselineReport.Results)
        {
            if (!applyById.TryGetValue(baseline.SampleId, out var apply))
            {
                continue;
            }

            samples.Add(BuildSample(baseline, apply));
        }

        return BuildReport(
            includeSeedBatches ? "Extended" : "A3",
            baselineReport,
            applyReport,
            samples);
    }

    public static string BuildMarkdownReport(
        GraphExpansionOptInComparisonReport a3Report,
        GraphExpansionOptInComparisonReport extendedReport)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Graph Expansion Opt-in Comparison");
        builder.AppendLine();
        AppendSummary(builder, "A3", a3Report);
        builder.AppendLine();
        AppendSummary(builder, "Extended", extendedReport);
        builder.AppendLine();
        builder.AppendLine("## Sample Diffs");
        builder.AppendLine();
        builder.AppendLine("| Scope | Sample | Mode | Applied | Fallback | Added | Sections | NormalChanged | ExpectedWarn | UnexpectedWarn | Guard | RiskChecks | Kinds |");
        builder.AppendLine("|---|---|---|---:|---:|---|---|---:|---:|---:|---|---|---|");
        foreach (var report in new[] { a3Report, extendedReport })
        {
            foreach (var sample in report.Samples.Where(item =>
                item.GraphExpansionApplied
                || item.GraphExpansionFallbackUsed
                || item.NormalSelectedSetChanged
                || item.UnexpectedWarningDelta > 0
                || item.RiskChecks.HasRisk))
            {
                builder.AppendLine(
                    $"| {report.Scope} | {sample.SampleId} | {sample.Mode} | {FormatBool(sample.GraphExpansionApplied)} | {FormatBool(sample.GraphExpansionFallbackUsed)} | {string.Join(", ", sample.AddedGraphItems)} | {string.Join(", ", sample.TargetSections)} | {FormatBool(sample.NormalSelectedSetChanged)} | {sample.ExpectedWarningDelta} | {sample.UnexpectedWarningDelta} | {sample.GuardStatus} | {FormatRisk(sample.RiskChecks)} | {string.Join(", ", sample.WarningKinds)} |");
            }
        }

        return builder.ToString();
    }

    private static GraphExpansionApplyOptions CreateApplyOptions()
    {
        return new GraphExpansionApplyOptions
        {
            Mode = GraphExpansionApplyOptions.ApplyGuardedMode,
            ApplyMode = GraphExpansionApplyOptions.ProfileScopedApplyMode,
            OptInProfiles = ["audit-v1", "conflict-v1"],
            AllowedTargetSections =
            [
                GraphExpansionTargetSection.AuditContext,
                GraphExpansionTargetSection.ConflictEvidence,
                GraphExpansionTargetSection.HistoricalContext,
                GraphExpansionTargetSection.DiagnosticsOnly
            ],
            DisallowNormalContextInjection = true,
            FallbackOnRisk = true,
            MaxAddedItemsPerPackage = 20,
            EmitComparisonTrace = true
        };
    }

    private static GraphExpansionOptInComparisonSample BuildSample(
        ContextEvalResult baseline,
        ContextEvalResult apply)
    {
        var addedItems = SplitCsv(apply.PackageMetadata.GetValueOrDefault("graphExpansionAddedItems"));
        var targetSections = SplitCsv(apply.PackageMetadata.GetValueOrDefault("graphExpansionTargetSections"));
        var riskChecks = ParseRiskChecks(apply.PackageMetadata.GetValueOrDefault("graphExpansionRiskChecks"));

        return new GraphExpansionOptInComparisonSample
        {
            SampleId = baseline.SampleId,
            Mode = baseline.Mode,
            NormalSelectedSetChanged = !SetEquals(baseline.SelectedIds, apply.SelectedIds),
            AuxiliaryGraphSectionChanged = addedItems.Count > 0 || targetSections.Count > 0,
            GraphExpansionApplied = IsTrue(apply.PackageMetadata.GetValueOrDefault("graphExpansionApplied")),
            GraphExpansionMode = apply.PackageMetadata.GetValueOrDefault("graphExpansionMode") ?? string.Empty,
            GraphExpansionFallbackUsed = IsTrue(apply.PackageMetadata.GetValueOrDefault("graphExpansionFallbackUsed")),
            GraphExpansionFallbackReason = apply.PackageMetadata.GetValueOrDefault("graphExpansionFallbackReason") ?? string.Empty,
            GraphExpansionWarnings = apply.PackageMetadata.GetValueOrDefault("graphExpansionWarnings") ?? string.Empty,
            BaselineSelected = baseline.SelectedIds,
            ApplySelected = apply.SelectedIds,
            AddedGraphItems = addedItems,
            TargetSections = targetSections,
            AddedAuditContextItems = ParseInt(apply.PackageMetadata.GetValueOrDefault("graphExpansionAddedAuditContextItems")),
            AddedConflictEvidenceItems = ParseInt(apply.PackageMetadata.GetValueOrDefault("graphExpansionAddedConflictEvidenceItems")),
            RiskChecks = riskChecks,
            WarningDelta = apply.WarningReasons.Count - baseline.WarningReasons.Count
        };
    }

    private static GraphExpansionOptInComparisonReport BuildReport(
        string scope,
        ContextEvalReport baselineReport,
        ContextEvalReport applyReport,
        IReadOnlyList<GraphExpansionOptInComparisonSample> samples)
    {
        var baselinePassRate = CalculatePassRate(baselineReport.Results);
        var applyPassRate = CalculatePassRate(applyReport.Results);
        var classifiedSamples = samples
            .Select(ClassifySampleWarnings)
            .ToArray();
        var warningDeltaByKind = BuildWarningDeltaByKind(classifiedSamples);
        return new GraphExpansionOptInComparisonReport
        {
            OperationId = $"graph-expansion-optin-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Scope = scope,
            TotalSamples = classifiedSamples.Length,
            NormalSelectedSetChanged = classifiedSamples.Count(item => item.NormalSelectedSetChanged),
            AuxiliaryGraphSectionChanged = classifiedSamples.Count(item => item.AuxiliaryGraphSectionChanged),
            GraphExpansionAppliedCount = classifiedSamples.Count(item => item.GraphExpansionApplied),
            AddedAuditContextItems = classifiedSamples.Sum(item => item.AddedAuditContextItems),
            AddedConflictEvidenceItems = classifiedSamples.Sum(item => item.AddedConflictEvidenceItems),
            RiskAfterRoutingCount = classifiedSamples.Sum(item => item.RiskChecks.RiskAfterRoutingCount),
            WrongSectionRiskCount = classifiedSamples.Sum(item => item.RiskChecks.WrongSectionRiskCount),
            MustNotHitRiskCount = classifiedSamples.Sum(item => item.RiskChecks.MustNotHitRiskCount),
            LifecycleRiskCount = classifiedSamples.Sum(item => item.RiskChecks.LifecycleRiskCount),
            MissingEvidenceCount = classifiedSamples.Sum(item => item.RiskChecks.MissingEvidenceCount),
            FallbackCount = classifiedSamples.Count(item => item.GraphExpansionFallbackUsed),
            PassRateDelta = applyPassRate - baselinePassRate,
            WarningDelta = classifiedSamples.Sum(item => item.WarningDelta),
            ExpectedWarningDelta = classifiedSamples.Sum(item => item.ExpectedWarningDelta),
            UnexpectedWarningDelta = classifiedSamples.Sum(item => item.UnexpectedWarningDelta),
            WarningDeltaByKind = warningDeltaByKind,
            DisallowedNormalContextInjection = classifiedSamples.Count(item => item.DisallowedNormalContextInjection),
            GuardStatus = ResolveGuardStatus(classifiedSamples),
            Samples = classifiedSamples
        };
    }

    public static GraphExpansionOptInComparisonReport BuildReportFromSamples(
        string scope,
        IReadOnlyList<GraphExpansionOptInComparisonSample> samples,
        double passRateDelta = 0)
    {
        var classifiedSamples = samples
            .Select(ClassifySampleWarnings)
            .ToArray();
        return new GraphExpansionOptInComparisonReport
        {
            OperationId = $"graph-expansion-optin-test-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Scope = scope,
            TotalSamples = classifiedSamples.Length,
            NormalSelectedSetChanged = classifiedSamples.Count(item => item.NormalSelectedSetChanged),
            AuxiliaryGraphSectionChanged = classifiedSamples.Count(item => item.AuxiliaryGraphSectionChanged),
            GraphExpansionAppliedCount = classifiedSamples.Count(item => item.GraphExpansionApplied),
            AddedAuditContextItems = classifiedSamples.Sum(item => item.AddedAuditContextItems),
            AddedConflictEvidenceItems = classifiedSamples.Sum(item => item.AddedConflictEvidenceItems),
            RiskAfterRoutingCount = classifiedSamples.Sum(item => item.RiskChecks.RiskAfterRoutingCount),
            WrongSectionRiskCount = classifiedSamples.Sum(item => item.RiskChecks.WrongSectionRiskCount),
            MustNotHitRiskCount = classifiedSamples.Sum(item => item.RiskChecks.MustNotHitRiskCount),
            LifecycleRiskCount = classifiedSamples.Sum(item => item.RiskChecks.LifecycleRiskCount),
            MissingEvidenceCount = classifiedSamples.Sum(item => item.RiskChecks.MissingEvidenceCount),
            FallbackCount = classifiedSamples.Count(item => item.GraphExpansionFallbackUsed),
            PassRateDelta = passRateDelta,
            WarningDelta = classifiedSamples.Sum(item => item.WarningDelta),
            ExpectedWarningDelta = classifiedSamples.Sum(item => item.ExpectedWarningDelta),
            UnexpectedWarningDelta = classifiedSamples.Sum(item => item.UnexpectedWarningDelta),
            WarningDeltaByKind = BuildWarningDeltaByKind(classifiedSamples),
            DisallowedNormalContextInjection = classifiedSamples.Count(item => item.DisallowedNormalContextInjection),
            GuardStatus = ResolveGuardStatus(classifiedSamples),
            Samples = classifiedSamples
        };
    }

    public static GraphExpansionGuardedOptInGateReport BuildGateReport(
        GraphExpansionOptInComparisonReport a3Report,
        GraphExpansionOptInComparisonReport extendedReport)
    {
        var scopes = new[]
        {
            BuildGateScopeResult(a3Report),
            BuildGateScopeResult(extendedReport)
        };
        var failed = scopes
            .SelectMany(scope => scope.FailedConditions.Select(condition => $"{scope.Scope}:{condition}"))
            .ToArray();
        return new GraphExpansionGuardedOptInGateReport
        {
            OperationId = $"graph-expansion-guarded-optin-gate-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Passed = failed.Length == 0,
            Scopes = scopes,
            FailedConditions = failed
        };
    }

    private static void AppendSummary(
        StringBuilder builder,
        string title,
        GraphExpansionOptInComparisonReport report)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine("| Metric | Value |");
        builder.AppendLine("|---|---:|");
        builder.AppendLine($"| TotalSamples | {report.TotalSamples} |");
        builder.AppendLine($"| NormalSelectedSetChanged | {report.NormalSelectedSetChanged} |");
        builder.AppendLine($"| AuxiliaryGraphSectionChanged | {report.AuxiliaryGraphSectionChanged} |");
        builder.AppendLine($"| GraphExpansionAppliedCount | {report.GraphExpansionAppliedCount} |");
        builder.AppendLine($"| AddedAuditContextItems | {report.AddedAuditContextItems} |");
        builder.AppendLine($"| AddedConflictEvidenceItems | {report.AddedConflictEvidenceItems} |");
        builder.AppendLine($"| RiskAfterRoutingCount | {report.RiskAfterRoutingCount} |");
        builder.AppendLine($"| WrongSectionRiskCount | {report.WrongSectionRiskCount} |");
        builder.AppendLine($"| MustNotHitRiskCount | {report.MustNotHitRiskCount} |");
        builder.AppendLine($"| LifecycleRiskCount | {report.LifecycleRiskCount} |");
        builder.AppendLine($"| MissingEvidenceCount | {report.MissingEvidenceCount} |");
        builder.AppendLine($"| FallbackCount | {report.FallbackCount} |");
        builder.AppendLine($"| PassRateDelta | {report.PassRateDelta:0.0000} |");
        builder.AppendLine($"| WarningDelta | {report.WarningDelta} |");
        builder.AppendLine($"| ExpectedWarningDelta | {report.ExpectedWarningDelta} |");
        builder.AppendLine($"| UnexpectedWarningDelta | {report.UnexpectedWarningDelta} |");
        builder.AppendLine($"| DisallowedNormalContextInjection | {report.DisallowedNormalContextInjection} |");
        builder.AppendLine($"| GuardStatus | {report.GuardStatus} |");
        builder.AppendLine();
        builder.AppendLine("| WarningKind | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var pair in report.WarningDeltaByKind.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {pair.Key} | {pair.Value} |");
        }
    }

    public static string BuildGateMarkdownReport(GraphExpansionGuardedOptInGateReport gateReport)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Graph Expansion Guarded Opt-in Gate");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{gateReport.Passed}`");
        builder.AppendLine($"- CreatedAt: `{gateReport.CreatedAt:O}`");
        builder.AppendLine();
        builder.AppendLine("| Scope | Passed | GuardStatus | NormalChanged | RiskAfter | WrongSection | MustNotHit | Lifecycle | MissingEvidence | UnexpectedWarning | DisallowedNormal |");
        builder.AppendLine("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var scope in gateReport.Scopes)
        {
            builder.AppendLine($"| {scope.Scope} | {FormatBool(scope.Passed)} | {scope.GuardStatus} | {scope.NormalSelectedSetChanged} | {scope.RiskAfterRoutingCount} | {scope.WrongSectionRiskCount} | {scope.MustNotHitRiskCount} | {scope.LifecycleRiskCount} | {scope.MissingEvidenceCount} | {scope.UnexpectedWarningDelta} | {scope.DisallowedNormalContextInjection} |");
        }

        if (gateReport.FailedConditions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Failed Conditions");
            builder.AppendLine();
            foreach (var condition in gateReport.FailedConditions)
            {
                builder.AppendLine($"- {condition}");
            }
        }

        return builder.ToString();
    }

    private static GraphExpansionOptInComparisonSample ClassifySampleWarnings(
        GraphExpansionOptInComparisonSample sample)
    {
        var kinds = new List<string>();
        var unexpectedWarningDelta = 0;
        var expectedWarningDelta = 0;
        var disallowedNormalContext = sample.TargetSections.Contains(
            GraphExpansionTargetSection.NormalContext,
            StringComparer.OrdinalIgnoreCase);

        if (sample.AuxiliaryGraphSectionChanged)
        {
            kinds.Add(GraphExpansionComparisonWarningKind.AuxiliaryGraphSectionAdded);
        }

        if (sample.AddedAuditContextItems > 0)
        {
            kinds.Add(GraphExpansionComparisonWarningKind.ExpectedAuditContextAdded);
        }

        if (sample.AddedConflictEvidenceItems > 0)
        {
            kinds.Add(GraphExpansionComparisonWarningKind.ExpectedConflictEvidenceAdded);
        }

        if (sample.GraphExpansionWarnings.Contains("dedupe", StringComparison.OrdinalIgnoreCase)
            || sample.GraphExpansionWarnings.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            kinds.Add(GraphExpansionComparisonWarningKind.GraphContributionDeduplicated);
        }

        if (sample.NormalSelectedSetChanged)
        {
            kinds.Add(GraphExpansionComparisonWarningKind.NormalSelectedSetChanged);
            unexpectedWarningDelta++;
        }

        if (disallowedNormalContext)
        {
            kinds.Add(GraphExpansionComparisonWarningKind.DisallowedNormalContextInjection);
            unexpectedWarningDelta++;
        }

        if (sample.GraphExpansionFallbackUsed && sample.RiskChecks.HasRisk)
        {
            kinds.Add(GraphExpansionComparisonWarningKind.RiskFallbackTriggered);
            unexpectedWarningDelta++;
        }

        if (sample.RiskChecks.MissingEvidenceCount > 0)
        {
            kinds.Add(GraphExpansionComparisonWarningKind.MissingEvidenceDetected);
            unexpectedWarningDelta += sample.RiskChecks.MissingEvidenceCount;
        }

        if (sample.RiskChecks.LifecycleRiskCount > 0)
        {
            kinds.Add(GraphExpansionComparisonWarningKind.LifecycleRiskDetected);
            unexpectedWarningDelta += sample.RiskChecks.LifecycleRiskCount;
        }

        if (sample.RiskChecks.WrongSectionRiskCount > 0)
        {
            kinds.Add(GraphExpansionComparisonWarningKind.WrongSectionRiskDetected);
            unexpectedWarningDelta += sample.RiskChecks.WrongSectionRiskCount;
        }

        var positiveWarningDelta = Math.Max(0, sample.WarningDelta);
        if (positiveWarningDelta > 0)
        {
            if (IsExpectedAuxiliaryGraphDelta(sample, disallowedNormalContext))
            {
                expectedWarningDelta += positiveWarningDelta;
            }
            else
            {
                unexpectedWarningDelta += positiveWarningDelta;
                kinds.Add(GraphExpansionComparisonWarningKind.UnexpectedPackageWarningDelta);
            }
        }

        var distinctKinds = kinds
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var guardPassed = !sample.NormalSelectedSetChanged
            && !disallowedNormalContext
            && !sample.RiskChecks.HasRisk
            && unexpectedWarningDelta == 0;

        return new GraphExpansionOptInComparisonSample
        {
            SampleId = sample.SampleId,
            Mode = sample.Mode,
            NormalSelectedSetChanged = sample.NormalSelectedSetChanged,
            AuxiliaryGraphSectionChanged = sample.AuxiliaryGraphSectionChanged,
            GraphExpansionApplied = sample.GraphExpansionApplied,
            GraphExpansionMode = sample.GraphExpansionMode,
            GraphExpansionFallbackUsed = sample.GraphExpansionFallbackUsed,
            GraphExpansionFallbackReason = sample.GraphExpansionFallbackReason,
            GraphExpansionWarnings = sample.GraphExpansionWarnings,
            BaselineSelected = sample.BaselineSelected,
            ApplySelected = sample.ApplySelected,
            AddedGraphItems = sample.AddedGraphItems,
            TargetSections = sample.TargetSections,
            AddedAuditContextItems = sample.AddedAuditContextItems,
            AddedConflictEvidenceItems = sample.AddedConflictEvidenceItems,
            RiskChecks = sample.RiskChecks,
            WarningDelta = sample.WarningDelta,
            ExpectedWarningDelta = expectedWarningDelta,
            UnexpectedWarningDelta = unexpectedWarningDelta,
            WarningKinds = distinctKinds,
            DisallowedNormalContextInjection = disallowedNormalContext,
            GuardStatus = guardPassed ? GraphExpansionGuardStatus.Passed : GraphExpansionGuardStatus.Failed
        };
    }

    private static bool IsExpectedAuxiliaryGraphDelta(
        GraphExpansionOptInComparisonSample sample,
        bool disallowedNormalContext)
    {
        if (!sample.AuxiliaryGraphSectionChanged
            || sample.NormalSelectedSetChanged
            || disallowedNormalContext
            || sample.RiskChecks.HasRisk)
        {
            return false;
        }

        return sample.TargetSections.Count == 0
            || sample.TargetSections.All(IsExpectedAuxiliarySection);
    }

    private static bool IsExpectedAuxiliarySection(string section)
    {
        return string.Equals(section, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, GraphExpansionTargetSection.HistoricalContext, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, GraphExpansionTargetSection.DiagnosticsOnly, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> BuildWarningDeltaByKind(
        IReadOnlyList<GraphExpansionOptInComparisonSample> samples)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in samples.SelectMany(sample => sample.WarningKinds))
        {
            result[kind] = result.TryGetValue(kind, out var count) ? count + 1 : 1;
        }

        return result;
    }

    private static string ResolveGuardStatus(IReadOnlyList<GraphExpansionOptInComparisonSample> samples)
    {
        return samples.Any(sample => string.Equals(sample.GuardStatus, GraphExpansionGuardStatus.Failed, StringComparison.OrdinalIgnoreCase))
            ? GraphExpansionGuardStatus.Failed
            : GraphExpansionGuardStatus.Passed;
    }

    private static GraphExpansionGuardedOptInGateScopeResult BuildGateScopeResult(
        GraphExpansionOptInComparisonReport report)
    {
        var failed = new List<string>();
        AddFailure(failed, nameof(report.NormalSelectedSetChanged), report.NormalSelectedSetChanged);
        AddFailure(failed, nameof(report.RiskAfterRoutingCount), report.RiskAfterRoutingCount);
        AddFailure(failed, nameof(report.WrongSectionRiskCount), report.WrongSectionRiskCount);
        AddFailure(failed, nameof(report.MustNotHitRiskCount), report.MustNotHitRiskCount);
        AddFailure(failed, nameof(report.LifecycleRiskCount), report.LifecycleRiskCount);
        AddFailure(failed, nameof(report.MissingEvidenceCount), report.MissingEvidenceCount);
        AddFailure(failed, nameof(report.UnexpectedWarningDelta), report.UnexpectedWarningDelta);
        AddFailure(failed, nameof(report.DisallowedNormalContextInjection), report.DisallowedNormalContextInjection);

        return new GraphExpansionGuardedOptInGateScopeResult
        {
            Scope = report.Scope,
            Passed = failed.Count == 0,
            GuardStatus = failed.Count == 0 ? GraphExpansionGuardStatus.Passed : GraphExpansionGuardStatus.Failed,
            NormalSelectedSetChanged = report.NormalSelectedSetChanged,
            RiskAfterRoutingCount = report.RiskAfterRoutingCount,
            WrongSectionRiskCount = report.WrongSectionRiskCount,
            MustNotHitRiskCount = report.MustNotHitRiskCount,
            LifecycleRiskCount = report.LifecycleRiskCount,
            MissingEvidenceCount = report.MissingEvidenceCount,
            UnexpectedWarningDelta = report.UnexpectedWarningDelta,
            DisallowedNormalContextInjection = report.DisallowedNormalContextInjection,
            FailedConditions = failed.ToArray()
        };
    }

    private static void AddFailure(
        ICollection<string> failed,
        string name,
        int value)
    {
        if (value > 0)
        {
            failed.Add($"{name}={value}");
        }
    }

    private static double CalculatePassRate(IReadOnlyList<ContextEvalResult> results)
    {
        return results.Count == 0
            ? 0
            : (double)results.Count(item =>
                string.Equals(item.Status, "Passed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Status, "PassedWithWarnings", StringComparison.OrdinalIgnoreCase)) / results.Count;
    }

    private static bool SetEquals(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        return left.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(right);
    }

    private static GraphExpansionApplyRiskChecks ParseRiskChecks(string? value)
    {
        var pairs = SplitKeyValue(value);
        return new GraphExpansionApplyRiskChecks
        {
            RiskAfterRoutingCount = ParseInt(pairs.GetValueOrDefault("riskAfterRouting")),
            WrongSectionRiskCount = ParseInt(pairs.GetValueOrDefault("wrongSection")),
            MustNotHitRiskCount = ParseInt(pairs.GetValueOrDefault("mustNotHit")),
            LifecycleRiskCount = ParseInt(pairs.GetValueOrDefault("lifecycle")),
            MissingEvidenceCount = ParseInt(pairs.GetValueOrDefault("missingEvidence"))
        };
    }

    private static Dictionary<string, string> SplitKeyValue(string? value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=', StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            result[part[..index]] = part[(index + 1)..];
        }

        return result;
    }

    private static IReadOnlyList<string> SplitCsv(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static bool IsTrue(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBool(bool value) => value ? "yes" : "no";

    private static string FormatRisk(GraphExpansionApplyRiskChecks risk)
    {
        return $"after={risk.RiskAfterRoutingCount}; wrong={risk.WrongSectionRiskCount}; mustNotHit={risk.MustNotHitRiskCount}; lifecycle={risk.LifecycleRiskCount}; missingEvidence={risk.MissingEvidenceCount}";
    }
}
