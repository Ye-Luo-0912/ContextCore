using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Router guarded opt-in 冻结门禁；只读评估，不替换 runtime router。</summary>
public sealed class RouterGuardedOptInReadinessGateRunner
{
    public const string PolicyVersion = "router-guarded-optin-gate-r2.f/v1";
    public const string DefaultOutputDirectory = "learning/router";
    public const string ReportFileName = "router-guarded-optin-readiness-gate.json";
    public const string MarkdownReportFileName = "router-guarded-optin-readiness-gate.md";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<RouterGuardedOptInReadinessGateReport> RunAsync(
        string outputDirectory,
        RouterGuardedOptInReadinessGateOptions? options = null,
        string? shadowEvalA3Path = null,
        string? shadowEvalExtendedPath = null,
        string? triageA3Path = null,
        string? triageExtendedPath = null,
        string? p15A3Path = null,
        string? p15ExtendedPath = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedOutput = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(resolvedOutput);
        var report = await BuildFromFilesAsync(
                options ?? new RouterGuardedOptInReadinessGateOptions(),
                shadowEvalA3Path ?? Path.Combine(resolvedOutput, RouterIntentShadowReportBuilder.ShadowEvalA3FileName),
                shadowEvalExtendedPath ?? Path.Combine(resolvedOutput, RouterIntentShadowReportBuilder.ShadowEvalExtendedFileName),
                triageA3Path ?? Path.Combine(resolvedOutput, RouterDisagreementTriageRunner.A3ReportFileName),
                triageExtendedPath ?? Path.Combine(resolvedOutput, RouterDisagreementTriageRunner.ExtendedReportFileName),
                p15A3Path ?? Path.Combine(Directory.GetCurrentDirectory(), "eval", "eval-report-p15-a3.json"),
                p15ExtendedPath ?? Path.Combine(Directory.GetCurrentDirectory(), "eval", "eval-report-p15-extended.json"),
                cancellationToken)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(
                Path.Combine(resolvedOutput, ReportFileName),
                JsonSerializer.Serialize(report, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(resolvedOutput, MarkdownReportFileName),
                BuildMarkdownReport(report),
                cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    public async Task<RouterGuardedOptInReadinessGateReport> BuildFromFilesAsync(
        RouterGuardedOptInReadinessGateOptions options,
        string shadowEvalA3Path,
        string shadowEvalExtendedPath,
        string triageA3Path,
        string triageExtendedPath,
        string p15A3Path,
        string p15ExtendedPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var shadowReports = new List<RouterIntentShadowEvalReport>();
        var triageReports = new List<RouterDisagreementTriageReport>();
        var failureReasons = new List<string>();

        await AddReportAsync(shadowReports, shadowEvalA3Path, RouterGuardedOptInGateFailureReasons.MissingShadowEvalReport, failureReasons, cancellationToken)
            .ConfigureAwait(false);
        await AddReportAsync(shadowReports, shadowEvalExtendedPath, RouterGuardedOptInGateFailureReasons.MissingShadowEvalReport, failureReasons, cancellationToken)
            .ConfigureAwait(false);
        await AddReportAsync(triageReports, triageA3Path, RouterGuardedOptInGateFailureReasons.MissingTriageReport, failureReasons, cancellationToken)
            .ConfigureAwait(false);
        await AddReportAsync(triageReports, triageExtendedPath, RouterGuardedOptInGateFailureReasons.MissingTriageReport, failureReasons, cancellationToken)
            .ConfigureAwait(false);

        var effectiveShadowReports = DeduplicateByInputPath(shadowReports, static report => report.InputPath);
        var effectiveTriageReports = DeduplicateByInputPath(triageReports, static report => report.InputPath);
        var p15GatePassed = await IsP15ReportPassingAsync(p15A3Path, cancellationToken).ConfigureAwait(false)
            && await IsP15ReportPassingAsync(p15ExtendedPath, cancellationToken).ConfigureAwait(false);
        var sampleCount = effectiveShadowReports.Sum(static report => report.SampleCount);
        var fixes = effectiveTriageReports.Sum(static report => report.ShadowFixesRuntime);
        var breaks = effectiveTriageReports.Sum(static report => report.ShadowBreaksRuntime);
        var netGain = fixes - breaks;
        var perIntentRegressionCount = effectiveShadowReports.Sum(static report => report.PerIntentRegression.Values.Sum());
        var agreementRate = sampleCount == 0
            ? 0
            : effectiveShadowReports.Sum(static report => report.AgreementRate * report.SampleCount) / sampleCount;
        var lowConfidenceCount = effectiveShadowReports.Sum(static report => report.LowConfidenceCount);

        AddGateFailures(
            options,
            fixes,
            breaks,
            netGain,
            perIntentRegressionCount,
            agreementRate,
            lowConfidenceCount,
            p15GatePassed,
            failureReasons);

        var distinctFailures = failureReasons
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctFailures.Length == 0;
        return new RouterGuardedOptInReadinessGateReport
        {
            OperationId = $"router-guarded-optin-gate-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            Passed = passed,
            ShadowFixesRuntime = fixes,
            ShadowBreaksRuntime = breaks,
            NetGain = netGain,
            PerIntentRegressionCount = perIntentRegressionCount,
            AgreementRate = agreementRate,
            AgreementRateThreshold = options.AgreementRateThreshold,
            LowConfidenceCount = lowConfidenceCount,
            LowConfidenceMaxCount = options.LowConfidenceMaxCount,
            P15GatePassed = p15GatePassed,
            FailureReasons = distinctFailures,
            Recommendation = passed
                ? RouterGuardedOptInGateRecommendations.ReadyForGuardedOptIn
                : ResolveRecommendation(distinctFailures),
            ShadowEvalReportPath = $"{Path.GetFullPath(shadowEvalA3Path)};{Path.GetFullPath(shadowEvalExtendedPath)}",
            TriageReportPath = $"{Path.GetFullPath(triageA3Path)};{Path.GetFullPath(triageExtendedPath)}",
            PolicyVersion = PolicyVersion
        };
    }

    public RouterGuardedOptInReadinessGateReport BuildReport(
        IReadOnlyList<RouterIntentShadowEvalReport> shadowReports,
        IReadOnlyList<RouterDisagreementTriageReport> triageReports,
        bool p15GatePassed,
        RouterGuardedOptInReadinessGateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(shadowReports);
        ArgumentNullException.ThrowIfNull(triageReports);

        var resolvedOptions = options ?? new RouterGuardedOptInReadinessGateOptions();
        var failureReasons = new List<string>();
        var effectiveShadowReports = DeduplicateByInputPath(shadowReports, static report => report.InputPath);
        var effectiveTriageReports = DeduplicateByInputPath(triageReports, static report => report.InputPath);
        var sampleCount = effectiveShadowReports.Sum(static report => report.SampleCount);
        var fixes = effectiveTriageReports.Sum(static report => report.ShadowFixesRuntime);
        var breaks = effectiveTriageReports.Sum(static report => report.ShadowBreaksRuntime);
        var netGain = fixes - breaks;
        var perIntentRegressionCount = effectiveShadowReports.Sum(static report => report.PerIntentRegression.Values.Sum());
        var agreementRate = sampleCount == 0
            ? 0
            : effectiveShadowReports.Sum(static report => report.AgreementRate * report.SampleCount) / sampleCount;
        var lowConfidenceCount = effectiveShadowReports.Sum(static report => report.LowConfidenceCount);

        if (shadowReports.Count == 0)
        {
            failureReasons.Add(RouterGuardedOptInGateFailureReasons.MissingShadowEvalReport);
        }

        if (triageReports.Count == 0)
        {
            failureReasons.Add(RouterGuardedOptInGateFailureReasons.MissingTriageReport);
        }

        AddGateFailures(
            resolvedOptions,
            fixes,
            breaks,
            netGain,
            perIntentRegressionCount,
            agreementRate,
            lowConfidenceCount,
            p15GatePassed,
            failureReasons);
        var distinctFailures = failureReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = distinctFailures.Length == 0;
        return new RouterGuardedOptInReadinessGateReport
        {
            OperationId = $"router-guarded-optin-gate-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            Passed = passed,
            ShadowFixesRuntime = fixes,
            ShadowBreaksRuntime = breaks,
            NetGain = netGain,
            PerIntentRegressionCount = perIntentRegressionCount,
            AgreementRate = agreementRate,
            AgreementRateThreshold = resolvedOptions.AgreementRateThreshold,
            LowConfidenceCount = lowConfidenceCount,
            LowConfidenceMaxCount = resolvedOptions.LowConfidenceMaxCount,
            P15GatePassed = p15GatePassed,
            FailureReasons = distinctFailures,
            Recommendation = passed
                ? RouterGuardedOptInGateRecommendations.ReadyForGuardedOptIn
                : ResolveRecommendation(distinctFailures),
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildMarkdownReport(RouterGuardedOptInReadinessGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Router Guarded Opt-in Readiness Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine($"PolicyVersion: `{report.PolicyVersion}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- Fixes: `{report.ShadowFixesRuntime}`");
        builder.AppendLine($"- Breaks: `{report.ShadowBreaksRuntime}`");
        builder.AppendLine($"- NetGain: `{report.NetGain}`");
        builder.AppendLine($"- PerIntentRegressionCount: `{report.PerIntentRegressionCount}`");
        builder.AppendLine($"- AgreementRate: `{report.AgreementRate.ToString("P2", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- AgreementRateThreshold: `{report.AgreementRateThreshold.ToString("P2", CultureInfo.InvariantCulture)}`");
        builder.AppendLine($"- LowConfidenceCount: `{report.LowConfidenceCount}`");
        builder.AppendLine($"- LowConfidenceMaxCount: `{report.LowConfidenceMaxCount}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        builder.AppendLine();
        builder.AppendLine("## Failure Reasons");
        builder.AppendLine();
        if (report.FailureReasons.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var reason in report.FailureReasons)
            {
                builder.AppendLine($"- `{reason}`");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Runtime Safety");
        builder.AppendLine();
        builder.AppendLine("- This gate is offline/read-only.");
        builder.AppendLine("- It does not replace the runtime router.");
        builder.AppendLine("- It does not change retrieval, planning, PackingPolicy, scoring, or package output.");
        builder.AppendLine("- Reports with the same input dataset path are counted once to avoid duplicated freeze metrics.");
        return builder.ToString();
    }

    private static void AddGateFailures(
        RouterGuardedOptInReadinessGateOptions options,
        int fixes,
        int breaks,
        int netGain,
        int perIntentRegressionCount,
        double agreementRate,
        int lowConfidenceCount,
        bool p15GatePassed,
        List<string> failureReasons)
    {
        if (breaks > fixes)
        {
            failureReasons.Add(RouterGuardedOptInGateFailureReasons.ShadowBreaksRuntimeGreaterThanFixes);
        }

        if (breaks != 0)
        {
            failureReasons.Add(RouterGuardedOptInGateFailureReasons.ShadowBreaksRuntimeNonZero);
        }

        if (fixes <= 0)
        {
            failureReasons.Add(RouterGuardedOptInGateFailureReasons.ShadowFixesRuntimeNotPositive);
        }

        if (netGain <= 0)
        {
            failureReasons.Add(RouterGuardedOptInGateFailureReasons.NetGainNotPositive);
        }

        if (perIntentRegressionCount != 0)
        {
            failureReasons.Add(RouterGuardedOptInGateFailureReasons.PerIntentRegressionNonZero);
        }

        if (agreementRate + double.Epsilon < options.AgreementRateThreshold)
        {
            failureReasons.Add(RouterGuardedOptInGateFailureReasons.AgreementRateBelowThreshold);
        }

        if (lowConfidenceCount > options.LowConfidenceMaxCount)
        {
            failureReasons.Add(RouterGuardedOptInGateFailureReasons.LowConfidenceCountAboveThreshold);
        }

        if (!p15GatePassed)
        {
            failureReasons.Add(RouterGuardedOptInGateFailureReasons.P15GateNotPassing);
        }
    }

    private static string ResolveRecommendation(IReadOnlyList<string> failureReasons)
    {
        if (failureReasons.Any(reason => string.Equals(
            reason,
            RouterGuardedOptInGateFailureReasons.MissingShadowEvalReport,
            StringComparison.OrdinalIgnoreCase)))
        {
            return RouterGuardedOptInGateRecommendations.NeedsMoreRealTraces;
        }

        if (failureReasons.Any(reason => string.Equals(
            reason,
            RouterGuardedOptInGateFailureReasons.ShadowBreaksRuntimeGreaterThanFixes,
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                reason,
                RouterGuardedOptInGateFailureReasons.PerIntentRegressionNonZero,
                StringComparison.OrdinalIgnoreCase)))
        {
            return RouterGuardedOptInGateRecommendations.KeepRuleBased;
        }

        return RouterGuardedOptInGateRecommendations.NeedsIntentBoundaryRepair;
    }

    private static async Task AddReportAsync<T>(
        List<T> reports,
        string path,
        string missingReason,
        List<string> failureReasons,
        CancellationToken cancellationToken)
    {
        var report = await ReadJsonAsync<T>(path, cancellationToken).ConfigureAwait(false);
        if (report is null)
        {
            failureReasons.Add(missingReason);
            return;
        }

        reports.Add(report);
    }

    private static IReadOnlyList<T> DeduplicateByInputPath<T>(
        IEnumerable<T> reports,
        Func<T, string> getInputPath)
    {
        var result = new List<T>();
        var seenInputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var report in reports)
        {
            var inputPath = getInputPath(report);
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                result.Add(report);
                continue;
            }

            var key = NormalizeInputPath(inputPath);
            if (seenInputPaths.Add(key))
            {
                result.Add(report);
            }
        }

        return result;
    }

    private static string NormalizeInputPath(string inputPath)
    {
        try
        {
            return Path.GetFullPath(inputPath);
        }
        catch (ArgumentException)
        {
            return inputPath.Trim();
        }
        catch (NotSupportedException)
        {
            return inputPath.Trim();
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static async Task<bool> IsP15ReportPassingAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            return GetInt(root, "failedSamples", "FailedSamples") == 0
                && GetInt(root, "invalidSamples", "InvalidSamples") == 0
                && GetInt(root, "mustNotHitViolationCount", "MustNotHitViolationCount") == 0
                && GetInt(root, "lifecycleViolationCount", "LifecycleViolationCount") == 0
                && GetInt(root, "hardConstraintMissingCount", "HardConstraintMissingCount") == 0;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static int GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyCaseInsensitive(element, name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue))
            {
                return intValue;
            }
        }

        return 0;
    }

    private static bool TryGetPropertyCaseInsensitive(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
